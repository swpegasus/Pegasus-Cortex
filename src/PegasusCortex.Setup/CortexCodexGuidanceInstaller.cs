using System.Text;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexCodexGuidanceReceiptV1(
    string Contract,
    int Version,
    string Target,
    string Disposition,
    bool LegacyAgentOpsSectionReplaced,
    string OriginalSha256,
    string ResultSha256,
    int ResultBytes);

internal sealed record CortexCodexGuidancePlan(CortexCodexFileUpdate Update, bool LegacyAgentOpsSectionReplaced)
{
    internal CortexCodexGuidanceReceiptV1 Apply()
    {
        CortexCodexGuidanceInstaller.RequireUnshadowedTarget(Update.Target);
        Update.Apply();
        return new("pegasus-cortex-codex-guidance", 1, Update.Target, Update.Disposition,
            LegacyAgentOpsSectionReplaced, Update.OriginalSha256, Update.ResultSha256, Update.Result.Length);
    }
}

internal static class CortexCodexGuidanceInstaller
{
    private const int MaximumAgentsBytes = 1_048_576;
    private const int MaximumFragmentBytes = 4_096;
    private const string CortexStartBoundary = "## Pegasus Cortex continuity";
    private const string CortexEndBoundary = "End of Pegasus Cortex continuity";
    private const string LegacyCortexStartMarker = "<!-- PEGASUS:CORTEX:MANAGED:";
    private const string LegacyCortexEndMarker = "<!-- /PEGASUS:CORTEX:MANAGED:";
    private const string PaoStartMarker = "<!-- PEGASUS:CORTEX:PAO:MANAGED:";
    private const string PaoEndMarker = "<!-- /PEGASUS:CORTEX:PAO:MANAGED:";
    private const string LegacyRoute = "`pegasus_agentops` MCP tool `pao`";

