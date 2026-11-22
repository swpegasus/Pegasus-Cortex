using System.Text.Json;
using System.Threading.Channels;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadLifecycleControlTests
{
    private static readonly LocalPeer Owner = new("S-1-5-21-10-20-30-1001", 7, @"C:\fixture\PegasusCortex.Host.exe", 71, false);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        Protocol();
        await OptionalBindingFailureAsync();
        await DisabledAndSchemaAsync();
        await ClockRangeAsync();
        await CadenceAndBusyAsync();
        await DisableRetainsOriginalAsync();
        await UnknownAndBudgetAsync();
        await SaveCorrelationAsync();
        await PublicationReadAsync();
        await ConfirmedPublicationCleanupAsync();
        await UnchangedPublicationCleanupAsync();
        await AmbiguousPublicationAsync();
        await InvalidPublicationEvidenceAsync();
        await SaveReadSupersessionAsync();
        await ClosedUiContextAsync();
        await ShutdownOwnsConfigurationAsync();
        await ShutdownOwnsOriginalAsync();
        Console.WriteLine($"PASS payload lifecycle owner: {_checks} injected settings/protocol/ticker/backend checks; default-off, real admission reuse, schema preparation, single-pass cadence, disabling/late commit, bounded deferral, unknown pause, save correlation and shutdown join; no runtime/database/configuration actions");
    }

    private static void Protocol()
    {
        foreach (var action in new[] { "lifecycle_status", "lifecycle_run", "lifecycle_save" })
        {
            var request = Request(action);
            var parsed = CortexPayloadTieringControl.ValidateLifecycleRequest(request);
            Check(parsed.Action == action && !parsed.ToString().Contains("project.one", StringComparison.Ordinal), "typed action and passive command diagnostics");
            foreach (var suffix in new[] { ",\"path\":\"C:\\\\other\"}", ",\"force\":true}", ",\"action\":\"lifecycle_run\"}" })
                Refused(request with { Body = request.Body![..^1] + suffix });
            Refused(request with { Root = "root-code" }); Refused(request with { Cwd = @"C:\other" });
            Refused(request with { Arg = new Dictionary<string, string> { ["path"] = "other" } });
        }
        var save = Request("lifecycle_save");
        Refused(save with { Body = save.Body!.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false", StringComparison.Ordinal) });
        Refused(save with { Body = save.Body!.Replace("\"expectedPolicyRevision\":1", "\"expectedPolicyRevision\":-1", StringComparison.Ordinal) });
        Refused(Request("lifecycle_status") with { Rid = "invalid" });
    }

    private static async Task OptionalBindingFailureAsync()
    {
        var admission = new UserHostAdmission();
        var backend = new Backend(admission); var runtime = new Lifetime();
        await using var control = new CortexPayloadTieringControl(Owner, admission, new Environment(admission));
        control.BindCurrent(backend, runtime);
        var tickerCreations = 0;
        var invalidDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "payload-invalid-" + new string('x', 240)));
        control.BindOptionalLifecycleDirectory(invalidDirectory, () => { tickerCreations++; return new Ticker(); });
        var statusResult = await control.HandleAsync(Request("lifecycle_status"), Owner, default);
        var status = Status(statusResult);
        Check(statusResult.Err == "payload_policy_unavailable" && status.OwnerBound && status.Saved is null &&
            status.State == "settings_unavailable" && status.Issue == "payload_policy_binding_unavailable" && !status.PassRunning,
            "bad optional policy path preserves bound core and reports unavailable without fallback/default policy");
        var upgrade = new OperationRequest(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(),
            Guid.NewGuid().ToString("N"), Body: "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":2}");
        Check((await control.HandleAsync(upgrade, Owner, default)).Ok && backend.ManualCalls == 1 &&
            backend.Rotations == 0 && admission.Accepting && runtime.Stops == 0,
            "existing manual backend/runtime remains usable after optional configuration failure");
        var rejectedRebind = false;
        try { control.BindOptionalLifecycleDirectory(invalidDirectory); }
        catch (InvalidOperationException error) when (error.Message == "payload_lifecycle_binding_unavailable")
        { rejectedRebind = true; }
        Check(rejectedRebind, "binding programming/state errors are not swallowed by optional path handling");
        await control.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(tickerCreations == 0, "bad optional constructor starts no automatic ticker even through completed close");
    }

    private static async Task DisabledAndSchemaAsync()
    {
        await using (var fixture = new Fixture(enabled: false))
        {
            await fixture.Ticker.WaitingAsync(1);
            var status = Status(await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default));
            Check(status.State == "disabled" && status.Saved?.Policy.Enabled == false && status.StoreSchemaVersion is null,
                "saved disabled default does not invent schema observation");
            fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(2);
            Check(fixture.Backend.Rotations == 0 && fixture.Backend.Revalidations == 0, "disabled tick performs no store action");
            var refused = await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default);
            Check(refused.Err == "payload_policy_disabled" && fixture.Backend.Rotations == 0, "disabled run cannot invoke backend");
            var foreign = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner with { Session = 8 }, default);
            Check(foreign.Err == "auth" && foreign.Data is null, "foreign owner gets no saved namespace/settings");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.Schema = 2;
            var result = await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default);
            var status = Status(result);
            Check(result.Err == "payload_schema_upgrade_required" && !result.Partial && !Flag(result, "outcome_unknown") &&
                status.State == "requires_explicit_upgrade" && status.StoreSchemaVersion == 2 && fixture.Backend.Rotations == 0,
                "schema2 is known preparation requirement, never implicit migration");
            var upgrade = new OperationRequest(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(),
                Guid.NewGuid().ToString("N"), Body: "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":2}");
            Check((await fixture.Control.HandleAsync(upgrade, Owner, default)).Ok && fixture.Backend.ManualCalls == 1,
                "existing explicit manual upgrade remains separate");
            Check((await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default)).Ok && fixture.Backend.Rotations == 1,
                "prepared schema can run original configured policy");
        }
    }

    private static async Task ClockRangeAsync()
    {
        await using var fixture = new Fixture();
        fixture.Clock.Now = DateTimeOffset.MinValue;
        var result = await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default);
        Check(result.Err == "payload_archive_clock_range" && !result.Partial && !Flag(result, "outcome_unknown") &&
            fixture.Backend.Rotations == 0, "clock subtraction underflow is an explicit known-no-effects refusal");
    }

    private static async Task CadenceAndBusyAsync()
    {
        await using var fixture = new Fixture();
        await fixture.Ticker.WaitingAsync(1);
        var active = fixture.Admission.TryEnter()!;
        fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(2);
        Check(fixture.Backend.Rotations == 0 && fixture.Admission.Accepting, "automatic pass defers active owner request without waiting/closing it");
        active.Dispose();
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(3);
        Check(fixture.Backend.Rotations == 1 && fixture.Backend.Requests[0].Namespace == "project.one", "one pass begins after later cadence");
        fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(4);
        Check(fixture.Backend.Rotations == 1, "no catch-up or immediate second batch");
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(5);
        Check(fixture.Backend.Rotations == 2 && fixture.Backend.Requests[1].Namespace == "project.two", "namespaces rotate round robin once per cadence");
        Check(fixture.Backend.LargestHeads == 1024 && fixture.Backend.LargestMilliseconds == 5000,
            "actual saved work limits passed to typed backend");
        Check(fixture.Backend.AllClosedAdmission && fixture.Admission.Accepting, "every backend callback owns same closed gate and then reopens it");
    }

    private static async Task DisableRetainsOriginalAsync()
    {
        await using var fixture = new Fixture();
        fixture.Backend.HoldRotate = true; fixture.Backend.CommitAfterCancel = true;
        await fixture.Ticker.WaitingAsync(1); fixture.Ticker.Tick();
        await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = Request("lifecycle_save", fixture.Settings.Current.Policy with { Enabled = false });
        var saved = await fixture.Control.HandleAsync(save, Owner, default);
        await fixture.Backend.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(saved.Ok && Status(saved).Saved?.Policy.Enabled == false && Status(saved).PassRunning && Status(saved).State == "stopping",
            "disable saves selection and signals cancellation without falsely declaring original stopped");
        Check(!fixture.Admission.Accepting && fixture.Backend.Rotations == 1, "original retains gate and backend ownership while held");
        fixture.Backend.ReleaseRotate(); await fixture.Ticker.WaitingAsync(2);
        var status = Status(await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default));
        Check(status.State == "disabled" && !status.PassRunning && status.HotBytesEvicted == 12 && fixture.Backend.Rotations == 1,
            "late confirmed commit wins cancellation while disabled policy prevents another pass");
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1); fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(3);
        Check(fixture.Backend.Rotations == 1 && fixture.Admission.Accepting, "disabled later tick has no new backend or lost gate");
    }

    private static async Task UnknownAndBudgetAsync()
    {
        await using (var fixture = new Fixture())
        {
            fixture.Backend.FailRotate = true;
            var request = Request("lifecycle_run");
            var failed = await fixture.Control.HandleAsync(request, Owner, default);
            Check(failed.State == "recovery_required" && failed.Partial && Flag(failed, "outcome_unknown") &&
                Status(failed).State == "paused", "unknown original pauses automatic work and keeps actual outcome classification");
            var same = await fixture.Control.HandleAsync(request, Owner, default);
            Check(same.Jid == failed.Jid && fixture.Backend.Rotations == 1, "same RID only observes original failure");
            Check((await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default)).Err == "payload_lifecycle_paused",
                "new request does not silently retry uncertain pass");
            fixture.Backend.FailRotate = false;
            var review = await fixture.Control.HandleAsync(Request("lifecycle_save"), Owner, default);
            Check(review.Ok && (await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default)).Ok,
                "explicit confirmed policy save can resume after review");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.DeferredBudget = true;
            var deferred = await fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default);
            var status = Status(deferred);
            Check(deferred.Ok && !deferred.Partial && !Flag(deferred, "outcome_unknown") && status.State == "deferred_budget" &&
                status.MetadataObservations == 1024 && status.SelectedRecords is null && status.HotBytesEvicted is null &&
                status.TargetSatisfied is null && status.ImmutableObjectsMayRemain, "budget exhaustion remains explicit deferred/unknown measurements, not invented zero/success");
            Check(fixture.Backend.Rotations == 1, "one budgeted invocation only");
        }
    }

    private static async Task SaveCorrelationAsync()
    {
        await using var fixture = new Fixture(enabled: false);
        var save = Request("lifecycle_save", Policy(enabled: true));
        var first = await fixture.Control.HandleAsync(save, Owner, default);
        var same = await fixture.Control.HandleAsync(save, Owner, default);
        Check(first.Ok && first.Jid == same.Jid && fixture.Settings.Saves == 1 && Status(first).Saved?.Revision == 2,
            "same save RID returns original without another file write");
        var changed = save with { Body = JsonSerializer.Serialize(new { version = 1, action = "lifecycle_save", expectedPolicyRevision = 1,
            policy = Policy(enabled: false) }, Json) };
        Check((await fixture.Control.HandleAsync(changed, Owner, default)).Err == "precondition", "changed policy cannot reuse saved identity");
        var oldAction = save with { Body = "{\"version\":1,\"action\":\"upgrade\",\"expectedSchemaVersion\":2}" };
        Check((await fixture.Control.HandleAsync(oldAction, Owner, default)).Err == "precondition" && fixture.Backend.ManualCalls == 0,
            "known settings RID cannot become a different manual mutation");
        fixture.Settings.ThrowAfterSave = true;
        var unknownRequest = Request("lifecycle_save", Policy(enabled: false), revision: 2);
        var unknown = await fixture.Control.HandleAsync(unknownRequest, Owner, default);
        Check(unknown.State == "recovery_required" && unknown.Partial && Flag(unknown, "outcome_unknown"),
            "file save throwing after publication remains unknown, not a safe-to-repeat refusal");
        Check((await fixture.Control.HandleAsync(unknownRequest, Owner, default)).Jid == unknown.Jid && fixture.Settings.Saves == 2,
            "uncertain save is never replayed");
        Check(Status(await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default)).Saved?.Revision == 3,
            "explicit read can observe actual saved revision without submitting work");
    }

    private static async Task PublicationReadAsync()
    {
        await using var fixture = new Fixture();
        await fixture.Ticker.WaitingAsync(1);
        fixture.Settings.ReadPublicationFailure = new("payload_policy_cleanup_required", fixture.Settings.Current);
        var actual = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default);
        Check(actual.Ok && !Flag(actual, "outcome_unknown") && Flag(actual, "selected_policy_verified") &&
            Status(actual).Saved?.Revision == 1 && Status(actual).Issue == "payload_policy_cleanup_required" && Status(actual).State == "paused",
            "verified selected policy stays readable during linked cleanup with explicit proof");
        fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(2);
        Check(fixture.Backend.Rotations == 0, "enabled selected policy never runs while cleanup is paused");
        fixture.Settings.ReadPublicationFailure = null;
        var reviewed = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default);
        Check(reviewed.Ok && Flag(reviewed, "selected_policy_verified") && !Flag(reviewed, "outcome_unknown") &&
            Status(reviewed).Issue == "payload_policy_review_required" && Status(reviewed).State == "paused",
            "successful actual read after cleanup keeps automation paused for fresh reviewed save");
    }

    private static async Task ConfirmedPublicationCleanupAsync()
    {
        await using var fixture = new Fixture();
        fixture.Backend.HoldRotate = true; fixture.Backend.CommitAfterCancel = true;
        await fixture.Ticker.WaitingAsync(1); fixture.Ticker.Tick();
        await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Settings.CleanupAfterPublication = true;
        var request = Request("lifecycle_save");
        var saved = await fixture.Control.HandleAsync(request, Owner, default);
        await fixture.Backend.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!saved.Ok && saved.State == "failed" && saved.Partial && !Flag(saved, "outcome_unknown") &&
            Flag(saved, "selected_policy_verified") && Flag(saved, "policy_save_applied") &&
            saved.Err == "payload_policy_cleanup_required" && Status(saved).Saved?.Revision == 2 && Status(saved).PassRunning,
            "typed cleanup preserves confirmed new publication and original pass ownership");
        var same = await fixture.Control.HandleAsync(request, Owner, default);
        Check(same.Jid == saved.Jid && fixture.Settings.Saves == 1 && fixture.Settings.SaveCalls == 1,
            "confirmed cleanup result observes same RID without another save");
        fixture.Backend.ReleaseRotate(); await fixture.Ticker.WaitingAsync(2);
        fixture.Settings.ReadPublicationFailure = new("payload_policy_cleanup_required", fixture.Settings.Current);
        var status = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default);
        Check(Status(status).Saved is { Revision: 2, Policy.Enabled: true } && !Status(status).PassRunning &&
            Status(status).State == "paused" && Status(status).Issue == "payload_policy_cleanup_required" &&
            Flag(status, "selected_policy_verified") && Status(status).HotBytesEvicted == 12,
            "older pass completion cannot clear newer publication cleanup pause or erase its actual result");
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1); fixture.Ticker.Tick(); await fixture.Ticker.WaitingAsync(3);
        Check(fixture.Backend.Rotations == 1, "enabled policy remains paused instead of retrying after known cleanup");
    }

    private static async Task UnchangedPublicationCleanupAsync()
    {
        await using var fixture = new Fixture(enabled: false);
        fixture.Settings.CleanupBeforePublication = true;
        var request = Request("lifecycle_save", Policy(enabled: true));
        var refused = await fixture.Control.HandleAsync(request, Owner, default);
        Check(!refused.Ok && !refused.Partial && !Flag(refused, "outcome_unknown") &&
            Flag(refused, "selected_policy_verified") && !Flag(refused, "policy_save_applied") &&
            refused.Err == "payload_policy_cleanup_required" && Status(refused).Saved is { Revision: 1, Policy.Enabled: false },
            "verified old selected snapshot is not mislabeled as this requested new publication");
        Check(fixture.Settings.Saves == 0 && fixture.Settings.SaveCalls == 1 &&
            (await fixture.Control.HandleAsync(request, Owner, default)).Jid == refused.Jid && fixture.Settings.SaveCalls == 1,
            "known unchanged cleanup is retained without replay or fabricated new settings");
    }

    private static async Task AmbiguousPublicationAsync()
    {
        await using var fixture = new Fixture();
        fixture.Settings.RecoveryAfterPublication = true;
        var request = Request("lifecycle_save");
        var unknown = await fixture.Control.HandleAsync(request, Owner, default);
        Check(unknown.State == "recovery_required" && unknown.Partial && Flag(unknown, "outcome_unknown") &&
            !Flag(unknown, "selected_policy_verified") && Status(unknown).Saved is null &&
            Status(unknown).Issue == "payload_policy_recovery_required",
            "ambiguous selected state never falls back to cached or default settings");
        var same = await fixture.Control.HandleAsync(request, Owner, default);
        Check(same.Jid == unknown.Jid && fixture.Settings.SaveCalls == 1 && fixture.Settings.Current.Revision == 2,
            "unknown publication retains its original identity despite actual fixture write");
        fixture.Settings.ReadPublicationFailure = new("payload_policy_recovery_required");
        var unavailable = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default);
        Check(unavailable.State == "recovery_required" && Flag(unavailable, "outcome_unknown") &&
            !Flag(unavailable, "selected_policy_verified") && Status(unavailable).Saved is null,
            "unknown recovery read exposes no selected-state proof");
        fixture.Settings.ReadPublicationFailure = null;
        var refreshed = await fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default);
        Check(refreshed.Ok && !Flag(refreshed, "outcome_unknown") && Flag(refreshed, "selected_policy_verified") &&
            Status(refreshed).Saved?.Revision == 2 && Status(refreshed).Issue == "payload_policy_review_required" &&
            Status(refreshed).State == "paused", "fresh verified actual read exposes current state for deliberate CAS review only");
        Check((await fixture.Control.HandleAsync(request, Owner, default)).Jid == unknown.Jid && fixture.Settings.SaveCalls == 1,
            "fresh selected-state proof does not rewrite original unknown receipt");
        fixture.Settings.RecoveryAfterPublication = false;
        var confirmed = await fixture.Control.HandleAsync(Request("lifecycle_save", Policy(enabled: false), revision: 2), Owner, default);
        Check(confirmed.Ok && Status(confirmed).Saved is { Revision: 3, Policy.Enabled: false },
            "review uses a fresh CAS operation rather than replaying uncertain save");
    }

    private static async Task InvalidPublicationEvidenceAsync()
    {
        foreach (var failure in new[]
        {
            new CortexPayloadLifecycleSettingsPublicationException("unrecognized_source_code", new(1, Policy())),
            new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", new(1, Policy() with { Version = 2 })),
            new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", new(2, Policy(enabled: false))),
            new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required"),
            new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", CortexPayloadLifecycleSettingsStore.DefaultSettings),
            new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", new(9, Policy()))
        })
        {
            await using var fixture = new Fixture();
            fixture.Settings.SavePublicationFailure = failure;
            var result = await fixture.Control.HandleAsync(Request("lifecycle_save"), Owner, default);
            Check(result.State == "recovery_required" && Flag(result, "outcome_unknown") &&
                !Flag(result, "selected_policy_verified") && Status(result).Saved is null && fixture.Settings.Saves == 0,
                "unknown/missing/default/mismatched publication evidence cannot prove a new save");
        }
    }

    private static async Task SaveReadSupersessionAsync()
    {
        foreach (var oldReadKind in new[] { "normal", "cleanup", "recovery" })
        {
            await using var fixture = new Fixture(enabled: false);
            fixture.Settings.HoldRead = true;
            if (oldReadKind == "cleanup")
                fixture.Settings.ReadPublicationFailure = new("payload_policy_cleanup_required", fixture.Settings.Current);
            if (oldReadKind == "recovery")
                fixture.Settings.ReadPublicationFailure = new("payload_policy_recovery_required");
            var earlierRead = Task.Run(() => fixture.Control.HandleAsync(Request("lifecycle_status"), Owner, default));
            await fixture.Settings.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var saved = await fixture.Control.HandleAsync(Request("lifecycle_save", Policy(enabled: true)), Owner, default);
                Check(saved.Ok && Status(saved).Saved?.Revision == 2, "new save publishes actual revision while older read is held: " + oldReadKind);
            }
            finally { fixture.Settings.ReleaseRead(); }
            var observed = Status(await earlierRead.WaitAsync(TimeSpan.FromSeconds(5)));
            fixture.Settings.HoldRead = false; fixture.Settings.ReadPublicationFailure = null;
            Check(observed.Saved is { Revision: 2, Policy.Enabled: true } && observed.Issue is null,
                "old read completion or typed publication failure cannot replace confirmed newer selection: " + oldReadKind);
        }
    }

    private static async Task ClosedUiContextAsync()
    {
        var previous = SynchronizationContext.Current;
        var abandoned = new AbandonedContext();
        Fixture fixture; Task<OperationReceipt> original;
        try
        {
            SynchronizationContext.SetSynchronizationContext(abandoned);
            fixture = new Fixture(enabled: false);
            original = fixture.Control.HandleAsync(Request("lifecycle_save"), Owner, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await using var owned = fixture;
        Check((await original.WaitAsync(TimeSpan.FromSeconds(5))).Ok && abandoned.Posts == 0,
            "saved publication and resident loop never depend on an abandoned tray synchronization context");
    }

    private static async Task ShutdownOwnsConfigurationAsync()
    {
        await using var fixture = new Fixture(enabled: false);
        fixture.Settings.HoldSave = true;
        var original = fixture.Control.HandleAsync(Request("lifecycle_save"), Owner, default);
        await fixture.Settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = fixture.Control.CloseAndDrainAsync();
        Check(!closing.IsCompleted && !original.IsCompleted, "shutdown retains already-admitted settings publication");
        fixture.Settings.ReleaseSave();
        var saved = await original.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(saved.Ok && Status(saved).Saved?.Revision == 2 && fixture.Settings.Saves == 1 && fixture.Ticker.Disposed,
            "configuration original and ticker join before close acknowledges");
    }

    private static async Task ShutdownOwnsOriginalAsync()
    {
        await using var fixture = new Fixture();
        fixture.Backend.HoldRotate = true; fixture.Backend.HoldRefresh = true; fixture.Backend.CommitAfterCancel = true;
        var original = fixture.Control.HandleAsync(Request("lifecycle_run"), Owner, default);
        await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = fixture.Control.CloseAndDrainAsync();
        await fixture.Backend.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!closing.IsCompleted && !original.IsCompleted && !fixture.Admission.Accepting,
            "shutdown retains original backend and gate after cancellation signal");
        fixture.Backend.ReleaseRotate();
        await fixture.Backend.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!closing.IsCompleted && !original.IsCompleted && !fixture.Backend.RefreshTokenCancelable,
            "shutdown joins actual noncancelable schema refresh after original commit");
        fixture.Backend.ReleaseRefresh();
        var result = await original.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Ok && !Flag(result, "outcome_unknown") && fixture.Backend.Rotations == 1 && fixture.Ticker.Disposed,
            "late confirmed result and ticker/original ownership settle before controller close");
    }

    private static CortexPayloadLifecyclePolicy Policy(bool enabled = true) => new(1, enabled,
        ["project.one", "project.two"], 30, 0, 5, 5000, 1024);
    private static OperationRequest Request(string action, CortexPayloadLifecyclePolicy? policy = null, long revision = 1) =>
        new(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(), Guid.NewGuid().ToString("N"), Body:
            action == "lifecycle_save" ? JsonSerializer.Serialize(new { version = 1, action, expectedPolicyRevision = revision, policy = policy ?? Policy() }, Json) :
                JsonSerializer.Serialize(new { version = 1, action }, Json));
    private static CortexPayloadLifecycleStatus Status(OperationReceipt value) =>
        value.Data?.GetValueOrDefault("lifecycle") as CortexPayloadLifecycleStatus ?? throw new InvalidOperationException("missing actual lifecycle status");
    private static bool Flag(OperationReceipt value, string key) => value.Data?.GetValueOrDefault(key) is true;
    private static void Check(bool value, string name) { Interlocked.Increment(ref _checks); if (!value) throw new InvalidOperationException("Lifecycle owner fixture: " + name); }
    private static void Refused(OperationRequest request)
    {
        try { CortexPayloadTieringControl.ValidateLifecycleRequest(request); } catch (ArgumentException) { _checks++; return; }
        throw new InvalidOperationException("Expected lifecycle protocol refusal");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal UserHostAdmission Admission { get; } = new();
        internal Settings Settings { get; }
        internal Clock Clock { get; } = new();
        internal Ticker Ticker { get; } = new();
        internal Backend Backend { get; }
        internal CortexPayloadTieringControl Control { get; }
        internal Fixture(bool enabled = true)
        {
            Settings = new(new(1, Policy(enabled)));
            Backend = new(Admission);
            Control = new(Owner, Admission, new Environment(Admission));
            Control.BindCurrent(Backend, new Lifetime());
            Control.BindLifecycle(Settings, Clock, () => Ticker);
        }
        public async ValueTask DisposeAsync()
        {
            Settings.ReleaseRead(); Settings.ReleaseSave();
            Backend.ReleaseRotate(); Backend.ReleaseRefresh();
            await Control.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private sealed class Settings(CortexPayloadLifecycleSettings initial) : ICortexPayloadLifecycleSettingsStore
    {
        internal CortexPayloadLifecycleSettings Current = initial;
        internal int Saves, SaveCalls;
        internal bool ThrowAfterSave, HoldRead, HoldSave, CleanupAfterPublication, CleanupBeforePublication, RecoveryAfterPublication;
        internal CortexPayloadLifecycleSettingsPublicationException? ReadPublicationFailure, SavePublicationFailure;
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _save = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseRead() => _read.TrySetResult();
        internal void ReleaseSave() => _save.TrySetResult();
        public CortexPayloadLifecycleSettings Read()
        {
            var captured = Current; var publicationFailure = ReadPublicationFailure;
            if (HoldRead) { ReadEntered.TrySetResult(); _read.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            if (publicationFailure is not null) throw publicationFailure;
            return captured;
        }
        public CortexPayloadLifecycleSettings Save(CortexPayloadLifecyclePolicy policy, long expectedRevision)
        {
            SaveCalls++;
            if (Current.Revision != expectedRevision) throw new InvalidOperationException("payload_policy_changed");
            if (HoldSave) { SaveEntered.TrySetResult(); _save.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            if (SavePublicationFailure is not null) throw SavePublicationFailure;
            if (CleanupBeforePublication) throw new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", Current);
            Saves++; Current = new(Current.Revision + 1, CortexPayloadLifecycleSettingsStore.Validate(policy));
            if (CleanupAfterPublication) throw new CortexPayloadLifecycleSettingsPublicationException("payload_policy_cleanup_required", Current);
            if (RecoveryAfterPublication) throw new CortexPayloadLifecycleSettingsPublicationException("payload_policy_recovery_required");
            if (ThrowAfterSave) throw new IOException("sensitive fixture after-publication fault");
            return Current;
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-05-01T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AbandonedContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }
    private sealed class Ticker : ICortexPayloadLifecycleTicker
    {
        private readonly Channel<bool> _ticks = Channel.CreateBounded<bool>(16);
        private readonly Channel<int> _waits = Channel.CreateBounded<int>(16);
        private int _waitCount;
        internal bool Disposed;
        public ValueTask<bool> WaitAsync(CancellationToken token)
        { _waits.Writer.TryWrite(++_waitCount); return _ticks.Reader.ReadAsync(token); }
        internal void Tick() { if (!_ticks.Writer.TryWrite(true)) throw new InvalidOperationException("fixture tick backlog"); }
        internal async Task WaitingAsync(int count)
        { while (await _waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) < count) { } }
        public ValueTask DisposeAsync() { Disposed = true; _ticks.Writer.TryComplete(); _waits.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Environment(UserHostAdmission admission) : ICortexPayloadTieringEnvironment
    {
        public LocalPeer Current => Owner;
        public void RequireMaintenanceOpen() { }
        public Task<string?> InspectWorkAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(!admission.Accepting, "inspector sees existing closed admission"); return Task.FromResult<string?>(null); }
    }
    private sealed class Lifetime : ICortexPayloadTieringRuntimeLifetime
    { internal int Stops; public void StopIngress() { Stops++; } public Task WhenIngressDrainedAsync() => Task.CompletedTask; }
    private sealed class Backend(UserHostAdmission admission) : ICortexPayloadTieringBackend
    {
        internal int Schema = 3, Rotations, Revalidations, ManualCalls, LargestHeads, LargestMilliseconds;
        internal bool HoldRotate, HoldRefresh, CommitAfterCancel, FailRotate, DeferredBudget, RefreshTokenCancelable;
        internal bool AllClosedAdmission = true;
        internal readonly List<SqliteCortexPayloadRotationRequest> Requests = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _rotate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseRotate() => _rotate.TrySetResult();
        internal void ReleaseRefresh() => _refresh.TrySetResult();
        public ValueTask<SqliteCortexTieringReceipt> ExecuteAsync(SqliteCortexPayloadMaintenanceAction action,
            int expectedSchemaVersion, SqliteCortexTieringRequest? selection, CancellationToken token)
        { ManualCalls++; Schema = 3; return ValueTask.FromResult(new SqliteCortexTieringReceipt(1, "upgraded", 3, 0, 0, 0, 0, 0, true)); }
        public async ValueTask<int> RevalidateAsync(CancellationToken token)
        {
            Revalidations++;
            if (Rotations != 0)
            {
                RefreshTokenCancelable = token.CanBeCanceled; RefreshEntered.TrySetResult();
                if (HoldRefresh) await _refresh.Task;
            }
            return Schema;
        }
        public async ValueTask<SqliteCortexPayloadRotationReceipt> RotateAsync(SqliteCortexPayloadRotationRequest request,
            int maximumMetadataHeads, int workMilliseconds, CancellationToken token)
        {
            Rotations++; Requests.Add(request); AllClosedAdmission &= !admission.Accepting;
            LargestHeads = Math.Max(LargestHeads, maximumMetadataHeads); LargestMilliseconds = Math.Max(LargestMilliseconds, workMilliseconds);
            using var registration = token.Register(() => CancellationObserved.TrySetResult());
            Entered.TrySetResult();
            if (HoldRotate) await _rotate.Task;
            if (FailRotate) throw new IOException("sensitive fixture backend failure");
            if (!CommitAfterCancel) token.ThrowIfCancellationRequested();
            if (DeferredBudget)
                return new(1, "deferred_budget", new(1, request.Namespace, null, null, null, null, null, null, request.After),
                    null, null, null, "payload_work_budget_exhausted")
                    { MetadataObservations = maximumMetadataHeads, ImmutableObjectsMayRemain = true };
            if (CommitAfterCancel)
            {
                var selection = new SqliteCortexTieringRequest(1, [new(request.Namespace, "task", "archived", 1, new string('a', 64))]);
                return new(1, "rotated", new(1, request.Namespace, 1, 0, 0, 0, 20, selection, null),
                    new(1, "offloaded", 3, 1, 1, 0, 12, 0, true), 8, false) { MetadataObservations = 8 };
            }
            return new(1, "no_selection", new(1, request.Namespace, 1, 0, 0, 0, 100, null, null),
                null, 100, false) { MetadataObservations = 3 };
        }
    }
}
