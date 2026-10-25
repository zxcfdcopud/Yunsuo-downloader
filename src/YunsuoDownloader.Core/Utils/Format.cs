namespace YunsuoDownloader.Core.Utils;

/// <summary>统一的格式化工具（中文界面用）。</summary>
public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {Units[i]}" : $"{v:0.00} {Units[i]}";
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes:00}:{d.Seconds:00}";
    }

    /// <summary>把 aria2/yt-dlp 的各种大小写法解析为字节（兼容 "1.2MiB" 与纯数字）。</summary>
    public static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim();
        if (long.TryParse(text, out var plain)) return plain;
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ',')) i++;
        if (i == 0) return 0;
        if (!double.TryParse(text[..i].Replace(",", ""), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var num)) return 0;
        var unit = text[i..].Trim().ToUpperInvariant().Replace("IB", "B");
        double mul = unit switch
        {
            "K" or "KB" => 1024d,
            "M" or "MB" => 1024d * 1024,
            "G" or "GB" => 1024d * 1024 * 1024,
            "T" or "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return (long)(num * mul);
    }

    /// <summary>把文件名里 Windows 不允许的字符替换掉。</summary>
    public static string SafeFileName(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0) result = fallback;
        if (result.Length > 180) result = result[..180];
        return result;
    }

    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
