using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

public sealed record CortexLocalProjectRegistrationRequest(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string RootPath,
    bool AllowRead,
    bool AllowWrite);

public sealed record CortexMcpLocalStatePaths(
    string ProfileId,
    string StateRoot,
    string DatabasePath,
    string RegistryPath);

public sealed record CortexLocalRegistryProvisioningResult(
    string ProfileId,
    string StateRoot,
    string RegistryPath,
    string RegistryHash,
    string OsIdentityKind,
    string OsIdentityHash,
    int ProjectCount);

/// <summary>
/// Supported create-new provisioning surface. It creates only one previously
/// absent dedicated state leaf and its registry; it never changes a parent ACL
/// or any registered project root.
/// </summary>
public static class CortexLocalProjectRegistryProvisioner
{
    public static CortexMcpLocalStatePaths CurrentUserStatePaths(string profileId)
    {
        CortexLocalProjectRegistryPolicy.RequireProfile(profileId);
        var localRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new InvalidOperationException(
                "The current user's local application-data directory is unavailable.");
        var stateRoot = Path.GetFullPath(
            Path.Combine(localRoot, $"PegasusCortex-{profileId}"));
        _ = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            stateRoot,
            requireExisting: false,
            allowAbsent: true);
        return CreateStatePaths(profileId, stateRoot);
    }

    public static CortexMcpLocalStatePaths PortableStatePaths(
        string profileId,
        string stateRoot)
    {
        CortexLocalProjectRegistryPolicy.RequireProfile(profileId);
        var canonical = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            stateRoot,
            requireExisting: false,
            allowAbsent: true);
        if (!string.Equals(
                Path.GetFileName(canonical),
                profileId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The dedicated portable state-root leaf must equal the explicit profile identifier.",
                nameof(stateRoot));
        }
        return CreateStatePaths(profileId, canonical);
    }

    public static async ValueTask<CortexLocalRegistryProvisioningResult>
        CreateNewPortableAsync(
            string profileId,
            string stateRoot,
            IEnumerable<CortexLocalProjectRegistrationRequest> projects,
            CancellationToken cancellationToken)
    {
        var paths = PortableStatePaths(profileId, stateRoot);
        return await CreateNewAsync(paths, projects, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<CortexLocalRegistryProvisioningResult>
        CreateNewCurrentUserAsync(
            string profileId,
            IEnumerable<CortexLocalProjectRegistrationRequest> projects,
            CancellationToken cancellationToken)
    {
        var paths = CurrentUserStatePaths(profileId);
        return await CreateNewAsync(paths, projects, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<CortexLocalRegistryProvisioningResult>
        CreateNewAsync(
            CortexMcpLocalStatePaths paths,
            IEnumerable<CortexLocalProjectRegistrationRequest> projects,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        cancellationToken.ThrowIfCancellationRequested();
        var registrations = projects.Select(project =>
            CortexLocalProjectRegistryFile.CreateRegistration(
                project.TenantId,
                project.ProjectLinkId,
                project.ProjectId,
                project.ProjectNamespace,
                project.RootPath,
                project.AllowRead,
                project.AllowWrite)).ToArray();
        var document = CortexLocalProjectRegistryFile.Create(
            paths.ProfileId,
            registrations);
        var root = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            paths.StateRoot,
            requireExisting: false,
            allowAbsent: true);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException(
                "The dedicated local Cortex state root already exists; provisioning never overwrites it.");
        }
        var parent = Path.GetDirectoryName(root) ??
            throw new ArgumentException("The state root has no parent.", nameof(paths));
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The dedicated state root parent must already exist.");
        }
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);

        var created = false;
        try
        {
            Directory.CreateDirectory(root);
            created = true;
            CortexLocalRegistryAclPolicy.ProtectDirectory(root);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(root);
            await CortexLocalProjectRegistryFile.WriteNewProtectedAsync(
                paths.RegistryPath,
                document,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (created && Directory.Exists(root) &&
                !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
            throw;
        }

        return new CortexLocalRegistryProvisioningResult(
            paths.ProfileId,
            paths.StateRoot,
            paths.RegistryPath,
            document.RegistryHash,
            document.OsIdentityKind,
            document.OsIdentityHash,
            document.Projects.Count);
    }

    private static CortexMcpLocalStatePaths CreateStatePaths(
        string profileId,
        string stateRoot) => new(
            profileId,
            stateRoot,
            Path.Combine(stateRoot, "cortex.db"),
            Path.Combine(stateRoot, "projects.json"));
}

internal sealed record CortexLocalProjectRegistration(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string RootPath,
    string RootPathHash,
    bool AllowRead,
    bool AllowWrite);

internal sealed record CortexLocalProjectRegistryDocument(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string ProfileId,
    string OsIdentityKind,
    string OsIdentityHash,
    IReadOnlyList<CortexLocalProjectRegistration> Projects,
    string RegistryHash);

/// <summary>
/// Immutable exact-project authorization registry. Loading observes only the
/// registry, root entry metadata, and root ancestors; it never enumerates,
/// reads, writes, refreshes, or otherwise crawls a registered project root.
/// </summary>
internal sealed class CortexLocalProjectRegistry
{
    private readonly IReadOnlyDictionary<string, CortexLocalProjectRegistration> _byLink;

    private CortexLocalProjectRegistry(
        CortexLocalProjectRegistryDocument document,
        IReadOnlyDictionary<string, CortexLocalProjectRegistration> byLink)
    {
        Document = document;
        _byLink = byLink;
    }

    public CortexLocalProjectRegistryDocument Document { get; }
    public bool HasReadableProjects => Document.Projects.Any(item => item.AllowRead);
    public bool HasWritableProjects => Document.Projects.Any(item => item.AllowWrite);

    public bool TryAuthorizeRead(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        out CortexLocalProjectRegistration? registration) =>
        TryAuthorize(
            projectLinkId,
            projectId,
            projectNamespace,
            requireRead: true,
            requireWrite: false,
            out registration);

    public bool TryAuthorizeWrite(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        out CortexLocalProjectRegistration? registration) =>
        TryAuthorize(
            projectLinkId,
            projectId,
            projectNamespace,
            requireRead: false,
            requireWrite: true,
            out registration);

    public bool TryAuthorizeProjectBriefRead(
        string projectLinkId,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !candidate.AllowRead)
        {
            return false;
        }
        registration = candidate;
        return true;
    }

    /// <summary>
    /// Resolves all proposal scope from the protected registry and requires the
    /// project's explicit local write grant. No proposal argument can provide
    /// or widen tenant, project, or namespace identity.
    /// </summary>
    public bool TryAuthorizeProposalWrite(
        string projectLinkId,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !candidate.AllowWrite)
        {
            return false;
        }
        registration = candidate;
        return true;
    }

    /// <summary>
    /// Separately authorizes private proposal retrieval. Local write authority
    /// does not imply read authority, and proposal bytes remain project scoped.
    /// </summary>
    public bool TryAuthorizeProposalRead(
        string projectLinkId,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !candidate.AllowRead)
        {
            return false;
        }
        registration = candidate;
        return true;
    }

    /// <summary>
    /// Resolves the only source root the explicit recovery importer may read.
    /// Import is passive retention, so it requires the project's read grant
    /// and cannot accept caller-supplied tenant, namespace, or root authority.
    /// </summary>
    public bool TryAuthorizeRecoveryImport(
        string projectLinkId,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !candidate.AllowRead)
        {
            return false;
        }
        registration = candidate;
        return true;
    }

    /// <summary>
    /// Resolves the single protected-registry project whose passive retained
    /// bytes may be copied by the explicit one-shot recovery exporter.
    /// Export authority is read-only and cannot be supplied by command input.
    /// </summary>
    public bool TryAuthorizeRecoveryExport(
        string projectLinkId,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !candidate.AllowRead)
        {
            return false;
        }
        registration = candidate;
        return true;
    }

    public static async ValueTask<CortexLocalProjectRegistry> LoadAsync(
        string registryPath,
        string expectedProfileId,
        CancellationToken cancellationToken)
    {
        var canonicalPath = CortexLocalRegistryPathPolicy.ValidateRegistryPath(
            registryPath,
            requireExisting: true);
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(canonicalPath, CortexUserStateAccess.Read);

        await using var input = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 2 or > CortexLocalProjectRegistryPolicy.MaximumRegistryBytes)
            throw new InvalidDataException("The local project registry exceeds its byte boundary.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        CortexLocalProjectRegistryDocument document;
        try
        {
            document = CortexCanonicalJson.Deserialize<CortexLocalProjectRegistryDocument>(
                bytes);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The local project registry is not a supported typed document.",
                exception);
        }

        var canonical = CortexCanonicalJson.Serialize(document);
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("The local project registry is not exact canonical JSON.");
        var validated = CortexLocalProjectRegistryPolicy.Validate(
            document,
            expectedProfileId,
            validateRoots: true);
        var byLink = validated.Projects.ToDictionary(
            item => item.ProjectLinkId,
            StringComparer.Ordinal);
        return new CortexLocalProjectRegistry(validated, byLink);
    }

    private bool TryAuthorize(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        bool requireRead,
        bool requireWrite,
        out CortexLocalProjectRegistration? registration)
    {
        registration = null;
        if (!_byLink.TryGetValue(projectLinkId, out var candidate) ||
            !string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(
                candidate.ProjectNamespace,
                projectNamespace,
                StringComparison.Ordinal) ||
            requireRead && !candidate.AllowRead ||
            requireWrite && !candidate.AllowWrite)
        {
            return false;
        }
        registration = candidate;
        return true;
    }
}

