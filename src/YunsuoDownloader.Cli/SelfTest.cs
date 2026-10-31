using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using YunsuoDownloader.Core.Engine;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Providers.Builtin;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.Cli;

/// <summary>
/// 端到端自检：完全不依赖外网，本地起 HTTP 服务器验证
///   1) 自研 HTTP 引擎的多连接分片下载 + 校验和
///   2) aria2 引擎的多连接分片下载 + 校验和
///   3) 断点续传（暂停后继续，最终文件哈希正确）
///   4) HLS/m3u8 通过 yt-dlp + ffmpeg 下载（真造一段带分片的视频）
///   5) 链接解析器（直链/磁力/迅雷/网盘/视频站）
///   6) 浏览器插件 WebSocket 协议（握手 → hello → ping → add → add-ack）
/// </summary>
public static class SelfTest
{
    private static readonly List<(string Name, bool Pass, string Detail)> Results = new();

    public static async Task<int> RunAsync(string? keepDir)
    {
        Console.WriteLine("================ 云梭下载器 自检 ================");
        Console.WriteLine();

        var work = keepDir ?? Path.Combine(Path.GetTempPath(), "ysd-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(work);
        var downloadDir = Path.Combine(work, "downloads");
        Directory.CreateDirectory(downloadDir);
        Console.WriteLine($"工作目录：{work}");
        Console.WriteLine($"数据目录：{AppPaths.DataDirectory}");
        Console.WriteLine();

        // ---- 准备本地测试资源
        var sourceFile = Path.Combine(work, "source-big.bin");
        var sourceHash = CreateRandomFile(sourceFile, 24 * 1024 * 1024);
        Console.WriteLine($"已生成测试文件：source-big.bin（24 MB）SHA256={sourceHash[..16]}…");

        await using var server = new TestHttpServer(work);
        Console.WriteLine($"本地测试服务器：{server.RootUrl}");
        Console.WriteLine();

        // ---- 初始化设置与工具探测
        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogDirectory);
        var settings = new SettingsService();
        settings.Load();
        settings.Update(s =>
        {
            s.DownloadDirectory = downloadDir;
            // 固定 8 线程：让既有用例（HTTP 引擎必须出现多连接）保持确定性
            s.ConnectionMode = "Fixed";
            s.ConnectionsPerDownload = 8;
            s.MinSplitSizeKb = 1024;
            s.MaxConcurrentDownloads = 3;
            s.ConcurrentFragments = 8;
        });
        var tools = new ToolsLocator(settings.Current);
        Console.WriteLine(tools.Summary());
        Console.WriteLine();
        Check("工具探测：aria2c 存在", tools.Aria2.Found, tools.Aria2.Version ?? "未找到");
        Check("工具探测：yt-dlp 存在", tools.YtDlp.Found, tools.YtDlp.Version ?? "未找到");
        Check("工具探测：ffmpeg 存在", tools.Ffmpeg.Found, tools.Ffmpeg.Version ?? "未找到");

        // ------------------------------------------------------------------ 1) 自研 HTTP 引擎
        await TestHttpEngineAsync(settings.Current, tools, server, downloadDir, sourceHash);

        // ------------------------------------------------------------------ 1.5) 线程数顾问
        await TestConnectionAdvisorAsync(settings, tools, server, downloadDir);

        // ------------------------------------------------------------------ 2) 续传
        await TestResumeAsync(settings.Current, tools, server, downloadDir, sourceHash);

        // ------------------------------------------------------------------ 3) aria2 引擎
        await TestAria2Async(settings.Current, tools, server, downloadDir, sourceHash);

        // ------------------------------------------------------------------ 4) 链接解析器
        TestResolvers(settings, server);

        // ------------------------------------------------------------------ 4.5) 移除任务（界面垃圾桶按钮背后的逻辑）
        await TestRemoveTaskAsync(settings, server, downloadDir);

        // ------------------------------------------------------------------ 5) HLS
        await TestHlsAsync(settings.Current, tools, server, work, downloadDir);

        // ------------------------------------------------------------------ 6) 插件协议
        await TestExtensionProtocolAsync(settings, downloadDir, server, sourceHash);

        // ------------------------------------------------------------------ 汇总
        Console.WriteLine();
        Console.WriteLine("================ 自检结果 ================");
        foreach (var (name, pass, detail) in Results)
        {
            var mark = pass ? "通过" : "失败";
            Console.WriteLine($"[{mark}] {name}{(string.IsNullOrWhiteSpace(detail) ? "" : "  —  " + detail)}");
        }
        var failed = Results.Count(r => !r.Pass);
        Console.WriteLine();
        Console.WriteLine($"合计 {Results.Count} 项，失败 {failed} 项。");
        if (keepDir is null)
        {
            try { Directory.Delete(work, true); } catch { Console.WriteLine($"（工作目录未能删除：{work}）"); }
        }
        else Console.WriteLine($"保留工作目录：{work}");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool pass, string detail = "")
    {
        Results.Add((name, pass, detail));
        var mark = pass ? "通过" : "失败";
        Console.WriteLine($"  [{mark}] {name}{(string.IsNullOrWhiteSpace(detail) ? "" : "  —  " + detail)}");
    }

