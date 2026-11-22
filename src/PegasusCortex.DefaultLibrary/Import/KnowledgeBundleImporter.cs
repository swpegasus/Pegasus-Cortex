using System.Text.Json;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Cortex.DefaultLibrary.Validation;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.DefaultLibrary.Import;

public sealed class KnowledgeBundleImporter(
    KnowledgeBundleValidator validator,
    IKnowledgeRevisionRepository repository)
{
    public async ValueTask<KnowledgeBundleImportResult> ImportJsonAsync(
        ReadOnlyMemory<byte> bundleJson,
        KnowledgeImportContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bundle = KnowledgeBundleSerializer.Deserialize(bundleJson.Span);
            return await ImportAsync(bundle, context, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Reject(KnowledgeBundleValidator.Error(
                "default_library_bundle_json_invalid",
                "The seed bundle is not valid strict JSON."));
        }
    }

    public async ValueTask<KnowledgeBundleImportResult> ImportAsync(
        KnowledgeSeedBundleV1 bundle,
        KnowledgeImportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!context.CanImport)
            return Reject(ImportError("default_library_import_forbidden", "Server-derived policy does not grant library import."));

        var validation = validator.Validate(bundle);
        if (!validation.Accepted)
            return new KnowledgeBundleImportResult(false, 0, 0, validation.Diagnostics);

        var imports = new List<KnowledgeImportItem>();
        var idempotentCount = 0;
        foreach (var record in bundle.Records)
        {
            if (!string.Equals(record.RecordStatus, "active", StringComparison.Ordinal))
                return Reject(ImportError("default_library_record_not_active", "Only active knowledge revisions may become repository current records.", record));
            if (!context.WritableNamespaces.Contains(record.Namespace))
                return Reject(ImportError("default_library_namespace_forbidden", "Server-derived policy does not grant the record namespace.", record));
            if (record.Approval.PolicyRevision != context.PolicyRevision)
                return Reject(ImportError("default_library_policy_revision_stale", "The record approval policy revision is not current.", record));
            if (record.Approval.State == "approved" && !context.CanApprove)
                return Reject(ImportError("default_library_approval_forbidden", "Server-derived policy does not permit approved seed import.", record));

            var sameRevision = await repository.FindRevisionAsync(record.RevisionId, cancellationToken).ConfigureAwait(false);
            if (sameRevision is not null)
            {
                idempotentCount++;
                continue;
            }

            var current = await repository.FindCurrentAsync(record.RecordId, cancellationToken).ConfigureAwait(false);
            if (current is null && record.Supersession.SupersedesRevisionIds.Count != 0)
                return Reject(ImportError("default_library_superseded_revision_missing", "The proposed revision supersedes an unavailable current record.", record));
            if (current is not null &&
                !record.Supersession.SupersedesRevisionIds.Contains(current.RevisionId, StringComparer.Ordinal))
                return Reject(ImportError("default_library_supersession_conflict", "A changed record must explicitly supersede the current immutable revision.", record));

            imports.Add(new KnowledgeImportItem(record, current?.RevisionId));
        }

        if (imports.Count == 0)
            return new KnowledgeBundleImportResult(true, 0, idempotentCount, []);

        var write = await repository.ImportImmutableAsync(imports, cancellationToken).ConfigureAwait(false);
        if (!write.Accepted)
        {
            return Reject(write.Diagnostic ?? ImportError(
                "default_library_repository_rejected",
                "The immutable knowledge repository rejected the atomic import."));
        }

        return new KnowledgeBundleImportResult(true, write.AddedCount, idempotentCount, []);
    }

    private static KnowledgeBundleImportResult Reject(PegasusDiagnostic diagnostic) =>
        new(false, 0, 0, [diagnostic]);

    private static PegasusDiagnostic ImportError(
        string code,
        string message,
        KnowledgeRecordV1? record = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.default-library.import",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["recordId"] = record?.RecordId,
                ["revisionId"] = record?.RevisionId
            });
}
