using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YunsuoDownloader.Core.Engine;

public sealed class Aria2Exception : Exception
{
    public int Code { get; }
    public Aria2Exception(int code, string message) : base(message) => Code = code;
}

/// <summary>
/// aria2 JSON-RPC over HTTP 客户端。
/// aria2 以 <c>--enable-rpc</c> 启动后监听本地端口，我们通过它派发/查询/控制任务。
/// </summary>
public sealed class Aria2RpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _secret;
    private long _id;

    public Aria2RpcClient(int port, string? secret)
    {
        _endpoint = $"http://127.0.0.1:{port}/jsonrpc";
        _secret = string.IsNullOrEmpty(secret) ? null : secret;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.ConnectionClose = false;
    }

    public string Endpoint => _endpoint;

    private List<object?> WithToken(IEnumerable<object?> args)
    {
        var list = new List<object?>();
        if (_secret is not null) list.Add("token:" + _secret);
        list.AddRange(args);
        return list;
    }

    public async Task<JsonElement> CallAsync(string method, IEnumerable<object?> args, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id).ToString(),
            ["method"] = method,
            ["params"] = WithToken(args)
        };
        return await PostAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> PostAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, Utils.JsonUtil.Compact);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            throw new Aria2Exception(-1, "aria2 返回空响应");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            throw new Aria2Exception(code, $"aria2 ({code}): {msg}");
        }
        if (!root.TryGetProperty("result", out var result))
            throw new Aria2Exception(-1, "aria2 响应缺少 result");
        return result.Clone();
    }

    /// <summary>批量调用（system.multicall），一次网络往返拿多个任务状态，降低开销。</summary>
    public async Task<List<JsonElement>> MulticallAsync(
        IReadOnlyList<(string Method, object?[] Args)> calls, CancellationToken ct = default)
    {
        var results = new List<JsonElement>();
        if (calls.Count == 0) return results;

        var sub = new List<object?>();
        foreach (var (method, args) in calls)
        {
            sub.Add(new Dictionary<string, object?>
            {
                ["methodName"] = method,
                ["params"] = WithToken(args)
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id).ToString(),
            ["method"] = "system.multicall",
            ["params"] = new object?[] { sub }
        };

        var result = await PostAsync(payload, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array) return results;
        foreach (var entry in result.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 0)
                results.Add(entry[0].Clone());
            else
                results.Add(default);   // fault
        }
        return results;
    }

    // ------------------------------------------------------------ 常用封装
    public Task<JsonElement> GetVersionAsync(CancellationToken ct = default)
        => CallAsync("aria2.getVersion", Array.Empty<object?>(), ct);

    public Task<JsonElement> GetGlobalStatAsync(CancellationToken ct = default)
        => CallAsync("aria2.getGlobalStat", Array.Empty<object?>(), ct);

    public async Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object?> options, CancellationToken ct = default)
    {
        var result = await CallAsync("aria2.addUri", new object?[] { uris.ToArray(), options }, ct).ConfigureAwait(false);
        return result.GetString() ?? string.Empty;
    }

    public Task<JsonElement> TellStatusAsync(string gid, IEnumerable<string>? keys = null, CancellationToken ct = default)
        => keys is null
            ? CallAsync("aria2.tellStatus", new object?[] { gid }, ct)
            : CallAsync("aria2.tellStatus", new object?[] { gid, keys.ToArray() }, ct);

    public Task PauseAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.pause", new object?[] { gid }, ct);

    public Task UnpauseAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.unpause", new object?[] { gid }, ct);

    public Task RemoveAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.remove", new object?[] { gid }, ct);

    public Task RemoveResultAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.removeDownloadResult", new object?[] { gid }, ct);

    public Task PurgeResultsAsync(CancellationToken ct = default)
        => CallAsync("aria2.purgeDownloadResult", Array.Empty<object?>(), ct);

    public Task ChangeGlobalOptionAsync(Dictionary<string, object?> options, CancellationToken ct = default)
        => CallAsync("aria2.changeGlobalOption", new object?[] { options }, ct);

    public Task ChangeOptionAsync(string gid, Dictionary<string, object?> options, CancellationToken ct = default)
        => CallAsync("aria2.changeOption", new object?[] { gid, options }, ct);

    public Task ShutdownAsync(CancellationToken ct = default)
        => CallAsync("aria2.shutdown", Array.Empty<object?>(), ct);

    public Task SaveSessionAsync(CancellationToken ct = default)
        => CallAsync("aria2.saveSession", Array.Empty<object?>(), ct);

    public void Dispose() => _http.Dispose();
}
