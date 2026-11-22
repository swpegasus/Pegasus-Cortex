using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

internal static class SqliteProjectBriefTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly string ProjectLinkId = $"plink_{new string('a', 64)}";
    private static readonly string ProjectNamespace = $"project-{new string('b', 48)}";

    public static async Task CanonicalSnapshotDeltaAndRestartAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-sqlite-brief-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profile", "cortex.db");
            var first = Snapshot(1, previous: null, "First current state.", '1');
            CortexProjectBriefSnapshot second;
            await using (var retention = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path)))
            {
                var briefs = new SqliteCortexProjectBriefStore(
                    retention,
                    new FixedTimeProvider(Now));
                var firstResult = await briefs.SubmitSnapshotAsync(
                    first,
                    "agent-local",
                    "brief-idem-1",
                    CancellationToken.None).ConfigureAwait(false);
                Require(firstResult.Disposition == CortexRetentionWriteDispositions.Appended,
                    "The first project brief did not append.");

                second = Snapshot(2, first.ContextSetId, "Second current state.", '2');
                var secondResult = await briefs.SubmitSnapshotAsync(
                    second,
                    "agent-local",
                    "brief-idem-2",
                    CancellationToken.None).ConfigureAwait(false);
                Require(secondResult.Disposition == CortexRetentionWriteDispositions.Appended,
                    "The successor project brief did not append.");
                Require(
                    secondResult.Document!.CanonicalUtf8.AsSpan().SequenceEqual(
                        CortexCanonicalJson.Serialize(second)),
                    "SQLite changed the provider-independent canonical project-brief bytes.");
            }

            await using var restarted = new SqliteCortexRetentionStore(new SqliteCortexStoreOptions(path));
            var restartedBriefs = new SqliteCortexProjectBriefStore(
                restarted,
                new FixedTimeProvider(Now));
            var service = new CortexProjectBriefService(
                restartedBriefs,
                new FixedTimeProvider(Now));
            var delta = await service.ReadAsync(
                new CortexProjectBriefRequest(
                    ProjectLinkId,
                    second.AuthoritySetHash,
                    first.ContextSetId),
                Scope(),
                CancellationToken.None).ConfigureAwait(false);
            var unchanged = await service.ReadAsync(
                new CortexProjectBriefRequest(
                    ProjectLinkId,
                    second.AuthoritySetHash,
                    second.ContextSetId),
                Scope(),
                CancellationToken.None).ConfigureAwait(false);
            var wrongProjectRejected = false;
            try
            {
                _ = await restartedBriefs.FindCurrentAsync(
                    Scope() with { ProjectId = "different-project" },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                wrongProjectRejected = true;
            }

            Require(delta.Accepted &&
                    delta.Response.Disposition == CortexProjectBriefDisposition.Delta &&
                    delta.Response.BaseContextSetId == first.ContextSetId,
                "The exact retained predecessor did not produce a delta.");
            Require(unchanged.Accepted &&
                    unchanged.Response.Disposition == CortexProjectBriefDisposition.Unchanged,
                "The exact current context did not produce unchanged.");
            Require(
                wrongProjectRejected,
                "The SQLite provider returned a brief across project identity.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task StaleBriefWriteFailsVisiblyAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-sqlite-brief-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var retention = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(Path.Combine(root, "profile", "cortex.db")));
            var briefs = new SqliteCortexProjectBriefStore(
                retention,
                new FixedTimeProvider(Now));
            var first = Snapshot(1, null, "Current state.", '3');
            await briefs.SubmitSnapshotAsync(
                first,
                "agent-local",
                "brief-idem-1",
                CancellationToken.None).ConfigureAwait(false);
            var stale = Snapshot(
                2,
                $"ctx_{new string('f', 64)}",
                "Stale state.",
                '4');
            var result = await briefs.SubmitSnapshotAsync(
                stale,
                "agent-local",
                "brief-idem-stale",
                CancellationToken.None).ConfigureAwait(false);
            Require(result.Disposition == CortexRetentionWriteDispositions.StaleBase &&
                    result.CurrentHeadHash == CortexCanonicalJson.Sha256(
                        CortexCanonicalJson.Serialize(first)),
                "A stale project-brief base was not reported with the exact current head.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task ReplayAfterHeadAdvanceAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-sqlite-brief-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var retention = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(Path.Combine(root, "profile", "cortex.db")));
            var briefs = new SqliteCortexProjectBriefStore(
                retention,
                new FixedTimeProvider(Now));
            var first = Snapshot(1, null, "Replay base.", '5');
            await briefs.SubmitSnapshotAsync(
                first,
                "agent-local",
                "brief-idem-replay",
                CancellationToken.None).ConfigureAwait(false);
            var second = Snapshot(2, first.ContextSetId, "Advanced head.", '6');
            await briefs.SubmitSnapshotAsync(
                second,
                "agent-local",
                "brief-idem-advanced",
                CancellationToken.None).ConfigureAwait(false);

            var replay = await briefs.SubmitSnapshotAsync(
                first,
                "agent-local",
                "brief-idem-replay",
                CancellationToken.None).ConfigureAwait(false);
            var secondHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(second));
            Require(
                replay.Disposition == CortexRetentionWriteDispositions.Idempotent &&
                replay.Document is { Revision: 1 } &&
                replay.CurrentHeadHash == secondHash,
                "An exact project-brief retry did not remain idempotent after head advancement.");

            var divergent = Snapshot(1, null, "Divergent replay.", '7');
            var conflict = await briefs.SubmitSnapshotAsync(
                divergent,
                "agent-local",
                "brief-idem-replay",
                CancellationToken.None).ConfigureAwait(false);
            Require(
                conflict.Disposition == CortexRetentionWriteDispositions.Conflict &&
                conflict.CurrentHeadHash == secondHash,
                "A divergent replay did not report the actual advanced head.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static CortexProjectBriefSnapshot Snapshot(
        long revision,
        string? previous,
        string currentState,
        char hash)
    {
        var freshness = new CortexProjectBriefFreshness(
            "current",
            Now.AddMinutes(-5),
            Now.AddHours(1),
            "exact-source-revision-and-hash");
        var task = new CortexProjectBriefTask(
            "task-current",
            0,
            "Retain current task",
            "active",
            currentState,
            "Continue from the bounded brief.",
            new CortexProjectBriefGate(
                "not_required",
                AuthorityId: null,
                ApprovalReferenceId: null,
                "No external approval is required for this reported task.",
                GrantsAuthority: false),
            null,
            new CortexProjectBriefTaskSource(
                "pegasus.agent-task-snapshot/1.0",
                "source-task-current",
                $"revision-{revision}",
                new string(hash, 64),
                "artifact://tasks/current"),
            freshness);
        var template = new CortexProjectBriefSnapshot(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            "tenant-local",
            ProjectLinkId,
            "project-local",
            ProjectNamespace,
            $"ctx_{new string('0', 64)}",
            previous,
            revision,
            revision,
            new string(hash, 64),
            "selection-v1",
            task,
            [],
            HasMore: false,
            CheckpointReferences: [],
            EvidenceReferences: [],
            Complete: true,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            freshness,
            AuthoritySetHash: new string(hash, 64));
        return template with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(template)
        };
    }

    private static CortexProjectBriefReadScope Scope() => new(
        "tenant-local",
        ProjectLinkId,
        "project-local",
        ProjectNamespace,
        "local-sqlite-policy",
        "local-os-user",
        "pegasus-cortex-local",
        PolicyRevision: 1);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
