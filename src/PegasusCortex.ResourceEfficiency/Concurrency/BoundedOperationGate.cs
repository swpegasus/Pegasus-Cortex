using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Metrics;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.ResourceEfficiency.Concurrency;

public sealed record ResourceGateEnterResult(
    bool Accepted,
    ResourceOperationLease? Lease,
    PegasusDiagnostic? Diagnostic);

public sealed class BoundedOperationGate : IAsyncDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly int _maximumQueued;
    private readonly string _operation;
    private readonly IResourceMetricsSink _metrics;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _queued;
    private int _disposed;

    public BoundedOperationGate(
        int maximumConcurrency,
        int maximumQueued,
        string operation,
        IResourceMetricsSink? metrics = null)
    {
        if (maximumConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (maximumQueued < 0) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _slots = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        _maximumQueued = maximumQueued;
        _operation = operation;
        _metrics = metrics ?? NullResourceMetricsSink.Instance;
    }

    public ResourceGateSnapshot Snapshot => new(
        Volatile.Read(ref _active),
        Volatile.Read(ref _queued),
        Volatile.Read(ref _disposed) == 1);

    public async ValueTask<ResourceGateEnterResult> EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_slots.Wait(0))
            return AcceptedLease();

        var queued = Interlocked.Increment(ref _queued);
        RecordQueueDepth(queued);
        if (queued > _maximumQueued)
        {
            Interlocked.Decrement(ref _queued);
            TrySignalDrained();
            return new ResourceGateEnterResult(
                false,
                null,
                ResourceDiagnostics.Error(
                    "resource_operation_backpressure",
                    "The bounded operation queue is full; retry after in-flight work completes.",
                    _operation,
                    retryable: true,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["maximumQueued"] = _maximumQueued.ToString()
                    }));
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
            return AcceptedLease();
        }
        finally
        {
            RecordQueueDepth(Interlocked.Decrement(ref _queued));
            TrySignalDrained();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _disposeCancellation.Cancel();
        TrySignalDrained();
        await _drained.Task.ConfigureAwait(false);
        _slots.Dispose();
        _disposeCancellation.Dispose();
    }

    internal void Release()
    {
        _slots.Release();
        var active = Interlocked.Decrement(ref _active);
        if (active == 0)
            TrySignalDrained();
    }

    private ResourceGateEnterResult AcceptedLease()
    {
        Interlocked.Increment(ref _active);
        if (Volatile.Read(ref _disposed) == 1)
        {
            Interlocked.Decrement(ref _active);
            _slots.Release();
            TrySignalDrained();
            throw new ObjectDisposedException(nameof(BoundedOperationGate));
        }
        return new ResourceGateEnterResult(true, new ResourceOperationLease(this), null);
    }

    private void RecordQueueDepth(int value) =>
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.QueueDepth, value, "items", _operation);

    private void TrySignalDrained()
    {
        if (Volatile.Read(ref _disposed) == 1 &&
            Volatile.Read(ref _active) == 0 &&
            Volatile.Read(ref _queued) == 0)
            _drained.TrySetResult();
    }
}

public sealed class ResourceOperationLease : IAsyncDisposable
{
    private BoundedOperationGate? _owner;

    internal ResourceOperationLease(BoundedOperationGate owner) => _owner = owner;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _owner, null)?.Release();
        return ValueTask.CompletedTask;
    }
}
