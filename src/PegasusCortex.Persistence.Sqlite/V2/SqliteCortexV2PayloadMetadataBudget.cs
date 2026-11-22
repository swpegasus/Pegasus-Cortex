namespace Pegasus.Cortex.Persistence.Sqlite.V2;

// One owned invocation only. It counts admitted metadata observations, not
// SQLite's internal visited pages or a hard wall-clock I/O deadline.
internal sealed class SqliteCortexPayloadMetadataBudget(int maximum, CancellationToken token)
{
    internal int Observed { get; private set; }
    internal int PublishedObjects { get; private set; }
    internal void Charge(int count = 1)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > maximum - Observed) throw new SqliteCortexPayloadBudgetExceededException();
        Observed += count;
    }
    internal void Published() => PublishedObjects++;
}

internal sealed class SqliteCortexPayloadBudgetExceededException : Exception { }
