using System.ComponentModel;
using System.Security.Cryptography;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Closed product purposes, not a provider/plugin registration surface. A
// capability is useful only for the original flow and retained High child.
internal enum OwnerElevationPurpose { CertificateStore, MachineServerCertificate, ScheduledTaskControl, WindowsServiceControl }

internal interface IOwnerScheduledTaskElevationLauncher
{
    Task<IOwnerCertificateElevationProcess> StartScheduledTaskAsync(string ticket, CancellationToken token);
}

internal interface IOwnerWindowsServiceElevationLauncher
{
    Task<IOwnerCertificateElevationProcess> StartWindowsServiceAsync(string ticket, CancellationToken token);
}

internal sealed record OwnerElevationObservation(bool PayloadIssued, CertificateElevationProcessIdentity? Process,
    int? Exit, string Code, int? NativeError = null);

// Flow state and public wire contracts remain domain-owned. These members are
// internal trusted composition, never choices supplied by a request or payload.
internal abstract class OwnerElevationFlow(OwnerElevationPurpose purpose)
{
    internal OwnerElevationPurpose Purpose { get; } = purpose;
    internal abstract bool IsControlRequest(OperationRequest request);
    internal abstract bool IsRedeem(OperationRequest request);
    internal abstract bool TryValidateRedeem(OperationRequest request);
    internal abstract object SnapshotPayload();
    internal abstract Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request);
    internal virtual Task WhenIdle => Task.CompletedTask;
}

/// <summary>
/// One bounded registry for fixed owner-High helpers. Owns capability/peer
/// admission and the original process, not business effects, payload parsing,
/// certificate/task state, databases, replay or privileged termination.
/// </summary>
internal sealed class OwnerElevationSession : IDisposable
{
    private readonly object _gate = new();
    private readonly string _ownerSid, _expectedHost;
    private readonly int _session;
    private readonly IOwnerCertificateElevationLauncher _launcher;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Pending> _tickets = new(StringComparer.Ordinal);
    private readonly HashSet<OwnerElevationPurpose> _closedPurposes = [];
    private bool _disposed;

    private sealed class Pending(string ticket, OwnerElevationFlow flow, Action<CertificateElevationProcessIdentity> issued, DateTimeOffset deadline)
    {
        internal readonly string Ticket = ticket;
        internal readonly OwnerElevationFlow Flow = flow;
        internal readonly Action<CertificateElevationProcessIdentity> PersistIssued = issued;
        internal readonly DateTimeOffset Deadline = deadline;
        internal readonly TaskCompletionSource<CertificateElevationProcessIdentity?> Binding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<OwnerElevationObservation> Outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HandlersIdle = CompletedSignal();
        internal CertificateElevationProcessIdentity? Process;
        internal IOwnerCertificateElevationProcess? RetainedProcess;
        internal int ActiveHandlers;
        internal bool Redeemed, Issued, Revoked, Closing, CompleteUnissuedWhenIdle;
        internal string Failure = Code(flow.Purpose, "no_completion");
    }

    internal OwnerElevationSession(string ownerSid, int session, string expectedHost,
        IOwnerCertificateElevationLauncher launcher, TimeProvider? clock = null)
    {
        _ = new OwnerHostIdentity(ownerSid, () => new(ownerSid, session));
        if (session <= 0 || !CanonicalImage(expectedHost)) throw new ArgumentException("owner_elevation_identity_invalid");
        _ownerSid = ownerSid; _session = session; _expectedHost = expectedHost;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _clock = clock ?? TimeProvider.System;
    }

    internal TimeProvider Clock => _clock;

