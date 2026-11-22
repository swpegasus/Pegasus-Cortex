using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// One portable source-file commitment in the exact order used by paged migration. Local
/// absolute paths are deliberately excluded; the inventory is data-only and grants no authority.
/// </summary>
public sealed record CortexLegacyMigrationInventoryFileV1
{
    [JsonConstructor]
    public CortexLegacyMigrationInventoryFileV1(
        int fileIndex,
        string role,
        string sourceRelativePath,
        long sourceBytes,
        string sourceSha256,
        string recordId,
        string revisionId,
        int chunkCount)
    {
        CortexSyncContractShapes.RequireIdentifier(role, nameof(role));
        CortexSyncContractShapes.RequireRelativePath(sourceRelativePath, nameof(sourceRelativePath));
        CortexSyncContractShapes.RequireHash(sourceSha256, nameof(sourceSha256));
        CortexSyncContractShapes.RequireIdentifier(recordId, nameof(recordId));
        CortexSyncContractShapes.RequireIdentifier(revisionId, nameof(revisionId));
        var expectedChunks = sourceBytes == 0
            ? 1
            : checked((int)((sourceBytes + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
                CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
        if (fileIndex is < 0 or >= CortexSyncContractLimits.MaximumMigrationFiles ||
            sourceBytes is < 0 or > CortexLegacyFileCopyPayloadV1.MaximumSourceBytes ||
            chunkCount != expectedChunks)
        {
            throw new ArgumentException("The migration inventory file is outside its ordered source bounds.");
        }

        FileIndex = fileIndex;
        Role = role;
        SourceRelativePath = sourceRelativePath;
        SourceBytes = sourceBytes;
        SourceSha256 = sourceSha256;
        RecordId = recordId;
        RevisionId = revisionId;
        ChunkCount = chunkCount;
    }

    public int FileIndex { get; }
    public string Role { get; }
    public string SourceRelativePath { get; }
    public long SourceBytes { get; }
    public string SourceSha256 { get; }
    public string RecordId { get; }
    public string RevisionId { get; }
    public int ChunkCount { get; }
}

/// <summary>
/// Hash-sealed portable projection of the exact source manifest inventory. A page must carry this
/// complete bounded list so ordered progress and terminal completion are derived, not asserted.
/// </summary>
public sealed record CortexLegacyMigrationInventoryV1
{
    [JsonConstructor]
    public CortexLegacyMigrationInventoryV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string migrationId,
        string sourceManifestHash,
        string storeId,
        string storeEpoch,
        string projectId,
        string sourceRootId,
        DateTimeOffset capturedAt,
        bool sourcePathCaseSensitive,
        IReadOnlyList<CortexLegacyMigrationInventoryFileV1> files,
        long totalSourceBytes,
        string contentTreatment,
        bool executable,
        IReadOnlyList<CortexSyncExtensionV1> extensions,
        string inventoryHash)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.LegacyMigrationInventory,
            canonicalization);
        CortexSyncContractShapes.RequireIdentifier(migrationId, nameof(migrationId));
        CortexSyncContractShapes.RequireHash(sourceManifestHash, nameof(sourceManifestHash));
        CortexSyncContractShapes.RequireIdentifier(storeId, nameof(storeId));
        CortexSyncContractShapes.RequireIdentifier(storeEpoch, nameof(storeEpoch));
        CortexSyncContractShapes.RequireIdentifier(projectId, nameof(projectId));
        CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
        CortexSyncContractShapes.RequireUtc(capturedAt, nameof(capturedAt));
        CortexSyncContractShapes.RequireHash(inventoryHash, nameof(inventoryHash));
        var capturedFiles = CortexSyncContractShapes.SnapshotList(
            files,
            CortexSyncContractLimits.MaximumMigrationFiles,
            nameof(files),
            requireNonEmpty: true);
        var pathComparer = sourcePathCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        long calculatedBytes = 0;
        for (var index = 0; index < capturedFiles.Count; index++)
        {
            if (capturedFiles[index].FileIndex != index)
                throw new ArgumentException("Migration inventory file indices must be contiguous and zero based.", nameof(files));
            calculatedBytes = checked(calculatedBytes + capturedFiles[index].SourceBytes);
        }
        if (capturedFiles.Select(item => item.SourceRelativePath).Distinct(pathComparer).Count() != capturedFiles.Count ||
            capturedFiles.Select(item => item.RecordId).Distinct(StringComparer.Ordinal).Count() != capturedFiles.Count ||
            capturedFiles.Select(item => item.RevisionId).Distinct(StringComparer.Ordinal).Count() != capturedFiles.Count ||
            calculatedBytes != totalSourceBytes ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable)
        {
            throw new ArgumentException("The migration inventory is incomplete, duplicated, or outside its data-only bounds.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        MigrationId = migrationId;
        SourceManifestHash = sourceManifestHash;
        StoreId = storeId;
        StoreEpoch = storeEpoch;
        ProjectId = projectId;
        SourceRootId = sourceRootId;
        CapturedAt = capturedAt;
        SourcePathCaseSensitive = sourcePathCaseSensitive;
        Files = capturedFiles;
        TotalSourceBytes = totalSourceBytes;
        ContentTreatment = contentTreatment;
        Executable = executable;
        Extensions = CortexSyncContractShapes.SnapshotExtensions(extensions, nameof(extensions));
        InventoryHash = inventoryHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string MigrationId { get; }
    public string SourceManifestHash { get; }
    public string StoreId { get; }
    public string StoreEpoch { get; }
    public string ProjectId { get; }
    public string SourceRootId { get; }
    public DateTimeOffset CapturedAt { get; }
    public bool SourcePathCaseSensitive { get; }
    public IReadOnlyList<CortexLegacyMigrationInventoryFileV1> Files { get; }
    public long TotalSourceBytes { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public IReadOnlyList<CortexSyncExtensionV1> Extensions { get; }
    public string InventoryHash { get; }
}
