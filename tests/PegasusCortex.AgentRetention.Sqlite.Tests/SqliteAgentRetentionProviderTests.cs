using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.AgentRetention.Sqlite.Tests;

internal static class SqliteAgentRetentionProviderTests
{
    public static async Task TaskSnapshotLifecycleAsync()
    {
        using var root = TemporarySqliteRoot.Create();
        var first = RetentionContractSamples.TaskSnapshot(
            revision: 1,
            previousHash: null,
            idempotencyKey: "task-idempotency-1",
            snapshotId: "snapshot-1",
            contextSetId: RetentionContractSamples.ContextSetOne,
            reportedState: AgentTaskStateIds.Active,
            currentState: "The initial task revision is active.");
        var firstBytes = CortexCanonicalJson.Serialize(first);
        AgentTaskSnapshotV1 second;
        byte[] secondBytes;
        string firstFullDocumentHash;
        string secondFullDocumentHash;

        await using (var store = CreateStore(root.DatabasePath))
        {
            var service = new CortexAgentRetentionApplicationService(store);
            var firstResult = await service.SubmitTaskSnapshotAsync(
                first,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                firstResult.Disposition);
            RequireExactDocument(firstResult.Document, firstBytes);
            firstFullDocumentHash = firstResult.Document!.ContentHash;
            ProviderAssert.Equal(
                CortexCanonicalJson.Sha256(firstBytes),
                firstFullDocumentHash);
            ProviderAssert.NotEqual(first.SnapshotHash, firstFullDocumentHash);
            var contentHashBypass = RetentionContractSamples.TaskSnapshot(
                revision: 2,
                previousHash: firstFullDocumentHash,
                idempotencyKey: "task-content-hash-bypass",
                snapshotId: "snapshot-content-hash-bypass",
                contextSetId: RetentionContractSamples.ContextSetTwo,
                reportedState: AgentTaskStateIds.Done,
                currentState: "A repository hash was supplied as a typed predecessor.");
            await ProviderAssert.ThrowsAsync<ArgumentException>(() =>
                service.SubmitTaskSnapshotAsync(
                    contentHashBypass,
                    CancellationToken.None).AsTask()).ConfigureAwait(false);

            var replay = await service.SubmitTaskSnapshotAsync(
                first,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Idempotent,
                replay.Disposition);
            RequireExactDocument(replay.Document, firstBytes);

            var sameKeyDifferentContent = RetentionContractSamples.TaskSnapshot(
                revision: 1,
                previousHash: null,
                idempotencyKey: first.IdempotencyKey,
                snapshotId: first.SnapshotId,
                contextSetId: first.ContextSetId,
                reportedState: AgentTaskStateIds.Active,
                currentState: "The same key now identifies different canonical content.");
            var conflict = await service.SubmitTaskSnapshotAsync(
                sameKeyDifferentContent,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Conflict,
                conflict.Disposition);
            ProviderAssert.Equal(firstFullDocumentHash, conflict.CurrentHeadHash);

            second = RetentionContractSamples.TaskSnapshot(
                revision: 2,
                previousHash: first.SnapshotHash,
                idempotencyKey: "task-idempotency-2",
                snapshotId: "snapshot-2",
                contextSetId: RetentionContractSamples.ContextSetTwo,
                reportedState: AgentTaskStateIds.Done,
                currentState: "The accepted task revision is complete.");
            ProviderAssert.True(
                AgentTaskSnapshotPolicy.ValidateRevision(first, second).Valid,
                "The typed task continuation was not valid before storage.");
            secondBytes = CortexCanonicalJson.Serialize(second);
            var secondResult = await service.SubmitTaskSnapshotAsync(
                second,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                secondResult.Disposition);
            RequireExactDocument(secondResult.Document, secondBytes);
            secondFullDocumentHash = secondResult.Document!.ContentHash;

            // Repository CAS uses the prior full-document hash, while the typed
            // canonical payload keeps its placeholder-based predecessor hash.
            ProviderAssert.Equal(
                firstFullDocumentHash,
                secondResult.Document.PreviousHash);
            ProviderAssert.NotEqual(first.SnapshotHash, secondResult.Document.PreviousHash);
            var storedSecond = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                secondResult.Document.CanonicalUtf8);
            ProviderAssert.Equal(first.SnapshotHash, storedSecond.PreviousSnapshotHash);
            ProviderAssert.Equal(second.SnapshotHash, storedSecond.SnapshotHash);

            var competingSecond = RetentionContractSamples.TaskSnapshot(
                revision: 2,
                previousHash: first.SnapshotHash,
                idempotencyKey: "task-idempotency-2-competing",
                snapshotId: "snapshot-2-competing",
                contextSetId: RetentionContractSamples.ContextSetThree,
                reportedState: AgentTaskStateIds.Blocked,
                currentState: "A competing producer view arrived after revision two.");
            var stale = await service.SubmitTaskSnapshotAsync(
                competingSecond,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.StaleBase,
                stale.Disposition);
            ProviderAssert.Equal(secondFullDocumentHash, stale.CurrentHeadHash);
        }

