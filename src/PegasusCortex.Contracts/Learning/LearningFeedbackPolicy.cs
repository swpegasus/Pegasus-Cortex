using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// Pure boundary policy for accepting feedback proposals and review decisions.
/// Storage, publication, export, training, and transport remain caller-owned concerns.
/// </summary>
public static partial class LearningFeedbackPolicy
{
    private static readonly string EmptyHash = new('0', 64);
    private static readonly HashSet<string> FeedbackKinds =
        ["correction", "failure", "preference", "quality", "regression", "safety", "success"];
    private static readonly HashSet<string> IntendedUses =
        ["distillation", "evaluation", "fine_tuning", "retrieval"];
    private static readonly HashSet<string> DecisionStates = ["approved", "rejected"];
    private static readonly HashSet<string> ProhibitedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordhash", "sessioncookie", "accesstoken", "refreshtoken",
        "privatekey", "secret", "apikey", "clientsecret", "credential", "authorization", "cookie",
        "rawterminal", "terminaldump", "rawchat", "chatdump", "rawtranscript", "transcriptdump",
        "chainofthought", "privatereasoning", "hiddenreasoning", "toolarguments"
    };
    private static readonly string[] ProhibitedTextMarkers =
    [
        "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", "Bearer ", "password=",
        "access_token=", "<chain_of_thought>", "private reasoning:", "hidden reasoning:",
        "raw transcript:", "terminal://", "thread://", "chat://"
    ];

    public static CortexContractResult<LearningFeedbackProposalV1> AcceptProposal(
        LearningFeedbackProposalV1 submitted,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var capture = CaptureProposal(submitted, cancellationToken);
        if (capture.Diagnostic is not null)
            return Reject<LearningFeedbackProposalV1>(capture.Diagnostic, null);
        var authorityCapture = CaptureAuthority(caller, submitted.ProposalId, cancellationToken);
        if (authorityCapture.Diagnostic is not null)
            return Reject<LearningFeedbackProposalV1>(authorityCapture.Diagnostic, null);

        var proposal = capture.Value!;
        var authority = authorityCapture.Value!;
        var hashingBoundary = ValidateProposalHashingBounds(proposal);
        if (hashingBoundary is not null)
            return Reject<LearningFeedbackProposalV1>(hashingBoundary, null);

        var submittedBytes = CortexCanonicalJson.Serialize(proposal);
        if (submittedBytes.Length > LearningFeedbackContractLimits.MaximumSerializedProposalBytes)
            return Reject<LearningFeedbackProposalV1>(
                Error("learning_feedback_serialized_size_invalid", "The proposal exceeds its serialized byte limit.", proposal.ProposalId),
                null);
        var submittedHash = CortexCanonicalJson.Sha256(submittedBytes);

        var validation = ValidateProposal(proposal, authority, now, cancellationToken);
        if (validation is not null)
            return Reject<LearningFeedbackProposalV1>(validation, submittedHash);

        var normalized = proposal with
        {
            ContractId = proposal.ContractId,
            Canonicalization = LearningFeedbackContractIds.Canonicalization,
            TenantId = authority.TenantId,
            Namespace = proposal.Namespace.Trim(),
            Source = proposal.Source with
            {
                AuthorityId = authority.AuthorityId,
                SubjectId = authority.SubjectId,
                ServiceId = authority.ServiceId,
                OccurredAt = proposal.Source.OccurredAt.ToUniversalTime()
            },
            Payload = CanonicalDictionary(proposal.Payload),
            IntendedUses = Array.AsReadOnly(
                proposal.IntendedUses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Evidence = Array.AsReadOnly(
                proposal.Evidence.OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray()),
            Taxonomy = IsProposalV1_1(proposal) ? proposal.Taxonomy : null,
            Keywords = IsProposalV1_1(proposal)
                ? Array.AsReadOnly(
                    (proposal.Keywords ?? [])
                        .Order(StringComparer.Ordinal)
                        .ToArray())
                : null,
            Applicability = IsProposalV1_1(proposal)
                ? Array.AsReadOnly(
                    (proposal.Applicability ?? [])
                        .OrderBy(item => item.ProductId, StringComparer.Ordinal)
                        .ThenBy(item => item.ComponentId, StringComparer.Ordinal)
                        .Select(item => item with { })
                        .ToArray())
                : null,
            Screening = proposal.Screening with { DecidedAt = proposal.Screening.DecidedAt.ToUniversalTime() },
            ReviewState = "pending",
            ContentTreatment = LearningFeedbackContractIds.ContentTreatment,
            Executable = false,
            CreatedAt = proposal.CreatedAt.ToUniversalTime(),
            ProposalHash = EmptyHash
        };
        normalized = normalized with { ProposalHash = ComputeProposalHash(normalized) };
        return new CortexContractResult<LearningFeedbackProposalV1>(true, normalized, submittedHash, []);
    }

    public static CortexContractResult<LearningFeedbackReviewDecisionV1> AcceptReviewDecision(
        LearningFeedbackReviewDecisionV1 submitted,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var proposalCapture = CaptureProposal(proposal, cancellationToken);
        if (proposalCapture.Diagnostic is not null)
            return Reject<LearningFeedbackReviewDecisionV1>(proposalCapture.Diagnostic, null);
        var authorityCapture = CaptureAuthority(caller, proposal.ProposalId, cancellationToken);
        if (authorityCapture.Diagnostic is not null)
            return Reject<LearningFeedbackReviewDecisionV1>(authorityCapture.Diagnostic, null);

        var proposalSnapshot = proposalCapture.Value!;
        var authority = authorityCapture.Value!;
        var proposalHashingBoundary = ValidateProposalHashingBounds(proposalSnapshot);
        if (proposalHashingBoundary is not null)
            return Reject<LearningFeedbackReviewDecisionV1>(proposalHashingBoundary, null);
        var decisionHashingBoundary = ValidateDecisionHashingBounds(submitted, proposalSnapshot.ProposalId);
        if (decisionHashingBoundary is not null)
            return Reject<LearningFeedbackReviewDecisionV1>(decisionHashingBoundary, null);

        var submittedBytes = CortexCanonicalJson.Serialize(submitted);
        if (submittedBytes.Length > LearningFeedbackContractLimits.MaximumSerializedProposalBytes)
            return Reject<LearningFeedbackReviewDecisionV1>(
                Error("learning_feedback_review_serialized_size_invalid", "The review decision exceeds its serialized byte limit.", proposal.ProposalId, submitted.DecisionId),
                null);
        var submittedHash = CortexCanonicalJson.Sha256(submittedBytes);

        var diagnostic = ValidateDecision(submitted, proposalSnapshot, authority, now);
        if (diagnostic is not null)
            return Reject<LearningFeedbackReviewDecisionV1>(diagnostic, submittedHash);

        var normalized = submitted with
        {
            ContractId = LearningFeedbackContractIds.ReviewDecision,
            Canonicalization = LearningFeedbackContractIds.Canonicalization,
            TenantId = authority.TenantId,
            Namespace = proposalSnapshot.Namespace,
            AuthorityId = authority.AuthorityId,
            ReviewerSubjectId = authority.SubjectId,
            PolicyRevision = authority.PolicyRevision,
            Reason = NormalizeText(submitted.Reason),
            CreatedAt = submitted.CreatedAt.ToUniversalTime(),
            DecisionHash = EmptyHash
        };
        normalized = normalized with { DecisionHash = ComputeDecisionHash(normalized) };
        return new CortexContractResult<LearningFeedbackReviewDecisionV1>(true, normalized, submittedHash, []);
    }

    /// <summary>
    /// Captures server-owned caller authority and authorizes exactly one namespace capability.
    /// Serialized feedback records are deliberately not inputs to this decision.
    /// </summary>
    public static CortexContractResult<LearningFeedbackNamespaceAccess> AuthorizeNamespace(
        LearningFeedbackAuthorityContext caller,
        string namespaceName,
        LearningFeedbackNamespaceCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(namespaceName) ||
            namespaceName.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            !string.Equals(namespaceName, namespaceName.Trim(), StringComparison.Ordinal))
        {
            return Reject<LearningFeedbackNamespaceAccess>(
                AccessError(
                    "learning_feedback_namespace_invalid",
                    "The requested namespace is missing, oversized, or not canonical.",
                    namespaceName,
                    capability),
                null);
        }

        var authorityCapture = CaptureAuthority(caller, null, cancellationToken);
        if (authorityCapture.Diagnostic is not null)
            return Reject<LearningFeedbackNamespaceAccess>(authorityCapture.Diagnostic, null);

        var authority = authorityCapture.Value!;
        if (!Enum.IsDefined(capability))
        {
            return Reject<LearningFeedbackNamespaceAccess>(
                AccessError(
                    "learning_feedback_capability_invalid",
                    "The requested namespace capability is unsupported.",
                    namespaceName,
                    capability),
                null);
        }

        if (!HasSingleNamespaceGrant(authority, namespaceName, capability))
        {
            return Reject<LearningFeedbackNamespaceAccess>(
                AccessError(
                    "learning_feedback_namespace_forbidden",
                    "Server-derived policy does not grant the requested namespace capability.",
                    namespaceName,
                    capability),
                null);
        }

        return new CortexContractResult<LearningFeedbackNamespaceAccess>(
            true,
            new LearningFeedbackNamespaceAccess(
                authority.TenantId,
                namespaceName,
                authority.AuthorityId,
                authority.SubjectId,
                authority.ServiceId,
                authority.PolicyRevision,
                capability),
            null,
            []);
    }

    public static string ComputeProposalHash(LearningFeedbackProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var canonical = proposal with
        {
            Payload = CanonicalDictionary(proposal.Payload),
            IntendedUses = proposal.IntendedUses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Evidence = proposal.Evidence.OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray(),
            Taxonomy = IsProposalV1_1(proposal) ? proposal.Taxonomy : null,
            Keywords = IsProposalV1_1(proposal)
                ? (proposal.Keywords ?? []).Order(StringComparer.Ordinal).ToArray()
                : null,
            Applicability = IsProposalV1_1(proposal)
                ? (proposal.Applicability ?? [])
                    .OrderBy(item => item.ProductId, StringComparer.Ordinal)
                    .ThenBy(item => item.ComponentId, StringComparer.Ordinal)
                    .ToArray()
                : null,
            ProposalHash = EmptyHash
        };
        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(canonical));
    }

    public static string ComputeDecisionHash(LearningFeedbackReviewDecisionV1 decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(decision with { DecisionHash = EmptyHash }));
    }

    private static PegasusDiagnostic? ValidateProposal(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var source = proposal.Source;
        var screening = proposal.Screening;
        if (source is null || screening is null)
            return Error("learning_feedback_shape_invalid", "The proposal contains a missing source or screening object.", proposal.ProposalId);
        if (proposal.SchemaVersion != 1 ||
            !LearningFeedbackContractIds.IsSupportedProposal(proposal.ContractId))
        {
            return Error(
                "learning_feedback_schema_unsupported",
                "Only the learning-feedback proposal 1.0 and 1.1 contracts are supported.",
                proposal.ProposalId);
        }
        if (!string.Equals(proposal.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal))
            return Error("learning_feedback_canonicalization_invalid", "The proposal canonicalization identifier is unsupported.", proposal.ProposalId);
        if (Missing(proposal.ContractId, proposal.Canonicalization, proposal.ProposalId, proposal.TenantId,
                    proposal.Namespace, proposal.FeedbackKind, proposal.Summary, proposal.ProposalHash,
                    source.AuthorityId, source.SubjectId, source.ServiceId,
                    source.OperationId, source.CorrelationId, source.RecordId,
                    source.RevisionId, source.ContentHash, screening.AuthorityId,
                    screening.DecisionId, screening.DecisionHash))
            return Error("learning_feedback_required_value_missing", "The proposal is missing a required value.", proposal.ProposalId);
        if (!ValidIdentifier(proposal.ProposalId) ||
            proposal.Namespace.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            !string.Equals(proposal.Namespace, proposal.Namespace.Trim(), StringComparison.Ordinal) ||
            !FeedbackKinds.Contains(proposal.FeedbackKind))
            return Error("learning_feedback_identity_invalid", "The proposal identifier, namespace, or feedback kind is invalid.", proposal.ProposalId);
        if (!ValidIdentifier(proposal.TenantId) || !ValidIdentifier(source.AuthorityId) ||
            !ValidIdentifier(source.SubjectId) || !ValidIdentifier(source.ServiceId) ||
            !ValidOptionalIdentifier(source.AgentId) || !ValidOptionalIdentifier(source.InstanceId) ||
            !ValidOptionalIdentifier(source.MachineId) || !ValidIdentifier(source.OperationId) ||
            !ValidOptionalIdentifier(source.TaskId) || !ValidIdentifier(source.CorrelationId) ||
            !ValidIdentifier(source.RecordId) || !ValidIdentifier(source.RevisionId) ||
            !ValidIdentifier(screening.AuthorityId) || !ValidIdentifier(screening.DecisionId))
            return Error("learning_feedback_source_identity_invalid", "A source or screening identity is outside the bounded identifier contract.", proposal.ProposalId);
        if (!string.Equals(proposal.TenantId, caller.TenantId, StringComparison.Ordinal) ||
            !string.Equals(proposal.Source.AuthorityId, caller.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(proposal.Source.SubjectId, caller.SubjectId, StringComparison.Ordinal) ||
            !string.Equals(proposal.Source.ServiceId, caller.ServiceId, StringComparison.Ordinal))
            return Error("learning_feedback_authority_mismatch", "Tenant and source identity must match server-derived caller authority.", proposal.ProposalId);

        var grants = caller.NamespaceGrants.Take(LearningFeedbackContractLimits.MaximumAuthorityEntries + 1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (grants.Length > LearningFeedbackContractLimits.MaximumAuthorityEntries)
            return Error("learning_feedback_authority_too_large", "The caller authority exceeds the bounded grant count.", proposal.ProposalId);
        if (!HasSingleNamespaceGrant(caller, proposal.Namespace, LearningFeedbackNamespaceCapability.Submit))
            return Error("learning_feedback_namespace_forbidden", "Server-derived policy does not grant this namespace submission.", proposal.ProposalId);
        if (!string.Equals(proposal.ReviewState, "pending", StringComparison.Ordinal))
            return Error("learning_feedback_review_state_invalid", "New proposals must begin pending independent review.", proposal.ProposalId);
        if (proposal.Executable || !string.Equals(proposal.ContentTreatment, LearningFeedbackContractIds.ContentTreatment, StringComparison.Ordinal))
            return Error("learning_feedback_content_treatment_invalid", "Feedback must be non-executable untrusted data.", proposal.ProposalId);
        if (proposal.ProposalHash.Length != 64 || proposal.ProposalHash.Any(character => character != '0'))
            return Error("learning_feedback_hash_preclaimed", "A submission must use the all-zero proposal-hash placeholder.", proposal.ProposalId);

        if (proposal.Summary.Length > LearningFeedbackContractLimits.MaximumSummaryCharacters || ContainsProhibitedText(proposal.Summary))
            return Error("learning_feedback_summary_invalid", "The summary is too large or contains prohibited content.", proposal.ProposalId);
        if (proposal.Payload.Count is < 1 or > LearningFeedbackContractLimits.MaximumPayloadFields ||
            proposal.Payload.Sum(item => (long)item.Key.Length + item.Value.Length) > LearningFeedbackContractLimits.MaximumPayloadCharacters ||
            proposal.Payload.Any(item => InvalidPayload(item.Key, item.Value)))
            return Error("learning_feedback_payload_invalid", "The payload is too large or contains credentials, raw streams, or private reasoning.", proposal.ProposalId);
        if (proposal.IntendedUses.Count is < 1 or > LearningFeedbackContractLimits.MaximumIntendedUses ||
            proposal.IntendedUses.Distinct(StringComparer.Ordinal).Count() != proposal.IntendedUses.Count ||
            proposal.IntendedUses.Any(use => !IntendedUses.Contains(use)))
            return Error("learning_feedback_intended_use_invalid", "Intended uses must be a unique subset of retrieval, evaluation, fine_tuning, and distillation.", proposal.ProposalId);
        var metadataDiagnostic = ValidateProposalMetadata(proposal);
        if (metadataDiagnostic is not null)
            return metadataDiagnostic;
        if ((proposal.IntendedUses.Contains("fine_tuning", StringComparer.Ordinal) || proposal.IntendedUses.Contains("distillation", StringComparer.Ordinal)) &&
            !string.Equals(proposal.Screening.UsageRights, "training_and_evaluation", StringComparison.Ordinal))
            return Error("learning_feedback_usage_rights_insufficient", "Fine-tuning and distillation require explicit training-and-evaluation usage rights.", proposal.ProposalId);

        if (!ValidScreening(proposal.Screening, caller))
            return Error("learning_feedback_screening_untrusted", "The proposal lacks a current trusted privacy, secret, reasoning, credential, redaction, and rights screening decision.", proposal.ProposalId);
        if (proposal.Evidence.Count > LearningFeedbackContractLimits.MaximumEvidenceItems || proposal.Evidence.Any(InvalidEvidence))
            return Error("learning_feedback_evidence_invalid", "Evidence is incomplete, unverified, too large, or references a prohibited live source.", proposal.ProposalId);
        if (proposal.Source.OccurredAt > now.AddMinutes(5) || proposal.Screening.DecidedAt > now.AddMinutes(5) || proposal.CreatedAt > now.AddMinutes(5))
            return Error("learning_feedback_timestamp_invalid", "Proposal timestamps may not be materially in the future.", proposal.ProposalId);
        if (!IsLowerHexHash(proposal.Source.ContentHash))
            return Error("learning_feedback_source_hash_invalid", "The source content hash is invalid.", proposal.ProposalId);
        return null;
    }

    private static PegasusDiagnostic? ValidateProposalMetadata(
        LearningFeedbackProposalV1 proposal)
    {
        if (!IsProposalV1_1(proposal))
        {
            if (proposal.Taxonomy is not null ||
                proposal.Keywords is not null ||
                proposal.Applicability is not null)
            {
                return Error(
                    "learning_feedback_metadata_contract_invalid",
                    "Proposal taxonomy, keywords, and applicability require the 1.1 contract.",
                    proposal.ProposalId);
            }
            return null;
        }

        var keywords = proposal.Keywords ?? [];
        var applicability = proposal.Applicability ?? [];
        if (proposal.Taxonomy is not null && !ValidIdentifier(proposal.Taxonomy) ||
            keywords.Count > LearningFeedbackContractLimits.MaximumProposalKeywords ||
            keywords.Distinct(StringComparer.Ordinal).Count() != keywords.Count ||
            keywords.Any(InvalidKeyword) ||
            applicability.Count >
                LearningFeedbackContractLimits.MaximumProposalApplicabilityItems ||
            applicability.Any(item =>
                item is null ||
                !ValidIdentifier(item.ProductId) ||
                !ValidIdentifier(item.ComponentId)) ||
            applicability
                .Select(item => (item.ProductId, item.ComponentId))
                .Distinct()
                .Count() != applicability.Count)
        {
            return Error(
                "learning_feedback_metadata_invalid",
                "Proposal metadata is malformed, duplicated, or outside its bounded contract.",
                proposal.ProposalId);
        }

        var screening = LearningFeedbackContentScreening.Screen(
            string.Empty,
            string.Empty,
            string.Empty,
            collective: false,
            proposal.Taxonomy,
            keywords,
            applicability);
        return screening.Accepted
            ? null
            : Error(
                "learning_feedback_metadata_prohibited",
                "Proposal metadata contains prohibited secret, raw-stream, or private-reasoning content.",
                proposal.ProposalId);
    }

    private static PegasusDiagnostic? ValidateProposalHashingBounds(LearningFeedbackProposalV1 proposal)
    {
        var identifiers = new[]
        {
            proposal.ContractId, proposal.Canonicalization, proposal.ProposalId, proposal.TenantId,
            proposal.FeedbackKind, proposal.ReviewState, proposal.ContentTreatment, proposal.ProposalHash,
            proposal.Source.AuthorityId, proposal.Source.SubjectId, proposal.Source.ServiceId,
            proposal.Source.AgentId, proposal.Source.InstanceId, proposal.Source.MachineId,
            proposal.Source.OperationId, proposal.Source.TaskId, proposal.Source.CorrelationId,
            proposal.Source.RecordId, proposal.Source.RevisionId, proposal.Source.ContentHash,
            proposal.Screening.RedactionDecision, proposal.Screening.PrivacyReview, proposal.Screening.SecretReview,
            proposal.Screening.ReasoningReview, proposal.Screening.ProviderCredentialReview,
            proposal.Screening.UsageRights, proposal.Screening.AuthorityId, proposal.Screening.DecisionId,
            proposal.Screening.DecisionHash
        };
        if (identifiers.Any(value => value?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            proposal.Namespace?.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            proposal.Summary?.Length > LearningFeedbackContractLimits.MaximumSummaryCharacters ||
            proposal.Taxonomy?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
            proposal.Payload.Any(item => item.Key?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
                                         item.Value?.Length > LearningFeedbackContractLimits.MaximumPayloadCharacters) ||
            proposal.IntendedUses.Any(value => value?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            proposal.Keywords?.Any(value =>
                value?.Length > LearningFeedbackContractLimits.MaximumProposalKeywordCharacters) == true ||
            proposal.Applicability?.Any(item =>
                item?.ProductId?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
                item?.ComponentId?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters) == true ||
            proposal.Evidence.Any(item => item.EvidenceId?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
                                          item.Reference?.Length > LearningFeedbackContractLimits.MaximumEvidenceReferenceCharacters ||
                                          item.MediaType?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
                                          item.Sha256?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
                                          item.Verification?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters))
        {
            return Error(
                "learning_feedback_hashing_boundary_exceeded",
                "A proposal value exceeds the pre-serialization hashing boundary.",
                proposal.ProposalId);
        }
        return null;
    }

    private static PegasusDiagnostic? ValidateDecisionHashingBounds(
        LearningFeedbackReviewDecisionV1 decision,
        string? proposalId)
    {
        var identifiers = new[]
        {
            decision.ContractId, decision.Canonicalization, decision.DecisionId, decision.ProposalId,
            decision.ProposalHash, decision.TenantId, decision.State, decision.AuthorityId,
            decision.ReviewerSubjectId, decision.DecisionHash
        };
        if (identifiers.Any(value => value?.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            decision.Namespace?.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            decision.Reason?.Length > LearningFeedbackContractLimits.MaximumReviewReasonCharacters)
        {
            return Error(
                "learning_feedback_review_hashing_boundary_exceeded",
                "A review decision value exceeds the pre-serialization hashing boundary.",
                proposalId,
                decision.DecisionId);
        }
        return null;
    }

    private static PegasusDiagnostic? ValidateDecision(
        LearningFeedbackReviewDecisionV1 decision,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuthorityContext caller,
        DateTimeOffset now)
    {
        if (decision.SchemaVersion != 1 || !string.Equals(decision.ContractId, LearningFeedbackContractIds.ReviewDecision, StringComparison.Ordinal) ||
            !string.Equals(decision.Canonicalization, LearningFeedbackContractIds.Canonicalization, StringComparison.Ordinal))
            return Error("learning_feedback_review_schema_unsupported", "Only the learning-feedback review-decision 1.0 contract is supported.", proposal.ProposalId, decision.DecisionId);
        if (Missing(decision.ContractId, decision.Canonicalization, decision.DecisionId, decision.ProposalId,
                    decision.ProposalHash, decision.TenantId, decision.Namespace, decision.State,
                    decision.AuthorityId, decision.ReviewerSubjectId, decision.Reason, decision.DecisionHash))
            return Error("learning_feedback_review_required_value_missing", "The review decision is missing a required value.", proposal.ProposalId, decision.DecisionId);
        if (!ValidIdentifier(decision.DecisionId) || !ValidIdentifier(decision.ProposalId) ||
            !ValidIdentifier(decision.TenantId) || !ValidIdentifier(decision.AuthorityId) ||
            !ValidIdentifier(decision.ReviewerSubjectId) || decision.Namespace.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
            !DecisionStates.Contains(decision.State) ||
            decision.Reason.Length > LearningFeedbackContractLimits.MaximumReviewReasonCharacters || ContainsProhibitedText(decision.Reason))
            return Error("learning_feedback_review_value_invalid", "The decision identifier, state, or sanitized reason is invalid.", proposal.ProposalId, decision.DecisionId);
        if (!string.Equals(proposal.ReviewState, "pending", StringComparison.Ordinal) ||
            !IsLowerHexHash(proposal.ProposalHash) || !string.Equals(proposal.ProposalHash, ComputeProposalHash(proposal), StringComparison.Ordinal))
            return Error("learning_feedback_proposal_integrity_invalid", "The reviewed proposal is not a current, intact pending proposal.", proposal.ProposalId, decision.DecisionId);
        if (string.Equals(caller.AuthorityId, proposal.Source.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(caller.SubjectId, proposal.Source.SubjectId, StringComparison.Ordinal))
            return Error("learning_feedback_review_independence_required", "The proposal submitter cannot approve or reject the same proposal.", proposal.ProposalId, decision.DecisionId);
        if (!string.Equals(decision.ProposalId, proposal.ProposalId, StringComparison.Ordinal) ||
            !string.Equals(decision.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal) ||
            !string.Equals(decision.TenantId, caller.TenantId, StringComparison.Ordinal) ||
            !string.Equals(decision.TenantId, proposal.TenantId, StringComparison.Ordinal) ||
            !string.Equals(decision.Namespace, proposal.Namespace, StringComparison.Ordinal) ||
            !string.Equals(decision.AuthorityId, caller.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(decision.ReviewerSubjectId, caller.SubjectId, StringComparison.Ordinal) ||
            decision.PolicyRevision != caller.PolicyRevision)
            return Error("learning_feedback_review_authority_mismatch", "The review decision does not match the proposal and server-derived reviewer authority.", proposal.ProposalId, decision.DecisionId);
        if (!HasSingleNamespaceGrant(caller, proposal.Namespace, LearningFeedbackNamespaceCapability.Review))
            return Error("learning_feedback_review_forbidden", "Server-derived policy does not grant review authority for this namespace.", proposal.ProposalId, decision.DecisionId);
        if (decision.CreatedAt < proposal.CreatedAt || decision.CreatedAt > now.AddMinutes(5))
            return Error("learning_feedback_review_timestamp_invalid", "The review decision timestamp is outside the permitted window.", proposal.ProposalId, decision.DecisionId);
        if (decision.DecisionHash.Length != 64 || decision.DecisionHash.Any(character => character != '0'))
            return Error("learning_feedback_review_hash_preclaimed", "A review submission must use the all-zero decision-hash placeholder.", proposal.ProposalId, decision.DecisionId);
        return null;
    }

    private static (LearningFeedbackProposalV1? Value, PegasusDiagnostic? Diagnostic) CaptureProposal(
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Source is null || proposal.Screening is null || proposal.Payload is null ||
            proposal.IntendedUses is null || proposal.Evidence is null)
        {
            return (null, Error(
                "learning_feedback_shape_invalid",
                "The proposal contains a missing object or collection.",
                proposal.ProposalId));
        }

        try
        {
            var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);
            long payloadCharacters = 0;
            foreach (var pair in proposal.Payload.Take(LearningFeedbackContractLimits.MaximumPayloadFields + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                payloadCharacters += (long)(pair.Key?.Length ?? 0) + (pair.Value?.Length ?? 0);
                if (payload.Count == LearningFeedbackContractLimits.MaximumPayloadFields ||
                    payloadCharacters > LearningFeedbackContractLimits.MaximumPayloadCharacters || pair.Key is null || pair.Value is null)
                    return (null, Error("learning_feedback_snapshot_limit", "The caller-owned payload exceeds its bounded snapshot limits.", proposal.ProposalId));
                payload.Add(pair.Key, pair.Value);
            }

            var intendedUses = proposal.IntendedUses.Take(LearningFeedbackContractLimits.MaximumIntendedUses + 1).ToArray();
            if (intendedUses.Any(item => item is null))
                return (null, Error("learning_feedback_shape_invalid", "The proposal contains a null intended-use value.", proposal.ProposalId));
            var evidence = new List<LearningFeedbackEvidenceV1>(LearningFeedbackContractLimits.MaximumEvidenceItems);
            foreach (var item in proposal.Evidence.Take(LearningFeedbackContractLimits.MaximumEvidenceItems + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null)
                    return (null, Error("learning_feedback_shape_invalid", "The proposal contains a null evidence item.", proposal.ProposalId));
                if (evidence.Count == LearningFeedbackContractLimits.MaximumEvidenceItems)
                    return (null, Error("learning_feedback_snapshot_limit", "A caller-owned collection exceeds its bounded snapshot count.", proposal.ProposalId));
                evidence.Add(item with { });
            }
            string[]? keywords = null;
            if (proposal.Keywords is not null)
            {
                keywords = proposal.Keywords
                    .Take(LearningFeedbackContractLimits.MaximumProposalKeywords + 1)
                    .ToArray();
                if (keywords.Any(item => item is null))
                {
                    return (null, Error(
                        "learning_feedback_shape_invalid",
                        "The proposal contains a null keyword value.",
                        proposal.ProposalId));
                }
            }

            List<LearningFeedbackApplicabilityV1>? applicability = null;
            if (proposal.Applicability is not null)
            {
                applicability = new List<LearningFeedbackApplicabilityV1>(
                    LearningFeedbackContractLimits.MaximumProposalApplicabilityItems);
                foreach (var item in proposal.Applicability.Take(
                             LearningFeedbackContractLimits.MaximumProposalApplicabilityItems + 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is null)
                    {
                        return (null, Error(
                            "learning_feedback_shape_invalid",
                            "The proposal contains a null applicability value.",
                            proposal.ProposalId));
                    }
                    if (applicability.Count ==
                        LearningFeedbackContractLimits.MaximumProposalApplicabilityItems)
                    {
                        return (null, Error(
                            "learning_feedback_snapshot_limit",
                            "A caller-owned collection exceeds its bounded snapshot count.",
                            proposal.ProposalId));
                    }
                    applicability.Add(item with { });
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (intendedUses.Length > LearningFeedbackContractLimits.MaximumIntendedUses ||
                keywords?.Length > LearningFeedbackContractLimits.MaximumProposalKeywords)
                return (null, Error("learning_feedback_snapshot_limit", "A caller-owned collection exceeds its bounded snapshot count.", proposal.ProposalId));

            return (proposal with
            {
                Source = proposal.Source with { },
                Payload = new ReadOnlyDictionary<string, string>(payload),
                IntendedUses = Array.AsReadOnly(intendedUses),
                Evidence = Array.AsReadOnly(evidence.ToArray()),
                Keywords = keywords is null ? null : Array.AsReadOnly(keywords),
                Applicability = applicability is null
                    ? null
                    : Array.AsReadOnly(applicability.ToArray()),
                Screening = proposal.Screening with { }
            }, null);
        }
        catch (ArgumentException exception)
        {
            return (null, Error("learning_feedback_snapshot_invalid", "The caller-owned proposal contains an invalid or duplicate collection value.", proposal.ProposalId, cause: exception.GetType().Name));
        }
    }

    private static (LearningFeedbackAuthorityContext? Value, PegasusDiagnostic? Diagnostic) CaptureAuthority(
        LearningFeedbackAuthorityContext caller,
        string? proposalId,
        CancellationToken cancellationToken)
    {
        if (caller.NamespaceGrants is null || caller.TrustedScreeningAuthorities is null ||
            caller.ApprovedScreeningDecisionHashes is null)
            return (null, Error("learning_feedback_authority_shape_invalid", "Server-derived authority contains a missing collection.", proposalId));

        var grants = caller.NamespaceGrants.Take(LearningFeedbackContractLimits.MaximumAuthorityEntries + 1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var trusted = caller.TrustedScreeningAuthorities.Take(LearningFeedbackContractLimits.MaximumAuthorityEntries + 1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var decisions = caller.ApprovedScreeningDecisionHashes.Take(LearningFeedbackContractLimits.MaximumAuthorityEntries + 1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidIdentifier(caller.TenantId) || !ValidIdentifier(caller.AuthorityId) ||
            !ValidIdentifier(caller.SubjectId) || !ValidIdentifier(caller.ServiceId) || caller.PolicyRevision < 1)
            return (null, Error("learning_feedback_authority_invalid", "Server-derived authority contains an invalid identity or policy revision.", proposalId));
        if (grants.Length > LearningFeedbackContractLimits.MaximumAuthorityEntries ||
            trusted.Length > LearningFeedbackContractLimits.MaximumAuthorityEntries ||
            decisions.Length > LearningFeedbackContractLimits.MaximumAuthorityEntries ||
            grants.Any(item => item is null) || trusted.Any(item => item is null) ||
            decisions.Any(item => item.Key is null || item.Value is null) ||
            grants.Any(item => string.IsNullOrWhiteSpace(item.Namespace) ||
                               item.Namespace.Length > LearningFeedbackContractLimits.MaximumNamespaceCharacters ||
                               !string.Equals(item.Namespace, item.Namespace.Trim(), StringComparison.Ordinal)) ||
            trusted.Any(item => !ValidIdentifier(item)) ||
            decisions.Any(item => !ValidIdentifier(item.Key) || !IsLowerHexHash(item.Value)))
            return (null, Error("learning_feedback_authority_too_large", "Server-derived authority is malformed or exceeds a bounded collection limit.", proposalId));

        try
        {
            return (caller with
            {
                NamespaceGrants = Array.AsReadOnly(grants.Select(item => item with { }).ToArray()),
                TrustedScreeningAuthorities = new HashSet<string>(trusted, StringComparer.Ordinal),
                ApprovedScreeningDecisionHashes = new ReadOnlyDictionary<string, string>(
                    new SortedDictionary<string, string>(
                        decisions.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                        StringComparer.Ordinal))
            }, null);
        }
        catch (ArgumentException exception)
        {
            return (null, Error("learning_feedback_authority_shape_invalid", "Server-derived authority contains duplicate or invalid entries.", proposalId, cause: exception.GetType().Name));
        }
    }

    private static bool ValidScreening(LearningFeedbackScreeningV1 screening, LearningFeedbackAuthorityContext caller) =>
        string.Equals(screening.RedactionDecision, "sanitized", StringComparison.Ordinal) &&
        string.Equals(screening.PrivacyReview, "passed", StringComparison.Ordinal) &&
        string.Equals(screening.SecretReview, "passed", StringComparison.Ordinal) &&
        string.Equals(screening.ReasoningReview, "passed", StringComparison.Ordinal) &&
        string.Equals(screening.ProviderCredentialReview, "passed", StringComparison.Ordinal) &&
        screening.UsageRights is "retrieval_only" or "evaluation" or "training_and_evaluation" &&
        screening.PolicyRevision == caller.PolicyRevision &&
        caller.TrustedScreeningAuthorities.Contains(screening.AuthorityId) &&
        caller.ApprovedScreeningDecisionHashes.TryGetValue(screening.DecisionId, out var hash) &&
        string.Equals(hash, screening.DecisionHash, StringComparison.Ordinal) && IsLowerHexHash(screening.DecisionHash);

    private static bool HasSingleNamespaceGrant(
        LearningFeedbackAuthorityContext caller,
        string namespaceName,
        LearningFeedbackNamespaceCapability capability) =>
        caller.NamespaceGrants.Count(grant =>
            string.Equals(grant.Namespace, namespaceName, StringComparison.Ordinal) &&
            capability switch
            {
                LearningFeedbackNamespaceCapability.Read => grant.CanRead,
                LearningFeedbackNamespaceCapability.Submit => grant.CanSubmit,
                LearningFeedbackNamespaceCapability.Review => grant.CanReview,
                LearningFeedbackNamespaceCapability.Audit => grant.CanAudit,
                LearningFeedbackNamespaceCapability.DelegateReview => grant.CanDelegateReview,
                _ => false
            }) == 1;

    private static bool InvalidEvidence(LearningFeedbackEvidenceV1 evidence) =>
        evidence is null || Missing(evidence.EvidenceId, evidence.Reference, evidence.MediaType, evidence.Sha256, evidence.Verification) ||
        !ValidIdentifier(evidence.EvidenceId) || evidence.Reference.Length > LearningFeedbackContractLimits.MaximumEvidenceReferenceCharacters ||
        evidence.MediaType.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
        !(evidence.Reference.StartsWith("artifact://", StringComparison.Ordinal) ||
          evidence.Reference.StartsWith("record://", StringComparison.Ordinal) ||
          evidence.Reference.StartsWith("revision://", StringComparison.Ordinal) ||
          evidence.Reference.StartsWith("hash://", StringComparison.Ordinal)) ||
        !IsLowerHexHash(evidence.Sha256) || !string.Equals(evidence.Verification, "verified", StringComparison.Ordinal) ||
        ContainsProhibitedText(evidence.Reference);

    private static bool InvalidPayload(string key, string value)
    {
        var normalizedName = new string(key.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(key) || key.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters ||
               ProhibitedPropertyNames.Contains(normalizedName) || ContainsProhibitedText(value);
    }

    private static bool InvalidKeyword(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length > LearningFeedbackContractLimits.MaximumProposalKeywordCharacters ||
        !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
        value.Any(char.IsControl) ||
        ContainsProhibitedText(value);

    private static IReadOnlyDictionary<string, string> CanonicalDictionary(IReadOnlyDictionary<string, string> values)
    {
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
            canonical.Add(pair.Key, pair.Value);
        return new ReadOnlyDictionary<string, string>(canonical);
    }

    private static bool ContainsProhibitedText(string? value)
    {
        if (value is null)
            return false;

        // The ingress sanitizer emits only these fixed placeholders. Remove
        // their complete syntactic forms before retaining the legacy strict
        // marker checks; raw markers and partially redacted values still fail.
        var screened = AcceptedSecretPlaceholderRegex().Replace(
            value,
            "[REDACTED]");
        return ProhibitedTextMarkers.Any(marker =>
            screened.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(
        "\\bBearer\\s+\\[TOKEN\\]|\\b(?:password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|session[_-]?cookie|authorization|credential|private[_-]?key|secret|token)\\s*[:=]\\s*\\[(?:SECRET|TOKEN|PRIVATE_KEY)\\]|\\[PRIVATE_KEY\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex AcceptedSecretPlaceholderRegex();

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidOptionalIdentifier(string? value) => value is null || ValidIdentifier(value);

    private static bool IsProposalV1_1(LearningFeedbackProposalV1 proposal) =>
        string.Equals(
            proposal.ContractId,
            LearningFeedbackContractIds.ProposalV1_1,
            StringComparison.Ordinal);

    private static bool IsLowerHexHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Missing(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
    private static string NormalizeText(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static CortexContractResult<T> Reject<T>(PegasusDiagnostic diagnostic, string? submittedHash) where T : class =>
        new(false, null, submittedHash, [diagnostic]);

    private static PegasusDiagnostic Error(
        string code,
        string message,
        string? proposalId,
        string? decisionId = null,
        string? cause = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.learning-feedback.validate",
            false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["proposalId"] = DiagnosticIdentifier(proposalId),
                ["decisionId"] = DiagnosticIdentifier(decisionId)
            },
            cause);

    private static PegasusDiagnostic AccessError(
        string code,
        string message,
        string? namespaceName,
        LearningFeedbackNamespaceCapability capability) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.learning-feedback.authorize",
            false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["namespace"] = namespaceName is { Length: <= LearningFeedbackContractLimits.MaximumNamespaceCharacters }
                    ? namespaceName
                    : null,
                ["capability"] = Enum.IsDefined(capability) ? capability.ToString() : null
            });

    private static string? DiagnosticIdentifier(string? value) =>
        value is { Length: > 0 and <= LearningFeedbackContractLimits.MaximumIdentifierCharacters }
            ? value
            : null;
}
