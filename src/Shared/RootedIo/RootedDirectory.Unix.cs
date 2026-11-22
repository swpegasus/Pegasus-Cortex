using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.RootedIo;

internal sealed partial class RootedDirectory
{
    private static bool Darwin => OperatingSystem.IsMacOS();
    private static int NoFollow => Darwin ? 0x100 : 0x20000;
    private static int DirectoryFlag => Darwin ? 0x100000 : 0x10000;
    private static int CloseOnExec => Darwin ? 0x1000000 : 0x80000;
    private static int NonBlock => Darwin ? 4 : 0x800;
    private static int CreateFlag => Darwin ? 0x200 : 0x40;
    private static int ExclusiveFlag => Darwin ? 0x800 : 0x80;
    private static int RemoveDirectoryFlag => Darwin ? 0x80 : 0x200;

    private static Node UnixOpenRoot(string path)
    {
        if (!OperatingSystem.IsLinux() && !Darwin)
            throw new PlatformNotSupportedException("Handle-relative I/O requires Windows, Linux or macOS.");
        var descriptor = UnixOpen(path, NoFollow | DirectoryFlag | CloseOnExec | NonBlock, 0);
        if (descriptor < 0) throw NativeError(Marshal.GetLastPInvokeError());
        return UnixNode(new SafeFileHandle((IntPtr)descriptor, true), null, path, directory: true);
    }

    private static Node UnixOpenChild(Node parent, string name, bool directory, bool create, bool exclusive,
        bool writable, bool deletion, uint mode)
    {
        UnixRequireBinding(parent);
        if (directory && create)
        {
            if (UnixMkdirAt(Fd(parent.Handle), name, mode) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 17 || exclusive) throw NativeError(error);
            }
        }
        var flags = NoFollow | CloseOnExec | NonBlock | (directory ? DirectoryFlag : writable ? 2 : 0);
        if (!directory && create) flags |= CreateFlag | (exclusive ? ExclusiveFlag : 0);
        var descriptor = UnixOpenAt(Fd(parent.Handle), name, flags, mode);
        if (descriptor < 0) throw NativeError(Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((IntPtr)descriptor, true);
        try
        {
            // Match FileShare.None/Read for cooperative .NET writers and the existing write lease.
            if (!directory && UnixFlock(descriptor, (writable ? 2 : 1) | 4) != 0)
                throw NativeError(Marshal.GetLastPInvokeError());
            return UnixNode(handle, parent, name, directory);
        }
        catch { handle.Dispose(); throw; }
    }

    private static Node UnixNode(SafeFileHandle handle, Node? parent, string name, bool directory)
    {
        try
        {
            var information = UnixInfo(handle, null);
            if (information.Type != (directory ? 0x4000 : 0x8000))
                throw new IOException("The opened entry is not the admitted ordinary file or directory.");
            return new Node(handle, parent, name, directory, information.Identity);
        }
        catch { handle.Dispose(); throw; }
    }

    private readonly record struct UnixInformation(FileIdentity Identity, int Mode)
    { internal int Type => Mode & 0xf000; }

