using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace YunsuoDownloader.Core.Models;

/// <summary>极简的可观察对象基类（不引入 WPF / MVVM 依赖，App 层可直接绑定）。</summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>批量刷新所有派生显示属性（进度等高频更新时用）。</summary>
    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}

public enum DownloadStatus
{
    Queued,
    Resolving,
    Connecting,
    Downloading,
    Merging,
    Paused,
    Completed,
    Failed,
    Canceled
}

public enum DownloadKind
{
    File,
    Video,
    Hls,
    Dash,
    Magnet,
    Torrent,
    PanShare
}

public enum EngineKind
{
    None,
    Aria2,
    Http,
    YtDlp
}

/// <summary>一条下载任务。既用于运行时状态，也用于 history.json 持久化。</summary>
public sealed class DownloadItem : ObservableModel
{
    private DownloadStatus _status = DownloadStatus.Queued;
    private long _downloadedBytes;
    private long _totalBytes;
    private double _speed;
    private double _etaSeconds;
    private int _connections;
    private string? _connectionNote;
    private string? _errorMessage;
    private string _fileName = string.Empty;
    private string _directory = string.Empty;
    private string? _fullPath;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private string? _finalUrl;
    private int _queueIndex;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户输入的原始链接（可能是网盘分享页 / 视频页 / m3u8 / 直链 / 磁力）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>解析后的真实下载地址（网盘/视频解析后回填）。</summary>
    public string? FinalUrl
    {
        get => _finalUrl;
        set => SetProperty(ref _finalUrl, value);
    }

    public DownloadKind Kind { get; set; } = DownloadKind.File;

    public EngineKind Engine { get; set; } = EngineKind.None;

    public string Title { get; set; } = string.Empty;

    public string FileName
    {
        get => _fileName;
        set { if (SetProperty(ref _fileName, value)) OnPropertiesChanged(nameof(DisplayName)); }
    }

    public string Directory
    {
        get => _directory;
        set { if (SetProperty(ref _directory, value)) OnPropertiesChanged(nameof(DisplayName)); }
    }

