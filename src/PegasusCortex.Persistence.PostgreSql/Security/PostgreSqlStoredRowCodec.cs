using System.Security.Cryptography;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Security;

internal sealed record PostgreSqlStoredPayload(
    byte[] Payload,
    string PayloadSha256,
    CortexRowSeal Seal);

internal sealed class PostgreSqlStoredRowCodec
{
    private const int MaximumPayloadBytes = 512 * 1024;
    private readonly ICortexRowAuthenticator _authenticator;

    public PostgreSqlStoredRowCodec(ICortexRowAuthenticator authenticator) =>
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));

    public PostgreSqlStoredPayload Encode<T>(
        string entityKind,
        string tenantId,
        string namespaceName,
        string primaryId,
        string? revisionId,
        T value)
    {
        var payload = CortexCanonicalJson.Serialize(value);
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw IntegrityFailure("cortex_postgresql_payload_size_invalid", entityKind, tenantId, namespaceName, primaryId);
        var payloadSha256 = CortexCanonicalJson.Sha256(payload);
        var envelope = CortexRowAuthentication.CreateEnvelope(
            entityKind,
            tenantId,
            namespaceName,
            primaryId,
            revisionId,
            payloadSha256);
        var seal = _authenticator.Seal(envelope)
            ?? throw IntegrityFailure("cortex_postgresql_row_seal_missing", entityKind, tenantId, namespaceName, primaryId);
        if (!_authenticator.Verify(envelope, seal))
            throw IntegrityFailure("cortex_postgresql_row_seal_unverifiable", entityKind, tenantId, namespaceName, primaryId);
        return new PostgreSqlStoredPayload(payload, payloadSha256, seal);
    }

    public T Decode<T>(
        string entityKind,
        string tenantId,
        string namespaceName,
        string primaryId,
        string? revisionId,
        byte[] payload,
        string payloadSha256,
        int authVersion,
        string authAlgorithm,
        string authKeyId,
        byte[] authSignature)
    {
        if (payload is not { Length: > 0 and <= MaximumPayloadBytes } ||
            !ValidSha256(payloadSha256) ||
            !string.Equals(CortexCanonicalJson.Sha256(payload), payloadSha256, StringComparison.Ordinal))
        {
            throw IntegrityFailure("cortex_postgresql_payload_hash_invalid", entityKind, tenantId, namespaceName, primaryId);
        }

        CortexRowSeal seal;
        CortexRowAuthenticationEnvelope envelope;
        try
        {
            seal = new CortexRowSeal(authVersion, authAlgorithm, authKeyId, authSignature);
            envelope = CortexRowAuthentication.CreateEnvelope(
                entityKind,
                tenantId,
                namespaceName,
                primaryId,
                revisionId,
                payloadSha256);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw IntegrityFailure(
                "cortex_postgresql_row_seal_invalid",
                entityKind,
                tenantId,
                namespaceName,
                primaryId,
                exception);
        }

        if (!_authenticator.Verify(envelope, seal))
            throw IntegrityFailure("cortex_postgresql_row_authentication_failed", entityKind, tenantId, namespaceName, primaryId);

        try
        {
            var value = CortexCanonicalJson.Deserialize<T>(payload);
            var canonical = CortexCanonicalJson.Serialize(value);
            if (canonical.Length != payload.Length ||
                !CryptographicOperations.FixedTimeEquals(canonical, payload))
            {
                throw IntegrityFailure("cortex_postgresql_payload_not_canonical", entityKind, tenantId, namespaceName, primaryId);
            }
            return value;
        }
        catch (PostgreSqlPersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw IntegrityFailure(
                "cortex_postgresql_payload_deserialization_failed",
                entityKind,
                tenantId,
                namespaceName,
                primaryId,
                exception);
        }
    }

    private static bool ValidSha256(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static PostgreSqlPersistenceException IntegrityFailure(
        string code,
        string entityKind,
        string tenantId,
        string namespaceName,
        string primaryId,
        Exception? cause = null) =>
        new(
            code,
            "cortex.postgresql.row.verify",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["entityKind"] = entityKind,
                ["tenantId"] = tenantId,
                ["namespace"] = namespaceName,
                ["primaryId"] = primaryId
            },
            innerException: cause);
}
