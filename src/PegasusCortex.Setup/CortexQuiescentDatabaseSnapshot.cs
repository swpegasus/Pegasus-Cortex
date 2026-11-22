using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Setup;

/// <summary>
/// Captures a closed database and every present companion without opening the
/// original through SQLite. SQLite inspection/recovery is confined to WorkingPath.
/// </summary>
internal sealed class CortexQuiescentDatabaseSnapshot : IDisposable
{
    private static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];
    private readonly string _root;
    private readonly string _parent;
    private int _disposed;
    internal string WorkingPath { get; }

    private CortexQuiescentDatabaseSnapshot(string root, string workingPath, string parent)
    { _root = root; WorkingPath = workingPath; _parent = parent; }

    internal static CortexQuiescentDatabaseSnapshot Capture(string sourcePath, CancellationToken cancellationToken = default,
        string? privateScratchBase = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateScratchBase ?? Path.GetTempPath()));
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (privateScratchBase is null)
                    throw new UnauthorizedAccessException("Elevated database capture requires an explicit machine-protected scratch location.");
                CortexPrivilegedCodePath.RequireProtectedAncestors(parent);
            }
        }
        var root = Path.Combine(parent, "cortex-db-" + Guid.NewGuid().ToString("N"));
        if (Path.Combine(root, "captured", "database.db-journal").Length > 240 ||
            source.Length + "-journal".Length > 240 || Directory.Exists(root) || File.Exists(root))
            throw new IOException("A private database snapshot path is unavailable.");
        var held = new List<(string Suffix, FileStream Stream, long Length)>();
        var created = false;
        try
        {
            CreatePrivateDirectory(root);
            created = true;
            var captured = Path.Combine(root, "captured");
            var working = Path.Combine(root, "working");
            Directory.CreateDirectory(captured);
            Directory.CreateDirectory(working);
            foreach (var suffix in Suffixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = source + suffix;
                RejectReparseComponents(path);
                if (Directory.Exists(path)) throw new IOException("A database companion identifies a directory.");
                if (!File.Exists(path))
                {
                    if (suffix.Length == 0) throw new FileNotFoundException("The database is absent.", path);
                    continue;
                }
                // Deny existing/new writers and replacement for the full set.
                // A live SQLite writer makes this admission fail, not a fuzzy copy.
                var stream = OpenRetainedSource(path);
                held.Add((suffix, stream, stream.Length));
            }
            foreach (var item in held)
            {
                var file = "database.db" + item.Suffix;
                using (var destination = new FileStream(Path.Combine(captured, file),
                           FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = item.Stream.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        destination.Write(buffer, 0, count);
                    }
                    destination.Flush(flushToDisk: true);
                }
                if (item.Stream.Length != item.Length)
                    throw new IOException("A database companion changed during capture.");
                File.Copy(Path.Combine(captured, file), Path.Combine(working, file), overwrite: false);
            }
            foreach (var suffix in Suffixes)
                if (File.Exists(source + suffix) != held.Any(item => item.Suffix == suffix))
                    throw new IOException("The database companion set changed during capture.");
            return new(root, Path.Combine(working, "database.db"), parent);
        }
        catch (Exception primary)
        {
            if (created)
                try { DeleteOwnedScratch(root, parent); }
                catch { primary.Data["CortexSnapshotCleanup"] = "private-snapshot-retained"; }
            throw;
        }
        finally { foreach (var item in held) item.Stream.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) DeleteOwnedScratch(_root, _parent);
    }

    internal static void CreatePrivateDirectory(string root)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(root); return; }
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var owner = administrator ? new SecurityIdentifier("S-1-5-32-544") : identity.User!;
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { owner, new SecurityIdentifier("S-1-5-18") }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(security);
    }

    internal static FileStream OpenRetainedSource(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new FileStream(OpenRetainedEntry(path, 0x80000000), FileAccess.Read);
    }

    internal static SafeFileHandle OpenRetainedEntry(string path, uint access)
    {
        var handle = CreateFile(path, access, 1, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("A database capture source is not quiescent or readable.",
                new System.ComponentModel.Win32Exception(error));
        }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8) ||
                (attributes.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("A captured database file is redirected or unavailable.");
            var resolved = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, resolved, checked((uint)resolved.Capacity), 0);
            var final = resolved.ToString();
            if (length == 0 || length >= resolved.Capacity || !final.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                !string.Equals(final[4..], path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A captured database file escaped its exact canonical path.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag value, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    private static void RejectReparseComponents(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A database capture path contains a reparse point.");
    }

    private static void DeleteOwnedScratch(string root, string expected)
    {
        if (!string.Equals(Path.GetDirectoryName(root), expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith("cortex-db-", StringComparison.Ordinal) ||
            Path.GetFileName(root).Length != "cortex-db-".Length + 32)
            throw new IOException("Scratch cleanup escaped the owned database capture.");
        if (!Directory.Exists(root)) return;
        RejectReparseComponents(root);
        Directory.Delete(root, recursive: true);
    }
}
