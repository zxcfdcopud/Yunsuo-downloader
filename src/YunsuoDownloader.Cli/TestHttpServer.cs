using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YunsuoDownloader.Cli;

/// <summary>
/// 自检用的极简 HTTP 静态文件服务器：支持 HEAD / GET / Range（206），
/// 用来在本地端到端验证多连接分片下载、断点续传、m3u8 下载，不依赖外网。
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string RootUrl => $"http://127.0.0.1:{Port}/";
    public int RequestCount;

    /// <summary>带 Range 头的请求数（可用来证明"多连接分片"确实发生了）。</summary>
    public int RangeRequestCount;

    /// <summary>服务端限速（字节/秒），0 = 不限。用于制造可观测的暂停/续传窗口。</summary>
    public long ThrottleBytesPerSecond { get; set; }

    public TestHttpServer(string root)
    {
        _root = root;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct).ConfigureAwait(false); }
                catch { /* 忽略 */ }
                finally { try { client.Close(); } catch { } }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine)) return;
        Interlocked.Increment(ref RequestCount);

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var method = parts[0].ToUpperInvariant();
        var rawPath = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct).ConfigureAwait(false)))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        var pathPart = rawPath.Split('?')[0].TrimStart('/');
        if (string.IsNullOrEmpty(pathPart)) pathPart = "index.html";
        var full = Path.GetFullPath(Path.Combine(_root, Uri.UnescapeDataString(pathPart)));

        if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct)
                .ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(full);
        var contentType = full.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl"
            : full.EndsWith(".ts") ? "video/mp2t"
            : full.EndsWith(".mp4") ? "video/mp4"
            : "application/octet-stream";

        long start = 0, end = info.Length - 1;
        bool partial = false;

        if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref RangeRequestCount);
            var spec = range[6..].Split('-');
            if (spec.Length == 2)
            {
                if (long.TryParse(spec[0], out var s)) start = s;
                if (long.TryParse(spec[1], out var e)) end = e;
                if (end >= info.Length) end = info.Length - 1;
                if (start > end) { start = 0; end = info.Length - 1; }
                else partial = true;
            }
        }

        var length = end - start + 1;
        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {length}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{info.Length}\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await WriteAsync(stream, sb.ToString(), ct).ConfigureAwait(false);
        if (method == "HEAD") return;

        await using var fs = File.OpenRead(full);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        long remaining = length;
        long sent = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = await fs.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n <= 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
            sent += n;

            var rate = ThrottleBytesPerSecond;
            if (rate > 0)
            {
                var expectedMs = sent * 1000.0 / rate;
                var delay = expectedMs - clock.Elapsed.TotalMilliseconds;
                if (delay > 1) await Task.Delay((int)delay, ct).ConfigureAwait(false);
            }
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { await Task.WhenAny(_loop, Task.Delay(500)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
