using Pegasus.Shared.Diagnostics;
using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.OfflineJournal.Contracts;

public static class OfflineJournalContractIds
{
    public const string Canonicalization = CortexSyncContractIds.Canonicalization;
    public const string Entry = "pegasus.cortex.offline-journal-storage-entry/1.0";
    public const string State = "pegasus.cortex.offline-journal-storage-state/1.0";
    public const string Receipt = "pegasus.cortex.offline-journal-storage-receipt/1.0";
    public const string LegacyImport = "pegasus.cortex.legacy-import-storage/1.0";
    public const string RetentionPlan = "pegasus.cortex.offline-journal-retention-plan/1.0";
    public const string LegacyFileCopy = CortexSyncContractIds.LegacyFileCopyPayload;
    public const string LegacyMigrationRecord = CortexSyncContractIds.LegacyMigrationRecord;
    public const string ContentTreatment = CortexSyncContractIds.ContentTreatment;
    public const string EmptySha256 = CortexSyncContractIds.EmptySha256;
}

public static class OfflineJournalContractLimits
{
    public const int MaximumRetentionPlanCandidates = 1024;
}

public sealed record OfflineJournalIdentity(
    string JournalId,
    string WriterId,
    string StoreId,
    string ProjectId);

public sealed record OfflineJournalOptions
{
    public int MaxIdentifierCharacters { get; init; } = CortexSyncContractLimits.MaximumIdentifierCharacters;
    public long MaxPlaintextBytes { get; init; } = CortexSyncContractLimits.MaximumEventPayloadBytes;
    public long MaxProtectedPayloadBytes { get; init; } = 1024 * 1024;
    public int MaxMetadataBytes { get; init; } = 64 * 1024;
    public int MaxPendingEntries { get; init; } = 10_000;
    public long MaxPendingBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxRetainedEntries { get; init; } = 50_000;
    public long MaxRetainedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaxRecoveryEntries { get; init; } = 50_000;
    public int MaxStagingArtifacts { get; init; } = 1024;
    public int MaxLegacyEvidenceReceipts { get; init; } = 50_000;
    public int MaxTypedReceipts { get; init; } = 50_000;
    public int MaxRetentionPlanCandidates { get; init; } =
        OfflineJournalContractLimits.MaximumRetentionPlanCandidates;
    public long MaxReceiptEvidenceBytes { get; init; } = 1024 * 1024;
    public int MaxLegacyImportFiles { get; init; } = CortexSyncContractLimits.MaximumMigrationFiles;
    public long MaxLegacyImportFileBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxLegacyImportTotalBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxLegacyManifestBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxLegacyExportPlaintextBytes { get; init; } =
        CortexSyncContractLimits.MaximumCanonicalBundleBytes / 2;
    public int MaxLegacyExportEvents { get; init; } = CortexSyncContractLimits.MaximumEventsPerBundle;
    public int MaxExportEntries { get; init; } = 64;
    public long MaxExportPlaintextBytes { get; init; } = 16L * 1024 * 1024;