        await using var restarted = CreateStore(root.DatabasePath);
        var restartedFirst = await restarted.FindRevisionAsync(
            first.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            first.TaskSetId,
            revision: 1,
            CancellationToken.None).ConfigureAwait(false);
        var restartedSecond = await restarted.FindRevisionAsync(
            second.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            second.TaskSetId,
            revision: 2,
            CancellationToken.None).ConfigureAwait(false);
        RequireExactDocument(restartedFirst, firstBytes);
        RequireExactDocument(restartedSecond, secondBytes);
        ProviderAssert.Equal(firstFullDocumentHash, restartedSecond!.PreviousHash);

        var recoveredFirst = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            restartedFirst!.CanonicalUtf8);
        var recoveredSecond = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            restartedSecond.CanonicalUtf8);
        ProviderAssert.True(
            AgentTaskSnapshotPolicy.HasValidHash(recoveredFirst) &&
            AgentTaskSnapshotPolicy.HasValidHash(recoveredSecond) &&
            AgentTaskSnapshotPolicy.ValidateRevision(recoveredFirst, recoveredSecond).Valid,
            "Restart changed the typed task self-hash chain.");

        var restartedService = new CortexAgentRetentionApplicationService(restarted);
        var restartedReplay = await restartedService.SubmitTaskSnapshotAsync(
            second,
            CancellationToken.None).ConfigureAwait(false);
        ProviderAssert.Equal(
            CortexRetentionWriteDispositions.Idempotent,
            restartedReplay.Disposition);
        var integrity = await restarted.CheckIntegrityAsync(CancellationToken.None)
            .ConfigureAwait(false);
        ProviderAssert.True(
            integrity.Healthy &&
            integrity.RetainedDocuments == 2 &&
            integrity.AuditEvents == 2,
            "Restarted task retention counts or integrity were incorrect.");
    }

    public static async Task WorkPacketLifecycleAsync()
    {
        using var root = TemporarySqliteRoot.Create();
        var start = RetentionContractSamples.WorkPacket(
            revision: 1,
            stage: AgentWorkPacketStageIds.Start,
            previousHash: null,
            idempotencyKey: "work-idempotency-1",
            contextSetId: RetentionContractSamples.ContextSetOne);
        var startBytes = CortexCanonicalJson.Serialize(start);
        var continuation = RetentionContractSamples.WorkPacket(
            revision: 2,
            stage: AgentWorkPacketStageIds.Continue,
            previousHash: start.PacketHash,
            idempotencyKey: "work-idempotency-2",
            contextSetId: RetentionContractSamples.ContextSetTwo);
        var continuationBytes = CortexCanonicalJson.Serialize(continuation);
        var closeout = RetentionContractSamples.WorkPacket(
            revision: 3,
            stage: AgentWorkPacketStageIds.Closeout,
            previousHash: continuation.PacketHash,
            idempotencyKey: "work-idempotency-3",
            contextSetId: RetentionContractSamples.ContextSetThree);
        var closeoutBytes = CortexCanonicalJson.Serialize(closeout);
        string startFullDocumentHash;
        string continuationFullDocumentHash;

        ProviderAssert.True(
            AgentWorkPacketPolicy.ValidateContinuation(start, continuation).Valid &&
            AgentWorkPacketPolicy.ValidateContinuation(continuation, closeout).Valid,
            "The typed work-packet chain was invalid before storage.");

        await using (var store = CreateStore(root.DatabasePath))
        {
            var service = new CortexAgentRetentionApplicationService(store);
            var startResult = await service.SubmitWorkPacketAsync(
                start,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                startResult.Disposition);
            RequireExactDocument(startResult.Document, startBytes);
            startFullDocumentHash = startResult.Document!.ContentHash;
            ProviderAssert.NotEqual(start.PacketHash, startFullDocumentHash);
            var contentHashBypass = RetentionContractSamples.WorkPacket(
                revision: 2,
                stage: AgentWorkPacketStageIds.Continue,
                previousHash: startFullDocumentHash,
                idempotencyKey: "work-content-hash-bypass",
                contextSetId: RetentionContractSamples.ContextSetTwo);
            await ProviderAssert.ThrowsAsync<ArgumentException>(() =>
                service.SubmitWorkPacketAsync(
                    contentHashBypass,
                    CancellationToken.None).AsTask()).ConfigureAwait(false);

            var continuationResult = await service.SubmitWorkPacketAsync(
                continuation,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                continuationResult.Disposition);
            RequireExactDocument(continuationResult.Document, continuationBytes);
            continuationFullDocumentHash = continuationResult.Document!.ContentHash;
            ProviderAssert.Equal(
                startFullDocumentHash,
                continuationResult.Document.PreviousHash);
            var storedContinuation = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                continuationResult.Document.CanonicalUtf8);
            ProviderAssert.Equal(start.PacketHash, storedContinuation.PreviousPacketHash);
            ProviderAssert.Equal(continuation.PacketHash, storedContinuation.PacketHash);

            var closeoutResult = await service.SubmitWorkPacketAsync(
                closeout,
                CancellationToken.None).ConfigureAwait(false);
            ProviderAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                closeoutResult.Disposition);
            RequireExactDocument(closeoutResult.Document, closeoutBytes);
            ProviderAssert.Equal(
                continuationFullDocumentHash,
                closeoutResult.Document!.PreviousHash);
            var storedCloseout = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                closeoutResult.Document.CanonicalUtf8);
            ProviderAssert.Equal(
                continuation.PacketHash,
                storedCloseout.PreviousPacketHash);
            ProviderAssert.Equal(
                AgentWorkCloseoutDispositionIds.Solved,
                storedCloseout.Closeout!.Disposition);
            var reopen = RetentionContractSamples.WorkPacket(
                revision: 4,
                stage: AgentWorkPacketStageIds.Continue,
                previousHash: closeout.PacketHash,
                idempotencyKey: "work-reopen-after-closeout",
                contextSetId: RetentionContractSamples.ContextSetThree);
            await ProviderAssert.ThrowsAsync<ArgumentException>(() =>
                service.SubmitWorkPacketAsync(
                    reopen,
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
        }

        await using var restarted = CreateStore(root.DatabasePath);
        var retainedStart = await FindWorkRevisionAsync(restarted, 1).ConfigureAwait(false);
        var retainedContinuation = await FindWorkRevisionAsync(restarted, 2).ConfigureAwait(false);
        var retainedCloseout = await FindWorkRevisionAsync(restarted, 3).ConfigureAwait(false);
        RequireExactDocument(retainedStart, startBytes);
        RequireExactDocument(retainedContinuation, continuationBytes);
        RequireExactDocument(retainedCloseout, closeoutBytes);

        var recoveredStart = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            retainedStart!.CanonicalUtf8);
        var recoveredContinuation = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            retainedContinuation!.CanonicalUtf8);
        var recoveredCloseout = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            retainedCloseout!.CanonicalUtf8);
        ProviderAssert.True(
            AgentWorkPacketPolicy.HasValidHash(recoveredStart) &&
            AgentWorkPacketPolicy.HasValidHash(recoveredContinuation) &&
            AgentWorkPacketPolicy.HasValidHash(recoveredCloseout) &&
            AgentWorkPacketPolicy.ValidateContinuation(
                recoveredStart,
                recoveredContinuation).Valid &&
            AgentWorkPacketPolicy.ValidateContinuation(
                recoveredContinuation,
                recoveredCloseout).Valid,
            "Restart changed the typed work-packet self-hash chain.");

        var restartedService = new CortexAgentRetentionApplicationService(restarted);
        var replay = await restartedService.SubmitWorkPacketAsync(
            closeout,
            CancellationToken.None).ConfigureAwait(false);
        ProviderAssert.Equal(CortexRetentionWriteDispositions.Idempotent, replay.Disposition);
        var integrity = await restarted.CheckIntegrityAsync(CancellationToken.None)
            .ConfigureAwait(false);
        ProviderAssert.True(
            integrity.Healthy &&
            integrity.RetainedDocuments == 3 &&
            integrity.AuditEvents == 3,
            "Restarted work-packet counts or integrity were incorrect.");
    }

    public static async Task AggregateSizeBoundsAsync()
    {
        ProviderAssert.Equal(
            32 * 1024,
            AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes);
        ProviderAssert.Equal(
            8 * 1024,
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes);
        ProviderAssert.Equal(
            32 * 1024,
            CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes);

        var oversizedTask = ProviderAssert.Throws<ArgumentException>(
            RetentionContractSamples.OversizedTaskSnapshot);
        ProviderAssert.Contains(
            "canonical task snapshot exceeds 32 KiB",
            oversizedTask.Message);
        var oversizedWork = ProviderAssert.Throws<ArgumentException>(
            RetentionContractSamples.OversizedWorkPacket);
        ProviderAssert.Contains(
            "canonical work packet exceeds eight KiB",
            oversizedWork.Message);

        using var root = TemporarySqliteRoot.Create();
        await using var store = CreateStore(root.DatabasePath);
        var integrity = await store.CheckIntegrityAsync(CancellationToken.None)
            .ConfigureAwait(false);
        ProviderAssert.True(
            integrity.Healthy &&
            integrity.RetainedDocuments == 0 &&
            integrity.AuditEvents == 0,
            "Aggregate-boundary rejection unexpectedly retained a document.");

        var taskBytes = CortexCanonicalJson.Serialize(
            RetentionContractSamples.TaskSnapshot(
                1,
                null,
                "bounded-task-idempotency",
                "bounded-snapshot",
                RetentionContractSamples.ContextSetOne,
                AgentTaskStateIds.Active,
                "A bounded task remains below its typed ceiling."));
        var workBytes = CortexCanonicalJson.Serialize(
            RetentionContractSamples.WorkPacket(
                1,
                AgentWorkPacketStageIds.Start,
                null,
                "bounded-work-idempotency",
                RetentionContractSamples.ContextSetOne));
        ProviderAssert.True(
            taskBytes.Length <= AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes &&
            workBytes.Length <= AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            "A valid baseline exceeded its typed canonical ceiling.");
    }

    private static SqliteCortexRetentionStore CreateStore(string databasePath) =>
        new(new SqliteCortexStoreOptions(databasePath));

    private static ValueTask<CortexRetainedDocument?> FindWorkRevisionAsync(
        SqliteCortexRetentionStore store,
        long revision) =>
        store.FindRevisionAsync(
            RetentionContractSamples.ProjectId,
            CortexRetainedDocumentKinds.WorkPacket,
            RetentionContractSamples.WorkId,
            revision,
            CancellationToken.None);

    private static void RequireExactDocument(
        CortexRetainedDocument? document,
        byte[] expectedCanonicalUtf8)
    {
        ProviderAssert.True(document is not null, "The retained document was missing.");
        ProviderAssert.SequenceEqual(expectedCanonicalUtf8, document!.CanonicalUtf8);
        ProviderAssert.Equal(
            CortexCanonicalJson.Sha256(expectedCanonicalUtf8),
            document.ContentHash);
    }
}
