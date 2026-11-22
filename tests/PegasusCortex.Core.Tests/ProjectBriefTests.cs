using Pegasus.Cortex.Core.Query;

internal static class ProjectBriefTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly string ProjectLinkId = $"plink_{new string('a', 64)}";
    private static readonly string ProjectNamespace = $"project-{new string('a', 48)}";

    public static async Task SnapshotDeltaAndUnchangedAsync()
    {
        var first = Snapshot(
            revision: 1,
            sourceHash: new string('1', 64),
            selectionVersion: "selection-v1",
            currentTask: TaskItem("task-current", 0, "Implement brief", "active", 'a'),
            hotTasks:
            [
                TaskItem("task-a", 1, "Task A", "planned", 'b'),
                TaskItem("task-b", 2, "Task B", "blocked", 'c'),
                TaskItem("task-c", 3, "Task C", "held", 'd'),
                TaskItem("task-d", 4, "Task D", "planned", 'e')
            ],
            hasMore: true);
        var middle = Snapshot(
            revision: 2,
            sourceHash: new string('2', 64),
            selectionVersion: "selection-v2",
            currentTask: first.CurrentTask,
            hotTasks: first.HotTasks,
            hasMore: true,
            previousContextSetId: first.ContextSetId);
        var current = Snapshot(
            revision: 3,
            sourceHash: new string('3', 64),
            selectionVersion: "selection-v3",
            currentTask: first.CurrentTask with
            {
                NextAction = "Review the exact project-brief response."
            },
            hotTasks:
            [
                first.HotTasks[0],
                first.HotTasks[1] with
                {
                    State = "active",
                    Blocker = null,
                    NextAction = "Continue the bounded read lane."
                },
                first.HotTasks[3] with { SelectionRank = 3 },
                TaskItem("task-e", 4, "Task E", "planned", 'f')
            ],
            hasMore: true,
            previousContextSetId: middle.ContextSetId);
        var service = new CortexProjectBriefService(
            new InMemoryCortexProjectBriefReadModel([first, middle, current]),
            new FixedTimeProvider(Now));

        var snapshot = await service.ReadAsync(
            new CortexProjectBriefRequest(ProjectLinkId, current.AuthoritySetHash),
            Scope(),
            CancellationToken.None);
        var delta = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                current.AuthoritySetHash,
                middle.ContextSetId),
            Scope(),
            CancellationToken.None);
        var olderNonPredecessor = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                current.AuthoritySetHash,
                first.ContextSetId),
            Scope(),
            CancellationToken.None);
        var unchanged = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                current.AuthoritySetHash,
                current.ContextSetId),
            Scope(),
            CancellationToken.None);
        var unknownBase = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                current.AuthoritySetHash,
                $"ctx_{new string('9', 64)}"),
            Scope(),
            CancellationToken.None);
        var missingAuthority = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                null!,
                current.ContextSetId),
            Scope(),
            CancellationToken.None);
        var missingInitialAuthority = await service.ReadAsync(
            new CortexProjectBriefRequest(ProjectLinkId, null!),
            Scope(),
            CancellationToken.None);
        var mismatchedAuthority = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                new string('f', 64),
                current.ContextSetId),
            Scope(),
            CancellationToken.None);
        var mismatchedInitialAuthority = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                new string('f', 64)),
            Scope(),
            CancellationToken.None);

        Assert.True(snapshot.Accepted);
        Assert.Equal(CortexProjectBriefDisposition.Snapshot, snapshot.Response.Disposition);
        Assert.Equal(current.ContextSetId, snapshot.Response.ContextSetId);
        Assert.Equal(new string('3', 64), snapshot.Response.SourceHash);
        Assert.Equal(current.AuthoritySetHash, snapshot.Response.AuthoritySetHash);
        Assert.Equal("selection-v3", snapshot.Response.SelectionVersion);
        Assert.Equal(4, snapshot.Response.HotTasks.Count);
        Assert.True(snapshot.Response.HasMore);
        Assert.True(snapshot.Response.CurrentTask is not null);

        Assert.True(delta.Accepted);
        Assert.Equal(CortexProjectBriefDisposition.Delta, delta.Response.Disposition);
        Assert.Equal(middle.ContextSetId, delta.Response.BaseContextSetId);
        Assert.Equal(current.ContextSetId, delta.Response.ContextSetId);
        Assert.Equal(3L, delta.Response.StateRevision);
        Assert.True(delta.Response.CurrentTask is not null);
        Assert.Equal(3, delta.Response.HotTasks.Count);
        Assert.Equal("task-b", delta.Response.HotTasks[0].TaskId);
        Assert.Equal("task-d", delta.Response.HotTasks[1].TaskId);
        Assert.Equal("task-e", delta.Response.HotTasks[2].TaskId);
        Assert.Equal(1, delta.Response.RemovedTaskIds.Count);
        Assert.Equal("task-c", delta.Response.RemovedTaskIds[0]);
        Assert.False(delta.Response.FallbackRequired);

        Assert.True(olderNonPredecessor.Accepted);
        Assert.Equal(
            CortexProjectBriefDisposition.Snapshot,
            olderNonPredecessor.Response.Disposition);
        Assert.Equal<string?>(null, olderNonPredecessor.Response.BaseContextSetId);
        Assert.True(olderNonPredecessor.Response.CurrentTask is not null);
        Assert.Equal(4, olderNonPredecessor.Response.HotTasks.Count);

        Assert.True(unchanged.Accepted);
        Assert.Equal(CortexProjectBriefDisposition.Unchanged, unchanged.Response.Disposition);
        Assert.Equal<CortexProjectBriefTask?>(null, unchanged.Response.CurrentTask);
        Assert.Equal(0, unchanged.Response.HotTasks.Count);
        Assert.Equal(0, unchanged.Response.RemovedTaskIds.Count);
        Assert.Equal(1, unchanged.Response.CheckpointReferences.Count);
        Assert.Equal(1, unchanged.Response.EvidenceReferences.Count);

        Assert.True(unknownBase.Accepted);
        Assert.Equal(CortexProjectBriefDisposition.Snapshot, unknownBase.Response.Disposition);
        Assert.Equal(4, unknownBase.Response.HotTasks.Count);

        AssertAuthorityMismatch(missingAuthority);
        AssertAuthorityMismatch(missingInitialAuthority);
        AssertAuthorityMismatch(mismatchedAuthority);
        AssertAuthorityMismatch(mismatchedInitialAuthority);
    }

    public static async Task MissingExpiredAndWrongScopeFailClosedAsync()
    {
        var empty = new CortexProjectBriefService(
            new InMemoryCortexProjectBriefReadModel(),
            new FixedTimeProvider(Now));
        var missing = await empty.ReadAsync(
            new CortexProjectBriefRequest(ProjectLinkId, new string('a', 64)),
            Scope(),
            CancellationToken.None);

        var expiredSnapshot = Snapshot(
            revision: 1,
            sourceHash: new string('3', 64),
            selectionVersion: "selection-expired",
            currentTask: TaskItem("task-expired", 0, "Expired task", "unknown", '3'),
            hotTasks: [],
            hasMore: false,
            reviewAfter: Now.AddMinutes(-1));
        var expired = await ReadSingleAsync(expiredSnapshot);
        var expiresNow = await ReadSingleAsync(Snapshot(
            revision: 1,
            sourceHash: new string('8', 64),
            selectionVersion: "selection-expiry-boundary",
            currentTask: TaskItem("task-expiry-boundary", 0, "Expiry boundary", "active", '8'),
            hotTasks: [],
            hasMore: false,
            reviewAfter: Now));

        var valid = Snapshot(
            revision: 1,
            sourceHash: new string('4', 64),
            selectionVersion: "selection-valid",
            currentTask: TaskItem("task-valid", 0, "Valid task", "active", '4'),
            hotTasks: [],
            hasMore: false);
        var corrupt = await ReadSingleAsync(valid with { SourceHash = new string('5', 64) });
        var retainedCheckpoint = CortexProjectBriefReferencePolicy.CreateRetained(
            valid.ProjectLinkId,
            valid.ProjectId,
            valid.ProjectNamespace,
            "task-snapshot",
            "task-set-valid",
            revision: 1,
            new string('9', 64),
            valid.SourceHash);
        var crossScopeTemplate = valid with
        {
            ContextSetId = $"ctx_{new string('0', 64)}",
            CheckpointReferences =
            [
                retainedCheckpoint with
                {
                    RetainedRecord = retainedCheckpoint.RetainedRecord! with
                    {
                        ProjectNamespace = "different.namespace"
                    }
                }
            ]
        };
        var crossScopeLocator = await ReadSingleAsync(
            crossScopeTemplate with
            {
                ContextSetId =
                    CortexProjectBriefService.ComputeContextSetId(
                        crossScopeTemplate)
            });
        var futureFreshness = new CortexProjectBriefFreshness(
            "current",
            Now.AddMinutes(2),
            Now.AddHours(1),
            "future-dated-source");
        var futureTemplate = valid with
        {
            ContextSetId = $"ctx_{new string('0', 64)}",
            CurrentTask = valid.CurrentTask with { Freshness = futureFreshness },
            Freshness = futureFreshness
        };
        var futureDated = await ReadSingleAsync(futureTemplate with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(futureTemplate)
        });

        var overflow = Snapshot(
            revision: 1,
            sourceHash: new string('6', 64),
            selectionVersion: "selection-overflow",
            currentTask: TaskItem("task-overflow", 0, "Overflow task", "active", '6'),
            hotTasks:
            [
                TaskItem("task-1", 1, "One", "planned", '1'),
                TaskItem("task-2", 2, "Two", "planned", '2'),
                TaskItem("task-3", 3, "Three", "planned", '3'),
                TaskItem("task-4", 4, "Four", "planned", '4'),
                TaskItem("task-5", 5, "Five", "planned", '5')
            ],
            hasMore: false);
        var tooManyHotTasks = await ReadSingleAsync(overflow);

        var incompleteSnapshot = Snapshot(
            revision: 1,
            sourceHash: new string('7', 64),
            selectionVersion: "selection-incomplete",
            currentTask: TaskItem("task-incomplete", 0, "Incomplete task", "blocked", '7'),
            hotTasks: [],
            hasMore: true,
            complete: false);
        var incomplete = await ReadSingleAsync(incompleteSnapshot);

        var wrongScope = await empty.ReadAsync(
            new CortexProjectBriefRequest(ProjectLinkId, new string('a', 64)),
            Scope() with { ProjectLinkId = $"plink_{new string('b', 64)}" },
            CancellationToken.None);
        var excessiveBudget = await empty.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                new string('a', 64),
                MaximumUtf8Bytes: CortexProjectBriefContract.MaximumUtf8Bytes + 1),
            Scope(),
            CancellationToken.None);

        AssertUnavailable(missing, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(expired, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(expiresNow, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(corrupt, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(
            crossScopeLocator,
            "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(futureDated, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(tooManyHotTasks, "cortex_project_brief_snapshot_not_available");
        AssertUnavailable(wrongScope, "cortex_project_brief_authority_invalid");
        AssertUnavailable(excessiveBudget, "cortex_project_brief_authority_invalid");

        Assert.True(incomplete.Accepted);
        Assert.False(incomplete.Response.Complete);
        Assert.True(incomplete.Response.FallbackRequired);
        Assert.Equal("source_incomplete", incomplete.Response.FallbackReason);
    }

    public static async Task CanonicalResponseBudgetAsync()
    {
        var longState = new string('s', 160);
        var current = TaskItem(
            "task-budget-current",
            0,
            "Budget current",
            "active",
            '1') with
        {
            CurrentState = longState,
            NextAction = new string('n', 160)
        };
        var hot = Enumerable.Range(1, 4)
            .Select(index => TaskItem(
                $"task-budget-{index}",
                index,
                $"Budget task {index}",
                "planned",
                (char)('1' + index)) with
            {
                CurrentState = longState,
                NextAction = new string((char)('n' + index), 160)
            })
            .ToArray();
        var snapshot = Snapshot(
            revision: 1,
            sourceHash: new string('9', 64),
            selectionVersion: "selection-budget-v1",
            current,
            hot,
            hasMore: false);
        var service = new CortexProjectBriefService(
            new InMemoryCortexProjectBriefReadModel([snapshot]),
            new FixedTimeProvider(Now));

        var full = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                snapshot.AuthoritySetHash,
                MaximumUtf8Bytes: CortexProjectBriefContract.MaximumUtf8Bytes),
            Scope(),
            CancellationToken.None);
        var bounded = await service.ReadAsync(
            new CortexProjectBriefRequest(
                ProjectLinkId,
                snapshot.AuthoritySetHash,
                MaximumUtf8Bytes: CortexProjectBriefContract.MinimumMaximumUtf8Bytes),
            Scope(),
            CancellationToken.None);

        if (!full.Accepted)
        {
            throw new InvalidOperationException(
                "Expected the maximum-budget project brief to be accepted; " +
                $"diagnostics: {string.Join(", ", full.Diagnostics.Select(item => item.Code))}.");
        }
        var canonicalBytes =
            Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson.Serialize(
                full.Response).Length;
        Assert.True(
            canonicalBytes > CortexProjectBriefContract.MinimumMaximumUtf8Bytes);
        Assert.True(canonicalBytes <= CortexProjectBriefContract.MaximumUtf8Bytes);
        AssertUnavailable(bounded, "cortex_project_brief_budget_exceeded");
    }

    private static async Task<CortexProjectBriefResult> ReadSingleAsync(
        CortexProjectBriefSnapshot snapshot) =>
        await new CortexProjectBriefService(
                new InMemoryCortexProjectBriefReadModel([snapshot]),
                new FixedTimeProvider(Now))
            .ReadAsync(
                new CortexProjectBriefRequest(
                    ProjectLinkId,
                    snapshot.AuthoritySetHash),
                Scope(),
                CancellationToken.None);

    private static void AssertUnavailable(CortexProjectBriefResult result, string code)
    {
        Assert.False(result.Accepted);
        Assert.True(result.Response.FallbackRequired);
        Assert.False(result.Response.Complete);
        Assert.Equal<string?>(null, result.Response.ContextSetId);
        Assert.ContainsCode(code, result.Diagnostics);
    }

    private static void AssertAuthorityMismatch(CortexProjectBriefResult result)
    {
        AssertUnavailable(result, "cortex_project_brief_authority_mismatch");
        Assert.Equal("authority_mismatch", result.Response.FallbackReason);
        Assert.Equal<string?>(null, result.Response.ProjectLinkId);
        Assert.Equal<string?>(null, result.Response.ProjectId);
        Assert.Equal<string?>(null, result.Response.ProjectNamespace);
        Assert.Equal<string?>(null, result.Response.ContextSetId);
        Assert.Equal<string?>(null, result.Response.BaseContextSetId);
        Assert.Equal<long?>(null, result.Response.StateRevision);
        Assert.Equal<string?>(null, result.Response.SourceHash);
        Assert.Equal<string?>(null, result.Response.AuthoritySetHash);
        Assert.Equal<string?>(null, result.Response.SelectionVersion);
        Assert.Equal<CortexProjectBriefTask?>(null, result.Response.CurrentTask);
        Assert.Equal(0, result.Response.HotTasks.Count);
        Assert.Equal(0, result.Response.RemovedTaskIds.Count);
        Assert.False(result.Response.HasMore);
        Assert.Equal(0, result.Response.CheckpointReferences.Count);
        Assert.Equal(0, result.Response.EvidenceReferences.Count);
        Assert.Equal<CortexProjectBriefFreshness?>(null, result.Response.Freshness);
    }

    private static CortexProjectBriefReadScope Scope() =>
        new(
            "tenant-1",
            ProjectLinkId,
            "project-1",
            ProjectNamespace,
            "local-cortex-policy",
            "subject-1",
            "pegasus-cortex-mcp",
            7);

    private static CortexProjectBriefTask TaskItem(
        string taskId,
        int rank,
        string title,
        string state,
        char hashCharacter)
    {
        var sourceHash = new string(hashCharacter, 64);
        return new CortexProjectBriefTask(
            taskId,
            rank,
            title,
            state,
            $"Reported state for {title}.",
            "Continue with exact source-bound state.",
            state is "blocked" or "held"
                ? new CortexProjectBriefGate(
                    "pending",
                    "external-approval-authority",
                    ApprovalReferenceId: null,
                    "An external authority must revalidate this reported gate.",
                    GrantsAuthority: false)
                : new CortexProjectBriefGate(
                    "not_required",
                    AuthorityId: null,
                    ApprovalReferenceId: null,
                    "No external approval is required for this reported task.",
                    GrantsAuthority: false),
            state is "blocked" or "held" ? "An explicit dependency is pending." : null,
            new CortexProjectBriefTaskSource(
                "pegasus.agent-task-snapshot/1.0",
                $"source-{taskId}",
                $"revision-{taskId}-1",
                sourceHash,
                $"artifact://tasks/{taskId}"),
            Freshness());
    }

    private static CortexProjectBriefSnapshot Snapshot(
        long revision,
        string sourceHash,
        string selectionVersion,
        CortexProjectBriefTask currentTask,
        IReadOnlyList<CortexProjectBriefTask> hotTasks,
        bool hasMore,
        string? previousContextSetId = null,
        bool complete = true,
        DateTimeOffset? reviewAfter = null)
    {
        var recordId = $"krec_{new string('c', 64)}";
        var revisionId = $"krev_{new string('d', 64)}";
        var snapshot = new CortexProjectBriefSnapshot(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            "tenant-1",
            ProjectLinkId,
            "project-1",
            ProjectNamespace,
            $"ctx_{new string('0', 64)}",
            previousContextSetId,
            revision,
            revision,
            sourceHash,
            selectionVersion,
            currentTask,
            hotTasks,
            hasMore,
            [
                new CortexProjectBriefReference(
                    "checkpoint://project/checkpoint-1",
                    new string('e', 64),
                    recordId,
                    revisionId)
            ],
            [
                new CortexProjectBriefReference(
                    $"record://{recordId}/{revisionId}",
                    new string('f', 64),
                    recordId,
                    revisionId)
            ],
            complete,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            Freshness(reviewAfter),
            AuthoritySetHash: sourceHash);
        return snapshot with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(snapshot)
        };
    }

    private static CortexProjectBriefFreshness Freshness(
        DateTimeOffset? reviewAfter = null) =>
        new(
            "current",
            Now.AddMinutes(-5),
            reviewAfter ?? Now.AddHours(1),
            "exact-source-revision-and-hash");
}
