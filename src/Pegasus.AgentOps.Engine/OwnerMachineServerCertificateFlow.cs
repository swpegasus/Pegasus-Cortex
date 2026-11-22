using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Domain phases only. The shared coordinator owns tickets, High peer admission,
// original-child observation and the common bounded lifetime.
internal sealed class OwnerMachineServerCertificateFlow
{
    private readonly object _gate = new();
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task<MachineServerCertificateSignedLeaf>> _signAndPublish;
    private readonly TimeProvider _clock;
    private bool _csrConsumed, _completionConsumed;
    private string? _leafId;
    private Task _signing = Task.CompletedTask;
    private MachineServerCertificateResult? _completion;
    internal MachineServerCertificateRequest Request { get; }

    internal OwnerMachineServerCertificateFlow(MachineServerCertificateRequest request,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<MachineServerCertificateSignedLeaf>> signAndPublish, TimeProvider clock)
    {
        Request = MachineServerCertificateElevationContract.Snapshot(request);
        _signAndPublish = signAndPublish ?? throw new ArgumentNullException(nameof(signAndPublish));
        _clock = clock;
    }

    internal Task WhenIdle { get { lock (_gate) return _signing; } }

    internal Task<OperationReceipt> SignAsync(OperationRequest request)
    {
        TaskCompletionSource<OperationReceipt> reservation;
        lock (_gate)
        {
            if (_csrConsumed || _completionConsumed) return Task.FromResult(Refused("certificate_elevation_consumed"));
            // The first authenticated attempt consumes this phase, including a
            // malformed CSR or a signing/publication failure. No replacement key.
            _csrConsumed = true;
            reservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish ownership before invoking any caller code. A synchronous
            // signer can reenter original-child exit observation on this thread.
            _signing = reservation.Task;
        }
        _ = CompleteReservedSigningAsync(request, reservation);
        return reservation.Task;
    }

    private async Task CompleteReservedSigningAsync(OperationRequest request, TaskCompletionSource<OperationReceipt> reservation)
    {
        try { reservation.TrySetResult(await SignCoreAsync(request).ConfigureAwait(false)); }
        catch (Exception failure) { reservation.TrySetException(failure); }
    }

    private async Task<OperationReceipt> SignCoreAsync(OperationRequest request)
    {
        try
        {
            var csr = MachineServerCertificateElevationContract.ParseCsr(request);
            // Once admitted, the parent retains signing/publication to completion.
            // Disconnecting a helper cannot release its CA or owner effect lease.
            var leaf = MachineServerCertificateElevationContract.SnapshotLeaf(
                await _signAndPublish(csr, CancellationToken.None).ConfigureAwait(false));
            MachineServerCertificateElevationContract.ValidateLeaf(leaf, Request, _clock.GetUtcNow());
            lock (_gate) _leafId = leaf.CertificateId;
            return new(true, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "done", "certificate_machine_elevation",
                Data: new Dictionary<string, object?> { ["signedLeaf"] = leaf });
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { return Refused("certificate_machine_signing_failed"); }
    }

    internal OperationReceipt Complete(OperationRequest request)
    {
        lock (_gate)
        {
            if (_completionConsumed) return Refused("certificate_elevation_consumed");
            _completionConsumed = true;
            try
            {
                var result = MachineServerCertificateElevationContract.ParseResult(request);
                if (result.Progress?.CertificateId is { } id && id != _leafId || result.State == "bound" && _leafId is null ||
                    !_signing.IsCompleted) return Refused("certificate_elevation_completion_invalid");
                _completion = result;
                return new(true, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "done", "certificate_machine_elevation",
                    Data: new Dictionary<string, object?> { ["completion_received"] = true });
            }
            catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
            { return Refused("certificate_elevation_completion_invalid"); }
        }
    }

    internal MachineServerCertificateResult Outcome(bool issued, string failure)
    {
        lock (_gate) return _completion ?? MachineServerCertificateElevationContract.Failed(failure, issued);
    }

    private static OperationReceipt Refused(string code) => new(false, Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"), "failed", "certificate_machine_elevation", code);
}
