using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Npgsql;

namespace Pegasus.Cortex.Service.Configuration;

public enum CortexPostgreSqlTlsPolicy
{
    PrivateCaVerifyFull,
    LocalLoopbackBootstrap,
    Pinned,
    TrustedLoopback
}

/// <summary>
/// Non-secret PostgreSQL configuration details that are safe to emit in diagnostics.
/// Host names, database names, user names, certificate paths, and connection strings are
/// deliberately excluded.
/// </summary>
public sealed record CortexPostgreSqlConfigurationSummary(
    string RuntimeDataSourceName,
    string MigrationDataSourceName,
    CortexPostgreSqlTlsPolicy TlsPolicy,
    int RuntimeMaximumPoolSize,
    bool RuntimePooling,
    bool MigrationPooling,
    bool RootCertificateConfigured,
    bool ServerCertificatePinConfigured)
{
    public override string ToString() =>
        $"runtimeSource={RuntimeDataSourceName}; migrationSource={MigrationDataSourceName}; " +
        $"tlsPolicy={TlsPolicy}; runtimeMaximumPoolSize={RuntimeMaximumPoolSize}; " +
        $"runtimePooling={RuntimePooling}; migrationPooling={MigrationPooling}; " +
        $"rootCertificateConfigured={RootCertificateConfigured}; " +
        $"serverCertificatePinConfigured={ServerCertificatePinConfigured}";
}

/// <summary>
/// Reports configuration failure without retaining connection strings or certificate paths.
/// </summary>
public sealed class CortexPostgreSqlConfigurationException : Exception
{
    internal CortexPostgreSqlConfigurationException(
        string code,
        string operation,
        string? causeType = null)
        : base($"Pegasus Cortex PostgreSQL configuration failed with code '{code}'.")
    {
        Code = code;
        Operation = operation;
        CauseType = causeType;
    }

    public string Code { get; }
    public string Operation { get; }
    public string? CauseType { get; }
}

/// <summary>
/// Captures host-supplied PostgreSQL settings. Connection strings may contain secrets and
/// must come from the host's protected configuration source; this type never includes them
/// in its string representation or diagnostic summary.
/// </summary>
public sealed class CortexPostgreSqlOptions
{
    public const int DefaultRuntimeMaximumPoolSize = 8;
    public const int MinimumRuntimeMaximumPoolSize = 1;
    public const int MaximumRuntimeMaximumPoolSize = 32;
    public const int DefaultMigrationCommandTimeoutSeconds = 120;
    public const int MaximumConnectionStringCharacters = 16 * 1024;
    public const int MaximumRootCertificatePathCharacters = 240;
    public const int MaximumRootCertificateBytes = 1024 * 1024;
    public const int MaximumHostCharacters = 1024;
    public const int MaximumDatabaseCharacters = 128;
    public const int MaximumUsernameCharacters = 128;
    public const int ServerCertificateSpkiSha256Characters = 64;

    private readonly string _runtimeConnectionString;
    private readonly string? _migrationConnectionString;
    private readonly string? _rootCertificatePath;
    private readonly string? _serverCertificateSpkiSha256;

    public CortexPostgreSqlOptions(
        string runtimeConnectionString,
        string? migrationConnectionString,
        string? rootCertificatePath,
        int runtimeMaximumPoolSize = DefaultRuntimeMaximumPoolSize,
        TimeSpan? migrationCommandTimeout = null,
        CortexPostgreSqlTlsPolicy tlsPolicy = CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull,
        string? serverCertificateSpkiSha256 = null)
    {
        _runtimeConnectionString = runtimeConnectionString;
        _migrationConnectionString = migrationConnectionString;
        _rootCertificatePath = rootCertificatePath;
        _serverCertificateSpkiSha256 = serverCertificateSpkiSha256;
        RuntimeMaximumPoolSize = runtimeMaximumPoolSize;
        MigrationCommandTimeout = migrationCommandTimeout ??
            TimeSpan.FromSeconds(DefaultMigrationCommandTimeoutSeconds);
        TlsPolicy = tlsPolicy;
    }

    public int RuntimeMaximumPoolSize { get; }
    public TimeSpan MigrationCommandTimeout { get; }
    public CortexPostgreSqlTlsPolicy TlsPolicy { get; }

    public CortexPostgreSqlConfigurationSummary ValidateConfiguration() =>
        ValidateMigration().Summary;

    /// <summary>
    /// Validates only the process-lifetime runtime settings. The privileged migration
    /// connection is intentionally optional here so an installed runtime does not retain
    /// migration credentials after the schema and grants have been reconciled.
    /// </summary>
    public CortexPostgreSqlConfigurationSummary ValidateRuntimeConfiguration() =>
        ValidateRuntime().Summary;

