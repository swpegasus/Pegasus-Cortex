using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pegasus.Cortex.Launcher;
using Pegasus.Cortex.Product;

internal static class LauncherConsoleTests
{
    internal static async Task<int> RunAsync(string scratchRoot)
    {
        try
        {
            Policy();
            await RedirectedProcessAsync(scratchRoot);
            Console.WriteLine("METRIC launcher-console policy-fixture=true redirected-process-fixture=true gui-visual=false");
            return 0;
        }
        catch (Exception error)
        {
            var message = error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            Console.Error.WriteLine($"FAIL launcher-console :: {error.GetType().Name}: {message[..Math.Min(message.Length, 300)]}");
            return 1;
        }
    }

    private static void Policy()
    {
        LocalAssert.True(CortexLauncherConsole.IsGuiRoute([]) && CortexLauncherConsole.IsGuiRoute(["--dashboard"]),
            "GUI route classification changed.");
        foreach (var arguments in new string[][] { ["stdio"], ["stdio", "--client", "codex"], ["version"], ["--dashboard", "extra"] })
            LocalAssert.False(CortexLauncherConsole.IsGuiRoute(arguments), "An explicit CLI route became GUI startup.");
        CortexLauncherConsole.StandardHandle[] pipes =
            [new(-10, new IntPtr(101), true, true), new(-11, new IntPtr(102), true, true), new(-12, new IntPtr(103), true, true)];
        CortexLauncherConsole.StandardHandle[] terminal =
            [new(-10, new IntPtr(201), true, false), new(-11, new IntPtr(202), true, false), new(-12, new IntPtr(203), true, false)];
        CortexLauncherConsole.StandardHandle[] mixed = [terminal[0], pipes[1], pipes[2]];
        var attachCalls = 0;
        var restored = new List<(int, IntPtr)>();
        int Attach() { attachCalls++; return 0; }
        int Restore(int kind, IntPtr value) { restored.Add((kind, value)); return 0; }
        CortexLauncherConsole.PrepareCore(true, false, terminal, Attach, Restore);
        CortexLauncherConsole.PrepareCore(false, false, pipes, Attach, Restore);
        CortexLauncherConsole.PrepareCore(false, true, mixed, Attach, Restore);
        LocalAssert.True(attachCalls == 0 && restored.Count == 0, "GUI, redirected or attached startup changed console ownership.");
        CortexLauncherConsole.PrepareCore(false, false, mixed, Attach, Restore);
        LocalAssert.True(attachCalls == 1 && restored.SequenceEqual(mixed.Where(value => value.Redirected)
            .Select(value => (value.Kind, value.Value))), "Partial redirection was not preserved around parent attachment.");
        foreach (var error in new[] { 5, 6, 87 })
        {
            restored.Clear();
            CortexLauncherConsole.PrepareCore(false, false, mixed, () => error, Restore);
            LocalAssert.True(restored.Count == 2, "Benign parent-console failure discarded redirected handles.");
        }
        restored.Clear();
        var failed = false;
        try { CortexLauncherConsole.PrepareCore(false, false, mixed, () => 123, Restore); }
        catch (Win32Exception error) when (error.NativeErrorCode == 123) { failed = true; }
        LocalAssert.True(failed && restored.Count == 2, "Unexpected attachment failure was hidden or lost redirected handles.");
        restored.Clear();
        failed = false;
        try
        {
            CortexLauncherConsole.PrepareCore(false, false, mixed, Attach, (kind, value) =>
            { restored.Add((kind, value)); return kind == -11 ? 5 : 0; });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 5) { failed = true; }
        LocalAssert.True(failed && restored.Count == 2, "Handle restoration stopped early or hid its failure.");
        LocalAssert.True(CortexLauncherConsole.ShouldCreateNoWindow(true, true, terminal) &&
            CortexLauncherConsole.ShouldCreateNoWindow(false, false, terminal) &&
            CortexLauncherConsole.ShouldCreateNoWindow(false, true, pipes) &&
            !CortexLauncherConsole.ShouldCreateNoWindow(false, true, mixed), "Child console inheritance policy changed CLI or headless I/O.");
        var stderr = new List<string>();
        var gui = new List<string>();
        CortexLauncherConsole.ReportFailure(true, new string('x', 800) + "\n", stderr.Add, gui.Add);
        LocalAssert.True(stderr.Count == 0 && gui.Count == 1 && gui[0].Length <= 600, "GUI failure was silent or unbounded.");
        CortexLauncherConsole.ReportFailure(false, "bad\ncatalog", stderr.Add, gui.Add);
        LocalAssert.True(gui.Count == 1 && stderr.SequenceEqual(["Pegasus Cortex launcher failed: bad catalog"]),
            "CLI failure used a GUI dialog or polluted the bounded stderr grammar.");
        (string Argument, string Encoded)[] quoted =
        [
            ("", "\"\""), ("plain", "\"plain\""), ("two words", "\"two words\""),
            ("say\"yes", "\"say\\\"yes\""), (@"C:\folder\", "\"C:\\folder\\\\\""),
            ("a\\\"b", "\"a" + new string('\\', 3) + "\"b\"")
        ];
        foreach (var (argument, encoded) in quoted)
            LocalAssert.Equal("\"tool.exe\" " + encoded, CortexStableLauncher.BuildCommandLine("tool.exe", [argument]),
                "Native child argument quoting changed the exact argument.");
        LocalAssert.Equal(32766, CortexStableLauncher.BuildCommandLine("x", [new string('a', 32760)]).Length,
            "The maximum Windows command line was refused.");
        foreach (var argument in new[] { "bad\0argument", new string('a', 32761) })
        {
            var refused = false;
            try { CortexStableLauncher.BuildCommandLine("x", [argument]); }
            catch (ArgumentException) { refused = true; }
            LocalAssert.True(refused, "An invalid native child command line was accepted.");
        }
    }

