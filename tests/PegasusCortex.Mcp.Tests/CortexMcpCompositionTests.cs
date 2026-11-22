using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Shared.Diagnostics;

internal static class CortexMcpCompositionTests
{
    private static readonly string ProjectLinkId =
        $"plink_{new string('a', 64)}";
    private const string ProjectId = "project-local";
    private const string ProjectNamespace = "project.local";

    public static Task GeneralSearchMergeBalancesSourcesAsync()
    {
        var defaultHits = Enumerable.Range(0, 10)
            .Select(index => new CortexMcpApplication.RoutedSearchHit(
                new CortexKnowledgeSearchHit(
                    CompositionSamples.Record(
                        $"krec_{new string((char)('0' + index), 64)}",
                        $"krev_{new string((char)('0' + index), 64)}",
                        new string((char)('0' + index), 64)),
                    1.0 - index * 0.001,
                    CortexQueryPolicy.SafetyNotice),
                ProjectLinkId: null));
        var projectLinkId = $"plink_{new string('f', 64)}";
        var collectiveHit = new CortexMcpApplication.RoutedSearchHit(
            new CortexKnowledgeSearchHit(
                CompositionSamples.Record(
                    $"krec_{new string('e', 64)}",
                    $"krev_{new string('f', 64)}",
                    new string('f', 64)),
                0.1,
                CortexQueryPolicy.SafetyNotice),
            projectLinkId);
        var secondProjectLinkId = $"plink_{new string('b', 64)}";
        var secondCollectiveHit = new CortexMcpApplication.RoutedSearchHit(
            new CortexKnowledgeSearchHit(
                CompositionSamples.Record(
                    $"krec_{new string('c', 64)}",
                    $"krev_{new string('d', 64)}",
                    new string('d', 64)),
                0.05,
                CortexQueryPolicy.SafetyNotice),
            secondProjectLinkId);

        var merged = CortexMcpApplication.MergeGeneralSearchResults(
            defaultHits.Append(collectiveHit).Append(secondCollectiveHit).ToArray(),
            limit: 3);

        CompositionAssert.Equal(3, merged.Count);
        CompositionAssert.True(
            merged.Any(item => item.ProjectLinkId is null) &&
            merged.Any(item => item.ProjectLinkId == projectLinkId) &&
            merged.Any(item => item.ProjectLinkId == secondProjectLinkId),
            "Global truncation did not reserve one result from every matching source.");
        return Task.CompletedTask;
    }

    public static async Task StartupCatalogSnapshotAsync()
    {
        var service = new RecordingCortexQueryService();
        var resolver = LegacyResolverFixture.Create();
        var authority = CompositionSamples.Authority();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            resolver,
            authority,
            CompositionSamples.Namespace,
            CancellationToken.None);

        CompositionAssert.Equal(1, service.CatalogQueries.Count);
        CompositionAssert.Equal(
            new CortexCatalogQuery(
                CompositionSamples.Namespace,
                CortexMcpContract.MaximumResourceRecords),
            service.CatalogQueries.Single());
        AssertAuthority(authority, service.CatalogAuthorities.Single());

        // The application must own a deep, session-stable startup snapshot.
        service.CatalogRecords.Add(CompositionSamples.Summary(
            CompositionSamples.Record(
                CompositionSamples.ThirdRecordId,
                CompositionSamples.ThirdRevisionId,
                new string('4', 64))));

        var firstResources = await application.ListResourcesAsync(CancellationToken.None);
        var secondResources = await application.ListResourcesAsync(CancellationToken.None);
        CompositionAssert.Equal(firstResources.ToJsonString(), secondResources.ToJsonString());
        CompositionAssert.Equal(3, firstResources.Count);
        AssertKeys(firstResources[0]!.AsObject(), "uri", "name", "description", "mimeType");

        var firstTemplates = await application.ListResourceTemplatesAsync(CancellationToken.None);
        var secondTemplates = await application.ListResourceTemplatesAsync(CancellationToken.None);
        CompositionAssert.Equal(firstTemplates.ToJsonString(), secondTemplates.ToJsonString());
        CompositionAssert.Equal(1, firstTemplates.Count);
        AssertKeys(firstTemplates[0]!.AsObject(), "uriTemplate", "name", "description", "mimeType");
        CompositionAssert.Equal(
            CortexMcpApplication.RecordResourceUriTemplate,
            String(firstTemplates[0]!.AsObject(), "uriTemplate"));

        var discovery = Structured(await application.CallToolAsync(
            "cortex_discover",
            Arguments(new JsonObject()),
            CancellationToken.None));
        AssertKeys(
            discovery,
            "server",
            "mode",
            "tools",
            "resources",
            "limits",
            "access",
            "storageKind",
            "features",
            "safetyNotice");
        var resourceDiscovery = discovery["resources"]!.AsObject();
        AssertKeys(resourceDiscovery, "status", "catalog", "guide", "recordTemplate", "exposedRecordCount");
        CompositionAssert.Equal(
            CortexMcpApplication.GuideResourceUri,
            String(resourceDiscovery, "guide"));
        CompositionAssert.Equal(
            CortexMcpApplication.RecordResourceUriTemplate,
            String(resourceDiscovery, "recordTemplate"));
        CompositionAssert.Equal(2, Integer(resourceDiscovery, "exposedRecordCount"));

        var firstStatus = Structured(await application.CallToolAsync(
            "cortex_status",
            Arguments(new JsonObject()),
            CancellationToken.None));
        var secondStatus = Structured(await application.CallToolAsync(
            "cortex_status",
            Arguments(new JsonObject()),
            CancellationToken.None));
        CompositionAssert.Equal(firstStatus.ToJsonString(), secondStatus.ToJsonString());
        AssertKeys(
            firstStatus,
            "status",
            "mode",
            "validation",
            "serverBuild",
            "library",
            "contentTreatment",
            "executable",
            "safetyNotice",
            "storageKind",
            "features");
        var serverBuild = firstStatus["serverBuild"]!.AsObject();
        AssertKeys(serverBuild, "fingerprint", "basis");
        var fingerprint = String(serverBuild, "fingerprint");
        CompositionAssert.True(
            fingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            fingerprint.Length == 71,
            "Status did not expose one bounded SHA-256 server build fingerprint.");
        CompositionAssert.True(
            string.Equals(String(serverBuild, "basis"), "entry-assembly-bytes", StringComparison.Ordinal) ||
            string.Equals(String(serverBuild, "basis"), "entry-assembly-metadata-v1", StringComparison.Ordinal),
            "Status exposed an unsupported server build fingerprint basis.");
        CompositionAssert.False(
            fingerprint.Contains('\\') || fingerprint.Contains('/'),
            "Status build fingerprint exposed a path separator.");
        var library = firstStatus["library"]!.AsObject();
        AssertKeys(
            library,
            "releaseVersion",
            "nuGetVersion",
            "bundleId",
            "bundleHash",
            "namespace",
            "recordCount",
            "defaultReadableRecordCount",
            "excludedByDefaultCount",
            "exposedResourceRecordCount",
            "taxonomyCount",
            "taxonomies",
            "freshness");
        CompositionAssert.Equal(2, Integer(library, "exposedResourceRecordCount"));

        var catalogResource = await application.ReadResourceAsync(
            CortexMcpApplication.CatalogResourceUri,
            CancellationToken.None);
        CompositionAssert.NotNull(catalogResource);
        AssertKeys(catalogResource!, "contents");
        var content = catalogResource!["contents"]!.AsArray().Single()!.AsObject();
        AssertKeys(content, "uri", "mimeType", "text");
        var catalog = JsonNode.Parse(String(content, "text"))!.AsObject();
        AssertKeys(catalog, "releaseVersion", "count", "records", "safetyNotice");
        CompositionAssert.Equal(2, Integer(catalog, "count"));

