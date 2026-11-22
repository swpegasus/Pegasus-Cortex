using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge.Geometry;

namespace Pegasus.Cortex.Geometry.Shadow;

public sealed record CortexShadowIdentity(string Namespace, string RecordId, long Revision,
    string ProvenanceSha256)
{
    public static CortexShadowIdentity From(CortexKnowledgeV2 record) => new(record.Namespace,
        record.RecordId, record.Revision, Provenance(record.Provenance.Source, record.Provenance.Actor,
            record.Provenance.SourceId, record.Provenance.CapturedAtUtc));
    public static CortexShadowIdentity From(CortexSearchHitV2 hit) => new(hit.Namespace, hit.RecordId,
        hit.Revision, Provenance(hit.Provenance.Source, hit.Provenance.Actor, hit.Provenance.SourceId,
            hit.Provenance.CapturedAtUtc));
    internal static string Provenance(CortexProvenanceSourceV2 source, string actor, string? id,
        DateTimeOffset at) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { source, actor, id, at })));
    [System.Text.Json.Serialization.JsonIgnore]
    public CortexKnowledgeTraversalCoordinate Coordinate => new(Namespace, "knowledge", RecordId, Revision);
}

/// <summary>Reviewed exact-revision admission is supplied by the owning authenticated composition,
/// never inferred from tags, retrieved instructions, search similarity or a client request.</summary>
public sealed record CortexShadowProfile(Guid TaskId, Guid ProfileId, Guid Generation,
    IReadOnlyList<CortexShadowIdentity> EligibleRecords);

public sealed record CortexShadowOptions(bool CollectionEnabled = false, bool EvaluationEnabled = true,
    int SampleEvery = 10, int QueueCapacity = 16, int MaximumQueuedBytes = 1024 * 1024,
    int MaximumPoints = 128, int JournalSlots = 32, int TickMilliseconds = 1000,
    int EvaluationMilliseconds = 50, int RetentionSeconds = 86400)
{
    public void Validate()
    {
        if (SampleEvery is < 1 or > 10000 || QueueCapacity is < 1 or > 64 ||
            MaximumQueuedBytes is < 32768 or > 4 * 1024 * 1024 || MaximumPoints is < 32 or > 256 ||
            JournalSlots is < 1 or > 128 || TickMilliseconds is < 10 or > 60000 ||
            EvaluationMilliseconds is < 10 or > 1000 || RetentionSeconds is < 1 or > 604800)
            throw new ArgumentOutOfRangeException(nameof(CortexShadowOptions));
    }
}

public enum CortexShadowMetric
{
    ProviderOperations, ForegroundFailures, SqliteStatementTraceEvents, MeasuredConnections,
    TraceAttachmentFailures, EligiblePoints, RejectedPoints, SamplesConsidered, Sampled,
    SamplingSkipped, Queued, Processed, QueueDropped, QueuedBytes, PeakQueuedBytes,
    StaleRevisions, Tombstones, PointEvictions, WorkerFailures, ObserverFailures,
    WorkerOverBudget, ProviderTicks, ForegroundCallTicks, ObserverTicks, WorkerTicks, LabeledSamples, FeedbackRejected, RevisionStateUpdates, RecoveryUncertainWork, LabeledTargets, LabeledReached, LabeledSelected, LabeledUnseen, MaintenanceTicks, SummaryTicks, PointDescriptorGenerations
}

/// <summary>Fixed elapsed-time buckets for this collector run, not CPU attribution.
/// Counts has one extra final bucket for values above every upper bound.</summary>
public sealed record CortexShadowLatencyBuckets(IReadOnlyList<long> UpperBoundsMicroseconds,
    IReadOnlyList<long> Counts, long Samples, long TotalStopwatchTicks, long MaximumStopwatchTicks);

