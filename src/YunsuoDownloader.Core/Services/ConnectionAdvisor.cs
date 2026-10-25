using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>一次"用几个线程"的建议：最终值 + 给用户看的一句话理由。</summary>
public sealed record ConnectionAdvice(int Connections, string Reason, bool IsAuto)
{
    public string ShortText => Connections <= 1 ? "单线程" : $"{Connections} 线程";

    /// <summary>下拉框里「自动」那一项的显示文字。</summary>
    public string AutoDisplay => $"自动（推荐 {ShortText}）";
}

/// <summary>
/// 线程数顾问：多线程不是越多越快。
///   * 小文件多开线程，建连/收尾的开销比省下的时间还多；
///   * 服务器不支持 Range（不分片）时多线程只会反复重下整文件；
///   * 每线程至少要能分到 1 MB，否则分片碎到没有意义；
///   * 网盘直链单连接常被限速，多线程收益明显，但线程太多容易被风控。
/// 所以推荐值 = 按文件大小分档 → 再按服务器能力/每线程下限/上限收敛。
/// </summary>
public static class ConnectionAdvisor
{
    public const int MinConnections = 1;
    public const int MaxConnections = 32;

    /// <summary>自动模式下"每线程至少分到多少字节"（与 aria2 --min-split-size 的最小值一致）。</summary>
    public const long MinBytesPerConnection = 1L << 20;

    public static int Clamp(int n) => Math.Clamp(n, MinConnections, MaxConnections);

    /// <summary>只按文件大小给的档位（不含服务器能力修正）。大小未知时给保守的 8 线程。</summary>
    public static int TierForSize(long bytes) => bytes switch
    {
        <= 0 => 8,                 // 大小未知：aria2 还会按 --min-split-size 自行收敛，8 是稳妥值
        < 1L << 20 => 1,           // < 1 MB
        < 8L << 20 => 2,           // < 8 MB
        < 32L << 20 => 4,          // < 32 MB
        < 128L << 20 => 6,         // < 128 MB
        < 512L << 20 => 8,         // < 512 MB
        < 2L << 30 => 12,          // < 2 GB
        _ => 16                    // >= 2 GB
    };

    /// <summary>
    /// 按"文件大小 + 任务类型 + 服务器是否支持分片"给推荐值。
    /// </summary>
    /// <param name="totalBytes">文件总大小，未知传 0。</param>
    /// <param name="kind">任务类型（BT/磁力走 aria2 的 BT 调度，不看分片）。</param>
    /// <param name="acceptRanges">服务器是否支持 Range：true/false 已知，null 未知。</param>
    /// <param name="panDirect">是不是网盘直链（单连接常被限速）。</param>
    /// <param name="cap">自动模式线程上限。</param>
    public static ConnectionAdvice Recommend(long totalBytes, DownloadKind kind, bool? acceptRanges,
        bool panDirect = false, int cap = 16)
    {
        if (kind is DownloadKind.Magnet or DownloadKind.Torrent)
            return new ConnectionAdvice(4, "BT / 磁力任务由 aria2 的 BT 引擎调度，连接数影响很小。", true);

        var limit = Math.Clamp(cap <= 0 ? 16 : cap, MinConnections, MaxConnections);

        // 服务器明确不分片：多连接只会各自从 0 重下，越开越慢
        if (acceptRanges == false)
            return new ConnectionAdvice(1, "服务器不支持断点续传（没有 Accept-Ranges），只能用单线程。", true);

        var n = TierForSize(totalBytes);
        var reason = totalBytes > 0
            ? $"{Format.Bytes(totalBytes)} 的文件，推荐 {n} 线程"
            : "还不知道文件大小，先用 8 线程（小文件 aria2 会按最小分片自动少开连接）";

        if (acceptRanges is null && n > 8)
        {
            n = 8;
            reason += "；还没探测到服务器是否支持分片，先收敛到 8 线程";
        }
        else if (acceptRanges == true && totalBytes > 0)
        {
            reason += "；服务器支持分片";
        }

        // 网盘直链：单连接常被限速，中等以上文件至少给到 8；但别超过 16 免得被风控
        if (panDirect)
        {
            if (totalBytes >= 16L << 20 && n < 8) n = 8;
            if (n > 16)
            {
                n = 16;
                reason += "；网盘直链上限 16 线程（更多容易被风控）";
            }
        }

        // 每线程至少 1 MB：小文件收敛线程数
        if (totalBytes > 0)
        {
            var bySplit = (int)Math.Max(1, totalBytes / MinBytesPerConnection);
            if (n > bySplit)
            {
                n = Math.Max(1, bySplit);
                reason += $"，线程数收敛到 {n}（每个线程至少 1 MB）";
            }
        }

        if (n > limit)
        {
            n = limit;
            reason += $"，受自动模式上限 {limit} 限制";
        }

        if (n <= 1 && totalBytes > 0)
            reason += "；小文件单线程反而更快（省掉建连开销）";

        return new ConnectionAdvice(Clamp(n), reason + "。", true);
    }

    /// <summary>最终生效值：任务手动指定 &gt; 设置里的固定值 &gt; 自动推荐。</summary>
    public static ConnectionAdvice Resolve(AppSettings settings, DownloadItem item, long totalBytes, bool? acceptRanges)
    {
        if (item.ConnectionsOverride > 0)
        {
            var manual = Clamp(item.ConnectionsOverride);
            return new ConnectionAdvice(manual, $"按你的选择使用 {manual} 线程（手动指定）。", false);
        }

        if (string.Equals(settings.ConnectionMode, "Fixed", StringComparison.OrdinalIgnoreCase))
        {
            var fixedN = Clamp(settings.ConnectionsPerDownload);
            return new ConnectionAdvice(fixedN, $"设置里固定为 {fixedN} 线程。", false);
        }

        return Recommend(totalBytes, item.Kind, acceptRanges, IsPanDirect(item), settings.AutoConnectionCap);
    }

    /// <summary>
    /// HLS / DASH 用的并发分片数（yt-dlp --concurrent-fragments）。
    /// 分片是彼此独立的小请求，并发多一点通常更快，但很长的视频要收敛，免得被限流。
    /// </summary>
    public static ConnectionAdvice ResolveFragments(AppSettings settings, DownloadItem item)
    {
        if (item.ConnectionsOverride > 0)
        {
            var manual = Clamp(item.ConnectionsOverride);
            return new ConnectionAdvice(manual, $"按你的选择并发拉取 {manual} 个分片（手动指定）。", false);
        }

        var baseN = settings.ConcurrentFragments <= 0 ? 16 : Clamp(settings.ConcurrentFragments);
        var d = item.DurationSeconds;

        if (d <= 0)
            return new ConnectionAdvice(Math.Min(baseN, 8), "还不知道视频时长，先并发拉 8 个分片。", true);

        if (d <= 120)
            return new ConnectionAdvice(Math.Min(baseN, 8),
                $"视频只有 {Format.Duration(TimeSpan.FromSeconds(d))}，并发 8 个分片就够。", true);

        if (d > 1800)
            return new ConnectionAdvice(Math.Min(baseN, 12),
                $"视频较长（{Format.Duration(TimeSpan.FromSeconds(d))}），分片并发收敛到 12 以免被服务器限流。", true);

        return new ConnectionAdvice(baseN, $"按设置的 {baseN} 个并发分片拉取。", true);
    }

    /// <summary>是否网盘直链（用于网盘限速补偿）。</summary>
    public static bool IsPanDirect(DownloadItem item)
        => item.Kind == DownloadKind.PanShare
           || item.ProviderName is "Baidu" or "Aliyun" or "Quark" or "Pan123";
}
