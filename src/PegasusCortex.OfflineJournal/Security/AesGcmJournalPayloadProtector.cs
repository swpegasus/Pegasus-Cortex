using System.Buffers;
using System.Security.Cryptography;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Security;

/// <summary>
/// Dependency-free AES-256-GCM protection for the bounded v1 journal payload. The host owns the
/// key provider and key lifecycle; this adapter owns only bounded I/O, authenticated encryption,
/// and prompt zeroing of temporary plaintext, ciphertext, nonce, and tag buffers.
/// </summary>
public sealed class AesGcmJournalPayloadProtector : IJournalPayloadProtector
{
    public const string AlgorithmId = "aes-256-gcm-v1";
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int AuthenticationTagBytes = 16;
    public const int MaximumSupportedPayloadBytes = 16 * 1024 * 1024;

    private const int MaximumAssociatedDataBytes = 64 * 1024;
    private const int InitialBufferBytes = 64 * 1024;
    private readonly IJournalPayloadKeyProvider _keyProvider;
    private readonly int _maximumPayloadBytes;

    public AesGcmJournalPayloadProtector(
        IJournalPayloadKeyProvider keyProvider,
        int maximumPayloadBytes = MaximumSupportedPayloadBytes)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
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
        using var associatedData = new SensitiveOwnedBuffer(context.AssociatedData.Span);

