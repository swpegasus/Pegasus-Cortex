using System.Runtime.CompilerServices;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Cortex.ResourceEfficiency.Caching;
using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Import;
using Pegasus.Cortex.ResourceEfficiency.Metrics;
using Pegasus.Cortex.ResourceEfficiency.Query;
using Pegasus.Cortex.ResourceEfficiency.Retention;
using Pegasus.Shared.Diagnostics;

var tests = new (string Name, Func<Task> Run)[]
{
    ("oversize import rejects before reading", TestOversizeBeforeReadAsync),
    ("query backpressure cancellation and cleanup", TestQueryCancellationCleanupAsync),
    ("query pages are bounded and authorization pushed down", TestBoundedPagedQueryAsync),
    ("cache evicts and disposes by ownership", TestCacheEvictionAsync),
    ("failed import leaves repository unchanged", TestFailedImportRollbackAsync),
    ("terminal success retains no operation resources", TestTerminalResourceCleanupAsync),
    ("retention and metrics remain bounded", TestRetentionAndMetricsBoundsAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static async Task TestOversizeBeforeReadAsync()
{
    var options = Options() with { MaxImportBytes = 1024 };
    var stream = new TrackingStream(new byte[1025]);
    await using var importer = new ResourceEfficientKnowledgeImporter(
        new InMemoryImportRepository(), options, new BoundedResourceMetricsBuffer(32));

    var result = await importer.ImportAsync(stream, 1025, ImportContext(), leaveOpen: false, CancellationToken.None);

    Assert.False(result.Accepted, "Oversize import was accepted.");
    Assert.ContainsCode("resource_import_oversize", result.Diagnostics);
    Assert.Equal(0L, stream.BytesRead);
    Assert.True(stream.Disposed, "Owned oversize stream was not disposed.");
    AssertGateDrained(importer.GateSnapshot);
}

static async Task TestQueryCancellationCleanupAsync()
{
    var options = Options() with { MaxConcurrentQueries = 1, MaxQueuedQueries = 0 };
    var metrics = new BoundedResourceMetricsBuffer(64);
    var repository = new BlockingAuthorizedRepository();
    await using var service = new ResourceEfficientKnowledgeQueryService(repository, options, metrics);
    using var cancellation = new CancellationTokenSource();

    var active = service.SearchPageAsync(
        new ResourceSearchPageRequest("cortex", PageSize: 5),
        ReadScope(),
        cancellation.Token).AsTask();
    await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var rejected = await service.SearchPageAsync(
        new ResourceSearchPageRequest("cortex", PageSize: 5),
        ReadScope(),
        CancellationToken.None);
    Assert.False(rejected.Accepted, "A query bypassed the zero-length backpressure queue.");
    Assert.Equal("resource_operation_backpressure", rejected.Diagnostic!.Code);

    cancellation.Cancel();
    await Assert.ThrowsCanceledAsync(active);
    await repository.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    AssertGateDrained(service.GateSnapshot);
    Assert.True(metrics.Snapshot().Any(metric => metric.Name == "cortex.resource.queue.depth"), "Queue depth metric missing.");
}

static async Task TestBoundedPagedQueryAsync()
{
    const string queryText = "resource-paging-fixture";
    var records = DefaultKnowledgeLibrary.Create().Records
        .Select((record, index) => record with
        {
            Title = $"{queryText} {index:D2}"
        })
        .ToArray();
    var repository = new SeedAuthorizedRepository(records);
    await using var service = new ResourceEfficientKnowledgeQueryService(repository, Options(), new BoundedResourceMetricsBuffer(64));
    var first = await service.SearchPageAsync(
        new ResourceSearchPageRequest(queryText, PageSize: 5),
        ReadScope(),
        CancellationToken.None);
    var second = await service.SearchPageAsync(
        new ResourceSearchPageRequest(queryText, PageSize: 5, ContinuationToken: first.ContinuationToken),
        ReadScope(),
        CancellationToken.None);

    Assert.True(first.Accepted && second.Accepted, "Paged query failed.");
    Assert.Equal(5, first.Records.Count);
    Assert.Equal(5, second.Records.Count);
    Assert.NotNull(first.ContinuationToken, "First page did not provide a continuation.");
    Assert.False(first.Records.Select(r => r.RevisionId).Intersect(second.Records.Select(r => r.RevisionId)).Any(), "Pages overlap.");
    Assert.Equal(Options().MaxRankingCandidates, repository.LastQuery!.MaximumCandidates);
    Assert.True(repository.LastQuery.ReadableNamespaces.Contains(DefaultKnowledgeLibrary.DefaultNamespace), "Authorization was not pushed to the provider.");
    AssertGateDrained(service.GateSnapshot);
}

static async Task TestCacheEvictionAsync()
{
    var metrics = new BoundedResourceMetricsBuffer(64);
    var first = new TrackedResource("first", 4);
    var second = new TrackedResource("second", 4);
    var third = new TrackedResource("third", 4);
    var cache = new BoundedResourceCache<string, TrackedResource>(2, 10, value => value.Bytes, metrics: metrics);

    Assert.True((await cache.SetAsync("first", first)).Accepted, "First cache insert failed.");
    Assert.True((await cache.SetAsync("second", second)).Accepted, "Second cache insert failed.");
    Assert.True(cache.TryGet("first", out _), "Cache promotion lookup failed.");
    Assert.True((await cache.SetAsync("third", third)).Accepted, "Third cache insert failed.");

    var snapshot = cache.Snapshot;
    Assert.Equal(2, snapshot.Entries);
    Assert.Equal(8L, snapshot.Bytes);
    Assert.Equal(1L, snapshot.Evictions);
    Assert.True(second.Disposed, "Least-recently-used entry was not disposed.");
    Assert.False(first.Disposed, "Recently used entry was incorrectly evicted.");

    await cache.DisposeAsync();
    Assert.True(first.Disposed && third.Disposed, "Cache terminal disposal retained owned values.");
    Assert.Equal(0, cache.Snapshot.Entries);
    Assert.True(cache.Snapshot.Disposed, "Cache did not report disposed state.");
    Assert.True(metrics.Snapshot().Any(metric => metric.Name == "cortex.resource.cache.evictions"), "Cache eviction metric missing.");
}

static async Task TestFailedImportRollbackAsync()
{
    var repository = new FailingImportRepository();
    var stream = new TrackingStream(KnowledgeBundleSerializer.Export(DefaultKnowledgeLibrary.Create()));
    await using var importer = new ResourceEfficientKnowledgeImporter(repository, Options(), new BoundedResourceMetricsBuffer(64));

    var result = await importer.ImportAsync(stream, stream.Length, ImportContext(), leaveOpen: false, CancellationToken.None);

    Assert.False(result.Accepted, "Failing repository import unexpectedly succeeded.");
    Assert.Equal(0, repository.Count);
    Assert.True(stream.Disposed, "Failed import retained its owned input stream.");
    AssertGateDrained(importer.GateSnapshot);
}

static async Task TestTerminalResourceCleanupAsync()
{
    var repository = new InMemoryImportRepository();
    var bundle = DefaultKnowledgeLibrary.CreatePriorRelease();
    var stream = new TrackingStream(KnowledgeBundleSerializer.Export(bundle));
    var importer = new ResourceEfficientKnowledgeImporter(repository, Options(), new BoundedResourceMetricsBuffer(64));

    var result = await importer.ImportAsync(stream, stream.Length, ImportContext(), leaveOpen: false, CancellationToken.None);
    Assert.True(result.Accepted,
        $"Valid bounded import failed: {string.Join(", ", result.Diagnostics.Select(value => value.Code))}");
    Assert.Equal(bundle.Records.Count, repository.Count);
    Assert.True(stream.Disposed, "Successful terminal import retained its owned stream.");
    AssertGateDrained(importer.GateSnapshot);

    await importer.DisposeAsync();
    Assert.True(importer.GateSnapshot.Disposed, "Importer gate did not dispose.");
    AssertGateDrained(importer.GateSnapshot);
}

static async Task TestRetentionAndMetricsBoundsAsync()
{
    var options = Options() with { RetentionBatchSize = 3 };
    var repository = new RetentionRepository(Enumerable.Range(1, 7)
        .Select(index => new ResourceRetentionCandidate($"record-{index:D2}", DateTimeOffset.UnixEpoch))
        .ToArray());
    var metrics = new BoundedResourceMetricsBuffer(3);
    var planner = new ResourceRetentionPlanner(repository, options, metrics);
    var page = await planner.PlanAsync(DateTimeOffset.UtcNow, null, CancellationToken.None);

    Assert.Equal(3, page.Candidates.Count);
    Assert.Equal(4, repository.LastRequestedLimit);
    Assert.Equal("record-03", page.ContinuationToken);

    for (var index = 0; index < 5; index++)
        metrics.Record(new ResourceMetricEvent("test.metric", index, "count", "test", "observed", DateTimeOffset.UnixEpoch));
    Assert.Equal(3, metrics.Snapshot().Count);
    Assert.True(metrics.Evictions >= 2, "Metrics buffer did not evict oldest events.");
}

static ResourceEfficiencyOptions Options() => new()
{
    MaxImportBytes = 1024 * 1024,
    MaxBundleRecords = 500,
    MaxEvidencePerRecord = 16,
    MaxSearchPageSize = 10,
    MaxRankedResults = 50,
    MaxRankingCandidates = 500,
    MaxConcurrentQueries = 2,
    MaxQueuedQueries = 2,
    MaxConcurrentImports = 1,
    MaxQueuedImports = 1,
    MaxCacheEntries = 16,
    MaxCacheBytes = 1024 * 1024,
    RetentionBatchSize = 10,
    MaxMetricEvents = 128
};

static KnowledgeImportContext ImportContext() => new(
    "pegasus-nexus",
    "resource-test",
    1,
    CanImport: true,
    CanApprove: true,
    new HashSet<string>([DefaultKnowledgeLibrary.DefaultNamespace], StringComparer.Ordinal));

static KnowledgeReadScope ReadScope() => new(
    new HashSet<string>([DefaultKnowledgeLibrary.DefaultNamespace], StringComparer.Ordinal),
    "internal",
    IncludeStale: true,
    IncludeBlockedOrUnverified: true);

static void AssertGateDrained(ResourceGateSnapshot snapshot)
{
    Assert.Equal(0, snapshot.Active);
    Assert.Equal(0, snapshot.Queued);
}

sealed class TrackingStream : Stream
{
    private readonly MemoryStream _inner;
    public TrackingStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);
    public long BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) { var read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
    public override int Read(Span<byte> buffer) { var read = _inner.Read(buffer); BytesRead += read; return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await _inner.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
    public override int ReadByte() { var value = _inner.ReadByte(); if (value >= 0) BytesRead++; return value; }
    protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; _inner.Dispose(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { Disposed = true; await _inner.DisposeAsync(); GC.SuppressFinalize(this); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class BlockingAuthorizedRepository : IResourceBoundKnowledgeReadRepository
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<KnowledgeRecordV1> StreamAuthorizedCandidatesAsync(
        ResourceAuthorizedKnowledgeQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            Disposed.TrySetResult();
        }
        yield break;
    }
}

sealed class SeedAuthorizedRepository(IReadOnlyList<KnowledgeRecordV1> records) : IResourceBoundKnowledgeReadRepository
{
    public ResourceAuthorizedKnowledgeQuery? LastQuery { get; private set; }

    public async IAsyncEnumerable<KnowledgeRecordV1> StreamAuthorizedCandidatesAsync(
        ResourceAuthorizedKnowledgeQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastQuery = query;
        var count = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!query.ReadableNamespaces.Contains(record.Namespace)) continue;
            if (++count > query.MaximumCandidates) yield break;
            yield return record;
            await Task.Yield();
        }
    }
}

sealed class TrackedResource(string name, long bytes) : IAsyncDisposable
{
    public string Name { get; } = name;
    public long Bytes { get; } = bytes;
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

class InMemoryImportRepository : IKnowledgeRevisionRepository
{
    protected readonly Dictionary<string, KnowledgeRecordV1> Revisions = new(StringComparer.Ordinal);
    protected readonly Dictionary<string, string> Current = new(StringComparer.Ordinal);
    public int Count => Revisions.Count;

    public ValueTask<KnowledgeRecordV1?> FindRevisionAsync(string revisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Revisions.GetValueOrDefault(revisionId));
    }

    public ValueTask<KnowledgeRecordV1?> FindCurrentAsync(string recordId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Current.TryGetValue(recordId, out var revisionId) ? Revisions[revisionId] : null);
    }

    public virtual ValueTask<KnowledgeRepositoryImportResult> ImportImmutableAsync(
        IReadOnlyList<KnowledgeImportItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            Current.TryGetValue(item.Record.RecordId, out var current);
            if (Revisions.ContainsKey(item.Record.RevisionId) || current != item.ExpectedCurrentRevisionId)
                return ValueTask.FromResult(Failure("resource_test_repository_conflict"));
        }
        foreach (var item in items)
        {
            Revisions.Add(item.Record.RevisionId, item.Record);
            Current[item.Record.RecordId] = item.Record.RevisionId;
        }
        return ValueTask.FromResult(new KnowledgeRepositoryImportResult(true, items.Count, null));
    }

    protected static KnowledgeRepositoryImportResult Failure(string code) => new(
        false,
        0,
        new PegasusDiagnostic(code, "The transactional test repository rejected the batch.", DiagnosticSeverity.Error, "test.resource.repository"));
}

