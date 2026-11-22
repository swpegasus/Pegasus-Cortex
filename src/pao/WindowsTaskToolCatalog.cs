using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Catalog/input admission only. No COM connection, task control or elevation.
internal static class WindowsTaskToolCatalog
{
    internal static readonly string[] Fields = ["op", "text", "root", "rid", "wait", "detail", "elevate"];
    internal static string[] FieldsFor(string action) => Fields.Where(field =>
        (field != "wait" || !WindowsTaskOperationContract.IsReadOnly(action) || WindowsTaskOperationContract.IsOwnerElevationAction(action)) &&
        (field != "elevate" || WindowsTaskOperationContract.IsOwnerElevationAction(action)) &&
        (field != "root" || WindowsTaskOperationContract.IsSystemAction(action))).ToArray();

    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body) =>
        ClosedPaoToolArguments.TryParse(arguments, Fields, ["detail", "wait"], out values, out body) && Valid(values, body, true);

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!WindowsTaskOperationContract.IsKnownAction(action) ||
            values.Keys.Any(key => !FieldsFor(action!).Contains(key, StringComparer.Ordinal) || key == "text") ||
            values.TryGetValue("detail", out var detail) && detail is not ("true" or "false" or "True" or "False" or "0" or "1") ||
            values.TryGetValue("root", out var root) && (root.Length != 4 || !root.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')) ||
            body is null && requireBody) return false;
        try
        {
            _ = WindowsTaskOperationContract.Parse(new("windows_task",
                values.Where(pair => pair.Key is not ("detail" or "root")).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Root: values.GetValueOrDefault("root"), Body: body ?? Example(action!).ToJsonString()));
            return true;
        }
        catch (WindowsTaskContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_windows_task",
        description = "Inspect local Windows scheduled tasks and control an exact pinned existing task. Ordinary-owner authoring uses one admitted registered User tool, InteractiveToken/least privilege, initially disabled. Explicit root selects System; elevate:uac separately selects same-owner one-shot UAC for exact existing-task reads/controls. No remote Scheduler, supplied principal/password/XML, arbitrary COM or privilege fallback.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = WindowsTaskOperationContract.Actions },
                text = new { type = "string", maxLength = WindowsTaskOperationContract.MaximumBodyBytes, description = "Closed version1 task JSON. Task paths are Scheduler namespace names, not filesystem roots. argv must be nonsecret." },
                root = new { type = "string", pattern = "^[A-Z0-9]{4}$", description = "Optional explicit authorized System route for existing tasks only; refused for prepare/create/update." },
                elevate = new { type = "string", @enum = new[] { "uac" }, description = "Explicit same-owner one-shot UAC for exact existing-task read/instances/control. RID required, incompatible with root; no alternate account or fallback." },
                rid = new { type = "string", maxLength = 128, description = "Required stable mutation identity. Preserve after uncertain response; never automatically repeat a task effect." },
                wait = new { type = "boolean", description = "Mutations or explicit UAC helpers only: false returns the same admitted JID while the original owner continues." },
                detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields)
    {
        var read = WindowsTaskOperationContract.IsReadOnly(action);
        var example = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() };
        if (!read) example["rid"] = "deliberate-task-change";
        return new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = read ? new[] { "text" } : new[] { "text", "rid" },
            ["description"] = action switch
            {
                "list" => "Page one exact folder's tasks or folders, with a bounded advisory path anchor; no recursive inventory.",
                "read" => "Read safe definition/principal/status summaries and private definition/security preimage digests; never raw XML, action argv or SDDL.",
                "instances" => "List instances visible at the caller's security context. Empty is not proof that no higher-context task is running.",
                "prepare" => "Admit the current registered User tool without creating a task; return the required definition version/digest pin.",
                "run" => "Request a pinned enabled demand-start task with no caller parameters, user/session override or flexible run flags. This does not prove work completion.",
                "stop" => "Stop exactly one pinned native running instance GUID. Never task-wide stop or process kill.",
                "enable" => "Enable a pinned task, permitting its future triggers; this may make configured work eligible to run.",
                "disable" => "Disable a pinned task's future starts; this does not stop its current instances.",
                "remove" => "Delete a pinned existing task registration; this does not prove current instances stopped.",
                "create" => "Create-only a disabled, fixed owner/least-privilege task in an existing folder from a pinned registered tool.",
                _ => "Update-only a supported owner/least-privilege task against its exact preimage; leave the replacement disabled."
            },
            ["executionRoute"] = action is "prepare" or "create" or "update"
                ? "Ordinary signed-in owner host only. No System or same-owner administrator task-authoring route."
                : WindowsTaskOperationContract.IsOwnerElevationAction(action)
                    ? "Ordinary owner by default; explicit root uses System; explicit elevate:uac uses the same owner's one-shot UAC helper. Root and elevate cannot be combined. No identity or privilege fallback."
                    : "Ordinary owner by default; explicit root uses the authenticated System service for existing-task listing. No automatic elevation or identity fallback.",
            ["constraints"] = new[]
            {
                "Strict version1 JSON, <=16384 UTF8 bytes, depth5, no unknown/duplicate fields. Literal Scheduler path <=240 UTF16 characters/depth32; no traversal.",
                "list folder defaults to \\, kind tasks|folders, max1..64; inspected entries <=4096, page<=256KiB. Fresh collection/anchor is advisory, not a snapshot.",
                "read/instances require task. Existing mutations require fresh taskSha; stop also exact canonical instance GUID. Mutation RID is stable and retained bodies are omitted; recovery never reissues lost task actions.",
                "prepare/create/update require tool, separate nonsecret argv, explicit environmentPolicy:scheduler and the registered cwd policy; create/update require toolVersion+toolSha, update also taskSha.",
                "schedule kind on_demand|once|daily; timed schedules require canonical UTC startUtc, daily daysInterval1..365. No raw action/XML/SDDL/password/remote server/principal/highest input.",
                "Image/hash admission applies at registration time only. Future Scheduler execution does not use Cortex's per-run hash, environment replacement, timeout, output capture or child ownership. Percent/environment and $(Arg...) expansions are refused.",
                "One original fixed Task Scheduler COM worker is owned through native return and cleanup. Native HRESULTs remain HRESULTs; cancellation after issue/lost acknowledgement stays observed or unknown, with no compensation/replay.",
                "Explicit elevate:uac requires RID, including read/instances, and owns one durable helper job until the original same-owner High child exits. wait may select deferred observation. No UAC for list/prepare/create/update, no alternate credentials, System service or persistent administrator cache.",
                "taskSha is a cooperative definition/security/principal precondition, not an atomic OS compare-and-swap. Local Scheduler timestamps retain local/DST ambiguity rather than inventing UTC.",
                "job_stat/job_wait with the same explicit root can observe only System service/task-control mutation JIDs. Observer cancellation never cancels or reissues the original effect."
            },
            ["mcp_example"] = example,
            ["cli_example"] = "pao windows_task " + action + " --stdin" + (read ? "" : " rid=deliberate-task-change")
        };
    }

    internal static JsonObject Example(string action)
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action == "list") { body["folder"] = "\\"; body["kind"] = "tasks"; body["max"] = 32; return body; }
        if (action != "prepare") body["task"] = "\\Pegasus\\Example";
        if (action is "read" or "instances") return body;
        if (action is not ("prepare" or "create")) body["taskSha"] = new string('0', 64);
        if (action == "stop") body["instance"] = "00000000-0000-0000-0000-000000000001";
        if (action is "prepare" or "create" or "update")
        {
            body["tool"] = "example"; body["argv"] = new JsonArray(); body["environmentPolicy"] = "scheduler";
            body["cwd"] = "C:\\Work";
            if (action != "prepare") { body["toolVersion"] = 1; body["toolSha"] = new string('0', 64); body["schedule"] = new JsonObject { ["kind"] = "on_demand" }; }
        }
        return body;
    }
}
