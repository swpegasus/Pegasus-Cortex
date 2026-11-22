using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;

namespace Pegasus.Cortex.Core.V2;

public sealed partial class CortexV2Application
{
    private readonly ICortexPersonalProfileProvider? _personalProfileProvider;

    private async ValueTask<CortexPersonalProfileV2?> PersonalProfileAsync(CancellationToken cancellationToken)
    {
        if (_personalProfileProvider is null)
            return null; // Existing provider-neutral/unmanaged compositions are unchanged.
        try
        {
            var value = await _personalProfileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value.State is not ("loaded" or "not_loaded") ||
                value.Profile is { } label && (string.IsNullOrWhiteSpace(label) ||
                    label.Length > CortexV2Limits.MaximumPersonalProfileCharacters || label.Any(char.IsControl)) ||
                value.Generation is { } generation && !IsProfileIdentifier(generation, CortexV2Limits.MaximumPersonalGenerationCharacters) ||
                value.IssueCode is { } issue && !IsProfileIdentifier(issue, CortexV2Limits.MaximumPersonalIssueCodeCharacters) ||
                value.State == "loaded" && (value.Profile is null || value.Generation is null))
                return new("not_loaded", IssueCode: "personal_profile_invalid");
            return value.State == "loaded" ? value : value with
            {
                Profile = null,
                IssueCode = value.IssueCode ?? "personal_profile_not_loaded"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or
            JsonException or System.Security.SecurityException)
        {
            // Optional profile transport/validation failure cannot disable core continuity.
            // Exception messages may carry local details; emit only a fixed issue token.
            return new("not_loaded", IssueCode: "personal_profile_unavailable");
        }
    }

    private static bool IsProfileIdentifier(string value, int maximum) =>
        value.Length is > 0 && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private ValueTask<CortexPersonalProfileV2?> PersonalProfileForAsync(
        IEnumerable<string> namespaces, CancellationToken cancellationToken) =>
        _personalProfileProvider is not null && namespaces.Contains(CortexKnowledgeSpaces.User, StringComparer.Ordinal)
            ? PersonalProfileAsync(cancellationToken)
            : ValueTask.FromResult<CortexPersonalProfileV2?>(null);

    private async ValueTask<CortexPersonalProfileV2?> RequirePersonalAdmissionAsync(
        IEnumerable<string> namespaces, CancellationToken cancellationToken)
    {
        var profile = await PersonalProfileForAsync(namespaces, cancellationToken).ConfigureAwait(false);
        if (profile is { State: not "loaded" })
            throw new CortexPersonalProfileException("personal_profile_not_loaded",
                profile.Retryable
                    ? "The optional personal profile host is temporarily unavailable. This does not indicate missing data and requires no database remap. Core shared, project, and task continuity remains available; retry once when personal context matters."
                    : "The optional personal profile is not loaded. This does not indicate missing data and requires no database remap. Core shared, project, and task continuity remains available.");
        return profile;
    }

    private async ValueTask RequirePersonalSnapshotAsync(
        CortexPersonalProfileV2? admitted, CancellationToken cancellationToken)
    {
        // A filtered request admitted while unloaded contains only core data;
        // recovery of the optional layer must not reject its completed result.
        if (admitted is null || admitted.State != "loaded")
            return;
        var current = await PersonalProfileAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current.State != admitted.State || current.Profile != admitted.Profile ||
            current.Generation != admitted.Generation)
            throw new CortexPersonalProfileException("personal_profile_changed",
                "The optional personal profile changed during this read. Its result was withheld; retry with fresh context.");
    }

    private static IReadOnlyList<string> AvailableNamespaces(
        IReadOnlyList<string> namespaces, CortexPersonalProfileV2 profile) =>
        profile.State == "loaded" ? namespaces :
            Array.AsReadOnly(namespaces.Where(value => value != CortexKnowledgeSpaces.User).ToArray());

    private static string PersonalCursorScope(string operation, IReadOnlyList<string> namespaces, params string?[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { operation, namespaces, values })));

    private static string PersonalCursorPrefix(CortexPersonalProfileV2 profile, string scope) =>
        "p1." + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.State, profile.Profile, profile.Generation, scope
        })))[..32] + ".";

    private static string? UnwrapPersonalCursor(string? cursor, CortexPersonalProfileV2 profile,
        string scope, bool resetOnChange)
    {
        if (cursor is null)
            return null;
        var prefix = PersonalCursorPrefix(profile, scope);
        if (cursor.StartsWith(prefix, StringComparison.Ordinal) && cursor.Length > prefix.Length)
        {
            var inner = cursor[prefix.Length..];
            CortexV2Policy.RequireIdentifier(inner, nameof(cursor));
            return inner;
        }
        if (resetOnChange)
            return null;
        throw new CortexPersonalProfileException("personal_profile_changed",
            "The personal-sensitive search cursor is no longer valid. Restart the search without its cursor.");
    }

    private static string WrapPersonalCursor(string cursor, CortexPersonalProfileV2 profile, string scope)
    {
        CortexV2Policy.RequireIdentifier(cursor, nameof(cursor));
        return PersonalCursorPrefix(profile, scope) + cursor;
    }

    private static int PersonalStoreBudget(int maximum, CortexPersonalProfileV2 profile) =>
        Math.Max(CortexV2Limits.MinimumResponseUtf8Bytes,
            maximum - JsonSerializer.SerializeToUtf8Bytes(profile, CortexV2Json.Options).Length - 64);

    private static bool FitsPersonalBudget<T>(T value, int maximum) =>
        JsonSerializer.SerializeToUtf8Bytes(value, CortexV2Json.Options).Length <= maximum;

    private static CortexStartResultV2 BudgetPersonalStart(CortexStartResultV2 result,
        CortexPersonalProfileV2? profile, string scope, int maximum)
    {
        if (profile is null)
            return CortexV2Policy.RequireBudget(result, maximum);
        result = result with
        {
            PersonalProfile = profile,
            ContextCursor = WrapPersonalCursor(result.ContextCursor, profile, scope)
        };
        // Display labels are optional at tight budgets; generation/state remain explicit.
        if (!FitsPersonalBudget(result, maximum))
            result = result with { PersonalProfile = profile with { Profile = null } };
        while (!FitsPersonalBudget(result, maximum) && result.Items.Count > 0)
            result = result with { Items = result.Items.Take(result.Items.Count - 1).ToArray(), HasMore = true };
        return CortexV2Policy.RequireBudget(result, maximum);
    }

    private static CortexSearchResultV2 BudgetPersonalSearch(CortexSearchResultV2 result,
        CortexPersonalProfileV2? profile, string scope, int maximum)
    {
        if (profile is null)
            return CortexV2Policy.RequireBudget(result, maximum);
        result = result with
        {
            PersonalProfile = profile,
            NextCursor = result.NextCursor is null ? null : WrapPersonalCursor(result.NextCursor, profile, scope)
        };
        if (!FitsPersonalBudget(result, maximum))
            result = result with { PersonalProfile = profile with { Profile = null } };
        // Never drop search hits after pagination: doing so could skip records.
        return CortexV2Policy.RequireBudget(result, maximum);
    }
}
