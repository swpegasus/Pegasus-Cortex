using System.Text.Json;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed record CortexFileStarterCorpusManifestV1(
    string Schema,
    string CorpusId,
    string Description,
    IReadOnlyList<CortexFileStarterCorpusPackV1> Packs)
{
    public const string CurrentSchema = "pegasus.cortex.starter-corpus/1.0";

    public CortexFileStarterCorpusManifestV1 Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException("The starter corpus schema is unsupported.");
        CortexFileV2Validation.RequireIdentifier(CorpusId, nameof(CorpusId));
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 2_048)
            throw new InvalidDataException("The starter corpus description is invalid.");
        if (Packs.Count is < 1 or > 32)
            throw new InvalidDataException("The starter corpus pack count is invalid.");
        if (Packs.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() != Packs.Count)
        {
            throw new InvalidDataException("Starter corpus pack paths must be unique.");
        }
        foreach (var pack in Packs)
            pack.Validate();
        return this;
    }

    public static async ValueTask<CortexFileStarterCorpusManifestV1> ReadAsync(
        string corpusRoot,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateCorpusRoot(corpusRoot);
        var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
            Path.Combine(root, "starter-corpus.json"),
            CortexFileV2Limits.MaximumGenerationManifestUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return (JsonSerializer.Deserialize<CortexFileStarterCorpusManifestV1>(
                bytes,
                CortexFileV2Json.Options) ??
                throw new InvalidDataException("The starter corpus manifest is empty."))
                .Validate();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The starter corpus manifest is malformed.", exception);
        }
    }

    public string ResolvePackRoot(string corpusRoot, CortexFileStarterCorpusPackV1 pack)
    {
        pack.Validate();
        var root = ValidateCorpusRoot(corpusRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, pack.Path));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison) ||
            !Directory.Exists(candidate) ||
            (System.IO.File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A starter corpus pack is missing, linked, or escaped its corpus root.");
        }
        return candidate;
    }

    private static string ValidateCorpusRoot(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        if (!Path.IsPathFullyQualified(corpusRoot))
            throw new ArgumentException("The starter corpus root must be absolute.", nameof(corpusRoot));
        var root = Path.GetFullPath(corpusRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || (System.IO.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The starter corpus root is unavailable or linked.");
        return root;
    }
}

public sealed record CortexFileStarterCorpusPackV1(string Namespace, string Path)
{
    internal void Validate()
    {
        CortexFileV2Validation.RequireIdentifier(Namespace, nameof(Namespace));
        if (string.IsNullOrWhiteSpace(Path) || System.IO.Path.IsPathFullyQualified(Path))
            throw new InvalidDataException("A starter corpus pack path is not portable.");
        var segments = Path.Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1 || segments[0] is "." or "..")
            throw new InvalidDataException("A starter corpus pack path must be one portable child directory.");
    }
}
