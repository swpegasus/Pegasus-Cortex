using System.Text;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.Mcp.Integrated.Recovery;
using Pegasus.Cortex.Mcp.Integrated.Runtime;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Protocol;

return await PegasusCortexMcpIntegratedProgram.RunAsync(args).ConfigureAwait(false);

internal static class PegasusCortexMcpIntegratedProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var diagnostics = new StreamWriter(
            Console.OpenStandardError(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: false)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // Transfer import is a literal one-shot operator command. It is
        // intentionally selected before stdio MCP construction and is never
        // represented in tools/list.
        if (args.Length > 0 &&
            string.Equals(
                args[0],
                CortexIntegratedProjectTransferImportArguments.Command,
                StringComparison.Ordinal))
        {
            return await CortexIntegratedProjectTransferImportCommand.RunAsync(
                    args,
                    diagnostics)
                .ConfigureAwait(false);
        }

        if (args.Length != 0)
        {
            await DiagnosticWriter.WriteAsync(
                diagnostics,
                "error",
                "mcp.start",
                "cortex_arguments_unsupported",
                "This stdio server does not accept command-line arguments.").ConfigureAwait(false);
            return 2;
        }

        using var input = McpUtf8Input.Create(
            Console.OpenStandardInput(),
            leaveOpen: false);
        await using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false)
        {
            AutoFlush = false,
            NewLine = "\n"
        };

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var integratedHost = await CortexMcpIntegratedHost.TryCreateAsync(
                    cancellation.Token)
                .ConfigureAwait(false);
            if (!integratedHost.Available)
            {
                await DiagnosticWriter.WriteAsync(
                    diagnostics,
                    "error",
                    "mcp.integrated.start",
                    integratedHost.IssueCode ?? "cortex_hybrid_not_ready",
                    "The explicit integrated MCP profile is not ready; no local fallback was started.",
                    cancellation.Token).ConfigureAwait(false);
                return 1;
            }

            await using var integratedRuntime = integratedHost.Runtime!;
            var source = DefaultLibraryQuerySource.CreateVerified();
            var queryService = new CortexQueryService(
                source,
                new CortexQueryServiceOptions(
                    DefaultKnowledgeLibrary.DefaultNamespace,
                    "isolated-read-only",
                    "verified-at-startup"));
            var authority = new CortexQueryAuthority(
                "local",
                "pegasus-cortex-mcp",
                "local-stdio",
                "pegasus-cortex-mcp",
                policyRevision: 1,
                [new CortexQueryNamespaceGrant(
                    DefaultKnowledgeLibrary.DefaultNamespace,
                    "internal",
                    [
                        CortexQueryCapability.Discover,
                        CortexQueryCapability.Status,
                        CortexQueryCapability.Catalog,
                        CortexQueryCapability.Search,
                        CortexQueryCapability.Read
                    ],
                    allowStale: true,
                    allowBlockedOrUnverified: true)]);
            var application = await CortexMcpApplication.CreateAsync(
                queryService,
                source,
                authority,
                DefaultKnowledgeLibrary.DefaultNamespace,
                integratedHost.Composition!,
                cancellation.Token).ConfigureAwait(false);
            var server = new McpStdioServer(application, input, output, diagnostics);
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await DiagnosticWriter.WriteExceptionAsync(
                diagnostics,
                "mcp.integrated.start",
                "cortex_mcp_integrated_start_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
