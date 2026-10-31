using System.Text.RegularExpressions;

namespace YunsuoDownloader.Core.Pan.Providers;

/// <summary>
/// 阿里云盘（alipan.com / aliyundrive.com）分享解析。
///
/// 2026-10 实测到的真实链路（用 Chrome CDP 抓的网页请求）：
///   1) POST {api}/adrive/v3/share_link/get_share_by_anonymous?share_id=…   分享信息（匿名）
///   2) POST {api}/v2/share_link/get_share_token   {share_id, share_pwd}    拿 share_token
///   3) POST {api}/adrive/v2/file/list_by_share    + x-share-token          列目录（新接口）
///   4) 取直链：/v2/file/get_share_link_download_url 已经被官方下线
///      —— 现在一律返回 <c>410 Gone</c>（空正文），说明阿里已经关掉匿名直链。
///      阿里云盘现在要求「登录 → 转存到自己网盘 → 用 access token 取直链」，
///      浏览器里的 access token 不是 Cookie，程序拿不到，所以 1.1 只做到"浏览 + 复制文件列表"。
///
/// 注意域名：网页版用的是 <c>api.aliyundrive.com</c>；
/// <c>api.alipan.com</c> 上 /v2/share_link/get_share_token 会返回 "uri not exist"，
/// 这是最容易踩的坑（一开始就踩了）。
/// </summary>
public sealed class AliyunPanProvider : IPanProvider
{
    private const string Api = "https://api.aliyundrive.com";
    private const string ApiAlt = "https://api.alipan.com";

    private static readonly Regex Rx = new(
        @"https?://(?:www\.)?(?:aliyundrive|alipan)\.com/s/(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PanKind Kind => PanKind.Aliyun;
    public string Id => "Aliyun";
    public string DisplayName => "阿里云盘";
    public string LoginUrl => "https://www.alipan.com/";
    public string CookieDomain => "www.alipan.com";
    public bool RequiresLogin => false;
    public string RootId => "root";
    public string Hint => "可以匿名浏览分享里的文件；阿里已关闭匿名直链，下载需要先转存到自己网盘。";

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

        // ---------- 1) 分享元信息（标题 / 是否有密码 / 是否还存在）----------
        if (string.IsNullOrWhiteSpace(share.Title))
        {
            var info = await browser.PostJsonAsync(
                $"{Api}/adrive/v3/share_link/get_share_by_anonymous?share_id={Uri.EscapeDataString(share.ShareId)}",
                new { share_id = share.ShareId }, headers, ct).ConfigureAwait(false);

            var code = info.Get("code");
            if (string.Equals(code, "NotFound.ShareLink", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "阿里云盘提示「分享不存在」：链接可能已失效、被取消，或 ID 抄错了。\n" +
                               "请先在浏览器里打开这个分享，确认还能看到文件。";
                return result;
            }

            share.Title = info.Get("share_name") ?? info.Get("share_title") ?? info.Get("display_name");

            if (PanJson.Bool(info.Root, "has_pwd") && string.IsNullOrEmpty(share.Password))
            {
                result.NeedPassword = true;
                result.Error = "这个阿里云盘分享设置了访问密码，请输入后重试。";
                return result;
            }
        }

        // ---------- 2) share_token ----------
        var token = share.Get("share_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = await FetchTokenAsync(share, browser, headers, result, ct).ConfigureAwait(false);
            if (result.NeedPassword) return result;
            if (string.IsNullOrWhiteSpace(token))
            {
                result.Error ??= "阿里云盘没有拿到 share_token（接口可能改版，点「诊断」看每一步的真实返回）。";
                return result;
            }
            share.Set("share_token", token);
        }

        var listHeaders = new List<KeyValuePair<string, string>>(headers)
        {
            new("x-share-token", token)
        };

        var body = new
        {
            share_id = share.ShareId,
            parent_file_id = dirId,
            limit = 100,
            order_by = "name",
            order_direction = "ASC",
            image_thumbnail_process = "image/resize,w_160/format/jpeg",
            video_thumbnail_process = "video/snapshot,t_1000,f_jpg,ar_auto,w_300"
        };

        // 新接口 list_by_share（网页版在用）；老版本退回 /adrive/v3/file/list
        var list = await browser.PostJsonAsync($"{Api}/adrive/v2/file/list_by_share", body, listHeaders, ct)
            .ConfigureAwait(false);

        if (list.Root is null || PanJson.At(list.Root, "items") is null)
        {
            browser.Diag("list_by_share 不可用，改试 /adrive/v3/file/list");
            list = await browser.PostJsonAsync($"{Api}/adrive/v3/file/list", body, listHeaders, ct)
                .ConfigureAwait(false);
        }

        var listCode = list.Get("code");
        if (!string.IsNullOrWhiteSpace(listCode))
        {
            if (listCode.Contains("TokenExpired", StringComparison.OrdinalIgnoreCase) ||
                listCode.Contains("ShareLinkToken", StringComparison.OrdinalIgnoreCase))
            {
                share.Set("share_token", null);
                result.Error = "阿里云盘的分享令牌过期了，请点「重新解析」再试一次。";
                return result;
            }
            if (IsPasswordProblem(list))
            {
                result.NeedPassword = true;
                result.Error = "这个阿里云盘分享需要正确的访问密码。";
                return result;
            }
            result.Error = $"阿里云盘列目录失败：{listCode} {list.Get("message")}\n{list.Snippet}";
            return result;
        }

        if (list.Root is not { } root)
        {
            result.Error = "阿里云盘返回的不是 JSON（可能被风控拦了）：\n" + list.Snippet;
            return result;
        }

        PanJson.ForEach(root, "items", item =>
        {
            var fileId = PanJson.Str(item, "file_id");
            if (string.IsNullOrWhiteSpace(fileId)) return;
            var type = PanJson.Str(item, "type") ?? "file";
            result.Entries.Add(new PanEntry
            {
                Id = fileId,
                Name = PanJson.Str(item, "name") ?? fileId,
                IsFolder = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase),
                Size = PanJson.Long(item, "size"),
                ModifiedAt = ParseTime(PanJson.Str(item, "updated_at") ?? PanJson.Str(item, "created_at"))
            });
        });

        result.Ok = true;
        result.ShareTitle = share.Title;
        return result;
    }

