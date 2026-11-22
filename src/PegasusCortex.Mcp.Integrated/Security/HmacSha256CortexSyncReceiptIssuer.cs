using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Mcp.Integrated.Security;

/// <summary>
/// Production host seam for deterministic authenticated sync receipts. The
/// protected HMAC key is process-local and never enters PostgreSQL, MCP output,
/// diagnostics, or project continuity files.
/// </summary>
internal sealed class HmacSha256CortexSyncReceiptIssuer :
    ICortexSyncReceiptIssuer,
    IDisposable
{
    internal const string ProofContractId =
        "pegasus.cortex.hmac-sha256-receipt-proof/1.0";
    internal const string AlgorithmId = "hmac-sha256";

    private readonly byte[] _key;
    private readonly string _keyId;
    private readonly string _serverInstanceId;
    private int _disposed;

    private HmacSha256CortexSyncReceiptIssuer(
        byte[] key,
        string keyId,
        string serverInstanceId)
    {
        _key = key;
        _keyId = keyId;
        _serverInstanceId = serverInstanceId;
    }

    public static HmacSha256CortexSyncReceiptIssuer Load(
        string path,
        string keyId,
        string serverInstanceId)
    {
        if (!ValidIdentifier(keyId) || !ValidIdentifier(serverInstanceId))
        {
            throw new InvalidOperationException(
                "The Cortex receipt key or server instance identifier is invalid.");
        }

        var encoded = ProtectedLocalFile.ReadUtf8(path, 256).Trim();
        if (encoded.Length != 64 ||
            encoded.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "The Cortex receipt key must be 32 lowercase-hex bytes.");
        }

        return new HmacSha256CortexSyncReceiptIssuer(
            Convert.FromHexString(encoded),
            keyId,
            serverInstanceId);
    }

    public ValueTask<CortexSyncReceiptV1> IssueAsync(
        CortexSyncReceiptIssuance issuance,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(issuance);
        cancellationToken.ThrowIfCancellationRequested();

        var receiptId = $"receipt_{CortexCanonicalJson.Sha256(
            Encoding.UTF8.GetBytes(issuance.Commit.CommitId))}";
        var durability = new CortexSyncReceiptDurabilityV1(
            issuance.Commit.CommitId,
            issuance.Commit.StorageRevision,
            issuance.Commit.CommittedAt);
        Span<byte> placeholderProof = stackalloc byte[1];
        var placeholder = CreateReceipt(
            issuance,
            receiptId,
            durability,
            CreateAuthentication(CortexSyncContractIds.EmptySha256, placeholderProof),
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
                CreateAuthentication(receiptHash, proof),
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                receipt.Authentication.ProofContractId,
                ProofContractId,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.Authentication.KeyId, _keyId, StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Authentication.AlgorithmId,
                AlgorithmId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ServerInstanceId,
                _serverInstanceId,
                StringComparison.Ordinal) ||
            !CortexSyncContractPolicy.HasValidSemanticHash(receipt))
        {
            return ValueTask.FromResult(false);
        }

        byte[] actual;
        try
        {
            actual = DecodeBase64Url(receipt.Authentication.ProofBase64Url);
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(false);
        }

        var signingBytes = Encoding.UTF8.GetBytes(receipt.ReceiptHash);
        byte[]? expected = null;
        try
        {
            expected = HMACSHA256.HashData(_key, signingBytes);
            return ValueTask.FromResult(
                actual.Length == expected.Length &&
                string.Equals(
                    CortexCanonicalJson.Sha256(actual),
                    receipt.Authentication.ProofSha256,
                    StringComparison.Ordinal) &&
                CryptographicOperations.FixedTimeEquals(actual, expected));
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            CryptographicOperations.ZeroMemory(_key);
    }

    private CortexSyncReceiptAuthenticationV1 CreateAuthentication(
        string coveredReceiptHash,
        ReadOnlySpan<byte> proof)
    {
        var captured = proof.ToArray();
        try
        {
            return new CortexSyncReceiptAuthenticationV1(
                ProofContractId,
                _keyId,
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

    private CortexSyncReceiptV1 CreateReceipt(
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
            _serverInstanceId,
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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("The Cortex receipt proof is not valid base64url.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');
}
