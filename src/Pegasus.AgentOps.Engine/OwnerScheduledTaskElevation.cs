using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Purpose-specific typed conversation over the shared exact-child capability
// registry. Root owns admission, issued durability, and the effect lease.
internal sealed class OwnerScheduledTaskElevation : IDisposable
{
    private readonly OwnerElevationSession _session;
    private readonly bool _ownsSession;
    private int _disposed;

    internal OwnerScheduledTaskElevation(string ownerSid, int session, string expectedHost,
        IOwnerCertificateElevationLauncher launcher, TimeProvider? clock = null)
        : this(new OwnerElevationSession(ownerSid, session, expectedHost, launcher, clock), true) { }
    internal OwnerScheduledTaskElevation(OwnerElevationSession shared) : this(shared, false) { }
    private OwnerScheduledTaskElevation(OwnerElevationSession session, bool ownsSession)
    { _session = session ?? throw new ArgumentNullException(nameof(session)); _ownsSession = ownsSession; }

    internal async Task<ScheduledTaskElevationOutcome> BeginAsync(ScheduledTaskElevationRequest request,
        Action payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadIssued);
        return await BeginAsync(request, _ => payloadIssued(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ScheduledTaskElevationOutcome> BeginAsync(ScheduledTaskElevationRequest request,
        Action<CertificateElevationProcessIdentity> payloadIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadIssued);
        var flow = new TaskFlow(request);
        if (Volatile.Read(ref _disposed) != 0) return new(null, false, "task_elevation_disposed");
        var observed = await _session.BeginAsync(flow, payloadIssued, cancellationToken).ConfigureAwait(false);
        var completion = flow.Completion;
        // Missing/malformed completion is no native evidence. In particular it
        // must never become a fabricated unissued WindowsTaskOutcome.
        return new(completion?.Result, observed.PayloadIssued,
            completion is null ? flow.Failure ?? observed.Code : completion.Failure,
            observed.Process, observed.Exit, observed.NativeError);
    }

    internal Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer, CancellationToken cancellationToken) =>
        _session.HandleAsync(OwnerElevationPurpose.ScheduledTaskControl, request, peer, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.RevokePurpose(OwnerElevationPurpose.ScheduledTaskControl);
        if (_ownsSession) _session.Dispose();
    }

    private sealed class TaskFlow : OwnerElevationFlow
    {
        private readonly object _gate = new();
        private readonly ScheduledTaskElevationRequest _request;
        private bool _completionConsumed;
        private ScheduledTaskElevationCompletion? _completion;
        private string? _failure;
        internal TaskFlow(ScheduledTaskElevationRequest request) : base(OwnerElevationPurpose.ScheduledTaskControl)
        { _request = ScheduledTaskElevationContract.Snapshot(request); }
        internal ScheduledTaskElevationCompletion? Completion { get { lock (_gate) return _completion; } }
        internal string? Failure { get { lock (_gate) return _failure; } }

        internal override bool IsControlRequest(OperationRequest request) => ScheduledTaskElevationContract.IsControlRequest(request);
        internal override bool IsRedeem(OperationRequest request) => request.Op == ScheduledTaskElevationContract.RedeemOperation;
        internal override bool TryValidateRedeem(OperationRequest request)
        {
            try { return IsRedeem(request) && ScheduledTaskElevationContract.ParseTicket(request).Length == 64; }
            catch (ScheduledTaskElevationContractException) { return false; }
        }
        internal override object SnapshotPayload() => ScheduledTaskElevationContract.Snapshot(_request);
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request)
        {
            lock (_gate)
            {
                if (_completionConsumed) return Task.FromResult(Refused("task_elevation_consumed"));
                // Core has already pinned the purpose, exact child generation,
                // owner/session/High image, and issued phase. Consume this phase
                // BEFORE validating the first completion so malformed evidence
                // cannot be replaced with a second, more favorable report.
                _completionConsumed = true;
                try { _completion = ScheduledTaskElevationContract.ParseCompletion(request, _request); }
                catch (ScheduledTaskElevationContractException)
                {
                    _failure = "task_elevation_completion_invalid";
                    return Task.FromResult(Refused(_failure));
                }
                return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    "done", "windows_task_elevation", Data: new Dictionary<string, object?> { ["completion_received"] = true }));
            }
        }
        private static OperationReceipt Refused(string error) => new(false, Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), "failed", "windows_task_elevation", error);
    }
}
