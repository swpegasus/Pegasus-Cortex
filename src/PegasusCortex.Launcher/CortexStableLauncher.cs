using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Launcher;

internal static partial class CortexStableLauncher
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    public static ValueTask<int> RunSelectedVersionAsync(
        string[] arguments,
        CancellationToken cancellationToken) => RunSelectedVersionAsync(
            Environment.ProcessPath ?? throw new InvalidOperationException("The Cortex launcher path is unavailable."),
            CortexDataLocation.ResolveMachineDataRoot, arguments, cancellationToken);

    // Internal composition seam for source-linked executable fixtures. The
    // shipping entry above always uses the fixed machine registration reader.
    internal static async ValueTask<int> RunSelectedVersionAsync(string launcherPath,
        Func<string, string> resolveDataRoot, string[] arguments, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The stable Cortex launcher requires Windows.");
        ArgumentNullException.ThrowIfNull(resolveDataRoot);
        ArgumentNullException.ThrowIfNull(arguments);
        var stableRoot = ResolveInstallRootForLauncher(launcherPath);
        AssertOrdinaryDirectoryChain(stableRoot, stableRoot);
        AssertOrdinaryFile(
            launcherPath,
            "The stable Cortex launcher is unavailable or is a reparse point.");

        var selectorPath = ResolveSelectorPath(stableRoot, resolveDataRoot);

        cancellationToken.ThrowIfCancellationRequested();
        var catalog = CortexProductCatalogV1.ReadExact(selectorPath);
        var roleName = SelectRole(arguments);
        var selectedRole = catalog.Roles.SingleOrDefault(
            role => string.Equals(role.Role, roleName, StringComparison.Ordinal)) ??
            throw new InvalidDataException($"The active Cortex catalog has no {roleName} role.");
        var executable = Path.GetFullPath(Path.Combine(
            stableRoot,
            selectedRole.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relativeExecutable = Path.GetRelativePath(stableRoot, executable);
        if (Path.IsPathRooted(relativeExecutable) ||
            relativeExecutable == ".." ||
            relativeExecutable.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected Cortex executable escaped its install root.");
        }
        var componentRoot = Path.GetDirectoryName(executable) ??
            throw new InvalidDataException("The selected Cortex executable has no parent directory.");
        AssertOrdinaryDirectoryChain(stableRoot, componentRoot);
        AssertOrdinaryFile(
            executable,
            "The selected immutable Cortex executable is unavailable or is a reparse point.");

        using var job = CreateKillOnCloseJob();
        var standardHandles = CortexLauncherConsole.CaptureStandardHandles();
        if (!CortexLauncherConsole.IsGuiRoute(arguments) && standardHandles.Any(handle => handle.Redirected))
            return await RunWithStandardHandlesAsync(executable, componentRoot, arguments, standardHandles,
                job, cancellationToken).ConfigureAwait(false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = CortexLauncherConsole.ChildNeedsNoWindow(arguments),
                WorkingDirectory = componentRoot
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("The selected Cortex application did not start.");
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    internal static string SelectRole(string[] arguments) =>
        arguments is ["--exit"] || arguments.FirstOrDefault() == "--payload-storage" ||
        CortexLauncherConsole.IsGuiRoute(arguments) ? "host" : "mcp";

    // Process.Start supplies STARTF_USESTDHANDLES only when it creates a new
    // redirected pipe. This launcher forwards existing endpoints without a
    // relay; specify those endpoints and restrict inheritance explicitly.
    private static async Task<int> RunWithStandardHandlesAsync(string executable, string workingDirectory,
        string[] arguments, IReadOnlyList<CortexLauncherConsole.StandardHandle> handles,
        SafeFileHandle job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var child = StartWithStandardHandles(executable, workingDirectory, arguments, handles);
        try
        {
            // Suspended creation closes the interval in which an unowned
            // child could execute or spawn a descendant before job assignment.
            if (!AssignProcessToJobObjectNative(job, child.Process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(child.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _ = CloseHandle(child.Thread);
            child.Thread = IntPtr.Zero;
            using var signal = new NativeProcessWaitHandle(child.Process);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(signal,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited,
                Timeout.Infinite, executeOnlyOnce: true);
            try { await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally { _ = registration.Unregister(null); }
            if (!GetExitCodeProcess(child.Process, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)exitCode);
        }
        catch (Exception error)
        {
            // Only this exact owned generation is terminated. Closing the job
            // on unwind also releases its descendants; no borrowed STD handle
            // is ever closed or made inheritable in the parent.
            _ = TerminateProcess(child.Process, 137);
            if (WaitForSingleObject(child.Process, 5000) != 0)
                throw new InvalidOperationException("The selected Cortex child did not complete failure cleanup.", error);
            throw;
        }
        finally
        {
            if (child.Thread != IntPtr.Zero) _ = CloseHandle(child.Thread);
            _ = CloseHandle(child.Process);
        }
    }

    private static NativeProcessInformation StartWithStandardHandles(string executable, string workingDirectory,
        string[] arguments, IReadOnlyList<CortexLauncherConsole.StandardHandle> handles)
    {
        var commandLine = BuildCommandLine(executable, arguments);
        var duplicates = new List<SafeFileHandle>(3);
        var attributes = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var initialized = false;
        try
        {
            var startup = new NativeStartupInfoEx
            {
                Startup = new NativeStartupInfo { Size = Marshal.SizeOf<NativeStartupInfoEx>(), Flags = 0x100 }
            };
            foreach (var handle in handles)
            {
                if (!handle.Present) continue;
                if (!DuplicateHandle(GetCurrentProcess(), handle.Value, GetCurrentProcess(),
                    out var duplicate, 0, true, 2))
                {
                    var error = Marshal.GetLastWin32Error();
                    duplicate.Dispose();
                    throw new Win32Exception(error);
                }
                duplicates.Add(duplicate);
                switch (handle.Kind)
                {
                    case -10: startup.Startup.StdInput = duplicate.DangerousGetHandle(); break;
                    case -11: startup.Startup.StdOutput = duplicate.DangerousGetHandle(); break;
                    case -12: startup.Startup.StdError = duplicate.DangerousGetHandle(); break;
                    default: throw new InvalidOperationException("Unexpected standard handle kind.");
                }
            }
            if (duplicates.Count == 0)
                throw new InvalidOperationException("The selected Cortex child has no standard endpoints to forward.");
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0 || size > 65536)
                throw new InvalidOperationException("The child handle-inheritance metadata is unavailable.");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            handleList = Marshal.AllocHGlobal(duplicates.Count * IntPtr.Size);
            for (var index = 0; index < duplicates.Count; index++)
                Marshal.WriteIntPtr(handleList, index * IntPtr.Size, duplicates[index].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020002), handleList,
                checked((nuint)(duplicates.Count * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            startup.Attributes = attributes;
            // Extended startup info + suspended. CREATE_NO_WINDOW is kept off
            // for an attached interactive console with partial redirection.
            var flags = 0x00080004u;
            if (CortexLauncherConsole.ShouldCreateNoWindow(false, CortexLauncherConsole.HasConsole(), handles))
                flags |= 0x08000000;
            if (!CreateProcessW(executable, new StringBuilder(commandLine, commandLine.Length + 1),
                IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, workingDirectory, ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return child;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            // Parent duplicates must close before the child is resumed, so a
            // retained duplicate cannot delay STDIN EOF or output-pipe closure.
            foreach (var duplicate in duplicates) duplicate.Dispose();
        }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder();
        foreach (var value in new[] { executable }.Concat(arguments))
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Contains('\0')) throw new ArgumentException("A child argument contains a null character.");
            if (command.Length != 0) command.Append(' ');
            command.Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2).Append('"');
            if (command.Length >= 32767) throw new ArgumentException("The child command line exceeds the Windows limit.");
        }
        return command.ToString();
    }

    private sealed class NativeProcessWaitHandle : WaitHandle
    {
        internal NativeProcessWaitHandle(IntPtr process)
        {
            // A registered wait can finish releasing its SafeHandle after its
            // callback runs. Give it its own non-inheritable process reference.
            if (!DuplicateWaitHandle(GetCurrentProcess(), process, GetCurrentProcess(),
                out var wait, 0, false, 2))
            {
                var error = Marshal.GetLastWin32Error();
                wait.Dispose();
                throw new Win32Exception(error);
            }
            SafeWaitHandle = wait;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStartupInfoEx { public NativeStartupInfo Startup; public IntPtr Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateWaitHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeWaitHandle duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, nuint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string workingDirectory, ref NativeStartupInfoEx startup, out NativeProcessInformation process);

    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObjectNative(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string ResolveSelectorPath(string stableRoot, Func<string, string> resolveDataRoot)
    {
        ArgumentNullException.ThrowIfNull(resolveDataRoot);
        // Production's fixed resolver validates the registered root and its
        // ancestry; fixture composition supplies an independently validated root.
        var dataRoot = resolveDataRoot(stableRoot);
        var selectorPath = Path.GetFullPath(Path.Combine(dataRoot, "install", "active.v1.json"));
        if (selectorPath.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new InvalidDataException("The Cortex selector exceeds the supported path length.");
        AssertOrdinaryDirectoryChain(dataRoot, Path.GetDirectoryName(selectorPath)!);
        AssertOrdinaryFile(selectorPath, "The stable Cortex launcher has no ordinary current-version selector.");
        return selectorPath;
    }

    internal static string ResolveStableRootForLauncher(
        string programFilesPath,
        string launcherPath)
    {
        var stableRoot = ResolveStableRoot(programFilesPath);
        return RequireLauncherInRoot(stableRoot, launcherPath);
    }

    internal static string ResolveInstallRootForLauncher(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath) || !Path.IsPathFullyQualified(launcherPath))
            throw new InvalidOperationException("The Cortex launcher path must be absolute.");
        var installRoot = Path.GetDirectoryName(Path.GetFullPath(launcherPath)) ??
            throw new InvalidOperationException("The Cortex launcher has no parent directory.");
        return RequireLauncherInRoot(installRoot, launcherPath);
    }

    private static string RequireLauncherInRoot(string stableRoot, string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath) || !Path.IsPathFullyQualified(launcherPath))
            throw new InvalidOperationException("The Cortex launcher path must be absolute.");
        var canonicalLauncher = Path.GetFullPath(launcherPath);
        var launcherRoot = Path.GetFullPath(
            Path.GetDirectoryName(canonicalLauncher) ??
                throw new InvalidOperationException("The Cortex launcher has no parent directory."));
        if (!string.Equals(launcherRoot, stableRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The stable Cortex launcher is outside its machine application root.");
        }
        return stableRoot;
    }

    internal static string ResolveStableRoot(string programFilesPath)
    {
        if (string.IsNullOrWhiteSpace(programFilesPath) ||
            !Path.IsPathFullyQualified(programFilesPath))
        {
            throw new InvalidOperationException(
                "The machine Program Files directory is unavailable or is not absolute.");
        }
        var canonicalProgramFiles = Path.GetFullPath(programFilesPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(canonicalProgramFiles, "Pegasus", "Cortex"));
    }

    private static void AssertOrdinaryDirectoryChain(string root, string directory)
    {
        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The selected Cortex component crosses a missing directory or reparse point.");
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        throw new InvalidDataException("The selected Cortex component is outside its install root.");
    }

    private static void AssertOrdinaryFile(string path, string message)
    {
        var item = new FileInfo(path);
        if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException(message, path);
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;
    }

    [GeneratedRegex(@"^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    internal sealed record VersionSelector(int SchemaVersion, string ReleaseVersion);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}

/// <summary>Console attachment for explicit CLI use; GUI and redirected MCP never allocate a console.</summary>
internal static class CortexLauncherConsole
{
    internal readonly record struct StandardHandle(int Kind, IntPtr Value, bool Present, bool Redirected);

    internal static bool IsGuiRoute(string[] arguments) => arguments.Length == 0 || arguments is ["--dashboard"];

    internal static bool HasConsole() => OperatingSystem.IsWindows() && GetConsoleCP() != 0;

    internal static bool ChildNeedsNoWindow(string[] arguments) =>
        ShouldCreateNoWindow(IsGuiRoute(arguments), HasConsole(), CaptureStandardHandles());

    internal static bool ShouldCreateNoWindow(bool guiRoute, bool attached, IReadOnlyList<StandardHandle> handles) =>
        guiRoute || !attached || IsHeadless(handles);

    private static bool IsHeadless(IReadOnlyList<StandardHandle> handles) =>
        handles.Any(handle => handle.Redirected) && handles.All(handle => !handle.Present || handle.Redirected);

    internal static void Prepare(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Cortex launcher requires Windows.");
        PrepareCore(IsGuiRoute(arguments), HasConsole(), CaptureStandardHandles(),
            () => AttachConsole(uint.MaxValue) ? 0 : Marshal.GetLastWin32Error(),
            (kind, value) => SetStdHandle(kind, value) ? 0 : Marshal.GetLastWin32Error());
    }

    // The seam is internal and fixture-only: shipping startup always uses the
    // native calls above. No stream is read, replaced with a relay, or closed.
    internal static void PrepareCore(bool guiRoute, bool attached, IReadOnlyList<StandardHandle> handles,
        Func<int> attachParent, Func<int, IntPtr, int> restoreHandle)
    {
        if (guiRoute || attached || IsHeadless(handles)) return;
        var attachError = 0;
        try { attachError = attachParent(); }
        finally
        {
            // AttachConsole can populate the standard-handle table. Keep every
            // inherited file/pipe/NUL endpoint, including mixed redirections.
            // These are borrowed handles; closing them would break MCP/CLI I/O.
            var restoreError = 0;
            foreach (var handle in handles)
                if (handle.Redirected)
                {
                    var error = restoreHandle(handle.Kind, handle.Value);
                    if (restoreError == 0) restoreError = error;
                }
            if (restoreError != 0) throw new Win32Exception(restoreError);
        }
        // Already attached, no parent console, and an exited parent are normal.
        // Never allocate a replacement console when attachment is unavailable.
        if (attachError is not (0 or 5 or 6 or 87)) throw new Win32Exception(attachError);
    }

    internal static StandardHandle[] CaptureStandardHandles() =>
        [Capture(-10), Capture(-11), Capture(-12)];

    private static StandardHandle Capture(int kind)
    {
        var value = GetStdHandle(kind);
        if (value == IntPtr.Zero || value == new IntPtr(-1)) return new(kind, value, false, false);
        var fileType = GetFileType(value);
        var valid = fileType != 0 || Marshal.GetLastWin32Error() == 0;
        var redirected = valid && !GetConsoleMode(value, out _);
        return new(kind, value, true, redirected);
    }

    internal static void ReportFailure(bool guiRoute, string message) => ReportFailure(
        guiRoute && OperatingSystem.IsWindows(), message,
        text => { try { Console.Error.WriteLine(text); } catch (IOException) { } },
        text => { _ = MessageBoxW(IntPtr.Zero, text, "Pegasus Cortex", 0x10); });

    internal static void ReportFailure(bool guiRoute, string message, Action<string> writeError, Action<string> showError)
    {
        var bounded = new string(message.Take(512).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (bounded.Length == 0) bounded = "An unexpected startup error occurred.";
        if (guiRoute) showError("Cortex could not continue.\n\n" + bounded);
        else writeError("Pegasus Cortex launcher failed: " + bounded);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int kind);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int kind, IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
