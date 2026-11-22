using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Journal;

internal static class OfflineJournalStatePolicy
{
    public static OfflineJournalStateV1 CreateGenesis(OfflineJournalIdentity identity)
    {
        var value = new OfflineJournalStateV1(
            CortexSyncContractIds.SchemaVersion,
            OfflineJournalContractIds.State,
            OfflineJournalContractIds.Canonicalization,
            identity.JournalId,
            identity.WriterId,
            identity.StoreId,
            identity.ProjectId,
            $"epoch-{Guid.NewGuid():N}",
            0,
            OfflineJournalContractIds.EmptySha256,
            OfflineJournalContractIds.EmptySha256,
            OfflineJournalContractIds.EmptySha256);
        return Seal(value);
    }

    public static OfflineJournalStateV1 Advance(
        OfflineJournalStateV1 previous,
        JournalEntryMetadataV1 entry)
    {
        if (entry.Sequence != previous.HighWaterSequence + 1 ||
            !string.Equals(entry.PreviousEntrySha256, previous.LastEntrySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The state checkpoint cannot advance across a sequence or hash gap.");
        }
        return Seal(previous with
        {
            HighWaterSequence = entry.Sequence,
            LastEntrySha256 = entry.EntrySha256,
            PreviousStateSha256 = previous.StateSha256,
            StateSha256 = OfflineJournalContractIds.EmptySha256
        });
    }

    public static void Validate(
        OfflineJournalStateV1 value,
        OfflineJournalIdentity identity,
        OfflineJournalStateV1? previous,
        int maximumIdentifierCharacters)
    {
        var expectedSequence = previous is null ? 0 : previous.HighWaterSequence + 1;
        var expectedPreviousState = previous?.StateSha256 ?? OfflineJournalContractIds.EmptySha256;
        if (value.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(value.ContractId, OfflineJournalContractIds.State, StringComparison.Ordinal) ||
            !string.Equals(value.Canonicalization, OfflineJournalContractIds.Canonicalization, StringComparison.Ordinal) ||
            !string.Equals(value.JournalId, identity.JournalId, StringComparison.Ordinal) ||
            !string.Equals(value.WriterId, identity.WriterId, StringComparison.Ordinal) ||
            !string.Equals(value.StoreId, identity.StoreId, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectId, identity.ProjectId, StringComparison.Ordinal) ||
            !ValidIdentifier(value.StoreEpoch, maximumIdentifierCharacters) ||
            value.HighWaterSequence != expectedSequence ||
            value.HighWaterSequence > CortexSyncContractLimits.MaximumSequence ||
            !IsSha256(value.LastEntrySha256) || !IsSha256(value.PreviousStateSha256) ||
            !IsSha256(value.StateSha256) ||
            !string.Equals(value.PreviousStateSha256, expectedPreviousState, StringComparison.Ordinal) ||
            previous is not null && !string.Equals(value.StoreEpoch, previous.StoreEpoch, StringComparison.Ordinal) ||
            value.HighWaterSequence == 0 && !string.Equals(
                value.LastEntrySha256,
                OfflineJournalContractIds.EmptySha256,
                StringComparison.Ordinal) ||
            !string.Equals(value.StateSha256, ComputeHash(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The durable journal high-water checkpoint is invalid, unsupported, or modified.");
        }
    }

    public static string DirectoryName(OfflineJournalStateV1 value) =>
        $"{value.HighWaterSequence:D16}-{value.StateSha256[..16]}";

    private static OfflineJournalStateV1 Seal(OfflineJournalStateV1 value) =>
        value with { StateSha256 = ComputeHash(value) };

    private static string ComputeHash(OfflineJournalStateV1 value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value with
        {
            StateSha256 = OfflineJournalContractIds.EmptySha256
        }));

    private static bool ValidIdentifier(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
