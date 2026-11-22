using System.Collections;
using Pegasus.Cortex.Core.Query;
using Pegasus.Shared.Diagnostics;

internal static class QueryServiceTests
{
    public static async Task AcceptedDelegationAsync()
    {
        var source = new FakeCortexQuerySource();
        var service = QuerySamples.Service(source);
        var capabilities = Enum.GetValues<CortexQueryCapability>().ToList();
        var grants = new List<CortexQueryNamespaceGrant>
        {
            QuerySamples.Grant(
                capabilities: capabilities,
                allowStale: true,
                allowBlockedOrUnverified: true)
        };
        var authority = QuerySamples.Authority(grants: grants);

        // Server authority must retain its own bounded snapshot of caller-owned collections.
        capabilities.Clear();
        grants.Clear();

        var catalogQuery = new CortexCatalogQuery(QuerySamples.DefaultNamespace, 10);
        var searchQuery = new CortexSearchQuery(
            QuerySamples.DefaultNamespace,
            "bounded query",
            "testing",
            "pegasus-cortex",
            "query-service",
            5,
            IncludeStale: true,
            IncludeBlockedOrUnverified: false,
            Mode: CortexSearchMode.Broad);
        var readQuery = new CortexRecordReadQuery(
            QuerySamples.DefaultNamespace,
            QuerySamples.RecordId,
            QuerySamples.RevisionId,
            IncludeStale: false,
            IncludeBlockedOrUnverified: true);

        var discovery = await service.DiscoverAsync(authority, CancellationToken.None);
        var status = await service.GetStatusAsync(authority, CancellationToken.None);
        var catalog = await service.ListCatalogAsync(catalogQuery, authority, CancellationToken.None);
        var search = await service.SearchAsync(searchQuery, authority, CancellationToken.None);
        var read = await service.ReadAsync(readQuery, authority, CancellationToken.None);

        QueryAssert.True(discovery.Accepted);
        QueryAssert.True(status.Accepted);
        QueryAssert.True(catalog.Accepted);
        QueryAssert.True(search.Accepted);
        QueryAssert.True(read.Accepted);
        QueryAssert.Equal(5, source.SnapshotScopes.Count);
        QueryAssert.Equal(1, source.CatalogScopes.Count);
        QueryAssert.Equal(1, source.SearchScopes.Count);
        QueryAssert.Equal(1, source.ReadScopes.Count);
        QueryAssert.Equal(catalogQuery, source.CatalogQueries.Single());
        QueryAssert.Equal(searchQuery, source.SearchQueries.Single());
        QueryAssert.Equal(readQuery, source.ReadQueries.Single());

        AssertScope(source.SnapshotScopes[0], CortexQueryCapability.Discover, "cortex.query.discover", false, false);
        AssertScope(source.SnapshotScopes[1], CortexQueryCapability.Status, "cortex.query.status", false, false);
        AssertScope(source.SnapshotScopes[2], CortexQueryCapability.Catalog, "cortex.query.catalog", false, false);
        AssertScope(source.SnapshotScopes[3], CortexQueryCapability.Search, "cortex.query.search", true, false);
        AssertScope(source.SnapshotScopes[4], CortexQueryCapability.Read, "cortex.query.read", false, true);
        AssertScope(source.CatalogScopes.Single(), CortexQueryCapability.Catalog, "cortex.query.catalog", false, false);
        AssertScope(source.SearchScopes.Single(), CortexQueryCapability.Search, "cortex.query.search", true, false);
        AssertScope(source.ReadScopes.Single(), CortexQueryCapability.Read, "cortex.query.read", false, true);

        var discoveryValue = discovery.Value!;
        var statusValue = status.Value!;
        var catalogValue = catalog.Value!;
        var searchValue = search.Value!;
        var readValue = read.Value!;
        QueryAssert.Equal(QuerySamples.DefaultNamespace, discoveryValue.DefaultNamespace);
        QueryAssert.Equal(1, statusValue.ExcludedByDefaultCount);
        QueryAssert.Equal(QuerySamples.RecordId, catalogValue.Records.Single().RecordId);
        var searchHit = searchValue.Records.Single();
        QueryAssert.Equal(QuerySamples.RecordId, searchHit.Record.RecordId);
        QueryAssert.Equal(2, searchHit.QueryTermCount);
        QueryAssert.True(searchHit.MatchedTerms is not null);
        QueryAssert.True(searchHit.MatchedTerms!.SequenceEqual(["bound", "query"]));
        QueryAssert.Equal(QuerySamples.RecordId, readValue.RecordId);

        // Accepted projections must not retain provider-owned mutable collection state.
        source.Taxonomies.Clear();
        source.Freshness.Clear();
        source.CatalogRecords.Clear();
        source.SearchRecords.Clear();
        source.Keywords.Clear();
        source.Applicability.Clear();
        source.Evidence.Clear();
        source.SourceReferences.Clear();
        source.Transformations.Clear();
        QueryAssert.Equal(1, discoveryValue.Taxonomies.Count);
        QueryAssert.Equal(2, statusValue.Freshness.Count);
        QueryAssert.Equal(1, catalogValue.Records.Count);
        QueryAssert.Equal(1, searchValue.Records.Count);
        QueryAssert.Equal(1, searchValue.Records.Single().Record.Keywords.Count);
        QueryAssert.Equal(1, readValue.Evidence.Count);
        QueryAssert.Equal(1, readValue.Provenance.SourceReferences.Count);
    }

