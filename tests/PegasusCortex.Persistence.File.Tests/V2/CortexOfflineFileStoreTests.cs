using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.File.V2;

namespace Pegasus.Cortex.Persistence.File.Tests.V2;

internal static class CortexOfflineFileStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T18:00:00Z");
    private const string DefaultTaskId = "019fe0aa-7e02-79f0-a6a0-b7dc365062e7";

    public static async Task ExactCandidateAndReceiptLifecycleAsync()
    {
        var root = CreateRoot();
        try
        {
            var firstClock = new FixedTimeProvider(Now);
            var store = await CreateStoreAsync(root, firstClock).ConfigureAwait(false);
            const string exact = "line one\r\n\tline two\nUnicode Ω 世界 👩🏽‍💻\rcontrols:\u0000\u0001\b\f end";
            var payload = Knowledge("knowledge.exact", exact, ["zeta", "alpha"]);
            var created = await store.CaptureAsync(payload).ConfigureAwait(false);
            Require(created.Status == CortexOfflineOperationStatus.Created &&
                created.Frame.Status == CortexOfflineFrameStatus.Pending,
                "The exact offline candidate was not created as pending.");

            await using var laterStore = await CortexOfflineFileStore.OpenAsync(
                root,
                DefaultTaskId,
                new FixedTimeProvider(Now.AddHours(3))).ConfigureAwait(false);
            var replay = await laterStore.CaptureAsync(payload with
            {
                CapturedAtUtc = Now.AddHours(3)
            }).ConfigureAwait(false);
            Require(replay.Status == CortexOfflineOperationStatus.Existing &&
                replay.Frame.FrameId == created.Frame.FrameId,
                "A moving outer capture clock changed logical candidate identity.");

            var read = await store.ReadPendingAsync(created.Frame.FrameId).ConfigureAwait(false);
            Require(read.Payload.Content == exact && read.Payload.Tags.SequenceEqual(["alpha", "zeta"]),
                "The candidate did not preserve exact content or canonicalize tag order.");
            var adaptation = await store.AdaptPendingAsync(created.Frame.FrameId).ConfigureAwait(false);
            Require(adaptation is { Status: CortexOfflineOperationStatus.Adapted, Candidate: not null } &&
                adaptation.Candidate.Command.Content == exact &&
                adaptation.Candidate.TaskId == DefaultTaskId &&
                adaptation.Candidate.Command.IdempotencyKey!.StartsWith("offline_", StringComparison.Ordinal) &&
                adaptation.Candidate.Command.IdempotencyKey.EndsWith(created.Frame.FrameId, StringComparison.Ordinal),
                "The direct current adapter changed content or produced unstable idempotency.");

            var candidate = adaptation.Candidate!;
            var central = CentralRecord(candidate.Command, revision: 1);
            await RequireThrowsAsync<InvalidOperationException>(
                () => Task.Run(() => CortexOfflineReceiptVerifier.Verify(
                    candidate,
                    central with
                    {
                        Provenance = central.Provenance with { SourceId = "different-source" }
                    },
                    "created",
                    Now.AddMinutes(1))),
                "Receipt verification ignored divergent central provenance.").ConfigureAwait(false);
            var receipt = CortexOfflineReceiptVerifier.Verify(
                candidate,
                central,
                "created",
                Now.AddMinutes(1));
            var replaced = await store.ReplaceWithReceiptAsync(created.Frame.FrameId, receipt)
                .ConfigureAwait(false);
            var replayReceipt = CortexOfflineReceiptVerifier.Verify(
                candidate,
                central,
                "idempotent",
                Now.AddMinutes(2));
            var idempotent = await store.ReplaceWithReceiptAsync(created.Frame.FrameId, replayReceipt)
                .ConfigureAwait(false);
            Require(replaced is { Status: CortexOfflineOperationStatus.Replaced, Frame.Status: CortexOfflineFrameStatus.Receipt } &&
                idempotent.Status == CortexOfflineOperationStatus.AlreadyReplaced,
                "Verified same-path receipt replacement was not atomic and idempotent.");
            var postReceiptReplay = await laterStore.CaptureAsync(payload with
            {
                CapturedAtUtc = Now.AddDays(1)
            }).ConfigureAwait(false);
            Require(postReceiptReplay is
                { Status: CortexOfflineOperationStatus.Existing, Frame.Status: CortexOfflineFrameStatus.Receipt },
                "A timestamp-only replay recreated a candidate after verified receipt replacement.");
            await RequireThrowsAsync<InvalidOperationException>(
                () => store.ReadPendingAsync(created.Frame.FrameId).AsTask(),
                "The receipt still exposed the ingested candidate body.").ConfigureAwait(false);
            Require((await store.GetPendingContextItemsAsync().ConfigureAwait(false)).Count == 0,
                "A receipt was retained as active pending context.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task BoundsConcurrencyAndHotProjectionAsync()
    {
        var root = CreateRoot();
        try
        {
            var store = await CreateStoreAsync(root, new FixedTimeProvider(Now)).ConfigureAwait(false);
            var maximum = new string('\u0001', CortexOfflineLimits.MaximumBodyCharacters);
            var maximumCapture = await store.CaptureAsync(Knowledge("knowledge.maximum", maximum))
                .ConfigureAwait(false);
            var maximumPath = ScopePath(root, DefaultTaskId, "frames", $"{maximumCapture.Frame.FrameId}.json");
            Require(new FileInfo(maximumPath).Length <= CortexOfflineLimits.MaximumFrameUtf8Bytes &&
                (await store.ReadPendingAsync(maximumCapture.Frame.FrameId).ConfigureAwait(false)).Payload.Content == maximum,
                "A maximum control-heavy candidate exceeded the frame bound or changed content.");

            await using var concurrent = await CortexOfflineFileStore.OpenAsync(
                root,
                DefaultTaskId,
                new FixedTimeProvider(Now.AddDays(1))).ConfigureAwait(false);
            var racePayload = Knowledge("knowledge.race", "race\r\n\tΩ\u0001");
            var race = await Task.WhenAll(
                store.CaptureAsync(racePayload).AsTask(),
                concurrent.CaptureAsync(racePayload).AsTask()).ConfigureAwait(false);
            Require(race.Count(value => value.Status == CortexOfflineOperationStatus.Created) == 1 &&
                race.Count(value => value.Status == CortexOfflineOperationStatus.Existing) == 1,
                "Concurrent logical capture was not serialized deterministically.");

            var pending = await store.GetPendingContextItemsAsync().ConfigureAwait(false);
            Require(pending.Any(value => value.RecordId == "knowledge.race" &&
                value.RecordType == "offline-knowledge-candidate"),
                "Pending excerpts were not exposed as bounded non-authoritative context.");
            var hotResult = await store.MergeHotAsync(new CortexOfflineHotRefreshRequest(
                "ctx-offline-test",
                ["project.test"],
                [],
                false)).ConfigureAwait(false);
            var hotBytes = await System.IO.File.ReadAllBytesAsync(ScopePath(root, DefaultTaskId, "HOT.json"))
                .ConfigureAwait(false);
            var hot = JsonSerializer.Deserialize<CortexOfflineHotV1>(hotBytes, CortexV2Json.Options);
            Require(hotResult.Utf8Bytes <= 4096 &&
                hot is { Generated: true, Authoritative: false, TaskId: DefaultTaskId } &&
                hot.Items.Any(value => value.RecordId == "knowledge.race"),
                "HOT merge did not include bounded pending context as a disposable projection.");

            var divergent = racePayload with { Content = "different" };
            await RequireThrowsAsync<InvalidOperationException>(
                () => store.CaptureAsync(divergent).AsTask(),
                "Divergent content reused a logical frame identity without conflict.").ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task HotMergeTruncationReportsHasMoreAsync()
    {
        var root = CreateRoot();
        try
        {
            var central = ContextItem(
                "central.merge",
                "central",
                new string('C', 128),
                new string('c', CortexV2Limits.MaximumReceiptExcerptCharacters));
            var pendingPayload = Knowledge(
                "pending.merge",
                new string('p', CortexV2Limits.MaximumReceiptExcerptCharacters));
            var pending = new CortexContextItemV2(
                pendingPayload.Namespace,
                "offline-knowledge-candidate",
                pendingPayload.RecordId,
                0,
                pendingPayload.Title,
                pendingPayload.Content,
                pendingPayload.Kind,
                pendingPayload.Tier,
                null,
                0);
            var finalCursor = "ctx-merge-final";
            var twoItemBytes = JsonSerializer.SerializeToUtf8Bytes(
                new CortexOfflineHotV1(
                    CortexOfflineSchemas.HotV1,
                    true,
                    false,
                    DefaultTaskId,
                    "offline-test-project",
                    finalCursor,
                    Now,
                    ["project.test"],
                    [central, pending],
                    true),
                CortexV2Json.Options).Length;
            Require(twoItemBytes is >= CortexFileV2Limits.MinimumHotUtf8Bytes and
                <= CortexFileV2Limits.MaximumHotUtf8Bytes,
                "The deterministic two-item HOT fixture is outside the supported byte bounds.");

            var store = await CreateStoreAsync(
                root,
                new FixedTimeProvider(Now),
                twoItemBytes).ConfigureAwait(false);
            var prior = ContextItem(
                "prior.merge",
                "knowledge",
                new string('R', 128),
                new string('r', CortexV2Limits.MaximumReceiptExcerptCharacters));
            var priorResult = await store.RefreshHotAsync(new CortexOfflineHotRefreshRequest(
                "ctx-merge-prior",
                ["project.test"],
                [prior],
                false)).ConfigureAwait(false);
            Require(priorResult is { ItemCount: 1, HasMore: false },
                "The prior HOT source did not fit before the merge fixture was assembled.");

            await store.CaptureAsync(pendingPayload).ConfigureAwait(false);
            Require((await store.GetPendingContextItemsAsync().ConfigureAwait(false))
                    .Any(value => value.RecordId == pending.RecordId),
                "The pending source was not present before the HOT merge.");

            var mergedResult = await store.MergeHotAsync(new CortexOfflineHotRefreshRequest(
                finalCursor,
                ["project.test"],
                [central],
                false)).ConfigureAwait(false);
            var mergedBytes = await System.IO.File.ReadAllBytesAsync(
                ScopePath(root, DefaultTaskId, "HOT.json")).ConfigureAwait(false);
            var merged = JsonSerializer.Deserialize<CortexOfflineHotV1>(mergedBytes, CortexV2Json.Options);
            Require(mergedResult is { ItemCount: 2, HasMore: true } &&
                merged is { HasMore: true } &&
                merged.Items.Select(value => value.RecordId).SequenceEqual(
                    new[] { central.RecordId, pending.RecordId },
                    StringComparer.Ordinal) &&
                !merged.Items.Any(value => value.RecordId == prior.RecordId) &&
                mergedBytes.Length <= twoItemBytes,
                "HOT merge did not report HasMore after trimming the prior projection from central + pending + prior candidates.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task CompatibilityAndUnknownPreservationAsync()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".cortex"));
            const string legacy = "{\"schema\":\"pegasus.cortex.file-project/1.0\",\"projectId\":\"legacy-project\",\"writeNamespace\":\"project.test\",\"readNamespaces\":[\"project.test\",\"shared.programming\"]}";
            var manifestPath = Path.Combine(root, ".cortex", "project.json");
            await System.IO.File.WriteAllTextAsync(manifestPath, legacy, new UTF8Encoding(false, true))
                .ConfigureAwait(false);
            var originalManifest = await System.IO.File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            await using var store = await CortexOfflineFileStore.InitializeAsync(
                    root,
                    DefaultTaskId,
                    new CortexFileProjectManifestV2(
                        CortexFileProjectManifestV2.CurrentSchema,
                        "legacy-project",
                        "project.test",
                        ["project.test", "shared.programming"]),
                    new FixedTimeProvider(Now))
                .ConfigureAwait(false);
            Require(store.Manifest.Schema == CortexFileProjectManifestV2.CurrentSchema &&
                store.Manifest.WriteNamespace == "project.test" &&
                (await System.IO.File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false)).AsSpan()
                    .SequenceEqual(originalManifest),
                "The direct legacy manifest reader did not adapt in memory while preserving source bytes.");

            var futurePath = Path.Combine(store.ScopeRoot, "frames", "future-frame.json");
            var futureBytes = Encoding.UTF8.GetBytes(
                "{\"opaque\":\"preserve exactly\",\"schema\":\"pegasus.cortex.offline-frame/2.0\"}");
            await System.IO.File.WriteAllBytesAsync(futurePath, futureBytes).ConfigureAwait(false);
            var inspected = await store.InspectAsync("future-frame").ConfigureAwait(false);
            Require(inspected.Frame.Status == CortexOfflineFrameStatus.UnsupportedOuterSchema &&
                (await System.IO.File.ReadAllBytesAsync(futurePath).ConfigureAwait(false)).AsSpan()
                    .SequenceEqual(futureBytes),
                "An unknown future outer frame was interpreted or changed instead of preserved.");

            var known = CortexOfflineFrameCodec.CreateKnowledgeCandidate(
                Knowledge("knowledge.future-payload", "opaque payload"),
                Now);
            var decoded = CortexOfflineFrameCodec.Decode(known);
            var futurePayloadFrame = Seal(decoded.Frame! with
            {
                PayloadSchema = "pegasus.cortex.offline.future-candidate/9.0"
            });
            var futurePayloadPath = Path.Combine(store.ScopeRoot, "frames",
                $"{futurePayloadFrame.FrameId}.json");
            var futurePayloadBytes = CortexCanonicalJson.Serialize(futurePayloadFrame);
            await System.IO.File.WriteAllBytesAsync(futurePayloadPath, futurePayloadBytes).ConfigureAwait(false);
            var unsupported = await store.AdaptPendingAsync(futurePayloadFrame.FrameId).ConfigureAwait(false);
            Require(unsupported.Status == CortexOfflineOperationStatus.UnsupportedPayload &&
                (await System.IO.File.ReadAllBytesAsync(futurePayloadPath).ConfigureAwait(false)).AsSpan()
                    .SequenceEqual(futurePayloadBytes),
                "An unknown future payload was converted or changed instead of preserved.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task TaskScopesIsolateRestartReceiptAndHotAsync()
    {
        var root = CreateRoot();
        var secondRoot = CreateRoot();
        const string taskA = "019fe0aa-7e02-79f0-a6a0-b7dc365062e7";
        const string taskB = "019fe0ab-10c4-7bd2-8602-4e9e84db72f1";
        try
        {
            await using var storeA = await CreateStoreAsync(
                root,
                new FixedTimeProvider(Now),
                taskId: taskA).ConfigureAwait(false);
            await using var storeB = await CreateStoreAsync(
                root,
                new FixedTimeProvider(Now.AddMinutes(1)),
                taskId: taskB).ConfigureAwait(false);
            Require(storeA.ScopeRoot != storeB.ScopeRoot &&
                storeA.TaskId == taskA && storeB.TaskId == taskB,
                "Two task identities shared an offline state root.");

            // The logical candidate ID may match across tasks, but its physical
            // frame, receipt, lock, and projection remain scope-local.
            var payload = Knowledge("knowledge.same", "same logical candidate");
            var captureA = await storeA.CaptureAsync(payload).ConfigureAwait(false);
            var captureB = await storeB.CaptureAsync(payload).ConfigureAwait(false);
            Require(captureA.Frame.FrameId == captureB.Frame.FrameId &&
                System.IO.File.Exists(Path.Combine(storeA.ScopeRoot, "frames", $"{captureA.Frame.FrameId}.json")) &&
                System.IO.File.Exists(Path.Combine(storeB.ScopeRoot, "frames", $"{captureB.Frame.FrameId}.json")),
                "Equal logical candidates were not retained independently by scope.");

            var onlyA = await storeA.CaptureAsync(Knowledge("knowledge.only-a", "private A"))
                .ConfigureAwait(false);
            await RequireThrowsAsync<FileNotFoundException>(
                () => storeB.ReadPendingAsync(onlyA.Frame.FrameId).AsTask(),
                "Task B read Task A's frame through the public API.").ConfigureAwait(false);
            await RequireThrowsAsync<FileNotFoundException>(
                () => storeB.AdaptPendingAsync(onlyA.Frame.FrameId).AsTask(),
                "Task B adapted Task A's frame for ingest through the public API.").ConfigureAwait(false);
            Require(!(await storeB.ListAsync(new CortexOfflineListRequest()).ConfigureAwait(false)).Items
                    .Any(item => item.RecordId == "knowledge.only-a") &&
                !(await storeB.SearchAsync(new CortexOfflineSearchRequest("private A")).ConfigureAwait(false)).Items.Any(),
                "Task B enumerated or searched Task A's pending state.");

            var adaptationA = await storeA.AdaptPendingAsync(onlyA.Frame.FrameId).ConfigureAwait(false);
            var centralA = CentralRecord(adaptationA.Candidate!.Command, 1);
            var receiptA = CortexOfflineReceiptVerifier.Verify(
                adaptationA.Candidate,
                centralA,
                "created",
                Now.AddMinutes(2));
            await RequireThrowsAsync<InvalidOperationException>(
                () => storeB.ReplaceWithReceiptAsync(captureB.Frame.FrameId, receiptA).AsTask(),
                "Task B accepted Task A's verified receipt capability.").ConfigureAwait(false);
            var replacedA = await storeA.ReplaceWithReceiptAsync(onlyA.Frame.FrameId, receiptA)
                .ConfigureAwait(false);
            var replayA = await storeA.ReplaceWithReceiptAsync(onlyA.Frame.FrameId, receiptA)
                .ConfigureAwait(false);
            Require(replacedA.Status == CortexOfflineOperationStatus.Replaced &&
                replayA.Status == CortexOfflineOperationStatus.AlreadyReplaced,
                "Task-local receipt replacement was not idempotent.");

            await storeA.MergeHotAsync(new CortexOfflineHotRefreshRequest(
                "ctx-task-a",
                ["project.test"],
                [],
                false)).ConfigureAwait(false);
            await storeB.MergeHotAsync(new CortexOfflineHotRefreshRequest(
                "ctx-task-b",
                ["project.test"],
                [],
                false)).ConfigureAwait(false);
            var hotA = JsonSerializer.Deserialize<CortexOfflineHotV1>(
                await System.IO.File.ReadAllBytesAsync(Path.Combine(storeA.ScopeRoot, "HOT.json"))
                    .ConfigureAwait(false),
                CortexV2Json.Options);
            var hotB = JsonSerializer.Deserialize<CortexOfflineHotV1>(
                await System.IO.File.ReadAllBytesAsync(Path.Combine(storeB.ScopeRoot, "HOT.json"))
                    .ConfigureAwait(false),
                CortexV2Json.Options);
            Require(hotA is { TaskId: taskA, ContextCursor: "ctx-task-a" } &&
                hotB is { TaskId: taskB, ContextCursor: "ctx-task-b" } &&
                !hotB.Items.Any(item => item.RecordId == "knowledge.only-a"),
                "Task-scoped HOT projections crossed task identities.");

            await using var reopenedA = await CortexOfflineFileStore.OpenAsync(
                root,
                taskA,
                new FixedTimeProvider(Now.AddDays(1))).ConfigureAwait(false);
            Require(reopenedA.ScopeRoot == storeA.ScopeRoot &&
                reopenedA.ScopeManifest.TaskId == taskA &&
                reopenedA.ScopeManifest.ProjectId == storeA.ScopeManifest.ProjectId &&
                reopenedA.ScopeManifest.WriteNamespace == storeA.ScopeManifest.WriteNamespace &&
                reopenedA.ScopeManifest.ReadNamespaces.SequenceEqual(
                    storeA.ScopeManifest.ReadNamespaces,
                    StringComparer.Ordinal) &&
                (await reopenedA.InspectAsync(onlyA.Frame.FrameId).ConfigureAwait(false)).Frame.Status ==
                    CortexOfflineFrameStatus.Receipt,
                "A stable restart did not reopen the exact same task binding and receipt.");

            await using var sameTaskOtherRoot = await CreateStoreAsync(
                secondRoot,
                new FixedTimeProvider(Now),
                taskId: taskA).ConfigureAwait(false);
            var otherRootCandidate = await sameTaskOtherRoot.CaptureAsync(
                Knowledge("knowledge.only-a", "private A")).ConfigureAwait(false);
            Require(otherRootCandidate.Frame.FrameId == onlyA.Frame.FrameId &&
                !string.Equals(sameTaskOtherRoot.ScopeRoot, storeA.ScopeRoot, StringComparison.OrdinalIgnoreCase),
                "The same task ID in another project root was confused with the first path scope.");
            await RequireThrowsAsync<InvalidOperationException>(
                () => sameTaskOtherRoot.ReplaceWithReceiptAsync(
                    otherRootCandidate.Frame.FrameId,
                    receiptA).AsTask(),
                "A verified receipt capability crossed project roots that reused the same task ID.")
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(secondRoot);
        }
    }

    public static async Task LegacyAmbiguityAndBindingMismatchFailClosedAsync()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".cortex", "offline", "frames"));
            var legacyFrame = Path.Combine(root, ".cortex", "offline", "frames", "legacy.json");
            await System.IO.File.WriteAllTextAsync(legacyFrame, "{}", new UTF8Encoding(false, true))
                .ConfigureAwait(false);
            var requested = TestManifest();
            await RequireThrowsMessageAsync<InvalidOperationException>(
                () => CortexOfflineFileStore.InitializeAsync(root, DefaultTaskId, requested).AsTask(),
                "explicit task-scope migration",
                "Legacy project-wide state was silently assigned to a task scope.").ConfigureAwait(false);
            Require(!Directory.Exists(Path.Combine(root, ".cortex", "offline", "scopes")) &&
                await System.IO.File.ReadAllTextAsync(legacyFrame).ConfigureAwait(false) == "{}",
                "Legacy conflict created scoped state or changed the legacy source.");
        }
        finally
        {
            DeleteRoot(root);
        }

        root = CreateRoot();
        try
        {
            await using var store = await CortexOfflineFileStore.InitializeAsync(
                root,
                DefaultTaskId,
                TestManifest()).ConfigureAwait(false);
            var manifestPath = Path.Combine(store.ScopeRoot, "manifest.json");
            var original = await System.IO.File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            await RequireThrowsAsync<InvalidOperationException>(
                () => CortexOfflineFileStore.InitializeAsync(
                    root,
                    DefaultTaskId,
                    TestManifest() with { WriteNamespace = "project.other", ReadNamespaces = ["project.other"] })
                    .AsTask(),
                "A task binding reopened with divergent namespace metadata.").ConfigureAwait(false);
            Require((await System.IO.File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false)).AsSpan()
                    .SequenceEqual(original),
                "A rejected binding mismatch changed the scoped manifest.");
            await RequireThrowsAsync<InvalidOperationException>(
                () => CortexOfflineFileStore.OpenAsync(
                    root,
                    "019fe0ac-35be-77e0-9e84-af5caf934862").AsTask(),
                "Opening an uninitialized task identity fell back to another scope.").ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    public static async Task ScopeIdentityPathAndManifestAdmissionFailClosedAsync()
    {
        var root = CreateRoot();
        try
        {
            await RequireThrowsMessageAsync<ArgumentException>(
                () => CortexOfflineFileStore.InitializeAsync(
                    root,
                    DefaultTaskId.ToUpperInvariant(),
                    TestManifest()).AsTask(),
                "canonical lowercase",
                "A non-canonical task identity was silently normalized or admitted.").ConfigureAwait(false);
            Require(!Directory.Exists(Path.Combine(root, ".cortex")),
                "Rejected task identity created partial project state.");

            var openings = await Task.WhenAll(
                CortexOfflineFileStore.InitializeAsync(root, DefaultTaskId, TestManifest()).AsTask(),
                CortexOfflineFileStore.InitializeAsync(root, DefaultTaskId, TestManifest()).AsTask())
                .ConfigureAwait(false);
            await using var first = openings[0];
            await using var second = openings[1];
            var manifestPath = Path.Combine(first.ScopeRoot, "manifest.json");
            var bytes = await System.IO.File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            var retained = JsonSerializer.Deserialize<CortexOfflineTaskManifestV1>(bytes, CortexV2Json.Options);
            Require(retained is { Schema: CortexOfflineTaskManifestV1.CurrentSchema, TaskId: DefaultTaskId } &&
                first.ScopeRoot == second.ScopeRoot &&
                !new DirectoryInfo(first.ScopeRoot).EnumerateFiles(".*.tmp", SearchOption.AllDirectories).Any(),
                "Concurrent task initialization left a partial, divergent, or temporary manifest.");

            var sharedPath = Path.Combine(root, ".cortex", "project.json");
            await System.IO.File.WriteAllTextAsync(
                sharedPath,
                JsonSerializer.Serialize(
                    TestManifest() with
                    {
                        ProjectId = "conflicting-project",
                        WriteNamespace = "project.other",
                        ReadNamespaces = ["project.other"]
                    },
                    CortexV2Json.Options),
                new UTF8Encoding(false, true)).ConfigureAwait(false);
            await RequireThrowsMessageAsync<InvalidOperationException>(
                () => CortexOfflineFileStore.OpenAsync(root, DefaultTaskId).AsTask(),
                "shared project namespace metadata conflicts",
                "A changed shared project manifest silently overrode or bypassed the task binding.")
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }

        var longRoot = CreateLongRoot();
        try
        {
            Require(longRoot.Length < 240, "The long-root fixture already exceeded the project-root bound.");
            await RequireThrowsAsync<PathTooLongException>(
                () => CortexOfflineFileStore.InitializeAsync(
                    longRoot,
                    DefaultTaskId,
                    TestManifest()).AsTask(),
                "A root that cannot contain the longest generated scoped path was admitted.")
                .ConfigureAwait(false);
            Require(!Directory.Exists(Path.Combine(longRoot, ".cortex")),
                "Derived path admission failure left partial empty Cortex directories.");
        }
        finally
        {
            DeleteRoot(longRoot);
        }
    }

    private static CortexOfflineKnowledgeCreatePayloadV1 Knowledge(
        string recordId,
        string content,
        IReadOnlyList<string>? tags = null) => new(
            CortexOfflineSchemas.KnowledgeCreateV1,
            "project.test",
            recordId,
            CortexKnowledgeKindV2.Correction,
            CortexKnowledgeTierV2.Explicit,
            $"Title {recordId}",
            content,
            tags ?? [],
            CortexProvenanceSourceV2.User,
            $"source.{recordId}",
            "Exact supplied evidence.",
            Now);

    private static CortexKnowledgeV2 CentralRecord(CortexKnowledgeCommandV2 command, long revision) => new(
        command.Namespace,
        command.RecordId!,
        revision,
        CortexKnowledgeStateV2.Active,
        command.Kind!.Value,
        command.Tier!.Value,
        command.Title!,
        command.Content!,
        command.Tags!,
        new CortexProvenanceV2(
            command.Provenance!.Source,
            "os-test",
            command.Provenance.SourceId,
            command.Provenance.Evidence,
            Now),
        null,
        Now,
        Now,
        null);

    private static CortexContextItemV2 ContextItem(
        string recordId,
        string recordType,
        string title,
        string excerpt) => new(
            "project.test",
            recordType,
            recordId,
            1,
            title,
            excerpt,
            null,
            null,
            null,
            1);

    private static CortexOfflineFrameV1 Seal(CortexOfflineFrameV1 frame)
    {
        var unsealed = frame with
        {
            FrameSha256 = new string('0', 64)
        };
        return unsealed with { FrameSha256 = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(unsealed)) };
    }

    private static async ValueTask<CortexOfflineFileStore> CreateStoreAsync(
        string root,
        TimeProvider timeProvider,
        int hotMaximumUtf8Bytes = CortexFileV2Limits.MaximumHotUtf8Bytes,
        string taskId = DefaultTaskId)
    {
        var manifest = TestManifest() with { HotMaximumUtf8Bytes = hotMaximumUtf8Bytes };
        return await CortexOfflineFileStore.InitializeAsync(root, taskId, manifest, timeProvider)
            .ConfigureAwait(false);
    }

    private static CortexFileProjectManifestV2 TestManifest() => new(
        CortexFileProjectManifestV2.CurrentSchema,
        "offline-test-project",
        "project.test",
        ["project.test"],
        CortexFileV2Limits.MaximumHotUtf8Bytes,
        16,
        32 * 1024);

    private static string ScopePath(string root, string taskId, params string[] relative)
    {
        var key = $"t-{CortexCanonicalJson.Sha256(taskId)[..32]}";
        return Path.Combine([root, ".cortex", "offline", "scopes", key, .. relative]);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pegasus-cortex-offline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateLongRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pegasus-cortex-offline-long-root",
            Guid.NewGuid().ToString("N"));
        while (root.Length < 180)
            root = Path.Combine(root, new string('p', Math.Min(40, Math.Max(1, 180 - root.Length - 1))));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action, string message)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsMessageAsync<T>(
        Func<Task> action,
        string expectedMessage,
        string failureMessage)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T exception) when (exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException(failureMessage);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
