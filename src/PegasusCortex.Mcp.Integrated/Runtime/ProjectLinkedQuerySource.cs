using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Mcp.Integrated.Runtime;

/// <summary>
/// Adds a small authenticated, dynamically derived collection snapshot around the existing
/// PostgreSQL source. This avoids a second mutable collection-metadata subsystem for
/// project-retained records.
/// </summary>
internal sealed class ProjectLinkedQuerySource : ICortexQuerySource
{
    private readonly ICortexQuerySource _inner;
    private readonly CortexProjectLinkV1 _link;

    public ProjectLinkedQuerySource(ICortexQuerySource inner, CortexProjectLinkV1 link)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _link = link ?? throw new ArgumentNullException(nameof(link));
    }

    public async ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        EnsureScope(scope);
        var catalogScope = scope with
        {
            Capability = CortexQueryCapability.Catalog,
            Operation = "cortex.query.catalog",
            IncludeStale = false,
            IncludeBlockedOrUnverified = false
        };
        var records = await _inner.ListCatalogAsync(
            new CortexCatalogQuery(scope.Namespace, CortexQueryLimits.MaximumCatalogRecords),
            catalogScope,
            cancellationToken).ConfigureAwait(false);
        var taxonomies = records
            .Select(record => record.Taxonomy)
            .Append("continuity")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var collectionProjection = records.Select(record => new
        {
            record.RecordId,
            record.RevisionId,
            record.Freshness
        }).ToArray();
        var collectionHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(collectionProjection));
        var freshness = records
            .GroupBy(record => record.Freshness, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CortexFreshnessCount(group.Key, group.Count()))
            .ToArray();
        return new CortexKnowledgeSourceSnapshot(
            "retained-knowledge-v1",
            "1.0.0",
            $"collection-{_link.CorrelationHash}",
            collectionHash,
            scope.Namespace,
            records.Count,
            records.Count,
            taxonomies,
            freshness,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice);
    }

    public ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        EnsureScope(scope);
        return _inner.ListCatalogAsync(query, scope, cancellationToken);
    }

    public ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        EnsureScope(scope);
        return _inner.SearchAsync(query, scope, cancellationToken);
    }

    public ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        EnsureScope(scope);
        return _inner.ReadAsync(query, scope, cancellationToken);
    }

    private void EnsureScope(CortexQuerySourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!string.Equals(
                scope.Namespace,
                _link.Metadata.ProjectNamespace,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The query scope does not match the correlated project namespace.");
        }
    }
}
