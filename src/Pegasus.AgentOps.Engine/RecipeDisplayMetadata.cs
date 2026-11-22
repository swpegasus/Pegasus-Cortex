using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.AgentOps.Engine;

public sealed record RecipeDisplayKey(string RecipeName, int DefinitionVersion, string? ScriptSha256, string Generation);
public sealed record RecipeDisplayProvenance(string SourceId, string SourceArtifactSha256, string SourceGroup, string SourceScope, bool IsFixture);
public sealed record RecipeDisplayDescriptor(RecipeDisplayKey Key, string Title, string Summary,
    string[] Steps, string[] Tools, string[] Inputs, string[] Outputs, string[] Conditions,
    string AuthorState, RecipeDisplayProvenance Provenance);
public sealed record RecipeDisplayCatalog(string Schema, RecipeDisplayDescriptor[] Records);
public sealed record RecipeDisplayMatch(string Status, RecipeDisplayDescriptor? Descriptor = null);
public sealed record RecipeListProjection(string Name, int DefinitionVersion, RecipeActionKind Action,
    string ToolName, ToolExecutionMode Mode, string Generation, bool HasDisplay);
public sealed record RecipeInputProjection(string Name, RecipeVariableType Type, bool Required);
public sealed record RecipeDetailProjection(string Name, int DefinitionVersion, RecipeActionKind Action,
    string ToolName, ToolExecutionMode Mode, string Generation, int ArgumentCount, RecipeInputProjection[] Inputs,
    string? Workspace, string? ScriptFileName, int? TimeoutSeconds, IReadOnlyList<int>? ExpectedExitCodes,
    ToolOutputBehavior OutputBehavior, int? MaxOutputBytes, string? PostconditionKind,
    string? ExecutableSha256, string? ScriptSha256, string DisplayStatus, RecipeDisplayDescriptor? Display);

/// <summary>Passive, optional author declarations. This store never changes recipe admission or execution.</summary>
public sealed class RecipeDisplaySnapshot
{
    private readonly IReadOnlyDictionary<RecipeDisplayKey, RecipeDisplayDescriptor> _records;
    private readonly string _status;
    internal RecipeDisplaySnapshot(string status, IReadOnlyDictionary<RecipeDisplayKey, RecipeDisplayDescriptor> records)
    { _status = status; _records = records; }
    public RecipeDisplayMatch Find(RecipeDisplayKey key)
    {
        if (_status != "available") return new(_status);
        if (_records.TryGetValue(key, out var descriptor)) return new("matched", descriptor);
        return new(_records.Keys.Any(item => item.RecipeName == key.RecipeName) ? "identity_mismatch" : "missing");
    }
}

