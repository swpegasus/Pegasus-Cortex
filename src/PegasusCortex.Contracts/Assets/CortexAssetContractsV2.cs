namespace Pegasus.Cortex.Contracts.Assets;

/// <summary>Passive metadata. Parameters retains exact opaque JSON text, never executable behavior.</summary>
public sealed record CortexAssetSpecV2(
    string Namespace, string Id, string Title, string Meaning, string MediaType,
    string Tags = "", string Alias = "", string Text = "", string License = "",
    string Source = "", string Pack = "", string PackVersion = "",
    string Parameters = "", string DetailsRef = "");

public sealed record CortexAssetV2(CortexAssetSpecV2 Metadata, string Ref, string Sha256,
    long Size, string ChunksSha256);
public sealed record CortexAssetSearchV2(IReadOnlyList<CortexAssetV2> Items,
    string? NextCursor, int Scanned, bool Complete);
public sealed record CortexAssetBytesV2(byte[] Bytes, long Offset, long TotalSize,
    string Sha256, string ContentSha256);

public sealed class CortexAssetException(string code, int? committed = null) : IOException(code)
{
    public string Code { get; } = code;
    public int? Committed { get; } = committed;
}

/// <summary>Physical storage selection never changes assets/2 identities or URI semantics.</summary>
public interface ICortexAssetStoreV2
{
    string Root { get; }
    long MaximumAssetBytes { get; }
    Task<CortexAssetV2> ImportAsync(string source, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default,
        string? expectedContentSha256 = null);
    CortexAssetV2 Read(string ns, string id, string reference);
    CortexAssetV2 ReadReference(string ns, string reference);
    CortexAssetV2 ReadReference(string reference);
    CortexAssetV2? Current(string ns, string id);
    Task<CortexAssetBytesV2> ReadBytesAsync(string ns, string id, string reference,
        long offset = 0, int length = 65536, CancellationToken cancellationToken = default);
    CortexAssetSearchV2 Search(string ns, string query, int limit = 16,
        string? cursor = null, CancellationToken cancellationToken = default);
    IReadOnlyList<string> ExposedNamespaces();
    Task ExposeAsync(string ns, CancellationToken cancellationToken = default);
    bool RecordIssue(string ns, string id, string reference, string code);
}

/// <summary>A stream sink consumes original bytes without extracting per-asset files.</summary>
public interface ICortexAssetStreamStoreV1 : ICortexAssetStoreV2
{
    Task<CortexAssetV2> ImportStreamAsync(Stream source, long contentLength,
        CortexAssetSpecV2 metadata, string? expectedRef = null,
        CancellationToken cancellationToken = default, string? expectedContentSha256 = null);
    Task<CortexAssetV2> ImportExistingObjectAsync(string contentSha256,
        CortexAssetSpecV2 metadata, string? expectedRef = null,
        CancellationToken cancellationToken = default);
}

