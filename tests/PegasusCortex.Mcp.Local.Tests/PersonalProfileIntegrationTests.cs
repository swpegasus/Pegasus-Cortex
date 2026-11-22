using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Product;

internal static class PersonalProfileIntegrationTests
{
    public static async Task OptionalProfilePipeAndSqliteAsync()
    {
        var owner = LocalIdentity.Current();
        var name = "cortex-profile-fixture-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new CortexPersonalProfileStatusV1(1, "not_loaded", null,
            Guid.NewGuid().ToString("N"), "profile_unloaded");
        var server = ServeAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var client = new CortexPersonalProfileClient(name, owner, TimeSpan.FromSeconds(1));
            var database = Path.Combine(root, "cortex-v2.db");
            // Seed only task-owned SQLite data; this unmanaged composition is an
            // explicit fixture, not a production path or a live user database.
            await using (var seed = await CortexMcpLocalComposition.CreateGeneralAsync(
                database, TimeProvider.System, CancellationToken.None))
            {
                foreach (var ns in new[] { "knowledge.shared", "knowledge.user" })
                    RequireOk(await CallAsync(seed, "cortex_knowledge", new
                    {
                        @namespace = ns, action = "create", recordId = "profile-fixture",
                        idempotencyKey = "seed-" + ns, kind = "preference", tier = "explicit",
                        title = "marker profile knowledge", content = "exact marker\r\nΩ\t" + ns,
                        tags = new[] { "fixture" }, provenance = new { source = "user" }
                    }));
                RequireOk(await CallAsync(seed, "cortex_task", new
                {
                    @namespace = "project.fixture", action = "create",
                    taskId = "profile-task", title = "marker core task", content = "core state",
                    idempotencyKey = "seed-task"
                }));
            }

            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                database, TimeProvider.System, CancellationToken.None, client);
            var status = await CallAsync(composition, "cortex_status", new { });
            RequireOk(status);
            LocalAssert.True(Body(status)["personalProfile"]?["state"]?.GetValue<string>() == "not_loaded",
                "Status did not distinguish core availability from personal profile state.");
            var mixed = await SearchAsync(composition);
            RequireOk(mixed);
            LocalAssert.True(Body(mixed)["items"]!.AsArray().All(item => item!["content"] is null),
                "Search unexpectedly returned an exact body field instead of a preview.");
            LocalAssert.True(Body(mixed)["effectiveNamespaces"]!.AsArray()
                    .All(item => item!.GetValue<string>() != "knowledge.user"),
                "Unavailable personal namespace was reported as effective.");
            LocalAssert.True(Body(mixed)["items"]!.AsArray().All(item =>
                    item!["namespace"]?.GetValue<string>() != "knowledge.user"),
                "Unloaded personal knowledge leaked into mixed search.");
            var unavailable = await ReadPersonalAsync(composition);
            LocalAssert.True(unavailable["isError"]!.GetValue<bool>() &&
                    unavailable.ToJsonString().Contains("personal_profile_not_loaded", StringComparison.Ordinal),
                "Exact personal read did not fail with its bounded availability code.");
            RequireOk(await CallAsync(composition, "cortex_finish", new
            {
                @namespace = "project.fixture", taskId = "profile-task", expectedRevision = 1,
                taskAction = "update", checkpoint = "core checkpoint while personal layer is unloaded",
                evidence = "isolated SQLite/pipe integration fixture", knowledge = Array.Empty<object>(),
                idempotencyKey = "finish-unloaded", taskContent = "core work continues"
            }));

            Volatile.Write(ref current, new(1, "loaded", "me", Guid.NewGuid().ToString("N"), null));
            var loaded = await ReadPersonalAsync(composition);
            RequireOk(loaded);
            LocalAssert.True(Body(loaded)["record"]?["content"]?.GetValue<string>() ==
                    "exact marker\r\nΩ\tknowledge.user", "Recovered personal read lost exact content.");
            var active = await SearchAsync(composition);
            RequireOk(active);
            LocalAssert.True(Body(active)["items"]!.AsArray().Any(item =>
                    item!["namespace"]?.GetValue<string>() == "knowledge.user"),
                "Same MCP composition did not recover personal search after tray load.");

            Volatile.Write(ref current, new(1, "not_loaded", null, Guid.NewGuid().ToString("N"), "ui_unavailable"));
            LocalAssert.True((await ReadPersonalAsync(composition))["isError"]!.GetValue<bool>(),
                "Tray failure did not unload personal reads.");
            var stillCore = await CallAsync(composition, "cortex_task", new
                { @namespace = "project.fixture", action = "get", taskId = "profile-task" });
            RequireOk(stillCore);

            var missing = new CortexPersonalProfileClient(name + "-absent", owner,
                TimeSpan.FromMilliseconds(100));
            var clock = Stopwatch.StartNew();
            var missingState = await missing.GetAsync(CancellationToken.None);
            LocalAssert.True(missingState.State == "not_loaded" && missingState.Retryable &&
                    !missingState.IndicatesMissingData && !missingState.RemapRequired,
                "Absent tray was not safely optional.");
            LocalAssert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "Absent tray lookup exceeded deadline.");

            var recoveredName = "cortex-profile-recovery-" + Guid.NewGuid().ToString("N");
            var recovering = new CortexPersonalProfileClient(
                recoveredName,
                owner,
                TimeSpan.FromMilliseconds(100));
            var beforeExplicitStart = await recovering.GetAsync(CancellationToken.None);
            LocalAssert.True(beforeExplicitStart.State == "not_loaded" && beforeExplicitStart.Retryable,
                "An absent owner did not remain safely unavailable.");
            // This fixture start is explicit and outside the client lookup.
            await using var recoveredPipe = LocalPipeFactory.Create(recoveredName, owner.Sid);
            var recoveredServer = Task.Run(async () =>
            {
                await recoveredPipe.WaitForConnectionAsync(stop.Token);
                await CortexPersonalProfilePipe.ReadRequestAsync(recoveredPipe, stop.Token);
                await CortexPersonalProfilePipe.WriteResponseAsync(recoveredPipe,
                    new(1, "loaded", "me", Guid.NewGuid().ToString("N"), null), stop.Token);
            });
            var recoveredState = await recovering.GetAsync(CancellationToken.None);
            LocalAssert.True(recoveredState.State == "loaded" &&
                    recoveredState.Profile == "me" && !recoveredState.Retryable,
                "A fresh read did not observe the explicitly started profile endpoint.");
            await recoveredServer;

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await missing.GetAsync(cancelled.Token);
                throw new InvalidOperationException("Caller cancellation was swallowed.");
            }
            catch (OperationCanceledException) { }
            var wrongSession = new CortexPersonalProfileClient(name,
                owner with { Session = owner.Session + 1 }, TimeSpan.FromSeconds(1));
            LocalAssert.True((await wrongSession.GetAsync(CancellationToken.None)).IssueCode == "profile_peer_mismatch",
                "A different Windows session was accepted as the profile owner.");
            await ProbeReplyAsync(owner, async (pipe, token) =>
            {
                // 1025-byte frame is rejected before any payload allocation/read.
                await pipe.WriteAsync(new byte[] { 1, 4, 0, 0 }, token);
                await pipe.FlushAsync(token);
            }, "profile_protocol_invalid");
            await ProbeReplyAsync(owner, (_, token) => Task.Delay(TimeSpan.FromSeconds(5), token),
                "profile_pipe_unavailable");
        }
        finally
        {
            stop.Cancel();
            try { await server; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            // The exact fresh non-reparse fixture is the only cleanup target.
            var directory = new DirectoryInfo(root);
            if (directory.Exists && directory.LinkTarget is null &&
                (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                directory.Name == name && directory.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                directory.Delete(recursive: true);
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                await using var pipe = LocalPipeFactory.Create(name, owner.Sid);
                ready.TrySetResult();
                await pipe.WaitForConnectionAsync(stop.Token);
                try
                {
                    if (!LocalIdentity.Capture(pipe).SameIdentity(owner)) continue;
                    await CortexPersonalProfilePipe.ReadRequestAsync(pipe, stop.Token);
                    await CortexPersonalProfilePipe.WriteResponseAsync(pipe, Volatile.Read(ref current), stop.Token);
                }
                catch (IOException) { /* Wrong-peer client closes before submitting a request. */ }
            }
        }
    }

    private static ValueTask<JsonObject> SearchAsync(CortexMcpLocalComposition composition) =>
        CallAsync(composition, "cortex_search", new
        {
            namespaces = new[] { "knowledge.shared", "project.fixture", "knowledge.user" },
            text = "marker", maxUtf8Bytes = 4096, format = "detailed"
        });

    private static ValueTask<JsonObject> ReadPersonalAsync(CortexMcpLocalComposition composition) =>
        CallAsync(composition, "cortex_read", new
            { @namespace = "knowledge.user", recordType = "knowledge", recordId = "profile-fixture" });

    private static JsonObject Body(JsonObject response) => response["structuredContent"]!.AsObject();
    private static void RequireOk(JsonObject response) => LocalAssert.True(
        response["isError"]?.GetValue<bool>() == false, "Fixture MCP call failed: " + response.ToJsonString());
    private static async ValueTask<JsonObject> CallAsync(CortexMcpLocalComposition composition, string operation, object args)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await composition.Application.CallToolAsync(operation, document.RootElement, CancellationToken.None);
    }

    private static async Task ProbeReplyAsync(LocalPeer owner,
        Func<NamedPipeServerStream, CancellationToken, Task> reply, string expectedIssue)
    {
        var name = "cortex-profile-reply-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        await using var pipe = LocalPipeFactory.Create(name, owner.Sid);
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(stop.Token);
            await CortexPersonalProfilePipe.ReadRequestAsync(pipe, stop.Token);
            await reply(pipe, stop.Token);
        });
        try
        {
            var client = new CortexPersonalProfileClient(name, owner, TimeSpan.FromMilliseconds(250));
            var state = await client.GetAsync(CancellationToken.None);
            LocalAssert.True(state.State == "not_loaded" && state.IssueCode == expectedIssue,
                "Malformed or stalled optional reply was not bounded safely.");
        }
        finally
        {
            stop.Cancel();
            try { await server; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
