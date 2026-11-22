using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Applies the typed agent-coordination contracts to the passive retention port.
/// This service validates and records assertions; it does not schedule, claim,
/// approve, hand off, or execute any described work.
/// </summary>
public sealed class CortexAgentRetentionApplicationService
{
    private readonly ICortexAgentRetentionRepository _repository;

    public CortexAgentRetentionApplicationService(ICortexAgentRetentionRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Retains one complete task-snapshot revision using its task-set identity as
    /// the repository document chain.
    /// </summary>
    public async ValueTask<CortexRetentionAppendResult> SubmitTaskSnapshotAsync(
        AgentTaskSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot))
        {
            throw new ArgumentException(
                "The task snapshot does not have a valid bounded self-hash.",
                nameof(snapshot));
        }

        var canonicalUtf8 = CortexCanonicalJson.Serialize(snapshot);
        RequireCanonicalSize(
            canonicalUtf8,
            AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes,
            nameof(snapshot));
        RequireSafeRetentionContent(canonicalUtf8);
        var expectedPreviousHash = await ResolveExpectedPreviousHashAsync(
            snapshot.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            snapshot.TaskSetId,
            snapshot.SnapshotRevision,
            snapshot.PreviousSnapshotHash,
            snapshot,
            AgentTaskSnapshotPolicy.HasValidHash,
            static (previous, current) =>
                AgentTaskSnapshotPolicy.ValidateRevision(previous, current).Valid,
            static value => new RetainedContractIdentity(
                value.ProjectId,
                value.TaskSetId,
                value.SnapshotRevision,
                value.IdempotencyKey,
                ProducerId(value.Producer),
                value.ContractId,
                value.CapturedAt,
                value.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        var request = new CortexRetentionAppendRequest(
            snapshot.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            snapshot.TaskSetId,
            snapshot.SnapshotRevision,
            expectedPreviousHash,
            snapshot.IdempotencyKey,
            ProducerId(snapshot.Producer),
            snapshot.ContractId,
            canonicalUtf8,
            CortexCanonicalJson.Sha256(canonicalUtf8),
            CortexAgentRetentionSearchProjection.Create(snapshot),
            snapshot.CapturedAt,
            snapshot.ExpiresAt).Validate();

        // Return the provider's result verbatim so conflict and stale-head
        // semantics remain owned by the repository implementation.
        return await _repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retains one work-packet revision as passive evidence. Expired, stale, or
    /// cancellation-requested packets remain retainable because those states
    /// prevent handoff/execution, not historical retention.
    /// </summary>
    public async ValueTask<CortexRetentionAppendResult> SubmitWorkPacketAsync(
        AgentWorkPacketV1 packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentWorkPacketPolicy.HasValidHash(packet))
        {
            throw new ArgumentException(
                "The work packet does not have a valid bounded self-hash.",
                nameof(packet));
        }

        var canonicalUtf8 = CortexCanonicalJson.Serialize(packet);
        RequireCanonicalSize(
            canonicalUtf8,
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            nameof(packet));
        RequireSafeRetentionContent(canonicalUtf8);
        var expectedPreviousHash = await ResolveExpectedPreviousHashAsync(
            packet.ProjectId,
            CortexRetainedDocumentKinds.WorkPacket,
            packet.WorkId,
            packet.PacketRevision,
            packet.PreviousPacketHash,
            packet,
            AgentWorkPacketPolicy.HasValidHash,
            static (previous, current) =>
                AgentWorkPacketPolicy.ValidateContinuation(previous, current).Valid,
            static value => new RetainedContractIdentity(
                value.ProjectId,
                value.WorkId,
                value.PacketRevision,
                value.IdempotencyKey,
                ProducerId(value.Owner),
                value.ContractId,
                value.IssuedAt,
                value.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        var request = new CortexRetentionAppendRequest(
            packet.ProjectId,
            CortexRetainedDocumentKinds.WorkPacket,
            packet.WorkId,
            packet.PacketRevision,
            expectedPreviousHash,
            packet.IdempotencyKey,
            ProducerId(packet.Owner),
            packet.ContractId,
            canonicalUtf8,
            CortexCanonicalJson.Sha256(canonicalUtf8),
            CortexAgentRetentionSearchProjection.Create(packet),
            packet.IssuedAt,
            packet.ExpiresAt).Validate();

        return await _repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retains passive proof that exact previously retained packet bytes were
    /// offered to an intended owner. The receipt creates no assignment, claim,
    /// approval, scheduling, or execution authority.
    /// </summary>
    public async ValueTask<CortexRetentionAppendResult>
        SubmitWorkPacketHandoffReceiptAsync(
            string projectId,
            AgentWorkPacketHandoffReceiptV1 receipt,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentWorkPacketPolicy.HasValidHash(receipt))
        {
            throw new ArgumentException(
                "The handoff receipt does not have a valid bounded self-hash.",
                nameof(receipt));
        }

        var canonicalUtf8 = CortexCanonicalJson.Serialize(receipt);
        RequireCanonicalSize(
            canonicalUtf8,
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            nameof(receipt));
        RequireSafeRetentionContent(canonicalUtf8);
        var retainedPacket = await _repository.FindRevisionAsync(
            projectId,
            CortexRetainedDocumentKinds.WorkPacket,
            receipt.WorkId,
            receipt.PacketRevision,
            cancellationToken).ConfigureAwait(false);
        if (retainedPacket is null)
            throw new CortexRetentionRevisionRejectedException();
        var packet = ReadVerifiedPrevious<AgentWorkPacketV1>(
            retainedPacket,
            CortexRetainedDocumentKinds.WorkPacket,
            AgentWorkPacketPolicy.HasValidHash,
            static value => new RetainedContractIdentity(
                value.ProjectId,
                value.WorkId,
                value.PacketRevision,
                value.IdempotencyKey,
                ProducerId(value.Owner),
                value.ContractId,
                value.IssuedAt,
                value.ExpiresAt));
        if (!string.Equals(packet.ProjectId, projectId, StringComparison.Ordinal) ||
            !AgentWorkPacketPolicy.ValidateHandoff(packet, receipt).Valid)
        {
            throw new CortexRetentionRevisionRejectedException();
        }

        var request = new CortexRetentionAppendRequest(
            projectId,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            receipt.ReceiptId,
            Revision: 1,
            ExpectedPreviousHash: null,
            receipt.IdempotencyKey,
            ProducerId(receipt.FromOwner),
            receipt.ContractId,
            canonicalUtf8,
            CortexCanonicalJson.Sha256(canonicalUtf8),
            BuildReceiptSearchText(
                receipt.WorkId,
                receipt.PacketStage,
                "handoff",
                receipt.ReceiptId),
            receipt.HandedOffAt,
            receipt.PacketExpiresAt).Validate();
        return await _repository.AppendAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Retains passive intended-owner acknowledgement of one exact retained
    /// handoff receipt. Acceptance remains observed-byte evidence only.
    /// </summary>
    public async ValueTask<CortexRetentionAppendResult>
        SubmitWorkPacketAcceptanceReceiptAsync(
            string projectId,
            AgentWorkPacketAcceptanceReceiptV1 receipt,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentWorkPacketPolicy.HasValidHash(receipt))
        {
            throw new ArgumentException(
                "The acceptance receipt does not have a valid bounded self-hash.",
                nameof(receipt));
        }

        var canonicalUtf8 = CortexCanonicalJson.Serialize(receipt);
        RequireCanonicalSize(
            canonicalUtf8,
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            nameof(receipt));
        RequireSafeRetentionContent(canonicalUtf8);
        var retainedHandoff = await _repository.FindRevisionAsync(
            projectId,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            receipt.HandoffReceiptId,
            revision: 1,
            cancellationToken).ConfigureAwait(false);
        if (retainedHandoff is null)
            throw new CortexRetentionRevisionRejectedException();
        var handoff = ReadVerifiedPrevious<AgentWorkPacketHandoffReceiptV1>(
            retainedHandoff,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            AgentWorkPacketPolicy.HasValidHash,
            value => new RetainedContractIdentity(
                projectId,
                value.ReceiptId,
                Revision: 1,
                value.IdempotencyKey,
                ProducerId(value.FromOwner),
                value.ContractId,
                value.HandedOffAt,
                value.PacketExpiresAt));
        if (!AgentWorkPacketPolicy.ValidateAcceptance(handoff, receipt).Valid)
            throw new CortexRetentionRevisionRejectedException();

        var request = new CortexRetentionAppendRequest(
            projectId,
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
            receipt.ReceiptId,
            Revision: 1,
            ExpectedPreviousHash: null,
            receipt.IdempotencyKey,
            ProducerId(receipt.AcceptedBy),
            receipt.ContractId,
            canonicalUtf8,
            CortexCanonicalJson.Sha256(canonicalUtf8),
            BuildReceiptSearchText(
                receipt.WorkId,
                receipt.PacketStage,
                receipt.Disposition,
                receipt.ReceiptId),
            receipt.AcceptedAt,
            handoff.PacketExpiresAt).Validate();
        return await _repository.AppendAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<string?> ResolveExpectedPreviousHashAsync<T>(
        string projectId,
        string kind,
        string documentId,
        long revision,
        string? declaredPreviousHash,
        T current,
        Func<T, bool> hasValidSelfHash,
        Func<T, T, bool> hasValidContinuation,
        Func<T, RetainedContractIdentity> identity,
        CancellationToken cancellationToken)
    {
        if (revision == 1)
            return null;

        // Typed contracts chain their placeholder-based self-hashes. The
        // repository CAS instead chains the SHA-256 of the exact stored bytes.
        // Resolve that impedance mismatch only after verifying the predecessor.
        var previousDocument = await _repository.FindRevisionAsync(
            projectId,
            kind,
            documentId,
            revision - 1,
            cancellationToken).ConfigureAwait(false);
        if (previousDocument is null)
            return declaredPreviousHash;

        var previous = ReadVerifiedPrevious(
            previousDocument,
            kind,
            hasValidSelfHash,
            identity);
        if (!hasValidContinuation(previous, current))
            throw new CortexRetentionRevisionRejectedException();
        return previousDocument.ContentHash;
    }

    private static T ReadVerifiedPrevious<T>(
        CortexRetainedDocument retained,
        string expectedKind,
        Func<T, bool> hasValidSelfHash,
        Func<T, RetainedContractIdentity> identity)
    {
        if (retained.CanonicalUtf8 is null ||
            retained.CanonicalUtf8.Length is < 2 or
                > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes ||
            !string.Equals(
                retained.ContentHash,
                CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
                StringComparison.Ordinal) ||
            (retained.Revision == 1
                ? retained.PreviousHash is not null
                : !IsLowerSha256(retained.PreviousHash)) ||
            retained.SearchText is null ||
            retained.SearchText.Length > CortexAgentRetentionLimits.MaximumSearchTextCharacters ||
            retained.SearchText.Any(character =>
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new InvalidDataException(
                "The retained predecessor envelope failed integrity validation.");
        }

        T previous;
        byte[] exactCanonicalUtf8;
        try
        {
            previous = CortexCanonicalJson.Deserialize<T>(retained.CanonicalUtf8);
            exactCanonicalUtf8 = CortexCanonicalJson.Serialize(previous);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The retained predecessor is not a valid typed canonical document.",
                exception);
        }

        if (!retained.CanonicalUtf8.AsSpan().SequenceEqual(exactCanonicalUtf8) ||
            !hasValidSelfHash(previous))
        {
            throw new InvalidDataException(
                "The retained predecessor is not exact canonical data with a valid self-hash.");
        }

        var expected = identity(previous);
        if (!string.Equals(retained.ProjectId, expected.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(retained.Kind, expectedKind, StringComparison.Ordinal) ||
            !string.Equals(retained.DocumentId, expected.DocumentId, StringComparison.Ordinal) ||
            retained.Revision != expected.Revision ||
            !string.Equals(retained.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(retained.ProducerId, expected.ProducerId, StringComparison.Ordinal) ||
            !string.Equals(retained.ContractId, expected.ContractId, StringComparison.Ordinal) ||
            retained.CapturedAt != expected.CapturedAt ||
            retained.ExpiresAt != expected.ExpiresAt)
        {
            throw new InvalidDataException(
                "The retained predecessor metadata does not match its typed canonical document.");
        }

        return previous;
    }

    private static string ProducerId(AgentWorkOwnerV1 owner)
    {
        // A digest keeps the complete authority/subject/service tuple stable
        // without exceeding the retention identifier ceiling.
        var canonicalOwner = CortexCanonicalJson.Serialize(owner);
        return $"agent:{CortexCanonicalJson.Sha256(canonicalOwner)}";
    }

    private static string BuildReceiptSearchText(
        string workId,
        string stage,
        string disposition,
        string receiptId) =>
        string.Join(
            '\n',
            "passive untrusted work transfer receipt",
            workId,
            stage,
            disposition,
            receiptId);

    private static void RequireCanonicalSize(
        byte[] canonicalUtf8,
        int contractMaximumBytes,
        string parameterName)
    {
        if (canonicalUtf8.Length is < 2 ||
            canonicalUtf8.Length > contractMaximumBytes ||
            canonicalUtf8.Length > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The canonical contract document exceeds a retention boundary.");
        }
    }

    private static void RequireSafeRetentionContent(byte[] canonicalUtf8)
        => CortexRetentionContentPolicy.RequireAccepted(canonicalUtf8);

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record RetainedContractIdentity(
        string ProjectId,
        string DocumentId,
        long Revision,
        string IdempotencyKey,
        string ProducerId,
        string ContractId,
        DateTimeOffset CapturedAt,
        DateTimeOffset ExpiresAt);
}
