using System.Text;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexKnowledgeSpaceMigrationResult(
    IReadOnlyList<string> EffectiveNamespaces,
    int SourceRecords,
    int Migrated,
    int Unchanged,
    int Archived,
    string Disposition);

public sealed partial class SqliteCortexV2OperationalStore
{
    private static readonly string[] LegacyKnowledgeNamespaces =
    [
        CortexKnowledgeSpaces.LegacySharedProgramming,
        CortexKnowledgeSpaces.LegacySharedGeneral,
        CortexKnowledgeSpaces.LegacyUserPreferences
    ];

    /// <summary>
    /// One bounded roll-forward pass for the preset local knowledge corpus.
    /// Current active heads are copied exactly into the base-first namespace,
    /// verified through the same transaction, and their legacy heads are
    /// archived without deleting immutable history. Project namespaces are
    /// neither enumerated nor modified.
    /// </summary>
    public async ValueTask<SqliteCortexKnowledgeSpaceMigrationResult> MigrateKnowledgeSpacesAsync(
        string actor,
        DateTimeOffset migratedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > CortexV2Limits.MaximumIdentifierCharacters ||
            actor.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded migration actor is required.", nameof(actor));
        }
        migratedAtUtc = migratedAtUtc.ToUniversalTime();

        var source = new List<CortexKnowledgeV2>();
        foreach (var legacyNamespace in LegacyKnowledgeNamespaces)
        {
            string? after = null;
            do
            {
                var page = await _store.ListHeadsAsync(
                    legacyNamespace,
                    KnowledgeKind,
                    after,
                    limit: 128,
                    cancellationToken,
                    searchableOnly: true).ConfigureAwait(false);
                source.AddRange(page.Items.Select(ToKnowledge));
                after = page.NextAfterRecordId;
            }
            while (after is not null);
        }

        if (source.Count == 0)
        {
            return new(
                [CortexKnowledgeSpaces.Shared, CortexKnowledgeSpaces.User],
                SourceRecords: 0,
                Migrated: 0,
                Unchanged: 0,
                Archived: 0,
                Disposition: "already-current");
        }
        if (source.Count > 128)
        {
            throw new InvalidDataException(
                "The one-pass knowledge-space migration accepts at most 128 active legacy heads.");
        }

        var canonical = new List<(CortexKnowledgeV2 Source, string Namespace)>();
        foreach (var group in source.GroupBy(
                     value => $"{CortexKnowledgeSpaces.Canonicalize(value.Namespace)}\n{value.RecordId}",
                     StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(value => Array.IndexOf(LegacyKnowledgeNamespaces, value.Namespace))
                .ToArray();
            var selected = ordered[0];
            if (ordered.Skip(1).Any(value => !SameKnowledge(selected, value)))
            {
                throw new InvalidDataException(
                    $"Legacy knowledge coordinate '{group.Key.Replace('\n', '/')}' is divergent and was not migrated.");
            }
            canonical.Add((selected, CortexKnowledgeSpaces.Canonicalize(selected.Namespace)));
        }

        var appends = new List<SqliteCortexV2AppendRequest>(source.Count + canonical.Count);
        var migrated = 0;
        var unchanged = 0;
        foreach (var item in canonical)
        {
            var existing = await _store.ReadHeadAsync(
                item.Namespace,
                KnowledgeKind,
                item.Source.RecordId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var current = ToKnowledge(existing);
                if (!SameKnowledge(item.Source, current) || current.State != CortexKnowledgeStateV2.Active)
                {
                    throw new InvalidDataException(
                        $"Canonical knowledge coordinate '{item.Namespace}/{item.Source.RecordId}' conflicts with the legacy head.");
                }
                unchanged++;
                continue;
            }

            var value = item.Source with
            {
                Namespace = item.Namespace,
                Revision = 1
            };
            var fingerprint = Fingerprint(
                "knowledge-space-migration",
                item.Source.Namespace,
                item.Namespace,
                item.Source.RecordId,
                item.Source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Source.Content);
            appends.Add(KnowledgeAppend(
                value,
                $"space.{fingerprint[..48]}",
                "KnowledgeSpaceMigration",
                expectedRevision: 0,
                contentChanged: true,
                content: Encoding.UTF8.GetBytes(value.Content),
                idempotencyFingerprint: fingerprint,
                actorOverride: actor));
            migrated++;
        }

        foreach (var legacy in source)
        {
            var archived = legacy with
            {
                Revision = legacy.Revision + 1,
                State = CortexKnowledgeStateV2.Archived,
                UpdatedAtUtc = migratedAtUtc,
                ArchivedAtUtc = migratedAtUtc
            };
            var fingerprint = Fingerprint(
                "knowledge-space-archive",
                legacy.Namespace,
                legacy.RecordId,
                legacy.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            appends.Add(KnowledgeAppend(
                archived,
                $"space.{fingerprint[..48]}",
                "KnowledgeSpaceArchived",
                expectedRevision: legacy.Revision,
                contentChanged: false,
                content: null,
                idempotencyFingerprint: fingerprint,
                actorOverride: actor));
        }

        var result = await _store.AppendBatchAsync(appends, cancellationToken).ConfigureAwait(false);
        if (!result.Committed || result.Items.Count != appends.Count)
        {
            throw new InvalidDataException(
                $"The knowledge-space migration was rejected atomically with '{result.Disposition}'.");
        }

        foreach (var item in canonical)
        {
            var stored = await _store.ReadHeadAsync(
                item.Namespace,
                KnowledgeKind,
                item.Source.RecordId,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("A migrated canonical knowledge head is unavailable.");
            if (!SameKnowledge(item.Source, ToKnowledge(stored)))
                throw new InvalidDataException("A migrated canonical knowledge head failed exact readback.");
        }

        return new(
            [CortexKnowledgeSpaces.Shared, CortexKnowledgeSpaces.User],
            source.Count,
            migrated,
            unchanged,
            source.Count,
            result.Disposition);
    }

    private static bool SameKnowledge(CortexKnowledgeV2 left, CortexKnowledgeV2 right) =>
        left.Kind == right.Kind &&
        left.Tier == right.Tier &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Content, right.Content, StringComparison.Ordinal) &&
        left.Tags.Order(StringComparer.Ordinal).SequenceEqual(
            right.Tags.Order(StringComparer.Ordinal),
            StringComparer.Ordinal) &&
        Equals(left.Provenance, right.Provenance) &&
        string.Equals(left.SupersededByRecordId, right.SupersededByRecordId, StringComparison.Ordinal) &&
        left.CreatedAtUtc == right.CreatedAtUtc;
}
