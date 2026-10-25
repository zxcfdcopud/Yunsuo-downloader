using System.Text;
using YunsuoDownloader.Core.Models;

namespace YunsuoDownloader.Core.Providers.Builtin;

/// <summary>普通 HTTP/HTTPS/FTP 直链。识别 m3u8 / mpd / torrent 并给出正确的引擎建议。</summary>
public sealed class DirectLinkProvider : ILinkProvider
{
    public string Name => "Direct";
    public string DisplayName => "通用直链";
    public int Priority => 0;
    public bool RequiresCredential => false;

    private static readonly string[] VideoExts = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v", ".rmvb", ".mpg", ".mpeg", ".3gp" };
    private static readonly string[] AudioExts = { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".opus" };

    public bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var path = TryGetPath(url);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        var result = new ResolveResult
        {
            Success = true,
            Url = url,
            ProviderName = Name
        };

        switch (ext)
        {
            case ".m3u8":
                result.Kind = DownloadKind.Hls;
                result.PreferYtDlp = true;
                result.Message = "识别为 HLS(m3u8) 流，交给 yt-dlp/ffmpeg 处理";
                break;
            case ".mpd":
                result.Kind = DownloadKind.Dash;
                result.PreferYtDlp = true;
                result.Message = "识别为 DASH(mpd) 流，交给 yt-dlp/ffmpeg 处理";
                break;
            case ".torrent":
                result.Kind = DownloadKind.Torrent;
                result.PreferAria2 = true;
                result.Message = "识别为 BT 种子";
                break;
            default:
                result.Kind = VideoExts.Contains(ext) || AudioExts.Contains(ext) ? DownloadKind.Video : DownloadKind.File;
                result.PreferAria2 = true;
                if (result.Kind == DownloadKind.Video) result.PreferYtDlp = false;
                break;
        }

        if (string.IsNullOrWhiteSpace(result.FileName))
        {
            var name = Path.GetFileName(Uri.UnescapeDataString(path));
            if (!string.IsNullOrWhiteSpace(name)) result.FileName = Utils.Format.SafeFileName(name);
        }
        return Task.FromResult(result);
    }

    private static string TryGetPath(string url)
    {
        try { return new Uri(url).AbsolutePath; }
        catch { return url; }
    }
}

/// <summary>磁力链接（aria2 原生支持 BT/DHT）。</summary>
public sealed class MagnetProvider : ILinkProvider
{
    public string Name => "Magnet";
    public string DisplayName => "磁力链接";
    public int Priority => 50;
    public bool RequiresCredential => false;

    public bool CanHandle(string url) => url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var name = ExtractName(url);
        return Task.FromResult(new ResolveResult
        {
            Success = true,
            Url = url,
            Kind = DownloadKind.Magnet,
            PreferAria2 = true,
            FileName = string.IsNullOrWhiteSpace(name) ? null : Utils.Format.SafeFileName(name),
            Title = name,
            ProviderName = Name,
            Message = "磁力链接将由 aria2 的 BT 内核下载"
        });
    }

    private static string? ExtractName(string magnet)
    {
        try
        {
            var idx = magnet.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rest = magnet[(idx + 3)..];
            var end = rest.IndexOf('&');
            var raw = end < 0 ? rest : rest[..end];
            return Uri.UnescapeDataString(raw).Replace('+', ' ');
        }
        catch { return null; }
    }
}

/// <summary>迅雷链接：thunder:// 后面是 "AA" + Base64(真实URL) + "ZZ"。</summary>
public sealed class ThunderProvider : ILinkProvider
{
    public string Name => "Thunder";
    public string DisplayName => "迅雷链接";
    public int Priority => 60;
    public bool RequiresCredential => false;

