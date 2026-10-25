namespace YunsuoDownloader.App.Models;

/// <summary>信息栏/通知的级别（对应 Fluent 的 InfoBarSeverity 语义）。</summary>
public enum InfoSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>首页列表的筛选条件。</summary>
public enum DownloadFilter
{
    All,
    Active,
    Completed,
    Failed
}

/// <summary>新建任务里让用户选择的清晰度。</summary>
public sealed class QualityOption
{
    public string Key { get; init; } = "best";
    public string Display { get; init; } = "最佳画质";
    public override string ToString() => Display;
}

/// <summary>设置页里"网盘账号"一行。</summary>
public sealed class PanRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _state = "未设置";
    private bool _hasCredential;

    public string Provider { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;

    public string State
    {
        get => _state;
        set { _state = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State))); }
    }

    public bool HasCredential
    {
        get => _hasCredential;
        set { _hasCredential = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCredential))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
