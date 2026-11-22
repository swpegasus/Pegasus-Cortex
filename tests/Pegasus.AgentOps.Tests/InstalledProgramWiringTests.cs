using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class InstalledProgramWiringTests
{
    private const string Body = "{\"version\":1,\"source\":\"machine64\",\"limit\":1}";
    internal static async Task RunAsync()
    {
        await MetadataAndCliAsync();
        var root = Directory.CreateTempSubdirectory("cortex-program-wiring-").FullName;
        try
        {
            var admission = new UserHostAdmission();
            await using var engine = new AgentOpsEngine(root, null, false, null, false, null,
                maintenanceMode: new OpenMode(), userHostAdmission: admission);
            var request = Request("ordinary-read");
            Check(!(await engine.ExecuteAsync(request)).Ok, "unbound engine admitted registry inventory");
            engine.BindRegistryOwner(GeneralInstalledProgramTests.CreateEngineFixtureBinding());
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            var original = new OperationReceipt(true, new string('a', 32), "collision-rid", "done", "mkdir");
            using (var insert = db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) VALUES($jid,$rid,1,'done',$receipt,NULL,1,$at)";
                insert.Parameters.AddWithValue("$jid", original.Jid);
                insert.Parameters.AddWithValue("$rid", original.Rid);
                insert.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(original));
                insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
            var observed = await engine.ExecuteAsync(request with { Rid = "collision-rid" });
            Check(observed.Ok && observed.Op == "installed_program_list" &&
                observed.Data?.GetValueOrDefault("product_verified") is false,
                "passive inventory replayed an unrelated durable RID or claimed verified products");
            Check(!AgentOpsEngine.IsMutation(request) && !AgentOpsEngine.SupportsDeferredCompletion(request),
                "read-only inventory gained mutation/deferred behavior");
            var another = await engine.ExecuteAsync(request);
            Check(another.Ok, "owner-bound inventory failed");
            using (var count = db.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM jobs";
                Check(Convert.ToInt64(count.ExecuteScalar()) == 1, "inventory created durable history rows");
            }
            foreach (var invalid in new[]
            {
                request with { Root = "AB12" }, request with { Ws = "forbidden" }, request with { Cwd = root },
                request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["wait"] = "false" } },
                request with { Arg = new Dictionary<string, string> { ["op"] = "list", [OperationRequest.BodyOmittedKey] = "1" } },
                request with { Body = Body[..^1] + ",\"uninstallCommand\":\"forbidden\"}" }
            }) Check(!(await engine.ExecuteAsync(invalid)).Ok, "inventory admitted forbidden context or fields");
            using (var closing = admission.TryClose() ?? throw new InvalidOperationException("close_failed"))
            {
                var denied = await engine.ExecuteAsync(request with { Rid = "collision-rid" });
                Check(!denied.Ok && denied.Err == "host_stopping", "closed owner replayed another operation as an inventory response");
            }
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-program-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, recursive: true);
        }
        Console.WriteLine("PASS installed-program wiring: captured CLI/MCP, owner-bound fake registry +owned SQLite, no durable read rows/RID cross-replay, forbidden context and closed-owner refusal; no live registry inventory");
    }

    private static async Task MetadataAndCliAsync()
    {
        var capture = new Capture();
        using var document = JsonDocument.Parse(new JsonObject { ["op"] = "installed_program_list", ["text"] = Body }.ToJsonString());
        await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
        Check(capture.Calls == 1 && capture.Last is { Op: "installed_program", Ws: null, Cwd: null, Root: null } &&
            capture.Last.Body == Body, "MCP inventory selected wrong route/default workspace");
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await PaoProgram.RunCliAsync(["installed_program", "list", "--stdin"], capture, output, error,
            CancellationToken.None, new StringReader(Body));
        Check(exit == 0 && capture.Calls == 2 && capture.Last is { Ws: null, Cwd: null, Root: null } &&
            error.ToString().Length == 0, "literal CLI inventory did not preserve closed owner context");
        var before = capture.Calls;
        foreach (var extra in new[] { "root", "cwd", "ws", "wait", "command" })
        {
            var invalid = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            invalid[extra] = "forbidden";
            using var bad = JsonDocument.Parse(invalid.ToJsonString());
            await PaoProgram.CallUnifiedMcpToolAsync(bad.RootElement, capture, CancellationToken.None);
        }
        using (var duplicate = JsonDocument.Parse("{\"op\":\"installed_program_list\",\"text\":\"{}\",\"text\":\"{}\"}"))
            await PaoProgram.CallUnifiedMcpToolAsync(duplicate.RootElement, capture, CancellationToken.None);
        Check(capture.Calls == before, "bad/duplicate inventory fields reached dispatcher");
        var help = PaoProgram.UnifiedMcpOperationHelp("installed_program_read") ?? throw new InvalidOperationException("help_missing");
        Check(help["availability"]!.GetValue<string>() == "unobserved" &&
            help["executionRoute"]!.GetValue<string>().Contains("ordinary", StringComparison.OrdinalIgnoreCase) &&
            !help["allowed"]!.AsArray().Any(field => field!.GetValue<string>() == "root"),
            "inventory metadata implied observed/System availability");
    }

    private static OperationRequest Request(string rid) => new("installed_program",
        new Dictionary<string, string> { ["op"] = "list" }, rid, Body: Body);
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, "captured", request.Rid ?? "read", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
}
