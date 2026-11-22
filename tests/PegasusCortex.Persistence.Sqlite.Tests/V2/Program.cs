using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

if(args is ["--semantic-admission-only"])
{
    await SqliteSemanticAdmissionTests.RunAsync();
    Console.WriteLine("PASS SQLite semantic admission and attribution integration.");
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("v2 semantic admission precedes initialization and idempotency", SqliteSemanticAdmissionTests.RunAsync),
    ("fresh v2 preserves exact UTF-8 and never touches v1", ExactRoundTripAndGenerationIsolationAsync),
    ("v2 head search is namespace-local Unicode-safe and minimum-match aware", UnicodeSearchAndChunksAsync),
    ("v2 atomic batch rolls every staged write back on stale input", AtomicBatchRollbackAsync),
    ("v2 history reuses one blob and never enlarges head FTS", HistoryDoesNotGrowHeadIndexAsync),
    ("v2 quick startup check remains corpus-independent", QuickCheckDoesNotScanCorpusAsync),
    ("v2 operational adapter preserves typed metadata, provenance, controls, and compact history", OperationalRoundTripAsync),
    ("v2 knowledge spaces compose geometrically and migrate without touching projects", KnowledgeSpacesAndGeometryAsync),
    ("v2 startup is exact-task aware, intent ranked, cursor bound, and scale bounded", OperationalTaskAwareStartAsync),
    ("v2 batched exact reads, metrics, phases, and consolidation stay bounded", OperationalEfficiencyExtensionsAsync),
    ("v2 operational finish is atomic and retries idempotently", OperationalFinishAsync),
    ("v2 namespace catalog pagination is bounded and stateless", OperationalNamespacePaginationAsync),
    ("v2 operational lifecycles retain exact history and hide archived heads", OperationalLifecyclesAsync),
    ("v2 start and search greedily honor compact response budgets", OperationalQueryBudgetsAsync),
    ("v2 persists contract-maximum control-heavy content exactly", MaximumControlContentAsync),
    ("v2 compact receipts permit maximum task and sixteen-record finish", MaximumCompactReceiptsAsync),
    ("v2 backup and create-only restore round-trip exact retained state", BackupRestoreRoundTripAsync),
    ("v2 backup never initializes an absent source store", BackupRequiresExistingSourceAsync),
    ("v2 restore rejects corrupt, live, locked, and existing destinations", BackupRestoreFailuresAsync),
    ("v2 rejects oversized compact mutation receipt before commit", MutationReceiptPreflightAsync),
    ("v2 reapplies the default page ceiling to a pre-existing store", ExistingStoreDefaultCapacityAsync),
    ("v2 honors an explicit larger page ceiling on a pre-existing store", ExistingStoreExpandedCapacityAsync),
    ("v2 never lowers an existing store ceiling below its current page count", ExistingStoreCapacityFloorAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"METRIC v2-tests passed={tests.Length - failures.Count} failed={failures.Count}");
return failures.Count == 0 ? 0 : 1;

static async Task ExactRoundTripAndGenerationIsolationAsync()
{
    await WithStoreAsync(async (root, store) =>
    {
        var v1 = Path.Combine(root, "cortex.db");
        var sentinel = Encoding.UTF8.GetBytes("untouched-v1-sentinel\r\n");
        await File.WriteAllBytesAsync(v1, sentinel).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("first\r\nsecond\n\tUnicode Ω 😀 漢字\0end\n```c#\nvar x = 1;\n```");
        var create = Request("exact", 0, "create-exact", content, attributes: "{\"state\":\"active\"}");
        var result = await store.AppendAsync(create).ConfigureAwait(false);
        Require(result.Disposition == SqliteCortexV2WriteDispositions.Appended && result.Revision == 1,
            "The exact record was not appended at revision one.");
        var replay = await store.AppendAsync(create).ConfigureAwait(false);
        Require(replay.Disposition == SqliteCortexV2WriteDispositions.Idempotent,
            "An exact replay was not idempotent.");
        var collision = await store.AppendAsync(create with { Title = "Different" }).ConfigureAwait(false);
        Require(collision.Disposition == SqliteCortexV2WriteDispositions.Conflict,
            "A divergent idempotency replay did not conflict.");

        var read = await store.ReadHeadAsync("team-alpha", "knowledge", "exact").ConfigureAwait(false);
        Require(read is not null && read.ContentUtf8.SequenceEqual(content),
            "Exact UTF-8 content did not round-trip byte-for-byte.");
        Require(read!.AttributesJson == "{\"state\":\"active\"}", "Revision attributes did not round-trip.");
        Require((await File.ReadAllBytesAsync(v1).ConfigureAwait(false)).SequenceEqual(sentinel),
            "Creating cortex-v2.db changed the neighboring v1 database sentinel.");

        var metrics = await store.GetStorageMetricsAsync().ConfigureAwait(false);
        Require(metrics.RevisionRows == 1 && metrics.BlobRows == 1 &&
                metrics.OriginalBlobBytes == content.Length && metrics.CompressedBlobBytes > 0,
            "Content addressing or Brotli storage metrics are incorrect.");
        var doctor = await store.FullDoctorAsync().ConfigureAwait(false);
        Require(doctor.Healthy && doctor.RevisionRowsScanned == 1 && doctor.BlobRowsScanned == 1,
            "The full doctor did not validate the exact fresh generation.");

        Expect<ArgumentException>(() => new SqliteCortexV2StoreOptions(v1).Validate());
    }).ConfigureAwait(false);
}

static async Task UnicodeSearchAndChunksAsync()
{
    await WithStoreAsync(async (_, store) =>
    {
        const string markdown = """
            # Compiler Ωmega

            The first semantic paragraph discusses portability.

            ## Unicode section

            漢字 search works independently from ASCII tokens.

            ```text
            code fence stays together
            ```
            """;
        await store.AppendAsync(Request(
            "unicode", 0, "unicode-create", Encoding.UTF8.GetBytes(markdown),
            title: "Compiler Ωmega", tags: ["unicode", "tenet"], summary: "portable 漢字 guidance"))
            .ConfigureAwait(false);
        await store.AppendAsync(Request(
            "other", 0, "other-create", Encoding.UTF8.GetBytes("漢字 Ωmega"), ns: "team-beta"))
            .ConfigureAwait(false);

        var two = await store.SearchHeadsAsync(new(
            "team-alpha", "Ωmega 漢字 absent", "knowledge", Limit: 10, MinimumMatch: 2))
            .ConfigureAwait(false);
        Require(two.Count == 1 && two[0].RecordId == "unicode" && two[0].MatchedTerms >= 2,
            "Unicode OR/minimum-match search did not recover the expected head.");
        var three = await store.SearchHeadsAsync(new(
            "team-alpha", "Ωmega 漢字 absent", "knowledge", Limit: 10, MinimumMatch: 3))
            .ConfigureAwait(false);
        Require(three.Count == 0, "Minimum-match admitted a head missing one required query term.");
        var isolated = await store.SearchHeadsAsync(new(
            "team-gamma", "漢字", "knowledge", Limit: 10, MinimumMatch: 1))
            .ConfigureAwait(false);
        Require(isolated.Count == 0, "Head search leaked across explicit namespaces.");

        var metrics = await store.GetStorageMetricsAsync().ConfigureAwait(false);
        Require(metrics.SearchChunkRows == metrics.HeadRows && metrics.SearchChunkRows == metrics.FtsRows,
            "Small semantic Markdown blocks were not packed into one bounded FTS row per head.");
    }).ConfigureAwait(false);
}

static async Task AtomicBatchRollbackAsync()
{
    await WithStoreAsync(async (_, store) =>
    {
        var taskContent = Encoding.UTF8.GetBytes("task body");
        await store.AppendAsync(Request("task-1", 0, "task-create", taskContent, kind: "task"))
            .ConfigureAwait(false);

        var failed = await store.AppendBatchAsync(
        [
            Request("task-1", 1, "finish-task", null, kind: "task", attributes: "{\"status\":\"completed\"}"),
            Request("knowledge-1", 9, "finish-knowledge-stale", Encoding.UTF8.GetBytes("lesson"))
        ]).ConfigureAwait(false);
        Require(!failed.Committed && failed.Disposition == SqliteCortexV2WriteDispositions.StaleRevision,
            "A stale finish batch was not rejected atomically.");
        var unchanged = await store.ReadHeadAsync("team-alpha", "task", "task-1").ConfigureAwait(false);
        Require(unchanged is { Revision: 1, AttributesJson: "{}" },
            "A staged task transition escaped a failed finish transaction.");
        Require(await store.ReadHeadAsync("team-alpha", "knowledge", "knowledge-1").ConfigureAwait(false) is null,
            "A failed finish created a knowledge head.");

        var accepted = await store.AppendBatchAsync(
        [
            Request("task-1", 1, "finish-task-ok", null, kind: "task", attributes: "{\"status\":\"completed\"}"),
            Request("knowledge-1", 0, "finish-knowledge-ok", Encoding.UTF8.GetBytes("lesson"))
        ]).ConfigureAwait(false);
        Require(accepted.Committed && accepted.Items.Count == 2,
            "A valid task-plus-knowledge finish did not commit as one batch.");
        Require((await store.ReadHeadAsync("team-alpha", "task", "task-1").ConfigureAwait(false))?.Revision == 2 &&
                (await store.ReadHeadAsync("team-alpha", "knowledge", "knowledge-1").ConfigureAwait(false))?.Revision == 1,
            "The accepted atomic finish did not advance both exact heads.");
    }).ConfigureAwait(false);
}

static async Task HistoryDoesNotGrowHeadIndexAsync()
{
    await WithStoreAsync(async (_, store) =>
    {
        var content = Encoding.UTF8.GetBytes("# Stable body\n\nA repeated task body that compresses well.");
        await store.AppendAsync(Request("long-task", 0, "history-0", content, kind: "task"))
            .ConfigureAwait(false);
        var baseline = await store.GetStorageMetricsAsync().ConfigureAwait(false);

        const int updates = 1_000;
        for (var offset = 0; offset < updates; offset += 250)
        {
            var batch = Enumerable.Range(offset + 1, Math.Min(250, updates - offset))
                .Select(revision => Request(
                    "long-task", revision, $"history-{revision}", null, kind: "task",
                    attributes: $"{{\"event\":{revision}}}"))
                .ToArray();
            var result = await store.AppendBatchAsync(batch).ConfigureAwait(false);
            Require(result.Committed, $"History batch at offset {offset} did not commit.");
        }

        var metrics = await store.GetStorageMetricsAsync().ConfigureAwait(false);
        Require(metrics.RevisionRows == updates + 1 && metrics.BlobRows == 1,
            "Content-identical task events duplicated the immutable content blob.");
        Require(metrics.SearchChunkRows == baseline.SearchChunkRows && metrics.FtsRows == baseline.FtsRows,
            "Historical revisions enlarged the current-head-only search index.");
        var history = await store.ReadHistoryAsync(
            "team-alpha", "task", "long-task", afterRevision: 900, limit: 101).ConfigureAwait(false);
        Require(history.Items.Count == 101 && history.Items.Select(item => item.ContentHash).Distinct().Count() == 1,
            "Compact task history did not expose shared immutable content identity.");
        Console.WriteLine($"METRIC v2-history revisions={metrics.RevisionRows} blobs={metrics.BlobRows} headFtsRows={metrics.FtsRows}");
    }).ConfigureAwait(false);
}

static async Task QuickCheckDoesNotScanCorpusAsync()
{
    await WithStoreAsync(async (_, store) =>
    {
        var content = Encoding.UTF8.GetBytes("quick-check scale payload");
        await store.AppendAsync(Request("quick-task", 0, "quick-0", content, kind: "task"))
            .ConfigureAwait(false);
        for (var offset = 0; offset < 500; offset += 250)
        {
            var batch = Enumerable.Range(offset + 1, 250)
                .Select(revision => Request("quick-task", revision, $"quick-{revision}", null, kind: "task"))
                .ToArray();
            Require((await store.AppendBatchAsync(batch).ConfigureAwait(false)).Committed,
                "The quick-check scale fixture did not commit.");
        }

        var before = await store.GetStorageMetricsAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var quick = await store.QuickCheckAsync().ConfigureAwait(false);
        stopwatch.Stop();
        var after = await store.GetStorageMetricsAsync().ConfigureAwait(false);
        Require(quick.Healthy && quick.CorpusRowsRead == 0 && quick.ProbesExecuted == 4,
            "The startup quick check did not preserve its constant-work contract.");
        Require(before == after && before.RevisionRows == 501,
            "The startup quick check changed or traversed the scale fixture state.");
        var doctor = await store.FullDoctorAsync().ConfigureAwait(false);
        Require(doctor.Healthy && doctor.RevisionRowsScanned == 501,
            "The separate full doctor did not exhaustively scan the scale fixture.");
        Console.WriteLine($"METRIC v2-quick revisions={before.RevisionRows} corpusRowsRead={quick.CorpusRowsRead} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
    }).ConfigureAwait(false);
}

static async Task OperationalRoundTripAsync()
{
    await WithOperationalStoreAsync(async (app, operational) =>
    {
        const string ns = "team-alpha";
        const string sharedId = "same-id";
        const string taskContent = "line one\r\n\tline two\0 Ω 😀";
        var taskCreate = new CortexTaskCommandV2(
            ns, CortexTaskActionV2.Create, sharedId, ExpectedRevision: null,
            IdempotencyKey: "task-create", Title: "Operational task", Content: taskContent,
            Assignee: null, ClaimedBy: null, Reason: null, DependencyId: null,
            Dependencies: [], FirstRevision: null, LastRevision: null, AfterTaskId: null);
        var createdTask = await app.ExecuteTaskAsync(taskCreate, default)
            .ConfigureAwait(false);
        Require(createdTask.Task is { Revision: 1, Status: CortexTaskStatusV2.Open },
            "Operational task create did not return its compact receipt.");

        var provenance = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.User, "alien-laptop", "corrected repeatedly");
        var knowledgeCreate = new CortexKnowledgeCommandV2(
            ns, CortexKnowledgeActionV2.Create, sharedId, ExpectedRevision: null,
            IdempotencyKey: "knowledge-create", Kind: CortexKnowledgeKindV2.Tenet,
            Tier: CortexKnowledgeTierV2.Explicit, Title: "Generalized tenet",
            Content: "Generalized compiler guidance\r\n\twith NUL\0 and 漢字",
            Tags: ["compiler", "tenet"], Provenance: provenance,
            ReplacementRecordId: null);
        var createdKnowledge = await app.ExecuteKnowledgeAsync(knowledgeCreate, default).ConfigureAwait(false);
        Require(createdKnowledge.Record is
        {
            Kind: CortexKnowledgeKindV2.Tenet,
            Tier: CortexKnowledgeTierV2.Explicit,
            Provenance.Actor: "os_test",
            Provenance.Source: CortexProvenanceSourceV2.User,
            Provenance.SourceId: "alien-laptop",
            Revision: 1
        }, "Core-stamped knowledge provenance was not retained exactly.");

        var taskRead = await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, sharedId, Revision: null), default).ConfigureAwait(false);
        var knowledgeRead = await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Knowledge, sharedId, Revision: null), default).ConfigureAwait(false);
        Require(taskRead.Record is CortexTaskV2 { Content: taskContent } &&
                knowledgeRead.Record is CortexKnowledgeV2 { Kind: CortexKnowledgeKindV2.Tenet },
            "The strict record-type discriminator did not resolve same-id task and knowledge heads.");

        var search = await app.SearchAsync(new(
            [ns], "Generalized 漢字", [CortexKnowledgeKindV2.Tenet], AfterCursor: null), default)
            .ConfigureAwait(false);
        Require(search.Items.Count == 1 &&
                search.Items[0].Kind == CortexKnowledgeKindV2.Tenet &&
                search.Items[0].Tier == CortexKnowledgeTierV2.Explicit &&
                search.Items[0].Provenance.Actor == "os_test" &&
                search.Items[0].Provenance.SourceId == "alien-laptop",
            "Head search did not retain compact kind, tier, and provenance receipt metadata.");
        Require(knowledgeRead.Record is CortexKnowledgeV2
            {
                Provenance.Evidence: "corrected repeatedly"
            }, "Exact typed read did not retain full provenance evidence.");

        var taskSearch = await app.SearchAsync(new(
            [ns], "Operational line", Kinds: null, AfterCursor: null), default).ConfigureAwait(false);
        Require(taskSearch.Items.Any(item =>
                item.RecordType == CortexRecordTypeV2.Task && item.RecordId == sharedId &&
                item.Kind is null && item.Tier is null && item.Provenance.Actor == "os_test"),
            "A task write was not immediately searchable as a typed current head.");
        Require(search.Items.All(item => item.RecordType == CortexRecordTypeV2.Knowledge),
            "A knowledge kind filter admitted a task hit.");

        await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.Assign, sharedId, ExpectedRevision: 1,
            IdempotencyKey: "task-assign", Title: null, Content: null,
            Assignee: "agent-a", ClaimedBy: null, Reason: null, DependencyId: null,
            Dependencies: null, FirstRevision: null, LastRevision: null, AfterTaskId: null), default)
            .ConfigureAwait(false);
        var taskCreateReplay = await app.ExecuteTaskAsync(taskCreate, default).ConfigureAwait(false);
        Require(taskCreateReplay.Disposition == SqliteCortexV2WriteDispositions.Idempotent &&
                taskCreateReplay.Task is { Revision: 1, Status: CortexTaskStatusV2.Open },
            "A task create replay after head advance did not return its original committed revision.");

        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Revise, sharedId, ExpectedRevision: 1,
            IdempotencyKey: "knowledge-revise", Kind: null, Tier: CortexKnowledgeTierV2.Verified,
            Title: null, Content: null, Tags: null, Provenance: provenance,
            ReplacementRecordId: null), default).ConfigureAwait(false);
        var knowledgeCreateReplay = await app.ExecuteKnowledgeAsync(knowledgeCreate, default).ConfigureAwait(false);
        Require(knowledgeCreateReplay.Disposition == SqliteCortexV2WriteDispositions.Idempotent &&
                knowledgeCreateReplay.Record is
                { Revision: 1, Tier: CortexKnowledgeTierV2.Explicit },
            "A knowledge create replay after head advance did not return its original committed revision.");
        var history = await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.History, sharedId, ExpectedRevision: null,
            IdempotencyKey: null, Title: null, Content: null, Assignee: null,
            ClaimedBy: null, Reason: null, DependencyId: null, Dependencies: null,
            FirstRevision: 1, LastRevision: 2, AfterTaskId: null), default).ConfigureAwait(false);
        Require(history.History is { Count: 2 } &&
                history.History[0].ContentChanged && !history.History[1].ContentChanged &&
                history.History[1].Status == CortexTaskStatusV2.Assigned,
            "Compact history did not map persisted status and immutable content reuse.");

        await ExpectAsync<InvalidDataException>(() => operational.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Create, "unstamped", ExpectedRevision: null,
            IdempotencyKey: "unstamped", Kind: CortexKnowledgeKindV2.Observation,
            Tier: CortexKnowledgeTierV2.Inferred, Title: "Rejected", Content: "body",
            Tags: [], Provenance: provenance, ReplacementRecordId: null), default).AsTask());
    }).ConfigureAwait(false);
}

