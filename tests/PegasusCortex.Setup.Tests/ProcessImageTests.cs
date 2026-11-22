using System.Diagnostics;
using Pegasus.Cortex.Setup;

internal static class ProcessImageTests
{
    internal static void Run(string root)
    {
        foreach (var name in new[] { "Secure System", "Registry", "Memory Compression" })
        {
            Check(CortexProductInstallLifecycle.IsKernelImageQueryFailure(31, name), "Observed kernel query failure was not classified");
            foreach (var error in new[] { 0, 5, 87, 299, 998 })
                Check(!CortexProductInstallLifecycle.IsKernelImageQueryFailure(error, name), "Unrelated error was treated as a kernel image failure");
        }
        foreach (var name in new[] { "PegasusCortex.Host", "PegasusCortex", "random", "Registry.exe", "registry", "Secure System " })
            Check(!CortexProductInstallLifecycle.IsKernelImageQueryFailure(31, name), "Ordinary or inexact process name bypassed image checks");
        using var current = Process.GetCurrentProcess();
        var expected = Path.GetFullPath(Environment.ProcessPath ?? throw new Exception("Test image unavailable"));
        var actual = CortexUserSessionStartup.NativeStartupPlatform.ReadProcessImage(checked((uint)current.Id));
        Check(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), "Limited native query returned a different test image");
        // This is actual OS-image enumeration, not a simulated process list.
        // The root cannot contain any installed executable and no process is stopped.
        CortexProductInstallLifecycle.RequireNoProductProcesses(Path.Combine(root, "unoccupied-product"), current.Id);
        var ownImageRoot = Path.GetDirectoryName(expected)!;
        var refused = false;
        try { CortexProductInstallLifecycle.RequireNoProductProcesses(ownImageRoot, -1); }
        catch (IOException failure) when (failure.Message.StartsWith("A Cortex program image is still loaded.", StringComparison.Ordinal))
        { refused = true; }
        Check(refused && !current.HasExited, "The loaded test image was not refused without stopping it");
        var fixture = Path.Combine(root, "Cortex");
        Check(CortexProductInstallLifecycle.IsContained(fixture, Path.Combine(fixture, "bin", "host.exe")) &&
            !CortexProductInstallLifecycle.IsContained(fixture, Path.Combine(root, "Cortex-other", "host.exe")) &&
            !CortexProductInstallLifecycle.IsContained(fixture, fixture), "Installed-root matching accepted a sibling or root itself");
        Console.WriteLine("PASS native limited-rights current-image query, read-only OS process enumeration, loaded test-image refusal and exact-root matching; no process/service/product mutation");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
