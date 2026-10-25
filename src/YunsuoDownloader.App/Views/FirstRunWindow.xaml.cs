using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.Views;

/// <summary>
/// 首次运行指引（需求 8）。
/// 4 步：欢迎 → 下载位置 → 性能 → 浏览器插件。所有值都会写进设置。
/// </summary>
public partial class FirstRunWindow : Wpf.Ui.Controls.FluentWindow
{
    private int _step = 1;
    private readonly List<System.Windows.Shapes.Ellipse> _dots;
    private readonly List<TextBlock> _labels;

    /// <summary>用户在引导里是否勾选了"开机自启"（主窗口据此写注册表）。</summary>
    public bool StartWithWindowsChosen { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();
        _dots = new List<System.Windows.Shapes.Ellipse> { Circle2, Circle3, Circle4 };
        _labels = new List<TextBlock> { Label2, Label3, Label4 };

        var s = AppServices.Initialized ? AppServices.Settings.Current : new AppSettings();
        DirectoryBox.Text = s.DownloadDirectory;
        DirectoryIntroText.Text =
            "默认放在你系统的「下载」文件夹里（" + AppSettings.DefaultDownloadDirectory + "）。" +
            "想换个位置可以点「浏览…」；以后随时能在「设置 → 下载位置」里改。";
        ApplyPerformanceDefaults(s);
        CreateSubfolderBox.IsChecked = s.CreateSubfolderPerTask;
        StartWithWindowsBox.IsChecked = s.StartWithWindows;

        try
        {
            RefreshExtensionPath();
        }
        catch (Exception ex)
        {
            Log.Warn("首次运行页定位插件目录失败：" + ex.Message);
        }

        TokenBox.Text = AppServices.Initialized ? AppServices.Settings.Current.MediaServerToken : "";
        UpdateServiceStatus();
        UpdateDirectoryHint();
        Render();
    }

    private void ApplyPerformanceDefaults(AppSettings s)
    {
        ConnectionsBox.Value = s.ConnectionsPerDownload;
        ConcurrentBox.Value = s.MaxConcurrentDownloads;
        FragmentsBox.Value = s.ConcurrentFragments;
        LimitBox.Value = s.GlobalSpeedLimitKbps;
    }

    /// <summary>NumberBox 的 Value 是可空 double，清空时为 null/NaN，这里统一兜底。</summary>
    private static double N(double? value, double fallback)
        => value is null || double.IsNaN(value.Value) ? fallback : value.Value;

    // ------------------------------------------------------------------ 步骤切换
    private void Render()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == 4 ? "完成，开始使用" : "下一步";
        SkipButton.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;

        var accent = (Brush?)TryFindResource("AccentFillColorDefaultBrush") ?? Brushes.DodgerBlue;
        var dim = (Brush?)TryFindResource("ControlFillColorSecondaryBrush") ?? Brushes.Gray;

        for (int i = 0; i < _dots.Count; i++)
        {
            var active = _step >= i + 2;
            _dots[i].Fill = active ? accent : dim;
            _labels[i].Opacity = active ? 1.0 : 0.6;
        }