static async Task KnowledgeSpacesAndGeometryAsync()
{
    await WithOperationalLowLevelStoreAsync(async (app, operational, lowLevel) =>
    {
        var capturedAt = new DateTimeOffset(2026, 8, 27, 4, 5, 6, TimeSpan.Zero);
        var input = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.User,
            "fixture",
            "knowledge-space geometry fixture");

        async Task SeedAsync(
            string ns,
            string id,
            string title,
            string content,
            IReadOnlyList<string> tags)
        {
            var command = new CortexKnowledgeCommandV2(
                ns,
                CortexKnowledgeActionV2.Create,
                id,
                ExpectedRevision: null,
                IdempotencyKey: $"seed-{ns}-{id}",
                Kind: CortexKnowledgeKindV2.Observation,
                Tier: CortexKnowledgeTierV2.Verified,
                Title: title,
                Content: content,
                Tags: tags,
                Provenance: input,
                ReplacementRecordId: null)
            {
                EffectiveProvenance = new(
                    CortexProvenanceSourceV2.User,
                    "os_fixture",
                    "fixture",
                    "knowledge-space geometry fixture",
                    capturedAt)
            };
            var result = await operational.ExecuteKnowledgeAsync(command, default)
                .ConfigureAwait(false);
            Require(result.Record is { Revision: 1 }, $"Failed to seed {ns}/{id}.");
        }

        await SeedAsync(
            CortexKnowledgeSpaces.LegacySharedProgramming,
            "sqlite-locking",
            "SQLite locking diagnosis",
            "Use bounded contention evidence before changing transaction policy.\r\nΩ exact body.",
            ["sqlite", "locking", "bounded-contention"]);
        await SeedAsync(
            CortexKnowledgeSpaces.LegacySharedGeneral,
            "transaction-contention",
            "Bounded transaction contention",
            "Diagnose ownership with bounded evidence.",
            ["bounded-contention", "transaction"]);
        await SeedAsync(
            CortexKnowledgeSpaces.LegacyUserPreferences,
            "preferred-lock-report",
            "Preferred contention report",
            "Keep lock evidence compact for this user.",
            ["bounded-contention", "report"]);
        await SeedAsync(
            "project.geometry",
            "project-only",
            "Project geometry fixture",
            "Project state must remain byte-for-byte unchanged by knowledge migration.",
            ["project", "unchanged"]);

        var projectBefore = await lowLevel.ReadHeadAsync(
            "project.geometry", "knowledge", "project-only").ConfigureAwait(false) ??
            throw new InvalidOperationException("The project fixture is missing.");

        var lexicalNeighbor = await lowLevel.SearchHeadsAsync(new(
            CortexKnowledgeSpaces.LegacySharedGeneral,
            "sqlite locking",
            "knowledge",
            Limit: 8,
            MinimumMatch: 1)).ConfigureAwait(false);
        Require(lexicalNeighbor.Count == 0,
            "The related fixture accidentally matched the original terms and cannot prove geometric expansion.");

        var shared = await app.SearchAsync(new(
            [CortexKnowledgeSpaces.LegacySharedProgramming],
            "sqlite locking",
            [CortexKnowledgeKindV2.Observation],
            AfterCursor: null,
            Limit: 8), default).ConfigureAwait(false);
        Require(shared.EffectiveNamespaces.SequenceEqual([CortexKnowledgeSpaces.Shared]) &&
                shared.Items.Count >= 2 &&
                shared.Items[0].RecordId == "sqlite-locking" &&
                shared.Items.Any(value => value.RecordId == "transaction-contention") &&
                shared.Items.All(value => value.RecordId != "preferred-lock-report"),
            "The shared space did not canonicalize, expand a related shared point, or exclude the user overlay.");

        var composed = await app.SearchAsync(new(
            [CortexKnowledgeSpaces.Shared, CortexKnowledgeSpaces.LegacyUserPreferences],
            "sqlite locking",
            [CortexKnowledgeKindV2.Observation],
            AfterCursor: null,
            Limit: 8), default).ConfigureAwait(false);
        Require(composed.EffectiveNamespaces.SequenceEqual(
                    [CortexKnowledgeSpaces.Shared, CortexKnowledgeSpaces.User]) &&
                composed.Items.Any(value => value.RecordId == "preferred-lock-report"),
            "Explicitly composing Knowledge.Shared and Knowledge.User did not admit the user overlay.");

        var exact = await app.ReadAsync(new(
            CortexKnowledgeSpaces.LegacySharedGeneral,
            CortexRecordTypeV2.Knowledge,
            "transaction-contention",
            Revision: null), default).ConfigureAwait(false);
        Require(exact.EffectiveNamespaces.SequenceEqual([CortexKnowledgeSpaces.Shared]) &&
                exact.Record is CortexKnowledgeV2
                 {
                     Namespace: CortexKnowledgeSpaces.Shared,
                     Content: "Diagnose ownership with bounded evidence."
                 },
            "A compatibility read did not return the exact body under its canonical visible coordinate.");

        var namespaces = await app.ListNamespacesAsync(new(null, 32), default).ConfigureAwait(false);
        Require(namespaces.Items.Count(value => value.Namespace == CortexKnowledgeSpaces.Shared) == 1 &&
                namespaces.Items.Count(value => value.Namespace == CortexKnowledgeSpaces.User) == 1 &&
                namespaces.Items.All(value => value.Namespace is not
                    CortexKnowledgeSpaces.LegacySharedProgramming and not
                    CortexKnowledgeSpaces.LegacySharedGeneral and not
                    CortexKnowledgeSpaces.LegacyUserPreferences),
            "The namespace catalog did not present one base-first logical entry per knowledge space.");

        var migrated = await operational.MigrateKnowledgeSpacesAsync(
            "os_fixture",
            capturedAt.AddMinutes(1),
            default).ConfigureAwait(false);
        Require(migrated.SourceRecords == 3 && migrated.Migrated == 3 && migrated.Archived == 3,
            "The one-hop conversion did not migrate and archive every active legacy head atomically.");
        var projectAfter = await lowLevel.ReadHeadAsync(
            "project.geometry", "knowledge", "project-only").ConfigureAwait(false) ??
            throw new InvalidOperationException("The project fixture disappeared.");
        Require(projectAfter.Revision == projectBefore.Revision &&
                projectAfter.RevisionHash == projectBefore.RevisionHash &&
                projectAfter.ContentUtf8.SequenceEqual(projectBefore.ContentUtf8),
            "The knowledge conversion changed a project namespace record.");
        foreach (var legacy in new[]
                 {
                     CortexKnowledgeSpaces.LegacySharedProgramming,
                     CortexKnowledgeSpaces.LegacySharedGeneral,
                     CortexKnowledgeSpaces.LegacyUserPreferences
                 })
        {
            var oldHead = await lowLevel.ReadHeadAsync(
                legacy,
                "knowledge",
                legacy == CortexKnowledgeSpaces.LegacySharedProgramming
                    ? "sqlite-locking"
                    : legacy == CortexKnowledgeSpaces.LegacySharedGeneral
                        ? "transaction-contention"
                        : "preferred-lock-report").ConfigureAwait(false);
            Require(oldHead is { Revision: 2, Searchable: false },
                $"Legacy head {legacy} was not preserved as a non-searchable archived revision.");
        }

        var replay = await operational.MigrateKnowledgeSpacesAsync(
            "os_fixture",
            capturedAt.AddMinutes(2),
            default).ConfigureAwait(false);
        Require(replay.SourceRecords == 0 && replay.Disposition == "already-current",
            "The one-hop conversion was not idempotent after the legacy heads were archived.");

        for (var index = 0; index < 40; index++)
        {
            _ = await app.ExecuteKnowledgeAsync(new(
                CortexKnowledgeSpaces.Shared,
                CortexKnowledgeActionV2.Create,
                $"paged-geometry-{index:D2}",
                ExpectedRevision: null,
                IdempotencyKey: $"paged-geometry-create-{index:D2}",
                Kind: CortexKnowledgeKindV2.Observation,
                Tier: CortexKnowledgeTierV2.Verified,
                Title: $"Pagedkeyword geometry record {index:D2}",
                Content: $"Exact pagedkeyword body {index:D2}.",
                Tags: ["pagedkeyword"],
                Provenance: new(CortexProvenanceSourceV2.Agent, "paging-fixture", "verified fixture"),
                ReplacementRecordId: null), default).ConfigureAwait(false);
        }
        var firstPage = await app.SearchAsync(new(
            [CortexKnowledgeSpaces.Shared],
            "pagedkeyword",
            [CortexKnowledgeKindV2.Observation],
            AfterCursor: null,
            Limit: 32,
            MaxUtf8Bytes: CortexV2Limits.MaximumCompactResponseUtf8Bytes,
            Format: CortexResponseFormatV2.Compact), default).ConfigureAwait(false);
        var secondPage = await app.SearchAsync(new(
            [CortexKnowledgeSpaces.Shared],
            "pagedkeyword",
            [CortexKnowledgeKindV2.Observation],
            firstPage.NextCursor,
            Limit: 32,
            MaxUtf8Bytes: CortexV2Limits.MaximumCompactResponseUtf8Bytes,
            Format: CortexResponseFormatV2.Compact), default).ConfigureAwait(false);
        var pagedItems = firstPage.Items.Concat(secondPage.Items).ToArray();
        Require(firstPage.Items.Count == 32 && firstPage.HasMore && firstPage.NextCursor is not null &&
                secondPage.Items.Count >= 8 && !secondPage.HasMore && secondPage.NextCursor is null &&
                pagedItems.Select(value => value.RecordId)
                    .Distinct(StringComparer.Ordinal).Count() == pagedItems.Length &&
                pagedItems.Count(value => value.RecordId.StartsWith("paged-geometry-", StringComparison.Ordinal)) == 40,
            $"Bounded geometric preview pagination did not expose every matching record coordinate: " +
            $"first={firstPage.Items.Count}/{firstPage.HasMore}/{firstPage.NextCursor is not null}, " +
            $"second={secondPage.Items.Count}/{secondPage.HasMore}/{secondPage.NextCursor is not null}, " +
            $"unique={pagedItems.Select(value => value.RecordId).Distinct(StringComparer.Ordinal).Count()}, " +
            $"direct={pagedItems.Count(value => value.RecordId.StartsWith("paged-geometry-", StringComparison.Ordinal))}.");
    }).ConfigureAwait(false);
}

