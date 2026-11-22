using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Certificates;

/// <summary>A single issued signing attempt is never automatically reversed or replayed.</summary>
public sealed class AuthenticodeAttemptException(string code, bool issued, bool outcomeUnknown,
    bool postconditionObserved, int? windowsStatus = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool Issued { get; } = issued;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public bool PostconditionObserved { get; } = postconditionObserved;
    public int? WindowsStatus { get; } = windowsStatus;
}

/// <summary>
/// Optional Windows signer: RSA/SHA-256, one embedded signature, ordinary owner only.
/// Signing creates no timestamp; verification leaves existing timestamp policy to offline Windows trust.
/// The execution owner supplies an already-admitted root and resolver; neither is a new grant.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAuthenticodeFileAdapter : IAuthenticodeFileAdapter
{
    private readonly string _ownerSid, _root;
    private readonly int _session;
    private readonly ICertificateKeyResolver _keys;
    private readonly Func<SafeFileHandle, string, AuthenticodeVerificationResult> _trust;

    public WindowsAuthenticodeFileAdapter(string expectedOwnerSid, string admittedRoot, ICertificateKeyResolver keys)
        : this(expectedOwnerSid, admittedRoot, keys, AuthenticodeNative.VerifyTrust) { }

    internal WindowsAuthenticodeFileAdapter(string expectedOwnerSid, string admittedRoot, ICertificateKeyResolver keys,
        Func<SafeFileHandle, string, AuthenticodeVerificationResult> trust)
    {
        if (!OperatingSystem.IsWindows()) throw new CertificateOperationException("authenticode_unsupported");
        _ownerSid = new SecurityIdentifier(expectedOwnerSid).Value;
        _root = AuthenticodeFileLease.Canonical(admittedRoot, directory: true);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        using var process = Process.GetCurrentProcess(); _session = process.SessionId;
        RequireOwner();
    }

    public AuthenticodeSignResult Sign(AuthenticodeSignRequest request) => Sign(request, CancellationToken.None);
    public AuthenticodeVerificationResult Verify(AuthenticodeVerifyRequest request) => Verify(request, CancellationToken.None);

    public AuthenticodeSignResult Sign(AuthenticodeSignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOwner(); RequireHash(request.ExpectedContentSha256); CertificateMaterial.RequireKeyReference(request.Signer);
        if (cancellationToken.IsCancellationRequested) throw Attempt("authenticode_canceled_before_issue", false);
        if (!AuthenticodeNative.SigningAvailable) throw Attempt("authenticode_signing_unsupported", false);
        using var file = new AuthenticodeFileLease(_root, request.AbsolutePath, writable: true);
        string preimage;
        try { preimage = file.Hash(cancellationToken); }
        catch (OperationCanceledException) { throw Attempt("authenticode_canceled_before_issue", false); }
        if (preimage != request.ExpectedContentSha256) throw Attempt("authenticode_preimage_changed", false);
        var existing = AuthenticodeNative.Inspect(file, null);
        if (existing.Signature != "not_signed") throw Attempt(existing.Signature == "unsupported" ? "authenticode_subject_unsupported" : "authenticode_already_signed", false);
        using var certificate = _keys.Resolve(request.Signer);
        CertificateMaterial.RequireIdentity(certificate, request.Signer.ExpectedCertificateId);
        CertificateMaterial.RequireSigning(certificate, DateTimeOffset.UtcNow, privateKey: true);
        RequireOwner();
        var issued = false;
        var observed = false;
        int? status = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.RequireIdentity(file.Handle);
            status = AuthenticodeNative.Sign(file, certificate, request.Signer.ExpectedCertificateId, cancellationToken, () => issued = true);
            file.Flush();
            file.RequireIdentity(file.Handle);
            var postimage = file.Hash(CancellationToken.None);
            var verification = AuthenticodeNative.Inspect(file, request.Signer.ExpectedCertificateId);
            observed = verification.Signature == "valid" && verification.CertificateId == request.Signer.ExpectedCertificateId;
            return CompleteSignAttempt(status.Value, observed, cancellationToken.IsCancellationRequested,
                request.Signer.ExpectedCertificateId, postimage);
        }
        catch (AuthenticodeAttemptException) { throw; }
        catch (OperationCanceledException) when (!issued) { throw Attempt("authenticode_canceled_before_issue", false); }
        catch (Exception error) when (issued && error is not OutOfMemoryException and not StackOverflowException)
        { throw new AuthenticodeAttemptException("authenticode_sign_outcome_unknown", true, true, observed, status); }
    }

    // A late cancellation cannot erase a flushed, exact-identity signed
    // postcondition. Native failure remains a failure, but an observed effect
    // is known rather than retrospectively labeled unknown or rolled back.
    internal static AuthenticodeSignResult CompleteSignAttempt(int nativeStatus, bool postconditionObserved,
        bool cancellationRequested, string certificateId, string postimage)
    {
        if (postconditionObserved)
        {
            if (nativeStatus == 0) return new("signed", certificateId, postimage, false);
            throw new AuthenticodeAttemptException("authenticode_native_error_postcondition_observed", true, false, true, nativeStatus);
        }
        throw new AuthenticodeAttemptException(cancellationRequested ? "authenticode_canceled_after_issue" : "authenticode_sign_outcome_unknown",
            true, true, false, nativeStatus);
    }

    public AuthenticodeVerificationResult Verify(AuthenticodeVerifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOwner(); RequireHash(request.ExpectedContentSha256); CertificateMaterial.RequireId(request.ExpectedCertificateId);
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new AuthenticodeFileLease(_root, request.AbsolutePath, writable: false);
        if (file.Hash(cancellationToken) != request.ExpectedContentSha256) throw Attempt("authenticode_preimage_changed", false);
        var result = AuthenticodeNative.Inspect(file, request.ExpectedCertificateId);
        if (result.Signature != "valid") return result;
        cancellationToken.ThrowIfCancellationRequested();
        var trust = _trust(file.Handle, file.Path);
        file.RequireIdentity(file.Handle);
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Trust = trust.Trust, Revocation = trust.Revocation, WindowsStatus = trust.WindowsStatus };
    }

    private void RequireOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (_session <= 0 || process.SessionId != _session || identity.User?.Value != _ownerSid ||
            identity.IsAnonymous || identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            _ownerSid is "S-1-5-19" or "S-1-5-20")
            throw new CertificateOperationException("authenticode_owner_required");
    }
    private static void RequireHash(string value) => CertificateMaterial.RequireId(value);
    private static AuthenticodeAttemptException Attempt(string code, bool issued) => new(code, issued, issued, false);
}
