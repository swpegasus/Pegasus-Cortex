using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ConnectionProfileToolCatalog
{
    internal static string[] FieldsFor(string action) => ConnectionProfileOperationContract.IsReadOnly(action)
        ? ["op", "text", "rid", "detail"] : ["op", "text", "rid", "wait", "detail"];
    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body)
    {
        values = new(StringComparer.Ordinal); body = null;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
        {
            if (!seen.Add(field.Name) || field.Name is not ("op" or "text" or "rid" or "wait" or "detail")) return false;
            if (field.Name == "text")
            { if (field.Value.ValueKind != JsonValueKind.String) return false; body = field.Value.GetString(); }
            else if (field.Name is "wait" or "detail")
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                values[field.Name] = field.Value.GetBoolean() ? "true" : "false";
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    !PaoScalarInput.TryNormalize(field.Name, field.Value.GetString(), out var normalized)) return false;
                values[field.Name] = normalized;
            }
        }
        return Valid(values, body, true);
    }

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!ConnectionProfileOperationContract.IsKnownAction(action) ||
            values.Keys.Any(key => !FieldsFor(action!).Contains(key, StringComparer.Ordinal) || key == "text")) return false;
        if (values.TryGetValue("detail", out var detail) && detail is not ("true" or "false" or "True" or "False" or "0" or "1")) return false;
        if (body is null && requireBody) return false;
        try
        {
            _ = ConnectionProfileOperationContract.Parse(new("connection_profile",
                values.Where(pair => pair.Key != "detail").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                values.GetValueOrDefault("rid"), Body: body ?? Example(action!).ToJsonString()));
            return true;
        }
        catch (ConnectionProfileContractException) { return false; }
    }

    internal static object Definition() => new
    {
        name = "pao_connection_profile",
        description = "Manage nonsecret saved SMB drive profiles and explicitly inspect/connect/disconnect a pinned profile. Ordinary original owner/logon only; temporary mapping, fixed Windows SMB provider, no System/UAC/identity fallback, raw password, automatic reconnect or forced disconnect.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = ConnectionProfileOperationContract.Actions },
                text = new { type = "string", maxLength = ConnectionProfileOperationContract.MaximumBodyBytes,
                    description = "Closed nonsecret version1/kind=smb-drive JSON. Explicit remote credentials use only an opaque SMB-purpose passwordReference." },
                rid = new { type = "string", maxLength = 128, description = "Required stable mutation identity. Observe original job after uncertain delivery; never automatically repeat." },
                wait = new { type = "boolean", description = "Mutations only; false acknowledges durable admission, not completion." },
                detail = new { type = "boolean" }
            },
            required = new[] { "op", "text" }, additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields)
    {
        var read = ConnectionProfileOperationContract.IsReadOnly(action);
        var example = new JsonObject { ["op"] = name, ["text"] = Example(action).ToJsonString() };
        if (!read) example["rid"] = "deliberate-profile-operation";
        return new Dictionary<string, object?>
        {
            ["name"] = name, ["allowed"] = fields, ["required"] = read ? new[] { "text" } : new[] { "text", "rid" },
            ["description"] = action switch
            {
                "list" => "List a bounded page of saved nonsecret SMB profiles; no connection or credential access.",
                "read" => "Read one saved SMB profile with its exact revision and content hash, including missing/tombstone state.",
                "save" => "Create or update a nonsecret SMB profile under its expected revision. Saving never connects.",
                "remove" => "Remove a saved profile under its expected revision, retaining a compact revision tombstone. Does not disconnect or delete credentials.",
                "inspect" => "Observe the exact pinned profile's local drive mapping; not remote authentication or share-content verification.",
                "connect" => "Connect an unoccupied local drive using the exact saved profile and selected credential mode, with no default-credential fallback.",
                _ => "Disconnect only the pinned profile's exact observed local-drive/remote-share preimage, without force or cleanup of other sessions."
            },
            ["executionRoute"] = "Existing ordinary signed-in owner Host; original Windows logon for mapping operations. No System, UAC, alternate local identity or automatic fallback.",
            ["constraints"] = new[]
            {
                "Strict closed version1 JSON <=8192 UTF8 bytes; kind is exactly smb-drive, no arbitrary provider or script.",
                "Profiles contain only id/label/UNC server-share root/local drive and explicit currentUser or explicitRemote mode; explicitRemote requires remoteUser and SMB-purpose passwordReference, never a password.",
                "Save expectedRevision0 creates an absent id; positive revision pins update/restore. Remove retains a compact revision tombstone. Cooperative file locking is not OS atomic compare-and-swap against arbitrary owner edits.",
                "Mapping requests pin id/revision/contentHash. Occupied or remembered drives are refused before resolving credentials. No adoption, automatic reconnect, persistent Windows mapping, prompt or saved Windows credential flag.",
                "Cancellation before native entry prevents dispatch; an issued noncancelable native call stays owned until actual completion. No automatic retry or compensating disconnect.",
                "Read responses are ephemeral. Mutating job history/replay keeps compact outcome metadata and request identity, not profile bodies/endpoints/accounts/credential references. Mapping observation never proves a remote account or password."
            },
            ["mcp_example"] = example,
            ["cli_example"] = "pao connection_profile " + action + " --stdin" + (read ? "" : " rid=deliberate-profile-operation")
        };
    }

    private static JsonObject Example(string action)
    {
        var value = new JsonObject { ["version"] = 1, ["kind"] = "smb-drive" };
        if (action == "list") { value["limit"] = 32; return value; }
        if (action == "save")
        {
            value["expectedRevision"] = 0;
            value["profile"] = new JsonObject { ["version"] = 1, ["kind"] = "smb-drive", ["id"] = "shared-files",
                ["label"] = "Shared files", ["remoteShare"] = @"\\fileserver\share", ["localDrive"] = "X:", ["identityMode"] = "currentUser" };
            return value;
        }
        value["id"] = "shared-files";
        if (action == "read") return value;
        if (action == "remove") value["expectedRevision"] = 1;
        else { value["revision"] = 1; value["contentHash"] = new string('0', 64); }
        if (action == "disconnect") value["expectedRemoteShare"] = @"\\fileserver\share";
        return value;
    }
}
