using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Provisioning;

/// <summary>
/// Literal one-shot command that creates one absent protected profile without
/// inventing project authority. Long-running local hosts never invoke it.
/// </summary>
internal static class CortexLocalProfileProvisionCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CortexLocalProfileProvisionArguments options;
        try
        {
            options = CortexLocalProfileProvisionArguments.Parse(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_profile_provision_arguments_invalid",
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
            var result =
                await CortexLocalProjectRegistryProvisioner.CreateNewPortableAsync(
                    options.ProfileId,
                    options.StatePaths.StateRoot,
                    [],
                    cancellation.Token).ConfigureAwait(false);

            // The protected empty registry is the commit point. A late console
            // cancellation must not remove it or misreport the accepted create.
            var canonical = CortexCanonicalJson.Serialize(result);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(canonical).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await diagnostics.WriteLineAsync(
                    "severity=warning operation=profile.provision code=cortex_local_profile_provision_cancelled message=The create-only profile provisioner was cancelled before committing a protected registry.")
                .ConfigureAwait(false);
            await diagnostics.FlushAsync().ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_profile_provision_failed",
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
                $"severity=error operation=profile.provision code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}
