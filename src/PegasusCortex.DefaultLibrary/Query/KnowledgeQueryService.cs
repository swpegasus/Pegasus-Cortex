using Pegasus.Cortex.Contracts.Query;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.DefaultLibrary.Query;

public sealed class KnowledgeQueryService(IKnowledgeReadRepository repository)
{
    private const int MaximumCandidates = 500;
    private const int MaximumResults = 50;

    public async ValueTask<KnowledgeSearchResult> SearchAsync(
        KnowledgeSearchRequest request,
        KnowledgeReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Limit is < 1 or > MaximumResults || request.Text.Length > 512 ||
            !Enum.IsDefined(request.Mode))
            return Reject("default_library_query_bounds_invalid", "Search text or result limit exceeds the bounded read contract.");
        if (!SensitivityRank.TryGetValue(scope.MaximumSensitivity, out var maximumSensitivity))
            return Reject("default_library_query_scope_invalid", "The caller sensitivity scope is unsupported.");

        var repositoryQuery = new KnowledgeRepositoryQuery(
            request.Taxonomy,
            request.ProductId,
            request.ComponentId,
            MaximumCandidates);
        var candidates = await repository.ReadCandidatesAsync(repositoryQuery, cancellationToken).ConfigureAwait(false);
        if (candidates.Count > MaximumCandidates)
            return Reject("default_library_repository_bounds_exceeded", "The repository returned more candidates than the bounded query contract permits.");

        // Authorization and safety filtering intentionally occurs before scoring or result shaping.
        var authorized = candidates.Where(record =>
            string.Equals(record.RecordStatus, "active", StringComparison.Ordinal) &&
            scope.ReadableNamespaces.Contains(record.Namespace) &&
            SensitivityRank.TryGetValue(record.Sensitivity, out var sensitivity) && sensitivity <= maximumSensitivity &&
            !record.Executable &&
            string.Equals(record.ContentTreatment, KnowledgeContractIds.ContentTreatment, StringComparison.Ordinal) &&
            FreshnessAllowed(record.Freshness.State, scope) &&
            MetadataMatches(record, request));

        var ranked = authorized
            .Select(record =>
            {
                var match = LexicalMatch(record, request);
                return new
                {
                    Record = record,
                    Match = match,
                    Score = CortexKnowledgeRanking.EffectiveScore(
                        match.WeightedScore,
                        record.Confidence.Value)
                };
            })
            .Where(item => item.Match.IsMatch)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Record.Title, StringComparer.Ordinal)
            .ThenBy(item => item.Record.RevisionId, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select(item => ToAgentResult(item.Record, item.Score))
            .ToArray();

        return new KnowledgeSearchResult(true, ranked, null);
    }

    private static bool FreshnessAllowed(string state, KnowledgeReadScope scope) => state switch
    {
        "current" => true,
        "stale" => scope.IncludeStale,
        "blocked" or "unverified" or "unknown" => scope.IncludeBlockedOrUnverified,
        _ => false
    };

    private static bool MetadataMatches(KnowledgeRecordV1 record, KnowledgeSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Taxonomy) &&
            !string.Equals(record.Taxonomy, request.Taxonomy, StringComparison.Ordinal))
            return false;

        return record.Applicability.Any(item =>
            (string.IsNullOrWhiteSpace(request.ProductId) || string.Equals(item.ProductId, request.ProductId, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(request.ComponentId) || string.Equals(item.ComponentId, request.ComponentId, StringComparison.Ordinal)));
    }

    private static CortexLexicalMatch LexicalMatch(
        KnowledgeRecordV1 record,
        KnowledgeSearchRequest request) =>
        CortexLexicalSearch.Match(
            request.Text,
            request.Mode == KnowledgeSearchMode.Broad
                ? CortexSearchModes.Broad
                : CortexSearchModes.Exact,
            [
                new CortexLexicalField(record.Title, 8, PhraseBoost: 20),
                new CortexLexicalField(record.Taxonomy, 5),
                new CortexLexicalField(string.Join(' ', record.Keywords), 5),
                new CortexLexicalField(record.Summary, 3),
                new CortexLexicalField(record.Content, 1)
            ]);

    private static AgentKnowledgeResultV1 ToAgentResult(KnowledgeRecordV1 record, double score) =>
        new(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Title,
            record.Summary,
            record.Content,
            record.Applicability,
            record.Evidence.Take(8).ToArray(),
            record.Confidence,
            record.Freshness,
            record.Sensitivity,
            score,
            KnowledgeContractIds.ContentTreatment,
            Executable: false,
            KnowledgeContractIds.AgentSafetyNotice);

    private static KnowledgeSearchResult Reject(string code, string message) =>
        new(
            false,
            [],
            new PegasusDiagnostic(
                code,
                message,
                DiagnosticSeverity.Error,
                "cortex.default-library.search",
                retryable: false));

    private static readonly IReadOnlyDictionary<string, int> SensitivityRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["public"] = 0,
            ["internal"] = 1,
            ["confidential"] = 2,
            ["restricted"] = 3
        };
}
