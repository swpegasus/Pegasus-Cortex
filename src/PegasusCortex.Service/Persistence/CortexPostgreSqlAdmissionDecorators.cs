using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Service.Persistence;

/// <summary>
/// Rechecks composition admission at every cached-adapter operation. A successful factory call
/// never becomes a permanent capability after readiness is cleared, the pool is poisoned, or the
/// composition is disposed.
/// </summary>
internal sealed class CortexPostgreSqlQueryAdmissionDecorator(
    ICortexQuerySource inner,
    Action<string> ensureAdmission) : ICortexQuerySource
{
    public ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission(scope?.Operation ?? "cortex.postgresql.query.snapshot");
        return inner.GetSnapshotAsync(scope!, cancellationToken);
    }

    public ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission(scope?.Operation ?? "cortex.postgresql.query.search");
        return inner.SearchAsync(query, scope!, cancellationToken);
    }

    public ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission(scope?.Operation ?? "cortex.postgresql.query.catalog");
        return inner.ListCatalogAsync(query, scope!, cancellationToken);
    }

    public ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission(scope?.Operation ?? "cortex.postgresql.query.read");
        return inner.ReadAsync(query, scope!, cancellationToken);
    }
}

internal class CortexPostgreSqlLearningAdmissionDecorator : ILearningFeedbackRepository
{
    private readonly ILearningFeedbackRepository _inner;
    private readonly Action<string> _ensureAdmission;

    protected CortexPostgreSqlLearningAdmissionDecorator(
        ILearningFeedbackRepository inner,
        Action<string> ensureAdmission)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ensureAdmission = ensureAdmission ?? throw new ArgumentNullException(nameof(ensureAdmission));
    }

    internal static ILearningFeedbackRepository Create(
        ILearningFeedbackRepository inner,
        Action<string> ensureAdmission)
    {
        var auditRepository = inner as ILearningFeedbackAuditRepository;
        var publicationChangeRepository =
            inner as ILearningFeedbackPublicationChangeRepository;
        if (auditRepository is not null && publicationChangeRepository is not null)
        {
            return new CortexPostgreSqlLearningAuditPublicationChangeAdmissionDecorator(
                inner,
                auditRepository,
                publicationChangeRepository,
                ensureAdmission);
        }
        if (auditRepository is not null)
        {
            return new CortexPostgreSqlLearningAuditAdmissionDecorator(
                inner,
                auditRepository,
                ensureAdmission);
        }
        if (publicationChangeRepository is not null)
        {
            return new CortexPostgreSqlLearningPublicationChangeAdmissionDecorator(
                inner,
                publicationChangeRepository,
                ensureAdmission);
        }
        return new CortexPostgreSqlLearningAdmissionDecorator(inner, ensureAdmission);
    }

    protected void EnsureAdmission(string operation) => _ensureAdmission(operation);

    public ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.find");
        return _inner.FindAsync(access, proposalId, cancellationToken);
    }

    public ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.submit");
        return _inner.AddProposalAsync(access, proposal, cancellationToken);
    }

    public ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.review");
        return _inner.AddDecisionIfPendingAsync(access, decision, cancellationToken);
    }

    public ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.list");
        return _inner.ListAsync(access, afterProposalId, limit, cancellationToken);
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.status");
        return _inner.GetStatusAsync(access, cancellationToken);
    }
}

/// <summary>
/// Preserves the optional audit boundary only when the admitted inner repository supports it.
/// This keeps advertised MCP audit capabilities aligned with the persistence surface.
/// </summary>
internal class CortexPostgreSqlLearningAuditAdmissionDecorator(
    ILearningFeedbackRepository inner,
    ILearningFeedbackAuditRepository auditRepository,
    Action<string> ensureAdmission) :
    CortexPostgreSqlLearningAdmissionDecorator(inner, ensureAdmission),
    ILearningFeedbackAuditRepository
{
    public ValueTask<LearningFeedbackReviewGrantWriteResult> IssueReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewGrantV1 grant,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.review-grant.issue");
        return auditRepository.IssueReviewGrantAsync(access, grant, cancellationToken);
    }

    public ValueTask<LearningFeedbackReviewGrantWriteResult> RevokeReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        string reviewGrantId,
        string reason,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.review-grant.revoke");
        return auditRepository.RevokeReviewGrantAsync(
            access,
            reviewGrantId,
            reason,
            revokedAt,
            cancellationToken);
    }

    public ValueTask<LearningFeedbackAuditApplyResult> ApplyAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackAuditAssessmentV1 assessment,
        LearningFeedbackReviewDecisionV1? autoApprovalDecision,
        bool collectiveLibrary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.audit.apply");
        return auditRepository.ApplyAssessmentAsync(
            access,
            assessment,
            autoApprovalDecision,
            collectiveLibrary,
            cancellationToken);
    }

    public ValueTask<LearningFeedbackAuditAssessmentV1?> FindLatestAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.audit.find");
        return auditRepository.FindLatestAssessmentAsync(
            access,
            proposalId,
            cancellationToken);
    }
}

