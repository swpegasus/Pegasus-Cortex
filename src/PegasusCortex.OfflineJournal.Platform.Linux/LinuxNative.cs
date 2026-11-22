using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

/// <summary>
/// Narrow Linux ABI surface used by the journal boundary. There are intentionally no path-based
/// managed fallbacks: an unavailable safety primitive is a startup/operation failure.
/// </summary>
internal static class LinuxNative
{
    private const string LibC = "libc";
    private const long OpenAt2SystemCall = 437;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x07ff;
    private const uint StatxMountId = 0x1000;
    private const int FDuplicateCloseOnExec = 1030;
    private const int MinimumOwnedFileDescriptor = 3;
    private const int PosixFadviseSequential = 2;
    private const int Interrupted = 4;
    private const int TryAgain = 11;
    private const int MaximumResolutionRetries = 3;

    internal const int AtCurrentWorkingDirectory = -100;
    internal const int AtRemoveDirectory = 0x200;
    internal const int LockExclusive = 2;
    internal const int LockNonBlocking = 4;

    internal const ulong OpenReadOnly = 0;
    internal const ulong OpenWriteOnly = 1;
    internal const ulong OpenReadWrite = 2;
    internal const ulong OpenCreate = 0x40;
    internal const ulong OpenExclusive = 0x80;
    internal const ulong OpenNoControllingTerminal = 0x100;
    internal const ulong OpenNonBlocking = 0x800;
    internal const ulong OpenCloseOnExec = 0x80000;
    internal const ulong OpenPath = 0x200000;

    // ARM64 overrides the asm-generic O_DIRECTORY and O_NOFOLLOW values for AArch32
    // compatibility; x64 uses the asm-generic defaults. Keeping both here prevents callers from
    // composing an otherwise valid openat2 request with flags the active kernel reads differently.
    internal static ulong OpenDirectory => GetArchitectureSpecificOpenFlag(
        arm64Value: 0x4000,
        x64Value: 0x10000);

    internal static ulong OpenNoFollow => GetArchitectureSpecificOpenFlag(
        arm64Value: 0x8000,
        x64Value: 0x20000);

    internal const ulong ResolveNoCrossDevice = 0x01;
    internal const ulong ResolveNoMagicLinks = 0x02;
    internal const ulong ResolveNoSymbolicLinks = 0x04;
    internal const ulong ResolveBeneath = 0x08;

