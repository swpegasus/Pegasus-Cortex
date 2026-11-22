using System.Globalization;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Integrated.Recovery;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;

namespace Pegasus.Cortex.Mcp.Integrated.Runtime;

/// <summary>
/// Narrow retained-state port used by reconnect synchronization. Keeping this
/// internal makes the real gateway executable-testable without opening a new
/// MCP or public service surface.
/// </summary>
internal interface ICortexIntegratedCurrentRecoveryRetention
{
    ValueTask<CortexRetainedAssertionWriteResult?> SubmitTaskSnapshotAsync(
        string projectLinkId,
        string projectNamespace,
        AgentTaskSnapshotV1 snapshot,
        CancellationToken cancellationToken);

    ValueTask<CortexRetainedAssertionRecord?> ReadRetainedAssertionAsync(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        CancellationToken cancellationToken);
}

/// <summary>
/// Non-MCP reconnect gateway. Nexus supplies the authenticated transport; this
/// runtime re-derives project scope from the protected integrated authority and
/// publishes a head only after journal, receipt, retained snapshot, and brief
/// evidence have all been read back exactly.
/// </summary>
internal sealed class CortexIntegratedCurrentRecoverySyncGateway :
    ICortexSyncGatewayRuntime
{
    private static readonly CortexSyncCapabilitiesV1 Capabilities =
        CortexSyncContractPolicy.Seal(new CortexSyncCapabilitiesV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncCapabilities,
            CortexSyncContractIds.Canonicalization,
            "pegasus-cortex-integrated-sync-v1",
            [
                CortexSyncContractIds.OfflineEvent,
                CortexSyncContractIds.OfflineBundle,
                CortexSyncContractIds.SyncReceipt,
                CortexSyncContractIds.SyncCapabilities,
                CortexSyncContractIds.SyncGatewayHeadRequest,
                CortexSyncContractIds.SyncGatewayHead,
                CortexSyncContractIds.SyncGatewaySubmitRequest,
                CortexSyncContractIds.SyncGatewaySubmitResult
            ],
            [AgentTaskSnapshotContractIds.Snapshot],
            [
                CortexSyncCapabilityIds.DurableReceipts,
                CortexSyncCapabilityIds.EventHashChain,
                CortexSyncCapabilityIds.ServerReadBackReconciliation,
                CortexSyncCapabilityIds.CurrentRecoveryMaterialization
            ],
            [],
            [],
            [],
            [HmacSha256CortexSyncReceiptIssuer.AlgorithmId],
            [HmacSha256CortexSyncReceiptIssuer.ProofContractId],
            new CortexSyncLimitsV1(
                CortexSyncGatewayLimits.MaximumEventPayloadBytes,
                CortexSyncGatewayLimits.MaximumEventsPerBundle,
                CortexSyncGatewayLimits.MaximumCanonicalBundleBytes,
                maximumExtensions: 0,
                CortexSyncContractLimits.MaximumExtensionPayloadBytes,
                CortexSyncGatewayLimits.MaximumReceiptOutcomes,
                maximumMigrationFiles: 1,
                CortexSyncContractLimits.MaximumJsonDepth),
            [],
            CortexSyncContractIds.EmptySha256));

    private readonly ICortexSyncRepository _repository;
    private readonly ICortexSyncPublishedHeadRepositoryV2 _publishedHeads;
    private readonly ICortexSyncReceiptIssuer _receiptIssuer;
    private readonly ICortexProjectLinkRepository _projectLinks;
    private readonly ICortexIntegratedCurrentRecoveryRetention _retention;
    private readonly ICortexProjectBriefHistoryReadModel _projectBriefs;
    private readonly CortexLocalAuthority _authority;
    private readonly TimeProvider _timeProvider;

    public CortexIntegratedCurrentRecoverySyncGateway(
        ICortexSyncRepository repository,
        ICortexSyncPublishedHeadRepositoryV2 publishedHeads,
        ICortexSyncReceiptIssuer receiptIssuer,
        ICortexProjectLinkRepository projectLinks,
        ICortexIntegratedCurrentRecoveryRetention retention,
        ICortexProjectBriefHistoryReadModel projectBriefs,
        CortexLocalAuthority authority,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _publishedHeads = publishedHeads ?? throw new ArgumentNullException(nameof(publishedHeads));
        _receiptIssuer = receiptIssuer ?? throw new ArgumentNullException(nameof(receiptIssuer));
        _projectLinks = projectLinks ?? throw new ArgumentNullException(nameof(projectLinks));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _projectBriefs = projectBriefs ?? throw new ArgumentNullException(nameof(projectBriefs));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<CortexSyncCapabilitiesV1?> GetSyncCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CortexSyncCapabilitiesV1?>(
            HasRequiredCapabilities() ? Capabilities : null);
    }

    public async ValueTask<CortexSyncRemoteHead?> GetSyncHeadAsync(
        CortexSyncHeadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasRequiredCapabilities())
            return null;
        var link = await ResolveExactLinkAsync(
            request.ProjectLinkId,
            request.StoreId,
            request.StoreEpoch,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;

        var scope = RepositoryScope(link, "cortex.sync.gateway.head");
        var published = await _publishedHeads.FindLatestPublishedAsync(
            new CortexSyncPublishedHeadLookup(scope),
            cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            return CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.SyncGatewayHead,
                CortexSyncContractIds.Canonicalization,
                exists: false,
                request.ProjectLinkId,
                request.StoreId,
                request.StoreEpoch,
                request.ProjectId,
                highWaterSequence: 0,
                lastEventId: null,
                CortexSyncContractIds.EmptySha256,
                lastBundleId: null,
                CortexSyncContractIds.EmptySha256,
                authorityHash: null,
                retainedPayloadHash: null,
                retainedSnapshotRevision: 0,
                retainedSnapshotHash: null,
                contextSetId: null,
                serverRevision: null,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                CortexSyncContractIds.EmptySha256));
        }

        return await BuildPublishedHeadAsync(
            link,
            published,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexSyncSubmitResult?> SubmitSyncAsync(
        CortexSyncSubmitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasRequiredCapabilities() || !ValidGatewayRequest(request))
            return null;
        var bundle = request.Bundle;
        var link = await ResolveExactLinkAsync(
            request.ProjectLinkId,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            cancellationToken).ConfigureAwait(false);
        if (link is null)
            return null;

        var scope = RepositoryScope(link, "cortex.sync.gateway.submit");
        var existing = await _publishedHeads.FindPublishedAsync(
            new CortexSyncPublishedBundleLookup(
                scope,
                bundle.BundleId,
                bundle.BundleHash),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await BuildSubmitResultAsync(
                link,
                existing,
                cancellationToken).ConfigureAwait(false);
        }

        var materializer = new CortexIntegratedCurrentRecoveryMaterializer(
            link,
            _retention,
            _projectBriefs,
            _authority);
        var service = new CortexSyncApplicationService(
            _repository,
            _receiptIssuer,
            _timeProvider,
            materializer);
        var submitted = await service.SubmitAsync(
            new SubmitCortexSyncBundleCommand(
                link.Metadata.ProjectNamespace,
                bundle,
                _authority.SyncAuthority(
                    link.Metadata.ProjectNamespace,
                    [AgentTaskSnapshotContractIds.Snapshot])),
            cancellationToken).ConfigureAwait(false);
        if (!submitted.Succeeded || submitted.Receipt is null)
            return null;

        var published = await _publishedHeads.FindPublishedAsync(
            new CortexSyncPublishedBundleLookup(
                scope,
                bundle.BundleId,
                bundle.BundleHash),
            cancellationToken).ConfigureAwait(false);
        if (published is null ||
            !CortexCanonicalJson.Serialize(submitted.Receipt).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(published.Receipt)))
        {
            return null;
        }
        return await BuildSubmitResultAsync(
            link,
            published,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CortexSyncSubmitResult?> BuildSubmitResultAsync(
        CortexProjectLinkV1 link,
        CortexSyncPublishedJournalHead published,
        CancellationToken cancellationToken)
    {
        var head = await BuildPublishedHeadAsync(
            link,
            published,
            cancellationToken).ConfigureAwait(false);
        if (head is null || !head.Exists)
            return null;
        var result = CortexSyncContractPolicy.Seal(new CortexSyncSubmitResult(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncGatewaySubmitResult,
            CortexSyncContractIds.Canonicalization,
            published.Receipt,
            head,
            CortexSyncContractIds.EmptySha256));
        return CortexCanonicalJson.Serialize(result).Length <
               CortexSyncGatewayLimits.NexusMaximumRequestBytes
            ? result
            : null;
    }

    private async ValueTask<CortexSyncRemoteHead?> BuildPublishedHeadAsync(
        CortexProjectLinkV1 link,
        CortexSyncPublishedJournalHead published,
        CancellationToken cancellationToken)
    {
        if (!ValidPublishedEvidence(link, published) ||
            !await _receiptIssuer.VerifyProofAsync(
                published.Receipt,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var verifier = new CortexIntegratedCurrentRecoveryMaterializer(
            link,
            _retention,
            _projectBriefs,
            _authority);
        var projection = await verifier.VerifyPublishedAsync(
            published.Bundle,
            cancellationToken).ConfigureAwait(false);
        if (projection is null)
            return null;

        var lastEvent = published.Bundle.Events[^1];
        return CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncGatewayHead,
            CortexSyncContractIds.Canonicalization,
            exists: true,
            link.Metadata.ProjectLinkId,
            published.Bundle.StoreId,
            published.Bundle.StoreEpoch,
            published.Bundle.ProjectId,
            lastEvent.Sequence,
            lastEvent.EventId,
            lastEvent.EventHash,
            published.Bundle.BundleId,
            published.Bundle.BundleHash,
            projection.AuthoritySetHash,
            lastEvent.PayloadSha256,
            projection.Snapshot.SnapshotRevision,
            projection.Snapshot.SnapshotHash,
            projection.Brief.ContextSetId,
            published.Commit.StorageRevision,
            published.ReceiptRecordedAt,
            CortexSyncContractIds.EmptySha256));
    }

    private static bool ValidGatewayRequest(CortexSyncSubmitRequest request)
    {
        if (!CortexSyncContractPolicy.HasValidHash(request))
            return false;
        return ValidGatewayBundle(request.Bundle) &&
            CortexCanonicalJson.Serialize(request).Length <
                CortexSyncGatewayLimits.NexusMaximumRequestBytes;
    }

    private static bool ValidGatewayBundle(CortexOfflineBundleV1 bundle) =>
        CortexSyncContractPolicy.HasValidHash(bundle) &&
            bundle.Events.Count <= CortexSyncGatewayLimits.MaximumEventsPerBundle &&
            bundle.Events.Count <= CortexSyncGatewayLimits.MaximumReceiptOutcomes &&
            bundle.Events.All(item =>
                item.PayloadBytes <= CortexSyncGatewayLimits.MaximumEventPayloadBytes) &&
            bundle.Extensions.Count == 0 &&
            CortexCanonicalJson.Serialize(bundle).Length <=
                CortexSyncGatewayLimits.MaximumCanonicalBundleBytes;

    private bool ValidPublishedEvidence(
        CortexProjectLinkV1 link,
        CortexSyncPublishedJournalHead published) =>
        ValidGatewayBundle(published.Bundle) &&
        published.Commit.Outcomes.Count <= CortexSyncGatewayLimits.MaximumReceiptOutcomes &&
        published.Receipt.Outcomes.Count <= CortexSyncGatewayLimits.MaximumReceiptOutcomes &&
        published.Receipt.Outcomes.Count == published.Bundle.Events.Count &&
        string.Equals(published.Scope.TenantId, _authority.TenantId, StringComparison.Ordinal) &&
        string.Equals(published.Scope.Namespace, link.Metadata.ProjectNamespace, StringComparison.Ordinal) &&
        string.Equals(published.Scope.StoreId, link.Metadata.StoreId, StringComparison.Ordinal) &&
        string.Equals(published.Scope.StoreEpoch, CanonicalStoreEpoch(link.Metadata.StoreEpoch), StringComparison.Ordinal) &&
        string.Equals(published.Scope.ProjectId, link.Metadata.ProjectId, StringComparison.Ordinal) &&
        string.Equals(published.Bundle.StoreId, link.Metadata.StoreId, StringComparison.Ordinal) &&
        string.Equals(published.Bundle.StoreEpoch, CanonicalStoreEpoch(link.Metadata.StoreEpoch), StringComparison.Ordinal) &&
        string.Equals(published.Bundle.ProjectId, link.Metadata.ProjectId, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.StoreId, published.Bundle.StoreId, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.StoreEpoch, published.Bundle.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.ProjectId, published.Bundle.ProjectId, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.BundleId, published.Bundle.BundleId, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.BundleHash, published.Bundle.BundleHash, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.Durability.CommitId, published.Commit.CommitId, StringComparison.Ordinal) &&
        string.Equals(published.Receipt.Durability.StorageRevision, published.Commit.StorageRevision, StringComparison.Ordinal);

    private bool HasRequiredCapabilities() =>
        _authority.HasCapability(CortexLocalCapability.Sync) &&
        _authority.HasCapability(CortexLocalCapability.RetainedRead) &&
        _authority.HasCapability(CortexLocalCapability.RetainedWrite) &&
        _authority.HasCapability(CortexLocalCapability.ProjectBrief);

    private async ValueTask<CortexProjectLinkV1?> ResolveExactLinkAsync(
        string projectLinkId,
        string storeId,
        string storeEpoch,
        string projectId,
        CancellationToken cancellationToken)
    {
        var link = await _projectLinks.FindAsync(
            _authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
            projectLinkId,
            cancellationToken).ConfigureAwait(false);
        return link is not null &&
               string.Equals(link.Metadata.EndpointId, _authority.EndpointId, StringComparison.Ordinal) &&
               string.Equals(link.Metadata.ProjectId, projectId, StringComparison.Ordinal) &&
               string.Equals(link.Metadata.StoreId, storeId, StringComparison.Ordinal) &&
               string.Equals(
                   CanonicalStoreEpoch(link.Metadata.StoreEpoch),
                   storeEpoch,
                   StringComparison.Ordinal)
            ? link
            : null;
    }

    private CortexSyncRepositoryScope RepositoryScope(
        CortexProjectLinkV1 link,
        string operation) =>
        new(
            _authority.TenantId,
            link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            _authority.SubjectId,
            "pegasus-cortex-sync",
            _authority.PolicyRevision,
            link.Metadata.StoreId,
            CanonicalStoreEpoch(link.Metadata.StoreEpoch),
            link.Metadata.ProjectId,
            operation);

    internal static string CanonicalStoreEpoch(long storeEpoch) =>
        storeEpoch.ToString(CultureInfo.InvariantCulture);
}

