using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Learning;

public static class LearningFeedbackApplicationLimits
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
}

public sealed record SubmitLearningFeedbackCommand(
    LearningFeedbackProposalV1 Proposal,
    LearningFeedbackAuthorityContext ServerAuthority);

public sealed record ReviewLearningFeedbackCommand(
    LearningFeedbackReviewDecisionV1 Decision,
    LearningFeedbackAuthorityContext ServerAuthority);

public sealed record ListLearningFeedbackQuery(
    string Namespace,
    string? AfterProposalId,
    int Limit,
    LearningFeedbackAuthorityContext ServerAuthority,
    LearningFeedbackNamespaceCapability Capability = LearningFeedbackNamespaceCapability.Read);

public sealed record GetLearningFeedbackStatusQuery(
    string Namespace,
    LearningFeedbackAuthorityContext ServerAuthority);

public enum LearningFeedbackMutationDisposition
{
    Accepted,
    Idempotent,
    Rejected,
    Conflict
}

/// <summary>
/// Immutable proposal and optional terminal review decision returned by the application.
/// The proposal remains byte-identical; its effective state is derived from the decision.
/// </summary>
public sealed record LearningFeedbackSnapshot(
    LearningFeedbackProposalV1 Proposal,
    LearningFeedbackReviewDecisionV1? Decision)
{
    public string ReviewState => Decision?.State ?? "pending";
}

/// <summary>
/// Metadata safe for an audit sink. It deliberately excludes summary, payload, evidence,
/// review reason, and all other caller-supplied content.
/// </summary>
public sealed record LearningFeedbackAuditMetadata(
    string Operation,
    LearningFeedbackMutationDisposition Disposition,
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string ProposalId,
    string ProposalHash,
    string? DecisionId,
    string? DecisionHash,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record LearningFeedbackSubmissionResult(
    LearningFeedbackMutationDisposition Disposition,
    LearningFeedbackProposalV1? Proposal,
    string? SubmittedPayloadHash,
    LearningFeedbackAuditMetadata? Audit,
    IReadOnlyList<PegasusDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Disposition is LearningFeedbackMutationDisposition.Accepted or
            LearningFeedbackMutationDisposition.Idempotent;
}

public sealed record LearningFeedbackReviewResult(
    LearningFeedbackMutationDisposition Disposition,
    LearningFeedbackReviewDecisionV1? Decision,
    string? SubmittedPayloadHash,
    LearningFeedbackAuditMetadata? Audit,
    IReadOnlyList<PegasusDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Disposition is LearningFeedbackMutationDisposition.Accepted or
            LearningFeedbackMutationDisposition.Idempotent;
}

public sealed record LearningFeedbackPage(
    IReadOnlyList<LearningFeedbackSnapshot> Items,
    string? NextAfterProposalId);

public sealed record LearningFeedbackPageResult(
    bool Accepted,
    LearningFeedbackPage? Page,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);

public sealed record LearningFeedbackStatusProjection(
    string TenantId,
    string Namespace,
    long Total,
    long Pending,
    long Approved,
    long Rejected);

public sealed record LearningFeedbackStatusResult(
    bool Accepted,
    LearningFeedbackStatusProjection? Status,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);

public interface ILearningFeedbackApplicationService
{
    ValueTask<LearningFeedbackSubmissionResult> SubmitAsync(
        SubmitLearningFeedbackCommand command,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackReviewResult> ReviewAsync(
        ReviewLearningFeedbackCommand command,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackPageResult> ListAsync(
        ListLearningFeedbackQuery query,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackStatusResult> GetStatusAsync(
        GetLearningFeedbackStatusQuery query,
        CancellationToken cancellationToken);
}
