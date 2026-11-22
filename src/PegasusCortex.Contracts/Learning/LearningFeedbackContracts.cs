using System.Text.Json.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Contracts.Learning;

public static class LearningFeedbackContractIds
{
    // Proposal remains the 1.0 alias so existing producers keep their exact
    // canonical and idempotency identity when no 1.1 metadata is supplied.
    public const string Proposal = "pegasus.cortex.learning-feedback-proposal/1.0";
    public const string ProposalV1_1 = "pegasus.cortex.learning-feedback-proposal/1.1";
    public const string ReviewDecision = "pegasus.cortex.learning-feedback-review-decision/1.0";
    public const string AuditAssessment = "pegasus.cortex.learning-feedback-audit-assessment/1.0";
    public const string ReviewGrant = "pegasus.cortex.learning-feedback-review-grant/1.0";
    public const string ReviewGrantRevocation = "pegasus.cortex.learning-feedback-review-grant-revocation/1.0";
    public const string ReviewGrantUse = "pegasus.cortex.learning-feedback-review-grant-use/1.0";
    public const string PublicationChange = "pegasus.cortex.publication-change/1.0";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string ContentTreatment = "untrusted_data";

    public static bool IsSupportedProposal(string? contractId) =>
        string.Equals(contractId, Proposal, StringComparison.Ordinal) ||
        string.Equals(contractId, ProposalV1_1, StringComparison.Ordinal);
}

public static class LearningFeedbackContractLimits
{
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumNamespaceCharacters = 256;
    public const int MaximumSummaryCharacters = 4096;
    public const int MaximumPayloadFields = 32;
    public const int MaximumPayloadCharacters = 64 * 1024;
    public const int MaximumEvidenceItems = 32;
    public const int MaximumEvidenceReferenceCharacters = 512;
    public const int MaximumIntendedUses = 4;
    public const int MaximumProposalKeywords = 16;
    public const int MaximumProposalApplicabilityItems = 16;
    public const int MaximumProposalKeywordCharacters = 128;
    public const int MaximumReviewReasonCharacters = 2048;
    public const int MaximumAuditRationaleCharacters = 2048;
    public const int MaximumSerializedProposalBytes = 128 * 1024;
    public const int MaximumAuthorityEntries = 256;
    public const int MaximumGrantFeedbackKinds = 2;
    public const int MaximumGrantUses = 1000;
    public static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromDays(30);
}

public sealed record LearningFeedbackSourceV1(
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    string? AgentId,
    string? InstanceId,
    string? MachineId,
    string OperationId,
    string? TaskId,
    string CorrelationId,
    string RecordId,
    string RevisionId,
    string ContentHash,
    DateTimeOffset OccurredAt);

public sealed record LearningFeedbackEvidenceV1(
    string EvidenceId,
    string Reference,
    string MediaType,
    string Sha256,
    string Verification);

/// <summary>
/// A bounded product/component coordinate supplied as screened proposal metadata.
/// Version and applicability state remain server-owned promotion projections.
/// </summary>
public sealed record LearningFeedbackApplicabilityV1(
    string ProductId,
    string ComponentId);

/// <summary>
/// Records a trusted upstream screening decision. It is necessary but not sufficient for
/// publication or dataset inclusion; a separate review decision remains mandatory.
/// </summary>
public sealed record LearningFeedbackScreeningV1(
    string RedactionDecision,
    string PrivacyReview,
    string SecretReview,
    string ReasoningReview,
    string ProviderCredentialReview,
    string UsageRights,
    long PolicyRevision,
    string AuthorityId,
    string DecisionId,
    string DecisionHash,
    DateTimeOffset DecidedAt);

