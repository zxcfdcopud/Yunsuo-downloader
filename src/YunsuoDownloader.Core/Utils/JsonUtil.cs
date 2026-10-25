using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YunsuoDownloader.Core.Utils;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T? ReadFile<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return default;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch { return default; }
    }

    /// <summary>原子写入：先写临时文件再替换，避免崩溃时损坏配置。</summary>
    public static bool WriteFile<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch { File.Copy(tmp, path, true); File.Delete(tmp); }
            }
            else File.Move(tmp, path);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