    internal const uint RenameNoReplace = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static SafeFileHandle OpenAt2(
        int directoryFileDescriptor,
        string path,
        ulong flags,
        uint mode,
        ulong resolve,
        string operation)
    {
        var resolutionRetries = 0;
        while (true)
        {
            var how = new OpenHow { Flags = flags, Mode = mode, Resolve = resolve };
            long result;
            try
            {
                result = SyscallOpenAt2(
                    OpenAt2SystemCall,
                    directoryFileDescriptor,
                    path,
                    ref how,
                    (nuint)Marshal.SizeOf<OpenHow>());
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }

            if (result >= 0)
                return NormalizeOpenedDescriptor(result, operation);
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Interrupted)
                continue;
            if (errno == TryAgain && resolutionRetries++ < MaximumResolutionRetries)
                continue;
            throw LinuxJournalFileSystemException.FromErrno(operation, errno);
        }
    }

    internal static SafeFileHandle? TryOpenAt2(
        int directoryFileDescriptor,
        string path,
        ulong flags,
        uint mode,
        ulong resolve,
        string operation,
        out int errno)
    {
        var resolutionRetries = 0;
        while (true)
        {
            var how = new OpenHow { Flags = flags, Mode = mode, Resolve = resolve };
            long result;
            try
            {
                result = SyscallOpenAt2(
                    OpenAt2SystemCall,
                    directoryFileDescriptor,
                    path,
                    ref how,
                    (nuint)Marshal.SizeOf<OpenHow>());
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }

            if (result >= 0)
            {
                errno = 0;
                return NormalizeOpenedDescriptor(result, operation);
            }
            errno = Marshal.GetLastPInvokeError();
            if (errno == Interrupted)
                continue;
            if (errno == TryAgain && resolutionRetries++ < MaximumResolutionRetries)
                continue;
            return null;
        }
    }

    internal static SafeFileHandle Duplicate(SafeFileHandle handle, string operation)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, handle);
        var referenceAdded = false;
        try
        {
            handle.DangerousAddRef(ref referenceAdded);
            var fileDescriptor = checked((int)handle.DangerousGetHandle());
            return DuplicateCloseOnExecRaw(fileDescriptor, operation);
        }
        finally
        {
            if (referenceAdded)
                handle.DangerousRelease();
        }
    }

    internal static LinuxFileMetadata StatFileDescriptor(SafeFileHandle handle, string operation)
    {
        StatxBuffer buffer;
        int result;
        do
        {
            try
            {
                result = checked((int)SyscallStatx(
                    GetStatxSystemCallNumber(),
                    GetFileDescriptor(handle),
                    string.Empty,
                    AtEmptyPath | AtSymlinkNoFollow,
                    StatxBasicStats | StatxMountId,
                    out buffer));
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());

        const uint requiredMask = 0x000f | 0x0100 | StatxMountId;
        if ((buffer.Mask & requiredMask) != requiredMask)
        {
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.SystemCallUnavailable);
        }
        return new LinuxFileMetadata(
            buffer.Mode,
            buffer.LinkCount,
            buffer.UserId,
            buffer.GroupId,
            buffer.Inode,
            buffer.DeviceMajor,
            buffer.DeviceMinor,
            buffer.MountId);
    }

    internal static uint GetEffectiveUserId(string operation)
    {
        try
        {
            return GetEffectiveUserIdNative();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw LinuxJournalFileSystemException.Unavailable(operation, exception);
        }
    }

    internal static void CreateDirectoryAt(
        SafeFileHandle parent,
        string leafName,
        uint mode,
        string operation)
    {
        int result;
        do
        {
            try
            {
                result = MkdirAt(GetFileDescriptor(parent), leafName, mode);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    internal static void RenameAtNoReplace(
        SafeFileHandle sourceParent,
        string sourceLeaf,
        SafeFileHandle destinationParent,
        string destinationLeaf,
        string operation)
    {
        int result;
        do
        {
            try
            {
                result = checked((int)SyscallRenameAt2(
                    GetRenameAt2SystemCallNumber(),
                    GetFileDescriptor(sourceParent),
                    sourceLeaf,
                    GetFileDescriptor(destinationParent),
                    destinationLeaf,
                    RenameNoReplace));
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    internal static void UnlinkAt(
        SafeFileHandle parent,
        string leafName,
        int flags,
        string operation)
    {
        int result;
        do
        {
            try
            {
                result = UnlinkAtNative(GetFileDescriptor(parent), leafName, flags);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    internal static void FlushDirectory(SafeFileHandle directory, string operation)
    {
        int result;
        do
        {
            try
            {
                result = Fsync(GetFileDescriptor(directory));
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    internal static void AcquireExclusiveNonBlockingLock(SafeFileHandle file, string operation)
    {
        int result;
        do
        {
            try
            {
                result = Flock(GetFileDescriptor(file), LockExclusive | LockNonBlocking);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    internal static void AdviseSequential(SafeFileHandle file, string operation)
    {
        int result;
        try
        {
            result = PosixFadvise(GetFileDescriptor(file), 0, 0, PosixFadviseSequential);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw LinuxJournalFileSystemException.Unavailable(operation, exception);
        }
        if (result != 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, result);
    }

    internal static IReadOnlyList<string> EnumerateNames(
        SafeFileHandle directory,
        int maximumEntries,
        string operation)
    {
        // Allocate before fdopendir takes ownership so even an allocation failure cannot strand
        // the duplicated native descriptor outside a SafeHandle or DIR* cleanup path.
        var names = new List<string>(Math.Min(maximumEntries, 256));
        using var duplicate = Duplicate(directory, operation);
        nint directoryStream;
        try
        {
            directoryStream = FileDescriptorOpenDirectory(GetFileDescriptor(duplicate));
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw LinuxJournalFileSystemException.Unavailable(operation, exception);
        }
        if (directoryStream == 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());

        // fdopendir owns the duplicated descriptor after a successful call.
        duplicate.SetHandleAsInvalid();
        try
        {
            while (true)
            {
                nint entry;
                try
                {
                    var errorLocation = ErrorNumberLocation();
                    Marshal.WriteInt32(errorLocation, 0);
                    entry = ReadDirectory(directoryStream);
                    if (entry == 0)
                    {
                        var errno = Marshal.ReadInt32(errorLocation);
                        if (errno == Interrupted)
                            continue;
                        if (errno != 0)
                            throw LinuxJournalFileSystemException.FromErrno(operation, errno);
                        break;
                    }
                }
                catch (LinuxJournalFileSystemException)
                {
                    throw;
                }
                catch (Exception exception) when (IsUnavailable(exception))
                {
                    throw LinuxJournalFileSystemException.Unavailable(operation, exception);
                }

                var name = ReadDirectoryEntryName(entry, operation);
                if (name is "." or "..")
                    continue;
                if (names.Count == maximumEntries)
                {
                    throw new LinuxJournalFileSystemException(
                        operation,
                        LinuxJournalIoCause.EnumerationLimitExceeded);
                }
                names.Add(name);
            }
        }
        catch
        {
            CloseDirectoryIgnoringSecondaryFailure(directoryStream);
            throw;
        }

        CloseDirectory(directoryStream, operation);
        return names;
    }

    internal static int GetFileDescriptor(SafeFileHandle handle)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, handle);
        return checked((int)handle.DangerousGetHandle());
    }

    private static SafeFileHandle NormalizeOpenedDescriptor(long rawResult, string operation)
    {
        if (rawResult is < 0 or > int.MaxValue)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.ResourceLimit);
        var rawFileDescriptor = (int)rawResult;
        if (rawFileDescriptor >= MinimumOwnedFileDescriptor)
            return new SafeFileHandle((nint)rawFileDescriptor, ownsHandle: true);

        SafeFileHandle duplicated;
        try
        {
            duplicated = DuplicateCloseOnExecRaw(rawFileDescriptor, operation);
        }
        catch
        {
            CloseRawDescriptorIgnoringSecondaryFailure(rawFileDescriptor);
            throw;
        }

        // On Linux close releases the descriptor even when interrupted; retrying could close a
        // descriptor another thread has since acquired. Call it exactly once.
        int closeResult;
        try
        {
            closeResult = Close(rawFileDescriptor);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            duplicated.Dispose();
            throw LinuxJournalFileSystemException.Unavailable(operation, exception);
        }
        var closeErrno = closeResult < 0 ? Marshal.GetLastPInvokeError() : 0;
        if (closeResult < 0)
        {
            duplicated.Dispose();
            throw LinuxJournalFileSystemException.FromErrno("close_low_file_descriptor", closeErrno);
        }
        return duplicated;
    }

    private static SafeFileHandle DuplicateCloseOnExecRaw(int fileDescriptor, string operation)
    {
        int duplicated;
        while (true)
        {
            try
            {
                // libc fcntl is variadic. Calling the kernel boundary directly keeps the third
                // argument pointer-width and deterministic on both supported Linux architectures.
                duplicated = SyscallFcntl(
                    GetFcntlSystemCallNumber(),
                    fileDescriptor,
                    FDuplicateCloseOnExec,
                    (nint)MinimumOwnedFileDescriptor);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                throw LinuxJournalFileSystemException.Unavailable(operation, exception);
            }

            if (duplicated >= 0)
                break;
            var errno = Marshal.GetLastPInvokeError();
            if (errno != Interrupted)
                throw LinuxJournalFileSystemException.FromErrno(operation, errno);
        }

        if (duplicated < MinimumOwnedFileDescriptor)
        {
            if (duplicated != fileDescriptor)
                CloseRawDescriptorIgnoringSecondaryFailure(duplicated);
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.ResourceLimit);
        }

        try
        {
            return new SafeFileHandle((nint)duplicated, ownsHandle: true);
        }
        catch
        {
            CloseRawDescriptorIgnoringSecondaryFailure(duplicated);
            throw;
        }
    }

    private static void CloseRawDescriptorIgnoringSecondaryFailure(int fileDescriptor)
    {
        try
        {
            _ = Close(fileDescriptor);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // A primary normalization error already carries the actionable failure context.
        }
    }

    private static string ReadDirectoryEntryName(nint entry, string operation)
    {
        const int recordLengthOffset = 16;
        const int nameOffset = 19;
        var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, recordLengthOffset));
        var available = recordLength - nameOffset;
        if (available is < 1 or > 261)
        {
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.InvalidEntryType);
        }
        // Linux dirent records are aligned, so up to five bytes may follow the 256-byte
        // d_name area. Never read those padding bytes as part of the name.
        var bytes = new byte[Math.Min(available, 256)];
        Marshal.Copy(entry + nameOffset, bytes, 0, bytes.Length);
        var terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator < 1)
        {
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.InvalidEntryType);
        }
        try
        {
            return StrictUtf8.GetString(bytes, 0, terminator);
        }
        catch (DecoderFallbackException exception)
        {
            _ = exception;
            // Decoder exceptions can reveal raw filename bytes and indices. Keep them outside the
            // public exception graph while retaining the stable operation/cause diagnostic.
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.InvalidRelativePath);
        }
    }

    private static void CloseDirectory(nint directoryStream, string operation)
    {
        int result;
        try
        {
            result = CloseDirectoryNative(directoryStream);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw LinuxJournalFileSystemException.Unavailable(operation, exception);
        }
        if (result < 0)
            throw LinuxJournalFileSystemException.FromErrno(operation, Marshal.GetLastPInvokeError());
    }

    private static void CloseDirectoryIgnoringSecondaryFailure(nint directoryStream)
    {
        try
        {
            _ = CloseDirectoryNative(directoryStream);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // A primary enumeration failure is already propagating. Do not replace its context.
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static ulong GetArchitectureSpecificOpenFlag(ulong arm64Value, ulong x64Value) =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => arm64Value,
            Architecture.X64 => x64Value,
            _ => throw new LinuxJournalFileSystemException(
                "resolve_open_flags",
                LinuxJournalIoCause.PlatformUnsupported)
        };

    private static long GetStatxSystemCallNumber() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 291,
        Architecture.X64 => 332,
        _ => throw new LinuxJournalFileSystemException(
            "statx",
            LinuxJournalIoCause.PlatformUnsupported)
    };

    private static long GetRenameAt2SystemCallNumber() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 276,
        Architecture.X64 => 316,
        _ => throw new LinuxJournalFileSystemException(
            "renameat2",
            LinuxJournalIoCause.PlatformUnsupported)
    };

    private static long GetFcntlSystemCallNumber() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 25,
        Architecture.X64 => 72,
        _ => throw new LinuxJournalFileSystemException(
            "fcntl_dupfd_cloexec",
            LinuxJournalIoCause.PlatformUnsupported)
    };

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpenAt2(
        long systemCallNumber,
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref OpenHow how,
        nuint size);

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallStatx(
        long systemCallNumber,
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out StatxBuffer buffer);

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallRenameAt2(
        long systemCallNumber,
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern int SyscallFcntl(
        long systemCallNumber,
        int fileDescriptor,
        int command,
        nint argument);

    [DllImport(LibC, EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserIdNative();

    [DllImport(LibC, EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport(LibC, EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAtNative(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport(LibC, EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "posix_fadvise")]
    private static extern int PosixFadvise(int fileDescriptor, long offset, long length, int advice);

    [DllImport(LibC, EntryPoint = "fdopendir", SetLastError = true)]
    private static extern nint FileDescriptorOpenDirectory(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "readdir", SetLastError = true)]
    private static extern nint ReadDirectory(nint directoryStream);

    [DllImport(LibC, EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectoryNative(nint directoryStream);

    [DllImport(LibC, EntryPoint = "__errno_location")]
    private static extern nint ErrorNumberLocation();

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint RawDeviceMajor;
        public uint RawDeviceMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare1;
        public ulong Spare2;
        public ulong Spare3;
        public ulong Spare4;
        public ulong Spare5;
        public ulong Spare6;
        public ulong Spare7;
        public ulong Spare8;
        public ulong Spare9;
        public ulong Spare10;
        public ulong Spare11;
        public ulong Spare12;
    }
}

internal readonly record struct LinuxFileMetadata(
    ushort Mode,
    uint LinkCount,
    uint UserId,
    uint GroupId,
    ulong Inode,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong MountId);
