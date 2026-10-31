using System.Net.Http;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Services;
namespace YunsuoDownloader.Core.Providers;

/// <summary>解析上下文：Provider 需要的所有外部依赖都从这里拿。</summary>
public sealed class ResolveContext
{
    public required AppSettings Settings { get; init; }
    public required SettingsService SettingsService { get; init; }
    public required HttpClient Http { get; init; }
    public Action<string>? Report { get; init; }

    /// <summary>
    /// 用户为这个分享提供的提取码（访问密码）。
    /// 为空表示还没提供——网盘 Provider 遇到需要密码的分享时应该返回
    /// <see cref="ResolveResult.NeedPassword"/>，让界面去问用户。
    /// </summary>
    public string? SharePassword { get; set; }

    /// <summary>打开后把每一步 HTTP 调用都记进诊断信息（CLI 的 -Verbose 会用）。</summary>
    public bool Verbose { get; init; }

    /// <summary>本次解析的调用记录，最终会放进 <see cref="ResolveResult.Diagnostics"/>。</summary>
    public List<string> Diagnostics { get; } = new();

    public void Log(string message) => Report?.Invoke(message);

    /// <summary>记录一条诊断信息（无论是否 verbose 都会收集关键步骤）。</summary>
    public void Diag(string message)
    {
        Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
        if (Verbose) Report?.Invoke("   " + message);
    }
}

/// <summary>
/// 链接解析器插件接口。
/// 新增一个网盘/站点支持 = 新写一个实现类并注册进 <see cref="LinkResolver"/>，无需改动下载内核。
/// </summary>
public interface ILinkProvider
{
    /// <summary>内部标识，例如 Baidu / Aliyun / Quark / Pan123 / Direct。</summary>
    string Name { get; }

    /// <summary>界面上显示的中文名。</summary>
    string DisplayName { get; }

    /// <summary>优先级，数字越大越先被询问。</summary>
    int Priority { get; }

    /// <summary>是否需要用户先提供 Cookie/登录态。</summary>
    bool RequiresCredential { get; }

    bool CanHandle(string url);

    Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default);
}

/// <summary>Provider 注册中心：按优先级依次询问，谁先认领谁处理。</summary>
public sealed class LinkResolver
{
    private readonly List<ILinkProvider> _providers = new();

    public LinkResolver Register(ILinkProvider provider)
    {
        _providers.Add(provider);
        _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return this;
    }

    public IReadOnlyList<ILinkProvider> Providers => _providers;

    public ILinkProvider? Find(string name) =>
        _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>找出会处理该链接的 Provider（用于界面上提前提示"这条链接走的哪个解析器"）。</summary>
    public ILinkProvider? Match(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        return _providers.FirstOrDefault(p =>
        {
            try { return p.CanHandle(url); } catch { return false; }
        });
    }

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ResolveResult.Fail("LinkResolver", "链接为空");

        url = url.Trim().Trim('"');

        var provider = Match(url);
        if (provider is null)
            return ResolveResult.Fail("LinkResolver", "没有解析器能处理这个链接");

        ctx.Log($"使用解析器：{provider.DisplayName}");
        try
        {
            var result = await provider.ResolveAsync(url, ctx, ct).ConfigureAwait(false);
            result.ProviderName ??= provider.Name;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Utils.Log.Error(ex, $"{provider.Name} 解析失败");
            return ResolveResult.Fail(provider.Name, $"{provider.DisplayName} 解析失败：{ex.Message}");
        }
    }
}