    internal static async Task<int> RunEchoChildAsync()
    {
        CortexLauncherConsole.Prepare(["stdio"]);
        var window = GetConsoleWindow();
        var visible = window != IntPtr.Zero && IsWindowVisible(window);
        // Console association/code page does not imply a displayed console.
        // Keep this observation in the exact owned adapter fixture, off STDIO.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "console-window-state.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                consoleAssociated = CortexLauncherConsole.HasConsole(), windowPresent = window != IntPtr.Zero,
                classicConsoleWindowVisible = visible
            }));
        if (visible) return 19;
        using var input = Console.OpenStandardInput();
        using var bytes = new MemoryStream();
        var buffer = new byte[512];
        int count;
        while ((count = await input.ReadAsync(buffer)) != 0)
        {
            if (bytes.Length + count > 4096) return 20;
            bytes.Write(buffer, 0, count);
        }
        using var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes.ToArray());
        using var error = Console.OpenStandardError();
        await error.WriteAsync(Encoding.UTF8.GetBytes("fixture stderr Ω\n"));
        return 17;
    }

    private static async Task RedirectedProcessAsync(string scratchRoot)
    {
        var root = Path.GetFullPath(scratchRoot);
        if (!Path.IsPathFullyQualified(scratchRoot) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            root.Length > 110 || File.Exists(root) || Directory.Exists(root))
            throw new InvalidOperationException("A new short local launcher-console fixture root is required.");
        CortexProductLayout.RejectExistingReparseComponents(root);
        var program = Path.Combine(root, "program");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(program);
        var assembly = typeof(LauncherConsoleTests).Assembly.Location;
        var apphost = Path.ChangeExtension(assembly, ".exe");
        if (!File.Exists(apphost)) throw new InvalidOperationException("The test apphost is required.");
        LifecycleProcessTests.CopyImageClosure(Path.GetDirectoryName(assembly)!, program);
        var launcher = Path.Combine(program, "PegasusCortex.exe");
        File.Copy(apphost, launcher, overwrite: true);
        var semantics = Path.Combine(program, "bin", "semantics");
        Directory.CreateDirectory(semantics);
        LifecycleProcessTests.CopyImageClosure(Path.GetDirectoryName(assembly)!, semantics);
        var adapter = Path.Combine(semantics, "PegasusCortex.exe");
        File.Copy(apphost, adapter, overwrite: true);
        LifecycleProcessTests.WriteFixtureCatalog(program, data);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = program,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true)
            }
        };
        process.StartInfo.ArgumentList.Add("fixture-launcher-child");
        process.StartInfo.ArgumentList.Add(data);
        process.StartInfo.ArgumentList.Add("launcher-stdio-echo-child");
        if (!process.Start()) throw new InvalidOperationException("The source-linked fixture loader did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var sample = """{"jsonrpc":"2.0","id":"stdio-Ω","method":"fixture","params":{"text":"日本語 😀"}}""" + "\n";
            await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(sample), deadline.Token);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout.WaitAsync(deadline.Token);
            var error = await stderr.WaitAsync(deadline.Token);
            File.WriteAllText(Path.Combine(root, "stdio-result.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                childExit = process.ExitCode, stdoutCharacters = output.Length, stderrCharacters = error.Length,
                stdoutMatches = output == sample, stderrMatches = error == "fixture stderr Ω\n",
                stdout = output[..Math.Min(output.Length, 4096)], stderr = error[..Math.Min(error.Length, 1024)],
                childStart = "explicit-native"
            }));
            LocalAssert.True(process.ExitCode == 17 && output == sample && error == "fixture stderr Ω\n",
                $"Redirected stdio changed: exit={process.ExitCode}; stdoutChars={output.Length}; stderrChars={error.Length}; " +
                $"stdoutMatches={output == sample}; stderrMatches={error == "fixture stderr Ω\n"}.");
            foreach (var image in new[] { launcher, adapter, Path.Combine(program, Path.GetFileName(assembly)),
                Path.Combine(semantics, Path.GetFileName(assembly)) }) LifecycleProcessTests.AssertReplaceable(image);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
