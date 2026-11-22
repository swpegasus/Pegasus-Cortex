using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.Sync.Client.Commands;
using Pegasus.Cortex.Sync.Client.ChangeSources;
using Pegasus.Cortex.Sync.Client.Contracts;
using Pegasus.Cortex.Sync.Client.Orchestration;
using Pegasus.Cortex.Sync.Client.Persistence;
using Pegasus.Cortex.Sync.Client.Watching;

var tests = new (string Name, Func<Task> Run)[]
{
    ("sync-now journals before submit and records reconciled cursor", TestSynchronizedAsync),
    ("unchanged authority refreshes expiring retained payload without false conflict", TestRetainedRefreshAsync),
    ("remote-only drift fails closed without local overwrite", TestRemoteDriftAsync),
    ("both-changed conflict preserves the journal and skips submit", TestBothChangedAsync),
    ("journal gaps and epoch rollback fail before network", TestJournalFailureAsync),
    ("cursor chain rejects remote epoch and high-water rollback", TestCursorPolicyAsync),
    ("materialize-current output is scope and authority verified", TestMaterializedChangeAsync),
    ("lost submit response replays byte-identical request after restart", TestLostResponseReplayAsync),
    ("lost response A replays after later descendant B with historical A proof", TestLostResponseAfterDescendantAsync),
    ("cursor write failure replays and repairs before attempt retirement", TestCursorWriteRepairAsync),
    ("pending attempt tamper and project-link mismatch fail closed", TestAttemptTamperAndScopeAsync),
    ("volatile pending save blocks every upload", TestVolatileAttemptSaveAsync),
    ("protected pending and cursor stores use durable read-back promotion", TestProtectedAttemptStoreAsync),
    ("pre-1.1 active pending evidence is durably re-sealed before replay", TestPreUpgradeAttemptResealAsync),
    ("pending recovery validates all stages before one promotion", TestProtectedAttemptRecoveryAsync),
    ("cursor 1.0 compatibility and bounded crash-stage recovery are explicit", TestCursorUpgradeAndRecoveryAsync),
    ("durable writers reserve bounded recovery artifact capacity", TestRecoveryArtifactCapacityAsync),
    ("retirement failure preserves acknowledged attempt for cleanup repair", TestCleanupSafetyAsync),
    ("older backlog response is validated against exported leaf not newer current", TestOlderBacklogAsync),
    ("server event limits reduce the exported backlog page", TestServerSmallerLimitsAsync),
    ("watch retries temporary outage and invalid response with bounded backoff", TestWatchRecoveryAsync),
    ("transport cancellation is retryable only when it is not caller cancellation", TestTransportCancellationAsync),
    ("watch cancellation stops retry without a busy loop", TestWatchCancellationAsync),
    ("watcher rejects unsafe Windows aliases and invalid intermediate components", TestWatcherPathValidationAsync),
    ("watcher enforces portable absolute and component path budgets", TestWatcherPortablePathBudgetsAsync),
    ("watcher rejects an intermediate reparse path", TestWatcherIntermediateReparseAsync),
    ("watcher treats replace and delete metadata races as hints", TestWatcherRaceHintsAsync),
    ("watcher treats a stable file event as a hint", TestWatcherHintAsync),
    ("poll winner cannot retain a reader and steal the next file hint", TestWatcherPollCleanupAsync)
};

var failures = new List<string>();
var skipped = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (TestSkippedException exception)
    {
        skipped.Add(test.Name);
        Console.WriteLine($"SKIP {test.Name}: {exception.Message}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}
Console.WriteLine($"RESULT {tests.Length - failures.Count - skipped.Count}/{tests.Length - skipped.Count} passed, {skipped.Count} skipped");
return failures.Count == 0 ? 0 : 1;

static async Task TestSynchronizedAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var source = new FakeSource(Change(authority));
    var journal = new FakeJournal(log);
    var cursor = new FakeCursorStore();
    var transport = new FakeTransport(log, MissingHead(), authority);
    var coordinator = Coordinator(source, journal, cursor, transport);
    using var output = new StringWriter();
    var exit = await CortexSyncClientCommand.RunAsync(
        ["sync-now"], coordinator, null, output, CancellationToken.None);

    Assert(exit == 0, $"sync-now did not succeed: {output}");
    Assert(log.IndexOf("append") >= 0 && log.IndexOf("append") < log.IndexOf("submit"),
        "submit occurred before durable journal append");
    Assert(journal.AcknowledgmentCount == 1, "typed receipt was not recorded");
    Assert(cursor.Value?.LastAcknowledgedAuthorityHash == authority, "acknowledged authority cursor missing");
    Assert(output.ToString().Contains("sync_reconciled", StringComparison.Ordinal), "command output omitted result");
    Assert(output.ToString().Contains(
            "\"contractId\":\"pegasus.cortex.sync-client-result/1.1\"",
            StringComparison.Ordinal),
        "command output did not advertise the changed 1.1 result contract");
}

static async Task TestRemoteDriftAsync()
{
    var oldHash = Hash("ancestor");
    var remoteHash = Hash("remote-change");
    var log = new List<string>();
    var journal = new FakeJournal(log, highWater: 1, epoch: "epoch-local", reconciled: true);
    var cursor = new FakeCursorStore(Cursor(oldHash, localHighWater: 1, remoteHighWater: 1));
    var transport = new FakeTransport(log, ExistingHead(remoteHash, highWater: 2), remoteHash);
    var result = await Coordinator(new FakeSource(Change(oldHash)), journal, cursor, transport)
        .SyncNowAsync(CancellationToken.None);

    Assert(result.Disposition == CortexSyncRunDisposition.RemoteDrift, "remote drift was not reported");
    Assert(!log.Contains("append") && !log.Contains("submit"), "remote drift mutated/uploaded local state");
}

static async Task TestRetainedRefreshAsync()
{
    var authority = Hash("same-authority");
    var baseline = Change(authority, retainedRevision: 1);
    var refreshed = Change(authority, retainedRevision: 2);
    var log = new List<string>();
    var journal = new FakeJournal(log, highWater: 1, epoch: "epoch-local", reconciled: true);
    var cursor = new FakeCursorStore(Cursor(
        authority,
        localHighWater: 1,
        remoteHighWater: 1,
        payloadHash: CortexCanonicalJson.Sha256(baseline.Payload.Span)));
    var transport = new FakeTransport(
        log,
        ExistingHead(
            authority,
            highWater: 1,
            payloadHash: CortexCanonicalJson.Sha256(baseline.Payload.Span)),
        authority);
    var result = await Coordinator(new FakeSource(refreshed), journal, cursor, transport)
        .SyncNowAsync(CancellationToken.None);

    Assert(result.Disposition == CortexSyncRunDisposition.Synchronized,
        $"same-authority retained refresh was mistaken for unchanged state or conflict: {result.Message}");
    Assert(log.Contains("append") && log.Contains("submit"),
        "refreshed retained payload was not journaled and submitted");
    Assert(cursor.Value?.LastAcknowledgedPayloadHash == CortexCanonicalJson.Sha256(refreshed.Payload.Span),
        "refreshed payload hash was not acknowledged");

    var convergedLog = new List<string>();
    var convergedJournal = new FakeJournal(
        convergedLog, highWater: 1, epoch: "epoch-local", reconciled: true);
    var convergedCursor = new FakeCursorStore(Cursor(
        authority,
        localHighWater: 1,
        remoteHighWater: 1,
        payloadHash: CortexCanonicalJson.Sha256(baseline.Payload.Span)));
    var convergedTransport = new FakeTransport(
        convergedLog,
        ExistingHead(
            authority,
            highWater: 1,
            payloadHash: CortexCanonicalJson.Sha256(refreshed.Payload.Span)),
        authority);
    var converged = await Coordinator(
            new FakeSource(refreshed),
            convergedJournal,
            convergedCursor,
            convergedTransport)
        .SyncNowAsync(CancellationToken.None);
    Assert(converged.Disposition == CortexSyncRunDisposition.Synchronized,
        "matching independent refreshes were reported as false divergence");
}

static async Task TestBothChangedAsync()
{
    var oldHash = Hash("ancestor");
    var localHash = Hash("local-change");
    var remoteHash = Hash("remote-change");
    var log = new List<string>();
    var journal = new FakeJournal(log, highWater: 1, epoch: "epoch-local", reconciled: true);
    var cursor = new FakeCursorStore(Cursor(oldHash, localHighWater: 1, remoteHighWater: 1));
    var transport = new FakeTransport(log, ExistingHead(remoteHash, highWater: 2), remoteHash);
    var result = await Coordinator(new FakeSource(Change(localHash)), journal, cursor, transport)
        .SyncNowAsync(CancellationToken.None);

    Assert(result.Disposition == CortexSyncRunDisposition.Conflict, "both-changed conflict was not reported");
    Assert(log.Contains("append"), "local conflict state was not durably journaled");
    Assert(!log.Contains("submit"), "conflict was submitted");
}

static async Task TestJournalFailureAsync()
{
    var hash = Hash("authority");
    var gapJournal = new FakeJournal([], highWater: 2, epoch: "epoch-local", reconciled: true, omitLastStatus: true);
    var transport = new FakeTransport([], MissingHead(), hash);
    var gap = await Coordinator(new FakeSource(Change(hash)), gapJournal, new FakeCursorStore(), transport)
        .SyncNowAsync(CancellationToken.None);
    Assert(gap.DiagnosticCode == "sync_local_state_invalid", "journal gap did not fail locally");
    Assert(transport.ProbeCount == 0, "journal gap contacted transport");

    var rollbackJournal = new FakeJournal([], highWater: 1, epoch: "epoch-replaced", reconciled: true);
    var rollbackCursor = new FakeCursorStore(Cursor(hash, localHighWater: 1, remoteHighWater: 1));
    var rollbackTransport = new FakeTransport([], ExistingHead(hash, highWater: 1), hash);
    var rollback = await Coordinator(new FakeSource(Change(hash)), rollbackJournal, rollbackCursor, rollbackTransport)
        .SyncNowAsync(CancellationToken.None);
    Assert(rollback.DiagnosticCode == "sync_local_state_invalid", "local epoch rollback did not fail closed");
    Assert(rollbackTransport.ProbeCount == 0, "local epoch rollback contacted transport");
}

static Task TestCursorPolicyAsync()
{
    var first = Cursor(Hash("one"), localHighWater: 1, remoteHighWater: 2);
    CortexSyncCursorPolicy.Validate(first, null);
    var invalid = CortexSyncCursorPolicy.Seal(first with
    {
        Revision = 2,
        RemoteStoreEpoch = "remote-other",
        RemoteHighWaterSequence = 1,
        PreviousCursorHash = first.CursorHash,
        CursorHash = CortexSyncClientContractIds.EmptySha256
    });
    AssertThrows<InvalidDataException>(() => CortexSyncCursorPolicy.Validate(invalid, first));
    return Task.CompletedTask;
}

static Task TestMaterializedChangeAsync()
{
    var (snapshot, authorityHash) = TestFixtures.Snapshot();
    var result = new
    {
        contractId = "pegasus.cortex.local-current-materialization-result/1.0",
        accepted = true,
        disposition = "appended",
        projectLinkId = snapshot.ProjectLinkId!,
        projectId = snapshot.ProjectId,
        projectNamespace = "projects.neural-commander",
        authoritySetHash = authorityHash,
        currentRevision = 12,
        retainedRevision = snapshot.SnapshotRevision,
        snapshotHash = snapshot.SnapshotHash,
        contextSetId = snapshot.ContextSetId,
        projectBriefDisposition = "appended",
        projectBriefContextSetId = snapshot.ContextSetId,
        projectionIssueCode = (string?)null,
        snapshotCanonicalBase64 = Convert.ToBase64String(CortexCanonicalJson.Serialize(snapshot))
    };
    var bytes = CortexCanonicalJson.Serialize(result).Concat("\n"u8.ToArray()).ToArray();
    var parsed = CortexMaterializeCurrentChangeSource.ParseResult(bytes);
    Assert(parsed.AuthorityHash == authorityHash, "authority hash was not retained");
    Assert(parsed.ProjectId == snapshot.ProjectId, "project scope changed");

    var changed = CortexCanonicalJson.Serialize(result with { authoritySetHash = Hash("wrong") });
    AssertThrows<InvalidDataException>(() => CortexMaterializeCurrentChangeSource.ParseResult(changed));
    return Task.CompletedTask;
}

static async Task TestLostResponseReplayAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var source = new FakeSource(Change(authority));
    var journal = new FakeJournal(log);
    var cursor = new FakeCursorStore();
    var attempts = new FakeAttemptStore();
    var transport = new FakeTransport(log, MissingHead(), authority)
    {
        LostResponseFailures = 1
    };

    var first = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(first.Retryable && first.DiagnosticCode == "sync_submit_validation_failed",
        "lost response was not classified as retryable");
    Assert(attempts.Value is not null && cursor.Value is null && journal.AcknowledgmentCount == 0,
        "lost response did not preserve only the pending request");
    var exact = transport.SubmittedCanonicalRequests.Single().ToArray();

    var second = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(second.Disposition == CortexSyncRunDisposition.Synchronized,
        $"restart replay did not reconcile: {second.Message}");
    Assert(transport.SubmittedCanonicalRequests.Count == 2 &&
        transport.SubmittedCanonicalRequests[1].Span.SequenceEqual(exact),
        "restart replay changed the canonical submit-request bytes");
    Assert(attempts.Value is null && journal.AcknowledgmentCount == 1 && cursor.Value is not null,
        "replayed request was retired before all local acknowledgments were durable");
}

