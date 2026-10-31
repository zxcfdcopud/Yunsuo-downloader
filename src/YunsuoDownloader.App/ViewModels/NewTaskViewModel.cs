using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Engine;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>「下载线程」下拉项。Value = 0 表示交给程序自动推荐。</summary>
public sealed partial class ConnectionOption : ObservableObject
{
    public int Value { get; init; }

    [ObservableProperty] private string _display = string.Empty;

    public bool IsAuto => Value <= 0;
}

/// <summary>「新建任务」页：粘贴链接 → 解析预览 → 加入下载队列。</summary>
public sealed partial class NewTaskViewModel : ObservableObject
{
    [ObservableProperty] private string _urlText = string.Empty;
    [ObservableProperty] private string _directory = string.Empty;
    [ObservableProperty] private bool _startImmediately = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "把链接粘贴到上面（支持每行一条）。支持直链、磁力、迅雷、m3u8，以及百度/阿里/夸克/123 网盘分享链接。";
    [ObservableProperty] private string? _detectedProvider;
    [ObservableProperty] private string? _detectedKind;
    [ObservableProperty] private string? _detectedFileName;
    [ObservableProperty] private string? _detectedSize;
    [ObservableProperty] private string? _previewTitle;
    [ObservableProperty] private string? _previewThumbnail;
    [ObservableProperty] private bool _hasPreview;

    private VideoInfo? _videoInfo;
    private QualityOption _selectedQuality = new();

    // ------------------------------------------------------------------ 下载线程（可选 + 自动推荐）
    /// <summary>线程数下拉项：自动 / 1 / 2 / 4 / 8 / 12 / 16 / 24 / 32。</summary>
    public ObservableCollection<ConnectionOption> ConnectionOptions { get; } = new();

    [ObservableProperty] private ConnectionOption? _selectedConnection;

    [ObservableProperty] private string _connectionHint = "自动模式：按文件大小和服务器的分片能力决定线程数，小文件会自动少开线程。";

    private string? _adviceReason;

    /// <summary>解析出文件后，把"自动"那一项的文案换成实际推荐值（例如「自动（推荐 8 线程）」）。</summary>
    private void UpdateConnectionAdvice(long totalBytes, DownloadKind kind, bool panDirect)
    {
        var cap = AppServices.Initialized ? AppServices.Settings.Current.AutoConnectionCap : 16;
        var advice = ConnectionAdvisor.Recommend(totalBytes, kind, null, panDirect, cap);
        _adviceReason = advice.Reason;
        var auto = ConnectionOptions.FirstOrDefault(o => o.IsAuto);
        if (auto is not null) auto.Display = advice.AutoDisplay;
        UpdateConnectionHint();
    }

    private void UpdateConnectionHint()
    {
        var v = SelectedConnection?.Value ?? 0;
        if (v > 0)
        {
            ConnectionHint = $"只对本次任务生效：固定用 {v} 线程。（想改默认值请到「设置 → 下载性能」）";
            return;
        }
        ConnectionHint = _adviceReason is { Length: > 0 }
            ? "自动：" + _adviceReason
            : "自动模式：按文件大小和服务器的分片能力决定线程数，小文件会自动少开线程。";
    }

    partial void OnSelectedConnectionChanged(ConnectionOption? value) => UpdateConnectionHint();

    public List<QualityOption> QualityOptions { get; } = new()
    {
        new QualityOption { Key = "best", Display = "最佳画质（默认）" },
        new QualityOption { Key = "2160", Display = "2160p / 4K" },
        new QualityOption { Key = "1440", Display = "1440p / 2K" },
        new QualityOption { Key = "1080", Display = "1080p" },
        new QualityOption { Key = "720", Display = "720p" },
        new QualityOption { Key = "480", Display = "480p" },
        new QualityOption { Key = "360", Display = "360p" },
        new QualityOption { Key = "audio", Display = "仅音频" }
    };

