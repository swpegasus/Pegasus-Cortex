using System.Text;
using Pegasus.Cortex.Service.Lifecycle;

namespace Pegasus.Cortex.Service.Tests;

internal static class CortexServiceLifecycleTests
{
    internal static async Task GracefulShutdownAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var output = new TrackingStringWriter();
        using var diagnostics = new StringWriter();

        var run = PegasusCortexServiceProgram.RunAsync(
            [PegasusCortexServiceProgram.LifecycleRehearsalArgument],
            output,
            diagnostics,
            shutdown.Token);

        TestAssert.False(run.IsCompleted, "Lifecycle rehearsal stopped before a shutdown request.");
        TestAssert.Equal(
            CortexServiceLifecycleRehearsal.ReadyMarker + Environment.NewLine,
            output.ToString(),
            "Lifecycle readiness marker was not deterministic or flushed before waiting.");
        TestAssert.Equal(1, output.FlushCount, "Lifecycle readiness was not flushed exactly once.");

        shutdown.Cancel();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        TestAssert.Equal(0, exitCode);
        TestAssert.Equal(
            CortexServiceLifecycleRehearsal.ReadyMarker + Environment.NewLine +
            CortexServiceLifecycleRehearsal.StoppedMarker + Environment.NewLine,
            output.ToString(),
            "Lifecycle shutdown marker was not deterministic.");
        TestAssert.Equal(2, output.FlushCount, "Lifecycle stop was not flushed exactly once.");
        TestAssert.Equal(string.Empty, diagnostics.ToString());
    }

    internal static async Task ShutdownBeforeReadyAsync()
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        using var output = new TrackingStringWriter();
        using var diagnostics = new StringWriter();

        var exitCode = await PegasusCortexServiceProgram.RunAsync(
            [PegasusCortexServiceProgram.LifecycleRehearsalArgument],
            output,
            diagnostics,
            shutdown.Token).ConfigureAwait(false);

        TestAssert.Equal(0, exitCode);
        TestAssert.Equal(
            CortexServiceLifecycleRehearsal.StoppedBeforeReadyMarker + Environment.NewLine,
            output.ToString(),
            "A pre-start shutdown incorrectly advertised readiness.");
        TestAssert.DoesNotContain("state=ready", output.ToString(), "Canceled rehearsal advertised readiness.");
        TestAssert.Equal(1, output.FlushCount, "Pre-start shutdown marker was not flushed.");
        TestAssert.Equal(string.Empty, diagnostics.ToString());
    }

    internal static async Task OutputFailuresAsync()
    {
        await AssertOutputFailureAsync(failOnLine: 1, failOnFlush: null, "emit_ready")
            .ConfigureAwait(false);
        await AssertOutputFailureAsync(failOnLine: null, failOnFlush: 1, "emit_ready")
            .ConfigureAwait(false);
        await AssertOutputFailureAsync(failOnLine: 2, failOnFlush: null, "emit_stopped")
            .ConfigureAwait(false);
    }

    private static async Task AssertOutputFailureAsync(
        int? failOnLine,
        int? failOnFlush,
        string expectedOperation)
    {
        using var shutdown = new CancellationTokenSource();
        using var output = new FailingTextWriter(failOnLine, failOnFlush);
        using var diagnostics = new StringWriter();

        var run = PegasusCortexServiceProgram.RunAsync(
            [PegasusCortexServiceProgram.LifecycleRehearsalArgument],
            output,
            diagnostics,
            shutdown.Token);
        if (failOnLine is > 1)
            shutdown.Cancel();

        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        TestAssert.Equal(3, exitCode);
        TestAssert.Contains(
            "cortex_service_lifecycle_output_failed",
            diagnostics.ToString(),
            "Lifecycle output failure did not retain a stable diagnostic code.");
        TestAssert.Contains(
            $"operation={expectedOperation} cause=IOException",
            diagnostics.ToString(),
            "Lifecycle output failure omitted its operation or cause type.");
        TestAssert.DoesNotContain(
            FailingTextWriter.SensitiveFailureMessage,
            diagnostics.ToString(),
            "Lifecycle diagnostic disclosed the writer's environment-specific message.");
    }

    private sealed class TrackingStringWriter : StringWriter
    {
        internal int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }

    private sealed class FailingTextWriter(int? failOnLine, int? failOnFlush) : TextWriter
    {
        internal const string SensitiveFailureMessage = "sensitive-host-pipe-path";
        private int _flushCount;
        private int _lineCount;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _lineCount) == failOnLine)
                throw new IOException(SensitiveFailureMessage);
        }

        public override void Flush()
        {
            if (Interlocked.Increment(ref _flushCount) == failOnFlush)
                throw new IOException(SensitiveFailureMessage);
        }
    }
}
