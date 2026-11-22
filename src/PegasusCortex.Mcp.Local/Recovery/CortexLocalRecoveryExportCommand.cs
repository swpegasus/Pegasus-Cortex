using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// One-shot command boundary. It is reachable only through the literal
/// export-recovery verb and is never composed into a long-running host.
/// </summary>
internal static class CortexLocalRecoveryExportCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CortexLocalRecoveryExportArguments options;
        try
        {
            options = CortexLocalRecoveryExportArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_recovery_export_arguments_invalid",
                exception).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var registry = await CortexLocalProjectRegistry.LoadAsync(
                options.StatePaths.RegistryPath,
                options.ProfileId,
                cancellation.Token).ConfigureAwait(false);
            var exporter = new CortexLocalRecoveryExporter(
                registry,
                options.ProfileId,
                options.StatePaths.StateRoot,
                options.StatePaths.DatabasePath);
            var result = await exporter.ExportAsync(
                options.ProjectLinkId,
                options.DestinationRoot,
                options.RunId,
                options.CapturedAt,
                cancellation.Token).ConfigureAwait(false);
            var canonical = CortexCanonicalJson.Serialize(result);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(canonical, cancellation.Token)
                .ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellation.Token)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return result.Accepted ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await diagnostics.WriteLineAsync(
                    "severity=warning operation=recovery.export code=cortex_local_recovery_export_cancelled message=The explicit recovery export was cancelled before an accepted receipt was returned.")
                .ConfigureAwait(false);
            await diagnostics.FlushAsync().ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_recovery_export_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async ValueTask WriteDiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var message = exception.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.Length > 512)
            message = message[..512];
        await diagnostics.WriteLineAsync(
                $"severity=error operation=recovery.export code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}
