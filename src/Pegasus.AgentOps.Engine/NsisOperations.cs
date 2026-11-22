using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pegasus.AgentOps.Engine;

public sealed record NsisValidationResult(
    string Source,
    int Lines,
    int Bytes,
    string Sha256,
    string TraceTail,
    bool TraceTruncated,
    string? Output);

internal static partial class NsisOperations
{
    private const int MaxScriptBytes = 512 * 1024;
    private const int MaxTraceBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex("(?im)^\\s*!(?:system|execute|appendfile|delfile|packhdr|finalize|uninstfinalize|makensis|tempfile|addplugindir)\\b")]
    private static partial Regex ForbiddenDirective();

    [GeneratedRegex("(?im)^\\s*OutFile\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>\\S+))\\s*$")]
    private static partial Regex OutputDirective();

    public static string ReadAndAdmitSource(string source)
    {
        if (!Path.IsPathFullyQualified(source) || !source.EndsWith(".nsi", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(source);
        var bytes = File.ReadAllBytes(source);
        if (bytes.Length is 0 or > MaxScriptBytes) throw new AgentOpsException("invalid");
        string text;
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            text = StrictUtf8.GetString(bytes.AsSpan(offset));
        }
        catch (DecoderFallbackException ex) { throw new AgentOpsException("encoding_required", inner: ex); }
        RejectUnsafeDirectives(text);
        return text;
    }

    public static IReadOnlyList<string> PreprocessArguments(string source, string? output = null)
    {
        var arguments = new List<string> { "/SAFEPPO", "/V4", "/WX", "/NOCONFIG", "/OUTPUTCHARSET", "UTF8" };
        if (output is not null) arguments.Add("/DPAO_OUT=" + output);
        arguments.Add(source);
        return arguments;
    }

    public static IReadOnlyList<string> BuildArguments(string source, string output) =>
        ["/V4", "/WX", "/NOCONFIG", "/OUTPUTCHARSET", "UTF8", "/DPAO_OUT=" + output, source];

    public static NsisValidationResult ValidateExpanded(string source, ToolExecutionResult result, string? expectedOutput)
    {
        if (result.StdoutTruncated || result.StderrTruncated) throw new AgentOpsException("tool_output_limit");
        var expandedBytes = Encoding.UTF8.GetByteCount(result.Stdout);
        if (expandedBytes is 0 or > MaxScriptBytes) throw new AgentOpsException("tool_output_limit");
        RejectUnsafeDirectives(result.Stdout);
        string? output = null;
        var matches = OutputDirective().Matches(result.Stdout);
        if (expectedOutput is not null)
        {
            if (matches.Count != 1) throw new AgentOpsException("nsis_output_contract");
            output = matches[0].Groups["quoted"].Success ? matches[0].Groups["quoted"].Value : matches[0].Groups["plain"].Value;
            if (!Path.IsPathFullyQualified(output) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(output), Path.GetFullPath(expectedOutput)))
                throw new AgentOpsException("nsis_output_contract");
        }
        var trace = BoundTail(result.Stderr.Length > 0 ? result.Stderr : result.Stdout, MaxTraceBytes);
        return new(source, CountLines(result.Stdout), expandedBytes, Hash(result.Stdout), trace.Text, trace.Truncated, output);
    }

    public static (long Bytes, string Sha256) VerifyInstaller(string output)
    {
        if (!File.Exists(output) || Directory.Exists(output)) throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(output);
        using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > 2L * 1024 * 1024 * 1024) throw new AgentOpsException("integrity");
        Span<byte> header = stackalloc byte[2];
        if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z') throw new AgentOpsException("integrity");
        stream.Position = 0;
        return (stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static void RejectUnsafeDirectives(string script)
    {
        if (script.Contains('\0') || ForbiddenDirective().IsMatch(script))
            throw new AgentOpsException("nsis_unsafe_directive");
    }

    private static int CountLines(string value) => value.Length == 0 ? 0 : value.Count(character => character == '\n') + 1;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static (string Text, bool Truncated) BoundTail(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return (value, false);
        var start = value.Length;
        var bytes = 0;
        while (start > 0)
        {
            var rune = Rune.GetRuneAt(value, start - (char.IsLowSurrogate(value[start - 1]) && start > 1 ? 2 : 1));
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maximumBytes) break;
            bytes += size;
            start -= rune.Utf16SequenceLength;
        }
        return (value[start..], true);
    }
}