        using var plaintextBuffer = await ReadBoundedAsync(
            plaintext,
            _maximumPayloadBytes,
            cancellationToken).ConfigureAwait(false);
        using var key = await AcquireCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var ciphertext = ArrayPool<byte>.Shared.Rent(Math.Max(plaintextBuffer.Length, 1));
        var nonce = GC.AllocateUninitializedArray<byte>(NonceBytes);
        var authenticationTag = GC.AllocateUninitializedArray<byte>(AuthenticationTagBytes);
        try
        {
            try
            {
                RandomNumberGenerator.Fill(nonce);
                using var aes = new AesGcm(key.KeyMaterial, AuthenticationTagBytes);
                aes.Encrypt(
                    nonce,
                    plaintextBuffer.Span,
                    ciphertext.AsSpan(0, plaintextBuffer.Length),
                    authenticationTag,
                    associatedData.Span);
            }
            catch (Exception exception) when (IsCryptographicProviderFailure(exception))
            {
                throw CryptographicProviderUnavailable(key.KeyId, exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (plaintextBuffer.Length > 0)
            {
                await protectedDestination.WriteAsync(
                    ciphertext.AsMemory(0, plaintextBuffer.Length),
                    cancellationToken).ConfigureAwait(false);
            }

            return new JournalPayloadProtectionMetadata(
                AlgorithmId,
                key.KeyId,
                JournalPayloadProtectionPolicy.EncodeBase64Url(nonce),
                JournalPayloadProtectionPolicy.EncodeBase64Url(authenticationTag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            ArrayPool<byte>.Shared.Return(ciphertext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(authenticationTag);
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
        using var associatedData = new SensitiveOwnedBuffer(context.AssociatedData.Span);
        using var suite = ParseSuiteMetadata(metadata);
        using var ciphertext = await ReadBoundedAsync(
            protectedPayload,
            _maximumPayloadBytes,
            cancellationToken).ConfigureAwait(false);
        using var key = await AcquireKeyAsync(suite.KeyId, cancellationToken).ConfigureAwait(false);
        var plaintext = ArrayPool<byte>.Shared.Rent(Math.Max(ciphertext.Length, 1));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var aes = new AesGcm(key.KeyMaterial, AuthenticationTagBytes);
                aes.Decrypt(
                    suite.Nonce,
                    ciphertext.Span,
                    suite.AuthenticationTag,
                    plaintext.AsSpan(0, ciphertext.Length),
                    associatedData.Span);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            catch (AuthenticationTagMismatchException)
            {
                return false;
            }
            catch (Exception exception) when (IsCryptographicProviderFailure(exception))
            {
                throw CryptographicProviderUnavailable(key.KeyId, exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
        }
    }

    public async ValueTask UnprotectAsync(
        Stream protectedPayload,
        Stream plaintextDestination,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        ValidateStreams(protectedPayload, plaintextDestination, destinationRequired: true);
        ValidateContext(context);
        using var associatedData = new SensitiveOwnedBuffer(context.AssociatedData.Span);
        using var suite = ParseSuiteMetadata(metadata);
        using var ciphertext = await ReadBoundedAsync(
            protectedPayload,
            _maximumPayloadBytes,
            cancellationToken).ConfigureAwait(false);
        using var key = await AcquireKeyAsync(suite.KeyId, cancellationToken).ConfigureAwait(false);
        var plaintext = ArrayPool<byte>.Shared.Rent(Math.Max(ciphertext.Length, 1));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var aes = new AesGcm(key.KeyMaterial, AuthenticationTagBytes);
                aes.Decrypt(
                    suite.Nonce,
                    ciphertext.Span,
                    suite.AuthenticationTag,
                    plaintext.AsSpan(0, ciphertext.Length),
                    associatedData.Span);
            }
            catch (AuthenticationTagMismatchException)
            {
                // A tag mismatch is definitive integrity failure and remains distinguishable
                // from a missing algorithm/provider so recovery can quarantine only bad bytes.
                throw;
            }
            catch (Exception exception) when (IsCryptographicProviderFailure(exception))
            {
                throw CryptographicProviderUnavailable(key.KeyId, exception);
            }

            // No plaintext reaches the caller until AES-GCM has authenticated the entire payload.
            cancellationToken.ThrowIfCancellationRequested();
            if (ciphertext.Length > 0)
            {
                await plaintextDestination.WriteAsync(
                    plaintext.AsMemory(0, ciphertext.Length),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
        }
    }

    private async ValueTask<JournalPayloadKeyLease> AcquireCurrentKeyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var lease = await _keyProvider.AcquireCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
            return ValidateLease(lease, expectedKeyId: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalPayloadProtectionUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw KeyProviderUnavailable(expectedKeyId: null, exception);
        }
    }

    private async ValueTask<JournalPayloadKeyLease> AcquireKeyAsync(
        string keyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var lease = await _keyProvider.AcquireKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
            return ValidateLease(lease, keyId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalPayloadProtectionUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw KeyProviderUnavailable(keyId, exception);
        }
    }

    private static JournalPayloadProtectionUnavailableException KeyProviderUnavailable(
        string? expectedKeyId,
        Exception exception) =>
        new(
            "offline_journal_payload_key_provider_unavailable",
            "The host payload-key provider failed before it could supply the required key.",
            expectedKeyId,
            retryable: true,
            exception);

    private static JournalPayloadProtectionUnavailableException CryptographicProviderUnavailable(
        string keyId,
        Exception exception) =>
        new(
            "offline_journal_payload_crypto_unavailable",
            "The configured journal cryptographic provider could not complete the requested operation.",
            keyId,
            retryable: true,
            exception);

    private static bool IsCryptographicProviderFailure(Exception exception) =>
        exception is CryptographicException and not AuthenticationTagMismatchException or
            PlatformNotSupportedException;

    private static JournalPayloadKeyLease ValidateLease(
        JournalPayloadKeyLease? lease,
        string? expectedKeyId)
    {
        if (lease is not null &&
            (expectedKeyId is null || string.Equals(lease.KeyId, expectedKeyId, StringComparison.Ordinal)))
        {
            return lease;
        }

        lease?.Dispose();
        throw new JournalPayloadProtectionUnavailableException(
            "offline_journal_payload_key_provider_invalid",
            "The host payload-key provider returned no key or returned a different key identifier.",
            expectedKeyId,
            retryable: false);
    }

    private static ParsedSuiteMetadata ParseSuiteMetadata(
        JournalPayloadProtectionMetadata? metadata)
    {
        try
        {
            JournalPayloadProtectionPolicy.ValidateMetadata(metadata, maximumIdentifierCharacters: 128);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("Journal payload protection metadata is malformed.", exception);
        }

        if (!string.Equals(metadata!.Algorithm, AlgorithmId, StringComparison.Ordinal))
        {
            throw new JournalPayloadProtectionUnavailableException(
                "offline_journal_payload_suite_unavailable",
                "The retained journal payload uses a protection suite that this host has not enabled.",
                metadata.KeyId,
                retryable: true);
        }

        if (!JournalPayloadProtectionPolicy.TryDecodeCanonicalBase64Url(
                metadata.Nonce,
                NonceBytes,
                out var nonce) ||
            !JournalPayloadProtectionPolicy.TryDecodeCanonicalBase64Url(
                metadata.AuthenticationTag,
                AuthenticationTagBytes,
                out var authenticationTag))
        {
            CryptographicOperations.ZeroMemory(nonce);
            throw new InvalidDataException("Journal AES-GCM metadata is not canonical or has the wrong size.");
        }

        return new ParsedSuiteMetadata(metadata.KeyId, nonce, authenticationTag);
    }

    private static void ValidateContext(JournalPayloadProtectionContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.AssociatedData.IsEmpty || context.AssociatedData.Length > MaximumAssociatedDataBytes)
            throw new ArgumentException("Journal payload associated data is missing or unbounded.", nameof(context));
    }

    private static void ValidateStreams(
        Stream source,
        Stream destination,
        bool destinationRequired)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.CanRead)
            throw new ArgumentException("The protected-payload source must be readable.", nameof(source));
        if (destinationRequired && !destination.CanWrite)
            throw new ArgumentException("The protected-payload destination must be writable.", nameof(destination));
    }

    private static async ValueTask<SensitivePooledBuffer> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var result = new SensitivePooledBuffer(Math.Min(InitialBufferBytes, maximumBytes));
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
                        if (await source.ReadAsync(overflow, cancellationToken).ConfigureAwait(false) != 0)
                            throw new InvalidDataException("The journal payload exceeds the configured AES-GCM bound.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(overflow);
                    }
                    break;
                }

                result.EnsureWritableCapacity(maximumBytes);
                var writable = Math.Min(result.Capacity, maximumBytes) - result.Length;
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
        public Memory<byte> WritableMemory => GetBuffer().AsMemory(Length);

        public void EnsureWritableCapacity(int maximumBytes)
        {
            var current = GetBuffer();
            if (Length < Math.Min(current.Length, maximumBytes))
                return;
            var nextCapacity = Math.Min(maximumBytes, checked(Math.Max(current.Length * 2, Length + 1)));
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
            _buffer ?? throw new ObjectDisposedException(nameof(SensitivePooledBuffer));
    }

    private sealed class SensitiveOwnedBuffer : IDisposable
    {
        private byte[]? _buffer;

        public SensitiveOwnedBuffer(ReadOnlySpan<byte> source) => _buffer = source.ToArray();

        public ReadOnlySpan<byte> Span =>
            _buffer ?? throw new ObjectDisposedException(nameof(SensitiveOwnedBuffer));

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
                CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private sealed class ParsedSuiteMetadata(
        string keyId,
        byte[] nonce,
        byte[] authenticationTag) : IDisposable
    {
        public string KeyId { get; } = keyId;
        public ReadOnlySpan<byte> Nonce => nonce;
        public ReadOnlySpan<byte> AuthenticationTag => authenticationTag;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(authenticationTag);
        }
    }
}
