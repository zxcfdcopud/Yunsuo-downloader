using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YunsuoDownloader.Core.Pan;

/// <summary>
/// 网盘接口的 HTTP 会话。
///
/// 为什么要单独造一个而不是复用下载引擎的 <see cref="HttpClient"/>：
///   1) 网盘接口要自己管理 Cookie（百度的 BDCLND、夸克的 __puus 都靠它），
///      而全局 HttpClient 的 handler 默认 <c>UseCookies=true</c>，
///      Cookie 会在四家网盘之间串味；
///   2) 每家网盘要的 Referer / Origin / UA 都不一样，必须能逐次覆盖；
///   3) 出问题时要把每一步的 URL、状态码、正文片段记下来——
///      网盘接口改版是常态，没有诊断日志根本没法排查。
/// </summary>
public sealed class PanBrowser : IDisposable
{
    /// <summary>桌面 Chrome 的 UA。网盘接口对 UA 很敏感，用默认的 .NET UA 会被直接拒绝。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private readonly bool _verbose;

    /// <summary>本次会话的关键步骤记录（一定收集，verbose 时同时打印到日志）。</summary>
    public List<string> Diagnostics { get; } = new();

    public PanBrowser(HttpClient? http, string? cookieHeader, Action<string>? log = null, bool verbose = false)
    {
        if (http is null)
        {
            // 独立 handler：不自动管理 Cookie（由本类负责），不自动重定向（要自己看 302 里的地址）
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                // 故意不开启自动解压：百度会把 404 页也标成 Content-Encoding: gzip，
                // 但正文其实是明文 HTML，交给 .NET 自动解压会直接抛
                // "GZip 头中的幻数不正确"。这里改成自己按 Content-Encoding 解，
                // 解不开就当明文用（见 DecodeBody）。
                AutomaticDecompression = DecompressionMethods.None,
                MaxAutomaticRedirections = 5
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }

        _log = log;
        _verbose = verbose;
        AbsorbCookieHeader(cookieHeader);
    }

    /// <summary>当前 Cookie 头（给下载引擎用：夸克/百度拿到的直链仍然要带登录态）。</summary>
    public string CookieHeader => string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    public int CookieCount => _cookies.Count;

    public bool HasCookie(string name) => _cookies.ContainsKey(name);

    public string? GetCookie(string name) => _cookies.TryGetValue(name, out var v) ? v : null;

    public void SetCookie(string name, string value) => _cookies[name] = value;

    /// <summary>把 "a=1; b=2" 形式的 Cookie 头塞进会话。</summary>
    public void AbsorbCookieHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length > 0) _cookies[name] = value;
        }
    }

    // ------------------------------------------------------------------ 请求
    public Task<PanResponse> GetAsync(string url, IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, url, null, headers, ct);

    public Task<PanResponse> PostJsonAsync(string url, object? body,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct = default)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return SendAsync(HttpMethod.Post, url, content, headers, ct);
    }

    public Task<PanResponse> PostFormAsync(string url, string form,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct = default)
    {
        var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        return SendAsync(HttpMethod.Post, url, content, headers, ct);
    }

    private async Task<PanResponse> SendAsync(HttpMethod method, string url, HttpContent? content,
        IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };

        if (!req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent))
            req.Headers.Add("User-Agent", DesktopUserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        // 单独设 Accept 是为了让接口走 JSON 分支，而不是返回一个 HTML 页面
        if (!req.Headers.Contains("Accept"))
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        foreach (var (k, v) in headers ?? Array.Empty<KeyValuePair<string, string>>())
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (string.Equals(k, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                AbsorbCookieHeader(v);
                continue;
            }
            if (!req.Headers.TryAddWithoutValidation(k, v))
                req.Content?.Headers.TryAddWithoutValidation(k, v);
        }

        var cookie = CookieHeader;
        if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);

        var label = $"{method.Method} {Shorten(url)}";
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var body = DecodeBody(resp, bytes);
            AbsorbSetCookie(resp);

            var pr = new PanResponse
            {
                Status = (int)resp.StatusCode,
                Body = body,
                FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url
            };
            Diag($"{label} → HTTP {pr.Status}，{body.Length} 字符{(cookie.Length > 0 ? "" : "（无 Cookie）")}");
            if (_verbose) Diag("   " + pr.Snippet.Replace("\r", " ").Replace("\n", " "));
            return pr;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 网络层失败也要返回一个"结果"，让上层能把它写进诊断而不是崩掉整个解析
            Diag($"{label} → 网络失败：{ex.Message}");
            return new PanResponse { Status = -1, NetworkError = ex.Message, FinalUrl = url };
        }
    }

    /// <summary>
    /// 按 Content-Encoding 手动解压；解不开就按明文当 UTF-8 读。
    /// 为什么要这么宽容：百度会把"分享不存在"的 404 页标成 gzip，正文却是明文 HTML，
    /// 严格解压会直接抛异常，把一个"分享失效"误报成"网络错误"。
    /// </summary>
    private static string DecodeBody(HttpResponseMessage resp, byte[] bytes)
    {
        var encoding = resp.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant();
        if (encoding is "gzip" or "deflate" or "br" && bytes.Length > 0)
        {
            try
            {
                using var source = new MemoryStream(bytes);
                Stream decompressor = encoding switch
                {
                    "gzip" => new GZipStream(source, CompressionMode.Decompress),
                    "br" => new BrotliStream(source, CompressionMode.Decompress),
                    _ => new ZLibStream(source, CompressionMode.Decompress)
                };
                using (decompressor)
                using (var output = new MemoryStream())
                {
                    decompressor.CopyTo(output);
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
            catch
            {
                // 头写 gzip、正文没压缩（百度的 404 页）—— 落到下面按明文读
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private void AbsorbSetCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var raw in values)
        {
            var first = raw.Split(';', 2)[0];
            var eq = first.IndexOf('=');
            if (eq <= 0) continue;
            var name = first[..eq].Trim();
            var value = first[(eq + 1)..].Trim();
            if (name.Length > 0) _cookies[name] = value;
        }
    }

    // ------------------------------------------------------------------ 诊断
    public void Diag(string message)
    {
        Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
        if (_verbose) _log?.Invoke("   " + message);
    }

    private static string Shorten(string url)
    {
        try
        {
            var u = new Uri(url);
            var q = u.Query.Length > 80 ? u.Query[..80] + "…" : u.Query;
            return u.Host + u.AbsolutePath + q;
        }
        catch { return url.Length > 120 ? url[..120] : url; }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
