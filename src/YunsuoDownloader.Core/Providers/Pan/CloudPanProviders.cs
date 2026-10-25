using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Providers.Pan;

// ============================================================================================
//  网盘 Provider 实现说明（配合 docs/PAN_PROVIDERS.md 一起看）
//
//  ★ 这些接口都是各家网盘「网页端私有接口」，官方不公开、随时会改。
//    本文件里的路径是 2026-10 实测过的状态，注释里标了哪些已失效。
//    没有真实账号时无法端到端验证，所以每个 Provider 都把「每一步调了什么、
//    返回了什么」记进 ctx.Diag(...)，失败时随错误信息一起展示，方便按诊断跟进修复。
//
//  ★ 提取码（访问密码）是通用能力：
//      - 链接里带 ?pwd=xxxx / #pwd=xxxx / "提取码: xxxx" 会自动识别
//      - 也支持界面弹框输入（ResolveResult.NeedPassword → 重试时带 SharePassword）
//
//  实测结论（2026-10）：
//    阿里云盘：/adrive/v{1,2,3,4}/share_link/get_share_token 全部 404 已下线；
//              get_share_by_anonymous(v2/v3) 与 file/list(v2/v3) 仍存在。
//              → 改成"先 anonymously 拿分享信息/令牌，再列目录"的多路兜底。
//    百度网盘：/share/list 不带验证凭证时返回 {"errno":9019,"errmsg":"need verify"}，
//              必须先 /share/verify 并接住 Set-Cookie 里的 BDCLND。
//    123网盘 ：www.123pan.com 与 api.123pan.com 的 /api/share/get 均已 404；
//              现役主机是 www.123865.com（对错误的 shareKey 也返回规范 JSON）。
//    夸克网盘：sharepage/token 与 sharepage/detail 路径仍有效，token 接口接受 passcode。
// ============================================================================================

/// <summary>阿里云盘（alipan.com / aliyundrive.com）分享解析。公开分享不需要登录。</summary>
public sealed class AliyunPanProvider : ILinkProvider
{
    private static readonly Regex Rx = new(
        @"https?://(?:www\.)?(?:aliyundrive|alipan)\.com/s/(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] VideoExts =
        { "mp4", "mkv", "avi", "mov", "flv", "wmv", "ts", "m2ts", "rmvb", "webm", "m4v", "iso" };

    // get_share_token 已下线，这里保留成"候选"，接口回来时无需改代码
    private static readonly string[] TokenPaths =
    {
        "/adrive/v2/share_link/get_share_token",
        "/adrive/v3/share_link/get_share_token"
    };

    private static readonly string[] AnonymousPaths =
    {
        "/adrive/v3/share_link/get_share_by_anonymous",
        "/adrive/v2/share_link/get_share_by_anonymous"
    };

    private static readonly string[] ListPaths = { "/adrive/v3/file/list", "/adrive/v2/file/list" };

    private static readonly string[] DlinkPaths =
    {
        "/adrive/v2/file/get_share_link_download_url",
        "/adrive/v3/file/get_share_link_download_url"
    };

    public string Name => "Aliyun";
    public string DisplayName => "阿里云盘";
    public int Priority => 100;
    public bool RequiresCredential => false;

    public bool CanHandle(string url) => Rx.IsMatch(url);

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        const string host = "https://api.alipan.com";
        var shareId = Rx.Match(url).Groups["id"].Value;
        var pwd = ctx.SharePassword ?? PanHttp.ExtractPwd(url);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Referer", "https://www.alipan.com/"),
            new("Origin", "https://www.alipan.com"),
            new("x-canary", "client=web,app=share,version=v2.3.2")
        };

        var session = new PanSession(ctx.Http, null, m => ctx.Log(m), ctx.Verbose);
        var result = new ResolveResult { ProviderName = Name, ShareId = shareId };

