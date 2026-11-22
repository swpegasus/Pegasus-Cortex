using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Certificates;

// Fixed native backend only. No caller controls a provider, key name, owner,
// descriptor, certificate store, file, private export or native handle.
[SupportedOSPlatform("windows")]
internal sealed class MachineServerCertificateNative : IMachineServerCertificateBackend
{
    internal const uint MachineKeyFlag = 0x20, SilentFlag = 0x40;
    internal const uint OwnerAndDaclFlags = 0x01 | 0x04 | SilentFlag;
    private const int BadKeySet = unchecked((int)0x80090016), KeyExists = unchecked((int)0x8009000F);
    private const string Provider = "Microsoft Software Key Storage Provider";
    private SafeNCryptProviderHandle? _provider;
    private SafeNCryptKeyHandle? _key;
    private RSACng? _rsa;
    private X509Store? _store;
    private X509Certificate2? _prepared;
    private string? _keyName;
    private int _rsaBits;
    private bool _storeIssued, _disposed;
    public bool KeyOwned { get; private set; }

    public void Create(Guid operationId, int rsaBits, Action beforeCreate)
    {
        RequireMachineAdministrator();
        if (_disposed || _key is not null || operationId == Guid.Empty || rsaBits is not (2048 or 3072 or 4096))
            throw Error("machine_server_create_invalid");
        _keyName = KeyName(operationId);
        _rsaBits = rsaBits;
        var status = NCryptOpenStorageProvider(out var provider, Provider, 0);
        if (status != 0) { provider?.Dispose(); throw Error("machine_server_provider_unavailable", status); }
        _provider = provider;
        // No OVERWRITE flag and no prior open/adoption. The original handle is
        // retained across property/finalization failures for exact cleanup.
        beforeCreate();
        status = NCryptCreatePersistedKey(provider, out var key, "RSA", _keyName, 0, MachineKeyFlag);
        if (status != 0)
        {
            key?.Dispose();
            throw Error(status == KeyExists ? "machine_server_key_exists" : "machine_server_key_creation_failed",
                status, unknown: status != KeyExists);
        }
        _key = key;
        KeyOwned = true;
    }

    public void ConfigureAndFinalize()
    {
        RequireMachineAdministrator();
        var key = Key();
        SetDword(key, "Length", _rsaBits);
        SetDword(key, "Export Policy", 0); // No export or archival flags.
        SetDword(key, "Key Usage", 3); // Decryption + signing; RSA server key usage.
        var descriptor = MachineServerCertificatePolicy.SecurityDescriptor();
        // OWNER/DACL selectors are mandatory for this built-in property.
        // Persist/CustomProperty are NOT substituted for security selectors.
        Check(NCryptSetProperty(key, "Security Descr", descriptor, descriptor.Length, OwnerAndDaclFlags),
            "machine_server_key_security_failed");
        Check(NCryptFinalizeKey(key, SilentFlag), "machine_server_key_finalization_failed");
        using var managed = CngKey.Open(key, CngKeyHandleOpenOptions.None);
        _rsa = new RSACng(managed);
    }

    public MachineServerCertificateKeyObservation ObserveKey(bool reopen)
    {
        RequireMachineAdministrator();
        if (!reopen) return Observe(Key());
        var status = NCryptOpenKey(ProviderHandle(), out var observed, Name(), 0, MachineKeyFlag | SilentFlag);
        try
        {
            Check(status, "machine_server_key_reopen_failed");
            return Observe(observed);
        }
        finally { observed?.Dispose(); }
    }

