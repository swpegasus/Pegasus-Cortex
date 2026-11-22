using System.Text;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// A deterministic, non-authoritative projection of one immutable project-bound
/// proposal. The exact proposal remains the audit source; only this projection
/// is eligible for provisional collective search.
/// </summary>
public sealed record LearningFeedbackCollectiveProjectionV1(
    string Title,
    string Summary,
    string Content,
    string Taxonomy,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<LearningFeedbackApplicabilityV1> Applicability,
    string ContentHash,
    IReadOnlyList<string> Transformations);

public sealed record LearningFeedbackCollectiveProjectionResult(
    bool Accepted,
    LearningFeedbackCollectiveProjectionV1? Value,
    IReadOnlyList<string> FindingCodes);

/// <summary>
/// Generalizes project identity and machine-local coordinates without a model,
/// network call, mutable dictionary, or additional persistence layer. Content
/// that resembles a credential, private reasoning, or a raw stream is rejected
/// before any replacement can conceal it.
/// </summary>
public static partial class LearningFeedbackCollectiveProjectionPolicy
{
    public const string ProjectPlaceholder = "[PROJECT]";
    public const string NamespacePlaceholder = "[PROJECT_NAMESPACE]";
    public const string ProjectRootPlaceholder = "[PROJECT_ROOT]";
    public const string InstallRootPlaceholder = "[INSTALL_ROOT]";
    public const string UserRootPlaceholder = "[USER_ROOT]";
    public const string SystemConfigRootPlaceholder = "[SYSTEM_CONFIG_ROOT]";
    public const string FileSystemRootPlaceholder = "[FILESYSTEM_ROOT]";
    public const string ProtectedPathPlaceholder = "[PROTECTED_PATH]";
    public const string PathPlaceholder = FileSystemRootPlaceholder;
    public const string HostPlaceholder = "[HOST]";
    public const string NetworkPlaceholder = "[NETWORK_ADDRESS]";
    public const string HashPlaceholder = "[HASH]";
    public const string OpaqueIdPlaceholder = "[OPAQUE_ID]";
    public const string EmailPlaceholder = "[EMAIL]";

    private const int MaximumTitleCharacters = 512;
    private const int MaximumSummaryCharacters = 2048;
    private const int MaximumContentCharacters = 32 * 1024;

