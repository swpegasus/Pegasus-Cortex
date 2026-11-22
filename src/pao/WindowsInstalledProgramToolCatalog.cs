using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class WindowsInstalledProgramToolCatalog
{
    internal static readonly string[] Fields = ReadOnlyInventoryToolArguments.Fields;
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ReadOnlyInventoryToolArguments.TryParse(arguments, out values, out body) && Valid(values, body, requireBody: true);

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!WindowsInstalledProgramOperationContract.IsKnownAction(action) ||
            !ReadOnlyInventoryToolArguments.ValidMetadata(values, body, requireBody)) return false;
        try
        {
            _ = WindowsInstalledProgramOperationContract.Parse(new("installed_program",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString()));
            return true;
        }
        catch (WindowsInstalledProgramContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_installed_program",
        description = "Read bounded metadata from fixed local Windows Uninstall registration roots. A registration is not verified installation or health. No uninstall commands, repair, installation, executable launch, remote registry, elevation or identity fallback.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = WindowsInstalledProgramOperationContract.Actions },
                text = new { type = "string", maxLength = WindowsInstalledProgramOperationContract.MaximumBodyBytes,
                    description = "Closed version1 JSON with explicit machine32/machine64/owner32/owner64 source; list limit/cursor or exact read key/generation." },
                rid = new { type = "string", maxLength = 128 }, detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields) =>
        new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = new[] { "text" },
            ["description"] = action == "list"
                ? "List a bounded page of Windows Uninstall registrations from one explicit local source and view."
                : "Read only safe metadata for one exact Windows Uninstall registration, optionally pinned to its observed generation.",
            ["executionRoute"] = "Existing ordinary signed-in owner Host and admitted registry binding; no System, UAC, remote registry or durable observation job.",
            ["constraints"] = new[]
            {
                "Strict version1 JSON, <=8192 UTF8 bytes, closed fields and duplicate rejection.",
                "Explicit source machine32/machine64/owner32/owner64; fixed Uninstall roots, no arbitrary key path or another account.",
                "List limit1..64/default32. Continuation is bound to source/root generation; a changed enumeration is not a snapshot.",
                "Only name/version/publisher/install-date/estimated-size metadata. No commands, URLs, icon paths, install paths or environment expansion.",
                "Presence is registration metadata, not verified files, product health, uninstall capability or permission to execute.",
                "Partial means limited observation visibility; it is not an issued effect or unknown mutation."
            },
            ["mcp_example"] = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() },
            ["cli_example"] = "pao installed_program " + action + " --stdin"
        };

    private static JsonObject Example(string action)
    {
        var result = new JsonObject { ["version"] = 1, ["source"] = "machine64" };
        if (action == "list") result["limit"] = 32;
        else result["key"] = "example-registration";
        return result;
    }
}