static async Task OperationalTaskAwareStartAsync()
{
    await WithOperationalLowLevelStoreAsync(async (app, _, lowLevel) =>
    {
        const string ns = "startup-space";
        const string taskId = "019ff269-3c07-7082-87cf-d9325d7e24ba";
        const string exactTaskBody = "Objective: repair parser recovery.\nVerified state: fixture created.\nBlocker: none.\nNext: finish atomically.";
        await app.ExecuteTaskAsync(TaskCommand(
            ns, CortexTaskActionV2.Create, taskId, null, "startup-current-create",
            "Current parser recovery", exactTaskBody, phaseId: "implementation",
            checkpointSubject: "parser recovery"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(
            ns, CortexTaskActionV2.Create, "aaa-parser-inventory", null, "startup-alpha-create",
            "Parser inventory", "Alphabetically early but only weakly related."), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(
            ns, CortexTaskActionV2.Create, "zzz-recovery-implementation", null, "startup-relevant-create",
            "Implement parser recovery", "Repair parser recovery with exact cursor binding."), default)
            .ConfigureAwait(false);
        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Create, "correction-parser-recovery", null,
            "startup-correction-create", CortexKnowledgeKindV2.Correction,
            CortexKnowledgeTierV2.Explicit, "Parser recovery correction",
            "The user requires exact task continuity for parser recovery.", ["parser", "recovery"],
            new(CortexProvenanceSourceV2.User, "user-startup-contract", "direct user correction"), null), default)
            .ConfigureAwait(false);

        var fixtureTime = new DateTimeOffset(2026, 8, 13, 2, 0, 0, TimeSpan.Zero);
        var fixtureMetadata = JsonSerializer.Serialize(new
        {
            state = CortexKnowledgeStateV2.Active,
            kind = CortexKnowledgeKindV2.Observation,
            tier = CortexKnowledgeTierV2.Inferred,
            provenance = new CortexProvenanceV2(
                CortexProvenanceSourceV2.Agent, "os_fixture", null, null, fixtureTime),
            supersededByRecordId = (string?)null,
            createdAtUtc = fixtureTime,
            updatedAtUtc = fixtureTime,
            archivedAtUtc = (DateTimeOffset?)null
        }, CortexV2Json.Options);
        for (var index = 0; index < 2_000; index++)
        {
            var id = $"generated-noise-{index:D4}";
            var write = await lowLevel.AppendAsync(Request(
                id,
                0,
                $"generated-noise-create-{index:D4}",
                Encoding.UTF8.GetBytes($"Generated background fixture {index}."),
                ns,
                "knowledge",
                $"Background fixture {index}",
                ["generated"],
                "Generated scale fixture",
                fixtureMetadata)).ConfigureAwait(false);
            Require(write.Disposition == SqliteCortexV2WriteDispositions.Appended,
                "A generated scale fixture record was not appended.");
        }

        var request = new CortexStartRequestV2(
            [ns], "parser recovery", KnownContextCursor: null,
            MaxUtf8Bytes: CortexV2Limits.DefaultStartResponseUtf8Bytes,
            TaskId: taskId,
            PhaseId: "implementation",
            Format: CortexResponseFormatV2.Compact);
        var start = await app.StartAsync(request, default).ConfigureAwait(false);
        var startBytes = JsonSerializer.SerializeToUtf8Bytes(start, CortexV2Json.Options).Length;
        Require(start.CurrentTask is { TaskId: taskId, Revision: 1 },
            "Startup did not return the exact current task separately.");
        Require(start.Items.Count is > 0 and <= 4 && startBytes <= 4 * 1024,
            "Startup exceeded its four-item or 4 KiB default bound.");
        Require(start.Items[0] is
            {
                RecordType: "knowledge",
                RecordId: "correction-parser-recovery",
                Kind: CortexKnowledgeKindV2.Correction,
                Tier: CortexKnowledgeTierV2.Explicit,
                Provenance.Source: CortexProvenanceSourceV2.User
            }, "A relevant explicit user correction did not outrank task context.");
        Require(start.Items.Any(value => value.RecordId == "zzz-recovery-implementation") &&
                start.Items.All(value => value.RecordId != taskId) &&
                start.Items.All(value => !value.RecordId.StartsWith("generated-noise-", StringComparison.Ordinal)),
            "Intent ranking did not replace alphabetical or generated-noise selection.");

        var exactRepeat = await app.StartAsync(
            request with { KnownContextCursor = start.ContextCursor }, default).ConfigureAwait(false);
        Require(exactRepeat.Disposition == CortexContextDispositionV2.Unchanged &&
                exactRepeat.CurrentTask is null && exactRepeat.Items.Count == 0,
            "An exact startup replay did not return unchanged.");
        var normalizedRepeat = await app.StartAsync(
            request with { Intent = "  PARSER   recovery ", KnownContextCursor = start.ContextCursor }, default)
            .ConfigureAwait(false);
        Require(normalizedRepeat.Disposition == CortexContextDispositionV2.Unchanged,
            "Equivalent normalized intent changed the startup cursor.");
        var changedIntent = await app.StartAsync(
            request with { Intent = "storage migration", KnownContextCursor = start.ContextCursor }, default)
            .ConfigureAwait(false);
        var changedTask = await app.StartAsync(
            request with { TaskId = "different-task", KnownContextCursor = start.ContextCursor }, default)
            .ConfigureAwait(false);
        var changedPhase = await app.StartAsync(
            request with { PhaseId = "verification", KnownContextCursor = start.ContextCursor }, default)
            .ConfigureAwait(false);
        Require(changedIntent.Disposition == CortexContextDispositionV2.Snapshot &&
                changedIntent.ContextCursor != start.ContextCursor &&
                changedTask.Disposition == CortexContextDispositionV2.Snapshot &&
                changedTask.ContextCursor != start.ContextCursor &&
                changedPhase.Disposition == CortexContextDispositionV2.Snapshot &&
                changedPhase.ContextCursor != start.ContextCursor,
            "Changed intent, task, or phase identity reused an incompatible startup cursor.");
        var compatibility = await app.StartAsync(new(
            [ns], "parser recovery", KnownContextCursor: null), default).ConfigureAwait(false);
        Require(compatibility.CurrentTask is null && compatibility.Items.Count is > 0 and <= 4,
            "A compatibility caller omitting taskId did not receive bounded intent-ranked context.");
        await ExpectAsync<ArgumentOutOfRangeException>(() => app.StartAsync(
            request with { MaxUtf8Bytes = CortexV2Limits.MaximumStartResponseUtf8Bytes + 1 }, default).AsTask());

        var exactRead = await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, taskId, Revision: null), default).ConfigureAwait(false);
        Require(exactRead.Record is CortexTaskV2 { Content: exactTaskBody },
            "Typed exact read did not preserve the current task body.");
        Require(exactRead.Record is CortexTaskV2
            {
                PhaseId: "implementation",
                CheckpointSubject: "parser recovery"
            }, "Typed exact read did not preserve task phase metadata.");
        Require(typeof(SqliteCortexV2HeadProjection).GetProperty("ContentUtf8") is null,
            "The compact exact-task startup projection exposes a hydrated content body.");
        var metrics = await lowLevel.GetStorageMetricsAsync().ConfigureAwait(false);
        Require(metrics.HeadRows >= 2_004,
            "The generated thousands-of-record startup fixture was not exercised.");

        var finish = await app.FinishAsync(new(
            ns,
            taskId,
            1,
            CortexFinishTaskActionV2.Complete,
            TaskTitle: null,
            TaskContent: "Objective complete.\nVerified state: startup contract tested.\nBlocker: none.\nNext: package candidate.",
            BlockedReason: null,
            Checkpoint: "Task-aware startup implemented.",
            Evidence: "Generated 2,000-record fixture and exact read passed.",
            Handoff: "Proceed to signed package.",
            Knowledge:
            [
                new CortexFinishKnowledgeWriteV2(
                    "decision-task-aware-startup",
                    null,
                    CortexKnowledgeKindV2.Decision,
                    CortexKnowledgeTierV2.Verified,
                    "Use exact task-aware startup",
                    "Bind startup to task identity, intent, selection version, and namespace revisions.",
                    ["startup", "continuity"],
                    new(CortexProvenanceSourceV2.Agent, "sqlite-startup-test", "locally validated"),
                    "startup-finish-knowledge")
            ],
            IdempotencyKey: "startup-finish"), default).ConfigureAwait(false);
        Require(finish.Disposition == SqliteCortexV2WriteDispositions.Appended &&
                finish.Task is { TaskId: taskId, Revision: 2, Status: CortexTaskStatusV2.Completed } &&
                finish.Knowledge is [{ RecordId: "decision-task-aware-startup", Revision: 1 }],
            "Exact task-ID lifecycle and atomic finish did not commit together.");
    }).ConfigureAwait(false);
}

static async Task OperationalEfficiencyExtensionsAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        const string ns = "efficiency-space";
        await app.ExecuteTaskAsync(TaskCommand(
            ns, CortexTaskActionV2.Create, "task-a", null, "eff-task-create",
            "Structured task", "Objective and verified state.", phaseId: "analysis",
            checkpointSubject: "initial baseline"), default).ConfigureAwait(false);
        var provenance = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.Agent, "efficiency-fixture", "verified fixture");
        foreach (var id in new[] { "duplicate-a", "duplicate-b" })
        {
            await app.ExecuteKnowledgeAsync(new(
                ns, CortexKnowledgeActionV2.Create, id, null, $"eff-{id}",
                CortexKnowledgeKindV2.Workaround, CortexKnowledgeTierV2.Verified,
                "Same normalized title", "Exact shared body", ["efficiency"], provenance, null), default)
                .ConfigureAwait(false);
        }

        var batch = await app.ReadBatchAsync(new(
        [
            new(ns, CortexRecordTypeV2.Task, "task-a", 1),
            new(ns, CortexRecordTypeV2.Knowledge, "duplicate-a", 1),
            new(ns, CortexRecordTypeV2.Knowledge, "duplicate-b", null)
        ]), default).ConfigureAwait(false);
        Require(batch.Items.Count == 3 && batch.EffectiveNamespaces.SequenceEqual([ns]) &&
                batch.Items[0].Record is CortexTaskV2 { Content: "Objective and verified state." } &&
                batch.Items[1].Record is CortexKnowledgeV2 { Content: "Exact shared body" },
            "Batched typed read did not return ordered exact full records.");
        await ExpectAsync<ArgumentException>(() => app.ReadBatchAsync(new(
        [
            new(ns, CortexRecordTypeV2.Task, "task-a", 1),
            new(ns, CortexRecordTypeV2.Task, "task-a", 1)
        ]), default).AsTask());

        var before = await app.StatusAsync(default).ConfigureAwait(false);
        var analysis = await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Analyze, RecordId: null, ExpectedRevision: null,
            IdempotencyKey: null, Kind: null, Tier: null, Title: null, Content: null,
            Tags: null, Provenance: null, ReplacementRecordId: null,
            AfterRecordId: null, Limit: 128), default).ConfigureAwait(false);
        var after = await app.StatusAsync(default).ConfigureAwait(false);
        Require(analysis is
            {
                Disposition: "analyzed",
                Record: null,
                Analysis.Suggestions: [{ Reason: "exact-content" }]
            }, "Read-only consolidation did not identify the deterministic exact duplicate.");
        Require(before.RevisionCount == after.RevisionCount,
            "Consolidation analysis mutated retained revisions.");
        Require(after.RetrievalMetrics is
            {
                BatchRead.Calls: 2,
                BatchRead.Failures: 1,
                ExactRecordsReturned: 3
            }, "Content-free retrieval metrics did not count successful and rejected batch reads.");
    }).ConfigureAwait(false);
}