static async Task TestLostResponseAfterDescendantAsync()
{
    var log = new List<string>();
    var authorityA = TestFixtures.Snapshot().AuthorityHash;
    var source = new FakeSource(Change(authorityA));
    var journal = new FakeJournal(log);
    var cursor = new FakeCursorStore();
    var attempts = new FakeAttemptStore();
    var transport = new FakeTransport(log, MissingHead(), authorityA)
    {
        LostResponseFailures = 1
    };

    var lost = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(lost.Retryable && attempts.Value is not null,
        "lost response A did not retain its exact request");
    var requestA = transport.SubmittedCanonicalRequests.Single().ToArray();
    transport.AdvanceToDescendant(Hash("authority-B"), Hash("payload-B"));
    var descendant = transport.CurrentHead;

    var replayed = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(replayed.Disposition == CortexSyncRunDisposition.Synchronized,
        $"historical A replay was blocked by later descendant B: {replayed.Message}");
    Assert(transport.SubmittedCanonicalRequests.Count == 2 &&
        transport.SubmittedCanonicalRequests[1].Span.SequenceEqual(requestA),
        "restart did not replay byte-identical A after B advanced the live head");
    Assert(transport.CurrentHead.HeadHash == descendant.HeadHash &&
        transport.CurrentHead.HighWaterSequence > cursor.Value!.RemoteHighWaterSequence,
        "historical A result incorrectly replaced B or checkpointed B as proof of A");
    Assert(attempts.Value is null && journal.AcknowledgmentCount == 1,
        "exact historical A proof did not durably acknowledge and retire A");
}

static async Task TestCursorWriteRepairAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var source = new FakeSource(Change(authority));
    var journal = new FakeJournal(log);
    var cursor = new FakeCursorStore(failSaves: 1);
    var attempts = new FakeAttemptStore();
    var transport = new FakeTransport(log, MissingHead(), authority);

    var first = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(first.DiagnosticCode == "sync_cursor_write_failed" && attempts.Value is not null,
        "cursor-write failure did not retain the exact submit attempt");
    Assert(journal.AcknowledgmentCount == 1 && cursor.Value is null,
        "cursor failure test did not reach durable receipt-before-cursor ordering");

    var second = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(second.Disposition == CortexSyncRunDisposition.Synchronized && cursor.Value is not null,
        "cursor repair replay did not complete");
    Assert(transport.SubmittedCanonicalRequests.Count == 2 &&
        transport.SubmittedCanonicalRequests[0].Span.SequenceEqual(
            transport.SubmittedCanonicalRequests[1].Span),
        "cursor repair did not replay byte-identical request evidence");
}

static async Task TestAttemptTamperAndScopeAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var change = Change(authority);
    var journal = new FakeJournal(log);
    await journal.AppendAsync(change, CancellationToken.None);
    var export = await journal.ExportPendingAsync(
        ExportRequest(limit: 1),
        CancellationToken.None);
    var request = CortexSyncContractPolicy.Seal(new CortexSyncSubmitRequest(
        change.ProjectLinkId,
        export.Bundle!));
    var attempt = CortexSyncPendingAttemptPolicy.Create(request, MissingHead());
    var tampered = attempt.Evidence with
    {
        CanonicalRequestSha256 = Hash("tampered-request"),
        AttemptHash = CortexSyncClientContractIds.EmptySha256
    };
    tampered = tampered with { AttemptHash = CortexSyncPendingAttemptPolicy.ComputeHash(tampered) };
    AssertThrows<InvalidDataException>(() => CortexSyncPendingAttemptPolicy.ValidateAndDecode(tampered));

    var attempts = new FakeAttemptStore(attempt);
    var otherScope = change with
    {
        ProjectLinkId = "plink_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    };
    var transport = new FakeTransport(log, MissingHead(), authority);
    var result = await Coordinator(
            new FakeSource(otherScope),
            journal,
            new FakeCursorStore(),
            transport,
            attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(result.DiagnosticCode == "sync_local_state_invalid" && transport.ProbeCount == 0,
        "pending attempt project-link mismatch did not fail before network access");
}

static async Task TestVolatileAttemptSaveAsync()
{
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var transport = new FakeTransport([], MissingHead(), authority);
    var attempts = new FakeAttemptStore { DurableForReplay = false };
    var source = new FakeSource(Change(authority));
    var journal = new FakeJournal([]);
    var cursor = new FakeCursorStore();
    var result = await Coordinator(
            source,
            journal,
            cursor,
            transport,
            attempts)
        .SyncNowAsync(CancellationToken.None);

    Assert(result.DiagnosticCode == "sync_pending_attempt_write_failed",
        "a volatile attempt save was not rejected at the upload gate");
    Assert(transport.SubmitCount == 0,
        "transport upload followed a save that was not crash durable");
    var probeCount = transport.ProbeCount;
    var retry = await Coordinator(
            source,
            journal,
            cursor,
            transport,
            attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(retry.DiagnosticCode == "sync_pending_attempt_write_failed" &&
        transport.SubmitCount == 0 && transport.ProbeCount == probeCount,
        "a later run contacted remote transport for an attempt without a fresh durability proof");

    var mismatchedTransport = new FakeTransport([], MissingHead(), authority);
    var mismatched = await Coordinator(
            new FakeSource(Change(authority)),
            new FakeJournal([]),
            new FakeCursorStore(),
            mismatchedTransport,
            new FakeAttemptStore { SavedAttemptHashOverride = Hash("wrong-save") })
        .SyncNowAsync(CancellationToken.None);
    Assert(mismatched.DiagnosticCode == "sync_pending_attempt_write_failed" &&
        mismatchedTransport.SubmitCount == 0,
        "upload followed a durable-save result for a different attempt hash");
}

static async Task TestProtectedAttemptStoreAsync()
{
    if (!OperatingSystem.IsWindows())
        throw new TestSkippedException(
            "the default protected-local write-through primitive is Windows-only");
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-sync-attempt-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    MakeOwnerPrivate(root);
    try
    {
        var attempt = await CreateAttemptAsync();

        await using (var store = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            var saved = await store.SaveAsync(attempt, CancellationToken.None);
            Assert(saved.DurableForReplay && saved.AttemptHash == attempt.Evidence.AttemptHash,
                "protected store did not prove its exact durable save");
        }
        await using (var reopened = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            var read = await reopened.ReadAsync(CancellationToken.None);
            Assert(read is not null &&
                read.CanonicalRequestBytes.Span.SequenceEqual(attempt.CanonicalRequestBytes.Span),
                "protected store did not replay the exact canonical bytes after restart");
        }

        // Model a process loss immediately after the durable active-to-retired move. Exact
        // retired evidence makes the retry idempotent and is then quarantined outside replay.
        Directory.Move(
            Path.Combine(root, "a", "p", attempt.Evidence.AttemptId),
            Path.Combine(root, "a", "r", attempt.Evidence.AttemptId));
        await using (var reopened = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            await reopened.RetireAsync(attempt, CancellationToken.None);
            Assert(await reopened.ReadAsync(CancellationToken.None) is null,
                "idempotent retirement restored or retained an active attempt");
        }
        Assert(!Directory.EnumerateFiles(root, "attempt.json", SearchOption.AllDirectories).Any(),
            "retired full-request bytes were not reconciled after the replay guard was safe");

        var cursor = Cursor(TestFixtures.Snapshot().AuthorityHash, 1, 1);
        await using (var cursorStore = new ProtectedCortexSyncCursorStore(root, root))
            await cursorStore.SaveAsync(cursor, CancellationToken.None);
        await using (var reopenedCursor = new ProtectedCortexSyncCursorStore(root, root))
        {
            var read = await reopenedCursor.ReadAsync(CancellationToken.None);
            Assert(read?.CursorHash == cursor.CursorHash,
                "durably promoted cursor checkpoint did not survive reopen/read-back");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestPreUpgradeAttemptResealAsync()
{
    if (!OperatingSystem.IsWindows())
        throw new TestSkippedException(
            "the default protected-local write-through primitive is Windows-only");
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-sync-legacy-attempt-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    MakeOwnerPrivate(root);
    try
    {
        var attempt = await CreateAttemptAsync();
        var active = Path.Combine(root, "a", "p", attempt.Evidence.AttemptId);
        Directory.CreateDirectory(active);
        await File.WriteAllBytesAsync(
            Path.Combine(active, "attempt.json"),
            CortexCanonicalJson.Serialize(attempt.Evidence));

        var boundaryFactory = new RecordingJournalBoundaryFactory();
        await using (var store = new ProtectedCortexSyncPendingAttemptStore(
                         root,
                         root,
                         boundaryFactory))
        {
            var recovered = await store.ReadAsync(CancellationToken.None);
            Assert(recovered?.Evidence.AttemptHash == attempt.Evidence.AttemptHash &&
                !File.Exists(Path.Combine(active, "durability.json")),
                "pre-1.1 active evidence was not recovered as explicitly unmarked state");

            var saved = await store.SaveAsync(attempt, CancellationToken.None);
            Assert(saved.DurableForReplay &&
                saved.AttemptHash == attempt.Evidence.AttemptHash &&
                File.Exists(Path.Combine(active, "durability.json")),
                "pre-1.1 active evidence received no verified durable 1.1 marker");
        }

        Assert(boundaryFactory.DurableMoves.Count == 2 &&
            boundaryFactory.DurableMoves[0].Source == $"a/p/{attempt.Evidence.AttemptId}" &&
            boundaryFactory.DurableMoves[0].Destination.StartsWith("a/x/s-a-", StringComparison.Ordinal) &&
            boundaryFactory.DurableMoves[1].Source.StartsWith("a/s/a-", StringComparison.Ordinal) &&
            boundaryFactory.DurableMoves[1].Destination == $"a/p/{attempt.Evidence.AttemptId}",
            "legacy active evidence was marked without the required crash-recoverable two-move transition");

        await using var reopened = new ProtectedCortexSyncPendingAttemptStore(root, root);
        var replay = await reopened.SaveAsync(attempt, CancellationToken.None);
        Assert(replay.DurableForReplay,
            "the durably re-sealed attempt did not retain verified replay authority after restart");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestProtectedAttemptRecoveryAsync()
{
    if (!OperatingSystem.IsWindows())
        throw new TestSkippedException(
            "the default protected-local write-through primitive is Windows-only");
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-sync-recovery-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    MakeOwnerPrivate(root);
    try
    {
        var first = await CreateAttemptAsync(retainedRevision: 1);
        var second = await CreateAttemptAsync(retainedRevision: 2);
        var firstStage = "a-11111111111111111111111111111111";
        var secondStage = "a-22222222222222222222222222222222";
        await WriteAttemptStageAsync(root, firstStage, first);
        await WriteAttemptStageAsync(root, secondStage, second);

        await using (var conflicted = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => conflicted.ReadAsync(CancellationToken.None).AsTask());
        }
        Assert(Directory.Exists(Path.Combine(root, "a", "s", firstStage)) &&
            Directory.Exists(Path.Combine(root, "a", "s", secondStage)),
            "recovery mutated the first stage before discovering a conflicting second attempt");

        Directory.Delete(Path.Combine(root, "a", "s", secondStage), recursive: true);
        await using (var recovered = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            var active = await recovered.ReadAsync(CancellationToken.None);
            Assert(active?.Evidence.AttemptHash == first.Evidence.AttemptHash,
                "the only fully validated stage was not durably promoted");
        }

        var conflictingRetired = Path.Combine(root, "a", "r", first.Evidence.AttemptId);
        Directory.CreateDirectory(conflictingRetired);
        await File.WriteAllBytesAsync(
            Path.Combine(conflictingRetired, "attempt.json"),
            CortexCanonicalJson.Serialize(first.Evidence));
        await using (var conflicted = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => conflicted.ReadAsync(CancellationToken.None).AsTask());
        }
        Assert(Directory.Exists(conflictingRetired) &&
            Directory.Exists(Path.Combine(root, "a", "p", first.Evidence.AttemptId)),
            "active-plus-retired conflict was mutated instead of failing closed");
        Directory.Delete(conflictingRetired, recursive: true);

        // An exact duplicate stage is safe only after all stages validate and must leave the
        // already-active byte identity unchanged.
        var duplicateStage = "a-33333333333333333333333333333333";
        await WriteAttemptStageAsync(root, duplicateStage, first);
        await using (var recovered = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            var active = await recovered.ReadAsync(CancellationToken.None);
            Assert(active?.Evidence.AttemptHash == first.Evidence.AttemptHash &&
                !Directory.Exists(Path.Combine(root, "a", "s", duplicateStage)),
                "exact duplicate stage was not reconciled outside replay authority");
            await recovered.RetireAsync(first, CancellationToken.None);
        }

        for (var index = 0; index < 9; index++)
        {
            Directory.CreateDirectory(Path.Combine(
                root,
                "a",
                "s",
                $"a-{index:x32}"));
        }
        await using (var bounded = new ProtectedCortexSyncPendingAttemptStore(root, root))
        {
            await AssertThrowsAsync<IOException>(
                () => bounded.ReadAsync(CancellationToken.None).AsTask());
        }
        Assert(Directory.EnumerateDirectories(Path.Combine(root, "a", "s")).Count() == 9,
            "over-limit stage enumeration mutated recovery evidence");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestCursorUpgradeAndRecoveryAsync()
{
    if (!OperatingSystem.IsWindows())
        throw new TestSkippedException(
            "the default protected-local write-through primitive is Windows-only");
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-cursor-recovery-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    MakeOwnerPrivate(root);
    try
    {
        var current = Cursor(TestFixtures.Snapshot().AuthorityHash, 1, 1);
        var legacyCandidate = current with
        {
            ContractId = CortexSyncClientContractIds.CursorV1_0,
            CursorHash = CortexSyncClientContractIds.EmptySha256
        };
        var legacy = legacyCandidate with
        {
            CursorHash = CortexSyncCursorPolicy.ComputeHash(legacyCandidate)
        };
        var history = Path.Combine(
            root,
            "c",
            "h",
            $"{legacy.Revision:D16}-{legacy.CursorHash[..16]}");
        Directory.CreateDirectory(history);
        await File.WriteAllBytesAsync(
            Path.Combine(history, "cursor.json"),
            CortexCanonicalJson.Serialize(legacy));

        await using (var store = new ProtectedCortexSyncCursorStore(root, root))
        {
            var read = await store.ReadAsync(CancellationToken.None);
            Assert(read?.ContractId == CortexSyncClientContractIds.CursorV1_0,
                "shape-compatible cursor 1.0 history was not accepted as a read-only prefix");
            var upgraded = NextCursor(read!);
            await store.SaveAsync(upgraded, CancellationToken.None);
        }

        CortexSyncCursorV1 latest;
        await using (var store = new ProtectedCortexSyncCursorStore(root, root))
            latest = (await store.ReadAsync(CancellationToken.None))!;
        Assert(latest.ContractId == CortexSyncClientContractIds.Cursor && latest.Revision == 2,
            "cursor 1.0 prefix did not continue with a 1.1 checkpoint");

        var staged = NextCursor(latest);
        await WriteCursorStageAsync(
            root,
            "c-11111111111111111111111111111111",
            staged);
        await using (var store = new ProtectedCortexSyncCursorStore(root, root))
        {
            var recovered = await store.ReadAsync(CancellationToken.None);
            Assert(recovered?.CursorHash == staged.CursorHash,
                "a complete bounded cursor stage was not durably recovered");
        }

        var firstConflict = NextCursor(staged);
        var secondConflictCandidate = firstConflict with
        {
            RemoteRevision = "server-rev-conflict",
            UpdatedAt = firstConflict.UpdatedAt.AddSeconds(1),
            CursorHash = CortexSyncClientContractIds.EmptySha256
        };
        var secondConflict = CortexSyncCursorPolicy.Seal(secondConflictCandidate);
        var firstStage = "c-22222222222222222222222222222222";
        var secondStage = "c-33333333333333333333333333333333";
        await WriteCursorStageAsync(root, firstStage, firstConflict);
        await WriteCursorStageAsync(root, secondStage, secondConflict);
        await using (var store = new ProtectedCortexSyncCursorStore(root, root))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(CancellationToken.None).AsTask());
        }
        Assert(Directory.Exists(Path.Combine(root, "c", "s", firstStage)) &&
            Directory.Exists(Path.Combine(root, "c", "s", secondStage)),
            "cursor recovery mutated a stage before discovering a conflicting candidate");
        Directory.Delete(Path.Combine(root, "c", "s", firstStage), recursive: true);
        Directory.Delete(Path.Combine(root, "c", "s", secondStage), recursive: true);
        for (var index = 0; index < 9; index++)
        {
            Directory.CreateDirectory(Path.Combine(
                root,
                "c",
                "s",
                $"c-{index:x32}"));
        }
        await using (var bounded = new ProtectedCortexSyncCursorStore(root, root))
        {
            await AssertThrowsAsync<IOException>(
                () => bounded.ReadAsync(CancellationToken.None).AsTask());
        }
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "c", "s")))
            Directory.Delete(directory);

        var incompleteCandidate = legacy with
        {
            RemoteBundleHash = string.Empty,
            CursorHash = CortexSyncClientContractIds.EmptySha256
        };
        var incomplete = incompleteCandidate with
        {
            CursorHash = CortexSyncCursorPolicy.ComputeHash(incompleteCandidate)
        };
        var migration = AssertThrowsAndReturn<InvalidDataException>(() =>
            CortexSyncCursorPolicy.ValidateStored(incomplete, null));
        Assert(migration.Message.Contains("operator-authorized bounded migration", StringComparison.Ordinal),
            "incompatible cursor 1.0 state did not expose its safe upgrade path");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestRecoveryArtifactCapacityAsync()
{
    if (!OperatingSystem.IsWindows())
        throw new TestSkippedException(
            "the default protected-local write-through primitive is Windows-only");

    var roots = Enumerable.Range(0, 3)
        .Select(index => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"cortex-sync-capacity-{index}-{Guid.NewGuid():N}"))))
        .ToArray();
    foreach (var root in roots)
    {
        Directory.CreateDirectory(root);
        MakeOwnerPrivate(root);
    }

    try
    {
        var cursorStageRoot = Path.Combine(roots[0], "c", "s");
        Directory.CreateDirectory(cursorStageRoot);
        for (var index = 0; index < 8; index++)
            Directory.CreateDirectory(Path.Combine(cursorStageRoot, $"c-{index:x32}"));
        var cursorBoundary = new RecordingJournalBoundaryFactory { FailCleanup = true };
        await using (var cursorStore = new ProtectedCortexSyncCursorStore(
                         roots[0],
                         roots[0],
                         cursorBoundary))
        {
            _ = await cursorStore.ReadAsync(CancellationToken.None);
            await AssertThrowsAsync<InvalidDataException>(() =>
                cursorStore.SaveAsync(
                    Cursor(TestFixtures.Snapshot().AuthorityHash, 1, 1),
                    CancellationToken.None).AsTask());
        }
        Assert(Directory.EnumerateDirectories(cursorStageRoot).Count() == 8,
            "cursor save exceeded its stage cap after cleanup failures");

        var pendingStageRoot = Path.Combine(roots[1], "a", "s");
        Directory.CreateDirectory(pendingStageRoot);
        for (var index = 0; index < 8; index++)
            Directory.CreateDirectory(Path.Combine(pendingStageRoot, $"a-{index:x32}"));
        var pendingBoundary = new RecordingJournalBoundaryFactory { FailCleanup = true };
        var pendingAttempt = await CreateAttemptAsync();
        await using (var attemptStore = new ProtectedCortexSyncPendingAttemptStore(
                         roots[1],
                         roots[1],
                         pendingBoundary))
        {
            await AssertThrowsAsync<InvalidDataException>(() =>
                attemptStore.SaveAsync(pendingAttempt, CancellationToken.None).AsTask());
        }
        Assert(Directory.EnumerateDirectories(pendingStageRoot).Count() == 8,
            "pending-attempt save exceeded its stage cap after cleanup failures");

        var legacyAttempt = await CreateAttemptAsync(retainedRevision: 2);
        var active = Path.Combine(roots[2], "a", "p", legacyAttempt.Evidence.AttemptId);
        Directory.CreateDirectory(active);
        await File.WriteAllBytesAsync(
            Path.Combine(active, "attempt.json"),
            CortexCanonicalJson.Serialize(legacyAttempt.Evidence));
        var cleanupRoot = Path.Combine(roots[2], "a", "x");
        Directory.CreateDirectory(cleanupRoot);
        for (var index = 0; index < 32; index++)
            Directory.CreateDirectory(Path.Combine(cleanupRoot, $"s-a-{index:x32}"));
        var cleanupBoundary = new RecordingJournalBoundaryFactory { FailCleanup = true };
        await using (var attemptStore = new ProtectedCortexSyncPendingAttemptStore(
                         roots[2],
                         roots[2],
                         cleanupBoundary))
        {
            await AssertThrowsAsync<InvalidDataException>(() =>
                attemptStore.SaveAsync(legacyAttempt, CancellationToken.None).AsTask());
        }
        Assert(Directory.Exists(active) &&
            Directory.EnumerateDirectories(cleanupRoot).Count() == 32 &&
            !Directory.EnumerateDirectories(Path.Combine(roots[2], "a", "s")).Any() &&
            cleanupBoundary.DurableMoves.Count == 0,
            "legacy active evidence moved before cleanup capacity was reserved");
    }
    finally
    {
        foreach (var root in roots)
            Directory.Delete(root, recursive: true);
    }
}

static async Task TestCleanupSafetyAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var source = new FakeSource(Change(authority));
    var journal = new FakeJournal(log);
    var cursor = new FakeCursorStore();
    var attempts = new FakeAttemptStore { RetirementFailures = 1 };
    var transport = new FakeTransport(log, MissingHead(), authority);

    var first = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(first.DiagnosticCode == "sync_pending_attempt_retire_failed" &&
        attempts.Value is not null && cursor.Value is not null,
        "retirement failure erased submit evidence or its durable cursor");
    var submitCount = transport.SubmitCount;

    var repaired = await Coordinator(source, journal, cursor, transport, attempts)
        .SyncNowAsync(CancellationToken.None);
    Assert(repaired.DiagnosticCode == "sync_pending_attempt_retired" && attempts.Value is null,
        "acknowledged pending evidence was not safely retired on repair");
    Assert(transport.SubmitCount == submitCount,
        "cleanup-only repair unnecessarily resubmitted an already acknowledged attempt");
}

