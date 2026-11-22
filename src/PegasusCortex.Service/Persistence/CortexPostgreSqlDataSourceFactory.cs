using Npgsql;
using Pegasus.Cortex.Service.Configuration;

namespace Pegasus.Cortex.Service.Persistence;

internal static class CortexPostgreSqlDataSourceFactory
{
    internal const string RuntimeDataSourceName = "pegasus-cortex-runtime";
    internal const string MigrationDataSourceName = "pegasus-cortex-migration";

    internal static NpgsqlDataSource CreateRuntime(ValidatedCortexPostgreSqlOptions options)
    {
        try
        {
            var settings = CreateRuntimeConnectionSettings(options);
            return CreateBuilder(settings, options, RuntimeDataSourceName).Build();
        }
        catch (CortexPostgreSqlConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!Fatal(exception))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_runtime_data_source_invalid",
                "cortex.postgresql.runtime.create",
                exception.GetType().FullName);
        }
    }

    internal static NpgsqlDataSource CreateMigration(ValidatedCortexPostgreSqlOptions options)
    {
        try
        {
            var settings = CreateMigrationConnectionSettings(options);
            return CreateBuilder(settings, options, MigrationDataSourceName).Build();
        }
        catch (CortexPostgreSqlConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!Fatal(exception))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_migration_data_source_invalid",
                "cortex.postgresql.migration.create",
                exception.GetType().FullName);
        }
    }

    internal static NpgsqlConnectionStringBuilder CreateRuntimeConnectionSettings(
        ValidatedCortexPostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new NpgsqlConnectionStringBuilder(options.RuntimeConnectionString);
        ApplyDefaults(settings, options, RuntimeDataSourceName);
        settings.Pooling = true;
        settings.MinPoolSize = 0;
        settings.MaxPoolSize = options.RuntimeMaximumPoolSize;
        settings.ConnectionIdleLifetime = 300;
        settings.ConnectionPruningInterval = 10;
        settings.ConnectionLifetime = 3600;
        return settings;
    }

    internal static NpgsqlConnectionStringBuilder CreateMigrationConnectionSettings(
        ValidatedCortexPostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MigrationConnectionString))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_migration_connection_invalid",
                "cortex.postgresql.migration.create");
        }

        var settings = new NpgsqlConnectionStringBuilder(options.MigrationConnectionString);
        ApplyDefaults(settings, options, MigrationDataSourceName);
        settings.Pooling = false;
        settings.MinPoolSize = 0;
        settings.MaxPoolSize = 1;
        settings.CommandTimeout = options.MigrationCommandTimeoutSeconds;
        return settings;
    }

    private static void ApplyDefaults(
        NpgsqlConnectionStringBuilder settings,
        ValidatedCortexPostgreSqlOptions options,
        string applicationName)
    {
        if (options.TlsPolicy == CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull)
        {
            settings.SslMode = SslMode.VerifyFull;
            settings.RootCertificate = options.RootCertificatePath;
            settings.CheckCertificateRevocation = true;
        }
        else if (options.TlsPolicy == CortexPostgreSqlTlsPolicy.Pinned)
        {
            // Npgsql's custom certificate callback is intentionally paired with
            // Require rather than VerifyFull. The callback performs the complete
            // private-CA, SAN-only host, lifetime, purpose, and SPKI validation.
            settings.SslMode = SslMode.Require;
            settings.Remove("Root Certificate");
            settings.Remove("SSL Certificate");
            settings.Remove("SSL Key");
            settings.Remove("SSL Password");
            settings.CheckCertificateRevocation = false;
        }
        else if (options.TlsPolicy is
            CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap or
            CortexPostgreSqlTlsPolicy.TrustedLoopback)
        {
            // Both loopback policies intentionally disable TLS. Configuration validation
            // separately limits them to literal loopback addresses; only TrustedLoopback
            // is eligible to admit runtime work after readiness succeeds.
            settings.SslMode = SslMode.Disable;
            settings.Remove("Root Certificate");
            settings.Remove("SSL Certificate");
            settings.Remove("SSL Key");
            settings.Remove("SSL Password");
            settings.Remove("Check Certificate Revocation");
        }
        else
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_tls_policy_invalid",
                "cortex.postgresql.data-source.configure");
        }

        settings.GssEncryptionMode = GssEncryptionMode.Disable;
        settings.ApplicationName = applicationName;
        settings.Timeout = 15;
        settings.CommandTimeout = 30;
        settings.CancellationTimeout = 2000;
        settings.NoResetOnClose = false;
        settings.IncludeErrorDetail = false;
        settings.LogParameters = false;
        settings.PersistSecurityInfo = false;
        settings.Enlist = false;
        settings.Multiplexing = false;

        // VerifyFull performs chain and host-name validation. Removing this legacy keyword
        // prevents an input connection string from implying a contradictory trust bypass.
        settings.Remove("Trust Server Certificate");
        // The PostgreSQL SSL negotiation path supports the project's PostgreSQL 14 floor;
        // direct SSL negotiation requires PostgreSQL 17 or newer.
        settings.Remove("SSL Negotiation");
    }

    private static NpgsqlDataSourceBuilder CreateBuilder(
        NpgsqlConnectionStringBuilder settings,
        ValidatedCortexPostgreSqlOptions options,
        string name)
    {
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString)
        {
            Name = name
        };
        if (options.TlsPolicy == CortexPostgreSqlTlsPolicy.Pinned)
        {
            var validationCallback = CortexPinnedPostgreSqlCertificateValidator.Create(
                options.RootCertificatePath!,
                settings.Host!,
                options.ServerCertificateSpkiSha256!);
            builder.UseSslClientAuthenticationOptionsCallback(
                sslOptions => sslOptions.RemoteCertificateValidationCallback = validationCallback);
        }

        return builder;
    }

    private static bool Fatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException;
}
