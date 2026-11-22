using System.Text;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Bounded owner-user scheduling, not a process sandbox. Only fixed operations
/// have knowable path effects; arbitrary external tools retain a global reservation.
/// The caller owns durable admission, queued/running arbitration and terminal receipts.
/// </summary>
internal sealed class BoundedMutationScheduler
{
    private sealed class Entry(
        string jid, long sequence, Reservation reservation, long payloadBytes,
        CancellationTokenSource cancellation)
    {
        internal readonly string Jid = jid;
        internal readonly long Sequence = sequence;
        internal readonly Reservation Reservation = reservation;
        internal readonly long PayloadBytes = payloadBytes;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal CancellationTokenRegistration CancellationRegistration;
    }

    private sealed record Reservation(bool Global, string[] Paths);

    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task> _execute;
    private readonly Action? _capacityAvailable;
    private readonly List<Entry> _pending = [];
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _identifiers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private Task? _disposeTask;
    private long _retainedPayloadBytes;
    private int _callbackFaultCount;
    private int _capacityCallbacks;

    internal BoundedMutationScheduler(
        Func<string, CancellationToken, Task> execute,
        int maximumPending = 128,
        int maximumActive = 3,
        long maximumPayloadBytes = 16 * 1024 * 1024,
        Action? capacityAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (maximumPending < 1 || maximumActive < 1 || maximumPayloadBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPending));
        _execute = execute;
        _capacityAvailable = capacityAvailable;
        MaximumPending = maximumPending;
        MaximumActive = maximumActive;
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    internal int MaximumPending { get; }
    internal int MaximumActive { get; }
    internal long MaximumPayloadBytes { get; }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal int ActiveCount { get { lock (_gate) return _active.Count; } }
    internal long RetainedPayloadBytes { get { lock (_gate) return _retainedPayloadBytes; } }
    internal bool IsIdle { get { lock (_gate) return _pending.Count == 0 && _active.Count == 0 && _capacityCallbacks == 0; } }
    internal int CallbackFaultCount => Volatile.Read(ref _callbackFaultCount);

    internal bool CanAccept(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = EstimatePayloadBytes(request);
        lock (_gate) return CanAcceptLocked(bytes);
    }

    internal bool CanEverAccept(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EstimatePayloadBytes(request) <= MaximumPayloadBytes;
    }

