using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

// The ordinary host owns the single-use ticket. UAC launches only this installed
// image; neither public DER nor a store/action selector appears on the command line.
[SupportedOSPlatform("windows")]
internal sealed class WindowsCertificateElevationLauncher : IOwnerCertificateElevationLauncher, IOwnerMachineCertificateElevationLauncher
{
    private readonly WindowsOwnerElevationLauncher _inner;

    internal WindowsCertificateElevationLauncher(CortexProductLayout layout, LocalPeer ordinaryOwner)
        => _inner = new WindowsOwnerElevationLauncher(layout, ordinaryOwner);

    internal WindowsCertificateElevationLauncher(CortexProductLayout layout, LocalPeer ordinaryOwner,
        Func<LocalPeer> current, Func<CertificateElevationProcessIdentity> parent,
        Action<string, string> requireProtected, Func<ProcessStartInfo, IOwnerCertificateElevationProcess> start)
        => _inner = new WindowsOwnerElevationLauncher(layout, ordinaryOwner, current, parent, requireProtected, start);

    public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token) => _inner.StartAsync(ticket, token);
    public Task<IOwnerCertificateElevationProcess> StartMachineServerAsync(string ticket, CancellationToken token) => _inner.StartMachineServerAsync(ticket, token);
    public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer) => _inner.CaptureElevatedPeer(peer);
}

[SupportedOSPlatform("windows")]
internal static class CortexCertificateStoreElevation
{
    internal static string HostPath(CortexProductLayout layout) => CortexOwnerElevation.HostPath(layout);

    internal static bool CanonicalStart(string start) => CortexOwnerElevation.CanonicalStart(start);

    internal static void RequireTicket(string ticket) => CertificateStoreElevationContract.ParseTicket(Control(CertificateStoreElevationContract.RedeemOperation, ticket));
    private static OperationRequest Control(string op, string ticket, string? body = null) =>
        new(op, new Dictionary<string, string>(StringComparer.Ordinal) { ["ticket"] = ticket }, Body: body);

    internal static async Task<int> RunHelperAsync(string ticket, uint parentPid, string parentStart, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await using var context = OpenHelperContext(ticket, parentPid, parentStart);
            return await RunOnceAsync(ticket, context.Owner, LocalIdentity.Current, WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity,
                context.Transport, new WindowsCertificateStoreBackend(), token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 2; }
    }

    // Compatibility entry for both certificate helpers. Ticket parsing remains
    // domain-owned; fixed image/owner/High/parent admission is shared.
    internal static CertificateHelperContext OpenHelperContext(string ticket, uint parentPid, string parentStart)
    {
        RequireTicket(ticket);
        return new(CortexOwnerElevation.OpenHelperContext(parentPid, parentStart));
    }

