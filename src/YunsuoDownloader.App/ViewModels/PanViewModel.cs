using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YunsuoDownloader.App.Services;
using YunsuoDownloader.App.Views.Dialogs;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Pan;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.ViewModels;

/// <summary>网盘列表里的一行（包一层是为了能勾选，而 <see cref="PanEntry"/> 是解析结果、不该带 UI 状态）。</summary>
public sealed partial class PanFileVm : ObservableObject
{
    public PanEntry Entry { get; }

    [ObservableProperty] private bool _isSelected;

    public PanFileVm(PanEntry entry, bool selected = false)
    {
        Entry = entry;
        _isSelected = selected && !entry.IsFolder;
    }

    public string Name => Entry.Name;

    public string SizeText => Entry.SizeText;

    public string ModifiedText => Entry.ModifiedText;

    public bool IsFolder => Entry.IsFolder;

    public string Glyph => Entry.Glyph;
}

/// <summary>「网盘解析」页里每家网盘的登录状态行。</summary>
public sealed partial class PanLoginRow : ObservableObject
{
    public string ProviderId { get; }

    public string DisplayName { get; }

    public string Hint { get; }

    public bool RequiresLogin { get; }

    [ObservableProperty] private bool _isLoggedIn;

    [ObservableProperty] private string _stateText = "未登录";

    public PanLoginRow(IPanProvider provider, bool loggedIn)
    {
        ProviderId = provider.Id;
        DisplayName = provider.DisplayName;
        Hint = provider.Hint;
        RequiresLogin = provider.RequiresLogin;
        _isLoggedIn = loggedIn;
        _stateText = loggedIn ? "已登录（Cookie 有效期内）" : (provider.RequiresLogin ? "未登录 — 需要登录才能下载" : "未登录（公开分享也能解析）");
    }

    public string ActionText => IsLoggedIn ? "退出" : "登录";
}

/// <summary>
/// 「网盘解析」页的 ViewModel：粘贴分享链接 → 列目录（可进文件夹）→ 勾选文件 → 批量下载。
/// </summary>
public sealed partial class PanViewModel : ObservableObject
{
    private PanService? _service;
    private PanShare? _share;
    private readonly List<(string Id, string Name)> _crumbs = new();

    [ObservableProperty] private string _linkText = string.Empty;
    [ObservableProperty] private string _passwordText = string.Empty;
    [ObservableProperty] private string _statusMessage = "把网盘分享链接粘到上面，点「解析分享」。";
    [ObservableProperty] private string _shareTitle = string.Empty;
    [ObservableProperty] private string _dirPathText = "根目录";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _hasDiagnostics;
    [ObservableProperty] private string _diagnosticsText = string.Empty;
    [ObservableProperty] private string _selectedCountText = "未选择文件";
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _hasEmpty = true;
    [ObservableProperty] private bool _canOpenShare;

    public ObservableCollection<PanLoginRow> Logins { get; } = new();

    public ObservableCollection<PanFileVm> Files { get; } = new();

    private PanService Service => _service ??= new PanService(AppServices.Settings);

    public PanViewModel()
    {
        RefreshLogins();
    }

    // ------------------------------------------------------------------ 登录状态
    private void RefreshLogins()
    {
        if (!AppServices.Initialized) return;

        Logins.Clear();
        foreach (var provider in PanCatalog.Providers)
            Logins.Add(new PanLoginRow(provider, Service.HasLogin(provider.Id)));
    }

    /// <summary>一行的「登录 / 退出」按钮：已登录就退出，没登录就弹内嵌浏览器登录。</summary>
    [RelayCommand]
    private void ToggleLogin(PanLoginRow? row)
    {
        if (row is null) return;

        if (row.IsLoggedIn)
        {
            AppServices.Settings.RemoveCredential(row.ProviderId);
            RefreshLogins();
            StatusMessage = $"已清除{row.DisplayName}的登录态。";
            return;
        }

        var provider = PanCatalog.Find(row.ProviderId);
        if (provider is null) return;

        var dialog = new PanLoginWindow(provider.DisplayName, provider.LoginUrl, provider.CookieDomain)
        {
            Owner = Application.Current?.MainWindow
        };

        _ = dialog.ShowDialog();

        // 判断成功只看对话框自己记的 Succeeded，不看 ShowDialog() 的返回值
        // （关窗方式不同会得到 true / false / null，靠它判断会漏掉成功的情况）。
        if (dialog.Succeeded && !string.IsNullOrWhiteSpace(dialog.CookieHeader))
        {
            AppServices.Settings.SetCredential(provider.Id, provider.DisplayName, dialog.CookieHeader);
            RefreshLogins();
            StatusMessage = $"已保存{provider.DisplayName}的登录态（DPAPI 加密，只存在本机）。";

            // 已经解析过分享的话，登录完立刻重新拉一次目录 ——
            // 用户登录的目的通常就是"继续刚才那个分享"，不该让他再点一次解析。
            if (_share is not null)
            {
                var dirId = _crumbs.Count == 0
                    ? PanCatalog.Find(_share.ProviderId)?.RootId ?? _share.ProviderId
                    : _crumbs[^1].Id;
                _ = LoadDirAsync(dirId, resetCrumbs: false);
            }
        }
        else
        {
            StatusMessage = $"{provider.DisplayName} 没有完成登录（已取消，或没取到 Cookie）。";
        }
    }

