using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.OfflineJournal.Import;

/// <summary>
/// Screens the small standalone recovery set before its owned staging
/// directory can be promoted. Finding values are intentionally discarded so
/// diagnostics cannot echo rejected source content.
/// </summary>
internal static class LegacyCortexRecoveryContentPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly IReadOnlySet<string> ProhibitedJsonProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "password",
            "passwordhash",
            "sessioncookie",
            "accesstoken",
            "refreshtoken",
            "privatekey",
            "secret",
            "apikey",
            "clientsecret",
            "credential",
            "authorization",
            "cookie",
            "rawterminal",
            "terminaldump",
            "rawchat",
            "chatdump",
            "rawtranscript",
            "transcriptdump",
            "chainofthought",
            "privatereasoning",
            "hiddenreasoning",
            "toolarguments"
        };

    public static void RequireAccepted(
        string relativePath,
        byte[] exactBytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new LegacyCortexRecoveryContentRejectedException(
                ["recovery_path_invalid"]);
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(exactBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new LegacyCortexRecoveryContentRejectedException(
                ["invalid_utf8"]);
        }

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (content.Any(character =>
                char.IsControl(character) &&
                character is not ('\r' or '\n' or '\t')))
        {
            findings.Add("binary_control_character");
        }

        var screening = LearningFeedbackContentScreening.Screen(
            relativePath,
            "standalone recovery file",
            content,
            collective: false);
        foreach (var finding in screening.FindingCodes)
            findings.Add(finding);

        if (string.Equals(relativePath, "meta.json", StringComparison.Ordinal))
            ScreenMetaJson(exactBytes, findings);

        if (findings.Count != 0)
            throw new LegacyCortexRecoveryContentRejectedException(findings);
    }

    private static void ScreenMetaJson(
        byte[] exactBytes,
        ISet<string> findings)
    {
        try
        {
            using var document = JsonDocument.Parse(
                exactBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            ScreenElement(document.RootElement, findings);
        }
        catch (JsonException)
        {
            findings.Add("meta_json_invalid");
        }
    }

    private static void ScreenElement(
        JsonElement element,
        ISet<string> findings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = NormalizePropertyName(property.Name);
                if (ProhibitedJsonProperties.Contains(normalized))
                    findings.Add("prohibited_json_property");
                ScreenElement(property.Value, findings);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return;
        foreach (var child in element.EnumerateArray())
            ScreenElement(child, findings);
    }

    private static string NormalizePropertyName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}

internal sealed class LegacyCortexRecoveryContentRejectedException :
    Exception
{
    public LegacyCortexRecoveryContentRejectedException(
        IEnumerable<string> findingCodes)
        : base("A recovery source was rejected by deterministic content screening.")
    {
        ArgumentNullException.ThrowIfNull(findingCodes);
        FindingCodes = Array.AsReadOnly(
            findingCodes
                .Where(code =>
                    code is { Length: > 0 and <= 128 } &&
                    code.All(character =>
                        char.IsAsciiLetterOrDigit(character) ||
                        character is '_' or '-'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal)
                .Take(32)
                .ToArray());
        if (FindingCodes.Count == 0)
            throw new ArgumentException(
                "At least one bounded finding code is required.",
                nameof(findingCodes));
    }

    public IReadOnlyList<string> FindingCodes { get; }
}
