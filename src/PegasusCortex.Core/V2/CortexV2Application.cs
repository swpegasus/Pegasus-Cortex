using System.Text.Json;
using System.Diagnostics;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;

namespace Pegasus.Cortex.Core.V2;

/// <summary>
/// Stateless validation, namespace-isolation, and response-budget boundary for
/// the lean V2 API. Persistence providers own transactions and revision state.
/// </summary>
public sealed partial class CortexV2Application
{
    private readonly ICortexV2Store _store;
    private readonly string _actor;
    private readonly TimeProvider _timeProvider;
    private readonly CortexV2RetrievalMetrics _retrievalMetrics;

    public CortexV2Application(
        ICortexV2Store store,
        string actor,
        TimeProvider? timeProvider = null,
        ICortexPersonalProfileProvider? personalProfileProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        CortexV2Policy.RequireIdentifier(actor, nameof(actor));
        _actor = actor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retrievalMetrics = new CortexV2RetrievalMetrics(_timeProvider.GetUtcNow());
        _personalProfileProvider = personalProfileProvider;
    }

    public async ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken)
    {
        var result = await _store.StatusAsync(cancellationToken).ConfigureAwait(false) with
        {
            RetrievalMetrics = _retrievalMetrics.Snapshot(),
            PersonalProfile = await PersonalProfileAsync(cancellationToken).ConfigureAwait(false)
        };
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, []);
        return CortexV2Policy.RequireBudget(result, CortexV2Limits.DefaultResponseUtf8Bytes);
    }

    public async ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
        CortexNamespacesRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CortexV2Policy.RequireOptionalIdentifier(request.AfterNamespace, nameof(request.AfterNamespace));
        CortexV2Policy.RequirePage(request.Limit);
        CortexV2Policy.RequireCompactBudget(request.MaxUtf8Bytes);
        var result = await _store.ListNamespacesAsync(request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Items.Count > request.Limit)
            throw new InvalidDataException("The provider exceeded the namespace page limit.");
        foreach (var item in result.Items)
        {
            CortexV2Policy.RequireNamespace(item.Namespace);
            CortexV2Policy.RequireEffectiveNamespaces(item.EffectiveNamespaces, [item.Namespace]);
        }
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, []);
        CortexV2Policy.RequireOptionalIdentifier(result.NextCursor, nameof(result.NextCursor));
        if (result.HasMore != (result.NextCursor is not null))
            throw new InvalidDataException("Namespace pagination metadata is inconsistent.");
        return CortexV2Policy.RequireBudget(result, request.MaxUtf8Bytes);
    }

    public async ValueTask<CortexStartResultV2> StartAsync(
        CortexStartRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with
        {
            Namespaces = CanonicalizeNamespaces(request.Namespaces)
        };
        CortexV2Policy.RequireNamespaces(request.Namespaces);
        CortexV2Policy.RequireText(request.Intent, CortexV2Limits.MaximumIntentCharacters, nameof(request.Intent));
        CortexV2Policy.RequireOptionalCursor(request.KnownContextCursor, nameof(request.KnownContextCursor));
        CortexV2Policy.RequireOptionalIdentifier(request.TaskId, nameof(request.TaskId));
        CortexV2Policy.RequireOptionalIdentifier(request.PhaseId, nameof(request.PhaseId));
        CortexV2Policy.RequireEnum(request.Format, nameof(request.Format));
        CortexV2Policy.RequireStartBudget(request.MaxUtf8Bytes);
        var profile = await PersonalProfileForAsync(request.Namespaces, cancellationToken).ConfigureAwait(false);
        var cursorScope = PersonalCursorScope("start", request.Namespaces, request.Intent, request.TaskId, request.PhaseId);
        var maximumBytes = request.MaxUtf8Bytes;
        if (profile is not null)
        {
            request = request with
            {
                Namespaces = AvailableNamespaces(request.Namespaces, profile),
                KnownContextCursor = UnwrapPersonalCursor(request.KnownContextCursor, profile, cursorScope, resetOnChange: true),
                MaxUtf8Bytes = PersonalStoreBudget(maximumBytes, profile)
            };
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = request.Namespaces.Count == 0
                ? new CortexStartResultV2(CortexContextDispositionV2.Snapshot, [], "empty", [], false)
                : await _store.StartAsync(request, cancellationToken).ConfigureAwait(false);
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
            CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, request.Namespaces);
            if (result.Items.Count > 4)
                throw new InvalidDataException("The provider exceeded the four-item startup context limit.");
            if (result.Items.Any(item => !request.Namespaces.Contains(item.Namespace, StringComparer.Ordinal)))
                throw new InvalidDataException("A startup item escaped the available requested namespaces.");
            if (result.Items
                .Select(value => (value.Namespace, value.RecordType, value.RecordId, value.Revision))
                .Distinct()
                .Count() != result.Items.Count)
            {
                throw new InvalidDataException("The provider returned duplicate startup record coordinates.");
            }
            if (result.CurrentTask is not null)
            {
                if (request.TaskId is null ||
                    !string.Equals(result.CurrentTask.TaskId, request.TaskId, StringComparison.Ordinal) ||
                    !request.Namespaces.Contains(result.CurrentTask.Namespace, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("The provider returned a current task outside the exact request identity.");
                }
                if (result.Items.Any(value =>
                        string.Equals(value.RecordType, "task", StringComparison.Ordinal) &&
                        string.Equals(value.Namespace, result.CurrentTask.Namespace, StringComparison.Ordinal) &&
                        string.Equals(value.RecordId, result.CurrentTask.TaskId, StringComparison.Ordinal) &&
                        value.Revision == result.CurrentTask.Revision))
                {
                    throw new InvalidDataException("The provider duplicated the current task in ranked context items.");
                }
            }
            var validated = BudgetPersonalStart(result, profile, cursorScope, maximumBytes);
            _retrievalMetrics.Record(
                CortexV2RetrievalOperation.Start,
                started,
                failed: false,
                validated.Items.Count + (validated.CurrentTask is null ? 0 : 1),
                JsonSerializer.SerializeToUtf8Bytes(validated, CortexV2Json.Options).Length);
            return validated;
        }
        catch
        {
            _retrievalMetrics.Record(CortexV2RetrievalOperation.Start, started, failed: true, 0, 0);
            throw;
        }
    }

    public async ValueTask<CortexSearchResultV2> SearchAsync(
        CortexSearchRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with
        {
            Namespaces = CanonicalizeNamespaces(request.Namespaces)
        };
        CortexV2Policy.RequireNamespaces(request.Namespaces);
        CortexV2Policy.RequireText(request.Text, CortexV2Limits.MaximumContentCharacters, nameof(request.Text));
        CortexV2Policy.RequireOptionalCursor(request.AfterCursor, nameof(request.AfterCursor));
        CortexV2Policy.RequireEnum(request.Format, nameof(request.Format));
        CortexV2Policy.RequirePage(request.Limit);
        CortexV2Policy.RequireCompactBudget(request.MaxUtf8Bytes);
        if (request.Kinds is { Count: 0 })
            throw new ArgumentException("Kinds must be omitted or non-empty.", nameof(request));
        if (request.Kinds is not null)
        {
            foreach (var kind in request.Kinds)
                CortexV2Policy.RequireEnum(kind, nameof(request.Kinds));
        }
        var profile = await PersonalProfileForAsync(request.Namespaces, cancellationToken).ConfigureAwait(false);
        var cursorScope = PersonalCursorScope("search", request.Namespaces, request.Text,
            request.Kinds is null ? null : string.Join(',', request.Kinds));
        var maximumBytes = request.MaxUtf8Bytes;
        if (profile is not null)
        {
            request = request with
            {
                Namespaces = AvailableNamespaces(request.Namespaces, profile),
                AfterCursor = UnwrapPersonalCursor(request.AfterCursor, profile, cursorScope, resetOnChange: false),
                MaxUtf8Bytes = PersonalStoreBudget(maximumBytes, profile)
            };
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = request.Namespaces.Count == 0
                ? new CortexSearchResultV2([], [], null, false)
                : await _store.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
            CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, request.Namespaces);
            foreach (var hit in result.Items)
                RequireSearchHit(hit, request);
            var validated = BudgetPersonalSearch(result, profile, cursorScope, maximumBytes);
            _retrievalMetrics.Record(
                CortexV2RetrievalOperation.Search,
                started,
                failed: false,
                result.Items.Count,
                JsonSerializer.SerializeToUtf8Bytes(validated, CortexV2Json.Options).Length);
            return validated;
        }
        catch
        {
            _retrievalMetrics.Record(CortexV2RetrievalOperation.Search, started, failed: true, 0, 0);
            throw;
        }
    }

    public async ValueTask<CortexReadResultV2> ReadAsync(
        CortexReadRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with
        {
            Namespace = CanonicalizeNamespace(request.Namespace)
        };
        CortexV2Policy.RequireNamespace(request.Namespace);
        CortexV2Policy.RequireEnum(request.RecordType, nameof(request.RecordType));
        CortexV2Policy.RequireIdentifier(request.RecordId, nameof(request.RecordId));
        if (request.Revision is < 1)
            throw new ArgumentOutOfRangeException(nameof(request.Revision));
        CortexV2Policy.RequireBudget(request.MaxUtf8Bytes);
        var profile = await RequirePersonalAdmissionAsync([request.Namespace], cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _store.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
            ValidateReadResult(request, result);
            var validated = CortexV2Policy.RequireBudget(result, request.MaxUtf8Bytes);
            _retrievalMetrics.Record(
                CortexV2RetrievalOperation.Read,
                started,
                failed: false,
                1,
                JsonSerializer.SerializeToUtf8Bytes(validated, CortexV2Json.Options).Length);
            return validated;
        }
        catch
        {
            _retrievalMetrics.Record(CortexV2RetrievalOperation.Read, started, failed: true, 0, 0);
            throw;
        }
    }

    public async ValueTask<CortexReadBatchResultV2> ReadBatchAsync(
        CortexReadBatchRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        try
        {
            ArgumentNullException.ThrowIfNull(request.Records);
            if (request.Records.Count is < 1 or > CortexV2Limits.MaximumBatchReadRecords)
                throw new ArgumentOutOfRangeException(nameof(request.Records));
            request = request with
            {
                Records = Array.AsReadOnly(request.Records.Select(value => value is null
                    ? null!
                    : value with
                    {
                        Namespace = CanonicalizeNamespace(value.Namespace)
                    }).ToArray())
            };
            CortexV2Policy.RequireBudget(request.MaxUtf8Bytes);
            var coordinateKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var coordinate in request.Records)
            {
                ArgumentNullException.ThrowIfNull(coordinate);
                CortexV2Policy.RequireNamespace(coordinate.Namespace);
                CortexV2Policy.RequireEnum(coordinate.RecordType, nameof(coordinate.RecordType));
                CortexV2Policy.RequireIdentifier(coordinate.RecordId, nameof(coordinate.RecordId));
                if (coordinate.Revision is < 1)
                    throw new ArgumentOutOfRangeException(nameof(coordinate.Revision));
                if (!coordinateKeys.Add($"{coordinate.Namespace}\n{coordinate.RecordType}\n{coordinate.RecordId}\n{coordinate.Revision}"))
                    throw new ArgumentException("Batch read coordinates must be unique.", nameof(request));
            }

            // Admit the entire batch before hydrating even its first coordinate.
            var profile = await RequirePersonalAdmissionAsync(
                request.Records.Select(value => value.Namespace), cancellationToken).ConfigureAwait(false);
            var items = new List<CortexReadBatchItemV2>(request.Records.Count);
            foreach (var coordinate in request.Records)
            {
                var single = new CortexReadRequestV2(
                    coordinate.Namespace,
                    coordinate.RecordType,
                    coordinate.RecordId,
                    coordinate.Revision,
                    request.MaxUtf8Bytes);
                var result = await _store.ReadAsync(single, cancellationToken).ConfigureAwait(false);
                await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
                ValidateReadResult(single, result);
                items.Add(new(coordinate, result.Record!));
            }
            var namespaces = request.Records.Select(value => value.Namespace)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
            var validated = CortexV2Policy.RequireBudget(
                new CortexReadBatchResultV2(namespaces, items.AsReadOnly()),
                request.MaxUtf8Bytes);
            _retrievalMetrics.Record(
                CortexV2RetrievalOperation.BatchRead,
                started,
                failed: false,
                items.Count,
                JsonSerializer.SerializeToUtf8Bytes(validated, CortexV2Json.Options).Length);
            return validated;
        }
        catch
        {
            _retrievalMetrics.Record(CortexV2RetrievalOperation.BatchRead, started, failed: true, 0, 0);
            throw;
        }
    }

    public async ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken) => await ExecuteTaskAsync(command, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
        CortexTaskCommandV2 command, CortexInvocationSourceV2? invocationSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command with
        {
            Namespace = CanonicalizeNamespace(command.Namespace)
        };
        CortexV2Policy.ValidateTask(command);
        invocationSource?.Validate();
        if (command.Action is not (CortexTaskActionV2.List or CortexTaskActionV2.Get or CortexTaskActionV2.History))
            RequireSemanticTask(command, invocationSource);
        var profile = await RequirePersonalAdmissionAsync([command.Namespace], cancellationToken).ConfigureAwait(false);
        var stamped = command with
        {
            EffectiveProvenance = command.Action is
                CortexTaskActionV2.List or CortexTaskActionV2.Get or CortexTaskActionV2.History
                ? null
                : Stamp(CortexProvenanceSourceV2.Agent, null, null, invocationSource)
        };
        var result = await _store.ExecuteTaskAsync(stamped, cancellationToken).ConfigureAwait(false);
        if (command.Action is CortexTaskActionV2.List or CortexTaskActionV2.Get or CortexTaskActionV2.History)
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, [command.Namespace]);
        RequireTaskResult(result, command);
        return CortexV2Policy.RequireBudget(result, command.MaxUtf8Bytes);
    }

    public async ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken) => await ExecuteKnowledgeAsync(command, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
        CortexKnowledgeCommandV2 command, CortexInvocationSourceV2? invocationSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command with
        {
            Namespace = CanonicalizeNamespace(command.Namespace)
        };
        CortexV2Policy.ValidateKnowledge(command);
        invocationSource?.Validate();
        if (command.Action is not (CortexKnowledgeActionV2.Read or CortexKnowledgeActionV2.Analyze))
            RequireSemanticKnowledge(command, invocationSource);
        var profile = await RequirePersonalAdmissionAsync([command.Namespace], cancellationToken).ConfigureAwait(false);
        var stamped = command with
        {
            EffectiveProvenance = command.Provenance is null
                ? null
                : Stamp(command.Provenance, invocationSource)
        };
        var result = await _store.ExecuteKnowledgeAsync(stamped, cancellationToken).ConfigureAwait(false);
        if (command.Action is CortexKnowledgeActionV2.Read or CortexKnowledgeActionV2.Analyze)
            await RequirePersonalSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, [command.Namespace]);
        if (result.Record is { } receipt)
        {
            if (!string.Equals(receipt.Namespace, command.Namespace, StringComparison.Ordinal) ||
                command.RecordId is not null && !string.Equals(receipt.RecordId, command.RecordId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The knowledge receipt did not match the requested coordinates.");
            }
            CortexV2Policy.RequireText(
                receipt.Title,
                CortexV2Limits.MaximumReceiptTitleCharacters,
                nameof(receipt.Title));
        }
        else if (command.Action != CortexKnowledgeActionV2.Analyze &&
            !IsNoCommitDisposition(result.Disposition))
        {
            throw new InvalidDataException("A successful knowledge action omitted its compact receipt.");
        }
        return CortexV2Policy.RequireBudget(result, command.MaxUtf8Bytes);
    }

    private static void ValidateReadResult(CortexReadRequestV2 request, CortexReadResultV2 result)
    {
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, [request.Namespace]);
        if (result.RecordType != request.RecordType ||
            !string.Equals(result.RecordId, request.RecordId, StringComparison.Ordinal) ||
            request.Revision is not null && result.Revision != request.Revision)
        {
            throw new InvalidDataException("The provider returned different record coordinates than requested.");
        }
        var recordCoordinatesMatch = result.Record switch
        {
            CortexTaskV2 task when request.RecordType == CortexRecordTypeV2.Task =>
                string.Equals(task.Namespace, request.Namespace, StringComparison.Ordinal) &&
                string.Equals(task.TaskId, request.RecordId, StringComparison.Ordinal) &&
                task.Revision == result.Revision,
            CortexKnowledgeV2 knowledge when request.RecordType == CortexRecordTypeV2.Knowledge =>
                string.Equals(knowledge.Namespace, request.Namespace, StringComparison.Ordinal) &&
                string.Equals(knowledge.RecordId, request.RecordId, StringComparison.Ordinal) &&
                knowledge.Revision == result.Revision,
            _ => false
        };
        if (!recordCoordinatesMatch)
            throw new InvalidDataException("The provider returned a missing, mismatched, or untyped exact record.");
    }

    public async ValueTask<CortexFinishResultV2> FinishAsync(
        CortexFinishRequestV2 request,
        CancellationToken cancellationToken) => await FinishAsync(request, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CortexFinishResultV2> FinishAsync(
        CortexFinishRequestV2 request, CortexInvocationSourceV2? invocationSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with
        {
            Namespace = CanonicalizeNamespace(request.Namespace)
        };
        CortexV2Policy.ValidateFinish(request);
        invocationSource?.Validate();
        var fields = new List<string?> { request.Namespace, request.TaskId, request.IdempotencyKey, request.Checkpoint,
            request.Evidence, request.Handoff, request.TaskTitle, request.TaskContent, request.BlockedReason,
            request.PhaseId, request.CheckpointSubject, invocationSource?.Label };
        foreach (var value in request.Knowledge)
        {
            fields.AddRange([value.RecordId, value.IdempotencyKey, value.Title, value.Content, value.Provenance.SourceId, value.Provenance.Evidence]);
            fields.AddRange(value.Tags);
        }
        Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticAccepted(fields.ToArray());
        // Availability is admission-only for writes: an admitted finish remains
        // one transaction against the same store and returns its valid receipt.
        await RequirePersonalAdmissionAsync([request.Namespace], cancellationToken).ConfigureAwait(false);
        var stamped = request with
        {
            EffectiveProvenance = Stamp(
                CortexProvenanceSourceV2.Agent,
                null,
                request.Evidence, invocationSource),
            Knowledge = Array.AsReadOnly(request.Knowledge.Select(value => value with
            {
                EffectiveProvenance = Stamp(value.Provenance, invocationSource)
            }).ToArray())
        };
        var result = await _store.FinishAsync(stamped, cancellationToken).ConfigureAwait(false);
        CortexV2Policy.RequireEffectiveNamespaces(result.EffectiveNamespaces, [request.Namespace]);
        if (!string.Equals(result.Task.Namespace, request.Namespace, StringComparison.Ordinal) ||
            !string.Equals(result.Task.TaskId, request.TaskId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The finish task receipt did not match the requested coordinates.");
        }
        CortexV2Policy.RequireText(
            result.Task.Title,
            CortexV2Limits.MaximumReceiptTitleCharacters,
            nameof(result.Task.Title));
        CortexV2Policy.RequireText(
            result.Task.Excerpt,
            CortexV2Limits.MaximumReceiptExcerptCharacters,
            nameof(result.Task.Excerpt),
            allowEmpty: true);
        CortexV2Policy.RequireOptionalText(
            result.Task.BlockedReason,
            CortexV2Limits.MaximumReceiptReasonCharacters,
            nameof(result.Task.BlockedReason));
        foreach (var receipt in result.Knowledge)
        {
            CortexV2Policy.RequireText(
                receipt.Title,
                CortexV2Limits.MaximumReceiptTitleCharacters,
                nameof(receipt.Title));
        }
        var successful = !IsNoCommitDisposition(result.Disposition);
        var expectedRecordIds = request.Knowledge.Select(value => value.RecordId);
        var actualRecordIds = result.Knowledge.Select(value => value.RecordId);
        if ((successful && !actualRecordIds.SequenceEqual(expectedRecordIds, StringComparer.Ordinal)) ||
            result.Knowledge.Any(value => !string.Equals(value.Namespace, request.Namespace, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The finish receipt did not echo the committed namespace and knowledge coordinates.");
        }
        return CortexV2Policy.RequireBudget(result, request.MaxUtf8Bytes);
    }

    private string CanonicalizeNamespace(string value)
    {
        var mapped = _store is ICortexKnowledgeSpaceMapper mapper ? mapper.CanonicalizeNamespace(value) : value;
        return _personalProfileProvider is null ? mapped : CortexKnowledgeSpaces.Canonicalize(mapped);
    }

    private IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values)
    {
        var mapped = _store is ICortexKnowledgeSpaceMapper mapper ? mapper.CanonicalizeNamespaces(values) : values;
        return _personalProfileProvider is null ? mapped : CortexKnowledgeSpaces.Canonicalize(mapped);
    }

    private static void RequireSemanticTask(CortexTaskCommandV2 command, CortexInvocationSourceV2? source)
    {
        var fields = new List<string?> { command.Namespace, command.TaskId, command.IdempotencyKey, command.Title, command.Content,
            command.Assignee, command.ClaimedBy, command.Reason, command.DependencyId, command.PhaseId, command.CheckpointSubject, source?.Label };
        if (command.Dependencies is not null) fields.AddRange(command.Dependencies);
        Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticAccepted(fields.ToArray());
    }

    private static void RequireSemanticKnowledge(CortexKnowledgeCommandV2 command, CortexInvocationSourceV2? source)
    {
        var fields = new List<string?> { command.Namespace, command.RecordId, command.IdempotencyKey, command.Title, command.Content,
            command.ReplacementRecordId, command.Provenance?.SourceId, command.Provenance?.Evidence, source?.Label };
        if (command.Tags is not null) fields.AddRange(command.Tags);
        Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticAccepted(fields.ToArray());
    }

    private CortexProvenanceV2 Stamp(CortexProvenanceInputV2 input, CortexInvocationSourceV2? invocationSource = null) => new(
        input.Source,
        _actor,
        input.SourceId,
        input.Evidence,
        _timeProvider.GetUtcNow().ToUniversalTime(), invocationSource);

    private CortexProvenanceV2 Stamp(
        CortexProvenanceSourceV2 source,
        string? sourceId,
        string? evidence, CortexInvocationSourceV2? invocationSource = null) => new(
            source,
            _actor,
            sourceId,
            evidence,
            _timeProvider.GetUtcNow().ToUniversalTime(), invocationSource);

    private static void RequireSearchHit(
        CortexSearchHitV2 hit,
        CortexSearchRequestV2 request)
    {
        ArgumentNullException.ThrowIfNull(hit);
        CortexV2Policy.RequireEnum(hit.RecordType, nameof(hit.RecordType));
        if (!request.Namespaces.Contains(hit.Namespace, StringComparer.Ordinal))
            throw new InvalidDataException("A search result escaped the requested namespaces.");
        if (hit.RecordType == CortexRecordTypeV2.Task)
        {
            if (request.Kinds is not null || hit.Kind is not null || hit.Tier is not null)
                throw new InvalidDataException("A task search hit carried knowledge-only metadata or bypassed a kinds filter.");
        }
        else
        {
            if (hit.Kind is null || hit.Tier is null)
                throw new InvalidDataException("A knowledge search hit omitted kind or tier.");
            if (request.Kinds is not null && !request.Kinds.Contains(hit.Kind.Value))
                throw new InvalidDataException("A knowledge search hit bypassed the requested kinds filter.");
        }
    }

    private static void RequireTaskResult(
        CortexTaskResultV2 result,
        CortexTaskCommandV2 command)
    {
        static void RequireSummary(CortexTaskSummaryV2 value)
        {
            CortexV2Policy.RequireText(
                value.Title,
                CortexV2Limits.MaximumReceiptTitleCharacters,
                nameof(value.Title));
            CortexV2Policy.RequireText(
                value.Excerpt,
                CortexV2Limits.MaximumReceiptExcerptCharacters,
                nameof(value.Excerpt),
                allowEmpty: true);
            CortexV2Policy.RequireOptionalText(
                value.BlockedReason,
                CortexV2Limits.MaximumReceiptReasonCharacters,
                nameof(value.BlockedReason));
        }

        if (result.Task is { } task &&
            (!string.Equals(task.Namespace, command.Namespace, StringComparison.Ordinal) ||
             command.TaskId is not null && !string.Equals(task.TaskId, command.TaskId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The task receipt did not match the requested task coordinates.");
        }
        if (result.Task is not null)
            RequireSummary(result.Task);
        else if (command.Action is not (CortexTaskActionV2.List or CortexTaskActionV2.History) &&
            !IsNoCommitDisposition(result.Disposition))
        {
            throw new InvalidDataException("A successful task action omitted its compact receipt.");
        }
        foreach (var item in result.Items)
            RequireSummary(item);
    }

    private static bool IsNoCommitDisposition(string disposition) => disposition is
        "conflict" or "stale-revision" or "not-found" or "rejected";
}

internal static class CortexV2Policy
{
    public static void RequireEnum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    public static void ValidateTask(CortexTaskCommandV2 command)
    {
        RequireNamespace(command.Namespace);
        RequireEnum(command.Action, nameof(command.Action));
        RequirePage(command.Limit);
        RequireCompactBudget(command.MaxUtf8Bytes);
        RequireOptionalIdentifier(command.TaskId, nameof(command.TaskId));
        RequireOptionalIdentifier(command.IdempotencyKey, nameof(command.IdempotencyKey));
        RequireOptionalIdentifier(command.Assignee, nameof(command.Assignee));
        RequireOptionalIdentifier(command.ClaimedBy, nameof(command.ClaimedBy));
        RequireOptionalIdentifier(command.DependencyId, nameof(command.DependencyId));
        RequireOptionalText(command.Title, CortexV2Limits.MaximumTitleCharacters, nameof(command.Title));
        RequireOptionalText(command.Content, CortexV2Limits.MaximumContentCharacters, nameof(command.Content), allowEmpty: true);
        RequireOptionalText(command.Reason, CortexV2Limits.MaximumReasonCharacters, nameof(command.Reason));
        RequireOptionalIdentifier(command.AfterTaskId, nameof(command.AfterTaskId));
        RequireOptionalIdentifier(command.PhaseId, nameof(command.PhaseId));
        RequireOptionalText(
            command.CheckpointSubject,
            CortexV2Limits.MaximumCheckpointSubjectCharacters,
            nameof(command.CheckpointSubject));
        if (command.Dependencies is not null)
            RequireIdentifiers(command.Dependencies, CortexV2Limits.MaximumDependencies, nameof(command.Dependencies));

        var needsId = command.Action is not CortexTaskActionV2.List;
        if (needsId && command.TaskId is null)
            throw new ArgumentException("The task action requires taskId.", nameof(command));
        var writes = command.Action is not (CortexTaskActionV2.List or CortexTaskActionV2.Get or CortexTaskActionV2.History);
        if (writes && command.IdempotencyKey is null)
            throw new ArgumentException("A task write requires idempotencyKey.", nameof(command));
        var mutates = writes && command.Action is not CortexTaskActionV2.Create;
        if (mutates && command.ExpectedRevision is null or < 1)
            throw new ArgumentException("A task mutation requires expectedRevision.", nameof(command));
        if (command.Action == CortexTaskActionV2.Create && (command.Title is null || command.Content is null))
            throw new ArgumentException("Task create requires title and content.", nameof(command));
        if (command.Action == CortexTaskActionV2.Update && command.Title is null && command.Content is null)
            throw new ArgumentException("Task update requires title or content.", nameof(command));
        if (command.Action == CortexTaskActionV2.Assign && command.Assignee is null ||
            command.Action == CortexTaskActionV2.Claim && command.ClaimedBy is null ||
            command.Action == CortexTaskActionV2.Block && command.Reason is null ||
            command.Action is CortexTaskActionV2.DependencyAdd or CortexTaskActionV2.DependencyRemove && command.DependencyId is null)
        {
            throw new ArgumentException("The task action is missing its action-specific value.", nameof(command));
        }
        if (command.Action == CortexTaskActionV2.History &&
            (command.FirstRevision is null or < 1 || command.LastRevision is null or < 1 ||
             command.LastRevision < command.FirstRevision || command.LastRevision - command.FirstRevision > 255))
        {
            throw new ArgumentException("Task history requires a bounded revision range.", nameof(command));
        }
    }

    public static void ValidateKnowledge(CortexKnowledgeCommandV2 command)
    {
        RequireNamespace(command.Namespace);
        RequireEnum(command.Action, nameof(command.Action));
        if (command.Action == CortexKnowledgeActionV2.Analyze)
            RequireOptionalIdentifier(command.RecordId, nameof(command.RecordId));
        else
            RequireIdentifier(command.RecordId, nameof(command.RecordId));
        RequireOptionalIdentifier(command.IdempotencyKey, nameof(command.IdempotencyKey));
        RequireOptionalIdentifier(command.ReplacementRecordId, nameof(command.ReplacementRecordId));
        RequireOptionalText(command.Title, CortexV2Limits.MaximumTitleCharacters, nameof(command.Title));
        RequireOptionalText(command.Content, CortexV2Limits.MaximumContentCharacters, nameof(command.Content), allowEmpty: true);
        if (command.Tags is not null)
            RequireIdentifiers(command.Tags, CortexV2Limits.MaximumTags, nameof(command.Tags));
        if (command.Provenance is not null)
            ValidateProvenanceInput(command.Provenance);
        if (command.Kind is { } kind)
            RequireEnum(kind, nameof(command.Kind));
        if (command.Tier is { } tier)
            RequireEnum(tier, nameof(command.Tier));
        RequireOptionalIdentifier(command.AfterRecordId, nameof(command.AfterRecordId));
        if (command.Action == CortexKnowledgeActionV2.Analyze)
        {
            if (command.Limit is < 1 or > CortexV2Limits.MaximumConsolidationHeads)
                throw new ArgumentOutOfRangeException(nameof(command.Limit));
        }
        else
        {
            RequirePage(command.Limit);
        }
        RequireCompactBudget(command.MaxUtf8Bytes);

        var writes = command.Action is not (CortexKnowledgeActionV2.Read or CortexKnowledgeActionV2.Analyze);
        if (writes && command.IdempotencyKey is null)
            throw new ArgumentException("A knowledge write requires idempotencyKey.", nameof(command));
        if (writes && command.Provenance is null)
            throw new ArgumentException("A knowledge write requires provenance.", nameof(command));
        if (command.Action == CortexKnowledgeActionV2.Create &&
            (command.Kind is null || command.Tier is null || command.Title is null ||
             command.Content is null || command.Provenance is null))
        {
            throw new ArgumentException("Knowledge create requires kind, tier, title, content, and provenance.", nameof(command));
        }
        if (command.Action == CortexKnowledgeActionV2.Revise &&
            command.Kind is null && command.Tier is null && command.Title is null &&
            command.Content is null && command.Tags is null)
        {
            throw new ArgumentException("Knowledge revision must change a field.", nameof(command));
        }
        if (command.Action is CortexKnowledgeActionV2.Revise or CortexKnowledgeActionV2.Supersede or CortexKnowledgeActionV2.Archive &&
            command.ExpectedRevision is null or < 1)
        {
            throw new ArgumentException("A knowledge mutation requires expectedRevision.", nameof(command));
        }
        if (command.Action == CortexKnowledgeActionV2.Supersede && command.ReplacementRecordId is null)
            throw new ArgumentException("Knowledge supersession requires replacementRecordId.", nameof(command));
    }

    public static void ValidateFinish(CortexFinishRequestV2 request)
    {
        RequireNamespace(request.Namespace);
        RequireEnum(request.TaskAction, nameof(request.TaskAction));
        RequireIdentifier(request.TaskId, nameof(request.TaskId));
        if (request.ExpectedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedRevision));
        RequireIdentifier(request.IdempotencyKey, nameof(request.IdempotencyKey));
        RequireText(request.Checkpoint, CortexV2Limits.MaximumCheckpointCharacters, nameof(request.Checkpoint));
        RequireText(request.Evidence, CortexV2Limits.MaximumEvidenceCharacters, nameof(request.Evidence));
        RequireOptionalText(request.Handoff, CortexV2Limits.MaximumHandoffCharacters, nameof(request.Handoff));
        RequireOptionalText(request.TaskTitle, CortexV2Limits.MaximumTitleCharacters, nameof(request.TaskTitle));
        RequireOptionalText(request.TaskContent, CortexV2Limits.MaximumContentCharacters, nameof(request.TaskContent), allowEmpty: true);
        RequireOptionalText(request.BlockedReason, CortexV2Limits.MaximumReasonCharacters, nameof(request.BlockedReason));
        RequireOptionalIdentifier(request.PhaseId, nameof(request.PhaseId));
        RequireOptionalText(
            request.CheckpointSubject,
            CortexV2Limits.MaximumCheckpointSubjectCharacters,
            nameof(request.CheckpointSubject));
        RequireCompactBudget(request.MaxUtf8Bytes);
        ArgumentNullException.ThrowIfNull(request.Knowledge);
        if (request.TaskAction == CortexFinishTaskActionV2.Block && request.BlockedReason is null)
            throw new ArgumentException("A blocked finish requires blockedReason.", nameof(request));
        if (request.Knowledge.Count > CortexV2Limits.MaximumKnowledgeWritesPerFinish)
            throw new ArgumentOutOfRangeException(nameof(request.Knowledge));
        foreach (var value in request.Knowledge)
        {
            ArgumentNullException.ThrowIfNull(value);
            RequireIdentifier(value.RecordId, nameof(value.RecordId));
            RequireIdentifier(value.IdempotencyKey, nameof(value.IdempotencyKey));
            if (value.ExpectedRevision is < 1)
                throw new ArgumentOutOfRangeException(nameof(value.ExpectedRevision));
            RequireText(value.Title, CortexV2Limits.MaximumTitleCharacters, nameof(value.Title));
            RequireText(value.Content, CortexV2Limits.MaximumContentCharacters, nameof(value.Content), allowEmpty: true);
            RequireIdentifiers(value.Tags, CortexV2Limits.MaximumTags, nameof(value.Tags));
            RequireEnum(value.Kind, nameof(value.Kind));
            RequireEnum(value.Tier, nameof(value.Tier));
            ValidateProvenanceInput(value.Provenance);
        }
    }

    public static T RequireBudget<T>(T result, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireBudget(maximumBytes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, CortexV2Json.Options);
        if (bytes.Length > maximumBytes)
            throw new CortexV2ResponseBudgetExceededException(maximumBytes, bytes.Length);
        return result;
    }

    public static void RequireEffectiveNamespaces(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("The provider did not echo the exact effective namespace sequence.");
    }

    public static void RequireNamespaces(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > CortexV2Limits.MaximumNamespaces)
            throw new ArgumentOutOfRangeException(nameof(values));
        RequireIdentifiers(values, CortexV2Limits.MaximumNamespaces, nameof(values));
    }

    public static void RequireNamespace(string value) => RequireIdentifier(value, "namespace");

    public static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CortexV2Limits.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("A bounded portable identifier is required.", parameterName);
        }
    }

    public static void RequireOptionalIdentifier(string? value, string parameterName)
    {
        if (value is not null)
            RequireIdentifier(value, parameterName);
    }

    public static void RequireOptionalCursor(string? value, string parameterName)
    {
        if (value is null)
            return;
        if (value.Length > CortexV2Limits.MaximumContextCursorCharacters)
            throw new ArgumentException("A bounded context cursor is required.", parameterName);
        // Keep the portable identifier alphabet; only cursor length is wider.
        RequireIdentifier(value.Length > CortexV2Limits.MaximumIdentifierCharacters
            ? value[..CortexV2Limits.MaximumIdentifierCharacters] : value, parameterName);
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new ArgumentException("A portable context cursor is required.", parameterName);
    }

    public static void RequireIdentifiers(IReadOnlyList<string> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Identifiers exceed the bound or contain duplicates.", parameterName);
        foreach (var value in values)
            RequireIdentifier(value, parameterName);
    }

    public static void RequireText(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Bounded text is required.", parameterName);
    }

    public static void RequireOptionalText(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is not null)
            RequireText(value, maximum, parameterName, allowEmpty);
    }

    public static void RequirePage(int value)
    {
        if (value is < 1 or > CortexV2Limits.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static void RequireBudget(int value)
    {
        if (value is < CortexV2Limits.MinimumResponseUtf8Bytes or > CortexV2Limits.MaximumResponseUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static void RequireCompactBudget(int value)
    {
        if (value is < CortexV2Limits.MinimumResponseUtf8Bytes or > CortexV2Limits.MaximumCompactResponseUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static void RequireStartBudget(int value)
    {
        if (value is < CortexV2Limits.MinimumResponseUtf8Bytes or > CortexV2Limits.MaximumStartResponseUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void ValidateProvenanceInput(CortexProvenanceInputV2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireEnum(value.Source, nameof(value.Source));
        RequireOptionalIdentifier(value.SourceId, nameof(value.SourceId));
        RequireOptionalText(value.Evidence, CortexV2Limits.MaximumEvidenceCharacters, nameof(value.Evidence));
    }
}

public sealed class CortexV2ResponseBudgetExceededException : InvalidOperationException
{
    public CortexV2ResponseBudgetExceededException(int maximumBytes, int actualBytes)
        : base($"The aggregate V2 response is {actualBytes} UTF-8 bytes and exceeds the {maximumBytes}-byte budget.")
    {
        MaximumBytes = maximumBytes;
        ActualBytes = actualBytes;
    }

    public int MaximumBytes { get; }
    public int ActualBytes { get; }
}
