using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.Mcp.Diagnostics;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Protocol;

var tests = new (string Name, Func<Task> Run)[]
{
    ("V2 MCP exposes one bounded eight-tool operational surface", CortexV2McpContractTests.RunAsync),
    ("V2 MCP reports optional personal-profile state without stale bodies", CortexPersonalProfileMcpTests.RunAsync),
    ("initialize lifecycle and read-only tool contract", InitializeLifecycleAsync),
    ("hybrid tool contract stays bounded and marks writes", HybridToolContractAsync),
    ("principal capabilities constrain the advertised hybrid tools", CapabilitySpecificToolContractAsync),
    ("initialize requires client capabilities and identity", InitializeRequiredParametersAsync),
    ("null request id cannot mutate lifecycle", NullRequestIdAsync),
    ("unsupported protocol negotiates supported revision", UnsupportedProtocolNegotiationAsync),
    ("discovery and status are deterministic", DiscoveryAndStatusAsync),
    ("server build fingerprint is exact and content-safe", ServerBuildFingerprintAsync),
    ("bounded search and exact read", SearchAndReadAsync),
    ("local MCP broad mode improves lexical recall without changing exact", BroadSearchBehaviorAsync),
    ("valid nonsense search returns a clean empty result", ValidNonsenseSearchAsync),
    ("search bounds and unknown tools fail observably", SearchFailurePathsAsync),
    ("unverified knowledge requires explicit opt-in", UnverifiedOptInAsync),
    ("resources are deterministic and URI allowlisted", ResourcesAndUriSafetyAsync),
    ("request byte limit handles ASCII framing and EOF", RequestByteBoundaryAsciiAsync),
    ("request byte limit handles multibyte scalars and chunk splits", RequestByteBoundaryMultibyteAsync),
    ("malformed and oversized lines recover", MalformedAndOversizedRecoveryAsync),
    ("oversized response is replaced before serialization grows unbounded", OversizedResponseFormattingAsync),
    ("pooled response buffer enforces its logical ceiling", PooledResponseBufferCeilingAsync),
    ("invalid UTF-8 terminates the session with a protocol error", InvalidUtf8TerminatesSessionAsync),
    ("notifications never produce responses", NotificationBehaviorAsync),
    ("cancellation is propagated", CancellationAsync),
    ("method invocation initialize payload is fresh and preserves native lifecycle", MethodInvocationTests.InitializePayloadAsync),
    ("method invocation validation and typed responses match ready STDIO", MethodInvocationTests.ValidationAndParityAsync),
    ("method invocation concurrent reuse keeps typed IDs independent", MethodInvocationTests.ConcurrentTypedIdsAsync),
    ("method invocation cancellation and unknown outcome have one boundary", MethodInvocationTests.CancellationAndFailureAsync),
    ("private dispatch is fair and count/byte bounded", RequestDispatchTests.FairnessAndBoundsAsync),
    ("private cancellation preserves uncertainty and active budgets", RequestDispatchTests.CancellationAndUncertaintyAsync),
    ("private correlation isolates sessions duplicates and finite drain", RequestDispatchTests.CorrelationAndDrainAsync),
    ("MCP reads typed cancellation while a callback is running", RequestDispatchTests.McpTargetedCancellationAsync),
    ("review regressions bound canceled queues reply errors and owner fairness", RequestDispatchTests.ReviewRegressionsAsync),
    ("opt-in MCP multiplex finishes independent requests out of order", RequestDispatchTests.McpNegotiatedMultiplexAsync),
    ("malformed-method duplicate IDs close before ambiguous responses", RequestDispatchTests.MalformedDuplicateIdAsync),
    ("cancellation cleanup and completed multiplex writes remain bounded", RequestDispatchTests.CancellationCleanupAndWriteTrackingAsync),
    ("canceled ready heads cannot dispatch unprepared successors", RequestDispatchTests.UnpreparedSuccessorAsync),
    ("general search reserves a result from each matching authorized source", CortexMcpCompositionTests.GeneralSearchMergeBalancesSourcesAsync),
    ("composition snapshots one session-stable resource catalog", CortexMcpCompositionTests.StartupCatalogSnapshotAsync),
    ("composition delegates bounded search and exact read", CortexMcpCompositionTests.SearchAndReadDelegationAsync),
    ("composition propagates explicit broad lexical mode", CortexMcpCompositionTests.BroadSearchDelegationAsync),
    ("broad search preserves reserved project-link isolation", CortexMcpCompositionTests.BroadSearchPreservesReservedIsolationAsync),
    ("runtime storage and search features are truthful", CortexMcpCompositionTests.RuntimeStorageAndFeaturesAreTruthfulAsync),
    ("publication maintenance validates exact CAS coordinates", CortexMcpCompositionTests.PublicationChangeAsync),
    ("provider-neutral project brief stays passive and byte bounded", CortexMcpCompositionTests.ProjectBriefRuntimeAndBudgetAsync),
    ("retained project briefs fail closed across project identity", CortexMcpCompositionTests.RetainedProjectBriefIdentityFailsClosedAsync),
    ("provider-neutral MCP output excludes integrated dependencies", CortexMcpCompositionTests.ProviderNeutralDependencyClosureAsync),
    ("provider-neutral MCP ignores integrated environment activation", CortexMcpCompositionTests.ProviderNeutralExecutableIgnoresIntegratedEnvironmentAsync),
    ("compact projections preserve full-response compatibility", CortexMcpCompositionTests.CompactProjectionAsync),
    ("compact search hydration is exact and bounded", CortexMcpCompositionTests.CompactSearchHydrationAsync),
    ("legacy record and revision aliases resolve exactly", CortexMcpCompositionTests.LegacyIdentifierResolutionAsync),
    ("taxonomy diagnostics preserve the legacy wire contract", CortexMcpCompositionTests.TaxonomyDiagnosticCompatibilityAsync),
    ("structured provenance fails closed on the legacy wire", CortexMcpCompositionTests.StructuredProvenanceFailsClosedAsync),
    ("record resources reject resolver identity redirects", CortexMcpCompositionTests.ResourceResolverRedirectFailsClosedAsync),
    ("composition cancellation propagates", CortexMcpCompositionTests.CancellationPropagationAsync),
    ("Default Library enforces scope operation pairing", CortexMcpCompositionTests.DefaultLibraryScopeOperationDefenseAsync)
};

if (args is ["--method-invocation-only"])
{
    tests = tests.Where(test => test.Name.StartsWith("method invocation ", StringComparison.Ordinal)).ToArray();
    if (tests.Length != 4)
        throw new InvalidOperationException("The method invocation selector must identify exactly four protocol fixtures.");
}

// Keep initialize compatibility on the existing in-memory protocol fixtures.
if (args is ["--initialize-compatibility-only"])
{
    tests = tests.Where(test =>
        test.Run == InitializeLifecycleAsync ||
        test.Run == InitializeRequiredParametersAsync ||
        test.Run == UnsupportedProtocolNegotiationAsync ||
        test.Run == RequestDispatchTests.McpNegotiatedMultiplexAsync).ToArray();
    if (tests.Length != 4)
        throw new InvalidOperationException("The initialize selector must identify exactly four protocol fixtures.");
}

// Keep embedded-guide qualification on the existing in-memory resource fixture.
if (args is ["--guidance-only"])
{
    tests = tests.Where(test => test.Run == ResourcesAndUriSafetyAsync).ToArray();
    if (tests.Length != 1)
        throw new InvalidOperationException("The guidance selector must identify exactly one resource fixture.");
}

