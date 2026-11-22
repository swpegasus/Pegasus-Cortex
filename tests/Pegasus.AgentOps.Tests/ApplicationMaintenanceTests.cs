using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static partial class ApplicationCapacityTests
{
    internal static async Task InventoryReleaseAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        var generations = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < UserApplicationManager.MaxRetainedApplications; i++)
        {
            var started = await manager.StartAsync(Configuration, "retained-" + i, ["private-value"], fixture.Path);
            generations.Add(started);
            await manager.StopAsync(Aid(started));
        }
        var page = await manager.InventoryAsync();
        Check(Int(page, "retained") == 128 && Int(page, "active") == 0 && Text(page, "admission") == "retained",
            "Idle applications and a full retained catalog were conflated.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        while (true)
        {
            pages++;
            var items = Rows(page);
            Check(items.Count <= 8, "Inventory page exceeded its bound.");
            foreach (var row in items) Check(seen.Add(Aid(row)), "Inventory repeated an aid.");
            Check(!JsonSerializer.Serialize(page, Json).Contains("private-value", StringComparison.Ordinal),
                "Inventory exposed invocation data.");
            var next = page.GetValueOrDefault("next")?.ToString();
            if (next is null) break;
            page = await manager.InventoryAsync(cursor: next);
        }
        Check(seen.Count == 128 && pages == 16, "Inventory omitted a retained generation.");
        var initial = await manager.InventoryAsync(maximum: 1);
        var cursor = Text(initial, "next"); var epoch = Text(initial, "epoch");
        var target = Aid(generations[0]); var terminations = handles.Sum(handle => handle.TerminationRequests);
        await WaitReleasableAsync(manager, target);
        var released = await manager.ReleaseAsync(target, epoch);
        Check(Text(released, "state") == "released" && Int(released, "freedRetained") == 1 &&
            handles.Sum(handle => handle.TerminationRequests) == terminations,
            "Releasing retired metadata requested process termination.");
        await RejectAsync("app_inventory_changed", () => manager.InventoryAsync(cursor: cursor));
        await RejectAsync("not_found", () => manager.StatusAsync("retained-0"));
        var replacement = await manager.StartAsync(Configuration, "retained-0", ["private-value"], fixture.Path);
        Check(Aid(replacement) != target && handles.Count == 129 &&
            Int(await manager.InventoryAsync(), "retained") == 128, "One release did not permit exactly one fresh admission.");
        await RejectAsync("app_capacity", () => manager.StartAsync(Configuration, "still-full", [], fixture.Path));
        await RejectAsync("app_not_releasable", () => manager.ReleaseAsync(Aid(replacement), epoch));
        Check(handles[^1].TerminationRequests == 0, "Rejecting release touched the live replacement.");
    }

    internal static async Task InventoryUnknownAndRetirementAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        var first = await manager.StartAsync(Configuration, "retiring", ["private-value"], fixture.Path);
        var original = handles[0];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.RetirementGate = gate;
        try
        {
            var epoch = Text(await manager.InventoryAsync(), "epoch");
            await RejectAsync("app_not_releasable", () => manager.ReleaseAsync(Aid(first), epoch));
            Check(original.TerminationRequests == 0, "Release stopped a live generation.");
            original.ObservationUnavailable = true;
            var unknown = await manager.InventoryAsync();
            Check(unknown["active"] is null && Int(unknown, "unknown") == 1 && Text(unknown, "admission") == "unknown",
                "Unknown application observation was reported as zero active.");
            await RejectAsync("app_not_releasable", () => manager.ReleaseAsync(Aid(first), epoch));
            Check(original.TerminationRequests == 0 && Int(unknown, "retained") == 1,
                "Unknown observation discarded or stopped a generation.");
            original.ObservationUnavailable = false;
            original.FinishWorkers();
            await WaitConditionAsync(async () => Text(await manager.StatusAsync("retiring"), "state") == "exited");
            var pending = await manager.InventoryAsync();
            Check(!Convert.ToBoolean(Rows(pending).Single()["releasable"]), "Terminal state hid pending retirement.");
            var before = original.TerminationRequests;
            await RejectAsync("app_not_releasable", () => manager.ReleaseAsync(Aid(first), epoch));
            Check(original.TerminationRequests == before, "Pending retirement release repeated termination.");
            gate.TrySetResult();
            await WaitReleasableAsync(manager, Aid(first));
            await RejectAsync("precondition", () => manager.ReleaseAsync(Aid(first), Guid.NewGuid().ToString("N")));
            await manager.ReleaseAsync(Aid(first), epoch);
            Check(Int(await manager.InventoryAsync(), "retained") == 0 && original.TerminationRequests == before,
                "Confirmed retirement failed exact reclamation.");
        }
        finally { original.ObservationUnavailable = false; gate.TrySetResult(); }
    }

    internal static async Task ReleasePreservesSuccessorAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        var first = await manager.StartAsync(Configuration, "slot", [], fixture.Path);
        await manager.StopAsync(Aid(first)); await WaitReleasableAsync(manager, Aid(first));
        var successor = await manager.StartAsync(Configuration, "slot", [], fixture.Path,
            expectedAid: Aid(first), rid: "successor");
        var epoch = Text(await manager.InventoryAsync(), "epoch");
        await manager.ReleaseAsync(Aid(first), epoch);
        Check(Aid(await manager.StatusAsync("slot")) == Aid(successor) && handles[1].TerminationRequests == 0,
            "Releasing a predecessor removed or stopped its successor.");
        await RejectAsync("app_owner_lost", () => manager.ReplayStartAsync(Aid(first), Configuration, "slot", [],
            fixture.Path, null, null, "first", CancellationToken.None));
        await RejectAsync("precondition", () => manager.StartAsync(Configuration, "slot", [], fixture.Path,
            expectedAid: Aid(first), rid: "old-restart"));
        Check(handles.Count == 2, "A released predecessor was implicitly relaunched.");
        await manager.StopAsync(Aid(successor)); await WaitReleasableAsync(manager, Aid(successor));
        await manager.ReleaseAsync(Aid(successor), epoch);
        var fresh = await manager.StartAsync(Configuration, "slot", [], fixture.Path);
        Check(Aid(fresh) != Aid(first) && Aid(fresh) != Aid(successor), "Name reuse recycled an aid.");
    }

    internal static async Task InventoryAtActiveCapacityAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        for (var i = 0; i < 16; i++) await manager.StartAsync(Configuration, "active-inventory-" + i, ["private-value"], fixture.Path);
        var page = await manager.InventoryAsync();
        Check(Int(page, "active") == 16 && Int(page, "retained") == 16 && Text(page, "admission") == "active",
            "Read-only inventory failed to distinguish active saturation.");
        await RejectAsync("app_not_releasable", () => manager.ReleaseAsync(Aid(Rows(page)[0]), Text(page, "epoch")));
        Check(handles.All(handle => handle.TerminationRequests == 0), "Inventory or release rejection stopped a live fake.");
        await RejectAsync("invalid", () => manager.InventoryAsync(9));
        var previousCursor = Text(page, "next");
        await manager.StopAsync(Aid(Rows(page)[0]));
        await manager.StartAsync(Configuration, "new-generation", [], fixture.Path);
        await RejectAsync("app_inventory_changed", () => manager.InventoryAsync(cursor: previousCursor));
    }

    internal static async Task MaintenanceProjectionAsync()
    {
        OperationRequest? captured = null; var calls = 0;
        var epoch = Guid.NewGuid().ToString("N"); var aid = Guid.NewGuid().ToString("N");
        var dispatcher = new RecordingDispatcher(request =>
        {
            captured = request; calls++;
            return new OperationReceipt(true, "jid", request.Rid ?? "rid", "done", OperationNames.Public(request),
                Data: new Dictionary<string, object?> { ["epoch"] = epoch, ["retained"] = 128, ["active"] = 0, ["items"] = Array.Empty<object>() });
        });
        var inventory = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_inventory", max = 8 }), dispatcher, CancellationToken.None, defaultWs: "unused-default");
        Check(calls == 1 && captured?.Op == "app" && captured.Arg["op"] == "inventory" &&
            captured.Ws is null && captured.Cwd is not null,
            "Read-only inventory was not mapped without start admission.");
        var prior = calls;
        var missingRid = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_release", aid, epoch }), dispatcher, CancellationToken.None);
        Check(calls == prior && missingRid["isError"]!.GetValue<bool>(), "Release without an explicit RID reached dispatch.");
        await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_release", aid, epoch, rid = "release-once" }), dispatcher, CancellationToken.None, defaultWs: "unused-default");
        Check(calls == prior + 1 && captured?.Arg["op"] == "release" && captured.Rid == "release-once" && captured.Ws is null,
            "Release lost its typed aid/epoch/RID contract.");
        var invalid = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_inventory", max = 9 }), dispatcher, CancellationToken.None);
        Check(invalid["isError"]!.GetValue<bool>() && calls == prior + 1, "Oversized inventory reached dispatch.");
        var agent = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_inventory", output = "agent" }), dispatcher, CancellationToken.None, defaultWs: "unused-default");
        var agentText = agent["content"]![0]!["text"]!.GetValue<string>();
        Check(agent["structuredContent"] is null && agentText.Contains(epoch, StringComparison.Ordinal) &&
            agentText.Contains("\"retained\":128", StringComparison.Ordinal) && agentText.Contains("\"active\":0", StringComparison.Ordinal),
            "Agent output lost the inventory coordinates or meaningful zero count.");
        using var cliOutput = new StringWriter(); using var cliError = new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json", "app", "inventory", "max=8"], dispatcher,
            cliOutput, cliError, CancellationToken.None) == 0 && captured?.Arg["op"] == "inventory" && captured.Cwd is not null,
            "CLI inventory lost its typed operation or ordinary working-directory context.");
        Check(await PaoProgram.RunCliAsync(["--json", "app", "release", aid, "epoch=" + epoch, "rid=cli-release"],
            dispatcher, cliOutput, cliError, CancellationToken.None) == 0 && captured?.Arg["op"] == "release" && captured.Rid == "cli-release",
            "CLI release lost its explicit aid/epoch/RID mapping.");
        prior = calls;
        var leakedField = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            { op = "app_inventory", arg = new[] { "private-value" } }), dispatcher, CancellationToken.None);
        Check(leakedField["isError"]!.GetValue<bool>() && calls == prior, "Inventory admitted invocation-body fields.");
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(IReadOnlyDictionary<string, object?> value)
        => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)value["items"]!;
    private static int Int(IReadOnlyDictionary<string, object?> value, string key) => Convert.ToInt32(value[key]);
    private static string Text(IReadOnlyDictionary<string, object?> value, string key) => value[key]!.ToString()!;
    private static async Task WaitReleasableAsync(UserApplicationManager manager, string aid)
        => await WaitConditionAsync(async () =>
        {
            var cursor = (string?)null;
            do
            {
                var page = await manager.InventoryAsync(cursor: cursor);
                var row = Rows(page).SingleOrDefault(value => Aid(value) == aid);
                if (row is not null) return Convert.ToBoolean(row["releasable"]);
                cursor = page.GetValueOrDefault("next")?.ToString();
            } while (cursor is not null);
            return false;
        });
    private static async Task WaitConditionAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        do { if (await condition()) return; await Task.Delay(10); } while (DateTime.UtcNow < until);
        throw new InvalidOperationException("Bounded fake lifecycle observation did not complete.");
    }
}
