using System.Collections.Concurrent;
using System.Net.Http;
using YunsuoDownloader.Core.Engine;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Providers;
using YunsuoDownloader.Core.Providers.Builtin;
using YunsuoDownloader.Core.Pan;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>新增下载的请求（界面/浏览器插件/CLI 都走这里）。</summary>
public sealed class AddDownloadRequest
{
    public string Url { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? Title { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? Origin { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Directory { get; set; }
    public DownloadKind? Kind { get; set; }
    public string? Site { get; set; }
    public string? Quality { get; set; }
    public string? Thumbnail { get; set; }
    public double Duration { get; set; }
    public bool IsHls { get; set; }
    public bool Encrypted { get; set; }
    public string? ProviderName { get; set; }

    /// <summary>是否立刻开始（false 则只入队）。</summary>
    public bool StartImmediately { get; set; } = true;

    /// <summary>跳过链接解析（插件已经把 m3u8/直链给到手了）。</summary>
    public bool SkipResolve { get; set; }

    /// <summary>网盘分享的提取码（访问密码）。</summary>
    public string? Password { get; set; }

    /// <summary>本任务手动指定的线程数（0 = 按文件自动推荐）。</summary>
    public int Connections { get; set; }

    /// <summary>文件真实总长（插件用 Content-Range 回填；0 = 未知）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>编码格式（hvc1 / hev1 / avc1…），用于界面提示"HEVC 可能放不出来"。</summary>
    public string? Codec { get; set; }

    /// <summary>配套音频 URL：音视频分离的站点（抖音/快手）用它在下载完后合并。</summary>
    public string? AudioUrl { get; set; }

    /// <summary>本条是独立音轨（不是视频）。</summary>
    public bool AudioOnly { get; set; }

    /// <summary>打开后把解析的每一步都记进诊断信息。</summary>
    public bool Verbose { get; set; }
}

/// <summary>
/// 下载总调度：
///   * 链接解析（Provider）→ 选择引擎（aria2 / 自研 HTTP / yt-dlp）→ 并发排队 → 状态广播
///   * 维护历史（含时间）与全局速度
/// </summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly ToolsLocator _tools;
    private readonly LinkResolver _resolver;
    private readonly TokenBucket _bucket;
    private readonly HttpClient _providerHttp;
    private readonly RuleBasedProvider _ruleProvider = new();

    private readonly Aria2Engine _aria2;
    private readonly HttpDownloadEngine _http;
    private readonly YtDlpEngine _ytdlp;

    private readonly ConcurrentDictionary<string, DownloadItem> _active = new();
    private readonly ConcurrentQueue<DownloadItem> _queue = new();
    private readonly object _pumpLock = new();
    private bool _initialized;

    /// <summary>
    /// 站点 Cookie 缓存：插件采集 → 按域名存盘 → 直接粘贴链接时复用。
    /// 这是"不用手填 Cookie"的关键：用户在浏览器里访问过站点即可。
    /// </summary>
    public CookieJar Cookies { get; }

    public DownloadManager(SettingsService settings, HistoryService history, ToolsLocator tools)
    {
        _settings = settings;
        _history = history;
        _tools = tools;
        Cookies = new CookieJar(settings);

        _bucket = new TokenBucket(settings.Current.GlobalSpeedLimitBytesPerSec);

        _providerHttp = new HttpClient(new SocketsHttpHandler
        {
            // 刻意不启用 Brotli(All)：部分网盘返回的压缩流与自动解压不兼容，
            // 会抛 "The archive entry was compressed using an unsupported compression method"。
            // 只留 gzip/deflate；真遇到怪流还有 PanHttp 的 identity 裸请求兜底。
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        })
        { Timeout = TimeSpan.FromSeconds(60) };
        _providerHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Aria2Engine.DefaultUserAgent);

        _resolver = new LinkResolver()
            // 四家网盘统一走适配器：PanLinkProvider 内部复用 PanService 的"列目录 + 取直链"能力
            .Register(new PanLinkProvider(PanCatalog.Find("Quark")!))
            .Register(new PanLinkProvider(PanCatalog.Find("Pan123")!))
            .Register(new PanLinkProvider(PanCatalog.Find("Baidu")!))
            .Register(new ThunderProvider())
            .Register(new Ed2kProvider())
            .Register(new MagnetProvider())
            .Register(new VideoSiteProvider())
            .Register(_ruleProvider)
            .Register(new DirectLinkProvider());

        _aria2 = new Aria2Engine(tools, settings.Current);
        _http = new HttpDownloadEngine(tools, settings.Current) { RateLimiter = settings.Current.GlobalSpeedLimitBytesPerSec > 0 ? _bucket : _bucket };
        _ytdlp = new YtDlpEngine(tools, settings.Current);

        foreach (var engine in new IDownloadEngine[] { _aria2, _http, _ytdlp })
        {
            engine.Progress += OnEngineProgress;
            engine.Completed += OnEngineCompleted;
            engine.Failed += OnEngineFailed;
        }

        _bucket.SetRate(settings.Current.GlobalSpeedLimitBytesPerSec);
    }

    // ------------------------------------------------------------------ 事件
    public event EventHandler<DownloadItem>? ItemAdded;
    public event EventHandler<DownloadItem>? ItemChanged;
    public event EventHandler<DownloadItem>? ItemCompleted;
    public event EventHandler<DownloadItem>? ItemFailed;
    public event EventHandler<string>? Notice;
    public event EventHandler? StatsChanged;

    public LinkResolver Resolver => _resolver;
    public Aria2Engine Aria2 => _aria2;
    public YtDlpEngine YtDlp => _ytdlp;
    public HttpDownloadEngine Http => _http;
    public ToolsLocator Tools => _tools;

    public IReadOnlyCollection<DownloadItem> ActiveItems => _active.Values.ToArray();

    public int ActiveCount => _active.Count;
    public int QueueCount => _queue.Count;

    public double TotalSpeed => _active.Values.Sum(i => i.Speed);

    public bool Aria2Running => _aria2.IsRunning;

    // ------------------------------------------------------------------ 初始化
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        AppPaths.EnsureCreated();
        LoadRules();
        _history.Load();

        // 历史里未完成的任务保持在"已暂停"，由用户决定是否续传
        foreach (var item in _history.Items.Where(i => i.Status == DownloadStatus.Paused))
            Log.Info($"发现未完成任务：{item.DisplayName}");

        if (_tools.Aria2.Found)
        {
            var ok = await _aria2.StartAsync(ct).ConfigureAwait(false);
            Log.Info(ok ? $"aria2 引擎已就绪（端口 {_aria2.Port}）" : "aria2 引擎启动失败，将使用自研 HTTP 引擎兜底");
        }
        else
        {
            Log.Warn("未找到 tools\\aria2c.exe，将使用自研 HTTP 多连接引擎（速度快但无 BT/磁力支持）");
        }

        if (!_tools.YtDlp.Found)
            Log.Warn("未找到 tools\\yt-dlp.exe，视频网站 / m3u8 解析将不可用（请运行 scripts\\fetch-tools.ps1）");
        if (!_tools.Ffmpeg.Found)
            Log.Warn("未找到 tools\\ffmpeg.exe，HLS 合并与音视频合流将不可用");
    }