public static class RecipeDisplayMetadata
{
    public const string Schema = "pegasus.agentops.recipe-display/1";
    public const string GenerationContract = "pegasus.agentops.recipe-definition-generation/1";
    public const string FileName = "recipe-display.v1.json";
    public const int MaximumCatalogBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };
    private static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web);

    // Input is the normalized existing registry DTO. No definition bytes leave
    // this method; callers receive only a domain-separated generation digest.
    public static string Generation(OneActionRecipeDefinition normalizedDefinition)
    {
        var element = JsonSerializer.SerializeToElement(normalizedDefinition, DefinitionJson);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, element);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(GenerationContract + "\n"));
        hash.AppendData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteRawValue(Quote(value.GetString()!)); break;
            case JsonValueKind.Number: writer.WriteNumberValue(value.GetInt64()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Unsupported recipe generation value.");
        }
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var character in text)
            result.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        return result.Append('"').ToString();
    }

    public static RecipeDisplaySnapshot LoadForRegistry(string registryPath)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(registryPath))!, FileName);
            if (!File.Exists(path)) return Empty("missing");
            PathNormalizer.RejectAbsoluteReparseComponents(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumCatalogBytes) return Empty("invalid");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or AgentOpsException)
        { return Empty("invalid"); }
    }

    public static RecipeDisplaySnapshot Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is < 1 or > MaximumCatalogBytes) return Empty("invalid");
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            var catalog = document.RootElement.Deserialize<RecipeDisplayCatalog>(Json);
            if (catalog is null || catalog.Schema != Schema || catalog.Records is null || catalog.Records.Length > 256) return Empty("invalid");
            var records = new Dictionary<RecipeDisplayKey, RecipeDisplayDescriptor>();
            foreach (var descriptor in catalog.Records)
            {
                Validate(descriptor);
                if (!records.TryAdd(descriptor.Key, descriptor)) return Empty("invalid");
            }
            return new("available", records);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or AgentOpsException)
        { return Empty("invalid"); }
    }

    public static RecipeListProjection[] ProjectList(IReadOnlyList<OneActionRecipeDefinition> recipes, RecipeDisplaySnapshot metadata) =>
        recipes.Select(recipe =>
        {
            var generation = Generation(recipe);
            return new RecipeListProjection(recipe.Name, recipe.DefinitionVersion, recipe.Action, recipe.ToolName, recipe.Mode,
                generation, metadata.Find(Key(recipe, generation)).Descriptor is not null);
        }).ToArray();

    public static RecipeDetailProjection ProjectDetail(OneActionRecipeDefinition recipe, string expectedGeneration, RecipeDisplaySnapshot metadata)
    {
        var generation = Generation(recipe);
        if (!IsSha(expectedGeneration) || generation != expectedGeneration) throw new AgentOpsException("precondition");
        var display = metadata.Find(Key(recipe, generation));
        return new(recipe.Name, recipe.DefinitionVersion, recipe.Action, recipe.ToolName, recipe.Mode, generation,
            recipe.FixedArguments?.Count ?? 0,
            recipe.Variables?.Select(variable => new RecipeInputProjection(variable.Name, variable.Type, variable.Required)).ToArray() ?? [],
            recipe.Workspace, recipe.ScriptPath is null ? null : Path.GetFileName(recipe.ScriptPath),
            recipe.TimeoutSeconds, recipe.ExpectedExitCodes, recipe.OutputBehavior, recipe.MaxOutputBytes,
            recipe.Postcondition?.Kind.ToString(), recipe.ExecutableSha256, recipe.ScriptSha256, display.Status, display.Descriptor);
    }

    public static RecipeDisplayMatch ReadProjected(JsonElement projection)
    {
        if (projection.ValueKind != JsonValueKind.Object || !projection.TryGetProperty("display", out var body) || body.ValueKind == JsonValueKind.Null)
            return new(projection.ValueKind == JsonValueKind.Object && projection.TryGetProperty("displayStatus", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString() is "missing" or "invalid" or "identity_mismatch" ? status.GetString()! : "unavailable" : "unavailable");
        try
        {
            RejectDuplicates(body);
            var descriptor = body.Deserialize<RecipeDisplayDescriptor>(Json) ?? throw new InvalidDataException(); Validate(descriptor);
            var key = new RecipeDisplayKey(projection.GetProperty("name").GetString()!, projection.GetProperty("definitionVersion").GetInt32(),
                projection.GetProperty("scriptSha256").ValueKind == JsonValueKind.Null ? null : projection.GetProperty("scriptSha256").GetString(),
                projection.GetProperty("generation").GetString()!);
            return key == descriptor.Key ? new("matched", descriptor) : new("identity_mismatch");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return new("invalid"); }
    }

    private static RecipeDisplayKey Key(OneActionRecipeDefinition recipe, string generation) => new(recipe.Name, recipe.DefinitionVersion, recipe.ScriptSha256, generation);
    private static RecipeDisplaySnapshot Empty(string status) => new(status, new Dictionary<RecipeDisplayKey, RecipeDisplayDescriptor>());
    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Plain(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid display text.");
    }
    private static void Lines(string[]? values)
    {
        if (values is null || values.Length > 12) throw new InvalidDataException("Invalid display list.");
        foreach (var value in values) Plain(value, 200);
    }
    private static void Validate(RecipeDisplayDescriptor descriptor)
    {
        if (descriptor is null || descriptor.Key is null || descriptor.Provenance is null) throw new InvalidDataException();
        ToolRecipeValidation.ValidateName(descriptor.Key.RecipeName);
        if (descriptor.Key.DefinitionVersion < 1 || !IsSha(descriptor.Key.Generation) || descriptor.Key.ScriptSha256 is not null && !IsSha(descriptor.Key.ScriptSha256)) throw new InvalidDataException();
        if (descriptor.AuthorState is not ("declared" or "held" or "prepared")) throw new InvalidDataException();
        Plain(descriptor.Title, 128); Plain(descriptor.Summary, 512);
        Lines(descriptor.Steps); Lines(descriptor.Tools); Lines(descriptor.Inputs); Lines(descriptor.Outputs); Lines(descriptor.Conditions);
        Plain(descriptor.Provenance.SourceId, 128); Plain(descriptor.Provenance.SourceGroup, 128); Plain(descriptor.Provenance.SourceScope, 512);
        if (!IsSha(descriptor.Provenance.SourceArtifactSha256)) throw new InvalidDataException();
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
