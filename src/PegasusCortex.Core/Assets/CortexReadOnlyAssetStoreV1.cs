using Pegasus.Cortex.Contracts.Assets;

namespace Pegasus.Cortex.Core.Assets;

/// <summary>Explicit legacy read selection. A miss never probes or migrates another provider.</summary>
public sealed class CortexReadOnlyAssetStoreV1(ICortexAssetStoreV2 source) : ICortexAssetStoreV2
{
    private readonly ICortexAssetStoreV2 _source = source ?? throw new ArgumentNullException(nameof(source));
    public string Root => _source.Root;
    public long MaximumAssetBytes => _source.MaximumAssetBytes;
    public CortexAssetV2 Read(string ns, string id, string reference) => _source.Read(ns, id, reference);
    public CortexAssetV2 ReadReference(string ns, string reference) => _source.ReadReference(ns, reference);
    public CortexAssetV2 ReadReference(string reference) => _source.ReadReference(reference);
    public CortexAssetV2? Current(string ns, string id) => _source.Current(ns, id);
    public Task<CortexAssetBytesV2> ReadBytesAsync(string ns, string id, string reference,
        long offset = 0, int length = CortexAssetCodecV2.ChunkSize, CancellationToken cancellationToken = default) =>
        _source.ReadBytesAsync(ns, id, reference, offset, length, cancellationToken);
    public CortexAssetSearchV2 Search(string ns, string query, int limit = 16,
        string? cursor = null, CancellationToken cancellationToken = default) =>
        _source.Search(ns, query, limit, cursor, cancellationToken);
    public IReadOnlyList<string> ExposedNamespaces() => _source.ExposedNamespaces();
    public Task<CortexAssetV2> ImportAsync(string source, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default,
        string? expectedContentSha256 = null) =>
        Task.FromException<CortexAssetV2>(new CortexAssetException("asset_read_only"));
    public Task ExposeAsync(string ns, CancellationToken cancellationToken = default) =>
        Task.FromException(new CortexAssetException("asset_read_only"));
    public bool RecordIssue(string ns, string id, string reference, string code) => false;
}

