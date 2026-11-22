using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Security;

public sealed record JournalPayloadProtectionContext(
    string JournalId,
    string WriterId,
    string StoreId,
    string ProjectId,
    long Sequence,
    string IdempotencyKey,
    string Operation,
    string PayloadContractId,
    string? BaseRevisionId,
    string? BaseRevisionSha256,
    string PreviousEntrySha256,
    ReadOnlyMemory<byte> AssociatedData);

public interface IJournalPayloadProtector
{
    /// <summary>
    /// Protects one bounded payload. Production implementations obtain keys from a host-owned
    /// provider and must never generate, persist, log, or silently substitute a key.
    /// </summary>
    ValueTask<JournalPayloadProtectionMetadata> ProtectAsync(
        Stream plaintext,
        Stream protectedDestination,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns false only after a supported suite and available key definitively reject payload
    /// authentication. A missing/retired key or unavailable suite must throw
    /// <see cref="JournalPayloadProtectionUnavailableException"/> so recovery preserves bytes.
    /// </summary>
    ValueTask<bool> VerifyAsync(
        Stream protectedPayload,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates the complete protected payload before writing any plaintext to the caller.
    /// </summary>
    ValueTask UnprotectAsync(
        Stream protectedPayload,
        Stream plaintextDestination,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken);
}
