using System.Windows;

namespace YunsuoDownloader.App.Views;

/// <summary>通用文本输入对话框（用于输入网盘 Cookie 等）。</summary>
public partial class InputDialog : Wpf.Ui.Controls.FluentWindow
{
    private string? _result;

    public InputDialog()
    {
        InitializeComponent();
    }

    /// <summary>返回 null 表示用户取消。</summary>
    public static string? Show(string title, string message, string initialValue,
        bool multiline = false, bool isSecret = false)
    {
        var dlg = new InputDialog
        {
            Title = title,
            Owner = Application.Current?.MainWindow
        };
        dlg.Bar.Title = title;
        dlg.MessageText.Text = message;
        dlg.ValueBox.Text = initialValue;
        dlg.ValueBox.AcceptsReturn = multiline;
        dlg.ValueBox.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (isSecret) dlg.ValueBox.FontFamily = new System.Windows.Media.FontFamily("Consolas");

        dlg.ValueBox.Focus();
        dlg.ValueBox.CaretIndex = dlg.ValueBox.Text.Length;

        dlg.ShowDialog();
        return dlg._result;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _result = ValueBox.Text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        DialogResult = false;
        Close();
    }
}