    public static async Task DenialBeforeSourceAsync()
    {
        var source = new FakeCortexQuerySource();
        var service = QuerySamples.Service(source);

        var emptyTenant = await service.DiscoverAsync(
            QuerySamples.Authority(tenantId: string.Empty),
            CancellationToken.None);
        var emptyService = await service.GetStatusAsync(
            QuerySamples.Authority(serviceId: string.Empty),
            CancellationToken.None);
        var wrongNamespace = await service.ListCatalogAsync(
            new CortexCatalogQuery(QuerySamples.DefaultNamespace, 10),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(
                    namespaceName: QuerySamples.OtherNamespace,
                    capabilities: [CortexQueryCapability.Catalog])
            ]),
            CancellationToken.None);
        var missingCapability = await service.SearchAsync(
            QuerySamples.SearchQuery(),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(capabilities: [CortexQueryCapability.Read])
            ]),
            CancellationToken.None);
        var invalidMode = await service.SearchAsync(
            QuerySamples.SearchQuery() with { Mode = (CortexSearchMode)99 },
            QuerySamples.Authority(),
            CancellationToken.None);
        var staleDenied = await service.SearchAsync(
            QuerySamples.SearchQuery(includeStale: true),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(capabilities: [CortexQueryCapability.Search])
            ]),
            CancellationToken.None);
        var blockedDenied = await service.ReadAsync(
            QuerySamples.ReadQuery(includeBlockedOrUnverified: true),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(capabilities: [CortexQueryCapability.Read])
            ]),
            CancellationToken.None);
        var cartesianDenied = await service.SearchAsync(
            QuerySamples.SearchQuery(includeStale: true),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(
                    capabilities: [CortexQueryCapability.Read],
                    allowStale: true),
                QuerySamples.Grant(
                    namespaceName: QuerySamples.OtherNamespace,
                    capabilities: [CortexQueryCapability.Search],
                    allowStale: true)
            ]),
            CancellationToken.None);

        AssertRejected(emptyTenant, "cortex_query_authority_invalid");
        AssertRejected(emptyService, "cortex_query_authority_invalid");
        AssertRejected(wrongNamespace, "cortex_query_access_denied");
        AssertRejected(missingCapability, "cortex_query_access_denied");
        AssertRejected(invalidMode, "cortex_query_search_request_invalid");
        AssertRejected(staleDenied, "cortex_query_access_denied");
        AssertRejected(blockedDenied, "cortex_query_access_denied");
        AssertRejected(cartesianDenied, "cortex_query_access_denied");
        QueryAssert.Equal(0, source.TotalCalls);
    }

    public static async Task SourceIntegrityAsync()
    {
        var inconsistentSnapshotSource = new FakeCortexQuerySource();
        inconsistentSnapshotSource.Snapshot = inconsistentSnapshotSource.Snapshot with
        {
            DefaultReadableRecordCount = 0
        };
        var inconsistentSnapshot = await QuerySamples.Service(inconsistentSnapshotSource).GetStatusAsync(
            QuerySamples.Authority(),
            CancellationToken.None);
        AssertRejected(inconsistentSnapshot, "cortex_query_source_snapshot_invalid");

        var crossScopeSource = SourceWithSearchRecord(
            QuerySamples.Record(namespaceName: QuerySamples.OtherNamespace));
        var crossScope = await QuerySamples.Service(crossScopeSource).SearchAsync(
            QuerySamples.SearchQuery(),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(capabilities: [CortexQueryCapability.Search])
            ]),
            CancellationToken.None);
        AssertRejected(crossScope, "cortex_query_source_record_invalid");

        var partialTermSource = SourceWithSearchRecord(QuerySamples.Record());
        var exactPartial = await QuerySamples.Service(partialTermSource).SearchAsync(
            QuerySamples.SearchQuery() with
            {
                Text = "bounded nebula",
                Mode = CortexSearchMode.Exact
            },
            QuerySamples.Authority(),
            CancellationToken.None);
        AssertRejected(exactPartial, "cortex_query_source_search_invalid");

        var broadPartial = await QuerySamples.Service(partialTermSource).SearchAsync(
            QuerySamples.SearchQuery() with
            {
                Text = "bounded nebula",
                Mode = CortexSearchMode.Broad
            },
            QuerySamples.Authority(),
            CancellationToken.None);
        QueryAssert.True(broadPartial.Accepted);
        QueryAssert.Equal(2, broadPartial.Value!.Records.Single().QueryTermCount);
        QueryAssert.True(broadPartial.Value.Records.Single().MatchedTerms!.SequenceEqual(["bound"]));

        var staleOnlySource = SourceWithSearchRecord(QuerySamples.Record(freshnessState: "blocked"));
        var staleOnly = await QuerySamples.Service(staleOnlySource).SearchAsync(
            QuerySamples.SearchQuery(includeStale: true),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(
                    capabilities: [CortexQueryCapability.Search],
                    allowStale: true)
            ]),
            CancellationToken.None);
        AssertRejected(staleOnly, "cortex_query_source_record_invalid");
        AssertScope(
            staleOnlySource.SearchScopes.Single(),
            CortexQueryCapability.Search,
            "cortex.query.search",
            includeStale: true,
            includeBlockedOrUnverified: false);

        var blockedOnlySource = SourceWithSearchRecord(QuerySamples.Record(freshnessState: "stale"));
        var blockedOnly = await QuerySamples.Service(blockedOnlySource).SearchAsync(
            QuerySamples.SearchQuery(includeBlockedOrUnverified: true),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(
                    capabilities: [CortexQueryCapability.Search],
                    allowBlockedOrUnverified: true)
            ]),
            CancellationToken.None);
        AssertRejected(blockedOnly, "cortex_query_source_record_invalid");
        AssertScope(
            blockedOnlySource.SearchScopes.Single(),
            CortexQueryCapability.Search,
            "cortex.query.search",
            includeStale: false,
            includeBlockedOrUnverified: true);

        var unauthorizedProvenanceSource = ProvenanceSource();
        var unauthorizedProvenance = await QuerySamples.Service(unauthorizedProvenanceSource).ReadAsync(
            QuerySamples.ReadQuery(),
            QuerySamples.Authority(grants:
            [
                QuerySamples.Grant(capabilities: [CortexQueryCapability.Read])
            ]),
            CancellationToken.None);
        AssertRejected(unauthorizedProvenance, "cortex_query_provenance_target_denied");
        QueryAssert.Equal(1, unauthorizedProvenanceSource.SnapshotScopes.Count);
        QueryAssert.Equal(1, unauthorizedProvenanceSource.ReadScopes.Count);

        var provenanceAuthority = QuerySamples.Authority(grants:
        [
            QuerySamples.Grant(capabilities: [CortexQueryCapability.Read]),
            QuerySamples.Grant(
                namespaceName: QuerySamples.OtherNamespace,
                capabilities: [CortexQueryCapability.Read])
        ]);
        var authorizedProvenanceSource = ProvenanceSource();
        var authorizedProvenance = await QuerySamples.Service(authorizedProvenanceSource).ReadAsync(
            QuerySamples.ReadQuery(),
            provenanceAuthority,
            CancellationToken.None);
        QueryAssert.True(authorizedProvenance.Accepted);
        QueryAssert.Equal(QuerySamples.RecordId, authorizedProvenance.Value!.RecordId);
        QueryAssert.Equal(2, authorizedProvenanceSource.SnapshotScopes.Count);
        QueryAssert.Equal(2, authorizedProvenanceSource.ReadScopes.Count);
        QueryAssert.Equal(
            new CortexRecordReadQuery(
                QuerySamples.OtherNamespace,
                QuerySamples.LinkedRecordId,
                QuerySamples.LinkedRevisionId,
                IncludeStale: false,
                IncludeBlockedOrUnverified: false),
            authorizedProvenanceSource.ReadQueries[1]);
        AssertScope(
            authorizedProvenanceSource.ReadScopes[0],
            CortexQueryCapability.Read,
            "cortex.query.read",
            false,
            false);
        AssertScope(
            authorizedProvenanceSource.ReadScopes[1],
            CortexQueryCapability.Read,
            "cortex.query.provenance.read",
            false,
            false,
            QuerySamples.OtherNamespace);
        AssertScope(
            authorizedProvenanceSource.SnapshotScopes[0],
            CortexQueryCapability.Read,
            "cortex.query.read",
            false,
            false);
        AssertScope(
            authorizedProvenanceSource.SnapshotScopes[1],
            CortexQueryCapability.Read,
            "cortex.query.provenance.read",
            false,
            false,
            QuerySamples.OtherNamespace);

        var wrongHash = await QuerySamples.Service(ProvenanceSource(targetHash: QuerySamples.WrongContentHash))
            .ReadAsync(QuerySamples.ReadQuery(), provenanceAuthority, CancellationToken.None);
        AssertRejected(wrongHash, "cortex_query_provenance_target_denied");

        var wrongIdentity = await QuerySamples.Service(ProvenanceSource(targetRecordId: "different-record"))
            .ReadAsync(QuerySamples.ReadQuery(), provenanceAuthority, CancellationToken.None);
        AssertRejected(wrongIdentity, "cortex_query_provenance_target_denied");

        await AssertProvenanceBudgetAndCacheAsync();
    }

    public static async Task BoundedEnumerationAsync()
    {
        QueryAssert.Throws<ArgumentException>(() =>
            QuerySamples.Grant(capabilities: QuerySamples.OverproducingCapabilities()));
        QueryAssert.Throws<ArgumentException>(() =>
            QuerySamples.Authority(grants: QuerySamples.OverproducingGrants()));

        var source = new FakeCortexQuerySource();
        var hit = new CortexKnowledgeSearchHit(
            QuerySamples.Record(),
            1.0,
            CortexQueryPolicy.SafetyNotice);
        source.SearchResult = new CortexQuerySourceSearchResult(
            true,
            new OverproducingReadOnlyList<CortexKnowledgeSearchHit>(1, hit, hit),
            null);
        var result = await QuerySamples.Service(source).SearchAsync(
            QuerySamples.SearchQuery(limit: 1),
            QuerySamples.Authority(),
            CancellationToken.None);

        AssertRejected(result, "cortex_query_source_search_invalid");
        QueryAssert.Equal(1, source.SnapshotScopes.Count);
        QueryAssert.Equal(1, source.SearchScopes.Count);
    }

    public static async Task CancellationAsync()
    {
        var preCanceledSource = new FakeCortexQuerySource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await QueryAssert.ThrowsAsync<OperationCanceledException>(() =>
            QuerySamples.Service(preCanceledSource).SearchAsync(
                QuerySamples.SearchQuery(),
                QuerySamples.Authority(),
                cancellation.Token).AsTask());
        QueryAssert.Equal(0, preCanceledSource.TotalCalls);

        var sourceCanceledSource = new FakeCortexQuerySource
        {
            SearchException = new OperationCanceledException("provider cancellation")
        };
        await QueryAssert.ThrowsAsync<OperationCanceledException>(() =>
            QuerySamples.Service(sourceCanceledSource).SearchAsync(
                QuerySamples.SearchQuery(),
                QuerySamples.Authority(),
                CancellationToken.None).AsTask());
        QueryAssert.Equal(1, sourceCanceledSource.SnapshotScopes.Count);
        QueryAssert.Equal(1, sourceCanceledSource.SearchScopes.Count);
    }

    public static async Task SourceDiagnosticsAsync()
    {
        var rejectedSource = new FakeCortexQuerySource
        {
            SearchResult = new CortexQuerySourceSearchResult(
                false,
                [],
                new PegasusDiagnostic(
                    "provider_secret_code",
                    "provider secret message must not cross the boundary",
                    DiagnosticSeverity.Error,
                    "provider.search",
                    retryable: true))
        };
        var normalized = await QuerySamples.Service(rejectedSource).SearchAsync(
            QuerySamples.SearchQuery(),
            QuerySamples.Authority(),
            CancellationToken.None);

        AssertRejected(normalized, "cortex_query_source_search_rejected");
        var normalizedDiagnostic = normalized.Diagnostics.Single();
        QueryAssert.Equal("The bounded knowledge query was rejected.", normalizedDiagnostic.Message);
        QueryAssert.True(normalizedDiagnostic.Retryable);
        QueryAssert.Equal("source_rejected", normalizedDiagnostic.Cause);
        QueryAssert.False(normalizedDiagnostic.Message.Contains("provider secret", StringComparison.Ordinal));
        QueryAssert.False((normalizedDiagnostic.Cause ?? string.Empty).Contains("provider_secret", StringComparison.Ordinal));

        const string privateCause = "private provider exception detail";
        var failedSource = new FakeCortexQuerySource
        {
            SearchException = new InvalidOperationException(privateCause)
        };
        var failed = await QuerySamples.Service(failedSource).SearchAsync(
            QuerySamples.SearchQuery(),
            QuerySamples.Authority(),
            CancellationToken.None);

        AssertRejected(failed, "cortex_query_source_failure");
        var failureDiagnostic = failed.Diagnostics.Single();
        QueryAssert.Equal(
            "The query source failed while processing the authorized request.",
            failureDiagnostic.Message);
        QueryAssert.True(failureDiagnostic.Retryable);
        QueryAssert.Equal(typeof(InvalidOperationException).FullName, failureDiagnostic.Cause);
        QueryAssert.False(failureDiagnostic.Message.Contains(privateCause, StringComparison.Ordinal));
        QueryAssert.False((failureDiagnostic.Cause ?? string.Empty).Contains(privateCause, StringComparison.Ordinal));
        QueryAssert.False(failureDiagnostic.Context?.Values.Any(value =>
            value?.Contains(privateCause, StringComparison.Ordinal) == true) == true);
    }

    private static FakeCortexQuerySource SourceWithSearchRecord(CortexKnowledgeRecord record)
    {
        var source = new FakeCortexQuerySource();
        source.SearchRecords.Clear();
        source.SearchRecords.Add(new CortexKnowledgeSearchHit(
            record,
            1.0,
            CortexQueryPolicy.SafetyNotice));
        return source;
    }

    private static FakeCortexQuerySource ProvenanceSource(
        string? targetRecordId = null,
        string? targetHash = null)
    {
        var source = new FakeCortexQuerySource();
        var link = new CortexKnowledgeSourceRevision(
            QuerySamples.OtherNamespace,
            QuerySamples.LinkedRecordId,
            QuerySamples.LinkedRevisionId,
            QuerySamples.LinkedContentHash);
        var root = QuerySamples.Record(sourceRevisions: [link]);
        var target = QuerySamples.Record(
            namespaceName: QuerySamples.OtherNamespace,
            recordId: targetRecordId ?? QuerySamples.LinkedRecordId,
            revisionId: QuerySamples.LinkedRevisionId,
            contentHash: targetHash ?? QuerySamples.LinkedContentHash);
        source.PrimaryRecord = root;
        source.SnapshotResolver = scope => QuerySamples.Snapshot(
            scope.Namespace,
            source.Taxonomies,
            source.Freshness);
        source.ReadResolver = query =>
        {
            if (query.Namespace == QuerySamples.DefaultNamespace &&
                query.RecordId == QuerySamples.RecordId &&
                query.RevisionId == QuerySamples.RevisionId)
            {
                return root;
            }

            return query.Namespace == QuerySamples.OtherNamespace &&
                query.RecordId == QuerySamples.LinkedRecordId &&
                query.RevisionId == QuerySamples.LinkedRevisionId
                    ? target
                    : null;
        };
        return source;
    }

    private static async Task AssertProvenanceBudgetAndCacheAsync()
    {
        var link = new CortexKnowledgeSourceRevision(
            QuerySamples.OtherNamespace,
            QuerySamples.LinkedRecordId,
            QuerySamples.LinkedRevisionId,
            QuerySamples.LinkedContentHash);
        var repeatedLinks = Enumerable.Repeat(link, CortexQueryLimits.MaximumProvenanceReferences).ToArray();
        var source = new FakeCortexQuerySource();
        source.SnapshotResolver = scope => QuerySamples.Snapshot(
            scope.Namespace,
            source.Taxonomies,
            source.Freshness);
        source.ReadResolver = query =>
            query.Namespace == QuerySamples.OtherNamespace &&
            query.RecordId == QuerySamples.LinkedRecordId &&
            query.RevisionId == QuerySamples.LinkedRevisionId
                ? QuerySamples.Record(
                    namespaceName: QuerySamples.OtherNamespace,
                    recordId: QuerySamples.LinkedRecordId,
                    revisionId: QuerySamples.LinkedRevisionId,
                    contentHash: QuerySamples.LinkedContentHash)
                : null;
        source.SearchRecords.Clear();
        source.SearchRecords.Add(new CortexKnowledgeSearchHit(
            QuerySamples.Record(
                recordId: "search-root-1",
                revisionId: "search-revision-1",
                contentHash: new string('1', 64),
                sourceRevisions: repeatedLinks),
            1.0,
            CortexQueryPolicy.SafetyNotice));
        source.SearchRecords.Add(new CortexKnowledgeSearchHit(
            QuerySamples.Record(
                recordId: "search-root-2",
                revisionId: "search-revision-2",
                contentHash: new string('2', 64),
                sourceRevisions: [link]),
            0.5,
            CortexQueryPolicy.SafetyNotice));

        var authority = QuerySamples.Authority(grants:
        [
            QuerySamples.Grant(capabilities: [CortexQueryCapability.Search]),
            QuerySamples.Grant(
                namespaceName: QuerySamples.OtherNamespace,
                capabilities: [CortexQueryCapability.Read])
        ]);
        var service = QuerySamples.Service(source);
        var overBudget = await service.SearchAsync(
            QuerySamples.SearchQuery(limit: 2),
            authority,
            CancellationToken.None);

        AssertRejected(overBudget, "cortex_query_provenance_budget_exceeded");
        QueryAssert.Equal(1, source.SnapshotScopes.Count(scope => scope.Namespace == QuerySamples.OtherNamespace));
        QueryAssert.Equal(1, source.ReadScopes.Count(scope => scope.Namespace == QuerySamples.OtherNamespace));
        AssertScope(
            source.ReadScopes.Single(),
            CortexQueryCapability.Read,
            "cortex.query.provenance.read",
            false,
            false,
            QuerySamples.OtherNamespace);
        AssertScope(
            source.SnapshotScopes.Single(scope => scope.Namespace == QuerySamples.OtherNamespace),
            CortexQueryCapability.Read,
            "cortex.query.provenance.read",
            false,
            false,
            QuerySamples.OtherNamespace);

        // Validation caches are per call: 64 repeated links reuse I/O within the next call,
        // but do not retain the prior call's target snapshot or exact-target validation.
        source.SearchRecords.Clear();
        source.SearchRecords.Add(new CortexKnowledgeSearchHit(
            QuerySamples.Record(
                recordId: "search-root-3",
                revisionId: "search-revision-3",
                contentHash: new string('3', 64),
                sourceRevisions: repeatedLinks),
            1.0,
            CortexQueryPolicy.SafetyNotice));
        var withinBudget = await service.SearchAsync(
            QuerySamples.SearchQuery(limit: 1),
            authority,
            CancellationToken.None);

        QueryAssert.True(withinBudget.Accepted);
        QueryAssert.Equal(2, source.SnapshotScopes.Count(scope => scope.Namespace == QuerySamples.OtherNamespace));
        QueryAssert.Equal(2, source.ReadScopes.Count(scope => scope.Namespace == QuerySamples.OtherNamespace));
    }

    private static void AssertScope(
        CortexQuerySourceScope scope,
        CortexQueryCapability capability,
        string operation,
        bool includeStale,
        bool includeBlockedOrUnverified,
        string namespaceName = QuerySamples.DefaultNamespace)
    {
        QueryAssert.Equal("tenant-1", scope.TenantId);
        QueryAssert.Equal("nexus", scope.AuthorityId);
        QueryAssert.Equal("subject-1", scope.SubjectId);
        QueryAssert.Equal("service-1", scope.ServiceId);
        QueryAssert.Equal(7L, scope.PolicyRevision);
        QueryAssert.Equal(namespaceName, scope.Namespace);
        QueryAssert.Equal(capability, scope.Capability);
        QueryAssert.Equal(operation, scope.Operation);
        QueryAssert.Equal("internal", scope.MaximumSensitivity);
        QueryAssert.Equal(includeStale, scope.IncludeStale);
        QueryAssert.Equal(includeBlockedOrUnverified, scope.IncludeBlockedOrUnverified);
    }

    private static void AssertRejected<T>(CortexQueryResult<T> result, string diagnosticCode)
    {
        QueryAssert.False(result.Accepted);
        QueryAssert.True(result.Value is null);
        QueryAssert.Equal(1, result.Diagnostics.Count);
        QueryAssert.Equal(diagnosticCode, result.Diagnostics.Single().Code);
    }
}

