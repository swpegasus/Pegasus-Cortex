using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Sync;

public sealed class CortexSyncApplicationService : ICortexSyncApplicationService
{
    private const string SubmitOperation = "cortex.sync.submit";
    private const string SubmitMigrationPageOperation = "cortex.sync.legacy-page.submit";
    private readonly ICortexSyncRepository _repository;
    private readonly ICortexLegacyMigrationPageCommitRepository? _migrationRepository;
    private readonly CortexSyncReceiptPersistenceCoordinator _receiptCoordinator;
    private readonly ICortexSyncBundleMaterializer? _materializer;
    private readonly TimeProvider _timeProvider;

    public CortexSyncApplicationService(
        ICortexSyncRepository repository,
        ICortexSyncReceiptIssuer receiptIssuer,
        TimeProvider timeProvider,
        ICortexSyncBundleMaterializer? materializer = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _migrationRepository = repository as ICortexLegacyMigrationPageCommitRepository;
        _receiptCoordinator = new CortexSyncReceiptPersistenceCoordinator(repository, receiptIssuer);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _materializer = materializer;
    }

    public ValueTask<CortexSyncApplicationResult> SubmitLegacyMigrationPageAsync(
        SubmitCortexLegacyMigrationPageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Page is null || command.ServerAuthority is null)
        {
            return ValueTask.FromResult(Rejected(Diagnostic(
                "cortex_sync_migration_page_invalid",
                "The legacy migration page request is incomplete.",
                SubmitMigrationPageOperation,
                command.TargetNamespace,
                command.ServerAuthority,
                command.Page?.Bundle)));
        }

        CortexSyncLegacyMigrationPageValidationV1 validation;
        try
        {
            validation = CortexSyncContractPolicy.ValidateLegacyMigrationPage(command.Page);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(Rejected(Diagnostic(
                "cortex_sync_migration_page_invalid",
                "The legacy migration page failed bounded canonical validation.",
                SubmitMigrationPageOperation,
                command.TargetNamespace,
                command.ServerAuthority,
                command.Page.Bundle,
                cause: exception.GetType().FullName)));
        }
        if (!validation.Accepted)
        {
            return ValueTask.FromResult(Rejected(Diagnostic(
                "cortex_sync_migration_page_invalid",
                "The legacy migration page failed its exact cursor, source, scope, or chain binding.",
                SubmitMigrationPageOperation,
                command.TargetNamespace,
                command.ServerAuthority,
                command.Page.Bundle,
                cause: string.Join(",", validation.Issues.Select(item => item.Code)))));
        }

