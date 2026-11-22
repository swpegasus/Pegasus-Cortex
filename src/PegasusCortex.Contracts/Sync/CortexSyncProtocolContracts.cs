using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

public sealed record CortexSyncLimitsV1
{
    public CortexSyncLimitsV1(
        int maximumEventPayloadBytes,
        int maximumEventsPerBundle,
        int maximumCanonicalBundleBytes,
        int maximumExtensions,
        int maximumExtensionPayloadBytes,
        int maximumReceiptOutcomes,
        int maximumMigrationFiles,
        int maximumJsonDepth)
    {
        if (maximumEventPayloadBytes is < 1 or > CortexSyncContractLimits.MaximumEventPayloadBytes ||
            maximumEventsPerBundle is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle ||
            maximumCanonicalBundleBytes < maximumEventPayloadBytes ||
            maximumCanonicalBundleBytes > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
            maximumExtensions is < 0 or > CortexSyncContractLimits.MaximumExtensions ||
            maximumExtensionPayloadBytes is < 1 or > CortexSyncContractLimits.MaximumExtensionPayloadBytes ||
            maximumReceiptOutcomes is < 1 or > CortexSyncContractLimits.MaximumReceiptOutcomes ||
            maximumMigrationFiles is < 1 or > CortexSyncContractLimits.MaximumMigrationFiles ||
            maximumJsonDepth is < 1 or > CortexSyncContractLimits.MaximumJsonDepth)
        {
            throw new ArgumentException("The advertised synchronization limits exceed the stable v1 hard bounds.");
        }

        MaximumEventPayloadBytes = maximumEventPayloadBytes;
        MaximumEventsPerBundle = maximumEventsPerBundle;
        MaximumCanonicalBundleBytes = maximumCanonicalBundleBytes;
        MaximumExtensions = maximumExtensions;
        MaximumExtensionPayloadBytes = maximumExtensionPayloadBytes;
        MaximumReceiptOutcomes = maximumReceiptOutcomes;
        MaximumMigrationFiles = maximumMigrationFiles;
        MaximumJsonDepth = maximumJsonDepth;
    }

    public int MaximumEventPayloadBytes { get; }
    public int MaximumEventsPerBundle { get; }
    public int MaximumCanonicalBundleBytes { get; }
    public int MaximumExtensions { get; }
    public int MaximumExtensionPayloadBytes { get; }
    public int MaximumReceiptOutcomes { get; }
    public int MaximumMigrationFiles { get; }
    public int MaximumJsonDepth { get; }
}

