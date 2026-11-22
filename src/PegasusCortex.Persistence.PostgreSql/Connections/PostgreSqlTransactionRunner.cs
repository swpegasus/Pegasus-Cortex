using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Connections;

public sealed record PostgreSqlDataSourceFault(
    string Code,
    string Operation,
    string CauseType,
    DateTimeOffset ObservedAt);

public interface IPostgreSqlDataSourceFaultSink
{
    void Report(PostgreSqlDataSourceFault fault);
}

/// <summary>
/// Owns the pooled-connection safety boundary shared by PostgreSQL repositories. Every
/// tenant command runs in one transaction after a clean-context check and local RLS bind.
/// A commit/rollback/context-contamination failure poisons this runner until replaced.
/// </summary>
public sealed class PostgreSqlTransactionRunner
{
    private const string CleanContextSql = """
        SELECT
            NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.namespace', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.authority_id', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.subject_id', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.service_id', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.policy_revision', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.capability', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.operation', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.maximum_sensitivity', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.allow_stale', true), '') IS NULL AND
            NULLIF(current_setting('pegasus.cortex.allow_blocked_or_unverified', true), '') IS NULL;
        """;

    private const string BindContextSql = """
        SELECT
            set_config('pegasus.cortex.tenant_id', @tenant_id, true),
            set_config('pegasus.cortex.namespace', @namespace, true),
            set_config('pegasus.cortex.authority_id', @authority_id, true),
            set_config('pegasus.cortex.subject_id', @subject_id, true),
            set_config('pegasus.cortex.service_id', @service_id, true),
            set_config('pegasus.cortex.policy_revision', @policy_revision, true),
            set_config('pegasus.cortex.capability', @capability, true),
            set_config('pegasus.cortex.operation', @operation, true),
            set_config('pegasus.cortex.maximum_sensitivity', @maximum_sensitivity, true),
            set_config('pegasus.cortex.allow_stale', @allow_stale, true),
            set_config('pegasus.cortex.allow_blocked_or_unverified', @allow_blocked_or_unverified, true);
        """;

    private readonly DbDataSource _dataSource;
    private readonly IPostgreSqlDataSourceFaultSink? _faultSink;
    private int _poisoned;
    private string? _faultSinkFailureType;

    public PostgreSqlTransactionRunner(
        DbDataSource dataSource,
        IPostgreSqlDataSourceFaultSink? faultSink = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _faultSink = faultSink;
    }

    public bool IsPoisoned => Volatile.Read(ref _poisoned) != 0;
    public string? FaultSinkFailureType => Volatile.Read(ref _faultSinkFailureType);

    internal DbDataSource DataSource => _dataSource;
    internal static string CleanContextCommandText => CleanContextSql;
    internal static string BindContextCommandText => BindContextSql;

    internal async ValueTask<T> ExecuteAsync<T>(
        PostgreSqlTransactionContext context,
        IsolationLevel isolationLevel,
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContext(context);
        if (IsPoisoned)
            throw Failure("cortex_postgresql_data_source_poisoned", context, retryable: false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        var commitAttempted = false;
        try
        {
            await EnsureCleanAndBindAsync(connection, transaction, context, cancellationToken).ConfigureAwait(false);
            var result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            if (commitAttempted)
                Poison("cortex_postgresql_commit_state_unknown", context.Operation, exception);
            else if (exception is PostgreSqlPersistenceException
            {
                Code: "cortex_postgresql_context_contaminated"
            })
                Poison("cortex_postgresql_context_contaminated", context.Operation, exception);

            Exception? cleanupFailure = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                cleanupFailure = rollbackException;
                Poison("cortex_postgresql_rollback_failed", context.Operation, rollbackException);
            }

            if (exception is OperationCanceledException && cleanupFailure is null && !commitAttempted)
                throw;
            if (exception is PostgreSqlPersistenceException && cleanupFailure is null && !commitAttempted)
                throw;

            var databaseFailure = exception as DbException;
            throw Failure(
                commitAttempted
                    ? "cortex_postgresql_commit_state_unknown"
                    : cleanupFailure is null
                        ? "cortex_postgresql_operation_failed"
                        : "cortex_postgresql_rollback_failed",
                context,
                retryable: !commitAttempted && cleanupFailure is null && IsRetryable(databaseFailure?.SqlState),
                exception,
                databaseFailure?.SqlState,
                cleanupFailure);
        }
    }

    private static async ValueTask EnsureCleanAndBindAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlTransactionContext context,
        CancellationToken cancellationToken)
    {
        await using (var check = PostgreSqlCommandFactory.Create(connection, transaction, CleanContextSql))
        {
            var clean = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (clean is not true)
                throw Failure("cortex_postgresql_context_contaminated", context, retryable: false);
        }

        await using var bind = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            BindContextSql,
            new PostgreSqlParameter("tenant_id", DbType.String, context.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, context.Namespace),
            new PostgreSqlParameter("authority_id", DbType.String, context.AuthorityId),
            new PostgreSqlParameter("subject_id", DbType.String, context.SubjectId),
            new PostgreSqlParameter("service_id", DbType.String, context.ServiceId),
            new PostgreSqlParameter("policy_revision", DbType.String, context.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new PostgreSqlParameter("capability", DbType.String, context.Capability),
            new PostgreSqlParameter("operation", DbType.String, context.Operation),
            new PostgreSqlParameter("maximum_sensitivity", DbType.String, context.MaximumSensitivity),
            new PostgreSqlParameter("allow_stale", DbType.String, context.AllowStale ? "true" : "false"),
            new PostgreSqlParameter("allow_blocked_or_unverified", DbType.String, context.AllowBlockedOrUnverified ? "true" : "false"));
        await bind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateContext(PostgreSqlTransactionContext context)
    {
        if (!ValidIdentifier(context.TenantId, 128) || !ValidNamespace(context.Namespace) ||
            !ValidIdentifier(context.AuthorityId, 128) || !ValidIdentifier(context.SubjectId, 128) ||
            !ValidIdentifier(context.ServiceId, 128) || context.PolicyRevision < 1 ||
            !ValidIdentifier(context.Capability, 32) || !ValidOperation(context.Operation) ||
            context.MaximumSensitivity is not ("public" or "internal" or "confidential" or "restricted"))
        {
            throw Failure("cortex_postgresql_scope_invalid", context, retryable: false);
        }
    }

    private void Poison(string code, string operation, Exception cause)
    {
        Interlocked.Exchange(ref _poisoned, 1);
        try
        {
            _faultSink?.Report(new PostgreSqlDataSourceFault(
                code,
                operation,
                cause.GetType().FullName ?? cause.GetType().Name,
                DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            // A diagnostic callback cannot prevent rollback. Preserve its safe type so
            // readiness exposes that pool quarantine reporting itself failed.
            Volatile.Write(
                ref _faultSinkFailureType,
                exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    private static PostgreSqlPersistenceException Failure(
        string code,
        PostgreSqlTransactionContext context,
        bool retryable,
        Exception? cause = null,
        string? sqlState = null,
        Exception? cleanupFailure = null) =>
        new(
            code,
            context.Operation,
            retryable,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = context.TenantId,
                ["namespace"] = context.Namespace,
                ["capability"] = context.Capability
            },
            sqlState,
            cause,
            cleanupFailure);

    private static bool IsRetryable(string? sqlState) => sqlState is "40001" or "40P01";

    private static bool ValidIdentifier(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidNamespace(string value) =>
        ValidIdentifier(value, 256) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool ValidOperation(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
