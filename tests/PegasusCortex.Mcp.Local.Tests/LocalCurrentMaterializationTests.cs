using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;

internal static class LocalCurrentMaterializationTests
{
    public static async Task VerifiedCurrentExtendsIndependentRetainedChainAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var capturedAt = DateTimeOffset.UtcNow;
        WriteProject(projectRoot, currentRevision: 12, capturedAt.AddMinutes(-1), "first");

        var options = new CortexLocalCurrentSnapshotArguments(
            context.ProfileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                context.ProfileId,
                Path.GetDirectoryName(context.DatabasePath)!),
            LocalTestContext.PrimaryProjectLinkId,
            capturedAt);
        var first = await CortexLocalCurrentSnapshotCommand.MaterializeAsync(
            options,
            CancellationToken.None);
        LocalAssert.True(first.Accepted, "The first CURRENT snapshot was rejected.");
        LocalAssert.Equal(12L, first.CurrentRevision, "CURRENT revision was relabeled.");
        LocalAssert.Equal(1L, first.RetainedRevision, "The retained chain did not start at one.");
        LocalAssert.Equal("appended", first.Disposition, "The first snapshot was not appended.");
        LocalAssert.True(first.ProjectBriefContextSetId is not null, "The derived brief was not materialized.");
        LocalAssert.True(first.ProjectionIssueCode is null, "The first projection reported an issue.");

        var replay = await CortexLocalCurrentSnapshotCommand.MaterializeAsync(
            options,
            CancellationToken.None);
        LocalAssert.True(replay.Accepted, "The exact CURRENT replay was rejected.");
        LocalAssert.Equal(1L, replay.RetainedRevision, "The replay extended the retained chain.");
        LocalAssert.Equal("idempotent", replay.Disposition, "The exact replay was not idempotent.");
        LocalAssert.Equal(first.SnapshotHash, replay.SnapshotHash, "The replay changed exact bytes.");

        var secondCapture = capturedAt.AddMinutes(1);
        WriteProject(projectRoot, currentRevision: 13, secondCapture, "second");
        var second = await CortexLocalCurrentSnapshotCommand.MaterializeAsync(
            options with { CapturedAt = secondCapture },
            CancellationToken.None);
        LocalAssert.True(second.Accepted, "The changed CURRENT snapshot was rejected.");
        LocalAssert.Equal(13L, second.CurrentRevision, "The updated CURRENT revision was lost.");
        LocalAssert.Equal(2L, second.RetainedRevision, "The retained chain did not advance once.");
        LocalAssert.Equal("appended", second.Disposition, "The changed snapshot was not appended.");
        LocalAssert.False(
            string.Equals(first.SnapshotHash, second.SnapshotHash),
            "The changed snapshot reused its predecessor hash.");
    }

    public static async Task ExpiredLegacyTaskSetContinuesWithoutRelabelingAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var now = DateTimeOffset.UtcNow;
        WriteProject(projectRoot, currentRevision: 7, now.AddMinutes(-1), "legacy");
        var current = await CortexCurrentProjectionReader.ReadAsync(
            projectRoot,
            now,
            maximumAge: null,
            CancellationToken.None);
        var expired = CortexCurrentTaskSnapshotFactory.Create(
            current,
            retainedRevision: 1,
            previousSnapshotHash: null,
            now.AddHours(-25),
            new AgentWorkOwnerV1(
                "local-os-policy",
                "legacy-test-agent",
                "legacy-current-producer"),
            taskSetId: "legacy-current-task-set");
        await using (var composition = await Pegasus.Cortex.Mcp.Local.Runtime
            .CortexMcpLocalComposition.CreateAsync(
                context.ProfileId,
                context.DatabasePath,
                context.RegistryPath,
                "operator",
                TimeProvider.System,
                CancellationToken.None))
        {
            var write = await composition.Runtime.SubmitTaskSnapshotAsync(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryNamespace,
                expired,
                CancellationToken.None);
            LocalAssert.True(write is not null, "The legacy head was not retained.");
        }

        var options = new CortexLocalCurrentSnapshotArguments(
            context.ProfileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                context.ProfileId,
                Path.GetDirectoryName(context.DatabasePath)!),
            LocalTestContext.PrimaryProjectLinkId,
            now);
        var advanced = await CortexLocalCurrentSnapshotCommand.MaterializeAsync(
            options,
            CancellationToken.None);
        LocalAssert.True(advanced.Accepted, "The expired legacy head did not advance.");
        LocalAssert.Equal(2L, advanced.RetainedRevision, "The legacy chain was relabeled or restarted.");

        var decoded = Convert.FromBase64String(advanced.SnapshotCanonicalBase64);
        var snapshot = Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson
            .Deserialize<AgentTaskSnapshotV1>(decoded);
        LocalAssert.Equal(
            "legacy-current-task-set",
            snapshot.TaskSetId,
            "The legacy task-set identity was not preserved.");
        LocalAssert.Equal(
            expired.SnapshotHash,
            snapshot.PreviousSnapshotHash!,
            "The legacy predecessor hash was not preserved.");
    }

    internal static void WriteProject(
        string root,
        long currentRevision,
        DateTimeOffset generatedAt,
        string suffix,
        string storeId = "sqlite-local-test",
        long storeEpoch = 1,
        string? checkpoint = null)
    {
        Directory.CreateDirectory(Path.Combine(root, ".cortex"));
        var sources = new[]
        {
            ("START_HERE.md", "start_here", $"# Start {suffix}\n"),
            ("AGENTS.md", "agents", "# Agents\n"),
            (".cortex/STATE.md", "cortex_state", $"# State {suffix}\n"),
            (".cortex/TASKS.md", "cortex_tasks", $"# Tasks {suffix}\n"),
            (".cortex/RECOVERY.md", "cortex_recovery", "# Recovery\n")
        };
        foreach (var (relative, _, content) in sources)
        {
            File.WriteAllText(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                content,
                new UTF8Encoding(false));
        }
        var revisions = sources.Select(item =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(
                root,
                item.Item1.Replace('/', Path.DirectorySeparatorChar)));
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            return new AgentWorkSourceRevisionV1(
                item.Item2,
                $"sha256-{hash}",
                hash);
        }).ToArray();
        var authorityHash = CortexProjectBriefService.ComputeAuthoritySetHash(revisions);
        File.WriteAllText(
            Path.Combine(root, ".cortex", "meta.json"),
            JsonSerializer.Serialize(new
            {
                project = new { name = "Primary Local Test" },
                cortexCorrelation = new
                {
                    projectId = LocalTestContext.PrimaryProjectId,
                    projectLinkId = LocalTestContext.PrimaryProjectLinkId,
                    storeId,
                    storeEpoch
                }
            }),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(root, ".cortex", "CURRENT.json"),
            JsonSerializer.Serialize(new
            {
                authority = new
                {
                    authoritySetHash = authorityHash,
                    revision = currentRevision,
                    sourceCount = 5
                },
                current = new
                {
                    checkpoint = checkpoint ?? $"Checkpoint {suffix} is verified.",
                    gate = "Live changes require explicit user authority.",
                    nextAction = "Continue the bounded synchronization test.",
                    task = "Preserve the current recovery projection."
                },
                fallback = new
                {
                    pointer = ".cortex/README.md#full-fallback-recovery-order",
                    when = "missing, stale, malformed, incomplete, or authority hash mismatch"
                },
                freshness = new
                {
                    generatedAtUtc = generatedAt.ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                        System.Globalization.CultureInfo.InvariantCulture)
                },
                project = new
                {
                    id = LocalTestContext.PrimaryProjectId,
                    name = "Primary Local Test",
                    projectLinkId = LocalTestContext.PrimaryProjectLinkId
                },
                schema = "pegasus.cortex.current/1.0"
            }),
            new UTF8Encoding(false));
    }
}
