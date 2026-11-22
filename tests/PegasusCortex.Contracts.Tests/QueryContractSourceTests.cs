using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Query;
using Pegasus.Cortex.Contracts.Serialization;

internal static class QueryContractSourceTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("query wire roots round trip through strict canonical JSON", RoundTripWireRoots),
        ("malformed query requests and responses fail at the JSON boundary", MalformedWireContractsFailClosed),
        ("query authority is server derived and default deny", AuthorityIsServerDerived),
        ("query collections are bounded immutable snapshots", CollectionsAreBoundedSnapshots),
        ("query cursor binds caller scope query generation and expiry", CursorBindingIsFailClosed),
        ("query schema is strict versioned and excludes caller authority", SchemaIsStrict)
    ];

    private static void RoundTripWireRoots()
    {
        var sample = Samples.Create();
        var provenance = RoundTrip(sample.Provenance);
        Require(provenance.SourceRevisionsComplete && provenance.TransformationsComplete,
            "Complete provenance did not round trip.");
        Assert.Throws<ArgumentException>(() => new CortexProvenanceProjectionV1(
            sample.Provenance.ProducerContract,
            sample.Provenance.ProducerAuthorityId,
            sample.Provenance.ProducerSubjectId,
            sample.Provenance.ProducerServiceId,
            sample.Provenance.AgentId,
            sample.Provenance.InstanceId,
            sample.Provenance.MachineId,
            sample.Provenance.OperationId,
            sample.Provenance.TaskId,
            sample.Provenance.CorrelationId,
            sample.Provenance.SourceRevisions,
            false,
            sample.Provenance.Transformations,
            true));
        Assert.Throws<ArgumentException>(() => new CortexProvenanceProjectionV1(
            sample.Provenance.ProducerContract,
            sample.Provenance.ProducerAuthorityId,
            sample.Provenance.ProducerSubjectId,
            sample.Provenance.ProducerServiceId,
            sample.Provenance.AgentId,
            sample.Provenance.InstanceId,
            sample.Provenance.MachineId,
            sample.Provenance.OperationId,
            sample.Provenance.TaskId,
            sample.Provenance.CorrelationId,
            sample.Provenance.SourceRevisions,
            true,
            sample.Provenance.Transformations,
            false));
        var search = RoundTrip(sample.SearchRequest);
        Require(search.Namespace == sample.SearchRequest.Namespace, "Search namespace did not round trip.");
        Require(search.FreshnessStates.SequenceEqual(sample.SearchRequest.FreshnessStates), "Search freshness changed.");
        var broadSearch = RoundTrip(new CortexSearchRequestV1_1(
            1,
            CortexQueryContractIds.SearchRequestV1_1,
            "request-search-broad",
            "project:alpha",
            "bounded query",
            null,
            null,
            null,
            "public",
            new[] { "current" },
            10,
            null,
            CortexSearchModes.Broad));
        Require(broadSearch.Mode == CortexSearchModes.Broad,
            "The explicit broad-search mode did not round trip.");

        var capabilities = RoundTrip(sample.Capabilities);
        Require(capabilities.SupportedResponseContracts.Contains(CortexQueryContractIds.Response), "Response negotiation was lost.");
        var status = RoundTrip(sample.Status);
        Require(status.Dependencies.Count == 4 && status.Readiness == "ready", "Status dependencies changed.");

        var summary = RoundTrip(sample.Summary);
        Require(summary.ContentTreatment == CortexQueryContractIds.ContentTreatment && !summary.Executable, "Summary safety labels changed.");
        Require(summary.Applicability.Single().ProductId == "pegasus-cortex", "Summary applicability changed.");
        var record = RoundTrip(sample.Record);
        Require(record.RecordId == sample.Record.RecordId && record.EvidenceComplete, "Exact record changed.");
        Require(record.Applicability.Single().ComponentId == "query-contracts", "Exact-record applicability changed.");
        Require(RoundTrip(new CortexRecordReadResultV1(sample.Record)).Record.RecordId == sample.Record.RecordId,
            "Exact-record result changed.");

        var namespacePage = RoundTrip(sample.NamespacePage);
        var searchPage = RoundTrip(sample.SearchPage);
        var lineagePage = RoundTrip(sample.LineagePage);
        Require(namespacePage.Items.Count == 1, "Namespace page changed.");
        Require(searchPage.Items.Count == 1, "Search page changed.");
        Require(lineagePage.Items.Single().Namespace == sample.Record.Namespace, "Lineage scope changed.");

        var response = RoundTrip(sample.SearchResponse);
        Require(response.Accepted && response.Payload?.Items.Count == 1, "Response envelope changed.");
        var diagnostic = new CortexDiagnosticV1(
            "cortex_query_invalid_request",
            CortexQueryDiagnosticCatalog.MessageFor("cortex_query_invalid_request"),
            "error",
            "cortex.query.search",
            false,
            new CortexDiagnosticContextV1("request-search", "project:alpha", null, null, "correlation-1", null, null, 7));
        var rejected = RoundTrip(new CortexQueryResponseV1<CortexSearchPageV1>(
            1, CortexQueryContractIds.Response, CortexQueryOperations.Search, "request-search", false, null, new[] { diagnostic }));
        Require(!rejected.Accepted && rejected.Diagnostics.Single().Code == diagnostic.Code, "Rejected response diagnostic changed.");
        var cursor = RoundTrip(sample.CursorPayload);
        Require(cursor.ScopeFingerprint == sample.CursorPayload.ScopeFingerprint, "Cursor payload changed.");

        RoundTrip(new CortexDiscoveryRequestV1(1, CortexQueryContractIds.DiscoveryRequest, "request-discover"));
        RoundTrip(new CortexHealthRequestV1(1, CortexQueryContractIds.HealthRequest, "request-health"));
        RoundTrip(new CortexNamespaceListRequestV1(1, CortexQueryContractIds.NamespaceListRequest, "request-namespaces", 25, null));
        RoundTrip(new CortexRecordReadRequestV1(1, CortexQueryContractIds.RecordReadRequest, "request-record", sample.Record.Namespace, sample.Record.RecordId, sample.Record.RevisionId));
        RoundTrip(new CortexLineageRequestV1(1, CortexQueryContractIds.LineageRequest, "request-lineage", sample.Record.Namespace, sample.Record.RecordId, sample.Record.RevisionId, "ancestors", 25, null));
    }

    private static void MalformedWireContractsFailClosed()
    {
        RequireJsonRejected<CortexDiscoveryRequestV1>(
            """{"schemaVersion":2,"contractId":"pegasus.cortex.query-discovery-request/1.0","requestId":"request-1"}""",
            "Discovery accepted an unsupported schema version.");
        RequireJsonRejected<CortexHealthRequestV1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.wrong/1.0","requestId":"request-1"}""",
            "Health accepted the wrong contract identifier.");
        RequireJsonRejected<CortexNamespaceListRequestV1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.namespace-list-request/1.0","requestId":"request-1","pageSize":0,"cursor":null}""",
            "Namespace list accepted an invalid page size.");
        RequireJsonRejected<CortexSearchRequestV1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.search-request/1.0","requestId":"request-1","namespace":"project:alpha","text":"query","taxonomy":null,"productId":null,"componentId":null,"maximumSensitivity":"internal","freshnessStates":["current","current"],"pageSize":25,"cursor":null}""",
            "Search accepted duplicate freshness filters.");
        RequireJsonRejected<CortexSearchRequestV1_1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.search-request/1.1","requestId":"request-1","namespace":"project:alpha","text":"query","taxonomy":null,"productId":null,"componentId":null,"maximumSensitivity":"internal","freshnessStates":["current"],"pageSize":25,"cursor":null,"mode":"semantic"}""",
            "Versioned search accepted an unsupported mode.");
        RequireJsonRejected<CortexSearchRequestV1_1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.search-request/1.1","requestId":"request-1","namespace":"project:alpha","text":"query","taxonomy":null,"productId":null,"componentId":null,"maximumSensitivity":"internal","freshnessStates":["current"],"pageSize":25,"cursor":null}""",
            "Versioned search accepted a missing mode.");
        RequireJsonRejected<CortexRecordReadRequestV1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.record-read-request/1.0","requestId":"request-1","namespace":" project:alpha","recordId":"record-1","revisionId":"revision-1"}""",
            "Exact-record read accepted a non-canonical namespace.");
        RequireJsonRejected<CortexLineageRequestV1>(
            """{"schemaVersion":1,"contractId":"pegasus.cortex.lineage-request/1.0","requestId":"request-1","namespace":"project:alpha","recordId":"record-1","revisionId":"revision-1","direction":"sideways","pageSize":25,"cursor":null}""",
            "Lineage accepted an unsupported direction.");

        var sample = Samples.Create();
        var page = new CortexPageMetadataV1(25, 1, null, "index-1", sample.Now);
        Assert.Throws<ArgumentException>(() => new CortexNamespacePageV1(
            new[] { new CortexNamespaceProjectionV1("invalid namespace", 0) }, page));
        Assert.Throws<ArgumentException>(() => new CortexSearchPageV1(
            "not-a-sha256-fingerprint", new[] { sample.Summary }, page));
        Assert.Throws<ArgumentNullException>(() => new CortexRecordReadResultV1(null!));
        RequireJsonRejected<CortexRecordReadResultV1>(
            """{"record":null}""",
            "Exact-record result accepted a null record.");
    }

    private static void AuthorityIsServerDerived()
    {
        var mutableOperations = new HashSet<string>(StringComparer.Ordinal) { CortexQueryOperations.Search };
        var grant = new CortexQueryNamespaceGrant(
            "project:alpha",
            mutableOperations,
            "internal",
            new[] { "current" },
            new[] { "active" });
        var grants = new List<CortexQueryNamespaceGrant> { grant };
        var authority = new CortexQueryAuthorityContext("tenant-1", "nexus", "subject-1", "pegasus-coder", 7, grants);
        grants.Clear();
        mutableOperations.Clear();

        var allowed = CortexQueryContractBoundary.AuthorizeNamespace(
            authority,
            "project:alpha",
            CortexQueryOperations.Search,
            "internal",
            new[] { "current" },
            CancellationToken.None);
        var denied = CortexQueryContractBoundary.AuthorizeNamespace(
            new CortexQueryAuthorityContext("tenant-1", "nexus", "subject-1", "pegasus-coder", 7, []),
            "project:alpha",
            CortexQueryOperations.Search,
            "internal",
            new[] { "current" },
            CancellationToken.None);

        Require(allowed.Accepted, "Server grant should authorize the exact scope.");
        Require(!denied.Accepted && denied.Diagnostics.Single().Code == "cortex_query_namespace_forbidden", "Missing server grant must deny.");

        var advertised = Samples.Create().Capabilities;
        Require(advertised.Operations.Any(item => item.Operation == CortexQueryOperations.Search && item.Supported), "Search support fixture is invalid.");
        Require(!CortexQueryContractBoundary.AuthorizeNamespace(
            new CortexQueryAuthorityContext("tenant-1", "nexus", "subject-1", "pegasus-coder", 7, []),
            "project:alpha",
            CortexQueryOperations.Search,
            "public",
            new[] { "current" },
            CancellationToken.None).Accepted,
            "Advertised support must not grant namespace access.");

        var longNamespace = "project:" + new string('a', 200);
        var longGrant = new CortexQueryNamespaceGrant(longNamespace, new[] { CortexQueryOperations.Search }, "public", new[] { "current" }, new[] { "active" });
        Require(CortexQueryContractBoundary.AuthorizeNamespace(
            new CortexQueryAuthorityContext("tenant-1", "nexus", "subject-1", "service-1", 1, new[] { longGrant }),
            longNamespace,
            CortexQueryOperations.Search,
            "public",
            new[] { "current" },
            CancellationToken.None).Accepted,
            "A valid namespace between 129 and 256 characters must remain supported.");
    }

    private static void CollectionsAreBoundedSnapshots()
    {
        var sample = Samples.Create();
        var freshness = new List<string> { "current" };
        var request = new CortexSearchRequestV1(
            1, CortexQueryContractIds.SearchRequest, "request-immutable", "project:alpha", "bounded query",
            null, null, null, "internal", freshness, 10, null);
        freshness[0] = "blocked";
        Require(request.FreshnessStates.Single() == "current", "Request freshness retained caller memory.");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.FreshnessStates)[0] = "stale");

        var summaries = new List<CortexRecordSummaryV1> { sample.Summary };
        var page = new CortexSearchPageV1(sample.HashA, summaries, new CortexPageMetadataV1(10, 1, null, "index-1", sample.Now));
        summaries.Clear();
        Require(page.Items.Count == 1, "Page retained caller-owned collection state.");

        var unbounded = Enumerable.Range(0, int.MaxValue).Select(index => sample.Evidence with { EvidenceId = $"evidence-{index}" });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CortexRecordProjectionV1(
            1, CortexQueryContractIds.RecordProjection, "record-1", "revision-1", "project:alpha", "testing",
            "Title", "Summary", "Content", Array.Empty<string>(),
            sample.Record.Applicability,
            unbounded.Take(CortexQueryContractLimits.MaximumEvidenceItems + 1).ToList(),
            true, sample.Provenance, "internal", sample.Confidence, sample.Freshness, "active", false,
            CortexQueryContractIds.ContentTreatment, false, sample.Now, sample.HashA, CortexQueryContractIds.SafetyNotice));
        var unboundedGrants = Enumerable.Range(0, int.MaxValue).Select(index =>
            new CortexQueryNamespaceGrant(
                $"project:project-{index}",
                new[] { CortexQueryOperations.Search },
                "public",
                new[] { "current" },
                new[] { "active" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CortexQueryAuthorityContext(
            "tenant-1", "nexus", "subject-1", "service-1", 1, unboundedGrants));

        Assert.Throws<ArgumentException>(() => new CortexDiagnosticV1(
            "cortex_query_internal_failure",
            "raw secret or exception message",
            "error",
            "cortex.query.search",
            false));
        Assert.Throws<ArgumentException>(() => new CortexDiagnosticV1(
            "cortex_query_invalid_request",
            CortexQueryDiagnosticCatalog.MessageFor("cortex_query_invalid_request"),
            "error",
            "cortex.query.search",
            false,
            new CortexDiagnosticContextV1(null, "", null, null, null, null, null, null)));
    }

    private static void CursorBindingIsFailClosed()
    {
        var sample = Samples.Create();
        var expected = new CortexCursorValidationContext(
            "cursor-v1", "tenant-1", "nexus", "subject-1", "service-1", 7,
            sample.HashB, "project:alpha", CortexQueryOperations.Search, sample.HashA, 25,
            "rank-v1", "index-1");
        var accepted = CortexQueryContractBoundary.ValidateCursorPayload(
            sample.CursorPayload, expected, sample.Now.AddMinutes(1), CancellationToken.None);
        var scopeDrift = CortexQueryContractBoundary.ValidateCursorPayload(
            sample.CursorPayload, expected with { ScopeFingerprint = sample.HashC }, sample.Now.AddMinutes(1), CancellationToken.None);
        var expired = CortexQueryContractBoundary.ValidateCursorPayload(
            sample.CursorPayload, expected, sample.Now.AddMinutes(20), CancellationToken.None);
        var future = CortexQueryContractBoundary.ValidateCursorPayload(
            sample.CursorPayload with { IssuedAt = sample.Now.AddMinutes(5), ExpiresAt = sample.Now.AddMinutes(10) },
            expected,
            sample.Now,
            CancellationToken.None);
        var oversizedSearchPage = CortexQueryContractBoundary.ValidateCursorPayload(
            sample.CursorPayload with { PageSize = 51 },
            expected with { PageSize = 51 },
            sample.Now.AddMinutes(1),
            CancellationToken.None);

        Require(accepted.Accepted, "Valid bound cursor should pass.");
        Require(!scopeDrift.Accepted && scopeDrift.Diagnostics.Single().Code == "cortex_query_cursor_invalid", "Scope drift must fail generically.");
        Require(!expired.Accepted && expired.Diagnostics.Single().Code == "cortex_query_cursor_invalid", "Expiry must fail generically.");
        Require(!future.Accepted && future.Diagnostics.Single().Code == "cortex_query_cursor_invalid", "Future issue time must fail generically.");
        Require(!oversizedSearchPage.Accepted && oversizedSearchPage.Diagnostics.Single().Code == "cortex_query_cursor_invalid", "Search cursor page size must remain at or below 50.");

        var namespaceListPayload = sample.CursorPayload with
        {
            Namespace = null,
            Operation = CortexQueryOperations.ListNamespaces
        };
        var namespaceExpected = expected with
        {
            Namespace = null,
            Operation = CortexQueryOperations.ListNamespaces
        };
        Require(CortexQueryContractBoundary.ValidateCursorPayload(
            namespaceListPayload,
            namespaceExpected,
            sample.Now.AddMinutes(1),
            CancellationToken.None).Accepted,
            "Namespace-list cursors require a null operation scope, not a fabricated namespace.");
    }

    private static void SchemaIsStrict()
    {
        var root = FindProjectRoot();
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "cortex-query-contracts.v1.schema.json")));
        var definitions = schema.RootElement.GetProperty("$defs");
        foreach (var name in new[]
                 {
                     "discoveryRequest", "healthRequest", "namespaceListRequest", "searchRequest",
                     "searchRequestV1_1",
                     "recordReadRequest", "lineageRequest", "queryResponse", "capabilities",
                     "serviceStatus", "recordProjection", "cursorPayload", "diagnostic"
                 })
        {
            Require(definitions.GetProperty(name).GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
                $"Schema definition '{name}' is not strict.");
        }

        var raw = schema.RootElement.GetRawText();
        Require(!raw.Contains("authorityContext", StringComparison.OrdinalIgnoreCase), "Caller authority leaked into the wire schema.");
        Require(raw.Contains("supportedResponseContracts", StringComparison.Ordinal), "Response negotiation is missing.");
        Require(raw.Contains("scopeFingerprint", StringComparison.Ordinal), "Cursor scope binding is missing.");
        Require(raw.Contains("indexGeneration", StringComparison.Ordinal), "Cursor index binding is missing.");
        Require(raw.Contains("sourceRevisions", StringComparison.Ordinal), "Revision-qualified lineage is missing.");
        Require(raw.Contains("applicability", StringComparison.Ordinal), "Product/component applicability is missing.");
        Require(raw.Contains("untrusted_data", StringComparison.Ordinal), "Record safety labeling is missing.");
        Require(raw.Contains("pegasus.cortex.search-request/1.1", StringComparison.Ordinal) &&
                raw.Contains("\"broad\"", StringComparison.Ordinal),
            "The opt-in versioned broad-search contract is missing.");
    }

    private static T RoundTrip<T>(T value)
    {
        var bytes = CortexCanonicalJson.Serialize(value);
        return CortexCanonicalJson.Deserialize<T>(bytes);
    }

    private static void RequireJsonRejected<T>(string json, string message)
    {
        try
        {
            _ = CortexCanonicalJson.Deserialize<T>(Encoding.UTF8.GetBytes(json));
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    internal static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Samples(
        DateTimeOffset Now,
        string HashA,
        string HashB,
        string HashC,
        CortexEvidenceProjectionV1 Evidence,
        CortexApplicabilityProjectionV1 Applicability,
        CortexProvenanceProjectionV1 Provenance,
        CortexConfidenceProjectionV1 Confidence,
        CortexFreshnessProjectionV1 Freshness,
        CortexSearchRequestV1 SearchRequest,
        CortexQueryCapabilitiesV1 Capabilities,
        CortexServiceStatusV1 Status,
        CortexRecordSummaryV1 Summary,
        CortexRecordProjectionV1 Record,
        CortexNamespacePageV1 NamespacePage,
        CortexSearchPageV1 SearchPage,
        CortexLineagePageV1 LineagePage,
        CortexQueryResponseV1<CortexSearchPageV1> SearchResponse,
        CortexCursorPayloadV1 CursorPayload)
    {
        public static Samples Create()
        {
            var now = DateTimeOffset.Parse("2026-07-14T14:00:00Z");
            var hashA = new string('a', 64);
            var hashB = new string('b', 64);
            var hashC = new string('c', 64);
            var evidence = new CortexEvidenceProjectionV1(
                "evidence-1", "artifact://verification/1", "application/json", hashC, "verified", now, "1.0");
            var applicability = new CortexApplicabilityProjectionV1(
                "pegasus-cortex", "query-contracts", "1.2026.07.14.07.05.A", null, "verified");
            var source = new CortexLineageLinkV1("derived_from", "project:alpha", "record-source", "revision-source", hashB);
            var provenance = new CortexProvenanceProjectionV1(
                "pegasus.cortex.knowledge-record/1.0", "nexus", "subject-1", "service-1",
                "agent-1", "instance-1", "machine-1", "operation-1", "task-1", "correlation-1",
                new[] { source }, true, new[] { "sanitized_projection" }, true);
            var confidence = new CortexConfidenceProjectionV1(9000, "verified evidence");
            var freshness = new CortexFreshnessProjectionV1("current", now, now.AddDays(30), "verified checkpoint");
            var searchRequest = new CortexSearchRequestV1(
                1, CortexQueryContractIds.SearchRequest, "request-search", "project:alpha", "bounded query",
                "testing", "pegasus-cortex", null, "internal", new[] { "current" }, 25, null);
            var limits = new CortexQueryLimitsV1(100, 50, 512, 2048, 131072, 262144, 32, 32, 16, 100);
            var capabilities = new CortexQueryCapabilitiesV1(
                1,
                CortexQueryContractIds.Capabilities,
                "1.2026.07.14.08.25.A",
                new[]
                {
                    CortexQueryContractIds.DiscoveryRequest, CortexQueryContractIds.HealthRequest,
                    CortexQueryContractIds.NamespaceListRequest, CortexQueryContractIds.SearchRequest,
                    CortexQueryContractIds.SearchRequestV1_1,
                    CortexQueryContractIds.RecordReadRequest, CortexQueryContractIds.LineageRequest
                },
                new[] { CortexQueryContractIds.Response, CortexQueryContractIds.Capabilities, CortexQueryContractIds.ServiceStatus, CortexQueryContractIds.RecordProjection },
                new[] { "cursor-v1" },
                new[]
                {
                    new CortexOperationSupportV1(CortexQueryOperations.Discover, true, false, false, 0),
                    new CortexOperationSupportV1(CortexQueryOperations.Health, true, false, false, 0),
                    new CortexOperationSupportV1(CortexQueryOperations.ListNamespaces, true, false, true, 100),
                    new CortexOperationSupportV1(CortexQueryOperations.Search, true, true, true, 50),
                    new CortexOperationSupportV1(CortexQueryOperations.GetRecord, true, true, false, 0),
                    new CortexOperationSupportV1(CortexQueryOperations.GetLineage, true, true, true, 100)
                },
                limits);
            var dependencies = new[]
            {
                new CortexDependencyStatusV1("store", true, true, "ready", "store-1", null, now),
                new CortexDependencyStatusV1("schema", true, true, "ready", "schema-1", null, now),
                new CortexDependencyStatusV1("index", true, true, "ready", "index-1", null, now),
                new CortexDependencyStatusV1("gateway", false, true, "ready", "gateway-1", null, now)
            };
            var status = new CortexServiceStatusV1(
                1, CortexQueryContractIds.ServiceStatus, "1.2026.07.14.08.25.A", "healthy", "ready", "index-1", now, dependencies);
            var summary = new CortexRecordSummaryV1(
                "record-1", "revision-1", "project:alpha", "testing", "A bounded record", "A bounded summary.",
                "A bounded excerpt.", false, hashA, "active", confidence, freshness, "internal", 910000,
                "rank-v1", new[] { applicability }, new[] { evidence }, true, provenance, CortexQueryContractIds.ContentTreatment, false,
                CortexQueryContractIds.SafetyNotice);
            var record = new CortexRecordProjectionV1(
                1, CortexQueryContractIds.RecordProjection, "record-1", "revision-1", "project:alpha", "testing",
                "A bounded record", "A bounded summary.", "A bounded complete record.", new[] { "bounded", "record" },
                new[] { applicability }, new[] { evidence }, true, provenance, "internal", confidence, freshness, "active", false,
                CortexQueryContractIds.ContentTreatment, false, now, hashA, CortexQueryContractIds.SafetyNotice);
            var namespacePage = new CortexNamespacePageV1(
                new[] { new CortexNamespaceProjectionV1("project:alpha", 7) },
                new CortexPageMetadataV1(25, 1, null, "index-1", now));
            var searchPage = new CortexSearchPageV1(
                hashA,
                new[] { summary },
                new CortexPageMetadataV1(25, 1, null, "index-1", now));
            var lineagePage = new CortexLineagePageV1(
                record.RecordId,
                record.RevisionId,
                "ancestors",
                new[] { source },
                new CortexPageMetadataV1(25, 1, null, "index-1", now));
            var response = new CortexQueryResponseV1<CortexSearchPageV1>(
                1, CortexQueryContractIds.Response, CortexQueryOperations.Search, "request-search", true, searchPage, []);
            var cursor = new CortexCursorPayloadV1(
                1, CortexQueryContractIds.CursorPayload, "cursor-v1", "tenant-1", "nexus", "subject-1",
                "service-1", 7, hashB, "project:alpha", CortexQueryOperations.Search, hashA, 25,
                "rank-v1", "index-1", "record-1:revision-1", now, now.AddMinutes(10));
            return new Samples(
                now, hashA, hashB, hashC, evidence, applicability, provenance, confidence, freshness, searchRequest,
                capabilities, status, summary, record, namespacePage, searchPage, lineagePage, response, cursor);
        }
    }
}
