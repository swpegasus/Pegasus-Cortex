using System.Text.RegularExpressions;

namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// The only proposal-input shape that may be used to compute identity, hashes,
/// screening evidence, or persistence payloads after caller input is received.
/// Values not recognized as secrets remain byte-for-byte unchanged.
/// </summary>
public sealed record LearningFeedbackSanitizedProposalInput(
    string Title,
    string Summary,
    string Content,
    string? Taxonomy,
    IReadOnlyList<string>? Keywords,
    IReadOnlyList<LearningFeedbackApplicabilityV1>? Applicability,
    IReadOnlyList<LearningFeedbackEvidenceV1> Evidence,
    IReadOnlyList<string> RedactionCodes);

public sealed record LearningFeedbackProposalInputSanitizationResult(
    bool Accepted,
    LearningFeedbackSanitizedProposalInput? Value,
    IReadOnlyList<string> FindingCodes);

/// <summary>
/// Deterministically replaces recognizable secret values before proposal
/// identity and persistence. It never retains a raw secret for provenance.
/// Private reasoning and raw chat, thread, or terminal streams remain hard
/// rejects because no safe derived proposal can preserve their meaning.
/// </summary>
public static partial class LearningFeedbackProposalInputSanitizer
{
    public const string SecretPlaceholder = "[SECRET]";
    public const string TokenPlaceholder = "[TOKEN]";
    public const string PrivateKeyPlaceholder = "[PRIVATE_KEY]";

    private const string IdentifierSecretPlaceholder = "secret-redacted";
    private const string IdentifierTokenPlaceholder = "token-redacted";
    private const string IdentifierPrivateKeyPlaceholder = "private-key-redacted";
    private const int MaximumTitleCharacters = 512;
    private const int MaximumContentCharacters = 32 * 1024;

    public static LearningFeedbackProposalInputSanitizationResult Sanitize(
        string title,
        string summary,
        string content,
        string? taxonomy,
        IReadOnlyList<string>? keywords,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? applicability,
        IReadOnlyList<LearningFeedbackEvidenceV1> evidence)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(evidence);

        if (title.Length is 0 or > MaximumTitleCharacters ||
            summary.Length is 0 or > LearningFeedbackContractLimits.MaximumSummaryCharacters ||
            content.Length is 0 or > MaximumContentCharacters ||
            evidence.Count > LearningFeedbackContractLimits.MaximumEvidenceItems ||
            keywords is { Count: > LearningFeedbackContractLimits.MaximumProposalKeywords } ||
            applicability is
                { Count: > LearningFeedbackContractLimits.MaximumProposalApplicabilityItems })
        {
            return Rejected("proposal_input_sanitization_bounds_invalid");
        }