static async Task TestOlderBacklogAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var older = Change(authority, retainedRevision: 1);
    var current = Change(authority, retainedRevision: 2);
    var journal = new FakeJournal(log, pendingChanges: [older]);
    var cursor = new FakeCursorStore();
    var transport = new FakeTransport(
        log,
        MissingHead(),
        authority,
        TestFixtures.Capabilities(maximumEventsPerBundle: 1, maximumReceiptOutcomes: 1));
    var result = await Coordinator(
            new FakeSource(current),
            journal,
            cursor,
            transport,
            new FakeAttemptStore())
        .SyncNowAsync(CancellationToken.None);

    var submitted = transport.SubmittedRequests.Single().Bundle.Events.Single();
    var olderHash = CortexCanonicalJson.Sha256(older.Payload.Span);
    Assert(result.Disposition == CortexSyncRunDisposition.Synchronized,
        $"older backlog page did not reconcile: {result.Message}");
    Assert(string.Equals(submitted.PayloadSha256, olderHash, StringComparison.Ordinal) &&
        string.Equals(cursor.Value?.LastAcknowledgedPayloadHash, olderHash, StringComparison.Ordinal),
        "submit validation or cursor used the newer CURRENT projection instead of the exported leaf");
    Assert(journal.PendingCount == 1, "newer CURRENT was not left pending for the next bounded page");
}

static async Task TestServerSmallerLimitsAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var backlog = Enumerable.Range(1, 8).Select(revision => Change(authority, revision)).ToArray();
    var current = Change(authority, retainedRevision: 9);
    var journal = new FakeJournal(log, pendingChanges: backlog);
    var one = await journal.ExportPendingAsync(ExportRequest(limit: 1), CancellationToken.None);
    var two = await journal.ExportPendingAsync(ExportRequest(limit: 2), CancellationToken.None);
    var oneBytes = CortexCanonicalJson.Serialize(one.Bundle!).Length;
    var twoBytes = CortexCanonicalJson.Serialize(two.Bundle!).Length;
    Assert(twoBytes > oneBytes, "server-limit fixture did not create distinct bundle sizes");
    var advertisedBundleBytes = oneBytes + ((twoBytes - oneBytes) / 2);
    var maximumPayload = backlog.Max(item => item.Payload.Length);
    var capabilities = TestFixtures.Capabilities(
        maximumEventPayloadBytes: maximumPayload,
        maximumEventsPerBundle: 8,
        maximumCanonicalBundleBytes: advertisedBundleBytes,
        maximumReceiptOutcomes: 8);
    var transport = new FakeTransport(log, MissingHead(), authority, capabilities);
    var result = await Coordinator(
            new FakeSource(current),
            journal,
            new FakeCursorStore(),
            transport,
            new FakeAttemptStore())
        .SyncNowAsync(CancellationToken.None);

    var submitted = transport.SubmittedRequests.Single().Bundle;
    Assert(result.Disposition == CortexSyncRunDisposition.Synchronized,
        $"smaller authenticated limits were not honored: {result.Message}");
    Assert(submitted.Events.Count == 1 &&
        CortexCanonicalJson.Serialize(submitted).Length <= advertisedBundleBytes &&
        journal.ExportLimits.Count > 1,
        "client did not reduce the contiguous page before persisting/submitting it");
}

