using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Real owned SQLite and Engine lifetime; DNS completion is injected. No query,
// credential, configuration, service or installed product is used by this fixture.
internal static class WindowsNetworkResolutionWiringTests
{
    private const string Owner = "S-1-5-21-101-202-303-1001";
    private static LocalPeer Current() => new(Owner, 7, Elevated: false);
    private static OperationRequest Request(string rid = "dns-fixture") => new("windows_network",
        new Dictionary<string, string> { ["op"] = "resolve" }, rid,
        Body: """{"version":1,"name":"example.test","recordType":"A","timeoutMs":30000}""");

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await EphemeralAndAdmissionAsync();
        await CancellationKeepsEffectAsync();
        await DisposalJoinsOriginalAsync();
        Console.WriteLine("PASS DNS Engine wiring: owned SQLite/no durable RID replay, captured active-effect admission, late cancellation/Exit guard and disposal drain; injected resolver, no native DNS query");
    }

    private static async Task EphemeralAndAdmissionAsync()
    {
        await using var fixture = new Fixture(hold: false, bind: false);
        var request = Request("unrelated-rid");
        Check(!(await fixture.Engine.ExecuteAsync(request)).Ok && fixture.Native.Calls == 0,
            "unbound DNS provider admitted a query");
        fixture.Bind();
        using var db = fixture.OpenDatabase();
        var original = new OperationReceipt(true, new string('b', 32), request.Rid!, "done", "mkdir");
        using (var insert = db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) VALUES($jid,$rid,1,'done',$receipt,NULL,1,$at)";
            insert.Parameters.AddWithValue("$jid", original.Jid);
            insert.Parameters.AddWithValue("$rid", original.Rid);
            insert.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(original));
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        for (var i = 0; i < 2; i++)
        {
            var result = await fixture.Engine.ExecuteAsync(request);
            Check(result.Ok && result.Op == "windows_network_resolve" && result.Jid != original.Jid &&
                result.Data?.GetValueOrDefault("queryIssued") is true,
                $"DNS observation replayed a durable RID or lost issuance evidence: ok={result.Ok},state={result.State},err={result.Err},op={result.Op},sameJid={result.Jid == original.Jid},queryIssued={result.Data?.GetValueOrDefault("queryIssued")},nativeCalls={fixture.Native.Calls}");
        }
        Check(fixture.Native.Calls == 2, "DNS observation was cached or replayed");
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM jobs";
            Check(Convert.ToInt64(count.ExecuteScalar()) == 1, "DNS observation retained a durable job or request");
        }
        foreach (var invalid in new[]
        {
            request with { Root = "AB12" }, request with { Ws = "forbidden" }, request with { Cwd = fixture.Root },
            request with { Arg = new Dictionary<string, string> { ["op"] = "resolve", ["wait"] = "false" } },
            request with { Arg = new Dictionary<string, string> { ["op"] = "resolve", [OperationRequest.BodyOmittedKey] = "1" } },
            request with { Body = """{"version":1,"name":"example.test","recordType":"A","server":"192.0.2.1"}""" }
        }) Check(!(await fixture.Engine.ExecuteAsync(invalid)).Ok && fixture.Native.Calls == 2,
            "invalid DNS context/body reached the resolver");
        Check(MaintenanceRequestPolicy.IsMutation(request), "active DNS query was classified as passive inventory");
        fixture.Mode.Closed = true;
        var closed = await fixture.Engine.ExecuteAsync(request);
        Check(!closed.Ok && closed.Err == "maintenance_busy" && fixture.Native.Calls == 2,
            "closed maintenance admission issued a DNS query");
        fixture.Mode.Closed = false;
        using var admission = fixture.Admission.TryClose() ?? throw new InvalidOperationException("fixture close failed");
        var stopped = await fixture.Engine.ExecuteAsync(request);
        Check(!stopped.Ok && stopped.Err == "host_stopping" && fixture.Native.Calls == 2,
            "closed owner admission replayed an unrelated RID or queried DNS");
    }

    private static async Task CancellationKeepsEffectAsync()
    {
        await using var fixture = new Fixture(hold: true);
        using var stop = new CancellationTokenSource();
        var pending = fixture.Engine.ExecuteAsync(Request(), stop.Token);
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var admission = fixture.Admission.TryClose() ?? throw new InvalidOperationException("fixture close failed");
        Check(await fixture.Engine.UserExitBlockerAsync(CancellationToken.None) == "work_active",
            "active DNS query did not retain the Exit guard");
        stop.Cancel();
        await fixture.Native.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!pending.IsCompleted && await fixture.Engine.UserExitBlockerAsync(CancellationToken.None) == "work_active",
            "cancellation released the query/effect before original completion");
        fixture.Native.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!result.Ok && result.Err == "canceled" && result.Data?.GetValueOrDefault("queryIssued") is true,
            "late canceled query was reported as unissued or successful");
        Check(await fixture.Engine.UserExitBlockerAsync(CancellationToken.None) is null,
            "settled DNS query retained a live effect");
        using var db = fixture.OpenDatabase();
        using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM jobs";
        Check(Convert.ToInt64(count.ExecuteScalar()) == 0, "canceled DNS query retained a durable body");
    }

    private static async Task DisposalJoinsOriginalAsync()
    {
        await using var fixture = new Fixture(hold: true);
        var pending = fixture.Engine.ExecuteAsync(Request());
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = fixture.StopEngineAsync();
        await fixture.Native.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!disposal.IsCompleted && !pending.IsCompleted,
            "Engine disposal released ownership beneath an outstanding DNS callback");
        fixture.Native.Release();
        await Task.WhenAll(disposal, pending).WaitAsync(TimeSpan.FromSeconds(5));
        Check(!pending.Result.Ok && pending.Result.Data?.GetValueOrDefault("queryIssued") is true,
            "shutdown fabricated a successful/unissued query outcome");
    }

    private sealed class ControlledDns(bool hold) : IWindowsNetworkDnsNative
    {
        internal int Calls;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public async Task<WindowsNetworkDnsNativeResult> ResolveAsync(WindowsNetworkResolveInvocation invocation, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            using var registration = token.Register(() => CancelRequested.TrySetResult());
            Entered.TrySetResult();
            if (hold) await _release.Task;
            var canceled = token.IsCancellationRequested;
            return new(!canceled, canceled ? "canceled" : "observed",
                canceled ? Array.Empty<WindowsNetworkDnsAnswer>() : new[] { new WindowsNetworkDnsAnswer("ipv4", "192.0.2.1", 60) },
                true, canceled);
        }
    }

    private sealed class Mode : IMaintenanceModeSource
    {
        internal bool Closed;
        public MaintenanceModeRecord? Read() => Closed ? throw new IOException("fixture unavailable") : null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("cortex-dns-wiring-").FullName;
        internal readonly UserHostAdmission Admission = new();
        internal readonly Mode Mode = new();
        internal readonly ControlledDns Native;
        internal readonly AgentOpsEngine Engine;
        private Task? _disposal;
        internal Fixture(bool hold, bool bind = true)
        {
            Native = new(hold);
            Engine = new(Root, null, false, null, false, null, maintenanceMode: Mode, userHostAdmission: Admission);
            if (bind) Bind();
        }
        internal void Bind() => Engine.BindWindowsNetworkResolution(new(Owner, Current, Native));
        internal SqliteConnection OpenDatabase()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Root, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open(); return db;
        }
        internal Task StopEngineAsync() => _disposal ??= Engine.DisposeAsync().AsTask();
        public async ValueTask DisposeAsync()
        {
            Native.Release();
            await StopEngineAsync();
            var root = Path.GetFullPath(Root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cortex-dns-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(root, recursive: true);
        }
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