/// <summary>
/// Preserves the optional author-owned publication-change boundary while still
/// rechecking PostgreSQL admission at the moment of the CAS operation.
/// </summary>
internal class CortexPostgreSqlLearningPublicationChangeAdmissionDecorator(
    ILearningFeedbackRepository inner,
    ILearningFeedbackPublicationChangeRepository publicationChangeRepository,
    Action<string> ensureAdmission) :
    CortexPostgreSqlLearningAdmissionDecorator(inner, ensureAdmission),
    ILearningFeedbackPublicationChangeRepository
{
    public ValueTask<LearningFeedbackPublicationChangeWriteResult>
        ApplyPublicationChangeAsync(
            LearningFeedbackNamespaceAccess access,
            LearningFeedbackPublicationChangeV1 change,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.publication-change");
        return publicationChangeRepository.ApplyPublicationChangeAsync(
            access,
            change,
            cancellationToken);
    }
}

/// <summary>
/// Combined adapter used by the PostgreSQL repository so capability discovery
/// retains both independent optional learning boundaries.
/// </summary>
internal sealed class CortexPostgreSqlLearningAuditPublicationChangeAdmissionDecorator(
    ILearningFeedbackRepository inner,
    ILearningFeedbackAuditRepository auditRepository,
    ILearningFeedbackPublicationChangeRepository publicationChangeRepository,
    Action<string> ensureAdmission) :
    CortexPostgreSqlLearningAuditAdmissionDecorator(
        inner,
        auditRepository,
        ensureAdmission),
    ILearningFeedbackPublicationChangeRepository
{
    public ValueTask<LearningFeedbackPublicationChangeWriteResult>
        ApplyPublicationChangeAsync(
            LearningFeedbackNamespaceAccess access,
            LearningFeedbackPublicationChangeV1 change,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.learning-feedback.publication-change");
        return publicationChangeRepository.ApplyPublicationChangeAsync(
            access,
            change,
            cancellationToken);
    }
}

internal class CortexPostgreSqlSyncAdmissionDecorator :
    ICortexSyncRepository,
    ICortexSyncPublishedHeadRepositoryV2
{
    private readonly ICortexSyncRepository _inner;
    private readonly Action<string> _ensureAdmission;

    protected CortexPostgreSqlSyncAdmissionDecorator(
        ICortexSyncRepository inner,
        Action<string> ensureAdmission)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ensureAdmission = ensureAdmission ?? throw new ArgumentNullException(nameof(ensureAdmission));
    }

    internal static ICortexSyncRepository Create(
        ICortexSyncRepository inner,
        Action<string> ensureAdmission) =>
        inner is ICortexLegacyMigrationPageCommitRepository migrationRepository
            ? new CortexPostgreSqlMigrationSyncAdmissionDecorator(
                inner,
                migrationRepository,
                ensureAdmission)
            : new CortexPostgreSqlSyncAdmissionDecorator(inner, ensureAdmission);

    protected void EnsureAdmission(string operation) => _ensureAdmission(operation);

    public ValueTask<CortexSyncCommitSnapshot> CommitAsync(
        CortexSyncCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.commit");
        return _inner.CommitAsync(request, cancellationToken);
    }

    public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.readback");
        return _inner.ReadBackAsync(request, cancellationToken);
    }

    public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.receipt.append");
        return _inner.AppendReceiptAsync(request, cancellationToken);
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.receipt.find");
        return _inner.FindReceiptAsync(request, cancellationToken);
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.receipt.read");
        return _inner.ReadReceiptAsync(request, cancellationToken);
    }

    public ValueTask<CortexSyncPublishedJournalHead?> FindLatestPublishedAsync(
        CortexSyncPublishedHeadLookup request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.published-head.find");
        return _inner is ICortexSyncPublishedHeadRepository published
            ? published.FindLatestPublishedAsync(request, cancellationToken)
            : throw new InvalidOperationException(
                "The configured synchronization repository cannot prove a published head.");
    }

    public ValueTask<CortexSyncPublishedJournalHead?> FindPublishedAsync(
        CortexSyncPublishedBundleLookup request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.published-bundle.find");
        return _inner is ICortexSyncPublishedHeadRepositoryV2 published
            ? published.FindPublishedAsync(request, cancellationToken)
            : throw new InvalidOperationException(
                "The configured synchronization repository cannot prove a published bundle.");
    }
}

