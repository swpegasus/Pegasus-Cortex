using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

// Observation of a borrowed retained handle, shared by vault reads and private
// export publication. Callers own open/share/ACL/path admission and disposal.
[SupportedOSPlatform("windows")]
internal sealed class CertificateFileIdentity
{
    private readonly SafeFileHandle _handle;
    private readonly IdentityInfo _identity;

    internal CertificateFileIdentity(SafeFileHandle handle, string expectedPath)
    {
        _handle = handle;
        _identity = ReadIdentity();
        RequireAt(expectedPath);
    }

    internal void RequireAt(string path)
    {
        if (ReadIdentity() != _identity ||
            !GetAttributeInfo(_handle, 9, out var attributes, 8) ||
            (attributes.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !GetStandardInfo(_handle, 1, out var standard, checked((uint)Marshal.SizeOf<StandardInfo>())) ||
            standard.Links != 1 || standard.DeletePending != 0)
            throw new IOException("certificate_file_identity_changed");
        var buffer = new char[512];
        var count = GetFinalPathNameByHandleW(_handle, buffer, checked((uint)buffer.Length), 0);
        if (count is 0 or >= 512 || !string.Equals(new string(buffer, 0, checked((int)count)), "\\\\?\\" + path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("certificate_file_identity_changed");
    }

    private IdentityInfo ReadIdentity() => GetIdentityInfo(_handle, 18, out var identity, 24)
        ? identity : throw new IOException("certificate_file_identity_unavailable");

    internal void PublishAs(string destination, bool replaceExisting = false)
    {
        // FILE_RENAME_INFO uses a byte BOOLEAN followed by pointer-aligned
        // HANDLE, DWORD byte length and UTF-16 filename. Marshal supplies the
        // x86/x64 offsets. Default remains no-replace for all certificate callers. Only a caller
        // holding its cooperative profile lock may request replacement; this is
        // not compare-and-swap against arbitrary same-owner path edits.
        // root=NULL; absolute local path.
        var name = Encoding.Unicode.GetBytes(destination + '\0');
        var offset = checked((int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName)));
        var lengthOffset = checked((int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileNameLength)));
        var bytes = new byte[checked(offset + name.Length)];
        name.CopyTo(bytes, offset);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            if (replaceExisting) Marshal.WriteByte(memory, 0, 1);
            Marshal.WriteInt32(memory, lengthOffset, name.Length - sizeof(char));
            if (!SetFileInformationByHandle(_handle, 3, memory, checked((uint)bytes.Length)))
                throw new IOException("certificate_file_publish_failed", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(memory); }
        RequireAt(destination);
    }

    internal void DeleteOnClose()
    {
        // FILE_DISPOSITION_INFO addresses this already-retained object, even
        // if a pathname was concurrently displaced. No pathname delete/retry.
        var disposition = new DispositionInfo { DeleteFile = true };
        if (!SetDisposition(_handle, 4, ref disposition, checked((uint)Marshal.SizeOf<DispositionInfo>())))
            throw new IOException("certificate_file_cleanup_failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct RenameInfo
    {
        [MarshalAs(UnmanagedType.U1)] internal bool ReplaceIfExists;
        internal nint RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DispositionInfo
    { [MarshalAs(UnmanagedType.U1)] internal bool DeleteFile; }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long Allocation, Length; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct IdentityInfo(ulong Volume, ulong Low, ulong High);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeInfo(SafeFileHandle handle, int kind, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentityInfo(SafeFileHandle handle, int kind, out IdentityInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] name, uint capacity, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, nint info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref DispositionInfo info, uint size);
}
