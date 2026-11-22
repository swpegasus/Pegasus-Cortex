using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Real owned SQLite + engine/coordinator composition, fake elevated process and
// in-process control messages. No UAC prompt, OS certificate store or key access.
internal static class CertificateAdministrationTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Host = @"C:\Program Files\Pegasus\Cortex\bin\host\PegasusCortex.Host.exe";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        using var certificate = new CertificateOperations().CreateAuthority(new("Owned administrator composition fixture",
            now.AddMinutes(-1), now.AddDays(1), 2048));
        foreach (var action in new[] { "store_admin_add", "store_admin_trust", "store_admin_remove" })
            await CompleteAsync(certificate, action);
        await UnknownAndRestartAsync(certificate);
        await UnavailableAsync(certificate);
        Console.WriteLine("PASS certificate administrator engine composition, issued durability, privacy, replay and recovery (owned SQLite/fake elevation only)");
    }

    private static async Task CompleteAsync(X509Certificate2 certificate, string action)
    {
        using var fixture = new Fixture(certificate);
        using var elevation = fixture.Coordinator();
        await using var engine = fixture.Engine(elevation);
        var request = fixture.Request(action);
        var pending = engine.ExecuteAsync(request);
        var ticket = await fixture.Launcher.Ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Check(await engine.InspectExitIssueAsync(default) == "work_active", "live helper ownership must block Exit");
            var redeem = await engine.HandleCertificateElevationAsync(Control(CertificateStoreElevationContract.RedeemOperation, ticket), fixture.Peer, default);
            Check(redeem.Ok && redeem.Data?["payload"] is AdminCertificateStoreRequest, "exact fake High child must redeem once");
            var payload = (AdminCertificateStoreRequest)redeem.Data!["payload"]!;
            Check(payload.Action == action.Replace("store_admin_", "store_", StringComparison.Ordinal) &&
                payload.CertificateId == fixture.Id && payload.Rid == request.Rid &&
                (action == "store_admin_remove" ? payload.PublicDer is null : payload.PublicDer!.SequenceEqual(certificate.RawData)),
                "ordinary owner supplies only the exact fixed action and pinned public material");
            var captured = fixture.Stored(request.Rid!);
            Check(captured.Receipt.State == "running" && Flag(captured.Receipt, "administrator_payload_issued") == true &&
                Flag(captured.Receipt, "outcome_unknown") == true && captured.Request is { Body: null },
                "issued uncertainty and body omission must be durable before payload is returned");
            AssertPrivate(captured, ticket, certificate.RawData);
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == payload.Jid && replay.State == "running" && replay.Data?.ContainsKey("payload") != true && fixture.Launcher.Starts == 1,
                "same in-flight RID observes metadata and never prompts or redeems again");
            var state = action == "store_admin_remove" ? "removed" : "added";
            var complete = new CertificateElevationResult(1, state, fixture.Id, true, true, false);
            Check((await engine.HandleCertificateElevationAsync(Control(CertificateStoreElevationContract.CompleteOperation, ticket,
                CertificateStoreElevationContract.SerializeResult(complete)), fixture.Peer, default)).Ok,
                "first typed report is acknowledged");
            Check(!pending.IsCompleted && await engine.InspectExitIssueAsync(default) == "work_active",
                "completion report does not release original child ownership before natural exit");
            fixture.Launcher.Process.Exit(37);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Ok && result.State == "done" && Flag(result, "postcondition_observed") == true &&
                result.Data?["administrator_exit"] is 37 && fixture.Repository.Resolves == 0 &&
                fixture.Repository.Inspects == (action == "store_admin_remove" ? 0 : 1),
                "known completed effect survives helper nonzero exit and never resolves a private key");
            var final = fixture.Stored(request.Rid!);
            AssertPrivate(final, ticket, certificate.RawData);
            Check(final.Request is null && Flag(final.Receipt, "store_action_issued") == true &&
                (await engine.ExecuteAsync(request)).Jid == payload.Jid && fixture.Launcher.Starts == 1,
                "final receipt is metadata only and same RID never repeats elevation");
        }
        finally { fixture.Launcher.Process.Exit(37); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task UnknownAndRestartAsync(X509Certificate2 certificate)
    {
        using var fixture = new Fixture(certificate);
        var request = fixture.Request("store_admin_remove");
        DurableJob captured;
        using (var elevation = fixture.Coordinator())
        await using (var engine = fixture.Engine(elevation))
        {
            var pending = engine.ExecuteAsync(request);
            var ticket = await fixture.Launcher.Ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Check((await engine.HandleCertificateElevationAsync(Control(CertificateStoreElevationContract.RedeemOperation, ticket), fixture.Peer, default)).Ok,
                    "owned lost-report case redeems once");
                captured = fixture.Stored(request.Rid!);
                fixture.Launcher.Process.Exit(4);
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!result.Ok && result.State == "recovery_required" && result.Partial &&
                    Flag(result, "administrator_payload_issued") == true && Flag(result, "outcome_unknown") == true &&
                    result.Data?.ContainsKey("store_action_issued") != true,
                    "lost completion records uncertainty without inventing an observed native mutation");
                Check((await engine.ExecuteAsync(request)).Jid == result.Jid && fixture.Launcher.Starts == 1,
                    "unknown outcome is never automatically replayed");
            }
            finally { fixture.Launcher.Process.Exit(4); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        // Restore only this fixture's captured running row to model restart;
        // no process crash, live database or real administrator effect is used.
        using (var store = new SqliteJobStore(fixture.State)) store.Update(captured);
        using var reopenedElevation = fixture.Coordinator();
        await using var reopened = fixture.Engine(reopenedElevation);
        var recovered = await reopened.ExecuteAsync(request);
        Check(recovered.State == "recovery_required" && recovered.Partial && fixture.Launcher.Starts == 1 &&
            Flag(recovered, "administrator_payload_issued") == true,
            "captured issued restart preserves uncertainty and never creates a fresh ticket or prompt");
        Check((await reopened.ExecuteAsync(request with { Body = request.Body!.Replace("\"personal\"", "\"intermediate\"", StringComparison.Ordinal) })).Err == "precondition",
            "same RID cannot replace the original selected store");
    }

    private static async Task UnavailableAsync(X509Certificate2 certificate)
    {
        using var fixture = new Fixture(certificate);
        await using var engine = fixture.Engine(null);
        var result = await engine.ExecuteAsync(fixture.Request("store_admin_add"));
        Check(!result.Ok && fixture.Launcher.Starts == 0 && fixture.Repository.Inspects == 0,
            "missing trusted elevation composition refuses before repository access or launch");
        foreach (var action in new[] { "store_admin_add", "store_admin_trust", "store_admin_remove" })
        {
            var help = PaoProgram.UnifiedMcpOperationHelp("certificate_" + action)!;
            Check(help["executionRoute"]!.GetValue<string>().Contains("UAC", StringComparison.Ordinal) &&
                help["availability"]!.GetValue<string>() == "unobserved", "selected help must state explicit UAC without claiming readiness");
        }
    }

    private static OperationRequest Control(string action, string ticket, string? body = null) =>
        new(action, new Dictionary<string, string> { ["ticket"] = ticket }, Body: body);
    private static bool? Flag(OperationReceipt receipt, string field) => receipt.Data?.GetValueOrDefault(field) switch
    { bool value => value, JsonElement { ValueKind: JsonValueKind.True } => true, JsonElement { ValueKind: JsonValueKind.False } => false, _ => null };
    private static void AssertPrivate(DurableJob row, string ticket, byte[] der)
    {
        var json = JsonSerializer.Serialize(row, Json);
        Check(!json.Contains(ticket, StringComparison.Ordinal) && !json.Contains(Convert.ToBase64String(der), StringComparison.Ordinal) &&
            row.Receipt.Data?.ContainsKey("certificate") != true && row.Receipt.Data?.ContainsKey("payload") != true &&
            row.Receipt.Detail is null && row.Request?.Body is null,
            "durable receipt/request cannot contain ticket, public DER, payload or live result");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot _root = new();
        internal readonly Launcher Launcher = new();
        internal readonly Repository Repository;
        internal string State { get; }
        internal string Id => Repository.Entry.CertificateId;
        internal LocalPeer Peer => new(Sid, 7, Host, Launcher.Process.Identity.Pid, true);
        internal Fixture(X509Certificate2 certificate)
        { State = _root.Dir("administrator-engine"); Repository = new(certificate); }
        internal OwnerCertificateStoreElevation Coordinator() => new(Sid, 7, Host, Launcher);
        internal AgentOpsEngine Engine(OwnerCertificateStoreElevation? elevation)
        {
            var engine = new AgentOpsEngine(State, null, false, null, false, null,
                maintenanceMode: new OpenMode(), userHostAdmission: new UserHostAdmission());
            engine.BindCertificateOwner(new(Sid, () => new(Sid, 7), () => Repository,
                (_, _) => throw new InvalidOperationException("administrator work must not construct a signing adapter")));
            if (elevation is not null) engine.BindCertificateStoreElevation(elevation);
            return engine;
        }
        internal OperationRequest Request(string action) => new("certificate", new Dictionary<string, string> { ["op"] = action },
            "admin-" + action, Body: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = 1, ["scope"] = "local_machine", ["store"] = action == "store_admin_trust" ? "root" : "personal",
                ["certificateId"] = Id
            }.Concat(action == "store_admin_remove" ? [] : new Dictionary<string, object?> { ["reference"] = "public-fixture" })
                .ToDictionary(pair => pair.Key, pair => pair.Value), Json));
        internal DurableJob Stored(string rid)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(State, "agentops.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT receipt,mutation,seq,request FROM jobs WHERE rid=$rid";
            command.Parameters.AddWithValue("$rid", rid);
            using var reader = command.ExecuteReader(); Check(reader.Read(), "owned fixture durable row is missing");
            return new(JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0), Json)!, reader.GetBoolean(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<OperationRequest>(reader.GetString(3), Json));
        }
        public void Dispose() => _root.Dispose();
    }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Repository(X509Certificate2 certificate) : IOwnerCertificateRepository
    {
        internal readonly OwnerCertificateEntry Entry = new(1, Sid, "public-fixture", CertificateOperations.Inspect(certificate).CertificateId,
            new string('0', 64), certificate.RawData, false);
        internal int Inspects, Resolves;
        public OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null)
        { Inspects++; Check(reference == Entry.Reference && expectedCertificateId == Entry.CertificateId, "public repository pin changed"); return Entry; }
        public X509Certificate2 Resolve(CertificateKeyReference reference) { Resolves++; throw new InvalidOperationException("private key must not be resolved"); }
        public OwnerCertificateEntry Publish(string reference, X509Certificate2 certificate, string identity, CancellationToken token = default) =>
            throw new InvalidOperationException("administrator work cannot publish a key");
    }
    private sealed class Launcher : IOwnerCertificateElevationLauncher
    {
        internal readonly TaskCompletionSource<string> Ticket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Child Process = new();
        internal int Starts;
        public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token)
        { Interlocked.Increment(ref Starts); Ticket.TrySetResult(ticket); return Task.FromResult<IOwnerCertificateElevationProcess>(Process); }
        public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer) => Process.Identity;
    }
    private sealed class Child : IOwnerCertificateElevationProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CertificateElevationProcessIdentity Identity { get; } = new(1234, "2026-11-20T12:00:00.0000000Z", Host);
        public Task<int> WaitForExitAsync() => _exit.Task;
        internal void Exit(int code) => _exit.TrySetResult(code);
        public void Dispose() { Check(_exit.Task.IsCompleted, "original fake helper disposed before exit"); }
    }
}
