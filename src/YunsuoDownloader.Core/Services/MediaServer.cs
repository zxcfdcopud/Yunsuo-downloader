using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>插件消息 + 它的来源连接。</summary>
public sealed record ExtensionMessage(MediaServer.ClientConnection Connection, MediaMessage Message);

/// <summary>
/// 浏览器插件与本机 App 之间的本地 WebSocket 服务。
/// 刻意用 <see cref="TcpListener"/> 自己实现 RFC6455 握手与帧编解码：
///   * 不需要管理员权限注册 HTTP.sys URL ACL（HttpListener 在非 localhost 前缀下需要管理员）
///   * 零第三方依赖，避免引入 Fleck / Websocket.Client 之类的包
/// 只监听回环地址，外部网络无法访问。
/// </summary>
public sealed class MediaServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public string Host { get; private set; } = "127.0.0.1";
    public string? LastError { get; private set; }
    public int ClientCount => _clients.Count;

    /// <summary>握手令牌校验失败次数（用于诊断）。</summary>
    public int AuthFailureCount { get; private set; }

    public void NoteAuthFailure() => AuthFailureCount++;

    public event EventHandler<ExtensionMessage>? MessageReceived;
    public event EventHandler<ClientConnection>? ClientConnected;
    public event EventHandler<ClientConnection>? ClientDisconnected;

    public async Task<bool> StartAsync(string host, int port, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        Port = port;

        try
        {
            var address = IPAddress.TryParse(Host, out var ip) && IPAddress.IsLoopback(ip)
                ? ip
                : IPAddress.Loopback;      // 安全考虑：只允许回环
            _listener = new TcpListener(address, port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            LastError = null;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log.Info($"浏览器插件服务已启动：ws://{Host}:{Port}/media");
            return true;
        }
        catch (SocketException ex)
        {
            IsRunning = false;
            LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {port} 已被占用，请到「设置 → 浏览器插件」换一个端口"
                : $"监听失败：{ex.Message}";
            Log.Error(ex, "启动插件服务失败");
            return false;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = ex.Message;
            Log.Error(ex, "启动插件服务失败");
            return false;
        }
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;

        foreach (var c in _clients.Values)
        {
            try { await c.CloseAsync("服务已停止").ConfigureAwait(false); } catch { }
        }
        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try { await Task.WhenAny(_acceptLoop, Task.Delay(1500)).ConfigureAwait(false); } catch { }
        }
        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"接受插件连接失败：{ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var conn = new ClientConnection(tcp);
        try
        {
            tcp.NoDelay = true;
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));

            if (!await conn.DoHandshakeAsync(handshakeCts.Token).ConfigureAwait(false))
            {
                await conn.CloseAsync("握手失败").ConfigureAwait(false);
                return;
            }

            conn.Id = Guid.NewGuid();
            _clients[conn.Id] = conn;
            Log.Info($"插件已连接：{conn.RemoteEndPoint}");
            ClientConnected?.Invoke(this, conn);
            await conn.SendTextAsync(JsonSerializer.Serialize(new { type = "welcome", app = "YunsuoDownloader", serverTime = DateTime.Now }, JsonUtil.Compact))
                      .ConfigureAwait(false);
            await OnClientLoopAsync(conn, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            Log.Warn($"插件连接异常：{ex.Message}");
        }
        finally
        {
            if (conn.Id != Guid.Empty) _clients.TryRemove(conn.Id, out _);
            try { await conn.CloseAsync("bye").ConfigureAwait(false); } catch { }
            ClientDisconnected?.Invoke(this, conn);
            Log.Info("插件连接已断开");
        }
    }

    private async Task OnClientLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        var message = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(conn.Stream, ct).ConfigureAwait(false);
            if (frame is null) break;

            var (opcode, fin, payload) = frame.Value;

            switch (opcode)
            {
                case 0x8:  // close
                    await conn.CloseAsync("客户端关闭").ConfigureAwait(false);
                    return;
                case 0x9:  // ping
                    await conn.SendFrameAsync(0xA, payload).ConfigureAwait(false);
                    continue;
                case 0xA:  // pong
                    continue;
                case 0x1:
                case 0x2:
                case 0x0:
                    if (opcode != 0x0) message.Clear();
                    if (message.Length + payload.Length > MaxMessageBytes)
                    {
                        Log.Warn("插件消息过大，已丢弃");
                        await conn.CloseAsync("消息过大").ConfigureAwait(false);
                        return;
                    }
                    message.Append(Encoding.UTF8.GetString(payload));
                    if (!fin) continue;
                    await DispatchAsync(conn, message.ToString(), ct).ConfigureAwait(false);
                    message.Clear();
                    continue;
                default:
                    continue;
            }
        }
    }

    private async Task DispatchAsync(ClientConnection conn, string json, CancellationToken ct)
    {
        MediaMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<MediaMessage>(json, JsonUtil.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"插件消息解析失败：{ex.Message}（{Format.Truncate(json, 200)}）");
            return;
        }
        if (msg?.Type is null) return;

        switch (msg.Type.ToLowerInvariant())
        {
            case "ping":
                await conn.SendTextAsync(JsonSerializer.Serialize(new PongMessage(), JsonUtil.Compact)).ConfigureAwait(false);
                return;
            case "hello":
                conn.BrowserName = msg.Browser ?? "unknown";
                conn.ExtensionVersion = msg.Version ?? "?";
                Log.Info($"插件握手：{conn.BrowserName} v{conn.ExtensionVersion}");
                break;
        }

        MessageReceived?.Invoke(this, new ExtensionMessage(conn, msg));
    }

    /// <summary>向所有已连接的插件广播。</summary>
    public async Task BroadcastAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonUtil.Compact);
        foreach (var c in _clients.Values)
        {
            try { await c.SendTextAsync(json).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"广播给插件失败：{ex.Message}"); }
        }
    }

    public Task NotifyExtensionAsync(string level, string message)
        => BroadcastAsync(new NotifyMessage { Level = level, Message = message });

    public Task PushConfigAsync(string downloadDir)
        => BroadcastAsync(new ConfigMessage { DownloadDir = downloadDir });

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------ 帧编解码
    private static async Task<(int Opcode, bool Fin, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
        if (header is null) return null;

        bool fin = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        bool masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var ext = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
            if (ext is null) return null;
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
            if (ext is null) return null;
            length = 0;
            for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
        }

        if (length < 0 || length > MaxMessageBytes)
            throw new InvalidOperationException($"WebSocket 帧过大：{length} 字节");

        byte[]? maskKey = null;
        if (masked)
        {
            maskKey = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
            if (maskKey is null) return null;
        }

        var payload = length > 0 ? await ReadExactAsync(stream, (int)length, ct).ConfigureAwait(false) : Array.Empty<byte>();
        if (payload is null) return null;

        if (masked && maskKey is not null)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i % 4];
        }

        return (opcode, fin, payload);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        if (count == 0) return Array.Empty<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n <= 0) return null;
                read += n;
            }
            var result = new byte[count];
            Array.Copy(buffer, result, count);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // ------------------------------------------------------------------ 客户端
    public sealed class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _closed;

        internal ClientConnection(TcpClient tcp) => _tcp = tcp;

        public Guid Id { get; internal set; } = Guid.Empty;
        public string? BrowserName { get; internal set; }
        public string? ExtensionVersion { get; internal set; }
        public NetworkStream Stream => _tcp.GetStream();
        public string RemoteEndPoint => _tcp.Client.RemoteEndPoint?.ToString() ?? "?";

        /// <summary>供外部（握手后）校验 token 用：设置 token 后可主动踢掉客户端。</summary>
        public string? ProvidedToken { get; internal set; }

        public bool Authenticated { get; internal set; } = true;

        internal async Task<bool> DoHandshakeAsync(CancellationToken ct)
        {
            var request = await ReadHttpRequestAsync(ct).ConfigureAwait(false);
            if (request is null) return false;

            var lines = request.Split("\r\n");
            if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.OrdinalIgnoreCase)) return false;

            string? key = null;
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            }
            if (string.IsNullOrWhiteSpace(key)) return false;

            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n" +
                           "Server: YunsuoDownloader\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            await Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }

        private async Task<string?> ReadHttpRequestAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout)
            {
                int n = await Stream.ReadAsync(one, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
                    return sb.ToString();
                if (sb.Length > 16 * 1024) return null;
            }
            return null;
        }

        public async Task SendTextAsync(string json)
            => await SendFrameAsync(0x1, Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);

        internal async Task SendFrameAsync(int opcode, byte[] payload)
        {
            if (_closed) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var ms = new MemoryStream();
                ms.WriteByte((byte)(0x80 | opcode));   // FIN + opcode
                if (payload.Length < 126)
                {
                    ms.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    ms.WriteByte(126);
                    ms.WriteByte((byte)(payload.Length >> 8));
                    ms.WriteByte((byte)(payload.Length & 0xFF));
                }
                else
                {
                    ms.WriteByte(127);
                    var len = (long)payload.Length;
                    for (int i = 7; i >= 0; i--) ms.WriteByte((byte)((len >> (8 * i)) & 0xFF));
                }
                ms.Write(payload, 0, payload.Length);
                var data = ms.ToArray();
                await Stream.WriteAsync(data).ConfigureAwait(false);
                await Stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"向插件发送失败：{ex.Message}");
                _closed = true;
            }
            finally { _sendLock.Release(); }
        }

        /// <summary>发送关闭帧并断开，可带自定义关闭码（4001 = 令牌错误）。</summary>
        public async Task CloseAsync(string reason, int code = 1000)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                var reasonBytes = Encoding.UTF8.GetBytes(reason);
                var payload = new byte[2 + Math.Min(reasonBytes.Length, 120)];
                payload[0] = (byte)(code >> 8);
                payload[1] = (byte)(code & 0xFF);
                Array.Copy(reasonBytes, 0, payload, 2, payload.Length - 2);
                await SendFrameAsyncDirect(0x8, payload).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            try { _tcp.Close(); } catch { }
        }

        private async Task SendFrameAsyncDirect(int opcode, byte[] payload)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | opcode));
            if (payload.Length < 126) ms.WriteByte((byte)payload.Length);
            else { ms.WriteByte(126); ms.WriteByte((byte)(payload.Length >> 8)); ms.WriteByte((byte)(payload.Length & 0xFF)); }
            ms.Write(payload, 0, payload.Length);
            var data = ms.ToArray();
            await Stream.WriteAsync(data).ConfigureAwait(false);
            await Stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
