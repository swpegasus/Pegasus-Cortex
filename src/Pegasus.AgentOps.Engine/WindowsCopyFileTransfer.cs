using System.Runtime.InteropServices;

namespace Pegasus.AgentOps.Engine;

internal sealed record CopyProgressSnapshot(long Deleted, long Copied, long Skipped, long BytesTransferred,
    long? CurrentFileTotalBytes, long CurrentFileBytesTransferred, bool DestinationMayHaveChanged,
    bool CancellationRequested, bool ByteProgressObserved = false, bool ByteCountSaturated = false);
internal sealed record CopyOperationOutcome(bool Ok, string? Error, long Deleted, long Copied, long Skipped,
    long BytesTransferred, bool Canceled, bool DestinationMayHaveChanged, int? NativeError = null,
    bool ByteProgressObserved = false, bool ByteCountSaturated = false)
{
    internal bool Partial => !Ok && DestinationMayHaveChanged;
    // The operation never deletes its source. This does NOT assert that an
    // external actor retained, preserved or left that source unchanged.
    internal bool SourceDeletionIssued => false;
}
internal sealed record CopyFileTransferOutcome(bool Success, bool Canceled, bool Issued, long BytesTransferred,
    long? TotalBytes = null, int? NativeError = null, string? Error = null, bool ProgressObserved = false);
internal interface ICopyFileTransfer
{
    // Progress arguments are whole-file total and whole-file bytes transferred,
    // NOT per-stream deltas. Completion owns the original file operation.
    CopyFileTransferOutcome Copy(string source, string destination, bool overwrite, CancellationToken token,
        Action<long, long>? progress);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint WindowsCopyProgressRoutine(long totalFileSize, long totalBytesTransferred,
    long streamSize, long streamBytesTransferred, uint streamNumber, uint callbackReason,
    nint sourceHandle, nint destinationHandle, nint data);
internal readonly record struct WindowsCopyNativeResult(bool Success, int Error);
internal interface IWindowsCopyFileNative
{
    WindowsCopyNativeResult Copy(string source, string destination, WindowsCopyProgressRoutine progress, uint flags);
}

/// <summary>The same Windows primitive/overwrite flags used by .NET File.Copy,
/// adding a fixed progress callback. Never restartable, decrypting, shell or retry.</summary>
internal sealed class WindowsCopyFileTransfer : ICopyFileTransfer
{
    internal const uint Continue = 0, Stop = 2, FailIfExists = 1;
    private readonly IWindowsCopyFileNative _native;
    internal WindowsCopyFileTransfer() : this(new Native()) { }
    internal WindowsCopyFileTransfer(IWindowsCopyFileNative native) => _native = native ?? throw new ArgumentNullException(nameof(native));

    public CopyFileTransferOutcome Copy(string source, string destination, bool overwrite, CancellationToken token,
        Action<long, long>? progress)
    {
        if (token.IsCancellationRequested) return new(false, true, false, 0, Error: "canceled");
        var gate = new object();
        long transferred = 0; long? total = null;
        var callbackSeen = false; var canceled = false; var reportFailed = false; var invalid = false;
        WindowsCopyProgressRoutine callback = (size, bytes, streamSize, streamBytes, stream, reason, sourceHandle, destinationHandle, data) =>
        {
            try
            {
                lock (gate)
                {
                    callbackSeen = true;
                    if (size < 0 || bytes < 0) { invalid = true; return Stop; }
                    total = total is { } previous ? Math.Max(previous, size) : size;
                    transferred = Math.Max(transferred, bytes);
                }
                try { progress?.Invoke(size, bytes); }
                catch { lock (gate) reportFailed = true; return Stop; }
                if (token.IsCancellationRequested) { lock (gate) canceled = true; return Stop; }
                return Continue;
            }
            catch { lock (gate) invalid = true; return Stop; } // No managed exception crosses the callback ABI.
        };
        WindowsCopyNativeResult result;
        try
        {
            // CopyFileEx is synchronous. Its callback, managed roots and borrowed
            // handles remain owned through actual return; never close those handles.
            // pbCancel is NULL so cancellation uses STOP (retains partial output),
            // not CANCEL/pbCancel (which may delete the partial destination).
            result = _native.Copy(source, destination, callback, overwrite ? 0 : FailIfExists);
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or PlatformNotSupportedException)
        { return new(false, false, false, 0, Error: "copy_cancellation_unsupported"); }
        finally { GC.KeepAlive(callback); }
        lock (gate)
        {
            // Successful native completion is authoritative even if cancellation
            // or a callback observation raced its final return.
            if (result.Success) return new(true, false, true, transferred, total, ProgressObserved: callbackSeen);
            var wasCanceled = result.Error == 1235 && canceled && !reportFailed && !invalid;
            var error = reportFailed ? "copy_progress_failed" : invalid ? "copy_progress_invalid" : wasCanceled ? "canceled" :
                result.Error is 80 or 183 ? "collision" : result.Error is 2 or 3 ? "not_found" :
                result.Error == 5 ? "access" : "copy_failed";
            return new(false, wasCanceled, true, transferred, total, result.Error, error, callbackSeen);
        }
    }

    private sealed class Native : IWindowsCopyFileNative
    {
        public WindowsCopyNativeResult Copy(string source, string destination, WindowsCopyProgressRoutine progress, uint flags)
        {
            var ok = CopyFileExW(NativePath(source), NativePath(destination), progress, 0, 0, flags);
            return new(ok, ok ? 0 : Marshal.GetLastPInvokeError());
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CopyFileExW(string source, string destination, WindowsCopyProgressRoutine progress,
            nint data, nint cancel, uint flags);
    }

    // Match File.Copy's Windows long-path shim after the shared walk has
    // normalized the path. Preserve short-path normalization and device syntax.
    internal static string NativePath(string path)
    {
        var device = path.StartsWith(@"\??\", StringComparison.Ordinal) || path.Length >= 4 &&
            (path[0] is '\\' or '/') && (path[1] is '\\' or '/') && (path[2] is '.' or '?') && (path[3] is '\\' or '/');
        if ((path.Length < 260 && !path.EndsWith(' ') && !path.EndsWith('.')) ||
            !Path.IsPathFullyQualified(path) || device)
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
