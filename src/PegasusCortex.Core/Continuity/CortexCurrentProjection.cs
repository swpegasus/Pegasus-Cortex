using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Core.Continuity;

/// <summary>
/// One verified, bounded view of project-local CURRENT.json and the authority
/// files from which it was generated. Markdown remains authoritative; this
/// value is only a disposable projection suitable for passive retention.
/// </summary>
public sealed record CortexCurrentProjection(
    string ProjectId,
    string ProjectName,
    string ProjectLinkId,
    string Task,
    string Gate,
    string Checkpoint,
    string NextAction,
    long AuthorityRevision,
    string AuthoritySetHash,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AgentWorkSourceRevisionV1> SourceRevisions);

/// <summary>
/// Fail-closed verifier for the 2 KiB hot projection. It recomputes every
/// authority-file hash and the provider-neutral authority-set hash, rejects
/// linked paths, and never mutates the project.
/// </summary>
public static class CortexCurrentProjectionReader
{
    public const string Schema = "pegasus.cortex.current/1.0";
    public const int MaximumProjectionBytes = 2 * 1024;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);

    private static readonly string[] AuthorityPaths =
    [
        "START_HERE.md",
        "AGENTS.md",
        ".cortex/STATE.md",
        ".cortex/TASKS.md",
        ".cortex/RECOVERY.md"
    ];

    private static readonly string[] AuthoritySourceIds =
    [
        "start_here",
        "agents",
        "cortex_state",
        "cortex_tasks",
        "cortex_recovery"
    ];

    public static async ValueTask<CortexCurrentProjection> ReadAsync(
        string projectRoot,
        DateTimeOffset observedAt,
        TimeSpan? maximumAge,
        CancellationToken cancellationToken)
    {
        RequireUtc(observedAt, nameof(observedAt));
        var ageLimit = maximumAge ?? DefaultMaximumAge;
        if (ageLimit <= TimeSpan.Zero || ageLimit > DefaultMaximumAge)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        var root = RequireProjectRoot(projectRoot);
        var currentBytes = await ReadStableFileAsync(
            root,
            ".cortex/CURRENT.json",
            MaximumProjectionBytes,
            cancellationToken).ConfigureAwait(false);
        using var current = ParseExactObject(currentBytes, "CURRENT.json");
        var rootElement = current.RootElement;
        RequireProperties(
            rootElement,
            ["authority", "current", "fallback", "freshness", "project", "schema"],
            "CURRENT.json");
        if (!string.Equals(
                RequiredString(rootElement, "schema"),
                Schema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("CURRENT.json uses an unsupported schema.");
        }

        var project = RequiredObject(rootElement, "project");
        RequireProperties(project, ["id", "name", "projectLinkId"], "CURRENT.project");
        var projectId = RequiredBoundedText(project, "id", 128);
        var projectName = RequiredBoundedText(project, "name", 320);
        var projectLinkId = RequiredString(project, "projectLinkId");
        RequireProjectLink(projectLinkId);

        var hot = RequiredObject(rootElement, "current");
        RequireProperties(
            hot,
            ["checkpoint", "gate", "nextAction", "task"],
            "CURRENT.current");
        var task = RequiredBoundedText(hot, "task", 320);
        var gate = RequiredBoundedText(hot, "gate", 320);
        var checkpoint = RequiredBoundedText(hot, "checkpoint", 320);
        var nextAction = RequiredBoundedText(hot, "nextAction", 320);

        var authority = RequiredObject(rootElement, "authority");
        RequireProperties(
            authority,
            ["authoritySetHash", "revision", "sourceCount"],
            "CURRENT.authority");
        if (!authority.TryGetProperty("revision", out var revisionValue) ||
            !revisionValue.TryGetInt64(out var revision) || revision < 1 ||
            !authority.TryGetProperty("sourceCount", out var sourceCountValue) ||
            !sourceCountValue.TryGetInt32(out var sourceCount) ||
            sourceCount != AuthorityPaths.Length)
        {
            throw new InvalidDataException("CURRENT.json authority bounds are invalid.");
        }
        var recordedAuthorityHash = RequiredHash(authority, "authoritySetHash");

        var freshness = RequiredObject(rootElement, "freshness");
        RequireProperties(freshness, ["generatedAtUtc"], "CURRENT.freshness");
        var generatedAt = ParseUtc(RequiredString(freshness, "generatedAtUtc"));
        var age = observedAt - generatedAt;
        if (age < TimeSpan.Zero || age > ageLimit)
            throw new InvalidDataException("CURRENT.json is future-dated or stale.");

        var fallback = RequiredObject(rootElement, "fallback");
        RequireProperties(fallback, ["pointer", "when"], "CURRENT.fallback");
        if (!string.Equals(
                RequiredString(fallback, "pointer"),
                ".cortex/README.md#full-fallback-recovery-order",
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(fallback, "when"),
                "missing, stale, malformed, incomplete, or authority hash mismatch",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("CURRENT.json fallback is not the required fail-safe path.");
        }

        var metaBytes = await ReadStableFileAsync(
            root,
            ".cortex/meta.json",
            64 * 1024,
            cancellationToken).ConfigureAwait(false);
        VerifyProjectIdentity(metaBytes, projectId, projectName, projectLinkId);

        var sources = new AgentWorkSourceRevisionV1[AuthorityPaths.Length];
        for (var index = 0; index < AuthorityPaths.Length; index++)
        {
            var bytes = await ReadStableFileAsync(
                root,
                AuthorityPaths[index],
                16 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            var hash = Sha256(bytes);
            sources[index] = new AgentWorkSourceRevisionV1(
                AuthoritySourceIds[index],
                $"sha256-{hash}",
                hash);
        }
        var computedAuthorityHash =
            CortexProjectBriefService.ComputeAuthoritySetHash(sources);
        if (!string.Equals(
                computedAuthorityHash,
                recordedAuthorityHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "CURRENT.json authority hash does not match the project files.");
        }

        return new CortexCurrentProjection(
            projectId,
            projectName,
            projectLinkId,
            task,
            gate,
            checkpoint,
            nextAction,
            revision,
            computedAuthorityHash,
            generatedAt,
            Array.AsReadOnly(sources));
    }

    private static void VerifyProjectIdentity(
        byte[] metaBytes,
        string projectId,
        string projectName,
        string projectLinkId)
    {
        using var meta = ParseExactObject(metaBytes, "meta.json");
        var root = meta.RootElement;
        var project = RequiredObject(root, "project");
        var correlation = RequiredObject(root, "cortexCorrelation");
        if (!string.Equals(RequiredString(project, "name"), projectName, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(correlation, "projectId"), projectId, StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(correlation, "projectLinkId"),
                projectLinkId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "CURRENT.json project identity does not match .cortex/meta.json.");
        }
    }

    private static string RequireProjectRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The project root does not exist.");
        RequireBoundedPath(root);
        RequireNoReparse(root);
        return root;
    }

    private static async ValueTask<byte[]> ReadStableFileAsync(
        string root,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            throw new InvalidDataException("A continuity path escaped the project root.");
        RequireBoundedPath(candidate);
        RequirePathChain(root, candidate);
        if (!File.Exists(candidate))
            throw new FileNotFoundException("A required continuity file is unavailable.");
        RequireNoReparse(candidate);

        var before = new FileInfo(candidate);
        if (before.Length is < 2 || before.Length > maximumBytes)
            throw new InvalidDataException("A continuity file exceeds its byte boundary.");
        await using var stream = new FileStream(
            candidate,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != before.Length)
            throw new IOException("A continuity file changed before it could be read.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(candidate);
        if (after.Length != before.Length ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new IOException("A continuity file changed while it was being read.");
        }
        _ = new UTF8Encoding(false, true).GetString(bytes);
        return bytes;
    }

    private static JsonDocument ParseExactObject(byte[] bytes, string name)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException($"{name} must be one JSON object.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{name} is not strict JSON.", exception);
        }
    }

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The required {name} object is absent.");
        }
        return result;
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var result) ||
            result.ValueKind != JsonValueKind.String ||
            result.GetString() is not { } text)
        {
            throw new InvalidDataException($"The required {name} string is absent.");
        }
        return text;
    }

    private static string RequiredBoundedText(
        JsonElement value,
        string name,
        int maximumCharacters)
    {
        var text = RequiredString(value, name);
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumCharacters ||
            text.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException($"The {name} text is outside its public bound.");
        }
        return text;
    }

    private static string RequiredHash(JsonElement value, string name)
    {
        var hash = RequiredString(value, name);
        if (hash.Length != 64 || hash.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"The {name} value is not canonical SHA-256.");
        }
        return hash;
    }

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyCollection<string> expected,
        string name)
    {
        var actual = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != expected.Count ||
            actual.Except(expected, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException($"{name} fields do not match the contract.");
        }
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidDataException("A continuity timestamp is not canonical UTC.");
        }
        return parsed;
    }

    private static void RequireProjectLink(string value)
    {
        if (value.Length != 70 || !value.StartsWith("plink_", StringComparison.Ordinal) ||
            value.AsSpan(6).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new InvalidDataException("The project link identifier is invalid.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", name);
    }

    private static void RequirePathChain(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                RequireNoReparse(current);
        }
    }

    private static void RequireNoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked continuity paths are not allowed.");
    }

    private static void RequireBoundedPath(string path)
    {
        if (path.Length > 240 || path.Split(Path.DirectorySeparatorChar)
                .Any(segment => Encoding.UTF8.GetByteCount(segment) > 240))
        {
            throw new PathTooLongException("A continuity path exceeds the safe boundary.");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

/// <summary>
/// Deterministically maps a verified hot projection to the narrow
/// current-recovery task set. CURRENT revision and retained revision remain
/// deliberately independent counters.
/// </summary>
public static class CortexCurrentTaskSnapshotFactory
{
    public const string TaskSetId = "current-recovery";

    public static AgentTaskSnapshotV1 Create(
        CortexCurrentProjection current,
        long retainedRevision,
        string? previousSnapshotHash,
        DateTimeOffset capturedAt,
        AgentWorkOwnerV1 producer,
        string? taskSetId = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(producer);
        if (capturedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC capture time is required.", nameof(capturedAt));
        var retainedTaskSetId = taskSetId ?? TaskSetId;
        if (string.IsNullOrWhiteSpace(retainedTaskSetId) ||
            retainedTaskSetId.Length > AgentWorkPacketContractLimits.MaximumIdentifierCharacters)
        {
            throw new ArgumentException("A bounded retained task-set identifier is required.", nameof(taskSetId));
        }
        if (retainedRevision < 1 ||
            retainedRevision == 1 && previousSnapshotHash is not null ||
            retainedRevision > 1 && previousSnapshotHash is null)
        {
            throw new ArgumentException("The retained revision predecessor is invalid.");
        }

        var expiresAt = capturedAt.AddHours(24);
        var identityHash = Sha256(
            Encoding.UTF8.GetBytes(
                $"{current.ProjectLinkId}\n{current.AuthoritySetHash}"));
        var sourceIds = current.SourceRevisions
            .Select(item => item.SourceId)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var objective = PortableProjectionText(
            current.Task,
            "task objective",
            current.AuthoritySetHash);
        var checkpoint = PortableProjectionText(
            current.Checkpoint,
            "checkpoint",
            current.AuthoritySetHash);
        var nextAction = PortableProjectionText(
            current.NextAction,
            "next action",
            current.AuthoritySetHash);
        var gate = PortableProjectionText(
            current.Gate,
            "approval gate",
            current.AuthoritySetHash);
        var task = new AgentTaskV1(
            $"current-{identityHash[..32]}",
            parentTaskId: null,
            ordinal: 0,
            AgentTaskStateIds.Unknown,
            objective,
            checkpoint,
            nextAction,
            constraints: [gate],
            dependencyTaskIds: [],
            blockers: [],
            knownFailures: [],
            reportedOwner: null,
            new AgentWorkApprovalGateV1(
                AgentWorkApprovalGateStateIds.Pending,
                authorityId: "project-local-authority",
                approvalReferenceId: null,
                gate,
                grantsAuthority: false),
            evidenceIds: [],
            sourceRevisionIds: sourceIds);
        var template = new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            retainedTaskSetId,
            $"snapshot-current-{retainedRevision}-{current.AuthoritySetHash[..16]}",
            retainedRevision,
            previousSnapshotHash,
            $"current-{retainedRevision}-{current.AuthoritySetHash[..32]}",
            current.ProjectId,
            current.ProjectLinkId,
            $"ctx_{current.AuthoritySetHash}",
            capturedAt,
            expiresAt,
            producer,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                capturedAt,
                expiresAt,
                "Verified CURRENT.json and five project-authority hashes."),
            current.SourceRevisions,
            evidenceReferences: [],
            AgentTaskSnapshotContractIds.Coverage,
            tasks: [task],
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentTaskSnapshotPolicy.Seal(template);
    }

    /// <summary>
    /// CURRENT remains useful locally and may therefore contain machine-only
    /// recovery paths. Retained snapshots cross provider and host boundaries,
    /// so a path-bearing field is replaced as a whole with a deterministic
    /// authority pointer rather than attempting a lossy path-token rewrite.
    /// </summary>
    private static string PortableProjectionText(
        string value,
        string field,
        string authoritySetHash) =>
        CortexRetentionTransferBundlePolicy.RedactAbsoluteMachinePaths(
            value,
            $"[machine-local {field} omitted; authority-set SHA-256 {authoritySetHash}]");

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
