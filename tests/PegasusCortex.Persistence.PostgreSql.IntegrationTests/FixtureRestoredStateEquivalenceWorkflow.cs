using System.Data;
using Npgsql;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Establishes read-only state equivalence for a database presented as a restored candidate.
/// It does not prove that pg_dump, pg_restore, or any other backup operation ran. Pegasus Server
/// owns the operation receipt, target creation, restore operation, and cleanup outside this process.
/// </summary>
internal static class FixtureRestoredStateEquivalenceWorkflow
{
    public static async Task RunAsync(
        FixtureConfiguration configuration,
        FixtureBackupRestoreProofV1 proof,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(reportPass);
        cancellationToken.ThrowIfCancellationRequested();
        FixtureAssert.True(
            configuration.BackupRestoreMode == FixtureBackupRestoreMode.VerifyRestoredState,
            "Restored-state equivalence ran without its exact mode.");
        FixtureBackupRestoreProofContract.EnsureValidForRestore(proof, configuration.DatabaseName);

        // Validate and bound the private-CA material before opening either loopback socket.
        configuration.PostgreSqlOptions.ValidateConfiguration();
        await using (var migrationDataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.MigrationConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-restore-migration",
            readOnlySession: true))
        {
            await LivePostgreSqlFixture.VerifyMigrationConnectionAsync(
                    migrationDataSource,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            var ledger = await LivePostgreSqlFixture.ReadMigrationLedgerAsync(
                    migrationDataSource,
                    cancellationToken)
                .ConfigureAwait(false);
            var expectedLedger = proof.Migrations
                .Select(value => (value.MigrationId, value.UpSha256));
            FixtureAssert.SequenceEqual(
                expectedLedger,
                ledger,
                "The candidate migration ledger does not match the captured source proof.");
        }
        reportPass("candidate TLS, owner identity, and exact migration ledger are equivalent");

        await LivePostgreSqlFixture.VerifyRuntimeConnectionAsync(
                configuration,
                readOnlySession: true,
                cancellationToken)
            .ConfigureAwait(false);

        await using var runtimeDataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-restore-runtime",
            readOnlySession: true);
        var transactions = new PostgreSqlTransactionRunner(runtimeDataSource);
        var scope = FixtureSyncContractFactory.Scope();
        await FixtureSyncLiveWorkflow.VerifySyncReadinessAsync(
                transactions,
                scope,
                TimeProvider.System,
                cancellationToken)
            .ConfigureAwait(false);
        reportPass("candidate runtime role, forced RLS, and catalog state are equivalent");

        var repository = new PostgreSqlCortexSyncRepository(transactions, TimeProvider.System);
        await VerifyAttemptsAsync(repository, scope, proof.Attempts, cancellationToken)
            .ConfigureAwait(false);
        await VerifyReceiptAsync(repository, scope, proof.Receipt, cancellationToken)
            .ConfigureAwait(false);
        reportPass("candidate durable attempts and canonical receipt are equivalent");