/// <summary>
/// Protocol support only. Capability identifiers and extension support never grant access;
/// authentication and namespace authorization remain server-derived on every upload.
/// </summary>
public sealed record CortexSyncCapabilitiesV1
{
    [JsonConstructor]
    public CortexSyncCapabilitiesV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string serviceVersion,
        IReadOnlyList<string> supportedContractIds,
        IReadOnlyList<string> acceptedPayloadContractIds,
        IReadOnlyList<string> supportedCapabilityIds,
        IReadOnlyList<string> requiredClientCapabilityIds,
        IReadOnlyList<string> supportedExtensionContractIds,
        IReadOnlyList<string> requiredClientExtensionContractIds,
        IReadOnlyList<string> supportedProtectionSuiteIds,
        IReadOnlyList<string> supportedReceiptProofContractIds,
        CortexSyncLimitsV1 limits,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string capabilitiesHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.SyncCapabilities, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(serviceVersion, nameof(serviceVersion));
        CortexSyncContractShapes.RequireHash(capabilitiesHash, nameof(capabilitiesHash));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ServiceVersion = serviceVersion;
        SupportedContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(supportedContractIds), true);
        AcceptedPayloadContractIds = CortexSyncContractShapes.SnapshotContractIdSet(acceptedPayloadContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(acceptedPayloadContractIds), true);
        SupportedCapabilityIds = CortexSyncContractShapes.SnapshotIdentifierSet(supportedCapabilityIds, CortexSyncContractLimits.MaximumCapabilityIds, nameof(supportedCapabilityIds), true);
        RequiredClientCapabilityIds = CortexSyncContractShapes.SnapshotIdentifierSet(requiredClientCapabilityIds, CortexSyncContractLimits.MaximumCapabilityIds, nameof(requiredClientCapabilityIds));
        SupportedExtensionContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedExtensionContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(supportedExtensionContractIds));
        RequiredClientExtensionContractIds = CortexSyncContractShapes.SnapshotContractIdSet(requiredClientExtensionContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(requiredClientExtensionContractIds));
        SupportedProtectionSuiteIds = CortexSyncContractShapes.SnapshotIdentifierSet(supportedProtectionSuiteIds, CortexSyncContractLimits.MaximumProtectionSuites, nameof(supportedProtectionSuiteIds), true);
        SupportedReceiptProofContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedReceiptProofContractIds, CortexSyncContractLimits.MaximumProtectionSuites, nameof(supportedReceiptProofContractIds), true);
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        CapabilitiesHash = capabilitiesHash;

        if (RequiredClientCapabilityIds.Except(SupportedCapabilityIds, StringComparer.Ordinal).Any() ||
            RequiredClientExtensionContractIds.Except(SupportedExtensionContractIds, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("A required client feature is not included in the server's supported set.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ServiceVersion { get; }
    public IReadOnlyList<string> SupportedContractIds { get; }
    public IReadOnlyList<string> AcceptedPayloadContractIds { get; }
    public IReadOnlyList<string> SupportedCapabilityIds { get; }
    public IReadOnlyList<string> RequiredClientCapabilityIds { get; }
    public IReadOnlyList<string> SupportedExtensionContractIds { get; }
    public IReadOnlyList<string> RequiredClientExtensionContractIds { get; }
    public IReadOnlyList<string> SupportedProtectionSuiteIds { get; }
    public IReadOnlyList<string> SupportedReceiptProofContractIds { get; }
    public CortexSyncLimitsV1 Limits { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string CapabilitiesHash { get; }
}

public sealed record CortexSyncCompatibilityRequestV1
{
    [JsonConstructor]
    public CortexSyncCompatibilityRequestV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string requestId,
        string clientVersion,
        IReadOnlyList<string> supportedContractIds,
        IReadOnlyList<string> requiredServerContractIds,
        IReadOnlyList<string> offeredPayloadContractIds,
        IReadOnlyList<string> supportedCapabilityIds,
        IReadOnlyList<string> requiredServerCapabilityIds,
        IReadOnlyList<string> supportedExtensionContractIds,
        IReadOnlyList<string> requiredServerExtensionContractIds,
        IReadOnlyList<string> supportedProtectionSuiteIds,
        IReadOnlyList<string> supportedReceiptProofContractIds,
        CortexSyncLimitsV1 limits,
        IReadOnlyList<CortexSyncExtensionV1> extensions)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.CompatibilityRequest, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(requestId, nameof(requestId));
        CortexSyncContractShapes.RequireIdentifier(clientVersion, nameof(clientVersion));
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        RequestId = requestId;
        ClientVersion = clientVersion;
        SupportedContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(supportedContractIds), true);
        RequiredServerContractIds = CortexSyncContractShapes.SnapshotContractIdSet(requiredServerContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(requiredServerContractIds), true);
        OfferedPayloadContractIds = CortexSyncContractShapes.SnapshotContractIdSet(offeredPayloadContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(offeredPayloadContractIds), true);
        SupportedCapabilityIds = CortexSyncContractShapes.SnapshotIdentifierSet(supportedCapabilityIds, CortexSyncContractLimits.MaximumCapabilityIds, nameof(supportedCapabilityIds), true);
        RequiredServerCapabilityIds = CortexSyncContractShapes.SnapshotIdentifierSet(requiredServerCapabilityIds, CortexSyncContractLimits.MaximumCapabilityIds, nameof(requiredServerCapabilityIds));
        SupportedExtensionContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedExtensionContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(supportedExtensionContractIds));
        RequiredServerExtensionContractIds = CortexSyncContractShapes.SnapshotContractIdSet(requiredServerExtensionContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(requiredServerExtensionContractIds));
        SupportedProtectionSuiteIds = CortexSyncContractShapes.SnapshotIdentifierSet(supportedProtectionSuiteIds, CortexSyncContractLimits.MaximumProtectionSuites, nameof(supportedProtectionSuiteIds), true);
        SupportedReceiptProofContractIds = CortexSyncContractShapes.SnapshotContractIdSet(supportedReceiptProofContractIds, CortexSyncContractLimits.MaximumProtectionSuites, nameof(supportedReceiptProofContractIds), true);
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        if (RequiredServerContractIds.Except(SupportedContractIds, StringComparer.Ordinal).Any() ||
            RequiredServerCapabilityIds.Except(SupportedCapabilityIds, StringComparer.Ordinal).Any() ||
            RequiredServerExtensionContractIds.Except(SupportedExtensionContractIds, StringComparer.Ordinal).Any() ||
            Extensions.Any(item => !SupportedExtensionContractIds.Contains(item.ContractId, StringComparer.Ordinal)))
        {
            throw new ArgumentException("A required server feature is not included in the client's supported set.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string RequestId { get; }
    public string ClientVersion { get; }
    public IReadOnlyList<string> SupportedContractIds { get; }
    public IReadOnlyList<string> RequiredServerContractIds { get; }
    public IReadOnlyList<string> OfferedPayloadContractIds { get; }
    public IReadOnlyList<string> SupportedCapabilityIds { get; }
    public IReadOnlyList<string> RequiredServerCapabilityIds { get; }
    public IReadOnlyList<string> SupportedExtensionContractIds { get; }
    public IReadOnlyList<string> RequiredServerExtensionContractIds { get; }
    public IReadOnlyList<string> SupportedProtectionSuiteIds { get; }
    public IReadOnlyList<string> SupportedReceiptProofContractIds { get; }
    public CortexSyncLimitsV1 Limits { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
}

public sealed record CortexSyncCompatibilityIssueV1
{
    public CortexSyncCompatibilityIssueV1(string code, string message)
    {
        CortexSyncContractShapes.RequireIdentifier(code, nameof(code));
        CortexSyncContractShapes.RequireText(message, CortexSyncContractLimits.MaximumDiagnosticMessageCharacters, nameof(message));
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}

public sealed record CortexSyncCompatibilityDecisionV1
{
    [JsonConstructor]
    public CortexSyncCompatibilityDecisionV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string requestId,
        bool accepted,
        IReadOnlyList<string> selectedPayloadContractIds,
        string? selectedProtectionSuiteId,
        string? selectedReceiptProofContractId,
        CortexSyncLimitsV1? effectiveLimits,
        IReadOnlyList<CortexSyncCompatibilityIssueV1> issues)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.CompatibilityDecision, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(requestId, nameof(requestId));
        var capturedPayloads = CortexSyncContractShapes.SnapshotContractIdSet(selectedPayloadContractIds, CortexSyncContractLimits.MaximumContractIds, nameof(selectedPayloadContractIds));
        var capturedIssues = CortexSyncContractShapes.SnapshotList(issues, CortexSyncContractLimits.MaximumCompatibilityIssues, nameof(issues));
        if (selectedProtectionSuiteId is not null)
            CortexSyncContractShapes.RequireIdentifier(selectedProtectionSuiteId, nameof(selectedProtectionSuiteId));
        if (selectedReceiptProofContractId is not null)
            CortexSyncContractShapes.RequireContractId(selectedReceiptProofContractId, nameof(selectedReceiptProofContractId));
        if (accepted != (capturedIssues.Count == 0) ||
            (accepted && (capturedPayloads.Count == 0 || selectedProtectionSuiteId is null ||
                          selectedReceiptProofContractId is null || effectiveLimits is null)) ||
            (!accepted && (capturedPayloads.Count != 0 || selectedProtectionSuiteId is not null ||
                           selectedReceiptProofContractId is not null || effectiveLimits is not null)))
        {
            throw new ArgumentException("The compatibility decision is internally inconsistent.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        RequestId = requestId;
        Accepted = accepted;
        SelectedPayloadContractIds = capturedPayloads;
        SelectedProtectionSuiteId = selectedProtectionSuiteId;
        SelectedReceiptProofContractId = selectedReceiptProofContractId;
        EffectiveLimits = effectiveLimits;
        Issues = capturedIssues;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string RequestId { get; }
    public bool Accepted { get; }
    public IReadOnlyList<string> SelectedPayloadContractIds { get; }
    public string? SelectedProtectionSuiteId { get; }
    public string? SelectedReceiptProofContractId { get; }
    public CortexSyncLimitsV1? EffectiveLimits { get; }
    public IReadOnlyList<CortexSyncCompatibilityIssueV1> Issues { get; }
}

public sealed record CortexSyncReceiptAuthenticationV1
{
    public CortexSyncReceiptAuthenticationV1(
        string proofContractId,
        string keyId,
        string algorithmId,
        string coveredReceiptHash,
        string proofBase64Url,
        int proofBytes,
        string proofSha256)
    {
        CortexSyncContractShapes.RequireContractId(proofContractId, nameof(proofContractId));
        CortexSyncContractShapes.RequireIdentifier(keyId, nameof(keyId));
        CortexSyncContractShapes.RequireIdentifier(algorithmId, nameof(algorithmId));
        CortexSyncContractShapes.RequireHash(coveredReceiptHash, nameof(coveredReceiptHash));
        CortexSyncContractShapes.RequireEncodedBytes(proofBase64Url, proofBytes, CortexSyncContractLimits.MaximumExtensionPayloadBytes, nameof(proofBase64Url));
        CortexSyncContractShapes.RequireHash(proofSha256, nameof(proofSha256));
        ProofContractId = proofContractId;
        KeyId = keyId;
        AlgorithmId = algorithmId;
        CoveredReceiptHash = coveredReceiptHash;
        ProofBase64Url = proofBase64Url;
        ProofBytes = proofBytes;
        ProofSha256 = proofSha256;
    }

    public string ProofContractId { get; }
    public string KeyId { get; }
    public string AlgorithmId { get; }
    public string CoveredReceiptHash { get; }
    public string ProofBase64Url { get; }
    public int ProofBytes { get; }
    public string ProofSha256 { get; }
}

public sealed record CortexSyncReceiptDurabilityV1
{
    public CortexSyncReceiptDurabilityV1(string commitId, string storageRevision, DateTimeOffset committedAt)
    {
        CortexSyncContractShapes.RequireIdentifier(commitId, nameof(commitId));
        CortexSyncContractShapes.RequireIdentifier(storageRevision, nameof(storageRevision));
        CortexSyncContractShapes.RequireUtc(committedAt, nameof(committedAt));
        CommitId = commitId;
        StorageRevision = storageRevision;
        CommittedAt = committedAt;
    }

    public string CommitId { get; }
    public string StorageRevision { get; }
    public DateTimeOffset CommittedAt { get; }
}

public sealed record CortexSyncReceiptOutcomeV1
{
    public CortexSyncReceiptOutcomeV1(
        long sequence,
        string eventId,
        string eventHash,
        string disposition,
        bool durable,
        string? serverEntityId,
        string? serverRevisionId,
        string? diagnosticCode)
    {
        if (sequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            disposition is not (CortexSyncDispositionIds.Applied or CortexSyncDispositionIds.Duplicate or
                CortexSyncDispositionIds.Conflict or CortexSyncDispositionIds.Rejected or CortexSyncDispositionIds.Quarantined) ||
            !durable)
        {
            throw new ArgumentException("Every receipt disposition must itself be durably recorded.");
        }
        CortexSyncContractShapes.RequireIdentifier(eventId, nameof(eventId));
        CortexSyncContractShapes.RequireHash(eventHash, nameof(eventHash));
        if (serverEntityId is not null)
            CortexSyncContractShapes.RequireIdentifier(serverEntityId, nameof(serverEntityId));
        if (serverRevisionId is not null)
            CortexSyncContractShapes.RequireIdentifier(serverRevisionId, nameof(serverRevisionId));
        if (diagnosticCode is not null)
            CortexSyncContractShapes.RequireIdentifier(diagnosticCode, nameof(diagnosticCode));
        Sequence = sequence;
        EventId = eventId;
        EventHash = eventHash;
        Disposition = disposition;
        Durable = durable;
        ServerEntityId = serverEntityId;
        ServerRevisionId = serverRevisionId;
        DiagnosticCode = diagnosticCode;
    }

    public long Sequence { get; }
    public string EventId { get; }
    public string EventHash { get; }
    public string Disposition { get; }
    public bool Durable { get; }
    public string? ServerEntityId { get; }
    public string? ServerRevisionId { get; }
    public string? DiagnosticCode { get; }
}

public sealed record CortexSyncReceiptReconciliationV1
{
    public CortexSyncReceiptReconciliationV1(
        string state,
        int sourceEventCount,
        int matchedEventCount,
        DateTimeOffset? readBackAt,
        string? readBackHash)
    {
        if (state is not ("pending" or "reconciled" or "mismatch") ||
            sourceEventCount is < 1 or > CortexSyncContractLimits.MaximumReceiptOutcomes ||
            matchedEventCount < 0 || matchedEventCount > sourceEventCount ||
            (state == "pending" && (matchedEventCount != 0 || readBackAt is not null || readBackHash is not null)) ||
            (state == "reconciled" && (matchedEventCount != sourceEventCount || readBackAt is null || readBackHash is null)) ||
            (state == "mismatch" && (readBackAt is null || readBackHash is null)))
        {
            throw new ArgumentException("The receipt reconciliation state is inconsistent.");
        }
        if (readBackAt is not null)
            CortexSyncContractShapes.RequireUtc(readBackAt.Value, nameof(readBackAt));
        if (readBackHash is not null)
            CortexSyncContractShapes.RequireHash(readBackHash, nameof(readBackHash));
        State = state;
        SourceEventCount = sourceEventCount;
        MatchedEventCount = matchedEventCount;
        ReadBackAt = readBackAt;
        ReadBackHash = readBackHash;
    }

    public string State { get; }
    public int SourceEventCount { get; }
    public int MatchedEventCount { get; }
    public DateTimeOffset? ReadBackAt { get; }
    public string? ReadBackHash { get; }
}

/// <summary>
/// Server-produced acknowledgement. The proof must be verified by a trusted host adapter;
/// merely finding this JSON file locally never authorizes pruning or cutover.
/// </summary>
public sealed record CortexSyncReceiptV1
{
    [JsonConstructor]
    public CortexSyncReceiptV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string receiptId,
        string serverInstanceId,
        string storeId,
        string storeEpoch,
        string projectId,
        string bundleId,
        string bundleHash,
        DateTimeOffset receivedAt,
        CortexSyncReceiptAuthenticationV1 authentication,
        CortexSyncReceiptDurabilityV1 durability,
        IReadOnlyList<CortexSyncReceiptOutcomeV1> outcomes,
        CortexSyncReceiptReconciliationV1 reconciliation,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string receiptHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.SyncReceipt, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(receiptId, nameof(receiptId));
        CortexSyncContractShapes.RequireIdentifier(serverInstanceId, nameof(serverInstanceId));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireIdentifier(bundleId, nameof(bundleId));
        CortexSyncContractShapes.RequireHash(bundleHash, nameof(bundleHash));
        CortexSyncContractShapes.RequireUtc(receivedAt, nameof(receivedAt));
        CortexSyncContractShapes.RequireHash(receiptHash, nameof(receiptHash));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        Durability = durability ?? throw new ArgumentNullException(nameof(durability));
        Reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        Outcomes = CortexSyncContractShapes.SnapshotList(outcomes, CortexSyncContractLimits.MaximumReceiptOutcomes, nameof(outcomes), true);
        if (!string.Equals(Authentication.CoveredReceiptHash, receiptHash, StringComparison.Ordinal) ||
            Outcomes.Select(item => item.Sequence).Distinct().Count() != Outcomes.Count ||
            Outcomes.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != Outcomes.Count ||
            Outcomes.Zip(Outcomes.Skip(1), (left, right) => right.Sequence == left.Sequence + 1).Any(result => !result) ||
            Reconciliation.SourceEventCount != Outcomes.Count)
        {
            throw new ArgumentException("Receipt outcomes must be unique, contiguous, and match reconciliation.");
        }
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ReceiptId = receiptId;
        ServerInstanceId = serverInstanceId;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        BundleId = bundleId;
        BundleHash = bundleHash;
        ReceivedAt = receivedAt;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        ReceiptHash = receiptHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ReceiptId { get; }
    public string ServerInstanceId { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string BundleId { get; }
    public string BundleHash { get; }
    public DateTimeOffset ReceivedAt { get; }
    public CortexSyncReceiptAuthenticationV1 Authentication { get; }
    public CortexSyncReceiptDurabilityV1 Durability { get; }
    public IReadOnlyList<CortexSyncReceiptOutcomeV1> Outcomes { get; }
    public CortexSyncReceiptReconciliationV1 Reconciliation { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string ReceiptHash { get; }
}
