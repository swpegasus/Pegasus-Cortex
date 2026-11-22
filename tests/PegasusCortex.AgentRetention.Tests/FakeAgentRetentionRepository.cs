using System.Runtime.CompilerServices;
using Pegasus.Cortex.Core.Retention;

/// <summary>
/// Mirrors the durable store's externally visible ordering: idempotency is
/// resolved before revision-head compare-and-swap.
/// </summary>
internal sealed class FakeAgentRetentionRepository : ICortexAgentRetentionRepository
{
    private readonly Dictionary<DocumentKey, CortexRetainedDocument> _revisions = [];
    private readonly Dictionary<HeadKey, CortexRetainedDocument> _heads = [];
    private readonly Dictionary<IdempotencyKey, CortexRetainedDocument> _idempotency = [];

    public int AppendCalls { get; private set; }
    public int FindRevisionCalls { get; private set; }
    public int DocumentCount => _revisions.Count;
    public CortexRetentionAppendRequest? LastAppendRequest { get; private set; }
    public CortexRetentionAppendResult? ForcedAppendResult { get; set; }

    public ValueTask<CortexRetentionAppendResult> AppendAsync(
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendCalls++;
        var captured = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        LastAppendRequest = captured;
        if (ForcedAppendResult is not null)
            return ValueTask.FromResult(ForcedAppendResult);

        var idempotencyKey = new IdempotencyKey(
            captured.ProjectId,
            captured.Kind,
            captured.ProducerId,
            captured.IdempotencyKey);
        if (_idempotency.TryGetValue(idempotencyKey, out var replay))
        {
            return ValueTask.FromResult(IsExactReplay(replay, captured)
                ? new CortexRetentionAppendResult(
                    CortexRetentionWriteDispositions.Idempotent,
                    replay.Snapshot(),
                    replay.ContentHash)
                : new CortexRetentionAppendResult(
                    CortexRetentionWriteDispositions.Conflict,
                    null,
                    replay.ContentHash));
        }

        var headKey = new HeadKey(captured.ProjectId, captured.Kind, captured.DocumentId);
        _heads.TryGetValue(headKey, out var head);
        var matchesHead = captured.Revision == 1
            ? head is null && captured.ExpectedPreviousHash is null
            : head is not null &&
                captured.Revision == head.Revision + 1 &&
                string.Equals(
                    captured.ExpectedPreviousHash,
                    head.ContentHash,
                    StringComparison.Ordinal);
        if (!matchesHead)
        {
            return ValueTask.FromResult(new CortexRetentionAppendResult(
                CortexRetentionWriteDispositions.StaleBase,
                head?.Snapshot(),
                head?.ContentHash));
        }

        var retained = ToDocument(captured);
        _revisions.Add(
            new DocumentKey(
                retained.ProjectId,
                retained.Kind,
                retained.DocumentId,
                retained.Revision),
            retained);
        _heads[headKey] = retained;
        _idempotency.Add(idempotencyKey, retained);
        return ValueTask.FromResult(new CortexRetentionAppendResult(
            CortexRetentionWriteDispositions.Appended,
            retained.Snapshot(),
            retained.ContentHash));
    }

    public ValueTask<CortexRetainedDocument?> FindHeadAsync(
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _heads.TryGetValue(new HeadKey(projectId, kind, documentId), out var retained);
        return ValueTask.FromResult(retained?.Snapshot());
    }

    public ValueTask<CortexRetainedDocument?> FindRevisionAsync(
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FindRevisionCalls++;
        _revisions.TryGetValue(
            new DocumentKey(projectId, kind, documentId, revision),
            out var retained);
        return ValueTask.FromResult(retained?.Snapshot());
    }

