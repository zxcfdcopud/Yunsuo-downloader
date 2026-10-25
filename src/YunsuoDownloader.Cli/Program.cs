using System.Text;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Cli;

/// <summary>
/// 命令行入口（ysd）。
/// 桌面 App 负责界面，这个 CLI 负责：批量下载、脚本化、以及不依赖外网的端到端自检。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        // 自检使用独立的数据目录，避免污染用户的真实设置
        if (command == "selftest")
        {
            var dir = Path.Combine(Path.GetTempPath(), "ysd-selftest-data-" + DateTime.Now.ToString("HHmmss"));
            Environment.SetEnvironmentVariable("YSD_DATA_DIR", dir);
        }

        try
        {
            return command switch
            {
                "selftest" => await SelfTest.RunAsync(ArgValue(args, "--work")),
                "tools" => await ToolsAsync(),
                "probe" => await ProbeAsync(args),
                "add" => await AddAsync(args),
                "list" => await ListAsync(),
                "update" => await UpdateAsync(),
                "extension" => Extension(args),
                "serve" => await ServeAsync(),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            Log.Error(ex, "CLI 执行失败");
            return 2;
        }
    }

    /// <summary>
    /// 插件目录：查看 / 导出。
    /// 发布后别人的机器上如果没有 extension\ 文件夹，用 `ysd extension --export &lt;目录&gt;` 就能从内置副本里放出来一份。
    /// </summary>
    private static int Extension(string[] args)
    {
        var exportDir = ArgValue(args, "--export") ?? ArgValue(args, "--out");
        try
        {
            if (!string.IsNullOrWhiteSpace(exportDir))
            {
                var count = ExtensionPackager.ExportTo(exportDir);
                if (count == 0 || !File.Exists(Path.Combine(exportDir, "manifest.json")))
                {
                    Console.Error.WriteLine($"导出失败：{exportDir}（写出 {count} 个文件）");
                    return 1;
                }
                Console.WriteLine($"已导出 {count} 个插件文件到：{Path.GetFullPath(exportDir)}");
                Console.WriteLine("在 Chrome 打开 chrome://extensions（Edge 用 edge://extensions）→ 开发者模式 → 加载已解压的扩展程序 → 选这个文件夹。");
                return 0;
            }

            var dir = ExtensionPackager.EnsureAvailable();
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;
            Console.WriteLine($"插件目录：{dir}");
            Console.WriteLine($"目录来源：{ExtensionPackager.DescribeSource(dir)}");
            Console.WriteLine($"文件数量：{files} 个（程序内置 {ExtensionPackager.EmbeddedFileCount} 个，目录丢了会自动释放一份）");
            Console.WriteLine("导出到别处：ysd extension --export <文件夹>");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("插件目录处理失败：" + ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            云梭下载器 命令行工具

            用法：ysd <命令> [参数]

            命令：
              add <url> [url2 ...]   添加下载任务（支持直链/磁力/迅雷/m3u8/网盘分享链接）
                   --dir <路径>       指定保存目录（默认取设置里的默认下载位置）
                   --name <文件名>    指定文件名（仅单任务时有效）
                   --pwd <提取码>     网盘分享的提取码（访问密码）
                   --connections N    本任务用几个线程（不给就按文件大小自动推荐）
              probe <url>            只解析链接，不下载，打印解析结果
                   --pwd <提取码>     网盘分享的提取码
                   --verbose          打印每一步 HTTP 请求与响应（排查网盘接口改版用）
              list                   列出历史任务
              extension              显示浏览器插件目录（找不到会自动释放程序内置的副本）
                   --export <目录>    把内置的插件副本导出到指定目录
              tools                  显示 aria2c / yt-dlp / ffmpeg 的探测结果
              update                 检查更新
              serve                  只启动浏览器插件 WebSocket 服务（不打开界面）
              selftest               端到端自检（不联网，本地起 HTTP 服务验证各引擎）
                   --work <目录>      保留自检工作目录便于排查

            示例：
              ysd add "https://www.bilibili.com/video/BV1xx411c7mD"
              ysd add "magnet:?xt=urn:btih:..." --dir D:\Downloads
              ysd add "https://pan.baidu.com/s/1AbCdEfGh" --pwd abcd
              ysd add "https://example.com/big.iso" --connections 2
              ysd probe "https://www.alipan.com/s/xxxx" --verbose
              ysd selftest
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine("运行 \"ysd help\" 查看可用命令。");
        return 2;
    }

    private static async Task<ServicesBundle> InitAsync()
    {
        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogDirectory);
        var settings = new SettingsService();
        settings.Load();
        var history = new HistoryService(settings.Current.MaxHistoryItems);
        var tools = new ToolsLocator(settings.Current);
        var manager = new DownloadManager(settings, history, tools);
        await manager.InitializeAsync();
        return new ServicesBundle(settings, history, tools, manager);
    }

    private sealed record ServicesBundle(SettingsService Settings, HistoryService History,
        ToolsLocator Tools, DownloadManager Manager);

    // ------------------------------------------------------------------ tools
    private static Task<int> ToolsAsync()
    {
        AppPaths.EnsureCreated();
        var settings = new SettingsService();
        settings.Load();
        var tools = new ToolsLocator(settings.Current);

        Console.WriteLine("外部工具探测结果：");
        foreach (var t in tools.All())
            Console.WriteLine($"  {(t.Found ? "√" : "×")} {t}");

        if (!tools.Aria2.Found || !tools.YtDlp.Found || !tools.Ffmpeg.Found)
        {
            Console.WriteLine();
            Console.WriteLine("有工具缺失，可用下面任一方式补齐：");
            Console.WriteLine("  1) 运行 scripts\\fetch-tools.ps1（自动下载到 tools\\）");
            Console.WriteLine("  2) 在桌面端的「设置 → 外部工具」里手动指定路径");
        }
        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ probe
    private static async Task<int> ProbeAsync(string[] args)
    {
        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("用法：ysd probe <url> [--pwd <提取码>] [--verbose]");
            return 2;
        }

        var pwd = ArgValue(args, "--pwd") ?? ArgValue(args, "--password");
        var verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || a == "-v");

        var bundle = await InitAsync();
        var provider = bundle.Manager.MatchProvider(url);

        Console.WriteLine($"链接        : {url}");
        Console.WriteLine($"命中解析器  : {provider?.DisplayName ?? "(无)"}" +
                          (provider?.RequiresCredential == true ? "  [需要登录 Cookie]" : ""));
        if (!string.IsNullOrEmpty(pwd)) Console.WriteLine($"提取码      : {(verbose ? pwd : "已提供")}");
        Console.WriteLine();

        // verbose 时把每一步 HTTP 调用实时打出来
        var result = await bundle.Manager.ResolveAsync(url, pwd, verbose);

        if (result.Success)
        {
            Console.WriteLine("解析成功：");
            Console.WriteLine($"  类型      : {result.Kind}");
            Console.WriteLine($"  真实地址  : {result.Url}");
            var normalized = UrlNormalizer.Normalize(url);
            if (!string.Equals(normalized, url, StringComparison.Ordinal))
                Console.WriteLine($"  归一化    : {normalized}  （网页里的内嵌播放器地址已还原成内容页，否则 yt-dlp 会报 Unsupported URL）");
            Console.WriteLine($"  文件名    : {result.FileName ?? "(自动)"}");
            Console.WriteLine($"  标题      : {result.Title ?? "-"}");
            Console.WriteLine($"  大小      : {(result.TotalBytes > 0 ? Format.Bytes(result.TotalBytes) : "未知")}");
            Console.WriteLine($"  引擎建议  : {(result.PreferYtDlp ? "yt-dlp" : result.PreferAria2 ? "aria2" : "自动")}");
            if (result.Message is not null) Console.WriteLine($"  说明      : {result.Message}");
        }
        else
        {
            Console.WriteLine("解析失败：" + result.ErrorMessage);
            if (result.NeedPassword)
            {
                Console.WriteLine();
                Console.WriteLine("→ 这个分享需要提取码。重新执行并加上 --pwd <提取码>，例如：");
                Console.WriteLine($"     ysd probe \"{url}\" --pwd abcd --verbose");
            }
        }

        if (result.Diagnostics.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"—— 解析过程诊断（{result.Diagnostics.Count} 步）——");
            foreach (var line in result.Diagnostics) Console.WriteLine("  " + line);
            Console.WriteLine();
            Console.WriteLine("提示：接口返回里出现 \"not exist\"/404 通常意味着网盘改了接口路径，");
            Console.WriteLine("      请参考 docs/PAN_PROVIDERS.md 更新对应 Provider。");
        }

        await bundle.Manager.DisposeAsync();
        return result.Success ? 0 : 1;
    }

    // ------------------------------------------------------------------ add
    private static async Task<int> AddAsync(string[] args)
    {
        var urls = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        if (urls.Length == 0) { Console.Error.WriteLine("用法：ysd add <url> [--dir <路径>] [--name <文件名>] [--pwd <提取码>] [--connections N]"); return 2; }

        var bundle = await InitAsync();
        var dir = ArgValue(args, "--dir") ?? bundle.Settings.Current.DownloadDirectory;
        var name = ArgValue(args, "--name");
        var pwd = ArgValue(args, "--pwd");
        var conns = int.TryParse(ArgValue(args, "--connections") ?? ArgValue(args, "-j"), out var cn) ? cn : 0;

        var completed = new List<DownloadItem>();
        var failed = new List<DownloadItem>();
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int pending = urls.Length;

        bundle.Manager.ItemCompleted += (_, item) =>
        {
            completed.Add(item);
            if (Interlocked.Decrement(ref pending) <= 0) allDone.TrySetResult(true);
        };
        bundle.Manager.ItemFailed += (_, item) =>
        {
            failed.Add(item);
            if (Interlocked.Decrement(ref pending) <= 0) allDone.TrySetResult(true);
        };

        foreach (var url in urls)
        {
            var item = await bundle.Manager.AddAsync(new AddDownloadRequest
            {
                Url = url,
                Directory = dir,
                FileName = urls.Length == 1 ? name : null,
                Password = pwd,
                Connections = conns
            });
            Console.WriteLine($"[{item.StatusText}] {item.DisplayName}");
            if (!string.IsNullOrEmpty(item.ErrorMessage)) Console.WriteLine("    " + item.ErrorMessage);
            if (item.Status == DownloadStatus.Failed)
            {
                if (Interlocked.Decrement(ref pending) <= 0) allDone.TrySetResult(true);
            }
        }

        Console.WriteLine();
        Console.WriteLine("等待下载完成（Ctrl+C 可中断，任务会保留在历史里）…");
        using var progressCts = new CancellationTokenSource();
        var progressTask = Task.Run(async () =>
        {
            while (!progressCts.IsCancellationRequested)
            {
                await Task.Delay(1000, progressCts.Token).ConfigureAwait(false);
                var active = bundle.Manager.ActiveItems.ToArray();
                if (active.Length == 0) continue;
                Console.Write($"\r总速度 {Format.Speed(bundle.Manager.TotalSpeed)}  ");
                foreach (var a in active.Take(3))
                    Console.Write($"| {Format.Truncate(a.DisplayName, 24)} {a.PercentText} {a.SpeedText} ");
                Console.Write("      ");
            }
        }, progressCts.Token);

        await Task.WhenAny(allDone.Task, Task.Delay(TimeSpan.FromHours(6)));
        progressCts.Cancel();
        try { await progressTask; } catch { }
        Console.WriteLine();

        Console.WriteLine($"完成 {completed.Count} 个，失败 {failed.Count} 个。");
        foreach (var f in failed) Console.WriteLine($"  失败：{f.DisplayName} — {f.ErrorMessage}");

        await bundle.Manager.DisposeAsync();
        return failed.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ list
    private static async Task<int> ListAsync()
    {
        var bundle = await InitAsync();
        var items = bundle.History.Items;
        if (items.Count == 0) Console.WriteLine("(没有历史记录)");

        Console.WriteLine($"{"状态",-8} {"进度",8} {"大小",-20} {"下载时间",-20} 名称");
        foreach (var i in items.Take(50))
            Console.WriteLine($"{i.StatusText,-8} {i.PercentText,8} {i.SizeText,-20} {i.CreatedAtText,-20} {Format.Truncate(i.DisplayName, 50)}");

        await bundle.Manager.DisposeAsync();
        return 0;
    }

    // ------------------------------------------------------------------ update
    private static async Task<int> UpdateAsync()
    {
        AppPaths.EnsureCreated();
        var settings = new SettingsService();
        settings.Load();
        var updates = new UpdateService(settings);

        Console.WriteLine($"当前版本：{UpdateService.CurrentVersionText}");
        Console.WriteLine("尝试的更新源：");
        foreach (var u in updates.CandidateFeedUrls()) Console.WriteLine("  - " + u);
        Console.WriteLine();

        var result = await updates.CheckAsync();
        Console.WriteLine(result.Message);
        if (result.Info is { } info)
        {
            Console.WriteLine($"  最新版本：{info.VersionText}");
            if (info.DownloadUrl is not null) Console.WriteLine($"  下载地址：{info.DownloadUrl}");
            if (info.Notes is not null) Console.WriteLine($"  更新说明：{Format.Truncate(info.Notes, 400)}");
        }
        return result.Status == UpdateStatus.Failed ? 1 : 0;
    }

    // ------------------------------------------------------------------ serve
    private static async Task<int> ServeAsync()
    {
        var bundle = await InitAsync();
        var bridge = new ExtensionBridge(bundle.Manager, bundle.Settings);
        bridge.StatusMessage += (_, m) => Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m);
        bridge.StateChanged += (_, _) =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 插件连接数：{bridge.ClientCount}；最近：{bridge.LastClientInfo ?? "-"}");

        var ok = await bridge.StartAsync();
        if (!ok)
        {
            Console.Error.WriteLine("启动失败：" + (bridge.LastError ?? "未知"));
            return 1;
        }

        Console.WriteLine($"插件服务已启动：ws://{bundle.Settings.Current.MediaServerHost}:{bundle.Settings.Current.MediaServerPort}/media");
        Console.WriteLine("令牌：" + bundle.Settings.Current.MediaServerToken);
        Console.WriteLine("按 Ctrl+C 退出。");

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
        await quit.Task;

        await bridge.DisposeAsync();
        await bundle.Manager.DisposeAsync();
        return 0;
    }

    // ------------------------------------------------------------------ 参数解析
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
