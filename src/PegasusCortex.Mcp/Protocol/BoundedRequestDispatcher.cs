using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

internal enum DispatchState { Completed, NotDispatched, Unknown, CapacityExceeded }
internal sealed record DispatchOutcome(DispatchState State, JsonObject? Response = null);

/// <summary>Transient scheduling only. A transport result is never a durable commit receipt.</summary>
internal sealed class BoundedRequestDispatcher
{
    internal const int MaximumActiveCallbacks = 4;
    internal const int MaximumSessionRequests = 32;
    internal const int MaximumRequests = 96;
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<(string Session, string Stream), StreamQueue> _streams = new();
    private readonly Dictionary<string, OwnerQueue> _owners = new(StringComparer.Ordinal);
    private readonly LinkedList<OwnerQueue> _ready = new();
    private readonly int _workers;
    // Deterministic admission interleaving seam; resident callers leave it null.
    private readonly Action? _beforePreparation;
    private int _running, _count, _bytes;
    private TaskCompletionSource _idle = CompletedSignal();

    internal BoundedRequestDispatcher(int workers = MaximumActiveCallbacks, Action? beforePreparation = null)
    {
        if (workers is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(workers));
        _workers = workers; _beforePreparation = beforePreparation;
    }
    private static TaskCompletionSource CompletedSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
    internal Task WhenIdleAsync() { lock (_gate) return _idle.Task; }
    internal int ReadyCount { get { lock (_gate) return _owners.Values.Sum(owner => owner.Ready.Count); } }
    internal (int Running, int Count, int Bytes) Snapshot
    { get { lock (_gate) return (_running, _count, _bytes); } }

