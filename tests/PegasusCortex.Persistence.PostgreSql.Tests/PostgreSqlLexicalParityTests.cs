using System.Reflection;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Query;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.Persistence.PostgreSql.Query;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

internal static class PostgreSqlLexicalParityTests
{
    public static Task SharedGoldenCorpusAsync()
    {
        var fixture = LoadFixture();
        var release = DefaultKnowledgeLibrary.Create();
        var target = CreateTargetRecord(
            fixture.Target,
            release.Records.First(record => record.Taxonomy == fixture.Target.Taxonomy));
        var corpus = release.Records
            .Select(CreateRecord)
            .Append(target)
            .ToArray();

        var exactQuery = new CortexSearchQuery(
            target.Namespace,
            fixture.PartialTermQuery,
            Limit: 3,
            Mode: CortexSearchMode.Exact);
        var broadQuery = exactQuery with { Mode = CortexSearchMode.Broad };
        var exactPartial = Evaluate(target, exactQuery);
        var broadPartial = Evaluate(target, broadQuery);

        TestAssert.False(
            exactPartial.IsMatch,
            "PostgreSQL exact matching accepted a partial-term record.");
        TestAssert.False(
            TopThree(corpus, exactQuery).Any(record => record.RecordId == target.RecordId),
            "PostgreSQL exact ranking returned the partial-term target.");
        TestAssert.True(
            broadPartial.IsMatch,
            "PostgreSQL broad matching rejected the partial-term target.");
        TestAssert.True(
            TopThree(corpus, broadQuery).Any(record => record.RecordId == target.RecordId),
            "PostgreSQL broad matching omitted the partial-term target from the shared corpus top three.");
        TestAssert.Equal(2, broadPartial.QueryTermCount, "Broad query-term metadata changed.");
        TestAssert.Equal(1, broadPartial.MatchedTerms.Count, "Broad matched-term metadata changed.");
        TestAssert.Equal("context", broadPartial.MatchedTerms[0], "Broad matched-term normalization changed.");

        foreach (var text in fixture.ExactQueries)
        {
            var query = new CortexSearchQuery(
                target.Namespace,
                text,
                Limit: 3,
                Mode: CortexSearchMode.Exact);
            TestAssert.True(
                Evaluate(target, query).IsMatch,
                $"PostgreSQL exact parity probe did not match all terms: {text}");
            TestAssert.True(
                TopThree(corpus, query).Any(record => record.RecordId == target.RecordId),
                $"PostgreSQL exact parity target did not rank in the shared corpus top three: {text}");
        }

        var broadHits = fixture.NaturalLanguageQueries.Count(text =>
            TopThree(
                corpus,
                new CortexSearchQuery(
                    target.Namespace,
                    text,
                    Limit: 3,
                    Mode: CortexSearchMode.Broad))
                .Any(record => record.RecordId == target.RecordId));
        TestAssert.True(
            broadHits >= fixture.MinimumBroadRecallAt3,
            $"PostgreSQL shared-corpus broad Recall@3 was {broadHits}/{fixture.NaturalLanguageQueries.Count}.");
        return Task.CompletedTask;
    }