if (args.Contains("--personal-profile-only", StringComparer.Ordinal))
{
    tests = tests.Where(test => test.Run == CortexPersonalProfileMcpTests.RunAsync).ToArray();
    if (tests.Length != 1)
        throw new InvalidOperationException("The personal-profile selector must identify exactly one MCP fixture.");
}

if (args.Contains("--protocol-executable-only", StringComparer.Ordinal))
    tests = tests.Where(test => test.Run == CortexMcpCompositionTests.ProviderNeutralExecutableIgnoresIntegratedEnvironmentAsync).ToArray();

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine($"{failures.Count}/{tests.Length} Pegasus Cortex MCP protocol checks failed.");
    return 1;
}

Console.WriteLine($"Pegasus Cortex MCP protocol checks passed ({tests.Length}/{tests.Length}).");
return 0;

static async Task InitializeLifecycleAsync()
{
    var session = await RunSessionAsync(
        Request(0, "tools/list"),
        InitializeRequest(1),
        Request(2, "tools/list"),
        InitializedNotification(),
        Request(3, "tools/list")).ConfigureAwait(false);

    Assert.Equal(4, session.Responses.Count);
    Assert.Equal(-32002, ErrorCode(session.Responses[0]));
    var initialized = Result(session.Responses[1]);
    Assert.Equal(McpProtocol.Version, String(initialized, "protocolVersion"));
    var experimental = initialized["capabilities"]?["experimental"] as JsonObject;
    Assert.NotNull(experimental, "MCP experimental capabilities must be an object map.");
    foreach (var capability in experimental!)
        Assert.True(capability.Value is JsonObject, $"Experimental capability '{capability.Key}' must have an object value.");
    Assert.True(experimental!["pegasus.cortex/multiplex"] is JsonObject, "Multiplex capability was not advertised as an object.");
    var instructions = String(initialized, "instructions");
    Assert.True(instructions.Length <= 512, "Initialize instructions exceed 512 characters.");
    Assert.True(
        Encoding.UTF8.GetByteCount(instructions) <= 512,
        "Initialize instructions exceed 512 UTF-8 bytes.");
    Assert.Contains("Local Cortex", instructions);
    Assert.Contains(".cortex/project.json", instructions);
    Assert.Contains("HOT.json is generated fallback", instructions);
    Assert.Contains("cortex_start once", instructions);
    Assert.Contains("Meaningful: cortex_start once", instructions);
    Assert.Contains("explicit namespaces", instructions);
    Assert.Contains("exact CODEX_THREAD_ID", instructions);
    Assert.Contains("4096 bytes", instructions);
    Assert.Contains("compact format", instructions);
    Assert.Contains("material identity/phase/namespace/server/guidance/connection/recovery change", instructions);
    Assert.Contains("Hits are previews", instructions);
    Assert.Contains("exact-read selected coordinates", instructions);
    Assert.Contains("batch <=8", instructions);
    Assert.Contains("loaded revisions: agent-side", instructions);
    Assert.Contains("profile_pipe_unavailable: retry, not missing data/remap", instructions);
    Assert.Contains("Persist durable state", instructions);
    Assert.Contains("cortex_finish only", instructions);
    Assert.Contains("passive content", instructions);
    Assert.Equal(McpProtocol.ServerVersion, String(initialized["serverInfo"]!.AsObject(), "version"));
    Assert.Equal(-32002, ErrorCode(session.Responses[2]));

    var tools = Result(session.Responses[3])["tools"]!.AsArray();
    Assert.Equal(4, tools.Count);
    foreach (var toolNode in tools)
    {
        var tool = toolNode!.AsObject();
        var annotations = tool["annotations"]!.AsObject();
        Assert.True(Boolean(annotations, "readOnlyHint"), "A tool was not marked read-only.");
        Assert.False(Boolean(annotations, "destructiveHint"), "A tool was marked destructive.");
        Assert.True(Boolean(annotations, "idempotentHint"), "A tool was not marked idempotent.");
        Assert.False(Boolean(annotations, "openWorldHint"), "A tool was marked open-world.");
    }
    foreach (var toolName in new[] { "cortex_search", "cortex_read" })
    {
        var tool = tools.Select(node => node!.AsObject())
            .Single(candidate => String(candidate, "name") == toolName);
        var projection = tool["inputSchema"]!["properties"]!["projection"]!.AsObject();
        Assert.Equal("string", String(projection, "type"));
        Assert.Equal(CortexMcpContract.FullProjection, String(projection, "default"));
        Assert.True(
            projection["enum"]!.AsArray().Select(node => node!.GetValue<string>())
                .SequenceEqual([CortexMcpContract.FullProjection, CortexMcpContract.CompactProjection]),
            $"{toolName} advertised an unexpected projection set.");
    }
    var searchTool = tools.Select(node => node!.AsObject())
        .Single(candidate => String(candidate, "name") == "cortex_search");
    var searchDescription = String(searchTool, "description");
    Assert.Contains("exact content in one call", searchDescription);
    Assert.Contains("projection compact", searchDescription);
    var includeContent = searchTool["inputSchema"]!["properties"]!["includeContent"]!.AsObject();
    Assert.Equal("boolean", String(includeContent, "type"));
    Assert.False(Boolean(includeContent, "default"), "Search content hydration was not opt-in.");
    Assert.Contains("avoid follow-up reads", String(includeContent, "description"));
    var searchMode = searchTool["inputSchema"]!["properties"]!["mode"]!.AsObject();
    Assert.Equal(CortexMcpContract.ExactSearchMode, String(searchMode, "default"));
    Assert.True(
        searchMode["enum"]!.AsArray().Select(node => node!.GetValue<string>())
            .SequenceEqual([CortexMcpContract.ExactSearchMode, CortexMcpContract.BroadSearchMode]),
        "Search advertised an unexpected retrieval-mode set.");
    var readTool = tools.Select(node => node!.AsObject())
        .Single(candidate => String(candidate, "name") == "cortex_read");
    Assert.Contains("known ID", String(readTool, "description"));
    Assert.Contains("search omitted content", String(readTool, "description"));
    Assert.False(
        readTool["inputSchema"]!["properties"]!.AsObject().ContainsKey("includeContent"),
        "Read unexpectedly advertised the search-only hydration argument.");
    Assert.Equal(string.Empty, session.Stderr);
}

