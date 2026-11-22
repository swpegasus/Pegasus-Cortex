using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Tray;

internal interface ICortexMachineServerCertificateSession : IDisposable
{
    MachineServerCertificateProgress Progress { get; }
    byte[] GetSigningRequest();
    void BindSignedLeaf(MachineServerCertificateSignedLeaf leaf, MachineServerCertificateRequest request, DateTimeOffset now);
    MachineServerCertificateProgress DeleteUnbound();
}

internal interface ICortexMachineServerCertificateFactory
{
    ICortexMachineServerCertificateSession Create(MachineServerCertificateRequest request, Action requireHigh);
}

[SupportedOSPlatform("windows")]
internal static class CortexMachineServerCertificateElevation
{
    internal const string HelperFlag = "--certificate-machine-server-once";

    internal static async Task<int> RunHelperAsync(string ticket, uint parentPid, string parentStart, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await using var context = CortexCertificateStoreElevation.OpenHelperContext(ticket, parentPid, parentStart);
            return await RunOnceAsync(ticket, context.Owner, LocalIdentity.Current, WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity,
                context.Transport, new WindowsFactory(), TimeProvider.System, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 2; }
    }

    internal static async Task<int> RunOnceAsync(string ticket, LocalPeer expectedOwner, Func<LocalPeer> current,
        Func<LocalPeer, CertificateElevationProcessIdentity?> captureHigh, IOperationDispatcher transport,
        ICortexMachineServerCertificateFactory factory, TimeProvider clock, CancellationToken token)
    {
        MachineServerCertificateRequest request;
        CertificateElevationProcessIdentity helper;
        try
        {
            token.ThrowIfCancellationRequested();
            _ = MachineServerCertificateElevationContract.ParseTicket(
                MachineServerCertificateElevationContract.Control(MachineServerCertificateElevationContract.RedeemOperation, ticket));
            helper = CortexCertificateStoreElevation.HighIdentity(current(), expectedOwner, captureHigh) ??
                throw new AgentOpsException("certificate_elevation_owner_denied");
            var redeemed = await transport.DispatchAsync(MachineServerCertificateElevationContract.Control(
                MachineServerCertificateElevationContract.RedeemOperation, ticket), CancellationToken.None).ConfigureAwait(false);
            if (!redeemed.Ok || redeemed.Data is null || !redeemed.Data.TryGetValue("payload", out var value)) return 3;
            var text = value switch
            {
                MachineServerCertificateRequest typed => MachineServerCertificateElevationContract.SerializeRequest(typed),
                JsonElement element => element.GetRawText(), string raw => raw,
                _ => throw new ArgumentException("certificate_machine_payload_invalid")
            };
            request = MachineServerCertificateElevationContract.ParseRequest(text);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 3; }

        ICortexMachineServerCertificateSession? session = null;
        MachineServerCertificateProgress? progress = null;
        var creationEntered = false;
        string? code = null;
        int? native = null;
        try
        {
            RequireHigh();
            creationEntered = true;
            session = factory.Create(request, RequireHigh);
            var csr = session.GetSigningRequest();
            var signed = await transport.DispatchAsync(MachineServerCertificateElevationContract.Control(
                MachineServerCertificateElevationContract.CsrOperation, ticket,
                MachineServerCertificateElevationContract.SerializeCsr(csr)), CancellationToken.None).ConfigureAwait(false);
            if (!signed.Ok || signed.Data is null || !signed.Data.TryGetValue("signedLeaf", out var value))
                throw new AgentOpsException("certificate_machine_signing_unavailable");
            var text = value switch
            {
                MachineServerCertificateSignedLeaf typed => MachineServerCertificateElevationContract.SerializeLeaf(typed),
                JsonElement element => element.GetRawText(), string raw => raw,
                _ => throw new ArgumentException("certificate_machine_leaf_invalid")
            };
            var leaf = MachineServerCertificateElevationContract.ParseLeaf(text);
            MachineServerCertificateElevationContract.ValidateLeaf(leaf, request, clock.GetUtcNow());
            RequireHigh();
            session.BindSignedLeaf(leaf, request, clock.GetUtcNow());
            progress = session.Progress;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            if (failure is MachineServerCertificateSessionException observed)
            { progress = observed.Progress; native = observed.NativeError; code = observed.Code; }
            else
            {
                progress = session?.Progress ?? (creationEntered ? null :
                    new("not-started", false, false, false, false, false, false, false, false));
                code = failure is AgentOpsException operation ? operation.Code :
                    failure is CertificateOperationException certificate ? certificate.Code : "certificate_machine_helper_failed";
            }
            // Cleanup is explicit and restricted to this exact still-unbound
            // session. Any possible store entry permanently forbids this path.
            if (session is not null && progress is { StoreActionIssued: false, CleanupIssued: false })
            {
                try { progress = session.DeleteUnbound(); }
                catch (MachineServerCertificateSessionException cleanup)
                { progress = cleanup.Progress; native ??= cleanup.NativeError; }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
                { progress = progress with { Phase = "unknown", OutcomeUnknown = true, Code = "machine_server_cleanup_unobserved" }; }
            }
        }
        finally
        {
            // The native session's Dispose closes handles only. Process exit is
            // also observed by the parent; it never implies deletion or rollback.
            try { session?.Dispose(); }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { }
        }

        if (progress is { StoreActionIssued: true, PostconditionObserved: false })
            progress = progress with { Phase = "unknown", OutcomeUnknown = true };
        var state = progress is { PostconditionObserved: true, OutcomeUnknown: false } ? "bound" :
            progress is null || progress.OutcomeUnknown ? "uncertain" : "failed";
        var result = new MachineServerCertificateResult(MachineServerCertificateElevationContract.Version, state, progress,
            state == "bound" ? null : code ?? progress?.Code ?? "certificate_machine_helper_failed", state == "bound" ? null : native);
        try
        {
            RequireHigh();
            var completed = await transport.DispatchAsync(MachineServerCertificateElevationContract.Control(
                MachineServerCertificateElevationContract.CompleteOperation, ticket,
                MachineServerCertificateElevationContract.SerializeResult(result)), CancellationToken.None).ConfigureAwait(false);
            return completed.Ok ? 0 : 4;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 4; }

        void RequireHigh()
        {
            if (CortexCertificateStoreElevation.HighIdentity(current(), expectedOwner, captureHigh) != helper)
                throw new AgentOpsException("certificate_elevation_owner_denied");
        }
    }

    private sealed class WindowsFactory : ICortexMachineServerCertificateFactory
    {
        public ICortexMachineServerCertificateSession Create(MachineServerCertificateRequest request, Action requireHigh) =>
            new WindowsSession(MachineServerCertificateSession.Create(request.OperationId, request.Specification,
                new(request.DnsNames, request.IpAddresses), requireHigh, CancellationToken.None));
    }

    private sealed class WindowsSession(MachineServerCertificateSession session) : ICortexMachineServerCertificateSession
    {
        public MachineServerCertificateProgress Progress => session.Progress;
        public byte[] GetSigningRequest() => session.GetSigningRequest();
        public void BindSignedLeaf(MachineServerCertificateSignedLeaf leaf, MachineServerCertificateRequest request, DateTimeOffset now) =>
            session.BindSignedLeaf(leaf.PublicLeafDer, leaf.CertificateId, request.PublicAuthorityDer,
                request.AuthorityCertificateId, now, CancellationToken.None);
        public MachineServerCertificateProgress DeleteUnbound() => session.DeleteUnbound(CancellationToken.None);
        public void Dispose() => session.Dispose();
    }
}
