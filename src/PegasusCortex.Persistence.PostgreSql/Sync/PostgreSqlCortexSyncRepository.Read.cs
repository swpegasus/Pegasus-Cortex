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
    private static async ValueTask<PostgreSqlStoredSyncCommit?> ReadStoredCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            StoredCommitSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("attempt_id", DbType.String, attemptId))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var storedAttemptId = reader.GetString(0);
        var advancesSourceHead = reader.GetBoolean(1);
        var bundleId = reader.GetString(2);
        var bundleSchemaVersion = reader.GetInt32(3);
        var bundleContractId = reader.GetString(4);
        var canonicalization = reader.GetString(5);
        var baseServerRevision = reader.IsDBNull(6) ? null : reader.GetString(6);
        var previousBundleHash = reader.GetString(7);
        var priorEventHash = reader.GetString(8);
        var firstSequence = reader.GetInt64(9);
        var lastSequence = reader.GetInt64(10);
        var entryCount = reader.GetInt32(11);
        var bundleHash = reader.GetString(12);
        var createdAt = ReadTimestamp(reader, 13);
        var batchReceivedAt = ReadTimestamp(reader, 14);
        var authorityId = reader.GetString(15);
        var subjectId = reader.GetString(16);
        var serviceId = reader.GetString(17);
        var policyRevision = reader.GetInt64(18);
        var extensionsCanonical = reader.GetFieldValue<byte[]>(19);
        var bundleCanonical = reader.GetFieldValue<byte[]>(20);
        var bundleCanonicalSha256 = reader.GetString(21);
        var migrationId = reader.IsDBNull(22) ? null : reader.GetString(22);
        var migrationPageNumber = reader.IsDBNull(23) ? (long?)null : reader.GetInt64(23);
        var migrationPageId = reader.IsDBNull(24) ? null : reader.GetString(24);
        var migrationPageHash = reader.IsDBNull(25) ? null : reader.GetString(25);
        var migrationCurrentCursorHash = reader.IsDBNull(26) ? null : reader.GetString(26);
        var migrationNextCursorHash = reader.IsDBNull(27) ? null : reader.GetString(27);
        var migrationInventoryHash = reader.IsDBNull(28) ? null : reader.GetString(28);
        var migrationPageCanonical = reader.IsDBNull(29) ? null : reader.GetFieldValue<byte[]>(29);
        var migrationPageCanonicalSha256 = reader.IsDBNull(30) ? null : reader.GetString(30);
        var commitId = reader.GetString(31);
        var storageRevision = reader.GetString(32);
        CortexSyncCommitDisposition disposition;
        try
        {
            disposition = PostgreSqlSyncHashPolicy.ParseDisposition(reader.GetString(33));
        }
        catch (ArgumentException exception)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_commit_projection_invalid",
                scope,
                bundleId,
                exception);
        }
        var commitReceivedAt = ReadTimestamp(reader, 34);
        var committedAt = ReadTimestamp(reader, 35);
        var durableStateHash = reader.GetString(36);
        var commitAuthorityId = reader.GetString(37);
        var commitSubjectId = reader.GetString(38);
        var commitServiceId = reader.GetString(39);
        var commitPolicyRevision = reader.GetInt64(40);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_commit_ambiguous",
                scope,
                bundleId);
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        var bundle = PostgreSqlSyncCanonicalCodec.DecodeBundle(
            bundleCanonical,
            bundleCanonicalSha256,
            scope);
        PostgreSqlStoredMigrationPage? migrationPage = null;
        var migrationProjectionCount = new object?[]
        {
            migrationId, migrationPageNumber, migrationPageId, migrationPageHash,
            migrationCurrentCursorHash, migrationNextCursorHash, migrationInventoryHash,
            migrationPageCanonical, migrationPageCanonicalSha256
        }.Count(value => value is not null);
        if (migrationProjectionCount != 0)
        {
            if (migrationProjectionCount != 9)
                throw PostgreSqlSyncCanonicalCodec.Failure(
                    "cortex_postgresql_sync_migration_page_projection_incomplete",
                    scope,
                    bundleId);
            var decodedPage = PostgreSqlSyncCanonicalCodec.DecodeMigrationPage(
                migrationPageCanonical!,
                migrationPageCanonicalSha256!,
                scope,
                migrationPageId!);
            if (!string.Equals(migrationId, decodedPage.CurrentCursor.MigrationId, StringComparison.Ordinal) ||
                migrationPageNumber != decodedPage.CurrentCursor.PageNumber ||
                !string.Equals(migrationPageId, decodedPage.PageId, StringComparison.Ordinal) ||
                !string.Equals(migrationPageHash, decodedPage.PageHash, StringComparison.Ordinal) ||
                !string.Equals(migrationCurrentCursorHash, decodedPage.CurrentCursor.CursorHash, StringComparison.Ordinal) ||
                !string.Equals(migrationNextCursorHash, decodedPage.NextCursor.CursorHash, StringComparison.Ordinal) ||
                !string.Equals(migrationInventoryHash, decodedPage.SourceInventory.InventoryHash, StringComparison.Ordinal) ||
                !string.Equals(decodedPage.Bundle.BundleHash, bundle.BundleHash, StringComparison.Ordinal))
            {
                throw PostgreSqlSyncCanonicalCodec.Failure(
                    "cortex_postgresql_sync_migration_page_projection_mismatch",
                    scope,
                    migrationPageId);
            }
            migrationPage = new PostgreSqlStoredMigrationPage(
                decodedPage,
                migrationPageCanonical!,
                migrationPageCanonicalSha256!);
        }
        var expectedExtensions = CortexCanonicalJson.Serialize(bundle.Extensions);
        if (!string.Equals(storedAttemptId, attemptId, StringComparison.Ordinal) ||
            !string.Equals(bundleId, bundle.BundleId, StringComparison.Ordinal) ||
            bundleSchemaVersion != bundle.SchemaVersion ||
            !string.Equals(bundleContractId, bundle.ContractId, StringComparison.Ordinal) ||
            !string.Equals(canonicalization, bundle.Canonicalization, StringComparison.Ordinal) ||
            !string.Equals(baseServerRevision, bundle.BaseServerRevision, StringComparison.Ordinal) ||
            !string.Equals(previousBundleHash, bundle.PreviousBundleHash, StringComparison.Ordinal) ||
            !string.Equals(priorEventHash, bundle.PriorEventHash, StringComparison.Ordinal) ||
            firstSequence != bundle.FirstSequence || lastSequence != bundle.LastSequence ||
            entryCount != bundle.Events.Count ||
            !string.Equals(bundleHash, bundle.BundleHash, StringComparison.Ordinal) ||
            createdAt != PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(bundle.CreatedAt) ||
            batchReceivedAt != commitReceivedAt || committedAt < commitReceivedAt ||
            !string.Equals(authorityId, commitAuthorityId, StringComparison.Ordinal) ||
            !string.Equals(subjectId, commitSubjectId, StringComparison.Ordinal) ||
            !string.Equals(serviceId, commitServiceId, StringComparison.Ordinal) ||
            policyRevision != commitPolicyRevision ||
            advancesSourceHead != (disposition == CortexSyncCommitDisposition.Committed) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(authorityId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(subjectId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(serviceId) ||
            policyRevision < 1 ||
            !extensionsCanonical.AsSpan().SequenceEqual(expectedExtensions) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(commitId) ||
            !PostgreSqlSyncCanonicalCodec.ValidIdentifier(storageRevision) ||
            !PostgreSqlSyncCanonicalCodec.ValidHash(durableStateHash))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_commit_projection_mismatch",
                scope,
                bundleId);
        }

        await ValidateStoredEntriesAsync(
            connection,
            transaction,
            scope,
            attemptId,
            advancesSourceHead,
            bundle,
            cancellationToken).ConfigureAwait(false);
        var outcomes = await ReadStoredOutcomesAsync(
            connection,
            transaction,
            scope,
            attemptId,
            bundle,
            commitId,
            cancellationToken).ConfigureAwait(false);
        var head = await ReadStoredHeadAsync(
            connection,
            transaction,
            scope,
            attemptId,
            commitId,
            committedAt,
            cancellationToken).ConfigureAwait(false);
        if (disposition == CortexSyncCommitDisposition.Committed != (head is not null))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_head_projection_mismatch",
                scope,
                bundleId);
        }

        var recomputedHash = PostgreSqlSyncHashPolicy.DurableStateHash(new PostgreSqlSyncDurableState(
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId,
            attemptId,
            bundle.BundleId,
            bundle.BundleHash,
            commitId,
            storageRevision,
            disposition,
            commitReceivedAt,
            committedAt,
            outcomes,
            head));
        if (!string.Equals(recomputedHash, durableStateHash, StringComparison.Ordinal))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_durable_hash_mismatch",
                scope,
                bundleId);
        }

        var snapshot = new CortexSyncCommitSnapshot(
            disposition,
            commitId,
            storageRevision,
            commitReceivedAt,
            committedAt,
            durableStateHash,
            outcomes);
        return new PostgreSqlStoredSyncCommit(
            scope with { },
            attemptId,
            bundle,
            bundleCanonical,
            bundleCanonicalSha256,
            disposition,
            commitId,
            storageRevision,
            commitReceivedAt,
            committedAt,
            durableStateHash,
            outcomes,
            head,
            migrationPage,
            snapshot);
    }

    private static async ValueTask ValidateStoredEntriesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        bool advancesSourceHead,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            StoredEntriesSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("attempt_id", DbType.String, attemptId))
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, bundle.BundleId))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= bundle.Events.Count)
                throw PostgreSqlSyncCanonicalCodec.Failure(
                    "cortex_postgresql_sync_entry_count_invalid",
                    scope,
                    bundle.BundleId);
            var expected = bundle.Events[index];
            var storedEventId = reader.GetString(6);
            var canonical = reader.GetFieldValue<byte[]>(32);
            var canonicalSha256 = reader.GetString(33);
            var stored = PostgreSqlSyncCanonicalCodec.DecodeEvent(
                canonical,
                canonicalSha256,
                scope,
                storedEventId);
            var decodedPayload = PostgreSqlSyncCanonicalCodec.DecodeBase64Url(
                stored.PayloadBase64Url,
                scope,
                stored.EventId);
            var expectedSource = CortexCanonicalJson.Serialize(stored.Source);
            var expectedExtensions = CortexCanonicalJson.Serialize(stored.Extensions);
            var observedPolicyRevision = reader.IsDBNull(17) ? (long?)null : reader.GetInt64(17);
            var sourceRootId = reader.IsDBNull(23) ? null : reader.GetString(23);
            var sourceRelativePath = reader.IsDBNull(24) ? null : reader.GetString(24);
            if (!string.Equals(reader.GetString(0), attemptId, StringComparison.Ordinal) ||
                reader.GetBoolean(1) != advancesSourceHead ||
                !string.Equals(reader.GetString(2), bundle.BundleId, StringComparison.Ordinal) ||
                reader.GetInt64(3) != bundle.FirstSequence ||
                reader.GetInt64(4) != bundle.LastSequence ||
                reader.GetInt64(5) != stored.Sequence ||
                !string.Equals(storedEventId, stored.EventId, StringComparison.Ordinal) ||
                reader.GetInt32(7) != stored.SchemaVersion ||
                !string.Equals(reader.GetString(8), stored.ContractId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(9), stored.Canonicalization, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(10), stored.PreviousEventHash, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(11), stored.EventHash, StringComparison.Ordinal) ||
                ReadTimestamp(reader, 12) != PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(stored.OccurredAt) ||
                !string.Equals(reader.GetString(13), stored.ProducerInstanceId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(14), stored.OperationId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(15), stored.TenantIdHint, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(16), stored.NamespaceHint, StringComparison.Ordinal) ||
                observedPolicyRevision != stored.ObservedPolicyRevision ||
                !string.Equals(reader.GetString(18), stored.PayloadContractId, StringComparison.Ordinal) ||
                !reader.GetFieldValue<byte[]>(19).AsSpan().SequenceEqual(decodedPayload) ||
                reader.GetInt32(20) != stored.PayloadBytes ||
                !string.Equals(reader.GetString(21), stored.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(22), stored.Source.SourceKind, StringComparison.Ordinal) ||
                !string.Equals(sourceRootId, stored.Source.SourceRootId, StringComparison.Ordinal) ||
                !string.Equals(sourceRelativePath, stored.Source.SourceRelativePath, StringComparison.Ordinal) ||
                reader.GetInt64(25) != stored.Source.SourceBytes ||
                !string.Equals(reader.GetString(26), stored.Source.SourceSha256, StringComparison.Ordinal) ||
                ReadTimestamp(reader, 27) != PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(stored.Source.CapturedAt) ||
                !reader.GetFieldValue<byte[]>(28).AsSpan().SequenceEqual(expectedSource) ||
                !string.Equals(reader.GetString(29), stored.ContentTreatment, StringComparison.Ordinal) ||
                reader.GetBoolean(30) != stored.Executable ||
                !reader.GetFieldValue<byte[]>(31).AsSpan().SequenceEqual(expectedExtensions) ||
                !canonical.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(expected)) ||
                !string.Equals(stored.EventHash, expected.EventHash, StringComparison.Ordinal))
            {
                throw PostgreSqlSyncCanonicalCodec.Failure(
                    "cortex_postgresql_sync_entry_projection_mismatch",
                    scope,
                    storedEventId);
            }
            index++;
        }

        if (index != bundle.Events.Count)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_entry_count_invalid",
                scope,
                bundle.BundleId);
        }
    }

    private static async ValueTask<IReadOnlyList<CortexSyncReceiptOutcomeV1>> ReadStoredOutcomesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        CortexOfflineBundleV1 bundle,
        string commitId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            StoredOutcomesSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("attempt_id", DbType.String, attemptId))
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, bundle.BundleId))
                .Append(new PostgreSqlParameter("commit_id", DbType.String, commitId))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<CortexSyncReceiptOutcomeV1>(bundle.Events.Count);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (values.Count >= bundle.Events.Count)
                    throw new ArgumentException("The stored outcome set exceeds its bundle.");
                var expected = bundle.Events[values.Count];
                var value = new CortexSyncReceiptOutcomeV1(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7));
                if (value.Sequence != expected.Sequence ||
                    !string.Equals(value.EventId, expected.EventId, StringComparison.Ordinal) ||
                    !string.Equals(value.EventHash, expected.EventHash, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The stored outcome does not match its event.");
                }
                values.Add(value);
            }
        }
        catch (ArgumentException exception)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_outcome_projection_mismatch",
                scope,
                bundle.BundleId,
                exception);
        }

        if (values.Count != bundle.Events.Count)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_outcome_count_invalid",
                scope,
                bundle.BundleId);
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static async ValueTask<PostgreSqlSyncSourceHead?> ReadStoredHeadAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string attemptId,
        string commitId,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            StoredHeadSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("attempt_id", DbType.String, attemptId))
                .Append(new PostgreSqlParameter("commit_id", DbType.String, commitId))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var value = new PostgreSqlSyncSourceHead(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5));
        var recordedAt = ReadTimestamp(reader, 6);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || recordedAt < committedAt)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_head_projection_invalid",
                scope,
                value.BundleId);
        }
        return value;
    }

    private static async ValueTask<string?> FindAttemptByCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string bundleId,
        string bundleHash,
        string commitId,
        string storageRevision,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            FindAttemptByCommitSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, bundleId))
                .Append(new PostgreSqlParameter("bundle_hash", DbType.String, bundleHash))
                .Append(new PostgreSqlParameter("commit_id", DbType.String, commitId))
                .Append(new PostgreSqlParameter("storage_revision", DbType.String, storageRevision))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var value = reader.GetString(0);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_attempt_ambiguous",
                scope,
                bundleId);
        return value;
    }

    private static async ValueTask<PostgreSqlCurrentSourceHead?> ReadCurrentLeafAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            CurrentLeafSql,
            CommonParameters(scope));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var value = new PostgreSqlCurrentSourceHead(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_head_ambiguous",
                scope,
                scope.StoreId);
        return value;
    }

    private static async ValueTask<string?> FindLatestPublishedAttemptAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            LatestPublishedAttemptSql,
            CommonParameters(scope));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return reader.GetString(0);
    }

    private static async ValueTask<string?> FindExactPublishedAttemptAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string bundleId,
        string bundleHash,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            ExactPublishedAttemptSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, bundleId))
                .Append(new PostgreSqlParameter("bundle_hash", DbType.String, bundleHash))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var attemptId = reader.GetString(0);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_published_bundle_ambiguous",
                scope,
                bundleId);
        }
        return attemptId;
    }

    private static async ValueTask<bool> HasAcceptedMigrationRegistrationAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            HasAcceptedMigrationRegistrationSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("migration_id", DbType.String, migrationId))
                .ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return false;
        return true;
    }

    private static async ValueTask<PostgreSqlMigrationPredecessor?> ReadCurrentMigrationPredecessorAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        PostgreSqlCurrentSourceHead head,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            CurrentMigrationPredecessorSql,
            CommonParameters(scope)
                .Concat(
                [
                    new PostgreSqlParameter("attempt_id", DbType.String, head.AttemptId),
                    new PostgreSqlParameter("commit_id", DbType.String, head.CommitId)
                ]).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var pageId = reader.GetString(0);
        var page = PostgreSqlSyncCanonicalCodec.DecodeMigrationPage(
            reader.GetFieldValue<byte[]>(1),
            reader.GetString(2),
            scope,
            pageId);
        var storageRevision = reader.GetString(3);
        var receiptId = reader.GetString(4);
        var receiptHash = reader.GetString(5);
        var receiptCanonicalSha256 = reader.GetString(6);
        var receipt = PostgreSqlSyncCanonicalCodec.DecodeReceipt(
            reader.GetFieldValue<byte[]>(7),
            receiptCanonicalSha256,
            scope,
            receiptId);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_migration_predecessor_ambiguous",
                scope,
                pageId);
        if (!string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ReceiptHash, receiptHash, StringComparison.Ordinal) ||
            !string.Equals(page.Bundle.BundleId, head.BundleId, StringComparison.Ordinal) ||
            !string.Equals(page.Bundle.BundleHash, head.BundleHash, StringComparison.Ordinal) ||
            !string.Equals(storageRevision, head.StorageRevision, StringComparison.Ordinal) ||
            !string.Equals(receipt.Durability.CommitId, head.CommitId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Durability.StorageRevision, storageRevision, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleId, page.Bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleHash, page.Bundle.BundleHash, StringComparison.Ordinal) ||
            receipt.Reconciliation.State != "reconciled" ||
            receipt.Reconciliation.MatchedEventCount != receipt.Reconciliation.SourceEventCount)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_migration_predecessor_receipt_invalid",
                scope,
                pageId);
        }

        return new PostgreSqlMigrationPredecessor(page, storageRevision, receipt);
    }

    private static async ValueTask<bool> HasAcceptedIdentityCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        PostgreSqlSyncCapturedBundle captured,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(1024 + captured.Events.Count * 64);
        sql.Append("WITH candidate(source_sequence, event_id, event_hash) AS (VALUES ");
        var parameters = new List<PostgreSqlParameter>(
            8 + captured.Events.Count * 3);
        parameters.AddRange(CommonParameters(captured.Scope));
        parameters.Add(new PostgreSqlParameter("bundle_id", DbType.String, captured.Bundle.BundleId));
        parameters.Add(new PostgreSqlParameter("bundle_hash", DbType.String, captured.Bundle.BundleHash));
        parameters.Add(new PostgreSqlParameter("bundle_canonical_sha256", DbType.String, captured.CanonicalSha256));
        for (var index = 0; index < captured.Events.Count; index++)
        {
            if (index != 0)
                sql.Append(',');
            sql.Append($"(@sequence_{index}, @event_id_{index}, @event_hash_{index})");
            var item = captured.Events[index].Event;
            parameters.Add(new PostgreSqlParameter($"sequence_{index}", DbType.Int64, item.Sequence));
            parameters.Add(new PostgreSqlParameter($"event_id_{index}", DbType.String, item.EventId));
            parameters.Add(new PostgreSqlParameter($"event_hash_{index}", DbType.String, item.EventHash));
        }
        sql.Append("""
            )
            SELECT
                EXISTS
                (
                    SELECT 1
                    FROM cortex_sync.journal_batch AS b
                    WHERE b.tenant_id = @tenant_id
                      AND b.namespace_name = @namespace
                      AND b.store_id = @store_id
                      AND b.store_epoch = @store_epoch
                      AND b.project_id = @project_id
                      AND b.advances_source_head
                      AND
                      (
                          b.bundle_id = @bundle_id OR
                          b.bundle_hash = @bundle_hash OR
                          b.bundle_canonical_sha256 = @bundle_canonical_sha256
                      )
                ) OR
                EXISTS
                (
                    SELECT 1
                    FROM cortex_sync.journal_entry AS e
                    JOIN candidate AS c
                      ON e.source_sequence = c.source_sequence
                      OR e.event_id = c.event_id
                      OR e.event_hash = c.event_hash
                    WHERE e.tenant_id = @tenant_id
                      AND e.namespace_name = @namespace
                      AND e.store_id = @store_id
                      AND e.store_epoch = @store_epoch
                      AND e.project_id = @project_id
                      AND e.advances_source_head
                );
            """);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            sql.ToString(),
            parameters.ToArray());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async ValueTask<PostgreSqlStoredReceipt?> FindStoredReceiptByCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncRepositoryScope scope,
        string bundleId,
        string bundleHash,
        string commitId,
        string storageRevision,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            FindReceiptByCommitSql,
            CommonParameters(scope)
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, bundleId))
                .Append(new PostgreSqlParameter("bundle_hash", DbType.String, bundleHash))
                .Append(new PostgreSqlParameter("commit_id", DbType.String, commitId))
                .Append(new PostgreSqlParameter("storage_revision", DbType.String, storageRevision))
                .ToArray());
        return await ReadStoredReceiptRowAsync(command, scope, bundleId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<PostgreSqlStoredReceipt?> ReadStoredReceiptAsync(
        DbConnection connection,
        DbTransaction transaction,
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            ReadReceiptSql,
            CommonParameters(request.Scope)
                .Append(new PostgreSqlParameter("bundle_id", DbType.String, request.BundleId))
                .Append(new PostgreSqlParameter("receipt_id", DbType.String, request.ReceiptId))
                .Append(new PostgreSqlParameter("receipt_hash", DbType.String, request.ReceiptHash))
                .Append(new PostgreSqlParameter(
                    "receipt_canonical_sha256",
                    DbType.String,
                    request.CanonicalSha256))
                .ToArray());
        return await ReadStoredReceiptRowAsync(
            command,
            request.Scope,
            request.BundleId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PostgreSqlStoredReceipt?> ReadStoredReceiptRowAsync(
        DbCommand command,
        CortexSyncRepositoryScope scope,
        string bundleId,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var receiptId = reader.GetString(0);
        var receiptHash = reader.GetString(1);
        var canonicalSha256 = reader.GetString(2);
        var recordedAt = ReadTimestamp(reader, 3);
        var canonical = reader.GetFieldValue<byte[]>(4);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_receipt_ambiguous",
                scope,
                bundleId);
        var receipt = PostgreSqlSyncCanonicalCodec.DecodeReceipt(
            canonical,
            canonicalSha256,
            scope,
            receiptId);
        if (!string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ReceiptHash, receiptHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleId, bundleId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreId, scope.StoreId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreEpoch, scope.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProjectId, scope.ProjectId, StringComparison.Ordinal) ||
            receipt.Reconciliation.ReadBackAt is null ||
            recordedAt < receipt.Reconciliation.ReadBackAt.Value)
        {
            throw PostgreSqlSyncCanonicalCodec.Failure(
                "cortex_postgresql_sync_receipt_projection_mismatch",
                scope,
                receiptId);
        }
        return new PostgreSqlStoredReceipt(
            receiptId,
            receiptHash,
            canonicalSha256,
            recordedAt,
            canonical);
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(
            reader.GetFieldValue<DateTimeOffset>(ordinal));

    private static PostgreSqlParameter[] CommonParameters(CortexSyncRepositoryScope scope) =>
    [
        new("tenant_id", DbType.String, scope.TenantId),
        new("namespace", DbType.String, scope.Namespace),
        new("store_id", DbType.String, scope.StoreId),
        new("store_epoch", DbType.String, scope.StoreEpoch),
        new("project_id", DbType.String, scope.ProjectId)
    ];
}