    public static LearningFeedbackCollectiveProjectionResult Create(
        LearningFeedbackProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (!proposal.Payload.TryGetValue("title", out var title) ||
            !proposal.Payload.TryGetValue("content", out var content) ||
            string.IsNullOrWhiteSpace(title) ||
            title.Length > MaximumTitleCharacters ||
            string.IsNullOrWhiteSpace(content) ||
            content.Length > MaximumContentCharacters ||
            string.IsNullOrWhiteSpace(proposal.Summary) ||
            proposal.Summary.Length > MaximumSummaryCharacters)
        {
            return Rejected("collective_projection_payload_invalid");
        }

        var evidenceProjection = string.Join(
            '\n',
            proposal.Evidence.Select(item => item.Reference));
        var unsafeScreen = LearningFeedbackContentScreening.Screen(
            title,
            proposal.Summary,
            string.Concat(content, "\n", evidenceProjection),
            collective: false,
            proposal.Taxonomy,
            proposal.Keywords,
            proposal.Applicability);
        if (!unsafeScreen.Accepted)
        {
            return new LearningFeedbackCollectiveProjectionResult(
                false,
                null,
                unsafeScreen.FindingCodes);
        }

        var replacements = KnownIdentifiers(proposal);
        var transformations = new SortedSet<string>(StringComparer.Ordinal)
        {
            "deterministic-collective-generalization"
        };
        var generalizedTitle = SanitizeText(title, replacements, transformations);
        var generalizedSummary = SanitizeText(
            proposal.Summary,
            replacements,
            transformations);
        var generalizedContent = SanitizeText(content, replacements, transformations);
        if (string.IsNullOrWhiteSpace(generalizedTitle) ||
            string.IsNullOrWhiteSpace(generalizedSummary) ||
            string.IsNullOrWhiteSpace(generalizedContent))
        {
            return Rejected("collective_projection_empty_after_generalization");
        }

        var taxonomy = SanitizeIdentifier(
            proposal.Taxonomy ?? "continuity",
            replacements,
            fallback: "continuity",
            transformations);
        var keywords = (proposal.Keywords ?? [])
            .Select(value => SanitizeText(value, replacements, transformations))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var applicability = (proposal.Applicability ?? [])
            .Select(item => new LearningFeedbackApplicabilityV1(
                SanitizeIdentifier(
                    item.ProductId,
                    replacements,
                    fallback: "project",
                    transformations),
                SanitizeIdentifier(
                    item.ComponentId,
                    replacements,
                    fallback: "component",
                    transformations)))
            .Distinct()
            .OrderBy(item => item.ProductId, StringComparer.Ordinal)
            .ThenBy(item => item.ComponentId, StringComparer.Ordinal)
            .ToArray();

        var finalScreen = LearningFeedbackContentScreening.Screen(
            PathPlaceholdersForScreening(generalizedTitle),
            PathPlaceholdersForScreening(generalizedSummary),
            PathPlaceholdersForScreening(generalizedContent),
            collective: true,
            taxonomy,
            keywords.Select(PathPlaceholdersForScreening).ToArray(),
            applicability);
        if (!finalScreen.Accepted)
        {
            return new LearningFeedbackCollectiveProjectionResult(
                false,
                null,
                finalScreen.FindingCodes);
        }

        var result = new LearningFeedbackCollectiveProjectionV1(
            generalizedTitle,
            generalizedSummary,
            generalizedContent,
            taxonomy,
            Array.AsReadOnly(keywords),
            Array.AsReadOnly(applicability),
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(generalizedContent)),
            Array.AsReadOnly(transformations.ToArray()));
        return new LearningFeedbackCollectiveProjectionResult(true, result, []);
    }

    private static LearningFeedbackCollectiveProjectionResult Rejected(string code) =>
        new(false, null, Array.AsReadOnly([code]));

    private static IReadOnlyList<(string Value, string Placeholder, string Transformation)>
        KnownIdentifiers(LearningFeedbackProposalV1 proposal)
    {
        var values = new Dictionary<string, (string Placeholder, string Transformation)>(
            StringComparer.OrdinalIgnoreCase);
        Add(values, proposal.Namespace, NamespacePlaceholder, "project-identifiers-generalized");

        var namespaceSeparator = proposal.Namespace.LastIndexOf(':');
        if (namespaceSeparator >= 0 && namespaceSeparator < proposal.Namespace.Length - 1)
        {
            var projectId = proposal.Namespace[(namespaceSeparator + 1)..];
            Add(values, projectId, ProjectPlaceholder, "project-identifiers-generalized");
            Add(
                values,
                projectId.Replace('-', ' ').Replace('_', ' '),
                ProjectPlaceholder,
                "project-identifiers-generalized");
            Add(
                values,
                projectId.Replace('-', '_'),
                ProjectPlaceholder,
                "project-identifiers-generalized");
        }

        Add(values, proposal.ProposalId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.ProposalHash, HashPlaceholder, "hashes-generalized");
        Add(values, proposal.Source.MachineId, HostPlaceholder, "host-identifiers-generalized");
        Add(values, proposal.Source.InstanceId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.Source.TaskId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.Source.CorrelationId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.Source.RecordId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.Source.RevisionId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
        Add(values, proposal.Source.ContentHash, HashPlaceholder, "hashes-generalized");
        Add(values, proposal.Source.SubjectId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");

        foreach (var evidence in proposal.Evidence)
        {
            Add(values, evidence.EvidenceId, OpaqueIdPlaceholder, "opaque-identifiers-generalized");
            Add(values, evidence.Sha256, HashPlaceholder, "hashes-generalized");
        }

        return Array.AsReadOnly(
            values
                .OrderByDescending(item => item.Key.Length)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => (item.Key, item.Value.Placeholder, item.Value.Transformation))
                .ToArray());
    }

    private static void Add(
        IDictionary<string, (string Placeholder, string Transformation)> target,
        string? value,
        string placeholder,
        string transformation)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
            return;
        target[value] = (placeholder, transformation);
    }

    private static string SanitizeText(
        string value,
        IReadOnlyList<(string Value, string Placeholder, string Transformation)> replacements,
        ISet<string> transformations)
    {
        var result = NormalizeLineEndings(value);
        result = GeneralizeAbsolutePaths(result, replacements, transformations);
        foreach (var replacement in replacements)
        {
            var replaced = Regex.Replace(
                result,
                $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(replacement.Value)}(?![\\p{{L}}\\p{{N}}])",
                replacement.Placeholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            if (!string.Equals(result, replaced, StringComparison.Ordinal))
            {
                result = replaced;
                transformations.Add(replacement.Transformation);
            }
        }

        result = Replace(result, ServiceEndpointRegex(), NetworkPlaceholder, "network-identifiers-generalized", transformations);
        result = Replace(result, EmailRegex(), EmailPlaceholder, "email-identifiers-generalized", transformations);
        result = Replace(result, LabeledHostRegex(), "$1=" + HostPlaceholder, "host-identifiers-generalized", transformations);
        result = Replace(result, NetworkAddressRegex(), NetworkPlaceholder, "network-identifiers-generalized", transformations);
        result = Replace(result, MacAddressRegex(), HostPlaceholder, "host-identifiers-generalized", transformations);
        result = Replace(result, PrefixedOpaqueIdRegex(), OpaqueIdPlaceholder, "opaque-identifiers-generalized", transformations);
        result = Replace(result, UuidRegex(), OpaqueIdPlaceholder, "opaque-identifiers-generalized", transformations);
        result = Replace(result, HexHashRegex(), HashPlaceholder, "hashes-generalized", transformations);

        result = HorizontalWhitespaceRegex().Replace(result, " ");
        result = ExcessBlankLinesRegex().Replace(result, "\n\n");
        return result.Trim();
    }

    private static string SanitizeIdentifier(
        string value,
        IReadOnlyList<(string Value, string Placeholder, string Transformation)> replacements,
        string fallback,
        ISet<string> transformations)
    {
        var generalized = SanitizeText(value, replacements, transformations);
        if (generalized.Contains('[', StringComparison.Ordinal) ||
            generalized.Contains(']', StringComparison.Ordinal) ||
            generalized.Length == 0 ||
            generalized.Length > LearningFeedbackContractLimits.MaximumIdentifierCharacters)
        {
            return fallback;
        }
        return generalized;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string Replace(
        string value,
        Regex pattern,
        string replacement,
        string transformation,
        ISet<string> transformations)
    {
        var sanitized = pattern.Replace(value, replacement);
        if (!string.Equals(value, sanitized, StringComparison.Ordinal))
            transformations.Add(transformation);
        return sanitized;
    }

    private static string GeneralizeAbsolutePaths(
        string value,
        IReadOnlyList<(string Value, string Placeholder, string Transformation)> replacements,
        ISet<string> transformations)
    {
        var projectAliases = replacements
            .Where(item => string.Equals(
                item.Placeholder,
                ProjectPlaceholder,
                StringComparison.Ordinal))
            .Select(item => NormalizePathKey(item.Value))
            .Where(item => item.Length >= 4)
            .ToHashSet(StringComparer.Ordinal);
        var result = ReplacePaths(value, PegasusRootVariablePathRegex(), projectAliases, transformations);
        result = ReplacePaths(result, TildePathRegex(), projectAliases, transformations);
        result = ReplacePaths(result, QuotedWindowsPathRegex(), projectAliases, transformations);
        result = ReplacePaths(result, WindowsPathRegex(), projectAliases, transformations);
        result = ReplacePaths(result, QuotedUnixPathRegex(), projectAliases, transformations);
        result = ReplacePaths(result, CommonUnixPathRegex(), projectAliases, transformations);
        return ReplacePaths(result, UnixPathRegex(), projectAliases, transformations);
    }

    private static string ReplacePaths(
        string value,
        Regex pattern,
        IReadOnlySet<string> projectAliases,
        ISet<string> transformations)
    {
        var transformed = pattern.Replace(
            value,
            match => GeneralizeAbsolutePath(match.Value, projectAliases));
        if (!string.Equals(value, transformed, StringComparison.Ordinal))
            transformations.Add("path-roots-generalized");
        return transformed;
    }

    private static string GeneralizeAbsolutePath(
        string matchedValue,
        IReadOnlySet<string> projectAliases)
    {
        var raw = matchedValue;
        if (raw.Length >= 2 &&
            raw[0] is '\'' or '"' &&
            raw[^1] == raw[0])
        {
            raw = raw[1..^1];
        }

        var normalized = raw.Replace('\\', '/');
        if (normalized.StartsWith("%PEGASUS_ROOT%", StringComparison.OrdinalIgnoreCase))
        {
            return BuildRootedPath(
                InstallRootPlaceholder,
                normalized["%PEGASUS_ROOT%".Length..]);
        }
        if (normalized.StartsWith("${PEGASUS_ROOT}", StringComparison.OrdinalIgnoreCase))
        {
            return BuildRootedPath(
                InstallRootPlaceholder,
                normalized["${PEGASUS_ROOT}".Length..]);
        }
        if (normalized.StartsWith("$PEGASUS_ROOT", StringComparison.OrdinalIgnoreCase))
        {
            return BuildRootedPath(
                InstallRootPlaceholder,
                normalized["$PEGASUS_ROOT".Length..]);
        }
        if (normalized.StartsWith("~/", StringComparison.Ordinal))
            return BuildRootedPath(UserRootPlaceholder, normalized[2..]);

        var unc = normalized.StartsWith("//", StringComparison.Ordinal);
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (!SafePathSegments(segments))
            return ProtectedPathPlaceholder;

        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (projectAliases.Contains(NormalizePathKey(segments[index])))
            {
                return BuildRootedPath(
                    ProjectRootPlaceholder,
                    segments[(index + 1)..]);
            }
        }

        var driveOffset = segments.Length > 0 &&
            segments[0].Length == 2 &&
            char.IsAsciiLetter(segments[0][0]) &&
            segments[0][1] == ':'
                ? 1
                : 0;
        if (TryInstallRoot(segments, driveOffset, out var installEnd))
        {
            return BuildRootedPath(
                InstallRootPlaceholder,
                segments[installEnd..]);
        }
        if (TryUserRoot(segments, driveOffset, out var userEnd))
            return BuildRootedPath(UserRootPlaceholder, segments[userEnd..]);
        if (TrySystemConfigRoot(segments, driveOffset, out var configEnd))
        {
            return BuildRootedPath(
                SystemConfigRootPlaceholder,
                segments[configEnd..]);
        }

        var genericStart = unc
            ? Math.Min(2, segments.Length)
            : driveOffset;
        return BuildRootedPath(
            FileSystemRootPlaceholder,
            segments[genericStart..]);
    }

    private static bool TryInstallRoot(
        IReadOnlyList<string> segments,
        int offset,
        out int rootEnd)
    {
        rootEnd = 0;
        var candidates = new[]
        {
            new[] { "opt", "pegasus" },
            new[] { "srv", "pegasus" },
            new[] { "var", "lib", "pegasus" },
            new[] { "var", "opt", "pegasus" },
            new[] { "usr", "local", "pegasus" },
            new[] { "program files", "pegasus" },
            new[] { "programdata", "pegasus" },
            new[] { "pegasus" }
        };
        foreach (var candidate in candidates)
        {
            if (SequenceAt(segments, offset, candidate))
            {
                rootEnd = offset + candidate.Length;
                return true;
            }
        }

        for (var index = offset; index < segments.Count; index++)
        {
            if (NormalizePathKey(segments[index]) == "pegasusroot")
            {
                rootEnd = index + 1;
                return true;
            }
        }
        return false;
    }

    private static bool TryUserRoot(
        IReadOnlyList<string> segments,
        int offset,
        out int rootEnd)
    {
        rootEnd = 0;
        if (SequenceAt(segments, offset, ["home"]) ||
            SequenceAt(segments, offset, ["users"]) ||
            SequenceAt(segments, offset, ["documents and settings"]))
        {
            if (segments.Count <= offset + 1)
                return false;
            rootEnd = offset + 2;
            return true;
        }
        if (SequenceAt(segments, offset, ["root"]))
        {
            rootEnd = offset + 1;
            return true;
        }
        return false;
    }

    private static bool TrySystemConfigRoot(
        IReadOnlyList<string> segments,
        int offset,
        out int rootEnd)
    {
        rootEnd = 0;
        foreach (var candidate in new[]
                 {
                     new[] { "etc" },
                     new[] { "usr", "local", "etc" },
                     new[] { "windows" },
                     new[] { "programdata" }
                 })
        {
            if (!SequenceAt(segments, offset, candidate))
                continue;
            rootEnd = offset + candidate.Length;
            return true;
        }
        return false;
    }

    private static bool SequenceAt(
        IReadOnlyList<string> source,
        int offset,
        IReadOnlyList<string> expected)
    {
        if (offset < 0 || source.Count - offset < expected.Count)
            return false;
        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(
                    source[offset + index],
                    expected[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SafePathSegments(IReadOnlyList<string> segments)
    {
        if (segments.Count is <= 0 or > 128)
            return false;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.Length is <= 0 or > 255 ||
                segment is "." or ".." ||
                segment.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
                segment.Any(char.IsControl) ||
                !(index == 0 && segment.Length == 2 && segment[1] == ':') &&
                segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string BuildRootedPath(
        string rootPlaceholder,
        string suffix) =>
        BuildRootedPath(
            rootPlaceholder,
            suffix.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string BuildRootedPath(
        string rootPlaceholder,
        IReadOnlyList<string> suffix)
    {
        if (!SafePathSegments(suffix) || string.Join('/', suffix).Length > 2048)
            return suffix.Count == 0 ? rootPlaceholder : ProtectedPathPlaceholder;
        return suffix.Count == 0
            ? rootPlaceholder
            : string.Concat(rootPlaceholder, "/", string.Join('/', suffix));
    }

    private static string NormalizePathKey(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    // Typed roots are deliberately relative capability markers. Remove only
    // their brackets in the verification copy so the strict absolute-path
    // detector does not mistake the preserved suffix for a live machine path.
    private static string PathPlaceholdersForScreening(string value) =>
        value
            .Replace(ProjectRootPlaceholder, "PROJECT_ROOT", StringComparison.Ordinal)
            .Replace(InstallRootPlaceholder, "INSTALL_ROOT", StringComparison.Ordinal)
            .Replace(UserRootPlaceholder, "USER_ROOT", StringComparison.Ordinal)
            .Replace(SystemConfigRootPlaceholder, "SYSTEM_CONFIG_ROOT", StringComparison.Ordinal)
            .Replace(FileSystemRootPlaceholder, "FILESYSTEM_ROOT", StringComparison.Ordinal)
            .Replace(ProtectedPathPlaceholder, "PROTECTED_PATH", StringComparison.Ordinal);

    [GeneratedRegex(
        "[\\\"'](?:[A-Z]:\\\\|\\\\\\\\)[^\\\"'\\r\\n]+[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex QuotedWindowsPathRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?:[A-Z]:\\\\|\\\\\\\\)[^\\r\\n;,)]*?(?=(?:[.,;:!?](?:\\s|$)|\\s+(?:and|but|then|while|with|where|which|that|because)\\b|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        "[\\\"']/(?!/)[^\\\"'\\r\\n]+[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex QuotedUnixPathRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])/(?:home|opt|srv|var|etc|root|mnt|run|tmp)/[^\\r\\n;,)]*?(?=(?:[.,;:!?](?:\\s|$)|\\s+(?:and|but|then|while|with|where|which|that|because)\\b|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CommonUnixPathRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9:\\]])/(?!/)(?:[^/\\s\\r\\n]+/)+[^/\\s\\r\\n,;)]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(
        "(?:%PEGASUS_ROOT%|\\$\\{PEGASUS_ROOT\\}|\\$PEGASUS_ROOT)[\\\\/][^\\s\\r\\n,;)]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PegasusRootVariablePathRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])~/[^\\s\\r\\n,;)]*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex TildePathRegex();

    [GeneratedRegex(
        "\\b(?:https?|ssh|postgres(?:ql)?|tcp)://[^\\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ServiceEndpointRegex();

    [GeneratedRegex(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        "\\b(host(?:name)?|machine(?:[-_ ]?id)?|node(?:[-_ ]?id)?)\\s*[:=]\\s*[A-Za-z0-9][A-Za-z0-9._-]{2,127}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex LabeledHostRegex();

    [GeneratedRegex(
        "\\b(?:localhost|(?:\\d{1,3}\\.){3}\\d{1,3})(?::\\d{1,5})?\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex NetworkAddressRegex();

    [GeneratedRegex(
        "\\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(
        "\\b(?:plink|proposal|decision|record|revision|task|ctx|screen|audit|grant|receipt|correlation|operation)_[A-Za-z0-9_-]{16,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrefixedOpaqueIdRegex();

    [GeneratedRegex(
        "\\b[0-9A-F]{8}-[0-9A-F]{4}-[1-5][0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(
        "\\b(?:sha256:)?[0-9A-F]{32,128}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex HexHashRegex();

    [GeneratedRegex("[ \\t]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex("\\n{3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ExcessBlankLinesRegex();
}
