namespace Pegasus.Cortex.Persistence.Sqlite.V2;

/// <summary>A whole-record history, or all semantic histories in one exact namespace.
/// Kind and RecordId must be supplied together. No selective revision pin is implied.</summary>
public sealed record SqliteCortexPayloadProtectionScope(string Namespace,
    string? Kind = null, string? RecordId = null);

/// <summary>Durable owner policy. Clearing either flag is another CAS-checked immutable
/// revision; neither age nor an automatic maintenance pass releases it.</summary>
public sealed record SqliteCortexPayloadProtectionRequest(int Version,
    SqliteCortexPayloadProtectionScope Scope, bool Pinned, bool Hold,
    long ExpectedRevision, string IdempotencyKey, string Actor);

/// <summary>One exact policy scope. Effective record protection also includes the
/// namespace policy. Revision zero means that this policy has never been written.</summary>
public sealed record SqliteCortexPayloadProtectionState(SqliteCortexPayloadProtectionScope Scope,
    long Revision, string? RevisionHash, bool Pinned, bool Hold);
