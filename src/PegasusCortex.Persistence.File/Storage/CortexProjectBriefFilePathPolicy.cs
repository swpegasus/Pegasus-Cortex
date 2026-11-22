using System.Text;
using Pegasus.Cortex.Persistence.File.Configuration;

namespace Pegasus.Cortex.Persistence.File.Storage;

internal static class CortexProjectBriefFilePathPolicy
{
    private const int MaximumWindowsPathCharacters = 240;
    private const int MaximumSegmentUtf8Bytes = 255;
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly HashSet<string> WindowsReservedNames =
        new(
        [
            "CON", "PRN", "AUX", "NUL",
            .. Enumerable.Range(1, 9).Select(value => $"COM{value}"),
            .. Enumerable.Range(1, 9).Select(value => $"LPT{value}")
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string CaptureRegisteredRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath) ||
            HasTraversalSegment(rootPath))
        {
            throw new ArgumentException(
                "The registered project root must be an absolute path without traversal.",
                nameof(rootPath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The registered project root is invalid.", nameof(rootPath), exception);
        }
        if (OperatingSystem.IsWindows() &&
            fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A local project-brief root cannot be a UNC or network path.",
                nameof(rootPath));
        }
        var filesystemRoot = Path.GetPathRoot(fullPath) ??
            throw new ArgumentException(
                "The registered project root has no filesystem root.",
                nameof(rootPath));
        if (PathEquals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(filesystemRoot)))
        {
            throw new ArgumentException(
                "A filesystem root cannot be registered as a project root.",
                nameof(rootPath));
        }
        if (OperatingSystem.IsWindows())
        {
            DriveType driveType;
            try
            {
                driveType = new DriveInfo(filesystemRoot).DriveType;
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException(
                    "The registered project root drive could not be verified.",
                    nameof(rootPath),
                    exception);
            }
            if (driveType != DriveType.Fixed)
            {
                throw new ArgumentException(
                    "A local project-brief root must be on a fixed local drive.",
                    nameof(rootPath));
            }
        }
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The registered project root does not exist.");

        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        ValidatePortableSegments(fullPath);
        var snapshotPath = ResolveSnapshotPath(fullPath);
        ValidatePathBudget(snapshotPath);
        RejectExistingReparseAncestry(fullPath);
        return fullPath;
    }

    public static string ResolveSnapshotPath(string registeredRoot)
    {
        var combined = Path.GetFullPath(
            Path.Combine(
                registeredRoot,
                CortexProjectBriefFileRegistration.SnapshotRelativePath
                    .Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(registeredRoot, combined))
            throw new InvalidDataException("The project-brief path escaped its registered root.");
        ValidatePortableSegments(combined);
        ValidatePathBudget(combined);
        return combined;
    }

    public static string PrepareWriteTarget(string registeredRoot)
    {
        ValidateExistingRoot(registeredRoot);
        var cortexDirectory = Path.Combine(registeredRoot, ".cortex");
        EnsureDirectDirectory(cortexDirectory);
        var generatedDirectory = Path.Combine(cortexDirectory, "generated");
        EnsureDirectDirectory(generatedDirectory);
        var snapshotPath = ResolveSnapshotPath(registeredRoot);
        RejectExistingReparseAncestry(snapshotPath);
        if (Directory.Exists(snapshotPath))
            throw new InvalidDataException("The project-brief target is a directory.");
        return snapshotPath;
    }

    public static string PrepareTemporaryWriteTarget(
        string registeredRoot,
        string randomToken)
    {
        if (randomToken.Length != 8 ||
            randomToken.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A project-brief temporary token must be eight hexadecimal characters.",
                nameof(randomToken));
        }

        var snapshotPath = PrepareWriteTarget(registeredRoot);
        var temporaryPath = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(snapshotPath)!,
                $".pb-{randomToken.ToLowerInvariant()}.tmp"));
        if (!IsContained(registeredRoot, temporaryPath))
            throw new InvalidDataException("The temporary project-brief path escaped its root.");
        ValidatePortableSegments(temporaryPath);
        ValidatePathBudget(temporaryPath);
        RejectExistingReparseAncestry(temporaryPath);
        if (Directory.Exists(temporaryPath))
            throw new InvalidDataException("A project-brief temporary target is a directory.");
        return temporaryPath;
    }

    public static string PrepareWriteLockTarget(string registeredRoot)
    {
        var snapshotPath = PrepareWriteTarget(registeredRoot);
        var lockPath = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(snapshotPath)!,
                ".project-brief.lock"));
        if (!IsContained(registeredRoot, lockPath))
            throw new InvalidDataException("The project-brief lock path escaped its root.");
        ValidatePortableSegments(lockPath);
        ValidatePathBudget(lockPath);
        RejectExistingReparseAncestry(lockPath);
        if (Directory.Exists(lockPath))
            throw new InvalidDataException("The project-brief lock target is a directory.");
        return lockPath;
    }

    public static string ValidateReadTarget(string registeredRoot)
    {
        ValidateExistingRoot(registeredRoot);
        var snapshotPath = ResolveSnapshotPath(registeredRoot);
        RejectExistingReparseAncestry(snapshotPath);
        if (Directory.Exists(snapshotPath))
            throw new InvalidDataException("The project-brief target is a directory.");
        return snapshotPath;
    }

    private static void ValidateExistingRoot(string registeredRoot)
    {
        if (!Directory.Exists(registeredRoot))
            throw new DirectoryNotFoundException("The registered project root is unavailable.");
        RejectExistingReparseAncestry(registeredRoot);
    }

    private static void EnsureDirectDirectory(string path)
    {
        if (System.IO.File.Exists(path))
            throw new InvalidDataException("A project-brief directory boundary is a file.");
        if (Directory.Exists(path))
        {
            RejectExistingReparseAncestry(path);
            return;
        }
        Directory.CreateDirectory(path);
        RejectExistingReparseAncestry(path);
    }

    private static void RejectExistingReparseAncestry(string path)
    {
        var pathRoot = Path.GetPathRoot(path) ??
            throw new InvalidDataException("The project-brief path has no filesystem root.");
        var current = pathRoot;
        var relative = path[pathRoot.Length..];
        foreach (var segment in relative.Split(
            Separators,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !System.IO.File.Exists(current))
                continue;
            FileAttributes attributes;
            try
            {
                attributes = System.IO.File.GetAttributes(current);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "A project-brief path boundary could not be inspected.",
                    exception);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "A project-brief path boundary is a link or reparse point.");
            }
        }
    }

    private static void ValidatePortableSegments(string fullPath)
    {
        var pathRoot = Path.GetPathRoot(fullPath) ??
            throw new ArgumentException("The project-brief path has no filesystem root.");
        var segments = fullPath[pathRoot.Length..].Split(
            Separators,
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var stem = segment.Split('.')[0];
            if (segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.Contains(':', StringComparison.Ordinal) ||
                Encoding.UTF8.GetByteCount(segment) > MaximumSegmentUtf8Bytes ||
                WindowsReservedNames.Contains(stem))
            {
                throw new ArgumentException(
                    "A project-brief path segment is not portable or safe.");
            }
        }
    }

    private static void ValidatePathBudget(string fullPath)
    {
        if (fullPath.Length > MaximumWindowsPathCharacters)
        {
            throw new PathTooLongException(
                "The generated project-brief path exceeds 240 UTF-16 characters.");
        }
    }

    private static bool HasTraversalSegment(string path)
    {
        var pathRoot = Path.GetPathRoot(path) ?? string.Empty;
        return path[pathRoot.Length..]
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static bool IsContained(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
