using System.IO;

namespace YunsuoDownloader.Core.Utils;

/// <summary>
/// 路径合法性检查（专门挡"把链接填进目录框"这种坑）。
///
/// 实测踩过：保存目录被填成了分享链接 <c>https://www.123684.com/s/…</c>，
/// aria2 于是去创建一个叫 "https://…" 的文件夹，报
/// <c>Failed to make the directory …, cause: Invalid argument</c>，
/// 用户只看到一个"无法创建目录"的失败任务，根本猜不到是目录填错了。
/// </summary>
public static class PathGuard
{
    /// <summary>是不是一个可以拿来当保存目录的路径。</summary>
    public static bool IsUsableDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        // 链接 / 磁力 / 迅雷：绝对不可能是目录
        if (dir.Contains("://", StringComparison.Ordinal)) return false;
        if (dir.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return false;
        if (dir.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase)) return false;

        // 必须是绝对路径：相对路径依赖进程工作目录，换个启动方式文件就跑到别处去了
        try { return Path.IsPathFullyQualified(dir); }
        catch { return false; }
    }
}
