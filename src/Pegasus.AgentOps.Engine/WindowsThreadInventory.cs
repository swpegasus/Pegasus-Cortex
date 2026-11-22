using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

[SupportedOSPlatform("windows")]
internal sealed class WindowsThreadInventory : IProcessThreadInventory
{
    private const uint ProcessQueryAccess = 0x00001000 | 0x00100000;
    private const uint ThreadQueryAccess = 0x00000800 | 0x00100000;
    private const uint SnapshotThreads = 0x00000004;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;

    public IProcessThreadProcessLease? OpenProcess(int pid)
    {
        var handle = Native.OpenProcess(ProcessQueryAccess, false, checked((uint)pid));
        if (!handle.IsInvalid) return new ProcessLease(handle);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorInvalidParameter or ErrorNotFound) return null;
        throw Failure("process_query_failed", error);
    }

    public IProcessThreadSnapshot OpenSnapshot()
    {
        // TH32CS_SNAPTHREAD ignores the PID parameter and captures systemwide.
        // The workflow bounds iteration; it cannot bound Windows' native snapshot allocation.
        var handle = Native.CreateToolhelp32Snapshot(SnapshotThreads, 0);
        if (!handle.IsInvalid) return new SnapshotLease(handle);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Failure("snapshot_failed", error);
    }

    public IProcessThreadLease? OpenThread(uint tid)
    {
        var handle = Native.OpenThread(ThreadQueryAccess, false, tid);
        if (!handle.IsInvalid) return new ThreadLease(handle);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorInvalidParameter or ErrorNotFound) return null;
        throw Failure("thread_query_failed", error);
    }

    private static ProcessThreadInventoryException Failure(string code, int error) =>
        new(error == 5 ? "access" : code, new Win32Exception(error));

    private static bool Exited(SafeHandle handle, string code) => Native.WaitForSingleObject(handle, 0) switch
    {
        0 => true,
        258 => false,
        uint.MaxValue => throw Failure(code, Marshal.GetLastWin32Error()),
        _ => throw new ProcessThreadInventoryException(code)
    };

    private static string Start(Native.FileTime time, string code) =>
        ProcessThreadOperations.DecodeCreationTime(((ulong)time.High << 32) | time.Low, code);

    private sealed class ProcessLease(SafeProcessHandle handle) : IProcessThreadProcessLease
    {
        public bool IsExited() => Exited(handle, "process_query_failed");

        public string ReadStart()
        {
            if (!Native.GetProcessTimes(handle, out var creation, out _, out _, out _))
                throw Failure("process_query_failed", Marshal.GetLastWin32Error());
            return Start(creation, "process_query_failed");
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class ThreadLease(SafeWaitHandle handle) : IProcessThreadLease
    {
        public uint ReadOwnerPid()
        {
            var pid = Native.GetProcessIdOfThread(handle);
            if (pid == 0) throw Failure("thread_query_failed", Marshal.GetLastWin32Error());
            return pid;
        }

        public bool IsExited() => Exited(handle, "thread_query_failed");

        public string ReadStart()
        {
            if (!Native.GetThreadTimes(handle, out var creation, out _, out _, out _))
                throw Failure("thread_query_failed", Marshal.GetLastWin32Error());
            return Start(creation, "thread_query_failed");
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class SnapshotLease(SafeFileHandle handle) : IProcessThreadSnapshot
    {
        private bool _started;
        private bool _finished;

        public bool MoveNext(out ProcessThreadSnapshotEntry entry)
        {
            entry = default;
            if (_finished) return false;
            var size = checked((uint)Marshal.SizeOf<Native.ThreadEntry32>());
            var native = new Native.ThreadEntry32 { Size = size };
            var success = _started ? Native.Thread32Next(handle, ref native) : Native.Thread32First(handle, ref native);
            _started = true;
            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNoMoreFiles) throw Failure("snapshot_failed", error);
                _finished = true;
                return false;
            }
            // dwSize may shrink. Only the two identities through offset 16 are
            // consumed; never read priority/control fields or an absent member.
            if (native.Size < 16 || native.Size > size) throw new ProcessThreadInventoryException("snapshot_failed");
            entry = new(native.ThreadId, native.OwnerProcessId);
            return true;
        }

        public void Dispose() => handle.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            internal uint Low;
            internal uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ThreadEntry32
        {
            internal uint Size;
            internal uint Usage;
            internal uint ThreadId;
            internal uint OwnerProcessId;
            internal int BasePriority;
            internal int DeltaPriority;
            internal uint Flags;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeWaitHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint tid);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32First(SafeFileHandle snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32Next(SafeFileHandle snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creation,
            out FileTime exit, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetThreadTimes(SafeWaitHandle thread, out FileTime creation,
            out FileTime exit, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetProcessIdOfThread(SafeWaitHandle thread);
    }
}
