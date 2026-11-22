using System.Text;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Service.Persistence;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Mcp.Integrated.Runtime;

internal sealed class CortexMcpIntegratedRuntime :
    ICortexProjectKnowledgeRuntime,
    ICortexProjectCorrelationRuntime,
    ICortexProposalSubmissionRuntime,
    ICortexProposalQueryRuntime,
    ICortexProposalReviewRuntime,
    ICortexPublicationChangeRuntime,
    ICortexProposalAuditRuntime,
    ICortexReviewGrantRuntime,
    ICortexProjectBriefRuntime,
    ICortexSyncRuntime,
    IAsyncDisposable
{
    private static readonly string EmptyHash = new('0', 64);
    private readonly CortexPostgreSqlComposition _composition;
    private readonly HmacSha256CortexRowAuthenticator _rowAuthenticator;
    private readonly ICortexQuerySource _querySource;
    private readonly ICortexProjectLinkRepository _projectLinks;
    private readonly ILearningFeedbackRepository _learningRepository;
    private readonly ILearningFeedbackAuditRepository? _auditRepository;
    private readonly ILearningFeedbackApplicationService _learning;
    private readonly ILearningFeedbackPublicationChangeService? _publicationChanges;
    private readonly CortexLocalAuthority _authority;
    private readonly CortexProjectBriefService _projectBrief;
    private readonly CortexSyncApplicationService? _sync;
    private readonly HmacSha256CortexSyncReceiptIssuer? _syncReceiptIssuer;

    public CortexMcpIntegratedRuntime(
        CortexPostgreSqlComposition composition,
        HmacSha256CortexRowAuthenticator rowAuthenticator,
        ICortexQuerySource querySource,
        ICortexProjectLinkRepository projectLinks,
        ILearningFeedbackRepository learningRepository,
        CortexLocalAuthority authority,
        ICortexProjectBriefReadModel? projectBriefReadModel = null,
        ICortexSyncRepository? syncRepository = null,
        HmacSha256CortexSyncReceiptIssuer? syncReceiptIssuer = null,
        TimeProvider? timeProvider = null)
    {
        _composition = composition;
        _rowAuthenticator = rowAuthenticator;
        _querySource = querySource;
        _projectLinks = projectLinks;
        _learningRepository = learningRepository;
        _auditRepository = learningRepository as ILearningFeedbackAuditRepository;
        var clock = timeProvider ?? TimeProvider.System;
        _learning = new LearningFeedbackApplicationService(learningRepository, clock);
        _publicationChanges = learningRepository is ILearningFeedbackPublicationChangeRepository changes
            ? new LearningFeedbackPublicationChangeService(changes, clock)
            : null;
        _authority = authority;
        _projectBrief = new CortexProjectBriefService(
            projectBriefReadModel ?? new InMemoryCortexProjectBriefReadModel(),
            clock);
        if ((syncRepository is null) != (syncReceiptIssuer is null))
        {
            throw new ArgumentException(
                "The synchronization repository and receipt authority must be composed together.");
        }
        _syncReceiptIssuer = syncReceiptIssuer;
        _sync = syncRepository is null
            ? null
            : new CortexSyncApplicationService(syncRepository, syncReceiptIssuer!, clock);
    }

    // Operator-only commands share the already-readiness-gated runtime owner.
    // This is internal and is never projected into the MCP tool surface.
    internal CortexPostgreSqlComposition PostgreSqlComposition => _composition;

    public CortexPreparedProjectLink PrepareProjectLink(
        string projectId,
        string storeId,
        long storeEpoch,
        string contractVersion,
        string libraryAccess) =>
        CortexProjectCorrelationPolicy.Prepare(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Correlate),
            projectId,
            storeId,
            storeEpoch,
            _authority.EndpointId,
            contractVersion,
            libraryAccess);

    public async ValueTask<CortexProjectLinkWriteResult> ConfirmProjectLinkAsync(
        string projectId,
        string storeId,
        long storeEpoch,
        string contractVersion,
        string libraryAccess,
        string correlationHash,
        CancellationToken cancellationToken)
    {
        var access = _authority.ProjectLinkAccess(CortexProjectLinkCapability.Correlate);
        var prepared = CortexProjectCorrelationPolicy.Prepare(
            access,
            projectId,
            storeId,
            storeEpoch,
            _authority.EndpointId,
            contractVersion,
            libraryAccess);
        var link = CortexProjectCorrelationPolicy.Confirm(access, prepared, correlationHash);
        return await _projectLinks.AddAsync(access, link, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexProjectSearchResult?> SearchProjectAsync(
        string projectLinkId,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return new CortexProjectSearchResult(
                CortexProjectSearchResult.LinkRejected,
                Page: null);
        }
        var service = CreateProjectQueryService(link);
        var query = new CortexSearchQuery(
            link.Metadata.ProjectNamespace,
            text,
            taxonomy,
            productId,
            componentId,
            limit,
            includeStale,
            includeBlockedOrUnverified,
            mode);
        var result = await service.SearchAsync(
            query,
            _authority.QueryAuthority(link.Metadata.ProjectNamespace),
            cancellationToken).ConfigureAwait(false);
        if (!result.Accepted || result.Value is null || result.Diagnostics.Count != 0)
        {
            return new CortexProjectSearchResult(
                CortexProjectSearchResult.ProviderFailure,
                Page: null);
        }
        return new CortexProjectSearchResult(
            CortexProjectSearchResult.Success,
            result.Value);
    }

    public async ValueTask<CortexCollectiveSearchResult?> SearchCollectiveAsync(
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        var links = await _projectLinks.ListAsync(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
            CortexProjectCorrelationContract.MaximumProjectLinkPageSize,
            cancellationToken).ConfigureAwait(false);
        if (links.Count > CortexProjectCorrelationContract.MaximumCollectiveLibraries)
            return null;

        var hits = new List<CortexCollectiveSearchHit>(
            checked(links.Count * limit));
        var eligibleSourceCount = 0;
        var queriedSourceCount = 0;
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    link.Metadata.EndpointId,
                    _authority.EndpointId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    CortexProjectCorrelationPolicy.EffectiveLibraryAccess(link.Metadata),
                    CortexProjectCorrelationContract.CollectiveLibraryAccess,
                    StringComparison.Ordinal))
            {
                continue;
            }

            eligibleSourceCount++;

            var service = CreateProjectQueryService(link);
            var query = new CortexSearchQuery(
                link.Metadata.ProjectNamespace,
                text,
                taxonomy,
                productId,
                componentId,
                limit,
                includeStale,
                includeBlockedOrUnverified,
                mode);
            queriedSourceCount++;
            var result = await service.SearchAsync(
                query,
                _authority.QueryAuthority(link.Metadata.ProjectNamespace),
                cancellationToken).ConfigureAwait(false);
            if (!result.Accepted || result.Value is null || result.Diagnostics.Count != 0)
                return null;
            hits.AddRange(result.Value.Records.Select(
                hit => new CortexCollectiveSearchHit(hit, link.Metadata.ProjectLinkId)));
        }

        // Each source is already bounded by limit. Return every source's candidates so
        // the provider-neutral application can de-duplicate, reserve one result per
        // source, and only then apply the global limit.
        var orderedHits = hits
            .OrderByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.Hit.Record.RevisionId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectLinkId, StringComparer.Ordinal)
            .ToArray();
        return new CortexCollectiveSearchResult(
            orderedHits,
            eligibleSourceCount,
            queriedSourceCount,
            PartialFailureCount: 0);
    }

    public async ValueTask<CortexKnowledgeRecord?> ReadProjectAsync(
        string projectLinkId,
        string recordId,
        string revisionId,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var service = CreateProjectQueryService(link);
        var result = await service.ReadAsync(
            new CortexRecordReadQuery(
                link.Metadata.ProjectNamespace,
                recordId,
                revisionId,
                includeStale,
                includeBlockedOrUnverified),
            _authority.QueryAuthority(link.Metadata.ProjectNamespace),
            cancellationToken).ConfigureAwait(false);
        return result.Accepted && result.Diagnostics.Count == 0 ? result.Value : null;
    }

    public async ValueTask<CortexProjectBriefResult?> ReadProjectBriefAsync(
        string projectLinkId,
        string? knownContextSetId,
        string knownAuthoritySetHash,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var scope = new CortexProjectBriefReadScope(
            _authority.TenantId,
            link.Metadata.ProjectLinkId,
            link.Metadata.ProjectId,
            link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            _authority.SubjectId,
            "pegasus-cortex-mcp",
            _authority.PolicyRevision);
        return await _projectBrief.ReadAsync(
            new CortexProjectBriefRequest(
                projectLinkId,
                knownAuthoritySetHash,
                knownContextSetId,
                maximumUtf8Bytes),
            scope,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexSyncApplicationResult?> SubmitSyncBundleAsync(
        string targetNamespace,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sync is null ||
            !_authority.HasCapability(CortexLocalCapability.Sync) ||
            !await CanSynchronizeProjectAsync(
                targetNamespace,
                bundle.ProjectId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await _sync.SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                targetNamespace,
                bundle,
                _authority.SyncAuthority(
                    targetNamespace,
                    AllowedSyncPayloadContractIds)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexSyncApplicationResult?> SubmitLegacyMigrationPageAsync(
        string targetNamespace,
        CortexLegacyMigrationPageV1 page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sync is null ||
            !_authority.HasCapability(CortexLocalCapability.Sync) ||
            !await CanSynchronizeProjectAsync(
                targetNamespace,
                page.Bundle.ProjectId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await _sync.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(
                targetNamespace,
                page,
                _authority.SyncAuthority(
                    targetNamespace,
                    AllowedSyncPayloadContractIds)),
            cancellationToken).ConfigureAwait(false);
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
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;

        var collective = string.Equals(
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(link.Metadata),
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            StringComparison.Ordinal);
        var stableKeyScreen =
            LearningFeedbackProposalInputSanitizer.ScreenStableIdentifier(
                idempotencyKey);
        if (!stableKeyScreen.Accepted)
            return ProposalScreeningRejected(stableKeyScreen.FindingCodes, collective);

        var boundedEvidence = CortexProposalEvidencePolicy.Capture(evidenceReferences);
        if (!CortexProposalMetadataPolicy.TryCapture(metadata, out var capturedMetadata))
            return ProposalScreeningRejected(["metadata_invalid"], collective);
        var sanitization = LearningFeedbackProposalInputSanitizer.Sanitize(
            title,
            summary,
            content,
            capturedMetadata?.Taxonomy,
            capturedMetadata?.Keywords,
            capturedMetadata?.Applicability,
            boundedEvidence);
        if (!sanitization.Accepted || sanitization.Value is null)
            return ProposalScreeningRejected(sanitization.FindingCodes, collective);

        // Never compute identity, hashes, audit provenance, or stored proposal
        // bytes from the raw caller input after this point.
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
            return ProposalScreeningRejected(["sanitized_metadata_invalid"], collective);
        }
        var evidence = CortexProposalEvidencePolicy.Capture(sanitized.Evidence);
        title = sanitized.Title;
        summary = sanitized.Summary;
        content = sanitized.Content;
        var projectScreening = LearningFeedbackContentScreening.ScreenProjectBoundProposal(
            title,
            summary,
            string.Concat(
                content,
                "\n",
                CortexProposalEvidencePolicy.ScreeningProjection(evidence)),
            collectiveProjectionRequested: collective,
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
            return ProposalScreeningRejected(screening.FindingCodes, collective);

        var contentHash = CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content));
        var identityHash = capturedMetadata is null
            ? CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    link = link.Metadata.ProjectLinkId,
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
                    link = link.Metadata.ProjectLinkId,
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
                        link = link.Metadata.ProjectLinkId,
                        idempotencyKey,
                        feedbackKind,
                        title,
                        summary,
                        contentHash,
                        evidence,
                        occurredAt = occurredAt.ToUniversalTime()
                    },
                    policyRevision = _authority.PolicyRevision,
                    libraryAccess = collective
                        ? CortexProjectCorrelationContract.CollectiveLibraryAccess
                        : CortexProjectCorrelationContract.ReservedLibraryAccess,
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
                        link = link.Metadata.ProjectLinkId,
                        idempotencyKey,
                        feedbackKind,
                        title,
                        summary,
                        contentHash,
                        metadata = capturedMetadata,
                        evidence,
                        occurredAt = occurredAt.ToUniversalTime()
                    },
                    policyRevision = _authority.PolicyRevision,
                    libraryAccess = collective
                        ? CortexProjectCorrelationContract.CollectiveLibraryAccess
                        : CortexProjectCorrelationContract.ReservedLibraryAccess,
                    findingCodes = screening.FindingCodes,
                    result = "sanitized"
                }));
        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Submit,
            screeningDecisionId,
            screeningDecisionHash);
        var proposal = new LearningFeedbackProposalV1(
            1,
            capturedMetadata is null
                ? LearningFeedbackContractIds.Proposal
                : LearningFeedbackContractIds.ProposalV1_1,
            LearningFeedbackContractIds.Canonicalization,
            $"proposal_{identityHash}",
            authority.TenantId,
            link.Metadata.ProjectNamespace,
            feedbackKind,
            new LearningFeedbackSourceV1(
                authority.AuthorityId,
                authority.SubjectId,
                authority.ServiceId,
                AgentId: "codex",
                InstanceId: null,
                MachineId: _authority.EndpointId,
                OperationId: "inline-content-submit",
                TaskId: null,
                CorrelationId: projectLinkId,
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
                authority.PolicyRevision,
                "local-cortex-screening",
                screeningDecisionId,
                screeningDecisionHash,
                occurredAt),
            "pending",
            LearningFeedbackContractIds.ContentTreatment,
            Executable: false,
            CreatedAt: occurredAt,
            ProposalHash: EmptyHash,
            Taxonomy: capturedMetadata?.Taxonomy,
            Keywords: capturedMetadata?.Keywords,
            Applicability: capturedMetadata?.Applicability);
        var submission = await _learning.SubmitAsync(
            new SubmitLearningFeedbackCommand(proposal, authority),
            cancellationToken).ConfigureAwait(false);
        var shouldPublishProvisionally =
            _authority.AutoPublishSafeCollectiveProposals &&
            screening.Accepted &&
            LearningFeedbackPublicationPolicy.IsProvisionalAutoPublicationEligible(
                feedbackKind,
                collective);
        if (!shouldPublishProvisionally ||
            !submission.Succeeded ||
            submission.Proposal is null)
        {
            return new CortexProposalSubmissionResult(
                submission,
                ProvisionalReview: null,
                ProvisionalPublicationAttempted: false,
                ExistingDecision: null);
        }

        // A retry can arrive after the proposal write succeeded but the review
        // write failed. Read the immutable snapshot before retrying so the same
        // request heals a pending proposal without conflicting with a decision
        // that was already accepted through another authorized path.
        if (submission.Disposition == LearningFeedbackMutationDisposition.Idempotent)
        {
            var readAuthority = _authority.LearningAuthority(
                link.Metadata.ProjectNamespace,
                LearningFeedbackNamespaceCapability.Read);
            var readAccess = LearningFeedbackPolicy.AuthorizeNamespace(
                readAuthority,
                link.Metadata.ProjectNamespace,
                LearningFeedbackNamespaceCapability.Read,
                cancellationToken);
            if (!readAccess.Accepted)
            {
                throw new UnauthorizedAccessException(
                    "The provisional-publication principal cannot read its submitted proposal.");
            }

            var existing = await _learningRepository.FindAsync(
                readAccess.Value!,
                submission.Proposal.ProposalId,
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    "An idempotent proposal submission has no durable snapshot.");
            }
            if (existing.Decision is not null)
            {
                return new CortexProposalSubmissionResult(
                    submission,
                    ProvisionalReview: null,
                    ProvisionalPublicationAttempted: false,
                    ExistingDecision: existing.Decision);
            }
        }

        var provisionalAuthority = _authority.ProvisionalReviewAuthority(
            link.Metadata.ProjectNamespace);
        var reviewedAt = DateTimeOffset.UtcNow;
        if (reviewedAt < submission.Proposal.CreatedAt)
            reviewedAt = submission.Proposal.CreatedAt;
        var provisionalIdentityHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(new
            {
                contract = "pegasus.cortex.provisional-publication/1.0",
                submission.Proposal.ProposalId,
                submission.Proposal.ProposalHash,
                provisionalAuthority.PolicyRevision
            }));
        var provisionalDecision = new LearningFeedbackReviewDecisionV1(
            1,
            LearningFeedbackContractIds.ReviewDecision,
            LearningFeedbackContractIds.Canonicalization,
            $"decision_{provisionalIdentityHash}",
            submission.Proposal.ProposalId,
            submission.Proposal.ProposalHash,
            provisionalAuthority.TenantId,
            submission.Proposal.Namespace,
            "approved",
            provisionalAuthority.AuthorityId,
            provisionalAuthority.SubjectId,
            provisionalAuthority.PolicyRevision,
            LearningFeedbackPublicationPolicy.ProvisionalReason,
            reviewedAt,
            EmptyHash);
        var provisionalReview = await _learning.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                provisionalDecision,
                provisionalAuthority),
            cancellationToken).ConfigureAwait(false);
        return new CortexProposalSubmissionResult(
            submission,
            provisionalReview,
            ProvisionalPublicationAttempted: true,
            ExistingDecision: null);
    }

    private static CortexProposalSubmissionResult ProposalScreeningRejected(
        IReadOnlyList<string> findingCodes,
        bool collective) =>
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
                            ["libraryAccess"] = collective
                                ? CortexProjectCorrelationContract.CollectiveLibraryAccess
                                : CortexProjectCorrelationContract.ReservedLibraryAccess
                        })
                ]),
            ProvisionalReview: null,
            ProvisionalPublicationAttempted: false,
            ExistingDecision: null);

    public async ValueTask<LearningFeedbackPublicationChangeResult?>
        ChangePublicationAsync(
            string projectLinkId,
            LearningFeedbackPublicationChangeRequestV1 request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_publicationChanges is null)
            return null;

        var link = await ResolveProjectLinkAsync(
            projectLinkId,
            cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;

        var authority = _authority.PublicationChangeAuthority(
            link.Metadata.ProjectNamespace);
        return await _publicationChanges.ChangeAsync(
            new ChangeLearningFeedbackPublicationCommand(
                link.Metadata.ProjectNamespace,
                request,
                authority),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LearningFeedbackPageResult?> ListProposalsAsync(
        string projectLinkId,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var capability = _authority.HasCapability(CortexLocalCapability.Audit)
            ? LearningFeedbackNamespaceCapability.Audit
            : _authority.HasCapability(CortexLocalCapability.Read)
                ? LearningFeedbackNamespaceCapability.Read
                : LearningFeedbackNamespaceCapability.Review;
        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            capability);
        return await _learning.ListAsync(
            new ListLearningFeedbackQuery(
                link.Metadata.ProjectNamespace,
                afterProposalId,
                limit,
                authority,
                capability),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LearningFeedbackAuditAssessmentV1?> FindLatestAssessmentAsync(
        string projectLinkId,
        string proposalId,
        CancellationToken cancellationToken)
    {
        if (_auditRepository is null)
            return null;
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var capability = _authority.HasCapability(CortexLocalCapability.Audit)
            ? LearningFeedbackNamespaceCapability.Audit
            : LearningFeedbackNamespaceCapability.Read;
        var authority = _authority.LearningAuthority(link.Metadata.ProjectNamespace, capability);
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            authority,
            link.Metadata.ProjectNamespace,
            capability,
            cancellationToken);
        return access.Accepted
            ? await _auditRepository.FindLatestAssessmentAsync(
                access.Value!,
                proposalId,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<LearningFeedbackReviewResult?> ReviewProposalAsync(
        string projectLinkId,
        string proposalId,
        string state,
        string reason,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken)
    {
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var readAuthority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Read);
        var readAccess = LearningFeedbackPolicy.AuthorizeNamespace(
            readAuthority,
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Read,
            cancellationToken);
        if (!readAccess.Accepted)
            return null;
        var snapshot = await _learningRepository.FindAsync(
            readAccess.Value!,
            proposalId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;

        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Review);
        var decisionIdentity = new
        {
            projectLinkId,
            proposalId,
            snapshot.Proposal.ProposalHash,
            state,
            reason,
            reviewedAt = reviewedAt.ToUniversalTime(),
            reviewer = authority.SubjectId
        };
        var decisionHashIdentity = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(decisionIdentity));
        var decision = new LearningFeedbackReviewDecisionV1(
            1,
            LearningFeedbackContractIds.ReviewDecision,
            LearningFeedbackContractIds.Canonicalization,
            $"decision_{decisionHashIdentity}",
            proposalId,
            snapshot.Proposal.ProposalHash,
            authority.TenantId,
            link.Metadata.ProjectNamespace,
            state,
            authority.AuthorityId,
            authority.SubjectId,
            authority.PolicyRevision,
            reason,
            reviewedAt,
            EmptyHash);
        return await _learning.ReviewAsync(
            new ReviewLearningFeedbackCommand(decision, authority),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LearningFeedbackReviewGrantWriteResult?> IssueReviewGrantAsync(
        string projectLinkId,
        string reviewGrantId,
        string delegateAuthorityId,
        string delegateSubjectId,
        IReadOnlyList<string> feedbackKinds,
        int maximumUses,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (_auditRepository is null)
            return null;
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.DelegateReview);
        var grant = new LearningFeedbackReviewGrantV1(
            1,
            LearningFeedbackContractIds.ReviewGrant,
            LearningFeedbackContractIds.Canonicalization,
            reviewGrantId,
            authority.TenantId,
            link.Metadata.ProjectNamespace,
            authority.AuthorityId,
            authority.SubjectId,
            delegateAuthorityId,
            delegateSubjectId,
            authority.PolicyRevision,
            feedbackKinds,
            CollectiveOnly: true,
            maximumUses,
            expiresAt,
            occurredAt,
            EmptyHash);
        var accepted = LearningFeedbackAuditPolicy.AcceptGrant(
            grant,
            authority,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!accepted.Accepted || accepted.Value is null)
            return null;
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            authority,
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.DelegateReview,
            cancellationToken);
        return access.Accepted
            ? await _auditRepository.IssueReviewGrantAsync(
                access.Value!,
                accepted.Value,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<LearningFeedbackReviewGrantWriteResult?> RevokeReviewGrantAsync(
        string projectLinkId,
        string reviewGrantId,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (_auditRepository is null)
            return null;
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.DelegateReview);
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            authority,
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.DelegateReview,
            cancellationToken);
        return access.Accepted
            ? await _auditRepository.RevokeReviewGrantAsync(
                access.Value!,
                reviewGrantId,
                reason,
                occurredAt,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<LearningFeedbackAuditApplyResult?> SubmitAuditAsync(
        string projectLinkId,
        string proposalId,
        string recommendation,
        string rationale,
        string? reviewGrantId,
        DateTimeOffset assessedAt,
        CancellationToken cancellationToken)
    {
        if (_auditRepository is null)
            return null;
        var link = await ResolveProjectLinkAsync(projectLinkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;
        var authority = _authority.LearningAuthority(
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Audit);
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            authority,
            link.Metadata.ProjectNamespace,
            LearningFeedbackNamespaceCapability.Audit,
            cancellationToken);
        if (!access.Accepted)
            return null;
        var snapshot = await _learningRepository.FindAsync(
            access.Value!,
            proposalId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;

        var identityHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(new
            {
                projectLinkId,
                proposalId,
                snapshot.Proposal.ProposalHash,
                recommendation,
                rationale,
                reviewGrantId,
                assessedAt = assessedAt.ToUniversalTime(),
                auditor = authority.SubjectId
            }));
        var submitted = new LearningFeedbackAuditAssessmentV1(
            1,
            LearningFeedbackContractIds.AuditAssessment,
            LearningFeedbackContractIds.Canonicalization,
            $"assessment_{identityHash}",
            proposalId,
            snapshot.Proposal.ProposalHash,
            authority.TenantId,
            link.Metadata.ProjectNamespace,
            recommendation,
            authority.AuthorityId,
            authority.SubjectId,
            authority.PolicyRevision,
            rationale,
            reviewGrantId,
            RetentionConfidence: 0,
            assessedAt,
            EmptyHash);
        var accepted = LearningFeedbackAuditPolicy.AcceptAssessment(
            submitted,
            snapshot.Proposal,
            authority,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!accepted.Accepted || accepted.Value is null)
            return null;

        LearningFeedbackReviewDecisionV1? autoDecision = null;
        if (string.Equals(recommendation, "approve", StringComparison.Ordinal))
        {
            // The repository validates the persisted grant before this candidate can be stored.
            var grantedReviewAuthority = authority with
            {
                NamespaceGrants =
                [
                    new LearningFeedbackNamespaceGrant(
                        link.Metadata.ProjectNamespace,
                        CanSubmit: false,
                        CanReview: true)
                ]
            };
            var decisionIdentity = CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(new
                {
                    accepted.Value.AssessmentId,
                    accepted.Value.AssessmentHash,
                    proposalId,
                    snapshot.Proposal.ProposalHash,
                    reviewer = authority.SubjectId
                }));
            var candidate = new LearningFeedbackReviewDecisionV1(
                1,
                LearningFeedbackContractIds.ReviewDecision,
                LearningFeedbackContractIds.Canonicalization,
                $"decision_{decisionIdentity}",
                proposalId,
                snapshot.Proposal.ProposalHash,
                authority.TenantId,
                link.Metadata.ProjectNamespace,
                "approved",
                authority.AuthorityId,
                authority.SubjectId,
                authority.PolicyRevision,
                $"Delegated independent audit approval: {accepted.Value.AssessmentId}.",
                assessedAt,
                EmptyHash);
            var acceptedDecision = LearningFeedbackPolicy.AcceptReviewDecision(
                candidate,
                snapshot.Proposal,
                grantedReviewAuthority,
                DateTimeOffset.UtcNow,
                cancellationToken);
            autoDecision = acceptedDecision.Accepted ? acceptedDecision.Value : null;
        }

        var collective = string.Equals(
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(link.Metadata),
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            StringComparison.Ordinal);
        return await _auditRepository.ApplyAssessmentAsync(
            access.Value!,
            accepted.Value,
            autoDecision,
            collective,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexProjectLinkV1?> ResolveProjectLinkAsync(
        string projectLinkId,
        CancellationToken cancellationToken)
    {
        var link = await _projectLinks.FindAsync(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
            projectLinkId,
            cancellationToken).ConfigureAwait(false);
        return link is not null &&
               string.Equals(
                   link.Metadata.EndpointId,
                   _authority.EndpointId,
                   StringComparison.Ordinal)
            ? link
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _composition.DisposeAsync().ConfigureAwait(false);
        _syncReceiptIssuer?.Dispose();
        _rowAuthenticator.Dispose();
    }

    private async ValueTask<bool> CanSynchronizeProjectAsync(
        string targetNamespace,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetNamespace) ||
            string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        var links = await _projectLinks.ListAsync(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
            CortexProjectCorrelationContract.MaximumProjectLinkPageSize,
            cancellationToken).ConfigureAwait(false);
        return links.Any(link =>
            string.Equals(link.Metadata.EndpointId, _authority.EndpointId, StringComparison.Ordinal) &&
            string.Equals(link.Metadata.ProjectId, projectId, StringComparison.Ordinal) &&
            string.Equals(link.Metadata.ProjectNamespace, targetNamespace, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> AllowedSyncPayloadContractIds { get; } =
    [
        CortexSyncContractIds.LegacyMigrationRecord,
        CortexSyncContractIds.LegacyFileCopyPayload,
        AgentTaskSnapshotContractIds.Snapshot,
        AgentWorkPacketContractIds.Packet,
        AgentWorkPacketContractIds.HandoffReceipt,
        AgentWorkPacketContractIds.AcceptanceReceipt
    ];

    private CortexQueryService CreateProjectQueryService(CortexProjectLinkV1 link) =>
        new(
            new ProjectLinkedQuerySource(_querySource, link),
            new CortexQueryServiceOptions(
                link.Metadata.ProjectNamespace,
                "hybrid-postgresql",
                "authenticated-at-read"));

}
