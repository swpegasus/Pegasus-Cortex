using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal sealed record CortexBehaviorPackAdoptionRecordV1(
    string RecordId,
    long ExpectedRevision);

internal sealed record CortexBehaviorPackAdoptionManifestV1(
    string Schema,
    string PackId,
    int PackRevision,
    string PriorSourceId,
    CortexProvenanceSourceV2 PriorSource,
    IReadOnlyList<CortexBehaviorPackAdoptionRecordV1> Records)
{
    internal const string Contract = "pegasus.cortex.behavior-pack-adoption/1.0";

    internal static async ValueTask<CortexBehaviorPackAdoptionManifestV1> ReadAsync(
        string path,
        string expectedPackId,
        int expectedPackRevision,
        string targetNamespace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The behavior-pack adoption manifest path must be absolute.", nameof(path));
        var canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical) || Directory.Exists(canonical) ||
            (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The behavior-pack adoption manifest is unavailable or linked.", canonical);
        var info = new FileInfo(canonical);
        if (info.Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("The behavior-pack adoption manifest exceeds its bound.");
        await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var manifest = await JsonSerializer.DeserializeAsync<CortexBehaviorPackAdoptionManifestV1>(
            stream,
            options,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The behavior-pack adoption manifest is empty.");
        if (manifest.Schema != Contract || manifest.PackId != expectedPackId ||
            manifest.PackRevision != expectedPackRevision ||
            string.IsNullOrWhiteSpace(manifest.PriorSourceId) ||
            manifest.PriorSourceId.Length > CortexV2Limits.MaximumIdentifierCharacters ||
            manifest.PriorSourceId.Any(char.IsControl) ||
            manifest.Records.Count is < 1 or > 256 ||
            manifest.Records.Select(value => value.RecordId).Distinct(StringComparer.Ordinal).Count() != manifest.Records.Count ||
            manifest.Records.Any(value => string.IsNullOrWhiteSpace(value.RecordId) || value.RecordId.Any(char.IsControl) ||
                value.ExpectedRevision < 1))
            throw new InvalidDataException("The behavior-pack adoption manifest does not match the selected pack generation.");
        _ = targetNamespace;
        return manifest;
    }

    internal IReadOnlyList<SqliteCortexV2KnowledgeSeedAdoption> ToSeeds(string targetNamespace) =>
        Records.Select(value => new SqliteCortexV2KnowledgeSeedAdoption(
            PackId, PackRevision, targetNamespace, value.RecordId, value.ExpectedRevision,
            PriorSourceId, PriorSource)).ToArray();
}
