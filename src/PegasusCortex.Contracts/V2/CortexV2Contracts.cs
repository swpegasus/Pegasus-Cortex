using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.V2;

public static class CortexV2Limits
{
    public const int MaximumNamespaceCharacters = 128;
    public const int MaximumNamespaces = 8;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumContextCursorCharacters = 256;
    public const int MaximumPersonalProfileCharacters = 128;
    public const int MaximumPersonalGenerationCharacters = 128;
    public const int MaximumPersonalIssueCodeCharacters = 64;
    public const int MaximumTitleCharacters = 512;
    public const int MaximumContentCharacters = 24 * 1024;
    public const int MaximumIntentCharacters = 2048;
    public const int MaximumReasonCharacters = 2048;
    public const int MaximumCheckpointCharacters = 4096;
    public const int MaximumEvidenceCharacters = 4096;
    public const int MaximumHandoffCharacters = 4096;
    public const int MaximumTags = 32;
    public const int MaximumDependencies = 64;
    public const int MaximumKnowledgeWritesPerFinish = 16;
    public const int MaximumReceiptTitleCharacters = 128;
    public const int MaximumReceiptExcerptCharacters = 256;
    public const int MaximumReceiptReasonCharacters = 256;
    public const int MaximumCompactExcerptCharacters = 320;
    public const int MaximumPhaseCharacters = 128;
    public const int MaximumCheckpointSubjectCharacters = 256;
    public const int MaximumBatchReadRecords = 8;
    public const int MaximumConsolidationHeads = 512;
    public const int MaximumConsolidationSuggestions = 32;
    public const int DefaultConsolidationHeads = 128;
    public const int MaximumPageSize = 32;
    public const int DefaultPageSize = 10;
    public const int MinimumResponseUtf8Bytes = 512;
    public const int DefaultStartResponseUtf8Bytes = 4 * 1024;
    public const int MaximumStartResponseUtf8Bytes = 8 * 1024;
    public const int DefaultResponseUtf8Bytes = 8 * 1024;
    public const int MaximumCompactResponseUtf8Bytes = 64 * 1024;
    public const int MaximumResponseUtf8Bytes = 256 * 1024;
}

public static class CortexV2Json
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

public enum CortexTaskActionV2
{
    Create,
    List,
    Get,
    Assign,
    Claim,
    Update,
    Block,
    Complete,
    Reopen,
    Archive,
    DependencyAdd,
    DependencyRemove,
    History
}

public enum CortexTaskStatusV2
{
    Open,
    Assigned,
    Claimed,
    Blocked,
    Completed,
    Archived
}

public enum CortexKnowledgeActionV2
{
    Create,
    Read,
    Revise,
    Supersede,
    Archive,
    Analyze
}

public enum CortexKnowledgeKindV2
{
    Correction,
    Preference,
    Workaround,
    Failure,
    Regression,
    Decision,
    Tenet,
    Observation
}

public enum CortexKnowledgeStateV2
{
    Active,
    Superseded,
    Archived
}

public enum CortexKnowledgeTierV2
{
    Explicit,
    Verified,
    Inferred
}

public enum CortexProvenanceSourceV2
{
    User,
    Agent,
    Imported,
    System
}

public enum CortexFinishTaskActionV2
{
    None,
    Update,
    Block,
    Complete
}

public enum CortexContextDispositionV2
{
    Snapshot,
    Delta,
    Unchanged
}

public enum CortexRecordTypeV2
{
    Task,
    Knowledge
}

public enum CortexResponseFormatV2
{
    Detailed,
    Compact
}

