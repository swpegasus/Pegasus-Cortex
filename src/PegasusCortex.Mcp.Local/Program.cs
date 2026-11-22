using System.Text;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Pipes;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Provisioning;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Persistence.File.Assets;

return await PegasusCortexMcpLocalProgram.RunAsync(args).ConfigureAwait(false);

public static class PegasusCortexMcpLocalProgram
{
    public const string StdioRetirementMessage = "Direct Cortex STDIO connections are retired. Connect your MCP client to the configured shared Cortex HTTPS endpoint instead; start Cortex explicitly if that endpoint is unavailable.";

    public static bool IsRetiredStdioInvocation(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length == 0 || string.Equals(args[0], "stdio", StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (IsRetiredStdioInvocation(args))
        {
            await Console.Error.WriteLineAsync(StdioRetirementMessage).ConfigureAwait(false);
            return 2;
        }
        await using var diagnostics = new StreamWriter(
            Console.OpenStandardError(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: false)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        if (args.Length > 0 && args[0] == "geometry-shadow-status")
        {
            if (args.Length != 1) return 2;
            try
            {
                var result = CortexShadowResearchReadout.ReadCurrent();
                await Console.Out.WriteLineAsync(result.Json);
                return result.Exit;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                       ArgumentException or InvalidOperationException)
            {
                await Console.Out.WriteLineAsync("""{"schema":"cortex.shadow.host-status/1","state":"unavailable","hostLivenessVerified":false}""");
                return 2;
            }
        }

        // Empty profile provisioning is a literal create-only operation. It
        // establishes protected local state without inventing project scope.
        if (args.Length > 0 &&
            string.Equals(args[0], "provision-profile", StringComparison.Ordinal))
        {
            return await CortexLocalProfileProvisionCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        // Local profile provisioning is a literal one-shot create operation.
        // Normal service and bridge startup never reach this branch.
        if (args.Length > 0 &&
            string.Equals(args[0], "provision-project", StringComparison.Ordinal))
        {
            return await CortexLocalProjectProvisionCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        // Exact local-store backup and create-only restore are explicit
        // operator commands. Neither can be reached from MCP startup.
        if (args.Length > 0 &&
            string.Equals(args[0], "backup-local", StringComparison.Ordinal))
        {
            return await CortexLocalStoreBackupCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            string.Equals(
                args[0],
                "restore-local-backup",
                StringComparison.Ordinal))
        {
            return await CortexLocalStoreRestoreCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        // The deterministic current-recovery producer is an explicit operator
        // action. It never runs during MCP service or bridge startup.
        if (args.Length > 0 &&
            string.Equals(args[0], "materialize-current", StringComparison.Ordinal))
        {
            return await CortexLocalCurrentSnapshotCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        // Continuity refresh is an explicit foreground local operator action.
        // Both one-shot and watch modes reuse materialize-current; no service,
        // network route, or implicit startup path is enabled.
        if (args.Length > 0 &&
            string.Equals(
                args[0],
                CortexLocalContinuityRefreshArguments.Command,
                StringComparison.Ordinal))
        {
            return await CortexLocalContinuityRefreshCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            string.Equals(args[0], "export-project-transfer", StringComparison.Ordinal))
        {
            return await CortexLocalProjectTransferExportCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        // Tenant rebinding is a separate explicit copy operation. It never
        // changes authoritative retained bytes or the default import scope
        // equality requirement, and normal MCP startup cannot select it.
        if (args.Length > 0 &&
            string.Equals(
                args[0],
                CortexLocalProjectTransferTenantRebindArguments.Command,
                StringComparison.Ordinal))
        {
            return await CortexLocalProjectTransferTenantRebindCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            string.Equals(args[0], "import-project-transfer", StringComparison.Ordinal))
        {
            return await CortexLocalProjectTransferImportCommand.RunAsync(args, diagnostics)
                .ConfigureAwait(false);
        }

        // Recovery import is an explicit one-shot producer. Normal local MCP
        // startup never parses, constructs, or invokes this command.
        if (args.Length > 0 &&
            string.Equals(args[0], "import-recovery", StringComparison.Ordinal))
        {
            return await CortexLocalRecoveryImportCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        // Retained-state export is likewise an explicit one-shot command. It
        // cannot be selected by environment state or normal host startup.
        if (args.Length > 0 &&
            string.Equals(args[0], "export-recovery", StringComparison.Ordinal))
        {
            return await CortexLocalRecoveryExportCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        // Recovery-export restore is a literal one-shot current-state rebuild.
        // Environment state and all normal service modes cannot activate it.
        if (args.Length > 0 &&
            string.Equals(
                args[0],
                "restore-recovery-export",
                StringComparison.Ordinal))
        {
            return await CortexLocalRecoveryRestoreCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        CortexMcpLocalArguments options;
        try
        {
            options = CortexMcpLocalArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "mcp.local.arguments",
                "cortex_local_arguments_invalid",
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
            return options.Mode switch
            {
                CortexMcpLocalMode.Service =>
                    await RunServiceAsync(options, diagnostics, cancellation.Token)
                        .ConfigureAwait(false),
                CortexMcpLocalMode.Bridge =>
                    await RunBridgeAsync(options, cancellation.Token)
                        .ConfigureAwait(false),
                _ => throw new InvalidOperationException("The explicit local mode is unsupported.")
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "mcp.local.start",
                "cortex_mcp_local_start_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunServiceAsync(
        CortexMcpLocalArguments options,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            options.ProfileId,
            options.StatePaths!.DatabasePath,
            options.StatePaths.RegistryPath,
            "service",
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        var endpoint = CortexLocalEndpoint.Create(options.ProfileId);
        var server = new CortexLocalPipeServer(
            endpoint,
            () => new CortexMcpLocalPipeHandler(
                composition.Application,
                diagnostics),
            sessionFault: exception =>
            {
                var message = Sanitize(exception.Message);
                diagnostics.WriteLine(
                    $"severity=warning operation=mcp.local.session code=cortex_local_session_failed message=cause={exception.GetType().Name}: {message}");
                diagnostics.Flush();
            });
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunBridgeAsync(
        CortexMcpLocalArguments options,
        CancellationToken cancellationToken)
    {
        using var input = McpStrictUtf8.CreateReader(
            Console.OpenStandardInput(),
            leaveOpen: false);
        await using var output = CreateOutputWriter();
        await CortexMcpLocalBridge.RunAsync(
            options.ProfileId,
            input,
            output,
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static StreamWriter CreateOutputWriter() => new(
        Console.OpenStandardOutput(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 4096,
        leaveOpen: false)
    {
        AutoFlush = false,
        NewLine = "\n"
    };

    private static async ValueTask WriteDiagnosticAsync(
        TextWriter diagnostics,
        string operation,
        string code,
        Exception exception)
    {
        var message = Sanitize(exception.Message);
        await diagnostics.WriteLineAsync(
            $"severity=error operation={operation} code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }

    private static string Sanitize(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 512
            ? singleLine
            : singleLine[..512];
    }
}
