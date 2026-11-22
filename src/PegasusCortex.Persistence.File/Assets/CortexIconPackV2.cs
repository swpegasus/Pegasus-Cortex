using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Pegasus.Cortex.Persistence.File.V2;

namespace Pegasus.Cortex.Persistence.File.Assets;

/// <summary>Small semantic metadata, not loaded artwork or a namespace access grant.</summary>
public sealed record CortexIconPackEntryV2(
    string Id,
    string Alias,
    string Title,
    string Meaning,
    string Text,
    string MediaType,
    string FilePath,
    long Size,
    string Sha256,
    string License,
    string Source,
    string PackId,
    string PackVersion);

/// <summary>The exact portable files that must follow an imported asset, including its notices.</summary>
public sealed record CortexIconPackFileV2(
    string FilePath, long Size, string Sha256, string MediaType, string Role);

/// <summary>
/// Read-only admission of the producer's frozen pegasus.asset-pack/1 INI contract into
/// Cortex's V2 asset layer. Validates one bounded file at a time; never renders, fetches
/// URLs, executes content, or retains artwork bytes. The supplied pins are mandatory.
/// </summary>
public sealed partial class CortexIconPackV2
{
    public const string ProducerContract = "pegasus.asset-pack/1";
    public const int MaximumCatalogBytes = 64 * 1024;
    public const int MaximumAssetBytes = 128 * 1024;
    public const int MaximumIcons = 64;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, CortexIconPackFileV2> filesByPath;

    private CortexIconPackV2(
        string root, string expectedCatalogSha256, string expectedContentSha256,
        CancellationToken cancellationToken)
    {
        ValidatePortableAbsolutePath(root);
        Root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(root);
        var catalog = Read("catalog.ini", MaximumCatalogBytes);
        CatalogSha256 = Digest(catalog);
        if (CatalogSha256 != RequireDigest(expectedCatalogSha256))
            throw new InvalidDataException("Icon catalog does not match its expected SHA-256.");

        var sections = Parse(Utf8.GetString(catalog));
        if (!sections.TryGetValue("pack", out var header))
            throw new InvalidDataException("Missing icon pack header.");
        Exact(header, ["contract", "id", "version", "namespace", "license", "license-file",
            "notice-file", "publisher", "upstream", "revision", "viewport"]);
        if (header["contract"] != ProducerContract || header["license"] != "MIT" || header["viewport"] != "20")
            throw new InvalidDataException("Unsupported passive icon pack contract, license, or viewport.");

        Id = header["id"];
        Version = header["version"];
        NamespacePrefix = header["namespace"];
        if (!PackIdPattern().IsMatch(Id) || !System.Version.TryParse(Version, out _) || NamespacePrefix != Id + ".")
            throw new InvalidDataException("Invalid icon pack identity.");
        License = header["license"];
        Publisher = RequiredText(header, "publisher", 128);
        SourceRevision = RequiredText(header, "revision", 128);
        Upstream = Https(header["upstream"]);

        var files = new SortedDictionary<string, CortexIconPackFileV2>(StringComparer.Ordinal)
        {
            ["catalog.ini"] = new("catalog.ini", catalog.LongLength, CatalogSha256, "text/plain", "catalog")
        };
        var licensingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "license-file", "notice-file" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = header[name];
            if (!relative.StartsWith("Licensing/", StringComparison.Ordinal) ||
                !relative.EndsWith(".txt", StringComparison.Ordinal) || !licensingPaths.Add(relative))
                throw new InvalidDataException("Distinct required licensing files must remain in Licensing.");
            var bytes = Read(relative, MaximumCatalogBytes);
            if (bytes.Length == 0)
                throw new InvalidDataException("Empty icon licensing file.");
            _ = Utf8.GetString(bytes);
            files.Add(relative, new(relative, bytes.LongLength, Digest(bytes), "text/plain",
                name == "license-file" ? "license" : "notice"));
        }

