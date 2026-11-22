using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

/// <summary>
/// Portable, content-preserving behavior pack. Task and machine history are
/// structurally excluded; content lives in bounded files rather than a ledger.
/// </summary>
public sealed record CortexFileBehaviorPackManifestV2(
    string Schema,
    string PackId,
    int PackRevision,
    string Description,
    bool IncludesTasks,
    bool IncludesMachineHistory,
    IReadOnlyList<CortexFileBehaviorRecordDescriptorV2> Records)
{
    public async ValueTask<IReadOnlyList<CortexFileBehaviorRecordV2>> LoadRecordsAsync(
        string packRoot,
        CancellationToken cancellationToken = default)
    {
        Validate();
        var root = ValidatePackRoot(packRoot);
        var records = new List<CortexFileBehaviorRecordV2>(Records.Count);
        foreach (var descriptor in Records)
        {
            descriptor.Validate();
            var path = ResolvePackRelative(root, descriptor.ContentPath);
            var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                path,
                CortexFileV2Limits.MaximumRecordUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
            var content = new UTF8Encoding(false, true).GetString(bytes);
            if (content.Length > CortexV2Limits.MaximumContentCharacters)
                throw new InvalidDataException($"Behavior record '{descriptor.RecordId}' exceeds the content bound.");
            records.Add(new CortexFileBehaviorRecordV2(descriptor, content));
        }
        return records;
    }

    public CortexFileBehaviorPackManifestV2 Validate()
    {
        if (Schema != CortexFileV2Schemas.BehaviorPack)
            throw new InvalidDataException("The behavior pack schema is unsupported.");
        CortexFileV2Validation.RequireIdentifier(PackId, nameof(PackId));
        if (PackRevision is < 0 or > 1_000_000)
            throw new InvalidDataException("The behavior pack revision is invalid.");
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 2048)
            throw new InvalidDataException("The behavior pack description is invalid.");
        if (IncludesTasks || IncludesMachineHistory)
            throw new InvalidDataException("Portable behavior packs cannot include tasks or machine history.");
        if (Records.Count > 4096 || Records.Select(value => value.RecordId).Distinct(StringComparer.Ordinal).Count() != Records.Count)
            throw new InvalidDataException("Behavior pack record identifiers must be unique and bounded.");
        if (Records.Select(value => value.ContentPath).Distinct(StringComparer.Ordinal).Count() != Records.Count)
            throw new InvalidDataException("Behavior pack content paths must be unique.");
        foreach (var record in Records)
            record.Validate();
        return this;
    }

    public static async ValueTask<CortexFileBehaviorPackManifestV2> ReadAsync(
        string packRoot,
        CancellationToken cancellationToken = default)
    {
        var root = ValidatePackRoot(packRoot);
        var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
            Path.Combine(root, "behavior-pack.json"),
            CortexFileV2Limits.MaximumGenerationManifestUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return (JsonSerializer.Deserialize<CortexFileBehaviorPackManifestV2>(
                bytes,
                CortexFileV2Json.Options) ?? throw new InvalidDataException("The behavior pack manifest is empty."))
                .Validate();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The behavior pack manifest is malformed.", exception);
        }
    }

    private static string ValidatePackRoot(string packRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        if (!Path.IsPathFullyQualified(packRoot))
            throw new ArgumentException("The behavior pack root must be absolute.", nameof(packRoot));
        var root = Path.GetFullPath(packRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || (System.IO.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The behavior pack root is unavailable or linked.");
        return root;
    }

    private static string ResolvePackRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("A behavior pack content path is not portable.");
        var segments = relative.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("A behavior pack content path contains traversal.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison) || !System.IO.File.Exists(path) ||
            (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A behavior pack content file is missing, linked, or escaped its pack.");
        }
        return path;
    }
}

public sealed record CortexFileBehaviorRecordDescriptorV2(
    string RecordId,
    CortexKnowledgeKindV2 Kind,
    CortexKnowledgeTierV2 Tier,
    string Title,
    IReadOnlyList<string> Tags,
    CortexProvenanceSourceV2 Source,
    string ContentPath,
    string? SourceRelativePath)
{
    internal void Validate()
    {
        CortexFileV2Validation.RequireIdentifier(RecordId, nameof(RecordId));
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > CortexV2Limits.MaximumTitleCharacters)
            throw new InvalidDataException("A behavior pack title is invalid.");
        if (Tags.Count > CortexV2Limits.MaximumTags ||
            Tags.Distinct(StringComparer.Ordinal).Count() != Tags.Count)
        {
            throw new InvalidDataException("Behavior pack tags must be unique and bounded.");
        }
        foreach (var tag in Tags)
            CortexFileV2Validation.RequireIdentifier(tag, nameof(Tags));

        var eligible = Kind switch
        {
            CortexKnowledgeKindV2.Tenet => Tier == CortexKnowledgeTierV2.Explicit &&
                Tags.Contains("generalized", StringComparer.Ordinal),
            CortexKnowledgeKindV2.Correction or CortexKnowledgeKindV2.Preference =>
                Tier == CortexKnowledgeTierV2.Explicit && Source == CortexProvenanceSourceV2.User,
            CortexKnowledgeKindV2.Workaround => Tier == CortexKnowledgeTierV2.Verified,
            _ => false
        };
        if (!eligible)
        {
            throw new InvalidDataException(
                "Behavior packs accept generalized tenets, explicit user corrections/preferences, and verified workarounds only.");
        }
        if (SourceRelativePath is not null &&
            (Path.IsPathFullyQualified(SourceRelativePath) ||
             SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)
                 .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                 .Any(segment => segment is "." or "..")))
        {
            throw new InvalidDataException(
                "Behavior pack source provenance must be portable, relative, and traversal-free.");
        }
    }
}

public sealed record CortexFileBehaviorRecordV2(
    CortexFileBehaviorRecordDescriptorV2 Descriptor,
    string Content);
