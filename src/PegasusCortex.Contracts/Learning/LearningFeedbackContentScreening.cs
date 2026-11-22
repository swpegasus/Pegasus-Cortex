using System.Text.RegularExpressions;

namespace Pegasus.Cortex.Contracts.Learning;

public sealed record LearningFeedbackContentScreeningResult(
    bool Accepted,
    IReadOnlyList<string> FindingCodes);

/// <summary>
/// Conservative deterministic screening for the MCP retention boundary. It reports
/// finding codes only, so rejected secret or machine-specific content is never echoed.
/// </summary>
public static partial class LearningFeedbackContentScreening
{
    private static readonly (string Code, Regex Pattern)[] AlwaysProhibited =
    [
        ("private_key", PrivateKeyRegex()),
        ("bearer_token", BearerTokenRegex()),
        ("credential_assignment", CredentialAssignmentRegex()),
        ("jwt", JwtRegex()),
        ("provider_token", ProviderTokenRegex()),
        ("raw_private_reasoning", PrivateReasoningRegex()),
        ("raw_stream_reference", RawStreamRegex())
    ];

    private static readonly (string Code, Regex Pattern)[] CollectiveOnlyProhibited =
    [
        ("email_address", EmailRegex()),
        ("absolute_windows_path", WindowsPathRegex()),
        ("absolute_unix_path", UnixPathRegex()),
        ("network_address", NetworkAddressRegex())
    ];

