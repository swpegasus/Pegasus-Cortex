using Pegasus.Cortex.Contracts.Learning;

internal static class LearningFeedbackSamples
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-14T06:15:00Z");
    private static readonly string ScreeningHash = new('b', 64);

    public static LearningFeedbackAuthorityContext Submitter(
        bool canSubmit = true,
        bool canRead = true,
        string namespaceName = "project:project-1") =>
        Authority(
            "nexus",
            "submitter-1",
            "pegasus-coder",
            new LearningFeedbackNamespaceGrant(namespaceName, canSubmit, CanRead: canRead));

    public static LearningFeedbackAuthorityContext Reviewer(
        string authorityId = "nexus",
        string subjectId = "reviewer-1",
        string serviceId = "cortex-review",
        bool canReview = true,
        bool canRead = true,
        string namespaceName = "project:project-1") =>
        Authority(
            authorityId,
            subjectId,
            serviceId,
            new LearningFeedbackNamespaceGrant(
                namespaceName,
                CanSubmit: false,
                CanReview: canReview,
                CanRead: canRead));

    public static LearningFeedbackAuthorityContext Reader(
        string namespaceName = "project:project-1") =>
        Authority(
            "nexus",
            "reader-1",
            "pegasus-nexus",
            new LearningFeedbackNamespaceGrant(namespaceName, CanSubmit: false, CanRead: true));

    public static LearningFeedbackAuthorityContext NoGrants() =>
        Authority("nexus", "denied-1", "pegasus-nexus", null);

    public static LearningFeedbackNamespaceAccess Access(
        LearningFeedbackAuthorityContext authority,
        LearningFeedbackNamespaceCapability capability,
        string namespaceName = "project:project-1") =>
        LearningFeedbackPolicy.AuthorizeNamespace(
            authority,
            namespaceName,
            capability,
            CancellationToken.None).Value!;

    public static LearningFeedbackProposalV1 Proposal(
        string proposalId = "proposal-1",
        DateTimeOffset? createdAt = null,
        string namespaceName = "project:project-1",
        string summary = "SENSITIVE-SUMMARY sanitized correction.",
        string payloadValue = "SENSITIVE-PAYLOAD bounded observation.")
    {
        var timestamp = createdAt ?? Now;
        return new LearningFeedbackProposalV1(
            1,
            LearningFeedbackContractIds.Proposal,
            LearningFeedbackContractIds.Canonicalization,
            proposalId,
            "tenant-1",
            namespaceName,
            "correction",
            new LearningFeedbackSourceV1(
                "nexus",
                "submitter-1",
                "pegasus-coder",
                "coder-agent-1",
                "instance-1",
                "machine-1",
                $"operation:{proposalId}",
                "task-1",
                $"correlation:{proposalId}",
                $"record:{proposalId}",
                "revision-1",
                new string('a', 64),
                timestamp),
            summary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["observation"] = payloadValue
            },
            ["retrieval", "evaluation"],
            [new LearningFeedbackEvidenceV1(
                "evidence-1",
                $"artifact://verification/{proposalId}",
                "application/json",
                new string('c', 64),
                "verified")],
            new LearningFeedbackScreeningV1(
                "sanitized",
                "passed",
                "passed",
                "passed",
                "passed",
                "training_and_evaluation",
                7,
                "cortex-screening",
                "screening-1",
                ScreeningHash,
                timestamp),
            "pending",
            LearningFeedbackContractIds.ContentTreatment,
            false,
            timestamp,
            new string('0', 64));
    }

    public static LearningFeedbackReviewDecisionV1 Decision(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuthorityContext reviewer,
        string decisionId = "review-1",
        string state = "approved",
        string reason = "SENSITIVE-REASON verified evidence supports the decision.") =>
        new(
            1,
            LearningFeedbackContractIds.ReviewDecision,
            LearningFeedbackContractIds.Canonicalization,
            decisionId,
            proposal.ProposalId,
            proposal.ProposalHash,
            proposal.TenantId,
            proposal.Namespace,
            state,
            reviewer.AuthorityId,
            reviewer.SubjectId,
            reviewer.PolicyRevision,
            reason,
            Now.AddMinutes(1),
            new string('0', 64));

    public static LearningFeedbackProposalV1 AcceptedProposal(string proposalId = "proposal-1") =>
        LearningFeedbackPolicy.AcceptProposal(
            Proposal(proposalId),
            Submitter(),
            Now,
            CancellationToken.None).Value!;

    private static LearningFeedbackAuthorityContext Authority(
        string authorityId,
        string subjectId,
        string serviceId,
        LearningFeedbackNamespaceGrant? grant) =>
        new(
            "tenant-1",
            authorityId,
            subjectId,
            serviceId,
            7,
            grant is null ? [] : [grant],
            new HashSet<string>(StringComparer.Ordinal) { "cortex-screening" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["screening-1"] = ScreeningHash
            });
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
