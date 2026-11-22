using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class NamedPipeDispatcherConcurrencyTests
{
    // An OS pipe fixture exercises the shared dispatcher, not the installed host,
    // service, queue, or filesystem. Its handler never executes a PAO operation.
    internal static async Task RunAsync()
    {
        var owner = LocalIdentity.Current();
        var pipeName = "pao-concurrent-" + Guid.NewGuid().ToString("N");
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var callerStop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = AuthenticatedPipeHost.RunOperationsAsync(
            pipeName, owner.Sid, async (request, peer, cancellationToken) =>
            {
                Check(peer.SameIdentity(owner), "Pipe fixture received a foreign identity.");
                if (request.Rid == "blocked")
                {
                    entered.TrySetResult();
                    try { await release.Task.WaitAsync(cancellationToken); }
                    finally { exited.TrySetResult(); }
                }
                return new OperationReceipt(true, "fixture-" + request.Rid, request.Rid!, "done", request.Op);
            }, serverStop.Token);
        await using var dispatcher = new NamedPipeDispatcher(pipeName, expectedServer: owner);
        Task<OperationReceipt>? blocked = null;
        try
        {
            blocked = dispatcher.DispatchAsync(Request("blocked"), callerStop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shortReceipt = await dispatcher.DispatchAsync(Request("short"))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Check(shortReceipt.Ok && shortReceipt.Rid == "short" && !blocked.IsCompleted,
                "An unrelated short call waited for the shared client's blocked call.");

            callerStop.Cancel();
            try
            {
                await blocked;
                throw new InvalidOperationException("Canceled pipe exchange unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (callerStop.IsCancellationRequested) { }
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var afterCancel = await dispatcher.DispatchAsync(Request("after-cancel"))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Check(afterCancel.Ok && afterCancel.Rid == "after-cancel",
                "Canceling one exchange retired or corrupted the shared dispatcher.");
        }
        finally
        {
            release.TrySetResult();
            callerStop.Cancel();
            if (blocked is not null)
                try { await blocked; } catch (OperationCanceledException) { }
            serverStop.Cancel();
            try { await host; } catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
        await SynchronousPrefixesDoNotBlockAcceptAsync(owner);
        await CapacityCancellationDrainsAsync(owner);
        await WrongPeerBeforePayloadAsync(owner);
    }


    private static async Task SynchronousPrefixesDoNotBlockAcceptAsync(LocalPeer owner)
    {
        var pipeName = "pao-sync-prefix-" + Guid.NewGuid().ToString("N");
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var captureRelease = new ManualResetEventSlim();
        using var handlerRelease = new ManualResetEventSlim();
        var captures = 0;
        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = AuthenticatedPipeHost.RunOperationsAsync(
            pipeName, owner.Sid, (request, peer, cancellationToken) =>
            {
                Check(peer.SameIdentity(owner), "Synchronous fixture received a foreign identity.");
                if (request.Rid == "sync-blocked")
                {
                    handlerEntered.TrySetResult();
                    // This separately exercises a synchronous handler after
                    // its real request read; it does not assume a buffered read.
                    try { handlerRelease.Wait(cancellationToken); }
                    finally { exited.TrySetResult(); }
                }
                return Task.FromResult(new OperationReceipt(true, "fixture-" + request.Rid, request.Rid!, "done", request.Op));
            }, serverStop.Token, beforeCapture: () =>
            {
                if (Interlocked.Increment(ref captures) != 1) return;
                captureEntered.TrySetResult();
                captureRelease.Wait(serverStop.Token);
            });
        await using var dispatcher = new NamedPipeDispatcher(pipeName, expectedServer: owner);
        Task<OperationReceipt>? blocked = null;
        try
        {
            blocked = dispatcher.DispatchAsync(Request("sync-blocked"));
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var captureShort = await dispatcher.DispatchAsync(Request("capture-short"))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Check(captureShort.Ok && captureShort.Rid == "capture-short" && !blocked.IsCompleted,
                "A synchronous capture prefix prevented replenishing the pipe listener.");

            captureRelease.Set();
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var handlerShort = await dispatcher.DispatchAsync(Request("handler-short"))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Check(handlerShort.Ok && handlerShort.Rid == "handler-short" && !blocked.IsCompleted,
                "An unrelated exchange waited for the synchronous post-read handler.");
            handlerRelease.Set();
            var blockedReceipt = await blocked.WaitAsync(TimeSpan.FromSeconds(3));
            Check(blockedReceipt.Ok && blockedReceipt.Rid == "sync-blocked",
                "The synchronous owner did not complete its original exchange.");
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            captureRelease.Set();
            handlerRelease.Set();
            serverStop.Cancel();
            if (blocked is not null)
                try { await blocked.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            try { await host.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
    }

    private static async Task CapacityCancellationDrainsAsync(LocalPeer owner)
    {
        const int capacity = 32;
        var pipeName = "pao-capacity-drain-" + Guid.NewGuid().ToString("N");
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var finished = 0;
        var handled = 0;
        var host = AuthenticatedPipeHost.RunOperationsAsync(
            pipeName, owner.Sid, async (request, peer, cancellationToken) =>
            {
                Check(peer.SameIdentity(owner), "Capacity fixture received a foreign identity.");
                Interlocked.Increment(ref handled);
                var current = Interlocked.Increment(ref active);
                for (var observed = Volatile.Read(ref maximum); current > observed;)
                {
                    var previous = Interlocked.CompareExchange(ref maximum, current, observed);
                    if (previous == observed) break;
                    observed = previous;
                }
                if (current == capacity) entered.TrySetResult();
                // Cancellation must not let RunAsync abandon a transferred
                // owner that has not finished its own bounded cleanup yet.
                try { await release.Task; }
                finally
                {
                    Interlocked.Decrement(ref active);
                    Interlocked.Increment(ref finished);
                }
                return new OperationReceipt(true, "fixture-" + request.Rid, request.Rid!, "done", request.Op);
            }, serverStop.Token);
        await using var dispatcher = new NamedPipeDispatcher(pipeName, TimeSpan.FromSeconds(5), owner);
        var calls = new List<Task<OperationReceipt>>();
        try
        {
            for (var index = 0; index < capacity; index++)
                calls.Add(dispatcher.DispatchAsync(Request("capacity-" + index)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Volatile.Read(ref active) == capacity && Volatile.Read(ref maximum) == capacity,
                "The pipe fixture did not retain exactly its bounded owners.");

            await using (var overflow = new NamedPipeDispatcher(pipeName, TimeSpan.FromMilliseconds(100), owner))
            {
                var refused = await overflow.DispatchAsync(Request("capacity-overflow"))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                Check(!refused.Ok && refused.Err == "svc_down" && !refused.Partial,
                    "A capacity-refused exchange was submitted or reported an invented outcome.");
            }
            Check(Volatile.Read(ref handled) == capacity && Volatile.Read(ref maximum) == capacity,
                "The pipe host exceeded its connection bound.");

            serverStop.Cancel();
            try
            {
                await host.WaitAsync(TimeSpan.FromMilliseconds(200));
                throw new InvalidOperationException("Pipe shutdown returned before joining its retained owners.");
            }
            catch (TimeoutException) { }
            Check(Volatile.Read(ref finished) == 0 && Volatile.Read(ref active) == capacity,
                "Capacity-wait cancellation discarded a retained owner.");

            release.TrySetResult();
            await host.WaitAsync(TimeSpan.FromSeconds(5));
            var receipts = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < receipts.Length; index++)
            {
                var receipt = receipts[index];
                Check(receipt.Rid == "capacity-" + index &&
                    (receipt.Ok || receipt.Err == "recovery_required" && receipt.Partial),
                    "An accepted exchange lost its identity or replayed after a lost shutdown reply.");
            }
            Check(Volatile.Read(ref active) == 0 && Volatile.Read(ref finished) == capacity &&
                Volatile.Read(ref handled) == capacity,
                "Pipe shutdown leaked an owner or handled an accepted request twice.");
        }
        finally
        {
            release.TrySetResult();
            serverStop.Cancel();
            try { await host.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
            if (calls.Count != 0) await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WrongPeerBeforePayloadAsync(LocalPeer owner)
    {
        var pipeName = "pao-wrong-peer-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = LocalPipeFactory.Create(pipeName, owner.Sid);
        var inspect = InspectAsync();
        await using var dispatcher = new NamedPipeDispatcher(pipeName,
            expectedServer: owner with { Session = owner.Session + 1 });
        var receipt = await dispatcher.DispatchAsync(Request("wrong-peer"), deadline.Token);
        Check(!receipt.Ok && receipt.Err == "auth", "Wrong server identity was accepted.");
        Check(await inspect == 0, "Request bytes reached a server with the wrong identity.");

        async Task<int> InspectAsync()
        {
            await server.WaitForConnectionAsync(deadline.Token);
            return await server.ReadAsync(new byte[1], deadline.Token);
        }
    }

    private static OperationRequest Request(string rid) =>
        new("diag", new Dictionary<string, string>(), rid);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
