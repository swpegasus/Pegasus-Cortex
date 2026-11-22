using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.Serialization;

/// <summary>
/// Implements the language-neutral Pegasus canonical JSON subset: compact UTF-8,
/// ordinally sorted object names, preserved array order, minimal string escaping,
/// and JSON integers. It performs no provider, filesystem, or network I/O.
/// </summary>
public static class CortexCanonicalJson
{
    private static readonly JsonSerializerOptions CanonicalOptions = CreateOptions();

    static CortexCanonicalJson()
    {
        CanonicalOptions.MakeReadOnly(populateMissingResolver: true);
    }

    public static JsonSerializerOptions CreateSerializerOptions() => new(CanonicalOptions);

    public static byte[] Serialize<T>(T value)
    {
        var regular = JsonSerializer.SerializeToUtf8Bytes(value, CanonicalOptions);
        using var document = JsonDocument.Parse(regular, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        using var output = new MemoryStream(regular.Length);
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteCanonical(writer, document.RootElement);
        }
        return output.ToArray();
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, CanonicalOptions)
        ?? throw new JsonException($"The {typeof(T).Name} document was empty.");

    public static async ValueTask<T> DeserializeAsync<T>(
        Stream input,
        CancellationToken cancellationToken)
    {
        var value = await JsonSerializer.DeserializeAsync<T>(
            input,
            CanonicalOptions,
            cancellationToken).ConfigureAwait(false);
        return value ?? throw new JsonException($"The {typeof(T).Name} document was empty.");
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256(string value) =>
        Sha256(Encoding.UTF8.GetBytes(value));

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false,
        MaxDepth = 64,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported canonical JSON value kind '{element.ValueKind}'.");
        }
    }
}
