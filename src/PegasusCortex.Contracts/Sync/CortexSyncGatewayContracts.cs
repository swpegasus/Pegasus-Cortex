using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Exact correlation key for one authenticated synchronization head. Tenant
/// and namespace are still derived by the Nexus gateway; the caller-supplied
/// project link is only a lookup key and never grants access.
/// </summary>
public sealed record CortexSyncHeadRequest
{
    public CortexSyncHeadRequest(
        string projectLinkId,
        string storeId,
        string storeEpoch,
        string projectId)
        : this(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncGatewayHeadRequest,
            CortexSyncContractIds.Canonicalization,
            projectLinkId,
            storeId,
            storeEpoch,
            projectId)
    {
    }

    [JsonConstructor]
    public CortexSyncHeadRequest(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string projectLinkId,
        string storeId,
        string storeEpoch,
        string projectId)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.SyncGatewayHeadRequest,
            canonicalization);
        RequireProjectLink(projectLinkId, nameof(projectLinkId));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ProjectLinkId = projectLinkId;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ProjectLinkId { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }

    internal static void RequireProjectLink(string? value, string parameterName)
    {
        if (value is not { Length: 70 } ||
            !value.StartsWith("plink_", StringComparison.Ordinal) ||
            !value.Skip(6).All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "An exact project-link identifier is required.",
                parameterName);
        }
    }
}

