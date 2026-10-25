using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Models;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>
/// 首页「最近下载」（需求 6）。
/// 列表直接绑定 DownloadItem（它自己实现 INotifyPropertyChanged），
/// 进度/速度每秒刷新一次，下载时间在列表里直接可见。
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DownloadFilter _filter = DownloadFilter.All;
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private string _totalSpeedText = "0 B/s";
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private string _summaryText = "暂无下载任务";
    [ObservableProperty] private bool _isEmpty = true;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public ICollectionView ItemsView { get; }

    public DownloadsViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        if (AppServices.Initialized)
        {
            AppServices.History.Changed += (_, _) => ReloadFromHistory();
            ReloadFromHistory();
        }

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();
    }

    // ------------------------------------------------------------------ 列表同步
    private void ReloadFromHistory()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            if (!AppServices.Initialized) return;
            var snapshot = AppServices.History.Items;

            // 增量同步，避免整表重建造成滚动位置丢失
            var existing = Items.Select(i => i.Id).ToHashSet();
            var incoming = snapshot.Select(i => i.Id).ToHashSet();

            for (int i = Items.Count - 1; i >= 0; i--)
                if (!incoming.Contains(Items[i].Id)) Items.RemoveAt(i);

            for (int i = 0; i < snapshot.Count; i++)
            {
                var item = snapshot[i];
                var idx = IndexOf(item.Id);
                if (idx < 0) Items.Insert(Math.Min(i, Items.Count), item);
                else if (idx != i)
                {
                    Items.Move(idx, Math.Min(i, Items.Count - 1));
                }
            }
            RefreshStats();
        });
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Id == id) return i;
        return -1;
    }

    private void RefreshStats()
    {
        if (!AppServices.Initialized) return;
        var mgr = AppServices.Downloads;
        ActiveCount = mgr.ActiveCount;
        QueuedCount = mgr.QueueCount;
        TotalSpeedText = Format.Speed(mgr.TotalSpeed);

        SummaryText = mgr.ActiveCount > 0
            ? $"正在下载 {mgr.ActiveCount} 个任务 · 队列 {mgr.QueueCount} 个"
            : Items.Count == 0 ? "暂无下载任务" : $"共 {Items.Count} 条历史记录";

        IsEmpty = Items.Count == 0;
        ItemsView.Refresh();
    }

    // ------------------------------------------------------------------ 筛选
    private bool FilterItem(object obj)
    {
        if (obj is not DownloadItem item) return false;

        bool statusOk = Filter switch
        {
            DownloadFilter.Active => item.IsActive || item.Status == DownloadStatus.Paused,
            DownloadFilter.Completed => item.Status == DownloadStatus.Completed,
            DownloadFilter.Failed => item.IsFailed,
            _ => true
        };
        if (!statusOk) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return Contains(item.DisplayName, q) || Contains(item.Title, q) || Contains(item.Url, q)
               || Contains(item.Site, q) || Contains(item.StatusText, q);
    }

    private static bool Contains(string? source, string q)
        => !string.IsNullOrEmpty(source) && source.Contains(q, StringComparison.OrdinalIgnoreCase);

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnFilterChanged(DownloadFilter value) => ItemsView.Refresh();

    /// <summary>与界面上筛选下拉框的索引一一对应：全部/下载中/已完成/失败。</summary>
    partial void OnFilterIndexChanged(int value) => Filter = (DownloadFilter)Math.Clamp(value, 0, 3);

    // ------------------------------------------------------------------ 命令
    [RelayCommand]
    private async Task PauseAsync(DownloadItem? item)
    {
        if (item is null) return;
        await AppServices.Downloads.PauseAsync(item.Id);
    }

    [RelayCommand]
    private async Task ResumeAsync(DownloadItem? item)
    {
        if (item is null) return;
        await AppServices.Downloads.ResumeAsync(item.Id);
    }

    [RelayCommand]
    private async Task RetryAsync(DownloadItem? item)
    {
        if (item is null) return;
        await AppServices.Downloads.RetryAsync(item.Id);
    }

    [RelayCommand]
    private async Task CancelAsync(DownloadItem? item)
    {
        if (item is null) return;
        var ok = Views.MessageBoxWindow.Confirm(
            $"确定要取消「{Format.Truncate(item.DisplayName, 60)}」吗？",
            "取消下载", "取消任务并删除已下载的部分", "仅取消，保留文件");
        if (ok is null) return;
        await AppServices.Downloads.CancelAsync(item.Id, deleteFile: ok.Value);
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var item in AppServices.Downloads.ActiveItems.ToArray())
            await AppServices.Downloads.PauseAsync(item.Id);
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        foreach (var item in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed).ToArray())
            await AppServices.Downloads.ResumeAsync(item.Id);
    }

    /// <summary>
    /// 从列表移除任务（对应每行右侧的垃圾桶按钮）。
    /// 注意：方法名不能带 Async 后缀之外的坑——MVVM Toolkit 只对返回 Task 的方法剥掉
    /// "Async" 后缀生成命令名。这里方法叫 Remove，生成的命令就是 RemoveCommand，
    /// 与 XAML 里的绑定一致（之前叫 RemoveAsync 导致生成的是 RemoveAsyncCommand，
    /// 绑定失败所以点了没反应）。
    /// </summary>
    [RelayCommand]
    private async Task Remove(DownloadItem? item)
    {
        if (item is null) return;

        var path = item.FullPath ?? Path.Combine(item.Directory, item.FileName);
        var fileExists = false;
        try { fileExists = File.Exists(path); } catch { /* ignore */ }

        bool deleteFile;
        if (item.IsActive)
        {
            var busy = Views.MessageBoxWindow.Confirm(
                $"「{Format.Truncate(item.DisplayName, 60)}」正在下载中。\n\n" +
                "从列表移除会同时停止这个下载。要继续吗？",
                "移除正在下载的任务", "停止下载并移除", "取消");
            if (busy is not true) return;
            deleteFile = AppServices.Settings.Current.DeleteFileWithTask;
        }
        else if (fileExists)
        {
            var prefer = AppServices.Settings.Current.DeleteFileWithTask;
            var choice = Views.MessageBoxWindow.Confirm(
                $"「{Format.Truncate(item.DisplayName, 60)}」\n\n文件已存在于：\n{path}\n\n要连文件一起删掉吗？",
                "从列表移除",
                prefer ? "同时删除文件" : "仅从列表移除（保留文件）",
                prefer ? "仅从列表移除（保留文件）" : "同时删除文件",
                "取消");
            if (choice is null) return;
            deleteFile = prefer ? choice.Value : !choice.Value;
        }
        else
        {
            deleteFile = false;
        }

        await AppServices.Downloads.RemoveAsync(item.Id, deleteFile);
        ReloadFromHistory();
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var count = Items.Count(i => i.Status == DownloadStatus.Completed);
        if (count == 0)
        {
            App.Notify("没有可清除的记录", "当前没有已完成的任务。", InfoSeverity.Info);
            return;
        }
        if (Views.MessageBoxWindow.Confirm(
                $"要从列表里清除 {count} 条已完成记录吗？\n\n（只清记录，已下载的文件不会删除）",
                "清除已完成", "清除", "取消") is not true) return;

        AppServices.History.ClearCompleted();
        ReloadFromHistory();
    }

    [RelayCommand]
    private void OpenFile(DownloadItem? item)
    {
        try
        {
            var path = item?.FullPath ?? (item is null ? null : Path.Combine(item.Directory, item.FileName));
            if (path is null || !File.Exists(path))
            {
                App.Notify("文件不存在", "可能已被移动或删除：" + path, InfoSeverity.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void OpenFolder(DownloadItem? item)
    {
        try
        {
            var dir = item?.Directory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                App.Notify("目录不存在", dir ?? "(未知)", InfoSeverity.Warning);
                return;
            }
            var path = item?.FullPath ?? Path.Combine(dir, item?.FileName ?? "");
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开目录失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void CopyUrl(DownloadItem? item)
    {
        if (item is null) return;
        try
        {
            Clipboard.SetText(item.FinalUrl ?? item.Url);
            App.Notify("已复制", "下载地址已复制到剪贴板", InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void CopyPath(DownloadItem? item)
    {
        if (item is null) return;
        try
        {
            Clipboard.SetText(item.FullPath ?? Path.Combine(item.Directory, item.FileName));
            App.Notify("已复制", "文件路径已复制到剪贴板", InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("复制失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private void ShowDetails(DownloadItem? item)
    {
        if (item is null) return;
        var text =
            $"名称：{item.DisplayName}\n" +
            $"状态：{item.StatusText}\n" +
            $"进度：{item.SizeText}（{item.PercentText}）\n" +
            $"速度：{item.SpeedText}    剩余：{item.EtaText}\n" +
            $"线程：{item.ConnectionsText}\n" +
            (string.IsNullOrEmpty(item.ConnectionNote) ? "" : $"线程说明：{item.ConnectionNote}\n") +
            $"引擎：{item.Engine}\n" +
            $"解析器：{item.ProviderName ?? "—"}\n" +
            $"站点：{item.Site ?? "—"}\n" +
            $"创建时间：{item.CreatedAtText}\n" +
            $"完成时间：{item.FinishedAtText}\n" +
            $"保存目录：{item.Directory}\n" +
            $"原始链接：{item.Url}" +
            (item.FinalUrl is null ? "" : $"\n直链：{item.FinalUrl}") +
            (string.IsNullOrEmpty(item.ErrorMessage) ? "" : $"\n\n错误：{item.ErrorMessage}");

        if (item.Diagnostics.Count > 0)
        {
            text += "\n\n—— 解析过程诊断（网盘接口改版时看这里）——\n" +
                    string.Join("\n", item.Diagnostics);
        }

        Views.MessageBoxWindow.ShowInfo(text, "任务详情");
    }

    /// <summary>
    /// 给"缺提取码"的网盘任务补填提取码并重新解析。
    /// </summary>
    [RelayCommand]
    private async Task FillPassword(DownloadItem? item)
    {
        if (item is null) return;

        var pwd = Views.InputDialog.Show(
            "填写提取码",
            $"「{Format.Truncate(item.Title.Length > 0 ? item.Title : item.Url, 60)}」\n\n" +
            "这个网盘分享需要提取码（访问密码）。填好后会立刻重新解析，不需要重新添加任务。",
            string.Empty, multiline: false);

        if (pwd is null) return;
        if (string.IsNullOrWhiteSpace(pwd))
        {
            App.Notify("提取码为空", "请输入分享的提取码。", InfoSeverity.Warning);
            return;
        }

        item.ErrorMessage = null;
        item.NeedPassword = false;
        item.DownloadedBytes = 0;
        item.Status = DownloadStatus.Queued;

        var req = new Core.Services.AddDownloadRequest
        {
            Url = item.Url,
            FileName = item.FileName,
            Title = item.Title,
            Directory = item.Directory,
            UserAgent = item.UserAgent,
            Referer = item.Referer,
            Password = pwd.Trim()
        };

        var newItem = await AppServices.Downloads.AddAsync(req);
        if (newItem.Status == DownloadStatus.Failed)
        {
            App.Notify("仍然失败", newItem.ErrorMessage ?? "解析失败", InfoSeverity.Error);
        }
        else
        {
            await AppServices.Downloads.RemoveAsync(item.Id, deleteFile: false);
            App.Notify("已重新解析", newItem.DisplayName, InfoSeverity.Success);
        }
        ReloadFromHistory();
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            var dir = AppServices.Settings.Current.DownloadDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify("打开目录失败", ex.Message, InfoSeverity.Error); }
    }

    [RelayCommand]
    private async Task AddUrlFromClipboardAsync()
    {
        try
        {
            if (!Clipboard.ContainsText()) { App.Notify("剪贴板为空", "剪贴板里没有文本", InfoSeverity.Warning); return; }
            var text = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            var item = await AppServices.Downloads.AddAsync(new Core.Services.AddDownloadRequest { Url = text });
            App.Notify("已添加下载", item.DisplayName, InfoSeverity.Success);
        }
        catch (Exception ex) { App.Notify("添加失败", ex.Message, InfoSeverity.Error); }
    }

    public void Dispose() => _timer.Stop();
}
