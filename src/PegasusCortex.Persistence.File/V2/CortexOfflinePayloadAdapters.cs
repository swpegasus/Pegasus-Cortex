using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public interface ICortexOfflinePayloadAdapter
{
    string SourcePayloadSchema { get; }
    string CurrentPayloadSchema { get; }

    CortexOfflineAdaptedCandidate Adapt(
        CortexOfflineDecodedFrame frame,
        string taskId,
        string scopeIdentity);
}

/// <summary>
/// Exact-source registry. Conversion is always one direct edge from a declared
/// source schema to the current canonical payload; it never searches or walks
/// a conversion chain.
/// </summary>
public sealed class CortexOfflinePayloadAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, ICortexOfflinePayloadAdapter> _adapters;

    public CortexOfflinePayloadAdapterRegistry(IEnumerable<ICortexOfflinePayloadAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<string, ICortexOfflinePayloadAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!map.TryAdd(adapter.SourcePayloadSchema, adapter))
                throw new ArgumentException("Two direct adapters claim the same source payload schema.", nameof(adapters));
        }
        _adapters = map;
    }

    public static CortexOfflinePayloadAdapterRegistry CreateDefault() => new(
        [new CortexOfflineKnowledgeCreateV1Adapter()]);

    public IReadOnlyList<string> SupportedSourceSchemas => _adapters.Keys.Order(StringComparer.Ordinal).ToArray();

    public CortexOfflineAdaptation Adapt(
        CortexOfflineDecodedFrame frame,
        string taskId,
        string scopeIdentity)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CortexFileV2Validation.RequireIdentifier(taskId, nameof(taskId));
        if (frame.Status == CortexOfflineFrameStatus.UnsupportedOuterSchema)
            return new(CortexOfflineOperationStatus.UnsupportedOuterSchema, null, frame.Diagnostic);
        if (frame.Frame is null || !_adapters.TryGetValue(frame.Frame.PayloadSchema, out var adapter))
            return new(CortexOfflineOperationStatus.UnsupportedPayload, null, frame.Diagnostic ??
                "No direct adapter is registered for this payload schema.");
        return new(
            CortexOfflineOperationStatus.Adapted,
            adapter.Adapt(frame, taskId, scopeIdentity),
            null);
    }
}

public sealed class CortexOfflineKnowledgeCreateV1Adapter : ICortexOfflinePayloadAdapter
{
    public string SourcePayloadSchema => CortexOfflineSchemas.KnowledgeCreateV1;
    public string CurrentPayloadSchema => CortexOfflineSchemas.KnowledgeCreateV1;

    public CortexOfflineAdaptedCandidate Adapt(
        CortexOfflineDecodedFrame frame,
        string taskId,
        string scopeIdentity)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CortexFileV2Validation.RequireIdentifier(taskId, nameof(taskId));
        var payload = CortexOfflineFrameCodec.DecodeKnowledgePayload(frame);
        var outer = frame.Frame!;
        if (scopeIdentity.Length != 64 || scopeIdentity.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase scope identity hash is required.", nameof(scopeIdentity));
        }
        // Bind central idempotency to the complete project-root/task scope
        // identity. The resulting key remains below the portable identifier
        // bound and does not reduce the collision resistance of the scope hash.
        var idempotencyKey = $"offline_{scopeIdentity}_{outer.FrameId}";
        CortexFileV2Validation.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        var command = new CortexKnowledgeCommandV2(
            payload.Namespace,
            CortexKnowledgeActionV2.Create,
            payload.RecordId,
            null,
            idempotencyKey,
            payload.Kind,
            payload.Tier,
            payload.Title,
            payload.Content,
            payload.Tags,
            new CortexProvenanceInputV2(payload.Source, payload.SourceId, payload.Evidence),
            null,
            CortexV2Limits.MaximumCompactResponseUtf8Bytes);
        return new CortexOfflineAdaptedCandidate(
            CortexOfflineOperationStatus.Adapted,
            taskId,
            scopeIdentity,
            outer.FrameId,
            outer.FrameSha256,
            outer.PayloadSha256,
            CortexOfflineFrameCodec.KnowledgeSemanticSha256(payload),
            SourcePayloadSchema,
            CurrentPayloadSchema,
            frame.PayloadBytes!.ToArray(),
            command);
    }
}

public static class CortexOfflineReceiptVerifier
{
    /// <summary>
    /// Compares the exact typed central record with the candidate and creates
    /// the only capability accepted by candidate replacement.
    /// </summary>
    public static CortexOfflineVerifiedReceiptV1 Verify(
        CortexOfflineAdaptedCandidate candidate,
        CortexKnowledgeV2 committedRecord,
        string disposition,
        DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(committedRecord);
        if (disposition is not ("created" or "appended" or "idempotent"))
            throw new InvalidOperationException("Only a successful central create disposition can replace a candidate.");
        var command = candidate.Command;
        if (!string.Equals(committedRecord.Namespace, command.Namespace, StringComparison.Ordinal) ||
            !string.Equals(committedRecord.RecordId, command.RecordId, StringComparison.Ordinal) ||
            committedRecord.Revision < 1 ||
            committedRecord.State != CortexKnowledgeStateV2.Active ||
            committedRecord.Kind != command.Kind ||
            committedRecord.Tier != command.Tier ||
            !string.Equals(committedRecord.Title, command.Title, StringComparison.Ordinal) ||
            !string.Equals(committedRecord.Content, command.Content, StringComparison.Ordinal) ||
            !committedRecord.Tags.SequenceEqual(command.Tags ?? [], StringComparer.Ordinal) ||
            command.Provenance is null ||
            committedRecord.Provenance.Source != command.Provenance.Source ||
            !string.Equals(committedRecord.Provenance.SourceId, command.Provenance.SourceId, StringComparison.Ordinal) ||
            !string.Equals(committedRecord.Provenance.Evidence, command.Provenance.Evidence, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The exact central knowledge record does not match the pending candidate.");
        }
        if (verifiedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The verification timestamp must be UTC.", nameof(verifiedAtUtc));
        var payload = new CortexOfflineReceiptPayloadV1(
            CortexOfflineSchemas.ReceiptV1,
            candidate.FrameId,
            candidate.FrameSha256,
            candidate.PayloadSha256,
            candidate.SemanticSha256,
            committedRecord.Namespace,
            committedRecord.RecordId,
            committedRecord.Revision,
            committedRecord.Kind,
            committedRecord.Tier,
            committedRecord.Title,
            disposition,
            Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson.Sha256(committedRecord.Content),
            verifiedAtUtc);
        return new CortexOfflineVerifiedReceiptV1(
            payload,
            candidate.TaskId,
            candidate.ScopeIdentity);
    }
}
