using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;

namespace Pegasus.Cortex.LearningExports.Processing;

public interface ILearningRecordTransformer
{
    string Id { get; }
    string Version { get; }

    bool SupportsParameters(IReadOnlyDictionary<string, string> parameters);

    ValueTask<IReadOnlyDictionary<string, string>> TransformAsync(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);
}

public sealed class LearningTransformationPipeline
{
    private const int MaximumTransformerImplementations = 256;
    private readonly IReadOnlyDictionary<(string Id, string Version), ILearningRecordTransformer> _transformers;

    public LearningTransformationPipeline(IEnumerable<ILearningRecordTransformer>? transformers = null)
    {
        var implementations = transformers is null
            ? [new NormalizeLineEndingsTransformer(), new TrimValuesTransformer()]
            : transformers.Take(MaximumTransformerImplementations + 1).ToArray();
        if (implementations.Length > MaximumTransformerImplementations)
            throw new ArgumentException("Too many learning record transformers were supplied.", nameof(transformers));
        _transformers = implementations.ToDictionary(Key);
    }

    public bool Supports(LearningTransformationSpecV1 spec) =>
        spec is not null && spec.Parameters is not null &&
        _transformers.TryGetValue(Key(spec.Id, spec.Version), out var transformer) &&
        transformer.SupportsParameters(spec.Parameters);

    public async ValueTask<IReadOnlyDictionary<string, string>> ApplyAsync(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyList<LearningTransformationSpecV1> transformations,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> current = LearningExportFactory.CanonicalDictionary(payload);
        foreach (var spec in transformations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformer = _transformers[Key(spec.Id, spec.Version)];
            current = LearningExportFactory.CanonicalDictionary(
                await transformer.TransformAsync(
                    current,
                    spec.Parameters,
                    cancellationToken).ConfigureAwait(false));
        }
        return current;
    }

    private static (string Id, string Version) Key(ILearningRecordTransformer transformer) =>
        Key(transformer.Id, transformer.Version);

    private static (string Id, string Version) Key(string id, string version) => (id, version);
}

public sealed class NormalizeLineEndingsTransformer : ILearningRecordTransformer
{
    public string Id => "normalize-line-endings";
    public string Version => "1";
    public bool SupportsParameters(IReadOnlyDictionary<string, string> parameters) => parameters.Count == 0;

    public ValueTask<IReadOnlyDictionary<string, string>> TransformAsync(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireNoParameters(parameters);
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
            payload.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n'),
                StringComparer.Ordinal));
    }

    private static void RequireNoParameters(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count != 0)
            throw new ArgumentException("normalize-line-endings/1 does not accept parameters.", nameof(parameters));
    }
}

public sealed class TrimValuesTransformer : ILearningRecordTransformer
{
    public string Id => "trim-values";
    public string Version => "1";
    public bool SupportsParameters(IReadOnlyDictionary<string, string> parameters) => parameters.Count == 0;

    public ValueTask<IReadOnlyDictionary<string, string>> TransformAsync(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.Count != 0)
            throw new ArgumentException("trim-values/1 does not accept parameters.", nameof(parameters));

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
            payload.ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal));
    }
}
