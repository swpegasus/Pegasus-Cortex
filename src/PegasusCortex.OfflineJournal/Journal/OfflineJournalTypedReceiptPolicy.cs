using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Journal;

/// <summary>
/// Pure receipt-to-projection policy. Authentication remains a host responsibility; this layer
/// binds an authenticated public receipt back to exact retained journal metadata.
/// </summary>
internal static class OfflineJournalTypedReceiptPolicy
{
    public static TypedReceiptProjection Evaluate(
        OfflineJournalIdentity identity,
        OfflineJournalStateV1 state,
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        bool authenticationProofVerified,
        IReadOnlyDictionary<long, JournalEntryMetadataV1> entries)
    {
        var validation = CortexSyncContractPolicy.ValidateReceipt(
            receipt,
            bundle,
            authenticationProofVerified);
        if (!validation.Accepted)
        {
            return TypedReceiptProjection.Rejected(
                validation.Issues.Count == 0
                    ? "The typed synchronization receipt was rejected."
                    : string.Join("; ", validation.Issues.Select(item => item.Code)));
        }

        if (!string.Equals(bundle.StoreId, identity.StoreId, StringComparison.Ordinal) ||
            !string.Equals(bundle.StoreEpoch, state.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(bundle.ProjectId, identity.ProjectId, StringComparison.Ordinal))
        {
            return TypedReceiptProjection.Rejected(
                "The typed bundle does not match the durable journal store epoch and project.");
        }

        foreach (var item in bundle.Events)
        {
            if (!entries.TryGetValue(item.Sequence, out var entry) ||
                !string.Equals(item.EventId, entry.EntryId, StringComparison.Ordinal) ||
                !string.Equals(item.ProducerInstanceId, identity.WriterId, StringComparison.Ordinal) ||
                !string.Equals(item.OperationId, entry.Operation, StringComparison.Ordinal) ||
                !string.Equals(item.PayloadContractId, entry.PayloadContractId, StringComparison.Ordinal) ||
                item.PayloadBytes != entry.PlaintextBytes ||
                !string.Equals(item.PayloadSha256, entry.PlaintextSha256, StringComparison.Ordinal) ||
                item.OccurredAt != entry.CreatedAt ||
                !string.Equals(item.Source.SourceKind, "client_operation", StringComparison.Ordinal))
            {
                return TypedReceiptProjection.Rejected(
                    "The typed bundle relabels or does not exactly identify retained journal content.");
            }
        }

        var stateValue = validation.DurablyReconciled
            ? JournalEntrySyncState.Reconciled
            : JournalEntrySyncState.Uploaded;
        return new TypedReceiptProjection(
            true,
            validation.DurablyReconciled,
            bundle.Events.Select(item => new JournalEntrySyncStatus(
                item.EventId,
                item.Sequence,
                stateValue,
                receipt.ReceiptId)).ToArray(),
            null);
    }
}

internal sealed record TypedReceiptProjection(
    bool Accepted,
    bool Reconciled,
    IReadOnlyList<JournalEntrySyncStatus> Statuses,
    string? Failure)
{
    public static TypedReceiptProjection Rejected(string failure) =>
        new(false, false, [], failure);
}
