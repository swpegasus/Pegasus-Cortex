using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Diagnostics.InheritanceImpact;

// HELD source proposal. No entry point, product integration, setters or writers.
// The public surface refuses execution pending Root/peer review. The private
// algorithm is included for review; its 15-second budget is cooperative only.
public static class AssessInheritanceImpact
{
    public const int MaximumEncounteredEntries = 8192;
    public const int MaximumQueuedDirectories = 512;
    public const int MaximumDepth = 32;
    public const int MaximumTotalSeconds = 15;
    private const int ExpectedSession = 1;
    private const string OwnerSidSha256 =
        "6abece5f532014d46cf1fc283a9b35ff064cb8af4714d77c79c6d998e7eafd90";
    private const string FixedUserParent = @"C:\Program Files\Pegasus\Cortex\state\users";
    private const int AllTargets = 15;

    public static AssessmentResult Assess() =>
        new(false, AssessmentReason.HeldForRootReview, new());

    // Deliberately unreachable from Assess(). Stopwatch checks cannot interrupt
    // a synchronous kernel call. Do not expose this core as an authorized runner.
    private static AssessmentResult ProposedCoreForReview()
    {
        var c = new AssessmentCounts();
        var clock = Stopwatch.StartNew();
        var pending = new Queue<DirectoryFrame>();
        var seen = new HashSet<FileIdentity>();
        var targetMask = 0;
        try
        {
            if (!OperatingSystem.IsWindows()) Stop(AssessmentReason.UnsupportedPlatform);
            using var caller = WindowsIdentity.GetCurrent();
            using var callerProcess = Process.GetCurrentProcess();
            var sid = caller.User?.Value;
            if (sid is null ||
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant() != OwnerSidSha256 ||
                callerProcess.SessionId != ExpectedSession ||
                new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
                Stop(AssessmentReason.CallerBindingMismatch);

            Budget(clock);
            var root = FixedUserParent + "\\" + sid;
            c.EncounteredEntriesCharged = 1; // Root also consumes the total budget.
            var rootHandle = Native.OpenRoot(root);
            try
            {
                Budget(clock);
                var physicalRoot = Native.FinalPath(rootHandle);
                Native.RequireExpectedRoot(physicalRoot, root);
                Budget(clock);
                Native.RequireNtfs(rootHandle);
                Budget(clock);
                var rootState = Observe(rootHandle, sid, physicalRoot, null, null, clock);
                if (!rootState.Identity.Directory) Stop(AssessmentReason.ObjectKindChanged);
                seen.Add(rootState.Identity);
                Count(rootState, c);
                pending.Enqueue(new(rootHandle, rootState, physicalRoot, 0, root.Length, 1));
                c.PeakQueuedDirectories = 1;
                rootHandle = null!;
            }
            finally { rootHandle?.Dispose(); }

            while (pending.Count != 0)
            {
                Budget(clock);
                using var frame = pending.Dequeue();
                Revalidate(frame, sid, clock);
                targetMask |= frame.Target;
                c.StartupTargetsObserved = PopCount(targetMask);
                var first = true;
                while (true)
                {
                    Budget(clock);
                    // Reserve the largest possible native batch BEFORE fetching.
                    // All returned entries are charged, including dot entries and
                    // entries later rejected or never retained in the queue.
                    if (MaximumEncounteredEntries - c.EncounteredEntriesCharged < Native.MaximumBatchEntries)
                        Stop(AssessmentReason.EncounteredEntryLimit);
                    c.EncounteredEntriesCharged += Native.MaximumBatchEntries;
                    var batch = Native.ReadDirectoryBatch(frame.Handle, first);
                    first = false;
                    if (batch is null)
                    {
                        c.EncounteredEntriesCharged -= Native.MaximumBatchEntries;
                        break;
                    }
                    var offsets = Native.ValidateBatch(batch);
                    c.EncounteredEntriesCharged -= Native.MaximumBatchEntries - offsets.Count;
                    Budget(clock);
                    foreach (var offset in offsets)
                    {
                        Budget(clock);
                        var entry = Native.DecodeEntry(batch, offset);
                        if (entry.Name is "." or "..") { c.DotEntries++; continue; }
                        Native.RequireSingleSegment(entry.Name);
                        var depth = frame.Depth + 1;
                        var logicalLength = frame.LogicalLength + 1 + entry.Name.Length;
                        if (depth > MaximumDepth) Stop(AssessmentReason.DepthLimit);
                        if (logicalLength > 240) Stop(AssessmentReason.PathBudgetUnsupported);
                        c.DeepestObserved = Math.Max(c.DeepestObserved, depth);
                        if (entry.Directory && pending.Count >= MaximumQueuedDirectories)
                            Stop(AssessmentReason.QueuedDirectoryLimit);

                        SafeFileHandle? child = Native.OpenChild(frame.Handle, entry);
                        try
                        {
                            var physical = frame.PhysicalPath + "\\" + entry.Name; // Transient only.
                            var state = Observe(child, sid, physical, entry.FileId, entry.CreationTime, clock);
                            if (state.Identity.Volume != frame.State.Identity.Volume ||
                                state.Identity.Directory != entry.Directory)
                                Stop(AssessmentReason.IdentityDrift);
                            if (!seen.Add(state.Identity)) Stop(AssessmentReason.IdentityAlias);
                            Count(state, c);
                            var target = frame.Depth == 0 ? entry.Name switch
                            {
                                "execution" => 2, "logs" => 4, "tray" => 8, _ => 0
                            } : 0;
                            if (target != 0 && !state.Identity.Directory)
                                Stop(AssessmentReason.ObjectKindChanged);
                            targetMask |= target;
                            c.StartupTargetsObserved = PopCount(targetMask);
                            if (!state.Identity.Directory) continue;
                            if (state.Protected && target == 0)
                            {
                                c.ProtectedDirectoryBoundaries++;
                                continue;
                            }
                            // Both checks precede retention/enqueue.
                            Budget(clock);
                            if (pending.Count >= MaximumQueuedDirectories)
                                Stop(AssessmentReason.QueuedDirectoryLimit);
                            pending.Enqueue(new(child, state, physical, depth, logicalLength, target));
                            c.PeakQueuedDirectories = Math.Max(c.PeakQueuedDirectories, pending.Count);
                            child = null;
                        }
                        finally { child?.Dispose(); }
                    }
                }
                Revalidate(frame, sid, clock);
            }
            Budget(clock);
            if (targetMask != AllTargets) Stop(AssessmentReason.StartupTargetMissing);
            return new(true, AssessmentReason.CompleteObservedTraversal, c);
        }
        catch (StopAssessment stop) { return new(false, stop.Reason, c); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or
                                     PlatformNotSupportedException or MarshalDirectiveException)
        { return new(false, AssessmentReason.UnsupportedNativeContract, c); }
        catch (Exception)
        {
            // No native error text, private path, exception message or content
            // escapes. An unexpected source fault can never become Complete.
            return new(false, AssessmentReason.UnqualifiedSourceFault, c);
        }
        finally
        {
            while (pending.TryDequeue(out var frame)) frame.Dispose();
            clock.Stop();
            c.ElapsedMilliseconds = clock.ElapsedMilliseconds;
        }
    }

    private static Observation Observe(SafeFileHandle handle, string owner, string physical,
        ulong? expectedId, long? expectedCreation, Stopwatch clock)
    {
        Budget(clock);
        var info = Native.Information(handle);
        Budget(clock);
        if ((info.Attributes & (0x400u | 0x40u)) != 0) Stop(AssessmentReason.ReparseOrDevice);
        var identity = new FileIdentity(info.Volume, info.FileId, info.CreationTime, (info.Attributes & 0x10) != 0);
        if (info.FileId == 0 || expectedId is { } id && id != info.FileId ||
            expectedCreation is { } created && created != info.CreationTime)
            Stop(AssessmentReason.IdentityDrift);
        if (!identity.Directory && info.Links != 1) Stop(AssessmentReason.IdentityAlias);
        if (!string.Equals(Native.FinalPath(handle), physical, StringComparison.OrdinalIgnoreCase))
            Stop(AssessmentReason.FinalPathMismatch);
        Budget(clock);
        Native.RequireOwner(handle, owner); // Owner-only query before DACL use.
        Budget(clock);
        var security = Native.Security(handle, owner);
        Budget(clock);
        var after = Native.Information(handle);
        if (info != after) Stop(AssessmentReason.IdentityDrift);
        return new(identity, info.LastWriteTime, security.Protected, security.Inherited,
            security.Explicit, security.ForeignAllow, security.Deny, security.Hash);
    }

    private static void Revalidate(DirectoryFrame frame, string owner, Stopwatch clock)
    {
        var now = Observe(frame.Handle, owner, frame.PhysicalPath,
            frame.State.Identity.FileId, frame.State.Identity.CreationTime, clock);
        if (now.Identity != frame.State.Identity || now.LastWriteTime != frame.State.LastWriteTime ||
            !CryptographicOperations.FixedTimeEquals(now.SecurityHash, frame.State.SecurityHash))
            Stop(AssessmentReason.IdentityDrift);
    }

    private static void Count(Observation state, AssessmentCounts c)
    {
        c.AdmittedObjects++;
        if (state.Identity.Directory) c.Directories++; else c.Files++;
        if (state.Protected) c.ProtectedObjects++; else c.UnprotectedObjects++;
        if (state.Inherited) c.ObjectsWithInheritedAces++;
        if (state.Explicit) c.ObjectsWithExplicitAces++;
        c.ForeignAllowAceOccurrences += state.ForeignAllow;
        c.DenyAceOccurrences += state.Deny;
    }

    private static void Budget(Stopwatch clock)
    {
        if (clock.Elapsed >= TimeSpan.FromSeconds(MaximumTotalSeconds))
            Stop(AssessmentReason.DeadlineObserved);
    }
    private static int PopCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Stop(AssessmentReason reason) => throw new StopAssessment(reason);
    private sealed class StopAssessment(AssessmentReason reason) : Exception { public AssessmentReason Reason => reason; }
    private readonly record struct FileIdentity(uint Volume, ulong FileId, long CreationTime, bool Directory);
    private sealed record Observation(FileIdentity Identity, long LastWriteTime, bool Protected,
        bool Inherited, bool Explicit, int ForeignAllow, int Deny, byte[] SecurityHash);
    private sealed class DirectoryFrame(SafeFileHandle handle, Observation state, string physical,
        int depth, int logicalLength, int target) : IDisposable
    {
        internal SafeFileHandle Handle => handle;
        internal Observation State => state;
        internal string PhysicalPath => physical;
        internal int Depth => depth;
        internal int LogicalLength => logicalLength;
        internal int Target => target;
        public void Dispose() => handle.Dispose();
    }

    private static class Native
    {
        internal const int BufferBytes = 1024;
        private const int HeaderBytes = 104;
        internal const int MaximumBatchEntries = BufferBytes / (HeaderBytes + 2); // Conservative 9.
        private const uint MetadataAccess = 0x00020080; // READ_CONTROL | FILE_READ_ATTRIBUTES.
        private const uint Synchronize = 0x00100000;
        private const uint ListDirectory = 1;
        private const uint ShareAll = 7;
        private const int NoMoreFiles = 18;

        internal readonly record struct FileInfo(uint Attributes, uint Volume, ulong FileId,
            long CreationTime, long LastWriteTime, uint Links);
        internal sealed record Entry(string Name, bool Directory, ulong FileId, long CreationTime);
        internal sealed record SecurityInfo(bool Protected, bool Inherited, bool Explicit,
            int ForeignAllow, int Deny, byte[] Hash);

        internal static SafeFileHandle OpenRoot(string path)
        {
            var handle = CreateFileW(path, MetadataAccess | Synchronize | ListDirectory, ShareAll,
                0, 3, 0x02200000, 0); // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); Win32Failure(error); }
            return handle;
        }

        internal static SafeFileHandle OpenChild(SafeFileHandle parent, Entry entry)
        {
            var name = Marshal.StringToHGlobalUni(entry.Name);
            nint unicode = 0;
            var retained = false;
            try
            {
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                var length = checked((ushort)(entry.Name.Length * 2));
                Marshal.StructureToPtr(new UnicodeString { Length = length,
                    MaximumLength = checked((ushort)(length + 2)), Buffer = name }, unicode, false);
                parent.DangerousAddRef(ref retained);
                var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(), ObjectName = unicode, Attributes = 0x40 };
                // One component relative to an admitted retained directory; no
                // intermediate path traversal. Final reparse points are opened
                // as reparse points and then rejected before ACL use/descent.
                var status = NtOpenFile(out var raw, MetadataAccess | Synchronize |
                    (entry.Directory ? ListDirectory : 0), ref attributes, out _, ShareAll,
                    0x00200000 | 0x20u | (entry.Directory ? 1u : 0x40u));
                var handle = new SafeFileHandle(raw, ownsHandle: true);
                if (status != 0 || handle.IsInvalid)
                {
                    handle.Dispose();
                    Stop(status == unchecked((int)0xC0000022) ? AssessmentReason.AccessUnavailable :
                        AssessmentReason.UnsupportedOrChangedNativeOpen);
                }
                return handle;
            }
            finally
            {
                if (retained) parent.DangerousRelease();
                if (unicode != 0) Marshal.FreeHGlobal(unicode);
                Marshal.FreeHGlobal(name);
            }
        }

        internal static FileInfo Information(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var value)) Win32Failure(Marshal.GetLastWin32Error());
            return new(value.Attributes, value.Volume, ((ulong)value.IndexHigh << 32) | value.IndexLow,
                Time(value.CreationTime), Time(value.LastWriteTime), value.Links);
        }
        private static long Time(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
            unchecked((long)(((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime));

        internal static string FinalPath(SafeFileHandle handle)
        {
            var text = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 1);
            if (length == 0) Win32Failure(Marshal.GetLastWin32Error());
            if (length >= text.Capacity) Stop(AssessmentReason.UnsupportedNativeContract);
            return text.ToString().TrimEnd('\\');
        }

        internal static void RequireExpectedRoot(string physical, string logical)
        {
            const string prefix = @"\\?\Volume{";
            var end = physical.IndexOf("}\\", StringComparison.Ordinal);
            if (!physical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || end < prefix.Length ||
                !Guid.TryParseExact(physical[prefix.Length..end], "D", out _) ||
                !string.Equals(physical[(end + 1)..], logical[2..], StringComparison.OrdinalIgnoreCase))
                Stop(AssessmentReason.FinalPathMismatch);
        }

        internal static void RequireNtfs(SafeFileHandle handle)
        {
            var name = new StringBuilder(32);
            if (!GetVolumeInformationByHandleW(handle, 0, 0, out _, out _, out _, name, (uint)name.Capacity))
                Win32Failure(Marshal.GetLastWin32Error());
            if (!string.Equals(name.ToString(), "NTFS", StringComparison.Ordinal))
                Stop(AssessmentReason.UnsupportedFileSystem);
        }

        internal static void RequireOwner(SafeFileHandle handle, string expected)
        {
            var error = GetSecurityInfo(handle, 1, 1, out var owner, out _, out _, out _, out var descriptor);
            try
            {
                if (error != 0) Win32Failure((int)error);
                if (descriptor == 0 || owner == 0 || new SecurityIdentifier(owner).Value != expected)
                    Stop(AssessmentReason.OwnerCustodyUncertain);
            }
            finally { if (descriptor != 0) LocalFree(descriptor); }
        }

        internal static SecurityInfo Security(SafeFileHandle handle, string expected)
        {
            var error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
            try
            {
                if (error != 0) Win32Failure((int)error);
                if (descriptor == 0) Stop(AssessmentReason.UnsupportedSecurityMetadata);
                var length = GetSecurityDescriptorLength(descriptor);
                if (length is < 20 or > 65536) Stop(AssessmentReason.UnsupportedSecurityMetadata);
                var bytes = new byte[length];
                try
                {
                    Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                    var security = new RawSecurityDescriptor(bytes, 0);
                    if (security.Owner?.Value != expected) Stop(AssessmentReason.OwnerCustodyUncertain);
                    if (security.DiscretionaryAcl is null) Stop(AssessmentReason.UnsupportedSecurityMetadata);
                    var inherited = false; var explicitRule = false; var foreign = 0; var deny = 0;
                    foreach (GenericAce ace in security.DiscretionaryAcl)
                    {
                        if (ace is not CommonAce common || common.IsCallback)
                            throw new StopAssessment(AssessmentReason.UnsupportedSecurityMetadata);
                        if ((ace.AceFlags & AceFlags.Inherited) != 0) inherited = true; else explicitRule = true;
                        if (common.AceQualifier == AceQualifier.AccessDenied) deny++;
                        else if (common.AceQualifier == AceQualifier.AccessAllowed)
                        {
                            var sid = common.SecurityIdentifier.Value;
                            if (sid != expected && sid != "S-1-5-18" && sid != "S-1-5-32-544") foreign++;
                        }
                        else Stop(AssessmentReason.UnsupportedSecurityMetadata);
                    }
                    return new((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                        inherited, explicitRule, foreign, deny, SHA256.HashData(bytes));
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { if (descriptor != 0) LocalFree(descriptor); }
        }

        internal static byte[]? ReadDirectoryBatch(SafeFileHandle handle, bool restart)
        {
            var bytes = new byte[BufferBytes];
            if (GetFileInformationByHandleEx(handle, restart ? 11 : 10, bytes, (uint)bytes.Length)) return bytes;
            var error = Marshal.GetLastWin32Error();
            if (error == NoMoreFiles) return null;
            Win32Failure(error); return null;
        }

        internal static List<int> ValidateBatch(byte[] bytes)
        {
            var offsets = new List<int>(MaximumBatchEntries);
            var offset = 0;
            while (true)
            {
                if (offset < 0 || offset > bytes.Length - HeaderBytes ||
                    offsets.Count >= MaximumBatchEntries) Stop(AssessmentReason.UnsupportedNativeRecord);
                var next = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 60));
                if (nameBytes is < 2 or > 510 || (nameBytes & 1) != 0 ||
                    HeaderBytes + nameBytes > bytes.Length - offset)
                    Stop(AssessmentReason.UnsupportedNativeRecord);
                offsets.Add(offset);
                if (next == 0) return offsets;
                if ((next & 7) != 0 || next < HeaderBytes + nameBytes || next > bytes.Length - offset)
                    Stop(AssessmentReason.UnsupportedNativeRecord);
                offset += checked((int)next);
            }
        }

        internal static Entry DecodeEntry(byte[] bytes, int offset)
        {
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 60));
            var name = new UnicodeEncoding(false, false, true).GetString(bytes, offset + HeaderBytes, nameBytes);
            return new(name, (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 56)) & 0x10) != 0,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 96)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 8)));
        }

        internal static void RequireSingleSegment(string name)
        {
            if (name.Length is < 1 or > 255 || name.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
                name.EndsWith('.') || name.EndsWith(' ') || Encoding.UTF8.GetByteCount(name) > 255)
                Stop(AssessmentReason.UnsupportedNativeRecord);
        }

        private static void Win32Failure(int error) => Stop(error == 5 ? AssessmentReason.AccessUnavailable :
            error is 2 or 3 ? AssessmentReason.IdentityDrift : AssessmentReason.UnsupportedNativeContract);

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public uint Length; public nint RootDirectory, ObjectName; public uint Attributes;
            public nint SecurityDescriptor, SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock { public nint Status; public nuint Information; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtOpenFile(out nint handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, uint share, uint options);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInformation info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder text, uint count, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, nint label, uint labelCount,
            out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemCount);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] byte[] bytes, uint count);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint info,
            out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint GetSecurityDescriptorLength(nint descriptor);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint LocalFree(nint memory);
    }
}