static Task HybridToolContractAsync()
{
    var tools = CortexMcpContract.ListTools(RuntimeContract(
        CortexMcpRuntimeCapability.Read |
        CortexMcpRuntimeCapability.Correlate |
        CortexMcpRuntimeCapability.Propose |
        CortexMcpRuntimeCapability.Review));
    Assert.Equal(10, tools.Count);
    var byName = tools
        .Select(node => node!.AsObject())
        .ToDictionary(tool => String(tool, "name"), StringComparer.Ordinal);
    foreach (var name in new[]
             {
                 "cortex_discover",
                 "cortex_status",
                 "cortex_search",
                 "cortex_read",
                 "cortex_project_brief",
                 "cortex_correlate_project",
                 "cortex_submit_proposal",
                 "cortex_list_proposals",
                 "cortex_change_publication",
                 "cortex_review_proposal"
             })
    {
        Assert.True(byName.ContainsKey(name), $"Hybrid tool '{name}' is missing.");
    }

    foreach (var name in new[]
             {
                 "cortex_correlate_project",
                 "cortex_submit_proposal",
                 "cortex_change_publication",
                 "cortex_review_proposal"
             })
    {
        var annotations = byName[name]["annotations"]!.AsObject();
        Assert.False(Boolean(annotations, "readOnlyHint"), $"{name} was incorrectly marked read-only.");
        Assert.False(Boolean(annotations, "destructiveHint"), $"{name} was incorrectly marked destructive.");
        Assert.True(Boolean(annotations, "idempotentHint"), $"{name} was not marked idempotent.");
        Assert.False(Boolean(annotations, "openWorldHint"), $"{name} was marked open-world.");
    }
    Assert.True(
        Boolean(byName["cortex_list_proposals"]["annotations"]!.AsObject(), "readOnlyHint"),
        "Proposal listing was not marked read-only.");
    Assert.True(
        Boolean(byName["cortex_project_brief"]["annotations"]!.AsObject(), "readOnlyHint"),
        "Project brief was not marked read-only.");
    Assert.Contains("never crawls files", String(byName["cortex_project_brief"], "description"));
    Assert.Contains("claims tasks", String(byName["cortex_project_brief"], "description"));
    Assert.Contains(
        "knownAuthoritySetHash",
        String(byName["cortex_project_brief"], "description"));
    var briefSchema = byName["cortex_project_brief"]["inputSchema"]!.AsObject();
    var briefProperties = briefSchema["properties"]!.AsObject();
    var briefMaximum = briefProperties["maxUtf8Bytes"]!.AsObject();
    Assert.Equal(
        CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
        briefMaximum["minimum"]!.GetValue<int>());
    Assert.Equal(
        CortexProjectBriefContract.MaximumUtf8Bytes,
        briefMaximum["maximum"]!.GetValue<int>());
    Assert.Equal(
        CortexProjectBriefContract.DefaultMaximumUtf8Bytes,
        briefMaximum["default"]!.GetValue<int>());
    Assert.Equal(
        "^[0-9a-f]{64}$",
        briefProperties["knownAuthoritySetHash"]!.AsObject()["pattern"]!
            .GetValue<string>());
    Assert.True(
        briefSchema["required"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .SequenceEqual(
                ["projectLinkId", "knownAuthoritySetHash"],
                StringComparer.Ordinal),
        "Project brief did not require both project link and local authority hash.");
    Assert.Contains(
        "Without projectLinkId it searches the Default Library plus any advertised collective provider",
        String(byName["cortex_search"], "description"));
    Assert.Contains(
        "reserved libraries require their exact authorized link",
        String(byName["cortex_search"], "description"));
    Assert.Contains(
        "reduced provisional confidence",
        String(byName["cortex_submit_proposal"], "description"));
    Assert.Contains(
        "later independent review",
        String(byName["cortex_submit_proposal"], "description"));
    Assert.Contains(
        "immutable history remains",
        String(byName["cortex_change_publication"], "description"));
    var publicationChangeSchema =
        byName["cortex_change_publication"]["inputSchema"]!.AsObject();
    var publicationChangeRequired = publicationChangeSchema["required"]!.AsArray()
        .Select(node => node!.GetValue<string>())
        .ToHashSet(StringComparer.Ordinal);
    Assert.True(
        publicationChangeRequired.SetEquals(
        [
            "projectLinkId",
            "action",
            "targetRecordId",
            "expectedHeadRevisionId",
            "expectedHeadGeneration",
            "reasonCode",
            "reason",
            "occurredAtUtc"
        ]),
        "Publication maintenance did not require the exact project/head coordinates.");
    Assert.False(
        publicationChangeSchema["properties"]!.AsObject().ContainsKey("userConfirmed"),
        "Author-owned publication maintenance incorrectly requires human review authority.");
    var expectedHeadGeneration = publicationChangeSchema["properties"]!.AsObject()
        ["expectedHeadGeneration"]!.AsObject();
    Assert.Equal(1, expectedHeadGeneration["minimum"]!.GetValue<int>());
    Assert.Equal(1, expectedHeadGeneration["maximum"]!.GetValue<int>());
    var proposalProperties =
        byName["cortex_submit_proposal"]["inputSchema"]!["properties"]!.AsObject();
    Assert.Equal(
        LearningFeedbackContractLimits.MaximumIdentifierCharacters,
        proposalProperties["taxonomy"]!.AsObject()["maxLength"]!.GetValue<int>());
    Assert.Equal(
        LearningFeedbackContractLimits.MaximumProposalKeywords,
        proposalProperties["keywords"]!.AsObject()["maxItems"]!.GetValue<int>());
    Assert.True(
        proposalProperties["keywords"]!.AsObject()["uniqueItems"]!.GetValue<bool>(),
        "Proposal keywords were not declared unique.");
    Assert.Equal(
        LearningFeedbackContractLimits.MaximumProposalApplicabilityItems,
        proposalProperties["applicability"]!.AsObject()["maxItems"]!.GetValue<int>());
    var applicabilityItem = proposalProperties["applicability"]!.AsObject()
        ["items"]!.AsObject();
    Assert.True(
        applicabilityItem["required"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .SequenceEqual(["productId", "componentId"], StringComparer.Ordinal),
        "Proposal applicability did not require exact product/component coordinates.");
    var correlateProperties =
        byName["cortex_correlate_project"]["inputSchema"]!["properties"]!.AsObject();
    var libraryAccess = correlateProperties["libraryAccess"]!.AsObject();
    Assert.Equal(
        CortexProjectCorrelationContract.CollectiveLibraryAccess,
        String(libraryAccess, "default"));
    Assert.True(
        libraryAccess["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .SequenceEqual(
                new[]
                {
                    CortexProjectCorrelationContract.CollectiveLibraryAccess,
                    CortexProjectCorrelationContract.ReservedLibraryAccess
                },
                StringComparer.Ordinal),
        "The correlation schema does not advertise exactly collective and reserved access.");
    var reviewRequired = byName["cortex_review_proposal"]["inputSchema"]!["required"]!.AsArray()
        .Select(node => node!.GetValue<string>())
        .ToArray();
    Assert.True(
        reviewRequired.Contains("userConfirmed", StringComparer.Ordinal),
        "Review did not require explicit user confirmation.");
    var correlateRequired = byName["cortex_correlate_project"]["inputSchema"]!["required"]!.AsArray()
        .Select(node => node!.GetValue<string>())
        .ToArray();
    Assert.False(
        correlateRequired.Contains("libraryAccess", StringComparer.Ordinal),
        "The default collective mode unexpectedly became a required client argument.");
    return Task.CompletedTask;
}

static Task CapabilitySpecificToolContractAsync()
{
    var auditor = CortexMcpContract.ListTools(
        RuntimeContract(CortexMcpRuntimeCapability.Audit));
    var auditorNames = auditor
        .Select(node => String(node!.AsObject(), "name"))
        .ToArray();
    Assert.True(
        auditorNames.SequenceEqual(
            ["cortex_discover", "cortex_status", "cortex_list_proposals", "cortex_submit_audit"],
            StringComparer.Ordinal),
        "The audit principal received a tool outside its exact capability set.");

    var delegator = CortexMcpContract.ListTools(
        RuntimeContract(CortexMcpRuntimeCapability.DelegateReview));
    var delegatorNames = delegator
        .Select(node => String(node!.AsObject(), "name"))
        .ToArray();
    Assert.True(
        delegatorNames.SequenceEqual(
            ["cortex_discover", "cortex_status", "cortex_manage_review_grant"],
            StringComparer.Ordinal),
        "The delegated-review principal received a tool outside its exact capability set.");

    var readOnly = CortexMcpContract.ListTools(
        RuntimeContract(CortexMcpRuntimeCapability.Read));
    var readOnlyNames = readOnly
        .Select(node => String(node!.AsObject(), "name"))
        .ToHashSet(StringComparer.Ordinal);
    Assert.True(readOnlyNames.Contains("cortex_search"), "Read authority did not expose search.");
    Assert.True(readOnlyNames.Contains("cortex_read"), "Read authority did not expose exact read.");
    Assert.True(readOnlyNames.Contains("cortex_project_brief"), "Read authority did not expose project brief.");
    Assert.True(readOnlyNames.Contains("cortex_list_proposals"), "Read authority did not expose proposal metadata.");
    Assert.False(readOnlyNames.Contains("cortex_submit_audit"), "Read authority exposed audit mutation.");
    Assert.False(readOnlyNames.Contains("cortex_manage_review_grant"), "Read authority exposed grant mutation.");

    var readyRuntime = RuntimeContract(CortexMcpRuntimeCapability.Read);
    var notReady = CortexMcpContract.ListTools(readyRuntime with
    {
        Descriptor = readyRuntime.Descriptor with { State = "not-ready" }
    });
    Assert.True(
        notReady.Select(node => String(node!.AsObject(), "name"))
            .SequenceEqual(["cortex_discover", "cortex_status"], StringComparer.Ordinal),
        "A not-ready runtime advertised a data or mutation tool.");
    return Task.CompletedTask;
}

static async Task InitializeRequiredParametersAsync()
{
    var missingCapabilities = new JsonObject
    {
        ["protocolVersion"] = McpProtocol.Version,
        ["clientInfo"] = ClientInfo()
    };
    var missingClientInfo = new JsonObject
    {
        ["protocolVersion"] = McpProtocol.Version,
        ["capabilities"] = new JsonObject()
    };
    var invalidClientInfo = new JsonObject
    {
        ["protocolVersion"] = McpProtocol.Version,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "client" }
    };
    var session = await RunSessionAsync(
        Request(1, "initialize", missingCapabilities),
        Request(2, "initialize", missingClientInfo),
        Request(3, "initialize", invalidClientInfo),
        InitializeRequest(4)).ConfigureAwait(false);

    Assert.Equal(4, session.Responses.Count);
    Assert.Equal(-32602, ErrorCode(session.Responses[0]));
    Assert.Equal(-32602, ErrorCode(session.Responses[1]));
    Assert.Equal(-32602, ErrorCode(session.Responses[2]));
    Assert.Equal(McpProtocol.Version, String(Result(session.Responses[3]), "protocolVersion"));
}

static async Task NullRequestIdAsync()
{
    var invalidInitialize = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = null,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = McpProtocol.Version,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = ClientInfo()
        }
    };
    var session = await RunSessionAsync(
        invalidInitialize.ToJsonString(),
        InitializeRequest(1)).ConfigureAwait(false);

    Assert.Equal(2, session.Responses.Count);
    Assert.Equal(-32600, ErrorCode(session.Responses[0]));
    Assert.Equal(McpProtocol.Version, String(Result(session.Responses[1]), "protocolVersion"));
}

