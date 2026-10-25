using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Engine;

/// <summary>
/// 自研 HTTP 多连接分片下载引擎。
///   * 作为 aria2 的兜底（用户没放 aria2c.exe 时依然能跑）
///   * 用 <see cref="RandomAccess"/> 按偏移并发写同一个文件，无需临时分片文件
///   * 分片进度写入 .fdlstate 边车文件，支持断点续传
/// </summary>
public sealed class HttpDownloadEngine : DownloadEngineBase
{
    private const int BufferSize = 128 * 1024;

    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private AppSettings _settings;

    public HttpDownloadEngine(ToolsLocator tools, AppSettings settings) : base(tools)
    {
        _settings = settings;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            // 关键：分片下载必须拿到未压缩的原始字节，否则偏移量全部错位
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public override EngineKind Kind => EngineKind.Http;

    public override bool IsAvailable => true;

    public override void ApplySettings(AppSettings settings) => _settings = settings;

    /// <summary>全局令牌桶（由 DownloadManager 注入，实现限速）。</summary>
    public TokenBucket? RateLimiter { get; set; }

    // ------------------------------------------------------------------ 状态文件
    private sealed class SegmentState
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Done { get; set; }
    }

    private sealed class ResumeState
    {
        public string Url { get; set; } = string.Empty;
        public long TotalLength { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public bool AcceptRanges { get; set; }
        public List<SegmentState> Segments { get; set; } = new();
    }

    // ------------------------------------------------------------------ 主流程
    public override Task EnqueueAsync(DownloadItem item, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var id = item.Id;
        if (!_running.TryAdd(id, cts)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(item, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting)
                    item.Status = DownloadStatus.Paused;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"HTTP 引擎任务失败 {item.Url}");
                RaiseFailed(item, ex.Message);
            }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(item.Directory);
        var url = item.FinalUrl ?? item.Url;

        var state = LoadState(item.StatePath);
        if (state is null || !string.Equals(state.Url, url, StringComparison.Ordinal))
        {
            state = await ProbeAsync(item, url, ct).ConfigureAwait(false);
            item.StatePath ??= Path.Combine(AppPaths.EngineStateDirectory, item.Id + ".fdlstate");
            SaveState(item.StatePath, state);
        }

        item.TotalBytes = state.TotalLength;
        item.FullPath = Path.Combine(item.Directory, item.FileName);
        item.Status = DownloadStatus.Downloading;
        item.StartedAt ??= DateTime.Now;

