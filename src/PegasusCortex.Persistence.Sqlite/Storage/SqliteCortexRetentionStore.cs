using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Security;

namespace Pegasus.Cortex.Persistence.Sqlite.Storage;

/// <summary>
/// Local-only SQLite implementation for passive agent-authored retention.
/// It owns no network transport and never turns retained content into authority.
/// </summary>
public sealed class SqliteCortexRetentionStore :
    ICortexAgentRetentionRepository,
    ICortexLocalStoreMaintenance,
    IAsyncDisposable
{
    private readonly SqliteCortexStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private volatile bool _initialized;
    private int _disposed;

    public SqliteCortexRetentionStore(
        SqliteCortexStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string DatabasePath => _options.DatabasePath;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_initialized)
            return;
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            PrepareStoreDirectory();
            var existingStore = File.Exists(_options.DatabasePath);
            if (!existingStore)
            {
                SqliteLocalPathPolicy.EnsureSidecarsAbsent(
                    _options.DatabasePath,
                    "A new Cortex database");
            }
            await using var connection = CreateConnection(createIfMissing: !existingStore);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (existingStore)
            {
                await SqliteCortexSchemaVerifier.VerifyAsync(
                    connection,
                    transaction: null,
                    cancellationToken: cancellationToken,
                    requireTaskSnapshotHeadIndex: false).ConfigureAwait(false);
                await EnsureTaskSnapshotHeadIndexAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
                await SqliteCortexSchemaVerifier.VerifyAsync(
                    connection,
                    transaction: null,
                    cancellationToken: cancellationToken,
                    requireTaskSnapshotHeadIndex: true).ConfigureAwait(false);
            }
            else
            {
                await using var command = connection.CreateCommand();
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await SqliteCortexSchemaVerifier.VerifyAsync(
                    connection,
                    transaction: null,
                    cancellationToken).ConfigureAwait(false);
            }
            await EnsureWalJournalAsync(connection, cancellationToken).ConfigureAwait(false);
            ProtectStoreFile();
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async ValueTask<CortexRetentionAppendResult> AppendAsync(
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var captured = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);

            var replay = await FindByIdempotencyAsync(
                connection,
                transaction,
                captured,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                var replayHead = await FindHeadCoreAsync(
                    connection,
                    transaction,
                    replay.ProjectId,
                    replay.Kind,
                    replay.DocumentId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return IsExactReplay(replay, captured)
                    ? new CortexRetentionAppendResult(
                        CortexRetentionWriteDispositions.Idempotent,
                        replay.Snapshot(),
                        replayHead?.ContentHash)
                    : new CortexRetentionAppendResult(
                        CortexRetentionWriteDispositions.Conflict,
                        null,
                        replayHead?.ContentHash);
            }

            if (string.Equals(
                    captured.Kind,
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    StringComparison.Ordinal))
            {
                var conflictingTaskHead =
                    await FindConflictingTaskSnapshotHeadAsync(
                        connection,
                        transaction,
                        captured.ProjectId,
                        captured.DocumentId,
                        cancellationToken).ConfigureAwait(false);
                if (conflictingTaskHead is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new CortexRetentionAppendResult(
                        CortexRetentionWriteDispositions.Conflict,
                        null,
                        conflictingTaskHead.ContentHash);
                }
            }

            var head = await FindHeadCoreAsync(
                connection,
                transaction,
                captured.ProjectId,
                captured.Kind,
                captured.DocumentId,
                cancellationToken).ConfigureAwait(false);
            if (!MatchesExpectedHead(captured, head))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CortexRetentionAppendResult(
                    CortexRetentionWriteDispositions.StaleBase,
                    head?.Snapshot(),
                    head?.ContentHash);
            }

            await EnsureQuotaAvailableAsync(
                connection,
                transaction,
                captured.ProjectId,
                cancellationToken).ConfigureAwait(false);
            await InsertDocumentAsync(connection, transaction, captured, cancellationToken)
                .ConfigureAwait(false);
            await UpsertHeadAsync(connection, transaction, captured, cancellationToken)
                .ConfigureAwait(false);
            await IndexDocumentAsync(connection, transaction, captured, cancellationToken)
                .ConfigureAwait(false);
            await AppendAuditAsync(connection, transaction, captured, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new CortexRetentionAppendResult(
                CortexRetentionWriteDispositions.Appended,
                ToDocument(captured),
                captured.ContentHash);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async ValueTask<CortexRetainedDocument?> FindHeadAsync(
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateLookup(projectId, kind, documentId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return await FindHeadCoreAsync(
            connection,
            transaction: null,
            projectId,
            kind,
            documentId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexRetainedDocument?> FindRevisionAsync(
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateLookup(projectId, kind, documentId);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectDocumentSql + """
            WHERE d.project_id = $project
              AND d.kind = $kind
              AND d.document_id = $document
              AND d.revision = $revision;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$revision", revision);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<CortexRetentionHeadPage> ListHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken) =>
        ListHeadsByKindCoreAsync(
            projectId,
            kind,
            afterDocumentId,
            limit,
            includeExpired: false,
            cancellationToken: cancellationToken);

    public ValueTask<CortexRetentionHeadPage> ListAllHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken) =>
        ListHeadsByKindCoreAsync(
            projectId,
            kind,
            afterDocumentId,
            limit,
            includeExpired: true,
            cancellationToken: cancellationToken);

    private async ValueTask<CortexRetentionHeadPage> ListHeadsByKindCoreAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        bool includeExpired,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateLookup(projectId, kind, afterDocumentId ?? "page");
        if (limit is < 1 or >
            CortexAgentRetentionLimits.MaximumKindHeadPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectDocumentSql + """
            JOIN retained_head h
              ON h.project_id = d.project_id
             AND h.kind = d.kind
             AND h.document_id = d.document_id
             AND h.revision = d.revision
            WHERE h.project_id = $project
              AND h.kind = $kind
              AND ($after IS NULL OR h.document_id > $after)
              AND ($includeExpired = 1 OR d.expires_at_utc > $now)
            ORDER BY h.document_id ASC
            LIMIT $limitPlusOne;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue(
            "$after",
            afterDocumentId is null ? DBNull.Value : afterDocumentId);
        command.Parameters.AddWithValue(
            "$now",
            FormatUtc(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue(
            "$includeExpired",
            includeExpired ? 1 : 0);
        command.Parameters.AddWithValue("$limitPlusOne", limit + 1);

        var items = new List<CortexRetainedDocument>(limit + 1);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadDocument(reader));
        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        return new CortexRetentionHeadPage(
            Array.AsReadOnly(items.Select(item => item.Snapshot()).ToArray()),
            hasMore ? items[^1].DocumentId : null);
    }

    public async IAsyncEnumerable<CortexRetainedDocument> ReadRevisionRangeAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateLookup(projectId, kind, documentId);
        if (firstRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(firstRevision));
        if (lastRevision < firstRevision)
            throw new ArgumentOutOfRangeException(nameof(lastRevision));
        var revisionCount = lastRevision - firstRevision + 1;
        if (revisionCount > CortexAgentRetentionLimits.MaximumRevisionRangeDocuments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastRevision),
                "The retained revision range exceeds the hard streaming bound.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectDocumentSql + """
            WHERE d.project_id = $project
              AND d.kind = $kind
              AND d.document_id = $document
              AND d.revision BETWEEN $firstRevision AND $lastRevision
            ORDER BY d.revision ASC
            LIMIT $maximumDocuments;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$firstRevision", firstRevision);
        command.Parameters.AddWithValue("$lastRevision", lastRevision);
        command.Parameters.AddWithValue("$maximumDocuments", revisionCount);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadDocument(reader);
    }

    public async ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsAsync(
        string projectId,
        string text,
        int limit,
        CancellationToken cancellationToken)
        => await SearchHeadsCoreAsync(
            projectId,
            kind: null,
            text,
            limit,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsByKindAsync(
        string projectId,
        string kind,
        string text,
        int limit,
        CancellationToken cancellationToken)
        => await SearchHeadsCoreAsync(
            projectId,
            kind,
            text,
            limit,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsCoreAsync(
        string projectId,
        string? kind,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateLookup(projectId, kind ?? CortexRetainedDocumentKinds.TaskSnapshot, "search");
        if (string.IsNullOrWhiteSpace(text) || text.Length > CortexAgentRetentionLimits.MaximumSearchTextCharacters)
            throw new ArgumentException("A bounded search query is required.", nameof(text));
        if (limit is < 1 or > CortexAgentRetentionLimits.MaximumListPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var query = BuildFtsQuery(text);
        if (query.Length == 0)
            return [];

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.project_id, d.kind, d.document_id, d.revision,
                   d.content_hash, d.search_text, d.captured_at_utc, d.expires_at_utc
              FROM retained_document_fts f
              JOIN retained_head h
                ON h.project_id = f.project_id
               AND h.kind = f.kind
               AND h.document_id = f.document_id
               AND CAST(h.revision AS TEXT) = f.revision
              JOIN retained_document d
                ON d.project_id = h.project_id
               AND d.kind = h.kind
               AND d.document_id = h.document_id
               AND d.revision = h.revision
             WHERE retained_document_fts MATCH $query
               AND d.project_id = $project
               AND ($kind IS NULL OR d.kind = $kind)
               AND d.expires_at_utc > $now
             ORDER BY bm25(retained_document_fts), d.captured_at_utc DESC,
                      d.kind, d.document_id
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : kind);
        command.Parameters.AddWithValue("$now", FormatUtc(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<CortexRetentionSearchHit>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CortexRetentionSearchHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseUtc(reader.GetString(6)),
                ParseUtc(reader.GetString(7))));
        }
        return results.AsReadOnly();
    }


    public async ValueTask<CortexLocalStoreIntegrity> CheckIntegrityAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        long documentCount = 0;
        long auditCount = 0;
        try
        {
            await RequireSqliteIntegrityAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await RequireWalJournalAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var documents = await ReadAndValidateDocumentsAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            documentCount = documents.Count;
            ValidateRevisionChains(documents);
            await ValidateHeadsAsync(
                connection,
                transaction,
                documents,
                cancellationToken).ConfigureAwait(false);
            auditCount = await ValidateAuditAsync(
                connection,
                transaction,
                documents,
                cancellationToken).ConfigureAwait(false);
            await ValidateSearchIndexAsync(
                connection,
                transaction,
                documents,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CortexLocalStoreIntegrity(true, "ok", documentCount, auditCount);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or SqliteException or FormatException or ArgumentException)
        {
            return new CortexLocalStoreIntegrity(
                false,
                "semantic-integrity-failed",
                documentCount,
                auditCount);
        }
    }

    public async ValueTask RebuildSearchIndexAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM retained_document_fts;
                INSERT INTO retained_document_fts(project_id, kind, document_id, revision, content)
                SELECT project_id, kind, document_id, CAST(revision AS TEXT), search_text
                  FROM retained_document;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    internal static async ValueTask RequireSqliteIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.Transaction = transaction;
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("SQLite structural integrity failed.");
        }
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.Transaction = transaction;
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQLite foreign-key integrity failed.");
    }

    private static async ValueTask RequireWalJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var journal = connection.CreateCommand())
        {
            journal.Transaction = transaction;
            journal.CommandText = "PRAGMA journal_mode;";
            var mode = Convert.ToString(
                await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The local Cortex database is not using WAL journaling.");
        }
    }

    internal static async ValueTask<List<CortexRetainedDocument>> ReadAndValidateDocumentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectDocumentSql + """
            ORDER BY d.project_id, d.kind, d.document_id, d.revision;
            """;
        var documents = new List<CortexRetainedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            documents.Add(ReadDocument(reader));
        return documents;
    }

    internal static void ValidateRevisionChains(IReadOnlyList<CortexRetainedDocument> documents)
    {
        DocumentIdentityKey? previousKey = null;
        CortexRetainedDocument? previous = null;
        foreach (var document in documents)
        {
            var key = new DocumentIdentityKey(
                document.ProjectId,
                document.Kind,
                document.DocumentId);
            if (previousKey is null || previousKey.Value != key)
            {
                if (document.Revision != 1 || document.PreviousHash is not null)
                    throw new InvalidDataException("A retained revision chain does not begin at revision one.");
            }
            else if (previous is null ||
                     document.Revision != previous.Revision + 1 ||
                     !string.Equals(
                         document.PreviousHash,
                         previous.ContentHash,
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException("A retained revision chain is not contiguous.");
            }
            previousKey = key;
            previous = document;
        }
    }

    internal static async ValueTask ValidateHeadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CortexRetainedDocument> documents,
        CancellationToken cancellationToken)
    {
        var expected = documents
            .GroupBy(document => new DocumentIdentityKey(
                document.ProjectId,
                document.Kind,
                document.DocumentId))
            .ToDictionary(group => group.Key, group => group.Last());
        var observed = new HashSet<DocumentIdentityKey>();
        var taskHeadProjects = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, kind, document_id, revision, content_hash
              FROM retained_head;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new DocumentIdentityKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));
            if (!observed.Add(key) ||
                string.Equals(
                    key.Kind,
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    StringComparison.Ordinal) &&
                !taskHeadProjects.Add(key.ProjectId) ||
                !expected.TryGetValue(key, out var head) ||
                head.Revision != reader.GetInt64(3) ||
                !string.Equals(head.ContentHash, reader.GetString(4), StringComparison.Ordinal))
            {
                throw new InvalidDataException("A retained head does not identify the final exact revision.");
            }
        }
        if (observed.Count != expected.Count)
            throw new InvalidDataException("One or more retained revision chains have no exact head.");
    }

    internal static async ValueTask<long> ValidateAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CortexRetainedDocument> documents,
        CancellationToken cancellationToken)
    {
        var expected = documents.ToDictionary(
            document => new DocumentRevisionKey(
                document.ProjectId,
                document.Kind,
                document.DocumentId,
                document.Revision));
        var observed = new HashSet<DocumentRevisionKey>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT audit_id, project_id, kind, document_id, revision,
                   content_hash, producer_id, occurred_at_utc
              FROM retention_audit;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new DocumentRevisionKey(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4));
            if (!observed.Add(key) || !expected.TryGetValue(key, out var document))
                throw new InvalidDataException("The append-only audit contains an unknown or duplicate revision.");
            var expectedAuditId = CortexCanonicalJson.Sha256(
                $"{document.ProjectId}\n{document.Kind}\n{document.DocumentId}\n{document.Revision}\n{document.ContentHash}");
            if (!string.Equals(reader.GetString(0), expectedAuditId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(5), document.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), document.ProducerId, StringComparison.Ordinal) ||
                ParseUtc(reader.GetString(7)) != document.CapturedAt)
            {
                throw new InvalidDataException("The append-only audit does not match its exact retained revision.");
            }
        }
        if (observed.Count != expected.Count)
            throw new InvalidDataException("One or more retained revisions have no append-only audit event.");
        return observed.Count;
    }

    internal static async ValueTask ValidateSearchIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CortexRetainedDocument> documents,
        CancellationToken cancellationToken)
    {
        var expected = documents.ToDictionary(
            document => new DocumentRevisionKey(
                document.ProjectId,
                document.Kind,
                document.DocumentId,
                document.Revision));
        var observed = new HashSet<DocumentRevisionKey>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, kind, document_id, revision, content
              FROM retained_document_fts;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!long.TryParse(
                    reader.GetString(3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var revision))
            {
                throw new InvalidDataException("The lexical index contains an invalid revision.");
            }
            var key = new DocumentRevisionKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                revision);
            if (!observed.Add(key) ||
                !expected.TryGetValue(key, out var document) ||
                !string.Equals(reader.GetString(4), document.SearchText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The lexical index does not match its exact retained revision.");
            }
        }
        if (observed.Count != expected.Count)
            throw new InvalidDataException("One or more retained revisions are absent from the lexical index.");
    }

    public async ValueTask BackupAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var destination = SqliteLocalPathPolicy.ValidateNewBackupPath(destinationPath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(destination)!;
        if (Directory.Exists(parent))
        {
            SqliteLocalAclPolicy.RequireProtectedDirectory(parent);
        }
        else
        {
            Directory.CreateDirectory(parent);
            SqliteLocalAclPolicy.ProtectDirectory(parent);
        }
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(parent);
        var temporary = destination + ".partial";
        SqliteLocalPathPolicy.EnsureFileEntryIsSafe(
            temporary,
            "The Cortex backup staging file");
        SqliteLocalPathPolicy.EnsureSidecarsAbsent(
            temporary,
            "The Cortex backup staging file");
        if (File.Exists(temporary))
            throw new IOException("The local backup staging file already exists.");
        try
        {
            await using var source = CreateConnection();
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(source, cancellationToken).ConfigureAwait(false);
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await target.OpenAsync(cancellationToken).ConfigureAwait(false);
            SqliteLocalAclPolicy.ProtectFile(temporary);
            source.BackupDatabase(target);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var checkpoint = target.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var journal = target.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode = DELETE;";
                var mode = Convert.ToString(
                    await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The local Cortex backup could not be consolidated.");
            }
            await target.CloseAsync().ConfigureAwait(false);
            RequireNoBackupSidecars(temporary);
            File.Move(temporary, destination);
            SqliteLocalAclPolicy.ProtectFile(destination);
        }
        catch
        {
            DeleteBackupStagingFiles(temporary);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        // Connections are operation-scoped. Keep the managed gates alive until
        // this store is collected so an operation that passed the disposed
        // check before shutdown can safely release its gate. New operations
        // fail through ThrowIfDisposed.
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection(bool createIfMissing = false)
    {
        SqliteLocalPathPolicy.EnsureDatabaseEntryIsSafe(_options.DatabasePath);
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
    }

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
        await using var pageLimit = connection.CreateCommand();
        pageLimit.CommandText = $"PRAGMA max_page_count = {_options.MaximumDatabasePages};";
        var effectiveLimit = Convert.ToInt64(
            await pageLimit.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (effectiveLimit > _options.MaximumDatabasePages)
            throw new InvalidOperationException("The local Cortex database already exceeds its configured page quota.");
    }

    private static async ValueTask EnsureWalJournalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The local Cortex database could not enable WAL journaling.");
    }

    private static void RequireNoBackupSidecars(string databasePath)
    {
        if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
            throw new IOException("The local Cortex backup retained live SQLite sidecars.");
    }

    private static void DeleteBackupStagingFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            SqliteLocalPathPolicy.EnsureFileEntryIsSafe(path, "A Cortex backup staging file");
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void PrepareStoreDirectory()
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath)!;
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(directory);
        SqliteLocalPathPolicy.EnsureDatabaseEntryIsSafe(_options.DatabasePath);
        Directory.CreateDirectory(directory);
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(directory);
        SqliteLocalPathPolicy.EnsureDatabaseEntryIsSafe(_options.DatabasePath);
        SqliteLocalAclPolicy.ProtectDirectory(directory);
    }

    private void ProtectStoreFile()
    {
        if (!File.Exists(_options.DatabasePath))
            throw new IOException("The SQLite database was not created.");
        var info = new FileInfo(_options.DatabasePath);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The SQLite database cannot be a link or reparse point.");
        SqliteLocalAclPolicy.ProtectFile(_options.DatabasePath);
    }

    private static bool MatchesExpectedHead(
        CortexRetentionAppendRequest request,
        CortexRetainedDocument? head) =>
        request.Revision == 1
            ? head is null && request.ExpectedPreviousHash is null
            : head is not null &&
               request.Revision == head.Revision + 1 &&
               string.Equals(request.ExpectedPreviousHash, head.ContentHash, StringComparison.Ordinal);

    private static bool IsExactReplay(
        CortexRetainedDocument retained,
        CortexRetentionAppendRequest requested) =>
        string.Equals(retained.ProjectId, requested.ProjectId, StringComparison.Ordinal) &&
        string.Equals(retained.Kind, requested.Kind, StringComparison.Ordinal) &&
        string.Equals(retained.DocumentId, requested.DocumentId, StringComparison.Ordinal) &&
        retained.Revision == requested.Revision &&
        string.Equals(retained.PreviousHash, requested.ExpectedPreviousHash, StringComparison.Ordinal) &&
        string.Equals(retained.IdempotencyKey, requested.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(retained.ProducerId, requested.ProducerId, StringComparison.Ordinal) &&
        string.Equals(retained.ContractId, requested.ContractId, StringComparison.Ordinal) &&
        string.Equals(retained.ContentHash, requested.ContentHash, StringComparison.Ordinal) &&
        retained.CanonicalUtf8.AsSpan().SequenceEqual(requested.CanonicalUtf8) &&
        string.Equals(retained.SearchText, requested.SearchText, StringComparison.Ordinal) &&
        retained.CapturedAt == requested.CapturedAt &&
        retained.ExpiresAt == requested.ExpiresAt;

    private static async ValueTask<CortexRetainedDocument?> FindByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectDocumentSql + """
            WHERE d.project_id = $project
              AND d.kind = $kind
              AND d.producer_id = $producer
              AND d.idempotency_key = $idempotency;
            """;
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$producer", request.ProducerId);
        command.Parameters.AddWithValue("$idempotency", request.IdempotencyKey);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CortexRetainedDocument?> FindHeadCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectDocumentSql + """
            JOIN retained_head h
              ON h.project_id = d.project_id
             AND h.kind = d.kind
             AND h.document_id = d.document_id
             AND h.revision = d.revision
            WHERE h.project_id = $project
              AND h.kind = $kind
              AND h.document_id = $document;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$document", documentId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CortexRetainedDocument?>
        FindConflictingTaskSnapshotHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectDocumentSql + """
            JOIN retained_head h
              ON h.project_id = d.project_id
             AND h.kind = d.kind
             AND h.document_id = d.document_id
             AND h.revision = d.revision
            WHERE h.project_id = $project
              AND h.kind = $kind
              AND h.document_id <> $document
            ORDER BY h.document_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue(
            "$kind",
            CortexRetainedDocumentKinds.TaskSnapshot);
        command.Parameters.AddWithValue("$document", documentId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureTaskSnapshotHeadIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var ambiguous = connection.CreateCommand())
        {
            ambiguous.Transaction = transaction;
            ambiguous.CommandText = """
                SELECT project_id
                  FROM retained_head
                 WHERE kind = 'task-snapshot'
                 GROUP BY project_id
                HAVING COUNT(*) > 1
                 LIMIT 1;
                """;
            if (await ambiguous.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                throw new InvalidDataException(
                    "The local Cortex store contains multiple task-set heads for one project.");
            }
        }

        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS
                ux_retained_head_single_task_snapshot
            ON retained_head(project_id)
            WHERE kind = 'task-snapshot';
            """;
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retained_document(
                project_id, kind, document_id, revision, previous_hash,
                idempotency_key, producer_id, contract_id, content_hash,
                canonical_json, search_text, captured_at_utc, expires_at_utc)
            VALUES(
                $project, $kind, $document, $revision, $previous,
                $idempotency, $producer, $contract, $hash,
                $json, $search, $captured, $expires);
            """;
        AddDocumentParameters(command, request);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpsertHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retained_head(project_id, kind, document_id, revision, content_hash)
            VALUES($project, $kind, $document, $revision, $hash)
            ON CONFLICT(project_id, kind, document_id)
            DO UPDATE SET revision = excluded.revision, content_hash = excluded.content_hash;
            """;
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$document", request.DocumentId);
        command.Parameters.AddWithValue("$revision", request.Revision);
        command.Parameters.AddWithValue("$hash", request.ContentHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask IndexDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retained_document_fts(project_id, kind, document_id, revision, content)
            VALUES($project, $kind, $document, $revision, $content);
            """;
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$document", request.DocumentId);
        command.Parameters.AddWithValue("$revision", request.Revision.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$content", request.SearchText);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AppendAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        var auditId = CortexCanonicalJson.Sha256(
            $"{request.ProjectId}\n{request.Kind}\n{request.DocumentId}\n{request.Revision}\n{request.ContentHash}");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retention_audit(
                audit_id, project_id, kind, document_id, revision,
                content_hash, producer_id, occurred_at_utc)
            VALUES($audit, $project, $kind, $document, $revision, $hash, $producer, $occurred);
            """;
        command.Parameters.AddWithValue("$audit", auditId);
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$document", request.DocumentId);
        command.Parameters.AddWithValue("$revision", request.Revision);
        command.Parameters.AddWithValue("$hash", request.ContentHash);
        command.Parameters.AddWithValue("$producer", request.ProducerId);
        command.Parameters.AddWithValue("$occurred", FormatUtc(request.CapturedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureQuotaAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM retained_document),
                (SELECT COUNT(*) FROM retained_document WHERE project_id = $project);
            """;
        command.Parameters.AddWithValue("$project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The local Cortex quota counters are unavailable.");
        var total = reader.GetInt64(0);
        var project = reader.GetInt64(1);
        if (total >= _options.MaximumRetainedDocuments)
            throw new CortexRetentionQuotaExceededException("The local Cortex store document quota is exhausted.");
        if (project >= _options.MaximumProjectDocuments)
            throw new CortexRetentionQuotaExceededException("The local Cortex project document quota is exhausted.");
    }

    private static void AddDocumentParameters(
        SqliteCommand command,
        CortexRetentionAppendRequest request)
    {
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$document", request.DocumentId);
        command.Parameters.AddWithValue("$revision", request.Revision);
        command.Parameters.AddWithValue("$previous", (object?)request.ExpectedPreviousHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$idempotency", request.IdempotencyKey);
        command.Parameters.AddWithValue("$producer", request.ProducerId);
        command.Parameters.AddWithValue("$contract", request.ContractId);
        command.Parameters.AddWithValue("$hash", request.ContentHash);
        command.Parameters.Add("$json", SqliteType.Blob).Value = request.CanonicalUtf8;
        command.Parameters.AddWithValue("$search", request.SearchText);
        command.Parameters.AddWithValue("$captured", FormatUtc(request.CapturedAt));
        command.Parameters.AddWithValue("$expires", FormatUtc(request.ExpiresAt));
    }

    private static async ValueTask<CortexRetainedDocument?> ReadSingleAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDocument(reader)
            : null;
    }

    private static CortexRetainedDocument ReadDocument(SqliteDataReader reader)
    {
        try
        {
            var document = new CortexRetainedDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                (byte[])reader[8],
                reader.GetString(9),
                reader.GetString(10),
                ParseUtc(reader.GetString(11)),
                ParseUtc(reader.GetString(12)));
            _ = new CortexRetentionAppendRequest(
                document.ProjectId,
                document.Kind,
                document.DocumentId,
                document.Revision,
                document.PreviousHash,
                document.IdempotencyKey,
                document.ProducerId,
                document.ContractId,
                document.CanonicalUtf8,
                document.ContentHash,
                document.SearchText,
                document.CapturedAt,
                document.ExpiresAt).Validate();
            return document;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException(
                "A retained document failed its canonical integrity boundary.",
                exception);
        }
    }

    private static CortexRetainedDocument ToDocument(CortexRetentionAppendRequest request) => new(
        request.ProjectId,
        request.Kind,
        request.DocumentId,
        request.Revision,
        request.ExpectedPreviousHash,
        request.IdempotencyKey,
        request.ProducerId,
        request.ContractId,
        request.CanonicalUtf8.ToArray(),
        request.ContentHash,
        request.SearchText,
        request.CapturedAt,
        request.ExpiresAt);

    private static string BuildFtsQuery(string text)
    {
        var tokens = new List<string>(16);
        var current = new List<char>(32);
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            {
                if (current.Count < 64)
                    current.Add(char.ToLowerInvariant(character));
                continue;
            }
            Flush();
            if (tokens.Count == 16)
                break;
        }
        Flush();
        return string.Join(" AND ", tokens.Select(token => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

        void Flush()
        {
            if (current.Count == 0 || tokens.Count == 16)
                return;
            tokens.Add(new string(current.ToArray()));
            current.Clear();
        }
    }

    private static void ValidateLookup(string projectId, string kind, string documentId)
    {
        static bool Valid(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length <= CortexAgentRetentionLimits.MaximumIdentifierCharacters &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.Skip(1).All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
        if (!Valid(projectId) || !Valid(kind) || !Valid(documentId))
            throw new ArgumentException("A bounded project, kind, and document identity is required.");
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private readonly record struct DocumentIdentityKey(
        string ProjectId,
        string Kind,
        string DocumentId);

    private readonly record struct DocumentRevisionKey(
        string ProjectId,
        string Kind,
        string DocumentId,
        long Revision);

    private const string SelectDocumentSql = """
        SELECT d.project_id, d.kind, d.document_id, d.revision, d.previous_hash,
               d.idempotency_key, d.producer_id, d.contract_id, d.canonical_json,
               d.content_hash, d.search_text, d.captured_at_utc, d.expires_at_utc
          FROM retained_document d
        """ + "\n";

    private const string SchemaSql = """
        PRAGMA page_size = 4096;
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS cortex_schema(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            version INTEGER NOT NULL CHECK(version > 0)
        ) STRICT;
        INSERT OR IGNORE INTO cortex_schema(singleton, version) VALUES(1, 1);

        CREATE TABLE IF NOT EXISTS retained_document(
            project_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            document_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision > 0),
            previous_hash TEXT NULL,
            idempotency_key TEXT NOT NULL,
            producer_id TEXT NOT NULL,
            contract_id TEXT NOT NULL,
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            canonical_json BLOB NOT NULL CHECK(length(canonical_json) BETWEEN 2 AND 32768),
            search_text TEXT NOT NULL CHECK(length(search_text) <= 4096),
            captured_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL,
            PRIMARY KEY(project_id, kind, document_id, revision),
            UNIQUE(project_id, kind, producer_id, idempotency_key),
            CHECK(
                (revision = 1 AND previous_hash IS NULL) OR
                (revision > 1 AND length(previous_hash) = 64))
        ) STRICT;

        CREATE TABLE IF NOT EXISTS retained_head(
            project_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            document_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            PRIMARY KEY(project_id, kind, document_id),
            FOREIGN KEY(project_id, kind, document_id, revision)
                REFERENCES retained_document(project_id, kind, document_id, revision)
                ON DELETE RESTRICT
        ) STRICT;

        CREATE UNIQUE INDEX ux_retained_head_single_task_snapshot
            ON retained_head(project_id)
            WHERE kind = 'task-snapshot';

        CREATE TABLE IF NOT EXISTS retention_audit(
            audit_id TEXT PRIMARY KEY CHECK(length(audit_id) = 64),
            project_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            document_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            producer_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            FOREIGN KEY(project_id, kind, document_id, revision)
                REFERENCES retained_document(project_id, kind, document_id, revision)
                ON DELETE RESTRICT
        ) STRICT;

        CREATE VIRTUAL TABLE IF NOT EXISTS retained_document_fts USING fts5(
            project_id UNINDEXED,
            kind UNINDEXED,
            document_id UNINDEXED,
            revision UNINDEXED,
            content,
            tokenize = 'unicode61'
        );
        """;
}