    private static string CreateRandomFile(string path, int size)
    {
        var buffer = new byte[1024 * 1024];
        using (var fs = File.Create(path))
        {
            var remaining = size;
            while (remaining > 0)
            {
                Random.Shared.NextBytes(buffer);
                int n = Math.Min(buffer.Length, remaining);
                fs.Write(buffer, 0, n);
                remaining -= n;
            }
        }
        return Sha256(path);
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>引擎可能还持有文件句柄（尤其是 aria2 刚写完的那一刻），重试几次再算哈希。</summary>
    private static async Task<string> Sha256WithRetryAsync(string path, int attempts = 15)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(path)) return Sha256(path);
            }
            catch (IOException) { /* 被占用，稍后重试 */ }
            await Task.Delay(300);
        }
        return string.Empty;
    }

    // ==================================================================== 1) HTTP 引擎
    private static async Task TestHttpEngineAsync(AppSettings settings, ToolsLocator tools,
        TestHttpServer server, string downloadDir, string expectedHash)
    {
        Console.WriteLine("---- 1) 自研 HTTP 多连接引擎 ----");
        var engine = new HttpDownloadEngine(tools, settings);
        var item = new DownloadItem
        {
            Url = server.RootUrl + "source-big.bin",
            FileName = "http-engine.bin",
            Directory = downloadDir,
            Kind = DownloadKind.File,
            Engine = EngineKind.Http
        };

        int maxConnections = 0;
        long maxReported = 0;
        server.ThrottleBytesPerSecond = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => { item.FullPath = e.FullPath; done.TrySetResult(true); };
        engine.Failed += (_, e) => done.TrySetResult(false);
        engine.Progress += (_, e) =>
        {
            if (e.Connections > maxConnections) maxConnections = e.Connections;
            if (e.DownloadedBytes > maxReported) maxReported = e.DownloadedBytes;
        };

        var rangeBefore = server.RangeRequestCount;
        var sw = Stopwatch.StartNew();
        await engine.EnqueueAsync(item);
        var ok = await WaitAsync(done.Task, TimeSpan.FromMinutes(3));
        sw.Stop();
        var rangeUsed = server.RangeRequestCount - rangeBefore;

        var target = Path.Combine(downloadDir, "http-engine.bin");
        var exists = File.Exists(target);
        var hash = exists ? Sha256(target) : "";
        Check("HTTP 引擎下载完成", ok, $"{sw.ElapsedMilliseconds} ms，{Utils(FileSize(target))}");
        Check("HTTP 引擎文件哈希正确", hash == expectedHash, hash.Length >= 16 ? hash[..16] + "…" : hash);
        Check("HTTP 引擎使用了多连接分片", maxConnections >= 2, $"上报并发分片数 {maxConnections}");
        Check("HTTP 引擎发出多个 Range 请求（证据）", rangeUsed >= 3,
            $"探测+分片共 {rangeUsed} 个 Range 请求，服务器累计 {server.RangeRequestCount} 个");
        Check("HTTP 引擎清理了状态文件", !File.Exists(target + ".fdlstate"));
        await engine.StopAsync();
        Console.WriteLine();
    }

    // ==================================================================== 1.5) 线程数顾问
    private static async Task TestConnectionAdvisorAsync(SettingsService settingsService, ToolsLocator tools,
        TestHttpServer server, string downloadDir)
    {
        Console.WriteLine("---- 1.5) 线程数顾问（按文件自动推荐 + 手动指定）----");
        var fixedSettings = settingsService.Current;

        Check("线程推荐：大小未知 -> 8 线程（保守值）",
            ConnectionAdvisor.Recommend(0, DownloadKind.File, null).Connections == 8,
            ConnectionAdvisor.Recommend(0, DownloadKind.File, null).Reason);
        Check("线程推荐：800 KB 小文件 -> 单线程",
            ConnectionAdvisor.Recommend(800 * 1024, DownloadKind.File, true).Connections == 1,
            ConnectionAdvisor.Recommend(800 * 1024, DownloadKind.File, true).Reason);
        Check("线程推荐：24 MB -> 4 线程",
            ConnectionAdvisor.Recommend(24L * 1024 * 1024, DownloadKind.File, true).Connections == 4);
        Check("线程推荐：1 GB -> 12 线程",
            ConnectionAdvisor.Recommend(1L << 30, DownloadKind.File, true).Connections == 12);
        Check("线程推荐：4 GB -> 16 线程",
            ConnectionAdvisor.Recommend(4L << 30, DownloadKind.File, true).Connections == 16);
        Check("线程推荐：服务器不分片 -> 单线程",
            ConnectionAdvisor.Recommend(4L << 30, DownloadKind.File, false).Connections == 1);
        Check("线程推荐：分片能力未知 -> 收敛到 8 线程",
            ConnectionAdvisor.Recommend(4L << 30, DownloadKind.File, null).Connections == 8);
        Check("线程推荐：BT / 磁力不看分片",
            ConnectionAdvisor.Recommend(4L << 30, DownloadKind.Magnet, true).Connections == 4);

        var auto = fixedSettings.Clone();
        auto.ConnectionMode = "Auto";
        auto.AutoConnectionCap = 2;
        Check("线程推荐：受“自动模式上限”约束",
            ConnectionAdvisor.Recommend(4L << 30, DownloadKind.File, true, false, auto.AutoConnectionCap).Connections == 2);

        var probeItem = new DownloadItem { Kind = DownloadKind.File, ConnectionsOverride = 3 };
        Check("线程取值：任务手动指定优先",
            ConnectionAdvisor.Resolve(auto, probeItem, 4L << 30, true).Connections == 3);
        probeItem.ConnectionsOverride = 0;
        Check("线程取值：自动模式按文件推荐",
            ConnectionAdvisor.Resolve(auto, probeItem, 24L * 1024 * 1024, true).Connections == 2);
        Check("线程取值：固定模式用设置里的值",
            ConnectionAdvisor.Resolve(fixedSettings, probeItem, 24L * 1024 * 1024, true).Connections == 8);

        // 真跑一遍：自动模式下 24 MB 文件应该只开 4 个连接
        auto.AutoConnectionCap = 16;
        var engine = new HttpDownloadEngine(tools, auto);
        server.ThrottleBytesPerSecond = 0;

        var autoItem = new DownloadItem
        {
            Url = server.RootUrl + "source-big.bin",
            FileName = "auto-conns.bin",
            Directory = downloadDir,
            Kind = DownloadKind.File
        };
        var autoDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => { autoItem.FullPath = e.FullPath; autoDone.TrySetResult(true); };
        engine.Failed += (_, e) => autoDone.TrySetResult(false);
        await engine.EnqueueAsync(autoItem);
        var autoOk = await WaitAsync(autoDone.Task, TimeSpan.FromMinutes(3));
        Check("自动模式：24 MB 文件下载完成",
            autoOk && File.Exists(Path.Combine(downloadDir, "auto-conns.bin")));
        Check("自动模式：实际开了 4 个线程", autoItem.Connections == 4, $"实际 {autoItem.Connections} 个");
        Check("自动模式：给了“为什么用这个线程数”的理由",
            !string.IsNullOrWhiteSpace(autoItem.ConnectionNote), autoItem.ConnectionNote ?? "（空）");

        var manualItem = new DownloadItem
        {
            Url = server.RootUrl + "source-big.bin",
            FileName = "manual-conns.bin",
            Directory = downloadDir,
            Kind = DownloadKind.File,
            ConnectionsOverride = 2
        };
        var manualDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => manualDone.TrySetResult(true);
        engine.Failed += (_, e) => manualDone.TrySetResult(false);
        var rangeBefore = server.RangeRequestCount;
        await engine.EnqueueAsync(manualItem);
        var manualOk = await WaitAsync(manualDone.Task, TimeSpan.FromMinutes(3));
        Check("手动指定：2 线程任务下载完成",
            manualOk && File.Exists(Path.Combine(downloadDir, "manual-conns.bin")));
        Check("手动指定：实际只开 2 个线程", manualItem.Connections == 2, $"实际 {manualItem.Connections} 个");
        Check("手动指定：2 线程确实少发了 Range 请求",
            server.RangeRequestCount - rangeBefore <= 4,
            $"2 线程下载 24 MB 用了 {server.RangeRequestCount - rangeBefore} 个 Range 请求");
        await engine.StopAsync();
        Console.WriteLine();
    }

    // ==================================================================== 2) 断点续传
    private static async Task TestResumeAsync(AppSettings settings, ToolsLocator tools,
        TestHttpServer server, string downloadDir, string expectedHash)
    {
        Console.WriteLine("---- 2) 断点续传 ----");
        var engine = new HttpDownloadEngine(tools, settings);
        var target = Path.Combine(downloadDir, "resume-test.bin");
        if (File.Exists(target)) File.Delete(target);

        var item = new DownloadItem
        {
            Url = server.RootUrl + "source-big.bin",
            FileName = "resume-test.bin",
            Directory = downloadDir,
            Kind = DownloadKind.File,
            Engine = EngineKind.Http
        };

        var completed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e.FullPath);
        engine.Failed += (_, e) => completed.TrySetResult(null);

        long maxDownloaded = 0;
        engine.Progress += (_, e) => { if (e.DownloadedBytes > maxDownloaded) maxDownloaded = e.DownloadedBytes; };

        // 服务端限速，制造一个可观测的"下载到一半"窗口
        server.ThrottleBytesPerSecond = 4 * 1024 * 1024;

        await engine.EnqueueAsync(item);

        // 等下载到 1MB 以上再暂停
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && maxDownloaded < 1024 * 1024)
            await Task.Delay(50);

        var partial = maxDownloaded;
        await engine.PauseAsync(item);
        await Task.Delay(600);
        var pausedSize = new FileInfo(target).Exists ? new FileInfo(target).Length : 0;
        var stateKept = File.Exists(item.StatePath);

        // 取消限速后继续
        server.ThrottleBytesPerSecond = 0;
        var rangeBeforeResume = server.RangeRequestCount;
        await engine.EnqueueAsync(item);
        var okPath = await WaitAsync(completed.Task, TimeSpan.FromMinutes(3));
        var hash = File.Exists(target) ? Sha256(target) : "";

        Check("暂停时已获得部分数据", partial > 0, $"{Utils(partial)}");
        Check("暂停后保留了续传状态文件", stateKept || pausedSize > 0,
            stateKept ? "已保留 .fdlstate" : $"文件残留 {Utils(pausedSize)}");
        Check("续传后下载完成", okPath is not null, okPath ?? "失败");
        Check("续传后文件哈希正确", hash == expectedHash, hash.Length >= 16 ? hash[..16] + "…" : hash);
        Check("续传时再次发起 Range 请求", server.RangeRequestCount > rangeBeforeResume,
            $"续传阶段 {server.RangeRequestCount - rangeBeforeResume} 个请求");
        await engine.StopAsync();
        Console.WriteLine();
    }

    // ==================================================================== 3) aria2
    private static async Task TestAria2Async(AppSettings settings, ToolsLocator tools,
        TestHttpServer server, string downloadDir, string expectedHash)
    {
        Console.WriteLine("---- 3) aria2 引擎 ----");
        if (!tools.Aria2.Found)
        {
            Check("aria2 引擎下载完成", false, "未找到 aria2c.exe，跳过");
            Console.WriteLine();
            return;
        }

        var engine = new Aria2Engine(tools, settings);
        if (!await engine.StartAsync())
        {
            Check("aria2 引擎启动", false, "RPC 未就绪");
            Console.WriteLine();
            return;
        }
        Check("aria2 引擎启动", true, $"RPC 端口 {engine.Port}");

        var item = new DownloadItem
        {
            Url = server.RootUrl + "source-big.bin",
            FileName = "aria2-engine.bin",
            Directory = downloadDir,
            Kind = DownloadKind.File,
            Engine = EngineKind.Aria2,
            UserAgent = Aria2Engine.DefaultUserAgent
        };

        var completed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e.FullPath);
        engine.Failed += (_, e) => completed.TrySetResult(null);

        var sw = Stopwatch.StartNew();
        await engine.EnqueueAsync(item);
        var path = await WaitAsync(completed.Task, TimeSpan.FromMinutes(3));
        sw.Stop();

        var target = Path.Combine(downloadDir, "aria2-engine.bin");
        var hash = File.Exists(target) ? Sha256(target) : "";
        Check("aria2 下载完成", path is not null && File.Exists(target), $"{sw.ElapsedMilliseconds} ms，{Utils(FileSize(target))}");
        Check("aria2 文件哈希正确", hash == expectedHash, hash.Length >= 16 ? hash[..16] + "…" : hash);
        await engine.StopAsync();
        Console.WriteLine();
    }

    // ==================================================================== 4) 解析器
    private static void TestResolvers(SettingsService settings, TestHttpServer server)
    {
        Console.WriteLine("---- 4) 链接解析器 ----");
        var manager = new DownloadManager(settings, new HistoryService(50), new ToolsLocator(settings.Current));

        Check("直链 -> Direct", manager.MatchProvider(server.RootUrl + "a.mp4")?.Name == "Direct");
        Check("m3u8 识别为 HLS", manager.MatchProvider("https://cdn.example.com/a/index.m3u8")?.Name == "Direct");
        Check("磁力 -> Magnet", manager.MatchProvider("magnet:?xt=urn:btih:0123456789abcdef&dn=test")?.Name == "Magnet");
        var thunderUrl = "thunder://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("AAhttp://example.com/a.mp4ZZ"));
        Check("迅雷 -> Thunder", manager.MatchProvider(thunderUrl)?.Name == "Thunder");
        Check("阿里云盘 -> Aliyun", manager.MatchProvider("https://www.alipan.com/s/abcdefg123")?.Name == "Aliyun");
        Check("夸克网盘 -> Quark", manager.MatchProvider("https://pan.quark.cn/s/abcdef123456")?.Name == "Quark");
        Check("百度网盘 -> Baidu", manager.MatchProvider("https://pan.baidu.com/s/1AbCdEfGh")?.Name == "Baidu");
        Check("123 网盘 -> Pan123", manager.MatchProvider("https://www.123pan.com/s/abcd-efgh")?.Name == "Pan123");
        Check("B站 -> VideoSite", manager.MatchProvider("https://www.bilibili.com/video/BV1xx411c7mD")?.Name == "VideoSite");
        Check("YouTube -> VideoSite", manager.MatchProvider("https://www.youtube.com/watch?v=dQw4w9WgXcQ")?.Name == "VideoSite");

        // ---- 链接归一化（浏览器插件送来的常常是网页里的内嵌播放器地址，yt-dlp 不认）
        Check("归一化：B站 player.html?bvid= -> 内容页",
            UrlNormalizer.Normalize("https://player.bilibili.com/player.html?bvid=BV1fsJ6tEUJ")
            == "https://www.bilibili.com/video/BV1fsJ6tEUJ",
            UrlNormalizer.Normalize("https://player.bilibili.com/player.html?bvid=BV1fsJ6tEUJ"));
        Check("归一化：B站 player.html 保留分 P",
            UrlNormalizer.Normalize("https://player.bilibili.com/player.html?bvid=BV1fsJ6tEUJ&page=3")
            == "https://www.bilibili.com/video/BV1fsJ6tEUJ?p=3",
            UrlNormalizer.Normalize("https://player.bilibili.com/player.html?bvid=BV1fsJ6tEUJ&page=3"));
        Check("归一化：B站 player.html?aid= -> av 内容页",
            UrlNormalizer.Normalize("https://player.bilibili.com/player.html?aid=12345&cid=999")
            == "https://www.bilibili.com/video/av12345");
        Check("归一化：m.bilibili.com -> www + 去掉跟踪参数",
            UrlNormalizer.Normalize("https://m.bilibili.com/video/BV1fsJ6tEUJ?spm_id_from=333.999")
            == "https://www.bilibili.com/video/BV1fsJ6tEUJ");
        Check("归一化：内容页保留 p、丢掉 vd_source",
            UrlNormalizer.Normalize("https://www.bilibili.com/video/BV1fsJ6tEUJ?p=2&vd_source=abc")
            == "https://www.bilibili.com/video/BV1fsJ6tEUJ?p=2");
        Check("归一化：番剧页不动（交给 yt-dlp）",
            UrlNormalizer.Normalize("https://www.bilibili.com/bangumi/play/ep123456")
            == "https://www.bilibili.com/bangumi/play/ep123456");
        Check("归一化：YouTube /embed/ -> watch",
            UrlNormalizer.Normalize("https://www.youtube.com/embed/dQw4w9WgXcQ")
            == "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Check("归一化：youtu.be 短链 -> watch",
            UrlNormalizer.Normalize("https://youtu.be/dQw4w9WgXcQ")
            == "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Check("归一化：普通直链原样返回（绝不乱改）",
            UrlNormalizer.Normalize("https://example.com/a.zip") == "https://example.com/a.zip");
        Check("归一化：非法输入不崩",
            UrlNormalizer.Normalize("not a url at all") == "not a url at all");
        Check("归一化：null/空串安全",
            UrlNormalizer.Normalize(null) == string.Empty && UrlNormalizer.Normalize("  ") == "  ");

        // ---- 抖音：视频 ID 在 query 的 modal_id 里，yt-dlp 只认 /video/<id> ----
        Check("归一化：抖音 jingxuan?modal_id= -> /video/<id>",
            UrlNormalizer.Normalize("https://www.douyin.com/jingxuan?modal_id=7680497115004505396")
            == "https://www.douyin.com/video/7680497115004505396",
            UrlNormalizer.Normalize("https://www.douyin.com/jingxuan?modal_id=7680497115004505396"));
        Check("归一化：抖音 user?modal_id= -> /video/<id>",
            UrlNormalizer.Normalize("https://www.douyin.com/user/MS4wLjABAAAA?modal_id=7680497115004505396")
            == "https://www.douyin.com/video/7680497115004505396");
        Check("归一化：抖音 /video/<id> 去掉跟踪参数",
            UrlNormalizer.Normalize("https://www.douyin.com/video/7680497115004505396?previous_page=app_code_link")
            == "https://www.douyin.com/video/7680497115004505396");
        Check("归一化：iesdouyin 分享页 -> 抖音内容页",
            UrlNormalizer.Normalize("https://www.iesdouyin.com/share/video/7680497115004505396/")
            == "https://www.douyin.com/video/7680497115004505396");

        // ---- Cookie 缓存：按域名匹配（含子域回退）----
        var cookieDomains = CookieJar.CandidateDomains("https://pan.baidu.com/s/1AbCdEfGh").ToList();
        Check("Cookie 域名：pan.baidu.com 会回退匹配 baidu.com",
            cookieDomains.Contains("pan.baidu.com") && cookieDomains.Contains("baidu.com"),
            string.Join(" / ", cookieDomains));
        Check("Cookie 域名：v.douyin.com 会回退匹配 douyin.com",
            CookieJar.CandidateDomains("https://v.douyin.com/abc/").Contains("douyin.com"));
        Check("Cookie 域名：非法 URL 不崩",
            CookieJar.CandidateDomains("not a url").Count() == 0);

        // ---- HEVC 判定（抖音大量 hvc1/hev1）----
        var hevcItem = new DownloadItem { Codec = "hvc1.1.6.L93.B0" };
        var avcItem = new DownloadItem { Codec = "avc1.640028" };
        var hev1Item = new DownloadItem { Codec = "hev1.2.4.L120.B0" };
        Check("HEVC：hvc1 判为 HEVC", hevcItem.IsHevc);
        Check("HEVC：hev1 判为 HEVC", hev1Item.IsHevc);
        Check("HEVC：avc1 不判为 HEVC", !avcItem.IsHevc);
        Check("HEVC：没有 codec 不判为 HEVC", !new DownloadItem().IsHevc);

        // ---- 网盘解析：链接识别 / 提取码（纯离线，不碰网络）----
        var panCases = new (string Url, string Provider, string ShareId, string? Pwd)[]
        {
            ("https://pan.baidu.com/s/1AbCdEfGh", "Baidu", "1AbCdEfGh", null),
            ("https://pan.baidu.com/s/1AbCdEfGh?pwd=abcd", "Baidu", "1AbCdEfGh", "abcd"),
            ("https://www.alipan.com/s/abcdefg123", "Aliyun", "abcdefg123", null),
            ("https://www.aliyundrive.com/s/xyz789?pwd=1234", "Aliyun", "xyz789", "1234"),
            ("https://pan.quark.cn/s/abcdef123456", "Quark", "abcdef123456", null),
            ("https://pan.quark.cn/s/abcdef123456?pwd=8888", "Quark", "abcdef123456", "8888"),
            ("https://www.123pan.com/s/abcd-efgh", "Pan123", "abcd-efgh", null),
            ("https://www.123865.com/s/abcd-efgh?pwd=9999", "Pan123", "abcd-efgh", "9999")
        };

        foreach (var c in panCases)
        {
            var provider = Core.Pan.PanCatalog.Match(c.Url);
            Check($"网盘识别：{c.Url}", provider?.Id == c.Provider,
                $"解析器={provider?.Id ?? "无"}");

            var share = provider?.ParseShare(c.Url);
            Check($"网盘分享 ID：{c.Provider}", share?.ShareId == c.ShareId,
                $"shareId={share?.ShareId ?? "无"}");
            Check($"网盘提取码：{c.Provider}", share?.Password == c.Pwd,
                $"pwd={share?.Password ?? "无"}");
        }

        Check("网盘：四家都注册了", Core.Pan.PanCatalog.Providers.Count == 4,
            string.Join("/", Core.Pan.PanCatalog.Providers.Select(p => p.Id)));
        Check("网盘：普通直链不误判为网盘", !Core.Pan.PanCatalog.IsPanUrl("https://example.com/a.zip"));
        Check("网盘：网盘链接会走 PanLinkProvider",
            manager.MatchProvider("https://pan.quark.cn/s/abcdef123456")?.GetType().Name == "PanLinkProvider",
            manager.MatchProvider("https://pan.quark.cn/s/abcdef123456")?.Name ?? "无");

        // 123 的直链是「跳转页 ?params=<base64url>」——这里验解码逻辑（真实场景无法离线复现）
        var inner = "https://guest-share-free-download-cdn.123295.com/123-1/abc/file.zip?t=1&s=2";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(inner))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var decoded = Core.Pan.PanUrl.TryDecodeRedirectParams(
            $"https://web-pro2.cjjd18.com/download-v2/?params={b64}&is_s3=0");
        Check("123 直链：跳转页 params 能解回真实地址", decoded == inner, Truncate(decoded, 60));
        Check("123 直链：不是跳转页时返回 null",
            Core.Pan.PanUrl.TryDecodeRedirectParams("https://example.com/a.zip") is null);
        Check("123 直链：params 是乱码时不崩",
            Core.Pan.PanUrl.TryDecodeRedirectParams("https://x/download-v2/?params=%%%") is null);

        // ---- 网盘登录态（插件自动采集通道）----
        Check("Cookie 域名匹配：子域名算同一站",
            Core.Services.SettingsService.MatchesDomain("sub.pan.quark.cn", "pan.quark.cn"));
        Check("Cookie 域名匹配：相似域名不算",
            !Core.Services.SettingsService.MatchesDomain("evilpan.quark.cn", "pan.quark.cn"));
        Check("网盘域名表：123 的镜像域名都列了",
            Core.Services.SettingsService.ProviderDomains("Pan123").Contains("www.123684.com"));

        settings.Update(s => s.BrowserCookies.Add(new Core.Models.BrowserCookie
        {
            Domain = "123684.com",
            Cookie = "__pus=FROM_EXTENSION"
        }));
        var viaExtension = settings.GetCookie("Pan123");
        Check("登录态：插件采集的 Cookie 能被网盘解析认出来（镜像域名）",
            viaExtension == "__pus=FROM_EXTENSION", viaExtension ?? "无");
        Check("登录态：不会串到别家网盘",
            string.IsNullOrEmpty(settings.GetCookie("Quark")), settings.GetCookie("Quark") ?? "空");

        // 非网盘链接不受影响（保证没误伤）
        var biliResolve = manager.ResolveAsync("https://www.bilibili.com/video/BV1xx411c7mD")
            .GetAwaiter().GetResult();
        Check("网盘改动没影响 B 站等普通链接", biliResolve.Success, Truncate(biliResolve.ErrorMessage, 60));


        // 迅雷链接解码
        var thunderDecoded = new ThunderProvider().ResolveAsync(thunderUrl,
            new Core.Providers.ResolveContext
            {
                Settings = settings.Current,
                SettingsService = settings,
                Http = new HttpClient()
            }).GetAwaiter().GetResult();
        Check("迅雷链接解码正确", thunderDecoded.Success && thunderDecoded.Url == "http://example.com/a.mp4",
            thunderDecoded.Url ?? thunderDecoded.ErrorMessage ?? "");

        // 自定义规则
        var rule = new RuleBasedProvider();
        rule.Load(new[]
        {
            new RuleBasedProvider.Rule
            {
                Name = "unit-test",
                Pattern = @"^https://demo\.local/v/(\d+)$",
                DirectUrl = "https://cdn.local/$1.mp4",
                Kind = "video"
            }
        });
        var ruleResult = rule.ResolveAsync("https://demo.local/v/42", new Core.Providers.ResolveContext
        {
            Settings = settings.Current,
            SettingsService = settings,
            Http = new HttpClient()
        }).GetAwaiter().GetResult();
        Check("自定义规则重写链接", ruleResult.Success && ruleResult.Url == "https://cdn.local/42.mp4", ruleResult.Url ?? "");

        // 更新源解析
        var info = UpdateService.Parse("{\"version\":\"1.4.2\",\"url\":\"https://x/y.exe\",\"notes\":\"n\",\"sha256\":\"abc\"}");
        Check("更新源 JSON 解析", info is { VersionText: "1.4.2" } && info.DownloadUrl == "https://x/y.exe");

        var gh = UpdateService.Parse("{\"tag_name\":\"v2.0.1\",\"html_url\":\"https://gh/rel\",\"assets\":[{\"name\":\"Setup.exe\",\"browser_download_url\":\"https://gh/a.exe\",\"size\":123}]}");
        Check("GitHub Releases 解析", gh is { VersionText: "2.0.1" } && gh.DownloadUrl == "https://gh/a.exe");
        Console.WriteLine();
    }

    // ==================================================================== 4.5) 移除任务
    /// <summary>
    /// 覆盖界面「从列表移除」按钮背后的两条路径：
    ///   ① 已完成的任务 —— 移除记录；deleteFile=true 时连文件一起删
    ///   ② 正在下载的任务 —— 必须真的把引擎停掉，否则列表清了后台还在写文件
    /// </summary>
    private static async Task TestRemoveTaskAsync(SettingsService settings, TestHttpServer server, string downloadDir)
    {
        Console.WriteLine("---- 4.5) 移除任务（对应列表里的垃圾桶按钮） ----");
        var history = new HistoryService(50);
        var manager = new DownloadManager(settings, history, new ToolsLocator(settings.Current));
        await manager.InitializeAsync();

        try
        {
            // ① 已完成的任务：移除 + 删文件
            var done = await manager.AddAsync(new AddDownloadRequest
            {
                Url = server.RootUrl + "source-big.bin",
                FileName = "remove-done.bin",
                Directory = downloadDir,
                Kind = DownloadKind.File
            });

            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (done.Status != DownloadStatus.Completed && DateTime.UtcNow < deadline)
                await Task.Delay(150);

            var path = Path.Combine(downloadDir, "remove-done.bin");
            Check("移除测试：任务先下载完成", done.Status == DownloadStatus.Completed && File.Exists(path),
                done.StatusText);

            await manager.RemoveAsync(done.Id, deleteFile: true);
            Check("移除测试：记录已从历史移除", manager.Find(done.Id) is null,
                $"历史条数 {history.Items.Count}");
            Check("移除测试：deleteFile=true 时文件被删除", !File.Exists(path),
                File.Exists(path) ? "文件仍在" : "文件已删除");

            // ② 正在下载的任务：移除时必须停引擎
            server.ThrottleBytesPerSecond = 2 * 1024 * 1024;   // 限速，制造"还在下载"的窗口
            var running = await manager.AddAsync(new AddDownloadRequest
            {
                Url = server.RootUrl + "source-big.bin",
                FileName = "remove-running.bin",
                Directory = downloadDir,
                Kind = DownloadKind.File
            });

            var waitStart = DateTime.UtcNow;
            while (running.Status != DownloadStatus.Downloading && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(30))
                await Task.Delay(100);

            var wasDownloading = running.IsActive;
            await manager.RemoveAsync(running.Id, deleteFile: true);
            await Task.Delay(1200);   // 给引擎一点时间真正停下

            var stillDownloading = manager.ActiveItems.Any(i => i.Id == running.Id);
            var runningPath = Path.Combine(downloadDir, "remove-running.bin");
            var sizeAfter = File.Exists(runningPath) ? new FileInfo(runningPath).Length : 0;
            await Task.Delay(800);
            var sizeLater = File.Exists(runningPath) ? new FileInfo(runningPath).Length : 0;

            Check("移除测试：正在下载的任务被真正停止",
                wasDownloading && !stillDownloading && sizeAfter == sizeLater,
                $"移除前在下载={wasDownloading}，之后仍活跃={stillDownloading}，文件大小 {Utils(sizeAfter)} → {Utils(sizeLater)}");
            server.ThrottleBytesPerSecond = 0;
        }
        finally
        {
            server.ThrottleBytesPerSecond = 0;
            await manager.DisposeAsync();
        }
        Console.WriteLine();
    }

    // ==================================================================== 5) HLS
    private static async Task TestHlsAsync(AppSettings settings, ToolsLocator tools,
        TestHttpServer server, string work, string downloadDir)
    {
        Console.WriteLine("---- 5) HLS / m3u8 ----");
        if (!tools.Ffmpeg.Found || !tools.YtDlp.Found)
        {
            Check("HLS 下载（需要 ffmpeg + yt-dlp）", false, "缺少工具，跳过");
            Console.WriteLine();
            return;
        }

        var hlsDir = Path.Combine(work, "hls-src");
        Directory.CreateDirectory(hlsDir);
        var playlist = Path.Combine(hlsDir, "index.m3u8");

        // 用 ffmpeg 造一段 3 秒、切成 3 个分片的 HLS
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
            "-t", "3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-g", "15", "-keyint_min", "15", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "64k",
            "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
            "-hls_segment_filename", Path.Combine(hlsDir, "seg%d.ts"),
            playlist
        };

        var genOk = await RunProcessAsync(tools.Ffmpeg.Path!, args, TimeSpan.FromMinutes(2));
        var segments = Directory.Exists(hlsDir) ? Directory.GetFiles(hlsDir, "*.ts").Length : 0;
        Check("生成测试用 HLS 流", genOk && File.Exists(playlist) && segments >= 2,
            $"分片数 {segments}{(genOk ? "" : "，ffmpeg 失败")}");
        if (!genOk) { Console.WriteLine(); return; }

        var engine = new YtDlpEngine(tools, settings);
        var item = new DownloadItem
        {
            Url = server.RootUrl + "hls-src/index.m3u8",
            FileName = "hls-test.mp4",
            Directory = downloadDir,
            Kind = DownloadKind.Hls,
            IsHls = true,
            Engine = EngineKind.YtDlp
        };

        var completed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e.FullPath);
        engine.Failed += (_, e) => completed.TrySetResult(null);

        await engine.EnqueueAsync(item);
        var path = await WaitAsync(completed.Task, TimeSpan.FromMinutes(3));

        var output = path ?? Directory.GetFiles(downloadDir, "hls-test*").FirstOrDefault();
        var size = output is null ? 0 : FileSize(output);
        Check("HLS(m3u8) 下载完成", output is not null && size > 1000,
            output is null ? "未生成输出文件" : $"{Path.GetFileName(output)}，{Utils(size)}");

        if (output is not null && tools.Ffprobe.Found)
        {
            var probe = await RunProcessCaptureAsync(tools.Ffprobe.Path!,
                new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", output },
                TimeSpan.FromSeconds(60));
            var durationOk = double.TryParse(probe.Trim(), out var d) && d > 1.5;
            Check("HLS 输出可被 ffprobe 识别（时长 ≈3s）", durationOk, probe.Trim());
        }
        Console.WriteLine();
    }

    // ==================================================================== 6) 插件协议
    private static async Task TestExtensionProtocolAsync(SettingsService settings, string downloadDir,
        TestHttpServer server, string expectedHash)
    {
        Console.WriteLine("---- 6) 浏览器插件 WebSocket 协议 ----");
        var history = new HistoryService(50);
        var manager = new DownloadManager(settings, history, new ToolsLocator(settings.Current));
        await manager.InitializeAsync();

        settings.Update(s => { s.MediaServerPort = 17999; s.MediaServerEnabled = true; });
        await using var bridge = new ExtensionBridge(manager, settings);
        var started = await bridge.StartAsync();
        Check("插件 WebSocket 服务启动", started, bridge.LastError ?? $"端口 {settings.Current.MediaServerPort}");
        if (!started) { Console.WriteLine(); return; }

        // 用完成事件而不是轮询文件存在，避免"文件刚建好还没写完"的竞态
        var delivered = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ItemCompleted += (_, item) => delivered.TrySetResult(item.FullPath);
        manager.ItemFailed += (_, item) =>
        {
            if (item.Url.Contains("source-big.bin", StringComparison.OrdinalIgnoreCase))
                delivered.TrySetResult(null);
        };

        try
        {
            using var client = new MiniWebSocketClient();
            var connected = await client.ConnectAsync("127.0.0.1", settings.Current.MediaServerPort, "/media", TimeSpan.FromSeconds(8));
            Check("WebSocket 握手（101 Switching Protocols）", connected);

            // hello（带正确令牌）
            var ackTask = client.WaitForMessageAsync("hello-ack", TimeSpan.FromSeconds(6));
            await client.SendAsync($"{{\"type\":\"hello\",\"client\":\"browser-extension\",\"version\":\"1.0.0\",\"browser\":\"selftest\",\"token\":\"{settings.Current.MediaServerToken}\"}}");
            var ack = await ackTask;
            Check("hello 握手返回 hello-ack", ack is not null && ack.Contains("downloadDir"), Truncate(ack, 120));

            // ping
            var pongTask = client.WaitForMessageAsync("pong", TimeSpan.FromSeconds(6));
            await client.SendAsync("{\"type\":\"ping\"}");
            var pong = await pongTask;
            Check("ping 返回 pong", pong is not null, Truncate(pong, 80));

            // add（本地大文件，实际会下载，借此验证"插件 -> App -> 下载"全链路）
            var addAckTask = client.WaitForMessageAsync("add-ack", TimeSpan.FromSeconds(15));
            var payload = "{\"type\":\"add\",\"tasks\":[{" +
                          $"\"url\":\"{server.RootUrl}source-big.bin\"," +
                          "\"kind\":\"file\",\"title\":\"来自插件的文件\",\"filename\":\"from-extension.bin\"," +
                          "\"referer\":\"https://example.com/watch\",\"userAgent\":\"Mozilla/5.0 selftest\",\"cookies\":\"a=b\"}]}";
            await client.SendAsync(payload);
            var addAck = await addAckTask;
            Check("add 返回 add-ack 且 accepted=1", addAck is not null && addAck.Contains("\"accepted\":1"), Truncate(addAck, 140));

            // 等待真正的下载完成（可能被 aria2 短暂占用文件，所以带重试地算哈希）
            var completedPath = await WaitAsync(delivered.Task, TimeSpan.FromMinutes(3));
            var target = Path.Combine(downloadDir, "from-extension.bin");
            var hash = await Sha256WithRetryAsync(target);
            Check("插件投递的任务真的下载完成", completedPath is not null && hash == expectedHash,
                hash == expectedHash
                    ? $"{Utils(FileSize(target))}，哈希一致"
                    : (completedPath is null ? "任务失败/超时" : "文件哈希不一致"));

            // 令牌错误必须被拒绝（关闭码 4001）
            using var badClient = new MiniWebSocketClient();
            var badConnected = await badClient.ConnectAsync("127.0.0.1", settings.Current.MediaServerPort, "/media", TimeSpan.FromSeconds(8));
            if (badConnected)
            {
                await badClient.SendAsync("{\"type\":\"hello\",\"client\":\"browser-extension\",\"version\":\"1.0.0\",\"token\":\"wrong-token\"}");
                var closeCode = await badClient.WaitForCloseAsync(TimeSpan.FromSeconds(6));
                Check("令牌不匹配时用 4001 关闭连接", closeCode == 4001, $"关闭码 {closeCode}");
            }
            else Check("令牌不匹配时用 4001 关闭连接", false, "第二个连接未建立");
        }
        catch (Exception ex)
        {
            Check("插件协议测试", false, ex.Message);
        }

        await manager.DisposeAsync();
        Console.WriteLine();
    }

    // ==================================================================== 工具方法
    private static async Task<T?> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? await task : default;
    }

    private static long FileSize(string? path)
    {
        try { return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static string Utils(long bytes) => Format.Bytes(bytes);

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static async Task<bool> RunProcessAsync(string exe, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) Console.WriteLine("      ffmpeg 输出：" + Truncate(err, 400));
        return p.ExitCode == 0;
    }

    private static async Task<string> RunProcessCaptureAsync(string exe, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return "";
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Trim();
    }
}
