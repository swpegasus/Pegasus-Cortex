using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Query;

internal static class QueryFingerprintSourceTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("query fingerprints match language-neutral golden vectors", GoldenVectorsMatch),
        ("query fingerprints isolate mutation and canonicalize semantic sets", OrderingMutationAndExclusions),
        ("query fingerprints bind every semantic field and cursor page size separately", IncludedFieldsAndCursorBinding),
        ("versioned search fingerprints bind exact and broad modes", SearchModeIsVersionedAndBound),
        ("query scope fingerprints bind exact server-visible authority", ScopeFieldsAreSensitive),
        ("query fingerprint boundaries fail closed", BoundariesFailClosed)
    ];

    private static void GoldenVectorsMatch()
    {
        var vectors = LoadVectors();
        AssertVector("namespace-scope-search", CortexQueryFingerprintProducerV1.CreateNamespaceScope(
            new CortexQueryNamespaceAccess(
                "tenant-1", "project:alpha", "nexus", "subject-1", "service-1", 7,
                CortexQueryOperations.Search, "internal", new[] { "stale", "current" },
                new[] { "superseded", "active", "archived" })), vectors);

        var authorityA = CreateListAuthority(reverse: false);
        var authorityB = CreateListAuthority(reverse: true);
        var authorityFingerprintA = CortexQueryFingerprintProducerV1.CreateNamespaceListScope(authorityA);
        var authorityFingerprintB = CortexQueryFingerprintProducerV1.CreateNamespaceListScope(authorityB);
        AssertVector("authority-list-order-a", authorityFingerprintA, vectors);
        AssertVector("authority-list-order-b", authorityFingerprintB, vectors);
        Require(authorityFingerprintA.Sha256 == authorityFingerprintB.Sha256,
            "Grant and set permutation changed the authority-list scope fingerprint.");

        AssertVector("request-discover", CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexDiscoveryRequestV1(1, CortexQueryContractIds.DiscoveryRequest, "request-discover")), vectors);
        AssertVector("request-health", CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexHealthRequestV1(1, CortexQueryContractIds.HealthRequest, "request-health")), vectors);
        AssertVector("request-list-namespaces", CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexNamespaceListRequestV1(
                1, CortexQueryContractIds.NamespaceListRequest, "request-list", 25, null)), vectors);
        AssertVector("request-search-full-unicode", CortexQueryFingerprintProducerV1.CreateRequest(
            Search(
                "request-full",
                "Say \"hi\"\\path\tcolumn\r\ncaf\u00e9 \U0001F600",
                "testing",
                "pegasus-cortex",
                "query-contracts",
                "internal",
                new[] { "stale", "current" },
                25,
                null)), vectors);
        AssertVector("request-search-null-filters", CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-null", "bounded query", null, null, null, "public", new[] { "unknown" }, 10, null)), vectors);
        AssertVector("request-search-composed", CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-composed", "caf\u00e9", null, null, null, "internal", new[] { "current" }, 10, null)), vectors);
        AssertVector("request-search-decomposed", CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-decomposed", "cafe\u0301", null, null, null, "internal", new[] { "current" }, 10, null)), vectors);
        AssertVector("request-search-broad-v1-1", CortexQueryFingerprintProducerV1.CreateRequest(
            VersionedSearch("request-broad", CortexSearchModes.Broad)), vectors);
        AssertVector("request-get-record", CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexRecordReadRequestV1(
                1, CortexQueryContractIds.RecordReadRequest, "request-record",
                "project:alpha", "record-1", "revision-2")), vectors);
        AssertVector("request-get-lineage", CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexLineageRequestV1(
                1, CortexQueryContractIds.LineageRequest, "request-lineage",
                "project:alpha", "record-1", "revision-2", "ancestors", 25, null)), vectors);

        Require(vectors.Count == 13, "The checked-in golden-vector inventory changed without test coverage.");
        using var listCanonical = JsonDocument.Parse(vectors["request-list-namespaces"].CanonicalJson);
        Require(listCanonical.RootElement.GetArrayLength() == 15,
            "The fixed request fingerprint recipe no longer has 15 positions.");
        Require(listCanonical.RootElement[5].ValueKind == JsonValueKind.Null &&
                listCanonical.RootElement[14].ValueKind == JsonValueKind.Null,
            "Namespace-list unused request slots must remain explicit JSON null values.");
    }

    private static void OrderingMutationAndExclusions()
    {
        var mutableFreshness = new List<string> { "stale", "current" };
        var request = Search(
            "request-a", "bounded query", "testing", "pegasus-cortex", "query-contracts",
            "internal", mutableFreshness, 25, null);
        var first = CortexQueryFingerprintProducerV1.CreateRequest(request);
        mutableFreshness.Clear();
        mutableFreshness.Add("blocked");
        var second = CortexQueryFingerprintProducerV1.CreateRequest(request);
        Require(first.Sha256 == second.Sha256, "Caller mutation changed the captured request fingerprint.");
        Require(request.FreshnessStates.SequenceEqual(new[] { "stale", "current" }),
            "Fingerprint production reordered or mutated the request snapshot.");

        var reversed = Search(
            "request-b", "bounded query", "testing", "pegasus-cortex", "query-contracts",
            "internal", new[] { "current", "stale" }, 25, null);
        Require(first.Sha256 == CortexQueryFingerprintProducerV1.CreateRequest(reversed).Sha256,
            "Freshness set order changed the request fingerprint.");

        var changedTrace = Search(
            "request-other", "bounded query", "testing", "pegasus-cortex", "query-contracts",
            "internal", new[] { "current", "stale" }, 25, "cursor-v1.token");
        var changedPage = Search(
            "request-a", "bounded query", "testing", "pegasus-cortex", "query-contracts",
            "internal", new[] { "current", "stale" }, 10, null);
        Require(first.Sha256 == CortexQueryFingerprintProducerV1.CreateRequest(changedTrace).Sha256,
            "Trace request ID or opaque cursor changed the semantic query fingerprint.");
        Require(first.Sha256 == CortexQueryFingerprintProducerV1.CreateRequest(changedPage).Sha256,
            "Page size should be bound by cursor state, not duplicated in the query fingerprint.");

        var exported = first.ExportCanonicalUtf8ForConformance();
        var expectedFirstByte = exported[0];
        exported[0] ^= 0xff;
        Require(first.ExportCanonicalUtf8ForConformance()[0] == expectedFirstByte,
            "Exported conformance bytes mutated the fingerprint value.");
    }

    private static void IncludedFieldsAndCursorBinding()
    {
        var baseline = Search(
            "request-base", "text-a", "taxonomy-a", "product-a", "component-a",
            "internal", new[] { "current" }, 25, null);
        var baselineHash = CortexQueryFingerprintProducerV1.CreateRequest(baseline).Sha256;
        var changed = new ICortexQueryRequestV1[]
        {
            new CortexSearchRequestV1(1, CortexQueryContractIds.SearchRequest, "request-base", "project:beta", "text-a", "taxonomy-a", "product-a", "component-a", "internal", new[] { "current" }, 25, null),
            Search("request-base", "text-b", "taxonomy-a", "product-a", "component-a", "internal", new[] { "current" }, 25, null),
            Search("request-base", "text-a", "taxonomy-b", "product-a", "component-a", "internal", new[] { "current" }, 25, null),
            Search("request-base", "text-a", "taxonomy-a", "product-b", "component-a", "internal", new[] { "current" }, 25, null),
            Search("request-base", "text-a", "taxonomy-a", "product-a", "component-b", "internal", new[] { "current" }, 25, null),
            Search("request-base", "text-a", "taxonomy-a", "product-a", "component-a", "confidential", new[] { "current" }, 25, null),
            Search("request-base", "text-a", "taxonomy-a", "product-a", "component-a", "internal", new[] { "stale" }, 25, null)
        };
        Require(changed.All(item => CortexQueryFingerprintProducerV1.CreateRequest(item).Sha256 != baselineHash),
            "A semantic search field was omitted from the request fingerprint.");

        var record = new CortexRecordReadRequestV1(
            1, CortexQueryContractIds.RecordReadRequest, "request-record", "project:alpha", "record-1", "revision-1");
        var recordHash = CortexQueryFingerprintProducerV1.CreateRequest(record).Sha256;
        Require(recordHash != CortexQueryFingerprintProducerV1.CreateRequest(new CortexRecordReadRequestV1(
                    1, CortexQueryContractIds.RecordReadRequest, "request-record", "project:alpha", "record-2", "revision-1")).Sha256 &&
                recordHash != CortexQueryFingerprintProducerV1.CreateRequest(new CortexRecordReadRequestV1(
                    1, CortexQueryContractIds.RecordReadRequest, "request-record", "project:alpha", "record-1", "revision-2")).Sha256,
            "Record or revision identity was omitted from the request fingerprint.");

        var lineage = new CortexLineageRequestV1(
            1, CortexQueryContractIds.LineageRequest, "request-lineage", "project:alpha",
            "record-1", "revision-1", "ancestors", 25, null);
        Require(CortexQueryFingerprintProducerV1.CreateRequest(lineage).Sha256 !=
                CortexQueryFingerprintProducerV1.CreateRequest(new CortexLineageRequestV1(
                    1, CortexQueryContractIds.LineageRequest, "request-lineage", "project:alpha",
                    "record-1", "revision-1", "descendants", 25, null)).Sha256,
            "Lineage direction was omitted from the request fingerprint.");

        var composed = CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-c", "caf\u00e9", null, null, null, "internal", new[] { "current" }, 10, null));
        var decomposed = CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-d", "cafe\u0301", null, null, null, "internal", new[] { "current" }, 10, null));
        Require(composed.Sha256 != decomposed.Sha256,
            "The fingerprint producer normalized distinct Unicode scalar sequences.");

        var access = new CortexQueryNamespaceAccess(
            "tenant-1", "project:alpha", "nexus", "subject-1", "service-1", 7,
            CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" });
        var scopeHash = CortexQueryFingerprintProducerV1.CreateNamespaceScope(access).Sha256;
        var queryHash = CortexQueryFingerprintProducerV1.CreateRequest(baseline).Sha256;
        Require(scopeHash != queryHash, "Scope and request domains were not separated.");

        var now = DateTimeOffset.Parse("2026-07-14T15:00:00Z");
        var payload = new CortexCursorPayloadV1(
            1, CortexQueryContractIds.CursorPayload, "cursor-v1", "tenant-1", "nexus", "subject-1",
            "service-1", 7, scopeHash, "project:alpha", CortexQueryOperations.Search, queryHash, 25,
            "rank-v1", "index-1", "record-1:revision-1", now, now.AddMinutes(10));
        var wrongPage = new CortexCursorValidationContext(
            "cursor-v1", "tenant-1", "nexus", "subject-1", "service-1", 7, scopeHash,
            "project:alpha", CortexQueryOperations.Search, queryHash, 10, "rank-v1", "index-1");
        var rejected = CortexQueryContractBoundary.ValidateCursorPayload(
            payload, wrongPage, now.AddMinutes(1), CancellationToken.None);
        Require(!rejected.Accepted && rejected.Diagnostics.Single().Code == "cortex_query_cursor_invalid",
            "Cursor page-size drift did not fail through the generic cursor diagnostic.");
    }

    private static void SearchModeIsVersionedAndBound()
    {
        var exact = CortexQueryFingerprintProducerV1.CreateRequest(
            VersionedSearch("request-exact", CortexSearchModes.Exact));
        var broad = CortexQueryFingerprintProducerV1.CreateRequest(
            VersionedSearch("request-broad", CortexSearchModes.Broad));
        Require(exact.Sha256 != broad.Sha256,
            "Exact and broad search modes produced the same fingerprint.");

        var legacy = CortexQueryFingerprintProducerV1.CreateRequest(
            Search("request-legacy", "bounded query", null, null, null, "public", new[] { "current" }, 10, null));
        using var legacyJson = JsonDocument.Parse(legacy.ExportCanonicalUtf8ForConformance());
        Require(legacyJson.RootElement[14].ValueKind == JsonValueKind.Null,
            "The legacy exact-search fingerprint shape changed.");
        using var exactJson = JsonDocument.Parse(exact.ExportCanonicalUtf8ForConformance());
        Require(exactJson.RootElement[14].GetString() == CortexSearchModes.Exact,
            "Versioned exact mode was not bound to the request fingerprint.");
    }

    private static void BoundariesFailClosed()
    {
        var illFormed = Search(
            "request-invalid", "bad\ud800", null, null, null, "internal", new[] { "current" }, 10, null);
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateRequest(illFormed));
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateNamespaceScope(
            new CortexQueryNamespaceAccess(
                "tenant-1", "project:alpha", "nexus", "subject-1", "service-1", 7,
                CortexQueryOperations.ListNamespaces, "internal", new[] { "current" }, new[] { "active" })));
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateNamespaceScope(
            new CortexQueryNamespaceAccess(
                "tenant-1", "project:alpha", "nexus", "subject-1", "service-1", 7,
                CortexQueryOperations.Search, "internal", new[] { "future" }, new[] { "active" })));

        var duplicate = new CortexQueryNamespaceGrant(
            "project:alpha", new[] { CortexQueryOperations.ListNamespaces }, "internal",
            new[] { "current" }, new[] { "active" });
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateNamespaceListScope(
            new CortexQueryAuthorityContext(
                "tenant-1", "nexus", "subject-1", "service-1", 7, new[] { duplicate, duplicate })));
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateNamespaceListScope(
            new CortexQueryAuthorityContext(
                "tenant-1", "nexus", "subject-1", "service-1", 7,
                new[]
                {
                    new CortexQueryNamespaceGrant(
                        "project:hidden", new[] { CortexQueryOperations.Search }, "internal",
                        new[] { "current" }, new[] { "active" })
                })));
        Assert.Throws<ArgumentException>(() => CortexQueryFingerprintProducerV1.CreateRequest(
            new UnsupportedRequest(1, "pegasus.cortex.unsupported-request/1.0", "request-unsupported")));

        var valid = CortexQueryFingerprintProducerV1.CreateRequest(
            new CortexHealthRequestV1(1, CortexQueryContractIds.HealthRequest, "request-health"));
        Require(valid.Sha256.Length == 64 && valid.Sha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Fingerprint output is not lowercase 64-character SHA-256.");
    }

    private static void ScopeFieldsAreSensitive()
    {
        var baselineAccess = Access(
            "tenant-1", "nexus", "subject-1", "service-1", 7, "project:alpha",
            CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" });
        var baselineNamespaceHash = CortexQueryFingerprintProducerV1.CreateNamespaceScope(baselineAccess).Sha256;
        var changedAccesses = new[]
        {
            Access("tenant-2", "nexus", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "external", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-2", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-2", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 8, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 7, "project:beta", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.GetRecord, "internal", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "confidential", new[] { "current" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "stale" }, new[] { "active" }),
            Access("tenant-1", "nexus", "subject-1", "service-1", 7, "project:alpha", CortexQueryOperations.Search, "internal", new[] { "current" }, new[] { "archived" })
        };
        Require(changedAccesses.All(access =>
                CortexQueryFingerprintProducerV1.CreateNamespaceScope(access).Sha256 != baselineNamespaceHash),
            "A namespace-scope field was omitted from the fingerprint.");

        var visible = Grant(
            "project:alpha", "internal",
            new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord },
            new[] { "current" },
            new[] { "active" });
        var hidden = Grant(
            "project:hidden", "public", new[] { CortexQueryOperations.Search },
            new[] { "current" }, new[] { "active" });
        var baselineAuthority = Authority("tenant-1", "nexus", "subject-1", "service-1", 7, visible, hidden);
        var baselineAuthorityValue = CortexQueryFingerprintProducerV1.CreateNamespaceListScope(baselineAuthority);
        var changedAuthorities = new[]
        {
            Authority("tenant-2", "nexus", "subject-1", "service-1", 7, visible, hidden),
            Authority("tenant-1", "external", "subject-1", "service-1", 7, visible, hidden),
            Authority("tenant-1", "nexus", "subject-2", "service-1", 7, visible, hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-2", 7, visible, hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 8, visible, hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7,
                Grant("project:beta", "internal", new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord }, new[] { "current" }, new[] { "active" }), hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7,
                Grant("project:alpha", "confidential", new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord }, new[] { "current" }, new[] { "active" }), hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7,
                Grant("project:alpha", "internal", new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.Search }, new[] { "current" }, new[] { "active" }), hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7,
                Grant("project:alpha", "internal", new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord }, new[] { "stale" }, new[] { "active" }), hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7,
                Grant("project:alpha", "internal", new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord }, new[] { "current" }, new[] { "archived" }), hidden),
            Authority("tenant-1", "nexus", "subject-1", "service-1", 7, visible, hidden,
                Grant("project:zeta", "public", new[] { CortexQueryOperations.ListNamespaces }, new[] { "current" }, new[] { "active" }))
        };
        Require(changedAuthorities.All(authority =>
                CortexQueryFingerprintProducerV1.CreateNamespaceListScope(authority).Sha256 != baselineAuthorityValue.Sha256),
            "An authority principal, policy, or visible-grant field was omitted from the fingerprint.");

        var changedHidden = Authority(
            "tenant-1", "nexus", "subject-1", "service-1", 7, visible,
            Grant("project:hidden-other", "restricted", new[] { CortexQueryOperations.GetLineage }, new[] { "unknown" }, new[] { "archived" }),
            Grant("project:hidden-extra", "public", new[] { CortexQueryOperations.Search }, new[] { "stale" }, new[] { "superseded" }));
        var changedHiddenValue = CortexQueryFingerprintProducerV1.CreateNamespaceListScope(changedHidden);
        Require(changedHiddenValue.Sha256 == baselineAuthorityValue.Sha256 &&
                changedHiddenValue.ExportCanonicalUtf8ForConformance().SequenceEqual(
                    baselineAuthorityValue.ExportCanonicalUtf8ForConformance()),
            "A grant without list_namespaces changed the complete visible grant view.");
        var canonical = Encoding.UTF8.GetString(baselineAuthorityValue.ExportCanonicalUtf8ForConformance());
        Require(!canonical.Contains("project:hidden", StringComparison.Ordinal),
            "A non-visible grant leaked into the authority-list canonical bytes.");
    }

    private static CortexSearchRequestV1 Search(
        string requestId,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        string sensitivity,
        IReadOnlyList<string> freshness,
        int pageSize,
        string? cursor) =>
        new(
            1,
            CortexQueryContractIds.SearchRequest,
            requestId,
            "project:alpha",
            text,
            taxonomy,
            productId,
            componentId,
            sensitivity,
            freshness,
            pageSize,
            cursor);

    private static CortexSearchRequestV1_1 VersionedSearch(string requestId, string mode) =>
        new(
            1,
            CortexQueryContractIds.SearchRequestV1_1,
            requestId,
            "project:alpha",
            "bounded query",
            null,
            null,
            null,
            "public",
            new[] { "current" },
            10,
            null,
            mode);

    private static CortexQueryNamespaceAccess Access(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        string namespaceName,
        string operation,
        string sensitivity,
        IReadOnlyList<string> freshness,
        IReadOnlyList<string> recordStates) =>
        new(
            tenantId,
            namespaceName,
            authorityId,
            subjectId,
            serviceId,
            policyRevision,
            operation,
            sensitivity,
            freshness,
            recordStates);

    private static CortexQueryNamespaceGrant Grant(
        string namespaceName,
        string sensitivity,
        IReadOnlyList<string> operations,
        IReadOnlyList<string> freshness,
        IReadOnlyList<string> recordStates) =>
        new(namespaceName, operations, sensitivity, freshness, recordStates);

    private static CortexQueryAuthorityContext Authority(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        params CortexQueryNamespaceGrant[] grants) =>
        new(tenantId, authorityId, subjectId, serviceId, policyRevision, grants);

    private static CortexQueryAuthorityContext CreateListAuthority(bool reverse)
    {
        var alpha = new CortexQueryNamespaceGrant(
            "project:alpha",
            reverse
                ? new[] { CortexQueryOperations.GetRecord, CortexQueryOperations.ListNamespaces }
                : new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.GetRecord },
            "internal",
            reverse ? new[] { "current", "unknown" } : new[] { "unknown", "current" },
            reverse ? new[] { "active", "archived" } : new[] { "archived", "active" });
        var zeta = new CortexQueryNamespaceGrant(
            "project:zeta",
            reverse
                ? new[] { CortexQueryOperations.ListNamespaces, CortexQueryOperations.Search }
                : new[] { CortexQueryOperations.Search, CortexQueryOperations.ListNamespaces },
            "confidential",
            reverse ? new[] { "current", "stale" } : new[] { "stale", "current" },
            reverse ? new[] { "active", "superseded" } : new[] { "superseded", "active" });
        var hidden = new CortexQueryNamespaceGrant(
            "project:hidden", new[] { CortexQueryOperations.Search }, "public",
            new[] { "current" }, new[] { "active" });
        return new CortexQueryAuthorityContext(
            "tenant-1",
            "nexus",
            "subject-1",
            "service-1",
            7,
            reverse ? new[] { alpha, zeta, hidden } : new[] { zeta, hidden, alpha });
    }

    private static IReadOnlyDictionary<string, GoldenVector> LoadVectors()
    {
        var path = Path.Combine(
            QueryContractSourceTests.FindProjectRoot(),
            "schemas",
            "cortex-query-fingerprint-v1.vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 &&
                root.GetProperty("contractId").GetString() == "pegasus.cortex.query-fingerprint-vectors/1.0",
            "The fingerprint vector header is invalid.");
        var result = new Dictionary<string, GoldenVector>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("vectors").EnumerateArray())
        {
            var vector = new GoldenVector(
                item.GetProperty("canonicalJson").GetString() ?? throw new InvalidOperationException("Missing canonical JSON."),
                item.GetProperty("canonicalUtf8Base64").GetString() ?? throw new InvalidOperationException("Missing canonical bytes."),
                item.GetProperty("sha256").GetString() ?? throw new InvalidOperationException("Missing vector hash."));
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || !result.TryAdd(id, vector))
                throw new InvalidOperationException("The golden-vector file contains a duplicate or empty ID.");
        }
        return result;
    }

    private static void AssertVector(
        string id,
        CortexQueryFingerprintValueV1 actual,
        IReadOnlyDictionary<string, GoldenVector> vectors)
    {
        if (!vectors.TryGetValue(id, out var expected))
            throw new InvalidOperationException($"Golden vector '{id}' is missing.");
        var bytes = actual.ExportCanonicalUtf8ForConformance();
        var actualBase64 = Convert.ToBase64String(bytes);
        Require(bytes.SequenceEqual(Convert.FromBase64String(expected.CanonicalUtf8Base64)),
            $"Golden vector '{id}' canonical bytes changed. Expected base64 '{expected.CanonicalUtf8Base64}', actual '{actualBase64}'.");
        Require(Encoding.UTF8.GetString(bytes) == expected.CanonicalJson,
            $"Golden vector '{id}' canonical text changed.");
        Require(actual.CanonicalByteCount == bytes.Length,
            $"Golden vector '{id}' byte count changed.");
        Require(actual.Sha256 == expected.Sha256,
            $"Golden vector '{id}' SHA-256 changed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record GoldenVector(
        string CanonicalJson,
        string CanonicalUtf8Base64,
        string Sha256);

    private sealed record UnsupportedRequest(
        int SchemaVersion,
        string ContractId,
        string RequestId) : ICortexQueryRequestV1;
}
