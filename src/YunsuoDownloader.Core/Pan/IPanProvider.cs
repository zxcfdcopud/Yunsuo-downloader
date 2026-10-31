namespace YunsuoDownloader.Core.Pan;

/// <summary>
/// 网盘解析插件接口：一家网盘一个实现。
///
/// 和 <see cref="Providers.ILinkProvider"/> 的分工：
///   * <see cref="ILinkProvider"/> 是"这个链接该交给谁下载"的粗粒度路由，只关心一个文件；
///   * 本接口是网盘专用，要支持**列目录 + 选文件**，所以拆成 List / GetDownload 两步。
/// 两者通过 <see cref="PanLinkProvider"/> 这个适配器接起来，
/// 这样新建任务页/命令行照旧粘网盘链接就能下（自动取第一个文件）。
/// </summary>
public interface IPanProvider
{
    PanKind Kind { get; }

    /// <summary>Provider 标识，和设置里存凭据用的键一致（Aliyun / Quark / Baidu / Pan123）。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>网盘网页版登录地址（内嵌浏览器登录用）。</summary>
    string LoginUrl { get; }

    /// <summary>登录后从哪个域名采集 Cookie。</summary>
    string CookieDomain { get; }

    /// <summary>是否必须有登录态才能拿到直链。</summary>
    bool RequiresLogin { get; }

    /// <summary>根目录 ID（各家不同：阿里是 root，夸克是 0，百度是 /）。</summary>
    string RootId { get; }

    /// <summary>界面上给用户的一句提示（能匿名解析的、必须登录的，说法不一样）。</summary>
    string Hint { get; }

    bool CanHandle(string url);

    /// <summary>从链接里抽出分享 ID 和提取码；认不出来返回 null。</summary>
    PanShare? ParseShare(string url);

    /// <summary>列出目录内容。dirId 传 <see cref="RootId"/> 表示根目录。</summary>
    Task<PanListResult> ListAsync(PanShare share, string dirId, PanBrowser browser, CancellationToken ct);

    /// <summary>把某个文件换成直链。</summary>
    Task<PanDownloadLink> GetDownloadAsync(PanShare share, PanEntry entry, PanBrowser browser,
        CancellationToken ct);
}

/// <summary>
/// 网盘注册表。
///
/// 阿里云盘暂时不在列表里：它的匿名直链接口已被官方关闭（实测 410 Gone），
/// 剩下唯一的路是"登录 → 转存到自己网盘 → 用 access token 取直链"，
/// 而 access token 不在 Cookie 里，当前这套 登录态（Cookie）方案拿不到。
/// 与其放一个"能看不能下"的入口，不如先摘掉，等做完 token 方案再加回来。
/// </summary>
public static class PanCatalog
{
    private static readonly IPanProvider[] All =
    {
        new Providers.QuarkPanProvider(),
        new Providers.Pan123Provider(),
        new Providers.BaiduPanProvider()
    };

    public static IReadOnlyList<IPanProvider> Providers => All;

    public static IPanProvider? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>按链接找 Provider（认不出来返回 null）。</summary>
    public static IPanProvider? Match(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        foreach (var p in All)
        {
            try { if (p.CanHandle(url)) return p; }
            catch { /* 单个 Provider 的正则出问题不影响其它家 */ }
        }
        return null;
    }

    public static bool IsPanUrl(string? url) => !string.IsNullOrWhiteSpace(url) && Match(url) is not null;
}

/// <summary>
/// 网盘解析服务：界面和命令行都通过它调用，负责把 Cookie、诊断、异常收口。
/// </summary>
public sealed class PanService
{
    private readonly Services.SettingsService _settings;

    public PanService(Services.SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>某家网盘当前是否已有登录态（用户手动填过，或插件自动采集到）。</summary>
    public bool HasLogin(string providerId) => !string.IsNullOrWhiteSpace(_settings.GetCookie(providerId));

    /// <summary>按链接解析出分享对象；不是网盘链接返回 null。</summary>
    public PanShare? Parse(string url) => PanCatalog.Match(url)?.ParseShare(url);

    /// <summary>列根目录。</summary>
    public Task<PanListResult> ListAsync(string url, string? password, bool verbose = false,
        CancellationToken ct = default)
    {
        var provider = PanCatalog.Match(url);
        if (provider is null)
            return Task.FromResult(PanListResult.Fail("这不是支持的网盘分享链接。"));

        var share = provider.ParseShare(url);
        if (share is null)
            return Task.FromResult(PanListResult.Fail($"{provider.DisplayName} 的分享链接格式认不出来，请检查是否复制完整。"));

        if (!string.IsNullOrEmpty(password)) share.Password = password;
        return ListAsync(share, provider.RootId, verbose, ct);
    }

    /// <summary>列某个目录（翻文件夹时用）。</summary>
    public async Task<PanListResult> ListAsync(PanShare share, string dirId, bool verbose = false,
        CancellationToken ct = default)
    {
        var provider = PanCatalog.Find(share.ProviderId);
        if (provider is null) return PanListResult.Fail("没有对应的网盘解析器。");

        using var browser = new PanBrowser(null, _settings.GetCookie(provider.Id), null, verbose);
        // 百度的 BDCLND 是"验证提取码"发下来的，跨调用要带着，否则每次都要重新验
        if (share.Get("BDCLND") is { Length: > 0 } bdclnd) browser.SetCookie("BDCLND", bdclnd);

        try
        {
            var result = await provider.ListAsync(share, dirId, browser, ct).ConfigureAwait(false);
            result.Diagnostics.AddRange(browser.Diagnostics);
            // 百度的 BDCLND 是"验证提取码成功"时下发的，记住它，后面取直链/翻目录时不用重新验
            if (browser.GetCookie("BDCLND") is { Length: > 0 } value) share.Set("BDCLND", value);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Utils.Log.Error(ex, $"{provider.Id} 列目录失败");
            return new PanListResult
            {
                Ok = false,
                Error = $"{provider.DisplayName} 解析出错：{ex.Message}",
                Diagnostics = browser.Diagnostics.ToList()
            };
        }
    }

    /// <summary>取单个文件的直链。</summary>
    public async Task<PanDownloadLink> GetDownloadAsync(PanShare share, PanEntry entry, bool verbose = false,
        CancellationToken ct = default)
    {
        var provider = PanCatalog.Find(share.ProviderId);
        if (provider is null) return PanDownloadLink.Fail("没有对应的网盘解析器。");

        using var browser = new PanBrowser(null, _settings.GetCookie(provider.Id), null, verbose);
        if (share.Get("BDCLND") is { Length: > 0 } bdclnd) browser.SetCookie("BDCLND", bdclnd);

        try
        {
            var link = await provider.GetDownloadAsync(share, entry, browser, ct).ConfigureAwait(false);
            link.Diagnostics.AddRange(browser.Diagnostics);
            return link;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Utils.Log.Error(ex, $"{provider.Id} 取直链失败");
            return new PanDownloadLink
            {
                Ok = false,
                Error = $"{provider.DisplayName} 取直链出错：{ex.Message}",
                Diagnostics = browser.Diagnostics.ToList()
            };
        }
    }
}
