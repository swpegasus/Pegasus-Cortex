using System.Text;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>
/// Standalone-only adapter for private reusable-lesson proposals. Project
/// identity and namespace come exclusively from the protected local registry.
/// It supports bounded project-scoped private head listing for later
/// Generalize screening, but has no review, audit, publication, generic-search,
/// or network capability.
/// </summary>
internal sealed class CortexMcpLocalProposalRuntime :
    ICortexProposalSubmissionRuntime,
    ICortexProposalQueryRuntime
{
    private const string LocalAuthorityId = "local-os-policy";
    private const string LocalServiceId = "pegasus-cortex-local";
    private const string ScreeningAuthorityId = "local-cortex-screening";
    private const string EmptyHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly CortexLocalProjectRegistry _registry;
    private readonly ICortexAgentRetentionRepository _retention;
    private readonly TimeProvider _timeProvider;
    private readonly string _localSubjectId;

    public CortexMcpLocalProposalRuntime(
        CortexLocalProjectRegistry registry,
        ICortexAgentRetentionRepository retention,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localSubjectId = $"os_{CortexLocalOsIdentity.Capture().Hash}";
    }

    public async ValueTask<CortexProposalSubmissionResult?> SubmitProposalAsync(
        string projectLinkId,
        string idempotencyKey,
        string feedbackKind,
        string title,
        string summary,
        string content,
        CortexProposalMetadataV1? metadata,
        IReadOnlyList<LearningFeedbackEvidenceV1> evidenceReferences,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeProposalWrite(
                projectLinkId,
                out var registration))
        {
            return null;
        }

        var stableKeyScreen =
            LearningFeedbackProposalInputSanitizer.ScreenStableIdentifier(
                idempotencyKey);
        if (!stableKeyScreen.Accepted)
            return ScreeningRejected(stableKeyScreen.FindingCodes);

        var boundedEvidence = CortexProposalEvidencePolicy.Capture(evidenceReferences);
        if (!CortexProposalMetadataPolicy.TryCapture(metadata, out var capturedMetadata))
            return ScreeningRejected(["metadata_invalid"]);
        var sanitization = LearningFeedbackProposalInputSanitizer.Sanitize(
            title,
            summary,
            content,
            capturedMetadata?.Taxonomy,
            capturedMetadata?.Keywords,
            capturedMetadata?.Applicability,
            boundedEvidence);
        if (!sanitization.Accepted || sanitization.Value is null)
            return ScreeningRejected(sanitization.FindingCodes);

        // From this boundary onward only sanitized values may participate in
        // identity, hashes, screening evidence, or persistence.
        var sanitized = sanitization.Value;
        var sanitizedMetadata = metadata is null
            ? null
            : new CortexProposalMetadataV1(
                sanitized.Taxonomy,
                sanitized.Keywords ?? [],
                sanitized.Applicability ?? []);
        if (!CortexProposalMetadataPolicy.TryCapture(
                sanitizedMetadata,
                out capturedMetadata))
        {
            return ScreeningRejected(["sanitized_metadata_invalid"]);
        }
        var evidence = CortexProposalEvidencePolicy.Capture(sanitized.Evidence);
        title = sanitized.Title;
        summary = sanitized.Summary;
        content = sanitized.Content;
        // Standalone proposals remain private pending records. Accept details
        // that a later integrated publication can deterministically replace.
        // Detectable secrets have already been replaced; private reasoning,
        // raw streams, and unsafe redaction boundaries still fail closed.
        var projectScreening = LearningFeedbackContentScreening.ScreenProjectBoundProposal(
            title,
            summary,
            string.Concat(
                content,
                "\n",
                CortexProposalEvidencePolicy.ScreeningProjection(evidence)),
            collectiveProjectionRequested: true,
            capturedMetadata?.Taxonomy,
            capturedMetadata?.Keywords,
            capturedMetadata?.Applicability);
        var screening = projectScreening with
        {
            FindingCodes = Array.AsReadOnly(
                projectScreening.FindingCodes
                    .Concat(sanitized.RedactionCodes)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray())
        };
        if (!screening.Accepted)
            return ScreeningRejected(screening.FindingCodes);

        var contentHash =
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content));
        var identityHash = capturedMetadata is null
            ? CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    link = registration!.ProjectLinkId,
                    idempotencyKey,
                    feedbackKind,
                    title,
                    summary,
                    contentHash,
                    evidence,
                    occurredAt = occurredAt.ToUniversalTime()
                }))
            : CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    contract = LearningFeedbackContractIds.ProposalV1_1,
                    link = registration!.ProjectLinkId,
                    idempotencyKey,
                    feedbackKind,
                    title,
                    summary,
                    contentHash,
                    metadata = capturedMetadata,
                    evidence,
                    occurredAt = occurredAt.ToUniversalTime()
                }));
        var screeningDecisionId = $"screen_{identityHash}";
        var screeningDecisionHash = capturedMetadata is null
            ? CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    contract = "pegasus.cortex.local-screening/1.0",
                    proposalIdentity = new
                    {
                        link = registration!.ProjectLinkId,
                        idempotencyKey,
                        feedbackKind,
                        title,
                        summary,
                        contentHash,
                        evidence,
                        occurredAt = occurredAt.ToUniversalTime()
                    },
                    policyRevision = 1,
                    libraryAccess = "private-local",
                    findingCodes = screening.FindingCodes,
                    result = "sanitized"
                }))
            : CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    contract = "pegasus.cortex.local-screening/1.0",
                    proposalIdentity = new
                    {
                        contract = LearningFeedbackContractIds.ProposalV1_1,
                        link = registration!.ProjectLinkId,
                        idempotencyKey,
                        feedbackKind,
                        title,
                        summary,
                        contentHash,
                        metadata = capturedMetadata,
                        evidence,
                        occurredAt = occurredAt.ToUniversalTime()
                    },
                    policyRevision = 1,
                    libraryAccess = "private-local",
                    findingCodes = screening.FindingCodes,
                    result = "sanitized"
                }));
        var authority = CreateAuthority(
            registration,
            screeningDecisionId,
            screeningDecisionHash);
        var proposal = new LearningFeedbackProposalV1(
            SchemaVersion: 1,
            capturedMetadata is null
                ? LearningFeedbackContractIds.Proposal
                : LearningFeedbackContractIds.ProposalV1_1,
            LearningFeedbackContractIds.Canonicalization,
            $"proposal_{identityHash}",
            registration.TenantId,
            registration.ProjectNamespace,
            feedbackKind,
            new LearningFeedbackSourceV1(
                LocalAuthorityId,
                _localSubjectId,
                LocalServiceId,
                // The local OS boundary authenticates the user and service
                // process, not the identity of an arbitrary stdio/pipe client.
                AgentId: null,
                InstanceId: null,
                MachineId: null,
                OperationId: "inline-content-submit",
                TaskId: null,
                CorrelationId: registration.ProjectLinkId,
                // These identifiers describe the inline submission itself;
                // they are not external or retained-record locators.
                RecordId: $"inline-content_{contentHash}",
                RevisionId: $"inline-content-revision_{contentHash}",
                ContentHash: contentHash,
                OccurredAt: occurredAt),
            summary,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content"] = content,
                ["retentionRecord"] = "true",
                ["sourceDisposition"] = "inline-submitted-content",
                ["title"] = title
            },
            ["retrieval", "evaluation"],
            Evidence: evidence,
            new LearningFeedbackScreeningV1(
                "sanitized",
                "passed",
                "passed",
                "passed",
                "passed",
                "evaluation",
                PolicyRevision: 1,
                ScreeningAuthorityId,
                screeningDecisionId,
                screeningDecisionHash,
                occurredAt),
            ReviewState: "pending",
            LearningFeedbackContractIds.ContentTreatment,
            Executable: false,
            CreatedAt: occurredAt,
            ProposalHash: EmptyHash,
            Taxonomy: capturedMetadata?.Taxonomy,
            Keywords: capturedMetadata?.Keywords,
            Applicability: capturedMetadata?.Applicability);

        var repository = new RetainedLearningFeedbackProposalRepository(
            _retention,
            new RetainedLearningFeedbackProposalScope(
                registration.ProjectId,
                idempotencyKey,
                _localSubjectId,
                DateTimeOffset.MaxValue));
        var application = new LearningFeedbackApplicationService(
            repository,
            _timeProvider);
        var submission = await application.SubmitAsync(
            new SubmitLearningFeedbackCommand(proposal, authority),
            cancellationToken).ConfigureAwait(false);
        return new CortexProposalSubmissionResult(
            submission,
            ProvisionalReview: null,
            ProvisionalPublicationAttempted: false,
            ExistingDecision: null);
    }

    public async ValueTask<LearningFeedbackPageResult?> ListProposalsAsync(
        string projectLinkId,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeProposalRead(
                projectLinkId,
                out var registration))
        {
            return null;
        }

        var repository = new RetainedLearningFeedbackProposalRepository(
            _retention,
            new RetainedLearningFeedbackProposalScope(
                registration!.ProjectId,
                "private-proposal-query",
                _localSubjectId,
                DateTimeOffset.MaxValue));
        var application = new LearningFeedbackApplicationService(
            repository,
            _timeProvider);
        return await application.ListAsync(
            new ListLearningFeedbackQuery(
                registration.ProjectNamespace,
                afterProposalId,
                limit,
                CreateReadAuthority(registration),
                LearningFeedbackNamespaceCapability.Read),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LearningFeedbackAuditAssessmentV1?>
        FindLatestAssessmentAsync(
            string projectLinkId,
            string proposalId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<LearningFeedbackAuditAssessmentV1?>(null);
    }

    private LearningFeedbackAuthorityContext CreateAuthority(
        CortexLocalProjectRegistration registration,
        string screeningDecisionId,
        string screeningDecisionHash) =>
        new(
            registration.TenantId,
            LocalAuthorityId,
            _localSubjectId,
            LocalServiceId,
            PolicyRevision: 1,
            [
                new LearningFeedbackNamespaceGrant(
                    registration.ProjectNamespace,
                    CanSubmit: true)
            ],
            new HashSet<string>(
                [ScreeningAuthorityId],
                StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [screeningDecisionId] = screeningDecisionHash
            });

    private LearningFeedbackAuthorityContext CreateReadAuthority(
        CortexLocalProjectRegistration registration) =>
        new(
            registration.TenantId,
            LocalAuthorityId,
            _localSubjectId,
            LocalServiceId,
            PolicyRevision: 1,
            [
                new LearningFeedbackNamespaceGrant(
                    registration.ProjectNamespace,
                    CanSubmit: false,
                    CanRead: true)
            ],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static CortexProposalSubmissionResult ScreeningRejected(
        IReadOnlyList<string> findingCodes) =>
        new(
            new LearningFeedbackSubmissionResult(
                LearningFeedbackMutationDisposition.Rejected,
                Proposal: null,
                SubmittedPayloadHash: null,
                Audit: null,
                Diagnostics:
                [
                    new PegasusDiagnostic(
                        "cortex_retention_screening_rejected",
                        "The proposed retained knowledge could not be safely bounded, redacted, or separated from prohibited private/raw content.",
                        DiagnosticSeverity.Error,
                        "cortex.learning-feedback.screen",
                        retryable: false,
                        new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["findings"] = string.Join(',', findingCodes),
                            ["libraryAccess"] = "private-local"
                        })
                ]),
            ProvisionalReview: null,
            ProvisionalPublicationAttempted: false,
            ExistingDecision: null);
}
