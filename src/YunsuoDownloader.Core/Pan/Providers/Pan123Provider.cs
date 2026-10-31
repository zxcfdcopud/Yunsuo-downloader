using System.Text;
using System.Text.RegularExpressions;

namespace YunsuoDownloader.Core.Pan.Providers;

/// <summary>
/// 123 云盘（123pan.com / 123684.com / 123865.com …）分享解析。
///
/// 123 的分享页域名有好几个镜像，接口前缀也跟着变（<c>/api</c> 或 <c>/b/api</c>），
/// 所以这里都用"按分享链接自己的域名去试，再退回镜像域名"的方式，避免写死一个域名后集体失效。
/// </summary>
public sealed class Pan123Provider : IPanProvider
{
    private static readonly Regex Rx = new(
        @"https?://(?:www\.)?(?:123pan|123684|123865|123912|123592)\.(?:com|cn)/s/(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>兜底域名：分享域名取不到接口时按这个试。</summary>
    private const string FallbackHost = "https://www.123865.com";

    public PanKind Kind => PanKind.Pan123;
    public string Id => "Pan123";
    public string DisplayName => "123 云盘";
    public string LoginUrl => "https://www.123pan.com/";
    public string CookieDomain => "www.123pan.com";
    public bool RequiresLogin => false;
    public string RootId => "0";
    public string Hint => "公开分享可以直接解析；设了提取码的分享需要填提取码。";

    public bool CanHandle(string url) => !string.IsNullOrWhiteSpace(url) && Rx.IsMatch(url);

    public PanShare? ParseShare(string url)
    {
        var m = Rx.Match(url);
        if (!m.Success) return null;
        var share = new PanShare
        {
            Kind = Kind,
            ProviderId = Id,
            DisplayName = DisplayName,
            ShareId = m.Groups["id"].Value,
            Password = PanUrl.ExtractPassword(url),
            OriginalUrl = url
        };

        // 记住分享页的域名，后面所有接口都按它拼
        var host = PanUrl.Host(url);
        if (!string.IsNullOrEmpty(host)) share.Set("host", "https://" + host);
        return share;
    }

    public async Task<PanListResult> ListAsync(PanShare share, string dirId, PanBrowser browser,
        CancellationToken ct)
    {
        var result = new PanListResult { ParentId = dirId };
        var headers = BaseHeaders(share);

        var query = "?limit=100&next=1&orderBy=file_name&orderDirection=asc" +
                    $"&shareKey={Uri.EscapeDataString(share.ShareId)}" +
                    $"&ParentFileId={Uri.EscapeDataString(dirId)}&Page=1" +
                    "&event=homeShareDetail&operateType=1&RequestSource=1";
        if (!string.IsNullOrEmpty(share.Password))
            query += "&SharePwd=" + Uri.EscapeDataString(share.Password);

        PanResponse? resp = null;
        foreach (var baseUrl in CandidateBases(share))
        {
            foreach (var prefix in new[] { "/api", "/b/api" })
            {
                resp = await browser.GetAsync($"{baseUrl}{prefix}/share/get{query}", headers, ct)
                    .ConfigureAwait(false);
                if (resp.Status == 404) continue;
                // 拿到 JSON 就停（code 非 0 也停，那是业务错误，换接口没意义）
                if (resp.Root is not null) break;
            }
            if (resp?.Root is not null) break;
        }

        if (resp is null || resp.Root is null)
        {
            result.Error = "123 云盘接口没有返回 JSON（可能改版或被风控拦截）：\n" + (resp?.Snippet ?? "无响应");
            return result;
        }

        var code = resp.GetLong("code");
        var message = resp.Get("message") ?? string.Empty;

        if (code != 0)
        {
            if (IsPasswordProblem(resp))
            {
                result.NeedPassword = true;
                result.WrongPassword = !string.IsNullOrEmpty(share.Password);
                result.Error = result.WrongPassword ? "提取码不正确，请重新输入。" : "这个 123 云盘分享需要提取码，请输入后重试。";
                return result;
            }

            result.Error = code switch
            {
                5103 => "分享不存在或已失效。",
                5113 => "分享已被取消。",
                401 or 403 => "没有权限：这个分享需要在网页上登录后才能访问。",
                _ => $"123 云盘解析失败（code={code}）：{message}"
            };
            return result;
        }

        share.Title ??= resp.Get("data", "ShareName");

        if (PanJson.At(resp.Root, "data", "InfoList") is { } infoList)
        {
            foreach (var item in infoList.EnumerateArray())
            {
                var fileId = PanJson.Str(item, "FileId");
                if (string.IsNullOrWhiteSpace(fileId)) continue;

                var entry = new PanEntry
                {
                    Id = fileId,
                    Name = PanJson.Str(item, "FileName") ?? fileId,
                    IsFolder = PanJson.Long(item, "Type") == 1,
                    Size = PanJson.Long(item, "Size"),
                    ModifiedAt = ParseTime(PanJson.Str(item, "UpdateAt") ?? PanJson.Str(item, "CreateAt"))
                };

                // 取直链要回传这几个字段，先记下来
                foreach (var key in new[] { "Etag", "S3KeyFlag", "Type" })
                {
                    var v = PanJson.Str(item, key);
                    if (!string.IsNullOrWhiteSpace(v)) entry.Extra[key] = v;
                }

                var direct = PanJson.Str(item, "DownloadUrl");
                if (!string.IsNullOrWhiteSpace(direct)) entry.Extra["DownloadUrl"] = direct;

                result.Entries.Add(entry);
            }
        }

        result.Ok = true;
        result.ShareTitle = share.Title;
        return result;
    }

    public async Task<PanDownloadLink> GetDownloadAsync(PanShare share, PanEntry entry, PanBrowser browser,
        CancellationToken ct)
    {
        if (entry.Extra.TryGetValue("DownloadUrl", out var cached) && !string.IsNullOrWhiteSpace(cached))
            return Ok(entry, cached, browser);

        var headers = BaseHeaders(share);

        // 2026-10 实测：分享文件走「分享」接口就能匿名取直链；
        // /api/file/download_info 是"个人网盘"接口，未登录直接回 code=20101。
        // 分享接口要求的字段名是 S3keyFlag / Size / Etag（大小写敏感，缺一个就 400）。
        var body = new
        {
            shareKey = share.ShareId,
            fileId = long.TryParse(entry.Id, out var fid) ? fid : 0,
            isUseTransCode = true,
            type = 0,
            @event = "share_download",
            s3keyFlag = entry.Extra.GetValueOrDefault("S3KeyFlag"),
            size = entry.Size,
            etag = entry.Extra.GetValueOrDefault("Etag")
        };

        PanResponse? resp = null;
        foreach (var baseUrl in CandidateBases(share))
        {
            foreach (var prefix in new[] { "/api", "/b/api" })
            {
                resp = await browser.PostJsonAsync($"{baseUrl}{prefix}/share/download/info", body, headers, ct)
                    .ConfigureAwait(false);
                if (resp.Status == 404) continue;
                if (resp.Root is not null) break;
            }
            if (resp?.Root is not null) break;
        }

        var url = resp?.Get("data", "DownloadURL") ?? resp?.Get("data", "DownloadUrl")
                  ?? resp?.Get("data", "downloadUrl");
        if (string.IsNullOrWhiteSpace(url))
        {
            var code = resp?.GetLong("code") ?? -1;
            var message = resp?.Get("message") ?? resp?.Snippet ?? "无响应";

            // 未登录（取的是个人网盘接口）或文件被限制时给可操作的说明
            if (code is 20101 or 20102 or 401)
            {
                return PanDownloadLink.Fail(
                    $"123 云盘要求先登录才能取这个文件的直链（code={code}「{message}」）。\n" +
                    "请到「网盘解析」页点 123 云盘那一行的「登录」，登录后再试一次。");
            }

            return PanDownloadLink.Fail(
                $"123 云盘取直链失败（code={code}）：{message}\n" +
                "常见原因：分享方关闭了下载、文件被和谐，或接口改版。");
        }

        // 123 返回的 DownloadURL 不是文件本身，还要走两段跳转（见 ResolveChainAsync）
        var final = await ResolveChainAsync(url, share, browser, ct).ConfigureAwait(false);
        return Ok(entry, final, browser);
    }

    /// <summary>
    /// 把 123 的下载地址解开到真正的文件 URL。
    ///
    /// 2026-10 实测的跳转链（三段）：
    ///   ① <c>web-pro2.cjjd18.com/download-v2/?params=&lt;base64url&gt;</c>
    ///      —— 一个 JS 跳转页（返回 HTML），但 params 解码后就是下一跳，
    ///      所以不用执行 JS，直接 base64 解出来。
    ///   ② <c>guest-share-free-download-cdn.…</c>
    ///      —— 返回 HTTP 210 + JSON <c>{"data":{"redirect_url":"…"}}</c>（不是标准 3xx！）
    ///   ③ <c>*.pd1.cjjd19.com/…</c> —— 才是真正的文件（200 + octet-stream）。
    ///
    /// 只对"网关域名"发请求、只读 JSON，避免把 100 MB 的文件整个读进内存。
    /// </summary>
    private static async Task<string> ResolveChainAsync(string url, PanShare share, PanBrowser browser,
        CancellationToken ct)
    {
        var headers = BaseHeaders(share);

        for (var hop = 0; hop < 4; hop++)
        {
            // ① 跳转页：params 里就是下一跳
            var decoded = PanUrl.TryDecodeRedirectParams(url);
            if (decoded is not null)
            {
                browser.Diag($"123 跳转：解出 params -> {Shorten(decoded)}");
                url = decoded;
                continue;
            }

            if (!IsGateway(url)) return url;

            // ②/③ 网关：只读 JSON，不是 JSON 就说明已经到文件了
            var resp = await browser.GetAsync(url, headers, ct).ConfigureAwait(false);
            var next = resp.Get("data", "redirect_url") ?? resp.Get("redirect_url");
            if (string.IsNullOrWhiteSpace(next))
            {
                browser.Diag($"123 跳转：{Shorten(url)} 没有 redirect_url，按最终地址处理（HTTP {resp.Status}）");
                return url;
            }

            browser.Diag($"123 跳转：{Shorten(url)} -> {Shorten(next)}");
            url = next;
        }

        return url;
    }

    /// <summary>下载网关的域名特征（只对这些地址发 GET，避免误拉文件本体）。</summary>
    private static bool IsGateway(string url)
    {
        var host = PanUrl.Host(url);
        return host.Contains("download-v2", StringComparison.OrdinalIgnoreCase)
               || host.Contains("guest-share-free-download-cdn", StringComparison.OrdinalIgnoreCase)
               || host.Contains("123295.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/download-v2/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Shorten(string url) => url.Length <= 70 ? url : url[..70] + "…";

    // ------------------------------------------------------------------ 内部
    private static PanDownloadLink Ok(PanEntry entry, string url, PanBrowser browser) => new()
    {
        Ok = true,
        Url = url,
        FileName = entry.Name,
        Size = entry.Size,
        Cookies = browser.CookieHeader.Length > 0 ? browser.CookieHeader : null,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://www.123pan.com/",
            ["User-Agent"] = PanBrowser.DesktopUserAgent
        },
        Message = $"123 云盘直链已获取：{entry.Name}"
    };

    private static List<KeyValuePair<string, string>> BaseHeaders(PanShare share) => new()
    {
        new("Referer", share.OriginalUrl),
        new("Origin", share.Get("host") ?? "https://www.123pan.com"),
        new("platform", "web"),
        new("app-version", "3")
    };

    /// <summary>先试分享页自己的域名，再试兜底域名。</summary>
    private static IEnumerable<string> CandidateBases(PanShare share)
    {
        var own = share.Get("host");
        if (!string.IsNullOrWhiteSpace(own)) yield return own;
        if (!string.Equals(own, FallbackHost, StringComparison.OrdinalIgnoreCase)) yield return FallbackHost;
    }

    private static bool IsPasswordProblem(PanResponse resp)
    {
        var text = (resp.Get("message") ?? "") + " " + resp.Snippet;
        return text.Contains("密码", StringComparison.Ordinal)
               || text.Contains("passcode", StringComparison.OrdinalIgnoreCase)
               || text.Contains("SharePwd", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? ParseTime(string? text)
        => DateTime.TryParse(text, out var t) ? t : null;
}
