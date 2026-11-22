using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Query;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Diagnostics;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.Knowledge;

public sealed class CortexMcpApplication : ICortexMcpProtocolApplication
{
    public const string StatusResourceUri = "cortex://default-library/status";
    public const string CatalogResourceUri = "cortex://default-library/catalog";
    public const string GuideResourceUri = "cortex://default-library/guide";
    public const string RecordResourceUriTemplate = "cortex://default-library/records/{recordId}";
    private const string RecordResourcePrefix = "cortex://default-library/records/";
    private readonly ICortexQueryService _queryService;
    private readonly ILegacyKnowledgeIdentifierResolver _identifierResolver;
    private readonly CortexQueryAuthority _authority;
    private readonly string _defaultNamespace;
    private readonly CortexKnowledgeCatalog _resourceCatalog;
    private readonly IReadOnlySet<(string RecordId, string RevisionId)> _resourceIdentities;
    private readonly IReadOnlySet<string> _resourceRecordIds;
    private readonly McpServerBuildIdentity _serverBuildIdentity;
    private readonly CortexMcpRuntimeComposition? _runtime;

    internal sealed record RoutedSearchHit(
        CortexKnowledgeSearchHit Hit,
        string? ProjectLinkId);

    private CortexMcpApplication(
        ICortexQueryService queryService,
        ILegacyKnowledgeIdentifierResolver identifierResolver,
        CortexQueryAuthority authority,
        string defaultNamespace,
        CortexKnowledgeCatalog resourceCatalog,
        McpServerBuildIdentity serverBuildIdentity,
        CortexMcpRuntimeComposition? runtime)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _identifierResolver = identifierResolver ?? throw new ArgumentNullException(nameof(identifierResolver));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _defaultNamespace = string.IsNullOrWhiteSpace(defaultNamespace)
            ? throw new ArgumentException("A default Cortex namespace is required.", nameof(defaultNamespace))
            : defaultNamespace;
        _resourceCatalog = SnapshotResourceCatalog(resourceCatalog, _defaultNamespace);
        _resourceIdentities = _resourceCatalog.Records
            .Select(record => (record.RecordId, record.RevisionId))
            .ToFrozenSet();
        _resourceRecordIds = _resourceCatalog.Records
            .Select(record => record.RecordId)
            .ToFrozenSet(StringComparer.Ordinal);
        _serverBuildIdentity = serverBuildIdentity ??
            throw new ArgumentNullException(nameof(serverBuildIdentity));
        _runtime = runtime;
    }

    /// <summary>
    /// Creates one session-stable resource view through the Core service. The bounded
    /// catalog is captured asynchronously before the request loop starts, so constructors
    /// perform no I/O and later calls neither drift nor repeat catalog work.
    /// </summary>
    public static async ValueTask<CortexMcpApplication> CreateAsync(
        ICortexQueryService queryService,
        ILegacyKnowledgeIdentifierResolver identifierResolver,
        CortexQueryAuthority authority,
        string defaultNamespace,
        CancellationToken cancellationToken)
        => await CreateAsync(
            queryService,
            identifierResolver,
            authority,
            defaultNamespace,
            runtime: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a read-only MCP application with a provider-neutral project-brief
    /// runtime. The runtime supplies already-authorized read-model state; the MCP
    /// application does not crawl project files or acquire task ownership.
    /// </summary>
    public static async ValueTask<CortexMcpApplication> CreateAsync(
        ICortexQueryService queryService,
        ILegacyKnowledgeIdentifierResolver identifierResolver,
        CortexQueryAuthority authority,
        string defaultNamespace,
        ICortexProjectBriefRuntime projectBriefRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectBriefRuntime);
        return await CreateAsync(
            queryService,
            identifierResolver,
            authority,
            defaultNamespace,
            new CortexMcpRuntimeComposition(
                CortexMcpRuntimeDescriptor.PortableProjectBrief(),
                ProjectBrief: projectBriefRuntime),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an MCP application from explicitly selected provider-neutral
    /// runtime ports. This method performs no provider discovery or activation.
    /// </summary>
    public static async ValueTask<CortexMcpApplication> CreateAsync(
        ICortexQueryService queryService,
        ILegacyKnowledgeIdentifierResolver identifierResolver,
        CortexQueryAuthority authority,
        string defaultNamespace,
        CortexMcpRuntimeComposition? runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryService);
        ArgumentNullException.ThrowIfNull(identifierResolver);
        ArgumentNullException.ThrowIfNull(authority);
        if (string.IsNullOrWhiteSpace(defaultNamespace))
            throw new ArgumentException("A default Cortex namespace is required.", nameof(defaultNamespace));
        runtime?.Descriptor.Validate();

        cancellationToken.ThrowIfCancellationRequested();
        var result = await queryService.ListCatalogAsync(
            new CortexCatalogQuery(defaultNamespace, CortexMcpContract.MaximumResourceRecords),
            authority,
            cancellationToken).ConfigureAwait(false);
        var catalog = RequireAccepted(result, "catalog composition");
        CortexContinuityGuide.Verify();
        var serverBuildIdentity = McpServerBuildIdentityProvider.CaptureEntryAssembly();
        return new CortexMcpApplication(
            queryService,
            identifierResolver,
            authority,
            defaultNamespace,
            catalog,
            serverBuildIdentity,
            runtime);
    }

    public JsonArray ListTools() => CortexMcpContract.ListTools(_runtime);

    /// <summary>
    /// Checks the complete structured MCP project-brief projection, including
    /// its passive-data safety fields, against the same UTF-8 budget enforced
    /// by the tool. Producers can use this before persisting a derived hot view
    /// that would otherwise be retained but unreadable without truncation.
    /// </summary>
    public static bool CanRepresentProjectBriefWithoutTruncation(
        CortexProjectBriefResult result,
        int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maximumUtf8Bytes is <
                CortexProjectBriefContract.MinimumMaximumUtf8Bytes or
                > CortexProjectBriefContract.MaximumUtf8Bytes ||
            !result.Accepted)
        {
            return false;
        }
        return Utf8Bytes(BuildProjectBrief(result)) <= maximumUtf8Bytes;
    }

    public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resources = new JsonArray
        {
            CortexMcpContract.ResourceDefinition(
                StatusResourceUri,
                "Default Library status",
                "Validated release and bounded availability status.")
        };
        if (CanReadKnowledge)
        {
            resources.Add(CortexMcpContract.ResourceDefinition(
                CatalogResourceUri,
                "Default Library catalog",
                "Default-readable knowledge record summaries and exact resource identifiers."));
        }
        resources.Add(CortexMcpContract.ResourceDefinition(
            GuideResourceUri,
            "Project continuity guide",
            "Versioned, hash-bound guidance for hybrid project continuity and retained Cortex knowledge."));

        return ValueTask.FromResult(resources);
    }

    public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CanReadKnowledge
            ? new JsonArray
            {
                CortexMcpContract.ResourceTemplateDefinition(
                RecordResourceUriTemplate,
                "Default Library record",
                "Reads one exact default-readable knowledge record by its catalog recordId.")
            }
            : new JsonArray());
    }

    public async ValueTask<JsonObject?> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        cancellationToken.ThrowIfCancellationRequested();

        JsonObject? document;
        if (string.Equals(uri, StatusResourceUri, StringComparison.Ordinal))
        {
            document = await BuildStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (CanReadKnowledge &&
                 string.Equals(uri, CatalogResourceUri, StringComparison.Ordinal))
        {
            document = BuildCatalog(_resourceCatalog);
        }
        else if (string.Equals(uri, GuideResourceUri, StringComparison.Ordinal))
        {
            document = CortexContinuityGuide.BuildDocument();
        }
        else if (CanReadKnowledge && TryParseRecordResourceUri(uri, out var recordId))
        {
            if (!_resourceRecordIds.Contains(recordId!))
                return null;

            var identity = await _identifierResolver.ResolveAsync(recordId!, cancellationToken).ConfigureAwait(false);
            if (identity is null ||
                !string.Equals(identity.Namespace, _defaultNamespace, StringComparison.Ordinal) ||
                !string.Equals(identity.RecordId, recordId, StringComparison.Ordinal) ||
                !_resourceIdentities.Contains((identity.RecordId, identity.RevisionId)))
            {
                return null;
            }
            var read = await _queryService.ReadAsync(
                new CortexRecordReadQuery(
                    identity.Namespace,
                    identity.RecordId,
                    identity.RevisionId,
                    IncludeStale: false,
                    IncludeBlockedOrUnverified: false),
                _authority,
                cancellationToken).ConfigureAwait(false);
            document = IsCleanAccepted(read) && CanProjectLegacyRecord(read.Value!)
                ? BuildRecord(read.Value!)
                : null;
        }
        else
        {
            return null;
        }

        if (document is null)
            return null;

        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "application/json",
                    ["text"] = document.ToJsonString(McpProtocol.JsonOptions)
                }
            }
        };
    }

    public async ValueTask<JsonObject> CallToolAsync(
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ToolAvailable(name))
        {
            return ToolError(
                "cortex_tool_unknown",
                "The requested Cortex tool is not available to this authenticated principal.",
                "cortex.tool.call");
        }
        return name switch
        {
            "cortex_discover" => await CallNoArgumentToolAsync(
                arguments,
                "cortex.discover",
                BuildDiscoveryAsync,
                cancellationToken).ConfigureAwait(false),
            "cortex_status" => await CallNoArgumentToolAsync(
                arguments,
                "cortex.status",
                BuildStatusAsync,
                cancellationToken).ConfigureAwait(false),
            "cortex_search" => await SearchAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_read" => await ReadAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_project_brief" when _runtime?.ProjectBrief is not null =>
                await ProjectBriefAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_submit_task_snapshot" when _runtime?.RetainedAssertionWriter is not null =>
                await SubmitTaskSnapshotAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_submit_work_packet" when _runtime?.RetainedAssertionWriter is not null =>
                await SubmitWorkPacketAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_submit_work_packet_handoff_receipt"
                when _runtime?.RetainedAssertionWriter is not null =>
                await SubmitWorkPacketHandoffReceiptAsync(
                    arguments,
                    cancellationToken).ConfigureAwait(false),
            "cortex_submit_work_packet_acceptance_receipt"
                when _runtime?.RetainedAssertionWriter is not null =>
                await SubmitWorkPacketAcceptanceReceiptAsync(
                    arguments,
                    cancellationToken).ConfigureAwait(false),
            "cortex_read_retained_record" when _runtime?.RetainedAssertionReader is not null =>
                await ReadRetainedAssertionAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_correlate_project" when _runtime?.ProjectCorrelation is not null =>
                await CorrelateProjectAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_submit_proposal" when _runtime?.ProposalSubmission is not null =>
                await SubmitProposalAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_list_proposals" when _runtime?.ProposalQuery is not null =>
                await ListProposalsAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_change_publication" when _runtime?.PublicationChange is not null =>
                await ChangePublicationAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_review_proposal" when _runtime?.ProposalReview is not null =>
                await ReviewProposalAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_submit_audit" when _runtime?.ProposalAudit is not null =>
                await SubmitAuditAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cortex_manage_review_grant" when _runtime?.ReviewGrant is not null =>
                await ManageReviewGrantAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ when _runtime?.Operations is not null &&
                CortexOperationalMcpTools.IsOperationalTool(name) =>
                await CortexOperationalMcpTools.CallAsync(
                    _runtime.Operations,
                    name,
                    arguments,
                    cancellationToken).ConfigureAwait(false),
            _ => ToolError(
                "cortex_tool_unknown",
                "The requested Cortex tool is not available.",
                "cortex.tool.call")
        };
    }

    private bool CanReadKnowledge =>
        _runtime is null || _runtime.HasCapability(CortexMcpRuntimeCapability.Read);

    private bool CanSearchProjectKnowledge =>
        _runtime?.ProjectKnowledge is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.Read);

    private bool CanReadProjectBrief =>
        _runtime?.CanUseProjectBrief ?? false;

    private bool CanReadRetainedState =>
        _runtime?.RetainedAssertionReader is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.RetainedRead);

    private bool CanWriteRetainedState =>
        _runtime?.RetainedAssertionWriter is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.RetainedWrite);

    private bool CanSubmitProposals =>
        _runtime?.ProposalSubmission is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.Propose);

    private bool CanQueryProposals =>
        _runtime?.ProposalQuery is not null &&
        (_runtime.HasCapability(CortexMcpRuntimeCapability.Read) ||
         _runtime.HasCapability(CortexMcpRuntimeCapability.Review) ||
         _runtime.HasCapability(CortexMcpRuntimeCapability.Audit));

    private bool CanReviewProposals =>
        _runtime?.ProposalReview is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.Review);

    private bool CanChangePublications =>
        _runtime?.PublicationChange is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.Propose);

    private bool CanAuditProposals =>
        _runtime?.ProposalAudit is not null &&
        _runtime.HasCapability(CortexMcpRuntimeCapability.Audit);

    private bool RuntimeWritesAvailable =>
        _runtime is not null &&
        (_runtime.ProjectCorrelation is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.Correlate) ||
         _runtime.ProposalSubmission is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.Propose) ||
         CanChangePublications ||
         _runtime.ProposalReview is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.Review) ||
         _runtime.ProposalAudit is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.Audit) ||
         _runtime.ReviewGrant is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.DelegateReview) ||
         _runtime.RetainedAssertionWriter is not null &&
         _runtime.HasCapability(CortexMcpRuntimeCapability.RetainedWrite) ||
         _runtime.Operations is not null &&
         (_runtime.HasCapability(CortexMcpRuntimeCapability.Tasks) ||
          _runtime.HasCapability(CortexMcpRuntimeCapability.KnowledgeWrite)));

    private bool ToolAvailable(string name) => name switch
    {
        "cortex_discover" or "cortex_status" => true,
        "cortex_search" or "cortex_read" => CanReadKnowledge,
        "cortex_project_brief" =>
            _runtime?.CanUseProjectBrief == true,
        "cortex_submit_task_snapshot" or
        "cortex_submit_work_packet" or
        "cortex_submit_work_packet_handoff_receipt" or
        "cortex_submit_work_packet_acceptance_receipt" =>
            _runtime?.RetainedAssertionWriter is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.RetainedWrite),
        "cortex_read_retained_record" =>
            _runtime?.RetainedAssertionReader is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.RetainedRead),
        "cortex_correlate_project" =>
            _runtime?.ProjectCorrelation is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.Correlate),
        "cortex_submit_proposal" =>
            _runtime?.ProposalSubmission is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.Propose),
        "cortex_list_proposals" =>
            _runtime?.ProposalQuery is not null &&
            (_runtime.HasCapability(CortexMcpRuntimeCapability.Read) ||
             _runtime.HasCapability(CortexMcpRuntimeCapability.Review) ||
              _runtime.HasCapability(CortexMcpRuntimeCapability.Audit)),
        "cortex_change_publication" => CanChangePublications,
        "cortex_review_proposal" =>
            _runtime?.ProposalReview is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.Review),
        "cortex_submit_audit" =>
            _runtime?.ProposalAudit is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.Audit),
        "cortex_manage_review_grant" =>
            _runtime?.ReviewGrant is not null &&
            _runtime.HasCapability(CortexMcpRuntimeCapability.DelegateReview),
        _ when _runtime?.Operations is not null &&
            CortexOperationalMcpTools.IsOperationalTool(name) =>
            name.StartsWith("cortex_task_", StringComparison.Ordinal)
                ? _runtime.HasCapability(CortexMcpRuntimeCapability.Tasks)
                : _runtime.HasCapability(CortexMcpRuntimeCapability.KnowledgeWrite),
        _ => false
    };

    private async ValueTask<JsonObject> CallNoArgumentToolAsync(
        JsonElement? arguments,
        string operation,
        Func<CancellationToken, ValueTask<JsonObject>> action,
        CancellationToken cancellationToken)
    {
        if (!ToolArgumentReader.Validate(arguments, CortexMcpContract.NoArguments, out var argumentError))
            return ToolError("cortex_arguments_invalid", argumentError!, operation);
        return ToolSuccess(await action(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<JsonObject> SearchAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.search";
        if (!ToolArgumentReader.Validate(arguments, CortexMcpContract.SearchArguments, out var argumentError))
            return ToolError("cortex_search_arguments_invalid", argumentError!, operation);

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "text",
                CortexMcpContract.MaximumSearchTextCharacters,
                out var text,
                out argumentError) ||
            string.IsNullOrWhiteSpace(text))
        {
            return ToolError(
                "cortex_search_text_invalid",
                argumentError ?? "Search text is required.",
                operation);
        }

        if (!ToolArgumentReader.OptionalString(values, "taxonomy", CortexMcpContract.MaximumFilterCharacters, out var taxonomy, out argumentError) ||
            !ToolArgumentReader.OptionalString(values, "productId", CortexMcpContract.MaximumFilterCharacters, out var productId, out argumentError) ||
             !ToolArgumentReader.OptionalString(values, "componentId", CortexMcpContract.MaximumFilterCharacters, out var componentId, out argumentError) ||
             !ToolArgumentReader.OptionalInt32(values, "limit", 5, 1, CortexMcpContract.MaximumSearchResults, out var limit, out argumentError) ||
             !ToolArgumentReader.OptionalString(values, "mode", 16, out var modeValue, out argumentError) ||
             !TryReadProjection(values, out var compactProjection, out argumentError) ||
             !ToolArgumentReader.OptionalBoolean(values, "includeContent", out var includeContent, out argumentError) ||
             !ToolArgumentReader.OptionalBoolean(values, "includeStale", out var includeStale, out argumentError) ||
             !ToolArgumentReader.OptionalBoolean(values, "includeBlockedOrUnverified", out var includeBlocked, out argumentError) ||
             !ToolArgumentReader.OptionalString(
                 values,
                 "projectLinkId",
                 CortexMcpContract.MaximumIdentifierCharacters,
                 out var projectLinkId,
                 out argumentError))
        {
            return ToolError("cortex_search_arguments_invalid", argumentError!, operation);
        }
        var mode = modeValue switch
        {
            null or CortexMcpContract.ExactSearchMode => CortexSearchMode.Exact,
            CortexMcpContract.BroadSearchMode => CortexSearchMode.Broad,
            _ => (CortexSearchMode?)null
        };
        if (mode is null)
        {
            return ToolError(
                "cortex_search_arguments_invalid",
                "The mode argument must be one of the supported values.",
                operation);
        }
        if (projectLinkId is not null && !ValidPrefixedHash(projectLinkId, "plink_"))
            return ToolError("cortex_search_arguments_invalid", "The projectLinkId is invalid.", operation);

        if (includeContent && !compactProjection)
        {
            return ToolError(
                "cortex_search_arguments_invalid",
                $"includeContent requires projection '{CortexMcpContract.CompactProjection}'.",
                operation);
        }

        if (includeContent && limit > CortexMcpContract.MaximumHydratedSearchResults)
        {
            return ToolError(
                "cortex_search_arguments_invalid",
                $"includeContent limits search results to at most {CortexMcpContract.MaximumHydratedSearchResults}.",
                operation);
        }

        var lexicalMode = mode == CortexSearchMode.Broad
            ? CortexSearchModes.Broad
            : CortexSearchModes.Exact;
        var queryTermCount = CortexLexicalSearch.NormalizeTerms(text!, lexicalMode).Count;
        IReadOnlyList<RoutedSearchHit> routedRecords;
        string route;
        int queriedSourceCount;
        var partialFailureCount = 0;
        if (projectLinkId is not null)
        {
            var projectKnowledge = _runtime?.ProjectKnowledge;
            if (projectKnowledge is null || !CanSearchProjectKnowledge)
            {
                return ToolError(
                    "cortex_project_linked_search_unsupported",
                    "The selected Cortex runtime does not advertise project-linked search.",
                    operation);
            }
            var projectResult = await projectKnowledge.SearchProjectAsync(
                projectLinkId,
                text!,
                taxonomy,
                productId,
                componentId,
                limit,
                mode.Value,
                includeStale,
                includeBlocked,
                cancellationToken).ConfigureAwait(false);
            if (projectResult is null || !projectResult.IsValid ||
                string.Equals(
                    projectResult.Disposition,
                    CortexProjectSearchResult.ProviderFailure,
                    StringComparison.Ordinal))
            {
                return ToolError(
                    "cortex_project_linked_search_provider_failure",
                    "The advertised project-linked search provider did not return a valid bounded result.",
                    operation);
            }
            if (string.Equals(
                    projectResult.Disposition,
                    CortexProjectSearchResult.LinkRejected,
                    StringComparison.Ordinal))
            {
                return ToolError(
                    "cortex_project_link_rejected",
                    "The correlated project link is unavailable or rejected for this principal.",
                    operation);
            }
            routedRecords = projectResult.Page!.Records
                .Select(record => new RoutedSearchHit(record, projectLinkId))
                .ToArray();
            route = "project-linked";
            queriedSourceCount = 1;
        }
        else
        {
            var query = new CortexSearchQuery(
                _defaultNamespace,
                text!,
                taxonomy,
                productId,
                componentId,
                limit,
                includeStale,
                includeBlocked,
                mode.Value);
            var search = await _queryService.SearchAsync(query, _authority, cancellationToken)
                .ConfigureAwait(false);
            if (!IsCleanAccepted(search))
            {
                var diagnostic = search.Diagnostics?.FirstOrDefault();
                if (!search.Accepted &&
                    string.Equals(diagnostic?.Code, "cortex_search_taxonomy_unknown", StringComparison.Ordinal))
                {
                    return ToolError(
                        "cortex_search_taxonomy_unknown",
                        "The requested taxonomy is not in the verified Default Library.",
                        operation);
                }

                return ToolError(
                    "cortex_search_rejected",
                    "The bounded knowledge query was rejected.",
                    operation);
            }

            var candidates = search.Value!.Records
                .Select(record => new RoutedSearchHit(record, ProjectLinkId: null))
                .ToList();
            route = "default-library";
            queriedSourceCount = 1;
            if (CanSearchProjectKnowledge &&
                _runtime?.ProjectKnowledge is { } projectKnowledge)
            {
                var collective = await projectKnowledge.SearchCollectiveAsync(
                    text!,
                    taxonomy,
                    productId,
                    componentId,
                    limit,
                    mode.Value,
                    includeStale,
                    includeBlocked,
                    cancellationToken).ConfigureAwait(false);
                if (collective is null || !collective.IsValid)
                {
                    return ToolError(
                        "cortex_collective_search_provider_failure",
                        "The advertised collective-search provider did not return a valid bounded result.",
                        operation);
                }
                candidates.AddRange(collective.Hits.Select(
                    item => new RoutedSearchHit(item.Hit, item.ProjectLinkId)));
                route = "general-collective";
                queriedSourceCount += collective.QueriedSourceCount;
                partialFailureCount = collective.PartialFailureCount;
            }
            routedRecords = MergeGeneralSearchResults(candidates, limit);
        }

        if (includeContent &&
            !FitsHydratedContentBudget(
                routedRecords.Select(item => item.Hit).ToArray(),
                CortexMcpContract.MaximumHydratedContentUtf8Bytes))
        {
            return ToolError(
                "cortex_search_hydration_too_large",
                "The selected exact content exceeds the bounded hydration budget. Reduce the result limit or narrow the query.",
                operation);
        }

        var records = new JsonArray();
        foreach (var routed in routedRecords)
        {
            // Default Library records have no project origin. An explicitly
            // linked query may expose its authorized project scope, but a
            // general collective result must not disclose the storage
            // namespace or source project correlation used for row security.
            var exposeSourceScope = projectLinkId is not null || routed.ProjectLinkId is null;
            var record = BuildSearchRecord(
                routed.Hit,
                compactProjection,
                includeContent,
                exposeSourceScope);
            if (projectLinkId is not null && routed.ProjectLinkId is not null)
                record["projectLinkId"] = routed.ProjectLinkId;
            record["matchedTerms"] = JsonSerializer.SerializeToNode(
                routed.Hit.MatchedTerms ?? Array.Empty<string>(),
                McpProtocol.JsonOptions);
            records.Add(record);
        }

        var response = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["text"] = text,
                ["taxonomy"] = taxonomy,
                ["productId"] = productId,
                ["componentId"] = componentId,
                ["limit"] = limit,
                ["includeStale"] = includeStale,
                ["includeBlockedOrUnverified"] = includeBlocked,
                ["projectLinkId"] = projectLinkId
            },
            ["retrieval"] = new JsonObject
            {
                ["route"] = route,
                ["queryTermCount"] = queryTermCount,
                ["queriedSourceCount"] = queriedSourceCount,
                ["partialFailureCount"] = partialFailureCount
            },
            ["count"] = records.Count,
            ["records"] = records,
            ["safetyNotice"] = CortexQueryPolicy.SafetyNotice
        };
        if (projectLinkId is null)
            response["query"]!.AsObject().Remove("projectLinkId");
        // Preserve the legacy exact response shape. Only explicit broad mode adds
        // a wire marker because it changes retrieval semantics.
        if (mode == CortexSearchMode.Broad)
            response["query"]!["mode"] = CortexMcpContract.BroadSearchMode;
        // Full is the compatibility default; only an explicit compact request
        // adds a projection marker and changes record projection.
        if (compactProjection)
            response["projection"] = CortexMcpContract.CompactProjection;
        if (includeContent)
            response["contentIncluded"] = true;

        var result = ToolSuccess(response);
        if (includeContent &&
            !BoundedJsonResponseFormatter.FitsWithinLimit(
                result,
                CortexMcpContract.MaximumHydratedToolResultBytes,
                out _))
        {
            return ToolError(
                "cortex_search_hydration_too_large",
                "The encoded hydrated result exceeds the bounded response budget. Reduce the result limit or narrow the query.",
                operation);
        }

        return result;
    }

    /// <summary>
    /// Merges federated results without treating source-specific score scales as global.
    /// One best result per matching authorized source is reserved before remaining slots
    /// are filled by raw score; the record scores returned to clients remain unchanged.
    /// </summary>
    internal static IReadOnlyList<RoutedSearchHit> MergeGeneralSearchResults(
        IReadOnlyList<RoutedSearchHit> candidates,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (limit < 1 || limit > CortexMcpContract.MaximumSearchResults)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var ranked = candidates
            .GroupBy(item => item.Hit.Record.ContentHash, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Hit.Score)
                .ThenBy(item => item.Hit.Record.RecordId, StringComparer.Ordinal)
                .ThenBy(item => item.Hit.Record.RevisionId, StringComparer.Ordinal)
                .ThenBy(item => item.ProjectLinkId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.Hit.Record.RevisionId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectLinkId, StringComparer.Ordinal)
            .ToArray();

        var selected = ranked
            .GroupBy(item => item.ProjectLinkId ?? string.Empty, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.ProjectLinkId is null ? 0 : 1)
            .ThenByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.Hit.Record.RevisionId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectLinkId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        var selectedContentHashes = selected
            .Select(item => item.Hit.Record.ContentHash)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in ranked)
        {
            if (selected.Count >= limit)
                break;
            if (selectedContentHashes.Add(candidate.Hit.Record.ContentHash))
                selected.Add(candidate);
        }

        return selected
            .OrderByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.Hit.Record.RevisionId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectLinkId, StringComparer.Ordinal)
            .ToArray();
    }

    private async ValueTask<JsonObject> ReadAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.read";
        if (!ToolArgumentReader.Validate(arguments, CortexMcpContract.ReadArguments, out var argumentError))
            return ToolError("cortex_read_arguments_invalid", argumentError!, operation);

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "id",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var identifier,
                out argumentError) ||
            !ToolArgumentReader.IsKnowledgeIdentifier(identifier!))
        {
            return ToolError(
                "cortex_read_identifier_invalid",
                argumentError ?? "A bounded Default Library record or revision identifier is required.",
                operation);
        }

        if (!TryReadProjection(values, out var compactProjection, out argumentError) ||
            !ToolArgumentReader.OptionalBoolean(values, "includeStale", out var includeStale, out argumentError) ||
            !ToolArgumentReader.OptionalBoolean(values, "includeBlockedOrUnverified", out var includeBlocked, out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "revisionId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var revisionId,
                out argumentError))
        {
            return ToolError("cortex_read_arguments_invalid", argumentError!, operation);
        }

        if (projectLinkId is not null)
        {
            var projectKnowledge = _runtime?.ProjectKnowledge;
            if (projectKnowledge is null)
                return RuntimeUnavailable(operation);
            if (!ValidPrefixedHash(projectLinkId, "plink_") ||
                !identifier!.StartsWith("krec_", StringComparison.Ordinal) ||
                revisionId is null ||
                !ValidPrefixedHash(revisionId, "krev_"))
            {
                return ToolError(
                    "cortex_read_arguments_invalid",
                    "Project reads require a valid projectLinkId, krec record id, and krev revisionId.",
                    operation);
            }
            var projectRecord = await projectKnowledge.ReadProjectAsync(
                projectLinkId,
                identifier,
                revisionId,
                includeStale,
                includeBlocked,
                cancellationToken).ConfigureAwait(false);
            if (projectRecord is null || !CanProjectLegacyRecord(projectRecord))
                return RecordNotAvailable(operation);
            return ToolSuccess(BuildRecord(projectRecord, compactProjection));
        }
        if (revisionId is not null)
        {
            return ToolError(
                "cortex_read_arguments_invalid",
                "revisionId is used only with a correlated projectLinkId.",
                operation);
        }

        var identity = await _identifierResolver.ResolveAsync(identifier!, cancellationToken).ConfigureAwait(false);
        if (identity is null)
            return RecordNotAvailable(operation);

        var read = await _queryService.ReadAsync(
            new CortexRecordReadQuery(
                identity.Namespace,
                identity.RecordId,
                identity.RevisionId,
                includeStale,
                includeBlocked),
            _authority,
            cancellationToken).ConfigureAwait(false);
        if (!IsCleanAccepted(read))
            return RecordNotAvailable(operation);

        var record = read.Value!;
        if (!CanProjectLegacyRecord(record))
            return RecordNotAvailable(operation);

        return ToolSuccess(BuildRecord(record, compactProjection));
    }

    private async ValueTask<JsonObject> ProjectBriefAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.project-brief.read";
        var projectBrief = _runtime?.ProjectBrief;
        if (projectBrief is null)
        {
            return ToolError(
                "cortex_project_brief_not_configured",
                "The project-brief read model is not configured.",
                operation);
        }
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.ProjectBriefArguments,
                out var argumentError))
        {
            return ToolError("cortex_project_brief_arguments_invalid", argumentError!, operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "knownContextSetId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var knownContextSetId,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "knownAuthoritySetHash",
                64,
                out var knownAuthoritySetHash,
                out argumentError) ||
            !ToolArgumentReader.OptionalInt32(
                values,
                "maxUtf8Bytes",
                CortexProjectBriefContract.DefaultMaximumUtf8Bytes,
                CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
                CortexProjectBriefContract.MaximumUtf8Bytes,
                out var maximumUtf8Bytes,
                out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            knownContextSetId is not null &&
            !ValidPrefixedHash(knownContextSetId, "ctx_") ||
            knownAuthoritySetHash is not null &&
            !ValidSha256(knownAuthoritySetHash))
        {
            return ToolError(
                "cortex_project_brief_arguments_invalid",
                argumentError ?? "A project-brief identifier is invalid.",
                operation);
        }

        var result = knownAuthoritySetHash is null
            ? CortexProjectBriefService.AuthorityMismatch()
            : await projectBrief.ReadProjectBriefAsync(
                projectLinkId!,
                knownContextSetId,
                knownAuthoritySetHash,
                maximumUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ToolError(
                "cortex_project_link_not_available",
                "The correlated project link is unavailable.",
                operation);
        }

        var response = BuildProjectBrief(result);
        ApplyProjectBriefBudget(response, maximumUtf8Bytes);
        return result.Accepted &&
               response["accepted"] is JsonValue accepted &&
               accepted.TryGetValue<bool>(out var acceptedValue) &&
               acceptedValue
            ? ToolSuccess(response)
            : ToolFailure(response);
    }

    private ValueTask<JsonObject> SubmitTaskSnapshotAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        SubmitRetainedAssertionAsync(
            arguments,
            "cortex.task-snapshot.submit",
            AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes,
            CortexMcpContract.SubmitRetainedAssertionArguments,
            projectIdRequired: false,
            static bytes => CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(bytes),
            AgentTaskSnapshotPolicy.HasValidHash,
            static (
                writer,
                projectLinkId,
                _,
                projectNamespace,
                snapshot,
                token) =>
                writer.SubmitTaskSnapshotAsync(
                    projectLinkId,
                    projectNamespace,
                    snapshot,
                    token),
            cancellationToken);

    private ValueTask<JsonObject> SubmitWorkPacketAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        SubmitRetainedAssertionAsync(
            arguments,
            "cortex.work-packet.submit",
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            CortexMcpContract.SubmitRetainedAssertionArguments,
            projectIdRequired: false,
            static bytes => CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(bytes),
            AgentWorkPacketPolicy.HasValidHash,
            static (
                writer,
                projectLinkId,
                _,
                projectNamespace,
                packet,
                token) =>
                writer.SubmitWorkPacketAsync(
                    projectLinkId,
                    projectNamespace,
                    packet,
                    token),
            cancellationToken);

    private ValueTask<JsonObject> SubmitWorkPacketHandoffReceiptAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        SubmitRetainedAssertionAsync(
            arguments,
            "cortex.work-packet-handoff-receipt.submit",
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            CortexMcpContract.SubmitRetainedReceiptArguments,
            projectIdRequired: true,
            static bytes =>
                CortexCanonicalJson.Deserialize<AgentWorkPacketHandoffReceiptV1>(
                    bytes),
            static receipt => AgentWorkPacketPolicy.HasValidHash(receipt),
            static (
                writer,
                projectLinkId,
                projectId,
                projectNamespace,
                receipt,
                token) =>
                writer.SubmitWorkPacketHandoffReceiptAsync(
                    projectLinkId,
                    projectId!,
                    projectNamespace,
                    receipt,
                    token),
            cancellationToken);

    private ValueTask<JsonObject> SubmitWorkPacketAcceptanceReceiptAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        SubmitRetainedAssertionAsync(
            arguments,
            "cortex.work-packet-acceptance-receipt.submit",
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            CortexMcpContract.SubmitRetainedReceiptArguments,
            projectIdRequired: true,
            static bytes =>
                CortexCanonicalJson.Deserialize<AgentWorkPacketAcceptanceReceiptV1>(
                    bytes),
            static receipt => AgentWorkPacketPolicy.HasValidHash(receipt),
            static (
                writer,
                projectLinkId,
                projectId,
                projectNamespace,
                receipt,
                token) =>
                writer.SubmitWorkPacketAcceptanceReceiptAsync(
                    projectLinkId,
                    projectId!,
                    projectNamespace,
                    receipt,
                    token),
            cancellationToken);

    private async ValueTask<JsonObject> SubmitRetainedAssertionAsync<T>(
        JsonElement? arguments,
        string operation,
        int maximumCanonicalBytes,
        IReadOnlySet<string> acceptedArguments,
        bool projectIdRequired,
        Func<byte[], T> deserialize,
        Func<T, bool> hasValidHash,
        Func<
            ICortexRetainedAssertionWriterRuntime,
            string,
            string?,
            string,
            T,
            CancellationToken,
            ValueTask<CortexRetainedAssertionWriteResult?>> submit,
        CancellationToken cancellationToken)
        where T : class
    {
        var writer = _runtime?.RetainedAssertionWriter;
        if (writer is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                acceptedArguments,
                out var argumentError))
        {
            return ToolError(
                "cortex_retained_assertion_arguments_invalid",
                argumentError!,
                operation);
        }

        var values = arguments!.Value;
        string? projectId = null;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "projectNamespace",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectNamespace,
                out argumentError) ||
            projectIdRequired &&
            (!ToolArgumentReader.RequiredString(
                 values,
                 "projectId",
                 CortexMcpContract.MaximumIdentifierCharacters,
                 out projectId,
                 out argumentError) ||
             !ValidIdentifier(projectId!)) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidIdentifier(projectNamespace!) ||
            !values.TryGetProperty("assertion", out var assertionElement) ||
            assertionElement.ValueKind != JsonValueKind.Object ||
            !ToolArgumentReader.ContainsUniqueObjectProperties(assertionElement))
        {
            return ToolError(
                "cortex_retained_assertion_arguments_invalid",
                argumentError ?? "The project identity or typed assertion is invalid.",
                operation);
        }

        T assertion;
        byte[] canonicalUtf8;
        try
        {
            var suppliedUtf8 = Encoding.UTF8.GetBytes(assertionElement.GetRawText());
            assertion = deserialize(suppliedUtf8);
            canonicalUtf8 = CortexCanonicalJson.Serialize(assertion);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            return ToolError(
                "cortex_retained_assertion_contract_invalid",
                "The assertion is not a valid supported typed contract.",
                operation);
        }

        if (canonicalUtf8.Length is < 2 ||
            canonicalUtf8.Length > maximumCanonicalBytes ||
            canonicalUtf8.Length > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes ||
            !hasValidHash(assertion))
        {
            return ToolError(
                "cortex_retained_assertion_integrity_invalid",
                "The assertion exceeds its canonical bound or has an invalid self-hash.",
                operation);
        }

        CortexRetainedAssertionWriteResult? result;
        try
        {
            result = await submit(
                writer,
                projectLinkId!,
                projectId,
                projectNamespace!,
                assertion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CortexRetentionContentRejectedException exception)
        {
            return ToolError(
                "cortex_retained_assertion_content_rejected",
                "The assertion contains prohibited retention content.",
                operation,
                exception.FindingCodes);
        }
        catch (CortexRetentionRevisionRejectedException)
        {
            return ToolError(
                "cortex_retained_assertion_revision_invalid",
                "The assertion does not form a valid typed continuation from its declared predecessor.",
                operation);
        }
        if (result is null)
        {
            return ToolError(
                "cortex_local_project_write_not_allowed",
                "The project is unregistered, mismatched, or not present in the local write allowlist.",
                operation);
        }
        if (!ValidRetainedWriteResult(result))
        {
            return ToolError(
                "cortex_retained_assertion_result_invalid",
                "The retention provider returned an invalid bounded result.",
                operation);
        }

        var retentionAccepted = result.Disposition is
            CortexRetentionWriteDispositions.Appended or
            CortexRetentionWriteDispositions.Idempotent;
        var projectionAccepted =
            !string.Equals(
                result.Kind,
                CortexRetainedDocumentKinds.TaskSnapshot,
                StringComparison.Ordinal) ||
            result.ProjectBriefDisposition is
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent or
                CortexProjectBriefMaterializationDispositions.AlreadyAdvanced;
        // Project-brief materialization is a derived hot read model. A valid
        // passive assertion remains accepted when that narrower projection is
        // unavailable; exact replay can later repair the derived view.
        var accepted = retentionAccepted;
        var response = new JsonObject
        {
            ["accepted"] = accepted,
            ["disposition"] = result.Disposition,
            ["projectLinkId"] = result.ProjectLinkId,
            ["projectId"] = result.ProjectId,
            ["projectNamespace"] = result.ProjectNamespace,
            ["kind"] = result.Kind,
            ["documentId"] = result.DocumentId,
            ["revision"] = result.Revision,
            ["assertionHash"] = result.AssertionHash,
            ["submittedContentHash"] = result.SubmittedContentHash,
            ["retainedContentHash"] = result.RetainedContentHash,
            ["actualHeadHash"] = result.ActualHeadHash,
            ["projectBriefDisposition"] = result.ProjectBriefDisposition,
            ["projectBriefContextSetId"] = result.ProjectBriefContextSetId,
            ["projectBriefMaterialized"] =
                string.Equals(
                    result.Kind,
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    StringComparison.Ordinal)
                    ? projectionAccepted
                    : null,
            ["fallbackRequired"] =
                string.Equals(
                    result.Kind,
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    StringComparison.Ordinal) &&
                !projectionAccepted,
            ["contentTreatment"] = AgentWorkPacketContractIds.ContentTreatment,
            ["authoritative"] = false,
            ["executable"] = false,
            ["grantsSchedulingAuthority"] = false,
            ["grantsTaskClaim"] = false,
            ["grantsAuthority"] = false,
            ["safetyNotice"] =
                "The retained assertion is passive untrusted reference data and confers no authority."
        };
        if (accepted && !projectionAccepted)
        {
            var unrepresentable = string.Equals(
                result.ProjectionIssueCode,
                "cortex_project_brief_projection_unrepresentable",
                StringComparison.Ordinal);
            response["materializationDiagnostic"] = new JsonObject
            {
                ["code"] = result.ProjectionIssueCode,
                ["message"] = unrepresentable
                    ? "The exact assertion was retained, but this revision cannot be represented by the bounded hot project brief. Use exact retained reads and submit a new representable revision if a hot brief is required."
                    : "The exact assertion was retained, but its derived project brief is incomplete. Use exact retained reads; replaying the exact current task head may repair a transient derived-view failure.",
                ["severity"] = "warning",
                ["operation"] = operation,
                ["retryable"] = !unrepresentable
            };
        }
        else if (!accepted)
        {
            var retentionConflict =
                result.Disposition == CortexRetentionWriteDispositions.Conflict;
            var retentionStale =
                result.Disposition == CortexRetentionWriteDispositions.StaleBase;
            response["diagnostic"] = new JsonObject
            {
                ["code"] = retentionConflict
                    ? "cortex_retained_assertion_conflict"
                    : retentionStale
                        ? "cortex_retained_assertion_stale_base"
                        : result.ProjectionIssueCode,
                ["message"] = retentionConflict
                    ? "The idempotency identity conflicts with different retained bytes."
                    : retentionStale
                        ? "The declared predecessor is not the actual retained head."
                        : "The assertion was retained, but its deterministic project brief is not yet materialized; retry the exact assertion to repair the read model.",
                ["severity"] = "error",
                ["operation"] = operation,
                ["retryable"] = !retentionConflict && !retentionStale
            };
        }
        return accepted ? ToolSuccess(response) : ToolFailure(response);
    }

    private async ValueTask<JsonObject> ReadRetainedAssertionAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.retained-assertion.read";
        var reader = _runtime?.RetainedAssertionReader;
        if (reader is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.ReadRetainedAssertionArguments,
                out var argumentError))
        {
            return ToolError(
                "cortex_retained_record_arguments_invalid",
                argumentError!,
                operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "projectId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "projectNamespace",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectNamespace,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "kind",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var kind,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "documentId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var documentId,
                out argumentError) ||
            !ToolArgumentReader.RequiredInt64(
                values,
                "revision",
                1,
                9_007_199_254_740_991,
                out var revision,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "contentHash",
                64,
                out var contentHash,
                out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidIdentifier(projectId!) ||
            !ValidIdentifier(projectNamespace!) ||
            !ValidIdentifier(documentId!) ||
            kind is not (
                CortexRetainedDocumentKinds.TaskSnapshot or
                CortexRetainedDocumentKinds.WorkPacket or
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt or
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt or
                CortexRetainedDocumentKinds.ProjectBrief) ||
            !ValidSha256(contentHash!))
        {
            return ToolError(
                "cortex_retained_record_arguments_invalid",
                argumentError ?? "The exact retained-record identity is invalid.",
                operation);
        }

        var result = await reader.ReadRetainedAssertionAsync(
            projectLinkId!,
            projectId!,
            projectNamespace!,
            kind!,
            documentId!,
            revision,
            contentHash!,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ToolError(
                "cortex_retained_record_not_available",
                "The exact record is absent, hash-mismatched, unregistered, or not present in the local read allowlist.",
                operation);
        }
        if (!TryValidateRetainedRecord(
                result,
                projectLinkId!,
                projectId!,
                projectNamespace!,
                kind!,
                documentId!,
                revision,
                contentHash!,
                out var assertionNode))
        {
            return ToolError(
                "cortex_retained_record_integrity_invalid",
                "The retention provider returned a record that failed its exact typed boundary.",
                operation);
        }

        return ToolSuccess(new JsonObject
        {
            ["accepted"] = true,
            ["disposition"] = "exact",
            ["projectLinkId"] = result.ProjectLinkId,
            ["projectId"] = result.ProjectId,
            ["projectNamespace"] = result.ProjectNamespace,
            ["kind"] = result.Kind,
            ["documentId"] = result.DocumentId,
            ["revision"] = result.Revision,
            ["contractId"] = result.ContractId,
            ["assertionHash"] = result.AssertionHash,
            ["contentHash"] = result.ContentHash,
            ["actualHeadHash"] = result.ActualHeadHash,
            ["assertion"] = assertionNode,
            ["contentTreatment"] = AgentWorkPacketContractIds.ContentTreatment,
            ["authoritative"] = false,
            ["executable"] = false,
            ["grantsSchedulingAuthority"] = false,
            ["grantsTaskClaim"] = false,
            ["grantsAuthority"] = false,
            ["safetyNotice"] =
                "This retained assertion is passive untrusted reference data and is not current execution, scheduling, claim, approval, or identity authority."
        });
    }

    private async ValueTask<JsonObject> CorrelateProjectAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operationName = "cortex.project.correlate";
        var correlationRuntime = _runtime?.ProjectCorrelation;
        if (correlationRuntime is null)
            return RuntimeUnavailable(operationName);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.CorrelateArguments,
                out var argumentError))
        {
            return ToolError("cortex_correlate_arguments_invalid", argumentError!, operationName);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(values, "operation", 16, out var operation, out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "projectId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "storeId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var storeId,
                out argumentError) ||
            !ToolArgumentReader.RequiredInt64(
                values,
                "storeEpoch",
                1,
                long.MaxValue,
                out var storeEpoch,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "contractVersion",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var contractVersion,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "libraryAccess",
                16,
                out var libraryAccess,
                out argumentError) ||
            !ValidIdentifier(projectId!) ||
            !ValidIdentifier(storeId!) ||
            !ValidIdentifier(contractVersion!) ||
            libraryAccess is not null &&
                libraryAccess is not (
                    CortexProjectCorrelationContract.CollectiveLibraryAccess or
                    CortexProjectCorrelationContract.ReservedLibraryAccess))
        {
            return ToolError(
                "cortex_correlate_arguments_invalid",
                argumentError ?? "A project correlation identifier is invalid.",
                operationName);
        }
        libraryAccess ??= CortexProjectCorrelationContract.CollectiveLibraryAccess;

        var prepared = correlationRuntime.PrepareProjectLink(
            projectId!,
            storeId!,
            storeEpoch,
            contractVersion!,
            libraryAccess);
        if (string.Equals(operation, "prepare", StringComparison.Ordinal))
        {
            var correlation = JsonSerializer.SerializeToNode(
                prepared.Metadata,
                McpProtocol.JsonOptions)!.AsObject();
            correlation["correlationHash"] = prepared.CorrelationHash;
            return ToolSuccess(new JsonObject
            {
                ["state"] = "prepared",
                ["correlation"] = correlation,
                ["metaPatch"] = new JsonObject
                {
                    ["schemaVersion"] = "0.2",
                    ["cortexCorrelation"] = correlation.DeepClone()
                },
                ["next"] =
                    "Narrowly patch the public project .cortex/meta.json when project rules allow, then call this tool with operation confirm and the same correlationHash."
            });
        }

        if (!string.Equals(operation, "confirm", StringComparison.Ordinal) ||
            !ToolArgumentReader.RequiredString(
                values,
                "correlationHash",
                64,
                out var correlationHash,
                out argumentError) ||
            !ValidSha256(correlationHash!))
        {
            return ToolError(
                "cortex_correlate_arguments_invalid",
                argumentError ?? "Confirm requires the exact prepared correlationHash.",
                operationName);
        }

        var result = await correlationRuntime.ConfirmProjectLinkAsync(
            projectId!,
            storeId!,
            storeEpoch,
            contractVersion!,
            libraryAccess,
            correlationHash!,
            cancellationToken).ConfigureAwait(false);
        if (result.Disposition == CortexProjectLinkWriteDisposition.Conflict)
        {
            return ToolError(
                "cortex_project_link_conflict",
                "The project-link identity already exists with different correlation metadata.",
                operationName);
        }
        return ToolSuccess(new JsonObject
        {
            ["state"] = "confirmed",
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
            ["projectLinkId"] = result.Link.Metadata.ProjectLinkId,
            ["projectNamespace"] = result.Link.Metadata.ProjectNamespace,
            ["endpointId"] = result.Link.Metadata.EndpointId,
            ["libraryAccess"] =
                CortexProjectCorrelationPolicy.EffectiveLibraryAccess(result.Link.Metadata),
            ["correlationHash"] = result.Link.CorrelationHash
        });
    }

    private async ValueTask<JsonObject> SubmitProposalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.proposal.submit";
        var proposalSubmission = _runtime?.ProposalSubmission;
        if (proposalSubmission is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.SubmitProposalArguments,
                out var argumentError))
        {
            return ToolError("cortex_proposal_arguments_invalid", argumentError!, operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "idempotencyKey",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var idempotencyKey,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(values, "feedbackKind", 32, out var feedbackKind, out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "title",
                CortexMcpContract.MaximumTitleCharacters,
                out var title,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "summary",
                CortexMcpContract.MaximumSummaryCharacters,
                out var summary,
                out argumentError) ||
              !ToolArgumentReader.RequiredString(
                  values,
                  "content",
                  CortexMcpContract.MaximumRetainedContentCharacters,
                  out var content,
                  out argumentError) ||
             !TryReadProposalMetadata(
                 values,
                 out var metadata,
                 out argumentError) ||
             !TryReadProposalEvidenceReferences(
                 values,
                 out var evidenceReferences,
                 out argumentError) ||
             !ToolArgumentReader.RequiredString(values, "occurredAtUtc", 64, out var occurredAtText, out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidIdentifier(idempotencyKey!) ||
            !ValidFeedbackKind(feedbackKind!) ||
            !TryParseUtc(occurredAtText!, out var occurredAt))
        {
            return ToolError(
                "cortex_proposal_arguments_invalid",
                argumentError ?? "A proposal value or stable UTC timestamp is invalid.",
                operation);
        }

        var result = await proposalSubmission.SubmitProposalAsync(
            projectLinkId!,
            idempotencyKey!,
            feedbackKind!,
            title!,
            summary!,
            content!,
            metadata,
            evidenceReferences,
            occurredAt,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
            return ToolError("cortex_project_link_not_available", "The correlated project link is unavailable.", operation);
        var submission = result.Submission;
        if (!submission.Succeeded || submission.Proposal is null)
        {
            return ToolError(
                submission.Diagnostics.FirstOrDefault()?.Code ?? "cortex_proposal_rejected",
                "The sanitized proposal was rejected by Cortex policy.",
                operation);
        }
        if (result.ProvisionalPublicationAttempted &&
            (result.ProvisionalReview is null || !result.ProvisionalReview.Succeeded))
        {
            return ToolError(
                result.ProvisionalReview?.Diagnostics.FirstOrDefault()?.Code ??
                    "cortex_provisional_publication_failed",
                "The proposal was stored but provisional publication failed closed; it remains pending.",
                operation);
        }
        var effectiveDecision =
            result.ProvisionalReview?.Decision ??
            result.ExistingDecision;
        var provisionallyPublished =
            LearningFeedbackPublicationPolicy.IsProvisionalDecision(
                effectiveDecision);
        var searchable = string.Equals(
            effectiveDecision?.State,
            "approved",
            StringComparison.Ordinal);
        return ToolSuccess(new JsonObject
        {
            ["disposition"] = submission.Disposition.ToString().ToLowerInvariant(),
            ["proposalId"] = submission.Proposal.ProposalId,
            ["proposalHash"] = submission.Proposal.ProposalHash,
            ["reviewState"] = provisionallyPublished
                ? "provisional"
                : effectiveDecision?.State ?? "pending",
            ["decisionId"] = effectiveDecision?.DecisionId,
            ["searchable"] = searchable,
            ["deferredReviewRequired"] = provisionallyPublished,
            ["publicationConfidence"] = provisionallyPublished
                ? LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence
                : null
        });
    }

    private static bool TryReadProposalMetadata(
        JsonElement values,
        out CortexProposalMetadataV1? metadata,
        out string? error)
    {
        metadata = null;
        error = null;
        var specified = values.TryGetProperty("taxonomy", out _) ||
            values.TryGetProperty("keywords", out _) ||
            values.TryGetProperty("applicability", out _);
        if (!specified)
            return true;

        if (!ToolArgumentReader.OptionalString(
                values,
                "taxonomy",
                LearningFeedbackContractLimits.MaximumIdentifierCharacters,
                out var taxonomy,
                out error) ||
            taxonomy is not null && !ValidIdentifier(taxonomy))
        {
            error ??= "The taxonomy argument must be a bounded identifier.";
            return false;
        }

        var keywords = new List<string>(
            LearningFeedbackContractLimits.MaximumProposalKeywords);
        if (values.TryGetProperty("keywords", out var keywordValues))
        {
            if (keywordValues.ValueKind != JsonValueKind.Array)
            {
                error = "The keywords argument must be an array.";
                return false;
            }
            var seenKeywords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in keywordValues.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()?.Trim()
                    : null;
                if (keywords.Count ==
                        LearningFeedbackContractLimits.MaximumProposalKeywords ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.Length > LearningFeedbackContractLimits
                        .MaximumProposalKeywordCharacters ||
                    value.Any(char.IsControl) ||
                    !seenKeywords.Add(value))
                {
                    error =
                        "Keywords must be unique bounded strings without control characters.";
                    return false;
                }
                keywords.Add(value);
            }
        }

        var applicability = new List<LearningFeedbackApplicabilityV1>(
            LearningFeedbackContractLimits.MaximumProposalApplicabilityItems);
        if (values.TryGetProperty("applicability", out var applicabilityValues))
        {
            if (applicabilityValues.ValueKind != JsonValueKind.Array)
            {
                error = "The applicability argument must be an array.";
                return false;
            }
            var seenApplicability = new HashSet<(string ProductId, string ComponentId)>();
            foreach (var item in applicabilityValues.EnumerateArray())
            {
                if (applicability.Count == LearningFeedbackContractLimits
                        .MaximumProposalApplicabilityItems ||
                    !ToolArgumentReader.Validate(
                        item,
                        CortexMcpContract.ProposalApplicabilityArguments,
                        out error) ||
                    !ExactRequiredString(
                        item,
                        "productId",
                        LearningFeedbackContractLimits.MaximumIdentifierCharacters,
                        out var productId,
                        out error) ||
                    !ExactRequiredString(
                        item,
                        "componentId",
                        LearningFeedbackContractLimits.MaximumIdentifierCharacters,
                        out var componentId,
                        out error) ||
                    !ValidIdentifier(productId!) ||
                    !ValidIdentifier(componentId!) ||
                    !seenApplicability.Add((productId!, componentId!)))
                {
                    error ??=
                        "Applicability values must be unique bounded product/component identifiers.";
                    return false;
                }
                applicability.Add(new LearningFeedbackApplicabilityV1(
                    productId!,
                    componentId!));
            }
        }

        var candidate = new CortexProposalMetadataV1(
            taxonomy,
            Array.AsReadOnly(keywords.ToArray()),
            Array.AsReadOnly(applicability.ToArray()));
        if (!CortexProposalMetadataPolicy.TryCapture(candidate, out metadata))
        {
            error = "Proposal metadata is invalid or outside its bounded contract.";
            return false;
        }
        return true;
    }

    private static bool TryReadProposalEvidenceReferences(
        JsonElement values,
        out IReadOnlyList<LearningFeedbackEvidenceV1> evidenceReferences,
        out string? error)
    {
        evidenceReferences = [];
        error = null;
        if (!values.TryGetProperty("evidenceReferences", out var collection))
            return true;
        if (collection.ValueKind != JsonValueKind.Array)
        {
            error = "The evidenceReferences argument must be an array.";
            return false;
        }

        var captured = new List<LearningFeedbackEvidenceV1>(
            CortexMcpContract.MaximumProposalEvidenceItems);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in collection.EnumerateArray())
        {
            if (captured.Count == CortexMcpContract.MaximumProposalEvidenceItems ||
                !ToolArgumentReader.Validate(
                    item,
                    CortexMcpContract.ProposalEvidenceArguments,
                    out error) ||
                !ExactRequiredString(
                    item,
                    "evidenceId",
                    CortexMcpContract.MaximumIdentifierCharacters,
                    out var evidenceId,
                    out error) ||
                !ExactRequiredString(
                    item,
                    "reference",
                    LearningFeedbackContractLimits
                        .MaximumEvidenceReferenceCharacters,
                    out var reference,
                    out error) ||
                !ExactRequiredString(
                    item,
                    "mediaType",
                    CortexMcpContract.MaximumIdentifierCharacters,
                    out var mediaType,
                    out error) ||
                !ExactRequiredString(
                    item,
                    "sha256",
                    64,
                    out var sha256,
                    out error) ||
                !ExactRequiredString(
                    item,
                    "verification",
                    16,
                    out var verification,
                    out error) ||
                !ValidIdentifier(evidenceId!) ||
                !evidenceIds.Add(evidenceId!) ||
                !ValidEvidenceReference(reference!) ||
                !ValidMediaType(mediaType!) ||
                !ValidSha256(sha256!) ||
                !string.Equals(verification, "verified", StringComparison.Ordinal))
            {
                error ??=
                    "Evidence references must be unique, bounded, verified, and use exact lowercase SHA-256.";
                return false;
            }

            captured.Add(new LearningFeedbackEvidenceV1(
                evidenceId!,
                reference!,
                mediaType!,
                sha256!,
                verification!));
        }

        evidenceReferences = Array.AsReadOnly(
            captured
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray());
        return true;
    }

    private static bool ExactRequiredString(
        JsonElement values,
        string name,
        int maximumLength,
        out string? value,
        out string? error)
    {
        if (!ToolArgumentReader.RequiredString(
                values,
                name,
                maximumLength,
                out value,
                out error))
        {
            return false;
        }

        var exact = values.GetProperty(name).GetString();
        if (!string.Equals(exact, value, StringComparison.Ordinal))
        {
            value = null;
            error = $"The {name} evidence value must use exact canonical text.";
            return false;
        }
        return true;
    }

    private static bool ValidEvidenceReference(string value) =>
        value.StartsWith("artifact://", StringComparison.Ordinal) ||
        value.StartsWith("record://", StringComparison.Ordinal) ||
        value.StartsWith("revision://", StringComparison.Ordinal) ||
        value.StartsWith("hash://", StringComparison.Ordinal);

    private static bool ValidMediaType(string value) =>
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '/' or '+' or '.' or '-');

    private async ValueTask<JsonObject> ListProposalsAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.proposal.list";
        var proposalQuery = _runtime?.ProposalQuery;
        if (proposalQuery is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.ListProposalArguments,
                out var argumentError))
        {
            return ToolError("cortex_proposal_list_arguments_invalid", argumentError!, operation);
        }
        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "afterProposalId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var afterProposalId,
                out argumentError) ||
            !ToolArgumentReader.OptionalInt32(values, "limit", 25, 1, 100, out var limit, out argumentError) ||
            !ToolArgumentReader.OptionalBoolean(values, "includeContent", out var includeContent, out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            afterProposalId is not null && !ValidPrefixedHash(afterProposalId, "proposal_") ||
            includeContent && limit > CortexMcpContract.MaximumHydratedSearchResults)
        {
            return ToolError(
                "cortex_proposal_list_arguments_invalid",
                argumentError ?? "A proposal-list identifier is invalid.",
                operation);
        }

        var result = await proposalQuery.ListProposalsAsync(
            projectLinkId!,
            afterProposalId,
            limit,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
            return ToolError("cortex_project_link_not_available", "The correlated project link is unavailable.", operation);
        if (!result.Accepted || result.Page is null)
            return ToolError("cortex_proposal_list_rejected", "The proposal list was rejected.", operation);

        var items = new JsonArray();
        foreach (var snapshot in result.Page.Items)
        {
            var latestAssessment = await proposalQuery.FindLatestAssessmentAsync(
                projectLinkId!,
                snapshot.Proposal.ProposalId,
                cancellationToken).ConfigureAwait(false);
            var provisional = LearningFeedbackPublicationPolicy.IsProvisionalDecision(
                snapshot.Decision);
            var item = new JsonObject
            {
                ["proposalId"] = snapshot.Proposal.ProposalId,
                ["proposalHash"] = snapshot.Proposal.ProposalHash,
                ["feedbackKind"] = snapshot.Proposal.FeedbackKind,
                ["summary"] = snapshot.Proposal.Summary,
                ["createdAt"] = snapshot.Proposal.CreatedAt,
                ["reviewState"] = provisional ? "provisional" : snapshot.ReviewState,
                ["decisionId"] = snapshot.Decision?.DecisionId,
                ["searchable"] = string.Equals(
                    snapshot.Decision?.State,
                    "approved",
                    StringComparison.Ordinal),
                ["deferredReviewRequired"] = provisional && latestAssessment is null,
                ["deferredReviewOutcome"] = provisional
                    ? latestAssessment?.Recommendation ?? "pending"
                    : null,
                ["publicationConfidence"] = provisional
                    ? LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence
                    : null,
                ["latestAudit"] = latestAssessment is null
                    ? null
                    : new JsonObject
                    {
                        ["assessmentId"] = latestAssessment.AssessmentId,
                        ["recommendation"] = latestAssessment.Recommendation,
                        ["rationale"] = latestAssessment.Rationale,
                        ["retentionConfidence"] = latestAssessment.RetentionConfidence,
                        ["reviewGrantId"] = latestAssessment.ReviewGrantId,
                        ["createdAt"] = latestAssessment.CreatedAt
                }
            };
            if (string.Equals(
                    snapshot.Proposal.ContractId,
                    LearningFeedbackContractIds.ProposalV1_1,
                    StringComparison.Ordinal))
            {
                item["taxonomy"] = snapshot.Proposal.Taxonomy;
                item["keywords"] = new JsonArray(
                    (snapshot.Proposal.Keywords ?? [])
                        .Select(value => (JsonNode)value)
                        .ToArray());
                item["applicability"] = new JsonArray(
                    (snapshot.Proposal.Applicability ?? [])
                        .Select(value => (JsonNode)new JsonObject
                        {
                            ["productId"] = value.ProductId,
                            ["componentId"] = value.ComponentId
                        })
                        .ToArray());
            }
            if (includeContent)
            {
                item["title"] = snapshot.Proposal.Payload.TryGetValue("title", out var title)
                    ? title
                    : null;
                item["content"] = snapshot.Proposal.Payload.TryGetValue("content", out var content)
                    ? content
                    : null;
                item["source"] = new JsonObject
                {
                    ["authorityId"] = snapshot.Proposal.Source.AuthorityId,
                    ["subjectId"] = snapshot.Proposal.Source.SubjectId,
                    ["serviceId"] = snapshot.Proposal.Source.ServiceId,
                    ["operationId"] = snapshot.Proposal.Source.OperationId,
                    ["correlationId"] = snapshot.Proposal.Source.CorrelationId,
                    ["recordId"] = snapshot.Proposal.Source.RecordId,
                    ["revisionId"] = snapshot.Proposal.Source.RevisionId,
                    ["contentHash"] = snapshot.Proposal.Source.ContentHash,
                    ["occurredAt"] = snapshot.Proposal.Source.OccurredAt
                };
                item["evidence"] = new JsonArray(
                    snapshot.Proposal.Evidence
                        .Select(evidence => (JsonNode)new JsonObject
                        {
                            ["evidenceId"] = evidence.EvidenceId,
                            ["reference"] = evidence.Reference,
                            ["mediaType"] = evidence.MediaType,
                            ["sha256"] = evidence.Sha256,
                            ["verification"] = evidence.Verification,
                            ["verificationSemantics"] =
                                "caller-reported-reference-metadata"
                        })
                        .ToArray());
            }
            items.Add(item);
        }
        return ToolSuccess(new JsonObject
        {
            ["count"] = items.Count,
            ["items"] = items,
            ["nextAfterProposalId"] = result.Page.NextAfterProposalId
        });
    }

    private async ValueTask<JsonObject> ReviewProposalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.proposal.review";
        var proposalReview = _runtime?.ProposalReview;
        if (proposalReview is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.ReviewProposalArguments,
                out var argumentError))
        {
            return ToolError("cortex_proposal_review_arguments_invalid", argumentError!, operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "proposalId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var proposalId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(values, "state", 16, out var state, out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "reason",
                CortexMcpContract.MaximumReviewReasonCharacters,
                out var reason,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(values, "reviewedAtUtc", 64, out var reviewedAtText, out argumentError) ||
            !ToolArgumentReader.OptionalBoolean(values, "userConfirmed", out var userConfirmed, out argumentError) ||
            !values.TryGetProperty("userConfirmed", out _) ||
            !userConfirmed ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidPrefixedHash(proposalId!, "proposal_") ||
            state is not ("approved" or "rejected") ||
            !TryParseUtc(reviewedAtText!, out var reviewedAt))
        {
            return ToolError(
                "cortex_proposal_review_arguments_invalid",
                argumentError ??
                "Review requires exact identifiers, a stable UTC timestamp, and explicit userConfirmed=true.",
                operation);
        }

        var result = await proposalReview.ReviewProposalAsync(
            projectLinkId!,
            proposalId!,
            state!,
            reason!,
            reviewedAt,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
            return ToolError("cortex_proposal_not_available", "The project link or proposal is unavailable.", operation);
        if (!result.Succeeded || result.Decision is null)
        {
            return ToolError(
                result.Diagnostics.FirstOrDefault()?.Code ?? "cortex_proposal_review_rejected",
                "The independent review decision was rejected by Cortex policy.",
                operation);
        }
        return ToolSuccess(new JsonObject
        {
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
            ["decisionId"] = result.Decision.DecisionId,
            ["decisionHash"] = result.Decision.DecisionHash,
            ["proposalId"] = result.Decision.ProposalId,
            ["state"] = result.Decision.State,
            ["searchable"] = string.Equals(result.Decision.State, "approved", StringComparison.Ordinal)
        });
    }

    private async ValueTask<JsonObject> ChangePublicationAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.publication.change";
        var publicationChange = _runtime?.PublicationChange;
        if (publicationChange is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.PublicationChangeArguments,
                out var argumentError))
        {
            return ToolError(
                "cortex_publication_change_arguments_invalid",
                argumentError!,
                operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "action",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var action,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "targetRecordId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var targetRecordId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "expectedHeadRevisionId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var expectedHeadRevisionId,
                out argumentError) ||
            !ToolArgumentReader.RequiredInt64(
                values,
                "expectedHeadGeneration",
                1,
                1,
                out var expectedHeadGeneration,
                out argumentError) ||
            !TryReadPublicationReplacement(
                values,
                out var replacement,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "reasonCode",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var reasonCode,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "reason",
                LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters,
                out var reason,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "occurredAtUtc",
                64,
                out var occurredAtText,
                out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidPrefixedHash(targetRecordId!, "krec_") ||
            !ValidPrefixedHash(expectedHeadRevisionId!, "krev_") ||
            action is not (
                LearningFeedbackPublicationChangeActions.Supersede or
                LearningFeedbackPublicationChangeActions.Withdraw) ||
            reasonCode is not (
                LearningFeedbackPublicationChangeReasonCodes.Corrected or
                LearningFeedbackPublicationChangeReasonCodes.Obsolete or
                LearningFeedbackPublicationChangeReasonCodes.Duplicate or
                LearningFeedbackPublicationChangeReasonCodes.UnsafeContent or
                LearningFeedbackPublicationChangeReasonCodes.InvalidContent) ||
            !TryParseUtc(occurredAtText!, out var occurredAt))
        {
            return ToolError(
                "cortex_publication_change_arguments_invalid",
                argumentError ??
                    "Publication change requires exact record/head coordinates and a stable UTC timestamp.",
                operation);
        }

        var request = new LearningFeedbackPublicationChangeRequestV1(
            SchemaVersion: 1,
            LearningFeedbackContractIds.PublicationChange,
            LearningFeedbackContractIds.Canonicalization,
            action!,
            targetRecordId!,
            expectedHeadRevisionId!,
            expectedHeadGeneration,
            replacement,
            reasonCode!,
            reason!,
            occurredAt);
        var result = await publicationChange.ChangePublicationAsync(
            projectLinkId!,
            request,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ToolError(
                "cortex_project_link_not_available",
                "The correlated project link is unavailable.",
                operation);
        }

        var response = new JsonObject
        {
            ["accepted"] = result.Succeeded,
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
            ["changeId"] = result.ChangeId,
            ["targetRecordId"] = result.TargetRecordId,
            ["headRevisionId"] = result.HeadRevisionId,
            ["headGeneration"] = result.HeadGeneration,
            ["replacementRecordId"] = result.ReplacementRecordId,
            ["replacementRevisionId"] = result.ReplacementRevisionId,
            ["historyPreserved"] = true,
            ["grantsAuthority"] = false
        };
        if (!result.Succeeded)
        {
            var diagnostic = result.Diagnostics.FirstOrDefault();
            response["diagnostic"] = new JsonObject
            {
                ["code"] = diagnostic?.Code ??
                    "cortex_publication_change_rejected",
                ["message"] = diagnostic?.Message ??
                    "The authored publication change was rejected.",
                ["severity"] = "error",
                ["operation"] = operation,
                ["retryable"] = diagnostic?.Retryable ?? false
            };
        }
        return result.Succeeded ? ToolSuccess(response) : ToolFailure(response);
    }

    private static bool TryReadPublicationReplacement(
        JsonElement values,
        out LearningFeedbackPublicationReplacementV1? replacement,
        out string? error)
    {
        replacement = null;
        error = null;
        if (!values.TryGetProperty("replacement", out var candidate) ||
            candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (candidate.ValueKind != JsonValueKind.Object ||
            !ToolArgumentReader.ContainsUniqueObjectProperties(candidate) ||
            !ToolArgumentReader.Validate(
                candidate,
                CortexMcpContract.PublicationReplacementArguments,
                out error) ||
            !ToolArgumentReader.RequiredString(
                candidate,
                "recordId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var recordId,
                out error) ||
            !ToolArgumentReader.RequiredString(
                candidate,
                "revisionId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var revisionId,
                out error) ||
            !ToolArgumentReader.RequiredString(
                candidate,
                "contentHash",
                64,
                out var contentHash,
                out error) ||
            !ValidPrefixedHash(recordId!, "krec_") ||
            !ValidPrefixedHash(revisionId!, "krev_") ||
            !ValidSha256(contentHash!))
        {
            error ??= "The optional replacement coordinates are invalid.";
            return false;
        }

        replacement = new LearningFeedbackPublicationReplacementV1(
            recordId!,
            revisionId!,
            contentHash!);
        return true;
    }

    private async ValueTask<JsonObject> SubmitAuditAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.proposal.audit";
        var proposalAudit = _runtime?.ProposalAudit;
        if (proposalAudit is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.SubmitAuditArguments,
                out var argumentError))
        {
            return ToolError("cortex_audit_arguments_invalid", argumentError!, operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "proposalId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var proposalId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "recommendation",
                16,
                out var recommendation,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "rationale",
                CortexMcpContract.MaximumReviewReasonCharacters,
                out var rationale,
                out argumentError) ||
            !ToolArgumentReader.OptionalString(
                values,
                "reviewGrantId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var reviewGrantId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "assessedAtUtc",
                64,
                out var assessedAtText,
                out argumentError) ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidPrefixedHash(proposalId!, "proposal_") ||
            reviewGrantId is not null && !ValidPrefixedHash(reviewGrantId, "grant_") ||
            recommendation is not ("approve" or "reject" or "human_review") ||
            !TryParseUtc(assessedAtText!, out var assessedAt))
        {
            return ToolError(
                "cortex_audit_arguments_invalid",
                argumentError ?? "The audit assessment contains an invalid value or timestamp.",
                operation);
        }

        var result = await proposalAudit.SubmitAuditAsync(
            projectLinkId!,
            proposalId!,
            recommendation!,
            rationale!,
            reviewGrantId,
            assessedAt,
            cancellationToken).ConfigureAwait(false);
        if (result?.Assessment is null)
        {
            return ToolError(
                "cortex_audit_rejected",
                "The independent audit assessment was rejected by Cortex policy.",
                operation);
        }
        return ToolSuccess(new JsonObject
        {
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
            ["assessmentId"] = result.Assessment.AssessmentId,
            ["assessmentHash"] = result.Assessment.AssessmentHash,
            ["recommendation"] = result.Assessment.Recommendation,
            ["retentionConfidence"] = result.Assessment.RetentionConfidence,
            ["outcome"] = result.Outcome,
            ["reviewGrantUseId"] = result.GrantUse?.UseId,
            ["decisionId"] = result.Decision?.DecisionId,
            ["reviewState"] = result.Decision?.State ?? "pending",
            ["searchable"] = result.Decision is not null
        });
    }

    private async ValueTask<JsonObject> ManageReviewGrantAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        const string operation = "cortex.review-grant.manage";
        var reviewGrant = _runtime?.ReviewGrant;
        if (reviewGrant is null)
            return RuntimeUnavailable(operation);
        if (!ToolArgumentReader.Validate(
                arguments,
                CortexMcpContract.ManageReviewGrantArguments,
                out var argumentError))
        {
            return ToolError("cortex_review_grant_arguments_invalid", argumentError!, operation);
        }

        var values = arguments!.Value;
        if (!ToolArgumentReader.RequiredString(values, "operation", 16, out var requestedOperation, out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "projectLinkId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var projectLinkId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(
                values,
                "reviewGrantId",
                CortexMcpContract.MaximumIdentifierCharacters,
                out var reviewGrantId,
                out argumentError) ||
            !ToolArgumentReader.RequiredString(values, "occurredAtUtc", 64, out var occurredAtText, out argumentError) ||
            !ToolArgumentReader.OptionalBoolean(values, "userConfirmed", out var userConfirmed, out argumentError) ||
            !values.TryGetProperty("userConfirmed", out _) ||
            !userConfirmed ||
            !ValidPrefixedHash(projectLinkId!, "plink_") ||
            !ValidPrefixedHash(reviewGrantId!, "grant_") ||
            requestedOperation is not ("issue" or "revoke") ||
            !TryParseUtc(occurredAtText!, out var occurredAt))
        {
            return ToolError(
                "cortex_review_grant_arguments_invalid",
                argumentError ??
                "Grant management requires exact identifiers, a stable UTC timestamp, and explicit userConfirmed=true.",
                operation);
        }

        LearningFeedbackReviewGrantWriteResult? result;
        if (string.Equals(requestedOperation, "issue", StringComparison.Ordinal))
        {
            if (!ToolArgumentReader.RequiredString(
                    values,
                    "delegateAuthorityId",
                    CortexMcpContract.MaximumIdentifierCharacters,
                    out var delegateAuthorityId,
                    out argumentError) ||
                !ToolArgumentReader.RequiredString(
                    values,
                    "delegateSubjectId",
                    CortexMcpContract.MaximumIdentifierCharacters,
                    out var delegateSubjectId,
                    out argumentError) ||
                !ToolArgumentReader.RequiredStringArray(
                    values,
                    "feedbackKinds",
                    1,
                    2,
                    16,
                    out var feedbackKinds,
                    out argumentError) ||
                !ToolArgumentReader.OptionalInt32(
                    values,
                    "maximumUses",
                    0,
                    1,
                    LearningFeedbackContractLimits.MaximumGrantUses,
                    out var maximumUses,
                    out argumentError) ||
                !values.TryGetProperty("maximumUses", out _) ||
                !ToolArgumentReader.RequiredString(
                    values,
                    "expiresAtUtc",
                    64,
                    out var expiresAtText,
                    out argumentError) ||
                !ValidIdentifier(delegateAuthorityId!) ||
                !ValidIdentifier(delegateSubjectId!) ||
                feedbackKinds.Any(kind => kind is not ("quality" or "success")) ||
                !TryParseUtc(expiresAtText!, out var expiresAt))
            {
                return ToolError(
                    "cortex_review_grant_arguments_invalid",
                    argumentError ?? "The grant issuance values are invalid.",
                    operation);
            }
            result = await reviewGrant.IssueReviewGrantAsync(
                projectLinkId!,
                reviewGrantId!,
                delegateAuthorityId!,
                delegateSubjectId!,
                feedbackKinds,
                maximumUses,
                expiresAt,
                occurredAt,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!ToolArgumentReader.RequiredString(
                    values,
                    "reason",
                    CortexMcpContract.MaximumReviewReasonCharacters,
                    out var reason,
                    out argumentError))
            {
                return ToolError(
                    "cortex_review_grant_arguments_invalid",
                    argumentError ?? "Grant revocation requires a reason.",
                    operation);
            }
            result = await reviewGrant.RevokeReviewGrantAsync(
                projectLinkId!,
                reviewGrantId!,
                reason!,
                occurredAt,
                cancellationToken).ConfigureAwait(false);
        }

        if (result?.Grant is null)
        {
            return ToolError(
                "cortex_review_grant_rejected",
                "The review grant operation was rejected or the grant is unavailable.",
                operation);
        }
        return ToolSuccess(new JsonObject
        {
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
            ["operation"] = requestedOperation,
            ["reviewGrantId"] = result.Grant.GrantId,
            ["reviewGrantHash"] = result.Grant.GrantHash,
            ["delegateAuthorityId"] = result.Grant.DelegateAuthorityId,
            ["delegateSubjectId"] = result.Grant.DelegateSubjectId,
            ["feedbackKinds"] = JsonSerializer.SerializeToNode(result.Grant.FeedbackKinds, McpProtocol.JsonOptions),
            ["maximumUses"] = result.Grant.MaximumUses,
            ["expiresAt"] = result.Grant.ExpiresAt,
            ["revocationId"] = result.Revocation?.RevocationId
        });
    }

    private async ValueTask<JsonObject> BuildDiscoveryAsync(CancellationToken cancellationToken)
    {
        var discovery = RequireAccepted(
            await _queryService.DiscoverAsync(_authority, cancellationToken).ConfigureAwait(false),
            "discovery");
        var tools = new JsonArray();
        foreach (var definition in ListTools().OfType<JsonObject>())
        {
            tools.Add(definition["name"]?.GetValue<string>());
        }
        var writes = RuntimeWritesAvailable;
        var descriptor = _runtime?.Descriptor;
        var storageKind = descriptor?.StorageKind ?? CortexMcpStorageKinds.None;
        var response = new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["name"] = McpProtocol.ServerName,
                ["title"] = McpProtocol.ServerTitle,
                ["version"] = McpProtocol.ServerVersion,
                ["protocolVersion"] = McpProtocol.Version,
                ["transport"] = "stdio-newline-delimited-json-rpc"
            },
            ["mode"] = discovery.Mode,
            ["tools"] = tools,
            ["resources"] = new JsonObject
            {
                ["status"] = StatusResourceUri,
                ["catalog"] = CatalogResourceUri,
                ["guide"] = GuideResourceUri,
                ["recordTemplate"] = RecordResourceUriTemplate,
                ["exposedRecordCount"] = CanReadKnowledge ? _resourceCatalog.Records.Count : 0
            },
            ["limits"] = new JsonObject
            {
                ["maximumRequestBytes"] = McpProtocol.MaximumRequestBytes,
                ["maximumResponseBytes"] = McpProtocol.MaximumResponseBytes,
                ["maximumSearchTextCharacters"] = CortexMcpContract.MaximumSearchTextCharacters,
                ["maximumSearchResults"] = CortexMcpContract.MaximumSearchResults,
                ["maximumReadRecordsPerCall"] = 1,
                ["maximumResourceRecords"] = CortexMcpContract.MaximumResourceRecords
            },
            ["access"] = new JsonObject
            {
                ["network"] = descriptor?.UsesNetwork ?? false,
                ["database"] = descriptor?.UsesDatabase ?? false,
                ["authentication"] = descriptor?.UsesAuthentication ?? false,
                ["writes"] = writes,
                ["liveServices"] = descriptor?.UsesLiveServices ?? false,
                ["projectContinuityRuntime"] = CanReadProjectBrief
            },
            ["storageKind"] = storageKind,
            ["features"] = BuildFeatureProjection(),
            ["safetyNotice"] = discovery.SafetyNotice
        };
        if (descriptor is not null)
        {
            response["hybrid"] = new JsonObject
            {
                ["state"] = descriptor.State,
                ["issueCode"] = descriptor.IssueCode,
                ["endpointId"] = descriptor.EndpointId,
                ["principal"] = descriptor.PrincipalName,
                ["subjectId"] = descriptor.SubjectId,
                ["principalCapabilities"] = new JsonArray(
                    Enum.GetValues<CortexMcpRuntimeCapability>()
                        .Where(value =>
                            value != CortexMcpRuntimeCapability.None &&
                            descriptor.HasCapability(value))
                        .Select(value => JsonValue.Create(value.ToString().ToLowerInvariant()))
                        .ToArray()),
                ["routing"] = descriptor.Routing
            };
        }
        return response;
    }

    private async ValueTask<JsonObject> BuildStatusAsync(CancellationToken cancellationToken)
    {
        var status = RequireAccepted(
            await _queryService.GetStatusAsync(_authority, cancellationToken).ConfigureAwait(false),
            "status");
        var response = JsonSerializer.SerializeToNode(new
        {
            status = status.Status,
            mode = status.Mode,
            validation = status.ValidationState,
            serverBuild = new
            {
                fingerprint = _serverBuildIdentity.Fingerprint,
                basis = _serverBuildIdentity.Basis
            },
            library = new
            {
                releaseVersion = status.ReleaseVersion,
                nuGetVersion = status.PackageVersion,
                bundleId = status.CollectionId,
                bundleHash = status.CollectionHash,
                @namespace = status.DefaultNamespace,
                recordCount = status.RecordCount,
                defaultReadableRecordCount = status.DefaultReadableRecordCount,
                excludedByDefaultCount = status.ExcludedByDefaultCount,
                exposedResourceRecordCount = _resourceCatalog.Records.Count,
                taxonomyCount = status.Taxonomies.Count,
                taxonomies = status.Taxonomies,
                freshness = status.Freshness
            },
            contentTreatment = status.ContentTreatment,
            executable = false,
            safetyNotice = status.SafetyNotice
        }, McpProtocol.JsonOptions)!.AsObject();
        response["storageKind"] =
            _runtime?.Descriptor.StorageKind ?? CortexMcpStorageKinds.None;
        response["features"] = BuildFeatureProjection();
        if (_runtime?.Descriptor is { } descriptor)
        {
            response["hybrid"] = JsonSerializer.SerializeToNode(new
            {
                state = descriptor.State,
                issueCode = descriptor.IssueCode,
                endpointId = descriptor.EndpointId,
                principal = descriptor.PrincipalName,
                subjectId = descriptor.SubjectId,
                storageKind = descriptor.StorageKind,
                writes = RuntimeWritesAvailable
            }, McpProtocol.JsonOptions);
        }
        return response;
    }

    private JsonObject BuildFeatureProjection()
    {
        var projectKnowledge = CanSearchProjectKnowledge;
        var retainedRead = CanReadRetainedState;
        var retainedWrite = CanWriteRetainedState;
        var proposalSubmit = CanSubmitProposals;
        var proposalQuery = CanQueryProposals;
        var proposalReview = CanReviewProposals;
        var proposalAudit = CanAuditProposals;
        var publicationChange = CanChangePublications;
        return new JsonObject
        {
            ["defaultLibrarySearch"] = CanReadKnowledge,
            ["projectBrief"] = CanReadProjectBrief,
            ["retainedState"] = retainedRead || retainedWrite,
            ["retainedStateRead"] = retainedRead,
            ["retainedStateWrite"] = retainedWrite,
            ["collectiveSearch"] = projectKnowledge,
            ["projectLinkedSearch"] = projectKnowledge,
            ["proposals"] = proposalSubmit || proposalQuery ||
                proposalReview || proposalAudit || publicationChange,
            ["proposalSubmit"] = proposalSubmit,
            ["proposalQuery"] = proposalQuery,
            ["proposalReview"] = proposalReview,
            ["proposalAudit"] = proposalAudit,
            ["publicationChange"] = publicationChange,
            ["operationalTasks"] = _runtime?.Operations is not null &&
                _runtime.HasCapability(CortexMcpRuntimeCapability.Tasks),
            ["operationalKnowledge"] = _runtime?.Operations is not null &&
                _runtime.HasCapability(CortexMcpRuntimeCapability.KnowledgeWrite),
            ["provisionalPublication"] = proposalSubmit &&
                (_runtime?.Descriptor.SupportsProvisionalPublication ?? false)
        };
    }

    private JsonObject BuildCatalog(CortexKnowledgeCatalog catalog)
    {
        var records = new JsonArray();
        foreach (var record in catalog.Records)
        {
            records.Add(JsonSerializer.SerializeToNode(new
            {
                recordId = record.RecordId,
                revisionId = record.RevisionId,
                resourceUri = ResourceUri(record.RecordId),
                taxonomy = record.Taxonomy,
                title = record.Title,
                summary = record.Summary,
                freshness = record.Freshness,
                sensitivity = record.Sensitivity,
                contentTreatment = record.ContentTreatment,
                executable = false
            }, McpProtocol.JsonOptions));
        }

        return new JsonObject
        {
            ["releaseVersion"] = catalog.ReleaseVersion,
            ["count"] = records.Count,
            ["records"] = records,
            ["safetyNotice"] = catalog.SafetyNotice
        };
    }

    private JsonObject BuildRecord(CortexKnowledgeRecord record, bool compact = false)
    {
        if (!CanProjectLegacyRecord(record))
            throw new InvalidOperationException("Structured provenance cannot be represented by the legacy MCP record contract.");

        if (compact)
        {
            return JsonSerializer.SerializeToNode(new
            {
                projection = CortexMcpContract.CompactProjection,
                recordId = record.RecordId,
                revisionId = record.RevisionId,
                title = record.Title,
                summary = record.Summary,
                content = record.Content,
                contentHash = record.ContentHash,
                freshness = record.Freshness,
                contentTreatment = record.ContentTreatment,
                executable = false,
                safetyNotice = CortexQueryPolicy.SafetyNotice
            }, McpProtocol.JsonOptions)!.AsObject();
        }

        return JsonSerializer.SerializeToNode(new
        {
            recordId = record.RecordId,
            revisionId = record.RevisionId,
            resourceUri = _resourceIdentities.Contains((record.RecordId, record.RevisionId))
                ? ResourceUri(record.RecordId)
                : null,
            @namespace = record.Namespace,
            taxonomy = record.Taxonomy,
            slug = record.Slug,
            title = record.Title,
            summary = record.Summary,
            content = record.Content,
            keywords = record.Keywords,
            applicability = record.Applicability,
            evidence = record.Evidence,
            provenance = new
            {
                producerContract = record.Provenance.ProducerContract,
                sourceRecordIds = record.Provenance.SourceReferences,
                transformations = record.Provenance.Transformations
            },
            confidence = record.Confidence,
            freshness = record.Freshness,
            sensitivity = record.Sensitivity,
            recordStatus = record.RecordStatus,
            trainingEligibility = record.TrainingEligibility,
            contentTreatment = record.ContentTreatment,
            executable = false,
            contentHash = record.ContentHash,
            safetyNotice = CortexQueryPolicy.SafetyNotice
        }, McpProtocol.JsonOptions)!.AsObject();
    }

    private static JsonObject BuildProjectBrief(CortexProjectBriefResult result)
    {
        var brief = result.Response;
        var response = new JsonObject
        {
            ["accepted"] = result.Accepted,
            ["schemaVersion"] = brief.SchemaVersion,
            ["contractId"] = brief.ContractId,
            ["disposition"] = brief.Disposition.ToString().ToLowerInvariant(),
            ["projectLinkId"] = brief.ProjectLinkId,
            ["projectId"] = brief.ProjectId,
            ["projectNamespace"] = brief.ProjectNamespace,
            ["contextSetId"] = brief.ContextSetId,
            ["baseContextSetId"] = brief.BaseContextSetId,
            ["stateRevision"] = brief.StateRevision,
            ["sourceHash"] = brief.SourceHash,
            ["authoritySetHash"] = brief.AuthoritySetHash,
            ["selectionVersion"] = brief.SelectionVersion,
            ["currentTask"] =
                JsonSerializer.SerializeToNode(brief.CurrentTask, McpProtocol.JsonOptions),
            ["hotTasks"] =
                JsonSerializer.SerializeToNode(brief.HotTasks, McpProtocol.JsonOptions),
            ["removedTaskIds"] =
                JsonSerializer.SerializeToNode(brief.RemovedTaskIds, McpProtocol.JsonOptions),
            ["hasMore"] = brief.HasMore,
            ["checkpointReferences"] =
                JsonSerializer.SerializeToNode(brief.CheckpointReferences, McpProtocol.JsonOptions),
            ["evidenceReferences"] =
                JsonSerializer.SerializeToNode(brief.EvidenceReferences, McpProtocol.JsonOptions),
            ["freshness"] =
                JsonSerializer.SerializeToNode(brief.Freshness, McpProtocol.JsonOptions),
            ["complete"] = brief.Complete,
            ["truncated"] = brief.Truncated,
            ["fallbackRequired"] = brief.FallbackRequired,
            ["fallbackReason"] = brief.FallbackReason,
            ["recoveryOrder"] =
                JsonSerializer.SerializeToNode(brief.RecoveryOrder, McpProtocol.JsonOptions),
            ["safetyNotice"] = CortexProjectBriefContract.SafetyNotice
        };
        if (!result.Accepted)
        {
            var diagnostic = result.Diagnostics.FirstOrDefault();
            response["diagnostic"] = new JsonObject
            {
                ["code"] = diagnostic?.Code ?? "cortex_project_brief_not_available",
                ["message"] = diagnostic?.Message ??
                    "No current project-brief snapshot is available.",
                ["severity"] = "error",
                ["operation"] = "cortex.project-brief.read",
                ["retryable"] = diagnostic?.Retryable ?? false
            };
        }
        return response;
    }

    private static void ApplyProjectBriefBudget(JsonObject response, int maximumUtf8Bytes)
    {
        if (Utf8Bytes(response) <= maximumUtf8Bytes)
            return;

        // Any projection loss makes the brief unsuitable as an accepted hot
        // recovery view. Preserve bounded context where possible, but force the
        // caller onto its authoritative local fallback.
        response["accepted"] = false;
        response["complete"] = false;
        response["truncated"] = true;
        response["fallbackRequired"] = true;
        response["fallbackReason"] = "response_truncated";
        response["diagnostic"] = ProjectBriefTruncationDiagnostic();
        foreach (var name in new[]
                 {
                     "hotTasks",
                     "removedTaskIds",
                     "evidenceReferences",
                     "checkpointReferences"
                 })
        {
            var values = response[name]!.AsArray();
            while (values.Count > 0 && Utf8Bytes(response) > maximumUtf8Bytes)
                values.RemoveAt(values.Count - 1);
        }

        if (Utf8Bytes(response) > maximumUtf8Bytes)
            response["currentTask"] = null;
        if (Utf8Bytes(response) > maximumUtf8Bytes &&
            response["freshness"] is JsonObject freshness)
        {
            freshness["basis"] = "response-truncated";
        }
        if (Utf8Bytes(response) > maximumUtf8Bytes)
            response["freshness"] = null;
        if (Utf8Bytes(response) <= maximumUtf8Bytes)
            return;

        // The advertised minimum fits this fail-closed skeleton. Never exceed a
        // caller's budget merely to preserve optional projection detail.
        response.Clear();
        response["accepted"] = false;
        response["schemaVersion"] = CortexProjectBriefContract.SchemaVersion;
        response["contractId"] = CortexProjectBriefContract.ContractId;
        response["disposition"] = "snapshot";
        response["projectLinkId"] = null;
        response["projectId"] = null;
        response["projectNamespace"] = null;
        response["contextSetId"] = null;
        response["baseContextSetId"] = null;
        response["stateRevision"] = null;
        response["sourceHash"] = null;
        response["authoritySetHash"] = null;
        response["selectionVersion"] = null;
        response["currentTask"] = null;
        response["hotTasks"] = new JsonArray();
        response["removedTaskIds"] = new JsonArray();
        response["hasMore"] = false;
        response["checkpointReferences"] = new JsonArray();
        response["evidenceReferences"] = new JsonArray();
        response["freshness"] = null;
        response["complete"] = false;
        response["truncated"] = true;
        response["fallbackRequired"] = true;
        response["fallbackReason"] = "response_truncated";
        response["recoveryOrder"] = JsonSerializer.SerializeToNode(
            CortexProjectBriefContract.FallbackRecoveryOrder,
            McpProtocol.JsonOptions);
        response["safetyNotice"] = CortexProjectBriefContract.SafetyNotice;
        response["diagnostic"] = ProjectBriefTruncationDiagnostic();
    }

    private static JsonObject ProjectBriefTruncationDiagnostic() => new()
    {
        ["code"] = "cortex_project_brief_response_truncated",
        ["message"] =
            "The complete project brief could not fit inside the requested UTF-8 budget.",
        ["severity"] = "error",
        ["operation"] = "cortex.project-brief.read",
        ["retryable"] = false
    };

    private static int Utf8Bytes(JsonObject value) =>
        Encoding.UTF8.GetByteCount(value.ToJsonString(McpProtocol.JsonOptions));

    private JsonObject BuildSearchRecord(
        CortexKnowledgeSearchHit hit,
        bool compact,
        bool includeContent = false,
        bool exposeSourceScope = true)
    {
        var record = hit.Record;
        if (compact)
        {
            var projected = JsonSerializer.SerializeToNode(new
            {
                recordId = record.RecordId,
                revisionId = record.RevisionId,
                title = record.Title,
                summary = record.Summary,
                score = hit.Score,
                freshness = record.Freshness,
                contentHash = record.ContentHash,
                contentTreatment = record.ContentTreatment,
                executable = false
            }, McpProtocol.JsonOptions)!.AsObject();
            if (includeContent)
                projected["content"] = record.Content;
            return projected;
        }

        var excerpt = record.Content.Length <= CortexMcpContract.MaximumExcerptCharacters
            ? record.Content
            : record.Content[..CortexMcpContract.MaximumExcerptCharacters];
        var fullProjection = JsonSerializer.SerializeToNode(new
        {
            recordId = record.RecordId,
            revisionId = record.RevisionId,
            resourceUri = _resourceIdentities.Contains((record.RecordId, record.RevisionId))
                ? ResourceUri(record.RecordId)
                : null,
            @namespace = record.Namespace,
            taxonomy = record.Taxonomy,
            title = record.Title,
            summary = record.Summary,
            contentExcerpt = excerpt,
            contentTruncated = excerpt.Length != record.Content.Length,
            applicability = record.Applicability.Take(8).ToArray(),
            evidence = record.Evidence.Take(4).ToArray(),
            confidence = record.Confidence,
            freshness = record.Freshness,
            sensitivity = record.Sensitivity,
            score = hit.Score,
            contentTreatment = record.ContentTreatment,
            executable = false,
            safetyNotice = hit.SafetyNotice
        }, McpProtocol.JsonOptions)!.AsObject();
        if (!exposeSourceScope)
        {
            fullProjection.Remove("namespace");
            fullProjection.Remove("resourceUri");
            fullProjection.Remove("evidence");
        }
        return fullProjection;
    }

    private static bool FitsHydratedContentBudget(
        IReadOnlyList<CortexKnowledgeSearchHit> records,
        int maximumUtf8Bytes)
    {
        var total = 0;
        foreach (var hit in records)
        {
            var bytes = Encoding.UTF8.GetByteCount(hit.Record.Content);
            if (bytes > maximumUtf8Bytes - total)
                return false;
            total += bytes;
        }

        return true;
    }

    private static bool TryReadProjection(
        JsonElement values,
        out bool compact,
        out string? error)
    {
        compact = false;
        if (!ToolArgumentReader.OptionalString(
                values,
                "projection",
                CortexMcpContract.MaximumProjectionCharacters,
                out var projection,
                out error))
        {
            return false;
        }

        if (projection is null ||
            string.Equals(projection, CortexMcpContract.FullProjection, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(projection, CortexMcpContract.CompactProjection, StringComparison.Ordinal))
        {
            compact = true;
            return true;
        }

        error = "The projection argument must be one of the supported values.";
        return false;
    }

    private static bool ValidRetainedWriteResult(
        CortexRetainedAssertionWriteResult result)
    {
        if (result.Disposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent or
                CortexRetentionWriteDispositions.Conflict or
                CortexRetentionWriteDispositions.StaleBase) ||
            !ValidPrefixedHash(result.ProjectLinkId, "plink_") ||
            !ValidIdentifier(result.ProjectId) ||
            !ValidIdentifier(result.ProjectNamespace) ||
            result.Kind is not (
                CortexRetainedDocumentKinds.TaskSnapshot or
                CortexRetainedDocumentKinds.WorkPacket or
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt or
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt) ||
            !ValidIdentifier(result.DocumentId) ||
            result.Revision < 1 ||
            !ValidAssertionHash(result.AssertionHash) ||
            !ValidSha256(result.SubmittedContentHash) ||
            result.RetainedContentHash is not null &&
            !ValidSha256(result.RetainedContentHash) ||
            result.ActualHeadHash is not null &&
            !ValidSha256(result.ActualHeadHash) ||
            result.ProjectBriefContextSetId is not null &&
            !ValidPrefixedHash(result.ProjectBriefContextSetId, "ctx_") ||
            result.ProjectionIssueCode is not null &&
            !ValidIdentifier(result.ProjectionIssueCode))
        {
            return false;
        }

        if (!string.Equals(
                result.Kind,
                CortexRetainedDocumentKinds.TaskSnapshot,
                StringComparison.Ordinal))
        {
            if (result.ProjectBriefDisposition is not null ||
                result.ProjectBriefContextSetId is not null ||
                result.ProjectionIssueCode is not null)
            {
                return false;
            }
        }
        else if (result.Disposition is
                 CortexRetentionWriteDispositions.Conflict or
                 CortexRetentionWriteDispositions.StaleBase)
        {
            if (result.ProjectBriefDisposition is not null ||
                result.ProjectBriefContextSetId is not null ||
                result.ProjectionIssueCode is not null)
            {
                return false;
            }
        }
        else if (result.ProjectBriefDisposition is
                 CortexRetentionWriteDispositions.Appended or
                 CortexRetentionWriteDispositions.Idempotent or
                 CortexProjectBriefMaterializationDispositions.AlreadyAdvanced)
        {
            if (result.ProjectBriefContextSetId is null ||
                result.ProjectionIssueCode is not null)
            {
                return false;
            }
        }
        else if (result.ProjectBriefDisposition is
                 CortexRetentionWriteDispositions.Conflict or
                 CortexRetentionWriteDispositions.StaleBase or
                 CortexProjectBriefMaterializationDispositions.Unavailable)
        {
            if (result.ProjectionIssueCode is null)
                return false;
        }
        else
        {
            return false;
        }

        if (result.Disposition == CortexRetentionWriteDispositions.Appended)
        {
            return string.Equals(
                       result.RetainedContentHash,
                       result.SubmittedContentHash,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       result.ActualHeadHash,
                       result.SubmittedContentHash,
                       StringComparison.Ordinal);
        }
        if (result.Disposition == CortexRetentionWriteDispositions.Idempotent)
        {
            // An exact replay may target an older immutable revision after a
            // successor became head. Preserve both the replayed revision hash
            // and the repository's actual later head.
            return string.Equals(
                       result.RetainedContentHash,
                       result.SubmittedContentHash,
                       StringComparison.Ordinal) &&
                   result.ActualHeadHash is not null;
        }
        return true;
    }

    private static bool TryValidateRetainedRecord(
        CortexRetainedAssertionRecord result,
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        out JsonNode? assertionNode)
    {
        assertionNode = null;
        if (!ValidIdentifier(result.TenantId) ||
            !string.Equals(result.ProjectLinkId, projectLinkId, StringComparison.Ordinal) ||
            !string.Equals(result.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(result.ProjectNamespace, projectNamespace, StringComparison.Ordinal) ||
            !string.Equals(result.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(result.DocumentId, documentId, StringComparison.Ordinal) ||
            result.Revision != revision ||
            !string.Equals(result.ContentHash, contentHash, StringComparison.Ordinal) ||
            !ValidAssertionHash(result.AssertionHash) ||
            !ValidSha256(result.ContentHash) ||
            !ValidSha256(result.ActualHeadHash) ||
            string.IsNullOrWhiteSpace(result.ContractId) ||
            result.ContractId.Length > CortexAgentRetentionLimits.MaximumContractIdCharacters ||
            result.CanonicalUtf8 is null ||
            result.CanonicalUtf8.Length is < 2 or
                > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes ||
            !string.Equals(
                CortexCanonicalJson.Sha256(result.CanonicalUtf8),
                result.ContentHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[] canonical;
            string assertionHash;
            switch (result.Kind)
            {
                case CortexRetainedDocumentKinds.TaskSnapshot:
                {
                    var snapshot =
                        CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                            result.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(snapshot);
                    assertionHash = snapshot.SnapshotHash;
                    if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
                        !string.Equals(snapshot.ProjectId, result.ProjectId, StringComparison.Ordinal) ||
                        !string.Equals(snapshot.TaskSetId, result.DocumentId, StringComparison.Ordinal) ||
                        snapshot.SnapshotRevision != result.Revision ||
                        snapshot.ProjectLinkId is not null &&
                        !string.Equals(
                            snapshot.ProjectLinkId,
                            result.ProjectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(snapshot.ContractId, result.ContractId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacket:
                {
                    var packet =
                        CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                            result.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(packet);
                    assertionHash = packet.PacketHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(packet) ||
                        !string.Equals(packet.ProjectId, result.ProjectId, StringComparison.Ordinal) ||
                        !string.Equals(packet.WorkId, result.DocumentId, StringComparison.Ordinal) ||
                        packet.PacketRevision != result.Revision ||
                        packet.ProjectLinkId is not null &&
                        !string.Equals(
                            packet.ProjectLinkId,
                            result.ProjectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(packet.ContractId, result.ContractId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketHandoffReceipt:
                {
                    var receipt = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketHandoffReceiptV1>(result.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(receipt);
                    assertionHash = receipt.ReceiptHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(receipt) ||
                        !string.Equals(
                            receipt.ReceiptId,
                            result.DocumentId,
                            StringComparison.Ordinal) ||
                        result.Revision != 1 ||
                        !string.Equals(
                            receipt.ContractId,
                            AgentWorkPacketContractIds.HandoffReceipt,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            receipt.ContractId,
                            result.ContractId,
                            StringComparison.Ordinal) ||
                        receipt.Executable ||
                        receipt.GrantsSchedulingAuthority ||
                        receipt.GrantsTaskClaim ||
                        receipt.GrantsAuthority ||
                        !string.Equals(
                            receipt.ContentTreatment,
                            AgentWorkPacketContractIds.ContentTreatment,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt:
                {
                    var receipt = CortexCanonicalJson.Deserialize<
                        AgentWorkPacketAcceptanceReceiptV1>(
                            result.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(receipt);
                    assertionHash = receipt.ReceiptHash;
                    if (!AgentWorkPacketPolicy.HasValidHash(receipt) ||
                        !string.Equals(
                            receipt.ReceiptId,
                            result.DocumentId,
                            StringComparison.Ordinal) ||
                        result.Revision != 1 ||
                        !string.Equals(
                            receipt.ContractId,
                            AgentWorkPacketContractIds.AcceptanceReceipt,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            receipt.ContractId,
                            result.ContractId,
                            StringComparison.Ordinal) ||
                        receipt.Executable ||
                        receipt.GrantsSchedulingAuthority ||
                        receipt.GrantsTaskClaim ||
                        receipt.GrantsAuthority ||
                        !string.Equals(
                            receipt.ContentTreatment,
                            AgentWorkPacketContractIds.ContentTreatment,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.ProjectBrief:
                {
                    var brief =
                        CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                            result.CanonicalUtf8);
                    canonical = CortexCanonicalJson.Serialize(brief);
                    assertionHash = brief.ContextSetId;
                    if (!string.Equals(
                            brief.ContractId,
                            CortexProjectBriefContract.ContractId,
                            StringComparison.Ordinal) ||
                        !string.Equals(brief.ContractId, result.ContractId, StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.TenantId,
                            result.TenantId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectLinkId,
                            result.ProjectLinkId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectId,
                            result.ProjectId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectNamespace,
                            result.ProjectNamespace,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            brief.ProjectNamespace,
                            result.DocumentId,
                            StringComparison.Ordinal) ||
                        brief.BriefRevision != result.Revision ||
                        !string.Equals(
                            brief.ContextSetId,
                            CortexProjectBriefService.ComputeContextSetId(brief),
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    break;
                }
                default:
                    return false;
            }

            if (!canonical.AsSpan().SequenceEqual(result.CanonicalUtf8) ||
                !string.Equals(
                    assertionHash,
                    result.AssertionHash,
                    StringComparison.Ordinal))
            {
                return false;
            }
            assertionNode = JsonNode.Parse(result.CanonicalUtf8);
            return assertionNode is JsonObject;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            assertionNode = null;
            return false;
        }
    }

    private JsonObject RuntimeUnavailable(string operation) =>
        ToolError(
            _runtime?.Descriptor.IssueCode ?? "cortex_hybrid_not_ready",
            "The protected hybrid Cortex runtime is not ready.",
            operation);

    private static bool TryParseUtc(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexMcpContract.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidPrefixedHash(string value, string prefix) =>
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ValidSha256(string value) =>
        value.Length == 64 &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ValidAssertionHash(string value) =>
        ValidSha256(value) || ValidPrefixedHash(value, "ctx_");

    private static bool ValidFeedbackKind(string value) =>
        value is "correction" or "failure" or "preference" or "quality" or
            "regression" or "safety" or "success";

    private static T RequireAccepted<T>(CortexQueryResult<T> result, string operation)
        where T : class
    {
        if (!IsCleanAccepted(result))
            throw new InvalidOperationException($"The Cortex query service rejected the bounded {operation} operation.");
        return result.Value!;
    }

    private static bool IsCleanAccepted<T>(CortexQueryResult<T> result)
        where T : class =>
        result.Accepted && result.Value is not null && result.Diagnostics is not null &&
        result.Diagnostics.Count == 0 && !result.Diagnostics.Any();

    private static JsonObject RecordNotAvailable(string operation) =>
        ToolError(
            "cortex_record_not_available",
            "The record is unavailable in the requested read scope or does not exist.",
            operation);

    private static JsonObject ToolSuccess(JsonObject structuredContent)
    {
        var text = structuredContent.ToJsonString(McpProtocol.JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structuredContent.DeepClone(),
            ["isError"] = false
        };
    }

    private static JsonObject ToolFailure(JsonObject structuredContent)
    {
        var text = structuredContent.ToJsonString(McpProtocol.JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structuredContent.DeepClone(),
            ["isError"] = true
        };
    }

    private static JsonObject ToolError(
        string code,
        string message,
        string operation,
        IReadOnlyList<string>? findingCodes = null)
    {
        var detail = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["severity"] = "error",
            ["operation"] = operation,
            ["retryable"] = false
        };
        if (findingCodes is not null)
        {
            detail["findingCodes"] = new JsonArray(
                findingCodes
                    .Take(16)
                    .Select(value => JsonValue.Create(value))
                    .ToArray());
        }
        var diagnostic = new JsonObject
        {
            ["accepted"] = false,
            ["diagnostic"] = detail
        };
        var text = diagnostic.ToJsonString(McpProtocol.JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = diagnostic.DeepClone(),
            ["isError"] = true
        };
    }

    private static bool CanProjectLegacyRecord(CortexKnowledgeRecord record) =>
        record.Provenance is not null &&
        record.Provenance.SourceRevisions is not null &&
        record.Provenance.SourceRevisions.Count == 0 &&
        !record.Provenance.SourceRevisions.Any();

    private static CortexKnowledgeCatalog SnapshotResourceCatalog(
        CortexKnowledgeCatalog catalog,
        string defaultNamespace)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(catalog.ReleaseVersion) || catalog.ReleaseVersion.Length > 128 ||
            !string.Equals(catalog.SafetyNotice, CortexQueryPolicy.SafetyNotice, StringComparison.Ordinal) ||
            catalog.Records is null)
        {
            throw new InvalidOperationException("The Core query service returned invalid resource catalog metadata.");
        }

        var records = new List<CortexKnowledgeRecordSummary>(CortexMcpContract.MaximumResourceRecords);
        var identities = new HashSet<(string RecordId, string RevisionId)>();
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in catalog.Records)
        {
            if (records.Count == CortexMcpContract.MaximumResourceRecords || record is null ||
                !record.RecordId.StartsWith("krec_", StringComparison.Ordinal) ||
                !record.RevisionId.StartsWith("krev_", StringComparison.Ordinal) ||
                !ToolArgumentReader.IsKnowledgeIdentifier(record.RecordId) ||
                !ToolArgumentReader.IsKnowledgeIdentifier(record.RevisionId) ||
                !string.Equals(record.Namespace, defaultNamespace, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(record.Taxonomy) || record.Taxonomy.Length > 128 ||
                string.IsNullOrWhiteSpace(record.Title) || record.Title.Length > 512 ||
                string.IsNullOrWhiteSpace(record.Summary) || record.Summary.Length > 2048 ||
                !string.Equals(record.Freshness, "current", StringComparison.Ordinal) ||
                record.Sensitivity is not ("public" or "internal") ||
                !string.Equals(record.ContentTreatment, CortexQueryPolicy.ContentTreatment, StringComparison.Ordinal) ||
                record.Executable ||
                !identities.Add((record.RecordId, record.RevisionId)) ||
                !recordIds.Add(record.RecordId))
            {
                throw new InvalidOperationException("The Core query service returned an invalid or ambiguous resource catalog record.");
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

        return new CortexKnowledgeCatalog(
            catalog.ReleaseVersion,
            Array.AsReadOnly(records
                .OrderBy(record => record.RecordId, StringComparer.Ordinal)
                .ThenBy(record => record.RevisionId, StringComparer.Ordinal)
                .ToArray()),
            catalog.SafetyNotice);
    }

    private static bool TryParseRecordResourceUri(string uri, out string? recordId)
    {
        recordId = null;
        if (!uri.StartsWith(RecordResourcePrefix, StringComparison.Ordinal))
            return false;
        var candidate = uri[RecordResourcePrefix.Length..];
        if (!candidate.StartsWith("krec_", StringComparison.Ordinal) ||
            !ToolArgumentReader.IsKnowledgeIdentifier(candidate))
        {
            return false;
        }
        recordId = candidate;
        return true;
    }

    private static string ResourceUri(string recordId) => $"{RecordResourcePrefix}{recordId}";
}