    /// <summary>High-signal semantic-note screening. Existing learning-review rules remain stricter.</summary>
    public static LearningFeedbackContentScreeningResult ScreenSemanticText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (text.Length > 4 * 1024 * 1024)
            return new(false, ["screening_size"]);
        try
        {
            AddFindings(AlwaysProhibited.Where(rule => rule.Code != "credential_assignment"), text, findings);
            var nextFence = text.IndexOf("```", StringComparison.Ordinal);
            var fences = 0;
            var matches = 0;
            foreach (Match match in SemanticCredentialAssignmentRegex().Matches(text))
            {
                if (++matches > 1024) { findings.Add("screening_budget"); break; }
                while (nextFence >= 0 && nextFence < match.Index)
                {
                    fences++;
                    nextFence = text.IndexOf("```", nextFence + 3, StringComparison.Ordinal);
                }
                if (!IsSemanticExampleValue(text, match, fences % 2 == 1)) findings.Add("credential_assignment");
            }
            if (SemanticReasoningRegex().IsMatch(text)) findings.Add("raw_private_reasoning");
        }
        catch (RegexMatchTimeoutException) { findings.Add("screening_budget"); }
        return new(findings.Count == 0, Array.AsReadOnly(findings.ToArray()));
    }

    private static bool IsSemanticExampleValue(string text, Match match, bool fencedCode)
    {
        var raw = match.Groups["value"].Value.TrimEnd(';', ',');
        var quoted = raw.Length >= 2 && raw[0] is '\'' or '"' && raw[^1] == raw[0];
        var value = quoted ? raw[1..^1] : raw;
        if (value.Length == 0) return true;
        if (SemanticPlaceholderRegex().IsMatch(value)) return true;
        if (quoted) return false;
        if (SemanticExpressionRegex().IsMatch(value)) return true;
        if (!SemanticVariableRegex().IsMatch(value)) return false;
        // Plain words in configuration assignments can be real passwords. Treat
        // a bare identifier as code only with an actual code declaration/fence.
        var start = Math.Max(0, match.Index - 128);
        var lineStart = match.Index == 0 ? -1 : text.LastIndexOf('\n', match.Index - 1, match.Index - start);
        var prefix = text[Math.Max(start, lineStart + 1)..match.Index];
        if (SemanticDeclarationRegex().IsMatch(prefix)) return true;
        return fencedCode;
    }

    [GeneratedRegex("""\b(?:password|passwd|secret|credential|authorization|cookie|private[_-]?key|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|session[_-]?cookie)["']?\s*[:=]\s*(?<value>"[^"\r\n]*"|'[^'\r\n]*'|\$\{[A-Za-z_][A-Za-z0-9_]*\}|[^\s,;}]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticCredentialAssignmentRegex();

    [GeneratedRegex("""^(?:\[(?:SECRET|TOKEN|PRIVATE_KEY|REDACTED|(?:YOUR|EXAMPLE|PLACEHOLDER)(?:[_-][A-Z0-9]+)*)\]|<(?:(?:YOUR|EXAMPLE|REDACTED|SECRET|TOKEN|PRIVATE_KEY)[A-Z0-9_-]*)>|(?:YOUR|EXAMPLE|PLACEHOLDER|REPLACE_ME|REDACTED)(?:[_-][A-Z0-9]+)*|\$\{[A-Za-z_][A-Za-z0-9_]*\}|%[A-Za-z_][A-Za-z0-9_]*%|null|none)$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticPlaceholderRegex();

    [GeneratedRegex("""^(?:\$env:[A-Za-z_][A-Za-z0-9_]*|\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%|[A-Za-z_][A-Za-z0-9_.]*\[["'][A-Za-z_][A-Za-z0-9_.:-]*["']\]|(?:[A-Za-z_][A-Za-z0-9_]*\.)+[A-Za-z_][A-Za-z0-9_]*(?:\([^\r\n]*\))?)$""",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticExpressionRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticVariableRegex();

    [GeneratedRegex("\\b(?:var|const|let|string|readonly|final)\\s+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticDeclarationRegex();

    [GeneratedRegex("<(?:think|analysis|private_reasoning)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SemanticReasoningRegex();

    public static LearningFeedbackContentScreeningResult Screen(
        string title,
        string summary,
        string content,
        bool collective,
        string? taxonomy = null,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? applicability = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(content);

        var combined = string.Concat(
            title,
            "\n",
            summary,
            "\n",
            content,
            "\n",
            taxonomy,
            "\n",
            string.Join('\n', (keywords ?? []).Take(
                LearningFeedbackContractLimits.MaximumProposalKeywords + 1)),
            "\n",
            string.Join(
                '\n',
                (applicability ?? [])
                    .Take(LearningFeedbackContractLimits.MaximumProposalApplicabilityItems + 1)
                    .Select(item => item is null
                        ? string.Empty
                        : string.Concat(item.ProductId, "\n", item.ComponentId))));
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        AddFindings(AlwaysProhibited, combined, findings);
        if (collective)
            AddFindings(CollectiveOnlyProhibited, combined, findings);
        return new LearningFeedbackContentScreeningResult(
            findings.Count == 0,
            Array.AsReadOnly(findings.ToArray()));
    }

    /// <summary>
    /// Screens exact proposal bytes that remain inside their project namespace.
    /// Secrets, credentials, private reasoning, and raw streams still fail
    /// closed. Machine-specific findings are retained as non-authoritative
    /// diagnostics because the collective publication projection replaces them
    /// deterministically before the record can become searchable.
    /// </summary>
    public static LearningFeedbackContentScreeningResult ScreenProjectBoundProposal(
        string title,
        string summary,
        string content,
        bool collectiveProjectionRequested,
        string? taxonomy = null,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? applicability = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(content);

        var combined = Combine(
            title,
            summary,
            content,
            taxonomy,
            keywords,
            applicability);
        var prohibited = new SortedSet<string>(StringComparer.Ordinal);
        AddFindings(AlwaysProhibited, combined, prohibited);

        var findings = new SortedSet<string>(prohibited, StringComparer.Ordinal);
        if (collectiveProjectionRequested)
            AddFindings(CollectiveOnlyProhibited, combined, findings);

        return new LearningFeedbackContentScreeningResult(
            prohibited.Count == 0,
            Array.AsReadOnly(findings.ToArray()));
    }

    private static string Combine(
        string title,
        string summary,
        string content,
        string? taxonomy,
        IReadOnlyList<string>? keywords,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? applicability) =>
        string.Concat(
            title,
            "\n",
            summary,
            "\n",
            content,
            "\n",
            taxonomy,
            "\n",
            string.Join('\n', (keywords ?? []).Take(
                LearningFeedbackContractLimits.MaximumProposalKeywords + 1)),
            "\n",
            string.Join(
                '\n',
                (applicability ?? [])
                    .Take(LearningFeedbackContractLimits.MaximumProposalApplicabilityItems + 1)
                    .Select(item => item is null
                        ? string.Empty
                        : string.Concat(item.ProductId, "\n", item.ComponentId))));

    private static void AddFindings(
        IEnumerable<(string Code, Regex Pattern)> rules,
        string value,
        ISet<string> findings)
    {
        foreach (var (code, pattern) in rules)
        {
            if (pattern.IsMatch(value))
                findings.Add(code);
        }
    }

    [GeneratedRegex(
        "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(
        "\\bBearer\\s+[A-Za-z0-9._~+/=-]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        "\\b(?:password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|session[_-]?cookie)\\s*[:=]\\s*(?!\\[(?:SECRET|TOKEN|PRIVATE_KEY)\\])\\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        "\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        "\\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,})\\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex(
        "(?:<chain_of_thought>|chain[ -]of[ -]thought:|private reasoning:|hidden reasoning:|raw transcript:|raw (?:chat|thread|terminal)(?: transcript| stream| output)?:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateReasoningRegex();

    [GeneratedRegex(
        "\\b(?:terminal|thread|chat)://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex RawStreamRegex();

    [GeneratedRegex(
        "\\b[A-Z]:\\\\(?:[^\\s<>:\"|?*]+\\\\)*[^\\s<>:\"|?*]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])/(?:home|opt|srv|var|etc|root|mnt|run|tmp)/[^\\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        "\\b(?:localhost|(?:\\d{1,3}\\.){3}\\d{1,3})(?::\\d{1,5})?\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex NetworkAddressRegex();
}
