namespace Pegasus.Cortex.ResourceEfficiency.Metrics;

public sealed record ResourceMetricEvent(
    string Name,
    double Value,
    string Unit,
    string Operation,
    string Outcome,
    DateTimeOffset ObservedAt);

public interface IResourceMetricsSink
{
    void Record(ResourceMetricEvent metric);
}

public sealed class NullResourceMetricsSink : IResourceMetricsSink
{
    public static NullResourceMetricsSink Instance { get; } = new();
    private NullResourceMetricsSink() { }
    public void Record(ResourceMetricEvent metric) { }
}

/// <summary>
/// Content-safe, bounded diagnostic buffer. Oldest metrics are evicted instead of growing unbounded.
/// </summary>
public sealed class BoundedResourceMetricsBuffer(int capacity) : IResourceMetricsSink
{
    private readonly object _gate = new();
    private readonly Queue<ResourceMetricEvent> _events = new(capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity)));

    public long Evictions { get; private set; }

    public void Record(ResourceMetricEvent metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        lock (_gate)
        {
            while (_events.Count >= capacity)
            {
                _events.Dequeue();
                Evictions++;
            }
            _events.Enqueue(metric);
        }
    }

    public IReadOnlyList<ResourceMetricEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }
}

internal static class ResourceMetricNames
{
    public const string MemoryBytes = "cortex.resource.memory.bytes";
    public const string OperationLatencyMilliseconds = "cortex.resource.operation.latency";
    public const string QueueDepth = "cortex.resource.queue.depth";
    public const string CacheEntries = "cortex.resource.cache.entries";
    public const string CacheBytes = "cortex.resource.cache.bytes";
    public const string CacheEvictions = "cortex.resource.cache.evictions";
    public const string CleanupFailures = "cortex.resource.cleanup.failures";
}

internal static class ResourceMetricRecorder
{
    public static void Gauge(
        IResourceMetricsSink sink,
        string name,
        double value,
        string unit,
        string operation,
        string outcome = "observed") =>
        sink.Record(new ResourceMetricEvent(name, value, unit, operation, outcome, DateTimeOffset.UtcNow));
}
