using System.Security.Cryptography;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Data-only compatibility names. Actual authority remains the domain-bound
// capability and the original, OS-observed High process in OwnerElevationSession.
internal sealed record CertificateElevationProcessIdentity(uint Pid, string Start, string ApplicationPath);
internal interface IOwnerCertificateElevationProcess : IDisposable
{
    CertificateElevationProcessIdentity Identity { get; }
    Task<int> WaitForExitAsync();
}
internal interface IOwnerCertificateElevationLauncher
{
    Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token);
    CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer);
}
internal interface IOwnerMachineCertificateElevationLauncher
{
    Task<IOwnerCertificateElevationProcess> StartMachineServerAsync(string ticket, CancellationToken token);
}
internal sealed record CertificateElevationOutcome(CertificateElevationResult Result, bool PayloadIssued,
    CertificateElevationProcessIdentity? Process = null, int? Exit = null);

// The two existing certificate wire contracts stay here. Only their capability,
// exact peer and original-child lifetime is shared with other fixed purposes.
internal sealed class OwnerCertificateStoreElevation : IDisposable
{
    private readonly OwnerElevationSession _session;
    private readonly bool _ownsSession;
    private bool _disposed;

    internal OwnerCertificateStoreElevation(string ownerSid, int session, string expectedHost,
        IOwnerCertificateElevationLauncher launcher, TimeProvider? clock = null)
        : this(new OwnerElevationSession(ownerSid, session, expectedHost, launcher, clock), ownsSession: true) { }

    internal OwnerCertificateStoreElevation(OwnerElevationSession shared) : this(shared, ownsSession: false) { }

    private OwnerCertificateStoreElevation(OwnerElevationSession shared, bool ownsSession)
    { _session = shared ?? throw new ArgumentNullException(nameof(shared)); _ownsSession = ownsSession; }

