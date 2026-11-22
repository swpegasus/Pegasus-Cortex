using System.Text;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;

// Test executable only: preserves retained-content, restart and EOF coverage
// after the public per-chat STDIO entry has been retired. It cannot select the
// installed Host, current-user profile, native client pipe or execution route.
internal static class OwnedProfileProtocolChild
{
    internal static async Task<int> RunAsync(string profile, string stateRoot)
    {
        await using var diagnostics = new StreamWriter(Console.OpenStandardError(),
            new UTF8Encoding(false), bufferSize: 1024, leaveOpen: false)
        { AutoFlush = true, NewLine = "\n" };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            // PortableStatePaths retains the real profile/path/no-reparse
            // admission. The extra fixture boundary excludes all non-temp data.
            var paths = CortexLocalProjectRegistryProvisioner.PortableStatePaths(profile, stateRoot);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var relative = Path.GetRelativePath(tempRoot, paths.StateRoot);
            if (relative is "." or ".." || Path.IsPathRooted(relative) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("The protocol fixture requires an explicit owned temporary state root.");

            using var input = McpStrictUtf8.CreateReader(Console.OpenStandardInput(), leaveOpen: false);
            await using var output = new StreamWriter(Console.OpenStandardOutput(),
                new UTF8Encoding(false), bufferSize: 4096, leaveOpen: false)
            { AutoFlush = false, NewLine = "\n" };
            await using var composition = await CortexMcpLocalComposition.CreateAsync(
                profile, paths.DatabasePath, paths.RegistryPath, "stdio", TimeProvider.System,
                cancellation.Token).ConfigureAwait(false);
            await using var execution = new RefusedExecution();
            var application = new CortexUnifiedMcpApplication(composition.Application, execution);
            var server = new McpStdioServer(application, input, output, diagnostics);
            // RunAsync joins actual scheduler callbacks before SQLite disposal.
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            // A fixed diagnostic and bounded type preserve startup-failure
            // assertions without exposing retained text or selected paths.
            await diagnostics.WriteLineAsync(
                "severity=error operation=fixture.profile.start code=cortex_mcp_local_start_failed cause=" +
                error.GetType().Name).ConfigureAwait(false);
            return error is ArgumentException ? 2 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private sealed class RefusedExecution : IOperationDispatcher
    {
        public Task<OperationReceipt> DispatchAsync(OperationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("execution_refused_in_owned_profile_fixture");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
