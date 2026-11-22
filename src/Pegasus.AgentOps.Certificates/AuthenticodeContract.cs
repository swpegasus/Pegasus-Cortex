namespace Pegasus.AgentOps.Certificates;

/// <summary>
/// Integration seam implemented by the optional WindowsAuthenticodeFileAdapter.
/// The adapter must retain exact file identity/preimage checks through signing, resolve the key reference in
/// the admitted identity, forbid interactive prompts/network timestamping, and inspect the resulting signature.
/// The native adapter supports single primary RSA/SHA256 PE and registered PS1 SIPs; optional signing
/// requires the Windows 11 digest callback. Unsupported formats/capabilities fail explicitly. Its concrete
/// cancellation overload never abandons issued native work or automatically reverses/replays an attempt.
/// Existing tools/signing/Sign-PegasusSingleFile.ps1 supports a CurrentUser PE certificate only. Its selected
/// thumbprint/provider must be derived from the resolved reference, not accepted as a replacement identity.
/// OS trust is evaluated offline without UI, separately from SIP/message integrity; revocation is not checked.
/// Signing never creates a timestamp. Existing timestamp attributes do not invalidate primary integrity;
/// Windows trust evaluates their time policy separately. Nested/multiple primary signatures are unsupported.
/// Actual platform/SIP qualification and store-key persistence remain separately reported capabilities.
/// </summary>
public interface IAuthenticodeFileAdapter
{
    AuthenticodeSignResult Sign(AuthenticodeSignRequest request);
    AuthenticodeVerificationResult Verify(AuthenticodeVerifyRequest request);
}

public sealed record AuthenticodeSignRequest(string AbsolutePath, string ExpectedContentSha256, CertificateKeyReference Signer);
public sealed record AuthenticodeVerifyRequest(string AbsolutePath, string ExpectedContentSha256, string ExpectedCertificateId);
public sealed record AuthenticodeSignResult(string State, string CertificateId, string ContentSha256, bool Timestamped);
// Signature validity, OS trust and revocation evidence are independent. An untrusted test CA may sign valid bytes.
public sealed record AuthenticodeVerificationResult(string Signature, string Trust, string Revocation,
    string? CertificateId, int? WindowsStatus);
