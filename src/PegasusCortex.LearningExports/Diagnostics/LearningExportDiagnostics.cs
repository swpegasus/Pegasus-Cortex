using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.LearningExports.Diagnostics;

internal static class LearningExportDiagnostics
{
    public const string Operation = "cortex.learning-export";

    public static PegasusDiagnostic Error(
        string code,
        string message,
        string? recordId = null,
        string? revisionId = null,
        string? cause = null,
        string? boundary = null)
    {
        var context = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(recordId))
            context["recordId"] = recordId;
        if (!string.IsNullOrWhiteSpace(revisionId))
            context["revisionId"] = revisionId;
        if (!string.IsNullOrWhiteSpace(boundary))
            context["boundary"] = boundary;

        return new PegasusDiagnostic(
            code,
            message,
            DiagnosticSeverity.Error,
            Operation,
            retryable: false,
            context,
            cause);
    }

    public static PegasusDiagnostic Information(string code, string message) =>
        new(code, message, DiagnosticSeverity.Information, Operation);

    public static PegasusDiagnostic Warning(string code, string message, string? cause = null) =>
        new(code, message, DiagnosticSeverity.Warning, Operation, retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal), cause);
}
