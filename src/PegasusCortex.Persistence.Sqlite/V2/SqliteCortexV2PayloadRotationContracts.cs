namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexPayloadRotationCursor(string Kind, string RecordId);

/// <summary>Age is an eligibility floor; the byte value is a soft hot-storage
/// target, never an admission quota or permission to erase retained history.</summary>
public sealed record SqliteCortexPayloadRotationRequest(int Version, string Namespace,
    DateTimeOffset ArchivedBeforeUtc, long TargetHotCompressedBytes = 0,
    SqliteCortexPayloadRotationCursor? After = null)
{
    internal SqliteCortexPayloadMetadataBudget? Budget { get; init; }
}

public sealed record SqliteCortexPayloadRotationPlan(int Version, string Namespace,
    int? ScannedHeads, int? ProtectedRecords, int? ReferenceProtectedRecords, int? OverBoundRecords,
    long? HotCompressedBytes, SqliteCortexTieringRequest? Selection,
    SqliteCortexPayloadRotationCursor? NextCursor);

public sealed record SqliteCortexPayloadRotationReceipt(int Version, string State,
    SqliteCortexPayloadRotationPlan Plan, SqliteCortexTieringReceipt? Tiering,
    long? ObservedHotCompressedBytesAfter, bool? TargetSatisfied, string? ObservationIssue = null)
{
    public int? MetadataObservations { get; init; }
    public bool ImmutableObjectsMayRemain { get; init; }
}
