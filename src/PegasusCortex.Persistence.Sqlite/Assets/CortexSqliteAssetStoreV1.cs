using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;
using File = System.IO.File;

namespace Pegasus.Cortex.Persistence.Sqlite.Assets;

/// <summary>
/// Explicit new-generation asset catalog. Metadata and inline payload chunks live in SQLite;
/// only payloads larger than the pinned cutoff have external files. This provider never probes,
/// imports, repairs or deletes a legacy asset store. DELETE journal with FULL synchronization keeps
/// readonly opens free of WAL/SHM creation; commits may briefly contend with readers. Processes under
/// one OS owner share one boundary.
/// </summary>
public sealed class CortexSqliteAssetStoreV1 : ICortexAssetStreamStoreV1, IDisposable, IAsyncDisposable
{
    public const string StoreGeneration = "pegasus.cortex.asset-hybrid/1";
    public const int SchemaVersion = 1;
    private const int ChunkSize = CortexAssetCodecV2.ChunkSize;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly CortexHybridAssetOptionsV1 _options;
    private readonly string _largeRoot;
    private readonly string _storeId;
    private int _disposed;

    private CortexSqliteAssetStoreV1(string databasePath, string largePayloadRoot,
        CortexHybridAssetOptionsV1 options, string storeId)
    {
        DatabasePath = databasePath;
        Root = Path.GetDirectoryName(databasePath)!;
        _largeRoot = largePayloadRoot;
        _options = options;
        _storeId = storeId;
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string LargePayloadRoot => _largeRoot;
    public long MaximumAssetBytes => _options.MaximumAssetBytes;
    public long InlineCutoffBytes => _options.InlineCutoffBytes;

    public static CortexSqliteAssetStoreV1 CreateNew(string databasePath, string largePayloadRoot,
        CortexHybridAssetOptionsV1 options) => Normalize(() =>
    {
        options = (options ?? throw new CortexAssetException("asset_configuration_invalid")).Validate();
        var (database, large) = ValidateLocations(databasePath, largePayloadRoot);
        if (File.Exists(database) || Directory.Exists(database) || File.Exists(database + "-wal") ||
            File.Exists(database + "-shm") || File.Exists(database + "-journal") || Directory.Exists(large))
            throw new CortexAssetException("asset_store_exists");
        var store = new CortexSqliteAssetStoreV1(database, large, options, Guid.NewGuid().ToString("N"));
        var owned = false;
        var published = false;
        try
        {
            // Reserve exactly this absent name under the already-private parent. No parent repair.
            using (new FileStream(database, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) owned = true;
            RequirePrivateEntry(database, directory: false);
            using var connection = store.OpenConnection(write: true, verify: false, creating: true);
            Execute(connection, null, "PRAGMA page_size=4096; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ScalarLong(connection, transaction,
                    "SELECT count(*) FROM sqlite_schema WHERE substr(name,1,7)!='sqlite_';") != 0)
                throw new CortexAssetException("asset_schema_invalid");
            foreach (var definition in Definitions) Execute(connection, transaction, definition.Value);
            Execute(connection, transaction, """
                INSERT INTO asset_meta VALUES(1,$version,$generation,$id,$cutoff,$maximum,$large);
                INSERT INTO asset_scopes VALUES('knowledge.shared');
                """, ("$version", SchemaVersion), ("$generation", StoreGeneration), ("$id", store._storeId),
                ("$cutoff", options.InlineCutoffBytes), ("$maximum", options.MaximumAssetBytes), ("$large", large));
            transaction.Commit();
            published = true;
            store.VerifyCatalog(connection);
            return store;
        }
        catch
        {
            if (owned && !published)
            {
                // Only this failed create's exact files are owned. Never enumerate a directory.
                foreach (var suffix in new[] { "-journal", "-wal", "-shm", "" })
                {
                    try { var path = database + suffix; CheckPath(path); if (File.Exists(path)) File.Delete(path); }
                    catch { /* Preserve uncertain cleanup rather than replacing the original failure. */ }
                }
            }
            throw;
        }
    });

    public static CortexSqliteAssetStoreV1 OpenExisting(string databasePath, string largePayloadRoot,
        CortexHybridAssetOptionsV1 options) => Normalize(() =>
    {
        options = (options ?? throw new CortexAssetException("asset_configuration_invalid")).Validate();
        var (database, large) = ValidateLocations(databasePath, largePayloadRoot);
        if (!File.Exists(database)) throw new CortexAssetException("asset_store_missing");
        var candidate = new CortexSqliteAssetStoreV1(database, large, options, "");
        using var connection = candidate.OpenConnection(write: false, verify: false);
        var id = candidate.VerifyCatalog(connection);
        return new CortexSqliteAssetStoreV1(database, large, options, id);
    });

    public Task<CortexAssetV2> ImportAsync(string source, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default,
        string? expectedContentSha256 = null) => GuardAsync(async () =>
    {
        CheckPath(source);
        if (!string.Equals(source, Path.GetFullPath(source), PathComparison))
            throw new CortexAssetException("asset_path_invalid");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ImportStreamCoreAsync(input, input.Length, metadata, expectedRef, cancellationToken,
            expectedContentSha256).ConfigureAwait(false);
    });

    /// <summary>The caller owns the stream. Exactly contentLength bytes through EOF are admitted.</summary>
    public Task<CortexAssetV2> ImportStreamAsync(Stream source, long contentLength, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default,
        string? expectedContentSha256 = null) => GuardAsync(() => ImportStreamCoreAsync(source, contentLength,
            metadata, expectedRef, cancellationToken, expectedContentSha256));

    private async Task<CortexAssetV2> ImportStreamCoreAsync(Stream source, long contentLength,
        CortexAssetSpecV2 metadata, string? expectedRef, CancellationToken token, string? expectedContentSha256)
    {
        ValidateImport(metadata, expectedRef, expectedContentSha256);
        if (source is null || !source.CanRead || contentLength < 0 || contentLength > MaximumAssetBytes)
            throw new CortexAssetException("asset_source_missing_or_oversized");
        // All digests have fixed width. Reject known canonical-size overflow before staging bytes.
        _ = CortexAssetCodecV2.Serialize(metadata, new string('0', 64), contentLength, new string('0', 64));
        token.ThrowIfCancellationRequested();
        if (contentLength <= _options.InlineCutoffBytes)
        {
            using var connection = OpenConnection(write: true);
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireStageEmpty(connection, transaction);
            RequireExpectedHead(connection, transaction, metadata, expectedRef);
            // Keep arbitrary configurable inline sizes out of managed memory and raw temporary files.
            // All staged SQL chunks roll back on failure and must be gone before commit.
            var prepared = await CopyAndHashAsync(source, contentLength, null,
                (ordinal, hash, bytes) =>
                {
                    Execute(connection, transaction, "INSERT INTO asset_import_chunks VALUES($n,$hash,$bytes);",
                        ("$n", ordinal), ("$hash", hash), ("$bytes", bytes));
                    return ValueTask.CompletedTask;
                }, token).ConfigureAwait(false);
            RequireExpectedDigest(prepared, expectedContentSha256);
            var existing = TryLoadObject(connection, transaction, prepared.Sha, metadata);
            if (existing is null)
            {
                InsertObject(connection, transaction, prepared, tier: 0);
                Execute(connection, transaction, """
                    INSERT INTO asset_chunks(sha,ordinal,hash,payload)
                    SELECT $sha,ordinal,hash,payload FROM asset_import_chunks ORDER BY ordinal;
                    """, ("$sha", prepared.Sha));
            }
            else
            {
                RequireSameObject(existing, prepared, 0);
                await VerifyWholeObjectAsync(connection, transaction, existing, token).ConfigureAwait(false);
            }
            Execute(connection, transaction, "DELETE FROM asset_import_chunks;");
            var result = CommitReference(connection, transaction, metadata, prepared, expectedRef);
            RequireStageEmpty(connection, transaction);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }

        EnsureLargeRoot();
        var temporary = PayloadPath(".p-" + Guid.NewGuid().ToString("N"));
        var temporaryOwned = false;
        try
        {
            Prepared prepared;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             ChunkSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryOwned = true;
                RequirePrivateEntry(temporary, directory: false);
                prepared = await CopyAndHashAsync(source, contentLength, output, null, token).ConfigureAwait(false);
                RequireExpectedDigest(prepared, expectedContentSha256);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            using var connection = OpenConnection(write: true);
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireStageEmpty(connection, transaction);
            RequireExpectedHead(connection, transaction, metadata, expectedRef);
            var existing = TryLoadObject(connection, transaction, prepared.Sha, metadata);
            if (existing is null)
            {
                var final = PayloadPath(prepared.Sha);
                if (File.Exists(final))
                    await VerifyExternalAsync(final, prepared, token).ConfigureAwait(false);
                else
                {
                    File.Move(temporary, final, overwrite: false);
                    temporaryOwned = false;
                    RequirePrivateEntry(final, directory: false);
                }
                InsertObject(connection, transaction, prepared, tier: 1);
                for (var ordinal = 0; ordinal < prepared.Hashes.Length; ordinal++)
                    Execute(connection, transaction, "INSERT INTO asset_chunks VALUES($sha,$n,$hash,NULL);",
                        ("$sha", prepared.Sha), ("$n", ordinal), ("$hash", prepared.Hashes[ordinal]));
            }
            else
            {
                RequireSameObject(existing, prepared, 1);
                await VerifyWholeObjectAsync(connection, transaction, existing, token).ConfigureAwait(false);
            }
            var result = CommitReference(connection, transaction, metadata, prepared, expectedRef);
            RequireStageEmpty(connection, transaction);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        finally
        {
            if (temporaryOwned) DeleteOwnedFile(temporary);
            // A newly published object may already be observed by a racing owner process after a
            // failed commit. Preserve it as an unreferenced object; no catalog-wide GC is implicit.
        }
    }

    public Task<CortexAssetV2> ImportExistingObjectAsync(string contentSha256, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        ValidateImport(metadata, expectedRef, contentSha256);
        using var connection = OpenConnection(write: true);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireStageEmpty(connection, transaction);
        RequireExpectedHead(connection, transaction, metadata, expectedRef);
        var existing = TryLoadObject(connection, transaction, contentSha256, metadata)
            ?? throw new CortexAssetException("asset_file_unavailable");
        await VerifyWholeObjectAsync(connection, transaction, existing, cancellationToken).ConfigureAwait(false);
        var result = CommitReference(connection, transaction, metadata, existing.Prepared, expectedRef);
        RequireStageEmpty(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    });

    public CortexAssetV2 Read(string ns, string id, string reference) => Guard(() =>
    {
        CortexAssetCodecV2.RequireName(ns, 63); CortexAssetCodecV2.RequireName(id, 127);
        var asset = ReadReference(reference);
        if (asset.Metadata.Namespace != ns || asset.Metadata.Id != id)
            throw new CortexAssetException("asset_scope_mismatch");
        return asset;
    });

    public CortexAssetV2 ReadReference(string ns, string reference) => Guard(() =>
    {
        CortexAssetCodecV2.RequireName(ns, 63);
        var asset = ReadReference(reference);
        if (asset.Metadata.Namespace != ns) throw new CortexAssetException("asset_scope_mismatch");
        return asset;
    });

    public CortexAssetV2 ReadReference(string reference) => Guard(() =>
    {
        CortexAssetCodecV2.RequireHash(reference);
        using var connection = OpenConnection(write: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return LoadReference(connection, transaction, reference);
    });

    public CortexAssetV2? Current(string ns, string id) => Guard(() =>
    {
        CortexAssetCodecV2.RequireName(ns, 63); CortexAssetCodecV2.RequireName(id, 127);
        using var connection = OpenConnection(write: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reference = CurrentReference(connection, transaction, ns, id);
        if (reference is null) return null;
        var asset = LoadReference(connection, transaction, reference);
        if (asset.Metadata.Namespace != ns || asset.Metadata.Id != id)
            throw new CortexAssetException("asset_scope_mismatch");
        return asset;
    });

    public Task<CortexAssetBytesV2> ReadBytesAsync(string ns, string id, string reference,
        long offset = 0, int length = ChunkSize, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        CortexAssetCodecV2.RequireName(ns, 63); CortexAssetCodecV2.RequireName(id, 127);
        CortexAssetCodecV2.RequireHash(reference);
        using var connection = OpenConnection(write: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var asset = LoadReference(connection, transaction, reference);
        if (asset.Metadata.Namespace != ns || asset.Metadata.Id != id)
            throw new CortexAssetException("asset_scope_mismatch");
        var wanted = CortexAssetCodecV2.ValidateRange(asset.Size, offset, length);
        var stored = TryLoadObject(connection, transaction, asset.Sha256, asset.Metadata)
            ?? throw new CortexAssetException("asset_integrity_failed");
        var result = new byte[wanted];
        await using var external = stored.Tier == 1 ? OpenPayload(stored.Prepared.Sha) : null;
        if (external is not null && external.Length != asset.Size)
            throw new CortexAssetException("asset_integrity_failed");
        var filled = 0;
        while (filled < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = checked((int)((offset + filled) / ChunkSize));
            var bytes = await ReadChunkAsync(connection, transaction, stored, ordinal, external, cancellationToken)
                .ConfigureAwait(false);
            var take = Math.Min(bytes.Length, wanted - filled);
            bytes.AsSpan(0, take).CopyTo(result.AsSpan(filled));
            filled += take;
        }
        return new CortexAssetBytesV2(result, offset, asset.Size, Hex(SHA256.HashData(result)), asset.Sha256);
    });

    public CortexAssetSearchV2 Search(string ns, string query, int limit = 16, string? cursor = null,
        CancellationToken cancellationToken = default) => Guard(() =>
    {
        var words = CortexAssetCodecV2.ValidateSearch(ns, query, limit);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection(write: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = BoundedScalarText(connection, transaction,
            "SELECT generation,typeof(generation),length(CAST(generation AS BLOB)) FROM asset_namespaces WHERE ns=$ns;",
            32, ("$ns", ns));
        if (generation is null)
        {
            if (ScalarLong(connection, transaction, "SELECT EXISTS(SELECT 1 FROM asset_heads WHERE ns=$ns LIMIT 1);", ("$ns", ns)) != 0)
                throw new CortexAssetException("asset_integrity_failed");
            if (cursor is not null) throw new CortexAssetException("asset_cursor_stale_or_invalid");
            return new CortexAssetSearchV2([], null, 0, true);
        }
        if (!Guid.TryParseExact(generation, "N", out _)) throw new CortexAssetException("asset_integrity_failed");
        var binding = Hex(SHA256.HashData(Utf8.GetBytes(_storeId + "\0" + ns + "\0" + query + "\0" +
            limit.ToString(CultureInfo.InvariantCulture) + "\0" + generation)))[..16];
        var after = "";
        if (cursor is not null)
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2 || parts[0] != binding || parts[1].Length != 66 ||
                parts[1].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                parts[1][..2] != parts[1].Substring(2, 2))
                throw new CortexAssetException("asset_cursor_stale_or_invalid");
            after = parts[1];
        }
        using var command = Command(connection, transaction, """
            SELECT id,sort_key,ref,typeof(id),length(CAST(id AS BLOB)),
                typeof(sort_key),length(CAST(sort_key AS BLOB)),typeof(ref),length(CAST(ref AS BLOB))
            FROM asset_heads WHERE ns=$ns AND sort_key>$after COLLATE BINARY
            ORDER BY sort_key COLLATE BINARY LIMIT 513;
            """, ("$ns", ns), ("$after", after));
        using var reader = command.ExecuteReader();
        var items = new List<CortexAssetV2>(limit);
        var scanned = 0; var more = false; var last = after;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= CortexAssetCodecV2.MaximumScan || items.Count >= limit) { more = true; break; }
            var id = RequiredText(reader, 0, 3, 4, 127);
            var key = RequiredText(reader, 1, 5, 6, 66);
            var reference = RequiredText(reader, 2, 7, 8, 64);
            if (key != SortKey(id)) throw new CortexAssetException("asset_metadata_integrity_failed");
            var asset = LoadReference(connection, transaction, reference);
            if (asset.Metadata.Namespace != ns || asset.Metadata.Id != id)
                throw new CortexAssetException("asset_scope_mismatch");
            last = key; scanned++;
            if (CortexAssetCodecV2.Matches(asset.Metadata, words)) items.Add(asset);
        }
        return new CortexAssetSearchV2(items, more ? binding + "." + last : null, scanned, !more);
    });

    public IReadOnlyList<string> ExposedNamespaces() => Guard(() =>
    {
        using var connection = OpenConnection(write: false);
        return ReadScopes(connection, null);
    });

    public Task ExposeAsync(string ns, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        CortexAssetCodecV2.RequireName(ns, 63);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection(write: true);
        using var transaction = connection.BeginTransaction(deferred: false);
        var scopes = ReadScopes(connection, transaction);
        if (!scopes.Contains(ns, StringComparer.Ordinal))
        {
            if (scopes.Count >= 32) throw new CortexAssetException("asset_scope_capacity");
            Execute(connection, transaction, "INSERT INTO asset_scopes VALUES($ns);", ("$ns", ns));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    });

    public bool RecordIssue(string ns, string id, string reference, string code)
    {
        try
        {
            ThrowIfDisposed();
            CortexAssetCodecV2.RequireName(ns, 63); CortexAssetCodecV2.RequireName(id, 127);
            CortexAssetCodecV2.RequireHash(reference);
            if (code is not ("asset_file_unavailable" or "asset_integrity_failed" or
                "asset_metadata_integrity_failed" or "asset_reference_unavailable")) return false;
            // ADO SQLite retries have second granularity; this is explicitly a bounded one-second
            // diagnostic budget, rather than claiming that busy_timeout=0 disables those retries.
            using var connection = OpenConnection(write: true, busyTimeout: 1_000);
            using var transaction = connection.BeginTransaction(deferred: false);
            Execute(connection, transaction, """
                INSERT INTO asset_health(ns,id,ref,code,at_utc) SELECT $ns,$id,$ref,$code,$now
                WHERE NOT EXISTS(SELECT 1 FROM asset_health WHERE ns=$ns AND id=$id AND ref=$ref AND code=$code);
                DELETE FROM asset_health WHERE seq NOT IN(SELECT seq FROM asset_health ORDER BY seq DESC LIMIT 128);
                """, ("$ns", ns), ("$id", id), ("$ref", reference), ("$code", code),
                ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            transaction.Commit();
            return true;
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException or
            InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return false; }
    }

    private CortexAssetV2 CommitReference(SqliteConnection connection, SqliteTransaction transaction,
        CortexAssetSpecV2 metadata, Prepared prepared, string? expectedRef)
    {
        var canonical = CortexAssetCodecV2.Serialize(metadata, prepared.Sha, prepared.Size, prepared.IndexSha);
        var reference = CortexAssetCodecV2.Reference(canonical);
        var oldRef = RequireExpectedHead(connection, transaction, metadata, expectedRef);
        if (oldRef is not null && oldRef != reference && expectedRef is null)
            throw new CortexAssetException("asset_revision_conflict");
        var previous = BoundedScalarText(connection, transaction,
            "SELECT canonical,typeof(canonical),length(CAST(canonical AS BLOB)) FROM asset_refs WHERE ref=$ref;",
            CortexAssetCodecV2.MaximumMetadataBytes, ("$ref", reference));
        if (previous is null)
            Execute(connection, transaction, "INSERT INTO asset_refs VALUES($ref,$canonical,$ns,$id,$sha);",
                ("$ref", reference), ("$canonical", canonical), ("$ns", metadata.Namespace), ("$id", metadata.Id), ("$sha", prepared.Sha));
        else
        {
            var existing = LoadReference(connection, transaction, reference);
            if (previous != canonical || existing.Metadata != metadata || existing.Sha256 != prepared.Sha)
                throw new CortexAssetException("asset_metadata_integrity_failed");
        }
        if (oldRef != reference)
        {
            Execute(connection, transaction, """
                INSERT INTO asset_namespaces VALUES($ns,$generation)
                ON CONFLICT(ns) DO UPDATE SET generation=excluded.generation;
                INSERT INTO asset_heads VALUES($ns,$id,$key,$ref)
                ON CONFLICT(ns,id) DO UPDATE SET ref=excluded.ref,sort_key=excluded.sort_key;
                """, ("$ns", metadata.Namespace), ("$generation", Guid.NewGuid().ToString("N")),
                ("$id", metadata.Id), ("$key", SortKey(metadata.Id)), ("$ref", reference));
        }
        return new CortexAssetV2(metadata, reference, prepared.Sha, prepared.Size, prepared.IndexSha);
    }

    private string? RequireExpectedHead(SqliteConnection connection, SqliteTransaction transaction,
        CortexAssetSpecV2 metadata, string? expectedRef)
    {
        var old = CurrentReference(connection, transaction, metadata.Namespace, metadata.Id);
        if (old is not null)
        {
            var current = LoadReference(connection, transaction, old);
            if (current.Metadata.Namespace != metadata.Namespace || current.Metadata.Id != metadata.Id)
                throw new CortexAssetException("asset_scope_mismatch");
        }
        if (expectedRef is not null && old != expectedRef) throw new CortexAssetException("asset_revision_conflict");
        return old;
    }

    private static string? CurrentReference(SqliteConnection connection, SqliteTransaction transaction, string ns, string id)
    {
        using var command = Command(connection, transaction, """
                SELECT ref,sort_key,typeof(ref),length(CAST(ref AS BLOB)),typeof(sort_key),length(CAST(sort_key AS BLOB))
                FROM asset_heads WHERE ns=$ns AND id=$id;
                """,
            ("$ns", ns), ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var reference = RequiredText(reader, 0, 2, 3, 64); CortexAssetCodecV2.RequireHash(reference);
        if (RequiredText(reader, 1, 4, 5, 66) != SortKey(id)) throw new CortexAssetException("asset_metadata_integrity_failed");
        return reference;
    }

    private CortexAssetV2 LoadReference(SqliteConnection connection, SqliteTransaction? transaction, string reference)
    {
        CortexAssetCodecV2.RequireHash(reference);
        string canonical, ns, id, sha;
        using (var command = Command(connection, transaction, """
                   SELECT canonical,ns,id,sha,typeof(canonical),length(CAST(canonical AS BLOB)),
                       typeof(ns),length(CAST(ns AS BLOB)),typeof(id),length(CAST(id AS BLOB)),
                       typeof(sha),length(CAST(sha AS BLOB))
                   FROM asset_refs WHERE ref=$ref;
                   """, ("$ref", reference)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new CortexAssetException("asset_reference_unavailable");
            canonical = RequiredText(reader, 0, 4, 5, CortexAssetCodecV2.MaximumMetadataBytes, "asset_metadata_oversized");
            ns = RequiredText(reader, 1, 6, 7, 63, "asset_metadata_integrity_failed");
            id = RequiredText(reader, 2, 8, 9, 127, "asset_metadata_integrity_failed");
            sha = RequiredText(reader, 3, 10, 11, 64, "asset_metadata_integrity_failed");
        }
        var asset = CortexAssetCodecV2.Parse(reference, canonical);
        if (asset.Metadata.Namespace != ns || asset.Metadata.Id != id || asset.Sha256 != sha)
            throw new CortexAssetException("asset_metadata_integrity_failed");
        var stored = TryLoadObject(connection, transaction, sha, asset.Metadata)
            ?? throw new CortexAssetException("asset_integrity_failed");
        if (asset.Size != stored.Prepared.Size || asset.ChunksSha256 != stored.Prepared.IndexSha)
            throw new CortexAssetException("asset_integrity_failed");
        return asset;
    }

    private StoredObject? TryLoadObject(SqliteConnection connection, SqliteTransaction? transaction,
        string sha, CortexAssetSpecV2 metadata)
    {
        CortexAssetCodecV2.RequireHash(sha);
        long size; string indexSha; int tier; string? fileName;
        using (var command = Command(connection, transaction, """
                   SELECT size,index_sha,tier,file_name,typeof(index_sha),length(CAST(index_sha AS BLOB)),
                       typeof(file_name),length(CAST(file_name AS BLOB)),typeof(size),typeof(tier)
                   FROM asset_objects WHERE sha=$sha;
                   """, ("$sha", sha)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetString(8) != "integer" || reader.GetString(9) != "integer")
                throw new CortexAssetException("asset_integrity_failed");
            size = reader.GetInt64(0); indexSha = RequiredText(reader, 1, 4, 5, 64); tier = reader.GetInt32(2);
            fileName = OptionalText(reader, 3, 6, 7, 64);
        }
        CortexAssetCodecV2.RequireHash(indexSha);
        if (size < 0 || size > MaximumAssetBytes || tier != (size <= _options.InlineCutoffBytes ? 0 : 1) ||
            (tier == 0 ? fileName is not null : fileName != sha))
            throw new CortexAssetException("asset_integrity_failed");
        if (tier == 1) _ = PayloadPath(sha);
        var count = checked((int)((size + ChunkSize - 1) / ChunkSize));
        var hashes = new string[count];
        using (var command = Command(connection, transaction,
                   """
                   SELECT ordinal,hash,length(payload),typeof(hash),length(CAST(hash AS BLOB)),typeof(payload),typeof(ordinal)
                   FROM asset_chunks WHERE sha=$sha ORDER BY ordinal LIMIT 32769;
                   """, ("$sha", sha)))
        using (var reader = command.ExecuteReader())
        {
            var ordinal = 0;
            while (reader.Read())
            {
                if (ordinal >= count || reader.GetString(6) != "integer" || reader.GetInt64(0) != ordinal)
                    throw new CortexAssetException("asset_integrity_failed");
                var hash = RequiredText(reader, 1, 3, 4, 64); CortexAssetCodecV2.RequireHash(hash);
                var expectedLength = (int)Math.Min(ChunkSize, size - (long)ordinal * ChunkSize);
                if (tier == 0 ? reader.GetString(5) != "blob" || reader.IsDBNull(2) || reader.GetInt64(2) != expectedLength
                    : reader.GetString(5) != "null" || !reader.IsDBNull(2))
                    throw new CortexAssetException("asset_integrity_failed");
                hashes[ordinal++] = hash;
            }
            if (ordinal != count) throw new CortexAssetException("asset_integrity_failed");
        }
        var index = IndexBytes(hashes);
        CortexAssetCodecV2.ValidateChunkIndex(new(metadata, new string('0', 64), sha, size, indexSha), index);
        return new StoredObject(new Prepared(sha, size, indexSha, hashes), tier);
    }

    private static void InsertObject(SqliteConnection connection, SqliteTransaction transaction, Prepared prepared, int tier) =>
        Execute(connection, transaction, "INSERT INTO asset_objects VALUES($sha,$size,$index,$tier,$file);",
            ("$sha", prepared.Sha), ("$size", prepared.Size), ("$index", prepared.IndexSha),
            ("$tier", tier), ("$file", tier == 1 ? prepared.Sha : null));

    private async Task<Prepared> CopyAndHashAsync(Stream input, long expectedSize, Stream? output,
        Func<int, string, byte[], ValueTask>? chunkSink, CancellationToken token)
    {
        using var contentDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hashes = new List<string>();
        var buffer = new byte[ChunkSize];
        long size = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == 0) break;
            size = checked(size + count);
            if (size > expectedSize || size > MaximumAssetBytes) throw new CortexAssetException("asset_source_changed");
            contentDigest.AppendData(buffer, 0, count);
            var hash = Hex(SHA256.HashData(buffer.AsSpan(0, count)));
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (chunkSink is not null)
                await chunkSink(hashes.Count, hash, buffer.AsSpan(0, count).ToArray()).ConfigureAwait(false);
            hashes.Add(hash);
        }
        if (size != expectedSize) throw new CortexAssetException("asset_source_changed");
        var array = hashes.ToArray();
        return new Prepared(Hex(contentDigest.GetHashAndReset()), size, Hex(SHA256.HashData(IndexBytes(array))), array);
    }

    private async Task VerifyWholeObjectAsync(SqliteConnection connection, SqliteTransaction transaction,
        StoredObject stored, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var external = stored.Tier == 1 ? OpenPayload(stored.Prepared.Sha) : null;
        if (external is not null && external.Length != stored.Prepared.Size)
            throw new CortexAssetException("asset_integrity_failed");
        for (var ordinal = 0; ordinal < stored.Prepared.Hashes.Length; ordinal++)
        {
            var bytes = await ReadChunkAsync(connection, transaction, stored, ordinal, external, token).ConfigureAwait(false);
            digest.AppendData(bytes);
        }
        if (Hex(digest.GetHashAndReset()) != stored.Prepared.Sha)
            throw new CortexAssetException("asset_integrity_failed");
    }

    private static async Task VerifyExternalAsync(string path, Prepared prepared, CancellationToken token)
    {
        CheckPath(path); RequirePrivateEntry(path, directory: false);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != prepared.Size) throw new CortexAssetException("asset_integrity_failed");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        for (var ordinal = 0; ordinal < prepared.Hashes.Length; ordinal++)
        {
            var count = (int)Math.Min(ChunkSize, prepared.Size - (long)ordinal * ChunkSize);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            CortexAssetCodecV2.VerifyChunk(buffer.AsSpan(0, count), prepared.Hashes[ordinal]);
            digest.AppendData(buffer, 0, count);
        }
        if (Hex(digest.GetHashAndReset()) != prepared.Sha) throw new CortexAssetException("asset_integrity_failed");
    }

    private async Task<byte[]> ReadChunkAsync(SqliteConnection connection, SqliteTransaction transaction,
        StoredObject stored, int ordinal, FileStream? external, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ordinal < 0 || ordinal >= stored.Prepared.Hashes.Length) throw new CortexAssetException("asset_integrity_failed");
        var expectedLength = (int)Math.Min(ChunkSize, stored.Prepared.Size - (long)ordinal * ChunkSize);
        byte[] bytes;
        if (stored.Tier == 1)
        {
            if (external is null) throw new CortexAssetException("asset_file_unavailable");
            external.Position = (long)ordinal * ChunkSize;
            bytes = new byte[expectedLength];
            await external.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        else
        {
            using var command = Command(connection, transaction, "SELECT payload FROM asset_chunks WHERE sha=$sha AND ordinal=$n;",
                ("$sha", stored.Prepared.Sha), ("$n", ordinal));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.GetBytes(0, 0, null, 0, 0) != expectedLength)
                throw new CortexAssetException("asset_integrity_failed");
            bytes = new byte[expectedLength];
            if (reader.GetBytes(0, 0, bytes, 0, bytes.Length) != expectedLength)
                throw new CortexAssetException("asset_integrity_failed");
        }
        CortexAssetCodecV2.VerifyChunk(bytes, stored.Prepared.Hashes[ordinal]);
        return bytes;
    }

