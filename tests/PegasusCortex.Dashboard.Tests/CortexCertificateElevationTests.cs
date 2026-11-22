using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

[SupportedOSPlatform("windows")]
internal static class CortexCertificateElevationTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Start = "2026-11-20T18:00:00.0000000Z";
    private static readonly string Ticket = new('a', 64);
    private static int _assertions;

    internal static async Task RunAsync()
    {
        _assertions = 0;
        await LauncherAsync();
        await HelperAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, assertions = _assertions,
            evidence = "injected launch/peer/transport/store fixtures with a new ephemeral certificate; no UAC, credential, store or installed-state calls" }));
    }

    // Explicit read-only platform qualification. This samples identity and the
    // installed image boundary, never redeems a ticket or opens a certificate store.
    internal static int RunNativePreflight(string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath) || !Directory.Exists(Path.GetDirectoryName(outputPath)))
            throw new ArgumentException("An existing absolute output directory is required.");
        PathNormalizer.RejectAbsoluteReparseComponents(outputPath);
        var report = new Dictionary<string, object?>();
        var stage = "current_peer";
        var ok = false;
        try
        {
            var peer = LocalIdentity.Current();
            report["elevated"] = peer.Elevated;
            report["interactiveSession"] = peer.Session > 0;
            stage = "execution_layout";
            var layout = CortexProductLayout.ResolveExecution();
            report["executionLayout"] = true;
            report["ownerMatches"] = layout.OwnerSid == peer.Sid;
            var host = CortexCertificateStoreElevation.HostPath(layout);
            stage = "protected_image";
            CortexPrivilegedCodePath.RequireProtectedImage(layout.InstallRoot, host);
            report["protectedImage"] = true;
            stage = "high_capture";
            var captured = WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity(peer);
            report["highCapture"] = captured is not null;
            report["canonicalStart"] = captured is not null && CortexCertificateStoreElevation.CanonicalStart(captured.Start);
            ok = peer.Elevated && peer.Session > 0 && layout.OwnerSid == peer.Sid && captured is not null &&
                captured.Pid == peer.ProcessId && CortexCertificateStoreElevation.CanonicalStart(captured.Start);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { report["failureStage"] = stage; report["exceptionType"] = failure.GetType().Name; report["hResult"] = failure.HResult; }
        report["ok"] = ok;
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, report);
        return ok ? 0 : 1;
    }

    private static async Task LauncherAsync()
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(Path.GetTempPath(), "ce-launch-fixture"), Sid);
        var host = CortexCertificateStoreElevation.HostPath(layout);
        var owner = new LocalPeer(Sid, 7, host, 123);
        var current = owner;
        var parent = new CertificateElevationProcessIdentity(123, Start, host);
        var child = new FakeProcess(new(456, Start, host));
        ProcessStartInfo? captured = null;
        var launches = 0; var protectedChecks = 0;
        using var canceledDuringStart = new CancellationTokenSource();
        var launcher = new WindowsCertificateElevationLauncher(layout, owner, () => current, () => parent,
            (root, image) => { Check(root == layout.InstallRoot && image == host, "protected check binds exact installed host role"); protectedChecks++; },
            start => { captured = start; launches++; canceledDuringStart.Cancel(); return child; });
        var returned = await launcher.StartAsync(Ticket, canceledDuringStart.Token);
        Check(ReferenceEquals(returned, child) && launches == 1 && canceledDuringStart.IsCancellationRequested,
            "post-issue cancellation preserves the returned original process without retry");
        Check(captured is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden } &&
            captured.FileName == host && captured.WorkingDirectory == Path.GetDirectoryName(host) &&
            captured.Arguments.Length == 0 && captured.ArgumentList.SequenceEqual(new[] { "--certificate-store-once", Ticket, "123", Start }),
            "elevation command has only fixed mode ticket and exact parent generation arguments");
        Check(protectedChecks == 1 && returned.Identity == child.Identity && await returned.WaitForExitAsync() == 0 && child.Waits == 1,
            "original process identity and wait are preserved");
        returned.Dispose();
        Check(child.Disposals == 1, "process ownership is explicit");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(await RefusedAsync(() => launcher.StartAsync(Ticket, canceled.Token)) && launches == 1,
            "canceled-before-launch request has no native start");
        Check(await RefusedAsync(() => launcher.StartAsync("invalid", CancellationToken.None)) && launches == 1,
            "invalid ticket has no native start");
        foreach (var denied in new[] { owner with { Elevated = true }, owner with { Sid = "S-1-5-21-1-2-3-1002" },
            owner with { Session = 8 }, owner with { ApplicationPath = host + ".other" }, owner with { ProcessId = 124 } })
        {
            current = denied;
            Check(await RefusedAsync(() => launcher.StartAsync(Ticket, CancellationToken.None)) && launches == 1,
                "owner session image or process-generation mismatch refuses native start");
        }
        current = owner;
        var unprotected = new WindowsCertificateElevationLauncher(layout, owner, () => current, () => parent,
            (_, _) => throw new UnauthorizedAccessException("fixture_protection_refusal"), _ => throw new InvalidOperationException("unexpected_native_start"));
        Check(await RefusedAsync(() => unprotected.StartAsync(Ticket, CancellationToken.None)), "unprotected executable refuses before native start");
    }

    private static async Task HelperAsync()
    {
        const string host = @"C:\fixture\bin\host\PegasusCortex.Host.exe";
        var owner = new LocalPeer(Sid, 7, host, 456, Elevated: true);
        CertificateElevationProcessIdentity? Capture(LocalPeer peer) => new(peer.ProcessId!.Value, Start, peer.ApplicationPath!);
        var now = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
        using var authority = new CertificateOperations().CreateAuthority(new("One-shot public fixture", now.AddMinutes(-1), now.AddHours(1), 2048));
        var der = CertificateOperations.ExportPublic(authority, PublicCertificateFormat.Der);
        var pin = Convert.ToHexStringLower(SHA256.HashData(der));
        AdminCertificateStoreRequest Payload(string action, string store) => new(1, action, store, pin,
            action == "store_remove" ? null : der, new string('b', 64), new string('c', 32), "elevation-fixture");

        foreach (var (action, store, kind) in new[] { ("store_add", "personal", CertificateStoreKind.Personal),
            ("store_trust", "root", CertificateStoreKind.Root), ("store_remove", "trusted_publisher", CertificateStoreKind.TrustedPublisher) })
        {
            var payload = Payload(action, store);
            await using var transport = new FakeTransport(payload);
            var backend = new FakeBackend(pin);
            var exit = await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, CancellationToken.None);
            Check(exit == 0 && transport.Redeems == 1 && transport.Completions == 1 && backend.Calls == 1 &&
                backend.Target == new CertificateStoreTarget(CertificateStoreScope.LocalMachine, kind), "one redeemed action reaches only its exact machine target");
            var result = transport.Result!;
            Check(result.CertificateId == pin && result.ActionIssued == true && result.PostconditionObserved && !result.OutcomeUnknown &&
                result.State == (action == "store_remove" ? "removed" : "added"), "typed observed result is pinned and action-correlated");
            Check(action == "store_remove" ? backend.RemovedId == pin : backend.Der!.AsSpan().SequenceEqual(der),
                "backend receives only exact public DER or exact removal pin");
        }

        foreach (var malformed in new[]
        {
            CertificateStoreElevationContract.SerializePayload(Payload("store_add", "personal")).Replace("\"certificateId\":\"" + pin, "\"certificateId\":\"" + new string('0', 64), StringComparison.Ordinal),
            CertificateStoreElevationContract.SerializePayload(Payload("store_add", "personal")).Replace("\"store\":\"personal\"", "\"store\":\"root\"", StringComparison.Ordinal),
            CertificateStoreElevationContract.SerializePayload(Payload("store_remove", "root")).Insert(1, "\"executable\":\"untrusted.exe\",")
        })
        {
            await using var transport = new FakeTransport(Payload("store_add", "personal")) { RawPayload = malformed };
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, CancellationToken.None) == 3 &&
                backend.Calls == 0 && transport.Completions == 0, "malformed pin action target or extra executable refuses before stores");
        }
        foreach (var denied in new[] { owner with { Elevated = false }, owner with { Session = 0 }, owner with { Sid = "S-1-5-18" },
            owner with { ApplicationPath = host + ".other" } })
        {
            await using var transport = new FakeTransport(Payload("store_remove", "personal"));
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => denied, Capture, transport, backend, CancellationToken.None) == 3 &&
                transport.Redeems == 0 && backend.Calls == 0, "invalid High owner refuses before payload redemption");
        }
        {
            var current = owner;
            await using var transport = new FakeTransport(Payload("store_remove", "personal")) { AfterRedeem = () => current = owner with { Elevated = false } };
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => current, Capture, transport, backend, CancellationToken.None) == 0 &&
                backend.Calls == 0 && transport.Result is { State: "failed", ActionIssued: false, OutcomeUnknown: false, Code: "certificate_elevation_owner_denied" },
                "High admission is resampled after redemption and known refusal is reported");
        }
        {
            using var cancel = new CancellationTokenSource();
            await using var transport = new FakeTransport(Payload("store_remove", "personal")) { AfterRedeem = cancel.Cancel };
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, cancel.Token) == 0 &&
                cancel.IsCancellationRequested && backend.Calls == 1 && transport.Completions == 1,
                "cancellation after redemption does not abandon native action or completion");
        }
        foreach (var explicitIssued in new[] { false, true })
        {
            await using var transport = new FakeTransport(Payload("store_remove", "personal"));
            var backend = new FakeBackend(pin) { Failure = explicitIssued
                ? new CertificateOperationException("certificate_store_outcome_unknown") : new IOException("unreportable_fixture_detail") };
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, CancellationToken.None) == 0 &&
                backend.Calls == 1 && transport.Result is { State: "uncertain", PostconditionObserved: false, OutcomeUnknown: true } &&
                transport.Result.ActionIssued == (explicitIssued ? true : (bool?)null) && transport.Result.Code == "certificate_store_elevation_unknown",
                "backend failure preserves issued versus unknown distinction without raw diagnostic output");
        }
        {
            await using var transport = new FakeTransport(Payload("store_remove", "personal"));
            var backend = new FakeBackend(new string('d', 64));
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, CancellationToken.None) == 0 &&
                backend.Calls == 1 && transport.Result is { State: "uncertain", ActionIssued: null, OutcomeUnknown: true } &&
                transport.Result.CertificateId == pin, "unexpected backend certificate identity cannot become a successful pinned receipt");
        }
        {
            await using var transport = new FakeTransport(Payload("store_remove", "personal")) { FailCompletion = true };
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, CancellationToken.None) == 4 &&
                backend.Calls == 1 && transport.Completions == 1, "lost completion acknowledgement never repeats the native action or report");
        }
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await using var transport = new FakeTransport(Payload("store_remove", "personal"));
            var backend = new FakeBackend(pin);
            Check(await CortexCertificateStoreElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, backend, cancel.Token) == 3 &&
                transport.Redeems == 0 && backend.Calls == 0, "canceled-before-redemption helper has no payload or store access");
        }
    }

    private static async Task<bool> RefusedAsync(Func<Task<IOwnerCertificateElevationProcess>> call)
    {
        try { using var unexpected = await call(); return false; }
        catch (Exception failure) when (failure is AgentOpsException or CertificateElevationContractException or OperationCanceledException or UnauthorizedAccessException) { return true; }
    }
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException(message); }
    private sealed class FakeProcess(CertificateElevationProcessIdentity identity) : IOwnerCertificateElevationProcess
    {
        internal int Waits, Disposals;
        public CertificateElevationProcessIdentity Identity => identity;
        public Task<int> WaitForExitAsync() { Waits++; return Task.FromResult(0); }
        public void Dispose() => Disposals++;
    }
    private sealed class FakeTransport(AdminCertificateStoreRequest payload) : IOperationDispatcher
    {
        internal int Redeems, Completions;
        internal string? RawPayload;
        internal bool FailCompletion;
        internal Action? AfterRedeem;
        internal CertificateElevationResult? Result;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        {
            Check(!token.CanBeCanceled && CertificateStoreElevationContract.ParseTicket(request) == Ticket,
                "helper control request is fixed-ticket and nonabandoning");
            if (request.Op == CertificateStoreElevationContract.RedeemOperation)
            {
                Redeems++; AfterRedeem?.Invoke();
                return Task.FromResult(new OperationReceipt(true, payload.Jid, payload.Rid, "done", request.Op,
                    Data: new Dictionary<string, object?> { ["payload"] = RawPayload ?? CertificateStoreElevationContract.SerializePayload(payload) }));
            }
            Completions++;
            Result = CertificateStoreElevationContract.ParseCompletion(request, payload.CertificateId);
            if (FailCompletion) throw new IOException("unreportable_completion_detail");
            return Task.FromResult(new OperationReceipt(true, payload.Jid, payload.Rid, "done", request.Op));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeBackend(string pin) : ICertificateStoreBackend
    {
        internal int Calls;
        internal CertificateStoreTarget? Target;
        internal byte[]? Der;
        internal string? RemovedId;
        internal Exception? Failure;
        public IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target) => throw new InvalidOperationException("unexpected_inventory");
        public CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] der)
        { Calls++; Target = target; Der = der.ToArray(); if (Failure is not null) throw Failure; return new("added", pin, true); }
        public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string id)
        { Calls++; Target = target; RemovedId = id; if (Failure is not null) throw Failure; return new("removed", pin, true); }
    }
}
