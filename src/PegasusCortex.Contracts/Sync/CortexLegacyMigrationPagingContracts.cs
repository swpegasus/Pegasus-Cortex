using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Hash-bound resume position for a copy-first legacy migration. The cursor carries no
/// authority and may be advanced only after the preceding page has a durable reconciled receipt.
/// File and chunk positions are zero-based so the initial cursor is unambiguous.
/// </summary>
public sealed record CortexLegacyMigrationCursorV1
{
    [JsonConstructor]
    public CortexLegacyMigrationCursorV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string migrationId,
        string sourceManifestHash,
        string sourceInventoryHash,
        int sourceFileCount,
        string storeId,
        string storeEpoch,
        string projectId,
        string sourceRootId,
        long pageNumber,
        int nextFileIndex,
        int nextChunkIndex,
        long nextSequence,
        string previousBundleHash,
        string priorEventHash,
        bool completed,
        string contentTreatment,
        bool executable,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string cursorHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.LegacyMigrationCursor,
            canonicalization);
        CortexSyncContractShapes.RequireIdentifier(migrationId, nameof(migrationId));
        CortexSyncContractShapes.RequireHash(sourceManifestHash, nameof(sourceManifestHash));
        CortexSyncContractShapes.RequireHash(sourceInventoryHash, nameof(sourceInventoryHash));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
        CortexSyncContractShapes.RequireHash(previousBundleHash, nameof(previousBundleHash));
        CortexSyncContractShapes.RequireHash(priorEventHash, nameof(priorEventHash));
        CortexSyncContractShapes.RequireHash(cursorHash, nameof(cursorHash));
        if (sourceFileCount is < 1 or > CortexSyncContractLimits.MaximumMigrationFiles ||
            pageNumber is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            nextFileIndex > sourceFileCount ||
            nextFileIndex is < 0 or > CortexSyncContractLimits.MaximumMigrationFiles ||
            nextChunkIndex is < 0 or >= CortexLegacyFileCopyPayloadV1.MaximumChunks ||
            nextSequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            completed && nextChunkIndex != 0 ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable)
        {
            throw new ArgumentException("The legacy migration cursor is outside its bounded data-only shape.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        MigrationId = migrationId;
        SourceManifestHash = sourceManifestHash;
        SourceInventoryHash = sourceInventoryHash;
        SourceFileCount = sourceFileCount;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        SourceRootId = sourceRootId;
        PageNumber = pageNumber;
        NextFileIndex = nextFileIndex;
        NextChunkIndex = nextChunkIndex;
        NextSequence = nextSequence;
        PreviousBundleHash = previousBundleHash;
        PriorEventHash = priorEventHash;
        Completed = completed;
        ContentTreatment = contentTreatment;
        Executable = executable;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        CursorHash = cursorHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string MigrationId { get; }
    public string SourceManifestHash { get; }
    public string SourceInventoryHash { get; }
    public int SourceFileCount { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string SourceRootId { get; }
    public long PageNumber { get; }
    public int NextFileIndex { get; }
    public int NextChunkIndex { get; }
    public long NextSequence { get; }
    public string PreviousBundleHash { get; }
    public string PriorEventHash { get; }
    public bool Completed { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string CursorHash { get; }
}

/// <summary>
/// One bounded migration batch. CurrentCursor identifies the exact requested position and
/// NextCursor is only a proposed resume position until Bundle receives a durable receipt.
/// </summary>
public sealed record CortexLegacyMigrationPageV1
{
    [JsonConstructor]
    public CortexLegacyMigrationPageV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string pageId,
        CortexLegacyMigrationInventoryV1 sourceInventory,
        CortexLegacyMigrationCursorV1 currentCursor,
        CortexOfflineBundleV1 bundle,
        CortexLegacyMigrationCursorV1 nextCursor,
        bool acknowledgmentRequired,
        bool sourceMutationAllowed,
        string contentTreatment,
        bool executable,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string pageHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.LegacyMigrationPage,
            canonicalization);
        CortexSyncContractShapes.RequireIdentifier(pageId, nameof(pageId));
        ArgumentNullException.ThrowIfNull(sourceInventory);
        ArgumentNullException.ThrowIfNull(currentCursor);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(nextCursor);
        CortexSyncContractShapes.RequireHash(pageHash, nameof(pageHash));
        if (!acknowledgmentRequired || sourceMutationAllowed ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable)
        {
            throw new ArgumentException("A migration page must remain acknowledgment-gated, source-preserving, and non-executable.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        PageId = pageId;
        SourceInventory = sourceInventory;
        CurrentCursor = currentCursor;
        Bundle = bundle;
        NextCursor = nextCursor;
        AcknowledgmentRequired = acknowledgmentRequired;
        SourceMutationAllowed = sourceMutationAllowed;
        ContentTreatment = contentTreatment;
        Executable = executable;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        PageHash = pageHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string PageId { get; }
    public CortexLegacyMigrationInventoryV1 SourceInventory { get; }
    public CortexLegacyMigrationCursorV1 CurrentCursor { get; }
    public CortexOfflineBundleV1 Bundle { get; }
    public CortexLegacyMigrationCursorV1 NextCursor { get; }
    public bool AcknowledgmentRequired { get; }
    public bool SourceMutationAllowed { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string PageHash { get; }
}