/// <summary>
/// A bounded, non-executable proposal. It carries sanitized observations, never a raw
/// conversation, terminal stream, credential, or private chain of thought.
/// </summary>
public sealed record LearningFeedbackProposalV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string ProposalId,
    string TenantId,
    string Namespace,
    string FeedbackKind,
    LearningFeedbackSourceV1 Source,
    string Summary,
    IReadOnlyDictionary<string, string> Payload,
    IReadOnlyList<string> IntendedUses,
    IReadOnlyList<LearningFeedbackEvidenceV1> Evidence,
    LearningFeedbackScreeningV1 Screening,
    string ReviewState,
    string ContentTreatment,
    bool Executable,
    DateTimeOffset CreatedAt,
    string ProposalHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Taxonomy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Keywords = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LearningFeedbackApplicabilityV1>? Applicability = null);

public sealed record LearningFeedbackReviewDecisionV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string DecisionId,
    string ProposalId,
    string ProposalHash,
    string TenantId,
    string Namespace,
    string State,
    string AuthorityId,
    string ReviewerSubjectId,
    long PolicyRevision,
    string Reason,
    DateTimeOffset CreatedAt,
    string DecisionHash);

/// <summary>
/// An advisory AI or human audit assessment. Rejection and escalation remain
/// non-terminal; approval can become terminal only through a valid bounded grant.
/// </summary>
public sealed record LearningFeedbackAuditAssessmentV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string AssessmentId,
    string ProposalId,
    string ProposalHash,
    string TenantId,
    string Namespace,
    string Recommendation,
    string AuthorityId,
    string AuditorSubjectId,
    long PolicyRevision,
    string Rationale,
    string? ReviewGrantId,
    double RetentionConfidence,
    DateTimeOffset CreatedAt,
    string AssessmentHash);

/// <summary>
/// Immutable, time-limited authority delegated to one qualified subject. Grant
/// consumption is represented by separate immutable use receipts.
/// </summary>
public sealed record LearningFeedbackReviewGrantV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string GrantId,
    string TenantId,
    string Namespace,
    string AuthorityId,
    string IssuerSubjectId,
    string DelegateAuthorityId,
    string DelegateSubjectId,
    long PolicyRevision,
    IReadOnlyList<string> FeedbackKinds,
    bool CollectiveOnly,
    int MaximumUses,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string GrantHash);

public sealed record LearningFeedbackReviewGrantRevocationV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string RevocationId,
    string GrantId,
    string GrantHash,
    string TenantId,
    string Namespace,
    string AuthorityId,
    string RevokerSubjectId,
    long PolicyRevision,
    string Reason,
    DateTimeOffset CreatedAt,
    string RevocationHash);

public sealed record LearningFeedbackReviewGrantUseV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string UseId,
    string GrantId,
    string GrantHash,
    string AssessmentId,
    string AssessmentHash,
    string ProposalId,
    string ProposalHash,
    string TenantId,
    string Namespace,
    string AuthorityId,
    string DelegateSubjectId,
    DateTimeOffset CreatedAt,
    string UseHash);

/// <summary>
/// Server-owned authority. Serialized feedback cannot create or extend these grants.
/// </summary>
public sealed record LearningFeedbackAuthorityContext(
    string TenantId,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    IReadOnlyList<LearningFeedbackNamespaceGrant> NamespaceGrants,
    IReadOnlySet<string> TrustedScreeningAuthorities,
    IReadOnlyDictionary<string, string> ApprovedScreeningDecisionHashes);

public enum LearningFeedbackNamespaceCapability
{
    Read,
    Submit,
    Review,
    Audit,
    DelegateReview
}

public sealed record LearningFeedbackNamespaceGrant(
    string Namespace,
    bool CanSubmit,
    bool CanReview = false,
    bool CanRead = false,
    bool CanAudit = false,
    bool CanDelegateReview = false);

/// <summary>
/// Immutable server-derived scope returned after an exact namespace capability check.
/// Feedback records cannot create this scope or extend its authority.
/// </summary>
public sealed record LearningFeedbackNamespaceAccess(
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    LearningFeedbackNamespaceCapability Capability);

public sealed record CortexContractResult<T>(
    bool Accepted,
    T? Value,
    string? SubmittedPayloadHash,
    IReadOnlyList<PegasusDiagnostic> Diagnostics)
    where T : class;
