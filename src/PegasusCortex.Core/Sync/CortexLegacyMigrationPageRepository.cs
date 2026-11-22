using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.Core.Sync;

public enum CortexLegacyMigrationPageCommitDisposition
{
    Accepted,
    Rejected
}

public static class CortexLegacyMigrationPageCommitDiagnosticCodes
{
    /// <summary>
    /// The bundle attempt already exists with different or absent migration-page evidence.
    /// Retrying cannot change immutable evidence, so callers must treat this as permanent.
    /// </summary>
    public const string ExactAttemptConflict = "cortex_sync_migration_page_conflict";
}

/// <summary>
/// Exact page evidence passed to the persistence transaction. The canonical page is retained so
/// a repository can prove an idempotent retry and cannot silently reduce the request to a bundle.
/// </summary>
public sealed class CortexLegacyMigrationPageCommitRequest
{
    private readonly byte[] _canonicalPage;

    public CortexLegacyMigrationPageCommitRequest(
        CortexSyncRepositoryScope scope,
        CortexLegacyMigrationPageV1 page,
        DateTimeOffset receivedAt)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        ReceivedAt = receivedAt;
        if (!CortexSyncContractPolicy.ValidateLegacyMigrationPage(page).Accepted)
            throw new ArgumentException("The migration page failed its canonical contract.", nameof(page));
        if (!string.Equals(scope.StoreId, page.Bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(scope.StoreEpoch, page.Bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(scope.ProjectId, page.Bundle.ProjectId, StringComparison.Ordinal) ||
            receivedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The migration page does not match its server repository scope.");
        }

        _canonicalPage = CortexCanonicalJson.Serialize(page);
        if (_canonicalPage.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentOutOfRangeException(nameof(page), "The migration page exceeds its bounded canonical persistence contract.");
        CanonicalSha256 = CortexCanonicalJson.Sha256(_canonicalPage);
    }

    public CortexSyncRepositoryScope Scope { get; }
    public CortexLegacyMigrationPageV1 Page { get; }
    public DateTimeOffset ReceivedAt { get; }
    public ReadOnlyMemory<byte> CanonicalPage => _canonicalPage;
    public string CanonicalSha256 { get; }
}

/// <summary>
/// A rejection means this call wrote no bundle attempt. Accepted carries the normal immutable
/// commit evidence and proceeds through the existing receipt/read-back path.
/// </summary>
public sealed class CortexLegacyMigrationPageCommitSnapshot
{
    public CortexLegacyMigrationPageCommitSnapshot(
        CortexLegacyMigrationPageCommitDisposition disposition,
        CortexSyncCommitSnapshot? commit,
        string? diagnosticCode,
        bool replayedExistingAttempt = false)
    {
        if (!Enum.IsDefined(disposition) ||
            (disposition == CortexLegacyMigrationPageCommitDisposition.Accepted) != (commit is not null) ||
            (disposition == CortexLegacyMigrationPageCommitDisposition.Accepted) != (diagnosticCode is null) ||
            (disposition == CortexLegacyMigrationPageCommitDisposition.Rejected && replayedExistingAttempt) ||
            (diagnosticCode is not null && !CortexSyncApplicationValidation.IsIdentifier(diagnosticCode)))
        {
            throw new ArgumentException("Migration page commit evidence is incoherent.");
        }

        Disposition = disposition;
        Commit = commit;
        DiagnosticCode = diagnosticCode;
        ReplayedExistingAttempt = replayedExistingAttempt;
    }

    public CortexLegacyMigrationPageCommitDisposition Disposition { get; }
    public CortexSyncCommitSnapshot? Commit { get; }
    public string? DiagnosticCode { get; }

    /// <summary>
    /// True only when the repository found and proved this exact canonical page on an immutable
    /// existing bundle attempt. Core may then reuse its stored timestamps after wall-clock drift.
    /// </summary>
    public bool ReplayedExistingAttempt { get; }
}

/// <summary>
/// Optional repository capability used only by legacy migration. Implementations must validate
/// and persist the page proof in the same transaction that conditionally writes its bundle.
/// </summary>
public interface ICortexLegacyMigrationPageCommitRepository
{
    ValueTask<CortexLegacyMigrationPageCommitSnapshot> CommitLegacyMigrationPageAsync(
        CortexLegacyMigrationPageCommitRequest request,
        CancellationToken cancellationToken);
}
