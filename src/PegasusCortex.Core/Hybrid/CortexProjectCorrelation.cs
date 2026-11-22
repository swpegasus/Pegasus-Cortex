using Pegasus.Cortex.Contracts.Serialization;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Core.Hybrid;

public static class CortexProjectCorrelationContract
{
    public const int LegacySchemaVersion = 1;
    public const int SchemaVersion = 2;
    public const string LegacyContractId = "pegasus.cortex.project-link/1.0";
    public const string ContractId = "pegasus.cortex.project-link/1.1";
    public const string ControlNamespace = "project-links";
    public const string CollectiveLibraryAccess = "collective";
    public const string ReservedLibraryAccess = "reserved";
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumCollectiveLibraries = 64;
    public const int MaximumProjectLinkPageSize = MaximumCollectiveLibraries + 1;
}

public enum CortexProjectLinkCapability
{
    Read,
    Correlate
}

/// <summary>
/// Server-derived access for the fixed project-link control namespace. Project metadata
/// supplied by a caller never grants this capability.
/// </summary>
public sealed record CortexProjectLinkAccess(
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    CortexProjectLinkCapability Capability);

/// <summary>
/// Safe public correlation metadata. It deliberately excludes a project path, bearer token,
/// caller authority, and machine credential so it can be copied into project-local metadata.
/// </summary>
public sealed record CortexProjectCorrelationMetadataV1(
    int SchemaVersion,
    string ContractId,
    string ProjectId,
    string StoreId,
    long StoreEpoch,
    string ProjectLinkId,
    string ProjectNamespace,
    string EndpointId,
    string ContractVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LibraryAccess = null);

public sealed record CortexPreparedProjectLink(
    CortexProjectCorrelationMetadataV1 Metadata,
    string CorrelationHash);

public sealed record CortexProjectLinkV1(
    CortexProjectCorrelationMetadataV1 Metadata,
    string CorrelationHash);

public enum CortexProjectLinkWriteDisposition
{
    Added,
    Existing,
    Conflict
}

public sealed record CortexProjectLinkWriteResult(
    CortexProjectLinkWriteDisposition Disposition,
    CortexProjectLinkV1 Link);

public interface ICortexProjectLinkRepository
{
    ValueTask<CortexProjectLinkV1?> FindAsync(
        CortexProjectLinkAccess access,
        string projectLinkId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CortexProjectLinkV1>> ListAsync(
        CortexProjectLinkAccess access,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<CortexProjectLinkWriteResult> AddAsync(
        CortexProjectLinkAccess access,
        CortexProjectLinkV1 link,
        CancellationToken cancellationToken);
}

/// <summary>
/// Pure deterministic policy for preparing and verifying server-issued project links.
/// Persistence and operating-system authority remain host-owned concerns.
/// </summary>
public static class CortexProjectCorrelationPolicy
{
    public static CortexPreparedProjectLink Prepare(
        CortexProjectLinkAccess access,
        string projectId,
        string storeId,
        long storeEpoch,
        string endpointId,
        string contractVersion,
        string libraryAccess = CortexProjectCorrelationContract.CollectiveLibraryAccess)
    {
        ValidateAccess(access, CortexProjectLinkCapability.Correlate);
        ValidateIdentifier(projectId, nameof(projectId));
        ValidateIdentifier(storeId, nameof(storeId));
        ValidateIdentifier(endpointId, nameof(endpointId));
        ValidateIdentifier(contractVersion, nameof(contractVersion));
        ValidateLibraryAccess(libraryAccess, nameof(libraryAccess));
        if (storeEpoch < 1)
            throw new ArgumentOutOfRangeException(nameof(storeEpoch));

        var identity = new
        {
            contract = CortexProjectCorrelationContract.ContractId,
            tenantId = access.TenantId,
            projectId,
            storeId,
            storeEpoch,
            endpointId,
            contractVersion,
            libraryAccess
        };
        var identityHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(identity));
        var metadata = new CortexProjectCorrelationMetadataV1(
            CortexProjectCorrelationContract.SchemaVersion,
            CortexProjectCorrelationContract.ContractId,
            projectId,
            storeId,
            storeEpoch,
            $"plink_{identityHash}",
            $"project-{identityHash[..48]}",
            endpointId,
            contractVersion,
            libraryAccess);
        var correlationHash = ComputeCorrelationHash(metadata);
        return new CortexPreparedProjectLink(metadata, correlationHash);
    }

    public static CortexProjectLinkV1 Confirm(
        CortexProjectLinkAccess access,
        CortexPreparedProjectLink prepared,
        string submittedCorrelationHash)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ValidateAccess(access, CortexProjectLinkCapability.Correlate);
        if (!ValidSha256(submittedCorrelationHash) ||
            !string.Equals(prepared.CorrelationHash, submittedCorrelationHash, StringComparison.Ordinal) ||
            !string.Equals(
                ComputeCorrelationHash(prepared.Metadata),
                submittedCorrelationHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The submitted project correlation hash does not match the prepared metadata.",
                nameof(submittedCorrelationHash));
        }

        return new CortexProjectLinkV1(prepared.Metadata with { }, submittedCorrelationHash);
    }

