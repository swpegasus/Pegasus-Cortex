using System.Text.Json;
using System.Text;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed partial class FileCortexV2Store
{
    public async ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Action switch
        {
            CortexTaskActionV2.List => await ListTasksAsync(command, cancellationToken).ConfigureAwait(false),
            CortexTaskActionV2.Get => await GetTaskAsync(command, cancellationToken).ConfigureAwait(false),
            CortexTaskActionV2.History => await TaskHistoryAsync(command, cancellationToken).ConfigureAwait(false),
            _ => await WriteTaskAsync(command, cancellationToken).ConfigureAwait(false)
        };
    }

    private async ValueTask<CortexTaskResultV2> ListTasksAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        RequireReadableNamespace(command.Namespace);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var ordered = state.Heads(TaskRecordType)
            .Where(value => value.Namespace == command.Namespace)
            .OrderBy(value => value.RecordId, StringComparer.Ordinal)
            .ToArray();
        var start = 0;
        if (command.AfterTaskId is not null)
        {
            while (start < ordered.Length &&
                string.CompareOrdinal(ordered[start].RecordId, command.AfterTaskId) <= 0)
            {
                start++;
            }
        }
        var items = new List<CortexTaskSummaryV2>();
        var hasMore = false;
        for (var index = start; index < ordered.Length; index++)
        {
            if (items.Count == command.Limit)
            {
                hasMore = true;
                break;
            }
            var task = await LoadTaskAsync(ordered[index], cancellationToken).ConfigureAwait(false);
            var candidate = TaskSummary(task);
            items.Add(candidate);
            var probe = new CortexTaskResultV2(
                [command.Namespace], "listed", null, items, null, candidate.TaskId);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length > command.MaxUtf8Bytes)
            {
                items.RemoveAt(items.Count - 1);
                hasMore = true;
                break;
            }
        }
        var next = hasMore && items.Count > 0 ? items[^1].TaskId : null;
        return new CortexTaskResultV2([command.Namespace], "listed", null, items, null, next);
    }

    private async ValueTask<CortexTaskResultV2> GetTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        RequireReadableNamespace(command.Namespace);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var entry = state.Head(TaskRecordType, command.Namespace, command.TaskId!) ??
            throw new KeyNotFoundException("The requested Cortex task does not exist.");
        var task = await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false);
        return new CortexTaskResultV2([command.Namespace], "read", TaskSummary(task), [], null, null);
    }

    private async ValueTask<CortexTaskResultV2> TaskHistoryAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        RequireReadableNamespace(command.Namespace);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var entries = state.Entries
            .Where(value => value.RecordType == TaskRecordType && value.Namespace == command.Namespace &&
                value.RecordId == command.TaskId && value.Revision >= command.FirstRevision &&
                value.Revision <= command.LastRevision)
            .OrderBy(value => value.Revision)
            .ToArray();
        if (entries.Length == 0)
            throw new KeyNotFoundException("The requested Cortex task history does not exist.");
        var revisions = new List<CortexTaskV2>(entries.Length);
        foreach (var entry in entries)
            revisions.Add(await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false));
        var history = new List<CortexTaskHistoryEventV2>(revisions.Count);
        CortexTaskV2? previous = null;
        foreach (var task in revisions)
        {
            var contentChanged = previous is null || !string.Equals(previous.Content, task.Content, StringComparison.Ordinal);
            history.Add(new CortexTaskHistoryEventV2(
                task.Namespace,
                task.TaskId,
                task.Revision,
                task.Status,
                Excerpt(task.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
                TaskEvent(previous, task),
                task.Provenance.Actor,
                task.UpdatedAtUtc,
                contentChanged,
                CortexFileV2AtomicIo.Sha256(Encoding.UTF8.GetBytes(task.Content)),
                task.PhaseId,
                task.CheckpointSubject));
            previous = task;
        }
        return new CortexTaskResultV2([command.Namespace], "history", null, [], history, null);
    }

    private async ValueTask<CortexTaskResultV2> WriteTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        RequireWritableNamespace(command.Namespace);
        if (command.EffectiveProvenance is null)
        {
            throw new InvalidOperationException(
                "Task provenance must be stamped by the Cortex V2 application boundary.");
        }
        var operation = $"task:{command.Action.ToString().ToLowerInvariant()}:{command.TaskId}";
        var commandHash = TaskCommandHash(command);
        CortexTaskV2 task;
        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var replay = FindReceipt(state, command.Namespace, operation, command.IdempotencyKey!, commandHash);
            if (replay is not null)
            {
                task = await LoadTaskFromReceiptAsync(state, replay, cancellationToken).ConfigureAwait(false);
                return RequireResponseBudget(new CortexTaskResultV2(
                    [command.Namespace], "idempotent", TaskSummary(task), [], null, null),
                    command.MaxUtf8Bytes);
            }

            var head = state.Head(TaskRecordType, command.Namespace, command.TaskId!);
            if (command.Action == CortexTaskActionV2.Create)
            {
                if (head is not null)
                    throw new InvalidOperationException("The Cortex task already exists.");
                var now = UtcNow();
                task = new CortexTaskV2(
                    command.Namespace,
                    command.TaskId!,
                    1,
                    CortexTaskStatusV2.Open,
                    command.Title!,
                    command.Content!,
                    null,
                    null,
                    command.Dependencies?.ToArray() ?? [],
                    null,
                    now,
                    now,
                    null,
                    null,
                    command.EffectiveProvenance,
                    command.PhaseId,
                    command.CheckpointSubject);
            }
            else
            {
                if (head is null)
                    throw new KeyNotFoundException("The Cortex task does not exist.");
                var current = await LoadTaskAsync(head, cancellationToken).ConfigureAwait(false);
                if (current.Revision != command.ExpectedRevision)
                    throw new InvalidOperationException("The Cortex task expected revision is stale.");
                task = MutateTask(current, command, UtcNow());
            }

            var response = new CortexTaskResultV2(
                [command.Namespace],
                command.Action == CortexTaskActionV2.Create ? "created" : "updated",
                TaskSummary(task),
                [],
                null,
                null);
            RequireResponseBudget(response, command.MaxUtf8Bytes);
            await AddTaskRevisionAsync(
                state,
                task,
                command.IdempotencyKey!,
                commandHash,
                cancellationToken).ConfigureAwait(false);
            state.Receipts.Add(new CortexFileV2Receipt(
                command.Namespace,
                operation,
                command.IdempotencyKey!,
                commandHash,
                0,
                [new CortexFileV2RecordLocator(TaskRecordType, task.TaskId, task.Revision)],
                command.EffectiveProvenance,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch));
            await CommitAsync(state, cancellationToken).ConfigureAwait(false);
        }
        await TryRefreshHotAsync(cancellationToken).ConfigureAwait(false);
        return new CortexTaskResultV2(
            [command.Namespace],
            command.Action == CortexTaskActionV2.Create ? "created" : "updated",
            TaskSummary(task),
            [],
            null,
            null);
    }

    private static CortexTaskV2 MutateTask(
        CortexTaskV2 current,
        CortexTaskCommandV2 command,
        DateTimeOffset now)
    {
        if (current.Status == CortexTaskStatusV2.Archived)
            throw new InvalidOperationException("An archived Cortex task cannot be mutated.");
        var task = current with
        {
            Revision = checked(current.Revision + 1),
            UpdatedAtUtc = now,
            Provenance = command.EffectiveProvenance ?? current.Provenance,
            PhaseId = command.PhaseId ?? current.PhaseId,
            CheckpointSubject = command.CheckpointSubject ?? current.CheckpointSubject
        };
        switch (command.Action)
        {
            case CortexTaskActionV2.Assign:
                RequireIncomplete(current);
                return task with
                {
                    Status = CortexTaskStatusV2.Assigned,
                    Assignee = command.Assignee,
                    ClaimedBy = null,
                    BlockedReason = null
                };
            case CortexTaskActionV2.Claim:
                RequireIncomplete(current);
                return task with
                {
                    Status = CortexTaskStatusV2.Claimed,
                    ClaimedBy = command.ClaimedBy,
                    BlockedReason = null
                };
            case CortexTaskActionV2.Update:
                return task with
                {
                    Title = command.Title ?? current.Title,
                    Content = command.Content ?? current.Content
                };
            case CortexTaskActionV2.Block:
                RequireIncomplete(current);
                return task with
                {
                    Status = CortexTaskStatusV2.Blocked,
                    BlockedReason = command.Reason,
                    CompletedAtUtc = null
                };
            case CortexTaskActionV2.Complete:
                return task with
                {
                    Status = CortexTaskStatusV2.Completed,
                    BlockedReason = null,
                    CompletedAtUtc = now
                };
            case CortexTaskActionV2.Reopen:
                if (current.Status is not (CortexTaskStatusV2.Completed or CortexTaskStatusV2.Blocked))
                    throw new InvalidOperationException("Only a completed or blocked Cortex task can be reopened.");
                return task with
                {
                    Status = CortexTaskStatusV2.Open,
                    BlockedReason = null,
                    CompletedAtUtc = null
                };
            case CortexTaskActionV2.Archive:
                return task with { Status = CortexTaskStatusV2.Archived, ArchivedAtUtc = now };
            case CortexTaskActionV2.DependencyAdd:
                if (command.DependencyId == current.TaskId)
                    throw new InvalidOperationException("A Cortex task cannot depend on itself.");
                if (current.Dependencies.Contains(command.DependencyId!, StringComparer.Ordinal))
                    return task;
                if (current.Dependencies.Count >= CortexV2Limits.MaximumDependencies)
                    throw new InvalidOperationException("The Cortex task dependency bound was reached.");
                return task with { Dependencies = [.. current.Dependencies, command.DependencyId!] };
            case CortexTaskActionV2.DependencyRemove:
                return task with
                {
                    Dependencies = current.Dependencies
                        .Where(value => value != command.DependencyId)
                        .ToArray()
                };
            default:
                throw new InvalidOperationException("The task command is not a write action.");
        }
    }

    private static void RequireIncomplete(CortexTaskV2 task)
    {
        if (task.Status == CortexTaskStatusV2.Completed)
            throw new InvalidOperationException("A completed Cortex task must be reopened before this mutation.");
    }

    private static string TaskEvent(CortexTaskV2? previous, CortexTaskV2 current)
    {
        if (previous is null)
            return "created";
        if (previous.Status != current.Status)
            return current.Status.ToString().ToLowerInvariant();
        if (!previous.Dependencies.SequenceEqual(current.Dependencies, StringComparer.Ordinal))
            return "dependencies-changed";
        if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal) ||
            !string.Equals(previous.Content, current.Content, StringComparison.Ordinal))
        {
            return "updated";
        }
        return "revision";
    }

    private async ValueTask<CortexTaskV2> LoadTaskFromReceiptAsync(
        CortexFileV2State state,
        CortexFileV2Receipt receipt,
        CancellationToken cancellationToken)
    {
        var locator = receipt.Results.Single(value => value.RecordType == TaskRecordType);
        var entry = state.RevisionEntry(
            TaskRecordType,
            receipt.Namespace,
            locator.RecordId,
            locator.Revision) ?? throw new InvalidDataException("An idempotency receipt lost its task revision.");
        return await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static CortexTaskSummaryV2 TaskSummary(CortexTaskV2 task) => new(
        task.Namespace,
        task.TaskId,
        task.Revision,
        task.Status,
        Excerpt(task.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
        Excerpt(task.Content, CortexV2Limits.MaximumReceiptExcerptCharacters),
        task.Assignee,
        task.ClaimedBy,
        task.BlockedReason is null
            ? null
            : Excerpt(task.BlockedReason, CortexV2Limits.MaximumReceiptReasonCharacters),
        task.PhaseId,
        task.CheckpointSubject is null
            ? null
            : Excerpt(task.CheckpointSubject, CortexV2Limits.MaximumCheckpointSubjectCharacters));
}