static async Task UnsupportedProtocolNegotiationAsync()
{
    var session = await RunSessionAsync(
        InitializeRequest(1, "1900-01-01"),
        InitializedNotification(),
        Request(2, "ping")).ConfigureAwait(false);

    Assert.Equal(2, session.Responses.Count);
    Assert.Equal(McpProtocol.Version, String(Result(session.Responses[0]), "protocolVersion"));
    Assert.Equal(0, Result(session.Responses[1]).Count);
}

static async Task DiscoveryAndStatusAsync()
{
    var session = await RunReadySessionAsync(
        CallTool(2, "cortex_discover", new JsonObject()),
        CallTool(3, "cortex_status", new JsonObject()),
        CallTool(4, "cortex_status", new JsonObject())).ConfigureAwait(false);

    Assert.Equal(4, session.Responses.Count);
    var discovery = Structured(session.Responses[1]);
    Assert.Equal("isolated-read-only", String(discovery, "mode"));
    var advertisedTools = discovery["tools"]!.AsArray()
        .Select(node => node!.GetValue<string>())
        .ToArray();
    Assert.True(
        advertisedTools.SequenceEqual(["cortex_discover", "cortex_status", "cortex_search", "cortex_read"]),
        "Discovery changed the isolated four-tool contract or advertised query-v1 tools.");
    var limits = discovery["limits"]!.AsObject();
    Assert.Equal(McpProtocol.MaximumRequestBytes, Integer(limits, "maximumRequestBytes"));
    Assert.Equal(McpProtocol.MaximumResponseBytes, Integer(limits, "maximumResponseBytes"));
    Assert.False(limits.ContainsKey("maximumRequestCharacters"), "Discovery retained a misleading character limit.");
    var access = discovery["access"]!.AsObject();
    foreach (var property in access)
        Assert.False(property.Value!.GetValue<bool>(), $"Access flag {property.Key} was unexpectedly enabled.");
    Assert.Equal(CortexMcpStorageKinds.None, String(discovery, "storageKind"));
    var features = discovery["features"]!.AsObject();
    Assert.True(Boolean(features, "defaultLibrarySearch"), "Default Library search was not advertised.");
    foreach (var feature in features.Where(item => item.Key != "defaultLibrarySearch"))
        Assert.False(feature.Value!.GetValue<bool>(), $"Feature {feature.Key} was unexpectedly enabled.");

    var firstStatus = Structured(session.Responses[2]);
    var secondStatus = Structured(session.Responses[3]);
    Assert.Equal(firstStatus.ToJsonString(), secondStatus.ToJsonString());
    Assert.Equal("ready", String(firstStatus, "status"));
    var library = firstStatus["library"]!.AsObject();
    Assert.Equal(19, Integer(library, "recordCount"));
    Assert.True(Integer(library, "defaultReadableRecordCount") > 0, "No records are default-readable.");
    Assert.True(Integer(library, "excludedByDefaultCount") > 0, "The unverified record was not excluded by default.");
    Assert.Equal("untrusted_data", String(firstStatus, "contentTreatment"));
    Assert.False(Boolean(firstStatus, "executable"), "Status declared executable content.");
    Assert.Equal(CortexMcpStorageKinds.None, String(firstStatus, "storageKind"));
    Assert.Equal(features.ToJsonString(), firstStatus["features"]!.ToJsonString());
}