    public bool CanHandle(string url) => url.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase);

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var payload = url["thunder://".Length..].Trim();
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            // 迅雷格式是 "AA" + 真实地址 + "ZZ"，两端的标记分别容错处理
            if (decoded.StartsWith("AA", StringComparison.Ordinal)) decoded = decoded[2..];
            if (decoded.EndsWith("ZZ", StringComparison.Ordinal)) decoded = decoded[..^2];
            decoded = decoded.Trim();
            if (decoded.Length == 0)
                return Task.FromResult(ResolveResult.Fail(Name, "迅雷链接内容为空"));

            var inner = decoded.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? DownloadKind.Magnet
                : decoded.Contains(".torrent", StringComparison.OrdinalIgnoreCase) ? DownloadKind.Torrent : DownloadKind.File;

            return Task.FromResult(new ResolveResult
            {
                Success = true,
                Url = decoded,
                Kind = inner,
                PreferAria2 = true,
                PreferYtDlp = false,
                ProviderName = Name,
                Message = "已从迅雷链接解出真实地址"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ResolveResult.Fail(Name, "迅雷链接解码失败：" + ex.Message));
        }
    }
}

/// <summary>ed2k / 电驴链接：aria2 不支持该协议，明确告知用户而不是静默失败。</summary>
public sealed class Ed2kProvider : ILinkProvider
{
    public string Name => "Ed2k";
    public string DisplayName => "电驴链接";
    public int Priority => 55;
    public bool RequiresCredential => false;

    public bool CanHandle(string url) => url.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase);

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
        => Task.FromResult(ResolveResult.Fail(Name,
            "aria2 内核不支持 ed2k/电驴协议。如需下载请安装 eMule 类工具，或把资源换成磁力链接。"));
}

/// <summary>
/// 视频网站页面（B站 / YouTube / 抖音 / 快手 / 小红书 / 微博 / 优酷 / 爱奇艺…）。
/// 不自己逆向签名，统一交给 yt-dlp——它有 1800+ 站点的持续维护的 extractor。
/// </summary>
public sealed class VideoSiteProvider : ILinkProvider
{
    public string Name => "VideoSite";
    public string DisplayName => "视频网站";
    public int Priority => 40;
    public bool RequiresCredential => false;

    public static readonly (string Host, string Site, string Display)[] Sites =
    {
        ("bilibili.com", "bilibili", "哔哩哔哩"),
        ("b23.tv", "bilibili", "哔哩哔哩"),
        ("youtube.com", "youtube", "YouTube"),
        ("youtu.be", "youtube", "YouTube"),
        ("douyin.com", "douyin", "抖音"),
        ("iesdouyin.com", "douyin", "抖音"),
        ("kuaishou.com", "kuaishou", "快手"),
        ("xiaohongshu.com", "xiaohongshu", "小红书"),
        ("xhslink.com", "xiaohongshu", "小红书"),
        ("weibo.com", "weibo", "微博"),
        ("youku.com", "youku", "优酷"),
        ("iqiyi.com", "iqiyi", "爱奇艺"),
        ("v.qq.com", "tencent", "腾讯视频"),
        ("douban.com", "douban", "豆瓣"),
        ("twitter.com", "twitter", "X/Twitter"),
        ("x.com", "twitter", "X/Twitter"),
        ("instagram.com", "instagram", "Instagram"),
        ("tiktok.com", "tiktok", "TikTok"),
        ("vimeo.com", "vimeo", "Vimeo"),
        ("twitch.tv", "twitch", "Twitch")
    };

    public bool CanHandle(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        // 明确是媒体文件 / 种子 / 播放列表的链接交给"通用直链"处理，避免被站点规则抢走
        var path = url;
        try { path = new Uri(url).AbsolutePath; } catch { /* 保持原样 */ }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".m3u8" or ".mpd" or ".ts" or ".m4s" or ".mp4" or ".flv" or ".mkv"
            or ".webm" or ".torrent" or ".mp3" or ".m4a" or ".flac") return false;

