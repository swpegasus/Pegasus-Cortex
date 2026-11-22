using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>Bounded local metadata snapshots; callers retain path, ACL and content authority.</summary>
internal static class CortexLocalBoundedMetadataReader
{
    internal static async ValueTask<byte[]> ReadAsync(
        string path, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Local metadata must be an ordinary file.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadStreamAsync(input, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadStreamAsync(
        Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maximumBytes < 2) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        // The allocation is authorized by the same opened handle that supplies
        // the canonical metadata, not by an earlier path-based observation.
        var length = input.Length;
        if (length is < 2 || length > maximumBytes)
            throw new InvalidDataException("Local metadata is outside its byte limit.");
        var bytes = new byte[checked((int)length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 || input.Length != length)
            throw new InvalidDataException("Local metadata changed during its bounded read.");
        return bytes;
    }

}
