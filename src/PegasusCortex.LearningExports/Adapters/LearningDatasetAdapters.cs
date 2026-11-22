using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;

namespace Pegasus.Cortex.LearningExports.Adapters;

/// <summary>
/// Local format seam. Implementations write one bounded item and must not call a provider,
/// model, network service, repository, or process.
/// </summary>
public interface ILearningDatasetAdapter
{
    string Id { get; }
    string Version { get; }
    string RelativeArtifactPath { get; }
    string MediaType { get; }

    ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken);
}

public sealed class CanonicalJsonlDatasetAdapter : ILearningDatasetAdapter
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    public string Id => LearningExportContractIds.CanonicalJsonlAdapter;
    public string Version => LearningExportContractIds.CanonicalJsonlAdapterVersion;
    public string RelativeArtifactPath => "learning-dataset.v1.jsonl";
    public string MediaType => "application/x-ndjson";

    public async ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(item);
        var bytes = LearningExportJson.Serialize(item);
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
    }
}
