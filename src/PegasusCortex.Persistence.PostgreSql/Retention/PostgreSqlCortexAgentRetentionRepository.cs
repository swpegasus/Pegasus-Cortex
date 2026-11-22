using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Retention;

/// <summary>
/// Scoped PostgreSQL provider for passive agent-retention records. The adapter
/// derives heads from immutable revisions and verifies the authenticated
/// canonical row payload every time a record crosses the storage boundary.
/// </summary>
public sealed class PostgreSqlCortexAgentRetentionRepository :
    ICortexAgentRetentionRepository
{
    private const int RevisionPageSize = 256;
    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly PostgreSqlStoredRowCodec _codec;
    private readonly PostgreSqlCortexRetentionScope _scope;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlCortexAgentRetentionRepository(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator authenticator,
        PostgreSqlCortexRetentionScope scope,
        TimeProvider? timeProvider = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _codec = new PostgreSqlStoredRowCodec(
            authenticator ?? throw new ArgumentNullException(nameof(authenticator)));
        _scope = (scope ?? throw new ArgumentNullException(nameof(scope))).Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PostgreSqlCortexRetentionScope Scope => _scope with { };

    public ValueTask<CortexRetentionAppendResult> AppendAsync(
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        if (_scope.Capability != PostgreSqlCortexRetentionCapability.Write)
            throw new UnauthorizedAccessException("The PostgreSQL retention scope is read-only.");
        var captured = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromRetention(
                _scope,
                "cortex.retention.append"),
            IsolationLevel.Serializable,
            async (connection, transaction, token) =>
            {
                await AcquireChainLockAsync(
                    connection,
                    transaction,
                    captured,
                    token).ConfigureAwait(false);

                var replay = await FindByIdempotencyAsync(
                    connection,
                    transaction,
                    captured,
                    token).ConfigureAwait(false);
                if (replay is not null)
                {
                    var replayHead = await FindHeadCoreAsync(
                        connection,
                        transaction,
                        captured.ProjectId,
                        captured.Kind,
                        replay.DocumentId,
                        token).ConfigureAwait(false);
                    return ExactReplay(replay, captured)
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
                    var conflicting = await FindConflictingTaskSnapshotHeadAsync(
                        connection,
                        transaction,
                        captured.ProjectId,
                        captured.DocumentId,
                        token).ConfigureAwait(false);
                    if (conflicting is not null)
                    {
                        return new CortexRetentionAppendResult(
                            CortexRetentionWriteDispositions.Conflict,
                            null,
                            conflicting.ContentHash);
                    }
                }

                var head = await FindHeadCoreAsync(
                    connection,
                    transaction,
                    captured.ProjectId,
                    captured.Kind,
                    captured.DocumentId,
                    token).ConfigureAwait(false);
                if (!MatchesExpectedHead(captured, head))
                {
                    return new CortexRetentionAppendResult(
                        CortexRetentionWriteDispositions.StaleBase,
                        head?.Snapshot(),
                        head?.ContentHash);
                }

                await EnsureChainAsync(
                    connection,
                    transaction,
                    captured,
                    token).ConfigureAwait(false);
                var document = ToDocument(captured);
                await InsertDocumentAsync(
                    connection,
                    transaction,
                    document,
                    token).ConfigureAwait(false);
                await InsertAuditAsync(
                    connection,
                    transaction,
                    captured,
                    token).ConfigureAwait(false);

                var stored = await FindRevisionCoreAsync(
                    connection,
                    transaction,
                    captured.ProjectId,
                    captured.Kind,
                    captured.DocumentId,
                    captured.Revision,
                    token).ConfigureAwait(false) ??
                    throw new InvalidDataException(
                        "The appended PostgreSQL retention revision could not be read back.");
                if (!SameDocument(stored, document))
                    throw new InvalidDataException(
                        "The appended PostgreSQL retention revision did not round-trip exactly.");
                return new CortexRetentionAppendResult(
                    CortexRetentionWriteDispositions.Appended,
                    stored.Snapshot(),
                    stored.ContentHash);
            },
            cancellationToken);
    }

    public ValueTask<CortexRetainedDocument?> FindHeadAsync(
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        ValidateLookup(projectId, kind, documentId);
        return ExecuteReadAsync(
            "cortex.retention.head.read",
            (connection, transaction, token) => FindHeadCoreAsync(
                connection,
                transaction,
                projectId,
                kind,
                documentId,
                token),
            cancellationToken);
    }

    public ValueTask<CortexRetainedDocument?> FindRevisionAsync(
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken)
    {
        ValidateLookup(projectId, kind, documentId);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        return ExecuteReadAsync(
            "cortex.retention.revision.read",
            (connection, transaction, token) => FindRevisionCoreAsync(
                connection,
                transaction,
                projectId,
                kind,
                documentId,
                revision,
                token),
            cancellationToken);
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
            cancellationToken);

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
            cancellationToken);

    public async IAsyncEnumerable<CortexRetainedDocument> ReadRevisionRangeAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateLookup(projectId, kind, documentId);
        if (firstRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(firstRevision));
        if (lastRevision < firstRevision)
            throw new ArgumentOutOfRangeException(nameof(lastRevision));
        if (lastRevision - firstRevision + 1 >
            CortexAgentRetentionLimits.MaximumRevisionRangeDocuments)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision));
        }

        var next = firstRevision;
        while (next <= lastRevision)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageLast = Math.Min(lastRevision, next + RevisionPageSize - 1);
            var expectedCount = checked((int)(pageLast - next + 1));
            var page = await ReadRevisionPageAsync(
                projectId,
                kind,
                documentId,
                next,
                pageLast,
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                yield break;
            foreach (var document in page)
            {
                yield return document.Snapshot();
                next = checked(document.Revision + 1);
            }
            if (page.Count < expectedCount)
                yield break;
        }
    }

    public ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsAsync(
        string projectId,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLookup(projectId, CortexRetainedDocumentKinds.TaskSnapshot, "search");
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > CortexAgentRetentionLimits.MaximumSearchTextCharacters)
        {
            throw new ArgumentException("A bounded search query is required.", nameof(text));
        }
        if (limit is < 1 or > CortexAgentRetentionLimits.MaximumListPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var query = BuildSearchQuery(text);
        if (query.Length == 0)
            return ValueTask.FromResult<IReadOnlyList<CortexRetentionSearchHit>>([]);

        return ExecuteReadAsync<IReadOnlyList<CortexRetentionSearchHit>>(
            "cortex.retention.search",
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    SearchHeadsSql,
                    Text("project_id", projectId),
                    Integer(
                        "candidate_limit",
                        CortexAgentRetentionLimits.MaximumRevisionRangeDocuments + 1));
                var authenticatedHeads = await ReadDocumentsAsync(
                        command,
                        CortexAgentRetentionLimits.MaximumRevisionRangeDocuments,
                        token)
                    .ConfigureAwait(false);
                return SearchAuthenticatedHeads(
                    authenticatedHeads,
                    query,
                    _timeProvider.GetUtcNow(),
                    limit);
            },
            cancellationToken);
    }

    private ValueTask<T> ExecuteReadAsync<T>(
        string operation,
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromRetention(_scope, operation),
            IsolationLevel.ReadCommitted,
            action,
            cancellationToken);

    private ValueTask<CortexRetentionHeadPage> ListHeadsByKindCoreAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        bool includeExpired,
        CancellationToken cancellationToken)
    {
        ValidateLookup(projectId, kind, afterDocumentId ?? "page");
        if (limit is < 1 or > CortexAgentRetentionLimits.MaximumKindHeadPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return ExecuteReadAsync(
            "cortex.retention.heads.list",
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    SelectDocumentSql + ListHeadsSuffixSql,
                    Text("project_id", projectId),
                    Text("kind", kind),
                    Integer(
                        "candidate_limit",
                        CortexAgentRetentionLimits.MaximumRevisionRangeDocuments + 1));
                var authenticatedHeads = await ReadDocumentsAsync(
                        command,
                        CortexAgentRetentionLimits.MaximumRevisionRangeDocuments,
                        token)
                    .ConfigureAwait(false);
                return ProjectAuthenticatedHeadPage(
                    authenticatedHeads,
                    afterDocumentId,
                    limit,
                    includeExpired,
                    _timeProvider.GetUtcNow());
            },
            cancellationToken);
    }

    private ValueTask<IReadOnlyList<CortexRetainedDocument>> ReadRevisionPageAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        CancellationToken cancellationToken) =>
        ExecuteReadAsync<IReadOnlyList<CortexRetainedDocument>>(
            "cortex.retention.revisions.read",
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    SelectDocumentSql + ReadRevisionRangeSuffixSql,
                    Text("project_id", projectId),
                    Text("kind", kind),
                    Text("document_id", documentId),
                    BigInt("first_revision", firstRevision),
                    BigInt("last_revision", lastRevision),
                    Integer("limit", RevisionPageSize));
                return (await ReadDocumentsAsync(command, RevisionPageSize, token)
                    .ConfigureAwait(false)).AsReadOnly();
            },
            cancellationToken);

    private async ValueTask AcquireChainLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            AcquireChainLockSql,
            Text(
                "lock_key",
                $"{_scope.TenantId}\n{_scope.Namespace}\n{request.ProjectId}\n{request.Kind}"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureChainAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await ReadChainAsync(
            connection,
            transaction,
            request.ProjectId,
            request.Kind,
            request.DocumentId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return;
        if (request.Revision != 1)
            throw new InvalidDataException("The retained PostgreSQL chain anchor is missing.");

        var chain = new PostgreSqlRetentionChainRecord(
            request.ProjectId,
            request.Kind,
            request.DocumentId,
            request.CapturedAt);
        var primaryId = PostgreSqlRetentionRecordIdentity.ChainPrimaryId(
            request.ProjectId,
            request.Kind,
            request.DocumentId);
        var stored = _codec.Encode(
            PostgreSqlRetentionRecordIdentity.ChainEntityKind,
            _scope.TenantId,
            _scope.Namespace,
            primaryId,
            revisionId: null,
            chain);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertChainSql,
            Text("tenant_id", _scope.TenantId),
            Text("namespace_name", _scope.Namespace),
            Text("project_id", request.ProjectId),
            Text("kind", request.Kind),
            Text("document_id", request.DocumentId),
            Timestamp("created_at", request.CapturedAt),
            Binary("payload", stored.Payload),
            Text("payload_sha256", stored.PayloadSha256),
            Integer("auth_version", stored.Seal.Version),
            Text("auth_algorithm", stored.Seal.Algorithm),
            Text("auth_key_id", stored.Seal.KeyId),
            Binary("auth_signature", stored.Seal.GetSignature()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The retained PostgreSQL chain insert count was invalid.");
    }

    private async ValueTask<PostgreSqlRetentionChainRecord?> ReadChainAsync(
        DbConnection connection,
        DbTransaction transaction,
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            SelectChainSql,
            Text("project_id", projectId),
            Text("kind", kind),
            Text("document_id", documentId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var projectedProjectId = reader.GetString(0);
        var projectedKind = reader.GetString(1);
        var projectedDocumentId = reader.GetString(2);
        var projectedCreatedAt = reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime();
        var primaryId = PostgreSqlRetentionRecordIdentity.ChainPrimaryId(
            projectedProjectId,
            projectedKind,
            projectedDocumentId);
        var chain = _codec.Decode<PostgreSqlRetentionChainRecord>(
            PostgreSqlRetentionRecordIdentity.ChainEntityKind,
            _scope.TenantId,
            _scope.Namespace,
            primaryId,
            revisionId: null,
            reader.GetFieldValue<byte[]>(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<byte[]>(9));
        if (!string.Equals(chain.ProjectId, projectedProjectId, StringComparison.Ordinal) ||
            !string.Equals(chain.Kind, projectedKind, StringComparison.Ordinal) ||
            !string.Equals(chain.DocumentId, projectedDocumentId, StringComparison.Ordinal) ||
            chain.CreatedAt != projectedCreatedAt ||
            !string.Equals(chain.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(chain.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(chain.DocumentId, documentId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The retained PostgreSQL chain identity did not match its row.");
        }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The retained PostgreSQL chain identity is duplicated.");
        return chain;
    }

    private async ValueTask InsertDocumentAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexRetainedDocument document,
        CancellationToken cancellationToken)
    {
        var primaryId = PostgreSqlRetentionRecordIdentity.ChainPrimaryId(
            document.ProjectId,
            document.Kind,
            document.DocumentId);
        var revisionId = PostgreSqlRetentionRecordIdentity.DocumentRevisionId(document.Revision);
        var stored = _codec.Encode(
            PostgreSqlRetentionRecordIdentity.DocumentEntityKind,
            _scope.TenantId,
            _scope.Namespace,
            primaryId,
            revisionId,
            document);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertDocumentSql,
            Text("tenant_id", _scope.TenantId),
            Text("namespace_name", _scope.Namespace),
            Text("project_id", document.ProjectId),
            Text("kind", document.Kind),
            Text("document_id", document.DocumentId),
            BigInt("revision", document.Revision),
            Text("previous_hash", document.PreviousHash),
            Text("idempotency_key", document.IdempotencyKey),
            Text("producer_id", document.ProducerId),
            Text("contract_id", document.ContractId),
            Text("content_hash", document.ContentHash),
            Text("search_text", document.SearchText),
            Timestamp("captured_at", document.CapturedAt),
            Timestamp("expires_at", document.ExpiresAt),
            Binary("payload", stored.Payload),
            Text("payload_sha256", stored.PayloadSha256),
            Integer("auth_version", stored.Seal.Version),
            Text("auth_algorithm", stored.Seal.Algorithm),
            Text("auth_key_id", stored.Seal.KeyId),
            Binary("auth_signature", stored.Seal.GetSignature()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The retained PostgreSQL document insert count was invalid.");
    }

    private async ValueTask InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken)
    {
        var auditId = PostgreSqlRetentionRecordIdentity.AuditId(request);
        var audit = new PostgreSqlRetentionAuditRecord(
            auditId,
            request.ProjectId,
            request.Kind,
            request.DocumentId,
            request.Revision,
            request.ContentHash,
            request.ProducerId,
            request.CapturedAt);
        var stored = _codec.Encode(
            PostgreSqlRetentionRecordIdentity.AuditEntityKind,
            _scope.TenantId,
            _scope.Namespace,
            auditId,
            revisionId: null,
            audit);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertAuditSql,
            Text("tenant_id", _scope.TenantId),
            Text("namespace_name", _scope.Namespace),
            Text("audit_id", auditId),
            Text("project_id", request.ProjectId),
            Text("kind", request.Kind),
            Text("document_id", request.DocumentId),
            BigInt("revision", request.Revision),
            Text("content_hash", request.ContentHash),
            Text("producer_id", request.ProducerId),
            Timestamp("occurred_at", request.CapturedAt),
            Binary("payload", stored.Payload),
            Text("payload_sha256", stored.PayloadSha256),
            Integer("auth_version", stored.Seal.Version),
            Text("auth_algorithm", stored.Seal.Algorithm),
            Text("auth_key_id", stored.Seal.KeyId),
            Binary("auth_signature", stored.Seal.GetSignature()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The retained PostgreSQL audit insert count was invalid.");
    }

    private ValueTask<CortexRetainedDocument?> FindByIdempotencyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken) =>
        ReadSingleAsync(
            connection,
            transaction,
            SelectDocumentSql + FindByIdempotencySuffixSql,
            cancellationToken,
            Text("project_id", request.ProjectId),
            Text("kind", request.Kind),
            Text("producer_id", request.ProducerId),
            Text("idempotency_key", request.IdempotencyKey));

    private ValueTask<CortexRetainedDocument?> FindHeadCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken) =>
        ReadSingleAsync(
            connection,
            transaction,
            SelectDocumentSql + FindHeadSuffixSql,
            cancellationToken,
            Text("project_id", projectId),
            Text("kind", kind),
            Text("document_id", documentId));

    private ValueTask<CortexRetainedDocument?> FindRevisionCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken) =>
        ReadSingleAsync(
            connection,
            transaction,
            SelectDocumentSql + FindRevisionSuffixSql,
            cancellationToken,
            Text("project_id", projectId),
            Text("kind", kind),
            Text("document_id", documentId),
            BigInt("revision", revision));

    private ValueTask<CortexRetainedDocument?> FindConflictingTaskSnapshotHeadAsync(
        DbConnection connection,
        DbTransaction transaction,
        string projectId,
        string documentId,
        CancellationToken cancellationToken) =>
        ReadSingleAsync(
            connection,
            transaction,
            SelectDocumentSql + FindConflictingTaskHeadSuffixSql,
            cancellationToken,
            Text("project_id", projectId),
            Text("kind", CortexRetainedDocumentKinds.TaskSnapshot),
            Text("document_id", documentId));

    private async ValueTask<CortexRetainedDocument?> ReadSingleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params PostgreSqlParameter[] parameters)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            sql,
            parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var document = ReadDocument(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("A unique PostgreSQL retention lookup returned multiple rows.");
        return document;
    }

    private async ValueTask<List<CortexRetainedDocument>> ReadDocumentsAsync(
        DbCommand command,
        int maximum,
        CancellationToken cancellationToken)
    {
        var values = new List<CortexRetainedDocument>(maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (values.Count == maximum)
                throw new InvalidDataException("A PostgreSQL retention read exceeded its bounded result.");
            values.Add(ReadDocument(reader));
        }
        return values;
    }

    private CortexRetainedDocument ReadDocument(DbDataReader reader)
    {
        var projected = new PostgreSqlRetentionProjectedDocument(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime(),
            reader.GetFieldValue<DateTimeOffset>(11).ToUniversalTime(),
            reader.GetFieldValue<byte[]>(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetFieldValue<byte[]>(17));
        return AuthenticateProjectedDocument(projected);
    }

    internal CortexRetainedDocument AuthenticateProjectedDocument(
        PostgreSqlRetentionProjectedDocument projected)
    {
        ArgumentNullException.ThrowIfNull(projected);
        var primaryId = PostgreSqlRetentionRecordIdentity.ChainPrimaryId(
            projected.ProjectId,
            projected.Kind,
            projected.DocumentId);
        var revisionId = PostgreSqlRetentionRecordIdentity.DocumentRevisionId(
            projected.Revision);
        var document = _codec.Decode<CortexRetainedDocument>(
            PostgreSqlRetentionRecordIdentity.DocumentEntityKind,
            _scope.TenantId,
            _scope.Namespace,
            primaryId,
            revisionId,
            projected.Payload,
            projected.PayloadSha256,
            projected.AuthVersion,
            projected.AuthAlgorithm,
            projected.AuthKeyId,
            projected.AuthSignature);
        try
        {
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
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A PostgreSQL retained document failed its canonical contract boundary.",
                exception);
        }
        if (!string.Equals(document.ProjectId, projected.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(document.Kind, projected.Kind, StringComparison.Ordinal) ||
            !string.Equals(document.DocumentId, projected.DocumentId, StringComparison.Ordinal) ||
            document.Revision != projected.Revision ||
            !string.Equals(document.PreviousHash, projected.PreviousHash, StringComparison.Ordinal) ||
            !string.Equals(document.IdempotencyKey, projected.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(document.ProducerId, projected.ProducerId, StringComparison.Ordinal) ||
            !string.Equals(document.ContractId, projected.ContractId, StringComparison.Ordinal) ||
            !string.Equals(document.ContentHash, projected.ContentHash, StringComparison.Ordinal) ||
            !string.Equals(document.SearchText, projected.SearchText, StringComparison.Ordinal) ||
            document.CapturedAt != projected.CapturedAt ||
            document.ExpiresAt != projected.ExpiresAt)
        {
            throw new InvalidDataException(
                "A PostgreSQL retained document does not match its authenticated projection.");
        }
        return document.Snapshot();
    }

    private static bool MatchesExpectedHead(
        CortexRetentionAppendRequest request,
        CortexRetainedDocument? head) =>
        head is null
            ? request.Revision == 1 && request.ExpectedPreviousHash is null
            : request.Revision == head.Revision + 1 &&
              string.Equals(
                  request.ExpectedPreviousHash,
                  head.ContentHash,
                  StringComparison.Ordinal);

    private static bool ExactReplay(
        CortexRetainedDocument stored,
        CortexRetentionAppendRequest request) =>
        SameDocument(stored, ToDocument(request));

    private static bool SameDocument(
        CortexRetainedDocument left,
        CortexRetainedDocument right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.DocumentId, right.DocumentId, StringComparison.Ordinal) &&
        left.Revision == right.Revision &&
        string.Equals(left.PreviousHash, right.PreviousHash, StringComparison.Ordinal) &&
        string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(left.ProducerId, right.ProducerId, StringComparison.Ordinal) &&
        string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
        left.CanonicalUtf8.AsSpan().SequenceEqual(right.CanonicalUtf8) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal) &&
        string.Equals(left.SearchText, right.SearchText, StringComparison.Ordinal) &&
        left.CapturedAt == right.CapturedAt &&
        left.ExpiresAt == right.ExpiresAt;

    private static CortexRetainedDocument ToDocument(CortexRetentionAppendRequest request) =>
        new(
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

    private static string BuildSearchQuery(string text)
    {
        return string.Join(' ', Tokenize(text, maximumTokens: 16));
    }

    /// <summary>
    /// Applies visibility, lexical matching, and ordering only after every row
    /// has crossed the authenticated payload/projection boundary.
    /// </summary>
    internal static IReadOnlyList<CortexRetentionSearchHit> SearchAuthenticatedHeads(
        IEnumerable<CortexRetainedDocument> authenticatedHeads,
        string normalizedQuery,
        DateTimeOffset observedAt,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(authenticatedHeads);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedQuery);
        if (observedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC search observation time is required.", nameof(observedAt));
        if (limit is < 1 or > CortexAgentRetentionLimits.MaximumListPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var queryTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryTokens.Length == 0)
            return [];

        var candidates = DeriveAuthenticatedHeads(authenticatedHeads)
            .Where(document => document.ExpiresAt > observedAt)
            .Select(document => new
            {
                Document = document,
                Score = SearchScore(document.SearchText, queryTokens)
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Document.CapturedAt)
            .ThenBy(candidate => candidate.Document.Kind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Document.DocumentId, StringComparer.Ordinal)
            .Take(limit)
            .Select(candidate => new CortexRetentionSearchHit(
                candidate.Document.ProjectId,
                candidate.Document.Kind,
                candidate.Document.DocumentId,
                candidate.Document.Revision,
                candidate.Document.ContentHash,
                candidate.Document.SearchText,
                candidate.Document.CapturedAt,
                candidate.Document.ExpiresAt))
            .ToArray();
        return Array.AsReadOnly(candidates);
    }

    internal static CortexRetentionHeadPage ProjectAuthenticatedHeadPage(
        IEnumerable<CortexRetainedDocument> authenticatedHeads,
        string? afterDocumentId,
        int limit,
        bool includeExpired,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(authenticatedHeads);
        if (observedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC head observation time is required.", nameof(observedAt));
        if (limit is < 1 or > CortexAgentRetentionLimits.MaximumKindHeadPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var values = DeriveAuthenticatedHeads(authenticatedHeads)
            .Where(document =>
                (afterDocumentId is null ||
                 string.CompareOrdinal(document.DocumentId, afterDocumentId) > 0) &&
                (includeExpired || document.ExpiresAt > observedAt))
            .OrderBy(document => document.DocumentId, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToArray();
        var hasMore = values.Length > limit;
        var items = values
            .Take(limit)
            .Select(document => document.Snapshot())
            .ToArray();
        return new CortexRetentionHeadPage(
            Array.AsReadOnly(items),
            hasMore ? items[^1].DocumentId : null);
    }

    private static IReadOnlyList<CortexRetainedDocument> DeriveAuthenticatedHeads(
        IEnumerable<CortexRetainedDocument> documents)
    {
        var heads = new Dictionary<AuthenticatedHeadKey, CortexRetainedDocument>();
        foreach (var source in documents)
        {
            var document = source?.Snapshot() ??
                throw new InvalidDataException("An authenticated retention row was missing.");
            var key = new AuthenticatedHeadKey(
                document.ProjectId,
                document.Kind,
                document.DocumentId);
            if (!heads.TryGetValue(key, out var current) ||
                document.Revision > current.Revision)
            {
                heads[key] = document;
            }
            else if (document.Revision == current.Revision &&
                     !SameDocument(document, current))
            {
                throw new InvalidDataException(
                    "Authenticated retention rows contain a divergent duplicate revision.");
            }
        }
        return heads.Values.Select(document => document.Snapshot()).ToArray();
    }

    private static int SearchScore(string searchText, IReadOnlyList<string> queryTokens)
    {
        var counts = Tokenize(searchText, maximumTokens: 4096)
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        var score = 0;
        foreach (var queryToken in queryTokens)
        {
            if (!counts.TryGetValue(queryToken, out var count))
                return -1;
            score = checked(score + count);
        }
        return score;
    }

    private static IReadOnlyList<string> Tokenize(string text, int maximumTokens)
    {
        var tokens = new List<string>(Math.Min(maximumTokens, 32));
        var current = new List<char>(64);
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            {
                if (current.Count < 64)
                    current.Add(char.ToLowerInvariant(character));
            }
            else
            {
                Flush();
                if (tokens.Count == maximumTokens)
                    break;
            }
        }
        Flush();
        return Array.AsReadOnly(tokens.ToArray());

        void Flush()
        {
            if (current.Count == 0 || tokens.Count == maximumTokens)
                return;
            tokens.Add(new string(current.ToArray()));
            current.Clear();
        }
    }

    private sealed record AuthenticatedHeadKey(
        string ProjectId,
        string Kind,
        string DocumentId);

    private static void ValidateLookup(string projectId, string kind, string documentId)
    {
        static bool Valid(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length <= CortexAgentRetentionLimits.MaximumIdentifierCharacters &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.Skip(1).All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or ':' or '-');
        if (!Valid(projectId) || !Valid(kind) || !Valid(documentId))
            throw new ArgumentException(
                "A bounded project, kind, and document identity is required.");
    }

    private static PostgreSqlParameter Text(string name, string? value) =>
        new(name, DbType.String, value);

    private static PostgreSqlParameter Binary(string name, byte[] value) =>
        new(name, DbType.Binary, value);

    private static PostgreSqlParameter BigInt(string name, long value) =>
        new(name, DbType.Int64, value);

    private static PostgreSqlParameter Integer(string name, int value) =>
        new(name, DbType.Int32, value);

    private static PostgreSqlParameter Boolean(string name, bool value) =>
        new(name, DbType.Boolean, value);

    private static PostgreSqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, DbType.DateTimeOffset, value.ToUniversalTime());

    private const string AcquireChainLockSql =
        "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";

    private const string InsertChainSql = """
        INSERT INTO cortex_data.retained_chain
            (tenant_id, namespace_name, project_id, kind, document_id,
             created_at, payload, payload_sha256, auth_version, auth_algorithm,
             auth_key_id, auth_signature)
        VALUES
            (@tenant_id, @namespace_name, @project_id, @kind, @document_id,
             @created_at, @payload, @payload_sha256, @auth_version, @auth_algorithm,
             @auth_key_id, @auth_signature);
        """;

    private const string SelectChainSql = """
        SELECT project_id, kind, document_id, created_at, payload,
               payload_sha256, auth_version, auth_algorithm, auth_key_id,
               auth_signature
        FROM cortex_data.retained_chain
        WHERE tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND namespace_name = current_setting('pegasus.cortex.namespace')
          AND project_id = @project_id
          AND kind = @kind
          AND document_id = @document_id;
        """;

    private const string InsertDocumentSql = """
        INSERT INTO cortex_data.retained_document
            (tenant_id, namespace_name, project_id, kind, document_id, revision,
             previous_hash, idempotency_key, producer_id, contract_id, content_hash,
             search_text, captured_at, expires_at, payload, payload_sha256,
             auth_version, auth_algorithm, auth_key_id, auth_signature)
        VALUES
            (@tenant_id, @namespace_name, @project_id, @kind, @document_id, @revision,
             @previous_hash, @idempotency_key, @producer_id, @contract_id, @content_hash,
             @search_text, @captured_at, @expires_at, @payload, @payload_sha256,
             @auth_version, @auth_algorithm, @auth_key_id, @auth_signature);
        """;

    private const string InsertAuditSql = """
        INSERT INTO cortex_data.retention_audit
            (tenant_id, namespace_name, audit_id, project_id, kind, document_id,
             revision, content_hash, producer_id, occurred_at, payload, payload_sha256,
             auth_version, auth_algorithm, auth_key_id, auth_signature)
        VALUES
            (@tenant_id, @namespace_name, @audit_id, @project_id, @kind, @document_id,
             @revision, @content_hash, @producer_id, @occurred_at, @payload, @payload_sha256,
             @auth_version, @auth_algorithm, @auth_key_id, @auth_signature);
        """;

    private const string SelectDocumentSql = """
        SELECT d.project_id, d.kind, d.document_id, d.revision, d.previous_hash,
               d.idempotency_key, d.producer_id, d.contract_id, d.content_hash,
               d.search_text, d.captured_at, d.expires_at, d.payload,
               d.payload_sha256, d.auth_version, d.auth_algorithm, d.auth_key_id,
               d.auth_signature
        FROM cortex_data.retained_document d
        """ + "\n";

    private const string FindByIdempotencySuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND d.producer_id = @producer_id
          AND d.idempotency_key = @idempotency_key;
        """;

    private const string FindHeadSuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND d.document_id = @document_id
        ORDER BY d.revision DESC
        LIMIT 1;
        """;

    private const string FindRevisionSuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND d.document_id = @document_id
          AND d.revision = @revision;
        """;

    private const string FindConflictingTaskHeadSuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND d.document_id <> @document_id
        ORDER BY d.revision DESC, d.document_id COLLATE "C"
        LIMIT 1;
        """;

    private const string ListHeadsSuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND NOT EXISTS
          (
              SELECT 1
              FROM cortex_data.retained_document newer
              WHERE newer.tenant_id = d.tenant_id
                AND newer.namespace_name = d.namespace_name
                AND newer.project_id = d.project_id
                AND newer.kind = d.kind
                AND newer.document_id = d.document_id
                AND newer.revision > d.revision
          )
        LIMIT @candidate_limit;
        """;

    private const string ReadRevisionRangeSuffixSql = """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND d.kind = @kind
          AND d.document_id = @document_id
          AND d.revision BETWEEN @first_revision AND @last_revision
        ORDER BY d.revision
        LIMIT @limit;
        """;

    private const string SearchHeadsSql = SelectDocumentSql + """
        WHERE d.tenant_id = current_setting('pegasus.cortex.tenant_id')
          AND d.namespace_name = current_setting('pegasus.cortex.namespace')
          AND d.project_id = @project_id
          AND NOT EXISTS
          (
              SELECT 1
              FROM cortex_data.retained_document newer
              WHERE newer.tenant_id = d.tenant_id
                AND newer.namespace_name = d.namespace_name
                AND newer.project_id = d.project_id
                AND newer.kind = d.kind
                AND newer.document_id = d.document_id
                AND newer.revision > d.revision
          )
        LIMIT @candidate_limit;
        """;
}
