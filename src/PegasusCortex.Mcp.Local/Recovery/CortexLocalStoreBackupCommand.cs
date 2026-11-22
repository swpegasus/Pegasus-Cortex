using System.Globalization;
using System.Security.Cryptography;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalStoreBackupManifest(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string ProfileId,
    string CapturedAtUtc,
    string RunIdHash,
    string RegistryHash,
    string RegistrySha256,
    long RegistryBytes,
    string DatabaseSha256,
    long DatabaseBytes,
    string SourceDatabasePathHash,
    string DestinationRootHash,
    string CommandHash,
    string ManifestHash);

internal sealed record CortexLocalStoreBackupResult(
    bool Accepted,
    string ProfileId,
    string BackupRelativePath,
    string DatabaseSha256,
    string RegistrySha256,
    string ManifestHash);

internal sealed record CortexLocalStoreRestoreResult(
    bool Accepted,
    string ProfileId,
    string DatabaseSha256,
    string RegistrySha256,
    string ManifestHash);

internal sealed record CortexLocalStoreBackupCommandBinding(
    string ProfileId,
    string CapturedAtUtc,
    string RunIdHash,
    string RegistryHash,
    string SourceDatabasePathHash,
    string DestinationRootHash);

/// <summary>
/// Creates an exact SQLite backup and binds it to the protected project
/// registry. The command records hashes instead of machine-specific paths so
/// the sealed evidence does not leak local layout information.
/// </summary>
internal static class CortexLocalStoreBackupCommand
{
    private const int ManifestSchemaVersion = 1;
    private const string ManifestContractId =
        "pegasus.cortex.local-store-backup/1.0";
    private const string Canonicalization = "pegasus-canonical-json-v1";
    private const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CortexLocalStoreBackupArguments options;
        try
        {
            options = CortexLocalStoreBackupArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_store_backup_arguments_invalid",
                exception).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await BackupAsync(options, cancellation.Token)
                .ConfigureAwait(false);
            await WriteResultAsync(result, cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_store_backup_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalStoreBackupResult> BackupAsync(
        CortexLocalStoreBackupArguments options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            options.StatePaths.RegistryPath,
            options.ProfileId,
            cancellationToken).ConfigureAwait(false);

        var destination = options.DestinationRoot;
        foreach (var project in registry.Document.Projects)
        {
            destination = CortexLocalRecoveryExportPathPolicy.RequireDestinationRoot(
                destination,
                project.RootPath,
                options.StatePaths.StateRoot);
        }

        var outputRoot = CortexLocalStoreBackupPathPolicy.OutputRoot(
            destination,
            options.RunId);
        var stagingRoot = CortexLocalStoreBackupPathPolicy.StagingRoot(
            destination,
            options.RunId);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
            throw new IOException("The sealed local backup already exists.");

        var created = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            created = true;
            CortexLocalRegistryAclPolicy.ProtectDirectory(stagingRoot);

            var backupDatabase = Path.Combine(stagingRoot, "cortex.db");
            await using (var composition =
                await CortexMcpLocalComposition.CreateAsync(
                    options.ProfileId,
                    options.StatePaths.DatabasePath,
                    options.StatePaths.RegistryPath,
                    "backup-local",
                    TimeProvider.System,
                    cancellationToken).ConfigureAwait(false))
            {
                await composition.Store.BackupAsync(
                    backupDatabase,
                    cancellationToken).ConfigureAwait(false);
            }

            var registryBytes = await File.ReadAllBytesAsync(
                options.StatePaths.RegistryPath,
                cancellationToken).ConfigureAwait(false);
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                stagingRoot,
                "projects.json",
                registryBytes,
                cancellationToken).ConfigureAwait(false);

