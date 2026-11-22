using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Regenerates only the disposable project-local CURRENT.json projection. All
/// inputs come from the registered root and the established five-file authority
/// set; the existing Core reader performs the final independent verification.
/// This type has no timer, watcher, network, service, or environment-selected
/// provider behavior.
/// </summary>
internal static class CortexLocalCurrentProjectionRefresher
{
    private const int MaximumAuthorityBytes = 16 * 1024 * 1024;
    private const int MaximumMetaBytes = 64 * 1024;
    private const int MaximumHotTextCharacters = 320;

    private static readonly (string Path, string SourceId)[] AuthoritySources =
    [
        ("START_HERE.md", "start_here"),
        ("AGENTS.md", "agents"),
        (".cortex/STATE.md", "cortex_state"),
        (".cortex/TASKS.md", "cortex_tasks"),
        (".cortex/RECOVERY.md", "cortex_recovery")
    ];

    private static readonly IReadOnlyDictionary<string, string> HotFieldLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Current task"] = "Current task",
            ["Gate"] = "Gate",
            ["Checkpoint"] = "Checkpoint",
            ["Next action"] = "Next action"
        };

    public static async ValueTask<CortexLocalCurrentProjectionCandidate> StageAsync(
        string projectRoot,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        if (generatedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC refresh timestamp is required.", nameof(generatedAt));

        var root = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var meta = await ReadStableFileAsync(
            root,
            ".cortex/meta.json",
            MaximumMetaBytes,
            cancellationToken).ConfigureAwait(false);
        var project = ReadProjectIdentity(meta);

        var sources = new AgentWorkSourceRevisionV1[AuthoritySources.Length];
        byte[]? startHere = null;
        for (var index = 0; index < AuthoritySources.Length; index++)
        {
            var spec = AuthoritySources[index];
            var bytes = await ReadStableFileAsync(
                root,
                spec.Path,
                MaximumAuthorityBytes,
                cancellationToken).ConfigureAwait(false);
            var hash = Sha256(bytes);
            sources[index] = new AgentWorkSourceRevisionV1(
                spec.SourceId,
                $"sha256-{hash}",
                hash);
            if (string.Equals(spec.Path, "START_HERE.md", StringComparison.Ordinal))
                startHere = bytes;
        }

        var authoritySetHash = CortexProjectBriefService.ComputeAuthoritySetHash(sources);
        var hot = ReadHotFields(startHere ??
            throw new InvalidDataException("START_HERE.md was not read from the authority set."));
        var outputPath = ResolveProjectPath(root, ".cortex/CURRENT.json");
        var previous = await ReadPreviousAsync(
            root,
            outputPath,
            cancellationToken).ConfigureAwait(false);
        var previousRevision = previous?.Document.Authority?.Revision is >= 1
            ? previous.Document.Authority.Revision
            : 0;
        var unchangedAuthority = previous is not null &&
            previous.Document.Authority is not null &&
            string.Equals(
                previous.Document.Authority.AuthoritySetHash,
                authoritySetHash,
                StringComparison.Ordinal) &&
            Equals(previous.Document.Project, project);
        var revision = unchangedAuthority
            ? previousRevision
            : checked(previousRevision + 1);
        if (revision < 1)
            revision = 1;

        var projection = new CurrentDocument(
            new CurrentAuthority(authoritySetHash, revision, sources.Length),
            hot,
            new CurrentFallback(
                ".cortex/README.md#full-fallback-recovery-order",
                "missing, stale, malformed, incomplete, or authority hash mismatch"),
            new CurrentFreshness(FormatUtc(generatedAt)),
            project,
            CortexCurrentProjectionReader.Schema);
        var canonical = CortexCanonicalJson.Serialize(projection);
        if (canonical.Length + 1 > CortexCurrentProjectionReader.MaximumProjectionBytes)
        {
            throw new InvalidDataException(
                "The regenerated CURRENT.json exceeds its 2 KiB boundary.");
        }
        var output = new byte[canonical.Length + 1];
        canonical.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        var staged = new CortexCurrentProjection(
            project.Id,
            project.Name,
            project.ProjectLinkId,
            hot.Task,
            hot.Gate,
            hot.Checkpoint,
            hot.NextAction,
            revision,
            authoritySetHash,
            generatedAt,
            Array.AsReadOnly(sources));
        return new CortexLocalCurrentProjectionCandidate(
            root,
            outputPath,
            staged,
            output,
            previous?.CanonicalUtf8,
            ComputeInputFingerprint(authoritySetHash, project));
    }

    /// <summary>
    /// Publishes a staged projection only after the retained snapshot and
    /// project brief have been verified. A failed file commit restores the
    /// exact prior projection when this process replaced it.
    /// </summary>
    public static async ValueTask<CortexCurrentProjection> CommitAndVerifyAsync(
        CortexLocalCurrentProjectionCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var input = await CaptureInputStateAsync(
            candidate.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                input.Fingerprint,
                candidate.InputFingerprint,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Continuity authority changed before CURRENT.json could be committed.");
        }

        try
        {
            await WriteAtomicallyAsync(
                candidate.ProjectRoot,
                candidate.OutputPath,
                candidate.CanonicalUtf8,
                cancellationToken).ConfigureAwait(false);
            var verified = await CortexCurrentProjectionReader.ReadAsync(
                candidate.ProjectRoot,
                candidate.Projection.GeneratedAt,
                CortexCurrentProjectionReader.DefaultMaximumAge,
                cancellationToken).ConfigureAwait(false);
            if (verified.AuthorityRevision != candidate.Projection.AuthorityRevision ||
                !string.Equals(
                    verified.AuthoritySetHash,
                    candidate.Projection.AuthoritySetHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    verified.ProjectId,
                    candidate.Projection.ProjectId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    verified.ProjectLinkId,
                    candidate.Projection.ProjectLinkId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The regenerated CURRENT.json did not verify against its exact authority inputs.");
            }
            return verified;
        }
        catch (Exception failure)
        {
            try
            {
                await RestorePreviousAsync(candidate).ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    "CURRENT.json commit failed and its prior projection could not be restored.",
                    failure,
                    restoreFailure);
            }
            throw;
        }
    }

    /// <summary>
    /// Captures only the inputs that can change CURRENT.json. Watch hints are
    /// intentionally advisory; this stable fingerprint makes the refresh
    /// decision deterministic and lets the safety poll recover missed events.
    /// </summary>
    internal static async ValueTask<CortexLocalContinuityInputState>
        CaptureInputStateAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var root = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var meta = await ReadStableFileAsync(
            root,
            ".cortex/meta.json",
            MaximumMetaBytes,
            cancellationToken).ConfigureAwait(false);
        var project = ReadProjectIdentity(meta);
        var sources = new AgentWorkSourceRevisionV1[AuthoritySources.Length];
        for (var index = 0; index < AuthoritySources.Length; index++)
        {
            var spec = AuthoritySources[index];
            var bytes = await ReadStableFileAsync(
                root,
                spec.Path,
                MaximumAuthorityBytes,
                cancellationToken).ConfigureAwait(false);
            var hash = Sha256(bytes);
            sources[index] = new AgentWorkSourceRevisionV1(
                spec.SourceId,
                $"sha256-{hash}",
                hash);
        }
        var authoritySetHash = CortexProjectBriefService.ComputeAuthoritySetHash(sources);
        var fingerprint = ComputeInputFingerprint(authoritySetHash, project);
        return new CortexLocalContinuityInputState(fingerprint, authoritySetHash);
    }

    private static CurrentProject ReadProjectIdentity(byte[] metaBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(metaBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("meta.json must be one JSON object.");
            var project = RequiredObject(document.RootElement, "project", "meta.json");
            var correlation = RequiredObject(
                document.RootElement,
                "cortexCorrelation",
                "meta.json");
            var name = RequiredBoundedText(project, "name", 320, "meta.project");
            var id = RequiredBoundedText(
                correlation,
                "projectId",
                128,
                "meta.cortexCorrelation");
            var projectLinkId = RequiredBoundedText(
                correlation,
                "projectLinkId",
                70,
                "meta.cortexCorrelation");
            if (projectLinkId.Length != 70 ||
                !projectLinkId.StartsWith("plink_", StringComparison.Ordinal) ||
                projectLinkId.AsSpan(6).ContainsAnyExcept("0123456789abcdef"))
            {
                throw new InvalidDataException(
                    "meta.json has an invalid correlated project link.");
            }
            return new CurrentProject(id, name, projectLinkId);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("meta.json is not strict JSON.", exception);
        }
    }

    private static CurrentHot ReadHotFields(byte[] startHereBytes)
    {
        var text = new UTF8Encoding(false, true).GetString(startHereBytes);
        var lines = text.Split('\n');
        var section = Array.FindIndex(
            lines,
            line => string.Equals(
                line.TrimEnd('\r'),
                "## Current Recovery",
                StringComparison.Ordinal));
        if (section < 0)
            throw new InvalidDataException("START_HERE.md is missing Current Recovery.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? activeLabel = null;
        StringBuilder? activeValue = null;

        void StoreActive()
        {
            if (activeLabel is null || activeValue is null)
                return;
            if (!values.TryAdd(activeLabel, CleanHotValue(activeValue.ToString(), activeLabel)))
            {
                throw new InvalidDataException(
                    $"START_HERE.md contains duplicate {activeLabel} fields.");
            }
            activeLabel = null;
            activeValue = null;
        }

        for (var index = section + 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                StoreActive();
                break;
            }
            if (line.StartsWith('#'))
            {
                StoreActive();
                continue;
            }

            // Only a column-zero bullet starts the next authority field.
            // Indented Markdown lines remain part of the active bullet.
            if (!line.StartsWith("-", StringComparison.Ordinal))
            {
                if (activeValue is not null &&
                    line.Length > 0 &&
                    char.IsWhiteSpace(line[0]) &&
                    !string.IsNullOrWhiteSpace(line))
                {
                    activeValue.Append(' ');
                    activeValue.Append(line.Trim());
                    if (activeValue.Length > MaximumHotTextCharacters + 2)
                    {
                        throw new InvalidDataException(
                            $"START_HERE.md {activeLabel} is outside its public text bound.");
                    }
                }
                continue;
            }

            StoreActive();
            var candidate = line.Trim();
            var separator = candidate.IndexOf(':');
            if (separator < 2)
                continue;
            var label = candidate[1..separator].Trim();
            if (!HotFieldLabels.TryGetValue(label, out var canonicalLabel))
                continue;
            activeLabel = canonicalLabel;
            activeValue = new StringBuilder(candidate[(separator + 1)..].Trim());
        }
        StoreActive();

        foreach (var required in new[]
                 {
                     "Current task", "Gate", "Checkpoint", "Next action"
                 })
        {
            if (!values.ContainsKey(required))
            {
                throw new InvalidDataException(
                    $"START_HERE.md Current Recovery is missing {required}.");
            }
        }
        return new CurrentHot(
            values["Checkpoint"],
            values["Gate"],
            values["Next action"],
            values["Current task"]);
    }

    private static string CleanHotValue(string value, string label)
    {
        var result = value.Trim();
        if (result.Length >= 2 && result[0] == '`' && result[^1] == '`')
            result = result[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(result) ||
            result.Length > MaximumHotTextCharacters ||
            result.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException(
                $"START_HERE.md {label} is outside its public text bound.");
        }
        return result;
    }

    private static async ValueTask<PreviousCurrent?> ReadPreviousAsync(
        string root,
        string outputPath,
        CancellationToken cancellationToken)
    {
        RequireSafeEntry(root, outputPath, allowAbsent: true);
        if (!File.Exists(outputPath))
            return null;
        var bytes = await ReadStableFileAsync(
            root,
            ".cortex/CURRENT.json",
            CortexCurrentProjectionReader.MaximumProjectionBytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = CortexCanonicalJson.Deserialize<CurrentDocument>(bytes);
            if (!string.Equals(
                    previous.Schema,
                    CortexCurrentProjectionReader.Schema,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The existing CURRENT.json uses an unsupported schema.");
            }
            return new PreviousCurrent(previous, bytes);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The existing CURRENT.json is not a supported projection.",
                exception);
        }
    }

    private static string ComputeInputFingerprint(
        string authoritySetHash,
        CurrentProject project) =>
        CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(
                new CurrentInputFingerprint(authoritySetHash, project)));

    private static async ValueTask RestorePreviousAsync(
        CortexLocalCurrentProjectionCandidate candidate)
    {
        RequireSafeEntry(
            candidate.ProjectRoot,
            candidate.OutputPath,
            allowAbsent: true);
        byte[]? currentBytes = null;
        if (File.Exists(candidate.OutputPath))
        {
            currentBytes = await File.ReadAllBytesAsync(
                candidate.OutputPath,
                CancellationToken.None).ConfigureAwait(false);
        }
        if (currentBytes is not null &&
            !currentBytes.AsSpan().SequenceEqual(candidate.CanonicalUtf8) &&
            (candidate.PreviousCanonicalUtf8 is null ||
             !currentBytes.AsSpan().SequenceEqual(candidate.PreviousCanonicalUtf8)))
        {
            throw new IOException(
                "CURRENT.json changed concurrently while a failed commit was being restored.");
        }

        if (candidate.PreviousCanonicalUtf8 is not null)
        {
            if (currentBytes is null ||
                !currentBytes.AsSpan().SequenceEqual(candidate.PreviousCanonicalUtf8))
            {
                await WriteAtomicallyAsync(
                    candidate.ProjectRoot,
                    candidate.OutputPath,
                    candidate.PreviousCanonicalUtf8,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        else if (currentBytes is not null)
        {
            File.Delete(candidate.OutputPath);
        }
    }

    private static async ValueTask<byte[]> ReadStableFileAsync(
        string root,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var path = ResolveProjectPath(root, relativePath);
        RequireSafeEntry(root, path, allowAbsent: false);
        var before = new FileInfo(path);
        before.Refresh();
        if (before.Length is < 2 || before.Length > maximumBytes)
            throw new InvalidDataException("A continuity file exceeds its byte boundary.");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != before.Length)
            throw new IOException("A continuity file changed before it could be read.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(path);
        after.Refresh();
        if (after.Length != before.Length ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new IOException("A continuity file changed while it was being read.");
        }
        _ = new UTF8Encoding(false, true).GetString(bytes);
        return bytes;
    }

    private static async ValueTask WriteAtomicallyAsync(
        string root,
        string outputPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        RequireSafeEntry(root, outputPath, allowAbsent: true);
        var directory = Path.GetDirectoryName(outputPath) ??
            throw new InvalidDataException("CURRENT.json has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".CURRENT.{Guid.NewGuid():N}.tmp");
        RequireBoundedPath(temporaryPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            RequireSafeEntry(root, outputPath, allowAbsent: true);
            File.Move(temporaryPath, outputPath, overwrite: true);
            RequireSafeEntry(root, outputPath, allowAbsent: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ResolveProjectPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
            throw new InvalidDataException("A continuity path escaped the project root.");
        RequireBoundedPath(path);
        return path;
    }

    private static void RequireSafeEntry(
        string root,
        string path,
        bool allowAbsent)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (!allowAbsent || !string.Equals(current, path, PathComparison))
                    throw new FileNotFoundException("A required continuity path is unavailable.");
                continue;
            }
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked continuity paths are not allowed.");
        }
        if (!allowAbsent && !File.Exists(path))
            throw new FileNotFoundException("A required continuity file is unavailable.");
        if (Directory.Exists(path))
            throw new InvalidDataException("A continuity file path identifies a directory.");
    }

    private static void RequireBoundedPath(string path)
    {
        if (path.Length > 240 || path.Split(Path.DirectorySeparatorChar)
                .Any(segment => Encoding.UTF8.GetByteCount(segment) > 240))
        {
            throw new PathTooLongException("A continuity path exceeds the safe boundary.");
        }
    }

    private static JsonElement RequiredObject(
        JsonElement value,
        string propertyName,
        string owner)
    {
        if (!value.TryGetProperty(propertyName, out var result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The required {owner}.{propertyName} object is absent.");
        }
        return result;
    }

    private static string RequiredBoundedText(
        JsonElement value,
        string propertyName,
        int maximumCharacters,
        string owner)
    {
        if (!value.TryGetProperty(propertyName, out var result) ||
            result.ValueKind != JsonValueKind.String ||
            result.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) ||
            text.Length > maximumCharacters ||
            text.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException(
                $"The required {owner}.{propertyName} text is outside its bound.");
        }
        return text;
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record CurrentDocument(
        CurrentAuthority Authority,
        CurrentHot Current,
        CurrentFallback Fallback,
        CurrentFreshness Freshness,
        CurrentProject Project,
        string Schema);

    private sealed record CurrentAuthority(
        string AuthoritySetHash,
        long Revision,
        int SourceCount);

    private sealed record CurrentHot(
        string Checkpoint,
        string Gate,
        string NextAction,
        string Task);

    private sealed record CurrentFallback(string Pointer, string When);

    private sealed record CurrentFreshness(string GeneratedAtUtc);

    private sealed record CurrentProject(string Id, string Name, string ProjectLinkId);

    private sealed record CurrentInputFingerprint(
        string AuthoritySetHash,
        CurrentProject Project);

    private sealed record PreviousCurrent(
        CurrentDocument Document,
        byte[] CanonicalUtf8);
}

internal sealed record CortexLocalContinuityInputState(
    string Fingerprint,
    string AuthoritySetHash);

internal sealed record CortexLocalCurrentProjectionCandidate(
    string ProjectRoot,
    string OutputPath,
    CortexCurrentProjection Projection,
    byte[] CanonicalUtf8,
    byte[]? PreviousCanonicalUtf8,
    string InputFingerprint);
