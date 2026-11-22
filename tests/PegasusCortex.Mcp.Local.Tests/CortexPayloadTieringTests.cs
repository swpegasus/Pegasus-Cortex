using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadTieringTests
{
    private const string Space = "project.tiering";
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-tiering-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await RealFilesAndRecoveryAsync(Path.Combine(root, "real"));
            await FaultsAsync(Path.Combine(root, "faults"));
            await UpgradeRollbackAsync(Path.Combine(root, "upgrade"));
            await LimitsAndKindsAsync(Path.Combine(root, "limits"));
            await TrustedMaintenanceAsync(Path.Combine(root, "trusted"));
            await Schema2ReadCeilingsAsync(Path.Combine(root, "read-ceilings"));
            await CommittedCleanupAsync(Path.Combine(root, "committed-cleanup"));
        }
        finally
        {
            // Only this new generated root. Every store/file lease has settled.
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("cortex-tiering-", StringComparison.Ordinal), "scratch boundary");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                Check((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "owned file is ordinary");
                using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            Directory.Delete(root, recursive: true);
        }
        Check(!Directory.Exists(root), "owned scratch released");
        Console.WriteLine($"PASS payload tiering: {_checks} owned SQLite/file and injected fault checks; schema3 opt-in, real hot eviction/exact reads/replay, shared protection, promotion, explicit unavailable, self-contained backup/restore and rollback/reopen; no live migration, product data, power-loss or physical-reclaim acceptance");
    }

    private static async Task RealFilesAndRecoveryAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "cortex-v2.db");
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true));
        var first = Seed("archived", 0, "shared old payload");
        var second = Seed("archived", 1, "exclusive new payload");
        _ = await store.AppendAsync(first);
        var head = await store.AppendAsync(second);
        var activeHead = await store.AppendAsync(Seed("active", 0, "shared old payload", archived: false));
        Check((await store.QuickCheckAsync()).SchemaVersion == 2, "normal create stays schema2");
        var metadataBefore = await ReadMetadataAsync(path);
        await ThrowsAsync<SqliteException>(() => ExecuteSqlAsync(path,
            "UPDATE cortex_v2_meta SET schema_version=3 WHERE singleton=1;"), "default schema2 CHECK remains strict");
        await using (var disabled = new SqliteCortexV2Store(new(path)))
            await ThrowsAsync<InvalidOperationException>(() => disabled.UpgradePayloadStorageAsync(2).AsTask(), "explicit opt-in required");
        var request = Selection("archived", head);
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(request).AsTask(), "no implicit schema upgrade");
        Check(!Directory.Exists(Path.Combine(root, "cold-v1")), "disabled path creates no cold directory");
        var upgraded = await store.UpgradePayloadStorageAsync(2);
        Check(upgraded.SchemaVersion == 3 && upgraded.RecordMetadataUnchanged, "explicit upgrade schema3");
        var metadataAfter = await ReadMetadataAsync(path);
        Check(metadataAfter.Version == 3 && metadataAfter.Singleton == metadataBefore.Singleton &&
            metadataAfter.Generation == metadataBefore.Generation && metadataAfter.CreatedAt == metadataBefore.CreatedAt,
            "metadata rebuild preserves exact singleton generation and creation identity");
        await ThrowsAsync<SqliteException>(() => ExecuteSqlAsync(path,
            "UPDATE cortex_v2_meta SET schema_version=2 WHERE singleton=1;"), "schema3 replacement CHECK remains strict");
        Check((await store.UpgradePayloadStorageAsync(2)).State == "already_upgraded", "upgrade is repeat-safe");
        Check((await store.QuickCheckAsync()).SchemaVersion != SqliteCortexV2Store.SchemaVersion,
            "new schema metadata makes the legacy schema2 equality guard refuse");
        var before = await store.GetStorageMetricsAsync();
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(Selection("active", activeHead)).AsTask(), "active selection refused");
        var offload = await store.OffloadArchivedPayloadsAsync(request);
        Check(offload.PayloadsChanged == 1 && offload.SharedPayloadsKeptHot == 1 && offload.HotBytesEvicted > 0,
            "exclusive payload evicted while nonselected shared reference stays hot");
        Check(offload.PhysicalBytesReclaimed == 0 && offload.RecordMetadataUnchanged, "no erase or reclaim claim");
        var after = await store.GetStorageMetricsAsync();
        Check(after.RevisionRows == before.RevisionRows && after.HeadRows == before.HeadRows &&
            after.BlobRows == before.BlobRows, "all metadata and payload identities retained");
        Check(after.ColdCompressedBlobBytes == offload.HotBytesEvicted &&
            before.HotCompressedBlobBytes - after.HotCompressedBlobBytes == offload.HotBytesEvicted, "actual resident hot bytes removed");
        var old = await store.ReadRevisionAsync(Space, "task", "archived", 1);
        var exact = await store.ReadRevisionAsync(Space, "task", "archived", 2);
        Check(Text(old) == "shared old payload" && Text(exact) == "exclusive new payload", "both exact revisions preserved");
        Check(exact!.RevisionHash == head.RevisionHash, "revision hash unchanged");
        Check((await store.ListHeadsAsync(Space, "task", null, 10)).Items.Count == 2, "head listing hydrates cold payload");
        Check((await store.AppendAsync(second)).Disposition == SqliteCortexV2WriteDispositions.Idempotent, "original replay identity survives eviction");
        Check((await store.FullDoctorAsync()).Healthy, "doctor verifies cold bytes");
        Check((await store.OffloadArchivedPayloadsAsync(request)).PayloadsChanged == 0, "no repeat eviction");
        var bad = request with { Records = [request.Records[0] with { ExpectedHeadRevision = 1 }] };
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(bad).AsTask(), "stale head refuses");
        await ThrowsAsync<ArgumentException>(() => store.OffloadArchivedPayloadsAsync(request with { Version = 2 }).AsTask(), "closed version");
        await ThrowsAsync<ArgumentException>(() => store.OffloadArchivedPayloadsAsync(request with
            { Records = Enumerable.Repeat(request.Records[0], 17).ToArray() }).AsTask(), "bounded selection");

        var raw = Path.Combine(root, "raw", "cortex-v2.db");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        await RawSnapshotAsync(path, raw);
        await ThrowsAsync<InvalidDataException>(() => SqliteCortexV2Recovery.RestoreNewAsync(raw,
            Path.Combine(root, "raw-restore", "cortex-v2.db")).AsTask(), "sidecar-dependent raw snapshot refuses restore");
        var backupPath = Path.Combine(root, "backup", "cortex-v2.db");
        var backup = await store.BackupAsync(backupPath);
        Check(backup.SchemaVersion == 3, "backup reports actual schema");
        var restoredPath = Path.Combine(root, "different", "cortex-v2.db");
        var restored = await SqliteCortexV2Recovery.RestoreNewAsync(backupPath, restoredPath);
        Check(restored.SchemaVersion == 3, "restore reports actual schema");
        await using (var restoredStore = new SqliteCortexV2Store(new(restoredPath)))
        {
            Check(Text(await restoredStore.ReadRevisionAsync(Space, "task", "archived", 2)) == Text(exact), "restore works at different root without cold files");
            Check((await restoredStore.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "snapshot is hydrated");
            Check((await restoredStore.FullDoctorAsync()).Healthy, "restored doctor");
            Check((await restoredStore.AppendAsync(second)).Disposition == SqliteCortexV2WriteDispositions.Idempotent, "restored replay barrier");
        }

        var coldFile = Directory.EnumerateFiles(Path.Combine(root, "cold-v1"), "*.br", SearchOption.AllDirectories).Single();
        var retained = await File.ReadAllBytesAsync(coldFile);
        await File.WriteAllBytesAsync(coldFile, [1, 2, 3]);
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.ReadRevisionAsync(Space, "task", "archived", 2).AsTask(), "tampered exact read unavailable");
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.AppendAsync(second).AsTask(), "missing replay payload never becomes a fresh append");
        Check((await store.FullDoctorAsync()).State == CortexPayloadUnavailableException.Code, "doctor reports exact unavailable state");
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.BackupAsync(Path.Combine(root, "failed-backup", "cortex-v2.db")).AsTask(), "backup refuses corrupt cold closure");
        var protocol = new CortexV2McpApplication(new CortexV2Application(
            new SqliteCortexV2OperationalStore(store, CortexLocalProductCommands.Version), "tiering-fixture"));
        using (var arguments = JsonDocument.Parse("""{"namespace":"project.tiering","recordType":"task","recordId":"archived","revision":2}"""))
        {
            var response = await protocol.CallToolAsync("cortex_read", arguments.RootElement, CancellationToken.None);
            Check(response["isError"]!.GetValue<bool>() && response["structuredContent"]?["error"]?["code"]?.GetValue<string>() ==
                CortexPayloadUnavailableException.Code, "MCP reports unavailable without record recreation");
        }
        Check((await store.GetCatalogCountersAsync()).RevisionRows == before.RevisionRows, "failed reads/replay changed no metadata");
        await File.WriteAllBytesAsync(coldFile, retained);
        File.Move(coldFile, coldFile + ".held");
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.ReadRevisionAsync(Space, "task", "archived", 2).AsTask(), "missing file unavailable");
        File.Move(coldFile + ".held", coldFile);
        _ = await store.AppendAsync(Seed("new-live", 0, "exclusive new payload", archived: false));
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "new live head promotes shared cold bytes first");
        Check(Text(await store.ReadRevisionAsync(Space, "task", "new-live", 1)) == "exclusive new payload", "promoted live read");
        var again = await store.OffloadArchivedPayloadsAsync(request);
        Check(again.PayloadsChanged == 0 && again.SharedPayloadsKeptHot == 2, "both blobs now shared outside selection");
        Check((await store.FullDoctorAsync()).Healthy, "final logical integrity");
    }

    private static async Task FaultsAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var files = new MemoryCold();
        SqliteCortexTieringRequest request;
        await using (var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: files))
        {
            var head = await store.AppendAsync(Seed("fault", 0, "known synthetic payload"));
            request = Selection("fault", head);
            await store.UpgradePayloadStorageAsync(2);
            files.FailAfterPublish = true;
            await ThrowsAsync<IOException>(() => store.OffloadArchivedPayloadsAsync(request).AsTask(), "publication failure rolls back");
            Check(files.Bytes.Count == 1 && (await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0,
                "published orphan does not evict hot bytes");
        }
        await using (var reopened = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: files))
        {
            Check(Text(await reopened.ReadRevisionAsync(Space, "task", "fault", 1)) == "known synthetic payload", "reopen after failed publication");
            files.FailAfterPublish = false; files.CorruptRead = true;
            await ThrowsAsync<InvalidDataException>(() => reopened.OffloadArchivedPayloadsAsync(request).AsTask(), "unverified provider publication refuses eviction");
            Check(files.Leases == 0 && (await reopened.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "failed readback releases lease/transaction");
            files.CorruptRead = false;
            await ExecuteSqlAsync(path, "CREATE TRIGGER fixture_eviction_abort BEFORE UPDATE OF storage_tier ON cortex_v2_content_blob WHEN NEW.storage_tier='cold' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            await ThrowsAsync<SqliteException>(() => reopened.OffloadArchivedPayloadsAsync(request).AsTask(), "transaction failure after publication rolls back");
            Check(files.Leases == 0 && (await reopened.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "rollback keeps hot bytes and releases all leases");
            await ExecuteSqlAsync(path, "DROP TRIGGER fixture_eviction_abort;");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => reopened.OffloadArchivedPayloadsAsync(request, canceled.Token).AsTask(), "precommit cancellation");
            await reopened.OffloadArchivedPayloadsAsync(request);
            Check(files.Leases == 0, "successful commit releases verified lease");
        }
        await using (var reopened = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: files))
        {
            Check(Text(await reopened.ReadRevisionAsync(Space, "task", "fault", 1)) == "known synthetic payload", "reopen after committed eviction");
            Check((await reopened.RehydratePayloadsAsync(request)).HotBytesRestored > 0, "explicit rollback path rehydrates payload");
            Check((await reopened.FullDoctorAsync()).Healthy, "rehydrated integrity");
        }
    }

    private static async Task UpgradeRollbackAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        MetadataSnapshot? before = null;
        await using (var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
        {
            _ = await store.AppendAsync(Seed("upgrade", 0, "preserve across DDL rollback"));
            before = await ReadMetadataAsync(path);
            await ExecuteSqlAsync(path, "CREATE INDEX ix_cortex_v3_head_content ON cortex_v2_record_head(record_id);");
            await ThrowsAsync<SqliteException>(() => store.UpgradePayloadStorageAsync(2).AsTask(), "failed DDL upgrade rolls back");
            Check(store.EffectiveSchemaVersion == 2 && await ReadMetadataAsync(path) == before,
                "uncommitted schema3 observation is never published after rollback");
        }
        await using var reopened = new SqliteCortexV2Store(new(path));
        Check(await ReadMetadataAsync(path) == before, "failed later index creation rolls back exact metadata-table rebuild");
        Check((await reopened.QuickCheckAsync()).SchemaVersion == 2, "failed upgrade leaves schema2");
        Check(Text(await reopened.ReadRevisionAsync(Space, "task", "upgrade", 1)) == "preserve across DDL rollback", "failed upgrade preserves content");
        Check((await reopened.FullDoctorAsync()).Healthy, "failed upgrade retains FK/hash integrity");
    }

    private static async Task LimitsAndKindsAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var files = new MemoryCold();
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: files);
        var small = Seed("many", 0, "small bounded fixture");
        SqliteCortexV2WriteResult? head = null;
        for (var revision = 0; revision < 257; revision++)
            head = await store.AppendAsync(small with { ExpectedRevision = revision, IdempotencyKey = "many-" + revision });
        await store.UpgradePayloadStorageAsync(2);
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(Selection("many", head!)).AsTask(), "revision scan bound refuses whole batch");
        Check(files.Bytes.Count == 0, "oversized history publishes nothing");
        for (var revision = 0; revision < 16; revision++)
            head = await store.AppendAsync(Seed("bytes", revision, "synthetic-" + revision + new string((char)('a' + revision), 1024 * 1024)));
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(Selection("bytes", head!)).AsTask(), "aggregate byte bound rolls back whole batch");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0 && files.Leases == 0,
            "aggregate failure leaves all hot rows and releases publication leases");
        var malformed = Seed("unknown", 0, "unknown lifecycle") with { AttributesJson = "{}" };
        head = await store.AppendAsync(malformed);
        await ThrowsAsync<InvalidOperationException>(() => store.OffloadArchivedPayloadsAsync(Selection("unknown", head)).AsTask(), "unproven lifecycle stays hot");
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var knowledge = Seed("knowledge", 0, "archived knowledge fixture") with
        {
            Kind = "knowledge",
            AttributesJson = JsonSerializer.Serialize(new
            {
                state = CortexKnowledgeStateV2.Archived, kind = CortexKnowledgeKindV2.Observation,
                tier = CortexKnowledgeTierV2.Verified,
                provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "tiering-fixture", null, null, now),
                supersededByRecordId = (string?)null, createdAtUtc = now, updatedAtUtc = now, archivedAtUtc = now
            }, CortexV2Json.Options)
        };
        head = await store.AppendAsync(knowledge);
        var request = new SqliteCortexTieringRequest(1, [new(Space, "knowledge", "knowledge", head.Revision!.Value, head.RevisionHash!)]);
        Check((await store.OffloadArchivedPayloadsAsync(request)).PayloadsChanged == 1, "archived knowledge supported");
        Check(Text(await store.ReadRevisionAsync(Space, "knowledge", "knowledge", 1)) == "archived knowledge fixture", "knowledge exact read");
    }

    private static async Task TrustedMaintenanceAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        await using var resident = new SqliteCortexV2Store(new(path));
        var head = await resident.AppendAsync(Seed("trusted", 0, "trusted synthetic payload"));
        var selection = Selection("trusted", head);
        await using (var helper = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
            await helper.UpgradePayloadStorageAsync(2);
        Check(resident.EffectiveSchemaVersion == 2, "unrefreshed resident retains prior observed generation");
        Check(await resident.RevalidatePayloadSchemaAdmissionAsync() == 3 && resident.EffectiveSchemaVersion == 3,
            "existing store refreshes exact committed schema under metadata admission");
        await ThrowsAsync<InvalidOperationException>(() => resident.OffloadArchivedPayloadsAsync(selection).AsTask(),
            "trusted admission does not flip default mutation option");
        Check((await resident.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Offload, 3, selection)).HotBytesEvicted > 0,
            "per-call trusted offload on actual resident store");
        Check((await resident.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Rehydrate, 3, selection)).HotBytesRestored > 0,
            "per-call trusted rehydrate");
        await ThrowsAsync<ArgumentException>(() => resident.ExecuteTrustedPayloadMaintenanceAsync(
            (SqliteCortexPayloadMaintenanceAction)999, 3, selection).AsTask(), "trusted action closed");
        await ThrowsAsync<InvalidOperationException>(() => resident.OffloadArchivedPayloadsAsync(selection).AsTask(),
            "default remains disabled after trusted calls");
        var missing = Path.Combine(root, "absent", "cortex-v2.db");
        await using var uninitialized = new SqliteCortexV2Store(new(missing));
        await ThrowsAsync<InvalidOperationException>(() => uninitialized.RevalidatePayloadSchemaAdmissionAsync().AsTask(),
            "revalidation cannot initialize missing store");
        Check(!File.Exists(missing) && !Directory.Exists(Path.GetDirectoryName(missing)), "metadata revalidation creates nothing");
    }

    private static async Task Schema2ReadCeilingsAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var content = new string('x', 2 * 1024 * 1024 + 1024);
        var original = Seed("large", 0, content);
        await using (var writer = new SqliteCortexV2Store(new(path, MaximumContentBytes: 4 * 1024 * 1024)))
            _ = await writer.AppendAsync(original);
        await using var reader = new SqliteCortexV2Store(new(path));
        Check(Text(await reader.ReadRevisionAsync(Space, "task", "large", 1)) == content,
            "schema2 exact read retains original16MiB ceiling above default write ceiling");
        var inherited = original with { ExpectedRevision = 1, IdempotencyKey = "large-inherited", ContentUtf8 = null };
        Check((await reader.AppendAsync(inherited)).Disposition == SqliteCortexV2WriteDispositions.Appended,
            "schema2 inherited content retains original16MiB read ceiling");
        Check((await reader.AppendAsync(inherited)).Disposition == SqliteCortexV2WriteDispositions.Idempotent,
            "schema2 inherited replay retains original16MiB read ceiling");
        await ThrowsAsync<InvalidDataException>(() => reader.ListHeadsAsync(Space, "task", null, 4).AsTask(),
            "head listing retains configured2MiB ceiling");
        Check(!(await reader.FullDoctorAsync()).Healthy, "doctor retains configured2MiB ceiling");
        Check((await reader.QuickCheckAsync()).SchemaVersion == 2 && !Directory.Exists(Path.Combine(root, "cold-v1")),
            "read compatibility does not opt in to tiering");
    }

    private static SqliteCortexV2AppendRequest Seed(string id, long expected, string content, bool archived = true)
    {
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var metadata = JsonSerializer.Serialize(new
        {
            status = archived ? CortexTaskStatusV2.Archived : CortexTaskStatusV2.Open,
            assignee = (string?)null, claimedBy = (string?)null, dependencies = Array.Empty<string>(), blockedReason = (string?)null,
            createdAtUtc = now, updatedAtUtc = now, completedAtUtc = (DateTimeOffset?)now,
            archivedAtUtc = archived ? (DateTimeOffset?)now : null,
            provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "tiering-fixture", null, null, now),
            checkpoint = (string?)null, evidence = (string?)null, handoff = (string?)null
        }, CortexV2Json.Options);
        return new(Space, "task", id, expected, "seed-" + id + "-" + expected, "tiering-fixture", "fixture",
            id, [], "synthetic summary", Encoding.UTF8.GetBytes(content), Searchable: !archived, AttributesJson: metadata);
    }

    private static SqliteCortexTieringRequest Selection(string id, SqliteCortexV2WriteResult head) =>
        new(1, [new(Space, "task", id, head.Revision!.Value, head.RevisionHash!)]);
    private static string? Text(SqliteCortexV2Record? record) => record is null ? null : Encoding.UTF8.GetString(record.ContentUtf8);
    private static async Task ExecuteSqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task RawSnapshotAsync(string path, string target)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await using var output = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = target, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await source.OpenAsync(); await output.OpenAsync(); source.BackupDatabase(output);
    }
    private sealed record MetadataSnapshot(long Singleton, long Version, string Generation, string CreatedAt);
    private static async Task<MetadataSnapshot> ReadMetadataAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT singleton,schema_version,generation,created_at_utc FROM cortex_v2_meta ORDER BY singleton LIMIT 2;";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidDataException("Fixture metadata is absent.");
        var value = new MetadataSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3));
        if (await reader.ReadAsync()) throw new InvalidDataException("Fixture metadata is ambiguous.");
        return value;
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string check) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, check); return; }
        throw new InvalidOperationException("Tiering fixture expected refusal: " + check);
    }
    private static void Check(bool condition, string check)
    { if (!condition) throw new InvalidOperationException("Tiering fixture: " + check); _checks++; }

    private static async Task CommittedCleanupAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "cortex-v2.db");
        var files = new MemoryCold();
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: files);
        var head = await store.AppendAsync(Seed("cleanup", 0, "confirmed despite cleanup notification fault"));
        await store.UpgradePayloadStorageAsync(2);
        files.FailLeaseCleanup = true;
        var result = await store.OffloadArchivedPayloadsAsync(Selection("cleanup", head));
        Check(result.State == "offloaded" && result.PayloadsChanged == 1 && result.CleanupIncomplete && files.Leases == 0,
            "confirmed commit survives provider cleanup notification failure with explicit issue");
        Check(Text(await store.ReadHeadAsync(Space, "task", "cleanup")) == "confirmed despite cleanup notification fault" &&
            (await store.FullDoctorAsync()).Healthy, "confirmed cold bytes remain exact and healthy");
        files.FailLeaseCleanup = false;
        Check((await store.OffloadArchivedPayloadsAsync(Selection("cleanup", head))).PayloadsChanged == 0,
            "subsequent observation never repeats committed eviction");
        _ = await store.AppendAsync(Seed("metrics", 0, "second committed payload"));
        files.AfterLeaseCleanup = () => ExecuteSqlAsync(path, "ALTER TABLE cortex_v2_content_blob RENAME TO fixture_hidden_blob;");
        var rotation = await store.RotateArchivedPayloadsAsync(new(1, Space, DateTimeOffset.UtcNow));
        Check(rotation.State == "rotated" && rotation.Tiering is { PayloadsChanged: 1 } &&
            rotation.ObservedHotCompressedBytesAfter is null && rotation.TargetSatisfied is null &&
            rotation.ObservationIssue == "payload_rotation_metrics_unavailable" && files.Leases == 0,
            "later metrics fault cannot hide original confirmed rotation");
        // Restore the exact owned schema name before normal release/doctor.
        await ExecuteSqlAsync(path, "ALTER TABLE fixture_hidden_blob RENAME TO cortex_v2_content_blob;");
        files.AfterLeaseCleanup = null;
        Check((await store.FullDoctorAsync()).Healthy, "owned metric fault restored without replay or payload loss");
    }

    private sealed class MemoryCold : ISqliteCortexColdPayloadStore
    {
        internal readonly Dictionary<string, byte[]> Bytes = new(StringComparer.Ordinal);
        internal bool FailAfterPublish, CorruptRead, FailLeaseCleanup;
        internal Func<Task>? AfterLeaseCleanup;
        internal int Leases;
        public ValueTask<byte[]> ReadAsync(SqliteCortexColdPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CorruptRead ? new byte[] { 0 } : Bytes[payload.CompressedSha256].ToArray());
        }
        public ValueTask<IAsyncDisposable> PublishVerifiedAsync(SqliteCortexColdPayload payload,
            ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Bytes[payload.CompressedSha256] = compressed.ToArray();
            if (FailAfterPublish) throw new IOException("fixture publication interrupted");
            Leases++; return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
        }
        private sealed class Lease(MemoryCold owner) : IAsyncDisposable
        {
            private bool _disposed;
            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true; owner.Leases--;
                if (owner.AfterLeaseCleanup is { } after) await after();
                if (owner.FailLeaseCleanup) throw new IOException("fixture cleanup notification failed after release");
            }
        }
    }
}