        var guideResource = await application.ReadResourceAsync(
            CortexMcpApplication.GuideResourceUri,
            CancellationToken.None);
        CompositionAssert.NotNull(guideResource);
        var guideContent = guideResource!["contents"]!.AsArray().Single()!.AsObject();
        var guide = JsonNode.Parse(String(guideContent, "text"))!.AsObject();
        CompositionAssert.Equal(CortexContinuityGuide.Version, String(guide, "guideVersion"));
        CompositionAssert.True(
            Boolean(guide["authority"]!.AsObject(), "projectFilesRemainAuthoritative"),
            "The guide did not preserve project-file authority.");
        CompositionAssert.False(
            Boolean(guide["authority"]!.AsObject(), "grantsOperationalAuthority"),
            "The guide unexpectedly granted operational authority.");
        CompositionAssert.Equal(
            CortexContinuityGuide.ExpectedContentSha256,
            String(guide["source"]!.AsObject(), "sha256"));
        CompositionAssert.Equal(1, service.CatalogQueries.Count);
    }

    public static async Task SearchAndReadDelegationAsync()
    {
        var service = new RecordingCortexQueryService();
        var resolver = LegacyResolverFixture.Create();
        var authority = CompositionSamples.Authority();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            resolver,
            authority,
            CompositionSamples.Namespace,
            CancellationToken.None);

        var searchResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "  dependency lifecycle  ",
                ["taxonomy"] = "testing",
                ["productId"] = "pegasus-cortex",
                ["componentId"] = "mcp",
                ["limit"] = 3,
                ["includeStale"] = true,
                ["includeBlockedOrUnverified"] = false
            }),
            CancellationToken.None);

        CompositionAssert.Equal(1, service.SearchQueries.Count);
        CompositionAssert.Equal(
            new CortexSearchQuery(
                CompositionSamples.Namespace,
                "dependency lifecycle",
                "testing",
                "pegasus-cortex",
                "mcp",
                3,
                IncludeStale: true,
                IncludeBlockedOrUnverified: false),
            service.SearchQueries.Single());
        AssertAuthority(authority, service.SearchAuthorities.Single());

        var search = Structured(searchResult);
        AssertToolSuccessEnvelope(searchResult);
        AssertKeys(search, "query", "retrieval", "count", "records", "safetyNotice");
        AssertKeys(
            search["query"]!.AsObject(),
            "text",
            "taxonomy",
            "productId",
            "componentId",
            "limit",
            "includeStale",
            "includeBlockedOrUnverified");
        var searchRecord = search["records"]!.AsArray().Single()!.AsObject();
        AssertKeys(
            searchRecord,
            "recordId",
            "revisionId",
            "resourceUri",
            "namespace",
            "taxonomy",
            "title",
            "summary",
            "contentExcerpt",
            "contentTruncated",
            "applicability",
            "evidence",
            "confidence",
            "freshness",
            "sensitivity",
            "score",
            "contentTreatment",
            "executable",
            "safetyNotice",
            "matchedTerms");

        var readResult = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject
            {
                ["id"] = CompositionSamples.RecordId,
                ["includeStale"] = false,
                ["includeBlockedOrUnverified"] = true
            }),
            CancellationToken.None);

        CompositionAssert.Equal(1, service.ReadQueries.Count);
        CompositionAssert.Equal(
            new CortexRecordReadQuery(
                CompositionSamples.Namespace,
                CompositionSamples.RecordId,
                CompositionSamples.RevisionId,
                IncludeStale: false,
                IncludeBlockedOrUnverified: true),
            service.ReadQueries.Single());
        AssertAuthority(authority, service.ReadAuthorities.Single());

        var read = Structured(readResult);
        AssertToolSuccessEnvelope(readResult);
        AssertKeys(
            read,
            "recordId",
            "revisionId",
            "resourceUri",
            "namespace",
            "taxonomy",
            "slug",
            "title",
            "summary",
            "content",
            "keywords",
            "applicability",
            "evidence",
            "provenance",
            "confidence",
            "freshness",
            "sensitivity",
            "recordStatus",
            "trainingEligibility",
            "contentTreatment",
            "executable",
            "contentHash",
            "safetyNotice");
        AssertKeys(
            read["provenance"]!.AsObject(),
            "producerContract",
            "sourceRecordIds",
            "transformations");
    }

    public static async Task BroadSearchDelegationAsync()
    {
        var service = new RecordingCortexQueryService();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var result = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["mode"] = CortexMcpContract.BroadSearchMode
            }),
            CancellationToken.None);

        CompositionAssert.Equal(1, service.SearchQueries.Count);
        CompositionAssert.Equal(CortexSearchMode.Broad, service.SearchQueries.Single().Mode);
        var search = Structured(result);
        AssertToolSuccessEnvelope(result);
        CompositionAssert.Equal(
            CortexMcpContract.BroadSearchMode,
            String(search["query"]!.AsObject(), "mode"));
    }

    public static async Task BroadSearchPreservesReservedIsolationAsync()
    {
        var reservedProjectLinkId = $"plink_{new string('9', 64)}";
        var collectiveProjectLinkId = $"plink_{new string('8', 64)}";
        var projectKnowledge = new StaticProjectKnowledgeRuntime(
            reservedProjectLinkId,
            collectiveProjectLinkId);
        var runtime = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "broad-isolation-test",
                "ready",
                IssueCode: null,
                EndpointId: "endpoint-test",
                PrincipalName: "principal-test",
                SubjectId: "subject-test",
                CortexMcpRuntimeCapability.Read,
                UsesNetwork: false,
                UsesDatabase: false,
                UsesAuthentication: false,
                UsesLiveServices: false,
                "Test-only project-knowledge routing boundary."),
            ProjectKnowledge: projectKnowledge);
        var application = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            runtime,
            CancellationToken.None);

        var generalResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["mode"] = CortexMcpContract.BroadSearchMode,
                ["limit"] = 3
            }),
            CancellationToken.None);
        var generalRecords = Structured(generalResult)["records"]!.AsArray();
        var generalRetrieval = Structured(generalResult)["retrieval"]!.AsObject();
        CompositionAssert.Equal("general-collective", String(generalRetrieval, "route"));
        CompositionAssert.Equal(2, Integer(generalRetrieval, "queriedSourceCount"));
        CompositionAssert.Equal(0, Integer(generalRetrieval, "partialFailureCount"));
        CompositionAssert.Equal(0, projectKnowledge.ProjectCalls.Count);
        CompositionAssert.Equal(CortexSearchMode.Broad, projectKnowledge.CollectiveCalls.Single());
        var generalizedRecordId = $"krec_{new string('8', 64)}";
        var generalized = generalRecords
            .Select(node => node!.AsObject())
            .Single(node => String(node, "recordId") == generalizedRecordId);
        CompositionAssert.False(
            generalized.ContainsKey("projectLinkId") ||
            generalized.ContainsKey("namespace") ||
            generalized.ContainsKey("resourceUri") ||
            generalized.ContainsKey("evidence"),
            "General broad search disclosed a collective record's source scope.");
        CompositionAssert.False(
            generalRecords.Any(node =>
                node!.AsObject()["projectLinkId"]?.GetValue<string>() == reservedProjectLinkId),
            "General broad search leaked the reserved project source.");

        var reservedResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["mode"] = CortexMcpContract.BroadSearchMode,
                ["projectLinkId"] = reservedProjectLinkId
            }),
            CancellationToken.None);
        var reservedRecords = Structured(reservedResult)["records"]!.AsArray();
        var reservedRetrieval = Structured(reservedResult)["retrieval"]!.AsObject();
        CompositionAssert.Equal("project-linked", String(reservedRetrieval, "route"));
        CompositionAssert.Equal(1, Integer(reservedRetrieval, "queriedSourceCount"));
        CompositionAssert.Equal(CortexSearchMode.Broad, projectKnowledge.ProjectCalls.Single());
        CompositionAssert.True(
            reservedRecords.Count == 1 &&
            reservedRecords[0]!.AsObject()["projectLinkId"]?.GetValue<string>() == reservedProjectLinkId,
            "Exact reserved project-link search did not remain isolated.");
    }

    public static async Task RuntimeStorageAndFeaturesAreTruthfulAsync()
    {
        var projectLinkId = $"plink_{new string('7', 64)}";
        var localRuntime = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "standalone-local-test",
                "ready",
                IssueCode: null,
                EndpointId: "pipe-test",
                PrincipalName: "current-os-user",
                SubjectId: "os_test",
                CortexMcpRuntimeCapability.Read,
                UsesNetwork: false,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: false,
                "Test-only standalone boundary.",
                StorageKind: CortexMcpStorageKinds.Sqlite));
        var localApplication = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            localRuntime,
            CancellationToken.None);
        var localDiscovery = Structured(await localApplication.CallToolAsync(
            "cortex_discover",
            Arguments(new JsonObject()),
            CancellationToken.None));
        CompositionAssert.Equal(
            CortexMcpStorageKinds.Sqlite,
            String(localDiscovery, "storageKind"));
        var localFeatures = localDiscovery["features"]!.AsObject();
        CompositionAssert.True(Boolean(localFeatures, "defaultLibrarySearch"), "Default search was hidden.");
        CompositionAssert.False(Boolean(localFeatures, "collectiveSearch"), "Standalone collective search was falsely advertised.");
        CompositionAssert.False(Boolean(localFeatures, "projectLinkedSearch"), "Standalone linked search was falsely advertised.");
        var unsupported = await localApplication.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projectLinkId"] = projectLinkId
            }),
            CancellationToken.None);
        AssertToolError(unsupported, "cortex_project_linked_search_unsupported");

        var projectKnowledge = new StaticProjectKnowledgeRuntime(
            projectLinkId,
            $"plink_{new string('6', 64)}");
        var integratedRuntime = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "integrated-test",
                "ready",
                IssueCode: null,
                EndpointId: "endpoint-test",
                PrincipalName: "principal-test",
                SubjectId: "subject-test",
                CortexMcpRuntimeCapability.Read,
                UsesNetwork: true,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: true,
                "Test-only integrated boundary.",
                StorageKind: CortexMcpStorageKinds.PostgreSql),
            ProjectKnowledge: projectKnowledge);
        var integratedApplication = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            integratedRuntime,
            CancellationToken.None);
        var integratedDiscovery = Structured(await integratedApplication.CallToolAsync(
            "cortex_discover",
            Arguments(new JsonObject()),
            CancellationToken.None));
        CompositionAssert.Equal(
            CortexMcpStorageKinds.PostgreSql,
            String(integratedDiscovery, "storageKind"));
        var integratedFeatures = integratedDiscovery["features"]!.AsObject();
        CompositionAssert.True(Boolean(integratedFeatures, "collectiveSearch"), "Collective search was hidden.");
        CompositionAssert.True(Boolean(integratedFeatures, "projectLinkedSearch"), "Linked search was hidden.");
        var rejectedLink = await integratedApplication.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projectLinkId"] = $"plink_{new string('5', 64)}"
            }),
            CancellationToken.None);
        AssertToolError(rejectedLink, "cortex_project_link_rejected");
        projectKnowledge.ProjectProviderFailure = true;
        var failedProvider = await integratedApplication.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projectLinkId"] = projectLinkId
            }),
            CancellationToken.None);
        AssertToolError(
            failedProvider,
            "cortex_project_linked_search_provider_failure");

        var invalidRuntime = localRuntime with
        {
            Descriptor = localRuntime.Descriptor with
            {
                StorageKind = CortexMcpStorageKinds.None
            }
        };
        await CompositionAssert.ThrowsAsync<InvalidOperationException>(async () =>
            await CortexMcpApplication.CreateAsync(
                new RecordingCortexQueryService(),
                LegacyResolverFixture.Create(),
                CompositionSamples.Authority(),
                CompositionSamples.Namespace,
                invalidRuntime,
                CancellationToken.None));
    }

    public static async Task PublicationChangeAsync()
    {
        var targetRecordId = $"krec_{new string('1', 64)}";
        var expectedRevisionId = $"krev_{new string('2', 64)}";
        var replacementRecordId = $"krec_{new string('3', 64)}";
        var replacementRevisionId = $"krev_{new string('4', 64)}";
        var replacementHash = new string('5', 64);
        var appliedRevisionId = $"krev_{new string('6', 64)}";
        var publicationRuntime = new RecordingPublicationChangeRuntime(
            new LearningFeedbackPublicationChangeResult(
                LearningFeedbackMutationDisposition.Accepted,
                $"pchg_{new string('7', 64)}",
                targetRecordId,
                appliedRevisionId,
                2,
                replacementRecordId,
                replacementRevisionId,
                []));
        var runtime = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "publication-change-test",
                "ready",
                IssueCode: null,
                EndpointId: "endpoint-test",
                PrincipalName: "principal-test",
                SubjectId: "subject-test",
                CortexMcpRuntimeCapability.Propose,
                UsesNetwork: true,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: true,
                "Test-only authored-publication maintenance boundary.",
                StorageKind: CortexMcpStorageKinds.PostgreSql),
            PublicationChange: publicationRuntime);
        var application = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            runtime,
            CancellationToken.None);

        var accepted = await application.CallToolAsync(
            "cortex_change_publication",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = ProjectLinkId,
                ["action"] = LearningFeedbackPublicationChangeActions.Supersede,
                ["targetRecordId"] = targetRecordId,
                ["expectedHeadRevisionId"] = expectedRevisionId,
                ["expectedHeadGeneration"] = 1,
                ["replacement"] = new JsonObject
                {
                    ["recordId"] = replacementRecordId,
                    ["revisionId"] = replacementRevisionId,
                    ["contentHash"] = replacementHash
                },
                ["reasonCode"] = LearningFeedbackPublicationChangeReasonCodes.Corrected,
                ["reason"] = "Later evidence corrected the earlier conclusion.",
                ["occurredAtUtc"] = "2026-08-07T18:00:00+00:00"
            }),
            CancellationToken.None);
        AssertToolSuccessEnvelope(accepted);
        var structured = Structured(accepted);
        CompositionAssert.True(Boolean(structured, "accepted"), "Publication change was rejected.");
        CompositionAssert.True(Boolean(structured, "historyPreserved"), "Immutable history was not reported.");
        CompositionAssert.False(Boolean(structured, "grantsAuthority"), "Notebook maintenance granted authority.");
        CompositionAssert.Equal(appliedRevisionId, String(structured, "headRevisionId"));
        CompositionAssert.Equal(1, publicationRuntime.Calls.Count);
        var call = publicationRuntime.Calls.Single();
        CompositionAssert.Equal(ProjectLinkId, call.ProjectLinkId);
        CompositionAssert.Equal(targetRecordId, call.Request.TargetRecordId);
        CompositionAssert.Equal(expectedRevisionId, call.Request.ExpectedHeadRevisionId);
        CompositionAssert.Equal(1L, call.Request.ExpectedHeadGeneration);
        CompositionAssert.NotNull(call.Request.Replacement);
        CompositionAssert.Equal(replacementRecordId, call.Request.Replacement!.RecordId);
        CompositionAssert.Equal(replacementRevisionId, call.Request.Replacement.RevisionId);
        CompositionAssert.Equal(replacementHash, call.Request.Replacement.ContentHash);

        var invalid = await application.CallToolAsync(
            "cortex_change_publication",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = ProjectLinkId,
                ["action"] = LearningFeedbackPublicationChangeActions.Supersede,
                ["targetRecordId"] = targetRecordId,
                ["expectedHeadRevisionId"] = expectedRevisionId,
                ["expectedHeadGeneration"] = 1,
                ["replacement"] = new JsonObject
                {
                    ["recordId"] = replacementRecordId,
                    ["revisionId"] = replacementRevisionId,
                    ["contentHash"] = replacementHash,
                    ["unexpected"] = true
                },
                ["reasonCode"] = LearningFeedbackPublicationChangeReasonCodes.Corrected,
                ["reason"] = "Later evidence corrected the earlier conclusion.",
                ["occurredAtUtc"] = "2026-08-07T18:00:00+00:00"
            }),
            CancellationToken.None);
        AssertToolError(invalid, "cortex_publication_change_arguments_invalid");
        CompositionAssert.Equal(1, publicationRuntime.Calls.Count);
    }

    public static async Task CompactProjectionAsync()
    {
        var service = new RecordingCortexQueryService();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var fullSearchResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject { ["text"] = "dependency lifecycle" }),
            CancellationToken.None);
        var compactSearchResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projection"] = CortexMcpContract.CompactProjection
            }),
            CancellationToken.None);

        var fullSearch = Structured(fullSearchResult);
        var compactSearch = Structured(compactSearchResult);
        CompositionAssert.False(
            fullSearch.ContainsKey("projection"),
            "Default search changed the legacy full-response shape.");
        AssertKeys(compactSearch, "query", "retrieval", "count", "records", "safetyNotice", "projection");
        CompositionAssert.Equal(
            CortexMcpContract.CompactProjection,
            String(compactSearch, "projection"));
        AssertKeys(
            compactSearch["records"]!.AsArray().Single()!.AsObject(),
            "recordId",
            "revisionId",
            "title",
            "summary",
            "score",
            "freshness",
            "contentHash",
            "contentTreatment",
            "executable",
            "matchedTerms");
        CompositionAssert.True(
            compactSearchResult.ToJsonString().Length < fullSearchResult.ToJsonString().Length,
            "Compact search was not smaller than the default full projection.");

        var fullReadResult = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.RecordId }),
            CancellationToken.None);
        var compactReadResult = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject
            {
                ["id"] = CompositionSamples.RecordId,
                ["projection"] = CortexMcpContract.CompactProjection
            }),
            CancellationToken.None);

        var fullRead = Structured(fullReadResult);
        var compactRead = Structured(compactReadResult);
        CompositionAssert.False(
            fullRead.ContainsKey("projection"),
            "Default read changed the legacy full-response shape.");
        AssertKeys(
            compactRead,
            "projection",
            "recordId",
            "revisionId",
            "title",
            "summary",
            "content",
            "contentHash",
            "freshness",
            "contentTreatment",
            "executable",
            "safetyNotice");
        CompositionAssert.Equal(
            CortexMcpContract.CompactProjection,
            String(compactRead, "projection"));
        CompositionAssert.Equal(String(fullRead, "content"), String(compactRead, "content"));
        CompositionAssert.Equal(String(fullRead, "contentHash"), String(compactRead, "contentHash"));
        CompositionAssert.True(
            compactReadResult.ToJsonString().Length < fullReadResult.ToJsonString().Length,
            "Compact read was not smaller than the default full projection.");
        CompositionAssert.Equal(2, service.SearchQueries.Count);
        CompositionAssert.Equal(service.SearchQueries[0], service.SearchQueries[1]);
        CompositionAssert.Equal(2, service.ReadQueries.Count);
        CompositionAssert.Equal(service.ReadQueries[0], service.ReadQueries[1]);
    }

    public static async Task CompactSearchHydrationAsync()
    {
        var service = new RecordingCortexQueryService();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var compactResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projection"] = CortexMcpContract.CompactProjection,
                ["limit"] = CortexMcpContract.MaximumHydratedSearchResults
            }),
            CancellationToken.None);
        var hydratedResult = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projection"] = CortexMcpContract.CompactProjection,
                ["includeContent"] = true,
                ["limit"] = CortexMcpContract.MaximumHydratedSearchResults
            }),
            CancellationToken.None);
        var readResult = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.RecordId }),
            CancellationToken.None);

        AssertToolSuccessEnvelope(compactResult);
        var compact = Structured(compactResult);
        CompositionAssert.False(
            compact.ContainsKey("contentIncluded"),
            "Compact search changed when hydration was omitted.");
        CompositionAssert.False(
            compact["records"]!.AsArray().Single()!.AsObject().ContainsKey("content"),
            "Compact search included exact content without opt-in.");
        AssertToolSuccessEnvelope(hydratedResult);
        var hydrated = Structured(hydratedResult);
        AssertKeys(
            hydrated,
            "query",
            "retrieval",
            "count",
            "records",
            "safetyNotice",
            "projection",
            "contentIncluded");
        CompositionAssert.True(Boolean(hydrated, "contentIncluded"), "Hydrated search did not confirm content inclusion.");
        var hydratedRecord = hydrated["records"]!.AsArray().Single()!.AsObject();
        AssertKeys(
            hydratedRecord,
            "recordId",
            "revisionId",
            "title",
            "summary",
            "score",
            "freshness",
            "contentHash",
            "contentTreatment",
            "executable",
            "content",
            "matchedTerms");
        var read = Structured(readResult);
        CompositionAssert.Equal(String(read, "content"), String(hydratedRecord, "content"));
        CompositionAssert.Equal(String(read, "contentHash"), String(hydratedRecord, "contentHash"));
        CompositionAssert.Equal(2, service.SearchQueries.Count);
        CompositionAssert.Equal(service.SearchQueries[0], service.SearchQueries[1]);
        CompositionAssert.Equal(
            CortexMcpContract.MaximumHydratedSearchResults,
            service.SearchQueries[1].Limit);

        var queriesBeforeInvalidCalls = service.SearchQueries.Count;
        var defaultProjection = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["includeContent"] = true
            }),
            CancellationToken.None);
        var excessiveLimit = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "dependency lifecycle",
                ["projection"] = CortexMcpContract.CompactProjection,
                ["includeContent"] = true,
                ["limit"] = CortexMcpContract.MaximumHydratedSearchResults + 1
            }),
            CancellationToken.None);
        AssertToolError(defaultProjection, "cortex_search_arguments_invalid");
        AssertToolError(excessiveLimit, "cortex_search_arguments_invalid");
        CompositionAssert.Equal(queriesBeforeInvalidCalls, service.SearchQueries.Count);

        var firstLargeRecord = CompositionSamples.Record(content: new string('a', 40 * 1024));
        var secondLargeRecord = CompositionSamples.Record(
            CompositionSamples.SecondRecordId,
            CompositionSamples.SecondRevisionId,
            new string('3', 64),
            content: new string('b', 40 * 1024));
        service.SearchResult = Accepted(new CortexKnowledgeSearchPage(
            new CortexSearchQuery(CompositionSamples.Namespace, "aggregate"),
            [
                new CortexKnowledgeSearchHit(firstLargeRecord, 2, CortexQueryPolicy.SafetyNotice),
                new CortexKnowledgeSearchHit(secondLargeRecord, 1, CortexQueryPolicy.SafetyNotice)
            ],
            CortexQueryPolicy.SafetyNotice));
        var aggregateTooLarge = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "aggregate",
                ["projection"] = CortexMcpContract.CompactProjection,
                ["includeContent"] = true,
                ["limit"] = 2
            }),
            CancellationToken.None);
        AssertToolError(aggregateTooLarge, "cortex_search_hydration_too_large");

        // Raw content can fit the aggregate byte cap while JSON escaping causes
        // the duplicated MCP text/structured envelope to exceed its exact cap.
        var oversizedRecord = CompositionSamples.Record(content: new string('\0', 64 * 1024));
        service.SearchResult = Accepted(new CortexKnowledgeSearchPage(
            new CortexSearchQuery(CompositionSamples.Namespace, "oversized"),
            [new CortexKnowledgeSearchHit(oversizedRecord, 1, CortexQueryPolicy.SafetyNotice)],
            CortexQueryPolicy.SafetyNotice));
        var oversized = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "oversized",
                ["projection"] = CortexMcpContract.CompactProjection,
                ["includeContent"] = true,
                ["limit"] = 1
            }),
            CancellationToken.None);
        AssertToolError(oversized, "cortex_search_hydration_too_large");
    }

    public static async Task LegacyIdentifierResolutionAsync()
    {
        var service = new RecordingCortexQueryService();
        var resolver = LegacyResolverFixture.Create();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            resolver,
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var byRecord = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.RecordId }),
            CancellationToken.None);
        var byRevision = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.RevisionId }),
            CancellationToken.None);
        var unknown = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.UnknownRecordId }),
            CancellationToken.None);

        CompositionAssert.False(Boolean(byRecord, "isError"), "Record alias read failed.");
        CompositionAssert.False(Boolean(byRevision, "isError"), "Revision alias read failed.");
        CompositionAssert.Equal(2, service.ReadQueries.Count);
        CompositionAssert.Equal(service.ReadQueries[0], service.ReadQueries[1]);
        CompositionAssert.Equal(3, resolver.Identifiers.Count);
        CompositionAssert.Equal(CompositionSamples.RecordId, resolver.Identifiers[0]);
        CompositionAssert.Equal(CompositionSamples.RevisionId, resolver.Identifiers[1]);
        CompositionAssert.Equal(CompositionSamples.UnknownRecordId, resolver.Identifiers[2]);
        AssertToolError(unknown, "cortex_record_not_available");
    }

    public static async Task TaxonomyDiagnosticCompatibilityAsync()
    {
        var service = new RecordingCortexQueryService
        {
            SearchResult = new CortexQueryResult<CortexKnowledgeSearchPage>(
                false,
                null,
                [
                    new PegasusDiagnostic(
                        "cortex_search_taxonomy_unknown",
                        "Core-owned message that must not replace the legacy wire message.",
                        DiagnosticSeverity.Error,
                        "cortex.query.search")
                ])
        };
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var result = await application.CallToolAsync(
            "cortex_search",
            Arguments(new JsonObject
            {
                ["text"] = "bounded search",
                ["taxonomy"] = "missing-taxonomy"
            }),
            CancellationToken.None);

        AssertToolError(result, "cortex_search_taxonomy_unknown");
        var diagnostic = Structured(result)["diagnostic"]!.AsObject();
        CompositionAssert.Equal(
            "The requested taxonomy is not in the verified Default Library.",
            String(diagnostic, "message"));
        CompositionAssert.Equal("cortex.search", String(diagnostic, "operation"));
        CompositionAssert.Equal(1, service.SearchQueries.Count);
    }

    public static async Task StructuredProvenanceFailsClosedAsync()
    {
        var record = CompositionSamples.Record(
            sourceRevisions:
            [
                new CortexKnowledgeSourceRevision(
                    CompositionSamples.Namespace,
                    CompositionSamples.SecondRecordId,
                    CompositionSamples.SecondRevisionId,
                    new string('3', 64))
            ]);
        var service = new RecordingCortexQueryService
        {
            ReadResult = Accepted(record)
        };
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var toolRead = await application.CallToolAsync(
            "cortex_read",
            Arguments(new JsonObject { ["id"] = CompositionSamples.RecordId }),
            CancellationToken.None);
        AssertToolError(toolRead, "cortex_record_not_available");
        CompositionAssert.False(
            toolRead.ToJsonString().Contains(CompositionSamples.SecondRecordId, StringComparison.Ordinal),
            "Structured provenance leaked into the legacy tool response.");

        var resourceRead = await application.ReadResourceAsync(
            $"cortex://default-library/records/{CompositionSamples.RecordId}",
            CancellationToken.None);
        CompositionAssert.True(resourceRead is null, "Structured provenance was projected through a record resource.");
        CompositionAssert.Equal(2, service.ReadQueries.Count);
    }

    public static async Task ResourceResolverRedirectFailsClosedAsync()
    {
        var service = new RecordingCortexQueryService();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            LegacyResolverFixture.CreateResourceRedirect(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        var result = await application.ReadResourceAsync(
            $"cortex://default-library/records/{CompositionSamples.RecordId}",
            CancellationToken.None);

        CompositionAssert.True(result is null, "A stable resource URI followed a redirected revision identity.");
        CompositionAssert.Equal(0, service.ReadQueries.Count);
    }

    public static async Task CancellationPropagationAsync()
    {
        using (var startupCancellation = new CancellationTokenSource())
        {
            startupCancellation.Cancel();
            await CompositionAssert.ThrowsAsync<OperationCanceledException>(() =>
                CortexMcpApplication.CreateAsync(
                    new RecordingCortexQueryService(),
                    LegacyResolverFixture.Create(),
                    CompositionSamples.Authority(),
                    CompositionSamples.Namespace,
                    startupCancellation.Token).AsTask());
        }

        var service = new RecordingCortexQueryService();
        var resolver = LegacyResolverFixture.Create();
        var application = await CortexMcpApplication.CreateAsync(
            service,
            resolver,
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            CancellationToken.None);

        service.SearchException = new OperationCanceledException("query service cancellation");
        await CompositionAssert.ThrowsAsync<OperationCanceledException>(() =>
            application.CallToolAsync(
                "cortex_search",
                Arguments(new JsonObject { ["text"] = "bounded" }),
                CancellationToken.None).AsTask());

        resolver.Exception = new OperationCanceledException("resolver cancellation");
        await CompositionAssert.ThrowsAsync<OperationCanceledException>(() =>
            application.CallToolAsync(
                "cortex_read",
                Arguments(new JsonObject { ["id"] = CompositionSamples.RecordId }),
                CancellationToken.None).AsTask());

        using var callCancellation = new CancellationTokenSource();
        callCancellation.Cancel();
        await CompositionAssert.ThrowsAsync<OperationCanceledException>(() =>
            application.ListResourcesAsync(callCancellation.Token).AsTask());
        await CompositionAssert.ThrowsAsync<OperationCanceledException>(() =>
            application.ListResourceTemplatesAsync(callCancellation.Token).AsTask());
    }

    public static async Task ProjectBriefRuntimeAndBudgetAsync()
    {
        var projectLinkId = $"plink_{new string('a', 64)}";
        var runtime = new StaticProjectBriefRuntime(ProjectBriefResult(oversized: false));
        var application = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            runtime,
            CancellationToken.None);

        var tool = application.ListTools()
            .Select(node => node!.AsObject())
            .Single(candidate =>
                string.Equals(String(candidate, "name"), "cortex_project_brief", StringComparison.Ordinal));
        CompositionAssert.True(
            Boolean(tool["annotations"]!.AsObject(), "readOnlyHint"),
            "The provider-neutral project brief was not read-only.");
        var discovery = Structured(await application.CallToolAsync(
            "cortex_discover",
            Arguments(new JsonObject()),
            CancellationToken.None));
        var access = discovery["access"]!.AsObject();
        CompositionAssert.True(
            Boolean(access, "projectContinuityRuntime"),
            "Discovery did not expose the configured project-brief runtime.");
        CompositionAssert.False(
            Boolean(access, "network"),
            "The provider-neutral project-brief seam implied network access.");

        var missingAuthority = await application.CallToolAsync(
            "cortex_project_brief",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = projectLinkId
            }),
            CancellationToken.None);
        CompositionAssert.True(
            Boolean(missingAuthority, "isError"),
            "A hashless first snapshot was presented as a successful tool call.");
        var missingBrief = Structured(missingAuthority);
        CompositionAssert.False(
            Boolean(missingBrief, "accepted"),
            "A hashless first snapshot remained accepted.");
        CompositionAssert.True(
            Boolean(missingBrief, "fallbackRequired"),
            "A hashless first snapshot did not require file fallback.");
        CompositionAssert.Equal(
            "authority_mismatch",
            String(missingBrief, "fallbackReason"));
        CompositionAssert.True(
            missingBrief["projectLinkId"] is null &&
            missingBrief["projectId"] is null &&
            missingBrief["projectNamespace"] is null &&
            missingBrief["contextSetId"] is null &&
            missingBrief["sourceHash"] is null &&
            missingBrief["authoritySetHash"] is null &&
            missingBrief["currentTask"] is null,
            "A hashless first snapshot exposed project, task, or source claims.");
        CompositionAssert.Equal(0, runtime.Calls.Count);

        var result = await application.CallToolAsync(
            "cortex_project_brief",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = projectLinkId,
                ["knownAuthoritySetHash"] = new string('4', 64),
                ["maxUtf8Bytes"] = CortexProjectBriefContract.MaximumUtf8Bytes
            }),
            CancellationToken.None);
        AssertToolSuccessEnvelope(result);
        var brief = Structured(result);
        CompositionAssert.Equal(CortexProjectBriefContract.ContractId, String(brief, "contractId"));
        CompositionAssert.Equal("snapshot", String(brief, "disposition"));
        CompositionAssert.Equal("selection-v3", String(brief, "selectionVersion"));
        CompositionAssert.Equal(new string('4', 64), String(brief, "authoritySetHash"));
        CompositionAssert.Equal(1, brief["hotTasks"]!.AsArray().Count);
        CompositionAssert.True(Boolean(brief, "hasMore"), "The brief lost its hasMore marker.");
        CompositionAssert.False(Boolean(brief, "fallbackRequired"), "A complete brief requested fallback.");
        CompositionAssert.True(
            Encoding.UTF8.GetByteCount(brief.ToJsonString(McpProtocol.JsonOptions)) <=
                CortexProjectBriefContract.MaximumUtf8Bytes,
            "The default project brief exceeded the 8 KiB hard ceiling.");
        CompositionAssert.Equal(1, runtime.Calls.Count);
        CompositionAssert.Equal(projectLinkId, runtime.Calls[0].ProjectLinkId);
        CompositionAssert.Equal<string?>(null, runtime.Calls[0].KnownContextSetId);
        CompositionAssert.Equal(new string('4', 64), runtime.Calls[0].KnownAuthoritySetHash);
        CompositionAssert.Equal(
            CortexProjectBriefContract.MaximumUtf8Bytes,
            runtime.Calls[0].MaximumUtf8Bytes);

        runtime.Result = ProjectBriefResult(oversized: true);
        var bounded = await application.CallToolAsync(
            "cortex_project_brief",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = projectLinkId,
                ["knownContextSetId"] = $"ctx_{new string('9', 64)}",
                ["knownAuthoritySetHash"] = new string('4', 64),
                ["maxUtf8Bytes"] = CortexProjectBriefContract.MinimumMaximumUtf8Bytes
            }),
            CancellationToken.None);
        CompositionAssert.True(
            Boolean(bounded, "isError"),
            "A truncated project brief was presented as a successful tool result.");
        var boundedBrief = Structured(bounded);
        CompositionAssert.False(
            Boolean(boundedBrief, "accepted"),
            "A truncated project brief remained accepted.");
        CompositionAssert.True(Boolean(boundedBrief, "truncated"), "Oversized brief was not marked truncated.");
        CompositionAssert.True(
            Boolean(boundedBrief, "fallbackRequired"),
            "Truncation did not require authoritative fallback.");
        CompositionAssert.Equal("response_truncated", String(boundedBrief, "fallbackReason"));
        CompositionAssert.Equal(
            "cortex_project_brief_response_truncated",
            String(boundedBrief["diagnostic"]!.AsObject(), "code"));
        CompositionAssert.True(
            Encoding.UTF8.GetByteCount(boundedBrief.ToJsonString(McpProtocol.JsonOptions)) <=
                CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
            "The projected brief exceeded the caller's UTF-8 budget.");
        CompositionAssert.Equal(2, runtime.Calls.Count);
        CompositionAssert.Equal($"ctx_{new string('9', 64)}", runtime.Calls[1].KnownContextSetId);
        CompositionAssert.Equal(new string('4', 64), runtime.Calls[1].KnownAuthoritySetHash);

        var rejectedResponse = ProjectBriefResult(oversized: false).Response with
        {
            CurrentTask = null,
            HotTasks = [],
            Complete = false,
            FallbackRequired = true,
            FallbackReason = "source_failed"
        };
        runtime.Result = new CortexProjectBriefResult(
            false,
            rejectedResponse,
            [
                new PegasusDiagnostic(
                    "provider_supplied_diagnostic",
                    new string('d', 4096),
                    DiagnosticSeverity.Error,
                    "cortex.project-brief.read")
            ]);
        var rejected = await application.CallToolAsync(
            "cortex_project_brief",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = projectLinkId,
                ["knownAuthoritySetHash"] = new string('4', 64),
                ["maxUtf8Bytes"] = CortexProjectBriefContract.MinimumMaximumUtf8Bytes
            }),
            CancellationToken.None);
        CompositionAssert.True(
            Boolean(rejected, "isError"),
            "A rejected project brief was not returned as a tool failure.");
        var rejectedBrief = Structured(rejected);
        CompositionAssert.False(
            Boolean(rejectedBrief, "accepted"),
            "A rejected project brief was marked accepted.");
        CompositionAssert.Equal(
            "cortex_project_brief_response_truncated",
            String(rejectedBrief["diagnostic"]!.AsObject(), "code"));
        CompositionAssert.True(
            Encoding.UTF8.GetByteCount(rejectedBrief.ToJsonString(McpProtocol.JsonOptions)) <=
                CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
            "A provider diagnostic escaped the project-brief byte ceiling.");
    }

    public static async Task RetainedProjectBriefIdentityFailsClosedAsync()
    {
        const string tenantId = "tenant-local";
        const string requestedProjectId = "project-authorized";
        const string forgedProjectId = "project-forged";
        const string projectNamespace = "project.authorized";
        var projectLinkId = $"plink_{new string('a', 64)}";
        var freshness = new CortexProjectBriefFreshness(
            "current",
            DateTimeOffset.Parse("2026-07-30T11:55:00Z"),
            DateTimeOffset.Parse("2026-07-30T13:00:00Z"),
            "Exact retained project-brief identity fixture.");
        var template = new CortexProjectBriefSnapshot(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            tenantId,
            projectLinkId,
            forgedProjectId,
            projectNamespace,
            $"ctx_{new string('0', 64)}",
            PreviousContextSetId: null,
            BriefRevision: 1,
            StateRevision: 1,
            SourceHash: new string('7', 64),
            SelectionVersion: "selection-retained-identity",
            BriefTask(
                "task-retained-identity",
                selectionRank: 0,
                "Verify exact retained project identity."),
            HotTasks: [],
            HasMore: false,
            CheckpointReferences: [],
            EvidenceReferences: [],
            Complete: true,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            freshness,
            AuthoritySetHash: new string('6', 64));
        var forgedBrief = template with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(template)
        };
        var canonical = CortexCanonicalJson.Serialize(forgedBrief);
        var contentHash = CortexCanonicalJson.Sha256(canonical);
        var reader = new StaticRetainedAssertionReader(
            new CortexRetainedAssertionRecord(
                tenantId,
                projectLinkId,
                requestedProjectId,
                projectNamespace,
                CortexRetainedDocumentKinds.ProjectBrief,
                projectNamespace,
                Revision: 1,
                forgedBrief.ContextSetId,
                contentHash,
                contentHash,
                CortexProjectBriefContract.ContractId,
                canonical));
        var runtime = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "retained-identity-test",
                "ready",
                IssueCode: null,
                EndpointId: null,
                PrincipalName: null,
                SubjectId: null,
                CortexMcpRuntimeCapability.RetainedRead,
                UsesNetwork: false,
                UsesDatabase: false,
                UsesAuthentication: false,
                UsesLiveServices: false,
                "Test-only exact retained-record identity boundary."),
            RetainedAssertionReader: reader);
        var application = await CortexMcpApplication.CreateAsync(
            new RecordingCortexQueryService(),
            LegacyResolverFixture.Create(),
            CompositionSamples.Authority(),
            CompositionSamples.Namespace,
            runtime,
            CancellationToken.None);

        var result = await application.CallToolAsync(
            "cortex_read_retained_record",
            Arguments(new JsonObject
            {
                ["projectLinkId"] = projectLinkId,
                ["projectId"] = requestedProjectId,
                ["projectNamespace"] = projectNamespace,
                ["kind"] = CortexRetainedDocumentKinds.ProjectBrief,
                ["documentId"] = projectNamespace,
                ["revision"] = 1,
                ["contentHash"] = contentHash
            }),
            CancellationToken.None);

        AssertToolError(result, "cortex_retained_record_integrity_invalid");
    }

    public static Task ProviderNeutralDependencyClosureAsync()
    {
        var forbiddenAssemblies = new[]
        {
            "Npgsql",
            "PegasusCortex.Persistence.PostgreSql",
            "PegasusCortex.Service"
        };
        var references = typeof(CortexMcpApplication).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var assemblyName in forbiddenAssemblies)
        {
            CompositionAssert.False(
                references.Contains(assemblyName),
                $"Provider-neutral MCP directly references {assemblyName}.");
            CompositionAssert.False(
                File.Exists(Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll")),
                $"Provider-neutral MCP test output copied {assemblyName}.dll.");
        }

        var dependencyManifest = Path.ChangeExtension(
            // dotnet <test.dll> uses the dotnet host as ProcessPath. Inspect the
            // actual test assembly closure for both DLL and apphost invocation.
            typeof(CortexMcpCompositionTests).Assembly.Location,
            ".deps.json");
        CompositionAssert.True(
            File.Exists(dependencyManifest),
            "The MCP test dependency manifest is unavailable.");
        var manifest = File.ReadAllText(dependencyManifest);
        foreach (var assemblyName in forbiddenAssemblies)
        {
            CompositionAssert.False(
                manifest.Contains(assemblyName, StringComparison.Ordinal),
                $"Provider-neutral MCP dependency closure retained {assemblyName}.");
        }

        return Task.CompletedTask;
    }

    public static async Task ProviderNeutralExecutableIgnoresIntegratedEnvironmentAsync()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "PegasusCortex.Mcp.exe"
                : "PegasusCortex.Mcp");
        CompositionAssert.True(
            File.Exists(executable),
            "The provider-neutral MCP executable is unavailable in the test closure.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["PEGASUS_CORTEX_AUTHORITY_FILE"] = "ignored-authority";
        start.Environment["PEGASUS_CORTEX_ROW_KEY_FILE"] = "ignored-key";
        start.Environment["PEGASUS_CORTEX_RUNTIME_CONNECTION"] = "ignored-runtime";
        start.Environment["PEGASUS_CORTEX_MIGRATION_CONNECTION"] = "ignored-migration";

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The provider-neutral MCP process did not start.");
        var requests = string.Join(
            '\n',
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = McpProtocol.Version,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "dependency-closure-test",
                        ["version"] = "1.0"
                    }
                }
            }.ToJsonString(),
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized"
            }.ToJsonString(),
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list"
            }.ToJsonString()) + '\n';
        await process.StandardInput.WriteAsync(requests).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        CompositionAssert.Equal(0, process.ExitCode);
        CompositionAssert.Equal(string.Empty, await standardError.ConfigureAwait(false));

        var responses = (await standardOutput.ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)?.AsObject() ??
                throw new InvalidOperationException("The local MCP emitted non-JSON output."))
            .ToArray();
        CompositionAssert.Equal(2, responses.Length);
        var tools = responses[1]["result"]!["tools"]!.AsArray();
        CompositionAssert.Equal(4, tools.Count);
        CompositionAssert.False(
            tools.Any(node =>
                string.Equals(
                    node!["name"]!.GetValue<string>(),
                    "cortex_correlate_project",
                    StringComparison.Ordinal)),
            "The local MCP environment-activated integrated tools.");
    }

    public static async Task DefaultLibraryScopeOperationDefenseAsync()
    {
        var source = DefaultLibraryQuerySource.CreateVerified();
        var provenanceReadScope = new CortexQuerySourceScope(
            "tenant-1",
            "mcp-test-authority",
            "subject-1",
            "pegasus-cortex-mcp",
            11,
            DefaultKnowledgeLibrary.DefaultNamespace,
            CortexQueryCapability.Read,
            "cortex.query.provenance.read",
            "internal",
            IncludeStale: false,
            IncludeBlockedOrUnverified: false);
        var snapshot = await source.GetSnapshotAsync(
            provenanceReadScope,
            CancellationToken.None);
        CompositionAssert.Equal(DefaultKnowledgeLibrary.DefaultNamespace, snapshot.DefaultNamespace);

        var mismatchedScope = new CortexQuerySourceScope(
            "tenant-1",
            "mcp-test-authority",
            "subject-1",
            "pegasus-cortex-mcp",
            11,
            DefaultKnowledgeLibrary.DefaultNamespace,
            CortexQueryCapability.Search,
            "cortex.query.status",
            "internal",
            IncludeStale: false,
            IncludeBlockedOrUnverified: false);

        await CompositionAssert.ThrowsAsync<InvalidOperationException>(() =>
            source.GetSnapshotAsync(mismatchedScope, CancellationToken.None).AsTask());
    }

    private static CortexQueryResult<T> Accepted<T>(T value) => new(true, value, []);

    private static CortexProjectBriefResult ProjectBriefResult(bool oversized)
    {
        var text = oversized
            ? new string('x', CortexProjectBriefContract.MaximumTextCharacters)
            : "Continue the bounded project-brief read lane.";
        var taskCount = oversized ? CortexProjectBriefContract.MaximumHotTasks : 1;
        var hotTasks = Enumerable.Range(1, taskCount)
            .Select(index => BriefTask($"task-hot-{index}", index, text))
            .ToArray();
        var response = new CortexProjectBriefResponse(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            CortexProjectBriefDisposition.Snapshot,
            ProjectLinkId,
            ProjectId,
            ProjectNamespace,
            $"ctx_{new string('8', 64)}",
            BaseContextSetId: null,
            StateRevision: 3,
            SourceHash: new string('7', 64),
            SelectionVersion: "selection-v3",
            BriefTask("task-current", 0, text),
            hotTasks,
            RemovedTaskIds: [],
            HasMore: true,
            CheckpointReferences:
            [
                new CortexProjectBriefReference(
                    "checkpoint://project/checkpoint-3",
                    new string('6', 64),
                    RecordId: null,
                    RevisionId: null)
            ],
            EvidenceReferences:
            [
                new CortexProjectBriefReference(
                    "artifact://project/evidence-3",
                    new string('5', 64),
                    RecordId: null,
                    RevisionId: null)
            ],
            new CortexProjectBriefFreshness(
                "current",
                DateTimeOffset.Parse("2026-07-30T11:55:00Z"),
                DateTimeOffset.Parse("2026-07-30T13:00:00Z"),
                text),
            Complete: true,
            Truncated: false,
            FallbackRequired: false,
            FallbackReason: null,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            AuthoritySetHash: new string('4', 64));
        return new CortexProjectBriefResult(true, response, []);
    }

    private static CortexProjectBriefTask BriefTask(
        string taskId,
        int selectionRank,
        string text) =>
        new(
            taskId,
            selectionRank,
            selectionRank == 0 ? "Current project brief" : $"Hot project task {selectionRank}",
            selectionRank == 0 ? "active" : "planned",
            text,
            text,
            new CortexProjectBriefGate(
                "not_required",
                AuthorityId: null,
                ApprovalReferenceId: null,
                "No external approval is required for this reported task.",
                GrantsAuthority: false),
            Blocker: null,
            new CortexProjectBriefTaskSource(
                "pegasus.agent-task-snapshot/1.0",
                $"source-{taskId}",
                $"revision-{taskId}-3",
                new string((char)('0' + Math.Min(selectionRank + 1, 9)), 64),
                $"artifact://tasks/{taskId}"),
            new CortexProjectBriefFreshness(
                "current",
                DateTimeOffset.Parse("2026-07-30T11:55:00Z"),
                DateTimeOffset.Parse("2026-07-30T13:00:00Z"),
                text));

    private static JsonElement Arguments(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Structured(JsonObject result) =>
        result["structuredContent"]?.AsObject() ??
        throw new InvalidOperationException("Tool result has no structured content.");

    private static void AssertToolSuccessEnvelope(JsonObject result)
    {
        AssertKeys(result, "content", "structuredContent", "isError");
        CompositionAssert.False(Boolean(result, "isError"), "Tool result was unexpectedly marked as an error.");
        var content = result["content"]!.AsArray().Single()!.AsObject();
        AssertKeys(content, "type", "text");
        CompositionAssert.Equal("text", String(content, "type"));
        CompositionAssert.True(JsonNode.DeepEquals(Structured(result), JsonNode.Parse(String(content, "text"))),
            "Text JSON and structured content differ after parsing.");
    }

    private static void AssertToolError(JsonObject result, string expectedCode)
    {
        AssertKeys(result, "content", "structuredContent", "isError");
        CompositionAssert.True(Boolean(result, "isError"), "Tool failure was not marked as an error.");
        var structured = Structured(result);
        AssertKeys(structured, "accepted", "diagnostic");
        CompositionAssert.False(Boolean(structured, "accepted"), "Rejected tool content was marked accepted.");
        var diagnostic = structured["diagnostic"]!.AsObject();
        AssertKeys(diagnostic, "code", "message", "severity", "operation", "retryable");
        CompositionAssert.Equal(expectedCode, String(diagnostic, "code"));
        CompositionAssert.Equal("error", String(diagnostic, "severity"));
        CompositionAssert.False(Boolean(diagnostic, "retryable"), "Compatibility error was marked retryable.");
        var content = result["content"]!.AsArray().Single()!.AsObject();
        AssertKeys(content, "type", "text");
        CompositionAssert.True(JsonNode.DeepEquals(structured, JsonNode.Parse(String(content, "text"))),
            "Error text JSON and structured content differ after parsing.");
    }

    private static void AssertAuthority(CortexQueryAuthority expected, CortexQueryAuthority actual)
    {
        CompositionAssert.Equal(expected.TenantId, actual.TenantId);
        CompositionAssert.Equal(expected.AuthorityId, actual.AuthorityId);
        CompositionAssert.Equal(expected.SubjectId, actual.SubjectId);
        CompositionAssert.Equal(expected.ServiceId, actual.ServiceId);
        CompositionAssert.Equal(expected.PolicyRevision, actual.PolicyRevision);
        CompositionAssert.Equal(expected.Grants.Count, actual.Grants.Count);
        for (var index = 0; index < expected.Grants.Count; index++)
        {
            var expectedGrant = expected.Grants[index];
            var actualGrant = actual.Grants[index];
            CompositionAssert.Equal(expectedGrant.Namespace, actualGrant.Namespace);
            CompositionAssert.Equal(expectedGrant.MaximumSensitivity, actualGrant.MaximumSensitivity);
            CompositionAssert.Equal(expectedGrant.AllowStale, actualGrant.AllowStale);
            CompositionAssert.Equal(
                expectedGrant.AllowBlockedOrUnverified,
                actualGrant.AllowBlockedOrUnverified);
            CompositionAssert.True(
                expectedGrant.Capabilities.SetEquals(actualGrant.Capabilities),
                "Query authority capabilities changed during MCP delegation.");
        }
    }

    private static void AssertKeys(JsonObject value, params string[] expected)
    {
        var actual = value.Select(property => property.Key).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected JSON fields [{string.Join(",", expected)}], received [{string.Join(",", actual)}].");
        }
    }

    private static string String(JsonObject value, string propertyName) =>
        value[propertyName]?.GetValue<string>() ??
        throw new InvalidOperationException($"Missing string property {propertyName}.");

    private static int Integer(JsonObject value, string propertyName) =>
        value[propertyName]?.GetValue<int>() ??
        throw new InvalidOperationException($"Missing integer property {propertyName}.");

    private static bool Boolean(JsonObject value, string propertyName) =>
        value[propertyName]?.GetValue<bool>() ??
        throw new InvalidOperationException($"Missing boolean property {propertyName}.");
}

