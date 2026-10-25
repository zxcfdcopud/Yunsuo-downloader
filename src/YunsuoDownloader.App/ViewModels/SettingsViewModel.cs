using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>
/// 「设置」页（需求 9：可修改默认下载位置；同时集中管理并发/线程/限速/主题/更新/网盘账号）。
/// 所有改动即时生效并落盘，无需点"保存"。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly bool _loading;

    public static IReadOnlyList<string> Themes { get; } = new[] { "跟随系统", "浅色", "深色" };
    public static IReadOnlyList<string> ProxyModes { get; } = new[] { "不使用代理", "使用系统代理", "手动指定" };

    /// <summary>线程数策略：自动（按文件推荐）/ 固定值。</summary>
    public static IReadOnlyList<string> ConnectionModes { get; } = new[] { "自动（按文件推荐）", "固定值" };

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

    public ObservableCollection<PanRow> PanRows { get; } = new();

    [ObservableProperty] private string _downloadDirectory = string.Empty;
    [ObservableProperty] private string _directoryHint = string.Empty;
    [ObservableProperty] private bool _askForDirectoryEachTime;
    [ObservableProperty] private bool _createSubfolderPerTask;
    [ObservableProperty] private bool _deleteFileWithTask;

    [ObservableProperty] private int _maxConcurrentDownloads = 3;
    [ObservableProperty] private int _connectionsPerDownload = 8;
    [ObservableProperty] private int _autoConnectionCap = 16;
    [ObservableProperty] private string _connectionModeDisplay = "自动（按文件推荐）";
    [ObservableProperty] private bool _isAutoConnectionMode = true;
    [ObservableProperty] private bool _isFixedConnectionMode;
    [ObservableProperty] private string _connectionModeHint = string.Empty;
    [ObservableProperty] private int _concurrentFragments = 16;
    [ObservableProperty] private int _globalSpeedLimitKbps;
    [ObservableProperty] private int _minSplitSizeKb = 1024;
    [ObservableProperty] private bool _autoStartDownload = true;
    [ObservableProperty] private bool _useAria2 = true;
    [ObservableProperty] private bool _useYtDlpForVideoPages = true;
    [ObservableProperty] private bool _autoConvertHlsToMp4 = true;
    [ObservableProperty] private QualityOption _preferredQuality = new();

    [ObservableProperty] private string _themeDisplay = "跟随系统";
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _startWithWindows;

    [ObservableProperty] private bool _checkUpdatesOnStartup = true;
    [ObservableProperty] private string _updateFeedUrl = string.Empty;
    [ObservableProperty] private string _updateStatus = "尚未检查。";

    [ObservableProperty] private string _proxyModeDisplay = "不使用代理";
    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string _userAgentOverride = string.Empty;

    [ObservableProperty] private int _maxHistoryItems = 500;
    [ObservableProperty] private bool _saveHistory = true;

    [ObservableProperty] private bool _extensionEnabled = true;
    [ObservableProperty] private int _extensionPort = 16888;
    [ObservableProperty] private string _extensionToken = string.Empty;
    [ObservableProperty] private string _extensionStatus = "未启动";
    [ObservableProperty] private int _extensionClients;

    [ObservableProperty] private string _toolStatus = "正在检测…";
    [ObservableProperty] private string _aria2Path = string.Empty;
    [ObservableProperty] private string _ytDlpPath = string.Empty;
    [ObservableProperty] private string _ffmpegPath = string.Empty;

    public SettingsViewModel()
    {
        if (!AppServices.Initialized) { _loading = true; return; }

        // 分段计时：这个构造函数实测要 1.7 秒，必须知道是哪一段（见下方 Log.Info）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) { marks.Add($"{what} {sw.ElapsedMilliseconds}"); sw.Restart(); }

        _loading = true;
        var s = AppServices.Settings.Current;
        Mark("取设置");

        DownloadDirectory = s.DownloadDirectory;
        AskForDirectoryEachTime = s.AskForDirectoryEachTime;
        CreateSubfolderPerTask = s.CreateSubfolderPerTask;
        DeleteFileWithTask = s.DeleteFileWithTask;

        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        ConnectionsPerDownload = s.ConnectionsPerDownload;
        AutoConnectionCap = s.AutoConnectionCap;
        ConnectionModeDisplay = string.Equals(s.ConnectionMode, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? "固定值"
            : "自动（按文件推荐）";
        UpdateConnectionModeFlags();
        ConcurrentFragments = s.ConcurrentFragments;
        GlobalSpeedLimitKbps = (int)Math.Clamp(s.GlobalSpeedLimitKbps, 0, 1024 * 1024);
        MinSplitSizeKb = s.MinSplitSizeKb;
        AutoStartDownload = s.AutoStartDownload;
        UseAria2 = s.UseAria2ForDirectLinks;
        UseYtDlpForVideoPages = s.UseYtDlpForVideoPages;
        AutoConvertHlsToMp4 = s.AutoConvertHlsToMp4;
        PreferredQuality = QualityOptions.FirstOrDefault(q => q.Key == s.PreferredVideoQuality) ?? QualityOptions[0];

        ThemeDisplay = s.Theme switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随系统" };
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        StartWithWindows = s.StartWithWindows;

        CheckUpdatesOnStartup = s.CheckUpdatesOnStartup;
        UpdateFeedUrl = s.UpdateFeedUrl;

        ProxyModeDisplay = s.ProxyMode switch { "System" => "使用系统代理", "Manual" => "手动指定", _ => "不使用代理" };
        ProxyUrl = s.ProxyUrl;
        UserAgentOverride = s.UserAgentOverride ?? string.Empty;

        MaxHistoryItems = s.MaxHistoryItems;
        SaveHistory = s.SaveHistory;

        ExtensionEnabled = s.MediaServerEnabled;
        ExtensionPort = s.MediaServerPort;
        ExtensionToken = s.MediaServerToken;

        ReloadCredentials();
        Mark("ReloadCredentials");
        RefreshTools();
        Mark("RefreshTools");
        RefreshExtensionStatus();
        Mark("RefreshExtensionStatus");

        _loading = false;
        UpdateDirectoryHint();
        Mark("UpdateDirectoryHint");

        Log.Info("[设置页 VM] 分段耗时(ms)：" + string.Join(" | ", marks));
    }

    // ------------------------------------------------------------------ 目录
    private void UpdateDirectoryHint()
    {
        try
        {
            var exists = Directory.Exists(DownloadDirectory);
            var drive = Path.GetPathRoot(DownloadDirectory) ?? "?";
            DirectoryHint = exists
                ? $"目录已存在 · 所在盘 {drive}"
                : $"目录尚不存在，首次下载时会自动创建 · 所在盘 {drive}";
        }
        catch { DirectoryHint = "路径格式无法识别"; }
    }

    partial void OnDownloadDirectoryChanged(string value)
    {
        UpdateDirectoryHint();
        if (_loading) return;
        AppServices.Settings.Update(s => s.DownloadDirectory = value);
        _ = AppServices.Extension.PushConfigAsync();
    }

    [RelayCommand]
    private void BrowseDownloadDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认下载位置",
            InitialDirectory = Directory.Exists(DownloadDirectory) ? DownloadDirectory : AppSettings.DefaultDownloadDirectory,
            Multiselect = false
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            DownloadDirectory = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenDownloadDirectory()
    {
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            Process.Start(new ProcessStartInfo(DownloadDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void ResetDownloadDirectory() => DownloadDirectory = AppSettings.DefaultDownloadDirectory;

    // ------------------------------------------------------------------ 写穿透
    partial void OnAskForDirectoryEachTimeChanged(bool value) => Write(s => s.AskForDirectoryEachTime = value);
    partial void OnCreateSubfolderPerTaskChanged(bool value) => Write(s => s.CreateSubfolderPerTask = value);
    partial void OnDeleteFileWithTaskChanged(bool value) => Write(s => s.DeleteFileWithTask = value);
    partial void OnMaxConcurrentDownloadsChanged(int value) => Write(s => s.MaxConcurrentDownloads = Math.Clamp(value, 1, 32));
    partial void OnConnectionsPerDownloadChanged(int value) => Write(s => s.ConnectionsPerDownload = Math.Clamp(value, 1, 64));
    partial void OnAutoConnectionCapChanged(int value)
    {
        Write(s => s.AutoConnectionCap = Math.Clamp(value, 1, 64));
        UpdateConnectionModeFlags();
    }

    partial void OnConnectionModeDisplayChanged(string value)
    {
        Write(s => s.ConnectionMode = value == "固定值" ? "Fixed" : "Auto");
        UpdateConnectionModeFlags();
    }

    /// <summary>把当前策略写成一句人话，顺便控制两个输入框谁可用。</summary>
    private void UpdateConnectionModeFlags()
    {
        var auto = ConnectionModeDisplay != "固定值";
        IsAutoConnectionMode = auto;
        IsFixedConnectionMode = !auto;
        ConnectionModeHint = auto
            ? $"自动模式：按文件大小和服务器的分片能力决定线程数，上限 {AutoConnectionCap}。" +
              "小文件会自动少开线程（多线程建连开销反而更慢），不支持断点续传的服务器自动用单线程。" +
              "觉得某类文件还是慢，可以在新建任务页为单个任务手动指定。"
            : $"固定模式：所有任务都用 {ConnectionsPerDownload} 线程（1~64）。服务器限速或文件很小时，线程开了也快不了。";
    }
    partial void OnConcurrentFragmentsChanged(int value) => Write(s => s.ConcurrentFragments = Math.Clamp(value, 1, 64));
    partial void OnGlobalSpeedLimitKbpsChanged(int value) => Write(s => s.GlobalSpeedLimitKbps = Math.Max(0, value));
    partial void OnMinSplitSizeKbChanged(int value) => Write(s => s.MinSplitSizeKb = Math.Clamp(value, 1024, 1024 * 1024));
    partial void OnAutoStartDownloadChanged(bool value) => Write(s => s.AutoStartDownload = value);
    partial void OnUseAria2Changed(bool value) => Write(s => s.UseAria2ForDirectLinks = value);
    partial void OnUseYtDlpForVideoPagesChanged(bool value) => Write(s => s.UseYtDlpForVideoPages = value);
    partial void OnAutoConvertHlsToMp4Changed(bool value) => Write(s => s.AutoConvertHlsToMp4 = value);
    partial void OnPreferredQualityChanged(QualityOption value) => Write(s => s.PreferredVideoQuality = value.Key);
    partial void OnMinimizeToTrayChanged(bool value) => Write(s => s.MinimizeToTray = value);
    partial void OnCloseToTrayChanged(bool value) => Write(s => s.CloseToTray = value);
    partial void OnCheckUpdatesOnStartupChanged(bool value) => Write(s => s.CheckUpdatesOnStartup = value);
    partial void OnUpdateFeedUrlChanged(string value) => Write(s => s.UpdateFeedUrl = value.Trim());
    partial void OnProxyUrlChanged(string value) => Write(s => s.ProxyUrl = value.Trim());
    partial void OnUserAgentOverrideChanged(string value) => Write(s => s.UserAgentOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    partial void OnMaxHistoryItemsChanged(int value) => Write(s => s.MaxHistoryItems = Math.Clamp(value, 20, 10000));
    partial void OnSaveHistoryChanged(bool value) => Write(s => s.SaveHistory = value);
    partial void OnExtensionEnabledChanged(bool value) => Write(s => s.MediaServerEnabled = value);
    partial void OnExtensionTokenChanged(string value) => Write(s => s.MediaServerToken = value.Trim());

    partial void OnStartWithWindowsChanged(bool value)
    {
        Write(s => s.StartWithWindows = value);
        App.ApplyStartWithWindows(value);
    }

    partial void OnThemeDisplayChanged(string value)
    {
        var key = value switch { "浅色" => "Light", "深色" => "Dark", _ => "System" };
        Write(s => s.Theme = key);
        App.ApplyTheme();
    }

    partial void OnProxyModeDisplayChanged(string value)
    {
        var key = value switch { "使用系统代理" => "System", "手动指定" => "Manual", _ => "None" };
        Write(s => s.ProxyMode = key);
    }

    partial void OnExtensionPortChanged(int value)
    {
        Write(s => s.MediaServerPort = Math.Clamp(value, 1024, 65535));
    }

    private void Write(Action<AppSettings> mutate)
    {
        if (_loading || !AppServices.Initialized) return;
        AppServices.Settings.Update(mutate);
    }

    // ------------------------------------------------------------------ 插件
    public void RefreshExtensionStatus()
    {
        if (!AppServices.Initialized) return;
        var ext = AppServices.Extension;
        ExtensionClients = ext.ClientCount;
        ExtensionStatus = ext.IsRunning
            ? $"运行中 · ws://{AppServices.Settings.Current.MediaServerHost}:{AppServices.Settings.Current.MediaServerPort}/media · 已连接插件 {ext.ClientCount} 个"
            : (ext.LastError ?? "未启动");
    }

    [RelayCommand]
    private async Task RestartExtensionAsync()
    {
        if (!AppServices.Initialized) return;
        var ok = await AppServices.Extension.RestartAsync();
        RefreshExtensionStatus();
        App.Notify(ok ? "插件服务已启动" : "插件服务启动失败",
            ExtensionStatus, ok ? InfoSeverity.Success : InfoSeverity.Error);
    }

    [RelayCommand]
    private void CopyExtensionToken()
    {
        try
        {
            System.Windows.Clipboard.SetText(ExtensionToken);
            App.Notify("已复制令牌", "把令牌粘贴到浏览器插件设置里即可连接", InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void RegenerateExtensionToken()
    {
        if (Views.MessageBoxWindow.Confirm("重新生成令牌后，所有已连接的浏览器插件都需要重新填写新令牌。继续吗？",
                "重新生成令牌", "重新生成", "取消") is not true) return;
        ExtensionToken = SecretProtector.NewToken();
        _ = AppServices.Extension.RestartAsync();
        App.Notify("已重新生成", "请到插件设置里更新令牌", InfoSeverity.Success);
    }

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        try
        {
            // 依次找：exe 同目录\extension（安装/便携）→ 工程目录 → 程序释放的内置副本
            var dir = ExtensionPackager.EnsureAvailable();
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Notify("找不到插件目录",
                "可以在「浏览器插件」页点「导出插件到…」，把随程序内置的插件释放到你指定的文件夹再加载。\n" + ex.Message,
                InfoSeverity.Warning);
        }
    }

    // ------------------------------------------------------------------ 工具检测
    /// <summary>
    /// 刷新外部工具状态。
    /// 关键：读取版本号要 <b>起子进程</b>（aria2c --version 之类），同步做在 UI 线程上
    /// 会让「设置」页首次打开明显卡顿（用户反馈的切换标签卡顿就是这个）。
    /// 所以这里先立刻给出路径与"检测中"，版本号丢到后台线程去补。
    /// </summary>
    public void RefreshTools()
    {
        if (!AppServices.Initialized) return;
        AppServices.Tools.Invalidate();
        var a = AppServices.Tools.Aria2;
        var y = AppServices.Tools.YtDlp;
        var f = AppServices.Tools.Ffmpeg;
        Aria2Path = a.Path ?? "";
        YtDlpPath = y.Path ?? "";
        FfmpegPath = f.Path ?? "";

        var missing = new List<string>();
        if (!a.Found) missing.Add("aria2c.exe（多线程下载内核）");
        if (!y.Found) missing.Add("yt-dlp.exe（视频/HLS 解析）");
        if (!f.Found) missing.Add("ffmpeg.exe（音视频合并）");

        if (missing.Count > 0)
        {
            ToolStatus = "缺少：" + string.Join("、", missing) +
                         "\n可以运行 scripts\\fetch-tools.ps1 自动下载，或在下方向导里手动指定路径。";
            return;
        }

        // 先给一个不含版本号的即时结果，避免界面空白
        ToolStatus = "全部就绪，正在读取版本号…";

        // 版本号在后台读（会起子进程），读完再回到 UI 线程更新
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                text = $"全部就绪：aria2 {Trim(AppServices.Tools.Aria2.Version)} · " +
                       $"yt-dlp {Trim(AppServices.Tools.YtDlp.Version)} · " +
                       $"ffmpeg {Trim(AppServices.Tools.Ffmpeg.Version)}";
            }
            catch (Exception ex)
            {
                Log.Warn("读取工具版本号失败：" + ex.Message);
                text = "全部就绪（版本号读取失败，可到「关于」页查看详情）";
            }

            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(() => ToolStatus = text));
            }
            catch { /* 程序正在退出时忽略 */ }
        });
    }

    private static string Trim(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "?";
        var v = version.Trim();
        return v.Length > 40 ? v[..40] + "…" : v;
    }

    [RelayCommand]
    private void DetectTools()
    {
        RefreshTools();
        App.Notify("工具检测完成", ToolStatus, InfoSeverity.Info);
    }

    [RelayCommand]
    private void BrowseAria2()
    {
        var p = PickExe("选择 aria2c.exe");
        if (p is null) return;
        AppServices.Settings.Update(s => s.Aria2Path = p);
        RefreshTools();
    }

    [RelayCommand]
    private void BrowseYtDlp()
    {
        var p = PickExe("选择 yt-dlp.exe");
        if (p is null) return;
        AppServices.Settings.Update(s => s.YtDlpPath = p);
        RefreshTools();
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var p = PickExe("选择 ffmpeg.exe");
        if (p is null) return;
        AppServices.Settings.Update(s => s.FfmpegPath = p);
        RefreshTools();
    }

    private static string? PickExe(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ------------------------------------------------------------------ 更新
    [RelayCommand]
    private async Task CheckUpdateNowAsync()
    {
        UpdateStatus = "正在检查更新…";
        try
        {
            var r = await AppServices.Updates.CheckAsync();
            UpdateStatus = r.Message + (r.FeedUrl is null ? "" : $"\n更新源：{r.FeedUrl}");
            App.Notify("检查更新", r.Message,
                r.Status == Core.Services.UpdateStatus.Failed ? InfoSeverity.Warning : InfoSeverity.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 网盘账号
    public void ReloadCredentials()
    {
        PanRows.Clear();
        if (!AppServices.Initialized) return;
        foreach (var (provider, display) in KnownPans)
        {
            var row = new PanRow { Provider = provider, Display = display };
            var cred = AppServices.Settings.Current.PanCredentials
                .FirstOrDefault(c => string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase));
            row.State = cred is null || string.IsNullOrEmpty(cred.Cookie)
                ? "未设置"
                : $"已设置 · 更新于 {cred.UpdatedAt:yyyy-MM-dd HH:mm}";
            row.HasCredential = cred is not null && !string.IsNullOrEmpty(cred.Cookie);
            PanRows.Add(row);
        }
    }

    /// <summary>需要在界面上列出的网盘（即使还没配 Cookie 也显示出来）。</summary>
    public static (string Provider, string Display)[] KnownPans => new[]
    {
        ("Aliyun", "阿里云盘（无需登录，建议直接粘贴分享链接）"),
        ("Quark", "夸克网盘（需要 Cookie）"),
        ("Pan123", "123 网盘（需要 Cookie）"),
        ("Baidu", "百度网盘（需要 Cookie）")
    };

    [RelayCommand]
    private void EditCredential(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        var current = AppServices.Settings.Current.PanCredentials
            .FirstOrDefault(c => c.Provider == provider)?.Cookie ?? string.Empty;

        var display = KnownPans.FirstOrDefault(k => k.Provider == provider).Display ?? provider;
        var text = Views.InputDialog.Show(
            $"设置「{display}」的登录态",
            "在浏览器里登录该网盘后按 F12 → Network → 任意请求 → 复制完整的 Cookie 请求头，粘贴到这里。\n" +
            "Cookie 会用 Windows DPAPI 加密后保存在本机，只对当前 Windows 用户可解密。",
            current, multiline: true, isSecret: true);

        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            AppServices.Settings.RemoveCredential(provider);
        }
        else
        {
            AppServices.Settings.SetCredential(provider, display, text.Trim());
        }
        ReloadCredentials();
        App.Notify("已保存", $"「{display}」的登录态已更新", InfoSeverity.Success);
    }

    [RelayCommand]
    private void RemoveCredential(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        AppServices.Settings.RemoveCredential(provider);
        ReloadCredentials();
        App.Notify("已删除", $"已清除「{provider}」的登录态", InfoSeverity.Info);
    }

    public string CredentialState(string provider)
    {
        var c = AppServices.Settings.Current.PanCredentials.FirstOrDefault(x => x.Provider == provider);
        if (c is null || string.IsNullOrEmpty(c.Cookie)) return "未设置";
        return $"已设置（{c.UpdatedAt:yyyy-MM-dd HH:mm}）";
    }
    [RelayCommand]
    private void OpenDataDirectory() => OpenPath(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenLogDirectory() => OpenPath(AppPaths.LogDirectory);

    [RelayCommand]
    private void OpenToolsDirectory()
    {
        var root = AppPaths.FindProjectRoot();
        OpenPath(root is null ? AppPaths.BaseDirectory : Path.Combine(root, "tools"));
    }

    [RelayCommand]
    private void OpenRulesFile()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "rules.json");
            if (!File.Exists(path)) JsonUtil.WriteFile(path, new List<object>());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void ReloadRules()
    {
        AppServices.Downloads.ReloadRules();
        App.Notify("已重新加载", "自定义解析规则已重新载入", InfoSeverity.Success);
    }

    [RelayCommand]
    private void ResetAllSettings()
    {
        if (Views.MessageBoxWindow.Confirm("确定要把所有设置恢复为默认值吗？（网盘 Cookie 不会被清除）",
                "恢复默认设置", "恢复默认", "取消") is not true) return;

        AppServices.Settings.Update(s =>
        {
            var dir = s.DownloadDirectory;
            var token = s.MediaServerToken;
            var creds = s.PanCredentials;
            var fresh = new AppSettings { DownloadDirectory = dir, MediaServerToken = token, PanCredentials = creds, FirstRunCompleted = true };
            foreach (var prop in typeof(AppSettings).GetProperties())
            {
                if (!prop.CanWrite) continue;
                prop.SetValue(s, prop.GetValue(fresh));
            }
        });
        App.Notify("已恢复默认设置", "部分设置需要重启程序后完全生效", InfoSeverity.Success);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }
}
