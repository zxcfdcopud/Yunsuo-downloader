using System.Text.Json;
using System.Text.Json.Serialization;

namespace YunsuoDownloader.Core.Pan;

/// <summary>支持的四家网盘。</summary>
public enum PanKind
{
    Aliyun,
    Quark,
    Baidu,
    Pan123
}

/// <summary>
/// 一个网盘分享链接解析出来的"分享对象"。
///
/// <see cref="State"/> 是各家自己的会话状态：阿里云盘存 share_token、夸克存 stoken、
/// 百度存 shareid/uk/bdstoken。放在这里而不是字段里，是因为不同家的状态完全不一样，
/// 一个字典加约定的键名就够了，新增一家网盘不用改这个类。
/// </summary>
public sealed class PanShare
{
    public PanKind Kind { get; init; }

    /// <summary>Provider 标识（Aliyun / Quark / Baidu / Pan123），与设置里的凭据键一致。</summary>
    public string ProviderId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>分享 ID（阿里的 share_id、夸克的 pwd_id、百度的 surl、123 的 shareKey）。</summary>
    public string ShareId { get; init; } = string.Empty;

    /// <summary>提取码 / 访问密码（可为空）。</summary>
    public string? Password { get; set; }

    /// <summary>用户输入的原始链接（诊断信息里用）。</summary>
    public string OriginalUrl { get; init; } = string.Empty;

    /// <summary>分享标题（拿到后回填，界面上显示）。</summary>
    public string? Title { get; set; }

    /// <summary>分享方的用户 ID 之类，各家按需。</summary>
    public Dictionary<string, string> State { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => State.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) State.Remove(key);
        else State[key] = value;
    }

    /// <summary>凭据缓存的键：同一个分享换了提取码要重新验证，所以把提取码也算进去。</summary>
    [JsonIgnore]
    public string CacheKey => $"{ProviderId}:{ShareId}:{Password}".ToLowerInvariant();

    public override string ToString() => $"{DisplayName} {ShareId}";
}

/// <summary>分享里的一个条目（文件或文件夹）。</summary>
public sealed class PanEntry
{
    /// <summary>目录/文件 ID（各家字段不同，统一映射到这里）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsFolder { get; init; }

    /// <summary>字节数；文件夹为 0。</summary>
    public long Size { get; init; }

    public DateTime? ModifiedAt { get; init; }

    /// <summary>取直链时要用到的额外字段（夸克的 share_fid_token、123 的 etag/s3keyFlag 等）。</summary>
    public Dictionary<string, string> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>给列表绑定用：勾选状态（不参与解析）。</summary>
    [JsonIgnore]
    public bool IsSelected { get; set; }

    [JsonIgnore]
    public string SizeText => IsFolder ? "文件夹" : Utils.Format.Bytes(Size);

    /// <summary>Segoe 图标：文件夹 / 文件。</summary>
    [JsonIgnore]
    public string Glyph => IsFolder ? "\uE8B7" : "\uE7C3";

    [JsonIgnore]
    public string ModifiedText => ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";

    public override string ToString() => $"{Name} ({SizeText})";
}

/// <summary>列目录的结果。</summary>
public sealed class PanListResult
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    /// <summary>需要提取码（没提供）。</summary>
    public bool NeedPassword { get; set; }

    /// <summary>提取码不对。</summary>
    public bool WrongPassword { get; set; }

    /// <summary>分享标题（有的网盘不给，就用链接里的 ID）。</summary>
    public string? ShareTitle { get; set; }

    /// <summary>当前目录 ID，根目录为 "0" / "root" 之类，由 Provider 决定。</summary>
    public string? ParentId { get; set; }

    public List<PanEntry> Entries { get; set; } = new();

    /// <summary>每一步 HTTP 调用的真实返回，排查接口改版时看这个。</summary>
    public List<string> Diagnostics { get; set; } = new();

    public static PanListResult Fail(string error) => new() { Ok = false, Error = error };

    public static PanListResult NeedPwd(bool wrong) => new()
    {
        Ok = false,
        NeedPassword = true,
        WrongPassword = wrong,
        Error = wrong ? "提取码不正确，请重新输入" : "这个分享需要提取码（访问密码），请输入后重试"
    };
}

/// <summary>取到的直链。</summary>
public sealed class PanDownloadLink
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public long Size { get; set; }

    /// <summary>下载时必须带上的请求头（Referer / User-Agent 等，缺了会被 403）。</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>下载时必须带上的 Cookie（夸克/百度拿直链后仍需登录态）。</summary>
    public string? Cookies { get; set; }

    public string? Message { get; set; }

    public List<string> Diagnostics { get; set; } = new();

    public static PanDownloadLink Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>一次 HTTP 调用的结果，带 JSON 便捷访问。</summary>
