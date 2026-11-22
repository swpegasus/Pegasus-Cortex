using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// Deterministic boundary policy for advisory audit assessments and bounded review
/// delegation. Storage must still enforce grant state, expiry, use limits, and atomicity.
/// </summary>
public static class LearningFeedbackAuditPolicy
{
    private static readonly string EmptyHash = new('0', 64);
    private static readonly HashSet<string> Recommendations =
        ["approve", "reject", "human_review"];
    private static readonly HashSet<string> AutoApprovalKinds = ["quality", "success"];

    public static CortexContractResult<LearningFeedbackAuditAssessmentV1> AcceptAssessment(
        LearningFeedbackAuditAssessmentV1 submitted,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            caller,
            proposal.Namespace,
            LearningFeedbackNamespaceCapability.Audit,
            cancellationToken);
        if (!access.Accepted)
            return Reject<LearningFeedbackAuditAssessmentV1>(access.Diagnostics);

        if (submitted.SchemaVersion != 1 ||
            !string.Equals(submitted.ContractId, LearningFeedbackContractIds.AuditAssessment, StringComparison.Ordinal) ||
            !string.Equals(submitted.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal) ||
            !ValidPrefixedHash(submitted.AssessmentId, "assessment_") ||
            !ValidPrefixedHash(submitted.ProposalId, "proposal_") ||
            !ValidIdentifier(submitted.TenantId) ||
            !ValidIdentifier(submitted.AuthorityId) ||
            !ValidIdentifier(submitted.AuditorSubjectId) ||
            submitted.ReviewGrantId is not null && !ValidPrefixedHash(submitted.ReviewGrantId, "grant_") ||
            !ValidHash(submitted.ProposalHash) ||
            !Recommendations.Contains(submitted.Recommendation) ||
            string.IsNullOrWhiteSpace(submitted.Rationale) ||
            submitted.Rationale.Length > LearningFeedbackContractLimits.MaximumAuditRationaleCharacters ||
            submitted.RetentionConfidence != 0 ||
            submitted.AssessmentHash.Length != 64 ||
            submitted.AssessmentHash.Any(character => character != '0'))
        {
            return Reject<LearningFeedbackAuditAssessmentV1>(
                Error("learning_feedback_audit_value_invalid", "The audit assessment is outside the bounded contract.", proposal.ProposalId));
        }

