using System.Collections.Frozen;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Query;

public sealed class CortexQueryService : ICortexQueryService
{
    private static readonly IReadOnlyDictionary<string, int> SensitivityRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["public"] = 0,
            ["internal"] = 1,
            ["confidential"] = 2,
            ["restricted"] = 3
        };

    private readonly ICortexQuerySource _source;
    private readonly CortexQueryServiceOptions _options;

    public CortexQueryService(
        ICortexQuerySource source,
        CortexQueryServiceOptions options)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = CaptureOptions(options ?? throw new ArgumentNullException(nameof(options)));
    }

    public async ValueTask<CortexQueryResult<CortexQueryDiscovery>> DiscoverAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAuthority = CaptureAuthority(authority);
        if (!TryAuthorize(
                capturedAuthority,
                _options.DefaultNamespace,
                CortexQueryCapability.Discover,
                includeStale: false,
                includeBlockedOrUnverified: false,
                "cortex.query.discover",
                out var scope,
                out var denied))
        {
            return Rejected<CortexQueryDiscovery>(denied!);
        }

        var snapshotResult = await GetSnapshotAsync(scope!, "cortex.query.discover", cancellationToken)
            .ConfigureAwait(false);
        if (!snapshotResult.Accepted)
            return Rejected<CortexQueryDiscovery>(snapshotResult.Diagnostics);

        var snapshot = snapshotResult.Value!;
        return Accepted(new CortexQueryDiscovery(
            _options.Mode,
            snapshot.DefaultNamespace,
            snapshot.DefaultReadableRecordCount,
            snapshot.Taxonomies.ToArray(),
            snapshot.ContentTreatment,
            snapshot.Executable,
            snapshot.SafetyNotice));
    }

    public async ValueTask<CortexQueryResult<CortexQueryStatus>> GetStatusAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAuthority = CaptureAuthority(authority);
        if (!TryAuthorize(
                capturedAuthority,
                _options.DefaultNamespace,
                CortexQueryCapability.Status,
                includeStale: false,
                includeBlockedOrUnverified: false,
                "cortex.query.status",
                out var scope,
                out var denied))
        {
            return Rejected<CortexQueryStatus>(denied!);
        }

        var snapshotResult = await GetSnapshotAsync(scope!, "cortex.query.status", cancellationToken)
            .ConfigureAwait(false);
        if (!snapshotResult.Accepted)
            return Rejected<CortexQueryStatus>(snapshotResult.Diagnostics);

        var snapshot = snapshotResult.Value!;
        return Accepted(new CortexQueryStatus(
            "ready",
            _options.Mode,
            _options.ValidationState,
            snapshot.ReleaseVersion,
            snapshot.PackageVersion,
            snapshot.CollectionId,
            snapshot.CollectionHash,
            snapshot.DefaultNamespace,
            snapshot.RecordCount,
            snapshot.DefaultReadableRecordCount,
            snapshot.RecordCount - snapshot.DefaultReadableRecordCount,
            snapshot.Taxonomies.ToArray(),
            snapshot.Freshness.Select(item => new CortexFreshnessCount(item.State, item.Count)).ToArray(),
            snapshot.ContentTreatment,
            snapshot.Executable,
            snapshot.SafetyNotice));
    }

    public async ValueTask<CortexQueryResult<CortexKnowledgeCatalog>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = new CortexCatalogQuery(query.Namespace, query.Limit);
        if (!ValidNamespace(capturedQuery.Namespace) ||
            capturedQuery.Limit is < 1 or > CortexQueryLimits.MaximumCatalogRecords)
        {
            return Rejected<CortexKnowledgeCatalog>(Diagnostic(
                "cortex_query_catalog_request_invalid",
                "The catalog namespace or result limit is invalid.",
                "cortex.query.catalog"));
        }

        var capturedAuthority = CaptureAuthority(authority);
        if (!TryAuthorize(
                capturedAuthority,
                capturedQuery.Namespace,
                CortexQueryCapability.Catalog,
                includeStale: false,
                includeBlockedOrUnverified: false,
                "cortex.query.catalog",
                out var scope,
                out var denied))
        {
            return Rejected<CortexKnowledgeCatalog>(denied!);
        }

        var snapshotResult = await GetSnapshotAsync(scope!, "cortex.query.catalog", cancellationToken)
            .ConfigureAwait(false);
        if (!snapshotResult.Accepted)
            return Rejected<CortexKnowledgeCatalog>(snapshotResult.Diagnostics);
        var snapshot = snapshotResult.Value!;

        try
        {
            var sourceRecords = await _source.ListCatalogAsync(capturedQuery, scope!, cancellationToken)
                .ConfigureAwait(false);
            if (!TryCaptureCatalog(sourceRecords, capturedQuery, scope!, snapshot, out var records))
            {
                return Rejected<CortexKnowledgeCatalog>(Diagnostic(
                    "cortex_query_source_catalog_invalid",
                    "The query source returned an invalid or cross-scope catalog.",
                    "cortex.query.catalog"));
            }

            return Accepted(new CortexKnowledgeCatalog(
                snapshot.ReleaseVersion,
                records!,
                snapshot.SafetyNotice));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SourceFailure<CortexKnowledgeCatalog>("cortex.query.catalog", exception);
        }
    }

    public async ValueTask<CortexQueryResult<CortexKnowledgeSearchPage>> SearchAsync(
        CortexSearchQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = CaptureSearchQuery(query);
        if (!ValidSearch(capturedQuery))
        {
            return Rejected<CortexKnowledgeSearchPage>(Diagnostic(
                "cortex_query_search_request_invalid",
                "The search namespace, text, filters, or result limit is invalid.",
                "cortex.query.search"));
        }

        var capturedAuthority = CaptureAuthority(authority);
        if (!TryAuthorize(
                capturedAuthority,
                capturedQuery.Namespace,
                CortexQueryCapability.Search,
                capturedQuery.IncludeStale,
                capturedQuery.IncludeBlockedOrUnverified,
                "cortex.query.search",
                out var scope,
                out var denied))
        {
            return Rejected<CortexKnowledgeSearchPage>(denied!);
        }

        var snapshotResult = await GetSnapshotAsync(scope!, "cortex.query.search", cancellationToken)
            .ConfigureAwait(false);
        if (!snapshotResult.Accepted)
            return Rejected<CortexKnowledgeSearchPage>(snapshotResult.Diagnostics);
        var snapshot = snapshotResult.Value!;

        if (capturedQuery.Taxonomy is not null &&
            !snapshot.Taxonomies.Contains(capturedQuery.Taxonomy, StringComparer.Ordinal))
        {
            return Rejected<CortexKnowledgeSearchPage>(Diagnostic(
                "cortex_search_taxonomy_unknown",
                "The requested taxonomy is not in the authorized knowledge collection.",
                "cortex.query.search"));
        }

        try
        {
            var sourceResult = await _source.SearchAsync(capturedQuery, scope!, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceResult.Accepted)
            {
                return Rejected<CortexKnowledgeSearchPage>(NormalizeSourceDiagnostic(
                    sourceResult.Diagnostic,
                    "cortex_query_source_search_rejected",
                    "The bounded knowledge query was rejected.",
                    "cortex.query.search"));
            }

            if (sourceResult.Diagnostic is not null ||
                !TryCopyBounded(sourceResult.Records, capturedQuery.Limit, out var sourceHits))
            {
                return Rejected<CortexKnowledgeSearchPage>(Diagnostic(
                    "cortex_query_source_search_invalid",
                    "The query source returned an invalid or unbounded search page.",
                    "cortex.query.search"));
            }

            var records = new List<CortexKnowledgeSearchHit>(sourceHits!.Length);
            var identities = new HashSet<(string RecordId, string RevisionId)>();
            var provenance = new ProvenanceValidationContext(snapshot);
            foreach (var hit in sourceHits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hit is null || hit.Record is null || !double.IsFinite(hit.Score) ||
                    !string.Equals(hit.SafetyNotice, snapshot.SafetyNotice, StringComparison.Ordinal) ||
                    !identities.Add((hit.Record.RecordId, hit.Record.RevisionId)))
                {
                    return Rejected<CortexKnowledgeSearchPage>(Diagnostic(
                        "cortex_query_source_search_invalid",
                        "The query source returned an invalid or duplicate search record.",
                        "cortex.query.search"));
                }

                var captured = await CaptureAuthorizedRecordAsync(
                    hit.Record,
                    scope!,
                    capturedAuthority,
                    snapshot,
                    provenance,
                    "cortex.query.search",
                    cancellationToken).ConfigureAwait(false);
                if (!captured.Accepted)
                    return Rejected<CortexKnowledgeSearchPage>(captured.Diagnostics);
                // Recompute match evidence from the defensively captured record.
                // Provider metadata is advisory input and never bypasses Core validation.
                var lexicalMatch = CortexKnowledgeLexicalSearch.Match(captured.Value!, capturedQuery);
                if (!lexicalMatch.IsMatch)
                {
                    return Rejected<CortexKnowledgeSearchPage>(Diagnostic(
                        "cortex_query_source_search_invalid",
                        "The query source returned a record outside the lexical search contract.",
                        "cortex.query.search"));
                }
                records.Add(new CortexKnowledgeSearchHit(
                    captured.Value!,
                    hit.Score,
                    hit.SafetyNotice,
                    lexicalMatch.MatchedTerms,
                    lexicalMatch.QueryTermCount));
            }

            return Accepted(new CortexKnowledgeSearchPage(
                capturedQuery,
                records.ToArray(),
                snapshot.SafetyNotice));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SourceFailure<CortexKnowledgeSearchPage>("cortex.query.search", exception);
        }
    }

    public async ValueTask<CortexQueryResult<CortexKnowledgeRecord>> ReadAsync(
        CortexRecordReadQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = new CortexRecordReadQuery(
            query.Namespace,
            query.RecordId,
            query.RevisionId,
            query.IncludeStale,
            query.IncludeBlockedOrUnverified);
        if (!ValidNamespace(capturedQuery.Namespace) ||
            !ValidIdentifier(capturedQuery.RecordId) ||
            !ValidIdentifier(capturedQuery.RevisionId))
        {
            return Rejected<CortexKnowledgeRecord>(Diagnostic(
                "cortex_query_read_request_invalid",
                "The exact namespace, record identifier, or revision identifier is invalid.",
                "cortex.query.read"));
        }

        var capturedAuthority = CaptureAuthority(authority);
        if (!TryAuthorize(
                capturedAuthority,
                capturedQuery.Namespace,
                CortexQueryCapability.Read,
                capturedQuery.IncludeStale,
                capturedQuery.IncludeBlockedOrUnverified,
                "cortex.query.read",
                out var scope,
                out var denied))
        {
            return Rejected<CortexKnowledgeRecord>(denied!);
        }

        var snapshotResult = await GetSnapshotAsync(scope!, "cortex.query.read", cancellationToken)
            .ConfigureAwait(false);
        if (!snapshotResult.Accepted)
            return Rejected<CortexKnowledgeRecord>(snapshotResult.Diagnostics);
        var snapshot = snapshotResult.Value!;

        try
        {
            var record = await _source.ReadAsync(capturedQuery, scope!, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_record_not_available",
                    "The record is unavailable in the requested read scope or does not exist.",
                    "cortex.query.read"));
            }

            if (!string.Equals(record.RecordId, capturedQuery.RecordId, StringComparison.Ordinal) ||
                !string.Equals(record.RevisionId, capturedQuery.RevisionId, StringComparison.Ordinal))
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_query_source_record_invalid",
                    "The query source returned a different record identity.",
                    "cortex.query.read"));
            }

            return await CaptureAuthorizedRecordAsync(
                record,
                scope!,
                capturedAuthority,
                snapshot,
                new ProvenanceValidationContext(snapshot),
                "cortex.query.read",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SourceFailure<CortexKnowledgeRecord>("cortex.query.read", exception);
        }
    }

    private async ValueTask<CortexQueryResult<CortexKnowledgeSourceSnapshot>> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _source.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);
            if (!TryCaptureSnapshot(snapshot, scope, out var captured))
            {
                return Rejected<CortexKnowledgeSourceSnapshot>(Diagnostic(
                    "cortex_query_source_snapshot_invalid",
                    "The query source returned invalid or cross-scope collection metadata.",
                    operation));
            }

            return Accepted(captured!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SourceFailure<CortexKnowledgeSourceSnapshot>(operation, exception);
        }
    }

    private async ValueTask<CortexQueryResult<CortexKnowledgeRecord>> CaptureAuthorizedRecordAsync(
        CortexKnowledgeRecord record,
        CortexQuerySourceScope scope,
        CortexQueryAuthority authority,
        CortexKnowledgeSourceSnapshot snapshot,
        ProvenanceValidationContext provenance,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!TryCaptureRecord(record, scope, snapshot, out var captured))
        {
            return Rejected<CortexKnowledgeRecord>(Diagnostic(
                "cortex_query_source_record_invalid",
                "The query source returned an invalid or cross-scope record.",
                operation));
        }

        foreach (var link in captured!.Provenance.SourceRevisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var linkKey = (link.Namespace, link.RecordId, link.RevisionId, link.ContentHash);
            if (!provenance.TryReserve())
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_query_provenance_budget_exceeded",
                    "Structured provenance exceeds the bounded response validation budget.",
                    operation));
            }
            if (provenance.IsValidated(linkKey))
                continue;

            if (!ValidNamespace(link.Namespace) ||
                !ValidIdentifier(link.RecordId) ||
                !ValidIdentifier(link.RevisionId) ||
                !ValidSha256(link.ContentHash))
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_query_provenance_target_denied",
                    "A structured provenance target is incomplete or outside current authority.",
                    operation));
            }

            if (!TryAuthorize(
                    authority,
                    link.Namespace,
                    CortexQueryCapability.Read,
                    scope.IncludeStale,
                    scope.IncludeBlockedOrUnverified,
                    "cortex.query.provenance.read",
                    out var targetScope,
                    out _))
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_query_provenance_target_denied",
                    "A structured provenance target is incomplete or outside current authority.",
                    operation));
            }
            CortexKnowledgeSourceSnapshot? targetSnapshot;
            if (!provenance.TryGetSnapshot(link.Namespace, out targetSnapshot))
            {
                var targetSnapshotValue = await _source.GetSnapshotAsync(targetScope!, cancellationToken)
                    .ConfigureAwait(false);
                if (!TryCaptureSnapshot(targetSnapshotValue, targetScope!, out targetSnapshot))
                {
                    return Rejected<CortexKnowledgeRecord>(Diagnostic(
                        "cortex_query_provenance_target_denied",
                        "A structured provenance target has no authorized collection metadata.",
                        operation));
                }
                provenance.AddSnapshot(link.Namespace, targetSnapshot!);
            }
            var target = await _source.ReadAsync(
                new CortexRecordReadQuery(
                    link.Namespace,
                    link.RecordId,
                    link.RevisionId,
                    scope.IncludeStale,
                    scope.IncludeBlockedOrUnverified),
                targetScope!,
                cancellationToken).ConfigureAwait(false);
            if (target is null ||
                !string.Equals(target.Namespace, link.Namespace, StringComparison.Ordinal) ||
                !string.Equals(target.RecordId, link.RecordId, StringComparison.Ordinal) ||
                !string.Equals(target.RevisionId, link.RevisionId, StringComparison.Ordinal) ||
                !string.Equals(target.ContentHash, link.ContentHash, StringComparison.Ordinal) ||
                !TryCaptureRecord(target, targetScope!, targetSnapshot!, out _))
            {
                return Rejected<CortexKnowledgeRecord>(Diagnostic(
                    "cortex_query_provenance_target_denied",
                    "A structured provenance target is unavailable or outside current authority.",
                    operation));
            }
            provenance.MarkValidated(linkKey);
        }

        return Accepted(captured);
    }

    private static bool TryAuthorize(
        CortexQueryAuthority authority,
        string namespaceName,
        CortexQueryCapability capability,
        bool includeStale,
        bool includeBlockedOrUnverified,
        string operation,
        out CortexQuerySourceScope? scope,
        out PegasusDiagnostic? diagnostic)
    {
        scope = null;
        diagnostic = null;
        if (!ValidAuthorityValue(authority.TenantId) ||
            !ValidAuthorityValue(authority.AuthorityId) ||
            !ValidAuthorityValue(authority.SubjectId) ||
            !ValidAuthorityValue(authority.ServiceId) ||
            authority.PolicyRevision <= 0 ||
            authority.Grants.Count is < 1 or > CortexQueryLimits.MaximumAuthorityGrants ||
            authority.Grants.Any(grant =>
                grant is null || !ValidNamespace(grant.Namespace) ||
                !SensitivityRank.ContainsKey(grant.MaximumSensitivity) ||
                grant.Capabilities.Count is < 1 or > CortexQueryLimits.MaximumKnownCapabilities ||
                grant.Capabilities.Any(capability => !Enum.IsDefined(capability))) ||
            authority.Grants.Select(grant => grant.Namespace).Distinct(StringComparer.Ordinal).Count() !=
                authority.Grants.Count)
        {
            diagnostic = Diagnostic(
                "cortex_query_authority_invalid",
                "Server-derived query authority is incomplete or invalid.",
                operation);
            return false;
        }

        var grant = authority.Grants.SingleOrDefault(item =>
            string.Equals(item.Namespace, namespaceName, StringComparison.Ordinal));
        if (grant is null || !grant.Capabilities.Contains(capability) ||
            includeStale && !grant.AllowStale ||
            includeBlockedOrUnverified && !grant.AllowBlockedOrUnverified)
        {
            diagnostic = Diagnostic(
                "cortex_query_access_denied",
                "The requested query operation is outside current server-derived authority.",
                operation);
            return false;
        }

        scope = new CortexQuerySourceScope(
            authority.TenantId,
            authority.AuthorityId,
            authority.SubjectId,
            authority.ServiceId,
            authority.PolicyRevision,
            namespaceName,
            capability,
            operation,
            grant.MaximumSensitivity,
            includeStale,
            includeBlockedOrUnverified);
        return true;
    }

    private static CortexQueryAuthority CaptureAuthority(CortexQueryAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new CortexQueryAuthority(
            authority.TenantId,
            authority.AuthorityId,
            authority.SubjectId,
            authority.ServiceId,
            authority.PolicyRevision,
            authority.Grants.Select(grant => new CortexQueryNamespaceGrant(
                grant.Namespace,
                grant.MaximumSensitivity,
                grant.Capabilities.ToArray(),
                grant.AllowStale,
                grant.AllowBlockedOrUnverified)).ToArray());
    }

    private static CortexQueryServiceOptions CaptureOptions(CortexQueryServiceOptions options)
    {
        if (!ValidNamespace(options.DefaultNamespace) ||
            !ValidBounded(options.Mode, 64) ||
            !ValidBounded(options.ValidationState, 64))
        {
            throw new ArgumentException("Cortex query service options are invalid.", nameof(options));
        }

        return new CortexQueryServiceOptions(
            options.DefaultNamespace,
            options.Mode,
            options.ValidationState);
    }

    private static CortexSearchQuery CaptureSearchQuery(CortexSearchQuery query) =>
        new(
            query.Namespace,
            query.Text?.Trim()!,
            query.Taxonomy?.Trim(),
            query.ProductId?.Trim(),
            query.ComponentId?.Trim(),
            query.Limit,
            query.IncludeStale,
            query.IncludeBlockedOrUnverified,
            query.Mode);

    private static bool TryCaptureSnapshot(
        CortexKnowledgeSourceSnapshot? snapshot,
        CortexQuerySourceScope scope,
        out CortexKnowledgeSourceSnapshot? captured)
    {
        captured = null;
        if (snapshot is null ||
            !TryCopyBounded(snapshot.Taxonomies, CortexQueryLimits.MaximumTaxonomies, out var taxonomies) ||
            !TryCopyBounded(snapshot.Freshness, CortexQueryLimits.MaximumTaxonomies, out var freshness) ||
            !ValidBounded(snapshot.ReleaseVersion, 128) ||
            !ValidBounded(snapshot.PackageVersion, 128) ||
            !ValidBounded(snapshot.CollectionId, 256) ||
            !ValidSha256(snapshot.CollectionHash) ||
            !string.Equals(snapshot.DefaultNamespace, scope.Namespace, StringComparison.Ordinal) ||
            snapshot.RecordCount < 0 || snapshot.DefaultReadableRecordCount < 0 ||
            snapshot.DefaultReadableRecordCount > snapshot.RecordCount ||
            taxonomies!.Any(item => !ValidBounded(item, 128)) ||
            taxonomies!.Distinct(StringComparer.Ordinal).Count() != taxonomies!.Length ||
            freshness!.Any(item => item is null || !ValidFreshnessState(item.State) || item.Count < 0) ||
            freshness!.Select(item => item.State).Distinct(StringComparer.Ordinal).Count() != freshness!.Length ||
            snapshot.DefaultReadableRecordCount !=
                (freshness!.FirstOrDefault(item =>
                    string.Equals(item.State, "current", StringComparison.Ordinal))?.Count ?? 0) ||
            !TrySumFreshness(freshness, snapshot.RecordCount) ||
            !string.Equals(snapshot.ContentTreatment, CortexQueryPolicy.ContentTreatment, StringComparison.Ordinal) ||
            snapshot.Executable ||
            !string.Equals(snapshot.SafetyNotice, CortexQueryPolicy.SafetyNotice, StringComparison.Ordinal))
        {
            return false;
        }

        captured = new CortexKnowledgeSourceSnapshot(
            snapshot.ReleaseVersion,
            snapshot.PackageVersion,
            snapshot.CollectionId,
            snapshot.CollectionHash,
            snapshot.DefaultNamespace,
            snapshot.RecordCount,
            snapshot.DefaultReadableRecordCount,
            taxonomies.Order(StringComparer.Ordinal).ToArray(),
            freshness.OrderBy(item => item.State, StringComparer.Ordinal)
                .Select(item => new CortexFreshnessCount(item.State, item.Count)).ToArray(),
            snapshot.ContentTreatment,
            Executable: false,
            snapshot.SafetyNotice);
        return true;
    }

    private static bool TryCaptureCatalog(
        IReadOnlyList<CortexKnowledgeRecordSummary>? sourceRecords,
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CortexKnowledgeSourceSnapshot snapshot,
        out IReadOnlyList<CortexKnowledgeRecordSummary>? captured)
    {
        captured = null;
        var maximum = Math.Min(query.Limit, snapshot.DefaultReadableRecordCount);
        if (!TryCopyBounded(sourceRecords, maximum, out var sourceRecordSnapshot))
        {
            return false;
        }

        var identities = new HashSet<(string RecordId, string RevisionId)>();
        var records = new List<CortexKnowledgeRecordSummary>(sourceRecordSnapshot!.Length);
        foreach (var record in sourceRecordSnapshot)
        {
            if (record is null ||
                !ValidIdentifier(record.RecordId) ||
                !ValidIdentifier(record.RevisionId) ||
                !string.Equals(record.Namespace, scope.Namespace, StringComparison.Ordinal) ||
                !snapshot.Taxonomies.Contains(record.Taxonomy, StringComparer.Ordinal) ||
                !ValidBounded(record.Title, 512) || !ValidBounded(record.Summary, 2048) ||
                !string.Equals(record.Freshness, "current", StringComparison.Ordinal) ||
                !SensitivityAllowed(record.Sensitivity, scope.MaximumSensitivity) ||
                !string.Equals(record.ContentTreatment, snapshot.ContentTreatment, StringComparison.Ordinal) ||
                record.Executable || !identities.Add((record.RecordId, record.RevisionId)))
            {
                return false;
            }

            records.Add(new CortexKnowledgeRecordSummary(
                record.RecordId,
                record.RevisionId,
                record.Namespace,
                record.Taxonomy,
                record.Title,
                record.Summary,
                record.Freshness,
                record.Sensitivity,
                record.ContentTreatment,
                Executable: false));
        }

        captured = records
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .ThenBy(record => record.RevisionId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool TryCaptureRecord(
        CortexKnowledgeRecord? record,
        CortexQuerySourceScope scope,
        CortexKnowledgeSourceSnapshot snapshot,
        out CortexKnowledgeRecord? captured)
    {
        captured = null;
        if (record is null || record.Provenance is null ||
            !TryCopyBounded(record.Keywords, 64, out var keywords) ||
            !TryCopyBounded(record.Applicability, 32, out var applicabilityValues) ||
            !TryCopyBounded(record.Evidence, 16, out var evidenceValues) ||
            !TryCopyBounded(
                record.Provenance.SourceReferences,
                CortexQueryLimits.MaximumProvenanceReferences,
                out var sourceReferences) ||
            !TryCopyBounded(
                record.Provenance.SourceRevisions,
                CortexQueryLimits.MaximumProvenanceReferences,
                out var sourceRevisionValues) ||
            !TryCopyBounded(
                record.Provenance.Transformations,
                CortexQueryLimits.MaximumProvenanceReferences,
                out var transformations) ||
            !ValidIdentifier(record.RecordId) ||
            !ValidIdentifier(record.RevisionId) ||
            !string.Equals(record.Namespace, scope.Namespace, StringComparison.Ordinal) ||
            !snapshot.Taxonomies.Contains(record.Taxonomy, StringComparer.Ordinal) ||
            !ValidBounded(record.Slug, 256) || !ValidBounded(record.Title, 512) ||
            !ValidBounded(record.Summary, 2048) || !ValidBounded(record.Content, 32 * 1024) ||
            record.Confidence is null || record.Freshness is null ||
            !ValidBounded(record.Provenance.ProducerContract, 256) ||
            !keywords!.All(item => ValidBounded(item, 256)) ||
            !applicabilityValues!.All(ValidApplicability) || !evidenceValues!.All(ValidEvidence) ||
            !sourceReferences!.All(item => ValidBounded(item, 4096)) ||
            !sourceRevisionValues!.All(item =>
                item is not null && ValidNamespace(item.Namespace) &&
                ValidIdentifier(item.RecordId) &&
                ValidIdentifier(item.RevisionId) && ValidSha256(item.ContentHash)) ||
            !transformations!.All(item => ValidBounded(item, 256)) ||
            !sourceReferences!.All(reference =>
                evidenceValues!.Any(evidence => string.Equals(evidence.Reference, reference, StringComparison.Ordinal))) ||
            !double.IsFinite(record.Confidence.Value) || record.Confidence.Value is < 0 or > 1 ||
            !ValidBounded(record.Confidence.Basis, 2048) ||
            !ValidBounded(record.Freshness.State, 64) || !ValidBounded(record.Freshness.Basis, 2048) ||
            !ValidFreshnessTimestamps(record.Freshness) ||
            !FreshnessAllowed(record.Freshness.State, scope) ||
            !SensitivityAllowed(record.Sensitivity, scope.MaximumSensitivity) ||
            !string.Equals(record.RecordStatus, "active", StringComparison.Ordinal) ||
            !string.Equals(record.ContentTreatment, snapshot.ContentTreatment, StringComparison.Ordinal) ||
            record.Executable || !ValidSha256(record.ContentHash))
        {
            return false;
        }

        var applicability = applicabilityValues!.Select(item => new CortexKnowledgeApplicability(
            item.ProductId, item.ComponentId, item.MinimumVersion, item.MaximumVersion, item.State)).ToArray();
        var evidence = evidenceValues!.Select(item => new CortexKnowledgeEvidence(
            item.Reference, item.MediaType, item.Sha256, item.Verification, item.ObservedAt, item.SourceVersion)).ToArray();
        var sourceRevisions = sourceRevisionValues!.Select(item => new CortexKnowledgeSourceRevision(
            item.Namespace, item.RecordId, item.RevisionId, item.ContentHash)).ToArray();
        captured = new CortexKnowledgeRecord(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.Taxonomy,
            record.Slug,
            record.Title,
            record.Summary,
            record.Content,
            keywords!,
            applicability,
            evidence,
            new CortexKnowledgeProvenance(
                record.Provenance.ProducerContract,
                sourceReferences!,
                sourceRevisions,
                transformations!),
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
        return true;
    }

    private static bool ValidSearch(CortexSearchQuery query) =>
        ValidNamespace(query.Namespace) &&
        !string.IsNullOrWhiteSpace(query.Text) &&
        query.Text.Length <= CortexQueryLimits.MaximumSearchTextCharacters &&
        !query.Text.Any(char.IsControl) &&
        ValidOptionalFilter(query.Taxonomy) &&
        ValidOptionalFilter(query.ProductId) &&
        ValidOptionalFilter(query.ComponentId) &&
        Enum.IsDefined(query.Mode) &&
        query.Limit is >= 1 and <= CortexQueryLimits.MaximumSearchResults;

    private static bool ValidApplicability(CortexKnowledgeApplicability item) =>
        item is not null && ValidBounded(item.ProductId, 256) && ValidBounded(item.ComponentId, 256) &&
        ValidOptionalBounded(item.MinimumVersion, 128) && ValidOptionalBounded(item.MaximumVersion, 128) &&
        item.State is "verified" or "blocked" or "unverified" or "unknown" or "stale";

    private static bool ValidEvidence(CortexKnowledgeEvidence item) =>
        item is not null && ValidBounded(item.Reference, 4096) && ValidBounded(item.MediaType, 256) &&
        ValidSha256(item.Sha256) && ValidBounded(item.Verification, 128) &&
        item.ObservedAt.Offset == TimeSpan.Zero && ValidOptionalBounded(item.SourceVersion, 128);

    private static bool FreshnessAllowed(string state, CortexQuerySourceScope scope) => state switch
    {
        "current" => true,
        "stale" => scope.IncludeStale,
        "blocked" or "unverified" or "unknown" => scope.IncludeBlockedOrUnverified,
        _ => false
    };

    private static bool ValidFreshnessTimestamps(CortexKnowledgeFreshness freshness) =>
        freshness.ObservedAt.Offset == TimeSpan.Zero &&
        (freshness.ReviewAfter is null ||
            freshness.ReviewAfter.Value.Offset == TimeSpan.Zero &&
            freshness.ReviewAfter.Value >= freshness.ObservedAt);

    private static bool SensitivityAllowed(string sensitivity, string maximumSensitivity) =>
        SensitivityRank.TryGetValue(sensitivity, out var actual) &&
        SensitivityRank.TryGetValue(maximumSensitivity, out var maximum) && actual <= maximum;

    private static bool TrySumFreshness(IReadOnlyList<CortexFreshnessCount> values, int expected)
    {
        try
        {
            return values.Aggregate(0, (sum, item) => checked(sum + item.Count)) == expected;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryCopyBounded<T>(
        IReadOnlyList<T>? source,
        int maximumCount,
        out T[]? captured)
    {
        captured = null;
        if (source is null || maximumCount < 0)
            return false;

        // Enumerate at most max + 1 so a mutable or dishonest provider Count cannot bypass bounds.
        var values = new List<T>(Math.Min(maximumCount, 64));
        foreach (var value in source)
        {
            if (values.Count == maximumCount)
                return false;
            values.Add(value);
        }

        captured = values.ToArray();
        return true;
    }

    private static bool ValidFreshnessState(string? value) =>
        value is "current" or "stale" or "blocked" or "unverified" or "unknown";

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidAuthorityValue(string? value) =>
        ValidIdentifier(value) && value!.Length <= CortexQueryLimits.MaximumAuthorityValueCharacters;

    private static bool ValidNamespace(string? value) =>
        ValidBounded(value, CortexQueryLimits.MaximumAuthorityValueCharacters) &&
        string.Equals(value, value!.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidOptionalFilter(string? value) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryLimits.MaximumFilterCharacters &&
        !value.Any(char.IsControl);

    private static bool ValidOptionalBounded(string? value, int maximum) =>
        value is null || value.Length <= maximum;

    private static bool ValidBounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static CortexQueryResult<T> Accepted<T>(T value) => new(true, value, []);

    private static CortexQueryResult<T> Rejected<T>(PegasusDiagnostic diagnostic) =>
        new(false, default, [diagnostic]);

    private static CortexQueryResult<T> Rejected<T>(IReadOnlyList<PegasusDiagnostic> diagnostics) =>
        new(false, default, diagnostics.Select(SnapshotDiagnostic).ToArray());

    private static CortexQueryResult<T> SourceFailure<T>(string operation, Exception exception) =>
        Rejected<T>(Diagnostic(
            "cortex_query_source_failure",
            "The query source failed while processing the authorized request.",
            operation,
            retryable: true,
            cause: exception.GetType().FullName));

    private static PegasusDiagnostic NormalizeSourceDiagnostic(
        PegasusDiagnostic? diagnostic,
        string fallbackCode,
        string fallbackMessage,
        string operation)
    {
        if (diagnostic is null)
            return Diagnostic(fallbackCode, fallbackMessage, operation);

        return Diagnostic(fallbackCode, fallbackMessage, operation, diagnostic.Retryable, "source_rejected");
    }

    private static PegasusDiagnostic SnapshotDiagnostic(PegasusDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity,
            diagnostic.Operation,
            diagnostic.Retryable,
            diagnostic.Context is null
                ? null
                : diagnostic.Context.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            diagnostic.Cause);

    private static PegasusDiagnostic Diagnostic(
        string code,
        string message,
        string operation,
        bool retryable = false,
        string? cause = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            operation,
            retryable,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            cause);

    private sealed class ProvenanceValidationContext(CortexKnowledgeSourceSnapshot rootSnapshot)
    {
        private readonly Dictionary<string, CortexKnowledgeSourceSnapshot> _snapshots =
            new(StringComparer.Ordinal) { [rootSnapshot.DefaultNamespace] = rootSnapshot };
        private readonly HashSet<(string Namespace, string RecordId, string RevisionId, string ContentHash)> _validated = [];
        private int _remaining = CortexQueryLimits.MaximumProvenanceReferences;

        public bool IsValidated((string Namespace, string RecordId, string RevisionId, string ContentHash) key) =>
            _validated.Contains(key);

        public bool TryReserve()
        {
            if (_remaining == 0)
                return false;
            _remaining--;
            return true;
        }

        public bool TryGetSnapshot(string namespaceName, out CortexKnowledgeSourceSnapshot? snapshot) =>
            _snapshots.TryGetValue(namespaceName, out snapshot);

        public void AddSnapshot(string namespaceName, CortexKnowledgeSourceSnapshot snapshot) =>
            _snapshots.Add(namespaceName, snapshot);

        public void MarkValidated((string Namespace, string RecordId, string RevisionId, string ContentHash) key) =>
            _validated.Add(key);
    }
}
