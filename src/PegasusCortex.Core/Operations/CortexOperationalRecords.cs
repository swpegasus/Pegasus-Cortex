using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Core.Operations;

public static class CortexOperationalContractIds
{
    public const string Task = "pegasus.cortex.operational-task/1.0";
    public const string Knowledge = "pegasus.cortex.operational-knowledge/1.0";
}

public static class CortexOperationalDocumentKinds
{
    public const string Task = "operational-task";
    public const string Knowledge = "operational-knowledge";
}

public static class CortexTaskStatuses
{
    public const string Open = "open";
    public const string Assigned = "assigned";
    public const string Claimed = "claimed";
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Archived = "archived";

    public static bool IsSupported(string? value) => value is
        Open or Assigned or Claimed or Blocked or Completed or Archived;
}

public static class CortexKnowledgeStates
{
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Archived = "archived";

    public static bool IsSupported(string? value) => value is
        Active or Superseded or Archived;
}

public static class CortexOperationalLimits
{
    public const int MaximumNamespaceCharacters = 128;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumTitleCharacters = 512;
    public const int MaximumContentCharacters = 24 * 1024;
    public const int MaximumReasonCharacters = 2048;
    public const int MaximumTags = 32;
    public const int MaximumDependencies = 64;
    public const int MaximumPageSize = 32;
}

