using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Geometry.Shadow;

/// <summary>Source-only composition attachment. The application receives Store in place of the same
/// already authenticated provider. This adds no connection, DB discovery, service, host or public transport.
/// Dispose the attachment before the owner disposes its original provider.</summary>
public sealed class CortexShadowAttachment : IAsyncDisposable
{
    public CortexShadowAttachment(ICortexV2Store original, CortexShadowProfile profile, string researchRoot,
        CortexShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        Collector = new(profile, researchRoot, options);
        Store = new CortexObservedV2Store(original, Collector);
        SummaryPath = (options?.CollectionEnabled ?? false) ? Path.Combine(researchRoot, "status.json") : null;
    }
    public ICortexV2Store Store { get; }
    public CortexGeometryShadow Collector { get; }
    public string? SummaryPath { get; }
    public ValueTask DisposeAsync() => Collector.DisposeAsync();
}
