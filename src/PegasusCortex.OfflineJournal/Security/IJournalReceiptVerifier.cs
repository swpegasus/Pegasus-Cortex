using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.OfflineJournal.Security;

public interface IJournalReceiptVerifier
{
    ValueTask<bool> VerifyAsync(
        Stream serverEvidence,
        JournalAcknowledgmentRequest acknowledgment,
        JournalEntryMetadataV1 pendingEntry,
        CancellationToken cancellationToken);

    ValueTask<bool> VerifyAsync(
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken);
}
