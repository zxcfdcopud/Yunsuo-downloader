using YunsuoDownloader.Core.Models;

namespace YunsuoDownloader.Core.Services;

/// <summary>记录一个外部工具（aria2c / yt-dlp / ffmpeg）的探测结果。</summary>
public sealed class ToolInfo
{
    private readonly Lazy<string?> _version;

    public ToolInfo(string name, string? path, string? version = null, Func<string?>? versionFactory = null)
    {
        Name = name;
        Path = path;
        // 传了现成的版本号就直接用；否则惰性去读（第一次访问 Version 才起子进程）。
        _version = versionFactory is not null
            ? new Lazy<string?>(versionFactory, LazyThreadSafetyMode.ExecutionAndPublication)
            : new Lazy<string?>(() => version, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Name { get; }
    public string? Path { get; }

    /// <summary>版本号：<b>第一次访问时会起子进程</b>（xxx.exe --version），约 0.3~0.6 秒。</summary>
    public string? Version => _version.Value;

    public bool Found => !string.IsNullOrEmpty(Path);
    public override string ToString() => Found ? $"{Name}: {Version ?? "?"} ({Path})" : $"{Name}: 未找到";
}

/// <summary>
/// 定位并缓存三件套：
///   aria2c.exe  多线程下载内核
///   yt-dlp.exe  视频 / HLS / m3u8 解析内核
///   ffmpeg.exe  音视频合并与转封装（HLS 必备）
/// 查找顺序：用户显式配置 → exe 目录\tools → 工程根目录\tools → exe 目录 → PATH。
/// </summary>
public sealed class ToolsLocator
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, ToolInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolsLocator(AppSettings settings) => _settings = settings;

    public ToolInfo Aria2 => Get("aria2c");
    public ToolInfo YtDlp => Get("yt-dlp");
    public ToolInfo Ffmpeg => Get("ffmpeg");
    public ToolInfo Ffprobe => Get("ffprobe");

    /// <summary>找到的工具所在目录（用于传给 yt-dlp --ffmpeg-location）。</summary>
    public string? FfmpegDirectory => Ffmpeg.Path is { } p ? Path.GetDirectoryName(p) : null;

    public void Invalidate() => _cache.Clear();

    public ToolInfo Get(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var info = Probe(name);
        _cache[name] = info;
        return info;
    }

    private ToolInfo Probe(string name)
    {
        var exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var explicitPath = name.ToLowerInvariant() switch
        {
            "aria2c" => _settings.Aria2Path,
            "yt-dlp" => _settings.YtDlpPath,
            "ffmpeg" => _settings.FfmpegPath,
            "ffprobe" => null,
            _ => null
        };

        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(Path.Combine(AppPaths.BaseDirectory, "tools", exeName));
        candidates.Add(Path.Combine(AppPaths.BaseDirectory, exeName));
        if (AppPaths.FindProjectRoot() is { } root)
        {
            candidates.Add(Path.Combine(root, "tools", exeName));
            if (name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe", StringComparison.OrdinalIgnoreCase))
                candidates.Add(Path.Combine(root, "tools", "ffmpeg", "bin", exeName));
        }
        // 环境变量 PATH
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), exeName)); } catch { /* ignore */ }
        }

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full))
                {
                    // 关键：这里只记路径，不读版本号。
                    // 读版本要起子进程（WaitForExit 最多 4 秒），以前在构造 / 设置页刷新时
                    // 同步做，三个工具加起来 1.8 秒，直接把界面卡住（用户反馈的标签切换卡顿）。
                    // 现在改成 Lazy：谁真的要看版本号谁才触发。
                    var path = full;
                    return new ToolInfo(name, path, versionFactory: () => ReadVersion(path));
                }
            }
            catch { /* ignore */ }
        }
        return new ToolInfo(name, null);
    }

    private static string? ReadVersion(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var line = p.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) line = p.StandardError.ReadLine();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } }
            return line?.Trim();
        }
        catch { return null; }
    }

    public IReadOnlyList<ToolInfo> All() => new[] { Aria2, YtDlp, Ffmpeg, Ffprobe };

    public string Summary() => string.Join(Environment.NewLine, All().Select(t => t.ToString()));
}
