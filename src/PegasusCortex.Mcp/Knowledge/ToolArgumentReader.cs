using System.Text.Json;

namespace Pegasus.Cortex.Mcp.Knowledge;

internal static class ToolArgumentReader
{
    public static bool Validate(
        JsonElement? arguments,
        IReadOnlySet<string> allowedNames,
        out string? error)
    {
        error = null;
        if (arguments is null)
        {
            if (allowedNames.Count == 0)
                return true;
            error = "Tool arguments must be a JSON object.";
            return false;
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            error = "Tool arguments must be a JSON object.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name) ||
                !seen.Add(property.Name))
            {
                error =
                    "Tool arguments contain an unsupported or duplicate property.";
                return false;
            }
        }

        return true;
    }

    public static bool ContainsUniqueObjectProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name) ||
                        !ContainsUniqueObjectProperties(property.Value))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
                return value.EnumerateArray().All(ContainsUniqueObjectProperties);
            default:
                return true;
        }
    }

    public static bool RequiredString(
        JsonElement values,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property))
        {
            value = null;
            error = $"The {name} argument is required.";
            return false;
        }

        return TryString(property, name, maximumLength, out value, out error);
    }

    public static bool OptionalString(
        JsonElement values,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property))
        {
            value = null;
            error = null;
            return true;
        }

        return TryString(property, name, maximumLength, out value, out error);
    }

    public static bool RequiredContentString(
        JsonElement values,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            value = null;
            error = $"The {name} argument is required and must be JSON text.";
            return false;
        }
        value = property.GetString();
        if (value is null || value.Length > maximumLength)
        {
            value = null;
            error = $"The {name} argument exceeds the supported text boundary.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool OptionalContentString(
        JsonElement values,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property))
        {
            value = null;
            error = null;
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            value = null;
            error = $"The {name} argument must be JSON text.";
            return false;
        }
        value = property.GetString();
        if (value is null || value.Length > maximumLength)
        {
            value = null;
            error = $"The {name} argument exceeds the supported text boundary.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool OptionalStringArray(
        JsonElement values,
        string name,
        int maximumItems,
        int maximumItemLength,
        out IReadOnlyList<string>? items,
        out string? error)
    {
        if (!values.TryGetProperty(name, out _))
        {
            items = null;
            error = null;
            return true;
        }
        var valid = RequiredStringArray(
            values, name, 0, maximumItems, maximumItemLength,
            out var required, out error);
        items = valid ? required : null;
        return valid;
    }

    public static bool OptionalInt32(
        JsonElement values,
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        out int value,
        out string? error)
    {
        value = defaultValue;
        if (!values.TryGetProperty(name, out var property))
        {
            error = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out value) ||
            value < minimum ||
            value > maximum)
        {
            error = $"The {name} argument is outside the supported integer range.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool RequiredInt64(
        JsonElement values,
        string name,
        long minimum,
        long maximum,
        out long value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out value) ||
            value < minimum ||
            value > maximum)
        {
            value = default;
            error = $"The {name} argument is required and must be inside the supported integer range.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool OptionalBoolean(
        JsonElement values,
        string name,
        out bool value,
        out string? error)
    {
        if (!values.TryGetProperty(name, out var property))
        {
            value = false;
            error = null;
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            value = false;
            error = $"The {name} argument must be a boolean.";
            return false;
        }

        value = property.GetBoolean();
        error = null;
        return true;
    }

    public static bool RequiredStringArray(
        JsonElement values,
        string name,
        int minimumItems,
        int maximumItems,
        int maximumItemLength,
        out IReadOnlyList<string> items,
        out string? error)
    {
        items = [];
        if (!values.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            error = $"The {name} argument is required and must be an array.";
            return false;
        }

        var captured = new List<string>(maximumItems);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        error = null;
        foreach (var item in property.EnumerateArray())
        {
            if (captured.Count == maximumItems ||
                !TryString(item, name, maximumItemLength, out var value, out error) ||
                !seen.Add(value!))
            {
                error ??= $"The {name} argument contains too many or duplicate values.";
                return false;
            }
            captured.Add(value!);
        }
        if (captured.Count < minimumItems)
        {
            error = $"The {name} argument contains too few values.";
            return false;
        }

        items = Array.AsReadOnly(captured.ToArray());
        error = null;
        return true;
    }

    public static bool IsKnowledgeIdentifier(string value)
    {
        var prefixLength = value.StartsWith("krec_", StringComparison.Ordinal) ||
            value.StartsWith("krev_", StringComparison.Ordinal)
            ? 5
            : 0;
        return prefixLength != 0 && value.Length == prefixLength + 64 &&
            value.AsSpan(prefixLength).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static bool TryString(
        JsonElement property,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        value = property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            error = $"The {name} argument must be a non-empty bounded string without control characters.";
            value = null;
            return false;
        }

        error = null;
        return true;
    }
}
