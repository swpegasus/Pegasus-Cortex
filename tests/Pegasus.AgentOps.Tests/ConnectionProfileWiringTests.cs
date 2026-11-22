using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ConnectionProfileWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SmbConnectionProfile Profile = new(1, "fixture-share", "Synthetic profile label",
        @"\\fixture-server\fixture-share", "X:", "currentUser");

    internal static async Task RunAsync()
    {
        await MetadataAndCliAsync();
        await OwnerLossAsync(afterEntry: false);
        await OwnerLossAsync(afterEntry: true);
        var root = Directory.CreateTempSubdirectory("cortex-profile-wiring-").FullName;
        var native = new Native();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            var peer = new LocalPeer(sid, 7, Elevated: false);
            var gate = new UserHostAdmission();
            var profiles = new OwnerSmbConnectionProfiles(Path.Combine(root, "profiles"), sid, () => peer);
            var passwords = new Passwords();
            var connections = new WindowsSmbConnections(sid, () => peer, () => new(42, 0), passwords, native);
            await using var engine = new AgentOpsEngine(Path.Combine(root, "engine"), null, false, null, false, null,
                maintenanceMode: new OpenMode(), userHostAdmission: gate);
            try
            {
                var save = Body("save");
                Check(!(await engine.ExecuteAsync(Request("save", save, "unbound"))).Ok, "unbound profile store admitted");
                engine.BindSmbOwner(new(sid, () => peer, () => profiles, () => connections));
                var saved = await engine.ExecuteAsync(Request("save", save, "profile-save"));
                Check(saved.Ok && saved.Data?.ContainsKey("snapshot") == true, "public save did not reach actual owned profile store");
                var snapshot = profiles.Read(Profile.Id).Snapshot ?? throw new InvalidOperationException("profile_missing");
                var pin = Pin(snapshot);
                var wrong = pin.DeepClone().AsObject(); wrong["revision"] = snapshot.Revision + 1;
                Check(!(await engine.ExecuteAsync(Request("connect", wrong.ToJsonString(), "wrong-profile"))).Ok && native.Connects == 0 && passwords.Reads == 0,
                    "stale profile reached credentials/native mapping");
                var queued = await engine.ExecuteAsync(Request("connect", pin.ToJsonString(), "profile-connect", deferred: true));
                Check(queued.State is "queued" or "running", "deferred connect did not return admission");
                Check(queued.Data?.GetValueOrDefault("seq") is long sequence && sequence > 0,
                    "private request identity projection lost admission sequence");
                await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await engine.InspectExitIssueAsync(CancellationToken.None) == "work_active",
                    "issued native connect lost its active-effect ownership");
                _ = await engine.ExecuteAsync(new("job", new Dictionary<string, string>
                    { ["op"] = "cancel", ["jid"] = queued.Jid }));
                Check(native.Connects == 1 && native.Disconnects == 0 && !native.Returned,
                    "cancellation replayed or compensated the pending native call");
                Check(await engine.InspectExitIssueAsync(CancellationToken.None) == "work_active",
                    "cancellation released a still-running native worker");
                native.Release.Set();
                var terminal = await engine.ExecuteAsync(new("job", new Dictionary<string, string>
                    { ["op"] = "wait", ["jid"] = queued.Jid }));
                Check(terminal is { Ok: true, State: "done", Op: "connection_profile_connect" } && terminal.Jid == queued.Jid,
                    "late actual native completion was fabricated as clean cancellation");
                var replay = await engine.ExecuteAsync(Request("connect", pin.ToJsonString(), "profile-connect"));
                Check(replay.Jid == queued.Jid && native.Connects == 1 && native.Disconnects == 0 && passwords.Reads == 0,
                    "same RID replayed native work or default mode resolved a password");
                var mismatched = await engine.ExecuteAsync(Request("connect", wrong.ToJsonString(), "profile-connect"));
                Check(!mismatched.Ok && mismatched.Err == "precondition" && native.Connects == 1,
                    "same RID accepted changed profile coordinates");
                var remote = Profile with { Id = "fixture-remote", IdentityMode = "explicitRemote",
                    RemoteUser = @"fixture-domain\fixture-user", PasswordReference = "fixture.password" };
                var remoteBody = new JsonObject { ["version"] = 1, ["kind"] = "smb-drive", ["expectedRevision"] = 0,
                    ["profile"] = JsonSerializer.SerializeToNode(remote, SmbConnectionProfile.Json) };
                Check((await engine.ExecuteAsync(Request("save", remoteBody.ToJsonString(), "remote-save"))).Ok && passwords.Reads == 0,
                    "saving nonsecret remote profile resolved its password");
                using (var db = OpenDb(root))
                {
                    using var command = db.CreateCommand();
                    command.CommandText = "SELECT receipt || COALESCE(request,'') FROM jobs";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var text = reader.GetString(0);
                        Check(!text.Contains(Profile.RemoteShare, StringComparison.Ordinal) &&
                            !text.Contains("fixture-server", StringComparison.Ordinal) && !text.Contains(Profile.Label, StringComparison.Ordinal) &&
                            !text.Contains("passwordReference", StringComparison.Ordinal) &&
                            !text.Contains("fixture-domain", StringComparison.Ordinal) && !text.Contains("fixture.password", StringComparison.Ordinal),
                            "profile payload/endpoint/account/reference leaked to durable history");
                    }
                }
                long before;
                using (var db = OpenDb(root)) before = Count(db);
                var read = await engine.ExecuteAsync(Request("read", Body("read"), "profile-read"));
                var list = await engine.ExecuteAsync(Request("list", Body("list"), "profile-list"));
                Check(read.Ok && list.Ok, "ephemeral profile observations failed");
                using (var db = OpenDb(root)) Check(Count(db) == before, "profile observations created durable jobs");
                foreach (var invalid in new[]
                {
                    Request("read", Body("read"), "invalid") with { Root = "AB12" },
                    Request("read", Body("read"), "invalid") with { Ws = "forbidden" },
                    Request("read", Body("read"), "invalid") with { Cwd = root },
                    Request("read", Body("read"), "invalid") with { Arg = new Dictionary<string, string>
                        { ["op"] = "read", [OperationRequest.BodyOmittedKey] = "1" } }
                }) Check(!(await engine.ExecuteAsync(invalid)).Ok, "profile request admitted forbidden context/internal marker");
            }
            finally { native.Release.Set(); }
        }
        finally
        {
            native.Release.Set();
            var canonical = Path.GetFullPath(root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-profile-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, recursive: true);
            native.Release.Dispose();
        }
        Console.WriteLine("PASS connection-profile wiring: actual owned profile files/SQLite +fake WNet/identity, exact profile pins, original worker survives cancellation, no replay/fallback/payload history, captured CLI/MCP; no real mappings or credentials");
    }

    private static async Task MetadataAndCliAsync()
    {
        var capture = new Capture();
        foreach (var action in ConnectionProfileOperationContract.Actions)
        {
            var name = ConnectionProfileOperationContract.PublicName(action);
            var help = PaoProgram.UnifiedMcpOperationHelp(name) ?? throw new InvalidOperationException("help_missing");
            var example = help["mcp_example"]!.DeepClone().AsObject();
            _ = ConnectionProfileOperationContract.Parse(action, example["text"]!.GetValue<string>());
            using var json = JsonDocument.Parse(example.ToJsonString());
            var calls = capture.Calls;
            await PaoProgram.CallUnifiedMcpToolAsync(json.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
            var captured = capture.Last ?? throw new InvalidOperationException("profile_dispatch_missing");
            Check(capture.Calls == calls + 1 && captured is { Op: "connection_profile", Root: null, Ws: null, Cwd: null } &&
                captured.Arg.GetValueOrDefault("op") == action, "profile MCP route selected context/fallback or failed to dispatch");
            if (!ConnectionProfileOperationContract.IsReadOnly(action)) Check(!string.IsNullOrWhiteSpace(captured.Rid), "mutation RID lost");
        }
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await PaoProgram.RunCliAsync(["connection_profile", "save", "--stdin", "rid=captured-save"], capture,
            output, error, CancellationToken.None, new StringReader(Body("save")));
        Check(exit == 0 && capture.Last is { Op: "connection_profile", Rid: "captured-save", Root: null, Ws: null, Cwd: null }, "profile CLI changed context/RID");
        var before = capture.Calls;
        foreach (var field in new[] { "root", "cwd", "ws", "password", "provider" })
        {
            var value = new JsonObject { ["op"] = "connection_profile_connect", ["rid"] = "rejected", ["text"] = Body("connect"), [field] = "forbidden" };
            using var json = JsonDocument.Parse(value.ToJsonString());
            await PaoProgram.CallUnifiedMcpToolAsync(json.RootElement, capture, CancellationToken.None);
        }
        Check(capture.Calls == before, "forbidden profile/secret fields reached dispatcher");
        var source = JsonNode.Parse(Body("save"))!.AsObject(); source["profile"]!["password"] = "synthetic-forbidden";
        try { _ = ConnectionProfileOperationContract.Parse("save", source.ToJsonString()); }
        catch (ConnectionProfileContractException) { return; }
        throw new InvalidOperationException("raw_password_profile_accepted");
    }

    private static async Task OwnerLossAsync(bool afterEntry)
    {
        var root = Directory.CreateTempSubdirectory("cortex-profile-wiring-").FullName;
        var native = new Native();
        native.Release.Set();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            var peer = new LocalPeer(sid, 7, Elevated: false);
            var profiles = new OwnerSmbConnectionProfiles(Path.Combine(root, "profiles"), sid, () => peer);
            var saved = profiles.Save(Profile, 0);
            var snapshot = saved.Snapshot ?? throw new InvalidOperationException("owner_loss_profile_missing");
            var connections = new WindowsSmbConnections(sid, () => peer, () => new(42, 0), new Passwords(), native);
            if (afterEntry) native.AfterConnect = () => peer = peer with { Session = 8 };
            await using var engine = new AgentOpsEngine(Path.Combine(root, "engine"), null, false, null, false, null,
                maintenanceMode: new OpenMode(), userHostAdmission: new UserHostAdmission());
            engine.BindSmbOwner(new(sid, () => peer, () => profiles, () =>
            {
                if (!afterEntry) peer = peer with { Session = 8 };
                return connections;
            }));
            var result = await engine.ExecuteAsync(Request("connect", Pin(snapshot).ToJsonString(), "owner-loss"));
            Check(!result.Ok && result.Data?.GetValueOrDefault("action_issued") is bool issued && issued == afterEntry &&
                result.Data.GetValueOrDefault("outcome_unknown") is bool unknown && unknown == afterEntry &&
                result.Partial == afterEntry && result.State == (afterEntry ? "recovery_required" : "failed") &&
                native.Connects == (afterEntry ? 1 : 0), "owner loss invented or lost native-action uncertainty");
            Check(result.Data is { } redacted && !redacted.ContainsKey("snapshot") && !redacted.ContainsKey("mapping_observed"),
                "owner loss retained owner-visible profile payload");
        }
        finally
        {
            native.Release.Set();
            var canonical = Path.GetFullPath(root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-profile-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, recursive: true);
            native.Release.Dispose();
        }
    }

    private static JsonObject Pin(SmbConnectionProfileSnapshot snapshot) => new()
    { ["version"] = 1, ["kind"] = "smb-drive", ["id"] = Profile.Id, ["revision"] = snapshot.Revision, ["contentHash"] = snapshot.ContentHash };
    private static string Body(string action)
    {
        var value = new JsonObject { ["version"] = 1, ["kind"] = "smb-drive" };
        if (action == "save") { value["expectedRevision"] = 0; value["profile"] = JsonSerializer.SerializeToNode(Profile, SmbConnectionProfile.Json); }
        else if (action == "list") value["limit"] = 1;
        else
        {
            value["id"] = Profile.Id;
            if (action is "connect" or "inspect" or "disconnect")
            { value["revision"] = 1; value["contentHash"] = new string('0', 64); }
        }
        return value.ToJsonString();
    }
    private static OperationRequest Request(string action, string body, string rid, bool deferred = false) => new("connection_profile",
        deferred ? new Dictionary<string, string> { ["op"] = action, ["wait"] = "false" } : new Dictionary<string, string> { ["op"] = action }, rid, Body: body);
    private static SqliteConnection OpenDb(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "engine", "agentops.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static long Count(SqliteConnection db)
    { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(command.ExecuteScalar()); }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Passwords : IOwnerSmbPasswordResolver
    { internal int Reads; public OwnerPasswordLease Resolve(string reference) { Reads++; throw new InvalidOperationException("password_must_not_be_read"); } }
    private sealed class Native : IWindowsSmbNative
    {
        internal readonly ManualResetEventSlim Release = new(false);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Connects, Disconnects;
        internal bool Returned;
        internal Action? AfterConnect;
        private string? _remote;
        public SmbDriveObservation InspectDrive(string drive) => new(_remote is not null, RemoteShare: _remote);
        public int Connect(SmbConnectionProfile profile, ReadOnlySpan<char> password, Action beforeEntry)
        { beforeEntry(); Connects++; Entered.TrySetResult(); Release.Wait(); _remote = profile.RemoteShare; AfterConnect?.Invoke(); Returned = true; return 0; }
        public int Disconnect(string drive, Action beforeEntry)
        { beforeEntry(); Disconnects++; _remote = null; return 0; }
    }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, "captured", request.Rid ?? "read", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
}
