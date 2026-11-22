using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

// Fixed SetupAPI/ConfigMgr32 readers only. Keys/types follow Microsoft's SDK:
// https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/devpkey.h
// No class installer, property setter, device interface/file open or remote API.
[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsDevices : IWindowsDeviceNative
{
    internal static int DeviceInfoDataSize => Marshal.SizeOf<DeviceInfoData>();
    internal static int PropertyKeySize => Marshal.SizeOf<PropertyKey>();
    internal static PropertyKey KeyFor(WindowsDeviceField field) => field switch
    {
        WindowsDeviceField.FriendlyName => new(DeviceProperties, 14),
        WindowsDeviceField.Description => new(DeviceProperties, 2),
        WindowsDeviceField.Class => new(DeviceProperties, 9),
        WindowsDeviceField.Manufacturer => new(DeviceProperties, 13),
        WindowsDeviceField.Service => new(DeviceProperties, 6),
        WindowsDeviceField.DriverInf => new(DriverProperties, 5),
        WindowsDeviceField.DriverVersion => new(DriverProperties, 3),
        WindowsDeviceField.DriverDate => new(DriverProperties, 2),
        WindowsDeviceField.DriverProvider => new(DriverProperties, 9),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    // Use the SDK DEFINE_DEVPROPKEY bytes: its nearby namespace comment
    // omits the leading zero of byte0x0c and is not a valid GUID string.
    private static readonly Guid DriverProperties = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    public IWindowsDeviceSet OpenList(bool presentOnly)
    {
        var handle = SetupDiGetClassDevsW(0, null, 0, 4u | (presentOnly ? 2u : 0u));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new WindowsDeviceNativeException(error);
        }
        return new DeviceSet(handle, null);
    }
    public IWindowsDeviceSet OpenExact(string instanceId)
    {
        WindowsDeviceOperationContract.RequireInstanceId(instanceId);
        var handle = SetupDiCreateDeviceInfoList(0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new WindowsDeviceNativeException(error);
        }
        try
        {
            var data = NewData();
            // Empty IDs are forbidden above: the API otherwise opens the root node.
            if (!SetupDiOpenDeviceInfoW(handle, instanceId, 0, 0, ref data))
                throw new WindowsDeviceNativeException(Marshal.GetLastWin32Error());
            return new DeviceSet(handle, data);
        }
        catch { handle.Dispose(); throw; }
    }
    private static DeviceInfoData NewData() => new() { Size = checked((uint)DeviceInfoDataSize) };

    private sealed class DeviceSet(SafeDeviceInfoSet handle, DeviceInfoData? exact) : IWindowsDeviceSet
    {
        internal SafeDeviceInfoSet Handle { get; } = handle;
        public WindowsDeviceEntry At(uint index)
        {
            RequireOpen();
            if (exact.HasValue) return index == 0 ? new(new DeviceNode(this, exact.Value)) : new(End: true);
            var data = NewData();
            if (SetupDiEnumDeviceInfo(Handle, index, ref data)) return new(new DeviceNode(this, data));
            var code = Marshal.GetLastWin32Error();
            return code == 259 ? new(End: true) : new(Error: code);
        }
        internal void RequireOpen() { if (Handle.IsClosed || Handle.IsInvalid) throw new ObjectDisposedException(nameof(DeviceSet)); }
        public void Dispose()
        {
            Handle.Dispose();
            if (Handle.ReleaseFailed) throw new WindowsDeviceNativeException(Handle.ReleaseError, cleanup: true);
        }
    }

    private sealed class DeviceNode(DeviceSet set, DeviceInfoData data) : IWindowsDeviceNode
    {
        private DeviceInfoData _data = data;
        public Guid ClassGuid => _data.ClassGuid;
        public WindowsDeviceIdentity Identity()
        {
            set.RequireOpen();
            var buffer = new char[WindowsDeviceOperationContract.MaximumInstanceIdCharacters + 1];
            if (!SetupDiGetDeviceInstanceIdW(set.Handle, ref _data, buffer, checked((uint)buffer.Length), out var required))
                return new(null, Marshal.GetLastWin32Error());
            var length = Array.IndexOf(buffer, '\0');
            return length is < 1 or > WindowsDeviceOperationContract.MaximumInstanceIdCharacters || required != length + 1
                ? new(null) : new(new string(buffer, 0, length));
        }
        public WindowsDeviceProperty Property(WindowsDeviceField field, int maximumBytes)
        {
            set.RequireOpen();
            if (maximumBytes is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            var key = KeyFor(field);
            var buffer = new byte[maximumBytes];
            try
            {
                if (!SetupDiGetDevicePropertyW(set.Handle, ref _data, ref key, out var type, buffer,
                    checked((uint)buffer.Length), out var required, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    return new(type, null, error, error == 122 && required > buffer.Length);
                }
                if (required > buffer.Length) return new(type, null, TooLarge: true);
                return new(type, buffer.AsSpan(0, checked((int)required)).ToArray());
            }
            finally { Array.Clear(buffer); }
        }
        public WindowsDeviceStatus Status()
        {
            set.RequireOpen();
            var code = CM_Get_DevNode_Status(out var status, out var problem, _data.DevInst, 0);
            return new(status, problem, code); // CONFIGRET, not GetLastError.
        }
    }

    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDeviceInfoSet() : base(true) { }
        internal bool ReleaseFailed { get; private set; }
        internal int ReleaseError { get; private set; }
        protected override bool ReleaseHandle()
        {
            var ok = SetupDiDestroyDeviceInfoList(handle);
            if (!ok) { ReleaseFailed = true; ReleaseError = Marshal.GetLastWin32Error(); }
            return ok;
        }
    }
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct PropertyKey(Guid FormatId, uint PropertyId);
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData
    { internal uint Size; internal Guid ClassGuid; internal uint DevInst; internal nuint Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeDeviceInfoSet SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeDeviceInfoSet SetupDiCreateDeviceInfoList(nint classGuid, nint parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(SafeDeviceInfoSet set, string instanceId, nint parent, uint flags, ref DeviceInfoData data);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSet set, uint index, ref DeviceInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(SafeDeviceInfoSet set, ref DeviceInfoData data,
        [Out] char[] instanceId, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(SafeDeviceInfoSet set, ref DeviceInfoData data,
        ref PropertyKey key, out uint type, [Out] byte[] bytes, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