    private static UnixInformation UnixInfo(SafeFileHandle handle, string? child)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // statx has a fixed kernel ABI across supported Linux architectures.
                if (UnixStatx(Fd(handle), child ?? string.Empty, child is null ? 0x1100 : 0x100, 0x7ff, buffer) != 0)
                    throw NativeError(Marshal.GetLastPInvokeError());
                if ((Marshal.ReadInt32(buffer, 0) & 0x103) != 0x103)
                    throw new IOException("The filesystem did not supply the required type and inode identity.");
                var device = ((ulong)(uint)Marshal.ReadInt32(buffer, 136) << 32) | (uint)Marshal.ReadInt32(buffer, 140);
                return new(new(device, (ulong)Marshal.ReadInt64(buffer, 32)), (ushort)Marshal.ReadInt16(buffer, 28));
            }
            var result = child is null ? UnixFstat(Fd(handle), buffer) : UnixFstatAt(Fd(handle), child, buffer, 0x20);
            if (result != 0) throw NativeError(Marshal.GetLastPInvokeError());
            return new(new((uint)Marshal.ReadInt32(buffer, 0), (ulong)Marshal.ReadInt64(buffer, 8)),
                (ushort)Marshal.ReadInt16(buffer, 4));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IReadOnlyList<Entry> UnixEnumerate(Node node)
    {
        UnixRequireBinding(node);
        // fdopendir takes ownership of its descriptor; rewind to avoid a shared dup offset
        // leaving a later inventory/cleanup at the previous enumeration's end.
        var descriptor = UnixOpenAt(Fd(node.Handle), ".", DirectoryFlag | CloseOnExec | NoFollow, 0);
        if (descriptor < 0) throw NativeError(Marshal.GetLastPInvokeError());
        var directory = UnixFdOpenDir(descriptor);
        if (directory == IntPtr.Zero) { var error = Marshal.GetLastPInvokeError(); UnixClose(descriptor); throw NativeError(error); }
        var entries = new List<Entry>();
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var item = Darwin ? UnixReadDir(directory) : UnixReadDir64(directory);
                if (item == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0) throw NativeError(error);
                    break;
                }
                var offset = Darwin ? 21 : 19;
                var recordLength = (ushort)Marshal.ReadInt16(item, 16);
                if (recordLength <= offset) throw new IOException("Invalid native directory entry.");
                var bytes = new byte[recordLength - offset];
                Marshal.Copy(IntPtr.Add(item, offset), bytes, 0, bytes.Length);
                var end = Array.IndexOf(bytes, (byte)0);
                if (end < 0) throw new IOException("Unterminated native directory entry.");
                var name = new UTF8Encoding(false, true).GetString(bytes, 0, end);
                if (name is "." or "..") continue;
                ValidateName(name);
                var information = UnixInfo(node.Handle, name);
                if (entries.Count >= 10_000) throw new IOException("The directory enumeration exceeds its admitted entry bound.");
                entries.Add(new(name, information.Type == 0x4000, information.Type == 0xa000, information.Identity));
            }
            return entries;
        }
        finally { UnixCloseDir(directory); }
    }

    private static void UnixRequireIdentity(Node node, string name)
    {
        var parent = node.Parent ?? throw new IOException("A filesystem root cannot be removed or published.");
        if (UnixInfo(parent.Handle, name).Identity != node.Identity)
            throw new IOException("The owned filesystem entry was replaced; its replacement was not adopted.");
    }

    private static void UnixRequireBinding(Node node)
    {
        for (var current = node; current.Parent is not null; current = current.Parent)
            UnixRequireIdentity(current, current.Name);
    }

    private static void UnixRename(Node node, string target, bool replace)
    {
        var parent = node.Parent ?? throw new IOException("A filesystem root cannot be renamed.");
        UnixRequireBinding(node);
        var descriptor = Fd(parent.Handle);
        int result;
        try
        {
            result = replace ? UnixRenameAt(descriptor, node.Name, descriptor, target)
                : Darwin ? UnixRenameAtExclusive(descriptor, node.Name, descriptor, target, 4)
                : UnixRenameAt2(descriptor, node.Name, descriptor, target, 1);
        }
        catch (EntryPointNotFoundException error)
        { throw new PlatformNotSupportedException("The filesystem requires an atomic no-replace rename primitive.", error); }
        if (result != 0) throw NativeError(Marshal.GetLastPInvokeError());
        node.Name = target; node.Published = true;
        UnixRequireBinding(node);
    }

    private static void UnixDelete(Node node)
    {
        UnixRequireBinding(node);
        if (UnixUnlinkAt(Fd(node.Parent!.Handle), node.Name, node.Directory ? RemoveDirectoryFlag : 0) != 0)
            throw NativeError(Marshal.GetLastPInvokeError());
    }

    private static void UnixDeleteLink(Node parent, Entry entry)
    {
        UnixRequireBinding(parent);
        var information = UnixInfo(parent.Handle, entry.Name);
        if (information.Type != 0xa000 || information.Identity != entry.Identity)
            throw new IOException("The cleanup link was replaced; the new object was not adopted.");
        if (UnixUnlinkAt(Fd(parent.Handle), entry.Name, 0) != 0) throw NativeError(Marshal.GetLastPInvokeError());
    }

    private static void UnixSetMode(Node node, UnixFileMode mode)
    { UnixRequireBinding(node); if (UnixFchmod(Fd(node.Handle), (uint)mode & 0xfff) != 0) throw NativeError(Marshal.GetLastPInvokeError()); }
    private static UnixFileMode UnixGetMode(Node node) => (UnixFileMode)(UnixInfo(node.Handle, null).Mode & 0xfff);

    private static SafeFileHandle UnixDuplicate(SafeFileHandle handle)
    {
        var descriptor = UnixDup(Fd(handle));
        if (descriptor < 0) throw NativeError(Marshal.GetLastPInvokeError());
        if (UnixFcntl(descriptor, 2, 1) != 0) { var error = Marshal.GetLastPInvokeError(); UnixClose(descriptor); throw NativeError(error); }
        return new SafeFileHandle((IntPtr)descriptor, true);
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int UnixOpenAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int UnixMkdirAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int UnixStatx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mask, IntPtr result);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int UnixFstat(int fd, IntPtr result);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)] private static extern int UnixFstatAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr result, int flags);
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)] private static extern int UnixRenameAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int targetParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int UnixRenameAt2(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int targetParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);
    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)] private static extern int UnixRenameAtExclusive(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int targetParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int UnixUnlinkAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static extern int UnixFchmod(int fd, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int UnixFlock(int fd, int operation);
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)] private static extern int UnixDup(int fd);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int UnixFcntl(int fd, int operation, int value);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int UnixClose(int fd);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr UnixFdOpenDir(int fd);
    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr UnixReadDir(IntPtr directory);
    [DllImport("libc", EntryPoint = "readdir64", SetLastError = true)] private static extern IntPtr UnixReadDir64(IntPtr directory);
    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)] private static extern int UnixCloseDir(IntPtr directory);
}
