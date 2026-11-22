using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;
using Pegasus.Cortex.Service.Persistence;

namespace Pegasus.Cortex.Mcp.Integrated.Runtime;

/// <summary>
/// Adapts server-derived project-link authority to PostgreSQL passive-retention
/// scopes. Assertions remain untrusted data and never grant task, scheduling,
/// approval, or execution authority.
/// </summary>
internal sealed class CortexMcpIntegratedRetentionRuntime :
    ICortexRetainedAssertionReaderRuntime,
    ICortexRetainedAssertionWriterRuntime,
    ICortexIntegratedCurrentRecoveryRetention
{
    private readonly CortexPostgreSqlComposition _composition;
    private readonly ICortexProjectLinkRepository _projectLinks;
    private readonly ICortexProjectBriefReadModel _projectBriefReadModel;
    private readonly CortexLocalAuthority _authority;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _projectionGate = new(1, 1);

    public CortexMcpIntegratedRetentionRuntime(
        CortexPostgreSqlComposition composition,
        ICortexProjectLinkRepository projectLinks,
        ICortexProjectBriefReadModel projectBriefReadModel,
        CortexLocalAuthority authority,
        TimeProvider? timeProvider = null)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _projectLinks = projectLinks ?? throw new ArgumentNullException(nameof(projectLinks));
        _projectBriefReadModel = projectBriefReadModel ??
            throw new ArgumentNullException(nameof(projectBriefReadModel));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexRetainedAssertionWriteResult?> SubmitTaskSnapshotAsync(
        string projectLinkId,
        string projectNamespace,
        AgentTaskSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var link = await ResolveAsync(
            projectLinkId,
            snapshot.ProjectId,
            projectNamespace,
            CortexLocalCapability.RetainedWrite,
            cancellationToken).ConfigureAwait(false);
        if (link is null ||
            snapshot.ProjectLinkId is not null &&
            !string.Equals(snapshot.ProjectLinkId, projectLinkId, StringComparison.Ordinal))
        {
            return null;
        }

        await _projectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repository = Repository(projectNamespace, write: true);
            var retention = new CortexAgentRetentionApplicationService(repository);
            var canonical = CortexCanonicalJson.Serialize(snapshot);
            var result = await retention.SubmitTaskSnapshotAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
            var write = CaptureWriteResult(
                result,
                projectLinkId,
                snapshot.ProjectId,
                projectNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                snapshot.TaskSetId,
                snapshot.SnapshotRevision,
                snapshot.SnapshotHash,
                CortexCanonicalJson.Sha256(canonical));
            if (result.Disposition is
                CortexRetentionWriteDispositions.Conflict or
                CortexRetentionWriteDispositions.StaleBase ||
                result.Document is null)
            {
                return result.Document is null
                    ? ProjectionUnavailable(write)
                    : write;
            }

            var scope = ProjectBriefScope(link);
            var current = await _projectBriefReadModel.FindCurrentAsync(
                scope,
                cancellationToken).ConfigureAwait(false);
            if (BriefMatchesSnapshot(current, snapshot))
            {
                return write with
                {
                    ProjectBriefDisposition = CortexRetentionWriteDispositions.Idempotent,
                    ProjectBriefContextSetId = current!.ContextSetId
                };
            }
            if (current is not null && current.StateRevision > snapshot.SnapshotRevision)
            {
                return write with
                {
                    ProjectBriefDisposition =
                        CortexProjectBriefMaterializationDispositions.AlreadyAdvanced,
                    ProjectBriefContextSetId = current.ContextSetId
                };
            }

            // The bounded current-recovery producer uses no task evidence. More
            // complex evidence remains retained exactly but does not receive a
            // hot projection until every referenced assertion is independently
            // revalidated by a future generalized projector.
            if (SelectedEvidenceIds(snapshot).Count != 0)
                return ProjectionUnavailable(write);

            CortexProjectBriefSnapshot projected;
            try
            {
                var checkpoint = CortexProjectBriefReferencePolicy.CreateRetained(
                    projectLinkId,
                    snapshot.ProjectId,
                    projectNamespace,
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    snapshot.TaskSetId,
                    snapshot.SnapshotRevision,
                    result.Document.ContentHash,
                    snapshot.SnapshotHash);
                projected = CortexProjectBriefProjector.Project(
                    snapshot,
                    new CortexProjectBriefProjectionInput(
                        snapshot.ProjectId,
                        scope,
                        [checkpoint],
                        [],
                        SkippedSourceChainProof: null),
                    current);
            }
            catch (ArgumentException)
            {
                return ProjectionUnavailable(write);
            }

            var preview = await new CortexProjectBriefService(
                    new InMemoryCortexProjectBriefReadModel([projected]),
                    _timeProvider)
                .ReadAsync(
                    new CortexProjectBriefRequest(
                        projectLinkId,
                        projected.AuthoritySetHash),
                    scope,
                    cancellationToken).ConfigureAwait(false);
            if (!CortexMcpApplication.CanRepresentProjectBriefWithoutTruncation(
                    preview,
                    CortexProjectBriefContract.MaximumUtf8Bytes))
            {
                return ProjectionUnavailable(write);
            }

            var briefWrite = await _composition.SubmitProjectBriefSnapshotAsync(
                projected,
                RetentionScope(projectNamespace, write: true),
                "integrated-projector",
                $"brief_{CortexCanonicalJson.Sha256(
                    System.Text.Encoding.UTF8.GetBytes(snapshot.IdempotencyKey))}",
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            return briefWrite.Disposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent
                ? write with
                {
                    ProjectBriefDisposition = briefWrite.Disposition,
                    ProjectBriefContextSetId = projected.ContextSetId
                }
                : write with
                {
                    ProjectBriefDisposition = briefWrite.Disposition,
                    ProjectBriefContextSetId = projected.ContextSetId,
                    ProjectionIssueCode =
                        "cortex_project_brief_materialization_incomplete"
                };
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    public ValueTask<CortexRetainedAssertionWriteResult?> SubmitWorkPacketAsync(
        string projectLinkId,
        string projectNamespace,
        AgentWorkPacketV1 packet,
        CancellationToken cancellationToken) =>
        SubmitAsync(
            projectLinkId,
            packet.ProjectId,
            projectNamespace,
            packet.ProjectLinkId,
            CortexRetainedDocumentKinds.WorkPacket,
            packet.WorkId,
            packet.PacketRevision,
            packet.PacketHash,
            packet,
            static (service, value, token) => service.SubmitWorkPacketAsync(value, token),
            cancellationToken);

    public ValueTask<CortexRetainedAssertionWriteResult?>
        SubmitWorkPacketHandoffReceiptAsync(
            string projectLinkId,
            string projectId,
            string projectNamespace,
            AgentWorkPacketHandoffReceiptV1 receipt,
            CancellationToken cancellationToken) =>
        SubmitAsync(
            projectLinkId,
            projectId,
            projectNamespace,
            assertionProjectLinkId: null,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            receipt.ReceiptId,
            revision: 1,
            receipt.ReceiptHash,
            receipt,
            (service, value, token) =>
                service.SubmitWorkPacketHandoffReceiptAsync(projectId, value, token),
            cancellationToken);

    public ValueTask<CortexRetainedAssertionWriteResult?>
        SubmitWorkPacketAcceptanceReceiptAsync(
            string projectLinkId,
            string projectId,
            string projectNamespace,
            AgentWorkPacketAcceptanceReceiptV1 receipt,
            CancellationToken cancellationToken) =>
        SubmitAsync(
            projectLinkId,
            projectId,
            projectNamespace,
            assertionProjectLinkId: null,
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
            receipt.ReceiptId,
            revision: 1,
            receipt.ReceiptHash,
            receipt,
            (service, value, token) =>
                service.SubmitWorkPacketAcceptanceReceiptAsync(projectId, value, token),
            cancellationToken);

    public async ValueTask<CortexRetainedAssertionRecord?> ReadRetainedAssertionAsync(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var link = await ResolveAsync(
            projectLinkId,
            projectId,
            projectNamespace,
            CortexLocalCapability.RetainedRead,
            cancellationToken).ConfigureAwait(false);
        if (link is null || kind is not (
                CortexRetainedDocumentKinds.TaskSnapshot or
                CortexRetainedDocumentKinds.WorkPacket or
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt or
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt or
                CortexRetainedDocumentKinds.ProjectBrief))
        {
            return null;
        }

        var repository = Repository(projectNamespace, write: false);
        var repositoryProjectId = kind == CortexRetainedDocumentKinds.ProjectBrief
            ? projectLinkId
            : projectId;
        var retained = await repository.FindRevisionAsync(
            repositoryProjectId,
            kind,
            documentId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (retained is null ||
            !string.Equals(retained.ContentHash, contentHash, StringComparison.Ordinal) ||
            !TryAssertionHash(retained, out var assertionHash))
        {
            return null;
        }

        var head = await repository.FindHeadAsync(
            repositoryProjectId,
            kind,
            documentId,
            cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;
        return new CortexRetainedAssertionRecord(
            _authority.TenantId,
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            assertionHash!,
            retained.ContentHash,
            head.ContentHash,
            retained.ContractId,
            retained.CanonicalUtf8.ToArray());
    }

    private async ValueTask<CortexRetainedAssertionWriteResult?> SubmitAsync<T>(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string? assertionProjectLinkId,
        string kind,
        string documentId,
        long revision,
        string assertionHash,
        T assertion,
        Func<CortexAgentRetentionApplicationService, T, CancellationToken,
            ValueTask<CortexRetentionAppendResult>> submit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        cancellationToken.ThrowIfCancellationRequested();
        var link = await ResolveAsync(
            projectLinkId,
            projectId,
            projectNamespace,
            CortexLocalCapability.RetainedWrite,
            cancellationToken).ConfigureAwait(false);
        if (link is null ||
            assertionProjectLinkId is not null &&
            !string.Equals(assertionProjectLinkId, projectLinkId, StringComparison.Ordinal))
        {
            return null;
        }

        var canonical = CortexCanonicalJson.Serialize(assertion);
        var result = await submit(
            new CortexAgentRetentionApplicationService(
                Repository(projectNamespace, write: true)),
            assertion,
            cancellationToken).ConfigureAwait(false);
        return CaptureWriteResult(
            result,
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            assertionHash,
            CortexCanonicalJson.Sha256(canonical));
    }

    private async ValueTask<CortexProjectLinkV1?> ResolveAsync(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        CortexLocalCapability capability,
        CancellationToken cancellationToken)
    {
        if (!_authority.HasCapability(capability))
            return null;
        var link = await _projectLinks.FindAsync(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
            projectLinkId,
            cancellationToken).ConfigureAwait(false);
        return link is not null &&
               string.Equals(link.Metadata.EndpointId, _authority.EndpointId, StringComparison.Ordinal) &&
               string.Equals(link.Metadata.ProjectId, projectId, StringComparison.Ordinal) &&
               string.Equals(link.Metadata.ProjectNamespace, projectNamespace, StringComparison.Ordinal)
            ? link
            : null;
    }

    private ICortexAgentRetentionRepository Repository(
        string projectNamespace,
        bool write) =>
        _composition.CreateAgentRetentionRepository(
            RetentionScope(projectNamespace, write),
            _timeProvider);

    private PostgreSqlCortexRetentionScope RetentionScope(
        string projectNamespace,
        bool write) =>
        new(
            _authority.TenantId,
            projectNamespace,
            "local-cortex-policy",
            _authority.SubjectId,
            "pegasus-cortex-mcp",
            _authority.PolicyRevision,
            write
                ? PostgreSqlCortexRetentionCapability.Write
                : PostgreSqlCortexRetentionCapability.Read);

    private CortexProjectBriefReadScope ProjectBriefScope(CortexProjectLinkV1 link) =>
        new(
            _authority.TenantId,
            link.Metadata.ProjectLinkId,
            link.Metadata.ProjectId,
            link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            _authority.SubjectId,
            "pegasus-cortex-mcp",
            _authority.PolicyRevision);

    private static CortexRetainedAssertionWriteResult CaptureWriteResult(
        CortexRetentionAppendResult result,
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string assertionHash,
        string submittedContentHash) =>
        new(
            result.Disposition,
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            assertionHash,
            submittedContentHash,
            result.Disposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent
                ? result.Document?.ContentHash
                : null,
            result.CurrentHeadHash);

    private static CortexRetainedAssertionWriteResult ProjectionUnavailable(
        CortexRetainedAssertionWriteResult result) =>
        result with
        {
            ProjectBriefDisposition =
                CortexProjectBriefMaterializationDispositions.Unavailable,
            ProjectionIssueCode =
                "cortex_project_brief_materialization_incomplete"
        };

    private static bool BriefMatchesSnapshot(
        CortexProjectBriefSnapshot? brief,
        AgentTaskSnapshotV1 snapshot) =>
        brief is not null &&
        brief.StateRevision == snapshot.SnapshotRevision &&
        string.Equals(brief.ProjectId, snapshot.ProjectId, StringComparison.Ordinal) &&
        string.Equals(brief.SourceHash, snapshot.SnapshotHash, StringComparison.Ordinal) &&
        string.Equals(brief.CurrentTask.Source.SourceId, snapshot.TaskSetId, StringComparison.Ordinal);

    private static IReadOnlySet<string> SelectedEvidenceIds(
        AgentTaskSnapshotV1 snapshot)
    {
        var ordered = snapshot.Tasks.OrderBy(task => task.Ordinal).ToArray();
        if (ordered.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var current = ordered.FirstOrDefault(task =>
                task.ReportedState == AgentTaskStateIds.Active) ??
            ordered.FirstOrDefault(task =>
                task.ReportedState is not (
                    AgentTaskStateIds.Done or AgentTaskStateIds.Cancelled)) ??
            ordered[0];
        return ordered
            .Where(task =>
                ReferenceEquals(task, current) ||
                task.ReportedState is not (
                    AgentTaskStateIds.Done or AgentTaskStateIds.Cancelled))
            .Take(CortexProjectBriefContract.MaximumHotTasks + 1)
            .SelectMany(task => task.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool TryAssertionHash(
        CortexRetainedDocument retained,
        out string? assertionHash)
    {
        assertionHash = null;
        if (retained.CanonicalUtf8 is null ||
            !string.Equals(
                retained.ContentHash,
                CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            assertionHash = retained.Kind switch
            {
                CortexRetainedDocumentKinds.TaskSnapshot =>
                    CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                        retained.CanonicalUtf8).SnapshotHash,
                CortexRetainedDocumentKinds.WorkPacket =>
                    CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                        retained.CanonicalUtf8).PacketHash,
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt =>
                    CortexCanonicalJson.Deserialize<AgentWorkPacketHandoffReceiptV1>(
                        retained.CanonicalUtf8).ReceiptHash,
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt =>
                    CortexCanonicalJson.Deserialize<AgentWorkPacketAcceptanceReceiptV1>(
                        retained.CanonicalUtf8).ReceiptHash,
                CortexRetainedDocumentKinds.ProjectBrief =>
                    CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                        retained.CanonicalUtf8).ContextSetId,
                _ => null
            };
            return assertionHash is not null;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            assertionHash = null;
            return false;
        }
    }
}
