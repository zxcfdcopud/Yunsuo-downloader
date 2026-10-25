using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Core.Services;

/// <summary>
/// 浏览器插件的「可分发化」支持：内置副本（编进 YunsuoDownloader.Core.dll）+ 路径自动探测 + 一键导出。
///
/// 这样即使把 exe 单独拷到别的机器，插件也不会丢：定位不到磁盘上的 extension\ 时，
/// 本类会把内置副本释放到数据目录（%AppData%\YunsuoDownloader\extension）。
///
/// 来源判定顺序（<see cref="Locate"/> / <see cref="EnsureAvailable"/> / <see cref="DescribeSource"/> 一致）：
///   1) exe 同目录\extension      —— 安装版 / 便携发布版（随程序分发）
///   2) 工程根目录\extension      —— 开发态（从 bin\Debug\... 往上找到 .sln）
///   3) 数据目录\extension        —— 之前由本类释放出来的副本（%AppData%）
///   4) 都没有                    —— Locate 返回 null；EnsureAvailable 释放内置副本到数据目录
/// </summary>
public static class ExtensionPackager
{
    /// <summary>嵌入资源名前缀。csproj 里用 LogicalName="ext/%(RecursiveDir)%(Filename)%(Extension)" 生成。</summary>
    private const string ResourcePrefix = "ext/";

    /// <summary>插件目录的判定标志文件：没有它就不算一个可加载的插件目录。</summary>
    private const string ManifestName = "manifest.json";

    /// <summary>EnsureAvailable / ExportTo 的并发保护（只影响本进程内的释放动作）。</summary>
    private static readonly object Gate = new();

    /// <summary>内置资源名列表，只枚举一次。</summary>
    private static readonly Lazy<string[]> ResourceNames = new(FindEmbeddedNames, LazyThreadSafetyMode.ExecutionAndPublication);

    // ------------------------------------------------------------------ 契约成员

