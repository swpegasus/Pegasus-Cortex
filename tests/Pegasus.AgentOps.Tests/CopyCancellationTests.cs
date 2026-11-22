using Pegasus.AgentOps.Engine;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Owned temporary files plus injected file/native boundaries. No service,
// network share, installed product or external source is used by this fixture.
internal static class CopyCancellationTests
{
    private static int _checks;

    internal static Task RunAsync()
    {
        _checks = 0;
        using (var files = new OwnedFiles())
        {
            PoliciesAndPartial(files);
            CancellationAndEffects(files);
            WholeFileProgress(files);
            NativeBoundary();
            if (OperatingSystem.IsWindows()) { WindowsOwnedFiles(files); OwnedReparseLeaf(files); }
            else
            {
                var source = files.File("unsupported-source", "kept");
                var target = files.PathFor("unsupported-destination");
                var result = CopyEngine.CopyControlled(source, target, "fail", CancellationToken.None);
                Check(!result.Ok && result.Error == "copy_cancellation_unsupported" && !File.Exists(target) &&
                    !result.DestinationMayHaveChanged, "non-Windows controlled refusal before effects");
            }
        }
        Console.WriteLine("PASS copy cancellation: " + _checks +
            " owned-file/injected transfer and callback assertions; policy/partial counters, cancellation, original-call ownership, metadata and source non-deletion; no network or installed-product transfer");
        return Task.CompletedTask;
    }