    public byte[] CreateSigningRequest(CertificateSpec specification)
    {
        RequireMachineAdministrator();
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(specification.CommonName);
        var request = new CertificateRequest(subject.Build(), _rsa ?? throw Error("machine_server_key_unavailable"),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Parent reconstructs all certificate policy; this CSR carries no extensions.
        return request.CreateSigningRequest();
    }

    public void PrepareBinding(X509Certificate2 publicLeaf, string certificateId)
    {
        RequireMachineAdministrator();
        if (_storeIssued || _prepared is not null || _store is not null) throw Error("machine_server_binding_consumed");
        var associated = publicLeaf.CopyWithPrivateKey(_rsa ?? throw Error("machine_server_key_unavailable"));
        var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            using var existing = WindowsCertificateServerBinding.ReadExact(store, certificateId);
            if (existing is not null) throw Error("machine_server_binding_exists");
            _store = store;
            _prepared = associated;
        }
        catch { associated.Dispose(); store.Dispose(); throw; }
    }

    public void PublishBinding(Action beforeStore)
    {
        RequireMachineAdministrator();
        if (_storeIssued || _store is null || _prepared is null) throw Error("machine_server_binding_unavailable");
        beforeStore();
        _storeIssued = true;
        _store.Add(_prepared);
    }

    public X509Certificate2 ReadBoundCertificate(string certificateId)
    {
        RequireMachineAdministrator();
        if (!_storeIssued || _store is null) throw Error("machine_server_binding_unavailable");
        // Release the adding certificate before proving the persistent association
        // from a newly opened store context.
        _prepared?.Dispose(); _prepared = null;
        _store.Close();
        _store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
        return WindowsCertificateServerBinding.ReadExact(_store, certificateId) ??
            throw Error("machine_server_binding_not_observed", unknown: true);
    }

