namespace Pegasus.Cortex.Contracts.V2;

// Numeric operational evidence only. These are not knowledge or hydration records.
public sealed record CortexUsageSample(
    string Channel, string Operation, string SessionId, string? TaskId,
    IReadOnlyList<string> Namespaces, bool Failed, long Results,
    long ExactRecords, long ResponseBytes, long DurationMicroseconds);

public sealed record CortexUsageRow(
    string Dimension, string Key, string Channel, string Operation,
    long Calls, long Failures, long Results, long ExactRecords, long ResponseBytes,
    long TotalMicroseconds, long MaximumMicroseconds, string LastSeenUtc)
{
    public double AverageMilliseconds => Calls == 0 ? 0 : TotalMicroseconds / (Calls * 1000d);
}

public sealed record CortexUsageReport(
    string Schema, string State, int Days, int RetentionDays,
    string? CollectionStartedUtc, string? LastSeenUtc,
    long ObservedProcesses, long ObservedTasks, bool RowsTruncated,
    IReadOnlyList<CortexUsageRow> Rows)
{
    public static CortexUsageReport Empty(int days, string state = "not-collected") =>
        new("cortex.utilization/1", state, days, 90, null, null, 0, 0, false, []);
}

public interface ICortexUsageStore
{
    ValueTask RecordUsageAsync(CortexUsageSample sample, CancellationToken cancellationToken = default);
    ValueTask<CortexUsageReport> ReadUsageAsync(int days = 7, CancellationToken cancellationToken = default);
}
