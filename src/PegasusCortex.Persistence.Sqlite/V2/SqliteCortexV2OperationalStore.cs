using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexV2KnowledgeSeed(
    string PackId,
    int PackRevision,
    string Namespace,
    string RecordId,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    CortexProvenanceSourceV2 Source,
    string? SourceRelativePath);

public sealed record SqliteCortexV2KnowledgeSeedAdoption(
    string PackId,
    int PackRevision,
    string Namespace,
    string RecordId,
    long ExpectedRevision,
    string PriorSourceId,
    CortexProvenanceSourceV2 PriorSource);

public sealed record SqliteCortexV2KnowledgeSeedResult(
    IReadOnlyList<string> EffectiveNamespaces,
    int Imported,
    int Revised,
    int Unchanged,
    int Total);

/// <summary>
/// Domain adapter from the public lean v2 contract to the fresh-generation
/// SQLite store. The adapter owns and disposes the supplied low-level store.
/// Every call is self-contained; it retains no namespace or cursor state.
/// </summary>
public sealed partial class SqliteCortexV2OperationalStore :
    ICortexV2Store,
    ICortexKnowledgeSpaceMapper,
    IAsyncDisposable
{
    public string CanonicalizeNamespace(string value) =>
        CortexKnowledgeSpaces.Canonicalize(value);

    public IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values) =>
        CortexKnowledgeSpaces.Canonicalize(values);
    private const string TaskKind = "task";
    private const string KnowledgeKind = "knowledge";
    private const string StartupSelectionVersion = "task-intent-phase-v2";
    private const int MaximumStartupItems = 4;
    private const int StartupSearchLimit = 32;
    private const int MaximumSearchCandidates = 256;
    private readonly SqliteCortexV2Store _store;
    private readonly string _version;

    public SqliteCortexV2OperationalStore(SqliteCortexV2Store store, string version)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsControl))
            throw new ArgumentException("A bounded Cortex product version is required.", nameof(version));
        _version = version;
    }

    public async ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken)
    {
        var quick = await _store.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
        var metrics = await _store.GetCatalogCountersAsync(cancellationToken).ConfigureAwait(false);
        var namespaces = await LogicalNamespacesAsync(cancellationToken).ConfigureAwait(false);
        return new(
            "Pegasus Cortex",
            _version,
            quick.Healthy ? "ready" : quick.State,
            "sqlite-v2",
            Network: false,
            Service: false,
            namespaces.Count,
            metrics.HeadRows,
            metrics.RevisionRows,
            EffectiveNamespaces: []);
    }

    public async ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
        CortexNamespacesRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var namespaces = await LogicalNamespacesAsync(cancellationToken).ConfigureAwait(false);
        var after = request.AfterNamespace is null
            ? null
            : CortexKnowledgeSpaces.Canonicalize(request.AfterNamespace);
        var page = namespaces
            .Where(value => after is null || string.CompareOrdinal(value.Name, after) > 0)
            .Take(request.Limit + 1)
            .ToList();
        var hasMore = page.Count > request.Limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        var items = page.Select(value => new CortexNamespaceV2(
            value.Name,
            DisplayName: CortexKnowledgeSpaces.DisplayPath(value.Name),
            Revision: value.RevisionCount,
            value.UpdatedAtUtc,
            EffectiveNamespaces: [value.Name])).ToArray();
        return new(
            items,
            hasMore && page.Count > 0 ? page[^1].Name : null,
            hasMore,
            EffectiveNamespaces: []);
    }

    public async ValueTask<CortexStartResultV2> StartAsync(
        CortexStartRequestV2 request,
        CancellationToken cancellationToken)
    {
        var cursor = await StartContextCursorAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.Equals(cursor, request.KnownContextCursor, StringComparison.Ordinal))
        {
            return new(
                CortexContextDispositionV2.Unchanged,
                request.Namespaces,
                cursor,
                Items: [],
                HasMore: false,
                CurrentTask: null);
        }

        CortexTaskSummaryV2? currentTask = null;
        if (request.TaskId is not null)
        {
            foreach (var ns in request.Namespaces)
            {
                var projection = await ReadHeadProjectionLogicalAsync(
                    ns, TaskKind, request.TaskId, cancellationToken).ConfigureAwait(false);
                if (projection is null || !projection.Searchable)
                    continue;
                var metadata = ParseTaskMetadata(projection.AttributesJson);
                if (metadata.Status == CortexTaskStatusV2.Archived)
                    continue;
                currentTask = ToTaskSummary(projection, metadata);
                break;
            }
        }

        var candidates = new List<StartupCandidate>();
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        var hasMore = false;
        for (var namespaceOrder = 0; namespaceOrder < request.Namespaces.Count; namespaceOrder++)
        {
            var ns = request.Namespaces[namespaceOrder];
            var kinds = CortexKnowledgeSpaces.IsGeometric(ns)
                ? new[] { TaskKind }
                : new[] { TaskKind, KnowledgeKind };
            foreach (var kind in kinds)
            {
                var hits = await SearchLogicalHeadsAsync(
                    ns, request.Intent, kind, StartupSearchLimit, minimumMatch: 1, cancellationToken)
                    .ConfigureAwait(false);
                hasMore |= hits.Count == StartupSearchLimit;
                foreach (var hit in hits)
                {
                    if (currentTask is not null && hit.Kind == TaskKind &&
                        string.Equals(hit.Namespace, currentTask.Namespace, StringComparison.Ordinal) &&
                        string.Equals(hit.RecordId, currentTask.TaskId, StringComparison.Ordinal) &&
                        hit.Revision == currentTask.Revision)
                    {
                        continue;
                    }
                    var coordinate = $"{hit.Namespace}\n{hit.Kind}\n{hit.RecordId}\n{hit.Revision}";
                    if (!coordinates.Add(coordinate))
                        continue;
                    var candidate = StartupContextItem(hit, namespaceOrder, request.PhaseId);
                    if (candidate is not null)
                        candidates.Add(candidate);
                }
            }
        }

        var geometricNamespaces = request.Namespaces
            .Where(CortexKnowledgeSpaces.IsGeometric)
            .ToArray();
        if (geometricNamespaces.Length > 0)
        {
            var hits = await SearchGeometricKnowledgeAsync(
                geometricNamespaces,
                request.Intent,
                StartupSearchLimit,
                MaximumStartupItems,
                cancellationToken).ConfigureAwait(false);
            hasMore |= hits.Count == StartupSearchLimit;
            foreach (var hit in hits)
            {
                var coordinate = $"{hit.Namespace}\n{hit.Kind}\n{hit.RecordId}\n{hit.Revision}";
                if (!coordinates.Add(coordinate))
                    continue;
                var namespaceOrder = Array.IndexOf(request.Namespaces.ToArray(), hit.Namespace);
                var candidate = StartupContextItem(
                    hit,
                    namespaceOrder < 0 ? request.Namespaces.Count : namespaceOrder,
                    request.PhaseId);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
        }

        var ordered = candidates
            .OrderBy(value => value.Priority)
            .ThenByDescending(value => value.Item.Score)
            .ThenBy(value => value.NamespaceOrder)
            .ThenBy(value => value.Item.RecordType, StringComparer.Ordinal)
            .ThenBy(value => value.Item.RecordId, StringComparer.Ordinal)
            .ThenBy(value => value.Item.Revision)
            .ToArray();
        var selected = new List<CortexContextItemV2>(MaximumStartupItems);
        foreach (var candidate in ordered)
        {
            if (selected.Count == MaximumStartupItems)
            {
                hasMore = true;
                break;
            }
            selected.Add(candidate.Item);
            var probe = new CortexStartResultV2(
                CortexContextDispositionV2.Snapshot,
                request.Namespaces,
                cursor,
                selected,
                hasMore || selected.Count < ordered.Length,
                currentTask);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length <= request.MaxUtf8Bytes)
                continue;
            selected.RemoveAt(selected.Count - 1);
            hasMore = true;
            break;
        }
        hasMore |= selected.Count < ordered.Length;
        return new(
            CortexContextDispositionV2.Snapshot,
            request.Namespaces,
            cursor,
            selected,
            hasMore,
            currentTask);
    }

    public async ValueTask<CortexSearchResultV2> SearchAsync(
        CortexSearchRequestV2 request,
        CancellationToken cancellationToken)
    {
        var values = new List<CortexSearchHitV2>();
        void AddHit(SqliteCortexV2SearchHit hit)
        {
            var ns = hit.Namespace;
            if (hit.Kind == TaskKind)
            {
                var metadata = ParseTaskMetadata(hit.AttributesJson);
                if (metadata.Status == CortexTaskStatusV2.Archived)
                    return;
                values.Add(new(
                    ns,
                    CortexRecordTypeV2.Task,
                    hit.RecordId,
                    hit.Revision,
                    hit.Title,
                    SearchExcerpt(hit.Excerpt, request.Format),
                    Kind: null,
                    Tier: null,
                    ToProvenanceReceipt(metadata.Provenance),
                    hit.Score));
                return;
            }

            var knowledge = ParseKnowledgeMetadata(hit.AttributesJson);
            if (knowledge.State != CortexKnowledgeStateV2.Active ||
                request.Kinds is not null && !request.Kinds.Contains(knowledge.Kind))
            {
                return;
            }
            values.Add(new(
                ns,
                CortexRecordTypeV2.Knowledge,
                hit.RecordId,
                hit.Revision,
                hit.Title,
                SearchExcerpt(hit.Excerpt, request.Format),
                knowledge.Kind,
                knowledge.Tier,
                ToProvenanceReceipt(knowledge.Provenance),
                hit.Score));
        }

        var geometricNamespaces = request.Namespaces
            .Where(CortexKnowledgeSpaces.IsGeometric)
            .ToArray();
        foreach (var ns in request.Namespaces)
        {
            if (CortexKnowledgeSpaces.IsGeometric(ns))
            {
                if (request.Kinds is null)
                {
                    var taskHits = await SearchLogicalHeadsAsync(
                        ns,
                        request.Text,
                        TaskKind,
                        MaximumSearchCandidates,
                        minimumMatch: 1,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var hit in taskHits)
                        AddHit(hit);
                }
                continue;
            }

            var hits = await SearchLogicalHeadsAsync(
                ns,
                request.Text,
                request.Kinds is null ? null : KnowledgeKind,
                MaximumSearchCandidates,
                minimumMatch: 1,
                cancellationToken).ConfigureAwait(false);
            foreach (var hit in hits)
                AddHit(hit);
        }

        if (geometricNamespaces.Length > 0)
        {
            var candidateLimit = Math.Min(
                MaximumSearchCandidates,
                Math.Max(StartupSearchLimit, request.Limit * 8));
            var geometricHits = await SearchGeometricKnowledgeAsync(
                geometricNamespaces,
                request.Text,
                candidateLimit,
                request.Limit,
                cancellationToken).ConfigureAwait(false);
            foreach (var hit in geometricHits)
                AddHit(hit);
        }

        var ordered = values
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.RecordId, StringComparer.Ordinal)
            .ToArray();
        var after = request.AfterCursor;
        if (after is not null)
            ordered = ordered.SkipWhile(value => EncodeCursor(CursorKey(value)) != after).Skip(1).ToArray();
        var selected = new List<CortexSearchHitV2>(request.Limit);
        var nextIndex = 0;
        while (nextIndex < ordered.Length && selected.Count < request.Limit)
        {
            selected.Add(ordered[nextIndex]);
            nextIndex++;
            var probeHasMore = nextIndex < ordered.Length;
            var probeCursor = probeHasMore ? EncodeCursor(CursorKey(selected[^1])) : null;
            var probe = new CortexSearchResultV2(
                request.Namespaces, selected, probeCursor, probeHasMore);
            if (JsonSerializer.SerializeToUtf8Bytes(probe, CortexV2Json.Options).Length <= request.MaxUtf8Bytes)
                continue;
            selected.RemoveAt(selected.Count - 1);
            nextIndex--;
            break;
        }
        var hasMore = nextIndex < ordered.Length;
        var nextCursor = hasMore && selected.Count > 0
            ? EncodeCursor(CursorKey(selected[^1]))
            : hasMore
                ? request.AfterCursor
                : null;
        return new(
            request.Namespaces,
            selected,
            nextCursor,
            hasMore);
    }

    public async ValueTask<CortexReadResultV2> ReadAsync(
        CortexReadRequestV2 request,
        CancellationToken cancellationToken)
    {
        var record = await ReadTypedAsync(
            request.Namespace, request.RecordType, request.RecordId, request.Revision, cancellationToken).ConfigureAwait(false);
        return new(
            [request.Namespace],
            request.RecordType,
            request.RecordId,
            record.Revision,
            record.Kind == TaskKind ? ToTask(record) : ToKnowledge(record));
    }

    public async ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken)
    {
        if (command.Action == CortexTaskActionV2.List)
        {
            var page = await _store.ListHeadsAsync(
                command.Namespace, TaskKind, command.AfterTaskId, command.Limit, cancellationToken,
                searchableOnly: true)
                .ConfigureAwait(false);
            return new(
                [command.Namespace],
                "listed",
                Task: null,
                page.Items.Select(ToTaskSummary).ToArray(),
                History: null,
                page.NextAfterRecordId);
        }

        if (command.Action == CortexTaskActionV2.Get)
        {
            var record = await RequireHeadAsync(command.Namespace, TaskKind, command.TaskId!, cancellationToken)
                .ConfigureAwait(false);
            return new([command.Namespace], "read", ToTaskSummary(record), [], History: null, NextCursor: null);
        }

        if (command.Action == CortexTaskActionV2.History)
        {
            var first = command.FirstRevision!.Value;
            var last = command.LastRevision!.Value;
            var history = await _store.ReadHistoryAsync(
                command.Namespace,
                TaskKind,
                command.TaskId!,
                afterRevision: first - 1,
                limit: checked((int)(last - first + 1)),
                cancellationToken).ConfigureAwait(false);
            var previousContentHash = first > 1
                ? (await _store.ReadRevisionAsync(
                    command.Namespace, TaskKind, command.TaskId!, first - 1, cancellationToken).ConfigureAwait(false))?.ContentHash
                : null;
            var events = new List<CortexTaskHistoryEventV2>(history.Items.Count);
            foreach (var item in history.Items)
            {
                var metadata = ParseTaskMetadata(item.AttributesJson);
                events.Add(new(
                    command.Namespace,
                    command.TaskId!,
                    item.Revision,
                    metadata.Status,
                    item.Title,
                    item.EventType,
                    item.Actor,
                    item.OccurredAtUtc,
                    !string.Equals(previousContentHash, item.ContentHash, StringComparison.Ordinal),
                    item.ContentHash,
                    metadata.PhaseId,
                    metadata.CheckpointSubject, metadata.Provenance.VerifiedSource));
                previousContentHash = item.ContentHash;
            }
            return new([command.Namespace], "history", Task: null, Items: [], events, NextCursor: null);
        }

        var provenance = command.EffectiveProvenance ??
            throw new InvalidDataException("A Cortex v2 task write requires Core-stamped effective provenance.");
        var existing = command.Action == CortexTaskActionV2.Create
            ? null
            : await RequireHeadAsync(command.Namespace, TaskKind, command.TaskId!, cancellationToken)
                .ConfigureAwait(false);
        if (existing is not null && existing.Revision == command.ExpectedRevision)
            EnsureTaskTransitionAllowed(ToTask(existing), command.Action);
        var task = ApplyTaskCommand(command, existing is null ? null : ToTask(existing), provenance);
        EnsureResponseFits(
            new CortexTaskResultV2(
                [command.Namespace],
                SqliteCortexV2WriteDispositions.Appended,
                ToTaskSummary(task),
                Items: [],
                History: null,
                NextCursor: null),
            command.MaxUtf8Bytes);
        var append = TaskAppend(task, command.IdempotencyKey!, command.Action.ToString(),
            command.Action == CortexTaskActionV2.Create ? 0 : command.ExpectedRevision!.Value,
            contentChanged: command.Action == CortexTaskActionV2.Create || command.Content is not null,
            content: command.Action == CortexTaskActionV2.Create || command.Content is not null
                ? Encoding.UTF8.GetBytes(task.Content)
                : null,
            idempotencyFingerprint: Fingerprint(JsonSerializer.Serialize(command, CortexV2Json.Options)));
        var result = await _store.AppendAsync(append, cancellationToken).ConfigureAwait(false);
        var stored = result.Disposition is SqliteCortexV2WriteDispositions.Appended or SqliteCortexV2WriteDispositions.Idempotent
            ? await _store.ReadRevisionAsync(
                command.Namespace,
                TaskKind,
                command.TaskId!,
                result.Revision!.Value,
                cancellationToken).ConfigureAwait(false) ??
              throw new InvalidDataException("The committed Cortex v2 task revision is unavailable.")
            : existing;
        return new(
            [command.Namespace],
            result.Disposition,
            stored is null ? null : ToTaskSummary(stored),
            Items: [],
            History: null,
            NextCursor: null);
    }

    public async ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken)
    {
        if (command.Action == CortexKnowledgeActionV2.Analyze)
            return await AnalyzeKnowledgeAsync(command, cancellationToken).ConfigureAwait(false);

        if (command.Action == CortexKnowledgeActionV2.Read)
        {
            var read = await RequireHeadAsync(command.Namespace, KnowledgeKind, command.RecordId!, cancellationToken)
                .ConfigureAwait(false);
            return new([command.Namespace], "read", ToKnowledgeReceipt(ToKnowledge(read)));
        }

        var provenance = command.EffectiveProvenance ??
            throw new InvalidDataException("A Cortex v2 knowledge write requires Core-stamped effective provenance.");
        var existing = command.Action == CortexKnowledgeActionV2.Create
            ? null
            : await RequireHeadAsync(command.Namespace, KnowledgeKind, command.RecordId!, cancellationToken)
                .ConfigureAwait(false);
        if (existing is not null && existing.Revision == command.ExpectedRevision)
            EnsureKnowledgeTransitionAllowed(ToKnowledge(existing), command.Action);
        var knowledge = ApplyKnowledgeCommand(command, existing is null ? null : ToKnowledge(existing), provenance);
        EnsureResponseFits(
            new CortexKnowledgeResultV2(
                [command.Namespace],
                SqliteCortexV2WriteDispositions.Appended,
                ToKnowledgeReceipt(knowledge)),
            command.MaxUtf8Bytes);
        var append = KnowledgeAppend(
            knowledge,
            command.IdempotencyKey!,
            command.Action.ToString(),
            command.Action == CortexKnowledgeActionV2.Create ? 0 : command.ExpectedRevision!.Value,
            contentChanged: command.Action == CortexKnowledgeActionV2.Create || command.Content is not null,
            content: command.Action == CortexKnowledgeActionV2.Create || command.Content is not null
                ? Encoding.UTF8.GetBytes(knowledge.Content)
                : null,
            idempotencyFingerprint: Fingerprint(JsonSerializer.Serialize(command, CortexV2Json.Options)));
        var result = await _store.AppendAsync(append, cancellationToken).ConfigureAwait(false);
        var stored = result.Disposition is SqliteCortexV2WriteDispositions.Appended or SqliteCortexV2WriteDispositions.Idempotent
            ? await _store.ReadRevisionAsync(
                command.Namespace,
                KnowledgeKind,
                command.RecordId!,
                result.Revision!.Value,
                cancellationToken).ConfigureAwait(false) ??
              throw new InvalidDataException("The committed Cortex v2 knowledge revision is unavailable.")
            : existing;
        return new(
            [command.Namespace],
            result.Disposition,
            stored is null ? null : ToKnowledgeReceipt(ToKnowledge(stored)));
    }

    /// <summary>
    /// Atomically admits a bounded portable starter corpus. Exact active heads
    /// are unchanged. A higher pack revision may revise a head owned by that
    /// exact pack; every foreign, ambiguous, or non-increasing conflict rejects
    /// the entire set before any record is changed.
    /// </summary>
    public async ValueTask<SqliteCortexV2KnowledgeSeedResult> ImportKnowledgeSeedsAsync(
        IReadOnlyList<SqliteCortexV2KnowledgeSeed> seeds,
        string actor,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        IReadOnlyList<SqliteCortexV2KnowledgeSeedAdoption>? adoptions = null)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (seeds.Count is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(seeds));
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > CortexV2Limits.MaximumIdentifierCharacters ||
            actor.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded starter-corpus actor is required.", nameof(actor));
        }
        capturedAtUtc = capturedAtUtc.ToUniversalTime();
        var duplicate = seeds
            .GroupBy(value => $"{value.Namespace}\n{value.RecordId}", StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("Starter corpus record coordinates must be unique.");

        var adoptionByCoordinate = (adoptions ?? [])
            .ToDictionary(value => $"{value.Namespace}\n{value.RecordId}", StringComparer.Ordinal);
        if (adoptionByCoordinate.Count != (adoptions?.Count ?? 0) || adoptionByCoordinate.Count > seeds.Count)
            throw new InvalidDataException("Behavior-pack adoption coordinates must be unique and bounded by the pack.");
        foreach (var adoption in adoptionByCoordinate.Values)
        {
            if (adoption.ExpectedRevision < 1 || string.IsNullOrWhiteSpace(adoption.PriorSourceId) ||
                adoption.PriorSourceId.Length > CortexV2Limits.MaximumIdentifierCharacters ||
                adoption.PriorSourceId.Any(char.IsControl) ||
                !seeds.Any(seed => seed.PackId == adoption.PackId && seed.PackRevision == adoption.PackRevision &&
                    seed.Namespace == adoption.Namespace && seed.RecordId == adoption.RecordId))
                throw new InvalidDataException("A behavior-pack adoption entry does not exactly bind to this pack generation.");
        }
        var consumedAdoptions = new HashSet<string>(StringComparer.Ordinal);

        var appends = new List<SqliteCortexV2AppendRequest>(seeds.Count);
        var appendRevises = new List<bool>(seeds.Count);
        var unchanged = 0;
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await _store.ReadHeadAsync(
                seed.Namespace,
                KnowledgeKind,
                seed.RecordId,
                cancellationToken).ConfigureAwait(false);
            CortexKnowledgeV2 knowledge;
            long expectedRevision = 0;
            var revises = false;
            if (existing is not null)
            {
                var current = ToKnowledge(existing);
                var coordinate = $"{seed.Namespace}\n{seed.RecordId}";
                var hasAdoption = adoptionByCoordinate.TryGetValue(coordinate, out var adoption);
                if (hasAdoption)
                {
                    if (current.State != CortexKnowledgeStateV2.Active ||
                        current.Revision != adoption!.ExpectedRevision ||
                        current.Provenance.Source != adoption.PriorSource ||
                        !string.Equals(current.Provenance.SourceId, adoption.PriorSourceId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Starter record '{seed.Namespace}/{seed.RecordId}' no longer matches its explicit adoption preimage.");
                    }
                    consumedAdoptions.Add(coordinate);
                }
                var exact = current.State == CortexKnowledgeStateV2.Active &&
                    current.Kind == seed.Kind &&
                    current.Tier == seed.Tier &&
                    string.Equals(current.Title, seed.Title, StringComparison.Ordinal) &&
                    string.Equals(current.Content, seed.Content, StringComparison.Ordinal) &&
                    current.Tags.Order(StringComparer.Ordinal).SequenceEqual(
                        seed.Tags.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal);
                if (exact && !hasAdoption)
                {
                    unchanged++;
                    continue;
                }

                var currentPackRevision = ManagedPackRevision(current.Provenance, seed.PackId);
                if (!hasAdoption && (current.State != CortexKnowledgeStateV2.Active ||
                    currentPackRevision is null ||
                    seed.PackRevision <= currentPackRevision.Value))
                {
                    throw new InvalidDataException(
                        $"Starter record '{seed.Namespace}/{seed.RecordId}' conflicts with an existing local head.");
                }
                expectedRevision = current.Revision;
                revises = true;
                knowledge = new CortexKnowledgeV2(
                    seed.Namespace,
                    seed.RecordId,
                    current.Revision + 1,
                    CortexKnowledgeStateV2.Active,
                    seed.Kind,
                    seed.Tier,
                    seed.Title,
                    seed.Content,
                    seed.Tags.ToArray(),
                    SeedProvenance(seed, actor, capturedAtUtc),
                    SupersededByRecordId: null,
                    current.CreatedAtUtc,
                    UpdatedAtUtc: capturedAtUtc,
                    ArchivedAtUtc: null);
            }
            else
            {
                if (adoptionByCoordinate.ContainsKey($"{seed.Namespace}\n{seed.RecordId}"))
                    throw new InvalidDataException(
                        $"Starter record '{seed.Namespace}/{seed.RecordId}' has no head matching its explicit adoption preimage.");
                knowledge = new CortexKnowledgeV2(
                    seed.Namespace,
                    seed.RecordId,
                    Revision: 1,
                    CortexKnowledgeStateV2.Active,
                    seed.Kind,
                    seed.Tier,
                    seed.Title,
                    seed.Content,
                    seed.Tags.ToArray(),
                    SeedProvenance(seed, actor, capturedAtUtc),
                    SupersededByRecordId: null,
                    CreatedAtUtc: capturedAtUtc,
                    UpdatedAtUtc: capturedAtUtc,
                    ArchivedAtUtc: null);
            }
            var fingerprint = Fingerprint(
                seed.PackId,
                seed.PackRevision.ToString(CultureInfo.InvariantCulture),
                seed.Namespace,
                seed.RecordId,
                seed.Kind.ToString(),
                seed.Tier.ToString(),
                seed.Title,
                seed.Content,
                string.Join('\n', seed.Tags),
                seed.Source.ToString(),
                seed.SourceRelativePath ?? string.Empty);
            appends.Add(KnowledgeAppend(
                knowledge,
                $"starter.{fingerprint[..48]}",
                "StarterSeed",
                expectedRevision,
                contentChanged: true,
                Encoding.UTF8.GetBytes(seed.Content),
                fingerprint));
            appendRevises.Add(revises);
        }

        if (consumedAdoptions.Count != adoptionByCoordinate.Count)
            throw new InvalidDataException("A behavior-pack adoption entry was not consumed by its exact predecessor head.");

        var imported = 0;
        var revised = 0;
        if (appends.Count > 0)
        {
            var batch = await _store.AppendBatchAsync(appends, cancellationToken).ConfigureAwait(false);
            if (!batch.Committed || batch.Items.Count != appends.Count ||
                batch.Items.Any(value => value.Disposition is not
                    (SqliteCortexV2WriteDispositions.Appended or SqliteCortexV2WriteDispositions.Idempotent)))
            {
                throw new InvalidDataException(
                    $"The starter corpus was rejected atomically with disposition '{batch.Disposition}'.");
            }
            for (var index = 0; index < batch.Items.Count; index++)
            {
                if (batch.Items[index].Disposition == SqliteCortexV2WriteDispositions.Idempotent)
                    unchanged++;
                else if (appendRevises[index])
                    revised++;
                else
                    imported++;
            }
        }

        foreach (var seed in seeds)
        {
            var stored = await _store.ReadHeadAsync(
                seed.Namespace,
                KnowledgeKind,
                seed.RecordId,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("A committed starter record is unavailable.");
            var exact = ToKnowledge(stored);
            if (exact.State != CortexKnowledgeStateV2.Active ||
                exact.Kind != seed.Kind ||
                exact.Tier != seed.Tier ||
                !string.Equals(exact.Title, seed.Title, StringComparison.Ordinal) ||
                !string.Equals(exact.Content, seed.Content, StringComparison.Ordinal) ||
                !exact.Tags.Order(StringComparer.Ordinal).SequenceEqual(
                    seed.Tags.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException("Starter corpus exact readback failed.");
            }
        }

        return new(
            seeds.Select(value => value.Namespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            imported,
            revised,
            unchanged,
            seeds.Count);
    }

    private static CortexProvenanceV2 SeedProvenance(
        SqliteCortexV2KnowledgeSeed seed,
        string actor,
        DateTimeOffset capturedAtUtc) => new(
            seed.Source,
            actor,
            seed.PackId,
            $"pack-revision={seed.PackRevision};source={seed.SourceRelativePath ?? string.Empty}",
            capturedAtUtc);

    private static int? ManagedPackRevision(CortexProvenanceV2 provenance, string packId)
    {
        if (!string.Equals(provenance.SourceId, packId, StringComparison.Ordinal))
            return null;
        const string prefix = "pack-revision=";
        if (provenance.Evidence is null || !provenance.Evidence.StartsWith(prefix, StringComparison.Ordinal))
            return 0;
        var separator = provenance.Evidence.IndexOf(';', prefix.Length);
        var value = separator < 0
            ? provenance.Evidence[prefix.Length..]
            : provenance.Evidence[prefix.Length..separator];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) &&
            revision is >= 0 and <= 1_000_000
                ? revision
                : null;
    }

    public async ValueTask<CortexFinishResultV2> FinishAsync(
        CortexFinishRequestV2 request,
        CancellationToken cancellationToken)
    {
        var provenance = request.EffectiveProvenance ??
            throw new InvalidDataException("A Cortex v2 finish requires Core-stamped effective provenance.");
        var taskRecord = await RequireHeadAsync(request.Namespace, TaskKind, request.TaskId, cancellationToken)
            .ConfigureAwait(false);
        var task = ToTask(taskRecord);
        if (taskRecord.Revision == request.ExpectedRevision && task.Status == CortexTaskStatusV2.Archived)
            throw new InvalidOperationException("A Cortex v2 finish cannot mutate an archived task.");
        var updatedTask = task with
        {
            Revision = task.Revision + 1,
            Title = request.TaskTitle ?? task.Title,
            Content = request.TaskContent ?? task.Content,
            PhaseId = request.PhaseId ?? task.PhaseId,
            CheckpointSubject = request.CheckpointSubject ?? task.CheckpointSubject,
            UpdatedAtUtc = provenance.CapturedAtUtc,
            Provenance = provenance
        };
        task = request.TaskAction switch
        {
            CortexFinishTaskActionV2.None or CortexFinishTaskActionV2.Update => updatedTask,
            CortexFinishTaskActionV2.Block => updatedTask with
            {
                Status = CortexTaskStatusV2.Blocked,
                BlockedReason = request.BlockedReason,
                CompletedAtUtc = null
            },
            CortexFinishTaskActionV2.Complete => updatedTask with
            {
                Status = CortexTaskStatusV2.Completed,
                BlockedReason = null,
                CompletedAtUtc = provenance.CapturedAtUtc
            },
            _ => throw new ArgumentOutOfRangeException(nameof(request.TaskAction))
        };

        var taskAttributes = JsonSerializer.Serialize(new TaskMetadata(
            task.Status, task.Assignee, task.ClaimedBy, task.Dependencies, task.BlockedReason,
            task.CreatedAtUtc, task.UpdatedAtUtc, task.CompletedAtUtc, task.ArchivedAtUtc,
            task.Provenance, request.Checkpoint, request.Evidence, request.Handoff,
            task.PhaseId, task.CheckpointSubject), CortexV2Json.Options);
        var finishFingerprint = JsonSerializer.Serialize(request, CortexV2Json.Options);
        var prospectiveReceipts = request.Knowledge.Select(value => new CortexKnowledgeReceiptV2(
            request.Namespace,
            value.RecordId,
            (value.ExpectedRevision ?? 0) + 1,
            CortexKnowledgeStateV2.Active,
            value.Kind,
            value.Tier,
            Truncate(value.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
            new CortexProvenanceReceiptV2(
                value.EffectiveProvenance!.Source,
                value.EffectiveProvenance.Actor,
                value.EffectiveProvenance.SourceId,
                value.EffectiveProvenance.CapturedAtUtc))).ToArray();
        EnsureResponseFits(
            new CortexFinishResultV2(
                [request.Namespace],
                SqliteCortexV2WriteDispositions.Appended,
                ToTaskSummary(task),
                prospectiveReceipts,
                ContextCursor: new string('0', 32)),
            request.MaxUtf8Bytes);
        var appends = new List<SqliteCortexV2AppendRequest>(request.Knowledge.Count + 1)
        {
            TaskAppend(
                task,
                request.IdempotencyKey,
                $"finish-{request.TaskAction.ToString().ToLowerInvariant()}",
                request.ExpectedRevision,
                contentChanged: request.TaskContent is not null,
                content: request.TaskContent is null ? null : Encoding.UTF8.GetBytes(task.Content),
                attributesOverride: taskAttributes,
                idempotencyFingerprint: Fingerprint("task", finishFingerprint))
        };
        foreach (var value in request.Knowledge)
        {
            var stamped = value.EffectiveProvenance ??
                throw new InvalidDataException("A Cortex v2 finish knowledge write lacks effective provenance.");
            CortexKnowledgeV2? prior = null;
            if (value.ExpectedRevision is not null)
            {
                prior = ToKnowledge(await RequireHeadAsync(
                    request.Namespace, KnowledgeKind, value.RecordId, cancellationToken).ConfigureAwait(false));
                if (prior.Revision == value.ExpectedRevision && prior.State != CortexKnowledgeStateV2.Active)
                    throw new InvalidOperationException("A Cortex v2 finish can revise only active knowledge.");
            }
            var knowledge = new CortexKnowledgeV2(
                request.Namespace,
                value.RecordId,
                (value.ExpectedRevision ?? 0) + 1,
                CortexKnowledgeStateV2.Active,
                value.Kind,
                value.Tier,
                value.Title,
                value.Content,
                value.Tags,
                stamped,
                SupersededByRecordId: null,
                CreatedAtUtc: prior?.CreatedAtUtc ?? stamped.CapturedAtUtc,
                UpdatedAtUtc: stamped.CapturedAtUtc,
                ArchivedAtUtc: null);
            appends.Add(KnowledgeAppend(
                knowledge,
                value.IdempotencyKey,
                "finish-create-or-revise",
                value.ExpectedRevision ?? 0,
                contentChanged: true,
                Encoding.UTF8.GetBytes(value.Content),
                idempotencyFingerprint: Fingerprint("knowledge", value.RecordId, finishFingerprint)));
        }

        var batch = await _store.AppendBatchAsync(appends, cancellationToken).ConfigureAwait(false);
        if (!batch.Committed)
        {
            return new(
                [request.Namespace],
                batch.Disposition,
                ToTaskSummary(taskRecord),
                Knowledge: [],
                await ContextCursorAsync([request.Namespace], cancellationToken).ConfigureAwait(false));
        }

        var taskWrite = batch.Items[0];
        var storedTask = await _store.ReadRevisionAsync(
            request.Namespace,
            TaskKind,
            request.TaskId,
            taskWrite.Revision!.Value,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The committed Cortex v2 finish task revision is unavailable.");
        var storedKnowledge = new List<CortexKnowledgeReceiptV2>(request.Knowledge.Count);
        for (var index = 0; index < request.Knowledge.Count; index++)
        {
            var value = request.Knowledge[index];
            var write = batch.Items[index + 1];
            var record = await _store.ReadRevisionAsync(
                request.Namespace,
                KnowledgeKind,
                value.RecordId,
                write.Revision!.Value,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("A committed Cortex v2 finish knowledge revision is unavailable.");
            storedKnowledge.Add(ToKnowledgeReceipt(ToKnowledge(record)));
        }
        return new(
            [request.Namespace],
            batch.Disposition,
            ToTaskSummary(storedTask),
            storedKnowledge.AsReadOnly(),
            await ContextCursorAsync([request.Namespace], cancellationToken).ConfigureAwait(false));
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private async ValueTask<SqliteCortexV2Record> ReadTypedAsync(
        string ns,
        CortexRecordTypeV2 recordType,
        string recordId,
        long? revision,
        CancellationToken cancellationToken)
    {
        var kind = recordType switch
        {
            CortexRecordTypeV2.Task => TaskKind,
            CortexRecordTypeV2.Knowledge => KnowledgeKind,
            _ => throw new ArgumentOutOfRangeException(nameof(recordType))
        };
        foreach (var source in CortexKnowledgeSpaces.ReadSources(ns))
        {
            var record = revision is null
                ? await _store.ReadHeadAsync(source, kind, recordId, cancellationToken).ConfigureAwait(false)
                : await _store.ReadRevisionAsync(source, kind, recordId, revision.Value, cancellationToken)
                    .ConfigureAwait(false);
            if (record is not null)
                return record with { Namespace = ns };
        }
        throw new KeyNotFoundException(
            "The requested typed Cortex v2 record does not exist in the explicit namespace.");
    }

    private async ValueTask<SqliteCortexV2Record> RequireHeadAsync(
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken) =>
        await _store.ReadHeadAsync(ns, kind, recordId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException("The requested Cortex v2 record does not exist in the explicit namespace.");

    private static CortexTaskV2 ApplyTaskCommand(
        CortexTaskCommandV2 command,
        CortexTaskV2? existing,
        CortexProvenanceV2 provenance)
    {
        if (command.Action == CortexTaskActionV2.Create)
        {
            return new(
                command.Namespace, command.TaskId!, 1,
                command.Assignee is null ? CortexTaskStatusV2.Open : CortexTaskStatusV2.Assigned,
                command.Title!, command.Content!, command.Assignee, ClaimedBy: null,
                command.Dependencies ?? [], BlockedReason: null,
                provenance.CapturedAtUtc, provenance.CapturedAtUtc,
                CompletedAtUtc: null, ArchivedAtUtc: null, provenance,
                command.PhaseId, command.CheckpointSubject);

        }

        var value = existing ?? throw new InvalidDataException("A task mutation requires a current task head.");
        var revision = value.Revision + 1;
        var updated = command.Action switch
        {
            CortexTaskActionV2.Update => value with
            {
                Revision = revision,
                Title = command.Title ?? value.Title,
                Content = command.Content ?? value.Content,
                UpdatedAtUtc = provenance.CapturedAtUtc,
                Provenance = provenance
            },
            CortexTaskActionV2.Assign => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Assigned,
                Assignee = command.Assignee, ClaimedBy = null, BlockedReason = null,
                CompletedAtUtc = null, ArchivedAtUtc = null,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.Claim => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Claimed,
                ClaimedBy = command.ClaimedBy, BlockedReason = null,
                CompletedAtUtc = null, ArchivedAtUtc = null,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.Block => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Blocked,
                BlockedReason = command.Reason, CompletedAtUtc = null,
                ArchivedAtUtc = null,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.Complete => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Completed,
                BlockedReason = null, CompletedAtUtc = provenance.CapturedAtUtc,
                ArchivedAtUtc = null,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.Reopen => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Open,
                ClaimedBy = null, BlockedReason = null, CompletedAtUtc = null, ArchivedAtUtc = null,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.Archive => value with
            {
                Revision = revision, Status = CortexTaskStatusV2.Archived,
                BlockedReason = null, CompletedAtUtc = null, ArchivedAtUtc = provenance.CapturedAtUtc,
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.DependencyAdd => value with
            {
                Revision = revision,
                Dependencies = Array.AsReadOnly(value.Dependencies.Append(command.DependencyId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            CortexTaskActionV2.DependencyRemove => value with
            {
                Revision = revision,
                Dependencies = Array.AsReadOnly(value.Dependencies.Where(item => !string.Equals(item, command.DependencyId, StringComparison.Ordinal)).ToArray()),
                UpdatedAtUtc = provenance.CapturedAtUtc, Provenance = provenance
            },
            _ => throw new InvalidOperationException("The task action is not a write transition.")
        };
        return updated with
        {
            PhaseId = command.PhaseId ?? value.PhaseId,
            CheckpointSubject = command.CheckpointSubject ?? value.CheckpointSubject
        };
    }

    private static void EnsureTaskTransitionAllowed(CortexTaskV2 task, CortexTaskActionV2 action)
    {
        var allowed = action switch
        {
            CortexTaskActionV2.Update or CortexTaskActionV2.Assign or CortexTaskActionV2.Claim or
                CortexTaskActionV2.Block or CortexTaskActionV2.Complete or
                CortexTaskActionV2.DependencyAdd or CortexTaskActionV2.DependencyRemove =>
                task.Status != CortexTaskStatusV2.Archived,
            CortexTaskActionV2.Reopen => task.Status is
                CortexTaskStatusV2.Blocked or CortexTaskStatusV2.Completed or CortexTaskStatusV2.Archived,
            CortexTaskActionV2.Archive => task.Status != CortexTaskStatusV2.Archived,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException(
                $"Task action '{action}' is invalid from status '{task.Status}'.");
    }

    private static CortexKnowledgeV2 ApplyKnowledgeCommand(
        CortexKnowledgeCommandV2 command,
        CortexKnowledgeV2? existing,
        CortexProvenanceV2 provenance)
    {
        if (command.Action == CortexKnowledgeActionV2.Create)
        {
            return new(
                command.Namespace, command.RecordId!, 1, CortexKnowledgeStateV2.Active,
                command.Kind!.Value, command.Tier!.Value, command.Title!, command.Content!,
                command.Tags ?? [], provenance, SupersededByRecordId: null,
                provenance.CapturedAtUtc, provenance.CapturedAtUtc, ArchivedAtUtc: null);
        }
        var value = existing ?? throw new InvalidDataException("A knowledge mutation requires a current head.");
        return command.Action switch
        {
            CortexKnowledgeActionV2.Revise => value with
            {
                Revision = value.Revision + 1,
                Kind = command.Kind ?? value.Kind,
                Tier = command.Tier ?? value.Tier,
                Title = command.Title ?? value.Title,
                Content = command.Content ?? value.Content,
                Tags = command.Tags ?? value.Tags,
                UpdatedAtUtc = provenance.CapturedAtUtc,
                Provenance = provenance
            },
            CortexKnowledgeActionV2.Supersede => value with
            {
                Revision = value.Revision + 1,
                State = CortexKnowledgeStateV2.Superseded,
                SupersededByRecordId = command.ReplacementRecordId,
                UpdatedAtUtc = provenance.CapturedAtUtc,
                Provenance = provenance
            },
            CortexKnowledgeActionV2.Archive => value with
            {
                Revision = value.Revision + 1,
                State = CortexKnowledgeStateV2.Archived,
                ArchivedAtUtc = provenance.CapturedAtUtc,
                UpdatedAtUtc = provenance.CapturedAtUtc,
                Provenance = provenance
            },
            _ => throw new InvalidOperationException("The knowledge action is not a write transition.")
        };
    }

    private static void EnsureKnowledgeTransitionAllowed(
        CortexKnowledgeV2 value,
        CortexKnowledgeActionV2 action)
    {
        var allowed = action switch
        {
            CortexKnowledgeActionV2.Revise or CortexKnowledgeActionV2.Supersede =>
                value.State == CortexKnowledgeStateV2.Active,
            CortexKnowledgeActionV2.Archive => value.State != CortexKnowledgeStateV2.Archived,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException(
                $"Knowledge action '{action}' is invalid from state '{value.State}'.");
    }

    private async ValueTask<CortexKnowledgeResultV2> AnalyzeKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken)
    {
        var heads = new List<CortexKnowledgeV2>(command.Limit);
        var after = command.AfterRecordId;
        var scanTruncated = false;
        while (heads.Count < command.Limit)
        {
            var page = await _store.ListHeadsAsync(
                command.Namespace,
                KnowledgeKind,
                after,
                Math.Min(256, command.Limit - heads.Count),
                cancellationToken,
                searchableOnly: true).ConfigureAwait(false);
            foreach (var record in page.Items)
            {
                var knowledge = ToKnowledge(record);
                if (knowledge.State == CortexKnowledgeStateV2.Active)
                    heads.Add(knowledge);
            }
            after = page.NextAfterRecordId;
            if (after is null)
                break;
            if (heads.Count >= command.Limit)
            {
                scanTruncated = true;
                break;
            }
        }

        var suggestions = CortexV2KnowledgeConsolidation.Analyze(heads);
        var suggestionsTruncated = suggestions.Count > CortexV2Limits.MaximumConsolidationSuggestions;
        var selected = suggestions.Take(CortexV2Limits.MaximumConsolidationSuggestions).ToList();
        CortexKnowledgeResultV2 Result() => new(
            [command.Namespace],
            "analyzed",
            Record: null,
            new CortexConsolidationAnalysisV2(
                "cortex.knowledge-consolidation/1",
                heads.Count,
                scanTruncated,
                suggestionsTruncated,
                selected.AsReadOnly(),
                scanTruncated ? after : null));
        while (selected.Count > 0 &&
            JsonSerializer.SerializeToUtf8Bytes(Result(), CortexV2Json.Options).Length > command.MaxUtf8Bytes)
        {
            suggestionsTruncated = true;
            selected.RemoveAt(selected.Count - 1);
        }
        var result = Result();
        EnsureResponseFits(result, command.MaxUtf8Bytes);
        return result;
    }

    private static SqliteCortexV2AppendRequest TaskAppend(
        CortexTaskV2 task,
        string idempotencyKey,
        string eventType,
        long expectedRevision,
        bool contentChanged,
        byte[]? content,
        string? attributesOverride = null,
        string? idempotencyFingerprint = null)
    {
        var metadata = attributesOverride ?? JsonSerializer.Serialize(new TaskMetadata(
            task.Status, task.Assignee, task.ClaimedBy, task.Dependencies, task.BlockedReason,
            task.CreatedAtUtc, task.UpdatedAtUtc, task.CompletedAtUtc, task.ArchivedAtUtc,
            task.Provenance, null, null, null, task.PhaseId, task.CheckpointSubject), CortexV2Json.Options);
        return new(
            task.Namespace, TaskKind, task.TaskId, expectedRevision,
            idempotencyKey, task.Provenance.Actor, eventType, task.Title,
            Tags: [], Summary: TaskSearchSummary(task),
            contentChanged ? content : null,
            Searchable: task.Status != CortexTaskStatusV2.Archived,
            metadata)
        {
            IdempotencyFingerprint = idempotencyFingerprint
        };
    }

    private static SqliteCortexV2AppendRequest KnowledgeAppend(
        CortexKnowledgeV2 value,
        string idempotencyKey,
        string eventType,
        long expectedRevision,
        bool contentChanged,
        byte[]? content,
        string? idempotencyFingerprint = null,
        string? actorOverride = null)
    {
        var metadata = JsonSerializer.Serialize(new KnowledgeMetadata(
            value.State, value.Kind, value.Tier, value.Provenance,
            value.SupersededByRecordId, value.CreatedAtUtc, value.UpdatedAtUtc, value.ArchivedAtUtc),
            CortexV2Json.Options);
        return new(
            value.Namespace, KnowledgeKind, value.RecordId, expectedRevision,
            idempotencyKey, actorOverride ?? value.Provenance.Actor, eventType, value.Title,
            value.Tags, SanitizeSearchText(Truncate(value.Content, 1_024)),
            contentChanged ? content : null,
            Searchable: value.State == CortexKnowledgeStateV2.Active,
            metadata)
        {
            IdempotencyFingerprint = idempotencyFingerprint
        };
    }

    private static CortexTaskV2 ToTask(SqliteCortexV2Record record)
    {
        var metadata = ParseTaskMetadata(record.AttributesJson);
        return new(
            record.Namespace, record.RecordId, record.Revision, metadata.Status,
            record.Title, Encoding.UTF8.GetString(record.ContentUtf8), metadata.Assignee,
            metadata.ClaimedBy, metadata.Dependencies, metadata.BlockedReason,
            metadata.CreatedAtUtc, metadata.UpdatedAtUtc, metadata.CompletedAtUtc,
            metadata.ArchivedAtUtc, metadata.Provenance,
            metadata.PhaseId, metadata.CheckpointSubject);
    }

    private static CortexKnowledgeV2 ToKnowledge(SqliteCortexV2Record record)
    {
        var metadata = ParseKnowledgeMetadata(record.AttributesJson);
        return new(
            record.Namespace, record.RecordId, record.Revision, metadata.State,
            metadata.Kind, metadata.Tier, record.Title, Encoding.UTF8.GetString(record.ContentUtf8),
            record.Tags, metadata.Provenance, metadata.SupersededByRecordId,
            metadata.CreatedAtUtc, metadata.UpdatedAtUtc, metadata.ArchivedAtUtc);
    }

    private static CortexTaskSummaryV2 ToTaskSummary(SqliteCortexV2Record record)
    {
        var task = ToTask(record);
        return ToTaskSummary(task);
    }

    private static CortexTaskSummaryV2 ToTaskSummary(CortexTaskV2 task) => new(
        task.Namespace, task.TaskId, task.Revision, task.Status,
        Truncate(task.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
        Truncate(TaskExcerpt(task), CortexV2Limits.MaximumReceiptExcerptCharacters),
        task.Assignee, task.ClaimedBy,
        TruncateNullable(task.BlockedReason, CortexV2Limits.MaximumReceiptReasonCharacters),
        task.PhaseId,
        TruncateNullable(task.CheckpointSubject, CortexV2Limits.MaximumCheckpointSubjectCharacters), task.Provenance.VerifiedSource);

    private static CortexTaskSummaryV2 ToTaskSummary(
        SqliteCortexV2HeadProjection projection,
        TaskMetadata metadata) => new(
        projection.Namespace,
        projection.RecordId,
        projection.Revision,
        metadata.Status,
        Truncate(projection.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
        Truncate(SanitizeSearchText(projection.Summary), CortexV2Limits.MaximumReceiptExcerptCharacters),
        metadata.Assignee,
        metadata.ClaimedBy,
        TruncateNullable(metadata.BlockedReason, CortexV2Limits.MaximumReceiptReasonCharacters),
        metadata.PhaseId,
        TruncateNullable(metadata.CheckpointSubject, CortexV2Limits.MaximumCheckpointSubjectCharacters), metadata.Provenance.VerifiedSource);

    private static CortexKnowledgeReceiptV2 ToKnowledgeReceipt(CortexKnowledgeV2 value) => new(
        value.Namespace,
        value.RecordId,
        value.Revision,
        value.State,
        value.Kind,
        value.Tier,
        Truncate(value.Title, CortexV2Limits.MaximumReceiptTitleCharacters),
        new CortexProvenanceReceiptV2(
            value.Provenance.Source,
            value.Provenance.Actor,
            value.Provenance.SourceId,
            value.Provenance.CapturedAtUtc, value.Provenance.VerifiedSource));

    private static StartupCandidate? StartupContextItem(
        SqliteCortexV2SearchHit hit,
        int namespaceOrder,
        string? phaseId)
    {
        if (hit.Kind == TaskKind)
        {
            var taskMetadata = ParseTaskMetadata(hit.AttributesJson);
            if (taskMetadata.Status == CortexTaskStatusV2.Archived)
                return null;
            var item = new CortexContextItemV2(
                hit.Namespace, TaskKind, hit.RecordId, hit.Revision,
                hit.Title, StartupExcerpt(hit.Excerpt), Kind: null, Tier: null,
                ToProvenanceReceipt(taskMetadata.Provenance),
                hit.Score + (phaseId is not null &&
                    string.Equals(phaseId, taskMetadata.PhaseId, StringComparison.Ordinal) ? 1_000 : 0));
            return new(item, StartupTaskPriority(taskMetadata.Status), namespaceOrder);
        }
        var knowledgeMetadata = ParseKnowledgeMetadata(hit.AttributesJson);
        if (knowledgeMetadata.State != CortexKnowledgeStateV2.Active)
            return null;
        var knowledgeItem = new CortexContextItemV2(
            hit.Namespace, KnowledgeKind, hit.RecordId, hit.Revision, hit.Title,
            StartupExcerpt(hit.Excerpt), knowledgeMetadata.Kind, knowledgeMetadata.Tier,
            ToProvenanceReceipt(knowledgeMetadata.Provenance), hit.Score);
        return new(knowledgeItem, StartupKnowledgePriority(hit.Namespace, knowledgeMetadata), namespaceOrder);
    }

    private async ValueTask<string> ContextCursorAsync(
        IReadOnlyList<string> namespaces,
        CancellationToken cancellationToken)
    {
        var selected = new List<string>();
        foreach (var ns in namespaces)
        {
            foreach (var source in CortexKnowledgeSpaces.ReadSources(ns))
            {
                var value = await _store.ReadNamespaceAsync(source, cancellationToken).ConfigureAwait(false);
                if (value is not null)
                {
                    selected.Add(
                        $"{ns}:{source}:{value.RevisionCount}:{value.HeadCount}:{value.UpdatedAtUtc:O}");
                }
            }
        }
        return Hash(string.Join('\n', namespaces) + "\n" + string.Join('\n', selected));
    }

    private async ValueTask<string> StartContextCursorAsync(
        CortexStartRequestV2 request,
        CancellationToken cancellationToken)
    {
        var selected = new List<object>();
        foreach (var ns in request.Namespaces)
        {
            foreach (var source in CortexKnowledgeSpaces.ReadSources(ns))
            {
                var value = await _store.ReadNamespaceAsync(source, cancellationToken).ConfigureAwait(false);
                if (value is not null)
                {
                    selected.Add(new
                    {
                        logicalName = ns,
                        physicalName = source,
                        value.RevisionCount,
                        value.HeadCount,
                        value.UpdatedAtUtc
                    });
                }
            }
        }
        var payload = JsonSerializer.Serialize(new
        {
            selectionVersion = StartupSelectionVersion,
            namespaces = request.Namespaces,
            normalizedIntent = NormalizeIntent(request.Intent),
            taskId = request.TaskId,
            phaseId = request.PhaseId,
            revisions = selected
        }, CortexV2Json.Options);
        return Hash(payload);
    }

    // Unknown/malformed lifecycle metadata is never a tiering permission.
    internal static bool IsArchivedForPayloadTiering(string kind, string attributes)
    {
        try
        {
            using var document = JsonDocument.Parse(attributes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
                document.RootElement.EnumerateObject().Count()) return false;
            if (kind == TaskKind)
            {
                var value = ParseTaskMetadata(attributes);
                return value.Status == CortexTaskStatusV2.Archived && value.ArchivedAtUtc is { Offset: var offset } &&
                    offset == TimeSpan.Zero && value.Dependencies is not null && value.Provenance is not null &&
                    document.RootElement.EnumerateObject().All(p => p.Name is "status" or "assignee" or "claimedBy" or
                        "dependencies" or "blockedReason" or "createdAtUtc" or "updatedAtUtc" or "completedAtUtc" or
                        "archivedAtUtc" or "provenance" or "checkpoint" or "evidence" or "handoff" or "phaseId" or "checkpointSubject");
            }
            if (kind == KnowledgeKind)
            {
                var value = ParseKnowledgeMetadata(attributes);
                return value.State == CortexKnowledgeStateV2.Archived && value.ArchivedAtUtc is { Offset: var offset } &&
                    offset == TimeSpan.Zero && value.Provenance is not null &&
                    document.RootElement.EnumerateObject().All(p => p.Name is "state" or "kind" or "tier" or "provenance" or
                        "supersededByRecordId" or "createdAtUtc" or "updatedAtUtc" or "archivedAtUtc");
            }
            return false;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return false; }
    }

    private static TaskMetadata ParseTaskMetadata(string value) =>
        JsonSerializer.Deserialize<TaskMetadata>(value, CortexV2Json.Options) ??
        throw new InvalidDataException("Cortex v2 task metadata is malformed.");

    private static KnowledgeMetadata ParseKnowledgeMetadata(string value) =>
        JsonSerializer.Deserialize<KnowledgeMetadata>(value, CortexV2Json.Options) ??
        throw new InvalidDataException("Cortex v2 knowledge metadata is malformed.");

    private static int StartupTaskPriority(CortexTaskStatusV2 status) => status switch
    {
        CortexTaskStatusV2.Blocked or CortexTaskStatusV2.Claimed or
        CortexTaskStatusV2.Assigned or CortexTaskStatusV2.Open => 3,
        _ => 8
    };

    private static int StartupKnowledgePriority(string ns, KnowledgeMetadata value)
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
        if (value.Kind == CortexKnowledgeKindV2.Tenet &&
            string.Equals(CortexKnowledgeSpaces.Canonicalize(ns), CortexKnowledgeSpaces.Shared, StringComparison.Ordinal))
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

    private static string StartupExcerpt(string value) =>
        SanitizeSearchText(Truncate(value, CortexV2Limits.MaximumCompactExcerptCharacters));

    private static string SearchExcerpt(string value, CortexResponseFormatV2 format) =>
        SanitizeSearchText(Truncate(
            value,
            format == CortexResponseFormatV2.Compact
                ? CortexV2Limits.MaximumCompactExcerptCharacters
                : 1_024));

    private static string TaskExcerpt(CortexTaskV2 task) =>
        SanitizeSearchText(Truncate(task.BlockedReason ?? task.Content, 1_024));

    private static string TaskSearchSummary(CortexTaskV2 task) =>
        SanitizeSearchText(Truncate(string.Join(' ', new[]
        {
            task.PhaseId,
            task.CheckpointSubject,
            task.BlockedReason,
            task.Content
        }.Where(value => !string.IsNullOrWhiteSpace(value))), 1_024));

    private static CortexProvenanceReceiptV2 ToProvenanceReceipt(CortexProvenanceV2 value) => new(
        value.Source,
        value.Actor,
        value.SourceId,
        value.CapturedAtUtc, value.VerifiedSource);

    private static string SanitizeSearchText(string value)
    {
        if (!value.Any(character => char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
        {
            return value;
        }
        return new string(value.Select(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t'
                ? ' '
                : character).ToArray());
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string? TruncateNullable(string? value, int maximum) =>
        value is null ? null : Truncate(value, maximum);

    private static void EnsureResponseFits<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CortexV2Json.Options).Length;
        if (bytes > maximumBytes)
            throw new CortexV2ResponseBudgetExceededException(maximumBytes, bytes);
    }

    private static string CursorKey(CortexSearchHitV2 value) =>
        $"{value.Namespace}\n{value.RecordType}\n{value.RecordId}\n{value.Revision}";

    private static string EncodeCursor(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static string Fingerprint(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record TaskMetadata(
        CortexTaskStatusV2 Status,
        string? Assignee,
        string? ClaimedBy,
        IReadOnlyList<string> Dependencies,
        string? BlockedReason,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        DateTimeOffset? ArchivedAtUtc,
        CortexProvenanceV2 Provenance,
        string? Checkpoint,
        string? Evidence,
        string? Handoff,
        string? PhaseId = null,
        string? CheckpointSubject = null);

    private sealed record KnowledgeMetadata(
        CortexKnowledgeStateV2 State,
        CortexKnowledgeKindV2 Kind,
        CortexKnowledgeTierV2 Tier,
        CortexProvenanceV2 Provenance,
        string? SupersededByRecordId,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? ArchivedAtUtc);

    private sealed record StartupCandidate(
        CortexContextItemV2 Item,
        int Priority,
        int NamespaceOrder);
}
