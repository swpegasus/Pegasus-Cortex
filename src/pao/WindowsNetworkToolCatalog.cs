using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class WindowsNetworkToolCatalog
{
    internal static readonly IReadOnlyList<string> Actions = Array.AsReadOnly(
        WindowsNetworkOperationContract.Actions.Append(WindowsNetworkResolutionContract.Action).ToArray());
    internal static readonly string[] Fields = ReadOnlyInventoryToolArguments.Fields;
    internal static bool IsKnownAction(string? action) =>
        WindowsNetworkOperationContract.IsKnownAction(action) || action == WindowsNetworkResolutionContract.Action;
    internal static string PublicName(string action) => IsKnownAction(action)
        ? "windows_network_" + action : throw new ArgumentException("windows_network_request_invalid");
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ReadOnlyInventoryToolArguments.TryParse(arguments, out values, out body) && Valid(values, body, requireBody: true);

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!IsKnownAction(action) ||
            !ReadOnlyInventoryToolArguments.ValidMetadata(values, body, requireBody)) return false;
        try
        {
            var request = new OperationRequest("windows_network",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString());
            if (action == WindowsNetworkResolutionContract.Action)
                _ = WindowsNetworkResolutionContract.Parse(request);
            else _ = WindowsNetworkOperationContract.Parse(request);
            return true;
        }
        catch (WindowsNetworkContractException) { return false; }
        catch (WindowsNetworkResolutionContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_windows_network",
        description = "Observe bounded local adapter/address/DNS-server configuration, IPv4/IPv6 routes and TCP/UDP endpoints, or explicitly resolve one A/AAAA DNS name. No remote scan or configuration changes.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = Actions },
                text = new { type = "string", maxLength = WindowsNetworkOperationContract.MaximumBodyBytes,
                    description = "Closed version1 JSON: list/read selects a local inventory view; resolve selects one name, A/AAAA record type and cancellation-request timeout." },
                rid = new { type = "string", maxLength = 128 }, detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields) =>
        action == WindowsNetworkResolutionContract.Action ? ResolutionHelp(name, fields) : new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = new[] { "text" },
            ["description"] = action == "list" ? "List a bounded page from one explicit local network inventory view."
                : "Reobserve one exact row key in a selected local network view.",
            ["executionRoute"] = "Existing ordinary signed-in owner Host; no System/UAC, remote computer, alternate identity or durable read job.",
            ["constraints"] = new[]
            {
                "Closed version1 JSON with view adapters/routes/connections. List limit1..64/default32 and returned afterKey; read exact key.",
                "Row keys identify normalized observed coordinates, not snapshot generations, permissions or verified process identities. Every request reobserves live local state.",
                "Identical coordinate/data rows retain their occurrence count. Conflicting metadata is omitted with an explicit partial issue; no arbitrary representative supplies those fields.",
                "DNS entries are configured server addresses, not successful resolution or reachability. UDP entries are endpoints with no invented remote peer or connection state.",
                "Native table/row/property/page limits and incomplete source coverage remain explicit. The route API allocates its OS-owned table before its row count can be checked; managed copying is bounded and the table is always released.",
                "No shell commands, traffic probes, configuration writes or implicit identity/provider fallback. A route or connection observation is not connectivity or account authentication proof."
            },
            ["mcp_example"] = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() },
            ["cli_example"] = "pao windows_network " + action + " --stdin"
        };

    private static JsonObject Example(string action)
    {
        if (action == WindowsNetworkResolutionContract.Action)
            return new JsonObject { ["version"] = 1, ["name"] = "example.com", ["recordType"] = "A", ["timeoutMs"] = 5000 };
        var value = new JsonObject { ["version"] = 1, ["view"] = "adapters" };
        if (action == "list") value["limit"] = 32;
        else value["key"] = new string('0', 64);
        return value;
    }

    private static IReadOnlyDictionary<string, object?> ResolutionHelp(string name, string[] fields) =>
        new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = new[] { "text" },
            ["description"] = "Issue one explicit A/AAAA DNS query through the configured Windows resolver.",
            ["executionRoute"] = "Existing ordinary signed-in owner Host; active network I/O, no System/UAC, alternate identity, durable job or replay.",
            ["constraints"] = new[]
            {
                "Closed version1 JSON: ASCII DNS name, recordType A or AAAA, optional timeoutMs100..30000/default5000. No caller-selected server, interface, provider or flags.",
                "Uses an absolute name, configured Windows DNS servers and fixed unicast wire-only options; no hosts/cache/local-name fallback or manual follow-up query.",
                "At most two original queries per provider. Busy refuses immediately. Timeout/cancellation requests native cancellation and retains ownership until the original callback and entered cancel call settle; no guaranteed native-return deadline.",
                "Answer traversal and serialized output are bounded. Windows allocates record memory before caller admission; no hard native preallocation cap is claimed.",
                "queryIssued proves API entry, not observed packets, reachability, remote authentication or general network health. Results are ephemeral; no DNS configuration changes or implicit retry."
            },
            ["mcp_example"] = new JsonObject { ["op"] = name, ["text"] = Example(WindowsNetworkResolutionContract.Action).ToJsonString() },
            ["cli_example"] = "pao windows_network resolve --stdin"
        };
}
