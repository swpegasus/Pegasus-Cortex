namespace Pegasus.Cortex.Persistence.PostgreSql.Recovery;

public enum PostgreSqlRecoveryAction
{
    Continue,
    RollBackApplication,
    RestoreIntoIsolation,
    BlockAndPreserveEvidence
}

public sealed record PostgreSqlRecoveryEvidence(
    bool SourceSnapshotVerified,
    bool PreMigrationBackupVerified,
    bool MigrationTransactionCommitted,
    bool PreviousApplicationCompatible,
    bool RestoreDrillVerified,
    bool MigrationHistoryValid,
    bool RowAuthenticityValid,
    bool HeadProjectionValid,
    bool LexicalIndexRebuildVerified);

public sealed record PostgreSqlRecoveryDecision(
    PostgreSqlRecoveryAction Action,
    IReadOnlyList<string> ReasonCodes,
    bool DestructiveActionAuthorized);

/// <summary>
/// Deterministic, plan-only recovery policy. It never runs down migrations, overwrites
/// evidence, repairs rows, restores backups, or switches a live database automatically.
/// </summary>
public static class PostgreSqlRecoveryPolicy
{
    public static PostgreSqlRecoveryDecision Evaluate(PostgreSqlRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var reasons = new List<string>();
        if (!evidence.SourceSnapshotVerified)
            reasons.Add("cortex_recovery_source_snapshot_unverified");
        if (!evidence.MigrationHistoryValid)
            reasons.Add("cortex_recovery_migration_history_invalid");
        if (!evidence.RowAuthenticityValid)
            reasons.Add("cortex_recovery_row_authenticity_invalid");
        if (!evidence.HeadProjectionValid)
            reasons.Add("cortex_recovery_head_projection_invalid");
        if (!evidence.LexicalIndexRebuildVerified)
            reasons.Add("cortex_recovery_index_rebuild_unverified");

        if (reasons.Count == 0)
            return new PostgreSqlRecoveryDecision(PostgreSqlRecoveryAction.Continue, [], false);

        if (!evidence.MigrationTransactionCommitted)
        {
            // Transactional DDL leaves the prior schema authoritative; preserve evidence
            // and keep the existing application unless its own health check disagrees.
            return new PostgreSqlRecoveryDecision(
                PostgreSqlRecoveryAction.BlockAndPreserveEvidence,
                reasons.Order(StringComparer.Ordinal).ToArray(),
                false);
        }

        if (evidence.PreviousApplicationCompatible &&
            evidence.MigrationHistoryValid &&
            evidence.RowAuthenticityValid)
        {
            return new PostgreSqlRecoveryDecision(
                PostgreSqlRecoveryAction.RollBackApplication,
                reasons.Order(StringComparer.Ordinal).ToArray(),
                false);
        }

        if (evidence.PreMigrationBackupVerified && evidence.RestoreDrillVerified)
        {
            return new PostgreSqlRecoveryDecision(
                PostgreSqlRecoveryAction.RestoreIntoIsolation,
                reasons.Order(StringComparer.Ordinal).ToArray(),
                false);
        }

        reasons.Add("cortex_recovery_verified_restore_unavailable");
        return new PostgreSqlRecoveryDecision(
            PostgreSqlRecoveryAction.BlockAndPreserveEvidence,
            reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            false);
    }
}
