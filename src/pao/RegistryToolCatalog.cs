using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

// One frontend contract for both the unified MCP route and the legacy typed
// CLI. Resource/value validation stays in the engine's pure registry parser.
internal static class RegistryToolCatalog
{
    internal static readonly string[] Fields = ["op", "text", "rid", "detail"];
    internal static string[] Required(string action) => RegistryOperationContract.IsReadOnly(action)
        ? ["text"] : ["text", "rid"];

    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body)
    {
        values = new(StringComparer.Ordinal);
        body = null;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !Fields.Contains(property.Name, StringComparer.Ordinal)) return false;
            if (property.Name == "text")
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                body = property.Value.GetString();
            }
            else if (property.Name == "detail")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                values["detail"] = property.Value.GetBoolean() ? "true" : "false";
            }
            else
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !PaoScalarInput.TryNormalize(property.Name, property.Value.GetString(), out var value)) return false;
                values[property.Name] = value;
            }
        }
        return Valid(values, body, requireBody: true);
    }

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        if (values.Keys.Any(key => key is not ("op" or "rid" or "detail")) ||
            !RegistryOperationContract.IsKnownAction(values.GetValueOrDefault("op"))) return false;
        var action = values["op"];
        if (!RegistryOperationContract.IsReadOnly(action) &&
            (!values.TryGetValue("rid", out var rid) || string.IsNullOrWhiteSpace(rid))) return false;
        if (values.TryGetValue("detail", out var detail) && detail is not ("true" or "false" or "True" or "False" or "0" or "1")) return false;
        if (body is null) return !requireBody;
        try
        {
            var request = new OperationRequest("registry",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Rid: values.GetValueOrDefault("rid"), Body: body);
            _ = RegistryOperationContract.Parse(request);
            return true;
        }
        catch (RegistryContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_registry",
        description = "Inspect, browse, read or change one exact local registry resource through the ordinary owner host. Writes require a fresh preimage; results do not promise atomic compare-and-swap. No elevation or service fallback.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = RegistryOperationContract.Actions },
                text = new { type = "string", maxLength = RegistryOperationContract.MaxRequestBytes, description = "Strict version1 JSON body; exact key hive, view and keyPath; explicit valueName including empty default name." },
                rid = new { type = "string", maxLength = 128, description = "Required mutation idempotency identity; preserve it after an uncertain response." },
                detail = new { type = "boolean", description = "Include bounded diagnostic detail." }
            },
            required = new[] { "op", "text" },
            additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string,object?> Help(string name,string action,string[] fields)
    {
        var zero = new string('0', 64);
        var body = new JsonObject
        {
            ["version"] = 1,
            ["key"] = new JsonObject { ["hive"] = "current_user", ["view"] = 64, ["keyPath"] = @"Software\Example" }
        };
        if (action is "read" or "set" or "delete_value") body["valueName"] = "";
        if (action == "browse") { body["kind"] = "values"; body["limit"] = 32; }
        if (action == "create") body["parentGeneration"] = zero;
        if (action == "delete_empty_key") body["keyGeneration"] = zero;
        if (action == "set") body["value"] = new JsonObject { ["type"] = "binary", ["base64"] = "AQ==" };
        if (action is "set" or "delete_value")
            body["preimage"] = new JsonObject { ["keyGeneration"] = zero, ["valueDigest"] = action == "set" ? null : JsonValue.Create(zero) };
        var input = body.ToJsonString();
        _ = RegistryOperationContract.Parse(action, input); // Examples obey the same parser as execution.
        var example = new JsonObject { ["op"] = name, ["text"] = input };
        if (!RegistryOperationContract.IsReadOnly(action)) example["rid"] = "deliberate-new-request";
        var display = new JsonSerializerOptions { WriteIndented = true };
        var selected = action switch
        {
            "inspect" => "Inspect one exact key and obtain its generation; no registry writes.",
            "browse" => $"Browse direct child keys or values: kind=keys|values, optional limit1..{RegistryLimits.MaxPageEntries}, and an unchanged returned cursor. Changed generations refuse continuation.",
            "read" => "Read one exact value. valueName is required; an empty name selects the default value. Returns raw typed bytes and the observed generation/digest; no registry writes.",
            "create" => "Create one key only when parentGeneration matches a fresh exact parent observation. No recursive parent creation.",
            "set" => "Set one exact value using value={type,base64} and preimage={keyGeneration,valueDigest}. Null valueDigest requires an absent value; otherwise use its exact observed digest.",
            "delete_value" => "Delete one exact value only with preimage={keyGeneration,valueDigest} from a fresh observation. Both generation and value digest are required.",
            "delete_empty_key" => "Delete one empty key only when keyGeneration still matches. Nonempty keys are refused; deletion never recurses.",
            _ => throw new ArgumentException("registry_operation_unknown")
        };
        return new Dictionary<string,object?>
        {
            ["name"] = name, ["required"] = Required(action), ["allowed"] = fields,
            ["description"] = selected,
            ["mcp_example"] = example.ToJsonString(display),
            ["cli_example"] = $"pao registry {action} --stdin" + (RegistryOperationContract.IsReadOnly(action) ? "" : " rid=<request-id>") +
                "\nStandard input:\n" + body.ToJsonString(display),
            ["constraints"] = "text is strict version1 JSON, at most 96 KiB: key={hive:current_user|local_machine|users,view:32|64,keyPath:exact}. " +
                selected + " Ordinary owner context only; no root, workspace or deferred completion. " +
                (RegistryOperationContract.IsReadOnly(action) ? "Reads are nondurable snapshots."
                    : "Mutation examples use zero placeholders: replace them with fresh observed generations/digests. A RID is required; no automatic replay or atomic compare-and-swap is promised."),
        };
    }
}