internal static class QuerySamples
{
    public const string DefaultNamespace = "shared:verified";
    public const string OtherNamespace = "shared:other";
    public const string RecordId = "record-1";
    public const string RevisionId = "revision-1";
    public const string LinkedRecordId = "linked-record";
    public const string LinkedRevisionId = "linked-revision";
    public static readonly string ContentHash = new('a', 64);
    public static readonly string LinkedContentHash = new('b', 64);
    public static readonly string WrongContentHash = new('c', 64);
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-14T14:00:00Z");

    public static CortexQueryService Service(ICortexQuerySource source) =>
        new(source, new CortexQueryServiceOptions(
            DefaultNamespace,
            "isolated-read-only",
            "verified-at-startup"));

    public static CortexQueryNamespaceGrant Grant(
        string? namespaceName = null,
        IEnumerable<CortexQueryCapability>? capabilities = null,
        string maximumSensitivity = "internal",
        bool allowStale = false,
        bool allowBlockedOrUnverified = false) =>
        new(
            namespaceName ?? DefaultNamespace,
            maximumSensitivity,
            capabilities ?? Enum.GetValues<CortexQueryCapability>(),
            allowStale,
            allowBlockedOrUnverified);

    public static CortexQueryAuthority Authority(
        string tenantId = "tenant-1",
        string serviceId = "service-1",
        IEnumerable<CortexQueryNamespaceGrant>? grants = null) =>
        new(
            tenantId,
            "nexus",
            "subject-1",
            serviceId,
            7,
            grants ?? [Grant()]);

