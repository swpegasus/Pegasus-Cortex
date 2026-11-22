using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

/// <summary>Connection-local IDs, ordered replies and finite transport drain.</summary>
internal sealed class CorrelatedRequestSession : IDisposable
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    private readonly BoundedRequestDispatcher _dispatcher;
    private readonly string _sessionId;
    private readonly Func<JsonObject, CancellationToken, ValueTask> _write;
    private readonly Func<DispatchState, JsonNode?, JsonObject> _error;
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<string, DispatchTicket> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private readonly HashSet<Task> _writes = new();
    private Exception? _writeFailure;
    internal int OutstandingWrites { get { lock (_gate) return _writes.Count; } }
    private readonly SemaphoreSlim _writer = new(1, 1);
    private bool _multiplex;
    private int _replyCount;
    internal void EnableMultiplexing() { lock (_gate) _multiplex = true; }

    internal CorrelatedRequestSession(BoundedRequestDispatcher dispatcher, string sessionId,
        Func<JsonObject, CancellationToken, ValueTask> write,
        Func<DispatchState, JsonNode?, JsonObject> error, CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher; _sessionId = sessionId; _write = write; _error = error;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }
    internal CancellationToken Token => _stop.Token;

    internal void Submit(string key, JsonNode id, int bytes,
        Func<CancellationToken, ValueTask<JsonObject>> operation, TimeSpan? timeout = null, string? streamId = null)
    {
        lock (_gate)
        {
            Reserve(key);
            var stream = _multiplex ? streamId ?? key : null;
            _dispatcher.TrySubmit(_sessionId, bytes, operation, Token, timeout ?? RequestTimeout, out var ticket, stream);
            _pending.Add(key, ticket); _replyCount++;
            QueueReply(key, id, ticket);
        }
    }

    internal void Reply(string? key, JsonNode? id, JsonObject response)
    {
        lock (_gate)
        {
            Reserve(key);
            var ticket = DispatchTicket.Completed(response);
            if (key is not null) _pending.Add(key, ticket);
            _replyCount++;
            QueueReply(key, id, ticket);
        }
    }

    private void QueueReply(string? key, JsonNode? id, DispatchTicket ticket)
    {
        var write = WriteAfterAsync(_multiplex ? Task.CompletedTask : _tail, key, id, ticket);
        if (!_multiplex) _tail = write;
        _writes.Add(write);
        _ = write.ContinueWith(completed =>
        {
            lock (_gate)
            {
                _writes.Remove(completed);
                if (completed.IsFaulted) _writeFailure ??= completed.Exception!.GetBaseException();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void RequireAvailableId(string key) { lock (_gate) Reserve(key); }
    private void Reserve(string? key)
    {
        // Reusing a live ID makes any second reply ambiguous. Close the session
        // before dispatching the duplicate instead of returning a second result.
        if (key is not null && _pending.ContainsKey(key)) throw new InvalidDataException("request_id_duplicate");
        if (_replyCount >= BoundedRequestDispatcher.MaximumSessionRequests)
            throw new InvalidDataException("request_pending_limit");
    }

    internal bool Cancel(string key)
    {
        DispatchTicket? ticket;
        lock (_gate) _pending.TryGetValue(key, out ticket);
        ticket?.Cancel(); return ticket is not null;
    }

    private async Task WriteAfterAsync(Task previous, string? key, JsonNode? id, DispatchTicket ticket)
    {
        try
        {
            await previous.ConfigureAwait(false);
            var outcome = await ticket.Completion.ConfigureAwait(false);
            Token.ThrowIfCancellationRequested();
            await _writer.WaitAsync(Token).ConfigureAwait(false);
            try { await _write(outcome.Response ?? _error(outcome.State, id), Token).ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        catch { _stop.Cancel(); throw; }
        finally { lock (_gate) { if (key is not null) _pending.Remove(key); _replyCount--; } }
    }

    internal async Task DrainAsync(TimeSpan? timeout = null)
    {
        Task[] writes;
        lock (_gate)
        {
            if (_writeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_writeFailure).Throw();
            writes = _writes.ToArray();
        }
        try { await Task.WhenAll(writes).WaitAsync(timeout ?? DrainTimeout, Token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _stop.Cancel();
            throw new IOException("request_drain_incomplete_outcome_unknown");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Observe late write failures without waiting forever for an operation
        // which ignores cancellation. The resident dispatcher still counts it.
        Task cleanup;
        lock (_gate) cleanup = Task.WhenAll(_writes);
        _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // A queued writer may still use this token source; its lifetime ends when
        // the finite response chain settles, not when the reader disconnects.
        _ = cleanup.ContinueWith(_ => { _stop.Dispose(); _writer.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String
        ? "s:" + id.GetString() : "n:" + id.GetRawText();

    internal static bool ValidId(JsonElement id) => id.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
        (id.ValueKind == JsonValueKind.String ? id.GetString()!.Length : id.GetRawText().Length) <= McpProtocol.MaximumRequestIdCharacters;
}
