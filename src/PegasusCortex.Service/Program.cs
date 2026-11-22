return await Pegasus.Cortex.Service.PegasusCortexServiceProgram.RunConsoleAsync(
    args,
    Console.Out,
    Console.Error);

namespace Pegasus.Cortex.Service
{
internal static class PegasusCortexServiceProgram
{
    internal const string LifecycleRehearsalArgument = "--service-lifecycle-rehearsal";

    internal static async Task<int> RunConsoleAsync(
        string[] args,
        TextWriter output,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (IsLifecycleRehearsal(args))
        {
            using var shutdown = Lifecycle.CortexServiceShutdownSource.Create();
            return await RunAsync(args, output, diagnostics, shutdown.Token).ConfigureAwait(false);
        }

        return await RunAsync(args, output, diagnostics, CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter diagnostics,
        CancellationToken shutdownToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (args.Length == 1 && string.Equals(args[0], "--composition-status", StringComparison.Ordinal))
        {
            output.WriteLine(
                "pegasus-cortex-service composition-contract-available runtime-not-configured");
            output.Flush();
            return 0;
        }

        if (args.Length == 1 &&
            string.Equals(
                args[0],
                Persistence.CortexPostgreSqlMigrationCommand.Argument,
                StringComparison.Ordinal))
        {
            return await Persistence.CortexPostgreSqlMigrationCommand.RunAsync(
                output,
                diagnostics,
                Environment.GetEnvironmentVariable,
                shutdownToken).ConfigureAwait(false);
        }

        if (IsLifecycleRehearsal(args))
        {
            try
            {
                return await Lifecycle.CortexServiceLifecycleRehearsal.RunAsync(
                    output,
                    shutdownToken).ConfigureAwait(false);
            }
            catch (Lifecycle.CortexServiceLifecycleOutputException exception)
            {
                // Preserve the failed operation and cause type without rendering writer messages,
                // which may include pipe, terminal, or filesystem details owned by the host.
                diagnostics.WriteLine(
                    "ERROR cortex.service.lifecycle cortex_service_lifecycle_output_failed " +
                    $"operation={exception.Operation} cause={exception.InnerException?.GetType().Name ?? "unknown"}");
                diagnostics.Flush();
                return 3;
            }
        }

        // The executable cannot safely invent a row-authentication key source. A hosting
        // product must compose the service with its protected ICortexRowAuthenticator.
        diagnostics.WriteLine(
            "ERROR cortex.service.start cortex_service_host_composition_required " +
            "A protected host row authenticator and explicit PostgreSQL configuration are required.");
        diagnostics.Flush();
        return 2;
    }

    private static bool IsLifecycleRehearsal(string[] args) =>
        args.Length == 1 &&
        string.Equals(args[0], LifecycleRehearsalArgument, StringComparison.Ordinal);
}
}
