using System.Runtime.InteropServices;

namespace WeChatDriver.Store;

/// <summary>DPAPI 保护（当前用户）。用于保护本地令牌等最小敏感数据。</summary>
public static class DpapiProtector
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        nint pvReserved, nint pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        nint pvReserved, nint pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public nint pbData;
    }

    public static byte[] Protect(byte[] plain)
    {
        var inBlob = ToBlob(plain);
        var entropy = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        if (!CryptProtectData(ref inBlob, "WeChatDriver", ref entropy, nint.Zero, nint.Zero, 0, ref outBlob))
            throw new InvalidOperationException("CryptProtectData failed");
        return FromBlob(outBlob);
    }

    public static byte[] Unprotect(byte[] cipher)
    {
        var inBlob = ToBlob(cipher);
        var entropy = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        if (!CryptUnprotectData(ref inBlob, "WeChatDriver", ref entropy, nint.Zero, nint.Zero, 0, ref outBlob))
            throw new InvalidOperationException("CryptUnprotectData failed");
        return FromBlob(outBlob);
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length };
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        blob.pbData = ptr;
        return blob;
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        LocalFree(blob.pbData);
        return result;
    }
}
