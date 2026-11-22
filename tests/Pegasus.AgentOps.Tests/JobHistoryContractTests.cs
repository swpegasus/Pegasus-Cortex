using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class JobHistoryContractTests
{
    internal static async Task RunAsync()
    {
        const string cutoff = "2026-11-20T23:46:29Z";
        await using var dispatcher = new HistoryCaptureDispatcher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await PaoProgram.RunCliAsync(
            ["job", "prune", "mode=discard_unresolved", "before=" + cutoff, "max=128", "rid=cleanup-contract-fixture"],
            dispatcher, output, error, CancellationToken.None);
        Check(code == 0 && dispatcher.Last is { Op: "job" } &&
            dispatcher.Last.Arg.GetValueOrDefault("op") == "prune" &&
            dispatcher.Last.Arg.GetValueOrDefault("mode") == "discard_unresolved" &&
            dispatcher.Last.Arg.GetValueOrDefault("before") == cutoff &&
            dispatcher.Last.Ws is null && dispatcher.Last.Cwd is null &&
            dispatcher.Last.Rid == "cleanup-contract-fixture",
            "Typed CLI cleanup lost its explicit mode or cutoff; exit=" + code + "; error=" + error.ToString());

        code = await PaoProgram.RunCliAsync(
            ["job", "ls", "state=unresolved", "before=" + cutoff, "after=100", "max=10"],
            dispatcher, output, error, CancellationToken.None);
        Check(code == 0 && dispatcher.Last!.Arg.GetValueOrDefault("state") == "unresolved" &&
            dispatcher.Last.Arg.GetValueOrDefault("after") == "100",
            "History preview did not preserve its bounded filters.");

        foreach (var extra in new[] { "root=AB12", "wait=false", "ws=unrelated", "jid=unrelated", "max=257", "mode=erase", "before=yesterday" })
        {
            var calls = dispatcher.Calls;
            var args = new List<string> { "job", "prune" };
            if (!extra.StartsWith("mode=", StringComparison.Ordinal)) args.Add("mode=details");
            if (!extra.StartsWith("before=", StringComparison.Ordinal)) args.Add("before=" + cutoff);
            args.Add(extra);
            code = await PaoProgram.RunCliAsync(args.ToArray(), dispatcher, output, error, CancellationToken.None);
            Check(code == 2 && dispatcher.Calls == calls,
                "Invalid cleanup scope reached execution: " + extra);
        }

        using var valid = JsonDocument.Parse("""
            {"op":"job_prune","mode":"details","before":"2026-11-20T23:46:29Z","max":8,"output":"agent"}
            """);
        var beforeCalls = dispatcher.Calls;
        var response = await PaoProgram.CallUnifiedMcpToolAsync(valid.RootElement, dispatcher, CancellationToken.None);
        Check(dispatcher.Calls == beforeCalls + 1 && dispatcher.Last!.Arg.GetValueOrDefault("op") == "prune",
            "Unified MCP did not reach the same typed cleanup contract: " + response.ToJsonString());
        using var projected = JsonDocument.Parse(response["content"]![0]!["text"]!.GetValue<string>());
        Check(projected.RootElement.GetProperty("pruned").GetInt32() == 0 &&
            !projected.RootElement.TryGetProperty("before", out _) &&
            !projected.RootElement.TryGetProperty("rid_barriers_preserved", out _) &&
            !projected.RootElement.TryGetProperty("next", out _),
            "Agent cleanup output repeated its inputs or internal audit metadata.");

        using var forbidden = JsonDocument.Parse("""
            {"op":"job_prune","mode":"details","before":"2026-11-20T23:46:29Z","root":"AB12"}
            """);
        beforeCalls = dispatcher.Calls;
        var refused = await PaoProgram.CallUnifiedMcpToolAsync(forbidden.RootElement, dispatcher, CancellationToken.None);
        Check(dispatcher.Calls == beforeCalls && refused["isError"]?.GetValue<bool>() == true,
            "Unified MCP accepted privileged history targeting.");
        Console.WriteLine("PASS bounded job-history CLI/MCP scope and owner-route contract (captured dispatcher only)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HistoryCaptureDispatcher : IOperationDispatcher
    {
        internal OperationRequest? Last;
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            Calls++;
            return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"),
                request.Rid ?? Guid.NewGuid().ToString("N"), "done", OperationNames.Public(request),
                Data: new Dictionary<string, object?>
                {
                    ["pruned"] = 0, ["more"] = false, ["next"] = 0,
                    ["before"] = "2026-11-20T23:46:29Z", ["mode"] = "details",
                    ["outcomes_unchanged"] = true, ["rid_barriers_preserved"] = true
                }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