public sealed class PanResponse
{
    public int Status { get; init; } = -1;

    public string Body { get; init; } = string.Empty;

    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>-1 表示连请求都没发出去（网络错误），<see cref="NetworkError"/> 里有原因。</summary>
    public string? NetworkError { get; init; }

    public bool IsSuccess => Status is >= 200 and < 300;

    public string Snippet => Body.Length <= 500 ? Body : Body[..500] + "…";

    private JsonDocument? _doc;
    private bool _parsed;

    /// <summary>把正文当 JSON 解析（失败返回 null，不抛异常）。</summary>
    public JsonDocument? TryJson()
    {
        if (_parsed) return _doc;
        _parsed = true;
        if (string.IsNullOrWhiteSpace(Body)) return null;
        try { _doc = JsonDocument.Parse(Body); } catch { _doc = null; }
        return _doc;
    }

    public JsonElement? Root => TryJson()?.RootElement;

    /// <summary>按路径取字符串，例如 <c>Get("data", "stoken")</c>。</summary>
    public string? Get(params string[] path) => PanJson.Str(Root, path);

    /// <summary>按路径取 long，例如 <c>GetLong("code")</c>。</summary>
    public long GetLong(params string[] path) => PanJson.Long(Root, path);
}

/// <summary>链接里提取码的小工具（四家网盘的写法五花八门，集中处理）。</summary>
public static class PanUrl
{
    private static readonly string[] PasswordKeys =
    {
        "pwd", "password", "passcode", "share_pwd", "extraction_code", "code"
    };

    /// <summary>
    /// 从分享链接里抠提取码。支持 <c>?pwd=abcd</c>、<c>&amp;password=</c>、
    /// <c>#pwd=</c> 这几种常见写法；没有就返回 null。
    /// </summary>
    public static string? ExtractPassword(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var hash = url.IndexOf('#');
            if (hash >= 0 && hash < url.Length - 1)
            {
                var fromFragment = Query(url[(hash + 1)..]);
                if (fromFragment is not null) return fromFragment;
            }

            var q = url.IndexOf('?');
            if (q >= 0 && q < url.Length - 1) return Query(url[(q + 1)..]);
            return null;
        }
        catch { return null; }
    }

    private static string? Query(string query)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].Trim();
            if (!PasswordKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            if (value.Length > 0) return value;
        }
        return null;
    }

    /// <summary>把 <c>/s/1AbCdEf</c> 里的 ID 抠出来（各家用不上就直接正则取）。</summary>
    public static string Host(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 解 123 云盘那种 <c>…/download-v2/?params=&lt;base64url&gt;</c> 跳转地址：
    /// params 解出来就是下一跳的真实地址（网页版靠 JS 跳，我们直接解，不用跑 JS）。
    /// 不是这种地址返回 null。
    /// </summary>
    public static string? TryDecodeRedirectParams(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var q = url.IndexOf("params=", StringComparison.OrdinalIgnoreCase);
        if (q < 0) return null;

        var start = q + "params=".Length;
        var end = url.IndexOf('&', start);
        var raw = end < 0 ? url[start..] : url[start..end];
        if (raw.Length == 0) return null;

        try
        {
            var b64 = Uri.UnescapeDataString(raw).Replace('-', '+').Replace('_', '/');
            while (b64.Length % 4 != 0) b64 += "=";
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch { return null; }
    }
}

/// <summary>JSON 取值小工具：容忍 null / 类型不一致，路径找不到就返回 null。</summary>
internal static class PanJson
{
    public static JsonElement? At(JsonElement? root, params string[] path)
    {
        if (root is not { } cur) return null;
        foreach (var key in path)
        {
            if (cur.ValueKind == JsonValueKind.Array && int.TryParse(key, out var idx))
            {
                if (idx < 0 || idx >= cur.GetArrayLength()) return null;
                cur = cur[idx];
                continue;
            }
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(key, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    public static string? Str(JsonElement? root, params string[] path)
    {
        var el = At(root, path);
        if (el is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long Long(JsonElement? root, params string[] path)
    {
        var el = At(root, path);
        if (el is not { } v) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : 0;
        if (v.ValueKind == JsonValueKind.String)
            return long.TryParse(v.GetString(), out var n) ? n : 0;
        return 0;
    }

    public static bool Bool(JsonElement? root, params string[] path)
    {
        var el = At(root, path);
        if (el is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    /// <summary>把数组里的每一项交给回调（数组不存在时什么都不做）。</summary>
    public static void ForEach(JsonElement? root, string key, Action<JsonElement> action)
    {
        if (root is not { } r || r.ValueKind != JsonValueKind.Object) return;
        if (!r.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var item in arr.EnumerateArray()) action(item);
    }
}