    internal bool TrySubmit(string sessionId, int payloadBytes,
        Func<CancellationToken, ValueTask<JsonObject>> operation, CancellationToken cancellationToken,
        TimeSpan timeout, out DispatchTicket ticket, string? streamId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(operation);
        if (payloadBytes is < 1 or > McpProtocol.MaximumRequestBytes || timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        Entry entry;
        lock (_gate)
        {
            _owners.TryGetValue(sessionId, out var owner);
            if (_count >= MaximumRequests || _bytes > MaximumPayloadBytes - payloadBytes ||
                owner?.Count >= MaximumSessionRequests)
            { ticket = DispatchTicket.Rejected; return false; }
            if (owner is null) _owners.Add(sessionId, owner = new OwnerQueue(sessionId));
            var key = (sessionId, streamId ?? "");
            if (!_streams.TryGetValue(key, out var stream)) _streams.Add(key, stream = new StreamQueue(key, owner));
            ticket = new DispatchTicket(cancellationToken);
            entry = new Entry(stream, ticket, operation, payloadBytes);
            entry.Node = stream.Queue.AddLast(entry);
            if (_count == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stream.Count++; owner.Count++; _count++; _bytes += payloadBytes;
        }
        // Registration can synchronously invoke cancellation. Do it outside the
        // scheduler lock; disposing a linked CTS may wait for its parent callback.
        try { _beforePreparation?.Invoke(); }
        catch
        {
            Cancel(entry); ticket.Release(); throw;
        }
        var registration = ticket.Source!.Token.Register(() => Cancel(entry));
        ticket.Source.CancelAfter(timeout);
        bool settled;
        lock (_gate)
        {
            ticket.Registration = registration; entry.Prepared = true; settled = entry.Settled;
            if (!settled) { Enqueue(entry.Stream); Pump(); }
        }
        if (settled) ticket.Release();
        return true;
    }

    private void Cancel(Entry entry)
    {
        bool cleanup;
        lock (_gate)
        {
            if (entry.Settled) return;
            entry.Ticket.CompletionSource.TrySetResult(new(entry.Started ? DispatchState.Unknown : DispatchState.NotDispatched));
            if (entry.Started) return;
            entry.Stream.Queue.Remove(entry.Node!); Release(entry); cleanup = entry.Prepared;
            Enqueue(entry.Stream); Pump();
        }
        if (cleanup) entry.Ticket.Release();
    }
    private void Enqueue(StreamQueue stream)
    {
        if (stream.Running || stream.ReadyNode is not null || stream.Queue.First is null || !stream.Queue.First.Value.Prepared) return;
        stream.ReadyNode = stream.Owner.Ready.AddLast(stream);
        EnqueueOwner(stream.Owner);
    }
    private void EnqueueOwner(OwnerQueue owner)
    {
        if (owner.ReadyNode is null && owner.Ready.Count != 0) owner.ReadyNode = _ready.AddLast(owner);
    }
    private void Pump()
    {
        while (_running < _workers && _ready.First is not null)
        {
            var owner = _ready.First.Value; _ready.RemoveFirst(); owner.ReadyNode = null;
            var stream = owner.Ready.First!.Value; owner.Ready.RemoveFirst(); stream.ReadyNode = null;
            EnqueueOwner(owner);
            // Canceling a prepared head can expose a successor whose registration
            // is still being installed outside the lock. Its admission will
            // enqueue it again after preparation; it cannot dispatch early.
            if (stream.Queue.First is null || !stream.Queue.First.Value.Prepared) continue;
            var entry = stream.Queue.First.Value; stream.Queue.RemoveFirst(); entry.Node = null;
            entry.Started = true; stream.Running = true; _running++;
            // Round-robin connections first, then their streams: opening32
            // independent streams cannot buy32 turns ahead of another client.
            // One active callback per stream retains dependent create->read FIFO.
            _ = Task.Run(() => ExecuteAsync(entry));
        }
    }
    private async Task ExecuteAsync(Entry entry)
    {
        try
        {
            var response = await entry.Operation(entry.Ticket.Source!.Token).ConfigureAwait(false);
            entry.Ticket.CompletionSource.TrySetResult(new(DispatchState.Completed, response));
        }
        catch (Exception)
        {
            // Cancellation/lost reply cannot prove rollback after dispatch. No replay.
            entry.Ticket.CompletionSource.TrySetResult(new(DispatchState.Unknown));
        }
        finally
        {
            // Linked cancellation cleanup can wait for a parent callback. Keep
            // its worker/dependencies owned, but hold no scheduler lock here.
            entry.Ticket.Release();
            lock (_gate)
            {
                entry.Stream.Running = false; _running--; Release(entry); Enqueue(entry.Stream); Pump();
            }
        }
    }
    private void Release(Entry entry)
    {
        entry.Settled = true;
        var stream = entry.Stream; var owner = stream.Owner;
        stream.Count--; owner.Count--; _count--; _bytes -= entry.PayloadBytes;
        if (stream.Count == 0)
        {
            if (stream.ReadyNode is not null) { owner.Ready.Remove(stream.ReadyNode); stream.ReadyNode = null; }
            _streams.Remove(stream.Key);
        }
        if (owner.Ready.Count == 0 && owner.ReadyNode is not null) { _ready.Remove(owner.ReadyNode); owner.ReadyNode = null; }
        if (owner.Count == 0) _owners.Remove(owner.Id);
        // Keep noncooperative work and its dependency lifetime counted until it
        // actually settles, even when its terminal transport result is Unknown.
        if (_count == 0) _idle.TrySetResult();
    }
    private sealed class OwnerQueue(string id)
    {
        internal readonly string Id = id;
        internal readonly LinkedList<StreamQueue> Ready = new();
        internal LinkedListNode<OwnerQueue>? ReadyNode;
        internal int Count;
    }
    private sealed class StreamQueue((string Session, string Stream) key, OwnerQueue owner)
    {
        internal readonly (string Session, string Stream) Key = key;
        internal readonly OwnerQueue Owner = owner;
        internal readonly LinkedList<Entry> Queue = new();
        internal LinkedListNode<StreamQueue>? ReadyNode;
        internal int Count;
        internal bool Running;
    }
    private sealed class Entry(StreamQueue stream, DispatchTicket ticket,
        Func<CancellationToken, ValueTask<JsonObject>> operation, int bytes)
    {
        internal readonly StreamQueue Stream = stream;
        internal readonly DispatchTicket Ticket = ticket;
        internal readonly Func<CancellationToken, ValueTask<JsonObject>> Operation = operation;
        internal readonly int PayloadBytes = bytes;
        internal LinkedListNode<Entry>? Node;
        internal bool Started, Settled, Prepared;
    }
}

internal sealed class DispatchTicket
{
    internal readonly TaskCompletionSource<DispatchOutcome> CompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource? Source;
    internal CancellationTokenRegistration Registration;
    private DispatchTicket(DispatchOutcome outcome) => CompletionSource.SetResult(outcome);
    internal DispatchTicket(CancellationToken token) => Source = CancellationTokenSource.CreateLinkedTokenSource(token);
    internal Task<DispatchOutcome> Completion => CompletionSource.Task;
    internal static DispatchTicket Rejected => new(new DispatchOutcome(DispatchState.CapacityExceeded));
    internal static DispatchTicket Completed(JsonObject response) => new(new DispatchOutcome(DispatchState.Completed, response));
    internal void Cancel() { try { Source?.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Release() { Registration.Unregister(); Source?.Dispose(); }
}