static Task ServerBuildFingerprintAsync()
{
    var assembly = Assembly.GetExecutingAssembly();
    var identity = McpServerBuildIdentityProvider.Capture(assembly);
    Assert.Equal("entry-assembly-bytes", identity.Basis);

    using var stream = new FileStream(
        assembly.Location,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete);
    var expected = $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
    Assert.Equal(expected, identity.Fingerprint);
    Assert.Equal(71, identity.Fingerprint.Length);
    Assert.True(
        identity.Fingerprint.AsSpan("sha256:".Length).ToString().All(
            character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
        "Build fingerprint was not lowercase hexadecimal.");
    Assert.False(
        identity.Fingerprint.Contains('\\') || identity.Fingerprint.Contains('/'),
        "Build fingerprint exposed a path separator.");
    Assert.False(
        string.Equals(identity.Fingerprint, assembly.Location, StringComparison.OrdinalIgnoreCase),
        "Build fingerprint exposed the entry-assembly path.");
    return Task.CompletedTask;
}

static async Task SearchAndReadAsync()
{
    var searchSession = await RunReadySessionAsync(
        CallTool(2, "cortex_search", new JsonObject
        {
            ["text"] = "dependency lifecycle",
            ["limit"] = 5
        })).ConfigureAwait(false);
    var search = Structured(searchSession.Responses[1]);
    var records = search["records"]!.AsArray();
    Assert.True(records.Count is > 0 and <= 5, "Search did not return a bounded result set.");
    var first = records[0]!.AsObject();
    var recordId = String(first, "recordId");
    Assert.True(recordId.StartsWith("krec_", StringComparison.Ordinal), "Search returned an invalid record identifier.");
    Assert.True(String(first, "contentExcerpt").Length <= 2048, "Search excerpt exceeded its bound.");
    Assert.False(Boolean(first, "executable"), "Search result was executable.");
    Assert.Equal("untrusted_data", String(first, "contentTreatment"));
    Assert.True(first["matchedTerms"]!.AsArray().Count > 0, "Search omitted lexical match evidence.");
    var retrieval = search["retrieval"]!.AsObject();
    Assert.Equal("default-library", String(retrieval, "route"));
    Assert.Equal(2, Integer(retrieval, "queryTermCount"));
    Assert.Equal(1, Integer(retrieval, "queriedSourceCount"));
    Assert.Equal(0, Integer(retrieval, "partialFailureCount"));

    var readSession = await RunReadySessionAsync(
        CallTool(2, "cortex_read", new JsonObject { ["id"] = recordId })).ConfigureAwait(false);
    var read = Structured(readSession.Responses[1]);
    Assert.Equal(recordId, String(read, "recordId"));
    Assert.True(String(read, "content").Length > 0, "Exact read returned no content.");
    Assert.False(Boolean(read, "executable"), "Exact read was executable.");
    Assert.Equal("untrusted_data", String(read, "contentTreatment"));
}

static async Task BroadSearchBehaviorAsync()
{
    var session = await RunReadySessionAsync(
        CallTool(2, "cortex_search", new JsonObject
        {
            ["text"] = "snapshots",
            ["mode"] = CortexMcpContract.ExactSearchMode,
            ["limit"] = 10
        }),
        CallTool(3, "cortex_search", new JsonObject
        {
            ["text"] = "snapshots",
            ["mode"] = CortexMcpContract.BroadSearchMode,
            ["limit"] = 10
        })).ConfigureAwait(false);

    var exact = Structured(session.Responses[1]);
    var broad = Structured(session.Responses[2]);
    Assert.Equal(0, Integer(exact, "count"));
    Assert.False(
        exact["query"]!.AsObject().ContainsKey("mode"),
        "Explicit exact mode changed the compatibility response shape.");
    Assert.True(
        broad["records"]!.AsArray().Any(node =>
            String(node!.AsObject(), "title") == "Verified snapshot before mutation"),
        "Broad MCP mode did not recover the singular snapshot record.");
    Assert.Equal(
        CortexMcpContract.BroadSearchMode,
        String(broad["query"]!.AsObject(), "mode"));
}

static async Task ValidNonsenseSearchAsync()
{
    var session = await RunReadySessionAsync(
        CallTool(2, "cortex_search", new JsonObject
        {
            ["text"] = "zzzxqvnonexistentterm",
            ["mode"] = CortexMcpContract.BroadSearchMode,
            ["limit"] = 3
        })).ConfigureAwait(false);
    var result = Structured(session.Responses[1]);
    Assert.Equal(0, Integer(result, "count"));
    Assert.Equal(0, result["records"]!.AsArray().Count);
    var retrieval = result["retrieval"]!.AsObject();
    Assert.Equal("default-library", String(retrieval, "route"));
    Assert.Equal(1, Integer(retrieval, "queryTermCount"));
    Assert.Equal(1, Integer(retrieval, "queriedSourceCount"));
    Assert.Equal(0, Integer(retrieval, "partialFailureCount"));
}

static async Task SearchFailurePathsAsync()
{
    var session = await RunReadySessionAsync(
        CallTool(2, "cortex_search", new JsonObject { ["text"] = "nexus", ["limit"] = 11 }),
        CallTool(3, "cortex_search", new JsonObject { ["text"] = "nexus", ["path"] = "../STATE.md" }),
        CallTool(4, "cortex_search", new JsonObject { ["text"] = "nexus", ["projection"] = "summary" }),
        CallTool(5, "cortex_search", new JsonObject { ["text"] = "nexus", ["mode"] = "semantic" }),
        CallTool(6, "cortex_read", new JsonObject
        {
            ["id"] = $"krec_{new string('0', 64)}",
            ["projection"] = "summary"
        }),
        CallTool(7, "cortex_missing", new JsonObject())).ConfigureAwait(false);

    Assert.Equal(7, session.Responses.Count);
    AssertToolError(session.Responses[1], "cortex_search_arguments_invalid");
    AssertToolError(session.Responses[2], "cortex_search_arguments_invalid");
    AssertToolError(session.Responses[3], "cortex_search_arguments_invalid");
    AssertToolError(session.Responses[4], "cortex_search_arguments_invalid");
    AssertToolError(session.Responses[5], "cortex_read_arguments_invalid");
    AssertToolError(session.Responses[6], "cortex_tool_unknown");
}

static async Task UnverifiedOptInAsync()
{
    var searchSession = await RunReadySessionAsync(
        CallTool(2, "cortex_search", new JsonObject
        {
            ["text"] = "Production readiness remains unverified",
            ["limit"] = 10
        }),
        CallTool(3, "cortex_search", new JsonObject
        {
            ["text"] = "Production readiness remains unverified",
            ["limit"] = 10,
            ["includeBlockedOrUnverified"] = true
        })).ConfigureAwait(false);

    var defaultRecords = Structured(searchSession.Responses[1])["records"]!.AsArray();
    Assert.False(defaultRecords.Any(IsUnverifiedNode), "Default search exposed unverified knowledge.");
    var optedIn = Structured(searchSession.Responses[2])["records"]!.AsArray();
    var unverified = optedIn.Select(node => node!.AsObject()).FirstOrDefault(IsUnverifiedObject);
    Assert.NotNull(unverified, "Explicit opt-in did not expose the unverified readiness record.");

    var recordId = String(unverified!, "recordId");
    var readSession = await RunReadySessionAsync(
        CallTool(2, "cortex_read", new JsonObject { ["id"] = recordId }),
        CallTool(3, "cortex_read", new JsonObject
        {
            ["id"] = recordId,
            ["includeBlockedOrUnverified"] = true
        })).ConfigureAwait(false);
    AssertToolError(readSession.Responses[1], "cortex_record_not_available");
    Assert.False(Boolean(Structured(readSession.Responses[2]), "executable"), "Opted-in read was executable.");
}

static async Task ResourcesAndUriSafetyAsync()
{
    var listSession = await RunReadySessionAsync(
        Request(2, "resources/list"),
        Request(3, "resources/list"),
        Request(4, "resources/templates/list"),
        Request(5, "resources/templates/list"),
        Request(6, "resources/templates/list", new JsonObject { ["cursor"] = "unsupported" }),
        Request(7, "resources/read", new JsonObject { ["uri"] = CortexMcpApplication.CatalogResourceUri })).ConfigureAwait(false);
    var first = Result(listSession.Responses[1])["resources"]!.AsArray();
    var second = Result(listSession.Responses[2])["resources"]!.AsArray();
    Assert.Equal(first.ToJsonString(), second.ToJsonString());
    Assert.Equal(3, first.Count);
    Assert.Equal(CortexMcpApplication.StatusResourceUri, String(first[0]!.AsObject(), "uri"));
    Assert.Equal(CortexMcpApplication.CatalogResourceUri, String(first[1]!.AsObject(), "uri"));
    Assert.Equal(CortexMcpApplication.GuideResourceUri, String(first[2]!.AsObject(), "uri"));

    var firstTemplates = Result(listSession.Responses[3])["resourceTemplates"]!.AsArray();
    var secondTemplates = Result(listSession.Responses[4])["resourceTemplates"]!.AsArray();
    Assert.Equal(firstTemplates.ToJsonString(), secondTemplates.ToJsonString());
    Assert.Equal(1, firstTemplates.Count);
    Assert.Equal(
        CortexMcpApplication.RecordResourceUriTemplate,
        String(firstTemplates[0]!.AsObject(), "uriTemplate"));
    Assert.Equal(-32602, ErrorCode(listSession.Responses[5]));
    AssertResourceJson(listSession.Responses[6]);

    var resourceDiscoveryBytes =
        Encoding.UTF8.GetByteCount(Result(listSession.Responses[1]).ToJsonString(McpProtocol.JsonOptions)) +
        Encoding.UTF8.GetByteCount(Result(listSession.Responses[3]).ToJsonString(McpProtocol.JsonOptions));
    Assert.True(
        resourceDiscoveryBytes <= 2048,
        $"Fixed resource and template discovery used {resourceDiscoveryBytes} UTF-8 bytes.");

    var catalogContents = Result(listSession.Responses[6])["contents"]!.AsArray();
    var catalogText = String(catalogContents[0]!.AsObject(), "text");
    var catalog = JsonNode.Parse(catalogText)!.AsObject();
    var recordUri = String(catalog["records"]!.AsArray()[0]!.AsObject(), "resourceUri");
    var readSession = await RunReadySessionAsync(
        Request(2, "resources/read", new JsonObject { ["uri"] = CortexMcpApplication.StatusResourceUri }),
        Request(3, "resources/read", new JsonObject { ["uri"] = CortexMcpApplication.CatalogResourceUri }),
        Request(4, "resources/read", new JsonObject { ["uri"] = CortexMcpApplication.GuideResourceUri }),
        Request(5, "resources/read", new JsonObject { ["uri"] = recordUri }),
        Request(6, "resources/read", new JsonObject { ["uri"] = "cortex://default-library/records/../../.cortex/STATE.md" }),
        Request(7, "resources/read", new JsonObject { ["uri"] = "file:///D:/Codex Projects/Pegasus Cortex/.cortex/STATE.md" })).ConfigureAwait(false);

    Assert.Equal(7, readSession.Responses.Count);
    AssertResourceJson(readSession.Responses[1]);
    AssertResourceJson(readSession.Responses[2]);
    AssertResourceJson(readSession.Responses[3]);
    AssertResourceJson(readSession.Responses[4]);
    var guideText = String(
        Result(readSession.Responses[3])["contents"]!.AsArray().Single()!.AsObject(),
        "text");
    var guide = JsonNode.Parse(guideText)!.AsObject();
    Assert.Equal(CortexContinuityGuide.Version, String(guide, "guideVersion"));
    Assert.Equal(
        CortexContinuityGuide.ExpectedContentSha256,
        String(guide["source"]!.AsObject(), "sha256"));
    Assert.Equal(
        CortexContinuityGuide.ExpectedContentSha256,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String(guide, "content"))))
            .ToLowerInvariant());
    Assert.True(
        Boolean(guide["authority"]!.AsObject(), "projectFilesRemainAuthoritative"),
        "The guide did not preserve project-file authority.");
    Assert.False(
        Boolean(guide["authority"]!.AsObject(), "grantsOperationalAuthority"),
        "The guide unexpectedly granted operational authority.");
    Assert.Equal(-32002, ErrorCode(readSession.Responses[5]));
    Assert.Equal(-32002, ErrorCode(readSession.Responses[6]));
}

