using System.Security.Cryptography;
using System.Text;

namespace Pegasus.Cortex.Persistence.File.V2;

internal static class CortexFileV2AtomicIo
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<byte[]> ReadExactAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        CortexFileV2PathPolicy.RejectFileReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
            throw new InvalidDataException("A Cortex document has an invalid byte length.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A Cortex document is not strict UTF-8.", exception);
        }
        return bytes;
    }

    public static async ValueTask WriteAtomicAsync(
        string target,
        byte[] bytes,
        int maximumBytes,
        string containmentRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        _ = StrictUtf8.GetString(bytes);
        CortexFileV2PathPolicy.RejectFileReparsePoint(target);
        var directory = Path.GetDirectoryName(target) ??
            throw new InvalidDataException("The Cortex document target has no directory.");
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(directory, containmentRoot);
        var temporary = CortexFileV2PathPolicy.ValidateAtomicTemporaryPath(
            containmentRoot,
            target,
            Guid.NewGuid().ToString("N"));
        try
        {
            await WriteNewAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            CortexFileV2PathPolicy.RejectFileReparsePoint(target);
            if (System.IO.File.Exists(target))
            {
                try
                {
                    System.IO.File.Replace(temporary, target, null);
                }
                catch (PlatformNotSupportedException)
                {
                    System.IO.File.Move(temporary, target, overwrite: true);
                }
            }
            else
            {
                System.IO.File.Move(temporary, target);
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }

    public static async ValueTask WriteImmutableAsync(
        string target,
        byte[] bytes,
        int maximumBytes,
        string containmentRoot,
        CancellationToken cancellationToken)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var directory = Path.GetDirectoryName(target) ??
            throw new InvalidDataException("The Cortex object target has no directory.");
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(directory, containmentRoot);
        try
        {
            await WriteNewAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (System.IO.File.Exists(target))
        {
            var retained = await ReadExactAsync(target, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!retained.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("A content-addressed Cortex object changed bytes.");
        }
    }

    public static async ValueTask WriteCreateOnlyAtomicAsync(
        string target,
        byte[] bytes,
        int maximumBytes,
        string containmentRoot,
        CancellationToken cancellationToken)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        _ = StrictUtf8.GetString(bytes);
        CortexFileV2PathPolicy.RejectFileReparsePoint(target);
        var directory = Path.GetDirectoryName(target) ??
            throw new InvalidDataException("The Cortex document target has no directory.");
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(directory, containmentRoot);
        var temporary = CortexFileV2PathPolicy.ValidateAtomicTemporaryPath(
            containmentRoot,
            target,
            Guid.NewGuid().ToString("N"));
        try
        {
            await WriteNewAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            CortexFileV2PathPolicy.RejectFileReparsePoint(target);
            try
            {
                System.IO.File.Move(temporary, target);
            }
            catch (IOException) when (System.IO.File.Exists(target))
            {
                var retained = await ReadExactAsync(target, maximumBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (!retained.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("An immutable Cortex manifest changed bytes.");
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async ValueTask WriteNewAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
