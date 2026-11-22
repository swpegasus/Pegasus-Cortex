using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// CLI and unified MCP reuse the engine's pure parser. No certificate, key,
// provider, file or owner repository is opened by catalog validation/help.
internal static class CertificateToolCatalog
{
    internal static readonly string[] Fields = ["op", "text", "src", "sha", "dst", "ws", "cwd", "rid", "detail"];
    internal static string[] FieldsFor(string action) => CertificateOperationContract.PathFields(action).FirstOrDefault() switch
    {
        "src" => ["op", "text", "src", "sha", "ws", "cwd", "rid", "detail"],
        "dst" => ["op", "text", "dst", "ws", "cwd", "rid", "detail"],
        _ => ["op", "text", "rid", "detail"]
    };
    internal static string[] Required(string action)
    {
        var fields = new List<string> { "text" };
        fields.AddRange(CertificateOperationContract.PathFields(action));
        if (fields.Contains("src", StringComparer.Ordinal)) fields.Add("sha");
        if (!CertificateOperationContract.IsReadOnly(action)) fields.Add("rid");
        return fields.ToArray();
    }

    internal static bool Parse(JsonElement arguments, out Dictionary<string, string> values, out string? body)
    {
        values = new(StringComparer.Ordinal); body = null;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        try
        {
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
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or ArgumentException)
        { values = new(StringComparer.Ordinal); body = null; return false; }
    }

    internal static bool Valid(IReadOnlyDictionary<string, string> values, string? body, bool requireBody)
    {
        var action = values.GetValueOrDefault("op");
        if (!CertificateOperationContract.IsKnownAction(action) ||
            values.Keys.Any(key => key == "text" || !FieldsFor(action!).Contains(key, StringComparer.Ordinal))) return false;
        if (values.TryGetValue("detail", out var detail) && detail is not ("true" or "false" or "True" or "False" or "0" or "1")) return false;
        if (body is null && requireBody) return false;
        try
        {
            // Dictionary admission runs before --stdin is read. A fixed public
            // example validates argument/RID shape without pretending a body is loaded.
            _ = CertificateOperationContract.Parse(Request(values, body ?? ExampleBody(action!).ToJsonString()));
            return true;
        }
        catch (CertificateContractException) { return false; }
    }

    private static OperationRequest Request(IReadOnlyDictionary<string, string> values, string body) => new("certificate",
        values.Where(pair => pair.Key is not ("detail" or "ws" or "cwd"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        Rid: values.GetValueOrDefault("rid"), Ws: values.GetValueOrDefault("ws"), Cwd: values.GetValueOrDefault("cwd"), Body: body);

    internal static object Definition() => new
    {
        name = "pao_certificate",
        description = "Manage exact owner-local certificate references, public imports, protected private files, signatures, serverAuth issuance and explicit store actions. Ordinary store_bind_server binds CurrentUser/My. Explicit issue_machine_server creates a nonexportable machine server key and binds LocalMachine/My through same-owner one-shot UAC; store_admin actions use that same elevation boundary for public material. No execution service, persistent administrator cache or fallback. Private PKCS#12 bytes never enter bodies/results; no raw passwords.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                op = new { type = "string", @enum = CertificateOperationContract.Actions },
                text = new { type = "string", maxLength = CertificateOperationContract.MaxRequestBytes, description = "Strict version1 certificate JSON, at most 16 KiB and depth 8; selected action defines its closed body fields." },
                src = new { type = "string", maxLength = 240, description = "Sign/verify/import_private only: exact input file in a registered workspace." },
                sha = new { type = "string", pattern = "^[a-f0-9]{64}$", description = "Required for every src action: freshly observed input file SHA-256 preimage." },
                dst = new { type = "string", maxLength = 240, description = "export_private only: absent destination in a registered workspace; create-only, no overwrite or payload result." },
                ws = new { type = "string", description = "File actions only: existing registered workspace alias." },
                cwd = new { type = "string", description = "File actions only: working directory resolved under the registered workspace." },
                rid = new { type = "string", maxLength = 128, description = "Required for every mutation, including private export disclosure and all store changes. Preserve after an uncertain response; no automatic replay." },
                detail = new { type = "boolean", description = "Include bounded diagnostic detail." }
            },
            required = new[] { "op", "text" },
            additionalProperties = false
        }
    };

