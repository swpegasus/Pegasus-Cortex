using System.Text;
using Pegasus.Cortex.ArchiveTool.Archive;

namespace Pegasus.Cortex.ArchiveTool.Security;

internal static class ArchivePathPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> WindowsReservedBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$", "conin$", "conout$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        "com¹", "com²", "com³", "lpt¹", "lpt²", "lpt³"
    };

    internal static StringComparison FilesystemComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static StringComparer FilesystemComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static ValidatedRequest ValidateRequest(
        ArchiveExtractionRequest request,
        ArchiveSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLimits(limits);

        var archivePath = CanonicalizeAbsoluteLocalPath(request.ArchivePath, "archive_path_invalid");
        var destinationRoot = CanonicalizeAbsoluteLocalPath(request.DestinationRoot, "destination_unsafe");
        var inventoryPath = CanonicalizeAbsoluteLocalPath(request.InventoryPath, "inventory_path_invalid");

        if (!File.Exists(archivePath))
            throw Failure("archive_path_invalid", "The archive must be an existing regular file.");
        if (!Directory.Exists(destinationRoot))
            throw Failure("destination_unsafe", "The destination root must be an existing directory.");

        EnsureRegularFileWithoutReparsePoints(archivePath, "archive_path_invalid");
        EnsureExistingAncestorsAreNotReparsePoints(destinationRoot, "destination_unsafe");

        var destinationName = ValidateDestinationName(request.DestinationName, limits);
        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, destinationName));
        EnsureDirectChild(destinationRoot, destinationPath, "destination_unsafe");
        EnsureWithinPathBudget(destinationPath, limits, "destination_unsafe");

        var inventoryParent = Path.GetDirectoryName(inventoryPath);
        if (string.IsNullOrEmpty(inventoryParent) || !Directory.Exists(inventoryParent))
            throw Failure("inventory_path_invalid", "The inventory parent must be an existing directory.");
        EnsureExistingAncestorsAreNotReparsePoints(inventoryParent, "inventory_path_invalid");
        EnsureWithinPathBudget(inventoryPath, limits, "inventory_path_invalid");

        if (string.Equals(archivePath, inventoryPath, FilesystemComparison) ||
            string.Equals(destinationPath, inventoryPath, FilesystemComparison))
        {
            throw Failure("inventory_path_invalid", "The inventory path must be distinct from the archive and destination.");
        }

        if (PathExistsNoFollow(destinationPath))
            throw Failure("destination_exists", "The destination already exists.");
        if (PathExistsNoFollow(inventoryPath))
            throw Failure("inventory_exists", "The inventory output already exists.");

        var expectedHash = ParseSha256(request.ExpectedSha256);
        return new ValidatedRequest(
            archivePath,
            expectedHash,
            destinationRoot,
            destinationName,
            destinationPath,
            inventoryPath,
            limits);
    }

    internal static ValidatedEntryPath ValidateArchiveEntryPath(
        string rawName,
        bool isDirectory,
        string destinationPath,
        ArchiveSafetyLimits limits,
        int entryIndex)
    {
        if (string.IsNullOrEmpty(rawName) || !HasWellFormedUnicode(rawName) || rawName.Any(char.IsControl))
            throw EntryFailure("entry_path_invalid", "An archive entry path is empty or contains invalid Unicode or control characters.", entryIndex);
        if (!string.Equals(rawName, rawName.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
            throw EntryFailure("entry_path_invalid", "An archive entry path is not Unicode NFC.", entryIndex);
        if (rawName.Contains('\\') || rawName.Contains(':') || rawName.StartsWith("/", StringComparison.Ordinal) ||
            rawName.StartsWith("//", StringComparison.Ordinal) || LooksDriveQualified(rawName))
        {
            throw EntryFailure("entry_path_invalid", "An archive entry path is absolute, drive-qualified, or uses an alternate stream separator.", entryIndex);
        }

        var name = rawName;
        if (name is "." or "./")
        {
            if (!isDirectory)
                throw EntryFailure("entry_path_invalid", "Only a directory entry may represent the archive root.", entryIndex);
            return new ValidatedEntryPath(string.Empty, true);
        }

        if (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
            if (name.StartsWith("./", StringComparison.Ordinal))
                throw EntryFailure("entry_path_invalid", "An archive entry has a non-canonical repeated root prefix.", entryIndex);
        }

        if (name.EndsWith("/", StringComparison.Ordinal))
        {
            if (!isDirectory || name.EndsWith("//", StringComparison.Ordinal))
                throw EntryFailure("entry_path_invalid", "An archive entry has a non-canonical trailing separator.", entryIndex);
            name = name[..^1];
        }

        var segments = name.Split('/');
        if (segments.Length is 0 || segments.Length > limits.MaximumSegments ||
            segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw EntryFailure("entry_path_invalid", "An archive entry contains traversal or non-canonical path segments.", entryIndex);
        }

        var relativeUtf8 = GetUtf8ByteCount(name, entryIndex);
        if (relativeUtf8 > limits.MaximumRelativePathUtf8)
            throw EntryFailure("entry_path_over_budget", "An archive entry exceeds the relative path byte budget.", entryIndex);

        foreach (var segment in segments)
            ValidatePortableSegment(segment, limits.MaximumSegmentUtf8, entryIndex);

        var candidate = Path.GetFullPath(Path.Combine(destinationPath, Path.Combine(segments)));
        EnsureContained(destinationPath, candidate, entryIndex);
        if (candidate.Length > limits.MaximumCanonicalPathUtf16)
            throw EntryFailure("entry_path_over_budget", "An archive entry exceeds the canonical path budget.", entryIndex);

        return new ValidatedEntryPath(string.Join('/', segments), false);
    }

    internal static string ResolveEntryPath(string extractionRoot, string relativePath)
    {
        var segments = relativePath.Split('/');
        var candidate = Path.GetFullPath(Path.Combine(extractionRoot, Path.Combine(segments)));
        EnsureContained(extractionRoot, candidate, null);
        return candidate;
    }

    internal static void EnsureExistingAncestorsAreNotReparsePoints(string path, string failureCode)
    {
        try
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            {
                if (!current.Exists)
                    continue;
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                    throw Failure(failureCode, "A filesystem path crosses a reparse point.");
            }
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure(failureCode, "A filesystem path could not be inspected safely.", exception);
        }
    }

    internal static void EnsurePathIsNotReparsePoint(string path, string failureCode)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Failure(failureCode, "A filesystem path is a reparse point.");
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure(failureCode, "A filesystem path could not be inspected safely.", exception);
        }
    }

    internal static bool PathExistsNoFollow(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure("path_inspection_failed", "A filesystem path could not be inspected without following it.", exception);
        }
    }

    private static void ValidateLimits(ArchiveSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumCompressedBytes is <= 0 or > ArchiveSafetyLimits.HardMaximumCompressedBytes ||
            limits.MaximumTarStreamBytes is <= 0 or > ArchiveSafetyLimits.HardMaximumTarStreamBytes ||
            limits.MaximumFileBytes is <= 0 or > ArchiveSafetyLimits.HardMaximumFileBytes ||
            limits.MaximumTotalFileBytes is <= 0 or > ArchiveSafetyLimits.HardMaximumTotalFileBytes ||
            limits.MaximumEntries is <= 0 or > ArchiveSafetyLimits.HardMaximumEntries ||
            !double.IsFinite(limits.MaximumExpansionRatio) || limits.MaximumExpansionRatio <= 0 ||
            limits.MaximumExpansionRatio > ArchiveSafetyLimits.HardMaximumExpansionRatio ||
            limits.MaximumCanonicalPathUtf16 is <= 0 or > ArchiveSafetyLimits.HardMaximumCanonicalPathUtf16 ||
            limits.MaximumRelativePathUtf8 is <= 0 or > ArchiveSafetyLimits.HardMaximumRelativePathUtf8 ||
            limits.MaximumSegmentUtf8 is <= 0 or > ArchiveSafetyLimits.HardMaximumSegmentUtf8 ||
            limits.MaximumSegments is <= 0 or > ArchiveSafetyLimits.HardMaximumSegments)
        {
            throw Failure("limits_invalid", "Archive safety limits must be positive and may only tighten policy ceilings.");
        }
    }

    private static string CanonicalizeAbsoluteLocalPath(string? value, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !HasWellFormedUnicode(value) ||
            !Path.IsPathFullyQualified(value) || value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw Failure(failureCode, "A path must be a control-free, fully-qualified local path.");
        }

        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (string.IsNullOrEmpty(canonical))
                throw Failure(failureCode, "A path could not be canonicalized.");
            var volumeRoot = Path.GetPathRoot(canonical) ?? string.Empty;
            if (canonical[volumeRoot.Length..].Contains(':'))
                throw Failure(failureCode, "A path cannot address an alternate data stream.");
            if (OperatingSystem.IsWindows())
            {
                var driveType = new DriveInfo(canonical).DriveType;
                if (driveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                    throw Failure(failureCode, "A path must use local writable storage.");
            }
            return canonical;
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw Failure(failureCode, "A path could not be canonicalized as local storage.", exception);
        }
    }

    private static string ValidateDestinationName(string? value, ArchiveSafetyLimits limits)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('/') || value.Contains('\\'))
            throw Failure("destination_unsafe", "The destination name must be one portable path segment.");
        ValidatePortableSegment(value, limits.MaximumSegmentUtf8, null);
        return value;
    }

    private static void ValidatePortableSegment(string segment, int maximumUtf8Bytes, int? entryIndex)
    {
        if (!HasWellFormedUnicode(segment) || segment.Any(char.IsControl) ||
            !string.Equals(segment, segment.Normalize(NormalizationForm.FormC), StringComparison.Ordinal) ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(['<', '>', ':', '"', '\\', '|', '?', '*']) >= 0 ||
            GetUtf8ByteCount(segment, entryIndex) > maximumUtf8Bytes ||
            WindowsReservedBasenames.Contains(segment.Split('.', 2)[0]))
        {
            if (entryIndex.HasValue)
                throw EntryFailure("entry_path_invalid", "An archive entry contains a non-portable path segment.", entryIndex.Value);
            throw Failure("destination_unsafe", "The destination name is not a portable path segment.");
        }
    }

    private static void EnsureRegularFileWithoutReparsePoints(string path, string failureCode)
    {
        try
        {
            var file = new FileInfo(path);
            var attributes = file.Attributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                file.LinkTarget is not null)
            {
                throw Failure(failureCode, "The archive must be a regular, non-reparse file.");
            }
            EnsureExistingAncestorsAreNotReparsePoints(file.DirectoryName ?? path, failureCode);
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure(failureCode, "The archive path could not be inspected safely.", exception);
        }
    }

    private static byte[] ParseSha256(string? value)
    {
        if (value is null || value.Length != 64)
            throw Failure("hash_invalid", "The expected SHA-256 must contain exactly 64 hexadecimal characters.");
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw Failure("hash_invalid", "The expected SHA-256 must contain exactly 64 hexadecimal characters.", exception);
        }
    }

    private static void EnsureDirectChild(string root, string candidate, string failureCode)
    {
        if (!string.Equals(Path.GetDirectoryName(candidate), root, FilesystemComparison))
            throw Failure(failureCode, "The destination must be a direct child of its trusted root.");
    }

    private static void EnsureContained(string root, string candidate, int? entryIndex)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (candidate.StartsWith(prefix, FilesystemComparison))
            return;
        if (entryIndex.HasValue)
            throw EntryFailure("entry_path_invalid", "An archive entry escapes the destination root.", entryIndex.Value);
        throw Failure("destination_unsafe", "A generated path escapes the destination root.");
    }

    private static void EnsureWithinPathBudget(
        string path,
        ArchiveSafetyLimits limits,
        string failureCode)
    {
        if (path.Length > limits.MaximumCanonicalPathUtf16)
            throw Failure(failureCode, "The destination exceeds the canonical path budget.");
    }

    private static int GetUtf8ByteCount(string value, int? entryIndex)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            if (entryIndex.HasValue)
                throw EntryFailure("entry_path_invalid", "An archive entry contains invalid Unicode.", entryIndex.Value, exception);
            throw Failure("destination_unsafe", "A path contains invalid Unicode.", exception);
        }
    }

    private static bool HasWellFormedUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool LooksDriveQualified(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static ArchiveBoundaryException Failure(
        string code,
        string message,
        Exception? innerException = null) => new(code, message, innerException);

    private static ArchiveBoundaryException EntryFailure(
        string code,
        string message,
        int entryIndex,
        Exception? innerException = null) => new(code, message, innerException, entryIndex);
}

internal sealed record ValidatedRequest(
    string ArchivePath,
    byte[] ExpectedSha256,
    string DestinationRoot,
    string DestinationName,
    string DestinationPath,
    string InventoryPath,
    ArchiveSafetyLimits Limits);

internal sealed record ValidatedEntryPath(string RelativePath, bool IsArchiveRoot);