internal sealed class StaticRetainedAssertionReader(
    CortexRetainedAssertionRecord result) : ICortexRetainedAssertionReaderRuntime
{
    public ValueTask<CortexRetainedAssertionRecord?> ReadRetainedAssertionAsync(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CortexRetainedAssertionRecord?>(result);
    }
}

internal sealed class RecordingPublicationChangeRuntime(
    LearningFeedbackPublicationChangeResult result) : ICortexPublicationChangeRuntime
{
    public List<PublicationChangeRuntimeCall> Calls { get; } = [];

    public ValueTask<LearningFeedbackPublicationChangeResult?> ChangePublicationAsync(
        string projectLinkId,
        LearningFeedbackPublicationChangeRequestV1 request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new PublicationChangeRuntimeCall(projectLinkId, request));
        return ValueTask.FromResult<LearningFeedbackPublicationChangeResult?>(result);
    }
}

internal sealed record PublicationChangeRuntimeCall(
    string ProjectLinkId,
    LearningFeedbackPublicationChangeRequestV1 Request);

internal sealed class StaticProjectBriefRuntime(
    CortexProjectBriefResult initialResult) : ICortexProjectBriefRuntime
{
    public CortexProjectBriefResult Result { get; set; } = initialResult;
    public List<ProjectBriefRuntimeCall> Calls { get; } = [];

    public ValueTask<CortexProjectBriefResult?> ReadProjectBriefAsync(
        string projectLinkId,
        string? knownContextSetId,
        string knownAuthoritySetHash,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new ProjectBriefRuntimeCall(
            projectLinkId,
            knownContextSetId,
            knownAuthoritySetHash,
            maximumUtf8Bytes));
        return ValueTask.FromResult<CortexProjectBriefResult?>(Result);
    }
}

