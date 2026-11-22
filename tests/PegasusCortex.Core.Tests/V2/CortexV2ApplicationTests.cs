using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;

public static class CortexV2ApplicationTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-13T12:00:00Z");

    public static async Task RunAsync()
    {
        await ExplicitNamespacesNeverBecomeStickyAsync().ConfigureAwait(false);
        await StartupIdentityAndProjectionAreBoundedAsync().ConfigureAwait(false);
        await RuntimeOwnsProvenanceAsync().ConfigureAwait(false);
        await FinishUsesOneAtomicStoreBoundaryAsync().ConfigureAwait(false);
        await AggregateResponseBudgetFailsClosedAsync().ConfigureAwait(false);
        await ContentRoundTripsExactlyAsync().ConfigureAwait(false);
        await WriteConcurrencyFieldsAreRequiredAsync().ConfigureAwait(false);
        TaskHistoryContractIsCompact();
        await NamespaceListingIsPagedAndBoundedAsync().ConfigureAwait(false);
        await ExactReadRequiresAndEchoesRecordTypeAsync().ConfigureAwait(false);
        await SearchHitTypesStayCoherentAsync().ConfigureAwait(false);
        MutationResultsStayCompact();
    }

    private static async Task StartupIdentityAndProjectionAreBoundedAsync()
    {
        var store = new FakeStore { IncludeCurrentTask = true };
        var app = Application(store);
        var result = await app.StartAsync(
            new CortexStartRequestV2(
                ["project-a", "shared"],
                "continue exact task",
                null,
                CortexV2Limits.DefaultStartResponseUtf8Bytes,
                "task-current"),
            CancellationToken.None).ConfigureAwait(false);

        Equal("task-current", store.LastStart!.TaskId);
        Equal(CortexV2Limits.DefaultStartResponseUtf8Bytes, store.LastStart.MaxUtf8Bytes);
        Equal("task-current", result.CurrentTask!.TaskId);

        store.DuplicateCurrentTaskInItems = true;
        await ThrowsAsync<InvalidDataException>(() => app.StartAsync(
            new CortexStartRequestV2(
                ["project-a"], "duplicate", null,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-current"),
            CancellationToken.None).AsTask()).ConfigureAwait(false);

        store.DuplicateCurrentTaskInItems = false;
        store.ExcessStartupItems = true;
        await ThrowsAsync<InvalidDataException>(() => app.StartAsync(
            new CortexStartRequestV2(["project-a"], "too many", null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);

        await ThrowsAsync<ArgumentOutOfRangeException>(() => app.StartAsync(
            new CortexStartRequestV2(
                ["project-a"], "too large", null,
                CortexV2Limits.MaximumStartResponseUtf8Bytes + 1),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static async Task ExplicitNamespacesNeverBecomeStickyAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        await app.StartAsync(
            new CortexStartRequestV2(["alpha", "shared"], "repair parser", null),
            CancellationToken.None).ConfigureAwait(false);
        await app.SearchAsync(
            new CortexSearchRequestV2(["beta"], "retry guidance", null, null),
            CancellationToken.None).ConfigureAwait(false);

        Equal("alpha,shared", string.Join(',', store.LastStart!.Namespaces));
        Equal("beta", string.Join(',', store.LastSearch!.Namespaces));

        store.OverrideEffectiveNamespaces = ["alpha"];
        await ThrowsAsync<InvalidDataException>(() => app.SearchAsync(
            new CortexSearchRequestV2(["beta"], "must not inherit", null, null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static async Task RuntimeOwnsProvenanceAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        var input = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.User,
            "request-17",
            "explicit correction");
        await app.ExecuteKnowledgeAsync(
            new CortexKnowledgeCommandV2(
                "project-a",
                CortexKnowledgeActionV2.Create,
                "correction-1",
                null,
                "idem-1",
                CortexKnowledgeKindV2.Correction,
                CortexKnowledgeTierV2.Explicit,
                "Preserve newlines",
                "Keep exact content.",
                ["parser"],
                input,
                null),
            CancellationToken.None).ConfigureAwait(false);

        var stamped = store.LastKnowledge?.EffectiveProvenance ??
            throw new InvalidOperationException("Core did not stamp provenance.");
        Equal("os_test_user", stamped.Actor);
        Equal(Now, stamped.CapturedAtUtc);
        Equal(CortexProvenanceSourceV2.User, stamped.Source);
        Equal(input.SourceId, stamped.SourceId);
        Equal(input.Evidence, stamped.Evidence);
    }

    private static async Task FinishUsesOneAtomicStoreBoundaryAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        var provenance = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.Agent,
            "build-4",
            "unit tests");
        var result = await app.FinishAsync(
            new CortexFinishRequestV2(
                "project-a",
                "task-1",
                3,
                CortexFinishTaskActionV2.Complete,
                null,
                null,
                null,
                "Parser fixed.",
                "Unit tests passed.",
                "Continue with packaging.",
                [new CortexFinishKnowledgeWriteV2(
                    "workaround-1",
                    null,
                    CortexKnowledgeKindV2.Workaround,
                    CortexKnowledgeTierV2.Verified,
                    "Quote the path",
                    "Use LiteralPath.",
                    ["powershell"],
                    provenance,
                    "finish-knowledge-1")],
                "finish-1"),
            CancellationToken.None).ConfigureAwait(false);

        Equal(1, store.FinishCalls);
        Equal(0, store.TaskCalls);
        Equal(0, store.KnowledgeCalls);
        Equal("project-a", result.EffectiveNamespaces.Single());
        Equal("os_test_user", store.LastFinish!.EffectiveProvenance!.Actor);
        var stamped = store.LastFinish!.Knowledge.Single().EffectiveProvenance ??
            throw new InvalidOperationException("Finish knowledge was not stamped.");
        Equal("os_test_user", stamped.Actor);
        Equal(Now, stamped.CapturedAtUtc);
    }

    private static async Task AggregateResponseBudgetFailsClosedAsync()
    {
        var store = new FakeStore { OversizedSearch = true };
        var app = Application(store);
        await ThrowsAsync<CortexV2ResponseBudgetExceededException>(() => app.SearchAsync(
            new CortexSearchRequestV2(["project-a"], "large", null, null, 10, 512),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static async Task ContentRoundTripsExactlyAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        const string content = "first\r\nsecond\n\tUnicode: 雪 🚀\u0001end";
        await app.ExecuteKnowledgeAsync(
            new CortexKnowledgeCommandV2(
                "project-a",
                CortexKnowledgeActionV2.Create,
                "observation-1",
                null,
                "idem-content",
                CortexKnowledgeKindV2.Observation,
                CortexKnowledgeTierV2.Inferred,
                "Exact content",
                content,
                [],
                new CortexProvenanceInputV2(CortexProvenanceSourceV2.Agent, null, null),
                null),
            CancellationToken.None).ConfigureAwait(false);
        Equal(content, store.LastKnowledge!.Content);
    }

    private static async Task WriteConcurrencyFieldsAreRequiredAsync()
    {
        var app = Application(new FakeStore());
        await ThrowsAsync<ArgumentException>(() => app.ExecuteTaskAsync(
            new CortexTaskCommandV2(
                "project-a", CortexTaskActionV2.Complete, "task-1",
                null, "idem", null, null, null, null, null, null, null,
                null, null, null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
        await ThrowsAsync<ArgumentException>(() => app.ExecuteKnowledgeAsync(
            new CortexKnowledgeCommandV2(
                "project-a", CortexKnowledgeActionV2.Archive, "record-1",
                1, null, null, null, null, null, null, null, null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
        await ThrowsAsync<ArgumentException>(() => app.ExecuteTaskAsync(
            new CortexTaskCommandV2(
                "project-a", CortexTaskActionV2.History, "task-1",
                null, null, null, null, null, null, null, null, null,
                1, null, null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static void TaskHistoryContractIsCompact()
    {
        var properties = typeof(CortexTaskHistoryEventV2).GetProperties()
            .Select(value => value.Name)
            .ToArray();
        Equal(false, properties.Contains("Content", StringComparer.Ordinal));
        Equal(false, properties.Contains("Dependencies", StringComparer.Ordinal));
        Equal(true, properties.Contains("ContentHash", StringComparer.Ordinal));
        Equal(true, properties.Contains("ContentChanged", StringComparer.Ordinal));
        Equal(
            typeof(CortexTaskHistoryEventV2),
            typeof(CortexTaskResultV2).GetProperty(nameof(CortexTaskResultV2.History))!
                .PropertyType.GenericTypeArguments[0]);
    }

    private static async Task NamespaceListingIsPagedAndBoundedAsync()
    {
        var store = new FakeStore { NamespaceHasMore = true };
        var result = await Application(store).ListNamespacesAsync(
            new CortexNamespacesRequestV2("prior", 1, 1024),
            CancellationToken.None).ConfigureAwait(false);
        Equal("prior", store.LastNamespaces!.AfterNamespace);
        Equal(1, result.Items.Count);
        Equal(true, result.HasMore);
        Equal("project-a", result.NextCursor);
        Equal(0, result.EffectiveNamespaces.Count);
    }

    private static async Task ExactReadRequiresAndEchoesRecordTypeAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        var result = await app.ReadAsync(
            new CortexReadRequestV2(
                "project-a", CortexRecordTypeV2.Task, "same-id", null, 1024),
            CancellationToken.None).ConfigureAwait(false);
        Equal(CortexRecordTypeV2.Task, result.RecordType);

        await ThrowsAsync<ArgumentOutOfRangeException>(() => app.ReadAsync(
            new CortexReadRequestV2(
                "project-a", (CortexRecordTypeV2)99, "same-id", null, 1024),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static async Task SearchHitTypesStayCoherentAsync()
    {
        var store = new FakeStore { IncludeTaskSearchHit = true };
        var app = Application(store);
        var unfiltered = await app.SearchAsync(
            new CortexSearchRequestV2(["project-a"], "same-id", null, null),
            CancellationToken.None).ConfigureAwait(false);
        Equal(CortexRecordTypeV2.Task, unfiltered.Items.Single().RecordType);

        await ThrowsAsync<InvalidDataException>(() => app.SearchAsync(
            new CortexSearchRequestV2(
                ["project-a"], "same-id", [CortexKnowledgeKindV2.Correction], null),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private static void MutationResultsStayCompact()
    {
        Equal(
            typeof(CortexTaskSummaryV2),
            typeof(CortexTaskResultV2).GetProperty(nameof(CortexTaskResultV2.Task))!
                .PropertyType);
        var knowledgeType = typeof(CortexFinishResultV2)
            .GetProperty(nameof(CortexFinishResultV2.Knowledge))!
            .PropertyType.GenericTypeArguments[0];
        Equal(typeof(CortexKnowledgeReceiptV2), knowledgeType);
        Equal(
            typeof(CortexKnowledgeReceiptV2),
            typeof(CortexKnowledgeResultV2).GetProperty(nameof(CortexKnowledgeResultV2.Record))!
                .PropertyType);
        var receiptProperties = knowledgeType.GetProperties()
            .Select(value => value.Name).ToArray();
        Equal(false, receiptProperties.Contains("Content", StringComparer.Ordinal));
        Equal(false, receiptProperties.Contains("Tags", StringComparer.Ordinal));
    }

    private static CortexV2Application Application(FakeStore store) =>
        new(store, "os_test_user", new FixedTimeProvider(Now));

    private static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
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
        public CortexStartRequestV2? LastStart { get; private set; }
        public CortexNamespacesRequestV2? LastNamespaces { get; private set; }
        public CortexSearchRequestV2? LastSearch { get; private set; }
        public CortexKnowledgeCommandV2? LastKnowledge { get; private set; }
        public CortexFinishRequestV2? LastFinish { get; private set; }
        public CortexTaskCommandV2? LastTask { get; private set; }
        public IReadOnlyList<string>? OverrideEffectiveNamespaces { get; set; }
        public bool OversizedSearch { get; set; }
        public int TaskCalls { get; private set; }
        public int KnowledgeCalls { get; private set; }
        public int FinishCalls { get; private set; }
        public bool NamespaceHasMore { get; set; }
        public bool IncludeTaskSearchHit { get; set; }
        public bool IncludeCurrentTask { get; set; }
        public bool DuplicateCurrentTaskInItems { get; set; }
        public bool ExcessStartupItems { get; set; }

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
                NamespaceHasMore ? "project-a" : null,
                NamespaceHasMore,
                []));
        }

        public ValueTask<CortexStartResultV2> StartAsync(
            CortexStartRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastStart = request;
            var currentTask = IncludeCurrentTask
                ? new CortexTaskSummaryV2(
                    "project-a", "task-current", 2, CortexTaskStatusV2.Open,
                    "Current task", "Continue", null, null, null)
                : null;
            var itemCount = ExcessStartupItems ? 5 : DuplicateCurrentTaskInItems ? 1 : 0;
            var items = Enumerable.Range(0, itemCount)
                .Select(index => new CortexContextItemV2(
                    "project-a",
                    "task",
                    DuplicateCurrentTaskInItems ? "task-current" : $"task-{index}",
                    DuplicateCurrentTaskInItems ? 2 : 1,
                    $"Task {index}",
                    "Excerpt",
                    null,
                    null,
                    ProvenanceReceipt(),
                    1 - index))
                .ToArray();
            return ValueTask.FromResult(new CortexStartResultV2(
                CortexContextDispositionV2.Snapshot,
                OverrideEffectiveNamespaces ?? request.Namespaces,
                "cursor-1",
                items,
                false,
                currentTask));
        }

        public ValueTask<CortexSearchResultV2> SearchAsync(
            CortexSearchRequestV2 request,
            CancellationToken cancellationToken)
        {
            LastSearch = request;
            IReadOnlyList<CortexSearchHitV2> items = OversizedSearch
                ? [new(
                    request.Namespaces[0], CortexRecordTypeV2.Knowledge,
                    "record-1", 1, "Large",
                    new string('x', 1024), CortexKnowledgeKindV2.Observation,
                    CortexKnowledgeTierV2.Inferred,
                    ProvenanceReceipt(), 1)]
                : IncludeTaskSearchHit
                    ? [new(
                        request.Namespaces[0], CortexRecordTypeV2.Task,
                        "same-id", 1, "Task", "task excerpt",
                        null, null, ProvenanceReceipt(), 1)]
                : [];
            return ValueTask.FromResult(new CortexSearchResultV2(
                OverrideEffectiveNamespaces ?? request.Namespaces,
                items,
                null,
                false));
        }

        public ValueTask<CortexReadResultV2> ReadAsync(
            CortexReadRequestV2 request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CortexReadResultV2(
                [request.Namespace], request.RecordType, request.RecordId,
                request.Revision ?? 1,
                request.RecordType == CortexRecordTypeV2.Task
                    ? new CortexTaskV2(
                        request.Namespace, request.RecordId, request.Revision ?? 1,
                        CortexTaskStatusV2.Open, "Task", "Content", null, null, [], null,
                        Now, Now, null, null, Provenance())
                    : new CortexKnowledgeV2(
                        request.Namespace, request.RecordId, request.Revision ?? 1,
                        CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Observation,
                        CortexKnowledgeTierV2.Inferred, "Knowledge", "Content", [],
                        Provenance(), null, Now, Now, null)));

        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
            CortexTaskCommandV2 command,
            CancellationToken cancellationToken)
        {
            TaskCalls++;
            LastTask = command;
            return ValueTask.FromResult(new CortexTaskResultV2(
                [command.Namespace], "accepted",
                command.TaskId is null ? null : new CortexTaskSummaryV2(
                    command.Namespace, command.TaskId, command.ExpectedRevision ?? 1,
                    CortexTaskStatusV2.Open, command.Title ?? "Task", "", null, null, null),
                [], null, null));
        }

        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
            CortexKnowledgeCommandV2 command,
            CancellationToken cancellationToken)
        {
            KnowledgeCalls++;
            LastKnowledge = command;
            return ValueTask.FromResult(new CortexKnowledgeResultV2(
                [command.Namespace], "accepted", new CortexKnowledgeReceiptV2(
                    command.Namespace, command.RecordId!, command.ExpectedRevision ?? 1,
                    CortexKnowledgeStateV2.Active,
                    command.Kind ?? CortexKnowledgeKindV2.Observation,
                    command.Tier ?? CortexKnowledgeTierV2.Inferred,
                    command.Title ?? "Knowledge",
                    new CortexProvenanceReceiptV2(
                        (command.EffectiveProvenance ?? Provenance()).Source,
                        (command.EffectiveProvenance ?? Provenance()).Actor,
                        (command.EffectiveProvenance ?? Provenance()).SourceId,
                        (command.EffectiveProvenance ?? Provenance()).CapturedAtUtc))));
        }

        public ValueTask<CortexFinishResultV2> FinishAsync(
            CortexFinishRequestV2 request,
            CancellationToken cancellationToken)
        {
            FinishCalls++;
            LastFinish = request;
            return ValueTask.FromResult(new CortexFinishResultV2(
                [request.Namespace],
                "accepted",
                new CortexTaskSummaryV2(
                    request.Namespace, request.TaskId, request.ExpectedRevision + 1,
                    CortexTaskStatusV2.Completed, "Task", request.Checkpoint,
                    null, null, null),
                request.Knowledge.Select(value => new CortexKnowledgeReceiptV2(
                    request.Namespace,
                    value.RecordId,
                    (value.ExpectedRevision ?? 0) + 1,
                    CortexKnowledgeStateV2.Active,
                    value.Kind,
                    value.Tier,
                    value.Title,
                    new CortexProvenanceReceiptV2(
                        value.EffectiveProvenance!.Source,
                        value.EffectiveProvenance.Actor,
                        value.EffectiveProvenance.SourceId,
                        value.EffectiveProvenance.CapturedAtUtc))).ToArray(),
                "generation-2"));
        }

        private static CortexProvenanceV2 Provenance() => new(
            CortexProvenanceSourceV2.Agent,
            "os_test_user",
            null,
            null,
            Now);

        private static CortexProvenanceReceiptV2 ProvenanceReceipt()
        {
            var value = Provenance();
            return new(value.Source, value.Actor, value.SourceId, value.CapturedAtUtc);
        }
    }
}
