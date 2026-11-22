using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.Core.Sync;

/// <summary>
/// Exact persistence scope used to locate the newest synchronization head that
/// has both an authenticated terminal receipt and a verified retained-state
/// projection. Raw journal leaves are intentionally not exposed through this
/// boundary.
/// </summary>
public sealed record CortexSyncPublishedHeadLookup(CortexSyncRepositoryScope Scope);

/// <summary>
/// Exact immutable bundle identity used to recover the original published
/// result even after a later bundle has advanced the source head.
/// </summary>
public sealed record CortexSyncPublishedBundleLookup(
    CortexSyncRepositoryScope Scope,
    string BundleId,
    string BundleHash);

/// <summary>
/// Receipt-backed journal evidence used by a non-MCP synchronization gateway to
/// construct a stable public head. The provider returns the original immutable
/// bundle, commit, and receipt so higher layers can independently verify every
/// hash and retained-state projection before publishing it.
/// </summary>
public sealed record CortexSyncPublishedJournalHead(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle,
    CortexSyncCommitSnapshot Commit,
    CortexSyncReceiptV1 Receipt,
    DateTimeOffset ReceiptRecordedAt);

public interface ICortexSyncPublishedHeadRepository
{
    ValueTask<CortexSyncPublishedJournalHead?> FindLatestPublishedAsync(
        CortexSyncPublishedHeadLookup request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Versioned historical extension for exact immutable bundle replay. Existing
/// published-head consumers retain the original latest-only interface.
/// </summary>
public interface ICortexSyncPublishedHeadRepositoryV2 :
    ICortexSyncPublishedHeadRepository
{
    ValueTask<CortexSyncPublishedJournalHead?> FindPublishedAsync(
        CortexSyncPublishedBundleLookup request,
        CancellationToken cancellationToken);
}
