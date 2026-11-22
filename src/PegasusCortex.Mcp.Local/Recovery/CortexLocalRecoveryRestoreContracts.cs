namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal static class CortexLocalRecoveryRestoreContractIds
{
    public const string Result =
        "pegasus.cortex.local-recovery-restore-result/1.0";
}

internal sealed record CortexLocalRecoveryRestoreResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string ExportRunId,
    int RestoredEntries,
    long RestoredBytes,
    long RetainedDocuments,
    long AuditEvents,
    string ProjectBriefContextSetId,
    string ExportedProjectBriefContextSetId,
    bool ContextLineageReset,
    bool HistoricalProjectBriefChainRestored,
    bool UsesNetwork,
    bool SourceMutationAllowed,
    bool FallbackRequired,
    string? DiagnosticCode,
    string RecoveryInstruction);
