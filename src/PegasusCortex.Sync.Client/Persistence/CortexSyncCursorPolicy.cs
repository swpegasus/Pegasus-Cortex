using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.Persistence;

public static class CortexSyncCursorPolicy
{
    public static CortexSyncCursorV1 Seal(CortexSyncCursorV1 value)
    {
        ValidateShape(value, requireValidHash: false, allowLegacy: false);
        return value with { CursorHash = ComputeHash(value) };
    }

    public static void Validate(CortexSyncCursorV1 value, CortexSyncCursorV1? previous)
    {
        ValidateShape(value, requireValidHash: true, allowLegacy: false);
        ValidateChain(value, previous);
    }

    internal static void ValidateStored(CortexSyncCursorV1 value, CortexSyncCursorV1? previous)
    {
        ValidateShape(value, requireValidHash: true, allowLegacy: true);
        if (previous is not null &&
            string.Equals(previous.ContractId, CortexSyncClientContractIds.Cursor, StringComparison.Ordinal) &&
            string.Equals(value.ContractId, CortexSyncClientContractIds.CursorV1_0, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A cursor 1.0 checkpoint cannot appear after the 1.1 chain has started.");
        }
        ValidateChain(value, previous);
    }

    private static void ValidateChain(CortexSyncCursorV1 value, CortexSyncCursorV1? previous)
    {
        if (value.Revision != (previous?.Revision ?? 0) + 1 ||
            !string.Equals(
                value.PreviousCursorHash,
                previous?.CursorHash ?? CortexSyncClientContractIds.EmptySha256,
                StringComparison.Ordinal) ||
            previous is not null &&
            (!string.Equals(value.ProjectId, previous.ProjectId, StringComparison.Ordinal) ||
             !string.Equals(value.StoreId, previous.StoreId, StringComparison.Ordinal) ||
             !string.Equals(value.LocalStoreEpoch, previous.LocalStoreEpoch, StringComparison.Ordinal) ||
             !string.Equals(value.RemoteStoreEpoch, previous.RemoteStoreEpoch, StringComparison.Ordinal) ||
             value.LocalHighWaterSequence < previous.LocalHighWaterSequence ||
             value.RemoteHighWaterSequence < previous.RemoteHighWaterSequence ||
             value.UpdatedAt < previous.UpdatedAt))
        {
            throw new InvalidDataException(
                "The sync cursor chain contains a gap, identity drift, epoch rollback, or high-water rollback.");
        }
    }

    public static string ComputeHash(CortexSyncCursorV1 value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value with
        {
            CursorHash = CortexSyncClientContractIds.EmptySha256
        }));

    private static void ValidateShape(
        CortexSyncCursorV1 value,
        bool requireValidHash,
        bool allowLegacy)
    {
        ArgumentNullException.ThrowIfNull(value);
        var legacy = string.Equals(
            value.ContractId,
            CortexSyncClientContractIds.CursorV1_0,
            StringComparison.Ordinal);
        if (legacy && !allowLegacy)
        {
            throw new InvalidDataException(
                "New cursor checkpoints must use contract 1.1; contract 1.0 is read-only upgrade input.");
        }
        if (value.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !legacy && !string.Equals(value.ContractId, CortexSyncClientContractIds.Cursor, StringComparison.Ordinal) ||
            !string.Equals(value.Canonicalization, CortexSyncClientContractIds.Canonicalization, StringComparison.Ordinal) ||
            value.Revision < 1 ||
            !ValidIdentifier(value.ProjectId) ||
            !ValidIdentifier(value.StoreId) ||
            !ValidIdentifier(value.LocalStoreEpoch) ||
            !ValidIdentifier(value.RemoteStoreEpoch) ||
            value.LocalHighWaterSequence < 0 ||
            value.RemoteHighWaterSequence < 0 ||
            !IsHash(value.RemoteEventHash) ||
            !IsHash(value.RemoteBundleHash) ||
            !ValidIdentifier(value.RemoteRevision) ||
            !IsHash(value.LastAcknowledgedAuthorityHash) ||
            !IsHash(value.LastAcknowledgedPayloadHash) ||
            value.UpdatedAt.Offset != TimeSpan.Zero ||
            !IsHash(value.PreviousCursorHash) ||
            !IsHash(value.CursorHash) ||
            requireValidHash && !string.Equals(value.CursorHash, ComputeHash(value), StringComparison.Ordinal))
        {
            if (legacy)
                throw LegacyUpgradeRequired();
            throw new InvalidDataException("The durable sync cursor is malformed, unsupported, or modified.");
        }
    }

    internal static InvalidDataException LegacyUpgradeRequired() =>
        new(
            "Cursor contract 1.0 lacks the complete authenticated bundle/payload lineage " +
            "required for compatible 1.1 continuation. Preserve c/h and use an " +
            "operator-authorized bounded migration from the retained journal receipt; " +
            "this client will not guess or reset cursor authority.");

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
