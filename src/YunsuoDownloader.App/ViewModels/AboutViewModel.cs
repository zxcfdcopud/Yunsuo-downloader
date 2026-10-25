using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>开源项目致谢 / 参考条目（需求 4：说明借鉴了哪些开源代码并给出项目地址）。</summary>
public sealed class CreditItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string License { get; init; } = "见项目仓库";
    public string Usage { get; init; } = string.Empty;
    public bool IsExternal => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}

/// <summary>「关于与开源致谢」页：版本、检查更新、开源清单、运行环境。</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    [ObservableProperty] private string _versionText = UpdateService.CurrentVersionText;
    [ObservableProperty] private string _updateStatus = "点「检查更新」看看有没有新版本。";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _newVersionText = string.Empty;
    [ObservableProperty] private string _releaseNotes = string.Empty;
    [ObservableProperty] private string _updateFeed = string.Empty;
    [ObservableProperty] private string _environmentText = string.Empty;

    private UpdateInfo? _info;
    private string? _downloadedInstaller;

    public ObservableCollection<CreditItem> Credits { get; } = new();

    /// <summary>需求 4：本程序借鉴/使用的开源项目清单。完整版另见 docs/第三方开源项目清单.txt。</summary>
    public static readonly CreditItem[] KnownCredits =
    {
        new() { Name = "aria2", Url = "https://github.com/aria2/aria2", License = "GPL-2.0-or-later",
                Usage = "多线程分片下载内核。本程序以内置进程 + JSON-RPC 方式调用它，是“下载要快”的主力（单文件最多 16 连接，同时支持 BT/磁力）。" },
        new() { Name = "yt-dlp", Url = "https://github.com/yt-dlp/yt-dlp", License = "Unlicense",
                Usage = "视频网站与 HLS/DASH 流解析内核。B站、YouTube、抖音、快手、小红书、m3u8 直播等的画质枚举与下载都走它。" },
        new() { Name = "youtube-dl", Url = "https://github.com/ytdl-org/youtube-dl", License = "Unlicense",
                Usage = "yt-dlp 的上游项目，其 extractor 架构与命令行参数设计被沿用。" },
        new() { Name = "FFmpeg", Url = "https://github.com/FFmpeg/FFmpeg", License = "LGPL-2.1 / GPL-2.0（取决于构建）",
                Usage = "音视频合并、HLS 分片转封装为 MP4、时长探测（ffprobe）。" },
        new() { Name = "WPF-UI (lepoco/wpfui)", Url = "https://github.com/lepoco/wpfui", License = "MIT",
                Usage = "Fluent 2 设计规范在 WPF 上的实现：FluentWindow、Mica 背景、圆角、标题栏、Fluent 控件与字体图标。" },
        new() { Name = "CommunityToolkit.Mvvm", Url = "https://github.com/CommunityToolkit/dotnet", License = "MIT",
                Usage = "MVVM 基类与命令/属性源生成器。" },
        new() { Name = "Fluent 2 设计规范", Url = "https://fluent2.microsoft.design/", License = "设计规范（非代码）",
                Usage = "界面视觉与交互的唯一设计依据：颜色层级、圆角、间距、控件形态。" },
        new() { Name = "Fluent System Icons", Url = "https://github.com/microsoft/fluentui-system-icons", License = "MIT",
                Usage = "界面图标字形（Segoe Fluent Icons 码位）。" },
        new() { Name = "Motrix", Url = "https://github.com/agalwood/Motrix", License = "MIT",
                Usage = "参考了它“本地起 aria2 进程 + RPC 调度 + 任务队列”的整体架构思路。" },
        new() { Name = "Gopeed", Url = "https://github.com/GopeedLab/gopeed", License = "GPL-3.0",
                Usage = "参考了多引擎（内置引擎 / 外部下载器）统一抽象与任务状态机的设计。" },
        new() { Name = "N_m3u8DL-RE", Url = "https://github.com/nilaoda/N_m3u8DL-RE", License = "MIT",
                Usage = "参考了 m3u8 分片并发拉取、加密流识别与合并的实现思路。" },
        new() { Name = "BaiduPCS-Go", Url = "https://github.com/qjfoidnh/BaiduPCS-Go", License = "Apache-2.0",
                Usage = "百度网盘分享解析（shareid/uk 获取、文件列表、直链签名）的接口流程参考。" },
        new() { Name = "aliyunpan", Url = "https://github.com/tickstep/aliyunpan", License = "Apache-2.0",
                Usage = "阿里云盘匿名分享 token / 文件列表 / 直链接口的调用流程参考。" },
        new() { Name = "quark-auto-save", Url = "https://github.com/Cp0204/quark-auto-save", License = "以仓库为准",
                Usage = "夸克网盘分享 token、目录列表、转存与取直链的接口流程参考。" },
        new() { Name = "cat-catch（抓猫）", Url = "https://github.com/xifangczy/cat-catch", License = "GPL-3.0",
                Usage = "浏览器插件侧“嗅探媒体请求 + 解析 m3u8 变体”的思路参考。" },
        new() { Name = "hls-downloader", Url = "https://github.com/puemos/hls-downloader", License = "MIT",
                Usage = "插件里 HLS 播放列表解析与清晰度枚举的做法参考。" }
    };

    public AboutViewModel()
    {
        foreach (var c in KnownCredits) Credits.Add(c);
        BuildEnvironmentText();
    }

    private void BuildEnvironmentText()
    {
        try
        {
            var lines = new List<string>
            {
                $"程序版本：{UpdateService.CurrentVersionText}",
                $"运行环境：.NET {Environment.Version}",
                $"操作系统：{Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "64 位" : "32 位")}进程）",
                $"程序目录：{AppPaths.BaseDirectory}",
                $"数据目录：{AppPaths.DataDirectory}",
                $"下载目录：{(AppServices.Initialized ? AppServices.Settings.Current.DownloadDirectory : "(未初始化)")}"
            };

            if (AppServices.Initialized)
            {
                lines.Add("—— 外部工具 ——");
                foreach (var t in AppServices.Tools.All())
                {
                    // 只写路径，不碰 Version：读版本要起子进程（三个工具 ≈1.6 秒），
                    // 同步做在构造里会让「关于」页首次打开卡住。版本号由下面的后台任务补。
                    lines.Add(t.Found ? $"{t.Name}: 检测中… ({t.Path})" : $"{t.Name}: 未找到");
                }
                lines.Add($"aria2 服务：{(AppServices.Downloads.Aria2Running ? $"运行中（端口 {AppServices.Downloads.Aria2.Port}）" : "未运行")}");
                lines.Add($"插件服务：{(AppServices.Extension.IsRunning ? $"运行中（{AppServices.Settings.Current.MediaServerPort}，已连接 {AppServices.Extension.ClientCount}）" : "未运行")}");
            }

            EnvironmentText = string.Join(Environment.NewLine, lines);

            // 版本号后台补：读完再整体重建一次文本
            if (!AppServices.Initialized) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var toolLines = AppServices.Tools.All().Select(t => t.ToString()).ToList();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var refreshed = new List<string>(lines);
                            // 把"—— 外部工具 ——"那一段替换成带版本号的
                            var idx = refreshed.FindIndex(l => l.StartsWith("—— 外部工具 ——"));
                            if (idx >= 0)
                            {
                                var tail = refreshed.Skip(idx + 1)
                                    .Where(l => !l.StartsWith("aria2 ") && !l.StartsWith("插件服务："))
                                    .ToList();
                                refreshed.RemoveRange(idx + 1, tail.Count);
                                refreshed.InsertRange(idx + 1, toolLines);
                            }
                            EnvironmentText = string.Join(Environment.NewLine, refreshed);
                        }
                        catch { /* 刷新失败就保留"检测中…" */ }
                    }));
                }
                catch { /* 读不到版本号不影响其它信息 */ }
            });
        }
        catch (Exception ex) { EnvironmentText = "读取环境信息失败：" + ex.Message; }
    }

    // ------------------------------------------------------------------ 检查更新
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        UpdateStatus = "正在检查更新…";
        IsUpdateAvailable = false;

        try
        {
            var result = await AppServices.Updates.CheckAsync();
            UpdateFeed = result.FeedUrl is null ? "" : $"更新源：{result.FeedUrl}";
            UpdateStatus = result.Message;

            if (result.Status == Core.Services.UpdateStatus.UpdateAvailable && result.Info is not null)
            {
                _info = result.Info;
                IsUpdateAvailable = true;
                NewVersionText = result.Info.VersionText;
                ReleaseNotes = result.Info.Notes ?? "（该版本没有提供更新说明）";
                App.Notify("发现新版本", result.Message, InfoSeverity.Success);
            }
            else if (result.Status == Core.Services.UpdateStatus.NotConfigured)
            {
                App.Notify("尚未配置更新源", result.Message, InfoSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新失败：" + ex.Message;
        }
        finally { IsChecking = false; }
    }

    /// <summary>供主窗口侧边栏的「检查更新」按钮调用。</summary>
    public void StartCheck() => _ = CheckAsync();

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_info is null) return;
        IsDownloading = true;
        DownloadProgress = 0;
        UpdateStatus = "正在下载新版本…";
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p * 100);
            _downloadedInstaller = await AppServices.Updates.DownloadUpdateAsync(_info, progress);
            UpdateStatus = $"已下载到：{_downloadedInstaller}";
            App.Notify("下载完成", "点「立即安装」启动安装程序，或到上面的目录手动运行。", InfoSeverity.Success);
        }
        catch (Exception ex)
        {
            UpdateStatus = "下载失败：" + ex.Message;
            App.Notify("下载失败", ex.Message, InfoSeverity.Error);
        }
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        try
        {
            if (_downloadedInstaller is null || !File.Exists(_downloadedInstaller))
            {
                App.Notify("还没有下载安装包", "请先点「下载新版本」", InfoSeverity.Warning);
                return;
            }
            UpdateService.LaunchInstaller(_downloadedInstaller);
            App.Notify("已启动安装程序", "安装完成后请手动重启本程序", InfoSeverity.Info);
        }
        catch (Exception ex) { App.Notify("启动安装程序失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _info?.PageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            App.Notify("没有发布页地址", "该更新源没有提供发布页面。", InfoSeverity.Warning);
            return;
        }
        OpenUrl(url);
    }

    [RelayCommand]
    private void SkipVersion()
    {
        if (_info is null) return;
        AppServices.Settings.Update(s => s.SkippedVersion = _info.VersionText);
        IsUpdateAvailable = false;
        UpdateStatus = $"已忽略版本 {_info.VersionText}，下次不会再提示。";
    }

    // ------------------------------------------------------------------ 开源与文档
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void OpenDocsFolder()
    {
        try
        {
            var root = AppPaths.FindProjectRoot();
            var dir = root is null ? null : Path.Combine(root, "docs");
            if (dir is null || !Directory.Exists(dir))
            {
                App.Notify("找不到文档目录", "文档位于工程根目录的 docs 文件夹内。", InfoSeverity.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void OpenSpecificDoc(string? fileName)
    {
        try
        {
            var root = AppPaths.FindProjectRoot();
            var path = root is null ? null : Path.Combine(root, "docs", fileName ?? "");
            if (path is null || !File.Exists(path))
            {
                App.Notify("找不到文档", $"{fileName}（应在 docs 目录下）", InfoSeverity.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void RefreshEnvironment() => BuildEnvironmentText();
}
