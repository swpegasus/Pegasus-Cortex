using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

/// <summary>
/// Portable project-to-namespace attachment. The project root is supplied by
/// the caller; this document deliberately contains no machine path or runtime
/// configuration.
/// </summary>
public sealed record CortexFileProjectManifestV2(
    string Schema,
    string ProjectId,
    string WriteNamespace,
    IReadOnlyList<string> ReadNamespaces,
    int HotMaximumUtf8Bytes = CortexFileV2Limits.MaximumHotUtf8Bytes,
    int ChunkMaximumRecords = CortexFileV2Limits.DefaultChunkMaximumRecords,
    int ChunkMaximumUtf8Bytes = CortexFileV2Limits.DefaultChunkMaximumUtf8Bytes)
{
    public const string CurrentSchema = "pegasus.cortex.file-project/2.0";
    public const string LegacySchemaV1 = "pegasus.cortex.file-project/1.0";

    public CortexFileProjectManifestV2 Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new ArgumentException("The file Cortex project manifest schema is unsupported.", nameof(Schema));
        CortexFileV2Validation.RequireIdentifier(ProjectId, nameof(ProjectId));
        CortexFileV2Validation.RequireIdentifier(WriteNamespace, nameof(WriteNamespace));
        ArgumentNullException.ThrowIfNull(ReadNamespaces);
        if (ReadNamespaces.Count is < 1 or > CortexFileV2Limits.MaximumAttachedNamespaces)
            throw new ArgumentOutOfRangeException(nameof(ReadNamespaces));
        var ordered = ReadNamespaces
            .Select(value => CortexFileV2Validation.RequireIdentifier(value, nameof(ReadNamespaces)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length != ReadNamespaces.Count ||
            !ordered.Contains(WriteNamespace, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Read namespaces must be unique and include the writable namespace.",
                nameof(ReadNamespaces));
        }
        if (HotMaximumUtf8Bytes is < CortexFileV2Limits.MinimumHotUtf8Bytes or
            > CortexFileV2Limits.MaximumHotUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(HotMaximumUtf8Bytes));
        }
        if (ChunkMaximumRecords is < 1 or > CortexFileV2Limits.MaximumChunkRecords)
            throw new ArgumentOutOfRangeException(nameof(ChunkMaximumRecords));
        if (ChunkMaximumUtf8Bytes is < CortexFileV2Limits.MinimumChunkUtf8Bytes or
            > CortexFileV2Limits.MaximumChunkUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkMaximumUtf8Bytes));
        }
        return this with { ReadNamespaces = Array.AsReadOnly(ordered) };
    }

    public static async ValueTask<CortexFileProjectManifestV2> ReadAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var path = CortexFileV2PathPolicy.ManifestPath(root);
        var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
            path,
            CortexFileV2Limits.MaximumManifestUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("schema", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The file Cortex project manifest has no schema.");
            }
            var schema = schemaElement.GetString();
            if (string.Equals(schema, CurrentSchema, StringComparison.Ordinal))
            {
                return (JsonSerializer.Deserialize<CortexFileProjectManifestV2>(
                        bytes,
                        CortexFileV2Json.Options) ??
                    throw new InvalidDataException("The file Cortex project manifest is empty."))
                    .Validate();
            }
            if (string.Equals(schema, LegacySchemaV1, StringComparison.Ordinal))
                return ReadLegacyV1(bytes).Validate();
            throw new InvalidDataException("The file Cortex project manifest schema is unsupported.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The file Cortex project manifest is malformed.",
                exception);
        }
    }

    private static CortexFileProjectManifestV2 ReadLegacyV1(byte[] bytes)
    {
        var legacy = JsonSerializer.Deserialize<CortexFileProjectManifestV1>(bytes, CortexFileV2Json.Options) ??
            throw new InvalidDataException("The legacy file Cortex project manifest is empty.");
        if (!string.Equals(legacy.Schema, LegacySchemaV1, StringComparison.Ordinal))
            throw new InvalidDataException("The legacy file Cortex project manifest schema is unsupported.");
        CortexFileV2Validation.RequireIdentifier(legacy.ProjectId, nameof(legacy.ProjectId));
        var usesSingleNamespace = legacy.Namespace is not null &&
            legacy.WriteNamespace is null && legacy.ReadNamespaces is null;
        var usesExplicitBinding = legacy.Namespace is null &&
            legacy.WriteNamespace is not null && legacy.ReadNamespaces is not null;
        if (!usesSingleNamespace && !usesExplicitBinding)
            throw new InvalidDataException("The legacy project namespace binding is ambiguous or incomplete.");
        var writeNamespace = CortexFileV2Validation.RequireIdentifier(
            usesSingleNamespace ? legacy.Namespace : legacy.WriteNamespace,
            nameof(legacy.WriteNamespace));
        var readNamespaces = usesSingleNamespace
            ? new[] { writeNamespace }
            : legacy.ReadNamespaces!
                .Select(value => CortexFileV2Validation.RequireIdentifier(value, nameof(legacy.ReadNamespaces)))
                .ToArray();
        if (readNamespaces.Length is < 1 or > CortexFileV2Limits.MaximumAttachedNamespaces ||
            readNamespaces.Distinct(StringComparer.Ordinal).Count() != readNamespaces.Length ||
            !readNamespaces.Contains(writeNamespace, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The legacy project namespace binding is invalid.");
        }
        var hotMaximumUtf8Bytes = legacy.HotMaximumUtf8Bytes ?? CortexFileV2Limits.MaximumHotUtf8Bytes;
        if (hotMaximumUtf8Bytes is < CortexFileV2Limits.MinimumHotUtf8Bytes or
            > CortexFileV2Limits.MaximumHotUtf8Bytes)
        {
            throw new InvalidDataException("The legacy project HOT bound is invalid.");
        }
        return new CortexFileProjectManifestV2(
            CurrentSchema,
            legacy.ProjectId,
            writeNamespace,
            readNamespaces,
            hotMaximumUtf8Bytes,
            CortexFileV2Limits.DefaultChunkMaximumRecords,
            CortexFileV2Limits.DefaultChunkMaximumUtf8Bytes);
    }

    public async ValueTask WriteAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate();
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var cortexRoot = CortexFileV2PathPolicy.PrepareOrdinaryDirectory(
            Path.Combine(root, ".cortex"),
            root);
        var path = CortexFileV2PathPolicy.ManifestPath(root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated, CortexFileV2Json.Options);
        await CortexFileV2AtomicIo.WriteAtomicAsync(
            path,
            bytes,
            CortexFileV2Limits.MaximumManifestUtf8Bytes,
            cortexRoot,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record CortexFileProjectManifestV1(
    string Schema,
    string ProjectId,
    string? Namespace,
    string? WriteNamespace,
    IReadOnlyList<string>? ReadNamespaces,
    int? HotMaximumUtf8Bytes);

public static class CortexFileV2Limits
{
    public const int MaximumHotUtf8Bytes = 4096;
    public const int MinimumHotUtf8Bytes = 512;
    public const int MaximumManifestUtf8Bytes = 16 * 1024;
    public const int MaximumAttachedNamespaces = CortexV2Limits.MaximumNamespaces;
    public const int DefaultChunkMaximumRecords = 32;
    public const int MaximumChunkRecords = 64;
    public const int DefaultChunkMaximumUtf8Bytes = 64 * 1024;
    public const int MinimumChunkUtf8Bytes = 4 * 1024;
    public const int MaximumChunkUtf8Bytes = 128 * 1024;
    // A contract-valid 24 KiB UTF-16 string can expand to roughly 144 KiB
    // when every control is JSON escaped; the envelope remains below 256 KiB.
    public const int MaximumRecordUtf8Bytes = 256 * 1024;
    public const int MaximumGenerationManifestUtf8Bytes = 64 * 1024;
    public const int MaximumPointerUtf8Bytes = 2048;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumSearchCharacters = 512;
    public const int MaximumSearchResults = 32;
}

internal static class CortexFileV2Validation
{
    public static string RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexFileV2Limits.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "The identifier must be bounded portable ASCII.",
                parameterName);
        }
        return value;
    }
}

internal static class CortexFileV2Json
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    static CortexFileV2Json() => Options.Converters.Add(new JsonStringEnumConverter(
        JsonNamingPolicy.CamelCase,
        allowIntegerValues: false));
}
