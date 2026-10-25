using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.Services;

/// <summary>
/// 组合根：把 Core 的各个服务装配好，供界面各页共享。
/// 刻意不引入 DI 容器，保持依赖简单直观。
/// </summary>
public static class AppServices
{
    public static SettingsService Settings { get; private set; } = null!;
    public static HistoryService History { get; private set; } = null!;
    public static ToolsLocator Tools { get; private set; } = null!;
    public static DownloadManager Downloads { get; private set; } = null!;
    public static ExtensionBridge Extension { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;
    public static SingleInstanceService SingleInstance { get; } = new();

    public static bool Initialized { get; private set; }

    public static async Task InitializeAsync()
    {
        if (Initialized) return;

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogDirectory);
        Log.Info("================ 云梭下载器 启动 ================");

        Settings = new SettingsService();
        Settings.Load();
        Log.Info($"数据目录：{AppPaths.DataDirectory}");
        Log.Info($"默认下载目录：{Settings.Current.DownloadDirectory}");

        History = new HistoryService(Settings.Current.MaxHistoryItems);
        Tools = new ToolsLocator(Settings.Current);
        Updates = new UpdateService(Settings);

        Downloads = new DownloadManager(Settings, History, Tools);
        await Downloads.InitializeAsync().ConfigureAwait(true);

        Extension = new ExtensionBridge(Downloads, Settings);

        foreach (var t in Tools.All())
            Log.Info("工具探测 -> " + t);

        Settings.Changed += (_, s) =>
        {
            Downloads.ApplySettings();
            _ = Extension.PushConfigAsync();
        };

        // 下载完成/失败时顺便通知浏览器插件
        Downloads.ItemCompleted += (_, item) => _ = Extension.NotifyDownloadResultAsync(item, true);
        Downloads.ItemFailed += (_, item) => _ = Extension.NotifyDownloadResultAsync(item, false);

        Initialized = true;
    }

    public static async Task ShutdownAsync()
    {
        try { if (Initialized) await Extension.DisposeAsync().ConfigureAwait(false); } catch { }
        try { if (Initialized) await Downloads.DisposeAsync().ConfigureAwait(false); } catch { }
        try { SingleInstance.Dispose(); } catch { }
        Log.Info("================ 云梭下载器 退出 ================");
    }

    /// <summary>当前主题（System / Light / Dark）。</summary>
    public static string Theme => Settings.Current.Theme;
}
