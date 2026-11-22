using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Shared.Diagnostics;

internal static class SyncApplicationServiceTests
{
    private const string TargetNamespace = "project:project-1";
    private const string PayloadContractId = "pegasus.cortex.test-payload/1.0";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-14T20:00:00Z");

    public static async Task RunAsync()
    {
        await AuthorityDenialAsync();
        await MigrationPageDelegationAsync();
        await ServerAuthoritativeMigrationCursorAsync();
        await TamperBeforeRepositoryAsync();
        await CommitReadBackOrderAsync();
        await LostResponseRetryRecoversReceiptAsync();
        await ConcurrentReceiptAppendRecoveryAsync();
        await ExistingBundleAsync();
        await ConflictAsync();
        await ReadBackMismatchAsync();
        await BadIssuerReceiptAsync();
        await ReceiptPersistenceFailureAsync();
        await CancellationAndExceptionsAsync();
    }

    public static Task PublishedRepositoryCompatibilityAsync()
    {
        var originalMethods = typeof(ICortexSyncPublishedHeadRepository)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly);
        SyncAssert.Equal(1, originalMethods.Length);
        SyncAssert.Equal("FindLatestPublishedAsync", originalMethods[0].Name);
        SyncAssert.True(typeof(ICortexSyncPublishedHeadRepository).IsAssignableFrom(
            typeof(ICortexSyncPublishedHeadRepositoryV2)));
        var extensionMethods = typeof(ICortexSyncPublishedHeadRepositoryV2)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly);
        SyncAssert.Equal(1, extensionMethods.Length);
        SyncAssert.Equal("FindPublishedAsync", extensionMethods[0].Name);
        return Task.CompletedTask;
    }

    private static async Task MigrationPageDelegationAsync()
    {
        var acceptedFixture = new SyncFixture();
        var page = SyncSamples.MigrationPage(Now);
        var accepted = await acceptedFixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, page, acceptedFixture.Authority),
            CancellationToken.None);
        SyncAssert.True(accepted.Succeeded);
        SyncAssert.Equal(1, acceptedFixture.Repository.CommitCount);
        SyncAssert.Equal(page.Bundle.BundleHash, accepted.Receipt!.BundleHash);

        var rejectedFixture = new SyncFixture();
        var tampered = new CortexLegacyMigrationPageV1(
            page.SchemaVersion,
            page.ContractId,
            page.Canonicalization,
            page.PageId,
            page.SourceInventory,
            page.CurrentCursor,
            page.Bundle,
            page.NextCursor,
            page.AcknowledgmentRequired,
            page.SourceMutationAllowed,
            page.ContentTreatment,
            page.Executable,
            page.Extensions,
            new string('f', 64));
        var rejected = await rejectedFixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, tampered, rejectedFixture.Authority),
            CancellationToken.None);
        SyncAssert.False(rejected.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_migration_page_invalid", rejected.Diagnostics);
        SyncAssert.Equal(0, rejectedFixture.Repository.CommitCount);

        // A caller-controlled cursor can still be canonically hash-sealed while lying about
        // terminal progress. The application boundary must reject that semantic mismatch before
        // the bundle reaches durable storage.
        var prematureCursor = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            page.NextCursor.SchemaVersion,
            page.NextCursor.ContractId,
            page.NextCursor.Canonicalization,
            page.NextCursor.MigrationId,
            page.NextCursor.SourceManifestHash,
            page.NextCursor.SourceInventoryHash,
            page.NextCursor.SourceFileCount,
            page.NextCursor.StoreId,
            page.NextCursor.StoreEpoch,
            page.NextCursor.ProjectId,
            page.NextCursor.SourceRootId,
            page.NextCursor.PageNumber,
            page.NextCursor.NextFileIndex,
            page.NextCursor.NextChunkIndex,
            page.NextCursor.NextSequence,
            page.NextCursor.PreviousBundleHash,
            page.NextCursor.PriorEventHash,
            false,
            page.NextCursor.ContentTreatment,
            page.NextCursor.Executable,
            page.NextCursor.Extensions,
            CortexSyncContractIds.EmptySha256));
        var semanticPlaceholder = new CortexLegacyMigrationPageV1(
            page.SchemaVersion,
            page.ContractId,
            page.Canonicalization,
            "legacy-page-premature-completion",
            page.SourceInventory,
            page.CurrentCursor,
            page.Bundle,
            prematureCursor,
            page.AcknowledgmentRequired,
            page.SourceMutationAllowed,
            page.ContentTreatment,
            page.Executable,
            page.Extensions,
            CortexSyncContractIds.EmptySha256);
        var canonicalSemanticHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(semanticPlaceholder));
        var semanticMismatch = new CortexLegacyMigrationPageV1(
            semanticPlaceholder.SchemaVersion,
            semanticPlaceholder.ContractId,
            semanticPlaceholder.Canonicalization,
            semanticPlaceholder.PageId,
            semanticPlaceholder.SourceInventory,
            semanticPlaceholder.CurrentCursor,
            semanticPlaceholder.Bundle,
            semanticPlaceholder.NextCursor,
            semanticPlaceholder.AcknowledgmentRequired,
            semanticPlaceholder.SourceMutationAllowed,
            semanticPlaceholder.ContentTreatment,
            semanticPlaceholder.Executable,
            semanticPlaceholder.Extensions,
            canonicalSemanticHash);
        SyncAssert.Equal(
            canonicalSemanticHash,
            CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(semanticPlaceholder)));
        var semanticFixture = new SyncFixture();
        var semanticRejected = await semanticFixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, semanticMismatch, semanticFixture.Authority),
            CancellationToken.None);
        SyncAssert.False(semanticRejected.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_migration_page_invalid", semanticRejected.Diagnostics);
        SyncAssert.Equal(0, semanticFixture.Repository.CommitCount);

        var conflictFixture = new SyncFixture
        {
            Repository =
            {
                Disposition = CortexSyncCommitDisposition.Conflict,
                ConflictOutcome = CortexSyncDispositionIds.Quarantined
            }
        };
        var conflict = await conflictFixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, page, conflictFixture.Authority),
            CancellationToken.None);
        SyncAssert.False(conflict.Succeeded);
        SyncAssert.True(conflict.ReceiptAccepted);
        SyncAssert.False(conflict.DurablyReconciled);
        SyncAssert.Equal(CortexSyncDispositionIds.Quarantined, conflict.Receipt!.Outcomes.Single().Disposition);
    }

    private static async Task AuthorityDenialAsync()
    {
        var fixture = new SyncFixture(canSync: false);
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.False(result.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_access_denied", result.Diagnostics);
        SyncAssert.Equal(0, fixture.Repository.CommitCount);
        SyncAssert.Equal(0, fixture.Issuer.IssueCount);
    }

    private static async Task ServerAuthoritativeMigrationCursorAsync()
    {
        var pages = SyncSamples.MigrationPages(Now);

        // A migration page must never fall back to the generic bundle API because that would
        // discard its cursor and inventory proof before the durable boundary.
        var missingAuthorityFixture = new SyncFixture();
        var genericOnly = new SyncGenericOnlyRepository(missingAuthorityFixture.Repository);
        var missingAuthorityService = new CortexSyncApplicationService(
            genericOnly,
            missingAuthorityFixture.Issuer,
            missingAuthorityFixture.Clock);
        var missingAuthority = await missingAuthorityService.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(
                TargetNamespace,
                pages[0],
                missingAuthorityFixture.Authority),
            CancellationToken.None);
        SyncAssert.False(missingAuthority.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_migration_page_authority_unavailable", missingAuthority.Diagnostics);
        SyncAssert.False(missingAuthority.Diagnostics.Single().Retryable);
        SyncAssert.Equal(0, missingAuthorityFixture.Repository.CommitCount);

        var skippedPrefix = new SyncFixture();
        var skippedPrefixResult = await skippedPrefix.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[1], skippedPrefix.Authority),
            CancellationToken.None);
        SyncAssert.False(skippedPrefixResult.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_migration_cursor_unproven", skippedPrefixResult.Diagnostics);
        SyncAssert.Equal(0, skippedPrefix.Repository.CommitCount);

        var fixture = new SyncFixture();
        var first = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[0], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("initial migration page", first);
        var retry = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[0], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("exact migration retry", retry);
        SyncAssert.Equal(first.Receipt!.ReceiptHash, retry.Receipt!.ReceiptHash);
        SyncAssert.Equal(1, fixture.Repository.DurableBundleWriteCount);

        // A clock correction cannot invalidate immutable retry evidence already stored by the
        // repository. The stored ReceivedAt can therefore be later than this request's wall clock.
        fixture.Clock.UtcNow = Now.AddMinutes(-5);
        var backwardClockRetry = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[0], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("backward-clock migration retry", backwardClockRetry);
        SyncAssert.Equal(first.Receipt.ReceiptHash, backwardClockRetry.Receipt!.ReceiptHash);
        SyncAssert.Equal(1, fixture.Repository.DurableBundleWriteCount);
        fixture.Clock.UtcNow = Now;

        // Page three is internally valid and self-hashed, but page two has not been accepted.
        var skippedMiddle = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[2], fixture.Authority),
            CancellationToken.None);
        SyncAssert.False(skippedMiddle.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_migration_cursor_unproven", skippedMiddle.Diagnostics);
        SyncAssert.Equal(1, fixture.Repository.DurableBundleWriteCount);

        var second = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[1], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("second migration page", second);

        var forkPlaceholder = new CortexLegacyMigrationPageV1(
            pages[1].SchemaVersion,
            pages[1].ContractId,
            pages[1].Canonicalization,
            "legacy-page-0002-fork",
            pages[1].SourceInventory,
            pages[1].CurrentCursor,
            pages[1].Bundle,
            pages[1].NextCursor,
            pages[1].AcknowledgmentRequired,
            pages[1].SourceMutationAllowed,
            pages[1].ContentTreatment,
            pages[1].Executable,
            pages[1].Extensions,
            CortexSyncContractIds.EmptySha256);
        var fork = CortexSyncContractPolicy.Seal(forkPlaceholder);
        var forked = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, fork, fixture.Authority),
            CancellationToken.None);
        SyncAssert.False(forked.Succeeded);
        SyncAssert.ContainsCode(
            CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict,
            forked.Diagnostics);
        SyncAssert.False(forked.Diagnostics.Single().Retryable);
        SyncAssert.Equal(2, fixture.Repository.DurableBundleWriteCount);

        var third = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[2], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("terminal migration page", third);
        SyncAssert.True(pages[2].NextCursor.Completed);
        SyncAssert.Equal(3, fixture.Repository.DurableBundleWriteCount);

        // An exact old accepted page remains an idempotent retry even after the head advances.
        var oldRetry = await fixture.Service.SubmitLegacyMigrationPageAsync(
            new SubmitCortexLegacyMigrationPageCommand(TargetNamespace, pages[0], fixture.Authority),
            CancellationToken.None);
        SyncAssert.Succeeded("old accepted migration retry", oldRetry);
        SyncAssert.Equal(3, fixture.Repository.DurableBundleWriteCount);
    }

    private static async Task TamperBeforeRepositoryAsync()
    {
        var fixture = new SyncFixture();
        var valid = SyncSamples.Bundle(Now);
        var tampered = SyncSamples.CopyBundle(valid, bundleHash: new string('f', 64));
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, tampered, fixture.Authority),
            CancellationToken.None);

        SyncAssert.False(result.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_bundle_invalid", result.Diagnostics);
        SyncAssert.Equal(0, fixture.Repository.CommitCount);
        SyncAssert.Equal(0, fixture.Repository.ReadBackCount);
    }

    private static async Task CommitReadBackOrderAsync()
    {
        var fixture = new SyncFixture();
        var bundle = SyncSamples.Bundle(Now);
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, bundle, fixture.Authority),
            CancellationToken.None);

        SyncAssert.True(result.Succeeded);
        SyncAssert.True(result.ReceiptAccepted);
        SyncAssert.True(result.DurablyReconciled);
        SyncAssert.Equal(CortexSyncCommitDisposition.Committed, result.Disposition!.Value);
        SyncAssert.SequenceEqual(
            new[] { "commit", "find_receipt", "readback", "issue", "verify", "append_receipt", "read_receipt" },
            fixture.Trace);
        SyncAssert.Equal(TargetNamespace, fixture.Repository.LastCommitRequest!.Scope.Namespace);
        SyncAssert.Equal("tenant-1", fixture.Repository.LastCommitRequest.Scope.TenantId);
        SyncAssert.True(CortexSyncContractPolicy.ValidateReceipt(result.Receipt!, bundle, true).DurablyReconciled);
        SyncAssert.True(
            fixture.Repository.LastReceiptAppendRequest!.CanonicalReceipt.Span.SequenceEqual(
                CortexCanonicalJson.Serialize(result.Receipt)));
    }

    private static async Task LostResponseRetryRecoversReceiptAsync()
    {
        var fixture = new SyncFixture
        {
            Issuer = { NondeterministicReceiptIds = true },
            Repository = { ReceivedAtOverride = Now.AddMinutes(-5) }
        };
        var bundle = SyncSamples.Bundle(Now);

        var first = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, bundle, fixture.Authority),
            CancellationToken.None);
        fixture.Clock.UtcNow = Now.AddMinutes(10);
        var retry = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, bundle, fixture.Authority),
            CancellationToken.None);

        SyncAssert.True(first.Succeeded);
        SyncAssert.True(retry.Succeeded);
        SyncAssert.Equal(first.Receipt!.ReceiptId, retry.Receipt!.ReceiptId);
        SyncAssert.Equal(Now.AddMinutes(-5), retry.Receipt.ReceivedAt);
        SyncAssert.Equal(1, fixture.Issuer.IssueCount);
        SyncAssert.Equal(1, fixture.Repository.ReceiptAppendCount);
        SyncAssert.Equal(2, fixture.Repository.ReceiptLookupCount);
        SyncAssert.Equal(1, fixture.Repository.ReadBackCount);
        SyncAssert.SequenceEqual(
            new[]
            {
                "commit", "find_receipt", "readback", "issue", "verify", "append_receipt", "read_receipt",
                "commit", "find_receipt", "verify"
            },
            fixture.Trace);
    }

    private static async Task ConcurrentReceiptAppendRecoveryAsync()
    {
        var fixture = new SyncFixture
        {
            Repository = { ConflictAfterStoringReceipt = true }
        };

        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.True(result.Succeeded);
        SyncAssert.Equal(1, fixture.Issuer.IssueCount);
        SyncAssert.Equal(1, fixture.Repository.ReceiptAppendCount);
        SyncAssert.Equal(2, fixture.Repository.ReceiptLookupCount);
        SyncAssert.Equal(0, fixture.Repository.ReceiptReadCount);
        SyncAssert.SequenceEqual(
            new[]
            {
                "commit", "find_receipt", "readback", "issue", "verify",
                "append_receipt", "find_receipt", "verify"
            },
            fixture.Trace);
    }

    private static async Task ExistingBundleAsync()
    {
        var fixture = new SyncFixture
        {
            Repository = { Disposition = CortexSyncCommitDisposition.Existing }
        };
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.True(result.Succeeded);
        SyncAssert.Equal(CortexSyncCommitDisposition.Existing, result.Disposition!.Value);
        SyncAssert.True(result.Receipt!.Outcomes.All(item => item.Disposition == CortexSyncDispositionIds.Duplicate));
    }

    private static async Task ConflictAsync()
    {
        var fixture = new SyncFixture
        {
            Repository = { Disposition = CortexSyncCommitDisposition.Conflict }
        };
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.False(result.Succeeded);
        SyncAssert.True(result.ReceiptAccepted);
        SyncAssert.False(result.DurablyReconciled);
        SyncAssert.Equal(CortexSyncCommitDisposition.Conflict, result.Disposition!.Value);
        SyncAssert.ContainsCode("cortex_sync_conflict", result.Diagnostics);
        SyncAssert.Equal(CortexSyncDispositionIds.Conflict, result.Receipt!.Outcomes.Single().Disposition);
    }

    private static async Task ReadBackMismatchAsync()
    {
        var fixture = new SyncFixture
        {
            Repository = { MismatchReadBack = true }
        };
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.False(result.Succeeded);
        SyncAssert.True(result.ReceiptAccepted);
        SyncAssert.False(result.DurablyReconciled);
        SyncAssert.Equal("mismatch", result.Receipt!.Reconciliation.State);
        SyncAssert.ContainsCode("cortex_sync_readback_mismatch", result.Diagnostics);
        SyncAssert.SequenceEqual(
            new[] { "commit", "find_receipt", "readback", "issue", "verify", "append_receipt", "read_receipt" },
            fixture.Trace);
    }

    private static async Task BadIssuerReceiptAsync()
    {
        var fixture = new SyncFixture
        {
            Issuer = { ReturnWrongDurability = true }
        };
        var result = await fixture.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), fixture.Authority),
            CancellationToken.None);

        SyncAssert.False(result.Succeeded);
        SyncAssert.False(result.ReceiptAccepted);
        SyncAssert.True(result.Receipt is null);
        SyncAssert.ContainsCode("cortex_sync_receipt_invalid", result.Diagnostics);
        SyncAssert.Equal(0, fixture.Repository.ReceiptAppendCount);
    }

    private static async Task ReceiptPersistenceFailureAsync()
    {
        var appendFailure = new SyncFixture();
        appendFailure.Repository.ReceiptAppendException = new InvalidOperationException("private receipt store detail");
        var appendResult = await appendFailure.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), appendFailure.Authority),
            CancellationToken.None);
        SyncAssert.False(appendResult.Succeeded);
        SyncAssert.False(appendResult.ReceiptAccepted);
        SyncAssert.ContainsCode("cortex_sync_receipt_persistence_failure", appendResult.Diagnostics);
        SyncAssert.Equal(0, appendFailure.Repository.ReceiptReadCount);

        var readBackTamper = new SyncFixture();
        readBackTamper.Repository.TamperReceiptReadBack = true;
        var readBackResult = await readBackTamper.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(TargetNamespace, SyncSamples.Bundle(Now), readBackTamper.Authority),
            CancellationToken.None);
        SyncAssert.False(readBackResult.Succeeded);
        SyncAssert.False(readBackResult.ReceiptAccepted);
        SyncAssert.ContainsCode("cortex_sync_receipt_readback_invalid", readBackResult.Diagnostics);
        SyncAssert.Equal(1, readBackTamper.Repository.ReceiptAppendCount);
        SyncAssert.Equal(1, readBackTamper.Repository.ReceiptReadCount);
    }

    private static async Task CancellationAndExceptionsAsync()
    {
        var callerCancelled = new SyncFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await SyncAssert.ThrowsAsync<OperationCanceledException>(() =>
            callerCancelled.Service.SubmitAsync(
                new SubmitCortexSyncBundleCommand(
                    TargetNamespace,
                    SyncSamples.Bundle(Now),
                    callerCancelled.Authority),
                cancellation.Token).AsTask());
        SyncAssert.Equal(0, callerCancelled.Repository.CommitCount);

        var providerCancellation = new SyncFixture();
        providerCancellation.Repository.CommitException = new OperationCanceledException("provider-originated cancellation");
        var providerCancellationResult = await providerCancellation.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                TargetNamespace,
                SyncSamples.Bundle(Now),
                providerCancellation.Authority),
            CancellationToken.None);
        SyncAssert.False(providerCancellationResult.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_commit_failure", providerCancellationResult.Diagnostics);
        SyncAssert.Equal(
            typeof(OperationCanceledException).FullName,
            providerCancellationResult.Diagnostics.Single().Cause);

        var repositoryFailure = new SyncFixture();
        repositoryFailure.Repository.CommitException = new InvalidOperationException("private provider detail");
        var repositoryResult = await repositoryFailure.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                TargetNamespace,
                SyncSamples.Bundle(Now),
                repositoryFailure.Authority),
            CancellationToken.None);
        SyncAssert.False(repositoryResult.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_commit_failure", repositoryResult.Diagnostics);
        SyncAssert.Equal(typeof(InvalidOperationException).FullName, repositoryResult.Diagnostics.Single().Cause);

        var issuerFailure = new SyncFixture();
        issuerFailure.Issuer.IssueException = new InvalidOperationException("private key provider detail");
        var issuerResult = await issuerFailure.Service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                TargetNamespace,
                SyncSamples.Bundle(Now),
                issuerFailure.Authority),
            CancellationToken.None);
        SyncAssert.False(issuerResult.Succeeded);
        SyncAssert.ContainsCode("cortex_sync_receipt_issuer_failure", issuerResult.Diagnostics);
        SyncAssert.Equal(typeof(InvalidOperationException).FullName, issuerResult.Diagnostics.Single().Cause);
    }
}

