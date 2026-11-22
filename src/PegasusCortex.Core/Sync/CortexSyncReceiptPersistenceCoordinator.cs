using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.Core.Sync;

/// <summary>
/// Owns the complete receipt lifecycle after bundle commit/read-back: idempotent lookup,
/// host proof verification, immutable append, conflict recovery, and exact canonical read-back.
/// </summary>
internal sealed class CortexSyncReceiptPersistenceCoordinator
{
    private readonly ICortexSyncRepository _repository;
    private readonly ICortexSyncReceiptIssuer _receiptIssuer;

    public CortexSyncReceiptPersistenceCoordinator(
        ICortexSyncRepository repository,
        ICortexSyncReceiptIssuer receiptIssuer)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _receiptIssuer = receiptIssuer ?? throw new ArgumentNullException(nameof(receiptIssuer));
    }

    public async ValueTask<CortexSyncReceiptRecoveryResult> RecoverAsync(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        var lookup = await LookupAsync(scope, bundle, commit, cancellationToken).ConfigureAwait(false);
        if (lookup.Error is not null)
            return CortexSyncReceiptRecoveryResult.Failure(lookup.Error);
        return lookup.Receipt is null
            ? CortexSyncReceiptRecoveryResult.NotFound
            : CortexSyncReceiptRecoveryResult.Found(lookup.Receipt);
    }

    public async ValueTask<CortexSyncReceiptPersistenceResult> IssueAndPersistAsync(
        CortexSyncReceiptIssuance issuance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuance);
        cancellationToken.ThrowIfCancellationRequested();

        CortexSyncReceiptV1 issuedReceipt;
        try
        {
            issuedReceipt = await _receiptIssuer.IssueAsync(issuance, cancellationToken).ConfigureAwait(false);
            if (issuedReceipt is null)
                throw new InvalidOperationException("The receipt issuer returned no receipt.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "cortex_sync_receipt_issuer_failure",
                "The host receipt issuer failed while authenticating synchronization evidence.",
                "cortex.sync.receipt.issue",
                receiptId: null,
                exception.GetType().FullName,
                retryable: true);
        }

        var verified = await VerifyAsync(
            issuedReceipt,
            issuance.Bundle,
            issuance.Commit,
            issuance,
            cancellationToken).ConfigureAwait(false);
        if (!verified.Succeeded)
            return verified;

        byte[] canonicalReceipt;
        CortexSyncReceiptAppendRequest appendRequest;
        CortexSyncReceiptAppendSnapshot append;
        try
        {
            canonicalReceipt = CortexCanonicalJson.Serialize(issuedReceipt);
            appendRequest = new CortexSyncReceiptAppendRequest(
                issuance.Scope,
                issuedReceipt,
                canonicalReceipt);
            append = await _repository.AppendReceiptAsync(
                appendRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "cortex_sync_receipt_persistence_failure",
                "The synchronization repository failed while appending authenticated receipt evidence.",
                "cortex.sync.receipt.append",
                issuedReceipt.ReceiptId,
                exception.GetType().FullName,
                retryable: true);
        }

        if (append is null)
        {
            return Failure(
                "cortex_sync_receipt_persistence_invalid",
                "The synchronization repository returned no receipt append evidence.",
                "cortex.sync.receipt.append",
                issuedReceipt.ReceiptId,
                "repository_result_invalid");
        }

        if (append.Disposition == CortexSyncReceiptAppendDisposition.Conflict)
        {
            // A concurrent writer may have won between lookup and append. Recover that exact row
            // through the same canonical and proof checks instead of issuing again.
            var concurrent = await LookupAsync(
                issuance.Scope,
                issuance.Bundle,
                issuance.Commit,
                cancellationToken).ConfigureAwait(false);
            if (concurrent.Error is not null)
                return CortexSyncReceiptPersistenceResult.Failure(concurrent.Error);
            if (concurrent.Receipt is not null)
                return concurrent.Receipt;
            return Failure(
                "cortex_sync_receipt_persistence_invalid",
                "The conflicting receipt append could not be recovered as exact immutable evidence.",
                "cortex.sync.receipt.append",
                issuedReceipt.ReceiptId,
                "repository_conflict");
        }

        if (!TryCaptureAppend(
                append,
                issuance.Scope,
                issuedReceipt,
                appendRequest.CanonicalSha256))
        {
            return Failure(
                "cortex_sync_receipt_persistence_invalid",
                "The synchronization repository did not attest an exact immutable receipt append.",
                "cortex.sync.receipt.append",
                issuedReceipt.ReceiptId,
                "repository_result_invalid");
        }

        CortexSyncReceiptReadBackSnapshot receiptReadBack;
        try
        {
            receiptReadBack = await _repository.ReadReceiptAsync(
                new CortexSyncReceiptReadRequest(
                    issuance.Scope,
                    issuedReceipt.BundleId,
                    issuedReceipt.ReceiptId,
                    issuedReceipt.ReceiptHash,
                    append.CanonicalSha256),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "cortex_sync_receipt_readback_failure",
                "The synchronization repository failed while reading back authenticated receipt evidence.",
                "cortex.sync.receipt.readback",
                issuedReceipt.ReceiptId,
                exception.GetType().FullName,
                retryable: true);
        }

        if (!TryCaptureReadBack(
                receiptReadBack,
                append,
                issuance,
                canonicalReceipt,
                out var persistedReceipt))
        {
            return Failure(
                "cortex_sync_receipt_readback_invalid",
                "The stored receipt did not match the exact authenticated public receipt.",
                "cortex.sync.receipt.readback",
                issuedReceipt.ReceiptId,
                "repository_result_invalid");
        }

        return CortexSyncReceiptPersistenceResult.Success(
            persistedReceipt,
            CortexSyncContractPolicy.ValidateReceipt(
                persistedReceipt,
                issuance.Bundle,
                authenticationProofVerified: true));
    }

    private async ValueTask<ReceiptLookupAttempt> LookupAsync(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        CancellationToken cancellationToken)
    {
        CortexSyncReceiptReadBackSnapshot? value;
        try
        {
            value = await _repository.FindReceiptAsync(
                new CortexSyncReceiptLookupRequest(
                    scope,
                    bundle.BundleId,
                    bundle.BundleHash,
                    commit.CommitId,
                    commit.StorageRevision),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReceiptLookupAttempt.Failed(new CortexSyncReceiptPersistenceError(
                "cortex_sync_receipt_lookup_failure",
                "The synchronization repository failed while looking up prior receipt evidence.",
                "cortex.sync.receipt.lookup",
                ReceiptId: null,
                exception.GetType().FullName,
                Retryable: true));
        }

        if (value is null)
            return ReceiptLookupAttempt.NotFound;
        if (!TryCaptureLookup(value, scope, bundle, commit, out var receipt))
        {
            return ReceiptLookupAttempt.Failed(new CortexSyncReceiptPersistenceError(
                "cortex_sync_receipt_lookup_invalid",
                "The stored receipt lookup did not match the exact committed bundle evidence.",
                "cortex.sync.receipt.lookup",
                value.ReceiptId,
                "repository_result_invalid",
                Retryable: false));
        }

        var verified = await VerifyAsync(
            receipt,
            bundle,
            commit,
            issuance: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return verified.Succeeded
            ? ReceiptLookupAttempt.Found(verified)
            : ReceiptLookupAttempt.Failed(verified.Error!);
    }

    private async ValueTask<CortexSyncReceiptPersistenceResult> VerifyAsync(
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        CortexSyncReceiptIssuance? issuance,
        CancellationToken cancellationToken)
    {
        bool proofVerified;
        CortexSyncReceiptValidationV1 validation;
        try
        {
            proofVerified = await _receiptIssuer.VerifyProofAsync(
                receipt,
                cancellationToken).ConfigureAwait(false);
            validation = CortexSyncContractPolicy.ValidateReceipt(
                receipt,
                bundle,
                proofVerified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "cortex_sync_receipt_issuer_failure",
                "The host receipt issuer failed while verifying synchronization evidence.",
                "cortex.sync.receipt.verify",
                receipt.ReceiptId,
                exception.GetType().FullName,
                retryable: true);
        }

        var exactEvidence = issuance is null
            ? MatchesCommit(receipt, bundle, commit)
            : MatchesIssuance(receipt, issuance);
        if (!proofVerified || !validation.Accepted || !exactEvidence)
        {
            var cause = !proofVerified
                ? "proof_verification_failed"
                : validation.Issues.FirstOrDefault()?.Code ?? "issued_receipt_mismatch";
            return Failure(
                "cortex_sync_receipt_invalid",
                "The receipt did not authenticate the exact committed bundle evidence.",
                "cortex.sync.receipt.validate",
                receipt.ReceiptId,
                cause);
        }

        return CortexSyncReceiptPersistenceResult.Success(receipt, validation);
    }

    private static bool TryCaptureAppend(
        CortexSyncReceiptAppendSnapshot? value,
        CortexSyncRepositoryScope scope,
        CortexSyncReceiptV1 receipt,
        string expectedCanonicalSha256)
    {
        var earliestRecordedAt = receipt.Reconciliation.ReadBackAt ?? receipt.Durability.CommittedAt;
        return value is not null &&
            value.Disposition is CortexSyncReceiptAppendDisposition.Appended or CortexSyncReceiptAppendDisposition.Existing &&
            string.Equals(value.TenantId, scope.TenantId, StringComparison.Ordinal) &&
            string.Equals(value.Namespace, scope.Namespace, StringComparison.Ordinal) &&
            string.Equals(value.StoreId, receipt.StoreId, StringComparison.Ordinal) &&
            string.Equals(value.StoreEpoch, receipt.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(value.ProjectId, receipt.ProjectId, StringComparison.Ordinal) &&
            string.Equals(value.BundleId, receipt.BundleId, StringComparison.Ordinal) &&
            string.Equals(value.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal) &&
            string.Equals(value.ReceiptHash, receipt.ReceiptHash, StringComparison.Ordinal) &&
            string.Equals(value.CanonicalSha256, expectedCanonicalSha256, StringComparison.Ordinal) &&
            value.RecordedAt.Offset == TimeSpan.Zero &&
            value.RecordedAt >= earliestRecordedAt;
    }

    private static bool TryCaptureLookup(
        CortexSyncReceiptReadBackSnapshot value,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        out CortexSyncReceiptV1 receipt)
    {
        receipt = null!;
        if (!string.Equals(value.TenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(value.Namespace, scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(value.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(value.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(value.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            value.RecordedAt.Offset != TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            var candidate = CortexCanonicalJson.Deserialize<CortexSyncReceiptV1>(value.CanonicalReceipt.Span);
            var earliestRecordedAt = candidate.Reconciliation.ReadBackAt ?? candidate.Durability.CommittedAt;
            if (!string.Equals(value.ReceiptId, candidate.ReceiptId, StringComparison.Ordinal) ||
                value.RecordedAt < earliestRecordedAt ||
                !CortexCanonicalJson.Serialize(candidate).AsSpan().SequenceEqual(value.CanonicalReceipt.Span) ||
                !CortexSyncContractPolicy.ValidateReceipt(candidate, bundle, true).Accepted ||
                !MatchesCommit(candidate, bundle, commit))
            {
                return false;
            }

            receipt = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryCaptureReadBack(
        CortexSyncReceiptReadBackSnapshot? value,
        CortexSyncReceiptAppendSnapshot append,
        CortexSyncReceiptIssuance issuance,
        ReadOnlySpan<byte> expectedCanonicalReceipt,
        out CortexSyncReceiptV1 receipt)
    {
        receipt = null!;
        var scope = issuance.Scope;
        var bundle = issuance.Bundle;
        if (value is null ||
            !string.Equals(value.TenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(value.Namespace, scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(value.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(value.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(value.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(value.ReceiptId, append.ReceiptId, StringComparison.Ordinal) ||
            value.RecordedAt != append.RecordedAt ||
            !value.CanonicalReceipt.Span.SequenceEqual(expectedCanonicalReceipt) ||
            !string.Equals(
                CortexCanonicalJson.Sha256(value.CanonicalReceipt.Span),
                append.CanonicalSha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var candidate = CortexCanonicalJson.Deserialize<CortexSyncReceiptV1>(value.CanonicalReceipt.Span);
            if (!CortexCanonicalJson.Serialize(candidate).AsSpan().SequenceEqual(expectedCanonicalReceipt) ||
                !CortexSyncContractPolicy.ValidateReceipt(candidate, bundle, true).Accepted ||
                !MatchesIssuance(candidate, issuance))
            {
                return false;
            }

            receipt = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool MatchesIssuance(
        CortexSyncReceiptV1 receipt,
        CortexSyncReceiptIssuance issuance)
    {
        var commit = issuance.Commit;
        var reconciliation = issuance.Reconciliation;
        return string.Equals(receipt.StoreId, issuance.Bundle.StoreId, StringComparison.Ordinal) &&
            string.Equals(receipt.StoreEpoch, issuance.Bundle.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(receipt.ProjectId, issuance.Bundle.ProjectId, StringComparison.Ordinal) &&
            string.Equals(receipt.BundleId, issuance.Bundle.BundleId, StringComparison.Ordinal) &&
            string.Equals(receipt.BundleHash, issuance.Bundle.BundleHash, StringComparison.Ordinal) &&
            receipt.ReceivedAt == issuance.ReceivedAt &&
            string.Equals(receipt.Durability.CommitId, commit.CommitId, StringComparison.Ordinal) &&
            string.Equals(receipt.Durability.StorageRevision, commit.StorageRevision, StringComparison.Ordinal) &&
            receipt.Durability.CommittedAt == commit.CommittedAt &&
            receipt.Outcomes.Count == commit.Outcomes.Count &&
            receipt.Outcomes.Zip(commit.Outcomes, OutcomeEquals).All(matches => matches) &&
            string.Equals(receipt.Reconciliation.State, reconciliation.State, StringComparison.Ordinal) &&
            receipt.Reconciliation.SourceEventCount == reconciliation.SourceEventCount &&
            receipt.Reconciliation.MatchedEventCount == reconciliation.MatchedEventCount &&
            receipt.Reconciliation.ReadBackAt == reconciliation.ReadBackAt &&
            string.Equals(receipt.Reconciliation.ReadBackHash, reconciliation.ReadBackHash, StringComparison.Ordinal);
    }

    private static bool MatchesCommit(
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit) =>
        receipt.Reconciliation.State is "reconciled" or "mismatch" &&
        string.Equals(receipt.StoreId, bundle.StoreId, StringComparison.Ordinal) &&
        string.Equals(receipt.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(receipt.ProjectId, bundle.ProjectId, StringComparison.Ordinal) &&
        string.Equals(receipt.BundleId, bundle.BundleId, StringComparison.Ordinal) &&
        string.Equals(receipt.BundleHash, bundle.BundleHash, StringComparison.Ordinal) &&
        receipt.ReceivedAt == commit.ReceivedAt &&
        string.Equals(receipt.Durability.CommitId, commit.CommitId, StringComparison.Ordinal) &&
        string.Equals(receipt.Durability.StorageRevision, commit.StorageRevision, StringComparison.Ordinal) &&
        receipt.Durability.CommittedAt == commit.CommittedAt &&
        receipt.Outcomes.Count == commit.Outcomes.Count &&
        receipt.Outcomes.Zip(commit.Outcomes, OutcomeEquals).All(matches => matches);

    private static bool OutcomeEquals(
        CortexSyncReceiptOutcomeV1 left,
        CortexSyncReceiptOutcomeV1 right) =>
        left.Sequence == right.Sequence &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal) &&
        string.Equals(left.EventHash, right.EventHash, StringComparison.Ordinal) &&
        string.Equals(left.Disposition, right.Disposition, StringComparison.Ordinal) &&
        left.Durable == right.Durable &&
        string.Equals(left.ServerEntityId, right.ServerEntityId, StringComparison.Ordinal) &&
        string.Equals(left.ServerRevisionId, right.ServerRevisionId, StringComparison.Ordinal) &&
        string.Equals(left.DiagnosticCode, right.DiagnosticCode, StringComparison.Ordinal);

    private static CortexSyncReceiptPersistenceResult Failure(
        string code,
        string message,
        string operation,
        string? receiptId,
        string? cause,
        bool retryable = false) =>
        CortexSyncReceiptPersistenceResult.Failure(new CortexSyncReceiptPersistenceError(
            code,
            message,
            operation,
            receiptId,
            cause,
            retryable));

    private sealed record ReceiptLookupAttempt(
        CortexSyncReceiptPersistenceResult? Receipt,
        CortexSyncReceiptPersistenceError? Error)
    {
        public static ReceiptLookupAttempt NotFound { get; } = new(null, null);

        public static ReceiptLookupAttempt Found(CortexSyncReceiptPersistenceResult receipt) =>
            new(receipt, null);

        public static ReceiptLookupAttempt Failed(CortexSyncReceiptPersistenceError error) =>
            new(null, error);
    }
}

internal sealed record CortexSyncReceiptPersistenceError(
    string Code,
    string Message,
    string Operation,
    string? ReceiptId,
    string? Cause,
    bool Retryable);

internal sealed record CortexSyncReceiptRecoveryResult(
    CortexSyncReceiptPersistenceResult? Receipt,
    CortexSyncReceiptPersistenceError? Error)
{
    public bool Succeeded => Error is null;
    public bool FoundReceipt => Receipt is not null;

    public static CortexSyncReceiptRecoveryResult NotFound { get; } = new(null, null);

    public static CortexSyncReceiptRecoveryResult Found(CortexSyncReceiptPersistenceResult receipt) =>
        new(receipt, null);

    public static CortexSyncReceiptRecoveryResult Failure(CortexSyncReceiptPersistenceError error) =>
        new(null, error);
}

internal sealed record CortexSyncReceiptPersistenceResult(
    CortexSyncReceiptV1? Receipt,
    CortexSyncReceiptValidationV1? Validation,
    CortexSyncReceiptPersistenceError? Error)
{
    public bool Succeeded => Receipt is not null && Validation is not null && Error is null;

    public static CortexSyncReceiptPersistenceResult Success(
        CortexSyncReceiptV1 receipt,
        CortexSyncReceiptValidationV1 validation) =>
        new(receipt, validation, null);

    public static CortexSyncReceiptPersistenceResult Failure(CortexSyncReceiptPersistenceError error) =>
        new(null, null, error);
}
