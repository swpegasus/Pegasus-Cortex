using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public enum CortexStateMigrationDisposition
{
    Migrated,
    PreservedExisting,
    SourceAbsent
}

public sealed record CortexStateMigrationItemReceiptV1(
    string Role,
    CortexStateMigrationDisposition Disposition,
    CortexStateMigrationStrategy Strategy,
    long SourceBytes,
    long DestinationBytes,
    int? SqliteUserVersion,
    int? SqliteTableCount);

public sealed record CortexStateMigrationReceiptV1(
    string Contract,
    int Version,
    string OwnerSid,
    IReadOnlyList<CortexStateMigrationItemReceiptV1> Items);

public sealed class CortexStateMigrationExecutor
{
    private readonly string? _privateScratchBase;
    public CortexStateMigrationExecutor(string? privateScratchBase = null) =>
        _privateScratchBase = privateScratchBase;
    public const string ReceiptContract = "pegasus-cortex-state-migration-receipt";

    public async ValueTask<CortexStateMigrationReceiptV1> ExecuteAsync(
        CortexStateMigrationPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Contract != CortexStateMigrationPlanV1.ContractId || plan.Version != 1)
            throw new InvalidDataException("The Cortex state migration plan is unsupported.");

        var receipts = new List<CortexStateMigrationItemReceiptV1>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectExistingReparseComponents(item.SourcePath);
            RejectExistingReparseComponents(item.DestinationPath);

            if (File.Exists(item.DestinationPath))
            {
                var existing = item.Strategy == CortexStateMigrationStrategy.SqliteOnlineBackup
                    ? InspectCapturedDatabase(item.DestinationPath, cancellationToken)
                    : default(SqliteEvidence?);
                receipts.Add(new CortexStateMigrationItemReceiptV1(
                    item.Role,
                    CortexStateMigrationDisposition.PreservedExisting,
                    item.Strategy,
                    File.Exists(item.SourcePath) ? new FileInfo(item.SourcePath).Length : 0,
                    new FileInfo(item.DestinationPath).Length,
                    existing?.UserVersion,
                    existing?.TableCount));
                continue;
            }

            if (!File.Exists(item.SourcePath))
            {
                if (item.Required)
                    throw new FileNotFoundException(
                        $"Required migration source '{item.Role}' is unavailable.",
                        item.SourcePath);
                receipts.Add(new CortexStateMigrationItemReceiptV1(
                    item.Role,
                    CortexStateMigrationDisposition.SourceAbsent,
                    item.Strategy,
                    0,
                    0,
                    null,
                    null));
                continue;
            }

            var parent = Path.GetDirectoryName(item.DestinationPath) ??
                throw new InvalidDataException("A migration destination has no parent directory.");
            Directory.CreateDirectory(parent);
            RejectExistingReparseComponents(parent);
            var temporary = Path.Combine(
                parent,
                $".{Path.GetFileName(item.DestinationPath)}.migrating-{Guid.NewGuid():N}");
            try
            {
                SqliteEvidence? evidence;
                if (item.Strategy == CortexStateMigrationStrategy.SqliteOnlineBackup)
                {
                    var sourceEvidence = BackupDatabase(item.SourcePath, temporary, cancellationToken);
                    evidence = sourceEvidence.Destination;
                    if (sourceEvidence.Source.UserVersion != evidence.Value.UserVersion ||
                        sourceEvidence.Source.TableCount != evidence.Value.TableCount)
                    {
                        throw new InvalidDataException(
                            $"SQLite migration evidence differs for role '{item.Role}'.");
                    }
                }
                else
                {
                    await CopyExactAsync(item.SourcePath, temporary, cancellationToken)
                        .ConfigureAwait(false);
                    evidence = null;
                }

                File.Move(temporary, item.DestinationPath, overwrite: false);
                receipts.Add(new CortexStateMigrationItemReceiptV1(
                    item.Role,
                    CortexStateMigrationDisposition.Migrated,
                    item.Strategy,
                    new FileInfo(item.SourcePath).Length,
                    new FileInfo(item.DestinationPath).Length,
                    evidence?.UserVersion,
                    evidence?.TableCount));
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        return new CortexStateMigrationReceiptV1(
            ReceiptContract,
            1,
            plan.OwnerSid,
            receipts);
    }

    private (SqliteEvidence Source, SqliteEvidence Destination) BackupDatabase(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        using var snapshot = CortexQuiescentDatabaseSnapshot.Capture(sourcePath, cancellationToken, _privateScratchBase);
        var evidence = InspectDatabase(snapshot.WorkingPath);
        var output = Path.Combine(Path.GetDirectoryName(snapshot.WorkingPath)!, "output.db");
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshot.WorkingPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = output,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        using var source = new SqliteConnection(sourceBuilder.ConnectionString);
        using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        destination.Close();
        source.Close();
        var outputEvidence = InspectDatabase(output);
        cancellationToken.ThrowIfCancellationRequested();
        // Publish only closed ordinary file bytes. SQLite never opens even the
        // temporary name under the original/service destination directory.
        using (var input = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var published = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(published);
            published.Flush(flushToDisk: true);
        }
        return (evidence, outputEvidence);
    }

    private SqliteEvidence InspectCapturedDatabase(string path, CancellationToken cancellationToken)
    {
        using var snapshot = CortexQuiescentDatabaseSnapshot.Capture(path, cancellationToken, _privateScratchBase);
        return InspectDatabase(snapshot.WorkingPath);
    }

    private static SqliteEvidence InspectDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check(1);";
            if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("A SQLite migration source or destination failed quick_check.");
        }
        int userVersion;
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        int tableCount;
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            tableCount = Convert.ToInt32(tables.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return new SqliteEvidence(userVersion, tableCount);
    }

    private static async ValueTask CopyExactAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        var sourceHash = await HashAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var destinationHash = await HashAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            throw new InvalidDataException("An exact state-file migration failed its digest check.");
    }

    private static async ValueTask<byte[]> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static void RejectExistingReparseComponents(string path)
    {
        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical) ??
            throw new InvalidDataException("A migration path has no local filesystem root.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, canonical).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A Cortex state migration path crosses a reparse point.");
        }
    }

    private readonly record struct SqliteEvidence(int UserVersion, int TableCount);
}
