namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexV2StoreOptions(
    string DatabasePath,
    int BusyTimeoutMilliseconds = 5_000,
    int MaximumContentBytes = 2 * 1024 * 1024,
    int MaximumSearchChunksPerHead = 1_024,
    int MaximumDatabasePages = 1_048_576,
    bool EnablePayloadOffload = false)
{
    public const string RequiredDatabaseFileName = "cortex-v2.db";

    public SqliteCortexV2StoreOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) ||
            !Path.IsPathFullyQualified(DatabasePath) ||
            DatabasePath.StartsWith("\\\\", StringComparison.Ordinal) ||
            DatabasePath.StartsWith("//", StringComparison.Ordinal) ||
            DatabasePath.Any(char.IsControl))
        {
            throw new ArgumentException("Cortex v2 requires an explicit fully qualified local database path.", nameof(DatabasePath));
        }

        var canonical = Path.GetFullPath(DatabasePath);
        if (!string.Equals(canonical, DatabasePath, PathComparison) ||
            !string.Equals(Path.GetFileName(canonical), RequiredDatabaseFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cortex v2 requires a canonical path ending in '{RequiredDatabaseFileName}'.",
                nameof(DatabasePath));
        }

        var parent = Path.GetDirectoryName(canonical);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(Path.TrimEndingDirectorySeparator(parent), root, PathComparison))
        {
            throw new ArgumentException("Cortex v2 cannot be stored at a filesystem root.", nameof(DatabasePath));
        }

        if (OperatingSystem.IsWindows() && canonical.Length > 240)
            throw new ArgumentException("The Cortex v2 database path exceeds the Windows path budget.", nameof(DatabasePath));
        if (EnablePayloadOffload && Path.Combine(parent, "cold-v1", "ff", new string('f', 64) + ".br").Length > 240)
            throw new ArgumentException("The fixed cold payload path exceeds the product path budget.", nameof(DatabasePath));
        if (BusyTimeoutMilliseconds is < 100 or > 30_000)
            throw new ArgumentOutOfRangeException(nameof(BusyTimeoutMilliseconds));
        if (MaximumContentBytes is < 1_024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumContentBytes));
        if (MaximumSearchChunksPerHead is < 1 or > 8_192)
            throw new ArgumentOutOfRangeException(nameof(MaximumSearchChunksPerHead));
        if (MaximumDatabasePages is < 256 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(MaximumDatabasePages));

        EnsureAncestorsAreOrdinary(parent);
        EnsureEntryIsOrdinary(canonical);
        EnsureEntryIsOrdinary(canonical + "-wal");
        EnsureEntryIsOrdinary(canonical + "-shm");
        return this with { DatabasePath = canonical };
    }

    private static void EnsureAncestorsAreOrdinary(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cortex v2 storage cannot cross a reparse point.");
        }
    }

    private static void EnsureEntryIsOrdinary(string path)
    {
        if (Directory.Exists(path))
            throw new IOException("A Cortex v2 database entry identifies a directory.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cortex v2 database files cannot be links or reparse points.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

