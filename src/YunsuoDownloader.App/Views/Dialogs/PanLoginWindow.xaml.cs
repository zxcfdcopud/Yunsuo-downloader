using System.Windows;
using Microsoft.Web.WebView2.Core;
using YunsuoDownloader.Core.Services;
using YunsuoDownloader.Core.Utils;
using Wpf.Ui.Controls;

namespace YunsuoDownloader.App.Views.Dialogs;

/// <summary>
/// 网盘登录窗口：内嵌一个 Edge WebView2，让用户在自己熟悉的网盘网页里扫码/账号登录，
/// 点「我已完成登录」后把该域名的 Cookie 取回来。
///
/// 为什么用内嵌浏览器而不是让用户手抄 Cookie：
///   F12 → Application → Cookie 这套操作对普通用户门槛太高，而且抄错一个字符就整天 403。
///   内嵌浏览器等于"程序里开了个网盘网页"，登录完拿 Cookie 是确定性的。
///
/// 用户数据放在 <c>%APPDATA%\YunsuoDownloader\webview2</c>，
/// 所以下次再登录同一家网盘，往往已经是登录状态，直接点完成就行。
/// </summary>
public partial class PanLoginWindow : FluentWindow
{
    private readonly string _loginUrl;
    private readonly string _cookieDomain;
    private readonly string _displayName;
    private bool _ready;

    /// <summary>取到的 Cookie 头（"a=1; b=2"）。窗口取消或失败时为空。</summary>
    public string CookieHeader { get; private set; } = string.Empty;

    /// <summary>用户点了「我已完成登录」并且真的取到了 Cookie。</summary>
    public bool Succeeded { get; private set; }

    public PanLoginWindow(string displayName, string loginUrl, string cookieDomain)
    {
        _displayName = displayName;
        _loginUrl = loginUrl;
        _cookieDomain = cookieDomain;

        InitializeComponent();

        Title = $"登录{displayName}";
        TitleBarHost.Title = Title;
        HintText.Text = $"在下面的窗口里登录{displayName}";
        StatusText.Text = "登录完成后点右边的「我已完成登录」，程序会把登录态加密保存在本机。";

        Loaded += OnLoadedAsync;
        Closed += (_, _) =>
        {
            try { Browser.Dispose(); } catch { /* 忽略 */ }
        };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAsync;

        try
        {
            // 先看看系统到底有没有 WebView2 运行时（Win11 自带；Win10 装过 Edge 一般也有）
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();

            var profileDir = Path.Combine(AppPaths.DataDirectory, "webview2");
            Directory.CreateDirectory(profileDir);

            var env = await CoreWebView2Environment.CreateAsync(null, profileDir);
            await Browser.EnsureCoreWebView2Async(env);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = args.IsSuccess
                    ? "页面已加载。登录完成后点右边的「我已完成登录」。"
                    : $"页面加载失败（{args.WebErrorStatus}），可以点「重试」或直接在浏览器里登录。";
                _ready = args.IsSuccess;
            };

            Browser.CoreWebView2.Navigate(_loginUrl);
        }
        catch (Exception ex)
        {
            ShowError(
                "打不开内嵌浏览器：系统里可能没有安装 WebView2 运行时。\n\n" +
                "解决办法（任选一个）：\n" +
                "1) 安装「Microsoft Edge WebView2 Runtime」（微软官网免费下载，Win11 一般自带）；\n" +
                "2) 不用内嵌登录，直接在「设置 → 网盘账号」里手动粘贴 Cookie；\n" +
                "3) 装浏览器插件，在浏览器里登录一次网盘，插件会自动把 Cookie 同步过来。\n\n" +
                $"错误详情：{ex.Message}");
        }
    }

    private async void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || Browser.CoreWebView2 is null)
        {
            StatusText.Text = "页面还没准备好，稍等一下再点。";
            return;
        }

        try
        {
            FinishButton.IsEnabled = false;
            StatusText.Text = "正在读取登录态…";

            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync($"https://{_cookieDomain}/");
            var header = string.Join("; ", cookies
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Select(c => $"{c.Name}={c.Value}"));

            if (string.IsNullOrWhiteSpace(header))
            {
                StatusText.Text = "没有取到登录 Cookie —— 请确认已经在这个窗口里成功登录了账号。";
                FinishButton.IsEnabled = true;
                return;
            }

            var important = cookies.Count(c =>
                c.Name is "BDUSS" or "__puus" or "__pus" or "STOKEN" or "PANWEB" or "shareToken" or "b-user-id");
            // 只记 Cookie 名字，不记值（值里有登录凭据）
            Log.Info($"[网盘登录] {_displayName}：取到 {cookies.Count} 个 Cookie（{header.Length} 字符，" +
                     $"含关键登录态 {important} 个）：{string.Join(",", cookies.Select(c => c.Name))}");

            CookieHeader = header;
            Succeeded = true;

            // 必须显式设 DialogResult：ShowDialog() 的返回值有 true / false / null 三种，
            // 只 Close() 不设 DialogResult 时拿到的是 null，
            // 调用方写 `if (ok == true)` 就会被判成"没登录成功"——这正是用户反馈
            // "点了我已完成登录，主页没反应"的原因。
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取 Cookie 失败：" + ex.Message;
            FinishButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        FinishButton.IsEnabled = false;
        StatusText.Text = "内嵌浏览器不可用，可以改用「手动粘贴 Cookie」或浏览器插件。";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
