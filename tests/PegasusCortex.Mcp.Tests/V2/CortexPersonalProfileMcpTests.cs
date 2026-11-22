using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.V2;

internal static class CortexPersonalProfileMcpTests
{
    private const string Project = "project.profile-tests";
    private const string Personal = "knowledge.user";
    private const string BodyMarker = "personal-exact-body-marker";

    public static async Task RunAsync()
    {
        foreach (var format in new[] { "compact", "detailed" })
            await RetrievalWireStatesAsync(format).ConfigureAwait(false);
        await ReadErrorsRecoveryAndStaleResultsAsync().ConfigureAwait(false);
        await UnmanagedWireShapeIsUnchangedAsync().ConfigureAwait(false);
        await CompactOptionalsDoNotAlterExactBodiesAsync().ConfigureAwait(false);
    }

    private static async Task RetrievalWireStatesAsync(string format)
    {
        var store = new ReadStore();
        var provider = new ProfileProvider();
        var app = Mcp(store, provider);
        Assert.Equal(8, app.ListTools().Count);
        Assert.Equal(string.Join(',', CortexV2McpContract.ToolNames), string.Join(',',
            app.ListTools().Select(value => value!["name"]!.GetValue<string>())));

        var status = Success(await Call(app, "cortex_status", new { }));
        Assert.Equal("ready", status["state"]!.GetValue<string>());
        Assert.Equal("not_loaded", status["personalProfile"]!["state"]!.GetValue<string>());
        Assert.True(!status["personalProfile"]!["indicatesMissingData"]!.GetValue<bool>(),
            "Unloaded profile status incorrectly indicated missing data.");
        Assert.True(!status["personalProfile"]!["remapRequired"]!.GetValue<bool>(),
            "Unloaded profile status incorrectly required remapping.");
        Assert.True(!status["personalProfile"]!["retryable"]!.GetValue<bool>(),
            "An intentional unloaded state was incorrectly marked retryable.");
        foreach (var tool in new[] { "cortex_start", "cortex_search" })
        {
            var mixed = await Call(app, tool, RetrievalArguments(tool, format, ["user.preferences", Project]));
            var structured = Success(mixed);
            var namespaceKey = format == "compact" ? "ns" : "effectiveNamespaces";
            Assert.Equal(Project, structured[namespaceKey]!.AsArray().Single()!.GetValue<string>());
            Assert.Equal("not_loaded", structured["personalProfile"]!["state"]!.GetValue<string>());
            Assert.Equal("personal_profile_not_loaded", structured["personalProfile"]!["issueCode"]!.GetValue<string>());
            Assert.True(!structured["personalProfile"]!.AsObject().ContainsKey("profile") &&
                !structured["personalProfile"]!.AsObject().ContainsKey("generation"), "Absent profile metadata added null fields.");
            Assert.Equal(1, structured["items"]!.AsArray().Count);
            Assert.True(Encoding.UTF8.GetByteCount(structured.ToJsonString()) <= 4096, "Profile metadata escaped the response budget.");
            Assert.Contains("personalProfile=not_loaded", Text(mixed));
            Assert.Contains("issue=personal_profile_not_loaded", Text(mixed));
            Assert.Contains($"effectiveNamespaces={Project}", Text(mixed));

            var before = store.RetrievalCalls;
            var empty = await Call(app, tool, RetrievalArguments(tool, format, [Personal]));
            var emptyStructured = Success(empty);
            Assert.Equal(0, emptyStructured[namespaceKey]!.AsArray().Count);
            Assert.Equal(0, emptyStructured["items"]!.AsArray().Count);
            Assert.Equal("not_loaded", emptyStructured["personalProfile"]!["state"]!.GetValue<string>());
            Assert.Contains("personalProfile=not_loaded", Text(empty));
            Assert.Equal(before, store.RetrievalCalls);
        }

        provider.Current = new("not_loaded", IssueCode: "profile_pipe_unavailable");
        var transient = await Call(app, "cortex_status", new { });
        var transientStructured = Success(transient)["personalProfile"]!;
        Assert.True(transientStructured["retryable"]!.GetValue<bool>(),
            "Missing profile pipe was not marked as retryable availability.");
        Assert.True(!transientStructured["indicatesMissingData"]!.GetValue<bool>(),
            "Missing profile pipe incorrectly indicated missing data.");
        Assert.True(!transientStructured["remapRequired"]!.GetValue<bool>(),
            "Missing profile pipe incorrectly requested a database remap.");
        Assert.Contains("personalProfile=temporarily_unavailable", Text(transient));
        Assert.Contains("dataMissing=false", Text(transient));
        Assert.Contains("remapRequired=false", Text(transient));
    }

