using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Certificates;

// One admitted object remains open through the SIP call and every postcheck.
// Write/delete sharing is never enabled to accommodate a provider that reopens it.
internal sealed class AuthenticodeFileLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly FileIdentity _identity;
    internal string Path { get; }
    internal SafeFileHandle Handle => _stream.SafeFileHandle;

    internal AuthenticodeFileLease(string admittedRoot, string path, bool writable)
    {
        Path = Canonical(path, directory: false);
        var root = Canonical(admittedRoot, directory: true);
        if (!Path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CertificateOperationException("authenticode_file_not_admitted");
        RequireOrdinaryComponents(Path);
        var handle = CreateFileW(Path, writable ? 0xc0000000u : 0x80000000u, 1, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("The admitted Authenticode file cannot be exclusively retained.", new Win32Exception(error));
        }
        try
        {
            if (!GetAttributeInfo(handle, 9, out var attributes, 8) ||
                (attributes.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new CertificateOperationException("authenticode_file_not_ordinary");
            if (!GetStandardInfo(handle, 1, out var standard, checked((uint)Marshal.SizeOf<StandardInfo>())))
                throw new IOException("The admitted file identity is unavailable.");
            if (standard.DeletePending != 0 || writable && standard.Links != 1)
                throw new CertificateOperationException("authenticode_file_links_unsupported");
            _identity = Identity(handle);
            RequireFinalPath(handle, Path);
            _stream = new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal string Hash(CancellationToken token)
    {
        RequireIdentity(Handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(Handle, buffer, offset)) != 0)
            { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); offset = checked(offset + read); }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal void Flush() => _stream.Flush(flushToDisk: true);
    internal void RequireIdentity(SafeFileHandle handle)
    {
        if (Identity(handle) != _identity) throw new CertificateOperationException("authenticode_file_identity_changed");
        RequireFinalPath(handle, Path);
    }
    public void Dispose() => _stream.Dispose();

    internal static string Canonical(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Length > 240)
            throw new CertificateOperationException("authenticode_path_invalid");
        var canonical = directory ? System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)) : System.IO.Path.GetFullPath(path);
        if (!string.Equals(canonical, directory ? System.IO.Path.TrimEndingDirectorySeparator(path) : path, StringComparison.OrdinalIgnoreCase))
            throw new CertificateOperationException("authenticode_path_invalid");
        var root = System.IO.Path.GetPathRoot(canonical)!;
        if (canonical.Length == root.Length) throw new CertificateOperationException("authenticode_path_invalid");
        foreach (var segment in canonical[root.Length..].Split(System.IO.Path.DirectorySeparatorChar))
        {
            var stem = segment.Split('.')[0];
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                System.Text.Encoding.UTF8.GetByteCount(segment) > 255 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    "123456789¹²³".Contains(stem[3])))
                throw new CertificateOperationException("authenticode_path_invalid");
        }
        return canonical;
    }

    private static void RequireOrdinaryComponents(string path)
    {
        for (var current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CertificateOperationException("authenticode_reparse_unsupported");
    }
    private static FileIdentity Identity(SafeFileHandle handle) => GetIdentityInfo(handle, 18, out var identity, 24)
        ? identity : throw new IOException("The admitted file identity is unavailable.");
    private static void RequireFinalPath(SafeFileHandle handle, string expected)
    {
        var name = new char[512];
        var length = GetFinalPathNameByHandleW(handle, name, checked((uint)name.Length), 0);
        if (length is 0 or >= 512 || !string.Equals(new string(name, 0, checked((int)length)), "\\\\?\\" + expected, StringComparison.OrdinalIgnoreCase))
            throw new CertificateOperationException("authenticode_file_identity_changed");
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long Allocation, Length; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeInfo(SafeFileHandle file, int kind, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle file, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentityInfo(SafeFileHandle file, int kind, out FileIdentity info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint capacity, uint flags);
}
