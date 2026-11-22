namespace Pegasus.Cortex.Core.Knowledge;

/// <summary>
/// Optional provider-owned namespace compatibility. Core uses identity mapping
/// for providers that do not implement it, which preserves task-scoped file
/// bindings and older offline schemas exactly.
/// </summary>
public interface ICortexKnowledgeSpaceMapper
{
    string CanonicalizeNamespace(string value);

    IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values);
}

/// <summary>
/// Human-readable, base-first knowledge-space names. Project namespaces retain
/// their existing coordinates because they also own task lifecycle state.
/// Legacy knowledge names are accepted only as one-hop inputs and collapse to
/// one canonical logical space; new writes always use the canonical name.
/// </summary>
public static class CortexKnowledgeSpaces
{
    public const string Shared = "knowledge.shared";
    public const string User = "knowledge.user";

    public const string LegacySharedProgramming = "shared.programming";
    public const string LegacySharedGeneral = "shared.general";
    public const string LegacyUserPreferences = "user.preferences";

    public static string Canonicalize(string value) => value switch
    {
        LegacySharedProgramming or LegacySharedGeneral => Shared,
        LegacyUserPreferences => User,
        _ => value
    };

    public static IReadOnlyList<string> Canonicalize(
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var canonical = Canonicalize(value);
            if (seen.Add(canonical))
                result.Add(canonical);
        }
        return result.AsReadOnly();
    }

    /// <summary>
    /// Ordered physical read sources. Canonical records win over retained
    /// legacy coordinates during the explicit one-release transition.
    /// </summary>
    public static IReadOnlyList<string> ReadSources(string canonical) => canonical switch
    {
        Shared => [Shared, LegacySharedProgramming, LegacySharedGeneral],
        User => [User, LegacyUserPreferences],
        _ => [canonical]
    };

    public static bool IsGeometric(string value) => Canonicalize(value) is Shared or User;

    public static string DisplayPath(string value) => Canonicalize(value) switch
    {
        Shared => "Knowledge.Shared",
        User => "Knowledge.User",
        var project when project.StartsWith("project.", StringComparison.Ordinal) =>
            $"Project.{project["project.".Length..]}",
        var other => other
    };
}
