namespace Pegasus.Cortex.Persistence.Sqlite.Configuration;

/// <summary>
/// Validated local-store configuration. The standalone profile uses an explicit
/// path and never discovers a database or remote endpoint from environment variables.
/// Document and fixed-page quotas bound long-lived local resource growth.
/// </summary>
public sealed record SqliteCortexStoreOptions(
    string DatabasePath,
    int BusyTimeoutMilliseconds = 5000,
    int MaximumRetainedDocuments = 100_000,
    int MaximumProjectDocuments = 10_000,
    int MaximumDatabasePages = 262_144)
{
    public const int MinimumBusyTimeoutMilliseconds = 100;
    public const int MaximumBusyTimeoutMilliseconds = 30_000;
    public const int MaximumConfiguredDocuments = 1_000_000;
    public const int MinimumDatabasePages = 32;
    public const int MaximumConfiguredDatabasePages = 16_777_216;

    public SqliteCortexStoreOptions Validate()
    {
        if (BusyTimeoutMilliseconds is < MinimumBusyTimeoutMilliseconds or > MaximumBusyTimeoutMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(BusyTimeoutMilliseconds));
        if (MaximumRetainedDocuments is < 1 or > MaximumConfiguredDocuments)
            throw new ArgumentOutOfRangeException(nameof(MaximumRetainedDocuments));
        if (MaximumProjectDocuments is < 1 ||
            MaximumProjectDocuments > MaximumRetainedDocuments)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumProjectDocuments));
        }
        if (MaximumDatabasePages is < MinimumDatabasePages or > MaximumConfiguredDatabasePages)
            throw new ArgumentOutOfRangeException(nameof(MaximumDatabasePages));
        return this with
        {
            DatabasePath = SqliteLocalPathPolicy.ValidateDatabasePath(DatabasePath)
        };
    }

    public static SqliteCortexStoreOptions ForCurrentUser(string profileId = "default")
    {
        if (!SqliteLocalPathPolicy.IsPortableIdentifier(profileId))
            throw new ArgumentException("A bounded portable profile identifier is required.", nameof(profileId));
        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new InvalidOperationException("The local application-data directory is unavailable.");
        return new SqliteCortexStoreOptions(
            Path.Combine(localRoot, "PegasusCortex", profileId, "cortex.db")).Validate();
    }
}

internal static class SqliteLocalPathPolicy
{
    private const int WindowsFinalPathLimit = 240;
    private const int MaximumIdentifierCharacters = 64;
    private static readonly HashSet<string> WindowsReservedBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    public static string ValidateDatabasePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The SQLite database requires a fully qualified local path.", nameof(value));
        }

        var canonical = Path.GetFullPath(value);
        if (!string.Equals(canonical, value, PathComparison) ||
            !string.Equals(Path.GetFileName(canonical), "cortex.db", StringComparison.Ordinal) ||
            OperatingSystem.IsWindows() && canonical.Length > WindowsFinalPathLimit)
        {
            throw new ArgumentException("The SQLite database path is not canonical or exceeds its path budget.", nameof(value));
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical) ?? string.Empty);
        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(Path.TrimEndingDirectorySeparator(parent), root, PathComparison))
        {
            throw new ArgumentException("The SQLite database cannot be placed at a filesystem root.", nameof(value));
        }
        if (OperatingSystem.IsWindows())
            RequireLocalWindowsDrive(canonical, nameof(value));
        EnsureExistingAncestorsAreNotReparsePoints(parent);
        EnsureDatabaseEntryIsSafe(canonical);
        return canonical;
    }

    public static string ValidateNewBackupPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A fully qualified local backup path is required.", nameof(value));
        }
        var canonical = Path.GetFullPath(value);
        if (!string.Equals(canonical, value, PathComparison) ||
            !string.Equals(Path.GetExtension(canonical), ".db", StringComparison.OrdinalIgnoreCase) ||
            OperatingSystem.IsWindows() && canonical.Length > WindowsFinalPathLimit ||
            File.Exists(canonical))
        {
            throw new ArgumentException("The backup path must be a new canonical .db file.", nameof(value));
        }
        var parent = Path.GetDirectoryName(canonical) ??
            throw new ArgumentException("The backup path has no parent.", nameof(value));
        RequireNonRootParent(canonical, parent, nameof(value));
        if (OperatingSystem.IsWindows())
            RequireLocalWindowsDrive(canonical, nameof(value));
        EnsureExistingAncestorsAreNotReparsePoints(parent);
        EnsureFileEntryIsSafe(canonical, "The Cortex backup");
        EnsureSidecarsAbsent(canonical, "The Cortex backup");
        EnsureFileEntryIsSafe(canonical + ".partial", "The Cortex backup staging file");
        EnsureSidecarsAbsent(canonical + ".partial", "The Cortex backup staging file");
        return canonical;
    }

    public static string ValidateExistingBackupPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A fully qualified local backup path is required.", nameof(value));
        }
        var canonical = Path.GetFullPath(value);
        if (!string.Equals(canonical, value, PathComparison) ||
            !string.Equals(Path.GetExtension(canonical), ".db", StringComparison.OrdinalIgnoreCase) ||
            OperatingSystem.IsWindows() && canonical.Length > WindowsFinalPathLimit ||
            !File.Exists(canonical))
        {
            throw new ArgumentException("The backup path must identify an existing canonical .db file.", nameof(value));
        }
        var info = new FileInfo(canonical);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Cortex backup cannot be a link or reparse point.");
        var parent = info.DirectoryName ??
            throw new ArgumentException("The backup path has no parent.", nameof(value));
        RequireNonRootParent(canonical, parent, nameof(value));
        if (OperatingSystem.IsWindows())
            RequireLocalWindowsDrive(canonical, nameof(value));
        EnsureExistingAncestorsAreNotReparsePoints(parent);
        return canonical;
    }

    public static bool IsPortableIdentifier(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        !value.EndsWith('.') &&
        !value.EndsWith(' ') &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !WindowsReservedBasenames.Contains(value.Split('.', 2)[0]);

    public static void EnsureExistingAncestorsAreNotReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (!current.Exists)
                continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local Cortex storage cannot cross a reparse point.");
        }
    }

    public static void EnsureDatabaseEntryIsSafe(string databasePath)
    {
        EnsureFileEntryIsSafe(databasePath, "The Cortex database");
        EnsureFileEntryIsSafe(databasePath + "-wal", "The Cortex database WAL");
        EnsureFileEntryIsSafe(databasePath + "-shm", "The Cortex database shared-memory file");
    }

    public static void EnsureSidecarsAbsent(string databasePath, string description)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = databasePath + suffix;
            EnsureFileEntryIsSafe(sidecar, $"{description} sidecar");
            if (File.Exists(sidecar) || Directory.Exists(sidecar))
                throw new IOException($"{description} cannot use pre-existing SQLite sidecars.");
        }
    }

    public static void EnsureFileEntryIsSafe(string path, string description)
    {
        if (Directory.Exists(path))
            throw new IOException($"{description} path identifies a directory.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{description} cannot be a link or reparse point.");
        }
    }

    private static void RequireNonRootParent(
        string canonical,
        string parent,
        string parameterName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical) ?? string.Empty);
        if (string.Equals(Path.TrimEndingDirectorySeparator(parent), root, PathComparison))
            throw new ArgumentException("Local Cortex files cannot be placed at a filesystem root.", parameterName);
    }

    private static void RequireLocalWindowsDrive(string path, string parameterName)
    {
        var driveType = new DriveInfo(path).DriveType;
        if (driveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
            throw new ArgumentException("Local Cortex files must use local storage.", parameterName);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
