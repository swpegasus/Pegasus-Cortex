using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.RootedIo;

namespace Pegasus.Cortex.Persistence.File.V2;

internal static class CortexFileV2AtomicIo
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<byte[]> ReadExactAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        CortexFileV2PathPolicy.RejectFileReparsePoint(path);
        using var directory = RootedDirectory.OpenRoot(Parent(path));
        using var file = directory.OpenRead(Path.GetFileName(path));
        return await ReadExactAsync(file, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask WriteAtomicAsync(string target, byte[] bytes, int maximumBytes,
        string containmentRoot, CancellationToken cancellationToken) =>
        WriteTemporaryAsync(target, bytes, maximumBytes, containmentRoot, createOnly: false, cancellationToken);

    public static async ValueTask WriteImmutableAsync(string target, byte[] bytes, int maximumBytes,
        string containmentRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 || bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(Parent(target), containmentRoot);
        using var directory = RootedDirectory.OpenRoot(Parent(target));
        RootedDirectory.RootedFile? created = null;
        try
        {
            created = directory.CreateNew(Path.GetFileName(target));
            try
            {
                await WriteNewAsync(created, bytes, cancellationToken).ConfigureAwait(false);
                created.RequireNamedIdentity();
            }
            catch (Exception original)
            {
                try { created.DeleteIfOwned(); }
                catch (Exception cleanup)
                { throw new IOException("Immutable Cortex creation failed and owned cleanup was incomplete.", new AggregateException(original, cleanup)); }
                throw;
            }
        }
        catch (IOException error) when (created is null && RootedDirectory.IsCollision(error))
        {
            using var retained = directory.OpenRead(Path.GetFileName(target));
            var actual = await ReadExactAsync(retained, maximumBytes, cancellationToken).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("A content-addressed Cortex object changed bytes.");
        }
        finally { created?.Dispose(); }
    }

    public static ValueTask WriteCreateOnlyAtomicAsync(string target, byte[] bytes, int maximumBytes,
        string containmentRoot, CancellationToken cancellationToken) =>
        WriteTemporaryAsync(target, bytes, maximumBytes, containmentRoot, createOnly: true, cancellationToken);

    private static async ValueTask WriteTemporaryAsync(string target, byte[] bytes, int maximumBytes,
        string containmentRoot, bool createOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 || bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _ = StrictUtf8.GetString(bytes);
        CortexFileV2PathPolicy.RejectFileReparsePoint(target);
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(Parent(target), containmentRoot);
        var temporaryPath = CortexFileV2PathPolicy.ValidateAtomicTemporaryPath(containmentRoot, target, Guid.NewGuid().ToString("N"));
        using var directory = RootedDirectory.OpenRoot(Parent(target));
        using var temporary = directory.CreateNew(Path.GetFileName(temporaryPath));
        Exception? primary = null;
        try
        {
            await WriteNewAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { temporary.Publish(Path.GetFileName(target), replace: !createOnly); }
            catch (IOException error) when (createOnly && RootedDirectory.IsCollision(error))
            {
                using var retained = directory.OpenRead(Path.GetFileName(target));
                var actual = await ReadExactAsync(retained, maximumBytes, cancellationToken).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("An immutable Cortex manifest changed bytes.");
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (!temporary.WasPublished)
                try { temporary.DeleteIfOwned(); }
                catch (Exception cleanup) when (primary is not null)
                { throw new IOException("Cortex document publication failed and owned temporary cleanup was incomplete.", new AggregateException(primary, cleanup)); }
        }
    }

    internal static FileStream OpenWriteLease(string path)
    {
        CortexFileV2PathPolicy.RejectFileReparsePoint(path);
        using var directory = RootedDirectory.OpenRoot(Parent(path));
        using var file = directory.OpenLock(Path.GetFileName(path));
        return file.OpenStream(); // The stream retains its handle and the admitted ancestor chain.
    }

    private static string Parent(string path) => Path.GetDirectoryName(path)
        ?? throw new InvalidDataException("The Cortex document has no parent directory.");

    private static async ValueTask<byte[]> ReadExactAsync(RootedDirectory.RootedFile file, int maximumBytes, CancellationToken token)
    {
        await using var stream = file.OpenStream();
        if (stream.Length is < 1 || stream.Length > maximumBytes) throw new InvalidDataException("A Cortex document has an invalid byte length.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try { _ = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("A Cortex document is not strict UTF-8.", error); }
        return bytes;
    }

    private static async ValueTask WriteNewAsync(RootedDirectory.RootedFile file, byte[] bytes, CancellationToken token)
    {
        await using var stream = file.OpenStream();
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