static async Task OperationalFinishAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        const string ns = "finish-space";
        await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.Create, "task", null, "create-task", "Task", "body",
            null, null, null, null, [], null, null, null), default).ConfigureAwait(false);
        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Create, "existing", null, "create-existing",
            CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Inferred,
            "Existing", "old", [], new(CortexProvenanceSourceV2.Agent, null, null), null), default)
            .ConfigureAwait(false);

        var stale = await app.FinishAsync(FinishRequest(
            ns, expectedTaskRevision: 1, taskKey: "finish-stale",
            new("existing", 9, CortexKnowledgeKindV2.Correction, CortexKnowledgeTierV2.Verified,
                "Replacement", "new", [], new(CortexProvenanceSourceV2.Agent, null, "evidence"),
                "finish-knowledge-stale")), default).ConfigureAwait(false);
        Require(stale.Disposition == SqliteCortexV2WriteDispositions.StaleRevision,
            "A stale operational finish was not rejected.");
        var unchanged = (CortexTaskV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, "task", null), default).ConfigureAwait(false)).Record!;
        Require(unchanged.Revision == 1 && unchanged.Status == CortexTaskStatusV2.Open,
            "A staged operational task finish escaped a failed atomic batch.");

        var request = FinishRequest(
            ns, expectedTaskRevision: 1, taskKey: "finish-good",
            new("new-knowledge", null, CortexKnowledgeKindV2.Workaround, CortexKnowledgeTierV2.Explicit,
                "Workaround", "exact\r\n\tcontent\0 Ω", ["compiler"],
                new(CortexProvenanceSourceV2.User, "user-note", "observed"), "finish-knowledge-good"));
        var accepted = await app.FinishAsync(request, default).ConfigureAwait(false);
        Require(accepted.Disposition == SqliteCortexV2WriteDispositions.Appended &&
                accepted.Task.Revision == 2 && accepted.Task.Status == CortexTaskStatusV2.Completed &&
                accepted.Knowledge is [{ Tier: CortexKnowledgeTierV2.Explicit, Revision: 1 }],
            "A valid operational finish did not atomically store task plus exact knowledge.");
        var exactKnowledge = (CortexKnowledgeV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Knowledge, "new-knowledge", null), default).ConfigureAwait(false)).Record!;
        var exactTask = (CortexTaskV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, "task", null), default).ConfigureAwait(false)).Record!;
        Require(exactKnowledge.Content == "exact\r\n\tcontent\0 Ω",
            "The compact finish receipt did not correspond to exact stored knowledge.");
        Require(exactTask is
            {
                Title: "Finished task",
                Content: "Final task body",
                Revision: 2,
                Status: CortexTaskStatusV2.Completed
            }, "A completed SQLite finish did not retain its supplied exact task title and body.");
        var completedHistory = await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.History, "task", ExpectedRevision: null, IdempotencyKey: null,
            Title: null, Content: null, Assignee: null, ClaimedBy: null, Reason: null,
            DependencyId: null, Dependencies: null, FirstRevision: 2, LastRevision: 2,
            AfterTaskId: null), default).ConfigureAwait(false);
        Require(completedHistory.History is [{ Revision: 2, ContentChanged: true }],
            "A completed SQLite finish did not record its supplied task body as a content change.");
        var replay = await app.FinishAsync(request, default).ConfigureAwait(false);
        Require(replay.Disposition == SqliteCortexV2WriteDispositions.Idempotent &&
                replay.Task.Revision == 2 && replay.Knowledge[0].Revision == 1,
            "An exact operational finish retry was not idempotent.");
        await app.ExecuteTaskAsync(TaskCommand(
            ns, CortexTaskActionV2.Reopen, "task", 2, "after-finish-reopen"), default)
            .ConfigureAwait(false);
        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Revise, "new-knowledge", 1, "after-finish-revise",
            Kind: null, Tier: CortexKnowledgeTierV2.Verified, Title: "Later title",
            Content: null, Tags: null,
            new(CortexProvenanceSourceV2.Agent, null, "later"), ReplacementRecordId: null), default)
            .ConfigureAwait(false);
        var lateReplay = await app.FinishAsync(request, default).ConfigureAwait(false);
        Require(lateReplay.Disposition == SqliteCortexV2WriteDispositions.Idempotent &&
                lateReplay.Task.Revision == 2 && lateReplay.Knowledge[0].Revision == 1 &&
                lateReplay.Knowledge[0].Title == "Workaround",
            "A finish retry after head advance did not return its original committed receipts.");

        await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.Create, "blocked-task", null, "create-blocked-task",
            "Before block", "Before block body", null, null, null, null, [], null, null, null), default)
            .ConfigureAwait(false);
        var blocked = await app.FinishAsync(new(
            ns, "blocked-task", 1, CortexFinishTaskActionV2.Block,
            TaskTitle: "Blocked closeout", TaskContent: "Exact blocker state", BlockedReason: "Dependency unavailable",
            Checkpoint: "Blocked checkpoint", Evidence: "Validated blocker", Handoff: "Resume after dependency",
            Knowledge: [], IdempotencyKey: "finish-blocked-task"), default).ConfigureAwait(false);
        var exactBlocked = (CortexTaskV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, "blocked-task", null), default).ConfigureAwait(false)).Record!;
        Require(blocked.Task is { Revision: 2, Status: CortexTaskStatusV2.Blocked } &&
                exactBlocked is
                {
                    Title: "Blocked closeout",
                    Content: "Exact blocker state",
                    BlockedReason: "Dependency unavailable",
                    Revision: 2,
                    Status: CortexTaskStatusV2.Blocked
                }, "A blocked SQLite finish did not retain its supplied exact task title and body.");
    }).ConfigureAwait(false);
}

static async Task OperationalNamespacePaginationAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        foreach (var ns in new[] { "alpha", "beta", "gamma" })
        {
            await app.ExecuteKnowledgeAsync(new(
                ns, CortexKnowledgeActionV2.Create, "record", null, $"create-{ns}",
                CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Inferred,
                ns, ns, [], new(CortexProvenanceSourceV2.Agent, null, null), null), default)
                .ConfigureAwait(false);
        }
        var first = await app.ListNamespacesAsync(new(null, Limit: 2), default).ConfigureAwait(false);
        var second = await app.ListNamespacesAsync(new(first.NextCursor, Limit: 2), default).ConfigureAwait(false);
        Require(first.Items.Select(item => item.Namespace).SequenceEqual(["alpha", "beta"]) &&
                first.HasMore && first.NextCursor == "beta" &&
                second.Items.Select(item => item.Namespace).SequenceEqual(["gamma"]) &&
                !second.HasMore && second.NextCursor is null,
            "Namespace pagination did not return a stable bounded ordinal page.");
    }).ConfigureAwait(false);
}

static async Task OperationalLifecyclesAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        const string ns = "lifecycle";
        const string taskId = "task";
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Create, taskId, null, "t-create",
            title: "Lifecycle searchable task", content: "original body"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.DependencyAdd, taskId, 1, "t-dep-add",
            dependencyId: "prerequisite"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Assign, taskId, 2, "t-assign",
            assignee: "agent-a"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Claim, taskId, 3, "t-claim",
            claimedBy: "agent-b"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Update, taskId, 4, "t-update",
            content: "updated body"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Block, taskId, 5, "t-block",
            reason: "dependency unavailable"), default).ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Reopen, taskId, 6, "t-reopen-1"), default)
            .ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Complete, taskId, 7, "t-complete"), default)
            .ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.Reopen, taskId, 8, "t-reopen-2"), default)
            .ConfigureAwait(false);
        await app.ExecuteTaskAsync(TaskCommand(ns, CortexTaskActionV2.DependencyRemove, taskId, 9, "t-dep-remove",
            dependencyId: "prerequisite"), default).ConfigureAwait(false);
        var archived = await app.ExecuteTaskAsync(
            TaskCommand(ns, CortexTaskActionV2.Archive, taskId, 10, "t-archive"), default).ConfigureAwait(false);
        Require(archived.Task is
        {
            Revision: 11,
            Status: CortexTaskStatusV2.Archived,
        }, "The complete task lifecycle did not retain its expected final state.");
        var archivedRead = (CortexTaskV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Task, taskId, null), default).ConfigureAwait(false)).Record!;
        Require(archivedRead.Content == "updated body" && archivedRead.Dependencies.Count == 0 &&
                archivedRead.ArchivedAtUtc is not null,
            "The archived task exact read lost content, dependencies, or archive state.");
        var archiveReplay = await app.ExecuteTaskAsync(
            TaskCommand(ns, CortexTaskActionV2.Archive, taskId, 10, "t-archive"), default).ConfigureAwait(false);
        Require(archiveReplay.Disposition == SqliteCortexV2WriteDispositions.Idempotent,
            "An archived task transition did not replay idempotently.");
        await ExpectAsync<InvalidOperationException>(() => app.ExecuteTaskAsync(
            TaskCommand(ns, CortexTaskActionV2.Update, taskId, 11, "t-after-archive", title: "forbidden"),
            default).AsTask());
        var hiddenTaskSearch = await app.SearchAsync(new(
            [ns], "Lifecycle searchable", Kinds: null, AfterCursor: null), default).ConfigureAwait(false);
        var hiddenTaskList = await app.ExecuteTaskAsync(new(
            ns, CortexTaskActionV2.List, null, null, null, null, null, null, null, null,
            null, null, null, null, null), default).ConfigureAwait(false);
        Require(hiddenTaskSearch.Items.All(item => item.RecordId != taskId) && hiddenTaskList.Items.Count == 0,
            "An archived task remained in active search or task-list projections.");

        var input = new CortexProvenanceInputV2(CortexProvenanceSourceV2.Agent, null, "lifecycle");
        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Create, "knowledge", null, "k-create",
            CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Inferred,
            "Lifecycle knowledge", "hidden phrase", [], input, null), default).ConfigureAwait(false);
        await app.ExecuteKnowledgeAsync(new(
            ns, CortexKnowledgeActionV2.Revise, "knowledge", 1, "k-revise",
            CortexKnowledgeKindV2.Correction, CortexKnowledgeTierV2.Verified,
            "Corrected knowledge", Content: null, Tags: ["corrected"], input, null), default)
            .ConfigureAwait(false);
        var supersede = new CortexKnowledgeCommandV2(
            ns, CortexKnowledgeActionV2.Supersede, "knowledge", 2, "k-supersede",
            Kind: null, Tier: null, Title: null, Content: null, Tags: null, input,
            ReplacementRecordId: "replacement");
        var superseded = await app.ExecuteKnowledgeAsync(supersede, default).ConfigureAwait(false);
        Require(superseded.Record is
        {
            Revision: 3,
            State: CortexKnowledgeStateV2.Superseded,
            Kind: CortexKnowledgeKindV2.Correction,
            Tier: CortexKnowledgeTierV2.Verified,
        }, "The knowledge revise/supersede lifecycle lost typed metadata or exact content.");
        var supersededRead = (CortexKnowledgeV2)(await app.ReadAsync(new(
            ns, CortexRecordTypeV2.Knowledge, "knowledge", null), default).ConfigureAwait(false)).Record!;
        Require(supersededRead.Content == "hidden phrase" &&
                supersededRead.SupersededByRecordId == "replacement",
            "The superseded knowledge exact read lost content or replacement identity.");
        Require((await app.ExecuteKnowledgeAsync(supersede, default).ConfigureAwait(false)).Disposition ==
                SqliteCortexV2WriteDispositions.Idempotent,
            "A superseded knowledge transition did not replay idempotently.");
        var hiddenKnowledge = await app.SearchAsync(new(
            [ns], "hidden phrase", Kinds: null, AfterCursor: null), default).ConfigureAwait(false);
        Require(hiddenKnowledge.Items.All(item => item.RecordId != "knowledge"),
            "Superseded knowledge remained in the active current-head search index.");
    }).ConfigureAwait(false);
}

static async Task OperationalQueryBudgetsAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        const string ns = "budget-space";
        var provenance = new CortexProvenanceInputV2(
            CortexProvenanceSourceV2.Imported,
            new string('s', 128),
            new string('e', 512));
        for (var index = 0; index < 10; index++)
        {
            await app.ExecuteKnowledgeAsync(new(
                ns,
                CortexKnowledgeActionV2.Create,
                $"knowledge-{index:D2}",
                ExpectedRevision: null,
                IdempotencyKey: $"budget-create-{index:D2}",
                Kind: CortexKnowledgeKindV2.Tenet,
                Tier: CortexKnowledgeTierV2.Explicit,
                Title: $"Migration budget record {index:D2} {new string('t', 128)}",
                Content: $"migration {new string((char)('a' + index), 2_000)}",
                Tags: ["migration", "budget"],
                Provenance: provenance,
                ReplacementRecordId: null), default).ConfigureAwait(false);
        }

        var start = await app.StartAsync(new([ns], "migration", KnownContextCursor: null), default)
            .ConfigureAwait(false);
        var startBytes = JsonSerializer.SerializeToUtf8Bytes(start, CortexV2Json.Options).Length;
        Require(startBytes <= CortexV2Limits.DefaultResponseUtf8Bytes && start.Items.Count < 6 && start.HasMore,
            "The default start snapshot did not greedily stop at its compact byte budget.");

        var search = await app.SearchAsync(new(
            [ns], "migration budget", [CortexKnowledgeKindV2.Tenet], AfterCursor: null, Limit: 10), default)
            .ConfigureAwait(false);
        var searchBytes = JsonSerializer.SerializeToUtf8Bytes(search, CortexV2Json.Options).Length;
        Require(searchBytes <= CortexV2Limits.DefaultResponseUtf8Bytes && search.Items.Count < 10 && search.HasMore,
            "The default search page did not greedily stop at its compact byte budget.");
    }).ConfigureAwait(false);
}

