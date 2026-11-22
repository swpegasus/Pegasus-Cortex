using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Hybrid;

/// <summary>
/// Authenticated, immutable PostgreSQL storage for public project correlation metadata.
/// Caller paths, credentials, tokens, and serialized authority are never accepted or stored.
/// </summary>
public sealed class PostgreSqlProjectLinkRepository : ICortexProjectLinkRepository
{
    private const string EntityKind = "project-link";
    private const string ReadOperation = "cortex.project-link.read";
    private const string CorrelateOperation = "cortex.project-link.correlate";

    private const string InsertSql = """
        INSERT INTO cortex_data.project_link
        (
            tenant_id, namespace_name, project_link_id, project_namespace,
            project_id, store_id, store_epoch, endpoint_id, correlation_hash,
            payload, payload_sha256, auth_version, auth_algorithm, auth_key_id,
            auth_signature, created_at
        )
        VALUES
        (
            @tenant_id, @namespace, @project_link_id, @project_namespace,
            @project_id, @store_id, @store_epoch, @endpoint_id, @correlation_hash,
            @payload, @payload_sha256, @auth_version, @auth_algorithm, @auth_key_id,
            @auth_signature, transaction_timestamp()
        )
        ON CONFLICT (tenant_id, namespace_name, project_link_id) DO NOTHING;
        """;

    private const string SelectSql = """
        SELECT tenant_id, namespace_name, project_link_id, project_namespace,
               project_id, store_id, store_epoch, endpoint_id, correlation_hash,
               CASE WHEN octet_length(payload) BETWEEN 1 AND 131072
                    THEN payload ELSE NULL END,
               payload_sha256, auth_version, auth_algorithm, auth_key_id,
               CASE WHEN octet_length(auth_signature) BETWEEN 1 AND 16384
                    THEN auth_signature ELSE NULL END
        FROM cortex_data.project_link
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND project_link_id = @project_link_id;
        """;

    private const string ListSql = """
        SELECT tenant_id, namespace_name, project_link_id, project_namespace,
               project_id, store_id, store_epoch, endpoint_id, correlation_hash,
               CASE WHEN octet_length(payload) BETWEEN 1 AND 131072
                    THEN payload ELSE NULL END,
               payload_sha256, auth_version, auth_algorithm, auth_key_id,
               CASE WHEN octet_length(auth_signature) BETWEEN 1 AND 16384
                    THEN auth_signature ELSE NULL END
        FROM cortex_data.project_link
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
        ORDER BY project_link_id COLLATE "C"
        LIMIT @limit;
        """;

    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly PostgreSqlStoredRowCodec _codec;

