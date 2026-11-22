using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed partial class FileCortexV2Store
{
    public async ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireReadableNamespace(command.Namespace);
        if (command.Action == CortexKnowledgeActionV2.Analyze)
            return await AnalyzeKnowledgeAsync(command, cancellationToken).ConfigureAwait(false);
        if (command.Action == CortexKnowledgeActionV2.Read)
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var head = state.Head(KnowledgeRecordType, command.Namespace, command.RecordId!) ??
                throw new KeyNotFoundException("The requested Cortex knowledge record does not exist.");
            var readRecord = await LoadKnowledgeAsync(head, cancellationToken).ConfigureAwait(false);
            return RequireResponseBudget(
                new CortexKnowledgeResultV2([command.Namespace], "read", KnowledgeReceipt(readRecord)),
                command.MaxUtf8Bytes);
        }

        RequireWritableNamespace(command.Namespace);
        if (command.EffectiveProvenance is null && command.Action == CortexKnowledgeActionV2.Create)
        {
            throw new InvalidOperationException(
                "Knowledge provenance must be stamped by the Cortex V2 application boundary.");
        }
        var operation = $"knowledge:{command.Action.ToString().ToLowerInvariant()}:{command.RecordId}";
        var commandHash = KnowledgeCommandHash(command);
        CortexKnowledgeV2 record;
        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var replay = FindReceipt(state, command.Namespace, operation, command.IdempotencyKey!, commandHash);
            if (replay is not null)
            {
                record = await LoadKnowledgeFromReceiptAsync(state, replay, cancellationToken)
                    .ConfigureAwait(false);
                return RequireResponseBudget(
                    new CortexKnowledgeResultV2(
                        [command.Namespace],
                        "idempotent",
                        KnowledgeReceipt(record)),
                    command.MaxUtf8Bytes);
            }

            var head = state.Head(KnowledgeRecordType, command.Namespace, command.RecordId!);
            if (command.Action == CortexKnowledgeActionV2.Create)
            {
                if (head is not null)
                    throw new InvalidOperationException("The Cortex knowledge record already exists.");
                var now = UtcNow();
                record = new CortexKnowledgeV2(
                    command.Namespace,
                    command.RecordId!,
                    1,
                    CortexKnowledgeStateV2.Active,
                    command.Kind!.Value,
                    command.Tier!.Value,
                    command.Title!,
                    command.Content!,
                    command.Tags?.ToArray() ?? [],
                    command.EffectiveProvenance!,
                    null,
                    now,
                    now,
                    null);
            }
            else
            {
                if (head is null)
                    throw new KeyNotFoundException("The Cortex knowledge record does not exist.");
                var current = await LoadKnowledgeAsync(head, cancellationToken).ConfigureAwait(false);
                if (current.Revision != command.ExpectedRevision)
                    throw new InvalidOperationException("The Cortex knowledge expected revision is stale.");
                record = MutateKnowledge(current, command, UtcNow());
            }

            var response = new CortexKnowledgeResultV2(
                [command.Namespace],
                command.Action == CortexKnowledgeActionV2.Create ? "created" : "updated",
                KnowledgeReceipt(record));
            RequireResponseBudget(response, command.MaxUtf8Bytes);
            await AddKnowledgeRevisionAsync(
                state,
                record,
                command.IdempotencyKey!,
                commandHash,
                cancellationToken).ConfigureAwait(false);
            state.Receipts.Add(new CortexFileV2Receipt(
                command.Namespace,
                operation,
                command.IdempotencyKey!,
                commandHash,
                0,
                [new CortexFileV2RecordLocator(KnowledgeRecordType, record.RecordId, record.Revision)],
                record.Provenance,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch));
            await CommitAsync(state, cancellationToken).ConfigureAwait(false);
        }
        await TryRefreshHotAsync(cancellationToken).ConfigureAwait(false);
        return new CortexKnowledgeResultV2(
            [command.Namespace],
            command.Action == CortexKnowledgeActionV2.Create ? "created" : "updated",
            KnowledgeReceipt(record));
    }

    private async ValueTask<CortexKnowledgeResultV2> AnalyzeKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var ordered = state.Heads(KnowledgeRecordType)
            .Where(value => value.Namespace == command.Namespace &&
                (command.AfterRecordId is null ||
                 string.CompareOrdinal(value.RecordId, command.AfterRecordId) > 0))
            .OrderBy(value => value.RecordId, StringComparer.Ordinal)
            .ToArray();
        var inspected = ordered.Take(command.Limit).ToArray();
        var heads = new List<CortexKnowledgeV2>(inspected.Length);
        foreach (var entry in inspected)
        {
            var record = await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false);
            if (record.State == CortexKnowledgeStateV2.Active)
                heads.Add(record);
        }
        var scanTruncated = inspected.Length < ordered.Length;
        var suggestions = CortexV2KnowledgeConsolidation.Analyze(heads);
        var suggestionsTruncated = suggestions.Count > CortexV2Limits.MaximumConsolidationSuggestions;
        var selected = suggestions.Take(CortexV2Limits.MaximumConsolidationSuggestions).ToList();
        CortexKnowledgeResultV2 Result() => new(
            [command.Namespace],
            "analyzed",
            Record: null,
            new CortexConsolidationAnalysisV2(
                "cortex.knowledge-consolidation/1",
                inspected.Length,
                scanTruncated,
                suggestionsTruncated,
                selected.AsReadOnly(),
                scanTruncated && inspected.Length > 0 ? inspected[^1].RecordId : null));
        while (selected.Count > 0 &&
            JsonSerializer.SerializeToUtf8Bytes(Result(), CortexV2Json.Options).Length > command.MaxUtf8Bytes)
        {
            suggestionsTruncated = true;
            selected.RemoveAt(selected.Count - 1);
        }
        return RequireResponseBudget(Result(), command.MaxUtf8Bytes);
    }

    private static CortexKnowledgeV2 MutateKnowledge(
        CortexKnowledgeV2 current,
        CortexKnowledgeCommandV2 command,
        DateTimeOffset now)
    {
        if (current.State == CortexKnowledgeStateV2.Archived)
            throw new InvalidOperationException("An archived Cortex knowledge record cannot be mutated.");
        var record = current with
        {
            Revision = checked(current.Revision + 1),
            UpdatedAtUtc = now
        };
        return command.Action switch
        {
            CortexKnowledgeActionV2.Revise => record with
            {
                Kind = command.Kind ?? current.Kind,
                Tier = command.Tier ?? current.Tier,
                Title = command.Title ?? current.Title,
                Content = command.Content ?? current.Content,
                Tags = command.Tags?.ToArray() ?? current.Tags,
                Provenance = command.EffectiveProvenance ?? current.Provenance
            },
            CortexKnowledgeActionV2.Supersede => record with
            {
                State = CortexKnowledgeStateV2.Superseded,
                SupersededByRecordId = command.ReplacementRecordId
            },
            CortexKnowledgeActionV2.Archive => record with
            {
                State = CortexKnowledgeStateV2.Archived,
                ArchivedAtUtc = now
            },
            _ => throw new InvalidOperationException("The knowledge command is not a mutation action.")
        };
    }

    private async ValueTask<CortexKnowledgeV2> LoadKnowledgeFromReceiptAsync(
        CortexFileV2State state,
        CortexFileV2Receipt receipt,
        CancellationToken cancellationToken)
    {
        var locator = receipt.Results.Single(value => value.RecordType == KnowledgeRecordType);
        var entry = state.RevisionEntry(
            KnowledgeRecordType,
            receipt.Namespace,
            locator.RecordId,
            locator.Revision) ?? throw new InvalidDataException("An idempotency receipt lost its knowledge revision.");
        return await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}
