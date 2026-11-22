using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;

namespace Pegasus.Cortex.Persistence.PostgreSql.Sync;

/// <summary>
/// Append-only PostgreSQL synchronization journal. An attempt is immutable whether it advances
/// the accepted source head or records a conflict; exact retries reread the original evidence.
/// </summary>
public sealed partial class PostgreSqlCortexSyncRepository :
    ICortexSyncRepository,
    ICortexSyncPublishedHeadRepositoryV2,
    ICortexLegacyMigrationPageCommitRepository
{
    private const string HeadConflictCode = "cortex_sync_head_conflict";
    private const string IdentityConflictCode = "cortex_sync_identity_conflict";
    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlCortexSyncRepository(
        PostgreSqlTransactionRunner transactions,
        TimeProvider? timeProvider = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexSyncCommitSnapshot> CommitAsync(
        CortexSyncCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = PostgreSqlSyncCanonicalCodec.Capture(request);
        var result = await CommitCapturedAsync(
                captured,
                migrationPage: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Commit ?? throw PostgreSqlSyncCanonicalCodec.Failure(
            "cortex_postgresql_sync_unexpected_migration_rejection",
            captured.Scope,
            captured.Bundle.BundleId);
    }

    public ValueTask<CortexLegacyMigrationPageCommitSnapshot> CommitLegacyMigrationPageAsync(
        CortexLegacyMigrationPageCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = PostgreSqlSyncCanonicalCodec.Capture(request);
        return CommitCapturedAsync(captured.Bundle, captured, cancellationToken);
    }

    private ValueTask<CortexLegacyMigrationPageCommitSnapshot> CommitCapturedAsync(
        PostgreSqlSyncCapturedBundle captured,
        PostgreSqlSyncCapturedMigrationPage? migrationPage,
        CancellationToken cancellationToken)
    {
        var attemptId = PostgreSqlSyncHashPolicy.AttemptId(captured);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromSync(captured.Scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await AcquireScopeLockAsync(
                    connection,
                    transaction,
                    captured.Scope,
                    token).ConfigureAwait(false);

                var existing = await ReadStoredCommitAsync(
                    connection,
                    transaction,
                    captured.Scope,
                    attemptId,
                    token).ConfigureAwait(false);
                if (existing is not null)
                {
                    EnsureExactBundleAttempt(existing, captured);
                    if (migrationPage is not null &&
                        !SameMigrationPage(existing.MigrationPage, migrationPage))
                    {
                        return RejectedMigrationCommit(
                            CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict);
                    }
                    if (migrationPage is null && existing.MigrationPage is not null)
                        EnsureExactAttempt(existing, captured, migrationPage);
                    return AcceptedMigrationCommit(existing.Snapshot, replayedExistingAttempt: true);
                }

                var head = await ReadCurrentLeafAsync(
                    connection,
                    transaction,
                    captured.Scope,
                    token).ConfigureAwait(false);
                if (migrationPage is not null)
                {
                    var rejection = await ValidateLegacyMigrationContinuationAsync(
                        connection,
                        transaction,
                        migrationPage,
                        head,
                        token).ConfigureAwait(false);
                    if (rejection is not null)
                        return RejectedMigrationCommit(rejection);
                }
                var collision = await HasAcceptedIdentityCollisionAsync(
                    connection,
                    transaction,
                    captured,
                    token).ConfigureAwait(false);
                var exactlyContinues = ExactlyContinues(captured.Bundle, head);
                var disposition = !collision && exactlyContinues
                    ? CortexSyncCommitDisposition.Committed
                    : CortexSyncCommitDisposition.Conflict;
                var conflictDiagnosticCode = disposition == CortexSyncCommitDisposition.Conflict
                    ? ClassifyConflictDiagnostic(collision, exactlyContinues)
                    : null;
                var commitId = PostgreSqlSyncHashPolicy.CommitId(captured, attemptId, disposition);
                var storageRevision = PostgreSqlSyncHashPolicy.StorageRevision(
                    captured,
                    attemptId,
                    disposition);
                var committedAt = captured.ReceivedAt;
                var outcomes = CreateOutcomes(
                    captured.Bundle,
                    disposition,
                    conflictDiagnosticCode);
                var sourceHead = disposition == CortexSyncCommitDisposition.Committed
                    ? new PostgreSqlSyncSourceHead(
                        captured.Bundle.BaseServerRevision,
                        captured.Bundle.BundleId,
                        storageRevision,
                        captured.Bundle.LastSequence,
                        captured.Bundle.Events[^1].EventId,
                        captured.Bundle.Events[^1].EventHash)
                    : null;
                var durableState = new PostgreSqlSyncDurableState(
                    captured.Scope.TenantId,
                    captured.Scope.Namespace,
                    captured.Scope.StoreId,
                    captured.Scope.StoreEpoch,
                    captured.Scope.ProjectId,
                    attemptId,
                    captured.Bundle.BundleId,
                    captured.Bundle.BundleHash,
                    commitId,
                    storageRevision,
                    disposition,
                    captured.ReceivedAt,
                    committedAt,
                    outcomes,
                    sourceHead);
                var durableStateHash = PostgreSqlSyncHashPolicy.DurableStateHash(durableState);

                await InsertAttemptAsync(
                    connection,
                    transaction,
                    captured,
                    attemptId,
                    disposition,
                    commitId,
                    storageRevision,
                    committedAt,
                    durableStateHash,
                    outcomes,
                    sourceHead,
                    migrationPage,
                    token).ConfigureAwait(false);
                await ForceDeferredConstraintsAsync(connection, transaction, token).ConfigureAwait(false);

                var reread = await ReadStoredCommitAsync(
                    connection,
                    transaction,
                    captured.Scope,
                    attemptId,
                    token).ConfigureAwait(false) ??
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_commit_reread_missing",
                        captured.Scope,
                        captured.Bundle.BundleId);
                EnsureExactAttempt(reread, captured, migrationPage);
                if (!string.Equals(reread.CommitId, commitId, StringComparison.Ordinal) ||
                    !string.Equals(reread.StorageRevision, storageRevision, StringComparison.Ordinal) ||
                    reread.Disposition != disposition ||
                    !string.Equals(reread.DurableStateHash, durableStateHash, StringComparison.Ordinal))
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_commit_reread_mismatch",
                        captured.Scope,
                        captured.Bundle.BundleId);
                }
                return AcceptedMigrationCommit(reread.Snapshot, replayedExistingAttempt: false);
            },
            cancellationToken);
    }

    public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken)
    {
        ValidateReadBackRequest(request, cancellationToken);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromSync(request.Scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var attemptId = await FindAttemptByCommitAsync(
                    connection,
                    transaction,
                    request.Scope,
                    request.BundleId,
                    request.BundleHash,
                    request.CommitId,
                    request.StorageRevision,
                    token).ConfigureAwait(false);
                var stored = attemptId is null
                    ? null
                    : await ReadStoredCommitAsync(
                        connection,
                        transaction,
                        request.Scope,
                        attemptId,
                        token).ConfigureAwait(false);
                if (stored is null ||
                    !string.Equals(stored.Bundle.BundleId, request.BundleId, StringComparison.Ordinal) ||
                    !string.Equals(stored.Bundle.BundleHash, request.BundleHash, StringComparison.Ordinal) ||
                    !string.Equals(stored.CommitId, request.CommitId, StringComparison.Ordinal) ||
                    !string.Equals(stored.StorageRevision, request.StorageRevision, StringComparison.Ordinal))
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_readback_missing",
                        request.Scope,
                        request.BundleId);
                }

                var readBackAt = PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(
                    _timeProvider.GetUtcNow());
                if (readBackAt < stored.CommittedAt)
                    readBackAt = stored.CommittedAt;
                return new CortexSyncReadBackSnapshot(
                    request.Scope.TenantId,
                    request.Scope.Namespace,
                    request.Scope.StoreId,
                    request.Scope.StoreEpoch,
                    request.Scope.ProjectId,
                    stored.Bundle.BundleId,
                    stored.Bundle.BundleHash,
                    stored.CommitId,
                    stored.StorageRevision,
                    stored.Bundle.Events.Count,
                    stored.Outcomes.Count,
                    readBackAt,
                    stored.DurableStateHash);
            },
            cancellationToken);
    }

    public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken)
    {
        ValidateReceiptAppendRequest(request, cancellationToken);
        var scope = request.Scope;
        var receipt = request.Receipt;
        var proof = PostgreSqlSyncCanonicalCodec.DecodeBase64Url(
            receipt.Authentication.ProofBase64Url,
            scope,
            receipt.ReceiptId);
        var extensions = CortexCanonicalJson.Serialize(receipt.Extensions);
        var recordedAt = PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(
            _timeProvider.GetUtcNow());
        if (recordedAt < receipt.Reconciliation.ReadBackAt!.Value)
            recordedAt = PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(
                receipt.Reconciliation.ReadBackAt.Value);

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromSync(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await AcquireScopeLockAsync(connection, transaction, scope, token).ConfigureAwait(false);
                var attemptId = await FindAttemptByCommitAsync(
                    connection,
                    transaction,
                    scope,
                    receipt.BundleId,
                    receipt.BundleHash,
                    receipt.Durability.CommitId,
                    receipt.Durability.StorageRevision,
                    token).ConfigureAwait(false);
                var commit = attemptId is null
                    ? null
                    : await ReadStoredCommitAsync(
                        connection,
                        transaction,
                        scope,
                        attemptId,
                        token).ConfigureAwait(false);
                if (commit is null || !ReceiptMatchesCommit(receipt, commit))
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_receipt_commit_mismatch",
                        scope,
                        receipt.ReceiptId);
                }

                var affected = await InsertReceiptAsync(
                    connection,
                    transaction,
                    request,
                    attemptId!,
                    proof,
                    extensions,
                    recordedAt,
                    token).ConfigureAwait(false);
                if (affected is not (0 or 1))
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_receipt_insert_count_invalid",
                        scope,
                        receipt.ReceiptId);
                }
                await ForceDeferredConstraintsAsync(connection, transaction, token).ConfigureAwait(false);

                var stored = await FindStoredReceiptByCommitAsync(
                    connection,
                    transaction,
                    scope,
                    receipt.BundleId,
                    receipt.BundleHash,
                    receipt.Durability.CommitId,
                    receipt.Durability.StorageRevision,
                    token).ConfigureAwait(false);
                if (stored is null || !StoredReceiptEquals(stored, request))
                {
                    return new CortexSyncReceiptAppendSnapshot(
                        CortexSyncReceiptAppendDisposition.Conflict,
                        scope.TenantId,
                        scope.Namespace,
                        scope.StoreId,
                        scope.StoreEpoch,
                        scope.ProjectId,
                        receipt.BundleId,
                        receipt.ReceiptId,
                        receipt.ReceiptHash,
                        request.CanonicalSha256,
                        recordedAt);
                }

                return new CortexSyncReceiptAppendSnapshot(
                    affected == 1
                        ? CortexSyncReceiptAppendDisposition.Appended
                        : CortexSyncReceiptAppendDisposition.Existing,
                    scope.TenantId,
                    scope.Namespace,
                    scope.StoreId,
                    scope.StoreEpoch,
                    scope.ProjectId,
                    receipt.BundleId,
                    stored.ReceiptId,
                    stored.ReceiptHash,
                    stored.CanonicalSha256,
                    stored.RecordedAt);
            },
            cancellationToken);
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken)
    {
        ValidateReceiptLookupRequest(request, cancellationToken);
        return _transactions.ExecuteAsync<CortexSyncReceiptReadBackSnapshot?>(
            PostgreSqlTransactionContext.FromSync(request.Scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var value = await FindStoredReceiptByCommitAsync(
                    connection,
                    transaction,
                    request.Scope,
                    request.BundleId,
                    request.BundleHash,
                    request.CommitId,
                    request.StorageRevision,
                    token).ConfigureAwait(false);
                if (value is null)
                    return null;
                return ToReceiptReadBack(request.Scope, request.BundleId, value);
            },
            cancellationToken);
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        ValidateReceiptReadRequest(request, cancellationToken);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromSync(request.Scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var value = await ReadStoredReceiptAsync(
                    connection,
                    transaction,
                    request,
                    token).ConfigureAwait(false) ??
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_receipt_readback_missing",
                        request.Scope,
                        request.ReceiptId);
                return ToReceiptReadBack(request.Scope, request.BundleId, value);
            },
            cancellationToken);
    }

    public ValueTask<CortexSyncPublishedJournalHead?> FindLatestPublishedAsync(
        CortexSyncPublishedHeadLookup request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_published_head_scope_invalid",
                request.Scope,
                request.Scope.ProjectId);
        }
        return FindPublishedCoreAsync(
            request.Scope,
            bundleId: null,
            bundleHash: null,
            cancellationToken);
    }

    public ValueTask<CortexSyncPublishedJournalHead?> FindPublishedAsync(
        CortexSyncPublishedBundleLookup request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.BundleId) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(request.BundleHash))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_published_bundle_lookup_invalid",
                request.Scope,
                request.BundleId);
        }
        return FindPublishedCoreAsync(
            request.Scope,
            request.BundleId,
            request.BundleHash,
            cancellationToken);
    }

    private ValueTask<CortexSyncPublishedJournalHead?> FindPublishedCoreAsync(
        CortexSyncRepositoryScope scope,
        string? bundleId,
        string? bundleHash,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync<CortexSyncPublishedJournalHead?>(
            PostgreSqlTransactionContext.FromSync(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var attemptId = bundleId is null
                    ? await FindLatestPublishedAttemptAsync(
                        connection,
                        transaction,
                        scope,
                        token).ConfigureAwait(false)
                    : await FindExactPublishedAttemptAsync(
                        connection,
                        transaction,
                        scope,
                        bundleId,
                        bundleHash!,
                        token).ConfigureAwait(false);
                if (attemptId is null)
                    return null;

                var stored = await ReadStoredCommitAsync(
                    connection,
                    transaction,
                    scope,
                    attemptId,
                    token).ConfigureAwait(false) ??
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_published_head_commit_missing",
                        scope,
                        attemptId);
                if (stored.Disposition != CortexSyncCommitDisposition.Committed)
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_published_head_commit_invalid",
                        scope,
                        stored.Bundle.BundleId);
                }

                var receiptRow = await FindStoredReceiptByCommitAsync(
                    connection,
                    transaction,
                    scope,
                    stored.Bundle.BundleId,
                    stored.Bundle.BundleHash,
                    stored.CommitId,
                    stored.StorageRevision,
                    token).ConfigureAwait(false) ??
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_published_head_receipt_missing",
                        scope,
                        stored.Bundle.BundleId);
                var receipt = PostgreSqlSyncCanonicalCodec.DecodeReceipt(
                    receiptRow.Canonical,
                    receiptRow.CanonicalSha256,
                    scope,
                    receiptRow.ReceiptId);
                if (!ReceiptMatchesCommit(receipt, stored) ||
                    receipt.Reconciliation.State != "reconciled" ||
                    receipt.Reconciliation.MatchedEventCount != receipt.Reconciliation.SourceEventCount)
                {
                    throw PostgreSqlSyncCanonicalCodec.Failure(
                        "cortex_postgresql_sync_published_head_receipt_invalid",
                        scope,
                        receipt.ReceiptId);
                }

                return new CortexSyncPublishedJournalHead(
                    scope,
                    stored.Bundle,
                    stored.Snapshot,
                    receipt,
                    receiptRow.RecordedAt);
            },
            cancellationToken);

    private static async ValueTask<string?> ValidateLegacyMigrationContinuationAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedMigrationPage captured,
        PostgreSqlCurrentSourceHead? head,
        CancellationToken cancellationToken)
    {
        var page = captured.Page;
        var scope = captured.Bundle.Scope;
        if (!ExactlyContinues(page.Bundle, head))
            return "cortex_sync_migration_cursor_stale_or_forked";

        if (page.CurrentCursor.PageNumber == 1)
        {
            if (!CortexSyncContractPolicy.IsCanonicalInitialLegacyMigrationCursor(page.CurrentCursor))
                return "cortex_sync_migration_initial_cursor_invalid";
            if (await HasAcceptedMigrationRegistrationAsync(
                    connection,
                    transaction,
                    scope,
                    page.CurrentCursor.MigrationId,
                    cancellationToken).ConfigureAwait(false))
            {
                return "cortex_sync_migration_restart_conflict";
            }
            return null;
        }

        if (head is null)
            return "cortex_sync_migration_predecessor_missing";
        var predecessor = await ReadCurrentMigrationPredecessorAsync(
            connection,
            transaction,
            scope,
            head,
            cancellationToken).ConfigureAwait(false);
        if (predecessor is null)
            return "cortex_sync_migration_predecessor_unreconciled";

        var previousPage = predecessor.Page;
        if (previousPage.NextCursor.Completed ||
            !CortexCanonicalJson.Serialize(previousPage.NextCursor).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(page.CurrentCursor)) ||
            !CortexCanonicalJson.Serialize(previousPage.SourceInventory).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(page.SourceInventory)) ||
            !string.Equals(page.Bundle.BaseServerRevision, predecessor.StorageRevision, StringComparison.Ordinal))
        {
            return "cortex_sync_migration_cursor_not_server_successor";
        }

        return null;
    }

    private static CortexLegacyMigrationPageCommitSnapshot AcceptedMigrationCommit(
        CortexSyncCommitSnapshot commit,
        bool replayedExistingAttempt) =>
        new(
            CortexLegacyMigrationPageCommitDisposition.Accepted,
            commit,
            diagnosticCode: null,
            replayedExistingAttempt);

    private static CortexLegacyMigrationPageCommitSnapshot RejectedMigrationCommit(
        string diagnosticCode) =>
        new(
            CortexLegacyMigrationPageCommitDisposition.Rejected,
            commit: null,
            diagnosticCode: diagnosticCode);

    private static bool ExactlyContinues(
        CortexOfflineBundleV1 bundle,
        PostgreSqlCurrentSourceHead? head)
    {
        if (head is null)
        {
            return bundle.BaseServerRevision is null &&
                bundle.FirstSequence == 1 &&
                string.Equals(bundle.PreviousBundleHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal) &&
                string.Equals(bundle.PriorEventHash, CortexSyncContractIds.EmptySha256, StringComparison.Ordinal);
        }

        return string.Equals(bundle.BaseServerRevision, head.StorageRevision, StringComparison.Ordinal) &&
            bundle.FirstSequence == head.SourceSequence + 1 &&
            string.Equals(bundle.PreviousBundleHash, head.BundleHash, StringComparison.Ordinal) &&
            string.Equals(bundle.PriorEventHash, head.EventHash, StringComparison.Ordinal);
    }

    private static IReadOnlyList<CortexSyncReceiptOutcomeV1> CreateOutcomes(
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitDisposition disposition,
        string? conflictDiagnosticCode) =>
        // "applied" is the v1 receipt term for acceptance into this immutable journal. A later
        // knowledge-materialization pipeline remains a separate, independently authorized step.
        Array.AsReadOnly(bundle.Events.Select(item => new CortexSyncReceiptOutcomeV1(
            item.Sequence,
            item.EventId,
            item.EventHash,
            disposition == CortexSyncCommitDisposition.Committed
                ? CortexSyncDispositionIds.Applied
                : CortexSyncDispositionIds.Conflict,
            durable: true,
            serverEntityId: null,
            serverRevisionId: null,
            diagnosticCode: disposition == CortexSyncCommitDisposition.Conflict
                ? conflictDiagnosticCode
                : null)).ToArray());

    internal static string ClassifyConflictDiagnostic(
        bool acceptedIdentityCollision,
        bool exactlyContinuesSourceHead)
    {
        if (acceptedIdentityCollision)
            return IdentityConflictCode;
        if (!exactlyContinuesSourceHead)
            return HeadConflictCode;
        throw new ArgumentException("A conflict diagnostic requires conflicting durable evidence.");
    }

    private static bool ReceiptMatchesCommit(
        CortexSyncReceiptV1 receipt,
        PostgreSqlStoredSyncCommit commit) =>
        CortexSyncContractPolicy.HasValidSemanticHash(receipt) &&
        string.Equals(receipt.StoreId, commit.Scope.StoreId, StringComparison.Ordinal) &&
        string.Equals(receipt.StoreEpoch, commit.Scope.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(receipt.ProjectId, commit.Scope.ProjectId, StringComparison.Ordinal) &&
        string.Equals(receipt.BundleId, commit.Bundle.BundleId, StringComparison.Ordinal) &&
        string.Equals(receipt.BundleHash, commit.Bundle.BundleHash, StringComparison.Ordinal) &&
        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(receipt.ReceivedAt) == commit.ReceivedAt &&
        string.Equals(receipt.Durability.CommitId, commit.CommitId, StringComparison.Ordinal) &&
        string.Equals(receipt.Durability.StorageRevision, commit.StorageRevision, StringComparison.Ordinal) &&
        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(receipt.Durability.CommittedAt) == commit.CommittedAt &&
        receipt.Reconciliation.State == "reconciled" &&
        receipt.Reconciliation.SourceEventCount == commit.Outcomes.Count &&
        receipt.Reconciliation.MatchedEventCount == commit.Outcomes.Count &&
        string.Equals(receipt.Reconciliation.ReadBackHash, commit.DurableStateHash, StringComparison.Ordinal) &&
        receipt.Outcomes.Count == commit.Outcomes.Count &&
        receipt.Outcomes.Zip(commit.Outcomes, SameOutcome).All(value => value);

    private static bool SameOutcome(
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

    private static void EnsureExactAttempt(
        PostgreSqlStoredSyncCommit stored,
        PostgreSqlSyncCapturedBundle requested,
        PostgreSqlSyncCapturedMigrationPage? migrationPage)
    {
        EnsureExactBundleAttempt(stored, requested);
        if (!SameMigrationPage(stored.MigrationPage, migrationPage))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_attempt_identity_collision",
                requested.Scope,
                requested.Bundle.BundleId);
        }
    }

    private static void EnsureExactBundleAttempt(
        PostgreSqlStoredSyncCommit stored,
        PostgreSqlSyncCapturedBundle requested)
    {
        if (!string.Equals(stored.Scope.TenantId, requested.Scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(stored.Scope.Namespace, requested.Scope.Namespace, StringComparison.Ordinal) ||
            !string.Equals(stored.Scope.StoreId, requested.Scope.StoreId, StringComparison.Ordinal) ||
            !string.Equals(stored.Scope.StoreEpoch, requested.Scope.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(stored.Scope.ProjectId, requested.Scope.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(stored.BundleCanonicalSha256, requested.CanonicalSha256, StringComparison.Ordinal) ||
            !stored.BundleCanonical.AsSpan().SequenceEqual(requested.Canonical))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_attempt_identity_collision",
                requested.Scope,
                requested.Bundle.BundleId);
        }
    }

    private static bool SameMigrationPage(
        PostgreSqlStoredMigrationPage? stored,
        PostgreSqlSyncCapturedMigrationPage? requested) =>
        (stored is null) == (requested is null) &&
        (stored is null ||
            string.Equals(stored.CanonicalSha256, requested!.CanonicalSha256, StringComparison.Ordinal) &&
            stored.Canonical.AsSpan().SequenceEqual(requested.Canonical));

    private static CortexSyncReceiptReadBackSnapshot ToReceiptReadBack(
        CortexSyncRepositoryScope scope,
        string bundleId,
        PostgreSqlStoredReceipt value) =>
        new(
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId,
            bundleId,
            value.ReceiptId,
            value.RecordedAt,
            value.Canonical);

    private static bool StoredReceiptEquals(
        PostgreSqlStoredReceipt stored,
        CortexSyncReceiptAppendRequest request) =>
        string.Equals(stored.ReceiptId, request.Receipt.ReceiptId, StringComparison.Ordinal) &&
        string.Equals(stored.ReceiptHash, request.Receipt.ReceiptHash, StringComparison.Ordinal) &&
        string.Equals(stored.CanonicalSha256, request.CanonicalSha256, StringComparison.Ordinal) &&
        stored.Canonical.AsSpan().SequenceEqual(request.CanonicalReceipt.Span);

    private static void ValidateReadBackRequest(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.BundleId) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(request.BundleHash) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.CommitId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.StorageRevision))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_readback_request_invalid",
                request.Scope,
                request.BundleId);
        }
    }

    private static void ValidateReceiptAppendRequest(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = request.Receipt;
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope) ||
            !CortexSyncContractPolicy.HasValidSemanticHash(receipt) ||
            !string.Equals(receipt.StoreId, request.Scope.StoreId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreEpoch, request.Scope.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProjectId, request.Scope.ProjectId, StringComparison.Ordinal) ||
            receipt.Reconciliation.State != "reconciled" ||
            receipt.Reconciliation.ReadBackAt is null ||
            receipt.Reconciliation.ReadBackHash is null ||
            receipt.Reconciliation.ReadBackAt.Value < receipt.Durability.CommittedAt)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_receipt_append_request_invalid",
                request.Scope,
                receipt.ReceiptId);
        }
    }

    private static void ValidateReceiptLookupRequest(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.BundleId) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(request.BundleHash) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.CommitId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.StorageRevision))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_receipt_lookup_request_invalid",
                request.Scope,
                request.BundleId);
        }
    }

    private static void ValidateReceiptReadRequest(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(request.Scope) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.BundleId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(request.ReceiptId) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(request.ReceiptHash) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(request.CanonicalSha256))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_receipt_read_request_invalid",
                request.Scope,
                request.ReceiptId);
        }
    }
}
