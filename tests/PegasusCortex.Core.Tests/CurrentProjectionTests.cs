using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;

internal static class CurrentProjectionTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-03T05:30:00+00:00");

    public static async Task VerifiedProjectionCreatesIndependentRetainedRevisionAsync()
    {
        var root = CreateProject(Now.AddMinutes(-2));
        try
        {
            var current = await CortexCurrentProjectionReader.ReadAsync(
                root,
                Now,
                maximumAge: null,
                CancellationToken.None);
            var previousHash = new string('a', 64);
            var snapshot = CortexCurrentTaskSnapshotFactory.Create(
                current,
                retainedRevision: 8,
                previousHash,
                Now,
                new AgentWorkOwnerV1(
                    "local-os-policy",
                    "os-test-subject",
                    "pegasus-cortex-sync-client"));

            Assert.Equal(12L, current.AuthorityRevision);
            Assert.Equal(8L, snapshot.SnapshotRevision);
            Assert.Equal(previousHash, snapshot.PreviousSnapshotHash);
            Assert.Equal(CortexCurrentTaskSnapshotFactory.TaskSetId, snapshot.TaskSetId);
            Assert.Equal(AgentCoordinationModeIds.Standalone, snapshot.Mode);
            Assert.Equal(AgentTaskStateIds.Unknown, snapshot.Tasks.Single().ReportedState);
            Assert.Equal("Continue bounded recovery work.", snapshot.Tasks.Single().NextAction);
            Assert.True(snapshot.Tasks.Single().Constraints.Contains(
                "User approval remains required for live mutation."));
            Assert.Equal(
                AgentWorkApprovalGateStateIds.Pending,
                snapshot.Tasks.Single().ApprovalGate.State);
            Assert.Equal(
                "project-local-authority",
                snapshot.Tasks.Single().ApprovalGate.AuthorityId);
            Assert.Equal(
                "User approval remains required for live mutation.",
                snapshot.Tasks.Single().ApprovalGate.Summary);
            Assert.False(snapshot.Tasks.Single().ApprovalGate.GrantsAuthority);
            Assert.True(AgentTaskSnapshotPolicy.HasValidHash(snapshot));
            Assert.Equal($"ctx_{current.AuthoritySetHash}", snapshot.ContextSetId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task AuthorityMismatchAndStalenessFailClosedAsync()
    {
        var mismatch = CreateProject(Now.AddMinutes(-2));
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(mismatch, ".cortex", "TASKS.md"),
                "\nchanged",
                new UTF8Encoding(false));
            await AssertFailsAsync<InvalidDataException>(() =>
                CortexCurrentProjectionReader.ReadAsync(
                    mismatch,
                    Now,
                    maximumAge: null,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(mismatch, recursive: true);
        }

        var stale = CreateProject(Now.AddHours(-25));
        try
        {
            await AssertFailsAsync<InvalidDataException>(() =>
                CortexCurrentProjectionReader.ReadAsync(
                    stale,
                    Now,
                    maximumAge: null,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(stale, recursive: true);
        }
    }

    private static string CreateProject(DateTimeOffset generatedAt)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cortex-current-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".cortex"));
        var sourceSpecs = new[]
        {
            ("START_HERE.md", "start_here", "# Start\n"),
            ("AGENTS.md", "agents", "# Agents\n"),
            (".cortex/STATE.md", "cortex_state", "# State\n"),
            (".cortex/TASKS.md", "cortex_tasks", "# Tasks\n"),
            (".cortex/RECOVERY.md", "cortex_recovery", "# Recovery\n")
        };
        foreach (var (relative, _, content) in sourceSpecs)
        {
            File.WriteAllText(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                content,
                new UTF8Encoding(false));
        }
        var sources = sourceSpecs.Select(item =>
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
        var authorityHash = CortexProjectBriefService.ComputeAuthoritySetHash(sources);
        var projectLink = $"plink_{new string('1', 64)}";
        File.WriteAllText(
            Path.Combine(root, ".cortex", "meta.json"),
            JsonSerializer.Serialize(new
            {
                project = new { name = "Continuity Test" },
                cortexCorrelation = new
                {
                    projectId = "continuity-test",
                    projectLinkId = projectLink
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
                    revision = 12,
                    sourceCount = 5
                },
                current = new
                {
                    checkpoint = "Project files remain authoritative.",
                    gate = "User approval remains required for live mutation.",
                    nextAction = "Continue bounded recovery work.",
                    task = "Preserve exact recoverable project state."
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
                    id = "continuity-test",
                    name = "Continuity Test",
                    projectLinkId = projectLink
                },
                schema = CortexCurrentProjectionReader.Schema
            }),
            new UTF8Encoding(false));
        return root;
    }

    private static async Task AssertFailsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }
}
