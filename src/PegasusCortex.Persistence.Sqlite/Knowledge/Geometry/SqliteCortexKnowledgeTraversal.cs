using System.Diagnostics;
using System.Text;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.Knowledge.Geometry;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexKnowledgeTraversalNeighborQuery(int Ordinal, int RawReturned,
    bool RawAtLimit, int Accepted, int Rejected);

public sealed record SqliteCortexKnowledgeTraversalResult(
    CortexKnowledgeTraversalResult Traversal, IReadOnlyList<CortexSearchHitV2> Items,
    IReadOnlyList<CortexSearchHitV2> AdmittedMetadata, IReadOnlyList<string> EffectiveNamespaces,
    int SeedQueries, int NeighborQueries, int RawSeedCandidates, bool SeedPoolAtLimit,
    IReadOnlyList<SqliteCortexKnowledgeTraversalNeighborQuery> RawNeighborQueries);

public sealed partial class SqliteCortexV2OperationalStore
{
    /// <summary>
    /// Separate opt-in knowledge-only exploration; normal SearchAsync is unchanged.
    /// Every seed and intermediate projection must be active and match the requested
    /// kind and selected namespace before it can participate. Edges stay in the
    /// querying parent's namespace. This returns metadata, never selected context.
    /// The bounded library trace is not a standard SearchAsync response: request
    /// MaxUtf8Bytes does not cap this trace; its caller owns a separate byte envelope.
    /// Core query counts describe accepted projections; raw pre-filter counts remain separate.
    /// </summary>
    public async ValueTask<SqliteCortexKnowledgeTraversalResult> TraverseKnowledgeAsync(
        CortexSearchRequestV2 request, CortexKnowledgeTraversalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text) || Encoding.UTF8.GetByteCount(request.Text) > 4096)
            throw new ArgumentException("Traversal requires bounded nonblank original intent.");
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var namespaces = CortexKnowledgeSpaces.Canonicalize(request.Namespaces);
        if (namespaces.Count is < 1 or > 4 || request.AfterCursor is not null ||
            request.Limit != options.ResultLimit)
            throw new ArgumentException("Traversal requires bounded explicit scope, matching limit and no cursor.");
        var hits = new Dictionary<CortexKnowledgeTraversalCoordinate, SqliteCortexV2SearchHit>();
        var seedQueries = 0;
        var neighborQueries = 0;
        var rawNeighborQueries = new List<SqliteCortexKnowledgeTraversalNeighborQuery>();
        var rawSeeds = 0;
        var seedAtLimit = false;
        var clock = Stopwatch.StartNew();
        if (!options.Enabled)
        {
            var disabled = await CortexKnowledgeGeometry.TraverseAsync(request.Text, namespaces, [],
                FindNeighbors, options, cancellationToken).ConfigureAwait(false);
            return Result(disabled);
        }

        var direct = new List<CortexKnowledgeGeometryPoint>();
        using (var seedDeadline = new CancellationTokenSource(options.MaximumDurationMilliseconds))
        using (var seedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, seedDeadline.Token))
        {
            try
            {
                foreach (var ns in namespaces)
                {
                    seedQueries++;
                    var values = await SearchLogicalHeadsAsync(ns, request.Text, KnowledgeKind, 32, 1, seedToken.Token)
                        .ConfigureAwait(false);
                    rawSeeds += values.Count;
                    seedAtLimit |= values.Count == 32;
                    foreach (var hit in values)
                        if (Admit(hit)) direct.Add(Point(hit, true));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && seedDeadline.IsCancellationRequested)
            {
                return Result(new(true, "time-limit-before-seeds", true, [], [], [], 0));
            }
        }
        var remaining = options.MaximumDurationMilliseconds - (int)clock.ElapsedMilliseconds;
        if (remaining < 1) return Result(new(true, "time-limit-before-traversal", true, [], [], [], 0));
        var seeds = direct.OrderByDescending(value => value.LexicalScore)
            .ThenBy(value => value.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.RecordId, StringComparer.Ordinal).ThenBy(value => value.Revision)
            .Take(32).ToArray();
        var trace = await CortexKnowledgeGeometry.TraverseAsync(request.Text, namespaces, seeds, FindNeighbors,
            options with { MaximumDurationMilliseconds = remaining }, cancellationToken).ConfigureAwait(false);
        return Result(trace);

        bool Admit(SqliteCortexV2SearchHit hit)
        {
            if (hit.Kind != KnowledgeKind || !namespaces.Contains(hit.Namespace, StringComparer.Ordinal)) return false;
            var metadata = ParseKnowledgeMetadata(hit.AttributesJson);
            if (metadata.State != CortexKnowledgeStateV2.Active ||
                request.Kinds is not null && !request.Kinds.Contains(metadata.Kind)) return false;
            hits.TryAdd(new(hit.Namespace, KnowledgeKind, hit.RecordId, hit.Revision), hit);
            return true;
        }
        static CortexKnowledgeGeometryPoint Point(SqliteCortexV2SearchHit hit, bool direct) =>
            new(string.Empty, hit.Namespace, hit.RecordId, hit.Revision, hit.Title, hit.Tags, hit.Summary, hit.Score, direct);
        async ValueTask<IReadOnlyList<CortexKnowledgeGeometryPoint>> FindNeighbors(
            CortexKnowledgeTraversalRequest query, CancellationToken token)
        {
            neighborQueries++;
            var values = await SearchLogicalHeadsAsync(query.Parent.Namespace, string.Join(' ', query.Terms),
                KnowledgeKind, query.Limit, 1, token).ConfigureAwait(false);
            var accepted = values.Where(Admit).Select(value => Point(value, false)).ToArray();
            rawNeighborQueries.Add(new(query.Ordinal, values.Count, values.Count == query.Limit,
                accepted.Length, values.Count - accepted.Length));
            return accepted;
        }
        CortexSearchHitV2 Metadata(CortexKnowledgeTraversalNode node)
        {
            var hit = hits[node.Coordinate];
            var metadata = ParseKnowledgeMetadata(hit.AttributesJson);
            return new(hit.Namespace, CortexRecordTypeV2.Knowledge, hit.RecordId, hit.Revision, hit.Title,
                SearchExcerpt(hit.Excerpt, request.Format), metadata.Kind, metadata.Tier,
                ToProvenanceReceipt(metadata.Provenance), node.Score * 1000);
        }
        SqliteCortexKnowledgeTraversalResult Result(CortexKnowledgeTraversalResult value) =>
            new(value, value.Ranked.Select(Metadata).ToArray(), value.Admitted.Select(Metadata).ToArray(),
                namespaces, seedQueries, neighborQueries, rawSeeds, seedAtLimit, rawNeighborQueries);
    }
}
