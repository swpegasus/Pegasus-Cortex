using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Operations;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Provider-neutral capabilities exposed by an optional project runtime.
/// Capabilities describe authenticated authority; a tool is advertised only
/// when both its capability and its narrow runtime port are present.
/// </summary>
[Flags]
public enum CortexMcpRuntimeCapability
{
    None = 0,
    Read = 1 << 0,
    Correlate = 1 << 1,
    Propose = 1 << 2,
    Review = 1 << 3,
    Audit = 1 << 4,
    DelegateReview = 1 << 5,
    RetainedRead = 1 << 6,
    RetainedWrite = 1 << 7,
    ProjectBrief = 1 << 8,
    Sync = 1 << 9,
    Tasks = 1 << 10,
    KnowledgeWrite = 1 << 11
}

/// <summary>
/// Content-safe runtime metadata used for discovery and status projections.
/// It contains no connection strings, paths, credentials, or provider objects.
/// </summary>
public sealed record CortexMcpRuntimeDescriptor(
    string Profile,
    string State,
    string? IssueCode,
    string? EndpointId,
    string? PrincipalName,
    string? SubjectId,
    CortexMcpRuntimeCapability Capabilities,
    bool UsesNetwork,
    bool UsesDatabase,
    bool UsesAuthentication,
    bool UsesLiveServices,
    string Routing,
    string StorageKind = CortexMcpStorageKinds.None,
    bool SupportsProvisionalPublication = false)
{
    public bool HasCapability(CortexMcpRuntimeCapability capability) =>
        string.Equals(State, "ready", StringComparison.Ordinal) &&
        (Capabilities & capability) == capability;

    /// <summary>
    /// Fails composition when the public storage projection would contradict
    /// the selected runtime. This keeps discovery/status truthful without
    /// exposing provider connection details.
    /// </summary>
    public void Validate()
    {
        if (!CortexMcpStorageKinds.IsSupported(StorageKind) ||
            UsesDatabase != !string.Equals(
                StorageKind,
                CortexMcpStorageKinds.None,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Cortex runtime storage descriptor is inconsistent.");
        }
    }

    public static CortexMcpRuntimeDescriptor PortableProjectBrief() =>
        new(
            "portable-project-brief",
            "ready",
            IssueCode: null,
            EndpointId: null,
            PrincipalName: null,
            SubjectId: null,
            CortexMcpRuntimeCapability.Read |
                CortexMcpRuntimeCapability.ProjectBrief,
            UsesNetwork: false,
            UsesDatabase: false,
            UsesAuthentication: false,
            UsesLiveServices: false,
            "Use the supplied provider-neutral read model for exact project briefs.");
}

public sealed record CortexCollectiveSearchHit(
    CortexKnowledgeSearchHit Hit,
    string ProjectLinkId);

/// <summary>
/// Optional proposal 1.1 metadata. A null value preserves the exact legacy 1.0
/// proposal identity; supplied values are independently revalidated by runtime
/// and contract boundaries.
/// </summary>
public sealed record CortexProposalMetadataV1(
    string? Taxonomy,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<LearningFeedbackApplicabilityV1> Applicability);

public sealed record CortexProposalSubmissionResult(
    LearningFeedbackSubmissionResult Submission,
    LearningFeedbackReviewResult? ProvisionalReview,
    bool ProvisionalPublicationAttempted,
    LearningFeedbackReviewDecisionV1? ExistingDecision);

/// <summary>
/// Provider-neutral projection of one passive local-retention CAS outcome.
/// SubmittedContentHash identifies the exact canonical bytes offered by the
/// caller; ActualHeadHash is always the repository's observed head.
/// </summary>
public sealed record CortexRetainedAssertionWriteResult(
    string Disposition,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string Kind,
    string DocumentId,
    long Revision,
    string AssertionHash,
    string SubmittedContentHash,
    string? RetainedContentHash,
    string? ActualHeadHash,
    string? ProjectBriefDisposition = null,
    string? ProjectBriefContextSetId = null,
    string? ProjectionIssueCode = null);

/// <summary>
/// Additional bounded outcome used only when a task snapshot is deterministically
/// projected into the project-brief read model.
/// </summary>
public static class CortexProjectBriefMaterializationDispositions
{
    public const string AlreadyAdvanced = "already-advanced";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Exact retained bytes and their immutable storage identity. CanonicalUtf8 is
/// passive untrusted data and must never be interpreted as runtime authority.
/// </summary>
public sealed record CortexRetainedAssertionRecord(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string Kind,
    string DocumentId,
    long Revision,
    string AssertionHash,
    string ContentHash,
    string ActualHeadHash,
    string ContractId,
    byte[] CanonicalUtf8);

public interface ICortexProjectKnowledgeRuntime
{
    ValueTask<CortexProjectSearchResult?> SearchProjectAsync(
        string projectLinkId,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken);

    ValueTask<CortexCollectiveSearchResult?> SearchCollectiveAsync(
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken);

    ValueTask<CortexKnowledgeRecord?> ReadProjectAsync(
        string projectLinkId,
        string recordId,
        string revisionId,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken);
}

public interface ICortexProjectCorrelationRuntime
{
    CortexPreparedProjectLink PrepareProjectLink(
        string projectId,
        string storeId,
        long storeEpoch,
        string contractVersion,
        string libraryAccess);

    ValueTask<CortexProjectLinkWriteResult> ConfirmProjectLinkAsync(
        string projectId,
        string storeId,
        long storeEpoch,
        string contractVersion,
        string libraryAccess,
        string correlationHash,
        CancellationToken cancellationToken);
}

public interface ICortexProposalSubmissionRuntime
{
    ValueTask<CortexProposalSubmissionResult?> SubmitProposalAsync(
        string projectLinkId,
        string idempotencyKey,
        string feedbackKind,
        string title,
        string summary,
        string content,
        CortexProposalMetadataV1? metadata,
        IReadOnlyList<LearningFeedbackEvidenceV1> evidenceReferences,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}

public interface ICortexProposalQueryRuntime
{
    ValueTask<LearningFeedbackPageResult?> ListProposalsAsync(
        string projectLinkId,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackAuditAssessmentV1?> FindLatestAssessmentAsync(
        string projectLinkId,
        string proposalId,
        CancellationToken cancellationToken);
}

public interface ICortexProposalReviewRuntime
{
    ValueTask<LearningFeedbackReviewResult?> ReviewProposalAsync(
        string projectLinkId,
        string proposalId,
        string state,
        string reason,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Author-owned append-only maintenance for an already published knowledge
/// record. The runtime resolves the project namespace from the protected link;
/// caller-provided identity never crosses this port.
/// </summary>
public interface ICortexPublicationChangeRuntime
{
    ValueTask<LearningFeedbackPublicationChangeResult?> ChangePublicationAsync(
        string projectLinkId,
        LearningFeedbackPublicationChangeRequestV1 request,
        CancellationToken cancellationToken);
}

public interface ICortexProposalAuditRuntime
{
    ValueTask<LearningFeedbackAuditApplyResult?> SubmitAuditAsync(
        string projectLinkId,
        string proposalId,
        string recommendation,
        string rationale,
        string? reviewGrantId,
        DateTimeOffset assessedAt,
        CancellationToken cancellationToken);
}

public interface ICortexReviewGrantRuntime
{
    ValueTask<LearningFeedbackReviewGrantWriteResult?> IssueReviewGrantAsync(
        string projectLinkId,
        string reviewGrantId,
        string delegateAuthorityId,
        string delegateSubjectId,
        IReadOnlyList<string> feedbackKinds,
        int maximumUses,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackReviewGrantWriteResult?> RevokeReviewGrantAsync(
        string projectLinkId,
        string reviewGrantId,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow append port for the two typed passive agent-retention contracts.
/// Project authorization is provider-owned and must fail closed.
/// </summary>
public interface ICortexRetainedAssertionWriterRuntime
{
    ValueTask<CortexRetainedAssertionWriteResult?> SubmitTaskSnapshotAsync(
        string projectLinkId,
        string projectNamespace,
        AgentTaskSnapshotV1 snapshot,
        CancellationToken cancellationToken);

    ValueTask<CortexRetainedAssertionWriteResult?> SubmitWorkPacketAsync(
        string projectLinkId,
        string projectNamespace,
        AgentWorkPacketV1 packet,
        CancellationToken cancellationToken);

    ValueTask<CortexRetainedAssertionWriteResult?>
        SubmitWorkPacketHandoffReceiptAsync(
            string projectLinkId,
            string projectId,
            string projectNamespace,
            AgentWorkPacketHandoffReceiptV1 receipt,
            CancellationToken cancellationToken);

    ValueTask<CortexRetainedAssertionWriteResult?>
        SubmitWorkPacketAcceptanceReceiptAsync(
            string projectLinkId,
            string projectId,
            string projectNamespace,
            AgentWorkPacketAcceptanceReceiptV1 receipt,
            CancellationToken cancellationToken);
}

/// <summary>
/// Exact passive retained-record port. Callers provide the expected content
/// hash so a changed or mismatched revision is unavailable rather than widened.
/// </summary>
public interface ICortexRetainedAssertionReaderRuntime
{
    ValueTask<CortexRetainedAssertionRecord?> ReadRetainedAssertionAsync(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral server-side synchronization port. It is intentionally not
/// represented as an MCP tool: integrated callers reach it only through the
/// Nexus-owned authenticated gateway.
/// </summary>
public interface ICortexSyncRuntime
{
    ValueTask<CortexSyncApplicationResult?> SubmitSyncBundleAsync(
        string targetNamespace,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncApplicationResult?> SubmitLegacyMigrationPageAsync(
        string targetNamespace,
        CortexLegacyMigrationPageV1 page,
        CancellationToken cancellationToken);
}

/// <summary>
/// Versioned reconnect gateway used behind the Nexus-authenticated transport.
/// This port is deliberately absent from the MCP tool list: it exposes only
/// capability discovery, an authenticated project/store head, and exact submit.
/// </summary>
public interface ICortexSyncGatewayRuntime
{
    ValueTask<CortexSyncCapabilitiesV1?> GetSyncCapabilitiesAsync(
        CancellationToken cancellationToken);

    ValueTask<CortexSyncRemoteHead?> GetSyncHeadAsync(
        CortexSyncHeadRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncSubmitResult?> SubmitSyncAsync(
        CortexSyncSubmitRequest request,
        CancellationToken cancellationToken);
}

public interface ICortexOperationalRuntime
{
    ValueTask<CortexOperationalWriteResult> CreateTaskAsync(
        string ns, string taskId, string title, string content,
        string? assignee, IReadOnlyList<string> dependencies,
        string idempotencyKey, CancellationToken cancellationToken);

    ValueTask<CortexOperationalWriteResult> MutateTaskAsync(
        string ns, string taskId, long expectedRevision, string operation,
        string? title, string? content, string? assignee, string? claimedBy,
        string? reason, string? dependencyId, string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<CortexOperationalTask?> ReadTaskAsync(
        string ns, string taskId, CancellationToken cancellationToken);

    ValueTask<CortexOperationalPage<CortexOperationalTask>> ListTasksAsync(
        string ns, string? afterTaskId, int limit,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CortexOperationalTask>> ReadTaskHistoryAsync(
        string ns, string taskId, long firstRevision, long lastRevision,
        CancellationToken cancellationToken);

    ValueTask<CortexOperationalWriteResult> CreateKnowledgeAsync(
        string ns, string recordId, string title, string content,
        IReadOnlyList<string> tags, string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<CortexOperationalWriteResult> MutateKnowledgeAsync(
        string ns, string recordId, long expectedRevision, string operation,
        string? title, string? content, IReadOnlyList<string>? tags,
        string? replacementRecordId, string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<CortexOperationalKnowledge?> ReadKnowledgeAsync(
        string ns, string recordId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CortexOperationalKnowledge>> SearchKnowledgeAsync(
        string ns, string text, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Optional runtime ports are composed explicitly by the selected executable
/// profile. The MCP application never discovers providers or activates a
/// database from environment variables.
/// </summary>
public sealed record CortexMcpRuntimeComposition(
    CortexMcpRuntimeDescriptor Descriptor,
    ICortexProjectKnowledgeRuntime? ProjectKnowledge = null,
    ICortexProjectCorrelationRuntime? ProjectCorrelation = null,
    ICortexProposalSubmissionRuntime? ProposalSubmission = null,
    ICortexProposalQueryRuntime? ProposalQuery = null,
    ICortexProposalReviewRuntime? ProposalReview = null,
    ICortexProposalAuditRuntime? ProposalAudit = null,
    ICortexReviewGrantRuntime? ReviewGrant = null,
    ICortexProjectBriefRuntime? ProjectBrief = null,
    ICortexRetainedAssertionReaderRuntime? RetainedAssertionReader = null,
    ICortexRetainedAssertionWriterRuntime? RetainedAssertionWriter = null,
    ICortexSyncRuntime? Sync = null,
    ICortexSyncGatewayRuntime? SyncGateway = null,
    ICortexPublicationChangeRuntime? PublicationChange = null,
    ICortexOperationalRuntime? Operations = null)
{
    public bool HasCapability(CortexMcpRuntimeCapability capability) =>
        Descriptor.HasCapability(capability);

    // Read implied project-brief access in profiles that predate the explicit
    // bit. New integrated authority emits ProjectBrief separately, while this
    // compatibility rule avoids invalidating the installed standalone profile.
    public bool CanUseProjectBrief =>
        ProjectBrief is not null &&
        (HasCapability(CortexMcpRuntimeCapability.ProjectBrief) ||
         HasCapability(CortexMcpRuntimeCapability.Read));
}
