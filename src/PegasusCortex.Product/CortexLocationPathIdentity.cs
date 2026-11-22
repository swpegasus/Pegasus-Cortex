using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Product;

/// <summary>
/// Metadata-only admission checks for explicitly selected semantic locations.
/// Physical paths are comparison identities, never replacement database paths.
/// These checks are snapshots; the caller must preserve destination ownership
/// and revalidate before a later open rather than treat them as lifetime locks.
/// </summary>
internal static class CortexLocationPathIdentity
{
    private const uint ReadAttributes = 0x00000080;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint VolumeNameGuid = 1;
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const int MaximumFinalPathCharacters = 32768;

    internal static string CanonicalRoot(string value)
    {
        var canonical = CanonicalPath(value);
        if (CortexProductLayout.PathsEqual(canonical, Path.GetPathRoot(canonical)!))
            throw new ArgumentException("A semantic location cannot be a volume root.", nameof(value));
        CortexProductLayout.RejectExistingReparseComponents(canonical);
        using var handle = OpenMetadata(canonical, out var error);
        if (handle.IsInvalid)
        {
            if (IsAbsent(error)) return canonical;
            throw MetadataFailure(error);
        }
        RequireRegular(ReadInformation(handle), directory: true);
        return canonical;
    }

    /// <summary>
    /// Resolves existing aliases through the volume GUID and normalized final
    /// name. An absent tail is appended to the nearest existing directory;
    /// access or identity failures never fall back to a lexical comparison.
    /// </summary>
    internal static string PhysicalPath(string canonicalPath)
    {
        var candidate = CanonicalPath(canonicalPath);
        CortexProductLayout.RejectExistingReparseComponents(candidate);
        var missing = new Stack<string>();
        while (true)
        {
            using var handle = OpenMetadata(candidate, out var error);
            if (!handle.IsInvalid)
            {
                var information = ReadInformation(handle);
                RequireRegular(information, directory: missing.Count > 0 ? true : null);
                var physical = FinalPath(handle);
                foreach (var segment in missing)
                    physical = Path.Combine(physical, segment);
                return Path.TrimEndingDirectorySeparator(physical);
            }
            if (!IsAbsent(error)) throw MetadataFailure(error);
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || CortexProductLayout.PathsEqual(parent, candidate))
                throw MetadataFailure(error);
            missing.Push(Path.GetFileName(candidate));
            candidate = parent;
        }
    }

    internal static void RequireSingleLinkFile(string canonicalFile)
    {
        var canonical = CanonicalPath(canonicalFile);
        CortexProductLayout.RejectExistingReparseComponents(canonical);
        using var handle = OpenMetadata(canonical, out var error);
        if (handle.IsInvalid)
        {
            if (IsAbsent(error)) return;
            throw MetadataFailure(error);
        }
        var information = ReadInformation(handle);
        RequireRegular(information, directory: false);
        if (information.NumberOfLinks != 1)
            throw new InvalidDataException("An isolated semantic file must have exactly one filesystem link.");
    }

    private static string CanonicalPath(string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Semantic location identity requires Windows filesystem metadata.");
        var canonical = CortexProductLayout.RequireCanonicalAbsoluteDirectory(value, nameof(value));
        var root = Path.GetPathRoot(canonical)!;
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("A semantic location must use a local drive path.", nameof(value));
        return canonical;
    }

    private static SafeFileHandle OpenMetadata(string path, out int error)
    {
        var handle = CreateFileW(path, ReadAttributes, ShareAll, 0, OpenExisting,
            BackupSemantics | OpenReparsePoint, 0);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    private static FileInformation ReadInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw MetadataFailure(Marshal.GetLastWin32Error());
        return information;
    }

    private static void RequireRegular(FileInformation information, bool? directory)
    {
        var attributes = (FileAttributes)information.Attributes;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            directory is { } expected && ((attributes & FileAttributes.Directory) != 0) != expected)
            throw new InvalidDataException("The semantic location has an unexpected filesystem object type.");
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(MaximumFinalPathCharacters);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameGuid);
        if (length == 0) throw MetadataFailure(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity)
            throw new PathTooLongException("The physical semantic location exceeds the identity buffer.");
        var final = buffer.ToString();
        const string prefix = @"\\?\Volume{";
        var end = final.IndexOf("}\\", StringComparison.Ordinal);
        if (final.Length != length || !final.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            end < prefix.Length || !Guid.TryParseExact(final[prefix.Length..end], "D", out _))
            throw new InvalidDataException("A normalized local volume identity is required for semantic location admission.");
        return final;
    }

    private static bool IsAbsent(int error) => error is FileNotFound or PathNotFound;

    private static IOException MetadataFailure(int error) =>
        new("Semantic location filesystem identity could not be established.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path,
        uint characters, uint flags);
}
