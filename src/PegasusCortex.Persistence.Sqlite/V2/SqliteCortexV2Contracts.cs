namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>
/// One immutable operational revision append. Revision zero means that the
/// record must not exist; a positive expected revision is an optimistic
/// concurrency precondition. Content may be omitted only for an update, in
/// which case the current immutable content blob is referenced again.
/// </summary>
public sealed record SqliteCortexV2AppendRequest(
    string Namespace,
    string Kind,
    string RecordId,
    long ExpectedRevision,
    string IdempotencyKey,
    string Actor,
    string EventType,
    string Title,
    IReadOnlyList<string> Tags,
    string Summary,
    byte[]? ContentUtf8,
    bool Searchable = true,
    string AttributesJson = "{}")
{
    // Only the domain adapter may replace volatile Core-stamped metadata with
    // the stable caller payload when evaluating a retry. Public low-level
    // callers cannot assert an arbitrary equivalence fingerprint.
    internal string? IdempotencyFingerprint { get; init; }
}

public static class SqliteCortexV2WriteDispositions
{
    public const string Appended = "appended";
    public const string Idempotent = "idempotent";
    public const string Conflict = "conflict";
    public const string StaleRevision = "stale-revision";
}

public sealed record SqliteCortexV2WriteResult(
    string Disposition,
    string Namespace,
    string Kind,
    string RecordId,
    long? Revision,
    long? ActualRevision,
    string? RevisionHash,
    string? ContentHash);

/// <summary>
/// Atomic multi-record write result. When Committed is false, no item in the
/// supplied batch changed the store, including items evaluated before the
/// reported failing item.
/// </summary>
public sealed record SqliteCortexV2BatchWriteResult(
    bool Committed,
    string Disposition,
    IReadOnlyList<SqliteCortexV2WriteResult> Items);

public sealed record SqliteCortexV2Record(
    string Namespace,
    string Kind,
    string RecordId,
    long Revision,
    string RevisionHash,
    string? PreviousRevisionHash,
    string ContentHash,
    string Title,
    IReadOnlyList<string> Tags,
    string Summary,
    string AttributesJson,
    string EventType,
    string Actor,
    DateTimeOffset OccurredAtUtc,
    bool Searchable,
    byte[] ContentUtf8);

/// <summary>
/// Compact history deliberately excludes the content body. ContentHash lets a
/// caller detect content reuse and request one exact revision only when needed.
/// </summary>
public sealed record SqliteCortexV2HistoryEvent(
    long Revision,
    string RevisionHash,
    string? PreviousRevisionHash,
    string ContentHash,
    string EventType,
    string Actor,
    DateTimeOffset OccurredAtUtc,
    bool Searchable,
    string Title,
    string Summary,
    string AttributesJson);

public sealed record SqliteCortexV2HistoryPage(
    string Namespace,
    string Kind,
    string RecordId,
    IReadOnlyList<SqliteCortexV2HistoryEvent> Items,
    long? NextAfterRevision);

public sealed record SqliteCortexV2HeadPage(
    string Namespace,
    IReadOnlyList<SqliteCortexV2Record> Items,
    string? NextAfterRecordId);

/// <summary>
/// One current-head projection without its immutable content blob. Startup
/// uses this for the exact current task so a compact brief never hydrates the
/// task body merely to return the existing summary and lifecycle metadata.
/// </summary>
public sealed record SqliteCortexV2HeadProjection(
    string Namespace,
    string Kind,
    string RecordId,
    long Revision,
    string Title,
    string Summary,
    string AttributesJson,
    bool Searchable);

public sealed record SqliteCortexV2SearchRequest(
    string Namespace,
    string Query,
    string? Kind = null,
    int Limit = 10,
    int MinimumMatch = 1);

public sealed record SqliteCortexV2SearchHit(
    string Namespace,
    string Kind,
    string RecordId,
    long Revision,
    string RevisionHash,
    string Title,
    IReadOnlyList<string> Tags,
    string Summary,
    string AttributesJson,
    string Excerpt,
    int MatchedTerms,
    double Score);

public sealed record SqliteCortexV2Namespace(
    string Name,
    long RevisionCount,
    long HeadCount,
    long ReferencedContentBytes,
    long HeadContentBytes,
    long HotChunkCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SqliteCortexV2NamespacePage(
    IReadOnlyList<SqliteCortexV2Namespace> Items,
    string? NextAfterNamespace);

public sealed record SqliteCortexV2CatalogCounters(
    long NamespaceRows,
    long RevisionRows,
    long HeadRows);

/// <summary>
/// A constant-work startup result. CorpusRowsRead is an explicit observable
/// invariant: quick checks never query revision, blob, head, or FTS rows.
/// </summary>
public sealed record SqliteCortexV2QuickCheckResult(
    bool Healthy,
    string State,
    int SchemaVersion,
    string JournalMode,
    int ProbesExecuted,
    long CorpusRowsRead);

public sealed record SqliteCortexV2DoctorResult(
    bool Healthy,
    string State,
    long RevisionRowsScanned,
    long BlobRowsScanned,
    long HeadRows,
    long SearchChunkRows,
    long FtsRows);

/// <summary>
/// Original/CompressedBlobBytes describe retained logical payloads across both
/// tiers. Hot/ColdCompressedBlobBytes split that encoding; none is disk reclaim.
/// </summary>
public sealed record SqliteCortexV2StorageMetrics(
    long NamespaceRows,
    long RevisionRows,
    long BlobRows,
    long HeadRows,
    long SearchChunkRows,
    long FtsRows,
    long OriginalBlobBytes,
    long CompressedBlobBytes,
    int SchemaVersion = 2,
    long HotCompressedBlobBytes = 0,
    long ColdCompressedBlobBytes = 0);

public sealed record SqliteCortexV2BackupReceipt(
    string SourceDatabasePath,
    string BackupPath,
    long BackupBytes,
    string Sha256,
    int SchemaVersion,
    long NamespaceRows,
    long RevisionRows,
    long HeadRows,
    DateTimeOffset CreatedAtUtc);

public sealed record SqliteCortexV2RestoreReceipt(
    string BackupPath,
    string DestinationDatabasePath,
    long RestoredBytes,
    string Sha256,
    int SchemaVersion,
    long NamespaceRows,
    long RevisionRows,
    long HeadRows,
    DateTimeOffset RestoredAtUtc);
