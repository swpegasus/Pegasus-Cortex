using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

/// <summary>
/// Strict canonical codec for the frozen offline transport frame. A future
/// outer schema is identified without interpretation so its original file can
/// remain untouched for a newer reader.
/// </summary>
public static class CortexOfflineFrameCodec
{
    private const string EmptySha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] CreateKnowledgeCandidate(
        CortexOfflineKnowledgeCreatePayloadV1 payload,
        DateTimeOffset createdAtUtc)
    {
        var validated = ValidateKnowledgePayload(payload);
        var payloadBytes = CanonicalSerialize(validated);
        var frameId = $"of_{CortexCanonicalJson.Sha256(
            CortexOfflineSchemas.KnowledgeCreateV1 + "\n" + validated.Namespace + "\n" + validated.RecordId)[..32]}";
        return CreateFrame(
            CortexOfflineSchemas.KnowledgeCreateV1,
            payloadBytes,
            createdAtUtc,
            frameId);
    }

    internal static byte[] CreateReceipt(
        CortexOfflineReceiptPayloadV1 payload,
        DateTimeOffset createdAtUtc,
        string frameId)
    {
        ValidateReceiptPayload(payload);
        return CreateFrame(
            CortexOfflineSchemas.ReceiptV1,
            CanonicalSerialize(payload),
            createdAtUtc,
            frameId);
    }

    public static CortexOfflineDecodedFrame Decode(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is < 1 or > CortexOfflineLimits.MaximumFrameUtf8Bytes)
            throw new InvalidDataException("An offline frame has an invalid byte length.");
        try
        {
            _ = StrictUtf8.GetString(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("An offline frame is not strict UTF-8.", exception);
        }

        string outerSchema;
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schema", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("An offline frame has no outer schema.");
            }
            outerSchema = schemaElement.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("An offline frame is malformed JSON.", exception);
        }

        if (!string.Equals(outerSchema, CortexOfflineSchemas.FrameV1, StringComparison.Ordinal))
        {
            return new CortexOfflineDecodedFrame(
                CortexOfflineFrameStatus.UnsupportedOuterSchema,
                null,
                null,
                outerSchema,
                "The outer frame schema is unsupported and was preserved without interpretation.");
        }

        CortexOfflineFrameV1 frame;
        try
        {
            frame = JsonSerializer.Deserialize<CortexOfflineFrameV1>(utf8Json, CortexFileV2Json.Options) ??
                throw new InvalidDataException("The offline frame is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The offline frame is malformed.", exception);
        }
        ValidateOuter(frame);

        var canonical = CanonicalSerialize(frame);
        if (!canonical.AsSpan().SequenceEqual(utf8Json))
            throw new InvalidDataException("The offline frame is not canonical JSON.");
        var unsealed = CanonicalSerialize(frame with { FrameSha256 = EmptySha256 });
        if (!FixedEquals(frame.FrameSha256, CortexCanonicalJson.Sha256(unsealed)))
            throw new InvalidDataException("The offline frame failed its integrity seal.");

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(frame.PayloadBase64Url);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The offline payload encoding is invalid.", exception);
        }
        if (payload.Length != frame.PayloadUtf8Bytes ||
            !FixedEquals(frame.PayloadSha256, CortexCanonicalJson.Sha256(payload)))
        {
            throw new InvalidDataException("The offline payload failed its length or hash check.");
        }
        try
        {
            _ = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The offline payload is not strict UTF-8.", exception);
        }

        var status = frame.PayloadSchema switch
        {
            CortexOfflineSchemas.KnowledgeCreateV1 => CortexOfflineFrameStatus.Pending,
            CortexOfflineSchemas.ReceiptV1 => CortexOfflineFrameStatus.Receipt,
            _ => CortexOfflineFrameStatus.UnsupportedPayload
        };
        return new CortexOfflineDecodedFrame(
            status,
            frame,
            payload,
            frame.Schema,
            status == CortexOfflineFrameStatus.UnsupportedPayload
                ? "The payload schema is unsupported and was preserved without interpretation."
                : null);
    }

    public static CortexOfflineKnowledgeCreatePayloadV1 DecodeKnowledgePayload(
        CortexOfflineDecodedFrame decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (decoded.Status != CortexOfflineFrameStatus.Pending || decoded.Frame is null || decoded.PayloadBytes is null ||
            !string.Equals(decoded.Frame.PayloadSchema, CortexOfflineSchemas.KnowledgeCreateV1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The frame is not a supported pending knowledge candidate.");
        }
        CortexOfflineKnowledgeCreatePayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<CortexOfflineKnowledgeCreatePayloadV1>(
                    decoded.PayloadBytes,
                    CortexFileV2Json.Options) ??
                throw new InvalidDataException("The offline knowledge payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The offline knowledge payload is malformed.", exception);
        }
        var validated = ValidateKnowledgePayload(payload);
        if (!CanonicalSerialize(validated).AsSpan().SequenceEqual(decoded.PayloadBytes))
            throw new InvalidDataException("The offline knowledge payload is not canonical JSON.");
        return validated;
    }

    public static string KnowledgeSemanticSha256(CortexOfflineKnowledgeCreatePayloadV1 payload)
    {
        var value = ValidateKnowledgePayload(payload);
        return CortexCanonicalJson.Sha256(CanonicalSerialize(new
        {
            value.Schema,
            value.Namespace,
            value.RecordId,
            value.Kind,
            value.Tier,
            value.Title,
            value.Content,
            value.Tags,
            value.Source,
            value.SourceId,
            value.Evidence
        }));
    }

    internal static CortexOfflineReceiptPayloadV1 DecodeReceiptPayload(CortexOfflineDecodedFrame decoded)
    {
        if (decoded.Status != CortexOfflineFrameStatus.Receipt || decoded.PayloadBytes is null)
            throw new InvalidOperationException("The frame is not a receipt.");
        try
        {
            var payload = JsonSerializer.Deserialize<CortexOfflineReceiptPayloadV1>(
                    decoded.PayloadBytes,
                    CortexFileV2Json.Options) ??
                throw new InvalidDataException("The offline receipt is empty.");
            ValidateReceiptPayload(payload);
            if (!CanonicalSerialize(payload).AsSpan().SequenceEqual(decoded.PayloadBytes))
                throw new InvalidDataException("The offline receipt is not canonical JSON.");
            return payload;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The offline receipt is malformed.", exception);
        }
    }

    internal static byte[] CanonicalSerialize<T>(T value)
    {
        var regular = JsonSerializer.SerializeToUtf8Bytes(value, CortexFileV2Json.Options);
        using var document = JsonDocument.Parse(regular);
        return CortexCanonicalJson.Serialize(document.RootElement);
    }

    internal static CortexOfflineKnowledgeCreatePayloadV1 ValidateKnowledgePayload(
        CortexOfflineKnowledgeCreatePayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(payload.Schema, CortexOfflineSchemas.KnowledgeCreateV1, StringComparison.Ordinal))
            throw new ArgumentException("The offline knowledge payload schema is unsupported.", nameof(payload));
        CortexFileV2Validation.RequireIdentifier(payload.Namespace, nameof(payload.Namespace));
        CortexFileV2Validation.RequireIdentifier(payload.RecordId, nameof(payload.RecordId));
        if (!Enum.IsDefined(payload.Kind) || !Enum.IsDefined(payload.Tier) || !Enum.IsDefined(payload.Source))
            throw new ArgumentException("The offline knowledge payload contains an unknown enum value.", nameof(payload));
        RequireText(payload.Title, CortexV2Limits.MaximumTitleCharacters, nameof(payload.Title));
        RequireText(payload.Content, CortexOfflineLimits.MaximumBodyCharacters, nameof(payload.Content));
        if (payload.Tags is null || payload.Tags.Count > CortexV2Limits.MaximumTags ||
            payload.Tags.Distinct(StringComparer.Ordinal).Count() != payload.Tags.Count)
        {
            throw new ArgumentException("Offline tags are null, duplicated, or exceed the bound.", nameof(payload.Tags));
        }
        foreach (var tag in payload.Tags)
            CortexFileV2Validation.RequireIdentifier(tag, nameof(payload.Tags));
        CortexFileV2Validation.RequireIdentifier(payload.SourceId, nameof(payload.SourceId));
        RequireText(payload.Evidence, CortexV2Limits.MaximumEvidenceCharacters, nameof(payload.Evidence), allowEmpty: true);
        RequireUtc(payload.CapturedAtUtc, nameof(payload.CapturedAtUtc));
        return payload with
        {
            Tags = Array.AsReadOnly(payload.Tags.Order(StringComparer.Ordinal).ToArray())
        };
    }

    private static byte[] CreateFrame(
        string payloadSchema,
        byte[] payloadBytes,
        DateTimeOffset createdAtUtc,
        string? frameId)
    {
        if (payloadBytes.Length is < 1 or > CortexOfflineLimits.MaximumPayloadUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        var payloadHash = CortexCanonicalJson.Sha256(payloadBytes);
        frameId ??= $"of_{CortexCanonicalJson.Sha256(payloadSchema + "\n" + payloadHash)[..32]}";
        CortexFileV2Validation.RequireIdentifier(frameId, nameof(frameId));
        var frame = new CortexOfflineFrameV1(
            CortexOfflineSchemas.FrameV1,
            CortexOfflineSchemas.CanonicalizationV1,
            frameId,
            createdAtUtc,
            CortexOfflineSchemas.ContentTreatment,
            false,
            payloadSchema,
            CortexOfflineSchemas.PayloadEncoding,
            payloadBytes.Length,
            payloadHash,
            EncodeBase64Url(payloadBytes),
            EmptySha256);
        frame = frame with { FrameSha256 = CortexCanonicalJson.Sha256(CanonicalSerialize(frame)) };
        var bytes = CanonicalSerialize(frame);
        if (bytes.Length > CortexOfflineLimits.MaximumFrameUtf8Bytes)
            throw new InvalidDataException("The canonical offline frame exceeds its byte bound.");
        return bytes;
    }

    private static void ValidateOuter(CortexOfflineFrameV1 frame)
    {
        if (!string.Equals(frame.Schema, CortexOfflineSchemas.FrameV1, StringComparison.Ordinal) ||
            !string.Equals(frame.Canonicalization, CortexOfflineSchemas.CanonicalizationV1, StringComparison.Ordinal) ||
            !string.Equals(frame.ContentTreatment, CortexOfflineSchemas.ContentTreatment, StringComparison.Ordinal) ||
            frame.Executable ||
            !string.Equals(frame.PayloadEncoding, CortexOfflineSchemas.PayloadEncoding, StringComparison.Ordinal) ||
            frame.PayloadUtf8Bytes is < 1 or > CortexOfflineLimits.MaximumPayloadUtf8Bytes)
        {
            throw new InvalidDataException("The offline frame violates the frozen outer contract.");
        }
        CortexFileV2Validation.RequireIdentifier(frame.FrameId, nameof(frame.FrameId));
        RequirePayloadSchema(frame.PayloadSchema, nameof(frame.PayloadSchema));
        RequireUtc(frame.CreatedAtUtc, nameof(frame.CreatedAtUtc));
        RequireSha256(frame.PayloadSha256, nameof(frame.PayloadSha256));
        RequireSha256(frame.FrameSha256, nameof(frame.FrameSha256));
        if (string.IsNullOrEmpty(frame.PayloadBase64Url))
            throw new InvalidDataException("The offline frame has no payload bytes.");
    }

    private static void ValidateReceiptPayload(CortexOfflineReceiptPayloadV1 payload)
    {
        if (!string.Equals(payload.Schema, CortexOfflineSchemas.ReceiptV1, StringComparison.Ordinal))
            throw new ArgumentException("The offline receipt schema is unsupported.", nameof(payload));
        CortexFileV2Validation.RequireIdentifier(payload.CandidateFrameId, nameof(payload.CandidateFrameId));
        RequireSha256(payload.CandidateFrameSha256, nameof(payload.CandidateFrameSha256));
        RequireSha256(payload.CandidatePayloadSha256, nameof(payload.CandidatePayloadSha256));
        RequireSha256(payload.CandidateSemanticSha256, nameof(payload.CandidateSemanticSha256));
        CortexFileV2Validation.RequireIdentifier(payload.EffectiveNamespace, nameof(payload.EffectiveNamespace));
        CortexFileV2Validation.RequireIdentifier(payload.RecordId, nameof(payload.RecordId));
        if (payload.Revision < 1 || !Enum.IsDefined(payload.Kind) || !Enum.IsDefined(payload.Tier))
            throw new ArgumentException("The offline receipt coordinates are invalid.", nameof(payload));
        RequireText(payload.Title, CortexV2Limits.MaximumTitleCharacters, nameof(payload.Title));
        RequireText(payload.Disposition, 64, nameof(payload.Disposition));
        RequireSha256(payload.VerifiedContentSha256, nameof(payload.VerifiedContentSha256));
        RequireUtc(payload.VerifiedAtUtc, nameof(payload.VerifiedAtUtc));
    }

    private static void RequireText(string? value, int maximum, string name, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum)
            throw new ArgumentException("Bounded text is required.", name);
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("The timestamp must be UTC.", name);
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 value is required.", name);
    }

    private static void RequirePayloadSchema(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !value.StartsWith("pegasus.cortex.", StringComparison.Ordinal) ||
            value.Count(character => character == '/') != 1 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or '/')))
        {
            throw new ArgumentException("A bounded versioned payload schema is required.", name);
        }
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('=') || value.Contains('+') || value.Contains('/'))
            throw new FormatException("The value is not canonical base64url.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var bytes = Convert.FromBase64String(padded);
        if (!string.Equals(value, EncodeBase64Url(bytes), StringComparison.Ordinal))
            throw new FormatException("The value is not canonical base64url.");
        return bytes;
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
