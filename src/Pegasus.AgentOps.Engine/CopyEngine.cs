using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed record CopyResult(long Deleted, long Copied, bool Partial = false);

public static class CopyEngine
{
    // Keep the original portable API and File.Copy backend. The same policy/walk
    // serves controlled copies; Move/ZIP have different publication/metadata rules.
    public static CopyResult Copy(string src, string dst, string pol)
    {
        var context = new Context(CancellationToken.None, null, new LegacyFileTransfer());
        try
        {
            Walk(src, dst, pol, context);
            return new(context.Deleted, context.Copied);
        }
        catch (AgentOpsException failure) when (pol == "clean" && context.Deleted > 0 && !context.Cleaning)
        { throw new AgentOpsException("copy_incomplete", context.Deleted, context.Copied, failure); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            if (context.Cleaning) throw new AgentOpsException("clean_failed", context.Deleted, context.Copied, failure);
            if (pol == "clean" && context.Copying) throw new AgentOpsException("copy_incomplete", context.Deleted, context.Copied, failure);
            throw;
        }
    }

    internal static CopyOperationOutcome CopyControlled(string src, string dst, string pol, CancellationToken cancellationToken,
        Action<CopyProgressSnapshot>? progress = null)
    {
        if (pol is not ("fail" or "replace" or "skip" or "clean"))
            return new(false, "invalid", 0, 0, 0, 0, false, false);
        // No streaming fallback that silently loses ADS/extended attributes.
        // Legacy Copy remains portable. Controlled cancellation is explicit.
        if (!OperatingSystem.IsWindows())
            return new(false, "copy_cancellation_unsupported", 0, 0, 0, 0, false, false);
        return CopyControlled(src, dst, pol, cancellationToken, new WindowsCopyFileTransfer(), progress);
    }

    internal static CopyOperationOutcome CopyControlled(string src, string dst, string pol, CancellationToken cancellationToken,
        ICopyFileTransfer fileTransfer, Action<CopyProgressSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(fileTransfer);
        var context = new Context(cancellationToken, progress, fileTransfer);
        try
        {
            if (pol is not ("fail" or "replace" or "skip" or "clean")) throw new AgentOpsException("invalid");
            context.CheckCancellation();
            Walk(src, dst, pol, context);
            // EOF/native success won. Never relabel a completed operation by
            // checking a token after its final successful effect or enumeration.
            return context.Outcome(true, null);
        }
        catch (OperationCanceledException) { return context.Outcome(false, "canceled", canceled: true); }
        catch (FileTransferFailure failure)
        { return context.Outcome(false, failure.Code, failure.Canceled, failure.NativeError); }
        catch (ProgressFailure) { return context.Outcome(false, "copy_progress_failed"); }
        catch (AgentOpsException failure)
        {
            var code = failure.Code is "not_found" or "collision" or "precondition" or "reparse_boundary" or
                "reparse_source" or "invalid" ? failure.Code : "copy_failed";
            return context.Outcome(false, code);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var code = failure is UnauthorizedAccessException ? "access" :
                failure is FileNotFoundException or DirectoryNotFoundException ? "not_found" :
                failure is ArgumentException or NotSupportedException ? "invalid" : "copy_failed";
            return context.Outcome(false, code);
        }
    }

    private static void Walk(string src, string dst, string pol, Context context)
    {
        context.CheckCancellation();
        src = Path.GetFullPath(src); dst = Path.GetFullPath(dst);
        RejectUnsafeRelationship(src, dst);
        var sourceFile = File.Exists(src); var sourceDirectory = Directory.Exists(src);
        if (!sourceFile && !sourceDirectory) throw new AgentOpsException("not_found");
        RejectReparse(src);
        if (TryGetAttributes(dst, out var destinationAttributes) && (destinationAttributes & FileAttributes.ReparsePoint) != 0)
            throw new AgentOpsException("reparse_boundary");
        if (pol == "clean" && !sourceDirectory) throw new AgentOpsException("precondition");
        if (!sourceDirectory)
        {
            if (Directory.Exists(dst)) throw new AgentOpsException("collision");
            if (File.Exists(dst) && pol == "skip") { context.Skip(); return; }
            if (File.Exists(dst) && pol != "replace") throw new AgentOpsException("collision");
            EnsureDirectory(Path.GetDirectoryName(dst)!, context);
            context.Copying = true; context.CopyFile(src, dst, pol == "replace"); return;
        }
        if (File.Exists(dst)) throw new AgentOpsException("collision");
        if (Directory.Exists(dst) && pol is not ("replace" or "skip" or "clean")) throw new AgentOpsException("collision");
        EnsureDirectory(dst, context);
        if (pol == "clean")
        {
            context.Cleaning = true;
            // MoveNext first: cancellation is checked only when another actual
            // entry exists. A final EOF does not turn successful clean into cancel.
            foreach (var entry in Directory.EnumerateFileSystemEntries(dst))
            { context.CheckCancellation(); DeleteEntry(entry, context); }
            context.Cleaning = false;
        }
        context.Copying = true;
        CopyContents(src, dst, pol, context);
    }