        return TryMatchSite(url) is not null;
    }

    public static string? TryMatchSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        foreach (var (h, site, _) in Sites)
        {
            if (host == h || host.EndsWith("." + h, StringComparison.Ordinal)) return site;
        }
        return null;
    }

    public static string? DisplayNameOf(string site) =>
        Sites.FirstOrDefault(s => s.Site == site).Display;

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var site = TryMatchSite(url);
        return Task.FromResult(new ResolveResult
        {
            Success = true,
            Url = url,
            Kind = DownloadKind.Video,
            PreferYtDlp = true,
            PreferAria2 = false,
            Site = site,
            ProviderName = Name,
            Message = site is null ? "交给 yt-dlp 解析" : $"识别为{DisplayNameOf(site)}页面，交给 yt-dlp 解析"
        });
    }
}

/// <summary>
/// 用户自定义规则（%AppData%\YunsuoDownloader\rules.json）。
/// 让用户不改代码也能修好失效的网盘解析：把分享链接重写成直链。
/// </summary>
public sealed class RuleBasedProvider : ILinkProvider
{
    public string Name => "UserRule";
    public string DisplayName => "自定义规则";
    public int Priority => 20;
    public bool RequiresCredential => false;

    private readonly List<Rule> _rules = new();

    public sealed class Rule
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        /// <summary>匹配链接的正则。</summary>
        public string Pattern { get; set; } = string.Empty;
        /// <summary>重写结果，可用 $1 $2 引用捕获组。</summary>
        public string? Replace { get; set; }
        /// <summary>或者直接给一个固定直链。</summary>
        public string? DirectUrl { get; set; }
        public string? Referer { get; set; }
        public string? UserAgent { get; set; }
        public string? FileName { get; set; }
        /// <summary>Kind: File / Video / Hls</summary>
        public string? Kind { get; set; }
        public bool PreferYtDlp { get; set; }
    }

    public void Load(IEnumerable<Rule> rules)
    {
        _rules.Clear();
        _rules.AddRange(rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)));
    }

    public int RuleCount => _rules.Count;

    public bool CanHandle(string url) => FindRule(url) is not null;

    private Rule? FindRule(string url)
    {
        foreach (var r in _rules)
        {
            try { if (System.Text.RegularExpressions.Regex.IsMatch(url, r.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return r; }
            catch { /* 规则写错时跳过 */ }
        }
        return null;
    }

    public Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var rule = FindRule(url);
        if (rule is null) return Task.FromResult(ResolveResult.Fail(Name, "没有匹配的自定义规则"));

        string? target = null;
        var source = rule.Replace ?? rule.DirectUrl;
        if (!string.IsNullOrWhiteSpace(source))
        {
            try
            {
                // 允许在 Replace / DirectUrl 里用 $1 $2 引用 Pattern 的捕获组
                target = System.Text.RegularExpressions.Regex.Replace(
                    url, rule.Pattern, source,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // 模式没有匹配到整串时，也支持直接使用固定直链
                if (string.Equals(target, url, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(rule.DirectUrl))
                    target = rule.DirectUrl;
            }
            catch (Exception ex)
            {
                return Task.FromResult(ResolveResult.Fail(Name, $"规则「{rule.Name}」正则替换失败：{ex.Message}"));
            }
        }
        if (string.IsNullOrWhiteSpace(target))
            return Task.FromResult(ResolveResult.Fail(Name, $"规则「{rule.Name}」没有产出可用地址"));

        var kind = rule.Kind?.ToLowerInvariant() switch
        {
            "video" => DownloadKind.Video,
            "hls" => DownloadKind.Hls,
            _ => DownloadKind.File
        };

        return Task.FromResult(new ResolveResult
        {
            Success = true,
            Url = target,
            Kind = kind,
            FileName = rule.FileName,
            Referer = rule.Referer,
            UserAgent = rule.UserAgent,
            PreferYtDlp = rule.PreferYtDlp || kind == DownloadKind.Hls,
            PreferAria2 = !rule.PreferYtDlp,
            ProviderName = $"{Name}:{rule.Name}",
            Message = $"命中自定义规则「{rule.Name}」"
        });
    }
}
