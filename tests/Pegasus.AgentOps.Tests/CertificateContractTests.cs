using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class CertificateContractTests
{
    private static readonly string Pin = new('a', 64);
    private static readonly string FileSha = new('b', 64);
    private const string Subject = "Literal CN=Example, O=Fixture 雪😀";

    internal static async Task RunAsync()
    {
        ShapesAndDefaults();
        RequestIdentity();
        ExtendedContract();
        ServerContract();
        MachineServerContract();
        ClosedBodyValidation();
        ReferenceAndPinValidation();
        ContextAndFileValidation();
        SizeAndUnicode();
        await CapturedFrontendAsync();
        Console.WriteLine("PASS certificate19 pure contracts/legacy identity/public import/private file boundaries/ordinary and explicit administrator targets and captured CLI/MCP/help (no UAC, keys, stores or native certificate calls)");
    }

    private static void ShapesAndDefaults()
    {
        Check(CertificateOperationContract.Actions.Count == 19 && CertificateOperationContract.Actions.Distinct(StringComparer.Ordinal).Count() == 19,
            "one closed nineteen-operation vocabulary");
        foreach (var action in CertificateOperationContract.Actions)
        {
            var invocation = Parse(action);
            Check(invocation.Action == action && invocation.IsMutation == (action is not ("inspect" or "export_public" or "verify" or "store_list")) &&
                CertificateOperationContract.IsReadOnly(action) == !invocation.IsMutation &&
                CertificateOperationContract.PublicName(action) == "certificate_" + action,
                "typed action and read/effect classification");
            Check(invocation.RequestIdentity.Length == 64 && invocation.RequestIdentity.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "opaque lower SHA-256 request identity");
            var expectedPath = action is "sign" or "verify" or "import_private" ? "src" : action == "export_private" ? "dst" : null;
            Check(CertificateOperationContract.PathFields(action).SequenceEqual(expectedPath is null ? Array.Empty<string>() : new[] { expectedPath! }) &&
                CertificateOperationContract.IsFileAction(action) == (expectedPath is not null) &&
                (expectedPath == "src" ? invocation.Src is not null && invocation.Sha is not null && invocation.Dst is null :
                    expectedPath == "dst" ? invocation.Dst is not null && invocation.Src is null && invocation.Sha is null :
                    invocation.Src is null && invocation.Sha is null && invocation.Dst is null), "exact source/destination file identity split");
            Check(invocation.ToString().Contains("redacted", StringComparison.Ordinal) && !invocation.ToString().Contains(Subject, StringComparison.Ordinal),
                "invocation diagnostic formatting does not serialize submitted content");
        }
        var ca = Parse("create_ca");
        var leaf = Parse("issue_code");
        Check(ca.Days == 365 && leaf.Days == 30 && ca.KeyBits == 3072 && leaf.KeyBits == 3072 && ca.Subject == Subject,
            "existing primitive defaults and exact Unicode common name");
        Check(leaf.Issuer == new CertificateIssuerIdentity("fixture-ca", Pin), "issuer is pinned independently from the new reference");
        Check(Parse("inspect").CertificateId is null && Parse("inspect", Body("inspect", pinnedInspect: true)).CertificateId == Pin,
            "inspect deliberately permits optional identity pin for lost-reply diagnosis");
        foreach (var action in new[] { "inspect", "export_public", "verify", "store_list" })
            Check(CertificateOperationContract.Parse(Request(action) with { Rid = null }).Action == action, "reads do not require a mutation RID");
        foreach (var action in CertificateOperationContract.Actions.Where(action => !CertificateOperationContract.IsReadOnly(action)))
            Refused(() => CertificateOperationContract.Parse(Request(action) with { Rid = null }), "rid");
        foreach (var rid in new[] { "", " padded", "line\nrid", new string('r', 129) })
            Refused(() => CertificateOperationContract.Parse(Request("create_ca") with { Rid = rid }), "rid");
        Check(typeof(CertificateInvocation).GetProperties().All(property => property.SetMethod is null),
            "validated invocation fields cannot be changed after identity calculation");
        Check(!CertificateOperationContract.IsKnownAction(null) && !CertificateOperationContract.IsReadOnly("eval") &&
            CertificateOperationContract.IsMutation("eval"), "unknown actions never acquire read-only admission");
    }

    private static void RequestIdentity()
    {
        foreach (var action in new[] { "create_ca", "issue_code", "inspect", "export_public", "sign", "verify" })
        {
            var value = Parse(action);
            var oldShape = new { Family = "certificate", Version = 1, value.Action, value.Reference, value.CertificateId,
                value.Issuer, value.Subject, value.Days, value.KeyBits, value.Format, value.Src, value.Sha };
            Check(value.RequestIdentity == Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldShape))),
                "original six v1 request fingerprints survive nullable extension fields unchanged");
        }
        foreach (var action in new[] { "import_public", "import_private", "export_private", "store_list", "store_add", "store_trust", "store_remove" })
        {
            var value = Parse(action);
            var oldShape = new { Family = "certificate", Version = 1, value.Action, value.Reference, value.CertificateId,
                value.Issuer, value.Subject, value.Days, value.KeyBits, value.Format, value.Src, value.Sha, value.Dst,
                value.Content, value.PasswordReference, value.Store, value.Max, value.AfterCertificateId };
            Check(value.RequestIdentity == Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldShape))),
                "all thirteen existing v1 fingerprints survive explicit administrator action additions");
        }
        foreach (var action in new[] { "create_ca", "issue_code" })
        {
            var original = Parse(action);
            var body = JsonNode.Parse(Body(action))!.AsObject();
            body["days"] = action == "create_ca" ? 365 : 30; body["keyBits"] = 3072;
            Check(original.RequestIdentity == Parse(action, body.ToJsonString()).RequestIdentity,
                "omitted and explicit defaults have one identity");
            var reversed = new JsonObject();
            foreach (var item in body.Reverse()) reversed.Add(item.Key, item.Value?.DeepClone());
            Check(original.RequestIdentity == Parse(action, reversed.ToJsonString()).RequestIdentity, "JSON property order is not identity");
            body["days"] = 1;
            Check(original.RequestIdentity != Parse(action, body.ToJsonString()).RequestIdentity, "changed validity is a changed request");
        }
        var canonical = Request("sign") with { Ws = "fixture", Cwd = @"C:\fixture" };
        canonical = With(canonical, "src", @"C:\fixture\sample.exe");
        var hash = CertificateOperationContract.Parse(canonical).RequestIdentity;
        var presentation = With(With(canonical, "detail", "true"), "output", "agent") with
            { Rid = "another-valid-rid", Ws = "different-context", Cwd = @"C:\other", Quiet = true, Detail = true };
        Check(CertificateOperationContract.Parse(presentation).RequestIdentity == hash,
            "RID/presentation/context are excluded once the same canonical src identity is supplied");
        Check(CertificateOperationContract.Parse(With(canonical, "src", @"C:\fixture\other.exe")).RequestIdentity != hash &&
            CertificateOperationContract.Parse(With(canonical, "sha", new string('c', 64))).RequestIdentity != hash,
            "file source and preimage hash are identity-bound");
        var changed = JsonNode.Parse(Body("sign"))!.AsObject(); changed["certificateId"] = new string('c', 64);
        Check(CertificateOperationContract.Parse(canonical with { Body = changed.ToJsonString() }).RequestIdentity != hash,
            "signer pin is identity-bound");
        Check(Parse("inspect").RequestIdentity != Parse("inspect", Body("inspect", pinnedInspect: true)).RequestIdentity,
            "an optional diagnostic pin still changes the exact request");
        var composed = JsonNode.Parse(Body("create_ca"))!.AsObject(); composed["subject"] = "é";
        var decomposed = JsonNode.Parse(Body("create_ca"))!.AsObject(); decomposed["subject"] = "e\u0301";
        Check(Parse("create_ca", composed.ToJsonString()).RequestIdentity != Parse("create_ca", decomposed.ToJsonString()).RequestIdentity,
            "Unicode common names are exact, without normalization aliases");
    }

    private static void ExtendedContract()
    {
        var imported = Parse("import_public");
        Check(imported.Content == "AA==" && imported.Format == "der" && imported.PasswordReference is null,
            "only public import carries bounded body content");
        foreach (var invalid in new[] { "", "AA", "AB==", "AA==\n", " AA==", "AA-_", "A===", "====" })
        {
            var body = JsonNode.Parse(Body("import_public"))!.AsObject(); body["content"] = invalid;
            Refused(() => Parse("import_public", body.ToJsonString()), "content");
        }
        var publicBody = JsonNode.Parse(Body("import_public"))!.AsObject(); publicBody["content"] = "AQ==";
        Check(Parse("import_public", publicBody.ToJsonString()).RequestIdentity != imported.RequestIdentity,
            "exact public content is identity-bound");
        const string pem = "-----BEGIN CERTIFICATE-----\r\nAA==\r\n-----END CERTIFICATE-----\r\n";
        publicBody["format"] = "pem"; publicBody["content"] = pem;
        var pemImport = Parse("import_public", publicBody.ToJsonString());
        Check(pemImport.Content == pem, "public PEM line endings are preserved, without normalization");
        publicBody["content"] = pem.Replace("\r\n", "\n", StringComparison.Ordinal);
        Check(Parse("import_public", publicBody.ToJsonString()).RequestIdentity != pemImport.RequestIdentity,
            "PEM textual identity remains exact across equivalent line endings");
        foreach (var invalid in new[] { " ", "-----BEGIN PRIVATE KEY-----\nAA==\n-----END PRIVATE KEY-----", pem + pem, "prefix" + pem, pem + "suffix" })
        { publicBody["content"] = invalid; Refused(() => Parse("import_public", publicBody.ToJsonString()), "content"); }

        foreach (var action in CertificateOperationContract.Actions.Where(action => action != "import_public"))
        {
            var body = JsonNode.Parse(Body(action))!.AsObject(); body["content"] = "AA==";
            Refused(() => Parse(action, body.ToJsonString()));
        }
        foreach (var action in new[] { "import_private", "export_private" })
        {
            var value = Parse(action);
            Check(value.IsMutation && value.Content is null && value.PasswordReference == "fixture-password" && value.CertificateId == Pin,
                "private exchange uses a pinned file and opaque password reference, never a body payload");
            var body = JsonNode.Parse(Body(action))!.AsObject(); body.Remove("passwordReference");
            Refused(() => Parse(action, body.ToJsonString()));
            foreach (var reference in new[] { "", "Password", "-prefix", "a/b", new string('p', 65) })
            { body["passwordReference"] = reference; Refused(() => Parse(action, body.ToJsonString()), "passwordReference"); }
            body["passwordReference"] = "other-password";
            Check(Parse(action, body.ToJsonString()).RequestIdentity != value.RequestIdentity, "password reference is identity-bound");
            Refused(() => CertificateOperationContract.Parse(With(Request(action), "passwordReference", "fixture-password")), "arguments");
        }
        var output = Request("export_private");
        foreach (var field in new[] { "src", "sha", "overwrite", "pol", "permanent" })
            Refused(() => CertificateOperationContract.Parse(With(output, field, "1")), "arguments");
        foreach (var path in new[] { "", " ", "x\0.pfx", new string('x', 241) })
            Refused(() => CertificateOperationContract.Parse(With(output, "dst", path)), "dst");
        Check(CertificateOperationContract.Parse(With(output, "dst", "fixture:other.pfx")).RequestIdentity != Parse("export_private").RequestIdentity,
            "create-only output destination is identity-bound");
        Refused(() => CertificateOperationContract.Parse(With(Request("import_private"), "dst", "fixture:other.pfx")), "arguments");

        var page = Parse("store_list");
        Check(page.Store == new CertificateStoreIdentity("current_user", "personal") && page.Max == 32 && page.AfterCertificateId is null && !page.IsMutation,
            "store inventory has one exact target and normalized bounded page defaults");
        var listBody = JsonNode.Parse(Body("store_list"))!.AsObject(); listBody["max"] = 32;
        Check(Parse("store_list", listBody.ToJsonString()).RequestIdentity == page.RequestIdentity, "omitted and explicit page defaults share identity");
        listBody["afterCertificateId"] = Pin;
        Check(Parse("store_list", listBody.ToJsonString()).RequestIdentity != page.RequestIdentity, "continuation certificate ID is identity-bound");
        foreach (var maximum in new[] { 1, 128 })
        { listBody["max"] = maximum; Check(Parse("store_list", listBody.ToJsonString()).Max == maximum, "store inventory page endpoints"); }
        foreach (var maximum in new[] { 0, 129, -1 })
        { listBody["max"] = maximum; Refused(() => Parse("store_list", listBody.ToJsonString())); }
        listBody["max"] = 32;
        foreach (var pin in new[] { "", new string('A', 64), new string('a', 63) })
        { listBody["afterCertificateId"] = pin; Refused(() => Parse("store_list", listBody.ToJsonString())); }
        listBody.Remove("afterCertificateId");
        foreach (var scope in new[] { "current_user", "local_machine" })
            foreach (var store in new[] { "personal", "intermediate", "root", "trusted_publisher", "trusted_people" })
            {
                listBody["scope"] = scope; listBody["store"] = store;
                Check(Parse("store_list", listBody.ToJsonString()).Store == new CertificateStoreIdentity(scope, store),
                    "store scope/kind are closed identity strings, not paths or privilege selectors");
            }
        foreach (var (field, invalid) in new[] { ("scope", "CurrentUser"), ("scope", "user:other"), ("store", "HKLM\\Root"), ("store", "my") })
        { var body = JsonNode.Parse(Body("store_list"))!.AsObject(); body[field] = invalid; Refused(() => Parse("store_list", body.ToJsonString())); }
        foreach (var action in new[] { "store_add", "store_trust" })
            foreach (var store in new[] { "personal", "intermediate", "root", "trusted_publisher", "trusted_people" })
            {
                var body = JsonNode.Parse(Body(action))!.AsObject(); body["store"] = store;
                var trust = store is "root" or "trusted_publisher" or "trusted_people";
                if (trust == (action == "store_trust")) Check(Parse(action, body.ToJsonString()).Store!.Store == store,
                    "store insertion and explicit trust have separate exact targets");
                else Refused(() => Parse(action, body.ToJsonString()), "store");
            }
        var remove = JsonNode.Parse(Body("store_remove"))!.AsObject(); remove["reference"] = "not-accepted";
        Refused(() => Parse("store_remove", remove.ToJsonString()));
        foreach (var action in new[] { "store_add", "store_trust", "store_remove" })
        { var body = JsonNode.Parse(Body(action))!.AsObject(); body["max"] = 1; Refused(() => Parse(action, body.ToJsonString())); }
        foreach (var action in new[] { "store_admin_add", "store_admin_trust", "store_admin_remove" })
        {
            var value = Parse(action);
            Check(CertificateOperationContract.IsAdministratorAction(action) && value.Store!.Scope == "local_machine" &&
                value.IsMutation && !CertificateOperationContract.IsFileAction(action), "explicit administrator action is machine-only, RID-bound and nonfile");
            var body = JsonNode.Parse(Body(action))!.AsObject(); body["scope"] = "current_user";
            Refused(() => Parse(action, body.ToJsonString()), "scope");
            body["scope"] = "local_machine"; body["store"] = action == "store_admin_add" ? "root" : action == "store_admin_trust" ? "personal" : "unknown";
            Refused(() => Parse(action, body.ToJsonString()), "store");
        }
        Check(!CertificateOperationContract.IsAdministratorAction("store_add") && !CertificateOperationContract.IsAdministratorAction("store_trust") &&
            !CertificateOperationContract.IsAdministratorAction("store_remove"), "ordinary store actions never silently select elevation");
    }

    private static void ServerContract()
    {
        var issued = Parse("issue_server");
        Check(issued.Days == 30 && issued.KeyBits == 3072 &&
            issued.Issuer == new CertificateIssuerIdentity("fixture-ca", Pin) &&
            issued.ServerNames!.DnsNames.SequenceEqual(new[] { "localhost" }) &&
            issued.ServerNames.IpAddresses.SequenceEqual(new[] { "127.0.0.1", "::1" }),
            "server issuance has pinned issuer and bounded DNS/IP defaults");
        var body = JsonNode.Parse(Body("issue_server"))!.AsObject();
        body["dnsNames"] = new JsonArray("LOCALHOST");
        body["ipAddresses"] = new JsonArray("::1", "127.0.0.1");
        Check(Parse("issue_server", body.ToJsonString()).RequestIdentity == issued.RequestIdentity,
            "server identity canonicalizes DNS case and SAN order");
        body["dnsNames"] = new JsonArray("different.localhost");
        Check(Parse("issue_server", body.ToJsonString()).RequestIdentity != issued.RequestIdentity,
            "server SANs participate in request identity");
        foreach (var dns in new[] { "*.localhost", "https://localhost", "bad_name", "localhost.", "127.0.0.1", "雪.local" })
        {
            body["dnsNames"] = new JsonArray(dns);
            Refused(() => Parse("issue_server", body.ToJsonString()));
        }
        body["dnsNames"] = new JsonArray("localhost", "LOCALHOST");
        Refused(() => Parse("issue_server", body.ToJsonString()));
        body["dnsNames"] = new JsonArray();
        foreach (var ip in new[] { "127.1", "2130706433", "[::1]", "fe80::1%1", "not-an-ip" })
        {
            body["ipAddresses"] = new JsonArray(ip);
            Refused(() => Parse("issue_server", body.ToJsonString()));
        }
        body["ipAddresses"] = new JsonArray();
        Refused(() => Parse("issue_server", body.ToJsonString()));
        body["dnsNames"] = new JsonArray(Enumerable.Range(0, 17).Select(index => (JsonNode?)JsonValue.Create("n" + index + ".localhost")).ToArray());
        Refused(() => Parse("issue_server", body.ToJsonString()));
        foreach (var forbidden in new[] { "scope", "store", "privateKey", "password", "dnsNames" })
        {
            var binding = JsonNode.Parse(Body("store_bind_server"))!.AsObject();
            binding[forbidden] = "local_machine";
            Refused(() => Parse("store_bind_server", binding.ToJsonString()));
        }
        Check(Parse("store_bind_server").Store is null && Parse("store_bind_server").IsMutation,
            "private server binding has fixed CurrentUser/My and no scope override");
    }

    private static void MachineServerContract()
    {
        var machine = Parse("issue_machine_server");
        Check(machine.IsMutation && machine.Days == 30 && machine.KeyBits == 3072 && machine.Store is null &&
            machine.Issuer == new CertificateIssuerIdentity("fixture-ca", Pin) &&
            machine.ServerNames!.DnsNames.SequenceEqual(["localhost"]),
            "machine issuance retains explicit CSR/issuer policy without caller-selected store");
        Check(machine.RequestIdentity != Parse("issue_server").RequestIdentity,
            "machine-key issuance cannot reuse ordinary issuance identity");
        var body = JsonNode.Parse(Body("issue_machine_server"))!.AsObject();
        body["ipAddresses"] = new JsonArray("::1", "127.0.0.1");
        body["dnsNames"] = new JsonArray("LOCALHOST");
        Check(Parse("issue_machine_server", body.ToJsonString()).RequestIdentity == machine.RequestIdentity,
            "machine issuance canonicalizes SAN order and DNS case");
        body["dnsNames"] = new JsonArray("changed.localhost");
        Check(Parse("issue_machine_server", body.ToJsonString()).RequestIdentity != machine.RequestIdentity,
            "machine issuance SANs participate in its replay identity");
        foreach (var field in new[] { "scope", "store", "provider", "container", "privateKey", "password", "certificateId" })
        {
            var forbidden = JsonNode.Parse(Body("issue_machine_server"))!.AsObject();
            forbidden[field] = "local_machine";
            Refused(() => Parse("issue_machine_server", forbidden.ToJsonString()));
        }
    }

    private static void ClosedBodyValidation()
    {
        foreach (var action in new[] { "", "CREATE_CA", "create", "import_pfx", "export_raw", "install_trust", "store", "eval" })
            Refused(() => CertificateOperationContract.Parse(Request("inspect") with
                { Arg = new Dictionary<string, string> { ["op"] = action } }));
        foreach (var action in CertificateOperationContract.Actions)
        {
            var request = Request(action);
            foreach (var body in new[] { "null", "[]", "{}", request.Body + "{}", request.Body![..^1] + ",}",
                request.Body!.Insert(1, "/*unsupported*/"), request.Body!.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
                request.Body!.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal) })
                Refused(() => CertificateOperationContract.Parse(request with { Body = body }));
            foreach (var name in new[] { "password", "pfx", "privateKey", "raw", "store", "trust", "provider", "timestamp", "root", "sid", "elevated", "extensions" })
            {
                var body = JsonNode.Parse(request.Body!)!.AsObject(); body[name] = "disallowed-fixture-field";
                Refused(() => CertificateOperationContract.Parse(request with { Body = body.ToJsonString() }));
            }
            Refused(() => CertificateOperationContract.Parse(request with { Body = null }), "body");
        }
        var issue = Body("issue_code");
        Refused(() => Parse("issue_code", issue.Replace("\"issuer\":{", "\"issuer\":{\"reference\":\"other\",", StringComparison.Ordinal)));
        Refused(() => Parse("issue_code", issue.Replace("\"issuer\":{", "\"issuer\":{\"privateKey\":{},", StringComparison.Ordinal)));
        var deep = "{\"version\":1,\"reference\":\"fixture\",\"subject\":\"fixture\",\"issuer\":" +
            new string('[', 10) + "0" + new string(']', 10) + "}";
        Refused(() => Parse("issue_code", deep));
        foreach (var (action, maximum) in new[] { ("create_ca", 3650), ("issue_code", 366) })
        {
            var body = JsonNode.Parse(Body(action))!.AsObject(); body["days"] = maximum;
            Check(Parse(action, body.ToJsonString()).Days == maximum, "existing maximum validity bound is accepted");
            foreach (var days in new[] { 0, -1, maximum + 1 })
            { body["days"] = days; Refused(() => Parse(action, body.ToJsonString())); }
            body["days"] = 1;
            foreach (var bits in new[] { 2048, 3072, 4096 })
            { body["keyBits"] = bits; Check(Parse(action, body.ToJsonString()).KeyBits == bits, "existing RSA bit vocabulary"); }
            foreach (var bits in new[] { 0, 1024, 8192 })
            { body["keyBits"] = bits; Refused(() => Parse(action, body.ToJsonString())); }
            body["keyBits"] = 3072;
            var integer = body.ToJsonString();
            Refused(() => Parse(action, integer.Replace("\"days\":1", "\"days\":1.0", StringComparison.Ordinal)));
            Refused(() => Parse(action, integer.Replace("\"days\":1", "\"days\":1e0", StringComparison.Ordinal)));
        }
        foreach (var invalid in new[] { "", " ", " padded", "padded ", "line\nname", new string('s', 129) })
        {
            var body = JsonNode.Parse(Body("create_ca"))!.AsObject(); body["subject"] = invalid;
            Refused(() => Parse("create_ca", body.ToJsonString()));
        }
    }

    private static void ReferenceAndPinValidation()
    {
        foreach (var reference in new[] { "a", "0", "a._-9", new string('a', 64) })
        {
            var body = JsonNode.Parse(Body("inspect"))!.AsObject(); body["reference"] = reference;
            Check(Parse("inspect", body.ToJsonString()).Reference == reference, "exact bounded lower-ASCII reference");
        }
        foreach (var reference in new[] { "", ".a", "-a", "_a", "A", "café", "a/b", "a\\b", "a:b", "a b", new string('a', 65) })
        {
            var body = JsonNode.Parse(Body("inspect"))!.AsObject(); body["reference"] = reference;
            Refused(() => Parse("inspect", body.ToJsonString()));
        }
        foreach (var action in CertificateOperationContract.Actions.Where(action => action is not ("create_ca" or "issue_code" or "issue_server" or "issue_machine_server" or "inspect" or "store_list")))
        {
            var body = JsonNode.Parse(Body(action))!.AsObject(); body.Remove("certificateId");
            Refused(() => Parse(action, body.ToJsonString()));
            foreach (var invalid in new[] { "", new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64) })
            { body["certificateId"] = invalid; Refused(() => Parse(action, body.ToJsonString())); }
        }
        var issuer = JsonNode.Parse(Body("issue_code"))!.AsObject(); issuer["issuer"]!.AsObject().Remove("certificateId");
        Refused(() => Parse("issue_code", issuer.ToJsonString()));
        var nullPin = JsonNode.Parse(Body("inspect"))!.AsObject(); nullPin["certificateId"] = null;
        Refused(() => Parse("inspect", nullPin.ToJsonString()));
        foreach (var format in new[] { "pem", "der" })
        {
            var body = JsonNode.Parse(Body("export_public"))!.AsObject(); body["format"] = format;
            Check(Parse("export_public", body.ToJsonString()).Format == format, "only public PEM/DER formats");
        }
        foreach (var format in new[] { "pfx", "pkcs12", "PEM", "", "private" })
        {
            var body = JsonNode.Parse(Body("export_public"))!.AsObject(); body["format"] = format;
            Refused(() => Parse("export_public", body.ToJsonString()));
        }
    }

    private static void ContextAndFileValidation()
    {
        foreach (var action in CertificateOperationContract.Actions)
        {
            var request = Request(action);
            Refused(() => CertificateOperationContract.Parse(request with { Root = "" }), "context");
            Refused(() => CertificateOperationContract.Parse(request with { PathRoots = new Dictionary<string, string>() }), "context");
            foreach (var field in new[] { "root", "env0", "stdin", "wait", "tmo", "arg0", "__authenticated_frozen", "__execution_context_system", OperationRequest.BodyOmittedKey })
                Refused(() => CertificateOperationContract.Parse(With(request, field, "1")), "arguments");
            Refused(() => CertificateOperationContract.Parse(With(request, "rid", "conflicting-rid")), "rid");
        }
        foreach (var action in CertificateOperationContract.Actions.Where(action => !CertificateOperationContract.IsFileAction(action)))
        {
            var request = Request(action);
            Refused(() => CertificateOperationContract.Parse(request with { Ws = "" }), "context");
            Refused(() => CertificateOperationContract.Parse(With(request, "src", "file.exe")), "arguments");
            Refused(() => CertificateOperationContract.Parse(With(request, "sha", FileSha)), "arguments");
            Refused(() => CertificateOperationContract.Parse(With(request, "dst", "file.pfx")), "arguments");
        }
        foreach (var action in new[] { "sign", "verify", "import_private" })
        {
            var request = Request(action);
            Check(CertificateOperationContract.Parse(request with { Ws = "fixture", Cwd = @"C:\fixture" }).Src == FileArguments(action)["src"],
                "raw workspace context is syntactic input, not admission");
            foreach (var field in new[] { "src", "sha" })
            {
                var args = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal); args.Remove(field);
                Refused(() => CertificateOperationContract.Parse(request with { Arg = args }));
            }
            foreach (var path in new[] { "", " ", "x\0.exe", "x\n.exe", new string('x', 241), "x\ud800.exe" })
                Refused(() => CertificateOperationContract.Parse(With(request, "src", path)), "src");
            foreach (var sha in new[] { "", new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64) })
                Refused(() => CertificateOperationContract.Parse(With(request, "sha", sha)), "sha");
        }
    }

    private static void SizeAndUnicode()
    {
        var body = Body("inspect");
        var padded = body + new string(' ', CertificateOperationContract.MaxRequestBytes - Encoding.UTF8.GetByteCount(body));
        Check(Parse("inspect", padded).Reference == "fixture-ca", "body byte ceiling includes valid trailing JSON whitespace");
        Refused(() => Parse("inspect", padded + " "), "body");
        var raw = "{\"version\":1,\"reference\":\"fixture\",\"subject\":\"" + new string('雪', 128) + "\"}";
        var utf8Oversized = raw + new string(' ', CertificateOperationContract.MaxRequestBytes - raw.Length);
        Check(utf8Oversized.Length == CertificateOperationContract.MaxRequestBytes && Encoding.UTF8.GetByteCount(utf8Oversized) > CertificateOperationContract.MaxRequestBytes,
            "UTF-8 boundary fixture distinguishes characters from bytes");
        Refused(() => Parse("create_ca", utf8Oversized), "body");
        foreach (var invalid in new[] { "\\uD800", "\\uDC00", "\\uD800\\u0041", "\\uD800x", "\ud800" })
            Refused(() => Parse("create_ca", "{\"version\":1,\"reference\":\"fixture\",\"subject\":\"" + invalid + "\"}"));
        var paired = Parse("create_ca", "{\"version\":1,\"reference\":\"fixture\",\"subject\":\"\\uD83D\\uDE00\"}");
        Check(paired.Subject == "😀", "paired escaped Unicode is preserved exactly");
        var literalEscape = Parse("create_ca", "{\"version\":1,\"reference\":\"fixture\",\"subject\":\"literal \\\\uD800\"}");
        Check(literalEscape.Subject == "literal \\uD800", "literal backslash text is not interpreted as an escaped surrogate");
    }

    private static async Task CapturedFrontendAsync()
    {
        await using var dispatcher = new CaptureDispatcher();
        foreach (var action in CertificateOperationContract.Actions)
        {
            var body = Body(action);
            var args = Cli(action);
            using var output = new StringWriter(); using var error = new StringWriter();
            var code = await PaoProgram.RunCliAsync(args, dispatcher, output, error, CancellationToken.None, new StringReader(body));
            Check(code == 0 && dispatcher.Last is { Op: "certificate", Root: null } && dispatcher.Last.Body == body &&
                CertificateOperationContract.Parse(dispatcher.Last!).Action == action,
                "captured CLI certificate action/body: " + action + "; " + error);
            var mcp = new JsonObject { ["op"] = CertificateOperationContract.PublicName(action), ["text"] = body, ["output"] = "structured" };
            if (!CertificateOperationContract.IsReadOnly(action)) mcp["rid"] = "certificate-frontend-" + action;
            if (CertificateOperationContract.IsFileAction(action))
                foreach (var field in FileArguments(action)) mcp[field.Key] = field.Value;
            var calls = dispatcher.Calls;
            var response = await Call(mcp.ToJsonString(), dispatcher);
            Check(dispatcher.Calls == calls + 1 && dispatcher.Last!.Body == body &&
                CertificateOperationContract.Parse(dispatcher.Last!).Action == action && response["isError"]?.GetValue<bool>() != true,
                "captured MCP delegates certificate semantics to the pure parser");
            using var help = new StringWriter();
            code = await PaoProgram.RunCliAsync(["help", CertificateOperationContract.PublicName(action)], dispatcher, help, error, CancellationToken.None);
            Check(code == 0 && help.ToString().Contains("version1", StringComparison.Ordinal) &&
                (!CertificateOperationContract.IsFileAction(action) || help.ToString().Contains("registered", StringComparison.OrdinalIgnoreCase)),
                "help uses parser-validated examples and describes file admission");
            foreach (var extra in new[] { "root=FAKE", "wait=false", "tmo=1", "arg0=x" })
            {
                calls = dispatcher.Calls;
                code = await PaoProgram.RunCliAsync(args.Append(extra).ToArray(), dispatcher, new StringWriter(), error,
                    CancellationToken.None, new StringReader(body));
                Check(code == 2 && dispatcher.Calls == calls, "unsupported CLI authority/completion fields never dispatch");
            }
            if (action == "export_private")
                foreach (var extra in new[] { "src=fixture:other.pfx", "sha=" + FileSha, "overwrite=true", "pol=replace" })
                {
                    calls = dispatcher.Calls;
                    code = await PaoProgram.RunCliAsync(args.Append(extra).ToArray(), dispatcher, new StringWriter(), error,
                        CancellationToken.None, new StringReader(body));
                    Check(code == 2 && dispatcher.Calls == calls, "private output never accepts input/hash/overwrite policy");
                }
            if (!CertificateOperationContract.IsFileAction(action))
                foreach (var extra in new[] { "ws=fixture", "cwd=C:\\fixture" })
                {
                    calls = dispatcher.Calls;
                    code = await PaoProgram.RunCliAsync(args.Append(extra).ToArray(), dispatcher, new StringWriter(), error,
                        CancellationToken.None, new StringReader(body));
                    Check(code == 2 && dispatcher.Calls == calls, "nonfile CLI leaves do not accept workspace/path fields");
                }
            else
                foreach (var required in CertificateOperationContract.PathFields(action)
                    .Concat(CertificateOperationContract.PathFields(action).Contains("src", StringComparer.Ordinal) ? new[] { "sha" } : Array.Empty<string>())
                    .Select(field => field + "="))
                {
                    calls = dispatcher.Calls;
                    code = await PaoProgram.RunCliAsync(args.Where(value => !value.StartsWith(required, StringComparison.Ordinal)).ToArray(),
                        dispatcher, new StringWriter(), error, CancellationToken.None, new StringReader(body));
                    Check(code == 2 && dispatcher.Calls == calls, "file CLI leaves require their exact input/hash or output path fields");
                }
            calls = dispatcher.Calls;
            code = await PaoProgram.RunCliAsync(args.Where(value => value != "--stdin").ToArray(), dispatcher,
                new StringWriter(), error, CancellationToken.None);
            Check(code == 2 && dispatcher.Calls == calls, "every action requires its explicit body");
        }
        foreach (var action in CertificateOperationContract.Actions.Where(action => !CertificateOperationContract.IsReadOnly(action)))
        {
            var calls = dispatcher.Calls;
            var code = await PaoProgram.RunCliAsync(Cli(action).Where(value => !value.StartsWith("rid=", StringComparison.Ordinal)).ToArray(),
                dispatcher, new StringWriter(), new StringWriter(), CancellationToken.None, new StringReader(Body(action)));
            Check(code == 2 && dispatcher.Calls == calls, "mutation RID is mandatory before CLI dispatch");
        }
        foreach (var action in new[] { "import_private", "export_private" })
        {
            var body = JsonNode.Parse(Body(action))!.AsObject(); body["content"] = "AA==";
            var calls = dispatcher.Calls;
            var code = await PaoProgram.RunCliAsync(Cli(action), dispatcher, new StringWriter(), new StringWriter(),
                CancellationToken.None, new StringReader(body.ToJsonString()));
            Check(code == 2 && dispatcher.Calls == calls, "private body payload is refused before CLI dispatch");
            var mcp = new JsonObject { ["op"] = CertificateOperationContract.PublicName(action), ["text"] = body.ToJsonString(), ["rid"] = "private-payload-refusal" };
            foreach (var field in FileArguments(action)) mcp[field.Key] = field.Value;
            var rejected = await Call(mcp.ToJsonString(), dispatcher);
            Check(dispatcher.Calls == calls && rejected["isError"]?.GetValue<bool>() == true,
                "private body payload is refused before MCP dispatch");
        }
        var prefix = "{\"op\":\"certificate_inspect\",\"text\":" + JsonSerializer.Serialize(Body("inspect"));
        foreach (var invalid in new[]
        {
            prefix + ",\"text\":" + JsonSerializer.Serialize(Body("inspect")) + "}",
            prefix + ",\"op\":\"certificate_inspect\"}", prefix + ",\"rid\":\"one\",\"rid\":\"two\"}",
            prefix + ",\"ws\":\"fixture\"}", prefix + ",\"src\":\"file.exe\"}", prefix + ",\"root\":\"FAKE\"}",
            prefix + ",\"wait\":false}", prefix + ",\"tmo\":1}", prefix + ",\"detail\":\"true\"}",
            "{\"op\":\"certificate_inspect\",\"text\":{}}",
            "{\"op\":\"certificate_inspect\",\"text\":\"{}\"}"
        })
        {
            var calls = dispatcher.Calls;
            var refused = await Call(invalid, dispatcher);
            Check(dispatcher.Calls == calls && refused["isError"]?.GetValue<bool>() == true,
                "invalid/duplicate MCP fields are refused before projection");
        }
        var discovery = PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Check(CertificateOperationContract.Actions.All(action => discovery.Contains(CertificateOperationContract.PublicName(action))),
            "all eighteen fixed certificate leaves are discoverable");
    }

    private static string[] Cli(string action)
    {
        var args = new List<string> { "--json", "certificate", action, "--stdin" };
        if (!CertificateOperationContract.IsReadOnly(action)) args.Add("rid=certificate-frontend-" + action);
        args.AddRange(FileArguments(action).Select(field => field.Key + "=" + field.Value));
        return args.ToArray();
    }

    private static async ValueTask<JsonObject> Call(string json, CaptureDispatcher dispatcher)
    {
        using var document = JsonDocument.Parse(json);
        return await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, dispatcher, CancellationToken.None,
            defaultWs: "must-not-default", defaultCwd: @"C:\ambient-unused");
    }

    private static CertificateInvocation Parse(string action, string? body = null) => CertificateOperationContract.Parse(Request(action, body));
    private static OperationRequest Request(string action, string? body = null)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = action };
        foreach (var field in FileArguments(action)) args[field.Key] = field.Value;
        args.Remove("ws");
        return new("certificate", args, "certificate-contract-" + action, Body: body ?? Body(action));
    }

    private static OperationRequest With(OperationRequest request, string key, string value) =>
        request with { Arg = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [key] = value } };

    private static string Body(string action, bool pinnedInspect = false)
    {
        var result = new JsonObject { ["version"] = 1 };
        if (action is "create_ca" or "issue_code" or "issue_server" or "issue_machine_server" or "store_bind_server" or "inspect" or "export_public" or "sign" or "import_public" or "import_private" or "export_private" or "store_add" or "store_trust" or "store_admin_add" or "store_admin_trust")
            result["reference"] = action is "create_ca" or "inspect" ? "fixture-ca" : action is "issue_server" or "issue_machine_server" or "store_bind_server" ? "fixture-server" : "fixture-code";
        if (action is "create_ca" or "issue_code" or "issue_server" or "issue_machine_server") result["subject"] = Subject;
        if (action is "issue_code" or "issue_server" or "issue_machine_server") result["issuer"] = new JsonObject { ["reference"] = "fixture-ca", ["certificateId"] = Pin };
        if (action is not ("create_ca" or "issue_code" or "issue_server" or "issue_machine_server" or "inspect" or "store_list") || action == "inspect" && pinnedInspect) result["certificateId"] = Pin;
        if (action is "issue_server" or "issue_machine_server")
        {
            result["dnsNames"] = new JsonArray("localhost");
            result["ipAddresses"] = new JsonArray("127.0.0.1", "::1");
        }
        if (action == "export_public") result["format"] = "pem";
        if (action == "import_public") { result["format"] = "der"; result["content"] = "AA=="; }
        if (action is "import_private" or "export_private") result["passwordReference"] = "fixture-password";
        if (action is "store_list" or "store_add" or "store_trust" or "store_remove" || CertificateOperationContract.IsAdministratorAction(action))
        { result["scope"] = CertificateOperationContract.IsAdministratorAction(action) ? "local_machine" : "current_user";
            result["store"] = action is "store_trust" or "store_admin_trust" ? "trusted_people" : "personal"; }
        return result.ToJsonString();
    }

    private static Dictionary<string, string> FileArguments(string action)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var field = CertificateOperationContract.PathFields(action).FirstOrDefault();
        if (field is null) return result;
        result["ws"] = "fixture";
        result[field] = field == "dst" ? "fixture:export.pfx" : action == "import_private" ? "fixture:input.pfx" : "fixture:sample.exe";
        if (field == "src") result["sha"] = FileSha;
        return result;
    }

    private static void Refused(Action action, string? field = null)
    {
        try { action(); }
        catch (CertificateContractException error)
        {
            Check(error.Message == "certificate_request_invalid" && (field is null || error.Field == field), "bounded constant contract diagnostics");
            return;
        }
        throw new InvalidOperationException("expected closed certificate contract refusal");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class CaptureDispatcher : IOperationDispatcher
    {
        internal OperationRequest? Last;
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Last = request; Calls++;
            return Task.FromResult(new OperationReceipt(true, "captured-certificate-jid", request.Rid ?? "captured-certificate-rid", "done",
                CertificateOperationContract.PublicName(request.Arg["op"]), Data: new Dictionary<string, object?>
                    { ["certificate"] = new { marker = "public-only-fixture" } }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
