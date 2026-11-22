using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;

namespace Pegasus.Cortex.Persistence.Sqlite.Assets;

/// <summary>The inline/max policy is immutable catalog metadata; busy/capacity limits are per open.</summary>
public sealed record CortexHybridAssetOptionsV1(
    long InlineCutoffBytes = 2L * 1024 * 1024,
    long MaximumAssetBytes = 256L * 1024 * 1024,
    int BusyTimeoutMilliseconds = 5_000,
    int MaximumDatabasePages = 1_048_576)
{
    public CortexHybridAssetOptionsV1 Validate()
    {
        if (MaximumAssetBytes is < 1 or > CortexAssetCodecV2.MaximumAssetBytes ||
            InlineCutoffBytes < 0 || InlineCutoffBytes > MaximumAssetBytes ||
            BusyTimeoutMilliseconds is < 1_000 or > 30_000 || BusyTimeoutMilliseconds % 1_000 != 0 ||
            MaximumDatabasePages is < 256 or > 16_777_216)
            throw new CortexAssetException("asset_configuration_invalid");
        return this;
    }
}
