using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace YunsuoDownloader.Core.Utils;

/// <summary>
/// 使用 Windows DPAPI（CryptProtectData）加密本机敏感数据（网盘 Cookie）。
/// 刻意不引入 NuGet 包，直接 P/Invoke crypt32.dll。
/// 加密结果与当前 Windows 用户绑定：换用户或换机器都无法解密。
/// </summary>
public static class SecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YunsuoDownloader.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!OperatingSystem.IsWindows()) return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            Marshal.Copy(Entropy, 0, entBlob.pbData, Entropy.Length);
            if (!CryptProtectData(ref inBlob, "YunsuoDownloader", ref entBlob, IntPtr.Zero, IntPtr.Zero,
                                  CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return Convert.ToBase64String(result);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        catch { return null; }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entBlob.pbData);
        }
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        if (!OperatingSystem.IsWindows())
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(protectedBase64)); } catch { return null; }
        }

        byte[] cipher;
        try { cipher = Convert.FromBase64String(protectedBase64); } catch { return null; }

        var inBlob = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(cipher, 0, inBlob.pbData, cipher.Length);
            Marshal.Copy(Entropy, 0, entBlob.pbData, Entropy.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(result);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        catch { return null; }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entBlob.pbData);
        }
    }

    /// <summary>生成插件连接用的随机令牌。</summary>
    public static string NewToken(int length = 24)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}
