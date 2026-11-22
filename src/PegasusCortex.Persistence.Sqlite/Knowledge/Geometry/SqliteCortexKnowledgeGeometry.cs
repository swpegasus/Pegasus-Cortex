using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.Knowledge.Geometry;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2OperationalStore
{
    private async ValueTask<IReadOnlyList<SqliteCortexV2Namespace>> LogicalNamespacesAsync(
        CancellationToken cancellationToken)
    {
        var physical = await _store.ListNamespacesAsync(cancellationToken).ConfigureAwait(false);
        return physical
            .GroupBy(value => CortexKnowledgeSpaces.Canonicalize(value.Name), StringComparer.Ordinal)
            .Select(group => new SqliteCortexV2Namespace(
                group.Key,
                group.Sum(value => value.RevisionCount),
                group.Sum(value => value.HeadCount),
                group.Sum(value => value.ReferencedContentBytes),
                group.Sum(value => value.HeadContentBytes),
                group.Sum(value => value.HotChunkCount),
                group.Min(value => value.CreatedAtUtc),
                group.Max(value => value.UpdatedAtUtc)))
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async ValueTask<SqliteCortexV2HeadProjection?> ReadHeadProjectionLogicalAsync(
        string logicalNamespace,
        string kind,
        string recordId,
        CancellationToken cancellationToken)
    {
        foreach (var source in CortexKnowledgeSpaces.ReadSources(logicalNamespace))
        {
            var value = await _store.ReadHeadProjectionAsync(
                source,
                kind,
                recordId,
                cancellationToken).ConfigureAwait(false);
            if (value is not null)
                return value with { Namespace = logicalNamespace };
        }
        return null;
    }

    private async ValueTask<IReadOnlyList<SqliteCortexV2SearchHit>> SearchLogicalHeadsAsync(
        string logicalNamespace,
        string query,
        string? kind,
        int limit,
        int minimumMatch,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<(string Kind, string RecordId), SqliteCortexV2SearchHit>();
        foreach (var source in CortexKnowledgeSpaces.ReadSources(logicalNamespace))
        {
            var hits = await _store.SearchHeadsAsync(
                new(source, query, kind, limit, minimumMatch),
                cancellationToken).ConfigureAwait(false);
            foreach (var hit in hits)
            {
                var key = (hit.Kind, hit.RecordId);
                if (!values.ContainsKey(key))
                    values.Add(key, hit with { Namespace = logicalNamespace });
            }
        }
        return values.Values
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Kind, StringComparer.Ordinal)
            .ThenBy(value => value.RecordId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<SqliteCortexV2SearchHit>> SearchGeometricKnowledgeAsync(
        IReadOnlyList<string> logicalNamespaces,
        string query,
        int limit,
        int expansionThreshold,
        CancellationToken cancellationToken)
    {
        var direct = new List<SqliteCortexV2SearchHit>();
        foreach (var ns in logicalNamespaces)
        {
            direct.AddRange(await SearchLogicalHeadsAsync(
                ns,
                query,
                KnowledgeKind,
                limit,
                minimumMatch: 1,
                cancellationToken).ConfigureAwait(false));
        }

        var hits = new Dictionary<string, SqliteCortexV2SearchHit>(StringComparer.Ordinal);
        var points = new Dictionary<string, CortexKnowledgeGeometryPoint>(StringComparer.Ordinal);
        foreach (var hit in direct)
            Add(hit, isDirect: true);

        var expansionTerms = direct.Count < expansionThreshold
            ? CortexKnowledgeGeometry.ExpansionTerms(query, points.Values.ToArray())
            : [];
        if (expansionTerms.Count > 0)
        {
            var expansionQuery = string.Join(' ', expansionTerms);
            foreach (var ns in logicalNamespaces)
            {
                var related = await SearchLogicalHeadsAsync(
                    ns,
                    expansionQuery,
                    KnowledgeKind,
                    limit,
                    minimumMatch: 1,
                    cancellationToken).ConfigureAwait(false);
                foreach (var hit in related)
                    Add(hit, isDirect: false);
            }
        }

        var ranked = CortexKnowledgeGeometry.Rank(query, points.Values.ToArray(), limit);
        return ranked.Select(value => hits[value.Point.Key] with
        {
            Score = value.Score * 1_000d
        }).ToArray();

        void Add(SqliteCortexV2SearchHit hit, bool isDirect)
        {
            var key = $"{hit.Namespace}\n{hit.Kind}\n{hit.RecordId}\n{hit.Revision}";
            if (hits.TryGetValue(key, out var existing))
            {
                if ((isDirect && !points[key].Direct) || hit.Score > existing.Score)
                    hits[key] = hit;
                if (isDirect && !points[key].Direct)
                    points[key] = points[key] with { Direct = true, LexicalScore = hit.Score };
                return;
            }
            hits.Add(key, hit);
            points.Add(key, new(
                key,
                hit.Namespace,
                hit.RecordId,
                hit.Revision,
                hit.Title,
                hit.Tags,
                hit.Summary,
                hit.Score,
                isDirect));
        }
    }
}
