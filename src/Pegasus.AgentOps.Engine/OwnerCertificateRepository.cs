using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Certificates;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

// Public DER is live response data, never an encrypted envelope or durable job receipt.
internal sealed record OwnerCertificateEntry(int Version, string OwnerSid, string Reference,
    string CertificateId, string RequestIdentity, byte[] PublicDer, bool ProtectedMaterialPresent);

internal interface IOwnerCertificateRepository : ICertificateKeyResolver
{
    OwnerCertificateEntry Publish(string reference, X509Certificate2 ownedCertificate,
        string requestIdentity, CancellationToken cancellationToken = default);
    OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null);
}

internal sealed class OwnerCertificateRepositoryException(string code, bool published = false,
    bool outcomeUnknown = false, bool postconditionObserved = false, Exception? inner = null) : Exception(code, inner)
{
    internal string Code { get; } = code;
    internal bool Published { get; } = published;
    internal bool OutcomeUnknown { get; } = outcomeUnknown;
    internal bool PostconditionObserved { get; } = postconditionObserved;
}

/// <summary>
/// Create-only owner-local storage. The trusted host supplies exactly its certificates
/// directory. Construction and reads never initialize missing state or repair permissions.
/// Persisted PFX is encrypted; only its random password is wrapped with CurrentUser DPAPI.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OwnerCertificateRepository : IOwnerCertificateRepository
{
    private const int MaximumPfxBytes = 1024 * 1024, MaximumEnvelopeBytes = 2 * 1024 * 1024;
    private const int MaximumDerBytes = 64 * 1024;
    private const string FilePrefix = "certificate-", FileSuffix = ".v1.json", PasswordReference = "owner-certificate-password";
    private readonly string _root, _ownerSid;
    private readonly SecurityIdentifier _user;
    private readonly OwnerHostIdentity _identity;
    private readonly WindowsPathContextResult _rootContext;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };

    // This type never escapes the repository. No serializer used for receipts sees it.
    private sealed record Envelope(int Version, string OwnerSid, string Reference, string CertificateId,
        string RequestIdentity, byte[] PublicDer, byte[]? ProtectedPfx, byte[]? ProtectedPassword);

    internal OwnerCertificateRepository(string root, string ownerSid)
    {
        _user = new SecurityIdentifier(ownerSid);
        if (_user.Value != ownerSid || ownerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") throw Error("certificate_owner_invalid");
        _ownerSid = ownerSid;
        _identity = new OwnerHostIdentity(ownerSid, LocalIdentity.Current);
        RequireOwner();
        _rootContext = new WindowsPathContextNormalizer().Normalize(root);
        _root = Path.TrimEndingDirectorySeparator(_rootContext.CanonicalPath);
        if (_rootContext.Kind != "local" || !string.Equals(_root, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
            _root.Length > 240)
            throw Error("certificate_vault_path_invalid");
        PathNormalizer.RejectAbsoluteReparseComponents(_root);
        if (File.Exists(_root)) throw Error("certificate_vault_path_invalid");
        if (Directory.Exists(_root)) RequireRoot(create: false);
    }

    public OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null)
    {
        RequireReference(reference);
        if (expectedCertificateId is not null) RequireHash(expectedCertificateId);
        lock (_gate)
        {
            RequireRoot(create: false);
            var envelope = ReadEnvelope(EntryPath(reference), reference, expectedCertificateId);
            try { return PublicEntry(envelope); }
            finally { Clear(envelope); }
        }
    }

    public X509Certificate2 Resolve(CertificateKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RequireReference(reference.Reference); RequireHash(reference.ExpectedCertificateId);
        lock (_gate)
        {
            RequireRoot(create: false);
            var envelope = ReadEnvelope(EntryPath(reference.Reference), reference.Reference, reference.ExpectedCertificateId);
            try { return OpenKey(envelope); }
            finally { Clear(envelope); }
        }
    }

    // The caller retains ownership of ownedCertificate. A successful return proves
    // the published envelope reopened; late cancellation cannot erase that effect.
    public OwnerCertificateEntry Publish(string reference, X509Certificate2 ownedCertificate,
        string requestIdentity, CancellationToken cancellationToken = default)
    {
        RequireReference(reference); RequireHash(requestIdentity);
        ArgumentNullException.ThrowIfNull(ownedCertificate);
        lock (_gate)
        {
            RequireOwner(); cancellationToken.ThrowIfCancellationRequested();
            var target = EntryPath(reference);
            if (target.Length + 1 + 32 + 4 > 240) throw Error("certificate_vault_path_invalid");
            RequireRoot(create: true);
            if (File.Exists(target) || Directory.Exists(target)) throw Error("certificate_reference_exists");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var publishAttempted = false;
            var published = false;
            Envelope? envelope = null;
            byte[]? encoded = null;
            FileStream? staging = null;
            CertificateFileIdentity? stagingIdentity = null;
            try
            {
                envelope = Seal(reference, ownedCertificate, requestIdentity);
                cancellationToken.ThrowIfCancellationRequested();
                encoded = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                if (encoded.Length > MaximumEnvelopeBytes) throw Error("certificate_envelope_bound");
                PathNormalizer.RejectAbsoluteReparseComponents(_root);
                staging = new FileInfo(temporary).Create(FileMode.CreateNew,
                    FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                    FileShare.Read, 4096, FileOptions.WriteThrough,
                    CortexUserStateDirectories.CreateWindowsUserFileSecurity(_user));
                stagingIdentity = new CertificateFileIdentity(staging.SafeFileHandle, temporary);
                staging.Write(encoded);
                staging.Flush(flushToDisk: true);
                stagingIdentity.RequireAt(temporary);
                CortexUserStateDirectories.RequireWindowsUserStateAccess(staging.GetAccessControl(), _user, CortexUserStateAccess.Read);
                VerifyStagedBytes(staging, encoded, cancellationToken);
                VerifyReopening(envelope);
                cancellationToken.ThrowIfCancellationRequested();
                RequireRoot(create: false);
                stagingIdentity.RequireAt(temporary);
                publishAttempted = true;
                stagingIdentity.PublishAs(target);
                published = true;
                // The no-write/delete-sharing fence covers validation and the
                // no-replace handle rename. The passive reader opens only after
                // closing our writer, whose access would exclude that reader.
                staging.Dispose();
                staging = null;
                var observed = ReadEnvelope(target, reference, envelope.CertificateId);
                try
                {
                    if (observed.RequestIdentity != requestIdentity) throw Error("certificate_envelope_identity");
                    VerifyReopening(observed);
                    return PublicEntry(observed);
                }
                finally { Clear(observed); }
            }
            catch (Exception error) when (publishAttempted && error is not OutOfMemoryException and not StackOverflowException)
            {
                // An issued rename or post-publication failure must not authorize
                // adoption, replacement, a fresh key, or automatic replay.
                throw new OwnerCertificateRepositoryException("certificate_publication_unknown", published, true, false, error);
            }
            finally
            {
                if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
                if (envelope is not null) Clear(envelope);
                // Only a definitely pre-publication temporary is disposable here.
                // Ambiguous publication retains exact artifacts for owner review.
                if (!publishAttempted && stagingIdentity is not null)
                {
                    try { stagingIdentity.DeleteOnClose(); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    { /* Retain an unproven artifact; never delete another object by pathname. */ }
                }
                staging?.Dispose();
            }
        }
    }

    private static void VerifyStagedBytes(FileStream staging, ReadOnlySpan<byte> expected, CancellationToken token)
    {
        if (RandomAccess.GetLength(staging.SafeFileHandle) != expected.Length) throw Error("certificate_envelope_changed");
        var buffer = new byte[64 * 1024];
        try
        {
            var offset = 0;
            while (offset < expected.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(staging.SafeFileHandle, buffer.AsSpan(0, Math.Min(buffer.Length, expected.Length - offset)), offset);
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read)))
                    throw Error("certificate_envelope_changed");
                offset += read;
            }
            if (RandomAccess.GetLength(staging.SafeFileHandle) != expected.Length) throw Error("certificate_envelope_changed");
            token.ThrowIfCancellationRequested();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private Envelope Seal(string reference, X509Certificate2 certificate, string requestIdentity)
    {
        var der = CertificateOperations.ExportPublic(certificate, PublicCertificateFormat.Der);
        if (der.Length > MaximumDerBytes) throw Error("certificate_public_bound");
        var id = CertificateOperations.Inspect(certificate).CertificateId;
        // Public-only entries use explicit paired JSON nulls, never placeholder
        // passwords/PFX material. Existing private version1 entries are unchanged.
        if (!certificate.HasPrivateKey) return new(1, _ownerSid, reference, id, requestIdentity, der, null, null);
        var randomPassword = RandomNumberGenerator.GetBytes(32);
        byte[]? pfx = null, protectedPassword = null;
        using var output = new MemoryStream();
        try
        {
            using var password = new PasswordResolver(randomPassword);
            CertificateExchange.ExportProtectedPkcs12(new(reference, id), new BorrowedCertificate(certificate),
                PasswordReference, password, output);
            pfx = output.ToArray();
            if (pfx.Length is < 1 or > MaximumPfxBytes) throw Error("certificate_pfx_bound");
            protectedPassword = CortexUserSecretProtection.Protect(randomPassword);
            if (protectedPassword.Length is < 1 or > 4096) throw Error("certificate_password_bound");
            var result = new Envelope(1, _ownerSid, reference, id, requestIdentity, der, pfx, protectedPassword);
            pfx = protectedPassword = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomPassword);
            if (pfx is not null) CryptographicOperations.ZeroMemory(pfx);
            if (protectedPassword is not null) CryptographicOperations.ZeroMemory(protectedPassword);
            if (output.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    private X509Certificate2 OpenKey(Envelope envelope)
    {
        RequireOwner();
        if (envelope.ProtectedPfx is null || envelope.ProtectedPassword is null)
            throw Error("certificate_private_key_required");
        byte[]? password = null;
        try
        {
            password = CortexUserSecretProtection.Unprotect(envelope.ProtectedPassword);
            if (password.Length != 32) throw Error("certificate_password_invalid");
            using var secrets = new PasswordResolver(password);
            using var source = new MemoryStream(envelope.ProtectedPfx, writable: false);
            return CertificateExchange.ImportProtectedPkcs12(source, PasswordReference, secrets, envelope.CertificateId);
        }
        catch (Exception error) when (error is CryptographicException or Win32Exception or CertificateOperationException)
        { throw new OwnerCertificateRepositoryException("certificate_key_unavailable", inner: error); }
        finally { if (password is not null) CryptographicOperations.ZeroMemory(password); }
    }

    private void VerifyReopening(Envelope envelope)
    {
        if (envelope.ProtectedPfx is null) return; // ReadEnvelope already validated exact public DER.
        using var reopened = OpenKey(envelope);
    }

    private Envelope ReadEnvelope(string path, string reference, string? expectedId)
    {
        byte[]? bytes = null;
        Envelope? result = null;
        try
        {
            using var lease = new EnvelopeLease(path, _user);
            bytes = new byte[checked((int)lease.Stream.Length)];
            lease.Stream.ReadExactly(bytes);
            lease.RequireIdentity();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("certificate_envelope_invalid");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string[] fields = ["version", "ownerSid", "reference", "certificateId", "requestIdentity", "publicDer", "protectedPfx", "protectedPassword"];
            foreach (var property in document.RootElement.EnumerateObject())
                if (!fields.Contains(property.Name, StringComparer.Ordinal) || !values.TryAdd(property.Name, property.Value))
                    throw Error("certificate_envelope_invalid");
            if (values.Count != fields.Length || !values["version"].TryGetInt32(out var version) || version != 1 ||
                values["ownerSid"].GetString() != _ownerSid || values["reference"].GetString() != reference)
                throw Error("certificate_envelope_identity");
            var id = values["certificateId"].GetString()!;
            var requestIdentity = values["requestIdentity"].GetString()!;
            RequireHash(id); RequireHash(requestIdentity);
            if (expectedId is not null && id != expectedId) throw Error("certificate_identity_mismatch");
            var publicOnly = values["protectedPfx"].ValueKind == JsonValueKind.Null;
            if (publicOnly != (values["protectedPassword"].ValueKind == JsonValueKind.Null))
                throw Error("certificate_envelope_invalid");
            result = new(1, _ownerSid, reference, id, requestIdentity,
                ReadBytes(values["publicDer"], MaximumDerBytes), publicOnly ? null : ReadBytes(values["protectedPfx"], MaximumPfxBytes),
                publicOnly ? null : ReadBytes(values["protectedPassword"], 4096));
            using var publicCertificate = CertificateExchange.ImportPublic(result.PublicDer, PublicCertificateFormat.Der);
            if (CertificateOperations.Inspect(publicCertificate).CertificateId != id) throw Error("certificate_identity_mismatch");
            lease.RequireIdentity();
            var returned = result; result = null;
            return returned;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or CertificateOperationException or CryptographicException)
        { throw new OwnerCertificateRepositoryException("certificate_envelope_invalid", inner: error); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (result is not null) Clear(result);
        }
    }

    private static byte[] ReadBytes(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetRawText().Length > (maximum + 2) / 3 * 4 + 2)
            throw Error("certificate_envelope_bound");
        var bytes = value.GetBytesFromBase64();
        if (bytes.Length is < 1 || bytes.Length > maximum)
        { CryptographicOperations.ZeroMemory(bytes); throw Error("certificate_envelope_bound"); }
        return bytes;
    }

    private static OwnerCertificateEntry PublicEntry(Envelope value) =>
        new(1, value.OwnerSid, value.Reference, value.CertificateId, value.RequestIdentity, value.PublicDer.ToArray(),
            value.ProtectedPfx is not null && value.ProtectedPassword is not null);
    private static void Clear(Envelope value)
    {
        CryptographicOperations.ZeroMemory(value.PublicDer);
        if (value.ProtectedPfx is not null) CryptographicOperations.ZeroMemory(value.ProtectedPfx);
        if (value.ProtectedPassword is not null) CryptographicOperations.ZeroMemory(value.ProtectedPassword);
    }
    private void RequireOwner() { if (!_identity.IsCurrentOwner()) throw Error("certificate_owner_denied"); }
    private void RequireRoot(bool create)
    {
        RequireOwner();
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(_rootContext);
        if (!Directory.Exists(_root))
        {
            if (!create) throw Error("certificate_vault_missing");
            CortexUserStateDirectories.EnsureCurrentUserDirectory(_root);
        }
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(_rootContext);
        CortexUserStateDirectories.RequireWindowsUserStateAccess(new DirectoryInfo(_root).GetAccessControl(), _user,
            create ? CortexUserStateAccess.Modify : CortexUserStateAccess.Read);
    }
    private string EntryPath(string reference)
    {
        var path = Path.Combine(_root, FilePrefix + reference + FileSuffix);
        if (path.Length > 240) throw Error("certificate_vault_path_invalid");
        return path;
    }
    private static void RequireReference(string reference)
    {
        if (reference is not { Length: >= 1 and <= 64 } || reference[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            reference.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw Error("certificate_reference_invalid");
    }
    private static void RequireHash(string value)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw Error("certificate_identity_invalid");
    }
    private static OwnerCertificateRepositoryException Error(string code) => new(code);

    private sealed class BorrowedCertificate(X509Certificate2 certificate) : ICertificateKeyResolver
    { public X509Certificate2 Resolve(CertificateKeyReference reference) => new(certificate); }
    private sealed class PasswordResolver : ICertificateSecretResolver, IDisposable
    {
        private readonly char[] _password = new char[64];
        internal PasswordResolver(byte[] random)
        {
            if (random.Length != 32 || !Convert.TryToHexString(random, _password, out var written) || written != _password.Length)
                throw Error("certificate_password_invalid");
        }
        public CertificateSecret Resolve(string reference) => reference == PasswordReference
            ? new(_password) : throw Error("certificate_password_invalid");
        public void Dispose() => Array.Clear(_password);
    }

    // No-follow open and ACL admission are performed on the retained object, not
    // on a second pathname lookup. Read sharing alone excludes writers/deletion.
    private sealed class EnvelopeLease : IDisposable
    {
        private readonly string _path;
        private readonly CertificateFileIdentity _identity;
        internal FileStream Stream { get; }
        internal EnvelopeLease(string path, SecurityIdentifier user)
        {
            _path = path;
            PathNormalizer.RejectAbsoluteReparseComponents(path);
            var handle = CreateFileW(path, 0x80000000u, 1, 0, 3, 0x00200000u, 0);
            if (handle.IsInvalid)
            { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("certificate_envelope_open_failed", new Win32Exception(error)); }
            FileStream? stream = null;
            try
            {
                if (!GetAttributeInfo(handle, 9, out var attributes, 8) ||
                    (attributes.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !GetStandardInfo(handle, 1, out var standard, checked((uint)Marshal.SizeOf<StandardInfo>())) ||
                    standard.Links != 1 || standard.DeletePending != 0 || standard.Length is < 1 or > MaximumEnvelopeBytes)
                    throw Error("certificate_envelope_invalid");
                _identity = new CertificateFileIdentity(handle, path);
                Stream = stream = new FileStream(handle, FileAccess.Read);
                CortexUserStateDirectories.RequireWindowsUserStateAccess(Stream.GetAccessControl(), user, CortexUserStateAccess.Read);
                RequireIdentity();
            }
            catch { stream?.Dispose(); handle.Dispose(); throw; }
        }
        internal void RequireIdentity() => _identity.RequireAt(_path);
        public void Dispose() => Stream.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long Allocation, Length; internal uint Links; internal byte DeletePending, Directory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeInfo(SafeFileHandle handle, int kind, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
}
