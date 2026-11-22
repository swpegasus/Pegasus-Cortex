using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.Sync.Client.ChangeSources;
using Pegasus.Cortex.Sync.Client.Contracts;
using Pegasus.Cortex.Sync.Client.Persistence;

namespace Pegasus.Cortex.Sync.Client.Orchestration;

/// <summary>
/// One deterministic reconnect attempt. It always journals a new local authority state before
/// remote inspection/upload and uses the last acknowledged authority hash as the merge base.
/// </summary>
public sealed class CortexSyncCoordinator
{
    private readonly ICortexLocalSyncChangeSource _source;
    private readonly ICortexSyncClientJournal _journal;
    private readonly ICortexSyncCursorStore _cursorStore;
    private readonly ICortexSyncPendingAttemptStoreV1_1 _attemptStore;
    private readonly ICortexSyncClientTransportV1_1 _transport;
    private readonly CortexSyncClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public CortexSyncCoordinator(
        ICortexLocalSyncChangeSource source,
        ICortexSyncClientJournal journal,
        ICortexSyncCursorStore cursorStore,
        ICortexSyncPendingAttemptStoreV1_1 attemptStore,
        ICortexSyncClientTransportV1_1 transport,
        CortexSyncClientOptions options,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        _attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(options);
    }

    public async ValueTask<CortexSyncRunResult> SyncNowAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recovery = await _journal.RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (!recovery.Ready)
            return Rejected("sync_local_journal_not_ready", "The protected offline journal did not recover cleanly.");

        CortexLocalSyncChange change;
        CortexSyncCursorV1? cursor;
        CortexSyncJournalSnapshot before;
        CortexSyncPendingSubmitAttempt? pendingAttempt;
        try
        {
            change = await _source.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            ValidateChange(change, _timeProvider.GetUtcNow());
            cursor = await _cursorStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            before = await _journal.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            ValidateLocalJournal(before, cursor, change);
            pendingAttempt = await _attemptStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (pendingAttempt is not null)
                ValidatePendingAttempt(pendingAttempt, before, cursor, change);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Rejected("sync_local_state_invalid", "Local sync state failed validation: " + exception.Message);
        }