    private void LoadRules()
    {
        var path = Path.Combine(AppPaths.DataDirectory, "rules.json");
        if (!File.Exists(path))
        {
            // 首次运行给一份带注释的示例规则
            var sample = new List<RuleBasedProvider.Rule>
            {
                new()
                {
                    Name = "示例：把 /view/ 链接重写成直链",
                    Enabled = false,
                    Pattern = @"^https?://example\.com/view/(?<id>\d+)$",
                    DirectUrl = "https://cdn.example.com/file/$1.mp4",
                    Referer = "https://example.com/",
                    Kind = "video",
                    PreferYtDlp = false
                }
            };
            try { JsonUtil.WriteFile(path, sample); } catch { /* ignore */ }
            _ruleProvider.Load(sample);
            return;
        }
        try
        {
            var rules = JsonUtil.ReadFile<List<RuleBasedProvider.Rule>>(path) ?? new List<RuleBasedProvider.Rule>();
            _ruleProvider.Load(rules);
            Log.Info($"已加载 {_ruleProvider.RuleCount} 条自定义解析规则");
        }
        catch (Exception ex) { Log.Warn($"加载 rules.json 失败：{ex.Message}"); }
    }

    public void ReloadRules() => LoadRules();

    // ------------------------------------------------------------------ 设置变更
    public void ApplySettings()
    {
        var s = _settings.Current;
        _bucket.SetRate(s.GlobalSpeedLimitBytesPerSec);
        _aria2.ApplySettings(s);
        _aria2.SetGlobalSpeedLimit(s.GlobalSpeedLimitBytesPerSec);
        _http.ApplySettings(s);
        _ytdlp.ApplySettings(s);
        _tools.Invalidate();
        lock (_pumpLock) PumpQueue();
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ 解析
    public ILinkProvider? MatchProvider(string url) => _resolver.Match(url);

    public async Task<ResolveResult> ResolveAsync(string url, CancellationToken ct = default)
        => await ResolveAsync(url, null, false, ct).ConfigureAwait(false);

    /// <summary>
    /// 解析链接。
    /// <paramref name="password"/> 是网盘分享的提取码（访问密码）；
    /// <paramref name="verbose"/> 打开后每一步 HTTP 调用都会记进 <see cref="ResolveResult.Diagnostics"/>。
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(string url, string? password, bool verbose = false,
        CancellationToken ct = default)
    {
        // 插件嗅探到的是"网页里嵌的播放器地址"（例如 B 站 player.bilibili.com/player.html?bvid=…），
        // 这类地址 yt-dlp 不认，先还原成内容页再解析。
        var normalized = UrlNormalizer.Normalize(url);
        if (!string.Equals(normalized, url, StringComparison.Ordinal))
            Log.Info($"[解析] 链接已归一化：{Format.Truncate(url, 100)} → {normalized}");

        var ctx = new ResolveContext
        {
            Settings = _settings.Current,
            SettingsService = _settings,
            Http = _providerHttp,
            Report = m => Log.Info("[解析] " + m),
            SharePassword = string.IsNullOrWhiteSpace(password) ? null : password.Trim(),
            Verbose = verbose
        };
        var result = await _resolver.ResolveAsync(normalized, ctx, ct).ConfigureAwait(false);
        if (result.Diagnostics.Count == 0) result.Diagnostics = ctx.Diagnostics;
        return result;
    }

    /// <summary>解析视频页面的清晰度信息（用于界面上让用户挑清晰度）。</summary>
    public async Task<VideoInfo?> ProbeVideoAsync(string url, CancellationToken ct = default)
    {
        var item = new DownloadItem { Url = UrlNormalizer.Normalize(url) };
        return await _ytdlp.ProbeAsync(item, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 新增任务
    public async Task<DownloadItem> AddAsync(AddDownloadRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            throw new ArgumentException("下载地址不能为空", nameof(req));

        var settings = _settings.Current;
        var item = new DownloadItem
        {
            // 归一化放在最前面：后面所有引擎/命名逻辑看到的都是内容页地址，
            // 而不是插件嗅探到的 player.html?bvid=… 这类内嵌播放器地址。
            Url = UrlNormalizer.Normalize(req.Url.Trim()),
            Title = req.Title ?? string.Empty,
            FileName = Format.SafeFileName(req.FileName, string.Empty),
            Referer = req.Referer,
            UserAgent = req.UserAgent,
            Cookies = req.Cookies,
            Origin = req.Origin,
            Thumbnail = req.Thumbnail,
            Site = req.Site,
            Quality = req.Quality,
            DurationSeconds = req.Duration,
            IsHls = req.IsHls || (req.Kind == DownloadKind.Hls),
            IsEncrypted = req.Encrypted,
            Kind = req.Kind ?? DownloadKind.File,
            CreatedAt = DateTime.Now,
            Status = DownloadStatus.Queued,
            ConnectionsOverride = req.Connections > 0 ? ConnectionAdvisor.Clamp(req.Connections) : 0,
            // 插件已经从 Content-Range 拿到真实总长：直接用，省掉一次探测，
            // 也让线程推荐和进度条一开始就是对的。
            TotalBytes = req.TotalBytes > 0 ? req.TotalBytes : 0,
            Codec = req.Codec,
            AudioUrl = req.AudioUrl,
            IsAudioOnly = req.AudioOnly
        };
        foreach (var kv in req.Headers) item.Headers[kv.Key] = kv.Value;

        // 没有显式 Cookie 时，用插件采集到的站点 Cookie 补上：
        // 抖音/快手/小红书这些站点不给登录态就直接 "Fresh cookies are needed"，
        // 而用户已经在浏览器里登录过了，没必要再让他手填一次。
        if (string.IsNullOrWhiteSpace(item.Cookies))
        {
            var cached = Cookies.GetForUrl(item.Url);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                item.Cookies = cached;
                var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var u) ? u.Host : item.Url;
                Log.Info($"[Cookie] 复用浏览器采集的站点登录态（{host}）");
            }
        }

        // 1) 解析链接
        if (!req.SkipResolve)
        {
            item.Status = DownloadStatus.Resolving;
            var result = await ResolveAsync(item.Url, req.Password, req.Verbose, ct).ConfigureAwait(false);
            item.Diagnostics = result.Diagnostics.Count > 100 ? result.Diagnostics.TakeLast(100).ToList() : result.Diagnostics;
            if (result.Success)
            {
                item.ProviderName = result.ProviderName;
                item.FinalUrl = result.Url;
                if (result.Kind != DownloadKind.File || item.Kind == DownloadKind.File) item.Kind = result.Kind;
                // 调用方（插件 / 界面）明确给了名字和标题就用它的，不要被 URL 猜出来的名字盖掉
                if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(result.FileName))
                    item.FileName = Format.SafeFileName(result.FileName);
                if (string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(result.Title))
                    item.Title = result.Title!;
                if (!string.IsNullOrWhiteSpace(result.Referer)) item.Referer = result.Referer;
                if (!string.IsNullOrWhiteSpace(result.UserAgent)) item.UserAgent = result.UserAgent;
                if (!string.IsNullOrWhiteSpace(result.Cookies)) item.Cookies = result.Cookies;
                if (!string.IsNullOrWhiteSpace(result.Site)) item.Site = result.Site;
                if (result.DurationSeconds > 0) item.DurationSeconds = result.DurationSeconds;
                if (result.Thumbnail is not null) item.Thumbnail = result.Thumbnail;
                if (result.TotalBytes > 0) item.TotalBytes = result.TotalBytes;
                foreach (var kv in result.Headers) item.Headers[kv.Key] = kv.Value;
                if (result.Message is not null) Log.Info($"[解析] {result.Message}");
            }
            else
            {
                // 网盘分享需要提取码：不要降级成直链下载（那样只会下一个 HTML 页面）
                if (result.NeedPassword)
                {
                    item.NeedPassword = true;
                    item.PasswordProvider = result.PasswordProvider;
                    item.ShareId = result.ShareId;
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = result.ErrorMessage;
                    _history.Add(item);
                    ItemAdded?.Invoke(this, item);
                    ItemFailed?.Invoke(this, item);
                    Log.Info($"[解析] 需要提取码：{item.Url}");
                    return item;
                }

                // 解析失败但本身是普通直链时，降级为直接下载
                var isPanShare = MatchProvider(item.Url) is { Priority: > 20 } p &&
                                 p.Name is "Baidu" or "Quark" or "Aliyun" or "Pan123";
                if (!isPanShare && item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"解析失败，降级为直链下载：{result.ErrorMessage}");
                    item.FinalUrl = item.Url;
                    item.ProviderName = "Direct(降级)";
                }
                else
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = result.ErrorMessage;
                    _history.Add(item);
                    ItemAdded?.Invoke(this, item);
                    ItemFailed?.Invoke(this, item);
                    return item;
                }
            }
        }
        else
        {
            item.FinalUrl = item.Url;
            item.ProviderName = req.ProviderName ?? "BrowserExtension";
        }

        // 2) 命名与目录
        var isVideo = item.IsHls || item.Kind is DownloadKind.Hls or DownloadKind.Dash or DownloadKind.Video;
        if (isVideo)
        {
            // 视频任务的最终产物永远是 mp4：插件/网页给的名字常常是页面名（…html）或没有扩展名，
            // 之前会生成「xxx_bilibili.html」这种文件名，看着像下错东西。
            var baseName = !string.IsNullOrWhiteSpace(item.Title)
                ? item.Title
                : Path.GetFileNameWithoutExtension(item.FileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = Path.GetFileNameWithoutExtension(HttpDownloadEngine.GuessFileName(item.FinalUrl ?? item.Url));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";

            item.FileName = Format.SafeFileName(baseName, "video") + ".mp4";
        }
        else if (string.IsNullOrWhiteSpace(item.FileName))
        {
            item.FileName = HttpDownloadEngine.GuessFileName(item.FinalUrl ?? item.Url);
        }
        else if (!Path.HasExtension(item.FileName))
        {
            // 调用方给了不带扩展名的名字（浏览器插件常见）：从 URL 补上扩展名
            var guess = HttpDownloadEngine.GuessFileName(item.FinalUrl ?? item.Url);
            var ext = Path.GetExtension(guess);
            if (!string.IsNullOrWhiteSpace(ext)) item.FileName += ext;
        }

        // 保存目录兜底：这里必须挡住"看起来像链接/不是绝对路径"的目录。
        // 实测踩过：目录被填成了分享链接（https://www.123684.com/s/…），
        // aria2 于是去 mkdir 一个叫 "https://…" 的文件夹，报
        // "Failed to make the directory …, cause: Invalid argument"，
        // 用户只能看到一个"无法创建目录"的失败任务，完全猜不到是目录填错了。
        item.Directory = !string.IsNullOrWhiteSpace(req.Directory) ? req.Directory! : settings.DownloadDirectory;
        if (!PathGuard.IsUsableDirectory(item.Directory))
        {
            // 优先退回"设置里的下载目录"（通常是好的），实在不行才退系统默认
            var fallback = PathGuard.IsUsableDirectory(settings.DownloadDirectory)
                ? settings.DownloadDirectory
                : Models.AppSettings.DefaultDownloadDirectory;
            Log.Warn($"保存目录不可用（{item.Directory}），已改用：{fallback}");
            item.Directory = fallback;
        }

        if (settings.CreateSubfolderPerTask && !string.IsNullOrWhiteSpace(item.Title) && item.Kind is DownloadKind.Video or DownloadKind.Hls)
        {
            try { item.Directory = Path.Combine(item.Directory, Format.SafeFileName(item.Title, "video")); }
            catch { /* ignore */ }
        }
        try { Directory.CreateDirectory(item.Directory); } catch (Exception ex) { Log.Warn($"创建目录失败：{ex.Message}"); }

        // 3) 选引擎
        item.Engine = PickEngine(item, settings);

        // 4) 入历史 + 排队
        _history.Add(item);
        ItemAdded?.Invoke(this, item);

        if (req.StartImmediately && settings.AutoStartDownload)
        {
            _queue.Enqueue(item);
            lock (_pumpLock) PumpQueue();
        }
        else
        {
            item.Status = DownloadStatus.Queued;
        }

        StatsChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    private EngineKind PickEngine(DownloadItem item, AppSettings s)
    {
        var finalUrl = item.FinalUrl ?? item.Url;
        bool isHlsLike = item.Kind is DownloadKind.Hls or DownloadKind.Dash || item.IsHls
                         || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                         || finalUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

        // m3u8/mpd 只有 yt-dlp(+ffmpeg) 能处理，aria2 不支持 HLS
        if (isHlsLike)
        {
            if (_tools.YtDlp.Found) return EngineKind.YtDlp;
            Log.Warn("没有 yt-dlp，无法下载 m3u8/mpd 流");
            return EngineKind.None;
        }

        if (item.Kind is DownloadKind.Video && s.UseYtDlpForVideoPages && _tools.YtDlp.Found
            && !finalUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            return EngineKind.YtDlp;

        if (s.UseAria2ForDirectLinks && _tools.Aria2.Found) return EngineKind.Aria2;

        return EngineKind.Http;
    }

    // ------------------------------------------------------------------ 队列调度
    private void PumpQueue()
    {
        var max = Math.Max(1, _settings.Current.MaxConcurrentDownloads);
        while (_active.Count < max && _queue.TryDequeue(out var item))
        {
            if (item.Status is DownloadStatus.Completed or DownloadStatus.Canceled) continue;
            if (_active.TryAdd(item.Id, item))
            {
                _ = StartItemAsync(item);
            }
        }
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StartItemAsync(DownloadItem item)
    {
        try
        {
            item.ErrorMessage = null;
            item.Status = DownloadStatus.Connecting;
            ItemChanged?.Invoke(this, item);

            var engine = item.Engine switch
            {
                EngineKind.Aria2 => (IDownloadEngine)_aria2,
                EngineKind.YtDlp => _ytdlp,
                EngineKind.Http => _http,
                _ => null
            };

            if (engine is null)
            {
                // 重新挑一次引擎（例如用户中途补上了 yt-dlp.exe）
                item.Engine = PickEngine(item, _settings.Current);
                engine = item.Engine switch
                {
                    EngineKind.Aria2 => _aria2,
                    EngineKind.YtDlp => _ytdlp,
                    EngineKind.Http => _http,
                    _ => null
                };
            }

            if (engine is null)
                throw new InvalidOperationException(
                    "没有可用的下载引擎。请运行 scripts\\fetch-tools.ps1 下载 aria2c.exe / yt-dlp.exe 后重试。");

            await engine.EnqueueAsync(item, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"启动任务失败：{item.Url}");
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            _active.TryRemove(item.Id, out _);
            ItemFailed?.Invoke(this, item);
            _history.MarkDirty();
            PumpQueue();
        }
    }

    // ------------------------------------------------------------------ 引擎回调
    private void OnEngineProgress(object? sender, DownloadProgressEventArgs e)
    {
        var item = e.Item;
        if (e.TotalBytes > 0) item.TotalBytes = e.TotalBytes;
        if (e.DownloadedBytes > 0) item.DownloadedBytes = e.DownloadedBytes;
        item.Speed = e.Speed;
        item.EtaSeconds = e.EtaSeconds;
        if (e.Connections > 0) item.Connections = e.Connections;
        if (item.Status is DownloadStatus.Connecting or DownloadStatus.Queued) item.Status = DownloadStatus.Downloading;

        ItemChanged?.Invoke(this, item);
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineCompleted(object? sender, DownloadDoneEventArgs e)
    {
        var item = e.Item;
        item.Status = DownloadStatus.Completed;
        item.CompletedAt = DateTime.Now;
        item.Speed = 0;
        item.EtaSeconds = 0;
        item.Connections = 0;
        if (!string.IsNullOrWhiteSpace(e.FullPath))
        {
            item.FullPath = e.FullPath;
            item.FileName = Path.GetFileName(e.FullPath!) ?? item.FileName;
            try
            {
                var fi = new FileInfo(e.FullPath!);
                if (fi.Exists) item.TotalBytes = fi.Length;
            }
            catch { /* ignore */ }
        }
        item.DownloadedBytes = item.TotalBytes;

        _active.TryRemove(item.Id, out _);
        _history.MarkDirty();
        Log.Info($"下载完成：{item.FullPath ?? item.FileName}");
        ItemCompleted?.Invoke(this, item);
        Notice?.Invoke(this, $"「{Format.Truncate(item.DisplayName, 40)}」下载完成");
        PumpQueue();
    }

    private void OnEngineFailed(object? sender, DownloadFailedEventArgs e)
    {
        var item = e.Item;
        item.Status = DownloadStatus.Failed;
        item.ErrorMessage = e.Error;
        item.Speed = 0;
        item.Connections = 0;

        _active.TryRemove(item.Id, out _);
        _history.MarkDirty();
        Log.Error($"下载失败：{item.Url} -> {e.Error}");
        ItemFailed?.Invoke(this, item);
        Notice?.Invoke(this, $"「{Format.Truncate(item.DisplayName, 40)}」下载失败：{Format.Truncate(e.Error, 80)}");
        PumpQueue();
    }

    // ------------------------------------------------------------------ 控制
    public async Task PauseAsync(string id)
    {
        var item = Find(id);
        if (item is null) return;
        var engine = EngineOf(item);
        if (engine is not null) await engine.PauseAsync(item).ConfigureAwait(false);
        item.Status = DownloadStatus.Paused;
        _active.TryRemove(id, out _);
        _history.MarkDirty();
        ItemChanged?.Invoke(this, item);
        PumpQueue();
    }

    public async Task ResumeAsync(string id)
    {
        var item = Find(id);
        if (item is null) return;
        if (item.Status == DownloadStatus.Completed) return;

        var engine = EngineOf(item);
        if (engine is not null && item.Gid is not null && item.Engine == EngineKind.Aria2)
        {
            if (_active.TryAdd(id, item))
            {
                await engine.ResumeAsync(item).ConfigureAwait(false);
                return;
            }
        }
        _queue.Enqueue(item);
        item.Status = DownloadStatus.Queued;
        ItemChanged?.Invoke(this, item);
        lock (_pumpLock) PumpQueue();
    }

    public async Task CancelAsync(string id, bool deleteFile)
    {
        var item = Find(id);
        if (item is null) return;
        var engine = EngineOf(item);
        if (engine is not null) await engine.CancelAsync(item, deleteFile).ConfigureAwait(false);
        item.Status = DownloadStatus.Canceled;
        _active.TryRemove(id, out _);
        _history.MarkDirty();
        ItemChanged?.Invoke(this, item);
        PumpQueue();
    }

    /// <summary>失败/取消后重试：重新解析并重新排队。</summary>
    public async Task RetryAsync(string id)
    {
        var item = Find(id);
        if (item is null) return;
        item.ErrorMessage = null;
        item.DownloadedBytes = 0;
        item.Connections = 0;
        item.Gid = null;
        if (string.IsNullOrWhiteSpace(item.FinalUrl))
        {
            var r = await ResolveAsync(item.Url).ConfigureAwait(false);
            if (r.Success) item.FinalUrl = r.Url;
        }
        await ResumeAsync(id).ConfigureAwait(false);
    }

    public void RemoveFromHistory(string id, bool deleteFile)
    {
        var item = Find(id);
        if (item is not null && deleteFile) Aria2Engine.TryDeleteFile(item);
        _history.Remove(id);
        _active.TryRemove(id, out _);
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从列表移除任务。如果它还在下载，会先**真正停掉引擎**（否则删了列表项后台还在写文件）。
    /// <paramref name="deleteFile"/> 为 true 时同时删除已下载的部分与续传状态。
    /// </summary>
    public async Task RemoveAsync(string id, bool deleteFile)
    {
        var item = Find(id);
        if (item is null) return;

        if (item.IsActive || item.Status == DownloadStatus.Queued)
        {
            var engine = EngineOf(item);
            if (engine is not null)
            {
                try { await engine.CancelAsync(item, deleteFile).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn($"移除任务时停止引擎失败：{ex.Message}"); }
            }
        }
        else if (deleteFile)
        {
            Aria2Engine.TryDeleteFile(item);
        }

        // 清理自研引擎的续传状态文件
        if (deleteFile && !string.IsNullOrWhiteSpace(item.StatePath))
        {
            try { if (File.Exists(item.StatePath)) File.Delete(item.StatePath); } catch { /* ignore */ }
        }

        _active.TryRemove(id, out _);
        _history.Remove(id);
        Log.Info($"已从列表移除：{item.DisplayName}（删除文件={deleteFile}）");
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public DownloadItem? Find(string id) =>
        _history.Items.FirstOrDefault(i => i.Id == id) ?? (_active.TryGetValue(id, out var a) ? a : null);

    private IDownloadEngine? EngineOf(DownloadItem item) => item.Engine switch
    {
        EngineKind.Aria2 => _aria2,
        EngineKind.YtDlp => _ytdlp,
        EngineKind.Http => _http,
        _ => null
    };

    // ------------------------------------------------------------------ 浏览器插件入口
    public async Task<(int Accepted, int Rejected, List<string> Errors)> AddFromExtensionAsync(
        IEnumerable<MediaTaskDto> tasks, CancellationToken ct = default)
    {
        int accepted = 0, rejected = 0;
        var errors = new List<string>();

        foreach (var t in tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Url)) { rejected++; continue; }
            try
            {
                var kind = t.Kind?.ToLowerInvariant() switch
                {
                    "video" => DownloadKind.Video,
                    "hls" => DownloadKind.Hls,
                    "file" => DownloadKind.File,
                    _ => t.IsHls ? DownloadKind.Hls : DownloadKind.File
                };

                var req = new AddDownloadRequest
                {
                    Url = t.Url!,
                    Kind = kind,
                    Title = t.Title,
                    FileName = t.FileName,
                    Referer = t.Referer,
                    UserAgent = t.UserAgent,
                    Cookies = t.Cookies,
                    Origin = t.Origin,
                    Headers = t.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Site = t.Site,
                    Quality = t.Quality,
                    Thumbnail = t.Thumbnail,
                    Duration = t.Duration,
                    IsHls = t.IsHls,
                    Encrypted = t.Encrypted,
                    ProviderName = "BrowserExtension",
                    // 插件嗅探时已经知道的信息，别丢：
                    TotalBytes = t.TotalBytes,
                    Codec = t.Codec,
                    AudioUrl = t.AudioUrl,
                    AudioOnly = t.AudioOnly
                };

                // 插件带来的 Cookie 顺手存进站点缓存：用户在浏览器里登录过一次，
                // 以后在程序里直接粘链接也能用这份登录态（不用再手填 Cookie）。
                var cookieSource = t.Referer ?? t.Url;
                if (!string.IsNullOrWhiteSpace(t.Cookies))
                    Cookies.SaveFromExtension(cookieSource, t.Cookies);

                // 插件给的多半已经是 m3u8/直链，解析一次成本很低，但失败也不阻塞
                var item = await AddAsync(req, ct).ConfigureAwait(false);
                if (item.Status == DownloadStatus.Failed) { rejected++; errors.Add(item.ErrorMessage ?? "添加失败"); }
                else accepted++;
            }
            catch (Exception ex)
            {
                rejected++;
                errors.Add(ex.Message);
            }
        }
        StatsChanged?.Invoke(this, EventArgs.Empty);
        return (accepted, rejected, errors);
    }

    // ------------------------------------------------------------------ 收尾
    public async ValueTask DisposeAsync()
    {
        try { await _aria2.StopAsync().ConfigureAwait(false); } catch { }
        try { await _http.StopAsync().ConfigureAwait(false); } catch { }
        try { await _ytdlp.StopAsync().ConfigureAwait(false); } catch { }
        _providerHttp.Dispose();
        _history.Dispose();
    }
}