        try
        {
            string? shareToken = null;
            string? firstFileId = null;
            string? firstName = null;
            string? firstExt = null;
            long firstSize = 0;
            var anonymousAnswered = false;      // 接口有应答（不是网络错误）
            var shareMissing = false;           // 明确告诉我们：分享不存在/已失效
            var tokenEndpointGone = false;      // get_share_token 全版本都 404 了

            // ---------- 1) 拿分享信息（公开分享无需登录）/ 顺带拿 share_token ----------
            foreach (var path in AnonymousPaths)
            {
                var resp = await session.PostJsonAsync(host + path, new { share_id = shareId }, headers, ct).ConfigureAwait(false);
                ctx.Diag($"POST {path} → HTTP {resp.Status} {resp.Snippet}");
                if (resp.Status == -1) continue;
                anonymousAnswered = true;

                if (resp.Body.Contains("NotFound.ShareLink", StringComparison.OrdinalIgnoreCase))
                {
                    shareMissing = true;
                    continue;
                }
                if (!resp.IsSuccess) continue;

                using var doc = resp.TryJson();
                if (doc is null) continue;

                shareToken = PanPath.GetString(doc.RootElement, "share_token") ?? PanPath.GetString(doc.RootElement, "token");

                if (PanPath.TryGet(doc.RootElement, out var infos, "file_infos") && infos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in infos.EnumerateArray())
                    {
                        var name = PanPath.GetString(it, "name");
                        var ext = Path.GetExtension(name ?? "").TrimStart('.').ToLowerInvariant();
                        if (firstFileId is null || VideoExts.Contains(ext))
                        {
                            firstFileId = PanPath.GetString(it, "file_id");
                            firstName = name;
                            firstSize = PanPath.GetLong(it, "size");
                            firstExt = ext;
                            if (VideoExts.Contains(ext)) break;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(shareToken) || firstFileId is not null) break;
            }

            // 分享确实不存在 -> 这不是提取码问题，不要误导用户
            if (shareMissing && firstFileId is null && string.IsNullOrWhiteSpace(shareToken))
            {
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                result.ErrorMessage =
                    "阿里云盘提示「分享不存在」：链接可能已失效、被取消，或 ID 抄错了。\n" +
                    "请先在浏览器里打开这个分享确认还能看到文件。";
                return result;
            }

            // ---------- 2) 没拿到 token 就试 token 接口（当前多半 404，接口回来即可用）----------
            if (string.IsNullOrWhiteSpace(shareToken) && firstFileId is null)
            {
                var goneCount = 0;
                foreach (var path in TokenPaths)
                {
                    var resp = await session.PostJsonAsync(host + path,
                        new { share_id = shareId, share_pwd = pwd ?? string.Empty }, headers, ct).ConfigureAwait(false);
                    ctx.Diag($"POST {path} (提取码={(pwd is null ? "未提供" : "已提供")}) → HTTP {resp.Status} {resp.Snippet}");

                    if (resp.Status == 404 && resp.Body.Contains("not exist", StringComparison.OrdinalIgnoreCase))
                    {
                        goneCount++;
                        ctx.Diag($"   {path} 已下线");
                        continue;
                    }
                    // 只有接口明确说"密码问题"才让用户填提取码；401 是令牌问题，不是密码问题
                    if (string.IsNullOrEmpty(pwd) && ExplicitPasswordProblem(resp.Body))
                        return NeedPwd(result, ctx, shareId);

                    shareToken = resp.Get("share_token") ?? resp.Get("token");
                    if (!string.IsNullOrWhiteSpace(shareToken)) break;
                }
                tokenEndpointGone = goneCount == TokenPaths.Length;
            }

            if (string.IsNullOrWhiteSpace(shareToken) && firstFileId is null && !anonymousAnswered)
            {
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                result.ErrorMessage =
                    "阿里云盘接口调用失败（连分享信息都没拿到）。\n" +
                    "可能原因：网络/代理不通、或接口域名被拦截。可点「详情」看每一步的真实返回。";
                return result;
            }

            var listHeaders = new List<KeyValuePair<string, string>>(headers);
            var hasToken = !string.IsNullOrWhiteSpace(shareToken);
            if (hasToken)
                listHeaders.Add(new KeyValuePair<string, string>("x-share-token", shareToken!));

            // ---------- 3) 列目录，挑一个文件（没令牌就不浪费一次必然 401 的调用）----------
            if (firstFileId is null && hasToken)
            {
                foreach (var path in ListPaths)
                {
                    var resp = await session.PostJsonAsync(host + path, new
                    {
                        share_id = shareId,
                        parent_file_id = "root",
                        limit = 100,
                        order_by = "name",
                        order_direction = "ASC",
                        image_thumbnail_process = "image/resize,w_160/format/jpeg",
                        video_thumbnail_process = "video/snapshot,t_1000,f_jpg,ar_auto,w_300"
                    }, listHeaders, ct).ConfigureAwait(false);

                    ctx.Diag($"POST {path} → HTTP {resp.Status} {resp.Snippet}");

                    if (resp.Status == 404 && resp.Body.Contains("not exist", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(pwd) && ExplicitPasswordProblem(resp.Body))
                        return NeedPwd(result, ctx, shareId);
                    if (!resp.IsSuccess) continue;

                    using var doc = resp.TryJson();
                    if (doc is null) continue;
                    if (!PanPath.TryGet(doc.RootElement, out var items, "items") || items.ValueKind != JsonValueKind.Array)
                    {
                        ctx.Diag("   响应里没有 items 数组");
                        continue;
                    }

                    foreach (var it in items.EnumerateArray())
                    {
                        var fid = PanPath.GetString(it, "file_id");
                        if (fid is null) continue;
                        var name = PanPath.GetString(it, "name");
                        var ext = Path.GetExtension(name ?? "").TrimStart('.').ToLowerInvariant();
                        if (firstFileId is null || VideoExts.Contains(ext))
                        {
                            firstFileId = fid;
                            firstName = name;
                            firstSize = PanPath.GetLong(it, "size");
                            firstExt = ext;
                            if (VideoExts.Contains(ext)) break;
                        }
                    }
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(firstFileId))
            {
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                result.ErrorMessage = tokenEndpointGone
                    ? "阿里云盘分享的「访问令牌」接口已全部下线（get_share_token 的 v2/v3 都返回 uri not exist），" +
                      "拿不到 x-share-token 就无法列目录取直链。\n" +
                      "这是官方接口变更导致的，需要更新 Provider 才能恢复。\n" +
                      "临时替代：用浏览器插件在阿里云盘网页上点一下，让插件把直链直接送进下载器。"
                    : "阿里云盘拿不到可下载的文件。\n" +
                      "可能原因：① 分享接口返回异常；② 分享设了提取码；③ 分享里的文件已被删除。\n" +
                      "兜底办法：用浏览器插件在网盘网页上点一下，让插件把直链直接送过来。";
                return result;
            }

            // ---------- 4) 取直链 ----------
            string? dlink = null;
            foreach (var path in DlinkPaths)
            {
                var resp = await session.PostJsonAsync(host + path, new
                {
                    share_id = shareId,
                    file_id = firstFileId,
                    share_token = shareToken ?? string.Empty,
                    expire_sec = 14400
                }, listHeaders, ct).ConfigureAwait(false);

                ctx.Diag($"POST {path} → HTTP {resp.Status} {resp.Snippet}");
                if (resp.Status is 404 or 410 && resp.Body.Length == 0) continue;
                dlink = resp.Get("url");
                if (!string.IsNullOrWhiteSpace(dlink)) break;
            }

            if (string.IsNullOrWhiteSpace(dlink))
            {
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                result.ErrorMessage = "阿里云盘能列出文件，但取直链失败（接口可能改版或该文件受限）。";
                return result;
            }

            result.Success = true;
            result.Url = dlink;
            result.Kind = VideoExts.Contains(firstExt ?? "") ? DownloadKind.Video : DownloadKind.File;
            result.FileName = firstName;
            result.Title = firstName;
            result.Referer = "https://www.alipan.com/";
            result.PreferAria2 = true;
            result.TotalBytes = firstSize;
            result.Headers["Referer"] = "https://www.alipan.com/";
            result.Headers["User-Agent"] = PanHttp.DefaultUserAgent;
            result.Message = $"阿里云盘解析成功（直链约 4 小时过期）：{firstName}";
            result.Diagnostics = ctx.Diagnostics;
            ctx.Log($"  阿里云盘已解析：{firstName} ({Format.Bytes(firstSize)})");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "阿里云盘解析异常");
            result.Success = false;
            result.Diagnostics = ctx.Diagnostics;
            result.ErrorMessage = "阿里云盘解析出错：" + ex.Message;
            return result;
        }
    }

    private static ResolveResult NeedPwd(ResolveResult r, ResolveContext ctx, string shareId)
    {
        r.Success = false;
        r.NeedPassword = true;
        r.PasswordProvider = "Aliyun";
        r.ShareId = shareId;
        r.Diagnostics = ctx.Diagnostics;
        r.ErrorMessage = "这个阿里云盘分享需要提取码，请输入后重试。";
        return r;
    }

    /// <summary>
    /// 接口是否**明确**说了"密码有问题"。
    /// 注意不能只看 401：401（AccessTokenInvalid）是令牌缺失，不是密码错误，
    /// 把它当成"需要提取码"会误导用户去填一个根本不存在的密码。
    /// </summary>
    private static bool ExplicitPasswordProblem(string body)
        => body.Contains("WrongPassword", StringComparison.OrdinalIgnoreCase)
           || body.Contains("ShareLinkPwdError", StringComparison.OrdinalIgnoreCase)
           || body.Contains("密码错误", StringComparison.Ordinal)
           || body.Contains("需要密码", StringComparison.Ordinal)
           || (body.Contains("share_pwd", StringComparison.OrdinalIgnoreCase)
               && body.Contains("error", StringComparison.OrdinalIgnoreCase));
}

/// <summary>夸克 / UC 网盘分享解析（必须提供自己账号的 Cookie）。</summary>
public sealed class QuarkPanProvider : ILinkProvider
{
    private static readonly Regex Rx = new(
        @"https?://(?:pan|drive|www)\.(?:quark|uc)\.cn/s/(?<id>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Api = "https://drive-pc.quark.cn/1/clouddrive";
    private const string Query = "?pr=ucpro&fr=pc";

    public string Name => "Quark";
    public string DisplayName => "夸克网盘";
    public int Priority => 95;
    public bool RequiresCredential => true;

    public bool CanHandle(string url) => Rx.IsMatch(url);

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var pwdId = Rx.Match(url).Groups["id"].Value;
        var passcode = ctx.SharePassword ?? PanHttp.ExtractPwd(url) ?? string.Empty;
        var result = new ResolveResult { ProviderName = Name, ShareId = pwdId };

        var cookie = ctx.SettingsService.GetCookie(Name);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Referer", $"https://pan.quark.cn/s/{pwdId}"),
            new("Origin", "https://pan.quark.cn")
        };

