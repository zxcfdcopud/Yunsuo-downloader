using System.Windows;
using System.Windows.Controls;
using YunsuoDownloader.App.ViewModels;

namespace YunsuoDownloader.App.Views.Pages;

/// <summary>
/// 「网盘解析」页。
///
/// 交互链路：粘链接 → 解析（列根目录）→ 点文件夹进去 → 勾选文件 → 下载选中。
/// 勾选状态在 <see cref="PanFileVm"/> 上，勾选数量由 code-behind 通知 ViewModel 刷新文案。
/// </summary>
public partial class PanPage : UserControl
{
    private readonly PanViewModel _viewModel;

    public PanPage()
    {
        InitializeComponent();
        _viewModel = new PanViewModel();
        DataContext = _viewModel;
    }

    private void OnFileCheckClick(object sender, RoutedEventArgs e)
    {
        // CheckBox 的 IsChecked 通过 Binding 写回 PanFileVm，这里只负责更新"已选择 N 个文件"
        _viewModel.NotifySelectionChanged();
    }

    /// <summary>从别的页面（例如新建任务页）粘了网盘链接时，直接带过来并开始解析。</summary>
    public void Prefill(string url)
    {
        _viewModel.LinkText = url;
        if (_viewModel.ParseCommand.CanExecute(null))
            _viewModel.ParseCommand.Execute(null);
    }
}
