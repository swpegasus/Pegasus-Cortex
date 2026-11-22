namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public enum SqliteCortexPayloadMaintenanceAction { Upgrade, Offload, Rehydrate }

public sealed record SqliteCortexTieringCoordinate(string Namespace, string Kind, string RecordId,
    long ExpectedHeadRevision, string ExpectedHeadHash);

public sealed record SqliteCortexTieringRequest(int Version,
    IReadOnlyList<SqliteCortexTieringCoordinate> Records)
{
    // Only the store's age/size planner can establish this execution guard.
    internal SqliteCortexPayloadRotationRequest? RotationGuard { get; init; }
}

public sealed record SqliteCortexTieringReceipt(int Version, string State, int SchemaVersion,
    int SelectedRecords, int PayloadsChanged, int SharedPayloadsKeptHot, long HotBytesEvicted,
    long HotBytesRestored, bool RecordMetadataUnchanged, long PhysicalBytesReclaimed = 0)
{
    public bool CleanupIncomplete { get; init; }
}

public sealed record SqliteCortexColdPayload(string ContentHash, int OriginalLength,
    int CompressedLength, string CompressedSha256, int FormatVersion = 1);

/// <summary>Product-owned immutable local payload storage; never executes record content.</summary>
public interface ISqliteCortexColdPayloadStore
{
    ValueTask<byte[]> ReadAsync(SqliteCortexColdPayload payload, CancellationToken cancellationToken);
    // The returned lease retains the verified object through the SQLite commit.
    ValueTask<IAsyncDisposable> PublishVerifiedAsync(SqliteCortexColdPayload payload,
        ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken);
}
