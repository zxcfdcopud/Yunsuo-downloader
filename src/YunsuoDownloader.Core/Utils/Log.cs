namespace YunsuoDownloader.Core.Utils;

/// <summary>极简文件日志（按天分文件，保留最近 7 天）。</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static bool _inited;

    public static string? Directory { get; private set; }

    public static void Init(string? directory)
    {
        Directory = directory;
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach (var f in System.IO.Directory.GetFiles(directory, "*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            _inited = true;
        }
        catch { _inited = false; }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Error(Exception ex, string? context = null)
        => Write("ERROR", (context is null ? "" : context + " -> ") + ex);

    private static void Write(string level, string msg)
    {
        if (!_inited || Directory is null)
        {
            System.Diagnostics.Debug.WriteLine($"[{level}] {msg}");
            return;
        }
        try
        {
            var file = Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
            // 带 BOM 的 UTF-8，记事本打开中文日志不会乱码
            lock (Lock)
            {
                if (!File.Exists(file))
                    File.WriteAllText(file, string.Empty, new System.Text.UTF8Encoding(true));
                File.AppendAllText(file, line, System.Text.Encoding.UTF8);
            }
        }
        catch { /* 日志失败不能影响主流程 */ }
    }
}
