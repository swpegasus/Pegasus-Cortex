using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Internal;
using Pegasus.AgentOps.Protocol;

internal static class UserApplicationLifecycleTests
{
    private const string Configuration = "appfixture";

    internal static async Task OwnedTreesAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        await using var manager = new UserApplicationManager(fixture.Tools);
        var first = await manager.StartAsync(Configuration, "first", ["--app-fixture-root-wait"], fixture.Work);
        var firstRoot = await fixture.ProcessAsync(fixture.Work, "root.json");
        var firstChild = await fixture.ProcessAsync(fixture.Work, "child.json");
        var firstObserved = await WaitInventoryStatusAsync(manager, "first", "running", [firstRoot, firstChild]);
        var running = firstObserved.Status;
        Check(Number(running, "pid") == firstRoot.Id, "The running snapshot lost the exact admitted root identity.");
        RequireReceipt(running, "first");
        Check(!running.ContainsKey("exit"), "A running application emitted an unknown exit code.");

        var repeated = await manager.StartAsync(Configuration, "first", ["--app-fixture-root-wait"], fixture.Work);
        Check(Aid(first) == Aid(repeated) && fixture.LaunchCount(fixture.Work) == 1, "Repeated start duplicated an active application.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "first", ["--app-fixture-exit"], fixture.Work));
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "first", ["--app-fixture-root-wait"], fixture.Work, expectedAid: Aid(first)));
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "first", ["--app-fixture-root-wait"], fixture.Work, expectedAid: Guid.NewGuid().ToString("N")));
        await RejectAsync("not_found", () => manager.StopAsync(Guid.NewGuid().ToString("N")));
        Check(fixture.LaunchCount(fixture.Work) == 1 && !firstChild.HasExited, "A rejected lifecycle request changed the owned tree.");

        var otherWork = fixture.NewWork("other");
        var other = await manager.StartAsync(Configuration, "other", ["--app-fixture-root-wait"], otherWork);
        var otherRoot = await fixture.ProcessAsync(otherWork, "root.json");
        var otherChild = await fixture.ProcessAsync(otherWork, "child.json");
        var otherObserved = await WaitInventoryStatusAsync(manager, "other", "running", [otherRoot, otherChild]);
        Check(!firstObserved.Inventory.Members.Select(member => member.Pid).Intersect(otherObserved.Inventory.Members.Select(member => member.Pid)).Any(),
            "Two independently owned applications share an exact job member.");
        var stopped = await manager.StopAsync(Aid(first));
        await RequireExitedAsync(firstRoot, firstChild);
        await RequireMembersGoneAsync(firstObserved.Inventory.Members);
        Check(State(stopped) == "stopped" && Number(stopped, "processes") == 0, "Stop did not report the terminal owned tree.");
        Check(!otherRoot.HasExited && !otherChild.HasExited, "Stopping one aid affected a different owned application.");
        await WaitInventoryStatusAsync(manager, "other", "running", [otherRoot, otherChild]);
        Check(Aid(await manager.StopAsync(Aid(first))) == Aid(first), "Repeated exact stop was not idempotent.");
        await manager.StopAsync(Aid(other));
        await RequireExitedAsync(otherRoot, otherChild);
        await RequireMembersGoneAsync(otherObserved.Inventory.Members);
    }

    internal static async Task RootExitRestartAndRetryAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        await using var manager = new UserApplicationManager(fixture.Tools);
        var original = await manager.StartAsync(Configuration, "workers", ["--app-fixture-root"], fixture.Work, rid: "first-start");
        var child = await fixture.ProcessAsync(fixture.Work, "child.json");
        var retainedObserved = await WaitInventoryStatusAsync(manager, "workers", "root_exited_workers_running", [child],
            absent: [Number(original, "pid")]);
        var retained = retainedObserved.Status;
        Check(Number(retained, "exit") == 0 && !child.HasExited,
            "Root exit disposed its job or hid the surviving worker.");
        Check(Aid(original) == Aid(retained), "Root exit changed application generation identity.");
        var replay = await manager.StartAsync(Configuration, "workers", ["--app-fixture-root"], fixture.Work);
        Check(Aid(replay) == Aid(original) && fixture.LaunchCount(fixture.Work) == 1, "Root exit made duplicate start launch again.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "workers", ["--app-fixture-root"], fixture.Work, expectedAid: Aid(original)));
        await manager.StopAsync(Aid(original));
        await RequireExitedAsync(child);
        await RequireMembersGoneAsync(retainedObserved.Inventory.Members);

        var successor = await manager.StartAsync(Configuration, "workers", ["--app-fixture-root-wait"], fixture.Work,
            expectedAid: Aid(original), rid: "restart-once");
        var successorChild = await fixture.ProcessAsync(fixture.Work, "child.json", differentPid: child.Id);
        var successorRoot = await fixture.ProcessAsync(fixture.Work, "root.json");
        var successorObserved = await WaitInventoryStatusAsync(manager, "workers", "running", [successorRoot, successorChild]);
        Check(Aid(successor) != Aid(original) && fixture.LaunchCount(fixture.Work) == 2, "Terminal restart did not create exactly one new generation.");
        var retried = await manager.StartAsync(Configuration, "workers", ["--app-fixture-root-wait"], fixture.Work,
            expectedAid: Aid(original), rid: "restart-once");
        Check(Aid(retried) == Aid(successor) && fixture.LaunchCount(fixture.Work) == 2, "Retry of old aid and same rid created another successor.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "workers", ["--app-fixture-root"], fixture.Work,
            expectedAid: Aid(original), rid: "restart-once"));
        await manager.StopAsync(Aid(original));
        Check(!successorChild.HasExited, "An old terminal aid stopped its successor.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "workers", ["--app-fixture-root"], fixture.Work,
            expectedAid: Aid(original), rid: "different-restart"));
        await manager.StopAsync(Aid(successor));
        await RequireExitedAsync(successorRoot, successorChild);
        await RequireMembersGoneAsync(successorObserved.Inventory.Members);

        var fastWork = fixture.NewWork("fast");
        var fast = await manager.StartAsync(Configuration, "fast", ["--app-fixture-exit"], fastWork);
        var exited = await WaitStatusAsync(manager, "fast", value => State(value) == "exited");
        Check(Number(exited, "processes") == 0 && Number(exited, "exit") == 0, "Root-only natural exit did not become terminal.");
        RequireReceipt(exited, "fast");
        await fixture.RegisterAsync("appfixtureupdated", ToolExecutionMode.User);
        var fastNext = await manager.StartAsync("appfixtureupdated", "fast", ["--app-fixture-exit"], fastWork,
            expectedAid: Aid(fast), rid: "fast-restart");
        await WaitStatusAsync(manager, "fast", value => State(value) == "exited");
        Check(Aid(fastNext) != Aid(fast) && fixture.LaunchCount(fastWork) == 2, "Natural-exit restart lost generation identity.");
    }

    internal static async Task OwnerDisposalAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        var manager = new UserApplicationManager(fixture.Tools);
        try
        {
            await manager.StartAsync(Configuration, "dispose", ["--app-fixture-root-wait"], fixture.Work);
            var root = await fixture.ProcessAsync(fixture.Work, "root.json");
            var child = await fixture.ProcessAsync(fixture.Work, "child.json");
            var observed = await WaitInventoryStatusAsync(manager, "dispose", "running", [root, child]);
            await manager.DisposeAsync();
            await RequireExitedAsync(root, child);
            await RequireMembersGoneAsync(observed.Inventory.Members);
        }
        finally { await manager.DisposeAsync(); }
    }

    internal static async Task OrdinaryRunCleanupAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        var invocation = fixture.Tools.PrepareTool(Configuration, ["--app-fixture-root"], cwd: fixture.Work);
        var result = await fixture.Tools.RunAsync(invocation).WaitAsync(TimeSpan.FromSeconds(12));
        Check(result.ExitCode == 0, "Ordinary fixed run failed.");
        using var identity = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Work, "child.json")));
        var pid = identity.RootElement.GetProperty("pid").GetInt32();
        var started = identity.RootElement.GetProperty("start").GetString()!;
        await WaitUntilAsync(() => !IsExactProcessAlive(pid, started), "Ordinary run retained an owned child after root exit.");
    }

    internal static async Task UserBoundaryAndSurfaceAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        Check(UserApplicationManager.MaxActiveApplications == 16 && UserApplicationManager.MaxRetainedApplications == 128,
            "Application capacity contract changed.");
        await fixture.RegisterAsync("systemfixture", ToolExecutionMode.System);
        await using (var manager = new UserApplicationManager(fixture.Tools))
        {
            await RejectAsync("auth", () => manager.StartAsync("systemfixture", "system", ["--app-fixture-exit"], fixture.Work));
            await RejectAsync("invalid", () => manager.StartAsync(Configuration, "unsafe/name", ["--app-fixture-exit"], fixture.Work));
            await RejectAsync("not_found", () => manager.StatusAsync("unknown"));
        }
        await using (var disabled = new AgentOpsEngine(fixture.State))
        {
            var denied = await disabled.ExecuteAsync(new OperationRequest("app",
                new Dictionary<string, string> { ["op"] = "start", ["cfg"] = Configuration, ["name"] = "disabled" }, Cwd: fixture.Work));
            Check(!denied.Ok && denied.Err == "user_dispatch_required", "A default engine admitted a persistent application.");
        }
        await using (var enabled = new AgentOpsEngine(fixture.Directory("enabled-state"), enableUserApplications: true))
        {
            var denied = await enabled.ExecuteAsync(new OperationRequest("app",
                new Dictionary<string, string> { ["op"] = "start", ["cfg"] = Configuration, ["name"] = "root" }, Root: "AB12", Cwd: fixture.Work));
            Check(!denied.Ok && denied.Err == "auth", "A root request entered User application management.");
        }
        Check(fixture.LaunchCount(fixture.Work) == 0, "A rejected boundary request launched a process.");
        await UserOnlyRoutingAsync();
        await ParserAndMcpParityAsync(fixture.Work);
    }

    internal static async Task EngineReplayAndRecoveryAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        OperationRequest restart;
        string successorAid;
        await using (var engine = new AgentOpsEngine(fixture.State, enableUserApplications: true))
        {
            var firstRequest = StartRequest("--app-fixture-exit", "engine-first");
            var firstPending = engine.ExecuteAsync(firstRequest);
            var retryPending = engine.ExecuteAsync(firstRequest);
            var conflictPending = engine.ExecuteAsync(StartRequest("--app-fixture-root-wait", "engine-first"));
            var attempts = await Task.WhenAll(firstPending, retryPending, conflictPending);
            var first = attempts[0];
            var firstData = RequireSuccess(first);
            await WaitEngineStateAsync(engine, "engine", "exited");
            Check(first.Jid == attempts[1].Jid && Aid(firstData) == Aid(RequireSuccess(attempts[1])) &&
                fixture.LaunchCount(fixture.Work) == 1,
                "Simultaneous identical engine RID starts did not coalesce to one launch and receipt.");
            Check(!attempts[2].Ok && attempts[2].Err == "precondition",
                "A simultaneous conflicting RID start inherited another launch's receipt.");
            restart = StartRequest("--app-fixture-root-wait", "engine-restart", Aid(firstData));
            var successor = await engine.ExecuteAsync(restart);
            var successorData = RequireSuccess(successor);
            successorAid = Aid(successorData);
            var root = await fixture.ProcessAsync(fixture.Work, "root.json", differentPid: Number(firstData, "pid"));
            var child = await fixture.ProcessAsync(fixture.Work, "child.json");
            var replay = await engine.ExecuteAsync(restart);
            Check(replay.Jid == successor.Jid && Aid(RequireSuccess(replay)) == successorAid && fixture.LaunchCount(fixture.Work) == 2,
                "Exact engine RID restart replay duplicated or rebound the application.");
            var changedArguments = await engine.ExecuteAsync(StartRequest("--app-fixture-exit", "engine-restart", Aid(firstData)));
            Check(!changedArguments.Ok && changedArguments.Err == "precondition", "A successful restart RID accepted changed launch arguments.");
            var changedAid = await engine.ExecuteAsync(StartRequest("--app-fixture-root-wait", "engine-restart", Guid.NewGuid().ToString("N")));
            Check(!changedAid.Ok && changedAid.Err == "precondition", "A successful restart RID accepted a different previous generation.");
            Check(fixture.LaunchCount(fixture.Work) == 2 && !root.HasExited && !child.HasExited,
                "Conflicting engine replay changed the owned application.");
            RequireSuccess(await engine.ExecuteAsync(new OperationRequest("app",
                new Dictionary<string, string> { ["op"] = "stop", ["aid"] = successorAid }, "engine-stop")));
            await RequireExitedAsync(root, child);
        }
        await using (var recovered = new AgentOpsEngine(fixture.State, enableUserApplications: true))
        {
            var lost = await recovered.ExecuteAsync(restart);
            Check(!lost.Ok && lost.Err == "app_owner_lost" && fixture.LaunchCount(fixture.Work) == 2,
                "A persisted successful start was adopted or relaunched by a replacement owner.");
        }

        var queuedWork = fixture.NewWork("queued-work");
        DurableJob queued;
        using (var store = new SqliteJobStore(fixture.State))
        {
            var request = new OperationRequest("app", new Dictionary<string, string>
            {
                ["op"] = "start", ["cfg"] = Configuration, ["name"] = "queued", ["arg0"] = "--app-fixture-exit"
            }, "queued-application", Cwd: queuedWork);
            queued = store.Claim(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid!, "queued", "app_start"), request, true);
        }
        await using (var recovered = new AgentOpsEngine(fixture.State, enableUserApplications: true))
        {
            // An ordinary queued mutation is a FIFO barrier after constructor recovery.
            var barrier = await recovered.ExecuteAsync(new OperationRequest("mkdir", new Dictionary<string, string>
            {
                ["dst"] = Path.Combine(fixture.Directory("barrier-parent"), "barrier")
            }, "recovery-barrier"));
            Check(barrier.Ok && fixture.LaunchCount(queuedWork) == 0, "Owner recovery automatically launched a persisted queued application.");
        }
        using (var store = new SqliteJobStore(fixture.State))
        {
            var quarantined = store.Find(queued.Receipt.Jid)!;
            Check(!quarantined.Receipt.Ok && quarantined.Receipt.State == "recovery_required" &&
                quarantined.Receipt.Err == "recovery_required" && quarantined.Request is null,
                "Persisted queued application was not quarantined without replayable launch input.");
        }

        OperationRequest StartRequest(string argument, string rid, string? aid = null)
        {
            var values = new Dictionary<string, string>
            {
                ["op"] = "start", ["cfg"] = Configuration, ["name"] = "engine", ["arg0"] = argument
            };
            if (aid is not null) values["aid"] = aid;
            return new OperationRequest("app", values, rid, Cwd: fixture.Work);
        }
    }

    internal static async Task MaintenanceEngineReplayAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        await using var engine = new AgentOpsEngine(fixture.State, enableUserApplications: true);
        OperationRequest Start(string rid) => new("app", new Dictionary<string, string>
        {
            ["op"] = "start", ["cfg"] = Configuration, ["name"] = "maintenance", ["arg0"] = "--app-fixture-exit"
        }, rid, Cwd: fixture.Work);
        async Task<IReadOnlyDictionary<string, object?>> Inventory()
            => RequireSuccess(await engine.ExecuteAsync(new OperationRequest("app",
                new Dictionary<string, string> { ["op"] = "inventory", ["max"] = "8" }, "read-snapshot", Cwd: fixture.Work)));
        static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(IReadOnlyDictionary<string, object?> data)
            => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)data["items"]!;
        async Task<IReadOnlyDictionary<string, object?>> Releasable(string aid)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var data = await Inventory();
                if (Rows(data).Any(row => Aid(row) == aid && Convert.ToBoolean(row["releasable"]))) return data;
                await Task.Delay(10);
            } while (DateTime.UtcNow < until);
            throw new InvalidOperationException("Exact fixture retirement did not complete.");
        }
        OperationRequest Release(string aid, string epoch, string? rid) => new("app",
            new Dictionary<string, string> { ["op"] = "release", ["aid"] = aid, ["epoch"] = epoch }, rid, Cwd: fixture.Work);
        var start = Start("maintenance-first");
        var first = RequireSuccess(await engine.ExecuteAsync(start));
        await WaitEngineStateAsync(engine, "maintenance", "exited");
        var snapshot = await Releasable(Aid(first));
        var epoch = snapshot["epoch"]!.ToString()!;
        var wrongScope = await engine.ExecuteAsync(Release(Aid(first), epoch, "invalid-workspace") with { Ws = "other" });
        Check(!wrongScope.Ok && wrongScope.Err == "invalid", "Release admitted workspace scoping.");
        var protectedRequest = await engine.ExecuteAsync(Release(Aid(first), epoch, "invalid-root") with { Root = "TEST" });
        Check(!protectedRequest.Ok && protectedRequest.Err == "auth", "Release admitted the privileged execution route.");
        int before;
        using (var store = new SqliteJobStore(fixture.State)) before = store.ListRecent(100).Count;
        await Inventory(); await Inventory();
        using (var store = new SqliteJobStore(fixture.State))
            Check(store.ListRecent(100).Count == before, "Read-only inventory created durable history.");
        var missingRid = await engine.ExecuteAsync(Release(Aid(first), epoch, null));
        Check(!missingRid.Ok && missingRid.Err == "invalid", "Raw engine accepted release without explicit RID.");
        var release = Release(Aid(first), epoch, "release-first");
        var released = await engine.ExecuteAsync(release);
        Check(released.Ok && released.Data!["state"]?.ToString() == "released", "Exact retired fixture was not released.");
        var duplicate = await engine.ExecuteAsync(release with { Cwd = fixture.State });
        Check(duplicate.Ok && duplicate.Jid == released.Jid, "Exact release RID did not replay its committed receipt.");
        var oldStart = await engine.ExecuteAsync(start);
        Check(!oldStart.Ok && oldStart.Err == "app_owner_lost" && fixture.LaunchCount(fixture.Work) == 1,
            "Released start RID implicitly launched or adopted a generation.");
        var second = RequireSuccess(await engine.ExecuteAsync(Start("maintenance-second")));
        Check(Aid(first) != Aid(second), "Name reuse recycled the released aid.");
        // Admission precedes the FakeTool startup ledger; root exit proves that write completed.
        await WaitEngineStateAsync(engine, "maintenance", "exited");
        Check(fixture.LaunchCount(fixture.Work) == 2, "The admitted second fixture did not finish its startup ledger.");
        duplicate = await engine.ExecuteAsync(release);
        Check(duplicate.Ok && duplicate.Jid == released.Jid &&
            Rows(await Inventory()).Any(row => Aid(row) == Aid(second)),
            "Old release RID removed a replacement generation.");
        var drift = await engine.ExecuteAsync(Release(Aid(second), epoch, "release-first"));
        Check(!drift.Ok && drift.Err == "precondition",
            $"Release RID did not reject changed aid: ok={drift.Ok}, err={drift.Err}.");
        Check(fixture.LaunchCount(fixture.Work) == 2, "Rejected release RID changed the observed startup ledger.");
        var wrongEpoch = Release(Aid(second), Guid.NewGuid().ToString("N"), "wrong-epoch");
        var rejected = await engine.ExecuteAsync(wrongEpoch);
        Check(!rejected.Ok && rejected.Err == "precondition", "Wrong host epoch reached reclamation.");
        var changedFailure = await engine.ExecuteAsync(Release(Aid(second), epoch, "wrong-epoch"));
        Check(!changedFailure.Ok && changedFailure.Err == "precondition" &&
            Rows(await Inventory()).Any(row => Aid(row) == Aid(second)),
            "A failed release RID accepted corrected arguments as a new effect.");
        await Releasable(Aid(second));
        await engine.ExecuteAsync(Release(Aid(second), epoch, "release-second"));
        Check(Convert.ToInt32((await Inventory())["retained"]) == 0, "Exact cleanup left a fixture retention record.");
    }

    internal static async Task MalformedFramesAndUnconfirmedStopAsync()
    {
        await using var fixture = await ApplicationFixture.CreateAsync();
        await using (var failingLaunch = new UserApplicationManager(fixture.Tools,
            (_, _) => Task.FromException<ApplicationProcessHost>(new InvalidDataException("app_host_protocol"))))
        {
            await RejectAsync("app_launch_failed", () => failingLaunch.StartAsync(Configuration, "bad-start", [], fixture.Work));
            await RejectAsync("not_found", () => failingLaunch.StatusAsync("bad-start"));
        }

        using var malformed = await FakeApplicationHandle.CreateAsync(malformed: true);
        using var successorHandle = await FakeApplicationHandle.CreateAsync(malformed: false);
        var launches = 0;
        await using var manager = new UserApplicationManager(fixture.Tools, (_, _) =>
        {
            var handle = ++launches == 1 ? malformed : successorHandle;
            return Task.FromResult(new ApplicationProcessHost(handle, 123, DateTime.UtcNow.ToString("O"),
                stopTimeout: TimeSpan.FromMilliseconds(5)));
        });
        var first = await manager.StartAsync(Configuration, "malformed", [], fixture.Work);
        var damaged = await WaitStatusAsync(manager, "malformed", value => value.GetValueOrDefault("issue")?.ToString() == "app_host_protocol");
        Check(State(damaged) == "stopping" && Number(damaged, "processes") == 1 && !damaged.ContainsKey("exit"),
            "Malformed helper frame fabricated a terminal state, zero processes, or an exit code.");
        Check(malformed.TerminationRequests > 0 && !malformed.IsDisposed,
            "Malformed helper frame did not request cleanup or discarded queryable accounting before quiescence.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "malformed", [], fixture.Work,
            expectedAid: Aid(first), rid: "too-soon"));

        try
        {
            await manager.StopAsync(Aid(first));
            throw new InvalidOperationException("Expected application rejection: app_stop_incomplete");
        }
        catch (AgentOpsException exception) when (exception.Code == "app_stop_incomplete")
        {
            Check(exception.Data[UserApplicationManager.StopEvidenceKey] is IReadOnlyDictionary<string, object?> evidence &&
                Aid(evidence) == Aid(first) && State(evidence) == "stopping" && Number(evidence, "processes") == 1 &&
                Text(evidence, "issue") == "app_stop_incomplete" && !evidence.ContainsKey("exit"),
                "Incomplete stop did not retain truthful evidence for its exact generation.");
        }
        var unresolved = await manager.StatusAsync("malformed");
        Check(State(unresolved) == "stopping" && Text(unresolved, "issue") == "app_stop_incomplete" &&
            Number(unresolved, "processes") == 1 && !unresolved.ContainsKey("exit") && !malformed.IsDisposed,
            "Unconfirmed stop was reported as exited/zero or released its accounting handle.");
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "malformed", [], fixture.Work,
            expectedAid: Aid(first), rid: "still-too-soon"));
        Check(launches == 1, "An unresolved application admitted a replacement generation.");

        malformed.ConfirmQuiescence();
        var confirmed = await manager.StatusAsync("malformed");
        Check(State(confirmed) is "stopped" or "exited" && Number(confirmed, "processes") == 0,
            "Observed quiescence did not resolve the application lifecycle.");
        var next = await manager.StartAsync(Configuration, "malformed", [], fixture.Work,
            expectedAid: Aid(first), rid: "confirmed-restart");
        Check(Aid(next) != Aid(first) && launches == 2, "Confirmed quiescence did not permit exactly one explicit restart.");
        successorHandle.ConfirmQuiescence();
        await manager.StopAsync(Aid(next));
        await UnconfirmedOwnerDisposalAsync(fixture);
        Check(fixture.LaunchCount(fixture.Work) == 0, "Deterministic protocol fixture unexpectedly launched a target process.");
    }

    private static async Task UnconfirmedOwnerDisposalAsync(ApplicationFixture fixture)
    {
        using var firstHandle = await FakeApplicationHandle.CreateAsync(malformed: false);
        using var secondHandle = await FakeApplicationHandle.CreateAsync(malformed: false);
        var handles = new[] { firstHandle, secondHandle };
        var launches = 0;
        await using var owner = new UserApplicationManager(fixture.Tools, (_, _) =>
        {
            var index = launches++;
            return Task.FromResult(new ApplicationProcessHost(handles[index], 456 + index, DateTime.UtcNow.ToString("O"),
                stopTimeout: TimeSpan.FromMilliseconds(5)));
        });
        var first = await owner.StartAsync(Configuration, "dispose-one", [], fixture.Work);
        var second = await owner.StartAsync(Configuration, "dispose-two", [], fixture.Work);
        var generations = new Dictionary<string, string> { [Aid(first)] = "dispose-one", [Aid(second)] = "dispose-two" };
        var failed = false;
        try { await owner.DisposeAsync(); }
        catch (AgentOpsException exception) when (exception.Code == "app_stop_incomplete")
        {
            failed = true;
            Check(exception.Data[UserApplicationManager.OperationEvidenceKey] is IReadOnlyDictionary<string, object?> evidence &&
                generations.TryGetValue(Aid(evidence), out var name) && Text(evidence, "name") == name &&
                State(evidence) == "stopping" && Text(evidence, "issue") == "app_stop_incomplete" && !evidence.ContainsKey("processes"),
                "Unconfirmed owner disposal lost exact-generation evidence or invented a zero count.");
        }
        Check(failed, "Unconfirmed owner disposal silently reported successful cleanup.");
        Check(launches == 2 && handles.All(handle => handle.IsDisposed && handle.TerminationRequests > 0 && handle.ActiveProcesses > 0),
            "Owner disposal did not close every admitted handle when multiple shutdowns remained unconfirmed.");
        var requests = handles.Sum(handle => handle.TerminationRequests);
        await owner.DisposeAsync();
        Check(handles.Sum(handle => handle.TerminationRequests) == requests, "Repeated owner disposal was not idempotent.");
    }

    private sealed class FakeApplicationHandle(Stream responses) : IApplicationHostHandle
    {
        private int _activeProcesses = 2;
        private int _terminationRequests;
        private int _disposed;
        private volatile bool _helperHasExited;
        public int ActiveProcesses => Volatile.Read(ref _activeProcesses);
        public bool HelperHasExited => _helperHasExited;
        public Stream Responses { get; } = responses;
        internal int TerminationRequests => Volatile.Read(ref _terminationRequests);
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool RequestTermination() { Interlocked.Increment(ref _terminationRequests); return true; }
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(_helperHasExited, "The host waited for a helper before fake quiescence was confirmed.");
            return Task.CompletedTask;
        }
        internal void ConfirmQuiescence()
        {
            _helperHasExited = true;
            Volatile.Write(ref _activeProcesses, 0);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Responses.Dispose();
        }
        internal static async Task<FakeApplicationHandle> CreateAsync(bool malformed)
        {
            var responses = new MemoryStream();
            await new ApplicationHostFrame(ApplicationHostFrame.RootExited, 0, 0, 0).WriteAsync(responses);
            responses.Position = 0;
            if (malformed) responses.WriteByte(0); // Corrupt the actual wire magic, not a fabricated exception path.
            responses.Position = 0;
            return new FakeApplicationHandle(responses);
        }
    }

    private static async Task UserOnlyRoutingAsync()
    {
        var previous = Environment.GetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable);
        try
        {
            // Only recording dispatchers exist in this scope. Exercise ordinary and nested
            // caller contexts explicitly; never clear the marker around a real pipe dispatch.
            foreach (var nested in new[] { false, true })
            {
                Environment.SetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable, nested ? "1" : null);
                var user = new RecordingDispatcher(request => new OperationReceipt(false, "user-jid", request.Rid!, "failed",
                    OperationNames.Public(request), "svc_down"));
                var service = new RecordingDispatcher(_ => throw new InvalidOperationException("Application request fell back to the service."));
                var system = new RecordingDispatcher(_ => throw new InvalidOperationException("Application request entered the System route."));
                await using var routing = new RoutingDispatcher(user, service, system);
                foreach (var action in new[] { "start", "status", "stop", "inventory", "release" })
                {
                    var request = new OperationRequest("app", new Dictionary<string, string> { ["op"] = action }, "route-app-" + action);
                    var blocked = nested && (action is "start" or "stop" or "release");
                    var expected = blocked ? "reentrant_mutation" : "svc_down";
                    var absent = await routing.DispatchAsync(request);
                    Check(!absent.Ok && absent.Err == expected,
                        $"Application route {action}, nested={nested}, returned {absent.Err}; expected {expected}.");
                    var root = await routing.DispatchAsync(request with { Root = "AB12" });
                    Check(!root.Ok && root.Err == (blocked ? "reentrant_mutation" : "auth"),
                        "Root application request escaped its earlier nested-mutation or authentication boundary.");
                }
                Check(user.Count == (nested ? 2 : 5) && service.Count == 0 && system.Count == 0,
                    "Application routing contacted a service or submitted a rejected mutation.");
            }
        }
        finally { Environment.SetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable, previous); }
    }

    private static IReadOnlyDictionary<string, object?> RequireSuccess(OperationReceipt receipt)
    {
        Check(receipt.Ok && receipt.Data is not null, "Application engine request failed: " + JsonSerializer.Serialize(receipt));
        return receipt.Data!;
    }

    private static async Task WaitEngineStateAsync(AgentOpsEngine engine, string name, string expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var receipt = await engine.ExecuteAsync(new OperationRequest("app", new Dictionary<string, string> { ["op"] = "status", ["name"] = name }));
            if (State(RequireSuccess(receipt)) == expected) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Application engine state did not become " + expected);
    }

    private static async Task ParserAndMcpParityAsync(string cwd)
    {
        await using var capture = new CaptureDispatcher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var aid = new string('a', 32);
        var epoch = new string('b', 32);
        var cursor = epoch + ":1:" + aid;
        var cases = new (string[] Cli, object Mcp, string Action)[]
        {
            (["app", "start", Configuration, "desktop", "cwd=" + cwd, "aid=" + aid, "rid=launch-rid", "--", "--app-fixture-exit", "space Ω"],
                new { op = "app_start", cfg = Configuration, name = "desktop", cwd, aid, rid = "launch-rid", arg = new[] { "--app-fixture-exit", "space Ω" } }, "start"),
            (["app", "status", "desktop"], new { op = "app_status", name = "desktop" }, "status"),
            (["app", "stop", aid, "rid=stop-rid"], new { op = "app_stop", aid, rid = "stop-rid" }, "stop"),
            (["app", "inventory", "max=8"], new { op = "app_inventory", max = 8 }, "inventory"),
            (["app", "inventory", "max=8", "\"cursor=" + cursor + "\""],
                new { op = "app_inventory", max = 8, cursor }, "inventory"),
            (["app", "release", aid, "\"epoch=" + epoch + "\"", "rid=release-parity"],
                new { op = "app_release", aid, epoch, rid = "release-parity" }, "release")
        };
        foreach (var item in cases)
        {
            Check(await PaoProgram.RunCliAsync(item.Cli, capture, output, error, CancellationToken.None) == 0,
                "CLI application parser rejected the fixed operation: " + error);
            var cli = capture.Last ?? throw new InvalidOperationException("CLI did not dispatch.");
            var response = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(item.Mcp), capture, CancellationToken.None);
            Check(!response["isError"]!.GetValue<bool>(), "MCP application parser rejected the fixed operation: " + response);
            var mcp = capture.Last ?? throw new InvalidOperationException("MCP did not dispatch.");
            Check(cli.Op == "app" && mcp.Op == cli.Op && cli.Arg["op"] == item.Action &&
                cli.Arg.OrderBy(value => value.Key, StringComparer.Ordinal).SequenceEqual(mcp.Arg.OrderBy(value => value.Key, StringComparer.Ordinal)) &&
                cli.Rid == mcp.Rid && cli.Cwd == mcp.Cwd && cli.Ws == mcp.Ws,
                "CLI/MCP application arguments, workspace, or generation identity diverged.");
            Check(!mcp.Arg.ContainsKey("root") && mcp.Root is null && mcp.Body is null, "Application parsing admitted privileged or body input.");
        }
        var prior = capture.Last;
        var invalid = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
        {
            op = "app_start", cfg = Configuration, name = "desktop", root = "AB12"
        }), capture, CancellationToken.None);
        Check(invalid["isError"]!.GetValue<bool>() && ReferenceEquals(prior, capture.Last), "MCP root input reached the application dispatcher.");
    }

    private static void RequireReceipt(IReadOnlyDictionary<string, object?> value, string name)
    {
        Check(Aid(value).Length == 32 && Aid(value).All(char.IsAsciiHexDigitLower) && Text(value, "name") == name,
            "Application receipt lacks exact generation/name identity.");
        Check(DateTimeOffset.TryParse(Text(value, "start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "Application root start identity was not a timestamp.");
        Check(!value.Keys.Any(key => key is "out" or "err_tail" or "stdout" or "stderr" or "helperPid"),
            "Application status exposed output tails or its internal helper.");
        Check(value.Values.All(item => item is not null) && !value.ContainsKey("issue"),
            "Healthy application status emitted a null optional field or a fabricated issue.");
    }

    private static async Task<IReadOnlyDictionary<string, object?>> WaitStatusAsync(UserApplicationManager manager, string name,
        Func<IReadOnlyDictionary<string, object?>, bool> predicate)
    {
        IReadOnlyDictionary<string, object?>? last = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            last = await manager.StatusAsync(name);
            if (predicate(last)) return last;
            await Task.Delay(25);
        }
        throw new TimeoutException("Application state did not converge: " + JsonSerializer.Serialize(last));
    }

    private sealed record LiveMember(int Pid, string Start);
    private sealed record LiveInventory(int Helper, IReadOnlyList<LiveMember> Members);
    private sealed record ObservedApplication(IReadOnlyDictionary<string, object?> Status, LiveInventory Inventory);

    private static async Task<ObservedApplication> WaitInventoryStatusAsync(UserApplicationManager manager, string name,
        string state, IReadOnlyList<Process> required, IReadOnlyList<int>? absent = null)
    {
        var identities = required.Select(process => new LiveMember(process.Id, process.StartTime.ToUniversalTime().ToString("O"))).ToArray();
        IReadOnlyDictionary<string, object?>? last = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var before = await ReadLiveInventoryAsync(manager, name);
            last = await manager.StatusAsync(name);
            var after = await ReadLiveInventoryAsync(manager, name);
            // Console/support processes belong to the application too. Compare against
            // a stable exact job inventory rather than assuming one process per argv.
            if (before is not null && after is not null && before.Helper == after.Helper &&
                before.Members.SequenceEqual(after.Members) && State(last) == state &&
                Number(last, "processes") == after.Members.Count(member => member.Pid != after.Helper) &&
                identities.All(after.Members.Contains) && (absent is null || !after.Members.Any(member => absent.Contains(member.Pid))))
                return new ObservedApplication(last, after);
            await Task.Delay(25);
        }
        throw new TimeoutException("Application status did not match stable live job membership: " + JsonSerializer.Serialize(last));
    }

    private static async Task<LiveInventory?> ReadLiveInventoryAsync(UserApplicationManager manager, string name)
    {
        var inventory = await manager.InspectProcessesAsync(name);
        if (inventory.GetValueOrDefault("processes") is not IEnumerable<IReadOnlyDictionary<string, object?>> entries) return null;
        Check(inventory.GetValueOrDefault("truncated") is false, "Exact fixture job inventory was truncated.");
        var members = new List<LiveMember>();
        foreach (var entry in entries)
        {
            if (entry.GetValueOrDefault("exited") is true) continue;
            if (entry.GetValueOrDefault("exited") is false && entry.GetValueOrDefault("start") is string start)
                members.Add(new LiveMember(Number(entry, "pid"), start));
            else if (entry.GetValueOrDefault("issue")?.ToString() != nameof(ArgumentException))
                return null; // Retry an unresolved observation; never turn access failure into proof of exit.
        }
        var helper = Number(inventory, "helper");
        if (!members.Any(member => member.Pid == helper)) return null;
        return new LiveInventory(helper, members.OrderBy(member => member.Pid).ToArray());
    }

    private static Task RequireMembersGoneAsync(IReadOnlyList<LiveMember> members) =>
        WaitUntilAsync(() => members.All(member => !IsExactProcessAlive(member.Pid, member.Start)),
            "An exact owned job member, including application support processes or the helper, survived terminal cleanup.");

    private static async Task RejectAsync(string code, Func<Task> action)
    {
        try { await action(); }
        catch (AgentOpsException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected application rejection: " + code);
    }

    private static async Task RequireExitedAsync(params Process[] processes)
    {
        foreach (var process in processes)
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(failure);
    }

    private static bool IsExactProcessAlive(int pid, string start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToString("O") == start;
        }
        catch (ArgumentException) { return false; }
    }

    private static string Text(IReadOnlyDictionary<string, object?> value, string key) =>
        value.TryGetValue(key, out var item) && item is not null ? Convert.ToString(item, CultureInfo.InvariantCulture)! :
            throw new InvalidOperationException("Missing application field: " + key);
    private static int Number(IReadOnlyDictionary<string, object?> value, string key) =>
        value.TryGetValue(key, out var item) && item is not null ? Convert.ToInt32(item, CultureInfo.InvariantCulture) :
            throw new InvalidOperationException("Missing numeric application field: " + key);
    private static string Aid(IReadOnlyDictionary<string, object?> value) => Text(value, "aid");
    private static string State(IReadOnlyDictionary<string, object?> value) => Text(value, "state");
    private static void Check(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }

    private sealed class ApplicationFixture : IAsyncDisposable
    {
        private readonly List<Process> _processes = [];
        private readonly string _leaf = "pao-app-" + Guid.NewGuid().ToString("N");
        private readonly string _root;
        private readonly string _executable;
        private ApplicationFixture()
        {
            Check(OperatingSystem.IsWindows() && LocalIdentity.Current().Session > 0, "Application tests require a signed-in Windows user session.");
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project is not null && !File.Exists(Path.Combine(project.FullName, "PegasusCortex.slnx"))) project = project.Parent;
            if (project is null) throw new DirectoryNotFoundException("Cortex source root was not found.");
            var parts = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var bin = Array.FindIndex(parts, part => part.Equals("bin", StringComparison.OrdinalIgnoreCase));
            var configuration = bin >= 0 && bin + 1 < parts.Length ? parts[bin + 1] : "Debug";
            _executable = Path.Combine(project.FullName, "tests", "Pegasus.AgentOps.FakeTool", "bin", configuration,
                "net10.0-windows", "win-x64", "Pegasus.AgentOps.FakeTool.exe");
            if (!File.Exists(_executable)) throw new FileNotFoundException("Build the owned FakeTool fixture first.", _executable);
            _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _leaf));
            System.IO.Directory.CreateDirectory(_root);
            State = Directory("state");
            Work = NewWork("work");
            Tools = new ToolRecipeOperations(State);
        }

        internal string State { get; }
        internal string Work { get; }
        internal ToolRecipeOperations Tools { get; }
        internal string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        internal string NewWork(string name)
        {
            var path = Directory(name);
            File.WriteAllText(Path.Combine(path, "app-fixture.marker"), _leaf);
            return path;
        }
        internal int LaunchCount(string cwd) => File.Exists(Path.Combine(cwd, "launches.txt")) ? File.ReadAllLines(Path.Combine(cwd, "launches.txt")).Length : 0;

        internal static async Task<ApplicationFixture> CreateAsync()
        {
            var fixture = new ApplicationFixture();
            try { await fixture.RegisterAsync(Configuration, ToolExecutionMode.User); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async Task RegisterAsync(string name, ToolExecutionMode mode)
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) environment[key] = value;
            using var binary = File.OpenRead(_executable);
            var sha = Convert.ToHexStringLower(SHA256.HashData(binary));
            await Tools.Tools.RegisterAsync(new RegisteredToolDefinition(name, 1, _executable, sha, Mode: mode,
                TimeoutSeconds: 10, ExpectedExitCodes: [0], MaxArguments: 8,
                VersionProbe: new ToolProbeDefinition(["--version"], [0], "[\"--version\"]"), Environment: environment));
        }

        internal async Task<Process> ProcessAsync(string cwd, string file, int? differentPid = null)
        {
            Process? result = null;
            await WaitUntilAsync(() =>
            {
                try
                {
                    using var identity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cwd, file)));
                    var pid = identity.RootElement.GetProperty("pid").GetInt32();
                    if (pid == differentPid) return false;
                    var process = Process.GetProcessById(pid);
                    if (process.HasExited || process.StartTime.ToUniversalTime().ToString("O") != identity.RootElement.GetProperty("start").GetString())
                    { process.Dispose(); return false; }
                    result = process;
                    _processes.Add(process);
                    return true;
                }
                catch (Exception exception) when (exception is IOException or JsonException or ArgumentException) { return false; }
            }, "The fixed fixture did not publish a live exact process identity.");
            return result!;
        }

        public async ValueTask DisposeAsync()
        {
            // Manager stop/disposal owns process termination. A broken cleanup test
            // waits for the fixed 25-second fixture timeout instead of killing by PID.
            foreach (var process in _processes)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                finally { process.Dispose(); }
            }
            var directory = new DirectoryInfo(_root);
            if (directory.Exists)
            {
                Check(directory.Name == _leaf && directory.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                    "Refusing cleanup outside the exact temporary application fixture.");
                RequireNoLinks(directory);
                directory.Delete(recursive: true);
            }
        }

        private static void RequireNoLinks(FileSystemInfo item)
        {
            Check(item.LinkTarget is null && (item.Attributes & FileAttributes.ReparsePoint) == 0, "Refusing recursive fixture cleanup through a link.");
            if (item is DirectoryInfo directory)
                foreach (var child in directory.EnumerateFileSystemInfos()) RequireNoLinks(child);
        }
    }
}
