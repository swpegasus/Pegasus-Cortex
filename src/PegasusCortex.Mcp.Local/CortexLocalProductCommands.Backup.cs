using System.Globalization;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.V2;

public static partial class CortexLocalProductCommands
{
    internal const int MaximumBackupManifestUtf8Bytes = 8 * 1024;
    private const string BackupManifestSchema = "pegasus.cortex.central-backup-manifest/1.0";
    private const string BackupManifestFileName = "manifest.json";

    public static ValueTask<(int ExitCode, string Json)> BackupAsync(
        CancellationToken cancellationToken) =>
        BackupAsync(
            CortexCurrentUserLayout.Resolve(),
            TimeProvider.System,
            cancellationToken);

    internal static async ValueTask<(int ExitCode, string Json)> BackupAsync(
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(timeProvider);
        string? ownedStagingDirectory = null;
        var committed = false;
        try
        {
            ValidateBackupLayout(layout);
            var options = new SqliteCortexV2StoreOptions(layout.V2DatabasePath).Validate();
            if (!File.Exists(options.DatabasePath))
            {
                return (1, JsonSerializer.Serialize(new
                {
                    healthy = false,
                    state = "uninitialized",
                    error = "The central Cortex v2 store is uninitialized; no backup was created.",
                    storeGeneration = 2,
                    network = false,
                    service = false
                }, JsonOptions));
            }

            layout.EnsureCreated();
            cancellationToken.ThrowIfCancellationRequested();
            var requestedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
            var directoryName = "cortex-v2-" + requestedAtUtc.ToString(
                "yyyyMMdd'T'HHmmss'.'fffffff'Z'",
                CultureInfo.InvariantCulture);
            var finalDirectory = Path.GetFullPath(Path.Combine(layout.BackupDirectory, directoryName));
            RequireDirectChild(layout.BackupDirectory, finalDirectory, "backup destination");
            RequireAbsent(finalDirectory, "The timestamped Cortex backup destination");

            ownedStagingDirectory = Path.Combine(
                layout.BackupDirectory,
                $".b-{Guid.NewGuid():N}");
            RequireDirectChild(layout.BackupDirectory, ownedStagingDirectory, "backup staging directory");
            RequireAbsent(ownedStagingDirectory, "The Cortex backup staging directory");
            Directory.CreateDirectory(ownedStagingDirectory);
            var stagingInfo = new DirectoryInfo(ownedStagingDirectory);
            if ((stagingInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                stagingInfo.LinkTarget is not null)
            {
                throw new IOException("The Cortex backup staging directory cannot be a link or reparse point.");
            }
            CortexLocalRegistryAclPolicy.ProtectDirectory(ownedStagingDirectory);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(ownedStagingDirectory);

            var stagingDatabase = Path.Combine(
                ownedStagingDirectory,
                SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
            await using var store = new SqliteCortexV2Store(options, timeProvider);
            var backup = await store.BackupAsync(stagingDatabase, cancellationToken).ConfigureAwait(false);
            var manifest = new CortexLocalProductBackupManifest(
                BackupManifestSchema,
                Version,
                StoreGeneration: 2,
                backup.SchemaVersion,
                backup.CreatedAtUtc.ToUniversalTime(),
                SqliteCortexV2StoreOptions.RequiredDatabaseFileName,
                backup.BackupBytes,
                backup.Sha256,
                backup.NamespaceRows,
                backup.RevisionRows,
                backup.HeadRows);
            var manifestUtf8 = CortexCanonicalJson.Serialize(manifest);
            if (manifestUtf8.Length > MaximumBackupManifestUtf8Bytes)
                throw new InvalidDataException("The Cortex backup manifest exceeds its byte limit.");

            var manifestPath = Path.Combine(ownedStagingDirectory, BackupManifestFileName);
            await WriteNewFileDurablyAsync(manifestPath, manifestUtf8, cancellationToken)
                .ConfigureAwait(false);
            RequireExactBackupEntries(ownedStagingDirectory);
            RequireAbsent(finalDirectory, "The timestamped Cortex backup destination");
            Directory.Move(ownedStagingDirectory, finalDirectory);
            committed = true;

            return (0, JsonSerializer.Serialize(new
            {
                healthy = true,
                state = "created",
                backupDirectory = finalDirectory,
                snapshot = Path.Combine(finalDirectory, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
                manifest = Path.Combine(finalDirectory, BackupManifestFileName),
                receipt = manifest,
                network = false,
                service = false
            }, JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 512)
                message = message[..512];
            return (1, JsonSerializer.Serialize(new
            {
                healthy = false,
                state = "backup-failed",
                error = message,
                network = false,
                service = false
            }, JsonOptions));
        }
        finally
        {
            if (!committed && ownedStagingDirectory is not null)
                CleanupOwnedBackupStaging(ownedStagingDirectory);
        }
    }

    private static void ValidateBackupLayout(CortexCurrentUserLayout layout)
    {
        var root = Path.GetFullPath(layout.Root);
        var semanticUserRoot = Path.GetFullPath(layout.SemanticUserRoot ?? layout.Root);
        var expectedCortex = Path.Combine(semanticUserRoot, "cortex");
        var expectedData = Path.Combine(expectedCortex, "data");
        var expectedBackup = Path.Combine(expectedCortex, "backups");
        if (!string.Equals(root, layout.Root, BackupPathComparison) ||
            !string.Equals(semanticUserRoot, layout.SemanticUserRoot ?? layout.Root, BackupPathComparison) ||
            !string.Equals(expectedData, layout.DataDirectory, BackupPathComparison) ||
            !string.Equals(expectedBackup, layout.BackupDirectory, BackupPathComparison) ||
            !string.Equals(
                Path.Combine(expectedData, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
                layout.V2DatabasePath,
                BackupPathComparison))
        {
            throw new ArgumentException(
                "The Cortex backup layout does not match the declared per-user semantic data and backup locations.",
                nameof(layout));
        }
        // Both product and SQLite backup transactions stage once. Validate the
        // full working filename before creating anything, not at a native open.
        var stage = ".b-" + new string('0', 32);
        if (OperatingSystem.IsWindows() && Path.Combine(expectedBackup, stage, stage, "cortex-v2.db-journal").Length >
            Pegasus.Cortex.Product.CortexProductLayout.MaximumWindowsPathCharacters)
            throw new PathTooLongException("The selected backup location exceeds the 240-character working-path budget.");
    }

    private static async ValueTask WriteNewFileDurablyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void RequireExactBackupEntries(string directory)
    {
        var expected = new HashSet<string>(BackupPathComparer)
        {
            Path.Combine(directory, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
            Path.Combine(directory, BackupManifestFileName)
        };
        var actual = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFullPath)
            .ToArray();
        if (actual.Length != expected.Count || actual.Any(path => !expected.Contains(path)))
            throw new InvalidDataException("The staged Cortex backup contains unexpected entries.");
        foreach (var path in actual)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A staged Cortex backup entry is not an ordinary file.");
            }
        }
    }

    private static void RequireDirectChild(string parent, string child, string description)
    {
        var canonicalParent = Path.GetFullPath(parent);
        var canonicalChild = Path.GetFullPath(child);
        if (!string.Equals(
                Path.GetDirectoryName(canonicalChild),
                canonicalParent,
                BackupPathComparison))
        {
            throw new IOException($"The Cortex {description} is outside the owned backup directory.");
        }
    }

    private static void RequireAbsent(string path, string description)
    {
        var file = new FileInfo(path);
        var directory = new DirectoryInfo(path);
        if (File.Exists(path) || Directory.Exists(path) ||
            file.LinkTarget is not null || directory.LinkTarget is not null)
        {
            throw new IOException($"{description} already exists.");
        }
    }

    private static void CleanupOwnedBackupStaging(string stagingDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(stagingDirectory);
            if (!directory.Exists || directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            var allowed = new HashSet<string>(BackupPathComparer)
            {
                Path.Combine(stagingDirectory, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
                Path.Combine(stagingDirectory, BackupManifestFileName)
            };
            var entries = Directory.EnumerateFileSystemEntries(stagingDirectory)
                .Select(Path.GetFullPath)
                .ToArray();
            if (entries.Any(path => !allowed.Contains(path) || Directory.Exists(path)))
                return;
            foreach (var path in entries)
            {
                var file = new FileInfo(path);
                if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return;
            }
            foreach (var path in entries)
                File.Delete(path);
            Directory.Delete(stagingDirectory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leave a uniquely named, task-owned staging directory rather than
            // broadening cleanup after a backup failure.
        }
    }

    private static StringComparer BackupPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison BackupPathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal sealed record CortexLocalProductBackupManifest(
    string Schema,
    string ProductVersion,
    int StoreGeneration,
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SnapshotFile,
    long SnapshotBytes,
    string SnapshotSha256,
    long NamespaceRows,
    long RevisionRows,
    long HeadRows);
