using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

// Injected launcher, peer, transport and machine session. RSA/CSR signing is
// ephemeral; this selector never touches CNG persistence, stores, UAC or a pipe.
[SupportedOSPlatform("windows")]
internal static class CortexMachineServerCertificateElevationTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Start = "2026-11-21T00:00:00.0000000Z";
    private static readonly string Ticket = new('a', 64);
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        await LauncherAsync();
        using var material = new Material();
        await HelperAsync(material);
        Console.WriteLine($"PASS machine certificate helper: {_checks} injected launch/peer/CSR/bind/cleanup checks; no UAC, machine key/store, pipe or service calls");
    }

    private static async Task LauncherAsync()
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(Path.GetTempPath(), "machine-cert-launch-fixture"), Sid);
        var host = CortexCertificateStoreElevation.HostPath(layout);
        var owner = new LocalPeer(Sid, 7, host, 123);
        var child = new FakeProcess(new(456, Start, host));
        ProcessStartInfo? observed = null;
        var launcher = new WindowsCertificateElevationLauncher(layout, owner, () => owner, () => new(123, Start, host),
            (root, image) => Check(root == layout.InstallRoot && image == host, "same protected installed image admission"),
            request => { observed = request; return child; });
        using var retained = await launcher.StartMachineServerAsync(Ticket, default);
        Check(ReferenceEquals(retained, child) && observed is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden } &&
            observed.FileName == host && observed.Arguments.Length == 0 && observed.ArgumentList.SequenceEqual(
                new[] { CortexMachineServerCertificateElevation.HelperFlag, Ticket, "123", Start }),
            "machine helper has only fixed mode capability and parent generation arguments");
        Check(await retained.WaitForExitAsync() == 0, "original fake process wait");
    }

    private static async Task HelperAsync(Material material)
    {
        var owner = new LocalPeer(Sid, 7, @"C:\fixture\PegasusCortex.Host.exe", 456, true);
        CertificateElevationProcessIdentity? Capture(LocalPeer peer) => new(peer.ProcessId!.Value, Start, peer.ApplicationPath!);
        foreach (var mode in new[] { "bound", "before-store", "after-store", "lost-signature", "lost-completion", "unexpected-create", "known-create-refusal" })
        {
            using var session = new FakeSession(material) { Mode = mode };
            var factory = new Factory(session) { Mode = mode };
            await using var transport = new Transport(material) { Mode = mode };
            var exit = await CortexMachineServerCertificateElevation.RunOnceAsync(Ticket, owner, () => owner, Capture,
                transport, factory, TimeProvider.System, default);
            if (mode == "bound")
                Check(exit == 0 && session.Binds == 1 && session.Deletes == 0 && transport.Result is { State: "bound", Progress.PostconditionObserved: true },
                    "observed bind remains successful without cleanup");
            else if (mode is "before-store" or "lost-signature")
                Check(exit == 0 && session.Deletes == 1 && transport.Result is { State: "failed", Progress: { StoreActionIssued: false, CleanupObserved: true } },
                    "known unbound session is explicitly cleaned once");
            else if (mode == "after-store")
                Check(exit == 0 && session.Binds == 1 && session.Deletes == 0 && transport.Result is { State: "uncertain", Progress: { StoreActionIssued: true, OutcomeUnknown: true } },
                    "possible store entry permanently prohibits cleanup");
            else if (mode == "lost-completion")
                Check(exit == 4 && session.Binds == 1 && session.Deletes == 0 && transport.Completes == 1,
                    "lost completion never repeats bind/report or deletes a bound key");
            else if (mode == "unexpected-create")
                Check(exit == 0 && transport.Result is { State: "uncertain", Progress: null } && session.Binds == 0 && session.Deletes == 0,
                    "unobserved create failure cannot invent unissued key/store flags");
            else
                Check(exit == 0 && transport.Result is { State: "failed", Progress.KeyCreationIssued: false } && session.Binds == 0 && session.Deletes == 0,
                    "typed pre-create refusal remains known unissued");
            Check(factory.Creates == 1 && transport.Redeems == 1 && transport.Completes == 1 && transport.Csrs <= 1,
                "one creation and each control phase at most once");
            Check(factory.ReturnedSession ? session.Disposals == 1 : session.Disposals == 0,
                "helper disposes only a returned owned session");
        }
        {
            using var session = new FakeSession(material);
            var factory = new Factory(session);
            await using var transport = new Transport(material) { RawPayload = "{}" };
            Check(await CortexMachineServerCertificateElevation.RunOnceAsync(Ticket, owner, () => owner, Capture,
                transport, factory, TimeProvider.System, default) == 3 && factory.Creates == 0 && transport.Csrs == 0,
                "incomplete payload fails before any key operation");
        }
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            using var session = new FakeSession(material);
            var factory = new Factory(session);
            await using var transport = new Transport(material);
            Check(await CortexMachineServerCertificateElevation.RunOnceAsync(Ticket, owner, () => owner, Capture,
                transport, factory, TimeProvider.System, cancel.Token) == 3 && transport.Redeems == 0 && factory.Creates == 0,
                "pre-redemption cancellation has no key or transport effects");
        }
        {
            using var session = new FakeSession(material);
            var factory = new Factory(session);
            await using var transport = new Transport(material);
            Check(await CortexMachineServerCertificateElevation.RunOnceAsync(Ticket, owner, () => owner with { Elevated = false }, Capture,
                transport, factory, TimeProvider.System, default) == 3 && transport.Redeems == 0 && factory.Creates == 0,
                "ordinary peer cannot run elevated helper effects");
        }
    }

    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }

    private sealed class Material : IDisposable
    {
        private readonly X509Certificate2 _authority;
        internal MachineServerCertificateRequest Request { get; }
        internal Material()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _authority = new CertificateOperations().CreateAuthority(new("Machine helper fixture CA", now.AddMinutes(-2), now.AddDays(3), 2048));
            Request = new(1, Guid.NewGuid(), new("localhost", now.AddMinutes(-1), now.AddDays(1), 2048), ["localhost"], ["127.0.0.1"],
                _authority.RawData, CertificateOperations.Inspect(_authority).CertificateId, new string('b', 64), new string('c', 32), "helper-fixture");
        }
        internal MachineServerCertificateSignedLeaf Sign(byte[] csr)
        {
            using var leaf = new CertificateOperations().IssueServerFromSigningRequest(Request.Specification, _authority,
                new(Request.DnsNames, Request.IpAddresses), csr);
            return new(1, leaf.RawData, CertificateOperations.Inspect(leaf).CertificateId);
        }
        public void Dispose() => _authority.Dispose();
    }

    private sealed class Factory(FakeSession session) : ICortexMachineServerCertificateFactory
    {
        internal string Mode = "bound";
        internal int Creates;
        internal bool ReturnedSession;
        public ICortexMachineServerCertificateSession Create(MachineServerCertificateRequest request, Action requireHigh)
        {
            Creates++; requireHigh();
            if (Mode == "unexpected-create") throw new IOException("private_native_detail_not_for_output");
            if (Mode == "known-create-refusal")
                throw new MachineServerCertificateSessionException("machine_server_administration_denied",
                    new("not-started", false, false, false, false, false, false, false, false));
            Check(request.OperationId != Guid.Empty && request.Specification.RsaBits == 2048, "admitted public request reaches factory");
            session.RequireHigh = requireHigh;
            ReturnedSession = true;
            return session;
        }
    }

    private sealed class FakeSession : ICortexMachineServerCertificateSession
    {
        private readonly RSA _key = RSA.Create(2048);
        internal string Mode = "bound";
        internal int Binds, Deletes, Disposals;
        internal Action RequireHigh = () => { };
        public MachineServerCertificateProgress Progress { get; private set; } = new("csr", true, true, true, false, false, false, false, false);
        internal FakeSession(Material material) { }
        public byte[] GetSigningRequest() => new CertificateRequest("CN=ignored", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        public void BindSignedLeaf(MachineServerCertificateSignedLeaf leaf, MachineServerCertificateRequest request, DateTimeOffset now)
        {
            RequireHigh(); Binds++;
            if (Mode == "before-store") throw new MachineServerCertificateSessionException("machine_server_binding_refused", Progress);
            Progress = Progress with { Phase = "binding", StoreActionIssued = true, CertificateId = leaf.CertificateId };
            if (Mode == "after-store")
            { Progress = Progress with { Phase = "unknown", OutcomeUnknown = true }; throw new MachineServerCertificateSessionException("machine_server_binding_unknown", Progress); }
            Progress = Progress with { Phase = "bound", PostconditionObserved = true };
        }
        public MachineServerCertificateProgress DeleteUnbound()
        {
            Check(!Progress.StoreActionIssued && !Progress.CleanupIssued, "cleanup is exact, once and strictly before store entry");
            Deletes++;
            return Progress = Progress with { Phase = "cleaned", CleanupIssued = true, CleanupObserved = true, OutcomeUnknown = false };
        }
        public void Dispose() { Disposals++; _key.Dispose(); }
    }

    private sealed class Transport(Material material) : IOperationDispatcher
    {
        internal string Mode = "bound";
        internal string? RawPayload;
        internal int Redeems, Csrs, Completes;
        internal MachineServerCertificateResult? Result;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        {
            Check(!token.CanBeCanceled && MachineServerCertificateElevationContract.ParseTicket(request) == Ticket,
                "fixed capability phases remain retained after redemption");
            if (request.Op == MachineServerCertificateElevationContract.RedeemOperation)
            {
                Redeems++;
                return Task.FromResult(Ok(request, new Dictionary<string, object?>
                    { ["payload"] = RawPayload ?? MachineServerCertificateElevationContract.SerializeRequest(material.Request) }));
            }
            if (request.Op == MachineServerCertificateElevationContract.CsrOperation)
            {
                Csrs++;
                var csr = MachineServerCertificateElevationContract.ParseCsr(request);
                if (Mode == "lost-signature") throw new IOException("public_phase_ack_lost");
                return Task.FromResult(Ok(request, new Dictionary<string, object?> { ["signedLeaf"] = material.Sign(csr) }));
            }
            Completes++;
            Result = MachineServerCertificateElevationContract.ParseResult(request);
            if (Mode == "lost-completion") throw new IOException("completion_ack_lost");
            return Task.FromResult(Ok(request));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static OperationReceipt Ok(OperationRequest request, IReadOnlyDictionary<string, object?>? data = null) =>
            new(true, new string('d', 32), "fixture-response", "done", request.Op, Data: data);
    }

    private sealed class FakeProcess(CertificateElevationProcessIdentity identity) : IOwnerCertificateElevationProcess
    {
        public CertificateElevationProcessIdentity Identity => identity;
        public Task<int> WaitForExitAsync() => Task.FromResult(0);
        public void Dispose() { }
    }
}
