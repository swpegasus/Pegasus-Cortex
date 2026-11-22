using Pegasus.AgentOps.Engine;

namespace Pegasus.AgentOps.Tray;

// Accepted means shutdown admission is committed, not that process exit or
// executable release has already been observed.
internal sealed record CortexRuntimeExitResult(bool Accepted, string? Issue = null);

/// <summary>Reversible admission close; commit cancellation only after both owners are safe.</summary>
internal sealed class CortexRuntimeExit(
    UserHostAdmission admission,
    Func<CancellationToken, Task<string?>> inspectWork,
    Func<CancellationToken, Task<string?>> stopOptionalService,
    Action requestStop,
    TimeSpan? drainTimeout = null)
{
    internal async Task<CortexRuntimeExitResult> RequestAsync(CancellationToken cancellationToken)
    {
        var result = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        return result.Accepted ? SignalStop() : result;
    }

    // A pipe caller commits the same policy, then signals only after its reply
    // attempt settles. Tray callers keep the original immediate-signal contract.
    internal async Task<CortexRuntimeExitResult> PrepareAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(false, "exit_canceled");
        using var closing = admission.TryClose();
        if (closing is null) return new(false, "exit_in_progress");
        try
        {
            using (var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                drain.CancelAfter(drainTimeout ?? TimeSpan.FromSeconds(5));
                while (true)
                {
                    await closing.WhenIdleAsync(drain.Token).ConfigureAwait(false);
                    var blocker = await inspectWork(drain.Token).ConfigureAwait(false);
                    if (blocker is null) break;
                    if (blocker != "work_active") return new(false, blocker);
                    await Task.Delay(50, drain.Token).ConfigureAwait(false);
                }
            }
            // Elevation is an explicit user action. Refusal or unknown service
            // state must leave this process/UI available, not report full OFF.
            cancellationToken.ThrowIfCancellationRequested();
            var serviceIssue = await stopOptionalService(cancellationToken).ConfigureAwait(false);
            if (serviceIssue is not null) return new(false, serviceIssue);
            cancellationToken.ThrowIfCancellationRequested();
            closing.Commit();
            Volatile.Write(ref _prepared, 1);
            return new(true);
        }
        catch (OperationCanceledException)
        { return new(false, cancellationToken.IsCancellationRequested ? "exit_canceled" : "work_active"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("event=exit_refused cause=" + error.GetType().Name);
            return new(false, "exit_state_unavailable");
        }
    }

    private int _prepared;
    private int _stopSignaled;
    internal CortexRuntimeExitResult SignalStop()
    {
        if (Volatile.Read(ref _prepared) == 0) return new(false, "exit_not_prepared");
        if (Interlocked.Exchange(ref _stopSignaled, 1) != 0) return new(true);
        try { requestStop(); return new(true); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("event=exit_signal_incomplete cause=" + error.GetType().Name);
            return new(true, "exit_signal_incomplete");
        }
    }
}
