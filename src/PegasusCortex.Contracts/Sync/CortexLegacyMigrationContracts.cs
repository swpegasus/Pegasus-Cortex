using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

public sealed record CortexLegacyMigrationFileV1
{
    public CortexLegacyMigrationFileV1(
        string role,
        string exactSourcePath,
        string sourceRelativePath,
        long? sourceBytes,
        string? sourceSha256,
        string disposition,
        string lifecycleState,
        string reasonCode,
        string reason,
        IReadOnlyList<string> mappedIds,
        IReadOnlyList<string> mappedPayloadContractIds,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<string> receiptIds)
    {
        CortexSyncContractShapes.RequireIdentifier(role, nameof(role));
        CortexSyncContractShapes.RequireExactSourcePath(exactSourcePath, nameof(exactSourcePath));
        CortexSyncContractShapes.RequireRelativePath(sourceRelativePath, nameof(sourceRelativePath));
        CortexSyncContractShapes.RequireIdentifier(reasonCode, nameof(reasonCode));
        CortexSyncContractShapes.RequireText(reason, CortexSyncContractLimits.MaximumReasonCharacters, nameof(reason));
        if (disposition is not (
                CortexLegacyFileDispositionIds.DiscoveredUnmapped or
                CortexLegacyFileDispositionIds.MappedStructured or
                CortexLegacyFileDispositionIds.CopiedVerbatim or
                CortexLegacyFileDispositionIds.PreservedUnsupported or
                CortexLegacyFileDispositionIds.ExcludedSensitive or
                CortexLegacyFileDispositionIds.RejectedUnsafe or
                CortexLegacyFileDispositionIds.MissingDeclared))
        {
            throw new ArgumentException("The legacy-file disposition is unsupported.", nameof(disposition));
        }
        if (lifecycleState is not (
                CortexLegacyFileLifecycleIds.Discovered or CortexLegacyFileLifecycleIds.Copied or
                CortexLegacyFileLifecycleIds.Validated or CortexLegacyFileLifecycleIds.Uploaded or
                CortexLegacyFileLifecycleIds.ReadBack or CortexLegacyFileLifecycleIds.Reconciled or
                CortexLegacyFileLifecycleIds.Conflict or CortexLegacyFileLifecycleIds.Rejected))
        {
            throw new ArgumentException("The legacy-file lifecycle state is unsupported.", nameof(lifecycleState));
        }

        var mapped = CortexSyncContractShapes.SnapshotIdentifierSet(
            mappedIds,
            CortexSyncContractLimits.MaximumMappedIdsPerFile,
            nameof(mappedIds));
        var payloadContracts = CortexSyncContractShapes.SnapshotContractIdSet(
            mappedPayloadContractIds,
            CortexSyncContractLimits.MaximumContractIds,
            nameof(mappedPayloadContractIds));
        var evidence = CortexSyncContractShapes.SnapshotIdentifierSet(
            evidenceIds,
            CortexSyncContractLimits.MaximumMigrationEvidenceIdsPerFile,
            nameof(evidenceIds));
        var receipts = CortexSyncContractShapes.SnapshotIdentifierSet(
            receiptIds,
            CortexSyncContractLimits.MaximumMigrationReceiptIdsPerFile,
            nameof(receiptIds));
        var sourceIsUnavailable = disposition is CortexLegacyFileDispositionIds.RejectedUnsafe or CortexLegacyFileDispositionIds.MissingDeclared;
        var sourceIsExcluded = disposition == CortexLegacyFileDispositionIds.ExcludedSensitive;
        var sourceIsMapped = disposition is CortexLegacyFileDispositionIds.MappedStructured or
            CortexLegacyFileDispositionIds.CopiedVerbatim or CortexLegacyFileDispositionIds.PreservedUnsupported;
        var isDiscovered = disposition == CortexLegacyFileDispositionIds.DiscoveredUnmapped;
        var requiresEvidence = lifecycleState is CortexLegacyFileLifecycleIds.Copied or
            CortexLegacyFileLifecycleIds.Validated or CortexLegacyFileLifecycleIds.Uploaded or
            CortexLegacyFileLifecycleIds.ReadBack or CortexLegacyFileLifecycleIds.Reconciled or
            CortexLegacyFileLifecycleIds.Conflict;
        var requiresReceipt = lifecycleState is CortexLegacyFileLifecycleIds.Uploaded or
            CortexLegacyFileLifecycleIds.ReadBack or CortexLegacyFileLifecycleIds.Reconciled or
            CortexLegacyFileLifecycleIds.Conflict;
        if ((sourceIsUnavailable && (sourceBytes is not null || sourceSha256 is not null || mapped.Count != 0 || payloadContracts.Count != 0)) ||
            (!sourceIsUnavailable && (sourceBytes is null or < 0 || !CortexSyncContractShapes.IsHash(sourceSha256))) ||
            (sourceIsMapped && (mapped.Count == 0 || payloadContracts.Count == 0)) ||
            (!sourceIsMapped && (mapped.Count != 0 || payloadContracts.Count != 0)) ||
            (isDiscovered != (lifecycleState == CortexLegacyFileLifecycleIds.Discovered)) ||
            (sourceIsUnavailable && lifecycleState != CortexLegacyFileLifecycleIds.Rejected) ||
            (sourceIsExcluded && lifecycleState != CortexLegacyFileLifecycleIds.Rejected) ||
            (requiresEvidence && evidence.Count == 0) || (requiresReceipt && receipts.Count == 0) ||
            (lifecycleState == CortexLegacyFileLifecycleIds.Discovered && receipts.Count != 0))
        {
            throw new ArgumentException("The legacy-file provenance and mapped identifiers do not match its disposition.");
        }

        Role = role;
        ExactSourcePath = exactSourcePath;
        SourceRelativePath = sourceRelativePath;
        SourceBytes = sourceBytes;
        SourceSha256 = sourceSha256;
        Disposition = disposition;
        LifecycleState = lifecycleState;
        ReasonCode = reasonCode;
        Reason = reason;
        MappedIds = mapped;
        MappedPayloadContractIds = payloadContracts;
        EvidenceIds = evidence;
        ReceiptIds = receipts;
    }

