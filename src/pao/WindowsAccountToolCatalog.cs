using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class WindowsAccountToolCatalog
{
    internal static readonly string[] Fields = ReadOnlyInventoryToolArguments.Fields;
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ReadOnlyInventoryToolArguments.TryParse(arguments, out values, out body) && Valid(values, body, requireBody: true);

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!WindowsAccountOperationContract.IsKnownAction(action) ||
            !ReadOnlyInventoryToolArguments.ValidMetadata(values, body, requireBody)) return false;
        try
        {
            _ = WindowsAccountOperationContract.Parse(new("windows_account",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString()));
            return true;
        }
        catch (WindowsAccountContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_windows_account",
        description = "Read bounded local Windows users, local groups, one local group's member SIDs, Windows sessions or the current Host identity. OS observations are separate from Cortex/Nexus identities. No account, group or session changes.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = WindowsAccountOperationContract.Actions },
                text = new { type = "string", maxLength = WindowsAccountOperationContract.MaximumBodyBytes,
                    description = "Closed version1 JSON: view users/groups/members/sessions/current. group required only for members. list uses limit/afterKey; read uses an exact returned key." },
                rid = new { type = "string", maxLength = 128 }, detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields) =>
        new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = new[] { "text" },
            ["description"] = action == "list" ? "List a bounded page from one local Windows account/session view."
                : "Reobserve one exact key in the same local Windows account/session view.",
            ["executionRoute"] = "Existing ordinary signed-in owner Host. No System/UAC, remote computer, alternate identity or durable read job.",
            ["constraints"] = new[]
            {
                "Closed version1 JSON with users/groups/members/sessions/current. members requires one exact local group name; other views reject group. List limit1..64/default32 and returned afterKey; read exact key.",
                "Every request reobserves local state. Keys are observation coordinates, not permissions, token grants, account/session generations or guarantees that a target still exists.",
                "Windows local accounts/groups and interactive sessions remain distinct from Cortex clients, Nexus identities and terminal-manager sessions. SAM views require a verified local workstation/member-server role and refuse domain controllers. Domain members are returned as SIDs without account-name or domain-controller resolution.",
                "No password, token, home/profile path or client-address request. Windows' user-information buffer includes full-name/comment pointers; they are never decoded or returned. No creation, removal, membership change, logoff, disconnect, remote server or credential fallback.",
                "Native buffers are released deterministically. User/group NetAPI page sizes are preferences, not hard pre-allocation caps. Membership and WTS allocate whole native results before count admission; managed rows, calls and output are bounded. Incomplete observations are explicit.",
                "Denied or incomplete source coverage cannot prove absence. Synchronous Windows calls are joined and cancellation checked around them; no forced abort or implicit elevation."
            },
            ["mcp_example"] = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() },
            ["cli_example"] = "pao windows_account " + action + " --stdin"
        };

    private static JsonObject Example(string action)
    {
        var value = new JsonObject { ["version"] = 1, ["view"] = "users" };
        if (action == "list") value["limit"] = 32;
        else value["key"] = new string('0', 64);
        return value;
    }
}
