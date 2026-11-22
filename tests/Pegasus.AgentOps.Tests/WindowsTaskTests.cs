using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Injected Scheduler/session/task boundaries only. RunAsync never activates COM,
// connects to Scheduler, registers a task, launches an executable, or changes ACLs.
internal static class WindowsTaskTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    internal const string EngineFixtureTaskPath = @"\CortexFixture\Task";
    internal const string EngineFixtureInstanceGuid = "11111111-2222-3333-4444-555555555555";
    internal static readonly string EngineFixtureTaskSha = new('a', 64);
    private static readonly string OtherSha = new('b', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _assertions;

    internal static EngineFixture CreateEngineFixture(string ownerSid, Func<LocalPeer> current,
        WindowsTaskAuthority authority = WindowsTaskAuthority.Owner) => new(ownerSid, current, authority);
    internal static WindowsTaskOperations CreateEngineFixtureOperations(string ownerSid, Func<LocalPeer> current) =>
        CreateEngineFixture(ownerSid, current).Operations;

    internal static async Task RunAsync()
    {
        _assertions = 0;
        Contract();
        ReadAndPaging();
        ExistingControls();
        AuthoringAndPins();
        await CancellationAndOwnerAsync();
        LocalDatesAndQuoting();
        Console.WriteLine("PASS Windows tasks: " + _assertions +
            " injected contract/Scheduler-boundary checks; owner/System fences, definition/security pins, exact-instance stop, run observations, bounded pages, typed disabled authoring, lease/reference cleanup and original-worker cancellation; no actual COM or task action");
    }

    private static void Contract()
    {
        Check(WindowsTaskOperationContract.Actions.SequenceEqual(new[]
            { "list", "read", "instances", "prepare", "run", "stop", "enable", "disable", "remove", "create", "update" }), "eleven closed actions");
        foreach (var action in WindowsTaskOperationContract.Actions)
        {
            var value = Parse(action);
            Check(value.Action == action && value.IsMutation == !WindowsTaskOperationContract.IsReadOnly(action), "classification");
            Check(value.RequestIdentity.Length == 64 && !value.ToString().Contains("toolSha", StringComparison.Ordinal), "opaque request identity");
            var body = JsonNode.Parse(Body(action))!.AsObject();
            var reversed = new JsonObject();
            foreach (var pair in body.Reverse()) reversed.Add(pair.Key, pair.Value?.DeepClone());
            Check(value.RequestIdentity == Parse(action, reversed.ToJsonString()).RequestIdentity, "property order identity");
            foreach (var field in new[] { "machine", "user", "password", "principal", "xml", "sddl", "parameters", "context", "flags" })
            {
                var extra = JsonNode.Parse(Body(action))!.AsObject(); extra[field] = null;
                Refuse(() => Parse(action, extra.ToJsonString()));
            }
            Refuse(() => Parse(action, Body(action).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
            if (value.IsMutation)
            {
                Refuse(() => WindowsTaskOperationContract.Parse(Request(action) with { Rid = null }));
                Check(WindowsTaskOperationContract.Parse(Request(action) with
                    { Arg = new Dictionary<string, string> { ["op"] = action, ["wait"] = "false" } }).Wait == false, "deferred mutation");
            }
            else Refuse(() => WindowsTaskOperationContract.Parse(Request(action) with
                { Arg = new Dictionary<string, string> { ["op"] = action, ["wait"] = "true" } }));
            Check(WindowsTaskOperationContract.Parse(Request(action) with { System = false }).RequestIdentity == value.RequestIdentity,
                "false System intent changed ordinary task identity");
            if (WindowsTaskOperationContract.IsSystemAction(action))
                Check(WindowsTaskOperationContract.Parse(Request(action) with { System = true }).Action == action, "explicit System shape");
            else Refuse(() => WindowsTaskOperationContract.Parse(Request(action) with { System = true }));
            Refuse(() => WindowsTaskOperationContract.Parse(Request(action) with { Root = "AB12" }));
        }
        foreach (var action in new[] { "list", "prepare", "create", "update" })
        {
            var field = action == "list" ? "max" : "toolVersion";
            foreach (var bad in new JsonNode?[] { JsonValue.Create("1"), null, JsonValue.Create(true) })
            {
                var root = JsonNode.Parse(Body(action))!.AsObject(); root[field] = bad?.DeepClone();
                Refuse(() => Parse(action, root.ToJsonString()));
            }
        }
        foreach (var bad in new JsonNode?[] { JsonValue.Create("1"), null, JsonValue.Create(false) })
        {
            var root = JsonNode.Parse(Body("read"))!.AsObject(); root["version"] = bad?.DeepClone();
            Refuse(() => Parse("read", root.ToJsonString()));
            var authored = JsonNode.Parse(Body("create"))!.AsObject();
            authored["schedule"] = new JsonObject { ["kind"] = "daily", ["startUtc"] = "2026-11-22T00:00:00.0000000Z", ["daysInterval"] = bad?.DeepClone() };
            Refuse(() => Parse("create", authored.ToJsonString()));
        }
        foreach (var path in new[] { "", "relative", @"\", @"\a\..\b", @"\a\\b", @"\a\b\", @"\a\*", @"\a\ b", "\\a\\b\0", new string('x', 241) })
            Refuse(() => WindowsTaskOperationContract.RequirePath(path));
        Check(Parse("list", "{\"version\":1}").Folder == @"\" && Parse("list", "{\"version\":1}").Maximum == 32, "folder defaults");
        Check(Parse("stop").InstanceGuid == EngineFixtureInstanceGuid, "exact canonical instance");
        foreach (var expansion in new[] { "%PATH%", "$(Arg0)", "$(arg31)", "x$(ARGother)" })
        {
            var body = JsonNode.Parse(Body("create"))!.AsObject(); body["argv"] = new JsonArray(expansion);
            Refuse(() => Parse("create", body.ToJsonString()));
        }
        Refuse(() => Parse("prepare", Body("prepare").Replace("\"scheduler\"", "\"registered\"", StringComparison.Ordinal)));
        Refuse(() => Parse("create", Body("create").Replace(",\"toolSha\":\"" + OtherSha + "\"", "", StringComparison.Ordinal)));
        Refuse(() => WindowsTaskOperationContract.Parse(Request("read") with { Ws = "fixture" }));
        Refuse(() => WindowsTaskOperationContract.Parse(Request("read") with { Cwd = @"C:\fixture" }));
    }

    private static void ReadAndPaging()
    {
        var fixture = New();
        Check(fixture.Operations.IsCurrentContext() && fixture.Platform.Connects == 0, "constructor is passive");
        var read = fixture.Operations.Execute(Parse("read"));
        Check(read.Ok && read.Current?.TaskSha == EngineFixtureTaskSha && read.Current.PrincipalKind == "owner_interactive_limited", "exact metadata");
        Check(fixture.NativeCalls == 0 && fixture.Platform.Outstanding == 0, "read cleanup/no mutation");
        var list = fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\",\"max\":1}"));
        Check(list.Ok && list.Page?.Items.Single().Path == EngineFixtureTaskPath && list.Page.HasMore == false, "one exact folder page");
        fixture.Platform.Count = 4000;
        var first = fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\",\"max\":2}"));
        Check(first.Ok && first.Page is { HasMore: true, Items.Count: 2 } && first.Page.Next == @"\CortexFixture\Task00001", "forward ordered page");
        var after = fixture.Operations.Execute(Parse("list", JsonSerializer.Serialize(new
            { version = 1, folder = @"\CortexFixture", max = 2, after = first.Page!.Next }, Json)));
        Check(after.Page?.Items[0].Path == @"\CortexFixture\Task00002" && after.Page.Items[1].Path == @"\CortexFixture\Task00003", "continuation no dropped row");
        fixture.Platform.Count = 4097;
        var bound = fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\"}"));
        Check(!bound.Ok && bound.Error == "task_enumeration_bound" && bound.Page is { HasMore: true, BoundReached: true, Next: null }, "count bound honest");
        fixture.Platform.Count = 64; fixture.Platform.LongState = new string('x', 16000);
        var bytePage = fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\",\"max\":64}"));
        Check(bytePage.Ok && bytePage.Page is { HasMore: true } && bytePage.Page.Items.Count < 64 &&
            JsonSerializer.SerializeToUtf8Bytes(bytePage, Json).Length <= WindowsTaskOperationContract.MaximumPageBytes, "closed serialization budget");
        fixture.Platform.LongState = null; fixture.Platform.Count = 1; fixture.Platform.ChangeCollection = true;
        Check(fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\"}")).Error == "task_collection_changed", "collection drift");
        fixture.Platform.ChangeCollection = false; fixture.Platform.BadItemPath = true;
        var changed = fixture.Operations.Execute(Parse("list", "{\"version\":1,\"folder\":\"\\\\CortexFixture\"}"));
        Check(!changed.Ok && changed.Page is null, "unidentifiable boundary no cursor");
        fixture.Platform.BadItemPath = false; fixture.Platform.VisibleInstances = 0; fixture.Platform.HiddenInstances = 5;
        var empty = fixture.Operations.Execute(Parse("instances"));
        Check(empty.Ok && empty.Instances is { Items.Count: 0, ObservedCount: 0, Visibility: "caller_context_visible_instances_only" } &&
            empty.Observation is null && fixture.NativeCalls == 0, "filtered empty not globally idle");
        fixture.Platform.VisibleInstances = 10000;
        var boundedInstances = fixture.Operations.Execute(Parse("instances"));
        Check(boundedInstances.Ok && boundedInstances.Instances is { Items.Count: 64, HasMore: true, BoundReached: true }, "instance projection bound");
        Check(fixture.Platform.Outstanding == 0, "all collection/task/session leases closed");
    }

    private static void ExistingControls()
    {
        foreach (var action in new[] { "run", "stop", "enable", "disable", "remove" })
        {
            var fixture = New();
            var result = fixture.Operations.Execute(Parse(action));
            Check(result.Ok && result.ActionIssued && !result.OutcomeUnknown && fixture.NativeCalls == 1, "single mutation " + action);
            Check(fixture.Platform.Outstanding == 0, "control cleanup " + action);
            if (action == "run") Check(result.Instance?.InstanceGuid == EngineFixtureInstanceGuid &&
                result.Observation == "instance_observed_not_completion", "run only instance observation");
            if (action == "stop") Check(fixture.Platform.StoppedInstance == EngineFixtureInstanceGuid && fixture.NativeCalls == 1, "one exact instance only");
            if (action == "disable") Check(result.Observation == "disabled_running_instances_not_stopped", "disable does not stop");
            if (action == "remove") Check(result.Current is null && result.Observation == "registration_absent_running_instances_not_stopped", "remove no stop/cleanup");
        }
        var stale = New(); stale.Platform.Sha = OtherSha;
        Check(stale.Operations.Execute(Parse("run")).Error == "precondition" && stale.NativeCalls == 0, "wrong pinned preimage");
        var changed = New(); changed.Platform.ChangeAtRead = 2;
        Check(changed.Operations.Execute(Parse("enable")).Error == "precondition" && changed.NativeCalls == 0, "pre-entry definition/security drift");
        var disabled = New(); disabled.Platform.Enabled = false;
        Check(disabled.Operations.Execute(Parse("run")).Error == "task_not_demand_startable" && disabled.NativeCalls == 0, "disabled run no S_OK inference");
        var demand = New(); demand.Platform.AllowDemand = false;
        Check(demand.Operations.Execute(Parse("run")).Error == "task_not_demand_startable" && demand.NativeCalls == 0, "demand-disabled no entry");
        var different = New(); different.Platform.Principal = "unverified_principal";
        Check(different.Operations.Execute(Parse("run")).Error == "task_identity_requires_explicit_authority" && different.NativeCalls == 0, "no principal-name lookup/fallback");
        var absent = New(); absent.Platform.VisibleInstances = 0;
        var noInstance = absent.Operations.Execute(Parse("stop"));
        Check(!noInstance.Ok && !noInstance.ActionIssued && noInstance.Error == "instance_not_found_in_caller_context", "exact instance absent no all-stop");
        var wrong = New(); wrong.Platform.InstancePath = @"\Other\Task";
        Check(!wrong.Operations.Execute(Parse("stop")).ActionIssued && wrong.NativeCalls == 0, "instance task path pin");
        var warning = New(); warning.FailureStatus = 0x0004131B;
        var warned = warning.Operations.Execute(Parse("run"));
        Check(warned.Ok && warned.Warning == "scheduler_warning" && warned.NativeError == 0x0004131B, "positive HRESULT retained");
        var noReturnedInstance = New(); noReturnedInstance.Platform.ReturnInstance = false;
        var acknowledged = noReturnedInstance.Operations.Execute(Parse("run"));
        Check(acknowledged.Ok && acknowledged.Instance is null && acknowledged.Observation == "run_request_acknowledged_instance_not_observed", "no invented started instance");
        var denied = New(); denied.FailureStatus = unchecked((int)0x80070005);
        var failure = denied.Operations.Execute(Parse("run"));
        Check(!failure.Ok && failure.ActionIssued && failure.OutcomeUnknown && failure.NativeError == unchecked((int)0x80070005), "native error numeric/no replay");
        var system = new EngineFixture(Sid, () => new("S-1-5-18", 0, Elevated: true), WindowsTaskAuthority.System);
        system.Platform.Principal = "different_or_noninteractive_identity";
        Check(system.Operations.IsCurrentContext() && system.Operations.Authority == WindowsTaskAuthority.System &&
            system.Operations.Execute(Parse("enable")).Ok, "explicit System existing-task authority");
        Check(system.Operations.Execute(Parse("create")).Error == "task_authoring_requires_ordinary_owner" &&
            system.Operations.Execute(Parse("prepare")).Error == "task_authoring_requires_ordinary_owner" &&
            system.Operations.Execute(Parse("update")).Error == "task_authoring_requires_ordinary_owner", "System authoring denied");
        var invalidSystem = new EngineFixture(Sid, () => new(Sid, 7, Elevated: true), WindowsTaskAuthority.System);
        Check(!invalidSystem.Operations.IsCurrentContext() && invalidSystem.Operations.Execute(Parse("read")).Error == "owner_denied", "role is not identity substitution");
        var high = new EngineFixture(Sid, () => new(Sid, 7, Elevated: true), WindowsTaskAuthority.OwnerAdmin);
        Check(high.Operations.Execute(Parse("disable")).Ok && high.Operations.Execute(Parse("create")).Error == "task_authoring_requires_ordinary_owner", "explicit High existing control only");
    }

    private static void AuthoringAndPins()
    {
        var prepare = New();
        var discovered = prepare.Operations.Execute(Parse("prepare"));
        Check(discovered.Ok && discovered.Tool?.DefinitionVersion == 3 && prepare.Platform.Connects == 0 && prepare.NativeCalls == 0 &&
            prepare.Platform.ToolOutstanding == 0, "read-only pin discovery/no COM");
        var create = New(); create.Platform.Exists = false;
        var created = create.Operations.Execute(Parse("create"));
        Check(created.Ok && created.ActionIssued && created.Current is { Enabled: false, ActionCount: 1, PrincipalKind: "owner_interactive_limited" } &&
            create.NativeCalls == 1 && create.Platform.ToolHeldDuringRegister && create.Platform.ToolHeldDuringReadback &&
            create.Platform.ToolOutstanding == 0 && create.Platform.Outstanding == 0, "disabled typed create/lease through readback");
        var update = New(); var updated = update.Operations.Execute(Parse("update"));
        Check(updated.Ok && updated.Current?.Enabled == false && update.NativeCalls == 1 && update.Platform.RegisterWasCreate == false, "update-only supported owner task");
        var occupied = New();
        Check(occupied.Operations.Execute(Parse("create")).Error == "precondition" && occupied.NativeCalls == 0 && occupied.Platform.ToolPreparations == 0, "create refuses occupied before tool resolution");
        var missing = New(); missing.Platform.Exists = false;
        Check(missing.Operations.Execute(Parse("update")).Error == "not_found" && missing.Platform.ToolPreparations == 0, "update never creates");
        var unsupported = New(); unsupported.Platform.AuthoringSupported = false;
        Check(unsupported.Operations.Execute(Parse("update")).Error == "task_definition_not_supported" && unsupported.NativeCalls == 0, "unsupported definition no rewrite");
        var stale = New(); stale.Platform.Pin = new("fixture", 4, OtherSha); stale.Platform.Exists = false;
        Check(stale.Operations.Execute(Parse("create")).Error == "task_tool_precondition" && stale.NativeCalls == 0 && stale.Platform.ToolOutstanding == 0, "wrong tool version pin");
        var expansion = New(); expansion.Platform.Executable = @"C:\%SystemRoot%\tool.exe"; expansion.Platform.Exists = false;
        Check(expansion.Operations.Execute(Parse("create")).Error == "task_tool_precondition" && expansion.NativeCalls == 0, "image expansion refused");
        var cwd = New(); cwd.Platform.WorkingDirectory = @"C:\fixture\$(Arg0)"; cwd.Platform.Exists = false;
        Check(cwd.Operations.Execute(Parse("create")).Error == "task_tool_precondition" && cwd.NativeCalls == 0, "cwd expansion refused");
        var badMode = New(); badMode.Platform.ToolMode = ToolExecutionMode.System; badMode.Platform.Exists = false;
        Check(badMode.Operations.Execute(Parse("create")).Error == "task_tool_precondition" && badMode.NativeCalls == 0, "no System tool authoring");
        var corruptReadback = New(); corruptReadback.Platform.Exists = false; corruptReadback.Platform.BadRegistrationReadback = true;
        var unknown = corruptReadback.Operations.Execute(Parse("create"));
        Check(!unknown.Ok && unknown.ActionIssued && unknown.OutcomeUnknown && corruptReadback.NativeCalls == 1 &&
            corruptReadback.Platform.Exists && corruptReadback.Platform.ToolOutstanding == 0 && corruptReadback.Platform.Outstanding == 0, "unknown registration no delete/compensation");
    }

    private static async Task CancellationAndOwnerAsync()
    {
        var cancelled = New(); using var before = new CancellationTokenSource(); before.Cancel();
        var noEntry = cancelled.Operations.Execute(Parse("run"), before.Token);
        Check(!noEntry.ActionIssued && noEntry.Error == "cancelled" && cancelled.Platform.Connects == 0, "pre-entry cancellation");
        using var entered = new CancellationTokenSource();
        var held = New(); held.HoldCall = true;
        var original = Task.Run(() => held.Operations.Execute(Parse("enable"), entered.Token), CancellationToken.None);
        try
        {
            await held.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            entered.Cancel();
            Check(!original.IsCompleted && held.NativeCalls == 1 && held.Platform.Outstanding > 0, "cancel does not abandon native worker/resources");
        }
        finally { held.Release(); }
        var terminal = await original.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!terminal.Ok && terminal.ActionIssued && terminal.OutcomeUnknown && terminal.Error == "cancelled" &&
            held.Platform.Enabled && held.NativeCalls == 1 && held.Platform.Outstanding == 0, "late actual effect retained unknown/no replay");
        var owner = new LocalPeer(Sid, 7);
        var lost = new EngineFixture(Sid, () => owner);
        lost.Platform.OnRead = count => { if (count == 1) owner = owner with { Session = 8 }; };
        var redacted = lost.Operations.Execute(Parse("read"));
        Check(!redacted.Ok && redacted.Error == "owner_denied" && redacted.Current is null && redacted.Previous is null &&
            lost.NativeCalls == 0 && lost.Platform.Outstanding == 0, "owner loss discards observation");
        owner = new(Sid, 7);
        var disposeLoss = new EngineFixture(Sid, () => owner);
        disposeLoss.Platform.OnSessionDispose = () => owner = owner with { Sid = "S-1-5-21-10-20-30-9999" };
        Check(disposeLoss.Operations.Execute(Parse("read")) is { Ok: false, Current: null, Error: "owner_denied" }, "owner fence after cleanup");
        var unknown = New(); unknown.UnknownAfterEntry = true;
        var outcome = unknown.Operations.Execute(Parse("disable"));
        Check(!outcome.Ok && outcome.ActionIssued && outcome.OutcomeUnknown && unknown.NativeCalls == 1 && unknown.Platform.Outstanding == 0, "unknown after native entry never replayed");
        var cleanup = New(); cleanup.Platform.FailCleanup = true;
        var failedCleanup = cleanup.Operations.Execute(Parse("read"));
        Check(!failedCleanup.Ok && failedCleanup.Error == "task_native_failed" && failedCleanup.NativeError == unchecked((int)0x81234567) &&
            cleanup.Platform.Outstanding == 0, "direct cleanup status preserved/no raw text");
    }

    private static void LocalDatesAndQuoting()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var repeated = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).ToOADate();
        var ambiguous = NativeWindowsTasks.LocalTime(repeated, zone);
        Check(ambiguous is { Ambiguous: true, UtcOffset: null, TimeBasis: "scheduler_local_ole_date" } &&
            !ambiguous.Value.EndsWith("Z", StringComparison.Ordinal), "OLE DATE does not invent repeated-hour UTC");
        var spring = NativeWindowsTasks.LocalTime(new DateTime(2026, 3, 8, 2, 30, 0).ToOADate(), zone);
        Check(spring is { Invalid: true, UtcOffset: null }, "invalid local clock labeled");
        Check(NativeWindowsTasks.LocalTime(0, zone) is null, "unprovided DATE sentinel");
        foreach (var argv in new IReadOnlyList<string>[] { [], [""], ["plain", "a b"], ["a\"b", @"x\"], ["a\\b\\\"", "終"] })
        {
            var tail = WindowsOwnerAdminLauncher.Arguments(argv);
            var full = WindowsOwnerAdminLauncher.CommandLine(@"C:\fixture\tool.exe", argv);
            var image = WindowsOwnerAdminLauncher.CommandLine(@"C:\fixture\tool.exe", []);
            Check(full == image + (argv.Count == 0 ? "" : " " + tail), "shared quoting keeps CommandLine behavior");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task RunNativeReadOnlyAsync()
    {
        // Separate explicit selector only: MTA worker, list at most two entries and
        // read one exact task if present. No Run/Stop/Enabled/Delete/Register call.
        var result = await Task.Run(() =>
        {
            var owner = LocalIdentity.Current();
            var provider = new WindowsTaskOperations(owner.Sid);
            var list = provider.Execute(Parse("list", "{\"version\":1,\"max\":2}"));
            if (!list.Ok) throw new InvalidOperationException("readonly_task_list_" + list.Error);
            var path = list.Page!.Items.FirstOrDefault()?.Path;
            if (path is not null)
            {
                var read = provider.Execute(Parse("read", JsonSerializer.Serialize(new { version = 1, task = path }, Json)));
                if (!read.Ok || read.Current?.Path != path) throw new InvalidOperationException("readonly_task_read_" + read.Error);
            }
            return new { count = list.Page.Items.Count, more = list.Page.HasMore, exactRead = path is not null };
        }, CancellationToken.None);
        Console.WriteLine("PASS actual readonly Windows task boundary: count=" + result.count.ToString(CultureInfo.InvariantCulture) +
            "; more=" + result.more + "; exactRead=" + result.exactRead + "; no task control/registration");
    }

    private static EngineFixture New() => new(Sid, () => new(Sid, 7));
    private static OperationRequest Request(string action, string? body = null) => new("windows_task",
        new Dictionary<string, string> { ["op"] = action }, WindowsTaskOperationContract.IsReadOnly(action) ? null : "task-fixture-rid",
        Body: body ?? Body(action));
    private static WindowsTaskInvocation Parse(string action, string? body = null) => WindowsTaskOperationContract.Parse(Request(action, body));
    private static string Body(string action)
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action == "list") body["folder"] = @"\CortexFixture";
        else if (action != "prepare") body["task"] = EngineFixtureTaskPath;
        if (!WindowsTaskOperationContract.IsReadOnly(action) && action != "create") body["taskSha"] = EngineFixtureTaskSha;
        if (action == "stop") body["instance"] = EngineFixtureInstanceGuid;
        if (action is "prepare" or "create" or "update")
        {
            body["tool"] = "fixture"; body["toolVersion"] = 3; body["toolSha"] = OtherSha;
            body["argv"] = new JsonArray("one", "two words"); body["environmentPolicy"] = "scheduler";
            body["cwd"] = @"C:\fixture";
            if (action != "prepare") body["schedule"] = new JsonObject { ["kind"] = "on_demand" };
        }
        return body.ToJsonString();
    }
    private static void Refuse(Action action)
    {
        try { action(); throw new InvalidOperationException("Expected closed task contract refusal."); }
        catch (WindowsTaskContractException failure) { Check(failure.Field.Length > 0, "bounded invalid field"); }
    }
    private static void Check(bool value, string message)
    { _assertions++; if (!value) throw new InvalidOperationException("Windows task fixture: " + message); }

    internal sealed class EngineFixture
    {
        internal FakePlatform Platform { get; }
        internal WindowsTaskOperations Operations { get; }
        internal string TaskPath => EngineFixtureTaskPath;
        internal string TaskSha => EngineFixtureTaskSha;
        internal string InstanceGuid => EngineFixtureInstanceGuid;
        internal int NativeCalls => Platform.NativeCalls;
        internal bool HoldCall { get => Platform.HoldCall; set => Platform.HoldCall = value; }
        internal Task Entered => Platform.Entered.Task;
        internal int? FailureStatus { get => Platform.FailureStatus; set => Platform.FailureStatus = value; }
        internal bool UnknownAfterEntry { get => Platform.UnknownAfterEntry; set => Platform.UnknownAfterEntry = value; }
        internal EngineFixture(string ownerSid, Func<LocalPeer> current, WindowsTaskAuthority authority = WindowsTaskAuthority.Owner)
        {
            Platform = new();
            Operations = new(ownerSid, current, Platform, new FakeTools(Platform), authority);
        }
        internal void Release() => Platform.Release();
    }

    internal sealed class FakePlatform : IWindowsTaskPlatform
    {
        internal int Connects, NativeCalls, Outstanding, Reads, ToolPreparations, ToolOutstanding;
        internal bool Exists = true, Enabled = true, AllowDemand = true, AuthoringSupported = true, ChangeCollection, BadItemPath;
        internal bool ReturnInstance = true, BadRegistrationReadback, FailCleanup, ToolHeldDuringRegister, ToolHeldDuringReadback;
        internal bool? RegisterWasCreate;
        internal string Sha = EngineFixtureTaskSha, Principal = "owner_interactive_limited", InstancePath = EngineFixtureTaskPath;
        internal string? LongState, StoppedInstance;
        internal int Count = 1, VisibleInstances = 1, HiddenInstances, ChangeAtRead = -1;
        internal string Executable = @"C:\fixture\tool.exe", WorkingDirectory = @"C:\fixture";
        internal ToolExecutionMode ToolMode = ToolExecutionMode.User;
        internal WindowsTaskToolPin Pin = new("fixture", 3, OtherSha);
        internal Action<int>? OnRead;
        internal Action? OnSessionDispose;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _release;
        private bool _hold;
        internal bool HoldCall
        {
            get => _hold;
            set
            {
                _hold = value;
                if (value) _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                else _release?.TrySetResult();
            }
        }
        internal int? FailureStatus;
        internal bool UnknownAfterEntry;
        internal void Release() => _release?.TrySetResult();
        internal int Mutate(Action effect)
        {
            NativeCalls++; Entered.TrySetResult();
            if (_hold) _release!.Task.GetAwaiter().GetResult(); // Models a noncancelable entered COM RPC.
            if (UnknownAfterEntry) throw new WindowsTaskNativeException("task_native_failed", unchecked((int)0x80004005));
            var status = FailureStatus ?? 0;
            if (status >= 0) effect();
            return status;
        }
        public IWindowsTaskSession Connect(CancellationToken token = default) { token.ThrowIfCancellationRequested(); Connects++; return new FakeSession(this); }
        private Lease Track() { Outstanding++; return new(() => Outstanding--); }
        private sealed class Lease(Action close) : IDisposable
        { private Action? _close = close; public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke(); }
        private sealed class FakeSession(FakePlatform p) : IWindowsTaskSession
        {
            private readonly Lease _lease = p.Track();
            public IWindowsTaskEnumeration OpenList(string folder, string kind, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return new FakeEnumeration(p, folder, kind); }
            public IWindowsTaskHandle? OpenTask(string path, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return p.Exists && path == EngineFixtureTaskPath ? new FakeTask(p) : null; }
            public IWindowsTaskRegistration PrepareRegistration(string path, IWindowsTaskHandle? existing, ToolInvocation invocation,
                WindowsTaskSchedule schedule, string ownerSid, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return new FakeRegistration(p, existing is null); }
            public void Dispose()
            {
                _lease.Dispose(); p.OnSessionDispose?.Invoke();
                if (p.FailCleanup) throw new WindowsTaskNativeException("task_native_failed", unchecked((int)0x81234567));
            }
        }
        private sealed class FakeEnumeration(FakePlatform p, string folder, string kind) : IWindowsTaskEnumeration
        {
            private readonly Lease _lease = p.Track();
            private int _countReads;
            public int Count => p.Count + (p.ChangeCollection && _countReads++ > 0 ? 1 : 0);
            public WindowsTaskSummary At(int index, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                var path = p.Count == 1 ? EngineFixtureTaskPath : folder + "\\Task" + (index - 1).ToString("D5", CultureInfo.InvariantCulture);
                return new(p.BadItemPath ? @"\wrong\item" : path, kind, p.LongState ?? "ready", true);
            }
            public void Dispose() => _lease.Dispose();
        }
        private sealed class FakeTask(FakePlatform p) : IWindowsTaskHandle
        {
            private readonly Lease _lease = p.Track();
            public WindowsTaskDetails Read(string ownerSid, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested(); p.Reads++; p.OnRead?.Invoke(p.Reads);
                if (p.Reads == p.ChangeAtRead) p.Sha = OtherSha;
                if (p.ToolOutstanding > 0 && p.NativeCalls > 0) p.ToolHeldDuringReadback = true;
                return new(EngineFixtureTaskPath, p.Enabled ? "ready" : "disabled", p.Enabled, false, p.AllowDemand,
                    p.Sha, p.Sha, OtherSha, p.BadRegistrationReadback ? "unverified_principal" : p.Principal, "least",
                    1, 0, p.AuthoringSupported);
            }
            public IWindowsTaskInstanceEnumeration OpenInstances(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return new FakeInstances(p); }
            public WindowsTaskRunResult Run()
            {
                var status = p.Mutate(() => { });
                return new(status, p.ReturnInstance ? new(EngineFixtureInstanceGuid, EngineFixtureTaskPath, "running") : null);
            }
            public int SetEnabled(bool enabled) => p.Mutate(() => { p.Enabled = enabled; });
            public int Remove() => p.Mutate(() => p.Exists = false);
            public void Dispose() => _lease.Dispose();
        }
        private sealed class FakeInstances(FakePlatform p) : IWindowsTaskInstanceEnumeration
        {
            private readonly Lease _lease = p.Track();
            public int Count => p.VisibleInstances;
            public WindowsTaskInstance At(int index, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return Item(index); }
            public IWindowsTaskRunningHandle Open(int index) => new FakeRunning(p, Item(index));
            private WindowsTaskInstance Item(int index) => new(index == 1 ? EngineFixtureInstanceGuid :
                index.ToString("x8", CultureInfo.InvariantCulture) + "-0000-0000-0000-000000000000", p.InstancePath, "running");
            public void Dispose() => _lease.Dispose();
        }
        private sealed class FakeRunning(FakePlatform p, WindowsTaskInstance instance) : IWindowsTaskRunningHandle
        {
            private readonly Lease _lease = p.Track();
            public WindowsTaskInstance Read(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return instance; }
            public int Stop() => p.Mutate(() => p.StoppedInstance = instance.InstanceGuid);
            public void Dispose() => _lease.Dispose();
        }
        private sealed class FakeRegistration(FakePlatform p, bool create) : IWindowsTaskRegistration
        {
            private readonly Lease _lease = p.Track();
            public WindowsTaskRegistrationResult Register()
            {
                p.ToolHeldDuringRegister = p.ToolOutstanding > 0; p.RegisterWasCreate = create;
                var status = p.Mutate(() => { p.Exists = true; p.Enabled = false; });
                return new(status, status < 0 ? null : new FakeTask(p));
            }
            public void Dispose() => _lease.Dispose();
        }
    }

    private sealed class FakeTools(FakePlatform p) : IWindowsTaskToolResolver
    {
        public WindowsTaskPreparedTool Prepare(WindowsTaskToolRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); p.ToolPreparations++; p.ToolOutstanding++;
            var invocation = new ToolInvocation(Guid.NewGuid(), request.Tool, p.Executable, request.Argv, p.WorkingDirectory,
                TimeSpan.FromSeconds(30), [0], p.ToolMode, ToolWindowMode.Hidden, "none", "utf8", "utf8", 4096, ProcessEnvironment.Empty, []);
            return new(invocation, p.Pin, new ToolLease(p));
        }
        private sealed class ToolLease(FakePlatform p) : IDisposable
        { private int _closed; public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) p.ToolOutstanding--; } }
    }
}
