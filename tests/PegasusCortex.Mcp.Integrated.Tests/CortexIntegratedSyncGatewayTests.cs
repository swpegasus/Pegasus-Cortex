using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Integrated.Runtime;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;

internal static class CortexIntegratedSyncGatewayTests
{
    public static async Task ExactReplayScopeAndProofAsync()
    {
        using var fixture = new GatewayFixture();
        var empty = await fixture.Gateway.GetSyncHeadAsync(
            fixture.HeadRequest(), CancellationToken.None);
        Assert.True(empty is { Exists: false }, "A valid empty synchronized head was not returned.");
        Assert.True(CortexSyncContractPolicy.HasValidHash(empty!), "The empty head was not sealed.");

        var readsBeforeWrongScope = fixture.Repository.PublishedReadCount;
        Assert.Equal<CortexSyncRemoteHead?>(null, await fixture.Gateway.GetSyncHeadAsync(
            fixture.HeadRequest(storeId: "wrong-store"), CancellationToken.None));
        Assert.Equal<CortexSyncRemoteHead?>(null, await fixture.Gateway.GetSyncHeadAsync(
            fixture.HeadRequest(storeEpoch: "07"), CancellationToken.None));
        Assert.Equal(readsBeforeWrongScope, fixture.Repository.PublishedReadCount);

        var snapshotA = fixture.Snapshot(1, previousHash: null);
        var bundleA = fixture.Bundle(snapshotA, 1, null, null, null);
        var resultA = await fixture.Gateway.SubmitSyncAsync(
            fixture.Submit(bundleA), CancellationToken.None);
        Assert.True(resultA is not null, "The first exact current-recovery bundle was rejected.");
        var bytesA = CortexCanonicalJson.Serialize(resultA!);

        var snapshotB = fixture.Snapshot(2, snapshotA.SnapshotHash);
        var bundleB = fixture.Bundle(
            snapshotB,
            2,
            resultA!.Head.ServerRevision,
            bundleA.BundleHash,
            bundleA.Events[^1].EventHash);
        var resultB = await fixture.Gateway.SubmitSyncAsync(
            fixture.Submit(bundleB), CancellationToken.None);
        Assert.True(resultB is not null, "The successor current-recovery bundle was rejected.");
        Assert.False(bytesA.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(resultB!)),
            "The successor incorrectly reproduced the predecessor result.");

