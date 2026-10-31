using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>设置读写 + 变更通知 + 网盘凭据的 DPAPI 加解密。</summary>
public sealed class SettingsService
{
    private readonly object _lock = new();

    public AppSettings Current { get; private set; } = new();

    public string FilePath => AppPaths.SettingsFile;

    public event EventHandler<AppSettings>? Changed;

    public void Load()
    {
        AppPaths.EnsureCreated();
        var loaded = JsonUtil.ReadFile<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        loaded.PanCredentials ??= new List<PanCredential>();
        loaded.BrowserCookies ??= new List<BrowserCookie>();

        // 网盘 Cookie：从 DPAPI 密文还原到内存
        foreach (var c in loaded.PanCredentials)
        {
            if (string.IsNullOrEmpty(c.Cookie) && !string.IsNullOrEmpty(c.CookieProtected))
                c.Cookie = SecretProtector.Unprotect(c.CookieProtected) ?? string.Empty;
        }

        // 站点 Cookie（插件采集的那份）同理
        foreach (var c in loaded.BrowserCookies)
        {
            if (string.IsNullOrEmpty(c.Cookie) && !string.IsNullOrEmpty(c.CookieProtected))
                c.Cookie = SecretProtector.Unprotect(c.CookieProtected) ?? string.Empty;
        }

        ApplyDefaults(loaded);
        Migrate(loaded);
        lock (_lock) Current = loaded;
        Save();
    }

    /// <summary>当前设置结构版本，见 <see cref="AppSettings.SettingsVersion"/>。</summary>
    public const int CurrentSettingsVersion = 2;

