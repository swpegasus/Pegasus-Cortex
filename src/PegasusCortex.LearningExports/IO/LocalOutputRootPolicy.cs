namespace Pegasus.Cortex.LearningExports.IO;

internal static class LocalOutputRootPolicy
{
    public static bool TryAuthorize(
        string? requestedRoot,
        string? approvedRoot,
        out string canonicalRoot,
        out string reason)
    {
        canonicalRoot = string.Empty;
        if (!TryCanonicalize(requestedRoot, out var requested, out reason) ||
            !TryCanonicalize(approvedRoot, out var approved, out reason))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(requestedRoot, requested, comparison) ||
            !string.Equals(approvedRoot, approved, comparison) ||
            !string.Equals(requested, approved, comparison))
        {
            reason = "The requested and authority-approved output roots must be the same canonical path.";
            return false;
        }

        canonicalRoot = requested;
        reason = string.Empty;
        return true;
    }

    private static bool TryCanonicalize(string? value, out string canonical, out string reason)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(value))
        {
            reason = "The output root must be a control-free fully-qualified local path.";
            return false;
        }

        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var pathRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical) ?? string.Empty);
            if (string.Equals(canonical, pathRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                reason = "Publishing directly to a filesystem root is not allowed.";
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                var driveType = new DriveInfo(canonical).DriveType;
                if (driveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    reason = "The output root must resolve to fixed, removable, or RAM-backed local storage.";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            reason = "The output root could not be canonicalized as local storage.";
            return false;
        }
    }
}
