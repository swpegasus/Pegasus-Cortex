using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pegasus.AgentOps.Internal;

internal static class ApplicationHost
{
    internal static async Task<int> RunAsync(bool visible)
    {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        try
        {
            // The owner assigns this gated bootstrap to its exact job before sending bytes.
            var request = await ProcessHostRequest.ReadAsync(input);
            if (!IsProcessInJob(GetCurrentProcess(), 0, out var assigned) || !assigned) return 8;
            var start = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = false, CreateNoWindow = !visible,
                WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                WorkingDirectory = request.WorkingDirectory
            };
            start.Environment.Clear();
            foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            if (!IsProcessInJob(process.Handle, 0, out assigned) || !assigned) { process.Kill(entireProcessTree: true); return 8; }
            var stdout = DiscardAsync(process.StandardOutput.BaseStream); var stderr = DiscardAsync(process.StandardError.BaseStream);
            await new ApplicationHostFrame(ApplicationHostFrame.Started, process.Id, process.StartTime.ToUniversalTime().Ticks, 0).WriteAsync(output);
            await process.WaitForExitAsync();
            await new ApplicationHostFrame(ApplicationHostFrame.RootExited, 0, 0, process.ExitCode).WriteAsync(output);
            // Root exit is not application exit. Continue draining inherited handles until
            // the owner observes no descendants or terminates the exact application job.
            _ = await input.ReadAsync(new byte[1]); _ = stdout; _ = stderr;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            try { await new ApplicationHostFrame(ApplicationHostFrame.Failed, 0, 0, 0).WriteAsync(output); } catch (IOException) { }
            return 8;
        }
    }
    private static async Task DiscardAsync(Stream source)
    {
        try { var buffer = new byte[8192]; while (await source.ReadAsync(buffer) != 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
