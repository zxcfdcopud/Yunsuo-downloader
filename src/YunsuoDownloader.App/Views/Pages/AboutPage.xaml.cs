using System.Windows.Controls;
using YunsuoDownloader.App.ViewModels;

namespace YunsuoDownloader.App.Views.Pages;

public partial class AboutPage : UserControl
{
    private readonly AboutViewModel _vm;

    public AboutPage()
    {
        InitializeComponent();
        _vm = new AboutViewModel();
        DataContext = _vm;
    }

    /// <summary>供主窗口侧边栏的「检查更新」按钮调用。</summary>
    public void StartCheck() => _vm.StartCheck();
}