    public PostgreSqlProjectLinkRepository(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator authenticator)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _codec = new PostgreSqlStoredRowCodec(
            authenticator ?? throw new ArgumentNullException(nameof(authenticator)));
    }

    public ValueTask<CortexProjectLinkV1?> FindAsync(
        CortexProjectLinkAccess access,
        string projectLinkId,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, access.Capability, cancellationToken);
        ValidateIdentifier(projectLinkId, access);
        var operation = access.Capability == CortexProjectLinkCapability.Correlate
            ? CorrelateOperation
            : ReadOperation;
        return _transactions.ExecuteAsync<CortexProjectLinkV1?>(
            PostgreSqlTransactionContext.FromProjectLink(access, operation),
            IsolationLevel.ReadCommitted,
            (connection, transaction, token) =>
                ReadAsync(connection, transaction, access, projectLinkId, operation, token),
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<CortexProjectLinkV1>> ListAsync(
        CortexProjectLinkAccess access,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, CortexProjectLinkCapability.Read, cancellationToken);
        if (limit is < 1 or > CortexProjectCorrelationContract.MaximumProjectLinkPageSize)
            throw Failure("cortex_postgresql_project_link_list_limit_invalid", access, null);

        return _transactions.ExecuteAsync<IReadOnlyList<CortexProjectLinkV1>>(
            PostgreSqlTransactionContext.FromProjectLink(access, ReadOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    ListSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
                    new PostgreSqlParameter("limit", DbType.Int32, limit));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var links = new List<CortexProjectLinkV1>(limit);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    links.Add(ReadProjectedLink(reader, access, expectedProjectLinkId: null));
                return links;
            },
            cancellationToken);
    }

    public ValueTask<CortexProjectLinkWriteResult> AddAsync(
        CortexProjectLinkAccess access,
        CortexProjectLinkV1 link,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, CortexProjectLinkCapability.Correlate, cancellationToken);
        CortexProjectCorrelationPolicy.ValidateStoredLink(access, link);
        var captured = new CortexProjectLinkV1(link.Metadata with { }, link.CorrelationHash);

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromProjectLink(access, CorrelateOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var stored = _codec.Encode(
                    EntityKind,
                    access.TenantId,
                    access.Namespace,
                    captured.Metadata.ProjectLinkId,
                    revisionId: null,
                    captured);
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    InsertSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
                    new PostgreSqlParameter(
                        "project_link_id",
                        DbType.String,
                        captured.Metadata.ProjectLinkId),
                    new PostgreSqlParameter(
                        "project_namespace",
                        DbType.String,
                        captured.Metadata.ProjectNamespace),
                    new PostgreSqlParameter("project_id", DbType.String, captured.Metadata.ProjectId),
                    new PostgreSqlParameter("store_id", DbType.String, captured.Metadata.StoreId),
                    new PostgreSqlParameter("store_epoch", DbType.Int64, captured.Metadata.StoreEpoch),
                    new PostgreSqlParameter("endpoint_id", DbType.String, captured.Metadata.EndpointId),
                    new PostgreSqlParameter(
                        "correlation_hash",
                        DbType.String,
                        captured.CorrelationHash),
                    new PostgreSqlParameter("payload", DbType.Binary, stored.Payload),
                    new PostgreSqlParameter(
                        "payload_sha256",
                        DbType.String,
                        stored.PayloadSha256),
                    new PostgreSqlParameter("auth_version", DbType.Int32, stored.Seal.Version),
                    new PostgreSqlParameter(
                        "auth_algorithm",
                        DbType.String,
                        stored.Seal.Algorithm),
                    new PostgreSqlParameter("auth_key_id", DbType.String, stored.Seal.KeyId),
                    new PostgreSqlParameter(
                        "auth_signature",
                        DbType.Binary,
                        stored.Seal.GetSignature()));
                var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (affected is not (0 or 1))
                    throw Failure("cortex_postgresql_project_link_insert_count_invalid", access, captured.Metadata.ProjectLinkId);

                var reread = await ReadAsync(
                    connection,
                    transaction,
                    access,
                    captured.Metadata.ProjectLinkId,
                    CorrelateOperation,
                    token).ConfigureAwait(false)
                    ?? throw Failure(
                        "cortex_postgresql_project_link_reread_missing",
                        access,
                        captured.Metadata.ProjectLinkId);
                var same = string.Equals(
                    reread.CorrelationHash,
                    captured.CorrelationHash,
                    StringComparison.Ordinal);
                return new CortexProjectLinkWriteResult(
                    affected == 1
                        ? CortexProjectLinkWriteDisposition.Added
                        : same
                            ? CortexProjectLinkWriteDisposition.Existing
                            : CortexProjectLinkWriteDisposition.Conflict,
                    reread);
            },
            cancellationToken);
    }

    private async ValueTask<CortexProjectLinkV1?> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexProjectLinkAccess access,
        string projectLinkId,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            SelectSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("project_link_id", DbType.String, projectLinkId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var link = ReadProjectedLink(reader, access, projectLinkId);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure("cortex_postgresql_project_link_ambiguous", access, projectLinkId);
        return link;
    }

    private CortexProjectLinkV1 ReadProjectedLink(
        DbDataReader reader,
        CortexProjectLinkAccess access,
        string? expectedProjectLinkId)
    {
        if (reader.IsDBNull(9) || reader.IsDBNull(12) || reader.IsDBNull(13) || reader.IsDBNull(14))
            throw Failure("cortex_postgresql_project_link_projection_invalid", access, expectedProjectLinkId);

        var tenantId = reader.GetString(0);
        var namespaceName = reader.GetString(1);
        var storedProjectLinkId = reader.GetString(2);
        var link = _codec.Decode<CortexProjectLinkV1>(
            EntityKind,
            tenantId,
            namespaceName,
            storedProjectLinkId,
            revisionId: null,
            reader.GetFieldValue<byte[]>(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetFieldValue<byte[]>(14));
        CortexProjectCorrelationPolicy.ValidateStoredLink(access, link, expectedProjectLinkId);
        if (!string.Equals(tenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(storedProjectLinkId, link.Metadata.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), link.Metadata.ProjectNamespace, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), link.Metadata.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(5), link.Metadata.StoreId, StringComparison.Ordinal) ||
            reader.GetInt64(6) != link.Metadata.StoreEpoch ||
            !string.Equals(reader.GetString(7), link.Metadata.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(8), link.CorrelationHash, StringComparison.Ordinal))
        {
            throw Failure(
                "cortex_postgresql_project_link_projection_mismatch",
                access,
                expectedProjectLinkId ?? storedProjectLinkId);
        }
        return link;
    }

    private static void ValidateAccess(
        CortexProjectLinkAccess access,
        CortexProjectLinkCapability expectedCapability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                access.Namespace,
                CortexProjectCorrelationContract.ControlNamespace,
                StringComparison.Ordinal) ||
            access.PolicyRevision < 1 ||
            !Enum.IsDefined(expectedCapability) ||
            access.Capability != expectedCapability)
        {
            throw Failure("cortex_postgresql_project_link_access_invalid", access, null);
        }
    }

    private static void ValidateIdentifier(string value, CortexProjectLinkAccess access)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexProjectCorrelationContract.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw Failure("cortex_postgresql_project_link_id_invalid", access, value);
        }
    }

    private static PostgreSqlPersistenceException Failure(
        string code,
        CortexProjectLinkAccess access,
        string? projectLinkId) =>
        new(
            code,
            access.Capability == CortexProjectLinkCapability.Correlate
                ? CorrelateOperation
                : ReadOperation,
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = access.TenantId,
                ["namespace"] = access.Namespace,
                ["projectLinkId"] = projectLinkId
            });
}
