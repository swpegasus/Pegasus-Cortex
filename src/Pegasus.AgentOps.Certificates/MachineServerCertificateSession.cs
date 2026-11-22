using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.AgentOps.Certificates;

/// <summary>Public outcomes only; no key-container, provider handle or descriptor is exposed.</summary>
public sealed record MachineServerCertificateProgress(string Phase, bool KeyCreationIssued, bool KeyFinalized,
    bool SecurityObserved, bool StoreActionIssued, bool PostconditionObserved, bool CleanupIssued,
    bool CleanupObserved, bool OutcomeUnknown, string? CertificateId = null, string? Code = null);

public sealed class MachineServerCertificateSessionException(string code, MachineServerCertificateProgress progress,
    int? nativeError = null) : Exception(code)
{
    public string Code { get; } = code;
    public MachineServerCertificateProgress Progress { get; } = progress;
    public int? NativeError { get; } = nativeError;
}

/// <summary>
/// Owns one fresh machine RSA key for an already admitted fixed elevated helper.
/// It is not an enrollment, authentication or elevation service. The parent signs
/// its public CSR using the ordinary owner's authority. Dispose closes handles only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineServerCertificateSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CertificateSpec _specification;
    private readonly ServerCertificateNames _names;
    private readonly Action _requireMachineAdministration;
    private readonly IMachineServerCertificateBackend _backend;
    private byte[]? _signingRequest;
    private byte[]? _publicKeyInfo;
    private bool _bindingAttempted, _disposed;
    private MachineServerCertificateProgress _progress = new("created", false, false, false, false, false, false, false, false);

    private MachineServerCertificateSession(CertificateSpec specification, ServerCertificateNames names,
        Action requireMachineAdministration, IMachineServerCertificateBackend backend)
    {
        _specification = specification;
        _names = names;
        _requireMachineAdministration = requireMachineAdministration;
        _backend = backend;
    }

    /// <summary>
    /// Creates, finalizes and observes one fixed Software KSP machine key. The
    /// nonempty GUID supplies its internal no-overwrite name, never a selector.
    /// The product supplies and retains the original operation identity/admission.
    /// Failed creation attempts report cleanup/uncertainty before closing handles.
    /// </summary>
    public static MachineServerCertificateSession Create(Guid operationId, CertificateSpec specification,
        ServerCertificateNames names, Action requireMachineAdministration, CancellationToken cancellationToken = default)
    {
        ValidateInput(operationId, specification, names, requireMachineAdministration);
        return CreateCore(operationId, specification, names, requireMachineAdministration,
            new MachineServerCertificateNative(), cancellationToken);
    }

    // The injected seam is internal to the toolkit's owned deterministic fixtures.
    internal static MachineServerCertificateSession CreateCore(Guid operationId, CertificateSpec specification,
        ServerCertificateNames names, Action requireMachineAdministration, IMachineServerCertificateBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ValidateInput(operationId, specification, names, requireMachineAdministration);
        var session = new MachineServerCertificateSession(specification, names, requireMachineAdministration, backend);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            requireMachineAdministration();
            session._progress = session._progress with { Phase = "key_creation" };
            backend.Create(operationId, specification.RsaBits,
                () => session._progress = session._progress with { KeyCreationIssued = true });
            cancellationToken.ThrowIfCancellationRequested();
            requireMachineAdministration();
            session._progress = session._progress with { Phase = "key_finalization" };
            backend.ConfigureAndFinalize();
            session._progress = session._progress with { KeyFinalized = true, Phase = "security_validation" };
            var held = backend.ObserveKey(reopen: false);
            MachineServerCertificatePolicy.RequireKey(held, specification.RsaBits);
            var reopened = backend.ObserveKey(reopen: true);
            MachineServerCertificatePolicy.RequireKey(reopened, specification.RsaBits);
            if (!held.PublicKeyInfo.AsSpan().SequenceEqual(reopened.PublicKeyInfo))
                throw new CertificateOperationException("machine_server_key_changed");
            session._publicKeyInfo = (byte[])held.PublicKeyInfo.Clone();
            session._progress = session._progress with { SecurityObserved = true };
            cancellationToken.ThrowIfCancellationRequested();
            requireMachineAdministration();
            var csr = backend.CreateSigningRequest(specification);
            if (csr.Length is < 1 or > 16 * 1024) throw new CertificateOperationException("server_csr_bound");
            var loaded = CertificateRequest.LoadSigningRequest(csr.AsSpan(), HashAlgorithmName.SHA256,
                out var consumed, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            if (consumed != csr.Length || !loaded.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(session._publicKeyInfo))
                throw new CertificateOperationException("machine_server_csr_changed");
            session._signingRequest = (byte[])csr.Clone();
            session._progress = session._progress with { Phase = "csr_ready" };
            return session;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var code = Code(error);
            session._progress = session._progress with { Phase = "failed", Code = code,
                OutcomeUnknown = error is MachineServerCertificateBackendException { OutcomeUnknown: true } };
            if (backend.KeyOwned) session.CleanupCore();
            var progress = session._progress;
            session.Dispose();
            throw new MachineServerCertificateSessionException(code, progress, NativeError(error));
        }
    }

    public MachineServerCertificateProgress Progress { get { lock (_gate) return _progress; } }

    /// <summary>Returns a defensive copy of the public PKCS#10 DER; no key or native selector.</summary>
    public byte[] GetSigningRequest()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _signingRequest is not null ? (byte[])_signingRequest.Clone() :
                throw Failure("machine_server_csr_unavailable");
        }
    }

    /// <summary>
    /// Consumes one signed-leaf attempt. Admission completes before store entry.
    /// StoreActionIssued is latched before Add and permanently prohibits key deletion,
    /// including after exceptions, cancellation or lost postcondition evidence.
    /// </summary>
    public ServerCertificateBindingResult BindSignedLeaf(ReadOnlySpan<byte> publicLeafDer, string expectedLeafCertificateId,
        ReadOnlySpan<byte> selectedAuthorityDer, string expectedAuthorityCertificateId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_bindingAttempted || _signingRequest is null || _progress.CleanupIssued)
                throw Failure("machine_server_binding_consumed");
            _bindingAttempted = true;
            _progress = _progress with { Phase = "binding_validation" };
            try
            {
                _requireMachineAdministration();
                using var leaf = PublicCertificate(publicLeafDer, expectedLeafCertificateId);
                using var authority = PublicCertificate(selectedAuthorityDer, expectedAuthorityCertificateId);
                var selected = ServerCertificateMaterial.Require(leaf, expectedLeafCertificateId, now, privateKey: false);
                if (leaf.GetNameInfo(X509NameType.SimpleName, false) != _specification.CommonName ||
                    new DateTimeOffset(leaf.NotBefore.ToUniversalTime()) != _specification.NotBefore ||
                    new DateTimeOffset(leaf.NotAfter.ToUniversalTime()) != _specification.NotAfter ||
                    !selected.DnsNames.SequenceEqual(_names.DnsNames) || !selected.IpAddresses.SequenceEqual(_names.IpAddresses) ||
                    !leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(_publicKeyInfo))
                    throw new CertificateOperationException("machine_server_leaf_changed");
                if (!CertificateOperations.Inspect(authority).IsAuthority ||
                    !CertificateChainOperations.VerifyServerAuthentication(leaf, expectedLeafCertificateId,
                        new(CertificateTrustSelection.CustomRoots, CertificateRevocationSelection.NotChecked,
                            now, [authority.RawData])).Trusted)
                    throw new CertificateOperationException("machine_server_authority_mismatch");
                var held = _backend.ObserveKey(reopen: false);
                MachineServerCertificatePolicy.RequireKey(held, _specification.RsaBits);
                if (!held.PublicKeyInfo.AsSpan().SequenceEqual(_publicKeyInfo))
                    throw new CertificateOperationException("machine_server_key_changed");
                cancellationToken.ThrowIfCancellationRequested();
                _requireMachineAdministration();
                // The backend prepares the exact key association and opens the fixed
                // store without publishing. All public material was checked above.
                _backend.PrepareBinding(leaf, expectedLeafCertificateId);
                cancellationToken.ThrowIfCancellationRequested();
                _requireMachineAdministration();
                _progress = _progress with { Phase = "store_publication", CertificateId = expectedLeafCertificateId };
                _backend.PublishBinding(() => _progress = _progress with { StoreActionIssued = true });
                // Late cancellation cannot erase an issued store mutation or skip
                // its postcondition. Only the original operation can reconcile it.
                using var observed = _backend.ReadBoundCertificate(expectedLeafCertificateId);
                _ = ServerCertificateMaterial.Require(observed, expectedLeafCertificateId, now, privateKey: true);
                var observedKey = _backend.ObserveBoundKey(observed);
                MachineServerCertificatePolicy.RequireKey(observedKey, _specification.RsaBits);
                if (!observedKey.PublicKeyInfo.AsSpan().SequenceEqual(_publicKeyInfo))
                    throw new CertificateOperationException("machine_server_key_changed");
                _requireMachineAdministration();
                ServerCertificateMaterial.ProvePrivateKey(observed);
                _progress = _progress with { Phase = "bound", PostconditionObserved = true, OutcomeUnknown = false };
                return new("bound", expectedLeafCertificateId, true, true, true, false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                _progress = _progress with { Phase = _progress.StoreActionIssued ? "unknown" : "failed",
                    OutcomeUnknown = _progress.StoreActionIssued, Code = Code(error) };
                throw new MachineServerCertificateSessionException(Code(error), _progress, NativeError(error));
            }
        }
    }

    /// <summary>Explicit deletion of this session's original unbound key only. Never deletes after store entry.</summary>
    public MachineServerCertificateProgress DeleteUnbound(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_progress.StoreActionIssued) throw Failure("machine_server_cleanup_forbidden");
            if (_progress.CleanupObserved) return _progress;
            if (_progress.CleanupIssued || !_backend.KeyOwned) throw Failure("machine_server_cleanup_unavailable");
            CleanupCore();
            return _progress;
        }
    }

    private void CleanupCore()
    {
        try
        {
            _requireMachineAdministration();
            _progress = _progress with { Phase = "cleanup", CleanupIssued = true };
            _backend.DeleteUnboundKey();
            _progress = _progress with { Phase = "cleaned", CleanupObserved = true, OutcomeUnknown = false };
            _signingRequest = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _progress = _progress with { Phase = "unknown", OutcomeUnknown = true, Code = Code(error) };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _backend.Dispose();
            _signingRequest = null;
            _publicKeyInfo = null;
        }
    }

    private MachineServerCertificateSessionException Failure(string code) => new(code, _progress);
    private static string Code(Exception error)
    {
        var code = error switch
        {
            MachineServerCertificateBackendException native => native.Code,
            CertificateOperationException certificate => certificate.Code,
            OperationCanceledException => "canceled",
            UnauthorizedAccessException => "machine_server_administration_denied",
            _ => "machine_server_failed"
        };
        return code is { Length: >= 1 and <= 96 } &&
            code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? code : "machine_server_failed";
    }
    private static int? NativeError(Exception error) =>
        error is MachineServerCertificateBackendException native ? native.NativeError : null;

    private static X509Certificate2 PublicCertificate(ReadOnlySpan<byte> der, string id)
    {
        CertificateMaterial.RequireId(id);
        if (der.Length is < 1 or > 65536) throw new CertificateOperationException("certificate_input_bound");
        var certificate = CertificateExchange.ImportPublic(der, PublicCertificateFormat.Der);
        try { CertificateMaterial.RequireIdentity(certificate, id); return certificate; }
        catch { certificate.Dispose(); throw; }
    }

    private static void ValidateInput(Guid operationId, CertificateSpec specification, ServerCertificateNames names, Action admission)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(admission);
        if (operationId == Guid.Empty || specification.RsaBits is not (2048 or 3072 or 4096) ||
            string.IsNullOrWhiteSpace(specification.CommonName) || specification.CommonName.Length > 128 ||
            specification.CommonName != specification.CommonName.Trim() || specification.CommonName.Any(char.IsControl) ||
            specification.NotBefore.Offset != TimeSpan.Zero || specification.NotAfter.Offset != TimeSpan.Zero ||
            specification.NotBefore.Ticks % TimeSpan.TicksPerSecond != 0 || specification.NotAfter.Ticks % TimeSpan.TicksPerSecond != 0 ||
            specification.NotAfter <= specification.NotBefore || specification.NotAfter - specification.NotBefore > TimeSpan.FromDays(366))
            throw new CertificateOperationException("machine_server_specification_invalid");
    }
}

