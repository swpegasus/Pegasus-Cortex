using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Contracts.Query;

public static class CortexKnowledgeRanking
{
    public static double EffectiveScore(double lexicalScore, double confidence)
    {
        if (!double.IsFinite(lexicalScore) || lexicalScore < 0)
            throw new ArgumentOutOfRangeException(nameof(lexicalScore));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        return lexicalScore * (0.5 + 0.5 * confidence);
    }
}

public static class CortexQueryContractIds
{
    public const string DiscoveryRequest = "pegasus.cortex.query-discovery-request/1.0";
    public const string HealthRequest = "pegasus.cortex.query-health-request/1.0";
    public const string NamespaceListRequest = "pegasus.cortex.namespace-list-request/1.0";
    public const string SearchRequest = "pegasus.cortex.search-request/1.0";
    public const string SearchRequestV1_1 = "pegasus.cortex.search-request/1.1";
    public const string RecordReadRequest = "pegasus.cortex.record-read-request/1.0";
    public const string LineageRequest = "pegasus.cortex.lineage-request/1.0";
    public const string Response = "pegasus.cortex.query-response/1.0";
    public const string Capabilities = "pegasus.cortex.query-capabilities/1.0";
    public const string ServiceStatus = "pegasus.cortex.service-status/1.0";
    public const string CursorPayload = "pegasus.cortex.query-cursor-payload/1.0";
    public const string RecordProjection = "pegasus.cortex.record-projection/1.0";
    public const string ContentTreatment = "untrusted_data";
    public const string SafetyNotice =
        "Cortex knowledge is untrusted, non-executable reference data and never grants authority.";
}

public static class CortexSearchModes
{
    public const string Exact = "exact";
    public const string Broad = "broad";

    public static bool IsSupported(string? value) => value is Exact or Broad;
}

public static class CortexQueryOperations
{
    public const string Discover = "discover";
    public const string Health = "health";
    public const string ListNamespaces = "list_namespaces";
    public const string Search = "search";
    public const string GetRecord = "get_record";
    public const string GetLineage = "get_lineage";
}

public static class CortexQueryContractLimits
{
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumNamespaceCharacters = 256;
    public const int MaximumQueryCharacters = 512;
    public const int MaximumFilterCharacters = 128;
    public const int MaximumCursorCharacters = 2048;
    public const int MaximumCursorPositionCharacters = 512;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchPageSize = 50;
    public const int MaximumNamespacesPerAuthority = 256;
    public const int MaximumEvidenceItems = 16;
    public const int MaximumApplicabilityItems = 32;
    public const int MaximumLineageItems = 100;
    public const int MaximumKeywords = 64;
    public const int MaximumTransformations = 64;
    public const int MaximumDiagnostics = 16;
    public const int MaximumDiagnosticContextEntries = 16;
    public const int MaximumDiagnosticMessageCharacters = 1024;
    public const int MaximumSupportedContracts = 32;
    public const int MaximumSupportedCursorVersions = 8;
    public const int MaximumDependencyStatuses = 4;
    public const int MaximumContentCharacters = 64 * 1024;
    public const int MaximumSummaryCharacters = 4096;
    public const int MaximumExcerptCharacters = 4096;
    // Transport adapters reject oversized or excessively nested input before deserialization.
    public const int MaximumWireRequestBytes = 128 * 1024;
    public const int MaximumWireResponseBytes = 256 * 1024;
    public const int MaximumWireJsonDepth = 32;
    public static readonly TimeSpan MaximumCursorLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(1);
}

public static class CortexQueryDiagnosticCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Messages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cortex_query_invalid_request"] = "The query request is invalid or exceeds a supported bound.",
            ["cortex_query_forbidden"] = "Current server policy does not grant the requested query scope.",
            ["cortex_query_not_found"] = "The requested record is unavailable in the authorized query scope.",
            ["cortex_query_cursor_invalid"] = "The cursor is invalid for the current request.",
            ["cortex_query_dependency_unavailable"] = "A required Cortex query dependency is unavailable.",
            ["cortex_query_internal_failure"] = "The Cortex query operation failed.",
        };

    public static string MessageFor(string code) =>
        Messages.TryGetValue(code, out var message)
            ? message
            : throw new ArgumentException("The wire diagnostic code is unsupported.", nameof(code));
}

/// <summary>
/// Decoded request contracts enforce semantic bounds. Every transport adapter must also
/// reject UTF-8 input over MaximumWireRequestBytes or JSON deeper than
/// MaximumWireJsonDepth before invoking a serializer, and cap serialized UTF-8 output at
/// MaximumWireResponseBytes, so allocation safety does not depend on decoded values.
/// A legacy character-counting adapter must migrate before advertising these limits.
/// </summary>
public sealed record CortexDiscoveryRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexDiscoveryRequestV1(int schemaVersion, string contractId, string requestId)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            schemaVersion,
            contractId,
            CortexQueryContractIds.DiscoveryRequest,
            requestId);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
}

public sealed record CortexHealthRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexHealthRequestV1(int schemaVersion, string contractId, string requestId)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            schemaVersion,
            contractId,
            CortexQueryContractIds.HealthRequest,
            requestId);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
}

public sealed record CortexNamespaceListRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexNamespaceListRequestV1(
        int schemaVersion,
        string contractId,
        string requestId,
        int pageSize,
        string? cursor)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            schemaVersion,
            contractId,
            CortexQueryContractIds.NamespaceListRequest,
            requestId);
        if (pageSize is < 1 or > CortexQueryContractLimits.MaximumPageSize ||
            !CortexQueryContractBoundary.IsOpaqueCursorBounded(cursor))
            throw new ArgumentException("The namespace-list request is invalid or unbounded.");
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
        PageSize = pageSize;
        Cursor = cursor;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
    public int PageSize { get; }
    public string? Cursor { get; }
}