internal sealed class StaticProjectKnowledgeRuntime(
    string reservedProjectLinkId,
    string collectiveProjectLinkId) : ICortexProjectKnowledgeRuntime
{
    public List<CortexSearchMode> ProjectCalls { get; } = [];
    public List<CortexSearchMode> CollectiveCalls { get; } = [];
    public bool ProjectProviderFailure { get; set; }

    public ValueTask<CortexProjectSearchResult?> SearchProjectAsync(
        string projectLinkId,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectCalls.Add(mode);
        if (!string.Equals(projectLinkId, reservedProjectLinkId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<CortexProjectSearchResult?>(
                new CortexProjectSearchResult(
                    CortexProjectSearchResult.LinkRejected,
                    Page: null));
        }
        if (ProjectProviderFailure)
        {
            return ValueTask.FromResult<CortexProjectSearchResult?>(
                new CortexProjectSearchResult(
                    CortexProjectSearchResult.ProviderFailure,
                    Page: null));
        }
        var record = CompositionSamples.Record(
            $"krec_{new string('9', 64)}",
            $"krev_{new string('9', 64)}",
            new string('9', 64));
        return ValueTask.FromResult<CortexProjectSearchResult?>(
            new CortexProjectSearchResult(
                CortexProjectSearchResult.Success,
                new CortexKnowledgeSearchPage(
                    new CortexSearchQuery(record.Namespace, text, Limit: limit, Mode: mode),
                    [new CortexKnowledgeSearchHit(record, 0.1, CortexQueryPolicy.SafetyNotice)],
                    CortexQueryPolicy.SafetyNotice)));
    }

    public ValueTask<CortexCollectiveSearchResult?> SearchCollectiveAsync(
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        int limit,
        CortexSearchMode mode,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CollectiveCalls.Add(mode);
        var record = CompositionSamples.Record(
            $"krec_{new string('8', 64)}",
            $"krev_{new string('8', 64)}",
            new string('8', 64));
        IReadOnlyList<CortexCollectiveSearchHit> hits =
        [
            new CortexCollectiveSearchHit(
                new CortexKnowledgeSearchHit(record, 0.2, CortexQueryPolicy.SafetyNotice),
                collectiveProjectLinkId)
        ];
        return ValueTask.FromResult<CortexCollectiveSearchResult?>(
            new CortexCollectiveSearchResult(
                hits,
                EligibleSourceCount: 1,
                QueriedSourceCount: 1,
                PartialFailureCount: 0));
    }

    public ValueTask<CortexKnowledgeRecord?> ReadProjectAsync(
        string projectLinkId,
        string recordId,
        string revisionId,
        bool includeStale,
        bool includeBlockedOrUnverified,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CortexKnowledgeRecord?>(null);
    }
}

internal sealed record ProjectBriefRuntimeCall(
    string ProjectLinkId,
    string? KnownContextSetId,
    string KnownAuthoritySetHash,
    int MaximumUtf8Bytes);

internal sealed class RecordingCortexQueryService : ICortexQueryService
{
    public RecordingCortexQueryService()
    {
        var record = CompositionSamples.Record();
        CatalogRecords.Add(CompositionSamples.Summary(record));
        CatalogRecords.Add(CompositionSamples.Summary(CompositionSamples.Record(
            CompositionSamples.SecondRecordId,
            CompositionSamples.SecondRevisionId,
            new string('3', 64))));
        DiscoveryResult = Accepted(new CortexQueryDiscovery(
            "isolated-read-only",
            CompositionSamples.Namespace,
            2,
            ["testing"],
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice));
        StatusResult = Accepted(new CortexQueryStatus(
            "ready",
            "isolated-read-only",
            "verified-at-startup",
            "1.2026.07.14.10.00.A",
            "1.20260714.1000-a",
            "fixture-collection",
            new string('5', 64),
            CompositionSamples.Namespace,
            RecordCount: 3,
            DefaultReadableRecordCount: 2,
            ExcludedByDefaultCount: 1,
            ["testing"],
            [new CortexFreshnessCount("current", 2), new CortexFreshnessCount("unverified", 1)],
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice));
        SearchResult = Accepted(new CortexKnowledgeSearchPage(
            new CortexSearchQuery(CompositionSamples.Namespace, "fixture"),
            [new CortexKnowledgeSearchHit(record, 10.5, CortexQueryPolicy.SafetyNotice)],
            CortexQueryPolicy.SafetyNotice));
        ReadResult = Accepted(record);
    }

    public List<CortexKnowledgeRecordSummary> CatalogRecords { get; } = [];
    public List<CortexCatalogQuery> CatalogQueries { get; } = [];
    public List<CortexQueryAuthority> CatalogAuthorities { get; } = [];
    public List<CortexQueryAuthority> DiscoverAuthorities { get; } = [];
    public List<CortexQueryAuthority> StatusAuthorities { get; } = [];
    public List<CortexSearchQuery> SearchQueries { get; } = [];
    public List<CortexQueryAuthority> SearchAuthorities { get; } = [];
    public List<CortexRecordReadQuery> ReadQueries { get; } = [];
    public List<CortexQueryAuthority> ReadAuthorities { get; } = [];
    public CortexQueryResult<CortexQueryDiscovery> DiscoveryResult { get; set; }
    public CortexQueryResult<CortexQueryStatus> StatusResult { get; set; }
    public CortexQueryResult<CortexKnowledgeSearchPage> SearchResult { get; set; }
    public CortexQueryResult<CortexKnowledgeRecord> ReadResult { get; set; }
    public Exception? CatalogException { get; set; }
    public Exception? DiscoverException { get; set; }
    public Exception? StatusException { get; set; }
    public Exception? SearchException { get; set; }
    public Exception? ReadException { get; set; }

    public ValueTask<CortexQueryResult<CortexQueryDiscovery>> DiscoverAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiscoverAuthorities.Add(authority);
        if (DiscoverException is not null)
            throw DiscoverException;
        return ValueTask.FromResult(DiscoveryResult);
    }

    public ValueTask<CortexQueryResult<CortexQueryStatus>> GetStatusAsync(
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusAuthorities.Add(authority);
        if (StatusException is not null)
            throw StatusException;
        return ValueTask.FromResult(StatusResult);
    }

    public ValueTask<CortexQueryResult<CortexKnowledgeCatalog>> ListCatalogAsync(
        CortexCatalogQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogQueries.Add(query with { });
        CatalogAuthorities.Add(authority);
        if (CatalogException is not null)
            throw CatalogException;
        return ValueTask.FromResult(Accepted(new CortexKnowledgeCatalog(
            "1.2026.07.14.10.00.A",
            CatalogRecords,
            CortexQueryPolicy.SafetyNotice)));
    }

    public ValueTask<CortexQueryResult<CortexKnowledgeSearchPage>> SearchAsync(
        CortexSearchQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchQueries.Add(query with { });
        SearchAuthorities.Add(authority);
        if (SearchException is not null)
            throw SearchException;
        return ValueTask.FromResult(SearchResult);
    }

    public ValueTask<CortexQueryResult<CortexKnowledgeRecord>> ReadAsync(
        CortexRecordReadQuery query,
        CortexQueryAuthority authority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadQueries.Add(query with { });
        ReadAuthorities.Add(authority);
        if (ReadException is not null)
            throw ReadException;
        return ValueTask.FromResult(ReadResult);
    }

    private static CortexQueryResult<T> Accepted<T>(T value) => new(true, value, []);
}

