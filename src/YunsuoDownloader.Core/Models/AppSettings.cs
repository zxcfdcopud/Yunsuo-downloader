using System.Text.Json.Serialization;

namespace YunsuoDownloader.Core.Models;

/// <summary>
/// 浏览器插件自动采集到的站点 Cookie（按域名缓存）。
///
/// 为什么需要：网盘和抖音/快手这类站点都要登录态，让用户手动去 F12 里翻 Cookie
/// 再粘贴进来非常难用。插件本来就在浏览器里、有 <c>cookies</c> 权限，
/// 于是改成：**用户在浏览器里正常访问过一次站点，插件就把该站点的 Cookie 带过来并存下来**，
/// 之后用户在程序里直接粘贴链接也能用这份登录态。
///
/// 和 <see cref="PanCredential"/> 一样用 DPAPI 加密后落盘，不存明文。
/// </summary>
public sealed class BrowserCookie
{
    /// <summary>域名（小写，不带端口），如 pan.baidu.com / www.douyin.com。</summary>
    public string Domain { get; set; } = string.Empty;

    [JsonIgnore] public string Cookie { get; set; } = string.Empty;

    /// <summary>加密后的 Cookie（Base64）。序列化时只写这个字段。</summary>
    public string? CookieProtected { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>网盘登录态（Cookie）。以 DPAPI 加密后落盘，见 <see cref="Utils.SecretProtector"/>。</summary>
public sealed class PanCredential
{
    /// <summary>Provider 名称，如 Baidu / Aliyun / Quark / Pan123。</summary>
    public string Provider { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [JsonIgnore] public string Cookie { get; set; } = string.Empty;

    /// <summary>加密后的 Cookie（Base64）。序列化时只写这个字段。</summary>
    public string? CookieProtected { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Remark { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>应用设置，持久化到 %AppData%\YunsuoDownloader\settings.json。</summary>
public sealed class AppSettings
{
    // ---------------------------------------------------------- 需求 9：默认下载位置
    public string DownloadDirectory { get; set; } = DefaultDownloadDirectory;

    public bool AskForDirectoryEachTime { get; set; }

    public bool CreateSubfolderPerTask { get; set; }

    // ---------------------------------------------------------- 需求 7：多线程 / 速度
    /// <summary>同时下载的任务数。</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>
    /// 线程数策略：Auto = 按文件大小和服务器能力自动推荐（默认，见 <see cref="Services.ConnectionAdvisor"/>）；
    /// Fixed = 用 <see cref="ConnectionsPerDownload"/> 的固定值。
    /// </summary>
    public string ConnectionMode { get; set; } = "Auto";

    /// <summary>单个任务的连接（分片）线程数，aria2 --split / --max-connection-per-server。</summary>
    public int ConnectionsPerDownload { get; set; } = 8;

    /// <summary>自动模式下允许的最大线程数（默认 16；觉得"线程多了反而慢"可以调到 8）。</summary>
    public int AutoConnectionCap { get; set; } = 16;

    /// <summary>HLS/DASH 并发分片数（yt-dlp --concurrent-fragments），HLS 提速的关键。</summary>
    public int ConcurrentFragments { get; set; } = 16;

    /// <summary>全局限速 KB/s，0 = 不限速。</summary>
    public long GlobalSpeedLimitKbps { get; set; }

    /// <summary>单任务最小分片大小（KB）。</summary>
    public int MinSplitSizeKb { get; set; } = 1024;

    public bool AutoStartDownload { get; set; } = true;

    public bool UseAria2ForDirectLinks { get; set; } = true;

    public bool UseYtDlpForVideoPages { get; set; } = true;

    public bool AutoConvertHlsToMp4 { get; set; } = true;

    /// <summary>best / 2160 / 1080 / 720 / 480</summary>
    public string PreferredVideoQuality { get; set; } = "best";

    // ---------------------------------------------------------- 需求 2/3：浏览器插件
    public bool MediaServerEnabled { get; set; } = true;

    public string MediaServerHost { get; set; } = "127.0.0.1";

    public int MediaServerPort { get; set; } = 16888;

    /// <summary>浏览器插件连接令牌（首次运行自动生成）。</summary>
    public string MediaServerToken { get; set; } = string.Empty;

    public bool ExtensionNotificationsEnabled { get; set; } = true;

    // ---------------------------------------------------------- 需求 10：检查更新
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>更新源；留空则依次尝试 exe 同目录 update.json 与内置 GitHub 源。</summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;

    public DateTime? LastUpdateCheck { get; set; }

    public string? SkippedVersion { get; set; }

    // ---------------------------------------------------------- 外观
    /// <summary>System / Light / Dark</summary>
    public string Theme { get; set; } = "System";

    public string BackdropType { get; set; } = "Mica";

    // ---------------------------------------------------------- 行为
    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    public int MaxHistoryItems { get; set; } = 500;

    public bool SaveHistory { get; set; } = true;

    /// <summary>从列表中移除任务时是否同时删除已下载的文件。</summary>
    public bool DeleteFileWithTask { get; set; }

    /// <summary>完成后自动打开文件所在目录。</summary>
    public bool OpenFolderWhenCompleted { get; set; }

    // ---------------------------------------------------------- 网络 / 代理（YouTube 等站点常需要）
    /// <summary>None / System / Manual</summary>
    public string ProxyMode { get; set; } = "None";

    public string ProxyUrl { get; set; } = string.Empty;

    public string? UserAgentOverride { get; set; }

    // ---------------------------------------------------------- 外部工具路径（留空自动探测 tools 目录）
    public string? Aria2Path { get; set; }
    public string? YtDlpPath { get; set; }
    public string? FfmpegPath { get; set; }

    /// <summary>网盘登录态。</summary>
    public List<PanCredential> PanCredentials { get; set; } = new();

    /// <summary>
    /// 浏览器插件自动采集的站点 Cookie（按域名）。让用户不用手填 Cookie：
    /// 在浏览器里正常访问一次站点，插件就会把 Cookie 带过来存进这里。
    /// </summary>
    public List<BrowserCookie> BrowserCookies { get; set; } = new();

    /// <summary>是否允许插件采集并保存站点 Cookie（默认开；关掉则完全不落盘）。</summary>
    public bool CaptureBrowserCookies { get; set; } = true;

    // ---------------------------------------------------------- 计算属性
    /// <summary>
    /// 默认下载位置：用户「下载」文件夹下的 YunsuoDownloader。
    ///
    /// 之前这里硬编码了开发机的 <c>F:\下载器工程\downloads</c>，发布到别人电脑上
    /// （没有 F 盘、也没有这个目录）时会退化成 C:\Users\xxx\Downloads\YunsuoDownloader，
    /// 但界面上仍写着"默认使用 F 盘工程目录"——对用户来说是错的。
    /// 现在统一按系统「下载」文件夹推导，任何机器上都合理。
    /// </summary>
    [JsonIgnore]
    public static string DefaultDownloadDirectory
    {
        get
        {
            try
            {
                // 优先用系统"下载"文件夹（会被 OneDrive / 组策略重定向，所以要取真实路径）
                var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var known = Path.Combine(downloads, "Downloads");
                var target = Directory.Exists(known) ? known : downloads;
                return Path.Combine(target, AppName);
            }
            catch
            {
                // 极端情况下连用户目录都取不到，退回程序所在盘的 Downloads
                try
                {
                    return Path.Combine(
                        Path.GetPathRoot(AppContext.BaseDirectory) ?? @"C:\", "Downloads", AppName);
                }
                catch { return Path.Combine(Path.GetTempPath(), AppName); }
            }
        }
    }

    /// <summary>程序名，用于拼默认下载目录。</summary>
    private const string AppName = "YunsuoDownloader";

    [JsonIgnore] public long GlobalSpeedLimitBytesPerSec => GlobalSpeedLimitKbps <= 0 ? 0 : GlobalSpeedLimitKbps * 1024;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
