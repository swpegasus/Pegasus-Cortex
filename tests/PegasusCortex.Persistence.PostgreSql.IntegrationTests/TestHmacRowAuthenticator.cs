using System.Security.Cryptography;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Fixture-only host authenticator. Production key custody remains outside the provider.
/// </summary>
internal sealed class TestHmacRowAuthenticator : ICortexRowAuthenticator, IDisposable
{
    private const string Algorithm = "HMAC-SHA256";
    private readonly string _keyId;
    private readonly byte[] _key;
    private bool _disposed;

    public TestHmacRowAuthenticator(string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length > 128 || key.Length is < 32 or > 4096)
            throw new ArgumentException("The fixture authentication key is invalid.", nameof(key));

        _keyId = keyId;
        _key = key.ToArray();
    }

    public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
        byte[]? signature = null;
        try
        {
            signature = HMACSHA256.HashData(_key, signingBytes);
            return new CortexRowSeal(
                CortexRowAuthenticationContract.SchemaVersion,
                Algorithm,
                _keyId,
                signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }
    }

    public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(seal);
        if (seal.Version != CortexRowAuthenticationContract.SchemaVersion ||
            !string.Equals(seal.Algorithm, Algorithm, StringComparison.Ordinal) ||
            !string.Equals(seal.KeyId, _keyId, StringComparison.Ordinal))
        {
            return false;
        }

        var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
        var actual = seal.GetSignature();
        byte[]? expected = null;
        try
        {
            expected = HMACSHA256.HashData(_key, signingBytes);
            return actual.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            CryptographicOperations.ZeroMemory(actual);
            if (expected is not null)
                CryptographicOperations.ZeroMemory(expected);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
