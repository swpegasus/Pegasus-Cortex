namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Bounded provider-neutral outcome for one general-collective query. Counts
/// describe only authorized sources selected by the provider and let clients
/// distinguish a valid empty result from an incomplete or failed route.
/// </summary>
public sealed record CortexCollectiveSearchResult(
    IReadOnlyList<CortexCollectiveSearchHit> Hits,
    int EligibleSourceCount,
    int QueriedSourceCount,
    int PartialFailureCount)
{
    public bool IsValid =>
        Hits is not null &&
        EligibleSourceCount >= 0 &&
        QueriedSourceCount >= 0 &&
        QueriedSourceCount <= EligibleSourceCount &&
        PartialFailureCount >= 0 &&
        PartialFailureCount <= QueriedSourceCount;
}