/// <summary>
/// Deliberately exposes only the generic repository contract. It proves the application will not
/// erase a migration page's cursor evidence and silently submit its bundle through this surface.
/// </summary>
internal sealed class SyncGenericOnlyRepository(ICortexSyncRepository inner) : ICortexSyncRepository
{
    public ValueTask<CortexSyncCommitSnapshot> CommitAsync(
        CortexSyncCommitRequest request,
        CancellationToken cancellationToken) =>
        inner.CommitAsync(request, cancellationToken);

    public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken) =>
        inner.ReadBackAsync(request, cancellationToken);

    public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken) =>
        inner.AppendReceiptAsync(request, cancellationToken);

    public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken) =>
        inner.FindReceiptAsync(request, cancellationToken);

    public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken) =>
        inner.ReadReceiptAsync(request, cancellationToken);
}

internal sealed class SyncFixture
{
    public SyncFixture(bool canSync = true)
    {
        Trace = [];
        Repository = new SyncRepositoryStub(Now, Trace);
        Issuer = new SyncReceiptIssuerStub(Trace);
        Authority = new CortexSyncAuthorityContext(
            "tenant-1",
            "authority-1",
            "subject-1",
            "service-1",
            7,
            [new CortexSyncNamespaceGrant(TargetNamespace, canSync)],
            [PayloadContractId, CortexSyncContractIds.LegacyMigrationRecord]);
        Clock = new SyncFixedTimeProvider(Now);
        Service = new CortexSyncApplicationService(Repository, Issuer, Clock);
    }

