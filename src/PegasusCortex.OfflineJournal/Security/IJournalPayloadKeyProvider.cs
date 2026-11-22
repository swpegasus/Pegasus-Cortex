using System.Security.Cryptography;

namespace Pegasus.Cortex.OfflineJournal.Security;

/// <summary>
/// Supplies host-owned payload keys without giving the journal responsibility for generating,
/// locating, persisting, rotating, or recovering secrets. Implementations must return a fresh
/// lease for each operation and retain prior decrypt keys until all referenced payloads have
/// been migrated or expired under an independently approved policy.
/// </summary>
public interface IJournalPayloadKeyProvider
{
    ValueTask<JournalPayloadKeyLease> AcquireCurrentKeyAsync(CancellationToken cancellationToken);

    ValueTask<JournalPayloadKeyLease> AcquireKeyAsync(
        string keyId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Short-lived copy of one AES-256 key. The lease deliberately exposes no public key accessor;
/// only the local protector can consume it, and disposal clears the entire owned buffer.
/// </summary>
public sealed class JournalPayloadKeyLease : IDisposable
{
    private byte[]? _keyMaterial;

    public JournalPayloadKeyLease(string keyId, ReadOnlySpan<byte> keyMaterial)
    {
        if (!JournalPayloadProtectionPolicy.IsIdentifier(keyId))
            throw new ArgumentException("The journal payload key identifier is invalid or unbounded.", nameof(keyId));
        if (keyMaterial.Length != AesGcmJournalPayloadProtector.KeyBytes)
            throw new ArgumentException("A journal AES-256 key must contain exactly 32 bytes.", nameof(keyMaterial));

        KeyId = keyId;
        _keyMaterial = keyMaterial.ToArray();
    }

    public string KeyId { get; }

    internal ReadOnlySpan<byte> KeyMaterial =>
        _keyMaterial ?? throw new ObjectDisposedException(nameof(JournalPayloadKeyLease));

    public void Dispose()
    {
        var material = Interlocked.Exchange(ref _keyMaterial, null);
        if (material is not null)
            CryptographicOperations.ZeroMemory(material);
    }
}

/// <summary>
/// Signals that protection cannot be attempted safely because a required host key or protection
/// suite is unavailable. It is distinct from definitive authentication failure so recovery can
/// preserve retained bytes instead of quarantining them as corrupt.
/// </summary>
public sealed class JournalPayloadProtectionUnavailableException : IOException
{
    public JournalPayloadProtectionUnavailableException(
        string diagnosticCode,
        string message,
        string? keyId,
        bool retryable,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!JournalPayloadProtectionPolicy.IsDiagnosticCode(diagnosticCode))
            throw new ArgumentException("The protection diagnostic code is invalid.", nameof(diagnosticCode));
        if (keyId is not null && !JournalPayloadProtectionPolicy.IsIdentifier(keyId))
            throw new ArgumentException("The protection diagnostic key identifier is invalid.", nameof(keyId));

        DiagnosticCode = diagnosticCode;
        KeyId = keyId;
        Retryable = retryable;
    }

    public string DiagnosticCode { get; }
    public string? KeyId { get; }
    public bool Retryable { get; }
}
