using Pegasus.Cortex.Setup;

internal static class InstallerAncestorTests
{
    public static void RunPolicy()
    {
        CortexProductInstallLifecycle.RequireCodexClosed(["explorer", "PegasusCortex"]);
        foreach (var codexName in new[] { "codex", "Codex", "ChatGPT", "chatgpt" })
        {
            var codexRejected = false;
            try { CortexProductInstallLifecycle.RequireCodexClosed(["explorer", codexName]); }
            catch (InvalidOperationException exception)
            {
                codexRejected = exception.Message.Contains("Close every Codex window", StringComparison.Ordinal);
            }
            Require(codexRejected, $"Running {codexName} did not block a Codex feature change.");
        }

        var events = new List<string>();
        var result = CortexProductInstallLifecycle.PrepareCore(
            () => { events.Add("service"); return true; },
            () => { events.Add("processes"); return 2; });
        Require(events.SequenceEqual(["service", "processes"]) &&
                result.ProductProcessesStopped == 2,
            "Successful lifecycle preparation order differs.");
        Console.WriteLine("PASS install_lifecycle_policy (feature-only Codex prerequisite and owned-component drain order)");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
