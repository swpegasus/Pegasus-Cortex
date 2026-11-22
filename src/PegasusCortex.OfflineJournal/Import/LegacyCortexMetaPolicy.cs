using System.Text.Json;
using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.OfflineJournal.Import;

/// <summary>
/// Strict syntax/version gate for the legacy metadata that authorizes a 0.2 migration claim.
/// Unknown legacy fields remain preservable, but duplicate names, comments, trailing commas,
/// non-object roots, and any other schema version fail closed.
/// </summary>
internal static class LegacyCortexMetaPolicy
{
    public static void Validate(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CortexSyncContractLimits.MaximumJsonDepth
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Legacy meta.json must contain one JSON object.");
            ValidateUniqueNames(document.RootElement);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                !string.Equals(version.GetString(), CortexSyncContractIds.LegacyCortexSchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Legacy meta.json does not declare the exact supported 0.2 schema.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Legacy meta.json is not strict bounded JSON.", exception);
        }
    }

    private static void ValidateUniqueNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Legacy meta.json contains a duplicate property name.");
                ValidateUniqueNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateUniqueNames(item);
        }
    }
}
