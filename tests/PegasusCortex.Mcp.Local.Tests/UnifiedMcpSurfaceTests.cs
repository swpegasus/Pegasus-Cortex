using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Protocol;

internal static class UnifiedMcpSurfaceTests
{
    public static async Task CortexAndPaoShareOneClosedSurfaceAsync()
    {
        var cortex = new FakeCortexApplication();
        await using var execution = new FakeExecutionDispatcher();
        var unified = new CortexUnifiedMcpApplication(cortex, execution);
        var names = unified.ListTools()
            .OfType<JsonObject>()
            .Select(item => item["name"]?.GetValue<string>())
            .ToArray();
        LocalAssert.True(
            names.SequenceEqual(["cortex_status", "pao"], StringComparer.Ordinal),
            "The unified host did not advertise Cortex followed by one PAO tool.");

        using var validDocument = JsonDocument.Parse("""{"op":"diag","detail":true}""");
        var valid = await unified.CallToolAsync(
            "pao",
            validDocument.RootElement.Clone(),
            CancellationToken.None);
        LocalAssert.True(
            valid["isError"]?.GetValue<bool>() == false &&
            execution.LastRequest is { Op: "diag", Detail: true },
            "A valid PAO request did not preserve the fixed typed operation.");

        using var cliOutput = new StringWriter();
        using var cliError = new StringWriter();
        var cliExit = await PaoProgram.RunCliAsync(
            ["--json", "nsis", "status", "rid=shared-nsis-route"],
            execution,
            cliOutput,
            cliError,
            CancellationToken.None);
        LocalAssert.True(
            cliExit == 0 && string.IsNullOrWhiteSpace(cliError.ToString()),
            "The standalone PAO JSON adapter rejected the fixed NSIS route.");

        using var nsisDocument = JsonDocument.Parse(
            """{"op":"nsis_status","rid":"shared-nsis-route"}""");
        var nsisMcp = await unified.CallToolAsync(
            "pao",
            nsisDocument.RootElement.Clone(),
            CancellationToken.None);
        var cliReceipt = JsonNode.Parse(cliOutput.ToString());
        LocalAssert.True(
            JsonNode.DeepEquals(cliReceipt, nsisMcp["structuredContent"]),
            "Standalone PAO JSON and MCP structuredContent did not serialize the same receipt.");
        LocalAssert.True(
            execution.Requests.TakeLast(2).All(request =>
                request.Op == "nsis" &&
                request.Arg.GetValueOrDefault("op") == "status"),
            "Standalone PAO and MCP did not resolve NSIS through the same engine operation.");

        using var invalidDocument = JsonDocument.Parse("""{"op":"diag","command":"whoami"}""");
        var invalid = await unified.CallToolAsync(
            "pao",
            invalidDocument.RootElement.Clone(),
            CancellationToken.None);
        LocalAssert.True(
            invalid["isError"]?.GetValue<bool>() == true && execution.DispatchCount == 3,
            "The unified PAO adapter dispatched an unknown or generic command field.");

        using var cortexDocument = JsonDocument.Parse("{}");
        _ = await unified.CallToolAsync(
            "cortex_status",
            cortexDocument.RootElement.Clone(),
            CancellationToken.None);
        LocalAssert.True(
            cortex.Calls == 1,
            "The unified PAO seam interfered with a Cortex operation.");
    }

    private sealed class FakeExecutionDispatcher : IOperationDispatcher
    {
        public OperationRequest? LastRequest { get; private set; }
        public int DispatchCount { get; private set; }
        public List<OperationRequest> Requests { get; } = [];

        public Task<OperationReceipt> DispatchAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Requests.Add(request);
            DispatchCount++;
            return Task.FromResult(new OperationReceipt(
                true,
                "fixture-job",
                request.Rid ?? "fixture-request",
                "done",
                request.Op,
                Data: new Dictionary<string, object?> { ["service"] = "fixture" }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCortexApplication : ICortexMcpProtocolApplication
    {
        public int Calls { get; private set; }

        public JsonArray ListTools() =>
            [new JsonObject { ["name"] = "cortex_status" }];

        public ValueTask<JsonObject> CallToolAsync(
            string name,
            JsonElement? arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new JsonObject
            {
                ["content"] = new JsonArray(),
                ["isError"] = false
            });
        }

        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonArray());

        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonArray());

        public ValueTask<JsonObject?> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<JsonObject?>(null);
    }
}
