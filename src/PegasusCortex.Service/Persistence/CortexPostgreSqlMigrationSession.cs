using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Service.Configuration;
using Npgsql;

namespace Pegasus.Cortex.Service.Persistence;

/// <summary>
/// Owns one short-lived, non-pooled migration data source. Callers must dispose the session
/// immediately after startup migration completes so no privileged migration pool or
/// connection is retained. The hosting product separately owns its protected settings.
/// </summary>
public sealed class CortexPostgreSqlMigrationSession : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlMigrator _migrator;
    private readonly CortexPostgreSqlRuntimePrivilegeProvisioner _runtimePrivilegeProvisioner;
    private Task? _disposeTask;
    private int _disposed;

    private CortexPostgreSqlMigrationSession(
        NpgsqlDataSource dataSource,
        string migrationRoleName,
        string runtimeRoleName)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _migrator = new PostgreSqlMigrator(_dataSource);
        _runtimePrivilegeProvisioner = new CortexPostgreSqlRuntimePrivilegeProvisioner(
            _dataSource,
            migrationRoleName,
            runtimeRoleName);
    }

    /// <summary>
    /// Creates a startup-only migration owner without attaching privileged settings to the
    /// process-lifetime runtime composition. Creation does not open a database connection.
    /// </summary>
    public static CortexPostgreSqlMigrationSession Create(CortexPostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validated = options.ValidateMigration();
        var dataSource = CortexPostgreSqlDataSourceFactory.CreateMigration(validated);
        try
        {
            var migrationRoleName =
                CortexPostgreSqlDataSourceFactory.CreateMigrationConnectionSettings(validated)
                    .Username ??
                throw new InvalidOperationException(
                    "cortex_postgresql_runtime_privilege_reconciliation_failed");
            var runtimeRoleName =
                CortexPostgreSqlDataSourceFactory.CreateRuntimeConnectionSettings(validated)
                    .Username ??
                throw new InvalidOperationException(
                    "cortex_postgresql_runtime_privilege_reconciliation_failed");
            return new CortexPostgreSqlMigrationSession(
                dataSource,
                migrationRoleName,
                runtimeRoleName);
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Gets the provider migrator while this session owns its data source. The migrator must
    /// not be retained or used after this session is disposed.
    /// </summary>
    public PostgreSqlMigrator Migrator
    {
        get
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return _migrator;
            }
        }
    }

    /// <summary>
    /// Reconciles the minimal runtime privilege required by the current schema. This remains
    /// startup-only and must run after migrations but before runtime readiness is accepted.
    /// </summary>
    public ValueTask ReconcileRuntimePrivilegesAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _runtimePrivilegeProvisioner.ReconcileAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            Volatile.Write(ref _disposed, 1);
            try
            {
                _disposeTask = _dataSource.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                _disposeTask = Task.FromException(exception);
            }

            return new ValueTask(_disposeTask);
        }
    }
}