static async Task TestWatchRecoveryAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var transport = new FakeTransport(log, MissingHead(), authority)
    {
        CapabilityOutageFailures = 1,
        InvalidSubmitFailures = 1
    };
    var coordinator = Coordinator(
        new FakeSource(Change(authority)),
        new FakeJournal(log),
        new FakeCursorStore(),
        transport,
        new FakeAttemptStore());
    using var stop = new CancellationTokenSource();
    await using var signal = new FakeSignalSource(call =>
    {
        if (call == 3)
            stop.Cancel();
    });
    using var output = new StringWriter();
    try
    {
        await CortexSyncClientCommand.RunAsync(
            ["watch"], coordinator, signal, output, stop.Token);
        throw new InvalidOperationException("watch unexpectedly exited without cancellation");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }

    Assert(transport.SubmitCount == 2 && transport.SubmittedCanonicalRequests.Count == 2 &&
        transport.SubmittedCanonicalRequests[0].Span.SequenceEqual(
            transport.SubmittedCanonicalRequests[1].Span),
        "watch did not recover the invalid response with the preserved request");
    Assert(signal.Intervals.SequenceEqual([
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(60)]),
        "watch retry backoff or safety poll was not bounded and deterministic");
}

static async Task TestTransportCancellationAsync()
{
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var timeoutTransport = new FakeTransport([], MissingHead(), authority)
    {
        TransportCancellationFailures = 1
    };
    var retryable = await Coordinator(
            new FakeSource(Change(authority)),
            new FakeJournal([]),
            new FakeCursorStore(),
            timeoutTransport)
        .SyncNowAsync(CancellationToken.None);
    Assert(retryable.Retryable && retryable.DiagnosticCode == "sync_remote_probe_failed",
        "a transport-owned TaskCanceledException was not classified as retryable");

    using var caller = new CancellationTokenSource();
    var callerTransport = new FakeTransport([], MissingHead(), authority)
    {
        TransportCancellationFailures = 1,
        BeforeTransportCancellation = caller.Cancel
    };
    await AssertThrowsAsync<OperationCanceledException>(() => Coordinator(
            new FakeSource(Change(authority)),
            new FakeJournal([]),
            new FakeCursorStore(),
            callerTransport)
        .SyncNowAsync(caller.Token)
        .AsTask());
}

static async Task TestWatchCancellationAsync()
{
    var log = new List<string>();
    var authority = TestFixtures.Snapshot().AuthorityHash;
    var transport = new FakeTransport(log, MissingHead(), authority)
    {
        CapabilityOutageFailures = int.MaxValue
    };
    var coordinator = Coordinator(
        new FakeSource(Change(authority)),
        new FakeJournal(log),
        new FakeCursorStore(),
        transport,
        new FakeAttemptStore());
    using var stop = new CancellationTokenSource();
    await using var signal = new FakeSignalSource(_ => stop.Cancel());
    using var output = new StringWriter();
    try
    {
        await CortexSyncClientCommand.RunAsync(
            ["watch"], coordinator, signal, output, stop.Token);
        throw new InvalidOperationException("cancelled watch unexpectedly returned");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
    Assert(transport.ProbeCount == 1 && signal.Intervals.Count == 1 &&
        signal.Intervals[0] == TimeSpan.FromSeconds(1),
        "cancelled watch retried without waiting or entered a busy loop");
}

static async Task TestWatcherPathValidationAsync()
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-sync-paths-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    try
    {
        var invalid = new[]
        {
            "C:CURRENT.json",
            "CURRENT.json:ads",
            "aux.json",
            "COM¹.txt",
            "lpt².log",
            "folder./CURRENT.json",
            "folder /CURRENT.json",
            new string('x', 241),
            new string('\u00e9', 121),
            new string('y', 4096),
            string.Join('/', Enumerable.Repeat("a", 33)),
            string.Join('/', Enumerable.Repeat(new string('a', 40), 6)) + "/CURRENT.json"
        };
        foreach (var path in invalid)
        {
            AssertThrows<ArgumentException>(() =>
                _ = new FileSystemCortexSyncChangeSignalSource(root, [path]));
        }
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new FileSystemCortexSyncChangeSignalSource(root, OverLimitAuthorityPaths()));

        var authorityDirectory = Path.Combine(root, ".cortex");
        Directory.CreateDirectory(authorityDirectory);
        await using (var accepted = new FileSystemCortexSyncChangeSignalSource(
                         root,
                         [".cortex/CURRENT.json"]))
        {
        }
        Directory.Delete(authorityDirectory);
        await File.WriteAllTextAsync(authorityDirectory, "not-a-directory");
        AssertThrows<ArgumentException>(() =>
            _ = new FileSystemCortexSyncChangeSignalSource(root, [".cortex/CURRENT.json"]));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestWatcherPortablePathBudgetsAsync()
{
    var utf8Boundary = new string('\u00e9', 120);
    Assert(FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "/srv/" + utf8Boundary,
            '/',
            '/'),
        "portable POSIX path rejected a component at exactly 240 UTF-8 bytes");
    Assert(!FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "/srv/" + utf8Boundary + '\u00e9',
            '/',
            '/'),
        "portable POSIX path accepted a component over 240 UTF-8 bytes");
    Assert(FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "/" + new string('p', 239),
            '/',
            '/'),
        "portable POSIX path rejected exactly 240 UTF-16 code units");
    Assert(!FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "/" + new string('p', 240),
            '/',
            '/'),
        "portable POSIX path accepted more than 240 UTF-16 code units");
    Assert(FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "C:\\" + new string('w', 237),
            '\\',
            '/'),
        "portable Windows path rejected exactly 240 UTF-16 code units");
    Assert(!FileSystemCortexSyncChangeSignalSource.HasCanonicalPathBudgetForTest(
            "C:\\" + new string('w', 238),
            '\\',
            '/'),
        "portable Windows path accepted more than 240 UTF-16 code units");

    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "cortex-sync-portable-path-" + Guid.NewGuid().ToString("N"))));
    Directory.CreateDirectory(root);
    try
    {
        var exactLeafLength = 240 - root.Length - 1;
        Assert(exactLeafLength is > 0 and <= 240,
            "test host temp root cannot exercise the canonical absolute-path boundary");
        var exactLeaf = new string('q', exactLeafLength);
        Assert(Path.Combine(root, exactLeaf).Length == 240,
            "test did not construct an exactly bounded canonical path");
        await using (var accepted = new FileSystemCortexSyncChangeSignalSource(root, [exactLeaf]))
        {
        }
        AssertThrows<ArgumentException>(() =>
            _ = new FileSystemCortexSyncChangeSignalSource(root, [exactLeaf + "q"]));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static Task TestWatcherIntermediateReparseAsync()
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "cortex-sync-link-" + Guid.NewGuid().ToString("N"))));
    var outside = root + "-outside";
    var link = Path.Combine(root, ".cortex");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    try
    {
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new TestSkippedException(
                "the host denied test-only symbolic-link creation");
        }
        AssertThrows<ArgumentException>(() =>
            _ = new FileSystemCortexSyncChangeSignalSource(root, [".cortex/CURRENT.json"]));
        Directory.Delete(link);
    }
    finally
    {
        if (Directory.Exists(link) &&
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(link);
        }
        Directory.Delete(root, recursive: true);
        Directory.Delete(outside, recursive: true);
    }
    return Task.CompletedTask;
}

static async Task TestWatcherRaceHintsAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "cortex-sync-races-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "CURRENT.json");
    await File.WriteAllTextAsync(file, "one");
    try
    {
        await using var watcher = new FileSystemCortexSyncChangeSignalSource(
            root,
            ["CURRENT.json"],
            TimeSpan.FromMilliseconds(100));
        File.Delete(file);
        watcher.CaptureHintSnapshotForTest();

        var replacement = Path.Combine(root, "replacement.tmp");
        await File.WriteAllTextAsync(replacement, "two");
        File.Move(replacement, file);
        watcher.CaptureHintSnapshotForTest();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = watcher.WaitAsync(TimeSpan.FromSeconds(60), timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        var old = Path.Combine(root, "old.tmp");
        var next = Path.Combine(root, "next.tmp");
        await File.WriteAllTextAsync(next, "three", timeout.Token);
        File.Move(file, old);
        File.Move(next, file);
        File.Delete(old);
        Assert(await wait == CortexSyncWakeReason.FileHint,
            "rename-away and replacement race faulted instead of producing a file hint");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestWatcherHintAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "cortex-sync-watch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "CURRENT.json");
    await File.WriteAllTextAsync(file, "one");
    try
    {
        await using var watcher = new FileSystemCortexSyncChangeSignalSource(
            root,
            ["CURRENT.json"],
            TimeSpan.FromMilliseconds(100));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = watcher.WaitAsync(TimeSpan.FromSeconds(60), timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        await File.WriteAllTextAsync(file, "two", timeout.Token);
        Assert(await wait == CortexSyncWakeReason.FileHint, "watcher did not surface a stable file hint");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestWatcherPollCleanupAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "cortex-sync-poll-cleanup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "CURRENT.json");
    await File.WriteAllTextAsync(file, "one");
    try
    {
        await using var watcher = new FileSystemCortexSyncChangeSignalSource(
            root,
            ["CURRENT.json"],
            TimeSpan.FromMilliseconds(100));
        var first = await watcher.WaitCoreAsync(
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);
        Assert(first == CortexSyncWakeReason.SafetyPoll, "short regression poll did not win");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = watcher.WaitCoreAsync(TimeSpan.FromSeconds(2), timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        await File.WriteAllTextAsync(file, "two", timeout.Token);
        Assert(await next == CortexSyncWakeReason.FileHint,
            "an abandoned poll-era channel reader consumed the next file hint");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static CortexSyncCoordinator Coordinator(
    ICortexLocalSyncChangeSource source,
    ICortexSyncClientJournal journal,
    ICortexSyncCursorStore cursor,
    ICortexSyncClientTransportV1_1 transport,
    ICortexSyncPendingAttemptStoreV1_1? attempts = null) =>
    new(
        source,
        journal,
        cursor,
        attempts ?? new FakeAttemptStore(),
        transport,
        new CortexSyncClientOptions("tenant-1", "projects.neural-commander", 1),
        new FixedTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00+00:00")));

static JournalExportRequest ExportRequest(int limit) => new(
    "bundle-test-1",
    "tenant-1",
    "projects.neural-commander",
    1,
    null,
    CortexSyncClientContractIds.EmptySha256,
    CortexSyncClientContractIds.EmptySha256,
    DateTimeOffset.Parse("2026-08-02T12:00:00+00:00"),
    limit);

static async Task<CortexSyncPendingSubmitAttempt> CreateAttemptAsync(
    int retainedRevision = 1)
{
    var authority = TestFixtures.Snapshot(retainedRevision).AuthorityHash;
    var change = Change(authority, retainedRevision);
    var journal = new FakeJournal([]);
    await journal.AppendAsync(change, CancellationToken.None);
    var export = await journal.ExportPendingAsync(ExportRequest(1), CancellationToken.None);
    var request = CortexSyncContractPolicy.Seal(new CortexSyncSubmitRequest(
        change.ProjectLinkId,
        export.Bundle!));
    return CortexSyncPendingAttemptPolicy.Create(request, MissingHead());
}

static async Task WriteAttemptStageAsync(
    string root,
    string stageLeaf,
    CortexSyncPendingSubmitAttempt attempt)
{
    var stage = Path.Combine(root, "a", "s", stageLeaf);
    Directory.CreateDirectory(stage);
    await File.WriteAllBytesAsync(
        Path.Combine(stage, "attempt.json"),
        CortexCanonicalJson.Serialize(attempt.Evidence));
}

static CortexSyncCursorV1 NextCursor(CortexSyncCursorV1 previous)
{
    var candidate = previous with
    {
        ContractId = CortexSyncClientContractIds.Cursor,
        Revision = previous.Revision + 1,
        UpdatedAt = previous.UpdatedAt.AddMinutes(1),
        PreviousCursorHash = previous.CursorHash,
        CursorHash = CortexSyncClientContractIds.EmptySha256
    };
    return CortexSyncCursorPolicy.Seal(candidate);
}

static async Task WriteCursorStageAsync(
    string root,
    string stageLeaf,
    CortexSyncCursorV1 cursor)
{
    var stage = Path.Combine(root, "c", "s", stageLeaf);
    Directory.CreateDirectory(stage);
    await File.WriteAllBytesAsync(
        Path.Combine(stage, "cursor.json"),
        CortexCanonicalJson.Serialize(cursor));
}

static IEnumerable<string> OverLimitAuthorityPaths()
{
    for (var index = 0; index < 65; index++)
        yield return $"authority-{index}.json";
    throw new InvalidOperationException("Watcher enumerated beyond its 65-item decision bound.");
}

static CortexLocalSyncChange Change(string authorityHash, int retainedRevision = 1)
{
    var payload = CortexCanonicalJson.Serialize(TestFixtures.Snapshot(retainedRevision).Snapshot);
    var payloadHash = CortexCanonicalJson.Sha256(payload);
    var capturedAt = DateTimeOffset.Parse("2026-08-02T11:59:00+00:00");
    return new CortexLocalSyncChange(
        "project-neural-commander",
        "plink_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        authorityHash,
        "snapshot-" + payloadHash[..24],
        "current-recovery",
        AgentTaskSnapshotContractIds.Snapshot,
        payload,
        capturedAt,
        capturedAt.AddHours(24));
}

static CortexSyncCursorV1 Cursor(
    string authorityHash,
    long localHighWater,
    long remoteHighWater,
    string? payloadHash = null)
{
    var value = new CortexSyncCursorV1(
        1,
        CortexSyncClientContractIds.Cursor,
        CortexSyncClientContractIds.Canonicalization,
        1,
        "project-neural-commander",
        "store-local",
        "epoch-local",
        localHighWater,
        "epoch-local",
        remoteHighWater,
        Hash("remote-event"),
        Hash("remote-bundle"),
        "server-rev-1",
        authorityHash,
        payloadHash ?? CortexCanonicalJson.Sha256(Change(authorityHash).Payload.Span),
        DateTimeOffset.Parse("2026-08-02T11:00:00+00:00"),
        CortexSyncClientContractIds.EmptySha256,
        CortexSyncClientContractIds.EmptySha256);
    return CortexSyncCursorPolicy.Seal(value);
}

static CortexSyncRemoteHead MissingHead() => CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
    1,
    CortexSyncContractIds.SyncGatewayHead,
    CortexSyncContractIds.Canonicalization,
    false,
    "plink_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "store-local",
    "epoch-local",
    "project-neural-commander",
    0,
    null,
    CortexSyncClientContractIds.EmptySha256,
    null,
    CortexSyncClientContractIds.EmptySha256,
    null,
    null,
    0,
    null,
    null,
    null,
    DateTimeOffset.Parse("2026-08-02T12:00:00+00:00"),
    CortexSyncClientContractIds.EmptySha256));

static CortexSyncRemoteHead ExistingHead(
    string authorityHash,
    long highWater,
    string? payloadHash = null) => CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
    1,
    CortexSyncContractIds.SyncGatewayHead,
    CortexSyncContractIds.Canonicalization,
    true,
    "plink_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "store-local",
    "epoch-local",
    "project-neural-commander",
    highWater,
    "remote-event-id",
    Hash("remote-event"),
    "remote-bundle-id",
    Hash("remote-bundle"),
    authorityHash,
    payloadHash ?? CortexCanonicalJson.Sha256(Change(authorityHash).Payload.Span),
    highWater,
    Hash($"remote-snapshot-{highWater}"),
    $"ctx_{Hash($"remote-context-{highWater}")}",
    $"server-rev-{highWater}",
    DateTimeOffset.Parse("2026-08-02T12:00:00+00:00"),
    CortexSyncClientContractIds.EmptySha256));

static string Hash(string value) => CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(value));

static void MakeOwnerPrivate(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return;
    }

    var current = WindowsIdentity.GetCurrent().User ??
        throw new InvalidOperationException("Current Windows SID unavailable.");
    var security = new DirectorySecurity();
    security.SetOwner(current);
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(
        current,
        FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow));
    new DirectoryInfo(path).SetAccessControl(security);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static TException AssertThrowsAndReturn<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class RecordingJournalBoundaryFactory : IJournalFileSystemBoundaryFactory
{
    private readonly IJournalFileSystemBoundaryFactory _inner =
        ProtectedLocalJournalFileSystemBoundaryFactory.Instance;

    public JournalFileSystemBoundarySafety Safety => _inner.Safety;
    public bool FailCleanup { get; init; }
    public List<(string Source, string Destination)> DurableMoves { get; } = [];

    public IJournalFileSystemBoundary OpenJournalRoot(
        string requestedRoot,
        string approvedRoot) =>
        new RecordingJournalBoundary(
            _inner.OpenJournalRoot(requestedRoot, approvedRoot),
            this);

    public ILegacySourceFileSystemBoundary OpenLegacySourceRoot(
        string requestedRoot,
        string approvedRoot) =>
        _inner.OpenLegacySourceRoot(requestedRoot, approvedRoot);

    private sealed class RecordingJournalBoundary(
        IJournalFileSystemBoundary inner,
        RecordingJournalBoundaryFactory owner) :
        IJournalFileSystemBoundary,
        IJournalDurableDirectoryPromotion
    {
        public JournalFileSystemBoundarySafety Safety => inner.Safety;
        public JournalDurabilityCapabilities Durability => inner.Durability;
        public string RootPath => inner.RootPath;
        public string AuthorizedRootIdentity => inner.AuthorizedRootIdentity;
        public string OpenedRootIdentity => inner.OpenedRootIdentity;
        public bool DurableDirectoryPromotionSupported =>
            inner is IJournalDurableDirectoryPromotion
            {
                DurableDirectoryPromotionSupported: true
            };

        public void EnsureDirectory(string relativePath) => inner.EnsureDirectory(relativePath);
        public void CreateDirectory(string relativePath) => inner.CreateDirectory(relativePath);
        public bool DirectoryExists(string relativePath) => inner.DirectoryExists(relativePath);
        public bool FileExists(string relativePath) => inner.FileExists(relativePath);
        public JournalFileSystemEntryKind GetEntryKind(string relativePath) =>
            inner.GetEntryKind(relativePath);
        public FileStream AcquireExclusiveLock(string relativePath) =>
            inner.AcquireExclusiveLock(relativePath);
        public FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous) =>
            inner.CreateNewFile(relativePath, bufferSize, asynchronous);
        public FileStream OpenReadFile(
            string relativePath,
            int bufferSize,
            bool asynchronous,
            bool sequentialScan) =>
            inner.OpenReadFile(relativePath, bufferSize, asynchronous, sequentialScan);
        public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
            string relativePath,
            int maximumEntries) =>
            inner.EnumerateDirectory(relativePath, maximumEntries);
        public void MoveDirectory(string sourceRelativePath, string destinationRelativePath) =>
            inner.MoveDirectory(sourceRelativePath, destinationRelativePath);

        public void MoveDirectoryDurably(
            string sourceRelativePath,
            string destinationRelativePath)
        {
            if (inner is not IJournalDurableDirectoryPromotion promotion)
                throw new NotSupportedException("The wrapped boundary has no durable promotion capability.");
            promotion.MoveDirectoryDurably(sourceRelativePath, destinationRelativePath);
            owner.DurableMoves.Add((sourceRelativePath, destinationRelativePath));
        }

        public void DeleteFile(string relativePath)
        {
            if (owner.FailCleanup)
                throw new IOException("Injected bounded-cleanup failure.");
            inner.DeleteFile(relativePath);
        }

        public void DeleteEmptyDirectory(string relativePath)
        {
            if (owner.FailCleanup)
                throw new IOException("Injected bounded-cleanup failure.");
            inner.DeleteEmptyDirectory(relativePath);
        }

        public void Dispose() => inner.Dispose();
    }
}

sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

sealed class TestSkippedException(string message) : Exception(message);

