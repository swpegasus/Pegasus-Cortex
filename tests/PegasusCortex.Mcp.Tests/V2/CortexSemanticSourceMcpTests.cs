using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.V2;

public static class CortexSemanticSourceMcpTests
{
    public static async Task RunAsync()
    {
        var store = new CapturedStore();
        var core = new CortexV2Application(store, "os_fixture");
        var shared = new CortexV2McpApplication(core);
        var first = new CortexInvocationSourceV2("approved-mcp-connection", "cortex.mcp/11111111111111111111111111111111",
            "22222222222222222222222222222222", 1, "First client");
        var second = first with { Subject = "33333333333333333333333333333333", Generation = 2, Label = "Second client" };
        var a = shared.WithInvocationSource(first);
        var b = shared.WithInvocationSource(second);
        var calls = await Task.WhenAll(Call(a, Command("a")), Call(b, Command("b")));
        Require(calls.All(result => result["isError"]!.GetValue<bool>() == false), "source facade rejected valid overlapping requests");
        Require(store.Calls == 2 && store.Sources["a"].VerifiedSource == first && store.Sources["b"].VerifiedSource == second,
            "per-invocation source was lost, shared globally or routed to another store");
        Require(calls[0]["structuredContent"]!["task"]!["verifiedSource"]!["subject"]!.GetValue<string>() == first.Subject,
            "verified source was not returned in the task receipt");
        Require(calls[0]["structuredContent"]!["task"]!["participantIdentityKind"]!.GetValue<string>() == "declared-label",
            "participant label was not explicitly declared");
        var local = await Call(shared, Command("local"));
        Require(local["isError"]!.GetValue<bool>() == false && store.Sources["local"].VerifiedSource is null,
            "scoped source leaked into the original shared application");

        var forged = JsonSerializer.SerializeToNode(Command("forged"), CortexV2Json.Options)!.AsObject();
        forged["verifiedSource"] = JsonSerializer.SerializeToNode(first, CortexV2Json.Options);
        var response = await a.CallToolAsync("cortex_task", JsonSerializer.SerializeToElement(forged), default);
        Require(response["isError"]!.GetValue<bool>() && store.Calls == 3, "wire verified-source forgery reached storage");
        var forgedProvenance = new JsonObject { ["namespace"] = "project-a", ["action"] = "create", ["recordId"] = "forged",
            ["idempotencyKey"] = "forged-write", ["kind"] = "observation", ["tier"] = "explicit", ["title"] = "Knowledge", ["content"] = "Safe",
            ["tags"] = new JsonArray(), ["provenance"] = new JsonObject { ["source"] = "agent", ["verifiedSource"] = JsonSerializer.SerializeToNode(first, CortexV2Json.Options) } };
        response = await a.CallToolAsync("cortex_knowledge", JsonSerializer.SerializeToElement(forgedProvenance), default);
        Require(response["isError"]!.GetValue<bool>() && store.Calls == 3, "wire provenance forgery reached storage");
        const string prohibited = "password=\"fixture-secret\"";
        response = await Call(a, Command("prohibited") with { Content = prohibited });
        Require(response["isError"]!.GetValue<bool>() && response.ToJsonString().Contains("cortex_content_prohibited", StringComparison.Ordinal) &&
            !response.ToJsonString().Contains("fixture-secret", StringComparison.Ordinal) && store.Calls == 3,
            "content rejection echoed input or dispatched through MCP");
        var tool = shared.ListTools().OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "cortex_task");
        var properties = tool["inputSchema"]!["properties"]!.AsObject();
        Require(!properties.ContainsKey("verifiedSource") && properties["claimedBy"]!["description"]!.GetValue<string>().Contains("Caller-declared", StringComparison.Ordinal),
            "public schema exposed trusted attribution or hid declared-label semantics");
    }

    private static CortexTaskCommandV2 Command(string id) => new("project-a", CortexTaskActionV2.Create, id, null, "write-" + id,
        "Task", "Factual note", "declared-person", "declared-agent", null, null, [], null, null, null);
    private static Task<JsonObject> Call(CortexV2McpApplication application, CortexTaskCommandV2 command) =>
        application.CallToolAsync("cortex_task", JsonSerializer.SerializeToElement(command, CortexV2Json.Options), default).AsTask();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class CapturedStore : ICortexV2Store
    {
        internal int Calls;
        internal readonly ConcurrentDictionary<string, CortexProvenanceV2> Sources = new();
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 command, CancellationToken token)
        {
            var provenance = command.EffectiveProvenance ?? throw new InvalidOperationException("missing stamped provenance");
            Sources[command.TaskId!] = provenance;
            if (Interlocked.Increment(ref Calls) >= 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            return new([command.Namespace], "accepted", new(command.Namespace, command.TaskId!, 1, CortexTaskStatusV2.Open,
                "Task", "Excerpt", command.Assignee, command.ClaimedBy, null, VerifiedSource: provenance.VerifiedSource), [], null, null);
        }
        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(CortexNamespacesRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(CortexKnowledgeCommandV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 request, CancellationToken token) => throw new NotSupportedException();
    }
}
