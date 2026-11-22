using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Real owned SQLite and the public CLI/MCP routing, with injected device/network
// and account inventories. No Windows inventory, probes, configuration or hardware actions.
internal static class WindowsInventoryWiringTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";

    internal static Task RunDevicesAsync() => RunAsync("windows_device",
        "{\"version\":1,\"scope\":\"present\",\"limit\":1}", "devices",
        engine => engine.BindWindowsDevices(WindowsDeviceTests.CreateEngineFixtureOperations(OwnerSid, Current)));

    internal static Task RunNetworkAsync() => RunAsync("windows_network",
        "{\"version\":1,\"view\":\"adapters\",\"limit\":1}", "items",
        engine => engine.BindWindowsNetwork(WindowsNetworkTests.CreateEngineFixtureOperations(OwnerSid, Current)));

    internal static Task RunAccountsAsync() => RunAsync("windows_account",
        "{\"version\":1,\"view\":\"users\",\"limit\":1}", "items",
        engine => engine.BindWindowsAccounts(WindowsAccountTests.CreateEngineFixtureOperations(OwnerSid, Current)));

    private static LocalPeer Current() => new(OwnerSid, 7, Elevated: false);

    private static async Task RunAsync(string family, string body, string itemField, Action<AgentOpsEngine> bind)
    {
        await MetadataAsync(family);
        var root = Directory.CreateTempSubdirectory("cortex-inventory-wiring-").FullName;
        try
        {
            var admission = new UserHostAdmission();
            await using var engine = new AgentOpsEngine(root, null, false, null, false, null,
                maintenanceMode: new OpenMode(), userHostAdmission: admission);
            var request = Request(family, body, "inventory-read");
            Check(!(await engine.ExecuteAsync(request)).Ok, "unbound inventory admitted");
            bind(engine);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            var original = new OperationReceipt(true, new string('b', 32), "unrelated-rid", "done", "mkdir");
            using (var insert = db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) VALUES($jid,$rid,1,'done',$receipt,NULL,1,$at)";
                insert.Parameters.AddWithValue("$jid", original.Jid); insert.Parameters.AddWithValue("$rid", original.Rid);
                insert.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(original));
                insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
            }
            var observed = await engine.ExecuteAsync(request with { Rid = original.Rid });
            Check(observed.Ok && observed.Jid != original.Jid && observed.Op == family + "_list" &&
                observed.Data?.GetValueOrDefault(itemField) is { } rows && JsonSerializer.SerializeToElement(rows).GetArrayLength() > 0,
                "inventory reused a durable RID or missed injected provider");
            Check(observed.Data is { } data && !data.ContainsKey("scanned") && !data.ContainsKey("observedUtc") &&
                !data.ContainsKey("observation"), "routine inventory echoed diagnostic metadata");
            using (var count = db.CreateCommand())
            { count.CommandText = "SELECT COUNT(*) FROM jobs"; Check(Convert.ToInt64(count.ExecuteScalar()) == 1, "inventory created durable rows"); }
            foreach (var invalid in new[]
            {
                request with { Root = "AB12" }, request with { Ws = "forbidden" }, request with { Cwd = root },
                request with { PathRoots = new Dictionary<string, string> { ["src"] = root } },
                request with { Arg = new Dictionary<string, string> { ["op"] = "list", [OperationRequest.BodyOmittedKey] = "1" } },
                request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["wait"] = "false" } }
            }) Check(!(await engine.ExecuteAsync(invalid)).Ok, "inventory admitted forbidden context/internal marker");
            Check(!MaintenanceRequestPolicy.IsMutation(request), "read inventory classified as mutation");
            using var closed = admission.TryClose() ?? throw new InvalidOperationException("fixture_admission_close_failed");
            var refused = await engine.ExecuteAsync(request with { Rid = original.Rid });
            Check(!refused.Ok && refused.Err == "host_stopping" && refused.Jid != original.Jid,
                "closed owner replayed unrelated history as a new observation");
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-inventory-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, recursive: true);
        }
        Console.WriteLine("PASS " + family + " wiring: captured CLI/MCP, injected owner/provider +owned SQLite, ephemeral reads/no RID cross-replay, closed admission/context and compact output; no live inventory");
    }

    internal static async Task RunResolutionMetadataAsync()
    {
        await MetadataAsync("windows_network", "resolve");
        var help = PaoProgram.UnifiedMcpOperationHelp("windows_network_resolve")!;
        Check(help["executionRoute"]!.GetValue<string>().Contains("active DNS", StringComparison.Ordinal) &&
            help["inputSchema"]!["properties"]!["text"]!["maxLength"]!.GetValue<int>() ==
                WindowsNetworkResolutionContract.MaximumBodyBytes,
            "DNS discovery hid active I/O or advertised the wrong body bound");
        Console.WriteLine("PASS windows_network_resolve metadata: captured CLI/MCP, explicit active-query help and closed metadata; no native DNS query");
    }

    private static async Task MetadataAsync(string family, params string[] actions)
    {
        var capture = new Capture();
        foreach (var action in actions.Length == 0 ? new[] { "list", "read" } : actions)
        {
            var name = family + "_" + action;
            var help = PaoProgram.UnifiedMcpOperationHelp(name) ?? throw new InvalidOperationException("inventory_help_missing");
            var example = help["mcp_example"]!.DeepClone().AsObject();
            using var json = JsonDocument.Parse(example.ToJsonString());
            var before = capture.Calls;
            await PaoProgram.CallUnifiedMcpToolAsync(json.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
            var request = capture.Last ?? throw new InvalidOperationException("inventory_dispatch_missing");
            Check(capture.Calls == before + 1 && request.Op == family && request.Arg.GetValueOrDefault("op") == action &&
                request.Root is null && request.Ws is null && request.Cwd is null && request.PathRoots is null,
                "inventory MCP selected context or failed actual per-action dispatch");
            using var output = new StringWriter(); using var error = new StringWriter();
            var exit = await PaoProgram.RunCliAsync([family, action, "--stdin"], capture, output, error, CancellationToken.None,
                new StringReader(example["text"]!.GetValue<string>()));
            Check(exit == 0 && capture.Calls == before + 2 && capture.Last is { Root: null, Ws: null, Cwd: null } cli &&
                cli.Op == family && cli.Arg.GetValueOrDefault("op") == action, "inventory CLI changed action/context");
            foreach (var extra in new[] { "root", "ws", "cwd", "wait", "provider", "identity", "password", "elevate", "server" })
            {
                var invalid = example.DeepClone().AsObject(); invalid[extra] = "forbidden";
                using var invalidJson = JsonDocument.Parse(invalid.ToJsonString());
                var calls = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(invalidJson.RootElement, capture, CancellationToken.None);
                Check(capture.Calls == calls, "forbidden inventory metadata reached dispatcher");
            }
            var encodedName = JsonSerializer.Serialize(name); var encodedBody = JsonSerializer.Serialize(example["text"]!.GetValue<string>());
            foreach (var invalid in new[]
            {
                "{\"op\":" + encodedName + ",\"op\":" + encodedName + ",\"text\":" + encodedBody + "}",
                "{\"op\":" + encodedName + ",\"text\":" + encodedBody + ",\"text\":" + encodedBody + "}",
                "{\"op\":" + encodedName + ",\"text\":" + encodedBody + ",\"detail\":\"true\"}"
            })
            {
                using var invalidJson = JsonDocument.Parse(invalid); var calls = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(invalidJson.RootElement, capture, CancellationToken.None);
                Check(capture.Calls == calls, "ambiguous/malformed inventory metadata reached dispatcher");
            }
        }
    }

    private static OperationRequest Request(string family, string body, string rid) =>
        new(family, new Dictionary<string, string> { ["op"] = "list" }, rid, Body: body);
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, "captured", request.Rid ?? "read", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
}
