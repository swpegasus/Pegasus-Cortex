using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;
using LauncherCore = Pegasus.Cortex.Launcher.CortexStableLauncher;

/// <summary>Source-linked launcher core fixtures and a separate shipping-image refusal check.</summary>
internal static class LauncherSelectorFixtureTests
{
    internal static async Task<int> RunLauncherChildAsync(string dataRoot, string[] arguments)
    {
        var image = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture loader image unavailable.");
        var programRoot = Path.GetDirectoryName(image)!;
        var layout = CortexProductLayout.ForIsolatedRoots(programRoot, dataRoot, LocalIdentity.Current().Sid);
        Pegasus.Cortex.Launcher.CortexLauncherConsole.Prepare(arguments);
        return await LauncherCore.RunSelectedVersionAsync(image, requestedRoot =>
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(requestedRoot, layout.InstallRoot))
                throw new InvalidDataException("Fixture loader program root mismatch.");
            return layout.DataRoot;
        }, arguments, CancellationToken.None);
    }

    internal static async Task<int> RunAsync(string builtLauncher, string scratchRoot)
    {
        try
        {
            var root = Path.GetFullPath(scratchRoot);
            if (!Path.IsPathFullyQualified(scratchRoot) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
                root.Length > 110 || Directory.Exists(root) || File.Exists(root))
                throw new InvalidOperationException("A new short local selector fixture root is required.");
            CortexProductLayout.RejectExistingReparseComponents(root);
            Directory.CreateDirectory(root);
            var cases = SelectorCases(root);
            await ShippingMismatchAsync(builtLauncher, Path.Combine(root, "shipping"));
            Console.WriteLine($"METRIC launcher-selection pure-fixture-cases={cases} shipping-mismatch-refused=true live-startup=false");
            return 0;
        }
        catch (Exception error)
        {
            var message = error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            Console.Error.WriteLine($"FAIL launcher-selection :: {error.GetType().Name}: {message[..Math.Min(message.Length, 300)]}");
            return 1;
        }
    }

    private static int SelectorCases(string root)
    {
        var program = Path.Combine(root, "program");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(data);
        LifecycleProcessTests.WriteFixtureCatalog(program);
        LifecycleProcessTests.WriteFixtureCatalog(program, data);
        var owner = LocalIdentity.Current().Sid;
        var layout = CortexProductLayout.ForIsolatedRoots(program, data, owner);
        var external = Path.Combine(data, "install", "active.v1.json");
        var legacy = Path.Combine(program, "state", "install", "active.v1.json");
        var cases = 0;

        Select(program, data, external);
        Select(null, null, legacy);
        Select(program, null, legacy);
        Refuse(() => Select(Path.Combine(root, "other-program"), data, external));
        Refuse(() => Select(null, data, external));
        Refuse(() => Select(program, "", external));
        var missing = Path.Combine(root, "missing-data");
        var missingLayout = CortexProductLayout.ForIsolatedRoots(program, missing, owner);
        Refuse(() => LauncherCore.ResolveSelectorPath(program,
            _ => CortexDataLocation.ApplyRegistration(missingLayout, program, missing).DataRoot));
        var noCatalog = Path.Combine(root, "no-catalog");
        Directory.CreateDirectory(noCatalog);
        Refuse(() => LauncherCore.ResolveSelectorPath(program, _ => noCatalog));
        var directoryCatalog = Path.Combine(root, "directory-catalog");
        Directory.CreateDirectory(Path.Combine(directoryCatalog, "install", "active.v1.json"));
        Refuse(() => LauncherCore.ResolveSelectorPath(program, _ => directoryCatalog));
        Refuse(() => Select(program, Path.Combine(data, "..", "data"), external));
        var invalidCatalog = Path.Combine(root, "invalid-catalog");
        Directory.CreateDirectory(Path.Combine(invalidCatalog, "install"));
        File.WriteAllText(Path.Combine(invalidCatalog, "install", "active.v1.json"), "{}");
        Refuse(() => CortexProductCatalogV1.ReadExact(LauncherCore.ResolveSelectorPath(program, _ => invalidCatalog)));
        foreach (var (arguments, expectedRole) in new (string[], string)[]
        {
            (["--exit"], "host"), (["--exit", "extra"], "mcp"),
            (["--Exit"], "mcp"), (["--dashboard"], "host"), (["stdio"], "mcp")
        })
        {
            LocalAssert.Equal(expectedRole, LauncherCore.SelectRole(arguments), "Launcher selected the wrong control role.");
            cases++;
        }
        return cases;

        void Select(string? registeredProgram, string? registeredData, string expected)
        {
            var selected = LauncherCore.ResolveSelectorPath(program,
                _ => CortexDataLocation.ApplyRegistration(layout, registeredProgram, registeredData).DataRoot);
            LocalAssert.Equal(expected, selected, "Selector did not follow its exact admitted data root.");
            cases++;
        }

        void Refuse(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException or System.Text.Json.JsonException)
            { cases++; return; }
            throw new InvalidOperationException("An invalid selector or registration fell back to the legacy catalog.");
        }
    }

    private static async Task ShippingMismatchAsync(string builtLauncher, string root)
    {
        var source = Path.GetFullPath(builtLauncher);
        CortexProductLayout.RejectExistingReparseComponents(source);
        if (!Path.IsPathFullyQualified(builtLauncher) || !File.Exists(source) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(source), "PegasusCortex.exe"))
            throw new InvalidOperationException("Supply the integrated build's shipping stable launcher.");
        using (var imageStream = File.OpenRead(source))
        using (var imageHeaders = new PEReader(imageStream))
            LocalAssert.True(imageHeaders.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui,
                "The shipping stable launcher is not a GUI-subsystem image.");
        Directory.CreateDirectory(root);
        LifecycleProcessTests.CopyImageClosure(Path.GetDirectoryName(source)!, root);
        // A valid local legacy catalog must not rescue a registered-root mismatch.
        LifecycleProcessTests.WriteFixtureCatalog(root);
        var semantic = Path.Combine(root, "bin", "semantics");
        Directory.CreateDirectory(semantic);
        var assembly = typeof(LauncherSelectorFixtureTests).Assembly.Location;
        LifecycleProcessTests.CopyImageClosure(Path.GetDirectoryName(assembly)!, semantic);
        File.Copy(Path.ChangeExtension(assembly, ".exe"), Path.Combine(semantic, "PegasusCortex.exe"), overwrite: true);
        var image = Path.Combine(root, "PegasusCortex.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(image)
            {
                UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("lifecycle-relay-child");
        process.StartInfo.ArgumentList.Add("absent-selector-fixture-" + Guid.NewGuid().ToString("N"));
        process.StartInfo.ArgumentList.Add(Environment.ProcessPath!);
        if (!process.Start()) throw new InvalidOperationException("Shipping launcher fixture did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            LocalAssert.True(process.ExitCode == 1 && output.Length == 0 && error.Contains(
                "The registered Cortex installation does not match the executing product root.", StringComparison.Ordinal),
                "Shipping mismatch refusal was not proven; this check requires machine registration to another install root.");
            LifecycleProcessTests.AssertReplaceable(image);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