public enum AssessmentReason
{
    HeldForRootReview, CompleteObservedTraversal, UnsupportedPlatform,
    CallerBindingMismatch, EncounteredEntryLimit, QueuedDirectoryLimit, DepthLimit,
    DeadlineObserved, AccessUnavailable, OwnerCustodyUncertain, IdentityDrift,
    IdentityAlias, FinalPathMismatch, ReparseOrDevice, ObjectKindChanged,
    PathBudgetUnsupported, UnsupportedFileSystem, UnsupportedOrChangedNativeOpen,
    UnsupportedNativeContract, UnsupportedNativeRecord, UnsupportedSecurityMetadata,
    StartupTargetMissing, UnqualifiedSourceFault
}

// Aggregate-only DTO: no root/relative path, SID, filename, ACL, SDDL, message,
// content, exception text or per-object identifier is exposed.
public sealed record AssessmentResult(bool Complete, AssessmentReason Reason, AssessmentCounts Counts);
public sealed class AssessmentCounts
{
    public int EncounteredEntriesCharged { get; internal set; }
    public int AdmittedObjects { get; internal set; }
    public int Directories { get; internal set; }
    public int Files { get; internal set; }
    public int DotEntries { get; internal set; }
    public int ProtectedObjects { get; internal set; }
    public int UnprotectedObjects { get; internal set; }
    public int ProtectedDirectoryBoundaries { get; internal set; }
    public int ObjectsWithInheritedAces { get; internal set; }
    public int ObjectsWithExplicitAces { get; internal set; }
    public int ForeignAllowAceOccurrences { get; internal set; }
    public int DenyAceOccurrences { get; internal set; }
    public int PeakQueuedDirectories { get; internal set; }
    public int DeepestObserved { get; internal set; }
    public int StartupTargetsObserved { get; internal set; }
    public long ElapsedMilliseconds { get; internal set; }
}
