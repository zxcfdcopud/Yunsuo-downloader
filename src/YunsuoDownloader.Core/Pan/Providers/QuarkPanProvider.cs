using System.Text.RegularExpressions;

namespace YunsuoDownloader.Core.Pan.Providers;

/// <summary>
/// 夸克网盘（pan.quark.cn）分享解析。
///
/// 流程：sharepage/token 拿 stoken → sharepage/detail 列目录 → 取直链。
/// 取直链有两条路：
///   * 先走 sharepage/download（分享直下，不需要登录，但不是所有文件都允许）；
///   * 不行再"转存到自己网盘 → 轮询任务 → file/download"，这条必须有登录态。
/// </summary>
public sealed class QuarkPanProvider : IPanProvider
{
    private const string Api = "https://drive-pc.quark.cn/1/clouddrive";
    private const string Query = "?pr=ucpro&fr=pc";

    private static readonly Regex Rx = new(
        @"https?://(?:pan|drive|www)\.(?:quark|uc)\.cn/s/(?<id>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PanKind Kind => PanKind.Quark;
    public string Id => "Quark";
    public string DisplayName => "夸克网盘";
    public string LoginUrl => "https://pan.quark.cn/";
    public string CookieDomain => "pan.quark.cn";
    public bool RequiresLogin => true;
    public string RootId => "0";
    public string Hint => "大部分分享需要登录后才能取直链；建议先在网盘页登录一次。";

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

        var stoken = share.Get("stoken");
        if (string.IsNullOrWhiteSpace(stoken))
        {
            stoken = await FetchTokenAsync(share, browser, headers, result, ct).ConfigureAwait(false);
            if (result.NeedPassword) return result;
            if (string.IsNullOrWhiteSpace(stoken))
            {
                result.Error ??= "夸克网盘没有拿到 stoken（接口可能改版，点「诊断」看真实返回）。";
                return result;
            }
            share.Set("stoken", stoken);
        }

        var url = $"{Api}/share/sharepage/detail{Query}" +
                  $"&pwd_id={Uri.EscapeDataString(share.ShareId)}" +
                  $"&stoken={Uri.EscapeDataString(stoken)}" +
                  $"&pdir_fid={Uri.EscapeDataString(dirId)}" +
                  "&force=0&_page=1&_size=100&_fetch_banner=0&_fetch_share=0&_fetch_total=1" +
                  "&_sort=file_type:asc,updated_at:desc";

        var resp = await browser.GetAsync(url, headers, ct).ConfigureAwait(false);

        if (IsPasswordProblem(resp))
        {
            result.NeedPassword = true;
            result.WrongPassword = !string.IsNullOrEmpty(share.Password);
            result.Error = result.WrongPassword ? "提取码不正确，请重新输入。" : "这个夸克分享需要提取码，请输入后重试。";
            return result;
        }

        if (PanJson.At(resp.Root, "data") is null)
        {
            var code = resp.Get("code") ?? "?";
            var message = resp.Get("message") ?? resp.Snippet;
            if (resp.Status == 401 || code is "31001" or "31002")
            {
                share.Set("stoken", null);
                result.Error = "夸克网盘的分享令牌过期了，请点「重新解析」再试一次。";
            }
            else
            {
                result.Error = $"夸克网盘列目录失败（code={code}）：{message}";
            }
            return result;
        }

        share.Title ??= resp.Get("data", "share", "title") ?? resp.Get("data", "share", "share_name");

        if (resp.Root is { } root && PanJson.At(root, "data", "list") is { } list)
        {
            foreach (var item in list.EnumerateArray())
            {
                var fid = PanJson.Str(item, "fid");
                if (string.IsNullOrWhiteSpace(fid)) continue;
                var entry = new PanEntry
                {
                    Id = fid,
                    Name = PanJson.Str(item, "file_name") ?? fid,
                    IsFolder = PanJson.Bool(item, "dir"),
                    Size = PanJson.Long(item, "size"),
                    ModifiedAt = ParseUnix(PanJson.Long(item, "updated_at") / 1000)
                };
                var ft = PanJson.Str(item, "share_fid_token");
                if (!string.IsNullOrWhiteSpace(ft)) entry.Extra["share_fid_token"] = ft;
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
        var stoken = share.Get("stoken");
        if (string.IsNullOrWhiteSpace(stoken))
            return PanDownloadLink.Fail("分享令牌已失效，请重新解析一次。");

        var headers = BaseHeaders(share);

        // ---------- 路线 1：分享直下 ----------
        var direct = await browser.PostJsonAsync($"{Api}/share/sharepage/download{Query}", new
        {
            fids = new[] { entry.Id },
            pwd_id = share.ShareId,
            stoken
        }, headers, ct).ConfigureAwait(false);

        var url = FirstDownloadUrl(direct);
        if (!string.IsNullOrWhiteSpace(url))
            return Ok(entry, url, browser, "夸克分享直链");

        browser.Diag("分享直下没给地址，改走「转存 → file/download」");

        // ---------- 路线 2：转存后取直链（需要登录态）----------
        if (string.IsNullOrWhiteSpace(browser.CookieHeader))
        {
            return PanDownloadLink.Fail(
                "夸克网盘需要登录才能取这个文件的直链。\n" +
                "请到「网盘解析」页点「登录夸克网盘」，或用浏览器插件在夸克网页登录后重试。");
        }

        var fidToken = entry.Extra.TryGetValue("share_fid_token", out var ft) ? ft : null;
        var save = await browser.PostJsonAsync($"{Api}/share/sharepage/save{Query}", new
        {
            fid_list = new[] { entry.Id },
            fid_token_list = new[] { fidToken ?? string.Empty },
            to_pdir_fid = "0",
            pwd_id = share.ShareId,
            stoken,
            pdir_fid = "0",
            scene = "link"
        }, headers, ct).ConfigureAwait(false);

        var taskId = save.Get("data", "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            // 2026-10 实测：未登录时夸克对"转存"直接回 401 code=31001「require login [guest]」
            if (save.Status == 401 || save.GetLong("code") == 31001)
            {
                var expired = browser.CookieHeader.Length > 0;
                return PanDownloadLink.Fail(
                    "夸克网盘要求先登录才能取直链（转存到自己网盘再下载）。\n" +
                    (expired
                        ? "程序里已经存了一份夸克登录态，但被服务端拒绝了 —— 多半是**过期了**，请重新登录一次。"
                        : "请点「网盘解析」页夸克那一行的「登录」，登录后再试一次。"));
            }
            return PanDownloadLink.Fail("夸克转存失败：" + (save.Get("message") ?? save.Snippet));
        }

        string? savedFid = null;
        for (var i = 0; i < 30 && savedFid is null; i++)
        {
            await Task.Delay(600, ct).ConfigureAwait(false);
            var task = await browser.GetAsync($"{Api}/task{Query}&task_id={taskId}&retry_index={i}",
                headers, ct).ConfigureAwait(false);
            var status = task.GetLong("data", "status");
            if (status == 2) savedFid = task.Get("data", "save_as", "save_as_top_fids", "0");
            if (status == 3)
                return PanDownloadLink.Fail("夸克转存任务失败：" + (task.Get("data", "message") ?? task.Snippet));
        }

        savedFid ??= entry.Id;
        var dl = await browser.PostJsonAsync($"{Api}/file/download{Query}",
            new { fids = new[] { savedFid } }, headers, ct).ConfigureAwait(false);
        url = FirstDownloadUrl(dl);
        return string.IsNullOrWhiteSpace(url)
            ? PanDownloadLink.Fail("夸克取直链失败：" + (dl.Get("message") ?? dl.Snippet))
            : Ok(entry, url, browser, "夸克转存直链");
    }

    // ------------------------------------------------------------------ 内部
    private async Task<string?> FetchTokenAsync(PanShare share, PanBrowser browser,
        List<KeyValuePair<string, string>> headers, PanListResult result, CancellationToken ct)
    {
        var resp = await browser.PostJsonAsync($"{Api}/share/sharepage/token{Query}", new
        {
            pwd_id = share.ShareId,
            passcode = share.Password ?? string.Empty
        }, headers, ct).ConfigureAwait(false);

        var stoken = resp.Get("data", "stoken");
        if (!string.IsNullOrWhiteSpace(stoken))
        {
            // stoken 接口顺带返回分享标题，别浪费一次请求
            share.Title ??= resp.Get("data", "title");
            return stoken;
        }

        if (IsPasswordProblem(resp))
        {
            result.NeedPassword = true;
            result.WrongPassword = !string.IsNullOrEmpty(share.Password);
            result.Error = result.WrongPassword ? "提取码不正确，请重新输入。" : "这个夸克分享需要提取码，请输入后重试。";
            return null;
        }

        var message = resp.Get("message") ?? resp.Snippet;
        result.Error = resp.Status == -1
            ? "连不上夸克网盘接口：" + resp.NetworkError
            : $"夸克网盘获取 stoken 失败：{message}";
        return null;
    }

    private static PanDownloadLink Ok(PanEntry entry, string url, PanBrowser browser, string message) => new()
    {
        Ok = true,
        Url = url,
        FileName = entry.Name,
        Size = entry.Size,
        Cookies = browser.CookieHeader.Length > 0 ? browser.CookieHeader : null,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://pan.quark.cn/",
            ["User-Agent"] = PanBrowser.DesktopUserAgent
        },
        Message = $"{message}：{entry.Name}"
    };

    private static List<KeyValuePair<string, string>> BaseHeaders(PanShare share) => new()
    {
        new("Referer", $"https://pan.quark.cn/s/{share.ShareId}"),
        new("Origin", "https://pan.quark.cn")
    };

    /// <summary>夸克的下载地址可能藏在 data[0]、data.list[0] 或 data.download_url，统一捞一遍。</summary>
    private static string? FirstDownloadUrl(PanResponse resp)
    {
        if (resp.Root is not { } root) return null;
        foreach (var path in new[]
                 {
                     new[] { "data", "0", "download_url" },
                     new[] { "data", "list", "0", "download_url" },
                     new[] { "data", "download_url" },
                     new[] { "download_url" }
                 })
        {
            var v = PanJson.Str(root, path);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static bool IsPasswordProblem(PanResponse resp)
    {
        var text = (resp.Get("message") ?? "") + " " + resp.Snippet;
        return text.Contains("密码", StringComparison.Ordinal)
               || text.Contains("passcode", StringComparison.OrdinalIgnoreCase)
               || resp.GetLong("code") is 41009 or 41010;
    }

    private static DateTime? ParseUnix(long seconds)
        => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;
}
