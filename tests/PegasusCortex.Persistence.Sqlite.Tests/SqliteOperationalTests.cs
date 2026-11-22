using Pegasus.Cortex.Core.Operations;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

internal static class SqliteOperationalTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 21, 0, 0, TimeSpan.Zero);

    public static async Task TaskLifecycleNamespaceAndConcurrencyAsync()
    {
        await WithApplicationAsync(async application =>
        {
            var created = await application.CreateTaskAsync(
                "team-alpha", "task-1", "Build local Cortex", "Initial\r\ncontent",
                null, [], "create-alpha", "os_test", CancellationToken.None);
            Require(created.Disposition == CortexRetentionWriteDispositions.Appended, "Task create was not appended.");
            var replay = await application.CreateTaskAsync(
                "team-alpha", "task-1", "Build local Cortex", "Initial\r\ncontent",
                null, [], "create-alpha", "os_test", CancellationToken.None);
            Require(replay.Disposition == CortexRetentionWriteDispositions.Idempotent, "Task create replay was not idempotent.");

            var otherNamespace = await application.CreateTaskAsync(
                "team-beta", "task-1", "Independent task", "Other namespace",
                null, [], "create-beta", "os_test", CancellationToken.None);
            Require(otherNamespace.Disposition == CortexRetentionWriteDispositions.Appended, "The second namespace was not isolated.");

            var assigned = await application.MutateTaskAsync(
                "team-alpha", "task-1", 1, "assign", null, null,
                "agent-a", null, null, null, "assign-1", "os_test",
                CancellationToken.None);
            Require(assigned.Revision == 2, "Task assignment did not advance revision.");
            var assignedReplay = await application.MutateTaskAsync(
                "team-alpha", "task-1", 1, "assign", null, null,
                "agent-a", null, null, null, "assign-1", "os_test",
                CancellationToken.None);
            Require(assignedReplay.Disposition == CortexRetentionWriteDispositions.Idempotent && assignedReplay.Revision == 2,
                "Task mutation replay was not idempotent after the head advanced.");
            var stale = await application.MutateTaskAsync(
                "team-alpha", "task-1", 1, "complete", null, null,
                null, null, null, null, "stale-complete", "os_test",
                CancellationToken.None);
            Require(stale.Disposition == "stale-revision" && stale.Revision == 2, "A stale expected revision was not rejected.");

            var dependency = await application.MutateTaskAsync(
                "team-alpha", "task-1", 2, "add-dependency", null, null,
                null, null, null, "task-0", "dependency-1", "os_test",
                CancellationToken.None);
            Require(dependency.Revision == 3, "Dependency append did not advance revision.");
            var blocked = await application.MutateTaskAsync(
                "team-alpha", "task-1", 3, "block", null, null,
                null, null, "Waiting for task-0", null, "block-1", "os_test",
                CancellationToken.None);
            Require(blocked.Revision == 4, "Task block did not advance revision.");
            var completed = await application.MutateTaskAsync(
                "team-alpha", "task-1", 4, "complete", null, null,
                null, null, null, null, "complete-1", "os_test",
                CancellationToken.None);
            Require(completed.Revision == 5, "Task completion did not advance revision.");
            var reopened = await application.MutateTaskAsync(
                "team-alpha", "task-1", 5, "reopen", null, null,
                null, null, null, null, "reopen-1", "os_test",
                CancellationToken.None);
            Require(reopened.Revision == 6, "Task reopen did not advance revision.");
            var archived = await application.MutateTaskAsync(
                "team-alpha", "task-1", 6, "archive", null, null,
                null, null, null, null, "archive-1", "os_test",
                CancellationToken.None);
            Require(archived.Revision == 7, "Task archive did not advance revision.");

            var head = await application.ReadTaskAsync("team-alpha", "task-1", CancellationToken.None);
            Require(head is { Status: CortexTaskStatuses.Archived, Revision: 7 }, "The task head is incorrect.");
            Require(head!.Dependencies.SequenceEqual(["task-0"]), "The dependency was not preserved.");
            var beta = await application.ReadTaskAsync("team-beta", "task-1", CancellationToken.None);
            Require(beta is { Title: "Independent task", Revision: 1 }, "Namespace isolation returned the wrong task.");
            var history = await application.ReadTaskHistoryAsync(
                "team-alpha", "task-1", 1, 7, CancellationToken.None);
            Require(history.Count == 7 && history.Select(item => item.Revision).SequenceEqual(Enumerable.Range(1, 7).Select(value => (long)value)), "Task history is not complete and ordered.");
            var page = await application.ListTasksAsync("team-alpha", null, 10, CancellationToken.None);
            Require(page.Namespace == "team-alpha" && page.Items.Count == 1, "Task listing did not echo and isolate the namespace.");
        });
    }

    public static async Task KnowledgeRoundTripSearchAndRevisionAsync()
    {
        await WithApplicationAsync(async application =>
        {
            const string original = "first\r\nsecond\n\tUnicode Ω 😀\u0000end\n```c#\nvar x = 1;\n```";
            var created = await application.CreateKnowledgeAsync(
                "knowledge-alpha", "record-1", "Multiline record", original,
                ["failure", "unicode"], "knowledge-create", "os_test",
                CancellationToken.None);
            Require(created.Disposition == CortexRetentionWriteDispositions.Appended, "Knowledge create was not appended.");
            var read = await application.ReadKnowledgeAsync(
                "knowledge-alpha", "record-1", CancellationToken.None);
            Require(read is not null && string.Equals(read.Content, original, StringComparison.Ordinal), "Knowledge content did not round-trip exactly.");
            var immediate = await application.SearchKnowledgeAsync(
                "knowledge-alpha", "Unicode", 10, CancellationToken.None);
            Require(immediate.Count == 1 && immediate[0].RecordId == "record-1", "Knowledge was not immediately searchable.");
            var isolated = await application.SearchKnowledgeAsync(
                "knowledge-beta", "Unicode", 10, CancellationToken.None);
            Require(isolated.Count == 0, "Knowledge search leaked across namespaces.");

            const string revisedContent = "corrected\ncontent\twith CRLF\r\nand 漢字";
            var revised = await application.MutateKnowledgeAsync(
                "knowledge-alpha", "record-1", 1, "revise",
                "Corrected record", revisedContent, ["correction"], null,
                "knowledge-revise", "os_test", CancellationToken.None);
            Require(revised.Revision == 2, "Knowledge revision did not advance.");
            var revisedReplay = await application.MutateKnowledgeAsync(
                "knowledge-alpha", "record-1", 1, "revise",
                "Corrected record", revisedContent, ["correction"], null,
                "knowledge-revise", "os_test", CancellationToken.None);
            Require(revisedReplay.Disposition == CortexRetentionWriteDispositions.Idempotent && revisedReplay.Revision == 2,
                "Knowledge mutation replay was not idempotent after the head advanced.");
            var corrected = await application.SearchKnowledgeAsync(
                "knowledge-alpha", "corrected", 10, CancellationToken.None);
            Require(corrected.Count == 1 && corrected[0].Revision == 2 && corrected[0].Content == revisedContent, "Revised knowledge was not immediately searchable.");
            var stale = await application.MutateKnowledgeAsync(
                "knowledge-alpha", "record-1", 1, "archive",
                null, null, null, null, "knowledge-stale", "os_test",
                CancellationToken.None);
            Require(stale.Disposition == "stale-revision" && stale.Revision == 2, "Stale knowledge revision was not rejected.");
            var superseded = await application.MutateKnowledgeAsync(
                "knowledge-alpha", "record-1", 2, "supersede",
                null, null, null, "record-2", "knowledge-supersede", "os_test",
                CancellationToken.None);
            Require(superseded.Revision == 3, "Knowledge supersession did not advance.");
            var hidden = await application.SearchKnowledgeAsync(
                "knowledge-alpha", "corrected", 10, CancellationToken.None);
            Require(hidden.Count == 0, "Superseded knowledge remained in normal local search results.");
            var archived = await application.MutateKnowledgeAsync(
                "knowledge-alpha", "record-1", 3, "archive",
                null, null, null, null, "knowledge-archive", "os_test",
                CancellationToken.None);
            Require(archived.Revision == 4, "Knowledge archive did not advance.");
            var final = await application.ReadKnowledgeAsync(
                "knowledge-alpha", "record-1", CancellationToken.None);
            Require(final is { State: CortexKnowledgeStates.Archived, Revision: 4 }, "Knowledge archive head is incorrect.");
        });
    }

    private static async Task WithApplicationAsync(
        Func<CortexOperationalApplicationService, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-operational-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.GetFullPath(Path.Combine(root, "data", "cortex.db"));
            await using var store = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(database),
                new FixedTimeProvider(Now));
            var application = new CortexOperationalApplicationService(
                store,
                new AdvancingTimeProvider(Now));
            await test(application);
            var integrity = await store.CheckIntegrityAsync(CancellationToken.None);
            Require(integrity.Healthy, "The operational store failed semantic integrity.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => now.AddTicks(Interlocked.Increment(ref _ticks));
    }
}
