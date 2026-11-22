using System.Runtime.Versioning;
using Pegasus.AgentOps.Certificates;

namespace Pegasus.AgentOps.Engine;

internal interface IOwnerSmbPasswordResolver
{
    // The caller owns and must dispose the mutable Engine-only lease.
    OwnerPasswordLease Resolve(string reference);
}

// Uses the same bounded CurrentUser ABI/encoding/cleanup as certificate passwords,
// with a different fixed purpose. Certificate references are never a fallback.
[SupportedOSPlatform("windows")]
internal sealed class OwnerSmbPasswordReferences : IOwnerSmbPasswordResolver
{
    private readonly OwnerCertificateSecretStore _store;
    private readonly OwnerCertificateSecrets _reader;
    internal OwnerSmbPasswordReferences(string ownerSid)
        : this(ownerSid, LocalIdentity.Current, new OwnerCertificateSecretNative(OwnerPasswordPurpose.SmbPassword)) { }
    internal OwnerSmbPasswordReferences(string ownerSid, Func<LocalPeer> current, IOwnerCertificateSecretBackend backend)
    {
        _store = new(ownerSid, current, backend, OwnerPasswordPurpose.SmbPassword);
        _reader = new(ownerSid, current, backend, OwnerPasswordPurpose.SmbPassword);
    }
    internal OwnerCertificateSecretResult Inspect(string reference) => Map(_store.Inspect(reference));
    internal OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password) => Map(_store.Save(reference, password));
    internal OwnerCertificateSecretResult Remove(string reference) => Map(_store.Remove(reference));
    public OwnerPasswordLease Resolve(string reference)
    {
        try { return _reader.ResolvePassword(reference); }
        catch (CertificateOperationException error)
        {
            throw new SmbConnectionException(error.Code switch
            {
                "certificate_secret_not_found" => "smb_password_not_found",
                "certificate_secret_owner_denied" => "smb_owner_denied",
                "certificate_secret_reference_invalid" => "smb_password_reference_invalid",
                _ => "smb_password_unavailable"
            });
        }
    }
    private static OwnerCertificateSecretResult Map(OwnerCertificateSecretResult result) => result with
    {
        Error = result.Error switch
        {
            null => null,
            "certificate_secret_reference_invalid" => "smb_password_reference_invalid",
            "certificate_secret_blob_invalid" => "smb_password_blob_invalid",
            "certificate_secret_owner_denied" => "smb_owner_denied",
            "certificate_secret_cleanup_failed" => "smb_password_cleanup_failed",
            "certificate_secret_write_failed" => "smb_password_write_failed",
            "certificate_secret_write_unobserved" => "smb_password_write_unobserved",
            "certificate_secret_remove_failed" => "smb_password_remove_failed",
            "certificate_secret_remove_unobserved" => "smb_password_remove_unobserved",
            _ => "smb_password_unavailable"
        }
    };
}