    private static void PoliciesAndPartial(OwnedFiles files)
    {
        foreach (var policy in new[] { "fail", "replace", "skip", "clean" })
        {
            var source = files.Directory(policy + "-source");
            File.WriteAllText(Path.Combine(source, "one"), "first");
            File.WriteAllText(Path.Combine(source, "two"), "second");
            var target = files.PathFor(policy + "-target");
            if (policy == "clean")
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "old-one"), "old");
                File.WriteAllText(Path.Combine(target, "old-two"), "old");
            }
            var transfer = new Transfer((s, d, overwrite, _, progress, call) =>
            {
                if (call == 1) return CopyNormally(s, d, overwrite, progress);
                File.WriteAllText(d, "partial"); progress?.Invoke(50, 7);
                return new(false, false, true, 7, 50, 5, "access", true);
            });
            var result = CopyEngine.CopyControlled(source, target, policy, CancellationToken.None, transfer);
            Check(!result.Ok && result.Partial && result.Error == "access" && result.NativeError == 5 &&
                result.Copied == 1 && result.Deleted == (policy == "clean" ? 2 : 0) && transfer.Calls == 2,
                policy + " failure retains exact completed counts: " + Evidence(result));
            Check(result.BytesTransferred is 12 or 13 && result.ByteProgressObserved && !result.SourceDeletionIssued &&
                Directory.GetFiles(source).Length == 2, policy + " byte/source evidence");
        }
        var skipSource = files.Directory("skip-existing-source");
        File.WriteAllText(Path.Combine(skipSource, "same"), "source");
        var skipTarget = files.Directory("skip-existing-target");
        File.WriteAllText(Path.Combine(skipTarget, "same"), "destination");
        var never = new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer"));
        var skip = CopyEngine.CopyControlled(skipSource, skipTarget, "skip", CancellationToken.None, never);
        Check(skip.Ok && skip.Skipped == 1 && skip.Copied == 0 && never.Calls == 0 &&
            !skip.DestinationMayHaveChanged && File.ReadAllText(Path.Combine(skipTarget, "same")) == "destination",
            "skip is observed without rewriting destination");

        var sourceFile = files.File("collision-source", "source");
        var destinationFile = files.File("collision-target", "target");
        var collision = CopyEngine.CopyControlled(sourceFile, destinationFile, "fail", CancellationToken.None, never);
        Check(!collision.Ok && collision.Error == "collision" && !collision.Partial && never.Calls == 0,
            "fail preflight has no destination effect");
        var invalidClean = CopyEngine.CopyControlled(sourceFile, destinationFile, "clean", CancellationToken.None, never);
        Check(!invalidClean.Ok && invalidClean.Error == "precondition" && !invalidClean.Partial,
            "clean requires directory source");
        var same = CopyEngine.CopyControlled(sourceFile, sourceFile, "replace", CancellationToken.None, never);
        Check(!same.Ok && same.Error == "precondition" && never.Calls == 0, "same-path protection preserved");
        var descendant = CopyEngine.CopyControlled(skipSource, Path.Combine(skipSource, "child"), "replace",
            CancellationToken.None, never);
        Check(!descendant.Ok && descendant.Error == "precondition" && !Directory.Exists(Path.Combine(skipSource, "child")),
            "descendant protection before effects");

        var legacySource = files.File("legacy-source", "legacy");
        var legacyTarget = files.PathFor("legacy-target");
        var legacy = CopyEngine.Copy(legacySource, legacyTarget, "fail");
        Check(legacy.Copied == 1 && legacy.Deleted == 0 && !legacy.Partial &&
            File.ReadAllText(legacyTarget) == "legacy", "public three-argument File.Copy API preserved");
    }

    private static void CancellationAndEffects(OwnedFiles files)
    {
        var source = files.File("cancel-source", "source remains");
        var target = files.PathFor("cancel-target");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var never = new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer"));
            var result = CopyEngine.CopyControlled(source, target, "fail", canceled.Token, never);
            Check(!result.Ok && result.Canceled && !result.Partial && !result.DestinationMayHaveChanged && never.Calls == 0 &&
                !File.Exists(target), "pre-entry cancellation has no effects");
        }
        using (var canceled = new CancellationTokenSource())
        {
            CopyProgressSnapshot? seen = null;
            var transfer = new Transfer((_, d, _, token, progress, _) =>
            {
                File.WriteAllText(d, "partial"); progress?.Invoke(100, 30); canceled.Cancel();
                Check(token.IsCancellationRequested, "original file worker receives token");
                return new(false, true, true, 30, 100, 1235, "canceled", true);
            });
            var result = CopyEngine.CopyControlled(source, target, "fail", canceled.Token, transfer, value => seen = value);
            Check(result.Canceled && result.Partial && result.Copied == 0 && result.BytesTransferred == 30 &&
                result.NativeError == 1235 && File.ReadAllText(target) == "partial" && File.ReadAllText(source) == "source remains",
                "mid-file stop retains partial destination and source: " + Evidence(result));
            Check(seen is { CurrentFileTotalBytes: 100, CurrentFileBytesTransferred: 30, CancellationRequested: false },
                "snapshot cancellation is last-observed, not a live-token assertion");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var oneSource = files.Directory("final-source");
            File.WriteAllText(Path.Combine(oneSource, "one"), "complete");
            var transfer = new Transfer((s, d, overwrite, _, progress, _) =>
            {
                var result = CopyNormally(s, d, overwrite, progress); canceled.Cancel(); return result;
            });
            var result = CopyEngine.CopyControlled(oneSource, files.PathFor("final-target"), "fail", canceled.Token, transfer);
            Check(result.Ok && !result.Canceled && result.Copied == 1, "last native success/EOF wins cancellation race");
        }
        var cleanSource = files.Directory("clean-cancel-source");
        File.WriteAllText(Path.Combine(cleanSource, "new"), "keep");
        var cleanTarget = files.Directory("clean-cancel-target");
        File.WriteAllText(Path.Combine(cleanTarget, "one"), "old");
        File.WriteAllText(Path.Combine(cleanTarget, "two"), "old");
        using (var canceled = new CancellationTokenSource())
        {
            var transfer = new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer"));
            var result = CopyEngine.CopyControlled(cleanSource, cleanTarget, "clean", canceled.Token, transfer,
                value => { if (value.Deleted == 1) canceled.Cancel(); });
            Check(result.Canceled && result.Partial && result.Deleted == 1 && result.Copied == 0 && transfer.Calls == 0 &&
                Directory.GetFiles(cleanTarget).Length == 1 && File.Exists(Path.Combine(cleanSource, "new")),
                "clean cancellation keeps exact deletion count without source action");
        }
        var readonlyTarget = files.Directory("readonly-target");
        var readonlyFile = Path.Combine(readonlyTarget, "readonly");
        File.WriteAllText(readonlyFile, "old"); File.SetAttributes(readonlyFile, File.GetAttributes(readonlyFile) | FileAttributes.ReadOnly);
        using (var canceled = new CancellationTokenSource())
        {
            var result = CopyEngine.CopyControlled(cleanSource, readonlyTarget, "clean", canceled.Token,
                new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer")),
                value => { if (value.DestinationMayHaveChanged && value.Deleted == 0) canceled.Cancel(); });
            Check(result.Canceled && result.Partial && result.Deleted == 0 && File.Exists(readonlyFile) &&
                (File.GetAttributes(readonlyFile) & FileAttributes.ReadOnly) == 0,
                "attribute-only clean effect is partial even with zero deleted files");
        }
        var empty = files.Directory("empty-source");
        var emptyTarget = files.PathFor("empty-target");
        var emptyResult = CopyEngine.CopyControlled(empty, emptyTarget, "fail", CancellationToken.None,
            new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer")),
            _ => throw new InvalidOperationException("injected observer fault"));
        Check(!emptyResult.Ok && emptyResult.Partial && emptyResult.Copied == 0 && Directory.Exists(emptyTarget) &&
            emptyResult.Error == "copy_progress_failed", "empty-directory effect survives observer failure");

        var finalTarget = files.PathFor("telemetry-target");
        var completed = CopyEngine.CopyControlled(source, finalTarget, "fail", CancellationToken.None,
            new Transfer((s, d, overwrite, _, _, _) => { File.Copy(s, d, overwrite); return new(true, false, true, 14); }),
            _ => throw new InvalidOperationException("terminal observer fault"));
        Check(completed.Ok && completed.Copied == 1 && File.Exists(finalTarget), "terminal telemetry cannot undo file success");

        var notIssued = CopyEngine.CopyControlled(source, files.PathFor("not-issued"), "fail", CancellationToken.None,
            new Transfer((_, _, _, _, _, _) => new(false, false, false, 0, Error: "copy_cancellation_unsupported")));
        Check(!notIssued.Ok && !notIssued.Partial && !notIssued.DestinationMayHaveChanged,
            "non-issued file refusal restores provisional destination marker");
    }

    private static void WholeFileProgress(OwnedFiles files)
    {
        var source = files.Directory("progress-source");
        File.WriteAllText(Path.Combine(source, "one"), "one");
        File.WriteAllText(Path.Combine(source, "two"), "two");
        var snapshots = new List<CopyProgressSnapshot>();
        var transfer = new Transfer((s, d, overwrite, _, progress, _) =>
        {
            File.Copy(s, d, overwrite);
            foreach (var count in new long[] { 20, 20, 10, 50, 100 }) progress?.Invoke(100, count);
            return new(true, false, true, 100, 100, ProgressObserved: true);
        });
        var result = CopyEngine.CopyControlled(source, files.PathFor("progress-target"), "fail", CancellationToken.None,
            transfer, snapshots.Add);
        Check(result.Ok && result.Copied == 2 && result.BytesTransferred == 200 && result.ByteProgressObserved &&
            !result.ByteCountSaturated, "whole-file cumulative progress is not summed as stream/chunk deltas");
        Check(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.BytesTransferred <= pair.Second.BytesTransferred),
            "aggregate progress never goes backward across file completion");
        var overflow = CopyEngine.CopyControlled(source, files.PathFor("overflow-target"), "fail", CancellationToken.None,
            new Transfer((s, d, overwrite, _, progress, _) =>
            {
                File.Copy(s, d, overwrite); progress?.Invoke(long.MaxValue, long.MaxValue);
                return new(true, false, true, long.MaxValue, long.MaxValue, ProgressObserved: true);
            }));
        Check(overflow.Ok && overflow.Copied == 2 && overflow.BytesTransferred == long.MaxValue && overflow.ByteCountSaturated,
            "unrepresentable byte sum is explicitly marked saturated");
    }

    private static void NativeBoundary()
    {
        uint returned = uint.MaxValue;
        var native = new Native((callback, flags) =>
        {
            Check(flags == WindowsCopyFileTransfer.FailIfExists, "fail-if-exists native flag");
            returned = callback(100, 30, 100, 30, 1, 0, (nint)11, (nint)12, (nint)13);
            return new(false, 1235);
        });
        var failed = new WindowsCopyFileTransfer(native).Copy("source", "target", false, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("injected callback observer"));
        Check(returned == WindowsCopyFileTransfer.Stop && failed.Error == "copy_progress_failed" && failed.Issued &&
            !failed.Canceled && failed.NativeError == 1235 && failed.BytesTransferred == 30,
            "callback exception contained at ABI and STOP preserves partial result");
        using var canceled = new CancellationTokenSource();
        var cancelNative = new Native((callback, flags) =>
        {
            Check(flags == 0, "replace uses no extra native flags"); canceled.Cancel();
            returned = callback(100, 40, 80, 40, 2, 1, 0, 0, 0); return new(false, 1235);
        });
        var stopped = new WindowsCopyFileTransfer(cancelNative).Copy("source", "target", true, canceled.Token, null);
        Check(stopped.Canceled && stopped.Issued && returned == 2 && stopped.BytesTransferred == 40,
            "cancellation uses STOP rather than deleting CANCEL");
        using var race = new CancellationTokenSource();
        var successNative = new Native((callback, _) =>
        { race.Cancel(); returned = callback(1, 1, 1, 1, 1, 0, 0, 0, 0); return new(true, 0); });
        var success = new WindowsCopyFileTransfer(successNative).Copy("source", "target", false, race.Token, null);
        Check(success.Success && !success.Canceled && returned == 2, "successful native return is authoritative despite STOP race");
        var invalidNative = new Native((callback, _) =>
        { returned = callback(-1, 1, 1, 1, 1, 0, 0, 0, 0); return new(false, 1235); });
        var invalid = new WindowsCopyFileTransfer(invalidNative).Copy("source", "target", false, CancellationToken.None, null);
        Check(!invalid.Success && invalid.Error == "copy_progress_invalid" && returned == 2,
            "invalid progress contained without fabricated cancellation");
        var missingNative = new Native((_, _) => new(false, 3));
        var missing = new WindowsCopyFileTransfer(missingNative).Copy("source", "target", false, CancellationToken.None, null);
        Check(missing.Error == "not_found" && missing.NativeError == 3 && !missing.ProgressObserved,
            "direct native status retained without progress invention");
        using var before = new CancellationTokenSource(); before.Cancel();
        var beforeNative = new Native((_, _) => throw new InvalidOperationException("unexpected native entry"));
        var beforeResult = new WindowsCopyFileTransfer(beforeNative).Copy("source", "target", false, before.Token, null);
        Check(!beforeResult.Issued && beforeResult.Canceled && beforeNative.Calls == 0, "native pre-entry cancellation");
    }

    private static void WindowsOwnedFiles(OwnedFiles files)
    {
        var source = files.File("metadata-source", "owned metadata fixture");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly | FileAttributes.Hidden);
        var baseline = files.PathFor("metadata-baseline"); File.Copy(source, baseline);
        var destination = files.PathFor("metadata-controlled");
        var result = CopyEngine.CopyControlled(source, destination, "fail", CancellationToken.None);
        Check(result.Ok && result.Copied == 1 && File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(baseline)) &&
            File.GetLastWriteTimeUtc(destination) == File.GetLastWriteTimeUtc(baseline) &&
            File.GetAttributes(destination) == File.GetAttributes(baseline), "real owned-file File.Copy metadata parity");

        var large = files.PathFor("native-cancel-source");
        using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { var block = new byte[1024 * 1024]; for (var index = 0; index < 16; index++) stream.Write(block); }
        var partial = files.PathFor("native-cancel-target");
        using var canceled = new CancellationTokenSource();
        var native = new WindowsCopyFileTransfer();
        var stopped = native.Copy(large, partial, false, canceled.Token, (_, _) => canceled.Cancel());
        Console.WriteLine($"Copy STOP observation: Success={stopped.Success},Canceled={stopped.Canceled},NativeError={stopped.NativeError},BytesTransferred={stopped.BytesTransferred}");
        Check(stopped.Issued && stopped.ProgressObserved && File.Exists(large) && File.Exists(partial) &&
            (stopped.Canceled && stopped.NativeError == 1235 || stopped.Success && !stopped.Canceled),
            "real owned-file STOP retains output, or authoritative already-completed success wins");
        Check(WindowsCopyFileTransfer.NativePath(@"C:\short\file") == @"C:\short\file" &&
            WindowsCopyFileTransfer.NativePath(@"C:\trailing.") == @"\\?\C:\trailing." &&
            WindowsCopyFileTransfer.NativePath(@"\\server\share\" + new string('x', 260)).StartsWith(@"\\?\UNC\server\share\", StringComparison.Ordinal) &&
            WindowsCopyFileTransfer.NativePath(@"\\?\C:\" + new string('x', 260)) == @"\\?\C:\" + new string('x', 260),
            "File.Copy-compatible extended path shim preserves short and existing device syntax");
    }

    private static void OwnedReparseLeaf(OwnedFiles files)
    {
        var source = files.Directory("link-clean-source");
        var destination = files.Directory("link-clean-target");
        var external = files.File("link-external-marker", "external owned marker");
        File.SetAttributes(external, File.GetAttributes(external) | FileAttributes.ReadOnly);
        var expectedAttributes = File.GetAttributes(external);
        var link = Path.Combine(destination, "readonly-link");
        var created = false;
        try
        {
            try { File.CreateSymbolicLink(link, external); created = true; }
            catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or NotSupportedException)
            {
                Console.WriteLine("SKIP copy readonly-link regression: unprivileged symbolic-link creation unavailable");
                return;
            }
            if (!SetLinkReadOnly(link, true, out var error))
            {
                Console.WriteLine("SKIP copy readonly-link regression: link-only attribute setup unavailable,NativeError=" + error);
                return;
            }
            var linkAttributes = File.GetAttributes(link);
            Check((linkAttributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) ==
                (FileAttributes.ReparsePoint | FileAttributes.ReadOnly), "fixture owns an actually readonly reparse leaf");
            var result = CopyEngine.CopyControlled(source, destination, "clean", CancellationToken.None,
                new Transfer((_, _, _, _, _, _) => throw new InvalidOperationException("unexpected transfer")));
            Check(File.ReadAllText(external) == "external owned marker" && File.GetAttributes(external) == expectedAttributes,
                "clean must never modify target contents/attributes through readonly reparse leaf");
            Check(result.Copied == 0 && result.Deleted is 0 or 1 &&
                (result.Ok && result.Deleted == 1 || !result.Ok && result.Partial && result.DestinationMayHaveChanged && result.Deleted == 0),
                "readonly link is unlinked or honestly refused: " + Evidence(result));
        }
        finally
        {
            // Unlink this exact owned leaf before OwnedFiles recursive cleanup.
            // Clearing readonly also uses OPEN_REPARSE_POINT, never the target.
            if (created)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(link); }
                catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException) { attributes = 0; }
                if (attributes != 0)
                {
                    Check((attributes & FileAttributes.ReparsePoint) != 0, "link cleanup refuses a substituted ordinary file");
                    if ((attributes & FileAttributes.ReadOnly) != 0 && !SetLinkReadOnly(link, false, out var error))
                        throw new InvalidOperationException("Copy link-only cleanup attribute failure,NativeError=" + error);
                    File.Delete(link);
                }
            }
        }
    }

    private static bool SetLinkReadOnly(string path, bool readOnly, out int error)
    {
        // Test-only fixed leaf setup/cleanup. Do not request data/key/security
        // access and do not follow a symlink while setting its own attribute.
        using var handle = OpenReparseLeaf(path, 0x100, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) { error = Marshal.GetLastPInvokeError(); return false; }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0) { error = 0; return false; }
        var information = new FileBasicInformation
        { Creation = 0, Access = 0, Write = 0, Change = 0,
            Attributes = (uint)(readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly) };
        var ok = SetBasicInformation(handle, 0, in information, (uint)Marshal.SizeOf<FileBasicInformation>());
        error = ok ? 0 : Marshal.GetLastPInvokeError(); return ok;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    { internal long Creation, Access, Write, Change; internal uint Attributes; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenReparseLeaf(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBasicInformation(SafeFileHandle handle, int informationClass,
        in FileBasicInformation information, uint size);

    private static CopyFileTransferOutcome CopyNormally(string source, string destination, bool overwrite, Action<long, long>? progress)
    {
        File.Copy(source, destination, overwrite); var bytes = new FileInfo(destination).Length;
        progress?.Invoke(bytes, bytes); return new(true, false, true, bytes, bytes, ProgressObserved: true);
    }
    private static string Evidence(CopyOperationOutcome result) =>
        $"ok={result.Ok},error={result.Error},del={result.Deleted},cp={result.Copied},bytes={result.BytesTransferred},canceled={result.Canceled},changed={result.DestinationMayHaveChanged},native={result.NativeError}";
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException("Copy cancellation fixture: " + message); }

    private sealed class Transfer(Func<string, string, bool, CancellationToken, Action<long, long>?, int, CopyFileTransferOutcome> copy) : ICopyFileTransfer
    {
        internal int Calls;
        public CopyFileTransferOutcome Copy(string source, string destination, bool overwrite, CancellationToken token,
            Action<long, long>? progress) => copy(source, destination, overwrite, token, progress, ++Calls);
    }
    private sealed class Native(Func<WindowsCopyProgressRoutine, uint, WindowsCopyNativeResult> copy) : IWindowsCopyFileNative
    {
        internal int Calls;
        public WindowsCopyNativeResult Copy(string source, string destination, WindowsCopyProgressRoutine progress, uint flags)
        { Calls++; return copy(progress, flags); }
    }
    private sealed class OwnedFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CortexCopyCancellation-" + Guid.NewGuid().ToString("N"));
        internal OwnedFiles() => System.IO.Directory.CreateDirectory(_root);
        internal string PathFor(string name) => System.IO.Path.Combine(_root, name);
        internal string Directory(string name) { var path = PathFor(name); System.IO.Directory.CreateDirectory(path); return path; }
        internal string File(string name, string text) { var path = PathFor(name); System.IO.File.WriteAllText(path, text); return path; }
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(_root);
            var parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            Check(System.IO.Path.GetDirectoryName(full) == System.IO.Path.TrimEndingDirectorySeparator(parent) &&
                System.IO.Path.GetFileName(full).StartsWith("CortexCopyCancellation-", StringComparison.Ordinal) &&
                (System.IO.File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "owned cleanup boundary");
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
            {
                var attributes = System.IO.File.GetAttributes(entry);
                Check((attributes & FileAttributes.ReparsePoint) == 0, "owned cleanup contains no reparse entries");
                if ((attributes & FileAttributes.ReadOnly) != 0) System.IO.File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
            System.IO.Directory.Delete(full, recursive: true);
        }
    }
}
