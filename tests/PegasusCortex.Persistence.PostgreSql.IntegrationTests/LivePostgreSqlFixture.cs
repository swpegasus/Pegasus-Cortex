using System.Security.Cryptography;
using Npgsql;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Service.Persistence;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal static class LivePostgreSqlFixture
{
    private const string FixtureApplicationVersion = "pegasus-cortex-postgresql-integration";

    public static async Task RunPreflightAsync(
        FixtureConfiguration configuration,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(reportPass);
        cancellationToken.ThrowIfCancellationRequested();

        // Public Service validation reads and bounds the CA file before any socket is opened.
        configuration.PostgreSqlOptions.ValidateConfiguration();

        await using var migrationDataSource = BuildDataSource(
            configuration.MigrationConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-preflight-migration",
            readOnlySession: true);
        await VerifyMigrationConnectionAsync(migrationDataSource, configuration, cancellationToken)
            .ConfigureAwait(false);
        reportPass("migration transport, database ownership, and role identity");

        await VerifyPristineDatabaseAsync(migrationDataSource, cancellationToken).ConfigureAwait(false);
        reportPass("pristine database contains no cortex_* schema or migration ledger");

        await VerifyRuntimeConnectionAsync(
                configuration,
                readOnlySession: true,
                cancellationToken)
            .ConfigureAwait(false);
        reportPass("runtime transport and restricted two-role identity");
    }

    public static async Task RunAsync(
        FixtureConfiguration configuration,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(reportPass);
        cancellationToken.ThrowIfCancellationRequested();

        await RunPreflightAsync(configuration, reportPass, cancellationToken).ConfigureAwait(false);

        await VerifyConcurrentAndIdempotentMigrationsAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        reportPass("Service-owned concurrent migration and sequential idempotency");

        var syncEvidence = await FixtureSyncLiveWorkflow.RunAsync(
                configuration,
                reportPass,
                cancellationToken)
            .ConfigureAwait(false);

        await FixtureRetentionLiveWorkflow.RunAsync(
                configuration,
                reportPass,
                cancellationToken)
            .ConfigureAwait(false);

        await FixturePublicationChangeLiveWorkflow.RunAsync(
                configuration,
                reportPass,
                cancellationToken)
            .ConfigureAwait(false);

        await VerifyUnseededReadinessFailsClosedAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        reportPass("Service readiness fails closed for the pristine unseeded store");

        if (configuration.BackupRestoreMode == FixtureBackupRestoreMode.Capture)
        {
            var proof = FixtureBackupRestoreProofContract.Create(
                configuration.DatabaseName,
                syncEvidence);
            FixtureBackupRestoreProofFile.WriteNew(configuration.BackupRestoreProofLocation!, proof);
            reportPass("canonical backup/restore proof captured for external orchestration");
        }
    }

    private static async Task VerifyConcurrentAndIdempotentMigrationsAsync(
        FixtureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var firstSession = CortexPostgreSqlMigrationSession.Create(
            configuration.PostgreSqlOptions);
        await using var secondSession = CortexPostgreSqlMigrationSession.Create(
            configuration.PostgreSqlOptions);
        await using var ledgerDataSource = BuildDataSource(
            configuration.MigrationConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-ledger");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ApplyAfterReleaseAsync(firstSession, release.Task, cancellationToken);
        var second = ApplyAfterReleaseAsync(secondSession, release.Task, cancellationToken);
        release.SetResult();

        var concurrentResults = await Task.WhenAll(first, second).ConfigureAwait(false);
        var expectedIds = PostgreSqlMigrationCatalog.All.Select(migration => migration.Id).ToArray();
        FixtureAssert.True(expectedIds.Length > 0, "The local migration catalog is empty.");
        FixtureAssert.Equal(
            1,
            concurrentResults.Count(result => result.AppliedMigrationIds.Count == expectedIds.Length),
            "Concurrent Service migrators did not produce exactly one migration owner.");
        FixtureAssert.Equal(
            1,
            concurrentResults.Count(result => result.AppliedMigrationIds.Count == 0),
            "The waiting Service migrator did not observe the committed migration history.");
        FixtureAssert.Equal(
            expectedIds.Length,
            concurrentResults.Sum(result => result.AppliedMigrationIds.Count),
            "Concurrent Service migrators applied an unexpected total number of migrations.");
        foreach (var result in concurrentResults)
        {
            FixtureAssert.Equal(
                PostgreSqlMigrationCatalog.LatestId,
                result.CurrentMigrationId,
                "A concurrent Service migrator reported a non-current migration.");
            if (result.AppliedMigrationIds.Count > 0)
            {
                FixtureAssert.SequenceEqual(
                    expectedIds,
                    result.AppliedMigrationIds,
                    "The migration owner did not apply the exact ordered catalog.");
            }
        }

        var sequential = await firstSession.Migrator
            .ApplyAsync(FixtureApplicationVersion, cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.Equal(
            0,
            sequential.AppliedMigrationIds.Count,
            "A sequential Service migration rerun was not idempotent.");
        FixtureAssert.Equal(
            PostgreSqlMigrationCatalog.LatestId,
            sequential.CurrentMigrationId,
            "The sequential Service migration rerun reported a non-current migration.");

        var actualLedger = await ReadMigrationLedgerAsync(ledgerDataSource, cancellationToken)
            .ConfigureAwait(false);
        var expectedLedger = PostgreSqlMigrationCatalog.All
            .Select(migration => (migration.Id, migration.Sha256))
            .ToArray();
        FixtureAssert.SequenceEqual(
            expectedLedger,
            actualLedger,
            "The persisted migration ledger does not exactly match the local catalog.");

        await FixtureRuntimePrivilegeBootstrap.ApplyAndVerifyAsync(
                ledgerDataSource,
                configuration,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<PostgreSqlMigrationResult> ApplyAfterReleaseAsync(
        CortexPostgreSqlMigrationSession session,
        Task release,
        CancellationToken cancellationToken)
    {
        await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await session.Migrator
            .ApplyAsync(FixtureApplicationVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<(string Id, string Sha256)>> ReadMigrationLedgerAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT migration_id, up_sha256
            FROM cortex_meta.schema_migrations
            ORDER BY sequence_number;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<(string Id, string Sha256)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            FixtureAssert.True(values.Count < 1024, "The migration ledger exceeded the fixture bound.");
            values.Add((reader.GetString(0), reader.GetString(1)));
        }

        return values;
    }

    private static async Task VerifyPristineDatabaseAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('cortex_meta.schema_migrations') IS NOT NULL,
                EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_namespace
                    WHERE nspname LIKE 'cortex\_%' ESCAPE '\'
                );
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The pristine fixture preflight returned no row.");
        FixtureAssert.True(
            !reader.GetBoolean(0) && !reader.GetBoolean(1),
            "The externally owned fixture database is not pristine; supply a new ctx_it_ database.");
        FixtureAssert.True(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The pristine fixture preflight returned more than one row.");
    }

    internal static async Task VerifyMigrationConnectionAsync(
        NpgsqlDataSource dataSource,
        FixtureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                COALESCE((SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_backend_pid()), false),
                current_database(),
                current_user,
                session_user,
                role_value.rolcanlogin,
                database_value.datdba = role_value.oid,
                current_setting('server_version_num')::integer >= 140000,
                current_setting('transaction_read_only') = 'on'
            FROM pg_catalog.pg_roles role_value
            JOIN pg_catalog.pg_database database_value
              ON database_value.datname = current_database()
            WHERE role_value.rolname = current_user;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The migration role preflight returned no row.");
        FixtureAssert.Equal(
            configuration.UsesTls,
            reader.GetBoolean(0),
            "The migration database transport differs from the selected fixture policy.");
        FixtureAssert.True(
            string.Equals(reader.GetString(1), configuration.DatabaseName, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), configuration.MigrationUsername, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), reader.GetString(3), StringComparison.Ordinal) &&
            reader.GetBoolean(4) &&
            reader.GetBoolean(5) &&
            reader.GetBoolean(6) &&
            reader.GetBoolean(7),
            "The migration preflight is not a read-only PostgreSQL 14+ database-owner session.");
        FixtureAssert.True(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The migration role preflight returned more than one row.");
    }

    internal static async Task VerifyRuntimeConnectionAsync(
        FixtureConfiguration configuration,
        bool readOnlySession,
        CancellationToken cancellationToken)
    {
        await using var dataSource = BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-runtime-smoke",
            readOnlySession);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                COALESCE((SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()), false),
                current_database(),
                current_user,
                session_user,
                role_value.rolsuper,
                role_value.rolcreatedb,
                role_value.rolcreaterole,
                role_value.rolreplication,
                role_value.rolbypassrls,
                role_value.rolcanlogin,
                role_value.rolinherit,
                NOT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_auth_members membership
                    WHERE membership.member = role_value.oid
                       OR membership.roleid = role_value.oid
                ),
                current_setting('server_version_num')::integer >= 140000,
                current_setting('transaction_read_only') = 'on'
            FROM pg_roles role_value
            WHERE role_value.rolname = current_user;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The runtime role query returned no row.");
        FixtureAssert.Equal(
            configuration.UsesTls,
            reader.GetBoolean(0),
            "The runtime database transport differs from the selected fixture policy.");
        FixtureAssert.Equal(
            configuration.DatabaseName,
            reader.GetString(1),
            "The runtime connection reached an unexpected database.");
        FixtureAssert.Equal(
            configuration.RuntimeUsername,
            reader.GetString(2),
            "The runtime connection authenticated as an unexpected role.");
        FixtureAssert.Equal(
            reader.GetString(2),
            reader.GetString(3),
            "The runtime session changed role after authentication.");
        FixtureAssert.True(
            Enumerable.Range(4, 5).All(index => !reader.GetBoolean(index)) &&
            reader.GetBoolean(9) &&
            !reader.GetBoolean(10) &&
            reader.GetBoolean(11) &&
            reader.GetBoolean(12) &&
            reader.GetBoolean(13),
            "The runtime preflight is not a read-only PostgreSQL 14+ restricted role session.");
        FixtureAssert.True(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The runtime role query returned more than one row.");
    }

    private static async Task VerifyUnseededReadinessFailsClosedAsync(
        FixtureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var authenticator = CreateFixtureAuthenticator();
        await using var composition = CortexPostgreSqlComposition.Create(
            configuration.PostgreSqlOptions,
            authenticator);
        var scope = new CortexQuerySourceScope(
            TenantId: "pegasus-cortex-integration",
            AuthorityId: "fixture-authority",
            SubjectId: "fixture-subject",
            ServiceId: "pegasus-cortex-integration",
            PolicyRevision: 1,
            Namespace: "pegasus.integration.unseeded",
            Capability: CortexQueryCapability.Status,
            Operation: "cortex.query.status",
            MaximumSensitivity: "restricted",
            IncludeStale: false,
            IncludeBlockedOrUnverified: false);
        var infrastructure = await composition.ProbeReadinessAsync(
                scope,
                requireStoreSentinel: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.True(
            infrastructure.State == PostgreSqlReadinessState.Ready &&
            infrastructure.ServerMajor >= 14 &&
            infrastructure.TlsActive == configuration.UsesTls &&
            infrastructure.HostTlsPolicyVerified &&
            infrastructure.RuntimeRoleRestricted &&
            infrastructure.ContextClean &&
            infrastructure.MigrationsCurrent &&
            infrastructure.RowLevelSecurityForced &&
            infrastructure.StoreSentinelVerified &&
            infrastructure.IndexStateVerified &&
            infrastructure.IssueCodes.Count == 0,
            "Infrastructure-only readiness did not preserve the exact positive migration, role, transport, and RLS contract.");
        FixtureAssert.True(
            composition.Admission.State == CortexPostgreSqlAdmissionState.Accepting,
            "A fully verified infrastructure-only readiness result did not open admission.");

        var result = await composition.ProbeReadinessAsync(
                scope,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        FixtureAssert.True(
            result.State == PostgreSqlReadinessState.NotReady,
            "An unseeded pristine store did not fail readiness closed.");
        FixtureAssert.True(
            result.ServerMajor >= 14 &&
            result.TlsActive == configuration.UsesTls &&
            result.HostTlsPolicyVerified &&
            result.RuntimeRoleRestricted &&
            result.ContextClean &&
            result.MigrationsCurrent &&
            result.RowLevelSecurityForced,
            "Scoped unseeded readiness lost transport, migration, runtime-role, context, or exact RLS evidence.");
        FixtureAssert.True(
            !result.StoreSentinelVerified,
            "An unseeded pristine store reported a verified store sentinel.");
        FixtureAssert.True(
            !result.IndexStateVerified,
            "An unseeded pristine store reported verified index state.");
        var expectedIssues = new HashSet<string>(StringComparer.Ordinal)
        {
            "cortex_postgresql_store_sentinel_unverified",
            "cortex_postgresql_index_state_unverified",
            "cortex_postgresql_store_sentinel_public_unverified",
            "cortex_postgresql_store_sentinel_internal_unverified",
            "cortex_postgresql_store_sentinel_confidential_unverified",
            "cortex_postgresql_store_sentinel_restricted_unverified",
            "cortex_postgresql_index_state_public_unverified",
            "cortex_postgresql_index_state_internal_unverified",
            "cortex_postgresql_index_state_confidential_unverified",
            "cortex_postgresql_index_state_restricted_unverified"
        };
        FixtureAssert.True(
            result.IssueCodes.Count > 0 &&
            result.IssueCodes.Contains("cortex_postgresql_store_sentinel_unverified", StringComparer.Ordinal) &&
            result.IssueCodes.Contains("cortex_postgresql_index_state_unverified", StringComparer.Ordinal) &&
            result.IssueCodes.All(expectedIssues.Contains),
            "Unseeded readiness omitted its sentinel/index failures or returned an unexpected error.");
        FixtureAssert.True(
            composition.Admission.State == CortexPostgreSqlAdmissionState.AwaitingReadiness,
            "A failed readiness probe opened the Service admission boundary.");
    }

    private static TestHmacRowAuthenticator CreateFixtureAuthenticator()
    {
        Span<byte> key = stackalloc byte[32];
        key.Fill(0xA5);
        try
        {
            return new TestHmacRowAuthenticator("fixture-only", key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static NpgsqlDataSource BuildDataSource(
        string connectionString,
        string rootCertificatePath,
        string name,
        bool readOnlySession = false)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = name,
            IncludeErrorDetail = false,
            LogParameters = false,
            PersistSecurityInfo = false,
            Enlist = false,
            Multiplexing = false
        };
        if (string.IsNullOrEmpty(rootCertificatePath))
        {
            settings.SslMode = SslMode.Disable;
            settings.Remove("Root Certificate");
            settings.Remove("SSL Certificate");
            settings.Remove("SSL Key");
            settings.Remove("SSL Password");
            settings.Remove("Check Certificate Revocation");
        }
        else
        {
            settings.SslMode = SslMode.VerifyFull;
            settings.RootCertificate = rootCertificatePath;
            settings.CheckCertificateRevocation = true;
        }
        if (readOnlySession)
            settings.Options = "-c default_transaction_read_only=on";
        settings.Remove("Trust Server Certificate");
        settings.Remove("SSL Negotiation");
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString)
        {
            Name = name
        };
        return builder.Build();
    }
}