/// <summary>Host-supplied transport attribution, separate from declared participant labels and Core permissions.</summary>
public sealed record CortexInvocationSourceV2(string Kind, string Authority, string Subject, long Generation, string? Label = null)
{
    public CortexInvocationSourceV2 Validate()
    {
        const string prefix = "cortex.mcp/";
        if (Kind != "approved-mcp-connection" || Authority is null || !Authority.StartsWith(prefix, StringComparison.Ordinal) ||
            !GuidValue(Authority[prefix.Length..]) || !GuidValue(Subject) || Generation < 1 ||
            Label is not null && (string.IsNullOrWhiteSpace(Label) || Label.Length > 256 || Label.Any(char.IsControl)))
            throw new ArgumentException("A bounded host-verified invocation source is required.");
        return this;
    }

    private static bool GuidValue(string? value) =>
        Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty ||
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
}

public sealed record CortexProvenanceV2(
    CortexProvenanceSourceV2 Source,
    string Actor,
    string? SourceId,
    string? Evidence,
    DateTimeOffset CapturedAtUtc,
    CortexInvocationSourceV2? VerifiedSource = null);

public sealed record CortexProvenanceInputV2(
    [property: JsonRequired] CortexProvenanceSourceV2 Source,
    string? SourceId,
    string? Evidence);

public sealed record CortexProvenanceReceiptV2(
    CortexProvenanceSourceV2 Source,
    string Actor,
    string? SourceId,
    DateTimeOffset CapturedAtUtc,
    CortexInvocationSourceV2? VerifiedSource = null);

public sealed record CortexStatusV2(
    string Product,
    string Version,
    string State,
    string StorageKind,
    bool Network,
    bool Service,
    long NamespaceCount,
    long HeadCount,
    long RevisionCount,
    IReadOnlyList<string> EffectiveNamespaces,
    CortexRetrievalMetricsV2? RetrievalMetrics = null,
    CortexPersonalProfileV2? PersonalProfile = null);

public sealed record CortexRetrievalOperationMetricsV2(
    long Calls,
    long Failures,
    long ReturnedItems,
    long ContractResponseUtf8Bytes,
    double TotalDurationMilliseconds,
    double MaximumDurationMilliseconds);

public sealed record CortexRetrievalMetricsV2(
    string Schema,
    DateTimeOffset StartedAtUtc,
    CortexRetrievalOperationMetricsV2 Start,
    CortexRetrievalOperationMetricsV2 Search,
    CortexRetrievalOperationMetricsV2 Read,
    CortexRetrievalOperationMetricsV2 BatchRead,
    long ExactRecordsReturned);

public sealed record CortexNamespaceV2(
    string Namespace,
    string? DisplayName,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<string> EffectiveNamespaces);

public sealed record CortexNamespacesRequestV2(
    string? AfterNamespace,
    int Limit = CortexV2Limits.DefaultPageSize,
    int MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes);

public sealed record CortexNamespacesResultV2(
    IReadOnlyList<CortexNamespaceV2> Items,
    string? NextCursor,
    bool HasMore,
    IReadOnlyList<string> EffectiveNamespaces);

public sealed record CortexContextItemV2(
    string Namespace,
    string RecordType,
    string RecordId,
    long Revision,
    string Title,
    string Excerpt,
    CortexKnowledgeKindV2? Kind,
    CortexKnowledgeTierV2? Tier,
    CortexProvenanceReceiptV2? Provenance,
    double Score);

public sealed record CortexStartRequestV2(
    IReadOnlyList<string> Namespaces,
    string Intent,
    string? KnownContextCursor,
    int MaxUtf8Bytes = CortexV2Limits.DefaultStartResponseUtf8Bytes,
    string? TaskId = null,
    string? PhaseId = null,
    CortexResponseFormatV2 Format = CortexResponseFormatV2.Detailed);

public sealed record CortexStartResultV2(
    CortexContextDispositionV2 Disposition,
    IReadOnlyList<string> EffectiveNamespaces,
    string ContextCursor,
    IReadOnlyList<CortexContextItemV2> Items,
    bool HasMore,
    CortexTaskSummaryV2? CurrentTask = null,
    CortexPersonalProfileV2? PersonalProfile = null);

