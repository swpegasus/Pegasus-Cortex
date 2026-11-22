using Pegasus.Cortex.Persistence.PostgreSql.Connections;

namespace Pegasus.Cortex.Service.Diagnostics;

public enum CortexPostgreSqlDiagnosticSeverity
{
    Error,
    Critical
}

public sealed record CortexPostgreSqlDiagnostic(
    CortexPostgreSqlDiagnosticSeverity Severity,
    string Code,
    string Operation,
    string CauseType,
    DateTimeOffset ObservedAt);

public interface ICortexPostgreSqlDiagnosticSink
{
    void Report(CortexPostgreSqlDiagnostic diagnostic);
}

/// <summary>
/// Adapts provider pool-poisoning faults to a bounded, secret-free service diagnostic.
/// </summary>
public sealed class PostgreSqlFaultSink(ICortexPostgreSqlDiagnosticSink sink)
    : IPostgreSqlDataSourceFaultSink
{
    private readonly ICortexPostgreSqlDiagnosticSink _sink =
        sink ?? throw new ArgumentNullException(nameof(sink));

    public void Report(PostgreSqlDataSourceFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        _sink.Report(new CortexPostgreSqlDiagnostic(
            CortexPostgreSqlDiagnosticSeverity.Critical,
            Sanitize(fault.Code, "cortex_postgresql_fault_code_invalid", 128),
            Sanitize(fault.Operation, "cortex.postgresql.unknown", 128),
            Sanitize(fault.CauseType, "System.Exception", 256),
            fault.ObservedAt));
    }

    private static string Sanitize(string? value, string fallback, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(character => character is < ' ' or > '~'))
        {
            return fallback;
        }

        return value;
    }
}
