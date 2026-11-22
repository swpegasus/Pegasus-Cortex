using System.Collections.Concurrent;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class BoundedMutationSchedulerTests
{
    private static readonly TimeSpan FailureDeadline = TimeSpan.FromSeconds(10);
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pao-scheduler-contract"));

    internal static async Task RunAsync()
    {
        ReservationBoundaries();
        await DisjointOverlapAsync();
        await SamePathOrderAsync();
        await OrderedAncestorConflictAsync();
        await UnknownGlobalAsync();
        await AdmissionBoundsAsync();
        await CanceledQueuedCallbackAsync();
        await CallbackFailureAndRefillAsync();
        await DisposalAsync();
    }

    private static void ReservationBoundaries()
    {
        var parent = Fixed("A");
        Check(BoundedMutationScheduler.ReservationConflicts(parent, Fixed("a", "nested", "item")), "ancestor/case conflict");
        Check(!BoundedMutationScheduler.ReservationConflicts(parent, Fixed("AB")), "adjacent prefix is not a descendant");
        var slash = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(Root, "A").Replace('\\', '/') });
        Check(BoundedMutationScheduler.ReservationConflicts(parent, slash), "mixed separators conflict");
        var copy = new OperationRequest("cp", new Dictionary<string, string>
        {
            ["src"] = Path.Combine(Root, "source"), ["dst"] = Path.Combine(Root, "destination")
        });
        Check(BoundedMutationScheduler.ReservationConflicts(copy, Fixed("source", "child")), "copy source reservation");
        Check(BoundedMutationScheduler.ReservationConflicts(copy, Fixed("destination", "child")), "copy destination reservation");
        Check(BoundedMutationScheduler.ReservationConflicts(parent, Unknown()), "unknown effects are global");
        var relative = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = "relative" });
        Check(BoundedMutationScheduler.ReservationConflicts(parent, relative), "unfrozen path is conservative");
        foreach (var op in new[] { "run", "pwsh", "git", "dotnet", "do", "nsis", "ctx", "tool", "recipe", "feedback", "ui", "proc" })
            Check(BoundedMutationScheduler.ReservationConflicts(parent, new(op, new Dictionary<string, string>())), op + " remains conservative");
    }

    private static async Task DisjointOverlapAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync, maximumActive: 3);
        try
        {
            Check(scheduler.TryEnqueue("a", 1, Fixed("a"), CancellationToken.None), "admit a");
            Check(scheduler.TryEnqueue("b", 2, Fixed("b"), CancellationToken.None), "admit b");
            Equal(2, scheduler.ActiveCount, "disjoint reservations admitted concurrently");
            Equal(0, scheduler.PendingCount, "disjoint jobs are not queued behind one another");
            await gates.StartedAsync("a"); await gates.StartedAsync("b");
            gates.Release("a"); gates.Release("b");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);
    }

    private static async Task OrderedAncestorConflictAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync, maximumActive: 2);
        try
        {
            Check(scheduler.TryEnqueue("first", 1, Fixed("a", "first"), CancellationToken.None), "first");
            Check(scheduler.TryEnqueue("parent", 2, Fixed("a"), CancellationToken.None), "parent");
            Check(scheduler.TryEnqueue("later", 3, Fixed("a", "other"), CancellationToken.None), "later");
            // Later is disjoint from active first, but conflicts with older pending parent.
            Equal(1, scheduler.ActiveCount, "older pending conflict cannot be bypassed");
            Equal(2, scheduler.PendingCount, "parent and later stay pending");
            Check(scheduler.TryEnqueue("other", 4, Fixed("b"), CancellationToken.None), "other");
            Equal(2, scheduler.ActiveCount, "unrelated later job may bypass blocked conflicts");
            await gates.StartedAsync("first"); await gates.StartedAsync("other");
            gates.Release("first");
            await gates.StartedAsync("parent");
            Equal(1, scheduler.PendingCount, "later waits for parent");
            Check(!gates.HasStarted("later"), "later has not executed before parent releases");
            gates.Release("parent");
            await gates.StartedAsync("later");
            gates.Release("later"); gates.Release("other");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);
    }

    private static async Task SamePathOrderAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync);
        try
        {
            Check(scheduler.TryEnqueue("first", 1, Fixed("Same"), CancellationToken.None), "same first");
            Check(scheduler.TryEnqueue("second", 2, Fixed("same"), CancellationToken.None), "same second");
            Equal(1, scheduler.ActiveCount, "exact same path has only one active job");
            Equal(1, scheduler.PendingCount, "same path pending order");
            await gates.StartedAsync("first"); gates.Release("first");
            await gates.StartedAsync("second"); gates.Release("second");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);
    }

    private static async Task UnknownGlobalAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync);
        try
        {
            Check(scheduler.TryEnqueue("unknown", 1, Unknown(), CancellationToken.None), "unknown");
            Check(scheduler.TryEnqueue("a", 2, Fixed("a"), CancellationToken.None), "a");
            Check(scheduler.TryEnqueue("b", 3, Fixed("b"), CancellationToken.None), "b");
            Equal(1, scheduler.ActiveCount, "global effects hold exclusive reservation");
            Equal(2, scheduler.PendingCount, "known paths wait behind global effects");
            await gates.StartedAsync("unknown"); gates.Release("unknown");
            await gates.StartedAsync("a"); await gates.StartedAsync("b");
            gates.Release("a"); gates.Release("b");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);
    }

    private static async Task AdmissionBoundsAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync, maximumPending: 1, maximumActive: 1);
        try
        {
            Check(scheduler.TryEnqueue("active", 1, Fixed("a"), CancellationToken.None), "active capacity");
            Check(!scheduler.TryEnqueue("active", 4, Fixed("a"), CancellationToken.None), "duplicate identifier rejected");
            Check(scheduler.TryEnqueue("pending", 2, Fixed("b"), CancellationToken.None), "pending capacity");
            Check(!scheduler.CanAccept(Fixed("c")), "pending capacity is reported before durable claim");
            Check(!scheduler.TryEnqueue("overflow", 3, Fixed("c"), CancellationToken.None), "overflow rejected");
            Equal(1, scheduler.ActiveCount, "active count bound"); Equal(1, scheduler.PendingCount, "pending count bound");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);

        var payload = new BoundedMutationScheduler((_, _) => Task.CompletedTask, maximumPayloadBytes: 1024);
        try
        {
            var tooLarge = Fixed("large") with { Body = new string('x', 1025) };
            Check(!payload.CanAccept(tooLarge), "body contributes to resident payload budget");
            Check(!payload.TryEnqueue("large", 1, tooLarge, CancellationToken.None), "oversized resident payload rejected");
            Equal(0L, payload.RetainedPayloadBytes, "rejected body is not retained");
        }
        finally { await payload.DisposeAsync().WaitAsync(FailureDeadline); }
    }

    private static async Task CanceledQueuedCallbackAsync()
    {
        var gates = new Gates();
        using var queuedCancellation = new CancellationTokenSource();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync, maximumActive: 2);
        try
        {
            Check(scheduler.TryEnqueue("global", 1, Unknown(), CancellationToken.None), "global active");
            Check(scheduler.TryEnqueue("canceled", 2, Fixed("a"), queuedCancellation.Token), "pending canceled");
            Equal(1, scheduler.PendingCount, "job initially blocked");
            queuedCancellation.Cancel();
            await gates.StartedAsync("canceled");
            Check(gates.CanceledAtEntry("canceled"), "canceled queued job reaches callback with canceled token");
            Equal(1, gates.Calls("canceled"), "canceled accepted job callback exactly once");
            gates.Release("global");
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        AssertReleased(scheduler);
    }

    private static async Task CallbackFailureAndRefillAsync()
    {
        var refill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BoundedMutationScheduler? scheduler = null;
        var refilled = 0;
        scheduler = new BoundedMutationScheduler((jid, _) =>
        {
            if (jid == "fault") throw new InvalidOperationException("isolated callback failure");
            secondStarted.TrySetResult(); return Task.CompletedTask;
        }, maximumActive: 1, capacityAvailable: () =>
        {
            if (Interlocked.CompareExchange(ref refilled, 1, 0) != 0) return;
            Check(scheduler!.CanAccept(Fixed("refill")), "capacity released before recovery callback");
            Check(scheduler!.TryEnqueue("refill", 2, Fixed("refill"), CancellationToken.None), "refill callback can reenter scheduler");
            refill.TrySetResult();
        });
        try
        {
            Check(scheduler.TryEnqueue("fault", 1, Fixed("fault"), CancellationToken.None), "fault admitted");
            await refill.Task.WaitAsync(FailureDeadline); await secondStarted.Task.WaitAsync(FailureDeadline);
        }
        finally { await scheduler.DisposeAsync().WaitAsync(FailureDeadline); }
        Equal(1, scheduler.CallbackFaultCount, "fault contained without stopping later job");
        AssertReleased(scheduler);
    }

    private static async Task DisposalAsync()
    {
        var gates = new Gates();
        var scheduler = new BoundedMutationScheduler(gates.ExecuteAsync, maximumActive: 1);
        Check(scheduler.TryEnqueue("active", 1, Unknown(), CancellationToken.None), "active");
        Check(scheduler.TryEnqueue("pending-a", 2, Fixed("a"), CancellationToken.None), "pending a");
        Check(scheduler.TryEnqueue("pending-b", 3, Fixed("b"), CancellationToken.None), "pending b");
        await gates.StartedAsync("active");
        var disposing = scheduler.DisposeAsync();
        Check(ReferenceEquals(disposing, scheduler.DisposeAsync()), "repeated disposal returns one drain");
        Check(!scheduler.CanAccept(Fixed("new")), "disposal closes admission");
        Check(!scheduler.TryEnqueue("new", 4, Fixed("new"), CancellationToken.None), "no enqueue after disposal begins");
        await disposing.WaitAsync(FailureDeadline);
        Equal(1, gates.Calls("active"), "active callback once");
        Equal(1, gates.Calls("pending-a"), "pending callback drains once");
        Equal(1, gates.Calls("pending-b"), "second pending callback drains once");
        Check(gates.CanceledAtEntry("pending-a") && gates.CanceledAtEntry("pending-b"), "disposed pending work reaches canceled callback");
        AssertReleased(scheduler);
    }

    private static OperationRequest Fixed(params string[] parts) => new("mkdir", new Dictionary<string, string>
    {
        ["dst"] = parts.Aggregate(Root, Path.Combine)
    });
    private static OperationRequest Unknown() => new("pwsh", new Dictionary<string, string>
    {
        ["src"] = Path.Combine(Root, "unknown.ps1")
    });

    private static void AssertReleased(BoundedMutationScheduler scheduler)
    {
        Check(scheduler.IsIdle, "scheduler fully idle");
        Equal(0, scheduler.ActiveCount, "no active jobs retained");
        Equal(0, scheduler.PendingCount, "no pending jobs retained");
        Equal(0L, scheduler.RetainedPayloadBytes, "no payload bytes retained");
    }

    private sealed class Gates
    {
        private sealed class Gate
        {
            internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Calls;
            internal bool WasCanceled;
        }
        private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
        private Gate Get(string jid) => _gates.GetOrAdd(jid, _ => new Gate());
        internal async Task ExecuteAsync(string jid, CancellationToken token)
        {
            var gate = Get(jid);
            Interlocked.Increment(ref gate.Calls);
            gate.WasCanceled = token.IsCancellationRequested;
            gate.Started.TrySetResult();
            if (!token.IsCancellationRequested) await gate.Released.Task.WaitAsync(token);
        }
        internal Task StartedAsync(string jid) => Get(jid).Started.Task.WaitAsync(FailureDeadline);
        internal bool HasStarted(string jid) => Get(jid).Started.Task.IsCompleted;
        internal bool CanceledAtEntry(string jid) => Get(jid).WasCanceled;
        internal int Calls(string jid) => Volatile.Read(ref Get(jid).Calls);
        internal void Release(string jid) => Get(jid).Released.TrySetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("bounded_scheduler: " + message);
    }
    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}; expected={expected}; actual={actual}");
}
