using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Security;

/// <summary>
/// Preserves already-human-readable recovery bytes exactly inside an
/// owner-private local journal and binds them to their manifest context with
/// SHA-256. This profile provides corruption/tamper detection, not encryption
/// or protection from a malicious process running as the same OS user.
/// </summary>
public sealed class ExactCopyJournalPayloadProtector : IJournalPayloadProtector
{
    public const string AlgorithmId = "sha256-exact-copy-v1";
    public const string BoundaryKeyId = "local-os-boundary";
    public const string NonceMarker = "none";
    public const int MaximumSupportedPayloadBytes =
        AesGcmJournalPayloadProtector.MaximumSupportedPayloadBytes;

    private static readonly byte[] DomainSeparator =
        Encoding.ASCII.GetBytes("pegasus.cortex.exact-copy/1.0\0");
    private const int MaximumAssociatedDataBytes = 64 * 1024;
    private const int InitialBufferBytes = 64 * 1024;
    private readonly int _maximumPayloadBytes;

    public ExactCopyJournalPayloadProtector(
        int maximumPayloadBytes = MaximumSupportedPayloadBytes)
    {
        if (maximumPayloadBytes is < 1 or > MaximumSupportedPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public async ValueTask<JournalPayloadProtectionMetadata> ProtectAsync(
        Stream plaintext,
        Stream protectedDestination,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        ValidateStreams(plaintext, protectedDestination, destinationRequired: true);
        ValidateContext(context);
        using var payload = await ReadBoundedAsync(
            plaintext,
            _maximumPayloadBytes,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length != 0)
        {
            await protectedDestination.WriteAsync(
                payload.Memory,
                cancellationToken).ConfigureAwait(false);
        }

        var tag = ComputeTag(context.AssociatedData.Span, payload.Span);
        try
        {
            return new JournalPayloadProtectionMetadata(
                AlgorithmId,
                BoundaryKeyId,
                NonceMarker,
                JournalPayloadProtectionPolicy.EncodeBase64Url(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public async ValueTask<bool> VerifyAsync(
        Stream protectedPayload,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        ValidateStreams(protectedPayload, Stream.Null, destinationRequired: false);
        ValidateContext(context);
        var expectedTag = ParseMetadata(metadata);
        try
        {
            using var payload = await ReadBoundedAsync(
                protectedPayload,
                _maximumPayloadBytes,
                cancellationToken).ConfigureAwait(false);
            var observedTag = ComputeTag(context.AssociatedData.Span, payload.Span);
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    expectedTag,
                    observedTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observedTag);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedTag);
        }
    }

    public async ValueTask UnprotectAsync(
        Stream protectedPayload,
        Stream plaintextDestination,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        ValidateStreams(
            protectedPayload,
            plaintextDestination,
            destinationRequired: true);
        ValidateContext(context);
        var expectedTag = ParseMetadata(metadata);
        try
        {
            using var payload = await ReadBoundedAsync(
                protectedPayload,
                _maximumPayloadBytes,
                cancellationToken).ConfigureAwait(false);
            var observedTag = ComputeTag(context.AssociatedData.Span, payload.Span);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        expectedTag,
                        observedTag))
                {
                    throw new CryptographicException(
                        "The exact-copy recovery payload failed its integrity check.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observedTag);
            }

            // The complete retained payload is verified before any source bytes
            // are released to the destination.
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length != 0)
            {
                await plaintextDestination.WriteAsync(
                    payload.Memory,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedTag);
        }
    }

    private static byte[] ComputeTag(
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        hash.AppendData(DomainSeparator);
        BinaryPrimitives.WriteInt32BigEndian(length, associatedData.Length);
        hash.AppendData(length);
        hash.AppendData(associatedData);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static byte[] ParseMetadata(
        JournalPayloadProtectionMetadata? metadata)
    {
        JournalPayloadProtectionPolicy.ValidateMetadata(
            metadata,
            maximumIdentifierCharacters: 128);
        if (!string.Equals(metadata!.Algorithm, AlgorithmId, StringComparison.Ordinal) ||
            !string.Equals(metadata.KeyId, BoundaryKeyId, StringComparison.Ordinal) ||
            !string.Equals(metadata.Nonce, NonceMarker, StringComparison.Ordinal))
        {
            throw new JournalPayloadProtectionUnavailableException(
                "offline_journal_exact_copy_suite_unavailable",
                "The retained recovery payload does not use the enabled exact-copy integrity suite.",
                metadata.KeyId,
                retryable: false);
        }
        if (!JournalPayloadProtectionPolicy.TryDecodeCanonicalBase64Url(
                metadata.AuthenticationTag,
                SHA256.HashSizeInBytes,
                out var tag))
        {
            throw new InvalidDataException(
                "The exact-copy recovery integrity metadata is malformed.");
        }
        return tag;
    }

    private static void ValidateContext(
        JournalPayloadProtectionContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.AssociatedData.IsEmpty ||
            context.AssociatedData.Length > MaximumAssociatedDataBytes)
        {
            throw new ArgumentException(
                "Journal payload associated data is missing or unbounded.",
                nameof(context));
        }
    }

    private static void ValidateStreams(
        Stream source,
        Stream destination,
        bool destinationRequired)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.CanRead)
            throw new ArgumentException(
                "The exact-copy payload source must be readable.",
                nameof(source));
        if (destinationRequired && !destination.CanWrite)
        {
            throw new ArgumentException(
                "The exact-copy payload destination must be writable.",
                nameof(destination));
        }
    }

    private static async ValueTask<SensitivePooledBuffer> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var result = new SensitivePooledBuffer(
            Math.Min(InitialBufferBytes, maximumBytes));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Length == maximumBytes)
                {
                    var overflow = new byte[1];
                    try
                    {
                        if (await source.ReadAsync(
                                overflow,
                                cancellationToken).ConfigureAwait(false) != 0)
                        {
                            throw new InvalidDataException(
                                "The exact-copy payload exceeds its configured bound.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(overflow);
                    }
                    break;
                }

                result.EnsureWritableCapacity(maximumBytes);
                var writable = Math.Min(result.Capacity, maximumBytes) -
                    result.Length;
                var read = await source.ReadAsync(
                    result.WritableMemory[..writable],
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                result.Advance(read);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private sealed class SensitivePooledBuffer : IDisposable
    {
        private byte[]? _buffer;

        public SensitivePooledBuffer(int initialCapacity) =>
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 1));

        public int Length { get; private set; }
        public int Capacity => GetBuffer().Length;
        public ReadOnlySpan<byte> Span => GetBuffer().AsSpan(0, Length);
        public ReadOnlyMemory<byte> Memory => GetBuffer().AsMemory(0, Length);
        public Memory<byte> WritableMemory => GetBuffer().AsMemory(Length);

        public void EnsureWritableCapacity(int maximumBytes)
        {
            var current = GetBuffer();
            if (Length < Math.Min(current.Length, maximumBytes))
                return;
            var nextCapacity = Math.Min(
                maximumBytes,
                checked(Math.Max(current.Length * 2, Length + 1)));
            var replacement = ArrayPool<byte>.Shared.Rent(nextCapacity);
            current.AsSpan(0, Length).CopyTo(replacement);
            CryptographicOperations.ZeroMemory(current);
            ArrayPool<byte>.Shared.Return(current);
            _buffer = replacement;
        }

        public void Advance(int count)
        {
            if (count < 0 || Length > Capacity - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            Length += count;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null)
                return;
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        private byte[] GetBuffer() =>
            _buffer ??
            throw new ObjectDisposedException(nameof(SensitivePooledBuffer));
    }
}
