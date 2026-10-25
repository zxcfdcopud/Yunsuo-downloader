using System.Windows;
using System.Windows.Controls;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.App.ViewModels;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;

namespace YunsuoDownloader.App.Views.Pages;

public partial class DownloadsPage : UserControl
{
    private readonly DownloadsViewModel _vm;

    public DownloadsPage()
    {
        InitializeComponent();
        _vm = new DownloadsViewModel();
        DataContext = _vm;
        Unloaded += (_, _) => _vm.Dispose();
    }

    // ------------------------------------------------------------------ 拖拽链接进来就能下载
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            var urls = new List<string>();

            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrWhiteSpace(text))
                    urls.AddRange(text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                urls.AddRange(files);
            }

            urls = urls.Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                   || u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                                   || u.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase)
                                   || u.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)
                                   || u.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                       .Distinct()
                       .ToList();

            if (urls.Count == 0) return;

            int ok = 0;
            foreach (var url in urls)
            {
                var item = await AppServices.Downloads.AddAsync(new AddDownloadRequest { Url = url });
                if (item.Status != DownloadStatus.Failed) ok++;
            }
            App.Notify("已添加下载", $"从拖拽内容里添加了 {ok} 个任务", InfoSeverity.Success);
        }
        catch (Exception ex)
        {
            App.Notify("拖拽添加失败", ex.Message, InfoSeverity.Error);
        }
    }
}
