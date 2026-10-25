namespace YunsuoDownloader.Core.Services;

/// <summary>应用用到的所有路径。</summary>
public static class AppPaths
{
    public const string AppName = "YunsuoDownloader";

    /// <summary>exe 所在目录（发布后即安装目录）。</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// 数据目录：默认 %AppData%\YunsuoDownloader。
    /// 可用环境变量 YSD_DATA_DIR 覆盖（自检 / 便携模式用）。
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("YSD_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { return Path.GetFullPath(custom); } catch { /* 回退默认 */ }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public static string CredentialsFile => Path.Combine(DataDirectory, "pan-credentials.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string EngineStateDirectory => Path.Combine(DataDirectory, "engine-state");
    public static string UpdateCacheDirectory => Path.Combine(DataDirectory, "update-cache");

    /// <summary>工程根目录（开发时用于定位 tools\）。</summary>
    public static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "YunsuoDownloader.sln"))) return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, "tools"))) return dir.FullName;
        }
        return null;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(EngineStateDirectory);
        Directory.CreateDirectory(UpdateCacheDirectory);
    }
}