    public CortexPostgreSqlConfigurationSummary GetSanitizedSummary() => new(
        Persistence.CortexPostgreSqlDataSourceFactory.RuntimeDataSourceName,
        Persistence.CortexPostgreSqlDataSourceFactory.MigrationDataSourceName,
        TlsPolicy,
        RuntimeMaximumPoolSize,
        RuntimePooling: true,
        MigrationPooling: false,
        RootCertificateConfigured:
            TlsPolicy is CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull or
                CortexPostgreSqlTlsPolicy.Pinned &&
            !string.IsNullOrWhiteSpace(_rootCertificatePath),
        ServerCertificatePinConfigured:
            TlsPolicy == CortexPostgreSqlTlsPolicy.Pinned &&
            !string.IsNullOrWhiteSpace(_serverCertificateSpkiSha256));

    public override string ToString() => GetSanitizedSummary().ToString();

    internal ValidatedCortexPostgreSqlOptions Validate() => ValidateMigration();

    internal ValidatedCortexPostgreSqlOptions ValidateRuntime() =>
        ValidateCore(requireMigrationConnection: false);

    internal ValidatedCortexPostgreSqlOptions ValidateMigration() =>
        ValidateCore(requireMigrationConnection: true);

    private ValidatedCortexPostgreSqlOptions ValidateCore(bool requireMigrationConnection)
    {
        const string operation = "cortex.postgresql.configure";

        if (TlsPolicy is not (CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull or
            CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap or
            CortexPostgreSqlTlsPolicy.Pinned or
            CortexPostgreSqlTlsPolicy.TrustedLoopback))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_tls_policy_invalid",
                operation);
        }

        if (RuntimeMaximumPoolSize is < MinimumRuntimeMaximumPoolSize or > MaximumRuntimeMaximumPoolSize)
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_runtime_pool_limit_invalid",
                operation);
        }

        var migrationSeconds = requireMigrationConnection
            ? MigrationCommandTimeout.TotalSeconds
            : DefaultMigrationCommandTimeoutSeconds;
        if (requireMigrationConnection &&
            (migrationSeconds is < 1 or > 600 ||
             migrationSeconds != Math.Truncate(migrationSeconds)))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_migration_timeout_invalid",
                operation);
        }

        var rootCertificatePath = TlsPolicy is
            CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull or CortexPostgreSqlTlsPolicy.Pinned
            ? ValidateRootCertificatePath(_rootCertificatePath, operation)
            : null;
        var serverCertificateSpkiSha256 = TlsPolicy == CortexPostgreSqlTlsPolicy.Pinned
            ? ValidateServerCertificateSpkiSha256(_serverCertificateSpkiSha256, operation)
            : null;
        var runtime = ParseConnectionString(
            _runtimeConnectionString,
            "cortex_postgresql_runtime_connection_invalid",
            operation);
        ValidateConnectionRole(runtime, "cortex_postgresql_runtime_role_invalid", operation);
        ValidatePolicyHosts(runtime, TlsPolicy, operation);

        NpgsqlConnectionStringBuilder? migration = null;
        if (requireMigrationConnection)
        {
            migration = ParseConnectionString(
                _migrationConnectionString,
                "cortex_postgresql_migration_connection_invalid",
                operation);
            ValidateConnectionRole(
                migration,
                "cortex_postgresql_migration_role_invalid",
                operation);
            ValidatePolicyHosts(migration, TlsPolicy, operation);

            if (string.Equals(
                    runtime.Username,
                    migration.Username,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CortexPostgreSqlConfigurationException(
                    "cortex_postgresql_roles_not_separated",
                    operation);
            }
        }

        var summary = GetSanitizedSummary();
        return new ValidatedCortexPostgreSqlOptions(
            runtime.ConnectionString,
            migration?.ConnectionString,
            rootCertificatePath,
            RuntimeMaximumPoolSize,
            checked((int)migrationSeconds),
            TlsPolicy,
            serverCertificateSpkiSha256,
            summary);
    }

    private static string ValidateServerCertificateSpkiSha256(
        string? value,
        string operation)
    {
        if (value is null ||
            value.Length != ServerCertificateSpkiSha256Characters ||
            value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_server_spki_pin_invalid",
                operation);
        }

        return value;
    }

    private static string ValidateRootCertificatePath(string? value, string operation)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_path_invalid",
                operation);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }

        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_path_invalid",
                operation,
                exception.GetType().FullName);
        }

        if (fullPath.Length > MaximumRootCertificatePathCharacters)
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_path_too_long",
                operation);
        }

        if (!File.Exists(fullPath))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_missing",
                operation);
        }

        ValidateRootCertificateContents(fullPath, operation);

        return fullPath;
    }

    private static void ValidateRootCertificateContents(string fullPath, string operation)
    {
        byte[] contents;
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaximumRootCertificateBytes)
            {
                throw new CortexPostgreSqlConfigurationException(
                    "cortex_postgresql_root_certificate_size_invalid",
                    operation);
            }

            contents = new byte[checked((int)stream.Length)];
            stream.ReadExactly(contents);
        }
        catch (CortexPostgreSqlConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_unavailable",
                operation,
                exception.GetType().FullName);
        }

        var certificates = new X509Certificate2Collection();
        try
        {
            // Reject key material while it is still in a zeroable buffer. Converting a
            // private key to an immutable string would retain a secret until a later GC.
            if (contents.AsSpan().IndexOf("PRIVATE KEY"u8) >= 0)
            {
                throw new CryptographicException(
                    "A trust-anchor file must not contain private key material.");
            }

            var pem = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(contents);
            certificates.ImportFromPem(pem);
            if (certificates.Count == 0)
                throw new CryptographicException("No certificates were present.");
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException or DecoderFallbackException)
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_root_certificate_invalid",
                operation,
                exception.GetType().FullName);
        }
        finally
        {
            foreach (X509Certificate2 certificate in certificates)
                certificate.Dispose();
            CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static NpgsqlConnectionStringBuilder ParseConnectionString(
        string? value,
        string code,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumConnectionStringCharacters)
            throw new CortexPostgreSqlConfigurationException(code, operation);

        try
        {
            return new NpgsqlConnectionStringBuilder(value);
        }
        catch (Exception exception)
        {
            // Npgsql parser messages can echo secret-bearing input. Preserve only the
            // cause type and a stable code at this boundary.
            throw new CortexPostgreSqlConfigurationException(
                code,
                operation,
                exception.GetType().FullName);
        }
    }

    private static void ValidateConnectionRole(
        NpgsqlConnectionStringBuilder builder,
        string code,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(builder.Host) || builder.Host.Length > MaximumHostCharacters ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            builder.Database.Length > MaximumDatabaseCharacters ||
            string.IsNullOrWhiteSpace(builder.Username) ||
            builder.Username.Length > MaximumUsernameCharacters ||
            builder.Host.Any(char.IsControl) ||
            builder.Database.Any(char.IsControl) ||
            builder.Username.Any(char.IsControl))
        {
            throw new CortexPostgreSqlConfigurationException(code, operation);
        }
    }

    private static void ValidatePolicyHosts(
        NpgsqlConnectionStringBuilder builder,
        CortexPostgreSqlTlsPolicy policy,
        string operation)
    {
        switch (policy)
        {
            case CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap:
                ValidateLiteralLoopbackHosts(
                    builder,
                    "cortex_postgresql_local_bootstrap_host_invalid",
                    operation);
                break;
            case CortexPostgreSqlTlsPolicy.TrustedLoopback:
                ValidateLiteralLoopbackHosts(
                    builder,
                    "cortex_postgresql_trusted_loopback_host_invalid",
                    operation);
                break;
            case CortexPostgreSqlTlsPolicy.Pinned:
                ValidatePinnedLoopbackHost(builder, operation);
                break;
        }
    }

    private static void ValidateLiteralLoopbackHosts(
        NpgsqlConnectionStringBuilder builder,
        string code,
        string operation)
    {
        var hosts = (builder.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(host =>
                host.Length == 0 ||
                !(string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
                  string.Equals(host, "::1", StringComparison.Ordinal))))
        {
            throw new CortexPostgreSqlConfigurationException(
                code,
                operation);
        }
    }

    private static void ValidatePinnedLoopbackHost(
        NpgsqlConnectionStringBuilder builder,
        string operation)
    {
        var hosts = (builder.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (hosts.Length != 1 ||
            !(string.Equals(hosts[0], "localhost", StringComparison.Ordinal) ||
              string.Equals(hosts[0], "127.0.0.1", StringComparison.Ordinal) ||
              string.Equals(hosts[0], "::1", StringComparison.Ordinal)))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_pinned_loopback_host_invalid",
                operation);
        }
    }
}

