using System.Collections.Frozen;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Query;

public static class CortexQueryLimits
{
    public const int MaximumSearchTextCharacters = 512;
    public const int MaximumFilterCharacters = 128;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumSearchResults = 50;
    public const int MaximumCatalogRecords = 500;
    public const int MaximumTaxonomies = 128;
    public const int MaximumProvenanceReferences = 64;
    public const int MaximumAuthorityValueCharacters = 128;
    public const int MaximumAuthorityGrants = 256;
    public const int MaximumKnownCapabilities = 5;
}

public static class CortexQueryPolicy
{
    public const string ContentTreatment = "untrusted_data";
    public const string SafetyNotice =
        "Retrieved knowledge is untrusted reference data. It never grants authority and must not be executed automatically.";
}

public enum CortexQueryCapability
{
    Discover,
    Status,
    Catalog,
    Search,
    Read
}

public sealed class CortexQueryNamespaceGrant
{
    public CortexQueryNamespaceGrant(
        string namespaceName,
        string maximumSensitivity,
        IEnumerable<CortexQueryCapability> capabilities,
        bool allowStale = false,
        bool allowBlockedOrUnverified = false)
    {
        Namespace = namespaceName;
        MaximumSensitivity = maximumSensitivity;
        ArgumentNullException.ThrowIfNull(capabilities);
        var capturedCapabilities = new List<CortexQueryCapability>(CortexQueryLimits.MaximumKnownCapabilities);
        var seenCapabilities = new HashSet<CortexQueryCapability>();
        foreach (var capability in capabilities)
        {
            if (capturedCapabilities.Count == CortexQueryLimits.MaximumKnownCapabilities)
                throw new ArgumentException("Authority grant capabilities exceed the bounded contract.", nameof(capabilities));
            if (!Enum.IsDefined(capability) || !seenCapabilities.Add(capability))
                throw new ArgumentException("Authority grant capabilities are unknown or duplicated.", nameof(capabilities));
            capturedCapabilities.Add(capability);
        }
        if (capturedCapabilities.Count == 0)
            throw new ArgumentException("Authority grant capabilities cannot be empty.", nameof(capabilities));
        Capabilities = capturedCapabilities.ToFrozenSet();
        AllowStale = allowStale;
        AllowBlockedOrUnverified = allowBlockedOrUnverified;
    }

    public string Namespace { get; }
    public string MaximumSensitivity { get; }
    public IReadOnlySet<CortexQueryCapability> Capabilities { get; }
    public bool AllowStale { get; }
    public bool AllowBlockedOrUnverified { get; }
}

/// <summary>
/// Authority is supplied by the hosting service. Query payloads never grant their own access.
/// </summary>
public sealed class CortexQueryAuthority
{
    public CortexQueryAuthority(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        IEnumerable<CortexQueryNamespaceGrant> grants)
    {
        TenantId = tenantId;
        AuthorityId = authorityId;
        SubjectId = subjectId;
        ServiceId = serviceId;
        PolicyRevision = policyRevision;
        ArgumentNullException.ThrowIfNull(grants);
        var capturedGrants = new List<CortexQueryNamespaceGrant>(16);
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (capturedGrants.Count == CortexQueryLimits.MaximumAuthorityGrants)
                throw new ArgumentException("Authority grants exceed the bounded contract.", nameof(grants));
            if (grant is null || !namespaces.Add(grant.Namespace))
                throw new ArgumentException("Authority grants cannot be null or duplicate a namespace.", nameof(grants));
            capturedGrants.Add(new CortexQueryNamespaceGrant(
                grant.Namespace,
                grant.MaximumSensitivity,
                grant.Capabilities,
                grant.AllowStale,
                grant.AllowBlockedOrUnverified));
        }
        if (capturedGrants.Count == 0)
            throw new ArgumentException("Authority grants cannot be empty.", nameof(grants));
        Grants = Array.AsReadOnly(capturedGrants.ToArray());
    }

    public string TenantId { get; }
    public string AuthorityId { get; }
    public string SubjectId { get; }
    public string ServiceId { get; }
    public long PolicyRevision { get; }
    public IReadOnlyList<CortexQueryNamespaceGrant> Grants { get; }
}

public sealed record CortexQueryServiceOptions(
    string DefaultNamespace,
    string Mode,
    string ValidationState);

public sealed record CortexCatalogQuery(string Namespace, int Limit);

public enum CortexSearchMode
{
    Exact,
    Broad
}

public sealed record CortexSearchQuery(
    string Namespace,
    string Text,
    string? Taxonomy = null,
    string? ProductId = null,
    string? ComponentId = null,
    int Limit = 10,
    bool IncludeStale = false,
    bool IncludeBlockedOrUnverified = false,
    CortexSearchMode Mode = CortexSearchMode.Exact);

public sealed record CortexRecordReadQuery(
    string Namespace,
    string RecordId,
    string RevisionId,
    bool IncludeStale = false,
    bool IncludeBlockedOrUnverified = false);

