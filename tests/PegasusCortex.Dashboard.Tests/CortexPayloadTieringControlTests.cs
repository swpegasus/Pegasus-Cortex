using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadTieringControlTests
{
    private static readonly LocalPeer Owner = new("S-1-5-21-10-20-30-1001", 7, @"C:\fixture\PegasusCortex.Host.exe", 71, false);
    private static int _checks;
    internal static async Task RunAsync()
    {
        _checks = 0;
        Protocol();
        await AdmissionAsync();
        await OwnershipAsync();
        await CommittedCleanupAsync();
        await FailureAndDrainAsync();
        await RefusalsAsync();
        Console.WriteLine("PASS payload owner control: " + _checks +
            " injected protocol, shared-admission/active-work drain, original backend/refresh/semantic shutdown and bounded RID-correlation checks; no SQLite, tiering, runtime, process or network action");
    }
    private static OperationRequest Request(string action = "upgrade", string? rid = null) =>
        new(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(), rid ?? Guid.NewGuid().ToString("N"),
            Body: Body(action));
    private static string Body(string action) => action == "upgrade"
        ? "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":2}"
        : JsonSerializer.Serialize(new
        {
            version = 1, action, expectedSchemaVersion = 3,
            records = new[] { new { @namespace = "project.fixture", kind = "task", recordId = "archived.fixture",
                expectedHeadRevision = 8, expectedHeadHash = new string('a', 64) } }
        });
    private static void Protocol()
    {
        foreach (var action in new[] { "upgrade", "offload", "rehydrate" })
        {
            var request = Request(action);
            var value = CortexPayloadTieringControl.ValidateRequest(request);
            Check(value.ExpectedSchemaVersion == (action == "upgrade" ? 2 : 3) &&
                value.Action.ToString().Equals(action, StringComparison.OrdinalIgnoreCase), "closed action/schema " + action);
            Check(value.Selection?.Records.Count == (action == "upgrade" ? (int?)null : 1), "closed selection " + action);
            Check(!value.ToString().Contains("project.fixture", StringComparison.Ordinal), "command diagnostics omit identities");
            var original = JsonNode.Parse(request.Body!)!.AsObject();
            var reordered = new JsonObject();
            foreach (var entry in original.Reverse()) reordered.Add(entry.Key, entry.Value?.DeepClone());
            Check(CortexPayloadTieringControl.ValidateRequest(request with { Body = reordered.ToJsonString() }).Identity == value.Identity,
                "semantic request digest independent of object field order");
            foreach (var key in new[] { "databasePath", "offline", "callback", "force" })
            {
                var bad = original.DeepClone().AsObject(); bad[key] = null;
                Refused(request with { Body = bad.ToJsonString() });
            }
        }
        var valid = Request();
        foreach (var bad in new[]
        {
            valid with { Op = "run" }, valid with { Root = "ABCD" }, valid with { Ws = "other" },
            valid with { Cwd = @"C:\other" }, valid with { PathRoots = new Dictionary<string,string> { ["fixture"] = @"C:\other" } },
            valid with { Arg = new Dictionary<string,string> { ["op"] = "upgrade" } },
            valid with { Detail = true }, valid with { Quiet = true }, valid with { Body = null },
            valid with { Rid = null }, valid with { Rid = "not-a-guid" },
            valid with { Body = "{\"version\":\"1\",\"action\":\"upgrade\",\"expectedSchemaVersion\":2}" },
            valid with { Body = "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":3}" },
            valid with { Body = "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":2,\"records\":null}" },
            valid with { Body = "{\"version\":1,\"action\":\"upgrade\",\"action\":\"offload\",\"expectedSchemaVersion\":2}" },
            valid with { Body = "{\"version\":1,\"action\":\"offload\",\"expectedSchemaVersion\":3,\"records\":[]}" }
        }) Refused(bad);
        var selected = JsonNode.Parse(Body("offload"))!.AsObject();
        var record = selected["records"]![0]!.AsObject();
        foreach (var key in new[] { "namespace", "kind", "recordId", "expectedHeadHash", "expectedHeadRevision" })
        {
            var bad = selected.DeepClone().AsObject(); bad["records"]![0]![key] = null;
            Refused(valid with { Body = bad.ToJsonString() });
        }
        foreach (var key in new[] { "password", "body", "path" })
        {
            var bad = selected.DeepClone().AsObject(); bad["records"]![0]![key] = "not-allowed";
            Refused(valid with { Body = bad.ToJsonString() });
        }
        var duplicate = selected.DeepClone().AsObject();
        duplicate["records"]!.AsArray().Add(record.DeepClone()); Refused(valid with { Body = duplicate.ToJsonString() });
        var padded = valid.Body! + new string(' ', CortexPayloadTieringControl.MaximumBodyBytes - valid.Body!.Length);
        Check(CortexPayloadTieringControl.ValidateRequest(valid with { Body = padded }).Selection is null, "body byte boundary accepted");
        Refused(valid with { Body = padded + " " });
    }

    private static async Task AdmissionAsync()
    {
        await using (var fixture = new Fixture())
        {
            var active = fixture.Admission.TryEnter() ?? throw new Exception("fixture_admission_missing");
            var request = Request();
            var pending = fixture.Control.HandleAsync(request, Owner, default);
            try
            {
                Check(!fixture.Admission.Accepting && !pending.IsCompleted && fixture.Backend.Calls == 0 &&
                    fixture.Admission.TryEnter() is null, "actual shared admission closes before waiting for existing lease");
                active.Dispose();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result.Ok && fixture.Backend.Calls == 1 && fixture.Backend.Revalidations == 1 &&
                    fixture.Environment.Inspections > 0 && fixture.Admission.Accepting, "drain then inspect then actual bound backend and refresh");
                Check(fixture.Backend.CleanupFinished && fixture.Backend.RefreshAfterCleanup && !fixture.Backend.RefreshTokenCancelable,
                    "original cleanup precedes noncancelable resident refresh");
                AssertPrivate(result);
            }
            finally { active.Dispose(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        await using (var fixture = new Fixture())
        {
            var active = fixture.Admission.TryEnter() ?? throw new Exception("fixture_admission_missing");
            using var cancel = new CancellationTokenSource();
            var pending = fixture.Control.HandleAsync(Request(), Owner, cancel.Token);
            try
            {
                cancel.Cancel();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!result.Ok && result.Err == "canceled" && !Flag(result, "operation_entered") &&
                    fixture.Backend.Calls == 0 && fixture.Backend.Revalidations == 0 && fixture.Admission.Accepting,
                    "canceled pre-entry drain restores admission without store access");
            }
            finally { active.Dispose(); }
        }
        await using (var fixture = new Fixture(TimeSpan.FromMilliseconds(40)))
        {
            fixture.Environment.Blocker = "work_active";
            var result = await fixture.Control.HandleAsync(Request(), Owner, default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!result.Ok && result.Err == "work_active" && fixture.Backend.Calls == 0 && fixture.Admission.Accepting &&
                fixture.Environment.Inspections > 0, "active deferred work prevents store entry without stopping it");
        }
    }

    private static async Task OwnershipAsync()
    {
        await using var fixture = new Fixture();
        fixture.Backend.HoldExecute = true; fixture.Backend.HoldRefresh = true; fixture.Backend.ThrowCanceled = true;
        using var cancel = new CancellationTokenSource();
        var request = Request("offload");
        var original = fixture.Control.HandleAsync(request, Owner, cancel.Token);
        try
        {
            await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = fixture.Control.HandleAsync(request, Owner, default);
            var changed = await fixture.Control.HandleAsync(request with { Body = Body("rehydrate") }, Owner, default);
            var busy = await fixture.Control.HandleAsync(Request(), Owner, default);
            Check(!original.IsCompleted && !duplicate.IsCompleted && changed.Err == "precondition" &&
                busy.Err == "payload_control_busy" && fixture.Backend.Calls == 1 && !fixture.Admission.Accepting,
                "current same RID observes original, changed body refuses, different RID cannot queue another operation");
            cancel.Cancel();
            await fixture.Backend.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!original.IsCompleted && !fixture.Admission.Accepting && fixture.Backend.Revalidations == 0,
                "caller cancellation retains original backend and gate through cleanup");
            fixture.Backend.ReleaseExecute();
            await fixture.Backend.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!original.IsCompleted && !fixture.Admission.Accepting && fixture.Backend.CleanupFinished &&
                fixture.Backend.RefreshAfterCleanup && !fixture.Backend.RefreshTokenCancelable,
                "canceled/ambiguous operation still holds gate through actual resident refresh");
            fixture.Backend.ReleaseRefresh();
            var results = await Task.WhenAll(original, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
            Check(results[0].State == "recovery_required" && results[0].Partial && Flag(results[0], "outcome_unknown") &&
                Flag(results[0], "schema_revalidated") && results[0].Jid == results[1].Jid && fixture.Admission.Accepting,
                "late original cancellation remains unknown but safely refreshed; no replay");
            var replay = await fixture.Control.HandleAsync(request, Owner, default);
            Check(replay.Jid == results[0].Jid && fixture.Backend.Calls == 1 && fixture.Backend.Revalidations == 1,
                "last in-process RID returns metadata without retrying uncertain operation");
            Check((await fixture.Control.HandleAsync(request with { Body = Body("rehydrate") }, Owner, default)).Err == "precondition",
                "last RID cannot change target action");
            AssertPrivate(replay);
        }
        finally { fixture.Backend.ReleaseExecute(); fixture.Backend.ReleaseRefresh(); await original.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CommittedCleanupAsync()
    {
        await using var fixture = new Fixture();
        fixture.Backend.CleanupIncomplete = true;
        var request = Request("offload");
        var result = await fixture.Control.HandleAsync(request, Owner, default);
        Check(!result.Ok && result.Err == "payload_cleanup_incomplete" && result.Partial && Has(result, "tiering") &&
            !Flag(result, "outcome_unknown") && Flag(result, "schema_revalidated"),
            "cleanup issue retains confirmed original result rather than inventing unknown/no-op");
        var same = await fixture.Control.HandleAsync(request, Owner, default);
        Check(same.Jid == result.Jid && fixture.Backend.Calls == 1 && fixture.Backend.Revalidations == 1,
            "same RID observes cleanup issue without replaying committed operation");
    }

    private static async Task FailureAndDrainAsync()
    {
        await using (var fixture = new Fixture())
        {
            fixture.Backend.FailRefresh = true; fixture.Runtime.HoldDrain = true;
            var request = Request();
            var operation = fixture.Control.HandleAsync(request, Owner, default);
            await fixture.Runtime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = fixture.Control.CloseAndDrainAsync();
            try
            {
                Check(fixture.Runtime.Fenced && !operation.IsCompleted && !closing.IsCompleted &&
                    !fixture.Admission.Accepting && fixture.Backend.CleanupFinished, "failed schema admission fences semantics but retains actual gate through drain ACK");
                fixture.Runtime.AcknowledgeDrain(); // Runtime signals ACK BEFORE joining control.
                var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!result.Ok && result.Err == "payload_schema_unavailable" && !Flag(result, "schema_revalidated") &&
                    Flag(result, "semantic_ingress_stopped") && fixture.Runtime.Stops == 1 && fixture.Admission.Accepting,
                    "semantic unavailable shutdown permits ordinary admission after actual drain");
                using var exit = fixture.Admission.TryClose();
                Check(exit is not null, "normal Exit can close admission after semantic failure");
            }
            finally { fixture.Runtime.AcknowledgeDrain(); await operation.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.FailRefresh = true; fixture.Runtime.HoldDrain = true; fixture.Runtime.ThrowStop = true;
            var operation = fixture.Control.HandleAsync(Request(), Owner, default);
            try
            {
                await fixture.Runtime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!operation.IsCompleted && !fixture.Admission.Accepting, "stop-request exception cannot bypass actual ingress drain");
                fixture.Runtime.AcknowledgeDrain();
                var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result.Err == "payload_schema_unavailable" && Flag(result, "semantic_ingress_stopped") &&
                    Flag(result, "semantic_stop_request_failed") &&
                    fixture.Admission.Accepting && !JsonSerializer.Serialize(result).Contains("sensitive_fixture_fault", StringComparison.Ordinal),
                    "fixed stop error preserves drain proof and ordinary Exit admission");
            }
            finally { fixture.Runtime.AcknowledgeDrain(); await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.FailExecute = true;
            var result = await fixture.Control.HandleAsync(Request("rehydrate"), Owner, default);
            Check(result.State == "recovery_required" && Flag(result, "outcome_unknown") &&
                fixture.Backend.Revalidations == 1 && fixture.Backend.RefreshAfterCleanup && fixture.Admission.Accepting &&
                fixture.Runtime.Stops == 0, "failed backend result still refreshes actual schema before reopening");
            Check(!JsonSerializer.Serialize(result).Contains("sensitive_fixture_fault", StringComparison.Ordinal), "backend exception text never echoed");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.AfterExecute = () => fixture.Environment.Peer = Owner with { Session = 8 };
            var result = await fixture.Control.HandleAsync(Request(), Owner, default);
            Check(!result.Ok && result.Err == "auth" && !Has(result, "tiering") && !Flag(result, "outcome_unknown") &&
                fixture.Backend.Revalidations == 1 && fixture.Runtime.Fenced && fixture.Admission.Accepting,
                "post-entry owner loss discards result metadata and ends semantics after refresh");
            Check((await fixture.Control.HandleAsync(Request(), Owner, default)).Err == "auth", "foreign context cannot read last result");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.HoldExecute = true;
            var operation = fixture.Control.HandleAsync(Request(), Owner, default);
            await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = fixture.Control.CloseAndDrainAsync();
            try
            {
                await fixture.Backend.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!close.IsCompleted && !operation.IsCompleted && !fixture.Admission.Accepting,
                    "runtime teardown joins original backend instead of disposing its store early");
                fixture.Backend.ReleaseExecute();
                await operation.WaitAsync(TimeSpan.FromSeconds(5)); await close.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Backend.Revalidations == 1 && fixture.Admission.Accepting, "teardown includes refresh and gate release");
            }
            finally { fixture.Backend.ReleaseExecute(); await operation.WaitAsync(TimeSpan.FromSeconds(5)); await close.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private static async Task RefusalsAsync()
    {
        await using var fixture = new Fixture();
        foreach (var peer in new[] { Owner with { Sid = "S-1-5-21-10-20-30-1002" }, Owner with { Session = 8 },
            Owner with { Elevated = true }, Owner with { Sid = "S-1-5-18", Session = 0 },
            Owner with { ApplicationPath = @"C:\fixture\Other.exe" } })
        {
            var reply = await fixture.Control.HandleAsync(Request(), peer, default);
            Check(reply.Err == "auth" && fixture.Backend.Calls == 0 && fixture.Admission.Accepting, "foreign/High/System/wrong-image peer refused before close");
        }
        var invalid = await fixture.Control.HandleAsync(Request() with { Body = "{\"databasePath\":\"forbidden\"}" }, Owner, default);
        Check(invalid.Err == "invalid" && fixture.Backend.Calls == 0 && fixture.Admission.Accepting, "invalid body before gate/store");
        using (fixture.Admission.TryClose() ?? throw new Exception("fixture_close_missing"))
            Check((await fixture.Control.HandleAsync(Request(), Owner, default)).Err == "payload_control_busy" && fixture.Backend.Calls == 0,
                "existing Exit/maintenance admission close cannot be replaced");
        fixture.Environment.MaintenanceCode = "maintenance_busy";
        Check((await fixture.Control.HandleAsync(Request(), Owner, default)).Err == "maintenance_busy" &&
            fixture.Backend.Calls == 0 && fixture.Admission.Accepting, "durable maintenance barrier retained");
        fixture.Environment.MaintenanceCode = "unexpected";
        var unreadable = await fixture.Control.HandleAsync(Request(), Owner, default);
        Check(unreadable.Err == "maintenance_state_unavailable" &&
            !JsonSerializer.Serialize(unreadable).Contains("sensitive_fixture_fault", StringComparison.Ordinal),
            "unreadable maintenance fails with fixed safe code");
        await using var unbound = new CortexPayloadTieringControl(Owner, new UserHostAdmission(), new Environment(new UserHostAdmission()));
        Check((await unbound.HandleAsync(Request(), Owner, default)).Err == "payload_store_unavailable", "uninitialized resident never creates or substitutes store");
    }

    private static void Refused(OperationRequest request)
    {
        try { CortexPayloadTieringControl.ValidateRequest(request); throw new Exception("Expected fixed payload request refusal."); }
        catch (ArgumentException) { _checks++; }
    }
    private static bool Has(OperationReceipt receipt, string name) => receipt.Data?.ContainsKey(name) == true;
    private static bool Flag(OperationReceipt receipt, string name) => receipt.Data?.TryGetValue(name, out var value) == true && value is true;
    private static void AssertPrivate(OperationReceipt receipt)
    {
        var value = JsonSerializer.Serialize(receipt);
        Check(!value.Contains("project.fixture", StringComparison.Ordinal) && !value.Contains("archived.fixture", StringComparison.Ordinal) &&
            !value.Contains(new string('a', 64), StringComparison.Ordinal) && !value.Contains(@"C:\\fixture", StringComparison.Ordinal),
            "operator receipt excludes selected coordinates, preimage hash and host/store paths");
    }
    private static void Check(bool value, string message)
    { _checks++; if (!value) throw new InvalidOperationException("Payload control fixture: " + message); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal UserHostAdmission Admission { get; } = new();
        internal Environment Environment { get; }
        internal Backend Backend { get; } = new();
        internal Lifetime Runtime { get; } = new();
        internal CortexPayloadTieringControl Control { get; }
        internal Fixture(TimeSpan? timeout = null)
        {
            Environment = new(Admission);
            Control = new(Owner, Admission, Environment, timeout); Control.BindCurrent(Backend, Runtime);
        }
        public async ValueTask DisposeAsync()
        {
            Backend.ReleaseExecute(); Backend.ReleaseRefresh(); Runtime.AcknowledgeDrain();
            await Control.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private sealed class Environment(UserHostAdmission admission) : ICortexPayloadTieringEnvironment
    {
        internal LocalPeer Peer = Owner;
        internal string? MaintenanceCode, Blocker;
        internal int Inspections;
        public LocalPeer Current => Peer;
        public void RequireMaintenanceOpen()
        {
            if (MaintenanceCode == "maintenance_busy") throw new AgentOpsException("maintenance_busy");
            if (MaintenanceCode is not null) throw new InvalidOperationException("sensitive_fixture_fault");
        }
        public Task<string?> InspectWorkAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Inspections++;
            Check(!admission.Accepting, "work inspector observes same closed admission");
            return Task.FromResult(Blocker);
        }
    }
    private sealed class Backend : ICortexPayloadTieringBackend
    {
        internal int Calls, Revalidations;
        internal bool HoldExecute, HoldRefresh, ThrowCanceled, FailExecute, FailRefresh,
            CleanupFinished, RefreshAfterCleanup, RefreshTokenCancelable, CleanupIncomplete;
        internal Action? AfterExecute;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _execute = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseExecute() => _execute.TrySetResult();
        internal void ReleaseRefresh() => _refresh.TrySetResult();
        public async ValueTask<SqliteCortexTieringReceipt> ExecuteAsync(SqliteCortexPayloadMaintenanceAction action,
            int expectedSchemaVersion, SqliteCortexTieringRequest? selection, CancellationToken token)
        {
            Calls++; Entered.TrySetResult();
            using var registration = token.Register(() => CancelObserved.TrySetResult());
            try
            {
                if (HoldExecute) await _execute.Task;
                if (FailExecute) throw new InvalidOperationException("sensitive_fixture_fault");
                if (ThrowCanceled) token.ThrowIfCancellationRequested();
                AfterExecute?.Invoke();
                return new(1, action switch { SqliteCortexPayloadMaintenanceAction.Upgrade => "upgraded",
                    SqliteCortexPayloadMaintenanceAction.Offload => "offloaded", _ => "rehydrated" },
                    3, selection?.Records.Count ?? 0, selection is null ? 0 : 1, 0, 0, 0, true)
                    { CleanupIncomplete = CleanupIncomplete };
            }
            finally { CleanupFinished = true; }
        }
        public async ValueTask<int> RevalidateAsync(CancellationToken token)
        {
            Revalidations++; RefreshAfterCleanup = CleanupFinished; RefreshTokenCancelable = token.CanBeCanceled;
            RefreshEntered.TrySetResult();
            if (HoldRefresh) await _refresh.Task;
            if (FailRefresh) throw new InvalidDataException("sensitive_fixture_fault");
            return 3;
        }
    }
    private sealed class Lifetime : ICortexPayloadTieringRuntimeLifetime
    {
        internal int Stops;
        internal bool HoldDrain, Fenced, ThrowStop;
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void StopIngress() { Stops++; Fenced = true; Stopped.TrySetResult(); if (!HoldDrain) _drained.TrySetResult(); if (ThrowStop) throw new InvalidOperationException("sensitive_fixture_fault"); }
        public Task WhenIngressDrainedAsync() => _drained.Task;
        internal void AcknowledgeDrain() => _drained.TrySetResult();
    }
}