public sealed record CortexOperationalTask(
    string Namespace,
    string TaskId,
    long Revision,
    string Status,
    string Title,
    string Content,
    string? Assignee,
    string? ClaimedBy,
    IReadOnlyList<string> Dependencies,
    string? BlockedReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    string LastActor)
{
    public CortexOperationalTask Validate()
    {
        CortexOperationalPolicy.RequireNamespace(Namespace);
        CortexOperationalPolicy.RequireIdentifier(TaskId, nameof(TaskId));
        CortexOperationalPolicy.RequireRevision(Revision);
        if (!CortexTaskStatuses.IsSupported(Status))
            throw new ArgumentException("The task status is unsupported.", nameof(Status));
        CortexOperationalPolicy.RequireContent(Title, CortexOperationalLimits.MaximumTitleCharacters, nameof(Title));
        CortexOperationalPolicy.RequireContent(Content, CortexOperationalLimits.MaximumContentCharacters, nameof(Content), allowEmpty: true);
        CortexOperationalPolicy.RequireOptionalIdentifier(Assignee, nameof(Assignee));
        CortexOperationalPolicy.RequireOptionalIdentifier(ClaimedBy, nameof(ClaimedBy));
        CortexOperationalPolicy.RequireOptionalContent(BlockedReason, CortexOperationalLimits.MaximumReasonCharacters, nameof(BlockedReason));
        CortexOperationalPolicy.RequireIdentifiers(Dependencies, CortexOperationalLimits.MaximumDependencies, nameof(Dependencies));
        CortexOperationalPolicy.RequireUtc(CreatedAtUtc, nameof(CreatedAtUtc));
        CortexOperationalPolicy.RequireUtc(UpdatedAtUtc, nameof(UpdatedAtUtc));
        CortexOperationalPolicy.RequireOptionalUtc(CompletedAtUtc, nameof(CompletedAtUtc));
        CortexOperationalPolicy.RequireOptionalUtc(ArchivedAtUtc, nameof(ArchivedAtUtc));
        CortexOperationalPolicy.RequireIdentifier(LastActor, nameof(LastActor));
        if (UpdatedAtUtc < CreatedAtUtc)
            throw new ArgumentException("The task update precedes creation.", nameof(UpdatedAtUtc));
        if (Status == CortexTaskStatuses.Blocked && string.IsNullOrWhiteSpace(BlockedReason) ||
            Status != CortexTaskStatuses.Blocked && BlockedReason is not null ||
            Status == CortexTaskStatuses.Completed && CompletedAtUtc is null ||
            Status != CortexTaskStatuses.Completed && CompletedAtUtc is not null ||
            Status == CortexTaskStatuses.Archived && ArchivedAtUtc is null ||
            Status != CortexTaskStatuses.Archived && ArchivedAtUtc is not null)
        {
            throw new ArgumentException("The task status metadata is inconsistent.");
        }
        return this with
        {
            Dependencies = Array.AsReadOnly(Dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
    }
}

public sealed record CortexOperationalKnowledge(
    string Namespace,
    string RecordId,
    long Revision,
    string State,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    string? SupersededByRecordId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    string LastActor)
{
    public CortexOperationalKnowledge Validate()
    {
        CortexOperationalPolicy.RequireNamespace(Namespace);
        CortexOperationalPolicy.RequireIdentifier(RecordId, nameof(RecordId));
        CortexOperationalPolicy.RequireRevision(Revision);
        if (!CortexKnowledgeStates.IsSupported(State))
            throw new ArgumentException("The knowledge state is unsupported.", nameof(State));
        CortexOperationalPolicy.RequireContent(Title, CortexOperationalLimits.MaximumTitleCharacters, nameof(Title));
        CortexOperationalPolicy.RequireContent(Content, CortexOperationalLimits.MaximumContentCharacters, nameof(Content), allowEmpty: true);
        CortexOperationalPolicy.RequireIdentifiers(Tags, CortexOperationalLimits.MaximumTags, nameof(Tags));
        CortexOperationalPolicy.RequireOptionalIdentifier(SupersededByRecordId, nameof(SupersededByRecordId));
        CortexOperationalPolicy.RequireUtc(CreatedAtUtc, nameof(CreatedAtUtc));
        CortexOperationalPolicy.RequireUtc(UpdatedAtUtc, nameof(UpdatedAtUtc));
        CortexOperationalPolicy.RequireOptionalUtc(ArchivedAtUtc, nameof(ArchivedAtUtc));
        CortexOperationalPolicy.RequireIdentifier(LastActor, nameof(LastActor));
        if (UpdatedAtUtc < CreatedAtUtc ||
            State == CortexKnowledgeStates.Superseded && SupersededByRecordId is null ||
            State != CortexKnowledgeStates.Superseded && SupersededByRecordId is not null ||
            State == CortexKnowledgeStates.Archived && ArchivedAtUtc is null ||
            State != CortexKnowledgeStates.Archived && ArchivedAtUtc is not null)
        {
            throw new ArgumentException("The knowledge state metadata is inconsistent.");
        }
        return this with
        {
            Tags = Array.AsReadOnly(Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
    }
}

public sealed record CortexOperationalWriteResult(
    string Disposition,
    string Namespace,
    string DocumentId,
    long? Revision,
    string? ActualHeadHash,
    object? Document);

public sealed record CortexOperationalPage<T>(
    string Namespace,
    IReadOnlyList<T> Items,
    string? NextAfterId);

public sealed class CortexOperationalApplicationService
{
    private static readonly DateTimeOffset NeverExpires =
        new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private readonly ICortexAgentRetentionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public CortexOperationalApplicationService(
        ICortexAgentRetentionRepository repository,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexOperationalWriteResult> CreateTaskAsync(
        string ns, string taskId, string title, string content,
        string? assignee, IReadOnlyList<string> dependencies,
        string idempotencyKey, string actor, CancellationToken cancellationToken)
    {
        var existing = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var first = await _repository.FindRevisionAsync(
                ns, CortexOperationalDocumentKinds.Task, taskId, 1, cancellationToken).ConfigureAwait(false);
            if (first is not null && string.Equals(first.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                var retained = Deserialize<CortexOperationalTask>(first).Validate();
                var dependenciesMatch = retained.Dependencies.SequenceEqual(
                    dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                return string.Equals(retained.Title, title, StringComparison.Ordinal) &&
                    string.Equals(retained.Content, content, StringComparison.Ordinal) &&
                    string.Equals(retained.Assignee, assignee, StringComparison.Ordinal) &&
                    dependenciesMatch
                    ? new(CortexRetentionWriteDispositions.Idempotent, ns, taskId, first.Revision, existing.ContentHash, retained)
                    : new(CortexRetentionWriteDispositions.Conflict, ns, taskId, existing.Revision, existing.ContentHash, Deserialize<CortexOperationalTask>(existing).Validate());
            }
            return new(CortexRetentionWriteDispositions.Conflict, ns, taskId, existing.Revision, existing.ContentHash, Deserialize<CortexOperationalTask>(existing).Validate());
        }
        var now = _timeProvider.GetUtcNow();
        var task = new CortexOperationalTask(
            ns, taskId, 1,
            assignee is null ? CortexTaskStatuses.Open : CortexTaskStatuses.Assigned,
            title, content, assignee, null, dependencies, null,
            now, now, null, null, actor).Validate();
        return await AppendAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId, 1, null,
            idempotencyKey, actor, CortexOperationalContractIds.Task,
            task, TaskSearchText(task), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexOperationalWriteResult> MutateTaskAsync(
        string ns, string taskId, long expectedRevision, string operation,
        string? title, string? content, string? assignee, string? claimedBy,
        string? reason, string? dependencyId, string idempotencyKey,
        string actor, CancellationToken cancellationToken)
    {
        var head = await ReadTaskAsync(ns, taskId, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return new("not-found", ns, taskId, null, null, null);
        if (head.Revision != expectedRevision)
        {
            if (head.Revision > expectedRevision)
            {
                var replay = await _repository.FindRevisionAsync(
                    ns, CortexOperationalDocumentKinds.Task, taskId,
                    expectedRevision + 1, cancellationToken).ConfigureAwait(false);
                if (replay is not null && string.Equals(replay.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    var replayDocument = Deserialize<CortexOperationalTask>(replay).Validate();
                    return TaskReplayMatches(
                        replayDocument, operation, title, content, assignee,
                        claimedBy, reason, dependencyId)
                        ? new(CortexRetentionWriteDispositions.Idempotent, ns, taskId, replay.Revision, null, replayDocument)
                        : new(CortexRetentionWriteDispositions.Conflict, ns, taskId, head.Revision, null, head);
                }
            }
            return new("stale-revision", ns, taskId, head.Revision, null, head);
        }
        EnsureTaskOperationAllowed(head.Status, operation);
        if (operation == "update" && title is null && content is null)
            throw new ArgumentException("A task update must change title or content.", nameof(operation));
        var now = _timeProvider.GetUtcNow();
        var next = operation switch
        {
            "update" => head with
            {
                Revision = head.Revision + 1,
                Title = title ?? head.Title,
                Content = content ?? head.Content,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "assign" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Assigned,
                Assignee = CortexOperationalPolicy.RequiredIdentifier(assignee, nameof(assignee)),
                ClaimedBy = null,
                BlockedReason = null,
                CompletedAtUtc = null,
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "claim" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Claimed,
                ClaimedBy = CortexOperationalPolicy.RequiredIdentifier(claimedBy, nameof(claimedBy)),
                BlockedReason = null,
                CompletedAtUtc = null,
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "block" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Blocked,
                BlockedReason = CortexOperationalPolicy.RequiredContent(reason, CortexOperationalLimits.MaximumReasonCharacters, nameof(reason)),
                CompletedAtUtc = null,
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "complete" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Completed,
                BlockedReason = null,
                CompletedAtUtc = now,
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "reopen" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Open,
                ClaimedBy = null,
                BlockedReason = null,
                CompletedAtUtc = null,
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "archive" => head with
            {
                Revision = head.Revision + 1,
                Status = CortexTaskStatuses.Archived,
                BlockedReason = null,
                CompletedAtUtc = null,
                ArchivedAtUtc = now,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "add-dependency" => head with
            {
                Revision = head.Revision + 1,
                Dependencies = head.Dependencies.Append(CortexOperationalPolicy.RequiredIdentifier(dependencyId, nameof(dependencyId))).ToArray(),
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "remove-dependency" => head with
            {
                Revision = head.Revision + 1,
                Dependencies = head.Dependencies.Where(value => !string.Equals(value, CortexOperationalPolicy.RequiredIdentifier(dependencyId, nameof(dependencyId)), StringComparison.Ordinal)).ToArray(),
                UpdatedAtUtc = now,
                LastActor = actor
            },
            _ => throw new ArgumentException("The task operation is unsupported.", nameof(operation))
        };
        next = next.Validate();
        var retained = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId, cancellationToken).ConfigureAwait(false);
        if (retained is null || retained.Revision != expectedRevision)
            return new("stale-revision", ns, taskId, retained?.Revision, retained?.ContentHash, head);
        return await AppendAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId, next.Revision,
            retained.ContentHash, idempotencyKey, actor,
            CortexOperationalContractIds.Task, next, TaskSearchText(next),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexOperationalTask?> ReadTaskAsync(
        string ns, string taskId, CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        CortexOperationalPolicy.RequireIdentifier(taskId, nameof(taskId));
        var value = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId, cancellationToken).ConfigureAwait(false);
        return value is null ? null : Deserialize<CortexOperationalTask>(value).Validate();
    }

    public async ValueTask<CortexOperationalPage<CortexOperationalTask>> ListTasksAsync(
        string ns, string? afterTaskId, int limit, CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        CortexOperationalPolicy.RequirePage(afterTaskId, limit);
        var page = await _repository.ListAllHeadsByKindAsync(
            ns, CortexOperationalDocumentKinds.Task, afterTaskId, limit, cancellationToken).ConfigureAwait(false);
        return new(ns, Array.AsReadOnly(page.Items.Select(value => Deserialize<CortexOperationalTask>(value).Validate()).ToArray()), page.NextAfterDocumentId);
    }

    public async ValueTask<IReadOnlyList<CortexOperationalTask>> ReadTaskHistoryAsync(
        string ns, string taskId, long firstRevision, long lastRevision,
        CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        CortexOperationalPolicy.RequireIdentifier(taskId, nameof(taskId));
        if (firstRevision < 1 || lastRevision < firstRevision || lastRevision - firstRevision > 255)
            throw new ArgumentOutOfRangeException(nameof(lastRevision));
        var values = new List<CortexOperationalTask>();
        await foreach (var value in _repository.ReadRevisionRangeAsync(
            ns, CortexOperationalDocumentKinds.Task, taskId,
            firstRevision, lastRevision, cancellationToken).ConfigureAwait(false))
        {
            values.Add(Deserialize<CortexOperationalTask>(value).Validate());
        }
        return values.AsReadOnly();
    }

    public async ValueTask<CortexOperationalWriteResult> CreateKnowledgeAsync(
        string ns, string recordId, string title, string content,
        IReadOnlyList<string> tags, string idempotencyKey, string actor,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, recordId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var first = await _repository.FindRevisionAsync(
                ns, CortexOperationalDocumentKinds.Knowledge, recordId, 1, cancellationToken).ConfigureAwait(false);
            if (first is not null && string.Equals(first.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                var retained = Deserialize<CortexOperationalKnowledge>(first).Validate();
                var tagsMatch = retained.Tags.SequenceEqual(tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                return string.Equals(retained.Title, title, StringComparison.Ordinal) &&
                    string.Equals(retained.Content, content, StringComparison.Ordinal) && tagsMatch
                    ? new(CortexRetentionWriteDispositions.Idempotent, ns, recordId, first.Revision, existing.ContentHash, retained)
                    : new(CortexRetentionWriteDispositions.Conflict, ns, recordId, existing.Revision, existing.ContentHash, Deserialize<CortexOperationalKnowledge>(existing).Validate());
            }
            return new(CortexRetentionWriteDispositions.Conflict, ns, recordId, existing.Revision, existing.ContentHash, Deserialize<CortexOperationalKnowledge>(existing).Validate());
        }
        var now = _timeProvider.GetUtcNow();
        var record = new CortexOperationalKnowledge(
            ns, recordId, 1, CortexKnowledgeStates.Active,
            title, content, tags, null, now, now, null, actor).Validate();
        return await AppendAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, recordId, 1, null,
            idempotencyKey, actor, CortexOperationalContractIds.Knowledge,
            record, KnowledgeSearchText(record), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexOperationalWriteResult> MutateKnowledgeAsync(
        string ns, string recordId, long expectedRevision, string operation,
        string? title, string? content, IReadOnlyList<string>? tags,
        string? replacementRecordId, string idempotencyKey, string actor,
        CancellationToken cancellationToken)
    {
        var head = await ReadKnowledgeAsync(ns, recordId, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return new("not-found", ns, recordId, null, null, null);
        if (head.Revision != expectedRevision)
        {
            if (head.Revision > expectedRevision)
            {
                var replay = await _repository.FindRevisionAsync(
                    ns, CortexOperationalDocumentKinds.Knowledge, recordId,
                    expectedRevision + 1, cancellationToken).ConfigureAwait(false);
                if (replay is not null && string.Equals(replay.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    var replayDocument = Deserialize<CortexOperationalKnowledge>(replay).Validate();
                    return KnowledgeReplayMatches(
                        replayDocument, operation, title, content, tags, replacementRecordId)
                        ? new(CortexRetentionWriteDispositions.Idempotent, ns, recordId, replay.Revision, null, replayDocument)
                        : new(CortexRetentionWriteDispositions.Conflict, ns, recordId, head.Revision, null, head);
                }
            }
            return new("stale-revision", ns, recordId, head.Revision, null, head);
        }
        EnsureKnowledgeOperationAllowed(head.State, operation);
        if (operation == "revise" && title is null && content is null && tags is null)
            throw new ArgumentException("A knowledge revision must change title, content, or tags.", nameof(operation));
        var now = _timeProvider.GetUtcNow();
        var next = operation switch
        {
            "revise" => head with
            {
                Revision = head.Revision + 1,
                Title = title ?? head.Title,
                Content = content ?? head.Content,
                Tags = tags ?? head.Tags,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "supersede" => head with
            {
                Revision = head.Revision + 1,
                State = CortexKnowledgeStates.Superseded,
                SupersededByRecordId = CortexOperationalPolicy.RequiredIdentifier(replacementRecordId, nameof(replacementRecordId)),
                ArchivedAtUtc = null,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            "archive" => head with
            {
                Revision = head.Revision + 1,
                State = CortexKnowledgeStates.Archived,
                SupersededByRecordId = null,
                ArchivedAtUtc = now,
                UpdatedAtUtc = now,
                LastActor = actor
            },
            _ => throw new ArgumentException("The knowledge operation is unsupported.", nameof(operation))
        };
        next = next.Validate();
        var retained = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, recordId, cancellationToken).ConfigureAwait(false);
        if (retained is null || retained.Revision != expectedRevision)
            return new("stale-revision", ns, recordId, retained?.Revision, retained?.ContentHash, head);
        return await AppendAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, recordId, next.Revision,
            retained.ContentHash, idempotencyKey, actor,
            CortexOperationalContractIds.Knowledge, next, KnowledgeSearchText(next),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexOperationalKnowledge?> ReadKnowledgeAsync(
        string ns, string recordId, CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        CortexOperationalPolicy.RequireIdentifier(recordId, nameof(recordId));
        var value = await _repository.FindHeadAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, recordId, cancellationToken).ConfigureAwait(false);
        return value is null ? null : Deserialize<CortexOperationalKnowledge>(value).Validate();
    }

    public async ValueTask<IReadOnlyList<CortexOperationalKnowledge>> SearchKnowledgeAsync(
        string ns, string text, int limit, CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        if (string.IsNullOrWhiteSpace(text) || text.Length > CortexAgentRetentionLimits.MaximumSearchTextCharacters)
            throw new ArgumentException("A bounded search query is required.", nameof(text));
        if (limit is < 1 or > CortexOperationalLimits.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var hits = await _repository.SearchHeadsByKindAsync(
            ns, CortexOperationalDocumentKinds.Knowledge, text,
            CortexOperationalLimits.MaximumPageSize, cancellationToken).ConfigureAwait(false);
        var values = new List<CortexOperationalKnowledge>(hits.Count);
        foreach (var hit in hits)
        {
            var value = await _repository.FindRevisionAsync(
                ns, CortexOperationalDocumentKinds.Knowledge,
                hit.DocumentId, hit.Revision, cancellationToken).ConfigureAwait(false);
            if (value is not null)
                values.Add(Deserialize<CortexOperationalKnowledge>(value).Validate());
        }
        return values
            .Where(value => value.State == CortexKnowledgeStates.Active)
            .Take(limit)
            .ToArray();
    }

    private static bool TaskReplayMatches(
        CortexOperationalTask retained,
        string operation,
        string? title,
        string? content,
        string? assignee,
        string? claimedBy,
        string? reason,
        string? dependencyId) => operation switch
    {
        "update" => (title is null || string.Equals(retained.Title, title, StringComparison.Ordinal)) &&
            (content is null || string.Equals(retained.Content, content, StringComparison.Ordinal)),
        "assign" => retained.Status == CortexTaskStatuses.Assigned &&
            string.Equals(retained.Assignee, assignee, StringComparison.Ordinal),
        "claim" => retained.Status == CortexTaskStatuses.Claimed &&
            string.Equals(retained.ClaimedBy, claimedBy, StringComparison.Ordinal),
        "block" => retained.Status == CortexTaskStatuses.Blocked &&
            string.Equals(retained.BlockedReason, reason, StringComparison.Ordinal),
        "complete" => retained.Status == CortexTaskStatuses.Completed,
        "reopen" => retained.Status == CortexTaskStatuses.Open,
        "archive" => retained.Status == CortexTaskStatuses.Archived,
        "add-dependency" => dependencyId is not null && retained.Dependencies.Contains(dependencyId, StringComparer.Ordinal),
        "remove-dependency" => dependencyId is not null && !retained.Dependencies.Contains(dependencyId, StringComparer.Ordinal),
        _ => false
    };

    private static bool KnowledgeReplayMatches(
        CortexOperationalKnowledge retained,
        string operation,
        string? title,
        string? content,
        IReadOnlyList<string>? tags,
        string? replacementRecordId) => operation switch
    {
        "revise" => (title is null || string.Equals(retained.Title, title, StringComparison.Ordinal)) &&
            (content is null || string.Equals(retained.Content, content, StringComparison.Ordinal)) &&
            (tags is null || retained.Tags.SequenceEqual(tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))),
        "supersede" => retained.State == CortexKnowledgeStates.Superseded &&
            string.Equals(retained.SupersededByRecordId, replacementRecordId, StringComparison.Ordinal),
        "archive" => retained.State == CortexKnowledgeStates.Archived,
        _ => false
    };

    private static void EnsureTaskOperationAllowed(string status, string operation)
    {
        var allowed = operation switch
        {
            "update" or "assign" or "claim" or "block" or "complete" or
                "add-dependency" or "remove-dependency" => status != CortexTaskStatuses.Archived,
            "reopen" => status is CortexTaskStatuses.Blocked or CortexTaskStatuses.Completed or CortexTaskStatuses.Archived,
            "archive" => status != CortexTaskStatuses.Archived,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Task operation '{operation}' is invalid from status '{status}'.");
    }

    private static void EnsureKnowledgeOperationAllowed(string state, string operation)
    {
        var allowed = operation switch
        {
            "revise" or "supersede" => state == CortexKnowledgeStates.Active,
            "archive" => state != CortexKnowledgeStates.Archived,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Knowledge operation '{operation}' is invalid from state '{state}'.");
    }

    private async ValueTask<CortexOperationalWriteResult> AppendAsync<T>(
        string ns, string kind, string documentId, long revision,
        string? expectedPreviousHash, string idempotencyKey, string actor,
        string contractId, T document, string searchText,
        CancellationToken cancellationToken)
    {
        CortexOperationalPolicy.RequireNamespace(ns);
        CortexOperationalPolicy.RequireIdentifier(documentId, nameof(documentId));
        CortexOperationalPolicy.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        CortexOperationalPolicy.RequireIdentifier(actor, nameof(actor));
        var bytes = CortexCanonicalJson.Serialize(document);
        var now = _timeProvider.GetUtcNow();
        var request = new CortexRetentionAppendRequest(
            ns, kind, documentId, revision, expectedPreviousHash,
            idempotencyKey, actor, contractId, bytes,
            CortexCanonicalJson.Sha256(bytes), searchText, now, NeverExpires).Validate();
        var result = await _repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        return new(
            result.Disposition, ns, documentId,
            result.Document?.Revision, result.CurrentHeadHash,
            result.Document is null ? null : document);
    }

    private static T Deserialize<T>(CortexRetainedDocument value) =>
        CortexCanonicalJson.Deserialize<T>(value.CanonicalUtf8);

    private static string TaskSearchText(CortexOperationalTask task) =>
        NormalizeSearchText([task.TaskId, task.Status, task.Title, task.Content, task.Assignee, task.ClaimedBy, task.BlockedReason, .. task.Dependencies]);

    private static string KnowledgeSearchText(CortexOperationalKnowledge record) =>
        NormalizeSearchText([record.RecordId, record.State, record.Title, record.Content, record.SupersededByRecordId, .. record.Tags]);

    private static string NormalizeSearchText(IEnumerable<string?> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (builder.Length > 0)
                builder.Append('\n');
            foreach (var character in value!)
            {
                builder.Append(char.IsControl(character) && character is not '\r' and not '\n' and not '\t' ? ' ' : character);
                if (builder.Length == CortexAgentRetentionLimits.MaximumSearchTextCharacters)
                    return builder.ToString();
            }
        }
        return builder.ToString();
    }
}

internal static class CortexOperationalPolicy
{
    public static void RequireNamespace(string? value) =>
        RequireIdentifier(value, "namespace", CortexOperationalLimits.MaximumNamespaceCharacters);

    public static void RequireIdentifier(string? value, string parameterName) =>
        RequireIdentifier(value, parameterName, CortexOperationalLimits.MaximumIdentifierCharacters);

    public static string RequiredIdentifier(string? value, string parameterName)
    {
        RequireIdentifier(value, parameterName);
        return value!;
    }

    public static void RequireOptionalIdentifier(string? value, string parameterName)
    {
        if (value is not null)
            RequireIdentifier(value, parameterName);
    }

    public static void RequireIdentifiers(IReadOnlyList<string> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum)
            throw new ArgumentOutOfRangeException(parameterName);
        foreach (var value in values)
            RequireIdentifier(value, parameterName);
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Identifiers must be unique.", parameterName);
    }

    public static void RequireContent(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Bounded text is required.", parameterName);
    }

    public static string RequiredContent(string? value, int maximum, string parameterName)
    {
        RequireContent(value, maximum, parameterName);
        return value!;
    }

    public static void RequireOptionalContent(string? value, int maximum, string parameterName)
    {
        if (value is not null)
            RequireContent(value, maximum, parameterName);
    }

    public static void RequireRevision(long revision)
    {
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public static void RequirePage(string? afterId, int limit)
    {
        RequireOptionalIdentifier(afterId, nameof(afterId));
        if (limit is < 1 or > CortexOperationalLimits.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
    }

    public static void RequireOptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is not null)
            RequireUtc(value.Value, parameterName);
    }

    private static void RequireIdentifier(string? value, string parameterName, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("A bounded portable identifier is required.", parameterName);
        }
    }
}