public sealed record CortexSearchRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexSearchRequestV1(
        int schemaVersion,
        string contractId,
        string requestId,
        string @namespace,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        string maximumSensitivity,
        IReadOnlyList<string> freshnessStates,
        int pageSize,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(freshnessStates);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
        Namespace = @namespace;
        Text = text;
        Taxonomy = taxonomy;
        ProductId = productId;
        ComponentId = componentId;
        MaximumSensitivity = maximumSensitivity;
        FreshnessStates = CortexQuerySnapshots.BoundedList(
            freshnessStates,
            5,
            nameof(freshnessStates),
            requireNonEmpty: true);
        PageSize = pageSize;
        Cursor = cursor;
        CortexQuerySnapshots.ValidateRequestHeader(
            SchemaVersion,
            ContractId,
            CortexQueryContractIds.SearchRequest,
            RequestId);
        if (!CortexQuerySnapshots.ValidNamespace(Namespace) ||
            !CortexQuerySnapshots.ValidBoundedText(Text, CortexQueryContractLimits.MaximumQueryCharacters, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidOptionalIdentifiers(Taxonomy, ProductId, ComponentId) ||
            MaximumSensitivity is not ("public" or "internal" or "confidential" or "restricted") ||
            FreshnessStates.Distinct(StringComparer.Ordinal).Count() != FreshnessStates.Count ||
            FreshnessStates.Any(state => state is not ("current" or "stale" or "blocked" or "unverified" or "unknown")) ||
            PageSize is < 1 or > CortexQueryContractLimits.MaximumSearchPageSize ||
            !CortexQueryContractBoundary.IsOpaqueCursorBounded(Cursor))
        {
            throw new ArgumentException("The search request is invalid or unbounded.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
    public string Namespace { get; }
    public string Text { get; }
    public string? Taxonomy { get; }
    public string? ProductId { get; }
    public string? ComponentId { get; }
    public string MaximumSensitivity { get; }
    public IReadOnlyList<string> FreshnessStates { get; }
    public int PageSize { get; }
    public string? Cursor { get; }
}

/// <summary>
/// Search v1.1 adds an explicit retrieval mode while retaining the v1.0 exact
/// request unchanged. Broad is opt-in; exact remains the compatibility default.
/// </summary>
public sealed record CortexSearchRequestV1_1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexSearchRequestV1_1(
        int schemaVersion,
        string contractId,
        string requestId,
        string @namespace,
        string text,
        string? taxonomy,
        string? productId,
        string? componentId,
        string maximumSensitivity,
        IReadOnlyList<string> freshnessStates,
        int pageSize,
        string? cursor,
        string mode)
    {
        ArgumentNullException.ThrowIfNull(freshnessStates);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
        Namespace = @namespace;
        Text = text;
        Taxonomy = taxonomy;
        ProductId = productId;
        ComponentId = componentId;
        MaximumSensitivity = maximumSensitivity;
        FreshnessStates = CortexQuerySnapshots.BoundedList(
            freshnessStates,
            5,
            nameof(freshnessStates),
            requireNonEmpty: true);
        PageSize = pageSize;
        Cursor = cursor;
        Mode = mode;
        CortexQuerySnapshots.ValidateRequestHeader(
            SchemaVersion,
            ContractId,
            CortexQueryContractIds.SearchRequestV1_1,
            RequestId);
        if (!CortexQuerySnapshots.ValidNamespace(Namespace) ||
            !CortexQuerySnapshots.ValidBoundedText(Text, CortexQueryContractLimits.MaximumQueryCharacters, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidOptionalIdentifiers(Taxonomy, ProductId, ComponentId) ||
            MaximumSensitivity is not ("public" or "internal" or "confidential" or "restricted") ||
            FreshnessStates.Distinct(StringComparer.Ordinal).Count() != FreshnessStates.Count ||
            FreshnessStates.Any(state => state is not ("current" or "stale" or "blocked" or "unverified" or "unknown")) ||
            PageSize is < 1 or > CortexQueryContractLimits.MaximumSearchPageSize ||
            !CortexQueryContractBoundary.IsOpaqueCursorBounded(Cursor) ||
            !CortexSearchModes.IsSupported(Mode))
        {
            throw new ArgumentException("The search request is invalid or unbounded.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
    public string Namespace { get; }
    public string Text { get; }
    public string? Taxonomy { get; }
    public string? ProductId { get; }
    public string? ComponentId { get; }
    public string MaximumSensitivity { get; }
    public IReadOnlyList<string> FreshnessStates { get; }
    public int PageSize { get; }
    public string? Cursor { get; }
    public string Mode { get; }
}

public sealed record CortexRecordReadRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexRecordReadRequestV1(
        int schemaVersion,
        string contractId,
        string requestId,
        string @namespace,
        string recordId,
        string revisionId)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            schemaVersion,
            contractId,
            CortexQueryContractIds.RecordReadRequest,
            requestId);
        if (!CortexQuerySnapshots.ValidNamespace(@namespace) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(recordId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(revisionId))
            throw new ArgumentException("The exact-record request is invalid or unbounded.");
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
        Namespace = @namespace;
        RecordId = recordId;
        RevisionId = revisionId;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
    public string Namespace { get; }
    public string RecordId { get; }
    public string RevisionId { get; }
}

public sealed record CortexLineageRequestV1 : ICortexQueryRequestV1
{
    [JsonConstructor]
    public CortexLineageRequestV1(
        int schemaVersion,
        string contractId,
        string requestId,
        string @namespace,
        string recordId,
        string revisionId,
        string direction,
        int pageSize,
        string? cursor)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            schemaVersion,
            contractId,
            CortexQueryContractIds.LineageRequest,
            requestId);
        if (!CortexQuerySnapshots.ValidNamespace(@namespace) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(recordId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(revisionId) ||
            direction is not ("ancestors" or "descendants") ||
            pageSize is < 1 or > CortexQueryContractLimits.MaximumPageSize ||
            !CortexQueryContractBoundary.IsOpaqueCursorBounded(cursor))
            throw new ArgumentException("The lineage request is invalid or unbounded.");
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RequestId = requestId;
        Namespace = @namespace;
        RecordId = recordId;
        RevisionId = revisionId;
        Direction = direction;
        PageSize = pageSize;
        Cursor = cursor;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RequestId { get; }
    public string Namespace { get; }
    public string RecordId { get; }
    public string RevisionId { get; }
    public string Direction { get; }
    public int PageSize { get; }
    public string? Cursor { get; }
}

/// <summary>
/// Server-derived authority. Requests and advertised capabilities are deliberately absent
/// because neither can create or extend a namespace grant.
/// </summary>
public sealed record CortexQueryAuthorityContext
{
    public CortexQueryAuthorityContext(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        IEnumerable<CortexQueryNamespaceGrant> namespaceGrants)
    {
        ArgumentNullException.ThrowIfNull(namespaceGrants);
        TenantId = tenantId;
        AuthorityId = authorityId;
        SubjectId = subjectId;
        ServiceId = serviceId;
        PolicyRevision = policyRevision;
        NamespaceGrants = CortexQuerySnapshots.BoundedList(
            namespaceGrants,
            CortexQueryContractLimits.MaximumNamespacesPerAuthority,
            nameof(namespaceGrants));
    }

    public string TenantId { get; }
    public string AuthorityId { get; }
    public string SubjectId { get; }
    public string ServiceId { get; }
    public long PolicyRevision { get; }
    public IReadOnlyList<CortexQueryNamespaceGrant> NamespaceGrants { get; }
}

public sealed record CortexQueryNamespaceGrant
{
    public CortexQueryNamespaceGrant(
        string namespaceName,
        IEnumerable<string> operations,
        string maximumSensitivity,
        IEnumerable<string> freshnessStates,
        IEnumerable<string> recordStates)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(freshnessStates);
        ArgumentNullException.ThrowIfNull(recordStates);
        Namespace = namespaceName;
        Operations = CortexQuerySnapshots.BoundedSet(operations, 4, nameof(operations), requireNonEmpty: true);
        MaximumSensitivity = maximumSensitivity;
        FreshnessStates = CortexQuerySnapshots.BoundedSet(freshnessStates, 5, nameof(freshnessStates), requireNonEmpty: true);
        RecordStates = CortexQuerySnapshots.BoundedSet(recordStates, 3, nameof(recordStates), requireNonEmpty: true);
    }

    public string Namespace { get; }
    public IReadOnlySet<string> Operations { get; }
    public string MaximumSensitivity { get; }
    public IReadOnlySet<string> FreshnessStates { get; }
    public IReadOnlySet<string> RecordStates { get; }
}

public sealed record CortexQueryNamespaceAccess
{
    public CortexQueryNamespaceAccess(
        string tenantId,
        string namespaceName,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        string operation,
        string maximumSensitivity,
        IEnumerable<string> freshnessStates,
        IEnumerable<string> recordStates)
    {
        TenantId = tenantId;
        Namespace = namespaceName;
        AuthorityId = authorityId;
        SubjectId = subjectId;
        ServiceId = serviceId;
        PolicyRevision = policyRevision;
        Operation = operation;
        MaximumSensitivity = maximumSensitivity;
        FreshnessStates = CortexQuerySnapshots.BoundedSet(
            freshnessStates,
            5,
            nameof(freshnessStates),
            requireNonEmpty: true);
        RecordStates = CortexQuerySnapshots.BoundedSet(
            recordStates,
            3,
            nameof(recordStates),
            requireNonEmpty: true);
    }

    public string TenantId { get; }
    public string Namespace { get; }
    public string AuthorityId { get; }
    public string SubjectId { get; }
    public string ServiceId { get; }
    public long PolicyRevision { get; }
    public string Operation { get; }
    public string MaximumSensitivity { get; }
    public IReadOnlySet<string> FreshnessStates { get; }
    public IReadOnlySet<string> RecordStates { get; }
}

public sealed record CortexQueryLimitsV1(
    int MaximumPageSize,
    int MaximumSearchPageSize,
    int MaximumQueryCharacters,
    int MaximumCursorCharacters,
    int MaximumWireRequestBytes,
    int MaximumWireResponseBytes,
    int MaximumWireJsonDepth,
    int MaximumApplicabilityItems,
    int MaximumEvidenceItems,
    int MaximumLineageItems);

public sealed record CortexOperationSupportV1(
    string Operation,
    bool Supported,
    bool NamespaceScoped,
    bool Pageable,
    int MaximumPageSize);

/// <summary>
/// Describes protocol support only. A supported operation still requires separately
/// captured server authority before it can read any namespace.
/// </summary>
public sealed record CortexQueryCapabilitiesV1
{
    [JsonConstructor]
    public CortexQueryCapabilitiesV1(
        int schemaVersion,
        string contractId,
        string serviceVersion,
        IReadOnlyList<string> supportedRequestContracts,
        IReadOnlyList<string> supportedResponseContracts,
        IReadOnlyList<string> supportedCursorVersions,
        IReadOnlyList<CortexOperationSupportV1> operations,
        CortexQueryLimitsV1 limits)
    {
        ArgumentNullException.ThrowIfNull(supportedRequestContracts);
        ArgumentNullException.ThrowIfNull(supportedResponseContracts);
        ArgumentNullException.ThrowIfNull(supportedCursorVersions);
        ArgumentNullException.ThrowIfNull(operations);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        ServiceVersion = serviceVersion;
        SupportedRequestContracts = CortexQuerySnapshots.BoundedList(
            supportedRequestContracts,
            CortexQueryContractLimits.MaximumSupportedContracts,
            nameof(supportedRequestContracts),
            requireNonEmpty: true);
        SupportedResponseContracts = CortexQuerySnapshots.BoundedList(
            supportedResponseContracts,
            CortexQueryContractLimits.MaximumSupportedContracts,
            nameof(supportedResponseContracts),
            requireNonEmpty: true);
        SupportedCursorVersions = CortexQuerySnapshots.BoundedList(
            supportedCursorVersions,
            CortexQueryContractLimits.MaximumSupportedCursorVersions,
            nameof(supportedCursorVersions),
            requireNonEmpty: true);
        Operations = CortexQuerySnapshots.BoundedList(operations, 6, nameof(operations), requireNonEmpty: true);
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (SchemaVersion != 1 || !string.Equals(ContractId, CortexQueryContractIds.Capabilities, StringComparison.Ordinal) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(ServiceVersion) ||
            SupportedRequestContracts.Distinct(StringComparer.Ordinal).Count() != SupportedRequestContracts.Count ||
            SupportedResponseContracts.Distinct(StringComparer.Ordinal).Count() != SupportedResponseContracts.Count ||
            SupportedCursorVersions.Distinct(StringComparer.Ordinal).Count() != SupportedCursorVersions.Count ||
            SupportedRequestContracts.Any(value => !CortexQuerySnapshots.IsBoundedContractId(value)) ||
            SupportedResponseContracts.Any(value => !CortexQuerySnapshots.IsBoundedContractId(value)) ||
            SupportedCursorVersions.Any(value => !CortexQuerySnapshots.IsBoundedIdentifier(value)) ||
            Operations.Select(value => value.Operation).Distinct(StringComparer.Ordinal).Count() != Operations.Count ||
            Operations.Any(value => !ValidOperationSupport(value)) ||
            Limits != new CortexQueryLimitsV1(
                CortexQueryContractLimits.MaximumPageSize,
                CortexQueryContractLimits.MaximumSearchPageSize,
                CortexQueryContractLimits.MaximumQueryCharacters,
                CortexQueryContractLimits.MaximumCursorCharacters,
                CortexQueryContractLimits.MaximumWireRequestBytes,
                CortexQueryContractLimits.MaximumWireResponseBytes,
                CortexQueryContractLimits.MaximumWireJsonDepth,
                CortexQueryContractLimits.MaximumApplicabilityItems,
                CortexQueryContractLimits.MaximumEvidenceItems,
                CortexQueryContractLimits.MaximumLineageItems))
        {
            throw new ArgumentException("The capability projection is inconsistent or unbounded.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string ServiceVersion { get; }
    public IReadOnlyList<string> SupportedRequestContracts { get; }
    public IReadOnlyList<string> SupportedResponseContracts { get; }
    public IReadOnlyList<string> SupportedCursorVersions { get; }
    public IReadOnlyList<CortexOperationSupportV1> Operations { get; }
    public CortexQueryLimitsV1 Limits { get; }

    private static bool ValidOperationSupport(CortexOperationSupportV1 item)
    {
        if (item is null || item.Operation is not (
                CortexQueryOperations.Discover or CortexQueryOperations.Health or
                CortexQueryOperations.ListNamespaces or CortexQueryOperations.Search or
                CortexQueryOperations.GetRecord or CortexQueryOperations.GetLineage))
            return false;

        var pageable = item.Operation is CortexQueryOperations.ListNamespaces or
            CortexQueryOperations.Search or CortexQueryOperations.GetLineage;
        var namespaceScoped = item.Operation is CortexQueryOperations.Search or
            CortexQueryOperations.GetRecord or CortexQueryOperations.GetLineage;
        var expectedMaximum = item.Operation == CortexQueryOperations.Search
            ? CortexQueryContractLimits.MaximumSearchPageSize
            : pageable
                ? CortexQueryContractLimits.MaximumPageSize
                : 0;
        return item.Pageable == pageable && item.NamespaceScoped == namespaceScoped &&
            item.MaximumPageSize == expectedMaximum;
    }
}

public sealed record CortexDependencyStatusV1(
    string Component,
    bool Required,
    bool Ready,
    string Status,
    string? Revision,
    string? DiagnosticCode,
    DateTimeOffset ObservedAt);

public sealed record CortexServiceStatusV1
{
    [JsonConstructor]
    public CortexServiceStatusV1(
        int schemaVersion,
        string contractId,
        string serviceVersion,
        string processHealth,
        string readiness,
        string? indexGeneration,
        DateTimeOffset observedAt,
        IReadOnlyList<CortexDependencyStatusV1> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        ServiceVersion = serviceVersion;
        ProcessHealth = processHealth;
        Readiness = readiness;
        IndexGeneration = indexGeneration;
        ObservedAt = observedAt;
        Dependencies = CortexQuerySnapshots.BoundedList(
            dependencies,
            CortexQueryContractLimits.MaximumDependencyStatuses,
            nameof(dependencies),
            requireNonEmpty: true);
        var requiredComponents = new HashSet<string>(StringComparer.Ordinal)
        {
            "store", "schema", "index", "gateway"
        };
        if (SchemaVersion != 1 || !string.Equals(ContractId, CortexQueryContractIds.ServiceStatus, StringComparison.Ordinal) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(ServiceVersion) || ObservedAt.Offset != TimeSpan.Zero ||
            ProcessHealth is not ("healthy" or "degraded" or "unhealthy") ||
            Readiness is not ("ready" or "not_ready") ||
            Dependencies.Count != requiredComponents.Count ||
            Dependencies.Select(item => item.Component).ToHashSet(StringComparer.Ordinal).SetEquals(requiredComponents) is false ||
            Dependencies.Any(item => !CortexQuerySnapshots.ValidDependencyStatus(item)) ||
            Readiness == "ready" &&
                (ProcessHealth == "unhealthy" || Dependencies.Any(item => item.Required && !item.Ready)) ||
            Readiness == "ready" && string.IsNullOrWhiteSpace(IndexGeneration))
        {
            throw new ArgumentException("The service health/readiness projection is inconsistent.");
        }
        if (IndexGeneration is not null)
            CortexQuerySnapshots.RequireBoundedIdentifier(IndexGeneration, nameof(indexGeneration));
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string ServiceVersion { get; }
    public string ProcessHealth { get; }
    public string Readiness { get; }
    public string? IndexGeneration { get; }
    public DateTimeOffset ObservedAt { get; }
    public IReadOnlyList<CortexDependencyStatusV1> Dependencies { get; }
}

public sealed record CortexNamespaceProjectionV1(
    string Namespace,
    long PolicyRevision);

public sealed record CortexEvidenceProjectionV1(
    string EvidenceId,
    string Reference,
    string MediaType,
    string Sha256,
    string Verification,
    DateTimeOffset ObservedAt,
    string? SourceVersion);

public sealed record CortexApplicabilityProjectionV1(
    string ProductId,
    string ComponentId,
    string? MinimumVersion,
    string? MaximumVersion,
    string State);

/// <summary>
/// A host must re-authorize the target namespace of every projected link. Authorization
/// of the root record is not transitive to lineage or provenance links.
/// </summary>
public sealed record CortexLineageLinkV1(
    string Relation,
    string Namespace,
    string RecordId,
    string RevisionId,
    string ContentHash);

public sealed record CortexConfidenceProjectionV1(
    int ValueBasisPoints,
    string Basis);

public sealed record CortexFreshnessProjectionV1(
    string State,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ReviewAfter,
    string Basis);

public sealed record CortexProvenanceProjectionV1
{
    [JsonConstructor]
    public CortexProvenanceProjectionV1(
        string producerContract,
        string? producerAuthorityId,
        string? producerSubjectId,
        string producerServiceId,
        string? agentId,
        string? instanceId,
        string? machineId,
        string? operationId,
        string? taskId,
        string? correlationId,
        IReadOnlyList<CortexLineageLinkV1> sourceRevisions,
        bool sourceRevisionsComplete,
        IReadOnlyList<string> transformations,
        bool transformationsComplete)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisions);
        ArgumentNullException.ThrowIfNull(transformations);
        ProducerContract = producerContract;
        ProducerAuthorityId = producerAuthorityId;
        ProducerSubjectId = producerSubjectId;
        ProducerServiceId = producerServiceId;
        AgentId = agentId;
        InstanceId = instanceId;
        MachineId = machineId;
        OperationId = operationId;
        TaskId = taskId;
        CorrelationId = correlationId;
        SourceRevisions = CortexQuerySnapshots.BoundedList(
            sourceRevisions,
            CortexQueryContractLimits.MaximumLineageItems,
            nameof(sourceRevisions));
        SourceRevisionsComplete = sourceRevisionsComplete;
        Transformations = CortexQuerySnapshots.BoundedList(
            transformations,
            CortexQueryContractLimits.MaximumTransformations,
            nameof(transformations));
        TransformationsComplete = transformationsComplete;
        if (!CortexQuerySnapshots.IsBoundedContractId(ProducerContract) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(ProducerServiceId) ||
            !CortexQuerySnapshots.ValidOptionalIdentifiers(
                ProducerAuthorityId, ProducerSubjectId, AgentId, InstanceId, MachineId,
                OperationId, TaskId, CorrelationId) ||
            SourceRevisions.Any(item => !CortexQuerySnapshots.ValidLineage(item)) ||
            Transformations.Any(item => !CortexQuerySnapshots.IsBoundedIdentifier(item)) ||
            !SourceRevisionsComplete || !TransformationsComplete)
        {
            throw new ArgumentException("The provenance projection is invalid, incomplete, or unbounded.");
        }
    }

    public string ProducerContract { get; }
    public string? ProducerAuthorityId { get; }
    public string? ProducerSubjectId { get; }
    public string ProducerServiceId { get; }
    public string? AgentId { get; }
    public string? InstanceId { get; }
    public string? MachineId { get; }
    public string? OperationId { get; }
    public string? TaskId { get; }
    public string? CorrelationId { get; }
    public IReadOnlyList<CortexLineageLinkV1> SourceRevisions { get; }
    public bool SourceRevisionsComplete { get; }
    public IReadOnlyList<string> Transformations { get; }
    public bool TransformationsComplete { get; }
}

public sealed record CortexRecordSummaryV1
{
    [JsonConstructor]
    public CortexRecordSummaryV1(
        string recordId,
        string revisionId,
        string @namespace,
        string taxonomy,
        string title,
        string summary,
        string contentExcerpt,
        bool contentTruncated,
        string contentHash,
        string recordStatus,
        CortexConfidenceProjectionV1 confidence,
        CortexFreshnessProjectionV1 freshness,
        string sensitivity,
        int relevanceScore,
        string rankingVersion,
        IReadOnlyList<CortexApplicabilityProjectionV1> applicability,
        IReadOnlyList<CortexEvidenceProjectionV1> evidence,
        bool evidenceComplete,
        CortexProvenanceProjectionV1 provenance,
        string contentTreatment,
        bool executable,
        string safetyNotice)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        ArgumentNullException.ThrowIfNull(evidence);
        RecordId = recordId;
        RevisionId = revisionId;
        Namespace = @namespace;
        Taxonomy = taxonomy;
        Title = title;
        Summary = summary;
        ContentExcerpt = contentExcerpt;
        ContentTruncated = contentTruncated;
        ContentHash = contentHash;
        RecordStatus = recordStatus;
        Confidence = confidence ?? throw new ArgumentNullException(nameof(confidence));
        Freshness = freshness ?? throw new ArgumentNullException(nameof(freshness));
        Sensitivity = sensitivity;
        RelevanceScore = relevanceScore;
        RankingVersion = rankingVersion;
        Applicability = CortexQuerySnapshots.BoundedList(
            applicability,
            CortexQueryContractLimits.MaximumApplicabilityItems,
            nameof(applicability),
            requireNonEmpty: true);
        Evidence = CortexQuerySnapshots.BoundedList(
            evidence,
            CortexQueryContractLimits.MaximumEvidenceItems,
            nameof(evidence));
        if (!evidenceComplete)
            throw new ArgumentException("The v1 search projection requires complete bounded evidence.", nameof(evidenceComplete));
        EvidenceComplete = evidenceComplete;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (!Provenance.SourceRevisionsComplete || !Provenance.TransformationsComplete)
            throw new ArgumentException("The v1 search projection requires complete bounded provenance.", nameof(provenance));
        ContentTreatment = contentTreatment;
        Executable = executable;
        SafetyNotice = safetyNotice;
        if (!CortexQuerySnapshots.ValidRecordIdentity(RecordId, RevisionId, Namespace, Taxonomy) ||
            !CortexQuerySnapshots.ValidBoundedText(Title, 512, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidBoundedText(Summary, CortexQueryContractLimits.MaximumSummaryCharacters, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidBoundedText(ContentExcerpt, CortexQueryContractLimits.MaximumExcerptCharacters, allowEmpty: true) ||
            !CortexQuerySnapshots.IsHash(ContentHash) || RecordStatus is not ("active" or "superseded" or "archived") ||
            !CortexQuerySnapshots.ValidConfidence(Confidence) || !CortexQuerySnapshots.ValidFreshness(Freshness) ||
            Sensitivity is not ("public" or "internal" or "confidential" or "restricted") ||
            RelevanceScore is < 0 or > 1_000_000 || !CortexQuerySnapshots.IsBoundedIdentifier(RankingVersion) ||
            Applicability.Any(item => !CortexQuerySnapshots.ValidApplicability(item)) ||
            Evidence.Any(item => !CortexQuerySnapshots.ValidEvidence(item)) ||
            !string.Equals(ContentTreatment, CortexQueryContractIds.ContentTreatment, StringComparison.Ordinal) ||
            Executable || !string.Equals(SafetyNotice, CortexQueryContractIds.SafetyNotice, StringComparison.Ordinal))
        {
            throw new ArgumentException("The search record projection is invalid or unbounded.");
        }
    }

    public string RecordId { get; }
    public string RevisionId { get; }
    public string Namespace { get; }
    public string Taxonomy { get; }
    public string Title { get; }
    public string Summary { get; }
    public string ContentExcerpt { get; }
    public bool ContentTruncated { get; }
    public string ContentHash { get; }
    public string RecordStatus { get; }
    public CortexConfidenceProjectionV1 Confidence { get; }
    public CortexFreshnessProjectionV1 Freshness { get; }
    public string Sensitivity { get; }
    public int RelevanceScore { get; }
    public string RankingVersion { get; }
    public IReadOnlyList<CortexApplicabilityProjectionV1> Applicability { get; }
    public IReadOnlyList<CortexEvidenceProjectionV1> Evidence { get; }
    public bool EvidenceComplete { get; }
    public CortexProvenanceProjectionV1 Provenance { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public string SafetyNotice { get; }
}

public sealed record CortexRecordProjectionV1
{
    [JsonConstructor]
    public CortexRecordProjectionV1(
        int schemaVersion,
        string contractId,
        string recordId,
        string revisionId,
        string @namespace,
        string taxonomy,
        string title,
        string summary,
        string content,
        IReadOnlyList<string> keywords,
        IReadOnlyList<CortexApplicabilityProjectionV1> applicability,
        IReadOnlyList<CortexEvidenceProjectionV1> evidence,
        bool evidenceComplete,
        CortexProvenanceProjectionV1 provenance,
        string sensitivity,
        CortexConfidenceProjectionV1 confidence,
        CortexFreshnessProjectionV1 freshness,
        string recordStatus,
        bool trainingEligible,
        string contentTreatment,
        bool executable,
        DateTimeOffset createdAt,
        string contentHash,
        string safetyNotice)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentNullException.ThrowIfNull(applicability);
        ArgumentNullException.ThrowIfNull(evidence);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        RecordId = recordId;
        RevisionId = revisionId;
        Namespace = @namespace;
        Taxonomy = taxonomy;
        Title = title;
        Summary = summary;
        Content = content;
        Keywords = CortexQuerySnapshots.BoundedList(
            keywords,
            CortexQueryContractLimits.MaximumKeywords,
            nameof(keywords));
        Applicability = CortexQuerySnapshots.BoundedList(
            applicability,
            CortexQueryContractLimits.MaximumApplicabilityItems,
            nameof(applicability),
            requireNonEmpty: true);
        Evidence = CortexQuerySnapshots.BoundedList(
            evidence,
            CortexQueryContractLimits.MaximumEvidenceItems,
            nameof(evidence));
        if (!evidenceComplete)
            throw new ArgumentException("The v1 record projection requires complete bounded evidence.", nameof(evidenceComplete));
        EvidenceComplete = evidenceComplete;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (!Provenance.SourceRevisionsComplete || !Provenance.TransformationsComplete)
            throw new ArgumentException("The v1 record projection requires complete bounded provenance.", nameof(provenance));
        Sensitivity = sensitivity;
        Confidence = confidence ?? throw new ArgumentNullException(nameof(confidence));
        Freshness = freshness ?? throw new ArgumentNullException(nameof(freshness));
        RecordStatus = recordStatus;
        TrainingEligible = trainingEligible;
        ContentTreatment = contentTreatment;
        Executable = executable;
        CreatedAt = createdAt;
        ContentHash = contentHash;
        SafetyNotice = safetyNotice;
        if (SchemaVersion != 1 || !string.Equals(ContractId, CortexQueryContractIds.RecordProjection, StringComparison.Ordinal) ||
            !CortexQuerySnapshots.ValidRecordIdentity(RecordId, RevisionId, Namespace, Taxonomy) ||
            !CortexQuerySnapshots.ValidBoundedText(Title, 512, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidBoundedText(Summary, CortexQueryContractLimits.MaximumSummaryCharacters, allowEmpty: false) ||
            !CortexQuerySnapshots.ValidBoundedText(Content, CortexQueryContractLimits.MaximumContentCharacters, allowEmpty: false) ||
            Keywords.Any(item => !CortexQuerySnapshots.IsBoundedIdentifier(item)) ||
            Keywords.Distinct(StringComparer.Ordinal).Count() != Keywords.Count ||
            Applicability.Any(item => !CortexQuerySnapshots.ValidApplicability(item)) ||
            Evidence.Any(item => !CortexQuerySnapshots.ValidEvidence(item)) ||
            Sensitivity is not ("public" or "internal" or "confidential" or "restricted") ||
            !CortexQuerySnapshots.ValidConfidence(Confidence) || !CortexQuerySnapshots.ValidFreshness(Freshness) ||
            RecordStatus is not ("active" or "superseded" or "archived") ||
            !string.Equals(ContentTreatment, CortexQueryContractIds.ContentTreatment, StringComparison.Ordinal) ||
            Executable || CreatedAt.Offset != TimeSpan.Zero || !CortexQuerySnapshots.IsHash(ContentHash) ||
            !string.Equals(SafetyNotice, CortexQueryContractIds.SafetyNotice, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exact record projection is invalid or unbounded.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string RecordId { get; }
    public string RevisionId { get; }
    public string Namespace { get; }
    public string Taxonomy { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Content { get; }
    public IReadOnlyList<string> Keywords { get; }
    public IReadOnlyList<CortexApplicabilityProjectionV1> Applicability { get; }
    public IReadOnlyList<CortexEvidenceProjectionV1> Evidence { get; }
    public bool EvidenceComplete { get; }
    public CortexProvenanceProjectionV1 Provenance { get; }
    public string Sensitivity { get; }
    public CortexConfidenceProjectionV1 Confidence { get; }
    public CortexFreshnessProjectionV1 Freshness { get; }
    public string RecordStatus { get; }
    public bool TrainingEligible { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public DateTimeOffset CreatedAt { get; }
    public string ContentHash { get; }
    public string SafetyNotice { get; }
}

public sealed record CortexPageMetadataV1
{
    [JsonConstructor]
    public CortexPageMetadataV1(
        int pageSize,
        int returnedCount,
        string? nextCursor,
        string indexGeneration,
        DateTimeOffset snapshotAt)
    {
        if (pageSize is < 1 or > CortexQueryContractLimits.MaximumPageSize ||
            returnedCount < 0 || returnedCount > pageSize ||
            nextCursor is not null && returnedCount == 0 ||
            !CortexQueryContractBoundary.IsOpaqueCursorBounded(nextCursor) ||
            snapshotAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The page metadata is invalid or unbounded.");
        }
        PageSize = pageSize;
        ReturnedCount = returnedCount;
        NextCursor = nextCursor;
        IndexGeneration = CortexQuerySnapshots.RequireBoundedIdentifier(indexGeneration, nameof(indexGeneration));
        SnapshotAt = snapshotAt;
    }

    public int PageSize { get; }
    public int ReturnedCount { get; }
    public string? NextCursor { get; }
    public string IndexGeneration { get; }
    public DateTimeOffset SnapshotAt { get; }
}

public sealed record CortexNamespacePageV1
{
    [JsonConstructor]
    public CortexNamespacePageV1(
        IReadOnlyList<CortexNamespaceProjectionV1> items,
        CortexPageMetadataV1 page)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = CortexQuerySnapshots.BoundedList(
            items,
            CortexQueryContractLimits.MaximumPageSize,
            nameof(items));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        if (Page.ReturnedCount != Items.Count ||
            Items.Any(item => !CortexQuerySnapshots.ValidNamespace(item.Namespace) || item.PolicyRevision <= 0) ||
            Items.Select(item => item.Namespace).Distinct(StringComparer.Ordinal).Count() != Items.Count)
        {
            throw new ArgumentException("The namespace page is invalid or does not match its immutable item snapshot.", nameof(page));
        }
    }

    public IReadOnlyList<CortexNamespaceProjectionV1> Items { get; }
    public CortexPageMetadataV1 Page { get; }
}

public sealed record CortexSearchPageV1
{
    [JsonConstructor]
    public CortexSearchPageV1(
        string queryFingerprint,
        IReadOnlyList<CortexRecordSummaryV1> items,
        CortexPageMetadataV1 page)
    {
        ArgumentNullException.ThrowIfNull(items);
        QueryFingerprint = queryFingerprint;
        Items = CortexQuerySnapshots.BoundedList(
            items,
            CortexQueryContractLimits.MaximumSearchPageSize,
            nameof(items));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        if (!CortexQuerySnapshots.IsHash(QueryFingerprint) ||
            Page.ReturnedCount != Items.Count || Page.PageSize > CortexQueryContractLimits.MaximumSearchPageSize)
            throw new ArgumentException("The search page metadata does not match its immutable item snapshot.", nameof(page));
    }

    public string QueryFingerprint { get; }
    public IReadOnlyList<CortexRecordSummaryV1> Items { get; }
    public CortexPageMetadataV1 Page { get; }
}

public sealed record CortexRecordReadResultV1
{
    [JsonConstructor]
    public CortexRecordReadResultV1(CortexRecordProjectionV1 record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public CortexRecordProjectionV1 Record { get; }
}

public sealed record CortexLineagePageV1
{
    [JsonConstructor]
    public CortexLineagePageV1(
        string recordId,
        string revisionId,
        string direction,
        IReadOnlyList<CortexLineageLinkV1> items,
        CortexPageMetadataV1 page)
    {
        ArgumentNullException.ThrowIfNull(items);
        RecordId = recordId;
        RevisionId = revisionId;
        Direction = direction;
        Items = CortexQuerySnapshots.BoundedList(
            items,
            CortexQueryContractLimits.MaximumLineageItems,
            nameof(items));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        if (Page.ReturnedCount != Items.Count)
            throw new ArgumentException("The lineage page count does not match its immutable item snapshot.", nameof(page));
        if (!CortexQuerySnapshots.IsBoundedIdentifier(RecordId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(RevisionId) || Direction is not ("ancestors" or "descendants") ||
            Items.Any(item => !CortexQuerySnapshots.ValidLineage(item)))
            throw new ArgumentException("The lineage page is invalid or contains an unsafe cross-record projection.");
    }

    public string RecordId { get; }
    public string RevisionId { get; }
    public string Direction { get; }
    public IReadOnlyList<CortexLineageLinkV1> Items { get; }
    public CortexPageMetadataV1 Page { get; }
}

/// <summary>
/// Host-internal cursor state. A transport exposes only an opaque protected token; this
/// payload grants no authority and requires validation against current server context.
/// Scope and query hashes must come from CortexQueryFingerprintProducerV1, whose domain-
/// separated fixed arrays and byte-exact vectors define the recipe. The hashes bind equality
/// only; the opaque cursor still requires host-owned integrity protection and key rotation.
/// </summary>
public sealed record CortexCursorPayloadV1(
    int SchemaVersion,
    string ContractId,
    string CursorVersion,
    string TenantId,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string ScopeFingerprint,
    string? Namespace,
    string Operation,
    string QueryFingerprint,
    int PageSize,
    string SortVersion,
    string IndexGeneration,
    string KeysetPosition,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record CortexCursorValidationContext(
    string CursorVersion,
    string TenantId,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string ScopeFingerprint,
    string? Namespace,
    string Operation,
    string QueryFingerprint,
    int PageSize,
    string SortVersion,
    string IndexGeneration);

public sealed record CortexDiagnosticContextV1(
    string? RequestId,
    string? Namespace,
    string? RecordId,
    string? RevisionId,
    string? CorrelationId,
    string? IndexGeneration,
    string? Dependency,
    long? PolicyRevision);

/// <summary>
/// Privacy-safe wire diagnostic. CauseCategory is a controlled type/category, never a raw
/// exception message, query, cursor, evidence reference, token, path, or record content.
/// </summary>
public sealed record CortexDiagnosticV1
{
    [JsonConstructor]
    public CortexDiagnosticV1(
        string code,
        string message,
        string severity,
        string operation,
        bool retryable,
        CortexDiagnosticContextV1? context = null,
        string? causeCategory = null)
    {
        Code = CortexQuerySnapshots.RequireBoundedIdentifier(code, nameof(code));
        Message = CortexQuerySnapshots.RequireBoundedText(
            message,
            CortexQueryContractLimits.MaximumDiagnosticMessageCharacters,
            nameof(message));
        if (!string.Equals(Message, CortexQueryDiagnosticCatalog.MessageFor(Code), StringComparison.Ordinal))
            throw new ArgumentException("The wire diagnostic message must come from the controlled catalog.", nameof(message));
        Severity = severity is "information" or "warning" or "error" or "critical"
            ? severity
            : throw new ArgumentException("The diagnostic severity is unsupported.", nameof(severity));
        Operation = CortexQuerySnapshots.RequireBoundedIdentifier(operation, nameof(operation));
        Retryable = retryable;
        Context = CortexQuerySnapshots.ValidateDiagnosticContext(context);
        CauseCategory = causeCategory is null
            ? null
            : CortexQuerySnapshots.RequireBoundedIdentifier(causeCategory, nameof(causeCategory));
    }

    public string Code { get; }
    public string Message { get; }
    public string Severity { get; }
    public string Operation { get; }
    public bool Retryable { get; }
    public CortexDiagnosticContextV1? Context { get; }
    public string? CauseCategory { get; }
}

public sealed record CortexQueryResponseV1<TPayload>
    where TPayload : class
{
    [JsonConstructor]
    public CortexQueryResponseV1(
        int schemaVersion,
        string contractId,
        string operation,
        string requestId,
        bool accepted,
        TPayload? payload,
        IReadOnlyList<CortexDiagnosticV1> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Operation = operation;
        RequestId = requestId;
        Accepted = accepted;
        Payload = payload;
        Diagnostics = CortexQuerySnapshots.BoundedList(
            diagnostics,
            CortexQueryContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (accepted && payload is null || !accepted && payload is not null ||
            !accepted && Diagnostics.Count == 0 ||
            accepted && Diagnostics.Any(item => item.Severity is "error" or "critical") ||
            !accepted && Diagnostics.All(item => item.Severity is not ("error" or "critical")) ||
            accepted && !PayloadMatchesOperation(operation, payload))
        {
            throw new ArgumentException("Response acceptance, payload, and diagnostics are inconsistent.");
        }
        if (SchemaVersion != 1 || !string.Equals(ContractId, CortexQueryContractIds.Response, StringComparison.Ordinal) ||
            Operation is not (CortexQueryOperations.Discover or CortexQueryOperations.Health or
                CortexQueryOperations.ListNamespaces or CortexQueryOperations.Search or
                CortexQueryOperations.GetRecord or CortexQueryOperations.GetLineage) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(RequestId))
            throw new ArgumentException("The response header is invalid.");
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Operation { get; }
    public string RequestId { get; }
    public bool Accepted { get; }
    public TPayload? Payload { get; }
    public IReadOnlyList<CortexDiagnosticV1> Diagnostics { get; }

    private static bool PayloadMatchesOperation(string operation, TPayload? payload) => operation switch
    {
        CortexQueryOperations.Discover => payload is CortexQueryCapabilitiesV1,
        CortexQueryOperations.Health => payload is CortexServiceStatusV1,
        CortexQueryOperations.ListNamespaces => payload is CortexNamespacePageV1,
        CortexQueryOperations.Search => payload is CortexSearchPageV1,
        CortexQueryOperations.GetRecord => payload is CortexRecordReadResultV1,
        CortexQueryOperations.GetLineage => payload is CortexLineagePageV1,
        _ => false
    };
}

public sealed record CortexQueryBoundaryResult<T>
    where T : class
{
    public CortexQueryBoundaryResult(
        bool accepted,
        T? value,
        IEnumerable<PegasusDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Accepted = accepted;
        Value = value;
        Diagnostics = CortexQuerySnapshots.BoundedList(
            diagnostics,
            CortexQueryContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
    }

    public bool Accepted { get; }
    public T? Value { get; }
    public IReadOnlyList<PegasusDiagnostic> Diagnostics { get; }
}

internal static class CortexQuerySnapshots
{
    public static void ValidateRequestHeader(
        int schemaVersion,
        string? contractId,
        string expectedContractId,
        string? requestId)
    {
        if (schemaVersion != 1 ||
            !string.Equals(contractId, expectedContractId, StringComparison.Ordinal) ||
            !IsBoundedIdentifier(requestId))
            throw new ArgumentException("The request contract version or identifier is invalid.");
    }

    public static IReadOnlyList<T> BoundedList<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (maximumCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        var snapshot = new List<T>(Math.Min(maximumCount, 16));
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (snapshot.Count == maximumCount)
                throw new ArgumentOutOfRangeException(parameterName, $"The collection exceeds {maximumCount} items.");
            if (enumerator.Current is null)
                throw new ArgumentException("The collection contains a null item.", parameterName);
            snapshot.Add(enumerator.Current);
        }

        if (requireNonEmpty && snapshot.Count == 0)
            throw new ArgumentException("The collection must not be empty.", parameterName);
        return Array.AsReadOnly(snapshot.ToArray());
    }

    public static IReadOnlySet<string> BoundedSet(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty = false)
    {
        var values = BoundedList(source, maximumCount, parameterName, requireNonEmpty);
        var set = values.ToFrozenSet(StringComparer.Ordinal);
        if (set.Count != values.Count)
            throw new ArgumentException("The collection contains duplicate values.", parameterName);
        return set;
    }

    public static string RequireBoundedIdentifier(string value, string parameterName)
    {
        if (!IsBoundedIdentifier(value))
            throw new ArgumentException("A bounded identifier is required.", parameterName);
        return value;
    }

    public static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool IsBoundedContractId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-' or '/');

    public static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool ValidOptionalIdentifiers(params string?[] values) =>
        values.All(value => value is null || IsBoundedIdentifier(value));

    public static bool ValidBoundedText(string? value, int maximumCharacters, bool allowEmpty) =>
        value is not null && value.Length <= maximumCharacters &&
        (allowEmpty || !string.IsNullOrWhiteSpace(value)) &&
        value.All(character => character is '\r' or '\n' or '\t' || !char.IsControl(character));

    public static bool ValidNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryContractLimits.MaximumNamespaceCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool ValidRecordIdentity(
        string? recordId,
        string? revisionId,
        string? namespaceName,
        string? taxonomy) =>
        IsBoundedIdentifier(recordId) && IsBoundedIdentifier(revisionId) &&
        ValidNamespace(namespaceName) && IsBoundedIdentifier(taxonomy);

    public static bool ValidEvidence(CortexEvidenceProjectionV1? value) =>
        value is not null && IsBoundedIdentifier(value.EvidenceId) &&
        ValidBoundedText(value.Reference, 2048, allowEmpty: false) &&
        ValidBoundedText(value.MediaType, 128, allowEmpty: false) && IsHash(value.Sha256) &&
        IsBoundedIdentifier(value.Verification) && value.ObservedAt.Offset == TimeSpan.Zero &&
        (value.SourceVersion is null || IsBoundedIdentifier(value.SourceVersion));

    public static bool ValidApplicability(CortexApplicabilityProjectionV1? value) =>
        value is not null && IsBoundedIdentifier(value.ProductId) &&
        IsBoundedIdentifier(value.ComponentId) &&
        ValidOptionalVersion(value.MinimumVersion) && ValidOptionalVersion(value.MaximumVersion) &&
        value.State is ("verified" or "blocked" or "unverified" or "unknown" or "stale");

    public static bool ValidLineage(CortexLineageLinkV1? value) =>
        value is not null && value.Relation is ("derived_from" or "supersedes" or "superseded_by" or "references") &&
        ValidNamespace(value.Namespace) && IsBoundedIdentifier(value.RecordId) &&
        IsBoundedIdentifier(value.RevisionId) && IsHash(value.ContentHash);

    public static bool ValidConfidence(CortexConfidenceProjectionV1? value) =>
        value is not null && value.ValueBasisPoints is >= 0 and <= 10_000 &&
        ValidBoundedText(value.Basis, 512, allowEmpty: false);

    public static bool ValidFreshness(CortexFreshnessProjectionV1? value) =>
        value is not null && value.State is ("current" or "stale" or "blocked" or "unverified" or "unknown") &&
        value.ObservedAt.Offset == TimeSpan.Zero &&
        (value.ReviewAfter is null || value.ReviewAfter.Value.Offset == TimeSpan.Zero) &&
        (value.ReviewAfter is null || value.ReviewAfter.Value >= value.ObservedAt) &&
        ValidBoundedText(value.Basis, 512, allowEmpty: false);

    public static bool ValidDependencyStatus(CortexDependencyStatusV1? value) =>
        value is not null && value.Component is ("store" or "schema" or "index" or "gateway") &&
        value.Status is ("ready" or "degraded" or "unavailable" or "unknown") &&
        value.Ready == string.Equals(value.Status, "ready", StringComparison.Ordinal) &&
        (value.Revision is null || IsBoundedIdentifier(value.Revision)) &&
        (value.DiagnosticCode is null || IsBoundedIdentifier(value.DiagnosticCode)) &&
        value.ObservedAt.Offset == TimeSpan.Zero;

    public static string RequireBoundedText(string value, int maximumCharacters, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters ||
            value.Any(character => character is '\r' or '\n' || char.IsControl(character)))
        {
            throw new ArgumentException("Bounded single-line text is required.", parameterName);
        }
        return value;
    }

    public static CortexDiagnosticContextV1? ValidateDiagnosticContext(CortexDiagnosticContextV1? context)
    {
        if (context is null)
            return null;
        ValidateOptionalIdentifier(context.RequestId, nameof(context.RequestId));
        ValidateOptionalNamespace(context.Namespace, nameof(context.Namespace));
        ValidateOptionalIdentifier(context.RecordId, nameof(context.RecordId));
        ValidateOptionalIdentifier(context.RevisionId, nameof(context.RevisionId));
        ValidateOptionalIdentifier(context.CorrelationId, nameof(context.CorrelationId));
        ValidateOptionalIdentifier(context.IndexGeneration, nameof(context.IndexGeneration));
        ValidateOptionalIdentifier(context.Dependency, nameof(context.Dependency));
        if (context.PolicyRevision is <= 0)
            throw new ArgumentException("The diagnostic policy revision must be positive.", nameof(context));
        return context with { };
    }

    private static void ValidateOptionalIdentifier(string? value, string parameterName)
    {
        if (value is not null)
            RequireBoundedIdentifier(value, parameterName);
    }

    private static bool ValidOptionalVersion(string? value) =>
        value is null ||
        ValidBoundedText(value, CortexQueryContractLimits.MaximumFilterCharacters, allowEmpty: false) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(character => character is not ('\r' or '\n' or '\t'));

    private static void ValidateOptionalNamespace(string? value, string parameterName)
    {
        if (value is null)
            return;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexQueryContractLimits.MaximumNamespaceCharacters ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new ArgumentException("A bounded canonical namespace is required.", parameterName);
    }
}

/// <summary>
/// Pure validation for server-owned namespace access and already-unwrapped cursor state.
/// Token protection, key management, persistence, and transport remain host responsibilities.
/// </summary>
public static class CortexQueryContractBoundary
{
    private static readonly IReadOnlyDictionary<string, int> SensitivityRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["public"] = 0,
            ["internal"] = 1,
            ["confidential"] = 2,
            ["restricted"] = 3
        };

    private static readonly FrozenSet<string> Operations = new[]
    {
        CortexQueryOperations.ListNamespaces,
        CortexQueryOperations.Search,
        CortexQueryOperations.GetRecord,
        CortexQueryOperations.GetLineage
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FreshnessStates = new[]
    {
        "current", "stale", "blocked", "unverified", "unknown"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> CursorOperations = new[]
    {
        CortexQueryOperations.ListNamespaces,
        CortexQueryOperations.Search,
        CortexQueryOperations.GetLineage
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RecordStates = new[]
    {
        "active", "superseded", "archived"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static CortexQueryBoundaryResult<CortexQueryNamespaceAccess> AuthorizeNamespace(
        CortexQueryAuthorityContext caller,
        string namespaceName,
        string operation,
        string requestedMaximumSensitivity,
        IEnumerable<string> requestedFreshnessStates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requestedFreshnessStates);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ValidIdentifier(caller.TenantId) || !ValidIdentifier(caller.AuthorityId) ||
            !ValidIdentifier(caller.SubjectId) || !ValidIdentifier(caller.ServiceId) ||
            caller.PolicyRevision <= 0 || caller.NamespaceGrants.Count > CortexQueryContractLimits.MaximumNamespacesPerAuthority)
        {
            return Reject<CortexQueryNamespaceAccess>(
                "cortex_query_authority_invalid",
                "The server authority context is invalid or exceeds its bounded grant count.",
                operation,
                namespaceName);
        }

        if (!ValidNamespace(namespaceName) || !Operations.Contains(operation) ||
            !SensitivityRank.TryGetValue(requestedMaximumSensitivity, out var requestedSensitivity))
        {
            return Reject<CortexQueryNamespaceAccess>(
                "cortex_query_request_scope_invalid",
                "The requested namespace, operation, or sensitivity is invalid.",
                operation,
                namespaceName);
        }

        IReadOnlySet<string> requestedFreshness;
        try
        {
            requestedFreshness = CortexQuerySnapshots.BoundedSet(
                requestedFreshnessStates,
                5,
                nameof(requestedFreshnessStates),
                requireNonEmpty: true);
        }
        catch (ArgumentException)
        {
            return Reject<CortexQueryNamespaceAccess>(
                "cortex_query_freshness_invalid",
                "The requested freshness states are invalid or unbounded.",
                operation,
                namespaceName);
        }

        if (requestedFreshness.Any(state => !FreshnessStates.Contains(state)))
        {
            return Reject<CortexQueryNamespaceAccess>(
                "cortex_query_freshness_invalid",
                "The requested freshness states are invalid or unbounded.",
                operation,
                namespaceName);
        }

        CortexQueryNamespaceGrant? match = null;
        foreach (var grant in caller.NamespaceGrants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidGrant(grant))
            {
                return Reject<CortexQueryNamespaceAccess>(
                    "cortex_query_authority_invalid",
                    "The server authority context contains an invalid namespace grant.",
                    operation,
                    namespaceName);
            }

            if (!string.Equals(grant.Namespace, namespaceName, StringComparison.Ordinal))
                continue;
            if (match is not null)
            {
                return Reject<CortexQueryNamespaceAccess>(
                    "cortex_query_authority_ambiguous",
                    "The server authority context contains duplicate namespace grants.",
                    operation,
                    namespaceName);
            }
            match = grant;
        }

        if (match is null || !match.Operations.Contains(operation) ||
            !SensitivityRank.TryGetValue(match.MaximumSensitivity, out var grantedSensitivity) ||
            requestedSensitivity > grantedSensitivity ||
            requestedFreshness.Any(state => !match.FreshnessStates.Contains(state)))
        {
            return Reject<CortexQueryNamespaceAccess>(
                "cortex_query_namespace_forbidden",
                "Current server policy does not grant the requested namespace query scope.",
                operation,
                namespaceName);
        }

        var access = new CortexQueryNamespaceAccess(
            caller.TenantId,
            namespaceName,
            caller.AuthorityId,
            caller.SubjectId,
            caller.ServiceId,
            caller.PolicyRevision,
            operation,
            requestedMaximumSensitivity,
            requestedFreshness,
            match.RecordStates);
        return new CortexQueryBoundaryResult<CortexQueryNamespaceAccess>(true, access, []);
    }

    public static CortexQueryBoundaryResult<CortexCursorPayloadV1> ValidateCursorPayload(
        CortexCursorPayloadV1 payload,
        CortexCursorValidationContext expected,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        now = now.ToUniversalTime();

        var validShape = payload.SchemaVersion == 1 &&
            string.Equals(payload.ContractId, CortexQueryContractIds.CursorPayload, StringComparison.Ordinal) &&
            ValidIdentifier(payload.CursorVersion) &&
            ValidIdentifier(payload.TenantId) && ValidIdentifier(payload.AuthorityId) &&
            ValidIdentifier(payload.SubjectId) && ValidIdentifier(payload.ServiceId) &&
            payload.PolicyRevision > 0 && IsHash(payload.ScopeFingerprint) &&
            CursorOperations.Contains(payload.Operation) && ValidCursorNamespace(payload.Namespace, payload.Operation) &&
            IsHash(payload.QueryFingerprint) &&
            payload.PageSize >= 1 && payload.PageSize <= MaximumCursorPageSize(payload.Operation) &&
            ValidIdentifier(payload.SortVersion) && ValidIdentifier(payload.IndexGeneration) &&
            !string.IsNullOrWhiteSpace(payload.KeysetPosition) &&
            payload.KeysetPosition.Length <= CortexQueryContractLimits.MaximumCursorPositionCharacters &&
            payload.KeysetPosition.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character)) &&
            payload.IssuedAt.Offset == TimeSpan.Zero && payload.ExpiresAt.Offset == TimeSpan.Zero &&
            payload.ExpiresAt > payload.IssuedAt &&
            payload.ExpiresAt - payload.IssuedAt <= CortexQueryContractLimits.MaximumCursorLifetime;
        if (!validShape)
        {
            return Reject<CortexCursorPayloadV1>(
                "cortex_query_cursor_invalid",
                "The cursor is invalid for the current request.",
                expected.Operation,
                expected.Namespace,
                "cortex.query.cursor.validate");
        }

        if (payload.ExpiresAt <= now)
        {
            return Reject<CortexCursorPayloadV1>(
                "cortex_query_cursor_invalid",
                "The cursor is invalid for the current request.",
                expected.Operation,
                expected.Namespace,
                "cortex.query.cursor.validate");
        }

        if (payload.IssuedAt > now + CortexQueryContractLimits.MaximumClockSkew)
        {
            return Reject<CortexCursorPayloadV1>(
                "cortex_query_cursor_invalid",
                "The cursor is invalid for the current request.",
                expected.Operation,
                expected.Namespace,
                "cortex.query.cursor.validate");
        }

        var matches = string.Equals(payload.CursorVersion, expected.CursorVersion, StringComparison.Ordinal) &&
            string.Equals(payload.TenantId, expected.TenantId, StringComparison.Ordinal) &&
            string.Equals(payload.AuthorityId, expected.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(payload.SubjectId, expected.SubjectId, StringComparison.Ordinal) &&
            string.Equals(payload.ServiceId, expected.ServiceId, StringComparison.Ordinal) &&
            payload.PolicyRevision == expected.PolicyRevision &&
            string.Equals(payload.ScopeFingerprint, expected.ScopeFingerprint, StringComparison.Ordinal) &&
            string.Equals(payload.Namespace, expected.Namespace, StringComparison.Ordinal) &&
            string.Equals(payload.Operation, expected.Operation, StringComparison.Ordinal) &&
            string.Equals(payload.QueryFingerprint, expected.QueryFingerprint, StringComparison.Ordinal) &&
            payload.PageSize == expected.PageSize &&
            string.Equals(payload.SortVersion, expected.SortVersion, StringComparison.Ordinal) &&
            string.Equals(payload.IndexGeneration, expected.IndexGeneration, StringComparison.Ordinal);
        if (!matches)
        {
            return Reject<CortexCursorPayloadV1>(
                "cortex_query_cursor_invalid",
                "The cursor is invalid for the current request.",
                expected.Operation,
                expected.Namespace,
                "cortex.query.cursor.validate");
        }

        return new CortexQueryBoundaryResult<CortexCursorPayloadV1>(true, payload with { }, []);
    }

    public static bool IsOpaqueCursorBounded(string? cursor) =>
        cursor is null ||
        cursor.Length is > 0 and <= CortexQueryContractLimits.MaximumCursorCharacters &&
        cursor.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-');

    private static bool ValidGrant(CortexQueryNamespaceGrant? grant) =>
        grant is not null && ValidNamespace(grant.Namespace) &&
        SensitivityRank.ContainsKey(grant.MaximumSensitivity) &&
        grant.Operations.Count is > 0 and <= 4 && grant.Operations.All(Operations.Contains) &&
        grant.FreshnessStates.Count is > 0 and <= 5 && grant.FreshnessStates.All(FreshnessStates.Contains) &&
        grant.RecordStates.Count is > 0 and <= 3 && grant.RecordStates.All(RecordStates.Contains);

    private static bool ValidCursorNamespace(string? namespaceName, string operation) =>
        string.Equals(operation, CortexQueryOperations.ListNamespaces, StringComparison.Ordinal)
            ? namespaceName is null
            : ValidNamespace(namespaceName);

    private static int MaximumCursorPageSize(string operation) =>
        string.Equals(operation, CortexQueryOperations.Search, StringComparison.Ordinal)
            ? CortexQueryContractLimits.MaximumSearchPageSize
            : CursorOperations.Contains(operation)
                ? CortexQueryContractLimits.MaximumPageSize
                : 0;

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryContractLimits.MaximumNamespaceCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CortexQueryBoundaryResult<T> Reject<T>(
        string code,
        string message,
        string? operation,
        string? namespaceName,
        string diagnosticOperation = "cortex.query.authorize")
        where T : class =>
        new(
            false,
            null,
            [new PegasusDiagnostic(
                code,
                message,
                DiagnosticSeverity.Error,
                diagnosticOperation,
                retryable: false,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["operation"] = Operations.Contains(operation ?? string.Empty) ? operation : null,
                    ["namespace"] = ValidNamespace(namespaceName) ? namespaceName : null
                })]);
}
