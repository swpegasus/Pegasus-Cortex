using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Query;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.DefaultLibrary.Validation;

namespace Pegasus.Cortex.Mcp.Knowledge;

public sealed record CortexResolvedRecordIdentity(
    string Namespace,
    string RecordId,
    string RevisionId);

/// <summary>
/// Compatibility-only resolver for the legacy MCP single-ID read contract.
/// It resolves an alias to one exact immutable identity but grants no read authority.
/// </summary>
public interface ILegacyKnowledgeIdentifierResolver
{
    ValueTask<CortexResolvedRecordIdentity?> ResolveAsync(
        string identifier,
        CancellationToken cancellationToken);
}

/// <summary>
/// Composition adapter from the verified built-in Default Library to the Core query source port.
/// It has no protocol, JSON, URI, filesystem, database, network, or project-continuity behavior.
/// </summary>
public sealed class DefaultLibraryQuerySource :
    ICortexQuerySource,
    IKnowledgeReadRepository,
    ILegacyKnowledgeIdentifierResolver
{
    private const int MaximumSeedRecords = 500;
    private const int MaximumSeedTaxonomies = 128;
    private const int MaximumRecordTextCharacters = 48 * 1024;

    private static readonly IReadOnlyDictionary<string, int> SensitivityRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["public"] = 0,
            ["internal"] = 1,
            ["confidential"] = 2,
            ["restricted"] = 3
        };

    private readonly KnowledgeSeedBundleV1 _bundle;
    private readonly string _namespace;
    private readonly IReadOnlyList<KnowledgeRecordV1> _records;
    private readonly IReadOnlyDictionary<(string RecordId, string RevisionId), KnowledgeRecordV1> _recordsByIdentity;
    private readonly IReadOnlyDictionary<string, CortexResolvedRecordIdentity?> _legacyIdentities;
    private readonly IReadOnlySet<(string RecordId, string RevisionId, string Reference)> _provenanceReferenceGrants;
    private readonly KnowledgeQueryService _queryService;

    private DefaultLibraryQuerySource(KnowledgeSeedBundleV1 bundle)
    {
        EnsureProjectionBounds(bundle);
        var namespaces = bundle.Records
            .Select(record => record.Namespace)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (namespaces.Length != 1)
            throw new InvalidOperationException("A static MCP knowledge bundle must contain one exact namespace.");

        _bundle = bundle;
        _namespace = namespaces[0];
        _records = bundle.Records
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .ThenBy(record => record.RevisionId, StringComparer.Ordinal)
            .ToArray();
        _recordsByIdentity = _records.ToDictionary(
            record => (record.RecordId, record.RevisionId));
        _legacyIdentities = CreateLegacyIdentityMap(_records);
        _provenanceReferenceGrants = CreateProvenanceReferenceGrants(_records);
        _queryService = new KnowledgeQueryService(this);
    }

    public static DefaultLibraryQuerySource CreateVerified()
        => CreateVerifiedBundle(DefaultKnowledgeLibrary.Create(), "Built-in Default Library");

    /// <summary>
    /// Creates the same bounded, read-only query projection over a caller-supplied
    /// immutable bundle. Composition remains responsible for choosing the bundle;
    /// this adapter performs no file, database, network, or project-continuity I/O.
    /// </summary>
    public static DefaultLibraryQuerySource CreateVerifiedBundle(KnowledgeSeedBundleV1 bundle)
        => CreateVerifiedBundle(bundle, "Static knowledge bundle");

    private static DefaultLibraryQuerySource CreateVerifiedBundle(
        KnowledgeSeedBundleV1 bundle,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var validation = new KnowledgeBundleValidator().Validate(bundle);
        if (!validation.Accepted)
        {
            var codes = string.Join(",", validation.Diagnostics
                .Select(diagnostic => diagnostic.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
            throw new InvalidOperationException($"{sourceName} validation failed: {codes}");
        }

        return new DefaultLibraryQuerySource(bundle);
    }

    public ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope, cancellationToken);
        var scopedRecords = _records.Where(record => IsWithinStaticScope(record, scope)).ToArray();
        var defaultReadableCount = scopedRecords.Count(record =>
            FreshnessAllowed(record.Freshness.State, includeStale: false, includeBlockedOrUnverified: false));
        var freshness = scopedRecords
            .GroupBy(record => record.Freshness.State, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CortexFreshnessCount(group.Key, group.Count()))
            .ToArray();

        return ValueTask.FromResult(new CortexKnowledgeSourceSnapshot(
            _bundle.ReleaseVersion,
            _bundle.NuGetVersion,
            _bundle.BundleId,
            _bundle.BundleHash,
            _namespace,
            scopedRecords.Length,
            defaultReadableCount,
            _bundle.Taxonomies.Select(item => item.Code).Order(StringComparer.Ordinal).ToArray(),
            freshness,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice));
    }

    public ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, cancellationToken, CortexQueryCapability.Catalog);
        if (!string.Equals(query.Namespace, scope.Namespace, StringComparison.Ordinal))
            throw new InvalidOperationException("Catalog query namespace does not match the Core-derived scope.");

        var records = _records
            .Where(record => IsReadable(record, scope))
            .Take(query.Limit)
            .Select(ToSummary)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<CortexKnowledgeRecordSummary>>(records);
    }

    public async ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, cancellationToken, CortexQueryCapability.Search);
        if (!string.Equals(query.Namespace, scope.Namespace, StringComparison.Ordinal))
            throw new InvalidOperationException("Search query namespace does not match the Core-derived scope.");

        var result = await _queryService.SearchAsync(
            new KnowledgeSearchRequest(
                query.Text,
                query.Taxonomy,
                query.ProductId,
                query.ComponentId,
                query.Limit,
                query.Mode == CortexSearchMode.Broad
                    ? KnowledgeSearchMode.Broad
                    : KnowledgeSearchMode.Exact),
            new KnowledgeReadScope(
                new HashSet<string>(StringComparer.Ordinal) { scope.Namespace },
                scope.MaximumSensitivity,
                scope.IncludeStale,
                scope.IncludeBlockedOrUnverified),
            cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
            return new CortexQuerySourceSearchResult(false, [], result.Diagnostic);

        var hits = new List<CortexKnowledgeSearchHit>(result.Records.Count);
        foreach (var item in result.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_recordsByIdentity.TryGetValue((item.RecordId, item.RevisionId), out var record) ||
                !IsReadable(record, scope))
            {
                throw new InvalidOperationException("Default Library search returned an unknown or cross-scope identity.");
            }

            var coreRecord = ToCoreRecord(record);
            var lexicalMatch = CortexKnowledgeLexicalSearch.Match(coreRecord, query);
            if (!lexicalMatch.IsMatch)
                throw new InvalidOperationException("Default Library search violated the shared lexical match contract.");

            hits.Add(new CortexKnowledgeSearchHit(
                coreRecord,
                item.Score,
                CortexQueryPolicy.SafetyNotice,
                lexicalMatch.MatchedTerms,
                lexicalMatch.QueryTermCount));
        }

        return new CortexQuerySourceSearchResult(true, hits.ToArray(), null);
    }

    public ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope, cancellationToken, CortexQueryCapability.Read);
        if (!string.Equals(query.Namespace, scope.Namespace, StringComparison.Ordinal))
            throw new InvalidOperationException("Read query namespace does not match the Core-derived scope.");

        if (!_recordsByIdentity.TryGetValue((query.RecordId, query.RevisionId), out var record) ||
            !IsReadable(record, scope))
        {
            return ValueTask.FromResult<CortexKnowledgeRecord?>(null);
        }

        return ValueTask.FromResult<CortexKnowledgeRecord?>(ToCoreRecord(record));
    }

    public ValueTask<CortexResolvedRecordIdentity?> ResolveAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _legacyIdentities.TryGetValue(identifier, out var identity) ? identity : null);
    }

    public ValueTask<IReadOnlyList<KnowledgeRecordV1>> ReadCandidatesAsync(
        KnowledgeRepositoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = _records.Where(record =>
            (string.IsNullOrWhiteSpace(query.Taxonomy) ||
                string.Equals(record.Taxonomy, query.Taxonomy, StringComparison.Ordinal)) &&
            record.Applicability.Any(item =>
                (string.IsNullOrWhiteSpace(query.ProductId) ||
                    string.Equals(item.ProductId, query.ProductId, StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(query.ComponentId) ||
                    string.Equals(item.ComponentId, query.ComponentId, StringComparison.Ordinal))))
            .Take(query.MaximumCandidates)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<KnowledgeRecordV1>>(candidates);
    }

    private CortexKnowledgeRecord ToCoreRecord(KnowledgeRecordV1 record)
    {
        var sourceReferences = record.Provenance.SourceRecordIds.ToArray();
        if (sourceReferences.Any(reference =>
            !_provenanceReferenceGrants.Contains((record.RecordId, record.RevisionId, reference))))
        {
            throw new InvalidOperationException("Default Library provenance reference is not independently allowlisted.");
        }

        return new CortexKnowledgeRecord(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Slug,
            record.Title,
            record.Summary,
            record.Content,
            record.Keywords.ToArray(),
            record.Applicability.Select(item => new CortexKnowledgeApplicability(
                item.ProductId,
                item.ComponentId,
                item.MinimumVersion,
                item.MaximumVersion,
                item.State)).ToArray(),
            record.Evidence.Select(item => new CortexKnowledgeEvidence(
                item.Reference,
                item.MediaType,
                item.Sha256,
                item.Verification,
                item.ObservedAt,
                item.SourceVersion)).ToArray(),
            new CortexKnowledgeProvenance(
                record.Provenance.ProducerContract,
                sourceReferences,
                SourceRevisions: [],
                record.Provenance.Transformations.ToArray()),
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
    }

    private static CortexKnowledgeRecordSummary ToSummary(KnowledgeRecordV1 record) =>
        new(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Title,
            record.Summary,
            record.Freshness.State,
            record.Sensitivity,
            record.ContentTreatment,
            Executable: false);

    private static IReadOnlyDictionary<string, CortexResolvedRecordIdentity?> CreateLegacyIdentityMap(
        IReadOnlyList<KnowledgeRecordV1> records)
    {
        var candidates = records.SelectMany(record => new[]
        {
            new KeyValuePair<string, CortexResolvedRecordIdentity>(
                record.RecordId,
                new CortexResolvedRecordIdentity(record.Namespace, record.RecordId, record.RevisionId)),
            new KeyValuePair<string, CortexResolvedRecordIdentity>(
                record.RevisionId,
                new CortexResolvedRecordIdentity(record.Namespace, record.RecordId, record.RevisionId))
        });
        var result = new Dictionary<string, CortexResolvedRecordIdentity?>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            var identities = group.Select(item => item.Value).Distinct().Take(2).ToArray();
            result.Add(group.Key, identities.Length == 1 ? identities[0] : null);
        }
        return result;
    }

    private static IReadOnlySet<(string RecordId, string RevisionId, string Reference)>
        CreateProvenanceReferenceGrants(IReadOnlyList<KnowledgeRecordV1> records)
    {
        var grants = new HashSet<(string RecordId, string RevisionId, string Reference)>();
        foreach (var record in records)
        {
            var evidenceReferences = record.Evidence
                .Select(item => item.Reference)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var reference in record.Provenance.SourceRecordIds)
            {
                if (!evidenceReferences.Contains(reference))
                    throw new InvalidOperationException("Default Library provenance is not backed by record evidence.");
                grants.Add((record.RecordId, record.RevisionId, reference));
            }
        }
        return grants;
    }

    private void ValidateScope(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken,
        CortexQueryCapability? expectedCapability = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(scope.Namespace, _namespace, StringComparison.Ordinal) ||
            !SensitivityRank.ContainsKey(scope.MaximumSensitivity) ||
            expectedCapability is not null && scope.Capability != expectedCapability ||
            !OperationMatchesCapability(scope.Operation, scope.Capability) ||
            scope.Capability is CortexQueryCapability.Discover or
                CortexQueryCapability.Status or CortexQueryCapability.Catalog &&
                (scope.IncludeStale || scope.IncludeBlockedOrUnverified))
        {
            throw new InvalidOperationException("Core-derived query scope is unsupported by the static knowledge adapter.");
        }
    }

    private static bool OperationMatchesCapability(
        string operation,
        CortexQueryCapability capability) => capability switch
    {
        CortexQueryCapability.Discover => string.Equals(operation, "cortex.query.discover", StringComparison.Ordinal),
        CortexQueryCapability.Status => string.Equals(operation, "cortex.query.status", StringComparison.Ordinal),
        CortexQueryCapability.Catalog => string.Equals(operation, "cortex.query.catalog", StringComparison.Ordinal),
        CortexQueryCapability.Search => string.Equals(operation, "cortex.query.search", StringComparison.Ordinal),
        CortexQueryCapability.Read =>
            string.Equals(operation, "cortex.query.read", StringComparison.Ordinal) ||
            string.Equals(operation, "cortex.query.provenance.read", StringComparison.Ordinal),
        _ => false
    };

    private static bool IsReadable(KnowledgeRecordV1 record, CortexQuerySourceScope scope) =>
        IsWithinStaticScope(record, scope) &&
        FreshnessAllowed(
            record.Freshness.State,
            scope.IncludeStale,
            scope.IncludeBlockedOrUnverified);

    private static bool IsWithinStaticScope(KnowledgeRecordV1 record, CortexQuerySourceScope scope) =>
        string.Equals(record.RecordStatus, "active", StringComparison.Ordinal) &&
        string.Equals(record.Namespace, scope.Namespace, StringComparison.Ordinal) &&
        !record.Executable &&
        string.Equals(record.ContentTreatment, KnowledgeContractIds.ContentTreatment, StringComparison.Ordinal) &&
        SensitivityRank.TryGetValue(record.Sensitivity, out var sensitivity) &&
        SensitivityRank.TryGetValue(scope.MaximumSensitivity, out var maximumSensitivity) &&
        sensitivity <= maximumSensitivity;

    private static bool FreshnessAllowed(
        string state,
        bool includeStale,
        bool includeBlockedOrUnverified) => state switch
    {
        "current" => true,
        "stale" => includeStale,
        "blocked" or "unverified" or "unknown" => includeBlockedOrUnverified,
        _ => false
    };

    private static void EnsureProjectionBounds(KnowledgeSeedBundleV1 bundle)
    {
        if (bundle.Records.Count > MaximumSeedRecords ||
            bundle.Taxonomies.Count > MaximumSeedTaxonomies)
        {
            throw new InvalidOperationException("Static knowledge bundle exceeds the bounded query-source collection contract.");
        }

        foreach (var taxonomy in bundle.Taxonomies)
        {
            if (taxonomy.Code.Length > 128 || taxonomy.DisplayName.Length > 256 ||
                taxonomy.Description.Length > 2048)
            {
                throw new InvalidOperationException("Static knowledge bundle taxonomy metadata exceeds the query-source bounds.");
            }
        }

        foreach (var record in bundle.Records)
        {
            if (record.Content.Length > 32 * 1024 || record.Summary.Length > 2048 ||
                record.Title.Length > 512 || record.Keywords.Count > 64 ||
                record.Applicability.Count > 32 || record.Evidence.Count > 16 ||
                record.Provenance.SourceRecordIds.Count > 64 ||
                record.Provenance.Transformations.Count > 64)
            {
                throw new InvalidOperationException($"Knowledge record '{record.RecordId}' exceeds a query-source field bound.");
            }

            var projectedStrings = new List<string?>
            {
                record.RecordId, record.RevisionId, record.Namespace, record.Taxonomy, record.Slug,
                record.Title, record.Summary, record.Content, record.Sensitivity, record.RecordStatus,
                record.ContentTreatment, record.Provenance.ProducerContract, record.Confidence.Basis,
                record.Freshness.State, record.Freshness.Basis
            };
            projectedStrings.AddRange(record.Keywords);
            projectedStrings.AddRange(record.Provenance.SourceRecordIds);
            projectedStrings.AddRange(record.Provenance.Transformations);
            foreach (var item in record.Applicability)
                projectedStrings.AddRange([item.ProductId, item.ComponentId, item.MinimumVersion, item.MaximumVersion, item.State]);
            foreach (var item in record.Evidence)
                projectedStrings.AddRange([item.Reference, item.MediaType, item.Sha256, item.Verification, item.SourceVersion]);

            if (projectedStrings.Any(value => value?.Length > 4096) ||
                projectedStrings.Sum(value => (long)(value?.Length ?? 0)) > MaximumRecordTextCharacters)
            {
                throw new InvalidOperationException($"Knowledge record '{record.RecordId}' exceeds the bounded query-source projection budget.");
            }
        }
    }
}