    public MachineServerCertificateKeyObservation ObserveBoundKey(X509Certificate2 certificate)
    {
        RequireMachineAdministrator();
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not RSACng cng || cng.Key.Provider?.Provider != Provider ||
            cng.Key.KeyName != Name() || !cng.Key.IsMachineKey)
            throw Error("machine_server_bound_key_changed", unknown: true);
        using var handle = cng.Key.Handle; // Separately owned duplicate.
        return Observe(handle);
    }

    public void DeleteUnboundKey()
    {
        RequireMachineAdministrator();
        if (_storeIssued || !KeyOwned) throw Error("machine_server_cleanup_forbidden");
        _prepared?.Dispose(); _prepared = null;
        _store?.Dispose(); _store = null;
        _rsa?.Dispose(); _rsa = null;
        var key = Key();
        var status = NCryptDeleteKey(key, SilentFlag);
        if (status != 0) throw Error("machine_server_cleanup_failed", status, unknown: true);
        // Successful NCryptDeleteKey has already freed this native handle.
        // Invalidating it prevents SafeHandle from calling NCryptFreeObject again.
        key.SetHandleAsInvalid();
        key.Dispose();
        _key = null;
        KeyOwned = false;
        status = NCryptOpenKey(ProviderHandle(), out var absent, Name(), 0, MachineKeyFlag | SilentFlag);
        try
        {
            // This is the same exact-key absence status used by CngKey.Exists.
            // Access/provider failures are not reclassified as absence.
            if (status != BadKeySet) throw Error("machine_server_cleanup_unobserved", status, unknown: true);
        }
        finally { absent?.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prepared?.Dispose(); _prepared = null;
        _store?.Dispose(); _store = null;
        _rsa?.Dispose(); _rsa = null;
        _key?.Dispose(); _key = null;
        _provider?.Dispose(); _provider = null;
        // Deliberately no Delete. Persisted keys survive disposal, especially
        // after store entry or any uncertain publication result.
        KeyOwned = false;
    }

    internal static string KeyName(Guid operationId) => operationId != Guid.Empty
        ? "Pegasus.Cortex.McpTls." + operationId.ToString("N") : throw Error("machine_server_create_invalid");

    private static MachineServerCertificateKeyObservation Observe(SafeNCryptKeyHandle key)
    {
        if (ReadString(key, "Algorithm Group") != "RSA") throw Error("machine_server_key_policy_invalid");
        using var managed = CngKey.Open(key, CngKeyHandleOpenOptions.None);
        using var rsa = new RSACng(managed);
        return new((ReadDword(key, "Key Type") & (int)MachineKeyFlag) != 0,
            ReadDword(key, "Export Policy"), ReadDword(key, "Length"), ReadDword(key, "Key Usage"),
            rsa.ExportSubjectPublicKeyInfo(), ReadProperty(key, "Security Descr", OwnerAndDaclFlags));
    }

    private static void SetDword(SafeNCryptKeyHandle key, string property, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        Check(NCryptSetProperty(key, property, bytes, bytes.Length, SilentFlag), "machine_server_key_property_failed");
    }

    private static int ReadDword(SafeNCryptKeyHandle key, string property)
    {
        var bytes = ReadProperty(key, property, SilentFlag);
        if (bytes.Length != sizeof(int)) throw Error("machine_server_key_property_invalid");
        return BitConverter.ToInt32(bytes);
    }

    private static string ReadString(SafeNCryptKeyHandle key, string property)
    {
        var bytes = ReadProperty(key, property, SilentFlag);
        if (bytes.Length is < 2 or > 1024 || bytes.Length % 2 != 0 ||
            bytes[^1] != 0 || bytes[^2] != 0) throw Error("machine_server_key_property_invalid");
        return Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
    }

    private static byte[] ReadProperty(SafeNCryptKeyHandle key, string property, uint flags)
    {
        Check(NCryptGetProperty(key, property, null, 0, out var required, flags), "machine_server_key_property_failed");
        if (required is < 1 or > 65536) throw Error("machine_server_key_property_bound");
        var bytes = new byte[required];
        Check(NCryptGetProperty(key, property, bytes, bytes.Length, out var written, flags), "machine_server_key_property_failed");
        if (written != bytes.Length) throw Error("machine_server_key_property_changed");
        return bytes;
    }

    private SafeNCryptKeyHandle Key() => !_disposed && _key is { IsInvalid: false, IsClosed: false } key
        ? key : throw Error("machine_server_key_unavailable");
    private SafeNCryptProviderHandle ProviderHandle() => !_disposed && _provider is { IsInvalid: false, IsClosed: false } provider
        ? provider : throw Error("machine_server_provider_unavailable");
    private string Name() => _keyName ?? throw Error("machine_server_key_unavailable");
    private static void Check(int status, string code)
    { if (status != 0) throw Error(code, status); }
    private static MachineServerCertificateBackendException Error(string code, int? status = null, bool unknown = false) =>
        new(code, status, unknown);

    internal static void RequireMachineAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw Error("machine_server_administration_denied");
        _ = GetTokenInformation(identity.AccessToken, 2, IntPtr.Zero, 0, out var size);
        if (size is < 4 or > 65536) throw Error("machine_server_administration_denied");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 2, buffer, size, out var written) || written > size)
                throw Error("machine_server_administration_denied");
            var count = Marshal.ReadInt32(buffer);
            var stride = Marshal.SizeOf<SidAndAttributes>();
            var offset = (sizeof(int) + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
            if (count is < 1 or > 1024 || offset + (long)count * stride > written)
                throw Error("machine_server_administration_denied");
            for (var index = 0; index < count; index++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, offset + index * stride));
                if (new SecurityIdentifier(group.Sid).Value == "S-1-5-32-544" &&
                    (group.Attributes & 0x0C) == 0x0C && (group.Attributes & 0x10) == 0)
                    return; // Enabled, owner-eligible BA; never deny-only.
            }
            throw Error("machine_server_owner_not_eligible");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int returnLength);

    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string name, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptCreatePersistedKey(SafeNCryptProviderHandle provider, out SafeNCryptKeyHandle key,
        string algorithm, string name, uint legacyKeySpec, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptOpenKey(SafeNCryptProviderHandle provider, out SafeNCryptKeyHandle key,
        string name, uint legacyKeySpec, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptSetProperty(SafeNCryptKeyHandle key, string property, byte[] input, int size, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptGetProperty(SafeNCryptKeyHandle key, string property,
        [Out] byte[]? output, int size, out int resultSize, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptFinalizeKey(SafeNCryptKeyHandle key, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptDeleteKey(SafeNCryptKeyHandle key, uint flags);
}