        HintText.Text = _step switch
        {
            1 => "提示：本程序的所有设置都可以在「设置」页随时修改。",
            2 => "提示：下载大文件时建议选择空间充足、非系统盘的位置。",
            3 => "提示：连接数受目标服务器限制，8~16 通常是最佳区间。",
            _ => "提示：插件是可选的，装不装都不影响本程序下载链接。"
        };
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 1) { _step--; ApplyCurrentStep(); Render(); }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        ApplyCurrentStep();
        if (_step < 4) { _step++; Render(); return; }

        // 完成
        try
        {
            AppServices.Settings.Update(s =>
            {
                s.DownloadDirectory = DirectoryBox.Text.Trim();
                s.CreateSubfolderPerTask = CreateSubfolderBox.IsChecked == true;
                s.ConnectionsPerDownload = (int)Math.Clamp(N(ConnectionsBox.Value, 16), 1, 64);
                s.MaxConcurrentDownloads = (int)Math.Clamp(N(ConcurrentBox.Value, 3), 1, 32);
                s.ConcurrentFragments = (int)Math.Clamp(N(FragmentsBox.Value, 16), 1, 64);
                s.GlobalSpeedLimitKbps = (long)Math.Max(0, N(LimitBox.Value, 0));
                s.StartWithWindows = StartWithWindowsBox.IsChecked == true;
                s.FirstRunCompleted = true;
            });

            StartWithWindowsChosen = StartWithWindowsBox.IsChecked == true;
            try { Directory.CreateDirectory(DirectoryBox.Text.Trim()); } catch { }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存首次运行设置失败");
            MessageBox.Show("保存设置失败：" + ex.Message, "云梭下载器");
        }
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.Settings.Update(s =>
            {
                if (!string.IsNullOrWhiteSpace(DirectoryBox.Text)) s.DownloadDirectory = DirectoryBox.Text.Trim();
                s.FirstRunCompleted = true;
            });
        }
        catch { /* 忽略 */ }
        DialogResult = false;
        Close();
    }

    private void ApplyCurrentStep()
    {
        if (!AppServices.Initialized) return;
        switch (_step)
        {
            case 2:
                AppServices.Settings.Update(s =>
                {
                    if (!string.IsNullOrWhiteSpace(DirectoryBox.Text))
                        s.DownloadDirectory = DirectoryBox.Text.Trim();
                    s.CreateSubfolderPerTask = CreateSubfolderBox.IsChecked == true;
                });
                UpdateDirectoryHint();
                break;
            case 3:
                AppServices.Settings.Update(s =>
                {
                    s.ConnectionsPerDownload = (int)Math.Clamp(N(ConnectionsBox.Value, 16), 1, 64);
                    s.MaxConcurrentDownloads = (int)Math.Clamp(N(ConcurrentBox.Value, 3), 1, 32);
                    s.ConcurrentFragments = (int)Math.Clamp(N(FragmentsBox.Value, 16), 1, 64);
                    s.GlobalSpeedLimitKbps = (long)Math.Max(0, N(LimitBox.Value, 0));
                });
                break;
        }
    }

    // ------------------------------------------------------------------ 步骤 2
    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存位置",
            InitialDirectory = Directory.Exists(DirectoryBox.Text) ? DirectoryBox.Text : AppSettings.DefaultDownloadDirectory,
            Multiselect = false
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            DirectoryBox.Text = dlg.FolderName;
            UpdateDirectoryHint();
        }
    }

    private void OnResetDirectory(object sender, RoutedEventArgs e)
    {
        DirectoryBox.Text = AppSettings.DefaultDownloadDirectory;
        UpdateDirectoryHint();
    }

    private void UpdateDirectoryHint()
    {
        try
        {
            var dir = DirectoryBox.Text.Trim();
            var drive = Path.GetPathRoot(dir) ?? "?";
            DirectoryHint.Text = Directory.Exists(dir)
                ? $"目录已存在（{drive}）"
                : $"目录尚不存在，首次下载时会自动创建（{drive}）";
        }
        catch { DirectoryHint.Text = "路径格式无法识别"; }
    }

    // ------------------------------------------------------------------ 步骤 4
    /// <summary>
    /// 定位插件目录（exe 同目录 → 工程根目录 → 数据目录，都没有就释放程序内置的副本），
    /// 并在路径框下面显示这段目录的来源。
    /// </summary>
    private void RefreshExtensionPath()
    {
        try
        {
            var dir = ExtensionPackager.EnsureAvailable();
            ExtensionPathBox.Text = dir;
            ExtensionSourceText.Text = $"插件来源：{ExtensionPackager.DescribeSource(dir)}（程序内置了 {ExtensionPackager.EmbeddedFileCount} 个插件文件，丢了可以重新导出）";
        }
        catch (Exception ex)
        {
            ExtensionPathBox.Text = "(未找到插件目录)";
            ExtensionSourceText.Text = "点「导出插件到…」可以把程序内置的插件副本释放到你指定的文件夹。";
            Log.Warn("定位插件目录失败：" + ex.Message);
        }
    }

    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // 目录被删/没找到时会用内置副本重新释放一份
            var dir = ExtensionPackager.EnsureAvailable();
            ExtensionPathBox.Text = dir;
            ExtensionSourceText.Text = $"插件来源：{ExtensionPackager.DescribeSource(dir)}";
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("找不到插件目录，请点「导出插件到…」把内置的插件释放到你指定的文件夹。\n" + ex.Message,
                "云梭下载器");
        }
    }

    /// <summary>把程序内置的插件副本导出到用户选定的文件夹。</summary>
    private void OnExportExtension(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择导出插件的文件夹（建议新建一个空文件夹，插件文件会写进去）",
                Multiselect = false,
                InitialDirectory = Directory.Exists(ExtensionPathBox.Text) ? ExtensionPathBox.Text : null
            };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

            var count = ExtensionPackager.ExportTo(dlg.FolderName);
            if (count <= 0)
            {
                MessageBox.Show($"没能写入任何文件，请换一个可写的文件夹再试：\n{dlg.FolderName}", "云梭下载器");
                return;
            }

            try { Process.Start(new ProcessStartInfo(dlg.FolderName) { UseShellExecute = true }); }
            catch { /* 打不开资源管理器不算导出失败 */ }

            MessageBox.Show($"共导出 {count} 个文件到：\n{dlg.FolderName}\n\n在 chrome://extensions 里用「加载已解压的扩展程序」选这个文件夹即可。",
                "云梭下载器");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "云梭下载器");
        }
    }

    private void OnCopyToken(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TokenBox.Text);
            ServiceStatusText.Text = "令牌已复制到剪贴板，粘贴到插件设置里保存即可。";
        }
        catch (Exception ex) { ServiceStatusText.Text = "复制失败：" + ex.Message; }
    }

    private void UpdateServiceStatus()
    {
        if (!AppServices.Initialized) { ServiceStatusText.Text = ""; return; }
        var ext = AppServices.Extension;
        ServiceStatusText.Text = ext.IsRunning
            ? $"本地插件服务正在运行：ws://{AppServices.Settings.Current.MediaServerHost}:{AppServices.Settings.Current.MediaServerPort}/media"
            : "本地插件服务未启动（可以在「浏览器插件」页里启动）。";
    }
}