internal sealed class LegacyResolverFixture : ILegacyKnowledgeIdentifierResolver
{
    private readonly IReadOnlyDictionary<string, CortexResolvedRecordIdentity> _identities;

    private LegacyResolverFixture(IReadOnlyDictionary<string, CortexResolvedRecordIdentity> identities) =>
        _identities = identities;

    public List<string> Identifiers { get; } = [];
    public Exception? Exception { get; set; }

    public static LegacyResolverFixture Create()
    {
        var identity = new CortexResolvedRecordIdentity(
            CompositionSamples.Namespace,
            CompositionSamples.RecordId,
            CompositionSamples.RevisionId);
        return new LegacyResolverFixture(
            new Dictionary<string, CortexResolvedRecordIdentity>(StringComparer.Ordinal)
            {
                [CompositionSamples.RecordId] = identity,
                [CompositionSamples.RevisionId] = identity
            });
    }

    public static LegacyResolverFixture CreateResourceRedirect() =>
        new(
            new Dictionary<string, CortexResolvedRecordIdentity>(StringComparer.Ordinal)
            {
                [CompositionSamples.RecordId] = new CortexResolvedRecordIdentity(
                    CompositionSamples.Namespace,
                    CompositionSamples.RecordId,
                    CompositionSamples.SecondRevisionId)
            });

