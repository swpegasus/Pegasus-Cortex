using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class RegistryFrontendTests
{
    internal static async Task RunAsync()
    {
        await using var dispatcher = new CaptureDispatcher();
        foreach (var action in RegistryOperationContract.Actions)
        {
            var body = Body(action);
            var args = new List<string> { "--json", "registry", action, "--stdin" };
            if (!RegistryOperationContract.IsReadOnly(action)) args.Add("rid=registry-frontend-" + action);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await PaoProgram.RunCliAsync(args.ToArray(), dispatcher, output, error,
                CancellationToken.None, new StringReader(body));
            Check(code == 0 && dispatcher.Last is { Op: "registry", Cwd: null, Ws: null, Root: null } &&
                dispatcher.Last.Arg["op"] == action && dispatcher.Last.Body == body,
                "CLI registry shape/body/default context: " + action + "; " + error);
            using var humanOutput = new StringWriter();
            code = await PaoProgram.RunCliAsync(args.Where(value => value != "--json").ToArray(), dispatcher,
                humanOutput, error, CancellationToken.None, new StringReader(body));
            var field = humanOutput.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Single(value => value.StartsWith("registry=", StringComparison.Ordinal));
            using var humanResult = JsonDocument.Parse(Uri.UnescapeDataString(field["registry=".Length..].Trim()));
            Check(code == 0 && humanResult.RootElement.GetProperty("marker").GetString() == "雪😀 ",
                "Plain CLI must retain the exact registry result/generation rather than dropping it.");

            var request = new JsonObject { ["op"] = RegistryOperationContract.PublicName(action),
                ["text"] = body, ["output"] = "agent" };
            if (!RegistryOperationContract.IsReadOnly(action)) request["rid"] = "registry-mcp-" + action;
            var calls = dispatcher.Calls;
            var response = await Call(request.ToJsonString(), dispatcher);
            Check(dispatcher.Calls == calls + 1 && dispatcher.Last!.Body == body &&
                dispatcher.Last.Cwd is null && dispatcher.Last.Ws is null,
                "MCP registry exact payload/defaults: " + action);
            using var projected = JsonDocument.Parse(response["content"]![0]!["text"]!.GetValue<string>());
            Check(projected.RootElement.GetProperty("registry").GetProperty("marker").GetString() == "雪😀 " &&
                !projected.RootElement.TryGetProperty("requestIdentity", out _) &&
                !projected.RootElement.TryGetProperty("resource_identity", out _) &&
                !projected.RootElement.TryGetProperty("log_available", out _) &&
                response["structuredContent"] is null, "Agent registry output should contain results without routine audit metadata.");
        }

        foreach (var extra in new[] { "root=AB12", "ws=elsewhere", "cwd=C:\\elsewhere", "wait=false", "arg0=anything", "quiet=true" })
        {
            var calls = dispatcher.Calls;
            var code = await PaoProgram.RunCliAsync(["registry", "read", "--stdin", extra], dispatcher,
                new StringWriter(), new StringWriter(), CancellationToken.None, new StringReader(Body("read")));
            Check(code == 2 && dispatcher.Calls == calls, "CLI must reject registry context/completion fields before dispatch.");
        }
        foreach (var action in RegistryOperationContract.Actions.Where(action => !RegistryOperationContract.IsReadOnly(action)))
        {
            var calls = dispatcher.Calls;
            var code = await PaoProgram.RunCliAsync(["registry", action, "--stdin"], dispatcher,
                new StringWriter(), new StringWriter(), CancellationToken.None, new StringReader(Body(action)));
            Check(code == 2 && dispatcher.Calls == calls, "Mutation RID is mandatory before CLI dispatch.");
        }

        var encodedBody = JsonSerializer.Serialize(Body("read"));
        var valid = "{\"op\":\"registry_read\",\"text\":" + encodedBody;
        foreach (var invalid in new[]
        {
            valid + ",\"op\":\"registry_inspect\"}",
            valid + ",\"text\":" + encodedBody + "}",
            valid + ",\"rid\":\"one\",\"rid\":\"two\"}",
            valid + ",\"output\":\"agent\",\"output\":\"structured\"}",
            valid + ",\"root\":\"AB12\"}", valid + ",\"ws\":\"elsewhere\"}",
            valid + ",\"wait\":false}", valid + ",\"detail\":\"true\"}",
            "{\"op\":\"registry_read\",\"text\":{}}",
            "{\"op\":\"registry_read\",\"text\":\"{}\"}",
            "{\"op\":\"registry_set\",\"text\":" + JsonSerializer.Serialize(Body("set")) + "}"
        })
        {
            var calls = dispatcher.Calls;
            var refused = await Call(invalid, dispatcher);
            Check(dispatcher.Calls == calls && refused["isError"]!.GetValue<bool>(),
                "Invalid/duplicate MCP registry fields must be rejected before dictionary projection.");
        }

        var malformed = Body("read").Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        foreach (var body in new[] { malformed, new string(' ', RegistryOperationContract.MaxRequestBytes + 1) })
        {
            var calls = dispatcher.Calls;
            var code = await PaoProgram.RunCliAsync(["registry", "read", "--stdin"], dispatcher,
                new StringWriter(), new StringWriter(), CancellationToken.None, new StringReader(body));
            Check(code == 2 && dispatcher.Calls == calls, "Malformed or oversized body must not reach the owner host.");
        }
        var tool = PaoProgram.UnifiedMcpToolDefinition();
        var operations = tool["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Check(RegistryOperationContract.Actions.All(action => operations.Contains(RegistryOperationContract.PublicName(action))),
            "All seven operations must be discoverable from the public unified tool.");
        using var help = new StringWriter();
        var helpCode = await PaoProgram.RunCliAsync(["help", "registry_set"], dispatcher, help,
            new StringWriter(), CancellationToken.None);
        Check(helpCode == 0 && help.ToString().Contains("preimage", StringComparison.Ordinal) &&
            help.ToString().Contains("rid", StringComparison.Ordinal), "Registry help must disclose mutation guards and identity.");
        Console.WriteLine("PASS registry CLI/MCP discovery, strict fields, exact Unicode bodies and minimal result output (captured dispatcher)");
    }

    private static async ValueTask<JsonObject> Call(string json, CaptureDispatcher dispatcher)
    {
        using var document = JsonDocument.Parse(json);
        return await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, dispatcher, CancellationToken.None,
            defaultWs: "must-not-be-used", defaultCwd: @"C:\must-not-be-used");
    }

    private static string Body(string action)
    {
        var digest = new string('0', 64);
        var result = new Dictionary<string,object?>
        {
            ["version"] = 1,
            ["key"] = new { hive = "current_user", view = 64, keyPath = "Software\\Registry 雪😀 " }
        };
        if (action is "read" or "set" or "delete_value") result["valueName"] = "";
        if (action == "browse") { result["kind"] = "values"; result["limit"] = 1; }
        if (action == "create") result["parentGeneration"] = digest;
        if (action == "delete_empty_key") result["keyGeneration"] = digest;
        if (action is "set" or "delete_value") result["preimage"] = new { keyGeneration = digest, valueDigest = digest };
        if (action == "set") result["value"] = new { type = "binary", base64 = "AQ==" };
        return JsonSerializer.Serialize(result);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class CaptureDispatcher : IOperationDispatcher
    {
        internal OperationRequest? Last;
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Last = request; Calls++;
            return Task.FromResult(new OperationReceipt(true, "registry-frontend-jid", request.Rid ?? "registry-frontend-read",
                "done", OperationNames.Public(request), Data: new Dictionary<string,object?>
                {
                    ["registry"] = new { marker = "雪😀 " }, ["requestIdentity"] = new string('0',64),
                    ["resource_identity"] = new string('1',64), ["registry_status"] = "ok",
                    ["registry_code"] = "ok", ["log_available"] = false
                }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
