using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Engine;

/// <summary>yt-dlp 探测出的视频元数据。</summary>
public sealed class VideoInfo
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public double Duration { get; set; }
    public string? Thumbnail { get; set; }
    public string? Uploader { get; set; }
    public string? Extractor { get; set; }
    public string? WebpageUrl { get; set; }
    public bool IsLive { get; set; }
    public List<VideoFormat> Formats { get; set; } = new();

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? (WebpageUrl ?? "未知视频") : Title!;
}

public sealed class VideoFormat
{
    public string? FormatId { get; set; }
    public string? Ext { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }
    public double Fps { get; set; }
    public long FileSize { get; set; }
    public double TotalBitrate { get; set; }
    public string? VCodec { get; set; }
    public string? ACodec { get; set; }
    public string? Note { get; set; }

    public bool HasVideo => !string.IsNullOrEmpty(VCodec) && VCodec != "none";
    public bool HasAudio => !string.IsNullOrEmpty(ACodec) && ACodec != "none";

    public string QualityText => Height > 0 ? $"{Height}p{(Fps > 30 ? (int)Fps : 0) switch { > 0 => ((int)Fps).ToString(), _ => "" }}"
                                            : (Note ?? FormatId ?? "?");
}

/// <summary>
/// yt-dlp 引擎：负责视频页面（B站/YouTube/抖音/快手/小红书…）与 HLS/DASH（m3u8/mpd）的下载。
/// 关键提速点：<c>--concurrent-fragments</c> 让 HLS 分片并发拉取。
/// </summary>
public sealed class YtDlpEngine : DownloadEngineBase
{
    private readonly ConcurrentDictionary<string, Process> _procs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private AppSettings _settings;

    public YtDlpEngine(ToolsLocator tools, AppSettings settings) : base(tools) => _settings = settings;

    public override EngineKind Kind => EngineKind.YtDlp;

    public override bool IsAvailable => Tools.YtDlp.Found;

    public override void ApplySettings(AppSettings settings) => _settings = settings;

