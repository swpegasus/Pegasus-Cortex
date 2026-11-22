using System.Security;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal enum MaintenanceReadStage
{
    LayoutResolution, PathValidation, RecordOpen, RecordSize, Protection,
    RecordParse, RecordValidation, RequiredRecord
}

/// <summary>Fixed public labels only; no exception messages, paths or policy values.</summary>
internal static class MaintenanceReadDiagnostics
{
    private const string StageKey="Pegasus.MaintenanceReadStage";
    private const string CauseKey="Pegasus.MaintenanceReadCause";
    private enum Cause { AccessDenied, Missing, PathRejected, SizeRejected, ProtectionRejected, InvalidRecord, Unavailable }

    internal static AgentOpsException Annotate(Exception error,MaintenanceReadStage stage)
    {
        // Preserve existing stable AgentOps codes and retain the underlying cause.
        var failure=error as AgentOpsException??new AgentOpsException("maintenance_state_unavailable",inner:error);
        failure.Data[StageKey]=stage;
        failure.Data[CauseKey]=error switch
        {
            UnauthorizedAccessException or SecurityException=>Cause.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException=>Cause.Missing,
            JsonException=>Cause.InvalidRecord,
            _=>stage switch
            {
                MaintenanceReadStage.RequiredRecord=>Cause.Missing,
                MaintenanceReadStage.PathValidation=>Cause.PathRejected,
                MaintenanceReadStage.RecordSize=>Cause.SizeRejected,
                MaintenanceReadStage.Protection=>Cause.ProtectionRejected,
                MaintenanceReadStage.RecordParse or MaintenanceReadStage.RecordValidation=>Cause.InvalidRecord,
                _=>Cause.Unavailable
            }
        };
        return failure;
    }

    internal static (string Stage,string Cause) Describe(Exception error) =>
        (error.Data[StageKey] switch
        {
            MaintenanceReadStage.LayoutResolution=>"layout-resolution",
            MaintenanceReadStage.PathValidation=>"path-validation",
            MaintenanceReadStage.RecordOpen=>"record-open",
            MaintenanceReadStage.RecordSize=>"record-size",
            MaintenanceReadStage.Protection=>"protection",
            MaintenanceReadStage.RecordParse=>"record-parse",
            MaintenanceReadStage.RecordValidation=>"record-validation",
            MaintenanceReadStage.RequiredRecord=>"required-record",
            _=>"state-read"
        },error.Data[CauseKey] switch
        {
            Cause.AccessDenied=>"access-denied", Cause.Missing=>"missing",
            Cause.PathRejected=>"path-rejected", Cause.SizeRejected=>"size-rejected",
            Cause.ProtectionRejected=>"protection-rejected", Cause.InvalidRecord=>"invalid-record",
            _=>"unavailable"
        });
}
