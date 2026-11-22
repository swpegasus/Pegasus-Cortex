using System.Text;
using System.Threading.Channels;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class LocalContinuityRefreshTests
{
    private const string MultilineTask =
        "Refresh the bounded direct continuity fixture across the complete retained path.";
    private const string MultilineGate =
        "Keep the fixture local, passive, and independent of every network route.";
    private const string MultilineCheckpoint =
        "The authority files are ready for one exact snapshot and matching project brief.";
    private const string MultilineNextAction =
        "Verify the complete retained snapshot, project brief, and published projection.";

    public static async Task ExplicitOneShotRefreshAndHorizonAsync()
    {
        await RefreshCycleRegeneratesAndPreservesHorizonAsync().ConfigureAwait(false);
        await FailedMaterializationDoesNotPublishCandidateAsync().ConfigureAwait(false);
        await LiteralCliRequiresOnceAsync().ConfigureAwait(false);
    }

    public static async Task WatchHintsPollCancellationAndInvalidAuthorityAsync()
    {
        await FileHintWakesBeforeSafetyPollAsync().ConfigureAwait(false);
        await WatchCoordinatorRecoversMissedEventsAsync().ConfigureAwait(false);
    }

    private static async Task RefreshCycleRegeneratesAndPreservesHorizonAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var horizon = DateTimeOffset.UtcNow;
        var firstCapture = horizon.AddHours(-23);
        LocalCurrentMaterializationTests.WriteProject(
            projectRoot,
            currentRevision: 12,
            firstCapture,
            "before-refresh");
        WriteMultilineRefreshStartHere(projectRoot);
        await File.AppendAllTextAsync(
            Path.Combine(projectRoot, ".cortex", "STATE.md"),
            "\n# Authority changed before refresh.\n",
            new UTF8Encoding(false)).ConfigureAwait(false);

        var currentOptions = new CortexLocalCurrentSnapshotArguments(
            context.ProfileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                context.ProfileId,
                Path.GetDirectoryName(context.DatabasePath)!),
            LocalTestContext.PrimaryProjectLinkId,
            firstCapture);
        var first = await CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
            new CortexLocalContinuityRefreshArguments(currentOptions),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(first.Accepted, "The first continuity refresh was rejected.");
        LocalAssert.Equal(13L, first.CurrentRevision, "Changed authority did not advance CURRENT once.");
        LocalAssert.Equal(1L, first.RetainedRevision, "The retained refresh chain did not start at one.");
        LocalAssert.Equal("appended", first.Disposition, "The first refresh was not appended.");
        LocalAssert.True(
            first.ProjectBriefContextSetId is not null &&
            first.ProjectionIssueCode is null,
            "The first refresh did not materialize its exact project brief.");
        var verifiedFirst = await CortexCurrentProjectionReader.ReadAsync(
            projectRoot,
            firstCapture,
            maximumAge: null,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.Equal(MultilineTask, verifiedFirst.Task, "CURRENT truncated the current task.");
        LocalAssert.Equal(MultilineGate, verifiedFirst.Gate, "CURRENT truncated the gate.");
        LocalAssert.Equal(
            MultilineCheckpoint,
            verifiedFirst.Checkpoint,
            "CURRENT truncated the checkpoint.");
        LocalAssert.Equal(
            MultilineNextAction,
            verifiedFirst.NextAction,
            "CURRENT truncated the next action.");
        var retainedFirst = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            Convert.FromBase64String(first.SnapshotCanonicalBase64));
        var retainedTask = retainedFirst.Tasks.Single();
        LocalAssert.Equal(MultilineTask, retainedTask.Objective, "Retention truncated the current task.");
        LocalAssert.Equal(
            MultilineCheckpoint,
            retainedTask.CurrentState,
            "Retention truncated the checkpoint.");
        LocalAssert.Equal(
            MultilineNextAction,
            retainedTask.NextAction!,
            "Retention truncated the next action.");
        LocalAssert.Equal(
            MultilineGate,
            retainedTask.Constraints.Single(),
            "Retention truncated the gate.");

        // This is the cycle seam a later watcher may call after a 60-second
        // hint/poll interval. No watcher or timer is enabled by this test.
        var afterSixtySeconds = await CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
            new CortexLocalContinuityRefreshArguments(
                currentOptions with { CapturedAt = firstCapture.AddSeconds(60) }),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.Equal(13L, afterSixtySeconds.CurrentRevision, "A time-only refresh advanced CURRENT authority.");
        LocalAssert.Equal(1L, afterSixtySeconds.RetainedRevision, "A 60-second replay advanced retained state.");
        LocalAssert.Equal("idempotent", afterSixtySeconds.Disposition, "A 60-second replay was not idempotent.");
        LocalAssert.Equal(first.SnapshotHash, afterSixtySeconds.SnapshotHash, "A time-only replay changed retained bytes.");

        var atHorizon = await CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
            new CortexLocalContinuityRefreshArguments(
                currentOptions with { CapturedAt = horizon }),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.Equal(13L, atHorizon.CurrentRevision, "The one-hour horizon relabeled CURRENT authority.");
        LocalAssert.Equal(2L, atHorizon.RetainedRevision, "The one-hour horizon did not advance retained state once.");
        LocalAssert.Equal("appended", atHorizon.Disposition, "The one-hour horizon was not refreshed.");
        LocalAssert.False(
            string.Equals(first.SnapshotHash, atHorizon.SnapshotHash, StringComparison.Ordinal),
            "The one-hour horizon reused expired refresh bytes.");

        var verifiedAtHorizon = await CortexCurrentProjectionReader.ReadAsync(
            projectRoot,
            horizon,
            maximumAge: null,
            CancellationToken.None).ConfigureAwait(false);
        var retained = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            Convert.FromBase64String(atHorizon.SnapshotCanonicalBase64));
        LocalAssert.Equal(
            verifiedAtHorizon.AuthoritySetHash,
            CortexProjectBriefService.ComputeAuthoritySetHash(retained.SourceRevisions),
            "CURRENT and the retained SQLite snapshot have different authority hashes.");
        LocalAssert.True(
            atHorizon.ProjectBriefContextSetId is not null,
            "The SQLite project brief did not return its verified context set.");
    }

    private static async Task FailedMaterializationDoesNotPublishCandidateAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var capturedAt = DateTimeOffset.UtcNow;
        LocalCurrentMaterializationTests.WriteProject(
            projectRoot,
            currentRevision: 30,
            capturedAt.AddMinutes(-1),
            "failure-atomic-before");
        WriteRefreshStartHere(projectRoot, "failure-atomic-before");
        var currentOptions = new CortexLocalCurrentSnapshotArguments(
            context.ProfileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                context.ProfileId,
                Path.GetDirectoryName(context.DatabasePath)!),
            LocalTestContext.PrimaryProjectLinkId,
            capturedAt);
        var options = new CortexLocalContinuityRefreshArguments(currentOptions);
        var before = await CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
            options,
            CancellationToken.None).ConfigureAwait(false);
        var currentPath = Path.Combine(projectRoot, ".cortex", "CURRENT.json");
        var currentBeforeFailure = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        var stateBeforeFailure = SnapshotStateRoot(context);
        var statePath = Path.Combine(projectRoot, ".cortex", "STATE.md");
        var authorityBeforeFailure = await File.ReadAllBytesAsync(statePath)
            .ConfigureAwait(false);
        await File.AppendAllTextAsync(
            statePath,
            "\n# Candidate that must not publish.\n",
            new UTF8Encoding(false)).ConfigureAwait(false);

        await LocalAssert.ThrowsAsync<IOException>(
            () => CortexLocalContinuityRefreshCommand.RefreshOnceAsync(
                    new CortexLocalContinuityRefreshArguments(
                        currentOptions with { CapturedAt = capturedAt.AddMinutes(1) }),
                    CancellationToken.None,
                    static (_, staged, _) =>
                    {
                        LocalAssert.True(
                            staged.AuthorityRevision > 1,
                            "The forced materialization failure did not receive a staged candidate.");
                        throw new IOException("forced materialization failure");
                    })
                .AsTask(),
            "A forced materialization failure was accepted.").ConfigureAwait(false);

        var currentAfterFailure = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        LocalAssert.True(
            currentBeforeFailure.AsSpan().SequenceEqual(currentAfterFailure),
            "A failed materialization published its staged CURRENT.json.");
        LocalAssert.Equal(
            stateBeforeFailure,
            SnapshotStateRoot(context),
            "A failed materialization changed the retained SQLite head.");

        // Restore the authority bytes so the preserved prior CURRENT can be
        // independently revalidated against the retained head.
        await File.WriteAllBytesAsync(statePath, authorityBeforeFailure)
            .ConfigureAwait(false);
        var preserved = await CortexCurrentProjectionReader.ReadAsync(
            projectRoot,
            capturedAt.AddMinutes(1),
            maximumAge: null,
            CancellationToken.None).ConfigureAwait(false);
        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            context.ProfileId,
            currentOptions.StatePaths.DatabasePath,
            currentOptions.StatePaths.RegistryPath,
            "operator",
            TimeProvider.System,
            CancellationToken.None).ConfigureAwait(false);
        var heads = await composition.Store.ListAllHeadsByKindAsync(
            before.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            afterDocumentId: null,
            CortexAgentRetentionLimits.MaximumKindHeadPageSize,
            CancellationToken.None).ConfigureAwait(false);
        var retainedHead = heads.Items.Single(item =>
            string.Equals(
                item.DocumentId,
                CortexCurrentTaskSnapshotFactory.TaskSetId,
                StringComparison.Ordinal));
        var retained = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            retainedHead.CanonicalUtf8);
        LocalAssert.Equal(
            preserved.AuthoritySetHash,
            CortexProjectBriefService.ComputeAuthoritySetHash(retained.SourceRevisions),
            "Preserved CURRENT and retained head diverged after materialization failure.");
        LocalAssert.Equal(
            before.SnapshotHash,
            retained.SnapshotHash,
            "A failed materialization advanced the retained snapshot head.");
    }

    private static async Task LiteralCliRequiresOnceAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var capturedAt = DateTimeOffset.UtcNow;
        LocalCurrentMaterializationTests.WriteProject(
            projectRoot,
            currentRevision: 4,
            capturedAt.AddMinutes(-1),
            "cli-before-refresh");
        WriteRefreshStartHere(projectRoot, "cli");
        await File.AppendAllTextAsync(
            Path.Combine(projectRoot, ".cortex", "TASKS.md"),
            "\n# CLI authority change.\n",
            new UTF8Encoding(false)).ConfigureAwait(false);

        var arguments = Arguments(context, capturedAt);
        var result = await LocalProcessHarness.RunAsync(arguments, [])
            .ConfigureAwait(false);
        LocalAssert.Equal(0, result.ExitCode, $"continuity-refresh --once failed: {result.StandardError}");
        LocalAssert.True(
            string.IsNullOrEmpty(result.StandardError),
            "Successful continuity refresh emitted an error diagnostic.");
        var materialized = CortexCanonicalJson.Deserialize<CortexLocalCurrentSnapshotResult>(
            Encoding.UTF8.GetBytes(result.StandardOutput.Trim()));
        LocalAssert.True(materialized.Accepted, "The CLI result did not accept exact retained bytes.");
        LocalAssert.Equal(5L, materialized.CurrentRevision, "The CLI did not regenerate CURRENT.");
        LocalAssert.True(
            materialized.ProjectBriefContextSetId is not null &&
            materialized.ProjectionIssueCode is null,
            "The CLI did not verify its SQLite project brief.");

        var currentPath = Path.Combine(projectRoot, ".cortex", "CURRENT.json");
        var beforeRejectedWatch = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        var watchArguments = arguments.ToArray();
        watchArguments[1] = "--watch";
        var parsedWatch = CortexLocalContinuityRefreshArguments.Parse(watchArguments);
        LocalAssert.Equal(
            CortexLocalContinuityRefreshMode.Watch,
            parsedWatch.Mode,
            "The literal watch selector did not select foreground watch mode.");

        var rejectedSelectorArguments = arguments.ToArray();
        rejectedSelectorArguments[1] = "--watch-now";
        var rejectedWatch = await LocalProcessHarness.RunAsync(
                rejectedSelectorArguments,
                [])
            .ConfigureAwait(false);
        LocalAssert.Equal(2, rejectedWatch.ExitCode, "A non-literal watch selector was accepted.");
        LocalAssert.True(
            rejectedWatch.StandardError.Contains(
                "cortex_local_continuity_refresh_arguments_invalid",
                StringComparison.Ordinal),
            "The rejected watch-like selector did not return its bounded diagnostic.");
        var afterRejectedWatch = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        LocalAssert.True(
            beforeRejectedWatch.AsSpan().SequenceEqual(afterRejectedWatch),
            "The rejected watch-like selector changed CURRENT.json.");
    }

    private static async Task FileHintWakesBeforeSafetyPollAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        var capturedAt = DateTimeOffset.UtcNow;
        LocalCurrentMaterializationTests.WriteProject(
            projectRoot,
            currentRevision: 8,
            capturedAt,
            "file-hint");
        WriteRefreshStartHere(projectRoot, "file-hint");

        await using var signals =
            new FileSystemCortexLocalContinuityChangeSignalSource(projectRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = signals.WaitAsync(
            CortexLocalContinuityWatch.SafetyPollInterval,
            deadline.Token).AsTask();
        await File.AppendAllTextAsync(
            Path.Combine(projectRoot, ".cortex", "STATE.md"),
            "\n# File hint.\n",
            new UTF8Encoding(false),
            deadline.Token).ConfigureAwait(false);
        var observed = await pending.ConfigureAwait(false);
        LocalAssert.Equal(
            CortexLocalContinuitySignal.FileHint,
            observed,
            "An exact authority-file event did not wake the watcher before its safety poll.");
    }

    private static async Task WatchCoordinatorRecoversMissedEventsAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var projectRoot = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId).RootPath;
        // Start far enough in the past to exercise the 23-hour refresh point
        // without presenting future-dated test assertions to the real SQLite
        // composition clock.
        var now = DateTimeOffset.UtcNow
            .AddHours(-23)
            .AddMinutes(-2);
        LocalCurrentMaterializationTests.WriteProject(
            projectRoot,
            currentRevision: 20,
            now.AddMinutes(-1),
            "watch-start");
        WriteRefreshStartHere(projectRoot, "watch-start");
        var currentOptions = new CortexLocalCurrentSnapshotArguments(
            context.ProfileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                context.ProfileId,
                Path.GetDirectoryName(context.DatabasePath)!),
            LocalTestContext.PrimaryProjectLinkId,
            now);
        var options = new CortexLocalContinuityRefreshArguments(
            currentOptions,
            CortexLocalContinuityRefreshMode.Watch);
        await using var signals = new ManualContinuitySignalSource();
        var clock = new ManualTimeProvider(now);
        var results = Channel.CreateUnbounded<CortexLocalCurrentSnapshotResult>();
        var failures = Channel.CreateUnbounded<Exception>();
        using var cancellation = new CancellationTokenSource();
        var watch = CortexLocalContinuityWatch.RunAsync(
            options,
            projectRoot,
            signals,
            clock,
            (result, _) =>
            {
                results.Writer.TryWrite(result);
                return ValueTask.CompletedTask;
            },
            (exception, _) =>
            {
                failures.Writer.TryWrite(exception);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        var first = await ReadWithTimeoutAsync(results.Reader).ConfigureAwait(false);
        LocalAssert.Equal(1L, first.RetainedRevision, "Watch startup did not materialize one head.");
        _ = await ReadWithTimeoutAsync(signals.WaitEntries).ConfigureAwait(false);
        var currentPath = Path.Combine(projectRoot, ".cortex", "CURRENT.json");
        var currentBeforeInvalid = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        var stateBeforeInvalid = SnapshotStateRoot(context);

        File.WriteAllText(
            Path.Combine(projectRoot, "START_HERE.md"),
            """
            # Start Here

            ## Current Recovery

            - Current task: Preserve invalid-authority test evidence.
            - Checkpoint: The required Gate field is intentionally absent.
            - Next action: Repair the authority file before refresh.
            """ + "\n",
            new UTF8Encoding(false));
        signals.Signal(CortexLocalContinuitySignal.FileHint);
        var invalid = await ReadWithTimeoutAsync(failures.Reader).ConfigureAwait(false);
        LocalAssert.True(
            invalid.Message.Contains("missing Gate", StringComparison.Ordinal),
            "Invalid authority did not fail at the missing hot field.");
        _ = await ReadWithTimeoutAsync(signals.WaitEntries).ConfigureAwait(false);
        var currentAfterInvalid = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        LocalAssert.True(
            currentBeforeInvalid.AsSpan().SequenceEqual(currentAfterInvalid),
            "Invalid authority changed CURRENT.json.");
        LocalAssert.Equal(
            stateBeforeInvalid,
            SnapshotStateRoot(context),
            "Invalid authority changed SQLite retained state.");

        // Repair without a file hint, then release only the safety-poll wake.
        // This proves correctness does not depend on delivery of watcher events.
        WriteRefreshStartHere(projectRoot, "missed-event-repair");
        clock.Advance(TimeSpan.FromMinutes(1));
        signals.Signal(CortexLocalContinuitySignal.SafetyPoll);
        var repaired = await ReadWithTimeoutAsync(results.Reader).ConfigureAwait(false);
        LocalAssert.Equal(2L, repaired.RetainedRevision, "The safety poll missed repaired authority.");
        LocalAssert.False(
            string.Equals(first.AuthoritySetHash, repaired.AuthoritySetHash, StringComparison.Ordinal),
            "The missed-event recovery retained the stale authority hash.");
        _ = await ReadWithTimeoutAsync(signals.WaitEntries).ConfigureAwait(false);

        var currentBeforeSpuriousHint = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        var stateBeforeSpuriousHint = SnapshotStateRoot(context);
        clock.Advance(TimeSpan.FromMinutes(1));
        signals.Signal(CortexLocalContinuitySignal.FileHint);
        _ = await ReadWithTimeoutAsync(signals.WaitEntries).ConfigureAwait(false);
        LocalAssert.False(
            results.Reader.TryRead(out _),
            "An unchanged file hint emitted another refresh result.");
        var currentAfterSpuriousHint = await File.ReadAllBytesAsync(currentPath)
            .ConfigureAwait(false);
        LocalAssert.True(
            currentBeforeSpuriousHint.AsSpan().SequenceEqual(currentAfterSpuriousHint),
            "An unchanged file hint rewrote CURRENT.json.");
        LocalAssert.Equal(
            stateBeforeSpuriousHint,
            SnapshotStateRoot(context),
            "An unchanged file hint rewrote SQLite retained state.");

        clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(59));
        signals.Signal(CortexLocalContinuitySignal.SafetyPoll);
        var horizonRefresh = await ReadWithTimeoutAsync(results.Reader)
            .ConfigureAwait(false);
        LocalAssert.Equal(
            3L,
            horizonRefresh.RetainedRevision,
            "The watch safety poll did not preserve the one-hour refresh horizon.");
        LocalAssert.Equal(
            repaired.AuthoritySetHash,
            horizonRefresh.AuthoritySetHash,
            "The watch horizon refresh relabeled unchanged authority.");
        _ = await ReadWithTimeoutAsync(signals.WaitEntries).ConfigureAwait(false);

        var started = System.Diagnostics.Stopwatch.StartNew();
        cancellation.Cancel();
        try
        {
            await watch.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            throw new InvalidOperationException("The watch completed without cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        LocalAssert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            "Watch cancellation did not interrupt the bounded signal wait.");
    }

    private static async ValueTask<T> ReadWithTimeoutAsync<T>(ChannelReader<T> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

    private static string SnapshotStateRoot(LocalTestContext context)
    {
        var stateRoot = Path.GetDirectoryName(context.DatabasePath)!;
        return string.Join(
            "\n",
            Directory.GetFiles(stateRoot)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                    $"{Path.GetFileName(path)}:{CortexCanonicalJson.Sha256(File.ReadAllBytes(path))}"));
    }

    private static string[] Arguments(
        LocalTestContext context,
        DateTimeOffset capturedAt) =>
    [
        CortexLocalContinuityRefreshArguments.Command,
        "--once",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!,
        "--project-link",
        LocalTestContext.PrimaryProjectLinkId,
        "--captured-at-utc",
        capturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    ];

    private static void WriteRefreshStartHere(string projectRoot, string suffix) =>
        File.WriteAllText(
            Path.Combine(projectRoot, "START_HERE.md"),
            $"""
            # Start Here

            ## Current Recovery

            - Current task: Refresh the bounded {suffix} continuity fixture.
            - Gate: Keep the fixture local and passive.
            - Checkpoint: The authority files are ready for one refresh.
            - Next action: Verify the retained snapshot and project brief.
            """ + "\n",
            new UTF8Encoding(false));

    private static void WriteMultilineRefreshStartHere(string projectRoot) =>
        File.WriteAllText(
            Path.Combine(projectRoot, "START_HERE.md"),
            """
            # Start Here

            ## Current Recovery

            - Current task: `Refresh the bounded direct continuity fixture across the complete
              retained path.`
            - Gate: Keep the fixture local, passive, and independent of every
              network route.
            - Checkpoint: The authority files are ready for one exact snapshot and matching
              project brief.
            - Next action: Verify the complete retained snapshot, project brief, and published
              projection.

            ## Next Section

            - This text must never be folded into a current-recovery field.
            """ + "\n",
            new UTF8Encoding(false));

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class ManualContinuitySignalSource :
        ICortexLocalContinuityChangeSignalSource
    {
        private readonly Channel<CortexLocalContinuitySignal> _signals =
            Channel.CreateUnbounded<CortexLocalContinuitySignal>();
        private readonly Channel<int> _waitEntries = Channel.CreateUnbounded<int>();
        private int _waitCount;

        public ChannelReader<int> WaitEntries => _waitEntries.Reader;

        public void Signal(CortexLocalContinuitySignal value) =>
            _signals.Writer.TryWrite(value);

        public async ValueTask<CortexLocalContinuitySignal> WaitAsync(
            TimeSpan safetyPollInterval,
            CancellationToken cancellationToken)
        {
            LocalAssert.Equal(
                CortexLocalContinuityWatch.SafetyPollInterval,
                safetyPollInterval,
                "The watch coordinator changed its fixed 60-second safety poll.");
            _waitEntries.Writer.TryWrite(Interlocked.Increment(ref _waitCount));
            return await _signals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _signals.Writer.TryComplete();
            _waitEntries.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
