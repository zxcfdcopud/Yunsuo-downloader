using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Engine;

/// <summary>
/// aria2 引擎：以 <c>--enable-rpc</c> 拉起 aria2c.exe，通过 JSON-RPC 派发任务。
/// 这是"下载要快"的主力：单文件最多 16 连接分片 + 磁力/BT 支持。
/// </summary>
public sealed class Aria2Engine : DownloadEngineBase
{
    private static readonly string[] StatusKeys =
    {
        "gid", "status", "totalLength", "completedLength", "downloadSpeed", "connections",
        "errorCode", "errorMessage", "dir", "files", "bittorrent", "numSeeders", "seeder"
    };

    private readonly ConcurrentDictionary<string, DownloadItem> _items = new();
    private readonly object _procLock = new();
    private Process? _process;
    private Aria2RpcClient? _client;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private AppSettings _settings = new();
    private string _secret = string.Empty;

    public Aria2Engine(ToolsLocator tools, AppSettings settings) : base(tools) => _settings = settings;

    public override EngineKind Kind => EngineKind.Aria2;

    public override bool IsAvailable => Tools.Aria2.Found;

    public bool IsRunning
    {
        get
        {
            lock (_procLock) return _process is { HasExited: false };
        }
    }

    public int Port { get; private set; }

