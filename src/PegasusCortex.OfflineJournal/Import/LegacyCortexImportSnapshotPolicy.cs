using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Import;

internal static class LegacyCortexImportSnapshotPolicy
{
    public static LegacyCortexImportManifestV1 Snapshot(LegacyCortexImportManifestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var items = value.Items.Select(item => item with
        {
            Protection = item.Protection with { }
        }).ToArray();
        return value with { Items = Array.AsReadOnly(items) };
    }

    public static void RequireBoundHash(
        LegacyCortexImportManifestV1 value,
        string expectedManifestSha256)
    {
        if (!string.Equals(value.ManifestSha256, expectedManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(value.ManifestSha256, ComputeHash(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The verified legacy import graph no longer matches its bound local manifest hash.");
        }
    }

    private static string ComputeHash(LegacyCortexImportManifestV1 value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value with
        {
            ManifestSha256 = OfflineJournalContractIds.EmptySha256
        }));
}