        var session = new PanSession(ctx.Http, cookie, m => ctx.Log(m), ctx.Verbose);

        try
        {
            // 1) stoken（passcode 就是提取码）
            var tokenResp = await session.PostJsonAsync($"{Api}/share/sharepage/token{Query}",
                new { pwd_id = pwdId, passcode }, headers, ct).ConfigureAwait(false);
            ctx.Diag($"POST sharepage/token (提取码={(passcode.Length > 0 ? "已提供" : "未提供")}) → HTTP {tokenResp.Status} {tokenResp.Snippet}");

            var code = tokenResp.GetLong("code");
            var stoken = tokenResp.Get("data", "stoken");

            if (string.IsNullOrWhiteSpace(stoken))
            {
                var msg = tokenResp.Get("message") ?? tokenResp.Snippet;
                if (NeedsPassword(msg) || code is 41009 or 41010)
                {
                    result.NeedPassword = true;
                    result.PasswordWrong = passcode.Length > 0;
                    result.PasswordProvider = Name;
                    result.ErrorMessage = passcode.Length == 0
                        ? "这个夸克分享需要提取码（访问密码），请输入后重试。"
                        : $"夸克提取码不正确：{msg}";
                }
                else
                {
                    result.ErrorMessage = string.IsNullOrWhiteSpace(cookie)
                        ? "夸克网盘需要登录态：请在「设置 → 网盘账号」填入夸克网盘 Cookie。"
                        : $"夸克获取 stoken 失败：{msg}";
                }
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            // 2) 列分享目录
            var detailUrl = $"{Api}/share/sharepage/detail{Query}&pwd_id={pwdId}&stoken={PanHttp.Escape(stoken)}" +
                            "&pdir_fid=0&force=0&_page=1&_size=100&_fetch_banner=0&_fetch_share=0&_fetch_total=1" +
                            "&_sort=file_type:asc,updated_at:desc";
            var detailResp = await session.GetAsync(detailUrl, headers, ct).ConfigureAwait(false);
            ctx.Diag($"GET sharepage/detail → HTTP {detailResp.Status} {detailResp.Snippet}");

            using var detailDoc = detailResp.TryJson();
            if (detailDoc is null
                || !PanPath.TryGet(detailDoc.RootElement, out var list, "data", "list")
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                result.Success = false;
                result.ErrorMessage = "夸克分享目录为空：" + detailResp.Snippet;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            var first = list[0];
            var fid = PanPath.GetString(first, "fid");
            var shareFidToken = PanPath.GetString(first, "share_fid_token");
            var fileName = PanPath.GetString(first, "file_name") ?? "quark_download";
            var size = PanPath.GetLong(first, "size");

            if (string.IsNullOrWhiteSpace(cookie))
            {
                result.Success = false;
                result.ErrorMessage = "夸克必须先登录：夸克要求把文件转存到自己网盘才能取直链，请在「设置 → 网盘账号」填入 Cookie。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            // 3) 转存到自己网盘
            var saveResp = await session.PostJsonAsync($"{Api}/share/sharepage/save{Query}", new
            {
                fid_list = new[] { fid },
                fid_token_list = new[] { shareFidToken },
                to_pdir_fid = "0",
                pwd_id = pwdId,
                stoken,
                pdir_fid = "0",
                scene = "link"
            }, headers, ct).ConfigureAwait(false);
            ctx.Diag($"POST sharepage/save → HTTP {saveResp.Status} {saveResp.Snippet}");

            var taskId = saveResp.Get("data", "task_id");
            if (string.IsNullOrWhiteSpace(taskId))
            {
                result.Success = false;
                result.ErrorMessage = "夸克转存失败：" + saveResp.Snippet;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            string? savedFid = null;
            for (int i = 0; i < 30 && savedFid is null; i++)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                var taskResp = await session.GetAsync($"{Api}/task{Query}&task_id={taskId}&retry_index={i}", headers, ct).ConfigureAwait(false);
                var status = taskResp.GetLong("data", "status");
                if (status == 2) savedFid = taskResp.Get("data", "save_as", "save_as_top_fids", "0");
                else if (status == 3)
                {
                    result.Success = false;
                    result.ErrorMessage = "夸克转存任务失败：" + (taskResp.Get("data", "message") ?? taskResp.Snippet);
                    result.Diagnostics = ctx.Diagnostics;
                    return result;
                }
            }
            savedFid ??= fid;
            ctx.Diag($"转存完成，fid={savedFid}");

            // 4) 取直链
            var dlResp = await session.PostJsonAsync($"{Api}/file/download{Query}",
                new { fids = new[] { savedFid } }, headers, ct).ConfigureAwait(false);
            ctx.Diag($"POST file/download → HTTP {dlResp.Status} {dlResp.Snippet}");

            var dlink = dlResp.Get("data", "0", "download_url");
            if (string.IsNullOrWhiteSpace(dlink))
            {
                result.Success = false;
                result.ErrorMessage = "夸克取直链失败：" + dlResp.Snippet;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            result.Success = true;
            result.Url = dlink;
            result.Kind = DownloadKind.File;
            result.FileName = fileName;
            result.Title = fileName;
            result.Referer = $"https://pan.quark.cn/s/{pwdId}";
            result.PreferAria2 = true;
            result.TotalBytes = size;
            result.Cookies = cookie;
            result.Headers["Referer"] = $"https://pan.quark.cn/s/{pwdId}";
            result.Headers["User-Agent"] = PanHttp.DefaultUserAgent;
            result.Message = $"夸克网盘解析成功：{fileName}";
            result.Diagnostics = ctx.Diagnostics;
            ctx.Log($"  夸克网盘已解析：{fileName} ({Format.Bytes(size)})");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "夸克解析异常");
            result.Success = false;
            result.Diagnostics = ctx.Diagnostics;
            result.ErrorMessage = "夸克解析出错：" + ex.Message;
            return result;
        }
    }

    private static bool NeedsPassword(string? message)
        => !string.IsNullOrEmpty(message) &&
           (message.Contains("密码", StringComparison.Ordinal) ||
            message.Contains("passcode", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("pwd", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 123 网盘分享解析。
/// 实测（2026-10）：老接口 www.123pan.com/api/share/get 已 404，
/// 现役接口在 https://www.123865.com/api/share/get。
/// </summary>
public sealed class Pan123Provider : ILinkProvider
{
    private static readonly Regex Rx = new(
        @"https?://(?:www\.)?(?:123pan|123684|123865|123912|123592)\.(?:com|cn)/s/(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Host = "https://www.123865.com";

    public string Name => "Pan123";
    public string DisplayName => "123 网盘";
    public int Priority => 90;
    public bool RequiresCredential => true;

    public bool CanHandle(string url) => Rx.IsMatch(url);

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var shareKey = Rx.Match(url).Groups["id"].Value;
        var pwd = ctx.SharePassword ?? PanHttp.ExtractPwd(url) ?? string.Empty;
        var result = new ResolveResult { ProviderName = Name, ShareId = shareKey };

        var cookie = ctx.SettingsService.GetCookie(Name);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Referer", $"{Host}/s/{shareKey}"),
            new("Origin", Host),
            new("platform", "web"),
            new("app-version", "3"),
            new("Accept", "application/json, text/plain, */*")
        };

        var session = new PanSession(ctx.Http, cookie, m => ctx.Log(m), ctx.Verbose);

        try
        {
            var listUrl = $"{Host}/api/share/get?limit=100&next=1&orderBy=file_name&orderDirection=asc" +
                          $"&shareKey={PanHttp.Escape(shareKey)}&ParentFileId=0&Page=1" +
                          "&event=homeShareDetail&operateType=1&RequestSource=1";
            var resp = await session.GetAsync(listUrl, headers, ct).ConfigureAwait(false);
            ctx.Diag($"GET /api/share/get → HTTP {resp.Status} {resp.Snippet}");

            var code = resp.GetLong("code");
            var message = resp.Get("message") ?? "";

            if (code != 0)
            {
                if (message.Contains("密码", StringComparison.Ordinal) ||
                    message.Contains("passcode", StringComparison.OrdinalIgnoreCase) ||
                    code is 403 or 5113)
                {
                    result.NeedPassword = true;
                    result.PasswordWrong = pwd.Length > 0;
                    result.PasswordProvider = Name;
                    result.ErrorMessage = pwd.Length == 0
                        ? "这个 123 网盘分享需要提取码，请输入后重试。"
                        : $"123 网盘提取码不正确（{message}）";
                }
                else
                {
                    var explain = code switch
                    {
                        5103 => "分享不存在或已失效",
                        401 or 403 => "无权限（通常是登录态缺失或已过期）",
                        _ => message
                    };
                    result.ErrorMessage = $"123 网盘解析失败（code={code}）：{explain}";
                    if (code is 401 or 403 || string.IsNullOrWhiteSpace(cookie))
                    {
                        result.ErrorMessage += code switch
                        {
                            5103 => "",
                            _ => string.IsNullOrWhiteSpace(cookie)
                                ? "\n提示：123 网盘需要登录态，请在「设置 → 网盘账号」填入 Cookie。"
                                : "\n提示：Cookie 可能已过期，请重新获取。"
                        };
                    }
                }
                result.Success = false;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            using var doc = resp.TryJson();
            if (doc is null
                || !PanPath.TryGet(doc.RootElement, out var infoList, "data", "InfoList")
                || infoList.ValueKind != JsonValueKind.Array || infoList.GetArrayLength() == 0)
            {
                result.Success = false;
                result.ErrorMessage = "123 网盘分享目录为空：" + resp.Snippet;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            var first = infoList[0];
            var fileId = PanPath.GetString(first, "FileId");
            var fileName = PanPath.GetString(first, "FileName") ?? "123pan_download";
            var size = PanPath.GetLong(first, "Size");
            var etag = PanPath.GetString(first, "Etag");
            var s3KeyFlag = PanPath.GetString(first, "S3KeyFlag");
            var type = PanPath.GetLong(first, "Type");

            var dlink = PanPath.GetString(first, "DownloadUrl");
            if (string.IsNullOrWhiteSpace(dlink))
            {
                var dlResp = await session.PostJsonAsync($"{Host}/api/file/download_info", new
                {
                    etag,
                    fileId,
                    fileName,
                    type,
                    s3keyFlag = s3KeyFlag,
                    size
                }, headers, ct).ConfigureAwait(false);
                ctx.Diag($"POST /api/file/download_info → HTTP {dlResp.Status} {dlResp.Snippet}");
                dlink = dlResp.Get("data", "DownloadUrl") ?? dlResp.Get("data", "downloadUrl");
            }

            if (string.IsNullOrWhiteSpace(dlink))
            {
                result.Success = false;
                result.ErrorMessage = "123 网盘取直链失败（接口可能改版，或该文件需要登录态）。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            result.Success = true;
            result.Url = dlink;
            result.Kind = DownloadKind.File;
            result.FileName = fileName;
            result.Title = fileName;
            result.Referer = $"{Host}/s/{shareKey}";
            result.PreferAria2 = true;
            result.TotalBytes = size;
            result.Cookies = string.IsNullOrWhiteSpace(cookie) ? null : cookie;
            result.Headers["Referer"] = $"{Host}/s/{shareKey}";
            result.Headers["User-Agent"] = PanHttp.DefaultUserAgent;
            result.Message = $"123 网盘解析成功：{fileName}";
            result.Diagnostics = ctx.Diagnostics;
            ctx.Log($"  123 网盘已解析：{fileName}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "123 网盘解析异常");
            result.Success = false;
            result.Diagnostics = ctx.Diagnostics;
            result.ErrorMessage = "123 网盘解析出错：" + ex.Message;
            return result;
        }
    }
}

/// <summary>
/// 百度网盘分享解析（必须提供含 BDUSS 的 Cookie）。
/// 关键点：百度现在强制要求先"验证分享"并接住 BDCLND Cookie，
/// 否则 /share/list 一律返回 {"errno":9019,"errmsg":"need verify"}。
/// </summary>
public sealed class BaiduPanProvider : ILinkProvider
{
    private static readonly Regex Rx = new(
        @"https?://pan\.baidu\.com/(?:s/|share/init\?(?:[^#]*&)?surl=)(?<id>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int AppId = 250528;

    public string Name => "Baidu";
    public string DisplayName => "百度网盘";
    public int Priority => 85;
    public bool RequiresCredential => true;

    public bool CanHandle(string url) => Rx.IsMatch(url);

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var surl = Rx.Match(url).Groups["id"].Value;
        var pwd = ctx.SharePassword ?? PanHttp.ExtractPwd(url);
        var result = new ResolveResult { ProviderName = Name, ShareId = surl };

        var cookie = ctx.SettingsService.GetCookie(Name);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Referer", $"https://pan.baidu.com/s/{surl}"),
            new("Origin", "https://pan.baidu.com")
        };

        var session = new PanSession(ctx.Http, cookie, m => ctx.Log(m), ctx.Verbose);

        try
        {
            // ---------- 1) 打开分享页拿 yunData（shareid / uk / bdstoken）与 BDCLND ----------
            var pageUrl = $"https://pan.baidu.com/s/{surl}" + (string.IsNullOrEmpty(pwd) ? "" : $"?pwd={pwd}");
            var page = await session.GetAsync(pageUrl, headers, ct).ConfigureAwait(false);
            ctx.Diag(page.Status == -1
                ? $"GET 分享页 → 网络失败：{page.Body}"
                : $"GET 分享页 → HTTP {page.Status}，正文 {page.Body.Length} 字符");
            ctx.Diag($"   Cookie 表 {session.CookieCount} 项，BDCLND={(session.CookieHeader.Contains("BDCLND") ? "有" : "无")}");

            var shareId = Match(page.Body, "\"shareid\"\\s*:\\s*\"?(\\d+)\"?") ?? Match(page.Body, "shareid=(\\d+)");
            var uk = Match(page.Body, "\"share_uk\"\\s*:\\s*\"(\\d+)\"") ?? Match(page.Body, "\"uk\"\\s*:\\s*\"?(\\d+)\"?");
            var bdstoken = Match(page.Body, "\"bdstoken\"\\s*:\\s*\"([^\"]+)\"");
            var needVerify = page.Body.Contains("请输入提取码", StringComparison.Ordinal)
                             || page.Body.Contains("share/verify", StringComparison.Ordinal);

            // ---------- 2) 需要验证就 verify（提取码），拿到 BDCLND ----------
            if (needVerify || shareId is null || uk is null)
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var verifyUrl = $"https://pan.baidu.com/share/verify?surl={PanHttp.Escape(surl)}&t={ts}" +
                                $"&channel=chunlei&web=1&app_id={AppId}&clienttype=0";
                var verify = await session.PostFormAsync(verifyUrl, "pwd=" + PanHttp.Escape(pwd ?? string.Empty),
                    headers, ct).ConfigureAwait(false);
                ctx.Diag($"POST share/verify (提取码={(pwd is null ? "未提供" : "已提供")}) → HTTP {verify.Status} {verify.Snippet}");

                var errno = verify.GetLong("errno");
                if (errno != 0)
                {
                    if (errno == 105)
                    {
                        result.Success = false;
                        result.ErrorMessage = "百度分享链接不存在或已失效（errno=105）。";
                    }
                    else if (string.IsNullOrEmpty(pwd))
                    {
                        result.NeedPassword = true;
                        result.PasswordProvider = Name;
                        result.ErrorMessage = $"这个百度分享需要提取码（接口返回 errno={errno}），请输入后重试。";
                    }
                    else
                    {
                        result.NeedPassword = true;
                        result.PasswordWrong = true;
                        result.PasswordProvider = Name;
                        result.ErrorMessage = $"百度提取码不正确，或登录态已过期（errno={errno}，需含 BDUSS 的 Cookie）。";
                    }
                    result.Diagnostics = ctx.Diagnostics;
                    return result;
                }

                page = await session.GetAsync(pageUrl, headers, ct).ConfigureAwait(false);
                ctx.Diag(page.Status == -1
                    ? $"GET 分享页(验证后) → 网络失败：{page.Body}"
                    : $"GET 分享页(验证后) → HTTP {page.Status}，正文 {page.Body.Length} 字符，BDCLND={(session.CookieHeader.Contains("BDCLND") ? "有" : "无")}");
                shareId ??= Match(page.Body, "\"shareid\"\\s*:\\s*\"?(\\d+)\"?") ?? Match(page.Body, "shareid=(\\d+)");
                uk ??= Match(page.Body, "\"share_uk\"\\s*:\\s*\"(\\d+)\"") ?? Match(page.Body, "\"uk\"\\s*:\\s*\"?(\\d+)\"?");
                bdstoken ??= Match(page.Body, "\"bdstoken\"\\s*:\\s*\"([^\"]+)\"");

                // 有些版本的 /share/verify 会直接把 shareid / uk 一起返回，这里当兜底
                shareId ??= verify.Get("shareid");
                uk ??= verify.Get("uk");
            }

            if (string.IsNullOrWhiteSpace(shareId) || string.IsNullOrWhiteSpace(uk))
            {
                result.Success = false;
                result.ErrorMessage =
                    "百度分享页里解析不到 shareid / uk。\n" +
                    "常见原因：① Cookie 缺失或过期（必须含 BDUSS）；② 分享需要提取码；" +
                    "③ 百度改了分享页结构。\n" +
                    "请在「设置 → 网盘账号 → 百度网盘」重新填入完整 Cookie 后重试。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            // ---------- 3) 列目录 ----------
            var ts2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var listUrl = $"https://pan.baidu.com/share/list?uk={uk}&shareid={shareId}&order=other&desc=1" +
                          $"&showempty=0&web=1&page=1&num=100&dir=%2F&t={ts2}&channel=chunlei" +
                          $"&app_id={AppId}&clienttype=0" +
                          (string.IsNullOrEmpty(bdstoken) ? "" : $"&bdstoken={bdstoken}");
            var listResp = await session.GetAsync(listUrl, headers, ct).ConfigureAwait(false);
            ctx.Diag($"GET /share/list → HTTP {listResp.Status} {listResp.Snippet}");

            var listErrno = listResp.GetLong("errno");
            if (listErrno == 9019)
            {
                result.Success = false;
                result.ErrorMessage =
                    "百度返回 errno=9019（need verify）：分享验证没有生效。\n" +
                    "请确认 Cookie 含 BDUSS；若分享有提取码，请把提取码填进去再试。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }
            if (listErrno != 0)
            {
                result.Success = false;
                result.ErrorMessage = $"百度 /share/list 返回 errno={listErrno}" +
                                      (listErrno == -9 ? "（Cookie 无效或已过期，需含 BDUSS）" : "") +
                                      "：" + listResp.Snippet;
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            using var listDoc = listResp.TryJson();
            if (listDoc is null
                || !PanPath.TryGet(listDoc.RootElement, out var list, "list")
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                result.Success = false;
                result.ErrorMessage = "百度分享目录为空（可能只有文件夹，或需要提取码）。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            JsonElement target = default;
            string? fileName = null;
            long size = 0, fsId = 0;
            foreach (var it in list.EnumerateArray())
            {
                if (PanPath.GetLong(it, "isdir") == 1) continue;
                target = it;
                fileName = PanPath.GetString(it, "server_filename");
                size = PanPath.GetLong(it, "size");
                fsId = PanPath.GetLong(it, "fs_id");
                break;
            }
            if (fsId == 0)
            {
                result.Success = false;
                result.ErrorMessage = "百度分享里没有找到可直接下载的文件（当前只支持文件，不支持文件夹）。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }
            var path = PanPath.GetString(target, "path") ?? $"/{fileName}";

            // ---------- 4) 取 dlink（社区通用 sign 算法）----------
            var rand = RandomAlnum(8);
            var sign = Md5($"app_id={AppId}&timestamp={ts2}&rand={rand}&uid={uk}&sign_type=1");
            var dlUrl = $"https://pan.baidu.com/api/sharedownload?sign={sign}&timestamp={ts2}" +
                        $"&channel=chunlei&web=1&app_id={AppId}&clienttype=0" +
                        (string.IsNullOrEmpty(bdstoken) ? "" : $"&bdstoken={bdstoken}");
            var form = $"encrypt=0&product=share&type=dlink&uk={uk}&primaryid={shareId}&fid_list=%5B{fsId}%5D";

            var dlResp = await session.PostFormAsync(dlUrl, form, headers, ct).ConfigureAwait(false);
            ctx.Diag($"POST /api/sharedownload → HTTP {dlResp.Status} {dlResp.Snippet}");

            var dlErrno = dlResp.GetLong("errno");
            if (dlErrno != 0)
            {
                var known = dlErrno switch
                {
                    -9 => "Cookie 无效或已过期（需含 BDUSS）",
                    112 => "该分享已失效",
                    113 => "分享已被取消",
                    _ => "接口返回了错误"
                };
                result.Success = false;
                result.ErrorMessage =
                    $"百度取直链失败 errno={dlErrno}（{known}）。\n" +
                    "百度 sharedownload 的 sign 算法与网页端版本绑定；若持续失败说明算法已更新，" +
                    "可按 docs/PAN_PROVIDERS.md 更换，或改用浏览器插件在网盘网页上直接抓直链。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            var dlink = dlResp.Get("list", "0", "dlink");
            if (string.IsNullOrWhiteSpace(dlink))
            {
                result.Success = false;
                result.ErrorMessage = "百度接口没有返回 dlink（可能需要会员，或该文件被限制）。";
                result.Diagnostics = ctx.Diagnostics;
                return result;
            }

            result.Success = true;
            result.Url = dlink;
            result.Kind = DownloadKind.File;
            result.FileName = fileName;
            result.Title = fileName;
            result.Referer = "https://pan.baidu.com/";
            result.PreferAria2 = true;
            result.TotalBytes = size;
            result.Cookies = session.CookieHeader;
            result.Headers["Referer"] = "https://pan.baidu.com/";
            result.Headers["User-Agent"] = PanHttp.DefaultUserAgent;
            result.Message = $"百度网盘解析成功：{path}";
            result.Diagnostics = ctx.Diagnostics;
            ctx.Log($"  百度网盘已解析：{fileName} ({Format.Bytes(size)})");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "百度网盘解析异常");
            result.Success = false;
            result.Diagnostics = ctx.Diagnostics;
            result.ErrorMessage = "百度网盘解析出错：" + ex.Message;
            return result;
        }
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string RandomAlnum(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        foreach (var b in RandomNumberGenerator.GetBytes(length)) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string Md5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
