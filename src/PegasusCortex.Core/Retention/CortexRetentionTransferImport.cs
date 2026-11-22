using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Copy-first provider-neutral import application. It validates every target
/// chain before the first append, accepts only exact prefixes, and never merges
/// or selects a winner for divergent state.
/// </summary>
public sealed class CortexRetentionTransferImportService
{
    private readonly ICortexAgentRetentionRepository _repository;
    private readonly ICortexRetentionTransferBriefRegenerator _briefRegenerator;

    public CortexRetentionTransferImportService(
        ICortexAgentRetentionRepository repository,
        ICortexRetentionTransferBriefRegenerator briefRegenerator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _briefRegenerator = briefRegenerator ??
            throw new ArgumentNullException(nameof(briefRegenerator));
    }

    public async ValueTask<CortexRetentionTransferImportResult> ImportAsync(
        CortexRetentionTransferBundleV1 bundle,
        CortexRetentionTransferImportScope authorizedScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedScope);
        var validated = CortexRetentionTransferBundlePolicy.Validate(bundle);
        if (!MatchesAuthorizedScope(validated.Project, authorizedScope))
        {
            return Rejected(
                validated,
                appendedCount: 0,
                existingCount: 0,
                "transfer_authorized_scope_mismatch");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var appendedCount = 0;
        var existingCount = 0;

        try
        {
            var preflight = await PreflightAsync(validated, cancellationToken)
                .ConfigureAwait(false);
            if (!preflight.Accepted)
            {
                return Rejected(
                    validated,
                    appendedCount,
                    existingCount,
                    preflight.DiagnosticCode!);
            }

            existingCount = preflight.ExistingCount;
            foreach (var document in preflight.MissingDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _repository.AppendAsync(
                        document.ToAppendRequest(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(
                        result.Disposition,
                        CortexRetentionWriteDispositions.Appended,
                        StringComparison.Ordinal))
                {
                    if (result.Document is null ||
                        !Exact(document, result.Document) ||
                        !string.Equals(
                            result.CurrentHeadHash,
                            document.ContentHash,
                            StringComparison.Ordinal))
                    {
                        return Rejected(
                            validated,
                            appendedCount,
                            existingCount,
                            "transfer_repository_append_contract_invalid");
                    }
                    appendedCount++;
                }
                else if (string.Equals(
                             result.Disposition,
                             CortexRetentionWriteDispositions.Idempotent,
                             StringComparison.Ordinal))
                {
                    if (result.Document is null || !Exact(document, result.Document))
                    {
                        return Rejected(
                            validated,
                            appendedCount,
                            existingCount,
                            "transfer_repository_idempotency_contract_invalid");
                    }
                    existingCount++;
                }
                else
                {
                    return Rejected(
                        validated,
                        appendedCount,
                        existingCount,
                        result.Disposition switch
                        {
                            CortexRetentionWriteDispositions.Conflict =>
                                "transfer_target_race_conflict",
                            CortexRetentionWriteDispositions.StaleBase =>
                                "transfer_target_race_stale_base",
                            _ => "transfer_repository_disposition_invalid"
                        });
                }
            }

            var latestTask = validated.AuthoritativeDocuments
                .Where(item => item.Kind == CortexRetainedDocumentKinds.TaskSnapshot)
                .OrderByDescending(item => item.Revision)
                .First();
            var taskSnapshot = ExactTaskSnapshot(latestTask);
            var regeneration = await _briefRegenerator.RegenerateAsync(
                    validated.Project,
                    taskSnapshot,
                    validated.DerivedBriefEvidence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ValidRegeneration(validated, taskSnapshot, regeneration))
            {
                return new CortexRetentionTransferImportResult(
                    Accepted: false,
                    CortexRetentionTransferImportDispositions.BriefRegenerationFailed,
                    validated.BundleId,
                    validated.BundleHash,
                    validated.AuthoritativeDocuments.Count,
                    appendedCount,
                    existingCount,
                    regeneration,
                    regeneration?.DiagnosticCode ??
                        "transfer_brief_regeneration_invalid");
            }

            return new CortexRetentionTransferImportResult(
                Accepted: true,
                appendedCount == 0
                    ? CortexRetentionTransferImportDispositions.Idempotent
                    : CortexRetentionTransferImportDispositions.Imported,
                validated.BundleId,
                validated.BundleHash,
                validated.AuthoritativeDocuments.Count,
                appendedCount,
                existingCount,
                regeneration,
                DiagnosticCode: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CortexRetentionTransferValidationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CortexRetentionTransferImportException(
                "transfer_import_dependency_failure",
                validated.BundleId,
                appendedCount,
                existingCount,
                exception);
        }
    }

    private async ValueTask<PreflightResult> PreflightAsync(
        CortexRetentionTransferBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        var sourceChains = bundle.AuthoritativeDocuments
            .GroupBy(
                document => new ChainKey(
                    document.RepositoryProjectId,
                    document.Kind,
                    document.DocumentId))
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Revision).ToArray());
        var targetHeads = new Dictionary<ChainKey, CortexRetainedDocument>();

        // A complete transfer may extend exact target prefixes but may not
        // silently coexist with an extra target chain omitted by the source.
        foreach (var kind in CortexRetentionTransferBundlePolicy.AuthoritativeKinds)
        {
            string? cursor = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _repository.ListAllHeadsByKindAsync(
                        bundle.Project.ProjectId,
                        kind,
                        cursor,
                        CortexAgentRetentionLimits.MaximumKindHeadPageSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var head in page.Items)
                {
                    var key = new ChainKey(head.ProjectId, head.Kind, head.DocumentId);
                    if (!sourceChains.ContainsKey(key))
                        return PreflightResult.Reject("transfer_target_extra_head");
                    if (!targetHeads.TryAdd(key, head.Snapshot()))
                        return PreflightResult.Reject("transfer_target_head_duplicated");
                }
                if (page.NextAfterDocumentId is not null &&
                    (string.Equals(cursor, page.NextAfterDocumentId, StringComparison.Ordinal) ||
                     page.Items.Count == 0))
                {
                    return PreflightResult.Reject("transfer_target_head_page_invalid");
                }
                cursor = page.NextAfterDocumentId;
            }
            while (cursor is not null);
        }