    private static void CopyContents(string src, string dst, string pol, Context context)
    {
        RejectReparse(src); RejectReparse(dst);
        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            context.CheckCancellation();
            var target = Path.Combine(dst, Path.GetFileName(entry));
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("reparse_source");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (File.Exists(target))
                {
                    if (pol == "skip") { context.Skip(); continue; }
                    throw new AgentOpsException("collision");
                }
                if (Directory.Exists(target)) RejectReparse(target);
                EnsureDirectory(target, context);
                CopyContents(entry, target, pol, context);
            }
            else
            {
                if (Directory.Exists(target))
                {
                    if (pol == "skip") { context.Skip(); continue; }
                    throw new AgentOpsException("collision");
                }
                if (File.Exists(target)) RejectReparse(target);
                if (File.Exists(target) && pol == "skip") { context.Skip(); continue; }
                if (File.Exists(target) && pol is not ("replace" or "clean")) throw new AgentOpsException("collision");
                context.CopyFile(entry, target, pol is "replace" or "clean");
            }
        }
    }

    private static void EnsureDirectory(string path, Context context)
    {
        context.CheckCancellation();
        var existed = Directory.Exists(path);
        if (!existed) context.MarkDestinationEffect();
        Directory.CreateDirectory(path);
        if (!existed) context.Publish();
    }

    private static void DeleteEntry(string path, Context context)
    {
        context.CheckCancellation();
        var attributes = File.GetAttributes(path);
        var directory = (attributes & FileAttributes.Directory) != 0;
        // Never write attributes through a reparse leaf. Unlink only; a readonly
        // link that Windows refuses to unlink remains a reported partial failure.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            context.CheckCancellation(); context.MarkDestinationEffect();
            if (directory) Directory.Delete(path); else File.Delete(path);
            context.DeletedOne(); return;
        }
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            context.CheckCancellation(); context.MarkDestinationEffect();
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            context.Publish();
        }
        if (!directory)
        {
            context.CheckCancellation(); context.MarkDestinationEffect(); File.Delete(path);
            context.DeletedOne(); return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        { context.CheckCancellation(); DeleteEntry(child, context); }
        context.CheckCancellation(); context.MarkDestinationEffect(); Directory.Delete(path);
        context.DeletedOne();
    }

    private static void RejectUnsafeRelationship(string src, string dst)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(src), Path.TrimEndingDirectorySeparator(dst)) ||
            IsStrictDescendant(src, dst) || IsStrictDescendant(dst, src))
            throw new AgentOpsException("precondition");
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("reparse_boundary");
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { attributes = default; return false; }
    }

    private sealed class Context(CancellationToken token, Action<CopyProgressSnapshot>? report, ICopyFileTransfer transfer)
    {
        private readonly object _gate = new();
        private long _deleted, _copied, _skipped, _completedBytes, _fileBytes;
        private long? _fileTotal;
        private bool _changed, _progressObserved, _byteCountSaturated;
        internal bool Cleaning, Copying;
        internal long Deleted { get { lock (_gate) return _deleted; } }
        internal long Copied { get { lock (_gate) return _copied; } }
        internal void CheckCancellation() => token.ThrowIfCancellationRequested();
        internal void MarkDestinationEffect() { lock (_gate) _changed = true; }
        internal void DeletedOne() { lock (_gate) _deleted++; Publish(); }
        internal void Skip() { lock (_gate) _skipped++; Publish(); }
        internal void Publish(bool completedFile = false)
        {
            if (report is null) return;
            CopyProgressSnapshot snapshot;
            lock (_gate) snapshot = new(_deleted, _copied, _skipped, AddBytes(_completedBytes, _fileBytes),
                _fileTotal, _fileBytes, _changed, token.IsCancellationRequested, _progressObserved, _byteCountSaturated);
            try { report(snapshot); }
            catch when (completedFile) { } // Optional telemetry cannot undo an already successful native file copy.
            catch { throw new ProgressFailure(); }
        }
        internal void CopyFile(string source, string destination, bool overwrite)
        {
            CheckCancellation();
            bool priorChanged;
            lock (_gate)
            {
                priorChanged = _changed; _changed = true;
                _fileBytes = 0; _fileTotal = null;
            }
            // Mark conservatively BEFORE entry. Even a zero-byte failure can have
            // created/truncated a destination or changed metadata. A clean
            // non-issued result restores only this call's provisional marker.
            var result = transfer.Copy(source, destination, overwrite, token, OnFileProgress);
            lock (_gate)
            {
                if (result.BytesTransferred < 0 || result.TotalBytes is < 0)
                    throw new FileTransferFailure("copy_progress_invalid");
                _fileBytes = Math.Max(_fileBytes, result.BytesTransferred);
                if (result.TotalBytes is { } total) _fileTotal = _fileTotal is { } prior ? Math.Max(prior, total) : total;
                _progressObserved |= result.ProgressObserved;
                if (!result.Issued && _fileBytes == 0 && !result.ProgressObserved) _changed = priorChanged;
                if (result.Success)
                {
                    if (!result.Issued || result.Canceled || result.Error is not null) throw new FileTransferFailure("copy_failed");
                    _copied++; // Exactly once, only on authoritative success.
                    _completedBytes = AddBytes(_completedBytes, _fileBytes); _fileBytes = 0; _fileTotal = null;
                }
            }
            if (!result.Success)
                throw new FileTransferFailure(SafeCode(result.Error), result.Canceled, result.NativeError);
            Publish(completedFile: true); // No cancellation check after successful file return.
        }
        private void OnFileProgress(long total, long transferred)
        {
            lock (_gate)
            {
                if (total < 0 || transferred < 0) throw new ProgressFailure();
                _changed = true; _progressObserved = true;
                _fileBytes = Math.Max(_fileBytes, transferred);
                _fileTotal = _fileTotal is { } previous ? Math.Max(previous, total) : total;
            }
            Publish();
        }
        internal CopyOperationOutcome Outcome(bool ok, string? error, bool canceled = false, int? nativeError = null)
        {
            lock (_gate) return new(ok, error, _deleted, _copied, _skipped,
                AddBytes(_completedBytes, _fileBytes), canceled, _changed, nativeError, _progressObserved, _byteCountSaturated);
        }
        private long AddBytes(long first, long second)
        {
            if (first <= long.MaxValue - second) return first + second;
            _byteCountSaturated = true;
            return long.MaxValue;
        }
        private static string SafeCode(string? error) => error is "canceled" or "collision" or "not_found" or "access" or
            "copy_cancellation_unsupported" or "copy_progress_failed" or "copy_progress_invalid" ? error : "copy_failed";
    }
    private sealed class LegacyFileTransfer : ICopyFileTransfer
    {
        public CopyFileTransferOutcome Copy(string source, string destination, bool overwrite, CancellationToken token,
            Action<long, long>? progress)
        { File.Copy(source, destination, overwrite); return new(true, false, true, 0); }
    }
    private sealed class ProgressFailure : Exception { }
    private sealed class FileTransferFailure(string code, bool canceled = false, int? nativeError = null) : Exception(code)
    {
        internal string Code { get; } = code;
        internal bool Canceled { get; } = canceled;
        internal int? NativeError { get; } = nativeError;
    }
}

public sealed class AgentOpsException : Exception
{
    public AgentOpsException(string code, long deleted = 0, long copied = 0, Exception? inner = null,
        OperationFailureEvidence? failure = null) : base(code, inner)
    { Code = code; Deleted = deleted; Copied = copied; Failure = failure; }
    public string Code { get; }
    public long Deleted { get; }
    public long Copied { get; }
    public OperationFailureEvidence? Failure { get; }
}
