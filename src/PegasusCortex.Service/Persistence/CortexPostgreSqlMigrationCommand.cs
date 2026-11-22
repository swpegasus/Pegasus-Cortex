using System.Text.Json;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Service.Configuration;

namespace Pegasus.Cortex.Service.Persistence;

/// <summary>
/// Runs the existing immutable PostgreSQL migrator as a short-lived administrative
/// command. Secrets remain host-supplied environment values and are never rendered.
/// </summary>
internal static class CortexPostgreSqlMigrationCommand
{
    internal const string Argument = "--apply-postgresql-migrations";
    internal const string RuntimeConnectionVariable = "PEGASUS_CORTEX_RUNTIME_CONNECTION";
    internal const string MigrationConnectionVariable = "PEGASUS_CORTEX_MIGRATION_CONNECTION";
    internal const string RootCertificateVariable = "PEGASUS_CORTEX_ROOT_CA";
    internal const string ApplicationVersionVariable = "PEGASUS_CORTEX_APPLICATION_VERSION";
    internal const string TlsPolicyVariable = "PEGASUS_CORTEX_TLS_POLICY";
    internal const string ServerSpkiPinVariable =
        "PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256";

    internal static async Task<int> RunAsync(
        TextWriter output,
        TextWriter diagnostics,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(environment);

        try
        {
            var runtimeConnection = RequireEnvironment(environment, RuntimeConnectionVariable);
            var migrationConnection = RequireEnvironment(environment, MigrationConnectionVariable);
            var applicationVersion = RequireEnvironment(environment, ApplicationVersionVariable);
            if (applicationVersion.Length > 128)
                throw new InvalidOperationException("cortex_postgresql_application_version_invalid");

            var tlsPolicy = ParseTlsPolicy(environment(TlsPolicyVariable));
            var rootCertificate = tlsPolicy is
                CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull or
                CortexPostgreSqlTlsPolicy.Pinned
                ? RequireEnvironment(environment, RootCertificateVariable)
                : null;
            var serverSpkiPin = tlsPolicy == CortexPostgreSqlTlsPolicy.Pinned
                ? RequireEnvironment(environment, ServerSpkiPinVariable)
                : null;

            var options = new CortexPostgreSqlOptions(
                runtimeConnection,
                migrationConnection,
                rootCertificate,
                runtimeMaximumPoolSize: 1,
                tlsPolicy: tlsPolicy,
                serverCertificateSpkiSha256: serverSpkiPin);
            await using var session = CortexPostgreSqlMigrationSession.Create(options);
            var result = await session.Migrator.ApplyAsync(
                applicationVersion,
                cancellationToken).ConfigureAwait(false);
            await session.ReconcileRuntimePrivilegesAsync(cancellationToken).ConfigureAwait(false);

            output.WriteLine(JsonSerializer.Serialize(new
            {
                status = "pass",
                operation = "postgresql-migrate",
                applicationVersion,
                currentMigrationId = result.CurrentMigrationId,
                appliedMigrationIds = result.AppliedMigrationIds,
                runtimePrivilegesReconciled = true
            }));
            output.Flush();
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                CortexPostgreSqlConfigurationException configuration => configuration.Code,
                PostgreSqlPersistenceException persistence => persistence.Code,
                InvalidOperationException invalid
                    when invalid.Message.StartsWith("cortex_", StringComparison.Ordinal) =>
                    invalid.Message,
                _ => "cortex_postgresql_migration_command_failed"
            };
            diagnostics.WriteLine(
                $"ERROR cortex.service.migrate {code} cause={exception.GetType().Name}");
            diagnostics.Flush();
            return 4;
        }
    }

    internal static CortexPostgreSqlTlsPolicy ParseTlsPolicy(string? value) => value switch
    {
        null or "" or "verify-full" => CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull,
        "trusted-loopback" => CortexPostgreSqlTlsPolicy.TrustedLoopback,
        "pinned-spki" => CortexPostgreSqlTlsPolicy.Pinned,
        _ => throw new InvalidOperationException(
            "cortex_postgresql_migration_tls_policy_invalid")
    };

    private static string RequireEnvironment(
        Func<string, string?> environment,
        string variable)
    {
        var value = environment(variable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("cortex_postgresql_migration_configuration_incomplete");
        return value;
    }
}