sealed class FakeSource(CortexLocalSyncChange change) : ICortexLocalSyncChangeSource
{
    public ValueTask<CortexLocalSyncChange> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(change);
    }
}

sealed class FakeCursorStore : ICortexSyncCursorStore
{
    private int _failSaves;

    public FakeCursorStore(CortexSyncCursorV1? initial = null, int failSaves = 0)
    {
        Value = initial;
        _failSaves = failSaves;
    }

    public CortexSyncCursorV1? Value { get; private set; }

    public ValueTask<CortexSyncCursorV1?> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Value);

    public ValueTask SaveAsync(CortexSyncCursorV1 cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failSaves > 0)
        {
            _failSaves--;
            throw new IOException("Injected cursor checkpoint failure.");
        }
        CortexSyncCursorPolicy.Validate(cursor, Value);
        Value = cursor;
        return ValueTask.CompletedTask;
    }
}

sealed class FakeAttemptStore : ICortexSyncPendingAttemptStoreV1_1
{
    public FakeAttemptStore(CortexSyncPendingSubmitAttempt? initial = null)
    {
        Value = initial;
    }

    public CortexSyncPendingSubmitAttempt? Value { get; private set; }
    public int RetirementFailures { get; set; }
    public bool DurableForReplay { get; set; } = true;
    public string? SavedAttemptHashOverride { get; set; }

    public ValueTask<CortexSyncPendingSubmitAttempt?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Value);
    }

    public ValueTask<CortexSyncPendingAttemptSaveResult> SaveAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CortexSyncPendingAttemptPolicy.Validate(attempt);
        if (Value is not null &&
            !string.Equals(Value.Evidence.AttemptHash, attempt.Evidence.AttemptHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A different fake attempt is already pending.");
        }
        Value = attempt;
        return ValueTask.FromResult(new CortexSyncPendingAttemptSaveResult(
            SavedAttemptHashOverride ?? attempt.Evidence.AttemptHash,
            DurableForReplay));
    }

    public ValueTask RetireAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RetirementFailures > 0)
        {
            RetirementFailures--;
            throw new IOException("Injected attempt-retirement failure.");
        }
        if (Value is null ||
            !string.Equals(Value.Evidence.AttemptHash, attempt.Evidence.AttemptHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fake pending attempt changed before retirement.");
        }
        Value = null;
        return ValueTask.CompletedTask;
    }
}

sealed class FakeJournal : ICortexSyncClientJournal
{
    private readonly List<string> _log;
    private readonly List<JournalEntrySyncStatus> _statuses;
    private readonly Dictionary<long, CortexLocalSyncChange> _changes = [];
    private readonly bool _omitLastStatus;
    private OfflineJournalStateV1 _state;

    public FakeJournal(
        List<string> log,
        long highWater = 0,
        string epoch = "epoch-local",
        bool reconciled = false,
        bool omitLastStatus = false,
        IReadOnlyList<CortexLocalSyncChange>? pendingChanges = null)
    {
        _log = log;
        _omitLastStatus = omitLastStatus;
        if (pendingChanges is not null && highWater != 0)
            throw new ArgumentException("A fake backlog supplies its own high-water sequence.");
        if (pendingChanges is not null)
            highWater = pendingChanges.Count;
        _state = State(highWater, epoch);
        _statuses = Enumerable.Range(1, checked((int)highWater))
            .Select(sequence => new JournalEntrySyncStatus(
                $"event-{sequence}", sequence,
                reconciled ? JournalEntrySyncState.Reconciled : JournalEntrySyncState.Pending,
                reconciled ? $"receipt-{sequence}" : null))
            .ToList();
        if (pendingChanges is not null)
        {
            for (var index = 0; index < pendingChanges.Count; index++)
                _changes.Add(index + 1L, pendingChanges[index]);
        }
    }

    public int AcknowledgmentCount { get; private set; }
    public int PendingCount => _statuses.Count(item => item.State == JournalEntrySyncState.Pending);
    public List<int> ExportLimits { get; } = [];

    public ValueTask<JournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new JournalRecoveryResult(true, _statuses.Count, 0, 0, []));

    public ValueTask<CortexSyncJournalSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JournalEntrySyncStatus> statuses = _omitLastStatus && _statuses.Count > 0
            ? _statuses[..^1]
            : _statuses;
        return ValueTask.FromResult(new CortexSyncJournalSnapshot(_state, statuses));
    }

    public ValueTask<JournalAppendResult> AppendAsync(
        CortexLocalSyncChange change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Add("append");
        var existing = _changes.SingleOrDefault(item =>
            string.Equals(item.Value.IdempotencyKey, change.IdempotencyKey, StringComparison.Ordinal));
        if (!existing.Equals(default(KeyValuePair<long, CortexLocalSyncChange>)))
        {
            return ValueTask.FromResult(new JournalAppendResult(
                JournalMutationDisposition.Existing,
                null,
                null));
        }
        var sequence = _state.HighWaterSequence + 1;
        _changes.Add(sequence, change);
        _state = State(sequence, _state.StoreEpoch);
        _statuses.Add(new JournalEntrySyncStatus($"event-{sequence}", sequence, JournalEntrySyncState.Pending, null));
        return ValueTask.FromResult(new JournalAppendResult(JournalMutationDisposition.Added, null, null));
    }

    public ValueTask<JournalExportResult> ExportPendingAsync(
        JournalExportRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExportLimits.Add(request.Limit);
        var selected = _statuses
            .Where(item => item.State == JournalEntrySyncState.Pending)
            .Take(request.Limit)
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("No fake pending change is available.");
        var previousHash = request.PriorEventHash;
        var events = new List<CortexOfflineEventV1>();
        foreach (var status in selected)
        {
            if (!_changes.TryGetValue(status.Sequence, out var change))
                throw new InvalidOperationException("A fake pending status has no retained change.");
            var payload = change.Payload.ToArray();
            var source = new CortexSyncSourceProvenanceV1(
                "client_operation", null, null, payload.Length, CortexCanonicalJson.Sha256(payload), change.CapturedAt);
            var candidate = new CortexOfflineEventV1(
                1, CortexSyncContractIds.OfflineEvent, CortexSyncContractIds.Canonicalization,
                _state.StoreId, _state.StoreEpoch, change.ProjectId, status.Sequence, status.EntryId,
                previousHash, change.CapturedAt, "sync-client", change.Operation,
                request.TenantIdHint, request.NamespaceHint, request.ObservedPolicyRevision,
                change.PayloadContractId, TestFixtures.Base64Url(payload), payload.Length, CortexCanonicalJson.Sha256(payload),
                source, CortexSyncContractIds.ContentTreatment, false, [], CortexSyncClientContractIds.EmptySha256);
            var item = CortexSyncContractPolicy.Seal(candidate);
            events.Add(item);
            previousHash = item.EventHash;
        }
        var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1, CortexSyncContractIds.OfflineBundle, CortexSyncContractIds.Canonicalization,
            request.BundleId, _state.StoreId, _state.StoreEpoch, _state.ProjectId,
            request.BaseServerRevision, request.PreviousBundleHash, request.PriorEventHash,
            events[0].Sequence, events[^1].Sequence, request.CreatedAt, events, [], CortexSyncClientContractIds.EmptySha256));
        return ValueTask.FromResult(new JournalExportResult(true, bundle, null));
    }

    public ValueTask<JournalTypedAcknowledgmentResult> RecordAcknowledgmentAsync(
        CortexOfflineBundleV1 bundle,
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcknowledgmentCount++;
        var existing = true;
        foreach (var item in bundle.Events)
        {
            var index = _statuses.FindIndex(status =>
                status.Sequence == item.Sequence &&
                string.Equals(status.EntryId, item.EventId, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidDataException("Fake receipt does not match retained status.");
            existing &= _statuses[index].State == JournalEntrySyncState.Reconciled &&
                string.Equals(_statuses[index].ReceiptId, receipt.ReceiptId, StringComparison.Ordinal);
            _statuses[index] = _statuses[index] with
            {
                State = JournalEntrySyncState.Reconciled,
                ReceiptId = receipt.ReceiptId
            };
        }
        return ValueTask.FromResult(new JournalTypedAcknowledgmentResult(
            existing ? JournalMutationDisposition.Existing : JournalMutationDisposition.Added,
            true,
            true,
            receipt,
            null));
    }

    private static OfflineJournalStateV1 State(long highWater, string epoch) => new(
        1,
        OfflineJournalContractIds.State,
        OfflineJournalContractIds.Canonicalization,
        "journal-local",
        "writer-local",
        "store-local",
        "project-neural-commander",
        epoch,
        highWater,
        CortexSyncClientContractIds.EmptySha256,
        CortexSyncClientContractIds.EmptySha256,
        CortexSyncClientContractIds.EmptySha256);
}

sealed class FakeTransport : ICortexSyncClientTransportV1_1
{
    private readonly List<string> _log;
    private readonly CortexSyncCapabilitiesV1 _capabilities;
    private readonly Dictionary<string, CortexSyncSubmitResult> _historicalResults = new(StringComparer.Ordinal);
    private CortexSyncRemoteHead _head;

    public FakeTransport(
        List<string> log,
        CortexSyncRemoteHead initialHead,
        string submittedAuthorityHash,
        CortexSyncCapabilitiesV1? capabilities = null)
    {
        _log = log;
        _head = initialHead;
        _capabilities = capabilities ?? TestFixtures.Capabilities();
        _ = submittedAuthorityHash;
    }

    public int ProbeCount { get; private set; }
    public int SubmitCount { get; private set; }
    public int LostResponseFailures { get; set; }
    public int CapabilityOutageFailures { get; set; }
    public int TransportCancellationFailures { get; set; }
    public int InvalidSubmitFailures { get; set; }
    public Action? BeforeTransportCancellation { get; set; }
    public CortexSyncRemoteHead CurrentHead => _head;
    public List<CortexSyncSubmitRequest> SubmittedRequests { get; } = [];
    public List<ReadOnlyMemory<byte>> SubmittedCanonicalRequests { get; } = [];

    public ValueTask<CortexSyncCapabilitiesV1> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount++;
        _log.Add("capabilities");
        if (TransportCancellationFailures > 0)
        {
            TransportCancellationFailures--;
            BeforeTransportCancellation?.Invoke();
            throw new TaskCanceledException("Injected transport-owned cancellation.");
        }
        if (CapabilityOutageFailures > 0)
        {
            CapabilityOutageFailures--;
            throw new HttpRequestException("Injected temporary capability outage.");
        }
        return ValueTask.FromResult(_capabilities);
    }

    public ValueTask<CortexSyncRemoteHead> GetHeadAsync(
        CortexSyncHeadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount++;
        _log.Add("head");
        return ValueTask.FromResult(_head);
    }

    public ValueTask<CortexSyncSubmitResult> SubmitAsync(
        CortexSyncSubmitRequest request,
        ReadOnlyMemory<byte> canonicalRequestBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CortexCanonicalJson.Serialize(request).AsSpan().SequenceEqual(canonicalRequestBytes.Span))
            throw new InvalidDataException("Transport did not receive the exact canonical request bytes.");
        SubmitCount++;
        SubmittedRequests.Add(request);
        SubmittedCanonicalRequests.Add(canonicalRequestBytes.ToArray());
        _log.Add("submit");
        if (InvalidSubmitFailures > 0)
        {
            InvalidSubmitFailures--;
            throw new InvalidDataException("Injected invalid remote submit response.");
        }
        if (_historicalResults.TryGetValue(request.RequestHash, out var historical))
            return ValueTask.FromResult(historical);
        var bundle = request.Bundle;
        var snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            Decode(bundle.Events[^1].PayloadBase64Url));
        var committedHead = CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
            1,
            CortexSyncContractIds.SyncGatewayHead,
            CortexSyncContractIds.Canonicalization,
            true,
            request.ProjectLinkId,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            bundle.LastSequence,
            bundle.Events[^1].EventId,
            bundle.Events[^1].EventHash,
            bundle.BundleId,
            bundle.BundleHash,
            CortexMaterializeCurrentChangeSource.ComputeAuthoritySetHash(snapshot.SourceRevisions),
            bundle.Events[^1].PayloadSha256,
            snapshot.SnapshotRevision,
            snapshot.SnapshotHash,
            snapshot.ContextSetId,
            "server-rev-1",
            DateTimeOffset.Parse("2026-08-02T12:00:02+00:00"),
            CortexSyncClientContractIds.EmptySha256));
        var result = CortexSyncContractPolicy.Seal(new CortexSyncSubmitResult(
            1,
            CortexSyncContractIds.SyncGatewaySubmitResult,
            CortexSyncContractIds.Canonicalization,
            TestFixtures.Receipt(bundle),
            committedHead,
            CortexSyncClientContractIds.EmptySha256));
        _historicalResults.Add(request.RequestHash, result);
        _head = committedHead;
        if (LostResponseFailures > 0)
        {
            LostResponseFailures--;
            throw new IOException("Injected lost submit response after remote commit.");
        }
        return ValueTask.FromResult(result);
    }

    public void AdvanceToDescendant(string authorityHash, string payloadHash)
    {
        if (!_head.Exists)
            throw new InvalidOperationException("A descendant requires an existing source head.");
        var sequence = _head.HighWaterSequence + 1;
        _head = CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
            1,
            CortexSyncContractIds.SyncGatewayHead,
            CortexSyncContractIds.Canonicalization,
            true,
            _head.ProjectLinkId,
            _head.StoreId,
            _head.StoreEpoch,
            _head.ProjectId,
            sequence,
            $"desc-event-{sequence}",
            Digest($"desc-event-{sequence}"),
            $"desc-bundle-{sequence}",
            Digest($"desc-bundle-{sequence}"),
            authorityHash,
            payloadHash,
            sequence,
            Digest($"desc-snapshot-{sequence}"),
            "ctx_" + Digest($"desc-context-{sequence}"),
            $"server-rev-desc-{sequence}",
            DateTimeOffset.Parse("2026-08-02T12:00:03+00:00"),
            CortexSyncClientContractIds.EmptySha256));
    }

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 0 => "", 2 => "==", 3 => "=", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }

    private static string Digest(string value) =>
        CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(value));
}