internal sealed class CortexPostgreSqlMigrationSyncAdmissionDecorator(
    ICortexSyncRepository inner,
    ICortexLegacyMigrationPageCommitRepository migrationRepository,
    Action<string> ensureAdmission) :
    CortexPostgreSqlSyncAdmissionDecorator(inner, ensureAdmission),
    ICortexLegacyMigrationPageCommitRepository
{
    public ValueTask<CortexLegacyMigrationPageCommitSnapshot> CommitLegacyMigrationPageAsync(
        CortexLegacyMigrationPageCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdmission("cortex.sync.legacy-page.commit");
        return migrationRepository.CommitLegacyMigrationPageAsync(request, cancellationToken);
    }
}

internal sealed class CortexPostgreSqlRetentionAdmissionDecorator(
    ICortexAgentRetentionRepository inner,
    Action<string> ensureAdmission) : ICortexAgentRetentionRepository
{
    public ValueTask<CortexRetentionAppendResult> AppendAsync(
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.append");
        return inner.AppendAsync(request, cancellationToken);
    }

    public ValueTask<CortexRetainedDocument?> FindHeadAsync(
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.head");
        return inner.FindHeadAsync(projectId, kind, documentId, cancellationToken);
    }

    public ValueTask<CortexRetainedDocument?> FindRevisionAsync(
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.revision");
        return inner.FindRevisionAsync(
            projectId,
            kind,
            documentId,
            revision,
            cancellationToken);
    }

    public ValueTask<CortexRetentionHeadPage> ListHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.heads");
        return inner.ListHeadsByKindAsync(
            projectId,
            kind,
            afterDocumentId,
            limit,
            cancellationToken);
    }

    public ValueTask<CortexRetentionHeadPage> ListAllHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.all-heads");
        return inner.ListAllHeadsByKindAsync(
            projectId,
            kind,
            afterDocumentId,
            limit,
            cancellationToken);
    }

    public async IAsyncEnumerable<CortexRetainedDocument> ReadRevisionRangeAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.range");
        await foreach (var item in inner.ReadRevisionRangeAsync(
                           projectId,
                           kind,
                           documentId,
                           firstRevision,
                           lastRevision,
                           cancellationToken).ConfigureAwait(false))
        {
            ensureAdmission("cortex.postgresql.retention.range");
            yield return item;
        }
    }

    public ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsAsync(
        string projectId,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.retention.search");
        return inner.SearchHeadsAsync(projectId, text, limit, cancellationToken);
    }
}

internal sealed class CortexPostgreSqlProjectBriefAdmissionDecorator(
    ICortexProjectBriefHistoryReadModel inner,
    Action<string> ensureAdmission) : ICortexProjectBriefHistoryReadModel
{
    public ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.project-brief.current");
        return inner.FindCurrentAsync(scope, cancellationToken);
    }

    public ValueTask<CortexProjectBriefSnapshot?> FindAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.project-brief.read");
        return inner.FindAsync(scope, contextSetId, cancellationToken);
    }

    public ValueTask<CortexProjectBriefSnapshot?> FindBySourceAsync(
        CortexProjectBriefReadScope scope,
        long stateRevision,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureAdmission("cortex.postgresql.project-brief.history.read");
        return inner.FindBySourceAsync(scope, stateRevision, sourceHash, cancellationToken);
    }
}
