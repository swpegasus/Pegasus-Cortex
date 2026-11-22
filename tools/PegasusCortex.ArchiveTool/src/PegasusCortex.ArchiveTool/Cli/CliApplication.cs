using System.Globalization;
using System.Text.Json;
using Pegasus.Cortex.ArchiveTool.Archive;

namespace Pegasus.Cortex.ArchiveTool.Cli;

internal static class CliApplication
{
    private const string Usage = """
        Usage:
          PegasusCortex.ArchiveTool extract --archive <absolute.tar.gz> --sha256 <64-hex> --destination-root <absolute-existing-root> --destination-name <new-child-name> --inventory <absolute-new.json> [tightening options]

        Tightening options (values cannot exceed built-in policy ceilings):
          --max-compressed-bytes <integer>       Default/hard ceiling: 209715200
          --max-tar-stream-bytes <integer>       Default/hard ceiling: 671088640
          --max-file-bytes <integer>             Default/hard ceiling: 134217728
          --max-total-file-bytes <integer>       Default/hard ceiling: 536870912
          --max-entries <integer>                Default/hard ceiling: 10000
          --max-expansion-ratio <number>         Default/hard ceiling: 100
          --max-canonical-path-utf16 <integer>   Default/hard ceiling: 240
          --max-relative-path-utf8 <integer>     Default/hard ceiling: 4096
          --max-segment-utf8 <integer>           Default/hard ceiling: 255
          --max-segments <integer>               Default/hard ceiling: 64

        Security precondition:
          The destination root and inventory parent must be trusted, local, and exclusively
          writable by the invoking identity for the duration of extraction. The tool rejects
          existing reparse ancestors, but lexical path APIs cannot authorize a hostile parent.
        """;

    internal static int Run(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            standardOutput.WriteLine(Usage);
            return 0;
        }
        if (args.Length == 0 || !string.Equals(args[0], "extract", StringComparison.Ordinal))
        {
            standardError.WriteLine(Usage);
            return 2;
        }

        try
        {
            var options = ParseOptions(args.AsSpan(1));
            var limits = ParseLimits(options);
            var request = new ArchiveExtractionRequest(
                Required(options, "--archive"),
                Required(options, "--sha256"),
                Required(options, "--destination-root"),
                Required(options, "--destination-name"),
                Required(options, "--inventory"),
                limits);
            var result = StructuredArchiveExtractor.Extract(request);
            WriteSuccess(standardOutput, result);
            return 0;
        }
        catch (CliUsageException exception)
        {
            WriteError(standardError, "usage_error", exception.Message, null, null, 0);
            return 2;
        }
        catch (ArchiveBoundaryException exception)
        {
            WriteError(
                standardError,
                exception.Code,
                exception.Message,
                exception.PrimaryCode,
                exception.EntryIndex,
                exception.CleanupFailureCount);
            return 3;
        }
        catch (Exception)
        {
            WriteError(
                standardError,
                "unexpected_failure",
                "The archive tool failed without exposing environment or archive-controlled details.",
                null,
                null,
                0);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw new CliUsageException("Options must be provided as name/value pairs.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--archive", "--sha256", "--destination-root", "--destination-name", "--inventory",
            "--max-compressed-bytes", "--max-tar-stream-bytes", "--max-file-bytes",
            "--max-total-file-bytes", "--max-entries", "--max-expansion-ratio",
            "--max-canonical-path-utf16", "--max-relative-path-utf8", "--max-segment-utf8",
            "--max-segments"
        };
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!allowed.Contains(name))
                throw new CliUsageException("An unknown option was supplied.");
            if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException("An option value is missing.");
            if (!parsed.TryAdd(name, value))
                throw new CliUsageException("An option was supplied more than once.");
        }
        return parsed;
    }

    private static ArchiveSafetyLimits ParseLimits(IReadOnlyDictionary<string, string> values) => new()
    {
        MaximumCompressedBytes = ParseInt64(values, "--max-compressed-bytes", ArchiveSafetyLimits.HardMaximumCompressedBytes),
        MaximumTarStreamBytes = ParseInt64(values, "--max-tar-stream-bytes", ArchiveSafetyLimits.HardMaximumTarStreamBytes),
        MaximumFileBytes = ParseInt64(values, "--max-file-bytes", ArchiveSafetyLimits.HardMaximumFileBytes),
        MaximumTotalFileBytes = ParseInt64(values, "--max-total-file-bytes", ArchiveSafetyLimits.HardMaximumTotalFileBytes),
        MaximumEntries = ParseInt32(values, "--max-entries", ArchiveSafetyLimits.HardMaximumEntries),
        MaximumExpansionRatio = ParseDouble(values, "--max-expansion-ratio", ArchiveSafetyLimits.HardMaximumExpansionRatio),
        MaximumCanonicalPathUtf16 = ParseInt32(values, "--max-canonical-path-utf16", ArchiveSafetyLimits.HardMaximumCanonicalPathUtf16),
        MaximumRelativePathUtf8 = ParseInt32(values, "--max-relative-path-utf8", ArchiveSafetyLimits.HardMaximumRelativePathUtf8),
        MaximumSegmentUtf8 = ParseInt32(values, "--max-segment-utf8", ArchiveSafetyLimits.HardMaximumSegmentUtf8),
        MaximumSegments = ParseInt32(values, "--max-segments", ArchiveSafetyLimits.HardMaximumSegments)
    };

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw new CliUsageException("A required option is missing.");

    private static long ParseInt64(
        IReadOnlyDictionary<string, string> values,
        string name,
        long defaultValue)
    {
        if (!values.TryGetValue(name, out var value))
            return defaultValue;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CliUsageException("A numeric option is invalid.");
    }

    private static int ParseInt32(
        IReadOnlyDictionary<string, string> values,
        string name,
        int defaultValue)
    {
        if (!values.TryGetValue(name, out var value))
            return defaultValue;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CliUsageException("A numeric option is invalid.");
    }

    private static double ParseDouble(
        IReadOnlyDictionary<string, string> values,
        string name,
        double defaultValue)
    {
        if (!values.TryGetValue(name, out var value))
            return defaultValue;
        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CliUsageException("A numeric option is invalid.");
    }

    private static void WriteSuccess(TextWriter output, ArchiveExtractionResult result)
    {
        var payload = new
        {
            schemaVersion = "pegasus.cortex.archive-tool-result/1",
            status = "succeeded",
            archiveSha256 = result.ArchiveSha256,
            compressedBytes = result.CompressedBytes,
            tarStreamBytes = result.TarStreamBytes,
            totalFileBytes = result.TotalFileBytes,
            archiveEntryCount = result.ArchiveEntryCount,
            materializedEntryCount = result.MaterializedEntryCount
        };
        output.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static void WriteError(
        TextWriter output,
        string cause,
        string message,
        string? primaryCause,
        int? entryIndex,
        int cleanupFailureCount)
    {
        var payload = new
        {
            schemaVersion = "pegasus.cortex.archive-tool-error/1",
            status = "failed",
            cause,
            message,
            primaryCause,
            entryIndex,
            cleanupFailureCount
        };
        output.WriteLine(JsonSerializer.Serialize(payload));
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
