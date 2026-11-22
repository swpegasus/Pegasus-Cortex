using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Learning;
using Pegasus.Cortex.Persistence.PostgreSql.Hybrid;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Security;
using Pegasus.Cortex.Persistence.PostgreSql.Sync;
using Pegasus.Cortex.Service.Configuration;
using Pegasus.Cortex.Service.Diagnostics;
using Npgsql;

namespace Pegasus.Cortex.Service.Persistence;

public enum CortexPostgreSqlAdmissionState
{
    AwaitingReadiness,
    Accepting,
    DataSourcePoisoned,
    Disposed
}

public sealed record CortexPostgreSqlAdmissionSnapshot(
    CortexPostgreSqlAdmissionState State,
    string? IssueCode)
{
    public bool CanAcceptWork => State == CortexPostgreSqlAdmissionState.Accepting;
}

public sealed class CortexPostgreSqlAdmissionException : InvalidOperationException
{
    internal CortexPostgreSqlAdmissionException(string code, string operation)
        : base($"Pegasus Cortex PostgreSQL admission failed with code '{code}'.")
    {
        Code = code;
        Operation = operation;
    }

    public string Code { get; }
    public string Operation { get; }
}

/// <summary>
/// Owns the process-lifetime runtime data source and creates driver-neutral provider
/// adapters. The host must supply a production <see cref="ICortexRowAuthenticator"/> backed
/// by its protected key lifecycle; this composition never creates, stores, or fakes row keys.
/// </summary>
public sealed class CortexPostgreSqlComposition : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly CortexPostgreSqlConfigurationSummary _configuration;
    private readonly CortexPostgreSqlTlsPolicy _tlsPolicy;
    private readonly ICortexRowAuthenticator _rowAuthenticator;
    private readonly NpgsqlDataSource _runtimeDataSource;
    private readonly PostgreSqlTransactionRunner _transactions;
    private Task? _disposeTask;
    private int _disposed;
    private int _readinessConfirmed;
    private int _readinessProbeActive;
    private long _readinessEpoch;
    private int _syncReadinessConfirmed;
    private int _syncReadinessProbeActive;
    private long _syncReadinessEpoch;

    private CortexPostgreSqlComposition(
        CortexPostgreSqlConfigurationSummary configuration,
        CortexPostgreSqlTlsPolicy tlsPolicy,
        ICortexRowAuthenticator rowAuthenticator,
        NpgsqlDataSource runtimeDataSource,
        IPostgreSqlDataSourceFaultSink? faultSink)
    {
        _configuration = configuration;
        _tlsPolicy = tlsPolicy;
        _rowAuthenticator = rowAuthenticator;
        _runtimeDataSource = runtimeDataSource;
        _transactions = new PostgreSqlTransactionRunner(_runtimeDataSource, faultSink);
    }

    /// <summary>
    /// Creates the runtime composition without opening a database connection.
    /// </summary>
    public static CortexPostgreSqlComposition Create(
        CortexPostgreSqlOptions options,
        ICortexRowAuthenticator rowAuthenticator,
        ICortexPostgreSqlDiagnosticSink? diagnosticSink = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rowAuthenticator);

        // Runtime composition must remain usable after the privileged migration
        // credential has been removed from the service environment.
        var validated = options.ValidateRuntime();
        var dataSource = CortexPostgreSqlDataSourceFactory.CreateRuntime(validated);
        try
        {
            return new CortexPostgreSqlComposition(
                validated.Summary,
                validated.TlsPolicy,
                rowAuthenticator,
                dataSource,
                diagnosticSink is null ? null : new PostgreSqlFaultSink(diagnosticSink));
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    public CortexPostgreSqlConfigurationSummary Configuration => _configuration;

    public CortexPostgreSqlAdmissionSnapshot Admission
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new CortexPostgreSqlAdmissionSnapshot(
                    CortexPostgreSqlAdmissionState.Disposed,
                    "cortex_postgresql_composition_disposed");
            }

            if (_transactions.IsPoisoned)
            {
                return new CortexPostgreSqlAdmissionSnapshot(
                    CortexPostgreSqlAdmissionState.DataSourcePoisoned,
                    "cortex_postgresql_data_source_poisoned");
            }

            if (Volatile.Read(ref _readinessConfirmed) == 0)
            {
                return new CortexPostgreSqlAdmissionSnapshot(
                    CortexPostgreSqlAdmissionState.AwaitingReadiness,
                    "cortex_postgresql_awaiting_readiness");
            }

            return new CortexPostgreSqlAdmissionSnapshot(
                CortexPostgreSqlAdmissionState.Accepting,
                IssueCode: null);
        }
    }

    public CortexPostgreSqlAdmissionSnapshot SyncAdmission
    {
        get
        {
            var baseAdmission = Admission;
            if (!baseAdmission.CanAcceptWork)
                return baseAdmission;
            if (Volatile.Read(ref _syncReadinessConfirmed) == 0)
            {
                return new CortexPostgreSqlAdmissionSnapshot(
                    CortexPostgreSqlAdmissionState.AwaitingReadiness,
                    "cortex_postgresql_sync_awaiting_readiness");
            }
            return new CortexPostgreSqlAdmissionSnapshot(
                CortexPostgreSqlAdmissionState.Accepting,
                IssueCode: null);
        }
    }

    public ICortexQuerySource CreateQuerySource()
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.query.create");
            return new CortexPostgreSqlQueryAdmissionDecorator(
                new PostgreSqlCortexQuerySource(_transactions, _rowAuthenticator),
                EnsureAccepting);
        }
    }

    public ILearningFeedbackRepository CreateLearningRepository()
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.learning.create");
            return CortexPostgreSqlLearningAdmissionDecorator.Create(
                new PostgreSqlLearningFeedbackRepository(_transactions, _rowAuthenticator),
                EnsureAccepting);
        }
    }

    public ICortexProjectLinkRepository CreateProjectLinkRepository()
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.project-link.create");
            return new PostgreSqlProjectLinkRepository(_transactions, _rowAuthenticator);
        }
    }

    public ICortexSyncRepository CreateSyncRepository(TimeProvider? timeProvider = null)
    {
        lock (_lifecycleGate)
        {
            EnsureSyncAccepting("cortex.postgresql.sync.create");
            return CortexPostgreSqlSyncAdmissionDecorator.Create(
                new PostgreSqlCortexSyncRepository(_transactions, timeProvider),
                EnsureSyncAccepting);
        }
    }

    public ICortexAgentRetentionRepository CreateAgentRetentionRepository(
        PostgreSqlCortexRetentionScope scope,
        TimeProvider? timeProvider = null)
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.retention.create");
            return new CortexPostgreSqlRetentionAdmissionDecorator(
                new PostgreSqlCortexAgentRetentionRepository(
                    _transactions,
                    _rowAuthenticator,
                    scope,
                    timeProvider),
                EnsureAccepting);
        }
    }

    public ICortexProjectBriefHistoryReadModel CreateProjectBriefReadModel(
        TimeProvider? timeProvider = null)
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.project-brief.create");
            return new CortexPostgreSqlProjectBriefAdmissionDecorator(
                new PostgreSqlCortexProjectBriefStore(
                    _transactions,
                    _rowAuthenticator,
                    timeProvider),
                EnsureAccepting);
        }
    }

    public ValueTask<CortexRetentionAppendResult> SubmitProjectBriefSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        PostgreSqlCortexRetentionScope writeScope,
        string producerId,
        string idempotencyKey,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.project-brief.submit");
            return new PostgreSqlCortexProjectBriefStore(
                    _transactions,
                    _rowAuthenticator,
                    timeProvider)
                .SubmitSnapshotAsync(
                    snapshot,
                    writeScope,
                    producerId,
                    idempotencyKey,
                    cancellationToken);
        }
    }

    /// <summary>
    /// Runs a readiness probe whose transport attestation is derived from the validated
    /// composition policy rather than accepted as a caller-supplied Boolean. Admission is
    /// cleared before every attempt and confirmed only by a real Ready result. VerifyFull,
    /// the fail-closed pinned policy, and an explicitly selected trusted-loopback policy
    /// may admit work; loopback bootstrap can return at most Degraded.
    /// </summary>
    public ValueTask<PostgreSqlPersistenceReadiness> ProbeReadinessAsync(
        CortexQuerySourceScope sentinelScope,
        int minimumServerMajor = 14,
        IReadOnlyList<string>? allowedReachableRoleNames = null,
        bool requireStoreSentinel = true,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        ProbeReadinessCoreAsync(
            token => CreateProviderReadinessProbe(
                sentinelScope,
                minimumServerMajor,
                allowedReachableRoleNames,
                requireStoreSentinel,
                timeProvider).ProbeAsync(token),
            cancellationToken);

    /// <summary>
    /// Attests only the isolated sync schema. Base readiness must already be accepting and remains
    /// independent; a sync result can never manufacture global PostgreSQL admission.
    /// </summary>
    public ValueTask<PostgreSqlSyncReadiness> ProbeSyncReadinessAsync(
        CortexSyncRepositoryScope sentinelScope,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        ProbeSyncReadinessCoreAsync(
            token => CreateProviderSyncReadinessProbe(sentinelScope, timeProvider).ProbeAsync(token),
            cancellationToken);

    internal PostgreSqlReadinessOptions CreateReadinessOptions(
        int minimumServerMajor,
        IReadOnlyList<string>? allowedReachableRoleNames,
        bool requireStoreSentinel = true)
    {
        var requireTls = IsTlsVerifiedPolicy(_tlsPolicy);
        // TrustedLoopback attests its host boundary through literal loopback validation.
        // Bootstrap deliberately withholds that attestation so the provider stays degraded.
        var hostTransportPolicyVerified =
            requireTls || _tlsPolicy == CortexPostgreSqlTlsPolicy.TrustedLoopback;
        return new PostgreSqlReadinessOptions(
            minimumServerMajor,
            RequireTls: requireTls,
            HostTlsPolicyVerified: hostTransportPolicyVerified,
            allowedReachableRoleNames,
            requireStoreSentinel);
    }

    internal async ValueTask<PostgreSqlPersistenceReadiness> ProbeReadinessCoreAsync(
        Func<CancellationToken, ValueTask<PostgreSqlPersistenceReadiness>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var epoch = Interlocked.Increment(ref _readinessEpoch);
        Volatile.Write(ref _readinessConfirmed, 0);
        Interlocked.Increment(ref _syncReadinessEpoch);
        Volatile.Write(ref _syncReadinessConfirmed, 0);
        if (Interlocked.CompareExchange(ref _readinessProbeActive, 1, 0) != 0)
        {
            throw new CortexPostgreSqlAdmissionException(
                "cortex_postgresql_readiness_probe_in_progress",
                "cortex.postgresql.readiness.probe");
        }

        try
        {
            lock (_lifecycleGate)
                EnsureNotDisposed("cortex.postgresql.readiness.probe");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await probe(cancellationToken).ConfigureAwait(false);
            ConfirmReadiness(result.State, epoch);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _readinessProbeActive, 0);
        }
    }

    internal async ValueTask<PostgreSqlSyncReadiness> ProbeSyncReadinessCoreAsync(
        Func<CancellationToken, ValueTask<PostgreSqlSyncReadiness>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        EnsureAccepting("cortex.postgresql.sync.readiness.probe");
        var epoch = Interlocked.Increment(ref _syncReadinessEpoch);
        Volatile.Write(ref _syncReadinessConfirmed, 0);
        if (Interlocked.CompareExchange(ref _syncReadinessProbeActive, 1, 0) != 0)
        {
            throw new CortexPostgreSqlAdmissionException(
                "cortex_postgresql_sync_readiness_probe_in_progress",
                "cortex.postgresql.sync.readiness.probe");
        }

        try
        {
            lock (_lifecycleGate)
                EnsureAccepting("cortex.postgresql.sync.readiness.probe");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await probe(cancellationToken).ConfigureAwait(false);
            ConfirmSyncReadiness(result.State, epoch);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _syncReadinessProbeActive, 0);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _readinessConfirmed, 0);
            Volatile.Write(ref _syncReadinessConfirmed, 0);
            try
            {
                _disposeTask = _runtimeDataSource.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                _disposeTask = Task.FromException(exception);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private void EnsureAccepting(string operation)
    {
        var admission = Admission;
        if (!admission.CanAcceptWork)
        {
            throw new CortexPostgreSqlAdmissionException(
                admission.IssueCode ?? "cortex_postgresql_admission_denied",
                operation);
        }
    }

    private void EnsureSyncAccepting(string operation)
    {
        var admission = SyncAdmission;
        if (!admission.CanAcceptWork)
        {
            throw new CortexPostgreSqlAdmissionException(
                admission.IssueCode ?? "cortex_postgresql_sync_admission_denied",
                operation);
        }
    }

    private void EnsureNotDisposed(string operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new CortexPostgreSqlAdmissionException(
                "cortex_postgresql_composition_disposed",
                operation);
        }
    }

    private PostgreSqlPersistenceReadinessProbe CreateProviderReadinessProbe(
        CortexQuerySourceScope sentinelScope,
        int minimumServerMajor,
        IReadOnlyList<string>? allowedReachableRoleNames,
        bool requireStoreSentinel,
        TimeProvider? timeProvider)
    {
        lock (_lifecycleGate)
        {
            EnsureNotDisposed("cortex.postgresql.readiness.probe");
            return new PostgreSqlPersistenceReadinessProbe(
                _transactions,
                _rowAuthenticator,
                sentinelScope,
                CreateReadinessOptions(
                    minimumServerMajor,
                    allowedReachableRoleNames,
                    requireStoreSentinel),
                timeProvider);
        }
    }

    private PostgreSqlSyncReadinessProbe CreateProviderSyncReadinessProbe(
        CortexSyncRepositoryScope sentinelScope,
        TimeProvider? timeProvider)
    {
        lock (_lifecycleGate)
        {
            EnsureAccepting("cortex.postgresql.sync.readiness.probe");
            return new PostgreSqlSyncReadinessProbe(_transactions, sentinelScope, timeProvider);
        }
    }

    private void ConfirmReadiness(PostgreSqlReadinessState state, long epoch)
    {
        if (!IsReadinessAdmittingPolicy(_tlsPolicy) ||
            state != PostgreSqlReadinessState.Ready ||
            epoch != Volatile.Read(ref _readinessEpoch))
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) == 0 && !_transactions.IsPoisoned &&
                epoch == Volatile.Read(ref _readinessEpoch))
            {
                Volatile.Write(ref _readinessConfirmed, 1);
            }
        }
    }

    private void ConfirmSyncReadiness(PostgreSqlReadinessState state, long epoch)
    {
        if (!IsReadinessAdmittingPolicy(_tlsPolicy) ||
            state != PostgreSqlReadinessState.Ready ||
            epoch != Volatile.Read(ref _syncReadinessEpoch) ||
            !Admission.CanAcceptWork)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) == 0 && !_transactions.IsPoisoned &&
                Admission.CanAcceptWork && epoch == Volatile.Read(ref _syncReadinessEpoch))
            {
                Volatile.Write(ref _syncReadinessConfirmed, 1);
            }
        }
    }

    private static bool IsTlsVerifiedPolicy(CortexPostgreSqlTlsPolicy policy) =>
        policy is CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull or
            CortexPostgreSqlTlsPolicy.Pinned;

    private static bool IsReadinessAdmittingPolicy(CortexPostgreSqlTlsPolicy policy) =>
        IsTlsVerifiedPolicy(policy) ||
        policy == CortexPostgreSqlTlsPolicy.TrustedLoopback;
}
