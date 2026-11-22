using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Protocol;

internal static class MethodInvocationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const string Initialize = "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}";
    private const string Initialized = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

    internal static async Task InitializePayloadAsync()
    {
        var app = new FixtureApplication();
        var native = new McpRequestProcessor(app, TextWriter.Null);
        var response = await native.ProcessAsync(Initialize, CancellationToken.None);
        var first = CortexMcpInvocationProcessor.CreateInitializeResult();
        Assert.True(JsonNode.DeepEquals(response!["result"], first), "The shared initialize metadata differs from native STDIO.");
        first["serverInfo"]!["name"] = "changed-only-in-this-result";
        Assert.True(JsonNode.DeepEquals(response["result"], CortexMcpInvocationProcessor.CreateInitializeResult()),
            "Initialize results share mutable nodes across callers.");
        var early = await native.ProcessAsync(Request("1", "ping"), CancellationToken.None);
        Assert.Equal("cortex_mcp_not_initialized", DiagnosticCode(early!));
        await native.ProcessAsync(Initialized, CancellationToken.None);
        Assert.True(native.IsReady && (await native.ProcessAsync(Request("2", "ping"), CancellationToken.None))!["result"] is not null,
            "Extracting initialize metadata changed the native lifecycle gate.");
        Assert.Equal(0, app.Calls);
    }

    internal static async Task ValidationAndParityAsync()
    {
        var nativeApp = new FixtureApplication();
        var externalApp = new FixtureApplication();
        var native = await ReadyAsync(nativeApp);
        var external = new CortexMcpInvocationProcessor(externalApp);
        (string Method, string? Parameters, int? Error, bool NoApplicationCall)[] cases =
        [
            ("ping", null, null, true), ("ping", "42", null, true),
            ("tools/list", null, null, false), ("tools/list", "{\"cursor\":null,\"_meta\":{}}", null, false),
            ("tools/list", "null", -32602, true), ("tools/list", "{\"cursor\":\"next\"}", -32602, true),
            ("tools/list", "{\"unexpected\":true}", -32602, true),
            ("tools/call", "{\"name\":\"echo\",\"arguments\":{\"text\":\"日本語 Ω\"},\"_meta\":{}}", null, false),
            ("tools/call", "{\"name\":\"echo\"}", null, false),
            ("tools/call", "{\"name\":\"echo\",\"arguments\":42}", null, false),
            ("tools/call", null, -32602, true), ("tools/call", "{\"name\":\"\"}", -32602, true),
            ("tools/call", "{\"name\":\"echo\",\"extra\":0}", -32602, true),
            ("tools/call", JsonSerializer.Serialize(new { name = new string('x', 129) }), -32602, true),
            ("resources/list", "{}", null, false), ("resources/list", "[]", -32602, true),
            ("resources/templates/list", null, null, false), ("resources/templates/list", "{\"cursor\":1}", -32602, true),
            ("resources/read", "{\"uri\":\"cortex://fixture/item\",\"_meta\":{}}", null, false),
            ("resources/read", "{\"uri\":\"cortex://fixture/missing\"}", -32002, false),
            ("resources/read", "{}", -32602, true), ("resources/read", "{\"uri\":\" \"}", -32602, true),
            ("resources/read", JsonSerializer.Serialize(new { uri = new string('x', 4097) }), -32602, true),
            ("initialize", "{}", -32600, true), ("unknown", null, -32601, true),
            ("", null, -32600, true), (new string('x', 129), null, -32600, true)
        ];
        foreach (var (method, parameters, error, noCall) in cases)
        {
            var before = externalApp.Calls;
            var request = Request("7", method, parameters);
            var actual = await InvokeAsync(external, request);
            var expected = await native.ProcessAsync(request, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(expected, actual), "Method invocation differs from ready native STDIO: " + method);
            if (error is { } code) Assert.Equal(code, actual["error"]!["code"]!.GetValue<int>());
            else Assert.True(actual["result"] is not null, "A valid method returned an error: " + method);
            if (noCall) Assert.Equal(before, externalApp.Calls);
            Assert.Equal(nativeApp.Calls, externalApp.Calls);
        }

        foreach (var rawId in new[] { "1", "\"1\"", "-0", "1.25", "1e+2", "9007199254740993", "\"Ω😀\"" })
        {
            var request = Request(rawId, "ping");
            var actual = await InvokeAsync(external, request);
            Assert.True(JsonNode.DeepEquals(await native.ProcessAsync(request, CancellationToken.None), actual),
                "An original typed ID changed between transports.");
            using var id = JsonDocument.Parse(rawId);
            if (id.RootElement.ValueKind == JsonValueKind.String)
                Assert.Equal(id.RootElement.GetString(), actual["id"]!.GetValue<string>());
            else Assert.Equal(rawId, actual["id"]!.ToJsonString());
        }

        foreach (var rawId in new[] { "null", "true", "[]", "{}", JsonSerializer.Serialize(new string('x', 1025)), new string('7', 1025) })
        {
            var before = externalApp.Calls;
            // Invalid ID must win over the simultaneously invalid method.
            var request = Request(rawId, "");
            var actual = await InvokeAsync(external, request);
            Assert.True(JsonNode.DeepEquals(await native.ProcessAsync(request, CancellationToken.None), actual),
                "ID validation order or error shape changed.");
            Assert.Equal(-32600, actual["error"]!["code"]!.GetValue<int>());
            Assert.Equal(before, externalApp.Calls);
        }
    }

    internal static async Task ConcurrentTypedIdsAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var app = new FixtureApplication(async (_, arguments, _) =>
        {
            if (Interlocked.Increment(ref count) == 3) entered.TrySetResult();
            await release.Task;
            return new JsonObject { ["marker"] = arguments!.Value.GetProperty("marker").GetString() };
        });
        var processor = new CortexMcpInvocationProcessor(app);
        var ids = new[] { JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement("1"), JsonSerializer.SerializeToElement("1") };
        var calls = ids.Select((id, index) => processor.InvokeAsync(id, "tools/call",
            JsonSerializer.SerializeToElement(new { name = "overlap", arguments = new { marker = "call" + index } }),
            CancellationToken.None).AsTask()).ToArray();
        try
        {
            await entered.Task.WaitAsync(Limit);
            Assert.True(calls.All(call => !call.IsCompleted), "A shared facade serialized or completed a held invocation.");
            release.TrySetResult();
            var replies = await Task.WhenAll(calls).WaitAsync(Limit);
            for (var index = 0; index < replies.Length; index++)
            {
                Assert.Equal("call" + index, replies[index]["result"]!["marker"]!.GetValue<string>());
                Assert.Equal(ids[index].GetRawText(), replies[index]["id"]!.ToJsonString());
            }
            Assert.Equal(3, app.Calls);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(calls).WaitAsync(Limit);
        }
    }

    internal static async Task CancellationAndFailureAsync()
    {
        var app = new FixtureApplication();
        var external = new CortexMcpInvocationProcessor(app);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => external.InvokeAsync(
                JsonSerializer.SerializeToElement(1), "tools/list", null, canceled.Token).AsTask());
            Assert.Equal(0, app.Calls);
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new FixtureApplication(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new JsonObject();
        });
        using (var cancellation = new CancellationTokenSource())
        {
            var call = new CortexMcpInvocationProcessor(blocking).InvokeAsync(JsonSerializer.SerializeToElement("cancel"),
                "tools/call", JsonSerializer.SerializeToElement(new { name = "hold" }), cancellation.Token).AsTask();
            try
            {
                await started.Task.WaitAsync(Limit);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(Limit));
                Assert.Equal(1, blocking.Calls);
            }
            finally
            {
                cancellation.Cancel();
                try { await call.WaitAsync(Limit); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }

        using var nativeDiagnostics = new StringWriter();
        using var externalDiagnostics = new StringWriter();
        static ValueTask<JsonObject> Fail(string _, JsonElement? __, CancellationToken ___) =>
            throw new InvalidOperationException("owned fixture failure");
        var nativeFailureApp = new FixtureApplication(Fail);
        var externalFailureApp = new FixtureApplication(Fail);
        var native = await ReadyAsync(nativeFailureApp, nativeDiagnostics);
        var facade = new CortexMcpInvocationProcessor(externalFailureApp, externalDiagnostics);
        var request = Request("\"original\"", "tools/call", "{\"name\":\"fail\"}");
        var nativeResponse = await native.ProcessAsync(request, CancellationToken.None);
        var response = await InvokeAsync(facade, request);
        Assert.True(JsonNode.DeepEquals(nativeResponse, response), "Domain failure changed the native error contract.");
        Assert.Equal(-32603, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("cortex_mcp_outcome_unknown", DiagnosticCode(response));
        Assert.Equal("Unknown", response["error"]!["data"]!["outcome"]!.GetValue<string>());
        Assert.Equal("original", response["id"]!.GetValue<string>());
        Assert.Equal(1, nativeFailureApp.Calls);
        Assert.Equal(1, externalFailureApp.Calls);
        foreach (var diagnostics in new[] { nativeDiagnostics, externalDiagnostics })
        {
            var lines = diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(1, lines.Length);
            Assert.Contains("operation=mcp.request code=cortex_mcp_request_failed", lines[0]);
        }
    }

    private static async Task<McpRequestProcessor> ReadyAsync(FixtureApplication app, TextWriter? diagnostics = null)
    {
        var processor = new McpRequestProcessor(app, diagnostics ?? TextWriter.Null);
        Assert.True((await processor.ProcessAsync(Initialize, CancellationToken.None))!["result"] is not null, "Fixture initialize failed.");
        await processor.ProcessAsync(Initialized, CancellationToken.None);
        Assert.True(processor.IsReady, "The native fixture did not become ready.");
        return processor;
    }

    private static string Request(string id, string method, string? parameters = null) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + JsonSerializer.Serialize(method) +
        (parameters is null ? "" : ",\"params\":" + parameters) + "}";

    private static async Task<JsonObject> InvokeAsync(CortexMcpInvocationProcessor processor, string request)
    {
        using var document = JsonDocument.Parse(request);
        var root = document.RootElement;
        return await processor.InvokeAsync(root.GetProperty("id"), root.GetProperty("method").GetString()!,
            root.TryGetProperty("params", out var parameters) ? parameters : null, CancellationToken.None);
    }

    private static string DiagnosticCode(JsonObject response) => response["error"]!["data"]!["code"]!.GetValue<string>();

    private sealed class FixtureApplication(
        Func<string, JsonElement?, CancellationToken, ValueTask<JsonObject>>? call = null) : ICortexMcpProtocolApplication
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public JsonArray ListTools()
        {
            Interlocked.Increment(ref _calls);
            return new JsonArray(new JsonObject { ["name"] = "echo", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        }
        public ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return call is not null ? call(name, arguments, cancellationToken) : ValueTask.FromResult(new JsonObject
            { ["fixtureName"] = name, ["argumentKind"] = arguments?.ValueKind.ToString() ?? "absent" });
        }
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(new JsonArray(new JsonObject { ["uri"] = "cortex://fixture/item", ["name"] = "fixture" }));
        }
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(new JsonArray(new JsonObject { ["uriTemplate"] = "cortex://fixture/{id}", ["name"] = "fixture" }));
        }
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<JsonObject?>(uri == "cortex://fixture/item"
                ? new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["text"] = "fixture" }) } : null);
        }
    }
}
