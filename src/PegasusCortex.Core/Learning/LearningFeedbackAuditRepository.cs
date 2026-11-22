using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Core.Learning;

public enum LearningFeedbackAuditWriteDisposition
{
    Added,
    Existing,
    Conflict,
    MissingProposal,
    ProposalHashConflict,
    AlreadyReviewed,
    GrantUnavailable
}

public sealed record LearningFeedbackReviewGrantWriteResult(
    LearningFeedbackAuditWriteDisposition Disposition,
    LearningFeedbackReviewGrantV1? Grant,
    LearningFeedbackReviewGrantRevocationV1? Revocation);

public sealed record LearningFeedbackAuditApplyResult(
    LearningFeedbackAuditWriteDisposition Disposition,
    LearningFeedbackAuditAssessmentV1? Assessment,
    LearningFeedbackReviewGrantUseV1? GrantUse,
    LearningFeedbackReviewDecisionV1? Decision,
    string Outcome);

/// <summary>
/// Append-only audit boundary. Implementations must atomically validate and consume
/// a review grant with the assessment, decision, and retained-record promotion.
/// </summary>
public interface ILearningFeedbackAuditRepository
{
    ValueTask<LearningFeedbackReviewGrantWriteResult> IssueReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewGrantV1 grant,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackReviewGrantWriteResult> RevokeReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        string reviewGrantId,
        string reason,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackAuditApplyResult> ApplyAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackAuditAssessmentV1 assessment,
        LearningFeedbackReviewDecisionV1? autoApprovalDecision,
        bool collectiveLibrary,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackAuditAssessmentV1?> FindLatestAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken);
}