    // ------------------------------------------------------------------ 解析
    [RelayCommand]
    private async Task Parse()
    {
        var url = (LinkText ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            StatusMessage = "先把网盘分享链接粘进来。";
            return;
        }

        var provider = PanCatalog.Match(url);
        if (provider is null)
        {
            StatusMessage = "这不是支持的网盘链接（百度 / 夸克 / 123 云盘）。";
            return;
        }

        var share = provider.ParseShare(url);
        if (share is null)
        {
            StatusMessage = $"{provider.DisplayName} 的链接格式认不出来，请确认复制完整。";
            return;
        }

        var typed = (PasswordText ?? string.Empty).Trim();
        if (typed.Length > 0) share.Password = typed;

        _share = share;
        CanOpenShare = true;
        await LoadDirAsync(provider.RootId, resetCrumbs: true);
    }

    /// <summary>
    /// 在默认浏览器里打开这条分享。
    ///
    /// 有些网盘（含已被官方关掉匿名直链的）只有在网页里配合浏览器插件才能拿到地址，
    /// 这个按钮就是把用户送过去：在浏览器里打开分享 → 插件嗅探。
    /// </summary>
    [RelayCommand]
    private void OpenShare()
    {
        if (_share is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_share.OriginalUrl) { UseShellExecute = true });
            StatusMessage = "已在浏览器中打开分享。可以在这时用浏览器插件嗅探下载地址。";
        }
        catch (Exception ex)
        {
            StatusMessage = "打开浏览器失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task EnterFolder(PanFileVm? row)
    {
        if (row is null || !row.IsFolder || _share is null) return;
        var provider = PanCatalog.Find(_share.ProviderId);
        if (provider is null) return;

        _crumbs.Add((row.Entry.Id, row.Entry.Name));
        await LoadDirAsync(row.Entry.Id, resetCrumbs: false);
    }

    [RelayCommand]
    private async Task Up()
    {
        if (_share is null || _crumbs.Count == 0) return;
        _crumbs.RemoveAt(_crumbs.Count - 1);
        var provider = PanCatalog.Find(_share.ProviderId);
        if (provider is null) return;
        var parent = _crumbs.Count == 0 ? provider.RootId : _crumbs[^1].Id;
        await LoadDirAsync(parent, resetCrumbs: false);
    }

    private async Task LoadDirAsync(string dirId, bool resetCrumbs)
    {
        if (_share is null) return;
        if (resetCrumbs) _crumbs.Clear();

        IsBusy = true;
        try
        {
            var result = await Service.ListAsync(_share, dirId, verbose: false);
            DiagnosticsText = string.Join(Environment.NewLine, result.Diagnostics);
            HasDiagnostics = result.Diagnostics.Count > 0;

            if (!result.Ok)
            {
                Files.Clear();
                HasResult = false;
                HasError = true;
                HasEmpty = false;
                ErrorText = result.Error ?? "解析失败。";
                if (result.NeedPassword) ErrorText += "\n如果是提取码问题：把提取码填到上面的输入框，再点「解析分享」。";
                SelectedCountText = "未选择文件";
                CanDownload = false;
                StatusMessage = "解析没有成功，原因见上面。";
                return;
            }

            HasError = false;
            ErrorText = string.Empty;

            Files.Clear();
            foreach (var entry in result.Entries)
            {
                var row = new PanFileVm(entry);
                // 勾选状态一变就刷新"已选择 N 个文件 / 下载按钮是否可用"。
                // 之前是靠 CheckBox 的 Click 事件驱动的，用键盘空格、或者程序内改状态时不会触发，
                // 按钮就会一直灰着。
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PanFileVm.IsSelected)) UpdateSelectedCount();
                };
                Files.Add(row);
            }

            _share.Title = result.ShareTitle ?? _share.Title;
            // 有的网盘（123）不返回分享名，这时别把裸 ID 当标题显示
            ShareTitle = string.IsNullOrWhiteSpace(_share.Title) || _share.Title == _share.ShareId
                ? $"{PanCatalog.Find(_share.ProviderId)?.DisplayName ?? "网盘"}分享"
                : _share.Title!;
            HasResult = Files.Count > 0;
            HasEmpty = Files.Count == 0;
            DirPathText = _crumbs.Count == 0
                ? "根目录"
                : "根目录 / " + string.Join(" / ", _crumbs.Select(c => c.Name));
            CanGoUp = _crumbs.Count > 0;
            UpdateSelectedCount();

            StatusMessage = Files.Count == 0
                ? "这个目录是空的。"
                : $"共 {Files.Count} 项，其中 {Files.Count(f => !f.IsFolder)} 个文件可直接下载。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 选择
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var files = Files.Where(f => !f.IsFolder).ToList();
        if (files.Count == 0) return;
        var allSelected = files.All(f => f.IsSelected);
        foreach (var f in files) f.IsSelected = !allSelected;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var f in Files) f.IsSelected = false;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        var count = Files.Count(f => f.IsSelected);
        SelectedCountText = count == 0 ? "未选择文件" : $"已选择 {count} 个文件";
        CanDownload = count > 0;
    }

    /// <summary>勾选变化时由界面调用（CheckBox 的 Click 事件）。</summary>
    public void NotifySelectionChanged() => UpdateSelectedCount();

    // ------------------------------------------------------------------ 下载
    [RelayCommand]
    private async Task DownloadSelected()
    {
        if (_share is null) return;

        var selected = Files.Where(f => f.IsSelected && !f.IsFolder).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "先勾选要下载的文件。";
            return;
        }

        // 需要登录的网盘先在本地拦一道：不然 10 个文件会打 10 次必然失败的接口，
        // 用户还得等半天才看到第一句"需要登录"。
        var provider = PanCatalog.Find(_share.ProviderId);
        if (provider is { RequiresLogin: true } && !Service.HasLogin(provider.Id))
        {
            StatusMessage = $"{provider.DisplayName} 需要先登录才能取直链。\n" +
                            $"请点上面那一行的「登录」，登录完成后再点「下载选中」。";
            return;
        }

        IsBusy = true;
        var added = 0;
        var errors = new List<string>();
        try
        {
            foreach (var row in selected)
            {
                var link = await Service.GetDownloadAsync(_share, row.Entry);
                if (!link.Ok)
                {
                    errors.Add($"{row.Name}：{link.Error}");
                    continue;
                }

                var request = new AddDownloadRequest
                {
                    Url = link.Url,
                    FileName = link.FileName ?? row.Entry.Name,
                    Title = link.FileName ?? row.Entry.Name,
                    Referer = link.Headers.GetValueOrDefault("Referer"),
                    UserAgent = link.Headers.GetValueOrDefault("User-Agent"),
                    Cookies = link.Cookies,
                    Headers = link.Headers,
                    SkipResolve = true,
                    Kind = DownloadKind.File,
                    Site = _share.ProviderId,
                    ProviderName = _share.ProviderId,
                    TotalBytes = link.Size > 0 ? link.Size : row.Entry.Size,
                    StartImmediately = true
                };

                await AppServices.Downloads.AddAsync(request);
                added++;
            }

            StatusMessage = errors.Count == 0
                ? $"已添加 {added} 个下载任务，到「最近下载」查看进度。"
                : $"已添加 {added} 个任务；{errors.Count} 个失败：\n" + string.Join("\n", errors.Take(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyLink(PanFileVm? row)
    {
        if (_share is null || row is null || row.IsFolder) return;

        var provider = PanCatalog.Find(_share.ProviderId);
        if (provider is { RequiresLogin: true } && !Service.HasLogin(provider.Id))
        {
            StatusMessage = $"{provider.DisplayName} 需要先登录才能取直链，请先点上面那一行的「登录」。";
            return;
        }

        IsBusy = true;
        try
        {
            var link = await Service.GetDownloadAsync(_share, row.Entry);
            if (!link.Ok)
            {
                StatusMessage = "取直链失败：" + link.Error;
                return;
            }

            try { Clipboard.SetText(link.Url); }
            catch { /* 剪贴板被别的程序占用，忽略 */ }
            StatusMessage = $"已复制「{row.Name}」的直链到剪贴板。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        LinkText = string.Empty;
        PasswordText = string.Empty;
        Files.Clear();
        _crumbs.Clear();
        _share = null;
        CanOpenShare = false;
        HasResult = false;
        HasError = false;
        ErrorText = string.Empty;
        HasEmpty = true;
        HasDiagnostics = false;
        DiagnosticsText = string.Empty;
        ShareTitle = string.Empty;
        DirPathText = "根目录";
        SelectedCountText = "未选择文件";
        StatusMessage = "把网盘分享链接粘到上面，点「解析分享」。";
    }
}