    public void Validate()
    {
        if (MaxIdentifierCharacters is < 32 or > CortexSyncContractLimits.MaximumIdentifierCharacters)
            throw new ArgumentOutOfRangeException(nameof(MaxIdentifierCharacters));
        if (MaxPlaintextBytes is < 1024 or > CortexSyncContractLimits.MaximumEventPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxPlaintextBytes));
        if (MaxProtectedPayloadBytes < MaxPlaintextBytes ||
            MaxProtectedPayloadBytes > 128L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProtectedPayloadBytes));
        }
        if (MaxMetadataBytes is < 4096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMetadataBytes));
        if (MaxPendingEntries is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingEntries));
        if (MaxPendingBytes < MaxProtectedPayloadBytes + MaxMetadataBytes ||
            MaxPendingBytes > 8L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingBytes));
        if (MaxRetainedEntries < MaxPendingEntries || MaxRetainedEntries > 200_000)
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedEntries));
        if (MaxReceiptEvidenceBytes is < 1024 or > 16L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxReceiptEvidenceBytes));
        if (MaxRetainedBytes < MaxPendingBytes + MaxReceiptEvidenceBytes + MaxMetadataBytes ||
            MaxRetainedBytes > 32L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedBytes));
        }
        if (MaxRecoveryEntries < MaxRetainedEntries || MaxRecoveryEntries > 200_000)
            throw new ArgumentOutOfRangeException(nameof(MaxRecoveryEntries));
        if (MaxStagingArtifacts is < 1 || MaxStagingArtifacts > MaxRecoveryEntries)
            throw new ArgumentOutOfRangeException(nameof(MaxStagingArtifacts));
        if (MaxLegacyEvidenceReceipts is < 1 ||
            MaxLegacyEvidenceReceipts > MaxRetainedEntries ||
            MaxLegacyEvidenceReceipts > MaxRecoveryEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyEvidenceReceipts));
        }
        if (MaxTypedReceipts is < 1 || MaxTypedReceipts > MaxRecoveryEntries)
            throw new ArgumentOutOfRangeException(nameof(MaxTypedReceipts));
        if (MaxRetentionPlanCandidates is < 1 ||
            MaxRetentionPlanCandidates > OfflineJournalContractLimits.MaximumRetentionPlanCandidates ||
            MaxRetentionPlanCandidates > MaxRetainedEntries)
            throw new ArgumentOutOfRangeException(nameof(MaxRetentionPlanCandidates));
        if (MaxLegacyImportFiles is < 1 or > CortexSyncContractLimits.MaximumMigrationFiles)
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyImportFiles));
        if (MaxLegacyImportFileBytes is < 1024 or > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyImportFileBytes));
        if (MaxLegacyImportTotalBytes < MaxLegacyImportFileBytes ||
            MaxLegacyImportTotalBytes > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyImportTotalBytes));
        }
        if (MaxLegacyManifestBytes is < 64 * 1024 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyManifestBytes));
        if (MaxLegacyExportPlaintextBytes < CortexLegacyFileCopyPayloadV1.MaximumSourceBytes ||
            MaxLegacyExportPlaintextBytes > CortexSyncContractLimits.MaximumCanonicalBundleBytes / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyExportPlaintextBytes));
        }
        if (MaxLegacyExportEvents is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle)
            throw new ArgumentOutOfRangeException(nameof(MaxLegacyExportEvents));
        if (MaxExportEntries is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle)
            throw new ArgumentOutOfRangeException(nameof(MaxExportEntries));
        if (MaxExportPlaintextBytes < MaxPlaintextBytes ||
            MaxExportPlaintextBytes > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxExportPlaintextBytes));
        }
    }
}

public sealed record OfflineJournalStateV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string JournalId,
    string WriterId,
    string StoreId,
    string ProjectId,
    string StoreEpoch,
    long HighWaterSequence,
    string LastEntrySha256,
    string PreviousStateSha256,
    string StateSha256);

public sealed record JournalPayloadProtectionMetadata(
    string Algorithm,
    string KeyId,
    string Nonce,
    string AuthenticationTag);

public sealed record JournalEntryMetadataV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string JournalId,
    string WriterId,
    long Sequence,
    string EntryId,
    string IdempotencyKey,
    string Operation,
    string PayloadContractId,
    string? BaseRevisionId,
    string? BaseRevisionSha256,
    DateTimeOffset CreatedAt,
    long PlaintextBytes,
    string PlaintextSha256,
    long ProtectedPayloadBytes,
    string ProtectedPayloadSha256,
    JournalPayloadProtectionMetadata Protection,
    string PreviousEntrySha256,
    string ContentTreatment,
    bool Executable,
    string EntrySha256);

public sealed record JournalAppendRequest(
    string IdempotencyKey,
    string Operation,
    string PayloadContractId,
    DateTimeOffset CreatedAt,
    string? BaseRevisionId = null,
    string? BaseRevisionSha256 = null,
    long? DeclaredPayloadBytes = null);

public enum JournalMutationDisposition
{
    Added,
    Existing,
    Conflict,
    Rejected
}