    public static Task BroadCandidateCrowdOutAsync()
    {
        const string validRecordId = "broad-valid-after-prefix-crowd";
        var template = CreateRecord(DefaultKnowledgeLibrary.Create().Records[0]);
        var valid = template with
        {
            RecordId = validRecordId,
            RevisionId = "broad-valid-revision",
            Taxonomy = "adversarial",
            Title = "zz context target",
            Summary = "valid folded term",
            Content = "bounded candidate",
            Keywords = []
        };
        var falsePrefixes = Enumerable.Range(0, 51)
            .Select(index => valid with
            {
                RecordId = $"broad-false-prefix-{index:D3}",
                RevisionId = $"broad-false-prefix-revision-{index:D3}",
                Title = $"aa contextual distractor {index:D3}"
            })
            .ToArray();
        var corpus = falsePrefixes.Append(valid).ToArray();
        var query = new CortexSearchQuery(
            valid.Namespace,
            "contexts",
            Limit: 50,
            Mode: CortexSearchMode.Broad);
        var foldedTerms = CortexLexicalSearch.NormalizeTerms(
            query.Text,
            CortexSearchModes.Broad);
        var providerTerms = CortexLexicalSearch.ProviderTerms(
            query.Text,
            CortexSearchModes.Broad);

        TestAssert.Equal(
            string.Join(' ', providerTerms),
            CortexKnowledgeLexicalSearch.ProviderQueryText(query),
            "The PostgreSQL query text diverged from the shared finite fold expansion.");
        TestAssert.False(
            providerTerms.Contains("contextual", StringComparer.Ordinal),
            "The exact provider lexemes still include the false contextual prefix.");

        var legacyPrefixPage = corpus
            .Where(record => RawTerms(record).Any(rawTerm =>
                foldedTerms.Any(term => rawTerm.StartsWith(term, StringComparison.Ordinal))))
            .OrderBy(record => record.Title, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
        TestAssert.Equal(50, legacyPrefixPage.Length, "The adversarial prefix page is not exercising the SQL bound.");
        TestAssert.False(
            legacyPrefixPage.Any(record => record.RecordId == validRecordId),
            "The adversarial corpus no longer reproduces legacy prefix crowd-out.");

        var exactProviderTerms = providerTerms.ToHashSet(StringComparer.Ordinal);
        var providerPage = corpus
            .Where(record => RawTerms(record).Any(exactProviderTerms.Contains))
            .OrderBy(record => record.Title, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
        TestAssert.Equal(1, providerPage.Length, "Exact fold expansion admitted false-prefix candidates.");
        TestAssert.Equal(validRecordId, providerPage[0].RecordId, "The valid folded-term row was lost before post-filtering.");
        TestAssert.True(
            providerPage.All(record => Evaluate(record, query).IsMatch),
            "The finite provider expansion admitted a row rejected by the shared matcher.");
        TestAssert.True(
            falsePrefixes.All(record => !Evaluate(record, query).IsMatch),
            "The adversarial distractors are not genuine shared-matcher false prefixes.");
        return Task.CompletedTask;
    }

    private static IReadOnlyList<CortexKnowledgeRecord> TopThree(
        IReadOnlyList<CortexKnowledgeRecord> corpus,
        CortexSearchQuery query) =>
        corpus
            .Select(record => new { Record = record, Match = Evaluate(record, query) })
            .Where(item => item.Match.IsMatch)
            .OrderByDescending(item => CortexKnowledgeRanking.EffectiveScore(
                item.Match.WeightedScore,
                item.Record.Confidence.Value))
            .ThenBy(item => item.Record.Title, StringComparer.Ordinal)
            .ThenBy(item => item.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.Record.RevisionId, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Record)
            .ToArray();

    private static CortexLexicalMatch Evaluate(
        CortexKnowledgeRecord record,
        CortexSearchQuery query)
    {
        var method = typeof(PostgreSqlCortexQuerySource).GetMethod(
            "LexicalMatch",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The PostgreSQL shared lexical matcher is unavailable.");
        return method.Invoke(null, [record, query]) as CortexLexicalMatch
            ?? throw new InvalidOperationException("The PostgreSQL shared lexical matcher returned no evidence.");
    }

    private static IReadOnlyList<string> RawTerms(CortexKnowledgeRecord record) =>
        CortexLexicalSearch.NormalizeTerms(
            string.Join(
                ' ',
                record.Title,
                record.Taxonomy,
                string.Join(' ', record.Keywords),
                record.Summary,
                record.Content),
            CortexSearchModes.Exact);

    private static CortexKnowledgeRecord CreateTargetRecord(
        LexicalParityTarget target,
        KnowledgeRecordV1 template) =>
        new(
            "parity-target",
            "parity-target-revision",
            DefaultKnowledgeLibrary.DefaultNamespace,
            target.Taxonomy,
            "bounded-agent-context-recovery",
            target.Title,
            target.Summary,
            target.Content,
            target.Keywords,
            template.Applicability.Select(item => new CortexKnowledgeApplicability(
                item.ProductId,
                item.ComponentId,
                item.MinimumVersion,
                item.MaximumVersion,
                item.State)).ToArray(),
            [],
            new CortexKnowledgeProvenance("parity-fixture", [], [], []),
            new CortexKnowledgeConfidence(template.Confidence.Value, template.Confidence.Basis),
            new CortexKnowledgeFreshness(
                template.Freshness.State,
                template.Freshness.ObservedAt,
                template.Freshness.ReviewAfter,
                template.Freshness.Basis),
            template.Sensitivity,
            template.RecordStatus,
            template.TrainingEligibility,
            template.ContentTreatment,
            Executable: false,
            ContentHash: new string('a', 64));

    private static CortexKnowledgeRecord CreateRecord(KnowledgeRecordV1 record) =>
        new(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Slug,
            record.Title,
            record.Summary,
            record.Content,
            record.Keywords,
            record.Applicability.Select(item => new CortexKnowledgeApplicability(
                item.ProductId,
                item.ComponentId,
                item.MinimumVersion,
                item.MaximumVersion,
                item.State)).ToArray(),
            [],
            new CortexKnowledgeProvenance(
                record.Provenance.ProducerContract,
                record.Provenance.SourceRecordIds,
                [],
                record.Provenance.Transformations),
            new CortexKnowledgeConfidence(record.Confidence.Value, record.Confidence.Basis),
            new CortexKnowledgeFreshness(
                record.Freshness.State,
                record.Freshness.ObservedAt,
                record.Freshness.ReviewAfter,
                record.Freshness.Basis),
            record.Sensitivity,
            record.RecordStatus,
            record.TrainingEligibility,
            record.ContentTreatment,
            Executable: false,
            record.ContentHash);

    private static LexicalParityFixture LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
        var path = Path.Combine(root, "tests", "fixtures", "lexical-search-provider-parity.v1.json");
        var fixture = JsonSerializer.Deserialize<LexicalParityFixture>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The shared lexical parity fixture is unavailable.");
        if (fixture.SchemaVersion != 1 || fixture.NaturalLanguageQueries.Count != 20 ||
            fixture.ExactQueries.Count == 0 || fixture.MinimumBroadRecallAt3 is < 1 or > 20)
        {
            throw new InvalidOperationException("The shared lexical parity fixture is malformed or unbounded.");
        }
        return fixture;
    }

    private sealed record LexicalParityTarget(
        string Taxonomy,
        string Title,
        string Summary,
        string Content,
        IReadOnlyList<string> Keywords);

    private sealed record LexicalParityFixture(
        int SchemaVersion,
        LexicalParityTarget Target,
        string PartialTermQuery,
        IReadOnlyList<string> ExactQueries,
        IReadOnlyList<string> NaturalLanguageQueries,
        int MinimumBroadRecallAt3);
}