        using var handle = File.OpenHandle(item.FullPath!, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        if (state.TotalLength > 0) RandomAccess.SetLength(handle, state.TotalLength);

        // 已完成的分片直接跳过（断点续传）
        var pending = state.Segments.Where(s => s.Done < s.End - s.Start + 1).ToList();
        long alreadyDone = state.Segments.Sum(s => s.Done);

        // 断点续传时不会重新分片，这里补算一次线程数（手动指定 > 设置固定值 > 自动推荐）
        if (item.Connections <= 0 || item.ConnectionNote is null)
        {
            var advice = ConnectionAdvisor.Resolve(_settings, item, state.TotalLength, state.AcceptRanges);
            item.ConnectionNote = advice.Reason;
            item.Connections = advice.Connections;
        }

        var concurrency = Math.Max(1, Math.Min(item.Connections, pending.Count));

        using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var totalDone = Interlocked.Read(ref alreadyDone);
        var reporter = ReportLoopAsync(item, state, () => Interlocked.Read(ref totalDone), concurrency, reporterCts.Token);

        // 立刻上报一次，让界面马上就能显示出线程数与总大小
        RaiseProgress(item, totalDone, state.TotalLength, 0, 0, concurrency);

        try
        {
            using var semaphore = new SemaphoreSlim(concurrency);
            var tasks = pending.Select(seg => Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await DownloadSegmentAsync(item, url, handle, seg, n => Interlocked.Add(ref totalDone, n), ct)
                        .ConfigureAwait(false);
                }
                finally { semaphore.Release(); }
            }, ct)).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 暂停 / 出错都要保住已完成的分片，下次才能续传
            SaveState(item.StatePath, state);
            throw;
        }
        finally
        {
            reporterCts.Cancel();
            try { await reporter.ConfigureAwait(false); } catch { /* ignore */ }
        }

        var actual = RandomAccess.GetLength(handle);
        if (state.TotalLength > 0 && actual < state.TotalLength)
        {
            SaveState(item.StatePath, state);
            throw new IOException($"下载不完整：期望 {state.TotalLength} 字节，实际 {actual} 字节");
        }

        TryDelete(item.StatePath);
        RaiseCompleted(item, item.FullPath);
    }

    private async Task ReportLoopAsync(DownloadItem item, ResumeState state, Func<long> done,
                                       int connections, CancellationToken ct)
    {
        var meter = new SpeedMeter();
        var lastSave = DateTime.UtcNow;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(first ? 120 : 300, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            first = false;

            var d = done();
            var speed = meter.Sample(d);
            var total = state.TotalLength;
            var eta = speed > 0 && total > d ? (total - d) / speed : 0;
            RaiseProgress(item, d, total, speed, eta, connections);

            if ((DateTime.UtcNow - lastSave).TotalSeconds > 3)
            {
                lastSave = DateTime.UtcNow;
                SaveState(item.StatePath, state);
            }
        }
    }

    // ------------------------------------------------------------------ 探测
    private async Task<ResumeState> ProbeAsync(DownloadItem item, string url, CancellationToken ct)
    {
        using var req = BuildRequest(item, url, HttpMethod.Get);
        req.Headers.Range = new RangeHeaderValue(0, 0);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var state = new ResumeState { Url = url, AcceptRanges = resp.StatusCode == HttpStatusCode.PartialContent };
        state.ETag = resp.Headers.ETag?.Tag;
        state.LastModified = resp.Content.Headers.LastModified?.ToString("R");

        if (state.AcceptRanges)
        {
            if (resp.Content.Headers.ContentRange?.Length is { } len) state.TotalLength = len;
        }
        if (state.TotalLength <= 0) state.TotalLength = resp.Content.Headers.ContentLength ?? 0;

        // 文件名：优先用户/插件给的，其次 Content-Disposition，最后从 URL 猜
        if (string.IsNullOrWhiteSpace(item.FileName))
        {
            var cd = resp.Content.Headers.ContentDisposition?.FileNameStar
                     ?? resp.Content.Headers.ContentDisposition?.FileName;
            cd = cd?.Trim('"');
            if (!string.IsNullOrWhiteSpace(cd)) item.FileName = Format.SafeFileName(cd);
            else item.FileName = GuessFileName(url);
        }
        if (!Path.HasExtension(item.FileName) )
        {
            var guess = GuessFileName(url);
            if (Path.HasExtension(guess)) item.FileName += Path.GetExtension(guess);
        }

        BuildSegments(state, item);
        Log.Info($"HTTP 探测完成: total={state.TotalLength} ranges={state.AcceptRanges} segs={state.Segments.Count} " +
                 $"conns={item.Connections} file={item.FileName} · {item.ConnectionNote}");
        return state;
    }

    private void BuildSegments(ResumeState state, DownloadItem item)
    {
        state.Segments.Clear();

        // 线程数：任务手动指定 > 设置固定值 > 按文件大小 + 服务器是否支持分片自动推荐
        var advice = ConnectionAdvisor.Resolve(_settings, item, state.TotalLength, state.AcceptRanges);
        item.ConnectionNote = advice.Reason;

        if (state.TotalLength <= 0)
        {
            item.Connections = 1;
            state.Segments.Add(new SegmentState { Index = 0, Start = 0, End = long.MaxValue - 1, Done = 0 });
            return;
        }
        if (!state.AcceptRanges)
        {
            // 服务器不分片：只能单连接顺序下（推荐值在 Resolve 里已经收敛为 1）
            item.Connections = 1;
            state.Segments.Add(new SegmentState { Index = 0, Start = 0, End = state.TotalLength - 1, Done = 0 });
            return;
        }

        var minSplit = Math.Max(256L * 1024, _settings.MinSplitSizeKb * 1024L);
        var desired = advice.Connections;
        var segSize = Math.Max(minSplit, state.TotalLength / desired);
        var count = (int)Math.Min(desired, Math.Max(1, (state.TotalLength + segSize - 1) / segSize));
        segSize = (state.TotalLength + count - 1) / count;

        item.Connections = count;

        long start = 0;
        for (int i = 0; i < count && start < state.TotalLength; i++)
        {
            var end = Math.Min(start + segSize - 1, state.TotalLength - 1);
            state.Segments.Add(new SegmentState { Index = i, Start = start, End = end, Done = 0 });
            start = end + 1;
        }
    }

    private async Task DownloadSegmentAsync(DownloadItem item, string url, SafeFileHandle handle,
                                            SegmentState seg, Action<long> onBytes, CancellationToken ct)
    {
        var segLen = seg.End - seg.Start + 1;
        if (segLen <= 0) return;

        const int maxRetry = 5;
        for (int attempt = 1; attempt <= maxRetry; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var from = seg.Start + seg.Done;
            if (from > seg.End) return;

            try
            {
                using var req = BuildRequest(item, url, HttpMethod.Get);
                if (segLen != long.MaxValue - 1 || from > 0)
                    req.Headers.Range = new RangeHeaderValue(from, seg.End);

                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                // 服务器忽略了 Range：只能从 0 开始整文件顺序写
                if (resp.StatusCode == HttpStatusCode.OK && from > 0)
                {
                    seg.Done = 0;
                    from = 0;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
                long pos = from;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int allowed = buffer.Length;
                    if (RateLimiter is { Enabled: true })
                    {
                        var wait = RateLimiter.DelayMsFor(buffer.Length);
                        if (wait > 0) { await Task.Delay(wait, ct).ConfigureAwait(false); continue; }
                        allowed = Math.Max(1, RateLimiter.Acquire(buffer.Length));
                    }

                    int n = await stream.ReadAsync(buffer.AsMemory(0, allowed), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, n), pos, ct).ConfigureAwait(false);
                    pos += n;
                    seg.Done = pos - seg.Start;
                    onBytes(n);
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (attempt >= maxRetry)
                    throw new IOException($"分片 {seg.Index} 重试 {maxRetry} 次仍失败：{ex.Message}", ex);
                Log.Warn($"分片 {seg.Index} 第 {attempt} 次失败，{attempt * 2}s 后重试：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ 请求构造
    private static HttpRequestMessage BuildRequest(DownloadItem item, string url, HttpMethod method)
    {
        var req = new HttpRequestMessage(method, url);
        var ua = item.UserAgent ?? Aria2Engine.DefaultUserAgent;
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        if (!string.IsNullOrWhiteSpace(item.Referer)) req.Headers.TryAddWithoutValidation("Referer", item.Referer);
        if (!string.IsNullOrWhiteSpace(item.Origin)) req.Headers.TryAddWithoutValidation("Origin", item.Origin);
        if (!string.IsNullOrWhiteSpace(item.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        foreach (var kv in item.Headers)
        {
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        return req;
    }

    public static string GuessFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name)) return Format.SafeFileName(name);
        }
        catch { /* ignore */ }
        return Format.SafeFileName("download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    }

    // ------------------------------------------------------------------ 控制
    public override Task PauseAsync(DownloadItem item)
    {
        if (_running.TryGetValue(item.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        item.Status = DownloadStatus.Paused;
        return Task.CompletedTask;
    }

    public override Task ResumeAsync(DownloadItem item) => EnqueueAsync(item);

    public override async Task CancelAsync(DownloadItem item, bool deleteFile)
    {
        if (_running.TryGetValue(item.Id, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        await Task.Delay(120).ConfigureAwait(false);
        TryDelete(item.StatePath);
        if (deleteFile) Aria2Engine.TryDeleteFile(item);
        item.Status = DownloadStatus.Canceled;
    }

    public override Task StopAsync()
    {
        foreach (var kv in _running) { try { kv.Value.Cancel(); } catch { } }
        _running.Clear();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 持久化
    private static ResumeState? LoadState(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path), JsonUtil.Options); }
        catch { return null; }
    }

    private static void SaveState(string? path, ResumeState state)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // 进度写入可能很频繁，用极简序列化并容忍失败
            var cloned = new ResumeState
            {
                Url = state.Url,
                TotalLength = state.TotalLength,
                ETag = state.ETag,
                LastModified = state.LastModified,
                AcceptRanges = state.AcceptRanges,
                Segments = state.Segments.Select(s => new SegmentState
                {
                    Index = s.Index, Start = s.Start, End = s.End, Done = s.Done
                }).ToList()
            };
            JsonUtil.WriteFile(path, cloned);
        }
        catch { /* ignore */ }
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
