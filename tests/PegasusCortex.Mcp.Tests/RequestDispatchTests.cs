using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pegasus.Cortex.Mcp.Protocol;

internal static class RequestDispatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task FairnessAndBoundsAsync()
    {
        var dispatcher = new BoundedRequestDispatcher(1);
        using var stop = new CancellationTokenSource();
        var started = Signal(); var release = Signal();
        var order = new List<string>();
        dispatcher.TrySubmit("flood", 10, async _ =>
        { started.SetResult(); await release.Task; lock (order) order.Add("first"); return new(); }, stop.Token, Limit, out var first);
        await started.Task.WaitAsync(Limit);
        var tickets = new List<DispatchTicket> { first };
        for (var n = 0; n < 3; n++)
        {
            var name = "a" + n;
            Check(dispatcher.TrySubmit("flood", 10, _ =>
            { lock (order) order.Add(name); return ValueTask.FromResult(new JsonObject()); }, stop.Token, Limit, out var ticket), "FIFO admission failed.");
            tickets.Add(ticket);
        }
        Check(dispatcher.TrySubmit("other", 10, _ =>
        { lock (order) order.Add("b"); return ValueTask.FromResult(new JsonObject()); }, stop.Token, Limit, out var other), "Other session admission failed.");
        tickets.Add(other);
        release.SetResult();
        await Task.WhenAll(tickets.Select(ticket => ticket.Completion)).WaitAsync(Limit);
        Check(order.SequenceEqual(new[] { "first", "b", "a0", "a1", "a2" }), "Round-robin progress or session FIFO failed.");
        await EmptyAsync(dispatcher);

        // Payload and count admission are separate bounds, not timing stress tests.
        foreach (var byteBound in new[] { true, false })
        {
            var bounded = new BoundedRequestDispatcher(1);
            var held = Signal(); var ready = Signal();
            using var cancel = new CancellationTokenSource();
            try
            {
                var bytes = byteBound ? McpProtocol.MaximumRequestBytes : 1;
                var count = byteBound ? BoundedRequestDispatcher.MaximumPayloadBytes / bytes : BoundedRequestDispatcher.MaximumRequests;
                for (var n = 0; n < count; n++)
                {
                    Check(bounded.TrySubmit("session" + n, bytes, async _ =>
                    { ready.TrySetResult(); await held.Task; return new(); }, cancel.Token, Limit, out _), "Capacity was rejected below the bound.");
                }
                await ready.Task.WaitAsync(Limit);
                Check(!bounded.TrySubmit("overflow", 1, _ => ValueTask.FromResult(new JsonObject()), cancel.Token, Limit, out var rejected), "Global bound was exceeded.");
                Check((await rejected.Completion).State == DispatchState.CapacityExceeded, "Overflow did not report NotDispatched capacity.");
                var snapshot = bounded.Snapshot;
                Check(snapshot.Running == 1 && snapshot.Count <= BoundedRequestDispatcher.MaximumRequests &&
                    snapshot.Bytes <= BoundedRequestDispatcher.MaximumPayloadBytes, "Reported dispatcher bounds are false.");
            }
            finally { cancel.Cancel(); held.TrySetResult(); await EmptyAsync(bounded); }
        }
        var perSession = new BoundedRequestDispatcher(1);
        using var cancelSession = new CancellationTokenSource();
        var perSessionRelease = Signal();
        try
        {
            for (var n = 0; n < BoundedRequestDispatcher.MaximumSessionRequests; n++)
                Check(perSession.TrySubmit("same", 1, async _ => { await perSessionRelease.Task; return new(); }, cancelSession.Token, Limit, out _), "Session bound rejected early.");
            Check(!perSession.TrySubmit("same", 1, _ => ValueTask.FromResult(new JsonObject()), cancelSession.Token, Limit, out _), "Session admission exceeded32.");
        }
        finally { cancelSession.Cancel(); perSessionRelease.TrySetResult(); await EmptyAsync(perSession); }
    }

    internal static async Task CancellationAndUncertaintyAsync()
    {
        var dispatcher = new BoundedRequestDispatcher(1);
        var started = Signal(); var release = Signal(); var replacementStarted = Signal();
        using var runningCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var calls = 0;
        dispatcher.TrySubmit("held", 10, async _ =>
        { Interlocked.Increment(ref calls); started.SetResult(); await release.Task; return new() { ["committed"] = true }; },
            runningCancellation.Token, Limit, out var running);
        await started.Task.WaitAsync(Limit);
        dispatcher.TrySubmit("held", 10, _ =>
        { Interlocked.Increment(ref calls); return ValueTask.FromResult(new JsonObject()); }, queuedCancellation.Token, Limit, out var queued);
        queuedCancellation.Cancel();
        Check((await queued.Completion.WaitAsync(Limit)).State == DispatchState.NotDispatched && calls == 1, "Queued cancellation executed its callback.");
        runningCancellation.Cancel();
        Check((await running.Completion.WaitAsync(Limit)).State == DispatchState.Unknown, "Started cancellation claimed rollback.");
        dispatcher.TrySubmit("next", 10, _ =>
        { replacementStarted.SetResult(); return ValueTask.FromResult(new JsonObject()); }, CancellationToken.None, Limit, out var replacement);
        Check(dispatcher.Snapshot.Running == 1 && !replacementStarted.Task.IsCompleted, "Noncooperative work freed its worker before settling.");
        release.SetResult();
        await replacement.Completion.WaitAsync(Limit);
        await EmptyAsync(dispatcher);
        Check((await running.Completion).State == DispatchState.Unknown && calls == 1, "Late completion replaced Unknown or replayed work.");

        var deadlineRelease = Signal(); var deadlineStarted = Signal();
        dispatcher.TrySubmit("deadline", 10, async _ =>
        { deadlineStarted.SetResult(); await deadlineRelease.Task; return new(); }, CancellationToken.None, TimeSpan.FromMilliseconds(100), out var deadline);
        try
        {
            await deadlineStarted.Task.WaitAsync(Limit);
            Check((await deadline.Completion.WaitAsync(Limit)).State == DispatchState.Unknown && dispatcher.Snapshot.Running == 1,
                "Request deadline falsely freed a running operation.");
        }
        finally { deadlineRelease.TrySetResult(); await EmptyAsync(dispatcher); }
    }

    internal static async Task CorrelationAndDrainAsync()
    {
        var dispatcher = new BoundedRequestDispatcher(2);
        var held = Signal(); var started = Signal(); var writesA = new List<JsonObject>(); var writesB = new List<JsonObject>();
        using var a = new CorrelatedRequestSession(dispatcher, "a", (body, _) =>
        { writesA.Add(body); return ValueTask.CompletedTask; }, Error, CancellationToken.None);
        using var b = new CorrelatedRequestSession(dispatcher, "b", (body, _) =>
        { writesB.Add(body); return ValueTask.CompletedTask; }, Error, CancellationToken.None);
        a.Submit("s:1", JsonValue.Create("1")!, 10, async _ => { started.SetResult(); await held.Task; return new() { ["session"] = "a" }; });
        await started.Task.WaitAsync(Limit);
        b.Submit("s:1", JsonValue.Create("1")!, 10, _ => ValueTask.FromResult(new JsonObject { ["session"] = "b" }));
        await b.DrainAsync(Limit);
        Check(writesA.Count == 0 && writesB.Single()["session"]!.GetValue<string>() == "b", "Cross-session ID reused the wrong response or blocked independent progress.");
        var duplicates = 0;
        try { a.Submit("s:1", JsonValue.Create("1")!, 10, _ => { duplicates++; return ValueTask.FromResult(new JsonObject()); });
            throw new InvalidOperationException("Duplicate ID accepted."); }
        catch (InvalidDataException) { }
        Check(duplicates == 0, "Duplicate live ID executed.");
        try { await a.DrainAsync(TimeSpan.FromMilliseconds(100)); throw new InvalidOperationException("Blocked EOF drain completed."); }
        catch (IOException error) { Check(error.Message.Contains("unknown", StringComparison.Ordinal), "Drain expiry concealed uncertainty."); }
        Check(dispatcher.Snapshot.Running == 1, "EOF drained by forgetting active work.");
        held.SetResult(); await EmptyAsync(dispatcher);
        Check(writesA.Count == 0, "Late result wrote to a closed session.");
    }

    internal static async Task McpTargetedCancellationAsync()
    {
        var app = new ControlledApplication();
        using var input = new ChannelReader();
        var output = new ChannelWriter();
        var dispatcher = new BoundedRequestDispatcher(2);
        var server = new McpStdioServer(app, input, output, TextWriter.Null, dispatcher, "targeted");
        using var deadline = new CancellationTokenSource(Limit);
        var run = server.RunAsync(deadline.Token);
        try
        {
            input.Send(Initialize()); input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            input.Send(Call("\"1\""));
            await app.Started.Task.WaitAsync(Limit);
            input.Send(Call("1"));
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1}}");
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"1\"}}");
            var initialize = await output.ReadAsync();
            Check(initialize["result"] is not null, "Initialization did not precede data replies.");
            var running = await output.ReadAsync(); var queued = await output.ReadAsync();
            Check(running["id"]!.GetValue<string>() == "1" && running["error"]!["data"]!["outcome"]!.GetValue<string>() == "Unknown", "Targeted running cancellation lost typed ID or uncertainty.");
            Check(queued["id"]!.GetValue<int>() == 1 && queued["error"]!["data"]!["outcome"]!.GetValue<string>() == "NotDispatched" && app.Calls == 1,
                "Targeted queued cancellation conflated string and numeric IDs.");
            input.Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
            input.End(); app.Release.TrySetResult();
            await run.WaitAsync(Limit);
            Check((await output.ReadAsync())["id"]!.GetValue<int>() == 3 && !output.HasResponse, "Late operation produced a second response.");
            await EmptyAsync(dispatcher);
        }
        finally
        {
            app.Release.TrySetResult(); input.End(); deadline.Cancel();
            try { await run.WaitAsync(Limit); } catch (OperationCanceledException) { }
            await EmptyAsync(dispatcher);
        }
    }

    internal static async Task ReviewRegressionsAsync()
    {
        var dispatcher = new BoundedRequestDispatcher(1);
        var started = Signal(); var release = Signal();
        dispatcher.TrySubmit("running", 1, async _ => { started.TrySetResult(); await release.Task; return new(); }, CancellationToken.None, Limit, out _);
        await started.Task.WaitAsync(Limit);
        try
        {
            for (var n = 0; n < 256; n++)
            {
                using var stop = new CancellationTokenSource();
                dispatcher.TrySubmit("canceled" + n, 1, _ => ValueTask.FromResult(new JsonObject()), stop.Token, Limit, out var ticket);
                stop.Cancel(); await ticket.Completion;
            }
            Check(dispatcher.ReadyCount == 0 && dispatcher.Snapshot.Count == 1, "Canceled requests leaked ready queue membership.");
            var writes = Signal();
            using var session = new CorrelatedRequestSession(dispatcher, "errors", (body, token) => new ValueTask(writes.Task.WaitAsync(token)), Error, CancellationToken.None);
            for (var n = 0; n < BoundedRequestDispatcher.MaximumSessionRequests; n++) session.Reply(null, null, new());
            try { session.Reply(null, null, new()); throw new InvalidOperationException("Uncorrelated errors bypassed reply bounds."); }
            catch (InvalidDataException) { }
            writes.TrySetResult(); await session.DrainAsync(Limit);
        }
        finally { release.TrySetResult(); await dispatcher.WhenIdleAsync().WaitAsync(Limit); }

        var fair = new BoundedRequestDispatcher(1); var held = Signal(); var first = Signal(); var order = new List<string>();
        fair.TrySubmit("flood", 1, async _ => { first.TrySetResult(); await held.Task; return new(); }, CancellationToken.None, Limit, out var firstTicket, "held");
        await first.Task.WaitAsync(Limit);
        var tickets = new List<DispatchTicket> { firstTicket };
        for (var n = 0; n < 8; n++)
        {
            fair.TrySubmit("flood", 1, _ => { order.Add("flood"); return ValueTask.FromResult(new JsonObject()); }, CancellationToken.None, Limit, out var ticket, "stream" + n);
            tickets.Add(ticket);
        }
        fair.TrySubmit("other", 1, _ => { order.Add("other"); return ValueTask.FromResult(new JsonObject()); }, CancellationToken.None, Limit, out var other);
        tickets.Add(other); held.TrySetResult(); await Task.WhenAll(tickets.Select(ticket => ticket.Completion)).WaitAsync(Limit);
        Check(order.IndexOf("other") <= 1, "Many streams purchased unfair priority over another connection.");
        await fair.WhenIdleAsync().WaitAsync(Limit);
    }

    internal static async Task McpNegotiatedMultiplexAsync()
    {
        var app = new ControlledApplication(); using var input = new ChannelReader(); var output = new ChannelWriter();
        var dispatcher = new BoundedRequestDispatcher(2);
        var server = new McpStdioServer(app, input, output, TextWriter.Null, dispatcher, "mux");
        using var deadline = new CancellationTokenSource(Limit); var run = server.RunAsync(deadline.Token);
        try
        {
            input.Send(Initialize().Replace("\"capabilities\":{}", "\"capabilities\":{\"experimental\":{\"pegasus.cortex/multiplex\":true}}", StringComparison.Ordinal));
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            input.Send(Call("1")); await app.Started.Task.WaitAsync(Limit);
            input.Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"fast\",\"arguments\":{}}}");
            Check((await output.ReadAsync())["result"]!["capabilities"]!["experimental"]!["pegasus.cortex/multiplex"] is JsonObject, "Multiplex capability was not advertised as an object.");
            Check((await output.ReadAsync())["id"]!.GetValue<int>() == 2 && !app.Release.Task.IsCompleted, "Opt-in MCP did not finish an independent request out of order.");
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1}}");
            Check((await output.ReadAsync())["error"]!["data"]!["outcome"]!.GetValue<string>() == "Unknown", "Opt-in MCP cancellation concealed an executing request.");
            input.End(); app.Release.TrySetResult(); await run.WaitAsync(Limit); await dispatcher.WhenIdleAsync().WaitAsync(Limit);
            Check(!output.HasResponse, "Opt-in MCP wrote a duplicate late result.");
        }
        finally { input.End(); app.Release.TrySetResult(); deadline.Cancel(); try { await run.WaitAsync(Limit); } catch (OperationCanceledException) { } }
    }

    internal static async Task MalformedDuplicateIdAsync()
    {
        var app = new ControlledApplication(); using var input = new ChannelReader(); var output = new ChannelWriter();
        var dispatcher = new BoundedRequestDispatcher(1);
        var server = new McpStdioServer(app, input, output, TextWriter.Null, dispatcher, "duplicate");
        using var deadline = new CancellationTokenSource(Limit); var run = server.RunAsync(deadline.Token);
        try
        {
            input.Send(Initialize()); input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            input.Send(Call("1")); await app.Started.Task.WaitAsync(Limit); _ = await output.ReadAsync();
            input.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":17}");
            try { await run.WaitAsync(Limit); throw new InvalidOperationException("Malformed duplicate ID accepted."); }
            catch (InvalidDataException) { }
            Check(app.Calls == 1 && !output.HasResponse, "Malformed duplicate emitted an ambiguous response or executed.");
        }
        finally
        {
            input.End(); app.Release.TrySetResult(); deadline.Cancel();
            try { await run.WaitAsync(Limit); } catch (Exception error) when (error is OperationCanceledException or InvalidDataException) { }
            await dispatcher.WhenIdleAsync().WaitAsync(Limit);
        }
    }

    internal static async Task CancellationCleanupAndWriteTrackingAsync()
    {
        // Finish versus parent cancellation exercises linked registration cleanup.
        for (var n = 0; n < 64; n++)
        {
            var dispatcher = new BoundedRequestDispatcher(1); using var parent = new CancellationTokenSource();
            var started = Signal(); var release = Signal();
            dispatcher.TrySubmit("race", 1, async _ => { started.TrySetResult(); await release.Task; return new(); }, parent.Token, Limit, out var ticket);
            await started.Task.WaitAsync(Limit);
            var cancel = Task.Run(parent.Cancel); release.TrySetResult();
            await Task.WhenAll(cancel, ticket.Completion, dispatcher.WhenIdleAsync()).WaitAsync(Limit);
            Check(dispatcher.Snapshot.Count == 0, "Cancellation/completion leaked a callback or deadlocked cleanup.");
        }
        var multiplex = new BoundedRequestDispatcher(2); var held = Signal(); var heldStarted = Signal();
        TaskCompletionSource? written = null;
        using var session = new CorrelatedRequestSession(multiplex, "tracked", (body, _) =>
        { if (body["fast"] is not null) written!.TrySetResult(); return ValueTask.CompletedTask; }, Error, CancellationToken.None);
        session.EnableMultiplexing();
        session.Submit("held", JsonValue.Create("held")!, 1, async _ => { heldStarted.TrySetResult(); await held.Task; return new(); });
        await heldStarted.Task.WaitAsync(Limit);
        try
        {
            for (var n = 0; n < 256; n++)
            {
                written = Signal();
                session.Submit("fast" + n, JsonValue.Create(n)!, 1, _ => ValueTask.FromResult(new JsonObject { ["fast"] = true }));
                await written.Task.WaitAsync(Limit);
                Check(session.OutstandingWrites <= 2, "Completed multiplex replies accumulated behind a held stream.");
            }
        }
        finally { held.TrySetResult(); await session.DrainAsync(Limit); await multiplex.WhenIdleAsync().WaitAsync(Limit); }
        Check(session.OutstandingWrites == 0, "Settled writes remained retained.");
    }

    internal static async Task UnpreparedSuccessorAsync()
    {
        var admitted = 0; var prepareEntered = Signal(); var prepareRelease = Signal();
        var dispatcher = new BoundedRequestDispatcher(1, () =>
        {
            if (Interlocked.Increment(ref admitted) == 3)
            { prepareEntered.TrySetResult(); prepareRelease.Task.GetAwaiter().GetResult(); }
        });
        var runningStarted = Signal(); var runningRelease = Signal(); var successorStarted = Signal();
        using var headCancellation = new CancellationTokenSource();
        dispatcher.TrySubmit("busy", 1, async _ => { runningStarted.TrySetResult(); await runningRelease.Task; return new(); }, CancellationToken.None, Limit, out var running);
        await runningStarted.Task.WaitAsync(Limit);
        dispatcher.TrySubmit("queued", 1, _ => ValueTask.FromResult(new JsonObject()), headCancellation.Token, Limit, out var head);
        var admission = Task.Run(() =>
        {
            dispatcher.TrySubmit("queued", 1, _ => { successorStarted.TrySetResult(); return ValueTask.FromResult(new JsonObject()); }, CancellationToken.None, Limit, out var successor);
            return successor;
        });
        try
        {
            await prepareEntered.Task.WaitAsync(Limit);
            headCancellation.Cancel();
            Check((await head.Completion).State == DispatchState.NotDispatched, "Queued head was executed.");
            runningRelease.TrySetResult(); await running.Completion.WaitAsync(Limit);
            using var timeout = new CancellationTokenSource(Limit);
            while (dispatcher.Snapshot.Running != 0) await Task.Delay(5, timeout.Token);
            Check(!successorStarted.Task.IsCompleted, "Unprepared successor dispatched before installing its lifetime registration.");
            prepareRelease.TrySetResult(); var successor = await admission.WaitAsync(Limit);
            Check((await successor.Completion.WaitAsync(Limit)).State == DispatchState.Completed, "Prepared successor did not resume.");
        }
        finally
        {
            runningRelease.TrySetResult(); prepareRelease.TrySetResult();
            await admission.WaitAsync(Limit); await dispatcher.WhenIdleAsync().WaitAsync(Limit);
        }
    }

    private static JsonObject Error(DispatchState state, JsonNode? id) => new() { ["id"] = id?.DeepClone(), ["state"] = state.ToString() };
    private static string Initialize() => "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}";
    private static string Call(string id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"hold\",\"arguments\":{}}}";
    private static async Task EmptyAsync(BoundedRequestDispatcher dispatcher)
    {
        using var stop = new CancellationTokenSource(Limit);
        while (dispatcher.Snapshot.Count != 0) await Task.Delay(5, stop.Token);
    }
    private sealed class ControlledApplication : ICortexMcpProtocolApplication
    {
        internal readonly TaskCompletionSource Started = Signal(), Release = Signal();
        internal int Calls;
        public JsonArray ListTools() => new();
        public async ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (name == "fast") return new() { ["content"] = new JsonArray() };
            Started.TrySetResult();
            // Deliberately ignores cancellation to model an effect whose reply is lost.
            await Release.Task;
            return new() { ["content"] = new JsonArray() };
        }
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken token) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken token) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken token) => ValueTask.FromResult<JsonObject?>(null);
    }
    private sealed class ChannelReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private string _current = ""; private int _offset;
        internal void Send(string line) => _lines.Writer.TryWrite(line + "\n");
        internal void End() => _lines.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length)
            {
                try { _current = await _lines.Reader.ReadAsync(cancellationToken); _offset = 0; }
                catch (ChannelClosedException) { return 0; }
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
    }
    private sealed class ChannelWriter : TextWriter
    {
        private readonly Channel<JsonObject> _responses = Channel.CreateUnbounded<JsonObject>();
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        internal bool HasResponse => _responses.Reader.TryPeek(out _);
        internal Task<JsonObject> ReadAsync() => _responses.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override Task WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            foreach (var character in value.Span)
                if (character == '\n') { _responses.Writer.TryWrite(JsonNode.Parse(_line.ToString())!.AsObject()); _line.Clear(); }
                else _line.Append(character);
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
