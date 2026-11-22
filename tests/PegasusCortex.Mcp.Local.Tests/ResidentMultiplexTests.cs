using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;

internal static partial class ResidentTransportTests
{
    public static async Task SinglePipeMultiplexingAsync()
    {
        var directory = Temporary();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var owner = LocalIdentity.Current();
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                Path.Combine(directory, "cortex-v2.db"), TimeProvider.System, stop.Token, new Profile());
            var application = new MultiplexApplication(composition.Application);
            var registry = new CortexClientRegistry(Path.Combine(directory, "clients"), owner);
            var key = registry.Register("fixture", owner.ApplicationPath!, "native", new(["alpha"], ["alpha"]), DateTimeOffset.UtcNow.AddDays(1));
            var server = new CortexResidentServer(application, registry, owner, "2.2026.10.08.05.26.A", TextWriter.Null);
            var name = "cortex-native-mux-" + Guid.NewGuid().ToString("N");
            var running = server.RunNativeAsync(name, stop.Token);
            try
            {
                await using var pipe = await CortexMcpPipe.ConnectAsync(name, owner, owner.ApplicationPath!, stop.Token);
                var hello = await Hello(pipe, key, CortexResidentServer.NativeMultiplexContract, stop.Token);
                LocalAssert.True(CortexResidentRuntime.ValidHello(hello, CortexResidentServer.NativeMultiplexContract, owner, "fixture") &&
                    hello.GetProperty("maximumConnections").GetInt32() == 24 &&
                    hello.GetProperty("multiplexing").GetProperty("streamIdRequired").GetBoolean(), "Explicit native/2 negotiation or preserved24 cap failed.");
                await SendAsync("""{"id":"commit","streamId":"write","op":"call","name":"commit","arguments":{"namespace":"alpha","action":"create","taskId":"uncertain-task","title":"Scratch uncertain write","content":"committed before withheld reply","idempotencyKey":"scratch-write-once"}}""");
                await application.Committed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await SendAsync("""{"id":"queued","streamId":"write","op":"call","name":"queued","arguments":{}}""");
                await SendAsync("""{"id":"fast1","streamId":"independent1","op":"call","name":"fast1","arguments":{}}""");
                await SendAsync("""{"id":"fast2","streamId":"independent2","op":"call","name":"fast2","arguments":{}}""");
                var replies = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                for (var n = 0; n < 2; n++)
                {
                    var response = await BinaryProtocol.ReadAsync<JsonObject>(pipe, stop.Token, CortexResidentServer.ResponseBytes);
                    var id = response["id"]!.GetValue<string>();
                    LocalAssert.True(id is "fast1" or "fast2" && response["result"]!["name"]!.GetValue<string>() == id &&
                        response["result"]!["text"]!.GetValue<string>().Length == 8192, "Concurrent replies crossed IDs or interleaved frame bytes.");
                    replies.Add(id, response);
                }
                LocalAssert.True(application.MaximumActive >= 3 && !application.Release.Task.IsCompleted,
                    "ONE pipe did not execute overlapping independent operations or finish out of order.");
                await SendAsync("""{"id":"cancel-queued","op":"cancel","targetId":"queued"}""");
                await SendAsync("""{"id":"cancel-running","op":"cancel","targetId":"commit"}""");
                for (var n = 0; n < 4; n++)
                {
                    var response = await BinaryProtocol.ReadAsync<JsonObject>(pipe, stop.Token, CortexResidentServer.ResponseBytes);
                    replies.Add(response["id"]!.GetValue<string>(), response);
                }
                LocalAssert.True(replies["queued"]["error"]!["outcome"]!.GetValue<string>() == "NotDispatched" && application.QueuedCalls == 0,
                    "Same-stream FIFO or queued cancellation executed the queued mutation.");
                LocalAssert.True(replies["commit"]["error"]!["outcome"]!.GetValue<string>() == "Unknown" && application.CommitCalls == 1,
                    "Started write was replayed or reported as rolled back.");
                var read = await composition.Application.CallToolAsync("cortex_task", JsonSerializer.SerializeToElement(new
                    { @namespace = "alpha", action = "get", taskId = "uncertain-task" }), stop.Token);
                LocalAssert.True(read["structuredContent"]!["task"]!["revision"]!.GetValue<int>() == 1,
                    "Actual scratch SQLite commit was not retained before the withheld reply.");
                // Transport closes promptly, but resident ownership may not dispose
                // the store under a callback which deliberately ignores cancellation.
                stop.Cancel(); await Task.Delay(100);
                LocalAssert.True(!running.IsCompleted, "Resident returned while a callback still owned the store.");
                application.Release.TrySetResult();
                try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                LocalAssert.True(application.ReadAfterStop && application.CommitCalls == 1,
                    "Shared dependency lifetime ended early or the write was replayed.");
                async Task SendAsync(string json) => await BinaryProtocol.WriteAsync(pipe,
                    JsonSerializer.Deserialize<JsonElement>(json), stop.Token, CortexResidentServer.RequestBytes);
            }
            finally
            {
                application.Release.TrySetResult(); stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            }
        }
        finally { Cleanup(directory); }
    }

    private sealed class MultiplexApplication(ICortexMcpProtocolApplication inner) : ICortexMcpProtocolApplication
    {
        internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fastOverlap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active, _fast;
        internal int MaximumActive, CommitCalls, QueuedCalls;
        internal bool ReadAfterStop;
        public JsonArray ListTools() => inner.ListTools();
        public async ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            int prior;
            do { prior = Volatile.Read(ref MaximumActive); if (prior >= active) break; }
            while (Interlocked.CompareExchange(ref MaximumActive, active, prior) != prior);
            try
            {
                if (name == "commit")
                {
                    Interlocked.Increment(ref CommitCalls);
                    var result = await inner.CallToolAsync("cortex_task", arguments, token);
                    LocalAssert.True(result["structuredContent"]!["task"]!["revision"]!.GetValue<int>() == 1, "Scratch mutation did not commit.");
                    Committed.TrySetResult(); await Release.Task;
                    var read = await inner.CallToolAsync("cortex_task", JsonSerializer.SerializeToElement(new
                        { @namespace = "alpha", action = "get", taskId = "uncertain-task" }), CancellationToken.None);
                    ReadAfterStop = read["structuredContent"]!["task"]!["revision"]!.GetValue<int>() == 1;
                    return result;
                }
                if (name == "queued") Interlocked.Increment(ref QueuedCalls);
                if (name.StartsWith("fast", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref _fast) == 2) _fastOverlap.TrySetResult();
                    await _fastOverlap.Task;
                }
                return new() { ["structuredContent"] = new JsonObject { ["name"] = name, ["text"] = new string('漢', 8192) }, ["content"] = new JsonArray() };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken token) => inner.ListResourcesAsync(token);
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken token) => inner.ListResourceTemplatesAsync(token);
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken token) => inner.ReadResourceAsync(uri, token);
    }
}
