using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.File.Assets;

internal static partial class ResidentTransportTests
{
    public static Task RegisteredKeysAndDefaultsAsync()
    {
        var directory = Temporary();
        try
        {
            var owner = LocalIdentity.Current();
            var clock = new Clock();
            var registry = new CortexClientRegistry(Path.Combine(directory, "clients"), owner, clock);
            var defaults = new CortexClientDefaults(["alpha", "knowledge.shared"], ["alpha"], true);
            var key = registry.Register("cerebral", owner.ApplicationPath!, "native", defaults, clock.GetUtcNow().AddDays(90));
            var lease = registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "native", owner);
            LocalAssert.True(registry.RequireActive(lease).Defaults.WriteNamespaces.SequenceEqual(["alpha"]), "Routing defaults changed.");
            Throws(() => registry.Authenticate(key.ClientId, key.KeyId, new string('0', 64), "native", owner));
            Throws(() => registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "mcp", owner));
            Throws(() => registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "native", owner with { Sid = "S-1-5-19" }));
            Throws(() => registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "native", owner with { Session = owner.Session + 1 }));
            Throws(() => registry.Authenticate(key.ClientId, key.KeyId, key.ApiKey, "native", owner with { ApplicationPath = Path.Combine(directory, "other.exe") }));
            Throws(() => registry.Register("bad", owner.ApplicationPath!, "native", defaults, clock.GetUtcNow()));
            Throws(() => registry.Register("bad", owner.ApplicationPath!, "native", new(["*"], []), clock.GetUtcNow().AddDays(1)));
            var path = Path.Combine(directory, "clients", "cerebral.key");
            CortexClientRegistry.SaveProtectedCredential(path, key);
            var decoded = CortexClientRegistry.ReadProtectedCredential(path);
            LocalAssert.True(decoded == key, "DPAPI credential round trip changed fields.");
            LocalAssert.True(!File.ReadAllText(Path.Combine(directory, "clients", "clients.v1.json")).Contains(key.ApiKey, StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(key.ApiKey, StringComparison.Ordinal), "Plain API key retained.");
            var rotated = registry.Register("cerebral", owner.ApplicationPath!, "native", defaults, clock.GetUtcNow().AddDays(90), rotate: true);
            Throws(() => registry.RequireActive(lease));
            var current = registry.Authenticate(rotated.ClientId, rotated.KeyId, rotated.ApiKey, "native", owner);
            registry.Revoke("cerebral"); Throws(() => registry.RequireActive(current));
            var expires = registry.Register("short", owner.ApplicationPath!, "native", defaults, clock.GetUtcNow().AddHours(1));
            var expiring = registry.Authenticate(expires.ClientId, expires.KeyId, expires.ApiKey, "native", owner);
            clock.Now += TimeSpan.FromHours(2); Throws(() => registry.RequireActive(expiring));
            LocalAssert.True(!JsonSerializer.Serialize(registry.List()).Contains(rotated.ApiKey, StringComparison.Ordinal), "Public status leaked a key.");
            return Task.CompletedTask;
        }
        finally { Cleanup(directory); }
    }

    public static async Task NativeAndMcpShareRuntimeAsync()
    {
        var directory = Temporary();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var owner = LocalIdentity.Current();
            var profile = new Profile();
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(Path.Combine(directory, "cortex-v2.db"), TimeProvider.System, stop.Token, profile);
            await using var execution = new FakeExecution();
            var registry = new CortexClientRegistry(Path.Combine(directory, "clients"), owner);
            var defaults = new CortexClientDefaults(["alpha"], ["alpha"], true);
            var nativeKey = registry.Register("cerebral", owner.ApplicationPath!, "native", defaults, DateTimeOffset.UtcNow.AddDays(90));
            var mcpKey = registry.Register("codex-test", owner.ApplicationPath!, "mcp", defaults, DateTimeOffset.UtcNow.AddDays(90));
            var assetSource = Path.Combine(directory, "asset.bin");
            await File.WriteAllBytesAsync(assetSource, [1, 2, 3], stop.Token);
            var assetStore = new CortexAssetStoreV2(Path.Combine(directory, "assets"));
            var asset = await assetStore.ImportAsync(assetSource, new("knowledge.shared", "fixture.asset", "Fixture asset", "Passive reference", "application/octet-stream"), cancellationToken: stop.Token);
            var resources = new CortexAssetResourcesV2(assetStore, ["alpha"], profile);
            using var diagnostics = new StringWriter();
            var server = new CortexResidentServer(new CortexUnifiedMcpApplication(composition.Application, execution, resources), registry, owner,
                "2.2026.10.07.15.19.A", diagnostics);
            var nativeName = "cortex-native-test-" + Guid.NewGuid().ToString("N");
            var mcpName = "cortex-mcp-test-" + Guid.NewGuid().ToString("N");
            var running = server.RunAsync(nativeName, mcpName, stop.Token);
            try
            {
                await using var native = await CortexMcpPipe.ConnectAsync(nativeName, owner, owner.ApplicationPath!, stop.Token);
                var nativeHello = await Hello(native, nativeKey, CortexResidentServer.NativeContract, stop.Token);
                LocalAssert.True(CortexResidentRuntime.ValidHello(nativeHello, CortexResidentServer.NativeContract, owner, "cerebral"), "Native identity handshake failed.");
                var catalog = await Request(native, """{"id":"cat","op":"catalog"}""", stop.Token);
                LocalAssert.True(catalog["ok"]!.GetValue<bool>() && catalog["result"]!["operations"]!.AsArray().Count == 9 &&
                    catalog["result"]!["namespacePolicy"]!.GetValue<string>() == "selection",
                    "Library catalog did not contain all eight semantic operations plus PAO.");
                var assetResult = await Request(native, JsonSerializer.Serialize(new { id = "asset", op = "resource", uri = "cortex://" + asset.Ref + "?part=bytes" }), stop.Token);
                LocalAssert.True(assetResult["ok"]!.GetValue<bool>() &&
                    Convert.FromBase64String(assetResult["result"]!["contents"]![0]!["blob"]!.GetValue<string>()).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                    execution.Calls == 0, "Native unique asset reference was namespace-gated or dispatched execution.");
                var created = await Request(native, """{"id":"create","op":"call","name":"cortex_task","arguments":{"namespace":"alpha","action":"create","taskId":"native-task","title":"Native task","content":"exact\r\nΩ\t漢字","idempotencyKey":"native-task-create"}}""", stop.Token);
                LocalAssert.True(created["ok"]!.GetValue<bool>() && created["result"]!["task"]!["revision"]!.GetValue<int>() == 1, "Native write failed.");
                foreach (var allowed in new[]
                {
                    """{"id":"other","op":"call","name":"cortex_task","arguments":{"namespace":"beta","action":"create","taskId":"other","title":"Other task","content":"other exact body","idempotencyKey":"other-create"}}""",
                    """{"id":"shared","op":"call","name":"cortex_knowledge","arguments":{"namespace":"knowledge.shared","action":"create","recordId":"shared-library","title":"Library reference","content":"shared exact body","idempotencyKey":"shared-create","kind":"observation","tier":"verified","tags":[],"provenance":{"source":"agent"}}}""",
                    """{"id":"search","op":"call","name":"cortex_search","arguments":{"namespaces":["alpha","beta","knowledge.shared"],"text":"body"}}""",
                    """{"id":"batch","op":"call","name":"cortex_read","arguments":{"records":[{"namespace":"alpha","recordType":"task","recordId":"native-task"},{"namespace":"beta","recordType":"task","recordId":"other"},{"namespace":"knowledge.shared","recordType":"knowledge","recordId":"shared-library"}]}}""",
                    """{"id":"namespaces","op":"call","name":"cortex_namespaces","arguments":{}}"""
                })
                {
                    var result = await Request(native, allowed, stop.Token);
                    LocalAssert.True(result["ok"]!.GetValue<bool>(), "Routing defaults became namespace permissions: " + result.ToJsonString());
                }
                var seededProfile = await Request(native, """{"id":"personal-create","op":"call","name":"cortex_knowledge","arguments":{"namespace":"knowledge.user","action":"create","recordId":"personal","title":"Personal reference","content":"personal exact body","idempotencyKey":"personal-create","kind":"preference","tier":"explicit","tags":[],"provenance":{"source":"user"}}}""", stop.Token);
                LocalAssert.True(seededProfile["ok"]!.GetValue<bool>(), "Loaded personal profile was denied by routing defaults.");
                const string personalRead = """{"id":"personal-read","op":"call","name":"cortex_read","arguments":{"namespace":"knowledge.user","recordType":"knowledge","recordId":"personal"}}""";
                var personal = await Request(native, personalRead, stop.Token);
                LocalAssert.True(personal["result"]!["record"]!["content"]!.GetValue<string>() == "personal exact body", "Loaded profile read changed content.");
                profile.Current = new("not_loaded", Generation: "unloaded", IssueCode: "ui_unavailable");
                var unloaded = await Request(native, personalRead, stop.Token);
                LocalAssert.True(!unloaded["ok"]!.GetValue<bool>() && unloaded.ToJsonString().Contains("personal_profile_not_loaded", StringComparison.Ordinal), "Unloaded personal profile remained readable.");
                var core = await Request(native, """{"id":"core","op":"call","name":"cortex_read","arguments":{"namespace":"beta","recordType":"task","recordId":"other"}}""", stop.Token);
                LocalAssert.True(core["ok"]!.GetValue<bool>(), "Profile unload disabled the project library.");
                profile.Current = new("loaded", "fixture", "reloaded");
                LocalAssert.True((await Request(native, personalRead, stop.Token))["ok"]!.GetValue<bool>(), "Profile reload required new client grants.");
                var privileged = await Request(native, """{"id":"deny","op":"call","name":"pao","arguments":{"op":"diag","root":"AB12"}}""", stop.Token);
                LocalAssert.True(!privileged["ok"]!.GetValue<bool>() && execution.Calls == 0, "API key authorized privileged execution.");
                foreach (var adminOperation in new[] {"admin_admit","admin_run","admin_stat","admin_revoke","admin_reconcile"})
                {
                    var adminRequest = JsonSerializer.Serialize(new {id="deny-"+adminOperation,op="call",name="pao",arguments=new{op=adminOperation}});
                    var denied = await Request(native,adminRequest,stop.Token);
                    LocalAssert.True(!denied["ok"]!.GetValue<bool>() && execution.Calls==0,"Native library credentials crossed the owner-admin execution boundary.");
                }
                var pao = await Request(native, """{"id":"pao","op":"call","name":"pao","arguments":{"op":"diag"}}""", stop.Token);
                LocalAssert.True(pao["ok"]!.GetValue<bool>() && execution.Calls == 1, "User PAO parity failed.");
                var malformed = await Request(native, """{"id":"bad","op":"ping","command":"anything"}""", stop.Token);
                LocalAssert.True(!malformed["ok"]!.GetValue<bool>(), "Generic command field accepted.");
                await using (var mcp = await CortexMcpPipe.ConnectAsync(mcpName, owner, owner.ApplicationPath!, stop.Token))
                {
                    var hello = await Hello(mcp, mcpKey, CortexResidentServer.McpContract, stop.Token);
                    LocalAssert.True(CortexResidentRuntime.ValidHello(hello, CortexResidentServer.McpContract, owner, "codex-test"), "MCP pipe identity failed.");
                    const string input = """
                    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}
                    {"jsonrpc":"2.0","method":"notifications/initialized"}
                    {"jsonrpc":"2.0","id":"\u96ea","method":"tools/call","params":{"name":"cortex_read","arguments":{"namespace":"alpha","recordType":"task","recordId":"native-task"}}}
                    """;
                    using var inputReader = new StreamReader(mcp, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                    await using var inputWriter = new StreamWriter(mcp, new UTF8Encoding(false, true), 4096, leaveOpen: true) { NewLine = "\n" };
                    await inputWriter.WriteLineAsync(input.AsMemory(), stop.Token);
                    await inputWriter.FlushAsync(stop.Token);
                    var responses = new BoundedJsonLineReader(inputReader, CortexResidentServer.ResponseBytes);
                    var lines = new string[2];
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var line = await responses.ReadAsync(stop.Token);
                        LocalAssert.True(!line.EndOfStream && !line.ExceededLimit, "Native MCP response was missing or exceeded its bound.");
                        lines[i] = line.Line!;
                    }
                    LocalAssert.True(lines.Length == 2 && lines[1].Contains("native-task", StringComparison.Ordinal), "MCP could not read the native write from the same runtime.");
                    using var response = JsonDocument.Parse(lines[1]);
                    LocalAssert.True(response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("record").GetProperty("content").GetString() == "exact\r\nΩ\t漢字", "Exact bytes changed across adapters.");
                }
                var alive = await Request(native, """{"id":"alive","op":"ping"}""", stop.Token);
                LocalAssert.True(alive["ok"]!.GetValue<bool>(), "Closing MCP killed the native connection/runtime.");
                await using (var wrong = await CortexMcpPipe.ConnectAsync(nativeName, owner, owner.ApplicationPath!, stop.Token))
                {
                    var denial = await Hello(wrong, nativeKey with { ApiKey = new string('0', 64) }, CortexResidentServer.NativeContract, stop.Token);
                    LocalAssert.True(!denial.GetProperty("ok").GetBoolean(), "Wrong key admitted.");
                }
                await using (var mismatch = await CortexMcpPipe.ConnectAsync(nativeName, owner, owner.ApplicationPath!, stop.Token))
                {
                    var denial = await Hello(mismatch, nativeKey, "pegasus.cortex.native/99", stop.Token);
                    LocalAssert.True(denial.GetProperty("error").GetProperty("code").GetString() == "contract_mismatch", "Contract skew was not explicit.");
                }
                registry.Revoke("cerebral");
                var revoked = await Request(native, """{"id":"revoked","op":"ping"}""", stop.Token);
                LocalAssert.True(!revoked["ok"]!.GetValue<bool>(), "Revoked active session continued.");
                LocalAssert.True(!diagnostics.ToString().Contains(nativeKey.ApiKey, StringComparison.Ordinal), "Diagnostics leaked a credential.");
            }
            finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            LocalAssert.True(server.ConnectionCount == 0, "Shutdown retained sessions.");
        }
        finally { Cleanup(directory); }
    }

    public static async Task FramingAndCancellationAsync()
    {
        ThrowsArgument(() => Pegasus.Cortex.Mcp.Local.Configuration.CortexMcpLocalArguments.Parse(["stdio", "--client", "external-test"]));
        ThrowsArgument(() => Pegasus.Cortex.Mcp.Local.Configuration.CortexMcpLocalArguments.Parse(["stdio", "--client", "../escape"]));
        using (var roundTrip = new MemoryStream())
        {
            await BinaryProtocol.WriteAsync(roundTrip, new { text = "Ω\r\n\t漢字\0" });
            LocalAssert.True(Encoding.UTF8.GetString(roundTrip.ToArray()).Contains("漢字", StringComparison.Ordinal), "Wire JSON needlessly escaped Unicode.");
            roundTrip.Position = 0;
            var body = await BinaryProtocol.ReadAsync<JsonElement>(roundTrip);
            LocalAssert.True(body.GetProperty("text").GetString() == "Ω\r\n\t漢字\0", "Wire controls changed.");
        }
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), CortexResidentServer.RequestBytes + 1);
        using (var invalid = new MemoryStream(header))
            await MustFailAsync(() => BinaryProtocol.ReadAsync<JsonElement>(invalid, CancellationToken.None, CortexResidentServer.RequestBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(header, 99);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        using (var mismatch = new MemoryStream(header)) await MustFailAsync(() => BinaryProtocol.ReadAsync<JsonElement>(mismatch));
        using (var truncated = new MemoryStream(header[..3])) await MustFailAsync(() => BinaryProtocol.ReadAsync<JsonElement>(truncated));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var blocked = new BoundedJsonLineReader(new BlockingReader(), 64);
        await MustFailAsync(async () => { await blocked.ReadAsync(canceled.Token); });
        var invalidHello = JsonSerializer.SerializeToElement(new { ok = true, contract = CortexResidentServer.NativeContract,
            product = "Pegasus Cortex", ownerSid = "test", session = "not-a-number", clientId = "test" });
        LocalAssert.True(!CortexResidentRuntime.ValidHello(invalidHello, CortexResidentServer.NativeContract, new("test", 1), "test"), "Malformed identity threw or was accepted.");
    }

    public static Task DirectAdapterSelectionAsync()
    {
        var root = Temporary();
        try
        {
            const string release = "2.2026.10.08.05.26.A";
            var components = new Dictionary<string, string> { ["mcp"] = "semantics", ["host"] = "execution",
                ["service"] = "execution", ["pao"] = "execution", ["process-host"] = "execution", ["ui-host"] = "execution",
                ["launcher"] = "launcher", ["setup"] = "installer" };
            var catalog = Pegasus.Cortex.Product.CortexProductCatalogV1.Create(release, 1, 2, 1,
                Pegasus.Cortex.Product.CortexProductCatalogV1.StableRolePaths.Select(role =>
                    new Pegasus.Cortex.Product.CortexProductRoleV1(role.Key, components[role.Key], release, role.Value)).ToArray());
            var catalogPath = Path.Combine(root, "active.v1.json");
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, CortexClientRegistry.Json));
            var adapter = Path.Combine(root, "bin", "semantics", "PegasusCortex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(adapter)!); File.WriteAllBytes(adapter, [0]);
            CortexResidentRuntime.RequireSelectedAdapter(root, catalogPath, adapter, release);
            Throws(() => CortexResidentRuntime.RequireSelectedAdapter(root, catalogPath, Path.Combine(root, "PegasusCortex.exe"), release));
            Throws(() => CortexResidentRuntime.RequireSelectedAdapter(root, catalogPath, adapter, "2.2026.10.09.05.26.A"));
            File.Delete(adapter);
            Throws(() => CortexResidentRuntime.RequireSelectedAdapter(root, catalogPath, adapter, release));
        }
        finally { Cleanup(root); }
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Hello(Stream pipe, CortexClientCredential credential, string contract, CancellationToken token)
    {
        await BinaryProtocol.WriteAsync(pipe, new { op = "hello", contract, credential.ClientId, credential.KeyId, credential.ApiKey }, token, 4096);
        return await BinaryProtocol.ReadAsync<JsonElement>(pipe, token, CortexResidentServer.ResponseBytes);
    }
    private static async Task<JsonObject> Request(Stream pipe, string json, CancellationToken token)
    {
        await BinaryProtocol.WriteAsync(pipe, JsonSerializer.Deserialize<JsonElement>(json), token, CortexResidentServer.RequestBytes);
        return await BinaryProtocol.ReadAsync<JsonObject>(pipe, token, CortexResidentServer.ResponseBytes);
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException) { return; }
        throw new InvalidOperationException("Expected rejection was absent.");
    }
    private static void ThrowsArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Unsafe client ID accepted.");
    }
    private static async Task MustFailAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected framing/cancellation failure was absent.");
    }
    private static string Temporary()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortex-resident-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static void Cleanup(string path)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-resident-tests"));
        var exact = Path.GetFullPath(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(exact), root) ||
            !Guid.TryParseExact(Path.GetFileName(exact), "N", out _)) throw new InvalidOperationException("Unsafe fixture cleanup target.");
        if (!Directory.Exists(exact)) return;
        var directories = new Stack<string>(); directories.Push(exact);
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse fixture cleanup target.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse fixture cleanup target.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
        Directory.Delete(exact, true);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Profile : ICortexPersonalProfileProvider
    {
        public CortexPersonalProfileV2 Current = new("loaded", "fixture", "initial");
        public ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);
    }
    private sealed class BlockingReader : TextReader
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
    }
    private sealed class FakeExecution : IOperationDispatcher
    {
        public int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new OperationReceipt(true, "fixture", "fixture", "done", request.Op)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