    public QualityOption SelectedQuality
    {
        get => _selectedQuality;
        set
        {            if (SetProperty(ref _selectedQuality, value) && AppServices.Initialized)
                AppServices.Settings.Update(s => s.PreferredVideoQuality = value.Key);
        }
    }

    /// <summary>解析后可供选择的清晰度（来自 yt-dlp）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<VideoFormat> AvailableFormats { get; } = new();

    public NewTaskViewModel()
    {
        ConnectionOptions.Add(new ConnectionOption { Value = 0, Display = "自动（按文件推荐）" });
        ConnectionOptions.Add(new ConnectionOption { Value = 1, Display = "1 线程（单连接）" });
        foreach (var n in new[] { 2, 4, 8, 12, 16, 24, 32 })
            ConnectionOptions.Add(new ConnectionOption { Value = n, Display = $"{n} 线程" });

        if (AppServices.Initialized)
        {
            var s = AppServices.Settings.Current;
            Directory = s.DownloadDirectory;
            var key = s.PreferredVideoQuality;
            SelectedQuality = QualityOptions.FirstOrDefault(q => q.Key == key) ?? QualityOptions[0];

            // 设置里选了"固定值"就默认选中同一个数字，否则默认"自动"
            var initial = string.Equals(s.ConnectionMode, "Fixed", StringComparison.OrdinalIgnoreCase)
                ? s.ConnectionsPerDownload
                : 0;
            SelectedConnection = ConnectionOptions.FirstOrDefault(o => o.Value == initial) ?? ConnectionOptions[0];
        }
        else
        {
            Directory = AppSettings.DefaultDownloadDirectory;
            SelectedConnection = ConnectionOptions[0];
        }
    }

    public string[] ParseUrls() => UrlText
        .Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Distinct()
        .ToArray();

    // ------------------------------------------------------------------ 浏览目录
    [RelayCommand]
    private void BrowseDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存位置",
            InitialDirectory = System.IO.Directory.Exists(Directory) ? Directory : AppSettings.DefaultDownloadDirectory,
            Multiselect = false
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            Directory = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开目录失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void UseDefaultDirectory()
        => Directory = AppServices.Initialized ? AppServices.Settings.Current.DownloadDirectory : AppSettings.DefaultDownloadDirectory;

    // ------------------------------------------------------------------ 解析预览
    /// <summary>用户在本次会话里为网盘分享填过的提取码（按 链接→提取码 记住）。</summary>
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    private List<string> _lastDiagnostics = new();

    /// <summary>最近一次解析是否留下了可查看的诊断日志（网盘接口排查用）。</summary>
    [ObservableProperty] private bool _hasDiagnostics;

    [RelayCommand]
    private void ShowParseLog()
    {
        if (_lastDiagnostics.Count == 0)
        {
            App.Notify("没有日志", "这次解析没有留下诊断信息。", InfoSeverity.Info);
            return;
        }
        Views.MessageBoxWindow.ShowInfo(
            "下面是这次解析实际发出的请求与收到的返回，接口改版时就照这个定位：\n\n" +
            string.Join("\n", _lastDiagnostics),
            "解析过程日志");
    }

