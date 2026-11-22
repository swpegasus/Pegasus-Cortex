using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

internal static class PostgreSqlRowCodecTests
{
    private const string EntityKind = "knowledge-revision";
    private const string TenantId = "tenant-a";
    private const string NamespaceName = "shared:pegasus-default-library";
    private const string PrimaryId = "record-7";
    private const string RevisionId = "revision-3";

    public static Task RoundTripAndTamperDenialAsync()
    {
        using var authenticator = new TestHmacRowAuthenticator(
            "test-key-1",
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var codec = new PostgreSqlStoredRowCodec(authenticator);
        var expected = new SampleRow("record-7", 3, "Canonical Cortex evidence");
        var stored = codec.Encode(
            EntityKind,
            TenantId,
            NamespaceName,
            PrimaryId,
            RevisionId,
            expected);

        var actual = Decode(codec, stored, TenantId, NamespaceName);
        TestAssert.Equal(expected, actual, "Canonical authenticated row did not round trip.");

        // Recompute the payload hash after mutation to prove that the scope-bound seal,
        // not only the plain SHA-256 comparison, rejects a self-consistent alteration.
        var mutatedPayload = CortexCanonicalJson.Serialize(expected with { Revision = 4 });
        var mutation = TestAssert.Throws<PostgreSqlPersistenceException>(() => codec.Decode<SampleRow>(
            EntityKind,
            TenantId,
            NamespaceName,
            PrimaryId,
            RevisionId,
            mutatedPayload,
            CortexCanonicalJson.Sha256(mutatedPayload),
            stored.Seal.Version,
            stored.Seal.Algorithm,
            stored.Seal.KeyId,
            stored.Seal.GetSignature()));
        AssertAuthenticationFailure(mutation, "Payload mutation");

        using var wrongAuthenticator = new TestHmacRowAuthenticator(
            "test-key-1",
            Enumerable.Range(33, 32).Select(value => (byte)value).ToArray());
        var wrongKey = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            Decode(new PostgreSqlStoredRowCodec(wrongAuthenticator), stored, TenantId, NamespaceName));
        AssertAuthenticationFailure(wrongKey, "Wrong key");

        var crossTenant = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            Decode(codec, stored, "tenant-b", NamespaceName));
        AssertAuthenticationFailure(crossTenant, "Cross-tenant substitution");

        var crossNamespace = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            Decode(codec, stored, TenantId, "private:learning"));
        AssertAuthenticationFailure(crossNamespace, "Cross-namespace substitution");
        return Task.CompletedTask;
    }

    public static Task LegacyProjectLinkPayloadRemainsCanonicalAsync()
    {
        const string projectLinkId =
            "plink_ffec4b42575828846638ad9e48fdd7b5d4dfcf462d2659f449df1808c10ad95a";
        const string payloadSha256 =
            "836e043e7f8a84e92d06f9193d3b8fbe30795c4ab478613c6100a80a0fcbfc66";
        const string legacyPayload =
            """{"correlationHash":"7b380ff58efc42860e54a6bf629573416028b643b883bf02ab2b760340d5b609","metadata":{"contractId":"pegasus.cortex.project-link/1.0","contractVersion":"hybrid-v1","endpointId":"pegasus-ai-arm64-cortex-01","projectId":"pegasus-cortex","projectLinkId":"plink_ffec4b42575828846638ad9e48fdd7b5d4dfcf462d2659f449df1808c10ad95a","projectNamespace":"project-ffec4b42575828846638ad9e48fdd7b5d4dfcf462d2659f4","schemaVersion":1,"storeEpoch":1,"storeId":"windows-workspace-d-codex-projects"}}""";

        var payload = Encoding.UTF8.GetBytes(legacyPayload);
        TestAssert.Equal(494, payload.Length, "The sealed legacy payload byte count drifted.");
        TestAssert.Equal(
            payloadSha256,
            CortexCanonicalJson.Sha256(payload),
            "The sealed legacy payload hash drifted.");

        using var authenticator = new TestHmacRowAuthenticator(
            "test-key-1",
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var envelope = CortexRowAuthentication.CreateEnvelope(
            "project-link",
            TenantId,
            CortexProjectCorrelationContract.ControlNamespace,
            projectLinkId,
            revisionId: null,
            payloadSha256);
        var seal = authenticator.Seal(envelope);
        var link = new PostgreSqlStoredRowCodec(authenticator).Decode<CortexProjectLinkV1>(
            "project-link",
            TenantId,
            CortexProjectCorrelationContract.ControlNamespace,
            projectLinkId,
            revisionId: null,
            payload,
            payloadSha256,
            seal.Version,
            seal.Algorithm,
            seal.KeyId,
            seal.GetSignature());

        CortexProjectCorrelationPolicy.ValidateStoredLink(
            new CortexProjectLinkAccess(
                TenantId,
                CortexProjectCorrelationContract.ControlNamespace,
                "local-policy",
                "codexsvc-read",
                "pegasus-cortex-mcp",
                1,
                CortexProjectLinkCapability.Read),
            link,
            projectLinkId);
        TestAssert.Equal(
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(link.Metadata),
            "The legacy project link did not retain collective access.");
        return Task.CompletedTask;
    }

    private static SampleRow Decode(
        PostgreSqlStoredRowCodec codec,
        PostgreSqlStoredPayload stored,
        string tenantId,
        string namespaceName) =>
        codec.Decode<SampleRow>(
            EntityKind,
            tenantId,
            namespaceName,
            PrimaryId,
            RevisionId,
            stored.Payload,
            stored.PayloadSha256,
            stored.Seal.Version,
            stored.Seal.Algorithm,
            stored.Seal.KeyId,
            stored.Seal.GetSignature());

    private static void AssertAuthenticationFailure(
        PostgreSqlPersistenceException failure,
        string scenario)
    {
        TestAssert.Equal(
            "cortex_postgresql_row_authentication_failed",
            failure.Code,
            $"{scenario} did not fail at the authenticated row boundary.");
        TestAssert.False(failure.Retryable, $"{scenario} was incorrectly marked retryable.");
    }

    private sealed record SampleRow(string Id, int Revision, string Summary);

    private sealed class TestHmacRowAuthenticator : ICortexRowAuthenticator, IDisposable
    {
        private const string Algorithm = "HMAC-SHA256";
        private readonly byte[] _key;
        private readonly string _keyId;
        private bool _disposed;

        public TestHmacRowAuthenticator(string keyId, byte[] key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentNullException.ThrowIfNull(key);
            if (key.Length < 32)
                throw new ArgumentException("The test HMAC key must contain at least 256 bits.", nameof(key));
            _keyId = keyId;
            _key = key.ToArray();
        }

        public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
            try
            {
                var signature = HMACSHA256.HashData(_key, signingBytes);
                try
                {
                    return new CortexRowSeal(1, Algorithm, _keyId, signature);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(signature);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingBytes);
            }
        }

        public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(seal);
            if (seal.Version != 1 ||
                !string.Equals(seal.Algorithm, Algorithm, StringComparison.Ordinal) ||
                !string.Equals(seal.KeyId, _keyId, StringComparison.Ordinal))
            {
                return false;
            }

            var signingBytes = CortexRowAuthentication.GetSigningBytes(envelope);
            var expected = HMACSHA256.HashData(_key, signingBytes);
            var actual = seal.GetSignature();
            try
            {
                return expected.Length == actual.Length &&
                    CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingBytes);
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
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
}
