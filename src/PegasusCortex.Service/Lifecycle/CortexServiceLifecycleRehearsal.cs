namespace Pegasus.Cortex.Service.Lifecycle;

/// <summary>
/// Exercises only the process readiness and shutdown boundary used by a Linux service
/// supervisor. It deliberately opens no transport, database, key store, or product data.
/// </summary>
internal static class CortexServiceLifecycleRehearsal
{
    internal const string ReadyMarker =
        "pegasus-cortex-service lifecycle-rehearsal state=ready " +
        "transport=disabled database=disabled keys=disabled notification=stdout-marker";

    internal const string StoppedMarker =
        "pegasus-cortex-service lifecycle-rehearsal state=stopped reason=shutdown-requested";

    internal const string StoppedBeforeReadyMarker =
        "pegasus-cortex-service lifecycle-rehearsal state=stopped reason=shutdown-before-ready";

    internal static async Task<int> RunAsync(
        TextWriter output,
        CancellationToken shutdownToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        var transitionGate = new object();
        var readyCommitted = false;
        var shutdownBeforeReady = false;
        var shutdownRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdownRegistration = shutdownToken.Register(() =>
        {
            lock (transitionGate)
            {
                if (!readyCommitted)
                    shutdownBeforeReady = true;
            }
            shutdownRequested.TrySetResult();
        });

        lock (transitionGate)
        {
            if (!shutdownBeforeReady)
                readyCommitted = true;
        }

        if (!readyCommitted)
        {
            WriteMarker(output, StoppedBeforeReadyMarker, "emit_stopped_before_ready");
            return 0;
        }

        WriteMarker(output, ReadyMarker, "emit_ready");
        // The cancellation callback completes this signal without a timer or polling loop.
        await shutdownRequested.Task.ConfigureAwait(false);

        WriteMarker(output, StoppedMarker, "emit_stopped");
        return 0;
    }

    private static void WriteMarker(TextWriter output, string marker, string operation)
    {
        try
        {
            output.WriteLine(marker);
            output.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new CortexServiceLifecycleOutputException(operation, exception);
        }
    }
}

/// <summary>
/// Retains the lifecycle operation and original writer failure for host diagnostics while
/// allowing the console boundary to redact environment-specific exception messages.
/// </summary>
internal sealed class CortexServiceLifecycleOutputException : Exception
{
    internal CortexServiceLifecycleOutputException(string operation, Exception innerException)
        : base("The service lifecycle marker could not be emitted.", innerException)
    {
        Operation = operation;
    }

    internal string Operation { get; }
}
