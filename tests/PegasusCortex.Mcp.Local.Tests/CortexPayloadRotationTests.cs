using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadRotationTests
{
    private const string Space = "project.rotation";
    private static readonly DateTimeOffset Old = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Cutoff = Old.AddDays(30);
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-rotation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await AgeAndProtectionAsync(Path.Combine(root, "age"));
            await ReferencesAsync(Path.Combine(root, "references"));
            await PaginationAndBoundsAsync(Path.Combine(root, "paging"));
            await ChangedPreconditionsAsync(Path.Combine(root, "change"));
            await KnowledgeReferencesAsync(Path.Combine(root, "knowledge"));
            await PinBetweenPlanAndWriteAsync(Path.Combine(root, "pin-race"));
            await MalformedHeadsAsync(Path.Combine(root, "heads"));
            await MissingHistoricalBlobAsync(Path.Combine(root, "historical-blob"));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("cortex-rotation-", StringComparison.Ordinal), "owned scratch boundary");
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "owned entries are ordinary");
            Directory.Delete(root, recursive: true);
        }
        Check(!Directory.Exists(root), "owned stores and cold files released");
        Console.WriteLine($"PASS payload rotation: {_checks} owned SQLite/file checks; age/server-time floor, namespace/record protection, task/knowledge and transitive references, soft byte target, bounded candidate pages, pin/selection races, malformed heads/history refusal and exact cold reads; no installed migration or automatic owner scheduling acceptance");
    }

    private static async Task AgeAndProtectionAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db"), EnablePayloadOffload: true), clock);
        _ = await store.AppendAsync(Seed("old", "exclusive eligible bytes"));
        _ = await store.AppendAsync(Seed("pinned", "exclusive pinned bytes"));
        _ = await store.AppendAsync(Seed("active", "exclusive active bytes", archived: false));
        _ = await store.AppendAsync(Seed("recent", "exclusive recent bytes", date: Cutoff.AddDays(1)));
        clock.Now = Cutoff.AddDays(2);
        _ = await store.AppendAsync(Seed("backdated", "backdated attributes but recent server stamp"));
        await store.UpgradePayloadStorageAsync(2);
        await store.SetPayloadProtectionAsync(new(1, new(Space, "task", "pinned"), true, false, 0, "pin", "fixture"));
        var request = new SqliteCortexPayloadRotationRequest(1, Space, Cutoff);
        var before = await store.GetStorageMetricsAsync();
        var plan = await store.PlanPayloadRotationAsync(request);
        Check(plan.Selection?.Records is { Count: 1 } selected && selected[0].RecordId == "old", "only old unprotected server-old archived head selected");
        Check(plan.ProtectedRecords == 1, "pin counted separately");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "planning is read only");
        var result = await store.RotateArchivedPayloadsAsync(request);
        Check(result.State == "rotated" && result.Tiering is { PayloadsChanged: 1, RecordMetadataUnchanged: true, PhysicalBytesReclaimed: 0 }, "actual bounded cold offload keeps metadata");
        Check(Encoding.UTF8.GetString((await store.ReadHeadAsync(Space, "task", "old"))!.ContentUtf8) == "exclusive eligible bytes", "exact read resolves rotated payload");
        var after = await store.GetStorageMetricsAsync();
        Check(before.HeadRows == after.HeadRows && before.RevisionRows == after.RevisionRows, "rotation never deletes records or replay rows");
        Check(result.TargetSatisfied == false, "protected active and recent data can leave zero target unmet");
        var soft = await store.RotateArchivedPayloadsAsync(request with { TargetHotCompressedBytes = 16L * 1024 * 1024 * 1024 });
        Check(soft.State == "no_selection" && soft.TargetSatisfied == true, "soft target needs no admission quota or forced eviction");
        Check((await store.FullDoctorAsync()).Healthy, "real rotated store doctor");
    }

    private static async Task ReferencesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db"), EnablePayloadOffload: true), clock);
        _ = await store.AppendAsync(Seed("needed", "referenced archived bytes"));
        _ = await store.AppendAsync(Seed("consumer", "active consumer", archived: false, dependencies: ["needed"]));
        _ = await store.AppendAsync(Seed("unresolved", "unresolved archived evidence", dependencies: ["absent"]));
        _ = await store.AppendAsync(Seed("independent", "independent archived bytes"));
        _ = await store.AppendAsync(Seed("cycle-a", "archived a", dependencies: ["cycle-b"]));
        _ = await store.AppendAsync(Seed("cycle-b", "archived b", dependencies: ["cycle-a"]));
        _ = await store.AppendAsync(Seed("chain-a", "archived chain a", dependencies: ["chain-b"]));
        _ = await store.AppendAsync(Seed("chain-b", "archived chain b"));
        _ = await store.AppendAsync(Seed("chain-live", "active chain owner", archived: false, dependencies: ["chain-a"]));
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        var plan = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
        var ids = plan.Selection!.Records.Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        Check(plan.ReferenceProtectedRecords == 4 && !ids.Contains("needed") && !ids.Contains("unresolved") &&
            !ids.Contains("chain-a") && !ids.Contains("chain-b"), "inbound active, transitive and unresolved outgoing references protect heads");
        Check(ids.SetEquals(["independent", "cycle-a", "cycle-b"]), "closed selected archived graph and independent record can rotate");
        var result = await store.RotateArchivedPayloadsAsync(new(1, Space, Cutoff));
        Check(result.Tiering?.PayloadsChanged == 3 && (await store.FullDoctorAsync()).Healthy, "reference-aware real offload retains complete metadata graph");
    }

    private static async Task PaginationAndBoundsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db"), EnablePayloadOffload: true), clock);
        var active = Enumerable.Range(0, 260).Select(i => Seed("a" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "active " + i, archived: false)).ToArray();
        Check((await store.AppendBatchAsync(active[..256])).Committed && (await store.AppendBatchAsync(active[256..])).Committed, "synthetic bounded seed batches");
        _ = await store.AppendAsync(Seed("z-old", "late archived candidate"));
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        var first = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
        Check(first.ScannedHeads == 256 && first.Selection is null && first.NextCursor?.RecordId == "a255", "candidate page limit and exact continuation");
        var next = await store.RotateArchivedPayloadsAsync(new(1, Space, Cutoff, After: first.NextCursor));
        Check(next.Plan.ScannedHeads == 5 && next.Plan.NextCursor is null && next.Tiering?.PayloadsChanged == 1, "continuation reaches later candidate without missing active-reference scan");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => store.PlanPayloadRotationAsync(new(1, Space, Cutoff), cancel.Token).AsTask(), "canceled scan");
        await ThrowsAsync<ArgumentException>(() => store.PlanPayloadRotationAsync(new(2, Space, Cutoff)).AsTask(), "closed version");
        await ThrowsAsync<ArgumentException>(() => store.PlanPayloadRotationAsync(new(1, Space, clock.Now.AddDays(1))).AsTask(), "future cutoff refused");
    }

    private static async Task ChangedPreconditionsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        var path = Path.Combine(root, "cortex-v2.db");
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), clock);
        _ = await store.AppendAsync(Seed("target", "target bytes"));
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        var plan = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
        Check(plan.Selection?.Records.Count == 1, "reviewed selection present");
        _ = await store.AppendAsync(Seed("new-consumer", "new live dependency", archived: false, dependencies: ["target"]));
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(plan.Selection!).AsTask(), "new incoming reference rechecked in eviction transaction");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "changed precondition causes no eviction");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cortex_v2_record_revision SET attributes_json='{}' WHERE record_id='new-consumer';";
        await command.ExecuteNonQueryAsync();
        await ThrowsAsync<InvalidDataException>(() => store.PlanPayloadRotationAsync(new(1, Space, Cutoff)).AsTask(), "tampered reference metadata fails closed");
    }

    private static async Task KnowledgeReferencesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db"), EnablePayloadOffload: true), clock);
        _ = await store.AppendAsync(SeedKnowledge(Space, "needed", "archived replacement target"));
        _ = await store.AppendAsync(SeedKnowledge(Space, "source", "superseded knowledge source",
            CortexKnowledgeStateV2.Superseded, "needed"));
        _ = await store.AppendAsync(Seed("needed", "independent task with same identifier"));
        _ = await store.AppendAsync(SeedKnowledge(Space, "independent", "independent knowledge"));
        // A matching ID in another namespace references its own namespace only.
        _ = await store.AppendAsync(SeedKnowledge("project.other", "independent", "other namespace target"));
        _ = await store.AppendAsync(SeedKnowledge("project.other", "source", "other namespace source",
            CortexKnowledgeStateV2.Superseded, "independent"));
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        var plan = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
        var coordinates = plan.Selection!.Records.Select(record => record.Kind + ":" + record.RecordId).ToHashSet(StringComparer.Ordinal);
        Check(plan.ReferenceProtectedRecords == 1 && !coordinates.Contains("knowledge:needed"),
            "nonselected superseded knowledge retains its same-namespace replacement target");
        Check(coordinates.SetEquals(["task:needed", "knowledge:independent"]),
            "knowledge links do not cross kind or namespace by matching identifier");
        var rotated = await store.RotateArchivedPayloadsAsync(new(1, Space, Cutoff));
        Check(rotated.Tiering?.PayloadsChanged == 2, "independent knowledge and task actually rotate");
        Check(Encoding.UTF8.GetString((await store.ReadHeadAsync(Space, "knowledge", "needed"))!.ContentUtf8) ==
            "archived replacement target", "protected knowledge remains exactly readable");
        Check((await store.FullDoctorAsync()).Healthy, "knowledge reference fixture preserves hashes and counters");
    }

    private static async Task PinBetweenPlanAndWriteAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db"), EnablePayloadOffload: true), clock);
        var target = await store.AppendAsync(Seed("target", "pin race archive"));
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        var plan = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
        Check(plan.Selection?.Records.Count == 1, "pin race has a reviewed live typed selection");
        var protection = new SqliteCortexPayloadProtectionRequest(1, new(Space, "task", "target"),
            true, false, 0, "pin-after-plan", "fixture");
        await store.SetPayloadProtectionAsync(protection);
        await ThrowsCodeAsync(() => store.OffloadArchivedPayloadsAsync(plan.Selection!).AsTask(),
            "payload_protected", "pin admitted after planning is rechecked inside original eviction transaction");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0 &&
            (await store.ReadHeadAsync(Space, "task", "target"))!.RevisionHash == target.RevisionHash,
            "pin race publishes no cold payload or semantic revision");
        Check((await store.ReadPayloadProtectionAsync(protection.Scope)).Pinned, "new pin remains committed after refused offload");
        await store.SetPayloadProtectionAsync(protection with
            { Pinned = false, ExpectedRevision = 1, IdempotencyKey = "release-after-refusal" });
        Check((await store.OffloadArchivedPayloadsAsync(plan.Selection!)).PayloadsChanged == 1,
            "same unchanged semantic selection works only after explicit pin release");
        Check((await store.FullDoctorAsync()).Healthy, "pin race and explicit release preserve store integrity");
    }

    private static async Task MalformedHeadsAsync(string root)
    {
        foreach (var corruption in new[] { "missing-revision", "content-mismatch", "rewound" })
        {
            var directory = Path.Combine(root, corruption); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "cortex-v2.db");
            var clock = new Clock(Old);
            await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), clock);
            _ = await store.AppendAsync(Seed("target", "target with malformed incoming owner"));
            clock.Now = Cutoff.AddDays(1);
            await store.UpgradePayloadStorageAsync(2);
            var plan = await store.PlanPayloadRotationAsync(new(1, Space, Cutoff));
            Check(plan.Selection?.Records.Count == 1, "original selection precedes corrupt incoming head");
            if (corruption == "rewound")
            {
                _ = await store.AppendAsync(Seed("consumer", "first archived consumer revision"));
                _ = await store.AppendAsync(Seed("consumer", "new live consumer revision", archived: false,
                    dependencies: ["target"]) with { ExpectedRevision = 1, IdempotencyKey = "consumer-revision2" });
                await SqlAsync(path, """
                    UPDATE cortex_v2_record_head SET revision=1,
                      revision_hash=(SELECT revision_hash FROM cortex_v2_record_revision r WHERE
                        r.namespace_name='project.rotation' AND r.kind='task' AND r.record_id='consumer' AND r.revision=1),
                      content_hash=(SELECT content_hash FROM cortex_v2_record_revision r WHERE
                        r.namespace_name='project.rotation' AND r.kind='task' AND r.record_id='consumer' AND r.revision=1)
                    WHERE namespace_name='project.rotation' AND kind='task' AND record_id='consumer';
                    """);
            }
            else
            {
                _ = await store.AppendAsync(Seed("consumer", "live consumer revision", archived: false, dependencies: ["target"]));
                if (corruption == "missing-revision")
                    await SqlAsync(path, """
                        PRAGMA foreign_keys=OFF;
                        DELETE FROM cortex_v2_record_revision WHERE namespace_name='project.rotation'
                          AND kind='task' AND record_id='consumer';
                        """);
                else
                    await SqlAsync(path, """
                        UPDATE cortex_v2_record_head SET content_hash=(SELECT content_hash FROM cortex_v2_record_head
                          WHERE namespace_name='project.rotation' AND kind='task' AND record_id='target')
                        WHERE namespace_name='project.rotation' AND kind='task' AND record_id='consumer';
                        """);
            }
            await ThrowsAsync<InvalidDataException>(() => store.PlanPayloadRotationAsync(new(1, Space, Cutoff)).AsTask(),
                "malformed incoming head cannot be skipped or treated as current: " + corruption);
            await ThrowsAsync<InvalidDataException>(() => store.OffloadArchivedPayloadsAsync(plan.Selection!).AsTask(),
                "original typed selection rechecks malformed incoming head: " + corruption);
            Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0,
                "malformed head prevents every payload eviction: " + corruption);
        }
    }

    private static async Task MissingHistoricalBlobAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), clock);
        var historical = await store.AppendAsync(Seed("target", "owned historical bytes"));
        var current = await store.AppendAsync(Seed("target", "owned valid current bytes") with
            { ExpectedRevision = 1, IdempotencyKey = "target-revision2" });
        clock.Now = Cutoff.AddDays(1);
        await store.UpgradePayloadStorageAsync(2);
        Check((await store.PlanPayloadRotationAsync(new(1, Space, Cutoff))).Selection?.Records.Count == 1,
            "complete history is initially selectable");
        // Only this owned negative fixture disables foreign keys; the current
        // head and its payload remain exact and readable after corruption.
        await SqlAsync(path, "PRAGMA foreign_keys=OFF; DELETE FROM cortex_v2_content_blob WHERE content_hash='" + historical.ContentHash + "';");
        Check((await store.ReadHeadAsync(Space, "task", "target"))!.RevisionHash == current.RevisionHash,
            "missing historical payload does not depend on a broken current head");
        await ThrowsAsync<InvalidDataException>(() => store.PlanPayloadRotationAsync(new(1, Space, Cutoff)).AsTask(),
            "missing historical blob cannot disappear from rotation charge");
        await ThrowsAsync<InvalidDataException>(() => store.RotateArchivedPayloadsAsync(new(1, Space, Cutoff)).AsTask(),
            "rotation refuses unavailable historical payload before publication");
        var after = await store.GetStorageMetricsAsync();
        Check(after.ColdCompressedBlobBytes == 0 && after.RevisionRows == 2,
            "missing historical blob causes no eviction or revision mutation");
    }

    private static SqliteCortexV2AppendRequest SeedKnowledge(string ns, string id, string body,
        CortexKnowledgeStateV2 state = CortexKnowledgeStateV2.Archived, string? replacement = null)
    {
        var attributes = JsonSerializer.Serialize(new
        {
            state, kind = CortexKnowledgeKindV2.Observation, tier = CortexKnowledgeTierV2.Verified,
            provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "fixture", null, null, Old),
            supersededByRecordId = replacement, createdAtUtc = Old, updatedAtUtc = Old,
            archivedAtUtc = state == CortexKnowledgeStateV2.Archived ? (DateTimeOffset?)Old : null
        }, CortexV2Json.Options);
        return new(ns, "knowledge", id, 0, "seed-knowledge-" + id, "fixture", "fixture", id, [], "fixture",
            Encoding.UTF8.GetBytes(body), state == CortexKnowledgeStateV2.Active, attributes);
    }

    private static async Task SqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    private static async Task ThrowsCodeAsync(Func<Task> action, string code, string name)
    {
        try { await action(); }
        catch (InvalidOperationException failure) when (failure.Message == code) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }

    private static SqliteCortexV2AppendRequest Seed(string id, string body, bool archived = true,
        string[]? dependencies = null, DateTimeOffset? date = null)
    {
        var when = date ?? Old;
        var attributes = JsonSerializer.Serialize(new
        {
            status = archived ? CortexTaskStatusV2.Archived : CortexTaskStatusV2.Open,
            assignee = (string?)null, claimedBy = (string?)null, dependencies = dependencies ?? [], blockedReason = (string?)null,
            createdAtUtc = when, updatedAtUtc = when, completedAtUtc = (DateTimeOffset?)when,
            archivedAtUtc = archived ? (DateTimeOffset?)when : null,
            provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "fixture", null, null, when),
            checkpoint = (string?)null, evidence = (string?)null, handoff = (string?)null
        }, CortexV2Json.Options);
        return new(Space, "task", id, 0, "seed-" + id, "fixture", "fixture", id, [], "fixture",
            Encoding.UTF8.GetBytes(body), !archived, attributes);
    }
    private static void Check(bool value, string name) { _checks++; if (!value) throw new InvalidOperationException(name); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
