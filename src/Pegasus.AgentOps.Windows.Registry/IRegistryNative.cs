namespace Pegasus.AgentOps.Windows.Registry;

// This trusted injectable boundary performs no admission itself. Production
// composition must expose RegistryOperations, never this adapter to callers.
// Each method must be bounded, close its handles, and reject registry links.
public interface IRegistryNative
{
    RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key);
    RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes);
    RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars);
    bool CreateKey(RegistryKeyIdentity key); // true only if THIS call created it
    void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data);
    void DeleteValue(RegistryValueIdentity identity);
    void DeleteEmptyKey(RegistryKeyIdentity key);
}

public sealed record RegistryNativeKeyInfo(long LastWriteFileTime, int SubKeyCount, int ValueCount);
public sealed record RegistryNativeValue(RegistryValueType Type, byte[] Data);
public enum RegistryNativeFailure { AccessDenied, NotFound, LimitExceeded, Unsupported, Conflict, Failed }
public sealed class RegistryNativeException(RegistryNativeFailure failure, string code, int? nativeError = null) : Exception(code)
{
    public RegistryNativeFailure Failure { get; } = failure;
    public string Code { get; } = code;
    public int? NativeError { get; } = nativeError;
}
