using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pegasus.Cortex.Product;

/// <summary>Windows CurrentUser DPAPI, with UI forbidden. No exportable key or new package dependency.</summary>
[SupportedOSPlatform("windows")]
public static class CortexUserSecretProtection
{
    public static byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var source = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var result = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref source, null, 0, 0, 0, 1, out result) :
                CryptUnprotectData(ref source, 0, 0, 0, 0, 1, out result);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (result.Length is < 1 or > 4096) throw new InvalidDataException("client_key_invalid");
            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            // Buffers may contain unprotected API keys. Zero before releasing either allocation.
            for (var index = 0; index < source.Length; index++) Marshal.WriteByte(source.Data, index, 0);
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != 0)
            {
                for (var index = 0; index < result.Length; index++) Marshal.WriteByte(result.Data, index, 0);
                _ = LocalFree(result.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob source, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob source, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
