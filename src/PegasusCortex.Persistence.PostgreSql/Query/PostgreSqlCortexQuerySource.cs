using System.Data;
using System.Data.Common;
using System.Text;
using Pegasus.Cortex.Contracts.Query;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Security;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Query;

/// <summary>
/// Durable Core query-source adapter. It performs fixed parameterized SQL inside an exact
/// Core-authorized RLS transaction and verifies every materialized canonical row seal.
/// </summary>
public sealed class PostgreSqlCortexQuerySource : ICortexQuerySource
{
    private const string SnapshotSql = """
        SELECT snapshot.tenant_id, snapshot.namespace_name, snapshot.maximum_sensitivity,
               CASE WHEN char_length(snapshot.collection_id) BETWEEN 1 AND 128
                    THEN snapshot.collection_id ELSE NULL END AS collection_id,
               CASE WHEN octet_length(snapshot.payload) BETWEEN 1 AND 524288
                    THEN snapshot.payload ELSE NULL END AS payload,
               CASE WHEN char_length(snapshot.payload_sha256) = 64
                    THEN snapshot.payload_sha256 ELSE NULL END AS payload_sha256,
               snapshot.auth_version,
               CASE WHEN char_length(snapshot.auth_algorithm) BETWEEN 1 AND 128
                    THEN snapshot.auth_algorithm ELSE NULL END AS auth_algorithm,
               CASE WHEN char_length(snapshot.auth_key_id) BETWEEN 1 AND 128
                    THEN snapshot.auth_key_id ELSE NULL END AS auth_key_id,
               CASE WHEN octet_length(snapshot.auth_signature) BETWEEN 1 AND 16384
                    THEN snapshot.auth_signature ELSE NULL END AS auth_signature
        FROM cortex_data.collection_head head_value
        JOIN cortex_data.collection_snapshot snapshot
         ON snapshot.tenant_id = head_value.tenant_id
         AND snapshot.namespace_name = head_value.namespace_name
         AND snapshot.maximum_sensitivity = head_value.maximum_sensitivity
         AND snapshot.collection_id = head_value.current_collection_id
        WHERE snapshot.tenant_id = @tenant_id
          AND snapshot.namespace_name = @namespace
          AND snapshot.maximum_sensitivity = @maximum_sensitivity;
        """;

    private const string RecordColumns = """
        k.tenant_id, k.namespace_name,
        CASE WHEN char_length(k.record_id) BETWEEN 1 AND 128 THEN k.record_id ELSE NULL END,
        CASE WHEN char_length(k.revision_id) BETWEEN 1 AND 128 THEN k.revision_id ELSE NULL END,
        CASE WHEN char_length(k.taxonomy) BETWEEN 1 AND 128 THEN k.taxonomy ELSE NULL END,
        CASE WHEN char_length(k.title) BETWEEN 1 AND 512 THEN k.title ELSE NULL END,
        CASE WHEN char_length(k.summary) BETWEEN 1 AND 2048 THEN k.summary ELSE NULL END,
        k.freshness_state, k.sensitivity,
        k.record_status, k.content_treatment, k.executable, k.content_hash,
        CASE WHEN char_length(k.applicability::text) <= 131072
             THEN k.applicability::text ELSE NULL END,
        CASE WHEN char_length(k.content) BETWEEN 1 AND 32768 THEN k.content ELSE NULL END,
        CASE WHEN char_length(k.keyword_text) BETWEEN 0 AND 8255 THEN k.keyword_text ELSE NULL END,
        CASE WHEN octet_length(k.payload) BETWEEN 1 AND 524288
             THEN k.payload ELSE NULL END AS payload,
        k.payload_sha256, k.auth_version,
        CASE WHEN char_length(k.auth_algorithm) BETWEEN 1 AND 128
             THEN k.auth_algorithm ELSE NULL END,
        CASE WHEN char_length(k.auth_key_id) BETWEEN 1 AND 128
             THEN k.auth_key_id ELSE NULL END,
        CASE WHEN octet_length(k.auth_signature) BETWEEN 1 AND 16384
             THEN k.auth_signature ELSE NULL END
        """;

