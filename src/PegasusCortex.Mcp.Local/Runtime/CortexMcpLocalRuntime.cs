using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>
/// Standalone adapter from exact registry authority to the provider-neutral MCP
/// ports. Registered roots are identity metadata only and are never opened.
/// </summary>
internal sealed class CortexMcpLocalRuntime :
    ICortexProjectBriefRuntime,
    ICortexRetainedAssertionReaderRuntime,
    ICortexRetainedAssertionWriterRuntime
{
    private const string LocalAuthorityId = "local-os-policy";
    private const string LocalServiceId = "pegasus-cortex-local";

    private readonly CortexLocalProjectRegistry _registry;
    private readonly ICortexAgentRetentionRepository _repository;
    private readonly CortexAgentRetentionApplicationService _retention;
    private readonly SqliteCortexProjectBriefStore _projectBriefStore;
    private readonly CortexProjectBriefService _projectBrief;
    private readonly SemaphoreSlim _taskProjectionGate = new(1, 1);
    private readonly string _localSubjectId;
    private readonly TimeProvider _timeProvider;

    public CortexMcpLocalRuntime(
        CortexLocalProjectRegistry registry,
        ICortexAgentRetentionRepository repository,
        SqliteCortexProjectBriefStore projectBriefStore,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _retention = new CortexAgentRetentionApplicationService(repository);
        _projectBriefStore =
            projectBriefStore ?? throw new ArgumentNullException(nameof(projectBriefStore));
        _projectBrief = new CortexProjectBriefService(
            projectBriefStore,
            timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localSubjectId = $"os_{CortexLocalOsIdentity.Capture().Hash}";
    }

    public async ValueTask<CortexRetainedAssertionWriteResult?>
        SubmitTaskSnapshotAsync(
        string projectLinkId,
        string projectNamespace,
        AgentTaskSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeWrite(
                projectLinkId,
                snapshot.ProjectId,
                projectNamespace,
                out var registration) ||
            snapshot.ProjectLinkId is not null &&
            !string.Equals(
                snapshot.ProjectLinkId,
                projectLinkId,
                StringComparison.Ordinal) ||
            !IsStandalone(snapshot))
        {
            return null;
        }

        // Serialize retention and derived-view repair within this runtime.
        // Retention remains primary: a valid immutable assertion is never
        // rejected merely because the bounded hot brief cannot represent it.
        await _taskProjectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var canonical = CortexCanonicalJson.Serialize(snapshot);
            var submittedContentHash = CortexCanonicalJson.Sha256(canonical);
            var scope = CreateScope(registration!);
            var retentionResult = await _retention.SubmitTaskSnapshotAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
            var writeResult = CaptureWriteResult(
                retentionResult,
                projectLinkId,
                projectNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                snapshot.ProjectId,
                snapshot.TaskSetId,
                snapshot.SnapshotRevision,
                snapshot.SnapshotHash,
                submittedContentHash);
            if (retentionResult.Disposition is
                CortexRetentionWriteDispositions.Conflict or
                CortexRetentionWriteDispositions.StaleBase)
            {
                return writeResult;
            }
            if (retentionResult.Document is null)
                return ProjectionIncomplete(writeResult);

            var currentBrief = await _projectBriefStore.FindCurrentAsync(
                scope,
                cancellationToken).ConfigureAwait(false);
            if (BriefMatchesSnapshot(currentBrief, snapshot))
            {
                return writeResult with
                {
                    ProjectBriefDisposition =
                        CortexRetentionWriteDispositions.Idempotent,
                    ProjectBriefContextSetId = currentBrief!.ContextSetId
                };
            }
            if (currentBrief is not null &&
                currentBrief.StateRevision > snapshot.SnapshotRevision)
            {
                return await CompleteAdvancedReplayAsync(
                    writeResult,
                    currentBrief,
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            CortexProjectBriefSnapshot projected;
            try
            {
                var projectionInput = await ProjectionInputAsync(
                    snapshot,
                    scope,
                    retentionResult.Document,
                    currentBrief,
                    cancellationToken).ConfigureAwait(false);
                if (projectionInput is null)
                    return ProjectionIncomplete(writeResult);
                projected = CortexProjectBriefProjector.Project(
                    snapshot,
                    projectionInput,
                    currentBrief);
            }
            catch (ArgumentException)
            {
                return ProjectionUnrepresentable(writeResult);
            }

            var preview = await new CortexProjectBriefService(
                    new InMemoryCortexProjectBriefReadModel([projected]),
                    _timeProvider)
                .ReadAsync(
                    new CortexProjectBriefRequest(
                        projected.ProjectLinkId,
                        projected.AuthoritySetHash),
                    scope,
                    cancellationToken).ConfigureAwait(false);
            if (!CortexMcpApplication.CanRepresentProjectBriefWithoutTruncation(
                    preview,
                    CortexProjectBriefContract.MaximumUtf8Bytes))
            {
                return ProjectionUnrepresentable(writeResult);
            }

            CortexRetentionAppendResult briefWrite;
            try
            {
                briefWrite = await _projectBriefStore.SubmitSnapshotAsync(
                    projected,
                    "local-projector",
                    $"brief_{CortexCanonicalJson.Sha256(snapshot.IdempotencyKey)}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return ProjectionUnrepresentable(writeResult);
            }
            return briefWrite.Disposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent
                ? writeResult with
                {
                    ProjectBriefDisposition = briefWrite.Disposition,
                    ProjectBriefContextSetId = projected.ContextSetId
                }
                : writeResult with
                {
                    ProjectBriefDisposition = briefWrite.Disposition,
                    ProjectBriefContextSetId = projected.ContextSetId,
                    ProjectionIssueCode =
                        "cortex_project_brief_materialization_incomplete"
                };
        }
        finally
        {
            _taskProjectionGate.Release();
        }
    }

    public ValueTask<CortexRetainedAssertionWriteResult?> SubmitWorkPacketAsync(
        string projectLinkId,
        string projectNamespace,
        AgentWorkPacketV1 packet,
        CancellationToken cancellationToken) =>
        SubmitAsync(
            projectLinkId,
            projectNamespace,
            packet,
            CortexRetainedDocumentKinds.WorkPacket,
            packet.ProjectId,
            packet.ProjectLinkId,
            packet.WorkId,
            packet.PacketRevision,
            packet.PacketHash,
            requireStandalone: true,
            _retention.SubmitWorkPacketAsync,
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
            projectNamespace,
            receipt,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            projectId,
            assertionProjectLinkId: null,
            receipt.ReceiptId,
            revision: 1,
            receipt.ReceiptHash,
            requireStandalone: false,
            (value, token) =>
                _retention.SubmitWorkPacketHandoffReceiptAsync(
                    projectId,
                    value,
                    token),
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
            projectNamespace,
            receipt,
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
            projectId,
            assertionProjectLinkId: null,
            receipt.ReceiptId,
            revision: 1,
            receipt.ReceiptHash,
            requireStandalone: false,
            (value, token) =>
                _retention.SubmitWorkPacketAcceptanceReceiptAsync(
                    projectId,
                    value,
                    token),
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
        if (!_registry.TryAuthorizeRead(
                projectLinkId,
                projectId,
                projectNamespace,
                out var registration) ||
            registration is null ||
            kind is not (
                CortexRetainedDocumentKinds.TaskSnapshot or
                CortexRetainedDocumentKinds.WorkPacket or
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt or
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt or
                CortexRetainedDocumentKinds.ProjectBrief))
        {
            return null;
        }

        var repositoryProjectId =
            string.Equals(
                kind,
                CortexRetainedDocumentKinds.ProjectBrief,
                StringComparison.Ordinal)
                ? projectLinkId
                : projectId;
        var retained = await _repository.FindRevisionAsync(
            repositoryProjectId,
            kind,
            documentId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (retained is null ||
            !string.Equals(retained.ContentHash, contentHash, StringComparison.Ordinal) ||
            !TryValidateRetainedDocument(
                retained,
                registration.TenantId,
                projectLinkId,
                projectId,
                projectNamespace,
                out var assertionHash))
        {
            return null;
        }

        var head = await _repository.FindHeadAsync(
            repositoryProjectId,
            kind,
            documentId,
            cancellationToken).ConfigureAwait(false);
        if (head is null)
            throw new InvalidDataException("A retained revision has no corresponding head.");

        return new CortexRetainedAssertionRecord(
            registration.TenantId,
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

    public async ValueTask<CortexProjectBriefResult?> ReadProjectBriefAsync(
        string projectLinkId,
        string? knownContextSetId,
        string knownAuthoritySetHash,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeProjectBriefRead(
                projectLinkId,
                out var registration))
        {
            return null;
        }

        var scope = CreateScope(registration!);
        var result = await _projectBrief.ReadAsync(
            new CortexProjectBriefRequest(
                projectLinkId,
                knownAuthoritySetHash,
                knownContextSetId,
                maximumUtf8Bytes),
            scope,
            cancellationToken).ConfigureAwait(false);
        if (!result.Accepted || result.Response.FallbackRequired)
            return result;

        // The brief store is derived state. Re-read its exact current snapshot
        // and independently bind it to the sole retained task-set head before
        // returning any snapshot, delta, or unchanged response.
        var currentBrief = await _projectBriefStore.FindCurrentAsync(
            scope,
            cancellationToken).ConfigureAwait(false);
        if (currentBrief is null ||
            !string.Equals(
                currentBrief.ContextSetId,
                result.Response.ContextSetId,
                StringComparison.Ordinal) ||
            !await MatchesSoleRetainedTaskHeadAsync(
                currentBrief,
                scope,
                cancellationToken).ConfigureAwait(false))
        {
            return TaskHeadMismatchFallback();
        }
        return result;
    }

    private async ValueTask<bool> MatchesSoleRetainedTaskHeadAsync(
        CortexProjectBriefSnapshot brief,
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        var heads = await _repository.ListAllHeadsByKindAsync(
            scope.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            afterDocumentId: null,
            limit: 2,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (heads.Items.Count != 1 || heads.NextAfterDocumentId is not null)
            return false;

        var head = heads.Items[0];
        var sourceId = brief.CurrentTask.Source.SourceId;
        if (!string.Equals(
                head.Kind,
                CortexRetainedDocumentKinds.TaskSnapshot,
                StringComparison.Ordinal) ||
            !string.Equals(head.ProjectId, scope.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(head.DocumentId, sourceId, StringComparison.Ordinal) ||
            head.Revision != brief.StateRevision ||
            !TryValidateRetainedDocument(
                head,
                scope.TenantId,
                scope.ProjectLinkId,
                scope.ProjectId,
                scope.ProjectNamespace,
                out var assertionHash) ||
            !string.Equals(assertionHash, brief.SourceHash, StringComparison.Ordinal))
        {
            return false;
        }

        // Every task in one projected brief must bind the same retained source
        // snapshot. This prevents a forged mixed-source brief from selecting a
        // convenient head.
        return brief.HotTasks
            .Prepend(brief.CurrentTask)
            .All(task =>
                string.Equals(
                    task.Source.SourceId,
                    sourceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    task.Source.SourceHash,
                    brief.SourceHash,
                    StringComparison.Ordinal));
    }

    private static CortexProjectBriefResult TaskHeadMismatchFallback()
    {
        var response = new CortexProjectBriefResponse(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            CortexProjectBriefDisposition.Snapshot,
            ProjectLinkId: null,
            ProjectId: null,
            ProjectNamespace: null,
            ContextSetId: null,
            BaseContextSetId: null,
            StateRevision: null,
            SourceHash: null,
            SelectionVersion: null,
            CurrentTask: null,
            HotTasks: [],
            RemovedTaskIds: [],
            HasMore: false,
            CheckpointReferences: [],
            EvidenceReferences: [],
            Freshness: null,
            Complete: false,
            Truncated: false,
            FallbackRequired: true,
            FallbackReason: "retained_task_head_mismatch",
            CortexProjectBriefContract.FallbackRecoveryOrder,
            AuthoritySetHash: null);
        var diagnostic = new PegasusDiagnostic(
            "cortex_project_brief_source_head_mismatch",
            "The retained task-set head does not match the current derived project brief.",
            DiagnosticSeverity.Error,
            "cortex.project-brief.read",
            retryable: false,
            context: null,
            cause: null);
        return new CortexProjectBriefResult(false, response, [diagnostic]);
    }

    private async ValueTask<CortexRetainedAssertionWriteResult?> SubmitAsync<T>(
        string projectLinkId,
        string projectNamespace,
        T assertion,
        string kind,
        string projectId,
        string? assertionProjectLinkId,
        string documentId,
        long revision,
        string assertionHash,
        bool requireStandalone,
        Func<T, CancellationToken, ValueTask<CortexRetentionAppendResult>> submit,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeWrite(
                projectLinkId,
                projectId,
                projectNamespace,
                out _) ||
            assertionProjectLinkId is not null &&
            !string.Equals(
                assertionProjectLinkId,
                projectLinkId,
                StringComparison.Ordinal) ||
            requireStandalone && !IsStandalone(assertion))
        {
            return null;
        }

        var canonical = CortexCanonicalJson.Serialize(assertion);
        var submittedContentHash = CortexCanonicalJson.Sha256(canonical);
        var result = await submit(assertion, cancellationToken).ConfigureAwait(false);
        var retainedContentHash = result.Disposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent
            ? result.Document?.ContentHash
            : null;
        return new CortexRetainedAssertionWriteResult(
            result.Disposition,
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            assertionHash,
            submittedContentHash,
            retainedContentHash,
            result.CurrentHeadHash);
    }

    private async ValueTask<CortexRetainedAssertionWriteResult>
        CompleteAdvancedReplayAsync(
            CortexRetainedAssertionWriteResult writeResult,
            CortexProjectBriefSnapshot currentBrief,
            AgentTaskSnapshotV1 replayed,
            CancellationToken cancellationToken)
    {
        var taskHead = await _repository.FindHeadAsync(
            replayed.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            replayed.TaskSetId,
            cancellationToken).ConfigureAwait(false);
        if (taskHead is null)
            return ProjectionIncomplete(writeResult);

        try
        {
            var headSnapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                taskHead.CanonicalUtf8);
            if (!AgentTaskSnapshotPolicy.HasValidHash(headSnapshot) ||
                !taskHead.CanonicalUtf8.AsSpan().SequenceEqual(
                    CortexCanonicalJson.Serialize(headSnapshot)) ||
                taskHead.Revision != headSnapshot.SnapshotRevision ||
                !string.Equals(
                    taskHead.ContentHash,
                    CortexCanonicalJson.Sha256(taskHead.CanonicalUtf8),
                    StringComparison.Ordinal) ||
                !BriefMatchesSnapshot(currentBrief, headSnapshot))
            {
                return ProjectionIncomplete(writeResult);
            }
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            return ProjectionIncomplete(writeResult);
        }

        return writeResult with
        {
            ProjectBriefDisposition =
                CortexProjectBriefMaterializationDispositions.AlreadyAdvanced,
            ProjectBriefContextSetId = currentBrief.ContextSetId
        };
    }

    private static CortexRetainedAssertionWriteResult ProjectionUnrepresentable(
        CortexRetainedAssertionWriteResult writeResult) =>
        writeResult with
        {
            ProjectBriefDisposition =
                CortexProjectBriefMaterializationDispositions.Unavailable,
            ProjectionIssueCode =
                "cortex_project_brief_projection_unrepresentable"
        };

    private static CortexRetainedAssertionWriteResult ProjectionIncomplete(
        CortexRetainedAssertionWriteResult writeResult) =>
        writeResult with
        {
            ProjectBriefDisposition =
                CortexProjectBriefMaterializationDispositions.Unavailable,
            ProjectionIssueCode =
                "cortex_project_brief_materialization_incomplete"
        };

    private async ValueTask<CortexProjectBriefProjectionInput?>
        ProjectionInputAsync(
        AgentTaskSnapshotV1 snapshot,
        CortexProjectBriefReadScope scope,
        CortexRetainedDocument retainedSnapshot,
        CortexProjectBriefSnapshot? previousBrief,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retainedSnapshot);
        var skippedSource = await ReadSkippedSourceChainAsync(
            snapshot,
            scope,
            previousBrief,
            cancellationToken).ConfigureAwait(false);
        if (!skippedSource.Complete)
            return null;
        var checkpoint = CortexProjectBriefReferencePolicy.CreateRetained(
            scope.ProjectLinkId,
            snapshot.ProjectId,
            scope.ProjectNamespace,
            CortexRetainedDocumentKinds.TaskSnapshot,
            snapshot.TaskSetId,
            snapshot.SnapshotRevision,
            retainedSnapshot.ContentHash,
            snapshot.SnapshotHash);
        var selectedEvidenceIds = SelectedEvidenceIds(snapshot);
        var evidence = new List<CortexProjectBriefProjectionEvidence>(
            selectedEvidenceIds.Count);
        foreach (var declared in snapshot.EvidenceReferences.Where(reference =>
                     selectedEvidenceIds.Contains(reference.EvidenceId)))
        {
            var retained = await _repository.FindRevisionAsync(
                snapshot.ProjectId,
                declared.Kind,
                declared.DocumentId,
                declared.Revision,
                cancellationToken).ConfigureAwait(false);
            if (retained is null)
                return null;
            if (!string.Equals(
                    retained.ContentHash,
                    declared.ContentHash,
                    StringComparison.Ordinal) ||
                !TryValidateRetainedDocument(
                    retained,
                    scope.TenantId,
                    scope.ProjectLinkId,
                    snapshot.ProjectId,
                    scope.ProjectNamespace,
                    out var assertionHash) ||
                !string.Equals(
                    assertionHash,
                    declared.AssertionHash,
                    StringComparison.Ordinal) ||
                !await ValidateEvidenceLinkAsync(
                    snapshot,
                    scope,
                    retained,
                    declared,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new ArgumentException(
                    "A declared evidence locator does not match exact typed retained bytes.");
            }
            evidence.Add(new CortexProjectBriefProjectionEvidence(
                declared.EvidenceId,
                CortexProjectBriefReferencePolicy.CreateRetained(
                    scope.ProjectLinkId,
                    snapshot.ProjectId,
                    scope.ProjectNamespace,
                    declared.Kind,
                    declared.DocumentId,
                    declared.Revision,
                    declared.ContentHash,
                    declared.AssertionHash)));
        }
        return new CortexProjectBriefProjectionInput(
            snapshot.ProjectId,
            scope,
            [checkpoint],
            Array.AsReadOnly(evidence
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray()),
            skippedSource.Proof);
    }

    private async ValueTask<(
        bool Complete,
        CortexProjectBriefSkippedSourceChainProof? Proof)>
        ReadSkippedSourceChainAsync(
        AgentTaskSnapshotV1 source,
        CortexProjectBriefReadScope scope,
        CortexProjectBriefSnapshot? previousBrief,
        CancellationToken cancellationToken)
    {
        var firstRevision = previousBrief?.StateRevision + 1 ?? 1;
        var lastRevision = source.SnapshotRevision - 1;
        if (lastRevision < firstRevision)
            return (true, null);
        var revisionCount = lastRevision - firstRevision + 1;
        if (revisionCount >
            CortexProjectBriefContract.MaximumSkippedSourceRevisions)
        {
            throw new ArgumentException(
                "The retained source gap exceeds the bounded project-brief recovery window.",
                nameof(source));
        }

        var startingPreviousHash = previousBrief?.SourceHash;
        var expectedPreviousHash = startingPreviousHash;
        var expectedRevision = firstRevision;
        await foreach (var retained in _repository.ReadRevisionRangeAsync(
            source.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            source.TaskSetId,
            firstRevision,
            lastRevision,
            cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained.Revision != expectedRevision)
                return (false, null);
            if (!TryValidateRetainedDocument(
                    retained,
                    scope.TenantId,
                    scope.ProjectLinkId,
                    source.ProjectId,
                    scope.ProjectNamespace,
                    out var assertionHash))
            {
                throw new ArgumentException(
                    "A skipped task-source revision failed its exact retained boundary.",
                    nameof(source));
            }

            var snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                retained.CanonicalUtf8);
            if (!string.Equals(
                    snapshot.SnapshotHash,
                    assertionHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.PreviousSnapshotHash,
                    expectedPreviousHash,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A skipped task-source revision broke the exact retained hash chain.",
                    nameof(source));
            }
            expectedPreviousHash = snapshot.SnapshotHash;
            expectedRevision++;
        }
        if (expectedRevision != lastRevision + 1)
            return (false, null);
        if (!string.Equals(
                source.PreviousSnapshotHash,
                expectedPreviousHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current task source does not extend the exact retained skipped chain.",
                nameof(source));
        }
        return (
            true,
            new CortexProjectBriefSkippedSourceChainProof(
                firstRevision,
                lastRevision,
                checked((int)revisionCount),
                startingPreviousHash,
                expectedPreviousHash ??
                    throw new InvalidOperationException(
                        "A non-empty skipped source range had no ending hash.")));
    }

    private async ValueTask<bool> ValidateEvidenceLinkAsync(
        AgentTaskSnapshotV1 snapshot,
        CortexProjectBriefReadScope scope,
        CortexRetainedDocument retained,
        AgentTaskEvidenceReferenceV1 declared,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (declared.Kind)
            {
                case CortexRetainedDocumentKinds.WorkPacket:
                {
                    var packet = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                        retained.CanonicalUtf8);
                    if (packet.PacketRevision == 1)
                        return packet.PreviousPacketHash is null;
                    var previous = await ReadVerifiedEvidenceAsync<AgentWorkPacketV1>(
                        snapshot,
                        scope,
                        CortexRetainedDocumentKinds.WorkPacket,
                        packet.WorkId,
                        packet.PacketRevision - 1,
                        static bytes =>
                            CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                                bytes),
                        static value => value.PacketHash,
                        cancellationToken).ConfigureAwait(false);
                    return previous is not null &&
                           AgentWorkPacketPolicy.ValidateContinuation(
                               previous,
                               packet).Valid;
                }
                case CortexRetainedDocumentKinds.WorkPacketHandoffReceipt:
                {
                    var handoff = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketHandoffReceiptV1>(
                            retained.CanonicalUtf8);
                    var packet = await ReadVerifiedEvidenceAsync<AgentWorkPacketV1>(
                        snapshot,
                        scope,
                        CortexRetainedDocumentKinds.WorkPacket,
                        handoff.WorkId,
                        handoff.PacketRevision,
                        static bytes =>
                            CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                                bytes),
                        static value => value.PacketHash,
                        cancellationToken).ConfigureAwait(false);
                    return packet is not null &&
                           AgentWorkPacketPolicy.ValidateHandoff(
                               packet,
                               handoff).Valid;
                }
                case CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt:
                {
                    var acceptance = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketAcceptanceReceiptV1>(
                            retained.CanonicalUtf8);
                    var handoff = await ReadVerifiedEvidenceAsync<
                        AgentWorkPacketHandoffReceiptV1>(
                            snapshot,
                            scope,
                            CortexRetainedDocumentKinds
                                .WorkPacketHandoffReceipt,
                            acceptance.HandoffReceiptId,
                            revision: 1,
                            static bytes =>
                                CortexCanonicalJson.Deserialize<
                                    AgentWorkPacketHandoffReceiptV1>(bytes),
                            static value => value.ReceiptHash,
                            cancellationToken).ConfigureAwait(false);
                    if (handoff is null ||
                        !AgentWorkPacketPolicy.ValidateAcceptance(
                            handoff,
                            acceptance).Valid)
                    {
                        return false;
                    }
                    var packet = await ReadVerifiedEvidenceAsync<AgentWorkPacketV1>(
                        snapshot,
                        scope,
                        CortexRetainedDocumentKinds.WorkPacket,
                        handoff.WorkId,
                        handoff.PacketRevision,
                        static bytes =>
                            CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                                bytes),
                        static value => value.PacketHash,
                        cancellationToken).ConfigureAwait(false);
                    return packet is not null &&
                           AgentWorkPacketPolicy.ValidateHandoff(
                               packet,
                               handoff).Valid;
                }
                default:
                    return false;
            }
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private async ValueTask<T?> ReadVerifiedEvidenceAsync<T>(
        AgentTaskSnapshotV1 snapshot,
        CortexProjectBriefReadScope scope,
        string kind,
        string documentId,
        long revision,
        Func<byte[], T> deserialize,
        Func<T, string> assertionHash,
        CancellationToken cancellationToken)
        where T : class
    {
        var retained = await _repository.FindRevisionAsync(
            snapshot.ProjectId,
            kind,
            documentId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (retained is null ||
            !TryValidateRetainedDocument(
                retained,
                scope.TenantId,
                scope.ProjectLinkId,
                snapshot.ProjectId,
                scope.ProjectNamespace,
                out var validatedAssertionHash))
        {
            return null;
        }
        var value = deserialize(retained.CanonicalUtf8);
        return string.Equals(
            assertionHash(value),
            validatedAssertionHash,
            StringComparison.Ordinal)
            ? value
            : null;
    }

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
        var selected = ordered
            .Where(task =>
                !ReferenceEquals(task, current) &&
                task.ReportedState is not (
                    AgentTaskStateIds.Done or AgentTaskStateIds.Cancelled))
            .Take(CortexProjectBriefContract.MaximumHotTasks)
            .Prepend(current);
        return selected
            .SelectMany(task => task.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool BriefMatchesSnapshot(
        CortexProjectBriefSnapshot? brief,
        AgentTaskSnapshotV1 snapshot) =>
        brief is not null &&
        brief.StateRevision == snapshot.SnapshotRevision &&
        string.Equals(
            brief.ProjectId,
            snapshot.ProjectId,
            StringComparison.Ordinal) &&
        string.Equals(
            brief.SourceHash,
            snapshot.SnapshotHash,
            StringComparison.Ordinal) &&
        string.Equals(
            brief.CurrentTask.Source.ProducerContract,
            snapshot.ContractId,
            StringComparison.Ordinal) &&
        string.Equals(
            brief.CurrentTask.Source.SourceId,
            snapshot.TaskSetId,
            StringComparison.Ordinal) &&
        string.Equals(
            brief.CurrentTask.Source.SourceRevisionId,
            snapshot.SnapshotId,
            StringComparison.Ordinal);

    private CortexProjectBriefReadScope CreateScope(
        CortexLocalProjectRegistration registration) =>
        new(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            LocalAuthorityId,
            _localSubjectId,
            LocalServiceId,
            PolicyRevision: 1);

    private static CortexRetainedAssertionWriteResult CaptureWriteResult(
        CortexRetentionAppendResult result,
        string projectLinkId,
        string projectNamespace,
        string kind,
        string projectId,
        string documentId,
        long revision,
        string assertionHash,
        string submittedContentHash)
    {
        var retainedContentHash = result.Disposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent
            ? result.Document?.ContentHash
            : null;
        return new CortexRetainedAssertionWriteResult(
            result.Disposition,
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            assertionHash,
            submittedContentHash,
            retainedContentHash,
            result.CurrentHeadHash);
    }

    private static bool IsStandalone<T>(T assertion) =>
        assertion switch
        {
            AgentTaskSnapshotV1 snapshot =>
                string.Equals(
                    snapshot.Mode,
                    AgentCoordinationModeIds.Standalone,
                    StringComparison.Ordinal),
            AgentWorkPacketV1 packet =>
                string.Equals(
                    packet.Mode,
                    AgentCoordinationModeIds.Standalone,
                    StringComparison.Ordinal),
            _ => false
        };

    private static bool TryValidateRetainedDocument(
        CortexRetainedDocument retained,
        string tenantId,
        string projectLinkId,
        string projectId,
        string projectNamespace,
        out string? assertionHash)
    {
        assertionHash = null;
        if (retained.CanonicalUtf8 is null ||
            (retained.Kind != CortexRetainedDocumentKinds.ProjectBrief &&
             !string.Equals(
                 retained.ProjectId,
                 projectId,
                 StringComparison.Ordinal)) ||
            retained.CanonicalUtf8.Length is < 2 or
                > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes ||
            !string.Equals(
                retained.ContentHash,
                CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[] canonical;
            switch (retained.Kind)
            {
                case CortexRetainedDocumentKinds.TaskSnapshot:
                {
                    var snapshot =
                        CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                            retained.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(snapshot);
                    assertionHash = snapshot.SnapshotHash;
                    if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
                        !string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal) ||
                        !string.Equals(snapshot.TaskSetId, retained.DocumentId, StringComparison.Ordinal) ||
                        snapshot.SnapshotRevision != retained.Revision ||
                        snapshot.ProjectLinkId is not null &&
                        !string.Equals(
                            snapshot.ProjectLinkId,
                            projectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(snapshot.ContractId, retained.ContractId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacket:
                {
                    var packet =
                        CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                            retained.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(packet);
                    assertionHash = packet.PacketHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(packet) ||
                        !string.Equals(packet.ProjectId, projectId, StringComparison.Ordinal) ||
                        !string.Equals(packet.WorkId, retained.DocumentId, StringComparison.Ordinal) ||
                        packet.PacketRevision != retained.Revision ||
                        packet.ProjectLinkId is not null &&
                        !string.Equals(
                            packet.ProjectLinkId,
                            projectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(packet.ContractId, retained.ContractId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketHandoffReceipt:
                {
                    var receipt = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketHandoffReceiptV1>(
                            retained.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(receipt);
                    assertionHash = receipt.ReceiptHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(receipt) ||
                        !string.Equals(
                            receipt.ReceiptId,
                            retained.DocumentId,
                            StringComparison.Ordinal) ||
                        retained.Revision != 1 ||
                        !string.Equals(
                            receipt.ContractId,
                            AgentWorkPacketContractIds.HandoffReceipt,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            receipt.ContractId,
                            retained.ContractId,
                            StringComparison.Ordinal) ||
                        receipt.Executable ||
                        receipt.GrantsSchedulingAuthority ||
                        receipt.GrantsTaskClaim ||
                        receipt.GrantsAuthority ||
                        !string.Equals(
                            receipt.ContentTreatment,
                            AgentWorkPacketContractIds.ContentTreatment,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt:
                {
                    var receipt = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketAcceptanceReceiptV1>(
                            retained.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(receipt);
                    assertionHash = receipt.ReceiptHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(receipt) ||
                        !string.Equals(
                            receipt.ReceiptId,
                            retained.DocumentId,
                            StringComparison.Ordinal) ||
                        retained.Revision != 1 ||
                        !string.Equals(
                            receipt.ContractId,
                            AgentWorkPacketContractIds.AcceptanceReceipt,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            receipt.ContractId,
                            retained.ContractId,
                            StringComparison.Ordinal) ||
                        receipt.Executable ||
                        receipt.GrantsSchedulingAuthority ||
                        receipt.GrantsTaskClaim ||
                        receipt.GrantsAuthority ||
                        !string.Equals(
                            receipt.ContentTreatment,
                            AgentWorkPacketContractIds.ContentTreatment,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.ProjectBrief:
                {
                    var brief =
                        CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                            retained.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(brief);
                    assertionHash = brief.ContextSetId;
                    if (!string.Equals(
                            retained.ProjectId,
                            projectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.TenantId,
                            tenantId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectLinkId,
                            projectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectId,
                            projectId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectNamespace,
                            projectNamespace,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectNamespace,
                            retained.DocumentId,
                            StringComparison.Ordinal) ||
                        brief.BriefRevision != retained.Revision ||
                        !string.Equals(
                            brief.ContractId,
                            CortexProjectBriefContract.ContractId,
                            StringComparison.Ordinal) ||
                        !string.Equals(brief.ContractId, retained.ContractId, StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ContextSetId,
                            CortexProjectBriefService.ComputeContextSetId(brief),
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                default:
                    return false;
            }

            return canonical.AsSpan().SequenceEqual(retained.CanonicalUtf8);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            assertionHash = null;
            return false;
        }
    }

}
