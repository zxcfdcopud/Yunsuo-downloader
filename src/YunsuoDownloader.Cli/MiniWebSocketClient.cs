using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace YunsuoDownloader.Cli;

/// <summary>
/// 自检用的极简 WebSocket 客户端（RFC6455）：客户端必须给帧加掩码，服务端不加。
/// 只实现自检需要的文本帧、ping/pong、close。
/// </summary>
internal sealed class MiniWebSocketClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader;
    private readonly List<string> _messages = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _inbox = new(0);

    public int CloseCode { get; private set; } = -1;
    public bool IsClosed { get; private set; }

    public async Task<bool> ConnectAsync(string host, int port, string path, TimeSpan timeout)
    {
        _tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        await _tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        _stream = _tcp.GetStream();

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var req = $"GET {path} HTTP/1.1\r\n" +
                  $"Host: {host}:{port}\r\n" +
                  "Upgrade: websocket\r\n" +
                  "Connection: Upgrade\r\n" +
                  $"Sec-WebSocket-Key: {key}\r\n" +
                  "Sec-WebSocket-Version: 13\r\n\r\n";
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(req), cts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(cts.Token).ConfigureAwait(false);

        var header = await ReadHttpHeaderAsync(_stream, cts.Token).ConfigureAwait(false);
        if (header is null || !header.Contains("101", StringComparison.Ordinal)) return false;

        _reader = Task.Run(ReadLoopAsync);
        return true;
    }

    private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 16 * 1024)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n <= 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
                return sb.ToString();
        }
        return null;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var header = await ReadExactAsync(2).ConfigureAwait(false);
                if (header is null) break;
                int opcode = header[0] & 0x0F;
                long len = header[1] & 0x7F;

                if (len == 126)
                {
                    var ext = await ReadExactAsync(2).ConfigureAwait(false);
                    if (ext is null) break;
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    var ext = await ReadExactAsync(8).ConfigureAwait(false);
                    if (ext is null) break;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }

                bool masked = (header[1] & 0x80) != 0;
                byte[]? mask = null;
                if (masked)
                {
                    mask = await ReadExactAsync(4).ConfigureAwait(false);
                    if (mask is null) break;
                }
                var payload = len > 0 ? await ReadExactAsync((int)len).ConfigureAwait(false) : Array.Empty<byte>();
                if (payload is null) break;

                if (opcode == 0x8)
                {
                    CloseCode = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : 1000;
                    IsClosed = true;
                    _inbox.Release();
                    break;
                }
                if (opcode == 0x1)
                {
                    lock (_lock) _messages.Add(Encoding.UTF8.GetString(payload));
                    _inbox.Release();
                }
            }
        }
        catch { /* 连接断开 */ }
        finally { IsClosed = true; try { _inbox.Release(); } catch { } }
    }

    private async Task<byte[]?> ReadExactAsync(int count)
    {
        if (count == 0) return Array.Empty<byte>();
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(read, count - read)).ConfigureAwait(false);
            if (n <= 0) return null;
            read += n;
        }
        return buffer;
    }

    public async Task SendAsync(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var mask = RandomNumberGenerator.GetBytes(4);

        using var ms = new MemoryStream();
        ms.WriteByte(0x81);                            // FIN + text
        if (payload.Length < 126) ms.WriteByte((byte)(0x80 | payload.Length));
        else if (payload.Length <= ushort.MaxValue)
        {
            ms.WriteByte(0x80 | 126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)(payload.Length & 0xFF));
        }
        else
        {
            ms.WriteByte(0x80 | 127);
            long len = payload.Length;
            for (int i = 7; i >= 0; i--) ms.WriteByte((byte)((len >> (8 * i)) & 0xFF));
        }
        ms.Write(mask, 0, 4);
        for (int i = 0; i < payload.Length; i++) ms.WriteByte((byte)(payload[i] ^ mask[i % 4]));

        var data = ms.ToArray();
        await _stream!.WriteAsync(data).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    public async Task<string?> WaitForMessageAsync(string contains, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                var hit = _messages.FirstOrDefault(m => m.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) { _messages.Remove(hit); return hit; }
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try { await _inbox.WaitAsync(remaining).ConfigureAwait(false); } catch { break; }
        }
        lock (_lock) return _messages.LastOrDefault();
    }

    public async Task<int> WaitForCloseAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && CloseCode < 0)
            await Task.Delay(80).ConfigureAwait(false);
        return CloseCode;
    }

    public void Dispose()
    {
        try { _tcp?.Close(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _inbox.Dispose();
    }
}