    public string? FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    // ---------------------------------------------------------- 运行时状态
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertiesChanged(nameof(Status), nameof(StatusText), nameof(IsActive), nameof(IsFinished),
                                nameof(IsFailed), nameof(CanPause), nameof(CanResume), nameof(StatusGlyph));
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (_downloadedBytes == value) return;
            _downloadedBytes = value;
            OnPropertiesChanged(nameof(DownloadedBytes), nameof(Progress), nameof(ProgressPercent),
                                nameof(SizeText), nameof(PercentText));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            OnPropertiesChanged(nameof(TotalBytes), nameof(Progress), nameof(ProgressPercent),
                                nameof(SizeText), nameof(PercentText));
        }
    }

    /// <summary>字节/秒。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (Math.Abs(_speed - value) < 0.5) return;
            _speed = value;
            OnPropertiesChanged(nameof(Speed), nameof(SpeedText));
        }
    }

    public double EtaSeconds
    {
        get => _etaSeconds;
        set
        {
            if (Math.Abs(_etaSeconds - value) < 0.5) return;
            _etaSeconds = value;
            OnPropertiesChanged(nameof(EtaSeconds), nameof(EtaText));
        }
    }

    public int Connections
    {
        get => _connections;
        set { if (SetProperty(ref _connections, value)) OnPropertyChanged(nameof(ConnectionsText)); }
    }

    /// <summary>用户在新建任务时手动指定的线程数（0 = 交给 <see cref="Services.ConnectionAdvisor"/> 自动推荐）。</summary>
    public int ConnectionsOverride { get; set; }

    /// <summary>本次实际采用的线程数是怎么来的（自动推荐的理由 / 手动指定 / 设置固定值）。</summary>
    public string? ConnectionNote
    {
        get => _connectionNote;
        set => SetProperty(ref _connectionNote, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    // ---------------------------------------------------------- 时间戳（需求 6：能看到下载对应的时间）
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? StartedAt
    {
        get => _startedAt;
        set { if (SetProperty(ref _startedAt, value)) OnPropertiesChanged(nameof(StartedAt), nameof(CreatedAtText), nameof(DurationText)); }
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        set { if (SetProperty(ref _completedAt, value)) OnPropertiesChanged(nameof(CompletedAt), nameof(CreatedAtText), nameof(FinishedAtText), nameof(DurationText)); }
    }

    // ---------------------------------------------------------- 请求上下文
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public string? Origin { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------- 来源与元数据
    public string? ProviderName { get; set; }
    public string? Site { get; set; }
    public string? Thumbnail { get; set; }
    public double DurationSeconds { get; set; }
    public string? Quality { get; set; }
    public bool IsHls { get; set; }
    public bool IsEncrypted { get; set; }

    // ---------------------------------------------------------- 编码与音视频分离
    /// <summary>视频编码（hvc1 / hev1 / avc1 / vp9…）。HEVC 在部分播放器上放不出来，界面要提示。</summary>
    public string? Codec { get; set; }

    /// <summary>是否是 HEVC（H.265）：决定要不要提示"部分播放器不支持"。</summary>
    [JsonIgnore]
    public bool IsHevc => !string.IsNullOrEmpty(Codec)
                          && (Codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
                              || Codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase)
                              || Codec.StartsWith("h265", StringComparison.OrdinalIgnoreCase));

    /// <summary>本条是独立音轨（抖音/快手常把音视频分开给）。</summary>
    public bool IsAudioOnly { get; set; }

    /// <summary>配套音频 URL：视频下载完后用它合并出带声音的文件。</summary>
    public string? AudioUrl { get; set; }

    /// <summary>音视频已合并完成（合并后清掉 AudioUrl，避免重复处理）。</summary>
    public bool AudioMerged { get; set; }

    /// <summary>aria2 的 GID（使用 aria2 引擎时有效）。</summary>
    public string? Gid { get; set; }

    /// <summary>自研 HTTP 引擎的分片状态文件，用于断点续传。</summary>
    public string? StatePath { get; set; }

    public int Priority { get; set; }

    /// <summary>排队序号（越小越先执行）。</summary>
    public int QueueIndex
    {
        get => _queueIndex;
        set => SetProperty(ref _queueIndex, value);
    }

    // ---------------------------------------------------------- 派生显示属性
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(FileName) ? (string.IsNullOrWhiteSpace(Title) ? Url : Title) : FileName;

    [JsonIgnore] public double Progress => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;

    [JsonIgnore] public double ProgressPercent => Math.Round(Progress * 100, 1);

    [JsonIgnore] public string PercentText => TotalBytes > 0 ? $"{ProgressPercent:0.0}%" : "—";

    [JsonIgnore] public string SizeText => TotalBytes > 0
        ? $"{Utils.Format.Bytes(DownloadedBytes)} / {Utils.Format.Bytes(TotalBytes)}"
        : Utils.Format.Bytes(DownloadedBytes);

    [JsonIgnore] public string SpeedText => IsActive && Speed > 0 ? Utils.Format.Speed(Speed) : "—";

    [JsonIgnore] public string EtaText => IsActive && EtaSeconds > 0 ? Utils.Format.Duration(TimeSpan.FromSeconds(EtaSeconds)) : "—";

    [JsonIgnore] public string ConnectionsText => Connections > 0 ? $"{Connections} 线程" : "—";

    /// <summary>解析这个任务时是否因为"缺提取码"而失败（界面据此提示用户补填）。</summary>
    public bool NeedPassword { get; set; }

    /// <summary>需要提取码的网盘 Provider（Baidu / Quark / Aliyun / Pan123）。</summary>
    public string? PasswordProvider { get; set; }

    /// <summary>分享 ID，用于把用户填的提取码和具体分享对应起来。</summary>
    public string? ShareId { get; set; }

    /// <summary>解析过程的诊断记录（每一步 HTTP 调用的真实返回），排查接口改版时看这个。</summary>
    public List<string> Diagnostics { get; set; } = new();

    /// <summary>需求 6：列表上直接能看到"下载时间"。</summary>
    [JsonIgnore] public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore] public string FinishedAtText => CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    [JsonIgnore] public string DurationText
    {
        get
        {
            if (StartedAt is null) return "—";
            var end = CompletedAt ?? DateTime.Now;
            var d = end - StartedAt.Value;
            return d.TotalSeconds < 1 ? "—" : Utils.Format.Duration(d);
        }
    }

    [JsonIgnore] public string StatusText => Status switch
    {
        DownloadStatus.Queued => "排队中",
        DownloadStatus.Resolving => "解析链接中",
        DownloadStatus.Connecting => "连接中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Merging => "合并音视频中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => "失败",
        DownloadStatus.Canceled => "已取消",
        _ => Status.ToString()
    };

    [JsonIgnore] public string StatusGlyph => Status switch
    {
        DownloadStatus.Queued => "\uE823",      // Recent
        DownloadStatus.Resolving => "\uE9D9",   // Search
        DownloadStatus.Connecting => "\uE703",  // Network
        DownloadStatus.Downloading => "\uE896", // Download
        DownloadStatus.Merging => "\uE9F5",     // Merge/Combine
        DownloadStatus.Paused => "\uE769",      // Pause
        DownloadStatus.Completed => "\uE930",   // Completed
        DownloadStatus.Failed => "\uEA39",      // ErrorBadge
        DownloadStatus.Canceled => "\uE711",    // Cancel
        _ => "\uE946"
    };

    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Resolving or DownloadStatus.Connecting
        or DownloadStatus.Downloading or DownloadStatus.Merging;

    [JsonIgnore] public bool IsFinished => Status is DownloadStatus.Completed;

    [JsonIgnore] public bool IsFailed => Status is DownloadStatus.Failed or DownloadStatus.Canceled;

    [JsonIgnore] public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Connecting
        or DownloadStatus.Queued or DownloadStatus.Resolving;

    [JsonIgnore] public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;

    /// <summary>刷新所有派生属性（UI 批量刷新用）。</summary>
    public void RaiseAll()
    {
        OnPropertiesChanged(nameof(DisplayName), nameof(Progress), nameof(ProgressPercent), nameof(PercentText),
            nameof(SizeText), nameof(SpeedText), nameof(EtaText), nameof(ConnectionsText), nameof(CreatedAtText),
            nameof(FinishedAtText), nameof(DurationText), nameof(StatusText), nameof(IsActive), nameof(IsFinished),
            nameof(IsFailed), nameof(CanPause), nameof(CanResume), nameof(StatusGlyph));
    }
}