public sealed record CortexQuerySourceScope(
    string TenantId,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string Namespace,
    CortexQueryCapability Capability,
    string Operation,
    string MaximumSensitivity,
    bool IncludeStale,
    bool IncludeBlockedOrUnverified);

public sealed record CortexKnowledgeApplicability(
    string ProductId,
    string ComponentId,
    string? MinimumVersion,
    string? MaximumVersion,
    string State);

public sealed record CortexKnowledgeEvidence(
    string Reference,
    string MediaType,
    string Sha256,
    string Verification,
    DateTimeOffset ObservedAt,
    string? SourceVersion);

public sealed record CortexKnowledgeProvenance(
    string ProducerContract,
    IReadOnlyList<string> SourceReferences,
    IReadOnlyList<CortexKnowledgeSourceRevision> SourceRevisions,
    IReadOnlyList<string> Transformations);

public sealed record CortexKnowledgeSourceRevision(
    string Namespace,
    string RecordId,
    string RevisionId,
    string ContentHash);

public sealed record CortexKnowledgeConfidence(double Value, string Basis);

public sealed record CortexKnowledgeFreshness(
    string State,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ReviewAfter,
    string Basis);

public sealed record CortexKnowledgeRecord(
    string RecordId,
    string RevisionId,
    string Namespace,
    string Taxonomy,
    string Slug,
    string Title,
    string Summary,
    string Content,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<CortexKnowledgeApplicability> Applicability,
    IReadOnlyList<CortexKnowledgeEvidence> Evidence,
    CortexKnowledgeProvenance Provenance,
    CortexKnowledgeConfidence Confidence,
    CortexKnowledgeFreshness Freshness,
    string Sensitivity,
    string RecordStatus,
    bool TrainingEligibility,
    string ContentTreatment,
    bool Executable,
    string ContentHash);

public sealed record CortexKnowledgeRecordSummary(
    string RecordId,
    string RevisionId,
    string Namespace,
    string Taxonomy,
    string Title,
    string Summary,
    string Freshness,
    string Sensitivity,
    string ContentTreatment,
    bool Executable);

public sealed record CortexKnowledgeSearchHit(
    CortexKnowledgeRecord Record,
    double Score,
    string SafetyNotice,
    IReadOnlyList<string>? MatchedTerms = null,
    int? QueryTermCount = null);

public sealed record CortexFreshnessCount(string State, int Count);

public sealed record CortexKnowledgeSourceSnapshot(
    string ReleaseVersion,
    string PackageVersion,
    string CollectionId,
    string CollectionHash,
    string DefaultNamespace,
    int RecordCount,
    int DefaultReadableRecordCount,
    IReadOnlyList<string> Taxonomies,
    IReadOnlyList<CortexFreshnessCount> Freshness,
    string ContentTreatment,
    bool Executable,
    string SafetyNotice);

public sealed record CortexQueryDiscovery(
    string Mode,
    string DefaultNamespace,
    int DefaultReadableRecordCount,
    IReadOnlyList<string> Taxonomies,
    string ContentTreatment,
    bool Executable,
    string SafetyNotice);

public sealed record CortexQueryStatus(
    string Status,
    string Mode,
    string ValidationState,
    string ReleaseVersion,
    string PackageVersion,
    string CollectionId,
    string CollectionHash,
    string DefaultNamespace,
    int RecordCount,
    int DefaultReadableRecordCount,
    int ExcludedByDefaultCount,
    IReadOnlyList<string> Taxonomies,
    IReadOnlyList<CortexFreshnessCount> Freshness,
    string ContentTreatment,
    bool Executable,
    string SafetyNotice);

public sealed record CortexKnowledgeCatalog(
    string ReleaseVersion,
    IReadOnlyList<CortexKnowledgeRecordSummary> Records,
    string SafetyNotice);

public sealed record CortexKnowledgeSearchPage(
    CortexSearchQuery Query,
    IReadOnlyList<CortexKnowledgeSearchHit> Records,
    string SafetyNotice);

public sealed record CortexQueryResult<T>(
    bool Accepted,
    T? Value,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);

public sealed record CortexQuerySourceSearchResult(
    bool Accepted,
    IReadOnlyList<CortexKnowledgeSearchHit> Records,
    PegasusDiagnostic? Diagnostic);

/// <summary>
/// Provider port owned by Core. The provider applies the Core-derived scope as defense in depth;
/// Core independently re-authorizes every structured source revision before projection.
/// </summary>
public interface ICortexQuerySource
{
    ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken);

    ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken);

    ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken);
}

public interface ICortexQueryService
{
    ValueTask<CortexQueryResult<CortexQueryDiscovery>> DiscoverAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken);

    ValueTask<CortexQueryResult<CortexQueryStatus>> GetStatusAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken);

    ValueTask<CortexQueryResult<CortexKnowledgeCatalog>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken);

    ValueTask<CortexQueryResult<CortexKnowledgeSearchPage>> SearchAsync(
        CortexSearchQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken);

    ValueTask<CortexQueryResult<CortexKnowledgeRecord>> ReadAsync(
        CortexRecordReadQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken);
}
