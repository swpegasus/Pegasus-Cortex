using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Retention;

internal static class AgentRetentionApplicationTests
{
    public static async Task TaskSnapshotMappingAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);
        var snapshot = RetentionContractSamples.TaskSnapshot();

        var result = await service.SubmitTaskSnapshotAsync(snapshot, CancellationToken.None);

        Assert.Equal(CortexRetentionWriteDispositions.Appended, result.Disposition);
        var request = repository.LastAppendRequest!;
        var expectedBytes = CortexCanonicalJson.Serialize(snapshot);
        Assert.Equal(snapshot.ProjectId, request.ProjectId);
        Assert.Equal(CortexRetainedDocumentKinds.TaskSnapshot, request.Kind);
        Assert.Equal(snapshot.TaskSetId, request.DocumentId);
        Assert.Equal(snapshot.SnapshotRevision, request.Revision);
        Assert.Equal(snapshot.IdempotencyKey, request.IdempotencyKey);
        Assert.Equal(snapshot.ContractId, request.ContractId);
        Assert.Equal(snapshot.CapturedAt, request.CapturedAt);
        Assert.Equal(snapshot.ExpiresAt, request.ExpiresAt);
        Assert.Equal(ExpectedProducerId(snapshot.Producer), request.ProducerId);
        Assert.SequenceEqual(expectedBytes, request.CanonicalUtf8);
        Assert.Equal(CortexCanonicalJson.Sha256(expectedBytes), request.ContentHash);
        Assert.Contains("passive untrusted reference data", request.SearchText);
        Assert.Contains("Inspect passive task state.", request.SearchText);
        Assert.DoesNotContain("evidence-private", request.SearchText);

        var invalid = RetentionContractSamples.TaskSnapshot(
            idempotencyKey: "task-invalid-hash",
            seal: false);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitTaskSnapshotAsync(invalid, CancellationToken.None).AsTask());
        Assert.Equal(1, repository.AppendCalls);
    }

    public static async Task TaskRevisionTranslationAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);
        var first = RetentionContractSamples.TaskSnapshot();
        var firstResult = await service.SubmitTaskSnapshotAsync(first, CancellationToken.None);
        var second = RetentionContractSamples.TaskSnapshot(
            revision: 2,
            previousHash: first.SnapshotHash);
        var secondResult = await service.SubmitTaskSnapshotAsync(second, CancellationToken.None);

        Assert.Equal(CortexRetentionWriteDispositions.Appended, secondResult.Disposition);
        Assert.Equal(firstResult.Document!.ContentHash, secondResult.Document!.PreviousHash);
        Assert.NotEqual(first.SnapshotHash, secondResult.Document.PreviousHash);
        var contentHashBypass = RetentionContractSamples.TaskSnapshot(
            revision: 2,
            previousHash: firstResult.Document.ContentHash,
            idempotencyKey: "task-content-hash-bypass");
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitTaskSnapshotAsync(
                contentHashBypass,
                CancellationToken.None).AsTask());
        Assert.Equal(2, repository.AppendCalls);

        var third = RetentionContractSamples.TaskSnapshot(
            revision: 3,
            previousHash: second.SnapshotHash);
        var thirdResult = await service.SubmitTaskSnapshotAsync(third, CancellationToken.None);
        Assert.Equal(CortexRetentionWriteDispositions.Appended, thirdResult.Disposition);

        // A replay of a non-head revision still resolves its exact predecessor,
        // then lets repository idempotency win before head comparison.
        var replay = await service.SubmitTaskSnapshotAsync(second, CancellationToken.None);
        Assert.Equal(CortexRetentionWriteDispositions.Idempotent, replay.Disposition);

        var wrongPrevious = RetentionContractSamples.TaskSnapshot(
            revision: 4,
            previousHash: new string('b', 64),
            idempotencyKey: "task-idempotency-wrong-base");
        await Assert.ThrowsAsync<CortexRetentionRevisionRejectedException>(
            () => service.SubmitTaskSnapshotAsync(
                wrongPrevious,
                CancellationToken.None).AsTask());
        Assert.Equal(4, repository.AppendCalls);
    }

    public static async Task WorkPacketRetentionAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);
        var first = RetentionContractSamples.WorkPacket(
            cancellationRequested: true);
        var firstResult = await service.SubmitWorkPacketAsync(first, CancellationToken.None);

        Assert.Equal(CortexRetentionWriteDispositions.Appended, firstResult.Disposition);
        var firstRequest = repository.LastAppendRequest!;
        Assert.Equal(CortexRetainedDocumentKinds.WorkPacket, firstRequest.Kind);
        Assert.Equal(first.WorkId, firstRequest.DocumentId);
        Assert.Equal(first.IssuedAt, firstRequest.CapturedAt);
        Assert.Equal(ExpectedProducerId(first.Owner), firstRequest.ProducerId);
        Assert.SequenceEqual(CortexCanonicalJson.Serialize(first), firstRequest.CanonicalUtf8);
        Assert.Contains("passive untrusted reference data", firstRequest.SearchText);
        Assert.Contains(first.Objective, firstRequest.SearchText);
        Assert.DoesNotContain("src/private-command.ps1", firstRequest.SearchText);
        Assert.DoesNotContain("evidence-private", firstRequest.SearchText);
        Assert.True(first.CancellationRequested);
        Assert.Equal(AgentWorkFreshnessStateIds.Stale, first.Freshness.State);

        var second = RetentionContractSamples.WorkPacket(
            revision: 2,
            previousHash: first.PacketHash,
            cancellationRequested: true);
        var secondResult = await service.SubmitWorkPacketAsync(second, CancellationToken.None);
        Assert.Equal(CortexRetentionWriteDispositions.Appended, secondResult.Disposition);
        Assert.Equal(firstResult.Document!.ContentHash, secondResult.Document!.PreviousHash);
        var contentHashBypass = RetentionContractSamples.WorkPacket(
            revision: 2,
            previousHash: firstResult.Document.ContentHash,
            idempotencyKey: "work-content-hash-bypass");
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitWorkPacketAsync(
                contentHashBypass,
                CancellationToken.None).AsTask());

        var invalid = RetentionContractSamples.WorkPacket(
            idempotencyKey: "work-invalid-hash",
            seal: false);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitWorkPacketAsync(invalid, CancellationToken.None).AsTask());
        Assert.Equal(2, repository.AppendCalls);
    }

    public static async Task RepositoryResultPreservationAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var expected = new CortexRetentionAppendResult(
            CortexRetentionWriteDispositions.Conflict,
            Document: null,
            CurrentHeadHash: new string('c', 64));
        repository.ForcedAppendResult = expected;
        var service = new CortexAgentRetentionApplicationService(repository);

        var actual = await service.SubmitTaskSnapshotAsync(
            RetentionContractSamples.TaskSnapshot(),
            CancellationToken.None);

        Assert.Same(expected, actual);
    }

    public static async Task CancellationAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SubmitTaskSnapshotAsync(
                RetentionContractSamples.TaskSnapshot(),
                cancellation.Token).AsTask());

        Assert.Equal(0, repository.AppendCalls);
        Assert.Equal(0, repository.FindRevisionCalls);
    }

    public static async Task CorruptPredecessorAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);
        var first = RetentionContractSamples.TaskSnapshot();
        await service.SubmitTaskSnapshotAsync(first, CancellationToken.None);
        repository.CorruptCanonicalBytes(
            first.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            first.TaskSetId,
            first.SnapshotRevision);
        var second = RetentionContractSamples.TaskSnapshot(
            revision: 2,
            previousHash: first.SnapshotHash);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.SubmitTaskSnapshotAsync(second, CancellationToken.None).AsTask());
        Assert.Equal(1, repository.AppendCalls);
    }

    public static async Task ContentScreeningAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);

        var nestedTaskSecret = RetentionContractSamples.TaskSnapshot(
            idempotencyKey: "task-secret-state",
            tasks:
            [
                RetentionContractSamples.Task(
                    currentState: "Blocked because password=correct-horse-battery-staple.",
                    blockers: ["Raw terminal output: captured during diagnosis."])
            ]);
        var taskException = await CaptureRejectedAsync(
            () => service.SubmitTaskSnapshotAsync(
                nestedTaskSecret,
                CancellationToken.None).AsTask());
        Assert.True(taskException.FindingCodes.Contains("credential_assignment"));
        Assert.True(taskException.FindingCodes.Contains("raw_private_reasoning"));

        var packetSecret = RetentionContractSamples.WorkPacket(
            constraints: ["Use Bearer abcdefghijklmnopqrstuvwxyz123456 only for this run."]);
        var packetException = await CaptureRejectedAsync(
            () => service.SubmitWorkPacketAsync(
                packetSecret,
                CancellationToken.None).AsTask());
        Assert.True(packetException.FindingCodes.Contains("bearer_token"));

        var lessonSecret = RetentionContractSamples.WorkPacket(
            revision: 2,
            previousHash: new string('a', 64),
            idempotencyKey: "work-secret-lesson",
            candidateReusableLesson: "Private reasoning: preserve the hidden chain.");
        var lessonException = await CaptureRejectedAsync(
            () => service.SubmitWorkPacketAsync(
                lessonSecret,
                CancellationToken.None).AsTask());
        Assert.True(lessonException.FindingCodes.Contains("raw_private_reasoning"));
        Assert.Equal(0, repository.AppendCalls);
        Assert.Equal(0, repository.FindRevisionCalls);

        var clean = RetentionContractSamples.WorkPacket(
            idempotencyKey: "work-token-usage-clean",
            constraints:
            [
                "Token Usage reports nine billion cached tokens without credential material."
            ]);
        var cleanResult = await service.SubmitWorkPacketAsync(clean, CancellationToken.None);
        Assert.Equal(CortexRetentionWriteDispositions.Appended, cleanResult.Disposition);
        Assert.Equal(1, repository.AppendCalls);
    }

    public static async Task SearchProjectionBoundsAsync()
    {
        var repository = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(repository);

        await service.SubmitTaskSnapshotAsync(
            RetentionContractSamples.LargeTaskSnapshot(),
            CancellationToken.None);

        var searchText = repository.LastAppendRequest!.SearchText;
        Assert.Equal(CortexAgentRetentionLimits.MaximumSearchTextCharacters, searchText.Length);
        Assert.False(char.IsHighSurrogate(searchText[^1]));
        Assert.Contains("passive untrusted reference data", searchText);
        Assert.DoesNotContain("evidence-private", searchText);
    }

    private static string ExpectedProducerId(AgentWorkOwnerV1 owner) =>
        $"agent:{CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(owner))}";

    private static async Task<CortexRetentionContentRejectedException> CaptureRejectedAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (CortexRetentionContentRejectedException exception)
        {
            Assert.DoesNotContain("correct-horse", exception.Message);
            Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", exception.Message);
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected {nameof(CortexRetentionContentRejectedException)}.");
    }
}