public sealed record CortexSearchRequestV2(
    IReadOnlyList<string> Namespaces,
    string Text,
    IReadOnlyList<CortexKnowledgeKindV2>? Kinds,
    string? AfterCursor,
    int Limit = CortexV2Limits.DefaultPageSize,
    int MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes,
    CortexResponseFormatV2 Format = CortexResponseFormatV2.Detailed);

public sealed record CortexSearchHitV2(
    string Namespace,
    CortexRecordTypeV2 RecordType,
    string RecordId,
    long Revision,
    string Title,
    string Excerpt,
    CortexKnowledgeKindV2? Kind,
    CortexKnowledgeTierV2? Tier,
    CortexProvenanceReceiptV2 Provenance,
    double Score);

public sealed record CortexSearchResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    IReadOnlyList<CortexSearchHitV2> Items,
    string? NextCursor,
    bool HasMore,
    CortexPersonalProfileV2? PersonalProfile = null);

public sealed record CortexReadRequestV2(
    string Namespace,
    [property: JsonRequired] CortexRecordTypeV2 RecordType,
    string RecordId,
    long? Revision,
    int MaxUtf8Bytes = CortexV2Limits.MaximumResponseUtf8Bytes);

public sealed record CortexReadResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    CortexRecordTypeV2 RecordType,
    string RecordId,
    long Revision,
    object? Record);

public sealed record CortexRecordCoordinateV2(
    string Namespace,
    [property: JsonRequired] CortexRecordTypeV2 RecordType,
    string RecordId,
    long? Revision);

public sealed record CortexReadBatchRequestV2(
    IReadOnlyList<CortexRecordCoordinateV2> Records,
    int MaxUtf8Bytes = CortexV2Limits.MaximumResponseUtf8Bytes,
    CortexResponseFormatV2 Format = CortexResponseFormatV2.Detailed);

public sealed record CortexReadBatchItemV2(
    CortexRecordCoordinateV2 Coordinate,
    object Record);

public sealed record CortexReadBatchResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    IReadOnlyList<CortexReadBatchItemV2> Items);

public sealed record CortexTaskV2(
    string Namespace,
    string TaskId,
    long Revision,
    CortexTaskStatusV2 Status,
    string Title,
    string Content,
    string? Assignee,
    string? ClaimedBy,
    IReadOnlyList<string> Dependencies,
    string? BlockedReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    CortexProvenanceV2 Provenance,
    string? PhaseId = null,
    string? CheckpointSubject = null)
{
    public string ParticipantIdentityKind => "declared-label";
}

public sealed record CortexTaskSummaryV2(
    string Namespace,
    string TaskId,
    long Revision,
    CortexTaskStatusV2 Status,
    string Title,
    string Excerpt,
    string? Assignee,
    string? ClaimedBy,
    string? BlockedReason,
    string? PhaseId = null,
    string? CheckpointSubject = null,
    CortexInvocationSourceV2? VerifiedSource = null)
{
    public string ParticipantIdentityKind => "declared-label";
}

public sealed record CortexTaskHistoryEventV2(
    string Namespace,
    string TaskId,
    long Revision,
    CortexTaskStatusV2 Status,
    string Title,
    string Event,
    string Actor,
    DateTimeOffset OccurredAtUtc,
    bool ContentChanged,
    string ContentHash,
    string? PhaseId = null,
    string? CheckpointSubject = null,
    CortexInvocationSourceV2? VerifiedSource = null);

public sealed record CortexTaskCommandV2(
    string Namespace,
    [property: JsonRequired] CortexTaskActionV2 Action,
    string? TaskId,
    long? ExpectedRevision,
    string? IdempotencyKey,
    string? Title,
    string? Content,
    string? Assignee,
    string? ClaimedBy,
    string? Reason,
    string? DependencyId,
    IReadOnlyList<string>? Dependencies,
    long? FirstRevision,
    long? LastRevision,
    string? AfterTaskId,
    int Limit = CortexV2Limits.DefaultPageSize,
    int MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes,
    string? PhaseId = null,
    string? CheckpointSubject = null)
{
    [JsonIgnore]
    public CortexProvenanceV2? EffectiveProvenance { get; init; }
}

