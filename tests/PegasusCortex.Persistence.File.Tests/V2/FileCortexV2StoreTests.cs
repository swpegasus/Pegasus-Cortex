using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.File.V2;

namespace Pegasus.Cortex.Persistence.File.Tests.V2;

internal static class FileCortexV2StoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");

    public static async Task ExactContentAndProjectionAsync()
    {
        var root = CreateRoot();
        try
        {
            var app = await CreateApplicationAsync(root).ConfigureAwait(false);
            const string exact = "line one\r\n\tline two\nUnicode: Καλημέρα 世界 👩🏽‍💻\r\ncontrol:\u0000\u0001 end\n```csharp\nvar x = \"ok\";\n```";
            var created = await app.ExecuteKnowledgeAsync(new CortexKnowledgeCommandV2(
                "project.test", CortexKnowledgeActionV2.Create, "correction.exact", null,
                "idem-exact", CortexKnowledgeKindV2.Correction, CortexKnowledgeTierV2.Explicit,
                "Exact content", exact, ["explicit"],
                new CortexProvenanceInputV2(CortexProvenanceSourceV2.User, "test-source", "Direct correction."),
                null), CancellationToken.None).ConfigureAwait(false);
            Require(created.Record is { RecordId: "correction.exact", Revision: 1 },
                "Create did not return the compact knowledge receipt.");

            var read = await app.ReadAsync(new CortexReadRequestV2(
                "project.test", CortexRecordTypeV2.Knowledge, "correction.exact", 1),
                CancellationToken.None).ConfigureAwait(false);
            Require(read.Record is CortexKnowledgeV2 knowledge && knowledge.Content == exact,
                "Read did not round-trip CRLF/LF, tabs, Unicode, Markdown, or controls exactly.");

            var budgetBody = "exact\r\nbody\tΩ\u0001";
            var budgetCommand = new CortexKnowledgeCommandV2(
                "project.test", CortexKnowledgeActionV2.Create, "budget.preflight", null,
                "idem-budget-preflight", CortexKnowledgeKindV2.Correction, CortexKnowledgeTierV2.Explicit,
                new string('\u0001', CortexV2Limits.MaximumReceiptTitleCharacters), budgetBody, [],
                new CortexProvenanceInputV2(CortexProvenanceSourceV2.User, null, null), null,
                CortexV2Limits.MinimumResponseUtf8Bytes);
            await RequireThrowsAsync<CortexV2ResponseBudgetExceededException>(
                () => app.ExecuteKnowledgeAsync(budgetCommand, CancellationToken.None).AsTask(),
                "An oversized escaped receipt was committed before response-budget preflight.").ConfigureAwait(false);
            await RequireThrowsAsync<KeyNotFoundException>(
                () => app.ReadAsync(new CortexReadRequestV2(
                    "project.test", CortexRecordTypeV2.Knowledge, "budget.preflight", 1),
                    CancellationToken.None).AsTask(),
                "A response-budget failure committed the knowledge revision.").ConfigureAwait(false);
            var budgetCreated = await app.ExecuteKnowledgeAsync(
                budgetCommand with { MaxUtf8Bytes = CortexV2Limits.DefaultResponseUtf8Bytes },
                CancellationToken.None).ConfigureAwait(false);
            var budgetRead = await app.ReadAsync(new CortexReadRequestV2(
                "project.test", CortexRecordTypeV2.Knowledge, "budget.preflight", 1),
                CancellationToken.None).ConfigureAwait(false);
            Require(budgetCreated.Record is { RecordId: "budget.preflight", Revision: 1 } &&
                budgetRead.Record is CortexKnowledgeV2 budgetKnowledge && budgetKnowledge.Content == budgetBody,
                "A sufficient compact receipt budget did not preserve the exact knowledge body for typed read.");

            var hotPath = Path.Combine(root, ".cortex", "HOT.json");
            var hotBytes = await System.IO.File.ReadAllBytesAsync(hotPath).ConfigureAwait(false);
            var hot = JsonSerializer.Deserialize<CortexFileHotV2>(hotBytes, CortexV2Json.Options);
            Require(hotBytes.Length <= 4096 && hot is { Generated: true, Authoritative: false },
                "HOT.json exceeded 4096 bytes or was not marked generated/non-authoritative.");

            var firstStart = await app.StartAsync(new CortexStartRequestV2(
                ["project.test"], "exact correction", null,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-a"), CancellationToken.None).ConfigureAwait(false);
            var unchangedStart = await app.StartAsync(new CortexStartRequestV2(
                ["project.test"], "exact correction", firstStart.ContextCursor,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-a"),
                CancellationToken.None).ConfigureAwait(false);
            var differentIntent = await app.StartAsync(new CortexStartRequestV2(
                ["project.test"], "different intent", firstStart.ContextCursor,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-a"),
                CancellationToken.None).ConfigureAwait(false);
            var differentTask = await app.StartAsync(new CortexStartRequestV2(
                ["project.test"], "exact correction", firstStart.ContextCursor,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-b"),
                CancellationToken.None).ConfigureAwait(false);
            var differentNamespace = await app.StartAsync(new CortexStartRequestV2(
                ["shared.programming"], "exact correction", firstStart.ContextCursor,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task-a"),
                CancellationToken.None).ConfigureAwait(false);
            Require(unchangedStart.Disposition == CortexContextDispositionV2.Unchanged &&
                differentIntent.Disposition == CortexContextDispositionV2.Snapshot &&
                differentIntent.ContextCursor != firstStart.ContextCursor &&
                differentTask.Disposition == CortexContextDispositionV2.Snapshot &&
                differentTask.ContextCursor != firstStart.ContextCursor &&
                differentNamespace.Disposition == CortexContextDispositionV2.Snapshot &&
                differentNamespace.ContextCursor != firstStart.ContextCursor &&
                JsonSerializer.SerializeToUtf8Bytes(firstStart, CortexV2Json.Options).Length <=
                    CortexV2Limits.DefaultStartResponseUtf8Bytes,
                "Start context was not bounded or its cursor was not scoped to exact intent, task, and namespace sequence.");

            var controlHeavy = new string('\u0001', CortexV2Limits.MaximumContentCharacters);
            var controlCreated = await app.ExecuteKnowledgeAsync(new CortexKnowledgeCommandV2(
                "project.test", CortexKnowledgeActionV2.Create, "control.max", null,
                "idem-control-max", CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Explicit,
                "Maximum control content", controlHeavy, [],
                new CortexProvenanceInputV2(CortexProvenanceSourceV2.User, null, null), null),
                CancellationToken.None).ConfigureAwait(false);
            var controlRead = await app.ReadAsync(new CortexReadRequestV2(
                "project.test", CortexRecordTypeV2.Knowledge, "control.max", 1,
                CortexV2Limits.MaximumResponseUtf8Bytes), CancellationToken.None).ConfigureAwait(false);
            Require(controlCreated.Record is { RecordId: "control.max", Revision: 1 } &&
                controlRead.Record is CortexKnowledgeV2 controlKnowledge && controlKnowledge.Content == controlHeavy,
                "The maximum control-heavy contract content did not round-trip exactly.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task LifecycleSearchAndAtomicFinishAsync()
    {
        var root = CreateRoot();
        try
        {
            var app = await CreateApplicationAsync(root).ConfigureAwait(false);
            var task = await app.ExecuteTaskAsync(TaskCommand(
                CortexTaskActionV2.Create, "task.alpha", null, "task-create", "Alpha", "Do the work."),
                CancellationToken.None).ConfigureAwait(false);
            Require(task.Task is { Revision: 1, Status: CortexTaskStatusV2.Open }, "Task create failed.");
            var immediateTaskSearch = await app.SearchAsync(new CortexSearchRequestV2(
                ["project.test"], "Alpha work", null, null), CancellationToken.None).ConfigureAwait(false);
            Require(immediateTaskSearch.Items.Any(value =>
                value is { RecordType: CortexRecordTypeV2.Task, RecordId: "task.alpha", Kind: null, Tier: null }),
                "A new task was not immediately searchable as a typed task hit.");
            var kindFilteredSearch = await app.SearchAsync(new CortexSearchRequestV2(
                ["project.test"], "Alpha work", [CortexKnowledgeKindV2.Tenet], null),
                CancellationToken.None).ConfigureAwait(false);
            Require(kindFilteredSearch.Items.All(value => value.RecordType == CortexRecordTypeV2.Knowledge),
                "A knowledge-kind filter did not exclude task hits.");

            var finishRequest = new CortexFinishRequestV2(
                "project.test", "task.alpha", 1, CortexFinishTaskActionV2.Complete,
                "Alpha finished", "Exact final task body Ω\r\nline two", null,
                "Checkpoint Ω", "Evidence\r\nline two", "Handoff\tready",
                [new CortexFinishKnowledgeWriteV2(
                    "workaround.alpha", null, CortexKnowledgeKindV2.Workaround,
                    CortexKnowledgeTierV2.Verified, "Verified workaround", "Use Ω\r\nexactly.",
                    ["verified"],
                    new CortexProvenanceInputV2(CortexProvenanceSourceV2.User, null, "Observed pass."),
                    "workaround-create")],
                "finish-alpha");
            var finish = await app.FinishAsync(finishRequest, CancellationToken.None).ConfigureAwait(false);
            Require(finish.Task is { Revision: 2, Status: CortexTaskStatusV2.Completed } &&
                finish.Knowledge is [{ RecordId: "workaround.alpha", Revision: 1 }],
                "Atomic finish did not return its task and knowledge writes.");
            var finishedTask = await app.ReadAsync(new CortexReadRequestV2(
                "project.test", CortexRecordTypeV2.Task, "task.alpha", 2), CancellationToken.None)
                .ConfigureAwait(false);
            Require(finishedTask.Record is CortexTaskV2
                {
                    Title: "Alpha finished",
                    Content: "Exact final task body Ω\r\nline two",
                    Revision: 2,
                    Status: CortexTaskStatusV2.Completed
                }, "Atomic File finish did not preserve the supplied exact task title and body.");

            var search = await app.SearchAsync(new CortexSearchRequestV2(
                ["project.test"], "verified workaround", [CortexKnowledgeKindV2.Workaround], null),
                CancellationToken.None).ConfigureAwait(false);
            Require(search.Items.Any(value => value.RecordId == "workaround.alpha"),
                "Finish knowledge was not immediately searchable.");
            var completedTaskSearch = await app.SearchAsync(new CortexSearchRequestV2(
                ["project.test"], "Alpha work", null, null), CancellationToken.None).ConfigureAwait(false);
            Require(completedTaskSearch.Items.Any(value =>
                value is { RecordType: CortexRecordTypeV2.Task, RecordId: "task.alpha", Revision: 2 }),
                "A completed task was not immediately searchable at its current revision.");
            var start = await app.StartAsync(new CortexStartRequestV2(
                ["project.test"], "continue alpha", null,
                CortexV2Limits.DefaultStartResponseUtf8Bytes, "task.alpha"),
                CancellationToken.None).ConfigureAwait(false);
            Require(start.CurrentTask is
                { TaskId: "task.alpha", Revision: 2, Status: CortexTaskStatusV2.Completed } &&
                start.Items.All(value => value.RecordType != "finish") &&
                start.Items.Count <= 4,
                "Start did not return the exact compact current task separately from ranked context.");
            var replay = await app.FinishAsync(finishRequest, CancellationToken.None).ConfigureAwait(false);
            Require(replay.Disposition == "idempotent" && replay.Task.Revision == 2,
                "Finish idempotency did not replay the committed generation.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task PortableBehaviorPackAsync()
    {
        var packRoot = FindPackRoot();
        var manifest = await CortexFileBehaviorPackManifestV2.ReadAsync(packRoot).ConfigureAwait(false);
        var records = await manifest.LoadRecordsAsync(packRoot).ConfigureAwait(false);
        Require(records.Count == 58 && manifest is { IncludesTasks: false, IncludesMachineHistory: false },
            "The reference behavior pack is not the bounded 58-record portable pack.");
        Require(records.All(value => value.Descriptor switch
            {
                { Kind: CortexKnowledgeKindV2.Tenet, Tier: CortexKnowledgeTierV2.Explicit } =>
                    value.Descriptor.Tags.Contains("generalized", StringComparer.Ordinal),
                { Kind: CortexKnowledgeKindV2.Workaround, Tier: CortexKnowledgeTierV2.Verified } => true,
                _ => false
            }),
            "The seeded records are not generalized explicit tenets or bounded verified workarounds.");
        Require(manifest.Records.All(value => !Path.IsPathFullyQualified(value.ContentPath) &&
            (value.SourceRelativePath is null || !Path.IsPathFullyQualified(value.SourceRelativePath))),
            "The behavior pack contains a machine-specific absolute path.");

        var root = CreateRoot();
        try
        {
            var store = await CreateStoreAsync(root).ConfigureAwait(false);
            var imported = await store.ImportBehaviorPackAsync(
                packRoot, "project.test", "test-user").ConfigureAwait(false);
            var replay = await store.ImportBehaviorPackAsync(
                packRoot, "project.test", "test-user").ConfigureAwait(false);
            var status = await store.StatusAsync(CancellationToken.None).ConfigureAwait(false);
            var generations = Directory.GetDirectories(Path.Combine(root, ".cortex", "v2", "generations"), "g-*");
            var indexChunks = Directory.GetFiles(generations.Single(), "index-*.json");
            Require(imported.Imported == 58 && imported.Unchanged == 0 &&
                replay.Imported == 0 && replay.Unchanged == 58 &&
                status is { HeadCount: 58, RevisionCount: 58 } &&
                generations.Length == 1 && indexChunks.Length > 1,
                "Behavior pack import was not one idempotent chunked generation.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static CortexTaskCommandV2 TaskCommand(
        CortexTaskActionV2 action,
        string id,
        long? expected,
        string idempotency,
        string? title = null,
        string? content = null) => new(
            "project.test", action, id, expected, idempotency, title, content,
            null, null, null, null, null, null, null, null, 10,
            CortexV2Limits.MaximumCompactResponseUtf8Bytes);

    private static async ValueTask<CortexV2Application> CreateApplicationAsync(string root)
    {
        var time = new FixedTimeProvider(Now);
        var store = await CreateStoreAsync(root, time).ConfigureAwait(false);
        return new CortexV2Application(store, "test-user", time);
    }

    private static async ValueTask<FileCortexV2Store> CreateStoreAsync(
        string root,
        TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(root);
        var manifest = new CortexFileProjectManifestV2(
            CortexFileProjectManifestV2.CurrentSchema,
            "test-project",
            "project.test",
            ["project.test", "shared.programming"],
            4096,
            2,
            4096);
        await manifest.WriteAsync(root).ConfigureAwait(false);
        return await FileCortexV2Store.OpenAsync(
            root,
            timeProvider ?? new FixedTimeProvider(Now)).ConfigureAwait(false);
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pegasus-cortex-file-v2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string FindPackRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "integrations", "file-cortex-v2");
            if (System.IO.File.Exists(Path.Combine(candidate, "behavior-pack.json")))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The file Cortex V2 reference pack was not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
