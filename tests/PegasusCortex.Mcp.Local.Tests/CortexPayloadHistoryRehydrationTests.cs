using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadHistoryRehydrationTests
{
    private const string Space = "project.history-rehydration";
    private const long MaximumCharge = 16 * 1024 * 1024;
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-history-rehydration-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await LargeHistoryAsync(Path.Combine(root, "large"));
            await BytesAndSharedHashesAsync(Path.Combine(root, "bytes"));
            await FaultsAndSkippedPrefixAsync(Path.Combine(root, "faults"));
            await AdmissionAndMalformedAsync(Path.Combine(root, "admission"));
        }
        finally { DeleteOwnedRoot(root); }
        Check(!Directory.Exists(root), "owned stores, cold files and provider callbacks released");
        Console.WriteLine($"PASS paged history rehydration: {_checks} owned SQLite/file and injected cold-provider checks; large active history, exact-head/cursor pages, byte and metadata bounds, shared hashes, durable restart/repeats, pin preconditions, cancellation/rollback and malformed-data refusal; no installed migration or automatic scheduling acceptance");
    }

    private static async Task LargeHistoryAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        SqliteCortexTieringCoordinate current;
        SqliteCortexPayloadHistoryCursor next;
        long semanticRevisions;
        var scope = new SqliteCortexPayloadProtectionScope(Space, "task", "large");
        var pin = new SqliteCortexPayloadProtectionRequest(1, scope, true, false, 0, "pin-large", "fixture");
        await using (var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
        {
            var archived = Enumerable.Range(0, 256).Select(i => Seed("large", i, "archived payload " + i)).ToArray();
            var seeded = await store.AppendBatchAsync(archived);
            Check(seeded.Committed && seeded.Items.Count == 256, "one bounded source history seed");
            await store.UpgradePayloadStorageAsync(2);
            var old = Coordinate("large", seeded.Items[^1]);
            Check((await store.OffloadArchivedPayloadsAsync(new(1, [old]))).PayloadsChanged == 256,
                "existing bounded API creates real cold history files");
            var live = Enumerable.Range(256, 32).Select(i => Seed("large", i, "active payload " + i, archived: false)).ToArray();
            var appended = await store.AppendBatchAsync(live);
            current = Coordinate("large", appended.Items[^1]);
            semanticRevisions = (await store.GetCatalogCountersAsync()).RevisionRows;
            Check(current.ExpectedHeadRevision == 288, "active history exceeds whole-record revision ceiling");
            await ThrowsAsync<InvalidOperationException>(() => store.RehydratePayloadsAsync(new(1, [current])).AsTask(),
                "existing whole-history ceiling is not lifted");
            await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(pin).AsTask(),
                "payload_protection_requires_rehydration", "cold history refuses a whole-history pin");
            var request = new SqliteCortexPayloadHistoryRehydrationRequest(1, current);
            var before = await store.GetStorageMetricsAsync();
            var first = await store.RehydratePayloadHistoryAsync(request);
            Bounds(first);
            Check(first.CompletedRevisions == 50 && first.Tiering.PayloadsChanged == 50 &&
                first.Next is { Revision: 50 } && !first.ReachedHead, "first committed page stops at its exact revision");
            var after = await store.GetStorageMetricsAsync();
            Check(after.HotCompressedBlobBytes - before.HotCompressedBlobBytes == first.Tiering.HotBytesRestored,
                "first receipt equals real hot-byte restoration");
            var replay = await store.RehydratePayloadHistoryAsync(request);
            Bounds(replay);
            Check(replay.State == "page_already_hot" && replay.Tiering.PayloadsChanged == 0 && replay.ChargedPayloadBytes == 0 &&
                replay.Through == first.Through && replay.Next == first.Next, "same page repeat has exact progress and no duplicate restoration");
            await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(pin).AsTask(),
                "payload_protection_requires_rehydration", "partial committed page cannot permit premature pinning");
            next = first.Next!;
        }
        await using (var reopened = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true)))
        {
            var pages = 0;
            SqliteCortexPayloadHistoryCursor? cursor = next;
            SqliteCortexPayloadHistoryCursor? end = null;
            do
            {
                var page = await reopened.RehydratePayloadHistoryAsync(new(1, current, cursor));
                Bounds(page); pages++;
                Check(page.StartedAfterRevision == cursor!.Revision && page.Through.Revision > cursor.Revision,
                    "reopened batch continues monotonically from committed cursor");
                end = page.Through; cursor = page.Next;
                Check(pages <= 16, "fixture continuation is bounded");
            } while (cursor is not null);
            Check(end!.Revision == current.ExpectedHeadRevision && pages > 1, "pages exhaust the fixed current head after reopen");
            var metrics = await reopened.GetStorageMetricsAsync();
            Check(metrics.ColdCompressedBlobBytes == 0 && metrics.RevisionRows == semanticRevisions,
                "all historical bytes restored without creating semantic or replay revisions");
            Check((await reopened.ReadHeadAsync(Space, "task", "large"))!.RevisionHash == current.ExpectedHeadHash,
                "active current head remains exactly unchanged");
            Check((await reopened.AppendAsync(Seed("large", 0, "archived payload 0"))).Disposition ==
                SqliteCortexV2WriteDispositions.Idempotent, "original historical write identity still replays without a new revision");
            Check((await reopened.SetPayloadProtectionAsync(pin)).Revision == 1,
                "same uncommitted pin first succeeds only after all history is hot");
            var exhausted = await reopened.RehydratePayloadHistoryAsync(new(1, current, end));
            Bounds(exhausted);
            Check(exhausted.CompletedRevisions == 0 && exhausted.ReachedHead && exhausted.Next is null &&
                exhausted.Tiering.PayloadsChanged == 0, "already exhausted cursor is repeat-safe");
            Check((await reopened.FullDoctorAsync()).Healthy, "large real-file history and policy doctor");
        }
    }

    private static async Task BytesAndSharedHashesAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db");
        var cold = new MemoryCold();
        await using var store = new SqliteCortexV2Store(new(path, MaximumContentBytes: 16 * 1024 * 1024,
            EnablePayloadOffload: true), coldPayloadStore: cold);
        SqliteCortexV2WriteResult? head = null;
        // This controlled descriptor fixture exercises a cold closure larger than
        // the old whole-record executor admits; the main fixture uses real files.
        string[] bodies = [new('a', 4 * 1024 * 1024), new('b', 4 * 1024 * 1024),
            new('a', 4 * 1024 * 1024), new('c', 4 * 1024 * 1024), new('d', 4 * 1024 * 1024)];
        for (var i = 0; i < bodies.Length; i++) head = await store.AppendAsync(Seed("bytes", i, bodies[i]));
        _ = await store.AppendAsync(Seed("shared", 0, bodies[0]));
        head = await store.AppendAsync(Seed("bytes", 5, "hot current head", archived: false));
        await store.UpgradePayloadStorageAsync(2);
        await MakeColdAsync(path, cold, keepHot: head.ContentHash);
        var record = Coordinate("bytes", head);
        var first = await store.RehydratePayloadHistoryAsync(new(1, record));
        Bounds(first);
        Check(first.Tiering.PayloadsChanged == 3 && first.CompletedRevisions == 4 && first.Next?.Revision == 4,
            "byte ceiling stops before next cold item without skipping its revision");
        Check(cold.Reads == 3 && cold.ChargedBytes == first.ChargedPayloadBytes && first.ChargedPayloadBytes <= MaximumCharge,
            "duplicate shared hash charged and read once with exact provider bytes");
        Check((await store.ReadHeadAsync(Space, "task", "shared"))!.ContentHash ==
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bodies[0]))), "shared hash is globally restored without changing its owner");
        cold.Reset();
        var last = await store.RehydratePayloadHistoryAsync(new(1, record, first.Next));
        Bounds(last);
        Check(last.Tiering.PayloadsChanged == 1 && last.CompletedRevisions == 2 && last.ReachedHead && last.Next is null && cold.Reads == 1,
            "continuation restores exactly the deferred cold hash");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "byte pages restore entire synthetic closure");
        Check((await store.FullDoctorAsync()).Healthy, "synthetic descriptor closure verifies after real SQL promotion");
        await SingleItemOverBoundAsync(Path.Combine(root, "single"));
    }

    private static async Task SingleItemOverBoundAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db"); var cold = new MemoryCold();
        await using var store = new SqliteCortexV2Store(new(path, MaximumContentBytes: 16 * 1024 * 1024,
            EnablePayloadOffload: true), coldPayloadStore: cold);
        var head = await store.AppendAsync(Seed("single", 0, new string('x', 16 * 1024 * 1024)));
        await store.UpgradePayloadStorageAsync(2);
        await MakeColdAsync(path, cold);
        var before = (await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes;
        await ThrowsCodeAsync(() => store.RehydratePayloadHistoryAsync(new(1, Coordinate("single", head))).AsTask(),
            "payload_history_item_over_bound", "single over-bound item refuses explicitly instead of a zero-progress cursor");
        Check(cold.Reads == 0 && (await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == before,
            "over-bound item loads no payload and commits no progress");
    }

    private static async Task FaultsAndSkippedPrefixAsync(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "cortex-v2.db"); var cold = new MemoryCold();
        await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: cold);
        var first = await store.AppendAsync(Seed("fault", 0, "first cold payload"));
        _ = await store.AppendAsync(Seed("fault", 1, "second cold payload"));
        var head = await store.AppendAsync(Seed("fault", 2, "third cold payload", archived: false));
        await store.UpgradePayloadStorageAsync(2); await MakeColdAsync(path, cold);
        var request = new SqliteCortexPayloadHistoryRehydrationRequest(1, Coordinate("fault", head));
        var before = (await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes;
        using (var cancel = new CancellationTokenSource())
        {
            cold.Cancel = cancel; cold.CancelAt = 2;
            await ThrowsAsync<OperationCanceledException>(() => store.RehydratePayloadHistoryAsync(request, cancel.Token).AsTask(),
                "cancellation after a prior promotion rolls the current page back");
        }
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == before, "canceled page keeps all original cold descriptors");
        cold.Reset(); cold.FailAt = 2;
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.RehydratePayloadHistoryAsync(request).AsTask(),
            "provider read failure after prior promotion preserves rollback");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == before, "faulted page commits no partial cursor or bytes");
        cold.Reset(); cold.Corrupt = true;
        await ThrowsAsync<CortexPayloadUnavailableException>(() => store.RehydratePayloadHistoryAsync(request).AsTask(),
            "cold content still requires exact compressed and content identities");
        cold.Reset();
        var wrong = request with { After = new(1, new string('0', 64)) };
        await ThrowsCodeAsync(() => store.RehydratePayloadHistoryAsync(wrong).AsTask(), "payload_history_cursor_changed",
            "cursor hash must match actual same-record immutable revision");
        Check(cold.Reads == 0, "cursor refusal occurs before cold reads");
        var skipped = await store.RehydratePayloadHistoryAsync(request with { After = new(1, first.RevisionHash!) });
        Bounds(skipped);
        Check(skipped.ReachedHead && skipped.Next is null && skipped.Tiering.PayloadsChanged == 2,
            "range exhaustion is explicit even when caller started after an earlier revision");
        var pin = new SqliteCortexPayloadProtectionRequest(1, new(Space, "task", "fault"), true, false, 0, "pin-fault", "fixture");
        await ThrowsCodeAsync(() => store.SetPayloadProtectionAsync(pin).AsTask(), "payload_protection_requires_rehydration",
            "range exhaustion never claims earlier skipped cold history is pinned");
        var completed = await store.RehydratePayloadHistoryAsync(request);
        Check(completed.Tiering.PayloadsChanged == 1 && (await store.SetPayloadProtectionAsync(pin)).Revision == 1,
            "explicit prefix recovery permits pin only after actual all-hot check");
        var newer = await store.AppendAsync(Seed("fault", 3, "new current generation", archived: false));
        cold.Reset();
        await ThrowsAsync<InvalidOperationException>(() => store.RehydratePayloadHistoryAsync(request).AsTask(),
            "changed current head is not silently adopted");
        Check(cold.Reads == 0 && (await store.ReadHeadAsync(Space, "task", "fault"))!.RevisionHash == newer.RevisionHash,
            "stale current request does not read or overwrite new generation");
        Check((await store.FullDoctorAsync()).Healthy, "fault/recovery fixture preserves original metadata chains");
    }

    private static async Task AdmissionAndMalformedAsync(string root)
    {
        foreach (var failure in new[] { "metadata", "missing-blob", "gap" })
        {
            var directory = Path.Combine(root, failure); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "cortex-v2.db"); var cold = new MemoryCold();
            await using var store = new SqliteCortexV2Store(new(path, EnablePayloadOffload: true), coldPayloadStore: cold);
            var first = await store.AppendAsync(Seed("malformed", 0, "old retained payload"));
            var head = await store.AppendAsync(Seed("malformed", 1, "valid current payload", archived: false));
            var request = new SqliteCortexPayloadHistoryRehydrationRequest(1, Coordinate("malformed", head));
            await ThrowsAsync<InvalidOperationException>(() => store.RehydratePayloadHistoryAsync(request).AsTask(),
                "no implicit schema upgrade");
            await store.UpgradePayloadStorageAsync(2); await MakeColdAsync(path, cold);
            await using (var disabled = new SqliteCortexV2Store(new(path), coldPayloadStore: cold))
                await ThrowsAsync<InvalidOperationException>(() => disabled.RehydratePayloadHistoryAsync(request).AsTask(),
                    "public history entry retains explicit option admission");
            await ThrowsAsync<ArgumentException>(() => store.RehydratePayloadHistoryAsync(request with { Version = 2 }).AsTask(), "closed history version");
            await ThrowsAsync<ArgumentException>(() => store.RehydratePayloadHistoryAsync(request with
                { After = new(head.Revision!.Value + 1, head.RevisionHash!) }).AsTask(), "cursor cannot exceed fixed head");
            var sql = failure switch
            {
                "metadata" => "UPDATE cortex_v2_record_revision SET attributes_json='{}' WHERE record_id='malformed' AND revision=1;",
                "missing-blob" => "PRAGMA foreign_keys=OFF; DELETE FROM cortex_v2_content_blob WHERE content_hash='" + first.ContentHash + "';",
                _ => "PRAGMA foreign_keys=OFF; DELETE FROM cortex_v2_record_revision WHERE record_id='malformed' AND revision=1;"
            };
            await SqlAsync(path, sql);
            await ThrowsAsync<InvalidDataException>(() => store.RehydratePayloadHistoryAsync(request).AsTask(),
                "malformed historical proof refuses before hydration: " + failure);
            Check(cold.Reads == 0, "historical refusal reads no cold body: " + failure);
        }
    }

    private static SqliteCortexV2AppendRequest Seed(string id, int expected, string body, bool archived = true)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var attributes = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = archived ? Pegasus.Cortex.Contracts.V2.CortexTaskStatusV2.Archived : Pegasus.Cortex.Contracts.V2.CortexTaskStatusV2.Open,
            assignee = (string?)null, claimedBy = (string?)null, dependencies = Array.Empty<string>(), blockedReason = (string?)null,
            createdAtUtc = now, updatedAtUtc = now, completedAtUtc = (DateTimeOffset?)null,
            archivedAtUtc = archived ? (DateTimeOffset?)now : null,
            provenance = new Pegasus.Cortex.Contracts.V2.CortexProvenanceV2(Pegasus.Cortex.Contracts.V2.CortexProvenanceSourceV2.Agent,
                "fixture", null, null, now), checkpoint = (string?)null, evidence = (string?)null, handoff = (string?)null
        }, Pegasus.Cortex.Contracts.V2.CortexV2Json.Options);
        return new(Space, "task", id, expected, "seed-" + id + "-" + expected, "fixture", "fixture", id, [], "fixture",
            Encoding.UTF8.GetBytes(body), !archived, attributes);
    }

    private static SqliteCortexTieringCoordinate Coordinate(string id, SqliteCortexV2WriteResult head) =>
        new(Space, "task", id, head.Revision!.Value, head.RevisionHash!);

    private static void Bounds(SqliteCortexPayloadHistoryRehydrationReceipt receipt)
    {
        Check(receipt.InspectedRevisions <= 53 && receipt.CompletedRevisions <= 50 && receipt.Tiering.PayloadsChanged <= 50,
            "page reserves head/cursor and helper metadata work within fixed bound");
        Check(receipt.ChargedPayloadBytes is >= 0 and <= MaximumCharge && receipt.Tiering.HotBytesRestored <= receipt.ChargedPayloadBytes &&
            receipt.Tiering.RecordMetadataUnchanged && receipt.Tiering.PhysicalBytesReclaimed == 0,
            "charged I/O stays bounded and receipt claims no metadata change or disk reclaim");
        Check(receipt.ReachedHead == (receipt.Next is null) && (receipt.Next is null || receipt.Next == receipt.Through),
            "continuation is exactly last completed revision, never a looked-ahead row");
    }

    private static async Task MakeColdAsync(string path, MemoryCold cold, string? keepHot = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        var rows = new List<(string Hash, byte[] Bytes)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT content_hash,compressed_content FROM cortex_v2_content_blob WHERE storage_tier='hot' AND content_hash!=$keep;";
            select.Parameters.AddWithValue("$keep", keepHot ?? "");
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), (byte[])reader[1]));
        }
        foreach (var row in rows)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(row.Bytes)); cold.Bytes[sha] = row.Bytes;
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE cortex_v2_content_blob SET compressed_content=NULL,storage_tier='cold',cold_format_version=1,cold_sha256=$sha WHERE content_hash=$hash;";
            update.Parameters.AddWithValue("$sha", sha); update.Parameters.AddWithValue("$hash", row.Hash);
            await update.ExecuteNonQueryAsync();
        }
    }

    private sealed class MemoryCold : ISqliteCortexColdPayloadStore
    {
        internal readonly Dictionary<string, byte[]> Bytes = new(StringComparer.Ordinal);
        internal int Reads, FailAt, CancelAt;
        internal long ChargedBytes;
        internal bool Corrupt;
        internal CancellationTokenSource? Cancel;
        internal void Reset() { Reads = 0; ChargedBytes = 0; FailAt = CancelAt = 0; Corrupt = false; Cancel = null; }
        public ValueTask<byte[]> ReadAsync(SqliteCortexColdPayload payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            ChargedBytes += (long)payload.OriginalLength + payload.CompressedLength;
            if (Reads == CancelAt) { Cancel!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (Reads == FailAt) throw new IOException("owned provider fixture failure");
            return ValueTask.FromResult(Corrupt ? new byte[] { 0 } : Bytes[payload.CompressedSha256].ToArray());
        }
        public ValueTask<IAsyncDisposable> PublishVerifiedAsync(SqliteCortexColdPayload payload, ReadOnlyMemory<byte> compressed,
            CancellationToken token) => throw new InvalidOperationException("History rehydration must never publish another cold copy.");
    }

    private static async Task SqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }
    private static async Task ThrowsCodeAsync(Func<Task> action, string code, string name)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error.Message == code) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("History rehydration fixture: " + name); _checks++; }

    private static void DeleteOwnedRoot(string root)
    {
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(root) == root && root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(root).StartsWith("cortex-history-rehydration-", StringComparison.Ordinal), "owned scratch boundary");
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(root));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            Check((directory.Attributes & FileAttributes.ReparsePoint) == 0, "owned directory is ordinary");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                Check((entry.Attributes & FileAttributes.ReparsePoint) == 0, "owned entry is ordinary");
                if (entry is DirectoryInfo child) pending.Push(child);
                else { using var exclusive = new FileStream(entry.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            }
        }
        Directory.Delete(root, recursive: true);
    }
}
