using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Product;

/// <summary>
/// Test-executable-only entry point using the real source server, SQLite and
/// asset reader. Never resolves installed layout, profile, dispatch or startup.
/// </summary>
internal static class NativeInteropHarness
{
    private sealed record Fixture(int Schema, string Root, string Pipe, string OwnerSid, int Session,
        string ProviderExecutable, string ClientExecutable, int LifetimeSeconds);
    private static readonly JsonSerializerOptions Json = new(CortexClientRegistry.Json)
    { WriteIndented = false };
    private const string ClientId = "cerebral";
    private const string FixtureNamespace = "project.cerebral-interop";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // Launch the apphost so its OS executable identity is unambiguous.
            if (args is not ["probe", "--root", _] && !StringComparer.OrdinalIgnoreCase.Equals(Environment.ProcessPath, AppHost()))
                throw new InvalidDataException("interop_apphost_required");
            if (args is ["prepare", "--application", var application])
            {
                application = CortexProductLayout.RequireCanonicalAbsoluteDirectory(application, nameof(application));
                CortexProductLayout.RejectExistingReparseComponents(application);
                if (!File.Exists(application) || !application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("interop_client_invalid");
                var parent = ScratchParent();
                CortexProductLayout.RejectExistingReparseComponents(parent);
                var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                if (Directory.Exists(root) || File.Exists(root)) throw new IOException("interop_root_exists");
                CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
                var owner = LocalIdentity.Current();
                if (owner.Session <= 0) throw new InvalidDataException("interop_user_session_required");
                var fixture = new Fixture(1, root, "pegasus-cortex-interop-" + Path.GetFileName(root),
                    owner.Sid, owner.Session, Environment.ProcessPath!, application, 900);
                var registry = Registry(fixture);
                var key = registry.Register(ClientId, application, "native",
                    new([FixtureNamespace, "knowledge.shared"], [FixtureNamespace]), DateTimeOffset.UtcNow.AddHours(1));
                CortexClientRegistry.SaveProtectedCredential(KeyFile(fixture, ClientId), key);
                using var file = new FileStream(Path.Combine(root, "fixture.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, fixture, Json); file.Flush(true);
                File.WriteAllText(Path.Combine(root, "endpoint.json"), JsonSerializer.Serialize(Descriptor(fixture), Json), new UTF8Encoding(false));
                Print(Descriptor(fixture));
                return 0;
            }
            if (args is [var action, "--root", var selected])
            {
                var fixture = Load(selected);
                if (action == "describe") { Print(Descriptor(fixture)); return 0; }
                if (action == "stop")
                {
                    var path = Path.Combine(fixture.Root, "stop.request");
                    if (!File.Exists(path)) using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                    Print(new { ok = true, state = "stop-requested", root = fixture.Root }); return 0;
                }
                if (action == "serve") { await ServeAsync(fixture); return 0; }
                if (action == "probe")
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Environment.ProcessPath, fixture.ClientExecutable))
                        throw new InvalidDataException("interop_client_identity_mismatch");
                    await ProbeAsync(fixture); return 0;
                }
            }
            Print(new { ok = false, error = "native_interop_arguments_invalid" }); return 2;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or JsonException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // No exception text, credential fields or request bodies in logs.
            Print(new { ok = false, error = "native_interop_failed", cause = error.GetType().Name }); return 3;
        }
    }

    private static async Task ServeAsync(Fixture fixture)
    {
        // One composition per fixture, even if two operators start it at once.
        using var lease = new FileStream(Path.Combine(fixture.Root, "provider.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stopFile = Path.Combine(fixture.Root, "stop.request");
        if (File.Exists(stopFile)) throw new InvalidDataException("interop_already_stopped");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(fixture.LifetimeSeconds));
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var watcher = new FileSystemWatcher(fixture.Root, "stop.request") { NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, _) => stop.Cancel();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(stopFile)) stop.Cancel();
        try
        {
            var data = Path.Combine(fixture.Root, "data");
            CortexUserStateDirectories.EnsureCurrentUserDirectory(data);
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                Path.Combine(data, "cortex-v2.db"), TimeProvider.System, stop.Token, new UnloadedProfile());
            var assets = new CortexAssetStoreV2(Path.Combine(fixture.Root, "assets"));
            var source = Path.Combine(fixture.Root, "fixture-asset.bin");
            if (!File.Exists(source)) await File.WriteAllBytesAsync(source, [1, 2, 3, 255], stop.Token);
            var asset = await assets.ImportAsync(source,
                new("knowledge.shared", "interop.asset", "Interop asset", "Passive source fixture", "application/octet-stream"),
                cancellationToken: stop.Token);
            await using var execution = new DisabledExecution();
            var app = new CortexUnifiedMcpApplication(composition.Application, execution,
                new CortexAssetResourcesV2(assets, ["knowledge.shared"], new UnloadedProfile()));
            var server = new CortexResidentServer(app, Registry(fixture), LocalIdentity.Current(),
                CortexLocalProductCommands.Version, Console.Error);
            var running = server.RunNativeAsync(fixture.Pipe, stop.Token);
            if (running.IsCompleted) await running;
            File.WriteAllText(Path.Combine(fixture.Root, "ready.json"), JsonSerializer.Serialize(new
            {
                isolated = true, pid = Environment.ProcessId, expectedHostExecutable = fixture.ProviderExecutable,
                nativePipe = fixture.Pipe, assetUri = "cortex://" + asset.Ref, expiresUtc = DateTimeOffset.UtcNow.AddSeconds(fixture.LifetimeSeconds)
            }, Json), new UTF8Encoding(false));
            Print(new { ok = true, state = "listening", isolated = true, nativePipe = fixture.Pipe,
                assetUri = "cortex://" + asset.Ref, lifetimeSeconds = fixture.LifetimeSeconds });
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            if (server.ConnectionCount != 0) throw new InvalidDataException("interop_sessions_retained");
            Print(new { ok = true, state = "stopped", activeConnections = 0 });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task ProbeAsync(Fixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Connect verifies the actual pipe-server OS PID/path/SID/session before
        // reading the DPAPI credential. No installed route or alternate provider.
        await using var pipe = await CortexMcpPipe.ConnectAsync(fixture.Pipe, LocalIdentity.Current(), fixture.ProviderExecutable, deadline.Token);
        var key = CortexClientRegistry.ReadProtectedCredential(KeyFile(fixture, ClientId));
        await BinaryProtocol.WriteAsync(pipe, new { op = "hello", contract = CortexResidentServer.NativeContract,
            key.ClientId, key.KeyId, key.ApiKey }, deadline.Token, 4096);
        var hello = await BinaryProtocol.ReadAsync<JsonElement>(pipe, deadline.Token, CortexResidentServer.ResponseBytes);
        Require(CortexResidentRuntime.ValidHello(hello, CortexResidentServer.NativeContract, LocalIdentity.Current(), ClientId));
        var catalog = await Request(new { id = "catalog", op = "catalog" });
        Require(catalog["result"]?["operations"]?.AsArray().Count == 8 && catalog["result"]?["namespacePolicy"]?.GetValue<string>() == "selection");
        _ = await Call("status", "cortex_status", new { });
        var taskId = "interop.probe." + Path.GetFileName(fixture.Root);
        _ = await Call("start", "cortex_start", new { namespaces = new[] { FixtureNamespace, "knowledge.shared" },
            taskId, intent = "Source interoperability fixture", format = "compact", maxUtf8Bytes = 4096 });
        _ = await Call("create", "cortex_task", new { @namespace = FixtureNamespace, action = "create", taskId,
            title = "Interop marker", content = "exact\r\nΩ\t漢字", idempotencyKey = "interop-create" });
        _ = await Call("finish", "cortex_finish", new { @namespace = FixtureNamespace, taskId, expectedRevision = 1,
            taskAction = "update", checkpoint = "Isolated native fixture", evidence = "Real source pipe and scratch SQLite",
            knowledge = Array.Empty<object>(), taskContent = "exact\r\nΩ\t漢字", idempotencyKey = "interop-finish" });
        var read = await Call("read", "cortex_read", new { @namespace = FixtureNamespace, recordType = "task", recordId = taskId });
        Require(read["result"]?["record"]?["content"]?.GetValue<string>() == "exact\r\nΩ\t漢字");
        _ = await Call("search", "cortex_search", new { namespaces = new[] { FixtureNamespace }, text = "Interop marker", format = "compact" });
        var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "ready.json")))!;
        var bytes = await Request(new { id = "asset", op = "resource", uri = ready["assetUri"]!.GetValue<string>() + "?part=bytes" });
        Require(Convert.FromBase64String(bytes["result"]!["contents"]![0]!["blob"]!.GetValue<string>()).SequenceEqual(new byte[] { 1, 2, 3, 255 }));
        Print(new { ok = true, state = "probe-passed", isolated = true, contract = CortexResidentServer.NativeContract,
            operations = 8, exactUnicodeRoundTrip = true, atomicFinish = true, exactAssetBytes = true, mcp = false });

        Task<JsonObject> Call(string id, string name, object arguments) => Request(new { id, op = "call", name, arguments });
        async Task<JsonObject> Request(object request)
        {
            await BinaryProtocol.WriteAsync(pipe, request, deadline.Token, CortexResidentServer.RequestBytes);
            var response = await BinaryProtocol.ReadAsync<JsonElement>(pipe, deadline.Token, CortexResidentServer.ResponseBytes);
            var node = JsonNode.Parse(response.GetRawText())!.AsObject();
            Require(node["ok"]?.GetValue<bool>() == true);
            return node;
        }
    }

    private static Fixture Load(string root)
    {
        root = CortexProductLayout.RequireCanonicalAbsoluteDirectory(root, nameof(root));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(root), ScratchParent()) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new InvalidDataException("interop_root_invalid");
        CortexProductLayout.RejectExistingReparseComponents(root);
        if (!Directory.Exists(root)) throw new InvalidDataException("interop_root_absent");
        CortexUserStateDirectories.RequireUserStateDirectoryAclForProtection(new DirectoryInfo(root).GetAccessControl(),
            WindowsIdentity.GetCurrent().User!, requirePrivate: true);
        var path = Path.Combine(root, "fixture.json");
        CortexProductLayout.RejectExistingReparseComponents(path);
        if (new FileInfo(path).Length is < 1 or > 16384) throw new InvalidDataException("interop_fixture_invalid");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("interop_fixture_invalid");
        var owner = LocalIdentity.Current();
        if (fixture.Schema != 1 || fixture.Root != root || fixture.OwnerSid != owner.Sid || fixture.Session != owner.Session ||
            fixture.Pipe != "pegasus-cortex-interop-" + Path.GetFileName(root) || fixture.LifetimeSeconds is < 1 or > 900 ||
            !StringComparer.OrdinalIgnoreCase.Equals(fixture.ProviderExecutable, AppHost()))
            throw new InvalidDataException("interop_fixture_invalid");
        return fixture;
    }

    private static string ScratchParent()
    {
        for (var source = new DirectoryInfo(AppContext.BaseDirectory); source is not null; source = source.Parent)
            if (File.Exists(Path.Combine(source.FullName, "PegasusCortex.slnx")))
                return Path.Combine(source.FullName, ".tmp", "native-interop");
        throw new InvalidDataException("interop_source_root_required");
    }
    private static CortexClientRegistry Registry(Fixture fixture) => new(Path.Combine(fixture.Root, "connections"), LocalIdentity.Current());
    private static string AppHost() => Path.Combine(AppContext.BaseDirectory, "PegasusCortex.Mcp.Local.Tests.exe");
    private static string KeyFile(Fixture fixture, string id) => Path.Combine(fixture.Root, "connections", id + ".key");
    private static object Descriptor(Fixture fixture) => new { ok = true, schema = "pegasus.cortex.native-interop/1", isolated = true,
        root = fixture.Root, contract = CortexResidentServer.NativeContract, nativePipe = fixture.Pipe,
        fixture.OwnerSid, fixture.Session, expectedHostExecutable = fixture.ProviderExecutable,
        clientId = ClientId, credentialDirectory = Path.Combine(fixture.Root, "connections"), credentialFile = KeyFile(fixture, ClientId),
        databasePath = Path.Combine(fixture.Root, "data", "cortex-v2.db"), fixtureNamespace = FixtureNamespace, personalProfile = "not_loaded", pao = false,
        maximumRequestBytes = CortexResidentServer.RequestBytes, maximumResponseBytes = CortexResidentServer.ResponseBytes,
        fixture.LifetimeSeconds, network = false, availability = "descriptor-only; validate real hello" };
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("interop_probe_failed"); }
    private sealed class UnloadedProfile : ICortexPersonalProfileProvider
    { public ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new CortexPersonalProfileV2("not_loaded", IssueCode: "interop_profile_not_loaded")); }
    private sealed class DisabledExecution : IOperationDispatcher
    {
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("interop_execution_disabled");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
