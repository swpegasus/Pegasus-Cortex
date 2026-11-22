using System.Text.Json;
using System.Diagnostics;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// Fixed task-owned lifecycle fixtures. No arbitrary executable or output path is accepted.
if (args is ["--app-fixture-root"] or ["--app-fixture-root-wait"] or ["--app-fixture-child"] or ["--app-fixture-exit"])
{
    var directory = new DirectoryInfo(Path.GetFullPath(Environment.CurrentDirectory));
    if (directory.Parent is null || directory.LinkTarget is not null ||
        (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
        !File.Exists(Path.Combine(directory.FullName, "app-fixture.marker")))
        throw new InvalidOperationException("Missing exact application fixture directory.");
    var childMode = args[0] == "--app-fixture-child";
    using var self = Process.GetCurrentProcess();
    await File.WriteAllTextAsync(Path.Combine(directory.FullName, childMode ? "child.json" : "root.json"),
        JsonSerializer.Serialize(new { pid = self.Id, start = self.StartTime.ToUniversalTime().ToString("O") }));
    if (childMode)
    {
        await Task.Delay(TimeSpan.FromSeconds(25));
        return;
    }
    await File.AppendAllTextAsync(Path.Combine(directory.FullName, "launches.txt"), "1\n");
    if (args[0] == "--app-fixture-exit") return;
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing fixture executable.");
    var launch = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory.FullName
    };
    if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        launch.ArgumentList.Add(typeof(Program).Assembly.Location);
    launch.ArgumentList.Add("--app-fixture-child");
    using var child = Process.Start(launch) ?? throw new InvalidOperationException("Fixture child did not start.");
    var ready = Stopwatch.StartNew();
    var childReady = false;
    while (ready.Elapsed < TimeSpan.FromSeconds(5))
    {
        try
        {
            using var identity = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "child.json")));
            childReady = identity.RootElement.GetProperty("pid").GetInt32() == child.Id;
            if (childReady) break;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { }
        await Task.Delay(20);
    }
    if (!childReady) throw new InvalidOperationException("Fixture child identity was not ready.");
    if (args[0] == "--app-fixture-root-wait") await Task.Delay(TimeSpan.FromSeconds(25));
    return;
}

var serialized = JsonSerializer.Serialize(args);
if (Environment.GetEnvironmentVariable("PAO_FAKE_ARGS_PATH") is { Length: > 0 } destination)
    await File.WriteAllTextAsync(destination, serialized);
if(Environment.GetEnvironmentVariable("PAO_FAKE_NSIS")=="1")
{
    if(args is ["/?"]){Console.Out.Write("MakeNSIS v3 fixture");return;}
    var source=args.LastOrDefault()??throw new InvalidOperationException("missing source");
    var script=await File.ReadAllTextAsync(source);
    var output=args.FirstOrDefault(value=>value.StartsWith("/DPAO_OUT=",StringComparison.Ordinal))?[10..];
    if(output is not null)script=script.Replace("${PAO_OUT}",output,StringComparison.Ordinal);
    if(args.Contains("/SAFEPPO",StringComparer.Ordinal))Console.Out.Write(script);
    else
    {
        if(string.IsNullOrWhiteSpace(output))throw new InvalidOperationException("missing output");
        var payload=System.Text.Encoding.UTF8.GetBytes("MZfixture-nsis-installer");
        await File.WriteAllBytesAsync(output,payload);
        Console.Error.Write("fixture compile ok");
    }
    return;
}
else if(Environment.GetEnvironmentVariable("PAO_FAKE_DOTNET_PUBLISH")=="1" &&
    args is ["publish", _, ..] && Array.IndexOf(args,"--output") is var outputIndex &&
    outputIndex >= 0 && outputIndex + 1 < args.Length)
{
    var publishDirectory=Path.GetFullPath(args[outputIndex+1]);
    Directory.CreateDirectory(publishDirectory);
    await File.WriteAllTextAsync(Path.Combine(publishDirectory,"fixture.dll"),"published");
    Console.Out.Write(serialized);
}
else
    Console.Out.Write(serialized);
if (Environment.GetEnvironmentVariable("PAO_FAKE_CWD_PATH") is { Length: > 0 } cwdPath)
    await File.WriteAllTextAsync(cwdPath, Environment.CurrentDirectory);
if(Environment.GetEnvironmentVariable("PAO_FAKE_COUNT_PATH") is {Length:>0} countPath)
    await File.AppendAllTextAsync(countPath,"1\n");
if(args.FirstOrDefault(value=>value.StartsWith("--wait-ms=",StringComparison.Ordinal)) is{ } wait&&int.TryParse(wait.AsSpan(10),out var waitMilliseconds)&&waitMilliseconds is >=1 and <=30000)
    await Task.Delay(waitMilliseconds);