sealed class FakeSignalSource(Action<int> onWait) : ICortexSyncChangeSignalSource
{
    public List<TimeSpan> Intervals { get; } = [];

    public ValueTask<CortexSyncWakeReason> WaitAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken)
    {
        Intervals.Add(safetyPollInterval);
        onWait(Intervals.Count);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CortexSyncWakeReason.SafetyPoll);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

static class TestFixtures
{
    public static (AgentTaskSnapshotV1 Snapshot, string AuthorityHash) Snapshot(int snapshotRevision = 1)
    {
        var captured = DateTimeOffset.Parse("2026-08-02T11:59:00+00:00");
        var sourceHash = new string('a', 64);
        var sources = new[] { new AgentWorkSourceRevisionV1("start_here", "sha256-" + sourceHash, sourceHash) };
        var projection = new
        {
            contractId = "pegasus.cortex.authority-set/1.0",
            sources = sources.Select(item => new { item.SourceId, item.RevisionId, item.Sha256 })
        };
        var authorityHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(projection));
        var owner = new AgentWorkOwnerV1("local-os-policy", "os_test", "pegasus-cortex-sync-client");
        var gate = new AgentWorkApprovalGateV1(
            AgentWorkApprovalGateStateIds.Satisfied,
            "user",
            "approval-1",
            "Approval remains external.",
            grantsAuthority: false);
        var task = new AgentTaskV1(
            "task-1",
            null,
            0,
            AgentTaskStateIds.Unknown,
            "Preserve current recovery.",
            "Current state is captured.",
            "Continue synchronization.",
            ["Do not infer authority."],
            [],
            [],
            [],
            owner,
            gate,
            [],
            ["start_here"]);
        var previous = snapshotRevision > 1 ? Snapshot(snapshotRevision - 1).Snapshot : null;
        var candidate = new AgentTaskSnapshotV1(
            1,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            "current-recovery",
            $"snapshot-{snapshotRevision}",
            snapshotRevision,
            previous?.SnapshotHash,
            $"snapshot-current-{snapshotRevision}-" + authorityHash[..16],
            "project-neural-commander",
            "plink_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ctx_" + authorityHash,
            captured,
            captured.AddHours(24),
            owner,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                captured,
                captured.AddHours(24),
                "Verified authority sources."),
            sources,
            [],
            AgentTaskSnapshotContractIds.Coverage,
            [task],
            AgentWorkPacketContractIds.ContentTreatment,
            false,
            false,
            false,
            false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return (AgentTaskSnapshotPolicy.Seal(candidate), authorityHash);
    }

    public static CortexSyncCapabilitiesV1 Capabilities(
        int maximumEventPayloadBytes = 512 * 1024,
        int maximumEventsPerBundle = 256,
        int maximumCanonicalBundleBytes = 32 * 1024 * 1024,
        int maximumReceiptOutcomes = 256) => CortexSyncContractPolicy.Seal(
        new CortexSyncCapabilitiesV1(
            1,
            CortexSyncContractIds.SyncCapabilities,
            CortexSyncContractIds.Canonicalization,
            "server-v1",
            [
                CortexSyncContractIds.OfflineBundle,
                CortexSyncContractIds.SyncReceipt,
                CortexSyncContractIds.SyncCapabilities,
                CortexSyncContractIds.SyncGatewayHeadRequest,
                CortexSyncContractIds.SyncGatewayHead,
                CortexSyncContractIds.SyncGatewaySubmitRequest,
                CortexSyncContractIds.SyncGatewaySubmitResult
            ],
            [AgentTaskSnapshotContractIds.Snapshot],
            [CortexSyncCapabilityIds.DurableReceipts, CortexSyncCapabilityIds.EventHashChain, CortexSyncCapabilityIds.ServerReadBackReconciliation, CortexSyncCapabilityIds.CurrentRecoveryMaterialization],
            [],
            [],
            [],
            ["exact-copy"],
            ["pegasus.cortex.test-proof/1.0"],
            new CortexSyncLimitsV1(
                maximumEventPayloadBytes,
                maximumEventsPerBundle,
                maximumCanonicalBundleBytes,
                16,
                16 * 1024,
                maximumReceiptOutcomes,
                4096,
                32),
            [],
            CortexSyncClientContractIds.EmptySha256));

    public static CortexSyncReceiptV1 Receipt(CortexOfflineBundleV1 bundle)
    {
        var at = DateTimeOffset.Parse("2026-08-02T12:00:01+00:00");
        var outcomes = bundle.Events.Select(item => new CortexSyncReceiptOutcomeV1(
            item.Sequence, item.EventId, item.EventHash, CortexSyncDispositionIds.Applied, true,
            "entity-1", "server-rev-1", null)).ToArray();
        var reconciliation = new CortexSyncReceiptReconciliationV1(
            "reconciled", outcomes.Length, outcomes.Length, at, bundle.BundleHash);
        var placeholder = Create(CortexSyncClientContractIds.EmptySha256, [1]);
        var receiptHash = CortexSyncContractPolicy.ComputeReceiptSemanticHash(placeholder);
        return Create(receiptHash, [2, 3, 4]);

        CortexSyncReceiptV1 Create(string hash, byte[] evidence) => new(
            1,
            CortexSyncContractIds.SyncReceipt,
            CortexSyncContractIds.Canonicalization,
            "receipt-1",
            "server-1",
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            bundle.BundleId,
            bundle.BundleHash,
            at,
            new CortexSyncReceiptAuthenticationV1(
                "pegasus.cortex.test-proof/1.0",
                "key-1",
                "test-proof",
                hash,
                Base64Url(evidence),
                evidence.Length,
                CortexCanonicalJson.Sha256(evidence)),
            new CortexSyncReceiptDurabilityV1("commit-1", "server-rev-1", at),
            outcomes,
            reconciliation,
            [],
            hash);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