    public override void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        if (_client is null) return;
        _ = SafeChangeGlobalOptionsAsync(settings);
    }

    public override void SetGlobalSpeedLimit(long bytesPerSecond)
    {
        if (_client is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.ChangeGlobalOptionAsync(new Dictionary<string, object?>
                {
                    ["max-overall-download-limit"] = bytesPerSecond > 0 ? bytesPerSecond.ToString() : "0"
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn($"aria2 设置全局限速失败: {ex.Message}"); }
        });
    }

    private async Task SafeChangeGlobalOptionsAsync(AppSettings s)
    {
        try
        {
            await _client!.ChangeGlobalOptionAsync(new Dictionary<string, object?>
            {
                ["max-concurrent-downloads"] = s.MaxConcurrentDownloads.ToString(),
                ["max-overall-download-limit"] = s.GlobalSpeedLimitBytesPerSec > 0 ? s.GlobalSpeedLimitBytesPerSec.ToString() : "0",
                ["split"] = s.ConnectionsPerDownload.ToString(),
                ["max-connection-per-server"] = s.ConnectionsPerDownload.ToString(),
                ["min-split-size"] = $"{ClampMinSplit(s.MinSplitSizeKb)}K"
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn($"aria2 应用设置失败: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ 启动
    public override async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) { Log.Warn("aria2c.exe 未找到，aria2 引擎不可用"); return false; }
        lock (_procLock) { if (_process is { HasExited: false }) return true; }

        Port = GetFreePort();
        _secret = SecretProtector.NewToken(16);

        var exe = Tools.Aria2.Path!;
        var args = BuildStartArguments(Port, _secret, _settings);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.BaseDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"[aria2] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn($"[aria2] {e.Data}"); };
        proc.Exited += (_, _) =>
        {
            Log.Warn($"aria2 进程退出，ExitCode={TryGetExitCode(proc)}");
            foreach (var item in _items.Values.Where(i => i.IsActive))
                RaiseFailed(item, "aria2 进程意外退出");
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 aria2 失败");
            return false;
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        lock (_procLock) _process = proc;
        _client = new Aria2RpcClient(Port, _secret);

        // 等待 RPC 就绪
        for (int i = 0; i < 40; i++)
        {
            try
            {
                await _client.GetVersionAsync(ct).ConfigureAwait(false);
                Log.Info($"aria2 RPC 就绪，端口 {Port}");
                await SafeChangeGlobalOptionsAsync(_settings).ConfigureAwait(false);
                StartPolling();
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (proc.HasExited) { Log.Error($"aria2 启动即退出，ExitCode={proc.ExitCode}"); return false; }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        Log.Error("aria2 RPC 在 10 秒内未就绪");
        return false;
    }

    private static int? TryGetExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int ClampMinSplit(int kb) => Math.Clamp(kb, 1024, 1024 * 1024);

    private static List<string> BuildStartArguments(int port, string secret, AppSettings s)
    {
        var ua = s.UserAgentOverride ?? DefaultUserAgent;
        var list = new List<string>
        {
            "--enable-rpc=true",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={port}",
            $"--rpc-secret={secret}",
            // 让 aria2 与本进程同生共死：程序被强杀/崩溃时不会留下孤儿 aria2 进程占着文件
            $"--stop-with-process={Environment.ProcessId}",
            "--rpc-max-request-size=16M",
            "--no-conf=true",
            "--continue=true",
            "--file-allocation=none",
            "--auto-file-renaming=false",
            "--allow-overwrite=false",
            $"--max-concurrent-downloads={s.MaxConcurrentDownloads}",
            $"--split={s.ConnectionsPerDownload}",
            $"--max-connection-per-server={s.ConnectionsPerDownload}",
            $"--min-split-size={ClampMinSplit(s.MinSplitSizeKb)}K",
            "--max-tries=5",
            "--retry-wait=3",
            "--timeout=30",
            "--connect-timeout=15",
            "--max-file-not-found=5",
            "--optimize-concurrent-downloads=true",
            "--check-certificate=false",
            "--http-accept-gzip=true",
            "--content-disposition-default-utf8=true",
            "--async-dns=true",
            $"--user-agent={ua}",
            "--summary-interval=0",
            "--console-log-level=warn",
            "--log-level=warn",
            "--show-console-readout=false",
            "--enable-color=false",
            "--seed-time=0",
            "--bt-max-peers=128",
            "--bt-tracker-connect-timeout=10",
            "--follow-torrent=true",
            "--follow-metalink=true",
            "--enable-dht=true",
            $"--dir={s.DownloadDirectory}"
        };

        if (s.GlobalSpeedLimitBytesPerSec > 0)
            list.Add($"--max-overall-download-limit={s.GlobalSpeedLimitBytesPerSec}");

        if (s.ProxyMode == "Manual" && !string.IsNullOrWhiteSpace(s.ProxyUrl))
            list.Add($"--all-proxy={s.ProxyUrl}");
        else if (s.ProxyMode == "System")
        {
            var sys = System.Net.WebRequest.DefaultWebProxy?.GetProxy(new Uri("https://example.com"));
            if (sys is not null) list.Add($"--all-proxy={sys}");
        }

        return list;
    }

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // ------------------------------------------------------------------ 派发任务
    public override async Task EnqueueAsync(DownloadItem item, CancellationToken ct = default)
    {
        if (!await StartAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("aria2 引擎启动失败（请检查 tools\\aria2c.exe）");

        var options = BuildTaskOptions(item, _settings);
        var urls = new List<string> { item.FinalUrl ?? item.Url };

        string gid;
        if (item.Kind == DownloadKind.Torrent && item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // 先把 .torrent 抓下来，再用 aria2.addTorrent 交给 BT 内核
            using var http = new System.Net.Http.HttpClient();
            var bytes = await http.GetByteArrayAsync(item.Url, ct).ConfigureAwait(false);
            var result = await _client!.CallAsync("aria2.addTorrent",
                new object?[] { Convert.ToBase64String(bytes), Array.Empty<string>(), options }, ct).ConfigureAwait(false);
            gid = result.GetString() ?? string.Empty;
        }
        else
        {
            gid = await _client!.AddUriAsync(urls, options, ct).ConfigureAwait(false);
        }

        item.Gid = gid;
        item.Engine = EngineKind.Aria2;
        item.Status = DownloadStatus.Connecting;
        item.StartedAt ??= DateTime.Now;
        item.StatePath = Path.Combine(AppPaths.EngineStateDirectory, item.Id + ".aria2");
        _items[gid] = item;
        Log.Info($"aria2 已接受任务 gid={gid} url={Format.Truncate(item.Url, 120)}");
    }

    private static Dictionary<string, object?> BuildTaskOptions(DownloadItem item, AppSettings s)
    {
        // 线程数：任务手动指定 > 设置固定值 > 按文件自动推荐（小文件/不分片的服务器少用线程反而更快）
        var advice = ConnectionAdvisor.Resolve(s, item, item.TotalBytes, null);
        var conns = advice.Connections;
        item.ConnectionNote = advice.Reason;

        var opt = new Dictionary<string, object?>
        {
            ["dir"] = item.Directory,
            ["continue"] = "true",
            ["auto-file-renaming"] = "false",
            ["allow-overwrite"] = "false",
            ["max-tries"] = "5",
            ["retry-wait"] = "3",
            ["timeout"] = "30",
            ["connect-timeout"] = "15",
            ["split"] = conns.ToString(),
            ["max-connection-per-server"] = conns.ToString(),
            ["min-split-size"] = $"{ClampMinSplit(s.MinSplitSizeKb)}K",
            ["check-certificate"] = "false",
            ["content-disposition-default-utf8"] = "true",
            ["user-agent"] = item.UserAgent ?? s.UserAgentOverride ?? DefaultUserAgent
        };

        if (!string.IsNullOrWhiteSpace(item.FileName))
            opt["out"] = item.FileName;

        if (!string.IsNullOrWhiteSpace(item.Referer))
            opt["referer"] = item.Referer;

        var headers = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Cookies))
            headers.Add($"Cookie: {item.Cookies}");
        foreach (var kv in item.Headers)
        {
            if (string.Equals(kv.Key, "Referer", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            headers.Add($"{kv.Key}: {kv.Value}");
        }
        if (headers.Count > 0) opt["header"] = headers.ToArray();

        if (item.Kind is DownloadKind.Magnet or DownloadKind.Torrent)
        {
            opt["seed-time"] = "0";
            opt["bt-max-peers"] = "128";
            opt["follow-torrent"] = "true";
        }

        return opt;
    }

    // ------------------------------------------------------------------ 进度轮询
    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _pollTask = Task.Run(async () =>
        {
            var meter = new ConcurrentDictionary<string, SpeedMeter>();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = _items.ToArray();
                    if (snapshot.Length > 0)
                    {
                        var calls = snapshot.Select(kv => ("aria2.tellStatus", new object?[] { kv.Key, StatusKeys })).ToList();
                        var results = await _client!.MulticallAsync(calls, token).ConfigureAwait(false);
                        for (int i = 0; i < results.Count && i < snapshot.Length; i++)
                        {
                            var gid = snapshot[i].Key;
                            var item = snapshot[i].Value;
                            if (results[i].ValueKind != JsonValueKind.Object) continue;
                            HandleStatus(gid, item, results[i], meter);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn($"aria2 轮询异常: {ex.Message}");
                }
                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void HandleStatus(string gid, DownloadItem item, JsonElement el, ConcurrentDictionary<string, SpeedMeter> meters)
    {
        var status = el.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var total = GetLong(el, "totalLength");
        var done = GetLong(el, "completedLength");
        var speed = GetLong(el, "downloadSpeed");
        var conns = (int)GetLong(el, "connections");

        switch (status)
        {
            case "active":
                item.Status = DownloadStatus.Downloading;
                break;
            case "waiting":
                if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Connecting;
                break;
            case "paused":
                item.Status = DownloadStatus.Paused;
                break;
            case "complete":
                FinishItem(gid, item, el);
                return;
            case "error":
                var code = (int)GetLong(el, "errorCode");
                var msg = el.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;
                var text = DescribeError(code, msg);
                _items.TryRemove(gid, out _);
                _ = _client!.RemoveResultAsync(gid).ContinueWith(_ => { }, TaskScheduler.Default);
                RaiseFailed(item, text);
                return;
            case "removed":
                _items.TryRemove(gid, out _);
                return;
        }

        var meter = meters.GetOrAdd(gid, _ => new SpeedMeter());
        var measured = meter.Sample(done);
        var effSpeed = speed > 0 ? speed : measured;
        var eta = effSpeed > 0 && total > done ? (total - done) / effSpeed : 0;

        // 让 UI 拿到的总大小尽早正确
        if (total > 0) item.TotalBytes = total;
        RaiseProgress(item, done, total, effSpeed, eta, conns);
    }

    private void FinishItem(string gid, DownloadItem item, JsonElement el)
    {
        _items.TryRemove(gid, out _);
        string? path = null;
        if (el.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
        {
            var f0 = files[0];
            if (f0.TryGetProperty("path", out var p)) path = p.GetString();
        }
        item.TotalBytes = Math.Max(item.TotalBytes, GetLong(el, "totalLength"));

        // aria2 的 files[0].path 在 BT 场景下是多文件里的第一个
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var candidate = Path.Combine(item.Directory, item.FileName);
            if (File.Exists(candidate)) path = candidate;
            else if (!string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath)) path = item.FullPath;
        }

        _ = _client!.RemoveResultAsync(gid).ContinueWith(_ => { }, TaskScheduler.Default);
        RaiseCompleted(item, path);
    }

    private static long GetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : 0,
            JsonValueKind.Number => v.TryGetInt64(out var l2) ? l2 : 0,
            _ => 0
        };
    }

    /// <summary>aria2 错误码 -> 中文说明。</summary>
    public static string DescribeError(int code, string? raw) => code switch
    {
        0 => raw ?? "未知错误",
        1 => "未知错误：" + raw,
        2 => "超时",
        3 => "资源不存在（404 或分享链接已失效）",
        4 => "连续 3 次遇到资源不存在，已放弃",
        5 => "下载速度过慢，已放弃",
        6 => "网络问题（请检查网络或代理）",
        7 => "未完成的下载过多",
        8 => "服务器不支持断点续传",
        9 => "磁盘空间不足",
        10 => "分片大小与控制文件不一致",
        11 => "正在下载相同的文件",
        12 => "正在下载相同的种子",
        13 => "文件已存在（可删除后重试）",
        14 => "重命名失败",
        15 => "无法打开现有文件（文件被占用？）",
        16 => "无法创建新文件或截断文件（目录不可写？）",
        17 => "文件系统出错",
        18 => "无法创建目录",
        19 => "域名解析失败（DNS）",
        20 => "无法解析 Metalink",
        21 => "FTP 命令执行失败",
        22 => "HTTP 响应头异常",
        23 => "重定向次数过多",
        24 => "HTTP 认证失败",
        25 => "无法解析 BT 种子",
        26 => "种子文件损坏",
        27 => "磁力链接格式错误",
        28 => "磁力链接缺少必要字段",
        29 => "BT 下载超时",
        30 => "无法解析 JSON-RPC 请求",
        31 => "保留字段",
        32 => "校验和不匹配",
        _ => $"aria2 错误 {code}" + (raw is null ? "" : $"：{raw}")
    };

    // ------------------------------------------------------------------ 控制
    public override async Task PauseAsync(DownloadItem item)
    {
        if (_client is null || string.IsNullOrEmpty(item.Gid)) return;
        try { await _client.PauseAsync(item.Gid).ConfigureAwait(false); item.Status = DownloadStatus.Paused; }
        catch (Exception ex) { Log.Warn($"aria2 暂停失败: {ex.Message}"); }
    }

    public override async Task ResumeAsync(DownloadItem item)
    {
        if (_client is null || string.IsNullOrEmpty(item.Gid)) return;
        try
        {
            await _client.UnpauseAsync(item.Gid).ConfigureAwait(false);
            _items[item.Gid] = item;
            item.Status = DownloadStatus.Downloading;
        }
        catch (Exception ex) { Log.Warn($"aria2 继续失败: {ex.Message}"); }
    }

    public override async Task CancelAsync(DownloadItem item, bool deleteFile)
    {
        if (_client is not null && !string.IsNullOrEmpty(item.Gid))
        {
            try
            {
                await _client.RemoveAsync(item.Gid).ConfigureAwait(false);
                await _client.RemoveResultAsync(item.Gid).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn($"aria2 取消失败: {ex.Message}"); }
            _items.TryRemove(item.Gid, out _);
        }
        if (deleteFile) TryDeleteFile(item);
    }

    internal static void TryDeleteFile(DownloadItem item)
    {
        try
        {
            var p = item.FullPath ?? Path.Combine(item.Directory, item.FileName);
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) File.Delete(p);
            var aria2 = p + ".aria2";
            if (File.Exists(aria2)) File.Delete(aria2);
        }
        catch (Exception ex) { Log.Warn($"删除文件失败: {ex.Message}"); }
    }

    public override async Task StopAsync()
    {
        try { _pollCts?.Cancel(); } catch { }
        try
        {
            if (_client is not null)
            {
                await _client.ShutdownAsync().ConfigureAwait(false);
                await Task.Delay(400).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }

        lock (_procLock)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    if (!_process.WaitForExit(3000)) _process.Kill(true);
                }
            }
            catch { /* ignore */ }
            _process?.Dispose();
            _process = null;
        }
        _client?.Dispose();
        _client = null;
        _items.Clear();
    }
}