    private static readonly Regex CortexBlock = new(
        @"^## Pegasus Cortex continuity\r?\n.*?^End of Pegasus Cortex continuity(?=\r?$)",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyCortexBlock = new(
        @"<!-- PEGASUS:CORTEX:MANAGED:v(?<version>[1-9][0-9]*) -->.*?<!-- /PEGASUS:CORTEX:MANAGED:v\k<version> -->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PaoBlock = new(
        @"<!-- PEGASUS:CORTEX:PAO:MANAGED:v(?<version>[1-9][0-9]*) -->.*?<!-- /PEGASUS:CORTEX:PAO:MANAGED:v\k<version> -->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyAgentOpsSection = new(
        @"^## Pegasus AgentOps \(PAO\)\r?\n.*?(?=^## |\z)",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static string ResolveDefaultTarget() =>
        ResolveDefaultTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ResolveDefaultTarget(string userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile) ||
            userProfile.StartsWith("\\\\", StringComparison.Ordinal) ||
            userProfile.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("The installing Codex user profile is unavailable or non-local.");
        var canonicalProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        if (!string.Equals(
                canonicalProfile,
                Path.TrimEndingDirectorySeparator(userProfile),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installing Codex user profile is not canonical.");
        return Path.Combine(canonicalProfile, ".codex", "AGENTS.md");
    }

    public static CortexCodexGuidanceReceiptV1 ApplyFromVerifiedPackage(
        CortexVerifiedProductPackageV1 package,
        CortexProductCatalogV1 catalog,
        string targetPath) => PrepareFromVerifiedPackage(package, catalog, targetPath).Apply();

    internal static CortexCodexGuidancePlan PrepareFromVerifiedPackage(
        CortexVerifiedProductPackageV1 package,
        CortexProductCatalogV1 catalog,
        string targetPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(catalog);
        var mcp = catalog.Roles.Single(role => role.Role == "mcp");
        var semanticsDirectory = Path.GetDirectoryName(
            mcp.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ??
            throw new InvalidDataException("The MCP product role has no immutable directory.");
        var cortexRelative = ToPackageRelative(Path.Combine(
            semanticsDirectory, "guidance", "pegasus-cortex-agents.v2.md"));
        var paoRelative = ToPackageRelative(Path.Combine(
            semanticsDirectory, "guidance", "pegasus-cortex-pao.v1.md"));
        var inventory = package.Files.Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        if (!inventory.Contains(cortexRelative) || !inventory.Contains(paoRelative))
            throw new InvalidDataException("The verified package omits a required managed Codex guidance fragment.");
        return Prepare(
            targetPath,
            ResolvePackageFile(package.Root, cortexRelative),
            ResolvePackageFile(package.Root, paoRelative));
    }

    internal static CortexCodexGuidanceReceiptV1 Apply(
        string targetPath,
        string cortexFragmentPath,
        string paoFragmentPath) => Prepare(targetPath, cortexFragmentPath, paoFragmentPath).Apply();

    internal static CortexCodexGuidancePlan Prepare(
        string targetPath,
        string cortexFragmentPath,
        string paoFragmentPath)
    {
        var target = CortexCodexFileUpdate.RequireTarget(targetPath, "AGENTS.md");
        RequireUnshadowedTarget(target);
        var existed = File.Exists(target);
        var originalBytes = CortexCodexFileUpdate.ReadBounded(target, MaximumAgentsBytes);
        var hasBom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var originalText = DecodeUtf8(originalBytes, hasBom);
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var cortexFragment = ReadCortexFragment(cortexFragmentPath, newline);
        var paoFragment = ReadFragment(
            paoFragmentPath, PaoBlock, PaoStartMarker, PaoEndMarker, newline);

        var (withPao, legacyReplaced) = UpsertPao(originalText, paoFragment, newline);
        var resultText = UpsertCortexAtTop(withPao, cortexFragment, newline);
        if (resultText.Contains(LegacyRoute, StringComparison.Ordinal))
            throw new InvalidDataException("The legacy standalone AgentOps MCP route remains after managed guidance composition.");
        var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetBytes(resultText);
        var resultBytes = payload;
        if (hasBom)
        {
            resultBytes = new byte[Encoding.UTF8.Preamble.Length + payload.Length];
            Encoding.UTF8.Preamble.CopyTo(resultBytes);
            payload.CopyTo(resultBytes, Encoding.UTF8.Preamble.Length);
        }
        if (resultBytes.Length > MaximumAgentsBytes + (2 * MaximumFragmentBytes))
            throw new InvalidDataException("The composed AGENTS.md exceeds the bounded managed-guidance limit.");
        return new(new(target, existed, originalBytes, resultBytes), legacyReplaced);
    }

    internal static CortexCodexGuidancePlan PrepareRemove(string targetPath)
    {
        var target = CortexCodexFileUpdate.RequireTarget(targetPath, "AGENTS.md");
        RequireUnshadowedTarget(target);
        var existed = File.Exists(target);
        var originalBytes = CortexCodexFileUpdate.ReadBounded(target, MaximumAgentsBytes);
        var hasBom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var originalText = DecodeUtf8(originalBytes, hasBom);
        ValidateCortexCoordinates(originalText);
        ValidateManagedCoordinates(originalText, PaoBlock, PaoStartMarker, PaoEndMarker);
        var matches = CortexBlock.Matches(originalText).Cast<Match>()
            .Concat(LegacyCortexBlock.Matches(originalText).Cast<Match>())
            .Concat(PaoBlock.Matches(originalText).Cast<Match>())
            .OrderByDescending(match => match.Index)
            .ToArray();
        var resultText = originalText;
        foreach (var match in matches)
        {
            var start = match.Index;
            var end = match.Index + match.Length;
            while (end < resultText.Length && resultText[end] is '\r' or '\n') end++;
            if (start > 0 && end < resultText.Length && resultText[start - 1] == '\n') start--;
            resultText = resultText[..start] + resultText[end..];
        }
        var payload = new UTF8Encoding(false, true).GetBytes(resultText);
        byte[] resultBytes = hasBom ? [.. Encoding.UTF8.Preamble, .. payload] : payload;
        return new(new(target, existed, originalBytes, resultBytes), false);
    }

    internal static void RequireUnshadowedTarget(string target)
    {
        var shadow = CortexCodexFileUpdate.RequireTarget(
            Path.Combine(Path.GetDirectoryName(target)!, "AGENTS.override.md"), "AGENTS.override.md");
        if (File.Exists(shadow) && new FileInfo(shadow).Length > 0)
            throw new InvalidDataException("AGENTS.override.md shadows the selected AGENTS.md. Resolve that override explicitly before selecting Codex guidance; setup will not alter it.");
    }

    private static (string Text, bool LegacyReplaced) UpsertPao(
        string input,
        string fragment,
        string newline)
    {
        ValidateManagedCoordinates(input, PaoBlock, PaoStartMarker, PaoEndMarker);
        var managed = PaoBlock.Matches(input);
        if (managed.Count == 1)
        {
            var match = managed[0];
            return (input[..match.Index] + fragment + input[(match.Index + match.Length)..], false);
        }
        var legacy = LegacyAgentOpsSection.Matches(input);
        if (legacy.Count > 1)
            throw new InvalidDataException("The target contains duplicate legacy AgentOps guidance sections.");
        if (legacy.Count == 1)
        {
            var match = legacy[0];
            return (input[..match.Index] + fragment + newline + newline + input[(match.Index + match.Length)..], true);
        }
        return (PrependBlock(input, fragment, newline), false);
    }

    private static string UpsertManagedBlockAtTop(
        string input,
        string fragment,
        Regex block,
        string startMarker,
        string endMarker,
        string newline)
    {
        ValidateManagedCoordinates(input, block, startMarker, endMarker);
        var matches = block.Matches(input);
        if (matches.Count == 0)
            return PrependBlock(input, fragment, newline);
        var match = matches[0];
        if (match.Index == 0)
            return fragment + input[match.Length..];

        var start = match.Index;
        var end = match.Index + match.Length;
        if (input.AsSpan(0, start).EndsWith(newline + newline, StringComparison.Ordinal))
            start -= 2 * newline.Length;
        else if (input.AsSpan(0, start).EndsWith(newline, StringComparison.Ordinal))
            start -= newline.Length;
        if (input.AsSpan(end).StartsWith(newline, StringComparison.Ordinal))
            end += newline.Length;
        return PrependBlock(input[..start] + input[end..], fragment, newline);
    }

    private static string UpsertCortexAtTop(string input, string fragment, string newline)
    {
        ValidateCortexCoordinates(input);
        var current = CortexBlock.Matches(input);
        var legacy = LegacyCortexBlock.Matches(input);
        if (current.Count == 0 && legacy.Count == 0)
            return PrependBlock(input, fragment, newline);

        var match = current.Count == 1 ? current[0] : legacy[0];
        if (match.Index == 0)
            return fragment + input[match.Length..];

        var start = match.Index;
        var end = match.Index + match.Length;
        if (input.AsSpan(0, start).EndsWith(newline + newline, StringComparison.Ordinal))
            start -= 2 * newline.Length;
        else if (input.AsSpan(0, start).EndsWith(newline, StringComparison.Ordinal))
            start -= newline.Length;
        if (input.AsSpan(end).StartsWith(newline, StringComparison.Ordinal))
            end += newline.Length;
        return PrependBlock(input[..start] + input[end..], fragment, newline);
    }

    private static string PrependBlock(string input, string fragment, string newline)
    {
        if (input.Length == 0)
            return fragment + newline;
        return input.StartsWith(newline, StringComparison.Ordinal)
            ? fragment + newline + input
            : fragment + newline + newline + input;
    }

    private static void ValidateManagedCoordinates(
        string value,
        Regex completeBlock,
        string startMarker,
        string endMarker)
    {
        var starts = Count(value, startMarker);
        var ends = Count(value, endMarker);
        var complete = completeBlock.Matches(value).Count;
        if (starts != ends || starts != complete || complete > 1)
            throw new InvalidDataException("Managed Codex guidance markers are malformed or duplicated.");
    }

    private static void ValidateCortexCoordinates(string value)
    {
        var legacyStarts = Count(value, LegacyCortexStartMarker);
        var legacyEnds = Count(value, LegacyCortexEndMarker);
        var legacy = LegacyCortexBlock.Matches(value);
        if (legacyStarts != legacyEnds || legacyStarts != legacy.Count || legacy.Count > 1)
            throw new InvalidDataException("Pegasus Cortex guidance boundaries are malformed or duplicated.");

        var currentText = value;
        if (legacy.Count == 1)
        {
            var block = legacy[0];
            // Older managed blocks use this same heading inside comment boundaries.
            // Exclude only a validated legacy span; nested new boundaries stay invalid.
            if (CountExactLine(block.Value, CortexStartBoundary) > 1 ||
                CountExactLine(block.Value, CortexEndBoundary) != 0)
                throw new InvalidDataException("Pegasus Cortex guidance boundaries are malformed or duplicated.");
            currentText = value.Remove(block.Index, block.Length);
        }
        var starts = CountExactLine(currentText, CortexStartBoundary);
        var ends = CountExactLine(currentText, CortexEndBoundary);
        var current = CortexBlock.Matches(currentText).Count;
        if (starts != ends || starts != current || current > 1 || current + legacy.Count > 1)
            throw new InvalidDataException("Pegasus Cortex guidance boundaries are malformed or duplicated.");
    }

    private static string ReadCortexFragment(string path, string newline)
    {
        var file = RequireOrdinaryFile(path, "managed Cortex guidance fragment");
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length is 0 or > MaximumFragmentBytes)
            throw new InvalidDataException("A managed Cortex guidance fragment is empty or oversized.");
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = DecodeUtf8(bytes, hasBom).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').TrimEnd('\n');
        ValidateCortexCoordinates(text);
        if (LegacyCortexBlock.IsMatch(text) || CortexBlock.Matches(text).Count != 1 ||
            CortexBlock.Match(text).Value != text)
            throw new InvalidDataException("A managed Cortex guidance fragment has content outside its stable boundaries.");
        return text.Replace("\n", newline, StringComparison.Ordinal);
    }

    private static string ReadFragment(
        string path,
        Regex expectedBlock,
        string startMarker,
        string endMarker,
        string newline)
    {
        var file = RequireOrdinaryFile(path, "managed guidance fragment");
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length is 0 or > MaximumFragmentBytes)
            throw new InvalidDataException("A managed Codex guidance fragment is empty or oversized.");
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = DecodeUtf8(bytes, hasBom).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').TrimEnd('\n');
        ValidateManagedCoordinates(text, expectedBlock, startMarker, endMarker);
        if (expectedBlock.Matches(text).Count != 1 || expectedBlock.Match(text).Value != text)
            throw new InvalidDataException("A managed Codex guidance fragment has content outside its versioned block.");
        return text.Replace("\n", newline, StringComparison.Ordinal);
    }

    private static string RequireOrdinaryFile(string path, string description)
    {
        var canonical = Path.GetFullPath(path);
        var info = new FileInfo(canonical);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException($"The {description} is absent or is a reparse point.", canonical);
        return canonical;
    }

    private static string ResolvePackageFile(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(
            root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var within = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(within) || within == ".." ||
            within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A managed guidance path escaped the verified package root.");
        return RequireOrdinaryFile(path, "verified package guidance fragment");
    }

    private static string DecodeUtf8(byte[] value, bool hasBom)
    {
        var offset = hasBom ? Encoding.UTF8.Preamble.Length : 0;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(value, offset, value.Length - offset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Codex guidance must be strict UTF-8.", exception);
        }
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static int CountExactLine(string value, string line) =>
        Regex.Matches(value, $@"(?m)^{Regex.Escape(line)}\r?$").Count;

    private static string ToPackageRelative(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

}
