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
        var body = new
        {
            etag = entry.Extra.GetValueOrDefault("Etag"),
            fileId = long.TryParse(entry.Id, out var fid) ? fid : 0,
            fileName = Path.GetFileNameWithoutExtension(entry.Name),
            type = 0,
            s3keyFlag = entry.Extra.GetValueOrDefault("S3KeyFlag"),
            size = entry.Size
        };

        PanResponse? resp = null;
        foreach (var baseUrl in CandidateBases(share))
        {
            foreach (var prefix in new[] { "/api", "/b/api" })
            {
                resp = await browser.PostJsonAsync($"{baseUrl}{prefix}/file/download_info", body, headers, ct)
                    .ConfigureAwait(false);
                if (resp.Status == 404) continue;
                if (resp.Root is not null) break;
            }
            if (resp?.Root is not null) break;
        }

        var url = resp?.Get("data", "DownloadUrl") ?? resp?.Get("data", "downloadUrl");
        if (string.IsNullOrWhiteSpace(url))
        {
            var code = resp?.GetLong("code") ?? -1;
            var message = resp?.Get("message") ?? resp?.Snippet ?? "无响应";
            return PanDownloadLink.Fail(
                $"123 云盘取直链失败（code={code}）：{message}\n" +
                "常见原因：分享方关闭了下载、文件需要登录、或接口改版。");
        }

        return Ok(entry, url, browser);
    }

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
