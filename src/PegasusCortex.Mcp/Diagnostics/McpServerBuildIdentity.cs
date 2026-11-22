using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Pegasus.Cortex.Mcp.Diagnostics;

/// <summary>
/// Captures one content-safe identity for the process entry assembly. Exact assembly
/// bytes are preferred; metadata is a deterministic fallback for single-file or
/// otherwise non-addressable hosts. Neither form exposes a local filesystem path.
/// </summary>
internal sealed record McpServerBuildIdentity(string Fingerprint, string Basis);

internal static class McpServerBuildIdentityProvider
{
    private const long MaximumEntryAssemblyBytes = 64L * 1024L * 1024L;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static McpServerBuildIdentity CaptureEntryAssembly() =>
        Capture(Assembly.GetEntryAssembly() ?? typeof(McpServerBuildIdentityProvider).Assembly);

    internal static McpServerBuildIdentity Capture(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var location = assembly.Location;
        if (!string.IsNullOrEmpty(location))
        {
            try
            {
                using var stream = new FileStream(
                    location,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                if (stream.Length is > 0 and <= MaximumEntryAssemblyBytes)
                {
                    return new McpServerBuildIdentity(
                        $"sha256:{Hex(SHA256.HashData(stream))}",
                        "entry-assembly-bytes");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A host may hide or lock the entry image. The metadata fallback stays
                // deterministic and path-free while keeping status available.
            }
        }

        var name = assembly.GetName();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        var canonicalIdentity = string.Join(
            '\n',
            name.Name ?? string.Empty,
            name.Version?.ToString() ?? string.Empty,
            informationalVersion,
            assembly.ManifestModule.ModuleVersionId.ToString("N"));

        return new McpServerBuildIdentity(
            $"sha256:{Hex(SHA256.HashData(StrictUtf8.GetBytes(canonicalIdentity)))}",
            "entry-assembly-metadata-v1");
    }

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}
