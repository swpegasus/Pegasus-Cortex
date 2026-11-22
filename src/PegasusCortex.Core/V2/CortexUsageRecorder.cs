using System.Diagnostics;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Core.V2;

/// <summary>Optional diagnostics must never turn a successful semantic write into a retryable failure.</summary>
public sealed class CortexUsageRecorder(ICortexUsageStore store, string channel, string? taskId = null)
{
    private long _lostSamples;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public long LostSamples => Interlocked.Read(ref _lostSamples);
    public void ReportLostSample() => Interlocked.Increment(ref _lostSamples);
    public static string? CanonicalTaskId(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value ? value : null;

    public async ValueTask RecordAsync(string operation, IReadOnlyList<string> namespaces,
        bool failed, long results, long exactRecords, long bytes, long started,
        string? explicitTaskId = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await store.RecordUsageAsync(new(channel, operation, SessionId,
                CanonicalTaskId(taskId) ?? CanonicalTaskId(explicitTaskId), namespaces,
                failed, results, exactRecords, bytes,
                (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Report a lower bound, never silently manufacture a complete history.
            ReportLostSample();
        }
    }

    public async ValueTask<CortexUsageReport> ReportAsync(int days = 7)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await store.ReadUsageAsync(days, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return CortexUsageReport.Empty(days, "unavailable");
        }
    }
}