        var entries = new List<CortexIconPackEntryV2>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, item) in sections.Where(pair => pair.Key != "pack").OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!key.StartsWith(NamespacePrefix, StringComparison.Ordinal) || !IconIdPattern().IsMatch(key))
                throw new InvalidDataException("Icon ID must remain in its pack's declared artwork namespace.");
            Exact(item, ["alias", "label", "description", "file", "sha256", "text", "source"]);
            var alias = RequiredText(item, "alias", 64);
            if (!AliasPattern().IsMatch(alias) || !aliases.Add(alias))
                throw new InvalidDataException("Invalid or duplicate icon compatibility alias.");
            var relative = item["file"];
            if (!relative.StartsWith("svg/", StringComparison.Ordinal) || !relative.EndsWith(".svg", StringComparison.Ordinal))
                throw new InvalidDataException("Icons must reference contained SVG assets.");
            var bytes = Read(relative, MaximumAssetBytes);
            var sha256 = RequireDigest(item["sha256"]);
            if (Digest(bytes) != sha256)
                throw new InvalidDataException("Icon bytes do not match their declared SHA-256.");
            ValidatePassiveSvg(bytes);
            entries.Add(new(key, alias, RequiredText(item, "label", 128), RequiredText(item, "description", 512),
                RequiredText(item, "text", 16), "image/svg+xml", relative, bytes.LongLength,
                sha256, License, Https(item["source"]), Id, Version));
            var manifest = new CortexIconPackFileV2(relative, bytes.LongLength, sha256, "image/svg+xml", "artwork");
            if (files.TryGetValue(relative, out var previous) && previous != manifest)
                throw new InvalidDataException("One icon path has conflicting file metadata.");
            files[relative] = manifest;
        }
        if (entries.Count is < 1 or > MaximumIcons || entries.Any(entry => sections.ContainsKey(entry.Alias)))
            throw new InvalidDataException("Invalid icon count or alias collision with a namespaced ID.");
        if (files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Icon file paths collide under Windows case folding.");

        // Producer compatibility: path NUL lowercase digest LF, sorted ordinal. Roots and timestamps do not participate.
        ContentSha256 = Digest(Utf8.GetBytes(string.Concat(files.Select(pair => pair.Key + "\0" + pair.Value.Sha256 + "\n"))));
        if (ContentSha256 != RequireDigest(expectedContentSha256))
            throw new InvalidDataException("Icon pack content does not match its expected canonical SHA-256.");
        Entries = entries.AsReadOnly();
        Files = files.Values.ToList().AsReadOnly();
        filesByPath = new ReadOnlyDictionary<string, CortexIconPackFileV2>(files);
    }

    public string Root { get; }
    public string Id { get; }
    public string Version { get; }
    public string NamespacePrefix { get; }
    public string License { get; }
    public string Publisher { get; }
    public string Upstream { get; }
    public string SourceRevision { get; }
    public string CatalogSha256 { get; }
    public string ContentSha256 { get; }
    public IReadOnlyList<CortexIconPackEntryV2> Entries { get; }
    public IReadOnlyList<CortexIconPackFileV2> Files { get; }

    public static CortexIconPackV2 Load(
        string root, string expectedCatalogSha256, string expectedContentSha256,
        CancellationToken cancellationToken = default) =>
        new(root, expectedCatalogSha256, expectedContentSha256, cancellationToken);

    /// <summary>
    /// Returns only a declared, unchanged file; the importer must still bind copied
    /// bytes to the returned manifest digest because a path is not an open file handle.
    /// </summary>
    public string GetFilePath(string relative)
    {
        if (!filesByPath.TryGetValue(relative, out var file))
            throw new KeyNotFoundException("The exact relative file is not in the admitted icon pack.");
        var bytes = Read(relative, file.Role == "artwork" ? MaximumAssetBytes : MaximumCatalogBytes);
        if (bytes.LongLength != file.Size || Digest(bytes) != file.Sha256)
            throw new InvalidDataException("An admitted icon pack file changed before import.");
        return Resolve(relative);
    }

    private string Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(part => !PortableSegment(part)))
            throw new InvalidDataException("A contained portable forward-slash relative icon path is required.");
        var path = CortexFileV2PathPolicy.FromProjectRelative(Root, relative);
        ValidatePortableAbsolutePath(path);
        return path;
    }

    private byte[] Read(string relative, int maximum)
    {
        var path = Resolve(relative);
        if ((System.IO.File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("A declared icon file is not an ordinary file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum)
            throw new InvalidDataException("An icon pack file exceeds its byte bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength)
            throw new IOException("An icon pack file changed while reading.");
        _ = Resolve(relative);
        return bytes;
    }

    private static void ValidatePortableAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute icon pack root is required.", nameof(path));
        var volume = Path.GetPathRoot(path) ?? throw new InvalidDataException("The icon path has no root.");
        if (path.Length > 240 || path[volume.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => !PortableSegment(part)))
            throw new InvalidDataException("An icon pack path exceeds the portable Windows path contract.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                // Exists() may report false for a dangling link. Inspect the entry itself instead.
                attributes = System.IO.File.GetAttributes(current);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An icon pack path crosses a link or reparse point.");
        }
    }

    private static bool PortableSegment(string part) =>
        part is not ("" or "." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
        Utf8.GetByteCount(part) <= 255 && !part.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)) &&
        !ReservedFileNamePattern().IsMatch(part.Split('.')[0]);

    private static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new(StringComparer.Ordinal);
                if (!result.TryAdd(line[1..^1], current) || result.Count > MaximumIcons + 1)
                    throw new InvalidDataException("Duplicate or excessive icon catalog sections.");
            }
            else
            {
                var equals = line.IndexOf('=');
                if (current is null || equals < 1 ||
                    !current.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim()))
                    throw new InvalidDataException("Invalid or duplicate icon catalog property.");
            }
        }
        return result;
    }

    // This is the producer's passive admission policy, not a geometry renderer. Clients retain their own rendering policy.
    private static void ValidatePassiveSvg(byte[] bytes)
    {
        try
        {
            using var input = new StringReader(Utf8.GetString(bytes));
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumAssetBytes
            });
            var document = XDocument.Load(reader);
            if (document.DescendantNodes().OfType<XProcessingInstruction>().Any())
                throw new InvalidDataException("SVG processing instructions are not passive icon assets.");
            XNamespace svg = "http://www.w3.org/2000/svg";
            if (document.Root?.Name != svg + "svg")
                throw new InvalidDataException("An SVG root is required.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "svg", "path", "g", "title", "desc" };
            var attributes = new HashSet<string>(StringComparer.Ordinal)
                { "width", "height", "viewBox", "fill", "fill-rule", "clip-rule", "d", "id" };
            foreach (var element in document.Descendants())
            {
                if (element.Name.Namespace != svg || !allowed.Contains(element.Name.LocalName))
                    throw new InvalidDataException("Active or unsupported SVG element.");
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (attribute.Value != svg.NamespaceName)
                            throw new InvalidDataException("A foreign SVG namespace declaration is not supported.");
                        continue;
                    }
                    if (attribute.Name.NamespaceName.Length != 0 || !attributes.Contains(attribute.Name.LocalName) ||
                        attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || attribute.Value.Length > 32768)
                        throw new InvalidDataException("Active or unsupported SVG attribute.");
                    if (attribute.Name.LocalName == "fill" && !PassiveFillPattern().IsMatch(attribute.Value))
                        throw new InvalidDataException("SVG paint must use an explicit passive fill literal.");
                    if (attribute.Name.LocalName is "fill-rule" or "clip-rule" && attribute.Value is not ("nonzero" or "evenodd"))
                        throw new InvalidDataException("Unsupported SVG fill rule.");
                }
            }
            if (!document.Descendants(svg + "path").Any())
                throw new InvalidDataException("SVG artwork has no path.");
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid or non-passive SVG document.", exception);
        }
    }

    private static string RequiredText(Dictionary<string, string> item, string name, int maximum)
    {
        var value = item[name];
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid bounded icon metadata: " + name);
        return value;
    }

    private static void Exact(Dictionary<string, string> item, string[] fields)
    {
        if (!item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("Unexpected icon catalog fields.");
    }

    private static string Https(string value)
    {
        if (value.Length > 2048 || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Publisher references must be bounded credential-free HTTPS URLs.");
        return value;
    }

    private static string RequireDigest(string value)
    {
        if (value is null || !Sha256Pattern().IsMatch(value))
            throw new InvalidDataException("An exact SHA-256 pin is required for icon admission.");
        return value.ToLowerInvariant();
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex("^[a-z][a-z0-9.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackIdPattern();
    [GeneratedRegex("^[a-z][a-z0-9.-]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex IconIdPattern();
    [GeneratedRegex("^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex("^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedFileNamePattern();
    [GeneratedRegex("^(?:none|currentColor|#[a-fA-F0-9]{3}|#[a-fA-F0-9]{4}|#[a-fA-F0-9]{6}|#[a-fA-F0-9]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex PassiveFillPattern();
}
