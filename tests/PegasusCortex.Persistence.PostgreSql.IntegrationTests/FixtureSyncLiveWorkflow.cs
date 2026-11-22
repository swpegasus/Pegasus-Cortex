using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Sync;
using Npgsql;
using NpgsqlTypes;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Exercises the authenticated runtime provider after the migration and privilege fixture owns
/// a complete schema. No base-Service readiness claim is made by these sync-only assertions.
/// </summary>
internal static class FixtureSyncLiveWorkflow
{
    private const string IdentityConflictCode = "cortex_sync_identity_conflict";
    private const string HeadConflictCode = "cortex_sync_head_conflict";
    private const string DeferredAttemptId = "fixture_deferred_integrity";

    public static async Task<FixtureSyncWorkflowEvidence> RunAsync(
        FixtureConfiguration configuration,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(reportPass);
        cancellationToken.ThrowIfCancellationRequested();

        var observedAt = TruncateToSecond(DateTimeOffset.UtcNow);
        var clock = new FixtureFixedTimeProvider(observedAt);
        var origin = FixtureSyncContractFactory.Origin(observedAt);
        IReadOnlyList<FixtureDurableAttemptProofV1> restartTargets;
        FixtureReceiptProofV1 receiptProof;
        FixtureSourceHeadProofV1 sourceHeadProof;

        await using (var dataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-sync"))
        {
            var transactions = new PostgreSqlTransactionRunner(dataSource);
            var repository = new PostgreSqlCortexSyncRepository(transactions, clock);
            var scope = FixtureSyncContractFactory.Scope();

            await VerifySyncReadinessAsync(transactions, scope, clock, cancellationToken)
                .ConfigureAwait(false);
            reportPass("sync schema, privileges, forced RLS, and catalog contract readiness");

            using var issuer = new FixtureSyncReceiptIssuer();
            var service = new CortexSyncApplicationService(repository, issuer, clock);
            var command = new SubmitCortexSyncBundleCommand(
                FixtureSyncContractFactory.Namespace,
                origin,
                FixtureSyncContractFactory.Authority());
            var initial = await service.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
            var receipt = RequireAcceptedReceipt(initial, CortexSyncCommitDisposition.Committed);

            var retry = await service.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
            var recoveredReceipt = RequireAcceptedReceipt(retry, CortexSyncCommitDisposition.Committed);
            var canonicalReceipt = CortexCanonicalJson.Serialize(receipt);
            FixtureAssert.SequenceEqual(
                canonicalReceipt,
                CortexCanonicalJson.Serialize(recoveredReceipt),
                "An exact lost-response retry did not recover the original canonical receipt.");
            FixtureAssert.Equal(
                1,
                issuer.IssueCount,
                "Lost-response recovery issued a second receipt instead of finding the durable one.");

            await VerifyReceiptRepositoryBoundaryAsync(
                    repository,
                    scope,
                    receipt,
                    canonicalReceipt,
                    cancellationToken)
                .ConfigureAwait(false);
            receiptProof = new FixtureReceiptProofV1(
                origin.BundleId,
                origin.BundleHash,
                receipt.Durability.CommitId,
                receipt.Durability.StorageRevision,
                receipt.ReceiptId,
                receipt.ReceiptHash,
                CortexCanonicalJson.Sha256(canonicalReceipt));
            reportPass("initial commit, exact retry, receipt append/find/read, and lost-response recovery");

            var identityCollision = FixtureSyncContractFactory.Continue(
                origin,
                receipt.Durability.StorageRevision,
                origin.BundleId,
                "event-identity-collision",
                observedAt);
            var identityCommit = await CommitAsync(repository, scope, identityCollision, observedAt, cancellationToken)
                .ConfigureAwait(false);
            AssertConflict(identityCommit, IdentityConflictCode, "accepted identity collision");

            var staleOrigin = FixtureSyncContractFactory.CreateHeadConflict(observedAt);
            var headConflict = await CommitAsync(repository, scope, staleOrigin, observedAt, cancellationToken)
                .ConfigureAwait(false);
            AssertConflict(headConflict, HeadConflictCode, "stale source head");

            var continuation = FixtureSyncContractFactory.Continue(
                origin,
                receipt.Durability.StorageRevision,
                "bundle-continuation",
                "event-continuation",
                observedAt);
            var continuationCommit = await CommitAsync(
                    repository,
                    scope,
                    continuation,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            FixtureAssert.True(
                continuationCommit.Disposition == CortexSyncCommitDisposition.Committed,
                "A valid source-head continuation was not committed.");

            var branches = new[]
            {
                FixtureSyncContractFactory.Continue(
                    continuation,
                    continuationCommit.StorageRevision,
                    "bundle-branch-a",
                    "event-branch-a",
                    observedAt),
                FixtureSyncContractFactory.Continue(
                    continuation,
                    continuationCommit.StorageRevision,
                    "bundle-branch-b",
                    "event-branch-b",
                    observedAt)
            };
            var branchCommits = await CommitConcurrentContinuationsAsync(
                    configuration,
                    repository,
                    scope,
                    branches,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            FixtureAssert.Equal(
                1,
                branchCommits.Count(value => value.Disposition == CortexSyncCommitDisposition.Committed),
                "Concurrent continuations did not produce exactly one committed winner.");
            FixtureAssert.Equal(
                1,
                branchCommits.Count(value => value.Disposition == CortexSyncCommitDisposition.Conflict),
                "Concurrent continuations did not retain exactly one durable conflict.");
            var losingIndex = Array.FindIndex(
                branchCommits,
                value => value.Disposition == CortexSyncCommitDisposition.Conflict);
            AssertConflict(branchCommits[losingIndex], HeadConflictCode, "concurrent continuation loser");
            var winningIndex = Array.FindIndex(
                branchCommits,
                value => value.Disposition == CortexSyncCommitDisposition.Committed);
            var winningBundle = branches[winningIndex];
            var winningCommit = branchCommits[winningIndex];
            sourceHeadProof = new FixtureSourceHeadProofV1(
                continuationCommit.StorageRevision,
                winningBundle.BundleId,
                winningBundle.BundleHash,
                winningCommit.CommitId,
                winningCommit.StorageRevision,
                winningBundle.LastSequence,
                winningBundle.Events[^1].EventId,
                winningBundle.Events[^1].EventHash);
            reportPass("identity conflict, head conflict, valid continuation, and one-winner concurrency");

            await VerifyCrossScopeRlsAsync(
                    dataSource,
                    scope,
                    origin,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyDeferredIntegrityRollbackAsync(
                    dataSource,
                    scope,
                    origin,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            reportPass("tenant/namespace RLS denial and multi-table deferred-integrity rollback");

            restartTargets =
            [
                ReadBackTarget(
                    origin,
                    CortexSyncCommitDisposition.Committed,
                    receipt.Durability.CommitId,
                    receipt.Durability.StorageRevision,
                    receipt.Reconciliation.ReadBackHash!),
                ReadBackTarget(
                    identityCollision,
                    identityCommit.Disposition,
                    identityCommit.CommitId,
                    identityCommit.StorageRevision,
                    identityCommit.DurableStateHash),
                ReadBackTarget(
                    staleOrigin,
                    headConflict.Disposition,
                    headConflict.CommitId,
                    headConflict.StorageRevision,
                    headConflict.DurableStateHash),
                ReadBackTarget(
                    continuation,
                    continuationCommit.Disposition,
                    continuationCommit.CommitId,
                    continuationCommit.StorageRevision,
                    continuationCommit.DurableStateHash),
                .. branches.Zip(branchCommits, (bundle, commit) => ReadBackTarget(
                    bundle,
                    commit.Disposition,
                    commit.CommitId,
                    commit.StorageRevision,
                    commit.DurableStateHash))
            ];
        }

        await VerifyRestartReadBackAsync(configuration, clock, restartTargets, cancellationToken)
            .ConfigureAwait(false);
        reportPass("runtime-pool restart and exact read-back of all six durable attempts");
        return new FixtureSyncWorkflowEvidence(restartTargets, receiptProof, sourceHeadProof);
    }

    internal static async Task VerifySyncReadinessAsync(
        PostgreSqlTransactionRunner transactions,
        CortexSyncRepositoryScope scope,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var readiness = await new PostgreSqlSyncReadinessProbe(transactions, scope, clock)
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.True(
            readiness.State == PostgreSqlReadinessState.Ready &&
            readiness.MigrationCurrent &&
            readiness.SchemaContractPresent &&
            readiness.RuntimePrivilegesRestricted &&
            readiness.RowLevelSecurityForced &&
            readiness.PolicyContractMatches &&
            readiness.IssueCodes.Count == 0,
            "The migrated runtime provider did not pass the complete sync readiness contract.");
    }

    private static CortexSyncReceiptV1 RequireAcceptedReceipt(
        CortexSyncApplicationResult result,
        CortexSyncCommitDisposition disposition)
    {
        FixtureAssert.True(
            result.Succeeded &&
            result.ReceiptAccepted &&
            result.DurablyReconciled &&
            result.Disposition == disposition &&
            result.Receipt is not null &&
            result.Diagnostics.Count == 0,
            "The synchronization application boundary did not return an accepted reconciled receipt.");
        return result.Receipt!;
    }

    private static async Task VerifyReceiptRepositoryBoundaryAsync(
        PostgreSqlCortexSyncRepository repository,
        CortexSyncRepositoryScope scope,
        CortexSyncReceiptV1 receipt,
        byte[] canonicalReceipt,
        CancellationToken cancellationToken)
    {
        var append = await repository.AppendReceiptAsync(
                new CortexSyncReceiptAppendRequest(scope, receipt, canonicalReceipt),
                cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.True(
            append.Disposition == CortexSyncReceiptAppendDisposition.Existing,
            "An exact terminal receipt append did not resolve to the existing durable row.");

        var found = await repository.FindReceiptAsync(
                new CortexSyncReceiptLookupRequest(
                    scope,
                    receipt.BundleId,
                    receipt.BundleHash,
                    receipt.Durability.CommitId,
                    receipt.Durability.StorageRevision),
                cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.True(found is not null, "The durable receipt lookup returned no row.");
        FixtureAssert.SequenceEqual(
            canonicalReceipt,
            found!.CanonicalReceipt.ToArray(),
            "Receipt lookup did not return the exact canonical bytes.");

        var canonicalSha256 = CortexCanonicalJson.Sha256(canonicalReceipt);
        var read = await repository.ReadReceiptAsync(
                new CortexSyncReceiptReadRequest(
                    scope,
                    receipt.BundleId,
                    receipt.ReceiptId,
                    receipt.ReceiptHash,
                    canonicalSha256),
                cancellationToken)
            .ConfigureAwait(false);
        FixtureAssert.SequenceEqual(
            canonicalReceipt,
            read.CanonicalReceipt.ToArray(),
            "Receipt read-back did not return the exact canonical bytes.");
    }

    private static async Task<CortexSyncCommitSnapshot> CommitAsync(
        PostgreSqlCortexSyncRepository repository,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken) =>
        await repository.CommitAsync(
                new CortexSyncCommitRequest(scope, bundle, receivedAt),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<CortexSyncCommitSnapshot[]> CommitConcurrentContinuationsAsync(
        FixtureConfiguration configuration,
        PostgreSqlCortexSyncRepository repository,
        CortexSyncRepositoryScope scope,
        IReadOnlyList<CortexOfflineBundleV1> bundles,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        await using var lockDataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.MigrationConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-sync-concurrency-gate");
        await using var connection = await lockDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);
        var lockScope = string.Join(
            '\n',
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_scope, 0));",
            connection,
            transaction))
        {
            acquire.Parameters.AddWithValue("lock_scope", NpgsqlDbType.Text, lockScope);
            await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var commits = bundles
            .Select(bundle => CommitAsync(repository, scope, bundle, receivedAt, cancellationToken))
            .ToArray();
        Exception? gateFailure = null;
        try
        {
            await WaitForAdvisoryLockWaitersAsync(
                    connection,
                    transaction,
                    expectedWaiters: bundles.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            FixtureAssert.True(
                commits.All(task => !task.IsCompleted),
                "A continuation completed while the exact repository scope lock remained held.");
        }
        catch (Exception exception)
        {
            gateFailure = exception;
        }

        using (var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await transaction.CommitAsync(releaseTimeout.Token).ConfigureAwait(false);
        }
        CortexSyncCommitSnapshot[] results;
        try
        {
            results = await Task.WhenAll(commits).ConfigureAwait(false);
        }
        catch (Exception commitFailure) when (gateFailure is not null)
        {
            throw new AggregateException(
                "The concurrency gate failed and a released continuation also failed.",
                gateFailure,
                commitFailure);
        }

        if (gateFailure is not null)
            ExceptionDispatchInfo.Capture(gateFailure).Throw();
        return results;
    }

    private static async Task WaitForAdvisoryLockWaitersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int expectedWaiters,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            await using var command = new NpgsqlCommand(
                """
                WITH held AS
                (
                    SELECT locktype, database, classid, objid, objsubid, mode
                    FROM pg_catalog.pg_locks
                    WHERE pid = pg_backend_pid()
                      AND locktype = 'advisory'
                      AND granted
                )
                SELECT COUNT(*)
                FROM pg_catalog.pg_locks waiting
                JOIN held
                  ON held.locktype = waiting.locktype
                 AND held.database IS NOT DISTINCT FROM waiting.database
                 AND held.classid IS NOT DISTINCT FROM waiting.classid
                 AND held.objid IS NOT DISTINCT FROM waiting.objid
                 AND held.objsubid IS NOT DISTINCT FROM waiting.objsubid
                 AND held.mode = waiting.mode
                WHERE NOT waiting.granted;
                """,
                connection,
                transaction);
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? -1L);
            if (count == expectedWaiters)
                return;
            FixtureAssert.True(
                count < expectedWaiters,
                "The concurrency gate observed more repository waiters than it started.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        throw new FixtureAssertionException(
            "Both continuation transactions did not reach the exact held repository scope lock.");
    }

    private static void AssertConflict(
        CortexSyncCommitSnapshot commit,
        string expectedCode,
        string scenario)
    {
        FixtureAssert.True(
            commit.Disposition == CortexSyncCommitDisposition.Conflict &&
            commit.Outcomes.Count > 0 &&
            commit.Outcomes.All(outcome =>
                outcome.Disposition == CortexSyncDispositionIds.Conflict &&
                outcome.Durable &&
                string.Equals(outcome.DiagnosticCode, expectedCode, StringComparison.Ordinal)),
            $"The {scenario} did not retain the expected durable diagnostic.");
    }

    private static async Task VerifyCrossScopeRlsAsync(
        NpgsqlDataSource dataSource,
        CortexSyncRepositoryScope correctScope,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        await VerifyRlsScopeCaseAsync(
                dataSource,
                correctScope,
                FixtureSyncContractFactory.Scope(tenantId: "tenant-fixture-wrong"),
                bundle,
                receivedAt,
                "fixture_rls_wrong_tenant",
                "tenant",
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyRlsScopeCaseAsync(
                dataSource,
                correctScope,
                FixtureSyncContractFactory.Scope(@namespace: "project:fixture-wrong"),
                bundle,
                receivedAt,
                "fixture_rls_wrong_namespace",
                "namespace",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyRlsScopeCaseAsync(
        NpgsqlDataSource dataSource,
        CortexSyncRepositoryScope correctScope,
        CortexSyncRepositoryScope mismatchedScope,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        string attemptId,
        string mismatchKind,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false))
        {
            await BindSyncContextAsync(
                connection,
                transaction,
                mismatchedScope,
                cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT COUNT(*)
                FROM cortex_sync.journal_batch
                WHERE tenant_id = @tenant_id AND namespace_name = @namespace;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, correctScope.TenantId);
            command.Parameters.AddWithValue("namespace", NpgsqlDbType.Text, correctScope.Namespace);
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? -1L);
            FixtureAssert.Equal(
                0L,
                count,
                $"A wrong-{mismatchKind} runtime context observed correct-scope journal rows.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        string? sqlState = null;
        await using (var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false))
        {
            await BindSyncContextAsync(connection, transaction, mismatchedScope, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await InsertIncompleteBatchAsync(
                        connection,
                        transaction,
                        correctScope,
                        bundle,
                        receivedAt,
                        attemptId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                sqlState = exception.SqlState;
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        FixtureAssert.Equal(
            "42501",
            sqlState ?? string.Empty,
            $"A valid wrong-{mismatchKind} INSERT did not fail at the RLS WITH CHECK boundary.");
        await VerifyAttemptAbsentAcrossJournalAsync(
                connection,
                correctScope,
                attemptId,
                $"The denied wrong-{mismatchKind} INSERT left a correct-scope journal row.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyDeferredIntegrityRollbackAsync(
        NpgsqlDataSource dataSource,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        string? integritySqlState = null;
        await using (var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false))
        {
            await BindSyncContextAsync(connection, transaction, scope, cancellationToken)
                .ConfigureAwait(false);
            await InsertIncompleteBatchAsync(
                    connection,
                    transaction,
                    scope,
                    bundle,
                    receivedAt,
                    DeferredAttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertIncompleteEntryAsync(
                    connection,
                    transaction,
                    scope,
                    bundle,
                    DeferredAttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using var force = new NpgsqlCommand(
                    "SET CONSTRAINTS ALL IMMEDIATE;",
                    connection,
                    transaction);
                await force.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                integritySqlState = exception.SqlState;
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        FixtureAssert.Equal(
            PostgresErrorCodes.CheckViolation,
            integritySqlState ?? string.Empty,
            "The incomplete sync attempt did not fail at the deferred integrity boundary.");

        await VerifyAttemptAbsentAcrossJournalAsync(
                connection,
                scope,
                DeferredAttemptId,
                "A failed deferred-integrity attempt left a partial journal row.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InsertIncompleteBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        DateTimeOffset receivedAt,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var canonical = CortexCanonicalJson.Serialize(bundle);
        var extensions = CortexCanonicalJson.Serialize(bundle.Extensions);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO cortex_sync.journal_batch
            (
                tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id,
                advances_source_head, bundle_id, bundle_schema_version, bundle_contract_id,
                canonicalization, base_server_revision, previous_bundle_hash, prior_event_hash,
                first_source_sequence, last_source_sequence, entry_count, bundle_hash, created_at,
                received_at, authority_id, subject_id, service_id, policy_revision,
                extensions_canonical, bundle_canonical, bundle_canonical_sha256
            )
            VALUES
            (
                @tenant_id, @namespace, @store_id, @store_epoch, @project_id, @attempt_id,
                false, @bundle_id, @bundle_schema_version, @bundle_contract_id,
                @canonicalization, @base_server_revision, @previous_bundle_hash, @prior_event_hash,
                @first_source_sequence, @last_source_sequence, @entry_count, @bundle_hash, @created_at,
                @received_at, @authority_id, @subject_id, @service_id, @policy_revision,
                @extensions_canonical, @bundle_canonical, @bundle_canonical_sha256
            );
            """,
            connection,
            transaction);
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Text, attemptId);
        command.Parameters.AddWithValue("bundle_id", NpgsqlDbType.Text, bundle.BundleId);
        command.Parameters.AddWithValue("bundle_schema_version", NpgsqlDbType.Integer, bundle.SchemaVersion);
        command.Parameters.AddWithValue("bundle_contract_id", NpgsqlDbType.Text, bundle.ContractId);
        command.Parameters.AddWithValue("canonicalization", NpgsqlDbType.Text, bundle.Canonicalization);
        command.Parameters.Add(new NpgsqlParameter("base_server_revision", NpgsqlDbType.Text)
        {
            Value = bundle.BaseServerRevision is null ? DBNull.Value : bundle.BaseServerRevision
        });
        command.Parameters.AddWithValue("previous_bundle_hash", NpgsqlDbType.Text, bundle.PreviousBundleHash);
        command.Parameters.AddWithValue("prior_event_hash", NpgsqlDbType.Text, bundle.PriorEventHash);
        command.Parameters.AddWithValue("first_source_sequence", NpgsqlDbType.Bigint, bundle.FirstSequence);
        command.Parameters.AddWithValue("last_source_sequence", NpgsqlDbType.Bigint, bundle.LastSequence);
        command.Parameters.AddWithValue("entry_count", NpgsqlDbType.Integer, bundle.Events.Count);
        command.Parameters.AddWithValue("bundle_hash", NpgsqlDbType.Text, bundle.BundleHash);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, bundle.CreatedAt);
        command.Parameters.AddWithValue("received_at", NpgsqlDbType.TimestampTz, receivedAt);
        command.Parameters.AddWithValue("extensions_canonical", NpgsqlDbType.Bytea, extensions);
        command.Parameters.AddWithValue("bundle_canonical", NpgsqlDbType.Bytea, canonical);
        command.Parameters.AddWithValue(
            "bundle_canonical_sha256",
            NpgsqlDbType.Text,
            CortexCanonicalJson.Sha256(canonical));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertIncompleteEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CortexSyncRepositoryScope scope,
        CortexOfflineBundleV1 bundle,
        string attemptId,
        CancellationToken cancellationToken)
    {
        FixtureAssert.Equal(
            1,
            bundle.Events.Count,
            "The partial-attempt fixture requires exactly one canonical event.");
        var item = bundle.Events[0];
        var payload = DecodeBase64Url(item.PayloadBase64Url);
        var sourceCanonical = CortexCanonicalJson.Serialize(item.Source);
        var extensionsCanonical = CortexCanonicalJson.Serialize(item.Extensions);
        var eventCanonical = CortexCanonicalJson.Serialize(item);
        await using var command = new NpgsqlCommand(
            """
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
            (
                @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
                @attempt_id, false, @bundle_id,
                @bundle_first_source_sequence, @bundle_last_source_sequence,
                @source_sequence, @event_id, @event_schema_version, @event_contract_id,
                @canonicalization, @previous_event_hash, @event_hash, @occurred_at,
                @producer_instance_id, @operation_id, @tenant_id_hint, @namespace_hint,
                @observed_policy_revision, @payload_contract_id, @payload, @payload_bytes,
                @payload_sha256, @source_kind, @source_root_id, @source_relative_path,
                @source_bytes, @source_sha256, @source_captured_at,
                @source_provenance_canonical, @content_treatment, @executable,
                @extensions_canonical, @event_canonical, @event_canonical_sha256
            );
            """,
            connection,
            transaction);
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Text, attemptId);
        command.Parameters.AddWithValue("bundle_id", NpgsqlDbType.Text, bundle.BundleId);
        command.Parameters.AddWithValue(
            "bundle_first_source_sequence",
            NpgsqlDbType.Bigint,
            bundle.FirstSequence);
        command.Parameters.AddWithValue(
            "bundle_last_source_sequence",
            NpgsqlDbType.Bigint,
            bundle.LastSequence);
        command.Parameters.AddWithValue("source_sequence", NpgsqlDbType.Bigint, item.Sequence);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, item.EventId);
        command.Parameters.AddWithValue("event_schema_version", NpgsqlDbType.Integer, item.SchemaVersion);
        command.Parameters.AddWithValue("event_contract_id", NpgsqlDbType.Text, item.ContractId);
        command.Parameters.AddWithValue("canonicalization", NpgsqlDbType.Text, item.Canonicalization);
        command.Parameters.AddWithValue("previous_event_hash", NpgsqlDbType.Text, item.PreviousEventHash);
        command.Parameters.AddWithValue("event_hash", NpgsqlDbType.Text, item.EventHash);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, item.OccurredAt);
        command.Parameters.AddWithValue("producer_instance_id", NpgsqlDbType.Text, item.ProducerInstanceId);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Text, item.OperationId);
        command.Parameters.AddWithValue("tenant_id_hint", NpgsqlDbType.Text, item.TenantIdHint);
        command.Parameters.AddWithValue("namespace_hint", NpgsqlDbType.Text, item.NamespaceHint);
        command.Parameters.Add(new NpgsqlParameter("observed_policy_revision", NpgsqlDbType.Bigint)
        {
            Value = item.ObservedPolicyRevision is null ? DBNull.Value : item.ObservedPolicyRevision.Value
        });
        command.Parameters.AddWithValue("payload_contract_id", NpgsqlDbType.Text, item.PayloadContractId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload);
        command.Parameters.AddWithValue("payload_bytes", NpgsqlDbType.Integer, item.PayloadBytes);
        command.Parameters.AddWithValue("payload_sha256", NpgsqlDbType.Text, item.PayloadSha256);
        command.Parameters.AddWithValue("source_kind", NpgsqlDbType.Text, item.Source.SourceKind);
        command.Parameters.Add(new NpgsqlParameter("source_root_id", NpgsqlDbType.Text)
        {
            Value = item.Source.SourceRootId is null ? DBNull.Value : item.Source.SourceRootId
        });
        command.Parameters.Add(new NpgsqlParameter("source_relative_path", NpgsqlDbType.Text)
        {
            Value = item.Source.SourceRelativePath is null ? DBNull.Value : item.Source.SourceRelativePath
        });
        command.Parameters.AddWithValue("source_bytes", NpgsqlDbType.Bigint, item.Source.SourceBytes);
        command.Parameters.AddWithValue("source_sha256", NpgsqlDbType.Text, item.Source.SourceSha256);
        command.Parameters.AddWithValue(
            "source_captured_at",
            NpgsqlDbType.TimestampTz,
            item.Source.CapturedAt);
        command.Parameters.AddWithValue(
            "source_provenance_canonical",
            NpgsqlDbType.Bytea,
            sourceCanonical);
        command.Parameters.AddWithValue("content_treatment", NpgsqlDbType.Text, item.ContentTreatment);
        command.Parameters.AddWithValue("executable", NpgsqlDbType.Boolean, item.Executable);
        command.Parameters.AddWithValue("extensions_canonical", NpgsqlDbType.Bytea, extensionsCanonical);
        command.Parameters.AddWithValue("event_canonical", NpgsqlDbType.Bytea, eventCanonical);
        command.Parameters.AddWithValue(
            "event_canonical_sha256",
            NpgsqlDbType.Text,
            CortexCanonicalJson.Sha256(eventCanonical));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyAttemptAbsentAcrossJournalAsync(
        NpgsqlConnection connection,
        CortexSyncRepositoryScope scope,
        string attemptId,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);
        await BindSyncContextAsync(connection, transaction, scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM cortex_sync.journal_batch WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id),
                (SELECT COUNT(*) FROM cortex_sync.journal_entry WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id),
                (SELECT COUNT(*) FROM cortex_sync.journal_commit WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id),
                (SELECT COUNT(*) FROM cortex_sync.journal_outcome WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id),
                (SELECT COUNT(*) FROM cortex_sync.journal_source_head WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id),
                (SELECT COUNT(*) FROM cortex_sync.journal_ack WHERE
                    tenant_id = @tenant_id AND namespace_name = @namespace AND
                    store_id = @store_id AND store_epoch = @store_epoch AND
                    project_id = @project_id AND attempt_id = @attempt_id);
            """,
            connection,
            transaction);
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Text, attemptId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            FixtureAssert.True(
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                Enumerable.Range(0, 6).All(index => reader.GetInt64(index) == 0) &&
                !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
                failureMessage);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task BindSyncContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CortexSyncRepositoryScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                set_config('pegasus.cortex.tenant_id', @tenant_id, true),
                set_config('pegasus.cortex.namespace', @namespace, true),
                set_config('pegasus.cortex.authority_id', @authority_id, true),
                set_config('pegasus.cortex.subject_id', @subject_id, true),
                set_config('pegasus.cortex.service_id', @service_id, true),
                set_config('pegasus.cortex.policy_revision', @policy_revision_text, true),
                set_config('pegasus.cortex.capability', 'sync', true),
                set_config('pegasus.cortex.operation', @operation, true),
                set_config('pegasus.cortex.maximum_sensitivity', 'restricted', true),
                set_config('pegasus.cortex.allow_stale', 'false', true),
                set_config('pegasus.cortex.allow_blocked_or_unverified', 'false', true);
            """,
            connection,
            transaction);
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue(
            "policy_revision_text",
            NpgsqlDbType.Text,
            scope.PolicyRevision.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, scope.Operation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void AddScopeParameters(NpgsqlCommand command, CortexSyncRepositoryScope scope)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, scope.TenantId);
        command.Parameters.AddWithValue("namespace", NpgsqlDbType.Text, scope.Namespace);
        command.Parameters.AddWithValue("store_id", NpgsqlDbType.Text, scope.StoreId);
        command.Parameters.AddWithValue("store_epoch", NpgsqlDbType.Text, scope.StoreEpoch);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, scope.ProjectId);
        command.Parameters.AddWithValue("authority_id", NpgsqlDbType.Text, scope.AuthorityId);
        command.Parameters.AddWithValue("subject_id", NpgsqlDbType.Text, scope.SubjectId);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, scope.ServiceId);
        command.Parameters.AddWithValue("policy_revision", NpgsqlDbType.Bigint, scope.PolicyRevision);
    }

    private static async Task VerifyRestartReadBackAsync(
        FixtureConfiguration configuration,
        TimeProvider clock,
        IReadOnlyList<FixtureDurableAttemptProofV1> targets,
        CancellationToken cancellationToken)
    {
        await using var dataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-pg-it-sync-restart");
        var repository = new PostgreSqlCortexSyncRepository(
            new PostgreSqlTransactionRunner(dataSource),
            clock);
        var scope = FixtureSyncContractFactory.Scope();
        foreach (var target in targets)
        {
            var readBack = await repository.ReadBackAsync(
                    new CortexSyncReadBackRequest(
                        scope,
                        target.BundleId,
                        target.BundleHash,
                        target.CommitId,
                        target.StorageRevision),
                    cancellationToken)
                .ConfigureAwait(false);
            FixtureAssert.True(
                readBack.SourceEventCount == target.SourceEventCount &&
                readBack.MatchedEventCount == target.SourceEventCount &&
                string.Equals(readBack.ReadBackHash, target.DurableStateHash, StringComparison.Ordinal),
                "Restart read-back did not reproduce the exact durable attempt evidence.");
        }
        FixtureAssert.Equal(6, targets.Count, "The restart proof omitted a durable sync attempt.");
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("The fixture event payload is not valid base64url.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static FixtureDurableAttemptProofV1 ReadBackTarget(
        CortexOfflineBundleV1 bundle,
        CortexSyncCommitDisposition disposition,
        string commitId,
        string storageRevision,
        string durableStateHash) =>
        new(
            bundle.BundleId,
            bundle.BundleHash,
            commitId,
            storageRevision,
            Disposition(disposition),
            bundle.Events.Count,
            durableStateHash);

    private static string Disposition(CortexSyncCommitDisposition value) => value switch
    {
        CortexSyncCommitDisposition.Committed => "committed",
        CortexSyncCommitDisposition.Existing => "existing",
        CortexSyncCommitDisposition.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private sealed class FixtureFixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
