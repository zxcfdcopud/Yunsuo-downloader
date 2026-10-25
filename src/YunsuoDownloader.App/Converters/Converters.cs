using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using YunsuoDownloader.Core.Models;
using YunsuoDownloader.Core.Utils;

namespace YunsuoDownloader.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

public sealed class BoolToInverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is long l ? Format.Bytes(l) : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SpeedToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0 ? Format.Speed(d) : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PercentToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d:0.0}%" : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>下载状态 -> 颜色。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DownloadStatus.Completed => "SystemFillColorSuccessBrush",
            DownloadStatus.Failed => "SystemFillColorCriticalBrush",
            DownloadStatus.Paused => "SystemFillColorCautionBrush",
            DownloadStatus.Canceled => "SystemFillColorNeutralBrush",
            DownloadStatus.Downloading => "SystemFillColorAttentionBrush",
            DownloadStatus.Merging => "SystemFillColorAttentionBrush",
            DownloadStatus.Connecting => "SystemFillColorAttentionBrush",
            DownloadStatus.Resolving => "SystemFillColorAttentionBrush",
            _ => "TextFillColorSecondaryBrush"
        };
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return value is DownloadStatus.Completed ? Brushes.SeaGreen
             : value is DownloadStatus.Failed ? Brushes.IndianRed
             : Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>下载状态 -> Fluent 图标字形（Segoe Fluent Icons 码位）。</summary>
public sealed class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DownloadStatus.Completed => "\uE930",
            DownloadStatus.Failed => "\uEA39",
            DownloadStatus.Paused => "\uE769",
            DownloadStatus.Canceled => "\uE711",
            DownloadStatus.Downloading => "\uE896",
            DownloadStatus.Merging => "\uE9F5",
            DownloadStatus.Connecting => "\uE703",
            DownloadStatus.Resolving => "\uE9D9",
            DownloadStatus.Queued => "\uE823",
            _ => "\uE946"
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