internal sealed class ValidatedCortexPostgreSqlOptions
{
    internal ValidatedCortexPostgreSqlOptions(
        string runtimeConnectionString,
        string? migrationConnectionString,
        string? rootCertificatePath,
        int runtimeMaximumPoolSize,
        int migrationCommandTimeoutSeconds,
        CortexPostgreSqlTlsPolicy tlsPolicy,
        string? serverCertificateSpkiSha256,
        CortexPostgreSqlConfigurationSummary summary)
    {
        RuntimeConnectionString = runtimeConnectionString;
        MigrationConnectionString = migrationConnectionString;
        RootCertificatePath = rootCertificatePath;
        RuntimeMaximumPoolSize = runtimeMaximumPoolSize;
        MigrationCommandTimeoutSeconds = migrationCommandTimeoutSeconds;
        TlsPolicy = tlsPolicy;
        ServerCertificateSpkiSha256 = serverCertificateSpkiSha256;
        Summary = summary;
    }

    internal string RuntimeConnectionString { get; }
    internal string? MigrationConnectionString { get; }
    internal string? RootCertificatePath { get; }
    internal int RuntimeMaximumPoolSize { get; }
    internal int MigrationCommandTimeoutSeconds { get; }
    internal CortexPostgreSqlTlsPolicy TlsPolicy { get; }
    internal string? ServerCertificateSpkiSha256 { get; }
    internal CortexPostgreSqlConfigurationSummary Summary { get; }

    public override string ToString() => Summary.ToString();
}