    private static readonly string CatalogSql = $$"""
        SELECT {{RecordColumns}}
        FROM cortex_data.knowledge_head h
        JOIN cortex_data.knowledge_revision k
          ON k.tenant_id = h.tenant_id
         AND k.namespace_name = h.namespace_name
         AND k.record_id = h.record_id
         AND k.revision_id = h.current_revision_id
        WHERE k.tenant_id = @tenant_id
          AND k.namespace_name = @namespace
          AND k.record_status = 'active'
          AND k.freshness_state = 'current'
          AND k.content_treatment = 'untrusted_data'
          AND k.executable = false
        ORDER BY k.record_id COLLATE "C", k.revision_id COLLATE "C"
        LIMIT @limit;
        """;

    private static readonly string ExactSearchSql = $$"""
        WITH query_value AS
        (
            SELECT plainto_tsquery('simple'::regconfig, @search_text) AS value
        )
        SELECT {{RecordColumns}},
               ts_rank_cd(
                   k.search_document ||
                   setweight(to_tsvector('simple'::regconfig, k.taxonomy), 'B'),
                   query_value.value)::double precision AS score
        FROM cortex_data.knowledge_head h
        JOIN cortex_data.knowledge_revision k
          ON k.tenant_id = h.tenant_id
         AND k.namespace_name = h.namespace_name
         AND k.record_id = h.record_id
         AND k.revision_id = h.current_revision_id
        CROSS JOIN query_value
        WHERE k.tenant_id = @tenant_id
          AND k.namespace_name = @namespace
          AND k.record_status = 'active'
          AND k.content_treatment = 'untrusted_data'
          AND k.executable = false
          AND
          (
              k.search_document ||
              setweight(to_tsvector('simple'::regconfig, k.taxonomy), 'B')
          ) @@ query_value.value
          AND (@taxonomy IS NULL OR k.taxonomy = @taxonomy)
          AND
          (
              (@product_id IS NULL AND @component_id IS NULL) OR
              EXISTS
              (
                  SELECT 1
                  FROM jsonb_array_elements(k.applicability) AS item
                  WHERE (@product_id IS NULL OR item->>'productId' = @product_id)
                    AND (@component_id IS NULL OR item->>'componentId' = @component_id)
              )
          )
        ORDER BY score DESC, k.title COLLATE "C", k.record_id COLLATE "C", k.revision_id COLLATE "C"
        LIMIT @limit;
        """;

    private static readonly string BroadSearchSql = $$"""
        WITH query_terms AS
        (
            SELECT DISTINCT term
            FROM regexp_split_to_table(lower(@search_text), '[^[:alnum:]]+') AS term
            WHERE term <> ''
        ),
        query_value AS
        (
            SELECT to_tsquery(
                       'simple'::regconfig,
                       string_agg(quote_literal(term), ' | ' ORDER BY term)) AS value
            FROM query_terms
        )
        SELECT {{RecordColumns}},
               ts_rank_cd(
                   setweight(to_tsvector('simple'::regconfig, k.title), 'A') ||
                   setweight(to_tsvector('simple'::regconfig, k.taxonomy), 'B') ||
                   setweight(to_tsvector('simple'::regconfig, k.keyword_text), 'B') ||
                   setweight(to_tsvector('simple'::regconfig, k.summary), 'C') ||
                   setweight(to_tsvector('simple'::regconfig, k.content), 'D'),
                   query_value.value)::double precision AS score
        FROM cortex_data.knowledge_head h
        JOIN cortex_data.knowledge_revision k
          ON k.tenant_id = h.tenant_id
         AND k.namespace_name = h.namespace_name
         AND k.record_id = h.record_id
         AND k.revision_id = h.current_revision_id
        CROSS JOIN query_value
        WHERE k.tenant_id = @tenant_id
          AND k.namespace_name = @namespace
          AND k.record_status = 'active'
          AND k.content_treatment = 'untrusted_data'
          AND k.executable = false
          AND query_value.value IS NOT NULL
          AND
          (
              k.search_document ||
              setweight(to_tsvector('simple'::regconfig, k.taxonomy), 'B')
          ) @@ query_value.value
          AND (@taxonomy IS NULL OR k.taxonomy = @taxonomy)
          AND
          (
              (@product_id IS NULL AND @component_id IS NULL) OR
              EXISTS
              (
                  SELECT 1
                  FROM jsonb_array_elements(k.applicability) AS item
                  WHERE (@product_id IS NULL OR item->>'productId' = @product_id)
                    AND (@component_id IS NULL OR item->>'componentId' = @component_id)
              )
          )
        ORDER BY score DESC, k.title COLLATE "C", k.record_id COLLATE "C", k.revision_id COLLATE "C"
        LIMIT @limit;
        """;

