using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Fixture-only deterministic receipt authority. It exercises Core's proof verification and
/// lost-response recovery without implying a production key-custody implementation.
/// </summary>
internal sealed class FixtureSyncReceiptIssuer : ICortexSyncReceiptIssuer, IDisposable
{
    private const string ProofContractId = "pegasus.cortex.fixture-hmac-proof/1.0";
    private const string KeyId = "fixture-sync-key";
    private const string AlgorithmId = "hmac-sha256";
    private readonly byte[] _key = Enumerable.Repeat((byte)0x5C, 32).ToArray();
    private bool _disposed;

    public int IssueCount { get; private set; }

    public ValueTask<CortexSyncReceiptV1> IssueAsync(
        CortexSyncReceiptIssuance issuance,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(issuance);
        cancellationToken.ThrowIfCancellationRequested();
        IssueCount++;

        var receiptId = "receipt-" + CortexCanonicalJson.Sha256(
            Encoding.UTF8.GetBytes(issuance.Commit.CommitId));
        var durability = new CortexSyncReceiptDurabilityV1(
            issuance.Commit.CommitId,
            issuance.Commit.StorageRevision,
            issuance.Commit.CommittedAt);
        var placeholderProof = new byte[] { 0 };
        var placeholderAuthentication = Authentication(
            CortexSyncContractIds.EmptySha256,
            placeholderProof);
        var placeholder = CreateReceipt(
            issuance,
            receiptId,
            durability,
            placeholderAuthentication,
            CortexSyncContractIds.EmptySha256);
        var receiptHash = CortexSyncContractPolicy.ComputeReceiptSemanticHash(placeholder);
        var signingBytes = Encoding.UTF8.GetBytes(receiptHash);
        byte[]? proof = null;
        try
        {
            proof = HMACSHA256.HashData(_key, signingBytes);
            return ValueTask.FromResult(CreateReceipt(
                issuance,
                receiptId,
                durability,
                Authentication(receiptHash, proof),
                receiptHash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            if (proof is not null)
                CryptographicOperations.ZeroMemory(proof);
        }
    }

    public ValueTask<bool> VerifyProofAsync(
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(receipt.Authentication.ProofContractId, ProofContractId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Authentication.KeyId, KeyId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Authentication.AlgorithmId, AlgorithmId, StringComparison.Ordinal) ||
            !CortexSyncContractPolicy.HasValidSemanticHash(receipt))
        {
            return ValueTask.FromResult(false);
        }

        var signingBytes = Encoding.UTF8.GetBytes(receipt.ReceiptHash);
        var actual = DecodeBase64Url(receipt.Authentication.ProofBase64Url);
        byte[]? expected = null;
        try
        {
            expected = HMACSHA256.HashData(_key, signingBytes);
            var verified = actual.Length == expected.Length &&
                string.Equals(
                    CortexCanonicalJson.Sha256(actual),
                    receipt.Authentication.ProofSha256,
                    StringComparison.Ordinal) &&
                CryptographicOperations.FixedTimeEquals(actual, expected);
            return ValueTask.FromResult(verified);
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

    private static CortexSyncReceiptAuthenticationV1 Authentication(
        string coveredReceiptHash,
        ReadOnlySpan<byte> proof)
    {
        var captured = proof.ToArray();
        try
        {
            return new CortexSyncReceiptAuthenticationV1(
                ProofContractId,
                KeyId,
                AlgorithmId,
                coveredReceiptHash,
                Base64Url(captured),
                captured.Length,
                CortexCanonicalJson.Sha256(captured));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(captured);
        }
    }

    private static CortexSyncReceiptV1 CreateReceipt(
        CortexSyncReceiptIssuance issuance,
        string receiptId,
        CortexSyncReceiptDurabilityV1 durability,
        CortexSyncReceiptAuthenticationV1 authentication,
        string receiptHash) =>
        new(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncReceipt,
            CortexSyncContractIds.Canonicalization,
            receiptId,
            "server-fixture",
            issuance.Bundle.StoreId,
            issuance.Bundle.StoreEpoch,
            issuance.Bundle.ProjectId,
            issuance.Bundle.BundleId,
            issuance.Bundle.BundleHash,
            issuance.ReceivedAt,
            authentication,
            durability,
            issuance.Commit.Outcomes,
            issuance.Reconciliation,
            [],
            receiptHash);

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("The fixture receipt proof is not valid base64url.")
        };
        return Convert.FromBase64String(normalized);
    }
}
