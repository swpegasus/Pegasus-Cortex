using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Contract-only dispatch capture. No engine, pipe, service, process, credential,
// filesystem mutation or connection approval is created by these fixtures.
internal static class SystemIntentContractTests
{
    internal static async Task RunAsync()
    {
        await using var capture = new Capture();
        var target = Path.Combine(Path.GetTempPath(), "system-intent-capture-" + Guid.NewGuid().ToString("N"));
        const string jid = "11111111111111111111111111111111";

        foreach (var arguments in new string[][]
        {
            ["diag", "system=false"],
            ["mkdir", target, "system=false", "wait=false"],
            ["job", "stat", "ordinary-id", "system=false", "ws=fixture"],
            ["system=false", "job", "stat", "ordinary-id"],
            ["job", "cancel", "ordinary-id", "system=false"]
        })
        {
            var before = capture.Calls;
            using var output = new StringWriter(); using var error = new StringWriter();
            Check(await PaoProgram.RunCliAsync(arguments, capture, output, error, default) == 0 &&
                capture.Calls == before + 1, "false System intent changed ordinary CLI admission");
            Ordinary(capture.Last!);
            if (arguments.Contains("wait=false", StringComparer.Ordinal))
                Check(capture.Last!.Arg.GetValueOrDefault("wait") == "false", "false System intent erased deferred ordinary completion");
            if (arguments.Contains("ws=fixture", StringComparer.Ordinal))
                Check(capture.Last!.Ws == "fixture" && capture.Last.Cwd is null, "false System intent erased ordinary workspace selection");
        }

        foreach (var arguments in new string[][]
        {
            ["admin", "stat", "jid=" + jid, "system=false"],
            ["service", "status", ApplicationAuthorizationProtocol.CerebralServiceName, "system=false"],
            ["mkdir", target, "system=true", "wait=false"],
            ["job", "cancel", jid, "system=true"],
            ["run", "fixture", "system=true"],
            ["diag", "system=AB12"], ["diag", "root=AB12"],
            ["root", "ls"], ["system", "ls"], ["system", "upsert", "fixture", "--stdin"]
        })
        {
            var before = capture.Calls;
            using var output = new StringWriter(); using var error = new StringWriter();
            Check(await PaoProgram.RunCliAsync(arguments, capture, output, error, default, new StringReader("AB12\n")) == 2 &&
                capture.Calls == before, "protected or retired CLI input reached dispatch without valid intent");
        }

        await McpAsync(capture, new { op = "job_stat", jid = "ordinary-id", system = false }, "fixture", expectedSystem: false);
        Check(capture.Last!.Ws == "fixture" && capture.Last.Cwd is null,
            "false System intent changed ordinary MCP workspace default");
        await McpAsync(capture, new { op = "mkdir", dst = target, system = false, wait = false }, "fixture", expectedSystem: false);
        Check(capture.Last!.Arg.GetValueOrDefault("wait") == "false", "MCP ordinary deferred mode changed");
        await McpAsync(capture, new { op = "job_stat", jid, system = true }, "must-not-apply", expectedSystem: true);
        Check(capture.Last!.Ws is null && capture.Last.Cwd is null,
            "true System job observation acquired an ordinary workspace or cwd");
        await McpAsync(capture, new { op = "service_status", service = ApplicationAuthorizationProtocol.CerebralServiceName, system = true },
            "must-not-apply", expectedSystem: true);

        foreach (var value in new object[]
        {
            new { op = "admin_stat", jid, system = false },
            new { op = "service_status", service = ApplicationAuthorizationProtocol.CerebralServiceName, system = false },
            new { op = "job_stat", jid = "ordinary-id", system = true },
            new { op = "job_cancel", jid, system = true },
            new { op = "mkdir", dst = target, system = true, wait = false },
            new { op = "run", cfg = "fixture", system = true },
            new { op = "diag", system = "AB12" },
            new { op = "diag", root = "AB12" },
            new { op = "diag", connectionAuthority = new { authority = jid, connectionId = jid, credentialGeneration = jid, grantRevision = 1 } }
        })
        {
            var before = capture.Calls;
            var response = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(value), capture, default);
            Check(response["isError"]?.GetValue<bool>() == true && capture.Calls == before,
                "protected or forged MCP input reached dispatch");
        }

        foreach (var operation in new[] { "admin_admit", "admin_stat", "service_status", "job_stat", "windows_service_read", "windows_task_read" })
        {
            var help = PaoProgram.UnifiedMcpOperationHelp(operation) ?? throw new InvalidOperationException("operation help missing");
            var text = help.ToJsonString();
            Check(!text.Contains("explicit root", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("root required", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("transient root", StringComparison.OrdinalIgnoreCase), "active help still instructs root-code authorization");
        }
        Check(PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["system"]!["type"]!.GetValue<string>() == "boolean",
            "public System intent is not boolean");
        Check(!Directory.Exists(target), "capture-only fixture performed a filesystem operation");
        Console.WriteLine("PASS System intent contract: false retains ordinary routing/deferred completion; true requires protected intent; legacy codes and caller authority refused; capture fixtures only");
    }

    private static async Task McpAsync(Capture capture, object value, string workspace, bool expectedSystem)
    {
        var before = capture.Calls;
        var response = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(value), capture, default, defaultWs: workspace);
        Check(response["isError"]?.GetValue<bool>() != true && capture.Calls == before + 1 && capture.Last!.System == expectedSystem,
            $"MCP boolean System intent changed: calls={before}->{capture.Calls}, expectedSystem={expectedSystem}, " +
            $"capturedSystem={capture.Last?.System}, isError={response["isError"]?.GetValue<bool>()}");
        Check(capture.Last!.Root is null && capture.Last.ConnectionAuthority is null && !capture.Last.Arg.ContainsKey("system"),
            "public intent supplied a credential or retained transport authority in arguments");
    }

    private static void Ordinary(OperationRequest request) => Check(!request.System && request.Root is null &&
        request.ConnectionAuthority is null && !request.Arg.ContainsKey("system"), "ordinary intent acquired protected authority");

    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls { get; private set; }
        internal OperationRequest? Last { get; private set; }
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            if (request.Op == "job" && request.Arg.GetValueOrDefault("op") is "stat" or "wait" or "cancel")
            {
                // The real compact formatter verifies selected observations.
                // Supply its actual wrapper/original shape instead of turning
                // a valid captured request into an unverified mock response.
                var original = new OperationReceipt(true, request.Arg["jid"], "captured-original", "done", "diag");
                return Task.FromResult(request.Arg["op"] == "wait" ? original :
                    new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid ?? "captured", "done",
                        OperationNames.Public(request), Data: new Dictionary<string, object?> { ["rec"] = original }));
            }
            return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid ?? "captured",
                "done", OperationNames.Public(request)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