        // The page remains present through the repository transaction. A repository without the
        // durable migration-page capability fails closed rather than reducing the page to a bundle.
        return SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                command.TargetNamespace,
                command.Page.Bundle,
                command.ServerAuthority),
            command.Page,
            SubmitMigrationPageOperation,
            cancellationToken);
    }

    public ValueTask<CortexSyncApplicationResult> SubmitAsync(
        SubmitCortexSyncBundleCommand command,
        CancellationToken cancellationToken) =>
        SubmitAsync(
            command,
            migrationPage: null,
            operation: SubmitOperation,
            cancellationToken: cancellationToken);

    private async ValueTask<CortexSyncApplicationResult> SubmitAsync(
        SubmitCortexSyncBundleCommand command,
        CortexLegacyMigrationPageV1? migrationPage,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (command.Bundle is null || command.ServerAuthority is null)
        {
            return Rejected(Diagnostic(
                "cortex_sync_request_invalid",
                "The synchronization request is incomplete.",
                operation,
                command.TargetNamespace,
                command.ServerAuthority,
                command.Bundle));
        }

        CortexSyncAuthorityContext authority;
        try
        {
            authority = CaptureAuthority(command.ServerAuthority);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Rejected(Diagnostic(
                "cortex_sync_authority_invalid",
                "The server-derived synchronization authority is invalid.",
                operation,
                command.TargetNamespace,
                command.ServerAuthority,
                command.Bundle,
                cause: exception.GetType().FullName));
        }

        if (!TryAuthorize(authority, command.TargetNamespace, operation, out var denied))
            return Rejected(denied!, authority: authority, targetNamespace: command.TargetNamespace, bundle: command.Bundle);

        if (!TryValidateBundle(
                command.Bundle,
                command.TargetNamespace,
                authority,
                out var validationCode,
                out var validationMessage))
        {
            return Rejected(Diagnostic(
                validationCode!,
                validationMessage!,
                operation,
                command.TargetNamespace,
                authority,
                command.Bundle));
        }

        var bundle = command.Bundle;
        var receivedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var scope = new CortexSyncRepositoryScope(
            authority.TenantId,
            command.TargetNamespace,
            authority.AuthorityId,
            authority.SubjectId,
            authority.ServiceId,
            authority.PolicyRevision,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            operation);

        var materializeCurrentRecovery = migrationPage is null && _materializer is not null;
        if (materializeCurrentRecovery)
        {
            var preflight = await RunMaterializationPreflightAsync(
                scope,
                bundle,
                cancellationToken).ConfigureAwait(false);
            if (!preflight.Succeeded)
            {
                return Rejected(Diagnostic(
                    preflight.DiagnosticCode,
                    preflight.Message,
                    "cortex.sync.materialization.preflight",
                    scope,
                    bundle,
                    cause: "materialization_preflight_rejected",
                    retryable: preflight.Retryable));
            }
        }

        CortexSyncCommitSnapshot commit;
        try
        {
            CortexSyncCommitSnapshot? repositoryCommit;
            var replayedExistingAttempt = false;
            if (migrationPage is null)
            {
                repositoryCommit = await _repository.CommitAsync(
                    new CortexSyncCommitRequest(scope, bundle, receivedAt),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_migrationRepository is null)
            {
                return Rejected(Diagnostic(
                    "cortex_sync_migration_page_authority_unavailable",
                    "The configured repository cannot prove a durable migration cursor successor.",
                    SubmitMigrationPageOperation,
                    scope,
                    bundle,
                    cause: "repository_capability_unavailable"));
            }
            else
            {
                var migrationCommit = await _migrationRepository.CommitLegacyMigrationPageAsync(
                    new CortexLegacyMigrationPageCommitRequest(scope, migrationPage, receivedAt),
                    cancellationToken).ConfigureAwait(false);
                if (migrationCommit is null)
                {
                    return Rejected(Diagnostic(
                        "cortex_sync_migration_page_commit_invalid",
                        "The repository returned no migration page commit evidence.",
                        SubmitMigrationPageOperation,
                        scope,
                        bundle,
                        cause: "repository_result_invalid"));
                }
                if (migrationCommit.Disposition == CortexLegacyMigrationPageCommitDisposition.Rejected)
                {
                    var exactAttemptConflict = string.Equals(
                        migrationCommit.DiagnosticCode,
                        CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict,
                        StringComparison.Ordinal);
                    return Rejected(Diagnostic(
                        exactAttemptConflict
                            ? CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict
                            : "cortex_sync_migration_cursor_unproven",
                        exactAttemptConflict
                            ? "The immutable bundle attempt already carries different migration-page evidence."
                            : "The migration cursor is not the canonical initial position or an exact durably reconciled server successor.",
                        SubmitMigrationPageOperation,
                        scope,
                        bundle,
                        cause: migrationCommit.DiagnosticCode,
                        retryable: false));
                }
                repositoryCommit = migrationCommit.Commit;
                replayedExistingAttempt = migrationCommit.ReplayedExistingAttempt;
            }
            if (!TryCaptureCommit(
                    repositoryCommit,
                    bundle,
                    receivedAt,
                    replayedExistingAttempt,
                    out commit))
            {
                return Rejected(Diagnostic(
                    "cortex_sync_commit_result_invalid",
                    "The synchronization repository returned invalid commit evidence.",
                    "cortex.sync.commit",
                    scope,
                    bundle,
                    cause: "repository_result_invalid"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Rejected(Diagnostic(
                "cortex_sync_commit_failure",
                "The synchronization repository failed while recording the bundle.",
                "cortex.sync.commit",
                scope,
                bundle,
                cause: exception.GetType().FullName,
                retryable: true));
        }

        // A conflicted journal attempt is durable evidence, but it must never
        // mutate or publish retained current-recovery state. Legacy/generic
        // callers keep their historical conflict-receipt behavior below.
        if (materializeCurrentRecovery &&
            commit.Disposition == CortexSyncCommitDisposition.Conflict)
        {
            return Rejected(Diagnostic(
                "cortex_sync_current_recovery_conflict",
                "The reconnect bundle conflicted with the current journal head and was not materialized.",
                "cortex.sync.materialization",
                scope,
                bundle,
                commit,
                cause: "repository_conflict",
                retryable: false));
        }

        // Legacy/generic consumers retain the original receipt-first replay
        // path. The reconnect gateway deliberately postpones recovery until
        // retained state and its project brief are reverified below.
        if (!materializeCurrentRecovery)
        {
            var recoveredReceipt = await _receiptCoordinator.RecoverAsync(
                scope,
                bundle,
                commit,
                cancellationToken).ConfigureAwait(false);
            if (!recoveredReceipt.Succeeded)
                return Rejected(recoveredReceipt.Error!, scope, bundle, commit);
            if (recoveredReceipt.FoundReceipt)
            {
                var recovered = recoveredReceipt.Receipt!;
                return Accepted(scope, bundle, commit, recovered.Receipt!, recovered.Validation!);
            }
        }

        CortexSyncReadBackSnapshot readBack;
        try
        {
            var repositoryReadBack = await _repository.ReadBackAsync(
                new CortexSyncReadBackRequest(
                    scope,
                    bundle.BundleId,
                    bundle.BundleHash,
                    commit.CommitId,
                    commit.StorageRevision),
                cancellationToken).ConfigureAwait(false);
            if (!TryCaptureReadBack(repositoryReadBack, scope, bundle, commit, out readBack))
            {
                return Rejected(Diagnostic(
                    "cortex_sync_readback_result_invalid",
                    "The synchronization repository returned invalid read-back evidence.",
                    "cortex.sync.readback",
                    scope,
                    bundle,
                    commit,
                    cause: "repository_result_invalid"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Rejected(Diagnostic(
                "cortex_sync_readback_failure",
                "The synchronization repository failed while reading back the durable bundle.",
                "cortex.sync.readback",
                scope,
                bundle,
                commit,
                cause: exception.GetType().FullName,
                retryable: true));
        }

        var readBackMatches = readBack.MatchedEventCount == readBack.SourceEventCount &&
            string.Equals(readBack.ReadBackHash, commit.DurableStateHash, StringComparison.Ordinal);
        if (materializeCurrentRecovery)
        {
            if (!readBackMatches)
            {
                return Rejected(Diagnostic(
                    "cortex_sync_materialization_source_mismatch",
                    "Retained-state materialization requires an exact durable journal read-back.",
                    "cortex.sync.materialization",
                    scope,
                    bundle,
                    commit,
                    cause: "journal_readback_mismatch",
                    retryable: true));
            }

            var materialized = await RunMaterializationAsync(
                scope,
                bundle,
                commit,
                readBack,
                cancellationToken).ConfigureAwait(false);
            if (!materialized.Succeeded)
            {
                return Rejected(Diagnostic(
                    materialized.DiagnosticCode,
                    materialized.Message,
                    "cortex.sync.materialization",
                    scope,
                    bundle,
                    commit,
                    cause: "materialization_not_verified",
                    retryable: materialized.Retryable));
            }

            // A lost response may leave an already authenticated receipt. It
            // is reusable only after the idempotent materialization was read
            // back again, keeping retry bytes stable without trusting stale
            // projection state.
            var recoveredReceipt = await _receiptCoordinator.RecoverAsync(
                scope,
                bundle,
                commit,
                cancellationToken).ConfigureAwait(false);
            if (!recoveredReceipt.Succeeded)
                return Rejected(recoveredReceipt.Error!, scope, bundle, commit);
            if (recoveredReceipt.FoundReceipt)
            {
                var recovered = recoveredReceipt.Receipt!;
                return Accepted(scope, bundle, commit, recovered.Receipt!, recovered.Validation!);
            }
        }
        var reconciliation = new CortexSyncReceiptReconciliationV1(
            readBackMatches ? "reconciled" : "mismatch",
            readBack.SourceEventCount,
            readBack.MatchedEventCount,
            readBack.ReadBackAt,
            readBack.ReadBackHash);
        var issuance = new CortexSyncReceiptIssuance(scope, bundle, commit.ReceivedAt, commit, reconciliation);

        var receiptResolution = await _receiptCoordinator.IssueAndPersistAsync(
            issuance,
            cancellationToken).ConfigureAwait(false);
        if (!receiptResolution.Succeeded)
            return Rejected(receiptResolution.Error!, scope, bundle, commit);

        var receipt = receiptResolution.Receipt!;
        var receiptValidation = receiptResolution.Validation!;
        return Accepted(scope, bundle, commit, receipt, receiptValidation);
    }

    private async ValueTask<CortexSyncMaterializationResult> RunMaterializationPreflightAsync(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _materializer!.PreflightAsync(
                new CortexSyncMaterializationPreflight(scope, bundle),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CortexSyncMaterializationResult(
                false,
                "cortex_sync_materialization_preflight_failed",
                "The current-recovery payload could not be validated before journal commit.",
                exception is IOException or TimeoutException);
        }
    }

    private async ValueTask<CortexSyncMaterializationResult> RunMaterializationAsync(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        CortexSyncReadBackSnapshot readBack,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _materializer!.MaterializeAndVerifyAsync(
                new CortexSyncMaterializationRequest(scope, bundle, commit, readBack),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CortexSyncMaterializationResult(
                false,
                "cortex_sync_materialization_failed",
                "The durable journal payload could not be materialized and read back exactly.",
                exception is IOException or TimeoutException);
        }
    }

    private static CortexSyncAuthorityContext CaptureAuthority(CortexSyncAuthorityContext authority) =>
        new(
            authority.TenantId,
            authority.AuthorityId,
            authority.SubjectId,
            authority.ServiceId,
            authority.PolicyRevision,
            authority.Grants.Select(grant => new CortexSyncNamespaceGrant(grant.Namespace, grant.CanSync)),
            authority.AllowedPayloadContractIds);

    private static bool TryAuthorize(
        CortexSyncAuthorityContext authority,
        string targetNamespace,
        string operation,
        out PegasusDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (!CortexSyncApplicationValidation.IsIdentifier(authority.TenantId) ||
            !CortexSyncApplicationValidation.IsIdentifier(authority.AuthorityId) ||
            !CortexSyncApplicationValidation.IsIdentifier(authority.SubjectId) ||
            !CortexSyncApplicationValidation.IsIdentifier(authority.ServiceId) ||
            authority.PolicyRevision <= 0 ||
            !IsNamespace(targetNamespace) ||
            authority.Grants.Count is < 1 or > CortexSyncApplicationLimits.MaximumAuthorityGrants ||
            authority.Grants.Any(grant => !IsNamespace(grant.Namespace)) ||
            authority.AllowedPayloadContractIds.Count is < 1 or > CortexSyncContractLimits.MaximumContractIds)
        {
            diagnostic = Diagnostic(
                "cortex_sync_authority_invalid",
                "The server-derived synchronization authority is incomplete or invalid.",
                operation,
                targetNamespace,
                authority,
                bundle: null);
            return false;
        }

        var grant = authority.Grants.SingleOrDefault(item =>
            string.Equals(item.Namespace, targetNamespace, StringComparison.Ordinal));
        if (grant is null || !grant.CanSync)
        {
            diagnostic = Diagnostic(
                "cortex_sync_access_denied",
                "The target namespace is outside current server-derived synchronization authority.",
                operation,
                targetNamespace,
                authority,
                bundle: null);
            return false;
        }

        return true;
    }

    private static bool TryValidateBundle(
        CortexOfflineBundleV1 bundle,
        string targetNamespace,
        CortexSyncAuthorityContext authority,
        out string? code,
        out string? message)
    {
        code = null;
        message = null;
        try
        {
            if (!CortexSyncContractPolicy.HasValidHash(bundle) ||
                !CortexSyncApplicationValidation.IsIdentifier(bundle.StoreId) ||
                !CortexSyncApplicationValidation.IsIdentifier(bundle.StoreEpoch) ||
                !CortexSyncApplicationValidation.IsIdentifier(bundle.ProjectId) ||
                bundle.Events.Count is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle ||
                bundle.FirstSequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
                bundle.LastSequence < bundle.FirstSequence ||
                bundle.LastSequence > CortexSyncContractLimits.MaximumSequence ||
                bundle.Events.Count != bundle.LastSequence - bundle.FirstSequence + 1)
            {
                return InvalidBundle(out code, out message);
            }

            long payloadBytes = 0;
            var expectedPreviousHash = bundle.PriorEventHash;
            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            var eventHashes = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < bundle.Events.Count; index++)
            {
                var item = bundle.Events[index];
                payloadBytes = checked(payloadBytes + item.PayloadBytes);
                if (!CortexSyncContractPolicy.HasValidHash(item) ||
                    !string.Equals(item.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
                    !string.Equals(item.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
                    !string.Equals(item.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
                    item.Sequence != bundle.FirstSequence + index ||
                    !string.Equals(item.PreviousEventHash, expectedPreviousHash, StringComparison.Ordinal) ||
                    !eventIds.Add(item.EventId) || !eventHashes.Add(item.EventHash))
                {
                    return InvalidBundle(out code, out message);
                }

                if (!string.Equals(item.TenantIdHint, authority.TenantId, StringComparison.Ordinal) ||
                    !string.Equals(item.NamespaceHint, targetNamespace, StringComparison.Ordinal) ||
                    item.ObservedPolicyRevision is not null &&
                    item.ObservedPolicyRevision != authority.PolicyRevision)
                {
                    code = "cortex_sync_hint_mismatch";
                    message = "Bundle authority hints do not match current server-derived scope.";
                    return false;
                }

                if (!authority.AllowedPayloadContractIds.Contains(item.PayloadContractId))
                {
                    code = "cortex_sync_payload_contract_denied";
                    message = "The bundle contains a payload contract outside current server policy.";
                    return false;
                }
                expectedPreviousHash = item.EventHash;
            }

            if (payloadBytes > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
                return InvalidBundle(out code, out message);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            return InvalidBundle(out code, out message);
        }
    }

    private static bool InvalidBundle(out string? code, out string? message)
    {
        code = "cortex_sync_bundle_invalid";
        message = "The offline bundle failed canonical integrity, identity, chain, or bounds validation.";
        return false;
    }

    private static bool TryCaptureCommit(
        CortexSyncCommitSnapshot? value,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        bool replayedExistingAttempt,
        out CortexSyncCommitSnapshot commit)
    {
        commit = null!;
        if (value is null || !Enum.IsDefined(value.Disposition) ||
            !CortexSyncApplicationValidation.IsIdentifier(value.CommitId) ||
            !CortexSyncApplicationValidation.IsIdentifier(value.StorageRevision) ||
            value.ReceivedAt.Offset != TimeSpan.Zero ||
            (!replayedExistingAttempt && value.ReceivedAt > receivedAt) ||
            value.CommittedAt.Offset != TimeSpan.Zero || value.CommittedAt < value.ReceivedAt ||
            !CortexSyncApplicationValidation.IsHash(value.DurableStateHash) ||
            value.Outcomes.Count != bundle.Events.Count)
        {
            return false;
        }

        for (var index = 0; index < value.Outcomes.Count; index++)
        {
            var outcome = value.Outcomes[index];
            var item = bundle.Events[index];
            if (outcome is null || !outcome.Durable || outcome.Sequence != item.Sequence ||
                !string.Equals(outcome.EventId, item.EventId, StringComparison.Ordinal) ||
                !string.Equals(outcome.EventHash, item.EventHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var dispositions = value.Outcomes.Select(item => item.Disposition).ToArray();
        var coherent = value.Disposition switch
        {
            CortexSyncCommitDisposition.Committed =>
                dispositions.All(item => item is CortexSyncDispositionIds.Applied or CortexSyncDispositionIds.Duplicate) &&
                dispositions.Contains(CortexSyncDispositionIds.Applied, StringComparer.Ordinal),
            CortexSyncCommitDisposition.Existing =>
                dispositions.All(item => item == CortexSyncDispositionIds.Duplicate),
            CortexSyncCommitDisposition.Conflict =>
                dispositions.All(item => item != CortexSyncDispositionIds.Applied) &&
                dispositions.Any(item => item is CortexSyncDispositionIds.Conflict or
                    CortexSyncDispositionIds.Rejected or CortexSyncDispositionIds.Quarantined),
            _ => false
        };
        if (!coherent)
            return false;

        try
        {
            commit = new CortexSyncCommitSnapshot(
                value.Disposition,
                value.CommitId,
                value.StorageRevision,
                value.ReceivedAt,
                value.CommittedAt,
                value.DurableStateHash,
                value.Outcomes.Select(CopyOutcome));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCaptureReadBack(
        CortexSyncReadBackSnapshot? value,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        out CortexSyncReadBackSnapshot readBack)
    {
        readBack = null!;
        if (value is null ||
            !string.Equals(value.TenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(value.Namespace, scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(value.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(value.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(value.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(value.BundleHash, bundle.BundleHash, StringComparison.Ordinal) ||
            !string.Equals(value.CommitId, commit.CommitId, StringComparison.Ordinal) ||
            !string.Equals(value.StorageRevision, commit.StorageRevision, StringComparison.Ordinal) ||
            value.SourceEventCount != bundle.Events.Count ||
            value.MatchedEventCount < 0 || value.MatchedEventCount > value.SourceEventCount ||
            value.ReadBackAt.Offset != TimeSpan.Zero || value.ReadBackAt < commit.CommittedAt ||
            !CortexSyncApplicationValidation.IsHash(value.ReadBackHash))
        {
            return false;
        }

        readBack = value with { };
        return true;
    }

    private static CortexSyncReceiptOutcomeV1 CopyOutcome(CortexSyncReceiptOutcomeV1 value) =>
        new(
            value.Sequence,
            value.EventId,
            value.EventHash,
            value.Disposition,
            value.Durable,
            value.ServerEntityId,
            value.ServerRevisionId,
            value.DiagnosticCode);

    private static bool IsNamespace(string? value)
    {
        return CortexSyncApplicationValidation.IsNamespace(value);
    }

    private static CortexSyncApplicationResult Accepted(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit,
        CortexSyncReceiptV1 receipt,
        CortexSyncReceiptValidationV1 validation)
    {
        if (receipt.Reconciliation.State == "mismatch")
        {
            return new CortexSyncApplicationResult(
                receiptAccepted: true,
                durablyReconciled: false,
                disposition: commit.Disposition,
                receipt: receipt,
                diagnostics: [Diagnostic(
                    "cortex_sync_readback_mismatch",
                    "The committed bundle did not match the repository read-back evidence.",
                    "cortex.sync.readback",
                    scope,
                    bundle,
                    commit,
                    receipt.ReceiptId,
                    "durable_state_mismatch")]);
        }

        if (commit.Disposition == CortexSyncCommitDisposition.Conflict)
        {
            return new CortexSyncApplicationResult(
                receiptAccepted: true,
                durablyReconciled: false,
                disposition: commit.Disposition,
                receipt: receipt,
                diagnostics: [Diagnostic(
                    "cortex_sync_conflict",
                    "The bundle was durably recorded but conflicted with current server state.",
                    "cortex.sync.commit",
                    scope,
                    bundle,
                    commit,
                    receipt.ReceiptId,
                    "repository_conflict")]);
        }

        return new CortexSyncApplicationResult(
            receiptAccepted: true,
            durablyReconciled: validation.DurablyReconciled,
            disposition: commit.Disposition,
            receipt: receipt,
            diagnostics: []);
    }

    private static CortexSyncApplicationResult Rejected(
        CortexSyncReceiptPersistenceError error,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot commit) =>
        Rejected(Diagnostic(
            error.Code,
            error.Message,
            error.Operation,
            scope,
            bundle,
            commit,
            error.ReceiptId,
            error.Cause,
            error.Retryable));

    private static CortexSyncApplicationResult Rejected(
        PegasusDiagnostic diagnostic,
        CortexSyncAuthorityContext? authority = null,
        string? targetNamespace = null,
        CortexOfflineBundleV1? bundle = null) =>
        new(false, false, null, null, [SnapshotDiagnostic(diagnostic)]);

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

    private static PegasusDiagnostic Diagnostic(
        string code,
        string message,
        string operation,
        string? targetNamespace,
        CortexSyncAuthorityContext? authority,
        CortexOfflineBundleV1? bundle,
        string? cause = null,
        bool retryable = false) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            operation,
            retryable,
            Context(targetNamespace, authority, bundle, commit: null, receiptId: null),
            cause);

    private static PegasusDiagnostic Diagnostic(
        string code,
        string message,
        string operation,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot? commit = null,
        string? receiptId = null,
        string? cause = null,
        bool retryable = false) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            operation,
            retryable,
            Context(scope, bundle, commit, receiptId),
            cause);

    private static IReadOnlyDictionary<string, string?> Context(
        string? targetNamespace,
        CortexSyncAuthorityContext? authority,
        CortexOfflineBundleV1? bundle,
        CortexSyncCommitSnapshot? commit,
        string? receiptId) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["tenantId"] = SafeIdentifier(authority?.TenantId),
            ["namespace"] = SafeNamespace(targetNamespace),
            ["authorityId"] = SafeIdentifier(authority?.AuthorityId),
            ["subjectId"] = SafeIdentifier(authority?.SubjectId),
            ["serviceId"] = SafeIdentifier(authority?.ServiceId),
            ["policyRevision"] = authority is { PolicyRevision: > 0 }
                ? authority.PolicyRevision.ToString()
                : null,
            ["storeId"] = SafeIdentifier(bundle?.StoreId),
            ["storeEpoch"] = SafeIdentifier(bundle?.StoreEpoch),
            ["projectId"] = SafeIdentifier(bundle?.ProjectId),
            ["bundleId"] = SafeIdentifier(bundle?.BundleId),
            ["bundleHash"] = SafeHash(bundle?.BundleHash),
            ["commitId"] = SafeIdentifier(commit?.CommitId),
            ["storageRevision"] = SafeIdentifier(commit?.StorageRevision),
            ["receiptId"] = SafeIdentifier(receiptId)
        };

    private static IReadOnlyDictionary<string, string?> Context(
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitSnapshot? commit,
        string? receiptId) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["tenantId"] = scope.TenantId,
            ["namespace"] = scope.Namespace,
            ["authorityId"] = scope.AuthorityId,
            ["subjectId"] = scope.SubjectId,
            ["serviceId"] = scope.ServiceId,
            ["policyRevision"] = scope.PolicyRevision.ToString(),
            ["storeId"] = bundle.StoreId,
            ["storeEpoch"] = bundle.StoreEpoch,
            ["projectId"] = bundle.ProjectId,
            ["bundleId"] = bundle.BundleId,
            ["bundleHash"] = bundle.BundleHash,
            ["commitId"] = commit?.CommitId,
            ["storageRevision"] = commit?.StorageRevision,
            ["receiptId"] = SafeIdentifier(receiptId)
        };

    private static string? SafeNamespace(string? value) => IsNamespace(value) ? value : null;
    private static string? SafeIdentifier(string? value) =>
        CortexSyncApplicationValidation.IsIdentifier(value) ? value : null;
    private static string? SafeHash(string? value) => CortexSyncApplicationValidation.IsHash(value) ? value : null;
}