    public ValueTask<CortexResolvedRecordIdentity?> ResolveAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Identifiers.Add(identifier);
        if (Exception is not null)
            throw Exception;
        return ValueTask.FromResult<CortexResolvedRecordIdentity?>(
            _identities.TryGetValue(identifier, out var identity) ? identity : null);
    }
}

internal static class CompositionSamples
{
    public const string Namespace = "shared:test-default-library";
    public static readonly string RecordId = $"krec_{new string('a', 64)}";
    public static readonly string RevisionId = $"krev_{new string('b', 64)}";
    public static readonly string SecondRecordId = $"krec_{new string('c', 64)}";
    public static readonly string SecondRevisionId = $"krev_{new string('d', 64)}";
    public static readonly string ThirdRecordId = $"krec_{new string('e', 64)}";
    public static readonly string ThirdRevisionId = $"krev_{new string('f', 64)}";
    public static readonly string UnknownRecordId = $"krec_{new string('0', 64)}";
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-14T17:00:00Z");

    public static CortexQueryAuthority Authority() =>
        new(
            "tenant-1",
            "mcp-test-authority",
            "subject-1",
            "pegasus-cortex-mcp",
            11,
            [
                new CortexQueryNamespaceGrant(
                    Namespace,
                    "internal",
                    Enum.GetValues<CortexQueryCapability>(),
                    allowStale: true,
                    allowBlockedOrUnverified: true)
            ]);

