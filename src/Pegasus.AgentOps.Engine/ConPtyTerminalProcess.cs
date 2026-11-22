using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

/// <summary>Real Windows pseudoconsole, owned user process and kill-on-close job.
/// Output is drained independently of input and ClosePseudoConsole to avoid pipe deadlocks.</summary>
internal sealed class ConPtyTerminalProcess
{
    private readonly Process _process;
    private readonly WindowsJob _job;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Action _ended;
    private readonly Task<string?> _pump;
    private readonly int _pid;
    private readonly string _start;
    private volatile bool _rootExited;
    private readonly object _gate = new();
    private nint _console;
    private Task? _cleanup;
    internal Task Lifetime { get; }
    internal int Pid => _pid;
    internal string Start => _start;
    internal string? OutputIssue => _pump.IsCompletedSuccessfully ? _pump.Result : null;

    private ConPtyTerminalProcess(Process process, WindowsJob job, nint console,
        FileStream input, FileStream output, Task<string?> pump, Action ended)
    {
        _process = process; _pid = process.Id; _start = process.StartTime.ToUniversalTime().ToString("O");
        _job = job; _console = console; _input = input; _output = output; _ended = ended; _pump = pump;
        Lifetime = ObserveExitAsync();
    }

    internal bool IsExited { get { try { return _rootExited || _process.HasExited; } catch (InvalidOperationException) when (_rootExited) { return true; } } }

    private static async Task<string?> DrainAsync(FileStream output, Action<byte[], int> append)
    {
        var bytes = new byte[8192];
        try
        {
            while (true)
            {
                var count = await output.ReadAsync(bytes).ConfigureAwait(false);
                if (count == 0) return null;
                append(bytes, count);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return "terminal_output_lost"; }
    }

    internal static ConPtyTerminalProcess StartProcess(ToolInvocation invocation, int width, int height,
        Action<byte[], int> append, Action ended)
    {
        using var admission = ToolRecipeValidation.AcquireInvocationLease(invocation);
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        FileStream? input = null, output = null;
        WindowsJob? job = null;
        Process? process = null;
        nint console = 0, attributes = 0, environment = 0;
        var pi = new ProcessInformation();
        var attributeInitialized = false;
        Task<string?>? pump = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) ||
                !CreatePipe(out outputRead, out outputWrite, 0, 0)) throw NativeFailure();
            input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false); inputWrite = null;
            output = new FileStream(outputRead, FileAccess.Read, 8192, isAsync: false); outputRead = null;
            var drainingOutput = output;
            pump = Task.Run(() => DrainAsync(drainingOutput, append));
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord((short)width, (short)height),
                inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out console));
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw NativeFailure();
            attributeInitialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (nint)0x00020016, console, (nuint)nint.Size, 0, 0)) throw NativeFailure();
            // Explicit null standard handles force connections to ConPTY even when the
            // parent has redirected pipes (microsoft/terminal discussion15814).
            var si = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100 }, Attributes = attributes };
            var environmentText = string.Join('\0', invocation.Environment.Entries.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(environmentText);
            var command = new StringBuilder(string.Join(' ', new[] { invocation.ExecutablePath }.Concat(invocation.Arguments).Select(Quote)));
            // The child cannot run before its exact job ownership is established.
            if (!CreateProcessW(invocation.ExecutablePath, command, 0, 0, false, 0x00080404,
                environment, invocation.WorkingDirectory, ref si, out pi)) throw NativeFailure();
            process = Process.GetProcessById(checked((int)pi.ProcessId));
            job = WindowsJob.Create();
            job.AssignAndVerify(process);
            var result = new ConPtyTerminalProcess(process, job, console, input, output, pump!, ended);
            // Ownership transferred before resume, including output drain and teardown.
            process = null; job = null; console = 0; input = null; output = null;
            if (ResumeThread(pi.Thread) == uint.MaxValue)
            {
                _ = result.CloseAsync();
                throw NativeFailure();
            }
            return result;
        }
        catch
        {
            if (pi.Process != 0) _ = TerminateProcess(pi.Process, 137);
            job?.Dispose(); process?.Dispose();
            inputRead?.Dispose(); outputWrite?.Dispose();
            // A drainer exists from pipe creation, even before STARTUPINFOEX or
            // CreateProcess can fail. Never close its read side before ConPTY.
            if (console != 0) ClosePseudoConsole(console);
            if (pump is not null) _ = pump.GetAwaiter().GetResult();
            input?.Dispose(); output?.Dispose();
            throw;
        }
        finally
        {
            if (pi.Thread != 0) CloseHandle(pi.Thread);
            if (pi.Process != 0) CloseHandle(pi.Process);
            if (attributeInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
        }
    }

    internal async Task WriteAsync(byte[] bytes)
    {
        await _input.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        await _input.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
    internal void Resize(int width, int height)
    { lock (_gate) { if (_console == 0) throw new AgentOpsException("terminal_closed"); Marshal.ThrowExceptionForHR(ResizePseudoConsole(_console, new((short)width, (short)height))); } }
    private async Task ObserveExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        _rootExited = true;
        await CleanupAsync().ConfigureAwait(false);
    }
    internal async Task CloseAsync()
    {
        if (Lifetime.IsCompletedSuccessfully) return;
        _job.RequestTermination();
        if (!_rootExited) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _rootExited = true;
        await CleanupAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Lifetime.ConfigureAwait(false);
    }
    private Task CleanupAsync()
    {
        lock (_gate) return _cleanup ??= Task.Run(async () =>
        {
            _job.RequestTermination();
            var deadline = Stopwatch.StartNew();
            while (_job.LiveProcessCount() != 0)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new AgentOpsException("terminal_close_incomplete");
                await Task.Delay(20).ConfigureAwait(false);
            }
            _job.Dispose();
            nint console;
            lock (_gate) { console = _console; _console = 0; }
            if (console != 0) await Task.Run(() => ClosePseudoConsole(console)).ConfigureAwait(false);
            _input.Dispose();
            await _pump.ConfigureAwait(false);
            _output.Dispose();
            _process.Dispose();
            _ended();
        });
    }
    private static AgentOpsException NativeFailure() => new("terminal_native", inner: new Win32Exception(Marshal.GetLastWin32Error()));
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(nint console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string cwd, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint handle, uint exit);
}
