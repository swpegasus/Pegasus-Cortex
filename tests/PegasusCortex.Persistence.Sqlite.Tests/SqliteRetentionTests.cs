using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

internal static class SqliteRetentionTests
{
    private static readonly DateTimeOffset RetentionTestNow =
        new(2026, 7, 30, 13, 0, 0, TimeSpan.Zero);

    public static async Task<int> HoldUncommittedWriteAsync(
        string databasePath,
        string readyPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var head = connection.CreateCommand();
        head.Transaction = transaction;
        head.CommandText = """
            SELECT content_hash
              FROM retained_head
             WHERE project_id = 'project-alpha'
               AND kind = 'task-snapshot'
               AND document_id = 'tasks-main';
            """;
        var previousHash = (string?)await head.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The crash probe could not find its retained head.");

        var canonical = Encoding.UTF8.GetBytes("""{"crashProbe":true}""");
        var contentHash = CortexCanonicalJson.Sha256(canonical);
        var auditId = CortexCanonicalJson.Sha256(
            Encoding.UTF8.GetBytes($"crash-probe|{previousHash}|{contentHash}"));
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO retained_document(
                project_id, kind, document_id, revision, previous_hash,
                idempotency_key, producer_id, contract_id, content_hash,
                canonical_json, search_text, captured_at_utc, expires_at_utc)
            VALUES(
                'project-alpha', 'task-snapshot', 'tasks-main', 2, $previous,
                'crash-probe', 'producer-alpha', 'pegasus.agent-task-snapshot/1.0',
                $contentHash, $canonical, 'uncommitted crash probe',
                '2026-07-30T12:00:00.0000000Z', '2026-07-31T12:00:00.0000000Z');
            UPDATE retained_head
               SET revision = 2, content_hash = $contentHash
             WHERE project_id = 'project-alpha'
               AND kind = 'task-snapshot'
               AND document_id = 'tasks-main';
            INSERT INTO retention_audit(
                audit_id, project_id, kind, document_id, revision,
                content_hash, producer_id, occurred_at_utc)
            VALUES(
                $auditId, 'project-alpha', 'task-snapshot', 'tasks-main', 2,
                $contentHash, 'producer-alpha', '2026-07-30T12:00:00.0000000Z');
            INSERT INTO retained_document_fts(
                project_id, kind, document_id, revision, content)
            VALUES(
                'project-alpha', 'task-snapshot', 'tasks-main', 2,
                'uncommitted crash probe');
            """;
        insert.Parameters.AddWithValue("$previous", previousHash);
        insert.Parameters.AddWithValue("$contentHash", contentHash);
        insert.Parameters.AddWithValue("$canonical", canonical);
        insert.Parameters.AddWithValue("$auditId", auditId);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);

        await File.WriteAllTextAsync(readyPath, contentHash, Encoding.ASCII)
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 2;
    }

    public static async Task RevisionChainAsync()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var first = Request(1, previous: null, idempotency: "idem-1", summary: "first active task");
            var appended = await store.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);
            Require(appended.Disposition == CortexRetentionWriteDispositions.Appended, "Revision one was not appended.");

            var replay = await store.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);
            Require(replay.Disposition == CortexRetentionWriteDispositions.Idempotent, "Exact replay was not idempotent.");

            var second = Request(2, first.ContentHash, "idem-2", "second current action");
            var continued = await store.AppendAsync(second, CancellationToken.None).ConfigureAwait(false);
            Require(continued.Disposition == CortexRetentionWriteDispositions.Appended, "Revision two was not appended.");

            var head = await store.FindHeadAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                CancellationToken.None).ConfigureAwait(false);
            Require(head is { Revision: 2 } && head.ContentHash == second.ContentHash, "Head did not advance exactly.");
            var original = await store.FindRevisionAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                1,
                CancellationToken.None).ConfigureAwait(false);
            Require(original is { Revision: 1 } && original.CanonicalUtf8.SequenceEqual(first.CanonicalUtf8),
                "The original immutable bytes changed.");
            var streamed = new List<CortexRetainedDocument>();
            await foreach (var retained in store.ReadRevisionRangeAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                firstRevision: 1,
                lastRevision: 2,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                streamed.Add(retained);
            }
            Require(
                streamed.Select(item => item.Revision).SequenceEqual([1L, 2L]) &&
                streamed[0].CanonicalUtf8.SequenceEqual(first.CanonicalUtf8) &&
                streamed[1].CanonicalUtf8.SequenceEqual(second.CanonicalUtf8),
                "The bounded revision reader did not stream exact ordered bytes.");
            var alternateTaskSet = Request(
                1,
                previous: null,
                idempotency: "idem-alternate-task-set",
                summary: "ambiguous alternate task set") with
            {
                DocumentId = "tasks-alternate"
            };
            var alternateResult = await store.AppendAsync(
                alternateTaskSet,
                CancellationToken.None).ConfigureAwait(false);
            Require(
                alternateResult.Disposition ==
                    CortexRetentionWriteDispositions.Conflict &&
                await store.FindRevisionAsync(
                    "project-alpha",
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    "tasks-alternate",
                    1,
                    CancellationToken.None).ConfigureAwait(false) is null,
                "A second task-set identity was retained for one project.");
            var integrity = await store.CheckIntegrityAsync(CancellationToken.None).ConfigureAwait(false);
            Require(integrity.Healthy && integrity.RetainedDocuments == 2 && integrity.AuditEvents == 2,
                "Integrity or append-only counts were incorrect.");
        }).ConfigureAwait(false);
    }

    public static async Task ConflictAndStaleBaseAsync()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var first = Request(1, null, "idem-1", "accepted base");
            await store.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);
            var conflict = Request(1, null, "idem-1", "different bytes");
            var conflictResult = await store.AppendAsync(conflict, CancellationToken.None).ConfigureAwait(false);
            Require(conflictResult.Disposition == CortexRetentionWriteDispositions.Conflict,
                "A reused idempotency key with different bytes did not conflict.");
            var metadataConflict = first with { DocumentId = "tasks-other" };
            var metadataConflictResult = await store.AppendAsync(
                metadataConflict,
                CancellationToken.None).ConfigureAwait(false);
            Require(metadataConflictResult.Disposition == CortexRetentionWriteDispositions.Conflict,
                "A reused idempotency key with different document identity was treated as an exact replay.");

            var wrongPrevious = new string('a', 64);
            var stale = Request(2, wrongPrevious, "idem-2", "stale continuation");
            var staleResult = await store.AppendAsync(stale, CancellationToken.None).ConfigureAwait(false);
            Require(staleResult.Disposition == CortexRetentionWriteDispositions.StaleBase &&
                    staleResult.CurrentHeadHash == first.ContentHash,
                "A stale continuation did not return the exact current head.");
        }).ConfigureAwait(false);
    }

    public static async Task RestartAndSearchAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            var clock = new FixedTimeProvider(RetentionTestNow);
            await using (var firstStore = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(path),
                clock))
            {
                await firstStore.AppendAsync(
                    Request(1, null, "idem-1", "heartbeat continuation checkpoint"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            await using var restarted = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(path),
                clock);
            await restarted.RebuildSearchIndexAsync(CancellationToken.None).ConfigureAwait(false);
            var hits = await restarted.SearchHeadsAsync(
                "project-alpha",
                "continuation checkpoint",
                3,
                CancellationToken.None).ConfigureAwait(false);
            Require(hits.Count == 1 && hits[0].DocumentId == "tasks-main",
                "Restarted search did not return the retained head.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task ConcurrentWriterAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using var firstStore = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            await using var secondStore = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            var first = Request(1, null, "idem-1", "concurrent base");
            await firstStore.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);

            var left = Request(2, first.ContentHash, "idem-left", "left continuation");
            var right = Request(2, first.ContentHash, "idem-right", "right continuation");
            var results = await Task.WhenAll(
                firstStore.AppendAsync(left, CancellationToken.None).AsTask(),
                secondStore.AppendAsync(right, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Require(results.Count(result =>
                    result.Disposition == CortexRetentionWriteDispositions.Appended) == 1,
                "Competing writers did not produce exactly one accepted revision.");
            Require(results.Count(result =>
                    result.Disposition == CortexRetentionWriteDispositions.StaleBase) == 1,
                "The losing writer was not reported as a stale-base conflict.");
            var head = await firstStore.FindHeadAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                CancellationToken.None).ConfigureAwait(false);
            Require(head is { Revision: 2 } &&
                    (head.ContentHash == left.ContentHash || head.ContentHash == right.ContentHash),
                "Concurrent retention selected neither exact submitted revision.");

            var shared = Request(3, head!.ContentHash, "idem-shared", "shared exact retry");
            var replayResults = await Task.WhenAll(
                firstStore.AppendAsync(shared, CancellationToken.None).AsTask(),
                secondStore.AppendAsync(shared, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Require(
                replayResults.Count(result =>
                    result.Disposition == CortexRetentionWriteDispositions.Appended) == 1 &&
                replayResults.Count(result =>
                    result.Disposition == CortexRetentionWriteDispositions.Idempotent) == 1,
                "Cross-instance exact retries did not resolve to one append and one idempotent replay.");

            var differentLeft = Request(
                1,
                previous: null,
                idempotency: "different-first-left",
                summary: "different first task set left") with
            {
                ProjectId = "project-task-set-race",
                DocumentId = "tasks-left"
            };
            var differentRight = Request(
                1,
                previous: null,
                idempotency: "different-first-right",
                summary: "different first task set right") with
            {
                ProjectId = "project-task-set-race",
                DocumentId = "tasks-right"
            };
            var differentResults = await Task.WhenAll(
                firstStore.AppendAsync(
                    differentLeft,
                    CancellationToken.None).AsTask(),
                secondStore.AppendAsync(
                    differentRight,
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            Require(
                differentResults.Count(result =>
                    result.Disposition ==
                        CortexRetentionWriteDispositions.Appended) == 1 &&
                differentResults.Count(result =>
                    result.Disposition ==
                        CortexRetentionWriteDispositions.Conflict) == 1,
                "Concurrent first writes retained competing task-set identities.");
            var leftHead = await firstStore.FindHeadAsync(
                "project-task-set-race",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-left",
                CancellationToken.None).ConfigureAwait(false);
            var rightHead = await firstStore.FindHeadAsync(
                "project-task-set-race",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-right",
                CancellationToken.None).ConfigureAwait(false);
            Require(
                (leftHead is null) != (rightHead is null),
                "The task-set race retained zero or multiple project heads.");
            var leftRevision = await firstStore.FindRevisionAsync(
                "project-task-set-race",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-left",
                1,
                CancellationToken.None).ConfigureAwait(false);
            var rightRevision = await firstStore.FindRevisionAsync(
                "project-task-set-race",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-right",
                1,
                CancellationToken.None).ConfigureAwait(false);
            var raceIntegrity = await firstStore.CheckIntegrityAsync(
                CancellationToken.None).ConfigureAwait(false);
            Require(
                (leftRevision is null) != (rightRevision is null) &&
                raceIntegrity is
                {
                    Healthy: true,
                    RetainedDocuments: 4,
                    AuditEvents: 4
                },
                "The losing task-set race left a revision or audit artifact.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task BackupAndRestoreAsync()
    {
        var root = NewRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source", "cortex.db");
            var backupPath = Path.Combine(root, "backup", "snapshot.db");
            await using (var source = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(sourcePath)))
            {
                await source.AppendAsync(
                    Request(1, null, "idem-1", "backup evidence"),
                    CancellationToken.None).ConfigureAwait(false);
                await source.BackupAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            }

            var restoredPath = Path.Combine(root, "restored", "cortex.db");
            await SqliteCortexStoreRecovery.RestoreNewAsync(
                backupPath,
                new SqliteCortexStoreOptions(restoredPath),
                CancellationToken.None).ConfigureAwait(false);
            await ExpectAsync<IOException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                backupPath,
                new SqliteCortexStoreOptions(restoredPath),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
            await using var restored = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(restoredPath));
            var integrity = await restored.CheckIntegrityAsync(CancellationToken.None).ConfigureAwait(false);
            var head = await restored.FindHeadAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                CancellationToken.None).ConfigureAwait(false);
            Require(integrity.Healthy && head is { Revision: 1 }, "The verified backup did not restore.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task InputAndPathBoundaryAsync()
    {
        var request = Request(1, null, "idem-1", "unaltered");
        var altered = request with
        {
            CanonicalUtf8 = Encoding.UTF8.GetBytes("{\"changed\":true}")
        };
        Expect<ArgumentException>(() => altered.Validate());
        foreach (var noncanonical in new[]
                 {
                     "not-json",
                     "{ \"value\":1}",
                     "{\"value\":1.0}",
                     "{\"value\":1e0}",
                     "{\"value\":-0}",
                     "{\"value\":1,\"value\":1}"
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(noncanonical);
            var invalid = request with
            {
                CanonicalUtf8 = bytes,
                ContentHash = CortexCanonicalJson.Sha256(bytes)
            };
            Expect<ArgumentException>(() => invalid.Validate());
        }
        Expect<ArgumentException>(() =>
            new SqliteCortexStoreOptions(Path.Combine(Path.GetTempPath(), "not-cortex.sqlite")).Validate());

        await WithStoreAsync(async (store, _) =>
        {
            var result = await store.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            Require(result.Disposition == CortexRetentionWriteDispositions.Appended,
                "The valid control document did not append.");
        }).ConfigureAwait(false);

        await ExistingDatabaseLinkBoundaryAsync().ConfigureAwait(false);
    }

    public static async Task CurrentUserBoundaryAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using var store = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            RequireWindowsCurrentUserOnly(Path.GetDirectoryName(path)!, isDirectory: true);
            RequireWindowsCurrentUserOnly(path, isDirectory: false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task InvalidBackupAsync()
    {
        var root = NewRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source", "cortex.db");
            var backupPath = Path.Combine(root, "backup", "snapshot.db");
            await using (var source = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(sourcePath)))
            {
                await source.AppendAsync(
                    Request(1, null, "idem-1", "recovery validation"),
                    CancellationToken.None).ConfigureAwait(false);
                await source.BackupAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            }

            var corruptPath = Path.Combine(root, "backup", "corrupt.db");
            File.Copy(backupPath, corruptPath);
            await using (var corrupt = new FileStream(corruptPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                corrupt.Position = 0;
                corrupt.WriteByte(0);
            }
            await ExpectAsync<InvalidDataException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                corruptPath,
                new SqliteCortexStoreOptions(Path.Combine(root, "corrupt-restore", "cortex.db")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

            var futurePath = Path.Combine(root, "backup", "future.db");
            File.Copy(backupPath, futurePath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = futurePath,
                             Mode = SqliteOpenMode.ReadWrite,
                             Pooling = false
                         }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE cortex_schema SET version = 2 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await ExpectAsync<InvalidDataException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                futurePath,
                new SqliteCortexStoreOptions(Path.Combine(root, "future-restore", "cortex.db")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

            var semanticPath = Path.Combine(root, "backup", "semantic.db");
            File.Copy(backupPath, semanticPath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = semanticPath,
                             Mode = SqliteOpenMode.ReadWrite,
                             Pooling = false
                         }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE retained_document SET canonical_json = X'7B7D';";
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await ExpectAsync<InvalidDataException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                semanticPath,
                new SqliteCortexStoreOptions(Path.Combine(root, "semantic-restore", "cortex.db")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task ForgedAndLiveBackupAsync()
    {
        var root = NewRoot();
        try
        {
            var forgedPath = Path.Combine(root, "forged.db");
            await using (var forged = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = forgedPath,
                             Mode = SqliteOpenMode.ReadWriteCreate,
                             Pooling = false
                         }.ToString()))
            {
                await forged.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = forged.CreateCommand();
                command.CommandText = """
                    PRAGMA page_size = 4096;
                    CREATE TABLE cortex_schema(
                        singleton INTEGER PRIMARY KEY,
                        version INTEGER NOT NULL,
                        CHECK(1 OR 'singletonintegerprimarykeycheck(singleton=1)'),
                        CHECK(1 OR 'versionintegernotnullcheck(version>0)')
                    ) STRICT;
                    INSERT INTO cortex_schema VALUES(1, 1);
                    CREATE TABLE retained_document(
                        project_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        document_id TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        previous_hash TEXT NULL,
                        idempotency_key TEXT NOT NULL,
                        producer_id TEXT NOT NULL,
                        contract_id TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        canonical_json BLOB NOT NULL,
                        search_text TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        expires_at_utc TEXT NOT NULL,
                        PRIMARY KEY(project_id, kind, document_id, revision),
                        UNIQUE(project_id, kind, producer_id, idempotency_key),
                        CHECK(1 OR 'revisionintegernotnullcheck(revision>0)'),
                        CHECK(1 OR 'content_hashtextnotnullcheck(length(content_hash)=64)'),
                        CHECK(1 OR 'canonical_jsonblobnotnullcheck(length(canonical_json)between2and32768)'),
                        CHECK(1 OR 'search_texttextnotnullcheck(length(search_text)<=4096)'),
                        CHECK(1 OR 'check((revision=1andprevious_hashisnull)or(revision>1andlength(previous_hash)=64))')
                    ) STRICT;
                    CREATE TABLE retained_head(
                        project_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        document_id TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        content_hash TEXT NOT NULL,
                        PRIMARY KEY(project_id, kind, document_id),
                        FOREIGN KEY(project_id, kind, document_id, revision)
                            REFERENCES retained_document(project_id, kind, document_id, revision)
                            ON DELETE RESTRICT,
                        CHECK(1 OR 'content_hashtextnotnullcheck(length(content_hash)=64)')
                    ) STRICT;
                    CREATE TABLE retention_audit(
                        audit_id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        document_id TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        content_hash TEXT NOT NULL,
                        producer_id TEXT NOT NULL,
                        occurred_at_utc TEXT NOT NULL,
                        FOREIGN KEY(project_id, kind, document_id, revision)
                            REFERENCES retained_document(project_id, kind, document_id, revision)
                            ON DELETE RESTRICT,
                        CHECK(1 OR 'audit_idtextprimarykeycheck(length(audit_id)=64)'),
                        CHECK(1 OR 'content_hashtextnotnullcheck(length(content_hash)=64)')
                    ) STRICT;
                    CREATE VIRTUAL TABLE retained_document_fts USING fts5(
                        project_id UNINDEXED,
                        kind UNINDEXED,
                        document_id UNINDEXED,
                        revision UNINDEXED,
                        content,
                        tokenize = 'unicode61'
                    );
                    """;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await ExpectAsync<InvalidDataException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                forgedPath,
                new SqliteCortexStoreOptions(Path.Combine(root, "forged-restore", "cortex.db")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

            var impostorPath = Path.Combine(root, "impostor", "cortex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(impostorPath)!);
            File.Copy(forgedPath, impostorPath);
            await using (var impostor = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(impostorPath)))
            {
                await ExpectAsync<InvalidDataException>(() =>
                    impostor.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = impostorPath,
                             Mode = SqliteOpenMode.ReadOnly,
                             Pooling = false
                         }.ToString()))
            {
                await verify.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = verify.CreateCommand();
                command.CommandText = """
                    SELECT sql
                      FROM sqlite_schema
                     WHERE type = 'table' AND name = 'retained_document';
                    """;
                var retainedSql = Convert.ToString(
                    await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false));
                Require(
                    retainedSql is not null &&
                    retainedSql.Contains(
                        "CHECK(1 OR 'revisionintegernotnullcheck(revision>0)')",
                        StringComparison.Ordinal) &&
                    !retainedSql.Contains(
                        "revision INTEGER NOT NULL CHECK(revision > 0)",
                        StringComparison.Ordinal),
                    "Initialization mutated a forged existing database before rejecting it.");
            }

            var validPath = Path.Combine(root, "valid", "cortex.db");
            var backupPath = Path.Combine(root, "backup", "snapshot.db");
            await using (var source = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(validPath)))
            {
                await source.AppendAsync(
                    Request(1, null, "live-sidecar-base", "live sidecar rejection"),
                    CancellationToken.None).ConfigureAwait(false);
                await source.BackupAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            }
            Require(
                !File.Exists(backupPath + "-wal") && !File.Exists(backupPath + "-shm"),
                "A deterministic backup retained SQLite sidecars.");
            await File.WriteAllBytesAsync(
                backupPath + "-wal",
                [1, 2, 3],
                CancellationToken.None).ConfigureAwait(false);
            await ExpectAsync<InvalidDataException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                backupPath,
                new SqliteCortexStoreOptions(Path.Combine(root, "live-restore", "cortex.db")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

            File.Delete(backupPath + "-wal");
            var orphanPath = Path.Combine(root, "orphan", "cortex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
            await File.WriteAllBytesAsync(
                orphanPath + "-wal",
                [4, 5, 6],
                CancellationToken.None).ConfigureAwait(false);
            await using (var orphanStore = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(orphanPath)))
            {
                await ExpectAsync<IOException>(() =>
                    orphanStore.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            await ExpectAsync<IOException>(() => SqliteCortexStoreRecovery.RestoreNewAsync(
                backupPath,
                new SqliteCortexStoreOptions(orphanPath),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task SemanticIntegrityAsync()
    {
        await AssertTamperDetectedAsync(
            "UPDATE retained_document SET canonical_json = X'7B7D' WHERE revision = 1;",
            verifyExactRead: true).ConfigureAwait(false);
        await AssertTamperDetectedAsync(
            $"UPDATE retained_head SET content_hash = '{new string('0', 64)}' WHERE revision = 1;",
            verifyExactRead: false).ConfigureAwait(false);
        await AssertTamperDetectedAsync(
            "DELETE FROM retention_audit WHERE revision = 1;",
            verifyExactRead: false).ConfigureAwait(false);
        await AssertTamperDetectedAsync(
            "DELETE FROM retained_document_fts WHERE revision = '1';",
            verifyExactRead: false).ConfigureAwait(false);
        await AssertMissingTaskHeadIndexMigratesAsync().ConfigureAwait(false);
        await AssertLegacyMultiTaskHeadRejectedAsync().ConfigureAwait(false);
    }

    public static async Task ContentScreeningBoundaryAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using var store = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(path));
            await ExpectAsync<CortexRetentionContentRejectedException>(() =>
                store.AppendAsync(
                    Request(
                        1,
                        previous: null,
                        idempotency: "screening-secret",
                        summary: "password=do-not-retain"),
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            var integrity = await store.CheckIntegrityAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Require(
                integrity is { Healthy: true, RetainedDocuments: 0, AuditEvents: 0 },
                "A prohibited retained payload reached SQLite, audit, or search state.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task UnexpectedSchemaObjectAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using (var source = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path)))
            {
                await source.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = path,
                             Mode = SqliteOpenMode.ReadWrite,
                             Pooling = false
                         }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER retained_document_unexpected
                    AFTER INSERT ON retained_document
                    BEGIN
                        DELETE FROM retention_audit;
                    END;
                    """;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await using var reopened = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            await ExpectAsync<InvalidDataException>(() =>
                reopened.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task QuotaBoundaryAsync()
    {
        var root = NewRoot();
        try
        {
            var options = new SqliteCortexStoreOptions(
                Path.Combine(root, "profile", "cortex.db"),
                MaximumRetainedDocuments: 2,
                MaximumProjectDocuments: 1);
            await using var store = new SqliteCortexRetentionStore(options);
            var first = Request(1, null, "quota-alpha", "alpha retained");
            await store.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);
            await ExpectAsync<CortexRetentionQuotaExceededException>(() =>
                store.AppendAsync(
                    Request(2, first.ContentHash, "quota-alpha-2", "alpha overflow"),
                    CancellationToken.None).AsTask()).ConfigureAwait(false);

            var secondProject = Request(1, null, "quota-beta", "beta retained") with
            {
                ProjectId = "project-beta",
                DocumentId = "tasks-beta"
            };
            await store.AppendAsync(secondProject, CancellationToken.None).ConfigureAwait(false);
            var thirdProject = Request(1, null, "quota-gamma", "gamma overflow") with
            {
                ProjectId = "project-gamma",
                DocumentId = "tasks-gamma"
            };
            await ExpectAsync<CortexRetentionQuotaExceededException>(() =>
                store.AppendAsync(thirdProject, CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task DiskFullRollbackAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            var options = new SqliteCortexStoreOptions(
                path,
                MaximumDatabasePages: SqliteCortexStoreOptions.MinimumDatabasePages);
            var first = Request(1, null, "disk-base", "disk rollback base");
            await using var store = new SqliteCortexRetentionStore(options);
            await store.AppendAsync(first, CancellationToken.None).ConfigureAwait(false);

            var retained = first;
            var retainedCount = 1;
            SqliteException? diskFull = null;
            for (var revision = 2; revision <= 64 && diskFull is null; revision++)
            {
                var template = Request(
                    revision,
                    retained.ContentHash,
                    $"disk-overflow-{revision}",
                    $"disk overflow {revision}");
                var largeBytes = CortexCanonicalJson.Serialize(new
                {
                    contractId = template.ContractId,
                    documentId = template.DocumentId,
                    payload = new string((char)('a' + revision % 26), 30_000),
                    revision
                });
                var large = template with
                {
                    CanonicalUtf8 = largeBytes,
                    ContentHash = CortexCanonicalJson.Sha256(largeBytes)
                };
                try
                {
                    await store.AppendAsync(large, CancellationToken.None).ConfigureAwait(false);
                    retained = large;
                    retainedCount++;
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 13)
                {
                    diskFull = exception;
                }
            }
            Require(diskFull is not null, "The bounded valid schema did not reach SQLite full.");

            var head = await store.FindHeadAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                CancellationToken.None).ConfigureAwait(false);
            var integrity = await store.CheckIntegrityAsync(CancellationToken.None).ConfigureAwait(false);
            Require(
                head is not null &&
                head.Revision == retained.Revision &&
                head.ContentHash == retained.ContentHash &&
                integrity.Healthy &&
                integrity.RetainedDocuments == retainedCount &&
                integrity.AuditEvents == retainedCount,
                "A failed disk-full append left a partial revision, head, audit, or index change.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task CrashRollbackAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using (var store =
                new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path)))
            {
                await store.AppendAsync(
                    Request(1, null, "crash-base", "crash rollback base"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            var readyPath = Path.Combine(root, "writer-ready.txt");
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current process path is unavailable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add("--hold-uncommitted");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(readyPath);

            using var writer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The crash writer did not start.");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(readyPath) && !writer.HasExited && DateTime.UtcNow < deadline)
                    await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                Require(File.Exists(readyPath), "The crash writer did not stage an uncommitted transaction.");
                Require(!writer.HasExited, "The crash writer exited before forced termination.");
                writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!writer.HasExited)
                {
                    writer.Kill(entireProcessTree: true);
                    await writer.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            await using var restarted =
                new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            var head = await restarted.FindHeadAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                CancellationToken.None).ConfigureAwait(false);
            var rolledBack = await restarted.FindRevisionAsync(
                "project-alpha",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "tasks-main",
                2,
                CancellationToken.None).ConfigureAwait(false);
            var integrity = await restarted.CheckIntegrityAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Require(head is { Revision: 1 }, "Crash recovery did not preserve the committed head.");
            Require(rolledBack is null, "Crash recovery retained an uncommitted revision.");
            Require(
                integrity.Healthy &&
                integrity.RetainedDocuments == 1 &&
                integrity.AuditEvents == 1,
                "Crash recovery left partial document, audit, head, or index state.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task ExpiredSearchBoundaryAsync()
    {
        var root = NewRoot();
        try
        {
            var now = RetentionTestNow.AddDays(2);
            await using var store = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(Path.Combine(root, "profile", "cortex.db")),
                new FixedTimeProvider(now));
            await store.AppendAsync(
                Request(1, null, "expired-1", "expired searchable checkpoint"),
                CancellationToken.None).ConfigureAwait(false);
            var hits = await store.SearchHeadsAsync(
                "project-alpha",
                "searchable checkpoint",
                3,
                CancellationToken.None).ConfigureAwait(false);
            Require(hits.Count == 0, "Expired retained state was returned by hot search.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertTamperDetectedAsync(string sql, bool verifyExactRead)
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using (var source = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path)))
            {
                await source.AppendAsync(
                    Request(1, null, "tamper-1", "tamper evidence"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = path,
                             Mode = SqliteOpenMode.ReadWrite,
                             Pooling = false
                         }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await using var reopened = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            var integrity = await reopened.CheckIntegrityAsync(CancellationToken.None).ConfigureAwait(false);
            Require(!integrity.Healthy, "Semantic retention tampering was reported as healthy.");
            if (verifyExactRead)
            {
                await ExpectAsync<InvalidDataException>(() => reopened.FindRevisionAsync(
                    "project-alpha",
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    "tasks-main",
                    1,
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertLegacyMultiTaskHeadRejectedAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using (var source = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(path)))
            {
                await source.AppendAsync(
                    Request(1, null, "legacy-primary", "legacy primary task set"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            var alternatePayload = CortexCanonicalJson.Serialize(new
            {
                contractId = "pegasus.agent-task-snapshot/1.0",
                documentId = "tasks-legacy-alternate",
                revision = 1,
                summary = "legacy alternate task set"
            });
            var alternateHash = CortexCanonicalJson.Sha256(alternatePayload);
            var alternateAuditId = CortexCanonicalJson.Sha256(
                "project-alpha\n" +
                CortexRetainedDocumentKinds.TaskSnapshot + "\n" +
                "tasks-legacy-alternate\n1\n" +
                alternateHash);
            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = path,
                                 Mode = SqliteOpenMode.ReadWrite,
                                 Pooling = false
                             }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX ux_retained_head_single_task_snapshot;
                    INSERT INTO retained_document(
                        project_id, kind, document_id, revision, previous_hash,
                        idempotency_key, producer_id, contract_id, content_hash,
                        canonical_json, search_text, captured_at_utc, expires_at_utc)
                    VALUES(
                        'project-alpha', 'task-snapshot',
                        'tasks-legacy-alternate', 1, NULL,
                        'legacy-alternate', 'agent-local',
                        'pegasus.agent-task-snapshot/1.0', $hash,
                        $canonical, 'legacy alternate task set',
                        '2026-07-30T12:00:00.0000000+00:00',
                        '2026-07-31T12:00:00.0000000+00:00');
                    INSERT INTO retained_head(
                        project_id, kind, document_id, revision, content_hash)
                    VALUES(
                        'project-alpha', 'task-snapshot',
                        'tasks-legacy-alternate', 1, $hash);
                    INSERT INTO retention_audit(
                        audit_id, project_id, kind, document_id, revision,
                        content_hash, producer_id, occurred_at_utc)
                    VALUES(
                        $audit, 'project-alpha', 'task-snapshot',
                        'tasks-legacy-alternate', 1, $hash, 'agent-local',
                        '2026-07-30T12:00:00.0000000+00:00');
                    INSERT INTO retained_document_fts(
                        project_id, kind, document_id, revision, content)
                    VALUES(
                        'project-alpha', 'task-snapshot',
                        'tasks-legacy-alternate', '1',
                        'legacy alternate task set');
                    """;
                command.Parameters.AddWithValue("$hash", alternateHash);
                command.Parameters.AddWithValue("$canonical", alternatePayload);
                command.Parameters.AddWithValue("$audit", alternateAuditId);
                await command.ExecuteNonQueryAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await using var reopened = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(path));
            await ExpectAsync<InvalidDataException>(() =>
                reopened.InitializeAsync(CancellationToken.None).AsTask())
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertMissingTaskHeadIndexMigratesAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using (var source = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(path)))
            {
                await source.AppendAsync(
                    Request(1, null, "migration-primary", "migration primary"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = path,
                                 Mode = SqliteOpenMode.ReadWrite,
                                 Pooling = false
                             }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await using var drop = connection.CreateCommand();
                drop.CommandText =
                    "DROP INDEX ux_retained_head_single_task_snapshot;";
                await drop.ExecuteNonQueryAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await using var reopened = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(path));
            await reopened.InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var alternate = Request(
                1,
                null,
                "migration-alternate",
                "migration alternate") with
            {
                DocumentId = "tasks-migration-alternate"
            };
            var conflict = await reopened.AppendAsync(
                alternate,
                CancellationToken.None).ConfigureAwait(false);
            Require(
                conflict.Disposition ==
                    CortexRetentionWriteDispositions.Conflict,
                "A valid legacy store did not migrate to the single task-set invariant.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task ExistingDatabaseLinkBoundaryAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "target.db");
            await File.WriteAllBytesAsync(
                target,
                Encoding.UTF8.GetBytes("must-not-change"),
                CancellationToken.None).ConfigureAwait(false);
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            var link = Path.Combine(profile, "cortex.db");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Some Windows configurations do not grant symbolic-link creation.
                return;
            }
            var original = await File.ReadAllBytesAsync(target, CancellationToken.None).ConfigureAwait(false);
            Expect<IOException>(() => new SqliteCortexStoreOptions(link).Validate());
            var actual = await File.ReadAllBytesAsync(target, CancellationToken.None).ConfigureAwait(false);
            Require(original.SequenceEqual(actual), "A linked database target was changed before rejection.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static CortexRetentionAppendRequest Request(
        long revision,
        string? previous,
        string idempotency,
        string summary)
    {
        var capturedAt = RetentionTestNow
            .AddHours(-1)
            .AddMinutes(revision == 1 ? 0 : 1);
        var payload = new
        {
            contractId = "pegasus.agent-task-snapshot/1.0",
            documentId = "tasks-main",
            revision,
            summary
        };
        var bytes = CortexCanonicalJson.Serialize(payload);
        return new CortexRetentionAppendRequest(
            "project-alpha",
            CortexRetainedDocumentKinds.TaskSnapshot,
            "tasks-main",
            revision,
            previous,
            idempotency,
            "agent-local",
            "pegasus.agent-task-snapshot/1.0",
            bytes,
            CortexCanonicalJson.Sha256(bytes),
            summary,
            capturedAt,
            capturedAt.AddDays(1));
    }

    private static async Task WithStoreAsync(Func<SqliteCortexRetentionStore, string, Task> test)
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            await using var store = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            await test(store, root).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-sqlite-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        Directory.Delete(root, recursive: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ExpectAsync<TException>(Func<Task> action)
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
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsCurrentUserOnly(string path, bool isDirectory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        Require(security.AreAccessRulesProtected, "The local Cortex ACL still inherits broader access.");
        Require(
            security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
            owner.Equals(currentUser),
            "The local Cortex ACL owner is not the current OS user.");
        var allowedIdentities = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Distinct()
            .ToArray();
        Require(
            allowedIdentities.Length == 1 && allowedIdentities[0].Equals(currentUser),
            "The local Cortex ACL grants access to an identity other than the current OS user.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
