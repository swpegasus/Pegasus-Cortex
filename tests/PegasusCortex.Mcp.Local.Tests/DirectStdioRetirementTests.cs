using Pegasus.Cortex.Launcher;
using Pegasus.Cortex.Mcp.Local.Configuration;

// Pure parsing plus isolated current-build child refusal. No installed connector,
// Host, client key, native semantic pipe or production database is opened here.
internal static class DirectStdioRetirementTests
{
    internal static async Task RunAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cortex-stdio-retired-" + Guid.NewGuid().ToString("N"));
        var profile = "retired-" + Guid.NewGuid().ToString("N")[..12];
        LocalAssert.True(!Directory.Exists(scratch) && !File.Exists(scratch), "Retirement fixture unexpectedly exists.");
        var retired = new string[][]
        {
            [], ["stdio"], ["stdio", "--client", "codex"], ["stdio", "--client", "external-client"],
            ["stdio", "--client", "../invalid"], ["stdio", "--profile", profile],
            ["stdio", "--profile", profile, "--state-root", scratch],
            ["stdio", "--profile", profile, "--state-root", scratch, "--ignored", "value"],
            ["stdio", "--client", "codex", "--state-root", scratch]
        };
        foreach (var arguments in retired)
        {
            LocalAssert.True(PegasusCortexMcpLocalProgram.IsRetiredStdioInvocation(arguments), "A direct STDIO variant bypassed the pure refusal gate.");
            try
            {
                _ = CortexMcpLocalArguments.Parse(arguments);
                throw new InvalidOperationException("A retired public startup selected a local composition.");
            }
            catch (ArgumentException refusal)
            {
                LocalAssert.Equal(PegasusCortexMcpLocalProgram.StdioRetirementMessage, refusal.Message,
                    "Retired argument parsing performed client/profile validation before the shared-endpoint refusal.");
            }
            var child = await LocalProcessHarness.RunPublicEntryAsync(arguments, [], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            LocalAssert.Equal(2, child.ExitCode, "A retired startup returned success or attempted a live connection.");
            LocalAssert.True(child.StandardOutput.Length == 0 && child.StandardError.Contains(
                PegasusCortexMcpLocalProgram.StdioRetirementMessage, StringComparison.Ordinal),
                "Retired startup emitted protocol responses or omitted the shared HTTPS remedy.");
            LocalAssert.True(!Directory.Exists(scratch) && !File.Exists(scratch), "Retired startup initialized the supplied state path.");
        }
        LocalAssert.True(Enum.GetNames<CortexMcpLocalMode>().SequenceEqual(new[] { "Service", "Bridge" }),
            "The retired local STDIO mode remains selectable.");
        var bridge = CortexMcpLocalArguments.Parse(["bridge", "--profile", profile]);
        LocalAssert.True(bridge.Mode == CortexMcpLocalMode.Bridge && bridge.ProfileId == profile && bridge.StatePaths is null,
            "Retirement changed independent explicit profile-bridge selection.");
        foreach (var arguments in new string[][] { ["version"], ["status"], ["geometry-shadow-status"],
            ["service", "--profile", profile], ["bridge", "--profile", profile] })
            LocalAssert.True(!PegasusCortexMcpLocalProgram.IsRetiredStdioInvocation(arguments), "The retirement gate consumed an independent CLI/profile mode.");
        LocalAssert.Equal("host", CortexStableLauncher.SelectRole([]), "Ordinary stable-launcher no-argument GUI selection changed.");
        LocalAssert.Equal("host", CortexStableLauncher.SelectRole(["--payload-storage", "upgrade"]),
            "Fixed payload-storage control must use the protected Host helper, not the retired MCP entry.");
        Console.WriteLine("PASS direct STDIO retirement: pure closed parsing and nine owned child refusals with shared HTTPS guidance/no state initialization; profile bridge and stable GUI selection preserved; no installed connector or native Host acceptance");
    }
}
