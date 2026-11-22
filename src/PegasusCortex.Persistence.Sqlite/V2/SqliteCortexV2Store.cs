using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>
/// Fresh-generation Cortex persistence. This type accepts only cortex-v2.db
/// and never probes, migrates, or modifies a v1 cortex.db.
/// </summary>
public sealed partial class SqliteCortexV2Store : IAsyncDisposable
{
    public const int SchemaVersion = 2;
    public const int TieredSchemaVersion = 3;
    private int _openedSchemaVersion = SchemaVersion;
    private readonly ISqliteCortexColdPayloadStore _coldPayloads;
    public int EffectiveSchemaVersion => Volatile.Read(ref _openedSchemaVersion);
    private const int MaximumIdentifierCharacters = 128;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumSummaryCharacters = 4_096;
    private const int MaximumTags = 64;
    private const int MaximumTagCharacters = 128;
    private const int MaximumHistoryPageSize = 256;
    // The public API still pages at 32. The provider may materialize up to
    // eight public pages so stateless preview cursors do not silently lose
    // keyword matches after the first page.
    private const int MaximumSearchLimit = 256;
    private const int MaximumSearchQueryCharacters = 2_048;
    private const string Generation = "pegasus-cortex-sqlite-v2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SqliteCortexV2StoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private volatile bool _initialized;
    private int _disposed;

    public SqliteCortexV2Store(
        SqliteCortexV2StoreOptions options,
        TimeProvider? timeProvider = null,
        ISqliteCortexColdPayloadStore? coldPayloadStore = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _coldPayloads = coldPayloadStore ?? new SqliteCortexV2ColdPayloadFiles(_options);
    }

    public string DatabasePath => _options.DatabasePath;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(requireExisting: false, cancellationToken);

    private async ValueTask InitializeAsync(
        bool requireExisting,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_initialized)
            return;

        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var existed = File.Exists(_options.DatabasePath);
            if (requireExisting && !existed)
            {
                throw new FileNotFoundException(
                    "The central Cortex v2 store is uninitialized; no backup was created.",
                    _options.DatabasePath);
            }
            PrepareDirectory(existed);
            await using var connection = CreateConnection(createIfMissing: !existed);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