    public static CortexKnowledgeRecord Record(
        string? recordId = null,
        string? revisionId = null,
        string? contentHash = null,
        IReadOnlyList<CortexKnowledgeSourceRevision>? sourceRevisions = null,
        string? content = null) =>
        new(
            recordId ?? RecordId,
            revisionId ?? RevisionId,
            Namespace,
            "testing",
            "composition-contract",
            "Composition contract",
            "A deterministic record for MCP composition tests.",
            content ?? "This bounded non-executable record is reference data.",
            ["composition", "mcp"],
            [
                new CortexKnowledgeApplicability(
                    "pegasus-cortex",
                    "mcp",
                    null,
                    null,
                    "verified")
            ],
            [
                new CortexKnowledgeEvidence(
                    "artifact://composition/1",
                    "application/json",
                    new string('1', 64),
                    "verified",
                    ObservedAt,
                    "1.0")
            ],
            new CortexKnowledgeProvenance(
                "pegasus.cortex.knowledge/1.0",
                ["artifact://composition/1"],
                sourceRevisions ?? [],
                ["verified"]),
            new CortexKnowledgeConfidence(0.99, "observable composition fixture"),
            new CortexKnowledgeFreshness(
                "current",
                ObservedAt,
                ObservedAt.AddDays(1),
                "current fixture"),
            "internal",
            "active",
            TrainingEligibility: false,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            contentHash ?? new string('2', 64));

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
}

internal static class CompositionAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void NotNull(object? value)
    {
        if (value is null)
            throw new InvalidOperationException("Expected a non-null value.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
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
