using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.Sync.Client.Contracts;

public static class CortexSyncClientContractIds
{
    public const string CursorV1_0 = "pegasus.cortex.sync-client-cursor/1.0";
    public const string Cursor = "pegasus.cortex.sync-client-cursor/1.1";
    public const string PendingAttempt = "pegasus.cortex.sync-client-pending-attempt/1.0";
    public const string Canonicalization = CortexSyncContractIds.Canonicalization;
    public const string EmptySha256 = CortexSyncContractIds.EmptySha256;
}

public sealed record CortexLocalSyncChange(
    string ProjectId,
    string ProjectLinkId,
    string AuthorityHash,
    string IdempotencyKey,
    string Operation,
    string PayloadContractId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    string? BaseRevisionId = null,
    string? BaseRevisionSha256 = null);

/// <summary>
/// Compatibility surface for the pre-durability candidate. It cannot be used by the 1.1
/// coordinator because object-only submission cannot guarantee byte-identical replay.
/// </summary>
[Obsolete("Use ICortexSyncClientTransportV1_1 for the coordinated exact-byte replay cutover.")]
public interface ICortexSyncClientTransport
{
    ValueTask<CortexSyncCapabilitiesV1> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    ValueTask<CortexSyncRemoteHead> GetHeadAsync(
        CortexSyncHeadRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncSubmitResult> SubmitAsync(
        CortexSyncSubmitRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinated 1.1 candidate transport. The adapter must send the supplied canonical bytes as the
/// request body; the parsed request is provided only for typed correlation and response checking.
/// </summary>
public interface ICortexSyncClientTransportV1_1
{
    ValueTask<CortexSyncCapabilitiesV1> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    ValueTask<CortexSyncRemoteHead> GetHeadAsync(
        CortexSyncHeadRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncSubmitResult> SubmitAsync(
        CortexSyncSubmitRequest request,
        ReadOnlyMemory<byte> canonicalRequestBytes,
        CancellationToken cancellationToken);
}

public interface ICortexLocalSyncChangeSource
{
    ValueTask<CortexLocalSyncChange> ReadCurrentAsync(
        CancellationToken cancellationToken);
}

public sealed record CortexSyncJournalSnapshot(
    OfflineJournalStateV1 State,
    IReadOnlyList<JournalEntrySyncStatus> Entries);

public interface ICortexSyncClientJournal
{
    ValueTask<JournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken);

    ValueTask<CortexSyncJournalSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken);

    ValueTask<JournalAppendResult> AppendAsync(
        CortexLocalSyncChange change,
        CancellationToken cancellationToken);

    ValueTask<JournalExportResult> ExportPendingAsync(
        JournalExportRequest request,
        CancellationToken cancellationToken);

    ValueTask<JournalTypedAcknowledgmentResult> RecordAcknowledgmentAsync(
        CortexOfflineBundleV1 bundle,
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken);
}

public sealed record CortexSyncCursorV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    long Revision,
    string ProjectId,
    string StoreId,
    string LocalStoreEpoch,
    long LocalHighWaterSequence,
    string RemoteStoreEpoch,
    long RemoteHighWaterSequence,
    string RemoteEventHash,
    string RemoteBundleHash,
    string RemoteRevision,
    string LastAcknowledgedAuthorityHash,
    string LastAcknowledgedPayloadHash,
    DateTimeOffset UpdatedAt,
    string PreviousCursorHash,
    string CursorHash);

public interface ICortexSyncCursorStore
{
    ValueTask<CortexSyncCursorV1?> ReadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(CortexSyncCursorV1 cursor, CancellationToken cancellationToken);
}

/// <summary>
/// One exact submit request retained across process loss. The duplicated scope and parent fields
/// let the client reject a validly encoded request when it no longer belongs to the authorized
/// local journal or to the remote ancestor from which it was created.
/// </summary>
public sealed record CortexSyncPendingAttemptV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string AttemptId,
    string ProjectLinkId,
    string ProjectId,
    string StoreId,
    string StoreEpoch,
    string BundleId,
    string BundleHash,
    long FirstSequence,
    long LastSequence,
    bool RemoteParentExists,
    long RemoteParentHighWaterSequence,
    string? RemoteParentLastEventId,
    string RemoteParentEventHash,
    string? RemoteParentLastBundleId,
    string RemoteParentBundleHash,
    string? RemoteParentAuthorityHash,
    string? RemoteParentPayloadHash,
    string? RemoteParentServerRevision,
    DateTimeOffset CreatedAt,
    int CanonicalRequestBytes,
    string CanonicalRequestSha256,
    string CanonicalRequestBase64,
    string AttemptHash);

public sealed record CortexSyncPendingSubmitAttempt(
    CortexSyncPendingAttemptV1 Evidence,
    CortexSyncSubmitRequest Request,
    ReadOnlyMemory<byte> CanonicalRequestBytes);

public sealed record CortexSyncPendingAttemptSaveResult(
    string AttemptHash,
    bool DurableForReplay);

/// <summary>
/// Coordinated 1.1 candidate store. Save may report success only after the active attempt's file
/// contents and promotion are crash durable and available to a new process.
/// </summary>
public interface ICortexSyncPendingAttemptStoreV1_1
{
    ValueTask<CortexSyncPendingSubmitAttempt?> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<CortexSyncPendingAttemptSaveResult> SaveAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken);

    ValueTask RetireAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken);
}

public sealed record CortexSyncClientOptions(
    string TenantIdHint,
    string NamespaceHint,
    long ObservedPolicyRevision,
    int MaximumEventsPerBundle = 64,
    TimeSpan? SafetyPollInterval = null,
    TimeSpan? StableFileDebounce = null)
{
    public TimeSpan EffectiveSafetyPollInterval => SafetyPollInterval ?? TimeSpan.FromSeconds(60);
    public TimeSpan EffectiveStableFileDebounce => StableFileDebounce ?? TimeSpan.FromMilliseconds(500);
}

public enum CortexSyncRunDisposition
{
    Synchronized,
    Unchanged,
    RemoteDrift,
    Conflict,
    Rejected
}

public sealed record CortexSyncRunResult(
    CortexSyncRunDisposition Disposition,
    string DiagnosticCode,
    string Message,
    string? AuthorityHash = null,
    string? ReceiptId = null,
    bool Retryable = false)
{
    public bool Accepted => Disposition is CortexSyncRunDisposition.Synchronized or CortexSyncRunDisposition.Unchanged;
}

public enum CortexSyncWakeReason
{
    FileHint,
    SafetyPoll
}

public interface ICortexSyncChangeSignalSource : IAsyncDisposable
{
    ValueTask<CortexSyncWakeReason> WaitAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken);
}
