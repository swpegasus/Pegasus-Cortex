using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class WindowsDeviceToolCatalog
{
    internal static readonly string[] Fields = ReadOnlyInventoryToolArguments.Fields;
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ReadOnlyInventoryToolArguments.TryParse(arguments, out values, out body) && Valid(values, body, requireBody: true);

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!WindowsDeviceOperationContract.IsKnownAction(action) ||
            !ReadOnlyInventoryToolArguments.ValidMetadata(values, body, requireBody)) return false;
        try
        {
            _ = WindowsDeviceOperationContract.Parse(new("windows_device",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString()));
            return true;
        }
        catch (WindowsDeviceContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_windows_device",
        description = "Read bounded local Windows device and registered driver metadata. No installation, enable/disable, eject, file execution, remote computer or arbitrary COM activation.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = WindowsDeviceOperationContract.Actions },
                text = new { type = "string", maxLength = WindowsDeviceOperationContract.MaximumBodyBytes,
                    description = "Closed version1 list body with scope/limit/afterInstanceId, or read body with exact instanceId." },
                rid = new { type = "string", maxLength = 128 }, detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields) =>
        new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = new[] { "text" },
            ["description"] = action == "list" ? "List a bounded page of local Windows devices and selected registered driver metadata."
                : "Read selected metadata for one exact local Windows device instance.",
            ["executionRoute"] = "Existing ordinary signed-in owner Host; no System/UAC, alternate user, remote provider or durable observation job.",
            ["constraints"] = new[]
            {
                "Closed version1 JSON, <=4096 UTF8 bytes; no workspace, working directory, Root, wait, wildcards or provider selection.",
                "List scope present(default) or all; limit1..64/default32; exact returned afterInstanceId anchor in a fresh live info set.",
                "Pages are live observations, not a shared snapshot. Topology changes can invalidate an anchor; enumeration/byte bounds and partial visibility remain explicit.",
                "Instance IDs are opaque identifiers, not file paths. Missing/unsupported/oversized properties retain fixed item-level issues.",
                "Driver metadata describes registrations, not loaded files, signature validity, installation success or hardware health. No hardware change is performed."
            },
            ["mcp_example"] = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() },
            ["cli_example"] = "pao windows_device " + action + " --stdin"
        };

    private static JsonObject Example(string action)
    {
        var value = new JsonObject { ["version"] = 1 };
        if (action == "list") { value["scope"] = "present"; value["limit"] = 32; }
        else value["instanceId"] = @"ROOT\EXAMPLE\0000";
        return value;
    }
}
