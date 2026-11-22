using System.Security.Cryptography;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Mcp.Integrated.Security;

/// <summary>
/// Process-local HMAC row authenticator backed by a protected host key file. The key never
/// enters PostgreSQL, MCP results, diagnostics, or project-local continuity metadata.
/// </summary>
internal sealed class HmacSha256CortexRowAuthenticator : ICortexRowAuthenticator, IDisposable
{
    private const string Algorithm = "hmac-sha256";
    private readonly byte[] _key;
    private readonly string _keyId;
    private int _disposed;

    private HmacSha256CortexRowAuthenticator(byte[] key, string keyId)
    {
        _key = key;
        _keyId = keyId;
    }

    public static HmacSha256CortexRowAuthenticator Load(string path, string keyId)
    {
        if (!ValidIdentifier(keyId))
            throw new InvalidOperationException("The Cortex row-authentication key identifier is invalid.");
        var encoded = ProtectedLocalFile.ReadUtf8(path, 256).Trim();
        if (encoded.Length != 64 ||
            encoded.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("The Cortex row-authentication key must be 32 lowercase-hex bytes.");
        }

        var key = Convert.FromHexString(encoded);
        return new HmacSha256CortexRowAuthenticator(key, keyId);
    }

    public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope)
    {
        ThrowIfDisposed();
        var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
        var signature = HMACSHA256.HashData(_key, signingBytes);
        try
        {
            return new CortexRowSeal(1, Algorithm, _keyId, signature);
        }
        finally
        {
            Array.Clear(signingBytes);
            Array.Clear(signature);
        }
    }

    public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(seal);
        if (seal.Version != 1 ||
            !string.Equals(seal.Algorithm, Algorithm, StringComparison.Ordinal) ||
            !string.Equals(seal.KeyId, _keyId, StringComparison.Ordinal))
        {
            return false;
        }

        var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
        var expected = HMACSHA256.HashData(_key, signingBytes);
        var submitted = seal.GetSignature();
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, submitted);
        }
        finally
        {
            Array.Clear(signingBytes);
            Array.Clear(expected);
            Array.Clear(submitted);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            CryptographicOperations.ZeroMemory(_key);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
