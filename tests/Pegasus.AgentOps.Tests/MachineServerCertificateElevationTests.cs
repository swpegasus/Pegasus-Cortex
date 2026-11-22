using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// In-memory coordinator, fake child/OS-peer observations and ephemeral RSA CSR
// signing plus owned temporary SQLite only. No UAC, child process, pipe,
// persisted key container, certificate store or installed database.
internal static class MachineServerCertificateElevationTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Host = @"C:\fixture\PegasusCortex.Host.exe";
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        using var material = new Material();
        Contract(material);
        await HappyAndAdmissionAsync(material);
        await SigningLifetimeAsync(material);
        await ReentrantSigningExitAsync(material);
        await ConsumedPhaseAsync(material);
        await CancelAndSharedCapacityAsync(material);
        await EngineWiringAsync(material);
        await EngineWiringAsync(material, loseReport: true);
        Console.WriteLine($"PASS machine certificate elevation: {_checks} contract/peer/phase/retained-callback and owned-SQLite Engine wiring checks; fake elevation/repository, no UAC, process, key/store or service calls");
    }

    private static void Contract(Material material)
    {
        var request = material.Request();
        var text = MachineServerCertificateElevationContract.SerializeRequest(request);
        var copy = MachineServerCertificateElevationContract.ParseRequest(text);
        Check(copy.OperationId == request.OperationId && copy.PublicAuthorityDer.SequenceEqual(request.PublicAuthorityDer), "public request round trip");
        Reject(() => MachineServerCertificateElevationContract.ParseRequest(text[..^1] + ",\"provider\":\"other\"}"));
        Reject(() => MachineServerCertificateElevationContract.ParseRequest(text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
        Reject(() => MachineServerCertificateElevationContract.Snapshot(request with { OperationId = Guid.Empty }));
        Reject(() => MachineServerCertificateElevationContract.Snapshot(request with { AuthorityCertificateId = new string('0', 64) }));
        Reject(() => MachineServerCertificateElevationContract.Snapshot(request with { DnsNames = ["*"] }));
        Reject(() => MachineServerCertificateElevationContract.ParseTicket(Control(MachineServerCertificateElevationContract.RedeemOperation, new string('a', 64)) with { Root = "ABCD" }));
        var unknown = MachineServerCertificateElevationContract.Failed("helper_lost", payloadIssued: true);
        Check(unknown is { State: "uncertain", Progress: null }, "lost evidence does not invent unissued flags");
        MachineServerCertificateElevationContract.ValidateResult(unknown);
        Reject(() => MachineServerCertificateElevationContract.ValidateResult(unknown with { State = "bound" }));
    }

    private static async Task HappyAndAdmissionAsync(Material material)
    {
        var launcher = new Launcher();
        using var coordinator = Coordinator(launcher);
        using var observer = new CancellationTokenSource();
        var issued = 0; var signatures = 0;
        var request = material.Request();
        var begin = coordinator.BeginMachineServerAsync(request, (csr, token) =>
        {
            Check(!token.CanBeCanceled, "accepted signer is retained independently of observer cancellation");
            signatures++;
            return Task.FromResult(material.Sign(csr));
        }, () => issued++, observer.Token);
        var child = launcher.Children.Single();
        var peer = Peer(child);
        var ticket = launcher.Tickets.Single();
        foreach (var wrong in new[] { peer with { Elevated = false }, peer with { Sid = "S-1-5-18" },
            peer with { Session = 8 }, peer with { ProcessId = peer.ProcessId + 1 }, peer with { ApplicationPath = @"C:\fixture\other.exe" } })
            Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), wrong, default)).Ok && issued == 0,
                "wrong peer cannot consume the machine payload");
        launcher.Capture = child.Identity with { Start = "changed-generation" };
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default)).Ok, "PID reuse refuses");
        launcher.Capture = null;
        Check(!(await coordinator.HandleAsync(new(CertificateStoreElevationContract.RedeemOperation,
            new Dictionary<string, string> { ["ticket"] = ticket }), peer, default)).Ok, "store phase cannot use a machine capability");
        var redeem = await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default);
        Check(redeem.Ok && issued == 1 && redeem.Data!["payload"] is MachineServerCertificateRequest && launcher.StoreStarts == 0 && launcher.MachineStarts == 1,
            "one fixed machine launch and one persisted payload");
        observer.Cancel();
        Check(!begin.IsCompleted, "postpayload observer cancellation cannot release the original operation");
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default)).Ok && issued == 1, "redeem once");
        var signed = await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
            MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), peer, default);
        Check(signed.Ok && signatures == 1 && signed.Data!["signedLeaf"] is MachineServerCertificateSignedLeaf, "one signed public leaf");
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
            MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), peer, default)).Ok && signatures == 1, "CSR replay cannot sign again");
        var leaf = (MachineServerCertificateSignedLeaf)signed.Data!["signedLeaf"]!;
        var progress = new MachineServerCertificateProgress("bound", true, true, true, true, true, false, false, false, leaf.CertificateId);
        var complete = await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CompleteOperation, ticket,
            MachineServerCertificateElevationContract.SerializeResult(new(1, "bound", progress))), peer, default);
        Check(complete.Ok && !begin.IsCompleted, "completion report does not bypass original process exit");
        child.Exit(0);
        var outcome = await begin.WaitAsync(TimeSpan.FromSeconds(3));
        Check(outcome.Result.State == "bound" && outcome.PayloadIssued && outcome.Exit == 0 && child.Disposals == 1 && !child.DisposedWhileRunning,
            "authoritative result and exact natural exit retained");
    }

    private static async Task SigningLifetimeAsync(Material material)
    {
        var launcher = new Launcher();
        using var coordinator = Coordinator(launcher);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var begin = coordinator.BeginMachineServerAsync(material.Request(), async (csr, token) =>
        { calls++; callbackEntered.SetResult(); await callbackRelease.Task; return material.Sign(csr); }, () => { }, default);
        var child = launcher.Children.Single(); var ticket = launcher.Tickets.Single(); var peer = Peer(child);
        Check((await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default)).Ok, "drain fixture redeemed");
        var sign = coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
            MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), peer, default);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        child.Exit(7);
        // This fake signals the original exit inline, so the coordinator reaches
        // its callback drain before Exit returns; no scheduling sleep or retry.
        Check(!begin.IsCompleted && child.Disposals == 0 && calls == 1, "helper exit cannot release a still-running signer/CA owner");
        callbackRelease.SetResult();
        Check((await sign.WaitAsync(TimeSpan.FromSeconds(3))).Ok, "original callback settles without another signature");
        var outcome = await begin.WaitAsync(TimeSpan.FromSeconds(3));
        Check(outcome is { PayloadIssued: true, Exit: 7, Result.State: "uncertain", Result.Progress: null } && child.Disposals == 1,
            "lost helper report remains unknown only after actual callback drain");
    }

    private static async Task ReentrantSigningExitAsync(Material material)
    {
        var launcher = new Launcher();
        using var coordinator = Coordinator(launcher);
        Task<MachineServerCertificateElevationOutcome>? begin = null;
        begin = coordinator.BeginMachineServerAsync(material.Request(), (csr, _) =>
        {
            var original = launcher.Children.Single();
            // Deliberately reenter exit observation before this synchronous
            // callback returns its Task, rather than after SignAsync returns.
            original.Exit(7);
            Check(!begin!.IsCompleted && original.Disposals == 0,
                "reentrant original exit released the signer before ownership was published");
            return Task.FromResult(material.Sign(csr));
        }, () => { }, default);
        var child = launcher.Children.Single();
        var ticket = launcher.Tickets.Single();
        Check((await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket),
            Peer(child), default)).Ok, "reentrant signer fixture redeemed");
        Check((await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
            MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), Peer(child), default)).Ok,
            "reentrant signer finished its one accepted callback");
        var outcome = await begin.WaitAsync(TimeSpan.FromSeconds(3));
        Check(outcome is { PayloadIssued: true, Exit: 7, Result.State: "uncertain", Result.Progress: null } && child.Disposals == 1,
            "reentrant exit reports unknown only after its original callback settled");
    }

    private static async Task ConsumedPhaseAsync(Material material)
    {
        var launcher = new Launcher();
        using var coordinator = Coordinator(launcher);
        var calls = 0;
        var begin = coordinator.BeginMachineServerAsync(material.Request(), (csr, token) =>
        { calls++; return Task.FromResult(material.Sign(csr)); }, () => { }, default);
        var child = launcher.Children.Single(); var peer = Peer(child); var ticket = launcher.Tickets.Single();
        _ = await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default);
        var bad = Control(MachineServerCertificateElevationContract.CsrOperation, ticket, "{}");
        Check(!(await coordinator.HandleAsync(bad, peer, default)).Ok && calls == 0, "first malformed CSR consumed before signing");
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
            MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), peer, default)).Ok && calls == 0, "malformed phase cannot be replaced");
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CompleteOperation, ticket, "{}"), peer, default)).Ok,
            "malformed completion consumed");
        var known = new MachineServerCertificateResult(1, "failed",
            new("cleaned", true, true, true, false, false, true, true, false), "signing_refused");
        Check(!(await coordinator.HandleAsync(Control(MachineServerCertificateElevationContract.CompleteOperation, ticket,
            MachineServerCertificateElevationContract.SerializeResult(known)), peer, default)).Ok, "completion cannot be replaced");
        child.Exit(0);
        Check((await begin.WaitAsync(TimeSpan.FromSeconds(3))).Result is { State: "uncertain", Progress: null }, "invalid first completion stays unknown");
    }

    private static async Task CancelAndSharedCapacityAsync(Material material)
    {
        var declined = new Launcher { Failure = new Win32Exception(1223) };
        using (var coordinator = Coordinator(declined))
        {
            var outcome = await coordinator.BeginMachineServerAsync(material.Request(), (csr, token) => throw new InvalidOperationException(), () => throw new InvalidOperationException(), default);
            Check(!outcome.PayloadIssued && outcome.Result is { State: "failed", Code: "certificate_elevation_declined" }, "UAC decline is clean and does not create key");
        }
        var launcher = new Launcher();
        using var shared = Coordinator(launcher);
        using var cancel = new CancellationTokenSource();
        var storePayload = new AdminCertificateStoreRequest(1, "store_add", "personal", material.AuthorityId,
            material.Authority.RawData, new string('1', 64), new string('2', 32), "store-fixture");
        var stores = Enumerable.Range(0, 3).Select(_ => shared.BeginAsync(storePayload, () => { }, cancel.Token)).ToArray();
        var machine = shared.BeginMachineServerAsync(material.Request(), (csr, token) => Task.FromResult(material.Sign(csr)), () => { }, cancel.Token);
        var refused = await shared.BeginMachineServerAsync(material.Request(), (csr, token) => throw new InvalidOperationException(), () => { }, default);
        Check(refused.Result.Code == "certificate_elevation_busy" && launcher.Children.Count == 4 && launcher.StoreStarts == 3 && launcher.MachineStarts == 1,
            "store and machine flows share one four-slot registry");
        cancel.Cancel();
        Check(!(await machine.WaitAsync(TimeSpan.FromSeconds(3))).PayloadIssued && (await Task.WhenAll(stores)).All(x => !x.PayloadIssued),
            "unredeemed cancellation releases observations without effects");
        foreach (var child in launcher.Children) child.Exit(0);
        foreach (var child in launcher.Children) await child.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(launcher.Children.All(p => p.Disposals == 1 && !p.DisposedWhileRunning), "canceled original children still reaped naturally");
    }

    private static async Task EngineWiringAsync(Material material, bool loseReport = false)
    {
        using var root = new TempRoot();
        var state = root.Dir("machine-engine");
        var launcher = new Launcher();
        using var coordinator = Coordinator(launcher);
        var repository = new EngineRepository(material.Authority);
        await using var engine = new AgentOpsEngine(state, null, false, null, false, null,
            maintenanceMode: new OpenMode(), userHostAdmission: new UserHostAdmission());
        engine.BindCertificateOwner(new(Sid, () => new(Sid, 7), () => repository,
            (_, _) => throw new InvalidOperationException("machine issuance cannot construct Authenticode")));
        engine.BindCertificateStoreElevation(coordinator);
        var body = JsonSerializer.Serialize(new
        {
            version = 1, reference = "machine-leaf", subject = "localhost", days = 1, keyBits = 2048,
            issuer = new { reference = "fixture-ca", certificateId = material.AuthorityId },
            dnsNames = new[] { "localhost" }, ipAddresses = new[] { "127.0.0.1" }
        });
        var request = new OperationRequest("certificate", new Dictionary<string, string> { ["op"] = "issue_machine_server" },
            "machine-engine-wiring", Body: body);
        var pending = engine.ExecuteAsync(request);
        var child = await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = Peer(child); var ticket = launcher.Tickets.Single();
        try
        {
            var redeemed = await engine.HandleCertificateElevationAsync(Control(MachineServerCertificateElevationContract.RedeemOperation, ticket), peer, default);
            Check(redeemed.Ok && redeemed.Data?["payload"] is MachineServerCertificateRequest, "public Engine action reaches machine coordinator");
            if (loseReport)
            {
                child.Exit(7);
                var unknown = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!unknown.Ok && unknown.State == "recovery_required" && unknown.Partial && repository.Publishes == 0 &&
                    unknown.Data?.ContainsKey("key_creation_issued") == false && unknown.Data?.ContainsKey("store_action_issued") == false,
                    "missing helper report must not invent unissued key/store flags");
                var retained = await engine.ExecuteAsync(request);
                Check(retained.Jid == unknown.Jid && launcher.MachineStarts == 1 && repository.Publishes == 0 &&
                    retained.Data?.ContainsKey("key_creation_issued") == false && retained.Data?.ContainsKey("store_action_issued") == false,
                    "retained missing report remains unknown without replay or fabricated flags");
                return;
            }
            var signed = await engine.HandleCertificateElevationAsync(Control(MachineServerCertificateElevationContract.CsrOperation, ticket,
                MachineServerCertificateElevationContract.SerializeCsr(material.Csr)), peer, default);
            Check(signed.Ok && signed.Data?["signedLeaf"] is MachineServerCertificateSignedLeaf && repository.Publishes == 1 && repository.PublicOnly,
                "Engine signs CSR and publishes only public leaf under owner reference");
            var leaf = (MachineServerCertificateSignedLeaf)signed.Data!["signedLeaf"]!;
            var completion = new MachineServerCertificateResult(1, "bound",
                new("bound", true, true, true, true, true, false, false, false, leaf.CertificateId));
            Check((await engine.HandleCertificateElevationAsync(Control(MachineServerCertificateElevationContract.CompleteOperation, ticket,
                MachineServerCertificateElevationContract.SerializeResult(completion)), peer, default)).Ok, "Engine admits exact helper completion");
            child.Exit(0);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Ok && result.State == "done" && result.Op == "certificate_issue_machine_server" &&
                result.Data?.GetValueOrDefault("certificateId") is string id && id == leaf.CertificateId &&
                !result.Partial && repository.Publishes == 1, "Engine returns compact successful machine issuance receipt");
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == result.Jid && launcher.MachineStarts == 1 && repository.Publishes == 1,
                "same RID returns original receipt without second launch or publication");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(state, "agentops.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT receipt,request FROM jobs WHERE rid=$rid";
            command.Parameters.AddWithValue("$rid", request.Rid);
            using var reader = command.ExecuteReader();
            Check(reader.Read(), "owned durable Engine row exists");
            using var storedReceipt = JsonDocument.Parse(reader.GetString(0));
            var safeData = storedReceipt.RootElement.GetProperty("data");
            Check(safeData.GetProperty("key_creation_issued").GetBoolean() && safeData.GetProperty("key_finalized").GetBoolean() &&
                safeData.GetProperty("key_security_observed").GetBoolean() && safeData.GetProperty("store_action_issued").GetBoolean() &&
                !safeData.GetProperty("cleanup_issued").GetBoolean() && !safeData.GetProperty("cleanup_observed").GetBoolean(),
                "durable receipt retains observed machine key/store/cleanup evidence");
            var stored = reader.GetString(0) + (reader.IsDBNull(1) ? "" : reader.GetString(1));
            Check(!stored.Contains(ticket, StringComparison.Ordinal) && !stored.Contains(body, StringComparison.Ordinal) &&
                !stored.Contains(Convert.ToBase64String(material.Csr), StringComparison.Ordinal) &&
                !stored.Contains(Convert.ToBase64String(leaf.PublicLeafDer), StringComparison.Ordinal) &&
                !stored.Contains(Convert.ToBase64String(material.Authority.RawData), StringComparison.Ordinal) &&
                !stored.Contains("signingRequest", StringComparison.Ordinal) && !stored.Contains("publicLeafDer", StringComparison.Ordinal),
                "durable row contains no capability, CSR, certificate DER or request body");
        }
        finally
        {
            child.Exit(0);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static OwnerCertificateStoreElevation Coordinator(Launcher launcher) => new(Sid, 7, Host, launcher);
    private static LocalPeer Peer(Child child) => new(Sid, 7, Host, child.Identity.Pid, true);
    private static OperationRequest Control(string op, string ticket, string? body = null) => MachineServerCertificateElevationContract.Control(op, ticket, body);
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or CertificateOperationException) { _checks++; return; } throw new InvalidOperationException("invalid contract accepted"); }

    private sealed class Material : IDisposable
    {
        internal X509Certificate2 Authority { get; }
        internal string AuthorityId { get; }
        internal CertificateSpec Specification { get; }
        private readonly RSA _key = RSA.Create(2048);
        internal byte[] Csr { get; }
        internal Material()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Authority = new CertificateOperations().CreateAuthority(new("Machine coordinator fixture", now.AddMinutes(-2), now.AddDays(3), 2048));
            AuthorityId = CertificateOperations.Inspect(Authority).CertificateId;
            Specification = new("localhost", now.AddMinutes(-1), now.AddDays(1), 2048);
            Csr = new CertificateRequest("CN=ignored-request-subject", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        }
        internal MachineServerCertificateRequest Request() => new(1, Guid.NewGuid(), Specification, ["localhost"], ["127.0.0.1"],
            Authority.RawData, AuthorityId, new string('a', 64), Guid.NewGuid().ToString("N"), "machine-fixture");
        internal MachineServerCertificateSignedLeaf Sign(ReadOnlyMemory<byte> csr)
        {
            using var leaf = new CertificateOperations().IssueServerFromSigningRequest(Specification, Authority,
                new(["localhost"], ["127.0.0.1"]), csr.Span);
            Check(!leaf.HasPrivateKey, "CSR signer returns public-only leaf");
            return new(1, leaf.RawData, CertificateOperations.Inspect(leaf).CertificateId);
        }
        public void Dispose() { _key.Dispose(); Authority.Dispose(); }
    }

    private sealed class Launcher : IOwnerCertificateElevationLauncher, IOwnerMachineCertificateElevationLauncher
    {
        internal int StoreStarts, MachineStarts;
        internal Exception? Failure;
        internal CertificateElevationProcessIdentity? Capture;
        internal List<Child> Children { get; } = [];
        internal List<string> Tickets { get; } = [];
        internal TaskCompletionSource<Child> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token)
        { StoreStarts++; return Start(ticket, token); }
        public Task<IOwnerCertificateElevationProcess> StartMachineServerAsync(string ticket, CancellationToken token)
        { MachineStarts++; return Start(ticket, token); }
        private Task<IOwnerCertificateElevationProcess> Start(string ticket, CancellationToken token)
        {
            Check(!token.CanBeCanceled, "original child launch remains retained");
            if (Failure is not null) return Task.FromException<IOwnerCertificateElevationProcess>(Failure);
            var child = new Child(new((uint)(100 + Children.Count), "2026-11-21T00:00:00.0000000Z", Host));
            Children.Add(child); Tickets.Add(ticket);
            Started.TrySetResult(child);
            return Task.FromResult<IOwnerCertificateElevationProcess>(child);
        }
        public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer) => Capture ?? Children.Single(p => p.Identity.Pid == peer.ProcessId).Identity;
    }

    private sealed class Child(CertificateElevationProcessIdentity identity) : IOwnerCertificateElevationProcess
    {
        private readonly TaskCompletionSource<int> _exit = new();
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        internal bool DisposedWhileRunning;
        public CertificateElevationProcessIdentity Identity => identity;
        public Task<int> WaitForExitAsync() => _exit.Task;
        internal void Exit(int code) => _exit.TrySetResult(code);
        public void Dispose() { Disposals++; DisposedWhileRunning |= !_exit.Task.IsCompleted; Disposed.TrySetResult(); }
    }

    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }

    private sealed class EngineRepository(X509Certificate2 authority) : IOwnerCertificateRepository
    {
        internal int Publishes;
        internal bool PublicOnly;
        private OwnerCertificateEntry? _published;
        public X509Certificate2 Resolve(CertificateKeyReference reference)
        {
            Check(reference.Reference == "fixture-ca" && reference.ExpectedCertificateId == CertificateOperations.Inspect(authority).CertificateId,
                "Engine resolves only exact admitted CA reference/pin");
            using var certificate = X509CertificateLoader.LoadCertificate(authority.RawData);
            using var key = authority.GetRSAPrivateKey() ?? throw new InvalidOperationException("fixture authority key missing");
            return certificate.CopyWithPrivateKey(key);
        }
        public OwnerCertificateEntry Publish(string reference, X509Certificate2 certificate, string identity, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Check(reference == "machine-leaf" && _published is null, "one create-only public leaf reference");
            Publishes++; PublicOnly = !certificate.HasPrivateKey;
            return _published = new(1, Sid, reference, CertificateOperations.Inspect(certificate).CertificateId, identity, certificate.RawData, false);
        }
        public OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null) =>
            _published is { } entry && reference == entry.Reference && (expectedCertificateId is null || expectedCertificateId == entry.CertificateId)
                ? entry with { PublicDer = entry.PublicDer.ToArray() } : throw new OwnerCertificateRepositoryException("certificate_reference_missing");
    }
}
