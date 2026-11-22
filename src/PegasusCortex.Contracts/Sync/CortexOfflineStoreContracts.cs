using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Stable local-store identity and layout. Queue progress is deliberately absent: it is
/// reconstructed from immutable bundles and receipts so a damaged mutable watermark cannot
/// silently discard work.
/// </summary>
public sealed record CortexLocalStoreManifestV1
{
    [JsonConstructor]
    public CortexLocalStoreManifestV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string storeId,
        string storeEpoch,
        string projectId,
        string projectDisplayName,
        DateTimeOffset createdAt,
        string stateRootRelativePath,
        string bundleDirectory,
        string receiptDirectory,
        string quarantineDirectory,
        bool payloadProtectionRequired,
        string sourceKind,
        string? sourceMigrationId,
        string contentTreatment,
        bool executable,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string manifestHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.LocalStoreManifest, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireText(projectDisplayName, CortexSyncContractLimits.MaximumDisplayNameCharacters, nameof(projectDisplayName));
        CortexSyncContractShapes.RequireUtc(createdAt, nameof(createdAt));
        CortexSyncContractShapes.RequireRelativePath(stateRootRelativePath, nameof(stateRootRelativePath));
        CortexSyncContractShapes.RequireRelativePath(bundleDirectory, nameof(bundleDirectory));
        CortexSyncContractShapes.RequireRelativePath(receiptDirectory, nameof(receiptDirectory));
        CortexSyncContractShapes.RequireRelativePath(quarantineDirectory, nameof(quarantineDirectory));
        CortexSyncContractShapes.RequireHash(manifestHash, nameof(manifestHash));
        var directoryPrefix = stateRootRelativePath.TrimEnd('/', '\\') + "/";
        if (!payloadProtectionRequired || sourceKind is not ("new" or "legacy_copy") ||
            (sourceKind == "new" && sourceMigrationId is not null) ||
            (sourceKind == "legacy_copy" && !CortexSyncContractShapes.IsIdentifier(sourceMigrationId)) ||
            new[] { bundleDirectory, receiptDirectory, quarantineDirectory }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            new[] { bundleDirectory, receiptDirectory, quarantineDirectory }
                .Any(path => !path.Replace('\\', '/').StartsWith(directoryPrefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) || executable)
        {
            throw new ArgumentException("The local-store safety or source declaration is invalid.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        ProjectDisplayName = projectDisplayName;
        CreatedAt = createdAt;
        StateRootRelativePath = stateRootRelativePath;
        BundleDirectory = bundleDirectory;
        ReceiptDirectory = receiptDirectory;
        QuarantineDirectory = quarantineDirectory;
        PayloadProtectionRequired = payloadProtectionRequired;
        SourceKind = sourceKind;
        SourceMigrationId = sourceMigrationId;
        ContentTreatment = contentTreatment;
        Executable = executable;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        ManifestHash = manifestHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string ProjectDisplayName { get; }
    public DateTimeOffset CreatedAt { get; }
    public string StateRootRelativePath { get; }
    public string BundleDirectory { get; }
    public string ReceiptDirectory { get; }
    public string QuarantineDirectory { get; }
    public bool PayloadProtectionRequired { get; }
    public string SourceKind { get; }
    public string? SourceMigrationId { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string ManifestHash { get; }
}

/// <summary>
/// Non-authoritative provenance captured before enqueue. Files use a stable source-root ID plus
/// a platform-neutral relative path; local absolute paths and usernames never enter wire events.
/// </summary>
public sealed record CortexSyncSourceProvenanceV1
{
    public CortexSyncSourceProvenanceV1(
        string sourceKind,
        string? sourceRootId,
        string? sourceRelativePath,
        long sourceBytes,
        string sourceSha256,
        DateTimeOffset capturedAt)
    {
        if (sourceKind is not ("client_operation" or "legacy_file_copy") || sourceBytes < 0)
            throw new ArgumentException("The source provenance kind or size is invalid.");
        if (sourceKind == "legacy_file_copy")
        {
            CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
            CortexSyncContractShapes.RequireRelativePath(sourceRelativePath, nameof(sourceRelativePath));
        }
        else if (sourceRootId is not null || sourceRelativePath is not null)
        {
            throw new ArgumentException("Client-operation provenance must not claim a filesystem source.");
        }
        CortexSyncContractShapes.RequireHash(sourceSha256, nameof(sourceSha256));
        CortexSyncContractShapes.RequireUtc(capturedAt, nameof(capturedAt));
        SourceKind = sourceKind;
        SourceRootId = sourceRootId;
        SourceRelativePath = sourceRelativePath;
        SourceBytes = sourceBytes;
        SourceSha256 = sourceSha256;
        CapturedAt = capturedAt;
    }

    public string SourceKind { get; }
    public string? SourceRootId { get; }
    public string? SourceRelativePath { get; }
    public long SourceBytes { get; }
    public string SourceSha256 { get; }
    public DateTimeOffset CapturedAt { get; }
}

/// <summary>
/// One immutable, idempotent client intent. Tenant/namespace and observed policy values are
/// hints for routing and conflict detection only; the server re-derives authority on upload.
/// </summary>
public sealed record CortexOfflineEventV1
{
    [JsonConstructor]
    public CortexOfflineEventV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string storeId,
        string storeEpoch,
        string projectId,
        long sequence,
        string eventId,
        string previousEventHash,
        DateTimeOffset occurredAt,
        string producerInstanceId,
        string operationId,
        string tenantIdHint,
        string namespaceHint,
        long? observedPolicyRevision,
        string payloadContractId,
        string payloadBase64Url,
        int payloadBytes,
        string payloadSha256,
        CortexSyncSourceProvenanceV1 source,
        string contentTreatment,
        bool executable,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string eventHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.OfflineEvent, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireIdentifier(eventId, nameof(eventId));
        CortexSyncContractShapes.RequireHash(previousEventHash, nameof(previousEventHash));
        CortexSyncContractShapes.RequireUtc(occurredAt, nameof(occurredAt));
        CortexSyncContractShapes.RequireIdentifier(producerInstanceId, nameof(producerInstanceId));
        CortexSyncContractShapes.RequireIdentifier(operationId, nameof(operationId));
        CortexSyncContractShapes.RequireIdentifier(tenantIdHint, nameof(tenantIdHint));
        CortexSyncContractShapes.RequireNamespace(namespaceHint, nameof(namespaceHint));
        CortexSyncContractShapes.RequireContractId(payloadContractId, nameof(payloadContractId));
        CortexSyncContractShapes.RequireEncodedBytes(
            payloadBase64Url,
            payloadBytes,
            CortexSyncContractLimits.MaximumEventPayloadBytes,
            nameof(payloadBase64Url));
        CortexSyncContractShapes.RequireHash(payloadSha256, nameof(payloadSha256));
        CortexSyncContractShapes.RequireHash(eventHash, nameof(eventHash));
        if (sequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            observedPolicyRevision is <= 0 || source is null || source.CapturedAt > occurredAt ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) || executable)
        {
            throw new ArgumentException("The offline event is invalid or carries unsafe semantics.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        Sequence = sequence;
        EventId = eventId;
        PreviousEventHash = previousEventHash;
        OccurredAt = occurredAt;
        ProducerInstanceId = producerInstanceId;
        OperationId = operationId;
        TenantIdHint = tenantIdHint;
        NamespaceHint = namespaceHint;
        ObservedPolicyRevision = observedPolicyRevision;
        PayloadContractId = payloadContractId;
        PayloadBase64Url = payloadBase64Url;
        PayloadBytes = payloadBytes;
        PayloadSha256 = payloadSha256;
        Source = source;
        ContentTreatment = contentTreatment;
        Executable = executable;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        EventHash = eventHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public long Sequence { get; }
    public string EventId { get; }
    public string PreviousEventHash { get; }
    public DateTimeOffset OccurredAt { get; }
    public string ProducerInstanceId { get; }
    public string OperationId { get; }
    public string TenantIdHint { get; }
    public string NamespaceHint { get; }
    public long? ObservedPolicyRevision { get; }
    public string PayloadContractId { get; }
    public string PayloadBase64Url { get; }
    public int PayloadBytes { get; }
    public string PayloadSha256 { get; }
    public CortexSyncSourceProvenanceV1 Source { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string EventHash { get; }
}

/// <summary>
/// An append-only batch with both event and bundle hash chains. BaseServerRevision is an
/// optimistic-concurrency observation, never a grant or an instruction to trust local state.
/// </summary>
public sealed record CortexOfflineBundleV1
{
    [JsonConstructor]
    public CortexOfflineBundleV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string bundleId,
        string storeId,
        string storeEpoch,
        string projectId,
        string? baseServerRevision,
        string previousBundleHash,
        string priorEventHash,
        long firstSequence,
        long lastSequence,
        DateTimeOffset createdAt,
        IReadOnlyList<CortexOfflineEventV1> events,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string bundleHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.OfflineBundle, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(bundleId, nameof(bundleId));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        if (baseServerRevision is not null)
            CortexSyncContractShapes.RequireIdentifier(baseServerRevision, nameof(baseServerRevision));
        CortexSyncContractShapes.RequireHash(previousBundleHash, nameof(previousBundleHash));
        CortexSyncContractShapes.RequireHash(priorEventHash, nameof(priorEventHash));
        CortexSyncContractShapes.RequireUtc(createdAt, nameof(createdAt));
        CortexSyncContractShapes.RequireHash(bundleHash, nameof(bundleHash));

        var capturedEvents = CortexSyncContractShapes.SnapshotList(
            events,
            CortexSyncContractLimits.MaximumEventsPerBundle,
            nameof(events),
            requireNonEmpty: true);
        if (firstSequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            lastSequence < firstSequence || lastSequence > CortexSyncContractLimits.MaximumSequence ||
            capturedEvents.Count != lastSequence - firstSequence + 1 ||
            capturedEvents.Sum(item => (long)item.PayloadBytes) > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
            !MatchesChain(capturedEvents, storeId, storeEpoch, projectId, firstSequence, priorEventHash))
        {
            throw new ArgumentException("The offline bundle sequence, scope, or event chain is inconsistent.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        BundleId = bundleId;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        BaseServerRevision = baseServerRevision;
        PreviousBundleHash = previousBundleHash;
        PriorEventHash = priorEventHash;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
        CreatedAt = createdAt;
        Events = capturedEvents;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        BundleHash = bundleHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string BundleId { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string? BaseServerRevision { get; }
    public string PreviousBundleHash { get; }
    public string PriorEventHash { get; }
    public long FirstSequence { get; }
    public long LastSequence { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<CortexOfflineEventV1> Events { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string BundleHash { get; }

    private static bool MatchesChain(
        IReadOnlyList<CortexOfflineEventV1> events,
        string storeId,
        string storeEpoch,
        string projectId,
        long firstSequence,
        string priorEventHash)
    {
        var expectedPreviousHash = priorEventHash;
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (!string.Equals(item.StoreId, storeId, StringComparison.Ordinal) ||
                !string.Equals(item.StoreEpoch, storeEpoch, StringComparison.Ordinal) ||
                !string.Equals(item.ProjectId, projectId, StringComparison.Ordinal) ||
                item.Sequence != firstSequence + index ||
                !string.Equals(item.PreviousEventHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return false;
            }
            expectedPreviousHash = item.EventHash;
        }
        return true;
    }
}
