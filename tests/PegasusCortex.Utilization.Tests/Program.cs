using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.V2;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Persistence.Sqlite.V2;

if (args is ["worker", var database, var countText])
{
    await using var worker = new SqliteCortexV2Store(new(database));
    var session = Guid.NewGuid().ToString("N");
    for (var i = 0; i < int.Parse(countText); i++) await worker.RecordUsageAsync(Sample(session));
    return 0;
}
if (args is ["rpc", var rpcDatabase])
{
    await using var store = new SqliteCortexV2Store(new(rpcDatabase));
    await store.InitializeAsync();
    await using var operational = new SqliteCortexV2OperationalStore(store, "fixture");
    var adapter = new CortexV2McpApplication(new CortexV2Application(operational, "test_actor"),
        new CortexUsageRecorder(store, "mcp"));
    await new McpStdioServer(adapter, Console.In, Console.Out, Console.Error).RunAsync(CancellationToken.None);
    return 0;
}

var tests = new (string, Func<Task>)[]
{
    ("durable cross-process counts and namespace non-additivity", ConcurrentAsync),
    ("read-only absent report and no semantic initialization", ReadOnlyAsync),
    ("90-day aggregate retention does not mutate semantic records", RetentionAsync),
    ("MCP success failure exact-read metrics and privacy", McpAsync),
    ("locked diagnostics degrade without corrupting results", LockedAsync),
    ("usage input bounds", BoundsAsync),
    ("collector overhead and shared page ceiling", OverheadAsync),
    ("raw JSON-RPC executable records durable metrics", ProtocolAsync)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"utilization tests={tests.Length} failed={failures}");
return failures == 0 ? 0 : 1;

static CortexUsageSample Sample(string? session = null) => new("mcp", "search",
    session ?? Guid.NewGuid().ToString("N"), "10000000-0000-0000-0000-000000000001",
    ["project.fixture", "knowledge.shared", "knowledge.shared"], false, 3, 0, 512, 2000);

static async Task ConcurrentAsync() => await Fixture(async (root, store) =>
{
    await store.InitializeAsync();
    var before = await store.GetStorageMetricsAsync();
    var processes = new List<Process>();
    try
    {
        for (var i = 0; i < 4; i++)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "worker", store.DatabasePath, "20" }) info.ArgumentList.Add(arg);
            processes.Add(Process.Start(info)!);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (var process in processes)
        {
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }
    }
    finally
    {
        foreach (var process in processes) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
    }
    await using var reopened = new SqliteCortexV2Store(new(store.DatabasePath));
    var report = await reopened.ReadUsageAsync();
    var total = report.Rows.Single(x => x.Dimension == "total");
    Check(total.Calls == 80 && total.Results == 240 && total.ResponseBytes == 40960, "lost or duplicated cross-process count");
    Check(report.ObservedProcesses == 4 && report.ObservedTasks == 1, "process/task attribution");
    Check(report.Rows.Where(x => x.Dimension == "namespace").All(x => x.Calls == 80 && x.ResponseBytes == 0), "namespace counts multiply payloads");
    var after = await store.GetStorageMetricsAsync();
    Check(before.RevisionRows == after.RevisionRows && before.BlobRows == after.BlobRows, "metrics changed semantic history");
    Check((await store.FullDoctorAsync()).Healthy, "optional tables invalidated doctor");
});

static async Task ReadOnlyAsync() => await Fixture(async (_, store) =>
{
    Check((await store.ReadUsageAsync()).State == "not-collected", "absent reporting");
    Check(!File.Exists(store.DatabasePath), "report created a semantic store");
    await store.InitializeAsync();
    Check((await store.ReadUsageAsync()).State == "not-collected", "old store without metric tables");
    await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False");
    await connection.OpenAsync();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'cortex_usage_%';";
    Check((long)command.ExecuteScalar()! == 0, "read migrated schema");
});

static async Task RetentionAsync() => await Fixture(async (_, store) =>
{
    await store.InitializeAsync();
    await using var old = new SqliteCortexV2Store(new(store.DatabasePath), new FixedClock(DateTimeOffset.UtcNow.AddDays(-100)));
    await old.RecordUsageAsync(Sample());
    await store.RecordUsageAsync(Sample());
    var report = await store.ReadUsageAsync(90);
    Check(report.Rows.Single(x => x.Dimension == "total").Calls == 1, "expired aggregates not retired");
    Check(report.RetentionDays == 90 && (await store.FullDoctorAsync()).Healthy, "retention altered semantic store");
});

