using System.Security.Cryptography;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>Immutable local files below the database's existing security boundary.</summary>
internal sealed class SqliteCortexV2ColdPayloadFiles : ISqliteCortexColdPayloadStore
{
    private readonly string _root;
    internal SqliteCortexV2ColdPayloadFiles(SqliteCortexV2StoreOptions options) =>
        _root = Path.Combine(Path.GetDirectoryName(options.DatabasePath)!, "cold-v1");

    public async ValueTask<byte[]> ReadAsync(SqliteCortexColdPayload payload, CancellationToken cancellationToken)
    {
        var path = PathFor(payload);
        await using var input = Open(path);
        return await ReadVerifiedAsync(input, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IAsyncDisposable> PublishVerifiedAsync(SqliteCortexColdPayload payload,
        ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken)
    {
        var path = PathFor(payload);
        if (compressed.Length != payload.CompressedLength || Hash(compressed.Span) != payload.CompressedSha256)
            throw new InvalidDataException("The cold publication bytes do not match their immutable descriptor.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireOrdinary(_root);
        Directory.CreateDirectory(_root);
        RequireOrdinary(_root);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        RequireOrdinary(parent);
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(parent, ".p-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(compressed, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                RequireOrdinary(parent);
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* Recheck exact immutable winner below. */ }
            }
            finally
            {
                // Only our unpublished random temporary is eligible for cleanup.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        var lease = Open(path);
        try
        {
            _ = await ReadVerifiedAsync(lease, payload, cancellationToken).ConfigureAwait(false);
            return lease; // No write/delete sharing until the database commit has settled.
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private string PathFor(SqliteCortexColdPayload payload)
    {
        if (payload.FormatVersion != 1 || !IsHash(payload.ContentHash) || !IsHash(payload.CompressedSha256) ||
            payload.OriginalLength is < 0 or > 16 * 1024 * 1024 ||
            payload.CompressedLength is < 1 or > 16 * 1024 * 1024 + 65536)
            throw new InvalidDataException("The cold payload descriptor is invalid.");
        var path = Path.Combine(_root, payload.CompressedSha256[..2], payload.CompressedSha256 + ".br");
        if (path.Length > 240) throw new IOException("The cold payload path exceeds the product path budget.");
        RequireOrdinary(path);
        return path;
    }

    private static FileStream Open(string path)
    {
        RequireOrdinary(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async ValueTask<byte[]> ReadVerifiedAsync(FileStream input, SqliteCortexColdPayload payload,
        CancellationToken cancellationToken)
    {
        if (input.Length != payload.CompressedLength)
            throw new InvalidDataException("The cold payload length differs from its retained identity.");
        var bytes = new byte[payload.CompressedLength];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (input.Position != input.Length || Hash(bytes) != payload.CompressedSha256)
            throw new InvalidDataException("The cold payload failed its retained hash.");
        return bytes;
    }

    private static void RequireOrdinary(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cold payload paths cannot cross links or reparse points.");
            }
            else if (new FileInfo(current).LinkTarget is not null)
                throw new IOException("Cold payload paths cannot cross links.");
        }
    }

    internal static bool IsHash(string value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
