using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Journal;

/// <summary>
/// Pure mapping from authenticated journal plaintext to the public synchronization v1 family.
/// Store scope and payload contracts come only from durable journal state, never caller hints.
/// </summary>
internal static class OfflineJournalExportPolicy
{
    public static CortexOfflineBundleV1 BuildBundle(
        OfflineJournalIdentity identity,
        OfflineJournalStateV1 state,
        JournalExportRequest request,
        IReadOnlyList<DecryptedJournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("At least one journal entry is required for export.", nameof(entries));
        if (request.CreatedAt.Offset != TimeSpan.Zero || request.CreatedAt < entries.Max(item => item.Entry.CreatedAt))
            throw new ArgumentException("Bundle creation time must be UTC and cannot predate its events.", nameof(request));

        var events = new List<CortexOfflineEventV1>(entries.Count);
        var previousEventHash = request.PriorEventHash;
        foreach (var item in entries)
        {
            var payloadHash = CortexCanonicalJson.Sha256(item.Plaintext);
            if (item.Plaintext.Length != item.Entry.PlaintextBytes ||
                !string.Equals(payloadHash, item.Entry.PlaintextSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Decrypted payload bytes do not match immutable journal metadata.");
            }

            var source = new CortexSyncSourceProvenanceV1(
                "client_operation",
                null,
                null,
                item.Plaintext.Length,
                payloadHash,
                item.Entry.CreatedAt);
            var candidate = new CortexOfflineEventV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.OfflineEvent,
                CortexSyncContractIds.Canonicalization,
                identity.StoreId,
                state.StoreEpoch,
                identity.ProjectId,
                item.Entry.Sequence,
                item.Entry.EntryId,
                previousEventHash,
                item.Entry.CreatedAt,
                identity.WriterId,
                item.Entry.Operation,
                request.TenantIdHint,
                request.NamespaceHint,
                request.ObservedPolicyRevision,
                item.Entry.PayloadContractId,
                EncodeBase64Url(item.Plaintext),
                item.Plaintext.Length,
                payloadHash,
                source,
                CortexSyncContractIds.ContentTreatment,
                false,
                [],
                CortexSyncContractIds.EmptySha256);
            var sealedEvent = CortexSyncContractPolicy.Seal(candidate);
            events.Add(sealedEvent);
            previousEventHash = sealedEvent.EventHash;
        }

        var bundle = new CortexOfflineBundleV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            request.BundleId,
            identity.StoreId,
            state.StoreEpoch,
            identity.ProjectId,
            request.BaseServerRevision,
            request.PreviousBundleHash,
            request.PriorEventHash,
            events[0].Sequence,
            events[^1].Sequence,
            request.CreatedAt,
            events,
            [],
            CortexSyncContractIds.EmptySha256);
        return CortexSyncContractPolicy.Seal(bundle);
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record DecryptedJournalEntry(
    JournalEntryMetadataV1 Entry,
    byte[] Plaintext);
