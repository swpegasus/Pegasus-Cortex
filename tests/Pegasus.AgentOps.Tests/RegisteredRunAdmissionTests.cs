using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class RegisteredRunAdmissionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("timeout-state");
        var executable = Path.Combine(fixture.Path, "admission-fixture.exe");
        await File.WriteAllTextAsync(executable, "Inert fixture; never executed.", new UTF8Encoding(false));
        var runner = new ProbeFixtureRunner();
        var operations = new ToolRecipeOperations(state, runner);
        await operations.Tools.RegisterAsync(new RegisteredToolDefinition(
            "timeout-fixture", 1, executable, TimeoutSeconds: 600,
            MaxArguments: 2, MaxArgumentUtf8Bytes: 16, MaxTotalArgumentUtf8Bytes: 24,
            VersionProbe: new ToolProbeDefinition(["--version"], RequiredText: "fixture")));

        foreach (var timeout in new[] { -1, 0, 601, 900 })
        {
            var rejected = Capture(() => operations.PrepareTool("timeout-fixture", [], timeoutSeconds: timeout));
            AssertTimeout(rejected.Code, rejected.Failure, timeout);
        }
        foreach (var timeout in new int?[] { 1, 120, 600, null })
            Check(operations.PrepareTool("timeout-fixture", [], timeoutSeconds: timeout).Timeout ==
                TimeSpan.FromSeconds(timeout ?? 600), "An admitted boundary or registered default changed.");
        Check(runner.Calls == 1, "Admission started a process instead of rejecting the request.");

        AssertArgument(operations, ["canary-one", "canary-two", "canary-three"], null, 3, 2, "items");
        AssertArgument(operations, ["private-value-must-never-be-echoed"], 0, 34, 16, "utf8_bytes");
        AssertArgument(operations, ["AAAAAAAAAAAAA", "BBBBBBBBBBBBB"], 1, 26, 24, "utf8_bytes");
        var invalidText = Capture(() => operations.PrepareTool("timeout-fixture", ["canary\0value"]));
        Check(invalidText.Code == "invalid" && invalidText.Failure?.Field == new OperationFailureField("arg", 0),
            "Invalid argument content lost its exact field/index.");
        AssertPrivate(invalidText.Failure!, "canary", executable);

        string jid;
        OperationFailureEvidence failure;
        await using (var engine = new AgentOpsEngine(state,null,false,null,false,null,maintenanceMode:new MaintenanceModeStore(Path.Combine(fixture.Path,"private-machine-mode"),protect:false)))
        {
            // The process runner is not mocked here: admission must stop before
            // attempting to launch the inert fixture. No external process runs.
            var structured = await CallAsync(engine, new
            {
                op = "run", cfg = "timeout-fixture", cwd = fixture.Path,
                arg = new[] { "private-value" }, tmo = 900, output = "structured", rid = "timeout-rejected"
            });
            var receipt = structured["structuredContent"]!.Deserialize<OperationReceipt>(Json)!;
            Check(!receipt.Ok && receipt.ExitCode == 3 && !receipt.Partial && receipt.State == "failed",
                $"Timeout admission changed the stable precondition exit/state contract: err={receipt.Err} exit={receipt.ExitCode} state={receipt.State}.");
            AssertTimeout(receipt.Err!, receipt.Failure, 900);
            Check(receipt.Data is not null && !receipt.Data.ContainsKey("exit") && !receipt.Data.ContainsKey("out"),
                "Admission rejection manufactured process execution/output evidence.");
            jid = receipt.Jid;
            failure = receipt.Failure!;
            AssertPrivate(failure, "private-value", fixture.Path);

            var compact = await CallAsync(engine, new
            {
                op = "run", cfg = "timeout-fixture", cwd = fixture.Path,
                arg = new[] { "private-value" }, tmo = 900, output = "agent"
            });
            var text = compact["content"]![0]!["text"]!.GetValue<string>();
            Check(compact["isError"]!.GetValue<bool>() && !compact.ContainsKey("structuredContent") &&
                text.Contains("field=tmo actual=900 max=600 unit=seconds", StringComparison.Ordinal) &&
                text.Contains("execution=not_dispatched source=registered-tool-admission retry=after_correction", StringComparison.Ordinal) &&
                text.Contains("Set tmo to a value from 1 through 600 seconds", StringComparison.Ordinal) &&
                text.EndsWith("detail=none log=not_retained", StringComparison.Ordinal) &&
                !text.Contains("private-value", StringComparison.Ordinal) && !text.Contains(fixture.Path, StringComparison.Ordinal) &&
                Encoding.UTF8.GetByteCount(text) <= 4096,
                "Compact timeout rejection lost its bounded actionable evidence or exposed input content.");
        }

        await using (var reopened = new AgentOpsEngine(state,null,false,null,false,null,maintenanceMode:new MaintenanceModeStore(Path.Combine(fixture.Path,"private-machine-mode"),protect:false)))
        {
            var retained = await CallAsync(reopened, new { op = "job_stat", jid, detail = true, output = "structured" });
            var saved = retained["structuredContent"]!["data"]!["rec"]!.Deserialize<OperationReceipt>(Json)!;
            Check(saved.Jid == jid && saved.Failure == failure, "SQLite reopening lost the original timeout evidence.");
            var log = await CallAsync(reopened, new { op = "job_log", jid, output = "structured" });
            var logData = log["structuredContent"]!["data"]!.AsObject();
            Check(!logData.ContainsKey("out") && !logData.ContainsKey("err_tail") && !logData.ContainsKey("exit"),
                "A retained admission rejection invented a process log or exit.");
        }
        Console.WriteLine("EVIDENCE timeout/argument admission unit checks and real isolated SQLite/MCP projection integration; fixture probe stubbed; no process execution or installed policy change.");
    }

    private static void AssertTimeout(string code, OperationFailureEvidence? failure, int actual)
    {
        Check(code == "precondition" && failure is not null &&
            failure.Field == new OperationFailureField("tmo") &&
            failure.Bound == new OperationFailureBound(actual, 600, "seconds") &&
            failure.Execution == new OperationFailureFact(OperationExecutionState.NotDispatched, "registered-tool-admission") &&
            failure.Retry == new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "registered-tool-admission") &&
            failure.Next!.Contains("1 through 600 seconds", StringComparison.Ordinal) &&
            failure.DetailAvailability == "none" && failure.LogAvailability == "not_retained",
            "Timeout admission lost the field, bound, safe retry guidance, or truthful execution state.");
    }

    private static void AssertArgument(ToolRecipeOperations operations, string[] arguments, int? index,
        long actual, long maximum, string unit)
    {
        var exception = Capture(() => operations.PrepareTool("timeout-fixture", arguments));
        Check(exception.Code == "invalid" && exception.Failure?.Field == new OperationFailureField("arg", index) &&
            exception.Failure.Bound == new OperationFailureBound(actual, maximum, unit) &&
            exception.Failure.Execution.Value == OperationExecutionState.NotDispatched,
            "Argument-bound rejection lost its field/index or measured limit.");
        foreach (var argument in arguments) AssertPrivate(exception.Failure!, argument);
    }

    private static void AssertPrivate(OperationFailureEvidence failure, params string[] sensitiveValues)
    {
        var text = JsonSerializer.Serialize(failure, Json);
        foreach (var value in sensitiveValues)
            Check(!text.Contains(value, StringComparison.Ordinal) &&
                !text.Contains(JsonSerializer.Serialize(value, Json)[1..^1], StringComparison.Ordinal),
                "Admission evidence echoed an input value or private path.");
    }

    private static AgentOpsException Capture(Action prepare)
    {
        try { prepare(); }
        catch (AgentOpsException exception) { return exception; }
        throw new InvalidOperationException("Expected admission rejection.");
    }

    private static ValueTask<JsonObject> CallAsync(AgentOpsEngine engine, object arguments) =>
        PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(arguments), new EngineDispatcher(engine), CancellationToken.None);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeFixtureRunner : IToolInvocationRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new ProcessResult(0, "fixture", string.Empty, false, false, false));
        }
    }
}