public sealed record CortexShadowStatus(bool CollectionEnabled, bool RankingPublished,
    Guid Generation, IReadOnlyDictionary<string, long> Counters, int CurrentPoints,
    long JournalBytes, long JournalWrites, long JournalEvictions, string? LastFailure,
    string QualityEvidence)
{
    public long StopwatchFrequency => System.Diagnostics.Stopwatch.Frequency;
    public string Lifecycle { get; init; } = "disabled";
    public long MetadataBytes { get; init; }
    public int ReviewedRecordRevisions { get; init; }
    public int LastObservedRecordIds { get; init; }
    public long ObservedRevisionEpoch { get; init; }
    // Nullable/additive fields distinguish older persisted summaries from a
    // measured zero. Existing cumulative counters still span restored runs.
    public bool? EvaluationEnabled { get; init; }
    public string EffectiveMode { get; init; } = "unreported";
    // ForegroundObserverSection covers Observe and its failure handler only; it
    // excludes provider await, trace setup/disposal and metric bookkeeping.
    // Worker covers one dequeued item including journal IO, not periodic upkeep;
    // QueueDelay covers enqueue through dequeue. None attributes CPU use.
    public IReadOnlyDictionary<string, CortexShadowLatencyBuckets>? LatenciesThisRun { get; init; }
    // Exact eligible identities seen by foreground admission, including queued
    // work later dropped; bounded by ReviewedRecordRevisions and reset per run.
    public int? DistinctAdmittedIdentitiesThisRun { get; init; }
    public long EligiblePointOccurrencesCumulative => Counters.GetValueOrDefault(nameof(CortexShadowMetric.EligiblePoints));
    public int DistinctPointIdentitiesInWindow => CurrentPoints;
}

public sealed record CortexShadowRank(CortexKnowledgeTraversalCoordinate Coordinate, int Rank);
public sealed record CortexShadowPointReference(CortexShadowIdentity Identity, string Projection,
    CortexKnowledgePointDescriptor? Descriptor, string State);
public sealed record CortexShadowObservation(string Schema, Guid ObservationId, Guid Generation,
    DateTimeOffset ObservedAtUtc, string Operation, long ProviderTicks, long ObserverTicks,
    long SqliteStatementTraceEvents, long MeasuredConnections, long TraceAttachmentFailures,
    IReadOnlyList<CortexShadowPointReference> Points, IReadOnlyList<CortexShadowRank> Ordinary,
    IReadOnlyList<CortexShadowRank> Shadow, IReadOnlyList<CortexKnowledgeTraversalCoordinate> Reached,
    int OrdinaryReturned, int OrdinaryEligible, int CommonSelected, int MemoryLookups,
    long WorkerTicks, string Status)
{
    public long StopwatchFrequency => System.Diagnostics.Stopwatch.Frequency;
    public long ObservedRevisionEpoch { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }
}
public sealed record CortexShadowFeedback(Guid ObservationId, Guid Generation, string EvidenceId,
    IReadOnlyList<CortexKnowledgeTraversalCoordinate> CanonicalTargets);
public sealed record CortexShadowFeedbackResult(string Schema, Guid ObservationId, Guid Generation,
    string EvidenceId, int Targets, int Reached, int Selected, int ReachedButUnselected, int Unseen);

internal sealed record ShadowProjection(CortexShadowIdentity Identity, CortexKnowledgeGeometryPoint? Point,
    string Projection, string State);
internal sealed record ShadowWork(Guid Id, DateTimeOffset At, string Operation, string? Intent,
    string[] Namespaces, ShadowProjection[] Points, CortexShadowRank[] Ordinary, int Returned,
    long ProviderTicks, long ObserverTicks, long SqlEvents, long Connections, long TraceFailures,
    int Bytes, CortexShadowFeedback? Feedback = null)
{
    // Monotonic process-local timing only; never persisted or restored.
    internal long EnqueuedTimestamp { get; init; }
}

public sealed record CortexShadowStudySummary(string Schema, Guid Generation, Guid RunId,
    DateTimeOffset SnapshotAtUtc, bool Final, int PendingWorkAtSnapshot, CortexShadowStatus Status);
internal sealed record ShadowFrontierEntry(CortexShadowIdentity Identity, bool Inactive);
internal sealed record ShadowFrontierSnapshot(string Schema, Guid Generation, long Epoch,
    IReadOnlyList<ShadowFrontierEntry> Entries);