    private static void RequireSameObject(StoredObject existing, Prepared prepared, int tier)
    {
        if (existing.Tier != tier || existing.Prepared.Size != prepared.Size ||
            existing.Prepared.IndexSha != prepared.IndexSha || !existing.Prepared.Hashes.SequenceEqual(prepared.Hashes))
            throw new CortexAssetException("asset_integrity_failed");
    }

    private static void ValidateImport(CortexAssetSpecV2 metadata, string? expectedRef, string? expectedContentSha256)
    {
        CortexAssetCodecV2.Validate(metadata);
        if (expectedRef is not null) CortexAssetCodecV2.RequireHash(expectedRef);
        if (expectedContentSha256 is not null) CortexAssetCodecV2.RequireHash(expectedContentSha256);
    }

    private static void RequireExpectedDigest(Prepared prepared, string? expected)
    {
        if (expected is not null && expected != prepared.Sha) throw new CortexAssetException("asset_integrity_failed");
    }

    private static byte[] IndexBytes(IEnumerable<string> hashes) => Utf8.GetBytes(string.Concat(hashes.Select(hash => hash + "\n")));
    private static string SortKey(string id) { var hash = Hex(SHA256.HashData(Utf8.GetBytes(id))); return hash[..2] + hash; }
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
    private sealed record Prepared(string Sha, long Size, string IndexSha, string[] Hashes);
    private sealed record StoredObject(Prepared Prepared, int Tier);

