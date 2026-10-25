using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>
/// 下载历史（需求 6：首页能看到最近下载的内容及对应时间）。
/// 进度更新很频繁，因此落盘做了防抖：最后一次变更后 3 秒写入。
/// </summary>
public sealed class HistoryService : IDisposable
{
    private readonly object _lock = new();
    private readonly List<DownloadItem> _items = new();
    private readonly System.Timers.Timer _debounce;
    private readonly int _maxItems;
    private bool _dirty;

    public event EventHandler? Changed;

    public HistoryService(int maxItems = 500)
    {
        _maxItems = Math.Max(20, maxItems);
        _debounce = new System.Timers.Timer(3000) { AutoReset = false };
        _debounce.Elapsed += (_, _) => { if (_dirty) SaveNow(); };
    }

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public void Load()
    {
        var loaded = JsonUtil.ReadFile<List<DownloadItem>>(AppPaths.HistoryFile) ?? new List<DownloadItem>();
        lock (_lock)
        {
            _items.Clear();
            foreach (var it in loaded)
            {
                // 上次退出时仍在进行的任务 -> 标记为"已暂停"，允许续传
                if (it.Status is DownloadStatus.Downloading or DownloadStatus.Connecting
                    or DownloadStatus.Resolving or DownloadStatus.Merging or DownloadStatus.Queued)
                {
                    it.Status = DownloadStatus.Paused;
                    it.Speed = 0;
                    it.Connections = 0;
                    it.EtaSeconds = 0;
                }
                it.RaiseAll();
                _items.Add(it);
            }
            _items.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            Trim();
        }
    }

    public void Add(DownloadItem item)
    {
        lock (_lock)
        {
            var existing = _items.FindIndex(i => i.Id == item.Id);
            if (existing >= 0) _items[existing] = item;
            else _items.Insert(0, item);
            Trim();
        }
        MarkDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_lock) _items.RemoveAll(i => i.Id == id);
        MarkDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCompleted()
    {
        lock (_lock) _items.RemoveAll(i => i.Status == DownloadStatus.Completed);
        MarkDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAll()
    {
        lock (_lock) _items.Clear();
        MarkDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkDirty()
    {
        _dirty = true;
        try
        {
            _debounce.Stop();
            _debounce.Start();
        }
        catch { /* ignore */ }
    }

    public void SaveNow()
    {
        List<DownloadItem> snapshot;
        lock (_lock)
        {
            _dirty = false;
            snapshot = _items.ToList();
        }
        JsonUtil.WriteFile(AppPaths.HistoryFile, snapshot);
    }

    private void Trim()
    {
        if (_items.Count <= _maxItems) return;
        // 优先保留未完成的任务
        var ordered = _items.OrderByDescending(i => i.IsActive).ThenByDescending(i => i.CreatedAt).Take(_maxItems).ToList();
        _items.Clear();
        _items.AddRange(ordered);
    }

    public void Dispose()
    {
        try { _debounce.Stop(); _debounce.Dispose(); } catch { }
        SaveNow();
    }
}