    // ------------------------------------------------------------------ 探测
    /// <summary>用 yt-dlp -J 获取视频元数据与清晰度列表（不下载）。</summary>
    public async Task<VideoInfo?> ProbeAsync(DownloadItem item, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        var args = new List<string> { "--no-warnings", "--no-playlist", "-J", "--no-color", "--socket-timeout", "20" };
        AppendNetworkArgs(args, item);
        args.Add(UrlNormalizer.Normalize(item.Url));

        var (exit, stdout, stderr) = await RunCaptureAsync(args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            Log.Warn($"yt-dlp 探测失败 exit={exit}: {Format.Truncate(stderr, 300)}");
            return null;
        }
        var line = stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var info = new VideoInfo
            {
                Id = Str(root, "id"),
                Title = Str(root, "title"),
                Duration = Num(root, "duration"),
                Thumbnail = Str(root, "thumbnail"),
                Uploader = Str(root, "uploader") ?? Str(root, "channel"),
                Extractor = Str(root, "extractor_key") ?? Str(root, "extractor"),
                WebpageUrl = Str(root, "webpage_url"),
                IsLive = root.TryGetProperty("is_live", out var lv) && lv.ValueKind == JsonValueKind.True
            };
            if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fmts.EnumerateArray())
                {
                    info.Formats.Add(new VideoFormat
                    {
                        FormatId = Str(f, "format_id"),
                        Ext = Str(f, "ext"),
                        Height = (int)Num(f, "height"),
                        Width = (int)Num(f, "width"),
                        Fps = Num(f, "fps"),
                        FileSize = (long)(Num(f, "filesize") is 0 ? Num(f, "filesize_approx") : Num(f, "filesize")),
                        TotalBitrate = Num(f, "tbr"),
                        VCodec = Str(f, "vcodec"),
                        ACodec = Str(f, "acodec"),
                        Note = Str(f, "format_note")
                    });
                }
            }
            return info;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 yt-dlp JSON 失败: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ 下载
    public override Task EnqueueAsync(DownloadItem item, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("yt-dlp.exe 未找到，无法解析视频/HLS 链接");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts[item.Id] = cts;

        _ = Task.Run(async () =>
        {
            try { await RunAsync(item, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Merging)
                    item.Status = DownloadStatus.Paused;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"yt-dlp 任务失败 {item.Url}");
                RaiseFailed(item, ex.Message);
            }
            finally
            {
                _cts.TryRemove(item.Id, out _);
                _procs.TryRemove(item.Id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(item.Directory);
        item.Status = DownloadStatus.Connecting;
        item.StartedAt ??= DateTime.Now;

        var args = BuildDownloadArguments(item);
        var psi = new ProcessStartInfo(Tools.YtDlp.Path!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = item.Directory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = new Process { StartInfo = psi };
        _procs[item.Id] = proc;
        var meter = new SpeedMeter();
        string? finalPath = null;
        var stderrTail = new StringBuilder();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("##FILE##", StringComparison.Ordinal))
            {
                finalPath = DecodePrintedPath(line["##FILE##".Length..].Trim());
                return;
            }
            if (line.StartsWith("##PROG##", StringComparison.Ordinal))
            {
                ParseProgress(item, line["##PROG##".Length..], meter);
                return;
            }
            if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[FixupM3u8]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
            {
                item.Status = DownloadStatus.Merging;
            }
            Log.Info($"[yt-dlp] {line}");
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            stderrTail.AppendLine(e.Data);
            if (stderrTail.Length > 4000) stderrTail.Remove(0, stderrTail.Length - 4000);
            Log.Warn($"[yt-dlp] {e.Data}");
        };
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => completed.TrySetResult(true);

        Log.Info($"启动 yt-dlp: {Format.Truncate(string.Join(' ', args), 400)}");
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        });

        await completed.Task.ConfigureAwait(false);
        try { proc.WaitForExit(5000); } catch { }

        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new IOException($"yt-dlp 退出码 {proc.ExitCode}：{SummarizeError(stderrTail.ToString())}");

        // 优先用 --print after_move:filepath 的结果；不可信时到目录里找最新文件。
        // （不能只信 --print：路径编码不一致、yt-dlp 改名、或文件已存在被跳过时都会对不上）
        var printed = finalPath;
        if (!string.IsNullOrWhiteSpace(printed) && File.Exists(printed))
        {
            finalPath = printed;
        }
        else
        {
            var discovered = FindNewestFile(item.Directory, item);
            if (!string.IsNullOrWhiteSpace(printed) && !string.Equals(printed, discovered, StringComparison.Ordinal))
                Log.Warn($"yt-dlp 报告的路径不可用（{printed}），改用目录中最新文件：{discovered ?? "(未找到)"}");
            finalPath = !string.IsNullOrWhiteSpace(discovered) ? discovered : printed;
        }

        if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
        {
            var fallback = Path.Combine(item.Directory, item.FileName);
            if (File.Exists(fallback)) finalPath = fallback;
        }

        if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
            throw new IOException("yt-dlp 报告成功，但找不到输出文件");

        Log.Info($"yt-dlp 输出文件：{finalPath}");
        item.FileName = Path.GetFileName(finalPath);
        TryDeletePartials(finalPath);
        RaiseCompleted(item, finalPath);
    }

    /// <summary>
    /// 解析 <c>--print "after_move:##FILE##%(filepath)j"</c> 的输出。
    /// JSON 转义能保证非 ASCII 路径不受控制台代码页影响；如果拿到的不是合法 JSON
    /// （例如用户换了 --print 模板），就退化成当作裸路径使用。
    /// </summary>
    private static string? DecodePrintedPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (!raw.StartsWith('"')) return raw;
        try
        {
            var decoded = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            return string.IsNullOrWhiteSpace(decoded) ? raw : decoded;
        }
        catch { return raw.Trim('"'); }
    }

    private static void TryDeletePartials(string finalPath)
    {
        try
        {
            foreach (var p in new[] { finalPath + ".part", finalPath + ".ytdl" })
                if (File.Exists(p)) File.Delete(p);
        }
        catch { /* ignore */ }
    }

    private static string? FindNewestFile(string dir, DownloadItem item)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var since = (item.StartedAt ?? DateTime.Now).AddMinutes(-10);
            var files = new DirectoryInfo(dir).GetFiles()
                .Where(f => !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl")
                            && !f.Name.EndsWith(".temp") && !f.Name.EndsWith(".fdlstate"))
                .ToList();
            if (files.Count == 0) return null;

            // 优先本次任务开始之后写过的文件；实在没有就退化成"目录里最新的文件"
            var recent = files.Where(f => f.LastWriteTime >= since).ToList();
            var pool = recent.Count > 0 ? recent : files;
            return pool.OrderByDescending(f => f.LastWriteTime).First().FullName;
        }
        catch { return null; }
    }

    private static string SummarizeError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var text = lines.Count > 0 ? string.Join(" | ", lines.TakeLast(3)) : stderr.Trim();
        return Format.Truncate(text, 400);
    }

    private void ParseProgress(DownloadItem item, string payload, SpeedMeter meter)
    {
        // downloaded|total|estimate|speed|eta|fragIndex|fragCount
        var parts = payload.Split('|');
        if (parts.Length < 4) return;
        long Get(int i) => i < parts.Length && long.TryParse(parts[i], out var v) ? v : 0;
        double GetD(int i) => i < parts.Length && double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        var downloaded = Get(0);
        var total = Get(1);
        if (total <= 0) total = Get(2);
        var speed = GetD(3);
        var eta = GetD(4);
        var fragIdx = Get(5);
        var fragCount = Get(6);

        if (item.Status is DownloadStatus.Connecting) item.Status = DownloadStatus.Downloading;

        var measured = meter.Sample(downloaded);
        if (speed <= 0) speed = measured;
        if (eta <= 0 && speed > 0 && total > downloaded) eta = (total - downloaded) / speed;

        var conns = item.Connections > 0 ? item.Connections : _settings.ConcurrentFragments;
        RaiseProgress(item, downloaded, total, speed, eta, conns);
        if (fragCount > 0 && fragIdx == fragCount)
            Log.Info($"yt-dlp 分片全部拉取完成（{fragCount} 个），正在合并…");
    }

    // ------------------------------------------------------------------ 参数拼装
    private List<string> BuildDownloadArguments(DownloadItem item)
    {
        // 并发分片数：手动指定 > 设置值（按视频时长收敛，很长/很短的视频不用开到最大）
        var frags = ConnectionAdvisor.ResolveFragments(_settings, item);
        item.Connections = frags.Connections;
        item.ConnectionNote = frags.Reason;

        var args = new List<string>
        {
            "--no-warnings",
            "--no-playlist",
            "--newline",
            "--no-color",
            "--progress",
            "--continue",
            "--windows-filenames",
            "--trim-filenames", "180",
            "--retries", "10",
            "--fragment-retries", "10",
            "--file-access-retries", "3",
            "--socket-timeout", "30",
            "--concurrent-fragments", frags.Connections.ToString(),
            "--throttled-rate", "100K",
            "--paths", item.Directory,
            "--output", BuildOutputTemplate(item),
            "--no-simulate",
            "--progress-template",
            "download:##PROG##%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s",
            // 用 (filepath)j 让 yt-dlp 输出 JSON 转义后的路径（非 ASCII 变成 \uXXXX）。
            // 直接输出裸路径会走 Windows 控制台的 ANSI 代码页（中文系统是 GBK），
            // 我们按 UTF-8 解码就会得到乱码，进而 File.Exists 判断失败。
            "--print", "after_move:##FILE##%(filepath)j",
            "--format", BuildFormatSelector(
                string.IsNullOrWhiteSpace(item.Quality) ? _settings.PreferredVideoQuality : item.Quality!)
        };

        if (Tools.Ffmpeg.Found)
        {
            args.Add("--ffmpeg-location");
            args.Add(Tools.FfmpegDirectory ?? AppPaths.BaseDirectory);
            args.Add("--merge-output-format");
            args.Add("mp4");
            if (_settings.AutoConvertHlsToMp4 && item.Kind is DownloadKind.Hls or DownloadKind.Dash)
            {
                args.Add("--remux-video");
                args.Add("mp4");
            }
        }

        AppendNetworkArgs(args, item);

        // 最后才是 URL（归一化：把 B 站 player.html?bvid=… 这类内嵌播放器地址还原成内容页）
        args.Add(UrlNormalizer.Normalize(item.FinalUrl ?? item.Url));
        return args;
    }

    private void AppendNetworkArgs(List<string> args, DownloadItem item)
    {
        var ua = item.UserAgent ?? _settings.UserAgentOverride ?? Aria2Engine.DefaultUserAgent;
        args.Add("--user-agent");
        args.Add(ua);

        if (!string.IsNullOrWhiteSpace(item.Referer))
        {
            args.Add("--add-header");
            args.Add("Referer:" + item.Referer);
        }
        if (!string.IsNullOrWhiteSpace(item.Origin))
        {
            args.Add("--add-header");
            args.Add("Origin:" + item.Origin);
        }
        if (!string.IsNullOrWhiteSpace(item.Cookies))
        {
            args.Add("--add-header");
            args.Add("Cookie:" + item.Cookies);
        }
        foreach (var kv in item.Headers)
        {
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            args.Add("--add-header");
            args.Add($"{kv.Key}:{kv.Value}");
        }

        if (_settings.ProxyMode == "Manual" && !string.IsNullOrWhiteSpace(_settings.ProxyUrl))
        {
            args.Add("--proxy");
            args.Add(_settings.ProxyUrl);
        }
        else if (_settings.ProxyMode == "System")
        {
            var sys = System.Net.WebRequest.DefaultWebProxy?.GetProxy(new Uri("https://example.com"));
            if (sys is not null) { args.Add("--proxy"); args.Add(sys.ToString()); }
        }
    }

    /// <summary>
    /// 输出命名模板：浏览器插件带了标题就用标题命名（用户看得懂），
    /// 否则用 yt-dlp 自己的 <c>%(title)s [%(id)s].%(ext)s</c>。
    /// </summary>
    private static string BuildOutputTemplate(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FileName)) return "%(title).150B [%(id)s].%(ext)s";
        var name = Path.GetFileNameWithoutExtension(Format.SafeFileName(item.FileName));
        // %(ext)s 让 yt-dlp 决定最终容器（合并后通常是 mp4）
        return name + ".%(ext)s";
    }

    /// <summary>
    /// 把清晰度偏好翻译成 yt-dlp 的 -f 表达式。
    /// 支持 "best" / "audio" / 任意数字档位（1080、720、568…），
    /// 数字档位就按"不高于这个高度"取，没有就退到最佳。
    /// </summary>
    public static string BuildFormatSelector(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference)) return "bv*+ba/b";
        if (string.Equals(preference, "audio", StringComparison.OrdinalIgnoreCase)) return "ba/b";
        if (int.TryParse(preference.Trim(), out var height) && height >= 144)
            return $"bv*[height<={height}]+ba/b[height<={height}]/bv*+ba/b";
        return "bv*+ba/b";
    }

    // ------------------------------------------------------------------ 控制
    public override Task PauseAsync(DownloadItem item)
    {
        // yt-dlp 不支持暂停；杀掉进程并保留 .part，恢复时用 --continue 续传
        KillProc(item.Id);
        item.Status = DownloadStatus.Paused;
        return Task.CompletedTask;
    }

    public override Task ResumeAsync(DownloadItem item) => EnqueueAsync(item);

    public override async Task CancelAsync(DownloadItem item, bool deleteFile)
    {
        KillProc(item.Id);
        await Task.Delay(150).ConfigureAwait(false);
        if (deleteFile)
        {
            Aria2Engine.TryDeleteFile(item);
            try
            {
                foreach (var f in Directory.GetFiles(item.Directory, Path.GetFileNameWithoutExtension(item.FileName) + "*"))
                {
                    if (f.EndsWith(".part") || f.EndsWith(".ytdl") || f.EndsWith(".temp")) File.Delete(f);
                }
            }
            catch { /* ignore */ }
        }
        item.Status = DownloadStatus.Canceled;
    }

    private void KillProc(string id)
    {
        if (_cts.TryGetValue(id, out var cts)) { try { cts.Cancel(); } catch { } }
        if (_procs.TryGetValue(id, out var p))
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }
    }

    public override async Task StopAsync()
    {
        foreach (var id in _procs.Keys.ToArray()) KillProc(id);
        await Task.Delay(300).ConfigureAwait(false);
        _procs.Clear();
        foreach (var c in _cts.Values) { try { c.Cancel(); c.Dispose(); } catch { } }
        _cts.Clear();
    }

    // ------------------------------------------------------------------ 进程工具
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Tools.YtDlp.Path!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            throw;
        }
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.GetString(), out var d) ? d : 0,
            _ => 0
        };
    }
}
