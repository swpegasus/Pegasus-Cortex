using System.Collections.Concurrent;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // The trusted internal constructor supplies only the owned fixture provider.
    // Public requests cannot select a copier, callback, native flag or identity.
    private readonly ICopyFileTransfer? _copyFileTransfer;
    private readonly ConcurrentDictionary<string, CopyProgressSnapshot> _copyProgress = new(StringComparer.Ordinal);

    private OperationReceipt ExecuteCopyReceipt(OperationRequest request, string jid, string rid,
        long sequence, CancellationToken cancellationToken, bool persist)
    {
        var source = ResolvePath(request, "src");
        var destination = ResolvePath(request, "dst");
        var policy = request.Arg.GetValueOrDefault("pol", "fail");
        if (policy is not ("fail" or "replace" or "skip" or "clean"))
            throw new AgentOpsException("invalid");

        try
        {
            // Only a running worker creates a slot. The bounded scheduler keeps
            // the original call/effect owned until its actual native return;
            // observer cancellation does not release this slot or execute again.
            void Observe(CopyProgressSnapshot snapshot) => _copyProgress[jid] = snapshot;
            var outcome = _copyFileTransfer is null
                ? CopyEngine.CopyControlled(source, destination, policy, cancellationToken, Observe)
                : CopyEngine.CopyControlled(source, destination, policy, cancellationToken, _copyFileTransfer, Observe);
            if (!persist && outcome.Canceled && !outcome.Partial)
                throw new OperationCanceledException(cancellationToken);

            var data = new Dictionary<string, object?>
            {
                ["src"] = source, ["dst"] = destination, ["pol"] = policy,
                ["del"] = outcome.Deleted, ["cp"] = outcome.Copied, ["skipped"] = outcome.Skipped,
                ["bytes_transferred"] = outcome.BytesTransferred,
                ["byte_progress_observed"] = outcome.ByteProgressObserved,
                ["byte_count_saturated"] = outcome.ByteCountSaturated,
                ["destination_may_have_changed"] = outcome.DestinationMayHaveChanged,
                // This records the operation's behavior, not an assertion that
                // an independently mutable source still exists or is unchanged.
                ["source_deletion_issued"] = outcome.SourceDeletionIssued,
                ["partial"] = outcome.Partial, ["seq"] = sequence
            };
            if (outcome.NativeError is { } nativeError) data["native_error"] = nativeError;
            var state = outcome.Ok ? "done" : outcome.Partial ? "attention" : outcome.Canceled ? "canceled" : "failed";
            var error = outcome.Ok ? null : outcome.Error ?? (outcome.Canceled ? "canceled" : "copy_failed");
            var receipt = new OperationReceipt(outcome.Ok, jid, rid, state, OperationNames.Public(request),
                error, data, Partial: outcome.Partial,
                Failure: outcome.Ok ? null : FailureEvidence.Generic(error!, outcome.Partial, "copy-engine"));
            // Persist just the terminal counters. Chunk observations never append
            // history rows, transcripts or per-file names to the durable store.
            return Finish(receipt, request, sequence, persist);
        }
        finally
        {
            _copyProgress.TryRemove(jid, out _);
        }
    }

    private IReadOnlyDictionary<string, object?> ObserveJob(OperationReceipt receipt)
    {
        var data = new Dictionary<string, object?> { ["rec"] = receipt };
        if (receipt.Op == "cp" && receipt.State == "running" &&
            _copyProgress.TryGetValue(receipt.Jid, out var progress))
        {
            // Keep the original retained receipt intact. This separate, advisory
            // snapshot is RAM-only and disappears when the host or call ends.
            data["progress"] = new Dictionary<string, object?>
            {
                ["live"] = true,
                ["del"] = progress.Deleted, ["cp"] = progress.Copied, ["skipped"] = progress.Skipped,
                ["bytes_transferred"] = progress.BytesTransferred,
                ["byte_progress_observed"] = progress.ByteProgressObserved,
                ["byte_count_saturated"] = progress.ByteCountSaturated,
                ["current_file_total_bytes"] = progress.CurrentFileTotalBytes,
                ["current_file_bytes_transferred"] = progress.CurrentFileBytesTransferred,
                ["destination_may_have_changed"] = progress.DestinationMayHaveChanged,
                ["cancellation_requested"] = progress.CancellationRequested ||
                    (_queued.TryGetValue(receipt.Jid, out var worker) && worker.Execution.IsCancellationRequested)
            };
        }
        return data;
    }
}