    private const string TargetNamespace = "project:project-1";
    private const string PayloadContractId = "pegasus.cortex.test-payload/1.0";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-14T20:00:00Z");

    public List<string> Trace { get; }
    public SyncRepositoryStub Repository { get; init; }
    public SyncReceiptIssuerStub Issuer { get; init; }
    public CortexSyncAuthorityContext Authority { get; }
    public SyncFixedTimeProvider Clock { get; }
    public CortexSyncApplicationService Service { get; }
}

internal sealed class SyncRepositoryStub(DateTimeOffset now, List<string> trace) :
    ICortexSyncRepository,
    ICortexLegacyMigrationPageCommitRepository
{
    private const string DurableStateHash =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    public CortexSyncCommitDisposition Disposition { get; set; } = CortexSyncCommitDisposition.Committed;
    public string ConflictOutcome { get; set; } = CortexSyncDispositionIds.Conflict;
    public bool MismatchReadBack { get; set; }
    public Exception? CommitException { get; set; }
    public Exception? ReadBackException { get; set; }
    public Exception? ReceiptLookupException { get; set; }
    public Exception? ReceiptAppendException { get; set; }
    public Exception? ReceiptReadException { get; set; }
    public bool TamperReceiptReadBack { get; set; }
    public bool ConflictAfterStoringReceipt { get; set; }
    public DateTimeOffset? ReceivedAtOverride { get; set; }
    public int CommitCount { get; private set; }
    public int DurableBundleWriteCount { get; private set; }
    public int ReadBackCount { get; private set; }
    public int ReceiptLookupCount { get; private set; }
    public int ReceiptAppendCount { get; private set; }
    public int ReceiptReadCount { get; private set; }
    public int MigrationProofCount { get; private set; }
    public int MigrationProofRejectedCount { get; private set; }
    public CortexSyncCommitRequest? LastCommitRequest { get; private set; }
    public CortexSyncCommitSnapshot? LastCommit { get; private set; }
    public CortexSyncReceiptAppendRequest? LastReceiptAppendRequest { get; private set; }

    private byte[]? StoredCanonicalReceipt { get; set; }
    private DateTimeOffset? ReceiptRecordedAt { get; set; }
    private readonly Dictionary<string, CortexLegacyMigrationPageV1> _pendingMigrationPages =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CortexLegacyMigrationPageV1> _acceptedMigrationPages =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CortexSyncCommitSnapshot> _migrationCommits =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _durableBundleHashes = new(StringComparer.Ordinal);

    public async ValueTask<CortexLegacyMigrationPageCommitSnapshot> CommitLegacyMigrationPageAsync(
        CortexLegacyMigrationPageCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MigrationProofCount++;
        trace.Add("migration_proof");
        var page = request.Page;
        var exactRetry = _acceptedMigrationPages.TryGetValue(page.PageHash, out var acceptedPage) &&
            CortexCanonicalJson.Serialize(acceptedPage).AsSpan().SequenceEqual(request.CanonicalPage.Span);
        if (_pendingMigrationPages.TryGetValue(page.Bundle.BundleHash, out var attemptedPage) &&
            !CortexCanonicalJson.Serialize(attemptedPage).AsSpan().SequenceEqual(request.CanonicalPage.Span))
        {
            MigrationProofRejectedCount++;
            return new CortexLegacyMigrationPageCommitSnapshot(
                CortexLegacyMigrationPageCommitDisposition.Rejected,
                commit: null,
                diagnosticCode: CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict);
        }
        if (exactRetry)
        {
            var storedCommit = _migrationCommits[page.PageHash];
            LastCommitRequest = new CortexSyncCommitRequest(request.Scope, page.Bundle, storedCommit.ReceivedAt);
            LastCommit = storedCommit;
            return new CortexLegacyMigrationPageCommitSnapshot(
                CortexLegacyMigrationPageCommitDisposition.Accepted,
                storedCommit,
                diagnosticCode: null,
                replayedExistingAttempt: true);
        }
        var current = _acceptedMigrationPages.Values
            .Where(item => string.Equals(
                item.CurrentCursor.MigrationId,
                page.CurrentCursor.MigrationId,
                StringComparison.Ordinal))
            .OrderByDescending(item => item.CurrentCursor.PageNumber)
            .FirstOrDefault();
        var bundleContinues = current is null
            ? page.Bundle.BaseServerRevision is null && page.Bundle.FirstSequence == 1 &&
                page.Bundle.PreviousBundleHash == CortexSyncContractIds.EmptySha256 &&
                page.Bundle.PriorEventHash == CortexSyncContractIds.EmptySha256
            : page.Bundle.BaseServerRevision == "storage-revision-1" &&
                page.Bundle.PreviousBundleHash == current.Bundle.BundleHash &&
                page.Bundle.PriorEventHash == current.Bundle.Events[^1].EventHash &&
                page.Bundle.FirstSequence == current.Bundle.LastSequence + 1;
        var initial = current is null &&
            CortexSyncContractPolicy.IsCanonicalInitialLegacyMigrationCursor(page.CurrentCursor);
        var successor = current is not null && !current.NextCursor.Completed &&
            CortexCanonicalJson.Serialize(current.NextCursor).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(page.CurrentCursor)) &&
            CortexCanonicalJson.Serialize(current.SourceInventory).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(page.SourceInventory));

        if (!bundleContinues || !initial && !successor)
        {
            MigrationProofRejectedCount++;
            return new CortexLegacyMigrationPageCommitSnapshot(
                CortexLegacyMigrationPageCommitDisposition.Rejected,
                commit: null,
                diagnosticCode: "cortex_sync_migration_cursor_not_server_successor");
        }

        var commit = await CommitAsync(
            new CortexSyncCommitRequest(request.Scope, page.Bundle, request.ReceivedAt),
            cancellationToken).ConfigureAwait(false);
        _pendingMigrationPages[page.Bundle.BundleHash] = page;
        _migrationCommits[page.PageHash] = commit;
        return new CortexLegacyMigrationPageCommitSnapshot(
            CortexLegacyMigrationPageCommitDisposition.Accepted,
            commit,
            diagnosticCode: null,
            replayedExistingAttempt: false);
    }

    public ValueTask<CortexSyncCommitSnapshot> CommitAsync(
        CortexSyncCommitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCount++;
        trace.Add("commit");
        LastCommitRequest = request;
        if (CommitException is not null)
            throw CommitException;
        if (_durableBundleHashes.Add(request.Bundle.BundleHash))
            DurableBundleWriteCount++;

        var disposition = Disposition switch
        {
            CortexSyncCommitDisposition.Committed => CortexSyncDispositionIds.Applied,
            CortexSyncCommitDisposition.Existing => CortexSyncDispositionIds.Duplicate,
            CortexSyncCommitDisposition.Conflict => ConflictOutcome,
            _ => throw new InvalidOperationException("Unsupported test disposition.")
        };
        var outcomes = request.Bundle.Events.Select(item => new CortexSyncReceiptOutcomeV1(
            item.Sequence,
            item.EventId,
            item.EventHash,
            disposition,
            durable: true,
            disposition is CortexSyncDispositionIds.Conflict or CortexSyncDispositionIds.Rejected or CortexSyncDispositionIds.Quarantined
                ? null
                : "entity-1",
            disposition is CortexSyncDispositionIds.Conflict or CortexSyncDispositionIds.Rejected or CortexSyncDispositionIds.Quarantined
                ? null
                : "revision-1",
            disposition switch
            {
                CortexSyncDispositionIds.Conflict => "sync_conflict",
                CortexSyncDispositionIds.Rejected => "sync_rejected",
                CortexSyncDispositionIds.Quarantined => "sync_quarantined",
                _ => null
            }));
        LastCommit = new CortexSyncCommitSnapshot(
            Disposition,
            "commit-1",
            "storage-revision-1",
            ReceivedAtOverride ?? request.ReceivedAt,
            now.AddSeconds(1),
            DurableStateHash,
            outcomes);
        return ValueTask.FromResult(LastCommit);
    }

    public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
        CortexSyncReadBackRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadBackCount++;
        trace.Add("readback");
        if (ReadBackException is not null)
            throw ReadBackException;
        var commit = LastCommit ?? throw new InvalidOperationException("Commit must precede read-back.");
        var commitRequest = LastCommitRequest ?? throw new InvalidOperationException("Commit request is unavailable.");
        var count = commitRequest.Bundle.Events.Count;
        return ValueTask.FromResult(new CortexSyncReadBackSnapshot(
            request.Scope.TenantId,
            request.Scope.Namespace,
            request.Scope.StoreId,
            request.Scope.StoreEpoch,
            request.Scope.ProjectId,
            request.BundleId,
            request.BundleHash,
            request.CommitId,
            request.StorageRevision,
            count,
            MismatchReadBack ? count - 1 : count,
            commit.CommittedAt.AddSeconds(ReadBackCount),
            MismatchReadBack ? new string('e', 64) : DurableStateHash));
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
        CortexSyncReceiptLookupRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiptLookupCount++;
        trace.Add("find_receipt");
        if (ReceiptLookupException is not null)
            throw ReceiptLookupException;
        if (StoredCanonicalReceipt is null || LastReceiptAppendRequest is null || ReceiptRecordedAt is null ||
            !string.Equals(LastReceiptAppendRequest.Receipt.BundleId, request.BundleId, StringComparison.Ordinal) ||
            !string.Equals(LastReceiptAppendRequest.Receipt.BundleHash, request.BundleHash, StringComparison.Ordinal))
            return ValueTask.FromResult<CortexSyncReceiptReadBackSnapshot?>(null);

        return ValueTask.FromResult<CortexSyncReceiptReadBackSnapshot?>(CreateReceiptReadBack(
            request.Scope,
            request.BundleId,
            LastReceiptAppendRequest.Receipt.ReceiptId,
            StoredCanonicalReceipt,
            ReceiptRecordedAt.Value));
    }

    public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
        CortexSyncReceiptAppendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiptAppendCount++;
        trace.Add("append_receipt");
        if (ReceiptAppendException is not null)
            throw ReceiptAppendException;

        LastReceiptAppendRequest = request;
        StoredCanonicalReceipt = request.CanonicalReceipt.ToArray();
        // Receipt persistence time follows the immutable evidence being recorded. A fixed test
        // timestamp eventually predates later multi-page read-back and creates a false failure.
        ReceiptRecordedAt = (request.Receipt.Reconciliation.ReadBackAt ??
            request.Receipt.Durability.CommittedAt).AddSeconds(1);
        if (request.Receipt.Reconciliation.State == "reconciled" &&
            _pendingMigrationPages.TryGetValue(request.Receipt.BundleHash, out var migrationPage) &&
            Disposition != CortexSyncCommitDisposition.Conflict)
        {
            _acceptedMigrationPages[migrationPage.PageHash] = migrationPage;
        }
        return ValueTask.FromResult(new CortexSyncReceiptAppendSnapshot(
            ConflictAfterStoringReceipt
                ? CortexSyncReceiptAppendDisposition.Conflict
                : CortexSyncReceiptAppendDisposition.Appended,
            request.Scope.TenantId,
            request.Scope.Namespace,
            request.Receipt.StoreId,
            request.Receipt.StoreEpoch,
            request.Receipt.ProjectId,
            request.Receipt.BundleId,
            request.Receipt.ReceiptId,
            request.Receipt.ReceiptHash,
            request.CanonicalSha256,
            ReceiptRecordedAt.Value));
    }

    public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
        CortexSyncReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiptReadCount++;
        trace.Add("read_receipt");
        if (ReceiptReadException is not null)
            throw ReceiptReadException;
        if (StoredCanonicalReceipt is null || LastReceiptAppendRequest is null || ReceiptRecordedAt is null)
            throw new InvalidOperationException("Receipt append must precede receipt read-back.");

        var canonical = StoredCanonicalReceipt.ToArray();
        if (TamperReceiptReadBack)
            canonical[0] ^= 0x01;
        return ValueTask.FromResult(CreateReceiptReadBack(
            request.Scope,
            request.BundleId,
            LastReceiptAppendRequest.Receipt.ReceiptId,
            canonical,
            ReceiptRecordedAt.Value));
    }

    private static CortexSyncReceiptReadBackSnapshot CreateReceiptReadBack(
        CortexSyncRepositoryScope scope,
        string bundleId,
        string receiptId,
        byte[] canonicalReceipt,
        DateTimeOffset recordedAt) =>
        new(
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId,
            bundleId,
            receiptId,
            recordedAt,
            canonicalReceipt);
}