    public async Task<PanDownloadLink> GetDownloadAsync(PanShare share, PanEntry entry, PanBrowser browser,
        CancellationToken ct)
    {
        var token = share.Get("share_token");
        if (string.IsNullOrWhiteSpace(token))
            return PanDownloadLink.Fail("分享令牌已失效，请重新解析一次。");

        var headers = new List<KeyValuePair<string, string>>(BaseHeaders(share))
        {
            new("x-share-token", token)
        };

        var resp = await browser.PostJsonAsync($"{Api}/v2/file/get_share_link_download_url", new
        {
            share_id = share.ShareId,
            file_id = entry.Id,
            expire_sec = 14400
        }, headers, ct).ConfigureAwait(false);

        var url = resp.Get("url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return new PanDownloadLink
            {
                Ok = true,
                Url = url,
                FileName = entry.Name,
                Size = entry.Size,
                Cookies = browser.CookieHeader.Length > 0 ? browser.CookieHeader : null,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Referer"] = "https://www.alipan.com/",
                    ["User-Agent"] = PanBrowser.DesktopUserAgent
                },
                Message = $"阿里云盘直链已获取：{entry.Name}"
            };
        }

        // 410 / 404 = 官方下线了匿名直链接口（2026-10 实测就是 410 Gone，正文为空）
        if (resp.Status is 410 or 404)
        {
            return PanDownloadLink.Fail(
                "阿里云盘已经关闭「匿名分享直链」接口（服务端返回 410 Gone）。\n" +
                "现在必须：登录阿里云盘 → 把这个文件转存到自己网盘 → 再从自己网盘下载。\n\n" +
                "可以这样做：\n" +
                "1) 在浏览器里打开这个分享，点「保存到我的网盘」；\n" +
                "2) 打开本程序的「浏览器插件」页，在阿里云盘网页上点插件的下载按钮，让它直接嗅探下载地址；\n" +
                "3) 或者先把文件转存，再用自己的账号在网页上下载。");
        }

        var code = resp.Get("code") ?? resp.Get("message") ?? resp.Snippet;
        return PanDownloadLink.Fail(
            $"阿里云盘没有返回下载地址（{code}）。\n" +
            "常见原因：分享方关闭了「允许下载」、文件被和谐，或接口已改版。");
    }

    // ------------------------------------------------------------------ 内部
    private async Task<string?> FetchTokenAsync(PanShare share, PanBrowser browser,
        List<KeyValuePair<string, string>> headers, PanListResult result, CancellationToken ct)
    {
        var body = new { share_id = share.ShareId, share_pwd = share.Password ?? string.Empty };

        var resp = await browser.PostJsonAsync($"{Api}/v2/share_link/get_share_token", body, headers, ct)
            .ConfigureAwait(false);

        // api.aliyundrive.com 上没有这个接口时（个别地区/版本），退到 api.alipan.com
        if (resp.Status == 404 || (resp.Body.Contains("not exist", StringComparison.OrdinalIgnoreCase)))
        {
            browser.Diag("api.aliyundrive.com 没有 get_share_token，改试 api.alipan.com");
            resp = await browser.PostJsonAsync($"{ApiAlt}/v2/share_link/get_share_token", body, headers, ct)
                .ConfigureAwait(false);
        }

        var token = resp.Get("share_token");
        if (!string.IsNullOrWhiteSpace(token)) return token;

        if (IsPasswordProblem(resp))
        {
            result.NeedPassword = true;
            result.WrongPassword = !string.IsNullOrEmpty(share.Password);
            result.Error = result.WrongPassword
                ? "访问密码不正确，请重新输入。"
                : "这个阿里云盘分享设置了访问密码，请输入后重试。";
            return null;
        }

        var code = resp.Get("code") ?? "?";
        var message = resp.Get("message");
        result.Error = code == "NotFound.ShareLink"
            ? "这个阿里云盘分享不存在或已失效。"
            : $"阿里云盘获取分享令牌失败（{code}）：{message}\n{resp.Snippet}";
        return null;
    }

    private static List<KeyValuePair<string, string>> BaseHeaders(PanShare share) => new()
    {
        new("Referer", $"https://www.alipan.com/s/{share.ShareId}"),
        new("Origin", "https://www.alipan.com"),
        // 网页版带这个头才会走"分享页"分支
        new("x-canary", "client=web,app=share,version=v2.3.2")
    };

    private static bool IsPasswordProblem(PanResponse resp)
    {
        var text = (resp.Get("code") ?? "") + " " + (resp.Get("message") ?? "") + " " + resp.Snippet;
        return text.Contains("password", StringComparison.OrdinalIgnoreCase)
               || text.Contains("密码", StringComparison.Ordinal)
               || text.Contains("ShareLinkPassword", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? ParseTime(string? text)
        => DateTime.TryParse(text, out var t) ? t : null;
}
