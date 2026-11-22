using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// The domain owns only its typed, one-use conversation. The shared session owns
// original-child/High admission and callback drain; the engine owns durable RID
// admission and the maintenance effect until BeginAsync actually completes.
internal sealed class OwnerWindowsServiceElevation : IDisposable
{
    private readonly OwnerElevationSession _session;
    private readonly bool _ownsSession;
    private int _disposed;

    internal OwnerWindowsServiceElevation(string ownerSid, int session, string expectedHost,
        IOwnerCertificateElevationLauncher launcher, TimeProvider? clock = null)
        : this(new OwnerElevationSession(ownerSid, session, expectedHost, launcher, clock), true) { }
    internal OwnerWindowsServiceElevation(OwnerElevationSession shared) : this(shared, false) { }
    private OwnerWindowsServiceElevation(OwnerElevationSession session, bool ownsSession)
    { _session = session ?? throw new ArgumentNullException(nameof(session)); _ownsSession = ownsSession; }

    internal async Task<WindowsServiceElevationOutcome> BeginAsync(WindowsServiceElevationRequest request,
        Action payloadIssued, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payloadIssued);
        return await BeginAsync(request, _ => payloadIssued(), token).ConfigureAwait(false);
    }
    internal async Task<WindowsServiceElevationOutcome> BeginAsync(WindowsServiceElevationRequest request,
        Action<CertificateElevationProcessIdentity> payloadIssued, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payloadIssued);
        var flow = new ServiceFlow(request);
        if (Volatile.Read(ref _disposed) != 0) return new(null, false, "service_elevation_disposed");
        var observed = await _session.BeginAsync(flow, payloadIssued, token).ConfigureAwait(false);
        var completion = flow.Completion;
        return new(completion?.Result, observed.PayloadIssued,
            completion is null ? flow.Failure ?? observed.Code : completion.Failure,
            observed.Process, observed.Exit, observed.NativeError);
    }
    internal Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer, CancellationToken token) =>
        _session.HandleAsync(OwnerElevationPurpose.WindowsServiceControl, request, peer, token);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.RevokePurpose(OwnerElevationPurpose.WindowsServiceControl);
        if (_ownsSession) _session.Dispose();
    }

    private sealed class ServiceFlow : OwnerElevationFlow
    {
        private readonly object _gate = new();
        private readonly WindowsServiceElevationRequest _request;
        private bool _consumed;
        private WindowsServiceElevationCompletion? _completion;
        private string? _failure;
        internal ServiceFlow(WindowsServiceElevationRequest request) : base(OwnerElevationPurpose.WindowsServiceControl)
        { _request = WindowsServiceElevationContract.Snapshot(request); }
        internal WindowsServiceElevationCompletion? Completion { get { lock (_gate) return _completion; } }
        internal string? Failure { get { lock (_gate) return _failure; } }
        internal override bool IsControlRequest(OperationRequest request) => WindowsServiceElevationContract.IsControlRequest(request);
        internal override bool IsRedeem(OperationRequest request) => request.Op == WindowsServiceElevationContract.RedeemOperation;
        internal override bool TryValidateRedeem(OperationRequest request)
        {
            try { return IsRedeem(request) && WindowsServiceElevationContract.ParseTicket(request).Length == 64; }
            catch (WindowsServiceElevationContractException) { return false; }
        }
        internal override object SnapshotPayload() => WindowsServiceElevationContract.Snapshot(_request);
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request)
        {
            lock (_gate)
            {
                if (_consumed) return Task.FromResult(Refused("service_elevation_consumed"));
                // Consume the first exact-child report before parsing, including
                // malformed evidence. No later replacement can improve it.
                _consumed = true;
                try { _completion = WindowsServiceElevationContract.ParseCompletion(request, _request); }
                catch (WindowsServiceElevationContractException)
                {
                    _failure = "service_elevation_completion_invalid";
                    return Task.FromResult(Refused(_failure));
                }
                return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    "done", "windows_service_elevation", Data: new Dictionary<string, object?> { ["completion_received"] = true }));
            }
        }
        private static OperationReceipt Refused(string code) => new(false, Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), "failed", "windows_service_elevation", code);
    }
}