    internal bool TryEnqueue(string jid, long sequence, OperationRequest request, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jid);
        ArgumentNullException.ThrowIfNull(request);
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        var reservation = GetReservation(request);
        var bytes = EstimatePayloadBytes(request);
        lock (_gate)
        {
            if (!CanAcceptLocked(bytes) || !_identifiers.Add(jid)) return false;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            var entry = new Entry(jid, sequence, reservation, bytes, cancellation);
            _pending.Add(entry);
            _pending.Sort(static (left, right) =>
            {
                var sequenceOrder = left.Sequence.CompareTo(right.Sequence);
                return sequenceOrder != 0 ? sequenceOrder : StringComparer.Ordinal.Compare(left.Jid, right.Jid);
            });
            _retainedPayloadBytes += bytes;
            // Registration may run synchronously for an already canceled token.
            // The entry is resident first; cleanup cannot pass this gate until
            // the registration itself has been stored.
            entry.CancellationRegistration = cancellation.Token.Register(() =>
            {
                lock (_gate) PumpLocked();
            });
            PumpLocked();
            return true;
        }
    }

    private bool CanAcceptLocked(long bytes) =>
        !_disposed && _pending.Count < MaximumPending &&
        bytes <= MaximumPayloadBytes - _retainedPayloadBytes;

    private void PumpLocked()
    {
        for (var index = 0; index < _pending.Count && _active.Count < MaximumActive;)
        {
            var candidate = _pending[index];
            // Cancellation does not silently drop durable accepted work: its
            // callback must still arbitrate/persist the queued cancellation.
            // Such a callback gets a canceled token and performs no operation.
            var canceled = candidate.Cancellation.IsCancellationRequested;
            if (!canceled && (_active.Values.Any(active => Conflicts(candidate.Reservation, active.Reservation)) ||
                _pending.Take(index).Any(older => !older.Cancellation.IsCancellationRequested &&
                    Conflicts(candidate.Reservation, older.Reservation))))
            {
                index++;
                continue;
            }
            _pending.RemoveAt(index);
            _active.Add(candidate.Jid, candidate);
            _ = Task.Run(() => ExecuteEntryAsync(candidate));
        }
        if (_disposed && _pending.Count == 0 && _active.Count == 0 && _capacityCallbacks == 0) _drained.TrySetResult();
    }

    private async Task ExecuteEntryAsync(Entry entry)
    {
        try { await _execute(entry.Jid, entry.Cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            // The engine callback is responsible for its failure receipt. A
            // callback defect must not fault the pump or orphan later entries.
            Interlocked.Increment(ref _callbackFaultCount);
        }
        finally
        {
            // Dispose registrations outside the gate: a concurrent cancellation
            // callback may itself be waiting for the scheduling gate.
            entry.CancellationRegistration.Dispose();
            entry.Cancellation.Dispose();
            bool notifyCapacity;
            lock (_gate)
            {
                _active.Remove(entry.Jid);
                _identifiers.Remove(entry.Jid);
                _retainedPayloadBytes -= entry.PayloadBytes;
                notifyCapacity = !_disposed && _capacityAvailable is not null;
                if (notifyCapacity) _capacityCallbacks++;
                PumpLocked();
            }
            // Recovery refill may acquire the engine admission gate and call
            // back into this scheduler. Never invoke it under our own gate.
            if (notifyCapacity)
            {
                try { _capacityAvailable!.Invoke(); }
                catch (Exception) { Interlocked.Increment(ref _callbackFaultCount); }
                finally { lock (_gate) { _capacityCallbacks--; PumpLocked(); } }
            }
        }
    }

    internal Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposed = true;
            // Cancellation can invoke registrations synchronously; do it outside
            // the gate, and keep repeated disposal bound to one draining task.
            return _disposeTask = Task.Run(async () =>
            {
                _stop.Cancel();
                lock (_gate) PumpLocked();
                await _drained.Task.ConfigureAwait(false);
                _stop.Dispose();
            });
        }
    }

    internal static bool ReservationConflicts(OperationRequest left, OperationRequest right) =>
        Conflicts(GetReservation(left), GetReservation(right));

    private static bool Conflicts(Reservation left, Reservation right) =>
        left.Global || right.Global || left.Paths.Any(first => right.Paths.Any(second =>
            ContainsPath(first, second) || ContainsPath(second, first)));

    private static bool ContainsPath(string parent, string candidate) =>
        StringComparer.OrdinalIgnoreCase.Equals(parent, candidate) ||
        candidate.StartsWith(parent.EndsWith('\\') ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase);

    private static Reservation GetReservation(OperationRequest request)
    {
        string[]? keys = request.Op switch
        {
            "mkdir" or "wr" or "edit" or "patch" or "rm" => ["dst"],
            "cp" or "mv" => ["src", "dst"],
            "encoding" when request.Arg.GetValueOrDefault("op") == "convert" => ["src", "dst"],
            "zip" when request.Arg.GetValueOrDefault("op") is "create" or "extract" => ["src", "dst"],
            "7z" when request.Arg.GetValueOrDefault("op") == "extract" => ["src", "dst"],
            _ => null
        };
        if (keys is null) return new Reservation(true, []);
        var paths = new List<string>(keys.Length);
        foreach (var key in keys)
        {
            if (!request.Arg.TryGetValue(key, out var supplied) || !Path.IsPathFullyQualified(supplied))
                return new Reservation(true, []);
            try
            {
                var canonical = Path.GetFullPath(supplied).Replace('/', '\\');
                // Preserve a volume/UNC root separator, but normalize ordinary
                // directory endings so containment is separator-aware.
                var root = Path.GetPathRoot(canonical);
                if (!StringComparer.OrdinalIgnoreCase.Equals(canonical, root)) canonical = canonical.TrimEnd('\\');
                paths.Add(canonical);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new Reservation(true, []);
            }
        }
        return new Reservation(false, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private long EstimatePayloadBytes(OperationRequest request)
    {
        // Retained UTF-8 field bytes plus a bounded per-entry allowance, not a
        // promise about managed heap/RSS. Body is deliberately not JSON-persisted
        // but still occupies resident memory and therefore counts here.
        var bytes = 512L;
        Add(request.Op); Add(request.Rid); Add(request.Ws); Add(request.Cwd); Add(request.Root); Add(request.Body);
        foreach (var pair in request.Arg) { Add(pair.Key); Add(pair.Value); }
        if (request.PathRoots is not null)
            foreach (var pair in request.PathRoots) { Add(pair.Key); Add(pair.Value); }
        return bytes;

        void Add(string? value)
        {
            if (value is null || bytes > MaximumPayloadBytes) return;
            bytes += Encoding.UTF8.GetByteCount(value);
        }
    }
}