internal sealed class SyncReceiptIssuerStub(List<string> trace) : ICortexSyncReceiptIssuer
{
    public bool ReturnWrongDurability { get; set; }
    public bool NondeterministicReceiptIds { get; set; }
    public bool ProofVerified { get; set; } = true;
    public Exception? IssueException { get; set; }
    public Exception? VerifyException { get; set; }
    public int IssueCount { get; private set; }

    public ValueTask<CortexSyncReceiptV1> IssueAsync(
        CortexSyncReceiptIssuance issuance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IssueCount++;
        trace.Add("issue");
        if (IssueException is not null)
            throw IssueException;
        return ValueTask.FromResult(CreateReceipt(
            issuance,
            NondeterministicReceiptIds ? $"receipt-{IssueCount}" : "receipt-1"));
    }

    public ValueTask<bool> VerifyProofAsync(
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        trace.Add("verify");
        if (VerifyException is not null)
            throw VerifyException;
        return ValueTask.FromResult(ProofVerified);
    }

    private CortexSyncReceiptV1 CreateReceipt(CortexSyncReceiptIssuance issuance, string receiptId)
    {
        var proofBytes = Encoding.UTF8.GetBytes("test-proof");
        var proofBase64Url = SyncSamples.Base64Url(proofBytes);
        var proofHash = CortexCanonicalJson.Sha256(proofBytes);
        var durability = new CortexSyncReceiptDurabilityV1(
            issuance.Commit.CommitId,
            ReturnWrongDurability ? "wrong-storage-revision" : issuance.Commit.StorageRevision,
            issuance.Commit.CommittedAt);
        var placeholderAuthentication = new CortexSyncReceiptAuthenticationV1(
            "pegasus.cortex.test-proof/1.0",
            "test-key-1",
            "test-sha256",
            CortexSyncContractIds.EmptySha256,
            proofBase64Url,
            proofBytes.Length,
            proofHash);
        var placeholder = Create(
            issuance,
            durability,
            placeholderAuthentication,
            receiptId,
            CortexSyncContractIds.EmptySha256);
        var receiptHash = CortexSyncContractPolicy.ComputeReceiptSemanticHash(placeholder);
        var authentication = new CortexSyncReceiptAuthenticationV1(
            placeholderAuthentication.ProofContractId,
            placeholderAuthentication.KeyId,
            placeholderAuthentication.AlgorithmId,
            receiptHash,
            proofBase64Url,
            proofBytes.Length,
            proofHash);
        return Create(issuance, durability, authentication, receiptId, receiptHash);
    }

