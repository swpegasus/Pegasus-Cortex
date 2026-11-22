using System.Security.Cryptography;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Integrated.Security;

namespace Pegasus.Cortex.Mcp.Integrated.Recovery;

internal sealed record CortexIntegratedTransferBundleReadResult(
    CortexRetentionTransferBundleV1 Bundle,
    string FileSha256);

/// <summary>
/// Pins the exact protected file bytes before decoding the already self-sealed
/// canonical transfer contract. Neither a normalized reserialization nor the
/// bundle's internal self-hash substitutes for the caller's file hash.
/// </summary>
internal static class CortexIntegratedProjectTransferBundleReader
{
    internal const int MaximumBundleFileBytes = 768 * 1024 * 1024;

    public static CortexIntegratedTransferBundleReadResult Read(
        string path,
        string expectedFileSha256)
    {
        if (!CortexIntegratedProjectTransferImportArguments.ValidSha256(
                expectedFileSha256))
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_bundle_file_hash_invalid");
        }

        var bytes = ProtectedLocalFile.ReadAllBytes(path, MaximumBundleFileBytes);
        try
        {
            var actualBytes = SHA256.HashData(bytes);
            var expectedBytes = Convert.FromHexString(expectedFileSha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        actualBytes,
                        expectedBytes))
                {
                    throw new CortexRetentionTransferValidationException(
                        "transfer_bundle_file_hash_mismatch");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualBytes);
                CryptographicOperations.ZeroMemory(expectedBytes);
            }

            var bundle = CortexRetentionTransferBundlePolicy.ReadCanonical(bytes);
            return new CortexIntegratedTransferBundleReadResult(
                bundle,
                expectedFileSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
