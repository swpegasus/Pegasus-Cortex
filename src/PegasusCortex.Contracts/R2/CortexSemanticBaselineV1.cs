namespace Pegasus.Cortex.Contracts.R2;

/// <summary>Phase-one identity only; no payload, migration, transport or authorization contract.</summary>
public sealed record CortexSemanticBaselineV1(
    string Contract, string SchemaGeneration, int SchemaVersion, Guid StoreInstanceId,
    DateTimeOffset CreatedUtc, string SchemaSha256)
{
    public const string ContractName = "pegasus.cortex.semantic-baseline/1";
    public const string Generation = "pegasus-cortex-r2";
    public const int Version = 1;
    public const int MaximumIdentifierCharacters = 128;

    public void Validate()
    {
        if (Contract != ContractName || SchemaGeneration != Generation || SchemaVersion != Version ||
            StoreInstanceId == Guid.Empty || CreatedUtc.Offset != TimeSpan.Zero ||
            SchemaSha256 is not { Length: 64 } ||
            SchemaSha256.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Unsupported or incomplete R2 baseline identity.");
    }

    internal static void RequireId(string? value, string name)
    {
        if (value is not { Length: > 0 and <= MaximumIdentifierCharacters } ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') ||
            value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A bounded, exact portable identifier is required.", name);
    }
}

/// <summary>Independent optional project/chat/task membership; no default project, namespace-derived ownership or access grant.</summary>
public sealed record CortexSemanticOwnerV1(string? ProjectId = null, string? ChatId = null, string? TaskId = null)
{
    public void Validate()
    {
        if (ProjectId is not null) CortexSemanticBaselineV1.RequireId(ProjectId, nameof(ProjectId));
        if (ChatId is not null) CortexSemanticBaselineV1.RequireId(ChatId, nameof(ChatId));
        if (TaskId is not null) CortexSemanticBaselineV1.RequireId(TaskId, nameof(TaskId));
    }
}

public enum CortexSemanticRecordKindV1 { Task, Knowledge }

/// <summary>
/// A store-qualified locator, not proof of a record's existence, loaded body, successful effect or access grant.
/// Identifiers are retained exactly; display names, paths and current defaults never replace them.
/// </summary>
public sealed record CortexSemanticRecordReferenceV1(
    Guid StoreInstanceId, string Namespace, CortexSemanticRecordKindV1 Kind,
    string RecordId, long Revision, CortexSemanticOwnerV1 Owner)
{
    public void Validate()
    {
        CortexSemanticBaselineV1.RequireId(Namespace, nameof(Namespace));
        CortexSemanticBaselineV1.RequireId(RecordId, nameof(RecordId));
        ArgumentNullException.ThrowIfNull(Owner);
        Owner.Validate();
        if (StoreInstanceId == Guid.Empty || Revision < 1 || !Enum.IsDefined(Kind))
            throw new ArgumentException("An exact R2 reference requires store, kind and positive revision.");
        if (Kind == CortexSemanticRecordKindV1.Task && Owner.TaskId != RecordId)
            throw new ArgumentException("A task reference must retain its exact task identity.");
    }

    public void RequireMatches(CortexSemanticBaselineV1 baseline, CortexSemanticOwnerV1 expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(expectedOwner);
        baseline.Validate();
        expectedOwner.Validate();
        Validate();
        if (StoreInstanceId != baseline.StoreInstanceId || Owner != expectedOwner)
            throw new InvalidOperationException("The reference belongs to another store or membership scope.");
    }
}

/// <summary>The bootstrap exposes identity counts only. It neither reads nor writes semantic payloads.</summary>
public sealed record CortexSemanticBaselineSnapshotV1(
    CortexSemanticBaselineV1 Baseline, long Projects, long Chats, long Tasks, long RecordIdentities)
{
    public bool IsEmpty => Projects == 0 && Chats == 0 && Tasks == 0 && RecordIdentities == 0;
}
