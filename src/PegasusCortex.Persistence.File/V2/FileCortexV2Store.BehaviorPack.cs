using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

public sealed record CortexFileBehaviorPackImportResultV2(
    string PackId,
    string Namespace,
    int Imported,
    int Unchanged,
    string ContextCursor);

public sealed partial class FileCortexV2Store
{
    /// <summary>
    /// Imports a validated portable behavior pack in one generation. Existing
    /// matching receipts are unchanged; any conflicting record fails the whole
    /// batch before the current pointer is advanced.
    /// </summary>
    public async ValueTask<CortexFileBehaviorPackImportResultV2> ImportBehaviorPackAsync(
        string packRoot,
        string targetNamespace,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireWritableNamespace(targetNamespace);
        CortexFileV2Validation.RequireIdentifier(actor, nameof(actor));
        var manifest = await CortexFileBehaviorPackManifestV2.ReadAsync(packRoot, cancellationToken)
            .ConfigureAwait(false);
        var records = await manifest.LoadRecordsAsync(packRoot, cancellationToken)
            .ConfigureAwait(false);
        var imported = 0;
        var unchanged = 0;
        string contextCursor;
        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in records)
            {
                var descriptor = value.Descriptor;
                var operation = $"behavior-pack:{manifest.PackId}:{descriptor.RecordId}";
                var idempotencyKey = BehaviorPackIdempotencyKey(manifest.PackId, descriptor.RecordId);
                var commandHash = CommandHash(new
                {
                    manifest.PackId,
                    Namespace = targetNamespace,
                    descriptor.RecordId,
                    descriptor.Kind,
                    descriptor.Tier,
                    descriptor.Title,
                    descriptor.Tags,
                    descriptor.Source,
                    value.Content
                });
                var replay = FindReceipt(
                    state,
                    targetNamespace,
                    operation,
                    idempotencyKey,
                    commandHash);
                if (replay is not null)
                {
                    unchanged++;
                    continue;
                }
                if (state.Head(KnowledgeRecordType, targetNamespace, descriptor.RecordId) is not null)
                {
                    throw new InvalidOperationException(
                        $"Behavior pack record '{descriptor.RecordId}' conflicts with existing knowledge.");
                }
                var now = UtcNow();
                var provenance = new CortexProvenanceV2(
                    CortexProvenanceSourceV2.Imported,
                    actor,
                    manifest.PackId,
                    $"Portable behavior pack source classification: {descriptor.Source}.",
                    now);
                var knowledge = new CortexKnowledgeV2(
                    targetNamespace,
                    descriptor.RecordId,
                    1,
                    CortexKnowledgeStateV2.Active,
                    descriptor.Kind,
                    descriptor.Tier,
                    descriptor.Title,
                    value.Content,
                    descriptor.Tags.ToArray(),
                    provenance,
                    null,
                    now,
                    now,
                    null);
                await AddKnowledgeRevisionAsync(
                    state,
                    knowledge,
                    idempotencyKey,
                    commandHash,
                    cancellationToken).ConfigureAwait(false);
                state.Receipts.Add(new CortexFileV2Receipt(
                    targetNamespace,
                    operation,
                    idempotencyKey,
                    commandHash,
                    0,
                    [new CortexFileV2RecordLocator(KnowledgeRecordType, knowledge.RecordId, 1)],
                    provenance,
                    null,
                    null,
                    null,
                    DateTimeOffset.UnixEpoch));
                imported++;
            }
            if (imported > 0)
                await CommitAsync(state, cancellationToken).ConfigureAwait(false);
            contextCursor = state.ContextCursor;
        }
        if (imported > 0)
            await TryRefreshHotAsync(cancellationToken).ConfigureAwait(false);
        return new CortexFileBehaviorPackImportResultV2(
            manifest.PackId,
            targetNamespace,
            imported,
            unchanged,
            contextCursor);
    }

    private static string BehaviorPackIdempotencyKey(string packId, string recordId)
    {
        var candidate = $"pack.{packId}.{recordId}";
        if (candidate.Length <= CortexV2Limits.MaximumIdentifierCharacters)
            return candidate;
        var suffix = CortexFileV2AtomicIo.Sha256(
            System.Text.Encoding.UTF8.GetBytes(candidate))[..24];
        return $"pack.{packId[..Math.Min(packId.Length, 64)]}.{suffix}";
    }
}
