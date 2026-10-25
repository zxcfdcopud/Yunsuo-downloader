using System.Drawing;
using System.Windows;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;
using WinForms = System.Windows.Forms;

namespace YunsuoDownloader.App.Services;

/// <summary>托盘图标：最小化到托盘、快速显示窗口、暂停/继续全部任务、退出。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Window _window;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _resumeItem;

    public TrayIconService(Window window)
    {
        _window = window;

        _statusItem = new WinForms.ToolStripMenuItem("状态：空闲") { Enabled = false };
        _pauseItem = new WinForms.ToolStripMenuItem("暂停全部");
        _resumeItem = new WinForms.ToolStripMenuItem("继续全部");
        var showItem = new WinForms.ToolStripMenuItem("显示主窗口");
        var exitItem = new WinForms.ToolStripMenuItem("退出");

        showItem.Click += (_, _) => ShowWindow();
        _pauseItem.Click += async (_, _) => await PauseAllAsync().ConfigureAwait(false);
        _resumeItem.Click += async (_, _) => await ResumeAllAsync().ConfigureAwait(false);
        exitItem.Click += (_, _) => ExitApp();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "云梭下载器",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        if (AppServices.Initialized)
        {
            AppServices.Downloads.StatsChanged += (_, _) => UpdateStatus();
            AppServices.Downloads.ItemCompleted += (_, item) => ShowBalloon("下载完成", item.DisplayName);
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1500).ConfigureAwait(false);
                try { UpdateStatus(); } catch { }
            }
        });
    }

    private static Icon LoadIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch { /* 忽略 */ }
        return SystemIcons.Application;
    }

    public void ShowWindow()
    {
        try
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
                _window.Topmost = true;
                _window.Topmost = false;
                _window.Focus();
            });
        }
        catch (Exception ex) { Log.Warn($"显示主窗口失败：{ex.Message}"); }
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = Format.Truncate(message, 120);
            _icon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(3000);
        }
        catch { /* 忽略 */ }
    }

    private void UpdateStatus()
    {
        if (!AppServices.Initialized) return;
        var mgr = AppServices.Downloads;
        var active = mgr.ActiveCount;
        var speed = mgr.TotalSpeed;

        var text = active > 0
            ? $"云梭下载器 — {active} 个任务，{Format.Speed(speed)}"
            : "云梭下载器 — 空闲";

        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { }
        _statusItem.Text = active > 0
            ? $"下载中 {active} 个 · {Format.Speed(speed)}"
            : "状态：空闲";

        var window = _window as Views.MainWindow;
        window?.UpdateTaskbarProgress(mgr.ActiveCount > 0
            ? mgr.ActiveItems.Sum(i => i.Progress) / Math.Max(1, mgr.ActiveCount)
            : 0);
    }

    private static async Task PauseAllAsync()
    {
        foreach (var item in AppServices.Downloads.ActiveItems.ToArray())
            await AppServices.Downloads.PauseAsync(item.Id).ConfigureAwait(false);
    }

    private static async Task ResumeAllAsync()
    {
        foreach (var item in AppServices.History.Items
                     .Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed)
                     .ToArray())
            await AppServices.Downloads.ResumeAsync(item.Id).ConfigureAwait(false);
    }

    private static void ExitApp()
        => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (System.Windows.Application.Current.MainWindow is Views.MainWindow mw) mw.ForceClose();
            System.Windows.Application.Current.Shutdown();
        });

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { /* 忽略 */ }
    }
}
