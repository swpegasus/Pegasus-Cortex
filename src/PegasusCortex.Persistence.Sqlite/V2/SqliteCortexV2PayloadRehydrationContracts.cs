namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexPayloadHistoryCursor(long Revision, string RevisionHash);

/// <summary>One exact current typed record and an optional exact historical cursor.
/// A changed current head requires deliberate re-reading, never implicit adoption.</summary>
public sealed record SqliteCortexPayloadHistoryRehydrationRequest(int Version,
    SqliteCortexTieringCoordinate Record, SqliteCortexPayloadHistoryCursor? After = null);

/// <summary>One committed page. InspectedRevisions counts historical/cursor/head
/// proof rows, including repeated head checks, not unique revisions. Payload metadata
/// reads have separate reserved room in the fixed per-batch bound. ReachedHead only
/// means range exhaustion: callers may not infer that earlier skipped pages are hot.
/// A pin still independently checks the whole protected history.</summary>
public sealed record SqliteCortexPayloadHistoryRehydrationReceipt(int Version, string State,
    SqliteCortexTieringCoordinate Record, long StartedAfterRevision,
    SqliteCortexPayloadHistoryCursor Through, int InspectedRevisions, int CompletedRevisions,
    long ChargedPayloadBytes, SqliteCortexTieringReceipt Tiering,
    SqliteCortexPayloadHistoryCursor? Next, bool ReachedHead);
