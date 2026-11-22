using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Engine;

var tests = new (string Name, Action Run)[]
{
    ("exact_paths_and_relationships", ExactPathsAndRelationships),
    ("volume_root_rejected", VolumeRootRejected),
    ("workspace_root_authorization_hook", WorkspaceRootAuthorization),
    ("same_volume_exact_rename_and_replace", SameVolumeRenameAndReplace),
    ("collision_preserves_both_entries", CollisionPreservesEntries),
    ("simulated_cross_volume_tree_verified", SimulatedCrossVolumeMove),
    ("cross_volume_first_file_failure_is_partial", CrossVolumeFirstFileFailure),
    ("cross_volume_cleanup_partial_recovery", CrossVolumeCleanupFailure),
    ("recoverable_remove_same_volume", RecoverableRemove),
    ("permanent_remove_no_follow", PermanentRemove),
    ("locked_file_stops_without_broadening", LockedFile),
    ("reparse_leaf_and_ancestor_safety", ReparseSafety),
    ("cross_volume_reparse_admission_precedes_replace", CrossVolumeReparseAdmission)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static void ExactPathsAndRelationships()
{
    using var temp = new TempRoot();
    var operations = new MoveRemoveOperations();
    var source = temp.Dir("source");
    var child = Directory.CreateDirectory(Path.Combine(source, "child")).FullName;
    var marker = temp.File(Path.Combine("source", "marker.txt"), "safe");

    ThrowsCode("invalid", () => operations.Remove("", permanent: true));
    ThrowsCode("invalid", () => operations.Remove(Path.Combine(temp.Path, "*.txt"), permanent: true));
    ThrowsCode("precondition", () => operations.Move(source, source));
    ThrowsCode("precondition", () => operations.Move(source, Path.Combine(source, "new-child")));
    ThrowsCode("precondition", () => operations.Move(child, source, "replace"));
    Equal("safe", File.ReadAllText(marker));

    ThrowsCode("not_found", () => operations.Move(marker, Path.Combine(temp.Path, "missing-parent", "destination.txt")));
    Check(!Directory.Exists(Path.Combine(temp.Path, "missing-parent")), "failed move created an unreported destination parent");
    Equal("safe", File.ReadAllText(marker));
}

static void VolumeRootRejected()
{
    using var temp = new TempRoot();
    var volumeRoot = Path.GetPathRoot(temp.Path) ?? throw new InvalidOperationException("volume root unavailable");
    var operations = new MoveRemoveOperations();
    ThrowsCode("root_target", () => operations.Remove(volumeRoot, permanent: true));
    ThrowsCode(
        "authorization",
        () => operations.Remove(
            volumeRoot,
            permanent: true,
            new MoveRemoveOptions(AllowRootTarget: true, AuthorizeRootTarget: () => false)));

    var authorizationCalls = 0;
    ThrowsCode(
        "precondition",
        () => operations.Remove(
            volumeRoot,
            permanent: false,
            new MoveRemoveOptions(
                AllowRootTarget: true,
                AuthorizeRootTarget: () =>
                {
                    authorizationCalls++;
                    return true;
                })));
    Equal(0, authorizationCalls);
}

static void WorkspaceRootAuthorization()
{
    using var temp = new TempRoot();
    var workspace = temp.Dir("registered-workspace");
    temp.File(Path.Combine("registered-workspace", "marker.txt"), "workspace");
    var recovery = temp.Dir("recovery");
    var operations = new MoveRemoveOperations();

    ThrowsCode(
        "root_target",
        () => operations.Remove(
            workspace,
            options: new MoveRemoveOptions(RegisteredWorkspaceRoots: [workspace], RecoveryRoot: recovery)));
    ThrowsCode(
        "authorization",
        () => operations.Remove(
            workspace,
            options: new MoveRemoveOptions(
                [workspace],
                AllowRootTarget: true,
                AuthorizeRootTarget: () => false,
                RecoveryRoot: recovery)));

    var authorizationCalls = 0;
    var receipt = operations.Remove(
        workspace,
        options: new MoveRemoveOptions(
            [workspace],
            AllowRootTarget: true,
            AuthorizeRootTarget: () =>
            {
                authorizationCalls++;
                return true;
            },
            RecoveryRoot: recovery));
    Check(receipt.Ok, receipt.Error ?? "workspace recovery failed");
    Equal(1, authorizationCalls);
    Check(receipt.RecoveryPath is not null, "workspace recovery path missing");
    Equal("workspace", File.ReadAllText(Path.Combine(receipt.RecoveryPath!, "marker.txt")));
}

static void SameVolumeRenameAndReplace()
{
    using var temp = new TempRoot();
    var operations = new MoveRemoveOperations();
    var source = temp.File("agent's [Ω] & source.txt", "exact payload");
    var destination = Path.Combine(temp.Path, "agent's [Ω] & destination.txt");
    var receipt = operations.Move(source, destination);
    Check(receipt.Ok && !receipt.CrossVolume && receipt.SourceRemoved, receipt.Error ?? "rename failed");
    Check(!File.Exists(source), "same-volume source survived");
    Equal("exact payload", File.ReadAllText(destination));

    var replacementSource = temp.Dir("replacement-source");
    temp.File(Path.Combine("replacement-source", "new.txt"), "new");
    var replacementDestination = temp.Dir("replacement-destination");
    temp.File(Path.Combine("replacement-destination", "old.txt"), "old");
    var replaced = operations.Move(replacementSource, replacementDestination, "replace");
    Check(replaced.Ok && replaced.DestinationReplaced, replaced.Error ?? "directory replace failed");
    Check(!Directory.Exists(replacementSource), "replacement source survived");
    Check(!File.Exists(Path.Combine(replacementDestination, "old.txt")), "old destination survived");
    Equal("new", File.ReadAllText(Path.Combine(replacementDestination, "new.txt")));
}

static void CollisionPreservesEntries()
{
    using var temp = new TempRoot();
    var operations = new MoveRemoveOperations();
    var source = temp.File("source.txt", "source");
    var destination = temp.File("destination.txt", "destination");
    ThrowsCode("collision", () => operations.Move(source, destination));
    Equal("source", File.ReadAllText(source));
    Equal("destination", File.ReadAllText(destination));
}

static void SimulatedCrossVolumeMove()
{
    using var temp = new TempRoot();
    var source = temp.Dir("cross-source");
    temp.File(Path.Combine("cross-source", "one.txt"), "one");
    temp.File(Path.Combine("cross-source", "nested", "two.txt"), "two Ω");
    Directory.CreateDirectory(Path.Combine(source, "empty"));
    var destination = Path.Combine(temp.Path, "cross-destination");
    var platform = new CrossVolumePlatform();
    var receipt = new MoveRemoveOperations(platform).Move(source, destination);

    Check(receipt.Ok && receipt.CrossVolume && receipt.Verified, receipt.Error ?? "cross-volume move failed");
    Check(receipt.SourceRemoved && platform.CleanupCalls == 1, "verified source cleanup did not occur exactly once");
    Equal(2L, receipt.Copied);
    Check(!Directory.Exists(source), "cross-volume source survived");
    Equal("one", File.ReadAllText(Path.Combine(destination, "one.txt")));
    Equal("two Ω", File.ReadAllText(Path.Combine(destination, "nested", "two.txt")));
    Check(Directory.Exists(Path.Combine(destination, "empty")), "empty directory missing from deterministic tree copy");
}

static void CrossVolumeCleanupFailure()
{
    using var temp = new TempRoot();
    var source = temp.Dir("partial-source");
    temp.File(Path.Combine("partial-source", "first.txt"), "first");
    temp.File(Path.Combine("partial-source", "second.txt"), "second");
    var destination = Path.Combine(temp.Path, "partial-destination");
    var platform = new CrossVolumePlatform(partialCleanupFailure: true);
    var receipt = new MoveRemoveOperations(platform).Move(source, destination);

    Check(!receipt.Ok, "partial cleanup was reported as success");
    Equal("source_cleanup_failed", receipt.Error);
    Check(receipt.CrossVolume && receipt.Verified && receipt.Partial && receipt.RecoveryRequired, "partial/recovery fields were not honest");
    Check(Directory.Exists(source), "source root unexpectedly vanished after injected cleanup failure");
    Check(!File.Exists(Path.Combine(source, "first.txt")), "injected partial cleanup did not remove its first entry");
    Equal("second", File.ReadAllText(Path.Combine(source, "second.txt")));
    Equal("first", File.ReadAllText(Path.Combine(destination, "first.txt")));
    Equal("second", File.ReadAllText(Path.Combine(destination, "second.txt")));
}

static void CrossVolumeFirstFileFailure()
{
    using var temp = new TempRoot();
    var source = temp.File("first-file-source.txt", "first-file-write-must-be-partial");
    var destination = Path.Combine(temp.Path, "first-file-destination.txt");
    var platform = new CrossVolumePlatform(failCopyAfterBytes: 5);
    var receipt = new MoveRemoveOperations(platform).Move(source, destination);

    Check(!receipt.Ok, "first-file write failure was reported as success");
    Equal("move_incomplete", receipt.Error);
    Check(receipt.Partial && !receipt.Verified && !receipt.SourceRemoved, "first-file mutation was not reported as partial");
    Equal(0L, receipt.Copied);
    Check(File.Exists(source), "source was removed after an unverified partial copy");
    Check(File.Exists(destination), "partial destination file was not retained for an honest receipt");
    Equal(5L, new FileInfo(destination).Length);
}

static void RecoverableRemove()
{
    using var temp = new TempRoot();
    var target = temp.File("recover me.txt", "recoverable");
    var recoveryRoot = Path.Combine(temp.Path, "explicit recovery");
    var receipt = new MoveRemoveOperations().Remove(
        target,
        options: new MoveRemoveOptions(RecoveryRoot: recoveryRoot));

    Check(receipt.Ok && !receipt.Permanent && !receipt.Partial, receipt.Error ?? "recoverable remove failed");
    Check(!File.Exists(target), "recoverable remove left original target");
    Check(receipt.RecoveryPath is not null && File.Exists(receipt.RecoveryPath), "recovered file missing");
    Equal("recoverable", File.ReadAllText(receipt.RecoveryPath!));
    Check(Path.GetRelativePath(recoveryRoot, receipt.RecoveryPath!) is var relative && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "receipt escaped recovery root");
}

static void PermanentRemove()
{
    using var temp = new TempRoot();
    var target = temp.Dir("permanent");
    var nested = Directory.CreateDirectory(Path.Combine(target, "nested")).FullName;
    var readOnly = Path.Combine(nested, "read-only.txt");
    File.WriteAllText(readOnly, "delete");
    File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly | FileAttributes.Hidden);
    var receipt = new MoveRemoveOperations().Remove(target, permanent: true);

    Check(receipt.Ok && receipt.Permanent && !receipt.Partial, receipt.Error ?? "permanent remove failed");
    Equal(3L, receipt.Removed);
    Check(!Directory.Exists(target), "permanent target survived");
}

