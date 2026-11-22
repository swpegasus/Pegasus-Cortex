using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Certificates;

// These are Windows Authenticode/SIP operations. No detached signature is authored.
internal static class AuthenticodeNative
{
    internal enum InspectionStage
    {
        NotStarted, PreIdentity, Container, SubjectGuid, SubjectSetup,
        SignedMessageSize, SignedMessageBytes, PeCertificatePadding, SignedEnvelope,
        MessageSignatureSize, MessageSignatureBytes, SignerCertificate,
        ExpectedIdentity, SigningPurpose, IndirectStructure, IndirectNativeSize,
        IndirectNativeBytes, IndirectHashProvider, IndirectDigest, Complete, PostIdentity
    }
    internal enum Ps1SignatureStructure { UnrecognizedEncoding, Absent, Present, Malformed }
    private const uint Encoding = 0x10001, Sha256 = 0x800c;
    private const int MaximumSignatureBytes = 4 * 1024 * 1024;
    private const string IndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    private static readonly Guid TrustAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    internal static bool SigningAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
            if (!NativeLibrary.TryLoad("mssign32.dll", typeof(AuthenticodeNative).Assembly, DllImportSearchPath.System32, out var module)) return false;
            try { return NativeLibrary.TryGetExport(module, "SignerSignEx3", out _); }
            finally { NativeLibrary.Free(module); }
        }
    }

    internal static int Sign(AuthenticodeFileLease file, X509Certificate2 certificate, string certificateId,
        CancellationToken token, Action issued)
    {
        nint key = 0;
        uint keySpec = uint.MaxValue;
        var acquired = false;
        var freeKey = false;
        try
        {
            // .NET ephemeral certificates own an already-attached CNG handle.
            // Reading fixed property 78 neither opens a provider nor transfers
            // ownership: the retained certificate must outlive every use.
            var keyBytes = checked((uint)IntPtr.Size);
            if (CertGetCertificateContextProperty(certificate.Handle, 78, out key, ref keyBytes))
            {
                if (keyBytes != checked((uint)IntPtr.Size) || key == 0 || key == new nint(-1))
                    throw new AuthenticodeAttemptException("authenticode_attached_key_invalid", false, false, false, null);
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                if (unchecked((uint)error) != 0x80092004)
                    throw new AuthenticodeAttemptException("authenticode_attached_key_unavailable", false, false, false, error);
                // Only genuine property absence permits the original silent
                // acquisition. NO_HEALING prohibits provider-property repair.
                if (!CryptAcquireCertificatePrivateKey(certificate.Handle, 0x40u | 0x4u | 0x40000u | 0x8u, 0,
                        out key, out keySpec, out freeKey))
                    throw new AuthenticodeAttemptException("authenticode_silent_key_unavailable", false, false, false, Marshal.GetLastWin32Error());
                acquired = true;
            }
            if (key == 0 || key == new nint(-1) || keySpec != uint.MaxValue) throw new CertificateOperationException("authenticode_cng_key_required");
            using var publicKey = certificate.GetRSAPublicKey() ?? throw new CertificateOperationException("rsa_key_required");
            using var store = OpenMemoryStore();
            if (!CertAddCertificateContextToStore(store.Handle, certificate.Handle, 4, 0)) throw NativeFailure();
            using var name = Allocation.Utf16(file.Path);
            using var index = Allocation.Value(0u);
            using var fileInfo = Allocation.Value(new SignerFileInfo { Size = Size<SignerFileInfo>(), Name = name.Pointer, File = file.Handle.DangerousGetHandle() });
            using var storeInfo = Allocation.Value(new SignerStoreInfo { Size = Size<SignerStoreInfo>(), Certificate = certificate.Handle, Policy = 1, Store = store.Handle });
            var subject = new SignerSubjectInfo { Size = Size<SignerSubjectInfo>(), Index = index.Pointer, Choice = 1, Info = fileInfo.Pointer };
            var signer = new SignerCert { Size = Size<SignerCert>(), Choice = 2, Info = storeInfo.Pointer };
            var signature = new SignerSignatureInfo { Size = Size<SignerSignatureInfo>(), Algorithm = Sha256 };
            DigestSign callback = (nint selected, nint metadata, uint algorithm, nint digest, uint length, nint handle, out Blob signed) =>
            {
                signed = default;
                nint allocation = 0;
                byte[]? bytes = null;
                byte[]? result = null;
                try
                {
                    if (token.IsCancellationRequested) return unchecked((int)0x80004004);
                    if (algorithm != Sha256 || length != 32 || digest == 0 || selected == 0) return unchecked((int)0x80070057);
                    using var borrowed = new SafeFileHandle(handle, ownsHandle: false);
                    file.RequireIdentity(borrowed);
                    using var selectedCertificate = ReadCertificate(selected);
                    CertificateMaterial.RequireIdentity(selectedCertificate, certificateId);
                    bytes = new byte[32]; Marshal.Copy(digest, bytes, 0, bytes.Length);
                    using var algorithmName = Allocation.Utf16("SHA256");
                    var padding = new Pkcs1Padding { Algorithm = algorithmName.Pointer };
                    var status = NCryptSignHash(key, ref padding, bytes, 32, null, 0, out var needed, 2u | 0x40u);
                    if (status != 0 || needed is < 1 or > 512) return status != 0 ? status : unchecked((int)0x80070057);
                    result = new byte[needed];
                    status = NCryptSignHash(key, ref padding, bytes, 32, result, needed, out var written, 2u | 0x40u);
                    if (status != 0) return status;
                    if (written != needed || !publicKey.VerifyHash(bytes, result, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        return unchecked((int)0x80090006);
                    // The SignerSignEx3 callback contract transfers a process-heap blob.
                    allocation = HeapAlloc(GetProcessHeap(), 0, checked((nuint)result.Length));
                    if (allocation == 0) return unchecked((int)0x8007000e);
                    Marshal.Copy(result, 0, allocation, result.Length);
                    signed = new Blob { Length = checked((uint)result.Length), Data = allocation };
                    allocation = 0;
                    return 0;
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { return unchecked((int)0x80004005); }
                finally
                {
                    if (allocation != 0) _ = HeapFree(GetProcessHeap(), 0, allocation);
                    if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                    if (result is not null) CryptographicOperations.ZeroMemory(result);
                }
            };
            var digestInfo = new DigestSignInfo { Size = Size<DigestSignInfo>(), Choice = 2, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
            nint context = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                file.RequireIdentity(file.Handle);
                issued();
                // STORE only embeds our memory-store certificate. No chain discovery,
                // TSA URL, prompt window, trust insertion or provider fallback is requested.
                var flags = 0x800u | (Path.GetExtension(file.Path).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ? 0u : 0x10u);
                return SignerSignEx3(flags, ref subject, ref signer, ref signature, 0, 0, 0, 0, 0, 0,
                    out context, 0, ref digestInfo, 0);
            }
            finally
            {
                if (context != 0) _ = SignerFreeSignerContext(context);
                GC.KeepAlive(callback);
            }
        }
        finally
        {
            if (acquired && freeKey && key != 0 && key != new nint(-1)) _ = NCryptFreeObject(key);
            GC.KeepAlive(certificate);
        }
    }

    internal static AuthenticodeVerificationResult Inspect(AuthenticodeFileLease file, string? expectedCertificateId)
        => Inspect(file, expectedCertificateId, out _);

    internal static AuthenticodeVerificationResult Inspect(AuthenticodeFileLease file, string? expectedCertificateId,
        out InspectionStage stage)
    {
        stage = InspectionStage.PreIdentity;
        try
        {
            file.RequireIdentity(file.Handle);
            stage = InspectionStage.Container;
            if (!SupportedContainer(file, out var unsignedEmbedded, out var pePayloadBytes)) return Result("unsupported", null, null);
            if (unsignedEmbedded)
            {
                // This adapter inspects embedded signatures, not catalog membership.
                // An absent PE table or recognized PS1 text without signature
                // markers proves absence; no native error is reinterpreted.
                return Result("not_signed", null, null);
            }
            stage = InspectionStage.SubjectGuid;
            if (!CryptSIPRetrieveSubjectGuid(file.Path, file.Handle, out var guid))
                return Result("unsupported", null, Marshal.GetLastWin32Error());
            stage = InspectionStage.SubjectSetup;
            using var subjectType = Allocation.Value(guid);
            using var name = Allocation.Utf16(file.Path);
            // Filename-only SIP reopening conflicts with our retained write
            // lease (observed ERROR_SHARING_VIOLATION). Use the admitted handle
            // throughout inspection to preserve the write/delete-sharing fence.
            var subject = new SipSubject { Size = Size<SipSubject>(), SubjectType = subjectType.Pointer,
                File = file.Handle.DangerousGetHandle(),
                Name = name.Pointer, Encoding = Encoding };
            uint count = 0;
            stage = InspectionStage.SignedMessageSize;
            var found = CryptSIPGetSignedDataMsg(ref subject, out var encoding, 0, ref count, null);
            var error = Marshal.GetLastWin32Error();
            if (!found && IsUnsigned(error)) return Result("not_signed", null, error);
            if (!found && error != 122 && error != 234) return Result(IsUnsupported(error) ? "unsupported" : "invalid", null, error);
            if (count is < 1 or > MaximumSignatureBytes) return Result("unsupported", null, null);
            var message = new byte[count];
            stage = InspectionStage.SignedMessageBytes;
            if (!CryptSIPGetSignedDataMsg(ref subject, out encoding, 0, ref count, message) || count == 0 || count > message.Length)
                return Result("invalid", null, Marshal.GetLastWin32Error());
            if (count != message.Length) Array.Resize(ref message, checked((int)count));
            stage = InspectionStage.SignedEnvelope;
            if ((encoding & 0x10000) == 0) return Result("unsupported", null, null);
            if (pePayloadBytes is { } payloadBytes)
            {
                stage = InspectionStage.PeCertificatePadding;
                if (!TryNormalizePeCertificateMessage(message, payloadBytes, out var normalized)) return Result("invalid", null, null);
                message = normalized;
                count = checked((uint)message.Length);
            }
            stage = InspectionStage.SignedEnvelope;
            if (!SupportedEnvelope(message)) return Result("unsupported", null, null);
            // This verifies the embedded signed message only. SIP verification below
            // independently binds its authenticated indirect digest to the actual file.
            var verification = new VerifyMessageParameters { Size = Size<VerifyMessageParameters>(), Encoding = Encoding };
            uint decodedLength = 0;
            nint signerCertificate = 0;
            nint hashProvider = 0;
            try
            {
                stage = InspectionStage.MessageSignatureSize;
                if (!CryptVerifyMessageSignature(ref verification, 0, message, count, null, ref decodedLength, out signerCertificate))
                    return Result("invalid", null, Marshal.GetLastWin32Error());
                if (signerCertificate != 0) { _ = CertFreeCertificateContext(signerCertificate); signerCertificate = 0; }
                // A detached message can return TRUE with zero decoded bytes. It is
                // never acceptable as the embedded Authenticode indirect content.
                if (decodedLength is < 1 or > MaximumSignatureBytes) return Result("invalid", null, null);
                var decoded = new byte[decodedLength];
                stage = InspectionStage.MessageSignatureBytes;
                if (!CryptVerifyMessageSignature(ref verification, 0, message, count, decoded, ref decodedLength, out signerCertificate) ||
                    decodedLength == 0 || decodedLength > decoded.Length || signerCertificate == 0)
                    return Result("invalid", null, Marshal.GetLastWin32Error());
                stage = InspectionStage.SignerCertificate;
                using var certificate = ReadCertificate(signerCertificate);
                var id = CertificateMaterial.Id(certificate);
                stage = InspectionStage.ExpectedIdentity;
                if (expectedCertificateId is not null && expectedCertificateId != id) return Result("identity_mismatch", id, null);
                stage = InspectionStage.SigningPurpose;
                if (!SigningPurpose(certificate)) return Result("unsupported", id, null);
                stage = InspectionStage.IndirectStructure;
                if (!SupportedIndirect(decoded.AsMemory(0, checked((int)decodedLength)))) return Result("unsupported", id, null);
                uint nativeBytes = 0;
                stage = InspectionStage.IndirectNativeSize;
                if (!CryptDecodeObjectEx(Encoding, IndirectDataOid, decoded, decodedLength, 0, 0, 0, ref nativeBytes) ||
                    nativeBytes is < 1 or > MaximumSignatureBytes) return Result("invalid", id, Marshal.GetLastWin32Error());
                using var indirect = new Allocation(checked((int)nativeBytes));
                stage = InspectionStage.IndirectNativeBytes;
                if (!CryptDecodeObjectEx(Encoding, IndirectDataOid, decoded, decodedLength, 0, 0, indirect.Pointer, ref nativeBytes))
                    return Result("invalid", id, Marshal.GetLastWin32Error());
                using var digestOid = Allocation.Ansi("2.16.840.1.101.3.4.2.1");
                subject.Algorithm = new AlgorithmIdentifier { Oid = digestOid.Pointer };
                if (Path.GetExtension(file.Path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    // Give the script SIP an explicit SHA256-capable public
                    // hashing context. No container/private keys; no provider UI.
                    stage = InspectionStage.IndirectHashProvider;
                    if (!CryptAcquireContextW(out var acquiredProvider, null,
                            "Microsoft Enhanced RSA and AES Cryptographic Provider", 24, 0xf0000040u))
                        return Result("unsupported", id, Marshal.GetLastWin32Error());
                    hashProvider = acquiredProvider;
                    subject.Provider = hashProvider;
                }
                stage = InspectionStage.IndirectDigest;
                if (!CryptSIPVerifyIndirectData(ref subject, indirect.Pointer)) return Result("invalid", id, Marshal.GetLastWin32Error());
                stage = InspectionStage.Complete;
                return Result("valid", id, null);
            }
            finally
            {
                if (hashProvider != 0) _ = CryptReleaseContext(hashProvider, 0);
                if (signerCertificate != 0) _ = CertFreeCertificateContext(signerCertificate);
            }
        }
        catch (Exception error) when (error is CryptographicException or AsnContentException or CertificateOperationException)
        { return Result("invalid", null, null); }
        catch (BadImageFormatException) { return Result("unsupported", null, null); }
        finally
        {
            try { file.RequireIdentity(file.Handle); }
            catch { stage = InspectionStage.PostIdentity; throw; }
        }
    }

    // Parse only the closed supported envelope, never replace Windows SIP hashing.
    // Primary RSA/SHA256 integrity is independent of timestamp/time policy.
    // Existing unsigned timestamp attributes are left for offline WinTrust;
    // nested signatures introduce signer selection and are explicitly unsupported.
    private static bool SupportedEnvelope(byte[] message)
    {
        var reader = new AsnReader(message, AsnEncodingRules.BER);
        var content = reader.ReadSequence();
        if (content.ReadObjectIdentifier() != "1.2.840.113549.1.7.2") return false;
        var wrapper = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        var signed = wrapper.ReadSequence();
        _ = signed.ReadInteger();
        var hashes = signed.ReadSetOf(skipSortOrderValidation: true);
        if (!Sha256Algorithm(hashes.ReadSequence()) || hashes.HasData) return false;
        var indirect = signed.ReadSequence();
        if (indirect.ReadObjectIdentifier() != IndirectDataOid || !indirect.HasData) return false;
        _ = indirect.ReadEncodedValue(); indirect.ThrowIfNotEmpty();
        if (signed.HasData && signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            var certificates = signed.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0, true));
            var number = 0;
            while (certificates.HasData)
            {
                if (++number > 16) return false;
                _ = certificates.ReadEncodedValue();
            }
        }
        // CRLs/other optional containers and multiple signers are outside this capability.
        if (!signed.HasData || !signed.PeekTag().HasSameClassAndValue(Asn1Tag.SetOf)) return false;
        var signers = signed.ReadSetOf(skipSortOrderValidation: true);
        var signer = signers.ReadSequence();
        _ = signer.ReadInteger(); _ = signer.ReadEncodedValue();
        if (!Sha256Algorithm(signer.ReadSequence())) return false;
        if (signer.HasData && signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) _ = signer.ReadEncodedValue();
        var algorithm = signer.ReadSequence();
        if (algorithm.ReadObjectIdentifier() is not ("1.2.840.113549.1.1.1" or "1.2.840.113549.1.1.11")) return false;
        if (algorithm.HasData) algorithm.ReadNull(); algorithm.ThrowIfNotEmpty();
        if (signer.ReadOctetString().Length is < 1 or > 512 || signers.HasData) return false;
        if (signer.HasData)
        {
            var attributes = signer.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 1, true));
            var count = 0;
            while (attributes.HasData)
            {
                if (++count > 32) return false;
                var attribute = attributes.ReadSequence();
                if (attribute.ReadObjectIdentifier() == "1.3.6.1.4.1.311.2.4.1") return false;
                _ = attribute.ReadSetOf(skipSortOrderValidation: true); attribute.ThrowIfNotEmpty();
            }
        }
        signer.ThrowIfNotEmpty();
        signed.ThrowIfNotEmpty(); wrapper.ThrowIfNotEmpty(); content.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
        return true;
    }
    internal static bool SupportedIndirect(ReadOnlyMemory<byte> encoded)
    {
        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.BER);
            var value = reader.ReadSequence(); _ = value.ReadEncodedValue();
            var digestInfo = value.ReadSequence();
            if (!Sha256Algorithm(digestInfo.ReadSequence()) || digestInfo.ReadOctetString().Length != 32) return false;
            digestInfo.ThrowIfNotEmpty(); value.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty(); return true;
        }
        catch (AsnContentException) { return false; }
    }
    // WIN_CERTIFICATE can include zero padding to the next eight-byte boundary
    // in dwLength. Trim only that PE storage padding, never ASN content or PS1
    // messages. Both envelope parsing and signature verification use this output.
    internal static bool TryNormalizePeCertificateMessage(byte[] payload, int expectedPayloadBytes, out byte[] message)
    {
        message = [];
        if (payload.Length != expectedPayloadBytes || payload.Length is < 1 or > MaximumSignatureBytes) return false;
        try
        {
            var reader = new AsnReader(payload, AsnEncodingRules.BER);
            var tag = reader.PeekTag();
            if (!tag.IsConstructed || !tag.HasSameClassAndValue(Asn1Tag.Sequence)) return false;
            var encoded = reader.ReadEncodedValue();
            var padding = payload.Length - encoded.Length;
            if (padding > 7 || padding != 0 && payload.Length % 8 != 0) return false;
            foreach (var value in payload.AsSpan(encoded.Length)) if (value != 0) return false;
            message = padding == 0 ? payload : encoded.ToArray();
            return true;
        }
        catch (AsnContentException) { return false; }
    }

    // Structural absence is deliberately limited to unambiguous ASCII and
    // strict UTF-16LE BOM text. Other encodings keep their existing SIP route.
    // This scan never authenticates a block or replaces the SIP digest rules.
    internal static Ps1SignatureStructure InspectPs1Structure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumSignatureBytes) return Ps1SignatureStructure.Malformed;
        char[]? characters = null;
        try
        {
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
                return Ps1SignatureStructure.UnrecognizedEncoding; // UTF-32LE, not UTF-16LE.
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                var encoding = new System.Text.UnicodeEncoding(false, false, true);
                characters = new char[encoding.GetCharCount(bytes[2..])];
                _ = encoding.GetChars(bytes[2..], characters);
                if (characters.AsSpan().Contains('\0')) return Ps1SignatureStructure.Malformed;
            }
            else
            {
                foreach (var value in bytes)
                    if (value > 0x7f || value == 0) return Ps1SignatureStructure.UnrecognizedEncoding;
                characters = new char[bytes.Length];
                for (var index = 0; index < bytes.Length; index++) characters[index] = (char)bytes[index];
            }
            ReadOnlySpan<char> text = characters;
            const string prefix = "# SIG #", begin = "# SIG # Begin signature block", end = "# SIG # End signature block";
            var remaining = text;
            var prefixes = 0;
            while (remaining.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) is var at && at >= 0)
            {
                if (++prefixes > 2) return Ps1SignatureStructure.Malformed;
                remaining = remaining[(at + prefix.Length)..];
            }
            if (prefixes == 0) return Ps1SignatureStructure.Absent;
            var first = text.IndexOf(begin, StringComparison.Ordinal);
            var last = text.IndexOf(end, StringComparison.Ordinal);
            if (prefixes != 2 || first < 0 || last <= first + begin.Length ||
                !IsPs1MarkerLine(text, first, begin.Length) || !IsPs1MarkerLine(text, last, end.Length))
                return Ps1SignatureStructure.Malformed;
            foreach (var value in text[(last + end.Length)..])
                if (value is not ('\r' or '\n' or ' ' or '\t')) return Ps1SignatureStructure.Malformed;
            return Ps1SignatureStructure.Present;
        }
        catch (System.Text.DecoderFallbackException) { return Ps1SignatureStructure.Malformed; }
        finally { if (characters is not null) Array.Clear(characters); }
    }

    private static bool IsPs1MarkerLine(ReadOnlySpan<char> text, int at, int length) =>
        (at == 0 || text[at - 1] is '\r' or '\n') &&
        (at + length == text.Length || text[at + length] is '\r' or '\n');

    private static bool SupportedContainer(AuthenticodeFileLease file, out bool unsignedEmbedded, out int? pePayloadBytes)
    {
        unsignedEmbedded = false;
        pePayloadBytes = null;
        var extension = Path.GetExtension(file.Path);
        using var borrowedHandle = new SafeFileHandle(file.Handle.DangerousGetHandle(), ownsHandle: false);
        using var input = new FileStream(borrowedHandle, FileAccess.Read);
        input.Position = 0;
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // The script SIP controls the actual digest/encoding. This bounded
            // scan rejects multiple text signature blocks rather than selecting one.
            if (input.Length > MaximumSignatureBytes) return false;
            var text = new byte[checked((int)input.Length)];
            try
            {
                input.ReadExactly(text);
                if (text.Length >= 2 && text[0] == 'M' && text[1] == 'Z') return false;
                var structure = InspectPs1Structure(text);
                if (structure == Ps1SignatureStructure.Malformed) return false;
                if (structure == Ps1SignatureStructure.Absent) { unsignedEmbedded = true; return true; }
                if (structure == Ps1SignatureStructure.Present) return true;
                // Preserve bounded inspection for other encodings without
                // promoting their missing/ambiguous markers to unsigned proof.
                var marker = System.Text.Encoding.ASCII.GetBytes("# SIG # Begin signature block");
                var wide = System.Text.Encoding.Unicode.GetBytes("# SIG # Begin signature block");
                return Occurrences(text, marker) + Occurrences(text, wide) <= 1;
            }
            finally { CryptographicOperations.ZeroMemory(text); }
        }
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.PEHeader is not { } header) return false;
        var table = header.CertificateTableDirectory;
        if (table.RelativeVirtualAddress == 0 && table.Size == 0)
        {
            unsignedEmbedded = true;
            return true;
        }
        if (table.RelativeVirtualAddress <= 0 || table.RelativeVirtualAddress % 8 != 0 || table.Size < 8 || table.Size > MaximumSignatureBytes ||
            (long)table.RelativeVirtualAddress + table.Size > input.Length) return false;
        input.Position = table.RelativeVirtualAddress;
        Span<byte> entry = stackalloc byte[8];
        input.ReadExactly(entry);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        // Exactly one WIN_CERTIFICATE, with only its required alignment padding.
        if (length < 8 || length > table.Size || ((length + 7UL) & ~7UL) != (ulong)table.Size ||
            BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]) != 0x200 || BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) != 2) return false;
        pePayloadBytes = checked((int)length) - 8;
        return true;
    }
    private static int Occurrences(ReadOnlySpan<byte> input, ReadOnlySpan<byte> pattern)
    {
        var count = 0;
        while (input.IndexOf(pattern) is var index && index >= 0)
        { if (++count > 1) return count; input = input[(index + pattern.Length)..]; }
        return count;
    }
    private static bool Sha256Algorithm(AsnReader value)
    {
        if (value.ReadObjectIdentifier() != "2.16.840.1.101.3.4.2.1") return false;
        if (value.HasData) value.ReadNull(); value.ThrowIfNotEmpty(); return true;
    }
    private static bool SigningPurpose(X509Certificate2 certificate)
    {
        if (certificate.Extensions.Cast<X509Extension>().GroupBy(value => value.Oid?.Value, StringComparer.Ordinal).Any(group => group.Count() > 1)) return false;
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        using var key = certificate.GetRSAPublicKey();
        // Time and chain acceptance belong to Windows trust, not signature integrity.
        return basic?.CertificateAuthority != true && key?.KeySize is 2048 or 3072 or 4096 &&
            usage is not null && (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0 &&
            eku is not null && eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CertificateOperations.CodeSigningOid);
    }

    internal static AuthenticodeVerificationResult VerifyTrust(SafeFileHandle file, string path)
    {
        using var name = Allocation.Utf16(path);
        using var info = Allocation.Value(new TrustFileInfo { Size = Size<TrustFileInfo>(), Name = name.Pointer, File = file.DangerousGetHandle() });
        using var settings = Allocation.Value(new TrustSignatureSettings { Size = Size<TrustSignatureSettings>(), Flags = 1 });
        var data = new TrustData { Size = Size<TrustData>(), Ui = 2, Choice = 1, Info = info.Pointer, StateAction = 1,
            Flags = 0x1000u | 0x10u, SignatureSettings = settings.Pointer };
        var action = TrustAction;
        int status;
        try { status = WinVerifyTrust(new nint(-1), ref action, ref data); }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new nint(-1), ref action, ref data); }
        var trust = status == 0 ? "trusted" : unchecked((uint)status) is 0x800b0109 or 0x800b0101 or 0x800b0111 or 0x800b0110 or 0x800b010a
            ? "untrusted" : "indeterminate";
        return new("not_evaluated", trust, "not_checked", null, status);
    }

    private static AuthenticodeVerificationResult Result(string signature, string? certificate, int? error) =>
        new(signature, "not_evaluated", "not_checked", certificate, error);
    private static bool IsUnsigned(int error) => unchecked((uint)error) is 0x800b0100 or 0x80092009 or 0x80092004;
    private static bool IsUnsupported(int error) => unchecked((uint)error) is 0x800b0003 or 0x800b0001 or 0x800b0002;
    private static X509Certificate2 ReadCertificate(nint context)
    {
        if (context == 0) throw new CryptographicException();
        var certificate = Marshal.PtrToStructure<CertificateContext>(context);
        if (certificate.Length is < 1 or > CertificateMaterial.MaximumCertificateBytes || certificate.Encoded == 0) throw new CryptographicException();
        var bytes = new byte[certificate.Length]; Marshal.Copy(certificate.Encoded, bytes, 0, bytes.Length);
        return X509CertificateLoader.LoadCertificate(bytes);
    }
    private static MemoryStore OpenMemoryStore()
    {
        var handle = CertOpenStore(new nint(2), Encoding, 0, 0x2000, 0);
        if (handle == 0) throw NativeFailure();
        return new(handle);
    }
    private static IOException NativeFailure() => new("The native Authenticode operation failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    private static uint Size<T>() where T : struct => checked((uint)Marshal.SizeOf<T>());
    private sealed class MemoryStore(nint handle) : IDisposable { internal nint Handle => handle; public void Dispose() { if (handle != 0) { _ = CertCloseStore(handle, 0); handle = 0; } } }
    private sealed class Allocation : IDisposable
    {
        internal nint Pointer { get; private set; }
        internal Allocation(int size) => Pointer = Marshal.AllocHGlobal(size);
        private Allocation(nint pointer) => Pointer = pointer;
        internal static Allocation Utf16(string text) => new(Marshal.StringToHGlobalUni(text));
        internal static Allocation Ansi(string text) => new(Marshal.StringToHGlobalAnsi(text));
        internal static Allocation Value<T>(T value) where T : struct
        { var result = new Allocation(Marshal.SizeOf<T>()); Marshal.StructureToPtr(value, result.Pointer, false); return result; }
        public void Dispose() { if (Pointer != 0) { Marshal.FreeHGlobal(Pointer); Pointer = 0; } }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DigestSign(nint certificate, nint metadata, uint algorithm, nint digest, uint length, nint file, out Blob signed);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal uint Length; internal nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct AlgorithmIdentifier { internal nint Oid; internal Blob Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct Pkcs1Padding { internal nint Algorithm; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerFileInfo { internal uint Size; internal nint Name, File; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerSubjectInfo { internal uint Size; internal nint Index; internal uint Choice; internal nint Info; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerStoreInfo { internal uint Size; internal nint Certificate; internal uint Policy; internal nint Store; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerCert { internal uint Size, Choice; internal nint Info, Window; }
    [StructLayout(LayoutKind.Sequential)] private struct SignerSignatureInfo { internal uint Size, Algorithm, AttributeChoice; internal nint Authcode, Authenticated, Unauthenticated; }
    [StructLayout(LayoutKind.Sequential)] private struct DigestSignInfo { internal uint Size, Choice; internal nint Callback, Metadata; internal uint Reserved1, Reserved2, Reserved3; }
    [StructLayout(LayoutKind.Sequential)] private struct SipSubject
    {
        internal uint Size; internal nint SubjectType, File, Name, DisplayName; internal uint Reserved1, Version; internal nint Provider;
        internal AlgorithmIdentifier Algorithm; internal uint Flags, Encoding, Reserved2, CapiSettings, SecuritySettings, Index, Choice; internal nint Additional, Client;
    }
    [StructLayout(LayoutKind.Sequential)] private struct VerifyMessageParameters { internal uint Size, Encoding; internal nint Provider, GetSigner, Argument, StrongSign; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { internal uint Encoding; internal nint Encoded; internal uint Length; internal nint Info, Store; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFileInfo { internal uint Size; internal nint Name, File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustSignatureSettings { internal uint Size, Index, Flags, SecondaryCount, VerifiedIndex; internal nint Policy; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { internal uint Size; internal nint Policy, Sip; internal uint Ui, Revocation, Choice; internal nint Info; internal uint StateAction; internal nint State, Url; internal uint Flags, UiContext; internal nint SignatureSettings; }

    [DllImport("mssign32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SignerSignEx3(uint flags, ref SignerSubjectInfo subject, ref SignerCert certificate, ref SignerSignatureInfo signature,
        nint provider, uint timestampFlags, nint timestampAlgorithm, nint timestampUrl, nint request, nint sip, out nint context, nint policy, ref DigestSignInfo digest, nint reserved);
    [DllImport("mssign32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int SignerFreeSignerContext(nint context);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertGetCertificateContextProperty(nint certificate, uint property, out nint value, ref uint size);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptAcquireCertificatePrivateKey(nint certificate, uint flags, nint parameter, out nint key, out uint keySpec, [MarshalAs(UnmanagedType.Bool)] out bool freeKey);
    [DllImport("ncrypt.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptSignHash(nint key, ref Pkcs1Padding padding, byte[] hash, uint hashSize, [Out] byte[]? signature, uint size, out uint written, uint flags);
    [DllImport("ncrypt.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NCryptFreeObject(nint handle);
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetProcessHeap();
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint HeapAlloc(nint heap, uint flags, nuint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapFree(nint heap, uint flags, nint memory);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint CertOpenStore(nint provider, uint encoding, nint cryptProvider, uint flags, nint parameter);
    [DllImport("crypt32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CertCloseStore(nint store, uint flags);
    [DllImport("crypt32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CertFreeCertificateContext(nint certificate);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CertAddCertificateContextToStore(nint store, nint certificate, uint disposition, nint stored);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSIPRetrieveSubjectGuid(string path, SafeFileHandle file, out Guid subject);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSIPGetSignedDataMsg(ref SipSubject subject, out uint encoding, uint index, ref uint size, [Out] byte[]? bytes);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptVerifyMessageSignature(ref VerifyMessageParameters parameters, uint signer, byte[] message, uint size, [Out] byte[]? decoded, ref uint decodedSize, out nint certificate);
    [DllImport("crypt32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptDecodeObjectEx(uint encoding, string kind, byte[] encoded, uint length, uint flags, nint parameters, nint decoded, ref uint size);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSIPVerifyIndirectData(ref SipSubject subject, nint indirect);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptAcquireContextW(out nint provider, string? container, string name, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptReleaseContext(nint provider, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
