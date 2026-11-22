using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

internal sealed record SqliteCortexPayloadLifecycle(bool Archived, DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset UpdatedAtUtc, IReadOnlyList<string> ReferencedIds);

public sealed partial class SqliteCortexV2OperationalStore
{
    // Only the typed dependency and supersession fields are references. Opaque
    // evidence/provenance strings and record bodies are never interpreted here.
    internal static SqliteCortexPayloadLifecycle ReadPayloadLifecycle(string kind, string attributes)
    {
        using var document = JsonDocument.Parse(attributes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name)
            .Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw InvalidLifecycle();
        static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
        static bool Identifier(string? value) => value is { Length: > 0 and <= CortexV2Limits.MaximumIdentifierCharacters } &&
            char.IsAsciiLetterOrDigit(value[0]) && value.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
        if (kind == TaskKind)
        {
            if (new[] { "status", "dependencies", "createdAtUtc", "updatedAtUtc", "provenance" }.Any(name => !root.TryGetProperty(name, out _))) throw InvalidLifecycle();
            var value = ParseTaskMetadata(attributes);
            if (!Enum.IsDefined(value.Status) || value.Provenance is null ||
                value.Dependencies is null || value.Dependencies.Count > CortexV2Limits.MaximumDependencies ||
                value.Dependencies.Any(id => !Identifier(id)) || !Utc(value.UpdatedAtUtc) ||
                !Utc(value.CreatedAtUtc) || value.ArchivedAtUtc is { } archived && !Utc(archived) ||
                !root.EnumerateObject().All(p => p.Name is "status" or "assignee" or "claimedBy" or
                    "dependencies" or "blockedReason" or "createdAtUtc" or "updatedAtUtc" or "completedAtUtc" or
                    "archivedAtUtc" or "provenance" or "checkpoint" or "evidence" or "handoff" or "phaseId" or "checkpointSubject")) throw InvalidLifecycle();
            var isArchived = value.Status == CortexTaskStatusV2.Archived;
            if (isArchived && value.ArchivedAtUtc is null) throw InvalidLifecycle();
            return new(isArchived, value.ArchivedAtUtc, value.UpdatedAtUtc, value.Dependencies.ToArray());
        }
        if (kind == KnowledgeKind)
        {
            if (new[] { "state", "kind", "tier", "createdAtUtc", "updatedAtUtc", "provenance" }.Any(name => !root.TryGetProperty(name, out _))) throw InvalidLifecycle();
            var value = ParseKnowledgeMetadata(attributes);
            if (!Enum.IsDefined(value.State) || !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Tier) ||
                value.Provenance is null || !Utc(value.CreatedAtUtc) || !Utc(value.UpdatedAtUtc) ||
                value.ArchivedAtUtc is { } archived && !Utc(archived) ||
                value.SupersededByRecordId is { } id && !Identifier(id) ||
                !root.EnumerateObject().All(p => p.Name is "state" or "kind" or "tier" or "provenance" or
                    "supersededByRecordId" or "createdAtUtc" or "updatedAtUtc" or "archivedAtUtc")) throw InvalidLifecycle();
            var isArchived = value.State == CortexKnowledgeStateV2.Archived;
            if (isArchived && value.ArchivedAtUtc is null) throw InvalidLifecycle();
            return new(isArchived, value.ArchivedAtUtc, value.UpdatedAtUtc,
                value.SupersededByRecordId is { } replacement ? [replacement] : []);
        }
        throw InvalidLifecycle();
    }

    private static InvalidDataException InvalidLifecycle() => new("payload_lifecycle_metadata_unavailable");
}