/// <summary>
/// Hash-bound projection of the last fully materialized current-recovery
/// event. A missing head still echoes the exact project/store epoch scope but
/// carries no journal, retention, brief, or server-revision evidence.
/// </summary>
public sealed record CortexSyncRemoteHead
{
    [JsonConstructor]
    public CortexSyncRemoteHead(
        int schemaVersion,
        string contractId,
        string canonicalization,
        bool exists,
        string projectLinkId,
        string storeId,
        string storeEpoch,
        string projectId,
        long highWaterSequence,
        string? lastEventId,
        string lastEventHash,
        string? lastBundleId,
        string lastBundleHash,
        string? authorityHash,
        string? retainedPayloadHash,
        long retainedSnapshotRevision,
        string? retainedSnapshotHash,
        string? contextSetId,
        string? serverRevision,
        DateTimeOffset observedAt,
        string headHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.SyncGatewayHead,
            canonicalization);
        CortexSyncHeadRequest.RequireProjectLink(projectLinkId, nameof(projectLinkId));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireUtc(observedAt, nameof(observedAt));
        CortexSyncContractShapes.RequireHash(lastEventHash, nameof(lastEventHash));
        CortexSyncContractShapes.RequireHash(lastBundleHash, nameof(lastBundleHash));
        CortexSyncContractShapes.RequireHash(headHash, nameof(headHash));
        if (highWaterSequence is < 0 or > CortexSyncContractLimits.MaximumSequence ||
            retainedSnapshotRevision is < 0 or > CortexSyncContractLimits.MaximumSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(highWaterSequence));
        }

        if (exists)
        {
            CortexSyncContractShapes.RequireIdentifier(lastEventId, nameof(lastEventId));
            CortexSyncContractShapes.RequireIdentifier(lastBundleId, nameof(lastBundleId));
            CortexSyncContractShapes.RequireHash(authorityHash, nameof(authorityHash));
            CortexSyncContractShapes.RequireHash(retainedPayloadHash, nameof(retainedPayloadHash));
            CortexSyncContractShapes.RequireHash(retainedSnapshotHash, nameof(retainedSnapshotHash));
            CortexSyncContractShapes.RequireIdentifier(contextSetId, nameof(contextSetId));
            CortexSyncContractShapes.RequireIdentifier(serverRevision, nameof(serverRevision));
            if (highWaterSequence < 1 || retainedSnapshotRevision < 1 ||
                string.Equals(lastEventHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal) ||
                string.Equals(lastBundleHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("An existing synchronization head is incomplete.");
            }
        }
        else if (lastEventId is not null || lastBundleId is not null ||
                 authorityHash is not null || retainedPayloadHash is not null ||
                 retainedSnapshotHash is not null || contextSetId is not null ||
                 serverRevision is not null || highWaterSequence != 0 ||
                 retainedSnapshotRevision != 0 ||
                 !string.Equals(lastEventHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal) ||
                 !string.Equals(lastBundleHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("A missing synchronization head must use the canonical empty shape.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        Exists = exists;
        ProjectLinkId = projectLinkId;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        HighWaterSequence = highWaterSequence;
        LastEventId = lastEventId;
        LastEventHash = lastEventHash;
        LastBundleId = lastBundleId;
        LastBundleHash = lastBundleHash;
        AuthorityHash = authorityHash;
        RetainedPayloadHash = retainedPayloadHash;
        RetainedSnapshotRevision = retainedSnapshotRevision;
        RetainedSnapshotHash = retainedSnapshotHash;
        ContextSetId = contextSetId;
        ServerRevision = serverRevision;
        ObservedAt = observedAt;
        HeadHash = headHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public bool Exists { get; }
    public string ProjectLinkId { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public long HighWaterSequence { get; }
    public string? LastEventId { get; }
    public string LastEventHash { get; }
    public string? LastBundleId { get; }
    public string LastBundleHash { get; }
    public string? AuthorityHash { get; }
    public string? RetainedPayloadHash { get; }
    public long RetainedSnapshotRevision { get; }
    public string? RetainedSnapshotHash { get; }
    public string? ContextSetId { get; }
    public string? ServerRevision { get; }
    public DateTimeOffset ObservedAt { get; }
    public string HeadHash { get; }
}

/// <summary>
/// Hash-bound non-MCP submit envelope. The project link is a correlation key;
/// tenant and namespace continue to come only from authenticated server state.
/// </summary>
public sealed record CortexSyncSubmitRequest
{
    public CortexSyncSubmitRequest(
        string projectLinkId,
        CortexOfflineBundleV1 bundle)
        : this(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncGatewaySubmitRequest,
            CortexSyncContractIds.Canonicalization,
            projectLinkId,
            bundle,
            CortexSyncContractIds.EmptySha256)
    {
    }

    [JsonConstructor]
    public CortexSyncSubmitRequest(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string projectLinkId,
        CortexOfflineBundleV1 bundle,
        string requestHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.SyncGatewaySubmitRequest,
            canonicalization);
        CortexSyncHeadRequest.RequireProjectLink(projectLinkId, nameof(projectLinkId));
        Bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        CortexSyncContractShapes.RequireHash(requestHash, nameof(requestHash));
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ProjectLinkId = projectLinkId;
        RequestHash = requestHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ProjectLinkId { get; }
    public CortexOfflineBundleV1 Bundle { get; }
    public string RequestHash { get; }
}

/// <summary>
/// Non-MCP synchronization result returned only after the source bundle,
/// retained task snapshots, project brief, and receipt-backed published head
/// have all been independently read back as exact durable evidence.
/// </summary>
public sealed record CortexSyncSubmitResult
{
    [JsonConstructor]
    public CortexSyncSubmitResult(
        int schemaVersion,
        string contractId,
        string canonicalization,
        CortexSyncReceiptV1 receipt,
        CortexSyncRemoteHead head,
        string resultHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.SyncGatewaySubmitResult,
            canonicalization);
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        Head = head ?? throw new ArgumentNullException(nameof(head));
        CortexSyncContractShapes.RequireHash(resultHash, nameof(resultHash));
        if (!head.Exists ||
            !string.Equals(receipt.StoreId, head.StoreId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreEpoch, head.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProjectId, head.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleId, head.LastBundleId, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleHash, head.LastBundleHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.Durability.StorageRevision, head.ServerRevision, StringComparison.Ordinal))
        {
            throw new ArgumentException("The synchronization submit result does not bind one exact receipt and head.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ResultHash = resultHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public CortexSyncReceiptV1 Receipt { get; }
    public CortexSyncRemoteHead Head { get; }
    public string ResultHash { get; }
}
