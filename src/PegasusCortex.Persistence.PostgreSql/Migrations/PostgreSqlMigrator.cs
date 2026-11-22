using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Migrations;

public sealed record PostgreSqlMigrationResult(
    IReadOnlyList<string> AppliedMigrationIds,
    string CurrentMigrationId);

/// <summary>
/// Applies immutable forward migrations under one transaction-level advisory lock. It
/// refuses checksum drift, unknown history, skipped migrations, and database downgrades.
/// Destructive down migration and role provisioning remain installer-owned recovery work.
/// </summary>
public sealed class PostgreSqlMigrator(DbDataSource migrationDataSource)
{
    private const long MigrationLockKey = 0x5045474354580001;
    private readonly DbDataSource _migrationDataSource =
        migrationDataSource ?? throw new ArgumentNullException(nameof(migrationDataSource));

    public async ValueTask<PostgreSqlMigrationResult> ApplyAsync(
        string applicationVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 128)
            throw new ArgumentException("The application version is invalid.", nameof(applicationVersion));
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _migrationDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            // The advisory lock serializes migrators. ReadCommitted ensures a waiter reads
            // migration history only after the lock holder's commit becomes visible.
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var commitAttempted = false;
        try
        {
            await AcquireLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var applied = await ReadAppliedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            ValidateAppliedHistory(applied);

            var newlyApplied = new List<string>();
            foreach (var migration in PostgreSqlMigrationCatalog.All.Skip(applied.Count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using (var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    migration.Sql))
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var ledger = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    """
                    INSERT INTO cortex_meta.schema_migrations
                        (migration_id, description, up_sha256, application_version)
                    VALUES (@migration_id, @description, @up_sha256, @application_version);
                    """,
                    new PostgreSqlParameter("migration_id", DbType.String, migration.Id),
                    new PostgreSqlParameter("description", DbType.String, migration.Description),
                    new PostgreSqlParameter("up_sha256", DbType.String, migration.Sha256),
                    new PostgreSqlParameter("application_version", DbType.String, applicationVersion));
                await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                newlyApplied.Add(migration.Id);
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlMigrationResult(newlyApplied.ToArray(), PostgreSqlMigrationCatalog.LatestId);
        }
        catch (OperationCanceledException exception)
        {
            var cleanupFailure = await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (cleanupFailure is null && !commitAttempted)
                throw;
            throw new PostgreSqlPersistenceException(
                commitAttempted
                    ? "cortex_postgresql_migration_commit_state_unknown"
                    : "cortex_postgresql_migration_rollback_failed",
                "cortex.postgresql.migrate",
                retryable: false,
                innerException: exception,
                cleanupFailure: cleanupFailure);
        }
        catch (Exception exception)
        {
            var cleanupFailure = await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (exception is PostgreSqlPersistenceException && cleanupFailure is null && !commitAttempted)
                throw;
            var databaseFailure = exception as DbException;
            throw new PostgreSqlPersistenceException(
                commitAttempted
                    ? "cortex_postgresql_migration_commit_state_unknown"
                    : cleanupFailure is null
                        ? "cortex_postgresql_migration_failed"
                        : "cortex_postgresql_migration_rollback_failed",
                "cortex.postgresql.migrate",
                retryable: !commitAttempted && cleanupFailure is null &&
                    databaseFailure?.SqlState is "40001" or "40P01",
                context: null,
                sqlState: databaseFailure?.SqlState,
                innerException: exception,
                cleanupFailure: cleanupFailure);
        }
    }

    private static async ValueTask AcquireLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(@lock_key);",
            new PostgreSqlParameter("lock_key", DbType.Int64, MigrationLockKey));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<(string Id, string Sha256)>> ReadAppliedAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            "SELECT to_regclass('cortex_meta.schema_migrations') IS NOT NULL;"))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                return [];
        }

        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            """
            SELECT migration_id, up_sha256
            FROM cortex_meta.schema_migrations
            ORDER BY sequence_number;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<(string Id, string Sha256)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (values.Count == 1024)
                throw new InvalidOperationException("The migration ledger exceeds its bounded contract.");
            values.Add((reader.GetString(0), reader.GetString(1)));
        }
        return values;
    }

    internal static void ValidateAppliedHistory(IReadOnlyList<(string Id, string Sha256)> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        var local = PostgreSqlMigrationCatalog.All;
        if (applied.Count > local.Count)
            throw HistoryFailure("The database contains migrations newer than this provider.");
        for (var index = 0; index < applied.Count; index++)
        {
            if (!string.Equals(applied[index].Id, local[index].Id, StringComparison.Ordinal) ||
                !string.Equals(applied[index].Sha256, local[index].Sha256, StringComparison.Ordinal))
            {
                throw HistoryFailure("The migration ledger is unknown, skipped, reordered, or checksum-drifted.");
            }
        }
    }

    private static PostgreSqlPersistenceException HistoryFailure(string reason) =>
        new(
            "cortex_postgresql_migration_history_invalid",
            "cortex.postgresql.migrate.validate",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["reason"] = reason });

    private static async ValueTask<Exception?> TryRollbackAsync(DbTransaction transaction)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(cleanup.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