    internal sealed class CertificateHelperContext : IAsyncDisposable
    {
        private readonly CortexOwnerElevation.HelperContext _inner;
        internal CertificateHelperContext(LocalPeer owner, NamedPipeDispatcher transport)
            : this(new CortexOwnerElevation.HelperContext(owner, transport)) { }
        internal CertificateHelperContext(CortexOwnerElevation.HelperContext inner) => _inner = inner;
        internal LocalPeer Owner => _inner.Owner;
        internal NamedPipeDispatcher Transport => _inner.Transport;
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    internal static async Task<int> RunOnceAsync(string ticket, LocalPeer expectedOwner, Func<LocalPeer> current,
        Func<LocalPeer, CertificateElevationProcessIdentity?> captureHigh, IOperationDispatcher transport,
        ICertificateStoreBackend backend, CancellationToken token)
    {
        AdminCertificateStoreRequest payload;
        CertificateElevationProcessIdentity helper;
        try
        {
            token.ThrowIfCancellationRequested(); RequireTicket(ticket);
            helper = HighIdentity(current(), expectedOwner, captureHigh) ?? throw new AgentOpsException("certificate_elevation_owner_denied");
            // The ticket may be revoked while UAC is pending. Once this one
            // redemption starts, cancellation must not abandon a delivered
            // payload, its native action, or the single completion report.
            var redeemed = await transport.DispatchAsync(Control(CertificateStoreElevationContract.RedeemOperation, ticket), CancellationToken.None).ConfigureAwait(false);
            if (!redeemed.Ok || redeemed.Data is null || !redeemed.Data.TryGetValue("payload", out var value)) return 3;
            var json = value switch
            {
                AdminCertificateStoreRequest request => CertificateStoreElevationContract.SerializePayload(request),
                JsonElement element => element.GetRawText(),
                string text => text,
                _ => throw new AgentOpsException("certificate_elevation_payload_invalid")
            };
            payload = CertificateStoreElevationContract.ParsePayload(json);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 3; }

        var enteredBackend = false;
        CertificateElevationResult result;
        try
        {
            CertificateStoreElevationContract.ValidatePayload(payload);
            var target = new CertificateStoreTarget(CertificateStoreScope.LocalMachine, Kind(payload.Store));
            var action = payload.Action switch
            {
                "store_add" => CertificateStoreAction.AddPublic,
                "store_trust" => CertificateStoreAction.InstallTrust,
                "store_remove" => CertificateStoreAction.RemovePublic,
                _ => throw new AgentOpsException("certificate_elevation_payload_invalid")
            };
            var admission = new ExactAdmission(target, action, () =>
            {
                if (HighIdentity(current(), expectedOwner, captureHigh) != helper)
                    throw new AgentOpsException("certificate_elevation_owner_denied");
            });
            var operations = new CertificateStoreOperations(new ObservedBackend(backend, () => enteredBackend = true), admission);
            var mutation = payload.Action switch
            {
                "store_add" => operations.AddPublic(target, payload.PublicDer!),
                "store_trust" => operations.InstallTrust(target, payload.PublicDer!),
                _ => operations.RemovePublic(target, payload.CertificateId)
            };
            var expectedState = payload.Action == "store_remove" ? mutation.State is "removed" or "absent" : mutation.State is "added" or "unchanged";
            if (!expectedState || mutation.CertificateId != payload.CertificateId)
                throw new AgentOpsException("certificate_elevation_result_invalid");
            result = new(1, mutation.State, payload.CertificateId, mutation.ActionIssued, true, false);
            CertificateStoreElevationContract.ValidateResult(result, payload.CertificateId);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            // Entering a backend does not prove whether its native mutation was
            // issued. Only that backend's explicit unknown-after-issue code
            // establishes true; every other unobserved backend failure is null.
            var issued = failure is CertificateOperationException { Code: "certificate_store_outcome_unknown" } ? true : (bool?)null;
            result = enteredBackend
                ? new(1, "uncertain", payload.CertificateId, issued, false, true, "certificate_store_elevation_unknown")
                : new(1, "failed", payload.CertificateId, false, false, false,
                    failure is AgentOpsException { Code: "certificate_elevation_owner_denied" } ? "certificate_elevation_owner_denied" : "certificate_store_elevation_refused");
        }
        try
        {
            CertificateStoreElevationContract.ValidateResult(result, payload.CertificateId);
            var completed = await transport.DispatchAsync(Control(CertificateStoreElevationContract.CompleteOperation, ticket,
                CertificateStoreElevationContract.SerializeResult(result)), CancellationToken.None).ConfigureAwait(false);
            // Zero denotes a delivered typed report, including a known refusal.
            // A lost acknowledgement never authorizes another store operation.
            return completed.Ok ? 0 : 4;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 4; }
    }

    internal static CertificateElevationProcessIdentity? HighIdentity(LocalPeer peer, LocalPeer owner,
        Func<LocalPeer, CertificateElevationProcessIdentity?> capture) => CortexOwnerElevation.HighIdentity(peer, owner, capture);

    private static CertificateStoreKind Kind(string value) => value switch
    {
        "personal" => CertificateStoreKind.Personal, "intermediate" => CertificateStoreKind.IntermediateAuthority,
        "root" => CertificateStoreKind.Root, "trusted_publisher" => CertificateStoreKind.TrustedPublisher,
        "trusted_people" => CertificateStoreKind.TrustedPeople,
        _ => throw new AgentOpsException("certificate_elevation_payload_invalid")
    };
    private sealed class ExactAdmission(CertificateStoreTarget target, CertificateStoreAction action, Action high) : ICertificateStoreAdmission
    {
        public void Authorize(CertificateStoreTarget selected, CertificateStoreAction requested)
        {
            high();
            if (selected != target || selected.Scope != CertificateStoreScope.LocalMachine || requested != action)
                throw new AgentOpsException("certificate_elevation_payload_invalid");
        }
    }
    private sealed class ObservedBackend(ICertificateStoreBackend backend, Action entered) : ICertificateStoreBackend
    {
        public IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target) => throw new NotSupportedException();
        public CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] der) { entered(); return backend.AddPublic(target, der); }
        public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string id) { entered(); return backend.RemovePublic(target, id); }
    }
}
