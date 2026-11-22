using Pegasus.Cortex.Contracts.Serialization;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Versioned record-revision payload produced by paged legacy migration. RecordId is stable for
/// one source-root/path identity; RevisionId identifies immutable content or a tombstone. This
/// data-only envelope carries namespace and provenance hints but grants no server authority.
/// </summary>
public sealed record CortexLegacyMigrationRecordV1
{
    [JsonConstructor]
    public CortexLegacyMigrationRecordV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string recordId,
        string revisionId,
        string namespaceHint,
        string recordKind,
        string sourceRootId,
        string sourceManifestHash,
        string sourceInventoryHash,
        int sourceFileIndex,
        string sourceRelativePath,
        long sourceBytes,
        string sourceSha256,
        string? supersedesRevisionId,
        bool tombstone,
        int chunkIndex,
        int chunkCount,
        long chunkOffset,
        string contentBase64Url,
        int contentBytes,
        string contentSha256,
        string contentTreatment,
        bool executable)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.LegacyMigrationRecord,
            canonicalization);
        CortexSyncContractShapes.RequireIdentifier(recordId, nameof(recordId));
        CortexSyncContractShapes.RequireIdentifier(revisionId, nameof(revisionId));
        CortexSyncContractShapes.RequireNamespace(namespaceHint, nameof(namespaceHint));
        CortexSyncContractShapes.RequireIdentifier(recordKind, nameof(recordKind));
        CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
        CortexSyncContractShapes.RequireHash(sourceManifestHash, nameof(sourceManifestHash));
        CortexSyncContractShapes.RequireHash(sourceInventoryHash, nameof(sourceInventoryHash));
        CortexSyncContractShapes.RequireRelativePath(sourceRelativePath, nameof(sourceRelativePath));
        CortexSyncContractShapes.RequireHash(sourceSha256, nameof(sourceSha256));
        CortexSyncContractShapes.RequireHash(contentSha256, nameof(contentSha256));
        if (supersedesRevisionId is not null)
            CortexSyncContractShapes.RequireIdentifier(supersedesRevisionId, nameof(supersedesRevisionId));
        if (sourceFileIndex is < 0 or >= CortexSyncContractLimits.MaximumMigrationFiles ||
            sourceBytes is < 0 or > CortexLegacyFileCopyPayloadV1.MaximumSourceBytes ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable)
        {
            throw new ArgumentException("The legacy migration record exceeds its provenance or safety bounds.");
        }

        if (tombstone)
        {
            if (supersedesRevisionId is null || sourceBytes != 0 || chunkIndex != 0 || chunkCount != 0 ||
                chunkOffset != 0 || contentBytes != 0 || !string.IsNullOrEmpty(contentBase64Url) ||
                !string.Equals(contentSha256, CortexCanonicalJson.Sha256(Array.Empty<byte>()), StringComparison.Ordinal))
            {
                throw new ArgumentException("A migration tombstone must identify a predecessor and carry no content.");
            }
        }
        else
        {
            if (chunkCount is < 1 or > CortexLegacyFileCopyPayloadV1.MaximumChunks ||
                chunkIndex is < 1 || chunkIndex > chunkCount || chunkOffset < 0 ||
                contentBytes < 0 || contentBytes > CortexLegacyFileCopyPayloadV1.MaximumChunkBytes ||
                chunkOffset > sourceBytes - contentBytes ||
                (sourceBytes == 0 && (chunkIndex != 1 || chunkCount != 1 || contentBytes != 0)) ||
                (sourceBytes > 0 && contentBytes == 0) ||
                (chunkIndex < chunkCount && chunkOffset + contentBytes >= sourceBytes) ||
                (chunkIndex == chunkCount && chunkOffset + contentBytes != sourceBytes))
            {
                throw new ArgumentException("The migration record chunk does not describe a complete bounded source revision.");
            }
            if (contentBytes == 0)
            {
                if (!string.IsNullOrEmpty(contentBase64Url))
                    throw new ArgumentException("An empty source revision must use empty encoded content.", nameof(contentBase64Url));
            }
            else
            {
                CortexSyncContractShapes.RequireEncodedBytes(
                    contentBase64Url,
                    contentBytes,
                    CortexLegacyFileCopyPayloadV1.MaximumChunkBytes,
                    nameof(contentBase64Url));
            }
            var decoded = contentBytes == 0
                ? Array.Empty<byte>()
                : CortexSyncContractShapes.DecodeBase64Url(contentBase64Url);
            if (!string.Equals(CortexCanonicalJson.Sha256(decoded), contentSha256, StringComparison.Ordinal))
                throw new ArgumentException("The migration record chunk hash does not match its content.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        RecordId = recordId;
        RevisionId = revisionId;
        NamespaceHint = namespaceHint;
        RecordKind = recordKind;
        SourceRootId = sourceRootId;
        SourceManifestHash = sourceManifestHash;
        SourceInventoryHash = sourceInventoryHash;
        SourceFileIndex = sourceFileIndex;
        SourceRelativePath = sourceRelativePath;
        SourceBytes = sourceBytes;
        SourceSha256 = sourceSha256;
        SupersedesRevisionId = supersedesRevisionId;
        Tombstone = tombstone;
        ChunkIndex = chunkIndex;
        ChunkCount = chunkCount;
        ChunkOffset = chunkOffset;
        ContentBase64Url = contentBase64Url;
        ContentBytes = contentBytes;
        ContentSha256 = contentSha256;
        ContentTreatment = contentTreatment;
        Executable = executable;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string RecordId { get; }
    public string RevisionId { get; }
    public string NamespaceHint { get; }
    public string RecordKind { get; }
    public string SourceRootId { get; }
    public string SourceManifestHash { get; }
    public string SourceInventoryHash { get; }
    public int SourceFileIndex { get; }
    public string SourceRelativePath { get; }
    public long SourceBytes { get; }
    public string SourceSha256 { get; }
    public string? SupersedesRevisionId { get; }
    public bool Tombstone { get; }
    public int ChunkIndex { get; }
    public int ChunkCount { get; }
    public long ChunkOffset { get; }
    public string ContentBase64Url { get; }
    public int ContentBytes { get; }
    public string ContentSha256 { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
}
