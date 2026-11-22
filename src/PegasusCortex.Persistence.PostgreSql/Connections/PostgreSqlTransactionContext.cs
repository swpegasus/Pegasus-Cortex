using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;

namespace Pegasus.Cortex.Persistence.PostgreSql.Connections;

internal sealed record PostgreSqlTransactionContext(
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string Capability,
    string Operation,
    string MaximumSensitivity,
    bool AllowStale,
    bool AllowBlockedOrUnverified)
{
    public static PostgreSqlTransactionContext FromQuery(CortexQuerySourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new PostgreSqlTransactionContext(
            scope.TenantId,
            scope.Namespace,
            scope.AuthorityId,
            scope.SubjectId,
            scope.ServiceId,
            scope.PolicyRevision,
            scope.Capability.ToString().ToLowerInvariant(),
            scope.Operation,
            scope.MaximumSensitivity,
            scope.IncludeStale,
            scope.IncludeBlockedOrUnverified);
    }

    public static PostgreSqlTransactionContext FromLearning(
        LearningFeedbackNamespaceAccess access,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(access);
        return new PostgreSqlTransactionContext(
            access.TenantId,
            access.Namespace,
            access.AuthorityId,
            access.SubjectId,
            access.ServiceId,
            access.PolicyRevision,
            LearningCapabilityToken(access.Capability),
            operation,
            "restricted",
            AllowStale: false,
            AllowBlockedOrUnverified: false);
    }

    // These values are part of the PostgreSQL RLS contract. Keep the mapping
    // explicit so multi-word enum names cannot silently drift from SQL tokens.
    private static string LearningCapabilityToken(
        LearningFeedbackNamespaceCapability capability) =>
        capability switch
        {
            LearningFeedbackNamespaceCapability.Read => "read",
            LearningFeedbackNamespaceCapability.Submit => "submit",
            LearningFeedbackNamespaceCapability.Review => "review",
            LearningFeedbackNamespaceCapability.Audit => "audit",
            LearningFeedbackNamespaceCapability.DelegateReview => "delegate_review",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };

    public static PostgreSqlTransactionContext FromProjectLink(
        CortexProjectLinkAccess access,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(access);
        return new PostgreSqlTransactionContext(
            access.TenantId,
            access.Namespace,
            access.AuthorityId,
            access.SubjectId,
            access.ServiceId,
            access.PolicyRevision,
            access.Capability.ToString().ToLowerInvariant(),
            operation,
            "internal",
            AllowStale: false,
            AllowBlockedOrUnverified: false);
    }

    public static PostgreSqlTransactionContext FromReadiness(CortexQuerySourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new PostgreSqlTransactionContext(
            scope.TenantId,
            scope.Namespace,
            scope.AuthorityId,
            scope.SubjectId,
            scope.ServiceId,
            scope.PolicyRevision,
            "readiness",
            "cortex.postgresql.readiness.store",
            scope.MaximumSensitivity,
            AllowStale: false,
            AllowBlockedOrUnverified: false);
    }

    public static PostgreSqlTransactionContext FromSync(CortexSyncRepositoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new PostgreSqlTransactionContext(
            scope.TenantId,
            scope.Namespace,
            scope.AuthorityId,
            scope.SubjectId,
            scope.ServiceId,
            scope.PolicyRevision,
            "sync",
            scope.Operation,
            "restricted",
            AllowStale: false,
            AllowBlockedOrUnverified: false);
    }

    public static PostgreSqlTransactionContext FromRetention(
        PostgreSqlCortexRetentionScope scope,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var captured = scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new PostgreSqlTransactionContext(
            captured.TenantId,
            captured.Namespace,
            captured.AuthorityId,
            captured.SubjectId,
            captured.ServiceId,
            captured.PolicyRevision,
            captured.Capability switch
            {
                PostgreSqlCortexRetentionCapability.Read => "retained_read",
                PostgreSqlCortexRetentionCapability.Write => "retained_write",
                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            },
            operation,
            "internal",
            AllowStale: false,
            AllowBlockedOrUnverified: false);
    }
}