internal sealed record MachineServerCertificateKeyObservation(bool MachineKey, int ExportPolicy, int RsaBits,
    int KeyUsage, byte[] PublicKeyInfo, byte[] SecurityDescriptor);

internal interface IMachineServerCertificateBackend : IDisposable
{
    bool KeyOwned { get; }
    void Create(Guid operationId, int rsaBits, Action beforeCreate);
    void ConfigureAndFinalize();
    MachineServerCertificateKeyObservation ObserveKey(bool reopen);
    byte[] CreateSigningRequest(CertificateSpec specification);
    void PrepareBinding(X509Certificate2 publicLeaf, string certificateId);
    void PublishBinding(Action beforeStore);
    X509Certificate2 ReadBoundCertificate(string certificateId);
    MachineServerCertificateKeyObservation ObserveBoundKey(X509Certificate2 certificate);
    void DeleteUnboundKey();
}

internal sealed class MachineServerCertificateBackendException(string code, int? nativeError = null,
    bool outcomeUnknown = false) : Exception(code)
{
    internal string Code { get; } = code;
    internal int? NativeError { get; } = nativeError;
    internal bool OutcomeUnknown { get; } = outcomeUnknown;
}

[SupportedOSPlatform("windows")]
internal static class MachineServerCertificatePolicy
{
    private const string SystemSid = "S-1-5-18", AdministratorsSid = "S-1-5-32-544";
    private const int GenericAll = 0x10000000;
    private const int SoftwareKspFullControl = unchecked((int)0xd01f01ff);
    internal static byte[] SecurityDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    internal static void RequireKey(MachineServerCertificateKeyObservation value, int rsaBits)
    {
        if (!value.MachineKey || value.ExportPolicy != 0 || value.RsaBits != rsaBits || value.KeyUsage != 3 ||
            value.PublicKeyInfo.Length is < 1 or > 2048 || value.SecurityDescriptor.Length is < 1 or > 65536)
            throw new CertificateOperationException("machine_server_key_policy_invalid");
        RawSecurityDescriptor descriptor;
        try { descriptor = new RawSecurityDescriptor(value.SecurityDescriptor, 0); }
        catch (ArgumentException) { throw new CertificateOperationException("machine_server_key_security_invalid"); }
        if (descriptor.Owner?.Value != AdministratorsSid ||
            (descriptor.ControlFlags & (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected)) !=
                (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is not { Count: 2 } acl)
            throw new CertificateOperationException("machine_server_key_security_invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The fixed Software KSP's held/reopened readback was 0xd01f01ff.
        // It retains GENERIC_ALL: the other bits grant no additional authority
        // beyond the intended full control for these same two principals. Admit
        // only the literal input and this exact observed encoding; do not infer
        // a general object-specific mapping or accept arbitrary additional bits.
        foreach (GenericAce entry in acl)
            if (entry is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, AceFlags: AceFlags.None,
                    AccessMask: GenericAll or SoftwareKspFullControl, IsCallback: false, OpaqueLength: 0 } allow ||
                allow.SecurityIdentifier.Value is not (SystemSid or AdministratorsSid) || !seen.Add(allow.SecurityIdentifier.Value))
                throw new CertificateOperationException("machine_server_key_security_invalid");
    }
}
