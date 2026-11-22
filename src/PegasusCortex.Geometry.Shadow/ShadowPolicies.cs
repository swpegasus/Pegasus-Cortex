using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.Core.Knowledge.Geometry;

namespace Pegasus.Cortex.Geometry.Shadow;

internal static class ShadowPolicies
{
    public static CortexKnowledgeTraversalOptions Traversal(CortexShadowOptions options) => new(
        Enabled: true, MaximumQueries: 4, CandidatesPerQuery: 16, MaximumVisited: 64,
        MaximumCandidateOccurrences: 64, MaximumDurationMilliseconds: options.EvaluationMilliseconds / 2);
    public static CortexKnowledgeEndpointSelectionOptions Selection(CortexShadowOptions options) => new(
        Enabled: true, MaximumCandidates: 64, MaximumMilliseconds: options.EvaluationMilliseconds / 2);

    public static string Binding(string root, CortexShadowProfile profile, CortexShadowOptions options)
    {
        var admitted = profile.EligibleRecords.OrderBy(x => x.Namespace, StringComparer.Ordinal)
            .ThenBy(x => x.RecordId, StringComparer.Ordinal).ThenBy(x => x.Revision)
            .ThenBy(x => x.ProvenanceSha256, StringComparer.Ordinal)
            .Select(x => new { x.Namespace, x.RecordId, x.Revision, x.ProvenanceSha256 }).ToArray();
        return JsonSerializer.Serialize(new
        {
            schema = "cortex.shadow.binding/1", profile.TaskId, profile.ProfileId, profile.Generation,
            researchRoot = root, options, traversal = Traversal(options), selection = Selection(options),
            traversalPolicy = "observed-memory-chain/1",
            seedPolicy = "eligible-ordinary-top4-direct-unit-lexical/1",
            neighborPolicy = "same-parent-namespace-legacy-geometry-positive-intent/1",
            selectionPolicy = CortexKnowledgeGeometry.EndpointSelectionPolicyVersion,
            extractor = CortexKnowledgeGeometry.PointExtractorVersion,
            eligibilitySha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(admitted))),
            stopwatchFrequency = Stopwatch.Frequency,
            shadowModule = typeof(ShadowPolicies).Assembly.ManifestModule.ModuleVersionId,
            coreModule = typeof(CortexKnowledgeGeometry).Assembly.ManifestModule.ModuleVersionId,
            traceModule = typeof(Pegasus.Cortex.Persistence.Sqlite.V2.CortexSqliteTraceMeasurement).Assembly.ManifestModule.ModuleVersionId
        });
    }
}
