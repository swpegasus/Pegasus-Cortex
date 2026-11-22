using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Runtime.Versioning;

namespace Pegasus.AgentOps.Certificates;

public sealed record CertificateExportResult(string CertificateId, string Format, long Bytes);

/// <summary>Explicit import/export only. Imports are ephemeral and never add a certificate to an OS store.</summary>
public static class CertificateExchange
{
    private static readonly Pkcs12LoaderLimits ProtectedInputLimits = new(Pkcs12LoaderLimits.Defaults)
    {
        MaxKeys = 1,
        MaxCertificates = 16
    };

    public static X509Certificate2 ImportPublic(ReadOnlySpan<byte> input, PublicCertificateFormat format)
    {
        if (input.Length is < 1 or > CertificateMaterial.MaximumCertificateBytes)
            throw new CertificateOperationException("certificate_input_bound");
        byte[]? decoded = null;
        char[]? pemCharacters = null;
        try
        {
            if (format == PublicCertificateFormat.Pem)
            {
                // Rejected PEM may contain a private key. Keep the full input in clearable storage, never a string.
                pemCharacters = new char[input.Length];
                var length = new UTF8Encoding(false, true).GetChars(input, pemCharacters);
                var pem = pemCharacters.AsSpan(0, length);
                if (!PemEncoding.TryFind(pem, out var fields) || !pem[fields.Label].SequenceEqual("CERTIFICATE".AsSpan()) ||
                    !OnlyWhitespace(pem[..fields.Location.Start.GetOffset(pem.Length)]) ||
                    !OnlyWhitespace(pem[fields.Location.End.GetOffset(pem.Length)..]))
                    throw new CertificateOperationException("public_certificate_only");
                decoded = new byte[fields.DecodedDataLength];
                if (!Convert.TryFromBase64Chars(pem[fields.Base64Data], decoded, out var written) || written != decoded.Length)
                    throw new CertificateOperationException("certificate_import_invalid");
            }
            else if (format == PublicCertificateFormat.Der) decoded = input.ToArray();
            else throw new CertificateOperationException("public_format_unsupported");
            // Require exactly one DER certificate; do not accept a PFX/PKCS#7 or appended material.
            var reader = new AsnReader(decoded, AsnEncodingRules.DER);
            _ = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            var certificate = X509CertificateLoader.LoadCertificate(decoded);
            if (certificate.HasPrivateKey) { certificate.Dispose(); throw new CertificateOperationException("public_certificate_only"); }
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or AsnContentException or FormatException or DecoderFallbackException)
        { throw new CertificateOperationException("certificate_import_invalid", error); }
        finally
        {
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
            if (pemCharacters is not null) Array.Clear(pemCharacters);
        }
    }