            var capturedAt = options.CapturedAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var runIdHash = CortexCanonicalJson.Sha256(options.RunId);
            var sourcePathHash = CortexCanonicalJson.Sha256(
                options.StatePaths.DatabasePath);
            var destinationHash = CortexCanonicalJson.Sha256(destination);
            var binding = new CortexLocalStoreBackupCommandBinding(
                options.ProfileId,
                capturedAt,
                runIdHash,
                registry.Document.RegistryHash,
                sourcePathHash,
                destinationHash);
            var commandHash = CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(binding));
            var databaseHash = await HashFileAsync(
                backupDatabase,
                cancellationToken).ConfigureAwait(false);
            var registrySha256 = CortexCanonicalJson.Sha256(registryBytes);
            var template = new CortexLocalStoreBackupManifest(
                ManifestSchemaVersion,
                ManifestContractId,
                Canonicalization,
                options.ProfileId,
                capturedAt,
                runIdHash,
                registry.Document.RegistryHash,
                registrySha256,
                registryBytes.LongLength,
                databaseHash,
                new FileInfo(backupDatabase).Length,
                sourcePathHash,
                destinationHash,
                commandHash,
                EmptySha256);
            var manifest = template with
            {
                ManifestHash = CortexCanonicalJson.Sha256(
                    CortexCanonicalJson.Serialize(template))
            };
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                stagingRoot,
                "manifest.json",
                CortexCanonicalJson.Serialize(manifest),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(stagingRoot, outputRoot);
            created = false;
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(outputRoot);
            return new CortexLocalStoreBackupResult(
                true,
                options.ProfileId,
                Path.GetRelativePath(destination, outputRoot).Replace('\\', '/'),
                databaseHash,
                registrySha256,
                manifest.ManifestHash);
        }
        catch
        {
            if (created)
            {
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    destination,
                    stagingRoot);
            }
            throw;
        }
    }

    internal static CortexLocalStoreBackupManifest ValidateManifest(
        CortexLocalStoreBackupManifest manifest,
        string expectedProfileId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        CortexLocalProjectRegistryPolicy.RequireProfile(expectedProfileId);
        if (manifest.SchemaVersion != ManifestSchemaVersion ||
            !string.Equals(manifest.ContractId, ManifestContractId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Canonicalization, Canonicalization, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProfileId, expectedProfileId, StringComparison.Ordinal) ||
            !CanonicalUtc(manifest.CapturedAtUtc) ||
            !Sha256(manifest.RunIdHash) ||
            !Sha256(manifest.RegistryHash) ||
            !Sha256(manifest.RegistrySha256) ||
            manifest.RegistryBytes < 2 ||
            !Sha256(manifest.DatabaseSha256) ||
            manifest.DatabaseBytes < 1 ||
            !Sha256(manifest.SourceDatabasePathHash) ||
            !Sha256(manifest.DestinationRootHash) ||
            !Sha256(manifest.CommandHash) ||
            !Sha256(manifest.ManifestHash))
        {
            throw new InvalidDataException("The local-store backup manifest is invalid.");
        }

        var binding = new CortexLocalStoreBackupCommandBinding(
            manifest.ProfileId,
            manifest.CapturedAtUtc,
            manifest.RunIdHash,
            manifest.RegistryHash,
            manifest.SourceDatabasePathHash,
            manifest.DestinationRootHash);
        if (!string.Equals(
                manifest.CommandHash,
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(binding)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local-store backup command binding is invalid.");
        }

        var template = manifest with { ManifestHash = EmptySha256 };
        if (!string.Equals(
                manifest.ManifestHash,
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(template)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local-store backup manifest hash is invalid.");
        }
        return manifest;
    }

    internal static async ValueTask<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static bool CanonicalUtc(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) &&
        parsed.Offset == TimeSpan.Zero &&
        string.Equals(
            value,
            parsed.ToString("O", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async ValueTask WriteResultAsync(
        CortexLocalStoreBackupResult result,
        CancellationToken cancellationToken)
    {
        var output = Console.OpenStandardOutput();
        await output.WriteAsync(
            CortexCanonicalJson.Serialize(result),
            cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask WriteDiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 512)
            message = message[..512];
        await diagnostics.WriteLineAsync(
            $"severity=error operation=store.backup code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}

internal static class CortexLocalStoreBackupPathPolicy
{
    public static string OutputRoot(string destinationRoot, string runId)
    {
        var output = Path.Combine(
            destinationRoot,
            $"backup-{CortexCanonicalJson.Sha256(runId)[..24]}");
        CortexLocalRecoveryExportPathPolicy.RequireOutputPath(destinationRoot, output);
        return output;
    }

    public static string StagingRoot(string destinationRoot, string runId)
    {
        var output = Path.Combine(
            destinationRoot,
            $".backup-{CortexCanonicalJson.Sha256(runId)[..16]}-{Guid.NewGuid():N}");
        CortexLocalRecoveryExportPathPolicy.RequireOutputPath(destinationRoot, output);
        return output;
    }

    public static string RequireBackupRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("An exact local backup root is required.", nameof(value));
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(canonical, value, StringComparison.Ordinal) ||
            !Directory.Exists(canonical))
        {
            throw new ArgumentException("The local backup root must exist and be canonical.", nameof(value));
        }
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(canonical);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(canonical);
        var info = new DirectoryInfo(canonical);
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The local backup root cannot be a link or reparse point.");
        }
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(canonical);
        return canonical;
    }
}

/// <summary>
/// Restores a sealed backup only into a new protected profile root. Registry
/// identity and every file hash are verified before the SQLite restore begins.
/// </summary>
internal static class CortexLocalStoreRestoreCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);
        CortexLocalStoreRestoreArguments options;
        try
        {
            options = CortexLocalStoreRestoreArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await CortexLocalStoreBackupCommand.WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_store_restore_arguments_invalid",
                exception).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await RestoreAsync(options, cancellation.Token)
                .ConfigureAwait(false);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(
                CortexCanonicalJson.Serialize(result),
                cancellation.Token).ConfigureAwait(false);
            await output.WriteAsync(
                "\n"u8.ToArray(),
                cancellation.Token).ConfigureAwait(false);
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await CortexLocalStoreBackupCommand.WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_store_restore_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalStoreRestoreResult> RestoreAsync(
        CortexLocalStoreRestoreArguments options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backupRoot = CortexLocalStoreBackupPathPolicy.RequireBackupRoot(
            options.BackupRoot);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "cortex.db",
            "projects.json",
            "manifest.json"
        };
        var entries = Directory.EnumerateFileSystemEntries(backupRoot).ToArray();
        if (entries.Length != allowed.Count ||
            entries.Any(entry =>
                Directory.Exists(entry) ||
                !allowed.Contains(Path.GetFileName(entry))))
        {
            throw new InvalidDataException("The local backup contains unexpected entries.");
        }

        var manifestPath = Path.Combine(backupRoot, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        if (manifestBytes.Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("The local backup manifest is outside its byte limit.");
        CortexLocalStoreBackupManifest manifest;
        try
        {
            manifest = CortexCanonicalJson.Deserialize<CortexLocalStoreBackupManifest>(
                manifestBytes);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException("The local backup manifest is malformed.", exception);
        }
        if (!CortexCanonicalJson.Serialize(manifest).AsSpan().SequenceEqual(manifestBytes))
            throw new InvalidDataException("The local backup manifest is not canonical JSON.");
        manifest = CortexLocalStoreBackupCommand.ValidateManifest(
            manifest,
            options.ProfileId);

        var databasePath = Path.Combine(backupRoot, "cortex.db");
        var registryPath = Path.Combine(backupRoot, "projects.json");
        var databaseInfo = new FileInfo(databasePath);
        var registryInfo = new FileInfo(registryPath);
        if (databaseInfo.Length != manifest.DatabaseBytes ||
            registryInfo.Length != manifest.RegistryBytes ||
            !string.Equals(
                await CortexLocalStoreBackupCommand.HashFileAsync(
                    databasePath,
                    cancellationToken).ConfigureAwait(false),
                manifest.DatabaseSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                await CortexLocalStoreBackupCommand.HashFileAsync(
                    registryPath,
                    cancellationToken).ConfigureAwait(false),
                manifest.RegistrySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local backup file hashes do not match its manifest.");
        }

        var registryBytes = await File.ReadAllBytesAsync(
            registryPath,
            cancellationToken).ConfigureAwait(false);
        var registryDocument = CortexCanonicalJson.Deserialize<
            CortexLocalProjectRegistryDocument>(registryBytes);
        var validatedRegistry = CortexLocalProjectRegistryPolicy.Validate(
            registryDocument,
            options.ProfileId,
            validateRoots: true);
        if (!string.Equals(
                validatedRegistry.RegistryHash,
                manifest.RegistryHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local backup registry binding does not match.");
        }

        var destinationRoot = options.DestinationPaths.StateRoot;
        if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot))
            throw new IOException("Restore never replaces an existing local profile root.");
        var parent = Path.GetDirectoryName(destinationRoot) ??
            throw new ArgumentException("The restore destination has no parent.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The restore destination parent must exist.");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);

        var created = false;
        try
        {
            Directory.CreateDirectory(destinationRoot);
            created = true;
            CortexLocalRegistryAclPolicy.ProtectDirectory(destinationRoot);
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                destinationRoot,
                "projects.json",
                registryBytes,
                cancellationToken).ConfigureAwait(false);
            await SqliteCortexStoreRecovery.RestoreNewAsync(
                databasePath,
                new SqliteCortexStoreOptions(options.DestinationPaths.DatabasePath),
                cancellationToken).ConfigureAwait(false);
            _ = await CortexLocalProjectRegistry.LoadAsync(
                options.DestinationPaths.RegistryPath,
                options.ProfileId,
                cancellationToken).ConfigureAwait(false);
            await using var restored = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(options.DestinationPaths.DatabasePath));
            await restored.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await restored.CheckIntegrityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!integrity.Healthy)
                throw new InvalidDataException("The restored local store failed integrity validation.");

            return new CortexLocalStoreRestoreResult(
                true,
                options.ProfileId,
                manifest.DatabaseSha256,
                manifest.RegistrySha256,
                manifest.ManifestHash);
        }
        catch
        {
            if (created)
            {
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    parent,
                    destinationRoot);
            }
            throw;
        }
    }
}
