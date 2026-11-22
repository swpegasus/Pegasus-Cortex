using System.Text.Json;

// Shared scalar/text envelope admission. Owning families retain their action,
// authority and versioned-body rules; this never projects unknown fields away.
internal static class ClosedPaoToolArguments
{
    internal static bool TryParse(JsonElement arguments, IReadOnlyList<string> fields,
        IReadOnlyList<string> booleans, out Dictionary<string, string> values, out string? body)
    {
        values = new(StringComparer.Ordinal); body = null;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !fields.Contains(field.Name, StringComparer.Ordinal)) return false;
            if (field.Name == "text")
            {
                if (field.Value.ValueKind != JsonValueKind.String) return false;
                body = field.Value.GetString();
            }
            else if (booleans.Contains(field.Name, StringComparer.Ordinal))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                values[field.Name] = field.Value.GetBoolean() ? "true" : "false";
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    !PaoScalarInput.TryNormalize(field.Name, field.Value.GetString(), out var value)) return false;
                values[field.Name] = value;
            }
        }
        return true;
    }
}