internal sealed record CortexIntegratedVerifiedProjection(
    AgentTaskSnapshotV1 Snapshot,
    CortexProjectBriefSnapshot Brief,
    string AuthoritySetHash);

/// <summary>
/// Deterministic materializer for the one reconnect payload admitted by the
/// gateway. It does no networking and never interprets retained task data as
/// execution authority.
/// </summary>
internal sealed class CortexIntegratedCurrentRecoveryMaterializer :
    ICortexSyncBundleMaterializer
{
    private readonly CortexProjectLinkV1 _link;
    private readonly ICortexIntegratedCurrentRecoveryRetention _retention;
    private readonly ICortexProjectBriefHistoryReadModel _projectBriefs;
    private readonly CortexLocalAuthority _authority;

    public CortexIntegratedCurrentRecoveryMaterializer(
        CortexProjectLinkV1 link,
        ICortexIntegratedCurrentRecoveryRetention retention,
        ICortexProjectBriefHistoryReadModel projectBriefs,
        CortexLocalAuthority authority)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _projectBriefs = projectBriefs ?? throw new ArgumentNullException(nameof(projectBriefs));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public ValueTask<CortexSyncMaterializationResult> PreflightAsync(
        CortexSyncMaterializationPreflight request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateScope(request.Scope, request.Bundle);
            _ = DecodeSnapshots(request.Bundle);
            return ValueTask.FromResult(CortexSyncMaterializationResult.Success);
        }
        catch (Exception exception) when (IsInvalidPayload(exception))
        {
            return ValueTask.FromResult(Failure(
                "cortex_sync_current_recovery_invalid",
                "The reconnect bundle is not a canonical current-recovery snapshot chain.",
                retryable: false));
        }
    }

    public async ValueTask<CortexSyncMaterializationResult> MaterializeAndVerifyAsync(
        CortexSyncMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<(CortexOfflineEventV1 Event, AgentTaskSnapshotV1 Snapshot)> snapshots;
        try
        {
            ValidateScope(request.Scope, request.Bundle);
            snapshots = DecodeSnapshots(request.Bundle);
        }
        catch (Exception exception) when (IsInvalidPayload(exception))
        {
            return Failure(
                "cortex_sync_current_recovery_invalid",
                "The durable reconnect bundle no longer matches its validated payload.",
                retryable: false);
        }

        foreach (var item in snapshots)
        {
            CortexRetainedAssertionWriteResult? write;
            try
            {
                write = await _retention.SubmitTaskSnapshotAsync(
                    _link.Metadata.ProjectLinkId,
                    _link.Metadata.ProjectNamespace,
                    item.Snapshot,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failure(
                    "cortex_sync_current_recovery_write_failed",
                    "The retained current-recovery snapshot could not be stored.",
                    retryable: true);
            }

            if (write is null ||
                write.Disposition is not (
                    CortexRetentionWriteDispositions.Appended or
                    CortexRetentionWriteDispositions.Idempotent) ||
                !string.Equals(write.AssertionHash, item.Snapshot.SnapshotHash, StringComparison.Ordinal) ||
                !string.Equals(write.SubmittedContentHash, item.Event.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(write.RetainedContentHash, item.Event.PayloadSha256, StringComparison.Ordinal) ||
                write.ProjectionIssueCode is not null ||
                write.ProjectBriefDisposition is not (
                    CortexRetentionWriteDispositions.Appended or
                    CortexRetentionWriteDispositions.Idempotent or
                    CortexProjectBriefMaterializationDispositions.AlreadyAdvanced))
            {
                return Failure(
                    "cortex_sync_current_recovery_write_unverified",
                    "The retained snapshot or project-brief projection was not durably verified.",
                    retryable: false);
            }
        }

        return await VerifyPublishedAsync(request.Bundle, cancellationToken).ConfigureAwait(false) is null
            ? Failure(
                "cortex_sync_current_recovery_readback_mismatch",
                "The retained current-recovery snapshot and project brief did not read back exactly.",
                retryable: true)
            : CortexSyncMaterializationResult.Success;
    }

    internal async ValueTask<CortexIntegratedVerifiedProjection?> VerifyPublishedAsync(
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(CortexOfflineEventV1 Event, AgentTaskSnapshotV1 Snapshot)> values;
        try
        {
            values = DecodeSnapshots(bundle);
        }
        catch (Exception exception) when (IsInvalidPayload(exception))
        {
            return null;
        }
        var latest = values[^1];
        var retained = await _retention.ReadRetainedAssertionAsync(
            _link.Metadata.ProjectLinkId,
            _link.Metadata.ProjectId,
            _link.Metadata.ProjectNamespace,
            CortexRetainedDocumentKinds.TaskSnapshot,
            latest.Snapshot.TaskSetId,
            latest.Snapshot.SnapshotRevision,
            latest.Event.PayloadSha256,
            cancellationToken).ConfigureAwait(false);
        if (retained is null ||
            !string.Equals(retained.AssertionHash, latest.Snapshot.SnapshotHash, StringComparison.Ordinal) ||
            !retained.CanonicalUtf8.AsSpan().SequenceEqual(DecodePayload(latest.Event)))
        {
            return null;
        }

        var briefScope = BriefScope();
        var brief = await _projectBriefs.FindBySourceAsync(
            briefScope,
            latest.Snapshot.SnapshotRevision,
            latest.Snapshot.SnapshotHash,
            cancellationToken).ConfigureAwait(false);
        var authoritySetHash = CortexProjectBriefService.ComputeAuthoritySetHash(
            latest.Snapshot.SourceRevisions);
        if (brief is null ||
            !await CortexIntegratedCurrentRecoveryBriefVerifier.MatchesAsync(
                _projectBriefs,
                brief,
                briefScope,
                latest.Snapshot,
                authoritySetHash,
                retained.ContentHash,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new CortexIntegratedVerifiedProjection(latest.Snapshot, brief, authoritySetHash);
    }

    private void ValidateScope(CortexSyncRepositoryScope scope, CortexOfflineBundleV1 bundle)
    {
        if (!string.Equals(scope.TenantId, _authority.TenantId, StringComparison.Ordinal) ||
            !string.Equals(scope.Namespace, _link.Metadata.ProjectNamespace, StringComparison.Ordinal) ||
            !string.Equals(scope.StoreId, _link.Metadata.StoreId, StringComparison.Ordinal) ||
            !string.Equals(
                scope.StoreEpoch,
                CortexIntegratedCurrentRecoverySyncGateway.CanonicalStoreEpoch(
                    _link.Metadata.StoreEpoch),
                StringComparison.Ordinal) ||
            !string.Equals(scope.ProjectId, _link.Metadata.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(bundle.StoreId, _link.Metadata.StoreId, StringComparison.Ordinal) ||
            !string.Equals(
                bundle.StoreEpoch,
                CortexIntegratedCurrentRecoverySyncGateway.CanonicalStoreEpoch(
                    _link.Metadata.StoreEpoch),
                StringComparison.Ordinal) ||
            !string.Equals(bundle.ProjectId, _link.Metadata.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The reconnect bundle does not match its server-derived project scope.");
        }
    }

    private IReadOnlyList<(CortexOfflineEventV1 Event, AgentTaskSnapshotV1 Snapshot)>
        DecodeSnapshots(CortexOfflineBundleV1 bundle)
    {
        if (!CortexSyncContractPolicy.HasValidHash(bundle) || bundle.Events.Count == 0)
            throw new ArgumentException("The reconnect bundle hash is invalid.");
        var values = new List<(CortexOfflineEventV1, AgentTaskSnapshotV1)>(bundle.Events.Count);
        foreach (var item in bundle.Events)
        {
            if (!string.Equals(
                    item.PayloadContractId,
                    AgentTaskSnapshotContractIds.Snapshot,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("Only task snapshots are admitted by reconnect synchronization.");
            }
            var payload = DecodePayload(item);
            var snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(payload);
            if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
                !CortexCanonicalJson.Serialize(snapshot).AsSpan().SequenceEqual(payload) ||
                !string.Equals(snapshot.ProjectId, _link.Metadata.ProjectId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.ProjectLinkId, _link.Metadata.ProjectLinkId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.TaskSetId, CortexCurrentTaskSnapshotFactory.TaskSetId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The event payload is not the exact current-recovery snapshot.");
            }
            if (values.Count != 0 &&
                !AgentTaskSnapshotPolicy.ValidateRevision(values[^1].Item2, snapshot).Valid)
            {
                throw new ArgumentException("The current-recovery snapshots do not form one exact revision chain.");
            }
            values.Add((item, snapshot));
        }
        return values;
    }

    private CortexProjectBriefReadScope BriefScope() =>
        new(
            _authority.TenantId,
            _link.Metadata.ProjectLinkId,
            _link.Metadata.ProjectId,
            _link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            _authority.SubjectId,
            "pegasus-cortex-mcp",
            _authority.PolicyRevision);

    private static byte[] DecodePayload(CortexOfflineEventV1 value)
    {
        var padded = value.PayloadBase64Url.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("The payload is not canonical base64url.")
        };
        var bytes = Convert.FromBase64String(padded);
        if (bytes.Length != value.PayloadBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(bytes), value.PayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The decoded payload does not match its declared hash and length.");
        }
        return bytes;
    }

    private static bool IsInvalidPayload(Exception exception) =>
        exception is ArgumentException or InvalidDataException or JsonException or
            InvalidOperationException or NotSupportedException;

    private static CortexSyncMaterializationResult Failure(
        string code,
        string message,
        bool retryable) =>
        new(false, code, message, retryable);
}
