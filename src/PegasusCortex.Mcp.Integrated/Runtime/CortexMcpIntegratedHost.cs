using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Service.Configuration;
using Pegasus.Cortex.Service.Persistence;

namespace Pegasus.Cortex.Mcp.Integrated.Runtime;

internal sealed record CortexMcpIntegratedHostResult(
    CortexMcpIntegratedRuntime? Runtime,
    CortexMcpRuntimeComposition? Composition,
    CortexLocalAuthority? Authority,
    string State,
    string? IssueCode)
{
    public bool Available =>
        Runtime is not null && Composition is not null && Authority is not null;
}

internal static class CortexMcpIntegratedHost
{
    private const string AuthorityFileVariable = "PEGASUS_CORTEX_AUTHORITY_FILE";
    private const string RowKeyFileVariable = "PEGASUS_CORTEX_ROW_KEY_FILE";
    private const string RuntimeConnectionVariable = "PEGASUS_CORTEX_RUNTIME_CONNECTION";
    private const string ReceiptKeyFileVariable = "PEGASUS_CORTEX_RECEIPT_KEY_FILE";

    public static async ValueTask<CortexMcpIntegratedHostResult> TryCreateAsync(
        CancellationToken cancellationToken)
    {
        var authorityPath = Environment.GetEnvironmentVariable(AuthorityFileVariable);
        var keyPath = Environment.GetEnvironmentVariable(RowKeyFileVariable);
        var runtimeConnection = Environment.GetEnvironmentVariable(RuntimeConnectionVariable);
        var configured = new[] { authorityPath, keyPath, runtimeConnection }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        if (configured == 0)
            return new CortexMcpIntegratedHostResult(
                null,
                null,
                null,
                "not-configured",
                "cortex_hybrid_not_configured");
        if (configured != 3)
            return new CortexMcpIntegratedHostResult(
                null,
                null,
                null,
                "not-ready",
                "cortex_hybrid_configuration_incomplete");

        HmacSha256CortexRowAuthenticator? authenticator = null;
        HmacSha256CortexSyncReceiptIssuer? receiptIssuer = null;
        CortexPostgreSqlComposition? composition = null;
        try
        {
            var authority = CortexLocalAuthority.Load(authorityPath!);
            if (authority.HasCapability(CortexLocalCapability.Sync) &&
                (!authority.HasCapability(CortexLocalCapability.RetainedRead) ||
                 !authority.HasCapability(CortexLocalCapability.RetainedWrite) ||
                 !authority.HasCapability(CortexLocalCapability.ProjectBrief)))
            {
                return new CortexMcpIntegratedHostResult(
                    null,
                    null,
                    null,
                    "not-ready",
                    "cortex_hybrid_sync_retention_capabilities_incomplete");
            }
            if (authority.HasCapability(CortexLocalCapability.Sync))
            {
                var receiptKeyPath = Environment.GetEnvironmentVariable(ReceiptKeyFileVariable);
                if (string.IsNullOrWhiteSpace(receiptKeyPath))
                {
                    return new CortexMcpIntegratedHostResult(
                        null,
                        null,
                        null,
                        "not-ready",
                        "cortex_hybrid_receipt_authority_missing");
                }
                receiptIssuer = HmacSha256CortexSyncReceiptIssuer.Load(
                    receiptKeyPath,
                    Environment.GetEnvironmentVariable("PEGASUS_CORTEX_RECEIPT_KEY_ID") ??
                    "cortex-sync-receipt-v1",
                    Environment.GetEnvironmentVariable("PEGASUS_CORTEX_SERVER_INSTANCE_ID") ??
                    authority.EndpointId);
            }
            authenticator = HmacSha256CortexRowAuthenticator.Load(
                keyPath!,
                Environment.GetEnvironmentVariable("PEGASUS_CORTEX_ROW_KEY_ID") ??
                "local-hmac-v1");
            var tlsPolicy = ParseTlsPolicy(
                Environment.GetEnvironmentVariable("PEGASUS_CORTEX_TLS_POLICY"));
            var rootCertificate = Environment.GetEnvironmentVariable("PEGASUS_CORTEX_ROOT_CA");
            var serverSpkiPin = Environment.GetEnvironmentVariable(
                "PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256");
            var options = new CortexPostgreSqlOptions(
                runtimeConnection!,
                migrationConnectionString: null,
                rootCertificate,
                runtimeMaximumPoolSize: 4,
                tlsPolicy: tlsPolicy,
                serverCertificateSpkiSha256: serverSpkiPin);
            composition = CortexPostgreSqlComposition.Create(options, authenticator);
            var sentinel = new CortexQuerySourceScope(
                authority.TenantId,
                "local-cortex-policy",
                $"{authority.PrincipalName}-readiness",
                "pegasus-cortex-mcp",
                authority.PolicyRevision,
                CortexProjectCorrelationContract.ControlNamespace,
                CortexQueryCapability.Status,
                "cortex.query.status",
                "restricted",
                IncludeStale: false,
                IncludeBlockedOrUnverified: false);
            var readiness = await composition.ProbeReadinessAsync(
                sentinel,
                requireStoreSentinel: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (readiness.State != PostgreSqlReadinessState.Ready)
            {
                await composition.DisposeAsync().ConfigureAwait(false);
                authenticator.Dispose();
                receiptIssuer?.Dispose();
                return new CortexMcpIntegratedHostResult(
                    null,
                    null,
                    null,
                    "not-ready",
                    readiness.IssueCodes.FirstOrDefault() ?? "cortex_hybrid_postgresql_not_ready");
            }

            if (authority.HasCapability(CortexLocalCapability.Sync))
            {
                var syncReadiness = await composition.ProbeSyncReadinessAsync(
                    new CortexSyncRepositoryScope(
                        authority.TenantId,
                        CortexProjectCorrelationContract.ControlNamespace,
                        "local-cortex-policy",
                        authority.SubjectId,
                        "pegasus-cortex-sync",
                        authority.PolicyRevision,
                        "readiness-store",
                        "readiness-epoch",
                        "readiness-project",
                        "cortex.postgresql.sync.readiness"),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (syncReadiness.State != PostgreSqlReadinessState.Ready)
                {
                    await composition.DisposeAsync().ConfigureAwait(false);
                    authenticator.Dispose();
                    receiptIssuer?.Dispose();
                    return new CortexMcpIntegratedHostResult(
                        null,
                        null,
                        null,
                        "not-ready",
                        syncReadiness.IssueCodes.FirstOrDefault() ??
                            "cortex_hybrid_sync_not_ready");
                }
            }

            var querySource = composition.CreateQuerySource();
            var learningRepository = composition.CreateLearningRepository();
            var projectLinks = composition.CreateProjectLinkRepository();
            var hasRetention = authority.HasCapability(
                CortexLocalCapability.RetainedRead) ||
                authority.HasCapability(CortexLocalCapability.RetainedWrite) ||
                authority.HasCapability(CortexLocalCapability.ProjectBrief);
            var projectBriefReadModel = hasRetention
                ? composition.CreateProjectBriefReadModel()
                : null;
            var retentionRuntime = hasRetention
                ? new CortexMcpIntegratedRetentionRuntime(
                    composition,
                    projectLinks,
                    projectBriefReadModel!,
                    authority)
                : null;
            var syncRepository = authority.HasCapability(CortexLocalCapability.Sync)
                ? composition.CreateSyncRepository()
                : null;
            var syncGateway = syncRepository is null
                ? null
                : new CortexIntegratedCurrentRecoverySyncGateway(
                    syncRepository,
                    syncRepository as ICortexSyncPublishedHeadRepositoryV2 ??
                        throw new InvalidOperationException(
                            "The integrated synchronization repository cannot replay exact published bundles."),
                    receiptIssuer!,
                    projectLinks,
                    retentionRuntime!,
                    projectBriefReadModel!,
                    authority);
            var runtime = new CortexMcpIntegratedRuntime(
                composition,
                authenticator,
                querySource,
                projectLinks,
                learningRepository,
                authority,
                projectBriefReadModel,
                syncRepository: syncRepository,
                syncReceiptIssuer: receiptIssuer);
            composition = null;
            authenticator = null;
            receiptIssuer = null;
            var descriptor = new CortexMcpRuntimeDescriptor(
                "integrated-postgresql",
                "ready",
                IssueCode: null,
                authority.EndpointId,
                authority.PrincipalName,
                authority.SubjectId,
                MapCapabilities(authority.Capabilities),
                UsesNetwork: true,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: true,
                "Use local .cortex files for current task authority and paths; use a confirmed projectLinkId for durable retained knowledge. Synchronization is available only through the Nexus-owned gateway.",
                StorageKind: CortexMcpStorageKinds.PostgreSql,
                SupportsProvisionalPublication:
                    authority.AutoPublishSafeCollectiveProposals);
            var ports = new CortexMcpRuntimeComposition(
                descriptor,
                ProjectKnowledge: runtime,
                ProjectCorrelation: runtime,
                ProposalSubmission: runtime,
                ProposalQuery: runtime,
                ProposalReview: runtime,
                ProposalAudit: runtime,
                ReviewGrant: runtime,
                ProjectBrief: authority.HasCapability(CortexLocalCapability.ProjectBrief)
                    ? runtime
                    : null,
                RetainedAssertionReader: authority.HasCapability(
                        CortexLocalCapability.RetainedRead)
                    ? retentionRuntime
                    : null,
                RetainedAssertionWriter: authority.HasCapability(
                        CortexLocalCapability.RetainedWrite)
                    ? retentionRuntime
                    : null,
                Sync: authority.HasCapability(CortexLocalCapability.Sync)
                    ? runtime
                    : null,
                SyncGateway: syncGateway,
                PublicationChange:
                    learningRepository is ILearningFeedbackPublicationChangeRepository &&
                    authority.HasCapability(CortexLocalCapability.Propose)
                        ? runtime
                        : null);
            return new CortexMcpIntegratedHostResult(
                runtime,
                ports,
                authority,
                "ready",
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (composition is not null)
                await composition.DisposeAsync().ConfigureAwait(false);
            authenticator?.Dispose();
            receiptIssuer?.Dispose();
            return new CortexMcpIntegratedHostResult(
                null,
                null,
                null,
                "not-ready",
                "cortex_hybrid_configuration_or_readiness_failed");
        }
    }

    internal static CortexPostgreSqlTlsPolicy ParseTlsPolicy(string? value) => value switch
    {
        null or "" or "verify-full" => CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull,
        "loopback-bootstrap" => CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap,
        "trusted-loopback" => CortexPostgreSqlTlsPolicy.TrustedLoopback,
        "pinned-spki" => CortexPostgreSqlTlsPolicy.Pinned,
        _ => throw new InvalidOperationException("The Cortex PostgreSQL TLS policy is unsupported.")
    };

    private static CortexMcpRuntimeCapability MapCapabilities(
        CortexLocalCapability capabilities)
    {
        var mapped = CortexMcpRuntimeCapability.None;
        if (capabilities.HasFlag(CortexLocalCapability.Read))
            mapped |= CortexMcpRuntimeCapability.Read;
        if (capabilities.HasFlag(CortexLocalCapability.Correlate))
            mapped |= CortexMcpRuntimeCapability.Correlate;
        if (capabilities.HasFlag(CortexLocalCapability.Propose))
            mapped |= CortexMcpRuntimeCapability.Propose;
        if (capabilities.HasFlag(CortexLocalCapability.Review))
            mapped |= CortexMcpRuntimeCapability.Review;
        if (capabilities.HasFlag(CortexLocalCapability.Audit))
            mapped |= CortexMcpRuntimeCapability.Audit;
        if (capabilities.HasFlag(CortexLocalCapability.DelegateReview))
            mapped |= CortexMcpRuntimeCapability.DelegateReview;
        if (capabilities.HasFlag(CortexLocalCapability.RetainedRead))
            mapped |= CortexMcpRuntimeCapability.RetainedRead;
        if (capabilities.HasFlag(CortexLocalCapability.RetainedWrite))
            mapped |= CortexMcpRuntimeCapability.RetainedWrite;
        if (capabilities.HasFlag(CortexLocalCapability.ProjectBrief))
            mapped |= CortexMcpRuntimeCapability.ProjectBrief;
        if (capabilities.HasFlag(CortexLocalCapability.Sync))
            mapped |= CortexMcpRuntimeCapability.Sync;
        return mapped;
    }
}