        if (pendingAttempt is not null)
        {
            if (IsDurablyAcknowledged(pendingAttempt, before, cursor))
            {
                try
                {
                    await _attemptStore.RetireAsync(pendingAttempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
                {
                    return Rejected(
                        "sync_pending_attempt_retire_failed",
                        "The already acknowledged submit attempt remains preserved for operator repair: " + exception.Message);
                }
                var leaf = DecodeSubmittedSnapshot(pendingAttempt.Request.Bundle.Events[^1]);
                return new CortexSyncRunResult(
                    CortexSyncRunDisposition.Synchronized,
                    "sync_pending_attempt_retired",
                    "The durable receipt and cursor were already present; the preserved submit attempt was retired.",
                    leaf.AuthorityHash);
            }

            try
            {
                await ProveDurableAttemptAsync(pendingAttempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
            {
                return Rejected(
                    "sync_pending_attempt_write_failed",
                    "The recovered submit request was not re-proven crash durable; no remote call was attempted: " +
                    exception.Message);
            }

            return await ReplayPendingAttemptAsync(
                    pendingAttempt,
                    cursor,
                    before,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var localPayloadHash = CortexCanonicalJson.Sha256(change.Payload.Span);
        var localChanged = cursor is null ||
            !string.Equals(change.AuthorityHash, cursor.LastAcknowledgedAuthorityHash, StringComparison.Ordinal) ||
            !string.Equals(localPayloadHash, cursor.LastAcknowledgedPayloadHash, StringComparison.Ordinal);
        if (localChanged)
        {
            var appended = await _journal.AppendAsync(change, cancellationToken).ConfigureAwait(false);
            if (appended.Disposition is not (JournalMutationDisposition.Added or JournalMutationDisposition.Existing))
            {
                return Rejected(
                    appended.Diagnostic?.Code ?? "sync_local_journal_append_failed",
                    "The local change was not durably journaled; upload was not attempted.");
            }
        }

        CortexSyncCapabilitiesV1 capabilities;
        CortexSyncRemoteHead remote;
        try
        {
            capabilities = await _transport.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!CortexSyncContractPolicy.HasValidHash(capabilities) ||
                !capabilities.AcceptedPayloadContractIds.Contains(change.PayloadContractId, StringComparer.Ordinal) ||
                !capabilities.SupportedContractIds.Contains(CortexSyncContractIds.OfflineBundle, StringComparer.Ordinal) ||
                !capabilities.SupportedContractIds.Contains(CortexSyncContractIds.SyncReceipt, StringComparer.Ordinal) ||
                !RequiredGatewayContracts.All(item =>
                    capabilities.SupportedContractIds.Contains(item, StringComparer.Ordinal)) ||
                change.Payload.Length > capabilities.Limits.MaximumEventPayloadBytes ||
                !RequiredCapabilities.All(item => capabilities.SupportedCapabilityIds.Contains(item, StringComparer.Ordinal)))
            {
                return Rejected("sync_remote_capabilities_incompatible", "The authenticated server capability set does not accept this payload contract.");
            }
            remote = await _transport.GetHeadAsync(
                new CortexSyncHeadRequest(
                    change.ProjectLinkId,
                    before.State.StoreId,
                    before.State.StoreEpoch,
                    change.ProjectId),
                cancellationToken).ConfigureAwait(false);
            ValidateRemoteHead(
                remote,
                change.ProjectLinkId,
                before.State.StoreId,
                before.State.StoreEpoch,
                change.ProjectId);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Rejected(
                "sync_remote_authorization_failed",
                "The authenticated remote rejected sync authority: " + exception.Message);
        }
        catch (Exception exception) when (IsExpectedRemoteFailure(exception, cancellationToken))
        {
            return RemoteRejected(
                "sync_remote_probe_failed",
                "The authenticated remote head could not be validated: ",
                exception);
        }

        var reconciliation = Reconcile(cursor, change, localPayloadHash, remote);
        if (reconciliation is not null)
            return reconciliation;

        // No cursor means this is a first acknowledged synchronization even if an imported
        // remote head already has the same authority hash. Submitting idempotently gives the
        // local journal a durable typed receipt instead of silently trusting a probe.
        if (!localChanged && cursor is not null)
        {
            if (remote.HighWaterSequence > cursor.RemoteHighWaterSequence)
            {
                try
                {
                    await SaveCursorAsync(
                            cursor,
                            before.State,
                            remote,
                            change.AuthorityHash,
                            localPayloadHash,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    return Rejected("sync_cursor_write_failed", "The advanced remote head could not be checkpointed locally: " + exception.Message);
                }
            }
            return new CortexSyncRunResult(
                CortexSyncRunDisposition.Unchanged,
                "sync_unchanged",
                "Local and server authority and retained-payload hashes match the last acknowledged ancestor.",
                change.AuthorityHash);
        }

        var afterAppend = await _journal.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateLocalJournal(afterAppend, cursor, change);
        }
        catch (InvalidDataException exception)
        {
            return Rejected("sync_local_journal_gap", exception.Message);
        }

        var now = _timeProvider.GetUtcNow();
        var createdAt = now < change.CapturedAt ? change.CapturedAt : now;
        var effectiveMaximumEvents = Math.Min(
            _options.MaximumEventsPerBundle,
            Math.Min(
                capabilities.Limits.MaximumEventsPerBundle,
                capabilities.Limits.MaximumReceiptOutcomes));
        JournalExportResult? export = null;
        string? exportLimitFailure = null;
        for (var limit = effectiveMaximumEvents; limit >= 1; limit--)
        {
            export = await _journal.ExportPendingAsync(
                new JournalExportRequest(
                    $"bundle-{change.AuthorityHash[..24]}-{afterAppend.State.HighWaterSequence}",
                    _options.TenantIdHint,
                    _options.NamespaceHint,
                    _options.ObservedPolicyRevision,
                    remote.Exists ? remote.ServerRevision : null,
                    remote.Exists ? remote.LastBundleHash : CortexSyncClientContractIds.EmptySha256,
                    remote.Exists ? remote.LastEventHash : CortexSyncClientContractIds.EmptySha256,
                    createdAt,
                    limit),
                cancellationToken).ConfigureAwait(false);
            if (!export.Accepted || export.Bundle is null)
            {
                return Rejected(
                    export.Diagnostic?.Code ?? "sync_local_export_failed",
                    "The protected journal could not produce a contiguous pending bundle.");
            }
            try
            {
                ValidateAdvertisedLimits(export.Bundle, capabilities.Limits);
                break;
            }
            catch (InvalidDataException exception)
            {
                exportLimitFailure = exception.Message;
                if (limit == 1)
                {
                    export = null;
                    break;
                }
                // Retry with one fewer contiguous event. This is bounded by the configured v1
                // event maximum and happens before an attempt is persisted or the network is used.
            }
        }
        if (export?.Bundle is null)
        {
            return Rejected(
                "sync_exported_payload_invalid",
                "Even one pending event exceeds the authenticated server limits: " + exportLimitFailure);
        }

        SubmittedSnapshotEvidence submittedEvidence;
        try
        {
            ValidateAdvertisedLimits(export.Bundle, capabilities.Limits);
            submittedEvidence = DecodeSubmittedSnapshot(export.Bundle.Events[^1]);
            if (!string.Equals(submittedEvidence.ProjectLinkId, change.ProjectLinkId, StringComparison.Ordinal) ||
                !string.Equals(submittedEvidence.ProjectId, change.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exported backlog page belongs to another project correlation scope.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return Rejected(
                "sync_exported_payload_invalid",
                "The protected journal export is outside the advertised server contract: " + exception.Message);
        }

        CortexSyncPendingSubmitAttempt attempt;
        try
        {
            var request = CortexSyncContractPolicy.Seal(
                new CortexSyncSubmitRequest(change.ProjectLinkId, export.Bundle));
            attempt = CortexSyncPendingAttemptPolicy.Create(request, remote);
            await ProveDurableAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_pending_attempt_write_failed",
                "The exact canonical submit request was not durably preserved; no upload was attempted: " + exception.Message);
        }

        CortexSyncSubmitResult submitted;
        try
        {
            submitted = await _transport.SubmitAsync(
                    attempt.Request,
                    attempt.CanonicalRequestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateSubmit(submitted, export.Bundle, submittedEvidence, capabilities);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Rejected(
                "sync_remote_authorization_failed",
                "The authenticated remote rejected sync authority: " + exception.Message);
        }
        catch (Exception exception) when (IsExpectedRemoteFailure(exception, cancellationToken))
        {
            return RemoteRejected(
                "sync_submit_validation_failed",
                "The durable submit/read-back result was rejected; the exact request remains pending: ",
                exception);
        }

        JournalTypedAcknowledgmentResult acknowledged;
        try
        {
            acknowledged = await _journal.RecordAcknowledgmentAsync(
                export.Bundle,
                submitted.Receipt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_receipt_write_failed",
                "The authenticated result was not durably acknowledged locally; the exact request remains pending: " + exception.Message);
        }
        if (acknowledged.Disposition is not (JournalMutationDisposition.Added or JournalMutationDisposition.Existing) ||
            !acknowledged.Uploaded || !acknowledged.Reconciled)
        {
            return Rejected(
                acknowledged.Diagnostic?.Code ?? "sync_receipt_not_reconciled",
                "The authenticated receipt was not durably reconciled locally.");
        }

        var acknowledgedState = await _journal.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCursorAsync(
                cursor,
                acknowledgedState.State,
                submitted.Head,
                submittedEvidence.AuthorityHash,
                submittedEvidence.PayloadHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Rejected(
                "sync_cursor_write_failed",
                "Remote storage and the typed local receipt are durable, but the cursor checkpoint failed: " + exception.Message);
        }
        try
        {
            await _attemptStore.RetireAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_pending_attempt_retire_failed",
                "The receipt and cursor are durable, but the submit attempt remains preserved for operator repair: " + exception.Message);
        }
        return new CortexSyncRunResult(
            CortexSyncRunDisposition.Synchronized,
            "sync_reconciled",
            "The local change was journaled, stored remotely, read back, and durably acknowledged.",
            submittedEvidence.AuthorityHash,
            submitted.Receipt.ReceiptId);
    }

    private async ValueTask<CortexSyncRunResult> ReplayPendingAttemptAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CortexSyncCursorV1? cursor,
        CortexSyncJournalSnapshot before,
        CancellationToken cancellationToken)
    {
        var bundle = attempt.Request.Bundle;
        var leaf = DecodeSubmittedSnapshot(bundle.Events[^1]);
        CortexSyncCapabilitiesV1 capabilities;
        CortexSyncRemoteHead remote;
        try
        {
            capabilities = await _transport.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!PendingAttemptFitsCapabilities(attempt, capabilities))
            {
                return Rejected(
                    "sync_pending_attempt_incompatible",
                    "The preserved request is outside the server's current authenticated capability limits.");
            }
            remote = await _transport.GetHeadAsync(
                new CortexSyncHeadRequest(
                    attempt.Evidence.ProjectLinkId,
                    attempt.Evidence.StoreId,
                    attempt.Evidence.StoreEpoch,
                    attempt.Evidence.ProjectId),
                cancellationToken).ConfigureAwait(false);
            ValidateRemoteHead(
                remote,
                attempt.Evidence.ProjectLinkId,
                attempt.Evidence.StoreId,
                attempt.Evidence.StoreEpoch,
                attempt.Evidence.ProjectId);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Rejected(
                "sync_remote_authorization_failed",
                "The authenticated remote rejected replay authority: " + exception.Message);
        }
        catch (Exception exception) when (IsExpectedRemoteFailure(exception, cancellationToken))
        {
            return RemoteRejected(
                "sync_remote_probe_failed",
                "The authenticated remote head could not be validated while the exact request remains pending: ",
                exception);
        }

        var remoteDisposition = ReconcilePendingRemote(attempt, leaf, remote);
        if (remoteDisposition is not null)
            return remoteDisposition;

        CortexSyncSubmitResult submitted;
        try
        {
            submitted = await _transport.SubmitAsync(
                    attempt.Request,
                    attempt.CanonicalRequestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateSubmit(submitted, bundle, leaf, capabilities);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Rejected(
                "sync_remote_authorization_failed",
                "The authenticated remote rejected replay authority: " + exception.Message);
        }
        catch (Exception exception) when (IsExpectedRemoteFailure(exception, cancellationToken))
        {
            return RemoteRejected(
                "sync_submit_validation_failed",
                "The replay result was unavailable or invalid; the byte-identical request remains pending: ",
                exception);
        }

        JournalTypedAcknowledgmentResult acknowledged;
        try
        {
            acknowledged = await _journal.RecordAcknowledgmentAsync(
                bundle,
                submitted.Receipt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_receipt_write_failed",
                "The replay receipt was not durably acknowledged locally; the exact request remains pending: " + exception.Message);
        }
        if (acknowledged.Disposition is not (JournalMutationDisposition.Added or JournalMutationDisposition.Existing) ||
            !acknowledged.Uploaded || !acknowledged.Reconciled)
        {
            return Rejected(
                acknowledged.Diagnostic?.Code ?? "sync_receipt_not_reconciled",
                "The replay receipt was not durably reconciled; the exact request remains pending.");
        }

        CortexSyncJournalSnapshot acknowledgedState;
        try
        {
            acknowledgedState = await _journal.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            ValidateAttemptJournalEntries(attempt, acknowledgedState);
            await SaveCursorAsync(
                cursor,
                acknowledgedState.State,
                submitted.Head,
                leaf.AuthorityHash,
                leaf.PayloadHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_cursor_write_failed",
                "The replay receipt is durable, but its cursor checkpoint failed; the exact request remains pending: " + exception.Message);
        }

        try
        {
            await _attemptStore.RetireAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLocalPersistenceFailure(exception))
        {
            return Rejected(
                "sync_pending_attempt_retire_failed",
                "The replay receipt and cursor are durable, but its submit evidence remains active: " + exception.Message);
        }
        return new CortexSyncRunResult(
            CortexSyncRunDisposition.Synchronized,
            "sync_pending_attempt_reconciled",
            "The byte-identical preserved request was replayed and durably reconciled.",
            leaf.AuthorityHash,
            submitted.Receipt.ReceiptId);
    }

    private static CortexSyncRunResult? ReconcilePendingRemote(
        CortexSyncPendingSubmitAttempt attempt,
        SubmittedSnapshotEvidence leaf,
        CortexSyncRemoteHead remote)
    {
        var evidence = attempt.Evidence;
        if (remote.HighWaterSequence < evidence.RemoteParentHighWaterSequence)
        {
            return Rejected(
                "sync_remote_epoch_or_head_rollback",
                "The remote head rolled back below the preserved submit parent.");
        }

        var parentMatches = remote.Exists == evidence.RemoteParentExists &&
            remote.HighWaterSequence == evidence.RemoteParentHighWaterSequence &&
            string.Equals(remote.LastEventId, evidence.RemoteParentLastEventId, StringComparison.Ordinal) &&
            string.Equals(remote.LastEventHash, evidence.RemoteParentEventHash, StringComparison.Ordinal) &&
            string.Equals(remote.LastBundleId, evidence.RemoteParentLastBundleId, StringComparison.Ordinal) &&
            string.Equals(remote.LastBundleHash, evidence.RemoteParentBundleHash, StringComparison.Ordinal) &&
            string.Equals(remote.AuthorityHash, evidence.RemoteParentAuthorityHash, StringComparison.Ordinal) &&
            string.Equals(remote.RetainedPayloadHash, evidence.RemoteParentPayloadHash, StringComparison.Ordinal) &&
            string.Equals(remote.ServerRevision, evidence.RemoteParentServerRevision, StringComparison.Ordinal);
        var bundle = attempt.Request.Bundle;
        var appliedMatches = remote.Exists &&
            remote.HighWaterSequence == bundle.LastSequence &&
            string.Equals(remote.LastEventId, bundle.Events[^1].EventId, StringComparison.Ordinal) &&
            string.Equals(remote.LastEventHash, bundle.Events[^1].EventHash, StringComparison.Ordinal) &&
            string.Equals(remote.LastBundleId, bundle.BundleId, StringComparison.Ordinal) &&
            string.Equals(remote.LastBundleHash, bundle.BundleHash, StringComparison.Ordinal) &&
            string.Equals(remote.AuthorityHash, leaf.AuthorityHash, StringComparison.Ordinal) &&
            string.Equals(remote.RetainedPayloadHash, leaf.PayloadHash, StringComparison.Ordinal);
        if (parentMatches || appliedMatches)
            return null;

        if (remote.Exists && remote.HighWaterSequence > bundle.LastSequence)
        {
            // A later authenticated head may be a descendant of a request whose response was
            // lost. The head projection cannot prove ancestry by itself, so replay the exact
            // sealed bytes and require the server to return the historical receipt and read-back
            // head for this bundle. ValidateSubmit keeps the attempt active unless that exact
            // durable evidence is returned.
            return null;
        }
        return new CortexSyncRunResult(
            CortexSyncRunDisposition.RemoteDrift,
            "sync_pending_remote_drift",
            "The remote head is neither the preserved parent, the exact submitted bundle, nor a possible later descendant; replay stopped for operator action.",
            leaf.AuthorityHash);
    }

    private static bool PendingAttemptFitsCapabilities(
        CortexSyncPendingSubmitAttempt attempt,
        CortexSyncCapabilitiesV1 capabilities)
    {
        if (!CortexSyncContractPolicy.HasValidHash(capabilities) ||
            !capabilities.SupportedContractIds.Contains(CortexSyncContractIds.OfflineBundle, StringComparer.Ordinal) ||
            !capabilities.SupportedContractIds.Contains(CortexSyncContractIds.SyncReceipt, StringComparer.Ordinal) ||
            !RequiredGatewayContracts.All(item =>
                capabilities.SupportedContractIds.Contains(item, StringComparer.Ordinal)) ||
            !RequiredCapabilities.All(item =>
                capabilities.SupportedCapabilityIds.Contains(item, StringComparer.Ordinal)) ||
            attempt.Request.Bundle.Events.Any(item =>
                !capabilities.AcceptedPayloadContractIds.Contains(item.PayloadContractId, StringComparer.Ordinal)))
        {
            return false;
        }
        try
        {
            ValidateAdvertisedLimits(attempt.Request.Bundle, capabilities.Limits);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static CortexSyncRunResult? Reconcile(
        CortexSyncCursorV1? cursor,
        CortexLocalSyncChange change,
        string localPayloadHash,
        CortexSyncRemoteHead remote)
    {
        if (cursor is null)
        {
            if (!remote.Exists ||
                string.Equals(remote.AuthorityHash, change.AuthorityHash, StringComparison.Ordinal) &&
                string.Equals(remote.RetainedPayloadHash, localPayloadHash, StringComparison.Ordinal))
                return null;
            return new CortexSyncRunResult(
                CortexSyncRunDisposition.Conflict,
                "sync_common_ancestor_missing",
                "The server already has a different authority hash and no acknowledged local ancestor exists.",
                change.AuthorityHash);
        }

        if (!remote.Exists ||
            !string.Equals(remote.StoreEpoch, cursor.RemoteStoreEpoch, StringComparison.Ordinal) ||
            remote.HighWaterSequence < cursor.RemoteHighWaterSequence)
        {
            return Rejected(
                "sync_remote_epoch_or_head_rollback",
                "The remote epoch or high-water mark rolled back; synchronization failed closed.");
        }

        var localChanged = !string.Equals(
            change.AuthorityHash,
            cursor.LastAcknowledgedAuthorityHash,
            StringComparison.Ordinal) ||
            !string.Equals(
                localPayloadHash,
                cursor.LastAcknowledgedPayloadHash,
                StringComparison.Ordinal);
        var remoteChanged = !string.Equals(
            remote.AuthorityHash,
            cursor.LastAcknowledgedAuthorityHash,
            StringComparison.Ordinal) ||
            !string.Equals(
                remote.RetainedPayloadHash,
                cursor.LastAcknowledgedPayloadHash,
                StringComparison.Ordinal);
        var localMatchesRemote =
            string.Equals(change.AuthorityHash, remote.AuthorityHash, StringComparison.Ordinal) &&
            string.Equals(localPayloadHash, remote.RetainedPayloadHash, StringComparison.Ordinal);
        if (localMatchesRemote)
            return null;
        if (localChanged && remoteChanged)
        {
            return new CortexSyncRunResult(
                CortexSyncRunDisposition.Conflict,
                "sync_both_changed",
                "Local and server authority changed from the last acknowledged ancestor; neither side was overwritten.",
                change.AuthorityHash);
        }
        if (!localChanged && remoteChanged)
        {
            return new CortexSyncRunResult(
                CortexSyncRunDisposition.RemoteDrift,
                "sync_remote_drift",
                "The server changed while local authority did not; local project files were left untouched.",
                change.AuthorityHash);
        }
        return null;
    }

    private async ValueTask SaveCursorAsync(
        CortexSyncCursorV1? previous,
        OfflineJournalStateV1 local,
        CortexSyncRemoteHead remote,
        string authorityHash,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var candidate = new CortexSyncCursorV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncClientContractIds.Cursor,
            CortexSyncClientContractIds.Canonicalization,
            (previous?.Revision ?? 0) + 1,
            remote.ProjectId,
            remote.StoreId,
            local.StoreEpoch,
            local.HighWaterSequence,
            remote.StoreEpoch!,
            remote.HighWaterSequence,
            remote.LastEventHash,
            remote.LastBundleHash,
            remote.ServerRevision!,
            authorityHash,
            payloadHash,
            _timeProvider.GetUtcNow(),
            previous?.CursorHash ?? CortexSyncClientContractIds.EmptySha256,
            CortexSyncClientContractIds.EmptySha256);
        var sealedCursor = CortexSyncCursorPolicy.Seal(candidate);
        CortexSyncCursorPolicy.Validate(sealedCursor, previous);
        await _cursorStore.SaveAsync(sealedCursor, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLocalJournal(
        CortexSyncJournalSnapshot snapshot,
        CortexSyncCursorV1? cursor,
        CortexLocalSyncChange change)
    {
        if (!string.Equals(snapshot.State.ProjectId, change.ProjectId, StringComparison.Ordinal) ||
            snapshot.Entries.Count != snapshot.State.HighWaterSequence ||
            snapshot.Entries.Select(item => item.Sequence).Where((value, index) => value != index + 1L).Any())
        {
            throw new InvalidDataException("The local journal identity or sequence chain is incomplete.");
        }
        if (cursor is not null &&
            (!string.Equals(snapshot.State.StoreId, cursor.StoreId, StringComparison.Ordinal) ||
             !string.Equals(snapshot.State.StoreEpoch, cursor.LocalStoreEpoch, StringComparison.Ordinal) ||
             snapshot.State.HighWaterSequence < cursor.LocalHighWaterSequence))
        {
            throw new InvalidDataException("The local journal epoch or high-water mark rolled back.");
        }
    }

    private static void ValidatePendingAttempt(
        CortexSyncPendingSubmitAttempt attempt,
        CortexSyncJournalSnapshot snapshot,
        CortexSyncCursorV1? cursor,
        CortexLocalSyncChange current)
    {
        CortexSyncPendingAttemptPolicy.Validate(attempt);
        if (!string.Equals(attempt.Evidence.ProjectLinkId, current.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(attempt.Evidence.ProjectId, current.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(attempt.Evidence.ProjectId, snapshot.State.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(attempt.Evidence.StoreId, snapshot.State.StoreId, StringComparison.Ordinal) ||
            !string.Equals(attempt.Evidence.StoreEpoch, snapshot.State.StoreEpoch, StringComparison.Ordinal) ||
            attempt.Evidence.LastSequence > snapshot.State.HighWaterSequence)
        {
            throw new InvalidDataException(
                "The pending attempt does not match the authorized local project, store epoch, or journal head.");
        }
        ValidateAttemptJournalEntries(attempt, snapshot);

        if (cursor is null)
            return;
        if (!string.Equals(cursor.ProjectId, attempt.Evidence.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(cursor.StoreId, attempt.Evidence.StoreId, StringComparison.Ordinal) ||
            !string.Equals(cursor.LocalStoreEpoch, attempt.Evidence.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(cursor.RemoteStoreEpoch, attempt.Evidence.StoreEpoch, StringComparison.Ordinal) ||
            cursor.RemoteHighWaterSequence < attempt.Evidence.RemoteParentHighWaterSequence ||
            cursor.RemoteHighWaterSequence > attempt.Evidence.LastSequence ||
            cursor.RemoteHighWaterSequence > attempt.Evidence.RemoteParentHighWaterSequence &&
            cursor.RemoteHighWaterSequence < attempt.Evidence.LastSequence)
        {
            throw new InvalidDataException(
                "The durable cursor is outside the pending attempt's exact parent-to-leaf range.");
        }
    }

    private static void ValidateAttemptJournalEntries(
        CortexSyncPendingSubmitAttempt attempt,
        CortexSyncJournalSnapshot snapshot)
    {
        var events = attempt.Request.Bundle.Events;
        if (events.Count == 0 ||
            events[0].Sequence != attempt.Evidence.FirstSequence ||
            events[^1].Sequence != attempt.Evidence.LastSequence ||
            events.Where((item, index) =>
                    item.Sequence != attempt.Evidence.FirstSequence + index ||
                    !snapshot.Entries.Any(status =>
                        status.Sequence == item.Sequence &&
                        string.Equals(status.EntryId, item.EventId, StringComparison.Ordinal)))
                .Any())
        {
            throw new InvalidDataException(
                "The pending attempt contains a gap or does not exactly identify retained journal entries.");
        }
    }

    private static bool IsDurablyAcknowledged(
        CortexSyncPendingSubmitAttempt attempt,
        CortexSyncJournalSnapshot snapshot,
        CortexSyncCursorV1? cursor)
    {
        if (cursor is null)
            return false;
        var bundle = attempt.Request.Bundle;
        var leaf = DecodeSubmittedSnapshot(bundle.Events[^1]);
        var statuses = bundle.Events.Select(item => snapshot.Entries.Single(status =>
            status.Sequence == item.Sequence &&
            string.Equals(status.EntryId, item.EventId, StringComparison.Ordinal))).ToArray();
        return statuses.All(item =>
                   item.State == JournalEntrySyncState.Reconciled &&
                   !string.IsNullOrWhiteSpace(item.ReceiptId)) &&
            statuses.Select(item => item.ReceiptId).Distinct(StringComparer.Ordinal).Count() == 1 &&
            string.Equals(cursor.ProjectId, bundle.ProjectId, StringComparison.Ordinal) &&
            string.Equals(cursor.StoreId, bundle.StoreId, StringComparison.Ordinal) &&
            string.Equals(cursor.LocalStoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) &&
            cursor.LocalHighWaterSequence >= bundle.LastSequence &&
            string.Equals(cursor.RemoteStoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) &&
            cursor.RemoteHighWaterSequence == bundle.LastSequence &&
            string.Equals(cursor.RemoteEventHash, bundle.Events[^1].EventHash, StringComparison.Ordinal) &&
            string.Equals(cursor.RemoteBundleHash, bundle.BundleHash, StringComparison.Ordinal) &&
            string.Equals(cursor.LastAcknowledgedAuthorityHash, leaf.AuthorityHash, StringComparison.Ordinal) &&
            string.Equals(cursor.LastAcknowledgedPayloadHash, leaf.PayloadHash, StringComparison.Ordinal);
    }

    private static void ValidateRemoteHead(
        CortexSyncRemoteHead head,
        string projectLinkId,
        string storeId,
        string localStoreEpoch,
        string projectId)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (!CortexSyncContractPolicy.HasValidHash(head) ||
            !string.Equals(head.ProjectLinkId, projectLinkId, StringComparison.Ordinal) ||
            !string.Equals(head.StoreId, storeId, StringComparison.Ordinal) ||
            !string.Equals(head.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(head.StoreEpoch, localStoreEpoch, StringComparison.Ordinal) ||
            head.ObservedAt.Offset != TimeSpan.Zero ||
            head.HighWaterSequence is < 0 or > CortexSyncContractLimits.MaximumSequence ||
            !CortexSyncCursorPolicy.IsHash(head.LastEventHash) ||
            !CortexSyncCursorPolicy.IsHash(head.LastBundleHash) ||
            head.Exists &&
            (!ValidIdentifier(head.StoreEpoch) ||
             !ValidIdentifier(head.ServerRevision) ||
             !CortexSyncCursorPolicy.IsHash(head.AuthorityHash) ||
             !CortexSyncCursorPolicy.IsHash(head.RetainedPayloadHash) ||
             head.RetainedSnapshotRevision < 1 ||
             !CortexSyncCursorPolicy.IsHash(head.RetainedSnapshotHash) ||
             !ValidIdentifier(head.ContextSetId) ||
             !ValidIdentifier(head.LastEventId) ||
             !ValidIdentifier(head.LastBundleId)) ||
            !head.Exists &&
            (head.ServerRevision is not null || head.AuthorityHash is not null ||
             head.RetainedPayloadHash is not null ||
             head.RetainedSnapshotHash is not null || head.ContextSetId is not null ||
             head.RetainedSnapshotRevision != 0 || head.LastEventId is not null || head.LastBundleId is not null ||
             head.HighWaterSequence != 0 ||
             !string.Equals(head.LastEventHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal) ||
             !string.Equals(head.LastBundleHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The remote head is malformed or belongs to another sync scope.");
        }
    }

    private static void ValidateSubmit(
        CortexSyncSubmitResult submitted,
        CortexOfflineBundleV1 bundle,
        SubmittedSnapshotEvidence expected,
        CortexSyncCapabilitiesV1 capabilities)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        var receipt = submitted.Receipt ?? throw new InvalidDataException("The server returned no durable receipt.");
        ValidateRemoteHead(
            submitted.Head,
            expected.ProjectLinkId,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId);
        if (!CortexSyncContractPolicy.HasValidHash(submitted) ||
            !capabilities.SupportedReceiptProofContractIds.Contains(
                receipt.Authentication.ProofContractId,
                StringComparer.Ordinal) ||
            !string.Equals(receipt.BundleHash, bundle.BundleHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !CortexSyncContractPolicy.HasValidSemanticHash(receipt) ||
            !string.Equals(receipt.Reconciliation.State, "reconciled", StringComparison.Ordinal) ||
            receipt.Outcomes.Any(item => !item.Durable || item.Disposition is
                CortexSyncDispositionIds.Conflict or CortexSyncDispositionIds.Rejected or CortexSyncDispositionIds.Quarantined) ||
            !submitted.Head.Exists ||
            receipt.Outcomes.Count != bundle.Events.Count ||
            receipt.Outcomes.Where((item, index) =>
                    item.Sequence != bundle.Events[index].Sequence ||
                    !string.Equals(item.EventId, bundle.Events[index].EventId, StringComparison.Ordinal) ||
                    !string.Equals(item.EventHash, bundle.Events[index].EventHash, StringComparison.Ordinal))
                .Any() ||
            !string.Equals(submitted.Head.AuthorityHash, expected.AuthorityHash, StringComparison.Ordinal) ||
            !string.Equals(
                submitted.Head.RetainedPayloadHash,
                expected.PayloadHash,
                StringComparison.Ordinal) ||
            !string.Equals(submitted.Head.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            submitted.Head.HighWaterSequence != bundle.LastSequence ||
            !string.Equals(submitted.Head.LastEventId, bundle.Events[^1].EventId, StringComparison.Ordinal) ||
            !string.Equals(submitted.Head.LastEventHash, bundle.Events[^1].EventHash, StringComparison.Ordinal) ||
            !string.Equals(submitted.Head.LastBundleHash, bundle.BundleHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The submit receipt or read-back head does not prove the uploaded authority state.");
        }
    }

    private static void ValidateChange(
        CortexLocalSyncChange change,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(change.ProjectId) ||
            !ValidProjectLink(change.ProjectLinkId) ||
            !CortexSyncCursorPolicy.IsHash(change.AuthorityHash) ||
            string.IsNullOrWhiteSpace(change.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(change.Operation) ||
            string.IsNullOrWhiteSpace(change.PayloadContractId) ||
            change.Payload.IsEmpty ||
            change.Payload.Length > CortexSyncContractLimits.MaximumEventPayloadBytes ||
            change.CapturedAt.Offset != TimeSpan.Zero ||
            change.ExpiresAt.Offset != TimeSpan.Zero ||
            change.ExpiresAt <= change.CapturedAt ||
            change.ExpiresAt - observedAt <= MinimumRetainedRefreshHorizon)
        {
            throw new ArgumentException("The current local change is malformed or outside sync bounds.", nameof(change));
        }
        var payloadHash = CortexCanonicalJson.Sha256(change.Payload.Span);
        if (!change.IdempotencyKey.Contains(change.AuthorityHash[..16], StringComparison.Ordinal) &&
            !change.IdempotencyKey.Contains(payloadHash[..16], StringComparison.Ordinal))
        {
            throw new ArgumentException("The idempotency key must be deterministically bound to authority or payload bytes.", nameof(change));
        }
    }

    private static void ValidateOptions(CortexSyncClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TenantIdHint) ||
            string.IsNullOrWhiteSpace(options.NamespaceHint) ||
            options.ObservedPolicyRevision <= 0 ||
            options.MaximumEventsPerBundle is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle ||
            options.EffectiveSafetyPollInterval != TimeSpan.FromSeconds(60) ||
            options.EffectiveStableFileDebounce < TimeSpan.FromMilliseconds(100) ||
            options.EffectiveStableFileDebounce > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentException("Sync client options violate the bounded v1 polling and upload profile.", nameof(options));
        }
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsExpectedRemoteFailure(
        Exception exception,
        CancellationToken callerCancellationToken) =>
        exception switch
        {
            // Transports commonly surface their own timeout/circuit cancellation as
            // TaskCanceledException. Preserve the caller's explicit cancellation while making
            // a non-caller transport cancellation a bounded retryable remote failure.
            OperationCanceledException => !callerCancellationToken.IsCancellationRequested,
            InvalidDataException or ArgumentException or IOException or InvalidOperationException or
                TimeoutException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or
                System.Net.Http.HttpRequestException => true,
            _ => false
        };

    private static bool IsExpectedLocalPersistenceFailure(Exception exception) =>
        exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException;

    private async ValueTask ProveDurableAttemptAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        var saved = await _attemptStore.SaveAsync(attempt, cancellationToken).ConfigureAwait(false);
        if (!saved.DurableForReplay ||
            !string.Equals(saved.AttemptHash, attempt.Evidence.AttemptHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The pending-attempt store did not prove crash-durable exact-byte replay.");
        }
    }

    private static readonly string[] RequiredCapabilities =
    [
        CortexSyncCapabilityIds.DurableReceipts,
        CortexSyncCapabilityIds.EventHashChain,
        CortexSyncCapabilityIds.ServerReadBackReconciliation,
        CortexSyncCapabilityIds.CurrentRecoveryMaterialization
    ];

    private static readonly string[] RequiredGatewayContracts =
    [
        CortexSyncContractIds.SyncGatewayHeadRequest,
        CortexSyncContractIds.SyncGatewayHead,
        CortexSyncContractIds.SyncGatewaySubmitRequest,
        CortexSyncContractIds.SyncGatewaySubmitResult
    ];

    private static void ValidateAdvertisedLimits(
        CortexOfflineBundleV1 bundle,
        CortexSyncLimitsV1 limits)
    {
        if (bundle.Events.Count > limits.MaximumEventsPerBundle ||
            bundle.Events.Count > limits.MaximumReceiptOutcomes ||
            bundle.Events.Any(item => item.PayloadBytes > limits.MaximumEventPayloadBytes) ||
            bundle.Extensions.Count > limits.MaximumExtensions ||
            bundle.Extensions.Any(item => item.PayloadBytes > limits.MaximumExtensionPayloadBytes) ||
            CortexCanonicalJson.Serialize(bundle).Length > limits.MaximumCanonicalBundleBytes)
        {
            throw new InvalidDataException(
                "The canonical bundle exceeds one or more advertised server limits.");
        }
    }

    private static SubmittedSnapshotEvidence DecodeSubmittedSnapshot(
        CortexOfflineEventV1 item)
    {
        if (!string.Equals(
                item.PayloadContractId,
                AgentTaskSnapshotContractIds.Snapshot,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Reconnect synchronization accepts only current-recovery task snapshots.");
        }

        byte[] canonical;
        try
        {
            var encoded = item.PayloadBase64Url.Replace('-', '+').Replace('_', '/');
            encoded += (encoded.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException("Invalid base64url length.")
            };
            canonical = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The exported task snapshot payload is not base64url.", exception);
        }

        AgentTaskSnapshotV1 snapshot;
        try
        {
            snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(canonical);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException("The exported task snapshot payload is malformed.", exception);
        }
        if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
            !CortexCanonicalJson.Serialize(snapshot).AsSpan().SequenceEqual(canonical) ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), item.PayloadSha256, StringComparison.Ordinal) ||
            snapshot.ProjectLinkId is null ||
            !string.Equals(snapshot.ProjectId, item.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exported task snapshot is noncanonical or hash/scope mismatched.");
        }
        return new SubmittedSnapshotEvidence(
            snapshot.ProjectLinkId,
            snapshot.ProjectId,
            CortexMaterializeCurrentChangeSource.ComputeAuthoritySetHash(snapshot.SourceRevisions),
            item.PayloadSha256);
    }

    private static bool ValidProjectLink(string? value) =>
        value is { Length: 70 } &&
        value.StartsWith("plink_", StringComparison.Ordinal) &&
        value.Skip(6).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record SubmittedSnapshotEvidence(
        string ProjectLinkId,
        string ProjectId,
        string AuthorityHash,
        string PayloadHash);

    private static readonly TimeSpan MinimumRetainedRefreshHorizon =
        TimeSpan.FromHours(1);

    private static CortexSyncRunResult Rejected(string code, string message) =>
        new(CortexSyncRunDisposition.Rejected, code, message);

    private static CortexSyncRunResult RemoteRejected(
        string code,
        string messagePrefix,
        Exception exception) =>
        new(
            CortexSyncRunDisposition.Rejected,
            code,
            messagePrefix + exception.Message,
            Retryable: exception is not (
                UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException));
}
