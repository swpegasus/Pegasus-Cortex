using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.RootedIo;

internal sealed partial class RootedDirectory
{
    private static Node NativeOpenRoot(string path)
    {
        if (!OperatingSystem.IsWindows()) return UnixOpenRoot(path);
        // Deny delete/write sharing for admitted directories, including empty ones that
        // could otherwise be renamed or converted to a reparse point between calls.
        var handle = CreateFileW(path, 0x1000a1, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw NativeError(error); }
        return WindowsNode(handle, null, path, directory: true, allowReparse: false);
    }

    private static Node NativeOpenChild(Node parent, string name, bool directory, bool create, bool exclusive,
        bool writable, bool deletion, uint mode)
    {
        if (!OperatingSystem.IsWindows()) return UnixOpenChild(parent, name, directory, create, exclusive, writable, deletion, mode);
        return WindowsOpenChild(parent, name, directory, create, exclusive, writable, deletion, allowReparse: false);
    }

    private static Node WindowsOpenChild(Node parent, string name, bool directory, bool create, bool exclusive,
        bool writable, bool deletion, bool allowReparse, bool shareDelete = false)
    {
        if (directory && create && !exclusive)
        {
            try { return WindowsOpenChild(parent, name, true, false, false, false, deletion, allowReparse); }
            catch (IOException error) when (IsMissing(error))
            {
                try { return WindowsOpenChild(parent, name, true, true, true, false, deletion, allowReparse); }
                catch (IOException collision) when (IsCollision(collision))
                { return WindowsOpenChild(parent, name, true, false, false, false, deletion, allowReparse); }
            }
        }
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = nameBuffer
            }, unicodeBuffer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.Handle.DangerousGetHandle(),
                ObjectName = unicodeBuffer, Attributes = allowReparse ? 0x40u : 0x1040u // OBJ_DONT_REPARSE
            };
            var access = directory ? 0x1000a1u : writable ? 0xc0100000u : 0x80100000u;
            if (deletion || create && (directory || exclusive)) access |= 0x10000;
            if (!directory && create && exclusive) access |= 0x40000; // Preserve a replaced target's DACL on this owned temporary.
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
                directory ? 0x10u : 0x80u, directory ? 1u : shareDelete ? 5u : writable ? 0u : 1u,
                create ? exclusive ? 2u : 3u : 1u, 0x00200020u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (status < 0) { handle?.Dispose(); throw NativeError((int)RtlNtStatusToDosError(status)); }
            return WindowsNode(handle, parent, name, directory, allowReparse);
        }
        finally { Marshal.FreeHGlobal(unicodeBuffer); Marshal.FreeHGlobal(nameBuffer); }
    }

    private static Node WindowsNode(SafeFileHandle handle, Node? parent, string name, bool directory, bool allowReparse)
    {
        try
        {
            if (!GetFileInformationByHandle(handle, out var information)) throw NativeError(Marshal.GetLastPInvokeError());
            if ((information.Attributes & 0x400) != 0 && !allowReparse ||
                ((information.Attributes & 0x10) != 0) != directory)
                throw new IOException("The opened filesystem object is a link or has an unexpected type.");
            return new Node(handle, parent, name, directory,
                new FileIdentity(information.Volume, ((ulong)information.IndexHigh << 32) | information.IndexLow));
        }
        catch { handle.Dispose(); throw; }
    }

    private static IReadOnlyList<Entry> NativeEnumerate(Node node)
    {
        if (!OperatingSystem.IsWindows()) return UnixEnumerate(node);
        var entries = new List<Entry>();
        const int capacity = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleEx(node.Handle, restart ? 11 : 10, buffer, capacity))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 18) break;
                    throw NativeError(error);
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > capacity - 104) throw new IOException("Invalid directory enumeration response.");
                    var item = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(item, 0);
                    var attributes = Marshal.ReadInt32(item, 56);
                    var length = Marshal.ReadInt32(item, 60);
                    if (length < 0 || (length & 1) != 0 || length > capacity - offset - 104)
                        throw new IOException("Invalid directory entry name length.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(item, 104), length / 2)!;
                    if (name is not ("." or ".."))
                    {
                        ValidateName(name);
                        if (entries.Count >= 10_000) throw new IOException("The directory enumeration exceeds its admitted entry bound.");
                        entries.Add(new(name, (attributes & 0x10) != 0, (attributes & 0x400) != 0,
                            new FileIdentity(node.Identity.Device, (ulong)Marshal.ReadInt64(item, 96))));
                    }
                    if (next == 0) break;
                    if (next < 104) throw new IOException("Invalid directory entry continuation.");
                    offset = checked(offset + next);
                }
            }
            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void NativeRename(Node node, string target, bool replace)
    {
        if (!OperatingSystem.IsWindows()) { UnixRename(node, target, replace); return; }
        ValidateName(target);
        var parent = node.Parent ?? throw new IOException("A filesystem root cannot be renamed.");
        var bytes = System.Text.Encoding.Unicode.GetBytes(target);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var size = checked(nameOffset + bytes.Length);
        var buffer = Marshal.AllocHGlobal(size);
        Node? replaced = null;
        Node? replacementProof = null;
        try
        {
            if (replace)
            {
                try { replaced = WindowsOpenChild(parent, target, false, false, false, false, false, false, shareDelete: true); }
                catch (IOException error) when (IsMissing(error)) { }
                catch (IOException error) { error.Data["RootedIoStage"] = "open_replace_target"; throw; }
                if (replaced is not null)
                {
                    WindowsPreserveReplacementMetadata(node, replaced);
                    Observe("replacement_metadata_pinned", replaced.Path);
                    // READ|DELETE sharing (never WRITE) is required by the qualified Ex
                    // replacement; a no-DELETE pin blocks our own replacement as well.
                    // Reject a changed no-follow name after metadata copying/observation.
                    // This preserves directory confinement and detects ordinary conflicts;
                    // it is not atomic same-name CAS against malicious same-owner native
                    // renames in the final check-to-rename interval, nor a tenant boundary.
                    try
                    {
                        replacementProof = WindowsOpenChild(parent, target, false, false, false, false, false, false, shareDelete: true);
                        if (replacementProof.Identity != replaced.Identity)
                        {
                            var conflict = new IOException("The admitted replacement target changed before publication.");
                            conflict.Data["RootedIoStage"] = "replace_target_changed";
                            throw conflict;
                        }
                    }
                    catch (IOException error)
                    {
                        if (!error.Data.Contains("RootedIoStage")) error.Data["RootedIoStage"] = "replace_revalidate_target";
                        throw;
                    }
                }
            }
            Marshal.Copy(new byte[size], 0, buffer, size);
            // Classic replace refuses an open target. Ex + POSIX replacement keeps the
            // admitted target handle alive through the atomic namespace change; its
            // metadata pin is never dropped to make a path-based retry possible.
            var pinnedReplacement = replaced is not null;
            if (pinnedReplacement) Marshal.WriteInt32(buffer, 3); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
            else Marshal.WriteByte(buffer, replace ? (byte)1 : (byte)0);
            // A simple name with NULL RootDirectory renames inside the source handle's
            // existing parent. Our API never moves across parents, and retains all of
            // them without write/delete sharing. Supplying that parent as RootDirectory
            // instead makes IopOpenLinkOrRenameTarget reopen it for FILE_WRITE_DATA,
            // conflicting with the very pin that prevents replacement/reparse swaps.
            // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
            var status = NtSetInformationFile(node.Handle, out _, buffer, (uint)size, pinnedReplacement ? 65 : 10);
            if (status < 0) throw NativeError((int)RtlNtStatusToDosError(status), pinnedReplacement ? "rename_replace_pinned" : "rename_same_parent");
            node.Name = target; node.Published = true;
        }
        finally { replacementProof?.Release(); replaced?.Release(); Marshal.FreeHGlobal(buffer); }
    }

    private static void WindowsPreserveReplacementMetadata(Node temporary, Node replaced)
    {
        if (!GetFileInformationByHandle(replaced.Handle, out var original) ||
            !GetFileInformationByHandle(temporary.Handle, out var created)) throw NativeError(Marshal.GetLastPInvokeError(), "replace_read_metadata");
        if ((original.Attributes & 1) != 0) throw new UnauthorizedAccessException("The existing document is read-only.");
        if ((original.Attributes & 0x4800) != (created.Attributes & 0x4800))
            throw new NotSupportedException("Atomic replacement cannot downgrade an encrypted or compressed document.");
        if (!GetVolumeInformationByHandleW(temporary.Handle, IntPtr.Zero, 0, out _, out _, out var features, IntPtr.Zero, 0))
            throw NativeError(Marshal.GetLastPInvokeError(), "replace_volume_features");
        if ((features & 8) != 0) // FILE_PERSISTENT_ACLS; FAT/exFAT have no DACL to carry forward.
        {
            var error = GetSecurityInfo(replaced.Handle, 1, 4, out _, out _, out var dacl, out _, out var descriptor);
            if (error != 0) throw NativeError((int)error, "replace_read_dacl");
            try
            {
                if (!GetSecurityDescriptorControl(descriptor, out var control, out _)) throw NativeError(Marshal.GetLastPInvokeError(), "replace_read_dacl_control");
                error = SetSecurityInfo(temporary.Handle, 1, 4u | ((control & 0x1000) != 0 ? 0x80000000u : 0x20000000u),
                    IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
                if (error != 0) throw NativeError((int)error, "replace_write_dacl");
            }
            finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
        }
        var basic = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(new byte[40], 0, basic, 40);
            Marshal.WriteInt64(basic, ((long)(uint)original.Creation.dwHighDateTime << 32) | (uint)original.Creation.dwLowDateTime);
            Marshal.WriteInt32(basic, 32, (int)(original.Attributes & 0x31a6));
            var status = NtSetInformationFile(temporary.Handle, out _, basic, 40, 4);
            if (status < 0) throw NativeError((int)RtlNtStatusToDosError(status), "replace_write_basic_metadata");
        }
        finally { Marshal.FreeHGlobal(basic); }
    }

    private static void NativeDelete(Node node)
    {
        if (!OperatingSystem.IsWindows()) { UnixDelete(node); return; }
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            var status = NtSetInformationFile(node.Handle, out _, buffer, 1, 13);
            if (status < 0) throw NativeError((int)RtlNtStatusToDosError(status));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void NativeDeleteLink(Node parent, Entry entry)
    {
        if (!OperatingSystem.IsWindows()) { UnixDeleteLink(parent, entry); return; }
        var node = WindowsOpenChild(parent, entry.Name, entry.Directory, false, false, false, true, true);
        try
        {
            if (node.Identity != entry.Identity) throw new IOException("The cleanup link identity changed.");
            NativeDelete(node);
        }
        finally { node.Release(); }
    }

    private static void NativeSetMode(Node node, UnixFileMode mode)
    { if (!OperatingSystem.IsWindows()) UnixSetMode(node, mode); }
    private static UnixFileMode NativeGetMode(Node node) => !OperatingSystem.IsWindows()
        ? UnixGetMode(node) : throw new PlatformNotSupportedException("Unix modes do not describe Windows files.");
    private static void NativeRequireBinding(Node node)
    { if (!OperatingSystem.IsWindows()) UnixRequireBinding(node); }

    private static SafeFileHandle NativeDuplicate(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return UnixDuplicate(handle);
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, handle, process, out var duplicate, 0, false, 2)) throw NativeError(Marshal.GetLastPInvokeError());
        return duplicate;
    }

    private static IOException NativeError(int error, string? stage = null)
    {
        IOException failure = error is 2 or 3
            ? new FileNotFoundException("The admitted filesystem entry is absent.")
            : new IOException("The handle-relative filesystem operation was refused (" + error + ").", unchecked((int)0x80070000) | error);
        if (stage is not null) failure.Data["RootedIoStage"] = stage;
        return failure;
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    { internal int Length; internal IntPtr RootDirectory, ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowsInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, int length);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, IntPtr volumeName, int volumeNameLength,
        out uint serial, out uint maximumComponentLength, out uint flags, IntPtr fileSystemName, int fileSystemNameLength);
}
