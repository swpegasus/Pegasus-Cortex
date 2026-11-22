using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Mcp.V2;

public static class CortexV2McpContractTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-13T12:00:00Z");

    public static async Task RunAsync()
    {
        ExactlyEightToolsStayBelowTenKiB();
        SchemasExposeTypedActionsAndExplicitNamespaces();
        await OmittedAdvertisedDefaultsAreAppliedAsync().ConfigureAwait(false);
        await PreviewDefaultsAreCompactAndDetailedIsExplicitAsync().ConfigureAwait(false);
        await ContentSchemaAndDispatchPreserveJsonTextAsync().ConfigureAwait(false);
        await ReadableUtf8JsonAsync().ConfigureAwait(false);
        await FullReadContentIsNotDuplicatedAsync().ConfigureAwait(false);
        await CompactPreviewsAndBatchExactReadsStayTypedAsync().ConfigureAwait(false);
        await WorstCaseContentSurvivesProtocolBoundsAsync().ConfigureAwait(false);
        await EmptyResourcesAndInvalidArgumentsFailClosedAsync().ConfigureAwait(false);
    }

    private static void ExactlyEightToolsStayBelowTenKiB()
    {
        var tools = CortexV2McpContract.ListTools();
        Equal(8, tools.Count);
        Equal(
            string.Join(',', CortexV2McpContract.ToolNames),
            string.Join(',', tools.Select(node => node!["name"]!.GetValue<string>())));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tools).Length;
        True(bytes < 10 * 1024, $"V2 tools/list is {bytes} bytes.");
        Console.WriteLine($"METRIC v2-tool-catalog bytes={bytes} tools={tools.Count}");
    }

    private static void SchemasExposeTypedActionsAndExplicitNamespaces()
    {
        var tools = CortexV2McpContract.ListTools()
            .Select(node => node!.AsObject())
            .ToDictionary(node => node["name"]!.GetValue<string>(), StringComparer.Ordinal);
        var stateful = new[]
        {
            "cortex_start", "cortex_search",
            "cortex_task", "cortex_knowledge", "cortex_finish"
        };
        foreach (var name in stateful)
        {
            var schema = tools[name]["inputSchema"]!.AsObject();
            var required = schema["required"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray();
            var expected = name is "cortex_start" or "cortex_search"
                ? "namespaces"
                : "namespace";
            True(required.Contains(expected, StringComparer.Ordinal),
                $"{name} does not require explicit {expected}.");
        }
        var namespaces = tools["cortex_namespaces"]["inputSchema"]!["properties"]!.AsObject();
        True(namespaces.ContainsKey("afterNamespace") && namespaces.ContainsKey("limit") &&
            namespaces.ContainsKey("maxUtf8Bytes"), "Namespace pagination is not advertised.");

        EnumEqual<CortexTaskActionV2>(tools["cortex_task"], "action");
        EnumEqual<CortexKnowledgeActionV2>(tools["cortex_knowledge"], "action");
        EnumEqual<CortexRecordTypeV2>(tools["cortex_read"], "recordType");
        var readSchema = tools["cortex_read"]["inputSchema"]!.AsObject();
        True(readSchema["oneOf"] is JsonArray { Count: 2 } &&
                readSchema["properties"]!["records"] is not null,
            "Exact read does not advertise mutually exclusive single and batch shapes.");
        var startProperties = tools["cortex_start"]["inputSchema"]!["properties"]!.AsObject();
        True(startProperties.ContainsKey("taskId"), "Startup does not advertise the optional exact task ID.");
        Equal("compact", startProperties["format"]!["default"]!.GetValue<string>());
        Equal("compact", tools["cortex_search"]["inputSchema"]!["properties"]!["format"]!["default"]!.GetValue<string>());
        var startBudget = startProperties["maxUtf8Bytes"]!.AsObject();
        Equal(
            (long)CortexV2Limits.DefaultStartResponseUtf8Bytes,
            JsonSerializer.Deserialize<long>(startBudget["default"]!.ToJsonString()));
        Equal(
            (long)CortexV2Limits.MaximumStartResponseUtf8Bytes,
            JsonSerializer.Deserialize<long>(startBudget["maximum"]!.ToJsonString()));
        EnumEqual<CortexKnowledgeKindV2>(tools["cortex_knowledge"], "kind");
        EnumEqual<CortexKnowledgeTierV2>(tools["cortex_knowledge"], "tier");
        var tierValues = Property(tools["cortex_knowledge"], "tier")["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray();
        Equal("explicit,verified,inferred", string.Join(',', tierValues));
        var provenance = Property(tools["cortex_knowledge"], "provenance")
            ["properties"]!.AsObject();
        True(!provenance.ContainsKey("actor") && !provenance.ContainsKey("capturedAtUtc"),
            "Caller-controlled provenance exposed runtime actor or time.");
    }

    private static async Task OmittedAdvertisedDefaultsAreAppliedAsync()
    {
        var store = new FakeStore();
        var app = Mcp(store);
        var calls = new (string Name, object Arguments)[]
        {
            ("cortex_namespaces", new { }),
            ("cortex_start", new { namespaces = new[] { "project-a" }, intent = "resume" }),
            ("cortex_search", new { namespaces = new[] { "project-a" }, text = "parser" }),
            ("cortex_read", new { @namespace = "project-a", recordType = "knowledge", recordId = "record-1" }),
            ("cortex_task", new { @namespace = "project-a", action = "list" }),
            ("cortex_knowledge", new { @namespace = "project-a", action = "read", recordId = "record-1" }),
            ("cortex_finish", new
            {
                @namespace = "project-a",
                taskId = "task-1",
                expectedRevision = 1,
                taskAction = "none",
                checkpoint = "complete",
                evidence = "unit test",
                knowledge = Array.Empty<object>(),
                idempotencyKey = "finish-1"
            })
        };
        foreach (var call in calls)
        {
            var result = await app.CallToolAsync(
                call.Name,
                JsonSerializer.SerializeToElement(call.Arguments),
                CancellationToken.None).ConfigureAwait(false);
            True(!result["isError"]!.GetValue<bool>(),
                $"{call.Name} rejected its advertised defaults.");
        }

        Equal(CortexV2Limits.DefaultPageSize, store.LastNamespaces!.Limit);
        Equal(CortexV2Limits.DefaultResponseUtf8Bytes, store.LastNamespaces.MaxUtf8Bytes);
        Equal(CortexV2Limits.DefaultStartResponseUtf8Bytes, store.LastStart!.MaxUtf8Bytes);
        Equal(CortexResponseFormatV2.Compact, store.LastStart.Format);
        Equal(CortexV2Limits.DefaultPageSize, store.LastSearch!.Limit);
        Equal(CortexV2Limits.DefaultResponseUtf8Bytes, store.LastSearch.MaxUtf8Bytes);
        Equal(CortexResponseFormatV2.Compact, store.LastSearch.Format);
        Equal(CortexV2Limits.MaximumResponseUtf8Bytes, store.LastRead!.MaxUtf8Bytes);
        Equal(CortexV2Limits.DefaultPageSize, store.LastTask!.Limit);
        Equal(CortexV2Limits.DefaultResponseUtf8Bytes, store.LastTask.MaxUtf8Bytes);
        Equal(CortexV2Limits.DefaultResponseUtf8Bytes, store.LastKnowledge!.MaxUtf8Bytes);
        Equal(CortexV2Limits.MaximumCompactResponseUtf8Bytes, store.LastFinish!.MaxUtf8Bytes);

        var explicitZero = await app.CallToolAsync(
            "cortex_namespaces",
            JsonSerializer.SerializeToElement(new { limit = 0 }),
            CancellationToken.None).ConfigureAwait(false);
        True(explicitZero["isError"]!.GetValue<bool>(),
            "An explicit invalid value was replaced with a default.");

        var taskAware = await app.CallToolAsync(
            "cortex_start",
            JsonSerializer.SerializeToElement(new
            {
                namespaces = new[] { "project-a", "shared-programming" },
                intent = "resume exact task",
                taskId = "019ff269-3c07-7082-87cf-d9325d7e24ba",
                maxUtf8Bytes = CortexV2Limits.MaximumStartResponseUtf8Bytes
            }),
            CancellationToken.None).ConfigureAwait(false);
        True(!taskAware["isError"]!.GetValue<bool>(), "Task-aware startup dispatch was rejected.");
        Equal("019ff269-3c07-7082-87cf-d9325d7e24ba", store.LastStart!.TaskId);
        Equal(CortexV2Limits.MaximumStartResponseUtf8Bytes, store.LastStart.MaxUtf8Bytes);
    }

    private static async Task PreviewDefaultsAreCompactAndDetailedIsExplicitAsync()
    {
        var app = Mcp(new FakeStore());
        foreach (var name in new[] { "cortex_start", "cortex_search" })
        {
            var arguments = new JsonObject
            {
                ["namespaces"] = new JsonArray("project-a"),
                [name == "cortex_start" ? "intent" : "text"] = "parser"
            };
            async Task<JsonObject> Call() => await app.CallToolAsync(name,
                JsonSerializer.SerializeToElement(arguments), CancellationToken.None).ConfigureAwait(false);
            var omitted = await Call().ConfigureAwait(false);
            arguments["format"] = "compact";
            var compact = await Call().ConfigureAwait(false);
            True(JsonNode.DeepEquals(omitted, compact), $"{name} omitted format differs from compact.");
            True(omitted["structuredContent"]!["ns"] is JsonArray &&
                omitted["structuredContent"]!["items"] is JsonArray,
                $"{name} compact default lost namespaces or empty results.");

            arguments["format"] = "detailed";
            var detailed = await Call().ConfigureAwait(false);
            True(!detailed["isError"]!.GetValue<bool>() &&
                detailed["structuredContent"]!["effectiveNamespaces"] is JsonArray &&
                detailed["structuredContent"]!["schema"] is null,
                $"{name} explicit detailed result changed shape.");
            Equal(detailed["structuredContent"]!["effectiveNamespaces"]!.ToJsonString(),
                omitted["structuredContent"]!["ns"]!.ToJsonString());
        }
    }

    private static async Task ContentSchemaAndDispatchPreserveJsonTextAsync()
    {
        var store = new FakeStore();
        var app = Mcp(store);
        const string exact = "line one\r\nline two\n\t雪 🚀\u0001done";
        var arguments = JsonSerializer.SerializeToElement(new
        {
            @namespace = "project-a",
            action = "create",
            recordId = "record-1",
            idempotencyKey = "idem-1",
            kind = "correction",
            tier = "explicit",
            title = "Multiline",
            content = exact,
            tags = new[] { "parser" },
            provenance = new { source = "user", sourceId = "request-1", evidence = "direct" }
        });
        var result = await app.CallToolAsync(
            "cortex_knowledge", arguments, CancellationToken.None).ConfigureAwait(false);
        True(!result["isError"]!.GetValue<bool>(), "Valid JSON content was rejected.");
        Equal(exact, store.LastKnowledge!.Content);
        Equal("os_test_user", store.LastKnowledge.EffectiveProvenance!.Actor);

        var contentSchema = Property(
            CortexV2McpContract.ListTools().Select(node => node!.AsObject())
                .Single(node => node["name"]!.GetValue<string>() == "cortex_knowledge"),
            "content");
        Equal(0, contentSchema["minLength"]!.GetValue<int>());
        Equal(CortexV2Limits.MaximumContentCharacters,
            contentSchema["maxLength"]!.GetValue<int>());
    }

    private static async Task ReadableUtf8JsonAsync()
    {
        const string exact = "Owner's <tag> & café Ω 雪 🚀\r\n\t\"quoted\"\u0002" +
            " C:\\Pegasus\\Cortex /srv/cortex literal \\u0027";
        var app = Mcp(new FakeStore());
        var created = await app.CallToolAsync("cortex_knowledge", JsonSerializer.SerializeToElement(new
        {
            @namespace = "project-a", action = "create", recordId = "utf8-record",
            idempotencyKey = "utf8-create", kind = "observation", tier = "verified",
            title = "Unicode round trip", content = exact, tags = Array.Empty<string>(),
            provenance = new { source = "agent", evidence = "fixture" }
        }), CancellationToken.None).ConfigureAwait(false);
        True(!created["isError"]!.GetValue<bool>(), "Readable JSON fixture create failed.");
        var read = await app.CallToolAsync("cortex_read", JsonSerializer.SerializeToElement(new
        {
            @namespace = "project-a", recordType = "knowledge", recordId = "utf8-record", revision = 1
        }), CancellationToken.None).ConfigureAwait(false);
        var response = McpProtocol.Success(JsonValue.Create(2), read);
        var wire = BoundedJsonResponseFormatter.Format(response);
        True(!wire.ReplacedOversizedResponse &&
            wire.Json.Contains("Owner's <tag> & café Ω 雪", StringComparison.Ordinal),
            "The bounded outer JSON writer escaped readable punctuation/Unicode.");
        True(!wire.Json.Contains('\u0002') && !wire.Json.Contains('\r') && !wire.Json.Contains('\n'),
            "Raw content controls escaped JSON framing.");
        var parsed = JsonNode.Parse(wire.Json)!;
        Equal(exact, parsed["result"]!["structuredContent"]!["record"]!["content"]!.GetValue<string>());
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(response).Length;
        var newBytes = Encoding.UTF8.GetByteCount(wire.Json);
        True(newBytes < oldBytes, "Readable JSON did not reduce the wire-byte count.");
        True(BoundedJsonResponseFormatter.FitsWithinLimit(response, McpProtocol.MaximumResponseBytes, out var measured) && measured == newBytes,
            "Response measurement disagrees with the emitted UTF-8 bytes.");
        True(!BoundedJsonResponseFormatter.FitsWithinLimit(response, newBytes - 1, out _),
            "The readable JSON writer exceeded its exact byte budget.");

        var error = await app.CallToolAsync("cortex_search", JsonSerializer.SerializeToElement(new
        {
            namespaces = new[] { "project-a" }, text = "fixture", unexpected = true
        }), CancellationToken.None).ConfigureAwait(false);
        True(error["isError"]!.GetValue<bool>(), "Unknown V2 input field was admitted.");
        var errorText = error["content"]![0]!["text"]!.GetValue<string>();
        True(errorText.Contains("'unexpected'", StringComparison.Ordinal) &&
            !errorText.Contains("\\u0027", StringComparison.Ordinal),
            "Embedded V2 JSON error still escapes ordinary apostrophes.");
        Equal(error["structuredContent"]!["error"]!["message"]!.GetValue<string>(),
            JsonNode.Parse(errorText)!["error"]!["message"]!.GetValue<string>());

        // Valid escape-heavy requests remain accepted; invalid UTF-8 stays rejected.
        var session = new McpJsonLineSessionProcessor(app);
        var malformed = await session.ProcessAsync(new byte[] { 0xff }, CancellationToken.None).ConfigureAwait(false);
        Equal(-32700, JsonNode.Parse(malformed!.Value.Span)!["error"]!["code"]!.GetValue<int>());
        Console.WriteLine($"METRIC cortex-readable-json bytes={newBytes} previous={oldBytes}");
    }

    private static async Task FullReadContentIsNotDuplicatedAsync()
    {
        var store = new FakeStore { FullRead = true };
        var app = Mcp(store);
        var result = await app.CallToolAsync(
            "cortex_read",
            JsonSerializer.SerializeToElement(new
            {
                @namespace = "project-a",
                recordType = "knowledge",
                recordId = "record-large",
                maxUtf8Bytes = CortexV2Limits.MaximumResponseUtf8Bytes
            }),
            CancellationToken.None).ConfigureAwait(false);
        True(!result["isError"]!.GetValue<bool>(), "Large exact read failed.");
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        True(text.Length <= 512, "MCP text summary is not bounded.");
        True(!text.Contains("payload-marker", StringComparison.Ordinal),
            "Full record content was duplicated into MCP text.");
        var structured = result["structuredContent"]!.ToJsonString();
        True(structured.Contains("payload-marker", StringComparison.Ordinal),
            "Full record was omitted from structuredContent.");
        True(Encoding.UTF8.GetByteCount(text) * 20 < Encoding.UTF8.GetByteCount(structured),
            "Text summary is not materially smaller than the exact record.");
    }

    private static async Task CompactPreviewsAndBatchExactReadsStayTypedAsync()
    {
        var store = new FakeStore();
        var app = Mcp(store);
        var compactStart = await app.CallToolAsync(
            "cortex_start",
            JsonSerializer.SerializeToElement(new
            {
                namespaces = new[] { "project-a" },
                intent = "resume parser",
                taskId = "task-1",
                phaseId = "implementation",
                format = "compact"
            }),
            CancellationToken.None).ConfigureAwait(false);
        True(!compactStart["isError"]!.GetValue<bool>(), "Compact startup was rejected.");
        var compact = compactStart["structuredContent"]!.AsObject();
        Equal("cortex.start.compact/1", compact["schema"]!.GetValue<string>());
        True(compact.ContainsKey("ns") && compact.ContainsKey("items") &&
                !compact.ContainsKey("effectiveNamespaces"),
            "Compact startup did not use the stable short-key response shape.");
        Equal("implementation", store.LastStart!.PhaseId);
        Equal(CortexResponseFormatV2.Compact, store.LastStart.Format);

        var batch = await app.CallToolAsync(
            "cortex_read",
            JsonSerializer.SerializeToElement(new
            {
                records = new object[]
                {
                    new { @namespace = "project-a", recordType = "task", recordId = "task-1", revision = 2 },
                    new { @namespace = "shared-programming", recordType = "knowledge", recordId = "tenet-1" }
                }
            }),
            CancellationToken.None).ConfigureAwait(false);
        True(!batch["isError"]!.GetValue<bool>(),
            $"Typed batch exact read was rejected: {batch.ToJsonString()}");
        var structured = batch["structuredContent"]!.AsObject();
        Equal(2, structured["items"]!.AsArray().Count);
        Equal("project-a,shared-programming", string.Join(',',
            structured["effectiveNamespaces"]!.AsArray().Select(value => value!.GetValue<string>())));
        True(structured.ToJsonString().Contains("\"content\":\"Content\"", StringComparison.Ordinal),
            "Batch read returned coordinates without exact full record bodies.");
    }

    private static async Task WorstCaseContentSurvivesProtocolBoundsAsync()
    {
        var store = new FakeStore();
        var application = Mcp(store);
        var content = new string('\u0001', CortexV2Limits.MaximumContentCharacters);
        var arguments = JsonSerializer.SerializeToNode(new
        {
            @namespace = "project-a",
            action = "create",
            recordId = "control-heavy",
            idempotencyKey = "control-heavy-create",
            kind = "observation",
            tier = "inferred",
            title = "Control-heavy round trip",
            content,
            tags = Array.Empty<string>(),
            provenance = new { source = "agent" }
        })!.AsObject();
        var initialize = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = McpProtocol.Version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "v2-bounds-test", ["version"] = "1" }
            }
        };
        var initialized = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject()
        };
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "cortex_knowledge",
                ["arguments"] = arguments
            }
        };
        var callJson = call.ToJsonString();
        var read = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "cortex_read",
                ["arguments"] = new JsonObject
                {
                    ["namespace"] = "project-a",
                    ["recordType"] = "knowledge",
                    ["recordId"] = "control-heavy"
                }
            }
        };
        var callBytes = Encoding.UTF8.GetByteCount(callJson);
        True(callBytes > 128 * 1024,
            "Worst-case valid content did not exercise the raised request bound.");
        True(callBytes <= McpProtocol.MaximumRequestBytes,
            "Worst-case valid content exceeds the bounded protocol request capacity.");

        var input = new StringReader(string.Join(
            '\n',
            initialize.ToJsonString(),
            initialized.ToJsonString(),
            callJson,
            read.ToJsonString()) + "\n");
        var output = new StringWriter();
        var diagnostics = new StringWriter();
        var server = new McpStdioServer(application, input, output, diagnostics);
        await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(3, lines.Length);
        var createResponse = JsonNode.Parse(lines[1])!["result"]!.AsObject();
        True(!createResponse["isError"]!.GetValue<bool>(),
            $"Worst-case content failed through STDIO: {diagnostics}");
        True(createResponse["structuredContent"]!["record"]!["content"] is null,
            "A knowledge write receipt repeated the full content.");
        var toolResponse = JsonNode.Parse(lines[2])!.AsObject();
        var result = toolResponse["result"]!.AsObject();
        True(!result["isError"]!.GetValue<bool>(),
            $"Worst-case exact read failed through STDIO: {diagnostics}");
        var roundTripped = result["structuredContent"]!["record"]!["content"]!.GetValue<string>();
        Equal(content, roundTripped);
        var summary = result["content"]![0]!["text"]!.GetValue<string>();
        True(summary.Length <= 512 && !summary.Contains(content[..64], StringComparison.Ordinal),
            "Worst-case content was duplicated into the text summary.");
        True(Encoding.UTF8.GetByteCount(lines[1]) <= McpProtocol.MaximumResponseBytes,
            "Worst-case valid result exceeded the bounded protocol response capacity.");

        var oversized = McpProtocol.Success(
            JsonValue.Create(3),
            new JsonObject { ["value"] = new string('x', McpProtocol.MaximumResponseBytes + 1) });
        var formatted = BoundedJsonResponseFormatter.Format(oversized);
        True(formatted.ReplacedOversizedResponse,
            "A response above the raised bound was not replaced.");
        True(Encoding.UTF8.GetByteCount(formatted.Json) <= McpProtocol.MaximumResponseBytes,
            "The oversized-response replacement exceeded the raised bound.");
    }

    private static async Task EmptyResourcesAndInvalidArgumentsFailClosedAsync()
    {
        var app = Mcp(new FakeStore());
        Equal(0, (await app.ListResourcesAsync(CancellationToken.None)).Count);
        Equal(0, (await app.ListResourceTemplatesAsync(CancellationToken.None)).Count);
        Equal<JsonObject?>(null, await app.ReadResourceAsync("cortex://none", CancellationToken.None));
        var nullResult = await app.CallToolAsync(
            "cortex_start", null, CancellationToken.None).ConfigureAwait(false);
        True(nullResult["isError"]!.GetValue<bool>(), "Null arguments were accepted.");
        var namespacesResult = await app.CallToolAsync(
            "cortex_namespaces",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None).ConfigureAwait(false);
        True(!namespacesResult["isError"]!.GetValue<bool>(),
            "Optional namespace page defaults were rejected.");
        var ambiguousRead = await app.CallToolAsync(
            "cortex_read",
            JsonSerializer.SerializeToElement(new
            {
                @namespace = "project-a",
                recordId = "same-id"
            }),
            CancellationToken.None).ConfigureAwait(false);
        True(ambiguousRead["isError"]!.GetValue<bool>(),
            "Exact read accepted a missing record type discriminator.");
        var forged = JsonDocument.Parse(
            """{"namespace":"project-a","action":"create","recordId":"x","idempotencyKey":"i","kind":"correction","tier":"explicit","title":"t","content":"c","provenance":{"source":"user","actor":"forged","capturedAtUtc":"2026-01-01T00:00:00Z"}}""")
            .RootElement.Clone();
        var forgedResult = await app.CallToolAsync(
            "cortex_knowledge", forged, CancellationToken.None).ConfigureAwait(false);
        True(forgedResult["isError"]!.GetValue<bool>(), "Forged provenance was accepted.");
    }

    private static CortexV2McpApplication Mcp(FakeStore store) => new(
        new CortexV2Application(store, "os_test_user", new FixedTimeProvider(Now)));

    private static JsonObject Property(JsonObject tool, string name) =>
        tool["inputSchema"]!["properties"]![name]!.AsObject();

    private static void EnumEqual<T>(JsonObject tool, string property) where T : struct, Enum
    {
        var actual = Property(tool, property)["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray();
        var expected = Enum.GetNames<T>()
            .Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray();
        Equal(string.Join(',', expected), string.Join(',', actual));
    }

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeStore : ICortexV2Store
    {
        public CortexNamespacesRequestV2? LastNamespaces { get; private set; }
        public CortexStartRequestV2? LastStart { get; private set; }
        public CortexSearchRequestV2? LastSearch { get; private set; }
        public CortexReadRequestV2? LastRead { get; private set; }
        public CortexTaskCommandV2? LastTask { get; private set; }
        public CortexKnowledgeCommandV2? LastKnowledge { get; private set; }
        public CortexFinishRequestV2? LastFinish { get; private set; }
        public bool FullRead { get; set; }

        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CortexStatusV2(
                "Pegasus Cortex", "2", "ready", "fake", false, false,
                1, 1, 1, []));

        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
            CortexNamespacesRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastNamespaces = request;
            return ValueTask.FromResult(new CortexNamespacesResultV2(
                [new("project-a", "Project A", 1, Now, ["project-a"])],
                null,
                false,
                []));
        }

        public ValueTask<CortexStartResultV2> StartAsync(
            CortexStartRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastStart = request;
            return ValueTask.FromResult(new CortexStartResultV2(
                CortexContextDispositionV2.Snapshot, request.Namespaces,
                "cursor-1", [], false));
        }

        public ValueTask<CortexSearchResultV2> SearchAsync(
            CortexSearchRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastSearch = request;
            return ValueTask.FromResult(new CortexSearchResultV2(
                request.Namespaces, [], null, false));
        }

        public ValueTask<CortexReadResultV2> ReadAsync(
            CortexReadRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastRead = request;
            object? record = LastKnowledge is { } knowledge &&
                string.Equals(knowledge.RecordId, request.RecordId, StringComparison.Ordinal)
                ? new CortexKnowledgeV2(
                    knowledge.Namespace,
                    knowledge.RecordId!,
                    1,
                    CortexKnowledgeStateV2.Active,
                    knowledge.Kind!.Value,
                    knowledge.Tier!.Value,
                    knowledge.Title!,
                    knowledge.Content!,
                    knowledge.Tags ?? [],
                    knowledge.EffectiveProvenance!,
                    null,
                    Now,
                    Now,
                    null)
                : FullRead
                ? new CortexKnowledgeV2(
                    request.Namespace, request.RecordId, 1,
                    CortexKnowledgeStateV2.Active,
                    CortexKnowledgeKindV2.Tenet,
                    CortexKnowledgeTierV2.Explicit,
                    "Large record",
                    "payload-marker:" + new string('x', 24 * 1024),
                    [],
                    Provenance(),
                    null,
                    Now,
                    Now,
                    null)
                : request.RecordType == CortexRecordTypeV2.Task
                    ? new CortexTaskV2(
                        request.Namespace, request.RecordId, request.Revision ?? 1,
                        CortexTaskStatusV2.Open, "Task", "Content", null, null, [], null,
                        Now, Now, null, null, Provenance())
                    : new CortexKnowledgeV2(
                        request.Namespace, request.RecordId, request.Revision ?? 1,
                        CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Observation,
                        CortexKnowledgeTierV2.Inferred, "Knowledge", "Content", [],
                        Provenance(), null, Now, Now, null);
            return ValueTask.FromResult(new CortexReadResultV2(
                [request.Namespace], request.RecordType, request.RecordId, request.Revision ?? 1, record));
        }

        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
            CortexTaskCommandV2 command,
            CancellationToken cancellationToken)
        {
            LastTask = command;
            return ValueTask.FromResult(new CortexTaskResultV2(
                [command.Namespace], "accepted", null, [], null, null));
        }

        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
            CortexKnowledgeCommandV2 command,
            CancellationToken cancellationToken)
        {
            LastKnowledge = command;
            var provenance = command.EffectiveProvenance ?? Provenance();
            var record = new CortexKnowledgeReceiptV2(
                command.Namespace,
                command.RecordId!,
                1,
                CortexKnowledgeStateV2.Active,
                command.Kind ?? CortexKnowledgeKindV2.Observation,
                command.Tier ?? CortexKnowledgeTierV2.Inferred,
                command.Title ?? "Knowledge",
                new CortexProvenanceReceiptV2(
                    provenance.Source,
                    provenance.Actor,
                    provenance.SourceId,
                    provenance.CapturedAtUtc));
            return ValueTask.FromResult(new CortexKnowledgeResultV2(
                [command.Namespace], "accepted", record));
        }

        public ValueTask<CortexFinishResultV2> FinishAsync(
            CortexFinishRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastFinish = request;
            return ValueTask.FromResult(new CortexFinishResultV2(
                [request.Namespace], "accepted",
                new CortexTaskSummaryV2(
                    request.Namespace, request.TaskId, request.ExpectedRevision + 1,
                    CortexTaskStatusV2.Completed, "Task", request.Checkpoint,
                    null, null, null),
                request.Knowledge.Select(value => new CortexKnowledgeReceiptV2(
                    request.Namespace,
                    value.RecordId,
                    value.ExpectedRevision is null ? 1 : value.ExpectedRevision.Value + 1,
                    CortexKnowledgeStateV2.Active,
                    value.Kind,
                    value.Tier,
                    value.Title,
                    new CortexProvenanceReceiptV2(
                        value.EffectiveProvenance!.Source,
                        value.EffectiveProvenance.Actor,
                        value.EffectiveProvenance.SourceId,
                        value.EffectiveProvenance.CapturedAtUtc))).ToArray(),
                "cursor-2"));
        }

        private static CortexProvenanceV2 Provenance() => new(
            CortexProvenanceSourceV2.User,
            "os_test_user",
            null,
            null,
            Now);
    }
}
