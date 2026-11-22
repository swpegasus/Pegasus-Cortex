using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Pipes;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.AgentOps.Protocol;

internal static class LifecycleAdmissionTests
{
    internal static async Task ClosedResidentIdentityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-closed-identity-" + Guid.NewGuid().ToString("N"));
        var owner = LocalIdentity.Current();
        var registry = new CortexClientRegistry(Path.Combine(root, "clients"), owner);
        var key = registry.Register("lifecycle", owner.ApplicationPath!, "native",
            new CortexClientDefaults(["alpha"], ["alpha"]), DateTimeOffset.UtcNow.AddDays(1));
        var admission = new UserHostAdmission();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = "cortex-closed-" + Guid.NewGuid().ToString("N");
        var server = new CortexResidentServer(new HeldApplication(), registry, owner, CortexLocalProductCommands.Version, TextWriter.Null, admission);
        var running = server.RunNativeAsync(name, stop.Token);
        try
        {
            await using var existing = await CortexMcpPipe.ConnectAsync(name, owner, owner.ApplicationPath!, stop.Token);
            var ready = await HelloAsync(existing, key.ApiKey);
            LocalAssert.True(ready.GetProperty("state").GetString() == "ready", "Open owner did not authenticate ready.");
            using (var closing = admission.TryClose()!)
            {
                await closing.WhenIdleAsync(stop.Token);
                await BinaryProtocol.WriteAsync(existing, new { id = "closing-ping", op = "ping" }, stop.Token);
                var ping = await BinaryProtocol.ReadAsync<JsonElement>(existing, stop.Token);
                LocalAssert.True(ping.GetProperty("result").GetProperty("state").GetString() == "stopping",
                    "Closed admission advertised ready on an existing identity lease.");
                await using var newcomer = await CortexMcpPipe.ConnectAsync(name, owner, owner.ApplicationPath!, stop.Token);
                var notReady = await HelloAsync(newcomer, key.ApiKey);
                LocalAssert.True(notReady.GetProperty("ok").GetBoolean() && notReady.GetProperty("state").GetString() == "stopping",
                    "Closed admission advertised ready to a new authenticated peer.");
                await using var rejected = await CortexMcpPipe.ConnectAsync(name, owner, owner.ApplicationPath!, stop.Token);
                var denied = await HelloAsync(rejected, new string('0', 64));
                LocalAssert.True(!denied.GetProperty("ok").GetBoolean(), "Shutdown status bypassed credential rejection.");
            }
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task<JsonElement> HelloAsync(Stream pipe, string apiKey)
        {
            await BinaryProtocol.WriteAsync(pipe, new { op = "hello", contract = CortexResidentServer.NativeContract,
                key.ClientId, key.KeyId, apiKey }, stop.Token, 4096);
            return await BinaryProtocol.ReadAsync<JsonElement>(pipe, stop.Token);
        }
    }

    internal static async Task SemanticDrainAndSelectionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-admission-" + Guid.NewGuid().ToString("N"));
        var owner = LocalIdentity.Current();
        var registry = new CortexClientRegistry(Path.Combine(root, "clients"), owner);
        var key = registry.Register("lifecycle", owner.ApplicationPath!, "native",
            new CortexClientDefaults(["alpha"], ["alpha"]), DateTimeOffset.UtcNow.AddDays(1));
        var lease = registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "native", owner);
        var gate = new UserHostAdmission();
        var inner = new HeldApplication();
        var scoped = new CortexClientApplication(inner, registry, lease, admission: gate);
        var first = scoped.CallToolAsync("held", null, CancellationToken.None).AsTask();
        await inner.Started.Task;
        using (var closing = gate.TryClose()!)
        {
            var rejected = await scoped.CallToolAsync("must-not-run", null, CancellationToken.None);
            LocalAssert.True(rejected["isError"]!.GetValue<bool>() && inner.Calls == 1,
                "Closing resident dispatched another semantic operation.");
            LocalAssert.True(!closing.WhenIdleAsync(CancellationToken.None).IsCompleted,
                "Resident disposed dependencies under an admitted semantic call.");
            inner.Release.SetResult();
            await first;
            await closing.WhenIdleAsync(CancellationToken.None);
        }
        LocalAssert.True(gate.Accepting && inner.Calls == 1,
            "Refused Exit replayed a semantic request.");

        var selected = CortexLocalProductCommands.Version;
        var hello = JsonSerializer.SerializeToElement(new
        {
            ok = true, contract = CortexResidentServer.McpContract, product = "Pegasus Cortex", version = selected,
            state = "ready", storageKind = "sqlite-v2", ownerSid = owner.Sid, session = owner.Session, clientId = "lifecycle"
        });
        LocalAssert.True(CortexResidentRuntime.ValidHello(hello, CortexResidentServer.McpContract, owner, "lifecycle", selected),
            "Selected resident hello was refused.");
        LocalAssert.True(!CortexResidentRuntime.ValidHello(hello, CortexResidentServer.McpContract, owner, "lifecycle", "2.2026.11.14.02.40.A"),
            "A stale resident version was admitted.");
    }

    private sealed class HeldApplication : ICortexMcpProtocolApplication
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public JsonArray ListTools() => new();
        public async ValueTask<JsonObject> CallToolAsync(string name, JsonElement? args, CancellationToken token)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task;
            return new JsonObject { ["isError"] = false };
        }
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken token) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken token) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken token) => ValueTask.FromResult<JsonObject?>(null);
    }
}