/// <summary>
/// Canonical registry construction helper used by explicit configuration
/// workflows and isolated tests. It never creates or changes project roots.
/// </summary>
internal static class CortexLocalProjectRegistryFile
{
    public static CortexLocalProjectRegistryDocument Create(
        string profileId,
        IEnumerable<CortexLocalProjectRegistration> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var identity = CortexLocalOsIdentity.Capture();
        var document = new CortexLocalProjectRegistryDocument(
            CortexLocalProjectRegistryPolicy.SchemaVersion,
            CortexLocalProjectRegistryPolicy.ContractId,
            CortexLocalProjectRegistryPolicy.Canonicalization,
            profileId,
            identity.Kind,
            identity.Hash,
            Array.AsReadOnly(projects.Select(item => item with { }).ToArray()),
            CortexLocalProjectRegistryPolicy.EmptySha256);
        return CortexLocalProjectRegistryPolicy.Seal(document);
    }

    public static CortexLocalProjectRegistration CreateRegistration(
        string tenantId,
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string rootPath,
        bool allowRead,
        bool allowWrite)
    {
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(rootPath);
        return new CortexLocalProjectRegistration(
            tenantId,
            projectLinkId,
            projectId,
            projectNamespace,
            canonicalRoot,
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(canonicalRoot)),
            allowRead,
            allowWrite);
    }

    public static async ValueTask WriteNewProtectedAsync(
        string registryPath,
        CortexLocalProjectRegistryDocument document,
        CancellationToken cancellationToken)
    {
        var validated = CortexLocalProjectRegistryPolicy.Validate(
            document,
            document.ProfileId,
            validateRoots: true);
        var path = CortexLocalRegistryPathPolicy.ValidateRegistryPath(
            registryPath,
            requireExisting: false);
        var parent = Path.GetDirectoryName(path) ??
            throw new ArgumentException("The registry path has no parent.", nameof(registryPath));
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The protected registry state root does not exist.");
        }
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(parent);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);

        var bytes = CortexCanonicalJson.Serialize(validated);
        if (bytes.Length > CortexLocalProjectRegistryPolicy.MaximumRegistryBytes)
            throw new ArgumentOutOfRangeException(nameof(document));

        var created = false;
        try
        {
            await using (var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            CortexLocalRegistryAclPolicy.ProtectFile(path);
            CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(path);
        }
        catch
        {
            if (created && File.Exists(path))
                File.Delete(path);
            throw;
        }
    }
}