static async Task MaximumControlContentAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        var exact = MaximumControlText();
        Require(exact.Length == CortexV2Limits.MaximumContentCharacters,
            "The control-heavy fixture did not reach the contract character ceiling.");
        var created = await app.ExecuteKnowledgeAsync(new(
            "maximum-content", CortexKnowledgeActionV2.Create, "record", null, "maximum-create",
            CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Verified,
            "Maximum content", exact, [],
            new(CortexProvenanceSourceV2.Agent, null, "maximum escaped controls"), null), default)
            .ConfigureAwait(false);
        Require(created.Record is { Revision: 1 },
            "Contract-maximum control-heavy content did not return a compact receipt.");
        var read = await app.ReadAsync(new(
            "maximum-content", CortexRecordTypeV2.Knowledge, "record", null,
            MaxUtf8Bytes: CortexV2Limits.MaximumResponseUtf8Bytes), default).ConfigureAwait(false);
        Require(read.Record is CortexKnowledgeV2 { Content: var retained } && retained == exact &&
                Encoding.UTF8.GetBytes(retained).SequenceEqual(Encoding.UTF8.GetBytes(exact)),
            "Contract-maximum control-heavy content did not round-trip exact UTF-8 through SQLite.");
    }).ConfigureAwait(false);
}

static async Task MaximumCompactReceiptsAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        var maximum = MaximumControlText();
        var taskWrite = await app.ExecuteTaskAsync(new(
            "maximum-receipts", CortexTaskActionV2.Create, "task", null, "task-create",
            "Maximum task", maximum, null, null, null, null, [], null, null, null,
            MaxUtf8Bytes: CortexV2Limits.DefaultResponseUtf8Bytes), default).ConfigureAwait(false);
        Require(taskWrite.Task is { Revision: 1 } && taskWrite.Task.Excerpt.Length <= 1_024,
            "A maximum task body did not return one compact successful receipt.");
        var exactTask = (CortexTaskV2)(await app.ReadAsync(new(
            "maximum-receipts", CortexRecordTypeV2.Task, "task", null,
            CortexV2Limits.MaximumResponseUtf8Bytes), default).ConfigureAwait(false)).Record!;
        Require(exactTask.Content == maximum,
            "The maximum task receipt did not correspond to exact stored content.");

        var knowledge = Enumerable.Range(0, CortexV2Limits.MaximumKnowledgeWritesPerFinish)
            .Select(index => new CortexFinishKnowledgeWriteV2(
                $"knowledge-{index:D2}", null, CortexKnowledgeKindV2.Observation,
                CortexKnowledgeTierV2.Verified, $"Knowledge {index:D2}", maximum,
                ["maximum"], new(CortexProvenanceSourceV2.Agent, $"source-{index:D2}", "tested"),
                $"finish-record-{index:D2}"))
            .ToArray();
        var finish = await app.FinishAsync(new(
            "maximum-receipts", "task", 1, CortexFinishTaskActionV2.Complete,
            TaskTitle: null, TaskContent: null, BlockedReason: null,
            Checkpoint: "all maximum records retained", Evidence: "focused SQLite test", Handoff: null,
            Knowledge: knowledge, IdempotencyKey: "finish-all",
            MaxUtf8Bytes: CortexV2Limits.MaximumCompactResponseUtf8Bytes), default).ConfigureAwait(false);
        Require(finish.Disposition == SqliteCortexV2WriteDispositions.Appended &&
                finish.Task is { Revision: 2, Status: CortexTaskStatusV2.Completed } &&
                finish.Knowledge.Count == CortexV2Limits.MaximumKnowledgeWritesPerFinish &&
                finish.Knowledge.All(item => item.Revision == 1),
            "A sixteen-record maximum-body finish did not commit with one compact response.");
        var serializedReceipt = JsonSerializer.SerializeToUtf8Bytes(finish, CortexV2Json.Options);
        Require(serializedReceipt.Length <= CortexV2Limits.MaximumCompactResponseUtf8Bytes,
            "The successful maximum finish receipt exceeded its compact response budget.");
        foreach (var receipt in finish.Knowledge)
        {
            var read = (CortexKnowledgeV2)(await app.ReadAsync(new(
                "maximum-receipts", CortexRecordTypeV2.Knowledge, receipt.RecordId, null,
                CortexV2Limits.MaximumResponseUtf8Bytes), default).ConfigureAwait(false)).Record!;
            Require(read.Content == maximum,
                "A maximum finish receipt did not correspond to exact stored knowledge content.");
        }
    }).ConfigureAwait(false);
}

static string MaximumControlText()
{
    const string pattern = "\0\u0001\b\f\r\n\tΩ😀";
    var builder = new StringBuilder(CortexV2Limits.MaximumContentCharacters);
    while (builder.Length + pattern.Length <= CortexV2Limits.MaximumContentCharacters)
        builder.Append(pattern);
    builder.Append('x', CortexV2Limits.MaximumContentCharacters - builder.Length);
    return builder.ToString();
}

static async Task BackupRestoreRoundTripAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-recovery-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "source", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var backupPath = Path.Combine(root, "backup", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var restoredPath = Path.Combine(root, "restored", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var exact = Encoding.UTF8.GetBytes("backup\r\n\tNUL\0 Ω 😀");
        SqliteCortexV2BackupReceipt backup;
        await using (var source = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(sourcePath)))
        {
            await source.InitializeAsync().ConfigureAwait(false);
            await source.AppendAsync(Request("record", 0, "backup-create", exact)).ConfigureAwait(false);
            await source.AppendAsync(Request(
                "record", 1, "backup-metadata", content: null, attributes: "{\"verified\":true}"))
                .ConfigureAwait(false);
            backup = await source.BackupAsync(backupPath).ConfigureAwait(false);
        }
        Require(backup.BackupPath == backupPath && backup.RevisionRows == 2 && backup.HeadRows == 1 &&
                backup.BackupBytes > 0 && backup.Sha256.Length == 64 &&
                !File.Exists(backupPath + "-wal") && !File.Exists(backupPath + "-shm"),
            "The online Cortex v2 backup receipt or consolidated files are incorrect.");

        var restored = await SqliteCortexV2Recovery.RestoreNewAsync(backupPath, restoredPath)
            .ConfigureAwait(false);
        Require(restored.DestinationDatabasePath == restoredPath && restored.RevisionRows == 2 &&
                restored.Sha256 == backup.Sha256 && restored.RestoredBytes == backup.BackupBytes,
            "The create-only Cortex v2 restore receipt did not match its verified backup.");
        await using var opened = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(restoredPath));
        var record = await opened.ReadHeadAsync("team-alpha", "knowledge", "record").ConfigureAwait(false);
        Require(record is { Revision: 2, AttributesJson: "{\"verified\":true}" } &&
                record.ContentUtf8.SequenceEqual(exact) &&
                (await opened.FullDoctorAsync().ConfigureAwait(false)).Healthy,
            "The restored Cortex v2 store did not retain exact content, metadata, and integrity.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task BackupRequiresExistingSourceAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-backup-absent-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "source", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var backupPath = Path.Combine(root, "backup", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        await using var source = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(sourcePath));
        await ExpectAsync<FileNotFoundException>(() => source.BackupAsync(backupPath).AsTask());
        Require(!File.Exists(sourcePath) && !File.Exists(backupPath),
            "Backing up an uninitialized V2 path created central state or a destination.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task BackupRestoreFailuresAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-recovery-failures-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "source", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var backupPath = Path.Combine(root, "backup", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        await using (var source = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(sourcePath)))
        {
            await source.InitializeAsync().ConfigureAwait(false);
            await source.AppendAsync(Request("record", 0, "recovery-create", Encoding.UTF8.GetBytes("body")))
                .ConfigureAwait(false);
            await source.BackupAsync(backupPath).ConfigureAwait(false);
            await ExpectAsync<IOException>(() => source.BackupAsync(backupPath).AsTask());
        }

        var corrupt = Path.Combine(root, "corrupt", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        File.Copy(backupPath, corrupt, overwrite: false);
        await using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 0;
            await stream.WriteAsync(Encoding.ASCII.GetBytes("not-sqlite-v2"));
        }
        await ExpectAsync<InvalidDataException>(() => SqliteCortexV2Recovery.RestoreNewAsync(
            corrupt, Path.Combine(root, "corrupt-destination", SqliteCortexV2StoreOptions.RequiredDatabaseFileName))
            .AsTask());

        var liveBackup = Path.Combine(root, "live", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(liveBackup)!);
        File.Copy(backupPath, liveBackup, overwrite: false);
        await File.WriteAllBytesAsync(liveBackup + "-wal", [1]).ConfigureAwait(false);
        await ExpectAsync<InvalidDataException>(() => SqliteCortexV2Recovery.RestoreNewAsync(
            liveBackup, Path.Combine(root, "live-destination", SqliteCortexV2StoreOptions.RequiredDatabaseFileName))
            .AsTask());

        var destination = Path.Combine(root, "existing", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, [1, 2, 3]).ConfigureAwait(false);
        await ExpectAsync<IOException>(() => SqliteCortexV2Recovery.RestoreNewAsync(backupPath, destination).AsTask());
        Require((await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(
                new byte[] { 1, 2, 3 }),
            "A rejected restore changed its existing destination.");

        if (OperatingSystem.IsWindows())
        {
            var lockedBackup = Path.Combine(root, "locked", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(lockedBackup)!);
            File.Copy(backupPath, lockedBackup, overwrite: false);
            var lockedDestination = Path.Combine(
                root, "locked-destination", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
            await using var lockStream = new FileStream(
                lockedBackup, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await ExpectAsync<IOException>(() => SqliteCortexV2Recovery.RestoreNewAsync(
                lockedBackup, lockedDestination).AsTask());
            Require(!File.Exists(lockedDestination), "A rejected locked restore created its destination.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task MutationReceiptPreflightAsync()
{
    await WithOperationalStoreAsync(async (app, _) =>
    {
        var escaped = new string('\\', CortexV2Limits.MaximumReceiptTitleCharacters);
        var rejected = new CortexKnowledgeCommandV2(
            "receipt-preflight", CortexKnowledgeActionV2.Create, "knowledge", null, "knowledge-create",
            CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Verified,
            escaped, "exact body", [],
            new(CortexProvenanceSourceV2.Agent, "source", null), null,
            MaxUtf8Bytes: CortexV2Limits.MinimumResponseUtf8Bytes);
        await ExpectAsync<CortexV2ResponseBudgetExceededException>(() =>
            app.ExecuteKnowledgeAsync(rejected, default).AsTask());
        await ExpectAsync<KeyNotFoundException>(() => app.ReadAsync(new(
            "receipt-preflight", CortexRecordTypeV2.Knowledge, "knowledge", null), default).AsTask());

        var accepted = rejected with
        {
            MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes,
            IdempotencyKey = "knowledge-create-accepted"
        };
        var result = await app.ExecuteKnowledgeAsync(accepted, default).ConfigureAwait(false);
        Require(result.Record is { Revision: 1 },
            "A sufficient compact receipt budget did not allow the knowledge mutation.");
        var exact = (CortexKnowledgeV2)(await app.ReadAsync(new(
            "receipt-preflight", CortexRecordTypeV2.Knowledge, "knowledge", null), default)
            .ConfigureAwait(false)).Record!;
        Require(exact.Title == escaped && exact.Content == "exact body",
            "A preflighted compact mutation did not retain its exact full record.");
    }).ConfigureAwait(false);
}

static CortexTaskCommandV2 TaskCommand(
    string ns,
    CortexTaskActionV2 action,
    string taskId,
    long? expectedRevision,
    string idempotencyKey,
    string? title = null,
    string? content = null,
    string? assignee = null,
    string? claimedBy = null,
    string? reason = null,
    string? dependencyId = null,
    string? phaseId = null,
    string? checkpointSubject = null) => new(
        ns, action, taskId, expectedRevision, idempotencyKey, title, content,
        assignee, claimedBy, reason, dependencyId, Dependencies: [],
        FirstRevision: null, LastRevision: null, AfterTaskId: null,
        PhaseId: phaseId, CheckpointSubject: checkpointSubject);

static CortexFinishRequestV2 FinishRequest(
    string ns,
    long expectedTaskRevision,
    string taskKey,
    CortexFinishKnowledgeWriteV2 knowledge) => new(
        ns, "task", expectedTaskRevision, CortexFinishTaskActionV2.Complete,
        TaskTitle: "Finished task", TaskContent: "Final task body", BlockedReason: null,
        Checkpoint: "checkpoint", Evidence: "tested", Handoff: null,
        Knowledge: [knowledge], IdempotencyKey: taskKey);

static SqliteCortexV2AppendRequest Request(
    string recordId,
    long expectedRevision,
    string idempotencyKey,
    byte[]? content,
    string ns = "team-alpha",
    string kind = "knowledge",
    string title = "Record title",
    IReadOnlyList<string>? tags = null,
    string summary = "record summary",
    string attributes = "{}") =>
    new(
        ns,
        kind,
        recordId,
        expectedRevision,
        idempotencyKey,
        "os_test",
        expectedRevision == 0 ? "create" : "update",
        title,
        tags ?? ["test"],
        summary,
        content,
        Searchable: true,
        AttributesJson: attributes);

static async Task ExistingStoreDefaultCapacityAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-capacity-default-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var options = new SqliteCortexV2StoreOptions(database);
        var content = Encoding.UTF8.GetBytes("pre-existing default capacity record\r\nΩ");
        await using (var creator = new SqliteCortexV2Store(options))
        {
            await creator.InitializeAsync().ConfigureAwait(false);
            await creator.AppendAsync(Request("capacity-default", 0, "capacity-default-create", content))
                .ConfigureAwait(false);
        }

        await using var reopened = new SqliteCortexV2Store(options);
        var capacity = await reopened.CapacityAsync().ConfigureAwait(false);
        Require(capacity.MaxPageCount == options.MaximumDatabasePages,
            $"The pre-existing default store reported {capacity.MaxPageCount} pages instead of the configured {options.MaximumDatabasePages} pages.");
        var exact = await reopened.ReadHeadAsync("team-alpha", "knowledge", "capacity-default")
            .ConfigureAwait(false);
        Require(exact is not null && exact.ContentUtf8.SequenceEqual(content),
            "Applying the default page ceiling changed pre-existing exact content.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task ExistingStoreExpandedCapacityAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-capacity-expanded-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var defaultOptions = new SqliteCortexV2StoreOptions(database);
        await using (var creator = new SqliteCortexV2Store(defaultOptions))
            await creator.InitializeAsync().ConfigureAwait(false);

        var expandedOptions = defaultOptions with { MaximumDatabasePages = 4_194_304 };
        await using var expanded = new SqliteCortexV2Store(expandedOptions);
        var capacity = await expanded.CapacityAsync().ConfigureAwait(false);
        Require(capacity.MaxPageCount == expandedOptions.MaximumDatabasePages,
            $"The explicit expanded profile reported {capacity.MaxPageCount} pages instead of {expandedOptions.MaximumDatabasePages} pages.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task ExistingStoreCapacityFloorAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-capacity-floor-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var defaultOptions = new SqliteCortexV2StoreOptions(database);
        var random = new Random(42);
        var content = new byte[1_500_000];
        for (var index = 0; index < content.Length; index++)
            content[index] = (byte)random.Next(32, 127);

        await using (var creator = new SqliteCortexV2Store(defaultOptions))
        {
            await creator.InitializeAsync().ConfigureAwait(false);
            var result = await creator.AppendAsync(
                Request("capacity-floor", 0, "capacity-floor-create", content) with { Searchable = false })
                .ConfigureAwait(false);
            Require(result.Disposition == SqliteCortexV2WriteDispositions.Appended,
                "The pre-existing capacity-floor fixture was not created.");
        }

        var constrainedOptions = defaultOptions with { MaximumDatabasePages = 256 };
        await using var constrained = new SqliteCortexV2Store(constrainedOptions);
        var capacity = await constrained.CapacityAsync().ConfigureAwait(false);
        Require(capacity.PageCount > constrainedOptions.MaximumDatabasePages,
            "The capacity-floor fixture did not exceed the deliberately small configured ceiling.");
        Require(capacity.MaxPageCount == capacity.PageCount,
            $"The effective ceiling {capacity.MaxPageCount} did not preserve the current {capacity.PageCount}-page database floor.");
        var exact = await constrained.ReadHeadAsync("team-alpha", "knowledge", "capacity-floor")
            .ConfigureAwait(false);
        Require(exact is not null && exact.ContentUtf8.SequenceEqual(content),
            "Applying the current-size floor changed pre-existing exact content.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task WithStoreAsync(Func<string, SqliteCortexV2Store, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        await using var store = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(database),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)));
        await store.InitializeAsync().ConfigureAwait(false);
        await action(root, store).ConfigureAwait(false);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task WithOperationalStoreAsync(
    Func<CortexV2Application, SqliteCortexV2OperationalStore, Task> action)
    => await WithOperationalLowLevelStoreAsync(
        (app, operational, _) => action(app, operational)).ConfigureAwait(false);

static async Task WithOperationalLowLevelStoreAsync(
    Func<CortexV2Application, SqliteCortexV2OperationalStore, SqliteCortexV2Store, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), $"cortex-v2-operational-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var time = new IncrementingTimeProvider(new DateTimeOffset(2026, 8, 13, 1, 2, 3, TimeSpan.Zero));
        var lowLevel = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(Path.Combine(
                root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName)), time);
        await using var operational = new SqliteCortexV2OperationalStore(lowLevel, "2.0.0-test");
        await lowLevel.InitializeAsync().ConfigureAwait(false);
        var app = new CortexV2Application(operational, "os_test", time);
        await action(app, operational, lowLevel).ConfigureAwait(false);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class IncrementingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks;

    public override DateTimeOffset GetUtcNow() =>
        start.AddTicks(Interlocked.Increment(ref _ticks));
}
