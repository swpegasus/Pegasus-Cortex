namespace Pegasus.AgentOps.Engine;

// Soft cleanup targets for completed execution history, never admission quotas.
// Current/uncertain work may keep usage above these targets. No timer or archive.
internal sealed record ExecutionHistoryRetention(
    TimeSpan MaximumAge,
    long UsedPageTriggerBytes,
    int CompletedRowTarget,
    int BatchSize,
    TimeSpan RecentReplayWindow)
{
    internal static ExecutionHistoryRetention Default { get; } = new(
        TimeSpan.FromDays(30), 64L * 1024 * 1024, 10_000, 256, TimeSpan.FromHours(24));

    internal void Validate()
    {
        if (MaximumAge <= TimeSpan.Zero || MaximumAge > TimeSpan.FromDays(3650) || RecentReplayWindow <= TimeSpan.Zero || RecentReplayWindow > MaximumAge ||
            UsedPageTriggerBytes < 1 || CompletedRowTarget is < 1 or > 1_000_000 || BatchSize is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(ExecutionHistoryRetention));
    }
}

internal sealed record ExecutionHistoryRetentionResult(int Examined, int Deleted, int PreservedActive,
    int PreservedRecent, int PreservedUncertain, int PreservedInvalid, int PreservedNewest, long NextSequence, bool More,
    long UsedPageBytes, int CompletedRowsAtLeast, bool Pressure);
