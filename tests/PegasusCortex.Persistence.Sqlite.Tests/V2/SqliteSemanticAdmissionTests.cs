using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class SqliteSemanticAdmissionTests
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("cortex-semantic-admission-").FullName;
        try
        {
            var path = Path.Combine(root, "data", SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
            await using (var store = new SqliteCortexV2Store(new(path)))
            {
                var before = Files(root);
                var bad = Append("rejected", "password=\"fixture-secret\"");
                await RejectedAsync(() => store.AppendAsync(bad).AsTask());
                Require(Files(root).SequenceEqual(before), "rejected append initialized database/blob/backup state");
                var good = Append("retained", "Factual note");
                await store.AppendAsync(good);
                await RejectedAsync(() => store.AppendAsync(good with { ContentUtf8 = bad.ContentUtf8 }).AsTask());
                var retained = await store.ReadHeadAsync("project-a", "knowledge", "retained");
                Require(retained is { Revision: 1 }, "rejected idempotent-key input changed an existing revision");

                var escaped = "{\"pass\\u0077ord\":\"fixture-secret\"}";
                await RejectedAsync(() => store.AppendBatchAsync([Append("first-in-batch", "Safe"),
                    Append("last-in-batch", "Safe") with { AttributesJson = escaped }]).AsTask());
                Require(await store.ReadHeadAsync("project-a", "knowledge", "first-in-batch") is null &&
                    await store.ReadHeadAsync("project-a", "knowledge", "last-in-batch") is null,
                    "a rejected later batch item committed another item");

                var source = new CortexInvocationSourceV2("approved-mcp-connection", "cortex.mcp/11111111111111111111111111111111",
                    "22222222222222222222222222222222", 3, "Fixture client");
                var app = new CortexV2Application(new SqliteCortexV2OperationalStore(store, "fixture"), "os_fixture");
                var create = new CortexTaskCommandV2("project-a", CortexTaskActionV2.Create, "task", null, "task-create", "Task", "Safe",
                    "declared-owner", null, null, null, [], null, null, null);
                var created = await app.ExecuteTaskAsync(create, source, default);
                var createdTask = created.Task ?? throw new InvalidOperationException("missing created task");
                Require(createdTask.Assignee == "declared-owner" && createdTask.VerifiedSource == source &&
                    createdTask.ParticipantIdentityKind == "declared-label", "SQLite task receipt conflated label and verified source");
                var claimed = await app.ExecuteTaskAsync(create with { Action = CortexTaskActionV2.Claim, ExpectedRevision = 1,
                    IdempotencyKey = "task-claim", Title = null, Content = null, ClaimedBy = "reported-agent" }, source, default);
                var claimedTask = claimed.Task ?? throw new InvalidOperationException("missing claimed task");
                Require(claimedTask.ClaimedBy == "reported-agent" && claimedTask.VerifiedSource == source,
                    "claim label was rejected or used as transport identity");
                var history = await app.ExecuteTaskAsync(create with { Action = CortexTaskActionV2.History, FirstRevision = 1,
                    LastRevision = 2, IdempotencyKey = null, Title = null, Content = null }, default);
                var events = history.History ?? throw new InvalidOperationException("missing task history");
                Require(events.Count == 2 && events.All(item => item.VerifiedSource == source),
                    "task history dropped the actual write source");
            }
            await using (var reopened = new SqliteCortexV2Store(new(path)))
            {
                var task = await reopened.ReadHeadAsync("project-a", "task", "task");
                using var attributes = JsonDocument.Parse(task!.AttributesJson);
                var provenance = attributes.RootElement.GetProperty("provenance");
                Require(provenance.GetProperty("actor").GetString() == "os_fixture" &&
                    provenance.GetProperty("verifiedSource").GetProperty("subject").GetString() == "22222222222222222222222222222222",
                    "verified source did not survive storage reopen");
                Require(attributes.RootElement.GetProperty("claimedBy").GetString() == "reported-agent", "declared claim label did not survive reopen");
            }
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-semantic-admission-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture cleanup path changed.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static SqliteCortexV2AppendRequest Append(string id, string content) => new("project-a", "knowledge", id, 0,
        "write-" + id, "os_fixture", "created", "Knowledge", [], "Factual summary", Encoding.UTF8.GetBytes(content));
    private static string[] Files(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    private static async Task RejectedAsync(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("prohibited append accepted"); }
        catch (CortexRetentionContentRejectedException error)
        { Require(error.FindingCodes.Count > 0 && !error.ToString().Contains("fixture-secret", StringComparison.Ordinal), "append rejection echoed content"); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
