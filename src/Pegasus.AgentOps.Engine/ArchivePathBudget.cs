using System.Text;

namespace Pegasus.AgentOps.Engine;

/// <summary>Shared path-budget admission for archive-generated Windows paths.</summary>
internal static class ArchivePathBudget
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumPathCharacters = 240;
    internal const int MaximumSegmentUtf8Bytes = 255;

    internal static void RequireSegment(string segment)
    {
        try
        {
            if (StrictUtf8.GetByteCount(segment) > MaximumSegmentUtf8Bytes)
                throw new AgentOpsException("archive_name");
        }
        catch (EncoderFallbackException exception)
        {
            throw new AgentOpsException("archive_name", inner: exception);
        }
    }

    internal static string RequireAbsolute(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > MaximumPathCharacters)
            throw new AgentOpsException("archive_name");
        foreach (var segment in full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
            RequireSegment(segment);
        return full;
    }
}
