using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Metadata and input admission only. No SCM, native calls or elevation.
internal static class WindowsServiceToolCatalog
{
    internal static readonly string[] Fields = ["op", "text", "system", "rid", "detail", "elevate", "wait"];
    internal static string[] FieldsFor(string action) => action == "list"
        ? ["op", "text", "rid", "detail"] : Fields;
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ClosedPaoToolArguments.TryParse(arguments, Fields, ["detail", "wait", "system"], out values, out body) && Valid(values, body, true);
    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!WindowsServiceOperationContract.IsKnownAction(action) ||
            values.Keys.Any(key => !FieldsFor(action!).Contains(key, StringComparer.Ordinal) || key == "text")) return false;
        if (values.TryGetValue("detail", out var detail) && detail is not ("true" or "false" or "True" or "False" or "0" or "1")) return false;
        if (!WindowsServiceOperationContract.IsReadOnly(action) && values.GetValueOrDefault("system")!="true" &&
            values.GetValueOrDefault("elevate") != "uac") return false;
        if (values.TryGetValue("system", out var system) && system is not("true" or "false")) return false;
        if (body is null && requireBody) return false;
        try
        {
            _ = WindowsServiceOperationContract.Parse(new("windows_service",
                values.Where(pair => pair.Key is not ("detail" or "system")).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString(),System:values.GetValueOrDefault("system")=="true"));
            return true;
        }
        catch (AgentOpsException) { return false; }
    }
    internal static object Definition() => new
    {
        name = "pao_windows_service",
        description = "Inspect general local Win32 services with ordinary owner permissions. Exact read/control may explicitly select either the authorized System route or the same-owner one-shot Windows elevation helper. No automatic elevation, remote/driver control, executable arguments, dependency cascade or generic Cortex service mutation.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = WindowsServiceOperationContract.Actions },
                text = new { type = "string", maxLength = WindowsServiceOperationContract.MaximumBodyBytes,
                    description = "Closed version1 JSON selected by the action; no service binary command lines, passwords or caller start arguments." },
                system = new { type = "boolean", description = "Explicit System execution with the connection's active Full control approval; incompatible with elevate and rejected for list." },
                elevate = new { type = "string", @enum = new[] { "uac" }, description = "Explicit same-owner one-shot Windows elevation for exact read/control. RID required; incompatible with system:true and rejected for list." },
                wait = new { type = "boolean", description = "Explicit UAC helper only: false returns the same admitted JID while the original helper continues; ordinary/System service calls retain their existing contract." },
                rid = new { type = "string", maxLength = 128, description = "Required mutation identity; preserve the RID after a lost or uncertain response. Never automatically replay." },
                detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };
    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields)
    {
        var read = WindowsServiceOperationContract.IsReadOnly(action);
        var body = Example(action).ToJsonString();
        var example = new JsonObject { ["op"] = name, ["text"] = body };
        if (!read) { example["system"] = true; example["rid"] = "deliberate-service-change"; }
        return new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields,
            ["required"] = read ? new[] { "text" } : new[] { "text", "rid" },
            ["description"] = action switch
            {
                "list" => "List caller-queryable local Win32 services with bounded name/display-name filtering and advisory paging.",
                "read" => "Read exact service status and safe configuration metadata. configSha pins configuration without disclosing binary command lines.",
                "start" => "Start one stopped service with its exact configuration preimage; no supplied start arguments.",
                "stop" => "Request ordinary SCM stop for the exact observed running service; no dependent cascade or process kill.",
                "restart" => "Stop then start the service; failures after either issued effect remain partial/uncertain.",
                _ => "Set manual, automatic, automatic_delayed or disabled startup. This does not start or stop the service."
            },
            ["executionRoute"] = action == "read" ? "Ordinary owner read by default; system:true with active connection Full control approval selects query-only LocalSystem observation; explicit elevate:uac owns one durable same-owner Windows helper. system:true and elevate are incompatible."
                : read ? "Ordinary signed-in owner host; no durable job or privileged service required."
                : "Explicit system:true with active connection Full control approval through the LocalSystem execution service OR explicit elevate:uac for the same owner's one-shot Windows helper; select exactly one route, with no automatic fallback.",
            ["constraints"] = new[]
            {
                "Strict version1 JSON, depth4, <=8192 UTF8 bytes; literal SCM service name1..256 characters, no slash/backslash/control.",
                "Read results omit executable command lines/arguments. Native inventory may omit services the caller cannot query; bound_reached is not a complete inventory.",
                "Mutations require fresh configSha and expected state; stop/restart also require current pid and canonical UTC start.",
                "Exact read/start/stop/restart/startup may explicitly use elevate:uac with RID. List cannot elevate. Helper payload, exact original High process and callback remain owned until exit; wait affects observation only. Missing reports after payload release remain unknown, never automatically retried.",
                "tmo1..120 seconds, default30. RID is stable request identity; retained or lost-body recovery never reissues a service action.",
                "Use job_stat or job_wait with the same active Full control connection and system:true to observe only these System-engine service mutation JIDs. Observation is metadata-only and never reissues work; wait cancellation ends observation only. Exact original RID/body resubmission remains supported.",
                "Cortex execution-service mutations always require its dedicated Cortex control/maintenance/Exit protocol.",
                "The held SCM service handle is not an atomic compare-and-swap or universal generation lease. No implicit rollback."
            },
            ["mcp_example"] = example,
            ["cli_example"] = "pao windows_service " + action + " --stdin" + (read ? "" : " system=true rid=deliberate-service-change")
        };
    }
    internal static JsonObject Example(string action)
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action == "list") { body["max"] = 64; return body; }
        body["service"] = "Spooler";
        if (action == "read") return body;
        body["configSha"] = new string('0', 64);
        body["expect"] = action == "start" ? "stopped" : "running";
        if (action is "stop" or "restart")
        { body["pid"] = 4242; body["start"] = "2026-11-21T12:00:00.0000000Z"; }
        if (action == "startup") body["mode"] = "manual";
        body["tmo"] = 30;
        return body;
    }
}
