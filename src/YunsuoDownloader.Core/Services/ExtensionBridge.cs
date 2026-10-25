using System.Reflection;
using System.Text.Json;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>
/// 把本地 WebSocket 服务与下载管理器接起来：处理插件握手、令牌校验、投递下载任务、回推通知。
/// </summary>
public sealed class ExtensionBridge : IAsyncDisposable
{
    private readonly MediaServer _server = new();
    private readonly DownloadManager _manager;
    private readonly SettingsService _settings;

    public ExtensionBridge(DownloadManager manager, SettingsService settings)
    {
        _manager = manager;
        _settings = settings;
        _server.MessageReceived += OnMessageReceived;
        _server.ClientConnected += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _server.ClientDisconnected += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public MediaServer Server => _server;
    public bool IsRunning => _server.IsRunning;
    public int ClientCount => _server.ClientCount;
    public string? LastError => _server.LastError;
    public int AuthFailureCount => _server.AuthFailureCount;

    /// <summary>最近一次插件握手信息，用于界面展示。</summary>
    public string? LastClientInfo { get; private set; }

    /// <summary>最近一次收到的任务标题，用于界面展示。</summary>
    public string? LastReceivedTitle { get; private set; }

    public event EventHandler<string>? StatusMessage;
    public event EventHandler? StateChanged;

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0";

    public async Task<bool> StartAsync()
    {
        var s = _settings.Current;
        if (!s.MediaServerEnabled)
        {
            StatusMessage?.Invoke(this, "浏览器插件服务已在设置中关闭");
            return false;
        }
        var ok = await _server.StartAsync(s.MediaServerHost, s.MediaServerPort).ConfigureAwait(false);
        StatusMessage?.Invoke(this, ok
            ? $"插件服务已启动：ws://{s.MediaServerHost}:{s.MediaServerPort}/media"
            : _server.LastError ?? "插件服务启动失败");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public async Task StopAsync()
    {
        await _server.StopAsync().ConfigureAwait(false);
        StatusMessage?.Invoke(this, "插件服务已停止");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        return await StartAsync().ConfigureAwait(false);
    }

    public Task NotifyAsync(string level, string message)
        => _server.IsRunning ? _server.NotifyExtensionAsync(level, message) : Task.CompletedTask;

    public Task PushConfigAsync()
        => _server.IsRunning ? _server.PushConfigAsync(_settings.Current.DownloadDirectory) : Task.CompletedTask;

    // ------------------------------------------------------------------
    private async void OnMessageReceived(object? sender, ExtensionMessage ev)
    {
        try
        {
            await HandleAsync(ev).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理插件消息失败");
        }
    }

    private async Task HandleAsync(ExtensionMessage ev)
    {
        var conn = ev.Connection;
        var msg = ev.Message;
        var type = msg.Type?.ToLowerInvariant() ?? "";

        switch (type)
        {
            case "hello":
            {
                var expected = _settings.Current.MediaServerToken;
                if (!string.IsNullOrEmpty(expected) &&
                    !string.Equals(expected, msg.Token, StringComparison.Ordinal))
                {
                    conn.Authenticated = false;
                    _server.NoteAuthFailure();
                    LastClientInfo = $"令牌不匹配（来自 {conn.RemoteEndPoint}）";
                    Log.Warn($"插件令牌不匹配，已拒绝：{conn.RemoteEndPoint}");
                    StatusMessage?.Invoke(this, "插件令牌不匹配，请把 App 里的令牌复制到插件设置中");
                    await conn.CloseAsync("令牌不匹配：请在下载器「设置 → 浏览器插件」复制令牌后填入插件", 4001)
                              .ConfigureAwait(false);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }

                conn.Authenticated = true;
                var s = _settings.Current;
                var ack = new HelloAckMessage
                {
                    AppVersion = AppVersion,
                    DownloadDir = s.DownloadDirectory,
                    Settings = new Dictionary<string, object?>
                    {
                        ["maxConcurrentDownloads"] = s.MaxConcurrentDownloads,
                        ["connectionsPerDownload"] = s.ConnectionsPerDownload,
                        ["connectionMode"] = s.ConnectionMode,
                        ["autoConnectionCap"] = s.AutoConnectionCap,
                        ["preferredVideoQuality"] = s.PreferredVideoQuality,
                        ["autoConvertHlsToMp4"] = s.AutoConvertHlsToMp4
                    }
                };
                await conn.SendTextAsync(JsonSerializer.Serialize(ack, JsonUtil.Compact)).ConfigureAwait(false);

                LastClientInfo = $"{msg.Browser ?? "浏览器"} 插件 v{msg.Version ?? "?"} 已连接（{conn.RemoteEndPoint}）";
                StatusMessage?.Invoke(this, "浏览器插件已连接");
                Log.Info(LastClientInfo!);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            case "ping":
                await conn.SendTextAsync(JsonSerializer.Serialize(new PongMessage(), JsonUtil.Compact)).ConfigureAwait(false);
                return;
        }

        if (!conn.Authenticated)
        {
            Log.Warn($"忽略未认证连接的消息：{type}");
            return;
        }

        switch (type)
        {
            case "add":
            {
                var tasks = msg.EnumerateTasks().ToList();
                if (tasks.Count == 0)
                {
                    await ReplyAddAckAsync(conn, 0, 0, "没有可下载的媒体").ConfigureAwait(false);
                    return;
                }

                LastReceivedTitle = tasks[0].Title ?? tasks[0].FileName ?? tasks[0].Url;
                var (accepted, rejected, errors) = await _manager.AddFromExtensionAsync(tasks).ConfigureAwait(false);
                var summary = accepted > 0
                    ? $"已接收 {accepted} 个任务" + (rejected > 0 ? $"，{rejected} 个失败" : "")
                    : "任务未能加入下载：" + string.Join("；", errors.Take(2));

                await ReplyAddAckAsync(conn, accepted, rejected, summary).ConfigureAwait(false);
                StatusMessage?.Invoke(this, summary);
                Log.Info($"[插件] {summary}");

                if (accepted > 0)
                {
                    await _server.NotifyExtensionAsync("success", summary).ConfigureAwait(false);
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            default:
                Log.Info($"收到未处理的插件消息类型：{type}");
                return;
        }
    }

    private static Task ReplyAddAckAsync(MediaServer.ClientConnection conn, int accepted, int rejected, string message)
        => conn.SendTextAsync(JsonSerializer.Serialize(new AddAckMessage
        {
            Accepted = accepted,
            Rejected = rejected,
            Message = message
        }, JsonUtil.Compact));

    /// <summary>下载完成/失败时推给插件，让浏览器也能弹通知。</summary>
    public async Task NotifyDownloadResultAsync(DownloadItem item, bool success)
    {
        if (!_server.IsRunning || _server.ClientCount == 0) return;
        var level = success ? "success" : "error";
        var text = success
            ? $"下载完成：{Format.Truncate(item.DisplayName, 60)}"
            : $"下载失败：{Format.Truncate(item.DisplayName, 40)} — {Format.Truncate(item.ErrorMessage, 80)}";
        if (!_settings.Current.ExtensionNotificationsEnabled) return;
        await NotifyAsync(level, text).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);
}