        var missing = new List<CortexRetentionTransferDocument>();
        var existingCount = 0;
        foreach (var pair in sourceChains.OrderBy(
                     item => CortexRetentionTransferBundlePolicy.KindOrder(item.Key.Kind))
                 .ThenBy(item => item.Key.DocumentId, StringComparer.Ordinal))
        {
            targetHeads.TryGetValue(pair.Key, out var targetHead);
            var sourceHead = pair.Value[^1];
            if (targetHead is not null && targetHead.Revision > sourceHead.Revision)
                return PreflightResult.Reject("transfer_target_ahead");

            foreach (var source in pair.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = await _repository.FindRevisionAsync(
                        source.RepositoryProjectId,
                        source.Kind,
                        source.DocumentId,
                        source.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (targetHead is null)
                {
                    if (target is not null)
                        return PreflightResult.Reject("transfer_target_orphan_revision");
                    missing.Add(source);
                }
                else if (source.Revision <= targetHead.Revision)
                {
                    if (target is null)
                        return PreflightResult.Reject("transfer_target_predecessor_missing");
                    if (!Exact(source, target))
                        return PreflightResult.Reject("transfer_target_diverged");
                    existingCount++;
                }
                else
                {
                    if (target is not null)
                        return PreflightResult.Reject("transfer_target_orphan_revision");
                    missing.Add(source);
                }
            }

            if (targetHead is not null)
            {
                var matchingSource = pair.Value.Single(item =>
                    item.Revision == targetHead.Revision);
                if (!Exact(matchingSource, targetHead))
                    return PreflightResult.Reject("transfer_target_diverged");
            }
        }

        return new PreflightResult(
            Accepted: true,
            Array.AsReadOnly(missing.ToArray()),
            existingCount,
            DiagnosticCode: null);
    }

