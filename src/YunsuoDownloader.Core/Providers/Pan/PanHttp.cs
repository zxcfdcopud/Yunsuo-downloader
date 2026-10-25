using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Providers.Pan;

/// <summary>一次网盘 HTTP 调用的结果，带少量取值辅助方法。</summary>
internal sealed class PanResponse
{
    public int Status { get; }
    public string Body { get; }
    public string Url { get; }

    public PanResponse(int status, string body, string url)
    {
        Status = status;
        Body = body;
        Url = url;
    }

    public bool IsSuccess => Status is >= 200 and < 300;

    public string Snippet => Format.Truncate(Body.Replace("\r", " ").Replace("\n", " "), 300);

    public JsonDocument? TryJson()
    {
        try { return JsonDocument.Parse(Body); }
        catch { return null; }
    }

    public string? Get(params string[] path)
    {
        using var doc = TryJson();
        return doc is null ? null : PanPath.GetString(doc.RootElement, path);
    }

    public long GetLong(params string[] path)
    {
        using var doc = TryJson();
        return doc is null ? 0 : PanPath.GetLong(doc.RootElement, path);
    }

    public bool Has(params string[] path)
    {
        using var doc = TryJson();
        return doc is not null && PanPath.TryGet(doc.RootElement, out _, path);
    }
}

