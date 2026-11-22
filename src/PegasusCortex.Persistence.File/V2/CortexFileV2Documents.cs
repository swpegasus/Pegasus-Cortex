using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

internal static class CortexFileV2Schemas
{
    public const string Pointer = "pegasus.cortex.file-pointer/2.0";
    public const string Generation = "pegasus.cortex.file-generation/2.0";
    public const string IndexChunk = "pegasus.cortex.file-index-chunk/2.0";
    public const string ReceiptChunk = "pegasus.cortex.file-receipt-chunk/2.0";
    public const string TaskObject = "pegasus.cortex.file-task/2.0";
    public const string KnowledgeObject = "pegasus.cortex.file-knowledge/2.0";
    public const string Hot = "pegasus.cortex.hot/2.0";
    public const string BehaviorPack = "pegasus.cortex.behavior-pack/2.0";
    public const string BehaviorChunk = "pegasus.cortex.behavior-chunk/2.0";
}

internal sealed record CortexFileV2Pointer(
    string Schema,
    long GenerationRevision,
    string GenerationPath,
    string ContextCursor,
    DateTimeOffset CommittedAtUtc);

internal sealed record CortexFileV2Generation(
    string Schema,
    long Revision,
    DateTimeOffset CommittedAtUtc,
    IReadOnlyList<string> IndexChunks,
    IReadOnlyList<string> ReceiptChunks,
    long HeadCount,
    long RevisionCount,
    long ReceiptCount);

internal sealed record CortexFileV2IndexChunk(
    string Schema,
    long GenerationRevision,
    int Index,
    IReadOnlyList<CortexFileV2IndexEntry> Items);

internal sealed record CortexFileV2ReceiptChunk(
    string Schema,
    long GenerationRevision,
    int Index,
    IReadOnlyList<CortexFileV2Receipt> Items);

internal sealed record CortexFileV2IndexEntry(
    string Namespace,
    string RecordType,
    string RecordId,
    long Revision,
    bool IsHead,
    string ObjectPath,
    string ObjectHash,
    DateTimeOffset UpdatedAtUtc);

internal sealed record CortexFileV2RecordLocator(
    string RecordType,
    string RecordId,
    long Revision);

internal sealed record CortexFileV2Receipt(
    string Namespace,
    string Operation,
    string IdempotencyKey,
    string CommandHash,
    long GenerationRevision,
    IReadOnlyList<CortexFileV2RecordLocator> Results,
    CortexProvenanceV2? Provenance,
    string? Checkpoint,
    string? Evidence,
    string? Handoff,
    DateTimeOffset CommittedAtUtc);

internal sealed record CortexFileV2TaskObject(
    string Schema,
    string IdempotencyKey,
    string CommandHash,
    CortexTaskV2 Record);

internal sealed record CortexFileV2KnowledgeObject(
    string Schema,
    string IdempotencyKey,
    string CommandHash,
    CortexKnowledgeV2 Record);

public sealed record CortexFileHotV2(
    string Schema,
    bool Generated,
    bool Authoritative,
    string ProjectId,
    string ContextCursor,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> EffectiveNamespaces,
    IReadOnlyList<CortexContextItemV2> Items,
    bool HasMore);

internal sealed class CortexFileV2State
{
    public CortexFileV2State(
        long revision,
        string contextCursor,
        DateTimeOffset committedAtUtc,
        List<CortexFileV2IndexEntry>? entries = null,
        List<CortexFileV2Receipt>? receipts = null)
    {
        Revision = revision;
        ContextCursor = contextCursor;
        CommittedAtUtc = committedAtUtc;
        Entries = entries ?? [];
        Receipts = receipts ?? [];
    }

    public long Revision { get; set; }
    public string ContextCursor { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public List<CortexFileV2IndexEntry> Entries { get; }
    public List<CortexFileV2Receipt> Receipts { get; }

    public IEnumerable<CortexFileV2IndexEntry> Heads(string recordType) =>
        Entries.Where(value => value.IsHead &&
            string.Equals(value.RecordType, recordType, StringComparison.Ordinal));

    public CortexFileV2IndexEntry? Head(string recordType, string @namespace, string id) =>
        Entries.SingleOrDefault(value => value.IsHead &&
            string.Equals(value.RecordType, recordType, StringComparison.Ordinal) &&
            string.Equals(value.Namespace, @namespace, StringComparison.Ordinal) &&
            string.Equals(value.RecordId, id, StringComparison.Ordinal));

    public CortexFileV2IndexEntry? RevisionEntry(
        string recordType,
        string @namespace,
        string id,
        long revision) => Entries.SingleOrDefault(value =>
            string.Equals(value.RecordType, recordType, StringComparison.Ordinal) &&
            string.Equals(value.Namespace, @namespace, StringComparison.Ordinal) &&
            string.Equals(value.RecordId, id, StringComparison.Ordinal) &&
            value.Revision == revision);
}