static async Task MalformedAndOversizedRecoveryAsync()
{
    var oversized = new string('x', McpProtocol.MaximumRequestBytes + 1);
    var session = await RunSessionAsync(
        "{not-json",
        oversized,
        InitializeRequest(1),
        InitializedNotification(),
        Request(2, "ping")).ConfigureAwait(false);

    Assert.Equal(4, session.Responses.Count);
    Assert.Equal(-32700, ErrorCode(session.Responses[0]));
    Assert.Equal(-32600, ErrorCode(session.Responses[1]));
    Assert.Equal(McpProtocol.Version, String(Result(session.Responses[2]), "protocolVersion"));
    Assert.Equal(0, Result(session.Responses[3]).Count);
    Assert.Contains("cortex_mcp_message_too_large", session.Stderr);
    AssertProtocolClean(session.Stdout);
}

static async Task RequestByteBoundaryAsciiAsync()
{
    var exact = new string('a', McpProtocol.MaximumRequestBytes);
    using (var input = new StringReader(exact + "\r\nnext\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var accepted = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(accepted.ExceededLimit, "An exact-byte ASCII CRLF frame was rejected.");
        Assert.Equal(exact, accepted.Line);
        var next = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Equal("next", next.Line);
    }

    using (var input = new StringReader(exact + "x\nvalid\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var oversized = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.True(oversized.ExceededLimit, "An over-limit ASCII frame was accepted.");
        Assert.Equal(null, oversized.Line);
        var recovered = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Equal("valid", recovered.Line);
    }

    using (var input = new StringReader(exact))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var finalFrame = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(exact, finalFrame.Line);
        var end = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.True(end.EndOfStream, "EOF after an unterminated final frame was not reported.");
    }

    using (var input = new StringReader(exact + "\r"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var legacyFinalFrame = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(exact, legacyFinalFrame.Line);
    }

    using (var input = new StringReader(exact + "\rX\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var internalCarriageReturn = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.True(internalCarriageReturn.ExceededLimit, "An internal CR bypassed the request-byte limit.");
    }

    using (var input = new ChunkedTextReader("aaaa\r\n", maximumChunkCharacters: 5))
    {
        var reader = new BoundedJsonLineReader(input, maximumBytes: 4);
        var splitCrLf = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Equal("aaaa", splitCrLf.Line);
    }
}

static async Task RequestByteBoundaryMultibyteAsync()
{
    const string scalar = "\U0001F600";
    var exact = string.Concat(Enumerable.Repeat(scalar, McpProtocol.MaximumRequestBytes / 4));
    Assert.Equal(McpProtocol.MaximumRequestBytes, Encoding.UTF8.GetByteCount(exact));

    using (var input = new StringReader(exact[..^2] + "\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var under = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(under.ExceededLimit, "An under-limit multibyte frame was rejected.");
    }
    using (var input = new StringReader(exact + "\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var at = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(at.ExceededLimit, "An exact-byte multibyte frame was rejected.");
        Assert.Equal(exact, at.Line);
    }
    using (var input = new StringReader(exact + scalar + "\n"))
    {
        var reader = new BoundedJsonLineReader(input, McpProtocol.MaximumRequestBytes);
        var over = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.True(over.ExceededLimit, "An over-limit multibyte frame was accepted.");
    }

    var splitAcrossChunks = new string('a', 4095) + scalar +
        new string('a', McpProtocol.MaximumRequestBytes - 4095 - 4);
    Assert.Equal(McpProtocol.MaximumRequestBytes, Encoding.UTF8.GetByteCount(splitAcrossChunks));
    using var chunkedInput = new ChunkedTextReader(splitAcrossChunks + "\n", maximumChunkCharacters: 4096);
    var chunkedReader = new BoundedJsonLineReader(chunkedInput, McpProtocol.MaximumRequestBytes);
    var split = await chunkedReader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
    Assert.False(split.ExceededLimit, "A surrogate pair split across TextReader chunks was rejected.");
    Assert.Equal(splitAcrossChunks, split.Line);
}

static Task OversizedResponseFormattingAsync()
{
    var response = McpProtocol.Success(
        JsonValue.Create(17),
        new JsonObject { ["value"] = new string('x', McpProtocol.MaximumResponseBytes + 1) });
    var formatted = BoundedJsonResponseFormatter.Format(response);

    Assert.True(formatted.ReplacedOversizedResponse, "Oversized response was not replaced.");
    Assert.True(
        Encoding.UTF8.GetByteCount(formatted.Json) <= McpProtocol.MaximumResponseBytes,
        "Replacement response exceeded the byte ceiling.");
    var error = JsonNode.Parse(formatted.Json)!.AsObject();
    Assert.Equal(17, Integer(error, "id"));
    Assert.Equal(-32603, ErrorCode(error));
    Assert.Contains("cortex_mcp_response_too_large", formatted.Json);
    return Task.CompletedTask;
}

static Task PooledResponseBufferCeilingAsync()
{
    using var buffer = new BoundedByteBufferWriter(10);
    Assert.Equal(10, buffer.GetSpan().Length);
    buffer.Advance(10);
    Assert.Throws<JsonResponseLimitExceededException>(() => buffer.GetMemory(1));
    Assert.Throws<JsonResponseLimitExceededException>(() => buffer.Advance(1));
    return Task.CompletedTask;
}

static async Task InvalidUtf8TerminatesSessionAsync()
{
    var validInput = string.Join('\n', InitializeRequest(1), InitializedNotification(), Request(2, "ping")) + "\n";
    var input = new RecoveringDecoderTextReader(validInput);
    var session = await RunReaderSessionAsync(input).ConfigureAwait(false);
    AssertUtf8Terminal(session);

    foreach (var invalidText in new[]
             {
                 "\ud800\n" + validInput,
                 "\udc00\n" + validInput,
                 "\ud800",
                 new string('x', McpProtocol.MaximumRequestBytes + 1) + "\ud800\n" + validInput
             })
    {
        AssertUtf8Terminal(await RunReaderSessionAsync(new StringReader(invalidText)).ConfigureAwait(false));
    }

    var invalidByteSequences = new[]
    {
        new byte[] { 0x80 },
        new byte[] { 0xc0, 0xaf },
        new byte[] { 0xed, 0xa0, 0x80 },
        new byte[] { 0xf4, 0x90, 0x80, 0x80 },
        new byte[] { 0xe2, 0x82, 0x0a },
        new byte[] { 0xf0, 0x9f, 0x98 },
        new byte[] { 0xff, 0xfe, 0x7b, 0x00 }
    };
    foreach (var invalidBytes in invalidByteSequences)
        AssertUtf8Terminal(await RunUtf8BytesSessionAsync(invalidBytes).ConfigureAwait(false));

    var bomPrefixed = new byte[] { 0xef, 0xbb, 0xbf }
        .Concat(Encoding.UTF8.GetBytes(InitializeRequest(1) + "\n" + InitializeRequest(2) + "\n"))
        .ToArray();
    var bomSession = await RunUtf8BytesSessionAsync(bomPrefixed).ConfigureAwait(false);
    Assert.Equal(2, bomSession.Responses.Count);
    Assert.Equal(-32700, ErrorCode(bomSession.Responses[0]));
    Assert.Equal(McpProtocol.Version, String(Result(bomSession.Responses[1]), "protocolVersion"));
}

static async Task NotificationBehaviorAsync()
{
    var session = await RunSessionAsync(
        InitializeRequest(1),
        InitializedNotification(),
        Notification("notifications/cancelled", new JsonObject { ["requestId"] = 99, ["reason"] = "test" }),
        Notification("notifications/example", new JsonObject()),
        Request(2, "ping"),
        InitializeRequest(3)).ConfigureAwait(false);

    Assert.Equal(3, session.Responses.Count);
    Assert.Equal(1, Integer(session.Responses[0], "id"));
    Assert.Equal(2, Integer(session.Responses[1], "id"));
    Assert.Equal(-32600, ErrorCode(session.Responses[2]));
}

static async Task CancellationAsync()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var server = await CreateServerAsync(
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter()).ConfigureAwait(false);
    await Assert.ThrowsAsync<OperationCanceledException>(() => server.RunAsync(cancellation.Token)).ConfigureAwait(false);

    using var drainCancellation = new CancellationTokenSource();
    using var input = new CancelAfterFirstReadTextReader("xxxxx", drainCancellation);
    var reader = new BoundedJsonLineReader(input, maximumBytes: 4);
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => reader.ReadAsync(drainCancellation.Token).AsTask()).ConfigureAwait(false);

    var blockingInput = new NonCooperativeBlockingTextReader();
    using var blockedCancellation = new CancellationTokenSource();
    var blockingReader = new BoundedJsonLineReader(
        blockingInput,
        McpProtocol.MaximumRequestBytes);
    var blockedRead = blockingReader.ReadAsync(blockedCancellation.Token).AsTask();
    try
    {
        await blockingInput.Started.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        blockedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => blockedRead.WaitAsync(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => blockingReader.ReadAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
    }
    finally
    {
        // Release the deliberately non-cooperative read after the cancellation
        // assertion so its fixture task cannot outlive the test process.
        blockingInput.ReleaseAsEndOfStream();
        await blockingInput.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            await blockedRead.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (blockedCancellation.IsCancellationRequested)
        {
            // The expected cancellation has now been fully observed.
        }
    }
}

static bool IsUnverifiedNode(JsonNode? node) => IsUnverifiedObject(node!.AsObject());

static bool IsUnverifiedObject(JsonObject node) =>
    string.Equals(String(node["freshness"]!.AsObject(), "state"), "unverified", StringComparison.Ordinal);

static async Task<SessionResult> RunReadySessionAsync(params string[] requests)
{
    var messages = new List<string> { InitializeRequest(1), InitializedNotification() };
    messages.AddRange(requests);
    return await RunSessionAsync(messages.ToArray()).ConfigureAwait(false);
}

static Task<SessionResult> RunSessionAsync(params string[] messages)
{
    var input = new StringReader(string.Join('\n', messages) + "\n");
    return RunReaderSessionAsync(input);
}

static async Task<SessionResult> RunReaderSessionAsync(TextReader input)
{
    using (input)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var server = await CreateServerAsync(input, output, diagnostics).ConfigureAwait(false);
        await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        var stdout = output.ToString();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(
                Encoding.UTF8.GetByteCount(line) <= McpProtocol.MaximumResponseBytes,
                "Protocol output exceeded the enforced response byte ceiling.");
        }
        var responses = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Protocol output was not JSON."))
            .ToArray();
        return new SessionResult(responses, stdout, diagnostics.ToString());
    }
}

static Task<SessionResult> RunUtf8BytesSessionAsync(byte[] input)
{
    var stream = new MemoryStream(input, writable: false);
    return RunReaderSessionAsync(McpUtf8Input.Create(stream, leaveOpen: false));
}

static async Task<McpStdioServer> CreateServerAsync(
    TextReader input,
    TextWriter output,
    TextWriter diagnostics)
{
    var source = DefaultLibraryQuerySource.CreateVerified();
    var service = new CortexQueryService(
        source,
        new CortexQueryServiceOptions(
            DefaultKnowledgeLibrary.DefaultNamespace,
            "isolated-read-only",
            "verified-at-startup"));
    var authority = new CortexQueryAuthority(
        "local",
        "pegasus-cortex-mcp-tests",
        "local-test",
        "pegasus-cortex-mcp-tests",
        policyRevision: 1,
        [new CortexQueryNamespaceGrant(
            DefaultKnowledgeLibrary.DefaultNamespace,
            "internal",
            [
                CortexQueryCapability.Discover,
                CortexQueryCapability.Status,
                CortexQueryCapability.Catalog,
                CortexQueryCapability.Search,
                CortexQueryCapability.Read
            ],
            allowStale: true,
            allowBlockedOrUnverified: true)]);
    var application = await CortexMcpApplication.CreateAsync(
        service,
        source,
        authority,
        DefaultKnowledgeLibrary.DefaultNamespace,
        CancellationToken.None).ConfigureAwait(false);
    return new McpStdioServer(application, input, output, diagnostics);
}

static string InitializeRequest(int id, string? protocolVersion = null) =>
    Request(id, "initialize", new JsonObject
    {
        ["protocolVersion"] = protocolVersion ?? McpProtocol.Version,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = ClientInfo()
    });

static JsonObject ClientInfo() => new() { ["name"] = "pegasus-cortex-tests", ["version"] = "1" };

static string InitializedNotification() => Notification("notifications/initialized", new JsonObject());

static string CallTool(int id, string name, JsonObject arguments) =>
    Request(id, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });

static string Request(int id, string method, JsonObject? parameters = null)
{
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
    if (parameters is not null)
        request["params"] = parameters;
    return request.ToJsonString();
}

static string Notification(string method, JsonObject parameters) =>
    new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters
    }.ToJsonString();