public sealed record JournalAppendResult(
    JournalMutationDisposition Disposition,
    JournalEntryMetadataV1? Entry,
    PegasusDiagnostic? Diagnostic);

public sealed record JournalAcknowledgmentRequest(
    string ReceiptId,
    string AuthorityId,
    string EntryId,
    long Sequence,
    string EntrySha256,
    string ServerRevisionId,
    string ServerProofAlgorithm,
    string ServerProofKeyId,
    DateTimeOffset AcceptedAt,
    string Disposition,
    long? DeclaredEvidenceBytes = null);

public sealed record JournalReceiptV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string JournalId,
    string WriterId,
    string ReceiptId,
    string AuthorityId,
    string EntryId,
    long Sequence,
    string EntrySha256,
    string ServerRevisionId,
    string ServerProofAlgorithm,
    string ServerProofKeyId,
    DateTimeOffset AcceptedAt,
    string Disposition,
    long EvidenceBytes,
    string EvidenceSha256,
    string ContentTreatment,
    bool Executable,
    string ReceiptSha256);

public sealed record JournalAcknowledgmentResult(
    JournalMutationDisposition Disposition,
    JournalReceiptV1? Receipt,
    PegasusDiagnostic? Diagnostic);

public sealed record JournalRecoveryResult(
    bool Ready,
    int PendingEntries,
    int RecoveredStages,
    int QuarantinedEntries,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);

public sealed record JournalExportRequest(
    string BundleId,
    string TenantIdHint,
    string NamespaceHint,
    long? ObservedPolicyRevision,
    string? BaseServerRevision,
    string PreviousBundleHash,
    string PriorEventHash,
    DateTimeOffset CreatedAt,
    int Limit);

public sealed record JournalExportResult(
    bool Accepted,
    CortexOfflineBundleV1? Bundle,
    PegasusDiagnostic? Diagnostic);

public enum JournalEntrySyncState
{
    Pending,
    Uploaded,
    Reconciled
}

public sealed record JournalEntrySyncStatus(
    string EntryId,
    long Sequence,
    JournalEntrySyncState State,
    string? ReceiptId);

public sealed record JournalTypedAcknowledgmentResult(
    JournalMutationDisposition Disposition,
    bool Uploaded,
    bool Reconciled,
    CortexSyncReceiptV1? Receipt,
    PegasusDiagnostic? Diagnostic);

public sealed record JournalRetentionPlanRequest(
    DateTimeOffset ReconciledBefore,
    DateTimeOffset PlannedAt,
    int MaximumCandidates);

public sealed record JournalRetentionCandidateV1(
    string EntryId,
    long Sequence,
    DateTimeOffset CreatedAt,
    string EntrySha256,
    string ReceiptId);

/// <summary>
/// Read-only retention evidence. This version deliberately has no apply contract because
/// deleting chained entry directories requires a separately versioned compact tombstone format.
/// </summary>
public sealed record OfflineJournalRetentionPlanV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string PlanId,
    string JournalId,
    string StoreId,
    string ProjectId,
    DateTimeOffset ReconciledBefore,
    DateTimeOffset PlannedAt,
    IReadOnlyList<JournalRetentionCandidateV1> Candidates,
    int QuarantinedArtifacts,
    string QuarantineDisposition,
    bool ApplySupported,
    bool SourceMutationAllowed,
    bool OperatorApprovalRequired,
    string PlanHash);

public sealed record JournalRetentionPlanResult(
    bool Accepted,
    OfflineJournalRetentionPlanV1? Plan,
    PegasusDiagnostic? Diagnostic);

public sealed record JournalDurabilityCapabilities(
    bool FileContentFlush,
    bool AtomicSameVolumePromotion,
    bool ParentDirectoryFlush,
    string Guarantee);

internal static class OfflineJournalDiagnostics
{
    public static PegasusDiagnostic Error(
        string code,
        string message,
        string operation,
        bool retryable = false,
        IReadOnlyDictionary<string, string?>? context = null,
        Exception? cause = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            operation,
            retryable,
            context,
            cause?.GetType().FullName);
}