sealed class FailingImportRepository : InMemoryImportRepository
{
    public override ValueTask<KnowledgeRepositoryImportResult> ImportImmutableAsync(
        IReadOnlyList<KnowledgeImportItem> items,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Failure("resource_test_repository_rollback"));
}

sealed class RetentionRepository(IReadOnlyList<ResourceRetentionCandidate> candidates) : IResourceRetentionRepository
{
    public int LastRequestedLimit { get; private set; }
    public ValueTask<IReadOnlyList<ResourceRetentionCandidate>> ReadExpiredPageAsync(
        DateTimeOffset now,
        string? afterRecordId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestedLimit = limit;
        var page = candidates.Where(item => afterRecordId is null || string.CompareOrdinal(item.RecordId, afterRecordId) > 0)
            .Take(limit).ToArray();
        return ValueTask.FromResult<IReadOnlyList<ResourceRetentionCandidate>>(page);
    }
}

static class Assert
{
    public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void False(bool value, string message) => True(!value, message);
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', received '{actual}'."); }
    public static void NotNull(object? value, string message) { if (value is null) throw new InvalidOperationException(message); }
    public static void ContainsCode(string code, IReadOnlyList<PegasusDiagnostic> diagnostics) { if (!diagnostics.Any(item => item.Code == code)) throw new InvalidOperationException($"Expected diagnostic '{code}'."); }
    public static async Task ThrowsCanceledAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected operation cancellation.");
    }
}