static JsonObject Result(JsonObject response) => response["result"]?.AsObject() ??
    throw new InvalidOperationException($"Response has no result: {response}");

static JsonObject Structured(JsonObject response) =>
    Result(response)["structuredContent"]?.AsObject() ??
    throw new InvalidOperationException($"Tool response has no structured content: {response}");

static int ErrorCode(JsonObject response) => Integer(response["error"]?.AsObject() ??
    throw new InvalidOperationException($"Response has no error: {response}"), "code");

static void AssertToolError(JsonObject response, string expectedCode)
{
    var result = Result(response);
    Assert.True(Boolean(result, "isError"), "Tool failure was not marked as an error.");
    var diagnostic = result["structuredContent"]!["diagnostic"]!.AsObject();
    Assert.Equal(expectedCode, String(diagnostic, "code"));
    Assert.Equal("error", String(diagnostic, "severity"));
    Assert.False(Boolean(diagnostic, "retryable"), "A validation failure was marked retryable.");
}

static void AssertResourceJson(JsonObject response)
{
    var contents = Result(response)["contents"]!.AsArray();
    Assert.Equal(1, contents.Count);
    var content = contents[0]!.AsObject();
    Assert.Equal("application/json", String(content, "mimeType"));
    Assert.NotNull(JsonNode.Parse(String(content, "text")), "Resource text is not JSON.");
}