    internal async Task<CertificateElevationOutcome> BeginAsync(AdminCertificateStoreRequest payload,
        Action payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(payloadIssued);
        return await BeginAsync(payload, _ => payloadIssued(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CertificateElevationOutcome> BeginAsync(AdminCertificateStoreRequest payload,
        Action<CertificateElevationProcessIdentity> payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(payloadIssued);
        var flow = new StoreFlow(Snapshot(payload));
        if (cancellationToken.IsCancellationRequested) return new(Failed(flow.Payload, "certificate_elevation_canceled"), false);
        if (_disposed) return new(Failed(flow.Payload, "certificate_elevation_unavailable"), false);
        var observed = await _session.BeginAsync(flow, payloadIssued, cancellationToken).ConfigureAwait(false);
        var result = flow.Completion ?? (observed.PayloadIssued ? Unknown(flow.Payload, observed.Code) :
            Failed(flow.Payload, observed.Code, observed.NativeError));
        return new(result, observed.PayloadIssued, observed.Process, observed.Exit);
    }

    internal async Task<MachineServerCertificateElevationOutcome> BeginMachineServerAsync(MachineServerCertificateRequest request,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<MachineServerCertificateSignedLeaf>> signAndPublish,
        Action payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(payloadIssued);
        return await BeginMachineServerAsync(request, signAndPublish, _ => payloadIssued(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<MachineServerCertificateElevationOutcome> BeginMachineServerAsync(MachineServerCertificateRequest request,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<MachineServerCertificateSignedLeaf>> signAndPublish,
        Action<CertificateElevationProcessIdentity> payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(payloadIssued);
        var flow = new MachineFlow(new OwnerMachineServerCertificateFlow(request, signAndPublish, _session.Clock));
        if (cancellationToken.IsCancellationRequested)
            return new(MachineServerCertificateElevationContract.Failed("certificate_elevation_canceled"), false);
        if (_disposed) return new(MachineServerCertificateElevationContract.Failed("certificate_elevation_unavailable"), false);
        var observed = await _session.BeginAsync(flow, payloadIssued, cancellationToken).ConfigureAwait(false);
        var result = flow.Domain.Outcome(observed.PayloadIssued, observed.Code);
        if (observed.NativeError is { } native) result = result with { NativeError = native };
        return new(result, observed.PayloadIssued, observed.Process, observed.Exit);
    }

    internal Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer, CancellationToken cancellationToken)
    {
        if (request is null || peer is null) return Task.FromResult(Refused("invalid"));
        var machine = MachineServerCertificateElevationContract.IsControlRequest(request);
        try
        {
            // Preserve original public-store/redeem envelope admission order.
            // Authenticated completion/CSR parsing remains domain-owned so its
            // first malformed attempt is still consumed after peer admission.
            if (machine && request.Op == MachineServerCertificateElevationContract.RedeemOperation)
                _ = MachineServerCertificateElevationContract.ParseTicket(request);
            else if (!machine && request.Op != CertificateStoreElevationContract.CompleteOperation)
                _ = CertificateStoreElevationContract.ParseTicket(request);
        }
        catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException)
        { return Task.FromResult(Refused("invalid")); }
        return _session.HandleAsync(machine ? OwnerElevationPurpose.MachineServerCertificate : OwnerElevationPurpose.CertificateStore,
            request, peer, cancellationToken);
    }

    private sealed class StoreFlow(AdminCertificateStoreRequest payload) : OwnerElevationFlow(OwnerElevationPurpose.CertificateStore)
    {
        private readonly object _gate = new();
        private bool _completionConsumed;
        private CertificateElevationResult? _completion;
        internal AdminCertificateStoreRequest Payload { get; } = payload;
        internal CertificateElevationResult? Completion { get { lock (_gate) return _completion; } }
        internal override bool IsControlRequest(OperationRequest request) => CertificateStoreElevationContract.IsControlRequest(request);
        internal override bool IsRedeem(OperationRequest request) => request.Op == CertificateStoreElevationContract.RedeemOperation;
        internal override bool TryValidateRedeem(OperationRequest request)
        {
            try { _ = CertificateStoreElevationContract.ParseTicket(request); return IsRedeem(request); }
            catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException) { return false; }
        }
        internal override object SnapshotPayload() => Snapshot(Payload);
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request)
        {
            lock (_gate)
            {
                if (_completionConsumed) return Task.FromResult(Refused("certificate_elevation_consumed"));
                _completionConsumed = true;
                try
                {
                    var completion = CertificateStoreElevationContract.ParseCompletion(request, Payload.CertificateId);
                    if (Payload.Action == "store_remove" ? completion.State is "added" or "unchanged" : completion.State is "removed" or "absent")
                        throw new ArgumentException("certificate_elevation_completion_action_changed");
                    _completion = completion;
                    return Task.FromResult(Acknowledged(new Dictionary<string, object?> { ["completion_received"] = true }));
                }
                catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException)
                { return Task.FromResult(Refused("certificate_elevation_completion_invalid")); }
            }
        }
    }

    private sealed class MachineFlow(OwnerMachineServerCertificateFlow domain) : OwnerElevationFlow(OwnerElevationPurpose.MachineServerCertificate)
    {
        internal OwnerMachineServerCertificateFlow Domain { get; } = domain;
        internal override bool IsControlRequest(OperationRequest request) => MachineServerCertificateElevationContract.IsControlRequest(request);
        internal override bool IsRedeem(OperationRequest request) => request.Op == MachineServerCertificateElevationContract.RedeemOperation;
        internal override bool TryValidateRedeem(OperationRequest request)
        {
            try { _ = MachineServerCertificateElevationContract.ParseTicket(request); return IsRedeem(request); }
            catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException) { return false; }
        }
        internal override object SnapshotPayload() => MachineServerCertificateElevationContract.Snapshot(Domain.Request);
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request) =>
            request.Op == MachineServerCertificateElevationContract.CsrOperation ? Domain.SignAsync(request) :
                Task.FromResult(Domain.Complete(request));
        internal override Task WhenIdle => Domain.WhenIdle;
    }

    private static AdminCertificateStoreRequest Snapshot(AdminCertificateStoreRequest payload)
    {
        CertificateStoreElevationContract.ValidatePayload(payload);
        var snapshot = payload with { PublicDer = payload.PublicDer?.ToArray() };
        CertificateStoreElevationContract.ValidatePayload(snapshot);
        if (snapshot.PublicDer is { } der)
        {
            using var certificate = CertificateExchange.ImportPublic(der, PublicCertificateFormat.Der);
            if (CertificateOperations.Inspect(certificate).CertificateId != snapshot.CertificateId)
                throw new ArgumentException("certificate_elevation_public_identity_invalid");
        }
        return snapshot;
    }
    private static CertificateElevationResult Failed(AdminCertificateStoreRequest payload, string code, int? native = null) =>
        new(1, "failed", payload.CertificateId, false, false, false, code, native);
    private static CertificateElevationResult Unknown(AdminCertificateStoreRequest payload, string code) =>
        new(1, "uncertain", payload.CertificateId, null, false, true, code);
    private static OperationReceipt Refused(string code) => new(false, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        "failed", "certificate_store_elevation", code);
    private static OperationReceipt Acknowledged(IReadOnlyDictionary<string, object?> data) => new(true,
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "done", "certificate_store_elevation", Data: data);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.RevokePurpose(OwnerElevationPurpose.CertificateStore);
        _session.RevokePurpose(OwnerElevationPurpose.MachineServerCertificate);
        if (_ownsSession) _session.Dispose();
    }
}
