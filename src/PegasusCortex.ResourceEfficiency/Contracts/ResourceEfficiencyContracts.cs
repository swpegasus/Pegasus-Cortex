using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.ResourceEfficiency.Contracts;

public sealed record ResourceEfficiencyOptions
{
    public int MaxImportBytes { get; init; } = 1024 * 1024;
    public int MaxBundleRecords { get; init; } = 500;
    public int MaxEvidencePerRecord { get; init; } = 16;
    public int MaxSearchPageSize { get; init; } = 20;
    public int MaxRankedResults { get; init; } = 50;
    public int MaxRankingCandidates { get; init; } = 500;
    public int MaxConcurrentQueries { get; init; } = 4;
    public int MaxQueuedQueries { get; init; } = 16;
    public int MaxConcurrentImports { get; init; } = 2;
    public int MaxQueuedImports { get; init; } = 4;
    public int MaxCacheEntries { get; init; } = 128;
    public long MaxCacheBytes { get; init; } = 16 * 1024 * 1024;
    public int RetentionBatchSize { get; init; } = 100;
    public int MaxMetricEvents { get; init; } = 1024;

    public void Validate()
    {
        if (MaxImportBytes is < 1024 or > 64 * 1024 * 1024 ||
            MaxBundleRecords is < 1 or > 500 || MaxEvidencePerRecord is < 1 or > 16 ||
            MaxSearchPageSize is < 1 or > 50 || MaxRankedResults < MaxSearchPageSize || MaxRankedResults > 50 ||
            MaxRankingCandidates < MaxRankedResults || MaxRankingCandidates > 5000 ||
            MaxConcurrentQueries is < 1 or > 256 || MaxQueuedQueries is < 0 or > 4096 ||
            MaxConcurrentImports is < 1 or > 64 || MaxQueuedImports is < 0 or > 1024 ||
            MaxCacheEntries is < 1 or > 100_000 || MaxCacheBytes is < 1024 or > 4L * 1024 * 1024 * 1024 ||
            RetentionBatchSize is < 1 or > 10_000 || MaxMetricEvents is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ResourceEfficiencyOptions),
                "Resource-efficiency limits are invalid, internally inconsistent, or unreasonably large.");
        }
    }
}

public sealed record ResourceGateSnapshot(int Active, int Queued, bool Disposed);

public sealed record ResourceSearchPageRequest(
    string Text,
    string? Taxonomy = null,
    string? ProductId = null,
    string? ComponentId = null,
    int PageSize = 10,
    string? ContinuationToken = null);

public sealed record ResourceSearchPageResult(
    bool Accepted,
    IReadOnlyList<AgentKnowledgeResultV1> Records,
    string? ContinuationToken,
    PegasusDiagnostic? Diagnostic);

/// <summary>
/// Includes authorization scope in the provider call so a database can filter before materialization.
/// </summary>
public sealed record ResourceAuthorizedKnowledgeQuery(
    string? Taxonomy,
    string? ProductId,
    string? ComponentId,
    IReadOnlySet<string> ReadableNamespaces,
    string MaximumSensitivity,
    bool IncludeStale,
    bool IncludeBlockedOrUnverified,
    int MaximumCandidates);

public interface IResourceBoundKnowledgeReadRepository
{
    IAsyncEnumerable<KnowledgeRecordV1> StreamAuthorizedCandidatesAsync(
        ResourceAuthorizedKnowledgeQuery query,
        CancellationToken cancellationToken);
}

public sealed record ResourceCacheSnapshot(int Entries, long Bytes, long Evictions, bool Disposed);

public sealed record ResourceCacheMutationResult(bool Accepted, PegasusDiagnostic? Diagnostic);

public sealed record ResourceRetentionCandidate(string RecordId, DateTimeOffset ExpiresAt);

public sealed record ResourceRetentionPage(
    IReadOnlyList<ResourceRetentionCandidate> Candidates,
    string? ContinuationToken);

public interface IResourceRetentionRepository
{
    ValueTask<IReadOnlyList<ResourceRetentionCandidate>> ReadExpiredPageAsync(
        DateTimeOffset now,
        string? afterRecordId,
        int limit,
        CancellationToken cancellationToken);
}

public static class ResourceDiagnostics
{
    public static PegasusDiagnostic Error(
        string code,
        string message,
        string operation,
        bool retryable = false,
        IReadOnlyDictionary<string, string?>? context = null) =>
        new(code, message, DiagnosticSeverity.Error, operation, retryable, context);
}