internal sealed record PostgreSqlStoredSyncCommit(
    CortexSyncRepositoryScope Scope,
    string AttemptId,
    CortexOfflineBundleV1 Bundle,
    byte[] BundleCanonical,
    string BundleCanonicalSha256,
    CortexSyncCommitDisposition Disposition,
    string CommitId,
    string StorageRevision,
    DateTimeOffset ReceivedAt,
    DateTimeOffset CommittedAt,
    string DurableStateHash,
    IReadOnlyList<CortexSyncReceiptOutcomeV1> Outcomes,
    PostgreSqlSyncSourceHead? SourceHead,
    PostgreSqlStoredMigrationPage? MigrationPage,
    CortexSyncCommitSnapshot Snapshot);

internal sealed record PostgreSqlCurrentSourceHead(
    string StorageRevision,
    string BundleId,
    string BundleHash,
    long SourceSequence,
    string EventId,
    string EventHash,
    string AttemptId,
    string CommitId);

internal sealed record PostgreSqlStoredMigrationPage(
    CortexLegacyMigrationPageV1 Page,
    byte[] Canonical,
    string CanonicalSha256);

internal sealed record PostgreSqlStoredReceipt(
    string ReceiptId,
    string ReceiptHash,
    string CanonicalSha256,
    DateTimeOffset RecordedAt,
    byte[] Canonical);

internal sealed record PostgreSqlMigrationPredecessor(
    CortexLegacyMigrationPageV1 Page,
    string StorageRevision,
    CortexSyncReceiptV1 Receipt);
