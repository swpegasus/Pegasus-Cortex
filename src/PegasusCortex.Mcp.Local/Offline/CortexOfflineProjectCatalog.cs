using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.File.V2;

namespace Pegasus.Cortex.Mcp.Local.Offline;

public sealed record CortexOfflineProjectRegistration(
    string TaskId,
    string ProjectId,
    string RootPath,
    string RootPathHash,
    string WriteNamespace,
    IReadOnlyList<string> ReadNamespaces);

public sealed record CortexOfflineProjectCatalogDocument(
    string Schema,
    string Canonicalization,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CortexOfflineProjectRegistration> Projects,
    string CatalogHash);

public sealed record CortexOfflineLegacyMigrationResult(
    CortexOfflineProjectRegistration Project,
    string Disposition,
    bool LegacyRegistrationPreserved);

/// <summary>
/// Current-user catalog of explicitly registered offline project roots. The
/// catalog is configuration only: project frames remain owned by each root.
/// </summary>
public sealed class CortexOfflineProjectCatalog
{
    public const string LegacySchema = "pegasus.cortex.offline-project-catalog/1.0";
    public const string CurrentSchema = "pegasus.cortex.offline-project-catalog/2.0";
    public const string LegacyUnassignedTaskId = "legacy-unassigned";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const int MaximumProjects = 1024;
    public const int MaximumCatalogUtf8Bytes = 2 * 1024 * 1024;
    private const string EmptyHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    public CortexOfflineProjectCatalog(string path, TimeProvider? timeProvider = null)
    {
        _path = ValidateCatalogPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Path => _path;

    public async ValueTask<CortexOfflineProjectCatalogDocument> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
            return EmptyDocument();
        RequireSafeFile(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumCatalogUtf8Bytes)
            throw new InvalidDataException("The offline project catalog exceeds its byte bound.");
        byte[] bytes;
        await using (var stream = new FileStream(
                         _path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            var schema = parsed.RootElement.GetProperty("schema").GetString();
            if (string.Equals(schema, LegacySchema, StringComparison.Ordinal))
            {
                return UpgradeLegacyInMemory(JsonSerializer.Deserialize<LegacyCatalogDocument>(
                        bytes,
                        JsonOptions) ??
                    throw new InvalidDataException("The legacy offline project catalog is empty."));
            }
            return Validate(JsonSerializer.Deserialize<CortexOfflineProjectCatalogDocument>(bytes, JsonOptions) ??
                throw new InvalidDataException("The offline project catalog is empty."));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The offline project catalog is malformed or uses an unsupported shape.",
                exception);
        }
    }

