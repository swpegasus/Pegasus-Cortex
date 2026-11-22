using System.Globalization;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed partial class FileCortexV2Store
{
    public async ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return new CortexStatusV2(
            "Pegasus Cortex file reference",
            "2.0.0",
            "ready",
            "file",
            false,
            false,
            _manifest.ReadNamespaces.Count,
            state.Entries.LongCount(value => value.IsHead),
            state.Entries.Count,
            []);
    }

    public async ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
        CortexNamespacesRequestV2 request,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var ordered = _manifest.ReadNamespaces.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var start = 0;
        if (request.AfterNamespace is not null)
        {
            while (start < ordered.Length &&
                string.CompareOrdinal(ordered[start], request.AfterNamespace) <= 0)
            {
                start++;
            }
        }
        var items = new List<CortexNamespaceV2>();
        var index = start;
        while (index < ordered.Length && items.Count < request.Limit)
        {
            var value = ordered[index];
            var updated = state.Receipts
                .Where(receipt => receipt.Namespace == value)
                .Select(receipt => receipt.CommittedAtUtc)
                .DefaultIfEmpty(DateTimeOffset.UnixEpoch)
                .Max();
            var revision = state.Receipts
                .Where(receipt => receipt.Namespace == value)
                .Select(receipt => receipt.GenerationRevision)
                .DefaultIfEmpty(0)
                .Max();
            var item = new CortexNamespaceV2(value, null, revision, updated, [value]);
            items.Add(item);
            index++;
            var cursor = index < ordered.Length ? value : null;
            var probe = new CortexNamespacesResultV2(items, cursor, cursor is not null, []);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length > request.MaxUtf8Bytes)
            {
                items.RemoveAt(items.Count - 1);
                index--;
                break;
            }
        }
        var hasMore = index < ordered.Length;
        var nextCursor = hasMore && items.Count > 0 ? items[^1].Namespace : null;
        if (hasMore && items.Count == 0)
            throw new InvalidOperationException("The namespace response budget cannot hold one item.");
        return new CortexNamespacesResultV2(items, nextCursor, hasMore, []);
    }

    public async ValueTask<CortexStartResultV2> StartAsync(
        CortexStartRequestV2 request,
        CancellationToken cancellationToken)
    {
        RequireReadNamespaces(request.Namespaces);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var contextCursor = StartContextCursor(
            state.ContextCursor, request.Namespaces, request.Intent, request.TaskId, request.PhaseId);
        if (request.KnownContextCursor == contextCursor)
        {
            return new CortexStartResultV2(
                CortexContextDispositionV2.Unchanged,
                request.Namespaces.ToArray(),
                contextCursor,
                [],
                false,
                null);
        }

        var tokens = SearchTokens(request.Intent);
        CortexTaskSummaryV2? currentTask = null;
        if (request.TaskId is not null)
        {
            foreach (var ns in request.Namespaces)
            {
                var currentEntry = state.Heads(TaskRecordType).SingleOrDefault(value =>
                    value.Namespace == ns && value.RecordId == request.TaskId);
                if (currentEntry is null)
                    continue;
                var task = await LoadTaskAsync(currentEntry, cancellationToken).ConfigureAwait(false);
                if (task.Status == CortexTaskStatusV2.Archived)
                    continue;
                currentTask = TaskSummary(task);
                break;
            }
        }

        var candidates = new List<StartupCandidate>();
        foreach (var entry in state.Heads(TaskRecordType)
            .Where(value => request.Namespaces.Contains(value.Namespace, StringComparer.Ordinal)))
        {
            var task = await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false);
            if (task.Status == CortexTaskStatusV2.Archived ||
                currentTask is not null && task.Namespace == currentTask.Namespace &&
                task.TaskId == currentTask.TaskId && task.Revision == currentTask.Revision)
                continue;
            var score = TextScore(task.Title, task.Content, tokens);
            if (score <= 0)
                continue;
            var item = new CortexContextItemV2(
                task.Namespace,
                TaskRecordType,
                task.TaskId,
                task.Revision,
                task.Title,
                Excerpt(task.Content, CortexV2Limits.MaximumCompactExcerptCharacters),
                null,
                null,
                ToProvenanceReceipt(task.Provenance),
                score + (request.PhaseId is not null &&
                    string.Equals(request.PhaseId, task.PhaseId, StringComparison.Ordinal) ? 1_000 : 0));
            candidates.Add(new(item, StartupTaskPriority(task.Status),
                Array.IndexOf(request.Namespaces.ToArray(), task.Namespace)));
        }
        foreach (var entry in state.Heads(KnowledgeRecordType)
            .Where(value => request.Namespaces.Contains(value.Namespace, StringComparer.Ordinal)))
        {
            var knowledge = await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false);
            if (knowledge.State != CortexKnowledgeStateV2.Active)
                continue;
            var score = TextScore(knowledge.Title, knowledge.Content, tokens);
            if (score <= 0)
                continue;
            var item = new CortexContextItemV2(
                knowledge.Namespace,
                KnowledgeRecordType,
                knowledge.RecordId,
                knowledge.Revision,
                knowledge.Title,
                Excerpt(knowledge.Content, CortexV2Limits.MaximumCompactExcerptCharacters),
                knowledge.Kind,
                knowledge.Tier,
                ToProvenanceReceipt(knowledge.Provenance),
                score);
            candidates.Add(new(item, StartupKnowledgePriority(knowledge),
                Array.IndexOf(request.Namespaces.ToArray(), knowledge.Namespace)));
        }

        var ordered = candidates
            .OrderBy(value => value.Priority)
            .ThenByDescending(value => value.Item.Score)
            .ThenBy(value => value.NamespaceOrder)
            .ThenBy(value => value.Item.RecordType, StringComparer.Ordinal)
            .ThenBy(value => value.Item.RecordId, StringComparer.Ordinal)
            .ThenBy(value => value.Item.Revision)
            .ToArray();
        var selected = new List<CortexContextItemV2>(4);
        var hasMore = false;
        foreach (var candidate in ordered)
        {
            if (selected.Count == 4)
            {
                hasMore = true;
                break;
            }
            selected.Add(candidate.Item);
            var probe = new CortexStartResultV2(
                CortexContextDispositionV2.Snapshot,
                request.Namespaces.ToArray(),
                contextCursor,
                selected,
                selected.Count < ordered.Length,
                currentTask);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length > request.MaxUtf8Bytes)
            {
                selected.RemoveAt(selected.Count - 1);
                hasMore = true;
                break;
            }
        }
        hasMore |= selected.Count < ordered.Length;
        return new CortexStartResultV2(
            CortexContextDispositionV2.Snapshot,
            request.Namespaces.ToArray(),
            contextCursor,
            selected,
            hasMore,
            currentTask);
    }

    public async ValueTask<CortexSearchResultV2> SearchAsync(
        CortexSearchRequestV2 request,
        CancellationToken cancellationToken)
    {
        RequireReadNamespaces(request.Namespaces);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var tokens = SearchTokens(request.Text);
        var hits = new List<CortexSearchHitV2>();
        if (request.Kinds is null)
        {
            foreach (var entry in state.Heads(TaskRecordType)
                .Where(value => request.Namespaces.Contains(value.Namespace, StringComparer.Ordinal)))
            {
                var task = await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false);
                if (task.Status == CortexTaskStatusV2.Archived)
                    continue;
                var score = TextScore(task.Title, task.Content, tokens);
                if (score <= 0)
                    continue;
                hits.Add(new CortexSearchHitV2(
                    task.Namespace,
                    CortexRecordTypeV2.Task,
                    task.TaskId,
                    task.Revision,
                    task.Title,
                    SearchExcerpt(task.Content, request.Format),
                    null,
                    null,
                    ToProvenanceReceipt(task.Provenance),
                    score + (task.Status == CortexTaskStatusV2.Completed ? 20 : 80)));
            }
        }
        foreach (var entry in state.Heads(KnowledgeRecordType)
            .Where(value => request.Namespaces.Contains(value.Namespace, StringComparer.Ordinal)))
        {
            var record = await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false);
            if (record.State != CortexKnowledgeStateV2.Active ||
                request.Kinds is not null && !request.Kinds.Contains(record.Kind))
            {
                continue;
            }
            var score = TextScore(record.Title, record.Content, tokens);
            if (score <= 0)
                continue;
            hits.Add(new CortexSearchHitV2(
                record.Namespace,
                CortexRecordTypeV2.Knowledge,
                record.RecordId,
                record.Revision,
                record.Title,
                SearchExcerpt(record.Content, request.Format),
                record.Kind,
                record.Tier,
                ToProvenanceReceipt(record.Provenance),
                score + KnowledgePriority(record)));
        }
        var ordered = hits
            .OrderByDescending(value => value.Score)
            .ThenBy(value => Array.IndexOf(request.Namespaces.ToArray(), value.Namespace))
            .ThenBy(value => value.RecordId, StringComparer.Ordinal)
            .ToArray();
        var start = DecodeCursor(request.AfterCursor, state.ContextCursor, ordered.Length);
        var selected = new List<CortexSearchHitV2>();
        var hasMore = false;
        var nextIndex = start;
        while (nextIndex < ordered.Length && selected.Count < request.Limit)
        {
            selected.Add(ordered[nextIndex]);
            nextIndex++;
            var cursor = nextIndex < ordered.Length ? EncodeCursor(nextIndex, state.ContextCursor) : null;
            var probe = new CortexSearchResultV2(request.Namespaces.ToArray(), selected, cursor, cursor is not null);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length > request.MaxUtf8Bytes)
            {
                selected.RemoveAt(selected.Count - 1);
                nextIndex--;
                hasMore = true;
                break;
            }
        }
        hasMore |= nextIndex < ordered.Length;
        var nextCursor = hasMore && selected.Count > 0
            ? EncodeCursor(nextIndex, state.ContextCursor)
            : request.AfterCursor is null && selected.Count == 0 && ordered.Length > 0
                ? EncodeCursor(start, state.ContextCursor)
                : null;
        return new CortexSearchResultV2(request.Namespaces.ToArray(), selected, nextCursor, hasMore);
    }

    public async ValueTask<CortexReadResultV2> ReadAsync(
        CortexReadRequestV2 request,
        CancellationToken cancellationToken)
    {
        RequireReadableNamespace(request.Namespace);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var recordType = request.RecordType == CortexRecordTypeV2.Task
            ? TaskRecordType
            : KnowledgeRecordType;
        var candidates = state.Entries.Where(value =>
            value.Namespace == request.Namespace && value.RecordType == recordType &&
            value.RecordId == request.RecordId &&
            (request.Revision is null ? value.IsHead : value.Revision == request.Revision)).ToArray();
        if (candidates.Length == 0)
            throw new KeyNotFoundException("The requested Cortex record does not exist.");
        if (candidates.Length != 1)
            throw new InvalidDataException("The selected Cortex record revision is duplicated.");
        var entry = candidates[0];
        object record = request.RecordType == CortexRecordTypeV2.Task
            ? await LoadTaskAsync(entry, cancellationToken).ConfigureAwait(false)
            : await LoadKnowledgeAsync(entry, cancellationToken).ConfigureAwait(false);
        return new CortexReadResultV2(
            [request.Namespace],
            request.RecordType,
            entry.RecordId,
            entry.Revision,
            record);
    }

    public async ValueTask RefreshHotAsync(CancellationToken cancellationToken = default)
    {
        var start = await StartAsync(new CortexStartRequestV2(
            _manifest.ReadNamespaces,
            "current tasks corrections preferences verified workarounds generalized tenets latest finish",
            null,
            _manifest.HotMaximumUtf8Bytes), cancellationToken).ConfigureAwait(false);
        var items = start.Items.ToList();
        CortexFileHotV2 hot;
        byte[] bytes;
        do
        {
            hot = new CortexFileHotV2(
                CortexFileV2Schemas.Hot,
                true,
                false,
                _manifest.ProjectId,
                start.ContextCursor,
                UtcNow(),
                _manifest.ReadNamespaces,
                items,
                start.HasMore || items.Count < start.Items.Count);
            bytes = JsonSerializer.SerializeToUtf8Bytes(hot, CortexFileV2Json.Options);
            if (bytes.Length <= _manifest.HotMaximumUtf8Bytes)
                break;
            if (items.Count == 0)
                throw new InvalidDataException("The configured HOT.json bound cannot hold its generated metadata.");
            items.RemoveAt(items.Count - 1);
        } while (true);
        await CortexFileV2AtomicIo.WriteAtomicAsync(
            CortexFileV2PathPolicy.HotPath(_projectRoot),
            bytes,
            _manifest.HotMaximumUtf8Bytes,
            Path.Combine(_projectRoot, ".cortex"),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TryRefreshHotAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshHotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The projection is explicitly disposable and non-authoritative.
        }
    }

    private static double KnowledgePriority(CortexKnowledgeV2 value) =>
        (value.Kind switch
        {
            CortexKnowledgeKindV2.Correction => 90,
            CortexKnowledgeKindV2.Preference => 85,
            CortexKnowledgeKindV2.Workaround => 80,
            CortexKnowledgeKindV2.Tenet => 75,
            _ => 40
        }) + (value.Tier switch
        {
            CortexKnowledgeTierV2.Verified => 15,
            CortexKnowledgeTierV2.Explicit => 10,
            _ => 0
        });

    private static IReadOnlyList<string> SearchTokens(string value) => value
        .Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
        .Where(token => token.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Take(64)
        .ToArray();

    private static double TextScore(string title, string content, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return 1;
        var titleNormalized = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var contentNormalized = content.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var matched = 0;
        var score = 0d;
        foreach (var token in tokens)
        {
            if (titleNormalized.Contains(token, StringComparison.Ordinal))
            {
                matched++;
                score += 8;
            }
            else if (contentNormalized.Contains(token, StringComparison.Ordinal))
            {
                matched++;
                score += 3;
            }
        }
        return matched == 0 ? 0 : score + (double)matched / tokens.Count;
    }

    private static string Excerpt(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
            return value;
        var length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    private static string EncodeCursor(int index, string contextCursor) =>
        $"p-{index:D8}-{contextCursor[^8..]}";

    private static string StartContextCursor(
        string generationCursor,
        IReadOnlyList<string> namespaces,
        string intent,
        string? taskId,
        string? phaseId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            selectionVersion = "task-intent-phase-v2",
            generationCursor,
            namespaces,
            normalizedIntent = NormalizeIntent(intent),
            taskId,
            phaseId
        }, CortexFileV2Json.Options);
        return $"ctx-{CortexFileV2AtomicIo.Sha256(bytes)[..32]}";
    }

    private static int StartupTaskPriority(CortexTaskStatusV2 status) => status switch
    {
        CortexTaskStatusV2.Blocked or CortexTaskStatusV2.Claimed or
        CortexTaskStatusV2.Assigned or CortexTaskStatusV2.Open => 3,
        _ => 8
    };

    private static int StartupKnowledgePriority(CortexKnowledgeV2 value)
    {
        if (value.Provenance.Source == CortexProvenanceSourceV2.User &&
            value.Tier == CortexKnowledgeTierV2.Explicit &&
            value.Kind is CortexKnowledgeKindV2.Correction or CortexKnowledgeKindV2.Preference)
        {
            return 2;
        }
        if (value.Tier == CortexKnowledgeTierV2.Verified &&
            value.Kind is CortexKnowledgeKindV2.Failure or CortexKnowledgeKindV2.Regression or CortexKnowledgeKindV2.Workaround)
        {
            return 4;
        }
        if (value.Tier == CortexKnowledgeTierV2.Verified &&
            value.Kind is CortexKnowledgeKindV2.Decision or CortexKnowledgeKindV2.Observation)
        {
            return 5;
        }
        if (value.Tier == CortexKnowledgeTierV2.Inferred && value.Kind == CortexKnowledgeKindV2.Observation)
            return 6;
        if (value.Kind == CortexKnowledgeKindV2.Tenet && value.Namespace.StartsWith("shared.", StringComparison.Ordinal))
            return 7;
        return value.Tier switch
        {
            CortexKnowledgeTierV2.Explicit => 4,
            CortexKnowledgeTierV2.Verified => 6,
            _ => 8
        };
    }

    private static string NormalizeIntent(string value) => string.Join(' ', value
        .Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string SearchExcerpt(string value, CortexResponseFormatV2 format) => Excerpt(
        value,
        format == CortexResponseFormatV2.Compact
            ? CortexV2Limits.MaximumCompactExcerptCharacters
            : 1_024);

    private static CortexProvenanceReceiptV2 ToProvenanceReceipt(CortexProvenanceV2 value) => new(
        value.Source,
        value.Actor,
        value.SourceId,
        value.CapturedAtUtc);

    private static int DecodeCursor(string? cursor, string contextCursor, int maximum)
    {
        if (cursor is null)
            return 0;
        if (cursor.Length != 19 || !cursor.StartsWith("p-", StringComparison.Ordinal) ||
            cursor[10] != '-' || cursor[11..] != contextCursor[^8..] ||
            !int.TryParse(cursor.AsSpan(2, 8), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index < 0 || index > maximum)
        {
            throw new InvalidOperationException("The search cursor is stale or malformed.");
        }
        return index;
    }

    private sealed record StartupCandidate(
        CortexContextItemV2 Item,
        int Priority,
        int NamespaceOrder);
}
