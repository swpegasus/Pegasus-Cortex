using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.Sync;

/// <summary>
/// Versioned canonical identities for one immutable PostgreSQL synchronization attempt.
/// These recipes are intentionally provider-local until promoted into a public wire contract.
/// </summary>
internal static class PostgreSqlSyncHashPolicy
{
    internal const string AttemptContractId = "pegasus.cortex.sync-attempt-id/1.0";
    internal const string CommitContractId = "pegasus.cortex.sync-commit-id/1.0";
    internal const string StorageRevisionContractId = "pegasus.cortex.sync-storage-revision/1.0";
    internal const string DurableStateContractId = "pegasus.cortex.sync-durable-state/1.0";

    internal static string AttemptId(PostgreSqlSyncCapturedBundle captured) =>
        Identifier("at_", new AttemptIdentity(
            1,
            AttemptContractId,
            captured.Scope.TenantId,
            captured.Scope.Namespace,
            captured.Scope.StoreId,
            captured.Scope.StoreEpoch,
            captured.Scope.ProjectId,
            captured.CanonicalSha256));

    internal static string CommitId(
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        CortexSyncCommitDisposition disposition) =>
        Identifier("cm_", new CommitIdentity(
            1,
            CommitContractId,
            captured.Scope.TenantId,
            captured.Scope.Namespace,
            captured.Scope.StoreId,
            captured.Scope.StoreEpoch,
            captured.Scope.ProjectId,
            attemptId,
            captured.Bundle.BundleId,
            captured.Bundle.BundleHash,
            Disposition(disposition)));

    internal static string StorageRevision(
        PostgreSqlSyncCapturedBundle captured,
        string attemptId,
        CortexSyncCommitDisposition disposition) =>
        Identifier("sr_", new StorageIdentity(
            1,
            StorageRevisionContractId,
            captured.Scope.TenantId,
            captured.Scope.Namespace,
            captured.Scope.StoreId,
            captured.Scope.StoreEpoch,
            captured.Scope.ProjectId,
            captured.Bundle.BaseServerRevision,
            attemptId,
            captured.Bundle.BundleId,
            captured.Bundle.BundleHash,
            captured.Bundle.LastSequence,
            captured.Bundle.Events[^1].EventHash,
            Disposition(disposition)));

    internal static string DurableStateHash(PostgreSqlSyncDurableState state) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(new DurableStateEvidence(
            1,
            DurableStateContractId,
            state.TenantId,
            state.Namespace,
            state.StoreId,
            state.StoreEpoch,
            state.ProjectId,
            state.AttemptId,
            state.BundleId,
            state.BundleHash,
            state.CommitId,
            state.StorageRevision,
            Disposition(state.Disposition),
            PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(state.ReceivedAt),
            PostgreSqlSyncCanonicalCodec.PostgreSqlTimestamp(state.CommittedAt),
            state.Outcomes.Select(value => new DurableOutcome(
                    value.Sequence,
                    value.EventId,
                    value.EventHash,
                    value.Disposition,
                    value.Durable,
                    value.ServerEntityId,
                    value.ServerRevisionId,
                    value.DiagnosticCode))
                .ToArray(),
            state.SourceHead is null
                ? null
                : new DurableSourceHead(
                    state.SourceHead.BaseServerRevision,
                    state.SourceHead.BundleId,
                    state.SourceHead.StorageRevision,
                    state.SourceHead.SourceSequence,
                    state.SourceHead.EventId,
                    state.SourceHead.EventHash))));

    internal static string Disposition(CortexSyncCommitDisposition value) => value switch
    {
        CortexSyncCommitDisposition.Committed => "committed",
        CortexSyncCommitDisposition.Existing => "existing",
        CortexSyncCommitDisposition.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static CortexSyncCommitDisposition ParseDisposition(string value) => value switch
    {
        "committed" => CortexSyncCommitDisposition.Committed,
        "existing" => CortexSyncCommitDisposition.Existing,
        "conflict" => CortexSyncCommitDisposition.Conflict,
        _ => throw new ArgumentException("The durable synchronization disposition is invalid.", nameof(value))
    };

    private static string Identifier<T>(string prefix, T body) =>
        prefix + CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(body));

    private sealed record AttemptIdentity(
        int SchemaVersion,
        string ContractId,
        string TenantId,
        string Namespace,
        string StoreId,
        string StoreEpoch,
        string ProjectId,
        string BundleCanonicalSha256);

    private sealed record CommitIdentity(
        int SchemaVersion,
        string ContractId,
        string TenantId,
        string Namespace,
        string StoreId,
        string StoreEpoch,
        string ProjectId,
        string AttemptId,
        string BundleId,
        string BundleHash,
        string Disposition);

    private sealed record StorageIdentity(
        int SchemaVersion,
        string ContractId,
        string TenantId,
        string Namespace,
        string StoreId,
        string StoreEpoch,
        string ProjectId,
        string? BaseServerRevision,
        string AttemptId,
        string BundleId,
        string BundleHash,
        long LastSourceSequence,
        string TerminalEventHash,
        string Disposition);

    private sealed record DurableStateEvidence(
        int SchemaVersion,
        string ContractId,
        string TenantId,
        string Namespace,
        string StoreId,
        string StoreEpoch,
        string ProjectId,
        string AttemptId,
        string BundleId,
        string BundleHash,
        string CommitId,
        string StorageRevision,
        string Disposition,
        DateTimeOffset ReceivedAt,
        DateTimeOffset CommittedAt,
        IReadOnlyList<DurableOutcome> Outcomes,
        DurableSourceHead? SourceHead);

    private sealed record DurableOutcome(
        long Sequence,
        string EventId,
        string EventHash,
        string Disposition,
        bool Durable,
        string? ServerEntityId,
        string? ServerRevisionId,
        string? DiagnosticCode);

    private sealed record DurableSourceHead(
        string? BaseServerRevision,
        string BundleId,
        string StorageRevision,
        long SourceSequence,
        string EventId,
        string EventHash);
}

internal sealed record PostgreSqlSyncDurableState(
    string TenantId,
    string Namespace,
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    string AttemptId,
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision,
    CortexSyncCommitDisposition Disposition,
    DateTimeOffset ReceivedAt,
    DateTimeOffset CommittedAt,
    IReadOnlyList<CortexSyncReceiptOutcomeV1> Outcomes,
    PostgreSqlSyncSourceHead? SourceHead);

internal sealed record PostgreSqlSyncSourceHead(
    string? BaseServerRevision,
    string BundleId,
    string StorageRevision,
    long SourceSequence,
    string EventId,
    string EventHash);
