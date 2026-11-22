namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// Defines the narrow server-owned path for publishing low-risk collective
/// learnings before a later independent review. Provisional publication never
/// applies to reserved libraries or corrective/safety feedback.
/// </summary>
public static class LearningFeedbackPublicationPolicy
{
    public const string ProvisionalAuthorityId = "cortex-provisional-policy";
    public const string ProvisionalReviewerSubjectId = "cortex-provisional-review";
    public const string ProvisionalReason =
        "Provisional publication after deterministic collective screening; deferred independent review required.";
    public const double ProvisionalRetentionConfidence = 0.50;
    public const string ProvisionalRetentionConfidenceBasis =
        "deterministic screening auto-publication; deferred independent review required";

    public static bool IsProvisionalAutoPublicationEligible(
        string feedbackKind,
        bool collectiveLibrary) =>
        collectiveLibrary &&
        LearningFeedbackAuditPolicy.IsAutoApprovalKind(feedbackKind);

    public static bool IsProvisionalDecision(LearningFeedbackReviewDecisionV1? decision) =>
        decision is not null &&
        string.Equals(decision.State, "approved", StringComparison.Ordinal) &&
        string.Equals(decision.AuthorityId, ProvisionalAuthorityId, StringComparison.Ordinal) &&
        string.Equals(
            decision.ReviewerSubjectId,
            ProvisionalReviewerSubjectId,
            StringComparison.Ordinal) &&
        string.Equals(decision.Reason, ProvisionalReason, StringComparison.Ordinal);
}
