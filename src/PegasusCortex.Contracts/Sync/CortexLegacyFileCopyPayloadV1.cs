using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Typed data-only envelope for one bounded chunk of a byte-preserved legacy file. Raw
/// Markdown or legacy JSON is never presented as a current Cortex wire contract.
/// </summary>
public sealed record CortexLegacyFileCopyPayloadV1
{
    // Base64url plus the envelope remains below the 512 KiB event payload ceiling.
    public const int MaximumChunkBytes = 384_000;
    public const long MaximumSourceBytes = 16L * 1024 * 1024;
    public const int MaximumChunks = 64;

    public CortexLegacyFileCopyPayloadV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string sourceRootId,
        string sourceRelativePath,
        string role,
        string mappingId,
        int chunkIndex,
        int chunkCount,
        long chunkOffset,
        long sourceBytes,
        string sourceSha256,
        string chunkBytesBase64Url,
        int chunkBytes,
        string chunkSha256,
        string contentTreatment,
        bool executable)
    {
        CortexSyncContractShapes.RequireHeader(
            schemaVersion,
            contractId,
            CortexSyncContractIds.LegacyFileCopyPayload,
            canonicalization);
        CortexSyncContractShapes.RequireIdentifier(sourceRootId, nameof(sourceRootId));
        CortexSyncContractShapes.RequireRelativePath(sourceRelativePath, nameof(sourceRelativePath));
        CortexSyncContractShapes.RequireIdentifier(role, nameof(role));
        CortexSyncContractShapes.RequireIdentifier(mappingId, nameof(mappingId));
        CortexSyncContractShapes.RequireHash(sourceSha256, nameof(sourceSha256));
        CortexSyncContractShapes.RequireHash(chunkSha256, nameof(chunkSha256));
        if (sourceBytes is < 0 or > MaximumSourceBytes ||
            chunkCount is < 1 or > MaximumChunks || chunkIndex is < 1 || chunkIndex > chunkCount ||
            chunkOffset < 0 || chunkOffset > sourceBytes || chunkBytes < 0 ||
            chunkOffset > sourceBytes - chunkBytes ||
            (sourceBytes == 0 && (chunkIndex != 1 || chunkCount != 1 || chunkOffset != 0 || chunkBytes != 0)) ||
            (sourceBytes > 0 && chunkBytes == 0) ||
            (chunkIndex < chunkCount && chunkOffset + chunkBytes >= sourceBytes) ||
            (chunkIndex == chunkCount && chunkOffset + chunkBytes != sourceBytes))
        {
            throw new ArgumentException("The legacy file chunk declaration is invalid.");
        }

        if (chunkBytes == 0)
        {
            if (!string.IsNullOrEmpty(chunkBytesBase64Url))
                throw new ArgumentException("An empty legacy file must use an empty base64url value.", nameof(chunkBytesBase64Url));
        }
        else
        {
            CortexSyncContractShapes.RequireEncodedBytes(
                chunkBytesBase64Url,
                chunkBytes,
                MaximumChunkBytes,
                nameof(chunkBytesBase64Url));
        }

        var decoded = chunkBytes == 0
            ? Array.Empty<byte>()
            : CortexSyncContractShapes.DecodeBase64Url(chunkBytesBase64Url);
        if (!string.Equals(CortexCanonicalJson.Sha256(decoded), chunkSha256, StringComparison.Ordinal) ||
            !string.Equals(contentTreatment, CortexSyncContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable)
        {
            throw new ArgumentException("The legacy file-copy payload integrity or safety declaration is invalid.");
        }

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        SourceRootId = sourceRootId;
        SourceRelativePath = sourceRelativePath;
        Role = role;
        MappingId = mappingId;
        ChunkIndex = chunkIndex;
        ChunkCount = chunkCount;
        ChunkOffset = chunkOffset;
        SourceBytes = sourceBytes;
        SourceSha256 = sourceSha256;
        ChunkBytesBase64Url = chunkBytesBase64Url;
        ChunkBytes = chunkBytes;
        ChunkSha256 = chunkSha256;
        ContentTreatment = contentTreatment;
        Executable = executable;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string SourceRootId { get; }
    public string SourceRelativePath { get; }
    public string Role { get; }
    public string MappingId { get; }
    public int ChunkIndex { get; }
    public int ChunkCount { get; }
    public long ChunkOffset { get; }
    public long SourceBytes { get; }
    public string SourceSha256 { get; }
    public string ChunkBytesBase64Url { get; }
    public int ChunkBytes { get; }
    public string ChunkSha256 { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
}
