using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using Npgsql;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Learning;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Security;
using Pegasus.Cortex.Service.Configuration;
using Pegasus.Cortex.Service.Persistence;

namespace Pegasus.Cortex.Service.Tests;

internal static class CortexPostgreSqlCompositionTests
{
    private const string RuntimeSecret = "runtime-secret-value";
    private const string MigrationSecret = "migration-secret-value";

    internal static Task ValidationAndRedactionAsync()
    {
        using var certificate = TestRootCertificate.Create();
        var options = CreateOptions(certificate.Path);
        var summary = options.ValidateConfiguration();

        TestAssert.Equal(CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull, summary.TlsPolicy);
        TestAssert.Equal(8, summary.RuntimeMaximumPoolSize);
        TestAssert.DoesNotContain(RuntimeSecret, options.ToString(), "Runtime credentials leaked from options.");
        TestAssert.DoesNotContain(MigrationSecret, options.ToString(), "Migration credentials leaked from options.");
        TestAssert.DoesNotContain(certificate.Path, options.ToString(), "Certificate path leaked from options.");
        var validated = options.Validate();
        TestAssert.DoesNotContain(RuntimeSecret, validated.ToString(), "Runtime credentials leaked from validated options.");
        TestAssert.DoesNotContain(MigrationSecret, validated.ToString(), "Migration credentials leaked from validated options.");

        var pinned = CreateOptions(certificate.Path, tlsPolicy: CortexPostgreSqlTlsPolicy.Pinned);
        var pinnedFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => pinned.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_server_spki_pin_invalid", pinnedFailure.Code);

        var sharedRole = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret),
            ConnectionString("cortex_runtime", MigrationSecret),
            certificate.Path);
        var roleFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => sharedRole.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_roles_not_separated", roleFailure.Code);
        TestAssert.DoesNotContain(RuntimeSecret, roleFailure.ToString(), "Runtime secret leaked in failure.");
        TestAssert.DoesNotContain(MigrationSecret, roleFailure.ToString(), "Migration secret leaked in failure.");

        TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            CreateOptions(certificate.Path, maximumPoolSize: 0).ValidateConfiguration());
        TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            CreateOptions(certificate.Path, maximumPoolSize: 33).ValidateConfiguration());
        TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            CreateOptions(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pem"))
                .ValidateConfiguration());
        var oversizedConnection = new string('x', CortexPostgreSqlOptions.MaximumConnectionStringCharacters + 1);
        var oversized = new CortexPostgreSqlOptions(
            oversizedConnection,
            ConnectionString("cortex_migrator", MigrationSecret),
            certificate.Path);
        TestAssert.Throws<CortexPostgreSqlConfigurationException>(() => oversized.ValidateConfiguration());

        using var malformedCertificate = TestFile.Create("not-a-certificate"u8.ToArray());
        var malformed = CreateOptions(malformedCertificate.Path);
        var malformedFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => malformed.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_root_certificate_invalid", malformedFailure.Code);

        using var privateKeyFile = TestFile.Create(
            "-----BEGIN PRIVATE KEY-----\nnot-key-material\n-----END PRIVATE KEY-----"u8.ToArray());
        var privateKeyFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => CreateOptions(privateKeyFile.Path).ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_root_certificate_invalid", privateKeyFailure.Code);

        using var oversizedCertificate = TestFile.Create(
            new byte[CortexPostgreSqlOptions.MaximumRootCertificateBytes + 1]);
        var oversizedCertificateOptions = CreateOptions(oversizedCertificate.Path);
        var oversizedCertificateFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => oversizedCertificateOptions.ValidateConfiguration());
        TestAssert.Equal(
            "cortex_postgresql_root_certificate_size_invalid",
            oversizedCertificateFailure.Code);
        return Task.CompletedTask;
    }

    internal static async Task RuntimeWithoutMigrationCredentialsAsync()
    {
        var options = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret),
            migrationConnectionString: null,
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap);

        var summary = options.ValidateRuntimeConfiguration();
        TestAssert.Equal(
            CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap,
            summary.TlsPolicy);

        await using var composition = CortexPostgreSqlComposition.Create(
            options,
            new TestRowAuthenticator());
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);

        var migrationFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => CortexPostgreSqlMigrationSession.Create(options));
        TestAssert.Equal(
            "cortex_postgresql_migration_connection_invalid",
            migrationFailure.Code);
        TestAssert.DoesNotContain(
            RuntimeSecret,
            migrationFailure.ToString(),
            "Runtime credentials leaked from missing-migration diagnostics.");
    }

    internal static async Task PinnedTlsPolicyAsync()
    {
        using var fixture = PinnedCertificateFixture.Create();
        var options = CreateOptions(
            fixture.RootPath,
            tlsPolicy: CortexPostgreSqlTlsPolicy.Pinned,
            serverSpkiPin: fixture.LeafSpkiSha256,
            host: "localhost");
        var validated = options.Validate();
        TestAssert.True(
            validated.Summary.RootCertificateConfigured,
            "Pinned TLS must report its protected private-CA input.");
        TestAssert.True(
            validated.Summary.ServerCertificatePinConfigured,
            "Pinned TLS must report its protected SPKI input.");
        TestAssert.DoesNotContain(
            fixture.LeafSpkiSha256,
            options.ToString(),
            "The exact server pin leaked through the sanitized summary.");

        var runtime = CortexPostgreSqlDataSourceFactory.CreateRuntimeConnectionSettings(validated);
        var migration = CortexPostgreSqlDataSourceFactory.CreateMigrationConnectionSettings(validated);
        AssertPinnedDefaults(runtime);
        AssertPinnedDefaults(migration);

        var callback = CortexPinnedPostgreSqlCertificateValidator.Create(
            fixture.RootPath,
            "localhost",
            fixture.LeafSpkiSha256);
        TestAssert.True(
            callback(
                null!,
                fixture.ValidLeaf,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors),
            "A current server-auth leaf with the exact CA, SAN, and SPKI was rejected.");
        TestAssert.False(
            callback(null!, null, null, SslPolicyErrors.RemoteCertificateNotAvailable),
            "A missing server certificate was accepted.");

        var wrongPin = new string('0', CortexPostgreSqlOptions.ServerCertificateSpkiSha256Characters);
        var wrongPinCallback = CortexPinnedPostgreSqlCertificateValidator.Create(
            fixture.RootPath,
            "localhost",
            wrongPin);
        TestAssert.False(
            wrongPinCallback(null!, fixture.ValidLeaf, null, SslPolicyErrors.None),
            "A server certificate with the wrong SPKI pin was accepted.");

        var wrongHostCallback = CortexPinnedPostgreSqlCertificateValidator.Create(
            fixture.RootPath,
            "127.0.0.1",
            fixture.LeafSpkiSha256);
        TestAssert.False(
            wrongHostCallback(null!, fixture.ValidLeaf, null, SslPolicyErrors.None),
            "A server certificate without the exact SAN host was accepted.");

        using var otherFixture = PinnedCertificateFixture.Create();
        var wrongRootCallback = CortexPinnedPostgreSqlCertificateValidator.Create(
            otherFixture.RootPath,
            "localhost",
            fixture.LeafSpkiSha256);
        TestAssert.False(
            wrongRootCallback(null!, fixture.ValidLeaf, null, SslPolicyErrors.None),
            "A pinned leaf outside the configured private-CA chain was accepted.");
        TestAssert.False(
            callback(null!, fixture.ExpiredLeaf, null, SslPolicyErrors.None),
            "An expired pinned leaf was accepted.");
        TestAssert.False(
            callback(null!, fixture.NoServerAuthenticationLeaf, null, SslPolicyErrors.None),
            "A pinned leaf without server-auth purpose was accepted.");

        var uppercasePin = fixture.LeafSpkiSha256.ToUpperInvariant();
        var uppercaseFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            CreateOptions(
                fixture.RootPath,
                tlsPolicy: CortexPostgreSqlTlsPolicy.Pinned,
                serverSpkiPin: uppercasePin,
                host: "localhost").ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_server_spki_pin_invalid", uppercaseFailure.Code);

        var remoteFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            CreateOptions(
                fixture.RootPath,
                tlsPolicy: CortexPostgreSqlTlsPolicy.Pinned,
                serverSpkiPin: fixture.LeafSpkiSha256,
                host: "example.com").ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_pinned_loopback_host_invalid", remoteFailure.Code);

        await using var composition = CortexPostgreSqlComposition.Create(
            options,
            new TestRowAuthenticator());
        var readiness = composition.CreateReadinessOptions(14, allowedReachableRoleNames: null);
        TestAssert.True(readiness.RequireTls, "Pinned TLS readiness did not require TLS.");
        TestAssert.True(
            readiness.HostTlsPolicyVerified,
            "Pinned TLS readiness did not attest the host validation policy.");
    }

    internal static async Task TrustedLoopbackAsync()
    {
        var options = new CortexPostgreSqlOptions(
            ConnectionString(
                "cortex_runtime",
                RuntimeSecret,
                "127.0.0.1",
                "Root Certificate=ignored-root.pem;SSL Certificate=ignored-client.pem;" +
                "SSL Key=ignored-client.key;SSL Password=ignored-tls-secret"),
            ConnectionString("cortex_migrator", MigrationSecret, "::1"),
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.TrustedLoopback);
        var validated = options.Validate();
        TestAssert.Equal(CortexPostgreSqlTlsPolicy.TrustedLoopback, validated.Summary.TlsPolicy);
        TestAssert.False(
            validated.Summary.RootCertificateConfigured,
            "Trusted loopback must not claim a TLS root certificate.");
        AssertTlsDisabledDefaults(
            CortexPostgreSqlDataSourceFactory.CreateRuntimeConnectionSettings(validated),
            "Trusted loopback");
        AssertTlsDisabledDefaults(
            CortexPostgreSqlDataSourceFactory.CreateMigrationConnectionSettings(validated),
            "Trusted loopback");

        await using var composition = CortexPostgreSqlComposition.Create(
            options,
            new TestRowAuthenticator());
        var readiness = composition.CreateReadinessOptions(14, allowedReachableRoleNames: null);
        TestAssert.False(readiness.RequireTls, "Trusted loopback must not claim TLS.");
        TestAssert.True(
            readiness.HostTlsPolicyVerified,
            "Trusted loopback did not attest its literal-loopback host policy.");
        await composition.ProbeReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateReadinessResult(
                PostgreSqlReadinessState.Ready,
                tlsActive: false,
                hostTlsPolicyVerified: true)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.Admission.State);
        await composition.ProbeSyncReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateSyncReadinessResult(PostgreSqlReadinessState.Ready)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.SyncAdmission.State);

        var remote = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret, "127.0.0.1,example.com"),
            ConnectionString("cortex_migrator", MigrationSecret, "127.0.0.1"),
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.TrustedLoopback);
        var remoteFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => remote.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_trusted_loopback_host_invalid", remoteFailure.Code);

        var alias = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret, "localhost"),
            ConnectionString("cortex_migrator", MigrationSecret, "127.0.0.1"),
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.TrustedLoopback);
        var aliasFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => alias.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_trusted_loopback_host_invalid", aliasFailure.Code);

        TestAssert.Equal(
            CortexPostgreSqlTlsPolicy.TrustedLoopback,
            CortexPostgreSqlMigrationCommand.ParseTlsPolicy("trusted-loopback"));
        using var migrationOutput = new StringWriter();
        using var migrationDiagnostics = new StringWriter();
        var migrationEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CortexPostgreSqlMigrationCommand.RuntimeConnectionVariable] =
                ConnectionString("cortex_runtime", RuntimeSecret, "localhost"),
            [CortexPostgreSqlMigrationCommand.MigrationConnectionVariable] =
                ConnectionString("cortex_migrator", MigrationSecret, "127.0.0.1"),
            [CortexPostgreSqlMigrationCommand.ApplicationVersionVariable] = "1.0.0",
            [CortexPostgreSqlMigrationCommand.TlsPolicyVariable] = "trusted-loopback"
        };
        var migrationStatus = await CortexPostgreSqlMigrationCommand.RunAsync(
            migrationOutput,
            migrationDiagnostics,
            name => migrationEnvironment.GetValueOrDefault(name),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(4, migrationStatus);
        TestAssert.Contains(
            "cortex_postgresql_trusted_loopback_host_invalid",
            migrationDiagnostics.ToString(),
            "Trusted-loopback migration parsing still required a root CA or reached the network.");
    }

    internal static Task ConnectionDefaultsAsync()
    {
        using var certificate = TestRootCertificate.Create();
        var validated = CreateOptions(certificate.Path).Validate();
        var runtime = CortexPostgreSqlDataSourceFactory.CreateRuntimeConnectionSettings(validated);
        var migration = CortexPostgreSqlDataSourceFactory.CreateMigrationConnectionSettings(validated);

        AssertSecureDefaults(
            runtime,
            certificate.Path,
            CortexPostgreSqlDataSourceFactory.RuntimeDataSourceName,
            commandTimeout: 30);
        AssertSecureDefaults(
            migration,
            certificate.Path,
            CortexPostgreSqlDataSourceFactory.MigrationDataSourceName,
            commandTimeout: 120);
        TestAssert.True(runtime.Pooling, "Runtime pooling must be enabled.");
        TestAssert.Equal(0, runtime.MinPoolSize);
        TestAssert.Equal(8, runtime.MaxPoolSize);
        TestAssert.Equal(300, runtime.ConnectionIdleLifetime);
        TestAssert.Equal(10, runtime.ConnectionPruningInterval);
        TestAssert.Equal(3600, runtime.ConnectionLifetime);
        TestAssert.False(migration.Pooling, "Migration pooling must be disabled.");
        TestAssert.Equal(1, migration.MaxPoolSize);
        TestAssert.Equal(120, migration.CommandTimeout);
        TestAssert.False(
            string.Equals(runtime.Username, migration.Username, StringComparison.OrdinalIgnoreCase),
            "Runtime and migration roles must be distinct.");
        return Task.CompletedTask;
    }

    internal static Task RuntimePrivilegeContractAsync()
    {
        const string roleName = "runtime\"role";
        const string databaseName = "cortex\"database";
        var grant = CortexPostgreSqlRuntimePrivilegeProvisioner.BuildGrantSql(
            roleName,
            databaseName);
        TestAssert.Contains(
            "REVOKE ALL PRIVILEGES ON DATABASE \"cortex\"\"database\" FROM PUBLIC;",
            grant,
            "Privilege reconciliation omitted the database public-access reset.");
        TestAssert.Contains(
            "GRANT CONNECT ON DATABASE \"cortex\"\"database\" TO \"runtime\"\"role\";",
            grant,
            "Privilege reconciliation omitted exact database access.");
        TestAssert.Contains(
            "GRANT USAGE ON SCHEMA \"cortex_meta\", \"cortex_data\", \"cortex_index\", \"cortex_private\", \"cortex_sync\" TO \"runtime\"\"role\";",
            grant,
            "Privilege reconciliation omitted exact schema usage.");
        foreach (var table in CortexPostgreSqlRuntimePrivilegeProvisioner.ReadTables)
            TestAssert.Contains(
                string.Join('.', table.Split('.').Select(segment => $"\"{segment}\"")),
                grant,
                $"Read access omitted {table}.");
        foreach (var table in CortexPostgreSqlRuntimePrivilegeProvisioner.AppendTables)
            TestAssert.Contains(
                string.Join('.', table.Split('.').Select(segment => $"\"{segment}\"")),
                grant,
                $"Append access omitted {table}.");
        foreach (var function in CortexPostgreSqlRuntimePrivilegeProvisioner.RequiredFunctions)
        {
            TestAssert.Contains(
                function,
                grant,
                $"Privilege reconciliation omitted required function {function}.");
        }
        foreach (var table in CortexPostgreSqlRuntimePrivilegeProvisioner.ProtectedTables)
        {
            var quoted = string.Join(
                '.',
                table.Split('.').Select(segment => $"\"{segment}\""));
            TestAssert.Contains(
                quoted,
                grant,
                $"Privilege reconciliation omitted protected table {table}.");
            TestAssert.False(
                CortexPostgreSqlRuntimePrivilegeProvisioner.ReadTables.Contains(table),
                $"Protected table {table} received direct SELECT.");
            TestAssert.False(
                CortexPostgreSqlRuntimePrivilegeProvisioner.AppendTables.Contains(table),
                $"Protected table {table} received direct INSERT.");
        }
        TestAssert.DoesNotContain("GRANT ALL", grant, "Privilege reconciliation grants ALL.");
        TestAssert.DoesNotContain("UPDATE", grant, "Privilege reconciliation grants UPDATE.");
        TestAssert.DoesNotContain("DELETE", grant, "Privilege reconciliation grants DELETE.");
        TestAssert.Contains(
            "pg_advisory_xact_lock(@lock_key)",
            CortexPostgreSqlRuntimePrivilegeProvisioner.AdvisoryLockSql,
            "Privilege reconciliation is not serialized by a transaction advisory lock.");
        TestAssert.Contains(
            "session_user = current_user",
            CortexPostgreSqlRuntimePrivilegeProvisioner.RoleAttestationSql,
            "Privilege reconciliation permits a hidden SET ROLE boundary.");
        TestAssert.Contains(
            "session_user = @migration_role",
            CortexPostgreSqlRuntimePrivilegeProvisioner.RoleAttestationSql,
            "Privilege reconciliation does not attest the configured migration role.");
        foreach (var prohibitedAttribute in new[]
                 {
                     "rolsuper",
                     "rolbypassrls",
                     "rolcreaterole",
                     "rolcreatedb",
                     "rolreplication"
                 })
        {
            TestAssert.Contains(
                prohibitedAttribute,
                CortexPostgreSqlRuntimePrivilegeProvisioner.RoleAttestationSql,
                $"Privilege reconciliation does not reject {prohibitedAttribute}.");
        }
        foreach (var requiredAttestation in new[]
                 {
                     "rolcanlogin",
                     "rolinherit",
                     "pg_auth_members",
                     "membership_is_empty"
                 })
        {
            TestAssert.Contains(
                requiredAttestation,
                CortexPostgreSqlRuntimePrivilegeProvisioner.RoleAttestationSql,
                $"Privilege reconciliation omits runtime-role attestation {requiredAttestation}.");
        }

        var verification = CortexPostgreSqlRuntimePrivilegeProvisioner.VerificationSql;
        foreach (var required in new[]
                 {
                     "@database_name",
                     "@schemas",
                     "@read_tables",
                     "@append_tables",
                     "@protected_tables",
                     "@required_functions"
                 })
        {
            TestAssert.Contains(
                required,
                verification,
                $"Privilege reconciliation does not verify {required}.");
        }
        foreach (var required in new[] { "'SELECT'", "'INSERT'" })
        {
            TestAssert.Contains(
                required,
                verification,
                $"Privilege reconciliation does not verify required {required} access.");
        }

        foreach (var functionContractAttestation in new[]
                 {
                     "privileged_function_contract_check",
                     "pg_catalog.pg_get_functiondef",
                     "function_value.prosecdef",
                     "function_value.proconfig",
                     "owner_role.rolname",
                     "installed_function.function_definition IS DISTINCT FROM"
                 })
        {
            TestAssert.Contains(
                functionContractAttestation,
                verification,
                $"Privilege reconciliation omits {functionContractAttestation}.");
        }

        foreach (var prohibited in new[]
                 {
                     "'UPDATE'",
                     "'DELETE'",
                     "'TRUNCATE'",
                     "'REFERENCES'",
                     "'TRIGGER'",
                     "'MAINTAIN'"
                 })
        {
            TestAssert.Contains(
                prohibited,
                verification,
                $"Privilege reconciliation does not reject {prohibited} access.");
        }

        TestAssert.Throws<InvalidOperationException>(() =>
            CortexPostgreSqlRuntimePrivilegeProvisioner.BuildGrantSql(" ", "cortex"));
        TestAssert.Throws<InvalidOperationException>(() =>
            CortexPostgreSqlRuntimePrivilegeProvisioner.BuildGrantSql(
                new string('x', 64),
                "cortex"));
        TestAssert.Throws<InvalidOperationException>(() =>
            CortexPostgreSqlRuntimePrivilegeProvisioner.BuildGrantSql(
                "runtime\nrole",
                "cortex"));
        TestAssert.Throws<InvalidOperationException>(() =>
            CortexPostgreSqlRuntimePrivilegeProvisioner.BuildGrantSql(
                "runtime",
                new string('x', 64)));
        return Task.CompletedTask;
    }

    internal static async Task LocalBootstrapAsync()
    {
        var options = new CortexPostgreSqlOptions(
            ConnectionString(
                "cortex_runtime",
                RuntimeSecret,
                "127.0.0.1",
                "Root Certificate=ignored-root.pem;SSL Certificate=ignored-client.pem;" +
                "SSL Key=ignored-client.key;SSL Password=ignored-tls-secret"),
            ConnectionString(
                "cortex_migrator",
                MigrationSecret,
                "::1",
                "Root Certificate=ignored-root.pem;SSL Certificate=ignored-client.pem;" +
                "SSL Key=ignored-client.key;SSL Password=ignored-tls-secret"),
            rootCertificatePath: "ignored-root.pem",
            tlsPolicy: CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap);
        var validated = options.Validate();
        TestAssert.False(
            validated.Summary.RootCertificateConfigured,
            "Bootstrap summary must not claim an ignored root certificate.");
        var runtime = CortexPostgreSqlDataSourceFactory.CreateRuntimeConnectionSettings(validated);
        var migration = CortexPostgreSqlDataSourceFactory.CreateMigrationConnectionSettings(validated);
        AssertTlsDisabledDefaults(runtime, "Bootstrap");
        AssertTlsDisabledDefaults(migration, "Bootstrap");

        await using var composition = CortexPostgreSqlComposition.Create(
            options,
            new TestRowAuthenticator());
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);
        var readiness = composition.CreateReadinessOptions(14, allowedReachableRoleNames: null);
        TestAssert.False(readiness.RequireTls, "Bootstrap readiness must not claim TLS.");
        TestAssert.True(
            readiness.RequireStoreSentinel,
            "Default PostgreSQL readiness no longer requires a verified store sentinel.");
        TestAssert.False(
            readiness.HostTlsPolicyVerified,
            "Bootstrap readiness must not claim host TLS verification.");
        var degraded = await composition.ProbeReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Degraded)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(PostgreSqlReadinessState.Degraded, degraded.State);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);
        TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            composition.CreateQuerySource());

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            composition.ProbeReadinessCoreAsync(
                _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Ready)),
                canceled.Token).AsTask()).ConfigureAwait(false);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);
        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            composition.ProbeReadinessCoreAsync(
                _ => ValueTask.FromException<PostgreSqlPersistenceReadiness>(
                    new InvalidOperationException("synthetic probe failure")),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);
        await composition.ProbeReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Ready)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State,
            "Bootstrap must remain closed even if an internal synthetic result says Ready.");

        var remote = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret, "127.0.0.1,example.com"),
            ConnectionString("cortex_migrator", MigrationSecret, "127.0.0.1"),
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap);
        var remoteFailure = TestAssert.Throws<CortexPostgreSqlConfigurationException>(
            () => remote.ValidateConfiguration());
        TestAssert.Equal("cortex_postgresql_local_bootstrap_host_invalid", remoteFailure.Code);

        var hostAlias = new CortexPostgreSqlOptions(
            ConnectionString("cortex_runtime", RuntimeSecret, "localhost"),
            ConnectionString("cortex_migrator", MigrationSecret, "127.0.0.1"),
            rootCertificatePath: null,
            tlsPolicy: CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap);
        TestAssert.Throws<CortexPostgreSqlConfigurationException>(() =>
            hostAlias.ValidateConfiguration());
    }

    internal static async Task LifecycleAsync()
    {
        using var certificate = TestRootCertificate.Create();
        var options = CreateOptions(certificate.Path);
        await using (var migration = CortexPostgreSqlMigrationSession.Create(options))
        {
            _ = migration.Migrator;
        }

        var composition = CortexPostgreSqlComposition.Create(options, new TestRowAuthenticator());

        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State);
        TestAssert.False(composition.Admission.CanAcceptWork, "New composition must await readiness.");
        var awaiting = TestAssert.Throws<CortexPostgreSqlAdmissionException>(
            () => composition.CreateQuerySource());
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", awaiting.Code);
        TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            composition.CreateLearningRepository());
        var retentionScope = new PostgreSqlCortexRetentionScope(
            "tenant-1",
            "project:project-1",
            "authority-1",
            "subject-1",
            "service-1",
            1,
            PostgreSqlCortexRetentionCapability.Read);
        TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            composition.CreateAgentRetentionRepository(retentionScope));
        TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            composition.CreateProjectBriefReadModel());

        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProbe = composition.ProbeReadinessCoreAsync(
            async _ =>
            {
                probeEntered.SetResult();
                await releaseProbe.Task.ConfigureAwait(false);
                return CreateReadinessResult(PostgreSqlReadinessState.Ready);
            },
            CancellationToken.None).AsTask();
        await probeEntered.Task.ConfigureAwait(false);
        var overlap = await TestAssert.ThrowsAsync<CortexPostgreSqlAdmissionException>(() =>
            composition.ProbeReadinessCoreAsync(
                _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Ready)),
                CancellationToken.None).AsTask()).ConfigureAwait(false);
        TestAssert.Equal("cortex_postgresql_readiness_probe_in_progress", overlap.Code);
        releaseProbe.SetResult();
        await firstProbe.ConfigureAwait(false);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.Admission.State,
            "An overlapping attempt must invalidate the earlier probe result.");

        await ConfirmReadyAsync(composition).ConfigureAwait(false);
        TestAssert.True(composition.Admission.CanAcceptWork, "Ready composition should accept work.");
        var querySource = composition.CreateQuerySource();
        var learningRepository = composition.CreateLearningRepository();
        var retentionRepository = composition.CreateAgentRetentionRepository(
            retentionScope);
        var projectBriefReadModel = composition.CreateProjectBriefReadModel();
        TestAssert.True(querySource is ICortexQuerySource, "Query factory failed.");
        TestAssert.True(
            learningRepository is ILearningFeedbackRepository,
            "Learning repository factory failed.");
        TestAssert.True(
            learningRepository is ILearningFeedbackAuditRepository,
            "The capable PostgreSQL repository lost its optional audit boundary.");
        TestAssert.True(
            retentionRepository is ICortexAgentRetentionRepository,
            "The passive-retention factory failed.");
        TestAssert.True(
            projectBriefReadModel is ICortexProjectBriefReadModel,
            "The project-brief read-model factory failed.");
        var auditRepository = (ILearningFeedbackAuditRepository)learningRepository;
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.SyncAdmission.State);
        var syncAwaiting = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            composition.CreateSyncRepository());
        TestAssert.Equal("cortex_postgresql_sync_awaiting_readiness", syncAwaiting.Code);

        var baseProbeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baseProbe = composition.ProbeReadinessCoreAsync(
            async _ =>
            {
                baseProbeEntered.SetResult();
                await releaseBaseProbe.Task.ConfigureAwait(false);
                return CreateReadinessResult(PostgreSqlReadinessState.Ready);
            },
            CancellationToken.None).AsTask();
        await baseProbeEntered.Task.ConfigureAwait(false);
        var cachedQueryDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            querySource.GetSnapshotAsync(CreateReadinessScope(), CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", cachedQueryDenied.Code);
        var cachedLearningDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            learningRepository.GetStatusAsync(null!, CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", cachedLearningDenied.Code);
        var cachedAuditDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            auditRepository.FindLatestAssessmentAsync(
                null!,
                "proposal_0000000000000000000000000000000000000000000000000000000000000000",
                CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", cachedAuditDenied.Code);
        var cachedRetentionDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            retentionRepository.FindHeadAsync(
                "project-1",
                CortexRetainedDocumentKinds.TaskSnapshot,
                "task-set-1",
                CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", cachedRetentionDenied.Code);
        var cachedBriefDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            projectBriefReadModel.FindCurrentAsync(null!, CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_awaiting_readiness", cachedBriefDenied.Code);
        releaseBaseProbe.SetResult();
        await baseProbe.ConfigureAwait(false);

        var syncResult = await composition.ProbeSyncReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateSyncReadinessResult(PostgreSqlReadinessState.Ready)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(PostgreSqlReadinessState.Ready, syncResult.State);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.SyncAdmission.State);
        var syncRepository = composition.CreateSyncRepository();
        TestAssert.True(
            syncRepository is ICortexLegacyMigrationPageCommitRepository,
            "The capable PostgreSQL repository lost its optional migration-page boundary.");
        var genericOnlySyncRepository = CortexPostgreSqlSyncAdmissionDecorator.Create(
            new GenericOnlySyncRepository(),
            _ => { });
        TestAssert.False(
            genericOnlySyncRepository is ICortexLegacyMigrationPageCommitRepository,
            "The admission decorator advertised migration-page authority absent from its inner repository.");

        var syncProbeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSyncProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncProbe = composition.ProbeSyncReadinessCoreAsync(
            async _ =>
            {
                syncProbeEntered.SetResult();
                await releaseSyncProbe.Task.ConfigureAwait(false);
                return CreateSyncReadinessResult(PostgreSqlReadinessState.Ready);
            },
            CancellationToken.None).AsTask();
        await syncProbeEntered.Task.ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.Admission.State,
            "A sync-only probe changed global admission.");
        var cachedSyncDenied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(() =>
            syncRepository.CommitAsync(null!, CancellationToken.None));
        TestAssert.Equal("cortex_postgresql_sync_awaiting_readiness", cachedSyncDenied.Code);
        releaseSyncProbe.SetResult();
        await syncProbe.ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.SyncAdmission.State);
        await ConfirmReadyAsync(composition).ConfigureAwait(false);
        TestAssert.Equal(
            CortexPostgreSqlAdmissionState.AwaitingReadiness,
            composition.SyncAdmission.State,
            "A fresh base-readiness epoch did not invalidate the older sync attestation.");
        TestAssert.True(
            typeof(CortexPostgreSqlComposition).GetMethod("CreateReadinessProbe") is null,
            "A public readiness-probe factory could bypass admission state updates.");
        TestAssert.True(
            typeof(CortexPostgreSqlComposition).GetMethod("ProbeReadinessAsync") is not null,
            "The stateful readiness workflow is missing.");

        TestAssert.True(
            typeof(CortexPostgreSqlComposition).GetMethod("CreateMigrationSession") is null,
            "Runtime composition must not expose migration-session creation.");
        TestAssert.True(
            typeof(CortexPostgreSqlComposition)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .All(field => field.FieldType != typeof(ValidatedCortexPostgreSqlOptions)),
            "Runtime composition retained secret-bearing validated options.");
        var privateReadiness = composition.CreateReadinessOptions(14, allowedReachableRoleNames: null);
        TestAssert.True(privateReadiness.RequireTls, "Normal readiness must require TLS.");
        var infrastructureReadiness = composition.CreateReadinessOptions(
            14,
            allowedReachableRoleNames: null,
            requireStoreSentinel: false);
        TestAssert.False(
            infrastructureReadiness.RequireStoreSentinel,
            "Explicit empty hybrid-store admission did not preserve its infrastructure-only boundary.");
        TestAssert.True(
            privateReadiness.HostTlsPolicyVerified,
            "Normal readiness must attest the configured VerifyFull policy.");

        await composition.DisposeAsync().ConfigureAwait(false);
        await composition.DisposeAsync().ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Disposed, composition.Admission.State);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Disposed, composition.SyncAdmission.State);
        var disposed = TestAssert.Throws<CortexPostgreSqlAdmissionException>(
            () => composition.CreateQuerySource());
        TestAssert.Equal("cortex_postgresql_composition_disposed", disposed.Code);
    }

    internal static async Task PoisonAdmissionAsync()
    {
        using var certificate = TestRootCertificate.Create();
        await using var composition = CortexPostgreSqlComposition.Create(
            CreateOptions(certificate.Path),
            new TestRowAuthenticator());
        await ConfirmReadyAsync(composition).ConfigureAwait(false);
        var transactionsField = typeof(CortexPostgreSqlComposition).GetField(
            "_transactions",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Transaction runner field was not found.");
        var transactions = (PostgreSqlTransactionRunner)(transactionsField.GetValue(composition) ??
            throw new InvalidOperationException("Transaction runner was null."));
        var poisonedField = typeof(PostgreSqlTransactionRunner).GetField(
            "_poisoned",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Poison signal field was not found.");
        poisonedField.SetValue(transactions, 1);

        TestAssert.Equal(CortexPostgreSqlAdmissionState.DataSourcePoisoned, composition.Admission.State);
        var denied = TestAssert.Throws<CortexPostgreSqlAdmissionException>(
            () => composition.CreateQuerySource());
        TestAssert.Equal("cortex_postgresql_data_source_poisoned", denied.Code);
        await composition.ProbeReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Ready)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.DataSourcePoisoned, composition.Admission.State);
    }

    internal static async Task ConcurrentLifecycleAsync()
    {
        using var certificate = TestRootCertificate.Create();
        var composition = CortexPostgreSqlComposition.Create(
            CreateOptions(certificate.Path),
            new TestRowAuthenticator());
        await ConfirmReadyAsync(composition).ConfigureAwait(false);
        using var start = new ManualResetEventSlim(initialState: false);
        var factoryTasks = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            start.Wait();
            try
            {
                _ = composition.CreateQuerySource();
                return "created";
            }
            catch (CortexPostgreSqlAdmissionException exception)
            {
                return exception.Code;
            }
        })).ToArray();
        var disposal = Task.Run(async () =>
        {
            start.Wait();
            await composition.DisposeAsync().ConfigureAwait(false);
        });

        start.Set();
        var outcomes = await Task.WhenAll(factoryTasks).ConfigureAwait(false);
        await disposal.ConfigureAwait(false);
        TestAssert.True(
            outcomes.All(outcome => outcome is "created" or "cortex_postgresql_composition_disposed"),
            "A factory/disposal race produced an unbounded outcome.");
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Disposed, composition.Admission.State);
        TestAssert.Throws<CortexPostgreSqlAdmissionException>(() => composition.CreateQuerySource());
    }

    internal static async Task ProgramBoundaryAsync()
    {
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var status = await PegasusCortexServiceProgram.RunAsync(
            ["--composition-status"],
            output,
            diagnostics,
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(0, status);
        TestAssert.Contains(
            "composition-contract-available runtime-not-configured",
            output.ToString(),
            "Static composition status was not explicit.");
        TestAssert.Equal(string.Empty, diagnostics.ToString());

        output.GetStringBuilder().Clear();
        var start = await PegasusCortexServiceProgram.RunAsync(
            [],
            output,
            diagnostics,
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(2, start);
        TestAssert.Contains(
            "cortex_service_host_composition_required",
            diagnostics.ToString(),
            "Missing host composition diagnostic was not actionable.");
        TestAssert.DoesNotContain(RuntimeSecret, diagnostics.ToString(), "Executable diagnostics leaked a secret.");

        output.GetStringBuilder().Clear();
        diagnostics.GetStringBuilder().Clear();
        var migration = await CortexPostgreSqlMigrationCommand.RunAsync(
            output,
            diagnostics,
            _ => null,
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(4, migration);
        TestAssert.Equal(string.Empty, output.ToString());
        TestAssert.Contains(
            "cortex_postgresql_migration_configuration_incomplete",
            diagnostics.ToString(),
            "The administrative migration command did not fail closed without host settings.");
        TestAssert.DoesNotContain(RuntimeSecret, diagnostics.ToString(), "Migration diagnostics leaked a secret.");
    }

    private static CortexPostgreSqlOptions CreateOptions(
        string certificatePath,
        int maximumPoolSize = CortexPostgreSqlOptions.DefaultRuntimeMaximumPoolSize,
        CortexPostgreSqlTlsPolicy tlsPolicy = CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull,
        string? serverSpkiPin = null,
        string host = "127.0.0.1") =>
        new(
            ConnectionString("cortex_runtime", RuntimeSecret, host),
            ConnectionString("cortex_migrator", MigrationSecret, host),
            certificatePath,
            maximumPoolSize,
            tlsPolicy: tlsPolicy,
            serverCertificateSpkiSha256: serverSpkiPin);

    private static string ConnectionString(
        string username,
        string password,
        string host = "127.0.0.1",
        string? additionalSettings = null) =>
        $"Host={host};Port=5432;Database=pegasus_cortex;Username={username};Password={password}" +
        (string.IsNullOrEmpty(additionalSettings) ? string.Empty : $";{additionalSettings}");

    private static void AssertSecureDefaults(
        NpgsqlConnectionStringBuilder settings,
        string certificatePath,
        string applicationName,
        int commandTimeout)
    {
        TestAssert.Equal(SslMode.VerifyFull, settings.SslMode);
        TestAssert.Equal(certificatePath, settings.RootCertificate);
        TestAssert.True(settings.CheckCertificateRevocation, "Certificate revocation checking must be enabled.");
        TestAssert.Equal(GssEncryptionMode.Disable, settings.GssEncryptionMode);
        TestAssert.Equal(applicationName, settings.ApplicationName);
        TestAssert.Equal(15, settings.Timeout);
        TestAssert.Equal(commandTimeout, settings.CommandTimeout);
        TestAssert.Equal(2000, settings.CancellationTimeout);
        TestAssert.False(settings.NoResetOnClose, "Pooled session state reset must remain enabled.");
        TestAssert.False(settings.IncludeErrorDetail, "Detailed server errors must remain disabled.");
        TestAssert.False(settings.LogParameters, "Parameter logging must remain disabled.");
        TestAssert.False(settings.PersistSecurityInfo, "Security information must not remain observable.");
        TestAssert.False(settings.Enlist, "Ambient transaction enlistment must remain disabled.");
        TestAssert.False(settings.Multiplexing, "Multiplexing must remain disabled for RLS context safety.");
        TestAssert.False(
            settings.ShouldSerialize("Trust Server Certificate"),
            "Legacy trust bypass must not survive normalization.");
        TestAssert.False(
            settings.ShouldSerialize("SSL Negotiation"),
            "PostgreSQL 17-only direct SSL negotiation must not survive normalization.");
    }

    private static void AssertTlsDisabledDefaults(
        NpgsqlConnectionStringBuilder settings,
        string policyName)
    {
        TestAssert.Equal(SslMode.Disable, settings.SslMode);
        TestAssert.False(
            settings.ShouldSerialize("Root Certificate"),
            $"{policyName} mode must remove remote trust configuration.");
        TestAssert.False(
            settings.ShouldSerialize("SSL Certificate"),
            $"{policyName} mode must remove an unused client certificate.");
        TestAssert.False(
            settings.ShouldSerialize("SSL Key"),
            $"{policyName} mode must remove an unused client private key path.");
        TestAssert.False(
            settings.ShouldSerialize("SSL Password"),
            $"{policyName} mode must remove an unused client-key password.");
        TestAssert.False(
            settings.CheckCertificateRevocation,
            $"{policyName} mode has no certificate whose revocation can be checked.");
        TestAssert.False(settings.Enlist, "Ambient transaction enlistment must remain disabled.");
        TestAssert.False(settings.Multiplexing, "Multiplexing must remain disabled for RLS context safety.");
    }

    private static void AssertPinnedDefaults(NpgsqlConnectionStringBuilder settings)
    {
        TestAssert.Equal(SslMode.Require, settings.SslMode);
        TestAssert.False(
            settings.ShouldSerialize("Root Certificate"),
            "Pinned TLS must perform trust validation only inside its callback.");
        TestAssert.False(
            settings.CheckCertificateRevocation,
            "Pinned TLS must not invoke the unsupported platform revocation path.");
        TestAssert.False(
            settings.ShouldSerialize("Trust Server Certificate"),
            "Pinned TLS must not retain a legacy trust bypass.");
        TestAssert.False(settings.Enlist, "Ambient transaction enlistment must remain disabled.");
        TestAssert.False(settings.Multiplexing, "Multiplexing must remain disabled for RLS context safety.");
    }

    private static CortexQuerySourceScope CreateReadinessScope() => new(
        TenantId: "pegasus-test",
        Namespace: "pegasus.default",
        AuthorityId: "local-test",
        SubjectId: "service-test",
        ServiceId: "pegasus-cortex-service-tests",
        PolicyRevision: 1,
        Capability: CortexQueryCapability.Status,
        Operation: "cortex.query.status",
        MaximumSensitivity: "restricted",
        IncludeStale: false,
        IncludeBlockedOrUnverified: false);

    private static async Task ConfirmReadyAsync(CortexPostgreSqlComposition composition)
    {
        var result = await composition.ProbeReadinessCoreAsync(
            _ => ValueTask.FromResult(CreateReadinessResult(PostgreSqlReadinessState.Ready)),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(PostgreSqlReadinessState.Ready, result.State);
        TestAssert.Equal(CortexPostgreSqlAdmissionState.Accepting, composition.Admission.State);
    }

    private static PostgreSqlPersistenceReadiness CreateReadinessResult(
        PostgreSqlReadinessState state,
        bool? tlsActive = null,
        bool? hostTlsPolicyVerified = null) => new(
        state,
        ServerMajor: state == PostgreSqlReadinessState.Ready ? 17 : null,
        TlsActive: tlsActive ?? state == PostgreSqlReadinessState.Ready,
        HostTlsPolicyVerified: hostTlsPolicyVerified ?? state == PostgreSqlReadinessState.Ready,
        RuntimeRoleRestricted: state == PostgreSqlReadinessState.Ready,
        ContextClean: state == PostgreSqlReadinessState.Ready,
        MigrationsCurrent: state == PostgreSqlReadinessState.Ready,
        RowLevelSecurityForced: state == PostgreSqlReadinessState.Ready,
        StoreSentinelVerified: state == PostgreSqlReadinessState.Ready,
        IndexStateVerified: state == PostgreSqlReadinessState.Ready,
        IssueCodes: state == PostgreSqlReadinessState.Ready
            ? []
            : ["cortex_postgresql_test_not_ready"],
        ObservedAt: DateTimeOffset.UnixEpoch);

    private static PostgreSqlSyncReadiness CreateSyncReadinessResult(
        PostgreSqlReadinessState state) => new(
        state,
        MigrationCurrent: state == PostgreSqlReadinessState.Ready,
        SchemaContractPresent: state == PostgreSqlReadinessState.Ready,
        RuntimePrivilegesRestricted: state == PostgreSqlReadinessState.Ready,
        RowLevelSecurityForced: state == PostgreSqlReadinessState.Ready,
        PolicyContractMatches: state == PostgreSqlReadinessState.Ready,
        IssueCodes: state == PostgreSqlReadinessState.Ready
            ? []
            : ["cortex_postgresql_sync_test_not_ready"],
        ObservedAt: DateTimeOffset.UnixEpoch);

    private sealed class TestRowAuthenticator : ICortexRowAuthenticator
    {
        public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope) =>
            new(1, "test-only", "test-only", [1]);

        public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal) => true;
    }

    /// <summary>
    /// Deliberately lacks the optional legacy-migration interface so the admission factory must
    /// preserve the application's fail-closed capability-absent diagnostic.
    /// </summary>
    private sealed class GenericOnlySyncRepository : ICortexSyncRepository
    {
        public ValueTask<CortexSyncCommitSnapshot> CommitAsync(
            CortexSyncCommitRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
            CortexSyncReadBackRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
            CortexSyncReceiptAppendRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
            CortexSyncReceiptLookupRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
            CortexSyncReceiptReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestRootCertificate : IDisposable
    {
        private TestRootCertificate(string path) => Path = path;

        internal string Path { get; }

        internal static TestRootCertificate Create()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Pegasus Cortex Test Root",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pegasus-cortex-root-{Guid.NewGuid():N}.pem");
            File.WriteAllText(path, certificate.ExportCertificatePem());
            return new TestRootCertificate(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A test cleanup failure does not hide the behavioral assertion result.
            }
            catch (UnauthorizedAccessException)
            {
                // A test cleanup failure does not hide the behavioral assertion result.
            }
        }
    }

    private sealed class PinnedCertificateFixture : IDisposable
    {
        private readonly List<X509Certificate2> _certificates;

        private PinnedCertificateFixture(
            string rootPath,
            X509Certificate2 validLeaf,
            X509Certificate2 expiredLeaf,
            X509Certificate2 noServerAuthenticationLeaf,
            string leafSpkiSha256,
            List<X509Certificate2> certificates)
        {
            RootPath = rootPath;
            ValidLeaf = validLeaf;
            ExpiredLeaf = expiredLeaf;
            NoServerAuthenticationLeaf = noServerAuthenticationLeaf;
            LeafSpkiSha256 = leafSpkiSha256;
            _certificates = certificates;
        }

        internal string RootPath { get; }
        internal X509Certificate2 ValidLeaf { get; }
        internal X509Certificate2 ExpiredLeaf { get; }
        internal X509Certificate2 NoServerAuthenticationLeaf { get; }
        internal string LeafSpkiSha256 { get; }

        internal static PinnedCertificateFixture Create()
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest(
                "CN=Pegasus Cortex Pinned Test Root",
                rootKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            var root = rootRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(2));
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"pegasus-cortex-pinned-root-{Guid.NewGuid():N}.pem");
            File.WriteAllText(rootPath, root.ExportCertificatePem());

            using var leafKey = RSA.Create(2048);
            var valid = CreateLeaf(
                root,
                rootKey,
                leafKey,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1),
                includeServerAuthentication: true);
            var expired = CreateLeaf(
                root,
                rootKey,
                leafKey,
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow.AddHours(-1),
                includeServerAuthentication: true);
            var noServerAuthentication = CreateLeaf(
                root,
                rootKey,
                leafKey,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1),
                includeServerAuthentication: false);
            var pin = Convert.ToHexString(
                    SHA256.HashData(valid.PublicKey.ExportSubjectPublicKeyInfo()))
                .ToLowerInvariant();
            return new PinnedCertificateFixture(
                rootPath,
                valid,
                expired,
                noServerAuthentication,
                pin,
                [root, valid, expired, noServerAuthentication]);
        }

        private static X509Certificate2 CreateLeaf(
            X509Certificate2 root,
            RSA rootKey,
            RSA leafKey,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            bool includeServerAuthentication)
        {
            var request = new CertificateRequest(
                "CN=unused-common-name",
                leafKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            if (includeServerAuthentication)
            {
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                    true));
            }

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build(critical: true));
            var serial = RandomNumberGenerator.GetBytes(16);
            try
            {
                return request.Create(root.SubjectName, X509SignatureGenerator.CreateForRSA(
                    rootKey,
                    RSASignaturePadding.Pkcs1), notBefore, notAfter, serial);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serial);
            }
        }

        public void Dispose()
        {
            foreach (var certificate in _certificates)
                certificate.Dispose();
            try
            {
                File.Delete(RootPath);
            }
            catch (IOException)
            {
                // Fixture cleanup must not hide a behavioral assertion.
            }
            catch (UnauthorizedAccessException)
            {
                // Fixture cleanup must not hide a behavioral assertion.
            }
        }
    }

    private sealed class TestFile : IDisposable
    {
        private TestFile(string path) => Path = path;

        internal string Path { get; }

        internal static TestFile Create(byte[] contents)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pegasus-cortex-input-{Guid.NewGuid():N}.pem");
            File.WriteAllBytes(path, contents);
            return new TestFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Test cleanup must not hide the behavioral assertion result.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup must not hide the behavioral assertion result.
            }
        }
    }
}