    private static bool ValidRegeneration(
        CortexRetentionTransferBundleV1 bundle,
        AgentTaskSnapshotV1 source,
        CortexRetentionTransferBriefRegenerationResult? result)
    {
        if (result is null || !result.Accepted ||
            string.IsNullOrWhiteSpace(result.Disposition) ||
            result.Disposition.Any(char.IsControl) ||
            result.ContextSetId is not { Length: 68 } ||
            !result.ContextSetId.StartsWith("ctx_", StringComparison.Ordinal) ||
            !ValidHash(result.ContextSetId[4..]) ||
            !string.Equals(result.SourceHash, source.SnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(
                result.AuthoritySetHash,
                bundle.Project.AuthoritySetHash,
                StringComparison.Ordinal) ||
            result.DiagnosticCode is not null)
        {
            return false;
        }
        return result.MatchingEvidenceContentHash is null ||
            bundle.DerivedBriefEvidence.Any(item => string.Equals(
                item.ContentHash,
                result.MatchingEvidenceContentHash,
                StringComparison.Ordinal));
    }

    private static AgentTaskSnapshotV1 ExactTaskSnapshot(
        CortexRetentionTransferDocument document)
    {
        try
        {
            var value = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                document.CanonicalUtf8);
            if (!AgentTaskSnapshotPolicy.HasValidHash(value) ||
                !CortexCanonicalJson.Serialize(value)
                    .AsSpan().SequenceEqual(document.CanonicalUtf8))
            {
                throw new CortexRetentionTransferValidationException(
                    "transfer_import_task_snapshot_invalid");
            }
            return value;
        }
        catch (CortexRetentionTransferValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Text.Json.JsonException or
                InvalidOperationException or NotSupportedException)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_import_task_snapshot_invalid");
        }
    }

    private static bool Exact(
        CortexRetentionTransferDocument expected,
        CortexRetainedDocument actual) =>
        string.Equals(expected.RepositoryProjectId, actual.ProjectId, StringComparison.Ordinal) &&
        string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal) &&
        string.Equals(expected.DocumentId, actual.DocumentId, StringComparison.Ordinal) &&
        expected.Revision == actual.Revision &&
        string.Equals(expected.PreviousHash, actual.PreviousHash, StringComparison.Ordinal) &&
        string.Equals(expected.IdempotencyKey, actual.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(expected.ProducerId, actual.ProducerId, StringComparison.Ordinal) &&
        string.Equals(expected.ContractId, actual.ContractId, StringComparison.Ordinal) &&
        expected.CanonicalUtf8.AsSpan().SequenceEqual(actual.CanonicalUtf8) &&
        string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal) &&
        string.Equals(expected.SearchText, actual.SearchText, StringComparison.Ordinal) &&
        expected.CapturedAt == actual.CapturedAt &&
        expected.ExpiresAt == actual.ExpiresAt;

    private static bool ValidHash(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool MatchesAuthorizedScope(
        CortexRetentionTransferProjectScope project,
        CortexRetentionTransferImportScope authorizedScope) =>
        string.Equals(project.TenantId, authorizedScope.TenantId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectLinkId, authorizedScope.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectId, authorizedScope.ProjectId, StringComparison.Ordinal) &&
        string.Equals(
            project.ProjectNamespace,
            authorizedScope.ProjectNamespace,
            StringComparison.Ordinal);

    private static CortexRetentionTransferImportResult Rejected(
        CortexRetentionTransferBundleV1 bundle,
        int appendedCount,
        int existingCount,
        string diagnosticCode) =>
        new(
            Accepted: false,
            CortexRetentionTransferImportDispositions.Rejected,
            bundle.BundleId,
            bundle.BundleHash,
            bundle.AuthoritativeDocuments.Count,
            appendedCount,
            existingCount,
            BriefRegeneration: null,
            diagnosticCode);

    private sealed record ChainKey(
        string RepositoryProjectId,
        string Kind,
        string DocumentId);

    private sealed record PreflightResult(
        bool Accepted,
        IReadOnlyList<CortexRetentionTransferDocument> MissingDocuments,
        int ExistingCount,
        string? DiagnosticCode)
    {
        public static PreflightResult Reject(string code) =>
            new(false, [], 0, code);
    }
}