    internal async Task<OwnerElevationObservation> BeginAsync(OwnerElevationFlow flow, Action payloadIssued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow); ArgumentNullException.ThrowIfNull(payloadIssued);
        return await BeginAsync(flow, _ => payloadIssued(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OwnerElevationObservation> BeginAsync(OwnerElevationFlow flow,
        Action<CertificateElevationProcessIdentity> payloadIssued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow); ArgumentNullException.ThrowIfNull(payloadIssued);
        if (!Enum.IsDefined(flow.Purpose)) throw new ArgumentException("owner_elevation_purpose_invalid");
        if (cancellationToken.IsCancellationRequested) return Observation(flow.Purpose, "canceled");
        Pending pending;
        lock (_gate)
        {
            if (_disposed || _closedPurposes.Contains(flow.Purpose) || !CanLaunch(flow.Purpose))
                return Observation(flow.Purpose, "unavailable");
            if (_tickets.Count >= 4) return Observation(flow.Purpose, "busy");
            var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            pending = new(ticket, flow, payloadIssued, _clock.GetUtcNow().AddMinutes(2));
            _tickets.Add(ticket, pending);
        }
        using var cancellation = cancellationToken.Register(() => RevokeUnissued(pending, "canceled"));
        _ = ObserveAsync(pending);
        return await pending.Outcome.Task.ConfigureAwait(false);
    }

    internal async Task<OperationReceipt> HandleAsync(OwnerElevationPurpose purpose, OperationRequest request,
        LocalPeer peer, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(purpose) || request is null || peer is null || request.Arg is null ||
            !request.Arg.TryGetValue("ticket", out var ticket) || !Capability(ticket)) return Refused(purpose, "invalid");
        Pending pending;
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticket, out pending!) || pending.Flow.Purpose != purpose) return Refused(purpose, "auth");
            if (!pending.Flow.IsControlRequest(request)) return Refused(purpose, "invalid");
            if (pending.Revoked && !pending.Issued) return Refused(purpose, "revoked");
        }
        if (!ExpectedPeerShape(peer)) return Refused(purpose, "auth");
        CertificateElevationProcessIdentity? bound;
        try { bound = await pending.Binding.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Refused(purpose, "canceled"); }
        if (bound is null || peer.ProcessId != bound.Pid) return Refused(purpose, "auth");
        CertificateElevationProcessIdentity? captured;
        try { captured = _launcher.CaptureElevatedPeer(peer); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return Refused(purpose, "auth"); }
        if (!SameProcess(captured, bound)) return Refused(purpose, "auth");

        if (pending.Flow.IsRedeem(request))
        {
            lock (_gate)
            {
                if (!Current(pending) || pending.Revoked || pending.Closing) return Refused(purpose, "revoked");
                try { if (!pending.Flow.TryValidateRedeem(request)) return Refused(purpose, "invalid"); }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                { return Refused(purpose, "invalid"); }
                if (pending.Redeemed) return Refused(purpose, "consumed");
                if (cancellationToken.IsCancellationRequested) return Refused(purpose, "canceled");
                if (_clock.GetUtcNow() >= pending.Deadline)
                { pending.Revoked = true; pending.Failure = Code(purpose, "expired"); return Refused(purpose, "expired"); }
                object payload;
                try { payload = pending.Flow.SnapshotPayload(); }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return Refused(purpose, "invalid"); }
                pending.Redeemed = true;
                // Persistence can also reenter cancellation or original-child
                // exit. Publish ownership before calling it, just as for an
                // asynchronous domain callback, and settle issuance first.
                ReserveHandler(pending);
                try
                {
                    // The only identity passed to persistence is the snapshot
                    // of this original child, authenticated above. Never derive
                    // a recovery generation from caller labels or the payload.
                    try { pending.PersistIssued(bound with { }); }
                    catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                    {
                        pending.Revoked = true; pending.Failure = Code(purpose, "issue_not_persisted");
                        return Refused(purpose, "issue_not_persisted");
                    }
                    if (pending.Revoked || _disposed || _closedPurposes.Contains(purpose)) return Refused(purpose, "revoked");
                    pending.Issued = true;
                    return Acknowledged(purpose, new Dictionary<string, object?> { ["payload"] = payload });
                }
                finally { ReleaseHandler(pending); }
            }
        }

        lock (_gate)
        {
            if (!Current(pending) || pending.Closing || pending.Revoked && !pending.Issued) return Refused(purpose, "revoked");
            if (!pending.Issued)
            {
                // Preserve the legacy public-store first-completion semantics.
                // Other domains refuse a premature report without a payload.
                if (purpose == OwnerElevationPurpose.CertificateStore)
                { pending.Revoked = true; pending.Failure = Code(purpose, "completion_invalid"); return Refused(purpose, "completion_invalid"); }
                return Refused(purpose, "revoked");
            }
            // Reserve BEFORE calling any domain handler: that call may complete
            // the child reentrantly before returning its actual asynchronous task.
            ReserveHandler(pending);
        }
        try
        {
            var response = await pending.Flow.HandleAuthenticatedAsync(request).ConfigureAwait(false);
            lock (_gate)
                if (response.Err == Code(purpose, "completion_invalid")) pending.Failure = Code(purpose, "completion_invalid");
            return response;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate) pending.Failure = Code(purpose, "completion_invalid");
            return Refused(purpose, "completion_invalid");
        }
        finally
        {
            lock (_gate) ReleaseHandler(pending);
        }
    }

    private async Task ObserveAsync(Pending pending)
    {
        lock (_gate)
        {
            if (pending.Revoked)
            { pending.Binding.TrySetResult(null); _tickets.Remove(pending.Ticket); return; }
        }
        IOwnerCertificateElevationProcess process;
        try
        {
            process = pending.Flow.Purpose switch
            {
                OwnerElevationPurpose.CertificateStore => await _launcher.StartAsync(pending.Ticket, CancellationToken.None).ConfigureAwait(false),
                OwnerElevationPurpose.MachineServerCertificate => await ((IOwnerMachineCertificateElevationLauncher)_launcher)
                    .StartMachineServerAsync(pending.Ticket, CancellationToken.None).ConfigureAwait(false),
                OwnerElevationPurpose.ScheduledTaskControl => await ((IOwnerScheduledTaskElevationLauncher)_launcher)
                    .StartScheduledTaskAsync(pending.Ticket, CancellationToken.None).ConfigureAwait(false),
                OwnerElevationPurpose.WindowsServiceControl => await ((IOwnerWindowsServiceElevationLauncher)_launcher)
                    .StartWindowsServiceAsync(pending.Ticket, CancellationToken.None).ConfigureAwait(false),
                _ => throw new InvalidOperationException("owner_elevation_purpose_invalid")
            };
            if (process is null) throw new InvalidOperationException("owner_elevation_process_missing");
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate)
            {
                pending.Failure = Code(pending.Flow.Purpose, failure is Win32Exception { NativeErrorCode: 1223 } ? "declined" : "launch_failed");
                pending.Binding.TrySetResult(null);
                Complete(pending, native: failure is Win32Exception native ? native.NativeErrorCode : null);
                _tickets.Remove(pending.Ticket);
            }
            return;
        }
        lock (_gate)
        {
            pending.RetainedProcess = process;
            CertificateElevationProcessIdentity? identity = null;
            try { identity = process.Identity; }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { }
            if (identity is null || identity.Pid == 0 || string.IsNullOrEmpty(identity.Start) || identity.Start.Length > 128 ||
                identity.Start.Any(char.IsControl) || !SameImage(identity.ApplicationPath))
            { pending.Revoked = true; pending.Failure = Code(pending.Flow.Purpose, "process_mismatch"); pending.Binding.TrySetResult(null); }
            else { pending.Process = identity with { }; pending.Binding.TrySetResult(pending.Process); }
        }
        int exit;
        try { exit = await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            // An unobserved exit retains this original handle, bounded slot and
            // issued effect ownership. There is no relaunch or privileged kill.
            lock (_gate) { pending.Revoked = true; pending.Closing = true; pending.Failure = Code(pending.Flow.Purpose, "exit_unobserved"); }
            return;
        }
        Task handlers;
        lock (_gate) { pending.Closing = true; handlers = pending.HandlersIdle.Task; }
        await handlers.ConfigureAwait(false);
        await pending.Flow.WhenIdle.ConfigureAwait(false);
        try { process.Dispose(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { }
        lock (_gate)
        {
            Complete(pending, exit);
            pending.RetainedProcess = null;
            _tickets.Remove(pending.Ticket);
        }
    }

    internal void RevokePurpose(OwnerElevationPurpose purpose)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentException("owner_elevation_purpose_invalid");
        lock (_gate)
        {
            _closedPurposes.Add(purpose);
            foreach (var pending in _tickets.Values.Where(p => p.Flow.Purpose == purpose && !p.Issued))
            { pending.Revoked = true; pending.Failure = Code(purpose, "disposed"); CompleteUnissued(pending); }
        }
    }

    private void RevokeUnissued(Pending pending, string suffix)
    {
        lock (_gate)
        {
            if (!Current(pending) || pending.Issued) return;
            pending.Revoked = true; pending.Failure = Code(pending.Flow.Purpose, suffix); CompleteUnissued(pending);
        }
    }
    // All callers hold _gate. Revocation remains prompt unless trusted caller
    // code has already entered; that callback retains its resources to return.
    private static void CompleteUnissued(Pending pending)
    {
        if (pending.ActiveHandlers == 0) Complete(pending);
        else pending.CompleteUnissuedWhenIdle = true;
    }
    private static void ReserveHandler(Pending pending)
    {
        if (pending.ActiveHandlers++ == 0) pending.HandlersIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static void ReleaseHandler(Pending pending)
    {
        if (--pending.ActiveHandlers != 0) return;
        pending.HandlersIdle.TrySetResult();
        if (pending.CompleteUnissuedWhenIdle && !pending.Issued) Complete(pending);
    }
    private static void Complete(Pending pending, int? exit = null, int? native = null) =>
        pending.Outcome.TrySetResult(new(pending.Issued, pending.Process, exit, pending.Failure, native));
    private static OwnerElevationObservation Observation(OwnerElevationPurpose purpose, string suffix) => new(false, null, null, Code(purpose, suffix));
    private bool CanLaunch(OwnerElevationPurpose purpose) => purpose switch
    {
        OwnerElevationPurpose.CertificateStore => true,
        OwnerElevationPurpose.MachineServerCertificate => _launcher is IOwnerMachineCertificateElevationLauncher,
        OwnerElevationPurpose.ScheduledTaskControl => _launcher is IOwnerScheduledTaskElevationLauncher,
        OwnerElevationPurpose.WindowsServiceControl => _launcher is IOwnerWindowsServiceElevationLauncher,
        _ => false
    };
    private bool Current(Pending pending) => _tickets.TryGetValue(pending.Ticket, out var actual) && ReferenceEquals(actual, pending);
    private bool ExpectedPeerShape(LocalPeer peer) => peer.Sid == _ownerSid && peer.Session == _session && peer.Elevated &&
        peer.ProcessId is > 0 && SameImage(peer.ApplicationPath);
    private bool SameImage(string? image) => image is not null && CanonicalImage(image) && StringComparer.OrdinalIgnoreCase.Equals(image, _expectedHost);
    private bool SameProcess(CertificateElevationProcessIdentity? actual, CertificateElevationProcessIdentity expected) => actual is not null &&
        actual.Pid == expected.Pid && actual.Start == expected.Start && SameImage(actual.ApplicationPath);
    private static bool CanonicalImage(string? image)
    {
        if (image is null || image.Length is < 1 or > 240 || image.Any(char.IsControl) || image.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try { return Path.IsPathFullyQualified(image) && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image), image); }
        catch (ArgumentException) { return false; }
    }
    private static bool Capability(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Code(OwnerElevationPurpose purpose, string suffix) => (purpose switch
    {
        OwnerElevationPurpose.WindowsServiceControl => "service_elevation_",
        OwnerElevationPurpose.ScheduledTaskControl => "task_elevation_",
        _ => "certificate_elevation_"
    }) + suffix;
    private static string ReceiptName(OwnerElevationPurpose purpose) => purpose switch
    {
        OwnerElevationPurpose.WindowsServiceControl => "windows_service_elevation",
        OwnerElevationPurpose.ScheduledTaskControl => "windows_task_elevation",
        _ => "certificate_store_elevation"
    };
    private static OperationReceipt Refused(OwnerElevationPurpose purpose, string suffix) =>
        new(false, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "failed", ReceiptName(purpose),
            suffix is "auth" or "invalid" or "canceled" ? suffix : Code(purpose, suffix));
    private static OperationReceipt Acknowledged(OwnerElevationPurpose purpose, IReadOnlyDictionary<string, object?> data) =>
        new(true, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "done", ReceiptName(purpose), Data: data);
    private static TaskCompletionSource CompletedSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _tickets.Values)
            {
                if (pending.Issued) continue;
                pending.Revoked = true; pending.Failure = Code(pending.Flow.Purpose, "disposed"); CompleteUnissued(pending);
            }
        }
    }
}
