using System.Net;
using Npgsql;
using Pegasus.Cortex.Service.Configuration;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal sealed class FixtureConfigurationException : Exception
{
    public FixtureConfigurationException(string code, Exception? cause = null)
        : base(code, cause)
    {
        Code = code;
    }

    public string Code { get; }
}

internal enum FixtureBackupRestoreMode
{
    None,
    Capture,
    VerifyRestoredState
}

internal sealed class FixtureConfiguration
{
    public const string IntegrationFlag = "PEGASUS_CORTEX_PG_INTEGRATION";
    public const string PreflightOnlyFlag = "PEGASUS_CORTEX_PG_PREFLIGHT_ONLY";
    public const string MigrationConnection = "PEGASUS_CORTEX_PG_MIGRATION_CONNECTION";
    public const string RuntimeConnection = "PEGASUS_CORTEX_PG_RUNTIME_CONNECTION";
    public const string TlsPolicyVariable = "PEGASUS_CORTEX_TLS_POLICY";
    public const string BackupRestoreModeVariable = "PEGASUS_CORTEX_PG_BACKUP_RESTORE_MODE";
    public const string BackupRestoreProofRoot = "PEGASUS_CORTEX_PG_BACKUP_RESTORE_PROOF_ROOT";
    public const string BackupRestoreProofFile = "PEGASUS_CORTEX_PG_BACKUP_RESTORE_PROOF_FILE";
    public const string LegacyBackupRestoreProof = "PEGASUS_CORTEX_PG_BACKUP_RESTORE_PROOF";

    private static readonly string[] CapturedNames =
    [
        IntegrationFlag,
        PreflightOnlyFlag,
        MigrationConnection,
        RuntimeConnection,
        TlsPolicyVariable,
        BackupRestoreModeVariable,
        BackupRestoreProofRoot,
        BackupRestoreProofFile,
        LegacyBackupRestoreProof,
        FixtureRevocationEvidence.EvidenceRootVariable,
        FixtureRevocationEvidence.ManifestFileVariable
    ];

    private FixtureConfiguration(
        string migrationConnectionString,
        string runtimeConnectionString,
        bool preflightOnly,
        FixtureBackupRestoreMode backupRestoreMode,
        FixtureBackupRestoreProofLocation? backupRestoreProofLocation,
        string rootCertificatePath,
        string databaseName,
        string migrationUsername,
        string runtimeUsername,
        FixtureRevocationEvidence? revocationEvidence,
        CortexPostgreSqlOptions postgreSqlOptions)
    {
        MigrationConnectionString = migrationConnectionString;
        RuntimeConnectionString = runtimeConnectionString;
        PreflightOnly = preflightOnly;
        BackupRestoreMode = backupRestoreMode;
        BackupRestoreProofLocation = backupRestoreProofLocation;
        RootCertificatePath = rootCertificatePath;
        DatabaseName = databaseName;
        MigrationUsername = migrationUsername;
        RuntimeUsername = runtimeUsername;
        RevocationEvidence = revocationEvidence;
        PostgreSqlOptions = postgreSqlOptions;
    }

    public string MigrationConnectionString { get; }
    public string RuntimeConnectionString { get; }
    public bool PreflightOnly { get; }
    public FixtureBackupRestoreMode BackupRestoreMode { get; }
    public FixtureBackupRestoreProofLocation? BackupRestoreProofLocation { get; }
    public string RootCertificatePath { get; }
    public string DatabaseName { get; }
    public string MigrationUsername { get; }
    public string RuntimeUsername { get; }
    public FixtureRevocationEvidence? RevocationEvidence { get; }
    public CortexPostgreSqlTlsPolicy TlsPolicy => PostgreSqlOptions.TlsPolicy;
    public bool UsesTls => TlsPolicy != CortexPostgreSqlTlsPolicy.TrustedLoopback;
    public CortexPostgreSqlOptions PostgreSqlOptions { get; }

    public static IReadOnlyDictionary<string, string?> CaptureEnvironment() =>
        CapturedNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

