using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

internal static partial class CortexStableLauncher
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    public static async ValueTask<int?> TryRunSelectedVersionAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var stableRoot = ResolveStableRoot(programFiles);
        var baseRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(baseRoot, stableRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The Cortex launcher path is unavailable.");
        stableRoot = ResolveStableRootForLauncher(programFiles, processPath);
        AssertOrdinaryDirectoryChain(programFiles, stableRoot);
        AssertOrdinaryFile(
            processPath,
            "The stable Cortex launcher is unavailable or is a reparse point.");

        var dataRoot = CortexDataLocation.ResolveMachineDataRoot(stableRoot);
        var selectorPath = Path.GetFullPath(Path.Combine(dataRoot, "install", "active.v1.json"));
        if (selectorPath.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new InvalidDataException("The Cortex selector exceeds the supported path length.");
        AssertOrdinaryDirectoryChain(
            dataRoot,
            Path.GetDirectoryName(selectorPath) ??
                throw new InvalidOperationException("The Cortex selector has no parent directory."));
        AssertOrdinaryFile(
            selectorPath,
            "The stable Cortex launcher has no ordinary current-version selector.");

        cancellationToken.ThrowIfCancellationRequested();
        var catalog = CortexProductCatalogV1.ReadExact(selectorPath);
        var mcpRole = catalog.Roles.SingleOrDefault(
            role => string.Equals(role.Role, "mcp", StringComparison.Ordinal)) ??
            throw new InvalidDataException("The active Cortex catalog has no MCP role.");
        var executable = Path.GetFullPath(Path.Combine(
            stableRoot,
            mcpRole.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
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

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = componentRoot
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        using var job = CreateKillOnCloseJob();
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

    internal static string ResolveStableRootForLauncher(
        string programFilesPath,
        string launcherPath)
    {
        var stableRoot = ResolveStableRoot(programFilesPath);
        if (string.IsNullOrWhiteSpace(launcherPath) || !Path.IsPathFullyQualified(launcherPath))
            throw new InvalidOperationException("The Cortex launcher path must be absolute.");
        var launcherRoot = Path.GetFullPath(
            Path.GetDirectoryName(Path.GetFullPath(launcherPath)) ??
                throw new InvalidOperationException("The Cortex launcher has no parent directory."));
        if (!string.Equals(launcherRoot, stableRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The stable Cortex launcher is outside its machine application root.");
        }
        return stableRoot;
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
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    canonicalRoot, StringComparison.OrdinalIgnoreCase))
                return;
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
                    job, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
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

    private sealed record VersionSelector(int SchemaVersion, string ReleaseVersion);

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
        SafeFileHandle job, uint informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
