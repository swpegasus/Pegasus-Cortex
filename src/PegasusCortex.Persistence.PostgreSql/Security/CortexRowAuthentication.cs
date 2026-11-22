using System.Security.Cryptography;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Persistence.PostgreSql.Security;

public static class CortexRowAuthenticationContract
{
    public const int SchemaVersion = 1;
    public const string Domain = "pegasus.cortex.postgresql.row-authentication/1";
    public const int MaximumSignatureBytes = 16 * 1024;
}

/// <summary>
/// Domain-separated identity signed outside PostgreSQL. Binding the physical scope and
/// immutable payload hash prevents a valid row from being substituted into another scope.
/// </summary>
public sealed record CortexRowAuthenticationEnvelope(
    int SchemaVersion,
    string Domain,
    string EntityKind,
    string TenantId,
    string Namespace,
    string PrimaryId,
    string? RevisionId,
    string PayloadSha256);

public sealed class CortexRowSeal
{
    private readonly byte[] _signature;

    public CortexRowSeal(int version, string algorithm, string keyId, ReadOnlySpan<byte> signature)
    {
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (algorithm.Length > 64 || keyId.Length > 128 ||
            !string.Equals(algorithm, algorithm.Trim(), StringComparison.Ordinal) ||
            !string.Equals(keyId, keyId.Trim(), StringComparison.Ordinal) ||
            algorithm.Any(char.IsControl) || keyId.Any(char.IsControl) ||
            signature.IsEmpty || signature.Length > CortexRowAuthenticationContract.MaximumSignatureBytes)
        {
            throw new ArgumentException("The row seal exceeds its bounded contract.", nameof(signature));
        }

        Version = version;
        Algorithm = algorithm;
        KeyId = keyId;
        _signature = signature.ToArray();
    }

    public int Version { get; }
    public string Algorithm { get; }
    public string KeyId { get; }
    public int SignatureLength => _signature.Length;
    public byte[] GetSignature() => _signature.ToArray();

    internal bool SignatureEquals(ReadOnlySpan<byte> value) =>
        value.Length == _signature.Length && CryptographicOperations.FixedTimeEquals(value, _signature);
}

/// <summary>
/// Host-owned key boundary. Implementations may use asymmetric signatures or a protected
/// MAC key ring; database rows contain only the algorithm, key identifier, and signature.
/// </summary>
public interface ICortexRowAuthenticator
{
    CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope);
    bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal);
}

public static class CortexRowAuthentication
{
    public static CortexRowAuthenticationEnvelope CreateEnvelope(
        string entityKind,
        string tenantId,
        string namespaceName,
        string primaryId,
        string? revisionId,
        string payloadSha256)
    {
        ValidateBoundedIdentifier(entityKind, 64, nameof(entityKind));
        ValidateBoundedIdentifier(tenantId, 128, nameof(tenantId));
        ValidateBoundedNamespace(namespaceName, nameof(namespaceName));
        ValidateBoundedIdentifier(primaryId, 256, nameof(primaryId));
        if (revisionId is not null)
            ValidateBoundedIdentifier(revisionId, 256, nameof(revisionId));
        if (!ValidSha256(payloadSha256))
            throw new ArgumentException("The payload hash must be lowercase SHA-256.", nameof(payloadSha256));

        return new CortexRowAuthenticationEnvelope(
            CortexRowAuthenticationContract.SchemaVersion,
            CortexRowAuthenticationContract.Domain,
            entityKind,
            tenantId,
            namespaceName,
            primaryId,
            revisionId,
            payloadSha256);
    }

    public static byte[] GetSigningBytes(CortexRowAuthenticationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var captured = CreateEnvelope(
            envelope.EntityKind,
            envelope.TenantId,
            envelope.Namespace,
            envelope.PrimaryId,
            envelope.RevisionId,
            envelope.PayloadSha256);
        if (envelope.SchemaVersion != captured.SchemaVersion ||
            !string.Equals(envelope.Domain, captured.Domain, StringComparison.Ordinal))
        {
            throw new ArgumentException("The row authentication envelope version or domain is unsupported.", nameof(envelope));
        }

        return CortexCanonicalJson.Serialize(captured);
    }

    private static void ValidateBoundedIdentifier(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("The value is not a bounded Cortex identifier.", parameterName);
        }
    }

    private static void ValidateBoundedNamespace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("The value is not a bounded Cortex namespace.", parameterName);
        }
    }

    private static bool ValidSha256(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
