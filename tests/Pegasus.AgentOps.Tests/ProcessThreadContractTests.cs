using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ProcessThreadContractTests
{
    private const int Pid = 42;
    private const string Start = "2026-11-20T10:00:00.0000001Z";
    private const string OtherStart = "2026-11-20T10:00:01.0000002Z";
    private const string ThreadStart = "2026-11-20T10:00:02.1234567Z";
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";

    internal static async Task RunAsync()
    {
        RequestContract();
        CreationTimeDecoder();
        ValidationBeforeAccess();
        ProcessBinding();
        EntryIdentityAndRaces();
        ObservationBounds();
        FailureAndCleanup();
        CancellationAndCleanup();
        await FrontendAsync();
        await EngineAsync();
        await DeniedRoutesAsync();
        Console.WriteLine("PASS process-threads injected identity/bounds/races/cleanup, captured CLI/MCP and isolated Engine/SQLite routing/history (no live inventory)");
    }

    // Deliberately separate from the injected suite: success here is evidence
    // for the real adapter against this owned test process, never product startup.
    internal static async Task<int> RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("UNAVAILABLE process-threads Windows fixture: ordinary interactive Windows context required");
            return 77;
        }
        var code = await RunWindowsCoreAsync();
        if (code == 77) Console.WriteLine("UNAVAILABLE process-threads Windows fixture: ordinary interactive Windows context required");
        return code;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsCoreAsync()
    {
        LocalPeer identity;
        try { identity = LocalIdentity.Current(); }
        catch (AgentOpsException error) when (error.Code == "auth") { return 77; }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { return 77; }
        if (identity.Elevated || identity.Session <= 0 || identity.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") return 77;
        var pid = Environment.ProcessId;
        var inspected = ProcessOperations.Inspect(pid);
        Check(inspected.Ok && inspected.Process is not null && inspected.State == "running", "current owned test process identity is available");
        var start = inspected.Process!.Start;
        using var release = new ManualResetEventSlim();
        var ready = new[] { new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var failures = new ConcurrentQueue<Exception>();
        var started = new List<Thread>(2);
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var slot = index;
                var thread = new Thread(() =>
                {
                    try
                    {
                        var tid = WindowsFixtureNative.GetCurrentThreadId();
                        Check(tid != 0, "owned fixture thread has a native identity");
                        ready[slot].TrySetResult(tid);
                        if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("owned thread release deadline");
                    }
                    catch (Exception error) { failures.Enqueue(error); ready[slot].TrySetException(error); }
                }) { IsBackground = true, Name = "PAO thread identity fixture" };
                thread.Start(); started.Add(thread);
            }
            var tids = await Task.WhenAll(ready.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(tids.Distinct().Count() == 2, "two distinct owned fixture threads are held open");
            var first = ProcessThreadOperations.List(pid, start, ProcessThreadOperations.MaximumResults);
            Check(first.Ok && first.Pid == pid && first.ObservedStart == start, "real adapter verifies the exact owned process generation");
            var observed = new Dictionary<uint, string>();
            foreach (var tid in tids)
            {
                var row = first.Threads.SingleOrDefault(candidate => candidate.Tid == tid);
                Check(row is not null && ProcessOperations.IsCanonicalStartIdentity(row.Start) &&
                    DateTime.ParseExact(row.Start, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToFileTimeUtc() > 0,
                    "real adapter returns the held thread with a canonical nonzero creation generation");
                observed.Add(tid, row!.Start);
            }
            var second = ProcessThreadOperations.List(pid, start, ProcessThreadOperations.MaximumResults);
            Check(second.Ok && second.ObservedStart == start && tids.All(tid => second.Threads.Any(row =>
                row.Tid == tid && row.Start == observed[tid])), "held native thread generations remain stable across two observations");
            var wrongStart = ProcessOperations.FormatStart(DateTime.ParseExact(start, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).AddTicks(1));
            var refused = ProcessThreadOperations.List(pid, wrongStart, 1);
            Check(!refused.Ok && refused.State == "reused" && refused.Error == "pid_reused" && refused.Threads.Count == 0,
                "real adapter refuses an incorrect process generation");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Cancelled(() => ProcessThreadOperations.List(pid, start, 1, cancelled.Token));
            Check(failures.IsEmpty, "owned threads remain held throughout the native observations");
        }
        finally
        {
            release.Set();
            var joined = true;
            foreach (var thread in started) joined &= thread.Join(TimeSpan.FromSeconds(5));
            Check(joined && failures.IsEmpty, "only the two owned fixture threads are released and joined");
        }
        Console.WriteLine("PASS process-threads real Windows adapter: two owned held threads, stable creation identities across two observations, generation/pre-cancel refusal and joined cleanup");
        return 0;
    }

    private static void RequestContract()
    {
        var request = Request();
        Check(ProcessThreadOperations.IsRequest(request), "exact process/thread selector");
        Check(ProcessThreadOperations.ParseRequest(request) == new ProcessThreadRequest(Pid, Start, 64), "default bounded request");
        Check(ProcessThreadOperations.ParseRequest(With(request, "max", "256")).Max == 256, "maximum request bound");
        var presentation = With(With(With(request, "rid", request.Rid!), "detail", "true"), "output", "structured") with
            { Cwd = @"C:\ambient-unused", Quiet = true, Detail = true };
        Check(ProcessThreadOperations.ParseRequest(presentation).Start == Start, "presentation/ambient directory does not change target");
        Check(!ProcessThreadOperations.IsRequest(request with { Op = "net" }) &&
            !ProcessThreadOperations.IsRequest(With(request, "op", "inspect")) &&
            !ProcessThreadOperations.IsRequest(request with { Arg = null! }), "predicate never expands to another operation");

        foreach (var forbidden in new[] { "root", "tmo", "wait", "pattern", "env0", "arg0", "cwd", "ws",
            "__execution_context_system", "__authenticated_frozen", OperationRequest.BodyOmittedKey, "MAX" })
            Refused(() => ProcessThreadOperations.ParseRequest(With(request, forbidden, "1")), "invalid");
        foreach (var malformed in new[]
        {
            request with { Root = "" }, request with { Root = "FAKE" },
            request with { Body = "" }, request with { Ws = "" },
            request with { PathRoots = new Dictionary<string, string>() }
        }) Refused(() => ProcessThreadOperations.ParseRequest(malformed), malformed.Root is not null ? "auth" : "invalid");
        foreach (var pid in new[] { "", "0", "-1", "+1", " 42", "42 ", "2147483648", "1.0", "４２", new string('1', 100) })
            Refused(() => ProcessThreadOperations.ParseRequest(With(request, "pid", pid)), "invalid");
        foreach (var maximum in new[] { "0", "257", "-1", "+1", " 1", "1.0", "2147483648" })
            Refused(() => ProcessThreadOperations.ParseRequest(With(request, "max", maximum)), "invalid");
        foreach (var start in new[] { "", "yesterday", Start.TrimEnd('Z'), "2026-11-20T10:00:00Z",
            "2026-11-20T10:00:00.0000001+00:00", Start + " ", new string('x', 100) })
            Refused(() => ProcessThreadOperations.ParseRequest(With(request, "start", start)), "invalid");
        foreach (var missing in new[] { "pid", "start" })
        {
            var args = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal); args.Remove(missing);
            Refused(() => ProcessThreadOperations.ParseRequest(request with { Arg = args }), "invalid");
        }
        Refused(() => ProcessThreadOperations.ParseRequest(With(request, "rid", "another-rid")), "invalid");
        Refused(() => ProcessThreadOperations.ParseRequest(request with { Rid = "bad\nrid" }), "invalid");
        Refused(() => ProcessThreadOperations.ParseRequest(request with { Rid = new string('r', 129) }), "invalid");
        Refused(() => ProcessThreadOperations.ParseRequest(With(request, "detail", "True")), "invalid");
        Refused(() => ProcessThreadOperations.ParseRequest(With(request, "output", "anything")), "invalid");
    }

    private static void ValidationBeforeAccess()
    {
        var inventory = Standard();
        foreach (var (pid, start, max) in new[] { (0, Start, 1), (-1, Start, 1), (Pid, "wrong", 1),
            (Pid, Start, 0), (Pid, Start, 257) })
            Refused(() => ProcessThreadOperations.List(pid, start, max, inventory), "invalid");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Cancelled(() => ProcessThreadOperations.List(Pid, Start, 1, inventory, cancellation.Token));
        Check(inventory.ProcessOpens == 0 && inventory.SnapshotOpens == 0 && inventory.ThreadOpens == 0,
            "validation and pre-cancellation precede every native boundary");
    }

    private static void CreationTimeDecoder()
    {
        // Invalid decoder inputs only; no process/thread native entry point is invoked.
        foreach (var value in new[] { 0UL, ulong.MaxValue, (ulong)long.MaxValue })
        {
            var refused = false;
            try { _ = ProcessThreadOperations.DecodeCreationTime(value, "thread_query_failed"); }
            catch (ProcessThreadInventoryException error) when (error.Code == "thread_query_failed") { refused = true; }
            Check(refused, "zero, overflowing and out-of-range creation times do not become generation identities");
        }
    }

    private static void ProcessBinding()
    {
        var missing = Standard(); missing.Process = null;
        var result = List(missing);
        Check(result.Ok && result.State == "exited" && result.ObservedStart is null && result.Threads.Count == 0 &&
            !result.SnapshotExhausted && missing.SnapshotOpens == 0, "missing process is exited, not an empty successful snapshot");

        var denied = Standard(); denied.ProcessOpenFailure = "access";
        result = List(denied);
        Check(!result.Ok && result.State == "locked" && result.Error == "access" && denied.SnapshotOpens == 0,
            "query denial is distinct from process absence");

        var mismatch = Standard(); mismatch.Process!.Start = OtherStart;
        result = List(mismatch);
        Check(!result.Ok && result.State == "reused" && result.Error == "pid_reused" && result.ObservedStart == OtherStart &&
            mismatch.SnapshotOpens == 0, "creation mismatch prevents enumeration");
        mismatch.AssertClosed();

        var exited = Standard(); exited.Process!.Exited = true;
        result = List(exited);
        Check(result.Ok && result.State == "exited" && exited.SnapshotOpens == 0, "already exited pinned process is honest");
        exited.AssertClosed();

        var invalid = Standard(); invalid.Process!.Start = "invalid-native-time";
        result = List(invalid);
        Check(!result.Ok && result.Error == "process_query_failed" && invalid.SnapshotOpens == 0,
            "invalid native process identity never opens a snapshot");
        invalid.AssertClosed();

        foreach (var change in new[] { "exit", "generation", "access" })
        {
            var during = Standard();
            during.Snapshot.OnDispose = () =>
            {
                if (change == "exit") during.Process!.Exited = true;
                if (change == "generation") during.Process!.Start = OtherStart;
                if (change == "access") during.Process!.Failure = "access";
            };
            result = List(during);
            Check(result.Threads.Count == 0 && !result.SnapshotExhausted && result.Scanned == 1,
                "post-enumeration target invalidation discards thread observations");
            Check(change == "exit" ? result.Ok && result.State == "exited" :
                !result.Ok && result.Error == (change == "generation" ? "pid_reused" : "access"),
                "post-enumeration target failure retains its actual category");
            during.AssertClosed();
        }
    }

    private static void EntryIdentityAndRaces()
    {
        var inventory = new Inventory();
        inventory.Snapshot.Rows.AddRange(new ProcessThreadSnapshotEntry[]
        {
            new(100, 99), new(101, Pid), new(102, Pid), new(103, Pid), new(104, Pid),
            new(105, Pid), new(106, Pid), new(107, Pid), new(0, Pid), new(uint.MaxValue - 7, Pid)
        });
        inventory.Add(101);
        inventory.Add(103).OwnerPid = 999;
        inventory.Add(104).Exited = true;
        var dies = inventory.Add(105); dies.OnReadStart = () => dies.Exited = true;
        inventory.Add(106).FailureAt = "owner";
        inventory.Add(107).Start = "noncanonical";
        inventory.Add(uint.MaxValue - 7).Start = OtherStart;
        var result = List(inventory);
        Check(result.Ok && result.State == "partial" && result.SnapshotExhausted && result.Limit is null &&
            result.Scanned == 10 && result.Raced == 4 && result.Unavailable == 3, "EOF, races and unavailable identities have independent meanings");
        Check(result.Threads.SequenceEqual(new[] { new AgentThreadIdentity(101, ThreadStart),
            new AgentThreadIdentity(uint.MaxValue - 7, OtherStart) }), "exact native-width TID and canonical creation identities are preserved");
        Check(!inventory.RequestedThreads.Contains(100) && !inventory.RequestedThreads.Contains(0),
            "foreign and invalid TIDs never trigger thread opens");
        inventory.AssertClosed();

        var empty = new Inventory();
        result = List(empty);
        Check(result.Ok && result.State == "running" && result.SnapshotExhausted && result.Threads.Count == 0 && result.Scanned == 0,
            "an exhausted empty snapshot is distinct from process absence");
        empty.AssertClosed();
    }

    private static void ObservationBounds()
    {
        var limited = new Inventory();
        for (uint tid = 1; tid <= 4; tid++) { limited.Snapshot.Rows.Add(new(tid, Pid)); limited.Add(tid); }
        var result = List(limited, 2);
        Check(result.Ok && result.Threads.Count == 2 && result.Scanned == 3 && result.Limit == "results" &&
            !result.SnapshotExhausted && limited.ThreadOpens == 3, "one verified lookahead establishes result truncation");
        limited.AssertClosed();

        var exact = new Inventory();
        for (uint tid = 1; tid <= 2; tid++) { exact.Snapshot.Rows.Add(new(tid, Pid)); exact.Add(tid); }
        result = List(exact, 2);
        Check(result.Ok && result.Threads.Count == 2 && result.SnapshotExhausted && result.Limit is null,
            "meeting the requested count alone does not claim omitted results");
        exact.AssertClosed();

        var gap = new Inventory();
        gap.Snapshot.Rows.AddRange(new ProcessThreadSnapshotEntry[] { new(1, Pid), new(2, Pid) });
        gap.Add(1); gap.Add(2).FailureAt = "start";
        result = List(gap, 1);
        Check(result.Ok && result.Threads.Count == 1 && result.SnapshotExhausted && result.Limit is null && result.Unavailable == 1,
            "an unverifiable extra candidate is a coverage gap, not proof of another verified result");
        gap.AssertClosed();

        var global = new Inventory();
        global.Snapshot.EntryAt = index => index < ProcessThreadOperations.MaximumSnapshotEntries + 10
            ? new ProcessThreadSnapshotEntry(checked((uint)index + 1), 999) : null;
        result = List(global, 1);
        Check(result.Ok && result.State == "partial" && result.Scanned == 65536 && result.Threads.Count == 0 &&
            result.Limit == "scan" && !result.SnapshotExhausted && global.Snapshot.Moves == 65536 && global.ThreadOpens == 0,
            "systemwide foreign entries cannot bypass the fixed streaming scan budget");
        global.AssertClosed();

        var beforeBudget = new Inventory();
        beforeBudget.Snapshot.EntryAt = index => index < ProcessThreadOperations.MaximumSnapshotEntries - 1
            ? new ProcessThreadSnapshotEntry(checked((uint)index + 1), 999) : null;
        result = List(beforeBudget, 1);
        Check(result.SnapshotExhausted && result.Limit is null && result.Scanned == 65535,
            "EOF before the scan ceiling is not reported as truncation");
        beforeBudget.AssertClosed();
    }

    private static void FailureAndCleanup()
    {
        var opening = Standard(); opening.SnapshotOpenFailure = true;
        var result = List(opening);
        Check(!result.Ok && result.Error == "snapshot_failed" && result.Threads.Count == 0 && !result.SnapshotExhausted,
            "snapshot creation failure is not a successful empty result");
        opening.AssertClosed();

        var midstream = Standard();
        midstream.Snapshot.BeforeMove = index => { if (index == 1) throw new ProcessThreadInventoryException("snapshot_failed"); };
        result = List(midstream);
        Check(!result.Ok && result.State == "partial" && result.Error == "snapshot_failed" && result.Threads.Count == 1 &&
            !result.SnapshotExhausted && result.Limit is null && result.Scanned == 1,
            "enumeration failure retains only verified observations with explicit incomplete coverage");
        midstream.AssertClosed();

        foreach (var stage in new[] { "owner", "exited", "start" })
        {
            var failed = Standard(); failed.Configured[7].FailureAt = stage;
            result = List(failed);
            Check(result.Ok && result.Unavailable == 1 && result.Threads.Count == 0 && result.SnapshotExhausted,
                "a per-thread query failure is a reported omission rather than absence");
            failed.AssertClosed();
        }
        var openFailed = Standard();
        openFailed.Factories[7] = () => throw new ProcessThreadInventoryException("access");
        result = List(openFailed);
        Check(result.Ok && result.Unavailable == 1 && result.Threads.Count == 0, "thread open denial remains unavailable");
        openFailed.AssertClosed();
    }

    private static void CancellationAndCleanup()
    {
        foreach (var stage in new[] { "process", "snapshot", "iteration", "thread", "owner", "start", "postcheck" })
        {
            var inventory = Standard();
            using var cancellation = new CancellationTokenSource();
            if (stage == "process") inventory.AfterOpenProcess = cancellation.Cancel;
            if (stage == "snapshot") inventory.AfterOpenSnapshot = cancellation.Cancel;
            if (stage == "iteration") inventory.Snapshot.BeforeMove = _ => cancellation.Cancel();
            if (stage == "thread") inventory.AfterOpenThread = cancellation.Cancel;
            if (stage == "owner") inventory.Configured[7].OnReadOwner = cancellation.Cancel;
            if (stage == "start") inventory.Configured[7].OnReadStart = cancellation.Cancel;
            if (stage == "postcheck") inventory.Snapshot.OnDispose = cancellation.Cancel;
            Cancelled(() => ProcessThreadOperations.List(Pid, Start, 2, inventory, cancellation.Token));
            inventory.AssertClosed();
        }
    }

    private static async Task FrontendAsync()
    {
        await using var dispatcher = new CaptureDispatcher();
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await PaoProgram.RunCliAsync(["--json", "proc", "threads", "pid=42", "start=" + Start, "max=2"],
            dispatcher, output, error, CancellationToken.None);
        Check(code == 0 && dispatcher.Last is { Op: "proc", Ws: null, Root: null } &&
            ProcessThreadOperations.ParseRequest(dispatcher.Last!) == new ProcessThreadRequest(Pid, Start, 2),
            "CLI exposes the single bounded threads leaf without an implicit workspace: " + error);
        foreach (var extra in new[] { "root=FAKE", "wait=false", "tmo=1", "pattern=x", "ws=elsewhere", "arg0=x", "max=257" })
        {
            var calls = dispatcher.Calls;
            code = await PaoProgram.RunCliAsync(["proc", "threads", "pid=42", "start=" + Start, extra], dispatcher,
                new StringWriter(), new StringWriter(), CancellationToken.None);
            Check(code == 2 && dispatcher.Calls == calls, "unsupported CLI field/bound must fail before dispatch");
        }
        var valid = "{\"op\":\"proc_threads\",\"pid\":42,\"start\":" + JsonSerializer.Serialize(Start);
        var previous = dispatcher.Calls;
        var response = await CallMcpAsync(valid + ",\"max\":256,\"output\":\"structured\"}", dispatcher);
        Check(dispatcher.Calls == previous + 1 && dispatcher.Last!.Ws is null &&
            ProcessThreadOperations.ParseRequest(dispatcher.Last!).Max == 256 && response["isError"]?.GetValue<bool>() != true,
            "MCP typed thread request reaches only the captured dispatcher");
        foreach (var invalid in new[]
        {
            valid + ",\"pid\":43}", valid + ",\"start\":" + JsonSerializer.Serialize(OtherStart) + "}",
            valid + ",\"op\":\"proc_inspect\"}", valid + ",\"op\":\"proc_stop\"}",
            "{\"op\":\"proc_stop\",\"op\":\"proc_threads\",\"pid\":42,\"start\":" + JsonSerializer.Serialize(Start) + "}",
            valid + ",\"max\":1,\"max\":2}",
            valid + ",\"max\":257}", valid + ",\"wait\":false}", valid + ",\"root\":\"FAKE\"}",
            valid + ",\"pattern\":\"x\"}", valid + ",\"tmo\":1}", valid + ",\"text\":\"unused\"}",
            "{\"op\":\"proc_threads\",\"pid\":42}", valid + ",\"max\":\"2\"}"
        })
        {
            var calls = dispatcher.Calls;
            response = await CallMcpAsync(invalid, dispatcher);
            Check(dispatcher.Calls == calls && response["isError"]?.GetValue<bool>() == true,
                "duplicate/invalid MCP fields are refused before dictionary projection");
        }
    }

    private static async Task EngineAsync()
    {
        using var temp = new TempRoot();
        var state = temp.Dir("thread-engine");
        var old = Seed(state, temp.Path);
        var inventory = Standard();
        var admission = new UserHostAdmission();
        await using var engine = Engine(state, inventory, admission);
        var request = Request(old.Rid);
        var result = await engine.ExecuteAsync(request);
        Check(result.Ok && result.Op == "proc_threads" && result.Jid != old.Jid &&
            result.Data?.GetValueOrDefault("result") is ProcessThreadsResult { Threads.Count: 1, SnapshotExhausted: true } &&
            inventory.ProcessOpens == 1 && RowCount(state) == 1,
            "fresh injected thread snapshot does not replay an unrelated durable RID or create history");
        inventory.AssertClosed();
        inventory.Reset();
        inventory.Snapshot.Rows.Add(new(8, Pid)); inventory.Add(8).Start = OtherStart;
        var second = await engine.ExecuteAsync(request);
        Check(second.Ok && second.Jid != result.Jid && second.Data?.GetValueOrDefault("result") is ProcessThreadsResult observed &&
            observed.Threads.Single().Tid == 8 && RowCount(state) == 1 && Stored(state, old.Rid).Jid == old.Jid,
            "repeated snapshot RID observes fresh identities and preserves the unrelated receipt");
        inventory.AssertClosed();

        var calls = inventory.ProcessOpens;
        foreach (var malformed in MalformedRequests(request))
        {
            var refused = await engine.ExecuteAsync(malformed);
            Check(!refused.Ok && refused.Op == "proc_threads" && refused.Jid != old.Jid && inventory.ProcessOpens == calls &&
                RowCount(state) == 1, "raw engine validation rejects preprojection fields before access/history/replay");
        }
        using (var closing = admission.TryClose() ?? throw new InvalidOperationException("fixture admission already closed"))
        {
            await closing.WhenIdleAsync(CancellationToken.None);
            var refused = await engine.ExecuteAsync(request);
            Check(!refused.Ok && refused.Err == "host_stopping" && refused.Op == "proc_threads" && refused.Jid != old.Jid &&
                inventory.ProcessOpens == calls && RowCount(state) == 1, "closed host never rescues a thread read with unrelated stored RID");
        }
        Check(Stored(state, old.Rid).Op == old.Op, "rejections preserve the original cross-operation receipt");
    }

    private static async Task DeniedRoutesAsync()
    {
        using var temp = new TempRoot();
        var state = temp.Dir("thread-denied");
        var old = Seed(state, temp.Path);
        var request = Request(old.Rid);
        var inventory = Standard();
        await using (var engine = Engine(state, inventory, new UserHostAdmission()))
        {
            var frozen = await engine.ExecuteAuthenticatedFrozenDirectAsync(request);
            Check(!frozen.Ok && frozen.Err == "user_dispatch_required" && frozen.Jid != old.Jid,
                "service-frozen route cannot query threads or replay unrelated history");
            var root = await engine.ExecuteAsync(request with { Root = "FAKE" });
            Check(!root.Ok && root.Err == "auth" && root.Jid != old.Jid, "a presented root request is not rescued by an ordinary receipt");
        }
        await using (var direct = Engine(state, inventory, new UserHostAdmission(), direct: true))
        {
            var refused = await direct.ExecuteAsync(request);
            Check(!refused.Ok && refused.Err == "precondition" && refused.Jid != old.Jid, "direct-only engine cannot execute the owner leaf");
            Check(!(await direct.ExecuteAuthenticatedFrozenDirectAsync(request)).Ok, "direct frozen dispatch also refuses the leaf");
        }
        var brokerCalls = 0;
        var brokerState = temp.Dir("thread-broker");
        await using (var broker = new AuthenticatedServiceBroker(brokerState, OwnerSid,
            new RootCodeVerifier(Path.Combine(brokerState, "fixture-unused.verifier")), false,
            (_, _, _, _) => { brokerCalls++; return Task.FromResult(old); },
            (_, _) => { brokerCalls++; return Task.FromResult(old); }, maintenanceMode: new OpenMode()))
        {
            var owner = new LocalPeer(OwnerSid, 7);
            var user = await broker.HandleUserAsync(request, owner, CancellationToken.None);
            var system = await broker.HandleSystemAsync(request with { Root = "FAKE" }, owner, CancellationToken.None);
            var application = await broker.HandleApplicationAsync(request, CancellationToken.None);
            Check(!user.Ok && user.Err == "user_dispatch_required" && !system.Ok && system.Err == "auth" &&
                !application.Ok && application.Err == "auth" && brokerCalls == 0,
                "broker user, SYSTEM and application entry points refuse before either dispatch delegate");
        }
        Check(inventory.ProcessOpens == 0 && inventory.SnapshotOpens == 0 && inventory.ThreadOpens == 0 && RowCount(state) == 1,
            "all excluded routes leave native reads and isolated existing history untouched");
    }

    private static IEnumerable<OperationRequest> MalformedRequests(OperationRequest request)
    {
        yield return request with { Root = "FAKE" };
        yield return request with { Body = "" };
        yield return request with { Ws = "" };
        yield return request with { PathRoots = new Dictionary<string, string>() };
        foreach (var field in new[] { "env0", "stdin", "wait", "tmo", "pattern", "__authenticated_frozen", "__execution_context_system" })
            yield return With(request, field, "1");
    }

    private static AgentOpsEngine Engine(string state, Inventory inventory, UserHostAdmission admission, bool direct = false) =>
        new(state, null, direct, null, false, null, maintenanceMode: new OpenMode(), userHostAdmission: admission, threadInventory: inventory);

    private static OperationReceipt Seed(string state, string root)
    {
        using var store = new SqliteJobStore(state);
        var receipt = new OperationReceipt(true, "unrelated-thread-fixture-jid", "unrelated-thread-fixture-rid", "done", "mkdir",
            Data: new Dictionary<string, object?> { ["marker"] = "unrelated-fixture" });
        return store.Claim(receipt, new("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(root, "never-created") }, receipt.Rid), true).Receipt;
    }

    private static long RowCount(string state)
    {
        using var connection = Connection(state); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static OperationReceipt Stored(string state, string rid)
    {
        using var connection = Connection(state); using var command = connection.CreateCommand();
        command.CommandText = "SELECT receipt FROM jobs WHERE rid=$rid"; command.Parameters.AddWithValue("$rid", rid);
        return JsonSerializer.Deserialize<OperationReceipt>((string)command.ExecuteScalar()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static SqliteConnection Connection(string state)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(state, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static async ValueTask<JsonObject> CallMcpAsync(string json, CaptureDispatcher dispatcher)
    {
        using var document = JsonDocument.Parse(json);
        return await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, dispatcher, CancellationToken.None,
            defaultWs: "must-not-forward", defaultCwd: @"C:\ambient-unused");
    }

    private static OperationRequest Request(string rid = "thread-fixture-rid") => new("proc", new Dictionary<string, string>
        { ["op"] = "threads", ["pid"] = Pid.ToString(CultureInfo.InvariantCulture), ["start"] = Start }, rid);

    private static OperationRequest With(OperationRequest request, string key, string value)
    {
        var args = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [key] = value };
        return request with { Arg = args };
    }

    private static ProcessThreadsResult List(Inventory inventory, int max = 64) => ProcessThreadOperations.List(Pid, Start, max, inventory);

    private static Inventory Standard()
    {
        var inventory = new Inventory(); inventory.Snapshot.Rows.Add(new(7, Pid)); inventory.Add(7); return inventory;
    }

    private static void Refused(Action action, string code)
    {
        try { action(); }
        catch (AgentOpsException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("expected fixed request refusal: " + code);
    }

    private static void Cancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("expected cooperative cancellation");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class OpenMode : IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read() => null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private sealed class CaptureDispatcher : IOperationDispatcher
    {
        internal OperationRequest? Last;
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Last = request; Calls++;
            return Task.FromResult(new OperationReceipt(true, "captured-thread-jid", request.Rid ?? "captured-thread-rid", "done", "proc_threads",
                Data: new Dictionary<string, object?> { ["result"] = new ProcessThreadsResult(true, "running", null, Pid, Start, Start,
                    new[] { new AgentThreadIdentity(7, ThreadStart) }, true, null, 1, 0, 0) }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Inventory : IProcessThreadInventory
    {
        internal FakeProcess? Process = new();
        internal FakeSnapshot Snapshot = new();
        internal readonly Dictionary<uint, Func<FakeThread?>> Factories = new();
        internal readonly Dictionary<uint, FakeThread> Configured = new();
        internal readonly List<uint> RequestedThreads = new();
        private readonly List<FakeLease> _opened = new();
        internal int ProcessOpens;
        internal int SnapshotOpens;
        internal int ThreadOpens;
        internal string? ProcessOpenFailure;
        internal bool SnapshotOpenFailure;
        internal Action? AfterOpenProcess;
        internal Action? AfterOpenSnapshot;
        internal Action? AfterOpenThread;

        internal FakeThread Add(uint tid)
        {
            var thread = new FakeThread(); Factories[tid] = () => thread; Configured[tid] = thread; return thread;
        }

        internal void Reset()
        {
            AssertClosed(); Process = new(); Snapshot = new(); Factories.Clear(); Configured.Clear();
        }

        public IProcessThreadProcessLease? OpenProcess(int pid)
        {
            Check(pid == Pid, "injected process receives the exact target PID"); ProcessOpens++;
            if (ProcessOpenFailure is not null) throw new ProcessThreadInventoryException(ProcessOpenFailure);
            if (Process is not null) _opened.Add(Process);
            AfterOpenProcess?.Invoke(); return Process;
        }

        public IProcessThreadSnapshot OpenSnapshot()
        {
            Check(Process is { Disposals: 0 }, "target process remains retained while opening the snapshot"); SnapshotOpens++;
            if (SnapshotOpenFailure) throw new ProcessThreadInventoryException("snapshot_failed");
            Snapshot.Guard = () => Check(Process is { Disposals: 0 }, "process remains retained during enumeration");
            _opened.Add(Snapshot); AfterOpenSnapshot?.Invoke(); return Snapshot;
        }

        public IProcessThreadLease? OpenThread(uint tid)
        {
            Check(Process is { Disposals: 0 } && Snapshot.Disposals == 0, "process and snapshot remain retained while opening a thread");
            ThreadOpens++; RequestedThreads.Add(tid);
            var thread = Factories.TryGetValue(tid, out var factory) ? factory() : null;
            if (thread is not null)
            {
                thread.Guard = () => Check(Process is { Disposals: 0 } && Snapshot.Disposals == 0,
                    "process and snapshot remain retained through each thread observation");
                _opened.Add(thread);
            }
            AfterOpenThread?.Invoke(); return thread;
        }

        internal void AssertClosed() => Check(_opened.All(lease => lease.Disposals == 1), "every acquired lease is disposed exactly once");
    }

    private abstract class FakeLease : IDisposable
    {
        internal int Disposals;
        internal Action? Guard;
        internal Action? OnDispose;
        protected void Enter() { Check(Disposals == 0, "no query after lease disposal"); Guard?.Invoke(); }
        public void Dispose() { Disposals++; OnDispose?.Invoke(); }
    }

    private sealed class FakeProcess : FakeLease, IProcessThreadProcessLease
    {
        internal string Start = ProcessThreadContractTests.Start;
        internal bool Exited;
        internal string? Failure;
        public bool IsExited() { Enter(); if (Failure is not null) throw new ProcessThreadInventoryException(Failure); return Exited; }
        public string ReadStart() { Enter(); if (Failure is not null) throw new ProcessThreadInventoryException(Failure); return Start; }
    }

    private sealed class FakeSnapshot : FakeLease, IProcessThreadSnapshot
    {
        internal readonly List<ProcessThreadSnapshotEntry> Rows = new();
        internal Func<int, ProcessThreadSnapshotEntry?>? EntryAt;
        internal Action<int>? BeforeMove;
        internal int Moves;
        public bool MoveNext(out ProcessThreadSnapshotEntry entry)
        {
            Enter(); var index = Moves++; BeforeMove?.Invoke(index);
            var next = EntryAt is null ? index < Rows.Count ? Rows[index] : (ProcessThreadSnapshotEntry?)null : EntryAt(index);
            entry = next ?? default; return next.HasValue;
        }
    }

    private sealed class FakeThread : FakeLease, IProcessThreadLease
    {
        internal uint OwnerPid = Pid;
        internal string Start = ThreadStart;
        internal bool Exited;
        internal string? FailureAt;
        internal Action? OnReadOwner;
        internal Action? OnReadStart;
        private void Query(string stage) { Enter(); if (FailureAt == stage) throw new ProcessThreadInventoryException("access"); }
        public uint ReadOwnerPid() { Query("owner"); OnReadOwner?.Invoke(); return OwnerPid; }
        public bool IsExited() { Query("exited"); return Exited; }
        public string ReadStart() { Query("start"); OnReadStart?.Invoke(); return Start; }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsFixtureNative
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetCurrentThreadId();
    }
}
