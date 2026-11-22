using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.Query;

/// <summary>
/// Common request header used only to select the fixed v1 fingerprint recipe.
/// RequestId remains trace metadata and is intentionally excluded from the hash.
/// </summary>
public interface ICortexQueryRequestV1
{
    int SchemaVersion { get; }
    string ContractId { get; }
    string RequestId { get; }
}

/// <summary>
/// Immutable result of one canonical fingerprint recipe. Canonical bytes are exported
/// only for conformance testing and may contain identifiers or query text; do not log them.
/// </summary>
public sealed class CortexQueryFingerprintValueV1
{
    private readonly byte[] _canonicalUtf8;

    internal CortexQueryFingerprintValueV1(byte[] canonicalUtf8)
    {
        ArgumentNullException.ThrowIfNull(canonicalUtf8);
        _canonicalUtf8 = canonicalUtf8.ToArray();
        Sha256 = CortexCanonicalJson.Sha256(_canonicalUtf8);
    }

    public string Sha256 { get; }
    public int CanonicalByteCount => _canonicalUtf8.Length;

    public byte[] ExportCanonicalUtf8ForConformance() => _canonicalUtf8.ToArray();
}

/// <summary>
/// Produces versioned, domain-separated equality bindings for query scope and request
/// semantics. These plain SHA-256 values provide no integrity, authenticity, secrecy,
/// authorization, authentication, or cursor protection. A host must separately authorize
/// current server-derived scope and protect cursor payloads with purpose-separated AEAD,
/// MAC, or signatures plus key version, expiry, index, sort, and page-size validation.
/// Query fingerprints are not safe cross-tenant cache keys without tenant/scope binding.
/// </summary>
public static class CortexQueryFingerprintProducerV1
{
    public const string ScopeDomain = "pegasus.cortex.query.scope";
    public const string RequestDomain = "pegasus.cortex.query.request";
    public const int RecipeVersion = 1;

