using System.Text.Json.Serialization;

namespace YunsuoDownloader.Core.Models;

/// <summary>
/// 浏览器插件 &lt;-&gt; 桌面 App 的 WebSocket 消息信封。
/// 字段名必须与 <c>extension/</c> 里的协议保持一致（见 docs/PROTOCOL.md）。
/// </summary>
public sealed class MediaMessage
{
    [JsonPropertyName("type")] public string? Type { get; set; }

    // hello
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("browser")] public string? Browser { get; set; }

    // add（兼容单条 url 与 tasks 数组两种写法）
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("tasks")] public List<MediaTaskDto>? Tasks { get; set; }

    // notify
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    // 预留：批量操作 / 查询
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }

    public IEnumerable<MediaTaskDto> EnumerateTasks()
    {
        if (Tasks is { Count: > 0 })
            foreach (var t in Tasks) yield return t;
        else if (!string.IsNullOrWhiteSpace(Url))
            yield return new MediaTaskDto { Url = Url };
    }
}

public sealed class MediaTaskDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("filename")] public string? FileName { get; set; }
    [JsonPropertyName("referer")] public string? Referer { get; set; }
    [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
    [JsonPropertyName("cookies")] public string? Cookies { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("isHls")] public bool IsHls { get; set; }
    [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }

    // ---- 编码格式：抖音等站点大量使用 HEVC(hvc1/hev1)，要能告知用户"这可能放不出来" ----
    [JsonPropertyName("codec")] public string? Codec { get; set; }
    [JsonPropertyName("codecFull")] public string? CodecFull { get; set; }
    [JsonPropertyName("hevc")] public bool Hevc { get; set; }

    /// <summary>
    /// 文件真实总长（字节）。插件用 Content-Range 的总长回填：
    /// 抖音这类站点用 Range 分段拉同一个 mp4，只取单段的 Content-Length 会显示成"只有几 MB"。
    /// </summary>
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }

    // ---- 音视频分离轨（抖音/快手常见：视频轨没声音，音频单独一条 URL）----
    /// <summary>这一条本身是独立音轨（kind 已被插件降级为 file）。</summary>
    [JsonPropertyName("audioOnly")] public bool AudioOnly { get; set; }

    /// <summary>与视频配套的音频 URL（视频条目上才有）。</summary>
    [JsonPropertyName("audioUrl")] public string? AudioUrl { get; set; }
    [JsonPropertyName("audioCodec")] public string? AudioCodec { get; set; }

    /// <summary>视频轨下载完需要把 audioUrl 合进来（ffmpeg -c copy）。</summary>
    [JsonPropertyName("needsMerge")] public bool NeedsMerge { get; set; }
}

// ------------------------------------------------------------------ App -> 插件

public sealed class HelloAckMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "hello-ack";
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("downloadDir")] public string DownloadDir { get; set; } = string.Empty;
    [JsonPropertyName("settings")] public Dictionary<string, object?> Settings { get; set; } = new();
}

public sealed class AddAckMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "add-ack";
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("rejected")] public int Rejected { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class NotifyMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "notify";
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class ConfigMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "config";
    [JsonPropertyName("downloadDir")] public string DownloadDir { get; set; } = string.Empty;
}

public sealed class PongMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "pong";
}
