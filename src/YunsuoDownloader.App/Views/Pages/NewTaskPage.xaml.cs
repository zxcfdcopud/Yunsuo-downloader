using System.Windows;
using System.Windows.Controls;
using YunsuoDownloader.App.ViewModels;

namespace YunsuoDownloader.App.Views.Pages;

public partial class NewTaskPage : UserControl
{
    private readonly NewTaskViewModel _vm;

    public NewTaskPage()
    {
        InitializeComponent();
        _vm = new NewTaskViewModel();
        DataContext = _vm;
    }

    /// <summary>供主窗口"把链接填进新建任务页"使用。</summary>
    public void Prefill(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _vm.UrlText = string.IsNullOrWhiteSpace(_vm.UrlText) ? url : _vm.UrlText + Environment.NewLine + url;
        UrlBox.Focus();
        _ = _vm.ParseCommand.ExecuteAsync(null);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var parts = new List<string>();
        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            e.Data.GetData(DataFormats.UnicodeText) is string text)
            parts.Add(text);
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
            parts.AddRange(files);

        _vm.AcceptDroppedText(string.Join(Environment.NewLine, parts));
    }
}
