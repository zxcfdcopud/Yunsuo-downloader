using System.Windows;
using System.Windows.Threading;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.App.Views;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;
using Wpf.Ui.Appearance;

namespace YunsuoDownloader.App;

public partial class App : Application
{
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    /// <summary>本进程启动时带进来的命令行参数（可能是链接）。</summary>
    public static string[] InitialArgs { get; private set; } = Array.Empty<string>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var args = SingleInstanceService.ParseArgs(e.Args);
        InitialArgs = args;

        // ---- 单实例：已有实例在跑就把参数转过去并退出
        try
        {
            if (!AppServices.SingleInstance.TryAcquire(args))
            {
                Shutdown();
                return;
            }
            AppServices.SingleInstance.ArgumentsReceived += OnExternalArguments;
        }
        catch (Exception ex)
        {
            Log.Warn($"单实例检查失败（忽略）：{ex.Message}");
        }

        // ---- 全局异常兜底，避免静默崩溃
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "UI 线程未处理异常");
            ex.Handled = true;
            ShowFatal(ex.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error(ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString()), "未处理异常");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "未观察的任务异常");
            ex.SetObserved();
        };

        try
        {
            await AppServices.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化失败");
            MessageBox.Show("初始化失败：" + ex.Message, "云梭下载器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ApplyTheme();

        _mainWindow = new MainWindow();
        _mainWindow.ForceClosed += (_, _) => Shutdown();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        _tray = new TrayIconService(_mainWindow);

        // --page=<downloads|newtask|pan|extension|settings|about>：启动后直接跳到指定页面（做快捷方式很好用）
        var startPage = ExtractOption(args, "--page");
        if (!string.IsNullOrWhiteSpace(startPage)) _mainWindow.NavigateTo(startPage!);

        // ---- 浏览器插件服务
        _ = AppServices.Extension.StartAsync();

        // ---- 首次运行指引（需求 8）
        var needsWizard = !AppServices.Settings.Current.FirstRunCompleted && string.IsNullOrWhiteSpace(startPage);
        Log.Info($"首次运行引导：需要显示={needsWizard}（firstRunCompleted={AppServices.Settings.Current.FirstRunCompleted}, " +
                 $"startPage='{startPage}'）");
        if (needsWizard)
        {
            // 不要在这里直接 ShowDialog：
            // 本方法是在 OnStartup 里 await 之后继续执行的，此时仍处在启动的同步上下文里，
            // 直接开模态循环会和主窗口的首次布局互相等待——现象就是窗口永远不出现、
            // 日志停在"开始 ShowDialog"（踩过：引导页完全不弹，用户以为程序没反应）。
            // 用 Dispatcher 以 Background 优先级排队，等 OnStartup 彻底返回、消息循环跑起来再弹。
            // （这里的 async lambda 故意不 await：BeginInvoke 返回后启动流程要继续走完，
            //   引导页是"延迟到空闲时再弹"的，不阻塞启动。）
#pragma warning disable CS4014
            Dispatcher.BeginInvoke(new Action(async () =>
#pragma warning restore CS4014
            {
                try
                {
                    var wizard = new FirstRunWindow { Owner = _mainWindow };
                    Log.Info("首次运行引导：窗口已创建，开始 ShowDialog");
                    var shown = wizard.ShowDialog();
                    Log.Info($"首次运行引导：已关闭（结果={shown}）");
                    AppServices.Settings.Update(s => s.FirstRunCompleted = true);
                    if (wizard.StartWithWindowsChosen) ApplyStartWithWindows(true);
                    await AppServices.Extension.RestartAsync();
                }
                catch (Exception ex)
                {
                    // 引导页出错绝不能拖死整个程序：记下来、标记已完成、继续正常启动
                    Log.Error(ex, "首次运行引导显示失败");
                    AppServices.Settings.Update(s => s.FirstRunCompleted = true);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---- 命令行带入的链接
        if (args.Length > 0) _ = HandleArgumentsAsync(args);

        // ---- 检查更新（需求 10）：每天第一次启动检查一次，同一天再开就不打扰 ----
        var updateSettings = AppServices.Settings.Current;
        var checkedToday = updateSettings.LastUpdateCheck?.Date == DateTime.Today;
        if (updateSettings.CheckUpdatesOnStartup && !checkedToday)
        {
            Log.Info($"今天还没检查过更新（上次：{updateSettings.LastUpdateCheck?.ToString("yyyy-MM-dd HH:mm") ?? "从未"}），开始检查");
            _ = CheckUpdatesSilentlyAsync();
        }
        else if (!updateSettings.CheckUpdatesOnStartup)
        {
            Notify("提示", "提示：可在「设置 → 更新」中开启每天自动检查更新", Models.InfoSeverity.Info);
        }

        Log.Info($"启动完成，工具状态：{Environment.NewLine}{AppServices.Tools.Summary()}");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try
        {
            if (AppServices.Initialized) AppServices.History.SaveNow();
        }
        catch { }
        try { await AppServices.ShutdownAsync(); } catch { }
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ 主题
    public static void ApplyTheme()
    {
        try
        {
            var theme = AppServices.Initialized ? AppServices.Settings.Current.Theme : "System";
            switch (theme)
            {
                case "Light": ApplicationThemeManager.Apply(ApplicationTheme.Light); break;
                case "Dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark); break;
                default: ApplicationThemeManager.ApplySystemTheme(); break;
            }
        }
        catch (Exception ex) { Log.Warn($"应用主题失败：{ex.Message}"); }
    }

    // ------------------------------------------------------------------ 界面通知
    /// <summary>把一条消息推给主窗口的信息栏 / 托盘气泡。</summary>
    public static void Notify(string title, string message, Models.InfoSeverity severity = Models.InfoSeverity.Info)
    {
        try
        {
            Current?.Dispatcher.Invoke(() =>
            {
                if (Current.MainWindow is MainWindow mw) mw.ShowInfo(title, message, severity);
            });
        }
        catch { /* 忽略 */ }
    }

    private static void ShowFatal(string message)
        => MessageBox.Show("程序遇到一个问题：\n\n" + message + "\n\n日志目录：" + AppPaths.LogDirectory,
            "云梭下载器", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ------------------------------------------------------------------ 外部参数 / 命令行
    private void OnExternalArguments(object? sender, string[] args)
        => Dispatcher.Invoke(() =>
        {
            _tray?.ShowWindow();
            _ = HandleArgumentsAsync(args);
        });

    public static async Task HandleArgumentsAsync(string[] args)
    {
        foreach (var raw in args)
        {
            var arg = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith('-')) continue;   // 预留开关

            if (!LooksLikeUrl(arg) && !File.Exists(arg)) continue;

            try
            {
                if (File.Exists(arg) && arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    await AppServices.Downloads.AddAsync(new AddDownloadRequest
                    {
                        Url = arg,
                        Kind = DownloadKind.Torrent,
                        Title = Path.GetFileNameWithoutExtension(arg)
                    });
                }
                else
                {
                    await AppServices.Downloads.AddAsync(new AddDownloadRequest { Url = arg });
                }
                Notify("已添加下载", arg, Models.InfoSeverity.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理命令行参数失败");
                Notify("添加失败", ex.Message, Models.InfoSeverity.Error);
            }
        }
    }

    private static bool LooksLikeUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase);

    /// <summary>支持 --name=value 与 --name value 两种写法。</summary>
    private static string? ExtractOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..].Trim('"');
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"');
        }
        return null;
    }

    // ------------------------------------------------------------------ 静默检查更新
    private static async Task CheckUpdatesSilentlyAsync()
    {
        try
        {
            var result = await AppServices.Updates.CheckAsync();
            if (result.Status == UpdateStatus.UpdateAvailable && result.Info is not null)
            {
                if (AppServices.Settings.Current.SkippedVersion == result.Info.VersionText) return;
                Notify("发现新版本", result.Message + "，请到「关于」页面查看详情", Models.InfoSeverity.Info);
            }
            else if (result.Status == UpdateStatus.Failed)
            {
                Log.Warn("启动时检查更新失败：" + result.Message);
            }
        }
        catch (Exception ex) { Log.Warn($"检查更新异常：{ex.Message}"); }
    }

    // ------------------------------------------------------------------ 开机自启
    public static void ApplyStartWithWindows(bool enable)
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("YunsuoDownloader", $"\"{exe}\" --tray");
            }
            else
            {
                if (key.GetValue("YunsuoDownloader") is not null) key.DeleteValue("YunsuoDownloader", false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"设置开机自启失败：{ex.Message}");
        }
    }
}
