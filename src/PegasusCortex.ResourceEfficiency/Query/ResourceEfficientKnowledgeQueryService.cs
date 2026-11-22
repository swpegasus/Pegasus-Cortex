using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Query;
using Pegasus.Cortex.ResourceEfficiency.Concurrency;
using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Metrics;

namespace Pegasus.Cortex.ResourceEfficiency.Query;

public sealed class ResourceEfficientKnowledgeQueryService : IAsyncDisposable
{
    private readonly IResourceBoundKnowledgeReadRepository _repository;
    private readonly ResourceEfficiencyOptions _options;
    private readonly IResourceMetricsSink _metrics;
    private readonly BoundedOperationGate _gate;

    public ResourceEfficientKnowledgeQueryService(
        IResourceBoundKnowledgeReadRepository repository,
        ResourceEfficiencyOptions options,
        IResourceMetricsSink? metrics = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? NullResourceMetricsSink.Instance;
        _gate = new BoundedOperationGate(
            options.MaxConcurrentQueries,
            options.MaxQueuedQueries,
            "cortex.resource.query",
            _metrics);
    }

    public ResourceGateSnapshot GateSnapshot => _gate.Snapshot;

    public async ValueTask<ResourceSearchPageResult> SearchPageAsync(
        ResourceSearchPageRequest request,
        KnowledgeReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ReadableNamespaces.Count == 0)
            return Reject("resource_query_namespace_scope_empty", "No server-derived readable namespace scope was supplied.");
        if (request.PageSize is < 1 || request.PageSize > _options.MaxSearchPageSize || request.Text.Length > 512)
            return Reject("resource_query_page_invalid", "The search page size or text exceeds the configured bound.");

        var fingerprint = ComputeFingerprint(request);
        if (!TryDecodeContinuation(request.ContinuationToken, fingerprint, out var offset))
            return Reject("resource_query_continuation_invalid", "The continuation token is malformed or belongs to a different query.");
        if (offset >= _options.MaxRankedResults)
            return Reject("resource_query_continuation_exhausted", "The continuation exceeds the bounded ranking window.");

        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(false);
        ResourceOperationLease? lease = null;
        var outcome = "rejected";
        try
        {
            var entered = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Accepted)
                return new ResourceSearchPageResult(false, [], null, entered.Diagnostic);
            lease = entered.Lease;

            var authorizedQuery = new ResourceAuthorizedKnowledgeQuery(
                request.Taxonomy,
                request.ProductId,
                request.ComponentId,
                scope.ReadableNamespaces,
                scope.MaximumSensitivity,
                scope.IncludeStale,
                scope.IncludeBlockedOrUnverified,
                _options.MaxRankingCandidates);

            var candidates = new List<KnowledgeRecordV1>(Math.Min(64, _options.MaxRankingCandidates));
            await foreach (var record in _repository
                .StreamAuthorizedCandidatesAsync(authorizedQuery, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (candidates.Count >= _options.MaxRankingCandidates)
                {
                    return Reject(
                        "resource_query_candidate_overrun",
                        "The provider exceeded the bounded authorized candidate window.");
                }
                candidates.Add(record);
            }

            var requestedWindow = Math.Min(
                offset + request.PageSize + 1,
                _options.MaxRankedResults);
            var baseQuery = new KnowledgeQueryService(new FixedKnowledgeReadRepository(candidates));
            var ranked = await baseQuery.SearchAsync(
                new KnowledgeSearchRequest(
                    request.Text,
                    request.Taxonomy,
                    request.ProductId,
                    request.ComponentId,
                    requestedWindow),
                scope,
                cancellationToken).ConfigureAwait(false);
            if (!ranked.Accepted)
                return new ResourceSearchPageResult(false, [], null, ranked.Diagnostic);

            var page = ranked.Records.Skip(offset).Take(request.PageSize).ToArray();
            var hasMore = ranked.Records.Count > offset + page.Length &&
                offset + page.Length < _options.MaxRankedResults;
            var continuation = hasMore
                ? EncodeContinuation(offset + page.Length, fingerprint)
                : null;
            outcome = "accepted";
            return new ResourceSearchPageResult(true, page, continuation, null);
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
            RecordOperationMetrics(stopwatch.Elapsed, memoryBefore, outcome);
        }
    }

    public ValueTask DisposeAsync() => _gate.DisposeAsync();

    private void RecordOperationMetrics(TimeSpan elapsed, long memoryBefore, string outcome)
    {
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.OperationLatencyMilliseconds, elapsed.TotalMilliseconds, "milliseconds", "cortex.resource.query", outcome);
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.MemoryBytes, GC.GetTotalMemory(false), "bytes", "cortex.resource.query", outcome);
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.MemoryBytes, memoryBefore, "bytes", "cortex.resource.query.start", "observed");
    }

    private static string ComputeFingerprint(ResourceSearchPageRequest request)
    {
        var canonical = $"{request.Text}\n{request.Taxonomy}\n{request.ProductId}\n{request.ComponentId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string EncodeContinuation(int offset, string fingerprint)
    {
        var bytes = Encoding.UTF8.GetBytes($"1|{offset}|{fingerprint}");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeContinuation(string? token, string fingerprint, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(token))
            return true;

        try
        {
            var value = token.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|');
            return parts.Length == 3 && parts[0] == "1" &&
                int.TryParse(parts[1], out offset) && offset >= 0 &&
                string.Equals(parts[2], fingerprint, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ResourceSearchPageResult Reject(string code, string message) =>
        new(false, [], null, ResourceDiagnostics.Error(code, message, "cortex.resource.query"));

    private sealed class FixedKnowledgeReadRepository(IReadOnlyList<KnowledgeRecordV1> records) : IKnowledgeReadRepository
    {
        public ValueTask<IReadOnlyList<KnowledgeRecordV1>> ReadCandidatesAsync(
            KnowledgeRepositoryQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(records);
        }
    }
}