    public static CortexSearchQuery SearchQuery(
        int limit = 5,
        bool includeStale = false,
        bool includeBlockedOrUnverified = false) =>
        new(
            DefaultNamespace,
            "bounded query",
            "testing",
            "pegasus-cortex",
            "query-service",
            limit,
            includeStale,
            includeBlockedOrUnverified);

    public static CortexRecordReadQuery ReadQuery(
        bool includeStale = false,
        bool includeBlockedOrUnverified = false) =>
        new(
            DefaultNamespace,
            RecordId,
            RevisionId,
            includeStale,
            includeBlockedOrUnverified);

    public static CortexKnowledgeSourceSnapshot Snapshot(
        string namespaceName,
        IReadOnlyList<string> taxonomies,
        IReadOnlyList<CortexFreshnessCount> freshness) =>
        new(
            "1.2026.07.14.07.05.A",
            "1.20260714.705-a",
            "default-library",
            new string('d', 64),
            namespaceName,
            RecordCount: 2,
            DefaultReadableRecordCount: 1,
            taxonomies,
            freshness,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice);

    public static CortexKnowledgeRecord Record(
        string? namespaceName = null,
        string? recordId = null,
        string? revisionId = null,
        string? contentHash = null,
        string freshnessState = "current",
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<CortexKnowledgeApplicability>? applicability = null,
        IReadOnlyList<CortexKnowledgeEvidence>? evidence = null,
        IReadOnlyList<string>? sourceReferences = null,
        IReadOnlyList<CortexKnowledgeSourceRevision>? sourceRevisions = null,
        IReadOnlyList<string>? transformations = null)
    {
        var evidenceValues = evidence ?? [Evidence()];
        var sourceReferenceValues = sourceReferences ?? evidenceValues
            .Select(item => item.Reference)
            .ToArray();
        return new CortexKnowledgeRecord(
            recordId ?? RecordId,
            revisionId ?? RevisionId,
            namespaceName ?? DefaultNamespace,
            "testing",
            "query-service-contract",
            "Query service contract",
            "A bounded knowledge record used to verify the Core query contract.",
            "This non-executable record is untrusted reference data.",
            keywords ?? ["bounded"],
            applicability ??
            [
                new CortexKnowledgeApplicability(
                    "pegasus-cortex",
                    "query-service",
                    null,
                    null,
                    "verified")
            ],
            evidenceValues,
            new CortexKnowledgeProvenance(
                "pegasus.cortex.knowledge/1.0",
                sourceReferenceValues,
                sourceRevisions ?? [],
                transformations ?? ["verified"]),
            new CortexKnowledgeConfidence(0.95, "observable contract verification"),
            new CortexKnowledgeFreshness(
                freshnessState,
                ObservedAt,
                ObservedAt.AddDays(1),
                "bounded test fixture"),
            "internal",
            "active",
            TrainingEligibility: false,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            contentHash ?? ContentHash);
    }

