using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Query;

/// <summary>
/// PostgreSQL-backed project-brief read model and explicit scoped snapshot
/// writer. Reads derive their retention scope only from server authority.
/// </summary>
public sealed class PostgreSqlCortexProjectBriefStore :
    ICortexProjectBriefReadModel,
    ICortexProjectBriefHistoryReadModel
{
    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly ICortexRowAuthenticator _authenticator;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlCortexProjectBriefStore(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator authenticator,
        TimeProvider? timeProvider = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexRetentionAppendResult> SubmitSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        PostgreSqlCortexRetentionScope writeScope,
        string producerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var capturedScope = (writeScope ?? throw new ArgumentNullException(nameof(writeScope)))
            .Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (capturedScope.Capability != PostgreSqlCortexRetentionCapability.Write)
            throw new UnauthorizedAccessException("Project-brief persistence requires retained-write authority.");
        if (!string.Equals(capturedScope.TenantId, snapshot.TenantId, StringComparison.Ordinal) ||
            !string.Equals(capturedScope.Namespace, snapshot.ProjectNamespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The project-brief snapshot does not match its server-derived retention scope.",
                nameof(snapshot));
        }

        await RequireValidCurrentSnapshotAsync(
            snapshot,
            capturedScope,
            cancellationToken).ConfigureAwait(false);
        var canonical = CortexCanonicalJson.Serialize(snapshot);
        if (canonical.Length > CortexProjectBriefContract.MaximumUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "The project brief exceeds 8 KiB.");

        var repository = Repository(capturedScope);
        var retainedRevision = await repository.FindRevisionAsync(
            snapshot.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            snapshot.ProjectNamespace,
            snapshot.BriefRevision,
            cancellationToken).ConfigureAwait(false);
        var head = await repository.FindHeadAsync(
            snapshot.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            snapshot.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        var current = head is null ? null : Capture(head, scope: null);

        string? expectedPreviousHash;
        if (retainedRevision is not null)
        {
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
        return await repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var repository = Repository(
            PostgreSqlCortexRetentionScope.FromProjectBrief(scope));
        var retained = await repository.FindHeadAsync(
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
        var repository = Repository(
            PostgreSqlCortexRetentionScope.FromProjectBrief(scope));
        var head = await repository.FindHeadAsync(
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

        var predecessor = await repository.FindRevisionAsync(
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

    public async ValueTask<CortexProjectBriefSnapshot?> FindBySourceAsync(
        CortexProjectBriefReadScope scope,
        long stateRevision,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (stateRevision < 1 ||
            sourceHash is not { Length: 64 } ||
            sourceHash.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            return null;
        }

        var repository = Repository(
            PostgreSqlCortexRetentionScope.FromProjectBrief(scope));
        var head = await repository.FindHeadAsync(
            scope.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            scope.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;
        var current = Capture(head, scope);
        if (current.StateRevision < stateRevision)
            return null;

        // The projector appends one brief for each contiguous task-snapshot
        // state revision. A transfer may seed an arbitrary state revision at
        // brief revision one, so preserve that fixed offset instead of assuming
        // both revision domains begin together.
        var stateDistance = current.StateRevision - stateRevision;
        if (stateDistance >= current.BriefRevision)
            return null;
        var briefRevision = current.BriefRevision - stateDistance;
        var retained = briefRevision == current.BriefRevision
            ? head
            : await repository.FindRevisionAsync(
                scope.ProjectLinkId,
                CortexRetainedDocumentKinds.ProjectBrief,
                scope.ProjectNamespace,
                briefRevision,
                cancellationToken).ConfigureAwait(false);
        if (retained is null)
            return null;
        var historical = briefRevision == current.BriefRevision
            ? current
            : Capture(retained, scope);
        return historical.StateRevision == stateRevision &&
               string.Equals(historical.SourceHash, sourceHash, StringComparison.Ordinal)
            ? historical
            : null;
    }

    private PostgreSqlCortexAgentRetentionRepository Repository(
        PostgreSqlCortexRetentionScope scope) =>
        new(_transactions, _authenticator, scope, _timeProvider);

    private async ValueTask RequireValidCurrentSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        PostgreSqlCortexRetentionScope scope,
        CancellationToken cancellationToken)
    {
        var briefScope = new CortexProjectBriefReadScope(
            snapshot.TenantId,
            snapshot.ProjectLinkId,
            snapshot.ProjectId,
            snapshot.ProjectNamespace,
            scope.AuthorityId,
            scope.SubjectId,
            scope.ServiceId,
            scope.PolicyRevision);
        var service = new CortexProjectBriefService(
            new InMemoryCortexProjectBriefReadModel([snapshot]),
            _timeProvider);
        var validation = await service.ReadAsync(
            new CortexProjectBriefRequest(
                snapshot.ProjectLinkId,
                snapshot.AuthoritySetHash),
            briefScope,
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
            throw new InvalidDataException(
                "The retained PostgreSQL project brief failed its storage boundary.");
        }

        CortexProjectBriefSnapshot snapshot;
        try
        {
            snapshot = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                retained.CanonicalUtf8);
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException(
                "The retained PostgreSQL project brief is not canonical contract data.",
                exception);
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
            throw new InvalidDataException(
                "The retained PostgreSQL project-brief identity or canonical bytes do not match.");
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
