using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Product;

/// <summary>
/// Actual scratch executable/STDIN lifetime checks. The child uses the production
/// authenticated pipe connector with synthetic pumps, not a retired STDIO adapter.
/// This does not qualify production enrollment, installation or Codex retry behavior.
/// </summary>
internal static class LifecycleProcessTests
{
    internal static async Task<int> RunAdapterChildAsync(string pipeName, string expectedServer)
    {
        if (!pipeName.StartsWith("cortex-lifecycle-", StringComparison.Ordinal) || !Path.IsPathFullyQualified(expectedServer)) return 2;
        try
        {
            await using var pipe = await CortexMcpPipe.ConnectAsync(pipeName, LocalIdentity.Current(), expectedServer, CancellationToken.None);
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096);
            await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false, true), 4096) { NewLine = "\n" };
            using var stop = new CancellationTokenSource();
            var lines = new Pegasus.Cortex.Mcp.Protocol.BoundedJsonLineReader(input, 4096);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            Task send = Task.CompletedTask, receive = Task.CompletedTask;
            try
            {
                send = SendAsync();
                receive = pipe.CopyToAsync(output.BaseStream, stop.Token);
                var first = await Task.WhenAny(send, receive);
                await first;
                throw new IOException("fixture_owner_ended");
            }
            finally
            {
                try { stop.Cancel(); }
                finally
                {
                    try { pipe.Dispose(); }
                    finally
                    {
                        try
                        {
                            // Both logical pumps settle. The existing bounded reader owns
                            // terminal cancellation/late-fault observation for console reads.
                            try { await Task.WhenAll(send, receive); }
                            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                        }
                        finally
                        {
                            // Teardown deliberately closed this pipe to release its pending
                            // I/O. A final writer flush must not replace the primary owner-loss
                            // IOException with an already-closed-pipe cleanup exception.
                            try { await writer.DisposeAsync(); }
                            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                        }
                    }
                }
            }

            async Task SendAsync()
            {
                while (true)
                {
                    var read = await lines.ReadAsync(stop.Token);
                    if (read.EndOfStream) return;
                    if (read.ExceededLimit) throw new IOException("fixture_input_limit");
                    await writer.WriteLineAsync(read.Line.AsMemory(), stop.Token);
                }
            }
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("fixture_adapter_disconnected_outcome_unknown");
            return 3;
        }
    }

    internal static Task RunAsync(string builtLauncher, string scratchRoot) =>
        RunCoreAsync(builtLauncher, scratchRoot, fixtureLoader: false);

    internal static Task RunFixtureAsync(string scratchRoot) =>
        RunCoreAsync(Path.ChangeExtension(typeof(LifecycleProcessTests).Assembly.Location, ".exe"), scratchRoot,
            fixtureLoader: true);

    private static async Task RunCoreAsync(string builtLauncher, string scratchRoot, bool fixtureLoader)
    {
        var root = Path.GetFullPath(scratchRoot);
        if (!Path.IsPathFullyQualified(scratchRoot) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            Directory.Exists(root) || File.Exists(root) || root.Length > 120)
            throw new InvalidOperationException("A new short isolated scratch directory is required.");
        CortexProductLayout.RejectExistingReparseComponents(root);
        var launcherSource = Path.GetFullPath(builtLauncher);
        CortexProductLayout.RejectExistingReparseComponents(launcherSource);
        if (launcherSource.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(launcherSource) ||
            !fixtureLoader && Path.GetFileName(launcherSource) != "PegasusCortex.exe")
            throw new InvalidOperationException("Supply the integrated build's real stable launcher image.");
        var testAssembly = typeof(LifecycleProcessTests).Assembly.Location;
        var testImage = Path.ChangeExtension(testAssembly, ".exe");
        if (!File.Exists(testImage)) throw new InvalidOperationException("The built test apphost is required, not dotnet-only invocation.");

        Directory.CreateDirectory(root);
        var programRoot = fixtureLoader ? Path.Combine(root, "program") : root;
        Directory.CreateDirectory(programRoot);
        CopyImageClosure(Path.GetDirectoryName(launcherSource)!, programRoot);
        var launcher = Path.Combine(programRoot, "PegasusCortex.exe");
        if (fixtureLoader) File.Copy(testImage, launcher, overwrite: true);
        if (!File.Exists(launcher)) throw new InvalidOperationException("Scratch launcher is absent.");
        var semanticDirectory = Path.Combine(programRoot, "bin", "semantics");
        Directory.CreateDirectory(semanticDirectory);
        CopyImageClosure(Path.GetDirectoryName(testAssembly)!, semanticDirectory);
        var adapter = Path.Combine(semanticDirectory, "PegasusCortex.exe");
        File.Copy(testImage, adapter, overwrite: true);
        var dataRoot = fixtureLoader ? Path.Combine(root, "data") : null;
        WriteFixtureCatalog(programRoot, dataRoot);

        foreach (var scenario in new[] { "idle", "partial-input", "outstanding-request" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var owner = LocalIdentity.Current();
            var pipeName = "cortex-lifecycle-" + Guid.NewGuid().ToString("N");
            using var server = LocalPipeFactory.Create(pipeName, owner.Sid);
            var accept = server.WaitForConnectionAsync(deadline.Token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(launcher)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = programRoot,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            if (fixtureLoader)
            {
                process.StartInfo.ArgumentList.Add("fixture-launcher-child");
                process.StartInfo.ArgumentList.Add(dataRoot!);
            }
            process.StartInfo.ArgumentList.Add("lifecycle-relay-child");
            process.StartInfo.ArgumentList.Add(pipeName);
            process.StartInfo.ArgumentList.Add(owner.ApplicationPath!);
            if (!process.Start()) throw new InvalidOperationException("Scratch launcher did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Process? child = null;
            var reaped = false;
            try
            {
                await accept;
                var peer = LocalIdentity.Capture(server);
                Check(peer.ProcessId is > 0 && peer.ApplicationPath is not null &&
                    Path.GetFullPath(peer.ApplicationPath).Equals(adapter, StringComparison.OrdinalIgnoreCase),
                    "The launcher did not run the scratch adapter image.");
                child = Process.GetProcessById(checked((int)peer.ProcessId!.Value));
                _ = child.Handle; // retain this exact generation through shutdown
                if (scenario == "partial-input")
                {
                    await process.StandardInput.WriteAsync("{\"jsonrpc\":".AsMemory(), deadline.Token);
                    await process.StandardInput.FlushAsync(deadline.Token);
                }
                if (scenario == "outstanding-request")
                {
                    await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"fixture\",\"arguments\":{}}}".AsMemory(), deadline.Token);
                    await process.StandardInput.FlushAsync(deadline.Token);
                    using var reader = new StreamReader(server, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                    var dispatched = await reader.ReadLineAsync(deadline.Token);
                    Check(dispatched is not null && dispatched.Contains("\"id\":1", StringComparison.Ordinal), "Fixture request was not dispatched.");
                }
                // Keep parent STDIN open. Endpoint loss alone must release both
                // images, even with incomplete input or an unconfirmed request.
                server.Dispose();
                await child.WaitForExitAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var error = await stderr.WaitAsync(deadline.Token);
                var output = await stdout.WaitAsync(deadline.Token);
                var outcomeUnknown = error.Contains("outcome_unknown", StringComparison.Ordinal);
                var failureTuple = JsonSerializer.Serialize(new
                {
                    scenario, exit = process.ExitCode, outcomeUnknown,
                    stdoutCharacters = output.Length, stderrCharacters = error.Length,
                    disposedPipeFailure = error.Contains("ObjectDisposedException", StringComparison.Ordinal),
                    fixtureDiagnostic = error.Contains("fixture_adapter_disconnected_outcome_unknown", StringComparison.Ordinal)
                        ? "fixture_adapter_disconnected_outcome_unknown" : null
                });
                Check(process.ExitCode != 0 && outcomeUnknown && output.Length == 0,
                    "Lost owner was reported as a completed request. " + failureTuple);
                // STDIN stayed open until both exact generations exited and
                // both output streams closed. Reap the parent's own handles
                // before testing executable replacement, as a real client does.
                child.Dispose();
                child = null;
                process.StandardInput.Dispose();
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                reaped = true;
                process.Dispose();
                foreach (var image in new[]
                {
                    launcher, adapter,
                    Path.Combine(semanticDirectory, Path.GetFileName(testAssembly)),
                    Path.Combine(semanticDirectory, "PegasusCortex.Mcp.Local.dll"),
                    Path.Combine(semanticDirectory, "Pegasus.AgentOps.Engine.dll")
                }) AssertReplaceable(image);
                if (fixtureLoader)
                    foreach (var name in new[] { Path.GetFileName(testAssembly), "PegasusCortex.Mcp.Local.dll",
                        "Pegasus.AgentOps.Engine.dll", "PegasusCortex.Product.dll" })
                        AssertReplaceable(Path.Combine(programRoot, name));
                var launcherAssembly = Path.Combine(programRoot, "PegasusCortex.dll");
                if (File.Exists(launcherAssembly)) AssertReplaceable(launcherAssembly);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    check = fixtureLoader ? "fixture-loader-adapter-release" : "scratch-adapter-launcher-release",
                    sourceLinkedLauncher = fixtureLoader, scenario, adapterExited = true, launcherExited = true,
                    stdinHeldOpen = true, replacementChecked = true, installedChanged = false, fixture = root
                }));
            }
            finally
            {
                // Only the exact fixture generations started above are ours.
                if (!reaped)
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                if (child is not null)
                {
                    if (!child.HasExited) child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    child.Dispose();
                }
            }
        }
        // Retain this explicitly supplied isolated fixture for the build owner's
        // evidence review. This harness performs no artifact cleanup.
    }

    internal static void CopyImageClosure(string source, string destination)
    {
        var files = Directory.EnumerateFiles(source).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)).Take(1025).ToArray();
        Check(files.Length <= 1024, "Image closure exceeds its fixture bound.");
        foreach (var file in files)
        {
            CortexProductLayout.RejectExistingReparseComponents(file);
            var target = Path.Combine(destination, Path.GetFileName(file));
            Check(target.Length <= 240, "Scratch image path exceeds the product path bound.");
            File.Copy(file, target, overwrite: false);
        }
    }

    internal static void WriteFixtureCatalog(string root, string? dataRoot = null)
    {
        var release = CortexLocalProductCommands.Version;
        var components = new Dictionary<string, string>
        {
            ["mcp"] = "semantics", ["host"] = "execution", ["service"] = "execution", ["pao"] = "execution",
            ["process-host"] = "execution", ["ui-host"] = "execution", ["launcher"] = "launcher", ["setup"] = "installer"
        };
        var catalog = CortexProductCatalogV1.Create(release, 1, 2, 1, CortexProductCatalogV1.StableRolePaths.Select(role =>
            new CortexProductRoleV1(role.Key, components[role.Key], release, role.Value)).ToArray());
        var directory = Path.Combine(dataRoot ?? Path.Combine(root, "state"), "install");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "active.v1.json"), JsonSerializer.Serialize(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal static void AssertReplaceable(string path)
    {
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var replacement = path + ".replacement";
        Check(replacement.Length <= 240, "Scratch replacement path exceeds the product path bound.");
        // Reuse only this harness's own unchanged replacement from an earlier scenario.
        if (!File.Exists(replacement)) File.Copy(path, replacement, overwrite: false);
        File.Copy(replacement, path, overwrite: true);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(SHA256.HashData(exclusive).SequenceEqual(before), "Replacement changed scratch image content.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
