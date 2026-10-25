using System.Text.RegularExpressions;

namespace YunsuoDownloader.Core.Utils;

/// <summary>
/// 链接归一化：把「网页里嵌的播放器地址」还原成「内容页地址」。
///
/// 为什么需要：浏览器插件是按页面里实际的媒体/iframe 请求来嗅探的，
/// 于是哔哩哔哩这类站点送过来的常常是 <c>player.bilibili.com/player.html?bvid=BVxxx</c>
/// 这种 <b>内嵌播放器（iframe embed）</b> 地址，而不是 <c>www.bilibili.com/video/BVxxx</c>。
/// yt-dlp 只认内容页，拿到 embed 地址会直接报
/// <c>ERROR: Unsupported URL</c>，于是任务在下载前就失败了。
///
/// 这里在「解析」和「交给 yt-dlp」两个入口都做一次归一化，
/// 插件就不用改，用户从任何入口粘进来都能work。
/// </summary>
public static class UrlNormalizer
{
    // BV 号通常是 BV + 10 位（共 12 字符），但实测存在 BV + 9 位的旧/短号（例如 BV1fsJ6tEUJ），
    // 所以长度放宽成 8~12，宁可多认也不要漏掉把用户卡住。
    private static readonly Regex BvId = new(@"\b(BV[0-9A-Za-z]{8,12})\b", RegexOptions.Compiled);
    private static readonly Regex AvId = new(@"\bav(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>B 站内嵌播放器 / 移动端 / 短链等需要改写成内容页的域名。</summary>
    private static readonly string[] BilibiliEmbedHosts =
    {
        "player.bilibili.com",
        "www.player.bilibili.com",
        "m.bilibili.com"
    };

    /// <summary>
    /// 归一化下载链接。认不出来的原样返回（绝不乱改）。
    /// </summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;

        // ---------------------------------------------------------- 哔哩哔哩
        if (IsBilibili(uri.Host))
        {
            var rewritten = NormalizeBilibili(uri);
            if (rewritten is not null) return rewritten;
        }

        // ---------------------------------------------------------- YouTube 嵌入 / 短链
        if (IsHost(uri.Host, "youtube.com") || IsHost(uri.Host, "youtube-nocookie.com"))
        {
            if (uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
            {
                var id = uri.AbsolutePath.Substring("/embed/".Length).Trim('/');
                if (id.Length > 0) return "https://www.youtube.com/watch?v=" + id;
            }
        }
        if (IsHost(uri.Host, "youtu.be"))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (id.Length > 0) return "https://www.youtube.com/watch?v=" + id;
        }

        // ---------------------------------------------------------- 抖音
        // 抖音的"精选/推荐/搜索"等页面，视频 ID 放在 query 的 modal_id 里，
        // 例如 https://www.douyin.com/jingxuan?modal_id=7680497115004505396
        // yt-dlp 只认 https://www.douyin.com/video/<id>，所以必须改写，
        // 否则会直接报 "ERROR: Unsupported URL"（用户实际踩到的就是这个）。
        if (IsHost(uri.Host, "douyin.com"))
        {
            var modalId = QueryValue(uri, "modal_id");
            if (!string.IsNullOrEmpty(modalId) && Regex.IsMatch(modalId, @"^\d{10,}$"))
                return "https://www.douyin.com/video/" + modalId;

            // 已经是 /video/<id> 的形式：只清掉跟踪参数
            var vm = Regex.Match(uri.AbsolutePath, @"/video/(\d{10,})");
            if (vm.Success) return "https://www.douyin.com/video/" + vm.Groups[1].Value;
        }

        // 抖音分享短链/分享页 → 视频页
        if (IsHost(uri.Host, "iesdouyin.com") &&
            uri.AbsolutePath.Contains("/share/video/", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(uri.AbsolutePath, @"/share/video/(\d+)");
            if (m.Success) return "https://www.douyin.com/video/" + m.Groups[1].Value;
        }

        return trimmed;
    }

    private static bool IsBilibili(string host)
        => IsHost(host, "bilibili.com") || IsHost(host, "b23.tv");

    private static bool IsHost(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把各种 B 站地址统一成 <c>https://www.bilibili.com/video/BVxxxx</c>（或 /video/av123）。
    /// 认不出返回 null（交给调用方保持原样）。
    /// </summary>
    private static string? NormalizeBilibili(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        // 已经是内容页：只把 m.bilibili.com / 带一堆跟踪参数的情况收敛一下
        var contentMatch = Regex.Match(path, @"^/video/(BV[0-9A-Za-z]{8,12}|av\d+)", RegexOptions.IgnoreCase);
        if (contentMatch.Success)
        {
            var id = contentMatch.Groups[1].Value;
            // 保留分 P 参数，其它跟踪参数（spm_id_from / vd_source / from…）全部丢掉
            var page = QueryValue(uri, "p");
            var baseUrl = "https://www.bilibili.com/video/" + id;
            return string.IsNullOrEmpty(page) ? baseUrl : baseUrl + "?p=" + page;
        }

        // 内嵌播放器：player.bilibili.com/player.html?bvid=BVxxx&page=1&aid=...
        if (BilibiliEmbedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            var bvid = QueryValue(uri, "bvid");
            if (!string.IsNullOrEmpty(bvid) && BvId.IsMatch(bvid))
                return "https://www.bilibili.com/video/" + BvId.Match(bvid).Groups[1].Value +
                       PageSuffix(QueryValue(uri, "page"));

            var aid = QueryValue(uri, "aid");
            if (!string.IsNullOrEmpty(aid) && long.TryParse(aid, out _))
                return "https://www.bilibili.com/video/av" + aid + PageSuffix(QueryValue(uri, "page"));

            // 有的 embed 地址把 bvid 塞在别的参数里（例如 ?cid=..&bvid=..），最后再兜一次
            var anyBv = BvId.Match(uri.Query);
            if (anyBv.Success)
                return "https://www.bilibili.com/video/" + anyBv.Groups[1].Value +
                       PageSuffix(QueryValue(uri, "page"));

            var anyAv = AvId.Match(uri.Query);
            if (anyAv.Success)
                return "https://www.bilibili.com/video/av" + anyAv.Groups[1].Value +
                       PageSuffix(QueryValue(uri, "page"));
        }

        // b23.tv 短链：必须联网才能解开，这里不动，交给 yt-dlp 自己跳转
        if (host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) return null;

        // 其它 B 站页面（番剧 / 直播 / 稍后再看…）交给 yt-dlp 自己判断
        return null;
    }

    private static string PageSuffix(string? page)
        => string.IsNullOrEmpty(page) || page == "1" ? string.Empty : "?p=" + page;

    private static string? QueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (query.Length <= 1) return null;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var k = Uri.UnescapeDataString(pair.Substring(0, eq));
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return null;
    }
}
