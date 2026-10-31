using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YunsuoDownloader.Core.Pan.Providers;

/// <summary>
/// 百度网盘（pan.baidu.com）分享解析。
///
/// 百度是四家里最麻烦的：
///   1) 分享页本身是 HTML，要从中抠出 <c>shareid</c> / <c>share_uk</c> / <c>bdstoken</c>；
///   2) 有提取码的分享必须先 <c>POST /share/verify</c>，让服务器下发 <c>BDCLND</c> Cookie，
///      否则 <c>/share/list</c> 一律返回 <c>errno=9019 (need verify)</c>；
///   3) 取直链的 <c>/api/sharedownload</c> 要带 <c>sign</c> + <c>timestamp</c>，
///      而且需要含 <c>BDUSS</c> 的登录 Cookie 才稳定。
/// </summary>
public sealed class BaiduPanProvider : IPanProvider
{
    private const string Host = "https://pan.baidu.com";
    private const int AppId = 250528;

    private static readonly Regex Rx = new(
        @"https?://pan\.baidu\.com/(?:s/|share/init\?(?:[^#]*&)?surl=)(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxShareId = new(@"""shareid""\s*:\s*""?(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxShareIdAlt = new(@"shareid=(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxUk = new(@"""share_uk""\s*:\s*""(\d+)""", RegexOptions.Compiled);
    private static readonly Regex RxUkAlt = new(@"""uk""\s*:\s*""?(\d+)""?", RegexOptions.Compiled);
    private static readonly Regex RxBdstoken = new(@"""bdstoken""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    public PanKind Kind => PanKind.Baidu;
    public string Id => "Baidu";
    public string DisplayName => "百度网盘";
    public string LoginUrl => "https://pan.baidu.com/";
    public string CookieDomain => "pan.baidu.com";
    public bool RequiresLogin => true;
    public string RootId => "/";
    public string Hint => "建议先登录百度网盘（未登录时列目录经常被拒）；有提取码的分享要填提取码。";

    public bool CanHandle(string url) => !string.IsNullOrWhiteSpace(url) && Rx.IsMatch(url);

    public PanShare? ParseShare(string url)
    {
        var m = Rx.Match(url);
        if (!m.Success) return null;
        return new PanShare
        {
            Kind = Kind,
            ProviderId = Id,
            DisplayName = DisplayName,
            ShareId = m.Groups["id"].Value,
            Password = PanUrl.ExtractPassword(url),
            OriginalUrl = url
        };
    }

    public async Task<PanListResult> ListAsync(PanShare share, string dirId, PanBrowser browser,
        CancellationToken ct)
    {
        var result = new PanListResult { ParentId = dirId };
        var headers = BaseHeaders(share);

        // ---------- 1) 分享页 -> shareid / uk / bdstoken ----------
        await LoadSharePageAsync(share, browser, headers, result, ct).ConfigureAwait(false);
        if (result.NeedPassword || !string.IsNullOrEmpty(result.Error)) return result;

        var shareId = share.Get("shareid");
        var uk = share.Get("uk");
        if (string.IsNullOrWhiteSpace(shareId) || string.IsNullOrWhiteSpace(uk))
        {
            result.Error =
                "百度分享页里解析不到 shareid / uk。\n" +
                "常见原因：① 分享需要提取码；② Cookie 缺失或已过期（必须含 BDUSS）；③ 百度改了分享页结构。\n" +
                "可以点「诊断」看分享页的真实返回，或先登录百度网盘再试。";
            return result;
        }

        // ---------- 2) 列目录（9019 就补一次 verify 再来）----------
        var list = await RequestListAsync(share, dirId, browser, headers, ct).ConfigureAwait(false);
        if (list.GetLong("errno") == 9019 && !string.IsNullOrEmpty(share.Password))
        {
            browser.Diag("/share/list 返回 9019（need verify），重新验证提取码");
            await VerifyAsync(share, browser, headers, result, ct).ConfigureAwait(false);
            if (result.NeedPassword) return result;
            list = await RequestListAsync(share, dirId, browser, headers, ct).ConfigureAwait(false);
        }

        var errno = list.GetLong("errno");
        if (errno != 0)
        {
            if (errno == 9019)
            {
                result.NeedPassword = true;
                result.Error = string.IsNullOrEmpty(share.Password)
                    ? "这个百度分享需要提取码（接口返回 errno=9019），请输入后重试。"
                    : "百度仍然要求验证分享：提取码可能不对，或 Cookie 已过期。";
            }
            else
            {
                // 百度会把真实原因放在 show_msg 里（如"来晚啦，该分享已被取消"），
                // 配合 errno 一起给人话，比甩一个 errno 数字有用得多。
                var showMsg = list.Get("show_msg");
                result.Error = $"百度网盘解析失败：{ExplainErrno((int)errno)}（errno={errno}）" +
                               (string.IsNullOrWhiteSpace(showMsg) ? $"：{list.Snippet}" : $"\n百度原话：{showMsg}");
            }
            return result;
        }

        if (list.Root is { } root)
        {
            PanJson.ForEach(root, "list", item =>
            {
                var fsId = PanJson.Long(item, "fs_id");
                if (fsId == 0) return;
                var name = PanJson.Str(item, "server_filename") ?? fsId.ToString();
                var entry = new PanEntry
                {
                    Id = fsId.ToString(),
                    Name = name,
                    IsFolder = PanJson.Long(item, "isdir") == 1,
                    Size = PanJson.Long(item, "size"),
                    ModifiedAt = ParseUnix(PanJson.Long(item, "server_mtime")),
                };
                entry.Extra["path"] = PanJson.Str(item, "path") ?? $"/{name}";
                result.Entries.Add(entry);
            });
        }

        result.Ok = true;
        result.ShareTitle = share.Title ?? share.ShareId;
        return result;
    }

    public async Task<PanDownloadLink> GetDownloadAsync(PanShare share, PanEntry entry, PanBrowser browser,
        CancellationToken ct)
    {
        var headers = BaseHeaders(share);
        var shareId = share.Get("shareid");
        var uk = share.Get("uk");
        if (string.IsNullOrWhiteSpace(shareId) || string.IsNullOrWhiteSpace(uk))
            return PanDownloadLink.Fail("分享信息不完整（shareid / uk 丢失），请重新解析一次。");

        if (!long.TryParse(entry.Id, out var fsId) || fsId == 0)
            return PanDownloadLink.Fail("这个条目没有文件 ID，无法取直链。");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rand = RandomAlnum(8);
        var sign = Md5($"app_id={AppId}&timestamp={ts}&rand={rand}&uid={uk}&sign_type=1");

        var url = $"{Host}/api/sharedownload?sign={sign}&timestamp={ts}" +
                  $"&channel=chunlei&web=1&app_id={AppId}&clienttype=0" +
                  Optional("bdstoken", share.Get("bdstoken"));

        var sekey = browser.GetCookie("BDCLND") ?? string.Empty;
        var extra = "{\"sekey\":\"" + Uri.EscapeDataString(sekey) + "\"}";
        var form = "encrypt=0&product=share&type=dlink" +
                   $"&uk={uk}&primaryid={shareId}&fid_list=%5B{fsId}%5D" +
                   "&extra=" + Uri.EscapeDataString(extra);

        var resp = await browser.PostFormAsync(url, form, headers, ct).ConfigureAwait(false);

        var errno = resp.GetLong("errno");
        if (errno != 0)
        {
            var known = errno switch
            {
                -9 => "Cookie 无效或已过期（需含 BDUSS）",
                112 => "该分享已失效",
                113 => "分享已被取消",
                118 => "该文件已被删除",
                _ => "接口返回了错误"
            };
            return PanDownloadLink.Fail(
                $"百度取直链失败 errno={errno}（{known}）。\n" +
                "百度的 sharedownload 签名算法与网页端版本绑定，若持续失败说明算法已更新。\n" +
                "可以用浏览器插件在百度网盘网页上直接嗅探下载地址作为替代。");
        }

        var dlink = resp.Get("list", "0", "dlink");
        if (string.IsNullOrWhiteSpace(dlink))
            return PanDownloadLink.Fail("百度接口没有返回 dlink（可能需要会员，或该文件被限制）。");

        return new PanDownloadLink
        {
            Ok = true,
            Url = dlink,
            FileName = entry.Name,
            Size = entry.Size,
            Cookies = browser.CookieHeader.Length > 0 ? browser.CookieHeader : null,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Referer"] = "https://pan.baidu.com/",
                ["User-Agent"] = PanBrowser.DesktopUserAgent
            },
            Message = $"百度网盘直链已获取：{entry.Name}"
        };
    }

    // ------------------------------------------------------------------ 内部
    private async Task LoadSharePageAsync(PanShare share, PanBrowser browser,
        List<KeyValuePair<string, string>> headers, PanListResult result, CancellationToken ct)
    {
        var pageUrl = $"{Host}/s/{share.ShareId}" +
                      (string.IsNullOrEmpty(share.Password) ? "" : $"?pwd={Uri.EscapeDataString(share.Password)}");
        var page = await browser.GetAsync(pageUrl, headers, ct).ConfigureAwait(false);

        if (page.Status == -1)
        {
            result.Error = "连不上百度网盘：" + page.NetworkError;
            return;
        }

        // 分享不存在时百度给的是一个 404 错误页（"页面不存在"），
        // 不能当成"需要提取码"——那会让用户一直去填一个不存在的密码。
        if (page.Status == 404 ||
            page.Body.Contains("页面不存在", StringComparison.Ordinal) ||
            page.Body.Contains("你访问的页面不存在", StringComparison.Ordinal))
        {
            result.Error = "百度提示这个分享不存在或已失效（分享页 404）。\n" +
                           "请先在浏览器里打开链接，确认还能看到文件；链接失效的话需要分享方重新分享。";
            return;
        }

        ParseSharePage(share, page.Body, browser);

        var needVerify = page.Body.Contains("请输入提取码", StringComparison.Ordinal)
                         || page.Body.Contains("share/verify", StringComparison.Ordinal)
                         || string.IsNullOrWhiteSpace(share.Get("shareid"))
                         || string.IsNullOrWhiteSpace(share.Get("uk"));

        if (!needVerify) return;

        if (string.IsNullOrEmpty(share.Password))
        {
            result.NeedPassword = true;
            result.Error = "这个百度分享需要提取码，请输入后重试。";
            return;
        }

        await VerifyAsync(share, browser, headers, result, ct).ConfigureAwait(false);
        if (result.NeedPassword) return;

        // 验证成功后再抓一次分享页，把 shareid / uk 补齐
        var again = await browser.GetAsync(pageUrl, headers, ct).ConfigureAwait(false);
        ParseSharePage(share, again.Body, browser);
    }

    private static void ParseSharePage(PanShare share, string html, PanBrowser browser)
    {
        share.Set("shareid", First(RxShareId, html) ?? First(RxShareIdAlt, html));
        share.Set("uk", First(RxUk, html) ?? First(RxUkAlt, html));
        share.Set("bdstoken", First(RxBdstoken, html));

        var title = Regex.Match(html, @"<title>(?<t>[^<]+)</title>", RegexOptions.IgnoreCase);
        if (title.Success && share.Title is null)
        {
            var t = title.Groups["t"].Value.Trim();
            if (t.Length > 0 && !t.Contains("百度网盘", StringComparison.Ordinal)) share.Title = t;
        }

        browser.Diag($"分享页解析：shareid={share.Get("shareid") ?? "无"} " +
                     $"uk={share.Get("uk") ?? "无"} bdstoken={(share.Get("bdstoken") is null ? "无" : "有")}");
    }

    private async Task VerifyAsync(PanShare share, PanBrowser browser,
        List<KeyValuePair<string, string>> headers, PanListResult result, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = $"{Host}/share/verify?surl={Uri.EscapeDataString(share.ShareId)}&t={ts}" +
                  $"&channel=chunlei&web=1&app_id={AppId}&clienttype=0";

        var resp = await browser.PostFormAsync(url,
            "pwd=" + Uri.EscapeDataString(share.Password ?? string.Empty), headers, ct).ConfigureAwait(false);

        var errno = resp.GetLong("errno");
        if (errno == 0) return;

        result.NeedPassword = true;
        result.WrongPassword = errno != 105;
        result.Error = errno switch
        {
            105 => "百度提示分享不存在或已失效（errno=105）。",
            _ => string.IsNullOrEmpty(share.Password)
                ? "这个百度分享需要提取码，请输入后重试。"
                : $"百度提取码不正确，或登录态已过期（errno={errno}）。"
        };
    }

    private async Task<PanResponse> RequestListAsync(PanShare share, string dirId, PanBrowser browser,
        List<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = $"{Host}/share/list?uk={share.Get("uk")}&shareid={share.Get("shareid")}" +
                  "&order=other&desc=1&showempty=0&web=1&page=1&num=100" +
                  $"&dir={Uri.EscapeDataString(string.IsNullOrWhiteSpace(dirId) ? "/" : dirId)}" +
                  $"&t={ts}&channel=chunlei&app_id={AppId}&clienttype=0" +
                  Optional("bdstoken", share.Get("bdstoken"));
        return await browser.GetAsync(url, headers, ct).ConfigureAwait(false);
    }

    private static List<KeyValuePair<string, string>> BaseHeaders(PanShare share) => new()
    {
        new("Referer", $"{Host}/s/{share.ShareId}"),
        new("Origin", Host)
    };

    /// <summary>把百度的 errno 翻成人话（实测常见值）。</summary>
    private static string ExplainErrno(int errno) => errno switch
    {
        -9 => "Cookie 无效或已过期（需要含 BDUSS 的登录态）",
        -7 => "链接不存在或已失效",
        2 => "链接不存在或已失效",
        105 => "分享不存在",
        112 => "分享已过期",
        113 => "分享已被取消",
        -21 => "分享已被取消",
        118 => "文件已被删除",
        110 => "分享已被封禁",
        _ => "接口返回错误"
    };

    private static string Optional(string key, string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : $"&{key}={Uri.EscapeDataString(value)}";

    private static string? First(Regex rx, string text)
    {
        var m = rx.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string RandomAlnum(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    private static string Md5(string text)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static DateTime? ParseUnix(long seconds)
        => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;
}