    public static bool IntegrationEnabled(IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue(IntegrationFlag, out var value) &&
        string.Equals(value, "1", StringComparison.Ordinal);

    public static bool PreflightOnlyEnabled(IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue(PreflightOnlyFlag, out var value) &&
        string.Equals(value, "1", StringComparison.Ordinal);

    public static FixtureConfiguration Load(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!IntegrationEnabled(values))
            throw Failure("integration_not_enabled");

        var preflightOnly = PreflightOnlyEnabled(values);
        var backupRestoreMode = ParseBackupRestoreMode(values);
        var backupRestoreProofLocation = ParseBackupRestoreProofLocation(values, backupRestoreMode);
        if (preflightOnly && backupRestoreMode != FixtureBackupRestoreMode.None)
            throw Failure("backup_restore_preflight_conflict");

        var migrationConnection = Required(values, MigrationConnection, "migration_connection_missing");
        var runtimeConnection = Required(values, RuntimeConnection, "runtime_connection_missing");
        var migration = ParseConnection(migrationConnection, "migration_connection_invalid");
        var runtime = ParseConnection(runtimeConnection, "runtime_connection_invalid");
        var tlsPolicy = ParseTlsPolicy(values);

        var migrationIdentity = ValidateEndpoint(migration, "migration_endpoint_invalid");
        var runtimeIdentity = ValidateEndpoint(runtime, "runtime_endpoint_invalid");
        var migrationRoot = ValidateTransportPolicy(
            migration,
            tlsPolicy,
            "migration_tls_policy_invalid",
            "migration_root_certificate_path_invalid");
        var runtimeRoot = ValidateTransportPolicy(
            runtime,
            tlsPolicy,
            "runtime_tls_policy_invalid",
            "runtime_root_certificate_path_invalid");
        if (tlsPolicy == CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull &&
            !PathsEqual(migrationRoot, runtimeRoot))
        {
            throw Failure("fixture_root_certificate_mismatch");
        }
        if (!string.Equals(migrationIdentity.Database, runtimeIdentity.Database, StringComparison.Ordinal))
            throw Failure("fixture_database_mismatch");
        if (string.Equals(migrationIdentity.Username, runtimeIdentity.Username, StringComparison.OrdinalIgnoreCase))
            throw Failure("fixture_roles_not_separate");
        var revocationEvidence = tlsPolicy == CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull
            ? FixtureRevocationEvidence.Load(
                values,
                migrationRoot,
                migrationIdentity.Addresses
                    .Concat(runtimeIdentity.Addresses)
                    .Distinct()
                    .ToArray(),
                [migrationIdentity.Port, runtimeIdentity.Port])
            : RequireNoRevocationEvidence(values);

        var options = new CortexPostgreSqlOptions(
            runtimeConnection,
            migrationConnection,
            string.IsNullOrEmpty(migrationRoot) ? null : migrationRoot,
            tlsPolicy: tlsPolicy);
        return new FixtureConfiguration(
            migrationConnection,
            runtimeConnection,
            preflightOnly,
            backupRestoreMode,
            backupRestoreProofLocation,
            migrationRoot,
            migrationIdentity.Database,
            migrationIdentity.Username,
            runtimeIdentity.Username,
            revocationEvidence,
            options);
    }

    private static CortexPostgreSqlTlsPolicy ParseTlsPolicy(
        IReadOnlyDictionary<string, string?> values)
    {
        values.TryGetValue(TlsPolicyVariable, out var value);
        return value switch
        {
            null or "" or "verify-full" => CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull,
            "trusted-loopback" => CortexPostgreSqlTlsPolicy.TrustedLoopback,
            _ => throw Failure("fixture_tls_policy_invalid")
        };
    }

    private static FixtureRevocationEvidence? RequireNoRevocationEvidence(
        IReadOnlyDictionary<string, string?> values)
    {
        if (values.TryGetValue(FixtureRevocationEvidence.EvidenceRootVariable, out var root) &&
            !string.IsNullOrWhiteSpace(root) ||
            values.TryGetValue(FixtureRevocationEvidence.ManifestFileVariable, out var manifest) &&
            !string.IsNullOrWhiteSpace(manifest))
        {
            throw Failure("trusted_loopback_revocation_evidence_unexpected");
        }

        return null;
    }

    private static NpgsqlConnectionStringBuilder ParseConnection(string value, string failureCode)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            throw Failure(failureCode);
        }
    }

    private static FixtureBackupRestoreMode ParseBackupRestoreMode(
        IReadOnlyDictionary<string, string?> values)
    {
        if (!values.TryGetValue(BackupRestoreModeVariable, out var value) || string.IsNullOrEmpty(value))
            return FixtureBackupRestoreMode.None;

        return value switch
        {
            "capture" => FixtureBackupRestoreMode.Capture,
            "verify-restored-state" => FixtureBackupRestoreMode.VerifyRestoredState,
            _ => throw Failure("backup_restore_mode_invalid")
        };
    }

    private static FixtureBackupRestoreProofLocation? ParseBackupRestoreProofLocation(
        IReadOnlyDictionary<string, string?> values,
        FixtureBackupRestoreMode mode)
    {
        values.TryGetValue(BackupRestoreProofRoot, out var root);
        values.TryGetValue(BackupRestoreProofFile, out var fileName);
        values.TryGetValue(LegacyBackupRestoreProof, out var legacyPath);
        if (!string.IsNullOrWhiteSpace(legacyPath))
            throw Failure("backup_restore_legacy_proof_variable_unsupported");
        if (mode == FixtureBackupRestoreMode.None)
        {
            if (!string.IsNullOrWhiteSpace(root) || !string.IsNullOrWhiteSpace(fileName))
                throw Failure("backup_restore_proof_location_unexpected");
            return null;
        }
        if (string.IsNullOrWhiteSpace(root))
            throw Failure("backup_restore_proof_root_missing");
        if (string.IsNullOrWhiteSpace(fileName))
            throw Failure("backup_restore_proof_file_missing");
        return FixtureBackupRestoreProofLocation.Parse(root, fileName);
    }

    private static FixtureEndpointIdentity ValidateEndpoint(
        NpgsqlConnectionStringBuilder value,
        string failureCode)
    {
        var database = value.Database;
        var username = value.Username;
        var hostValue = value.Host;
        if (!IsFixtureDatabaseName(database) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(hostValue))
        {
            throw Failure(failureCode);
        }

        var hosts = hostValue.Split(',', StringSplitOptions.TrimEntries);
        var addresses = hosts
            .Select(ParseLoopbackHost)
            .ToArray();
        if (addresses.Length == 0 || addresses.Any(address => address is null))
            throw Failure(failureCode);

        return new FixtureEndpointIdentity(
            database!,
            username,
            addresses.Select(address => address!).ToArray(),
            value.Port);
    }

    private static bool IsFixtureDatabaseName(string? value) =>
        value is { Length: >= 8 and <= 63 } &&
        value.StartsWith("ctx_it_", StringComparison.Ordinal) &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string ValidateTransportPolicy(
        NpgsqlConnectionStringBuilder value,
        CortexPostgreSqlTlsPolicy tlsPolicy,
        string tlsFailureCode,
        string pathFailureCode)
    {
        if (tlsPolicy == CortexPostgreSqlTlsPolicy.TrustedLoopback)
        {
            if (value.SslMode != SslMode.Disable ||
                !string.IsNullOrWhiteSpace(value.RootCertificate) ||
                TrustBypassEnabled(value))
            {
                throw Failure(tlsFailureCode);
            }
            return string.Empty;
        }

        if (value.SslMode != SslMode.VerifyFull ||
            string.IsNullOrWhiteSpace(value.RootCertificate) ||
            TrustBypassEnabled(value))
        {
            throw Failure(tlsFailureCode);
        }
        if (!Path.IsPathFullyQualified(value.RootCertificate))
            throw Failure(pathFailureCode);

        try
        {
            return Path.GetFullPath(value.RootCertificate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(pathFailureCode);
        }
    }

    private static bool TrustBypassEnabled(NpgsqlConnectionStringBuilder value) =>
        value.TryGetValue("Trust Server Certificate", out var setting) &&
        setting switch
        {
            bool booleanValue => booleanValue,
            string text when bool.TryParse(text, out var parsedValue) => parsedValue,
            _ => false
        };

    private static IPAddress? ParseLoopbackHost(string value)
    {
        var candidate = value.Length > 2 && value[0] == '[' && value[^1] == ']'
            ? value[1..^1]
            : value;
        return IPAddress.TryParse(candidate, out var address) && IPAddress.IsLoopback(address)
            ? address
            : null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Required(
        IReadOnlyDictionary<string, string?> values,
        string name,
        string failureCode)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw Failure(failureCode);
        return value;
    }

    private static FixtureConfigurationException Failure(string code) => new(code);

    private sealed record FixtureEndpointIdentity(
        string Database,
        string Username,
        IReadOnlyList<IPAddress> Addresses,
        int Port);
}