    private static readonly string ReadSql = $$"""
        SELECT {{RecordColumns}}
        FROM cortex_data.knowledge_head h
        JOIN cortex_data.knowledge_revision k
          ON k.tenant_id = h.tenant_id
         AND k.namespace_name = h.namespace_name
         AND k.record_id = h.record_id
         AND k.revision_id = h.current_revision_id
        WHERE k.tenant_id = @tenant_id
          AND k.namespace_name = @namespace
          AND k.record_id = @record_id
          AND k.revision_id = @revision_id
          AND k.record_status = 'active'
          AND k.content_treatment = 'untrusted_data'
          AND k.executable = false;
        """;

    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly PostgreSqlStoredRowCodec _codec;

    public PostgreSqlCortexQuerySource(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator authenticator)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _codec = new PostgreSqlStoredRowCodec(authenticator);
    }

    public ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope, expectedCapability: null, cancellationToken);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromQuery(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    SnapshotSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, scope.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, scope.Namespace),
                    new PostgreSqlParameter(
                        "maximum_sensitivity",
                        DbType.String,
                        scope.MaximumSensitivity));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    throw SourceFailure("cortex_postgresql_snapshot_missing", scope, retryable: false);
                var snapshot = ReadSnapshot(reader, scope);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw SourceFailure("cortex_postgresql_snapshot_ambiguous", scope, retryable: false);
                return snapshot;
            },
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, CortexQueryCapability.Catalog, cancellationToken);
        EnsureNamespace(query.Namespace, scope);
        return _transactions.ExecuteAsync<IReadOnlyList<CortexKnowledgeRecordSummary>>(
            PostgreSqlTransactionContext.FromQuery(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    CatalogSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, scope.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, scope.Namespace),
                    new PostgreSqlParameter("limit", DbType.Int32, query.Limit));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var records = new List<CortexKnowledgeRecordSummary>(query.Limit);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (records.Count == query.Limit)
                        throw SourceFailure("cortex_postgresql_catalog_unbounded", scope, retryable: false);
                    records.Add(ToSummary(ReadRecord(reader, scope)));
                }
                return records.ToArray();
            },
            cancellationToken);
    }

    public async ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, CortexQueryCapability.Search, cancellationToken);
        EnsureNamespace(query.Namespace, scope);
        try
        {
            return await _transactions.ExecuteAsync(
                PostgreSqlTransactionContext.FromQuery(scope),
                IsolationLevel.ReadCommitted,
                async (connection, transaction, token) =>
                {
                    var searchSql = query.Mode == CortexSearchMode.Broad
                        ? BroadSearchSql
                        : ExactSearchSql;
                    await using var command = PostgreSqlCommandFactory.Create(
                        connection,
                        transaction,
                        searchSql,
                        new PostgreSqlParameter("tenant_id", DbType.String, scope.TenantId),
                        new PostgreSqlParameter("namespace", DbType.String, scope.Namespace),
                        new PostgreSqlParameter(
                            "search_text",
                            DbType.String,
                            CortexKnowledgeLexicalSearch.ProviderQueryText(query)),
                        new PostgreSqlParameter("taxonomy", DbType.String, query.Taxonomy),
                        new PostgreSqlParameter("product_id", DbType.String, query.ProductId),
                        new PostgreSqlParameter("component_id", DbType.String, query.ComponentId),
                        new PostgreSqlParameter("limit", DbType.Int32, CortexQueryLimits.MaximumSearchResults));
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    var records = new List<CortexKnowledgeSearchHit>(CortexQueryLimits.MaximumSearchResults);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (records.Count == CortexQueryLimits.MaximumSearchResults)
                            throw SourceFailure("cortex_postgresql_search_unbounded", scope, retryable: false);
                        var record = ReadRecord(reader, scope);
                        var lexicalScore = reader.GetDouble(22);
                        if (!double.IsFinite(lexicalScore) || lexicalScore <= 0)
                            throw SourceFailure("cortex_postgresql_search_score_invalid", scope, retryable: false);
                        var lexicalMatch = LexicalMatch(record, query);
                        // Prefix candidates keep broad lookup indexable while the shared
                        // matcher removes over-inclusive prefixes before projection.
                        if (!lexicalMatch.IsMatch)
                            continue;
                        var score = CortexKnowledgeRanking.EffectiveScore(
                            lexicalMatch.WeightedScore,
                            record.Confidence.Value);
                        records.Add(new CortexKnowledgeSearchHit(
                            record,
                            score,
                            CortexQueryPolicy.SafetyNotice,
                            lexicalMatch.MatchedTerms,
                            lexicalMatch.QueryTermCount));
                    }
                    return new CortexQuerySourceSearchResult(
                        true,
                        records
                            .OrderByDescending(item => item.Score)
                            .ThenBy(item => item.Record.Title, StringComparer.Ordinal)
                            .ThenBy(item => item.Record.RecordId, StringComparer.Ordinal)
                            .ThenBy(item => item.Record.RevisionId, StringComparer.Ordinal)
                            .Take(query.Limit)
                            .ToArray(),
                        null);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgreSqlPersistenceException exception)
        {
            return new CortexQuerySourceSearchResult(
                false,
                [],
                new PegasusDiagnostic(
                    "cortex_postgresql_search_failed",
                    "The durable Cortex search source rejected the authorized query.",
                    DiagnosticSeverity.Error,
                    scope.Operation,
                    exception.Retryable,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["namespace"] = scope.Namespace,
                        ["sourceCode"] = exception.Code
                    },
                    exception.GetType().FullName));
        }
    }

    public ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, CortexQueryCapability.Read, cancellationToken);
        EnsureNamespace(query.Namespace, scope);
        return _transactions.ExecuteAsync<CortexKnowledgeRecord?>(
            PostgreSqlTransactionContext.FromQuery(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    ReadSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, scope.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, scope.Namespace),
                    new PostgreSqlParameter("record_id", DbType.String, query.RecordId),
                    new PostgreSqlParameter("revision_id", DbType.String, query.RevisionId));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    return null;
                var record = ReadRecord(reader, scope);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw SourceFailure("cortex_postgresql_record_ambiguous", scope, retryable: false);
                return record;
            },
            cancellationToken);
    }

    private CortexKnowledgeSourceSnapshot ReadSnapshot(
        DbDataReader reader,
        CortexQuerySourceScope scope)
    {
        var tenantId = reader.GetString(0);
        var namespaceName = reader.GetString(1);
        var maximumSensitivity = reader.GetString(2);
        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) ||
            reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9))
            throw RowProjectionFailure("collection-snapshot", tenantId, namespaceName, namespaceName);
        var collectionId = reader.GetString(3);
        // The tier is part of the authenticated identity so a valid low-sensitivity
        // projection cannot be replayed as the namespace's restricted projection.
        var value = _codec.Decode<CortexKnowledgeSourceSnapshot>(
            "collection-snapshot",
            tenantId,
            namespaceName,
            maximumSensitivity,
            collectionId,
            reader.GetFieldValue<byte[]>(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<byte[]>(9));
        if (!string.Equals(tenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(maximumSensitivity, scope.MaximumSensitivity, StringComparison.Ordinal) ||
            !string.Equals(value.CollectionId, collectionId, StringComparison.Ordinal) ||
            !string.Equals(value.DefaultNamespace, namespaceName, StringComparison.Ordinal))
        {
            throw RowProjectionFailure("collection-snapshot", tenantId, namespaceName, namespaceName);
        }
        return value;
    }

    private CortexKnowledgeRecord ReadRecord(
        DbDataReader reader,
        CortexQuerySourceScope scope)
    {
        var tenantId = reader.GetString(0);
        var namespaceName = reader.GetString(1);
        if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) ||
            reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(13) ||
            reader.IsDBNull(14) || reader.IsDBNull(15) || reader.IsDBNull(16) ||
            reader.IsDBNull(19) || reader.IsDBNull(20) || reader.IsDBNull(21))
        {
            throw RowProjectionFailure("knowledge-revision", tenantId, namespaceName, namespaceName);
        }
        var recordId = reader.GetString(2);
        var revisionId = reader.GetString(3);
        var record = _codec.Decode<CortexKnowledgeRecord>(
            "knowledge-revision",
            tenantId,
            namespaceName,
            recordId,
            revisionId,
            reader.GetFieldValue<byte[]>(16),
            reader.GetString(17),
            reader.GetInt32(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetFieldValue<byte[]>(21));

        if (!string.Equals(tenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(record.Namespace, namespaceName, StringComparison.Ordinal) ||
            !string.Equals(record.RecordId, recordId, StringComparison.Ordinal) ||
            !string.Equals(record.RevisionId, revisionId, StringComparison.Ordinal) ||
            !string.Equals(record.Taxonomy, reader.GetString(4), StringComparison.Ordinal) ||
            !string.Equals(record.Title, reader.GetString(5), StringComparison.Ordinal) ||
            !string.Equals(record.Summary, reader.GetString(6), StringComparison.Ordinal) ||
            !string.Equals(record.Freshness.State, reader.GetString(7), StringComparison.Ordinal) ||
            !string.Equals(record.Sensitivity, reader.GetString(8), StringComparison.Ordinal) ||
            !string.Equals(record.RecordStatus, reader.GetString(9), StringComparison.Ordinal) ||
            !string.Equals(record.ContentTreatment, reader.GetString(10), StringComparison.Ordinal) ||
            record.Executable != reader.GetBoolean(11) ||
            !string.Equals(record.ContentHash, reader.GetString(12), StringComparison.Ordinal) ||
            !ApplicabilityMatches(record.Applicability, reader.GetString(13)) ||
            !string.Equals(record.Content, reader.GetString(14), StringComparison.Ordinal) ||
            !string.Equals(string.Join(' ', record.Keywords), reader.GetString(15), StringComparison.Ordinal))
        {
            throw RowProjectionFailure("knowledge-revision", tenantId, namespaceName, recordId);
        }
        return record;
    }

    private static bool ApplicabilityMatches(
        IReadOnlyList<CortexKnowledgeApplicability> expected,
        string storedJson)
    {
        try
        {
            var stored = CortexCanonicalJson.Deserialize<CortexKnowledgeApplicability[]>(
                Encoding.UTF8.GetBytes(storedJson));
            return CortexCanonicalJson.Serialize(stored).AsSpan()
                .SequenceEqual(CortexCanonicalJson.Serialize(expected));
        }
        catch
        {
            return false;
        }
    }

    private static CortexKnowledgeRecordSummary ToSummary(CortexKnowledgeRecord record) =>
        new(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Title,
            record.Summary,
            record.Freshness.State,
            record.Sensitivity,
            record.ContentTreatment,
            record.Executable);

    private static CortexLexicalMatch LexicalMatch(
        CortexKnowledgeRecord record,
        CortexSearchQuery query) =>
        CortexKnowledgeLexicalSearch.Match(record, query);

    private static void ValidateScope(
        CortexQuerySourceScope scope,
        CortexQueryCapability? expectedCapability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedCapability is not null && scope.Capability != expectedCapability ||
            !OperationMatches(scope.Capability, scope.Operation))
        {
            throw SourceFailure("cortex_postgresql_query_scope_invalid", scope, retryable: false);
        }
    }

    private static bool OperationMatches(CortexQueryCapability capability, string operation) => capability switch
    {
        CortexQueryCapability.Discover => operation == "cortex.query.discover",
        CortexQueryCapability.Status => operation == "cortex.query.status",
        CortexQueryCapability.Catalog => operation == "cortex.query.catalog",
        CortexQueryCapability.Search => operation == "cortex.query.search",
        CortexQueryCapability.Read => operation is "cortex.query.read" or "cortex.query.provenance.read",
        _ => false
    };

    private static void EnsureNamespace(string namespaceName, CortexQuerySourceScope scope)
    {
        if (!string.Equals(namespaceName, scope.Namespace, StringComparison.Ordinal))
            throw SourceFailure("cortex_postgresql_query_namespace_mismatch", scope, retryable: false);
    }

    private static PostgreSqlPersistenceException RowProjectionFailure(
        string entityKind,
        string tenantId,
        string namespaceName,
        string primaryId) =>
        new(
            "cortex_postgresql_row_projection_mismatch",
            "cortex.postgresql.row.verify",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["entityKind"] = entityKind,
                ["tenantId"] = tenantId,
                ["namespace"] = namespaceName,
                ["primaryId"] = primaryId
            });

    private static PostgreSqlPersistenceException SourceFailure(
        string code,
        CortexQuerySourceScope scope,
        bool retryable) =>
        new(
            code,
            scope.Operation,
            retryable,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = scope.TenantId,
                ["namespace"] = scope.Namespace,
                ["capability"] = scope.Capability.ToString()
            });
}