    public static CortexKnowledgeRecordSummary Summary(CortexKnowledgeRecord record) =>
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
            record.Executable);

    public static CortexKnowledgeEvidence Evidence() =>
        new(
            "artifact://verification/1",
            "application/json",
            new string('e', 64),
            "verified",
            ObservedAt,
            "1.0");

    public static IEnumerable<CortexQueryCapability> OverproducingCapabilities()
    {
        foreach (var capability in Enum.GetValues<CortexQueryCapability>())
            yield return capability;
        yield return (CortexQueryCapability)int.MaxValue;
    }

    public static IEnumerable<CortexQueryNamespaceGrant> OverproducingGrants()
    {
        for (var index = 0; index <= CortexQueryLimits.MaximumAuthorityGrants; index++)
        {
            yield return Grant(
                namespaceName: $"shared:grant-{index}",
                capabilities: [CortexQueryCapability.Read]);
        }
    }
}

internal sealed class FakeCortexQuerySource : ICortexQuerySource
{
    public FakeCortexQuerySource()
    {
        Taxonomies.Add("testing");
        Freshness.Add(new CortexFreshnessCount("current", 1));
        Freshness.Add(new CortexFreshnessCount("unverified", 1));
        Keywords.Add("bounded");
        Applicability.Add(new CortexKnowledgeApplicability(
            "pegasus-cortex",
            "query-service",
            null,
            null,
            "verified"));
        Evidence.Add(QuerySamples.Evidence());
        SourceReferences.Add(Evidence.Single().Reference);
        Transformations.Add("verified");
        PrimaryRecord = QuerySamples.Record(
            keywords: Keywords,
            applicability: Applicability,
            evidence: Evidence,
            sourceReferences: SourceReferences,
            sourceRevisions: SourceRevisions,
            transformations: Transformations);
        Snapshot = QuerySamples.Snapshot(QuerySamples.DefaultNamespace, Taxonomies, Freshness);
        CatalogRecords.Add(QuerySamples.Summary(PrimaryRecord));
        SearchRecords.Add(new CortexKnowledgeSearchHit(
            PrimaryRecord,
            1.0,
            CortexQueryPolicy.SafetyNotice));
        SearchResult = new CortexQuerySourceSearchResult(true, SearchRecords, null);
    }

