using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public static class CortexOfflineSchemas
{
    public const string FrameV1 = "pegasus.cortex.offline-frame/1.0";
    public const string KnowledgeCreateV1 = "pegasus.cortex.offline.knowledge-create/1.0";
    public const string ReceiptV1 = "pegasus.cortex.offline.ingest-receipt/1.0";
    public const string HotV1 = "pegasus.cortex.offline-hot/1.0";
    public const string CanonicalizationV1 = "pegasus-canonical-json-v1";
    public const string PayloadEncoding = "base64url";
    public const string ContentTreatment = "untrusted_data";
}

public static class CortexOfflineLimits
{
    public const int MaximumPendingFrames = 32;
    public const int MaximumBodyCharacters = 24 * 1024;
    public const int MaximumPendingUtf8Bytes = 256 * 1024;
    public const int MaximumTotalFrames = 2048;
    public const int MaximumFrameUtf8Bytes = 256 * 1024;
    public const int MaximumPayloadUtf8Bytes = 192 * 1024;
    public const int MaximumTitleCharacters = CortexV2Limits.MaximumTitleCharacters;
    public const int MaximumSearchCharacters = CortexFileV2Limits.MaximumSearchCharacters;
    public const int MaximumPageSize = 32;
}

public enum CortexOfflineFrameStatus
{
    Pending,
    Receipt,
    UnsupportedPayload,
    UnsupportedOuterSchema
}

public enum CortexOfflineOperationStatus
{
    Created,
    Existing,
    Replaced,
    AlreadyReplaced,
    Adapted,
    UnsupportedPayload,
    UnsupportedOuterSchema
}

/// <summary>
/// Frozen transport envelope. Payload revisions evolve independently; the
/// outer frame remains data-only and cannot acquire executable semantics.
/// </summary>
public sealed record CortexOfflineFrameV1(
    string Schema,
    string Canonicalization,
    string FrameId,
    DateTimeOffset CreatedAtUtc,
    string ContentTreatment,
    bool Executable,
    string PayloadSchema,
    string PayloadEncoding,
    int PayloadUtf8Bytes,
    string PayloadSha256,
    string PayloadBase64Url,
    string FrameSha256);

public sealed record CortexOfflineKnowledgeCreatePayloadV1(
    string Schema,
    string Namespace,
    string RecordId,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    CortexProvenanceSourceV2 Source,
    string SourceId,
    string Evidence,
    DateTimeOffset CapturedAtUtc);

public sealed record CortexOfflineReceiptPayloadV1(
    string Schema,
    string CandidateFrameId,
    string CandidateFrameSha256,
    string CandidatePayloadSha256,
    string CandidateSemanticSha256,
    string EffectiveNamespace,
    string RecordId,
    long Revision,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    string Disposition,
    string VerifiedContentSha256,
    DateTimeOffset VerifiedAtUtc);

public sealed record CortexOfflineFrameSummary(
    string FrameId,
    CortexOfflineFrameStatus Status,
    string OuterSchema,
    string? PayloadSchema,
    DateTimeOffset? CreatedAtUtc,
    string? Namespace,
    string? RecordId,
    string? Title,
    string? Excerpt,
    int Utf8Bytes);

public sealed record CortexOfflineCaptureResult(
    CortexOfflineOperationStatus Status,
    CortexOfflineFrameSummary Frame);

public sealed record CortexOfflineInspection(
    CortexOfflineFrameSummary Frame,
    bool IntegrityVerified,
    string? Diagnostic);

public sealed record CortexOfflineDecodedFrame(
    CortexOfflineFrameStatus Status,
    CortexOfflineFrameV1? Frame,
    byte[]? PayloadBytes,
    string OuterSchema,
    string? Diagnostic);

public sealed record CortexOfflineListRequest(
    string? AfterFrameId = null,
    CortexOfflineFrameStatus? Status = null,
    int Limit = CortexOfflineLimits.MaximumPageSize);

public sealed record CortexOfflineSearchRequest(
    string Query,
    string? AfterFrameId = null,
    int Limit = CortexOfflineLimits.MaximumPageSize);

public sealed record CortexOfflinePage(
    IReadOnlyList<CortexOfflineFrameSummary> Items,
    bool HasMore,
    string? NextCursor);

public sealed record CortexOfflinePendingRead(
    CortexOfflineFrameSummary Frame,
    CortexOfflineKnowledgeCreatePayloadV1 Payload,
    string FrameSha256,
    string PayloadSha256);

public sealed record CortexOfflineAdaptedCandidate(
    CortexOfflineOperationStatus Status,
    string TaskId,
    string ScopeIdentity,
    string FrameId,
    string FrameSha256,
    string PayloadSha256,
    string SemanticSha256,
    string SourcePayloadSchema,
    string CurrentPayloadSchema,
    byte[] CurrentCanonicalPayload,
    CortexKnowledgeCommandV2 Command);

public sealed record CortexOfflineAdaptation(
    CortexOfflineOperationStatus Status,
    CortexOfflineAdaptedCandidate? Candidate,
    string? Diagnostic);

public sealed record CortexOfflineReceiptResult(
    CortexOfflineOperationStatus Status,
    CortexOfflineFrameSummary Frame);

public sealed record CortexOfflineHotRefreshRequest(
    string ContextCursor,
    IReadOnlyList<string> EffectiveNamespaces,
    IReadOnlyList<CortexContextItemV2> Items,
    bool HasMore = false);

public sealed record CortexOfflineHotRefreshResult(
    string ContextCursor,
    int ItemCount,
    int Utf8Bytes,
    bool HasMore);

public sealed record CortexOfflineHotV1(
    string Schema,
    bool Generated,
    bool Authoritative,
    string TaskId,
    string ProjectId,
    string ContextCursor,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> EffectiveNamespaces,
    IReadOnlyList<CortexContextItemV2> Items,
    bool HasMore);

/// <summary>
/// Capability produced only after an exact central Cortex record is compared
/// with the adapted candidate. It contains no candidate body.
/// </summary>
public sealed class CortexOfflineVerifiedReceiptV1
{
    internal CortexOfflineVerifiedReceiptV1(
        CortexOfflineReceiptPayloadV1 payload,
        string taskId,
        string scopeIdentity)
    {
        Payload = payload;
        TaskId = CortexOfflineTaskIdentity.Require(taskId, nameof(taskId));
        if (scopeIdentity.Length != 64 || scopeIdentity.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase scope identity hash is required.", nameof(scopeIdentity));
        }
        ScopeIdentity = scopeIdentity;
    }

    internal CortexOfflineReceiptPayloadV1 Payload { get; }

    public string TaskId { get; }
    internal string ScopeIdentity { get; }
    public string CandidateFrameId => Payload.CandidateFrameId;
    public string EffectiveNamespace => Payload.EffectiveNamespace;
    public string RecordId => Payload.RecordId;
    public long Revision => Payload.Revision;
}
