using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.SharedMcp;

internal static class SharedMcpEmbeddingTests
{
    internal static async Task RunAsync()
    {
        await ResidentTransportTests.RegisteredKeysAndDefaultsAsync();
        await ManagedCodexBootstrapDoesNotGateOwnerAsync();
        ReadinessNeverReopensDuringDrain();
        await AdmissionAsync();
        await SharedSchedulingAsync();
        await ConnectionFactoryAdmissionAsync();
        await NativeFaultClosesIngressBeforeDrainAsync();
        await ServiceGatewayAsync();
        await ServiceInvocationEnvelopeAsync();
        Console.WriteLine("PASS embedded shared MCP: registered-client lease regression, optional managed-key refusal/no reissue, monotonic readiness, owner admission, same resident scheduler, typed-ID/context isolation, revoked factory/queued admission, cancellation uncertainty/drain and exact service identity envelope; in-memory and owned-file fixtures, no live endpoint");
    }


    private static async Task ManagedCodexBootstrapDoesNotGateOwnerAsync()
    {
        foreach (var expire in new[] { false, true })
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-codex-optional-" + Guid.NewGuid().ToString("N")));
            var app = new HeldApplication();
            var admission = new UserHostAdmission();
            var scheduler = new BoundedRequestDispatcher();
            CortexEmbeddedMcpDispatcher? endpoint = null;
            var gateway = new CortexOwnerMcpGateway();
            try
            {
                var owner = LocalIdentity.Current();
                var clock = new BootstrapClock();
                // A newly owned private root; no installed credentials or registry are read.
                var registry = new CortexClientRegistry(root, owner, clock);
                var codex = registry.Register("codex", owner.ApplicationPath!, "mcp", new([], [], true),
                    clock.Now.AddHours(1), managedAdapter: true);
                var codexPath = Path.Combine(root, "codex.key");
                CortexClientRegistry.SaveProtectedCredential(codexPath, codex);
                var lease = registry.Authenticate(codex.ClientId, codex.KeyId, codex.ApiKey, "mcp", owner);
                var native = registry.Register("fixture-native", owner.ApplicationPath!, "native", new([], []),
                    clock.Now.AddDays(90));
                using var diagnostics = new StringWriter();
                await CortexResidentRuntime.BootstrapManagedCodexCredentialAsync(registry, owner.ApplicationPath!, diagnostics);
                Check(diagnostics.ToString().Length == 0, "healthy managed bootstrap reported refusal");

                if (expire) clock.Now += TimeSpan.FromHours(2);
                else registry.Revoke("codex");
                var registryPath = Path.Combine(root, "clients.v1.json");
                var registryBefore = File.ReadAllBytes(registryPath);
                var keyBefore = File.ReadAllBytes(codexPath);
                var filesBefore = Directory.GetFiles(root).Order(StringComparer.Ordinal).ToArray();
                await CortexResidentRuntime.BootstrapManagedCodexCredentialAsync(registry, owner.ApplicationPath!, diagnostics);
                Check(diagnostics.ToString().Trim() ==
                    "component=managed-codex-credential state=unavailable cause=UnauthorizedAccessException",
                    "managed-key refusal diagnostic omitted its bounded cause");
                Check(File.ReadAllBytes(registryPath).SequenceEqual(registryBefore) &&
                    File.ReadAllBytes(codexPath).SequenceEqual(keyBefore) &&
                    Directory.GetFiles(root).Order(StringComparer.Ordinal).SequenceEqual(filesBefore),
                    "refused bootstrap repaired or reissued managed credential state");
                RefusesClient(() => registry.Authenticate(codex.ClientId, codex.KeyId, codex.ApiKey, "mcp", owner));
                RefusesClient(() => registry.RequireActive(lease));
                _ = registry.RequireActive(registry.Authenticate(native.ClientId, native.KeyId, native.ApiKey, "native", owner));

                // The actual owner wrapper/dispatcher and registered service gateway
                // remain independent of that refused private-client key.
                app.Release.TrySetResult();
                var ownerApp = CortexClientApplication.ForOwnerEndpoint(app, admission);
                endpoint = new(ownerApp, scheduler, admission, TextWriter.Null);
                var answered = await endpoint.DispatchAsync(Context('f', 101), Request(101, "owner-after-refusal"),
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Check(answered["result"]?["structuredContent"]?["tag"]?.GetValue<string>() == "owner-after-refusal",
                    "managed-key refusal disabled owner dispatch");
                gateway.Bind(CortexMcpSettingsStore.Default with { Enabled = true, Owner = CortexMcpOwner.Service },
                    () => new CortexEmbeddedMcpDispatcher(ownerApp, scheduler, admission, TextWriter.Null));
                var initialized = await gateway.ExecuteServiceRequestAsync(Relay("initialize", "{}"), CancellationToken.None);
                Check(initialized.Ok, "managed-key refusal disabled service initialization");
                var invocation = new JsonObject
                {
                    ["sessionId"] = new string('e', 32), ["streamId"] = 1, ["taskId"] = null,
                    ["requestId"] = "102", ["identity"] = IdentityJson(SyntheticIdentity()),
                    ["request"] = Request(102, "service-after-refusal")
                };
                var relayed = await gateway.ExecuteServiceRequestAsync(Relay("invoke", invocation.ToJsonString()),
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Check(relayed.Ok && relayed.Data?.GetValueOrDefault("mcp") is JsonObject result &&
                    result["result"]?["structuredContent"]?["tag"]?.GetValue<string>() == "service-after-refusal",
                    "managed-key refusal disabled service dispatch");
                Check(app.Calls == 2, "managed-key bootstrap replayed a semantic operation");
            }
            finally
            {
                app.Release.TrySetResult();
                try { await gateway.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally
                {
                    try { if (endpoint is not null) await endpoint.DisposeAsync(); }
                    finally
                    {
                        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                            !Path.GetFileName(root).StartsWith("cortex-codex-optional-", StringComparison.Ordinal) ||
                            Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException("owned bootstrap fixture cleanup changed");
                        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    }
                }
            }
        }

        static void RefusesClient(Action action)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("revoked or expired private-client key was accepted");
        }
    }

    private static void ReadinessNeverReopensDuringDrain()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalStates = new List<string>();
        var normal = new CortexResidentRuntime.Readiness(normalStates.Add);
        normal.Initializing();
        normal.ReadyIfServing(drain.Task);
        normal.Stopping();
        normal.ReadyIfServing(drain.Task);
        Check(!drain.Task.IsCompleted && normalStates.SequenceEqual(["initializing", "ready", "stopping"]),
            "long drain still reports ready or allows a stopping-to-ready transition");
        normal.Unavailable();
        normal.ReadyIfServing(drain.Task);
        normal.Initializing();
        Check(normalStates.SequenceEqual(["initializing", "ready", "stopping", "unavailable"]),
            "completed shutdown reopened readiness");

        var earlyStates = new List<string>();
        var early = new CortexResidentRuntime.Readiness(earlyStates.Add);
        early.Initializing();
        // A failed native startup can enter stopping before returning an
        // incomplete drain task to its caller's readiness publication.
        early.Stopping();
        early.ReadyIfServing(drain.Task);
        Check(earlyStates.SequenceEqual(["initializing", "stopping"]),
            "early native failure was overwritten by ready");
        early.Unavailable();
        var completedStates = new List<string>();
        new CortexResidentRuntime.Readiness(completedStates.Add).ReadyIfServing(Task.CompletedTask);
        Check(completedStates.Count == 0, "completed listener task was reported ready");

        var notifications = 0;
        var broken = new CortexResidentRuntime.Readiness(_ =>
        {
            notifications++;
            throw new IOException("owned presentation failure");
        });
        broken.Initializing();
        broken.Stopping(); // Must return normally so ingress close/drain can execute.
        var closureReached = true;
        broken.ReadyIfServing(drain.Task);
        broken.Unavailable();
        Check(closureReached && notifications == 3, "observer failure skipped closure or reset the stopping latch");
        drain.SetResult();
    }

