using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed partial class FileCortexV2Store
{
    public async ValueTask<CortexFinishResultV2> FinishAsync(
        CortexFinishRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireWritableNamespace(request.Namespace);
        if (request.EffectiveProvenance is null ||
            request.Knowledge.Any(value => value.EffectiveProvenance is null))
        {
            throw new InvalidOperationException(
                "Finish knowledge provenance must be stamped by the Cortex V2 application boundary.");
        }
        var commandHash = FinishCommandHash(request);
        CortexTaskV2 task;
        var knowledge = new List<CortexKnowledgeV2>();
        string contextCursor;
        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var replay = FindReceipt(state, request.Namespace, "finish", request.IdempotencyKey, commandHash);
            if (replay is not null)
            {
                task = await LoadTaskFromReceiptAsync(state, replay, cancellationToken).ConfigureAwait(false);
                foreach (var locator in replay.Results.Where(value => value.RecordType == KnowledgeRecordType))
                {
                    var entry = state.RevisionEntry(
                        KnowledgeRecordType,
                        request.Namespace,
                        locator.RecordId,
                        locator.Revision) ?? throw new InvalidDataException("A finish receipt lost a knowledge revision.");
                    knowledge.Add(await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false));
                }
                return RequireResponseBudget(new CortexFinishResultV2(
                    [request.Namespace],
                    "idempotent",
                    TaskSummary(task),
                    knowledge.Select(KnowledgeReceipt).ToArray(),
                    state.ContextCursor), request.MaxUtf8Bytes);
            }

            var taskHead = state.Head(TaskRecordType, request.Namespace, request.TaskId) ??
                throw new KeyNotFoundException("The Cortex finish task does not exist.");
            var currentTask = await LoadTaskAsync(taskHead, cancellationToken).ConfigureAwait(false);
            if (currentTask.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("The Cortex finish expected task revision is stale.");
            var now = UtcNow();
            task = FinishTask(currentTask, request, now);
            if (!ReferenceEquals(task, currentTask))
            {
                await AddTaskRevisionAsync(
                    state,
                    task,
                    request.IdempotencyKey,
                    commandHash,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var write in request.Knowledge)
            {
                var head = state.Head(KnowledgeRecordType, request.Namespace, write.RecordId);
                CortexKnowledgeV2 record;
                if (write.ExpectedRevision is null)
                {
                    if (head is not null)
                        throw new InvalidOperationException($"Finish knowledge '{write.RecordId}' already exists.");
                    record = new CortexKnowledgeV2(
                        request.Namespace,
                        write.RecordId,
                        1,
                        CortexKnowledgeStateV2.Active,
                        write.Kind,
                        write.Tier,
                        write.Title,
                        write.Content,
                        write.Tags.ToArray(),
                        write.EffectiveProvenance!,
                        null,
                        now,
                        now,
                        null);
                }
                else
                {
                    if (head is null)
                        throw new KeyNotFoundException($"Finish knowledge '{write.RecordId}' does not exist.");
                    var current = await LoadKnowledgeAsync(head, cancellationToken).ConfigureAwait(false);
                    if (current.Revision != write.ExpectedRevision)
                        throw new InvalidOperationException($"Finish knowledge '{write.RecordId}' has a stale revision.");
                    if (current.State != CortexKnowledgeStateV2.Active)
                        throw new InvalidOperationException($"Finish knowledge '{write.RecordId}' is not active.");
                    record = current with
                    {
                        Revision = checked(current.Revision + 1),
                        Kind = write.Kind,
                        Tier = write.Tier,
                        Title = write.Title,
                        Content = write.Content,
                        Tags = write.Tags.ToArray(),
                        Provenance = write.EffectiveProvenance!,
                        UpdatedAtUtc = now
                    };
                }
                await AddKnowledgeRevisionAsync(
                    state,
                    record,
                    write.IdempotencyKey,
                    commandHash,
                    cancellationToken).ConfigureAwait(false);
                knowledge.Add(record);
            }

            var projectedReceipts = knowledge.Select(KnowledgeReceipt).ToArray();
            RequireResponseBudget(new CortexFinishResultV2(
                [request.Namespace],
                "finished",
                TaskSummary(task),
                projectedReceipts,
                "ctx-00000000000000000000000000000000"), request.MaxUtf8Bytes);
            var locators = new List<CortexFileV2RecordLocator>
            {
                new(TaskRecordType, task.TaskId, task.Revision)
            };
            locators.AddRange(knowledge.Select(value =>
                new CortexFileV2RecordLocator(KnowledgeRecordType, value.RecordId, value.Revision)));
            state.Receipts.Add(new CortexFileV2Receipt(
                request.Namespace,
                "finish",
                request.IdempotencyKey,
                commandHash,
                0,
                locators,
                request.EffectiveProvenance,
                request.Checkpoint,
                request.Evidence,
                request.Handoff,
                DateTimeOffset.UnixEpoch));
            await CommitAsync(state, cancellationToken).ConfigureAwait(false);
            contextCursor = state.ContextCursor;
        }
        await TryRefreshHotAsync(cancellationToken).ConfigureAwait(false);
        return new CortexFinishResultV2(
            [request.Namespace],
            "finished",
            TaskSummary(task),
            knowledge.Select(KnowledgeReceipt).ToArray(),
            contextCursor);
    }

    private static CortexTaskV2 FinishTask(
        CortexTaskV2 current,
        CortexFinishRequestV2 request,
        DateTimeOffset now)
    {
        var hasFieldUpdate = request.TaskTitle is not null || request.TaskContent is not null ||
            request.PhaseId is not null || request.CheckpointSubject is not null;
        if (request.TaskAction == CortexFinishTaskActionV2.None && !hasFieldUpdate)
            return current;
        if (current.Status == CortexTaskStatusV2.Archived)
            throw new InvalidOperationException("An archived Cortex task cannot be finished.");
        var task = current with
        {
            Revision = checked(current.Revision + 1),
            Title = request.TaskTitle ?? current.Title,
            Content = request.TaskContent ?? current.Content,
            PhaseId = request.PhaseId ?? current.PhaseId,
            CheckpointSubject = request.CheckpointSubject ?? current.CheckpointSubject,
            UpdatedAtUtc = now,
            Provenance = request.EffectiveProvenance!
        };
        return request.TaskAction switch
        {
            CortexFinishTaskActionV2.None or CortexFinishTaskActionV2.Update => task,
            CortexFinishTaskActionV2.Block => task with
            {
                Status = CortexTaskStatusV2.Blocked,
                BlockedReason = request.BlockedReason,
                CompletedAtUtc = null
            },
            CortexFinishTaskActionV2.Complete => task with
            {
                Status = CortexTaskStatusV2.Completed,
                BlockedReason = null,
                CompletedAtUtc = now
            },
            _ => throw new InvalidOperationException("The finish task action is unsupported.")
        };
    }

    private static CortexKnowledgeReceiptV2 KnowledgeReceipt(CortexKnowledgeV2 value) => new(
        value.Namespace,
        value.RecordId,
        value.Revision,
        value.State,
        value.Kind,
        value.Tier,
        Excerpt(value.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
        new CortexProvenanceReceiptV2(
            value.Provenance.Source,
            value.Provenance.Actor,
            value.Provenance.SourceId,
            value.Provenance.CapturedAtUtc, value.Provenance.VerifiedSource));
}
