using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>
/// 站点 Cookie 缓存：插件采集 → 按域名存盘（DPAPI 加密）→ 直接粘贴链接时复用。
///
/// 设计目标：**用户不需要手填 Cookie**。
///   * 用户在浏览器里正常访问过 pan.baidu.com / www.douyin.com，插件把 Cookie 带过来；
///   * 之后在程序里直接粘链接，这里按域名找到对应 Cookie 自动带上；
///   * 没有就返回 null，行为与以前完全一致。
///
/// 安全：落盘只写 DPAPI 密文（<see cref="SecretProtector"/>），明文只在内存里。
/// 用户可以在设置里关掉采集（<see cref="AppSettings.CaptureBrowserCookies"/>）。
/// </summary>
public sealed class CookieJar
{
    private readonly SettingsService _settings;
    private readonly object _lock = new();

    /// <summary>每条域名最多保留多少字符，防止某个站点几 MB 的 Cookie 撑爆设置文件。</summary>
    private const int MaxCookieLength = 16 * 1024;

    public CookieJar(SettingsService settings) => _settings = settings;

    /// <summary>
    /// 从 URL 里取出要匹配的域名。<c>pan.baidu.com</c> 这种还会往上退一级
    /// （<c>baidu.com</c>），因为插件可能按主域存过。
    /// </summary>
    public static IEnumerable<string> CandidateDomains(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;

        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) yield break;
        yield return host;

        // 去掉 www / m / v 这类前缀再来一次（v.douyin.com → douyin.com）
        var parts = host.Split('.');
        if (parts.Length >= 3)
        {
            var bare = string.Join('.', parts.Skip(parts.Length - 2));
            if (!string.Equals(bare, host, StringComparison.Ordinal)) yield return bare;
        }
        // pan.baidu.com 这种"子域 + 主域"两层，再退一级
        if (parts.Length >= 4)
        {
            var parent = string.Join('.', parts.Skip(1));
            if (!string.Equals(parent, host, StringComparison.Ordinal)) yield return parent;
        }
    }

    /// <summary>按 URL 找已缓存的 Cookie 头；没有返回 null。</summary>
    public string? GetForUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var cookies = _settings.Current.BrowserCookies;
        if (cookies is null || cookies.Count == 0) return null;

        foreach (var domain in CandidateDomains(url))
        {
            BrowserCookie? hit;
            lock (_lock)
                hit = cookies.FirstOrDefault(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;

            var plain = ResolvePlain(hit);
            if (!string.IsNullOrWhiteSpace(plain)) return plain;
        }
        return null;
    }

    /// <summary>某个域名是否已有缓存的登录态（界面用来显示"已从浏览器同步"）。</summary>
    public bool HasForUrl(string? url) => !string.IsNullOrWhiteSpace(GetForUrl(url));

    /// <summary>
    /// 保存插件采集到的 Cookie。
    /// <paramref name="url"/> 是页面地址（用它的域名做 key）。
    /// 返回 true 表示确实写入了（内容没变则跳过）。
    /// </summary>
    public bool SaveFromExtension(string? url, string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return false;
        if (!_settings.Current.CaptureBrowserCookies) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var domain = uri.Host.ToLowerInvariant();
        if (domain.Length == 0) return false;

        var value = cookieHeader.Trim();
        if (value.Length > MaxCookieLength) value = value[..MaxCookieLength];

        // 只保存看起来像 Cookie 的东西（必须有 "name=value" 结构）
        if (!value.Contains('=')) return false;

        lock (_lock)
        {
            var list = _settings.Current.BrowserCookies;
            var existing = list.FirstOrDefault(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase));
            var existingPlain = existing is null ? null : ResolvePlain(existing);
            if (string.Equals(existingPlain, value, StringComparison.Ordinal)) return false;

            if (existing is null)
            {
                list.Add(new BrowserCookie { Domain = domain, Cookie = value, UpdatedAt = DateTime.Now });
            }
            else
            {
                existing.Cookie = value;
                existing.UpdatedAt = DateTime.Now;
            }
        }

        // 触发落盘（Save 里会把 Cookie 加密成 CookieProtected）
        _settings.Save();
        Log.Info($"已从浏览器同步站点 Cookie：{domain}（{value.Length} 字符）");
        return true;
    }

    /// <summary>清掉某个域名的缓存。</summary>
    public void Remove(string domain)
    {
        lock (_lock)
        {
            var list = _settings.Current.BrowserCookies;
            var hit = list.FirstOrDefault(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return;
            list.Remove(hit);
        }
        _settings.Save();
    }

    /// <summary>清空所有缓存的站点 Cookie。</summary>
    public void Clear()
    {
        lock (_lock) _settings.Current.BrowserCookies.Clear();
        _settings.Save();
    }

    private static string? ResolvePlain(BrowserCookie c)
    {
        if (!string.IsNullOrEmpty(c.Cookie)) return c.Cookie;
        if (string.IsNullOrEmpty(c.CookieProtected)) return null;
        var plain = SecretProtector.Unprotect(c.CookieProtected);
        if (!string.IsNullOrEmpty(plain)) c.Cookie = plain;
        return plain;
    }
}