static void AssertProtocolClean(string stdout)
{
    foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var parsed = JsonNode.Parse(line)?.AsObject();
        Assert.NotNull(parsed, "Stdout contained a non-protocol line.");
        Assert.Equal("2.0", String(parsed!, "jsonrpc"));
    }
}

static void AssertUtf8Terminal(SessionResult session)
{
    Assert.Equal(1, session.Responses.Count);
    Assert.Equal(-32700, ErrorCode(session.Responses[0]));
    Assert.Contains("cortex_mcp_utf8_invalid", session.Stderr);
    AssertProtocolClean(session.Stdout);
}

static string String(JsonObject value, string propertyName) =>
    value[propertyName]?.GetValue<string>() ?? throw new InvalidOperationException($"Missing string property {propertyName}.");

static int Integer(JsonObject value, string propertyName) =>
    value[propertyName]?.GetValue<int>() ?? throw new InvalidOperationException($"Missing integer property {propertyName}.");

static bool Boolean(JsonObject value, string propertyName) =>
    value[propertyName]?.GetValue<bool>() ?? throw new InvalidOperationException($"Missing boolean property {propertyName}.");

static CortexMcpRuntimeComposition RuntimeContract(
    CortexMcpRuntimeCapability capabilities) =>
    new(
        new CortexMcpRuntimeDescriptor(
            "integrated-test",
            "ready",
            IssueCode: null,
            "endpoint-test",
            "principal-test",
            "subject-test",
            capabilities,
            UsesNetwork: true,
            UsesDatabase: true,
            UsesAuthentication: true,
            UsesLiveServices: true,
            "Test-only runtime descriptor.",
            StorageKind: CortexMcpStorageKinds.PostgreSql),
        Port<ICortexProjectKnowledgeRuntime>(),
        Port<ICortexProjectCorrelationRuntime>(),
        Port<ICortexProposalSubmissionRuntime>(),
        Port<ICortexProposalQueryRuntime>(),
        Port<ICortexProposalReviewRuntime>(),
        Port<ICortexProposalAuditRuntime>(),
        Port<ICortexReviewGrantRuntime>(),
        Port<ICortexProjectBriefRuntime>(),
        PublicationChange: Port<ICortexPublicationChangeRuntime>());

static T Port<T>() where T : class =>
    DispatchProxy.Create<T, RuntimePortProxy>();

sealed record SessionResult(IReadOnlyList<JsonObject> Responses, string Stdout, string Stderr);

internal class RuntimePortProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException(
            $"The contract-only runtime port cannot invoke '{targetMethod?.Name}'.");
}

sealed class RecoveringDecoderTextReader(string remainingInput) : TextReader
{
    private readonly StringReader _remaining = new(remainingInput);
    private bool _failed;

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_failed)
        {
            _failed = true;
            return ValueTask.FromException<int>(new DecoderFallbackException("Injected invalid UTF-8 sequence."));
        }

        return _remaining.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _remaining.Dispose();
        base.Dispose(disposing);
    }
}

sealed class ChunkedTextReader(string input, int maximumChunkCharacters) : TextReader
{
    private int _offset;

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_offset >= input.Length)
            return ValueTask.FromResult(0);
        var count = Math.Min(Math.Min(buffer.Length, maximumChunkCharacters), input.Length - _offset);
        input.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return ValueTask.FromResult(count);
    }
}

sealed class CancelAfterFirstReadTextReader(
    string input,
    CancellationTokenSource cancellation) : TextReader
{
    private bool _read;

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_read)
            return ValueTask.FromResult(0);
        if (input.Length > buffer.Length)
            throw new InvalidOperationException("The cancellation fixture exceeds its one-read buffer.");
        input.AsMemory().CopyTo(buffer);
        _read = true;
        cancellation.Cancel();
        return ValueTask.FromResult(input.Length);
    }
}

sealed class NonCooperativeBlockingTextReader : TextReader
{
    private readonly TaskCompletionSource<bool> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public Task Completion => _completion.Task;

    public override ValueTask<int> ReadAsync(
        Memory<char> buffer,
        CancellationToken cancellationToken = default)
    {
        _started.TrySetResult(true);
        return new ValueTask<int>(_completion.Task);
    }

    public void ReleaseAsEndOfStream() => _completion.TrySetResult(0);
}

static class Assert
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
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text containing '{expectedSubstring}'.");
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
            throw new InvalidOperationException(message);
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
