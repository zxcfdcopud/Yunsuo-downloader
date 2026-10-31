using System.Collections.ObjectModel;
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

        var ok = dialog.ShowDialog();
        if (ok == true && dialog.Succeeded && !string.IsNullOrWhiteSpace(dialog.CookieHeader))
        {
            AppServices.Settings.SetCredential(provider.Id, provider.DisplayName, dialog.CookieHeader);
            RefreshLogins();
            StatusMessage = $"已保存{provider.DisplayName}的登录态（DPAPI 加密，只存在本机）。";
        }
        else
        {
            StatusMessage = $"{provider.DisplayName} 没有完成登录。";
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
            StatusMessage = "这不是支持的四家网盘链接（百度 / 阿里云盘 / 夸克 / 123）。";
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
        await LoadDirAsync(provider.RootId, resetCrumbs: true);
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
                SelectedCountText = "未选择文件";
                StatusMessage = result.Error ?? "解析失败。";
                if (result.NeedPassword) StatusMessage += "\n（如果是提取码问题，把提取码填到上面的输入框再点解析）";
                return;
            }

            Files.Clear();
            foreach (var entry in result.Entries)
                Files.Add(new PanFileVm(entry));

            _share.Title = result.ShareTitle ?? _share.Title;
            ShareTitle = _share.Title ?? _share.ShareId;
            HasResult = Files.Count > 0;
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
        HasResult = false;
        HasDiagnostics = false;
        DiagnosticsText = string.Empty;
        ShareTitle = string.Empty;
        DirPathText = "根目录";
        SelectedCountText = "未选择文件";
        StatusMessage = "把网盘分享链接粘到上面，点「解析分享」。";
    }
}
