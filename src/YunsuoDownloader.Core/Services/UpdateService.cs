using System.Net.Http;
using System.Text.Json;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NotConfigured,
    Failed
}

public sealed class UpdateInfo
{
    public Version Version { get; set; } = new(0, 0);
    public string VersionText { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? PageUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public string? AssetName { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public sealed class UpdateCheckResult
{
    public UpdateStatus Status { get; set; }
    public UpdateInfo? Info { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? FeedUrl { get; set; }
}

/// <summary>
/// 检查更新（需求 10）。
/// 支持两种更新源，按顺序尝试：
///   1) 设置里填写的 URL
///   2) exe 同目录的 update.json（适合企业内网/U 盘分发）
///   3) 内置 GitHub Releases API 地址
/// 两种格式都能解析：
///   * 通用 JSON：{ "version": "1.1.0", "url": "...", "notes": "...", "sha256": "..." }
///   * GitHub Releases API：{ "tag_name": "v1.1.0", "assets": [ ... ] }
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// 内置的默认更新源仓库（owner/repo）。
    /// 指向本项目的 GitHub 仓库：有新 Release 时程序会提示更新。
    /// 留空表示不使用内置 GitHub 源（只读设置里的地址和程序目录下的 update.json）。
    /// </summary>
    public const string BuiltInGithubRepo = "zxcfdcopud/Yunsuo-downloader";

    /// <summary>仅用于文档示例展示。</summary>
    public const string ExampleGithubRepo = "your-name/your-repo";

    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "YunsuoDownloader-Updater");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
        ?? new Version(1, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    public IReadOnlyList<string> CandidateFeedUrls()
    {
        var list = new List<string>();
        var configured = _settings.Current.UpdateFeedUrl;
        if (!string.IsNullOrWhiteSpace(configured)) list.Add(configured.Trim());

        var local = Path.Combine(AppPaths.BaseDirectory, "update.json");
        list.Add(local);
        if (!string.IsNullOrWhiteSpace(BuiltInGithubRepo))
            list.Add($"https://api.github.com/repos/{BuiltInGithubRepo}/releases/latest");
        return list;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var url in CandidateFeedUrls())
        {
            try
            {
                var isLocal = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                if (isLocal && !File.Exists(url))
                {
                    errors.Add($"{Path.GetFileName(url)} 不存在");
                    continue;
                }

                var json = isLocal
                    ? await File.ReadAllTextAsync(url, ct).ConfigureAwait(false)
                    : await _http.GetStringAsync(url, ct).ConfigureAwait(false);

                var info = Parse(json);
                if (info is null)
                {
                    errors.Add($"{Short(url)} 内容无法识别");
                    continue;
                }

                _settings.Update(s => s.LastUpdateCheck = DateTime.Now);

                var current = CurrentVersion;
                if (info.Version > current)
                {
                    return new UpdateCheckResult
                    {
                        Status = UpdateStatus.UpdateAvailable,
                        Info = info,
                        FeedUrl = url,
                        Message = $"发现新版本 {info.VersionText}（当前 {CurrentVersionText}）"
                    };
                }
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.UpToDate,
                    Info = info,
                    FeedUrl = url,
                    Message = $"已是最新版本（{CurrentVersionText}）"
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"检查更新失败 {Short(url)}：{ex.Message}");
                errors.Add($"{Short(url)}：{ex.Message}");
            }
        }

        var configuredOnly = !string.IsNullOrWhiteSpace(_settings.Current.UpdateFeedUrl);
        return new UpdateCheckResult
        {
            Status = configuredOnly ? UpdateStatus.Failed : UpdateStatus.NotConfigured,
            Message = configuredOnly
                ? "检查更新失败：" + string.Join("；", errors.Take(2))
                // 内置 GitHub 源也可能暂时取不到（仓库还没有 Release / 断网 / 被墙）。
                // 这时明确说"没查到"，而不是让用户以为必须自己去配更新源。
                : string.IsNullOrWhiteSpace(BuiltInGithubRepo)
                    ? "尚未配置更新源。可在「设置 → 更新」中填写你的发布地址，或把 update.json 放到程序目录。"
                    : $"暂时查不到更新信息（已尝试 GitHub 仓库 {BuiltInGithubRepo}，可能还没有发布 Release，或网络不通）。\n" +
                      "不影响正常使用；也可以在「设置 → 更新」里手动指定发布地址。"
        };
    }

    private static string Short(string url)
    {
        try { return url.StartsWith("http") ? new Uri(url).Host : Path.GetFileName(url); }
        catch { return url; }
    }

    /// <summary>同时兼容通用 JSON 与 GitHub Releases API。</summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var info = new UpdateInfo();

        // ---- GitHub Releases
        if (root.TryGetProperty("tag_name", out var tag))
        {
            info.VersionText = (tag.GetString() ?? "").TrimStart('v', 'V');
            info.PageUrl = Str(root, "html_url");
            info.Notes = Str(root, "body");
            if (root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String
                && DateTime.TryParse(pub.GetString(), out var dt)) info.PublishedAt = dt;

            info.Version = ParseVersion(info.VersionText) ?? new Version(0, 0);

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                // 一个 Release 可能挂多个文件（安装包 / 便携 zip / 校验文件）。
                // 优先挑安装包（文件名含 setup / installer），其次任意 .exe/.msi，最后 zip。
                // 不能"谁先出现就选谁"——GitHub 返回顺序是按 asset id，不保证安装包在最前。
                JsonElement? installer = null, exe = null, zip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name") ?? "";
                    if (name.Length == 0) continue;
                    var lower = name.ToLowerInvariant();
                    var isExe = lower.EndsWith(".exe") || lower.EndsWith(".msi");
                    if (isExe && (lower.Contains("setup") || lower.Contains("installer")))
                    {
                        installer ??= a;
                    }
                    else if (isExe)
                    {
                        exe ??= a;
                    }
                    else if (lower.EndsWith(".zip"))
                    {
                        zip ??= a;
                    }
                }
                var best = installer ?? exe ?? zip;
                if (best is { } b)
                {
                    info.AssetName = Str(b, "name");
                    info.DownloadUrl = Str(b, "browser_download_url");
                    if (b.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s)) info.Size = s;
                }
            }
            return info;
        }

        // ---- 通用 JSON
        var versionText = Str(root, "version") ?? Str(root, "latestVersion") ?? Str(root, "tag");
        if (string.IsNullOrWhiteSpace(versionText)) return null;
        info.VersionText = versionText.Trim().TrimStart('v', 'V');
        info.Version = ParseVersion(info.VersionText) ?? new Version(0, 0);
        info.DownloadUrl = Str(root, "url") ?? Str(root, "downloadUrl") ?? Str(root, "download_url");
        info.Notes = Str(root, "notes") ?? Str(root, "changelog") ?? Str(root, "body");
        info.PageUrl = Str(root, "page") ?? Str(root, "pageUrl") ?? Str(root, "releaseUrl");
        info.Sha256 = Str(root, "sha256");
        if (root.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size)) info.Size = size;
        // 注意：发布包里的 update.json 示例中 url 是空字符串，这里必须挡一下，
        // 否则 new Uri("") 会抛 "Invalid URI: The URI is empty"，把整个更新检查搞失败。
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            try { info.AssetName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath); }
            catch { info.AssetName = null; }
        }
        else
        {
            info.DownloadUrl = null;
        }
        return info;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = new string(text.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            return new Version(major, minor, build);
        }
        catch { return null; }
    }

    /// <summary>下载新版本安装包到缓存目录，返回本地路径。</summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("更新源没有提供下载地址，请到发布页面手动下载。");

        Directory.CreateDirectory(AppPaths.UpdateCacheDirectory);
        var name = string.IsNullOrWhiteSpace(info.AssetName)
            ? $"YunsuoDownloader-{info.VersionText}.exe"
            : Format.SafeFileName(info.AssetName!);
        var target = Path.Combine(AppPaths.UpdateCacheDirectory, name);

        using var resp = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.Size;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

        var buffer = new byte[128 * 1024];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (total > 0) progress?.Report(Math.Clamp((double)done / total, 0, 1));
        }
        await dst.FlushAsync(ct).ConfigureAwait(false);
        await dst.DisposeAsync().ConfigureAwait(false);
        Log.Info($"新版本已下载到 {target}");

        // 校验可选哈希
        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            var actual = await ComputeSha256Async(target, ct).ConfigureAwait(false);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"安装包哈希校验失败：期望 {info.Sha256}，实际 {actual}");
        }

        return target;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>用系统默认程序打开安装包。</summary>
    public static void LaunchInstaller(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("安装包不存在", path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