    public ValueTask<CortexRetentionHeadPage> ListHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken) =>
        ListHeadsByKindCore(
            projectId,
            kind,
            afterDocumentId,
            limit,
            includeExpired: false,
            cancellationToken: cancellationToken);

    public ValueTask<CortexRetentionHeadPage> ListAllHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken) =>
        ListHeadsByKindCore(
            projectId,
            kind,
            afterDocumentId,
            limit,
            includeExpired: true,
            cancellationToken: cancellationToken);

    private ValueTask<CortexRetentionHeadPage> ListHeadsByKindCore(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        bool includeExpired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or >
            CortexAgentRetentionLimits.MaximumKindHeadPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        var candidates = _heads.Values
            .Where(item =>
                string.Equals(item.ProjectId, projectId, StringComparison.Ordinal) &&
                string.Equals(item.Kind, kind, StringComparison.Ordinal) &&
                (afterDocumentId is null ||
                 string.CompareOrdinal(item.DocumentId, afterDocumentId) > 0) &&
                (includeExpired || item.ExpiresAt > DateTimeOffset.UtcNow))
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToArray();
        var hasMore = candidates.Length > limit;
        var items = candidates
            .Take(limit)
            .Select(item => item.Snapshot())
            .ToArray();
        return ValueTask.FromResult(new CortexRetentionHeadPage(
            Array.AsReadOnly(items),
            hasMore ? items[^1].DocumentId : null));
    }

    public async IAsyncEnumerable<CortexRetainedDocument> ReadRevisionRangeAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (firstRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(firstRevision));
        if (lastRevision < firstRevision ||
            lastRevision - firstRevision + 1 >
                CortexAgentRetentionLimits.MaximumRevisionRangeDocuments)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        for (var revision = firstRevision; ; revision++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_revisions.TryGetValue(
                    new DocumentKey(projectId, kind, documentId, revision),
                    out var retained))
            {
                yield return retained.Snapshot();
            }
            if (revision == lastRevision)
                break;
        }
    }

    public ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsAsync(
        string projectId,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<CortexRetentionSearchHit>>([]);
    }


    public void CorruptCanonicalBytes(
        string projectId,
        string kind,
        string documentId,
        long revision)
    {
        var key = new DocumentKey(projectId, kind, documentId, revision);
        var retained = _revisions[key];
        var corruptBytes = retained.CanonicalUtf8.ToArray();
        corruptBytes[0] ^= 1;
        var corrupt = retained with { CanonicalUtf8 = corruptBytes };
        _revisions[key] = corrupt;

        var headKey = new HeadKey(projectId, kind, documentId);
        if (_heads.TryGetValue(headKey, out var head) && head.Revision == revision)
            _heads[headKey] = corrupt;
    }

    public IReadOnlyList<CortexRetainedDocument> SnapshotDocuments() =>
        Array.AsReadOnly(_revisions.Values
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.Revision)
            .Select(item => item.Snapshot())
            .ToArray());

    public void RemoveRevision(
        string projectId,
        string kind,
        string documentId,
        long revision) =>
        _revisions.Remove(new DocumentKey(projectId, kind, documentId, revision));

    private static bool IsExactReplay(
        CortexRetainedDocument retained,
        CortexRetentionAppendRequest requested) =>
        string.Equals(retained.ProjectId, requested.ProjectId, StringComparison.Ordinal) &&
        string.Equals(retained.Kind, requested.Kind, StringComparison.Ordinal) &&
        string.Equals(retained.DocumentId, requested.DocumentId, StringComparison.Ordinal) &&
        retained.Revision == requested.Revision &&
        string.Equals(retained.PreviousHash, requested.ExpectedPreviousHash, StringComparison.Ordinal) &&
        string.Equals(retained.IdempotencyKey, requested.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(retained.ProducerId, requested.ProducerId, StringComparison.Ordinal) &&
        string.Equals(retained.ContractId, requested.ContractId, StringComparison.Ordinal) &&
        string.Equals(retained.ContentHash, requested.ContentHash, StringComparison.Ordinal) &&
        retained.CanonicalUtf8.AsSpan().SequenceEqual(requested.CanonicalUtf8) &&
        string.Equals(retained.SearchText, requested.SearchText, StringComparison.Ordinal) &&
        retained.CapturedAt == requested.CapturedAt &&
        retained.ExpiresAt == requested.ExpiresAt;

    private static CortexRetainedDocument ToDocument(CortexRetentionAppendRequest request) => new(
        request.ProjectId,
        request.Kind,
        request.DocumentId,
        request.Revision,
        request.ExpectedPreviousHash,
        request.IdempotencyKey,
        request.ProducerId,
        request.ContractId,
        request.CanonicalUtf8.ToArray(),
        request.ContentHash,
        request.SearchText,
        request.CapturedAt,
        request.ExpiresAt);

    private sealed record DocumentKey(
        string ProjectId,
        string Kind,
        string DocumentId,
        long Revision);

    private sealed record HeadKey(string ProjectId, string Kind, string DocumentId);

    private sealed record IdempotencyKey(
        string ProjectId,
        string Kind,
        string ProducerId,
        string Value);
}
