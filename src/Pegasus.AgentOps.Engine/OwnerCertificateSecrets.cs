using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Pegasus.AgentOps.Certificates;

namespace Pegasus.AgentOps.Engine;

// Trusted native-read seam. Allocation owns Blob until Free; neither this
// structure nor the native reader is a public credential-management interface.
internal readonly record struct OwnerCertificateSecretRead(nint Allocation, uint Type, nint Blob, uint BlobBytes, int Error)
{
    public override string ToString() => "OwnerCertificateSecretRead(redacted)";
}
internal interface IOwnerCertificateSecretReader
{
    OwnerCertificateSecretRead Read(string fixedTarget);
    void Free(nint allocation);
}

/// <summary>
/// The existing certificate resolver defaults to its original fixed CurrentUser
/// namespace. The internal password reader also serves the one fixed SMB purpose;
/// neither path enumerates, substitutes identities or returns an immutable secret.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OwnerCertificateSecrets : ICertificateSecretResolver
{
    private const uint MaximumPasswordBytes = 2048;
    private readonly OwnerHostIdentity _identity;
    private readonly IOwnerCertificateSecretReader _reader;
    private readonly OwnerPasswordPurpose _purpose;

    internal OwnerCertificateSecrets(string ownerSid) : this(ownerSid, LocalIdentity.Current, new OwnerCertificateSecretNative()) { }
    internal OwnerCertificateSecrets(string ownerSid, Func<LocalPeer> current, IOwnerCertificateSecretReader reader)
        : this(ownerSid, current, reader, OwnerPasswordPurpose.CertificatePassword) { }
    internal OwnerCertificateSecrets(string ownerSid, Func<LocalPeer> current, IOwnerCertificateSecretReader reader, OwnerPasswordPurpose purpose)
    {
        OwnerPasswordTargets.RequirePurpose(purpose);
        _purpose = purpose;
        _identity = new OwnerHostIdentity(ownerSid, current);
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public CertificateSecret Resolve(string reference)
    {
        using var password = ResolvePassword(reference);
        return new CertificateSecret(password.Value);
    }

    internal OwnerPasswordLease ResolvePassword(string reference)
    {
        var target = OwnerPasswordTargets.For(_purpose, reference);
        RequireOwner(); // Resample immediately before the one fixed native read.
        var read = default(OwnerCertificateSecretRead);
        byte[]? bytes = null;
        char[]? characters = null;
        OwnerPasswordLease? secret = null;
        try
        {
            read = _reader.Read(target);
            if (read.Error != 0) throw Error(read.Error == 1168 ? "certificate_secret_not_found" : "certificate_secret_unavailable");
            RequireOwner();
            if (read.Allocation == 0 || read.Allocation == new nint(-1) || read.Type != 1 || read.Blob == 0 || read.Blob == new nint(-1) ||
                read.BlobBytes is < 2 or > MaximumPasswordBytes || read.BlobBytes % 2 != 0)
                throw Error("certificate_secret_blob_invalid");
            bytes = new byte[checked((int)read.BlobBytes)];
            Marshal.Copy(read.Blob, bytes, 0, bytes.Length);
            var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            var count = encoding.GetCharCount(bytes);
            if (count is < 1 or > 1024) throw Error("certificate_secret_blob_invalid");
            characters = new char[count];
            _ = encoding.GetChars(bytes, characters);
            if (characters.AsSpan().Contains('\0')) throw Error("certificate_secret_blob_invalid");
            secret = new OwnerPasswordLease(characters);
        }
        catch (CertificateOperationException) { throw; }
        catch (DecoderFallbackException) { throw Error("certificate_secret_blob_invalid"); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { throw Error("certificate_secret_unavailable"); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (characters is not null) Array.Clear(characters);
            try
            {
                OwnerCertificateSecretNative.ClearAndFree(read, _reader);
            }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
            {
                secret?.Dispose();
                throw Error("certificate_secret_cleanup_failed");
            }
        }
        return secret!;
    }

    private void RequireOwner() { if (!_identity.IsCurrentOwner()) throw Error("certificate_secret_owner_denied"); }
    private static CertificateOperationException Error(string code) => new(code);

}

// Engine-only short-lived memory. Never serialize or expose as a string/public property.
internal sealed class OwnerPasswordLease : IDisposable
{
    private char[]? _value;
    internal OwnerPasswordLease(ReadOnlySpan<char> value) => _value = value.ToArray();
    internal ReadOnlySpan<char> Value => _value ?? throw new ObjectDisposedException(nameof(OwnerPasswordLease));
    public override string ToString() => "<protected owner password>";
    public void Dispose() { if (_value is not { } value) return; Array.Clear(value); _value = null; }
}