    private static async Task ReadErrorsRecoveryAndStaleResultsAsync()
    {
        var store = new ReadStore();
        var provider = new ProfileProvider();
        var app = Mcp(store, provider);
        var arguments = new { @namespace = "user.preferences", recordType = "knowledge", recordId = "profile-record" };
        Error(await Call(app, "cortex_read", arguments), "personal_profile_not_loaded");
        Assert.Equal(0, store.ReadCalls);
        var batch = new
        {
            records = new[]
            {
                new { @namespace = Project, recordType = "knowledge", recordId = "core-record" },
                new { @namespace = "user.preferences", recordType = "knowledge", recordId = "profile-record" }
            }
        };
        Error(await Call(app, "cortex_read", batch), "personal_profile_not_loaded");
        Assert.Equal(0, store.ReadCalls);

        provider.Current = Loaded('a');
        var exact = await Call(app, "cortex_read", arguments);
        var structured = Success(exact);
        Assert.Equal(Personal, structured["effectiveNamespaces"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(BodyMarker, structured["record"]!["content"]!.GetValue<string>());
        Assert.True(!Text(exact).Contains(BodyMarker, StringComparison.Ordinal), "Exact body was duplicated into text.");
        Assert.Equal(1, store.ReadCalls);

        store.BeforeReadReturn = () => provider.Current = Loaded('b');
        Error(await Call(app, "cortex_read", arguments), "personal_profile_changed");
        Assert.Equal(2, store.ReadCalls);
        store.BeforeReadReturn = null;
        var recovered = Success(await Call(app, "cortex_read", arguments));
        Assert.Equal(BodyMarker, recovered["record"]!["content"]!.GetValue<string>());

        foreach (var format in new[] { "compact", "detailed" })
        {
            var result = Success(await Call(app, "cortex_search", RetrievalArguments("cortex_search", format, [Personal])));
            Assert.Equal("loaded", result["personalProfile"]!["state"]!.GetValue<string>());
            Assert.Equal(1, result["items"]!.AsArray().Count);
        }
    }

    private static async Task UnmanagedWireShapeIsUnchangedAsync()
    {
        var app = Mcp(new ReadStore(), null);
        foreach (var format in new[] { "compact", "detailed" })
        {
            foreach (var tool in new[] { "cortex_start", "cortex_search" })
            {
                var result = await Call(app, tool, RetrievalArguments(tool, format, [Personal]));
                Assert.True(!Success(result).ContainsKey("personalProfile"), "No-provider wire output gained an optional null field.");
                Assert.True(!Text(result).Contains("personalProfile", StringComparison.Ordinal), "No-provider summary changed.");
            }
        }
    }

    private static object RetrievalArguments(string tool, string format, string[] namespaces) => tool == "cortex_start"
        ? new { namespaces, intent = "profile context", format, maxUtf8Bytes = 4096 }
        : new { namespaces, text = "profile context", format, maxUtf8Bytes = 4096 };

    private static async Task CompactOptionalsDoNotAlterExactBodiesAsync()
    {
        var store = new ReadStore { CompactOptionalsFixture = true };
        var app = Mcp(store, null);
        foreach (var tool in new[] { "cortex_start", "cortex_search" })
        {
            var request = tool == "cortex_start"
                ? (object)new { namespaces = new[] { Project }, intent = "profile context", taskId = "current-task", format = "compact" }
                : RetrievalArguments(tool, "compact", [Project]);
            var compact = Success(await Call(app, tool, request));
            RequireNoNullObjectProperties(compact);
            Assert.True(compact.ContainsKey("ns") && compact.ContainsKey("items"), "Required compact arrays disappeared.");
            Assert.True(!compact["more"]!.GetValue<bool>(), "Meaningful false disappeared.");
            Assert.Equal(0d, compact["items"]![0]!["score"]!.GetValue<double>());
            Assert.Equal("", compact["items"]![0]!["summary"]!.GetValue<string>());
            Assert.True(!compact["items"]![0]!.AsObject().ContainsKey("kind") &&
                !compact["items"]![0]!.AsObject().ContainsKey("tier"), "Optional hit nulls remained.");
            if (tool == "cortex_start")
                Assert.True(!compact["task"]!.AsObject().ContainsKey("phase") &&
                    !compact["task"]!.AsObject().ContainsKey("subject"), "Optional empty task metadata remained.");
            else
                Assert.True(!compact.ContainsKey("next"), "Optional null pagination remained.");
        }
        var detailed = Success(await Call(app, "cortex_start", new
        {
            namespaces = new[] { Project }, intent = "profile context", taskId = "current-task", format = "detailed"
        }));
        Assert.Equal("", detailed["currentTask"]!["phaseId"]!.GetValue<string>());
        foreach (var body in new[] { "", "Unicode: 雪 😀\n\t" })
        {
            store.ExactBody = body;
            var exact = Success(await Call(app, "cortex_read", new
            {
                @namespace = Project, recordType = "knowledge", recordId = "profile-record"
            }));
            Assert.Equal(body, exact["record"]!["content"]!.GetValue<string>());
            var batch = Success(await Call(app, "cortex_read", new
            {
                records = new[] { new { @namespace = Project, recordType = "knowledge", recordId = "profile-record" } },
                format = "compact"
            }));
            Assert.Equal(body, batch["items"]![0]!["record"]!["content"]!.GetValue<string>());
        }
        var unloaded = Mcp(new ReadStore(), new ProfileProvider());
        var empty = Success(await Call(unloaded, "cortex_start", RetrievalArguments("cortex_start", "compact", [Personal])));
        RequireNoNullObjectProperties(empty);
        Assert.Equal(0, empty["ns"]!.AsArray().Count);
        Assert.Equal(0, empty["items"]!.AsArray().Count);
        Assert.True(!empty["more"]!.GetValue<bool>() && !empty.ContainsKey("task"), "Empty compact shape changed meaningful values.");
    }

    private static void RequireNoNullObjectProperties(JsonNode node)
    {
        if (node is JsonObject value)
            foreach (var property in value)
            {
                Assert.True(property.Value is not null, $"Compact optional null remained: {property.Key}");
                RequireNoNullObjectProperties(property.Value!);
            }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null)
                    RequireNoNullObjectProperties(child);
    }

    private static async Task<JsonObject> Call(CortexV2McpApplication app, string tool, object arguments) =>
        await app.CallToolAsync(tool, JsonSerializer.SerializeToElement(arguments), CancellationToken.None).ConfigureAwait(false);

    private static JsonObject Success(JsonObject result)
    {
        Assert.True(!result["isError"]!.GetValue<bool>(), $"Unexpected MCP error: {result.ToJsonString()}");
        return result["structuredContent"]!.AsObject();
    }

    private static void Error(JsonObject result, string code)
    {
        Assert.True(result["isError"]!.GetValue<bool>(), "Expected an explicit MCP error.");
        Assert.Equal(code, result["structuredContent"]!["error"]!["code"]!.GetValue<string>());
        Assert.Contains(code, Text(result));
        Assert.True(!result.ToJsonString().Contains(BodyMarker, StringComparison.Ordinal), "A rejected or stale read disclosed its body.");
        Assert.True(!result["structuredContent"]!.AsObject().ContainsKey("record"), "A profile error included a record.");
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();
    private static CortexPersonalProfileV2 Loaded(char generation) => new("loaded", "default", new string(generation, 32));
    private static CortexV2McpApplication Mcp(ReadStore store, ProfileProvider? provider) =>
        new(new CortexV2Application(store, "profile_test_actor", personalProfileProvider: provider));

    private sealed class ProfileProvider : ICortexPersonalProfileProvider
    {
        public CortexPersonalProfileV2 Current { get; set; } = new("not_loaded", IssueCode: "personal_profile_not_loaded");
        public ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);
    }

    // Only read paths are used here; Core owns the broader mutation-admission fixture.
    private sealed class ReadStore : ICortexV2Store, ICortexKnowledgeSpaceMapper
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");
        private static readonly CortexProvenanceReceiptV2 Receipt = new(CortexProvenanceSourceV2.Agent, "profile_test_actor", null, Now);
        public int RetrievalCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public Action? BeforeReadReturn { get; set; }
        public bool CompactOptionalsFixture { get; init; }
        public string ExactBody { get; set; } = BodyMarker;
        public string CanonicalizeNamespace(string value) => CortexKnowledgeSpaces.Canonicalize(value);
        public IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values) => CortexKnowledgeSpaces.Canonicalize(values);

        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CortexStatusV2("Pegasus Cortex", "2", "ready", "fake", false, false, 2, 2, 2, []));

        public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 request, CancellationToken cancellationToken)
        {
            RetrievalCalls++;
            if (CompactOptionalsFixture)
                return ValueTask.FromResult(new CortexStartResultV2(CortexContextDispositionV2.Snapshot,
                    request.Namespaces, "store-context",
                    [new(Project, "task", "other-task", 1, "Other task", "", null, null, null, 0)], false,
                    new(Project, request.TaskId!, 1, CortexTaskStatusV2.Open, "Current task", "", null, null, null, "", "")));
            return ValueTask.FromResult(new CortexStartResultV2(CortexContextDispositionV2.Snapshot,
                request.Namespaces, "store-context", request.Namespaces.Select(space => new CortexContextItemV2(
                    space, "knowledge", "profile-record", 1, "Profile context", "Bounded preview.",
                    CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit, Receipt, 1)).ToArray(), false));
        }

        public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 request, CancellationToken cancellationToken)
        {
            RetrievalCalls++;
            if (CompactOptionalsFixture)
                return ValueTask.FromResult(new CortexSearchResultV2(request.Namespaces,
                    [new(Project, CortexRecordTypeV2.Task, "other-task", 1, "Other task", "", null, null, Receipt, 0)], null, false));
            return ValueTask.FromResult(new CortexSearchResultV2(request.Namespaces,
                request.Namespaces.Select(space => new CortexSearchHitV2(space, CortexRecordTypeV2.Knowledge,
                    "profile-record", 1, "Profile context", "Bounded preview.", CortexKnowledgeKindV2.Preference,
                    CortexKnowledgeTierV2.Explicit, Receipt, 1)).ToArray(), null, false));
        }

        public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 request, CancellationToken cancellationToken)
        {
            ReadCalls++;
            var record = new CortexKnowledgeV2(request.Namespace, request.RecordId, request.Revision ?? 1,
                CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit,
                "Profile preference", ExactBody, [], new(CortexProvenanceSourceV2.Agent, "profile_test_actor", null, null, Now),
                null, Now, Now, null);
            var result = new CortexReadResultV2([request.Namespace], request.RecordType, request.RecordId, record.Revision, record);
            BeforeReadReturn?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(CortexNamespacesRequestV2 request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(CortexKnowledgeCommandV2 command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