    public List<string> Taxonomies { get; } = [];
    public List<CortexFreshnessCount> Freshness { get; } = [];
    public List<string> Keywords { get; } = [];
    public List<CortexKnowledgeApplicability> Applicability { get; } = [];
    public List<CortexKnowledgeEvidence> Evidence { get; } = [];
    public List<string> SourceReferences { get; } = [];
    public List<CortexKnowledgeSourceRevision> SourceRevisions { get; } = [];
    public List<string> Transformations { get; } = [];
    public List<CortexKnowledgeRecordSummary> CatalogRecords { get; } = [];
    public List<CortexKnowledgeSearchHit> SearchRecords { get; } = [];
    public List<CortexQuerySourceScope> SnapshotScopes { get; } = [];
    public List<CortexQuerySourceScope> CatalogScopes { get; } = [];
    public List<CortexQuerySourceScope> SearchScopes { get; } = [];
    public List<CortexQuerySourceScope> ReadScopes { get; } = [];
    public List<CortexCatalogQuery> CatalogQueries { get; } = [];
    public List<CortexSearchQuery> SearchQueries { get; } = [];
    public List<CortexRecordReadQuery> ReadQueries { get; } = [];
    public CortexKnowledgeSourceSnapshot Snapshot { get; set; }
    public CortexKnowledgeRecord PrimaryRecord { get; set; }
    public CortexQuerySourceSearchResult SearchResult { get; set; }
    public Func<CortexQuerySourceScope, CortexKnowledgeSourceSnapshot>? SnapshotResolver { get; set; }
    public Func<CortexRecordReadQuery, CortexKnowledgeRecord?>? ReadResolver { get; set; }
    public Exception? SnapshotException { get; set; }
    public Exception? CatalogException { get; set; }
    public Exception? SearchException { get; set; }
    public Exception? ReadException { get; set; }
    public int TotalCalls =>
        SnapshotScopes.Count + CatalogScopes.Count + SearchScopes.Count + ReadScopes.Count;

