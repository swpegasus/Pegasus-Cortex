using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Geometry.Shadow;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal sealed record CortexShadowConfiguration(string State, string? Issue,
    CortexShadowProfile? Profile = null, string? ResearchRoot = null, CortexShadowOptions? Options = null)
{
    internal const string FileName = "geometry-shadow.v1.json";
    internal const string Schema = "cortex.shadow.host-config/1";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private sealed record Document(string Schema, bool Enabled, Guid TaskId, Guid ProfileId,
        Guid Generation, string? ResearchRoot, IReadOnlyList<CortexShadowIdentity>? EligibleRecords,
        CortexShadowOptions? Options);

    // Called only by the resident owner. Offline/adapter compositions never resolve ambient config.
    internal static CortexShadowConfiguration ReadOwner(string configDirectory)
    {
        try
        {
            var root = Path.GetFullPath(configDirectory);
            if (!Path.IsPathFullyQualified(configDirectory) || root != configDirectory)
                return new("disabled", "invalid-config-directory");
            var path = Path.Combine(root, FileName);
            CortexProductLayout.RejectExistingReparseComponents(path);
            // FileMode.Open distinguishes absent from inaccessible; no directory is created here.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 512 * 1024) return new("disabled", "config-too-large");
            CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(path, CortexUserStateAccess.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateNames(document.RootElement);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.GetProperty("schema").GetString() != Schema ||
                value.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new("disabled", "invalid-config");
            if (!value.GetProperty("enabled").GetBoolean()) return new("off", null);
            var parsed = value.Deserialize<Document>(Json)!;
            if (parsed.TaskId == Guid.Empty || parsed.ProfileId == Guid.Empty || parsed.Generation == Guid.Empty ||
                parsed.EligibleRecords is null || parsed.EligibleRecords.Count > 2048 || parsed.ResearchRoot is null ||
                parsed.EligibleRecords.Any(id => id is null || id.Namespace is null || id.RecordId is null || id.ProvenanceSha256 is null))
                return new("disabled", "invalid-profile");
            var options = parsed.Options ?? new(CollectionEnabled: true);
            options.Validate();
            if (!options.CollectionEnabled) return new("disabled", "conflicting-collection-options");
            var expected = Path.Combine(root, "geometry-shadow", parsed.Generation.ToString("N"));
            if (parsed.ResearchRoot != Path.GetFullPath(parsed.ResearchRoot) ||
                !StringComparer.OrdinalIgnoreCase.Equals(parsed.ResearchRoot, expected))
                return new("disabled", "research-root-outside-owner-scope");
            // Snapshot the configuration once. Collector repeats exact cohort/budget validation before IO.
            return new("selected", null, new(parsed.TaskId, parsed.ProfileId, parsed.Generation,
                parsed.EligibleRecords.ToArray()), expected, options);
        }
        catch (FileNotFoundException) { return new("absent", null); }
        catch (DirectoryNotFoundException) { return new("absent", null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                   ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or System.Security.SecurityException)
        { return new("disabled", "config-" + error.GetType().Name); }
    }

    private static void ValidateNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate research configuration property.");
                ValidateNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateNames(item);
    }

    internal void PrepareRoot()
    {
        if (State != "selected" || ResearchRoot is null) return;
        var parent = Path.GetDirectoryName(ResearchRoot)!;
        var config = Path.GetDirectoryName(parent)!;
        CortexProductLayout.RejectExistingReparseComponents(ResearchRoot);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(config);
        foreach (var path in new[] { parent, ResearchRoot })
        {
            if (!Directory.Exists(path)) CortexUserStateDirectories.EnsureCurrentUserDirectory(path);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(path);
        }
    }
}