static async Task McpAsync() => await Fixture(async (_, store) =>
{
    await store.InitializeAsync();
    await using var operational = new SqliteCortexV2OperationalStore(store, "test");
    var core = new CortexV2Application(operational, "test_actor");
    var observer = new CortexUsageRecorder(store, "mcp");
    var adapter = new CortexV2McpApplication(core, observer);
    async Task<System.Text.Json.Nodes.JsonObject> Call(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await adapter.CallToolAsync(name, doc.RootElement, CancellationToken.None);
    }
    var created = await Call("cortex_task", """{"action":"create","namespace":"project.fixture","taskId":"10000000-0000-0000-0000-000000000001","title":"Fixture","content":"PRIVATE_BODY_SENTINEL","idempotencyKey":"fixture-create"}""");
    Check(created["isError"]!.GetValue<bool>() == false, created.ToJsonString());
    var search = await Call("cortex_search", """{"namespaces":["project.fixture"],"text":"PRIVATE_QUERY_SENTINEL","format":"compact"}""");
    Check(search["isError"]!.GetValue<bool>() == false, "search failed");
    var read = await Call("cortex_read", """{"namespace":"project.fixture","recordType":"task","recordId":"10000000-0000-0000-0000-000000000001"}""");
    Check(read.ToJsonString().Contains("PRIVATE_BODY_SENTINEL"), "exact read changed body");
    await Call("cortex_search", """{"namespaces":["project.fixture"],"text":"x","limit":99999}""");
    var status = await Call("cortex_status", "{}");
    Check(status["structuredContent"]?["utilization"]?["state"]?.GetValue<string>() == "available", "status has no durable report");
    Check(JsonSerializer.SerializeToUtf8Bytes(status["structuredContent"]!["utilization"]).Length <= 2048,
        "status metrics summary is unbounded");
    var report = await store.ReadUsageAsync();
    Check(report.Rows.Where(x => x.Dimension == "total").Sum(x => x.Calls) == 5, "MCP calls lost");
    Check(report.Rows.Where(x => x.Dimension == "total").Sum(x => x.Failures) == 1, "validation failure not counted");
    Check(report.Rows.Where(x => x.Dimension == "total").Sum(x => x.ExactRecords) == 1, "exact read count");
    var json = JsonSerializer.Serialize(report);
    Check(!json.Contains("PRIVATE_") && !json.Contains("Fixture"), "sensitive payload in metrics");
    Check(observer.LostSamples == 0, "normal collector dropped samples");
});

static async Task LockedAsync() => await Fixture(async (_, store) =>
{
    await store.InitializeAsync();
    await store.RecordUsageAsync(Sample());
    await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
    await connection.OpenAsync();
    using var transaction = connection.BeginTransaction();
    var observer = new CortexUsageRecorder(store, "mcp");
    var timer = Stopwatch.StartNew();
    await observer.RecordAsync("status", [], false, 0, 0, 12, Stopwatch.GetTimestamp());
    Check(observer.LostSamples == 1 && timer.Elapsed < TimeSpan.FromSeconds(4), "lock not bounded/exposed");
    transaction.Rollback();
    Check((await store.ReadUsageAsync()).Rows.Single(x => x.Dimension == "total").Calls == 1, "failed metric partially committed");
});

static async Task BoundsAsync() => await Fixture(async (_, store) =>
{
    foreach (var invalid in new[] { Sample() with { Channel = "SECRET" }, Sample() with { Operation = "query body" },
        Sample() with { Namespaces = ["invalid\nnamespace"] }, Sample() with { ResponseBytes = long.MaxValue },
        Sample() with { TaskId = "not-a-task-guid" } })
    {
        try { await store.RecordUsageAsync(invalid); throw new Exception("invalid metric accepted"); }
        catch (ArgumentException) { }
    }
    Check(!File.Exists(store.DatabasePath), "invalid metrics touched disk");
});

static async Task OverheadAsync() => await Fixture(async (_, store) =>
{
    await store.InitializeAsync();
    var session = Guid.NewGuid().ToString("N");
    var times = new List<double>();
    for (var i = 0; i < 50; i++)
    {
        var elapsed = Stopwatch.StartNew();
        await store.RecordUsageAsync(Sample(session));
        times.Add(elapsed.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    Console.WriteLine($"METRIC collector local-fixture samples=50 medianMs={times[24]:F3} p95Ms={times[47]:F3} maxMs={times[^1]:F3}");
    // A fresh connection defaults to SQLite's engine ceiling. Inspect the actual
    // metrics connection after applying exactly the helper used by RecordUsageAsync.
    var factory = typeof(SqliteCortexV2Store).GetMethod("CreateUsageConnection", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await using var connection = (SqliteConnection)factory.Invoke(store, [false])!;
    await connection.OpenAsync();
    var configure = typeof(SqliteCortexV2Store).GetMethod("ApplyPageCeilingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await (ValueTask)configure.Invoke(store, [connection, CancellationToken.None])!;
    using var command = connection.CreateCommand(); command.CommandText = "PRAGMA max_page_count;";
    Check(Convert.ToInt64(command.ExecuteScalar()) == 1_048_576, "metrics bypassed default page ceiling");
});

static async Task ProtocolAsync() => await Fixture(async (_, store) =>
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("rpc"); info.ArgumentList.Add(store.DatabasePath);
    using var process = Process.Start(info)!;
    try
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        foreach (var line in new[] {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"usage-fixture","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"cortex_status","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cortex_status","arguments":{}}}"""
        }) await process.StandardInput.WriteLineAsync(line);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        Check(process.ExitCode == 0, await error);
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 3, "protocol output contains diagnostics or missing responses");
        using var last = JsonDocument.Parse(lines[^1]);
        Check(last.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("utilization")
            .GetProperty("calls").GetInt64() == 1, "status did not see previous process call");
        Check((await store.ReadUsageAsync()).Rows.Single(x => x.Dimension == "total").Calls == 2,
            "process close lost usage");
    }
    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
});

static async Task Fixture(Func<string, SqliteCortexV2Store, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), "cortex-util-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db")));
        await action(root, store);
    }
    finally
    {
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Unexpected fixture reparse point");
        Directory.Delete(root, recursive: true);
    }
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