    private SqliteConnection OpenConnection(bool write, bool verify = true, int? busyTimeout = null, bool creating = false)
    {
        ThrowIfDisposed();
        ValidateStatePaths();
        if (!File.Exists(DatabasePath)) throw new CortexAssetException("asset_store_missing");
        if (!creating) RequireReadonlySafeHeader();
        // Refuse foreign/malformed stores through a read-only admission before an RW handle can
        // recover a journal or a write pragma can change the database. Recovery is an explicit lane.
        if (write && verify)
        {
            using var probe = OpenConnection(write: false, busyTimeout: busyTimeout);
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false,
            // A zero ADO command timeout means unbounded retry, even with a bounded SQLite busy pragma.
            DefaultTimeout = Math.Max(1, ((busyTimeout ?? _options.BusyTimeoutMilliseconds) + 999) / 1000)
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, $"PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout={busyTimeout ?? _options.BusyTimeoutMilliseconds};");
            if (verify) VerifyCatalog(connection);
            if (write)
            {
                // SQLite will refuse to lower max_page_count below the current size. Treat that as
                // a write admission failure, never as permission to ignore the requested budget.
                if (ScalarLong(connection, null, "PRAGMA page_count;") > _options.MaximumDatabasePages)
                    throw new CortexAssetException("asset_capacity_reached");
                Execute(connection, null, "PRAGMA synchronous=FULL;");
                var ceiling = ScalarLong(connection, null, $"PRAGMA max_page_count={_options.MaximumDatabasePages};");
                if (ceiling != _options.MaximumDatabasePages)
                    throw new CortexAssetException("asset_capacity_invalid");
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private string VerifyCatalog(SqliteConnection connection)
    {
        if (ScalarLong(connection, null, "SELECT count(*) FROM sqlite_schema WHERE substr(name,1,7)!='sqlite_';") != Definitions.Count)
            throw new CortexAssetException("asset_schema_invalid");
        foreach (var definition in Definitions)
        {
            using var command = Command(connection, null, """
                SELECT type,sql,typeof(type),length(CAST(type AS BLOB)),typeof(sql),length(CAST(sql AS BLOB))
                FROM sqlite_schema WHERE name=$name;
                """, ("$name", definition.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || RequiredText(reader, 0, 2, 3, 16, "asset_schema_invalid") != "table" ||
                NormalizeSchema(RequiredText(reader, 1, 4, 5, Utf8.GetByteCount(definition.Value) + 4, "asset_schema_invalid")) !=
                NormalizeSchema(definition.Value))
                throw new CortexAssetException("asset_schema_invalid");
        }
        using (var command = Command(connection, null, """
                   SELECT singleton,schema_version,generation,store_id,inline_cutoff,maximum_asset_bytes,large_root,
                       typeof(generation),length(CAST(generation AS BLOB)),typeof(store_id),length(CAST(store_id AS BLOB)),
                       typeof(large_root),length(CAST(large_root AS BLOB)),typeof(singleton),typeof(schema_version),
                       typeof(inline_cutoff),typeof(maximum_asset_bytes) FROM asset_meta LIMIT 2;
                   """))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(13) != "integer" || reader.GetString(14) != "integer" ||
                reader.GetString(15) != "integer" || reader.GetString(16) != "integer" ||
                reader.GetInt64(0) != 1 || reader.GetInt64(1) != SchemaVersion ||
                RequiredText(reader, 2, 7, 8, 64, "asset_schema_invalid") != StoreGeneration)
                throw new CortexAssetException("asset_schema_invalid");
            var id = RequiredText(reader, 3, 9, 10, 32);
            if (!Guid.TryParseExact(id, "N", out _) || _storeId.Length > 0 && id != _storeId)
                throw new CortexAssetException("asset_integrity_failed");
            var large = RequiredText(reader, 6, 11, 12, 4 * 240);
            if (reader.GetInt64(4) != _options.InlineCutoffBytes || reader.GetInt64(5) != MaximumAssetBytes ||
                large.Length > 240 || !string.Equals(large, _largeRoot, PathComparison))
                throw new CortexAssetException("asset_policy_mismatch");
            if (reader.Read()) throw new CortexAssetException("asset_schema_invalid");
            RequireStageEmpty(connection, null);
            // This PRAGMA returns a SQLite engine enum, not stored user text.
            using var journalCommand = Command(connection, null, "PRAGMA journal_mode;");
            var journal = journalCommand.ExecuteScalar() as string;
            if (!string.Equals(journal, "delete", StringComparison.OrdinalIgnoreCase))
                throw new CortexAssetException("asset_schema_invalid");
            return id;
        }
    }

    private static IReadOnlyList<string> ReadScopes(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT ns,typeof(ns),length(CAST(ns AS BLOB)) FROM asset_scopes ORDER BY ns COLLATE BINARY LIMIT 33;");
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var ns = RequiredText(reader, 0, 1, 2, 63, "asset_scope_invalid");
            CortexAssetCodecV2.RequireName(ns, 63); result.Add(ns);
        }
        if (result.Count is < 1 or > 32) throw new CortexAssetException("asset_scope_invalid");
        return result.AsReadOnly();
    }

