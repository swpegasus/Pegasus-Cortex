using System.Buffers.Binary;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;

namespace Pegasus.Cortex.LearningExports.Processing;

public static class DeterministicSplitAssigner
{
    public static string Assign(
        string datasetId,
        string leakageGroupId,
        LearningSplitPolicyV1 policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leakageGroupId);
        ArgumentNullException.ThrowIfNull(policy);

        var hash = LearningExportIdentity.ComputeHashBytes(
            "pegasus.cortex.learning-split-bucket/1.0",
            [
                KeyValuePair.Create("algorithm", policy.Algorithm),
                KeyValuePair.Create("datasetId", datasetId),
                KeyValuePair.Create("salt", policy.Salt),
                KeyValuePair.Create("leakageGroupId", leakageGroupId)
            ]);
        var bucket = BinaryPrimitives.ReadUInt64BigEndian(hash) % 10_000UL;

        if (bucket < (ulong)policy.TrainBasisPoints)
            return "train";
        if (bucket < (ulong)(policy.TrainBasisPoints + policy.ValidationBasisPoints))
            return "validation";
        return "test";
    }

    public static string ComputeLeakageGroupHash(
        string datasetId,
        string leakageGroupId,
        LearningSplitPolicyV1 policy) =>
        LearningExportIdentity.ComputeHash(
            "pegasus.cortex.learning-leakage-group/1.0",
            KeyValuePair.Create("algorithm", policy.Algorithm),
            KeyValuePair.Create("datasetId", datasetId),
            KeyValuePair.Create("salt", policy.Salt),
            KeyValuePair.Create("leakageGroupId", leakageGroupId));
}
