using System.Text;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Serialization;

namespace Pegasus.Cortex.DefaultLibrary.Seeds;

/// <summary>
/// Regression scanner for the active curated baseline. Historical artifacts are excluded by path.
/// </summary>
public static class DefaultLibraryBaselineScanner
{
    private static readonly string[] DefaultForbiddenTokens =
    [
        string.Concat("Pegasus", "AI"),
        string.Concat("pegasus", "ai", "-operations"),
        string.Concat("Server", " Mgmt"),
        string.Concat("server", "-management"),
        string.Concat("host ", "inventory"),
        string.Concat("hardware ", "inventory"),
        string.Concat("management ", "playbook"),
        string.Concat("access ", "procedure"),
        string.Concat("operational ", "risk"),
        string.Concat("service-control ", "detail")
    ];

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".json", ".md", ".props", ".slnx"
    };

    public static IReadOnlyList<string> ScanBundle(
        KnowledgeSeedBundleV1 bundle,
        IEnumerable<string>? additionalForbiddenTokens = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var violations = ScanText(
            KnowledgeBundleSerializer.ExportText(bundle),
            "default-library-bundle",
            additionalForbiddenTokens).ToList();
        var sourceValidation = DefaultLibrarySourcePolicy.Validate(bundle);
        violations.AddRange(sourceValidation.Diagnostics.Select(diagnostic =>
            $"default-library-bundle: {diagnostic.Code}"));
        return violations;
    }

    public static IReadOnlyList<string> ScanActiveTree(
        string projectRoot,
        IEnumerable<string>? additionalForbiddenTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsHistoricalOrGenerated(root, path) || !ScannedExtensions.Contains(Path.GetExtension(path)))
                continue;
            var relative = Path.GetRelativePath(root, path);
            var text = File.ReadAllText(path, Encoding.UTF8);
            violations.AddRange(ScanText(text, relative, additionalForbiddenTokens));
        }
        return violations;
    }

    private static IEnumerable<string> ScanText(
        string text,
        string source,
        IEnumerable<string>? additionalForbiddenTokens)
    {
        var tokens = DefaultForbiddenTokens.Concat(additionalForbiddenTokens ?? [])
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                yield return $"{source}: forbidden baseline token detected";
        }
    }

    private static bool IsHistoricalOrGenerated(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "bin" or "obj" or ".git"))
            return true;
        if (segments.Length > 0 && string.Equals(segments[0], "artifacts", StringComparison.OrdinalIgnoreCase))
            return true;

        // Project recovery notes intentionally retain exact local evidence. They are
        // authority inputs for agents, not candidate content for the shared library.
        if (segments.Length > 0 && string.Equals(segments[0], ".cortex", StringComparison.OrdinalIgnoreCase))
            return true;
        return segments.Length == 1 &&
            string.Equals(segments[0], "START_HERE.md", StringComparison.OrdinalIgnoreCase);
    }
}