    private static CortexSyncReceiptV1 Create(
        CortexSyncReceiptIssuance issuance,
        CortexSyncReceiptDurabilityV1 durability,
        CortexSyncReceiptAuthenticationV1 authentication,
        string receiptId,
        string receiptHash) =>
        new(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncReceipt,
            CortexSyncContractIds.Canonicalization,
            receiptId,
            "server-instance-1",
            issuance.Bundle.StoreId,
            issuance.Bundle.StoreEpoch,
            issuance.Bundle.ProjectId,
            issuance.Bundle.BundleId,
            issuance.Bundle.BundleHash,
            issuance.ReceivedAt,
            authentication,
            durability,
            issuance.Commit.Outcomes,
            issuance.Reconciliation,
            [],
            receiptHash);
}

internal static class SyncSamples
{
    private const string PayloadContractId = "pegasus.cortex.test-payload/1.0";

    public static CortexOfflineBundleV1 Bundle(DateTimeOffset now)
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var payloadBase64Url = Base64Url(payload);
        var payloadHash = CortexCanonicalJson.Sha256(payload);
        var source = new CortexSyncSourceProvenanceV1(
            "client_operation",
            null,
            null,
            payload.Length,
            payloadHash,
            now);
        var eventPlaceholder = new CortexOfflineEventV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            "store-1",
            "epoch-1",
            "project-1",
            1,
            "event-1",
            CortexSyncContractIds.EmptySha256,
            now,
            "producer-1",
            "operation-1",
            "tenant-1",
            "project:project-1",
            7,
            PayloadContractId,
            payloadBase64Url,
            payload.Length,
            payloadHash,
            source,
            CortexSyncContractIds.ContentTreatment,
            executable: false,
            [],
            CortexSyncContractIds.EmptySha256);
        var @event = CortexSyncContractPolicy.Seal(eventPlaceholder);
        var bundlePlaceholder = new CortexOfflineBundleV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            "bundle-1",
            "store-1",
            "epoch-1",
            "project-1",
            null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            1,
            now,
            [@event],
            [],
            CortexSyncContractIds.EmptySha256);
        return CortexSyncContractPolicy.Seal(bundlePlaceholder);
    }

    public static CortexLegacyMigrationPageV1 MigrationPage(DateTimeOffset now)
    {
        var sourceBytes = Encoding.UTF8.GetBytes("legacy state\n");
        var sourceHash = CortexCanonicalJson.Sha256(sourceBytes);
        var sourceManifestHash = new string('a', 64);
        var inventory = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationInventoryV1(
            1, CortexSyncContractIds.LegacyMigrationInventory, CortexSyncContractIds.Canonicalization,
            "migration-1", sourceManifestHash, "store-1", "epoch-1", "project-1", "source-root-1",
            now, true,
            [new CortexLegacyMigrationInventoryFileV1(
                0, "state", ".cortex/STATE.md", sourceBytes.Length, sourceHash,
                "legacy-record-1", "legacy-revision-1", 1)],
            sourceBytes.Length, CortexSyncContractIds.ContentTreatment, false, [],
            CortexSyncContractIds.EmptySha256));
        var payloadValue = new CortexLegacyMigrationRecordV1(
            1, CortexSyncContractIds.LegacyMigrationRecord, CortexSyncContractIds.Canonicalization,
            "legacy-record-1", "legacy-revision-1", "project:project-1", "state",
            "source-root-1", sourceManifestHash, inventory.InventoryHash, 0,
            ".cortex/STATE.md", sourceBytes.Length, sourceHash, null, false,
            1, 1, 0, Base64Url(sourceBytes), sourceBytes.Length, sourceHash,
            CortexSyncContractIds.ContentTreatment, false);
        var payload = CortexCanonicalJson.Serialize(payloadValue);
        var item = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            1, CortexSyncContractIds.OfflineEvent, CortexSyncContractIds.Canonicalization,
            "store-1", "epoch-1", "project-1", 1, "legacy-event-1",
            CortexSyncContractIds.EmptySha256, now, "producer-1", "legacy.copy",
            "tenant-1", "project:project-1", 7, CortexSyncContractIds.LegacyMigrationRecord,
            Base64Url(payload), payload.Length, CortexCanonicalJson.Sha256(payload),
            new CortexSyncSourceProvenanceV1(
                "legacy_file_copy", "source-root-1", ".cortex/STATE.md", sourceBytes.Length,
                sourceHash, now),
            CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));
        var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1, CortexSyncContractIds.OfflineBundle, CortexSyncContractIds.Canonicalization,
            "legacy-bundle-1", "store-1", "epoch-1", "project-1", null,
            CortexSyncContractIds.EmptySha256, CortexSyncContractIds.EmptySha256,
            1, 1, now, [item], [], CortexSyncContractIds.EmptySha256));
        var current = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            1, CortexSyncContractIds.LegacyMigrationCursor, CortexSyncContractIds.Canonicalization,
            "migration-1", sourceManifestHash, inventory.InventoryHash, inventory.Files.Count,
            bundle.StoreId, bundle.StoreEpoch, bundle.ProjectId,
            "source-root-1", 1, 0, 0, bundle.FirstSequence, bundle.PreviousBundleHash,
            bundle.PriorEventHash, false, CortexSyncContractIds.ContentTreatment, false, [],
            CortexSyncContractIds.EmptySha256));
        var next = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            1, CortexSyncContractIds.LegacyMigrationCursor, CortexSyncContractIds.Canonicalization,
            "migration-1", sourceManifestHash, inventory.InventoryHash, inventory.Files.Count,
            bundle.StoreId, bundle.StoreEpoch, bundle.ProjectId,
            "source-root-1", 2, 1, 0, bundle.LastSequence + 1, bundle.BundleHash,
            bundle.Events[^1].EventHash, true, CortexSyncContractIds.ContentTreatment, false, [],
            CortexSyncContractIds.EmptySha256));
        return CortexSyncContractPolicy.Seal(new CortexLegacyMigrationPageV1(
            1, CortexSyncContractIds.LegacyMigrationPage, CortexSyncContractIds.Canonicalization,
            "legacy-page-0001", inventory, current, bundle, next, true, false,
            CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));
    }

    public static IReadOnlyList<CortexLegacyMigrationPageV1> MigrationPages(DateTimeOffset now)
    {
        var sourceManifestHash = new string('a', 64);
        var roles = new[] { "state", "tasks", "worklog" };
        var paths = new[] { ".cortex/STATE.md", ".cortex/TASKS.md", ".cortex/WORKLOG.md" };
        var sourceValues = Enumerable.Range(0, roles.Length)
            .Select(index => Encoding.UTF8.GetBytes($"legacy {roles[index]}\n"))
            .ToArray();
        var inventoryFiles = Enumerable.Range(0, roles.Length)
            .Select(index => new CortexLegacyMigrationInventoryFileV1(
                index,
                roles[index],
                paths[index],
                sourceValues[index].Length,
                CortexCanonicalJson.Sha256(sourceValues[index]),
                $"legacy-record-{index + 1}",
                $"legacy-revision-{index + 1}",
                1))
            .ToArray();
        var inventory = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationInventoryV1(
            1,
            CortexSyncContractIds.LegacyMigrationInventory,
            CortexSyncContractIds.Canonicalization,
            "migration-1",
            sourceManifestHash,
            "store-1",
            "epoch-1",
            "project-1",
            "source-root-1",
            now,
            true,
            inventoryFiles,
            sourceValues.Sum(value => (long)value.Length),
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
        var current = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            1,
            CortexSyncContractIds.LegacyMigrationCursor,
            CortexSyncContractIds.Canonicalization,
            "migration-1",
            sourceManifestHash,
            inventory.InventoryHash,
            inventory.Files.Count,
            "store-1",
            "epoch-1",
            "project-1",
            "source-root-1",
            1,
            0,
            0,
            1,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            false,
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
        var pages = new List<CortexLegacyMigrationPageV1>(inventory.Files.Count);
        for (var index = 0; index < inventory.Files.Count; index++)
        {
            var source = sourceValues[index];
            var file = inventory.Files[index];
            var payloadValue = new CortexLegacyMigrationRecordV1(
                1,
                CortexSyncContractIds.LegacyMigrationRecord,
                CortexSyncContractIds.Canonicalization,
                file.RecordId,
                file.RevisionId,
                "project:project-1",
                file.Role,
                inventory.SourceRootId,
                inventory.SourceManifestHash,
                inventory.InventoryHash,
                index,
                file.SourceRelativePath,
                file.SourceBytes,
                file.SourceSha256,
                null,
                false,
                1,
                1,
                0,
                Base64Url(source),
                source.Length,
                file.SourceSha256,
                CortexSyncContractIds.ContentTreatment,
                false);
            var payload = CortexCanonicalJson.Serialize(payloadValue);
            var item = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
                1,
                CortexSyncContractIds.OfflineEvent,
                CortexSyncContractIds.Canonicalization,
                "store-1",
                "epoch-1",
                "project-1",
                index + 1,
                $"legacy-event-{index + 1}",
                current.PriorEventHash,
                now,
                "producer-1",
                "legacy.copy",
                "tenant-1",
                "project:project-1",
                7,
                CortexSyncContractIds.LegacyMigrationRecord,
                Base64Url(payload),
                payload.Length,
                CortexCanonicalJson.Sha256(payload),
                new CortexSyncSourceProvenanceV1(
                    "legacy_file_copy",
                    inventory.SourceRootId,
                    file.SourceRelativePath,
                    file.SourceBytes,
                    file.SourceSha256,
                    now),
                CortexSyncContractIds.ContentTreatment,
                false,
                [],
                CortexSyncContractIds.EmptySha256));
            var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
                1,
                CortexSyncContractIds.OfflineBundle,
                CortexSyncContractIds.Canonicalization,
                $"legacy-bundle-{index + 1}",
                "store-1",
                "epoch-1",
                "project-1",
                index == 0 ? null : "storage-revision-1",
                current.PreviousBundleHash,
                current.PriorEventHash,
                item.Sequence,
                item.Sequence,
                now,
                [item],
                [],
                CortexSyncContractIds.EmptySha256));
            var completed = index + 1 == inventory.Files.Count;
            var next = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
                1,
                CortexSyncContractIds.LegacyMigrationCursor,
                CortexSyncContractIds.Canonicalization,
                "migration-1",
                sourceManifestHash,
                inventory.InventoryHash,
                inventory.Files.Count,
                "store-1",
                "epoch-1",
                "project-1",
                "source-root-1",
                index + 2,
                index + 1,
                0,
                bundle.LastSequence + 1,
                bundle.BundleHash,
                item.EventHash,
                completed,
                CortexSyncContractIds.ContentTreatment,
                false,
                [],
                CortexSyncContractIds.EmptySha256));
            pages.Add(CortexSyncContractPolicy.Seal(new CortexLegacyMigrationPageV1(
                1,
                CortexSyncContractIds.LegacyMigrationPage,
                CortexSyncContractIds.Canonicalization,
                $"legacy-page-{index + 1:0000}",
                inventory,
                current,
                bundle,
                next,
                true,
                false,
                CortexSyncContractIds.ContentTreatment,
                false,
                [],
                CortexSyncContractIds.EmptySha256)));
            current = next;
        }

        return pages.AsReadOnly();
    }

    public static CortexOfflineBundleV1 CopyBundle(
        CortexOfflineBundleV1 value,
        string bundleHash) =>
        new(
            value.SchemaVersion,
            value.ContractId,
            value.Canonicalization,
            value.BundleId,
            value.StoreId,
            value.StoreEpoch,
            value.ProjectId,
            value.BaseServerRevision,
            value.PreviousBundleHash,
            value.PriorEventHash,
            value.FirstSequence,
            value.LastSequence,
            value.CreatedAt,
            value.Events,
            value.Extensions,
            bundleHash);

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class SyncFixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal static class SyncAssert
{
    public static void Succeeded(string stage, CortexSyncApplicationResult value)
    {
        if (!value.Succeeded)
        {
            var diagnostic = string.Join(
                ",",
                value.Diagnostics.Select(item => $"{item.Code}:{item.Cause}:{item.Retryable}"));
            throw new InvalidOperationException($"{stage} failed: {diagnostic}");
        }
    }

    public static void True(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void ContainsCode(string code, IReadOnlyList<PegasusDiagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Expected diagnostic '{code}'.");
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Expected call order was not preserved.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, received {exception.GetType().Name}.",
                exception);
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
