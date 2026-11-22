using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class McpOutputProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var path = Path.Combine(fixture.Path, "numbered context.txt");
        await File.WriteAllTextAsync(path, "outside-first\r\n    alpha Ω\r\n\r\n\tvalue = \"雪🦄\";  \r\noutside-last\r\n", new UTF8Encoding(false));
        var data = ExtendedOperations.ReadLines(path, 2, 3);
        var receipt = new OperationReceipt(true, "fixture-jid", "fixture-rid", "done", "read_lines", Data: data);
        await using var dispatcher = new RecordingDispatcher(request =>
        {
            Check(!request.Arg.ContainsKey("output"), "Presentation entered the engine request.");
            return receipt;
        });

        var arguments = new { op = "read_lines", src = path, line = 2, count = 3 };
        var legacy = await EmbeddedAsync(dispatcher, arguments);
        var structured = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, line = 2, count = 3, output = "structured" });
        Check(legacy.ToJsonString(Json) == structured.ToJsonString(Json), "Explicit structured output changed the legacy wire receipt.");
        var receiptJson = JsonSerializer.Serialize(receipt, Json);
        Check(Text(legacy) == receiptJson && legacy["structuredContent"]!.ToJsonString(Json) == receiptJson,
            "Default MCP no longer carries the complete engine receipt twice for legacy consumers.");
        Check(legacy["structuredContent"]!["data"]!["entries"]![0]!["truncated"]!.GetValue<bool>() == false &&
            legacy["structuredContent"]!["err"] is null && legacy["structuredContent"]!.AsObject().ContainsKey("err"),
            "Legacy null/false fields were removed.");

        var agent = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, line = 2, count = 3, output = "agent" });
        var expected = "next=5 total=5" +
            "\n2:     alpha Ω\n3: \n4: \tvalue = \"雪🦄\";  ";
        Check(Text(agent) == expected, "The agent excerpt changed indentation, empty lines, Unicode, or selected range.");
        Check(!agent.ContainsKey("structuredContent") && agent["content"]!.AsArray().Count == 1 &&
            agent["isError"]!.GetValue<bool>() == false, "Agent output duplicated the receipt or misreported success.");
        foreach (var omitted in new[] { "outside-first", "outside-last", "sha=", "bom=", "enc=", "newline=", "truncated", "rid=", "receipt=", "src=", "line=", "count=", "entries", "has_more" })
            Check(!Text(agent).Contains(omitted, StringComparison.Ordinal), "The normal excerpt retained unnecessary metadata or unselected content.");

        using var cliOutput = new StringWriter();
        using var cliError = new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json", "read-lines", path, "line=2", "count=3"], dispatcher,
            cliOutput, cliError, CancellationToken.None) == 0 && cliOutput.ToString().TrimEnd() == receiptJson,
            "CLI --json no longer preserves the full structured receipt.");
        cliOutput.GetStringBuilder().Clear();
        Check(await PaoProgram.RunCliAsync(["read-lines", path, "line=2", "count=3"], dispatcher,
            cliOutput, cliError, CancellationToken.None) == 0 && cliOutput.ToString().Contains("entries=%5B", StringComparison.Ordinal),
            "Existing CLI compact output changed.");

        var standaloneAgent = await StandaloneAsync(dispatcher, new { op = "read_lines", src = path, line = 2, count = 3, output = "agent" });
        var standaloneLegacy = await StandaloneAsync(dispatcher, arguments);
        var standaloneStructured = await StandaloneAsync(dispatcher, new { op = "read_lines", src = path, line = 2, count = 3, output = "structured" });
        Check(JsonNode.DeepEquals(agent, standaloneAgent["result"]) && JsonNode.DeepEquals(legacy, standaloneLegacy["result"]) &&
            JsonNode.DeepEquals(legacy, standaloneStructured["result"]), "Embedded and standalone MCP presentation diverged.");

        var legacyBytes = Encoding.UTF8.GetByteCount(standaloneLegacy.ToJsonString(Json));
        var agentBytes = Encoding.UTF8.GetByteCount(standaloneAgent.ToJsonString(Json));
        Check(agentBytes < legacyBytes, "The compact response did not reduce the bounded fixture payload.");
        Console.WriteLine($"EVIDENCE read_lines same non-first-line fixture serialized MCP response UTF8 bytes legacy={legacyBytes} agent={agentBytes}; bytes only, not model tokens");

        receipt = receipt with { Data = ExtendedOperations.ReadLines(path, 5, 1) };
        var final = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, line = 5, count = 1, output = "agent" });
        Check(Text(final) == "5: outside-last",
            "EOF advertised continuation or lost the last source line.");

        await File.WriteAllTextAsync(path, "outside-first\n" + string.Concat(Enumerable.Repeat("🦄", 70_000)) + "\nlast\n", new UTF8Encoding(false));
        receipt = receipt with { Data = ExtendedOperations.ReadLines(path, 2, 2) };
        var bounded = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, line = 2, count = 2, output = "agent" });
        var boundedStructured = JsonSerializer.SerializeToNode(receipt, Json)!;
        var boundedSource = boundedStructured["data"]!["entries"]![0]!["text"]!.GetValue<string>();
        Check(Text(bounded).Contains("truncated=true truncated_lines=[2]", StringComparison.Ordinal) &&
            Text(bounded).Contains("next=3", StringComparison.Ordinal) && Text(bounded).EndsWith("\n2: " + boundedSource, StringComparison.Ordinal) &&
            !Text(bounded).Contains('\uFFFD') && Encoding.UTF8.GetByteCount(Text(bounded)) <= 256 * 1024 + 2048,
            "Bounded Unicode excerpt hid clipping, split a scalar, or inflated its source budget.");

        await ProjectionAndValidationAsync(path);
        await ReadableJsonAsync();
    }

    private static async Task ReadableJsonAsync()
    {
        const string exact = "Owner's <tag> & café Ω 雪 🚀\r\n\t\"quoted\"\u0002" +
            " C:\\Pegasus\\Cortex /srv/cortex literal \\u0027";
        var receipt = new OperationReceipt(true, "utf8-jid", "utf8-rid", "done", "diag",
            Data: new Dictionary<string, object?> { ["text"] = exact });
        await using var dispatcher = new RecordingDispatcher(_ => receipt);
        foreach (var mode in new[] { "structured", "agent" })
        {
            var result = await EmbeddedAsync(dispatcher, new { op = "diag", output = mode });
            var text = Text(result);
            Check(text.Contains("Owner's <tag> & café Ω 雪", StringComparison.Ordinal),
                "Embedded JSON still uses HTML/ASCII escapes for readable text.");
            var data = mode == "agent" ? SuccessData(result) : JsonNode.Parse(text)!["data"]!;
            Check(data["text"]!.GetValue<string>() == exact, "JSON presentation changed exact content.");
            var wire = await StandaloneWireAsync(dispatcher, new { op = "diag", output = mode });
            Check(wire.Contains("Owner's <tag> & café Ω 雪", StringComparison.Ordinal) &&
                !wire.Contains('\uFFFD') && !wire.Contains('\u0002') && !wire.Contains('\r') && !wire.Contains('\n'),
                "Standalone MCP output escaped normal text or emitted raw frame controls.");
            var parsed = JsonNode.Parse(wire)!["result"]!.AsObject();
            Check(Text(parsed) == text, "Outer JSON-RPC serialization changed the inner response.");
            var oldBytes = Encoding.UTF8.GetByteCount(JsonNode.Parse(wire)!.ToJsonString(new(JsonSerializerDefaults.Web)));
            Check(Encoding.UTF8.GetByteCount(wire) < oldBytes,
                "Readable JSON did not reduce the escape-heavy response bytes.");
            Console.WriteLine($"METRIC pao-readable-json mode={mode} bytes={Encoding.UTF8.GetByteCount(wire)} previous={oldBytes}");
        }

        var rejected = await EmbeddedAsync(dispatcher, new { op = "search", root = "unused", pattern = "x" });
        Check(Text(rejected).Contains("'root'", StringComparison.Ordinal) &&
            !Text(rejected).Contains("\\u0027", StringComparison.Ordinal),
            "Pre-dispatch field error still escapes apostrophes.");
        using var cliOutput = new StringWriter();
        using var cliError = new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json", "diag"], dispatcher, cliOutput, cliError,
            CancellationToken.None) == 0, "CLI JSON fixture failed.");
        Check(cliOutput.ToString().Contains("Owner's <tag> & café Ω 雪", StringComparison.Ordinal) &&
            JsonNode.Parse(cliOutput.ToString())!["data"]!["text"]!.GetValue<string>() == exact,
            "CLI JSON is not readable or lost exact content.");
    }

    private static async Task ProjectionAndValidationAsync(string path)
    {
        var receipt = new OperationReceipt(true, "fixture-jid", "fixture-rid", "done", "diag", Data: new Dictionary<string, object?>
        {
            ["out"] = "", ["err_tail"] = "", ["notes"] = null,
            ["matches"] = Array.Empty<object>(), ["text"] = "", ["count"] = 0, ["equal"] = false,
            ["submission"] = new { schema = "pegasus.cortex.job-submission/1", command = "pao diag" },
            ["nested"] = new Dictionary<string, object?> { ["optional"] = null, ["text"] = "", ["results"] = Array.Empty<object>() }
        });
        await using var dispatcher = new RecordingDispatcher(request =>
        {
            Check(!request.Arg.ContainsKey("output"), "Generic presentation entered the engine request.");
            return receipt;
        });
        var projected = await EmbeddedAsync(dispatcher, new { op = "diag", output = "agent" });
        var data = SuccessData(projected);
        Check(!Text(projected).Contains("fixture-jid", StringComparison.Ordinal) &&
            !projected.ContainsKey("structuredContent") && !data.ContainsKey("out") && !data.ContainsKey("err_tail") && !data.ContainsKey("notes") && !data.ContainsKey("seq") && !data.ContainsKey("submission"),
            "Compact optional metadata was not omitted.");
        Check(data["matches"] is JsonArray { Count: 0 } && data["text"]!.GetValue<string>() == "" &&
            data["count"]!.GetValue<int>() == 0 && data["equal"]!.GetValue<bool>() == false &&
            data["nested"]!["results"] is JsonArray { Count: 0 } && !data["nested"]!.AsObject().ContainsKey("optional"),
            "Projection dropped meaningful empty results, empty content, false, or zero.");

        var detailed = await EmbeddedAsync(dispatcher, new { op = "diag", output = "structured" });
        Check(detailed["structuredContent"]!["data"]!["submission"] is not null,
            "Explicit structured inspection lost retained submission details.");
        var original = receipt;
        receipt = new OperationReceipt(true, "inspect-jid", "inspect-rid", "done", "job_stat",
            Data: new Dictionary<string, object?> { ["rec"] = original });
        var inspected = await EmbeddedAsync(dispatcher, new { op = "job_stat", jid = new string('a', 32), output = "agent" });
        Check(SuccessData(inspected)["rec"]!["data"]!["submission"] is not null,
            "Selected job inspection lost retained submission details.");

        receipt = new OperationReceipt(false, "failure-jid", "failure-rid", "failed", "read_lines", "invalid",
            Data: new Dictionary<string, object?> { ["count"] = 0, ["timed_out"] = false }, Partial: false,
            Failure: new OperationFailureEvidence(
                "An argument exceeds the registered per-argument UTF-8 limit.",
                new OperationFailureFact(OperationExecutionState.NotDispatched, "argument-vector-validation"),
                new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "argument-vector-validation"),
                new OperationFailureField("arg", 5),
                new OperationFailureBound(65_538, 16_384, "utf8_bytes"),
                Diagnostics:
                [
                    new("first", "fixture.cs", 4, 2, "E1"),
                    new("first", "fixture.cs", 4, 2, "E1"),
                    new("second"), new("third"), new("fourth"), new("fifth")
                ],
                Next: "Write large content to a file and pass its admitted path.",
                DetailAvailability: "none",
                LogAvailability: "not_retained"));
        var failure = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, output = "agent" });
        var failureText = Text(failure);
        Check(failure["isError"]!.GetValue<bool>() && !failure.ContainsKey("structuredContent") &&
            failureText.StartsWith("FAIL failure-jid invalid\n", StringComparison.Ordinal) &&
            failureText.Contains("field=arg[5] actual=65538 max=16384 unit=utf8_bytes", StringComparison.Ordinal) &&
            failureText.Contains("execution=not_dispatched source=argument-vector-validation retry=after_correction", StringComparison.Ordinal) &&
            !failureText.Contains("failure-rid", StringComparison.Ordinal) &&
            failureText.Split('\n').Count(line => line.StartsWith("diag[", StringComparison.Ordinal)) == 4 &&
            failureText.Contains("diagnostics_omitted=true", StringComparison.Ordinal) &&
            failureText.EndsWith("detail=none log=not_retained", StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(failureText) <= 4096,
            "Agent error lost recovery, identity, or failure evidence.");
        var fullFailure = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, output = "structured" });
        var fullFailureBody = JsonNode.Parse(Text(fullFailure))!.AsObject();
        Check(fullFailure.ContainsKey("structuredContent") && fullFailureBody["rid"]!.GetValue<string>() == "failure-rid" &&
            fullFailureBody["data"]!["count"]!.GetValue<int>() == 0 &&
            fullFailureBody["failure"]!["field"]!["index"]!.GetValue<int>() == 5 &&
            fullFailureBody["failure"]!["diagnostics"]!.AsArray().Count == 6,
            "Structured output did not preserve the complete receipt and underlying failure evidence.");

        receipt = receipt with
        {
            Partial = true,
            Failure = receipt.Failure! with
            {
                Execution = new OperationFailureFact(OperationExecutionState.Partial, "operation-engine"),
                Retry = new OperationFailureFact(OperationRetryDisposition.ReconcileFirst, "operation-engine")
            }
        };
        var reconcile = await EmbeddedAsync(dispatcher, new { op = "read_lines", src = path, output = "agent" });
        Check(Text(reconcile).Contains("retry=reconcile_first", StringComparison.Ordinal) &&
            Text(reconcile).Split('\n').Count(line => line == "rid=failure-rid") == 1,
            "A reconciliation-required failure omitted or duplicated its RID.");

        var calls = dispatcher.Count;
        foreach (var invalid in new[]
                 {
                     "{\"op\":\"diag\",\"output\":\"text\"}", "{\"op\":\"diag\",\"output\":null}",
                     "{\"op\":\"diag\",\"output\":false}", "{\"op\":\"diag\",\"output\":\"\"}",
                     "{\"op\":\"diag\",\"output\":\"agent\",\"output\":\"structured\"}"
                 })
        {
            using var document = JsonDocument.Parse(invalid);
            var rejected = await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, dispatcher, CancellationToken.None);
            Check(rejected["isError"]!.GetValue<bool>() && Text(rejected).Contains("output", StringComparison.Ordinal),
                "Invalid projection selection was accepted or lacked a remedy.");
        }
        Check(dispatcher.Count == calls, "Invalid projection selection reached the engine.");
        var invalidAgent = await EmbeddedAsync(dispatcher, new { op = "diag", output = "agent", unexpected = true });
        Check(invalidAgent["isError"]!.GetValue<bool>() && !invalidAgent.ContainsKey("structuredContent") &&
            Text(invalidAgent).StartsWith("FAIL no-receipt pao_arguments_invalid\n", StringComparison.Ordinal) &&
            Text(invalidAgent).Contains("execution=not_dispatched source=mcp-admission", StringComparison.Ordinal),
            "Embedded invalid-argument agent output duplicated its error.");
        var invalidStandalone = await StandaloneAsync(dispatcher, new { op = "diag", output = "text" });
        Check(invalidStandalone["error"]!["code"]!.GetValue<int>() == -32602, "Standalone invalid-params contract changed.");

        var schema = PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["output"]!;
        Check(schema["default"]!.GetValue<string>() == "structured" && schema["enum"]!.AsArray().Count == 2,
            "Schema did not expose the compatible closed presentation selector.");
        var help = await EmbeddedAsync(dispatcher, new { op = "help", name = "read_lines", output = "agent" });
        var helpData = SuccessData(help);
        Check(helpData["allowed"]!.AsArray().Any(item => item?.GetValue<string>() == "output") &&
            helpData["mcp_example"]!.GetValue<string>().Contains("\"output\":\"agent\"", StringComparison.Ordinal) &&
            helpData["constraints"]!.GetValue<string>().Contains("legacy", StringComparison.Ordinal),
            "Help omitted projection admission, normal agent use, or legacy compatibility.");
        Check(dispatcher.Count == calls, "Local help unexpectedly dispatched a job.");

        var logHelp = await EmbeddedAsync(dispatcher, new { op = "help", name = "job_log", output = "agent" });
        var logHelpData = SuccessData(logHelp);
        receipt = new OperationReceipt(true, "job-log-jid", "job-log-rid", "done", "job_log");
        using (var example = JsonDocument.Parse(logHelpData["mcp_example"]!.GetValue<string>()))
        {
            var beforeExample = dispatcher.Count;
            var admitted = await PaoProgram.CallUnifiedMcpToolAsync(example.RootElement, dispatcher, CancellationToken.None);
            Check(!admitted["isError"]!.GetValue<bool>() && dispatcher.Count == beforeExample + 1,
                "The advertised job_log MCP example does not satisfy the live parser contract.");
        }
        using (var cliOutput = new StringWriter())
        using (var cliError = new StringWriter())
        {
            Check(await PaoProgram.RunCliAsync(["job", "log", "0123456789abcdef0123456789abcdef", "max=16384"],
                    dispatcher, cliOutput, cliError, CancellationToken.None) != 2,
                "The advertised job_log CLI shape failed local argument admission.");
        }

        receipt = new OperationReceipt(true, "nsis-jid", "nsis-rid", "done", "nsis_build", Data: new Dictionary<string, object?>
        {
            ["src"] = "setup.nsi", ["dst"] = "setup.exe", ["installer_bytes"] = 42L,
            ["installer_sha"] = new string('a', 64), ["log_available"] = true,
            ["out"] = new string('x', 32_000), ["err_tail"] = "compiler diagnostic"
        });
        var nsis = await EmbeddedAsync(dispatcher, new { op = "nsis_build", src = "setup.nsi", dst = "setup.exe", output = "agent" });
        var nsisBody = SuccessData(nsis);
        Check(nsisBody["jid"]!.GetValue<string>() == "nsis-jid" && nsisBody["log_available"]!.GetValue<bool>() &&
            !Text(nsis).Contains("compiler diagnostic", StringComparison.Ordinal) && !Text(nsis).Contains(new string('x', 100), StringComparison.Ordinal),
            "Compact NSIS output lost retained-log availability or leaked compiler logs.");

        var tool = new RegisteredToolDefinition("fixture", 1, path, MaxArguments: 8,
            MaxArgumentUtf8Bytes: 16_384, MaxTotalArgumentUtf8Bytes: 131_072);
        try
        {
            ToolRecipeValidation.ValidateArgumentVector(tool,
                ["one", "two", "three", "four", "five", new string('x', 16_385)]);
            throw new InvalidOperationException("Oversized argument was accepted.");
        }
        catch (AgentOpsException exception)
        {
            Check(exception.Code == "invalid" && exception.Failure?.Field == new OperationFailureField("arg", 5) &&
                exception.Failure.Bound == new OperationFailureBound(16_385, 16_384, "utf8_bytes") &&
                exception.Failure.Execution.Value == OperationExecutionState.NotDispatched &&
                exception.Failure.LogAvailability == "not_retained",
                "Argument admission did not preserve precise no-launch evidence.");
        }

        var processSecret = "process-secret";
        var processFailure = new ToolExecutionException("external_failed",
            new ToolExecutionResult(23, string.Empty,
                string.Join('\n',
                    $"C:\\private\\demo.cs(10,2): error CS1001: bad token={processSecret}",
                    $"C:\\private\\demo.cs(10,2): error CS1001: bad token={processSecret}",
                    "C:\\private\\two.cs(11): error CS1002: second",
                    "C:\\private\\three.cs(12): error CS1003: third",
                    "C:\\private\\four.cs(13): error CS1004: fourth",
                    "C:\\private\\five.cs(14): error CS1005: fifth"),
                false, false, ToolExecutionMode.User, "fixture", null), false);
        var produced = FailureEvidence.ToolExecution(processFailure);
        Check(produced.Execution.Value == OperationExecutionState.Started &&
            produced.Retry.Value == OperationRetryDisposition.AfterCorrection &&
            produced.Diagnostics is { Count: 4 } && produced.DiagnosticsOmitted &&
            produced.Diagnostics[0].Source == "demo.cs" && produced.Diagnostics[0].Line == 10 &&
            !produced.Diagnostics.Any(item => item.Message.Contains(processSecret, StringComparison.Ordinal)) &&
            produced.Diagnostics[0].Message.Contains("<redacted>", StringComparison.Ordinal),
            "Process failure evidence was unbounded, duplicated, unstamped, or exposed secret/path content.");

        var secret = "top-secret-value";
        receipt = new OperationReceipt(false, "privacy-jid", "privacy-rid", "failed", "run", "external_failed",
            Failure: new OperationFailureEvidence(
                "External process failed.",
                new OperationFailureFact(OperationExecutionState.Started, "registered-process-runner"),
                new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "registered-process-runner"),
                Diagnostics: [new($"token={secret}")],
                Next: "Correct the process failure.",
                DetailAvailability: "structured", LogAvailability: "available"));
        var privacy = await EmbeddedAsync(dispatcher, new { op = "run", cfg = "fixture", output = "agent" });
        Check(!Text(privacy).Contains(secret, StringComparison.Ordinal) &&
            Text(privacy).Contains("token=<redacted>", StringComparison.Ordinal),
            "Compact failure diagnostics exposed a secret-shaped value or omitted its redaction marker.");

        receipt = new OperationReceipt(true, "process-jid", "process-rid", "done", "dotnet_build",
            Data: new Dictionary<string, object?>
            {
                ["exit"] = 0, ["out"] = new string('o', 32_000), ["err_tail"] = string.Empty,
                ["out_truncated"] = false, ["err_truncated"] = false, ["tool"] = "dotnet", ["seq"] = 44L
            });
        var processSuccess = await EmbeddedAsync(dispatcher,
            new { op = "dotnet_build", src = "fixture.csproj", output = "agent" });
        var processData = SuccessData(processSuccess);
        Check(processData["jid"]!.GetValue<string>() == "process-jid" &&
            !processData.ContainsKey("out") && !processData.ContainsKey("err_tail") && !processData.ContainsKey("seq") &&
            processData["log_available"]!.GetValue<bool>() && processData["exit"]!.GetValue<int>() == 0,
            "Compact process success duplicated retained output or lost its log/exit disposition.");

        const string exactOutput = "{\"untrusted\":true}\n42 雪🦄\n";
        var processResult = new Dictionary<string, object?>
        {
            ["exit"] = 0, ["out"] = exactOutput, ["err_tail"] = string.Empty,
            ["out_truncated"] = false, ["err_truncated"] = false
        };
        receipt = new OperationReceipt(true, "value-jid", "value-rid", "done", "pwsh", Data: processResult);
        var valueResult = await EmbeddedAsync(dispatcher, new { op = "pwsh", src = "fixture.ps1", output = "agent" });
        var valueData = SuccessData(valueResult);
        Check(valueData["out"]!.GetValue<string>() == exactOutput && !valueData.ContainsKey("untrusted") &&
            !valueData.ContainsKey("log_available") && !Text(valueResult).Contains("value-jid", StringComparison.Ordinal),
            "A useful small process result was hidden, interpreted as trusted JSON, or wrapped in routine metadata.");

        var longOutput = string.Concat(Enumerable.Repeat("雪🦄", 900));
        processResult["out"] = longOutput;
        processResult["err_tail"] = "retained diagnostic";
        var longResult = await EmbeddedAsync(dispatcher, new { op = "pwsh", src = "fixture.ps1", output = "agent" });
        var longData = SuccessData(longResult);
        var excerpt = longData["out"]!.GetValue<string>();
        Check(Encoding.UTF8.GetByteCount(excerpt) <= 4096 && !excerpt.Contains('\uFFFD') && excerpt.EndsWith("...", StringComparison.Ordinal) &&
            longData["out_truncated"]!.GetValue<bool>() && longData["log_available"]!.GetValue<bool>() && longData["jid"]!.GetValue<string>() == "value-jid" &&
            !longData.ContainsKey("err_tail") && (string)processResult["out"]! == longOutput &&
            (bool)processResult["out_truncated"]! == false,
            "Process presentation exceeded its UTF-8 bound, hid truncation, or mutated the retained receipt.");
    }

    private static ValueTask<JsonObject> EmbeddedAsync(IOperationDispatcher dispatcher, object arguments) =>
        PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(arguments), dispatcher, CancellationToken.None);

    private static async Task<JsonObject> StandaloneAsync(IOperationDispatcher dispatcher, object arguments)
        => JsonNode.Parse(await StandaloneWireAsync(dispatcher, arguments))!.AsObject();

    private static async Task<string> StandaloneWireAsync(IOperationDispatcher dispatcher, object arguments)
    {
        using var reader = new McpLineReader();
        using var writer = new McpCaptureWriter();
        using var stderr = new StringWriter();
        var run = PaoProgram.RunMcpAsync(dispatcher, reader, writer, stderr, CancellationToken.None, defaultCwd: Environment.CurrentDirectory);
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2026-07-28", capabilities = new { }, clientInfo = new { name = "projection-fixture", version = "1" } } }));
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }));
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "pao", arguments } }));
        string response;
        try { response = await writer.Response.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { reader.Complete(); }
        Check(await run == 0 && string.IsNullOrWhiteSpace(stderr.ToString()), "Standalone fixture failed or polluted STDERR.");
        return response;
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();
    private static JsonObject SuccessData(JsonObject result)
    {
        return JsonNode.Parse(Text(result))!.AsObject();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