        if (!string.Equals(submitted.ProposalId, proposal.ProposalId, StringComparison.Ordinal) ||
            !string.Equals(submitted.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal) ||
            !string.Equals(submitted.TenantId, caller.TenantId, StringComparison.Ordinal) ||
            !string.Equals(submitted.TenantId, proposal.TenantId, StringComparison.Ordinal) ||
            !string.Equals(submitted.Namespace, proposal.Namespace, StringComparison.Ordinal) ||
            !string.Equals(submitted.AuthorityId, caller.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(submitted.AuditorSubjectId, caller.SubjectId, StringComparison.Ordinal) ||
            submitted.PolicyRevision != caller.PolicyRevision)
        {
            return Reject<LearningFeedbackAuditAssessmentV1>(
                Error("learning_feedback_audit_authority_mismatch", "The audit assessment does not match server-derived authority.", proposal.ProposalId));
        }

        if (string.Equals(caller.AuthorityId, proposal.Source.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(caller.SubjectId, proposal.Source.SubjectId, StringComparison.Ordinal))
        {
            return Reject<LearningFeedbackAuditAssessmentV1>(
                Error("learning_feedback_audit_independence_required", "The proposal submitter cannot audit the same proposal.", proposal.ProposalId));
        }

        if (submitted.CreatedAt < proposal.CreatedAt || submitted.CreatedAt > now.AddMinutes(5))
        {
            return Reject<LearningFeedbackAuditAssessmentV1>(
                Error("learning_feedback_audit_timestamp_invalid", "The audit timestamp is outside the permitted window.", proposal.ProposalId));
        }

        var normalized = submitted with
        {
            ContractId = LearningFeedbackContractIds.AuditAssessment,
            Canonicalization = LearningFeedbackContractIds.Canonicalization,
            TenantId = caller.TenantId,
            Namespace = proposal.Namespace,
            AuthorityId = caller.AuthorityId,
            AuditorSubjectId = caller.SubjectId,
            PolicyRevision = caller.PolicyRevision,
            Rationale = submitted.Rationale.Trim(),
            RetentionConfidence = string.Equals(submitted.Recommendation, "approve", StringComparison.Ordinal)
                ? 0.60
                : 0,
            CreatedAt = submitted.CreatedAt.ToUniversalTime(),
            AssessmentHash = EmptyHash
        };
        normalized = normalized with { AssessmentHash = ComputeAssessmentHash(normalized) };
        return Accepted(normalized);
    }

    public static CortexContractResult<LearningFeedbackReviewGrantV1> AcceptGrant(
        LearningFeedbackReviewGrantV1 submitted,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            caller,
            submitted.Namespace,
            LearningFeedbackNamespaceCapability.DelegateReview,
            cancellationToken);
        if (!access.Accepted)
            return Reject<LearningFeedbackReviewGrantV1>(access.Diagnostics);

        var kinds = submitted.FeedbackKinds?
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (submitted.SchemaVersion != 1 ||
            !string.Equals(submitted.ContractId, LearningFeedbackContractIds.ReviewGrant, StringComparison.Ordinal) ||
            !string.Equals(submitted.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal) ||
            !ValidPrefixedHash(submitted.GrantId, "grant_") ||
            !ValidIdentifier(submitted.TenantId) ||
            !ValidIdentifier(submitted.AuthorityId) ||
            !ValidIdentifier(submitted.IssuerSubjectId) ||
            !ValidIdentifier(submitted.DelegateAuthorityId) ||
            !ValidIdentifier(submitted.DelegateSubjectId) ||
            submitted.PolicyRevision != caller.PolicyRevision ||
            kinds.Length is < 1 or > LearningFeedbackContractLimits.MaximumGrantFeedbackKinds ||
            kinds.Any(kind => !AutoApprovalKinds.Contains(kind)) ||
            !submitted.CollectiveOnly ||
            submitted.MaximumUses is < 1 or > LearningFeedbackContractLimits.MaximumGrantUses ||
            submitted.CreatedAt > now.AddMinutes(5) ||
            submitted.ExpiresAt <= submitted.CreatedAt ||
            submitted.ExpiresAt - submitted.CreatedAt > LearningFeedbackContractLimits.MaximumGrantLifetime ||
            submitted.GrantHash.Length != 64 ||
            submitted.GrantHash.Any(character => character != '0'))
        {
            return Reject<LearningFeedbackReviewGrantV1>(
                Error("learning_feedback_review_grant_invalid", "The delegated review grant is outside the safe bounded contract.", null));
        }

        if (!string.Equals(submitted.TenantId, caller.TenantId, StringComparison.Ordinal) ||
            !string.Equals(submitted.AuthorityId, caller.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(submitted.IssuerSubjectId, caller.SubjectId, StringComparison.Ordinal) ||
            string.Equals(submitted.DelegateAuthorityId, caller.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(submitted.DelegateSubjectId, caller.SubjectId, StringComparison.Ordinal))
        {
            return Reject<LearningFeedbackReviewGrantV1>(
                Error("learning_feedback_review_grant_authority_mismatch", "The delegated review grant does not match server authority or is self-delegated.", null));
        }

        var normalized = submitted with
        {
            ContractId = LearningFeedbackContractIds.ReviewGrant,
            Canonicalization = LearningFeedbackContractIds.Canonicalization,
            TenantId = caller.TenantId,
            AuthorityId = caller.AuthorityId,
            IssuerSubjectId = caller.SubjectId,
            PolicyRevision = caller.PolicyRevision,
            FeedbackKinds = Array.AsReadOnly(kinds),
            ExpiresAt = submitted.ExpiresAt.ToUniversalTime(),
            CreatedAt = submitted.CreatedAt.ToUniversalTime(),
            GrantHash = EmptyHash
        };
        normalized = normalized with { GrantHash = ComputeGrantHash(normalized) };
        return Accepted(normalized);
    }

    public static bool IsAutoApprovalKind(string feedbackKind) =>
        AutoApprovalKinds.Contains(feedbackKind);

    public static string ComputeAssessmentHash(LearningFeedbackAuditAssessmentV1 assessment) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(assessment with { AssessmentHash = EmptyHash }));

    public static string ComputeGrantHash(LearningFeedbackReviewGrantV1 grant) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(grant with
        {
            FeedbackKinds = grant.FeedbackKinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            GrantHash = EmptyHash
        }));

    public static string ComputeRevocationHash(LearningFeedbackReviewGrantRevocationV1 revocation) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(revocation with { RevocationHash = EmptyHash }));

    public static string ComputeUseHash(LearningFeedbackReviewGrantUseV1 use) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(use with { UseHash = EmptyHash }));

    private static CortexContractResult<T> Accepted<T>(T value)
        where T : class =>
        new(true, value, null, []);

    private static CortexContractResult<T> Reject<T>(PegasusDiagnostic diagnostic)
        where T : class =>
        new(false, null, null, [diagnostic]);

    private static CortexContractResult<T> Reject<T>(IReadOnlyList<PegasusDiagnostic> diagnostics)
        where T : class =>
        new(false, null, null, diagnostics);

    private static PegasusDiagnostic Error(string code, string message, string? proposalId) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.learning-feedback.audit",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["proposalId"] = ValidIdentifier(proposalId) ? proposalId : null
            });

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        ValidHash(value[prefix.Length..]);
}
