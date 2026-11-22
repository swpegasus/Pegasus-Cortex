using System.Diagnostics;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Core.V2;

internal enum CortexV2RetrievalOperation
{
    Start,
    Search,
    Read,
    BatchRead
}

/// <summary>
/// Process-local, content-free operation counters. Metrics contain no query,
/// namespace, record coordinate, title, excerpt, body, or provenance data.
/// </summary>
internal sealed class CortexV2RetrievalMetrics
{
    private readonly DateTimeOffset _startedAtUtc;
    private readonly Operation[] _operations =
        Enumerable.Range(0, 4).Select(_ => new Operation()).ToArray();
    private long _exactRecordsReturned;

    public CortexV2RetrievalMetrics(DateTimeOffset startedAtUtc) =>
        _startedAtUtc = startedAtUtc.ToUniversalTime();

    public void Record(
        CortexV2RetrievalOperation operation,
        long startedTimestamp,
        bool failed,
        int returnedItems,
        int responseUtf8Bytes)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        var elapsedMicroseconds = Math.Max(0L, checked(elapsedTicks * 1_000_000L / Stopwatch.Frequency));
        _operations[(int)operation].Record(failed, returnedItems, responseUtf8Bytes, elapsedMicroseconds);
        if (!failed && operation is CortexV2RetrievalOperation.Read or CortexV2RetrievalOperation.BatchRead)
            Interlocked.Add(ref _exactRecordsReturned, returnedItems);
    }

    public CortexRetrievalMetricsV2 Snapshot() => new(
        "cortex.retrieval-metrics/1",
        _startedAtUtc,
        _operations[(int)CortexV2RetrievalOperation.Start].Snapshot(),
        _operations[(int)CortexV2RetrievalOperation.Search].Snapshot(),
        _operations[(int)CortexV2RetrievalOperation.Read].Snapshot(),
        _operations[(int)CortexV2RetrievalOperation.BatchRead].Snapshot(),
        Interlocked.Read(ref _exactRecordsReturned));

    private sealed class Operation
    {
        private long _calls;
        private long _failures;
        private long _returnedItems;
        private long _responseUtf8Bytes;
        private long _totalMicroseconds;
        private long _maximumMicroseconds;

        public void Record(bool failed, int returnedItems, int responseUtf8Bytes, long elapsedMicroseconds)
        {
            Interlocked.Increment(ref _calls);
            if (failed)
                Interlocked.Increment(ref _failures);
            Interlocked.Add(ref _returnedItems, returnedItems);
            Interlocked.Add(ref _responseUtf8Bytes, responseUtf8Bytes);
            Interlocked.Add(ref _totalMicroseconds, elapsedMicroseconds);
            long observed;
            do
            {
                observed = Interlocked.Read(ref _maximumMicroseconds);
                if (observed >= elapsedMicroseconds)
                    break;
            }
            while (Interlocked.CompareExchange(
                ref _maximumMicroseconds,
                elapsedMicroseconds,
                observed) != observed);
        }

        public CortexRetrievalOperationMetricsV2 Snapshot() => new(
            Interlocked.Read(ref _calls),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _returnedItems),
            Interlocked.Read(ref _responseUtf8Bytes),
            Interlocked.Read(ref _totalMicroseconds) / 1_000d,
            Interlocked.Read(ref _maximumMicroseconds) / 1_000d);
    }
}