    private sealed class BootstrapClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task AdmissionAsync()
    {
        var admission = new UserHostAdmission();
        var app = new HeldApplication();
        var owner = CortexClientApplication.ForOwnerEndpoint(app, admission);
        Check(owner.ListTools().Count == 1, "owner catalog");
        using (var close = admission.TryClose() ?? throw new InvalidOperationException("close admission"))
        {
            try { owner.ListTools(); throw new InvalidOperationException("closed catalog accepted"); }
            catch (IOException) { }
            var refusal = await owner.CallToolAsync("fixture_echo", null, CancellationToken.None);
            Check(refusal["isError"]!.GetValue<bool>() && app.Calls == 0, "closed owner call has no callback");
        }
        Check(owner.ListTools().Count == 1, "refused exit reopens admission");
    }

    private static async Task SharedSchedulingAsync()
    {
        var admission = new UserHostAdmission();
        var app = new HeldApplication { Expected = 2 };
        var scheduler = new BoundedRequestDispatcher();
        await using var adapter = new CortexEmbeddedMcpDispatcher(
            CortexClientApplication.ForOwnerEndpoint(app, admission), scheduler, admission, TextWriter.Null);
        var first = Request(7, "a");
        var second = Request(7, "b");
        try
        {
            var a = adapter.DispatchAsync(Context('a', 7), first, CancellationToken.None);
            var b = adapter.DispatchAsync(Context('b', 7), second, CancellationToken.None);
            await app.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first["params"]!["arguments"]!["tag"] = "mutated-a";
            second["params"]!["arguments"]!["tag"] = "mutated-b";
            Check(scheduler.Snapshot.Running == 2 && admission.ActiveCount == 2, "same resident scheduler/admission owns both callbacks");
            app.Release.TrySetResult();
            var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
            Check(results.All(r => r["id"]!.GetValue<int>() == 7), "duplicate numeric IDs remain connection-local");
            Check(results[0]["result"]!["structuredContent"]!["tag"]!.GetValue<string>() == "a" &&
                results[1]["result"]!["structuredContent"]!["tag"]!.GetValue<string>() == "b", "arguments survive transport-object mutation");
            await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            app.Reset(ignoreCancellation: true);
            using var cancel = new CancellationTokenSource();
            var pending = adapter.DispatchAsync(Context('c', 9), Request(9, "c"), cancel.Token);
            await app.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var unknown = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(unknown["error"]!["data"]!["outcome"]!.GetValue<string>() == "Unknown", "started cancellation remains unknown");
            Check(!scheduler.WhenIdleAsync().IsCompleted, "callback retains scheduler reservation after response cancellation");
            app.Release.TrySetResult();
            await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(app.Calls == 3 && admission.ActiveCount == 0, "no replay and actual callback drained");
        }
        finally
        {
            app.Release.TrySetResult();
            await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ServiceGatewayAsync()
    {
        var inactive = new CortexOwnerMcpGateway();
        var constructions = 0;
        inactive.Bind(CortexMcpSettingsStore.Default, () => { constructions++; return new GatewayDispatcher(); });
        var unavailable = await inactive.ExecuteServiceRequestAsync(Relay("initialize", "{}"), CancellationToken.None);
        Check(!unavailable.Ok && constructions == 0, "disabled gateway never constructs dispatcher");
        await inactive.CloseAndDrainAsync();

        var gateway = new CortexOwnerMcpGateway();
        var held = new GatewayDispatcher();
        gateway.Bind(CortexMcpSettingsStore.Default with { Enabled = true, Owner = CortexMcpOwner.Service }, () => held);
        try
        {
            var initialization = gateway.ExecuteServiceRequestAsync(Relay("initialize", "{}"), CancellationToken.None);
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = gateway.CloseAndDrainAsync();
            Check(!closing.IsCompleted && held.Disposals == 0, "gateway retains active initialization");
            var refusal = await gateway.ExecuteServiceRequestAsync(Relay("initialize", "{}"), CancellationToken.None);
            Check(!refusal.Ok, "closed gateway refuses a new initialization");
            held.Release.TrySetResult();
            Check((await initialization).Ok, "original initialization settles");
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await gateway.CloseAndDrainAsync();
            Check(held.Disposals == 1, "gateway disposes exactly once after actual callback");
        }
        finally
        {
            held.Release.TrySetResult();
            await gateway.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ConnectionFactoryAdmissionAsync()
    {
        var admission = new UserHostAdmission();
        var scheduler = new BoundedRequestDispatcher(workers: 1);
        var app = new HeldApplication();
        var expected = SyntheticIdentity();
        var approved = true;
        var factoryCalls = 0;
        CortexMcpInvocationProcessor Factory(McpRequestContext context)
        {
            Interlocked.Increment(ref factoryCalls);
            if (!Volatile.Read(ref approved) || context.Identity != expected)
                throw new UnauthorizedAccessException("fixture connection revoked");
            // Use the production wrapper's callback-time guard as well as the
            // pre-schedule factory check; queueing must not freeze old approval.
            return new(CortexClientApplication.ForOwnerEndpoint(app, admission,
                () => Volatile.Read(ref approved) && context.Identity == expected), TextWriter.Null);
        }
        await using var adapter = new CortexEmbeddedMcpDispatcher(
            CortexClientApplication.ForOwnerEndpoint(app, admission), scheduler, admission, TextWriter.Null, Factory);
        try
        {
            foreach (var context in new[]
            {
                Context('a', 31) with { Identity = null },
                Context('b', 31) with { Identity = new(expected.Authority, new string('4', 32), expected.Generation) },
                Context('c', 31) with { Identity = new(expected.Authority, expected.Subject, new string('5', 32)) }
            })
            {
                var refused = await adapter.DispatchAsync(context, Request(31, "unapproved"), CancellationToken.None);
                Check(refused["error"]?["data"]?["code"]?.GetValue<string>() == "connection_revoked" &&
                    app.Calls == 0 && scheduler.Snapshot.Count == 0 && admission.ActiveCount == 0,
                    "missing or foreign identity reached scheduler/application before factory refusal");
            }
            approved = false;
            var revoked = await adapter.DispatchAsync(Context('d', 32), Request(32, "revoked"), CancellationToken.None);
            Check(revoked["error"]?["data"]?["code"]?.GetValue<string>() == "connection_revoked" && app.Calls == 0,
                "revoked factory admitted a new application call");
            approved = true;
            var running = adapter.DispatchAsync(Context('e', 33), Request(33, "already-running"), CancellationToken.None);
            await app.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = adapter.DispatchAsync(Context('f', 34), Request(34, "queued-before-revoke"), CancellationToken.None);
            Check(scheduler.Snapshot.Running == 1 && scheduler.Snapshot.Count == 2 && app.Calls == 1,
                "revocation fixture did not hold one actually queued callback");
            approved = false;
            app.Release.TrySetResult();
            var results = await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(5));
            Check(results[0]["result"]?["structuredContent"]?["tag"]?.GetValue<string>() == "already-running" &&
                results[1]["result"]?["isError"]?.GetValue<bool>() == true && app.Calls == 1,
                "queued stale approval reached the application or replayed the original call");
            await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(factoryCalls == 6 && admission.ActiveCount == 0, "factory or queued callback leaked/replayed admission");
        }
        finally
        {
            app.Release.TrySetResult();
            await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ServiceInvocationEnvelopeAsync()
    {
        var gateway = new CortexOwnerMcpGateway();
        var dispatcher = new GatewayDispatcher();
        gateway.Bind(CortexMcpSettingsStore.Default with { Enabled = true, Owner = CortexMcpOwner.Service }, () => dispatcher);
        try
        {
            var first = Context('a', 7) with { TaskId = "first-task" };
            var second = Context('b', 7) with { TaskId = "second-task",
                Identity = new(new string('4', 32), new string('5', 32), new string('6', 32)) };
            foreach (var context in new[] { first, second })
            {
                var request = Request(7, context.TaskId!);
                var response = await gateway.ExecuteServiceRequestAsync(
                    Relay("invoke", Invocation(context, request).ToJsonString()), CancellationToken.None);
                Check(response.Ok && response.Data?.GetValueOrDefault("mcp") is JsonObject answer &&
                    answer["id"]?.GetValue<int>() == 7, "exact six-field private invocation was refused or lost its ID");
            }
            var observed = dispatcher.Contexts.ToArray();
            Check(observed.Length == 2 && observed[0] == first && observed[1] == second &&
                observed[0].Identity != observed[1].Identity && observed[0].SessionId != observed[1].SessionId,
                "private hop mixed identity, task, stream or session coordinates");

            var valid = Invocation(first, Request(7, "negative-envelope"));
            var invalid = new List<JsonObject>();
            void Mutate(Action<JsonObject> change)
            { var value = valid.DeepClone().AsObject(); change(value); invalid.Add(value); }
            Mutate(value => value.Remove("identity")); // Original five-field envelope is deliberately obsolete.
            Mutate(value => value["identity"] = null);
            Mutate(value => value["identity"] = "declared-name");
            Mutate(value => value["identity"]!.AsObject().Remove("authority"));
            Mutate(value => value["identity"]!["subject"] = new string('0', 32));
            Mutate(value => value["identity"]!["generation"] = "caller-label");
            Mutate(value => value["identity"]!["authority"] = 1);
            Mutate(value => value["identity"]!["fullControl"] = true);
            Mutate(value => value["requestId"] = "\"7\""); // Numeric and string IDs are distinct.
            Mutate(value => value["streamId"] = 0);
            var bodies = invalid.Select(value => value.ToJsonString()).ToList();
            bodies.Add(valid.ToJsonString().Insert(1, "\"identity\":" + IdentityJson(first.Identity!).ToJsonString() + ","));
            bodies.Add(valid.ToJsonString().Replace("\"identity\":{", "\"identity\":{\"subject\":\"" +
                new string('2', 32) + "\",", StringComparison.Ordinal));
            foreach (var body in bodies)
            {
                var refused = await gateway.ExecuteServiceRequestAsync(Relay("invoke", body), CancellationToken.None);
                Check(!refused.Ok && refused.Err == "invalid" && dispatcher.Contexts.Count == 2,
                    "malformed, old or forged private identity envelope reached dispatch");
            }

            var stringRequest = Request(7, "string-id");
            stringRequest["id"] = "7";
            var stringContext = first with { RequestId = "\"7\"" };
            var stringResult = await gateway.ExecuteServiceRequestAsync(
                Relay("invoke", Invocation(stringContext, stringRequest).ToJsonString()), CancellationToken.None);
            Check(stringResult.Ok && stringResult.Data?.GetValueOrDefault("mcp") is JsonObject mapped &&
                mapped["id"]?.GetValue<string>() == "7" && dispatcher.Contexts.Last() == stringContext,
                "valid typed string ID or its authenticated context changed on the private hop");

            foreach (var request in new[]
            {
                Relay("invoke", valid.ToJsonString()) with { System = true },
                Relay("invoke", valid.ToJsonString()) with { ConnectionAuthority = new Pegasus.Cortex.Product.CortexMcpConnectionAuthority(
                    new string('1', 32), new string('2', 32), new string('3', 32), 1) }
            })
            {
                try
                {
                    await gateway.ExecuteServiceRequestAsync(request, CancellationToken.None);
                    throw new InvalidOperationException("private relay accepted outer execution authority");
                }
                catch (AgentOpsException error) { Check(error.Code == "invalid", "unexpected private relay authority refusal"); }
            }
            Check(dispatcher.Contexts.Count == 3, "outer authority refusal invoked the product dispatcher");
        }
        finally
        {
            dispatcher.Release.TrySetResult();
            await gateway.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(dispatcher.Disposals == 1, "invocation gateway did not release its one borrowed dispatcher");
    }

    private static async Task NativeFaultClosesIngressBeforeDrainAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-native-stop-" + Guid.NewGuid().ToString("N")));
        var app = new HeldApplication();
        IMcpDispatcher? endpoint = null;
        CortexResidentServer? server = null;
        Task? native = null;
        try
        {
            var owner = LocalIdentity.Current();
            // Let the production factory create this NEW owned private entry;
            // pre-creating it would deliberately retain unrelated Temp inheritance.
            var registry = new CortexClientRegistry(root, owner);
            var admission = new UserHostAdmission();
            var stopped = false;
            server = new CortexResidentServer(app, registry, owner, "fixture", TextWriter.Null, admission,
                endpointsStopping: () =>
                {
                    stopped = true;
                    if (endpoint is not null) endpoint.DisposeAsync().GetAwaiter().GetResult();
                    app.Release.TrySetResult();
                });
            endpoint = server.CreateOwnerEndpointDispatcher(admission);
            var pending = endpoint.DispatchAsync(Context('d', 11), Request(11, "native-fault"), CancellationToken.None);
            await app.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Empty pipe name fails before creating any actual listener. Its
            // shutdown callback must run before waiting on our held callback.
            native = server.RunNativeAsync(string.Empty, CancellationToken.None);
            try { await native.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("invalid listener accepted"); }
            catch (ArgumentException) { }
            Check(stopped && (await pending).ContainsKey("result"), "native failure closes ingress before its common drain");
            var refused = await endpoint.DispatchAsync(Context('e', 12), Request(12, "late"), CancellationToken.None);
            Check(refused["error"]!["data"]!["outcome"]!.GetValue<string>() == "NotDispatched" && app.Calls == 1,
                "failed resident accepts no new shared callback");
            Check(admission.Accepting, "semantic failure does not close independent user execution admission");
        }
        finally
        {
            app.Release.TrySetResult();
            if (endpoint is not null) await endpoint.DisposeAsync();
            if (native is not null) try { await native.WaitAsync(TimeSpan.FromSeconds(5)); } catch (ArgumentException) { }
            if (server is not null) await server.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cortex-native-stop-", StringComparison.Ordinal) ||
                Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("owned native-stop fixture cleanup changed");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static OperationRequest Relay(string kind, string body) => new("_cortex_shared_mcp",
        new Dictionary<string, string> { ["kind"] = kind }, Guid.NewGuid().ToString("N"), Body: body);
    private static McpRequestContext Context(char identity, int id) =>
        new("cortex", new string(identity, 32), identity, null, id.ToString()) { Identity = SyntheticIdentity() };
    private static McpAuthenticatedIdentity SyntheticIdentity() => new(new string('1', 32), new string('2', 32), new string('3', 32));
    private static JsonObject IdentityJson(McpAuthenticatedIdentity identity) => new()
    { ["authority"] = identity.Authority, ["subject"] = identity.Subject, ["generation"] = identity.Generation };
    private static JsonObject Invocation(McpRequestContext context, JsonObject request) => new()
    {
        ["sessionId"] = context.SessionId, ["streamId"] = context.StreamId, ["taskId"] = context.TaskId,
        ["requestId"] = context.RequestId, ["identity"] = IdentityJson(context.Identity!), ["request"] = request.DeepClone()
    };
    private static JsonObject Request(int id, string tag) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = "fixture_echo", ["arguments"] = new JsonObject { ["tag"] = tag } }
    };
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }

    private sealed class HeldApplication : ICortexMcpProtocolApplication
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Expected = 1;
        internal int Calls;
        private int entered;
        private bool ignoreCancellation;
        internal void Reset(bool ignoreCancellation)
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Expected = 1; entered = 0; this.ignoreCancellation = ignoreCancellation;
        }
        public JsonArray ListTools() => new(new JsonObject { ["name"] = "fixture_echo" });
        public async ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Interlocked.Increment(ref entered) == Expected) Entered.TrySetResult();
            if (ignoreCancellation) await Release.Task; else await Release.Task.WaitAsync(cancellationToken);
            return new JsonObject { ["isError"] = false, ["structuredContent"] = new JsonObject { ["tag"] = arguments!.Value.GetProperty("tag").GetString() } };
        }
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken cancellationToken) => ValueTask.FromResult<JsonObject?>(null);
    }

    private sealed class GatewayDispatcher : IMcpDispatcher
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<McpRequestContext> Contexts = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public async Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task; return new JsonObject { ["fixture"] = true }; }
        public Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            return Task.FromResult(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone(), ["result"] = new JsonObject() });
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
