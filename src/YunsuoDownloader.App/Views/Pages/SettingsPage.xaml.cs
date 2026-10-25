using System.Diagnostics;
using System.Windows.Controls;
using YunsuoDownloader.App.ViewModels;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        // 本页有 234 个控件（8 个 NumberBox），首次构造要 1.8 秒左右。
        // 拆开计时，方便以后判断变慢是 XAML 还是 ViewModel 的锅。
        var sw = Stopwatch.StartNew();
        InitializeComponent();
        var xamlMs = sw.ElapsedMilliseconds;

        sw.Restart();
        DataContext = new SettingsViewModel();
        var vmMs = sw.ElapsedMilliseconds;

        Log.Info($"[设置页] 构造完成：XAML {xamlMs} ms + ViewModel {vmMs} ms = {xamlMs + vmMs} ms");
    }
}
