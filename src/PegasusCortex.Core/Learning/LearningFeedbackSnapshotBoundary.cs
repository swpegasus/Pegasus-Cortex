using System.Collections.ObjectModel;
using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Core.Learning;

/// <summary>
/// Takes bounded immutable copies at repository adapter boundaries and verifies canonical
/// hashes before application code returns provider-owned values to a caller.
/// </summary>
public static class LearningFeedbackSnapshotBoundary
{
    public static bool TryCapture(
        LearningFeedbackSnapshot? source,
        out LearningFeedbackSnapshot? captured,
        CancellationToken cancellationToken)
    {
        captured = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (source?.Proposal is null)
            return false;

        try
        {
            var proposal = CaptureProposal(source.Proposal, cancellationToken);
            if (proposal is null ||
                proposal.SchemaVersion != 1 ||
                !LearningFeedbackContractIds.IsSupportedProposal(proposal.ContractId) ||
                !string.Equals(proposal.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal) ||
                !string.Equals(proposal.ReviewState, "pending", StringComparison.Ordinal) ||
                !string.Equals(proposal.ContentTreatment, LearningFeedbackContractIds.ContentTreatment, StringComparison.Ordinal) ||
                proposal.Executable ||
                !IsHash(proposal.ProposalHash) ||
                !string.Equals(
                    proposal.ProposalHash,
                    LearningFeedbackPolicy.ComputeProposalHash(proposal),
                    StringComparison.Ordinal))
            {
                return false;
            }

            var decision = CaptureDecision(source.Decision);
            if (source.Decision is not null && decision is null)
                return false;
            if (decision is not null &&
                (decision.SchemaVersion != 1 ||
                 !string.Equals(decision.ContractId, LearningFeedbackContractIds.ReviewDecision, StringComparison.Ordinal) ||
                 !string.Equals(decision.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal) ||
                 decision.State is not ("approved" or "rejected") ||
                 !string.Equals(decision.ProposalId, proposal.ProposalId, StringComparison.Ordinal) ||
                 !string.Equals(decision.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal) ||
                 !string.Equals(decision.TenantId, proposal.TenantId, StringComparison.Ordinal) ||
                 !string.Equals(decision.Namespace, proposal.Namespace, StringComparison.Ordinal) ||
                 !IsHash(decision.DecisionHash) ||
                 !string.Equals(
                     decision.DecisionHash,
                     LearningFeedbackPolicy.ComputeDecisionHash(decision),
                     StringComparison.Ordinal)))
            {
                return false;
            }

            captured = new LearningFeedbackSnapshot(proposal, decision);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static LearningFeedbackProposalV1? CaptureProposal(
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Source is null || proposal.Screening is null || proposal.Payload is null ||
            proposal.IntendedUses is null || proposal.Evidence is null ||
            !ProposalScalarsAreBounded(proposal))
        {
            return null;
        }

        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long payloadCharacters = 0;
        foreach (var item in proposal.Payload.Take(LearningFeedbackContractLimits.MaximumPayloadFields + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            payloadCharacters += (long)(item.Key?.Length ?? 0) + (item.Value?.Length ?? 0);
            if (payload.Count == LearningFeedbackContractLimits.MaximumPayloadFields ||
                payloadCharacters > LearningFeedbackContractLimits.MaximumPayloadCharacters ||
                item.Key is null || item.Value is null ||
                item.Key.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters)
            {
                return null;
            }
            payload.Add(item.Key, item.Value);
        }
        if (payload.Count == 0)
            return null;

        var intendedUses = proposal.IntendedUses
            .Take(LearningFeedbackContractLimits.MaximumIntendedUses + 1)
            .ToArray();
        if (intendedUses.Length is 0 or > LearningFeedbackContractLimits.MaximumIntendedUses ||
            intendedUses.Any(item => !RequiredBounded(item) ||
                item is not ("distillation" or "evaluation" or "fine_tuning" or "retrieval")))
        {
            return null;
        }

        var evidence = new List<LearningFeedbackEvidenceV1>(LearningFeedbackContractLimits.MaximumEvidenceItems);
        foreach (var item in proposal.Evidence.Take(LearningFeedbackContractLimits.MaximumEvidenceItems + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence.Count == LearningFeedbackContractLimits.MaximumEvidenceItems || item is null ||
                !RequiredBounded(item.EvidenceId) || !RequiredBounded(item.MediaType) ||
                !IsHash(item.Sha256) || !string.Equals(item.Verification, "verified", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Reference) ||
                item.Reference.Length > LearningFeedbackContractLimits.MaximumEvidenceReferenceCharacters ||
                !(item.Reference.StartsWith("artifact://", StringComparison.Ordinal) ||
                  item.Reference.StartsWith("record://", StringComparison.Ordinal) ||
                  item.Reference.StartsWith("revision://", StringComparison.Ordinal) ||
                  item.Reference.StartsWith("hash://", StringComparison.Ordinal)))
            {
                return null;
            }
            evidence.Add(item with { });
        }

        string[]? keywords = null;
        if (proposal.Keywords is not null)
        {
            keywords = proposal.Keywords
                .Take(LearningFeedbackContractLimits.MaximumProposalKeywords + 1)
                .ToArray();
            if (keywords.Length > LearningFeedbackContractLimits.MaximumProposalKeywords ||
                keywords.Any(item => item is null))
            {
                return null;
            }
        }

        LearningFeedbackApplicabilityV1[]? applicability = null;
        if (proposal.Applicability is not null)
        {
            applicability = proposal.Applicability
                .Take(LearningFeedbackContractLimits.MaximumProposalApplicabilityItems + 1)
                .ToArray();
            if (applicability.Length >
                    LearningFeedbackContractLimits.MaximumProposalApplicabilityItems ||
                applicability.Any(item => item is null))
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return proposal with
        {
            Source = proposal.Source with { },
            Payload = new ReadOnlyDictionary<string, string>(payload),
            IntendedUses = Array.AsReadOnly(intendedUses),
            Evidence = Array.AsReadOnly(evidence.ToArray()),
            Keywords = keywords is null ? null : Array.AsReadOnly(keywords),
            Applicability = applicability is null
                ? null
                : Array.AsReadOnly(applicability.Select(item => item with { }).ToArray()),
            Screening = proposal.Screening with { }
        };
    }

    private static LearningFeedbackReviewDecisionV1? CaptureDecision(
        LearningFeedbackReviewDecisionV1? decision)
    {
        if (decision is null)
            return null;
        if (!RequiredBounded(decision.ContractId) || !RequiredBounded(decision.Canonicalization) ||
            !RequiredBounded(decision.DecisionId) || !RequiredBounded(decision.ProposalId) ||
            !RequiredBounded(decision.ProposalHash) || !RequiredBounded(decision.TenantId) ||
            string.IsNullOrWhiteSpace(decision.Namespace) ||
            decision.Namespace.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            !string.Equals(decision.Namespace, decision.Namespace.Trim(), StringComparison.Ordinal) ||
            !RequiredBounded(decision.State) || !RequiredBounded(decision.AuthorityId) ||
            !RequiredBounded(decision.ReviewerSubjectId) || !RequiredBounded(decision.DecisionHash) ||
            string.IsNullOrWhiteSpace(decision.Reason) ||
            decision.Reason.Length > LearningFeedbackContractLimits.MaximumReviewReasonCharacters)
        {
            return null;
        }
        return decision with { };
    }

    private static bool ProposalScalarsAreBounded(LearningFeedbackProposalV1 proposal)
    {
        var required = new[]
        {
            proposal.ContractId, proposal.Canonicalization, proposal.ProposalId, proposal.TenantId,
            proposal.FeedbackKind, proposal.ReviewState, proposal.ContentTreatment, proposal.ProposalHash,
            proposal.Source.AuthorityId, proposal.Source.SubjectId, proposal.Source.ServiceId,
            proposal.Source.OperationId, proposal.Source.CorrelationId,
            proposal.Source.RecordId, proposal.Source.RevisionId, proposal.Source.ContentHash,
            proposal.Screening.RedactionDecision, proposal.Screening.PrivacyReview,
            proposal.Screening.SecretReview, proposal.Screening.ReasoningReview,
            proposal.Screening.ProviderCredentialReview, proposal.Screening.UsageRights,
            proposal.Screening.AuthorityId, proposal.Screening.DecisionId,
            proposal.Screening.DecisionHash
        };
        var optional = new[]
        {
            proposal.Source.AgentId, proposal.Source.InstanceId,
            proposal.Source.MachineId, proposal.Source.TaskId
        };
        return required.All(RequiredBounded) &&
               optional.All(value => value is null || RequiredBounded(value)) &&
               (proposal.FeedbackKind is "correction" or "failure" or "preference" or
                   "quality" or "regression" or "safety" or "success") &&
               IsHash(proposal.Source.ContentHash) &&
               IsHash(proposal.Screening.DecisionHash) &&
               string.Equals(proposal.Screening.RedactionDecision, "sanitized", StringComparison.Ordinal) &&
               string.Equals(proposal.Screening.PrivacyReview, "passed", StringComparison.Ordinal) &&
               string.Equals(proposal.Screening.SecretReview, "passed", StringComparison.Ordinal) &&
               string.Equals(proposal.Screening.ReasoningReview, "passed", StringComparison.Ordinal) &&
               string.Equals(proposal.Screening.ProviderCredentialReview, "passed", StringComparison.Ordinal) &&
               (proposal.Screening.UsageRights is "retrieval_only" or "evaluation" or "training_and_evaluation") &&
               !string.IsNullOrWhiteSpace(proposal.Namespace) &&
               proposal.Namespace.Length <= LearningFeedbackContractLimits.MaximumNamespaceCharacters &&
               string.Equals(proposal.Namespace, proposal.Namespace.Trim(), StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(proposal.Summary) &&
               proposal.Summary.Length <= LearningFeedbackContractLimits.MaximumSummaryCharacters &&
               ValidProposalMetadata(proposal);
    }

    private static bool ValidProposalMetadata(LearningFeedbackProposalV1 proposal)
    {
        if (string.Equals(
                proposal.ContractId,
                LearningFeedbackContractIds.Proposal,
                StringComparison.Ordinal))
        {
            return proposal.Taxonomy is null &&
                proposal.Keywords is null &&
                proposal.Applicability is null;
        }
        if (!string.Equals(
            proposal.ContractId,
            LearningFeedbackContractIds.ProposalV1_1,
            StringComparison.Ordinal) ||
            proposal.Keywords is null ||
            proposal.Applicability is null ||
            proposal.Taxonomy is not null && !ValidIdentifier(proposal.Taxonomy) ||
            proposal.Keywords.Count > LearningFeedbackContractLimits.MaximumProposalKeywords ||
            proposal.Keywords.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > LearningFeedbackContractLimits.MaximumProposalKeywordCharacters ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Any(char.IsControl)) ||
            proposal.Keywords.Distinct(StringComparer.Ordinal).Count() != proposal.Keywords.Count ||
            !proposal.Keywords.SequenceEqual(
                proposal.Keywords.Order(StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            proposal.Applicability.Count >
                LearningFeedbackContractLimits.MaximumProposalApplicabilityItems ||
            proposal.Applicability.Any(item =>
                item is null ||
                !ValidIdentifier(item.ProductId) ||
                !ValidIdentifier(item.ComponentId)) ||
            proposal.Applicability
                .Select(item => (item.ProductId, item.ComponentId))
                .Distinct()
                .Count() != proposal.Applicability.Count ||
            !proposal.Applicability.SequenceEqual(
                proposal.Applicability
                    .OrderBy(item => item.ProductId, StringComparer.Ordinal)
                    .ThenBy(item => item.ComponentId, StringComparer.Ordinal)))
        {
            return false;
        }

        return LearningFeedbackContentScreening.Screen(
            string.Empty,
            string.Empty,
            string.Empty,
            collective: false,
            proposal.Taxonomy,
            proposal.Keywords,
            proposal.Applicability).Accepted;
    }

    private static bool RequiredBounded(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters;

    private static bool ValidIdentifier(string? value) =>
        RequiredBounded(value) &&
        char.IsAsciiLetterOrDigit(value![0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsHash(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
