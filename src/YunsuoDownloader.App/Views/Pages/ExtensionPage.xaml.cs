using System.Windows.Controls;
using YunsuoDownloader.App.ViewModels;

namespace YunsuoDownloader.App.Views.Pages;

public partial class ExtensionPage : UserControl
{
    public ExtensionPage()
    {
        InitializeComponent();
        DataContext = new ExtensionViewModel();
    }
}
