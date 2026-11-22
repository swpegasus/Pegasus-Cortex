using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Literal one-shot restore command. It is never composed into normal MCP
/// startup and has no environment-selected activation path.
/// </summary>
internal static class CortexLocalRecoveryRestoreCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CortexLocalRecoveryRestoreArguments options;
        try
        {
            options = CortexLocalRecoveryRestoreArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_recovery_restore_arguments_invalid",
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
            var restorer = new CortexLocalRecoveryExportRestorer(
                registry,
                options.ProfileId,
                options.StatePaths.StateRoot,
                options.StatePaths.DatabasePath);
            var result = await restorer.RestoreAsync(
                options.ProjectLinkId,
                options.ExportRoot,
                cancellation.Token).ConfigureAwait(false);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(
                CortexCanonicalJson.Serialize(result),
                cancellation.Token).ConfigureAwait(false);
            await output.WriteAsync(
                "\n"u8.ToArray(),
                cancellation.Token).ConfigureAwait(false);
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return result.Accepted ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_recovery_restore_failed",
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
                $"severity=error operation=recovery.restore code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}
