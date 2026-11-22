using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Journal;
using Pegasus.Cortex.Sync.Client.Contracts;
using DurableOfflineJournal = Pegasus.Cortex.OfflineJournal.Journal.OfflineJournal;

namespace Pegasus.Cortex.Sync.Client.Journal;

/// <summary>
/// Small adapter over the existing crash-safe journal. Payload bytes are appended and flushed
/// before the coordinator is allowed to export or contact its submit transport.
/// </summary>
public sealed class OfflineJournalSyncClientJournal : ICortexSyncClientJournal
{
    private readonly DurableOfflineJournal _journal;
    private readonly int _maximumStatusEntries;

    public OfflineJournalSyncClientJournal(
        DurableOfflineJournal journal,
        int maximumStatusEntries = 50_000)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (maximumStatusEntries is < 1 or > 200_000)
            throw new ArgumentOutOfRangeException(nameof(maximumStatusEntries));
        _maximumStatusEntries = maximumStatusEntries;
    }

    public ValueTask<JournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken) =>
        _journal.RecoverAsync(cancellationToken);

    public async ValueTask<CortexSyncJournalSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var state = await _journal.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var statuses = await _journal.ReadSyncStatusAsync(
            _maximumStatusEntries,
            cancellationToken).ConfigureAwait(false);
        return new CortexSyncJournalSnapshot(state, statuses);
    }

    public async ValueTask<JournalAppendResult> AppendAsync(
        CortexLocalSyncChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var payload = new MemoryStream(change.Payload.ToArray(), writable: false);
        return await _journal.AppendAsync(
            new JournalAppendRequest(
                change.IdempotencyKey,
                change.Operation,
                change.PayloadContractId,
                change.CapturedAt,
                change.BaseRevisionId,
                change.BaseRevisionSha256,
                change.Payload.Length),
            payload,
            leaveOpen: false,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<JournalExportResult> ExportPendingAsync(
        JournalExportRequest request,
        CancellationToken cancellationToken) =>
        _journal.ExportPendingAsync(request, cancellationToken);

    public ValueTask<JournalTypedAcknowledgmentResult> RecordAcknowledgmentAsync(
        CortexOfflineBundleV1 bundle,
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken) =>
        _journal.RecordTypedAcknowledgmentAsync(bundle, receipt, cancellationToken);
}
