using System.Data;
using System.Data.Common;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;

namespace Pegasus.Cortex.Persistence.PostgreSql.Sync;

public sealed partial class PostgreSqlCortexSyncRepository
{
    private static async ValueTask AcquireScopeLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        CancellationToken cancellationToken)
    {
        var lockScope = string.Join('\n',
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            AdvisoryLockSql,
            new PostgreSqlParameter("lock_scope", DbType.String, lockScope));
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAttemptAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        CortexSyncCommitDisposition disposition,
        string commitId,
        string storageRevision,
        DateTimeOffset committedAt,
        string durableStateHash,
        IReadOnlyList<CortexSyncReceiptOutcomeV1> outcomes,
        PostgreSqlSyncSourceHead? sourceHead,
        PostgreSqlSyncCapturedMigrationPage? migrationPage,
        CancellationToken cancellationToken)
    {
        var advances = disposition == CortexSyncCommitDisposition.Committed;
        await InsertBatchAsync(
            connection,
            transaction,
            captured,
            attemptId,
            advances,
            migrationPage,
            cancellationToken).ConfigureAwait(false);
        await InsertEntriesAsync(
            connection,
            transaction,
            captured,
            attemptId,
            advances,
            cancellationToken).ConfigureAwait(false);
        await InsertCommitAsync(
            connection,
            transaction,
            captured,
            attemptId,
            disposition,
            commitId,
            storageRevision,
            committedAt,
            durableStateHash,
            cancellationToken).ConfigureAwait(false);
        await InsertOutcomesAsync(
            connection,
            transaction,
            captured.Scope,
            attemptId,
            captured.Bundle.BundleId,
            commitId,
            outcomes,
            cancellationToken).ConfigureAwait(false);
        if (sourceHead is not null)
        {
            await InsertSourceHeadAsync(
                connection,
                transaction,
                captured.Scope,
                attemptId,
                commitId,
                captured.Bundle.BundleHash,
                committedAt,
                sourceHead,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        bool advancesSourceHead,
        PostgreSqlSyncCapturedMigrationPage? migrationPage,
        CancellationToken cancellationToken)
    {
        var scope = captured.Scope;
        var bundle = captured.Bundle;
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertBatchSql,
            CommonParameters(scope)
                .Concat(
                [
                    new("attempt_id", DbType.String, attemptId),
                    new("advances_source_head", DbType.Boolean, advancesSourceHead),
                    new("bundle_id", DbType.String, bundle.BundleId),
                    new("bundle_schema_version", DbType.Int32, bundle.SchemaVersion),
                    new("bundle_contract_id", DbType.String, bundle.ContractId),
                    new("canonicalization", DbType.String, bundle.Canonicalization),
                    new("base_server_revision", DbType.String, bundle.BaseServerRevision),
                    new("previous_bundle_hash", DbType.String, bundle.PreviousBundleHash),
                    new("prior_event_hash", DbType.String, bundle.PriorEventHash),
                    new("first_source_sequence", DbType.Int64, bundle.FirstSequence),
                    new("last_source_sequence", DbType.Int64, bundle.LastSequence),
                    new("entry_count", DbType.Int32, bundle.Events.Count),
                    new("bundle_hash", DbType.String, bundle.BundleHash),
                    new("created_at", DbType.DateTimeOffset,
                        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(bundle.CreatedAt)),
                    new("received_at", DbType.DateTimeOffset, captured.ReceivedAt),
                    new("authority_id", DbType.String, scope.AuthorityId),
                    new("subject_id", DbType.String, scope.SubjectId),
                    new("service_id", DbType.String, scope.ServiceId),
                    new("policy_revision", DbType.Int64, scope.PolicyRevision),
                    new("extensions_canonical", DbType.Binary, captured.ExtensionsCanonical),
                    new("bundle_canonical", DbType.Binary, captured.Canonical),
                    new("bundle_canonical_sha256", DbType.String, captured.CanonicalSha256),
                    new("migration_id", DbType.String, migrationPage?.Page.CurrentCursor.MigrationId),
                    new("migration_page_number", DbType.Int64, migrationPage?.Page.CurrentCursor.PageNumber),
                    new("migration_page_id", DbType.String, migrationPage?.Page.PageId),
                    new("migration_page_hash", DbType.String, migrationPage?.Page.PageHash),
                    new("migration_current_cursor_hash", DbType.String, migrationPage?.Page.CurrentCursor.CursorHash),
                    new("migration_next_cursor_hash", DbType.String, migrationPage?.Page.NextCursor.CursorHash),
                    new("migration_inventory_hash", DbType.String, migrationPage?.Page.SourceInventory.InventoryHash),
                    new("migration_page_canonical", DbType.Binary, migrationPage?.Canonical),
                    new("migration_page_canonical_sha256", DbType.String, migrationPage?.CanonicalSha256)
                ]).ToArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_batch_insert_count_invalid",
                scope,
                bundle.BundleId);
        }
    }

    private static async ValueTask InsertEntriesAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        bool advancesSourceHead,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(2048 + captured.Events.Count * 640);
        sql.Append("""
            INSERT INTO cortex_sync.journal_entry
            (
                tenant_id, namespace_name, store_id, store_epoch, project_id,
                attempt_id, advances_source_head, bundle_id,
                bundle_first_source_sequence, bundle_last_source_sequence,
                source_sequence, event_id, event_schema_version, event_contract_id,
                canonicalization, previous_event_hash, event_hash, occurred_at,
                producer_instance_id, operation_id, tenant_id_hint, namespace_hint,
                observed_policy_revision, payload_contract_id, payload, payload_bytes,
                payload_sha256, source_kind, source_root_id, source_relative_path,
                source_bytes, source_sha256, source_captured_at,
                source_provenance_canonical, content_treatment, executable,
                extensions_canonical, event_canonical, event_canonical_sha256
            )
            VALUES
            """);
        var parameters = new List<PostgreSqlParameter>(16 + captured.Events.Count * 29);
        parameters.AddRange(CommonParameters(captured.Scope));
        parameters.Add(new PostgreSqlParameter("attempt_id", DbType.String, attemptId));
        parameters.Add(new PostgreSqlParameter("advances_source_head", DbType.Boolean, advancesSourceHead));
        parameters.Add(new PostgreSqlParameter("bundle_id", DbType.String, captured.Bundle.BundleId));
        parameters.Add(new PostgreSqlParameter(
            "bundle_first_source_sequence",
            DbType.Int64,
            captured.Bundle.FirstSequence));
        parameters.Add(new PostgreSqlParameter(
            "bundle_last_source_sequence",
            DbType.Int64,
            captured.Bundle.LastSequence));

        for (var index = 0; index < captured.Events.Count; index++)
        {
            if (index != 0)
                sql.Append(',');
            sql.Append($"""
                (
                    @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
                    @attempt_id, @advances_source_head, @bundle_id,
                    @bundle_first_source_sequence, @bundle_last_source_sequence,
                    @source_sequence_{index}, @event_id_{index}, @event_schema_version_{index},
                    @event_contract_id_{index}, @canonicalization_{index},
                    @previous_event_hash_{index}, @event_hash_{index}, @occurred_at_{index},
                    @producer_instance_id_{index}, @operation_id_{index}, @tenant_id_hint_{index},
                    @namespace_hint_{index}, @observed_policy_revision_{index},
                    @payload_contract_id_{index}, @payload_{index}, @payload_bytes_{index},
                    @payload_sha256_{index}, @source_kind_{index}, @source_root_id_{index},
                    @source_relative_path_{index}, @source_bytes_{index}, @source_sha256_{index},
                    @source_captured_at_{index}, @source_provenance_canonical_{index},
                    @content_treatment_{index}, @executable_{index}, @extensions_canonical_{index},
                    @event_canonical_{index}, @event_canonical_sha256_{index}
                )
                """);
            var capturedEvent = captured.Events[index];
            var item = capturedEvent.Event;
            var source = item.Source;
            parameters.AddRange(
            [
                new($"source_sequence_{index}", DbType.Int64, item.Sequence),
                new($"event_id_{index}", DbType.String, item.EventId),
                new($"event_schema_version_{index}", DbType.Int32, item.SchemaVersion),
                new($"event_contract_id_{index}", DbType.String, item.ContractId),
                new($"canonicalization_{index}", DbType.String, item.Canonicalization),
                new($"previous_event_hash_{index}", DbType.String, item.PreviousEventHash),
                new($"event_hash_{index}", DbType.String, item.EventHash),
                new($"occurred_at_{index}", DbType.DateTimeOffset,
                    PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(item.OccurredAt)),
                new($"producer_instance_id_{index}", DbType.String, item.ProducerInstanceId),
                new($"operation_id_{index}", DbType.String, item.OperationId),
                new($"tenant_id_hint_{index}", DbType.String, item.TenantIdHint),
                new($"namespace_hint_{index}", DbType.String, item.NamespaceHint),
                new($"observed_policy_revision_{index}", DbType.Int64, item.ObservedPolicyRevision),
                new($"payload_contract_id_{index}", DbType.String, item.PayloadContractId),
                new($"payload_{index}", DbType.Binary, capturedEvent.Payload),
                new($"payload_bytes_{index}", DbType.Int32, item.PayloadBytes),
                new($"payload_sha256_{index}", DbType.String, item.PayloadSha256),
                new($"source_kind_{index}", DbType.String, source.SourceKind),
                new($"source_root_id_{index}", DbType.String, source.SourceRootId),
                new($"source_relative_path_{index}", DbType.String, source.SourceRelativePath),
                new($"source_bytes_{index}", DbType.Int64, source.SourceBytes),
                new($"source_sha256_{index}", DbType.String, source.SourceSha256),
                new($"source_captured_at_{index}", DbType.DateTimeOffset,
                    PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(source.CapturedAt)),
                new($"source_provenance_canonical_{index}", DbType.Binary,
                    capturedEvent.SourceCanonical),
                new($"content_treatment_{index}", DbType.String, item.ContentTreatment),
                new($"executable_{index}", DbType.Boolean, item.Executable),
                new($"extensions_canonical_{index}", DbType.Binary,
                    capturedEvent.ExtensionsCanonical),
                new($"event_canonical_{index}", DbType.Binary, capturedEvent.Canonical),
                new($"event_canonical_sha256_{index}", DbType.String,
                    capturedEvent.CanonicalSha256)
            ]);
        }
        sql.Append(';');
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            sql.ToString(),
            parameters.ToArray());
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != captured.Events.Count)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_entry_insert_count_invalid",
                captured.Scope,
                captured.Bundle.BundleId);
        }
    }

    private static async ValueTask InsertCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        CortexSyncCommitDisposition disposition,
        string commitId,
        string storageRevision,
        DateTimeOffset committedAt,
        string durableStateHash,
        CancellationToken cancellationToken)
    {
        var scope = captured.Scope;
        var bundle = captured.Bundle;
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertCommitSql,
            CommonParameters(scope)
                .Concat(
                [
                    new("attempt_id", DbType.String, attemptId),
                    new("advances_source_head", DbType.Boolean,
                        disposition == CortexSyncCommitDisposition.Committed),
                    new("bundle_id", DbType.String, bundle.BundleId),
                    new("bundle_hash", DbType.String, bundle.BundleHash),
                    new("first_source_sequence", DbType.Int64, bundle.FirstSequence),
                    new("last_source_sequence", DbType.Int64, bundle.LastSequence),
                    new("source_event_count", DbType.Int32, bundle.Events.Count),
                    new("commit_id", DbType.String, commitId),
                    new("storage_revision", DbType.String, storageRevision),
                    new("disposition", DbType.String, PostgreSqlSyncHashPolicy.Disposition(disposition)),
                    new("received_at", DbType.DateTimeOffset, captured.ReceivedAt),
                    new("committed_at", DbType.DateTimeOffset, committedAt),
                    new("durable_state_hash", DbType.String, durableStateHash),
                    new("authority_id", DbType.String, scope.AuthorityId),
                    new("subject_id", DbType.String, scope.SubjectId),
                    new("service_id", DbType.String, scope.ServiceId),
                    new("policy_revision", DbType.Int64, scope.PolicyRevision)
                ]).ToArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_commit_insert_count_invalid",
                scope,
                bundle.BundleId);
        }
    }

    private static async ValueTask InsertOutcomesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        string bundleId,
        string commitId,
        IReadOnlyList<CortexSyncReceiptOutcomeV1> outcomes,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(1024 + outcomes.Count * 320);
        sql.Append("""
            INSERT INTO cortex_sync.journal_outcome
            (
                tenant_id, namespace_name, store_id, store_epoch, project_id,
                attempt_id, bundle_id, commit_id, source_sequence, event_id,
                event_hash, disposition, durable, server_entity_id,
                server_revision_id, diagnostic_code
            )
            VALUES
            """);
        var parameters = new List<PostgreSqlParameter>(8 + outcomes.Count * 8);
        parameters.AddRange(CommonParameters(scope));
        parameters.Add(new PostgreSqlParameter("attempt_id", DbType.String, attemptId));
        parameters.Add(new PostgreSqlParameter("bundle_id", DbType.String, bundleId));
        parameters.Add(new PostgreSqlParameter("commit_id", DbType.String, commitId));
        for (var index = 0; index < outcomes.Count; index++)
        {
            if (index != 0)
                sql.Append(',');
            sql.Append($"""
                (
                    @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
                    @attempt_id, @bundle_id, @commit_id, @source_sequence_{index},
                    @event_id_{index}, @event_hash_{index}, @disposition_{index},
                    @durable_{index}, @server_entity_id_{index}, @server_revision_id_{index},
                    @diagnostic_code_{index}
                )
                """);
            var item = outcomes[index];
            parameters.AddRange(
            [
                new($"source_sequence_{index}", DbType.Int64, item.Sequence),
                new($"event_id_{index}", DbType.String, item.EventId),
                new($"event_hash_{index}", DbType.String, item.EventHash),
                new($"disposition_{index}", DbType.String, item.Disposition),
                new($"durable_{index}", DbType.Boolean, item.Durable),
                new($"server_entity_id_{index}", DbType.String, item.ServerEntityId),
                new($"server_revision_id_{index}", DbType.String, item.ServerRevisionId),
                new($"diagnostic_code_{index}", DbType.String, item.DiagnosticCode)
            ]);
        }
        sql.Append(';');
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            sql.ToString(),
            parameters.ToArray());
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != outcomes.Count)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_outcome_insert_count_invalid",
                scope,
                bundleId);
        }
    }

    private static async ValueTask InsertSourceHeadAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        string commitId,
        string bundleHash,
        DateTimeOffset committedAt,
        PostgreSqlSyncSourceHead sourceHead,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertHeadSql,
            CommonParameters(scope)
                .Concat(
                [
                    new("attempt_id", DbType.String, attemptId),
                    new("storage_revision", DbType.String, sourceHead.StorageRevision),
                    new("base_server_revision", DbType.String, sourceHead.BaseServerRevision),
                    new("bundle_id", DbType.String, sourceHead.BundleId),
                    new("commit_id", DbType.String, commitId),
                    new("committed_event_id", DbType.String, sourceHead.EventId),
                    new("committed_bundle_hash", DbType.String, bundleHash),
                    new("committed_source_sequence", DbType.Int64, sourceHead.SourceSequence),
                    new("committed_chain_hash", DbType.String, sourceHead.EventHash),
                    new("recorded_at", DbType.DateTimeOffset, committedAt)
                ]).ToArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_head_insert_count_invalid",
                scope,
                sourceHead.BundleId);
        }
    }

    private static async ValueTask<int> InsertReceiptAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncReceiptAppendRequest request,
        string attemptId,
        byte[] proof,
        byte[] extensionsCanonical,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        var scope = request.Scope;
        var receipt = request.Receipt;
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertReceiptSql,
            CommonParameters(scope)
                .Concat(
                [
                    new("attempt_id", DbType.String, attemptId),
                    new("bundle_id", DbType.String, receipt.BundleId),
                    new("receipt_id", DbType.String, receipt.ReceiptId),
                    new("receipt_schema_version", DbType.Int32, receipt.SchemaVersion),
                    new("receipt_contract_id", DbType.String, receipt.ContractId),
                    new("canonicalization", DbType.String, receipt.Canonicalization),
                    new("server_instance_id", DbType.String, receipt.ServerInstanceId),
                    new("bundle_hash", DbType.String, receipt.BundleHash),
                    new("first_source_sequence", DbType.Int64, receipt.Outcomes[0].Sequence),
                    new("last_source_sequence", DbType.Int64, receipt.Outcomes[^1].Sequence),
                    new("source_event_count", DbType.Int32, receipt.Outcomes.Count),
                    new("received_at", DbType.DateTimeOffset,
                        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(receipt.ReceivedAt)),
                    new("commit_id", DbType.String, receipt.Durability.CommitId),
                    new("storage_revision", DbType.String, receipt.Durability.StorageRevision),
                    new("committed_at", DbType.DateTimeOffset,
                        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(receipt.Durability.CommittedAt)),
                    new("reconciliation_state", DbType.String, receipt.Reconciliation.State),
                    new("matched_event_count", DbType.Int32, receipt.Reconciliation.MatchedEventCount),
                    new("read_back_at", DbType.DateTimeOffset,
                        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(
                            receipt.Reconciliation.ReadBackAt!.Value)),
                    new("read_back_hash", DbType.String, receipt.Reconciliation.ReadBackHash),
                    new("receipt_hash", DbType.String, receipt.ReceiptHash),
                    new("proof_contract_id", DbType.String, receipt.Authentication.ProofContractId),
                    new("proof_key_id", DbType.String, receipt.Authentication.KeyId),
                    new("proof_algorithm_id", DbType.String, receipt.Authentication.AlgorithmId),
                    new("covered_receipt_hash", DbType.String,
                        receipt.Authentication.CoveredReceiptHash),
                    new("proof", DbType.Binary, proof),
                    new("proof_sha256", DbType.String, receipt.Authentication.ProofSha256),
                    new("extensions_canonical", DbType.Binary, extensionsCanonical),
                    new("receipt_canonical", DbType.Binary, request.CanonicalReceipt.ToArray()),
                    new("receipt_canonical_sha256", DbType.String, request.CanonicalSha256),
                    new("recorded_at", DbType.DateTimeOffset, recordedAt)
                ]).ToArray());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ForceDeferredConstraintsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            ForceDeferredConstraintsSql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