    public async ValueTask<CortexOfflineProjectRegistration> RegisterAsync(
        string projectRoot,
        string taskId,
        CortexFileProjectManifestV2 manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireTaskId(taskId);
        if (string.Equals(taskId, LegacyUnassignedTaskId, StringComparison.Ordinal))
            throw new ArgumentException("The reserved legacy task ID cannot identify a task scope.", nameof(taskId));
        var validatedManifest = manifest.Validate();
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        await EnsureParentAsync(cancellationToken).ConfigureAwait(false);
        var lockPath = _path + ".lock";
        RequireSafeFile(lockPath);
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);

        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Projects.Any(item =>
                string.Equals(item.TaskId, LegacyUnassignedTaskId, StringComparison.Ordinal) &&
                string.Equals(item.RootPath, canonicalRoot, PathComparison)))
        {
            throw new InvalidOperationException(
                "The project root has an unassigned legacy project-level offline registration; explicit migration is required before task-scoped initialization.");
        }
        var registration = new CortexOfflineProjectRegistration(
            taskId,
            validatedManifest.ProjectId,
            canonicalRoot,
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(canonicalRoot)),
            validatedManifest.WriteNamespace,
            Array.AsReadOnly(validatedManifest.ReadNamespaces.ToArray()));
        var projects = current.Projects.ToList();
        var byId = projects.FindIndex(item =>
            string.Equals(item.TaskId, registration.TaskId, StringComparison.Ordinal) &&
            string.Equals(item.ProjectId, registration.ProjectId, StringComparison.Ordinal));
        var byRoot = projects.FindIndex(item =>
            string.Equals(item.TaskId, registration.TaskId, StringComparison.Ordinal) &&
            string.Equals(item.RootPath, registration.RootPath, PathComparison));
        if (byId >= 0 || byRoot >= 0)
        {
            var existing = projects[Math.Max(byId, byRoot)];
            if (byId != byRoot || !Equivalent(existing, registration))
            {
                throw new InvalidDataException(
                    "The offline task/project identifier or task/root binding conflicts with an existing registration.");
            }
            return registration;
        }
        if (projects.Count >= MaximumProjects)
            throw new InvalidOperationException("The offline project catalog is full.");
        projects.Add(registration);
        var updated = Seal(new CortexOfflineProjectCatalogDocument(
            CurrentSchema,
            Canonicalization,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            Array.AsReadOnly(projects.ToArray()),
            EmptyHash));
        await WriteAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
        return registration;
    }

    /// <summary>
    /// Verifies that one historical project-level registration can be paired
    /// with the requested task binding. This is deliberately read-only so the
    /// caller can initialize an isolated task scope before committing the
    /// catalog entry.
    /// </summary>
    public async ValueTask<CortexOfflineProjectRegistration> RequireLegacyMigrationAsync(
        string projectRoot,
        string taskId,
        CortexFileProjectManifestV2 manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireTaskId(taskId);
        if (string.Equals(taskId, LegacyUnassignedTaskId, StringComparison.Ordinal))
            throw new ArgumentException("The reserved legacy task ID cannot identify a task scope.", nameof(taskId));
        var validatedManifest = manifest.Validate();
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var migration = RequireLegacyMigration(current, canonicalRoot, taskId, validatedManifest);
        if (migration.Existing is null && current.Projects.Count >= MaximumProjects)
            throw new InvalidOperationException("The offline project catalog is full.");
        return migration.Requested;
    }

    /// <summary>
    /// Adds an exact task-scoped registration while preserving the matching
    /// legacy-unassigned row as historical evidence. No project HOT or frame
    /// content is read, assigned, or incorporated by this catalog operation.
    /// </summary>
    public async ValueTask<CortexOfflineLegacyMigrationResult> MigrateLegacyAsync(
        string projectRoot,
        string taskId,
        CortexFileProjectManifestV2 manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireTaskId(taskId);
        if (string.Equals(taskId, LegacyUnassignedTaskId, StringComparison.Ordinal))
            throw new ArgumentException("The reserved legacy task ID cannot identify a task scope.", nameof(taskId));
        var validatedManifest = manifest.Validate();
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        await EnsureParentAsync(cancellationToken).ConfigureAwait(false);
        var lockPath = _path + ".lock";
        RequireSafeFile(lockPath);
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);

        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var migration = RequireLegacyMigration(current, canonicalRoot, taskId, validatedManifest);
        if (migration.Existing is not null)
        {
            return new CortexOfflineLegacyMigrationResult(
                migration.Existing,
                "existing",
                LegacyRegistrationPreserved: true);
        }
        if (current.Projects.Count >= MaximumProjects)
            throw new InvalidOperationException("The offline project catalog is full.");

        var projects = current.Projects.Append(migration.Requested).ToArray();
        var updated = Seal(new CortexOfflineProjectCatalogDocument(
            CurrentSchema,
            Canonicalization,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            Array.AsReadOnly(projects),
            EmptyHash));
        await WriteAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
        return new CortexOfflineLegacyMigrationResult(
            migration.Requested,
            "migrated",
            LegacyRegistrationPreserved: true);
    }

    public async ValueTask<CortexOfflineProjectRegistration> RequireRegisteredRootAsync(
        string projectRoot,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        RequireTaskId(taskId);
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var registration = document.Projects.SingleOrDefault(item =>
                   string.Equals(item.TaskId, taskId, StringComparison.Ordinal) &&
                   string.Equals(item.RootPath, canonicalRoot, PathComparison));
        if (registration is not null)
            return registration;
        if (document.Projects.Any(item =>
                string.Equals(item.TaskId, LegacyUnassignedTaskId, StringComparison.Ordinal) &&
                string.Equals(item.RootPath, canonicalRoot, PathComparison)))
        {
            throw new InvalidOperationException(
                "The project root has only an unassigned legacy project-level offline registration; explicit migration is required.");
        }
        throw new KeyNotFoundException(
            "The project root is not registered for offline Cortex use by the requested task ID.");
    }

    public async ValueTask RequireNoLegacyRootAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var canonicalRoot = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (document.Projects.Any(item =>
                string.Equals(item.TaskId, LegacyUnassignedTaskId, StringComparison.Ordinal) &&
                string.Equals(item.RootPath, canonicalRoot, PathComparison)))
        {
            throw new InvalidOperationException(
                "The project root has an unassigned legacy project-level offline registration; explicit migration is required before task-scoped initialization.");
        }
    }

    private async ValueTask EnsureParentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = System.IO.Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The offline project catalog has no parent.");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        if (File.Exists(parent))
            throw new IOException("The offline catalog directory path identifies a file.");
        Pegasus.Cortex.Product.CortexUserStateDirectories.EnsureCurrentUserDirectory(parent);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(parent);
    }

    private async ValueTask WriteAtomicAsync(
        CortexOfflineProjectCatalogDocument document,
        CancellationToken cancellationToken)
    {
        var bytes = CortexCanonicalJson.Serialize(document);
        if (bytes.Length > MaximumCatalogUtf8Bytes)
            throw new InvalidOperationException("The offline project catalog exceeds its byte bound.");
        var parent = System.IO.Path.GetDirectoryName(_path)!;
        var temporary = System.IO.Path.Combine(
            parent,
            $".offline-projects.{Guid.NewGuid():N}.tmp");
        try
        {
            RequireSafeFile(_path);
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            CortexLocalRegistryAclPolicy.ProtectFile(temporary);
            RequireSafeFile(_path);
            File.Move(temporary, _path, overwrite: true);
            CortexLocalRegistryAclPolicy.ProtectFile(_path);
            CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private CortexOfflineProjectCatalogDocument EmptyDocument() => Seal(
        new CortexOfflineProjectCatalogDocument(
            CurrentSchema,
            Canonicalization,
            DateTimeOffset.UnixEpoch,
            Array.Empty<CortexOfflineProjectRegistration>(),
            EmptyHash));

    private static CortexOfflineProjectCatalogDocument Seal(
        CortexOfflineProjectCatalogDocument document)
    {
        var normalized = Normalize(document with { CatalogHash = EmptyHash });
        ValidateShape(normalized);
        return normalized with
        {
            CatalogHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(normalized))
        };
    }

    private static CortexOfflineProjectCatalogDocument Validate(
        CortexOfflineProjectCatalogDocument document)
    {
        var normalized = Normalize(document);
        ValidateShape(normalized);
        var expected = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(
            normalized with { CatalogHash = EmptyHash }));
        if (!string.Equals(expected, normalized.CatalogHash, StringComparison.Ordinal))
            throw new InvalidDataException("The offline project catalog hash is invalid.");
        return normalized;
    }

    private static CortexOfflineProjectCatalogDocument UpgradeLegacyInMemory(
        LegacyCatalogDocument document)
    {
        if (!string.Equals(document.Schema, LegacySchema, StringComparison.Ordinal) ||
            !string.Equals(document.Canonicalization, Canonicalization, StringComparison.Ordinal) ||
            document.Projects is null || document.Projects.Count > MaximumProjects ||
            !ValidSha256(document.CatalogHash))
        {
            throw new InvalidDataException("The legacy offline project catalog header or bound is invalid.");
        }
        var normalized = document with
        {
            UpdatedAtUtc = document.UpdatedAtUtc.ToUniversalTime(),
            Projects = Array.AsReadOnly(document.Projects
                .OrderBy(item => item.ProjectId, StringComparer.Ordinal)
                .ToArray())
        };
        var expected = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(
            normalized with { CatalogHash = EmptyHash }));
        if (!string.Equals(expected, normalized.CatalogHash, StringComparison.Ordinal))
            throw new InvalidDataException("The legacy offline project catalog hash is invalid.");
        var upgraded = new CortexOfflineProjectCatalogDocument(
            CurrentSchema,
            Canonicalization,
            normalized.UpdatedAtUtc,
            Array.AsReadOnly(normalized.Projects.Select(item => new CortexOfflineProjectRegistration(
                LegacyUnassignedTaskId,
                item.ProjectId,
                item.RootPath,
                item.RootPathHash,
                item.WriteNamespace,
                item.ReadNamespaces)).ToArray()),
            EmptyHash);
        return Seal(upgraded);
    }

    private static CortexOfflineProjectCatalogDocument Normalize(
        CortexOfflineProjectCatalogDocument document)
    {
        if (document.Projects is null)
            throw new InvalidDataException("The offline project catalog has no project collection.");
        return document with
        {
            UpdatedAtUtc = document.UpdatedAtUtc.ToUniversalTime(),
            Projects = Array.AsReadOnly(document.Projects
                .Select(item => item ?? throw new InvalidDataException(
                    "The offline project catalog contains a null registration."))
                .OrderBy(item => item.TaskId, StringComparer.Ordinal)
                .ThenBy(item => item.ProjectId, StringComparer.Ordinal)
                .ToArray())
        };
    }

    private static void ValidateShape(CortexOfflineProjectCatalogDocument document)
    {
        if (!string.Equals(document.Schema, CurrentSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The offline project catalog uses a legacy or unsupported project-level schema; explicit task-scoped re-registration is required.");
        }
        if (
            !string.Equals(document.Canonicalization, Canonicalization, StringComparison.Ordinal) ||
            document.Projects.Count > MaximumProjects ||
            !ValidSha256(document.CatalogHash))
        {
            throw new InvalidDataException("The offline project catalog header or bound is invalid.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Projects)
        {
            if (!string.Equals(item.TaskId, LegacyUnassignedTaskId, StringComparison.Ordinal))
                RequireTaskId(item.TaskId);
            var root = Directory.Exists(item.RootPath)
                ? CortexLocalRegistryPathPolicy.ValidateProjectRoot(item.RootPath)
                : CortexLocalRegistryPathPolicy.ValidateStateRootPath(
                    item.RootPath,
                    requireExisting: false,
                    allowAbsent: true);
            var pathKey = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
            if (!ids.Add(item.TaskId + "\n" + item.ProjectId) ||
                !roots.Add(item.TaskId + "\n" + pathKey) ||
                !string.Equals(root, item.RootPath, PathComparison) ||
                !string.Equals(
                    item.RootPathHash,
                    CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(root)),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("An offline project registration is invalid or duplicate.");
            }
            _ = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                item.ProjectId,
                item.WriteNamespace,
                item.ReadNamespaces).Validate();
        }
    }

    private static string ValidateCatalogPath(string value)
    {
        var canonical = System.IO.Path.GetFullPath(value);
        if (!System.IO.Path.IsPathFullyQualified(value) ||
            !string.Equals(canonical, value, PathComparison) ||
            !string.Equals(System.IO.Path.GetFileName(canonical), "offline-projects.json", StringComparison.Ordinal) ||
            canonical.Length > 240)
        {
            throw new ArgumentException("The offline project catalog path is not the canonical bounded catalog path.", nameof(value));
        }
        var parent = System.IO.Path.GetDirectoryName(canonical) ??
            throw new ArgumentException("The offline project catalog has no parent.", nameof(value));
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        RequireSafeFile(canonical);
        return canonical;
    }

    private static void RequireSafeFile(string path)
    {
        if (Directory.Exists(path))
            throw new IOException("An offline catalog file path identifies a directory.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Offline catalog files cannot be links or reparse points.");
        }
    }

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void RequireTaskId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !IsLowerAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !IsLowerAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "A canonical lowercase ASCII bounded portable task ID is required.",
                nameof(value));
        }
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool Equivalent(
        CortexOfflineProjectRegistration left,
        CortexOfflineProjectRegistration right) =>
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.RootPath, right.RootPath, PathComparison) &&
        string.Equals(left.RootPathHash, right.RootPathHash, StringComparison.Ordinal) &&
        string.Equals(left.WriteNamespace, right.WriteNamespace, StringComparison.Ordinal) &&
        left.ReadNamespaces.SequenceEqual(right.ReadNamespaces, StringComparer.Ordinal);

    private static LegacyMigrationState RequireLegacyMigration(
        CortexOfflineProjectCatalogDocument document,
        string canonicalRoot,
        string taskId,
        CortexFileProjectManifestV2 manifest)
    {
        var requested = new CortexOfflineProjectRegistration(
            taskId,
            manifest.ProjectId,
            canonicalRoot,
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(canonicalRoot)),
            manifest.WriteNamespace,
            Array.AsReadOnly(manifest.ReadNamespaces.ToArray()));
        var legacy = document.Projects.SingleOrDefault(item =>
            string.Equals(item.TaskId, LegacyUnassignedTaskId, StringComparison.Ordinal) &&
            string.Equals(item.RootPath, canonicalRoot, PathComparison));
        if (legacy is null)
        {
            throw new InvalidOperationException(
                "The project root has no preserved legacy project-level registration to migrate.");
        }
        if (!SameProjectBinding(legacy, requested))
        {
            throw new InvalidDataException(
                "The preserved legacy project registration conflicts with the requested task binding.");
        }

        var projects = document.Projects.ToList();
        var byId = projects.FindIndex(item =>
            string.Equals(item.TaskId, requested.TaskId, StringComparison.Ordinal) &&
            string.Equals(item.ProjectId, requested.ProjectId, StringComparison.Ordinal));
        var byRoot = projects.FindIndex(item =>
            string.Equals(item.TaskId, requested.TaskId, StringComparison.Ordinal) &&
            string.Equals(item.RootPath, requested.RootPath, PathComparison));
        CortexOfflineProjectRegistration? existing = null;
        if (byId >= 0 || byRoot >= 0)
        {
            existing = projects[Math.Max(byId, byRoot)];
            if (byId != byRoot || !Equivalent(existing, requested))
            {
                throw new InvalidDataException(
                    "The offline task/project identifier or task/root binding conflicts with an existing registration.");
            }
        }
        return new LegacyMigrationState(requested, existing);
    }

    private static bool SameProjectBinding(
        CortexOfflineProjectRegistration left,
        CortexOfflineProjectRegistration right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.RootPath, right.RootPath, PathComparison) &&
        string.Equals(left.RootPathHash, right.RootPathHash, StringComparison.Ordinal) &&
        string.Equals(left.WriteNamespace, right.WriteNamespace, StringComparison.Ordinal) &&
        left.ReadNamespaces.SequenceEqual(right.ReadNamespaces, StringComparer.Ordinal);

    private sealed record LegacyMigrationState(
        CortexOfflineProjectRegistration Requested,
        CortexOfflineProjectRegistration? Existing);

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record LegacyProjectRegistration(
        string ProjectId,
        string RootPath,
        string RootPathHash,
        string WriteNamespace,
        IReadOnlyList<string> ReadNamespaces);

    private sealed record LegacyCatalogDocument(
        string Schema,
        string Canonicalization,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<LegacyProjectRegistration> Projects,
        string CatalogHash);
}
