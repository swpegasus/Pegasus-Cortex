using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.Knowledge.Geometry;

internal static class CortexKnowledgeGeometryTests
{
    public static Task RunAsync()
    {
        Assert.Equal("knowledge.shared", CortexKnowledgeSpaces.Canonicalize("shared.programming"));
        Assert.Equal("knowledge.shared", CortexKnowledgeSpaces.Canonicalize("shared.general"));
        Assert.Equal("knowledge.user", CortexKnowledgeSpaces.Canonicalize("user.preferences"));
        Assert.Equal("project.exchange", CortexKnowledgeSpaces.Canonicalize("project.exchange"));
        Assert.Equal("Knowledge.Shared", CortexKnowledgeSpaces.DisplayPath("knowledge.shared"));

        CortexKnowledgeGeometryPoint[] points =
        [
            new(
                "shared/direct/1",
                CortexKnowledgeSpaces.Shared,
                "sqlite-locking",
                1,
                "SQLite locking diagnosis",
                ["sqlite", "locking", "bounded-contention"],
                "Use bounded contention evidence before changing transaction policy.",
                80,
                Direct: true),
            new(
                "shared/related/1",
                CortexKnowledgeSpaces.Shared,
                "transaction-contention",
                1,
                "Bounded transaction contention",
                ["bounded-contention", "transaction"],
                "Diagnose lock ownership with bounded evidence.",
                12,
                Direct: false),
            new(
                "user/related/1",
                CortexKnowledgeSpaces.User,
                "preferred-lock-report",
                1,
                "Preferred contention report",
                ["bounded-contention", "report"],
                "Keep lock evidence compact.",
                11,
                Direct: false)
        ];

        var expansion = CortexKnowledgeGeometry.ExpansionTerms("sqlite locking", points);
        Assert.True(expansion.Contains("bounded", StringComparer.Ordinal) ||
                    expansion.Contains("contention", StringComparer.Ordinal));

        var ranked = CortexKnowledgeGeometry.Rank("sqlite locking", points, limit: 3);
        Assert.Equal("sqlite-locking", ranked[0].Point.RecordId);
        Assert.True(ranked.Any(value =>
            value.Point.RecordId == "transaction-contention" &&
            value.NeighborSimilarity > 0 &&
            value.SharedFeatures > 0));
        Assert.True(ranked.Select(value => value.Point.Key).SequenceEqual(
            CortexKnowledgeGeometry.Rank("sqlite locking", points, limit: 3)
                .Select(value => value.Point.Key),
            StringComparer.Ordinal));
        return Task.CompletedTask;
    }
}