    [RelayCommand]
    private async Task ParseAsync()
    {
        var urls = ParseUrls();
        if (urls.Length == 0)
        {
            StatusMessage = "请先粘贴至少一条链接。";
            return;
        }
        var url = urls[0];

        IsBusy = true;
        StatusMessage = "正在解析链接…";
        HasPreview = false;
        AvailableFormats.Clear();
        _videoInfo = null;

        try
        {
            var provider = AppServices.Downloads.MatchProvider(url);
            _passwords.TryGetValue(url, out var remembered);

            var result = await AppServices.Downloads.ResolveAsync(url, remembered, verbose: false);

            // 需要提取码（或提取码错了）：弹框问用户，然后带上去重试一次
            if (result.NeedPassword)
            {
                IsBusy = false;
                var pwd = Views.InputDialog.Show(
                    result.PasswordWrong ? "提取码不正确" : "填写提取码",
                    $"「{provider?.DisplayName ?? "网盘"}」的这个分享需要提取码（访问密码）。\n" +
                    "提示：也可以直接写成 https://.../s/xxxx?pwd=1234 的样子粘进来。\n\n" +
                    (result.PasswordWrong ? "上次填的不对，请重新输入。" : "请输入分享的提取码："),
                    remembered ?? string.Empty);

                if (pwd is null || string.IsNullOrWhiteSpace(pwd))
                {
                    StatusMessage = "已取消：这个分享需要提取码才能解析。";
                    return;
                }

                _passwords[url] = pwd.Trim();
                IsBusy = true;
                StatusMessage = "正在用提取码重新解析…";
                result = await AppServices.Downloads.ResolveAsync(url, pwd.Trim(), verbose: false);
            }

            DetectedProvider = result.Success ? result.ProviderName : null;
            DetectedKind = result.Kind.ToString();
            DetectedFileName = result.FileName;
            DetectedSize = result.TotalBytes > 0 ? Format.Bytes(result.TotalBytes) : null;
            PreviewTitle = result.Title ?? result.FileName;
            PreviewThumbnail = result.Thumbnail;

            if (!result.Success)
            {
                StatusMessage = "解析失败：" + result.ErrorMessage;
                // 需要登录态的网盘（百度 / 夸克）解析失败时，顺手提示去哪里登录
                if (provider is not null && provider.RequiresCredential &&
                    !(AppServices.Settings.Current.PanCredentials
                        .Any(c => c.Provider == provider.Name && !string.IsNullOrEmpty(c.Cookie))))
                    StatusMessage += "\n提示：该网盘需要登录态。可以到「网盘解析」页点登录，或安装浏览器插件后在网盘网页登录一次。";

                if (result.Diagnostics.Count > 0)
                {
                    _lastDiagnostics = result.Diagnostics;
                    HasDiagnostics = true;
                    StatusMessage += "\n（已记录解析步骤，可点「查看解析日志」）";
                }
                return;
            }

            _lastDiagnostics = result.Diagnostics;
            HasDiagnostics = result.Diagnostics.Count > 0;

            HasPreview = true;
            StatusMessage = result.Message ?? "解析成功。";

            // 拿到大小/类型后刷新"下载线程"的推荐值
            var panDirect = provider?.Name is "Baidu" or "Aliyun" or "Quark" or "Pan123";
            UpdateConnectionAdvice(result.TotalBytes, result.Kind, panDirect);
            if (result.PreferYtDlp)
            {
                StatusMessage += "\n正在读取可用清晰度…";
                var info = await AppServices.Downloads.ProbeVideoAsync(url);
                if (info is not null)
                {
                    _videoInfo = info;
                    PreviewTitle = info.DisplayTitle;
                    PreviewThumbnail = info.Thumbnail ?? PreviewThumbnail;
                    foreach (var f in info.Formats
                                 .Where(f => f.HasVideo && f.Height > 0)
                                 .GroupBy(f => f.Height)
                                 .Select(g => g.OrderByDescending(x => x.TotalBitrate).First())
                                 .OrderByDescending(f => f.Height)
                                 .Take(12))
                        AvailableFormats.Add(f);

                    var audio = info.Formats.Where(f => f.HasAudio && !f.HasVideo).OrderByDescending(f => f.TotalBitrate).FirstOrDefault();
                    if (audio is not null) AvailableFormats.Add(audio);

                    StatusMessage = $"解析成功：{info.DisplayTitle}" +
                                    (info.Duration > 0 ? $"（时长 {Format.Duration(TimeSpan.FromSeconds(info.Duration))}）" : "") +
                                    (AvailableFormats.Count > 0 ? $"\n检测到 {AvailableFormats.Count} 种可选画质。" : "");
                }
                else
                {
                    StatusMessage += "\n（未能读取清晰度列表，将按设置里的默认画质下载）";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "预览解析失败");
            StatusMessage = "解析出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 添加
    /// <summary>「开始下载」：按设置里的清晰度下（qualityOverride = null）。</summary>
    [RelayCommand]
    private Task AddAsync() => AddCoreAsync(null);

    /// <summary>
    /// 点某个清晰度标签就直接按这个清晰度下载。
    /// 之前这些标签只是"展示"，点了没反应（还要自己回上面改下拉框），
    /// 现在点哪个就下哪个。
    /// </summary>
    [RelayCommand]
    private Task DownloadWithQuality(VideoFormat? format) => AddCoreAsync(QualityKeyOf(format));

    /// <summary>把 yt-dlp 的一路格式翻译成 <see cref="AddDownloadRequest.Quality"/> 用的档位字符串。</summary>
    private static string? QualityKeyOf(VideoFormat? format)
    {
        if (format is null) return null;
        if (format.Height >= 144) return format.Height.ToString();
        if (format.HasAudio && !format.HasVideo) return "audio";
        return "best";
    }

    private async Task AddCoreAsync(string? qualityOverride)
    {
        var urls = ParseUrls();
        if (urls.Length == 0)
        {
            StatusMessage = "请先粘贴至少一条链接。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Directory))
        {
            StatusMessage = "请先选择下载保存位置。";
            return;
        }
        if (!PathGuard.IsUsableDirectory(Directory))
        {
            StatusMessage = $"保存位置不是有效的文件夹路径：{Directory}\n点「浏览…」重新选一个目录（别把链接填进来）。";
            return;
        }

        IsBusy = true;
        int ok = 0, fail = 0;
        var errors = new List<string>();

        try
        {
            foreach (var url in urls)
            {
                try
                {
                    var item = await AppServices.Downloads.AddAsync(new AddDownloadRequest
                    {
                        Url = url,
                        Directory = Directory,
                        StartImmediately = StartImmediately,
                        Title = urls.Length == 1 ? PreviewTitle : null,
                        FileName = urls.Length == 1 ? DetectedFileName : null,
                        Thumbnail = urls.Length == 1 ? PreviewThumbnail : null,
                        Password = _passwords.GetValueOrDefault(url),
                        Connections = SelectedConnection?.Value ?? 0,
                        // 点清晰度标签时带的档位；走「开始下载」时为 null，由引擎用设置里的清晰度
                        Quality = qualityOverride
                    });
                    if (item.Status == DownloadStatus.Failed) { fail++; errors.Add(item.ErrorMessage ?? url); }
                    else ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    errors.Add(ex.Message);
                }
            }

            StatusMessage = fail == 0
                ? $"已添加 {ok} 个任务，正在下载。"
                : $"已添加 {ok} 个，失败 {fail} 个：{string.Join("；", errors.Take(3))}";

            App.Notify("新建任务", StatusMessage, fail == 0 ? InfoSeverity.Success : InfoSeverity.Warning);

            if (fail == 0)
            {
                UrlText = string.Empty;
                HasPreview = false;
                AvailableFormats.Clear();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        UrlText = string.Empty;
        HasPreview = false;
        AvailableFormats.Clear();
        StatusMessage = "已清空。";
    }

    /// <summary>把剪贴板里的链接直接填进来。</summary>
    [RelayCommand]
    private async Task PasteFromClipboardAsync()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                UrlText = System.Windows.Clipboard.GetText();
                await ParseAsync();
            }
            else StatusMessage = "剪贴板里没有文本。";
        }
        catch (Exception ex) { StatusMessage = "读取剪贴板失败：" + ex.Message; }
    }

    /// <summary>页面把拖进来的文件/文本交给这里。</summary>
    public void AcceptDroppedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        UrlText = string.IsNullOrWhiteSpace(UrlText) ? text : UrlText + Environment.NewLine + text;
        _ = ParseAsync();
    }
}