internal static class CortexLocalProjectRegistryPolicy
{
    public const int SchemaVersion = 1;
    public const string ContractId = "pegasus.cortex.local-project-registry/1.0";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const int MaximumRegistryBytes = 64 * 1024;
    public const int MaximumProjects = 64;
    public const int MaximumIdentifierCharacters = 128;
    public const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static void RequireProfile(string profileId)
    {
        if (!ValidProfile(profileId))
            throw new ArgumentException(
                "A bounded portable local profile identifier is required.",
                nameof(profileId));
    }

    public static CortexLocalProjectRegistryDocument Seal(
        CortexLocalProjectRegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Normalize(document with { RegistryHash = EmptySha256 });
        ValidateShape(normalized, normalized.ProfileId, validateRoots: true);
        var hash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(normalized));
        return normalized with { RegistryHash = hash };
    }

    public static CortexLocalProjectRegistryDocument Validate(
        CortexLocalProjectRegistryDocument document,
        string expectedProfileId,
        bool validateRoots)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Normalize(document);
        ValidateShape(normalized, expectedProfileId, validateRoots);
        var expectedHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(normalized with { RegistryHash = EmptySha256 }));
        if (!string.Equals(
                normalized.RegistryHash,
                expectedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local project registry self-hash is invalid.");
        }
        return normalized;
    }

    private static CortexLocalProjectRegistryDocument Normalize(
        CortexLocalProjectRegistryDocument document)
    {
        if (document.Projects is null)
            throw new InvalidDataException("The local project registry has no project collection.");
        var projects = document.Projects
            .Select(item => item is null
                ? throw new InvalidDataException("The local project registry contains a null project.")
                : item with { })
            .OrderBy(item => item.ProjectLinkId, StringComparer.Ordinal)
            .ToArray();
        return document with
        {
            Projects = Array.AsReadOnly(projects)
        };
    }

    private static void ValidateShape(
        CortexLocalProjectRegistryDocument document,
        string expectedProfileId,
        bool validateRoots)
    {
        var identity = CortexLocalOsIdentity.Capture();
        if (document.SchemaVersion != SchemaVersion ||
            !string.Equals(document.ContractId, ContractId, StringComparison.Ordinal) ||
            !string.Equals(
                document.Canonicalization,
                Canonicalization,
                StringComparison.Ordinal) ||
            !ValidProfile(document.ProfileId) ||
            !string.Equals(
                document.ProfileId,
                expectedProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.OsIdentityKind,
                identity.Kind,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.OsIdentityHash,
                identity.Hash,
                StringComparison.Ordinal) ||
            !ValidSha256(document.RegistryHash) ||
            document.Projects.Count > MaximumProjects)
        {
            throw new InvalidDataException(
                "The local project registry header, OS identity, profile, or bound is invalid.");
        }

        var links = new HashSet<string>(StringComparer.Ordinal);
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in document.Projects)
        {
            if (!ValidIdentifier(project.TenantId) ||
                !ValidPrefixedHash(project.ProjectLinkId, "plink_") ||
                !ValidIdentifier(project.ProjectId) ||
                !ValidIdentifier(project.ProjectNamespace) ||
                !ValidSha256(project.RootPathHash) ||
                !project.AllowRead && !project.AllowWrite ||
                !links.Add(project.ProjectLinkId) ||
                !projectIds.Add(project.ProjectId))
            {
                throw new InvalidDataException(
                    "A local project registration is invalid, duplicate, or has no allowed operation.");
            }

            var canonicalRoot = validateRoots
                ? CortexLocalRegistryPathPolicy.ValidateProjectRoot(project.RootPath)
                : project.RootPath;
            if (!string.Equals(canonicalRoot, project.RootPath, PathComparison) ||
                !string.Equals(
                    project.RootPathHash,
                    CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(canonicalRoot)),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A local project root path or path hash is invalid.");
            }
        }
    }

    private static bool ValidProfile(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        !value.EndsWith('.') &&
        !value.EndsWith(' ') &&
        !value.Contains("..", StringComparison.Ordinal);

    private static bool ValidIdentifier(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal static class CortexLocalRegistryPathPolicy
{
    private const int MaximumFinalPathCharacters = 240;
    private const int MaximumSegmentUtf8Bytes = 255;

    public static string ValidateStateRootPath(
        string value,
        bool requireExisting,
        bool allowAbsent)
    {
        var canonical = ValidateLocalAbsolutePath(value, "state root");
        var parent = Path.GetDirectoryName(canonical) ??
            throw new ArgumentException("The state root has no parent.", nameof(value));
        RequireNonRootParent(
            Path.Combine(canonical, "state.marker"),
            canonical,
            nameof(value));
        RequireSafeExistingAncestors(parent);
        if (File.Exists(canonical))
            throw new IOException("The local Cortex state root identifies a file.");
        if (Directory.Exists(canonical))
        {
            RequireSafeExistingAncestors(canonical);
            var info = new DirectoryInfo(canonical);
            if (info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The local Cortex state root cannot be a link or reparse point.");
            }
        }
        else if (requireExisting || !allowAbsent)
        {
            throw new DirectoryNotFoundException(
                "The dedicated local Cortex state root does not exist.");
        }
        return canonical;
    }

    public static string ValidateRegistryPath(
        string value,
        bool requireExisting)
    {
        var canonical = ValidateLocalAbsolutePath(value, "registry");
        if (!string.Equals(Path.GetExtension(canonical), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The local project registry must be a JSON file.", nameof(value));
        var parent = Path.GetDirectoryName(canonical) ??
            throw new ArgumentException("The registry path has no parent.", nameof(value));
        RequireNonRootParent(canonical, parent, nameof(value));
        RequireSafeExistingAncestors(parent);
        RequireSafeFileEntry(canonical);
        if (requireExisting && !File.Exists(canonical) ||
            !requireExisting && (File.Exists(canonical) || Directory.Exists(canonical)))
        {
            throw new ArgumentException(
                requireExisting
                    ? "The local project registry does not exist."
                    : "The new local project registry path is already occupied.",
                nameof(value));
        }
        return canonical;
    }

    public static string ValidateProjectRoot(string value)
    {
        var canonical = ValidateLocalAbsolutePath(value, "project root");
        if (!Directory.Exists(canonical))
            throw new ArgumentException("The registered project root does not exist.", nameof(value));
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(canonical) ?? string.Empty);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(canonical),
                root,
                PathComparison))
        {
            throw new ArgumentException("A filesystem root cannot be registered as a project.", nameof(value));
        }

        RequireSafeExistingAncestors(canonical);
        var info = new DirectoryInfo(canonical);
        RequireOrdinaryProjectRootEntry(info.Attributes, info.LinkTarget);
        CortexLocalRegistryAclPolicy.RequireCurrentUserCanUseProjectRoot(canonical);
        return canonical;
    }

    internal static void RequireOrdinaryProjectRootEntry(
        FileAttributes attributes,
        string? linkTarget)
    {
        if (linkTarget is not null ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A registered project root cannot be a link or reparse point.");
        }
    }

    public static void RequireSafeExistingAncestors(string path)
    {
        for (var current = new DirectoryInfo(path);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists)
                continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                current.LinkTarget is not null)
            {
                throw new IOException(
                    "Local Cortex registry paths cannot cross a link or reparse point.");
            }
        }
    }

    private static string ValidateLocalAbsolutePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The {description} requires a fully qualified local path.",
                nameof(value));
        }

        var canonical = Path.GetFullPath(value);
        if (!string.Equals(canonical, value, PathComparison) ||
            canonical.Length > MaximumFinalPathCharacters)
        {
            throw new ArgumentException(
                $"The {description} path is not canonical or exceeds 240 characters.",
                nameof(value));
        }
        if (OperatingSystem.IsWindows())
        {
            if (canonical.AsSpan(2).Contains(':'))
                throw new ArgumentException("Alternate data streams are not allowed.", nameof(value));
            var drive = new DriveInfo(canonical);
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or
                DriveType.NoRootDirectory or DriveType.Unknown)
            {
                throw new ArgumentException(
                    $"The {description} must use a local drive.",
                    nameof(value));
            }
        }

        var rootLength = (Path.GetPathRoot(canonical) ?? string.Empty).Length;
        foreach (var segment in canonical[rootLength..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                Encoding.UTF8.GetByteCount(segment) > MaximumSegmentUtf8Bytes)
            {
                throw new ArgumentException(
                    $"The {description} contains an unsafe path segment.",
                    nameof(value));
            }
        }
        return canonical;
    }

    private static void RequireSafeFileEntry(string path)
    {
        if (Directory.Exists(path))
            throw new IOException("The local project registry path identifies a directory.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The local project registry cannot be a link or reparse point.");
        }
    }

    private static void RequireNonRootParent(
        string canonical,
        string parent,
        string parameterName)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(canonical) ?? string.Empty);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                root,
                PathComparison))
        {
            throw new ArgumentException(
                "The local project registry cannot be placed at a filesystem root.",
                parameterName);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal static class CortexLocalRegistryAclPolicy
{

    public static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsDirectory(path);
            return;
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }

    public static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsFile(path);
            return;
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RequireProtectedFileAndParent(string path,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        var parent = Path.GetDirectoryName(path) ??
            throw new InvalidDataException("The local project registry has no parent.");
        if (OperatingSystem.IsWindows())
        {
            RequireProtectedWindowsEntry(parent, directory: true, requiredAccess);
            RequireProtectedWindowsEntry(path, directory: false, requiredAccess);
            return;
        }
        RequireUnixMode(
            parent,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        RequireUnixMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RequireProtectedDirectory(string path) =>
        RequireProtectedDirectory(path, CortexUserStateAccess.Modify);

    public static void RequireProtectedDirectory(string path, CortexUserStateAccess requiredAccess)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireProtectedWindowsEntry(path, directory: true, requiredAccess);
            return;
        }
        RequireUnixMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }

    public static void RequireCurrentUserCanUseProjectRoot(string path)
    {
        if (OperatingSystem.IsWindows())
            RequireWindowsProjectRootAccess(path);
        // Portable BCL APIs do not expose a Unix path owner. The registry file
        // remains the strict current-user authorization boundary on Unix.
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsDirectory(string path)
    {
        new DirectoryInfo(path).SetAccessControl(CreatePrivateWindowsDirectorySecurity());
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreatePrivateWindowsDirectorySecurity() =>
        CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(CurrentWindowsSid());

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsFile(string path)
    {
        new FileInfo(path).SetAccessControl(
            CortexUserStateDirectories.CreateWindowsUserFileSecurity(CurrentWindowsSid()));
    }

    [SupportedOSPlatform("windows")]
    private static void RequireProtectedWindowsEntry(string path, bool directory,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        var identity = CurrentWindowsSid();
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
        CortexUserStateDirectories.RequireWindowsUserStateAccess(security, identity, requiredAccess);
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsProjectRootAccess(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var principal = new WindowsPrincipal(identity);
        var presentTokenSids = new HashSet<string>(StringComparer.Ordinal)
        {
            currentUser.Value
        };
        var enabledTokenSids = new HashSet<string>(StringComparer.Ordinal)
        {
            currentUser.Value
        };
        if (identity.Groups is not null)
        {
            foreach (var group in identity.Groups.OfType<SecurityIdentifier>())
            {
                presentTokenSids.Add(group.Value);
                if (principal.IsInRole(group))
                    enabledTokenSids.Add(group.Value);
            }
        }

        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var trustedInheritedWriterSids = TrustedInheritedWorkspaceWriters(
            path,
            security,
            currentUser);
        RequireWindowsProjectRootAcl(
            security,
            currentUser,
            enabledTokenSids,
            presentTokenSids,
            trustedInheritedWriterSids);
    }

    /// <summary>
    /// Validates a selected project root without rewriting its collaborative ACL.
    /// The existing owner is a designated project authority, while the caller must
    /// independently receive effective modify access from an acceptable principal.
    /// Broad or unrelated writable principals still fail closed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void RequireWindowsProjectRootAcl(
        FileSystemSecurity security,
        SecurityIdentifier currentUser,
        IReadOnlySet<string> enabledTokenSids,
        IReadOnlySet<string> presentTokenSids,
        IReadOnlySet<string>? trustedInheritedWriterSids = null)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(enabledTokenSids);
        ArgumentNullException.ThrowIfNull(presentTokenSids);

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !IsAcceptableProjectRootOwner(owner))
        {
            throw new UnauthorizedAccessException(
                "A registered project root has an unacceptable owner authority.");
        }

        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        uint allowed = 0;
        uint denied = 0;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                throw new UnauthorizedAccessException(
                    "A registered project root contains an unreadable access-control identity.");
            }

            var rights = ExpandGenericRights(rule.FileSystemRights);
            if (rule.AccessControlType == AccessControlType.Allow &&
                HasWriteAuthority(rights) &&
                !IsTrustedProjectWriter(
                    sid,
                    owner,
                    currentUser,
                    enabledTokenSids,
                    rule.IsInherited,
                    trustedInheritedWriterSids))
            {
                throw new UnauthorizedAccessException(
                    "A registered project root grants write authority to an untrusted OS principal.");
            }

            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                continue;
            if (rule.AccessControlType == AccessControlType.Deny &&
                presentTokenSids.Contains(sid.Value))
            {
                denied |= rights;
            }
            else if (rule.AccessControlType == AccessControlType.Allow &&
                     enabledTokenSids.Contains(sid.Value))
            {
                allowed |= rights;
            }
        }

        var required = unchecked((uint)(int)FileSystemRights.Modify);
        if ((denied & required) != 0 || (allowed & required) != required)
        {
            throw new UnauthorizedAccessException(
                "The current OS user does not have effective modify access to the registered project root.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAcceptableProjectRootOwner(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        !IsBroadUntrustedIdentity(owner);

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedProjectWriter(
        SecurityIdentifier sid,
        SecurityIdentifier owner,
        SecurityIdentifier currentUser,
        IReadOnlySet<string> enabledTokenSids,
        bool isInherited,
        IReadOnlySet<string>? trustedInheritedWriterSids)
    {
        if (sid.Equals(currentUser) ||
            sid.Equals(owner) ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) ||
            string.Equals(sid.Value, "S-1-3-4", StringComparison.Ordinal))
        {
            return true;
        }

        if (isInherited && trustedInheritedWriterSids?.Contains(sid.Value) == true)
            return true;

        return enabledTokenSids.Contains(sid.Value) &&
            !IsBroadUntrustedIdentity(sid);
    }

    /// <summary>
    /// Allows the installed Codex workspace convention without making a broad
    /// writable principal globally trusted. Authenticated Users may be used as
    /// the caller's effective writer only when the selected project inherited
    /// that exact right from its ordinary direct parent and that parent is
    /// owned by the current user. Explicit or unmatched broad writer rules are
    /// still rejected.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlySet<string> TrustedInheritedWorkspaceWriters(
        string projectPath,
        FileSystemSecurity projectSecurity,
        SecurityIdentifier currentUser)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        var parent = Directory.GetParent(projectPath);
        if (parent is null || !parent.Exists || parent.LinkTarget is not null ||
            (parent.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return trusted;
        }

        var parentSecurity = parent.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (parentSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier parentOwner ||
            !parentOwner.Equals(currentUser))
        {
            return trusted;
        }

        var authenticatedUsers = new SecurityIdentifier(
            WellKnownSidType.AuthenticatedUserSid,
            domainSid: null);
        var parentRules = parentSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid &&
                sid.Equals(authenticatedUsers) &&
                (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0)
            .ToArray();
        if (parentRules.Length == 0)
            return trusted;

        var childHasMatchingInheritedRule = projectSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(childRule =>
            {
                if (!childRule.IsInherited ||
                    childRule.AccessControlType != AccessControlType.Allow ||
                    childRule.IdentityReference is not SecurityIdentifier childSid ||
                    !childSid.Equals(authenticatedUsers))
                {
                    return false;
                }

                var childRights = ExpandGenericRights(childRule.FileSystemRights);
                return HasWriteAuthority(childRights) && parentRules.Any(parentRule =>
                {
                    var parentRights = ExpandGenericRights(parentRule.FileSystemRights);
                    return (parentRights & childRights) == childRights;
                });
            });
        if (childHasMatchingInheritedRule)
            trusted.Add(authenticatedUsers.Value);
        return trusted;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsBroadUntrustedIdentity(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.NullSid) ||
        sid.IsWellKnown(WellKnownSidType.WorldSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalSid) ||
        sid.IsWellKnown(WellKnownSidType.CreatorGroupSid) ||
        sid.IsWellKnown(WellKnownSidType.DialupSid) ||
        sid.IsWellKnown(WellKnownSidType.NetworkSid) ||
        sid.IsWellKnown(WellKnownSidType.BatchSid) ||
        sid.IsWellKnown(WellKnownSidType.InteractiveSid) ||
        sid.IsWellKnown(WellKnownSidType.ServiceSid) ||
        sid.IsWellKnown(WellKnownSidType.AnonymousSid) ||
        sid.IsWellKnown(WellKnownSidType.ProxySid) ||
        sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
        sid.IsWellKnown(WellKnownSidType.RestrictedCodeSid) ||
        sid.IsWellKnown(WellKnownSidType.TerminalServerSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinPowerUsersSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
        sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);

    [SupportedOSPlatform("windows")]
    private static bool HasWriteAuthority(uint rights)
    {
        const uint genericAll = 0x10000000;
        const uint genericWrite = 0x40000000;
        var writeRights = unchecked((uint)(int)(
            FileSystemRights.Write |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership));
        return (rights & (writeRights | genericAll | genericWrite)) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static uint ExpandGenericRights(FileSystemRights rights)
    {
        const uint genericAll = 0x10000000;
        const uint genericExecute = 0x20000000;
        const uint genericWrite = 0x40000000;
        const uint genericRead = 0x80000000;
        var expanded = unchecked((uint)(int)rights);
        if ((expanded & genericAll) != 0)
            expanded |= unchecked((uint)(int)FileSystemRights.FullControl);
        if ((expanded & genericWrite) != 0)
            expanded |= unchecked((uint)(int)FileSystemRights.Write);
        if ((expanded & genericRead) != 0)
            expanded |= unchecked((uint)(int)FileSystemRights.Read);
        if ((expanded & genericExecute) != 0)
            expanded |= unchecked((uint)(int)FileSystemRights.ReadAndExecute);
        return expanded;
    }

    [UnsupportedOSPlatform("windows")]
    private static void RequireUnixMode(string path, UnixFileMode expected)
    {
        if (File.GetUnixFileMode(path) != expected)
        {
            throw new UnauthorizedAccessException(
                "The local project registry is accessible outside the current OS user.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsSid() =>
        WindowsIdentity.GetCurrent().User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
}

internal static class CortexLocalOsIdentity
{
    public static CortexLocalOsIdentityValue Capture()
    {
        if (OperatingSystem.IsWindows())
        {
            return new CortexLocalOsIdentityValue(
                "windows-sid-sha256",
                CortexCanonicalJson.Sha256($"windows-sid:{CaptureWindowsSid()}"));
        }
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsFreeBSD())
        {
            return new CortexLocalOsIdentityValue(
                "unix-euid-sha256",
                CortexCanonicalJson.Sha256($"unix-euid:{GetEffectiveUserId()}"));
        }
        throw new PlatformNotSupportedException(
            "Standalone Cortex local identity requires a supported OS user boundary.");
    }

    [SupportedOSPlatform("windows")]
    private static string CaptureWindowsSid() =>
        WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint GetEffectiveUserId();
}

internal sealed record CortexLocalOsIdentityValue(string Kind, string Hash);