    private static void RequireStageEmpty(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ScalarLong(connection, transaction, "SELECT EXISTS(SELECT 1 FROM asset_import_chunks LIMIT 1);") != 0)
            throw new CortexAssetException("asset_integrity_failed");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values) { using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery(); }
    private static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values) { using var command = Command(connection, transaction, sql, values); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static string? BoundedScalarText(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, int maximumBytes, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        using var reader = command.ExecuteReader();
        return reader.Read() ? RequiredText(reader, 0, 1, 2, maximumBytes) : null;
    }
    private static string RequiredText(SqliteDataReader reader, int value, int type, int byteLength,
        int maximumBytes, string code = "asset_integrity_failed")
    {
        // typeof() is an engine-produced short enum; the byte length is computed in SQLite before
        // GetString hydrates persisted data. Character length alone would miss embedded-NUL tails.
        if (reader.GetString(type) != "text" || reader.IsDBNull(byteLength) ||
            reader.GetInt64(byteLength) < 0 || reader.GetInt64(byteLength) > maximumBytes)
            throw new CortexAssetException(code);
        return reader.GetString(value);
    }
    private static string? OptionalText(SqliteDataReader reader, int value, int type, int byteLength, int maximumBytes)
    {
        if (reader.GetString(type) == "null" && reader.IsDBNull(value) && reader.IsDBNull(byteLength)) return null;
        return RequiredText(reader, value, type, byteLength, maximumBytes);
    }
    private static string NormalizeSchema(string value) => string.Join(' ', value.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["asset_meta"] = """
            CREATE TABLE asset_meta(singleton INTEGER PRIMARY KEY CHECK(singleton=1),schema_version INTEGER NOT NULL,
                generation TEXT NOT NULL,store_id TEXT NOT NULL,inline_cutoff INTEGER NOT NULL,
                maximum_asset_bytes INTEGER NOT NULL,large_root TEXT NOT NULL) STRICT;
            """,
        ["asset_objects"] = """
            CREATE TABLE asset_objects(sha TEXT PRIMARY KEY NOT NULL CHECK(length(sha)=64),size INTEGER NOT NULL CHECK(size>=0 AND size<=2147483648),
                index_sha TEXT NOT NULL CHECK(length(index_sha)=64),tier INTEGER NOT NULL CHECK(tier IN(0,1)),file_name TEXT,
                CHECK((tier=0 AND file_name IS NULL) OR (tier=1 AND file_name=sha))) STRICT;
            """,
        ["asset_chunks"] = """
            CREATE TABLE asset_chunks(sha TEXT NOT NULL REFERENCES asset_objects(sha),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<32768),
                hash TEXT NOT NULL CHECK(length(hash)=64),payload BLOB CHECK(payload IS NULL OR length(payload) BETWEEN 1 AND 65536),PRIMARY KEY(sha,ordinal)) STRICT;
            """,
        ["asset_refs"] = """
            CREATE TABLE asset_refs(ref TEXT PRIMARY KEY NOT NULL CHECK(length(ref)=64),canonical TEXT NOT NULL CHECK(length(CAST(canonical AS BLOB))<=32768),
                ns TEXT NOT NULL,id TEXT NOT NULL,sha TEXT NOT NULL REFERENCES asset_objects(sha)) STRICT;
            """,
        ["asset_heads"] = """
            CREATE TABLE asset_heads(ns TEXT NOT NULL,id TEXT NOT NULL,sort_key TEXT NOT NULL CHECK(length(sort_key)=66),
                ref TEXT NOT NULL REFERENCES asset_refs(ref),PRIMARY KEY(ns,id),UNIQUE(ns,sort_key)) STRICT;
            """,
        ["asset_namespaces"] = "CREATE TABLE asset_namespaces(ns TEXT PRIMARY KEY NOT NULL,generation TEXT NOT NULL) STRICT;",
        ["asset_scopes"] = "CREATE TABLE asset_scopes(ns TEXT PRIMARY KEY NOT NULL) STRICT;",
        ["asset_health"] = """
            CREATE TABLE asset_health(seq INTEGER PRIMARY KEY,ns TEXT NOT NULL,id TEXT NOT NULL,ref TEXT NOT NULL,code TEXT NOT NULL,
                at_utc TEXT NOT NULL,UNIQUE(ns,id,ref,code)) STRICT;
            """,
        ["asset_import_chunks"] = """
            CREATE TABLE asset_import_chunks(ordinal INTEGER PRIMARY KEY CHECK(ordinal>=0 AND ordinal<32768),hash TEXT NOT NULL CHECK(length(hash)=64),
                payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 65536)) STRICT;
            """
    };

    private static (string Database, string Large) ValidateLocations(string databasePath, string largePayloadRoot)
    {
        CheckPath(databasePath); CheckPath(largePayloadRoot);
        var database = Path.GetFullPath(databasePath); var large = Path.TrimEndingDirectorySeparator(Path.GetFullPath(largePayloadRoot));
        if (!string.Equals(database, databasePath, PathComparison) || !string.Equals(large, largePayloadRoot, PathComparison) ||
            string.Equals(Path.GetFileName(database), "cortex-v2.db", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(database), "cortex.db", StringComparison.OrdinalIgnoreCase))
            throw new CortexAssetException("asset_path_invalid");
        var parent = Path.GetDirectoryName(database);
        if (string.IsNullOrEmpty(parent) || !string.Equals(Path.GetDirectoryName(large), parent, PathComparison) ||
            string.Equals(database, large, PathComparison)) throw new CortexAssetException("asset_path_invalid");
        RequirePrivateEntry(parent, directory: true);
        if (Directory.Exists(database) || File.Exists(large)) throw new CortexAssetException("asset_path_invalid");
        if (Directory.Exists(large)) RequirePrivateEntry(large, directory: true);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            CheckPath(database + suffix);
            if (File.Exists(database + suffix)) RequirePrivateEntry(database + suffix, directory: false);
            if (Directory.Exists(database + suffix)) throw new CortexAssetException("asset_path_invalid");
        }
        CheckPath(Path.Combine(large, ".p-" + new string('0', 32)));
        CheckPath(Path.Combine(large, new string('0', 64)));
        return (database, large);
    }

    private void ValidateStatePaths() => _ = ValidateLocations(DatabasePath, _largeRoot);
    private void RequireReadonlySafeHeader()
    {
        if (File.Exists(DatabasePath + "-wal") || File.Exists(DatabasePath + "-shm"))
            throw new CortexAssetException("asset_schema_invalid");
        // Never let a read/open repair, adopt or remove a journal. An active writer can be retried;
        // a crash journal needs a separately selected recovery operation.
        if (File.Exists(DatabasePath + "-journal")) throw new CortexAssetException("asset_journal_present");
        using var input = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[100];
        if (input.Length < header.Length) throw new CortexAssetException("asset_schema_invalid");
        input.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8) || header[16] != 16 || header[17] != 0 ||
            header[18] != 1 || header[19] != 1) throw new CortexAssetException("asset_schema_invalid");
    }
    private void EnsureLargeRoot()
    {
        ValidateStatePaths();
        if (!Directory.Exists(_largeRoot)) Directory.CreateDirectory(_largeRoot);
        RequirePrivateEntry(_largeRoot, directory: true);
    }
    private string PayloadPath(string name)
    {
        var path = Path.Combine(_largeRoot, name); CheckPath(path);
        if (Directory.Exists(path)) throw new CortexAssetException("asset_path_invalid");
        if (File.Exists(path)) RequirePrivateEntry(path, directory: false);
        return path;
    }
    private FileStream OpenPayload(string sha)
    {
        var path = PayloadPath(sha);
        if (!File.Exists(path)) throw new CortexAssetException("asset_file_unavailable");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }
    private static void DeleteOwnedFile(string path)
    {
        try { CheckPath(path); if (File.Exists(path)) { RequirePrivateEntry(path, directory: false); File.Delete(path); } }
        catch { /* Exact task-owned cleanup only; never weaken a failed identity/ACL check. */ }
    }

    private static void CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.Length > 240 ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new CortexAssetException("asset_path_invalid");
        var root = Path.GetPathRoot(path)!;
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Utf8.GetByteCount(segment) > 255 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
                throw new CortexAssetException("asset_path_invalid");
        }
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new CortexAssetException("asset_reparse_path"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RequirePrivateEntry(string path, bool directory)
    {
        CheckPath(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new CortexAssetException("asset_store_unavailable");
        if (OperatingSystem.IsWindows()) RequireWindowsPrivateEntry(path, directory);
        else
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new CortexAssetException("asset_access_denied");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsPrivateEntry(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value ?? throw new CortexAssetException("asset_access_denied");
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        static bool SystemOwner(string sid) => sid is "S-1-5-18" or "S-1-5-32-544";
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            owner.Value != ownerSid && !SystemOwner(owner.Value)) throw new CortexAssetException("asset_access_denied");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (rule.AccessControlType == AccessControlType.Allow && sid != ownerSid && !SystemOwner(sid))
                throw new CortexAssetException("asset_access_denied");
        }
    }

    private T Guard<T>(Func<T> action) { ThrowIfDisposed(); return Normalize(action); }
    private async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        ThrowIfDisposed();
        try { return await action().ConfigureAwait(false); }
        catch (CortexAssetException) { throw; }
        catch (SqliteException error) { throw SqliteFailure(error); }
        catch (UnauthorizedAccessException) { throw new CortexAssetException("asset_access_denied"); }
        catch (DecoderFallbackException) { throw new CortexAssetException("asset_metadata_integrity_failed"); }
        catch (EncoderFallbackException) { throw new CortexAssetException("asset_metadata_invalid"); }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        { throw new CortexAssetException("asset_integrity_failed"); }
        catch (InvalidOperationException error) when (error is not ObjectDisposedException)
        { throw new CortexAssetException("asset_integrity_failed"); }
        catch (IOException) { throw new CortexAssetException("asset_file_unavailable"); }
    }
    private static T Normalize<T>(Func<T> action)
    {
        try { return action(); }
        catch (CortexAssetException) { throw; }
        catch (SqliteException error) { throw SqliteFailure(error); }
        catch (UnauthorizedAccessException) { throw new CortexAssetException("asset_access_denied"); }
        catch (DecoderFallbackException) { throw new CortexAssetException("asset_metadata_integrity_failed"); }
        catch (EncoderFallbackException) { throw new CortexAssetException("asset_metadata_invalid"); }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        { throw new CortexAssetException("asset_integrity_failed"); }
        catch (InvalidOperationException error) when (error is not ObjectDisposedException)
        { throw new CortexAssetException("asset_integrity_failed"); }
        catch (IOException) { throw new CortexAssetException("asset_file_unavailable"); }
    }
    private static CortexAssetException SqliteFailure(SqliteException error) => new(error.SqliteErrorCode switch
    {
        5 or 6 => "asset_busy",
        8 or 23 => "asset_access_denied",
        11 or 19 or 26 => "asset_integrity_failed",
        13 => "asset_capacity_reached",
        _ => "asset_store_unavailable"
    });
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CortexSqliteAssetStoreV1)); }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
