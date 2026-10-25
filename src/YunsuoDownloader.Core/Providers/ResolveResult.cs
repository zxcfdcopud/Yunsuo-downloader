using YunsuoDownloader.Core.Models;

namespace YunsuoDownloader.Core.Providers;

/// <summary>链接解析结果：把一个"页面链接 / 网盘分享链接 / 特殊协议"变成可下载的地址。</summary>
public sealed class ResolveResult
{
    public bool Success { get; set; }

    /// <summary>真正交给下载引擎的地址（直链、m3u8、磁力，或视频页面 URL 交给 yt-dlp）。</summary>
    public string? Url { get; set; }

    public DownloadKind Kind { get; set; } = DownloadKind.File;

    /// <summary>建议文件名（不含扩展名）。</summary>
    public string? FileName { get; set; }

    public string? Title { get; set; }

    public string? Referer { get; set; }

    public string? UserAgent { get; set; }

    public string? Cookies { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ProviderName { get; set; }

    /// <summary>必须交给 yt-dlp 处理（视频页 / m3u8 / dash）。</summary>
    public bool PreferYtDlp { get; set; }

    /// <summary>建议用 aria2 下载（普通直链 / 磁力 / 种子）。</summary>
    public bool PreferAria2 { get; set; }

    /// <summary>站点标识（bilibili / youtube / douyin …）。</summary>
    public string? Site { get; set; }

    /// <summary>视频时长（秒），来自插件或 yt-dlp。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>封面图。</summary>
    public string? Thumbnail { get; set; }

    public long TotalBytes { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>解析过程的说明，展示给用户（例如"已通过阿里云盘匿名分享解析"）。</summary>
    public string? Message { get; set; }

    /// <summary>
    /// 这个分享需要提取码（访问密码），而且当前没有提供或提供的不对。
    /// 界面看到它就应该弹输入框让用户填，然后用 <see cref="ResolveContext.SharePassword"/> 重试。
    /// </summary>
    public bool NeedPassword { get; set; }

    /// <summary>需要提取码的 Provider 名字（Baidu / Quark / Aliyun / Pan123）。</summary>
    public string? PasswordProvider { get; set; }

    /// <summary>分享 ID / surl，用于把用户填的提取码和具体分享对应起来。</summary>
    public string? ShareId { get; set; }

    /// <summary>本次解析每一步的调用记录（verbose 模式下会全部展示，便于排查接口改版）。</summary>
    public List<string> Diagnostics { get; set; } = new();

    /// <summary>提取码错误（而不是"没提供"）。</summary>
    public bool PasswordWrong { get; set; }

    public static ResolveResult Ok(string url, DownloadKind kind, string provider) => new()
    {
        Success = true,
        Url = url,
        Kind = kind,
        ProviderName = provider
    };

    public static ResolveResult Fail(string provider, string error) => new()
    {
        Success = false,
        ProviderName = provider,
        ErrorMessage = error
    };

    /// <summary>构造一个"需要提取码"的结果。</summary>
    public static ResolveResult NeedPwd(string provider, string? shareId, string? hint = null) => new()
    {
        Success = false,
        NeedPassword = true,
        PasswordProvider = provider,
        ShareId = shareId,
        ProviderName = provider,
        ErrorMessage = hint ?? "这个分享需要提取码，请输入后重试"
    };

    /// <summary>构造一个"提取码不对"的结果。</summary>
    public static ResolveResult WrongPwd(string provider, string? shareId, string? raw = null) => new()
    {
        Success = false,
        NeedPassword = true,
        PasswordWrong = true,
        PasswordProvider = provider,
        ShareId = shareId,
        ProviderName = provider,
        ErrorMessage = "提取码不正确，请重新输入" + (raw is null ? "" : $"（接口返回：{raw}）")
    };
}
