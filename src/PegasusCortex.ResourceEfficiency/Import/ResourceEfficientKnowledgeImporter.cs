using System.Diagnostics;
using System.Text.Json;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Import;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Cortex.DefaultLibrary.Validation;
using Pegasus.Cortex.ResourceEfficiency.Concurrency;
using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Metrics;

namespace Pegasus.Cortex.ResourceEfficiency.Import;

public sealed class ResourceEfficientKnowledgeImporter : IAsyncDisposable
{
    private readonly ResourceEfficiencyOptions _options;
    private readonly IResourceMetricsSink _metrics;
    private readonly KnowledgeBundleImporter _importer;
    private readonly BoundedOperationGate _gate;

    public ResourceEfficientKnowledgeImporter(
        IKnowledgeRevisionRepository repository,
        ResourceEfficiencyOptions options,
        IResourceMetricsSink? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? NullResourceMetricsSink.Instance;
        _importer = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), repository);
        _gate = new BoundedOperationGate(
            options.MaxConcurrentImports,
            options.MaxQueuedImports,
            "cortex.resource.import",
            _metrics);
    }

    public ResourceGateSnapshot GateSnapshot => _gate.Snapshot;

    /// <summary>
    /// Parses incrementally from a bounded stream. Unless leaveOpen is true, this operation owns and disposes input.
    /// </summary>
    public async ValueTask<KnowledgeBundleImportResult> ImportAsync(
        Stream input,
        long? declaredLength,
        KnowledgeImportContext context,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        ResourceOperationLease? lease = null;
        var outcome = "rejected";

        try
        {
            if (IsKnownOversize(input, declaredLength))
            {
                return Reject(ResourceDiagnostics.Error(
                    "resource_import_oversize",
                    "The import exceeds the configured byte budget and was rejected before reading.",
                    "cortex.resource.import",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["maximumBytes"] = _options.MaxImportBytes.ToString()
                    }));
            }

            var entered = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Accepted)
                return Reject(entered.Diagnostic!);
            lease = entered.Lease;

            await using var bounded = new BoundedReadStream(input, _options.MaxImportBytes);
            KnowledgeSeedBundleV1? bundle;
            try
            {
                bundle = await JsonSerializer.DeserializeAsync<KnowledgeSeedBundleV1>(
                    bounded,
                    KnowledgeBundleSerializer.Options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceLimitExceededException)
            {
                return Reject(ResourceDiagnostics.Error(
                    "resource_import_oversize",
                    "The streaming import exceeded the configured byte budget.",
                    "cortex.resource.import"));
            }
            catch (JsonException)
            {
                return Reject(ResourceDiagnostics.Error(
                    "resource_import_json_invalid",
                    "The streaming import is not a valid Default Library bundle.",
                    "cortex.resource.import"));
            }

            if (bundle is null)
                return Reject(ResourceDiagnostics.Error("resource_import_empty", "The streaming import was empty.", "cortex.resource.import"));
            if (bundle.Records.Count > _options.MaxBundleRecords ||
                bundle.Records.Any(record => record.Evidence.Count > _options.MaxEvidencePerRecord))
            {
                return Reject(ResourceDiagnostics.Error(
                    "resource_import_shape_oversize",
                    "The import exceeds the configured record or per-record evidence budget.",
                    "cortex.resource.import"));
            }

            var result = await _importer.ImportAsync(bundle, context, cancellationToken).ConfigureAwait(false);
            outcome = result.Accepted ? "accepted" : "rejected";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
            if (!leaveOpen)
                await input.DisposeAsync().ConfigureAwait(false);
            RecordOperationMetrics(stopwatch.Elapsed, memoryBefore, outcome);
        }
    }

    public ValueTask DisposeAsync() => _gate.DisposeAsync();

    private bool IsKnownOversize(Stream input, long? declaredLength)
    {
        if (declaredLength > _options.MaxImportBytes)
            return true;
        if (!input.CanSeek)
            return false;

        try
        {
            return input.Length - input.Position > _options.MaxImportBytes;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private void RecordOperationMetrics(TimeSpan elapsed, long memoryBefore, string outcome)
    {
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.OperationLatencyMilliseconds, elapsed.TotalMilliseconds, "milliseconds", "cortex.resource.import", outcome);
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.MemoryBytes, GC.GetTotalMemory(false), "bytes", "cortex.resource.import", outcome);
        ResourceMetricRecorder.Gauge(_metrics, ResourceMetricNames.MemoryBytes, memoryBefore, "bytes", "cortex.resource.import.start", "observed");
    }

    private static KnowledgeBundleImportResult Reject(Pegasus.Shared.Diagnostics.PegasusDiagnostic diagnostic) =>
        new(false, 0, 0, [diagnostic]);
}

internal sealed class ResourceLimitExceededException : IOException;

internal sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, BoundCount(count));
        AddRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..BoundCount(buffer.Length)]);
        AddRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..BoundCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
        AddRead(read);
        return read;
    }

    public override int ReadByte()
    {
        var value = inner.ReadByte();
        if (value >= 0)
        {
            _read++;
            if (_read > maximumBytes)
                throw new ResourceLimitExceededException();
        }
        return value;
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int BoundCount(int requested)
    {
        var remaining = maximumBytes - _read;
        return (int)Math.Min(requested, Math.Max(1, remaining));
    }

    private void AddRead(int read)
    {
        _read += read;
        if (_read > maximumBytes)
            throw new ResourceLimitExceededException();
    }
}
