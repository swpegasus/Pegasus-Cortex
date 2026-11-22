using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;

namespace Pegasus.Cortex.Mcp.Integrated.Recovery;

internal sealed record CortexIntegratedTransferBriefReadback(
    bool Verified,
    string ContextSetId,
    string SourceHash,
    string AuthoritySetHash,
    long StateRevision,
    string TaskId);

/// <summary>
/// Repairs the derived hot brief by replaying the already-imported latest task
/// snapshot through the integrated projector, then reads the persisted brief
/// back and compares every current-recovery field with the authoritative task.
/// </summary>
internal sealed class CortexIntegratedTransferBriefRegenerator :
    ICortexRetentionTransferBriefRegenerator
{
    private readonly ICortexRetainedAssertionWriterRuntime _writer;
    private readonly ICortexProjectBriefReadModel _briefs;
    private readonly CortexProjectBriefReadScope _scope;

    public CortexIntegratedTransferBriefRegenerator(
        ICortexRetainedAssertionWriterRuntime writer,
        ICortexProjectBriefReadModel briefs,
        CortexProjectBriefReadScope scope)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _briefs = briefs ?? throw new ArgumentNullException(nameof(briefs));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public CortexIntegratedTransferBriefReadback? LastReadback { get; private set; }

    public async ValueTask<CortexRetentionTransferBriefRegenerationResult> RegenerateAsync(
        CortexRetentionTransferProjectScope project,
        AgentTaskSnapshotV1 authoritativeTaskSnapshot,
        IReadOnlyList<CortexRetentionTransferDocument> comparisonEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(authoritativeTaskSnapshot);
        ArgumentNullException.ThrowIfNull(comparisonEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        LastReadback = null;

        if (!MatchesScope(project, authoritativeTaskSnapshot))
            return Reject("transfer_brief_scope_mismatch");

        var write = await _writer.SubmitTaskSnapshotAsync(
                project.ProjectLinkId,
                project.ProjectNamespace,
                authoritativeTaskSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (write is null ||
            write.Disposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent) ||
            write.ProjectBriefDisposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent) ||
            write.RetainedContentHash is null ||
            write.ProjectBriefContextSetId is null ||
            write.ProjectionIssueCode is not null)
        {
            return Reject("transfer_brief_projection_rejected");
        }

        var brief = await _briefs.FindCurrentAsync(_scope, cancellationToken)
            .ConfigureAwait(false);
        if (brief is null || !await CortexIntegratedCurrentRecoveryBriefVerifier.MatchesAsync(
                _briefs,
                brief,
                _scope,
                authoritativeTaskSnapshot,
                project.AuthoritySetHash,
                write.RetainedContentHash,
                cancellationToken).ConfigureAwait(false))
        {
            return Reject("transfer_brief_readback_mismatch");
        }

        var matchingEvidenceHash = comparisonEvidence
            .FirstOrDefault(item =>
                string.Equals(
                    item.ContentHash,
                    CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(brief)),
                    StringComparison.Ordinal))
            ?.ContentHash;
        LastReadback = new CortexIntegratedTransferBriefReadback(
            Verified: true,
            brief.ContextSetId,
            brief.SourceHash,
            brief.AuthoritySetHash,
            brief.StateRevision,
            brief.CurrentTask.TaskId);
        return new CortexRetentionTransferBriefRegenerationResult(
            Accepted: true,
            write.ProjectBriefDisposition,
            brief.ContextSetId,
            brief.SourceHash,
            brief.AuthoritySetHash,
            matchingEvidenceHash,
            DiagnosticCode: null);
    }

    private bool MatchesScope(
        CortexRetentionTransferProjectScope project,
        AgentTaskSnapshotV1 source) =>
        string.Equals(project.TenantId, _scope.TenantId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectLinkId, _scope.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectId, _scope.ProjectId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectNamespace, _scope.ProjectNamespace, StringComparison.Ordinal) &&
        string.Equals(source.ProjectId, project.ProjectId, StringComparison.Ordinal) &&
        string.Equals(source.ProjectLinkId, project.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(
            CortexProjectBriefService.ComputeAuthoritySetHash(source.SourceRevisions),
            project.AuthoritySetHash,
            StringComparison.Ordinal);

    private static CortexRetentionTransferBriefRegenerationResult Reject(string code) =>
        new(
            Accepted: false,
            CortexRetentionTransferImportDispositions.BriefRegenerationFailed,
            ContextSetId: null,
            SourceHash: null,
            AuthoritySetHash: null,
            MatchingEvidenceContentHash: null,
            DiagnosticCode: code);
}
