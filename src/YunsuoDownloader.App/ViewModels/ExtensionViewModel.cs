using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>「浏览器插件」页：服务状态、令牌、安装指引、支持站点。</summary>
public sealed partial class ExtensionViewModel : ObservableObject
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "未启动";
    [ObservableProperty] private string _statusDetail = string.Empty;
    [ObservableProperty] private int _port = 16888;
    [ObservableProperty] private int _clientCount;
    [ObservableProperty] private string _clientInfo = "还没有浏览器插件连接过来。";
    [ObservableProperty] private string _token = string.Empty;
    [ObservableProperty] private string _extensionPath = string.Empty;
    [ObservableProperty] private bool _extensionPathExists;
    [ObservableProperty] private string _lastReceived = "—";

    /// <summary>插件目录的来源说明（安装目录 / 开发工程目录 / 程序释放的副本…）。</summary>
    [ObservableProperty] private string _extensionPathSource = string.Empty;

    /// <summary>给用户看的一行提示：插件是内置副本，丢了可以一键导出。</summary>
    [ObservableProperty] private string _extensionHint = string.Empty;

    public ObservableCollection<string> SupportedSites { get; } = new()
    {
        "哔哩哔哩（B站）", "YouTube", "抖音", "快手", "小红书",
        "微博 / 优酷 / 爱奇艺 / 腾讯视频", "X(Twitter) / Instagram / TikTok",
        "以及任意网站的 <video>、m3u8(HLS)、mpd(DASH)、mp4/flv/mkv 资源"
    };

    public ObservableCollection<string> InstallSteps { get; } = new()
    {
        "打开本页下方的「打开插件目录」；如果那个目录不存在，点「导出插件到…」把程序内置的插件副本释放到你指定的文件夹。",
        "在 Chrome / Edge 地址栏输入 chrome://extensions （Edge 为 edge://extensions）并回车。",
        "打开右上角的「开发者模式」开关。",
        "点击「加载已解压的扩展程序」，选中上面那个插件文件夹。",
        "装好后列表里会出现一张卡片：「视频嗅探下载器助手」。",
        "点浏览器工具栏上的插件图标打开它的界面，把本页的「连接令牌」填进插件设置里保存；" +
        "卡片上的「服务工作进程(Service Worker)」显示为运行中，就说明连接成功。"
    };

    public ExtensionViewModel()
    {
        Refresh();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // 定位插件目录可能要"释放内置副本"（写盘）或读目录内容，
        // 放在构造函数里会拖慢首次切到本页。先给个占位，后台算完再回填。
        ExtensionPath = "正在定位插件目录…";
        _ = Task.Run(() =>
        {
            try
            {
                var dir = ExtensionPackager.EnsureAvailable();
                var exists = Directory.Exists(dir);
                var source = ExtensionPackager.DescribeSource(dir);
                var count = ExtensionPackager.EmbeddedFileCount;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ExtensionPath = dir;
                    ExtensionPathExists = exists;
                    ExtensionPathSource = source;
                    ExtensionHint = $"这个文件夹就是插件的安装位置（程序内置了 {count} 个插件文件）。" +
                                    "如果它被误删或想换台机器用，点「导出插件到…」会重新导出一份完整的。";
                }));
            }
            catch (Exception ex)
            {
                Log.Warn("定位插件目录失败：" + ex.Message);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ExtensionPath = "(未找到插件目录)";
                    ExtensionPathExists = false;
                    ExtensionPathSource = "未找到";
                    ExtensionHint = "点「导出插件到…」可以把程序内置的插件副本释放到你指定的文件夹。";
                }));
            }
        });
    }

    /// <summary>
    /// 定位插件目录：exe 同目录 → 工程根目录 → 数据目录；都没有就把内置副本释放到 %AppData%。
    /// 插件被内置进 YunsuoDownloader.Core.dll，因此即使只拷走一个 exe 也不会丢。
    /// </summary>
    private void RefreshExtensionPath()
    {
        try
        {
            var dir = ExtensionPackager.EnsureAvailable();
            ExtensionPath = dir;
            ExtensionPathExists = Directory.Exists(dir);
            ExtensionPathSource = ExtensionPackager.DescribeSource(dir);
            ExtensionHint = $"这个文件夹就是插件的安装位置（程序内置了 {ExtensionPackager.EmbeddedFileCount} 个插件文件）。" +
                            "如果它被误删或想换台机器用，点「导出插件到…」会重新导出一份完整的。";
        }
        catch (Exception ex)
        {
            ExtensionPath = "(未找到插件目录)";
            ExtensionPathExists = false;
            ExtensionPathSource = "未找到";
            ExtensionHint = "点「导出插件到…」可以把程序内置的插件副本释放到你指定的文件夹。";
            Log.Warn("定位插件目录失败：" + ex.Message);
        }
    }

    public void Refresh()
    {
        if (!AppServices.Initialized) return;
        var ext = AppServices.Extension;
        var s = AppServices.Settings.Current;

        IsRunning = ext.IsRunning;
        Port = s.MediaServerPort;
        ClientCount = ext.ClientCount;
        Token = s.MediaServerToken;

        StatusText = IsRunning ? "运行中" : "未启动";
        StatusDetail = IsRunning
            ? $"ws://{s.MediaServerHost}:{s.MediaServerPort}/media"
            : (ext.LastError ?? "服务未启动，可在「设置 → 浏览器插件」里重新启动。");

        ClientInfo = string.IsNullOrWhiteSpace(ext.LastClientInfo)
            ? "还没有浏览器插件连接过来。"
            : ext.LastClientInfo!;

        LastReceived = ext.LastReceivedTitle ?? "—";

        if (ext.AuthFailureCount > 0)
            StatusDetail += $"（令牌校验失败 {ext.AuthFailureCount} 次）";
    }

    [RelayCommand]
    private void CopyToken()
    {
        try
        {
            System.Windows.Clipboard.SetText(Token);
            App.Notify("已复制令牌", "粘贴到浏览器插件设置里即可连接", InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void CopyAddress()
    {
        try
        {
            System.Windows.Clipboard.SetText($"ws://{AppServices.Settings.Current.MediaServerHost}:{Port}/media");
            App.Notify("已复制地址", "插件默认就是这个地址，一般无需修改", InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private async Task RegenerateTokenAsync()
    {
        if (Views.MessageBoxWindow.Confirm(
                "重新生成令牌后，已经连接过的浏览器插件需要重新填写新令牌才能继续使用。继续吗？",
                "重新生成令牌", "重新生成", "取消") is not true) return;

        AppServices.Settings.Update(s => s.MediaServerToken = SecretProtector.NewToken());
        await AppServices.Extension.RestartAsync();
        Refresh();
        App.Notify("已重新生成令牌", "请到插件设置里更新令牌", InfoSeverity.Success);
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        var ok = await AppServices.Extension.RestartAsync();
        Refresh();
        App.Notify(ok ? "插件服务已启动" : "插件服务启动失败", StatusDetail,
            ok ? InfoSeverity.Success : InfoSeverity.Error);
    }

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        try
        {
            // 目录被删/没找到时会用内置副本重新释放一份
            var dir = ExtensionPackager.EnsureAvailable();
            ExtensionPath = dir;
            ExtensionPathExists = Directory.Exists(dir);
            ExtensionPathSource = ExtensionPackager.DescribeSource(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Notify("找不到插件目录",
                "请点「导出插件到…」把程序内置的插件副本释放到你指定的文件夹。\n" + ex.Message,
                InfoSeverity.Warning);
        }
    }

    /// <summary>把内置的插件副本导出到用户选定的文件夹（插件丢了/换机器就靠它）。</summary>
    [RelayCommand]
    private void ExportExtension()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择导出插件的文件夹（建议新建一个空文件夹，插件文件会写进去）",
                Multiselect = false,
                InitialDirectory = Directory.Exists(ExtensionPath) ? ExtensionPath : null
            };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

            var target = dlg.FolderName;
            var count = ExtensionPackager.ExportTo(target);
            if (count <= 0)
            {
                App.Notify("导出失败", $"没能写入任何文件，请换一个可写的文件夹再试：{target}", InfoSeverity.Error);
                return;
            }

            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { /* 打不开资源管理器不算导出失败 */ }

            App.Notify("已导出浏览器插件", $"共导出 {count} 个文件到：{target}\n在 chrome://extensions 里用「加载已解压的扩展程序」选这个文件夹即可。",
                InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("导出失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void CopyExtensionPath()
    {
        try
        {
            System.Windows.Clipboard.SetText(ExtensionPath);
            App.Notify("已复制路径", ExtensionPath, InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void OpenExtensionsPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("chrome://extensions") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("msedge://extensions") { UseShellExecute = true }); }
            catch (Exception ex) { App.Notify("无法自动打开", "请手动在浏览器地址栏输入 chrome://extensions\n" + ex.Message, InfoSeverity.Warning); }
        }
    }
}