    private static readonly FrozenSet<string> NamespaceScopeOperations = new[]
    {
        CortexQueryOperations.Search,
        CortexQueryOperations.GetRecord,
        CortexQueryOperations.GetLineage
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GrantOperations = new[]
    {
        CortexQueryOperations.ListNamespaces,
        CortexQueryOperations.Search,
        CortexQueryOperations.GetRecord,
        CortexQueryOperations.GetLineage
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Sensitivities = new[]
    {
        "public", "internal", "confidential", "restricted"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FreshnessStates = new[]
    {
        "current", "stale", "blocked", "unverified", "unknown"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RecordStates = new[]
    {
        "active", "superseded", "archived"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Binds one already-authorized namespace scope. Namespace listing is deliberately
    /// rejected here because one namespace grant cannot represent its authority-wide view.
    /// </summary>
    public static CortexQueryFingerprintValueV1 CreateNamespaceScope(
        CortexQueryNamespaceAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        ValidatePrincipal(
            access.TenantId,
            access.AuthorityId,
            access.SubjectId,
            access.ServiceId,
            access.PolicyRevision,
            nameof(access));
        if (!CortexQuerySnapshots.ValidNamespace(access.Namespace) ||
            !NamespaceScopeOperations.Contains(access.Operation) ||
            !Sensitivities.Contains(access.MaximumSensitivity))
        {
            throw new ArgumentException("The namespace access is invalid or is not namespace scoped.", nameof(access));
        }

        var freshness = SnapshotSortedSet(
            access.FreshnessStates,
            5,
            FreshnessStates,
            nameof(access.FreshnessStates));
        var recordStates = SnapshotSortedSet(
            access.RecordStates,
            3,
            RecordStates,
            nameof(access.RecordStates));

        return Create(writer =>
        {
            writer.WriteStartArray();
            WriteRequiredString(writer, ScopeDomain, nameof(ScopeDomain));
            writer.WriteNumberValue(RecipeVersion);
            WriteRequiredString(writer, "namespace", "scopeKind");
            WriteRequiredString(writer, access.TenantId, nameof(access.TenantId));
            WriteRequiredString(writer, access.AuthorityId, nameof(access.AuthorityId));
            WriteRequiredString(writer, access.SubjectId, nameof(access.SubjectId));
            WriteRequiredString(writer, access.ServiceId, nameof(access.ServiceId));
            writer.WriteNumberValue(access.PolicyRevision);
            WriteRequiredString(writer, access.Namespace, nameof(access.Namespace));
            WriteRequiredString(writer, access.Operation, nameof(access.Operation));
            WriteRequiredString(writer, access.MaximumSensitivity, nameof(access.MaximumSensitivity));
            WriteStringArray(writer, freshness);
            WriteStringArray(writer, recordStates);
            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// Binds the complete visible namespace-list grant view for one server-derived authority.
    /// A grant is visible only when it explicitly contains list_namespaces. Every grant is
    /// validated and duplicate namespaces fail closed; non-visible grants are not hashed.
    /// This fingerprint does not itself authorize listing.
    /// </summary>
    public static CortexQueryFingerprintValueV1 CreateNamespaceListScope(
        CortexQueryAuthorityContext authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidatePrincipal(
            authority.TenantId,
            authority.AuthorityId,
            authority.SubjectId,
            authority.ServiceId,
            authority.PolicyRevision,
            nameof(authority));

        var grants = CortexQuerySnapshots.BoundedList(
            authority.NamespaceGrants,
            CortexQueryContractLimits.MaximumNamespacesPerAuthority,
            nameof(authority.NamespaceGrants));
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var visible = new List<NamespaceGrantView>(grants.Count);
        foreach (var grant in grants)
        {
            if (!CortexQuerySnapshots.ValidNamespace(grant.Namespace) ||
                !Sensitivities.Contains(grant.MaximumSensitivity) ||
                !seenNamespaces.Add(grant.Namespace))
            {
                throw new ArgumentException("The authority contains an invalid or duplicate namespace grant.", nameof(authority));
            }

            var operations = SnapshotSortedSet(grant.Operations, 4, GrantOperations, nameof(grant.Operations));
            var freshness = SnapshotSortedSet(grant.FreshnessStates, 5, FreshnessStates, nameof(grant.FreshnessStates));
            var recordStates = SnapshotSortedSet(grant.RecordStates, 3, RecordStates, nameof(grant.RecordStates));
            if (operations.Contains(CortexQueryOperations.ListNamespaces, StringComparer.Ordinal))
            {
                visible.Add(new NamespaceGrantView(
                    grant.Namespace,
                    grant.MaximumSensitivity,
                    operations,
                    freshness,
                    recordStates));
            }
        }

        if (visible.Count == 0)
            throw new ArgumentException("The authority has no explicit namespace-list grant.", nameof(authority));
        var sortedVisible = visible.OrderBy(item => item.Namespace, StringComparer.Ordinal).ToArray();

        return Create(writer =>
        {
            writer.WriteStartArray();
            WriteRequiredString(writer, ScopeDomain, nameof(ScopeDomain));
            writer.WriteNumberValue(RecipeVersion);
            WriteRequiredString(writer, "authority", "scopeKind");
            WriteRequiredString(writer, authority.TenantId, nameof(authority.TenantId));
            WriteRequiredString(writer, authority.AuthorityId, nameof(authority.AuthorityId));
            WriteRequiredString(writer, authority.SubjectId, nameof(authority.SubjectId));
            WriteRequiredString(writer, authority.ServiceId, nameof(authority.ServiceId));
            writer.WriteNumberValue(authority.PolicyRevision);
            WriteRequiredString(writer, CortexQueryOperations.ListNamespaces, "operation");
            writer.WriteStartArray();
            foreach (var grant in sortedVisible)
            {
                writer.WriteStartArray();
                WriteRequiredString(writer, grant.Namespace, nameof(grant.Namespace));
                WriteRequiredString(writer, grant.MaximumSensitivity, nameof(grant.MaximumSensitivity));
                WriteStringArray(writer, grant.Operations);
                WriteStringArray(writer, grant.FreshnessStates);
                WriteStringArray(writer, grant.RecordStates);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// Binds semantic request fields using one fixed-length recipe. RequestId is trace-only,
    /// cursor would be self-referential, and page size is bound separately by cursor state;
    /// those three fields are intentionally excluded. All other unused slots are JSON null.
    /// </summary>
    public static CortexQueryFingerprintValueV1 CreateRequest(ICortexQueryRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = ValidateAndProjectRequest(request);
        return Create(writer =>
        {
            writer.WriteStartArray();
            WriteRequiredString(writer, RequestDomain, nameof(RequestDomain));
            writer.WriteNumberValue(RecipeVersion);
            writer.WriteNumberValue(view.SchemaVersion);
            WriteRequiredString(writer, view.ContractId, nameof(view.ContractId));
            WriteRequiredString(writer, view.Operation, nameof(view.Operation));
            WriteOptionalString(writer, view.Namespace, nameof(view.Namespace));
            WriteOptionalString(writer, view.Text, nameof(view.Text));
            WriteOptionalString(writer, view.Taxonomy, nameof(view.Taxonomy));
            WriteOptionalString(writer, view.ProductId, nameof(view.ProductId));
            WriteOptionalString(writer, view.ComponentId, nameof(view.ComponentId));
            WriteOptionalString(writer, view.MaximumSensitivity, nameof(view.MaximumSensitivity));
            WriteOptionalStringArray(writer, view.FreshnessStates);
            WriteOptionalString(writer, view.RecordId, nameof(view.RecordId));
            WriteOptionalString(writer, view.RevisionId, nameof(view.RevisionId));
            WriteOptionalString(writer, view.Direction, nameof(view.Direction));
            writer.WriteEndArray();
        });
    }

    private static RequestView ValidateAndProjectRequest(ICortexQueryRequestV1 request)
    {
        switch (request)
        {
            case CortexDiscoveryRequestV1 discovery:
                ValidateHeader(discovery, CortexQueryContractIds.DiscoveryRequest);
                return RequestView.Empty(discovery, CortexQueryOperations.Discover);

            case CortexHealthRequestV1 health:
                ValidateHeader(health, CortexQueryContractIds.HealthRequest);
                return RequestView.Empty(health, CortexQueryOperations.Health);

            case CortexNamespaceListRequestV1 list:
                ValidateHeader(list, CortexQueryContractIds.NamespaceListRequest);
                if (list.PageSize is < 1 or > CortexQueryContractLimits.MaximumPageSize ||
                    !CortexQueryContractBoundary.IsOpaqueCursorBounded(list.Cursor))
                    throw new ArgumentException("The namespace-list request is invalid or unbounded.", nameof(request));
                return RequestView.Empty(list, CortexQueryOperations.ListNamespaces);

            case CortexSearchRequestV1 search:
                ValidateHeader(search, CortexQueryContractIds.SearchRequest);
                var freshness = SnapshotSortedSet(
                    search.FreshnessStates,
                    5,
                    FreshnessStates,
                    nameof(search.FreshnessStates));
                if (!CortexQuerySnapshots.ValidNamespace(search.Namespace) ||
                    !CortexQuerySnapshots.ValidBoundedText(
                        search.Text,
                        CortexQueryContractLimits.MaximumQueryCharacters,
                        allowEmpty: false) ||
                    !CortexQuerySnapshots.ValidOptionalIdentifiers(search.Taxonomy, search.ProductId, search.ComponentId) ||
                    !Sensitivities.Contains(search.MaximumSensitivity) ||
                    search.PageSize is < 1 or > CortexQueryContractLimits.MaximumSearchPageSize ||
                    !CortexQueryContractBoundary.IsOpaqueCursorBounded(search.Cursor))
                {
                    throw new ArgumentException("The search request is invalid or unbounded.", nameof(request));
                }
                return new RequestView(
                    search.SchemaVersion,
                    search.ContractId,
                    CortexQueryOperations.Search,
                    search.Namespace,
                    search.Text,
                    search.Taxonomy,
                    search.ProductId,
                    search.ComponentId,
                    search.MaximumSensitivity,
                    freshness,
                    null,
                    null,
                    null);

            case CortexSearchRequestV1_1 search:
                ValidateHeader(search, CortexQueryContractIds.SearchRequestV1_1);
                var versionedFreshness = SnapshotSortedSet(
                    search.FreshnessStates,
                    5,
                    FreshnessStates,
                    nameof(search.FreshnessStates));
                if (!CortexQuerySnapshots.ValidNamespace(search.Namespace) ||
                    !CortexQuerySnapshots.ValidBoundedText(
                        search.Text,
                        CortexQueryContractLimits.MaximumQueryCharacters,
                        allowEmpty: false) ||
                    !CortexQuerySnapshots.ValidOptionalIdentifiers(search.Taxonomy, search.ProductId, search.ComponentId) ||
                    !Sensitivities.Contains(search.MaximumSensitivity) ||
                    !CortexSearchModes.IsSupported(search.Mode) ||
                    search.PageSize is < 1 or > CortexQueryContractLimits.MaximumSearchPageSize ||
                    !CortexQueryContractBoundary.IsOpaqueCursorBounded(search.Cursor))
                {
                    throw new ArgumentException("The search request is invalid or unbounded.", nameof(request));
                }
                // The v1 fixed-length request recipe already reserves its final discriminator
                // slot for operation-specific semantics. Search v1.1 binds mode there, while
                // the v1.0 exact-request bytes remain unchanged.
                return new RequestView(
                    search.SchemaVersion,
                    search.ContractId,
                    CortexQueryOperations.Search,
                    search.Namespace,
                    search.Text,
                    search.Taxonomy,
                    search.ProductId,
                    search.ComponentId,
                    search.MaximumSensitivity,
                    versionedFreshness,
                    null,
                    null,
                    search.Mode);

            case CortexRecordReadRequestV1 read:
                ValidateHeader(read, CortexQueryContractIds.RecordReadRequest);
                ValidateRecordIdentity(read.Namespace, read.RecordId, read.RevisionId, nameof(request));
                return new RequestView(
                    read.SchemaVersion,
                    read.ContractId,
                    CortexQueryOperations.GetRecord,
                    read.Namespace,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    read.RecordId,
                    read.RevisionId,
                    null);

            case CortexLineageRequestV1 lineage:
                ValidateHeader(lineage, CortexQueryContractIds.LineageRequest);
                ValidateRecordIdentity(lineage.Namespace, lineage.RecordId, lineage.RevisionId, nameof(request));
                if (lineage.Direction is not ("ancestors" or "descendants") ||
                    lineage.PageSize is < 1 or > CortexQueryContractLimits.MaximumPageSize ||
                    !CortexQueryContractBoundary.IsOpaqueCursorBounded(lineage.Cursor))
                {
                    throw new ArgumentException("The lineage request is invalid or unbounded.", nameof(request));
                }
                return new RequestView(
                    lineage.SchemaVersion,
                    lineage.ContractId,
                    CortexQueryOperations.GetLineage,
                    lineage.Namespace,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    lineage.RecordId,
                    lineage.RevisionId,
                    lineage.Direction);

            default:
                throw new ArgumentException("The query request type is unsupported by recipe v1.", nameof(request));
        }
    }

    private static void ValidateHeader(ICortexQueryRequestV1 request, string expectedContractId)
    {
        CortexQuerySnapshots.ValidateRequestHeader(
            request.SchemaVersion,
            request.ContractId,
            expectedContractId,
            request.RequestId);
    }

    private static void ValidateRecordIdentity(
        string namespaceName,
        string recordId,
        string revisionId,
        string parameterName)
    {
        if (!CortexQuerySnapshots.ValidNamespace(namespaceName) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(recordId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(revisionId))
        {
            throw new ArgumentException("The record identity is invalid or unbounded.", parameterName);
        }
    }

    private static void ValidatePrincipal(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        string parameterName)
    {
        if (!CortexQuerySnapshots.IsBoundedIdentifier(tenantId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(authorityId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(subjectId) ||
            !CortexQuerySnapshots.IsBoundedIdentifier(serviceId) ||
            policyRevision <= 0)
        {
            throw new ArgumentException("The server-derived principal scope is invalid.", parameterName);
        }
    }

    private static IReadOnlyList<string> SnapshotSortedSet(
        IEnumerable<string> source,
        int maximumCount,
        IReadOnlySet<string> allowed,
        string parameterName)
    {
        var snapshot = CortexQuerySnapshots.BoundedList(
            source,
            maximumCount,
            parameterName,
            requireNonEmpty: true);
        if (snapshot.Distinct(StringComparer.Ordinal).Count() != snapshot.Count ||
            snapshot.Any(value => !allowed.Contains(value)))
        {
            throw new ArgumentException("The set contains a duplicate or unsupported value.", parameterName);
        }
        foreach (var value in snapshot)
            RequireWellFormedUnicode(value, parameterName);
        return Array.AsReadOnly(snapshot.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static CortexQueryFingerprintValueV1 Create(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            write(writer);
        }
        return new CortexQueryFingerprintValueV1(output.ToArray());
    }

    private static void WriteRequiredString(Utf8JsonWriter writer, string value, string parameterName)
    {
        RequireWellFormedUnicode(value, parameterName);
        // Utf8JsonWriter escapes supplementary Unicode scalars as UTF-16 surrogate pairs,
        // even with its relaxed encoder. Write a validated JSON string value directly so
        // every implementation can follow the v1 rule: escape only JSON-required characters
        // and encode all other Unicode scalars as their original UTF-8 bytes.
        writer.WriteRawValue(CreateCanonicalJsonString(value), skipInputValidation: false);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string? value, string parameterName)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        WriteRequiredString(writer, value, parameterName);
    }

    private static void WriteStringArray(Utf8JsonWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
            WriteRequiredString(writer, value, nameof(values));
        writer.WriteEndArray();
    }

    private static void WriteOptionalStringArray(Utf8JsonWriter writer, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }
        WriteStringArray(writer, values);
    }

    private static void RequireWellFormedUnicode(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("The value contains ill-formed UTF-16.", parameterName);
                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ArgumentException("The value contains ill-formed UTF-16.", parameterName);
            }
        }
    }

    private static string CreateCanonicalJsonString(string value)
    {
        StringBuilder? escaped = null;
        var segmentStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var replacement = value[index] switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                var control when control < ' ' => $"\\u{(int)control:x4}",
                _ => null
            };
            if (replacement is null)
                continue;

            escaped ??= new StringBuilder(value.Length + 8).Append('"');
            escaped.Append(value, segmentStart, index - segmentStart);
            escaped.Append(replacement);
            segmentStart = index + 1;
        }

        if (escaped is null)
            return string.Concat("\"", value, "\"");

        escaped.Append(value, segmentStart, value.Length - segmentStart);
        return escaped.Append('"').ToString();
    }

    private sealed record NamespaceGrantView(
        string Namespace,
        string MaximumSensitivity,
        IReadOnlyList<string> Operations,
        IReadOnlyList<string> FreshnessStates,
        IReadOnlyList<string> RecordStates);

    private sealed record RequestView(
        int SchemaVersion,
        string ContractId,
        string Operation,
        string? Namespace,
        string? Text,
        string? Taxonomy,
        string? ProductId,
        string? ComponentId,
        string? MaximumSensitivity,
        IReadOnlyList<string>? FreshnessStates,
        string? RecordId,
        string? RevisionId,
        string? Direction)
    {
        public static RequestView Empty(ICortexQueryRequestV1 request, string operation) =>
            new(
                request.SchemaVersion,
                request.ContractId,
                operation,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
    }
}
