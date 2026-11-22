using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Metrics;

namespace Pegasus.Cortex.ResourceEfficiency.Caching;

/// <summary>
/// Size-aware LRU cache. Set transfers ownership of accepted and rejected values to the cache.
/// </summary>
public sealed class BoundedResourceCache<TKey, TValue> : IAsyncDisposable
    where TKey : notnull
    where TValue : class
{
    private sealed record CacheEntry(TKey Key, TValue Value, long Bytes);

    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Func<TValue, long> _sizeEstimator;
    private readonly IResourceMetricsSink _metrics;
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _entries;
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _bytes;
    private long _evictions;
    private bool _disposed;

    public BoundedResourceCache(
        int maximumEntries,
        long maximumBytes,
        Func<TValue, long> sizeEstimator,
        IEqualityComparer<TKey>? comparer = null,
        IResourceMetricsSink? metrics = null)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
        _sizeEstimator = sizeEstimator ?? throw new ArgumentNullException(nameof(sizeEstimator));
        _metrics = metrics ?? NullResourceMetricsSink.Instance;
        _entries = new Dictionary<TKey, LinkedListNode<CacheEntry>>(comparer);
    }

    public ResourceCacheSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new ResourceCacheSnapshot(_entries.Count, _bytes, _evictions, _disposed);
        }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out var node))
            {
                value = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public async ValueTask<ResourceCacheMutationResult> SetAsync(
        TKey key,
        TValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var size = _sizeEstimator(value);
        if (size < 0)
            throw new InvalidOperationException("A cache size estimator cannot return a negative value.");

        if (size > _maximumBytes)
        {
            var cleanup = await DisposeValueAsync(value).ConfigureAwait(false);
            if (cleanup is not null)
                ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.CleanupFailures, 1, "failures", "cortex.resource.cache.reject", "failed");
            return new ResourceCacheMutationResult(
                false,
                ResourceDiagnostics.Error(
                    "resource_cache_item_oversize",
                    cleanup is null
                        ? "The cache item exceeds the configured byte budget."
                        : "The cache item exceeds the configured byte budget and cleanup also failed.",
                    "cortex.resource.cache.set",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["maximumBytes"] = _maximumBytes.ToString(),
                        ["cleanupCause"] = cleanup?.GetType().Name
                    }));
        }

        var evicted = new List<TValue>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Remove(key, out var prior))
            {
                _lru.Remove(prior);
                _bytes -= prior.Value.Bytes;
                evicted.Add(prior.Value.Value);
            }

            var node = _lru.AddFirst(new CacheEntry(key, value, size));
            _entries.Add(key, node);
            _bytes += size;

            while (_entries.Count > _maximumEntries || _bytes > _maximumBytes)
            {
                var victim = _lru.Last!;
                _lru.RemoveLast();
                _entries.Remove(victim.Value.Key);
                _bytes -= victim.Value.Bytes;
                _evictions++;
                evicted.Add(victim.Value.Value);
            }
            RecordGauges();
        }

        Exception? cleanupFailure = null;
        foreach (var victim in evicted)
        {
            var failure = await DisposeValueAsync(victim).ConfigureAwait(false);
            cleanupFailure ??= failure;
        }

        if (cleanupFailure is null)
            return new ResourceCacheMutationResult(true, null);

        ResourceMetricRecorder.Gauge(
            _metrics,
            ResourceMetricNames.CleanupFailures,
            1,
            "failures",
            "cortex.resource.cache.evict",
            "failed");
        return new ResourceCacheMutationResult(
            false,
            ResourceDiagnostics.Error(
                "resource_cache_cleanup_failed",
                "A cache entry was evicted but deterministic cleanup failed.",
                "cortex.resource.cache.evict",
                context: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["causeType"] = cleanupFailure.GetType().Name
                }));
    }

    public async ValueTask DisposeAsync()
    {
        TValue[] values;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            values = _lru.Select(entry => entry.Value).ToArray();
            _lru.Clear();
            _entries.Clear();
            _bytes = 0;
            RecordGauges();
        }

        foreach (var value in values)
        {
            var failure = await DisposeValueAsync(value).ConfigureAwait(false);
            if (failure is not null)
                ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.CleanupFailures, 1, "failures", "cortex.resource.cache.dispose", "failed");
        }
    }

    private void RecordGauges()
    {
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.CacheEntries, _entries.Count, "items", "cortex.resource.cache");
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.CacheBytes, _bytes, "bytes", "cortex.resource.cache");
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.CacheEvictions, _evictions, "items", "cortex.resource.cache");
    }

    private static async ValueTask<Exception?> DisposeValueAsync(TValue value)
    {
        try
        {
            if (value is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (value is IDisposable disposable)
                disposable.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
