using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Owned SQLite + in-memory certificate exchange and fake stores/secrets only.
// No Windows certificate store, credential reader, DPAPI or native signer is
// instantiated. Generic owner/replay admission matrices live in WiringTests.
internal static class CertificateStoreExchangeTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";
    private const string SecretReference = "fixture.exchange.secret";
    private const string SecretValue = "Synthetic exchange password never enters a request or receipt!";
    private static readonly string ZeroHash = new('0', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var authority = new CertificateOperations().CreateAuthority(new("Exchange fixture authority", now.AddMinutes(-1), now.AddDays(2), 2048));
        await PublicImportAsync(authority);
        await ProtectedExchangeAsync(authority);
        await StorePolicyAsync(authority);
        await StoreOutcomesAsync(authority);
        await ExportBodyLossAsync(authority);
    }

    private static async Task PublicImportAsync(X509Certificate2 authority)
    {
        using var fixture = new Fixture(authority);
        await using var engine = fixture.Create();
        var content = Convert.ToBase64String(authority.RawData);
        var request = Request("import_public", new { version = 1, reference = "imported-public", certificateId = fixture.Id, format = "der", content }, "public-import");
        var result = await engine.ExecuteAsync(request);
        Check(result.Ok && fixture.Repository.Peek("imported-public") is { ProtectedMaterialPresent: false } entry && entry.CertificateId == fixture.Id &&
            fixture.Repository.KeyReads == 0 && fixture.Secrets.Reads == 0 && fixture.Stores.Calls == 0,
            "public import publishes only a pinned public reference without private-key, password or store access");
        await RetiredAsync(engine);
        AssertMetadata(fixture.Stored(request.Rid!).Receipt, request, content, SecretValue, "Exchange fixture authority");
        Check(fixture.Stored(request.Rid!).Request is null, "completed public import clears its durable request");
        var publishes = fixture.Repository.Publishes;
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == result.Jid && fixture.Repository.Publishes == publishes, "public-import replay never republishes material");
        AssertMetadata(replay, request, content, SecretValue);

        var mismatched = Request("import_public", new { version = 1, reference = "wrong-pin", certificateId = ZeroHash, format = "der", content }, "public-wrong-pin");
        Check(!(await engine.ExecuteAsync(mismatched)).Ok && !fixture.Repository.Contains("wrong-pin") && fixture.Repository.Publishes == publishes,
            "public import pin is checked before repository publication");
        var original = fixture.Repository.Peek("imported-public");
        var collision = Request("create_ca", new { version = 1, reference = "imported-public", subject = "Must not replace imported public reference", days = 1, keyBits = 2048 }, "public-create-collision");
        Check(!(await engine.ExecuteAsync(collision)).Ok && fixture.Repository.Peek("imported-public").CertificateId == original.CertificateId &&
            !fixture.Repository.Peek("imported-public").ProtectedMaterialPresent,
            "create-only reference collision cannot upgrade or replace an imported public-only reference");
        var reads = fixture.Repository.KeyReads;
        var secretReads = fixture.Secrets.Reads;
        await fixture.RegisterAsync(engine);
        var privateExport = fixture.PrivateRequest("export_private", "imported-public", "public-cannot-export-private");
        var denied = await engine.ExecuteAsync(privateExport);
        Check(!denied.Ok && fixture.Repository.KeyReads == reads + 1 && fixture.Secrets.Reads == secretReads && !HasPayload(denied) && !File.Exists(fixture.Destination),
            "public-only reference cannot Resolve a private key or fall back to public export/password access");
    }

    private static async Task ProtectedExchangeAsync(X509Certificate2 authority)
    {
        using var fixture = new Fixture(authority);
        await using var engine = fixture.Create();
        var encrypted = authority.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000), SecretValue);
        try
        {
            var content = Convert.ToBase64String(encrypted);
            fixture.WriteImport(encrypted);
            await fixture.RegisterAsync(engine);
            var importedRequest = fixture.PrivateRequest("import_private", "imported-private", "private-import");
            Check(!Contains(importedRequest.Body!, SecretValue) && !Contains(importedRequest.Body!, content),
                "private import uses an admitted file and opaque password reference, never PFX/password text");
            var imported = await engine.ExecuteAsync(importedRequest);
            Check(imported.Ok && fixture.Repository.Peek("imported-private").ProtectedMaterialPresent && fixture.Repository.KeyReads == 0 &&
                fixture.Secrets.Reads == 1 && fixture.Secrets.References.SequenceEqual(new[] { SecretReference }) && fixture.Exchange.Imports == 1 &&
                fixture.LastExchangeRoot == fixture.Workspace && !Contains(JsonSerializer.Serialize(imported, Json), content),
                "encrypted import resolves the explicit secret and publishes one ephemeral private certificate without OS-store effects");
            await RetiredAsync(engine);
            AssertMetadata(fixture.Stored(imported.Rid).Receipt, importedRequest, content, SecretValue, "Exchange fixture authority");
            var secretReads = fixture.Secrets.Reads;
            var publishes = fixture.Repository.Publishes;
            Check((await engine.ExecuteAsync(importedRequest)).Jid == imported.Jid && fixture.Secrets.Reads == secretReads && fixture.Repository.Publishes == publishes,
                "private-import replay neither decrypts nor republishes its secret body");

            var exportRequest = fixture.PrivateRequest("export_private", "imported-private", "private-export");
            var factories = fixture.Factories;
            var keys = fixture.Repository.KeyReads;
            var noRid = await engine.ExecuteAsync(exportRequest with { Rid = null });
            Check(!noRid.Ok && fixture.Factories == factories && fixture.Repository.KeyReads == keys && fixture.Secrets.Reads == secretReads,
                "export-private requires a mutation RID before factories, keys or secrets");
            var noWorkspace = await engine.ExecuteAsync(exportRequest with { Rid = "private-export-no-workspace", Ws = null });
            Check(!noWorkspace.Ok && fixture.Factories == factories && fixture.Repository.KeyReads == keys && fixture.Secrets.Reads == secretReads,
                "private export never infers its output authority from the destination's parent directory");
            var exported = await engine.ExecuteAsync(exportRequest);
            Check(exported.Ok && exported.Data?.GetValueOrDefault("certificate") is CertificatePrivateExportResult metadata &&
                metadata.CertificateId == fixture.Id && metadata.Destination == fixture.Destination && metadata.Bytes > 0 && metadata.Sha256 == Hash(fixture.Destination) &&
                fixture.Repository.KeyReads == keys + 1 && fixture.Secrets.Reads == secretReads + 1 && fixture.Exchange.Exports == 1,
                "original private-export result contains exact destination/count/hash metadata after create-only owned-file output");
            var exportedBytes = File.ReadAllBytes(fixture.Destination);
            var exportedContent = Convert.ToBase64String(exportedBytes);
            try
            {
                Check(!Contains(JsonSerializer.Serialize(exported, Json), exportedContent) && !Contains(JsonSerializer.Serialize(exported, Json), SecretValue),
                    "private export reply contains neither encrypted private material nor the password");
                using var input = new MemoryStream(exportedBytes, writable: false);
                using var restored = CertificateExchange.ImportProtectedPkcs12(input, SecretReference, new FakeSecrets(), fixture.Id);
                Check(restored.HasPrivateKey && CertificateOperations.Inspect(restored).CertificateId == fixture.Id,
                    "actual in-memory encrypted export round-trips the exact private certificate through the library");
            }
            finally { CryptographicOperations.ZeroMemory(exportedBytes); }
            await RetiredAsync(engine);
            var stored = fixture.Stored(exportRequest.Rid!);
            Check(stored.Request is null, "completed private export leaves no recoverable request");
            AssertMetadata(stored.Receipt, exportRequest, exportedContent, content, SecretValue, "Exchange fixture authority", fixture.Destination);
            keys = fixture.Repository.KeyReads; secretReads = fixture.Secrets.Reads; factories = fixture.Factories;
            var replay = await engine.ExecuteAsync(exportRequest);
            var waited = await engine.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "wait", ["jid"] = exported.Jid }, "observe-private-export"));
            Check(replay.Jid == exported.Jid && waited.Jid == exported.Jid && fixture.Repository.KeyReads == keys &&
                fixture.Secrets.Reads == secretReads && fixture.Factories == factories && fixture.Exchange.Exports == 1,
                "same-RID replay and job wait observe metadata without fresh export, key resolution or secret access");
            AssertMetadata(replay, exportRequest, exportedContent, content, SecretValue);
            AssertMetadata(waited, exportRequest, exportedContent, content, SecretValue);
            var originalOutput = Hash(fixture.Destination);
            var collision = await engine.ExecuteAsync(exportRequest with { Rid = "private-export-no-overwrite" });
            Check(!collision.Ok && Hash(fixture.Destination) == originalOutput,
                "fresh export RID cannot overwrite an existing private output file");
            var unknownDestination = Path.Combine(fixture.Workspace, "unknown-export.pfx");
            fixture.Exchange.UnknownAfterExport = true;
            var unknownRequest = fixture.PrivateRequest("export_private", "imported-private", "private-export-publication-unknown", unknownDestination);
            var unknown = await engine.ExecuteAsync(unknownRequest);
            AssertUnknown(unknown);
            Check(File.Exists(unknownDestination) && !HasPayload(unknown), "post-publication fake failure retains output without returning private material");
            var unknownBytes = File.ReadAllBytes(unknownDestination);
            string unknownContent;
            try { unknownContent = Convert.ToBase64String(unknownBytes); }
            finally { CryptographicOperations.ZeroMemory(unknownBytes); }
            await RetiredAsync(engine);
            var exports = fixture.Exchange.Exports;
            var unknownReplay = await engine.ExecuteAsync(unknownRequest);
            AssertUnknown(unknownReplay);
            Check(unknownReplay.Jid == unknown.Jid && fixture.Exchange.Exports == exports, "unknown output publication never triggers another private export");
            AssertMetadata(unknownReplay, unknownRequest, exportedContent, unknownContent, content, SecretValue, unknownDestination);
            var rows = fixture.Rows();
            keys = fixture.Repository.KeyReads; secretReads = fixture.Secrets.Reads;
            var read = await engine.ExecuteAsync(Request("store_list", StoreBody("current_user", "personal"), exported.Rid));
            Check(read.Ok && read.Op == "certificate_store_list" && read.Jid != exported.Jid && fixture.Rows() == rows &&
                fixture.Repository.KeyReads == keys && fixture.Secrets.Reads == secretReads,
                "new store snapshot is nondurable and cannot rescue an old private-export RID/result");
            fixture.AssertHistoryExcludes(content, exportedContent, unknownContent, SecretValue, "BEGIN PRIVATE KEY", "Exchange fixture authority");
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private static async Task StorePolicyAsync(X509Certificate2 authority)
    {
        using var fixture = new Fixture(authority);
        await using var engine = fixture.Create();
        foreach (var scope in new[] { "current_user", "local_machine" })
        {
            var rows = fixture.Rows();
            var listed = await engine.ExecuteAsync(Request("store_list", StoreBody(scope, "personal"), "list-" + scope));
            Check(listed.Ok && listed.Data?.GetValueOrDefault("certificate") is CertificateStorePage && fixture.Rows() == rows &&
                fixture.Repository.KeyReads == 0 && fixture.Secrets.Reads == 0,
                "CurrentUser/LocalMachine inventory uses fake public backend and remains nondurable");
        }
        var factories = fixture.Factories;
        var backend = fixture.Stores.Calls;
        var repo = fixture.Repository.Calls;
        foreach (var action in new[] { "store_add", "store_trust", "store_remove" })
        {
            var denied = await engine.ExecuteAsync(Request(action, StoreBody("local_machine", action == "store_trust" ? "root" : "personal",
                action == "store_remove" ? null : "public", fixture.Id), "deny-machine-" + action));
            Check(!denied.Ok && fixture.Factories == factories && fixture.Stores.Calls == backend && fixture.Repository.Calls == repo && fixture.Secrets.Reads == 0,
                "LocalMachine mutation refused before every backend/factory/key/secret boundary");
        }
        var wrongTrust = await engine.ExecuteAsync(Request("store_add", StoreBody("current_user", "root", "public", fixture.Id), "ordinary-add-cannot-install-trust"));
        Check(!wrongTrust.Ok && fixture.Stores.Calls == backend, "ordinary store insertion cannot silently install trust");
        var added = await engine.ExecuteAsync(Request("store_add", StoreBody("current_user", "personal", "public", fixture.Id), "add-public-personal"));
        Check(added.Ok && fixture.Stores.Writes == 1 && fixture.Authorizations.Contains((CertificateStoreScope.CurrentUser, CertificateStoreAction.AddPublic)),
            "CurrentUser personal insertion explicitly authorizes public-only AddPublic");
        var trust = await engine.ExecuteAsync(Request("store_trust", StoreBody("current_user", "root", "public", fixture.Id), "explicit-root-trust"));
        Check(trust.Ok && fixture.Stores.Writes == 2 && fixture.Authorizations.Contains((CertificateStoreScope.CurrentUser, CertificateStoreAction.InstallTrust)) &&
            fixture.Repository.KeyReads == 0 && fixture.Secrets.Reads == 0,
            "explicit CurrentUser trust action inserts only public DER with no private-key or password access");
        var removed = await engine.ExecuteAsync(Request("store_remove", StoreBody("current_user", "personal", certificateId: fixture.Id), "remove-public-personal"));
        Check(removed.Ok && fixture.Stores.Writes == 3 && fixture.Authorizations.Contains((CertificateStoreScope.CurrentUser, CertificateStoreAction.RemovePublic)),
            "CurrentUser removal targets the exact public identity");
        var before = fixture.Stores.Calls;
        var pinned = await engine.ExecuteAsync(Request("store_add", StoreBody("current_user", "personal", "public", ZeroHash), "store-pin-mismatch"));
        Check(!pinned.Ok && fixture.Stores.Calls == before, "store insertion cannot substitute a reference whose public pin differs");
        factories = fixture.Factories; backend = fixture.Stores.Calls; repo = fixture.Repository.Calls;
        foreach (var peer in new[] { new LocalPeer("S-1-5-21-100-200-300-1002", 7), new LocalPeer(OwnerSid, 7, Elevated: true) })
        {
            fixture.Peer = peer;
            var denied = await engine.ExecuteAsync(Request("store_list", StoreBody("local_machine", "personal"), "no-identity-fallback"));
            Check(!denied.Ok && fixture.Factories == factories && fixture.Stores.Calls == backend && fixture.Repository.Calls == repo,
                "foreign/elevated identity does not fall back to another owner or CurrentUser store");
        }
    }

    private static async Task StoreOutcomesAsync(X509Certificate2 authority)
    {
        using var fixture = new Fixture(authority);
        await using var engine = fixture.Create();
        fixture.Stores.FailureBeforeWrite = true;
        var before = await engine.ExecuteAsync(Request("store_add", StoreBody("current_user", "personal", "public", fixture.Id), "store-preissue-failure"));
        Check(!before.Ok && before.State == "failed" && !before.Partial && fixture.Stores.Writes == 0 &&
            before.Failure?.Execution.Value == OperationExecutionState.NotDispatched,
            "store failure before issue is not presented as a partial or uncertain effect");
        fixture.Stores.FailureBeforeWrite = false; fixture.Stores.UnknownAfterWrite = true;
        var request = Request("store_add", StoreBody("current_user", "personal", "public", fixture.Id), "store-postissue-unknown");
        var uncertain = await engine.ExecuteAsync(request);
        AssertUnknown(uncertain);
        Check(fixture.Stores.Writes == 1, "fake store records one issued effect before lost observation");
        await RetiredAsync(engine);
        var stored = fixture.Stored(request.Rid!);
        AssertUnknown(stored.Receipt); AssertMetadata(stored.Receipt, request, SecretValue, Convert.ToBase64String(authority.RawData));
        var calls = fixture.Stores.Calls;
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == uncertain.Jid && fixture.Stores.Calls == calls && fixture.Stores.Writes == 1,
            "unknown store mutation is never replayed as a fresh write or fake success");
        AssertUnknown(replay);
    }

    private static async Task ExportBodyLossAsync(X509Certificate2 authority)
    {
        using var fixture = new Fixture(authority);
        var engine = fixture.Create();
        await fixture.RegisterAsync(engine);
        var request = fixture.PrivateRequest("export_private", "private", "queued-private-export-body-loss");
        fixture.Mode.HoldAfterNextRead();
        var pending = engine.ExecuteAsync(request);
        try
        {
            await UntilAsync(() => fixture.Find(request.Rid!)?.State == "queued");
            var row = fixture.Stored(request.Rid!);
            Check(row.Request is { Op: "certificate", Body: null, Cwd: null, Ws: null, PathRoots: null } skeleton &&
                skeleton.Arg.Count == 2 && skeleton.Arg.ContainsKey("op") && skeleton.Arg.GetValueOrDefault(OperationRequest.BodyOmittedKey) == "1" &&
                fixture.Factories == 0 && fixture.Repository.KeyReads == 0 && fixture.Secrets.Reads == 0,
                "accepted queued private export retains no credential/key/payload context and performs no export");
        }
        finally
        {
            await engine.DisposeAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
        fixture.Mode.Open();
        await using var reopened = fixture.Create();
        var recovered = fixture.Stored(request.Rid!);
        AssertUnknown(recovered.Receipt);
        Check(recovered.Request is null && fixture.Factories == 0 && fixture.Repository.KeyReads == 0 && fixture.Secrets.Reads == 0,
            "lost export body becomes recovery_required without reconstructing secrets or fresh encrypted output");
        var replay = await reopened.ExecuteAsync(request);
        Check(replay.Jid == recovered.Receipt.Jid && fixture.Factories == 0, "recovered export RID observes uncertainty without re-export");
        AssertMetadata(replay, request, SecretValue);
    }

    private static OperationRequest Request(string action, object body, string? rid) =>
        new("certificate", new Dictionary<string, string> { ["op"] = action }, rid, Body: JsonSerializer.Serialize(body, Json));
    private static object StoreBody(string scope, string store, string? reference = null, string? certificateId = null)
    {
        var body = new Dictionary<string, object?> { ["version"] = 1, ["scope"] = scope, ["store"] = store };
        if (reference is not null) body["reference"] = reference;
        if (certificateId is not null) body["certificateId"] = certificateId;
        return body;
    }
    private static bool HasPayload(OperationReceipt receipt) => receipt.Data?.ContainsKey("certificate") == true;
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string? Identity(OperationReceipt receipt) => receipt.Data?.GetValueOrDefault("requestIdentity") switch
    { string value => value, JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(), _ => null };
    private static void AssertMetadata(OperationReceipt receipt, OperationRequest request, params string[] forbidden)
    {
        Check(!HasPayload(receipt) && receipt.Detail is null && Identity(receipt) == CertificateOperationContract.Parse(request).RequestIdentity,
            "durable exchange/store receipt contains immutable identity and outcomes, not its original live result");
        var raw = JsonSerializer.Serialize(receipt, Json);
        foreach (var value in forbidden) Check(!Contains(raw, value), "exchange material or secret entered durable receipt/replay");
    }
    private static bool Contains(string raw, string value)
    {
        if (raw.Length == 0) return false;
        using var document = JsonDocument.Parse(raw);
        return Strings(document.RootElement).Any(text => text.Contains(value, StringComparison.Ordinal));
    }
    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (var text in Strings(property.Value)) yield return text;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var text in Strings(item)) yield return text;
    }
    private static void AssertUnknown(OperationReceipt receipt) => Check(!receipt.Ok && receipt.State == "recovery_required" && receipt.Partial &&
        receipt.Failure is { Execution.Value: OperationExecutionState.Unknown, Retry.Value: OperationRetryDisposition.NotSupported },
        "issued/unknown store or export evidence remains unresolved and cannot authorize replay");
    private static Task RetiredAsync(AgentOpsEngine engine) => UntilAsync(() => engine.ResidentCompletionCount == 0 && engine.ResidentJobCount == 0);
    private static async Task UntilAsync(Func<bool> ready)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, deadline.Token); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record Stored(string State, OperationReceipt Receipt, OperationRequest? Request);

    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot _root = new();
        private int _factories;
        internal readonly MemoryRepository Repository = new();
        internal readonly FakeStores Stores = new();
        internal readonly FakeSecrets Secrets = new();
        internal readonly FakeExchange Exchange;
        internal readonly UserHostAdmission Admission = new();
        internal readonly OpenMode Mode = new();
        internal readonly List<(CertificateStoreScope, CertificateStoreAction)> Authorizations = [];
        internal LocalPeer Peer = new(OwnerSid, 7);
        internal string State { get; }
        internal string Workspace { get; }
        internal string Source { get; }
        internal string Destination { get; }
        internal string Id { get; }
        internal string? LastExchangeRoot;
        internal int Factories => Volatile.Read(ref _factories);
        internal Fixture(X509Certificate2 authority)
        {
            State = _root.Dir("certificate-store-exchange"); Workspace = _root.Dir("exchange-workspace");
            Source = Path.Combine(Workspace, "owned-input.pfx"); Destination = Path.Combine(Workspace, "owned-output.pfx");
            Id = CertificateOperations.Inspect(authority).CertificateId;
            Repository.Seed("private", authority);
            using var publicOnly = CertificateExchange.ImportPublic(authority.RawData, PublicCertificateFormat.Der);
            Repository.Seed("public", publicOnly);
            Exchange = new(Repository, Secrets);
        }
        internal AgentOpsEngine Create()
        {
            var engine = new AgentOpsEngine(State, null, false, null, false, null, maintenanceMode: Mode, userHostAdmission: Admission);
            engine.BindCertificateOwner(new(OwnerSid, () => Peer,
                () => { Interlocked.Increment(ref _factories); return Repository; },
                (_, _) => throw new InvalidOperationException("store/exchange fixture reached native signing factory"),
                stores: admission =>
                {
                    Interlocked.Increment(ref _factories);
                    return new(Stores, new RecordingAdmission(admission, Authorizations));
                }, secrets: () => { Interlocked.Increment(ref _factories); return Secrets; },
                exchange: (root, repository, secrets) =>
                {
                    Interlocked.Increment(ref _factories); LastExchangeRoot = root;
                    Check(root == Workspace && ReferenceEquals(repository, Repository) && ReferenceEquals(secrets, Secrets),
                        "private exchange did not receive the exact registered root/repository/secret provider");
                    return Exchange;
                }));
            return engine;
        }
        internal void WriteImport(byte[] bytes) => File.WriteAllBytes(Source, bytes);
        internal OperationRequest PrivateRequest(string action, string reference, string rid, string? destination = null)
        {
            var request = Request(action, new { version = 1, reference, certificateId = Id, passwordReference = SecretReference }, rid);
            var arguments = new Dictionary<string, string>(request.Arg);
            if (action == "import_private") { arguments["src"] = Source; arguments["sha"] = Hash(Source); }
            else arguments["dst"] = destination ?? Destination;
            return request with { Arg = arguments, Ws = "exchange-fixture" };
        }
        internal async Task RegisterAsync(AgentOpsEngine engine)
        {
            Check((await engine.ExecuteAsync(new("ctx", new Dictionary<string, string> { ["register"] = "exchange-fixture:" + Workspace }))).Ok,
                "owned exchange workspace registration failed");
            await RetiredAsync(engine);
        }
        internal long Rows()
        { using var connection = Connection(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(command.ExecuteScalar()); }
        internal Stored Stored(string rid) => Find(rid) ?? throw new InvalidOperationException("owned exchange fixture row missing");
        internal Stored? Find(string rid)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,receipt,request FROM jobs WHERE rid=$rid"; command.Parameters.AddWithValue("$rid", rid);
            using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            return new(reader.GetString(0), JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(1), Json)!,
                reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<OperationRequest>(reader.GetString(2), Json));
        }
        internal void AssertHistoryExcludes(params string[] forbidden)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT receipt,COALESCE(request,'') FROM jobs"; using var reader = command.ExecuteReader();
            while (reader.Read()) foreach (var raw in new[] { reader.GetString(0), reader.GetString(1) })
                foreach (var value in forbidden) Check(!Contains(raw, value), "exchange secret/PFX/public material entered owned SQLite history");
        }
        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(State, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() { Repository.Dispose(); _root.Dispose(); }
    }

    private sealed class RecordingAdmission(ICertificateStoreAdmission inner, List<(CertificateStoreScope, CertificateStoreAction)> observed) : ICertificateStoreAdmission
    {
        public void Authorize(CertificateStoreTarget target, CertificateStoreAction action)
        { inner.Authorize(target, action); observed.Add((target.Scope, action)); }
    }
    private sealed class FakeSecrets : ICertificateSecretResolver
    {
        internal int Reads;
        internal readonly List<string> References = [];
        public CertificateSecret Resolve(string reference)
        {
            Reads++; References.Add(reference);
            if (reference != SecretReference) throw new CertificateOperationException("fixture_secret_missing");
            return new(SecretValue);
        }
    }
    // The injectable exchange runs the real encrypted stream primitives over
    // owned fixture files. It does not qualify the production native file lease,
    // ACL-at-creation, reparse or publication implementation.
    private sealed class FakeExchange(MemoryRepository repository, FakeSecrets secrets) : IOwnerCertificateFileExchange
    {
        internal int Imports, Exports;
        internal bool UnknownAfterExport;
        public X509Certificate2 Import(string source, string sha, string passwordReference, string certificateId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Imports++;
            var bytes = File.ReadAllBytes(source);
            try
            {
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha)
                    throw new OwnerCertificateFileExchangeException("certificate_preimage_changed");
                using var input = new MemoryStream(bytes, writable: false);
                return CertificateExchange.ImportProtectedPkcs12(input, passwordReference, secrets, certificateId);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public CertificatePrivateExportResult Export(string destination, CertificateKeyReference key, string passwordReference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Exports++;
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new OwnerCertificateFileExchangeException("certificate_export_destination_exists");
            using var output = new MemoryStream();
            byte[]? bytes = null;
            try
            {
                var result = CertificateExchange.ExportProtectedPkcs12(key, repository, passwordReference, secrets, output);
                bytes = output.ToArray(); token.ThrowIfCancellationRequested();
                using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes);
                if (UnknownAfterExport)
                    throw new OwnerCertificateFileExchangeException("certificate_export_outcome_unknown", published: true, outcomeUnknown: true);
                return new(result.CertificateId, destination, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            finally
            {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if (output.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
            }
        }
    }
    private sealed class FakeStores : ICertificateStoreBackend
    {
        private readonly Dictionary<CertificateStoreTarget, Dictionary<string, CertificateSummary>> _values = [];
        internal int Calls, Writes;
        internal bool FailureBeforeWrite, UnknownAfterWrite;
        public IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target)
        { Calls++; return _values.TryGetValue(target, out var values) ? values.Values.ToArray() : []; }
        public CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] certificateDer)
        {
            Calls++;
            if (FailureBeforeWrite) throw new CertificateOperationException("certificate_store_unavailable");
            using var certificate = CertificateExchange.ImportPublic(certificateDer, PublicCertificateFormat.Der);
            Check(!certificate.HasPrivateKey, "store boundary received private material");
            var summary = CertificateOperations.Inspect(certificate);
            if (!_values.TryGetValue(target, out var values)) _values[target] = values = [];
            var changed = values.TryAdd(summary.CertificateId, summary);
            if (changed) Writes++;
            if (UnknownAfterWrite) throw new CertificateOperationException("certificate_store_outcome_unknown");
            return new(changed ? "added" : "unchanged", summary.CertificateId, changed);
        }
        public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string certificateId)
        {
            Calls++;
            if (FailureBeforeWrite) throw new CertificateOperationException("certificate_store_unavailable");
            var changed = _values.TryGetValue(target, out var values) && values.Remove(certificateId);
            if (changed) Writes++;
            if (UnknownAfterWrite) throw new CertificateOperationException("certificate_store_outcome_unknown");
            return new(changed ? "removed" : "absent", certificateId, changed);
        }
    }
    private sealed class MemoryRepository : IOwnerCertificateRepository, IDisposable
    {
        private readonly Dictionary<string, (OwnerCertificateEntry Entry, X509Certificate2 Certificate)> _values = new(StringComparer.Ordinal);
        internal int Publishes, Reads, KeyReads;
        internal int Calls => Publishes + Reads + KeyReads;
        internal void Seed(string reference, X509Certificate2 certificate) => _values.Add(reference, (Entry(reference, certificate, ZeroHash), Copy(certificate)));
        internal bool Contains(string reference) => _values.ContainsKey(reference);
        internal OwnerCertificateEntry Peek(string reference) => _values[reference].Entry;
        public OwnerCertificateEntry Publish(string reference, X509Certificate2 ownedCertificate, string requestIdentity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Publishes++;
            if (_values.ContainsKey(reference)) throw new OwnerCertificateRepositoryException("certificate_reference_exists");
            var entry = Entry(reference, ownedCertificate, requestIdentity); _values.Add(reference, (entry, Copy(ownedCertificate))); return entry;
        }
        public OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null)
        {
            Reads++;
            if (!_values.TryGetValue(reference, out var value)) throw new OwnerCertificateRepositoryException("certificate_reference_missing");
            if (expectedCertificateId is not null && expectedCertificateId != value.Entry.CertificateId)
                throw new OwnerCertificateRepositoryException("certificate_identity_mismatch");
            return value.Entry;
        }
        public X509Certificate2 Resolve(CertificateKeyReference reference)
        {
            KeyReads++;
            if (!_values.TryGetValue(reference.Reference, out var value) || value.Entry.CertificateId != reference.ExpectedCertificateId)
                throw new OwnerCertificateRepositoryException("certificate_identity_mismatch");
            if (!value.Entry.ProtectedMaterialPresent) throw new OwnerCertificateRepositoryException("certificate_private_key_required");
            return Copy(value.Certificate);
        }
        private static OwnerCertificateEntry Entry(string reference, X509Certificate2 certificate, string identity) =>
            new(1, OwnerSid, reference, CertificateOperations.Inspect(certificate).CertificateId, identity, certificate.RawData, certificate.HasPrivateKey);
        private static X509Certificate2 Copy(X509Certificate2 certificate)
        {
            var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            if (!certificate.HasPrivateKey) return publicOnly;
            using (publicOnly)
            using (var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("ephemeral fixture key missing"))
                return publicOnly.CopyWithPrivateKey(key);
        }
        public void Dispose() { foreach (var value in _values.Values) value.Certificate.Dispose(); _values.Clear(); }
    }
    private sealed class OpenMode : IMaintenanceModeSource
    {
        private readonly object _gate = new();
        private bool _hold, _closed;
        internal void HoldAfterNextRead() { lock (_gate) _hold = true; }
        internal void Open() { lock (_gate) { _hold = false; _closed = false; } }
        public MaintenanceModeRecord? Read()
        {
            lock (_gate)
            {
                var closed = _closed;
                if (_hold) { _hold = false; _closed = true; }
                return closed ? new(1, "exchange-fixture", "active", "fixture-jid", "fixture-rid", "fixture-scope", "fixture-task", OwnerSid, 7, "fixture-service", null, 1) : null;
            }
        }
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }
}