    /// <summary>
    /// 一次性设置迁移。只做"老版本默认值写错了"这类修正，不碰用户主动改过的选项。
    ///
    /// 公开出来是为了让命令行自检（ysd selftest）能直接验证迁移逻辑 ——
    /// 这段代码只在"读老配置文件"时跑一次，平时很难复现。
    /// </summary>
    public static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentSettingsVersion) return;

        if (s.SettingsVersion < 2)
        {
            // 1.0 的默认值是 MinimizeToTray = true，导致最小化时窗口被 Hide()，
            // Win11 的原生最小化动画完全不出现。这里把老用户的这个值也掰回 false：
            // 想常驻托盘的可以自己在设置里打开（关闭按钮的"关闭到托盘"不受影响）。
            s.MinimizeToTray = false;
            Utils.Log.Info("[设置迁移] MinimizeToTray -> false（最小化改为进任务栏，恢复系统动画）");
        }

        s.SettingsVersion = CurrentSettingsVersion;
    }

    private static void ApplyDefaults(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.MediaServerToken)) s.MediaServerToken = SecretProtector.NewToken();
        if (s.MediaServerPort is < 1024 or > 65535) s.MediaServerPort = 16888;
        if (string.IsNullOrWhiteSpace(s.MediaServerHost)) s.MediaServerHost = "127.0.0.1";
        if (s.ConnectionsPerDownload is < 1 or > 64) s.ConnectionsPerDownload = 8;
        if (s.AutoConnectionCap is < 1 or > 64) s.AutoConnectionCap = 16;
        if (string.IsNullOrWhiteSpace(s.ConnectionMode)) s.ConnectionMode = "Auto";
        if (s.ConnectionMode is not ("Auto" or "Fixed")) s.ConnectionMode = "Auto";
        if (s.ConcurrentFragments is < 1 or > 64) s.ConcurrentFragments = 16;
        if (s.MaxConcurrentDownloads is < 1 or > 32) s.MaxConcurrentDownloads = 3;
        if (s.MinSplitSizeKb < 1024) s.MinSplitSizeKb = 1024;   // aria2 硬性要求 >= 1M
        if (s.MinSplitSizeKb > 1024 * 1024) s.MinSplitSizeKb = 1024 * 1024;
        if (s.MaxHistoryItems < 20) s.MaxHistoryItems = 500;
        if (string.IsNullOrWhiteSpace(s.DownloadDirectory)) s.DownloadDirectory = AppSettings.DefaultDownloadDirectory;
        if (string.IsNullOrWhiteSpace(s.Theme)) s.Theme = "System";
    }

    public void Save()
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = Current.Clone();
            snapshot.PanCredentials = Current.PanCredentials
                .Select(c => new PanCredential
                {
                    Provider = c.Provider,
                    DisplayName = c.DisplayName,
                    Enabled = c.Enabled,
                    Remark = c.Remark,
                    UpdatedAt = c.UpdatedAt,
                    CookieProtected = string.IsNullOrEmpty(c.Cookie) ? c.CookieProtected : SecretProtector.Protect(c.Cookie)
                }).ToList();

            // 站点 Cookie 同样只写密文（明文只留在内存里）
            snapshot.BrowserCookies = Current.BrowserCookies
                .Select(c => new BrowserCookie
                {
                    Domain = c.Domain,
                    UpdatedAt = c.UpdatedAt,
                    CookieProtected = string.IsNullOrEmpty(c.Cookie) ? c.CookieProtected : SecretProtector.Protect(c.Cookie)
                }).ToList();
        }
        JsonUtil.WriteFile(AppPaths.SettingsFile, snapshot);
    }

    /// <summary>修改设置并落盘 + 广播。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock) mutate(Current);
        ApplyDefaults(Current);
        Save();
        Changed?.Invoke(this, Current);
    }

    public PanCredential? FindCredential(string provider) =>
        Current.PanCredentials.FirstOrDefault(c =>
            c.Enabled && string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取某个网盘的 Cookie。
    /// 优先用用户在「设置 → 网盘账号」里手动填的；**没有就用插件自动采集的那份**
    /// （<see cref="AppSettings.BrowserCookies"/>，按域名匹配）。
    /// 这样用户只要在浏览器里登录过网盘，就不必再手动粘贴一次。
    /// </summary>
    public string? GetCookie(string provider)
    {
        var manual = FindCredential(provider)?.Cookie;
        if (!string.IsNullOrWhiteSpace(manual)) return manual;

        // 没有手填的：按 provider 对应的域名去插件采集的缓存里找。
        // 一个网盘往往有多个域名（阿里 alipan / aliyundrive，123 有一堆镜像），
        // 所以这里按"域名列表 + 子域后缀"匹配，否则用户在 123684.com 上登录过，
        // 程序认的是 123pan.com，就永远匹配不上。
        var domains = ProviderDomains(provider);
        if (domains.Length == 0) return null;

        lock (_lock)
        {
            var hit = Current.BrowserCookies.FirstOrDefault(c => domains.Any(d => MatchesDomain(c.Domain, d)));
            if (hit is null) return null;
            if (!string.IsNullOrEmpty(hit.Cookie)) return hit.Cookie;
            if (string.IsNullOrEmpty(hit.CookieProtected)) return null;
            var plain = SecretProtector.Unprotect(hit.CookieProtected);
            if (!string.IsNullOrEmpty(plain)) hit.Cookie = plain;
            return plain;
        }
    }

    /// <summary>Cookie 的域名是否属于 <paramref name="domain"/>（含子域名，双向兼容）。</summary>
    public static bool MatchesDomain(string? cookieDomain, string domain)
    {
        if (string.IsNullOrWhiteSpace(cookieDomain)) return false;
        return cookieDomain.Equals(domain, StringComparison.OrdinalIgnoreCase)
               || cookieDomain.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
               || domain.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 网盘 Provider → 可能落到哪些域名上（插件采集是按域名存的）。
    /// 第一个是主域名，其余是镜像/旧域名。
    /// </summary>
    public static string[] ProviderDomains(string provider) => provider.ToLowerInvariant() switch
    {
        "baidu" => new[] { "pan.baidu.com", "baidu.com" },
        "aliyun" => new[] { "www.alipan.com", "www.aliyundrive.com", "alipan.com", "aliyundrive.com" },
        "quark" => new[] { "pan.quark.cn", "quark.cn" },
        "pan123" => new[]
        {
            "www.123pan.com", "www.123684.com", "www.123865.com", "www.123912.com", "www.123592.com",
            "123pan.com", "123684.com", "123865.com"
        },
        _ => Array.Empty<string>()
    };

    /// <summary>兼容旧调用：取第一个主域名。</summary>
    public static string? ProviderDomain(string provider)
    {
        var list = ProviderDomains(provider);
        return list.Length > 0 ? list[0] : null;
    }

    public void SetCredential(string provider, string displayName, string cookie)
    {
        Update(s =>
        {
            var existing = s.PanCredentials.FirstOrDefault(c =>
                string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                s.PanCredentials.Add(new PanCredential
                {
                    Provider = provider,
                    DisplayName = displayName,
                    Cookie = cookie,
                    UpdatedAt = DateTime.Now
                });
            }
            else
            {
                existing.Cookie = cookie;
                existing.DisplayName = displayName;
                existing.UpdatedAt = DateTime.Now;
                existing.Enabled = true;
            }
        });
    }

    public void RemoveCredential(string provider) =>
        Update(s => s.PanCredentials.RemoveAll(c =>
            string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase)));
}
