using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Pan;

namespace YunsuoDownloader.Core.Providers;

/// <summary>
/// 把网盘 Provider 接到通用解析器链上的适配器。
///
/// 存在的意义：新建任务页、命令行、插件投递都只认 <see cref="ILinkProvider"/>
/// （"给我一个能下的地址"），而网盘需要先列目录再挑文件。
/// 这里做一层桥：**默认取分享里的第一个文件**；
/// 想挑文件（或者进文件夹）的用户走「网盘解析」页面。
/// </summary>
public sealed class PanLinkProvider : ILinkProvider
{
    private readonly IPanProvider _pan;

    public PanLinkProvider(IPanProvider pan) => _pan = pan;

    public string Name => _pan.Id;

    public string DisplayName => _pan.DisplayName;

    public int Priority => 95;

    public bool RequiresCredential => _pan.RequiresLogin;

    public bool CanHandle(string url) => _pan.CanHandle(url);

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var share = _pan.ParseShare(url);
        if (share is null)
            return ResolveResult.Fail(Name, $"{DisplayName} 的分享链接格式认不出来，请检查是否复制完整。");

        // 提取码优先级：用户在界面上填的 > 链接里带的
        var pwd = !string.IsNullOrWhiteSpace(ctx.SharePassword) ? ctx.SharePassword : share.Password;
        share.Password = pwd;

        var service = new PanService(ctx.SettingsService);

        var list = await service.ListAsync(share, _pan.RootId, ctx.Verbose, ct).ConfigureAwait(false);
        ctx.Diagnostics.AddRange(list.Diagnostics);

        if (!list.Ok)
        {
            if (list.NeedPassword)
            {
                var r = list.WrongPassword
                    ? ResolveResult.WrongPwd(Name, share.ShareId, list.Error)
                    : ResolveResult.NeedPwd(Name, share.ShareId, list.Error);
                r.Diagnostics = ctx.Diagnostics;
                return r;
            }
            var fail = ResolveResult.Fail(Name, list.Error ?? "解析失败");
            fail.Diagnostics = ctx.Diagnostics;
            fail.ShareId = share.ShareId;
            return fail;
        }

        var file = list.Entries.FirstOrDefault(e => !e.IsFolder);
        if (file is null)
        {
            var fail = ResolveResult.Fail(Name,
                "这个分享里没有文件（只有文件夹）。\n" +
                "请到「网盘解析」页面进入文件夹挑选具体文件，那里可以直接选多个文件批量下载。");
            fail.Diagnostics = ctx.Diagnostics;
            fail.ShareId = share.ShareId;
            return fail;
        }

        var link = await service.GetDownloadAsync(share, file, ctx.Verbose, ct).ConfigureAwait(false);
        ctx.Diagnostics.AddRange(link.Diagnostics);

        if (!link.Ok)
        {
            var fail = ResolveResult.Fail(Name, link.Error ?? "取直链失败");
            fail.Diagnostics = ctx.Diagnostics;
            fail.ShareId = share.ShareId;
            return fail;
        }

        var result = new ResolveResult
        {
            Success = true,
            Url = link.Url,
            Kind = DownloadKind.File,
            FileName = link.FileName ?? file.Name,
            Title = link.FileName ?? file.Name,
            Referer = link.Headers.GetValueOrDefault("Referer"),
            UserAgent = link.Headers.GetValueOrDefault("User-Agent"),
            Cookies = link.Cookies,
            ProviderName = Name,
            PreferAria2 = true,
            TotalBytes = link.Size > 0 ? link.Size : file.Size,
            Site = _pan.Id,
            Message = link.Message ?? $"{DisplayName} 解析成功：{file.Name}",
            ShareId = share.ShareId,
            Diagnostics = ctx.Diagnostics
        };

        foreach (var (k, v) in link.Headers) result.Headers[k] = v;
        return result;
    }
}
