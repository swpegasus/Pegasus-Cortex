using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;

internal static class CortexPersonalProfileTests
{
    private const string Project = "project.profile-tests";
    private const string Personal = "knowledge.user";
    private const string PersonalAlias = "user.preferences";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");

    public static async Task RunAsync()
    {
        Func<Task>[] tests =
        [
            MissingProviderPreservesLegacyBehaviorAsync,
            CoreOnlyOperationsDoNotConsultProfileAsync,
            UnloadedPersonalOperationsFailBeforeStoreAccessAsync,
            MixedRetrievalIsTruthfulAndBoundedAsync,
            SameApplicationRecoversAfterLoadAsync,
            CursorsArePortableAndProfileBoundAsync,
            InFlightReadsSuppressChangedProfileResultsAsync,
            AddedProfileMetadataCannotEscapeResponseBudgetAsync,
            UnloadedResponsesFitMinimumBudgetAsync,
            InvalidOrFailedProfileDegradesIndependentlyAsync,
            AdmittedWritesRetainSuccessfulReceiptsAsync,
            FilteredCoreRetrievalSurvivesPersonalRecoveryAsync
        ];
        foreach (var test in tests)
        {
            try
            {
                await test().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"{test.Method.Name}: {exception.Message}", exception);
            }
        }
    }

    private static async Task MissingProviderPreservesLegacyBehaviorAsync()
    {
        var store = new FakeStore();
        var app = Application(store);
        var start = await app.StartAsync(Start([PersonalAlias]), CancellationToken.None);
        Assert.Equal<string?>(null, start.PersonalProfile?.State);
        Assert.Equal(FakeStore.StartCursor, start.ContextCursor);
        Assert.Equal(Personal, start.EffectiveNamespaces.Single());
        var search = await app.SearchAsync(Search([PersonalAlias]), CancellationToken.None);
        Assert.Equal<string?>(null, search.PersonalProfile?.State);
        Assert.Equal(FakeStore.SearchCursor, search.NextCursor);
        var status = await app.StatusAsync(CancellationToken.None);
        Assert.Equal<string?>(null, status.PersonalProfile?.State);
        await app.ReadAsync(Read(PersonalAlias), CancellationToken.None);
        await app.ExecuteTaskAsync(TaskCommand(PersonalAlias, CortexTaskActionV2.Create), CancellationToken.None);
        await app.ExecuteKnowledgeAsync(KnowledgeCommand(PersonalAlias, CortexKnowledgeActionV2.Create), CancellationToken.None);
        await app.FinishAsync(Finish(PersonalAlias), CancellationToken.None);
        Assert.Equal(1, store.ReadCalls);
        Assert.Equal(1, store.TaskCalls);
        Assert.Equal(1, store.KnowledgeCalls);
        Assert.Equal(1, store.FinishCalls);
    }

    private static async Task CoreOnlyOperationsDoNotConsultProfileAsync()
    {
        var store = new FakeStore();
        var provider = new MutableProfileProvider { ThrowOnGet = true };
        var app = Application(store, provider);
        var start = await app.StartAsync(Start([Project, "shared.programming"]), CancellationToken.None);
        var search = await app.SearchAsync(Search([Project, "knowledge.shared"]), CancellationToken.None);
        Assert.Equal<string?>(null, start.PersonalProfile?.State);
        Assert.Equal<string?>(null, search.PersonalProfile?.State);
        Assert.Equal(FakeStore.StartCursor, start.ContextCursor);
        Assert.Equal(FakeStore.SearchCursor, search.NextCursor);
        await app.ListNamespacesAsync(new(null), CancellationToken.None);
        await app.ReadAsync(Read(Project), CancellationToken.None);
        await app.ReadBatchAsync(Batch(Project, "knowledge.shared"), CancellationToken.None);
        await app.ExecuteTaskAsync(TaskCommand(Project, CortexTaskActionV2.Get), CancellationToken.None);
        await app.ExecuteTaskAsync(TaskCommand(Project, CortexTaskActionV2.Create), CancellationToken.None);
        await app.ExecuteKnowledgeAsync(KnowledgeCommand("knowledge.shared", CortexKnowledgeActionV2.Read), CancellationToken.None);
        await app.ExecuteKnowledgeAsync(KnowledgeCommand("knowledge.shared", CortexKnowledgeActionV2.Create), CancellationToken.None);
        await app.FinishAsync(Finish(Project), CancellationToken.None);
        Assert.Equal(0, provider.Calls);

        provider.ThrowOnGet = false;
        var status = await app.StatusAsync(CancellationToken.None);
        Assert.Equal("ready", status.State);
        Assert.Equal("not_loaded", status.PersonalProfile!.State);
        Assert.Equal(1, provider.Calls);
    }

    private static async Task UnloadedPersonalOperationsFailBeforeStoreAccessAsync()
    {
        foreach (var space in new[] { Personal, PersonalAlias })
        {
            var store = new FakeStore();
            var app = Application(store, new MutableProfileProvider());
            await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                app.ReadAsync(Read(space), CancellationToken.None).AsTask());
            foreach (var action in new[] { CortexTaskActionV2.Get, CortexTaskActionV2.Create })
                await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                    app.ExecuteTaskAsync(TaskCommand(space, action), CancellationToken.None).AsTask());
            foreach (var action in new[]
                     { CortexKnowledgeActionV2.Read, CortexKnowledgeActionV2.Analyze, CortexKnowledgeActionV2.Create })
                await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                    app.ExecuteKnowledgeAsync(KnowledgeCommand(space, action), CancellationToken.None).AsTask());
            await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                app.FinishAsync(Finish(space), CancellationToken.None).AsTask());

            // Put the core coordinate first: denial must happen before even that body is hydrated.
            await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                app.ReadBatchAsync(Batch(Project, space), CancellationToken.None).AsTask());
            Assert.Equal(0, store.ReadCalls);
            Assert.Equal(0, store.TaskCalls);
            Assert.Equal(0, store.KnowledgeCalls);
            Assert.Equal(0, store.FinishCalls);
        }
    }

    private static async Task MixedRetrievalIsTruthfulAndBoundedAsync()
    {
        var store = new FakeStore();
        var provider = new MutableProfileProvider();
        var app = Application(store, provider);
        string[] mixed = [PersonalAlias, Project, "shared.programming", Personal];
        var start = await app.StartAsync(Start(mixed), CancellationToken.None);
        var search = await app.SearchAsync(Search(mixed), CancellationToken.None);
        Assert.Equal($"{Project},knowledge.shared", string.Join(',', store.LastStart!.Namespaces));
        Assert.Equal($"{Project},knowledge.shared", string.Join(',', store.LastSearch!.Namespaces));
        Assert.Equal($"{Project},knowledge.shared", string.Join(',', start.EffectiveNamespaces));
        Assert.Equal($"{Project},knowledge.shared", string.Join(',', search.EffectiveNamespaces));
        Assert.True(start.Items.All(value => value.Namespace != Personal));
        Assert.True(search.Items.All(value => value.Namespace != Personal));
        Assert.Equal("not_loaded", start.PersonalProfile!.State);
        Assert.Equal("not_loaded", search.PersonalProfile!.State);
        RequireBudget(start, 4096);
        RequireBudget(search, 4096);

        var startCalls = store.StartCalls;
        var searchCalls = store.SearchCalls;
        var emptyStart = await app.StartAsync(Start([PersonalAlias, Personal]), CancellationToken.None);
        var emptySearch = await app.SearchAsync(Search([Personal]), CancellationToken.None);
        Assert.Equal(0, emptyStart.EffectiveNamespaces.Count);
        Assert.Equal(0, emptySearch.EffectiveNamespaces.Count);
        Assert.Equal(0, emptyStart.Items.Count);
        Assert.Equal(0, emptySearch.Items.Count);
        Assert.True(emptyStart.CurrentTask is null);
        Assert.False(emptyStart.HasMore);
        Assert.False(emptySearch.HasMore);
        Assert.True(emptySearch.NextCursor is null);
        Assert.Equal("not_loaded", emptyStart.PersonalProfile!.State);
        Assert.Equal("not_loaded", emptySearch.PersonalProfile!.State);
        Assert.Equal(startCalls, store.StartCalls);
        Assert.Equal(searchCalls, store.SearchCalls);
        RequireBudget(emptyStart, 4096);
        RequireBudget(emptySearch, 4096);
    }

    private static async Task SameApplicationRecoversAfterLoadAsync()
    {
        var store = new FakeStore();
        var provider = new MutableProfileProvider();
        var app = Application(store, provider);
        var unloaded = await app.StartAsync(Start([Personal]), CancellationToken.None);
        Assert.Equal(0, store.StartCalls);
        provider.Current = Loaded('a');
        var loaded = await app.StartAsync(Start([Personal], unloaded.ContextCursor), CancellationToken.None);
        Assert.Equal("loaded", loaded.PersonalProfile!.State);
        Assert.Equal(Personal, loaded.EffectiveNamespaces.Single());
        Assert.Equal(1, loaded.Items.Count);
        Assert.True(store.LastStart!.KnownContextCursor is null);
        await app.ReadAsync(Read(PersonalAlias), CancellationToken.None);
        await app.ReadBatchAsync(Batch(Project, PersonalAlias), CancellationToken.None);
        await app.ExecuteTaskAsync(TaskCommand(PersonalAlias, CortexTaskActionV2.Create), CancellationToken.None);
        await app.ExecuteKnowledgeAsync(KnowledgeCommand(PersonalAlias, CortexKnowledgeActionV2.Create), CancellationToken.None);
        await app.FinishAsync(Finish(PersonalAlias), CancellationToken.None);
        var search = await app.SearchAsync(Search([Personal]), CancellationToken.None);
        Assert.Equal("loaded", search.PersonalProfile!.State);
        Assert.Equal(Personal, search.Items.Single().Namespace);
        Assert.Equal(3, store.ReadCalls);
        Assert.Equal(1, store.TaskCalls);
        Assert.Equal(1, store.KnowledgeCalls);
        Assert.Equal(1, store.FinishCalls);
    }

    private static async Task CursorsArePortableAndProfileBoundAsync()
    {
        var provider = new MutableProfileProvider { Current = Loaded('a') };
        var firstStore = new FakeStore();
        var first = Application(firstStore, provider);
        string[] scope = [Project, Personal];
        var start = await first.StartAsync(Start(scope), CancellationToken.None);
        var search = await first.SearchAsync(Search(scope), CancellationToken.None);
        Assert.False(start.ContextCursor == FakeStore.StartCursor);
        Assert.False(search.NextCursor == FakeStore.SearchCursor);

        // A fresh application instance can decode the cursor without in-memory registration.
        var secondStore = new FakeStore();
        var second = Application(secondStore, provider);
        var repeated = await second.StartAsync(Start(scope, start.ContextCursor), CancellationToken.None);
        await second.SearchAsync(Search(scope, search.NextCursor), CancellationToken.None);
        Assert.Equal(FakeStore.StartCursor, secondStore.LastStart!.KnownContextCursor);
        Assert.Equal(FakeStore.SearchCursor, secondStore.LastSearch!.AfterCursor);
        Assert.Equal(start.ContextCursor, repeated.ContextCursor);

        provider.Current = Loaded('b');
        await second.StartAsync(Start(scope, start.ContextCursor), CancellationToken.None);
        Assert.True(secondStore.LastStart!.KnownContextCursor is null);
        var calls = secondStore.SearchCalls;
        await ThrowsProfileAsync("personal_profile_changed", () =>
            second.SearchAsync(Search(scope, search.NextCursor), CancellationToken.None).AsTask());
        Assert.Equal(calls, secondStore.SearchCalls);

        // Returning to the original generation does not allow the cursor in another query scope.
        provider.Current = Loaded('a');
        await ThrowsProfileAsync("personal_profile_changed", () =>
            second.SearchAsync(Search([Personal], search.NextCursor), CancellationToken.None).AsTask());
        Assert.Equal(calls, secondStore.SearchCalls);

        provider.Current = Unloaded();
        await second.StartAsync(Start(scope, start.ContextCursor), CancellationToken.None);
        Assert.True(secondStore.LastStart!.KnownContextCursor is null);
        await ThrowsProfileAsync("personal_profile_changed", () =>
            second.SearchAsync(Search(scope, search.NextCursor), CancellationToken.None).AsTask());
        Assert.Equal(calls, secondStore.SearchCalls);
    }

    private static async Task InFlightReadsSuppressChangedProfileResultsAsync()
    {
        Func<CortexV2Application, Task>[] operations =
        [
            app => app.StartAsync(Start([Project, Personal]), CancellationToken.None).AsTask(),
            app => app.SearchAsync(Search([Project, Personal]), CancellationToken.None).AsTask(),
            app => app.ReadAsync(Read(Personal), CancellationToken.None).AsTask(),
            app => app.ReadBatchAsync(Batch(Project, Personal), CancellationToken.None).AsTask(),
            app => app.ExecuteTaskAsync(TaskCommand(Personal, CortexTaskActionV2.Get), CancellationToken.None).AsTask(),
            app => app.ExecuteTaskAsync(TaskCommand(Personal, CortexTaskActionV2.List), CancellationToken.None).AsTask(),
            app => app.ExecuteTaskAsync(TaskCommand(Personal, CortexTaskActionV2.History), CancellationToken.None).AsTask(),
            app => app.ExecuteKnowledgeAsync(KnowledgeCommand(Personal, CortexKnowledgeActionV2.Read), CancellationToken.None).AsTask(),
            app => app.ExecuteKnowledgeAsync(KnowledgeCommand(Personal, CortexKnowledgeActionV2.Analyze), CancellationToken.None).AsTask()
        ];
        foreach (var replacement in new[] { Loaded('b'), Unloaded() })
        {
            foreach (var operation in operations)
            {
                var provider = new MutableProfileProvider { Current = Loaded('a') };
                var store = new FakeStore { OnOperation = () => provider.Current = replacement };
                await ThrowsProfileAsync("personal_profile_changed", () => operation(Application(store, provider)));
                Assert.True(store.TotalCalls > 0);
            }
        }
    }

    private static async Task AddedProfileMetadataCannotEscapeResponseBudgetAsync()
    {
        var store = new FakeStore { Excerpt = new string('x', 300), HasMore = false };
        var legacy = await Application(store).SearchAsync(Search([Personal]), CancellationToken.None);
        var budget = JsonSerializer.SerializeToUtf8Bytes(legacy, CortexV2Json.Options).Length;
        Assert.True(budget >= CortexV2Limits.MinimumResponseUtf8Bytes);
        Assert.True(budget < CortexV2Limits.DefaultResponseUtf8Bytes);
        var provider = new MutableProfileProvider { Current = Loaded('a') };
        await ThrowsAsync<CortexV2ResponseBudgetExceededException>(() =>
            Application(store, provider).SearchAsync(
                Search([Personal]) with { MaxUtf8Bytes = budget }, CancellationToken.None).AsTask());
    }

    private static async Task UnloadedResponsesFitMinimumBudgetAsync()
    {
        var store = new FakeStore();
        var provider = new MutableProfileProvider
        {
            Current = new("not_loaded", Generation: new string('a', 128), IssueCode: new string('i', 64))
        };
        var app = Application(store, provider);
        var start = await app.StartAsync(Start([Personal]) with { MaxUtf8Bytes = 512 }, CancellationToken.None);
        var search = await app.SearchAsync(Search([Personal]) with { MaxUtf8Bytes = 512 }, CancellationToken.None);
        RequireBudget(start, 512);
        RequireBudget(search, 512);
        Assert.Equal("not_loaded", start.PersonalProfile!.State);
        Assert.Equal("not_loaded", search.PersonalProfile!.State);
        Assert.Equal(0, store.TotalCalls);
    }

    private static async Task InvalidOrFailedProfileDegradesIndependentlyAsync()
    {
        CortexPersonalProfileV2[] invalid =
        [
            new("unknown"),
            new("loaded"),
            Loaded('a') with { Profile = new string('p', 129) },
            Loaded('a') with { Generation = new string('a', 129) },
            Loaded('a') with { Generation = "contains space" },
            Loaded('a') with { IssueCode = new string('i', 65) }
        ];
        foreach (var snapshot in invalid)
        {
            var store = new FakeStore();
            var app = Application(store, new MutableProfileProvider { Current = snapshot });
            var status = await app.StatusAsync(CancellationToken.None);
            Assert.Equal("ready", status.State);
            Assert.Equal("not_loaded", status.PersonalProfile!.State);
            Assert.Equal("personal_profile_invalid", status.PersonalProfile.IssueCode);
            await ThrowsProfileAsync("personal_profile_not_loaded", () =>
                app.ReadAsync(Read(Personal), CancellationToken.None).AsTask());
            Assert.Equal(0, store.ReadCalls);
        }

        var failedStore = new FakeStore();
        var failedApp = Application(failedStore, new MutableProfileProvider { ThrowOnGet = true });
        var failedStatus = await failedApp.StatusAsync(CancellationToken.None);
        Assert.Equal("ready", failedStatus.State);
        Assert.Equal("not_loaded", failedStatus.PersonalProfile!.State);
        Assert.Equal("personal_profile_unavailable", failedStatus.PersonalProfile.IssueCode);
        var core = await failedApp.SearchAsync(Search([Project]), CancellationToken.None);
        Assert.Equal(Project, core.EffectiveNamespaces.Single());
    }

    private static async Task AdmittedWritesRetainSuccessfulReceiptsAsync()
    {
        Func<CortexV2Application, Task>[] writes =
        [
            async app => Assert.Equal("accepted", (await app.ExecuteTaskAsync(
                TaskCommand(Personal, CortexTaskActionV2.Create), CancellationToken.None)).Disposition),
            async app => Assert.Equal("accepted", (await app.ExecuteKnowledgeAsync(
                KnowledgeCommand(Personal, CortexKnowledgeActionV2.Create), CancellationToken.None)).Disposition),
            async app => Assert.Equal("accepted", (await app.FinishAsync(Finish(Personal), CancellationToken.None)).Disposition)
        ];
        foreach (var write in writes)
        {
            var provider = new MutableProfileProvider { Current = Loaded('a') };
            var store = new FakeStore { OnOperation = () => provider.Current = Unloaded() };
            await write(Application(store, provider));
            Assert.Equal(1, provider.Calls);
            Assert.Equal(1, store.TotalCalls);
            Assert.Equal("not_loaded", provider.Current.State);
        }
    }

    private static async Task FilteredCoreRetrievalSurvivesPersonalRecoveryAsync()
    {
        var provider = new MutableProfileProvider();
        var store = new FakeStore { OnOperation = () => provider.Current = Loaded('a') };
        var app = Application(store, provider);
        var start = await app.StartAsync(Start([Project, Personal]), CancellationToken.None);
        Assert.Equal(Project, start.EffectiveNamespaces.Single());
        Assert.Equal(Project, start.Items.Single().Namespace);
        Assert.Equal("not_loaded", start.PersonalProfile!.State);
        provider.Current = Unloaded();
        var search = await app.SearchAsync(Search([Project, Personal]), CancellationToken.None);
        Assert.Equal(Project, search.EffectiveNamespaces.Single());
        Assert.Equal(Project, search.Items.Single().Namespace);
        Assert.Equal("not_loaded", search.PersonalProfile!.State);
    }

    private static CortexV2Application Application(FakeStore store, MutableProfileProvider? provider = null) =>
        new(store, "profile_test_actor", new FixedTimeProvider(Now), personalProfileProvider: provider);

    private static CortexPersonalProfileV2 Loaded(char generation) =>
        new("loaded", "default", new string(generation, 32));

    private static CortexPersonalProfileV2 Unloaded() =>
        new("not_loaded", IssueCode: "personal_profile_not_loaded");

    private static CortexStartRequestV2 Start(IReadOnlyList<string> spaces, string? cursor = null) =>
        new(spaces, "bounded profile context", cursor, 4096);

    private static CortexSearchRequestV2 Search(IReadOnlyList<string> spaces, string? cursor = null) =>
        new(spaces, "profile context", null, cursor, MaxUtf8Bytes: 4096);

    private static CortexReadRequestV2 Read(string space) =>
        new(space, CortexRecordTypeV2.Knowledge, "profile-record", 1);

    private static CortexReadBatchRequestV2 Batch(string first, string second) => new(
        [new(first, CortexRecordTypeV2.Knowledge, "first-record", 1),
         new(second, CortexRecordTypeV2.Knowledge, "second-record", 1)]);

    private static CortexTaskCommandV2 TaskCommand(string space, CortexTaskActionV2 action) => new(
        space, action, action == CortexTaskActionV2.List ? null : "profile-task",
        null, action == CortexTaskActionV2.Create ? "task-write" : null,
        action == CortexTaskActionV2.Create ? "Profile task" : null,
        action == CortexTaskActionV2.Create ? "Bounded task content." : null,
        null, null, null, null, null,
        action == CortexTaskActionV2.History ? 1 : null,
        action == CortexTaskActionV2.History ? 1 : null, null);

    private static CortexKnowledgeCommandV2 KnowledgeCommand(string space, CortexKnowledgeActionV2 action) => new(
        space, action, "profile-record", null,
        action == CortexKnowledgeActionV2.Create ? "knowledge-write" : null,
        CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit,
        "Profile preference", "Bounded personal content.", [],
        action == CortexKnowledgeActionV2.Create
            ? new(CortexProvenanceSourceV2.User, "profile-test", "Deterministic fixture.") : null,
        null);

    private static CortexFinishRequestV2 Finish(string space) => new(
        space, "profile-task", 1, CortexFinishTaskActionV2.Complete,
        null, null, null, "Bounded checkpoint.", "Deterministic fixture.", null,
        [new("profile-record", null, CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit,
            "Profile preference", "Bounded personal content.", [],
            new(CortexProvenanceSourceV2.User, "profile-test", "Deterministic fixture."), "finish-knowledge")],
        "finish-write");

    private static async Task ThrowsProfileAsync(string code, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (CortexPersonalProfileException exception)
        {
            Assert.Equal(code, exception.Code);
            return;
        }
        throw new InvalidOperationException($"Expected profile diagnostic '{code}'.");
    }

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

    private static void RequireBudget<T>(T result, int budget) =>
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, CortexV2Json.Options).Length <= budget);

    private sealed class MutableProfileProvider : ICortexPersonalProfileProvider
    {
        public CortexPersonalProfileV2 Current { get; set; } = Unloaded();
        public int Calls { get; private set; }
        public bool ThrowOnGet { get; set; }

        public ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (ThrowOnGet)
                throw new InvalidOperationException("Core-only operation consulted the optional profile provider.");
            return ValueTask.FromResult(Current);
        }
    }

    private sealed class FakeStore : ICortexV2Store, ICortexKnowledgeSpaceMapper
    {
        public const string StartCursor = "store-start";
        public const string SearchCursor = "store-search";
        public Action? OnOperation { get; init; }
        public string Excerpt { get; init; } = "Bounded context.";
        public bool HasMore { get; init; } = true;
        public int StartCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int TaskCalls { get; private set; }
        public int KnowledgeCalls { get; private set; }
        public int FinishCalls { get; private set; }
        public int TotalCalls => StartCalls + SearchCalls + ReadCalls + TaskCalls + KnowledgeCalls + FinishCalls;
        public CortexStartRequestV2? LastStart { get; private set; }
        public CortexSearchRequestV2? LastSearch { get; private set; }

        public string CanonicalizeNamespace(string value) => CortexKnowledgeSpaces.Canonicalize(value);
        public IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values) =>
            CortexKnowledgeSpaces.Canonicalize(values);

        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CortexStatusV2("Pegasus Cortex", "2", "ready", "fake", false, false, 3, 3, 3, []));

        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
            CortexNamespacesRequestV2 request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CortexNamespacesResultV2([], null, false, []));

        public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 request, CancellationToken cancellationToken)
        {
            StartCalls++;
            LastStart = request;
            var result = new CortexStartResultV2(CortexContextDispositionV2.Snapshot, request.Namespaces, StartCursor,
                request.Namespaces.Select(space => new CortexContextItemV2(
                    space, "knowledge", "profile-record", 1, "Profile context", Excerpt,
                    CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit, Receipt(), 1)).ToArray(), false);
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 request, CancellationToken cancellationToken)
        {
            SearchCalls++;
            LastSearch = request;
            var result = new CortexSearchResultV2(request.Namespaces,
                request.Namespaces.Select(space => new CortexSearchHitV2(
                    space, CortexRecordTypeV2.Knowledge, "profile-record", 1, "Profile context", Excerpt,
                    CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit, Receipt(), 1)).ToArray(),
                HasMore ? SearchCursor : null, HasMore);
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 request, CancellationToken cancellationToken)
        {
            ReadCalls++;
            var record = new CortexKnowledgeV2(request.Namespace, request.RecordId, request.Revision ?? 1,
                CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit,
                "Profile preference", "Exact personal fixture content.", [], Provenance(), null, Now, Now, null);
            var result = new CortexReadResultV2([request.Namespace], request.RecordType, request.RecordId,
                request.Revision ?? 1, record);
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 command, CancellationToken cancellationToken)
        {
            TaskCalls++;
            var result = new CortexTaskResultV2([command.Namespace], "accepted",
                command.TaskId is null ? null : TaskSummary(command.Namespace, command.TaskId), [], [], null);
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
            CortexKnowledgeCommandV2 command, CancellationToken cancellationToken)
        {
            KnowledgeCalls++;
            var result = new CortexKnowledgeResultV2([command.Namespace], "accepted",
                KnowledgeReceipt(command.Namespace, command.RecordId!));
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 request, CancellationToken cancellationToken)
        {
            FinishCalls++;
            var result = new CortexFinishResultV2([request.Namespace], "accepted",
                TaskSummary(request.Namespace, request.TaskId),
                request.Knowledge.Select(value => KnowledgeReceipt(request.Namespace, value.RecordId)).ToArray(), "finish-context");
            OnOperation?.Invoke();
            return ValueTask.FromResult(result);
        }

        private static CortexTaskSummaryV2 TaskSummary(string space, string id) =>
            new(space, id, 1, CortexTaskStatusV2.Open, "Profile task", "Task excerpt.", null, null, null);

        private static CortexKnowledgeReceiptV2 KnowledgeReceipt(string space, string id) =>
            new(space, id, 1, CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Preference,
                CortexKnowledgeTierV2.Explicit, "Profile preference", Receipt());

        private static CortexProvenanceV2 Provenance() =>
            new(CortexProvenanceSourceV2.Agent, "profile_test_actor", null, "Deterministic fixture.", Now);

        private static CortexProvenanceReceiptV2 Receipt() =>
            new(CortexProvenanceSourceV2.Agent, "profile_test_actor", null, Now);
    }
}
