using System.Text.Json;

// Shared bounded op/text/rid/detail envelope. Each owning contract validates
// its complete body and whether the operation is passive inventory or active
// diagnosis; sharing the envelope does not grant execution or identity rights.
internal static class ReadOnlyInventoryToolArguments
{
    internal static readonly string[] Fields = ["op", "text", "rid", "detail"];

    internal static bool TryParse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ClosedPaoToolArguments.TryParse(arguments, Fields, ["detail"], out values, out body);

    internal static bool ValidMetadata(IReadOnlyDictionary<string, string> values, string? body, bool requireBody) =>
        !values.Keys.Any(key => !Fields.Contains(key, StringComparer.Ordinal) || key == "text") &&
        (!values.TryGetValue("detail", out var detail) || detail is "true" or "false" or "True" or "False" or "0" or "1") &&
        (body is not null || !requireBody);
}
