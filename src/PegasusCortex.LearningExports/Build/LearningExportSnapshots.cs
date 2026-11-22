using System.Collections.ObjectModel;
using Pegasus.Cortex.LearningExports.Contracts;

namespace Pegasus.Cortex.LearningExports.Build;

internal sealed record LearningExportExecutionSnapshot(
    LearningInputBundleV1 Bundle,
    LearningDatasetDefinitionV1 Definition,
    LearningDatasetBuildRequest Request);

internal abstract class LearningExportSnapshotException(string boundary, string message) : IOException(message)
{
    public string Boundary { get; } = boundary;
}

internal sealed class LearningExportSnapshotLimitException(string boundary) : LearningExportSnapshotException(
    boundary,
    $"The caller-owned '{boundary}' collection exceeds its pre-validation snapshot limit.");

internal sealed class LearningExportSnapshotShapeException(string boundary) : LearningExportSnapshotException(
    boundary,
    $"The caller-owned '{boundary}' value is missing.");

/// <summary>
/// Severs caller-owned mutable collections before validation. Every enumeration is bounded,
/// so an untrusted IReadOnly* implementation cannot allocate beyond the configured contract.
/// </summary>
internal static class LearningExportSnapshots
{
    public static LearningExportExecutionSnapshot Capture(
        LearningInputBundleV1 bundle,
        LearningDatasetDefinitionV1 definition,
        LearningDatasetBuildRequest request,
        LearningExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var records = SnapshotList(
            Require(bundle.Records, "bundle.records"),
            options.MaxRecords,
            "bundle.records",
            record => Require(record, "bundle.records.item") with
            {
                Payload = SnapshotDictionary(
                    Require(record.Payload, "record.payload"),
                    options.MaxPayloadFields,
                    options.MaxPayloadCharacters,
                    "record.payload",
                    cancellationToken),
                Review = Require(record.Review, "record.review") with { }
            },
            cancellationToken);
        var bundleSnapshot = bundle with { Records = records };

        var definitionSnapshot = definition with
        {
            SelectedRevisions = SnapshotList(
                Require(definition.SelectedRevisions, "definition.selectedRevisions"),
                options.MaxRecords,
                "definition.selectedRevisions",
                selection => Require(selection, "definition.selectedRevisions.item") with { },
                cancellationToken),
            AllowedNamespaces = SnapshotList(
                Require(definition.AllowedNamespaces, "definition.allowedNamespaces"),
                256,
                "definition.allowedNamespaces",
                value => value,
                cancellationToken),
            IntendedUses = SnapshotList(
                Require(definition.IntendedUses, "definition.intendedUses"),
                3,
                "definition.intendedUses",
                value => value,
                cancellationToken),
            SplitPolicy = Require(definition.SplitPolicy, "definition.splitPolicy") with { },
            Transformations = SnapshotList(
                Require(definition.Transformations, "definition.transformations"),
                64,
                "definition.transformations",
                transformation => Require(transformation, "definition.transformations.item") with
                {
                    Parameters = SnapshotDictionary(
                        Require(transformation.Parameters, "transformation.parameters"),
                        32,
                        64 * 1024,
                        "transformation.parameters",
                        cancellationToken)
                },
                cancellationToken),
            Approval = Require(definition.Approval, "definition.approval") with { }
        };

        var authority = Require(request.Authority, "request.authority");
        var authoritySnapshot = authority with
        {
            TrustedApprovalAuthorities = SnapshotSet(
                authority.TrustedApprovalAuthorities,
                256,
                "authority.trustedApprovalAuthorities",
                cancellationToken),
            ApprovedDecisionHashes = SnapshotDictionary(
                authority.ApprovedDecisionHashes,
                options.MaxRecords + 1,
                checked((options.MaxRecords + 1) * (256 + 64)),
                "authority.approvedDecisionHashes",
                cancellationToken),
            ExportableNamespaces = SnapshotSet(
                authority.ExportableNamespaces,
                256,
                "authority.exportableNamespaces",
                cancellationToken)
        };

        return new LearningExportExecutionSnapshot(
            bundleSnapshot,
            definitionSnapshot,
            request with { Authority = authoritySnapshot });
    }

    private static IReadOnlyList<TOutput> SnapshotList<TInput, TOutput>(
        IEnumerable<TInput> values,
        int maximumCount,
        string boundary,
        Func<TInput, TOutput> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = new List<TOutput>(Math.Min(maximumCount, 256));
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Count == maximumCount)
                throw new LearningExportSnapshotLimitException(boundary);
            snapshot.Add(map(value));
        }
        return new ReadOnlyCollection<TOutput>(snapshot);
    }

    private static T Require<T>(T? value, string boundary) where T : class =>
        value ?? throw new LearningExportSnapshotShapeException(boundary);

    private static IReadOnlyDictionary<string, string> SnapshotDictionary(
        IEnumerable<KeyValuePair<string, string>> values,
        int maximumCount,
        int maximumCharacters,
        string boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long characters = 0;
        var enumerated = 0;
        foreach (var pair in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enumerated++ == maximumCount)
                throw new LearningExportSnapshotLimitException(boundary);
            if (pair.Key is null || pair.Value is null)
                throw new LearningExportSnapshotShapeException(boundary);
            characters += (long)pair.Key.Length + pair.Value.Length;
            if (characters > maximumCharacters)
                throw new LearningExportSnapshotLimitException(boundary);
            snapshot.Add(pair.Key, pair.Value);
        }
        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static IReadOnlySet<string> SnapshotSet(
        IEnumerable<string> values,
        int maximumCount,
        string boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = new HashSet<string>(StringComparer.Ordinal);
        var enumerated = 0;
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enumerated++ == maximumCount)
                throw new LearningExportSnapshotLimitException(boundary);
            snapshot.Add(value);
        }
        return new ReadOnlySet<string>(snapshot);
    }

    private sealed class ReadOnlySet<T>(ISet<T> values) : IReadOnlySet<T>
    {
        public int Count => values.Count;
        public bool Contains(T item) => values.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