        var codes = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var sanitizedTitle = RedactText(title, codes);
            var sanitizedSummary = RedactText(summary, codes);
            var sanitizedContent = RedactText(content, codes);
            var sanitizedTaxonomy = taxonomy is null
                ? null
                : RedactIdentifier(taxonomy, codes);
            var sanitizedKeywords = keywords is null
                ? null
                : Array.AsReadOnly(
                    keywords
                        .Select(value => RedactText(value, codes))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
            var sanitizedApplicability = applicability is null
                ? null
                : Array.AsReadOnly(
                    applicability
                        .Select(item => new LearningFeedbackApplicabilityV1(
                            RedactIdentifier(item.ProductId, codes),
                            RedactIdentifier(item.ComponentId, codes)))
                        .Distinct()
                        .OrderBy(item => item.ProductId, StringComparer.Ordinal)
                        .ThenBy(item => item.ComponentId, StringComparer.Ordinal)
                        .ToArray());
            var sanitizedEvidence = Array.AsReadOnly(
                evidence
                    .Select(item => item with
                    {
                        EvidenceId = RedactIdentifier(item.EvidenceId, codes),
                        Reference = RedactText(item.Reference, codes)
                    })
                    .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                    .ToArray());

            var duplicateEvidenceIds = sanitizedEvidence
                .Select(item => item.EvidenceId)
                .Distinct(StringComparer.Ordinal)
                .Count() != sanitizedEvidence.Count;
            if (duplicateEvidenceIds ||
                !WithinBounds(
                    sanitizedTitle,
                    sanitizedSummary,
                    sanitizedContent,
                    sanitizedTaxonomy,
                    sanitizedKeywords,
                    sanitizedApplicability,
                    sanitizedEvidence))
            {
                return Rejected("proposal_input_sanitization_result_invalid");
            }

            var postScreen = LearningFeedbackContentScreening.ScreenProjectBoundProposal(
                sanitizedTitle,
                sanitizedSummary,
                string.Concat(
                    sanitizedContent,
                    "\n",
                    string.Join('\n', sanitizedEvidence.Select(item => item.Reference))),
                collectiveProjectionRequested: false,
                sanitizedTaxonomy,
                sanitizedKeywords,
                sanitizedApplicability);
            if (!postScreen.Accepted)
            {
                return new LearningFeedbackProposalInputSanitizationResult(
                    false,
                    null,
                    postScreen.FindingCodes);
            }

            var redactionCodes = Array.AsReadOnly(codes.ToArray());
            return new LearningFeedbackProposalInputSanitizationResult(
                true,
                new LearningFeedbackSanitizedProposalInput(
                    sanitizedTitle,
                    sanitizedSummary,
                    sanitizedContent,
                    sanitizedTaxonomy,
                    sanitizedKeywords,
                    sanitizedApplicability,
                    sanitizedEvidence,
                    redactionCodes),
                redactionCodes);
        }
        catch (RegexMatchTimeoutException)
        {
            return Rejected("proposal_input_secret_redaction_timeout");
        }
        catch (UnsafeSecretBoundaryException)
        {
            return Rejected("proposal_input_secret_boundary_unsafe");
        }
    }

    /// <summary>
    /// Screens a caller-controlled stable coordinate without ever rewriting it.
    /// Stable keys participate in idempotency and therefore cannot be replaced
    /// with a shared placeholder without collapsing distinct requests. Secret,
    /// private-reasoning, and raw-stream material consequently fails closed.
    /// </summary>
    public static LearningFeedbackContentScreeningResult ScreenStableIdentifier(
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var redacted = RedactText(value, findings);
            if (!string.Equals(value, redacted, StringComparison.Ordinal))
                findings.Add("stable_identifier_secret_material");

            var contentScreen =
                LearningFeedbackContentScreening.ScreenProjectBoundProposal(
                    value,
                    value,
                    value,
                    collectiveProjectionRequested: false);
            foreach (var finding in contentScreen.FindingCodes)
                findings.Add(finding);
        }
        catch (RegexMatchTimeoutException)
        {
            findings.Add("stable_identifier_secret_screen_timeout");
        }
        catch (UnsafeSecretBoundaryException)
        {
            findings.Add("stable_identifier_secret_boundary_unsafe");
        }

        return new LearningFeedbackContentScreeningResult(
            findings.Count == 0,
            Array.AsReadOnly(findings.ToArray()));
    }

    private static string RedactText(string value, ISet<string> codes)
    {
        var result = Replace(
            value,
            CompletePrivateKeyRegex(),
            PrivateKeyPlaceholder,
            "private_key_redacted",
            codes);
        if (PrivateKeyHeaderRegex().IsMatch(result))
            throw new UnsafeSecretBoundaryException();

        result = Replace(
            result,
            UriUserInfoRegex(),
            match => string.Concat(
                match.Groups["prefix"].Value,
                SecretPlaceholder,
                "@"),
            "uri_userinfo_redacted",
            codes);
        result = Replace(
            result,
            DigestAuthorizationRegex(),
            match => string.Concat(
                match.Groups["label"].Value,
                match.Groups["separator"].Value,
                "Digest ",
                TokenPlaceholder),
            "digest_authorization_redacted",
            codes);
        result = Replace(
            result,
            AuthorizationCredentialRegex(),
            match => string.Concat(
                match.Groups["label"].Value,
                match.Groups["separator"].Value,
                match.Groups["scheme"].Success
                    ? match.Groups["scheme"].Value + " "
                    : string.Empty,
                TokenPlaceholder),
            "authorization_token_redacted",
            codes);
        result = Replace(
            result,
            StandaloneDigestRegex(),
            "Digest " + TokenPlaceholder,
            "digest_token_redacted",
            codes);
        result = Replace(
            result,
            StandaloneBasicOrApiKeyRegex(),
            match => string.Concat(
                match.Groups["scheme"].Value,
                " ",
                TokenPlaceholder),
            "authorization_scheme_token_redacted",
            codes);

        result = CredentialAssignmentRegex().Replace(result, match =>
        {
            var label = match.Groups["label"].Value;
            var boundedValue = match.Groups["value"].Value;
            var placeholder = boundedValue.Contains(
                PrivateKeyPlaceholder,
                StringComparison.Ordinal)
                ? PrivateKeyPlaceholder
                : TokenLabelRegex().IsMatch(label)
                    ? TokenPlaceholder
                    : SecretPlaceholder;
            if (placeholder == TokenPlaceholder)
                codes.Add("credential_token_redacted");
            else if (placeholder == SecretPlaceholder)
                codes.Add("credential_secret_redacted");
            return string.Concat(label, match.Groups["separator"].Value, placeholder);
        });
        result = Replace(
            result,
            BearerTokenRegex(),
            "Bearer " + TokenPlaceholder,
            "bearer_token_redacted",
            codes);
        result = Replace(
            result,
            JwtRegex(),
            TokenPlaceholder,
            "jwt_redacted",
            codes);
        result = Replace(
            result,
            ProviderTokenRegex(),
            TokenPlaceholder,
            "provider_token_redacted",
            codes);

        // A remaining recognizable secret marker means its extent was not
        // safely bounded by the deterministic rules above.
        if (ResidualSecretLikeRegex().IsMatch(result))
            throw new UnsafeSecretBoundaryException();
        return result;
    }

    private static string RedactIdentifier(string value, ISet<string> codes)
    {
        var redacted = RedactText(value, codes);
        if (string.Equals(value, redacted, StringComparison.Ordinal))
            return value;
        if (redacted.Contains(PrivateKeyPlaceholder, StringComparison.Ordinal))
            return IdentifierPrivateKeyPlaceholder;
        if (redacted.Contains(TokenPlaceholder, StringComparison.Ordinal))
            return IdentifierTokenPlaceholder;
        return IdentifierSecretPlaceholder;
    }

    private static bool WithinBounds(
        string title,
        string summary,
        string content,
        string? taxonomy,
        IReadOnlyList<string>? keywords,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? applicability,
        IReadOnlyList<LearningFeedbackEvidenceV1> evidence) =>
        title.Length is > 0 and <= MaximumTitleCharacters &&
        summary.Length is > 0 and <= LearningFeedbackContractLimits.MaximumSummaryCharacters &&
        content.Length is > 0 and <= MaximumContentCharacters &&
        (taxonomy is null || taxonomy.Length is > 0 and <=
            LearningFeedbackContractLimits.MaximumIdentifierCharacters) &&
        (keywords ?? []).All(value =>
            value.Length is > 0 and <=
                LearningFeedbackContractLimits.MaximumProposalKeywordCharacters) &&
        (applicability ?? []).All(item =>
            item.ProductId.Length is > 0 and <=
                LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
            item.ComponentId.Length is > 0 and <=
                LearningFeedbackContractLimits.MaximumIdentifierCharacters) &&
        evidence.All(item =>
            item.EvidenceId.Length is > 0 and <=
                LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
            item.Reference.Length is > 0 and <=
                LearningFeedbackContractLimits.MaximumEvidenceReferenceCharacters);

    private static LearningFeedbackProposalInputSanitizationResult Rejected(string code) =>
        new(false, null, Array.AsReadOnly([code]));

    private static string Replace(
        string value,
        Regex pattern,
        string replacement,
        string code,
        ISet<string> codes)
    {
        var redacted = pattern.Replace(value, replacement);
        if (!string.Equals(value, redacted, StringComparison.Ordinal))
            codes.Add(code);
        return redacted;
    }

    private static string Replace(
        string value,
        Regex pattern,
        MatchEvaluator replacement,
        string code,
        ISet<string> codes)
    {
        var redacted = pattern.Replace(value, replacement);
        if (!string.Equals(value, redacted, StringComparison.Ordinal))
            codes.Add(code);
        return redacted;
    }

    [GeneratedRegex(
        "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\\s\\S]{0,65536}?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CompletePrivateKeyRegex();

    [GeneratedRegex(
        "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKeyHeaderRegex();

    [GeneratedRegex(
        "(?<prefix>\\b[a-z][a-z0-9+.-]{1,31}://)(?<userinfo>[^/@\\s]{1,512})@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(
        "(?<label>(?:proxy[-_]?authorization|authorization))(?<separator>[ \\t]*[:=][ \\t]*)Digest\\s+(?<parameters>(?:[A-Za-z][A-Za-z0-9_-]*[ \\t]*=[ \\t]*(?:\"[^\"\\r\\n]*\"|[^,\\s\\r\\n]+)[ \\t]*(?:,[ \\t]*)?){1,32})(?=\\r?$|\\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex DigestAuthorizationRegex();

    [GeneratedRegex(
        "(?<label>(?:proxy[-_]?authorization|authorization))(?<separator>[ \\t]*[:=][ \\t]*)(?!Digest\\b)(?:(?<scheme>Bearer|Basic|ApiKey)[ \\t]+)?(?<value>[^\\s,;\"']{1,32768})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex AuthorizationCredentialRegex();

    [GeneratedRegex(
        "\\bDigest[ \\t]+(?:(?:[A-Za-z][A-Za-z0-9_-]*[ \\t]*=[ \\t]*(?:\"[^\"\\r\\n]*\"|[^,\\s\\r\\n]+)[ \\t]*(?:,[ \\t]*)?){1,32})(?=\\r?$|\\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex StandaloneDigestRegex();

    [GeneratedRegex(
        "\\b(?<scheme>Basic|ApiKey)\\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex StandaloneBasicOrApiKeyRegex();

    [GeneratedRegex(
        "(?<label>password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|session[_-]?cookie|credential|private[_-]?key|secret|token)(?<separator>[ \\t]*[:=][ \\t]*)(?<value>\"[^\"\\r\\n]{1,32768}\"|'[^'\\r\\n]{1,32768}'|Bearer[ \\t]+[^\\s]{1,32768}|[^\\s\"']{1,32768})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        "token|api[_-]?key|authorization|session[_-]?cookie",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex TokenLabelRegex();

    [GeneratedRegex(
        "\\bBearer\\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex BearerTokenRegex();

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
        "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\\bBearer\\s+[A-Za-z0-9._~+/=-]{8,}|\\b(?:Basic|ApiKey)\\s+(?!\\[TOKEN\\])[A-Za-z0-9._~+/=-]{8,}|\\bDigest\\s+(?!\\[TOKEN\\])[^\\r\\n]{1,4096}|(?<protocol>\\b[a-z][a-z0-9+.-]{1,31}://)(?!\\[SECRET\\]@)[^/@\\s]{1,4096}@|\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b|\\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,})\\b|\\b(?:proxy[-_]?authorization|authorization)[ \\t]*[:=](?![ \\t]*(?:(?:Bearer|Basic|ApiKey|Digest)[ \\t]+\\[TOKEN\\]|\\[TOKEN\\]))|\\b(?:password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|session[_-]?cookie|credential|private[_-]?key|secret|token)[ \\t]*[:=](?![ \\t]*\\[(?:SECRET|TOKEN|PRIVATE_KEY)\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ResidualSecretLikeRegex();

    private sealed class UnsafeSecretBoundaryException : Exception
    {
    }
}
