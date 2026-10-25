using System.Windows;

namespace YunsuoDownloader.App.Views;

/// <summary>Fluent 风格的提示/确认窗口（替代系统 MessageBox）。</summary>
public partial class MessageBoxWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool? _result;

    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    /// <summary>纯提示。</summary>
    public static void ShowInfo(string message, string title = "提示", string buttonText = "知道了")
    {
        var w = new MessageBoxWindow
        {
            Title = title,
            Owner = Application.Current?.MainWindow
        };
        w.Bar.Title = title;
        w.HeaderText.Text = title;
        w.BodyText.Text = message;
        w.PrimaryButton.Content = buttonText;
        w.CancelButton.Visibility = Visibility.Collapsed;
        w.ShowDialog();
    }

    /// <summary>
    /// 两/三选一确认。
    /// 返回 true = 主按钮；false = 次按钮；null = 取消/关闭。
    /// </summary>
    public static bool? Confirm(string message, string title, string primaryText, string secondaryText,
        string? cancelText = "取消")
    {
        var w = new MessageBoxWindow
        {
            Title = title,
            Owner = Application.Current?.MainWindow
        };
        w.Bar.Title = title;
        w.HeaderText.Text = title;
        w.BodyText.Text = message;
        w.PrimaryButton.Content = primaryText;
        w.SecondaryButton.Content = secondaryText;
        w.SecondaryButton.Visibility = Visibility.Visible;

        if (cancelText is null) w.CancelButton.Visibility = Visibility.Collapsed;
        else
        {
            w.CancelButton.Content = cancelText;
            w.CancelButton.Visibility = Visibility.Visible;
        }

        w.ShowDialog();
        return w._result;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _result = true;
        DialogResult = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _result = false;
        DialogResult = false;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        DialogResult = null;
        Close();
    }
}