            if (existed)
            {
                if (!await TableExistsAsync(connection, "cortex_v2_meta", cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The existing cortex-v2.db is not a Cortex v2 store; automatic migration is prohibited.");
                }
                await VerifyGenerationAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
                await VerifyRequiredObjectsAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
                await RequireWalAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ConfigureNewDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);

                // A racing creator may have completed while this process waited
                // for the SQLite writer lock. Accept only that exact generation.
                if (!await TableExistsAsync(connection, "cortex_v2_meta", transaction, cancellationToken).ConfigureAwait(false))
                {
                    if (await CountUserSchemaObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("The new Cortex v2 path acquired unexpected pre-existing schema objects.");
                    await CreateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }

                await VerifyGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await VerifyRequiredObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await RequireWalAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    /// <summary>
    /// Performs a bounded startup probe. It deliberately reads no namespace,
    /// blob, revision, head, search, or FTS row.
    /// </summary>
    public async ValueTask<SqliteCortexV2QuickCheckResult> QuickCheckAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        var probes = 0;
        var (version, generation) = await ReadGenerationAsync(
            connection, transaction: null, cancellationToken).ConfigureAwait(false);
        probes++;
        var journal = await ReadScalarStringAsync(
            connection, transaction: null, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
        probes++;
        var foreignKeys = await ReadScalarInt64Async(
            connection, transaction: null, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false);
        probes++;
        var requiredObjects = await CountRequiredObjectsAsync(
            connection, transaction: null, cancellationToken).ConfigureAwait(false);
        probes++;

        var healthy = version is SchemaVersion or TieredSchemaVersion &&
            string.Equals(generation, Generation, StringComparison.Ordinal) &&
            string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase) &&
            foreignKeys == 1 &&
            requiredObjects == RequiredSchemaObjects.Length;
        return new(
            healthy,
            healthy ? "ready" : "quick-check-failed",
            version,
            journal,
            probes,
            CorpusRowsRead: 0);
    }

    /// <summary>
    /// Reads only SQLite allocation pragmas and filesystem lengths. It does
    /// not scan namespaces, records, blobs, search chunks, or FTS rows.
    /// </summary>
    public async ValueTask<SqliteCortexV2Capacity> CapacityAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var pageSize = await ReadScalarInt64Async(
            connection, null, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        var pageCount = await ReadScalarInt64Async(
            connection, null, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        var freelist = await ReadScalarInt64Async(
            connection, null, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        var maxPages = await ReadScalarInt64Async(
            connection, null, "PRAGMA max_page_count;", cancellationToken).ConfigureAwait(false);
        if (pageSize < 1 || pageCount < 0 || freelist < 0 || freelist > pageCount || maxPages < 1)
            throw new InvalidDataException("SQLite returned invalid capacity pragmas.");
        var mainBytes = System.IO.File.Exists(_options.DatabasePath)
            ? new FileInfo(_options.DatabasePath).Length
            : 0;
        var walPath = _options.DatabasePath + "-wal";
        var walBytes = System.IO.File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        var usedBytes = checked((pageCount - freelist) * pageSize);
        var maximumBytes = checked(maxPages * pageSize);
        var utilization = maximumBytes == 0
            ? 0
            : Math.Round(100d * usedBytes / maximumBytes, 4, MidpointRounding.AwayFromZero);
        var state = SqliteCortexV2CapacityPolicy.Classify(mainBytes, maximumBytes);
        return new(
            mainBytes,
            walBytes,
            pageSize,
            pageCount,
            freelist,
            maxPages,
            usedBytes,
            utilization,
            state,
            SqliteCortexV2CapacityPolicy.Recommendation(state));
    }

    public async ValueTask<SqliteCortexV2WriteResult> AppendAsync(
        SqliteCortexV2AppendRequest request,
        CancellationToken cancellationToken = default)
    {
        var batch = await AppendBatchAsync([request], cancellationToken).ConfigureAwait(false);
        if (batch.Items.Count != 1)
            throw new InvalidDataException("A singular Cortex v2 append returned an ambiguous batch result.");
        return batch.Items[0];
    }

    /// <summary>
    /// Atomically applies a task transition and any knowledge revisions emitted
    /// by one finish operation. Every idempotency and expected-revision check is
    /// evaluated inside the same immediate transaction. Any conflict or stale
    /// precondition rolls the entire batch back.
    /// </summary>
    public async ValueTask<SqliteCortexV2BatchWriteResult> AppendBatchAsync(
        IReadOnlyList<SqliteCortexV2AppendRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(requests));
        var validated = requests.Select(ValidateAppend).ToArray();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);

            var results = new List<SqliteCortexV2WriteResult>(validated.Length);
            foreach (var item in validated)
            {
                var result = await AppendInTransactionAsync(
                    connection, transaction, item, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (result.Disposition is SqliteCortexV2WriteDispositions.Conflict or
                    SqliteCortexV2WriteDispositions.StaleRevision)
                {
                    return new(false, result.Disposition, [result]);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var disposition = results.All(result =>
                result.Disposition == SqliteCortexV2WriteDispositions.Idempotent)
                ? SqliteCortexV2WriteDispositions.Idempotent
                : SqliteCortexV2WriteDispositions.Appended;
            return new(true, disposition, results.AsReadOnly());
        }
        finally
        {
            _writer.Release();
        }
    }

    private async ValueTask<SqliteCortexV2WriteResult> AppendInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCortexV2AppendRequest validated,
        CancellationToken cancellationToken)
    {

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var namespaceToken = NamespaceToken(validated.Namespace);
            await EnsureNamespaceAsync(connection, transaction, validated.Namespace, namespaceToken, now, cancellationToken)
                .ConfigureAwait(false);

            var baseRevision = validated.ExpectedRevision == 0
                ? null
                : await ReadRevisionMetadataAsync(
                    connection,
                    transaction,
                    validated.Namespace,
                    validated.Kind,
                    validated.RecordId,
                    validated.ExpectedRevision,
                    cancellationToken).ConfigureAwait(false);
            var suppliedContent = validated.ContentUtf8?.ToArray();
            var effectiveContentHash = suppliedContent is null
                ? baseRevision?.ContentHash
                : Sha256(suppliedContent);
            var requestHash = RequestHash(validated, effectiveContentHash ?? "missing-base");

            var replay = await FindIdempotencyAsync(
                connection,
                transaction,
                validated.Namespace,
                validated.Actor,
                validated.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                // Retained identity is never reinterpreted as a new request if its
                // exact payload is unavailable. Replay uses the same verified reader.
                if (string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                    _ = await ReadPayloadAsync(connection, transaction, replay.ContentHash, cancellationToken).ConfigureAwait(false);
                return string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
                    ? new(
                        SqliteCortexV2WriteDispositions.Idempotent,
                        replay.Namespace,
                        replay.Kind,
                        replay.RecordId,
                        replay.Revision,
                        replay.Revision,
                        replay.RevisionHash,
                        replay.ContentHash)
                    : new(
                        SqliteCortexV2WriteDispositions.Conflict,
                        validated.Namespace,
                        validated.Kind,
                        validated.RecordId,
                        null,
                        null,
                        null,
                        null);
            }

            var head = await ReadHeadMetadataAsync(
                connection,
                transaction,
                validated.Namespace,
                validated.Kind,
                validated.RecordId,
                cancellationToken).ConfigureAwait(false);
            var actualRevision = head?.Revision ?? 0;
            if (actualRevision != validated.ExpectedRevision ||
                validated.ExpectedRevision > 0 && baseRevision is null)
            {
                return new(
                    SqliteCortexV2WriteDispositions.StaleRevision,
                    validated.Namespace,
                    validated.Kind,
                    validated.RecordId,
                    null,
                    actualRevision,
                    head?.RevisionHash,
                    head?.ContentHash);
            }

            if (validated.ExpectedRevision == 0 && suppliedContent is null)
                throw new ArgumentException("A new Cortex v2 record requires exact UTF-8 content.", nameof(validated));
            if (effectiveContentHash is null)
                throw new InvalidDataException("The expected base revision has no immutable content blob.");

            long contentLength;
            byte[] effectiveContent;
            if (suppliedContent is not null)
            {
                contentLength = suppliedContent.Length;
                await InsertBlobAsync(
                    connection, transaction, effectiveContentHash, suppliedContent, now, cancellationToken)
                    .ConfigureAwait(false);
                effectiveContent = suppliedContent;
            }
            else
            {
                var blob = await ReadBlobAsync(
                    connection, transaction, effectiveContentHash, cancellationToken).ConfigureAwait(false) ??
                    throw new InvalidDataException("The inherited Cortex v2 content blob is missing.");
                effectiveContent = blob.Content;
                contentLength = blob.Content.Length;
            }

            var newRevision = checked(validated.ExpectedRevision + 1);
            var tagsJson = SerializeTags(validated.Tags);
            var tagsSearch = string.Join('\n', validated.Tags);
            var revisionHash = RevisionHash(
                validated,
                newRevision,
                head?.RevisionHash,
                effectiveContentHash,
                tagsJson,
                now);

            await InsertRevisionAsync(
                connection,
                transaction,
                validated,
                newRevision,
                head?.RevisionHash,
                revisionHash,
                effectiveContentHash,
                tagsJson,
                tagsSearch,
                requestHash,
                now,
                cancellationToken).ConfigureAwait(false);
            await PromotePayloadAsync(connection, transaction, effectiveContentHash, cancellationToken).ConfigureAwait(false);
            await UpsertHeadAsync(
                connection,
                transaction,
                validated,
                newRevision,
                revisionHash,
                effectiveContentHash,
                cancellationToken).ConfigureAwait(false);

            var oldChunks = await CountRecordChunksAsync(
                connection, transaction, validated.Namespace, validated.Kind, validated.RecordId, cancellationToken)
                .ConfigureAwait(false);
            await DeleteSearchHeadAsync(
                connection, transaction, validated.Namespace, validated.Kind, validated.RecordId, cancellationToken)
                .ConfigureAwait(false);
            long newChunks = 0;
            if (validated.Searchable)
            {
                var body = StrictUtf8.GetString(effectiveContent);
                var chunks = SqliteCortexV2MarkdownChunker.Chunk(body, _options.MaximumSearchChunksPerHead);
                await InsertSearchHeadAsync(
                    connection,
                    transaction,
                    validated,
                    namespaceToken,
                    newRevision,
                    revisionHash,
                    tagsSearch,
                    chunks,
                    cancellationToken).ConfigureAwait(false);
                newChunks = chunks.Count;
            }

            var oldHeadBytes = head is null
                ? 0
                : await ReadBlobLengthAsync(connection, transaction, head.ContentHash, cancellationToken)
                    .ConfigureAwait(false);
            await UpdateNamespaceCountersAsync(
                connection,
                transaction,
                validated.Namespace,
                isNewHead: head is null,
                referencedBytes: contentLength,
                headByteDelta: contentLength - oldHeadBytes,
                hotChunkDelta: newChunks - oldChunks,
                now,
                cancellationToken).ConfigureAwait(false);

            return new(
                SqliteCortexV2WriteDispositions.Appended,
                validated.Namespace,
                validated.Kind,
                validated.RecordId,
                newRevision,
                newRevision,
                revisionHash,
                effectiveContentHash);
    }

    public async ValueTask<SqliteCortexV2Record?> ReadHeadAsync(
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(ns, kind, recordId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadRecordAsync(
            connection,
            transaction: null,
            ns,
            kind,
            recordId,
            revision: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SqliteCortexV2HeadProjection?> ReadHeadProjectionAsync(
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(ns, kind, recordId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.revision, r.title, r.summary, r.attributes_json, r.is_searchable
              FROM cortex_v2_record_head h
              JOIN cortex_v2_record_revision r
                ON r.namespace_name = h.namespace_name
               AND r.kind = h.kind
               AND r.record_id = h.record_id
               AND r.revision = h.revision
             WHERE h.namespace_name = $namespace
               AND h.kind = $kind
               AND h.record_id = $record;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var result = new SqliteCortexV2HeadProjection(
            ns,
            kind,
            recordId,
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) == 1);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("A Cortex v2 head projection is duplicated.");
        return result;
    }

    public async ValueTask<SqliteCortexV2Record?> ReadRevisionAsync(
        string ns,
        string kind,
        string recordId,
        long revision,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(ns, kind, recordId);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadRecordAsync(
            connection,
            transaction: null,
            ns,
            kind,
            recordId,
            revision,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates a bounded current-head page in one read transaction without a
    /// wildcard FTS query. This is the provider primitive for task lists and
    /// namespace-context startup.
    /// </summary>
    public async ValueTask<SqliteCortexV2HeadPage> ListHeadsAsync(
        string ns,
        string? kind,
        string? afterRecordId,
        int limit,
        CancellationToken cancellationToken = default,
        bool searchableOnly = false)
    {
        RequireIdentifier(ns, nameof(ns));
        if (kind is not null)
            RequireIdentifier(kind, nameof(kind));
        if (afterRecordId is not null)
            RequireIdentifier(afterRecordId, nameof(afterRecordId));
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.kind, r.record_id, r.revision, r.revision_hash,
                   r.previous_revision_hash, r.content_hash, r.title, r.tags_json,
                   r.summary, r.attributes_json, r.event_type, r.actor,
                   r.occurred_at_utc, r.is_searchable,
                   b.original_length, b.compressed_content
              FROM cortex_v2_record_head h
              JOIN cortex_v2_record_revision r
                ON r.namespace_name = h.namespace_name
               AND r.kind = h.kind
               AND r.record_id = h.record_id
               AND r.revision = h.revision
              JOIN cortex_v2_content_blob b ON b.content_hash = r.content_hash
             WHERE h.namespace_name = $namespace
               AND ($kind IS NULL OR h.kind = $kind)
               AND ($searchableOnly = 0 OR r.is_searchable = 1)
               AND ($after IS NULL OR h.record_id > $after)
             ORDER BY h.record_id, h.kind
             LIMIT $limitPlusOne;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : kind);
        command.Parameters.AddWithValue("$searchableOnly", searchableOnly ? 1 : 0);
        command.Parameters.AddWithValue("$after", afterRecordId is null ? DBNull.Value : afterRecordId);
        command.Parameters.AddWithValue("$limitPlusOne", limit + 1);
        var items = new List<SqliteCortexV2Record>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var contentHash = reader.GetString(5);
            var content = await ReadPayloadAsync(connection, transaction, contentHash, cancellationToken,
                maximumBytes: _options.MaximumContentBytes).ConfigureAwait(false);
            if (!string.Equals(contentHash, Sha256(content), StringComparison.Ordinal))
                throw new InvalidDataException("A Cortex v2 listed head failed its immutable content hash.");
            items.Add(new(
                ns, reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                contentHash, reader.GetString(6), DeserializeTags(reader.GetString(7)),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), ParseUtc(reader.GetString(12)),
                reader.GetInt64(13) == 1, content));
        }
        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ns, items.AsReadOnly(), hasMore ? items[^1].RecordId : null);
    }

    public async ValueTask<SqliteCortexV2HistoryPage> ReadHistoryAsync(
        string ns,
        string kind,
        string recordId,
        long afterRevision = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(ns, kind, recordId);
        if (afterRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(afterRevision));
        if (limit is < 1 or > MaximumHistoryPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision, revision_hash, previous_revision_hash, content_hash,
                   event_type, actor, occurred_at_utc, is_searchable, title, summary,
                   attributes_json
              FROM cortex_v2_record_revision
             WHERE namespace_name = $namespace
               AND kind = $kind
               AND record_id = $record
               AND revision > $after
             ORDER BY revision
             LIMIT $limitPlusOne;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        command.Parameters.AddWithValue("$after", afterRevision);
        command.Parameters.AddWithValue("$limitPlusOne", limit + 1);
        var items = new List<SqliteCortexV2HistoryEvent>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseUtc(reader.GetString(6)),
                reader.GetInt64(7) == 1,
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }
        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        return new(ns, kind, recordId, items.AsReadOnly(), hasMore ? items[^1].Revision : null);
    }

    public async ValueTask<IReadOnlyList<SqliteCortexV2SearchHit>> SearchHeadsAsync(
        SqliteCortexV2SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateSearch(request);
        var terms = SqliteCortexV2MarkdownChunker.QueryTerms(validated.Query);
        if (terms.Count == 0)
            return [];
        if (validated.MinimumMatch > terms.Count)
            return [];

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.kind, s.record_id, s.revision, s.revision_hash,
                   s.title, s.tags, s.summary, s.body, r.attributes_json,
                   bm25(cortex_v2_search_fts, 0.0, 0.0, 10.0, 6.0, 3.0, 1.0) AS rank
              FROM cortex_v2_search_fts f
              JOIN cortex_v2_search_head s ON s.search_row_id = f.rowid
              JOIN cortex_v2_record_revision r
                ON r.namespace_name = s.namespace_name
               AND r.kind = s.kind
               AND r.record_id = s.record_id
               AND r.revision = s.revision
             WHERE cortex_v2_search_fts MATCH $match
               AND s.namespace_name = $namespace
               AND ($kind IS NULL OR s.kind = $kind)
             ORDER BY rank, s.kind, s.record_id, s.chunk_ordinal
             LIMIT $candidateLimit;
            """;
        command.Parameters.AddWithValue("$match", BuildFtsQuery(validated.Namespace, validated.Kind, terms));
        command.Parameters.AddWithValue("$namespace", validated.Namespace);
        command.Parameters.AddWithValue("$kind", validated.Kind is null ? DBNull.Value : validated.Kind);
        command.Parameters.AddWithValue("$candidateLimit", Math.Min(1_024, Math.Max(64, validated.Limit * 16)));

        var candidates = new Dictionary<(string Kind, string RecordId), SearchAccumulator>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (Kind: reader.GetString(0), RecordId: reader.GetString(1));
            var title = reader.GetString(4);
            var tags = reader.GetString(5);
            var summary = reader.GetString(6);
            var body = reader.GetString(7);
            var attributesJson = reader.GetString(8);
            var rank = reader.GetDouble(9);
            if (!candidates.TryGetValue(key, out var value))
            {
                value = new(
                    key.Kind,
                    key.RecordId,
                    reader.GetInt64(2),
                    reader.GetString(3),
                    title,
                    tags,
                    summary,
                    attributesJson,
                    body,
                    rank,
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(
                        SqliteCortexV2MarkdownChunker.MatchingTerms(terms, title, tags),
                        StringComparer.Ordinal));
                candidates.Add(key, value);
            }
            if (rank < value.Rank)
            {
                value.Rank = rank;
                value.Excerpt = body;
            }
            foreach (var term in SqliteCortexV2MarkdownChunker.MatchingTerms(
                         terms, title, tags, summary, body))
            {
                value.MatchedTerms.Add(term);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return candidates.Values
            .Where(value => value.MatchedTerms.Count >= validated.MinimumMatch)
            .OrderByDescending(value => ExactTitleTagWeight(value.TitleTagTerms, terms))
            .ThenByDescending(value => value.MatchedTerms.Count)
            .ThenBy(value => value.Rank)
            .ThenBy(value => value.Kind, StringComparer.Ordinal)
            .ThenBy(value => value.RecordId, StringComparer.Ordinal)
            .Take(validated.Limit)
            .Select(value => new SqliteCortexV2SearchHit(
                validated.Namespace,
                value.Kind,
                value.RecordId,
                value.Revision,
                value.RevisionHash,
                value.Title,
                Array.AsReadOnly(
                    value.Tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                value.Summary,
                value.AttributesJson,
                value.Excerpt,
                value.MatchedTerms.Count,
                ExactTitleTagWeight(value.TitleTagTerms, terms) * 1_000d +
                value.MatchedTerms.Count * 10d - value.Rank))
            .ToArray();
    }

    private static int ExactTitleTagWeight(
        IReadOnlySet<string> matches,
        IReadOnlyList<string> orderedTerms)
    {
        var weight = 0;
        for (var index = 0; index < orderedTerms.Count; index++)
        {
            if (matches.Contains(orderedTerms[index]))
                weight += orderedTerms.Count - index;
        }
        return weight;
    }

    public async ValueTask<IReadOnlyList<SqliteCortexV2Namespace>> ListNamespacesAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace_name, revision_count, head_count,
                   referenced_content_bytes, head_content_bytes, hot_chunk_count,
                   created_at_utc, updated_at_utc
              FROM cortex_v2_namespace
             ORDER BY namespace_name;
            """;
        var values = new List<SqliteCortexV2Namespace>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                ParseUtc(reader.GetString(6)),
                ParseUtc(reader.GetString(7))));
        }
        return values.AsReadOnly();
    }

    /// <summary>
    /// Reads one bounded ordinal namespace page without scanning or materializing
    /// the catalog beyond the requested page.
    /// </summary>
    public async ValueTask<SqliteCortexV2NamespacePage> ListNamespacesAsync(
        string? afterNamespace,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterNamespace is not null)
            RequireIdentifier(afterNamespace, nameof(afterNamespace));
        if (limit is < 1 or > MaximumSearchLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace_name, revision_count, head_count,
                   referenced_content_bytes, head_content_bytes, hot_chunk_count,
                   created_at_utc, updated_at_utc
              FROM cortex_v2_namespace
             WHERE ($after IS NULL OR namespace_name > $after)
             ORDER BY namespace_name
             LIMIT $limitPlusOne;
            """;
        command.Parameters.AddWithValue("$after", afterNamespace is null ? DBNull.Value : afterNamespace);
        command.Parameters.AddWithValue("$limitPlusOne", limit + 1);
        var values = new List<SqliteCortexV2Namespace>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                ParseUtc(reader.GetString(6)), ParseUtc(reader.GetString(7))));
        }
        var hasMore = values.Count > limit;
        if (hasMore)
            values.RemoveAt(values.Count - 1);
        return new(values.AsReadOnly(), hasMore ? values[^1].Name : null);
    }

    public async ValueTask<SqliteCortexV2Namespace?> ReadNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(ns, nameof(ns));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace_name, revision_count, head_count,
                   referenced_content_bytes, head_content_bytes, hot_chunk_count,
                   created_at_utc, updated_at_utc
              FROM cortex_v2_namespace
             WHERE namespace_name = $namespace;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                ParseUtc(reader.GetString(6)), ParseUtc(reader.GetString(7)))
            : null;
    }

    /// <summary>
    /// Reads aggregate operational counts from the compact namespace catalog,
    /// never from revision, content, head, or FTS corpus tables.
    /// </summary>
    public async ValueTask<SqliteCortexV2CatalogCounters> GetCatalogCountersAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(revision_count), 0), COALESCE(SUM(head_count), 0)
              FROM cortex_v2_namespace;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Cortex v2 namespace catalog counters are unavailable.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async ValueTask<SqliteCortexV2StorageMetrics> GetStorageMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var (version, _) = await ReadGenerationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM cortex_v2_namespace),
                (SELECT COUNT(*) FROM cortex_v2_record_revision),
                (SELECT COUNT(*) FROM cortex_v2_content_blob),
                (SELECT COUNT(*) FROM cortex_v2_record_head),
                (SELECT COUNT(*) FROM cortex_v2_search_head),
                (SELECT COUNT(*) FROM cortex_v2_search_fts_docsize),
                COALESCE((SELECT SUM(original_length) FROM cortex_v2_content_blob), 0),
                COALESCE((SELECT SUM(compressed_length) FROM cortex_v2_content_blob), 0)
            """;
        command.CommandText += version == TieredSchemaVersion
            ? ",COALESCE((SELECT SUM(compressed_length) FROM cortex_v2_content_blob WHERE storage_tier='hot'),0),COALESCE((SELECT SUM(compressed_length) FROM cortex_v2_content_blob WHERE storage_tier='cold'),0);"
            : ",COALESCE((SELECT SUM(compressed_length) FROM cortex_v2_content_blob),0),0;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Cortex v2 storage metrics are unavailable.");
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            SchemaVersion: version, HotCompressedBlobBytes: reader.GetInt64(8), ColdCompressedBlobBytes: reader.GetInt64(9));
    }

    public async ValueTask<SqliteCortexV2DoctorResult> FullDoctorAsync(
        CancellationToken cancellationToken = default)
    {
        long revisions = 0;
        long blobs = 0;
        long heads = 0;
        long chunks = 0;
        long fts = 0;
        try
        {
            var quick = await QuickCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!quick.Healthy)
                return new(false, quick.State, 0, 0, 0, 0, 0);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);

            if (!string.Equals(
                    await ReadScalarStringAsync(
                        connection, transaction, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false),
                    "ok",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("SQLite structural integrity failed.");
            }
            await RequireNoRowsAsync(
                connection, transaction, "PRAGMA foreign_key_check;", "SQLite foreign-key integrity failed.", cancellationToken)
                .ConfigureAwait(false);

            await using (var blobCommand = connection.CreateCommand())
            {
                blobCommand.Transaction = transaction;
                blobCommand.CommandText = """
                    SELECT content_hash, original_length, compressed_length, compressed_content
                      FROM cortex_v2_content_blob
                     ORDER BY content_hash;
                    """;
                await using var reader = await blobCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var hash = reader.GetString(0);
                    var originalLength = reader.GetInt64(1);
                    var content = await ReadPayloadAsync(connection, transaction, hash, cancellationToken,
                        maximumBytes: _options.MaximumContentBytes).ConfigureAwait(false);
                    if (content.LongLength != originalLength)
                        throw new InvalidDataException("A Cortex v2 blob length is inconsistent.");
                    if (!string.Equals(Sha256(content), hash, StringComparison.Ordinal))
                        throw new InvalidDataException("A Cortex v2 content-addressed blob failed its hash.");
                    blobs++;
                }
            }

            await ValidateRevisionChainsAsync(connection, transaction, cancellationToken, count => revisions = count)
                .ConfigureAwait(false);
            await RequireNoRowsAsync(
                connection,
                transaction,
                """
                    SELECT 1
                      FROM cortex_v2_record_head h
                     WHERE NOT EXISTS (
                            SELECT 1 FROM cortex_v2_record_revision r
                             WHERE r.namespace_name = h.namespace_name
                               AND r.kind = h.kind
                               AND r.record_id = h.record_id
                               AND r.revision = h.revision
                               AND r.revision_hash = h.revision_hash
                               AND r.content_hash = h.content_hash)
                        OR EXISTS (
                            SELECT 1 FROM cortex_v2_record_revision newer
                             WHERE newer.namespace_name = h.namespace_name
                               AND newer.kind = h.kind
                               AND newer.record_id = h.record_id
                               AND newer.revision > h.revision)
                     LIMIT 1;
                    """,
                "A Cortex v2 head is not the exact latest revision.",
                cancellationToken).ConfigureAwait(false);
            await RequireNoRowsAsync(
                connection,
                transaction,
                """
                    SELECT 1
                      FROM cortex_v2_search_head s
                      LEFT JOIN cortex_v2_record_head h
                        ON h.namespace_name = s.namespace_name
                       AND h.kind = s.kind
                       AND h.record_id = s.record_id
                     WHERE h.revision IS NULL
                        OR h.revision != s.revision
                        OR h.revision_hash != s.revision_hash
                     LIMIT 1;
                    """,
                "The Cortex v2 hot search projection contains a non-head revision.",
                cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceCountersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            heads = await ReadScalarInt64Async(
                connection, transaction, "SELECT COUNT(*) FROM cortex_v2_record_head;", cancellationToken).ConfigureAwait(false);
            chunks = await ReadScalarInt64Async(
                connection, transaction, "SELECT COUNT(*) FROM cortex_v2_search_head;", cancellationToken).ConfigureAwait(false);
            fts = await ReadScalarInt64Async(
                connection, transaction, "SELECT COUNT(*) FROM cortex_v2_search_fts_docsize;", cancellationToken).ConfigureAwait(false);
            if (fts != chunks)
                throw new InvalidDataException("The Cortex v2 head FTS cardinality differs from its source projection.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "ok", revisions, blobs, heads, chunks, fts);
        }
        catch (Pegasus.Cortex.Core.V2.CortexPayloadUnavailableException)
        {
            return new(false, Pegasus.Cortex.Core.V2.CortexPayloadUnavailableException.Code, revisions, blobs, heads, chunks, fts);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or SqliteException or InvalidOperationException or FormatException)
        {
            return new(false, "full-doctor-failed", revisions, blobs, heads, chunks, fts);
        }
    }

    /// <summary>
    /// Creates one online, consolidated SQLite snapshot at an explicit new
    /// caller-selected path. The backup path must end in cortex-v2.db so the
    /// same strict generation verifier can validate it. Existing files and
    /// sidecars are never overwritten or removed.
    /// </summary>
    public async ValueTask<SqliteCortexV2BackupReceipt> BackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var destination = ValidateIndependentDatabasePath(backupPath, _options.DatabasePath, mustExist: false);
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        EnsureParentDirectory(destination);
        RequireDatabaseEntryAbsent(destination, "The Cortex v2 backup destination");
        var parent = Path.GetDirectoryName(destination)!;
        var stagingDirectory = Path.Combine(parent, $".b-{Guid.NewGuid():N}");
        var staging = Path.Combine(stagingDirectory, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var writerHeld = false;
        var committed = false;
        try
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            writerHeld = true;
            // Recheck after taking the writer gate so a racing creator cannot
            // turn a create-only backup into an overwrite.
            RequireDatabaseEntryAbsent(destination, "The Cortex v2 backup destination");
            Directory.CreateDirectory(stagingDirectory);
            if ((new DirectoryInfo(stagingDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Cortex v2 backup staging directory cannot be a reparse point.");
            await using var source = CreateConnection();
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(source, cancellationToken).ConfigureAwait(false);
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = staging,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, _options.BusyTimeoutMilliseconds / 1_000)
            }.ToString());
            await target.OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            source.BackupDatabase(target);
            cancellationToken.ThrowIfCancellationRequested();
            await using var mode = target.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode = WAL;";
            if (!string.Equals(
                    Convert.ToString(await mode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture),
                    "wal",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The Cortex v2 backup could not retain WAL generation metadata.");
            }
            await target.CloseAsync().ConfigureAwait(false);
            _writer.Release();
            writerHeld = false;

            await HydrateSnapshotAsync(staging, cancellationToken).ConfigureAwait(false);
            var evidence = await SqliteCortexV2Recovery.ValidateSnapshotAsync(staging, cancellationToken)
                .ConfigureAwait(false);
            SqliteCortexV2Recovery.RejectLiveSidecars(
                staging, "The validated Cortex v2 backup staging database");
            var length = new FileInfo(staging).Length;
            var hash = await HashFileAsync(staging, cancellationToken).ConfigureAwait(false);
            RequireDatabaseEntryAbsent(destination, "The Cortex v2 backup destination");
            File.Move(staging, destination, overwrite: false);
            committed = true;
            try
            {
                Directory.Delete(stagingDirectory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The immutable backup is already committed. An empty staging
                // directory is safer than reporting the backup as failed.
            }
            return new(
                _options.DatabasePath,
                destination,
                length,
                hash,
                evidence.SchemaVersion,
                evidence.NamespaceRows,
                evidence.RevisionRows,
                evidence.HeadRows,
                _timeProvider.GetUtcNow().ToUniversalTime());
        }
        finally
        {
            if (writerHeld)
                _writer.Release();
            if (!committed)
                SqliteCortexV2Recovery.CleanupOwnedStaging(stagingDirectory, staging);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _initialization.Dispose();
            _writer.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    internal static string ValidateIndependentDatabasePath(
        string path,
        string? differentFrom,
        bool mustExist)
    {
        var validated = new SqliteCortexV2StoreOptions(path).Validate().DatabasePath;
        if (differentFrom is not null && string.Equals(
                validated,
                differentFrom,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Cortex v2 source and destination paths must differ.", nameof(path));
        if (mustExist && !File.Exists(validated))
            throw new FileNotFoundException("The Cortex v2 database snapshot does not exist.", validated);
        return validated;
    }

    internal static void EnsureParentDirectory(string databasePath)
    {
        var parent = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(parent);
        _ = new SqliteCortexV2StoreOptions(databasePath).Validate();
    }

    internal static void RequireDatabaseEntryAbsent(string databasePath, string description)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"{description} already exists or has a live sidecar.");
        }
    }

    internal static async ValueTask<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static readonly string[] RequiredSchemaObjects =
    [
        "cortex_v2_content_blob",
        "cortex_v2_meta",
        "cortex_v2_migration_lock",
        "cortex_v2_namespace",
        "cortex_v2_record_head",
        "cortex_v2_record_revision",
        "cortex_v2_search_fts",
        "cortex_v2_search_fts_config",
        "cortex_v2_search_fts_data",
        "cortex_v2_search_fts_docsize",
        "cortex_v2_search_fts_idx",
        "cortex_v2_search_head",
        "cortex_v2_search_head_ad",
        "cortex_v2_search_head_ai",
        "cortex_v2_search_head_au"
    ];

    private const string SchemaSql = """
        CREATE TABLE cortex_v2_meta(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL CHECK(schema_version = 2),
            generation TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE cortex_v2_migration_lock(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            generation TEXT NOT NULL,
            owner_token TEXT NOT NULL,
            acquired_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE cortex_v2_namespace(
            namespace_name TEXT PRIMARY KEY,
            namespace_token TEXT NOT NULL UNIQUE,
            revision_count INTEGER NOT NULL DEFAULT 0 CHECK(revision_count >= 0),
            head_count INTEGER NOT NULL DEFAULT 0 CHECK(head_count >= 0),
            referenced_content_bytes INTEGER NOT NULL DEFAULT 0 CHECK(referenced_content_bytes >= 0),
            head_content_bytes INTEGER NOT NULL DEFAULT 0 CHECK(head_content_bytes >= 0),
            hot_chunk_count INTEGER NOT NULL DEFAULT 0 CHECK(hot_chunk_count >= 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE cortex_v2_content_blob(
            content_hash TEXT PRIMARY KEY CHECK(length(content_hash) = 64),
            compression TEXT NOT NULL CHECK(compression = 'br'),
            original_length INTEGER NOT NULL CHECK(original_length >= 0),
            compressed_length INTEGER NOT NULL CHECK(compressed_length >= 0),
            compressed_content BLOB NOT NULL,
            created_at_utc TEXT NOT NULL,
            CHECK(length(compressed_content) = compressed_length)
        ) STRICT;

        CREATE TABLE cortex_v2_record_revision(
            namespace_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            record_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision > 0),
            previous_revision_hash TEXT NULL,
            revision_hash TEXT NOT NULL CHECK(length(revision_hash) = 64),
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            title TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            tags_search TEXT NOT NULL,
            summary TEXT NOT NULL,
            attributes_json TEXT NOT NULL,
            event_type TEXT NOT NULL,
            actor TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            request_hash TEXT NOT NULL CHECK(length(request_hash) = 64),
            is_searchable INTEGER NOT NULL CHECK(is_searchable IN (0, 1)),
            occurred_at_utc TEXT NOT NULL,
            PRIMARY KEY(namespace_name, kind, record_id, revision),
            UNIQUE(namespace_name, actor, idempotency_key),
            UNIQUE(revision_hash),
            FOREIGN KEY(namespace_name) REFERENCES cortex_v2_namespace(namespace_name) ON DELETE RESTRICT,
            FOREIGN KEY(content_hash) REFERENCES cortex_v2_content_blob(content_hash) ON DELETE RESTRICT,
            CHECK((revision = 1 AND previous_revision_hash IS NULL) OR
                  (revision > 1 AND length(previous_revision_hash) = 64))
        ) STRICT;

        CREATE INDEX ix_cortex_v2_revision_history
            ON cortex_v2_record_revision(namespace_name, kind, record_id, revision);

        CREATE TABLE cortex_v2_record_head(
            namespace_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            record_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            revision_hash TEXT NOT NULL CHECK(length(revision_hash) = 64),
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            PRIMARY KEY(namespace_name, kind, record_id),
            FOREIGN KEY(namespace_name, kind, record_id, revision)
                REFERENCES cortex_v2_record_revision(namespace_name, kind, record_id, revision)
                ON DELETE RESTRICT
        ) STRICT;

        CREATE TABLE cortex_v2_search_head(
            search_row_id INTEGER PRIMARY KEY,
            namespace_name TEXT NOT NULL,
            namespace_token TEXT NOT NULL,
            kind TEXT NOT NULL,
            record_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            revision_hash TEXT NOT NULL CHECK(length(revision_hash) = 64),
            chunk_ordinal INTEGER NOT NULL CHECK(chunk_ordinal >= 0),
            title TEXT NOT NULL,
            tags TEXT NOT NULL,
            summary TEXT NOT NULL,
            body TEXT NOT NULL,
            UNIQUE(namespace_name, kind, record_id, chunk_ordinal),
            FOREIGN KEY(namespace_name, kind, record_id)
                REFERENCES cortex_v2_record_head(namespace_name, kind, record_id)
                ON DELETE RESTRICT
        ) STRICT;

        CREATE INDEX ix_cortex_v2_search_record
            ON cortex_v2_search_head(namespace_name, kind, record_id, chunk_ordinal);

        CREATE VIRTUAL TABLE cortex_v2_search_fts USING fts5(
            namespace_token,
            kind,
            title,
            tags,
            summary,
            body,
            content = 'cortex_v2_search_head',
            content_rowid = 'search_row_id',
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER cortex_v2_search_head_ai AFTER INSERT ON cortex_v2_search_head BEGIN
            INSERT INTO cortex_v2_search_fts(
                rowid, namespace_token, kind, title, tags, summary, body)
            VALUES(
                new.search_row_id, new.namespace_token, new.kind,
                new.title, new.tags, new.summary, new.body);
        END;

        CREATE TRIGGER cortex_v2_search_head_ad AFTER DELETE ON cortex_v2_search_head BEGIN
            INSERT INTO cortex_v2_search_fts(
                cortex_v2_search_fts, rowid, namespace_token, kind, title, tags, summary, body)
            VALUES(
                'delete', old.search_row_id, old.namespace_token, old.kind,
                old.title, old.tags, old.summary, old.body);
        END;

        CREATE TRIGGER cortex_v2_search_head_au AFTER UPDATE ON cortex_v2_search_head BEGIN
            INSERT INTO cortex_v2_search_fts(
                cortex_v2_search_fts, rowid, namespace_token, kind, title, tags, summary, body)
            VALUES(
                'delete', old.search_row_id, old.namespace_token, old.kind,
                old.title, old.tags, old.summary, old.body);
            INSERT INTO cortex_v2_search_fts(
                rowid, namespace_token, kind, title, tags, summary, body)
            VALUES(
                new.search_row_id, new.namespace_token, new.kind,
                new.title, new.tags, new.summary, new.body);
        END;
        """;

    private SqliteConnection CreateConnection(bool createIfMissing = false) => CortexSqliteTraceMeasurement.Observe(new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            // Deterministic release matters for backup, restore, side-by-side
            // rollback, and test cleanup. MCP is already a warm process and
            // SQLite shared-cache remains enabled, so v2 avoids process-global
            // pooled handles rather than clearing unrelated v1 pools.
            Pooling = false,
            DefaultTimeout = Math.Max(1, _options.BusyTimeoutMilliseconds / 1_000)
        }.ToString()));

    private async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};
            PRAGMA trusted_schema = OFF;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await ApplyPageCeilingAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyPageCeilingAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // max_page_count is a connection policy, not durable database metadata.
        // Apply the selected capacity profile before every transaction so an
        // admitted pre-existing store and all later operation connections use
        // the same ceiling. SQLite refuses to lower this value below the
        // current page count; verifying that exact floor preserves an existing
        // oversized store without silently leaving the engine-wide default in
        // effect for smaller databases.
        var effectiveMaximum = await ReadScalarInt64Async(
            connection,
            transaction: null,
            $"PRAGMA max_page_count = {_options.MaximumDatabasePages};",
            cancellationToken).ConfigureAwait(false);
        if (effectiveMaximum < _options.MaximumDatabasePages)
        {
            throw new InvalidOperationException(
                "SQLite could not apply the configured Cortex v2 active-store page ceiling.");
        }
        if (effectiveMaximum > _options.MaximumDatabasePages)
        {
            var currentPages = await ReadScalarInt64Async(
                connection,
                transaction: null,
                "PRAGMA page_count;",
                cancellationToken).ConfigureAwait(false);
            if (effectiveMaximum != currentPages)
            {
                throw new InvalidOperationException(
                    "SQLite could not apply the configured Cortex v2 active-store page ceiling.");
            }
        }
    }

    private async ValueTask ConfigureNewDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA page_size = 4096;
            PRAGMA journal_mode = WAL;
            PRAGMA auto_vacuum = INCREMENTAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CreateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var now = FormatUtc(_timeProvider.GetUtcNow());
        var owner = $"init_{Guid.NewGuid():N}";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql + """
            INSERT INTO cortex_v2_meta(singleton, schema_version, generation, created_at_utc)
            VALUES(1, $version, $generation, $now);
            INSERT INTO cortex_v2_migration_lock(
                singleton, generation, owner_token, acquired_at_utc, completed_at_utc)
            VALUES(1, $generation, $owner, $now, $now);
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.Parameters.AddWithValue("$generation", Generation);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteCortexV2AppendRequest ValidateAppend(SqliteCortexV2AppendRequest? request)
    {
        var value = request ?? throw new ArgumentNullException(nameof(request));
        ValidateIdentity(value.Namespace, value.Kind, value.RecordId);
        RequireIdentifier(value.IdempotencyKey, nameof(value.IdempotencyKey));
        RequireIdentifier(value.Actor, nameof(value.Actor));
        RequireIdentifier(value.EventType, nameof(value.EventType));
        if (value.ExpectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(value.ExpectedRevision));
        if (string.IsNullOrWhiteSpace(value.Title) ||
            value.Title.Length > MaximumTitleCharacters ||
            value.Title.Any(IsUnsupportedSearchControl))
        {
            throw new ArgumentException("A bounded title is required.", nameof(value.Title));
        }
        if (value.Summary is null ||
            value.Summary.Length > MaximumSummaryCharacters ||
            value.Summary.Any(IsUnsupportedSearchControl))
        {
            throw new ArgumentException("A bounded summary is required.", nameof(value.Summary));
        }
        ArgumentNullException.ThrowIfNull(value.Tags);
        if (value.Tags.Count > MaximumTags)
            throw new ArgumentOutOfRangeException(nameof(value.Tags));
        var tags = value.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (tags.Length != value.Tags.Count || tags.Any(tag => !IsTag(tag)))
            throw new ArgumentException("Tags must be unique bounded portable values.", nameof(value.Tags));
        if (value.ContentUtf8 is { Length: > 0 } content && content.Length > _options.MaximumContentBytes)
            throw new ArgumentOutOfRangeException(nameof(value.ContentUtf8));
        if (value.ContentUtf8 is not null)
            _ = StrictUtf8.GetString(value.ContentUtf8);
        if (value.Kind is "task" or "knowledge")
        {
            Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticAccepted(
                value.Namespace, value.RecordId, value.Actor, value.IdempotencyKey, value.Title, value.Summary,
                value.ContentUtf8 is null ? null : StrictUtf8.GetString(value.ContentUtf8));
            Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticAccepted(value.Tags.ToArray());
        }
        try
        {
            using var attributes = JsonDocument.Parse(value.AttributesJson);
            if (attributes.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Cortex v2 attributes must be one JSON object.", nameof(value.AttributesJson));
            if (value.Kind is "task" or "knowledge")
                Pegasus.Cortex.Core.Retention.CortexRetentionContentPolicy.RequireSemanticJsonAccepted(attributes.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Cortex v2 attributes must be valid JSON.", nameof(value.AttributesJson), exception);
        }
        if (value.IdempotencyFingerprint is not null &&
            (value.IdempotencyFingerprint.Length != 64 ||
             value.IdempotencyFingerprint.Any(character =>
                 character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException(
                "A supplied idempotency fingerprint must be one lowercase SHA-256 value.",
                nameof(value.IdempotencyFingerprint));
        }
        return value with { Tags = Array.AsReadOnly(tags), ContentUtf8 = value.ContentUtf8?.ToArray() };
    }

    private SqliteCortexV2SearchRequest ValidateSearch(SqliteCortexV2SearchRequest? request)
    {
        var value = request ?? throw new ArgumentNullException(nameof(request));
        RequireIdentifier(value.Namespace, nameof(value.Namespace));
        if (value.Kind is not null)
            RequireIdentifier(value.Kind, nameof(value.Kind));
        if (string.IsNullOrWhiteSpace(value.Query) || value.Query.Length > MaximumSearchQueryCharacters)
            throw new ArgumentException("A bounded search query is required.", nameof(value.Query));
        if (value.Limit is < 1 or > MaximumSearchLimit)
            throw new ArgumentOutOfRangeException(nameof(value.Limit));
        if (value.MinimumMatch is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(value.MinimumMatch));
        return value;
    }

    private void PrepareDirectory(bool databaseExists)
    {
        var parent = Path.GetDirectoryName(_options.DatabasePath)!;
        if (!Directory.Exists(parent))
        {
            if (databaseExists)
                throw new DirectoryNotFoundException("The Cortex v2 database parent is unavailable.");
            Directory.CreateDirectory(parent);
        }
    }

    private static async ValueTask EnsureNamespaceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cortex_v2_namespace(
                namespace_name, namespace_token, created_at_utc, updated_at_utc)
            VALUES($namespace, $token, $now, $now)
            ON CONFLICT(namespace_name) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InsertBlobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        byte[] content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var compressed = Compress(content);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cortex_v2_content_blob(
                content_hash, compression, original_length, compressed_length,
                compressed_content, created_at_utc)
            VALUES($hash, 'br', $originalLength, $compressedLength, $content, $now)
            ON CONFLICT(content_hash) DO NOTHING;
            """;
        var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version == TieredSchemaVersion)
            command.CommandText = """
                INSERT INTO cortex_v2_content_blob(
                    content_hash,compression,original_length,compressed_length,compressed_content,created_at_utc,storage_tier)
                VALUES($hash,'br',$originalLength,$compressedLength,$content,$now,'hot')
                ON CONFLICT(content_hash) DO NOTHING;
                """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$originalLength", content.Length);
        command.Parameters.AddWithValue("$compressedLength", compressed.Length);
        command.Parameters.Add("$content", SqliteType.Blob).Value = compressed;
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _ = await ReadPayloadAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);
        var storedLength = await ReadBlobLengthAsync(connection, transaction, hash, cancellationToken)
            .ConfigureAwait(false);
        if (storedLength != content.Length)
            throw new InvalidDataException("A Cortex v2 content hash collision was detected.");
    }

    private static async ValueTask InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCortexV2AppendRequest request,
        long revision,
        string? previousRevisionHash,
        string revisionHash,
        string contentHash,
        string tagsJson,
        string tagsSearch,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cortex_v2_record_revision(
                namespace_name, kind, record_id, revision, previous_revision_hash,
                revision_hash, content_hash, title, tags_json, tags_search, summary,
                attributes_json, event_type, actor, idempotency_key, request_hash, is_searchable,
                occurred_at_utc)
            VALUES(
                $namespace, $kind, $record, $revision, $previous,
                $revisionHash, $contentHash, $title, $tagsJson, $tagsSearch, $summary,
                $attributes, $event, $actor, $idempotency, $requestHash, $searchable, $occurred);
            """;
        command.Parameters.AddWithValue("$namespace", request.Namespace);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$record", request.RecordId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$previous", previousRevisionHash is null ? DBNull.Value : previousRevisionHash);
        command.Parameters.AddWithValue("$revisionHash", revisionHash);
        command.Parameters.AddWithValue("$contentHash", contentHash);
        command.Parameters.AddWithValue("$title", request.Title);
        command.Parameters.AddWithValue("$tagsJson", tagsJson);
        command.Parameters.AddWithValue("$tagsSearch", tagsSearch);
        command.Parameters.AddWithValue("$summary", request.Summary);
        command.Parameters.AddWithValue("$attributes", request.AttributesJson);
        command.Parameters.AddWithValue("$event", request.EventType);
        command.Parameters.AddWithValue("$actor", request.Actor);
        command.Parameters.AddWithValue("$idempotency", request.IdempotencyKey);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$searchable", request.Searchable ? 1 : 0);
        command.Parameters.AddWithValue("$occurred", FormatUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpsertHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCortexV2AppendRequest request,
        long revision,
        string revisionHash,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cortex_v2_record_head(
                namespace_name, kind, record_id, revision, revision_hash, content_hash)
            VALUES($namespace, $kind, $record, $revision, $revisionHash, $contentHash)
            ON CONFLICT(namespace_name, kind, record_id) DO UPDATE SET
                revision = excluded.revision,
                revision_hash = excluded.revision_hash,
                content_hash = excluded.content_hash;
            """;
        command.Parameters.AddWithValue("$namespace", request.Namespace);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$record", request.RecordId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$revisionHash", revisionHash);
        command.Parameters.AddWithValue("$contentHash", contentHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DeleteSearchHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM cortex_v2_search_head
             WHERE namespace_name = $namespace AND kind = $kind AND record_id = $record;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertSearchHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCortexV2AppendRequest request,
        string namespaceToken,
        long revision,
        string revisionHash,
        string tagsSearch,
        IReadOnlyList<string> chunks,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < chunks.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cortex_v2_search_head(
                    namespace_name, namespace_token, kind, record_id, revision,
                    revision_hash, chunk_ordinal, title, tags, summary, body)
                VALUES(
                    $namespace, $token, $kind, $record, $revision,
                    $revisionHash, $ordinal, $title, $tags, $summary, $body);
                """;
            command.Parameters.AddWithValue("$namespace", request.Namespace);
            command.Parameters.AddWithValue("$token", namespaceToken);
            command.Parameters.AddWithValue("$kind", request.Kind);
            command.Parameters.AddWithValue("$record", request.RecordId);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$revisionHash", revisionHash);
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$title", NormalizeSearchText(request.Title));
            command.Parameters.AddWithValue("$tags", NormalizeSearchText(tagsSearch));
            command.Parameters.AddWithValue("$summary", NormalizeSearchText(request.Summary));
            command.Parameters.AddWithValue("$body", NormalizeSearchText(chunks[index]));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask UpdateNamespaceCountersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        bool isNewHead,
        long referencedBytes,
        long headByteDelta,
        long hotChunkDelta,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE cortex_v2_namespace
               SET revision_count = revision_count + 1,
                   head_count = head_count + $newHead,
                   referenced_content_bytes = referenced_content_bytes + $referencedBytes,
                   head_content_bytes = head_content_bytes + $headByteDelta,
                   hot_chunk_count = hot_chunk_count + $chunkDelta,
                   updated_at_utc = $now
             WHERE namespace_name = $namespace;
            """;
        command.Parameters.AddWithValue("$newHead", isNewHead ? 1 : 0);
        command.Parameters.AddWithValue("$referencedBytes", referencedBytes);
        command.Parameters.AddWithValue("$headByteDelta", headByteDelta);
        command.Parameters.AddWithValue("$chunkDelta", hotChunkDelta);
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        command.Parameters.AddWithValue("$namespace", ns);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The Cortex v2 namespace counter update was not singular.");
    }

    private async ValueTask<SqliteCortexV2Record?> ReadRecordAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ns,
        string kind,
        string recordId,
        long? revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = revision is null
            ? """
                SELECT r.revision, r.revision_hash, r.previous_revision_hash,
                       r.content_hash, r.title, r.tags_json, r.summary,
                       r.attributes_json, r.event_type, r.actor, r.occurred_at_utc, r.is_searchable,
                       b.original_length, b.compressed_content
                  FROM cortex_v2_record_head h
                  JOIN cortex_v2_record_revision r
                    ON r.namespace_name = h.namespace_name
                   AND r.kind = h.kind
                   AND r.record_id = h.record_id
                   AND r.revision = h.revision
                  JOIN cortex_v2_content_blob b ON b.content_hash = r.content_hash
                 WHERE h.namespace_name = $namespace AND h.kind = $kind AND h.record_id = $record;
                """
            : """
                SELECT r.revision, r.revision_hash, r.previous_revision_hash,
                       r.content_hash, r.title, r.tags_json, r.summary,
                       r.attributes_json, r.event_type, r.actor, r.occurred_at_utc, r.is_searchable,
                       b.original_length, b.compressed_content
                  FROM cortex_v2_record_revision r
                  JOIN cortex_v2_content_blob b ON b.content_hash = r.content_hash
                 WHERE r.namespace_name = $namespace AND r.kind = $kind
                   AND r.record_id = $record AND r.revision = $revision;
                """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        if (revision is not null)
            command.Parameters.AddWithValue("$revision", revision.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var hash = reader.GetString(3);
        var content = await ReadPayloadAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, Sha256(content), StringComparison.Ordinal))
            throw new InvalidDataException("The Cortex v2 record content failed its immutable hash.");
        return new(
            ns,
            kind,
            recordId,
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            hash,
            reader.GetString(4),
            DeserializeTags(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            ParseUtc(reader.GetString(10)),
            reader.GetInt64(11) == 1,
            content);
    }

    private static async ValueTask<RevisionMetadata?> ReadHeadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, revision_hash, content_hash
              FROM cortex_v2_record_head
             WHERE namespace_name = $namespace AND kind = $kind AND record_id = $record;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        return await ReadRevisionMetadataAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RevisionMetadata?> ReadRevisionMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string kind,
        string recordId,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, revision_hash, content_hash
              FROM cortex_v2_record_revision
             WHERE namespace_name = $namespace AND kind = $kind
               AND record_id = $record AND revision = $revision;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        command.Parameters.AddWithValue("$revision", revision);
        return await ReadRevisionMetadataAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RevisionMetadata?> ReadRevisionMetadataAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async ValueTask<IdempotencyMetadata?> FindIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string actor,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT namespace_name, kind, record_id, revision, revision_hash,
                   content_hash, request_hash
              FROM cortex_v2_record_revision
             WHERE namespace_name = $namespace AND actor = $actor
               AND idempotency_key = $idempotency;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$idempotency", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6))
            : null;
    }

    private async ValueTask<BlobValue?> ReadBlobAsync(
        SqliteConnection connection, SqliteTransaction transaction, string contentHash,
        CancellationToken cancellationToken) =>
        new(await ReadPayloadAsync(connection, transaction, contentHash, cancellationToken).ConfigureAwait(false));

    private static async ValueTask<long> ReadBlobLengthAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT original_length FROM cortex_v2_content_blob WHERE content_hash = $hash;";
        command.Parameters.AddWithValue("$hash", hash);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidDataException("A referenced Cortex v2 content blob is missing.")
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> CountRecordChunksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ns,
        string kind,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM cortex_v2_search_head
             WHERE namespace_name = $namespace AND kind = $kind AND record_id = $record;
            """;
        command.Parameters.AddWithValue("$namespace", ns);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$record", recordId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask ValidateRevisionChainsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        Action<long> reportCount)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT namespace_name, kind, record_id, revision, previous_revision_hash,
                   revision_hash, content_hash, title, tags_json, summary,
                   attributes_json, event_type, actor, idempotency_key, is_searchable, occurred_at_utc
              FROM cortex_v2_record_revision
             ORDER BY namespace_name, kind, record_id, revision;
            """;
        string? previousIdentity = null;
        string? previousHash = null;
        long previousRevision = 0;
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ns = reader.GetString(0);
            var kind = reader.GetString(1);
            var record = reader.GetString(2);
            var revision = reader.GetInt64(3);
            var suppliedPrevious = reader.IsDBNull(4) ? null : reader.GetString(4);
            var observedHash = reader.GetString(5);
            var identity = $"{ns}\n{kind}\n{record}";
            if (!string.Equals(identity, previousIdentity, StringComparison.Ordinal))
            {
                if (revision != 1 || suppliedPrevious is not null)
                    throw new InvalidDataException("A Cortex v2 revision chain does not begin at revision one.");
            }
            else if (revision != previousRevision + 1 ||
                     !string.Equals(suppliedPrevious, previousHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A Cortex v2 revision chain is not contiguous.");
            }

            var request = new SqliteCortexV2AppendRequest(
                ns, kind, record, revision - 1, reader.GetString(13), reader.GetString(12),
                reader.GetString(11), reader.GetString(7), DeserializeTags(reader.GetString(8)),
                reader.GetString(9), null, reader.GetInt64(14) == 1, reader.GetString(10));
            var expectedHash = RevisionHash(
                request,
                revision,
                suppliedPrevious,
                reader.GetString(6),
                reader.GetString(8),
                ParseUtc(reader.GetString(15)));
            if (!string.Equals(expectedHash, observedHash, StringComparison.Ordinal))
                throw new InvalidDataException("A Cortex v2 revision failed its immutable hash.");
            previousIdentity = identity;
            previousRevision = revision;
            previousHash = observedHash;
            count++;
        }
        reportCount(count);
    }

    private static async ValueTask ValidateNamespaceCountersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await RequireNoRowsAsync(
            connection,
            transaction,
            """
                SELECT 1
                  FROM cortex_v2_namespace n
                 WHERE n.revision_count != (
                           SELECT COUNT(*) FROM cortex_v2_record_revision r
                            WHERE r.namespace_name = n.namespace_name)
                    OR n.head_count != (
                           SELECT COUNT(*) FROM cortex_v2_record_head h
                            WHERE h.namespace_name = n.namespace_name)
                    OR n.referenced_content_bytes != (
                           SELECT COALESCE(SUM(b.original_length), 0)
                             FROM cortex_v2_record_revision r
                             JOIN cortex_v2_content_blob b ON b.content_hash = r.content_hash
                            WHERE r.namespace_name = n.namespace_name)
                    OR n.head_content_bytes != (
                           SELECT COALESCE(SUM(b.original_length), 0)
                             FROM cortex_v2_record_head h
                             JOIN cortex_v2_content_blob b ON b.content_hash = h.content_hash
                            WHERE h.namespace_name = n.namespace_name)
                    OR n.hot_chunk_count != (
                           SELECT COUNT(*) FROM cortex_v2_search_head s
                            WHERE s.namespace_name = n.namespace_name)
                 LIMIT 1;
                """,
            "A Cortex v2 namespace counter does not match retained state.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RequireNoRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException(message);
    }

    private static string RequestHash(SqliteCortexV2AppendRequest request, string contentHash)
    {
        var payload = request.IdempotencyFingerprint ?? HashFields(
            request.EventType, request.Title, SerializeTags(request.Tags), request.Summary,
            contentHash, request.Searchable ? "1" : "0", request.AttributesJson);
        return HashFields(
            request.Namespace, request.Kind, request.RecordId,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
            request.IdempotencyKey, request.Actor, payload);
    }

    private static string RevisionHash(
        SqliteCortexV2AppendRequest request,
        long revision,
        string? previousRevisionHash,
        string contentHash,
        string tagsJson,
        DateTimeOffset occurredAt) =>
        HashFields(
            request.Namespace, request.Kind, request.RecordId,
            revision.ToString(CultureInfo.InvariantCulture), previousRevisionHash ?? string.Empty,
            contentHash, request.Title, tagsJson, request.Summary, request.EventType,
            request.Actor, request.IdempotencyKey, request.Searchable ? "1" : "0",
            request.AttributesJson, FormatUtc(occurredAt));

    private static string HashFields(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = StrictUtf8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(content);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed, int expectedLength, int maximumLength)
    {
        if (expectedLength < 0 || expectedLength > maximumLength)
            throw new InvalidDataException("A Cortex v2 blob exceeds its decompression bound.");
        using var input = new MemoryStream(compressed, writable: false);
        using var stream = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        stream.CopyTo(output);
        if (output.Length != expectedLength)
            throw new InvalidDataException("A Cortex v2 blob decompressed to an unexpected length.");
        return output.ToArray();
    }

    private static string NamespaceToken(string ns) =>
        $"n{Sha256(StrictUtf8.GetBytes(ns))}";

    private static string BuildFtsQuery(string ns, string? kind, IReadOnlyList<string> terms)
    {
        var content = string.Join(" OR ", terms.Select(QuoteFts));
        var query = $"namespace_token : {QuoteFts(NamespaceToken(ns))} AND {{ title tags summary body }} : ({content})";
        if (kind is not null)
            query += $" AND kind : {QuoteFts(kind)}";
        return query;
    }

    private static string QuoteFts(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string NormalizeSearchText(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            result.Append(IsUnsupportedSearchControl(character) ? ' ' : character);
        return result.ToString();
    }

    private static bool IsUnsupportedSearchControl(char character) =>
        char.IsControl(character) && character is not '\r' and not '\n' and not '\t';

    private static string SerializeTags(IReadOnlyList<string> tags) =>
        JsonSerializer.Serialize(tags);

    private static IReadOnlyList<string> DeserializeTags(string value) =>
        Array.AsReadOnly(
            JsonSerializer.Deserialize<string[]>(value) ??
            throw new InvalidDataException("Cortex v2 tags are malformed."));

    private static void ValidateIdentity(string ns, string kind, string recordId)
    {
        RequireIdentifier(ns, nameof(ns));
        RequireIdentifier(kind, nameof(kind));
        RequireIdentifier(recordId, nameof(recordId));
    }

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("A bounded portable identifier is required.", parameterName);
        }
    }

    private static bool IsTag(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= MaximumTagCharacters &&
        value.All(character => !char.IsControl(character));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken) =>
        await TableExistsAsync(connection, name, transaction: null, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string name,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<long> CountUserSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        await ReadScalarInt64Async(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';",
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<long> CountRequiredObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new List<string>(RequiredSchemaObjects.Length);
        for (var index = 0; index < RequiredSchemaObjects.Length; index++)
        {
            var name = $"$name{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, RequiredSchemaObjects[index]);
        }
        command.CommandText = $"""
            SELECT COUNT(*) FROM sqlite_master WHERE name IN ({string.Join(',', parameters)})
              AND type = CASE WHEN name IN (
                  'cortex_v2_search_head_ad', 'cortex_v2_search_head_ai', 'cortex_v2_search_head_au')
                  THEN 'trigger' ELSE 'table' END;
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async ValueTask VerifyRequiredObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (await CountRequiredObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false) !=
            RequiredSchemaObjects.Length)
        {
            throw new InvalidDataException("The Cortex v2 schema is incomplete or unsupported.");
        }
        // Metadata shape was admitted before its values were read. The remaining
        // fixed table contracts are checked once, without scanning corpus rows.
        var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var table in RequiredTableShapes)
            await VerifyRequiredTableShapeAsync(connection, transaction,
                version == TieredSchemaVersion && table.Name == "cortex_v2_content_blob" ? TieredBlobShape : table,
                cancellationToken).ConfigureAwait(false);
        if (version == TieredSchemaVersion)
            await VerifyTieringIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask VerifyGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await VerifyRequiredTableShapeAsync(connection, transaction, RequiredMetadataShape, cancellationToken)
            .ConfigureAwait(false);
        var (version, generation) = await ReadGenerationAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (version is not (SchemaVersion or TieredSchemaVersion) || !string.Equals(generation, Generation, StringComparison.Ordinal))
            throw new InvalidDataException("The Cortex v2 database generation is unsupported; automatic migration is prohibited.");
    }

    private async ValueTask<(int Version, string Generation)> ReadGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version, generation FROM cortex_v2_meta WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The Cortex v2 schema metadata is missing or ambiguous.");
        var result = (reader.GetInt32(0), reader.GetString(1));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The Cortex v2 schema metadata is ambiguous.");
        // A transaction may be inspecting its own not-yet-committed upgrade.
        // Only a committed observation refreshes the externally visible state.
        if (transaction is null) Volatile.Write(ref _openedSchemaVersion, result.Item1);
        return result;
    }

    private static async ValueTask RequireWalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var journal = await ReadScalarStringAsync(
            connection, transaction, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Cortex v2 database is not using WAL journaling.");
    }

    private static async ValueTask<long> ReadScalarInt64Async(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string> ReadScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record RevisionMetadata(long Revision, string RevisionHash, string ContentHash);
    private sealed record IdempotencyMetadata(
        string Namespace,
        string Kind,
        string RecordId,
        long Revision,
        string RevisionHash,
        string ContentHash,
        string RequestHash);
    private sealed record BlobValue(byte[] Content);

    private sealed class SearchAccumulator(
        string kind,
        string recordId,
        long revision,
        string revisionHash,
        string title,
        string tags,
        string summary,
        string attributesJson,
        string excerpt,
        double rank,
        HashSet<string> matchedTerms,
        HashSet<string> titleTagTerms)
    {
        public string Kind { get; } = kind;
        public string RecordId { get; } = recordId;
        public long Revision { get; } = revision;
        public string RevisionHash { get; } = revisionHash;
        public string Title { get; } = title;
        public string Tags { get; } = tags;
        public string Summary { get; } = summary;
        public string AttributesJson { get; } = attributesJson;
        public string Excerpt { get; set; } = excerpt;
        public double Rank { get; set; } = rank;
        public HashSet<string> MatchedTerms { get; } = matchedTerms;
        public HashSet<string> TitleTagTerms { get; } = titleTagTerms;
    }
}