    internal static IReadOnlyDictionary<string, object?> Help(string name, string action, string[] fields)
    {
        var body = ExampleBody(action);
        var input = body.ToJsonString();
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = action };
        if (!CertificateOperationContract.IsReadOnly(action)) values["rid"] = "deliberate-new-request";
        if (CertificateOperationContract.IsFileAction(action))
        {
            var pathField = CertificateOperationContract.PathFields(action).Single();
            values[pathField] = action == "export_private" ? "export.pfx" : action == "import_private" ? "input.pfx" : "sample.exe";
            if (pathField == "src") values["sha"] = new string('0', 64);
            values["ws"] = "example";
        }
        _ = CertificateOperationContract.Parse(Request(values, input));
        var example = new JsonObject { ["op"] = name, ["text"] = input };
        foreach (var pair in values.Where(pair => pair.Key != "op")) example[pair.Key] = pair.Value;
        var selected = action switch
        {
            "create_ca" => "Create one new owner-private local CA reference; subject is a literal common name. Defaults: 365 days and RSA 3072; days 1..3650, keyBits 2048|3072|4096. No trust installation.",
            "issue_server" => "Issue one new serverAuth-only TLS leaf reference from exact issuer={reference,certificateId}. dnsNames/ipAddresses are required arrays with 1..16 combined DNS/IP SANs: ASCII DNS labels only (no wildcard/URI), canonical unscoped IPv4/IPv6, no duplicates. Defaults 30 days/RSA3072, days 1..366 within issuer validity. No store/trust change or connection test.",
            "issue_machine_server" => "Explicitly create one new nonexportable machine RSA server key through same-owner one-shot UAC and bind the signed serverAuth leaf to LocalMachine/My. Issuance fields and SAN bounds match issue_server. The ordinary owner signs the public CSR with the exact selected issuer; private CA and machine key never cross identities. Fixed provider/key owner/store policy, mandatory RID, no overwrite, no trust change, service start or connection test. Lost or issued outcomes are not automatically replayed; a possibly bound key is preserved.",
            "store_bind_server" => "Explicitly bind the exact managed private server leaf to CurrentUser/My only. Body is version/reference/certificateId; mandatory RID. Persists the user key without marking it Exportable and without MachineKeySet, reopens exact SHA256 DER and proves key usability. Public-only conflicts refuse; existing usable binding is unchanged. No trust, machine store, raw key/password/PFX, socket, service or administrator fallback. Unknown persistence is never automatically replayed.",
            "issue_code" => "Issue one new code-signing reference from the exact pinned issuer={reference,certificateId}. Defaults: 30 days and RSA 3072; days 1..366, also bounded by issuer validity. No intermediate CA or arbitrary extension policy.",
            "inspect" => "Inspect validated public certificate material by reference. certificateId is an optional pin, permitting diagnosis after a lost publication reply. Protected-material presence does not prove key decryption or current signing readiness.",
            "export_public" => "Return only the pinned public certificate in explicit pem or der format. No private-key or PKCS#12 export and no implicit destination write.",
            "sign" => "Sign one exact pinned file using the exact reference/certificateId in the ordinary owner identity. Registered-workspace admission and current src/sha preimage are required. Native format/platform support is explicit; signing adds no network timestamp and an uncertain issued attempt is never automatically replayed.",
            "verify" => "Verify one exact src/sha file against certificateId. Registered-workspace admission is required. Signature integrity, offline Windows trust and revocation-not-checked evidence remain distinct; verification makes no trust-store change.",
            "import_public" => "Publish one new reference from one pinned public certificate. format=der uses canonical base64; format=pem accepts one exact CERTIFICATE block. Runtime import must validate the actual certificate and certificateId. No OS-store or trust change.",
            "import_private" => "Import one protected PKCS#12 from a registered-workspace src with exact sha preimage, reference/certificateId and opaque passwordReference. The admitted owner resolves the password privately. No encrypted package, raw key or password in the request body; no store/trust installation.",
            "export_private" => "Explicit private-material disclosure: export the pinned reference to an absent registered-workspace dst using passwordReference. RID is mandatory. Protected PKCS#12 travels only through the create-only file; response contains output identity/count, never encrypted bytes. No overwrite or automatic replay.",
            "store_list" => "Observe one exact Windows store: scope=current_user|local_machine; store=personal|intermediate|root|trusted_publisher|trusted_people. max defaults to 32, bounds 1..128; afterCertificateId is an optional lower 64-hex continuation. Pages are fresh observations and do not promise a stable store generation.",
            "store_add" => "Explicitly add the pinned public reference to current_user personal or intermediate. This action cannot target a trust store and does not install private keys. local_machine writes are refused in this ordinary owner family; no administrative fallback.",
            "store_trust" => "Deliberately change current_user trust using the pinned public reference and exactly root, trusted_publisher or trusted_people. This is distinct from creation/import and store_add. local_machine writes are refused here; a separate administrator workflow is required.",
            "store_remove" => "Deliberately remove only the exact certificateId from the selected current_user store; persisted private-key removal remains unsupported. local_machine writes are refused here. No implicit cleanup, broad deletion or administrative fallback.",
            "store_admin_add" => "Explicitly add the pinned public reference to local_machine personal or intermediate using same-owner one-shot UAC. RID required. No trust-store target, execution service, persistent administrator cache or fallback; another account's elevation is refused.",
            "store_admin_trust" => "Deliberately change local_machine root, trusted_publisher or trusted_people trust using the exact pinned public reference and same-owner one-shot UAC. RID required. This is separate from ordinary store_add/trust; no service, administrator cache or fallback.",
            "store_admin_remove" => "Deliberately remove only the exact public certificateId from one local_machine store through same-owner one-shot UAC. RID required; private-key removal remains unsupported. No service, persistent administrator cache, broad cleanup or fallback.",
            _ => throw new ArgumentException("certificate_operation_unknown")
        };
        var display = new JsonSerializerOptions { WriteIndented = true };
        return new Dictionary<string, object?>
        {
            ["name"] = name, ["required"] = Required(action), ["allowed"] = fields, ["description"] = selected,
            ["mcp_example"] = example.ToJsonString(display),
            ["cli_example"] = $"pao certificate {action} --stdin" +
                (CertificateOperationContract.PathFields(action).Contains("src", StringComparer.Ordinal)
                    ? " src=" + (action == "import_private" ? "input.pfx" : "sample.exe") + " ws=example sha=<observed-file-sha256>"
                    : action == "export_private" ? " dst=export.pfx ws=example" : "") +
                (CertificateOperationContract.IsReadOnly(action) ? "" : " rid=<request-id>") + "\nStandard input:\n" + body.ToJsonString(display),
            ["constraints"] = $"All {CertificateOperationContract.Actions.Count} actions require strict version1 JSON, at most 16 KiB and depth 8. References/passwordReference use 1..64 lowercase ASCII letters/digits/dot/underscore/hyphen and start with a letter/digit; pins are lowercase 64-hex. " +
                selected + " No root argument, service fallback, deferred completion, raw passwords or private payload in bodies/results. " +
                (CertificateOperationContract.RequiresOwnerElevation(action) ? "This explicit action uses same-owner one-shot UAC only; no service or persistent administrator cache. " : "Ordinary owner execution refuses machine writes. ") +
                "Example IDs, hashes and public certificate content are syntax placeholders: replace them with exact observed data. File roots must be registered, not inferred from the file's parent directory."
        };
    }

    private static JsonObject ExampleBody(string action)
    {
        if (!CertificateOperationContract.IsKnownAction(action)) throw new ArgumentException("certificate_operation_unknown");
        var body = new JsonObject { ["version"] = CertificateOperationContract.Version };
        var pin = new string('0', 64);
        if (action is "create_ca" or "issue_code" or "issue_server" or "issue_machine_server" or "store_bind_server" or "inspect" or "export_public" or "sign" or "import_public" or "import_private" or "export_private" or "store_add" or "store_trust" or "store_admin_add" or "store_admin_trust")
            body["reference"] = action is "create_ca" or "inspect" ? "example-ca" : action is "issue_server" or "issue_machine_server" or "store_bind_server" ? "example-server" : "example-code";
        if (action is "create_ca" or "issue_code" or "issue_server" or "issue_machine_server")
        {
            body["subject"] = action == "create_ca" ? "Example local CA" : action is "issue_server" or "issue_machine_server" ? "Example HTTPS server" : "Example code signer";
            body["days"] = action == "create_ca" ? 365 : 30; body["keyBits"] = 3072;
        }
        if (action is "issue_code" or "issue_server" or "issue_machine_server") body["issuer"] = new JsonObject { ["reference"] = "example-ca", ["certificateId"] = pin };
        if (action is not ("create_ca" or "issue_code" or "issue_server" or "issue_machine_server" or "inspect" or "store_list")) body["certificateId"] = pin;
        if (action is "issue_server" or "issue_machine_server")
        {
            body["dnsNames"] = new JsonArray("localhost");
            body["ipAddresses"] = new JsonArray("127.0.0.1", "::1");
        }
        if (action == "export_public") body["format"] = "pem";
        if (action == "import_public") { body["format"] = "der"; body["content"] = "AA=="; }
        if (action is "import_private" or "export_private") body["passwordReference"] = "example-password";
        if (action is "store_list" or "store_add" or "store_trust" or "store_remove" || CertificateOperationContract.IsAdministratorAction(action))
        {
            body["scope"] = CertificateOperationContract.IsAdministratorAction(action) ? "local_machine" : "current_user";
            body["store"] = action is "store_trust" or "store_admin_trust" ? "trusted_people" : "personal";
        }
        return body;
    }
}
