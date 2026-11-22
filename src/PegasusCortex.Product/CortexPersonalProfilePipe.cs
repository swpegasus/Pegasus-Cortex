using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.Product;

/// <summary>Read-only availability, never personal records or an identity grant.</summary>
public sealed record CortexPersonalProfileStatusV1(
    int Version,
    string State,
    string? Profile,
    string Generation,
    string? IssueCode);

/// <summary>
/// Fixed local status protocol. The caller must authenticate the pipe's Windows
/// SID and session separately; neither the display label nor pipe name is identity.
/// Frames are a little-endian Int32 length followed by at most 1024 UTF-8 JSON bytes.
/// </summary>
public static class CortexPersonalProfilePipe
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 1024;
    public const string Prefix = "pegasus-cortex-personal-profile-v1";
    public const string Loaded = "loaded";
    public const string NotLoaded = "not_loaded";
    public const string ProfileUnloaded = "profile_unloaded";
    public const string UiUnavailable = "ui_unavailable";
    public const string PipeUnavailable = "profile_pipe_unavailable";
    public const string LabelInvalid = "profile_label_invalid";

    public static string Name(int session)
    {
        if (session <= 0) throw new ArgumentOutOfRangeException(nameof(session));
        return $"{Prefix}-{session}";
    }

    public static string CanonicalProfileLabel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
            throw new ArgumentException("The personal profile display label contains control characters.", nameof(value));
        var label = value.Trim().Normalize(NormalizationForm.FormC);
        if (label.Length is < 1 or > 128 || label.EnumerateRunes().Any(rune =>
                !Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                    (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) &&
                rune.Value is not (' ' or '.' or '_' or '-' or '@' or '\'')))
            throw new ArgumentException("The personal profile display label is invalid.", nameof(value));
        return label;
    }

    public static async Task WriteRequestAsync(Stream stream, CancellationToken cancellationToken = default) =>
        await WriteFrameAsync(stream, "{\"version\":1,\"operation\":\"status\"}"u8.ToArray(), cancellationToken).ConfigureAwait(false);

    public static async Task ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            var root = ExactObject(document, ["version", "operation"]);
            if (root.GetProperty("version").GetInt32() != Version ||
                root.GetProperty("operation").GetString() != "status")
                throw new InvalidDataException("personal_profile_protocol_invalid");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("personal_profile_protocol_invalid", exception);
        }
    }

    public static async Task WriteResponseAsync(
        Stream stream, CortexPersonalProfileStatusV1 status, CancellationToken cancellationToken = default)
    {
        Validate(status);
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = status.Version,
            state = status.State,
            profile = status.Profile,
            generation = status.Generation,
            issueCode = status.IssueCode
        }), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CortexPersonalProfileStatusV1> ReadResponseAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            var root = ExactObject(document, ["version", "state", "profile", "generation", "issueCode"]);
            var status = new CortexPersonalProfileStatusV1(
                root.GetProperty("version").GetInt32(), root.GetProperty("state").GetString()!,
                NullableString(root.GetProperty("profile")), root.GetProperty("generation").GetString()!,
                NullableString(root.GetProperty("issueCode")));
            Validate(status);
            return status;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("personal_profile_protocol_invalid", exception);
        }
    }

    public static void Validate(CortexPersonalProfileStatusV1 status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Version != Version || status.State is not (Loaded or NotLoaded) ||
            status.Generation is not { Length: 32 } ||
            status.Generation.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            status.IssueCode is not (null or ProfileUnloaded or UiUnavailable or PipeUnavailable or LabelInvalid) ||
            (status.State == Loaded && (status.Profile is null || status.IssueCode is not null)) ||
            (status.State == NotLoaded && status.Profile is not null))
            throw new InvalidDataException("personal_profile_status_invalid");
        if (status.Profile is not null && !string.Equals(
                CanonicalProfileLabel(status.Profile), status.Profile, StringComparison.Ordinal))
            throw new InvalidDataException("personal_profile_label_invalid");
    }

    private static string? NullableString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static JsonElement ExactObject(JsonDocument document, string[] fields)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("personal_profile_protocol_invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("personal_profile_protocol_invalid");
        if (seen.Count != fields.Length) throw new InvalidDataException("personal_profile_protocol_invalid");
        return root;
    }

    private static async Task<JsonDocument> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("personal_profile_frame_invalid");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("personal_profile_frame_invalid");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
