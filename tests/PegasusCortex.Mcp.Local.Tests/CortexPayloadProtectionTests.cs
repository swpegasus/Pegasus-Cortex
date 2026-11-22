using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadProtectionTests
{
    private const string Space = "project.protection";
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-protection-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await DurableAndReplayAsync(Path.Combine(root, "durable"));
            await NamespaceIsolationAsync(Path.Combine(root, "scope"));
            await MalformedAsync(Path.Combine(root, "malformed"));
            await RefusalAndRollbackAsync(Path.Combine(root, "rollback"));
            await MissingBlobRefusalAsync(Path.Combine(root, "missing"));
        }
        finally { DeleteOwnedRoot(root); }
        Check(!Directory.Exists(root), "owned SQLite, cold and backup handles released");
        Console.WriteLine($"PASS payload protection: {_checks} owned SQLite/file checks; durable record/namespace whole-history pins and holds, CAS/replay, rehydrate-before-pin, immutable fail-closed metadata, isolation, backup/doctor and rollback; no live store or installed migration");
    }

    private static async Task DurableAndReplayAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var scope = new SqliteCortexPayloadProtectionScope(Space, "task", "first");
        var request = new SqliteCortexPayloadProtectionRequest(1, scope, true, false, 0, "pin-first", "protection-fixture");
        string expectedHash;
        await using (var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
        {
            _ = await store.AppendAsync(Seed(Space, "task", "first", "first historical archive"));
            var first = await store.AppendAsync(Seed(Space, "task", "first", "first current archive") with
                { ExpectedRevision = 1, IdempotencyKey = "seed-first-revision2" });
            var second = await store.AppendAsync(Seed(Space, "task", "second", "second unrelated archive"));
            var original = await store.ReadHeadAsync(Space, "task", "first");
            Check((await store.ReadPayloadProtectionAsync(scope)).Revision == 0, "absent exact policy is revision zero");
            var pinned = await store.SetPayloadProtectionAsync(request);
            Check(pinned.Disposition == SqliteCortexV2WriteDispositions.Appended && pinned.Revision == 1, "pin is a normal immutable append");
            var state = await store.ReadPayloadProtectionAsync(scope);
            Check(state.Pinned && !state.Hold && state.RevisionHash == pinned.RevisionHash, "exact policy readback");
            Check((await store.GetCatalogCountersAsync()).HeadRows == 3, "internal policy is truthfully included in retained catalog counts");
            Check((await store.SearchHeadsAsync(new(Space, "protection"))).Count == 0, "policy never enters FTS");
            Check((await ScalarAsync(path, "SELECT COUNT(*) FROM cortex_v2_search_head WHERE kind='payload-protection';")) == 0,
                "no policy search projection");
            Check((await store.SetPayloadProtectionAsync(request)).Disposition == SqliteCortexV2WriteDispositions.Idempotent,
                "same original request replays");
            Check((await store.SetPayloadProtectionAsync(request with { Pinned = false })).Disposition == SqliteCortexV2WriteDispositions.Conflict,
                "same identity different policy conflicts");
            Check((await store.SetPayloadProtectionAsync(request with { IdempotencyKey = "stale" })).Disposition == SqliteCortexV2WriteDispositions.StaleRevision,
                "new identity still requires exact CAS");
            await store.UpgradePayloadStorageAsync(2);
            await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "first", first)).AsTask(),
                "record pin prevents eviction");
            Check((await store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "second", second))).PayloadsChanged == 1,
                "unrelated record still eligible");
            Check((await store.ReadPayloadProtectionAsync(new(Space, "task", "second"))).Revision == 0,
                "pin does not invent sibling policy");
            var clear = await store.SetPayloadProtectionAsync(request with
                { ExpectedRevision = 1, Pinned = false, IdempotencyKey = "clear-first" });
            Check(clear.Revision == 2 && !(await store.ReadPayloadProtectionAsync(scope)).Pinned,
                "explicit unpin is another revision");
            Check((await store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "first", first))).PayloadsChanged == 2,
                "cleared policy permits both historical and current payload eviction");
            await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(request with
                { ExpectedRevision = 2, IdempotencyKey = "repin-first" }).AsTask(),
                "payload_protection_requires_rehydration", "cold record cannot claim a successful pin");
            Check((await store.ReadPayloadProtectionAsync(scope)).Revision == 2, "failed cold pin rolls policy back");
            await store.RehydratePayloadsAsync(Selection(Space, "task", "first", first));
            var repin = await store.SetPayloadProtectionAsync(request with { ExpectedRevision = 2, IdempotencyKey = "repin-first" });
            Check(repin.Revision == 3, "same refused identity can first commit only after explicit rehydration");
            expectedHash = repin.RevisionHash!;
            var after = await store.ReadHeadAsync(Space, "task", "first");
            Check(after!.RevisionHash == original!.RevisionHash && after.AttributesJson == original.AttributesJson &&
                after.ContentUtf8.SequenceEqual(original.ContentUtf8), "source semantic revision and metadata remain unchanged");
            Check((await store.FullDoctorAsync()).Healthy, "all policy revisions and source chains verify");
            _ = await store.BackupAsync(Path.Combine(root, "backup", "cortex-v2.db"));
        }
        await using (var reopened = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
        {
            var retained = await reopened.ReadPayloadProtectionAsync(scope);
            Check(retained.Pinned && retained.Revision == 3 && retained.RevisionHash == expectedHash,
                "pin survives original store disposal and reopen");
            Check((await reopened.FullDoctorAsync()).Healthy, "reopened doctor includes policies");
        }
        await using (var backup = new SqliteCortexV2Store(new(Path.Combine(root, "backup", "cortex-v2.db"))))
        {
            var saved = await backup.ReadPayloadProtectionAsync(scope);
            Check(saved.Pinned && saved.RevisionHash == expectedHash, "self-contained backup retains exact policy");
            Check((await backup.FullDoctorAsync()).Healthy, "backup doctor includes policy chain");
        }
    }

    private static async Task NamespaceIsolationAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true));
        var task = await store.AppendAsync(Seed(Space, "task", "same", "task history"));
        var knowledge = await store.AppendAsync(Seed(Space, "knowledge", "same", "knowledge history"));
        var other = await store.AppendAsync(Seed("project.other", "task", "same", "other namespace history"));
        await store.UpgradePayloadStorageAsync(2);
        var request = new SqliteCortexPayloadProtectionRequest(1, new(Space), false, true, 0, "hold-namespace", "protection-fixture");
        Check((await store.SetPayloadProtectionAsync(request)).Revision == 1, "namespace hold committed");
        await ThrowsCodeAsync(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "same", task)).AsTask(),
            "payload_protected", "namespace hold protects task");
        await ThrowsCodeAsync(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "knowledge", "same", knowledge)).AsTask(),
            "payload_protected", "namespace hold protects knowledge independently of kind");
        Check((await store.OffloadArchivedPayloadsAsync(Selection("project.other", "task", "same", other))).PayloadsChanged == 1,
            "namespace hold does not cross namespace");
        var record = new SqliteCortexPayloadProtectionRequest(1, new(Space, "task", "same"), true, false, 0,
            "pin-task", "protection-fixture");
        await store.SetPayloadProtectionAsync(record);
        await store.SetPayloadProtectionAsync(request with { Hold = false, ExpectedRevision = 1, IdempotencyKey = "release-namespace" });
        await ThrowsCodeAsync(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "same", task)).AsTask(),
            "payload_protected", "record pin remains after independent namespace release");
        Check((await store.OffloadArchivedPayloadsAsync(Selection(Space, "knowledge", "same", knowledge))).PayloadsChanged == 1,
            "same ID in another kind remains independent");
        await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(request with
            { Hold = true, ExpectedRevision = 2, IdempotencyKey = "hold-again" }).AsTask(),
            "payload_protection_requires_rehydration", "namespace protection refuses any cold semantic history");
        Check((await store.ReadPayloadProtectionAsync(new(Space))).Revision == 2, "failed namespace hold has no policy revision");
        var historical = await store.AppendAsync(Seed(Space, "task", "historical", "cold historical revision"));
        await store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "historical", historical));
        var currentHistorical = await store.AppendAsync(Seed(Space, "task", "historical", "new hot current revision", archived: false) with
            { ExpectedRevision = 1, IdempotencyKey = "new-historical-head" });
        await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(new(1, new(Space, "task", "historical"),
            true, false, 0, "pin-history", "protection-fixture")).AsTask(),
            "payload_protection_requires_rehydration", "hot head does not conceal a cold historical revision");
        var activeSelection = Selection(Space, "task", "historical", currentHistorical);
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(activeSelection).AsTask(),
            "active current head never permits offload of its history");
        Check((await store.RehydratePayloadsAsync(activeSelection)).PayloadsChanged == 1,
            "explicit rehydration restores cold history behind a current active head");
        Check((await store.SetPayloadProtectionAsync(new(1, new(Space, "task", "historical"),
            true, false, 0, "pin-history", "protection-fixture"))).Revision == 1,
            "original uncommitted pin can succeed after active history rehydration");
        Check((await store.ReadHeadAsync(Space, "task", "historical"))!.RevisionHash == currentHistorical.RevisionHash &&
            (await store.RehydratePayloadsAsync(activeSelection)).PayloadsChanged == 0,
            "rehydration under a pin preserves active head and is repeat safe");
    }

    private static async Task MalformedAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true));
        var head = await store.AppendAsync(Seed(Space, "task", "protected", "preserve unknown policy"));
        var scope = new SqliteCortexPayloadProtectionScope(Space);
        var policy = await store.SetPayloadProtectionAsync(new(1, scope, false, false, 0, "create-clear", "protection-fixture"));
        await store.UpgradePayloadStorageAsync(2);
        string[] malformed =
        [
            "{\"version\":2,\"namespace\":\"project.protection\",\"kind\":null,\"recordId\":null,\"pinned\":false,\"hold\":false}",
            "{\"version\":1,\"namespace\":\"project.protection\",\"kind\":null,\"recordId\":null,\"pinned\":false,\"hold\":false,\"future\":true}",
            "{\"version\":1,\"namespace\":\"project.protection\",\"kind\":null,\"recordId\":null,\"pinned\":false,\"pinned\":false,\"hold\":false}",
            "{\"version\":1,\"namespace\":\"project.other\",\"kind\":null,\"recordId\":null,\"pinned\":false,\"hold\":false}"
        ];
        foreach (var attributes in malformed)
        {
            // Trusted low-level fixture injects a correctly hashed but semantically
            // invalid policy. This is not an admitted product wire operation.
            policy = await store.AppendAsync(new(Space, policy.Kind, policy.RecordId, policy.Revision!.Value,
                "malformed-" + policy.Revision, "protection-fixture", "payload.protection.updated",
                "Payload protection", [], "", [], false, attributes));
            await ThrowsAsync<InvalidDataException>(() => store.ReadPayloadProtectionAsync(scope).AsTask(),
                "closed metadata parser refuses unknown or duplicate policy");
            await ThrowsAsync<InvalidDataException>(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "protected", head)).AsTask(),
                "invalid namespace policy never becomes permission to evict");
        }
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "malformed policies published no cold payload");
        // Restore valid metadata through the trusted fixture writer, then corrupt
        // the stored JSON without updating its immutable hash.
        var valid = "{\"version\":1,\"namespace\":\"project.protection\",\"kind\":null,\"recordId\":null,\"pinned\":true,\"hold\":false}";
        policy = await store.AppendAsync(new(Space, policy.Kind, policy.RecordId, policy.Revision!.Value,
            "restore-valid", "protection-fixture", "payload.protection.updated", "Payload protection", [], "", [], false, valid));
        await SqlAsync(path, "UPDATE cortex_v2_record_revision SET attributes_json=replace(attributes_json,'\"pinned\":true','\"pinned\":false') WHERE kind='payload-protection' AND revision=" + policy.Revision + ";");
        await ThrowsAsync<InvalidDataException>(() => store.OffloadArchivedPayloadsAsync(Selection(Space, "task", "protected", head)).AsTask(),
            "forged false policy fails immutable metadata hash");
        Check(!(await store.FullDoctorAsync()).Healthy, "doctor reports intentional fixture corruption");
        await SqlAsync(path, "DELETE FROM cortex_v2_record_head WHERE kind='payload-protection';");
        await ThrowsAsync<InvalidDataException>(() => store.ReadPayloadProtectionAsync(scope).AsTask(),
            "missing policy head cannot erase retained protection history");
    }

    private static async Task RefusalAndRollbackAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        await using var store = new SqliteCortexV2Store(new(path));
        _ = await store.AppendAsync(Seed(Space, "task", "present", "owned rollback fixture"));
        var request = new SqliteCortexPayloadProtectionRequest(1, new(Space, "task", "present"), true, true,
            0, "protect", "protection-fixture");
        await ThrowsAsync<ArgumentException>(() => store.SetPayloadProtectionAsync(request with { Version = 2 }).AsTask(), "request version closed");
        await ThrowsAsync<ArgumentException>(() => store.SetPayloadProtectionAsync(request with { Scope = new(Space, "task") }).AsTask(), "scope pair required");
        await ThrowsAsync<ArgumentException>(() => store.SetPayloadProtectionAsync(request with { Scope = new(Space, "payload-protection", "present") }).AsTask(), "arbitrary target kind refused");
        await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(request with { Scope = new(Space, "task", "missing") }).AsTask(),
            "payload_protection_target_unavailable", "missing target cannot create a policy");
        await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(request with { Scope = new("project.missing") }).AsTask(),
            "payload_protection_target_unavailable", "missing namespace cannot create a policy namespace");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => store.SetPayloadProtectionAsync(request, canceled.Token).AsTask(), "canceled writer changes nothing");
        Check((await store.ReadPayloadProtectionAsync(request.Scope)).Revision == 0, "no canceled policy head");
        await SqlAsync(path, "CREATE TRIGGER fixture_protection_abort BEFORE INSERT ON cortex_v2_record_revision WHEN NEW.kind='payload-protection' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        await ThrowsAsync<SqliteException>(() => store.SetPayloadProtectionAsync(request).AsTask(), "actual insert failure rolls transaction back");
        Check((await store.ReadPayloadProtectionAsync(request.Scope)).Revision == 0, "failed insert has no head");
        await SqlAsync(path, "DROP TRIGGER fixture_protection_abort;");
        var result = await store.SetPayloadProtectionAsync(request);
        Check(result.Revision == 1 && (await store.ReadPayloadProtectionAsync(request.Scope)).Hold, "original uncommitted identity remains usable");
        Check((await store.GetCatalogCountersAsync()).NamespaceRows == 1, "target refusal creates no unrelated namespace");
        Check((await store.FullDoctorAsync()).Healthy, "rollback preserves all revision/counter invariants");
    }

    private static async Task MissingBlobRefusalAsync(string root)
    {
        foreach (var tiered in new[] { false, true })
        {
            var directory = Path.Combine(root, tiered ? "schema3" : "schema2");
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "cortex-v2.db");
            await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: tiered));
            var head = await store.AppendAsync(Seed(Space, "task", "missing-blob", "owned malformed payload fixture"));
            if (tiered) await store.UpgradePayloadStorageAsync(2);
            // Deliberate corruption of this one owned fixture. No product path
            // can disable its foreign-key boundary through the protection API.
            await SqlAsync(path, "PRAGMA foreign_keys=OFF; DELETE FROM cortex_v2_content_blob WHERE content_hash='" + head.ContentHash + "';");
            var request = new SqliteCortexPayloadProtectionRequest(1, new(Space, "task", "missing-blob"),
                true, false, 0, "missing-pin", "protection-fixture");
            await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(request).AsTask(),
                "payload_protection_payload_unavailable", "missing referenced payload cannot be reported hot");
            Check((await store.ReadPayloadProtectionAsync(request.Scope)).Revision == 0,
                "missing payload rolls newly appended protection back");
            Check((await store.GetCatalogCountersAsync()).RevisionRows == 1,
                "failed residency precondition leaves no policy revision/replay row");
            Check(!(await store.FullDoctorAsync()).Healthy, "doctor reports intentional missing-blob fixture");
        }
    }

    private static SqliteCortexV2AppendRequest Seed(string ns, string kind, string id, string body, bool archived = true)
    {
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "protection-fixture", null, null, now);
        var attributes = kind == "task"
            ? JsonSerializer.Serialize(new
            {
                status = archived ? CortexTaskStatusV2.Archived : CortexTaskStatusV2.Open, assignee = (string?)null, claimedBy = (string?)null,
                dependencies = Array.Empty<string>(), blockedReason = (string?)null,
                createdAtUtc = now, updatedAtUtc = now, completedAtUtc = (DateTimeOffset?)null,
                archivedAtUtc = archived ? (DateTimeOffset?)now : null, provenance, checkpoint = (string?)null,
                evidence = (string?)null, handoff = (string?)null
            }, CortexV2Json.Options)
            : JsonSerializer.Serialize(new
            {
                state = archived ? CortexKnowledgeStateV2.Archived : CortexKnowledgeStateV2.Active, kind = CortexKnowledgeKindV2.Observation,
                tier = CortexKnowledgeTierV2.Verified, provenance, supersededByRecordId = (string?)null,
                createdAtUtc = now, updatedAtUtc = now, archivedAtUtc = archived ? (DateTimeOffset?)now : null
            }, CortexV2Json.Options);
        return new(ns, kind, id, 0, "seed-" + kind + "-" + id, "protection-fixture", "fixture", id, [], "",
            Encoding.UTF8.GetBytes(body), !archived, attributes);
    }

    private static SqliteCortexTieringRequest Selection(string ns, string kind, string id, SqliteCortexV2WriteResult head) =>
        new(1, [new(ns, kind, id, head.Revision!.Value, head.RevisionHash!)]);

    private static async Task SqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string check) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, check); return; }
        throw new InvalidOperationException("Protection fixture expected refusal: " + check);
    }
    private static async Task ThrowsCodeAsync(Func<Task> action, string code, string check)
    {
        try { await action(); }
        catch (InvalidOperationException failure) when (failure.Message == code) { Check(true, check); return; }
        throw new InvalidOperationException("Protection fixture expected " + code + ": " + check);
    }
    private static void Check(bool condition, string check)
    { if (!condition) throw new InvalidOperationException("Protection fixture: " + check); _checks++; }

    private static void DeleteOwnedRoot(string root)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(root) == root && root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(root).StartsWith("cortex-protection-", StringComparison.Ordinal), "scratch boundary");
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(root));
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            Check((current.Attributes & FileAttributes.ReparsePoint) == 0, "owned directory is ordinary");
            foreach (var child in current.EnumerateFileSystemInfos())
            {
                Check((child.Attributes & FileAttributes.ReparsePoint) == 0, "owned entry is ordinary");
                if (child is DirectoryInfo directory) pending.Push(directory);
                else { using var exclusive = new FileStream(child.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            }
        }
        Directory.Delete(root, recursive: true);
    }
}
