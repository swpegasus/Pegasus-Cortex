namespace Pegasus.Cortex.OfflineJournal.Paths;

internal sealed class JournalPathPolicy
{
    public const int WindowsFinalPathLimit = 240;
    private const int MaximumSegmentCharacters = 128;

    private static readonly HashSet<string> WindowsReservedBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    private JournalPathPolicy(string root) => Root = root;

    public string Root { get; }

    /// <summary>
    /// Creates only the lexical path formatter used after a trusted filesystem boundary has
    /// already authorized and anchored the root. This method does not grant filesystem authority.
    /// </summary>
    internal static JournalPathPolicy FromBoundaryRoot(string root)
    {
        if (!TryCanonicalize(root, out var canonical, out var reason) ||
            !string.Equals(root, canonical, PathComparison) ||
            OperatingSystem.IsWindows() && canonical.Length > WindowsFinalPathLimit)
        {
            throw new ArgumentException(reason.Length == 0
                ? "The boundary root must already be canonical."
                : reason, nameof(root));
        }
        return new JournalPathPolicy(canonical);
    }

    // Lexical/reparse preflight for the non-production test boundary only.
    internal static bool TryValidateLexicalTestRoot(
        string? requestedRoot,
        string? approvedRoot,
        out JournalPathPolicy? policy,
        out string reason)
    {
        policy = null;
        if (!TryCanonicalize(requestedRoot, out var requested, out reason) ||
            !TryCanonicalize(approvedRoot, out var approved, out reason))
        {
            return false;
        }

        var comparison = PathComparison;
        if (!string.Equals(requestedRoot, requested, comparison) ||
            !string.Equals(approvedRoot, approved, comparison) ||
            !string.Equals(requested, approved, comparison))
        {
            reason = "The requested and authority-approved journal roots must be the same canonical path.";
            return false;
        }

        if (OperatingSystem.IsWindows() && requested.Length > WindowsFinalPathLimit)
        {
            reason = "The approved journal root exceeds the Windows path budget.";
            return false;
        }

        try
        {
            EnsureExistingAncestorsAreNotReparsePoints(requested);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "The approved journal root crosses a reparse point or cannot be inspected safely.";
            return false;
        }

        policy = new JournalPathPolicy(requested);
        reason = string.Empty;
        return true;
    }

    public string Resolve(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
            return Root;

        var path = Root;
        foreach (var segment in segments)
        {
            if (!IsPortableSegment(segment))
                throw new ArgumentException("A journal path segment is not portable or bounded.", nameof(segments));
            path = Path.Combine(path, segment);
        }

        path = Path.GetFullPath(path);
        var prefix = Root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison) ||
            OperatingSystem.IsWindows() && path.Length > WindowsFinalPathLimit)
        {
            throw new IOException("The generated journal path escapes its approved root or exceeds the path budget.");
        }
        return path;
    }

    public void EnsureExistingPathIsSafe(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (!string.Equals(canonical, Root, PathComparison) &&
            !canonical.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new IOException("The path is outside the approved journal root.");
        }
        EnsureExistingAncestorsAreNotReparsePoints(canonical);
    }

    public static bool IsPortableIdentifier(string? value, int maximumCharacters) =>
        value is { Length: > 0 } &&
        value.Length <= Math.Min(maximumCharacters, MaximumSegmentCharacters) &&
        IsPortableSegment(value);

    private static bool TryCanonicalize(string? value, out string canonical, out string reason)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(value))
        {
            reason = "The journal root must be a control-free, fully-qualified local path.";
            return false;
        }

        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical) ?? string.Empty);
            if (string.Equals(canonical, filesystemRoot, PathComparison))
            {
                reason = "A filesystem root cannot be used as the journal root.";
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                var driveType = new DriveInfo(canonical).DriveType;
                if (driveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    reason = "The journal root must use fixed, removable, or RAM-backed local storage.";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            reason = "The journal root could not be canonicalized as local storage.";
            return false;
        }
    }

    private static bool IsPortableSegment(string? value)
    {
        if (value is not { Length: > 0 } || value.Length > MaximumSegmentCharacters ||
            value.Any(char.IsControl) || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value[0] is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')) ||
            value.Any(character => character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '.' or '_' or '-')))
        {
            return false;
        }

        return !WindowsReservedBasenames.Contains(value.Split('.', 2)[0]);
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (!current.Exists)
                continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Journal paths cannot cross reparse points.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
