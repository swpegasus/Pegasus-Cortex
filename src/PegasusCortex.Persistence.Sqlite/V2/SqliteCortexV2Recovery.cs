namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>
/// Create-only recovery for the fresh SQLite generation. Restore copies an
/// immutable backup into an isolated same-parent staging directory, validates
/// that copy with both the constant-work check and exhaustive doctor, and only
/// then performs a same-volume no-overwrite move to an absent cortex-v2.db.
/// </summary>
public static class SqliteCortexV2Recovery
{
    public static async ValueTask<SqliteCortexV2RestoreReceipt> RestoreNewAsync(
        string backupPath,
        string destinationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        var source = SqliteCortexV2Store.ValidateIndependentDatabasePath(
            backupPath, differentFrom: null, mustExist: true);
        RejectLiveSidecars(source, "The Cortex v2 backup");
        var destination = SqliteCortexV2Store.ValidateIndependentDatabasePath(
            destinationDatabasePath, source, mustExist: false);
        SqliteCortexV2Store.EnsureParentDirectory(destination);
        SqliteCortexV2Store.RequireDatabaseEntryAbsent(destination, "The Cortex v2 restore destination");

        var parent = Path.GetDirectoryName(destination)!;
        var stagingDirectory = Path.Combine(parent, $".cortex-v2-restore-{Guid.NewGuid():N}");
        var staging = Path.Combine(stagingDirectory, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var committed = false;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            if ((new DirectoryInfo(stagingDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Cortex v2 restore staging directory cannot be a reparse point.");
            await CopySnapshotAsync(source, staging, cancellationToken).ConfigureAwait(false);
            var evidence = await ValidateSnapshotAsync(staging, cancellationToken).ConfigureAwait(false);
            RejectLiveSidecars(staging, "The validated Cortex v2 restore staging database");
            SqliteCortexV2Store.RequireDatabaseEntryAbsent(destination, "The Cortex v2 restore destination");
            var hash = await SqliteCortexV2Store.HashFileAsync(staging, cancellationToken).ConfigureAwait(false);
            var length = new FileInfo(staging).Length;
            File.Move(staging, destination, overwrite: false);
            committed = true;
            try
            {
                Directory.Delete(stagingDirectory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The validated destination is already committed. An empty
                // staging directory is safer than reporting restore failure.
            }
            return new(
                source,
                destination,
                length,
                hash,
                evidence.SchemaVersion,
                evidence.NamespaceRows,
                evidence.RevisionRows,
                evidence.HeadRows,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (!committed)
                CleanupOwnedStaging(stagingDirectory, staging);
        }
    }

    internal static async ValueTask<SqliteCortexV2StorageMetrics> ValidateSnapshotAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var verifier = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(databasePath));
            await verifier.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var quick = await verifier.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!quick.Healthy)
                throw new InvalidDataException("The Cortex v2 snapshot failed its bounded generation check.");
            await verifier.RequireSelfContainedSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var doctor = await verifier.FullDoctorAsync(cancellationToken).ConfigureAwait(false);
            if (!doctor.Healthy)
                throw new InvalidDataException("The Cortex v2 snapshot failed its exhaustive integrity doctor.");
            return await verifier.GetStorageMetricsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or FormatException ||
            exception.GetType().Namespace == "Microsoft.Data.Sqlite")
        {
            throw new InvalidDataException("The Cortex v2 snapshot is corrupt, locked, or not the supported generation.", exception);
        }
    }

    private static async ValueTask CopySnapshotAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void RejectLiveSidecars(string databasePath, string description)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = databasePath + suffix;
            _ = new SqliteCortexV2StoreOptions(databasePath).Validate();
            if (File.Exists(sidecar) || Directory.Exists(sidecar))
                throw new InvalidDataException($"{description} has a live SQLite sidecar.");
        }
    }

    internal static void CleanupOwnedStaging(string stagingDirectory, string staging)
    {
        if (!Directory.Exists(stagingDirectory))
            return;
        var directory = new DirectoryInfo(stagingDirectory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Cortex v2 restore staging directory became a reparse point; cleanup stopped.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(staging),
            Path.GetFullPath(staging + "-wal"),
            Path.GetFullPath(staging + "-shm")
        };
        var entries = Directory.EnumerateFileSystemEntries(stagingDirectory).Select(Path.GetFullPath).ToArray();
        if (entries.Any(path => !expected.Contains(path)))
            throw new IOException("The Cortex v2 restore staging directory contains an unexpected entry; cleanup stopped.");
        foreach (var path in entries)
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A Cortex v2 restore staging entry became a link; cleanup stopped.");
            File.Delete(path);
        }
        Directory.Delete(stagingDirectory, recursive: false);
    }
}
