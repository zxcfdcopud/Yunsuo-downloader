using YunsuoDownloader.Core.Models;

namespace YunsuoDownloader.Core.Engine;

public sealed class DownloadProgressEventArgs : EventArgs
{
    public DownloadItem Item { get; init; } = null!;
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Speed { get; init; }
    public double EtaSeconds { get; init; }
    public int Connections { get; init; }
}

public sealed class DownloadDoneEventArgs : EventArgs
{
    public DownloadItem Item { get; init; } = null!;
    public string? FullPath { get; init; }
}

public sealed class DownloadFailedEventArgs : EventArgs
{
    public DownloadItem Item { get; init; } = null!;
    public string Error { get; init; } = string.Empty;
}

/// <summary>下载引擎抽象。目前有三个实现：aria2 / 自研 HTTP / yt-dlp。</summary>
public interface IDownloadEngine
{
    EngineKind Kind { get; }

    /// <summary>当前环境是否可用（例如 aria2c.exe 是否找到）。</summary>
    bool IsAvailable { get; }

    /// <summary>启动引擎（aria2 需要拉起进程；其它引擎为空实现）。</summary>
    Task<bool> StartAsync(CancellationToken ct = default);

    Task StopAsync();

    /// <summary>把任务交给引擎开始下载。</summary>
    Task EnqueueAsync(DownloadItem item, CancellationToken ct = default);

    Task PauseAsync(DownloadItem item);

    Task ResumeAsync(DownloadItem item);

    Task CancelAsync(DownloadItem item, bool deleteFile);

    /// <summary>运行期调整全局限速（字节/秒，0 = 不限）。</summary>
    void SetGlobalSpeedLimit(long bytesPerSecond);

    /// <summary>运行期调整并发数。</summary>
    void ApplySettings(AppSettings settings);

    event EventHandler<DownloadProgressEventArgs>? Progress;
    event EventHandler<DownloadDoneEventArgs>? Completed;
    event EventHandler<DownloadFailedEventArgs>? Failed;
}

/// <summary>引擎基类：统一事件触发。</summary>
public abstract class DownloadEngineBase : IDownloadEngine
{
    protected readonly Services.ToolsLocator Tools;

    protected DownloadEngineBase(Services.ToolsLocator tools) => Tools = tools;

    public abstract EngineKind Kind { get; }
    public abstract bool IsAvailable { get; }

    public virtual Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public virtual Task StopAsync() => Task.CompletedTask;
    public abstract Task EnqueueAsync(DownloadItem item, CancellationToken ct = default);
    public virtual Task PauseAsync(DownloadItem item) => Task.CompletedTask;
    public virtual Task ResumeAsync(DownloadItem item) => Task.CompletedTask;
    public virtual Task CancelAsync(DownloadItem item, bool deleteFile) => Task.CompletedTask;
    public virtual void SetGlobalSpeedLimit(long bytesPerSecond) { }
    public virtual void ApplySettings(AppSettings settings) { }

    public event EventHandler<DownloadProgressEventArgs>? Progress;
    public event EventHandler<DownloadDoneEventArgs>? Completed;
    public event EventHandler<DownloadFailedEventArgs>? Failed;

    protected void RaiseProgress(DownloadItem item, long done, long total, double speed, double eta, int conns)
        => Progress?.Invoke(this, new DownloadProgressEventArgs
        {
            Item = item,
            DownloadedBytes = done,
            TotalBytes = total,
            Speed = speed,
            EtaSeconds = eta,
            Connections = conns
        });

    protected void RaiseCompleted(DownloadItem item, string? path)
        => Completed?.Invoke(this, new DownloadDoneEventArgs { Item = item, FullPath = path });

    protected void RaiseFailed(DownloadItem item, string error)
        => Failed?.Invoke(this, new DownloadFailedEventArgs { Item = item, Error = error });
}
