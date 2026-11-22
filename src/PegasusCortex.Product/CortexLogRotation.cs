namespace Pegasus.Cortex.Product;

/// <summary>Two-slot diagnostic rotation. Callers retain their existing root admission and writer lock.</summary>
public static class CortexLogRotation
{
    public static bool ShouldRotate(long length, DateTime lastWriteUtc, long maximumBytes, DateTimeOffset now)
    {
        if (length < 0 || maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        return length > maximumBytes || length > 0 && lastWriteUtc.Date < now.UtcDateTime.Date;
    }

    public static bool RotateIfNeeded(string path, string previous, long maximumBytes, DateTimeOffset now)
    {
        var currentPath = Path.GetFullPath(path);
        var previousPath = Path.GetFullPath(previous);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(previous) ||
            string.Equals(currentPath, previousPath, comparison) ||
            !string.Equals(Path.GetDirectoryName(currentPath), Path.GetDirectoryName(previousPath), comparison))
            throw new ArgumentException("Diagnostic slots must be distinct absolute files in the same admitted directory.");
        var current = OrdinaryFile(currentPath);
        if (current is null || !ShouldRotate(current.Length, current.LastWriteTimeUtc, maximumBytes, now)) return false;
        _ = OrdinaryFile(previousPath);
        File.Move(currentPath, previousPath, overwrite: true);
        return true;
    }

    private static FileInfo? OrdinaryFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A diagnostic slot is not an ordinary file.");
        return new FileInfo(path);
    }
}
