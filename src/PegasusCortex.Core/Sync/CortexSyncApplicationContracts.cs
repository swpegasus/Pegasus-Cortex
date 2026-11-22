using System.Collections.Frozen;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Sync;

public static class CortexSyncApplicationLimits
{
    public const int MaximumAuthorityGrants = 256;
}

public sealed record CortexSyncNamespaceGrant(string Namespace, bool CanSync);

/// <summary>
/// Server-derived authority. Bundle hints are never used to construct this context.
/// </summary>
public sealed class CortexSyncAuthorityContext
{
    public CortexSyncAuthorityContext(
        string tenantId,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        IEnumerable<CortexSyncNamespaceGrant> grants,
        IEnumerable<string> allowedPayloadContractIds)
    {
        TenantId = tenantId;
        AuthorityId = authorityId;
        SubjectId = subjectId;
        ServiceId = serviceId;
        PolicyRevision = policyRevision;

        ArgumentNullException.ThrowIfNull(grants);
        var capturedGrants = new List<CortexSyncNamespaceGrant>(16);
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        using (var enumerator = grants.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                if (capturedGrants.Count == CortexSyncApplicationLimits.MaximumAuthorityGrants)
                    throw new ArgumentOutOfRangeException(nameof(grants), "Sync grants exceed the bounded authority contract.");
                var grant = enumerator.Current;
                if (grant is null || !namespaces.Add(grant.Namespace))
                    throw new ArgumentException("Sync grants cannot be null or duplicate a namespace.", nameof(grants));
                capturedGrants.Add(new CortexSyncNamespaceGrant(grant.Namespace, grant.CanSync));
            }
        }
        if (capturedGrants.Count == 0)
            throw new ArgumentException("At least one server-derived sync grant is required.", nameof(grants));

        Grants = Array.AsReadOnly(capturedGrants.ToArray());
        AllowedPayloadContractIds = CortexSyncApplicationValidation
            .SnapshotContractIds(
                allowedPayloadContractIds,
                CortexSyncContractLimits.MaximumContractIds,
                nameof(allowedPayloadContractIds))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    public string TenantId { get; }
    public string AuthorityId { get; }
    public string SubjectId { get; }
    public string ServiceId { get; }
    public long PolicyRevision { get; }
    public IReadOnlyList<CortexSyncNamespaceGrant> Grants { get; }
    public IReadOnlySet<string> AllowedPayloadContractIds { get; }
}

/// <summary>
/// TargetNamespace is an explicit caller request. Authorization is evaluated only from
/// ServerAuthority; tenant and namespace hints inside Bundle are consistency evidence.
/// </summary>
public sealed record SubmitCortexSyncBundleCommand(
    string TargetNamespace,
    CortexOfflineBundleV1 Bundle,
    CortexSyncAuthorityContext ServerAuthority);

public sealed record SubmitCortexLegacyMigrationPageCommand(
    string TargetNamespace,
    CortexLegacyMigrationPageV1 Page,
    CortexSyncAuthorityContext ServerAuthority);

public sealed record CortexSyncReceiptIssuance(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle,
    DateTimeOffset ReceivedAt,
    CortexSyncCommitSnapshot Commit,
    CortexSyncReceiptReconciliationV1 Reconciliation);

/// <summary>
/// Provider-neutral result from the optional retained-state materialization
/// boundary. A failure prevents a terminal receipt from being recovered or
/// issued; retryability is explicit and never inferred from exception text.
/// </summary>
public sealed record CortexSyncMaterializationResult(
    bool Succeeded,
    string DiagnosticCode,
    string Message,
    bool Retryable)
{
    public static CortexSyncMaterializationResult Success { get; } = new(
        true,
        "cortex_sync_materialization_verified",
        "The synchronization payload was materialized and read back exactly.",
        false);
}

public sealed record CortexSyncMaterializationPreflight(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle);

public sealed record CortexSyncMaterializationRequest(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle,
    CortexSyncCommitSnapshot Commit,
    CortexSyncReadBackSnapshot ReadBack);

/// <summary>
/// Optional durable projection hook used by the reconnect gateway. It is not a
/// task executor and is never exposed as an MCP tool. Preflight runs before the
/// append-only journal commit; materialization runs after exact journal
/// read-back and before any terminal receipt can escape.
/// </summary>
public interface ICortexSyncBundleMaterializer
{
    ValueTask<CortexSyncMaterializationResult> PreflightAsync(
        CortexSyncMaterializationPreflight request,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncMaterializationResult> MaterializeAndVerifyAsync(
        CortexSyncMaterializationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Host boundary for receipt identity and proof keys. Core always verifies the returned proof
/// and the exact repository evidence before accepting the issued receipt.
/// </summary>
public interface ICortexSyncReceiptIssuer
{
    ValueTask<CortexSyncReceiptV1> IssueAsync(
        CortexSyncReceiptIssuance issuance,
        CancellationToken cancellationToken);

    ValueTask<bool> VerifyProofAsync(
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken);
}

public sealed class CortexSyncApplicationResult
{
    public CortexSyncApplicationResult(
        bool receiptAccepted,
        bool durablyReconciled,
        CortexSyncCommitDisposition? disposition,
        CortexSyncReceiptV1? receipt,
        IEnumerable<PegasusDiagnostic> diagnostics)
    {
        ReceiptAccepted = receiptAccepted;
        DurablyReconciled = durablyReconciled;
        Disposition = disposition;
        Receipt = receipt;
        Diagnostics = Array.AsReadOnly(CortexSyncApplicationValidation.SnapshotList(
                diagnostics,
                CortexSyncContractLimits.MaximumCompatibilityIssues,
                nameof(diagnostics),
                requireNonEmpty: false)
            .Select(SnapshotDiagnostic)
            .ToArray());
    }

    public bool ReceiptAccepted { get; }
    public bool DurablyReconciled { get; }
    public CortexSyncCommitDisposition? Disposition { get; }
    public CortexSyncReceiptV1? Receipt { get; }
    public IReadOnlyList<PegasusDiagnostic> Diagnostics { get; }
    public bool Succeeded => ReceiptAccepted && DurablyReconciled;

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
}

public interface ICortexSyncApplicationService
{
    ValueTask<CortexSyncApplicationResult> SubmitAsync(
        SubmitCortexSyncBundleCommand command,
        CancellationToken cancellationToken);

    ValueTask<CortexSyncApplicationResult> SubmitLegacyMigrationPageAsync(
        SubmitCortexLegacyMigrationPageCommand command,
        CancellationToken cancellationToken);
}
