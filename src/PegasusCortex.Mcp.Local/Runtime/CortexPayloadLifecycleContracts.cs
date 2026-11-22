namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal sealed record CortexPayloadLifecyclePolicy(int Version, bool Enabled, IReadOnlyList<string> Namespaces,
    int MinimumArchiveAgeDays = 30, long TargetHotMiB = 0, int IntervalMinutes = 60,
    int WorkMilliseconds = 1000, int MaximumMetadataHeads = 16384);

internal sealed record CortexPayloadLifecycleSettings(long Revision, CortexPayloadLifecyclePolicy Policy);

internal sealed record CortexPayloadLifecycleStatus(string Schema, CortexPayloadLifecycleSettings? Saved,
    bool OwnerBound, int? StoreSchemaVersion, bool PassRunning, string State, string? Issue = null,
    string? Namespace = null, DateTimeOffset? LastStartedUtc = null, DateTimeOffset? LastFinishedUtc = null,
    long? HotBytesEvicted = null, int? SelectedRecords = null, bool? TargetSatisfied = null,
    int? MetadataObservations = null, bool ImmutableObjectsMayRemain = false);

internal interface ICortexPayloadLifecycleSettingsStore
{
    CortexPayloadLifecycleSettings Read();
    CortexPayloadLifecycleSettings Save(CortexPayloadLifecyclePolicy policy, long expectedRevision);
}

internal sealed record CortexPayloadLifecycleClientResult(string State, string? Error = null,
    CortexPayloadLifecycleStatus? Status = null, string? RequestId = null);

internal interface ICortexPayloadLifecycleClient
{
    Task<CortexPayloadLifecycleClientResult> ReadAsync(CancellationToken token);
    Task<CortexPayloadLifecycleClientResult> SaveAsync(CortexPayloadLifecyclePolicy policy, long expectedRevision, CancellationToken token);
    Task<CortexPayloadLifecycleClientResult> RunOnceAsync(CancellationToken token);
    Task<CortexPayloadLifecycleClientResult> UpgradeAsync(CancellationToken token);
}
