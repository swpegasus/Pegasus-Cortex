using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.DefaultLibrary.Contracts;

namespace Pegasus.Cortex.DefaultLibrary.Serialization;

public static class KnowledgeBundleSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Export(KnowledgeSeedBundleV1 bundle) =>
        JsonSerializer.SerializeToUtf8Bytes(bundle, Options);

    public static string ExportText(KnowledgeSeedBundleV1 bundle) =>
        Encoding.UTF8.GetString(Export(bundle));

    public static KnowledgeSeedBundleV1 Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<KnowledgeSeedBundleV1>(utf8Json, Options)
        ?? throw new JsonException("The default-library bundle was empty.");

    public static KnowledgeSeedBundleV1 Deserialize(string json) =>
        Deserialize(Encoding.UTF8.GetBytes(json));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            WriteIndented = false,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public static class KnowledgeRecordFactory
{
    private static readonly string EmptyHash = new('0', 64);

    public static KnowledgeRecordV1 Create(
        string @namespace,
        string taxonomy,
        string slug,
        string title,
        string summary,
        string content,
        IEnumerable<string> keywords,
        IEnumerable<KnowledgeApplicability> applicability,
        IEnumerable<KnowledgeEvidence> evidence,
        KnowledgeProvenance provenance,
        KnowledgeConfidence confidence,
        KnowledgeFreshness freshness,
        string sensitivity,
        KnowledgeRetention retention,
        KnowledgeApproval approval,
        KnowledgePromotion promotion,
        IEnumerable<string>? supersedesRevisionIds,
        DateTimeOffset createdAt,
        string recordStatus = "active")
    {
        var normalizedNamespace = NormalizeRequired(@namespace);
        var normalizedTaxonomy = NormalizeRequired(taxonomy);
        var normalizedSlug = NormalizeRequired(slug).ToLowerInvariant();
        var recordId = ComputeRecordId(normalizedNamespace, normalizedTaxonomy, normalizedSlug);

        var record = new KnowledgeRecordV1(
            SchemaVersion: 1,
            RecordId: recordId,
            RevisionId: string.Empty,
            Namespace: normalizedNamespace,
            Taxonomy: normalizedTaxonomy,
            Slug: normalizedSlug,
            Title: NormalizeRequired(title),
            Summary: NormalizeRequired(summary),
            Content: NormalizeRequired(content),
            Keywords: keywords.Select(NormalizeRequired).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Applicability: applicability.OrderBy(a => a.ProductId, StringComparer.Ordinal)
                .ThenBy(a => a.ComponentId, StringComparer.Ordinal).ToArray(),
            Evidence: evidence.OrderBy(e => e.Reference, StringComparer.Ordinal).ToArray(),
            Provenance: provenance with
            {
                SourceRecordIds = provenance.SourceRecordIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Transformations = provenance.Transformations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            },
            Confidence: confidence,
            Freshness: freshness,
            Sensitivity: NormalizeRequired(sensitivity),
            Retention: retention,
            RecordStatus: NormalizeRequired(recordStatus),
            Approval: approval,
            Promotion: promotion,
            Supersession: new KnowledgeSupersession(
                (supersedesRevisionIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            TrainingEligibility: false,
            ContentTreatment: KnowledgeContractIds.ContentTreatment,
            Executable: false,
            CreatedAt: createdAt,
            ContentHash: EmptyHash);

        var contentHash = ComputeContentHash(record);
        return record with { RevisionId = $"krev_{contentHash}", ContentHash = contentHash };
    }

    public static KnowledgeSeedBundleV1 CreateBundle(
        string releaseVersion,
        string nuGetVersion,
        DateTimeOffset generatedAt,
        IEnumerable<KnowledgeTaxonomyDefinition> taxonomies,
        IEnumerable<KnowledgeRecordV1> records)
    {
        var normalizedRelease = NormalizeRequired(releaseVersion);
        var bundleId = $"kbundle_{Sha256(Encoding.UTF8.GetBytes($"{KnowledgeContractIds.SeedBundle}\n{normalizedRelease}"))}";
        var bundle = new KnowledgeSeedBundleV1(
            SchemaVersion: 1,
            ContractId: KnowledgeContractIds.SeedBundle,
            BundleId: bundleId,
            ReleaseVersion: normalizedRelease,
            NuGetVersion: NormalizeRequired(nuGetVersion),
            GeneratedAt: generatedAt,
            Taxonomies: taxonomies.OrderBy(t => t.Code, StringComparer.Ordinal).ToArray(),
            Records: records.OrderBy(r => r.RecordId, StringComparer.Ordinal).ThenBy(r => r.RevisionId, StringComparer.Ordinal).ToArray(),
            ContentTreatment: KnowledgeContractIds.ContentTreatment,
            Executable: false,
            BundleHash: EmptyHash);

        return bundle with { BundleHash = ComputeBundleHash(bundle) };
    }

    public static string ComputeRecordId(string @namespace, string taxonomy, string slug) =>
        $"krec_{Sha256(Encoding.UTF8.GetBytes($"{NormalizeRequired(@namespace)}\n{NormalizeRequired(taxonomy)}\n{NormalizeRequired(slug).ToLowerInvariant()}"))}";

    public static string ComputeContentHash(KnowledgeRecordV1 record)
    {
        var canonical = record with { RevisionId = string.Empty, ContentHash = EmptyHash };
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(canonical, KnowledgeBundleSerializer.Options));
    }

    public static string ComputeBundleHash(KnowledgeSeedBundleV1 bundle)
    {
        var canonical = bundle with { BundleHash = EmptyHash };
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(canonical, KnowledgeBundleSerializer.Options));
    }

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
