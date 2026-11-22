using System.Text.Json;
using Pegasus.Cortex.Sync.Client.Contracts;
using Pegasus.Cortex.Sync.Client.Orchestration;

namespace Pegasus.Cortex.Sync.Client.Commands;

public static class CortexSyncClientCommand
{
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CortexSyncCoordinator coordinator,
        ICortexSyncChangeSignalSource? signalSource,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(output);
        if (arguments.Count != 1 || arguments[0] is not ("sync-now" or "watch"))
        {
            await output.WriteLineAsync("Usage: PegasusCortex.Sync.Client sync-now|watch").ConfigureAwait(false);
            return 2;
        }

        if (arguments[0] == "sync-now")
        {
            var single = await RunOnceAsync(coordinator, output, cancellationToken).ConfigureAwait(false);
            return single.Accepted ? 0 : 1;
        }
        if (signalSource is null)
            throw new ArgumentNullException(nameof(signalSource), "watch requires an injected change-signal source.");

        var result = await RunOnceAsync(coordinator, output, cancellationToken).ConfigureAwait(false);
        var retryIndex = 0;
        while (true)
        {
            if (!result.Accepted && !result.Retryable)
                return 1;

            var interval = result.Accepted
                ? SafetyPollInterval
                : RetryDelay(retryIndex++);
            if (result.Accepted)
                retryIndex = 0;
            await signalSource.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            result = await RunOnceAsync(coordinator, output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CortexSyncRunResult> RunOnceAsync(
        CortexSyncCoordinator coordinator,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.SyncNowAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            contractId = "pegasus.cortex.sync-client-result/1.1",
            accepted = result.Accepted,
            disposition = result.Disposition.ToString().ToLowerInvariant(),
            diagnosticCode = result.DiagnosticCode,
            message = result.Message,
            authorityHash = result.AuthorityHash,
            receiptId = result.ReceiptId,
            retryable = result.Retryable
        })).ConfigureAwait(false);
        return result;
    }

    private static TimeSpan RetryDelay(int retryIndex)
    {
        var boundedIndex = Math.Clamp(retryIndex, 0, 5);
        var seconds = Math.Min(1 << boundedIndex, (int)MaximumRetryDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