    public static void ValidateStoredLink(
        CortexProjectLinkAccess access,
        CortexProjectLinkV1 link,
        string? expectedProjectLinkId = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(link.Metadata);
        ValidateAccess(access, access.Capability);
        var metadata = link.Metadata;
        ValidateIdentifier(metadata.ProjectId, nameof(link));
        ValidateIdentifier(metadata.StoreId, nameof(link));
        ValidateIdentifier(metadata.ProjectLinkId, nameof(link));
        ValidateIdentifier(metadata.ProjectNamespace, nameof(link));
        ValidateIdentifier(metadata.EndpointId, nameof(link));
        ValidateIdentifier(metadata.ContractVersion, nameof(link));
        var legacy = metadata.SchemaVersion == CortexProjectCorrelationContract.LegacySchemaVersion &&
                     string.Equals(
                         metadata.ContractId,
                         CortexProjectCorrelationContract.LegacyContractId,
                         StringComparison.Ordinal);
        var current = metadata.SchemaVersion == CortexProjectCorrelationContract.SchemaVersion &&
                      string.Equals(
                          metadata.ContractId,
                          CortexProjectCorrelationContract.ContractId,
                          StringComparison.Ordinal);
        if (!legacy && !current ||
            metadata.StoreEpoch < 1 ||
            legacy && metadata.LibraryAccess is not null ||
            current && !ValidLibraryAccess(metadata.LibraryAccess) ||
            !ValidSha256(link.CorrelationHash) ||
            !string.Equals(
                ComputeCorrelationHash(metadata),
                link.CorrelationHash,
                StringComparison.Ordinal) ||
            expectedProjectLinkId is not null &&
            !string.Equals(metadata.ProjectLinkId, expectedProjectLinkId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The stored project link is outside its bounded contract.", nameof(link));
        }
    }

    public static string EffectiveLibraryAccess(CortexProjectCorrelationMetadataV1 metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.SchemaVersion == CortexProjectCorrelationContract.LegacySchemaVersion &&
               string.Equals(
                   metadata.ContractId,
                   CortexProjectCorrelationContract.LegacyContractId,
                   StringComparison.Ordinal)
            ? CortexProjectCorrelationContract.CollectiveLibraryAccess
            : ValidLibraryAccess(metadata.LibraryAccess)
                ? metadata.LibraryAccess!
                : throw new ArgumentException(
                    "The project library-access mode is outside its bounded contract.",
                    nameof(metadata));
    }

    private static string ComputeCorrelationHash(CortexProjectCorrelationMetadataV1 metadata)
    {
        if (metadata.SchemaVersion == CortexProjectCorrelationContract.LegacySchemaVersion &&
            string.Equals(
                metadata.ContractId,
                CortexProjectCorrelationContract.LegacyContractId,
                StringComparison.Ordinal))
        {
            var legacy = new LegacyCorrelationMetadata(
                metadata.SchemaVersion,
                metadata.ContractId,
                metadata.ProjectId,
                metadata.StoreId,
                metadata.StoreEpoch,
                metadata.ProjectLinkId,
                metadata.ProjectNamespace,
                metadata.EndpointId,
                metadata.ContractVersion);
            return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(legacy));
        }

        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(metadata));
    }

    private static void ValidateAccess(
        CortexProjectLinkAccess access,
        CortexProjectLinkCapability expectedCapability)
    {
        ArgumentNullException.ThrowIfNull(access);
        ValidateIdentifier(access.TenantId, nameof(access));
        ValidateIdentifier(access.AuthorityId, nameof(access));
        ValidateIdentifier(access.SubjectId, nameof(access));
        ValidateIdentifier(access.ServiceId, nameof(access));
        if (access.PolicyRevision < 1 ||
            access.Capability != expectedCapability ||
            !string.Equals(
                access.Namespace,
                CortexProjectCorrelationContract.ControlNamespace,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The project-link authority is invalid.", nameof(access));
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexProjectCorrelationContract.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("The value is not a bounded Cortex identifier.", parameterName);
        }
    }

    private static void ValidateLibraryAccess(string value, string parameterName)
    {
        if (!ValidLibraryAccess(value))
            throw new ArgumentException("The project library-access mode is invalid.", parameterName);
    }

    private static bool ValidLibraryAccess(string? value) =>
        value is CortexProjectCorrelationContract.CollectiveLibraryAccess or
            CortexProjectCorrelationContract.ReservedLibraryAccess;

    private static bool ValidSha256(string value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record LegacyCorrelationMetadata(
        int SchemaVersion,
        string ContractId,
        string ProjectId,
        string StoreId,
        long StoreEpoch,
        string ProjectLinkId,
        string ProjectNamespace,
        string EndpointId,
        string ContractVersion);
}