    public static CertificateExportResult ExportProtectedPkcs12(CertificateKeyReference key,
        ICertificateKeyResolver keys, string passwordReference, ICertificateSecretResolver secrets, Stream destination)
    {
        CertificateMaterial.RequireKeyReference(key);
        CertificateMaterial.RequireReference(passwordReference);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new CertificateOperationException("output_unwritable");
        using var certificate = keys.Resolve(key);
        CertificateMaterial.RequireIdentity(certificate, key.ExpectedCertificateId);
        if (!certificate.HasPrivateKey) throw new CertificateOperationException("export_private_key_required");
        using var password = secrets.Resolve(passwordReference);
        byte[]? protectedBytes = null;
        try
        {
            try
            {
                // Generation has no destination effects. Failures here cannot be mistaken for partial output.
                // .NET 10's documented explicit-PBE export takes string, with no TryExportPkcs12/span overload.
                // This bounded call-only copy cannot be zeroed; never retain it in a field, process argument or receipt.
                // The owned resolver buffer and encoded output are still cleared by their enclosing finally paths.
                protectedBytes = certificate.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000), new string(password.Value));
            }
            catch (CryptographicException error) { throw new CertificateOperationException("private_key_export_failed", error); }
            if (protectedBytes.Length > CertificateMaterial.MaximumCertificateBytes)
                throw new CertificateOperationException("certificate_output_bound");
            var result = new CertificateExportResult(CertificateMaterial.Id(certificate), "pkcs12-protected", protectedBytes.Length);
            try { destination.Write(protectedBytes); }
            catch (Exception error)
            {
                // A provider may throw any exception after accepting a prefix. Do not authorize replay.
                throw new CertificateOperationException("certificate_export_output_unknown", error);
            }
            return result;
        }
        finally { if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    /// <summary>Returns one owned ephemeral private-key certificate. No PersistKeySet or OS-store write occurs.</summary>
    public static X509Certificate2 ImportProtectedPkcs12(Stream source, string passwordReference,
        ICertificateSecretResolver secrets, string expectedCertificateId)
    {
        CertificateMaterial.RequireReference(passwordReference);
        CertificateMaterial.RequireId(expectedCertificateId);
        ArgumentNullException.ThrowIfNull(secrets);
        using var password = secrets.Resolve(passwordReference);
        byte[] protectedBytes;
        try { protectedBytes = CertificateMaterial.ReadBounded(source, CertificateMaterial.MaximumCertificateBytes); }
        catch (IOException error) { throw new CertificateOperationException("certificate_import_read_failed", error); }
        X509Certificate2Collection? certificates = null;
        X509Certificate2? returned = null;
        try
        {
            RequireProtectedPkcs12(protectedBytes);
            // Count key bags in the loader, including orphan keys without a returned certificate.
            // Clone defaults so KDF/iteration ceilings stay intact while key/certificate limits tighten.
            certificates = X509CertificateLoader.LoadPkcs12Collection(protectedBytes, password.Value,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable, ProtectedInputLimits);
            if (certificates.Count is < 1 or > 16 || certificates.Cast<X509Certificate2>().Count(value => value.HasPrivateKey) != 1)
                throw new CertificateOperationException("pkcs12_single_key_required");
            var selected = certificates.Cast<X509Certificate2>().Single(value => value.HasPrivateKey);
            CertificateMaterial.RequireIdentity(selected, expectedCertificateId);
            returned = selected;
            return selected;
        }
        catch (CryptographicException error) { throw new CertificateOperationException("pkcs12_import_invalid", error); }
        finally
        {
            if (certificates is not null)
                foreach (var certificate in certificates) if (!ReferenceEquals(certificate, returned)) certificate.Dispose();
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    /// <summary>
    /// Read a bounded protected input from the exact admitted local file. The
    /// execution owner admits the root/identity; this primitive never changes
    /// an OS store or persists the returned private key. Caller owns the result.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2 ImportProtectedPkcs12File(string admittedRoot, string absolutePath,
        string expectedContentSha256, string passwordReference, ICertificateSecretResolver secrets,
        string expectedCertificateId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        CertificateMaterial.RequireId(expectedContentSha256);
        CertificateMaterial.RequireId(expectedCertificateId);
        CertificateMaterial.RequireReference(passwordReference);
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new AuthenticodeFileLease(admittedRoot, absolutePath, writable: false);
        var length = RandomAccess.GetLength(file.Handle);
        if (length is < 1 or > CertificateMaterial.MaximumCertificateBytes)
            throw new CertificateOperationException("certificate_input_bound");
        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(file.Handle, bytes.AsSpan(offset), offset);
                if (read == 0) throw new CertificateOperationException("certificate_input_changed");
                offset += read;
            }
            file.RequireIdentity(file.Handle);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedContentSha256)
                throw new CertificateOperationException("certificate_preimage_changed");
            cancellationToken.ThrowIfCancellationRequested();
            using var input = new MemoryStream(bytes, writable: false);
            return ImportProtectedPkcs12(input, passwordReference, secrets, expectedCertificateId);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireProtectedPkcs12(byte[] bytes)
    {
        byte[]? authenticatedSafeBytes = null;
        try
        {
            var outer = new AsnReader(bytes, AsnEncodingRules.BER);
            var pfx = outer.ReadSequence();
            if (pfx.ReadInteger() != 3) throw new CertificateOperationException("pkcs12_protection_required");
            var authSafe = pfx.ReadSequence();
            if (authSafe.ReadObjectIdentifier() != "1.2.840.113549.1.7.1") throw new CertificateOperationException("pkcs12_protection_required");
            var wrapped = authSafe.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            authenticatedSafeBytes = wrapped.ReadOctetString();
            wrapped.ThrowIfNotEmpty(); authSafe.ThrowIfNotEmpty();
            // Require a MAC as well as encrypted key material; the .NET loader verifies it and bounds KDF work.
            if (!pfx.HasData) throw new CertificateOperationException("pkcs12_protection_required");
            _ = pfx.ReadSequence(); pfx.ThrowIfNotEmpty(); outer.ThrowIfNotEmpty();
            var safeReader = new AsnReader(authenticatedSafeBytes, AsnEncodingRules.BER);
            var safe = safeReader.ReadSequence();
            var protectedKey = false;
            var entries = 0;
            while (safe.HasData)
            {
                if (++entries > 16) throw new CertificateOperationException("pkcs12_input_bound");
                var entry = safe.ReadSequence();
                var kind = entry.ReadObjectIdentifier();
                if (kind == "1.2.840.113549.1.7.6") protectedKey = true; // encrypted SafeContents
                else if (kind == "1.2.840.113549.1.7.1")
                {
                    var context = entry.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                    var bagBytes = context.ReadOctetString();
                    try
                    {
                        var bagsReader = new AsnReader(bagBytes, AsnEncodingRules.BER);
                        var bags = bagsReader.ReadSequence();
                        var bagCount = 0;
                        while (bags.HasData)
                        {
                            if (++bagCount > 32) throw new CertificateOperationException("pkcs12_input_bound");
                            var bag = bags.ReadSequence();
                            var bagKind = bag.ReadObjectIdentifier();
                            if (bagKind == "1.2.840.113549.1.12.10.1.1") throw new CertificateOperationException("pkcs12_protection_required");
                            if (bagKind == "1.2.840.113549.1.12.10.1.2") protectedKey = true;
                        }
                        bagsReader.ThrowIfNotEmpty(); context.ThrowIfNotEmpty();
                    }
                    finally { CryptographicOperations.ZeroMemory(bagBytes); }
                }
            }
            safeReader.ThrowIfNotEmpty();
            if (!protectedKey) throw new CertificateOperationException("pkcs12_protection_required");
        }
        catch (AsnContentException error) { throw new CertificateOperationException("pkcs12_import_invalid", error); }
        finally { if (authenticatedSafeBytes is not null) CryptographicOperations.ZeroMemory(authenticatedSafeBytes); }
    }

    private static bool OnlyWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text) if (!char.IsWhiteSpace(character)) return false;
        return true;
    }
}
