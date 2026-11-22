namespace Pegasus.AgentOps.Windows.Registry;

// V1 is local-only. Merged HKCR, remote hives, hive loading, ACLs and recursive
// deletion are deliberately outside this contract.
public enum RegistryHiveId { CurrentUser = 1, LocalMachine = 2, Users = 3 }
public enum RegistryViewId { Registry32 = 32, Registry64 = 64 }
public enum RegistryBrowseKind { Keys = 1, Values = 2 }
public enum RegistryOperation { InspectKey = 1, Browse = 2, ReadValue = 3, CreateKey = 4, SetValue = 5, DeleteValue = 6, DeleteEmptyKey = 7 }
public enum RegistryValueType : uint { None = 0, String = 1, ExpandString = 2, Binary = 3, DWord = 4, DWordBigEndian = 5, MultiString = 7, QWord = 11 }
public enum RegistryStatus { Ok, InvalidRequest, Denied, NotFound, Stale, PreconditionFailed, LimitExceeded, Unsupported, Cancelled, Failed, Uncertain }

public sealed record RegistryKeyIdentity(RegistryHiveId Hive, RegistryViewId View, string KeyPath);

// Empty ValueName explicitly identifies the unnamed/default value. No aliases
// such as "(Default)" are interpreted. Key/value names are never trimmed.
public sealed record RegistryValueIdentity(RegistryKeyIdentity Key, string ValueName);

// Raw bytes preserve Windows kinds, UTF-16, terminators, embedded NULs and
// unexpanded REG_EXPAND_SZ exactly. The owning transport must not log this body.
public sealed class RegistryValueData(RegistryValueType type, string base64)
{
    public RegistryValueType Type { get; } = type;
    public string Base64 { get; } = base64;
    public override string ToString() => $"RegistryValueData(Type={Type}, Content=redacted)";
}

public sealed record RegistryKeySnapshot(RegistryKeyIdentity Key, string Generation, int SubKeyCount, int ValueCount);
public sealed record RegistryValueSnapshot(RegistryValueIdentity Identity, RegistryKeySnapshot Key, RegistryValueData? Value, string? Digest)
{
    public bool Exists => Value is not null;
    public override string ToString() => $"RegistryValueSnapshot(Exists={Exists}, Content=redacted)";
}

public sealed record RegistryBrowseEntry(string Name, uint? NativeValueType);

// A cursor is a bounded, untrusted position, never an authorization token or
// snapshot. Generation is derived from Windows last-write metadata and counts;
// it detects observed changes but is neither a durable revision nor proof that
// no writer changed and restored state between observations.
public sealed record RegistryBrowseCursor(int Version, string KeyFingerprint, RegistryBrowseKind Kind, string Generation, int NextIndex);
public sealed record RegistryBrowseRequest(RegistryKeyIdentity Key, RegistryBrowseKind Kind, int Limit = 64, RegistryBrowseCursor? Cursor = null);
public sealed record RegistryBrowsePage(RegistryKeySnapshot Key, RegistryBrowseKind Kind, IReadOnlyList<RegistryBrowseEntry> Entries,
    bool HasMore, RegistryBrowseCursor? Continuation);

// A null digest means the exact value must be absent within an existing key.
// DeleteValue requires a non-null digest. Key generations come from InspectKey,
// Browse or ReadValue; they cannot be bypassed with an overwrite/force flag.
public sealed record RegistryValuePreimage(string KeyGeneration, string? ValueDigest);
public sealed record RegistryCreateKeyRequest(RegistryKeyIdentity Key, string ParentGeneration);
public sealed record RegistrySetValueRequest(RegistryValueIdentity Identity, RegistryValueData Value, RegistryValuePreimage Preimage);
public sealed record RegistryDeleteValueRequest(RegistryValueIdentity Identity, RegistryValuePreimage Preimage);
public sealed record RegistryDeleteEmptyKeyRequest(RegistryKeyIdentity Key, string KeyGeneration);

// Success means the API returned success and the requested postcondition was
// subsequently observed. It NEVER proves CAS, exclusive access, attribution,
// persistence after power loss, or absence of a concurrent registry writer.
// Once invocation starts, any failed verification is Uncertain; never replay it
// automatically. No value bodies or history are retained in mutation results.
public sealed record RegistryMutationObservation(RegistryOperation Operation, bool WriteInvoked, bool PostconditionObserved,
    string? ObservedGeneration)
{
    public bool AtomicCompareAndSwap => false;
}
public sealed record RegistryResult<T>(RegistryStatus Status, string Code, T? Value = default, int? NativeError = null)
{
    public bool IsSuccess => Status == RegistryStatus.Ok;
}

// The Engine constructs this boundary from its authenticated execution context
// and policy. There are no caller-provided admin flags, credentials or grants.
// Authorization must cover the full target (and parent for Create/DeleteKey).
public sealed record RegistryAccessRequest(int ContractVersion, RegistryOperation Operation, RegistryKeyIdentity Key,
    string? ValueName = null, RegistryKeyIdentity? Parent = null);
public interface IRegistryAdmission
{
    bool IsAllowed(RegistryAccessRequest request);
}

public sealed class DenyRegistryAdmission : IRegistryAdmission
{
    public static DenyRegistryAdmission Instance { get; } = new();
    private DenyRegistryAdmission() { }
    public bool IsAllowed(RegistryAccessRequest request) => false;
}

public static class RegistryLimits
{
    public const int ContractVersion = 1;
    public const int MaxKeyPathChars = 1024;
    public const int MaxKeyDepth = 32;
    public const int MaxKeySegmentChars = 255;
    public const int MaxValueNameChars = 512;
    public const int MaxValueBytes = 65_536;
    public const int MaxPageEntries = 128;
    public const int MaxPageNameChars = 16_384;
    public const int MaxCursorIndex = 1_000_000;
}