/// <summary>
/// 网盘请求会话：**维护自己的 Cookie 表**（用户登录态 + 服务端下发的 Set-Cookie）。
/// 这一步很关键：百度验证通过后的凭证放在 Set-Cookie 的 BDCLND 里，不接住它，
/// 后面的 /share/list 一定返回 errno 9019 "need verify"。
/// 同时记录每次请求/响应，便于诊断（verbose 时输出）。
/// </summary>
internal sealed class PanSession
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private readonly bool _verbose;

    public PanSession(HttpClient http, string? userCookie, Action<string>? log, bool verbose)
    {
        _http = http;
        _log = log;
        _verbose = verbose;
        MergeCookieHeader(userCookie);
    }

    public int CookieCount => _cookies.Count;

    public string CookieHeader => string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    public void MergeCookieHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var name = kv[0].Trim();
            if (name.Length > 0) _cookies[name] = kv[1].Trim();
        }
    }

    public async Task<PanResponse> SendAsync(HttpMethod method, string url, HttpContent? content,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
    {
        // 网络抖动（代理换节点、TLS 握手失败等）重试一次，让诊断信息干净一些
        var first = await SendOnceAsync(method, url, content, headers, ct, attempt: 1).ConfigureAwait(false);
        if (first.Status != -1 || ct.IsCancellationRequested) return first;

        // 有些网盘页面会返回与 Content-Encoding 不匹配的压缩流（百度分享页实测遇到过
        // "The archive entry was compressed using an unsupported compression method"），
        // 这种请求改走"不解压 + identity"的裸客户端再试一次。
        var raw = PanHttp.SendRawAsync(_http, method, url, content, headers, CookieHeader, _log, _verbose, ct);
        if (raw is not null)
        {
            var rawResp = await raw.ConfigureAwait(false);
            if (rawResp.Status != -1)
            {
                if (_verbose) _log?.Invoke("   ↻ 自动解压失败，改用 identity 重新请求成功");
                return rawResp;
            }
        }

        if (_verbose) _log?.Invoke($"   ↻ 首次失败（{first.Body}），重试一次");
        await Task.Delay(400, ct).ConfigureAwait(false);
        return await SendOnceAsync(method, url, content, headers, ct, attempt: 2).ConfigureAwait(false);
    }

    private async Task<PanResponse> SendOnceAsync(HttpMethod method, string url, HttpContent? content,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct, int attempt)
    {
        // HttpContent 只能被发送一次，第二轮要重建
        var body = content is null ? null : await content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var mediaType = content?.Headers.ContentType?.MediaType ?? "application/json";
        var actualContent = body is null
            ? null
            : new StringContent(body, Encoding.UTF8, mediaType);

        using var req = new HttpRequestMessage(method, url) { Content = actualContent };
        req.Headers.TryAddWithoutValidation("User-Agent", PanHttp.DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (_cookies.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        if (headers is not null)
            foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

        if (_verbose)
            _log?.Invoke($"→ ({attempt}) {method.Method} {url}{(body is null ? "" : "  请求体: " + Format.Truncate(body, 400))}");

        int status;
        string text;
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            CaptureSetCookie(resp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = -1;
            text = ex.Message;
        }

        if (_verbose)
            _log?.Invoke($"← {status}: {Format.Truncate(text.Replace("\r", " ").Replace("\n", " "), 500)}");

        return new PanResponse(status, text, url);
    }

    private void CaptureSetCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var raw in values)
        {
            var first = raw.Split(';')[0];   // 只取 name=value
            var kv = first.Split('=', 2);
            if (kv.Length != 2) continue;
            var name = kv[0].Trim();
            if (name.Length == 0) continue;
            _cookies[name] = kv[1].Trim();
            if (_verbose) _log?.Invoke($"   [cookie] {name} 已更新（{kv[1].Trim().Length} 字符）");
        }
    }

    public Task<PanResponse> PostJsonAsync(string url, object? body,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
        => SendAsync(HttpMethod.Post, url,
            body is null ? null : new StringContent(JsonSerializer.Serialize(body, JsonUtil.Compact), Encoding.UTF8, "application/json"),
            headers, ct);

    public Task<PanResponse> PostFormAsync(string url, string form,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
        => SendAsync(HttpMethod.Post, url, new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"), headers, ct);

    public Task<PanResponse> GetAsync(string url, IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
        => SendAsync(HttpMethod.Get, url, null, headers, ct);
}

/// <summary>按路径（属性名或数组下标）从 JSON 里取值的小工具，属性名大小写不敏感。</summary>
internal static class PanPath
{
    private static bool Step(ref JsonElement cur, string p)
    {
        if (cur.ValueKind == JsonValueKind.Array)
        {
            if (!int.TryParse(p, out var idx)) return false;
            if (idx < 0 || idx >= cur.GetArrayLength()) return false;
            cur = cur[idx];
            return true;
        }
        if (cur.ValueKind != JsonValueKind.Object) return false;
        if (cur.TryGetProperty(p, out var next))
        {
            cur = next;
            return true;
        }
        foreach (var prop in cur.EnumerateObject())
        {
            if (string.Equals(prop.Name, p, StringComparison.OrdinalIgnoreCase))
            {
                cur = prop.Value;
                return true;
            }
        }
        return false;
    }

    public static string? GetString(JsonElement el, params string[] path)
    {
        var cur = el;
        foreach (var p in path) if (!Step(ref cur, p)) return null;
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long GetLong(JsonElement el, params string[] path)
    {
        var s = GetString(el, path);
        return long.TryParse(s, out var v) ? v : 0;
    }

    public static bool TryGet(JsonElement el, out JsonElement found, params string[] path)
    {
        var cur = el;
        foreach (var p in path)
        {
            if (!Step(ref cur, p)) { found = default; return false; }
        }
        found = cur;
        return true;
    }
}

/// <summary>网盘 Provider 共用的常量与小工具。</summary>
internal static class PanHttp
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>不解压、不带 Accept-Encoding 的裸客户端，用于压缩流异常的兜底。</summary>
    private static readonly HttpClient RawClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>用裸客户端再请求一次（不解压、identity）。失败返回 null。</summary>
    public static async Task<PanResponse?> SendRawAsync(HttpClient _unused, HttpMethod method, string url,
        HttpContent? content, IEnumerable<KeyValuePair<string, string>>? headers, string cookieHeader,
        Action<string>? log, bool verbose, CancellationToken ct)
    {
        try
        {
            string? body = null;
            if (content is not null) body = await content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var mediaType = content?.Headers.ContentType?.MediaType ?? "application/json";

            using var req = new HttpRequestMessage(method, url)
            {
                Content = body is null ? null : new StringContent(body, Encoding.UTF8, mediaType)
            };
            req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            if (headers is not null)
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var resp = await RawClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (verbose) log?.Invoke($"← {((int)resp.StatusCode)} (raw): {Format.Truncate(text.Replace("\r", " ").Replace("\n", " "), 300)}");
            return new PanResponse((int)resp.StatusCode, text, url);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (verbose) log?.Invoke($"   裸请求也失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>从链接里提取提取码：支持 ?pwd= / #pwd= / &amp;pwd= / "提取码: xxxx"。</summary>
    public static string? ExtractPwd(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"[?&#/]\s*(?:pwd|password|passcode|secret)\s*=\s*([A-Za-z0-9]{1,16})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(url,
            @"(?:提取码|密码|访问码)\s*[:：=]?\s*([A-Za-z0-9]{1,16})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string Escape(string s) => Uri.EscapeDataString(s);
}
