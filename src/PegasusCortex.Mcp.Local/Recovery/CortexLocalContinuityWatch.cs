using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using System.Text.Json;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Foreground, network-independent watch coordinator. File notifications only
/// shorten the wait; the complete input fingerprint and fixed safety poll own
/// correctness. A failed cycle retains the last verified outputs unchanged and
/// is retried after the next hint or poll.
/// </summary>
internal static class CortexLocalContinuityWatch
{
    public static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromHours(1);

    internal static async Task RunAsync(
        Configuration.CortexLocalContinuityRefreshArguments options,
        string projectRoot,
        ICortexLocalContinuityChangeSignalSource signals,
        TimeProvider timeProvider,
        Func<CortexLocalCurrentSnapshotResult, CancellationToken, ValueTask> onRefreshed,
        Func<Exception, CancellationToken, ValueTask> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onRefreshed);
        ArgumentNullException.ThrowIfNull(onFailure);

        string? successfulFingerprint = null;
        DateTimeOffset? refreshAt = null;
        var firstCycle = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturedAt = firstCycle
                ? options.Current.CapturedAt
                : timeProvider.GetUtcNow();
            firstCycle = false;
            try
            {
                var input = await CortexLocalCurrentProjectionRefresher
                    .CaptureInputStateAsync(projectRoot, cancellationToken)
                    .ConfigureAwait(false);
                var refreshRequired =
                    !string.Equals(
                        input.Fingerprint,
                        successfulFingerprint,
                        StringComparison.Ordinal) ||
                    refreshAt is null || capturedAt >= refreshAt.Value;
                if (refreshRequired)
                {
                    var cycleOptions = options with
                    {
                        Current = options.Current with { CapturedAt = capturedAt }
                    };
                    var result = await CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
                        cycleOptions,
                        cancellationToken).ConfigureAwait(false);
                    if (!result.Accepted)
                    {
                        throw new InvalidDataException(
                            "The continuity refresh did not accept retained state.");
                    }
                    var retained = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                        Convert.FromBase64String(result.SnapshotCanonicalBase64));
                    var after = await CortexLocalCurrentProjectionRefresher
                        .CaptureInputStateAsync(projectRoot, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(
                            after.AuthoritySetHash,
                            result.AuthoritySetHash,
                            StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "Continuity authority changed before the refreshed cycle could settle.");
                    }
                    successfulFingerprint = after.Fingerprint;
                    refreshAt = retained.ExpiresAt - RefreshBeforeExpiry;
                    await onRefreshed(result, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or
                    IOException or JsonException or InvalidOperationException or
                    NotSupportedException or
                    UnauthorizedAccessException)
            {
                await onFailure(exception, cancellationToken).ConfigureAwait(false);
            }

            await signals.WaitAsync(SafetyPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
