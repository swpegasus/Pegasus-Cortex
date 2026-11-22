using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.V2;

public static class CortexSemanticAdmissionTests
{
    public static async Task RunAsync()
    {
        var store = new CapturedStore();
        var app = new CortexV2Application(store, "os_fixture");
        var prohibited = new[] { "-----BEGIN PRIVATE KEY-----", "Authorization: Bearer " + new string('x', 24),
            "password=\"fixture-secret\"", "credential=\"fixture-private-token\"", "ghp_" + new string('a', 24), "raw transcript:\nuser: fixture", "<analysis>fixture</analysis>", "terminal://fixture/stream" };
        foreach (var text in prohibited)
        {
            await RejectedAsync(text, () => app.ExecuteTaskAsync(TaskCommand("task", text), default).AsTask());
            await RejectedAsync(text, () => app.ExecuteKnowledgeAsync(Knowledge("note", "Safe") with
                { Provenance = new(CortexProvenanceSourceV2.User, "declared", text) }, default).AsTask());
            await RejectedAsync(text, () => app.FinishAsync(Finish(text), default).AsTask());
        }
        Require(store.TaskCalls + store.KnowledgeCalls + store.FinishCalls == 0, "prohibited content reached a store/idempotency boundary");
        var encoded = "{\"pass\\u0077ord\":\"fixture-secret\"}";
        await RejectedAsync(encoded, () => app.ExecuteTaskAsync(TaskCommand("encoded", encoded), default).AsTask());
        const string literal = "password=\"fixture-secret\"";
        foreach (var finish in new[] { Finish("Safe") with { TaskContent = literal }, Finish("Safe") with { TaskTitle = literal },
            Finish("Safe") with { BlockedReason = literal }, Finish("Safe") with { Checkpoint = literal },
            Finish("Safe") with { Evidence = literal }, Finish("Safe") with { Handoff = literal } })
            await RejectedAsync(literal, () => app.FinishAsync(finish, default).AsTask());
        var identifierToken = "ghp_" + new string('b', 24);
        await RejectedAsync(identifierToken, () => app.ExecuteTaskAsync(TaskCommand("metadata", "Safe") with
            { Assignee = identifierToken }, default).AsTask());
        await RejectedAsync(identifierToken, () => app.ExecuteKnowledgeAsync(Knowledge("metadata", "Safe") with
            { Tags = [identifierToken] }, default).AsTask());
        await RejectedAsync(identifierToken, () => app.ExecuteTaskAsync(TaskCommand("metadata", "Safe"), Source(1) with
            { Label = identifierToken }, default).AsTask());
        Require(store.TaskCalls == 0, "decoded JSON credential reached storage");
        Require(store.KnowledgeCalls + store.FinishCalls == 0, "finish or metadata rejection reached storage");

        foreach (var code in new[] { "var api_key = configuredValue;", "api_key = config.GetApiKey()", "api_key=$env:API_KEY",
            "api_key=\"YOUR_API_KEY\"", "api_key=[SECRET]", "api_key=\"[YOUR_API_KEY]\"", "api_key=${API_KEY}", "api_key=\"${API_KEY}\"", "api_key=\"\"",
            "api_key=configuration[\"ApiKey\"]", "```csharp\napi_key = input;\n```", "{\"api_key\":\"YOUR_API_KEY\"}",
            "Use C:\\Projects\\Example; compare the SHA256 before replacing a file." })
            await app.ExecuteKnowledgeAsync(Knowledge("safe", code), default);
        Require(store.KnowledgeCalls == 13, "legitimate code/reference examples were rejected");
        Require(!LearningFeedbackContentScreening.Screen("", "", "api_key=config.GetApiKey()", false).Accepted,
            "existing learning-feedback screening was relaxed");

        var source = Source(1);
        var command = TaskCommand("declared", "Factual note") with { Assignee = "someone-else", ClaimedBy = "reported-agent",
            EffectiveProvenance = new(CortexProvenanceSourceV2.System, "forged-actor", "forged-source", null, DateTimeOffset.UtcNow) };
        var result = await app.ExecuteTaskAsync(command, source, default);
        var stampedTask = store.LastTask ?? throw new InvalidOperationException("missing task write");
        Require(stampedTask.Assignee == "someone-else" && stampedTask.ClaimedBy == "reported-agent",
            "declared task labels were turned into caller identity restrictions");
        Require(stampedTask.EffectiveProvenance is { Actor: "os_fixture", Source: CortexProvenanceSourceV2.Agent } provenance &&
            provenance.VerifiedSource == source, "host attribution did not replace caller-supplied effective provenance");
        var taskReceipt = result.Task ?? throw new InvalidOperationException("missing task receipt");
        Require(taskReceipt.ParticipantIdentityKind == "declared-label" && taskReceipt.VerifiedSource == source,
            "task receipt did not distinguish declared participants from verified source");
        await app.ExecuteKnowledgeAsync(Knowledge("attributed", "Selected factual note"), source, default);
        Require(store.LastKnowledge!.EffectiveProvenance!.VerifiedSource == source, "knowledge omitted verified source");
        await app.FinishAsync(Finish("Reviewed factual lesson"), source, default);
        var stampedFinish = store.LastFinish ?? throw new InvalidOperationException("missing finish write");
        Require(stampedFinish.EffectiveProvenance!.VerifiedSource == source &&
            stampedFinish.Knowledge.All(item => item.EffectiveProvenance!.VerifiedSource == source), "finish batch source was not stamped consistently");
        await app.ExecuteTaskAsync(TaskCommand("local", "Local note"), default);
        Require(store.LastTask!.EffectiveProvenance!.VerifiedSource is null, "ordinary local call invented a verified connection");
        var before = store.TaskCalls;
        foreach (var invalid in new[] { source with { Kind = "reported-chat" }, source with { Authority = "caller-supplied" },
            source with { Subject = Guid.Empty.ToString("N") }, source with { Generation = 0 }, source with { Label = "invalid\nlabel" } })
        {
            try { await app.ExecuteTaskAsync(command, invalid, default); throw new InvalidOperationException("invalid source accepted"); }
            catch (ArgumentException) { }
        }
        Require(store.TaskCalls == before, "invalid source reached storage");
    }