public sealed record CortexTaskResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    string Disposition,
    CortexTaskSummaryV2? Task,
    IReadOnlyList<CortexTaskSummaryV2> Items,
    IReadOnlyList<CortexTaskHistoryEventV2>? History,
    string? NextCursor);

public sealed record CortexKnowledgeV2(
    string Namespace,
    string RecordId,
    long Revision,
    CortexKnowledgeStateV2 State,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    CortexProvenanceV2 Provenance,
    string? SupersededByRecordId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc);

public sealed record CortexKnowledgeCommandV2(
    string Namespace,
    [property: JsonRequired] CortexKnowledgeActionV2 Action,
    string? RecordId,
    long? ExpectedRevision,
    string? IdempotencyKey,
    CortexKnowledgeKindV2? Kind,
    CortexKnowledgeTierV2? Tier,
    string? Title,
    string? Content,
    IReadOnlyList<string>? Tags,
    CortexProvenanceInputV2? Provenance,
    string? ReplacementRecordId,
    int MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes,
    string? AfterRecordId = null,
    int Limit = CortexV2Limits.DefaultPageSize)
{
    [JsonIgnore]
    public CortexProvenanceV2? EffectiveProvenance { get; init; }
}

public sealed record CortexKnowledgeResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    string Disposition,
    CortexKnowledgeReceiptV2? Record,
    CortexConsolidationAnalysisV2? Analysis = null);

public sealed record CortexConsolidationSuggestionV2(
    string Reason,
    CortexRecordCoordinateV2 Canonical,
    CortexRecordCoordinateV2 Candidate,
    string Evidence);

public sealed record CortexConsolidationAnalysisV2(
    string Schema,
    int HeadsAnalyzed,
    bool ScanTruncated,
    bool SuggestionsTruncated,
    IReadOnlyList<CortexConsolidationSuggestionV2> Suggestions,
    string? NextCursor);

public sealed record CortexKnowledgeReceiptV2(
    string Namespace,
    string RecordId,
    long Revision,
    CortexKnowledgeStateV2 State,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    CortexProvenanceReceiptV2 Provenance);

public sealed record CortexFinishKnowledgeWriteV2(
    string RecordId,
    long? ExpectedRevision,
    [property: JsonRequired] CortexKnowledgeKindV2 Kind,
    [property: JsonRequired] CortexKnowledgeTierV2 Tier,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    CortexProvenanceInputV2 Provenance,
    string IdempotencyKey)
{
    [JsonIgnore]
    public CortexProvenanceV2? EffectiveProvenance { get; init; }
}

public sealed record CortexFinishRequestV2(
    string Namespace,
    string TaskId,
    long ExpectedRevision,
    [property: JsonRequired] CortexFinishTaskActionV2 TaskAction,
    string? TaskTitle,
    string? TaskContent,
    string? BlockedReason,
    string Checkpoint,
    string Evidence,
    string? Handoff,
    IReadOnlyList<CortexFinishKnowledgeWriteV2> Knowledge,
    string IdempotencyKey,
    int MaxUtf8Bytes = CortexV2Limits.MaximumCompactResponseUtf8Bytes,
    string? PhaseId = null,
    string? CheckpointSubject = null)
{
    [JsonIgnore]
    public CortexProvenanceV2? EffectiveProvenance { get; init; }
}

public sealed record CortexFinishResultV2(
    IReadOnlyList<string> EffectiveNamespaces,
    string Disposition,
    CortexTaskSummaryV2 Task,
    IReadOnlyList<CortexKnowledgeReceiptV2> Knowledge,
    string ContextCursor);
