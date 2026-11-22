using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Windows.Registry;

public static class WindowsRegistryOperations
{
    // Constructed by the owning Engine in its admitted execution context.
    // No identity changes, elevation, privileges, remote registry or services.
    [SupportedOSPlatform("windows")]
    public static RegistryOperations Create(IRegistryAdmission? admission = null) => new(new WindowsRegistryNative(), admission);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryNative : IRegistryNative
{
    private const uint QueryValue = 0x0001, SetValueAccess = 0x0002, CreateSubKey = 0x0004, EnumerateSubKeys = 0x0008;
    private const uint OpenLink = 0x0008;
    private const int Success = 0, FileNotFound = 2, PathNotFound = 3, AccessDenied = 5, MoreData = 234, NoMoreItems = 259, KeyDeleted = 1018;

    public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key)
    {
        using var handle = Open(key, QueryValue, missingAllowed: true);
        return handle is null ? null : Info(handle);
    }

    public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes)
    {
        using var handle = OpenRequired(identity.Key, QueryValue);
        // Never allocate according to an untrusted registry-provided size. A
        // concurrently growing value reports a bounded failure, not a retry.
        var bytes = new byte[maxBytes];
        var length = checked((uint)bytes.Length);
        var error = Native.RegQueryValueExW(handle, identity.ValueName, 0, out var type, bytes, ref length);
        if (error == FileNotFound) return null;
        Check(error, "value_read");
        if (length > bytes.Length) throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "native_value_size_limit");
        Array.Resize(ref bytes, checked((int)length));
        return new((RegistryValueType)type, bytes);
    }

    public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars)
    {
        using var handle = OpenRequired(key, kind == RegistryBrowseKind.Keys ? QueryValue | EnumerateSubKeys : QueryValue);
        var name = new StringBuilder(maxNameChars + 1);
        var length = checked((uint)name.Capacity);
        uint? valueType = null;
        int error;
        if (kind == RegistryBrowseKind.Keys)
            error = Native.RegEnumKeyExW(handle, checked((uint)index), name, ref length, 0, 0, 0, 0);
        else
        {
            error = Native.RegEnumValueW(handle, checked((uint)index), name, ref length, 0, out var type, 0, 0);
            valueType = type;
        }
        if (error == NoMoreItems) return null;
        Check(error, "name_enumerate");
        return new(RegistryNativeNameDecoder.Decode(name.ToString(), length, maxNameChars), valueType);
    }

    public bool CreateKey(RegistryKeyIdentity key)
    {
        // Only the direct child is passed to RegCreateKeyEx: no missing parent
        // chain is silently created, and no existing key is reconfigured.
        using var parent = OpenRequired(RegistryValidation.Parent(key), QueryValue | CreateSubKey);
        var error = Native.RegCreateKeyExW(parent, RegistryValidation.Leaf(key), 0, 0, 0, QueryValue | View(key), 0,
            out var created, out var disposition);
        using (created) Check(error, "key_create");
        return disposition == 1; // REG_CREATED_NEW_KEY; otherwise a concurrent existing key won.
    }

    public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data)
    {
        using var key = OpenRequired(identity.Key, QueryValue | SetValueAccess);
        // RegSetValueEx preserves supplied bytes and never expands REG_EXPAND_SZ.
        Check(Native.RegSetValueExW(key, identity.ValueName, 0, (uint)type, data, checked((uint)data.Length)), "value_set");
    }

    public void DeleteValue(RegistryValueIdentity identity)
    {
        using var key = OpenRequired(identity.Key, QueryValue | SetValueAccess);
        Check(Native.RegDeleteValueW(key, identity.ValueName), "value_delete");
    }

    public void DeleteEmptyKey(RegistryKeyIdentity key)
    {
        using (var target = OpenRequired(key, QueryValue))
        {
            var immediatelyBefore = Info(target);
            if (immediatelyBefore.SubKeyCount != 0 || immediatelyBefore.ValueCount != 0)
                throw new RegistryNativeException(RegistryNativeFailure.Conflict, "native_key_not_empty");
        }
        using var parent = OpenRequired(RegistryValidation.Parent(key), QueryValue);
        // RegDeleteKeyEx NEVER recurses. Windows can still remove concurrently
        // added values between our check and this call. Admission must account
        // for that non-atomic race; this contract cannot implement registry CAS.
        // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regdeletekeyexw
        Check(Native.RegDeleteKeyExW(parent, RegistryValidation.Leaf(key), View(key), 0), "key_delete");
    }

    private static RegistryNativeKeyInfo Info(SafeRegistryHandle key)
    {
        Check(Native.RegQueryInfoKeyW(key, 0, 0, 0, out var subKeys, 0, 0, out var values, 0, 0, 0, out var lastWrite), "key_inspect");
        if (subKeys > int.MaxValue || values > int.MaxValue)
            throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "native_key_count_limit");
        return new(lastWrite, (int)subKeys, (int)values);
    }

    private static SafeRegistryHandle OpenRequired(RegistryKeyIdentity key, uint access) => Open(key, access, missingAllowed: false)!;

    private static SafeRegistryHandle? Open(RegistryKeyIdentity key, uint access, bool missingAllowed)
    {
        if (!Environment.Is64BitOperatingSystem && key.View == RegistryViewId.Registry64)
            throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "registry64_view_unavailable");
        SafeRegistryHandle current;
        if (key.Hive == RegistryHiveId.CurrentUser)
        {
            // Resolve HKCU in the actual effective OS security context instead
            // of relying on the cached predefined HKCU handle during impersonation.
            var error = Native.RegOpenCurrentUser(QueryValue | (key.KeyPath.Length == 0 ? access : 0) | View(key), out current);
            if (error != Success) { current.Dispose(); Check(error, "current_user_open"); }
        }
        else
        {
            var predefined = key.Hive == RegistryHiveId.LocalMachine ? unchecked((int)0x80000002) : unchecked((int)0x80000003);
            current = new SafeRegistryHandle((nint)predefined, ownsHandle: false);
        }
        try
        {
            if (key.KeyPath.Length == 0) return current;
            var segments = key.KeyPath.Split('\\');
            for (var index = 0; index < segments.Length; index++)
            {
                var error = Native.RegOpenKeyExW(current, segments[index], OpenLink,
                    QueryValue | (index == segments.Length - 1 ? access : 0) | View(key), out var next);
                if (missingAllowed && error is FileNotFound or PathNotFound or KeyDeleted)
                {
                    next.Dispose();
                    current.Dispose();
                    return null;
                }
                if (error != Success) { next.Dispose(); Check(error, "key_open"); }
                current.Dispose();
                current = next;
                RejectLink(current);
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static void RejectLink(SafeRegistryHandle key)
    {
        // Open one segment at a time with OPEN_LINK so a scoped path cannot
        // silently traverse a registry symbolic link into a different target.
        var length = 0u;
        var error = Native.RegQueryValueExTypeW(key, "SymbolicLinkValue", 0, out var type, 0, ref length);
        if (error is Success or MoreData)
        {
            if (type == 6) throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "registry_link_not_supported");
            return;
        }
        if (error != FileNotFound) Check(error, "link_inspect");
    }

    private static uint View(RegistryKeyIdentity key) => key.View == RegistryViewId.Registry32 ? 0x0200u : 0x0100u;

    private static void Check(int error, string stage)
    {
        if (error == Success) return;
        var failure = error switch
        {
            AccessDenied => RegistryNativeFailure.AccessDenied,
            FileNotFound or PathNotFound or KeyDeleted => RegistryNativeFailure.NotFound,
            MoreData => RegistryNativeFailure.LimitExceeded,
            _ => RegistryNativeFailure.Failed
        };
        // No FormatMessage, caller values or registry contents enter diagnostics.
        throw new RegistryNativeException(failure, "registry_native_" + stage + "_failed", error);
    }

    private static class Native
    {
        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern int RegOpenCurrentUser(uint desired, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, uint desired, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, uint reserved, nint keyClass, uint options,
            uint desired, nint securityAttributes, out SafeRegistryHandle result, out uint disposition);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern int RegQueryInfoKeyW(SafeRegistryHandle key, nint keyClass, nint classLength, nint reserved,
            out uint subKeys, nint maxSubKeyLength, nint maxClassLength, out uint values, nint maxValueNameLength,
            nint maxValueLength, nint securityDescriptorLength, out long lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegQueryValueExW(SafeRegistryHandle key, string name, uint reserved, out uint type, [Out] byte[] data, ref uint dataLength);
        [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegQueryValueExTypeW(SafeRegistryHandle key, string name, uint reserved, out uint type, nint data, ref uint dataLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength,
            nint reserved, nint keyClass, nint classLength, nint lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegEnumValueW(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength,
            nint reserved, out uint type, nint data, nint dataLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint length);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegDeleteValueW(SafeRegistryHandle key, string name);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegDeleteKeyExW(SafeRegistryHandle key, string subKey, uint desired, uint reserved);
    }
}