    /// <summary>只查找不释放：exe 同目录\extension → 工程根目录\extension → 数据目录\extension；都没有返回 null。</summary>
    public static string? Locate()
    {
        foreach (var candidate in CandidateDirectories())
        {
            try
            {
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, ManifestName)))
                    return Normalize(candidate);
            }
            catch (Exception ex)
            {
                Log.Warn($"探测插件目录失败（{candidate}）：{ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// 返回可用的插件目录：<see cref="Locate"/> 命中就直接返回；
    /// 否则把内置副本释放到 %AppData%\YunsuoDownloader\extension 后返回（失败时抛异常）。
    ///
    /// 命中「数据目录里由本类释放的那一份」时会顺手按内置副本刷新一次：App 升级后插件文件会变，
    /// 而老用户机器上那份可能还是旧版。内容相同的文件不会重复写盘，刷新失败只记日志、仍返回该目录。
    /// 「exe 同目录」「工程根目录」这两支只返回、不改写（安装包升级会替换，开发态是源）。
    /// 幂等 + 线程安全。
    /// </summary>
    public static string EnsureAvailable()
    {
        lock (Gate)
        {
            var located = Locate();
            if (located is not null)
            {
                RefreshReleasedCopyIfNeeded(located);
                return located;
            }

            string fallback;
            try
            {
                fallback = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "extension"));
            }
            catch (Exception ex)
            {
                Log.Warn("计算插件释放目录失败：" + ex.Message);
                throw;
            }

            var exported = ExportToCore(fallback);
            if (exported == 0 || !File.Exists(Path.Combine(fallback, ManifestName)))
            {
                Log.Warn($"内置插件副本释放失败（写出 {exported} 个文件）：{fallback}");
                throw new IOException($"无法释放内置的浏览器插件副本：{fallback}（请检查目录权限，或改用完整的安装包重装）");
            }

            Log.Info($"内置插件副本已就绪：{fallback}（{exported} 个文件）");
            return fallback;
        }
    }

    /// <summary>
    /// 命中数据目录里那份释放副本时，按内置副本补写一次（内容相同不写盘，所以仍然幂等）。
    /// 只多读一遍插件文件做内容比对；任何失败都只记日志，绝不影响调用方拿到可用目录。
    /// </summary>
    private static void RefreshReleasedCopyIfNeeded(string located)
    {
        string released;
        try
        {
            released = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "extension"));
        }
        catch (Exception ex)
        {
            Log.Warn("计算数据目录插件路径失败，跳过刷新：" + ex.Message);
            return;
        }

        // exe 同目录 / 工程根目录：只返回，不在这里改写
        if (!IsSame(located, released)) return;

        try
        {
            var refreshed = ExportToCore(released);
            if (refreshed == 0 || !File.Exists(Path.Combine(released, ManifestName)))
                Log.Warn($"刷新内置插件副本失败（写出 {refreshed} 个文件）：{released}");
            else
                Log.Info($"内置插件副本已刷新：{released}（{refreshed} 个文件）");
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新内置插件副本异常（{released}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 把内置插件释放到指定目录（覆盖同名文件），返回写出的文件数。
    /// 返回值 = 导出的插件文件总数（内容已一致、未重复写盘的文件也计入）。
    /// </summary>
    public static int ExportTo(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("导出目录不能为空。", nameof(directory));

        string target;
        try
        {
            target = Path.GetFullPath(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"导出插件失败，目录不合法（{directory}）：{ex.Message}");
            throw new ArgumentException("导出目录不合法：" + directory, nameof(directory), ex);
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                // 目录都建不出来（权限/只读盘），记录后返回 0，由调用方提示用户
                Log.Warn($"创建插件导出目录失败（{target}）：{ex.Message}");
                return 0;
            }
            return ExportToCore(target);
        }
    }

    /// <summary>内置插件文件数。</summary>
    public static int EmbeddedFileCount => ResourceNames.Value.Length;

    /// <summary>目录来源的中文说明，如 "安装目录（随程序分发）" / "开发工程目录" / "程序释放的副本（%AppData%）"。</summary>
    public static string DescribeSource(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return "未知来源";
            var full = Normalize(directory);

            // 1) exe 同目录（安装版 / 便携发布版）
            if (IsSame(full, Path.Combine(AppPaths.BaseDirectory, "extension")))
                return "安装目录（随程序分发）";

            // 2) 工程根目录（开发态）
            var root = AppPaths.FindProjectRoot();
            if (!string.IsNullOrEmpty(root) && IsSame(full, Path.Combine(root, "extension")))
                return "开发工程目录";

            // 3) 数据目录里由本类释放的副本
            if (IsSame(full, Path.Combine(AppPaths.DataDirectory, "extension")))
                return "程序释放的副本（%AppData%）";

            // 4) 用户自己导出到别处的副本
            if (File.Exists(Path.Combine(full, ManifestName)))
                return "手动导出的插件副本（内置）";

            return "指定的插件目录";
        }
        catch (Exception ex)
        {
            Log.Warn($"判断插件目录来源失败（{directory}）：{ex.Message}");
            return "未知来源";
        }
    }

    // ------------------------------------------------------------------ 内部实现

    /// <summary>四处候选目录，顺序即优先级。</summary>
    private static List<string> CandidateDirectories()
    {
        var list = new List<string>();
        try { list.Add(Path.Combine(AppPaths.BaseDirectory, "extension")); }
        catch (Exception ex) { Log.Warn("计算 exe 同目录插件路径失败：" + ex.Message); }

        try
        {
            if (AppPaths.FindProjectRoot() is { Length: > 0 } root)
                list.Add(Path.Combine(root, "extension"));
        }
        catch (Exception ex) { Log.Warn("计算工程插件路径失败：" + ex.Message); }

        try { list.Add(Path.Combine(AppPaths.DataDirectory, "extension")); }
        catch (Exception ex) { Log.Warn("计算数据目录插件路径失败：" + ex.Message); }

        return list;
    }

    /// <summary>把内置副本写到 <paramref name="targetFullPath"/>；单个文件失败只记日志，返回成功导出的文件数。</summary>
    private static int ExportToCore(string targetFullPath)
    {
        var assembly = typeof(ExtensionPackager).Assembly;
        var names = ResourceNames.Value;
        if (names.Length == 0)
        {
            Log.Warn("程序集里没有找到内置插件资源（ext/...）——csproj 的 EmbeddedResource 是否漏了？");
            return 0;
        }

        var exported = 0;
        foreach (var name in names)
        {
            var relative = RelativePath(name);
            if (relative is null)
            {
                Log.Warn("跳过无法识别的内置插件资源：" + name);
                continue;
            }

            var destination = Path.Combine(targetFullPath, relative);
            try
            {
                var folder = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    Log.Warn("读取内置插件资源失败：" + name);
                    continue;
                }

                byte[] content;
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    content = buffer.ToArray();
                }

                // 只在文件不存在或内容不同时写盘，避免每次启动都刷一遍磁盘
                if (NeedsWrite(destination, content))
                {
                    // 逐字节写出：源文件本身就是 UTF-8 无 BOM 的 JS/HTML/CSS（以及 PNG），不额外加 BOM
                    File.WriteAllBytes(destination, content);
                    Log.Info($"释放插件文件：{destination}");
                }

                exported++;
            }
            catch (Exception ex)
            {
                // 单个文件失败不影响其它文件；由调用方根据 manifest.json 是否存在判断整体成败
                Log.Warn($"释放插件文件失败（{destination}）：{ex.Message}");
            }
        }

        return exported;
    }

    /// <summary>枚举本程序集里以 ext/ 开头（兼容反斜杠）的资源名。</summary>
    private static string[] FindEmbeddedNames()
    {
        try
        {
            return typeof(ExtensionPackager).Assembly
                .GetManifestResourceNames()
                .Where(static n => n.Replace('\\', '/').StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn("枚举内置插件资源失败：" + ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>资源名 → 相对路径（保留子目录，拒绝目录穿越）。</summary>
    private static string? RelativePath(string resourceName)
    {
        var normalized = resourceName.Replace('\\', '/');
        if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return null;

        var relative = normalized.Substring(ResourcePrefix.Length).Trim('/');
        if (relative.Length == 0) return null;

        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(static p => p is "." or "..")) return null;

        return Path.Combine(parts);
    }

    /// <summary>文件不存在或内容不同才需要写。</summary>
    private static bool NeedsWrite(string path, byte[] content)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var existing = File.ReadAllBytes(path);
            return existing.Length != content.Length || !existing.SequenceEqual(content);
        }
        catch (Exception ex)
        {
            Log.Warn($"比较插件文件失败（{path}）：{ex.Message}");
            return true;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsSame(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