        var replayA = await fixture.Gateway.SubmitSyncAsync(
            fixture.Submit(bundleA), CancellationToken.None);
        Assert.True(replayA is not null, "The historical exact retry was rejected after head advancement.");
        Assert.True(bytesA.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(replayA!)),
            "Bundle A did not return its byte-identical original result after bundle B advanced.");
        Assert.Equal(2, fixture.Repository.CommitCount);
        Assert.Equal(2, fixture.Repository.StoredReceiptCount);

        fixture.Repository.TamperPublishedReceipt = true;
        Assert.Equal<CortexSyncRemoteHead?>(null, await fixture.Gateway.GetSyncHeadAsync(
            fixture.HeadRequest(), CancellationToken.None));
        fixture.Repository.TamperPublishedReceipt = false;

        Func<CortexProjectBriefSnapshot, CortexProjectBriefSnapshot>[] mutations =
        [
            brief => brief with { TenantId = "different-tenant" },
            brief => brief with { AuthoritySetHash = new string('f', 64) },
            brief => brief with { BriefRevision = brief.BriefRevision + 1 },
            brief => brief with { PreviousContextSetId = null },
            brief => brief with
            {
                CurrentTask = brief.CurrentTask with
                {
                    Gate = brief.CurrentTask.Gate with { Summary = "tampered gate" }
                }
            },
            brief => brief with
            {
                CurrentTask = brief.CurrentTask with
                {
                    Source = brief.CurrentTask.Source with
                    {
                        Reference = $"hash://{new string('e', 64)}"
                    }
                }
            },
            brief => brief with
            {
                Freshness = brief.Freshness with { Basis = "tampered freshness" }
            },
            brief => brief with { CheckpointReferences = [] },
            brief => brief with
            {
                EvidenceReferences = [brief.CheckpointReferences[0]]
            }
        ];
        foreach (var mutation in mutations)
        {
            fixture.Briefs.Mutate = brief =>
            {
                if (brief.StateRevision != snapshotB.SnapshotRevision)
                    return brief;
                var changed = mutation(brief);
                return changed with
                {
                    ContextSetId = CortexProjectBriefService.ComputeContextSetId(changed)
                };
            };
            Assert.Equal<CortexSyncRemoteHead?>(null, await fixture.Gateway.GetSyncHeadAsync(
                fixture.HeadRequest(), CancellationToken.None));
        }
    }

    public static async Task LimitsFailuresAndConcurrencyAsync()
    {
        using (var fixture = new GatewayFixture())
        {
            var oversized = fixture.OversizedBundle();
            Assert.Equal<CortexSyncSubmitResult?>(null, await fixture.Gateway.SubmitSyncAsync(
                fixture.Submit(oversized), CancellationToken.None));
            Assert.Equal(0, fixture.Repository.CommitCount);

            fixture.Retention.FailWrites = true;
            var snapshot = fixture.Snapshot(1, previousHash: null);
            Assert.Equal<CortexSyncSubmitResult?>(null, await fixture.Gateway.SubmitSyncAsync(
                fixture.Submit(fixture.Bundle(snapshot, 1, null, null, null)),
                CancellationToken.None));
            Assert.Equal(0, fixture.Repository.StoredReceiptCount);
        }

        using (var fixture = new GatewayFixture())
        {
            fixture.Repository.ForceConflict = true;
            var snapshot = fixture.Snapshot(1, previousHash: null);
            Assert.Equal<CortexSyncSubmitResult?>(null, await fixture.Gateway.SubmitSyncAsync(
                fixture.Submit(fixture.Bundle(snapshot, 1, null, null, null)),
                CancellationToken.None));
            Assert.Equal(0, fixture.Retention.WriteCount);
            Assert.Equal(0, fixture.Repository.StoredReceiptCount);
        }

        using (var fixture = new GatewayFixture())
        {
            var snapshot = fixture.Snapshot(1, previousHash: null);
            var request = fixture.Submit(fixture.Bundle(snapshot, 1, null, null, null));
            var values = await Task.WhenAll(
                fixture.Gateway.SubmitSyncAsync(request, CancellationToken.None).AsTask(),
                fixture.Gateway.SubmitSyncAsync(request, CancellationToken.None).AsTask());
            Assert.True(values.All(value => value is not null),
                "A concurrent exact retry failed to return a result.");
            Assert.True(CortexCanonicalJson.Serialize(values[0]!).AsSpan().SequenceEqual(
                    CortexCanonicalJson.Serialize(values[1]!)),
                "Concurrent exact retries did not converge on one result.");
            Assert.Equal(1, fixture.Repository.StoredReceiptCount);
        }
    }

    private sealed class GatewayFixture : IDisposable
    {
        private const string TenantId = "local";
        private const string EndpointId = "pegasus-cortex-test";
        private const string ProjectId = "project-1";
        private const string StoreId = "store-1";
        private const long StoreEpoch = 7;
        private static readonly DateTimeOffset Now =
            DateTimeOffset.Parse("2026-08-03T12:00:00+00:00");
        private readonly string _root;
        private readonly HmacSha256CortexSyncReceiptIssuer _issuer;

        public GatewayFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"cortex-gateway-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            var authorityPath = Path.Combine(_root, "authority.json");
            var receiptKeyPath = Path.Combine(_root, "receipt-key.hex");
            File.WriteAllText(authorityPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                tenantId = TenantId,
                endpointId = EndpointId,
                policyRevision = 7,
                principals = new[]
                {
                    new
                    {
                        name = Environment.UserName,
                        read = true,
                        correlate = true,
                        propose = false,
                        review = false,
                        subjectId = "gateway-test-subject",
                        retainedRead = true,
                        retainedWrite = true,
                        projectBrief = true,
                        sync = true
                    }
                }
            }));
            File.WriteAllText(receiptKeyPath, new string('b', 64));
            Authority = CortexLocalAuthority.Load(authorityPath);
            var access = Authority.ProjectLinkAccess(CortexProjectLinkCapability.Correlate);
            var prepared = CortexProjectCorrelationPolicy.Prepare(
                access,
                ProjectId,
                StoreId,
                StoreEpoch,
                EndpointId,
                "sync-v1");
            Link = CortexProjectCorrelationPolicy.Confirm(
                access,
                prepared,
                prepared.CorrelationHash);
            Briefs = new GatewayBriefStore();
            Retention = new GatewayRetention(TenantId, Link, Briefs);
            Repository = new GatewayRepository(Now);
            _issuer = HmacSha256CortexSyncReceiptIssuer.Load(
                receiptKeyPath,
                "gateway-test-key",
                "gateway-test-server");
            Gateway = new CortexIntegratedCurrentRecoverySyncGateway(
                Repository,
                Repository,
                _issuer,
                new GatewayProjectLinks(Link),
                Retention,
                Briefs,
                Authority,
                new GatewayClock(Now));
        }

        public CortexLocalAuthority Authority { get; }
        public CortexProjectLinkV1 Link { get; }
        public GatewayBriefStore Briefs { get; }
        public GatewayRetention Retention { get; }
        public GatewayRepository Repository { get; }
        public CortexIntegratedCurrentRecoverySyncGateway Gateway { get; }

        public CortexSyncHeadRequest HeadRequest(
            string? storeId = null,
            string? storeEpoch = null) =>
            new(Link.Metadata.ProjectLinkId, storeId ?? StoreId,
                storeEpoch ?? StoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProjectId);

        public CortexSyncSubmitRequest Submit(CortexOfflineBundleV1 bundle) =>
            CortexSyncContractPolicy.Seal(new CortexSyncSubmitRequest(
                Link.Metadata.ProjectLinkId,
                bundle));

        public AgentTaskSnapshotV1 Snapshot(long revision, string? previousHash)
        {
            var capturedAt = Now.AddMinutes(revision);
            var task = new AgentTaskV1(
                $"task-{revision}",
                parentTaskId: null,
                ordinal: 0,
                AgentTaskStateIds.Active,
                $"Recover revision {revision}.",
                $"Revision {revision} is current.",
                $"Continue revision {revision}.",
                constraints: [],
                dependencyTaskIds: [],
                blockers: [],
                knownFailures: [],
                reportedOwner: null,
                new AgentWorkApprovalGateV1(
                    AgentWorkApprovalGateStateIds.NotRequired,
                    authorityId: null,
                    approvalReferenceId: null,
                    "No approval is asserted as required.",
                    grantsAuthority: false),
                evidenceIds: [],
                sourceRevisionIds: ["source-current"]);
            return AgentTaskSnapshotPolicy.Seal(new AgentTaskSnapshotV1(
                AgentTaskSnapshotContractIds.SchemaVersion,
                AgentTaskSnapshotContractIds.Snapshot,
                AgentWorkPacketContractIds.Canonicalization,
                AgentCoordinationModeIds.Standalone,
                CortexCurrentTaskSnapshotFactory.TaskSetId,
                $"snapshot-{revision}",
                revision,
                previousHash,
                $"task-idempotency-{revision}",
                ProjectId,
                Link.Metadata.ProjectLinkId,
                $"ctx_{revision.ToString().PadLeft(64, '0')}",
                capturedAt,
                capturedAt.AddHours(24),
                new AgentWorkOwnerV1("nexus-authority", "cortex-agent-1", "pegasus-cortex"),
                new AgentWorkFreshnessV1(
                    AgentWorkFreshnessStateIds.Current,
                    capturedAt,
                    capturedAt.AddHours(24),
                    "The source revision was verified."),
                [new AgentWorkSourceRevisionV1(
                    "source-current",
                    $"revision-{revision}",
                    new string((char)('a' + revision), 64))],
                evidenceReferences: [],
                AgentTaskSnapshotContractIds.Coverage,
                [task],
                AgentWorkPacketContractIds.ContentTreatment,
                executable: false,
                grantsSchedulingAuthority: false,
                grantsTaskClaim: false,
                grantsAuthority: false,
                AgentTaskSnapshotContractIds.SafetyNotice,
                AgentWorkPacketContractIds.EmptySha256));
        }

        public CortexOfflineBundleV1 Bundle(
            AgentTaskSnapshotV1 snapshot,
            long sequence,
            string? baseRevision,
            string? previousBundleHash,
            string? priorEventHash)
        {
            var payload = CortexCanonicalJson.Serialize(snapshot);
            var payloadHash = CortexCanonicalJson.Sha256(payload);
            var occurredAt = Now.AddMinutes(sequence);
            var eventValue = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.OfflineEvent,
                CortexSyncContractIds.Canonicalization,
                StoreId,
                StoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProjectId,
                sequence,
                $"event-{sequence}",
                priorEventHash ?? CortexSyncContractIds.EmptySha256,
                occurredAt,
                "producer-1",
                $"operation-{sequence}",
                TenantId,
                Link.Metadata.ProjectNamespace,
                7,
                AgentTaskSnapshotContractIds.Snapshot,
                Base64Url(payload),
                payload.Length,
                payloadHash,
                new CortexSyncSourceProvenanceV1(
                    "client_operation", null, null, payload.Length, payloadHash, occurredAt),
                CortexSyncContractIds.ContentTreatment,
                executable: false,
                [],
                CortexSyncContractIds.EmptySha256));
            return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.OfflineBundle,
                CortexSyncContractIds.Canonicalization,
                $"bundle-{sequence}",
                StoreId,
                StoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProjectId,
                baseRevision,
                previousBundleHash ?? CortexSyncContractIds.EmptySha256,
                priorEventHash ?? CortexSyncContractIds.EmptySha256,
                sequence,
                sequence,
                occurredAt,
                [eventValue],
                [],
                CortexSyncContractIds.EmptySha256));
        }

        public CortexOfflineBundleV1 OversizedBundle()
        {
            // The generic sync contract accepts canonical typed JSON and the
            // integrated gateway owns the tighter per-event transport limit.
            var payload = CortexCanonicalJson.Serialize(new Dictionary<string, object?>
            {
                ["contractId"] = AgentTaskSnapshotContractIds.Snapshot,
                ["filler"] = new string('x', CortexSyncGatewayLimits.MaximumEventPayloadBytes),
                ["schemaVersion"] = AgentTaskSnapshotContractIds.SchemaVersion
            });
            Assert.True(payload.Length > CortexSyncGatewayLimits.MaximumEventPayloadBytes,
                "The rejection fixture did not exceed the integrated gateway payload limit.");
            var hash = CortexCanonicalJson.Sha256(payload);
            var item = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.OfflineEvent,
                CortexSyncContractIds.Canonicalization,
                StoreId,
                StoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProjectId,
                1,
                "oversized-event",
                CortexSyncContractIds.EmptySha256,
                Now,
                "producer-1",
                "oversized-operation",
                TenantId,
                Link.Metadata.ProjectNamespace,
                7,
                AgentTaskSnapshotContractIds.Snapshot,
                Base64Url(payload),
                payload.Length,
                hash,
                new CortexSyncSourceProvenanceV1(
                    "client_operation", null, null, payload.Length, hash, Now),
                CortexSyncContractIds.ContentTreatment,
                executable: false,
                [],
                CortexSyncContractIds.EmptySha256));
            return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.OfflineBundle,
                CortexSyncContractIds.Canonicalization,
                "oversized-bundle",
                StoreId,
                StoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProjectId,
                baseServerRevision: null,
                CortexSyncContractIds.EmptySha256,
                CortexSyncContractIds.EmptySha256,
                1,
                1,
                Now,
                [item],
                [],
                CortexSyncContractIds.EmptySha256));
        }

        public void Dispose()
        {
            _issuer.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class GatewayProjectLinks(CortexProjectLinkV1 link) :
        ICortexProjectLinkRepository
    {
        public ValueTask<CortexProjectLinkV1?> FindAsync(
            CortexProjectLinkAccess access,
            string projectLinkId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CortexProjectLinkV1?>(
                string.Equals(projectLinkId, link.Metadata.ProjectLinkId, StringComparison.Ordinal)
                    ? link
                    : null);

        public ValueTask<IReadOnlyList<CortexProjectLinkV1>> ListAsync(
            CortexProjectLinkAccess access,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CortexProjectLinkWriteResult> AddAsync(
            CortexProjectLinkAccess access,
            CortexProjectLinkV1 value,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class GatewayBriefStore :
        ICortexProjectBriefReadModel,
        ICortexProjectBriefHistoryReadModel
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CortexProjectBriefSnapshot> _bySource =
            new(StringComparer.Ordinal);
        private CortexProjectBriefSnapshot? _current;

        public Func<CortexProjectBriefSnapshot, CortexProjectBriefSnapshot>? Mutate { get; set; }

        public CortexProjectBriefSnapshot? Current
        {
            get { lock (_gate) return _current; }
        }

        public void Store(CortexProjectBriefSnapshot value)
        {
            lock (_gate)
            {
                _bySource[value.SourceHash] = value;
                if (_current is null || value.StateRevision > _current.StateRevision)
                    _current = value;
            }
        }

        public ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
            CortexProjectBriefReadScope scope,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                return ValueTask.FromResult(Apply(_current));
        }

        public ValueTask<CortexProjectBriefSnapshot?> FindAsync(
            CortexProjectBriefReadScope scope,
            string contextSetId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                return ValueTask.FromResult(Apply(_bySource.Values.FirstOrDefault(value =>
                    string.Equals(value.ContextSetId, contextSetId, StringComparison.Ordinal))));
        }

        public ValueTask<CortexProjectBriefSnapshot?> FindBySourceAsync(
            CortexProjectBriefReadScope scope,
            long stateRevision,
            string sourceHash,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _bySource.TryGetValue(sourceHash, out var value);
                return ValueTask.FromResult(Apply(
                    value?.StateRevision == stateRevision ? value : null));
            }
        }

        private CortexProjectBriefSnapshot? Apply(CortexProjectBriefSnapshot? value) =>
            value is null || Mutate is null ? value : Mutate(value);
    }

    private sealed class GatewayRetention(
        string tenantId,
        CortexProjectLinkV1 link,
        GatewayBriefStore briefs) : ICortexIntegratedCurrentRecoveryRetention
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<long, (AgentTaskSnapshotV1 Snapshot, byte[] Canonical)> _snapshots = [];
        public bool FailWrites { get; set; }
        public int WriteCount { get; private set; }

        public async ValueTask<CortexRetainedAssertionWriteResult?> SubmitTaskSnapshotAsync(
            string projectLinkId,
            string projectNamespace,
            AgentTaskSnapshotV1 snapshot,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            if (FailWrites)
                return null;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var canonical = CortexCanonicalJson.Serialize(snapshot);
                var contentHash = CortexCanonicalJson.Sha256(canonical);
                if (_snapshots.TryGetValue(snapshot.SnapshotRevision, out var existing))
                {
                    return existing.Canonical.AsSpan().SequenceEqual(canonical)
                        ? WriteResult(snapshot, contentHash, CortexRetentionWriteDispositions.Idempotent,
                            briefs.Current?.ContextSetId)
                        : null;
                }
                _snapshots[snapshot.SnapshotRevision] = (snapshot, canonical);
                var scope = new CortexProjectBriefReadScope(
                    tenantId,
                    link.Metadata.ProjectLinkId,
                    link.Metadata.ProjectId,
                    link.Metadata.ProjectNamespace,
                    "local-cortex-policy",
                    "gateway-test-subject",
                    "pegasus-cortex-mcp",
                    7);
                var previous = briefs.Current;
                if (previous is null || previous.StateRevision < snapshot.SnapshotRevision)
                {
                    var checkpoint = CortexProjectBriefReferencePolicy.CreateRetained(
                        projectLinkId,
                        snapshot.ProjectId,
                        projectNamespace,
                        CortexRetainedDocumentKinds.TaskSnapshot,
                        snapshot.TaskSetId,
                        snapshot.SnapshotRevision,
                        contentHash,
                        snapshot.SnapshotHash);
                    briefs.Store(CortexProjectBriefProjector.Project(
                        snapshot,
                        new CortexProjectBriefProjectionInput(
                            snapshot.ProjectId,
                            scope,
                            [checkpoint],
                            [],
                            SkippedSourceChainProof: null),
                        previous));
                }
                return WriteResult(
                    snapshot,
                    contentHash,
                    CortexRetentionWriteDispositions.Appended,
                    briefs.Current?.ContextSetId);
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask<CortexRetainedAssertionRecord?> ReadRetainedAssertionAsync(
            string projectLinkId,
            string projectId,
            string projectNamespace,
            string kind,
            string documentId,
            long revision,
            string contentHash,
            CancellationToken cancellationToken)
        {
            if (!_snapshots.TryGetValue(revision, out var value) ||
                !string.Equals(CortexCanonicalJson.Sha256(value.Canonical), contentHash, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<CortexRetainedAssertionRecord?>(null);
            }
            return ValueTask.FromResult<CortexRetainedAssertionRecord?>(new(
                tenantId,
                projectLinkId,
                projectId,
                projectNamespace,
                kind,
                documentId,
                revision,
                value.Snapshot.SnapshotHash,
                contentHash,
                contentHash,
                value.Snapshot.ContractId,
                value.Canonical.ToArray()));
        }

        private CortexRetainedAssertionWriteResult WriteResult(
            AgentTaskSnapshotV1 snapshot,
            string contentHash,
            string disposition,
            string? contextSetId) => new(
            disposition,
            link.Metadata.ProjectLinkId,
            snapshot.ProjectId,
            link.Metadata.ProjectNamespace,
            CortexRetainedDocumentKinds.TaskSnapshot,
            snapshot.TaskSetId,
            snapshot.SnapshotRevision,
            snapshot.SnapshotHash,
            contentHash,
            contentHash,
            contentHash,
            disposition,
            contextSetId,
            ProjectionIssueCode: null);
    }

    private sealed class GatewayRepository(DateTimeOffset now) :
        ICortexSyncRepository,
        ICortexSyncPublishedHeadRepositoryV2
    {
        private sealed record Stored(
            CortexSyncRepositoryScope Scope,
            CortexOfflineBundleV1 Bundle,
            CortexSyncCommitSnapshot Commit,
            CortexSyncReceiptV1? Receipt,
            byte[]? CanonicalReceipt,
            DateTimeOffset? ReceiptRecordedAt);

        private readonly object _gate = new();
        private readonly Dictionary<string, Stored> _stored = new(StringComparer.Ordinal);
        public bool ForceConflict { get; set; }
        public bool TamperPublishedReceipt { get; set; }
        public int CommitCount { get; private set; }
        public int PublishedReadCount { get; private set; }
        public int StoredReceiptCount
        {
            get { lock (_gate) return _stored.Values.Count(value => value.Receipt is not null); }
        }

        public ValueTask<CortexSyncCommitSnapshot> CommitAsync(
            CortexSyncCommitRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CommitCount++;
                if (_stored.TryGetValue(request.Bundle.BundleHash, out var existing))
                    return ValueTask.FromResult(existing.Commit.WithDisposition(CortexSyncCommitDisposition.Existing));

                var disposition = ForceConflict
                    ? CortexSyncCommitDisposition.Conflict
                    : CortexSyncCommitDisposition.Committed;
                var outcome = new CortexSyncReceiptOutcomeV1(
                    request.Bundle.Events[^1].Sequence,
                    request.Bundle.Events[^1].EventId,
                    request.Bundle.Events[^1].EventHash,
                    disposition == CortexSyncCommitDisposition.Conflict
                        ? CortexSyncDispositionIds.Conflict
                        : CortexSyncDispositionIds.Applied,
                    durable: true,
                    disposition == CortexSyncCommitDisposition.Conflict ? null : "retained-entity",
                    disposition == CortexSyncCommitDisposition.Conflict ? null : $"retained-{request.Bundle.LastSequence}",
                    disposition == CortexSyncCommitDisposition.Conflict ? "sync_conflict" : null);
                var commit = new CortexSyncCommitSnapshot(
                    disposition,
                    $"commit-{request.Bundle.LastSequence}",
                    $"storage-{request.Bundle.LastSequence}",
                    request.ReceivedAt,
                    now.AddSeconds(request.Bundle.LastSequence),
                    CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(request.Bundle)),
                    [outcome]);
                if (!ForceConflict)
                    _stored[request.Bundle.BundleHash] = new Stored(
                        request.Scope, request.Bundle, commit, null, null, null);
                return ValueTask.FromResult(commit);
            }
        }

        public ValueTask<CortexSyncReadBackSnapshot> ReadBackAsync(
            CortexSyncReadBackRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stored = _stored[request.BundleHash];
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
                    stored.Bundle.Events.Count,
                    stored.Bundle.Events.Count,
                    stored.Commit.CommittedAt.AddSeconds(1),
                    stored.Commit.DurableStateHash));
            }
        }

        public ValueTask<CortexSyncReceiptAppendSnapshot> AppendReceiptAsync(
            CortexSyncReceiptAppendRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stored = _stored[request.Receipt.BundleHash];
                var recordedAt = stored.ReceiptRecordedAt ??
                    request.Receipt.Reconciliation.ReadBackAt!.Value.AddSeconds(1);
                var disposition = stored.Receipt is null
                    ? CortexSyncReceiptAppendDisposition.Appended
                    : stored.CanonicalReceipt!.AsSpan().SequenceEqual(request.CanonicalReceipt.Span)
                        ? CortexSyncReceiptAppendDisposition.Existing
                        : CortexSyncReceiptAppendDisposition.Conflict;
                if (disposition != CortexSyncReceiptAppendDisposition.Conflict)
                {
                    _stored[request.Receipt.BundleHash] = stored with
                    {
                        Receipt = request.Receipt,
                        CanonicalReceipt = request.CanonicalReceipt.ToArray(),
                        ReceiptRecordedAt = recordedAt
                    };
                }
                return ValueTask.FromResult(new CortexSyncReceiptAppendSnapshot(
                    disposition,
                    request.Scope.TenantId,
                    request.Scope.Namespace,
                    request.Scope.StoreId,
                    request.Scope.StoreEpoch,
                    request.Scope.ProjectId,
                    request.Receipt.BundleId,
                    request.Receipt.ReceiptId,
                    request.Receipt.ReceiptHash,
                    request.CanonicalSha256,
                    recordedAt));
            }
        }

        public ValueTask<CortexSyncReceiptReadBackSnapshot?> FindReceiptAsync(
            CortexSyncReceiptLookupRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _stored.TryGetValue(request.BundleHash, out var stored) &&
                    stored.Receipt is not null
                        ? ReadBack(stored)
                        : null);
            }
        }

        public ValueTask<CortexSyncReceiptReadBackSnapshot> ReadReceiptAsync(
            CortexSyncReceiptReadRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stored = _stored.Values.Single(value =>
                    string.Equals(value.Bundle.BundleId, request.BundleId, StringComparison.Ordinal));
                return ValueTask.FromResult(ReadBack(stored)!);
            }
        }

        public ValueTask<CortexSyncPublishedJournalHead?> FindLatestPublishedAsync(
            CortexSyncPublishedHeadLookup request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                PublishedReadCount++;
                var value = _stored.Values
                    .Where(item => item.Receipt is not null && SameScope(item.Scope, request.Scope))
                    .OrderByDescending(item => item.Bundle.LastSequence)
                    .FirstOrDefault();
                return ValueTask.FromResult(Published(value, request.Scope));
            }
        }

        public ValueTask<CortexSyncPublishedJournalHead?> FindPublishedAsync(
            CortexSyncPublishedBundleLookup request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                PublishedReadCount++;
                _stored.TryGetValue(request.BundleHash, out var value);
                return ValueTask.FromResult(
                    value is not null &&
                    string.Equals(value.Bundle.BundleId, request.BundleId, StringComparison.Ordinal) &&
                    SameScope(value.Scope, request.Scope)
                        ? Published(value, request.Scope)
                        : null);
            }
        }

        private CortexSyncPublishedJournalHead? Published(
            Stored? value,
            CortexSyncRepositoryScope scope)
        {
            if (value?.Receipt is null || value.ReceiptRecordedAt is null)
                return null;
            return new CortexSyncPublishedJournalHead(
                scope,
                value.Bundle,
                value.Commit,
                TamperPublishedReceipt ? Tamper(value.Receipt) : value.Receipt,
                value.ReceiptRecordedAt.Value);
        }

        private static CortexSyncReceiptReadBackSnapshot? ReadBack(Stored stored) =>
            stored.Receipt is null || stored.CanonicalReceipt is null || stored.ReceiptRecordedAt is null
                ? null
                : new CortexSyncReceiptReadBackSnapshot(
                    stored.Scope.TenantId,
                    stored.Scope.Namespace,
                    stored.Scope.StoreId,
                    stored.Scope.StoreEpoch,
                    stored.Scope.ProjectId,
                    stored.Bundle.BundleId,
                    stored.Receipt.ReceiptId,
                    stored.ReceiptRecordedAt.Value,
                    stored.CanonicalReceipt);

        private static bool SameScope(
            CortexSyncRepositoryScope left,
            CortexSyncRepositoryScope right) =>
            string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
            string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal) &&
            string.Equals(left.StoreId, right.StoreId, StringComparison.Ordinal) &&
            string.Equals(left.StoreEpoch, right.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal);

        private static CortexSyncReceiptV1 Tamper(CortexSyncReceiptV1 receipt)
        {
            var proof = new byte[32];
            var authentication = new CortexSyncReceiptAuthenticationV1(
                receipt.Authentication.ProofContractId,
                receipt.Authentication.KeyId,
                receipt.Authentication.AlgorithmId,
                receipt.ReceiptHash,
                Base64Url(proof),
                proof.Length,
                CortexCanonicalJson.Sha256(proof));
            return new CortexSyncReceiptV1(
                receipt.SchemaVersion,
                receipt.ContractId,
                receipt.Canonicalization,
                receipt.ReceiptId,
                receipt.ServerInstanceId,
                receipt.StoreId,
                receipt.StoreEpoch,
                receipt.ProjectId,
                receipt.BundleId,
                receipt.BundleHash,
                receipt.ReceivedAt,
                authentication,
                receipt.Durability,
                receipt.Outcomes,
                receipt.Reconciliation,
                receipt.Extensions,
                receipt.ReceiptHash);
        }
    }

    private sealed class GatewayClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CortexSyncCommitSnapshot WithDisposition(
        this CortexSyncCommitSnapshot value,
        CortexSyncCommitDisposition disposition) =>
        new(
            disposition,
            value.CommitId,
            value.StorageRevision,
            value.ReceivedAt,
            value.CommittedAt,
            value.DurableStateHash,
            value.Outcomes);

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
