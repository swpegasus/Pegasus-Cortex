using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class GeneralOperationalTests
{
    public static async Task ExplicitNamespaceToolRoundTripAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-general-mcp-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.GetFullPath(Path.Combine(root, "data", "cortex-v2.db"));
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                database,
                TimeProvider.System,
                CancellationToken.None);
            var tools = composition.Application.ListTools()
                .OfType<JsonObject>()
                .Select(item => item["name"]?.GetValue<string>())
                .Where(value => value is not null)
                .ToArray();
            var expected = new[]
            {
                "cortex_status", "cortex_namespaces", "cortex_start", "cortex_search",
                "cortex_read", "cortex_task", "cortex_knowledge", "cortex_finish"
            };
            LocalAssert.True(
                tools.SequenceEqual(expected, StringComparer.Ordinal),
                "The general composition did not expose exactly the ordered V2 tool surface.");

            var task = await CallAsync(
                composition,
                "cortex_task",
                """
                {"namespace":"alpha","action":"create","taskId":"task-1","title":"Task one","content":"line 1\r\nline 2\tΩ","idempotencyKey":"task-create-1"}
                """);
            RequireSuccessAndNamespace(task, "alpha");
            var taskRevision = task["structuredContent"]?["task"]?["revision"]?.GetValue<long>() ?? 0;
            LocalAssert.True(taskRevision == 1, "V2 task create did not return revision one.");

            var missingNamespace = await CallAsync(
                composition,
                "cortex_task",
                """{"action":"get","taskId":"task-1"}""");
            LocalAssert.True(
                missingNamespace["isError"]?.GetValue<bool>() == true,
                "A stateful task read succeeded without an explicit namespace.");

            var unknownArgument = await CallAsync(
                composition,
                "cortex_task",
                """{"namespace":"alpha","action":"get","taskId":"task-1","profile":"forbidden"}""");
            LocalAssert.True(
                unknownArgument["isError"]?.GetValue<bool>() == true,
                "An operational tool accepted a profile or unknown argument outside its schema.");

            var betaTask = await CallAsync(
                composition,
                "cortex_task",
                """
                {"namespace":"beta","action":"create","taskId":"task-1","title":"Other task","content":"isolated","idempotencyKey":"task-create-beta"}
                """);
            RequireSuccessAndNamespace(betaTask, "beta");

            const string exactContent = "first\r\nsecond\n\t漢字\u0000end";
            var knowledge = await CallAsync(
                composition,
                "cortex_knowledge",
                """
                {"namespace":"alpha","action":"create","recordId":"knowledge-1","idempotencyKey":"knowledge-create-1","kind":"correction","tier":"explicit","title":"Unicode","content":"first\r\nsecond\n\t漢字\u0000end","tags":["correction"],"provenance":{"source":"user","sourceId":"test","evidence":"direct"}}
                """);
            RequireSuccessAndNamespace(knowledge, "alpha");

            var search = await CallAsync(
                composition,
                "cortex_search",
                """{"namespaces":["alpha"],"text":"Unicode"}""");
            RequireSuccessAndNamespace(search, "alpha");
            var items = search["structuredContent"]?["items"] as JsonArray;
            LocalAssert.True(
                search["structuredContent"]?["schema"]?.GetValue<string>() == "cortex.search.compact/1" &&
                items is { Count: 1 } &&
                items[0]?["id"]?.GetValue<string>() == "knowledge-1" &&
                items[0]?["ns"]?.GetValue<string>() == "alpha" &&
                items[0]?["type"]?.GetValue<string>() == "knowledge" &&
                items[0]?["rev"]?.GetValue<long>() == 1,
                "New local knowledge was not immediately searchable through MCP.");

            var read = await CallAsync(
                composition,
                "cortex_read",
                """{"namespace":"alpha","recordType":"knowledge","recordId":"knowledge-1"}""");
            RequireSuccessAndNamespace(read, "alpha");
            LocalAssert.True(
                read["structuredContent"]?["record"]?["content"]?.GetValue<string>() == exactContent,
                "The SQLite V2 MCP route did not preserve exact JSON text.");

            var omittedType = await CallAsync(
                composition,
                "cortex_read",
                """{"namespace":"alpha","recordId":"knowledge-1"}""");
            LocalAssert.True(
                omittedType["isError"]?.GetValue<bool>() == true,
                "An exact read silently defaulted a missing record type.");

            var isolatedSearch = await CallAsync(
                composition,
                "cortex_search",
                """{"namespaces":["beta"],"text":"Unicode"}""");
            RequireSuccessAndNamespace(isolatedSearch, "beta");
            LocalAssert.True(
                isolatedSearch["structuredContent"]?["items"] is JsonArray { Count: 0 },
                "MCP knowledge search retained sticky namespace state.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask<JsonObject> CallAsync(
        CortexMcpLocalComposition composition,
        string name,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return await composition.Application.CallToolAsync(
            name,
            document.RootElement.Clone(),
            CancellationToken.None);
    }

    private static void RequireSuccessAndNamespace(JsonObject result, string expected)
    {
        LocalAssert.True(
            result["isError"]?.GetValue<bool>() == false,
            $"The MCP operation failed: {result.ToJsonString()}.");
        var body = result["structuredContent"]?.AsObject();
        var schema = body?["schema"]?.GetValue<string>();
        var namespaces = body?[schema is "cortex.start.compact/1" or "cortex.search.compact/1"
            ? "ns" : "effectiveNamespaces"] as JsonArray;
        LocalAssert.True(
            namespaces is { Count: 1 } && namespaces[0]?.GetValue<string>() == expected,
            "The MCP result did not echo the effective namespace.");
    }
}
