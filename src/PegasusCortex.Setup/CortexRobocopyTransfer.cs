using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Setup;

/// <summary>One fixed OS bulk copy. No shell, deletion, retry recipe or alternate executable.</summary>
internal static class CortexRobocopyTransfer
{
    private static readonly string[] Extensions = [".db", ".sqlite", ".sqlite3"];
    private static readonly string[] Companions = ["-wal", "-shm", "-journal"];
    internal sealed record Capture(string Text, bool Truncated);
    internal sealed record Result(int ExitCode, Capture Output, Capture Error);

    internal static bool IsDatabaseFile(string path) => Extensions.Any(extension =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
        Companions.Any(suffix => path.EndsWith(extension + suffix, StringComparison.OrdinalIgnoreCase)));

    internal static string DatabasePrimary(string path)
    {
        foreach (var suffix in Companions)
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Extensions.Any(extension => path[..^suffix.Length].EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                return path[..^suffix.Length];
        return path;
    }

    internal static string[] Arguments(string source, string destination) =>
        [source, destination, "/E", "/COPY:DAT", "/DCOPY:DAT", "/MT:8", "/R:0", "/W:0",
            "/SL", "/SJ", "/NFL", "/NDL", "/NP", "/NJH", "/NJS", "/XF",
            .. Extensions.SelectMany(extension => new[] { "*" + extension }.Concat(Companions.Select(suffix => "*" + extension + suffix)))];

    internal static void RequireComplete(Result result)
    {
        // Native1 means copied,2/3 can include harmless destination extras.
        // A mismatch is distinct from native failure but cannot select a stage.
        if (result.ExitCode is >= 0 and <= 3) return;
        var reason = result.ExitCode is >= 4 and < 8 ? "mismatched entries" : "copy failure";
        throw new IOException($"Robocopy reported {reason} (native exit {result.ExitCode}); the staged copy was not selected. " +
            (result.Error.Truncated || result.Output.Truncated ? "Diagnostic tail truncated. " : "") +
            (result.Error.Text.Length != 0 ? result.Error.Text : result.Output.Text));
    }

    internal static Result Copy(string source, string destination, CancellationToken token,
        TimeSpan? timeout = null, Action<int>? started = null) =>
        CopyAsync(source, destination, token, timeout, started).GetAwaiter().GetResult();

    private static async Task<Result> CopyAsync(string source, string destination, CancellationToken token,
        TimeSpan? timeout, Action<int>? started)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Robocopy requires Windows.");
        var duration = timeout ?? TimeSpan.FromHours(6);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(6)) throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        using var child = Start(Arguments(source, destination));
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        signal.SafeWaitHandle = new SafeWaitHandle(child.Process.DangerousGetHandle(), ownsHandle: false);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var elapsed = Stopwatch.StartNew();
        deadline.CancelAfter(duration);
        var output = DrainAsync(child.Output, 4096);
        var error = DrainAsync(child.Error, 4096);
        var readFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = output.ContinueWith(task => readFailure.TrySetException(task.Exception!.InnerExceptions),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = error.ContinueWith(task => readFailure.TrySetException(task.Exception!.InnerExceptions),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Exception? primary = null;
        try
        {
            started?.Invoke(child.Pid);
            if (elapsed.Elapsed >= duration) deadline.Cancel();
            deadline.Token.ThrowIfCancellationRequested();
            var completion = await Task.WhenAny(exited.Task, readFailure.Task).WaitAsync(deadline.Token).ConfigureAwait(false);
            await completion.ConfigureAwait(false);
            var code = ExitCode(child.Process);
            // No descendant may retain a pipe or continue work after this call.
            child.Job.Dispose();
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new Result(code, output.Result, error.Result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var stopRequested = WaitForSingleObject(child.Process, 0) != 0;
            child.Job.Dispose();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Exception failure = token.IsCancellationRequested
                ? new OperationCanceledException("The bulk copy was cancelled; the staged copy was not selected.", token)
                : new TimeoutException("The bulk copy deadline elapsed; the staged copy was not selected.");
            failure.Data["NativeExitCode"] = ExitCode(child.Process);
            failure.Data["ChildStopRequested"] = stopRequested;
            primary = failure;
            throw failure;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            child.Job.Dispose(); // Kill-on-close also covers unexpected exceptions.
            var stopped = WaitForSingleObject(child.Process, 5000) == 0;
            wait.Unregister(null);
            if (!stopped) throw new IOException("The owned Robocopy process did not confirm exit; copy outcome is uncertain.", primary);
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) when (primary is not null) { primary.Data["OutputCleanup"] = "unavailable"; }
            if (readFailure.Task.IsFaulted) _ = readFailure.Task.Exception;
        }
    }

    internal static async Task<Capture> DrainAsync(TextReader reader, int maximum)
    {
        var text = new StringBuilder(maximum); var buffer = new char[1024]; var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) return new Capture(text.ToString(), truncated);
            text.Append(buffer, 0, count);
            if (text.Length <= maximum) continue;
            text.Remove(0, text.Length - maximum); truncated = true;
        }
    }

    private sealed record Child(SafeProcessHandle Process, SafeFileHandle Job, StreamReader Output, StreamReader Error, int Pid) : IDisposable
    {
        public void Dispose() { Job.Dispose(); Output.Dispose(); Error.Dispose(); Process.Dispose(); }
    }

    private static Child Start(string[] arguments)
    {
        // Reuse the launcher's explicit inherited-handle pattern. JOB_LIST
        // assigns containment atomically at creation, before any copy can run.
        var executable = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null, job = null;
        SafeProcessHandle? process = null;
        var list = IntPtr.Zero; var handles = IntPtr.Zero; var jobs = IntPtr.Zero; var initialized = false;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw NativeFailure("The bulk-copy containment job could not be created.");
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, checked((uint)Marshal.SizeOf<JobLimits>())))
                throw NativeFailure("The bulk-copy containment policy could not be applied.");
            if (!CreatePipe(out outputRead, out outputWrite, ref attributes, 0) ||
                !CreatePipe(out errorRead, out errorWrite, ref attributes, 0))
                throw NativeFailure("The bulk-copy output pipes could not be created.");
            if (!SetHandleInformation(outputRead, 1, 0) || !SetHandleInformation(errorRead, 1, 0))
                throw NativeFailure("The bulk-copy parent read handles could not be protected.");
            input = CreateFileW("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero);
            if (input.IsInvalid) throw NativeFailure("The bulk-copy input could not be closed safely.");
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0 || size > 65536) throw new IOException("Bulk-copy startup metadata is unavailable.");
            list = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(list, 2, 0, ref size)) throw NativeFailure("Bulk-copy startup initialization failed.");
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errorWrite.DangerousGetHandle());
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x20002), handles, checked((nuint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(list, 0, new IntPtr(0x2000d), jobs, checked((nuint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw NativeFailure("Bulk-copy handle/job admission failed.");
            var startup = new StartupEx
            {
                Startup = new Startup { Size = Marshal.SizeOf<StartupEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle() },
                Attributes = list
            };
            var command = string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
            if (!CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                0x08080000, IntPtr.Zero, Environment.SystemDirectory, ref startup, out var created))
                throw NativeFailure("The fixed System32 Robocopy executable could not start.");
            process = new SafeProcessHandle(created.Process, true);
            using var thread = new SafeWaitHandle(created.Thread, true);
            var output = new StreamReader(new FileStream(outputRead, FileAccess.Read), detectEncodingFromByteOrderMarks: true);
            var error = new StreamReader(new FileStream(errorRead, FileAccess.Read), detectEncodingFromByteOrderMarks: true);
            var child = new Child(process, job, output, error, checked((int)created.Pid));
            process = null; job = null; outputRead = null; errorRead = null;
            return child;
        }
        catch
        {
            job?.Dispose();
            if (process is not null && !process.IsInvalid) _ = WaitForSingleObject(process, 5000);
            process?.Dispose(); outputRead?.Dispose(); errorRead?.Dispose();
            throw;
        }
        finally
        {
            outputWrite?.Dispose(); errorWrite?.Dispose(); input?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
        }
    }

    internal static string Quote(string argument)
    {
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            text.Append(character);
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static int ExitCode(SafeProcessHandle process) => GetExitCodeProcess(process, out var code)
        ? unchecked((int)code) : throw NativeFailure("The original bulk-copy exit code is unavailable.");
    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public IntPtr Security; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct Startup
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, Fill, Flags;
        public short Show, Reserved2; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum;
        public uint Active; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string file, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, uint kind, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr kind, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string executable, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