    public ValueTask<CortexKnowledgeSourceSnapshot> GetSnapshotAsync(
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        SnapshotScopes.Add(scope with { });
        cancellationToken.ThrowIfCancellationRequested();
        if (SnapshotException is not null)
            throw SnapshotException;
        var snapshot = SnapshotResolver?.Invoke(scope) ?? Snapshot with { DefaultNamespace = scope.Namespace };
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<CortexQuerySourceSearchResult> SearchAsync(
        CortexSearchQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        SearchQueries.Add(query with { });
        SearchScopes.Add(scope with { });
        cancellationToken.ThrowIfCancellationRequested();
        if (SearchException is not null)
            throw SearchException;
        return ValueTask.FromResult(SearchResult);
    }

    public ValueTask<IReadOnlyList<CortexKnowledgeRecordSummary>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        CatalogQueries.Add(query with { });
        CatalogScopes.Add(scope with { });
        cancellationToken.ThrowIfCancellationRequested();
        if (CatalogException is not null)
            throw CatalogException;
        return ValueTask.FromResult<IReadOnlyList<CortexKnowledgeRecordSummary>>(CatalogRecords);
    }

    public ValueTask<CortexKnowledgeRecord?> ReadAsync(
        CortexRecordReadQuery query,
        CortexQuerySourceScope scope,
        CancellationToken cancellationToken)
    {
        ReadQueries.Add(query with { });
        ReadScopes.Add(scope with { });
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadException is not null)
            throw ReadException;

        CortexKnowledgeRecord? record;
        if (ReadResolver is not null)
        {
            record = ReadResolver(query);
        }
        else
        {
            record = query.Namespace == PrimaryRecord.Namespace &&
                query.RecordId == PrimaryRecord.RecordId &&
                query.RevisionId == PrimaryRecord.RevisionId
                    ? PrimaryRecord
                    : null;
        }
        return ValueTask.FromResult(record);
    }
}

internal sealed class OverproducingReadOnlyList<T>(int reportedCount, params T[] values) : IReadOnlyList<T>
{
    public int Count { get; } = reportedCount;
    public T this[int index] => values[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
}

internal static class QueryAssert
{
    public static void True(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, received {exception.GetType().Name}.",
                exception);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, received {exception.GetType().Name}.",
                exception);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
