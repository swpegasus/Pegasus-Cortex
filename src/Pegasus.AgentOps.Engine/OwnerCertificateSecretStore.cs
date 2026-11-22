using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Pegasus.AgentOps.Certificates;

namespace Pegasus.AgentOps.Engine;

// Local UI metadata only. This type never carries a password, blob, target,
// Windows username, native allocation or arbitrary native diagnostic.
internal sealed record OwnerCertificateSecretResult(bool Ok, string State, string? Error = null,
    bool ActionIssued = false, bool OutcomeUnknown = false);

/// <summary>
/// Ordinary original-owner certificate-password provisioning only. Save explicitly
/// creates OR replaces; no compare/create guarantee, enumeration, identity switch,
/// durable request, database storage, elevation, fallback or automatic retry.
/// The product's local admission/lifetime lease remains the caller's responsibility.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OwnerCertificateSecretStore
{
    private readonly OwnerHostIdentity _identity;
    private readonly IOwnerCertificateSecretBackend _backend;
    private readonly OwnerPasswordPurpose _purpose;
    private static readonly UnicodeEncoding Encoding = new(false, false, true);

    internal OwnerCertificateSecretStore(string ownerSid)
        : this(ownerSid, LocalIdentity.Current, new OwnerCertificateSecretNative()) { }

    internal OwnerCertificateSecretStore(string ownerSid, Func<LocalPeer> current, IOwnerCertificateSecretBackend backend)
        : this(ownerSid, current, backend, OwnerPasswordPurpose.CertificatePassword) { }

    internal OwnerCertificateSecretStore(string ownerSid, Func<LocalPeer> current, IOwnerCertificateSecretBackend backend, OwnerPasswordPurpose purpose)
    {
        OwnerPasswordTargets.RequirePurpose(purpose);
        _purpose = purpose;
        _identity = new OwnerHostIdentity(ownerSid, current);
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    // Presence is not a claim that the password is correct or even usable by a
    // certificate. Inspect never decodes, exposes or returns the credential blob.
    internal OwnerCertificateSecretResult Inspect(string reference)
    {
        try
        {
            var target = OwnerPasswordTargets.For(_purpose, reference);
            _ = Observe(target, ReadOnlySpan<byte>.Empty, compare: false, out var present);
            return new(true, present ? "present" : "missing");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return new(false, "unavailable", Code(error)); }
    }

    /// <summary>
    /// Upserts one exact fixed target. A successful result requires an exact byte
    /// readback, which is an observation rather than a lock against other writers.
    /// Any unobserved outcome after native entry remains unknown, never retried.
    /// </summary>
    internal OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password)
    {
        var issued = false;
        char[]? characters = null;
        byte[]? bytes = null;
        try
        {
            var target = OwnerPasswordTargets.For(_purpose, reference);
            if (password.Length is < 1 or > 1024) throw Error("certificate_secret_blob_invalid");
            // Snapshot caller-owned UI memory once; no immutable password string.
            characters = password.ToArray();
            if (characters.AsSpan().Contains('\0')) throw Error("certificate_secret_blob_invalid");
            bytes = Encoding.GetBytes(characters); // Strict UTF16LE rejects unpaired surrogates.
            RequireOwner();
            var result = _backend.Write(target, bytes, () => { RequireOwner(); issued = true; });
            RequireOwner();
            if (!result.Ok || result.Error != 0) throw Error("certificate_secret_write_failed");
            if (!issued) throw Error("certificate_secret_write_unobserved");
            var matches = Observe(target, bytes, compare: true, out var present);
            if (!present || !matches) throw Error("certificate_secret_write_unobserved");
            return new(true, "saved", ActionIssued: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return Failed(error, issued); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (characters is not null) Array.Clear(characters);
        }
    }

    /// <summary>Removes only the exact Generic Credential. Caller owns the human confirmation.</summary>
    internal OwnerCertificateSecretResult Remove(string reference)
    {
        var issued = false;
        try
        {
            var target = OwnerPasswordTargets.For(_purpose, reference);
            RequireOwner();
            var result = _backend.Delete(target, () => { RequireOwner(); issued = true; });
            RequireOwner();
            if (!issued) throw Error("certificate_secret_remove_unobserved");
            if (!result.Ok)
            {
                if (result.Error == 1168) return new(true, "absent", ActionIssued: true);
                throw Error("certificate_secret_remove_failed");
            }
            if (result.Error != 0) throw Error("certificate_secret_remove_failed");
            _ = Observe(target, ReadOnlySpan<byte>.Empty, compare: false, out var present);
            if (present) throw Error("certificate_secret_remove_unobserved");
            return new(true, "removed", ActionIssued: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return Failed(error, issued); }
    }

    private bool Observe(string target, ReadOnlySpan<byte> expected, bool compare, out bool present)
    {
        var read = default(OwnerCertificateSecretRead);
        byte[]? observed = null;
        present = false;
        try
        {
            RequireOwner();
            read = _backend.Read(target);
            RequireOwner();
            if (read.Error == 1168) return false;
            if (read.Error != 0) throw Error("certificate_secret_unavailable");
            if (read.Allocation == 0 || read.Allocation == new nint(-1) || read.Type != 1 ||
                read.Blob == 0 || read.Blob == new nint(-1) || read.BlobBytes is < 1 or > 5 * 512)
                throw Error("certificate_secret_blob_invalid");
            present = true;
            if (!compare) return false;
            if (read.BlobBytes != expected.Length) return false;
            observed = new byte[checked((int)read.BlobBytes)];
            Marshal.Copy(read.Blob, observed, 0, observed.Length);
            return CryptographicOperations.FixedTimeEquals(observed, expected);
        }
        finally
        {
            if (observed is not null) CryptographicOperations.ZeroMemory(observed);
            try { OwnerCertificateSecretNative.ClearAndFree(read, _backend); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
            { throw Error("certificate_secret_cleanup_failed"); }
            RequireOwner();
        }
    }

    private void RequireOwner()
    { if (!_identity.IsCurrentOwner()) throw Error("certificate_secret_owner_denied"); }
    private static CertificateOperationException Error(string code) => new(code);
    private static OwnerCertificateSecretResult Failed(Exception error, bool issued) =>
        new(false, issued ? "unknown" : "failed", Code(error), issued, issued);
    private static string Code(Exception error)
    {
        var code = error switch
        {
            CertificateOperationException certificate => certificate.Code,
            EncoderFallbackException => "certificate_secret_blob_invalid",
            _ => "certificate_secret_unavailable"
        };
        // Only this unit's closed vocabulary may reach UI metadata. A future
        // backend exception code can itself contain a secret-looking value.
        return code is "certificate_secret_reference_invalid" or "certificate_secret_blob_invalid" or
            "certificate_secret_owner_denied" or "certificate_secret_unavailable" or "certificate_secret_cleanup_failed" or
            "certificate_secret_write_failed" or "certificate_secret_write_unobserved" or
            "certificate_secret_remove_failed" or "certificate_secret_remove_unobserved"
            ? code : "certificate_secret_unavailable";
    }
}

// Exactly two product purposes; callers cannot supply a target prefix/provider.
internal enum OwnerPasswordPurpose { CertificatePassword, SmbPassword }
internal static class OwnerPasswordTargets
{
    internal static void RequirePurpose(OwnerPasswordPurpose purpose)
    {
        if (purpose is not (OwnerPasswordPurpose.CertificatePassword or OwnerPasswordPurpose.SmbPassword))
            throw new CertificateOperationException("certificate_secret_reference_invalid");
    }
    internal static string Prefix(OwnerPasswordPurpose purpose) => purpose switch
    {
        OwnerPasswordPurpose.CertificatePassword => "Pegasus.Cortex.Certificate.",
        OwnerPasswordPurpose.SmbPassword => "Pegasus.Cortex.Smb.",
        _ => throw new CertificateOperationException("certificate_secret_reference_invalid")
    };
    internal static string For(OwnerPasswordPurpose purpose, string reference)
    {
        if (reference is not { Length: >= 1 and <= 64 } || reference[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            reference.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new CertificateOperationException("certificate_secret_reference_invalid");
        return Prefix(purpose) + reference;
    }
    internal static void RequireFixedTarget(OwnerPasswordPurpose purpose, string target)
    {
        var prefix = Prefix(purpose);
        if (target is null || !target.StartsWith(prefix, StringComparison.Ordinal) || For(purpose, target[prefix.Length..]) != target)
            throw new CertificateOperationException("certificate_secret_reference_invalid");
    }
}
internal static class OwnerCertificateSecretTarget
{
    internal const string Prefix = "Pegasus.Cortex.Certificate.";
    internal static string For(string reference) => OwnerPasswordTargets.For(OwnerPasswordPurpose.CertificatePassword, reference);
    internal static void RequireFixedTarget(string target) => OwnerPasswordTargets.RequireFixedTarget(OwnerPasswordPurpose.CertificatePassword, target);
}
