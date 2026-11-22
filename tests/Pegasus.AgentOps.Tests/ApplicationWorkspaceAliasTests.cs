using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ApplicationWorkspaceAliasTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("alias-state");
        var work = fixture.Dir("private workspace Ω");
        new WorkspaceRegistry(new AgentOpsPaths(state)).Register("app-workspace", work);
        await using var engine = new AgentOpsEngine(state, enableUserApplications: true);
        var dispatcher = new EngineDispatcher(engine);

        // The deliberately unregistered tool makes accepted workspace resolution
        // observable without any launch or changes to installed tool definitions.
        OperationRequest Start(string? ws) => new("app",
            new Dictionary<string, string> { ["op"] = "start", ["cfg"] = "unregistered-alias-fixture", ["name"] = "alias-fixture" },
            Ws: ws, Cwd: work);
        OperationReceipt? rejectedPath = null;
        foreach (var ws in new[] { work, "unregistered-private-alias" })
        {
            var rejected = await engine.ExecuteAsync(Start(ws));
            AssertAliasFailure(rejected);
            var evidence = JsonSerializer.Serialize(rejected.Failure, Json);
            Check(!evidence.Contains(ws, StringComparison.Ordinal) &&
                !evidence.Contains(JsonSerializer.Serialize(ws, Json)[1..^1], StringComparison.Ordinal),
                "Workspace rejection echoed the supplied alias or private path.");
            rejectedPath ??= rejected;
        }
        foreach (var ws in new string?[] { "app-workspace", "APP-WORKSPACE", null })
        {
            var admitted = await engine.ExecuteAsync(Start(ws));
            Check(!admitted.Ok && admitted.Err == "not_found" &&
                admitted.Failure?.Diagnostics?.Any(item => item.Code == "workspace_alias_unknown") != true,
                "Registered, case-insensitive, or omitted workspace no longer reaches tool admission.");
        }

        var compact = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
        {
            op = "app_start", cfg = "unregistered-alias-fixture", name = "alias-fixture", ws = work, cwd = work, output = "agent"
        }), dispatcher, CancellationToken.None);
        var text = compact["content"]![0]!["text"]!.GetValue<string>();
        Check(compact["isError"]!.GetValue<bool>() && !compact.ContainsKey("structuredContent") &&
            text.Contains("field=ws", StringComparison.Ordinal) &&
            text.Contains("execution=not_dispatched source=application-workspace-resolution", StringComparison.Ordinal) &&
            text.Contains("ctx_ls", StringComparison.Ordinal) && text.Contains("pao ctx ls", StringComparison.Ordinal) &&
            text.Contains("cwd", StringComparison.Ordinal) && !text.Contains(work, StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(text) <= 4096,
            "Compact MCP rejection lost actionable alias guidance or bounded private evidence.");

        var structured = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
        {
            op = "app_start", cfg = "unregistered-alias-fixture", name = "alias-fixture",
            ws = work, cwd = work, output = "structured"
        }), dispatcher, CancellationToken.None);
        AssertAliasFailure(structured["structuredContent"]!.Deserialize<OperationReceipt>(Json)!);

        var saved = await engine.ExecuteAsync(new OperationRequest("job",
            new Dictionary<string, string> { ["op"] = "stat", ["jid"] = rejectedPath!.Jid }, Detail: true));
        var retained = JsonSerializer.SerializeToElement(saved.Data!["rec"], Json).Deserialize<OperationReceipt>(Json)!;
        AssertAliasFailure(retained);

        var help = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
        {
            op = "help", name = "app_start", output = "structured"
        }), dispatcher, CancellationToken.None);
        var data = help["structuredContent"]!["data"]!;
        var constraints = data["constraints"]!.GetValue<string>();
        Check(constraints.Contains("registered workspace alias", StringComparison.Ordinal) &&
            constraints.Contains("ctx_ls", StringComparison.Ordinal), "Local help omitted alias discovery.");
        using var example = JsonDocument.Parse(data["mcp_example"]!.GetValue<string>());
        Check(example.RootElement.GetProperty("ws").GetString() == "project" &&
            Path.IsPathFullyQualified(example.RootElement.GetProperty("cwd").GetString()!) &&
            data["cli_example"]!.GetValue<string>().Contains("ws=project", StringComparison.Ordinal),
            "Help examples did not distinguish workspace alias from absolute cwd.");
        var schema = PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["ws"]!["description"]!.GetValue<string>();
        Check(schema.Contains("registered workspace alias", StringComparison.Ordinal) &&
            schema.Contains("ctx_ls", StringComparison.Ordinal) && schema.Contains("app_start", StringComparison.Ordinal),
            "Live tool schema omitted the app_start alias requirement.");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Check(await PaoProgram.RunCliAsync(["help", "app_start"], dispatcher, stdout, stderr, CancellationToken.None) == 0 &&
            stdout.ToString().Contains("registered workspace alias", StringComparison.Ordinal) &&
            stdout.ToString().Contains("ws=project", StringComparison.Ordinal),
            "CLI help did not expose the same alias guidance.");
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        Check(await PaoProgram.RunCliAsync(["app", "start", "unregistered-alias-fixture", "alias-fixture",
            "ws=" + work, "cwd=" + work], dispatcher, stdout, stderr, CancellationToken.None) == 3 &&
            Uri.UnescapeDataString(stdout.ToString()).Contains("Unknown workspace alias", StringComparison.Ordinal) &&
            Uri.UnescapeDataString(stdout.ToString()).Contains("pao ctx ls", StringComparison.Ordinal),
            "Legacy CLI rejection hid the actionable workspace-alias remedy.");
        Console.WriteLine("EVIDENCE isolated engine/SQLite and MCP projection checks; local schema/CLI help; no application launch or installed configuration change.");
    }

    private static void AssertAliasFailure(OperationReceipt receipt)
    {
        var failure = receipt.Failure;
        Check(!receipt.Ok && receipt.Err == "precondition" && receipt.ExitCode == 3 && !receipt.Partial &&
            failure?.Field == new OperationFailureField("ws") &&
            failure.Execution == new OperationFailureFact(OperationExecutionState.NotDispatched, "application-workspace-resolution") &&
            failure.Retry == new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "application-workspace-resolution") &&
            failure.Diagnostics?.Single().Code == "workspace_alias_unknown" &&
            failure.Message.Contains("Unknown workspace alias", StringComparison.Ordinal) &&
            failure.Next!.Contains("ctx_ls", StringComparison.Ordinal) &&
            failure.DetailAvailability == "none" && failure.LogAvailability == "not_retained",
            "Unknown workspace did not retain its stable error and explicit pre-launch alias evidence.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
