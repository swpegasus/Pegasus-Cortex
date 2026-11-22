using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Standard PKCS#12 fixture with one matched key/certificate and one additional encrypted orphan key.
// The independent default .NET loader must first accept it and expose only one key-bearing certificate.
// This deliberately exercises a key-bag bound that cannot be tested by counting returned certificates.
internal static class OrphanKeyFixture
{
    private static readonly Asn1Tag ExplicitZero = new(TagClass.ContextSpecific, 0, true);
    internal static byte[] Create(X509Certificate2 certificate, ReadOnlySpan<char> password)
    {
        using var matched = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("fixture requires a key");
        using var orphan = RSA.Create(2048);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000);
        var firstKey = matched.ExportEncryptedPkcs8PrivateKey(password, pbe);
        var orphanKey = orphan.ExportEncryptedPkcs8PrivateKey(password, pbe);
        byte[]? bags = null, safe = null, macKey = null;
        var firstId = SHA256.HashData(certificate.RawData).AsSpan(0, 20).ToArray();
        var orphanId = RandomNumberGenerator.GetBytes(20);
        var salt = RandomNumberGenerator.GetBytes(16);
        try
        {
            var contents = new AsnWriter(AsnEncodingRules.DER);
            contents.PushSequence();
            WriteKey(contents, firstKey, firstId);
            WriteKey(contents, orphanKey, orphanId);
            contents.PushSequence();
            contents.WriteObjectIdentifier("1.2.840.113549.1.12.10.1.3"); // certBag
            contents.PushSequence(ExplicitZero);
            contents.PushSequence();
            contents.WriteObjectIdentifier("1.2.840.113549.1.9.22.1");
            contents.PushSequence(ExplicitZero); contents.WriteOctetString(certificate.RawData); contents.PopSequence(ExplicitZero);
            contents.PopSequence(); contents.PopSequence(ExplicitZero);
            WriteId(contents, firstId);
            contents.PopSequence(); contents.PopSequence();
            bags = contents.Encode();
            var authenticated = new AsnWriter(AsnEncodingRules.DER);
            authenticated.PushSequence(); WriteData(authenticated, bags); authenticated.PopSequence();
            safe = authenticated.Encode();
            // RFC 7292 Appendix B, SHA-256 MAC key, diversifier 3. Only one 32-byte block is needed.
            macKey = MacKey(password, salt, iterations: 2048);
            var mac = HMACSHA256.HashData(macKey, safe);
            try
            {
                var pfx = new AsnWriter(AsnEncodingRules.DER);
                pfx.PushSequence(); pfx.WriteInteger(3); WriteData(pfx, safe);
                pfx.PushSequence(); // MacData
                pfx.PushSequence(); // DigestInfo
                pfx.PushSequence(); pfx.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); pfx.WriteNull(); pfx.PopSequence();
                pfx.WriteOctetString(mac); pfx.PopSequence();
                pfx.WriteOctetString(salt); pfx.WriteInteger(2048); pfx.PopSequence(); pfx.PopSequence();
                return pfx.Encode();
            }
            finally { CryptographicOperations.ZeroMemory(mac); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstKey); CryptographicOperations.ZeroMemory(orphanKey);
            CryptographicOperations.ZeroMemory(firstId); CryptographicOperations.ZeroMemory(orphanId);
            CryptographicOperations.ZeroMemory(salt);
            if (bags is not null) CryptographicOperations.ZeroMemory(bags);
            if (safe is not null) CryptographicOperations.ZeroMemory(safe);
            if (macKey is not null) CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static void WriteData(AsnWriter writer, byte[] bytes)
    {
        writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.7.1");
        writer.PushSequence(ExplicitZero); writer.WriteOctetString(bytes); writer.PopSequence(ExplicitZero); writer.PopSequence();
    }
    private static void WriteKey(AsnWriter writer, byte[] encryptedKey, byte[] id)
    {
        writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.12.10.1.2");
        writer.PushSequence(ExplicitZero); writer.WriteEncodedValue(encryptedKey); writer.PopSequence(ExplicitZero);
        WriteId(writer, id); writer.PopSequence();
    }
    private static void WriteId(AsnWriter writer, byte[] id)
    {
        writer.PushSetOf(); writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.9.21");
        writer.PushSetOf(); writer.WriteOctetString(id); writer.PopSetOf(); writer.PopSequence(); writer.PopSetOf();
    }
    private static byte[] MacKey(ReadOnlySpan<char> password, byte[] salt, int iterations)
    {
        var passwordBytes = new byte[(password.Length + 1) * 2];
        for (var index = 0; index < password.Length; index++)
        {
            passwordBytes[index * 2] = (byte)(password[index] >> 8);
            passwordBytes[index * 2 + 1] = (byte)password[index];
        }
        var expandedPasswordLength = ((passwordBytes.Length + 63) / 64) * 64;
        var input = new byte[64 + 64 + expandedPasswordLength];
        Span<byte> current = stackalloc byte[32];
        Span<byte> next = stackalloc byte[32];
        try
        {
            input.AsSpan(0, 64).Fill(3);
            for (var index = 0; index < 64; index++) input[64 + index] = salt[index % salt.Length];
            for (var index = 0; index < expandedPasswordLength; index++) input[128 + index] = passwordBytes[index % passwordBytes.Length];
            SHA256.HashData(input, current);
            for (var iteration = 1; iteration < iterations; iteration++)
            {
                SHA256.HashData(current, next); next.CopyTo(current);
            }
            return current.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes); CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(current); CryptographicOperations.ZeroMemory(next);
        }
    }
}