        await VerifySourceHeadAsync(runtimeDataSource, scope, proof.SourceHead, cancellationToken)
            .ConfigureAwait(false);
        reportPass("candidate source-head continuation leaf is equivalent");
    }

    private static async Task VerifyAttemptsAsync(
        PostgreSqlCortexSyncRepository repository,
        CortexSyncRepositoryScope scope,
        IReadOnlyList<FixtureDurableAttemptProofV1> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var attempt in attempts)
        {
            var readBack = await repository.ReadBackAsync(
                    new CortexSyncReadBackRequest(
                        scope,
                        attempt.BundleId,
                        attempt.BundleHash,
                        attempt.CommitId,
                        attempt.StorageRevision),
                    cancellationToken)
                .ConfigureAwait(false);
            FixtureAssert.True(
                readBack.SourceEventCount == attempt.SourceEventCount &&
                readBack.MatchedEventCount == attempt.SourceEventCount &&
                string.Equals(readBack.ReadBackHash, attempt.DurableStateHash, StringComparison.Ordinal),
                "A candidate durable attempt does not match the captured source proof.");
        }
    }

    private static async Task VerifyReceiptAsync(
        PostgreSqlCortexSyncRepository repository,
        CortexSyncRepositoryScope scope,
        FixtureReceiptProofV1 expected,
        CancellationToken cancellationToken)
    {
        var found = await repository.FindReceiptAsync(
                new CortexSyncReceiptLookupRequest(
                    scope,
                    expected.BundleId,
                    expected.BundleHash,
                    expected.CommitId,
                    expected.StorageRevision),
                cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.True(found is not null, "The candidate canonical receipt was not found.");
        AssertReceiptBytes(found!, expected);

        var read = await repository.ReadReceiptAsync(
                new CortexSyncReceiptReadRequest(
                    scope,
                    expected.BundleId,
                    expected.ReceiptId,
                    expected.ReceiptHash,
                    expected.CanonicalReceiptSha256),
                cancellationToken)
            .ConfigureAwait(false);
        AssertReceiptBytes(read, expected);

        var receipt = CortexCanonicalJson.Deserialize<CortexSyncReceiptV1>(read.CanonicalReceipt.Span);
        FixtureAssert.True(
            string.Equals(receipt.BundleId, expected.BundleId, StringComparison.Ordinal) &&
            string.Equals(receipt.BundleHash, expected.BundleHash, StringComparison.Ordinal) &&
            string.Equals(receipt.ReceiptId, expected.ReceiptId, StringComparison.Ordinal) &&
            string.Equals(receipt.ReceiptHash, expected.ReceiptHash, StringComparison.Ordinal) &&
            string.Equals(receipt.Durability.CommitId, expected.CommitId, StringComparison.Ordinal) &&
            string.Equals(receipt.Durability.StorageRevision, expected.StorageRevision, StringComparison.Ordinal) &&
            CortexSyncContractPolicy.HasValidSemanticHash(receipt),
            "The candidate canonical receipt contract does not match its proof identity.");
        using var issuer = new FixtureSyncReceiptIssuer();
        FixtureAssert.True(
            await issuer.VerifyProofAsync(receipt, cancellationToken).ConfigureAwait(false),
            "The candidate fixture receipt proof is not valid.");
    }

    private static void AssertReceiptBytes(
        CortexSyncReceiptReadBackSnapshot value,
        FixtureReceiptProofV1 expected)
    {
        FixtureAssert.True(
            string.Equals(value.BundleId, expected.BundleId, StringComparison.Ordinal) &&
            string.Equals(value.ReceiptId, expected.ReceiptId, StringComparison.Ordinal) &&
            string.Equals(
                CortexCanonicalJson.Sha256(value.CanonicalReceipt.Span),
                expected.CanonicalReceiptSha256,
                StringComparison.Ordinal),
            "The candidate canonical receipt bytes do not match the captured source proof.");
    }

    private static async Task VerifySourceHeadAsync(
        NpgsqlDataSource dataSource,
        CortexSyncRepositoryScope scope,
        FixtureSourceHeadProofV1 expected,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);
        await FixtureSyncLiveWorkflow.BindSyncContextAsync(
                connection,
                transaction,
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                head_value.base_server_revision,
                head_value.bundle_id,
                head_value.committed_bundle_hash,
                head_value.commit_id,
                head_value.storage_revision,
                head_value.committed_source_sequence,
                head_value.committed_event_id,
                head_value.committed_chain_hash
            FROM cortex_sync.journal_source_head AS head_value
            WHERE head_value.tenant_id = @tenant_id
              AND head_value.namespace_name = @namespace
              AND head_value.store_id = @store_id
              AND head_value.store_epoch = @store_epoch
              AND head_value.project_id = @project_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM cortex_sync.journal_source_head AS child
                  WHERE child.tenant_id = head_value.tenant_id
                    AND child.namespace_name = head_value.namespace_name
                    AND child.store_id = head_value.store_id
                    AND child.store_epoch = head_value.store_epoch
                    AND child.project_id = head_value.project_id
                    AND child.base_server_revision = head_value.storage_revision
              )
            ORDER BY head_value.committed_source_sequence DESC
            LIMIT 2;
            """,
            connection,
            transaction);
        FixtureSyncLiveWorkflow.AddScopeParameters(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            string.Equals(reader.GetString(0), expected.BaseServerRevision, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), expected.BundleId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), expected.BundleHash, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(3), expected.CommitId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(4), expected.StorageRevision, StringComparison.Ordinal) &&
            reader.GetInt64(5) == expected.SourceSequence &&
            string.Equals(reader.GetString(6), expected.EventId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(7), expected.EventHash, StringComparison.Ordinal) &&
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The candidate current source-head leaf does not match the captured source proof.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