static void LockedFile()
{
    using var temp = new TempRoot();
    var target = temp.File("locked.txt", "locked");
    RemoveOperationResult receipt;
    using (var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        receipt = new MoveRemoveOperations().Remove(target, permanent: true);
        Check(!receipt.Ok && receipt.Error == "remove_failed" && !receipt.Partial, "locked failure receipt was not bounded");
        Check(File.Exists(target), "locked exact target was deleted");
    }
    Equal("locked", File.ReadAllText(target));

    var moveSource = temp.File("locked-move-source.txt", "move-locked");
    var moveDestination = Path.Combine(temp.Path, "locked-move-destination.txt");
    using (var held = new FileStream(moveSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var moveReceipt = new MoveRemoveOperations().Move(moveSource, moveDestination);
        Check(!moveReceipt.Ok && moveReceipt.Error == "move_failed" && !moveReceipt.Partial, "locked move failure was not bounded");
        Check(File.Exists(moveSource), "locked move source was removed");
        Check(!File.Exists(moveDestination), "locked move created a destination");
    }
    Equal("move-locked", File.ReadAllText(moveSource));
}

static void ReparseSafety()
{
    using var temp = new TempRoot();
    var outside = temp.Dir("outside");
    var marker = temp.File(Path.Combine("outside", "marker.txt"), "external-safe");
    var leaf = Path.Combine(temp.Path, "leaf-link");
    CreateDirectoryLink(leaf, outside);

    var removed = new MoveRemoveOperations().Remove(leaf, permanent: true);
    Check(removed.Ok, removed.Error ?? "leaf reparse delete failed");
    Check(!Directory.Exists(leaf), "leaf reparse entry survived");
    Equal("external-safe", File.ReadAllText(marker));

    var recoverableLeaf = Path.Combine(temp.Path, "recoverable-leaf-link");
    CreateDirectoryLink(recoverableLeaf, outside);
    var recovered = new MoveRemoveOperations().Remove(
        recoverableLeaf,
        options: new MoveRemoveOptions(RecoveryRoot: Path.Combine(temp.Path, "link-recovery")));
    Check(recovered.Ok && recovered.RecoveryPath is not null, recovered.Error ?? "recoverable leaf reparse move failed");
    Check(!Directory.Exists(recoverableLeaf), "recoverable leaf link survived at the source");
    Equal("external-safe", File.ReadAllText(marker));
    var deletedRecoveredLink = new MoveRemoveOperations().Remove(recovered.RecoveryPath!, permanent: true);
    Check(deletedRecoveredLink.Ok, deletedRecoveredLink.Error ?? "recovered leaf cleanup failed");
    Equal("external-safe", File.ReadAllText(marker));

    var ancestor = Path.Combine(temp.Path, "ancestor-link");
    CreateDirectoryLink(ancestor, outside);
    ThrowsCode("reparse_boundary", () => new MoveRemoveOperations().Remove(Path.Combine(ancestor, "marker.txt"), permanent: true));
    Equal("external-safe", File.ReadAllText(marker));
    Directory.Delete(ancestor);
}

static void CrossVolumeReparseAdmission()
{
    using var temp = new TempRoot();
    var outside = temp.Dir("outside");
    temp.File(Path.Combine("outside", "external.txt"), "external-safe");
    var source = temp.Dir("source-with-link");
    temp.File(Path.Combine("source-with-link", "ordinary.txt"), "ordinary");
    var link = Path.Combine(source, "linked");
    CreateDirectoryLink(link, outside);
    var destination = temp.Dir("existing-destination");
    var destinationMarker = temp.File(Path.Combine("existing-destination", "keep.txt"), "keep");

    ThrowsCode(
        "reparse_boundary",
        () => new MoveRemoveOperations(new CrossVolumePlatform()).Move(source, destination, "replace"));
    Equal("keep", File.ReadAllText(destinationMarker));
    Equal("ordinary", File.ReadAllText(Path.Combine(source, "ordinary.txt")));
    Equal("external-safe", File.ReadAllText(Path.Combine(outside, "external.txt")));
    Directory.Delete(link);
}

static void CreateDirectoryLink(string link, string target)
{
    Directory.CreateDirectory(link);
    try
    {
        Junction.Create(link, target);
    }
    catch
    {
        if (Directory.Exists(link)) Directory.Delete(link);
        throw;
    }
}

static void ThrowsCode(string code, Action action)
{
    try
    {
        action();
        throw new InvalidOperationException($"expected {code}");
    }
    catch (AgentOpsException ex) when (ex.Code == code)
    {
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class CrossVolumePlatform(bool partialCleanupFailure = false, int? failCopyAfterBytes = null) : IMoveRemovePlatform
{
    private int _copyStreams;
    public int CleanupCalls { get; private set; }

    public bool IsSameVolume(string source, string destination) => false;

    public Stream CreateDestinationFile(string destination, int bufferSize)
    {
        var stream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.SequentialScan);
        return failCopyAfterBytes is { } limit && Interlocked.Increment(ref _copyStreams) == 1
            ? new FailingWriteStream(stream, limit)
            : stream;
    }

    public void DeleteVerifiedSource(string source, Action deleteExact)
    {
        CleanupCalls++;
        if (partialCleanupFailure)
        {
            File.Delete(Path.Combine(source, "first.txt"));
            throw new IOException("injected cleanup failure");
        }
        deleteExact();
    }
}

sealed class FailingWriteStream(Stream inner, int byteLimit) : Stream
{
    private int _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var allowed = Math.Max(0, Math.Min(count, byteLimit - _written));
        if (allowed > 0)
        {
            inner.Write(buffer, offset, allowed);
            _written += allowed;
        }
        throw new IOException("injected first-file write failure");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-move-remove-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public string File(string relative, string text)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint MountPointTag = 0xA0000003;

    public static void Create(string link, string target)
    {
        var printName = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var substituteName = @"\??\" + printName;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var pathBytes = new byte[substituteBytes.Length + 2 + printBytes.Length + 2];
        substituteBytes.CopyTo(pathBytes, 0);
        printBytes.CopyTo(pathBytes, substituteBytes.Length + 2);

        var buffer = new byte[16 + pathBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(8 + pathBytes.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        pathBytes.CopyTo(buffer, 16);

        using var handle = NativeMethods.CreateFile(
            link,
            GenericWrite,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open junction fixture");
        if (!NativeMethods.DeviceIoControl(
                handle,
                SetReparsePoint,
                buffer,
                checked((uint)buffer.Length),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "set junction fixture");
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[] input,
            uint inputLength,
            IntPtr output,
            uint outputLength,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
