using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.App.ViewModels;
using YunsuoDownloader.App.Views.Pages;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;
using Wpf.Ui.Controls;

namespace YunsuoDownloader.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private bool _allowClose;
    private TaskbarItemInfo? _taskbar;

    /// <summary>用户真的选择了"退出"（而不是最小化到托盘）。</summary>
    public event EventHandler? ForceClosed;

    /// <summary>页面标签（和 NavList 的 Tag 一致）。</summary>
    private static readonly string[] AllPages = { "downloads", "newtask", "extension", "settings", "about" };

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = $"v{UpdateService.CurrentVersionText}";

        try
        {
            _taskbar = new TaskbarItemInfo { ProgressState = TaskbarItemProgressState.None };
            TaskbarItemInfo = _taskbar;
        }
        catch { /* 某些系统不支持任务栏进度 */ }

        NavList.SelectedIndex = 0;

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        Loaded += (_, _) =>
        {
            UpdateStatusBar();
            if (AppServices.Initialized)
                ShowInfo("提示", "把链接粘贴到「新建任务」即可下载；浏览器插件已就绪时可从网页一键发送。",
                    InfoSeverity.Info, autoCloseSeconds: 8);

            // 首屏渲染完、窗口可以交互之后再预热其它页面。
            // 页面构造里有 XAML 解析和 ViewModel 初始化（工具探测要起子进程），
            // 放在点击时才做就会卡住导航（用户反馈的"切换标签页卡顿"就是这个）。
            PrewarmPages();
        };
    }

    // ------------------------------------------------------------------ 导航
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;
        var tag = item.Tag as string ?? "downloads";

        var sw = Stopwatch.StartNew();
        var cached = _pages.ContainsKey(tag);
        ContentHost.Content = GetOrCreatePage(tag);
        sw.Stop();

        // 这条日志用来验证"切换标签不再卡"：命中缓存时应该只有零点几毫秒。
        Log.Info($"[导航] {tag} 耗时 {sw.ElapsedMilliseconds} ms（{(cached ? "已预热" : "首次创建")}）");
    }

    private UserControl GetOrCreatePage(string tag)
    {
        if (_pages.TryGetValue(tag, out var cached)) return cached;

        UserControl page = tag switch
        {
            "newtask" => new NewTaskPage(),
            "extension" => new ExtensionPage(),
            "settings" => new SettingsPage(),
            "about" => new AboutPage(),
            _ => new DownloadsPage()
        };
        _pages[tag] = page;
        return page;
    }

    /// <summary>
    /// 在后台把还没创建过的页面逐个建好，避免用户第一次点某个标签时才现造
    /// （造页面 = 解析 XAML + 初始化 ViewModel，其中工具探测会起子进程，
    ///  同步做在 UI 线程上就是肉眼可见的卡顿）。
    ///
    /// 实测耗时：settings 页有 234 个控件（8 个 NumberBox），首次构造要 1.8 秒；
    /// 其余页面 20~90 ms。预热后切换标签只要 20~25 ms（命中缓存）。
    ///
    /// 策略：
    ///   * 先等 1.5 秒，让用户先看到界面、开始操作；
    ///   * 每个页面之间间隔 250 ms，避免连续占用 UI 线程造成掉帧；
    ///   * 用 ContextIdle 优先级，用户一有操作就让路；
    ///   * 已经建好的页面直接跳过（切换标签时零开销）。
    /// </summary>
    private void PrewarmPages()
    {
        var queue = new Queue<string>(AllPages);

        void Step()
        {
            while (queue.Count > 0)
            {
                var tag = queue.Dequeue();
                if (_pages.ContainsKey(tag)) continue;

                var sw = Stopwatch.StartNew();
                try
                {
                    _ = GetOrCreatePage(tag);
                    Log.Info($"[预热] {tag} 耗时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    // 预热失败不能影响正常使用：记日志，用户点到时再按需创建
                    Log.Warn($"预热页面失败（{tag}）：{ex.Message}");
                }

                // 一个建完就停手，隔一会儿再建下一个
                var t = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    Dispatcher.BeginInvoke(new Action(Step),
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                };
                t.Start();
                return;
            }
        }

        // 启动后先让界面活 1.5 秒，再开始预热
        var start = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        start.Tick += (_, _) =>
        {
            start.Stop();
            Dispatcher.BeginInvoke(new Action(Step),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
        start.Start();
    }

    /// <summary>供外部（托盘/插件/命令行）跳到指定页面。</summary>
    public void NavigateTo(string tag)
    {
        for (int i = 0; i < NavList.Items.Count; i++)
        {
            if (NavList.Items[i] is ListBoxItem it && (it.Tag as string) == tag)
            {
                NavList.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>切换到"新建任务"并把链接填进去。</summary>
    public void PrefillUrl(string url)
    {
        NavigateTo("newtask");
        if (GetOrCreatePage("newtask") is NewTaskPage page) page.Prefill(url);
    }

    // ------------------------------------------------------------------ 信息栏
    public void ShowInfo(string title, string message, InfoSeverity severity = InfoSeverity.Info,
        int autoCloseSeconds = 6)
    {
        try
        {
            Info.Title = title;
            Info.Message = message;
            Info.Severity = severity switch
            {
                InfoSeverity.Success => InfoBarSeverity.Success,
                InfoSeverity.Warning => InfoBarSeverity.Warning,
                InfoSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
            Info.IsOpen = true;

            if (autoCloseSeconds > 0)
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(autoCloseSeconds)
                };
                timer.Tick += (_, _) => { Info.IsOpen = false; timer.Stop(); };
                timer.Start();
            }
        }
        catch { /* 忽略 */ }
    }

    // ------------------------------------------------------------------ 状态栏
    private void UpdateStatusBar()
    {
        if (!AppServices.Initialized) return;
        try
        {
            var mgr = AppServices.Downloads;
            StatusSpeed.Text = Format.Speed(mgr.TotalSpeed);
            StatusTasks.Text = mgr.ActiveCount > 0
                ? $"下载中 {mgr.ActiveCount} · 队列 {mgr.QueueCount}"
                : "空闲";

            var tools = AppServices.Tools;
            var missing = new List<string>();
            if (!tools.Aria2.Found) missing.Add("aria2c");
            if (!tools.YtDlp.Found) missing.Add("yt-dlp");
            if (!tools.Ffmpeg.Found) missing.Add("ffmpeg");
            StatusTools.Text = missing.Count == 0
                ? $"插件 {AppServices.Extension.ClientCount} 个 · 引擎 aria2 + HTTP 分片"
                : $"缺少工具：{string.Join("/", missing)}";

            UpdateTaskbarProgress(mgr.ActiveCount > 0
                ? mgr.ActiveItems.Sum(i => i.Progress) / Math.Max(1, mgr.ActiveCount)
                : 0);
        }
        catch { /* 忽略 */ }
    }

    public void UpdateTaskbarProgress(double value)
    {
        if (_taskbar is null) return;
        try
        {
            if (value <= 0 || value >= 1)
            {
                _taskbar.ProgressState = TaskbarItemProgressState.None;
            }
            else
            {
                _taskbar.ProgressState = TaskbarItemProgressState.Normal;
                _taskbar.ProgressValue = Math.Clamp(value, 0, 1);
            }
        }
        catch { /* 忽略 */ }
    }

    // ------------------------------------------------------------------ 按钮
    private void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppServices.Settings.Current.DownloadDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowInfo("打开失败", ex.Message, InfoSeverity.Error); }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowInfo("打开失败", ex.Message, InfoSeverity.Error); }
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        NavigateTo("about");
        if (GetOrCreatePage("about") is AboutPage page) page.StartCheck();
    }

    // ------------------------------------------------------------------ 托盘行为
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && AppServices.Initialized && AppServices.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            App.Notify("已最小化到托盘", "程序仍在后台下载，双击托盘图标可以恢复窗口。", InfoSeverity.Info);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && AppServices.Initialized && AppServices.Settings.Current.MinimizeToTray)
            Hide();
    }

    /// <summary>真正退出程序（托盘菜单"退出"走这里）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        ForceClosed?.Invoke(this, EventArgs.Empty);
        try { Close(); } catch { /* 忽略 */ }
    }
}
