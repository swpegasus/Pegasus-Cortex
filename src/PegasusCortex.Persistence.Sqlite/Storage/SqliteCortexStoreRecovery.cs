using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Security;

namespace Pegasus.Cortex.Persistence.Sqlite.Storage;

/// <summary>
/// Verified recovery operations kept outside the live repository API. Restore is
/// deliberately create-only so a caller cannot silently replace an active store.
/// </summary>
public static class SqliteCortexStoreRecovery
{
    public static async ValueTask RestoreNewAsync(
        string backupPath,
        SqliteCortexStoreOptions destinationOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinationOptions);
        var source = SqliteLocalPathPolicy.ValidateExistingBackupPath(backupPath);
        var destination = destinationOptions.Validate().DatabasePath;
        if (File.Exists(destination))
            throw new IOException("Restore never replaces an existing Cortex database.");
        SqliteLocalPathPolicy.EnsureSidecarsAbsent(
            destination,
            "A Cortex restore destination");
        RejectLiveSidecars(source);

        await VerifyBackupAsync(source, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetDirectoryName(destination)!;
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(destinationDirectory);
        SqliteLocalAclPolicy.ProtectDirectory(destinationDirectory);

        var stagingPath = destination + ".restore-partial";
        SqliteLocalPathPolicy.EnsureFileEntryIsSafe(
            stagingPath,
            "The Cortex restore staging file");
        SqliteLocalPathPolicy.EnsureSidecarsAbsent(
            stagingPath,
            "The Cortex restore staging file");
        if (File.Exists(stagingPath))
            throw new IOException("The restore staging file already exists.");
        try
        {
            File.Copy(source, stagingPath, overwrite: false);
            SqliteLocalAclPolicy.ProtectFile(stagingPath);
            await VerifyBackupAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination);
        }
        catch
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
            throw;
        }
    }

    private static async ValueTask VerifyBackupAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyBackupCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The Cortex backup is not a valid supported SQLite store.", exception);
        }
    }

    private static async ValueTask VerifyBackupCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var configuration = connection.CreateCommand())
        {
            configuration.CommandText = """
                PRAGMA query_only = ON;
                PRAGMA foreign_keys = ON;
                PRAGMA trusted_schema = OFF;
                """;
            await configuration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SqliteCortexSchemaVerifier.VerifyAsync(
            connection,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await SqliteCortexRetentionStore.RequireSqliteIntegrityAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var documents = await SqliteCortexRetentionStore.ReadAndValidateDocumentsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        SqliteCortexRetentionStore.ValidateRevisionChains(documents);
        await SqliteCortexRetentionStore.ValidateHeadsAsync(
            connection,
            transaction,
            documents,
            cancellationToken).ConfigureAwait(false);
        _ = await SqliteCortexRetentionStore.ValidateAuditAsync(
            connection,
            transaction,
            documents,
            cancellationToken).ConfigureAwait(false);
        await SqliteCortexRetentionStore.ValidateSearchIndexAsync(
            connection,
            transaction,
            documents,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RejectLiveSidecars(string backupPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = backupPath + suffix;
            SqliteLocalPathPolicy.EnsureFileEntryIsSafe(
                sidecar,
                "A Cortex restore sidecar");
            if (File.Exists(sidecar))
            {
                throw new InvalidDataException(
                    "A Cortex backup with live WAL or shared-memory sidecars cannot be restored.");
            }
        }
    }

}
