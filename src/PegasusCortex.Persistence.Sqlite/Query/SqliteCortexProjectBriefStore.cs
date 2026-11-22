using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Persistence.Sqlite.Query;

/// <summary>
/// SQLite-backed passive project-brief read model and narrow snapshot writer.
/// The canonical snapshot bytes are identical to a file-backed producer using
/// CortexCanonicalJson; persistence adds no prompt- or storage-specific fields.
/// </summary>
public sealed class SqliteCortexProjectBriefStore :
    ICortexProjectBriefReadModel
{
    private const string LocalAuthorityId = "local-sqlite-policy";
    private const string LocalSubjectId = "local-os-user";
    private const string LocalServiceId = "pegasus-cortex-local";
    private readonly ICortexAgentRetentionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SqliteCortexProjectBriefStore(
        ICortexAgentRetentionRepository repository,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexRetentionAppendResult> SubmitSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        string producerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireValidCurrentSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);

        var canonical = CortexCanonicalJson.Serialize(snapshot);
        if (canonical.Length > CortexProjectBriefContract.MaximumUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "The project brief exceeds 8 KiB.");

        var retainedRevision = await _repository.FindRevisionAsync(
            snapshot.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            snapshot.ProjectNamespace,
            snapshot.BriefRevision,
            cancellationToken).ConfigureAwait(false);
        var head = await _repository.FindHeadAsync(
            snapshot.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            snapshot.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        var current = head is null ? null : Capture(head, scope: null);

        string? expectedPreviousHash;
        if (retainedRevision is not null)
        {
            // Let the repository compare the original exact revision and
            // idempotency tuple even when a later head now exists.
            _ = Capture(retainedRevision, scope: null);
            expectedPreviousHash = retainedRevision.PreviousHash;
        }
        else if (snapshot.BriefRevision == 1)
        {
            expectedPreviousHash = null;
        }
        else if (current is not null &&
                 current.BriefRevision == snapshot.BriefRevision - 1 &&
                 string.Equals(
                     current.ContextSetId,
                     snapshot.PreviousContextSetId,
                     StringComparison.Ordinal))
        {
            expectedPreviousHash = head!.ContentHash;
        }
        else
        {
            return new CortexRetentionAppendResult(
                CortexRetentionWriteDispositions.StaleBase,
                head?.Snapshot(),
                head?.ContentHash);
        }

        var request = new CortexRetentionAppendRequest(
            snapshot.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            snapshot.ProjectNamespace,
            snapshot.BriefRevision,
            expectedPreviousHash,
            idempotencyKey,
            producerId,
            CortexProjectBriefContract.ContractId,
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            BuildSearchText(snapshot),
            snapshot.Freshness.ObservedAt,
            snapshot.Freshness.ReviewAfter!.Value);
        return await _repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var retained = await _repository.FindHeadAsync(
            scope.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            scope.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        return retained is null ? null : Capture(retained, scope);
    }

    public async ValueTask<CortexProjectBriefSnapshot?> FindAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextSetId);
        var head = await _repository.FindHeadAsync(
            scope.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            scope.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;
        var current = Capture(head, scope);
        if (string.Equals(current.ContextSetId, contextSetId, StringComparison.Ordinal))
            return current;
        if (head.Revision <= 1 ||
            !string.Equals(current.PreviousContextSetId, contextSetId, StringComparison.Ordinal))
        {
            return null;
        }

        var predecessor = await _repository.FindRevisionAsync(
            scope.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            scope.ProjectNamespace,
            head.Revision - 1,
            cancellationToken).ConfigureAwait(false);
        if (predecessor is null)
            return null;
        var captured = Capture(predecessor, scope);
        return string.Equals(captured.ContextSetId, contextSetId, StringComparison.Ordinal)
            ? captured
            : null;
    }

    private async ValueTask RequireValidCurrentSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var scope = new CortexProjectBriefReadScope(
            snapshot.TenantId,
            snapshot.ProjectLinkId,
            snapshot.ProjectId,
            snapshot.ProjectNamespace,
            LocalAuthorityId,
            LocalSubjectId,
            LocalServiceId,
            PolicyRevision: 1);
        var service = new CortexProjectBriefService(
            new InMemoryCortexProjectBriefReadModel([snapshot]),
            _timeProvider);
        var validation = await service.ReadAsync(
            new CortexProjectBriefRequest(
                snapshot.ProjectLinkId,
                snapshot.AuthoritySetHash),
            scope,
            cancellationToken).ConfigureAwait(false);
        if (!validation.Accepted)
        {
            throw new ArgumentException(
                "The project-brief snapshot failed its scope, integrity, freshness, or bound checks.",
                nameof(snapshot));
        }
    }

    private static CortexProjectBriefSnapshot Capture(
        CortexRetainedDocument retained,
        CortexProjectBriefReadScope? scope)
    {
        if (!string.Equals(
                retained.Kind,
                CortexRetainedDocumentKinds.ProjectBrief,
                StringComparison.Ordinal) ||
            !string.Equals(
                retained.ContractId,
                CortexProjectBriefContract.ContractId,
                StringComparison.Ordinal) ||
            retained.CanonicalUtf8.Length > CortexProjectBriefContract.MaximumUtf8Bytes ||
            !string.Equals(
                retained.ContentHash,
                CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The retained project brief failed its storage boundary.");
        }

        CortexProjectBriefSnapshot snapshot;
        try
        {
            snapshot = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                retained.CanonicalUtf8);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException("The retained project brief is not canonical contract data.", exception);
        }
        var canonical = CortexCanonicalJson.Serialize(snapshot);
        if (!canonical.AsSpan().SequenceEqual(retained.CanonicalUtf8) ||
            snapshot.BriefRevision != retained.Revision ||
            !string.Equals(snapshot.ProjectLinkId, retained.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ProjectNamespace, retained.DocumentId, StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.ContextSetId,
                CortexProjectBriefService.ComputeContextSetId(snapshot),
                StringComparison.Ordinal) ||
            scope is not null &&
            (!string.Equals(snapshot.TenantId, scope.TenantId, StringComparison.Ordinal) ||
             !string.Equals(snapshot.ProjectLinkId, scope.ProjectLinkId, StringComparison.Ordinal) ||
             !string.Equals(snapshot.ProjectId, scope.ProjectId, StringComparison.Ordinal) ||
             !string.Equals(snapshot.ProjectNamespace, scope.ProjectNamespace, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The retained project brief identity or canonical bytes do not match.");
        }
        return snapshot;
    }

    private static string BuildSearchText(CortexProjectBriefSnapshot snapshot)
    {
        var values = new List<string>(32)
        {
            snapshot.CurrentTask.Title,
            snapshot.CurrentTask.State,
            snapshot.CurrentTask.CurrentState,
            snapshot.CurrentTask.NextAction
        };
        AddGate(values, snapshot.CurrentTask.Gate);
        if (snapshot.CurrentTask.Blocker is not null)
            values.Add(snapshot.CurrentTask.Blocker);
        foreach (var task in snapshot.HotTasks)
        {
            values.Add(task.Title);
            values.Add(task.State);
            values.Add(task.CurrentState);
            values.Add(task.NextAction);
            AddGate(values, task.Gate);
            if (task.Blocker is not null)
                values.Add(task.Blocker);
        }
        var text = string.Join('\n', values);
        return text.Length <= CortexAgentRetentionLimits.MaximumSearchTextCharacters
            ? text
            : text[..CortexAgentRetentionLimits.MaximumSearchTextCharacters];
    }

    private static void AddGate(
        ICollection<string> values,
        CortexProjectBriefGate gate)
    {
        values.Add(gate.State);
        values.Add(gate.Summary);
        if (gate.AuthorityId is not null)
            values.Add(gate.AuthorityId);
        if (gate.ApprovalReferenceId is not null)
            values.Add(gate.ApprovalReferenceId);
    }
}
