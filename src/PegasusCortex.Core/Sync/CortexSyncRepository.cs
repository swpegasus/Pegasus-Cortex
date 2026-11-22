using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Core.Sync;

public enum CortexSyncCommitDisposition
{
    Committed,
    Existing,
    Conflict
}

public enum CortexSyncReceiptAppendDisposition
{
    Appended,
    Existing,
    Conflict
}

/// <summary>
/// Exact server-authorized scope passed to persistence. It carries no caller-provided grants.
/// </summary>
public sealed record CortexSyncRepositoryScope(
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    string Operation);

public sealed record CortexSyncCommitRequest(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle,
    DateTimeOffset ReceivedAt);

public sealed class CortexSyncCommitSnapshot
{
    public CortexSyncCommitSnapshot(
        CortexSyncCommitDisposition disposition,
        string commitId,
        string storageRevision,
        DateTimeOffset receivedAt,
        DateTimeOffset committedAt,
        string durableStateHash,
        IEnumerable<CortexSyncReceiptOutcomeV1> outcomes)
    {
        Disposition = disposition;
        CommitId = commitId;
        StorageRevision = storageRevision;
        ReceivedAt = receivedAt;
        CommittedAt = committedAt;
        DurableStateHash = durableStateHash;
        Outcomes = CortexSyncApplicationValidation.SnapshotList(
            outcomes,
            CortexSyncContractLimits.MaximumReceiptOutcomes,
            nameof(outcomes),
            requireNonEmpty: true);
    }

    public CortexSyncCommitDisposition Disposition { get; }
    public string CommitId { get; }
    public string StorageRevision { get; }
    /// <summary>
    /// Durable receipt time for this exact attempt. On idempotent recovery it can predate the
    /// current request and must be reused so a prior public receipt remains byte-for-byte valid.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; }
    public DateTimeOffset CommittedAt { get; }
    public string DurableStateHash { get; }
    public IReadOnlyList<CortexSyncReceiptOutcomeV1> Outcomes { get; }
}

public sealed record CortexSyncReadBackRequest(
    CortexSyncRepositoryScope Scope,
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision);

/// <summary>
/// Immutable provider read-back evidence. Core compares every identity and the durable state
/// hash with the commit snapshot before marking the receipt reconciled.
/// </summary>
public sealed record CortexSyncReadBackSnapshot(
    string TenantId,
    string Namespace,
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision,
    int SourceEventCount,
    int MatchedEventCount,
    DateTimeOffset ReadBackAt,
    string ReadBackHash);

/// <summary>
/// Exact, already-verified public receipt evidence passed to persistence. The canonical bytes
/// are copied and checked against the receipt so a provider cannot silently choose a different
/// serialization or omit extension evidence.
/// </summary>
public sealed class CortexSyncReceiptAppendRequest
{
    private readonly byte[] _canonicalReceipt;

    public CortexSyncReceiptAppendRequest(
        CortexSyncRepositoryScope scope,
        CortexSyncReceiptV1 receipt,
        ReadOnlyMemory<byte> canonicalReceipt)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        _canonicalReceipt = canonicalReceipt.ToArray();
        if (_canonicalReceipt.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentOutOfRangeException(nameof(canonicalReceipt), "Canonical receipt evidence exceeds its bounded persistence contract.");
        if (!CortexCanonicalJson.Serialize(receipt).AsSpan().SequenceEqual(_canonicalReceipt))
            throw new ArgumentException("Canonical receipt evidence does not represent the supplied receipt.", nameof(canonicalReceipt));

        CanonicalSha256 = CortexCanonicalJson.Sha256(_canonicalReceipt);
    }

    public CortexSyncRepositoryScope Scope { get; }
    public CortexSyncReceiptV1 Receipt { get; }
    public ReadOnlyMemory<byte> CanonicalReceipt => _canonicalReceipt;
    public string CanonicalSha256 { get; }
}

/// <summary>
/// Provider acknowledgement that the exact terminal receipt row was appended or already
/// existed byte-for-byte. Conflict is fail-closed and never counts as accepted persistence.
/// </summary>
public sealed record CortexSyncReceiptAppendSnapshot(
    CortexSyncReceiptAppendDisposition Disposition,
    string TenantId,
    string Namespace,
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    string BundleId,
    string ReceiptId,
    string ReceiptHash,
    string CanonicalSha256,
    DateTimeOffset RecordedAt);

public sealed record CortexSyncReceiptReadRequest(
    CortexSyncRepositoryScope Scope,
    string BundleId,
    string ReceiptId,
    string ReceiptHash,
    string CanonicalSha256);

/// <summary>
/// Idempotent recovery key used before issuing a receipt. A provider returns the one canonical
/// receipt already bound to this exact bundle commit, or null when no receipt has been appended.
/// </summary>
public sealed record CortexSyncReceiptLookupRequest(
    CortexSyncRepositoryScope Scope,
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision);

/// <summary>
/// Independent read-back of the stored canonical receipt. Bytes are defensively copied so Core
/// can compare the complete authenticated public contract, including extensions, before success.
/// </summary>
public sealed class CortexSyncReceiptReadBackSnapshot
{
    private readonly byte[] _canonicalReceipt;

    public CortexSyncReceiptReadBackSnapshot(
        string tenantId,
        string @namespace,
        string storeId,
        string storeEpoch,
        string projectId,
        string bundleId,
        string receiptId,
        DateTimeOffset recordedAt,
        ReadOnlyMemory<byte> canonicalReceipt)
    {
        TenantId = tenantId;
        Namespace = @namespace;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        BundleId = bundleId;
        ReceiptId = receiptId;
        RecordedAt = recordedAt;
        _canonicalReceipt = canonicalReceipt.ToArray();
        if (_canonicalReceipt.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentOutOfRangeException(nameof(canonicalReceipt), "Canonical receipt read-back exceeds its bounded persistence contract.");
    }

    public string TenantId { get; }
    public string Namespace { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string BundleId { get; }
    public string ReceiptId { get; }
    public DateTimeOffset RecordedAt { get; }
    public ReadOnlyMemory<byte> CanonicalReceipt => _canonicalReceipt;
}

public interface ICortexSyncRepository
{
    ValueTask<CortexSyncCommitSnapshot> CommitAsync(
        CortexSyncCommitRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken);
}