    public static CortexInvocationSourceV2 Source(long generation) => new("approved-mcp-connection",
        "cortex.mcp/11111111111111111111111111111111", "22222222222222222222222222222222", generation, "Fixture client");

    public static CortexTaskCommandV2 TaskCommand(string id, string content) => new("project-a", CortexTaskActionV2.Create, id,
        null, "task-write", "Task", content, null, null, null, null, [], null, null, null);

    public static CortexKnowledgeCommandV2 Knowledge(string id, string content) => new("project-a", CortexKnowledgeActionV2.Create,
        id, null, "knowledge-write", CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Explicit, "Knowledge", content, [],
        new(CortexProvenanceSourceV2.User, "declared-reference", "Reviewed"), null);

    public static CortexFinishRequestV2 Finish(string finalContent) => new("project-a", "task", 1, CortexFinishTaskActionV2.Update,
        null, null, null, "Checkpoint", "Reviewed evidence", null, [new("first", null, CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Explicit,
            "First", "Safe", [], new(CortexProvenanceSourceV2.Agent, null, null), "first-write"),
            new("last", null, CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Explicit, "Last", finalContent, [],
                new(CortexProvenanceSourceV2.Agent, null, null), "last-write")], "finish-write");

    private static async Task RejectedAsync(string original, Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("prohibited content accepted"); }
        catch (CortexRetentionContentRejectedException error)
        {
            Require(error.FindingCodes.Count > 0 && !error.ToString().Contains(original, StringComparison.Ordinal), "screening echoed rejected content");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class CapturedStore : ICortexV2Store
    {
        public int TaskCalls, KnowledgeCalls, FinishCalls;
        public CortexTaskCommandV2? LastTask;
        public CortexKnowledgeCommandV2? LastKnowledge;
        public CortexFinishRequestV2? LastFinish;
        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 command, CancellationToken token)
        {
            TaskCalls++; LastTask = command;
            return ValueTask.FromResult(new CortexTaskResultV2([command.Namespace], "accepted", Summary(command.TaskId!, command.EffectiveProvenance), [], null, null));
        }
        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(CortexKnowledgeCommandV2 command, CancellationToken token)
        {
            KnowledgeCalls++; LastKnowledge = command;
            return ValueTask.FromResult(new CortexKnowledgeResultV2([command.Namespace], "accepted", Receipt(command.RecordId!, command.EffectiveProvenance!)));
        }
        public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 request, CancellationToken token)
        {
            FinishCalls++; LastFinish = request;
            return ValueTask.FromResult(new CortexFinishResultV2([request.Namespace], "accepted", Summary(request.TaskId, request.EffectiveProvenance),
                request.Knowledge.Select(item => Receipt(item.RecordId, item.EffectiveProvenance!)).ToArray(), "cursor"));
        }
        private static CortexTaskSummaryV2 Summary(string id, CortexProvenanceV2? source) => new("project-a", id, 2,
            CortexTaskStatusV2.Open, "Task", "Excerpt", null, null, null, VerifiedSource: source?.VerifiedSource);
        private static CortexKnowledgeReceiptV2 Receipt(string id, CortexProvenanceV2 source) => new("project-a", id, 1,
            CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Explicit, "Knowledge",
            new(source.Source, source.Actor, source.SourceId, source.CapturedAtUtc, source.VerifiedSource));
        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(CortexNamespacesRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 request, CancellationToken token) => throw new NotSupportedException();
    }
}
