using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>Opt-in actual SQLite statement/trigger trace events, never SQL text or inferred fanout.
/// This is not an ADO command count, VM-step count or a count of every engine-internal operation.</summary>
public sealed class CortexSqliteTraceMeasurement : IDisposable
{
    private static readonly AsyncLocal<CortexSqliteTraceMeasurement?> Current = new();
    private readonly CortexSqliteTraceMeasurement? _previous;
    private long _events, _connections, _failures;
    private int _disposed;
    private CortexSqliteTraceMeasurement() { _previous = Current.Value; Current.Value = this; }
    public static CortexSqliteTraceMeasurement Begin() => new();
    public long StatementTraceEvents => Interlocked.Read(ref _events);
    public long Connections => Interlocked.Read(ref _connections);
    public long AttachmentFailures => Interlocked.Read(ref _failures);

    internal static SqliteConnection Observe(SqliteConnection connection)
    {
        var scope = Current.Value;
        if (scope is null) return connection;
        // V2 deliberately disables pooling; the native callback dies with this connection.
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState != ConnectionState.Open) return;
            try
            {
                raw.sqlite3_trace(connection.Handle!, (delegate_trace)((_, _) =>
                {
                    if (Volatile.Read(ref scope._disposed) == 0)
                        Interlocked.Increment(ref scope._events);
                }), scope);
                Interlocked.Increment(ref scope._connections);
            }
            catch (Exception) { Interlocked.Increment(ref scope._failures); }
        };
        return connection;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Current.Value = _previous;
    }
}