    public string Role { get; }
    public string ExactSourcePath { get; }
    public string SourceRelativePath { get; }
    public long? SourceBytes { get; }
    public string? SourceSha256 { get; }
    public string Disposition { get; }
    public string LifecycleState { get; }
    public string ReasonCode { get; }
    public string Reason { get; }
    public IReadOnlyList<string> MappedIds { get; }
    public IReadOnlyList<string> MappedPayloadContractIds { get; }
    public IReadOnlyList<string> EvidenceIds { get; }
    public IReadOnlyList<string> ReceiptIds { get; }
}

/// <summary>
/// Copy-first inventory for the permissive Codex Cortex 0.2 filesystem. It records exact local
/// provenance and dispositions without changing the source. No value in this manifest grants
/// cleanup or cutover; both require later read-back reconciliation and separate user approval.
/// </summary>
public sealed record CortexLegacyMigrationManifestV1
{
    [JsonConstructor]
    public CortexLegacyMigrationManifestV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string migrationId,
        string storeId,
        string storeEpoch,
        string projectId,
        string sourceRootId,
        string sourceSchemaVersion,
        string sourcePathStyle,
        bool sourcePathCaseSensitive,
        string sourceProjectRootExact,
        string sourceMetaPathExact,
        long sourceMetaBytes,
        string sourceMetaSha256,
        DateTimeOffset capturedAt,
        long manifestRevision,
        string previousManifestHash,
        IReadOnlyList<CortexLegacyMigrationFileV1> files,
        string copyMode,
        bool sourceMutationAllowed,
        string cleanupState,
        string cutoverState,
        bool reconciliationRequired,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string manifestHash)
    {
        CortexSyncContractShapes.RequireHeader(schemaVersion, contractId, CortexSyncContractIds.LegacyMigrationManifest, canonicalization);
        CortexSyncContractShapes.RequireIdentifier(migrationId, nameof(migrationId));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
        CortexSyncContractShapes.RequireExactSourcePath(sourceProjectRootExact, nameof(sourceProjectRootExact));
        CortexSyncContractShapes.RequireExactSourcePath(sourceMetaPathExact, nameof(sourceMetaPathExact));
        CortexSyncContractShapes.RequireHash(sourceMetaSha256, nameof(sourceMetaSha256));
        CortexSyncContractShapes.RequireUtc(capturedAt, nameof(capturedAt));
        CortexSyncContractShapes.RequireHash(previousManifestHash, nameof(previousManifestHash));
        CortexSyncContractShapes.RequireHash(manifestHash, nameof(manifestHash));
        if (sourcePathStyle is not ("windows" or "posix"))
            throw new ArgumentException("The captured source path style is unsupported.", nameof(sourcePathStyle));
        var capturedFiles = CortexSyncContractShapes.SnapshotList(
            files,
            CortexSyncContractLimits.MaximumMigrationFiles,
            nameof(files),
            requireNonEmpty: true);
        var sourcePathComparer = sourcePathCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        if (!string.Equals(sourceSchemaVersion, CortexSyncContractIds.LegacyCortexSchemaVersion, StringComparison.Ordinal) ||
            manifestRevision is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            (manifestRevision == 1) != string.Equals(previousManifestHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal) ||
            sourceMetaBytes < 1 || copyMode != "copy_first" || sourceMutationAllowed ||
            cleanupState != "prohibited_until_separate_approval" || cutoverState != "not_approved" ||
            !reconciliationRequired ||
            capturedFiles.Select(item => item.SourceRelativePath)
                .Distinct(sourcePathComparer).Count() != capturedFiles.Count ||
            !capturedFiles.Any(item =>
                sourcePathComparer.Equals(item.ExactSourcePath, sourceMetaPathExact) &&
                item.SourceBytes == sourceMetaBytes && string.Equals(item.SourceSha256, sourceMetaSha256, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The legacy migration manifest is not copy-first, complete, or internally consistent.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        MigrationId = migrationId;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        SourceRootId = sourceRootId;
        SourceSchemaVersion = sourceSchemaVersion;
        SourcePathStyle = sourcePathStyle;
        SourcePathCaseSensitive = sourcePathCaseSensitive;
        SourceProjectRootExact = sourceProjectRootExact;
        SourceMetaPathExact = sourceMetaPathExact;
        SourceMetaBytes = sourceMetaBytes;
        SourceMetaSha256 = sourceMetaSha256;
        CapturedAt = capturedAt;
        ManifestRevision = manifestRevision;
        PreviousManifestHash = previousManifestHash;
        Files = capturedFiles;
        CopyMode = copyMode;
        SourceMutationAllowed = sourceMutationAllowed;
        CleanupState = cleanupState;
        CutoverState = cutoverState;
        ReconciliationRequired = reconciliationRequired;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        ManifestHash = manifestHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string MigrationId { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string SourceRootId { get; }
    public string SourceSchemaVersion { get; }
    public string SourcePathStyle { get; }
    public bool SourcePathCaseSensitive { get; }
    public string SourceProjectRootExact { get; }
    public string SourceMetaPathExact { get; }
    public long SourceMetaBytes { get; }
    public string SourceMetaSha256 { get; }
    public DateTimeOffset CapturedAt { get; }
    public long ManifestRevision { get; }
    public string PreviousManifestHash { get; }
    public IReadOnlyList<CortexLegacyMigrationFileV1> Files { get; }
    public string CopyMode { get; }
    public bool SourceMutationAllowed { get; }
    public string CleanupState { get; }
    public string CutoverState { get; }
    public bool ReconciliationRequired { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string ManifestHash { get; }
}
