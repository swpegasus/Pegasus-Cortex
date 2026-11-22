using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Rebuilds one absent standalone database from a fully verified recovery
/// export. Immutable source envelopes are restored exactly; the derived
/// project brief is intentionally regenerated from the latest task head.
/// </summary>
internal sealed class CortexLocalRecoveryExportRestorer
{
    private readonly CortexLocalProjectRegistry _registry;
    private readonly string _profileId;
    private readonly string _stateRoot;
    private readonly string _databasePath;
    private readonly Action<string> _deletePromotedStagingRoot;

    public CortexLocalRecoveryExportRestorer(
        CortexLocalProjectRegistry registry,
        string profileId,
        string stateRoot,
        string databasePath,
        Action<string>? deletePromotedStagingRoot = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _profileId = profileId;
        _stateRoot = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            stateRoot,
            requireExisting: true,
            allowAbsent: false);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(_stateRoot);
        _databasePath = new SqliteCortexStoreOptions(databasePath)
            .Validate()
            .DatabasePath;
        var expectedDatabasePath = Path.Combine(_stateRoot, "cortex.db");
        if (!string.Equals(
                _databasePath,
                expectedDatabasePath,
                PathComparison))
        {
            throw new ArgumentException(
                "Recovery can target only the explicit profile database.",
                nameof(databasePath));
        }
        _deletePromotedStagingRoot =
            deletePromotedStagingRoot ?? Directory.Delete;
    }

    public async ValueTask<CortexLocalRecoveryRestoreResult> RestoreAsync(
        string projectLinkId,
        string exportRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectLinkId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeProjectBriefRead(
                projectLinkId,
                out var registration) ||
            registration is null ||
            !registration.AllowWrite)
        {
            throw new UnauthorizedAccessException(
                "Recovery restore requires the protected registry's read and write grants.");
        }

        // Admission precedes export reads, so a rejected command never touches
        // a remote, linked, project, or standalone-state source tree.
        var admittedExportRoot =
            CortexLocalRecoveryExportPathPolicy.RequireDestinationRoot(
                exportRoot,
                registration.RootPath,
                _stateRoot);
        RequireAbsentTarget();
        var readback = await CortexLocalRecoveryExportReader.ReadAsync(
                admittedExportRoot,
                limits: null,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExactScope(readback.Manifest, registration);
        RequireAbsentTarget();

        var retained = readback.RetainedDocuments
            .Where(document => !string.Equals(
                document.Kind,
                CortexRetainedDocumentKinds.ProjectBrief,
                StringComparison.Ordinal))
            .OrderBy(document => document.ProjectId, StringComparer.Ordinal)
            .ThenBy(document => document.Kind, StringComparer.Ordinal)
            .ThenBy(document => document.DocumentId, StringComparer.Ordinal)
            .ThenBy(document => document.Revision)
            .Select(document => document.Snapshot())
            .ToArray();
        if (retained.Length + 1 != readback.RetainedDocuments.Count)
        {
            throw new InvalidDataException(
                "Recovery requires exactly one exported derived project brief.");
        }
        if (retained.Any(document =>
                string.Equals(
                    document.Kind,
                    CortexRetainedDocumentKinds.LearningFeedbackProposal,
                    StringComparison.Ordinal) &&
                document.SearchText.Length != 0))
        {
            throw new InvalidDataException(
                "Private proposals must remain absent from the generic search projection.");
        }

        var latestTaskDocument = retained
            .Where(document => string.Equals(
                document.Kind,
                CortexRetainedDocumentKinds.TaskSnapshot,
                StringComparison.Ordinal))
            .OrderByDescending(document => document.Revision)
            .FirstOrDefault() ??
            throw new InvalidDataException(
                "Recovery requires one complete task-snapshot chain.");
        var latestTask = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            latestTaskDocument.CanonicalUtf8);
        var exportedBrief =
            CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                readback.ProjectBriefUtf8);

        var stagingRoot = CortexLocalRecoveryRestorePathPolicy.CreateStagingRoot(
            _stateRoot,
            readback.Manifest.RunId);
        var stagingDatabasePath = Path.Combine(stagingRoot, "cortex.db");
        var promoted = false;
        try
        {
            CortexLocalRecoveryRestorePathPolicy.CreateProtectedStagingRoot(
                _stateRoot,
                stagingRoot);
            var staged = await BuildAndVerifyAsync(
                    stagingDatabasePath,
                    registration,
                    retained,
                    latestTask,
                    exportedBrief,
                    readback.Manifest.CapturedAt,
                    cancellationToken)
                .ConfigureAwait(false);

            // Closing and reopening the staged database proves that validation
            // does not depend on live SQLite handles or uncheckpointed WAL.
            var restarted = await VerifyRestartAsync(
                    stagingDatabasePath,
                    registration,
                    retained,
                    latestTask,
                    exportedBrief,
                    readback.Manifest.CapturedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSameVerification(staged, restarted);
            CortexLocalRecoveryRestorePathPolicy.RequireClosedDatabase(
                stagingDatabasePath);

            var result = new CortexLocalRecoveryRestoreResult(
                CortexLocalRecoveryRestoreContractIds.Result,
                Accepted: true,
                Disposition: "current-state-rebuilt",
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                readback.Manifest.RunId,
                RestoredEntries: checked((int)restarted.Integrity.RetainedDocuments),
                RestoredBytes: restarted.RestoredBytes,
                restarted.Integrity.RetainedDocuments,
                restarted.Integrity.AuditEvents,
                restarted.Brief.ContextSetId,
                exportedBrief.ContextSetId,
                ContextLineageReset: !string.Equals(
                    restarted.Brief.ContextSetId,
                    exportedBrief.ContextSetId,
                    StringComparison.Ordinal),
                HistoricalProjectBriefChainRestored: false,
                UsesNetwork: false,
                SourceMutationAllowed: false,
                FallbackRequired: false,
                DiagnosticCode: null,
                RecoveryInstruction:
                    "Current local state is ready; use native SQLite backup recovery when forensic exact derived history is required.");

            // File.Move without overwrite is the commit point. A concurrent
            // normal startup wins safely by creating the target first.
            RequireAbsentTarget();
            File.Move(stagingDatabasePath, _databasePath, overwrite: false);
            promoted = true;
            try
            {
                _deletePromotedStagingRoot(stagingRoot);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The database commit is durable, but normal startup correctly
                // rejects any remaining staging child. Return a non-accepted,
                // actionable outcome instead of overstating readiness.
                return result with
                {
                    Accepted = false,
                    Disposition = "cleanup-required",
                    FallbackRequired = true,
                    DiagnosticCode =
                        "cortex_local_recovery_restore_cleanup_required",
                    RecoveryInstruction =
                        $"Inspect the protected staging child '{Path.GetFileName(stagingRoot)}', remove it only after verifying it contains no unexpected entries, and do not rerun restore while cortex.db exists."
                };
            }
            return result;
        }
        finally
        {
            if (!promoted)
            {
                CortexLocalRecoveryRestorePathPolicy.DeleteCreatedStaging(
                    _stateRoot,
                    stagingRoot,
                    stagingDatabasePath);
            }
        }
    }

    private async ValueTask<RestoreVerification> BuildAndVerifyAsync(
        string databasePath,
        CortexLocalProjectRegistration registration,
        IReadOnlyList<CortexRetainedDocument> retained,
        AgentTaskSnapshotV1 latestTask,
        CortexProjectBriefSnapshot exportedBrief,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var clock = new FixedTimeProvider(capturedAt);
        RestoreVerification verification;
        await using (var store = CreateStore(databasePath, clock))
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            foreach (var expected in retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await store.AppendAsync(
                        ToAppendRequest(expected),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Disposition is not (
                        CortexRetentionWriteDispositions.Appended or
                        CortexRetentionWriteDispositions.Idempotent) ||
                    result.Document is null ||
                    !ExactDocument(result.Document, expected))
                {
                    throw new InvalidDataException(
                        "A recovery envelope conflicted with the staged immutable store.");
                }
            }

            var briefStore = new SqliteCortexProjectBriefStore(store, clock);
            var runtime = new CortexMcpLocalRuntime(
                _registry,
                store,
                briefStore,
                clock);
            var replay = await runtime.SubmitTaskSnapshotAsync(
                    registration.ProjectLinkId,
                    registration.ProjectNamespace,
                    latestTask,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is null ||
                !string.Equals(
                    replay.Disposition,
                    CortexRetentionWriteDispositions.Idempotent,
                    StringComparison.Ordinal) ||
                replay.ProjectBriefDisposition is not (
                    CortexRetentionWriteDispositions.Appended or
                    CortexRetentionWriteDispositions.Idempotent) ||
                replay.ProjectionIssueCode is not null)
            {
                throw new InvalidDataException(
                    "The latest task head could not rematerialize the derived project brief.");
            }
            verification = await VerifyStoreAsync(
                    store,
                    briefStore,
                    registration,
                    retained,
                    latestTask,
                    exportedBrief,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        CortexLocalRecoveryRestorePathPolicy.RequireClosedDatabase(databasePath);
        return verification;
    }

    private async ValueTask<RestoreVerification> VerifyRestartAsync(
        string databasePath,
        CortexLocalProjectRegistration registration,
        IReadOnlyList<CortexRetainedDocument> retained,
        AgentTaskSnapshotV1 latestTask,
        CortexProjectBriefSnapshot exportedBrief,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var clock = new FixedTimeProvider(capturedAt);
        RestoreVerification verification;
        await using (var store = CreateStore(databasePath, clock))
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            verification = await VerifyStoreAsync(
                    store,
                    new SqliteCortexProjectBriefStore(store, clock),
                    registration,
                    retained,
                    latestTask,
                    exportedBrief,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        CortexLocalRecoveryRestorePathPolicy.RequireClosedDatabase(databasePath);
        return verification;
    }

    private static async ValueTask<RestoreVerification> VerifyStoreAsync(
        SqliteCortexRetentionStore store,
        SqliteCortexProjectBriefStore briefStore,
        CortexLocalProjectRegistration registration,
        IReadOnlyList<CortexRetainedDocument> retained,
        AgentTaskSnapshotV1 latestTask,
        CortexProjectBriefSnapshot exportedBrief,
        CancellationToken cancellationToken)
    {
        long restoredBytes = 0;
        foreach (var expected in retained)
        {
            var actual = await store.FindRevisionAsync(
                    expected.ProjectId,
                    expected.Kind,
                    expected.DocumentId,
                    expected.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (actual is null || !ExactDocument(actual, expected))
                throw new InvalidDataException(
                    "An exact restored revision failed staged readback.");
            restoredBytes = checked(restoredBytes + actual.CanonicalUtf8.Length);
        }

        var scope = new CortexProjectBriefReadScope(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            "local-sqlite-policy",
            "local-os-user",
            "pegasus-cortex-local",
            PolicyRevision: 1);
        var brief = await briefStore.FindCurrentAsync(scope, cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidDataException(
                "The rematerialized project brief is unavailable after readback.");
        RequireDerivedBrief(brief, latestTask, exportedBrief, registration);
        var briefDocument = await store.FindRevisionAsync(
                registration.ProjectLinkId,
                CortexRetainedDocumentKinds.ProjectBrief,
                registration.ProjectNamespace,
                revision: 1,
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidDataException(
                "The rematerialized project brief envelope is unavailable.");
        restoredBytes = checked(restoredBytes + briefDocument.CanonicalUtf8.Length);

        foreach (var proposal in retained.Where(document => string.Equals(
                     document.Kind,
                     CortexRetainedDocumentKinds.LearningFeedbackProposal,
                     StringComparison.Ordinal)))
        {
            var search = await store.SearchHeadsAsync(
                    proposal.ProjectId,
                    proposal.DocumentId,
                    CortexAgentRetentionLimits.MaximumListPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (search.Any(hit => string.Equals(
                    hit.Kind,
                    CortexRetainedDocumentKinds.LearningFeedbackProposal,
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "A restored private proposal leaked into generic search.");
            }
        }

        var integrity = await store.CheckIntegrityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!integrity.Healthy ||
            integrity.RetainedDocuments != retained.Count + 1 ||
            integrity.AuditEvents != integrity.RetainedDocuments)
        {
            throw new InvalidDataException(
                "The staged recovery database failed semantic, audit, or index integrity.");
        }
        return new RestoreVerification(brief, integrity, restoredBytes);
    }

    private void RequireExactScope(
        CortexLocalRecoveryExportManifest manifest,
        CortexLocalProjectRegistration registration)
    {
        if (!string.Equals(manifest.ProfileId, _profileId, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.RegistryHash,
                _registry.Document.RegistryHash,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.TenantId, registration.TenantId, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ProjectLinkId,
                registration.ProjectLinkId,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.ProjectId, registration.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ProjectNamespace,
                registration.ProjectNamespace,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery export does not match the protected registry scope.");
        }
    }

    private void RequireAbsentTarget()
    {
        foreach (var path in new[]
                 {
                     _databasePath,
                     _databasePath + "-wal",
                     _databasePath + "-shm",
                     _databasePath + "-journal"
                 })
        {
            var info = new FileInfo(path);
            if (File.Exists(path) ||
                Directory.Exists(path) ||
                info.LinkTarget is not null ||
                info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Recovery restore requires an absent database and absent SQLite sidecars.");
            }
        }
    }

    private static SqliteCortexRetentionStore CreateStore(
        string databasePath,
        TimeProvider clock) =>
        new(
            new SqliteCortexStoreOptions(
                databasePath,
                MaximumRetainedDocuments: 10_001,
                MaximumProjectDocuments: 10_001),
            clock);

    private static CortexRetentionAppendRequest ToAppendRequest(
        CortexRetainedDocument document) =>
        new CortexRetentionAppendRequest(
            document.ProjectId,
            document.Kind,
            document.DocumentId,
            document.Revision,
            document.PreviousHash,
            document.IdempotencyKey,
            document.ProducerId,
            document.ContractId,
            document.CanonicalUtf8.ToArray(),
            document.ContentHash,
            document.SearchText,
            document.CapturedAt,
            document.ExpiresAt).Validate();

    private static bool ExactDocument(
        CortexRetainedDocument actual,
        CortexRetainedDocument expected) =>
        string.Equals(actual.ProjectId, expected.ProjectId, StringComparison.Ordinal) &&
        string.Equals(actual.Kind, expected.Kind, StringComparison.Ordinal) &&
        string.Equals(actual.DocumentId, expected.DocumentId, StringComparison.Ordinal) &&
        actual.Revision == expected.Revision &&
        string.Equals(actual.PreviousHash, expected.PreviousHash, StringComparison.Ordinal) &&
        string.Equals(actual.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(actual.ProducerId, expected.ProducerId, StringComparison.Ordinal) &&
        string.Equals(actual.ContractId, expected.ContractId, StringComparison.Ordinal) &&
        actual.CanonicalUtf8.AsSpan().SequenceEqual(expected.CanonicalUtf8) &&
        string.Equals(actual.ContentHash, expected.ContentHash, StringComparison.Ordinal) &&
        string.Equals(actual.SearchText, expected.SearchText, StringComparison.Ordinal) &&
        actual.CapturedAt == expected.CapturedAt &&
        actual.ExpiresAt == expected.ExpiresAt;

    private static void RequireDerivedBrief(
        CortexProjectBriefSnapshot brief,
        AgentTaskSnapshotV1 latestTask,
        CortexProjectBriefSnapshot exportedBrief,
        CortexLocalProjectRegistration registration)
    {
        if (brief.BriefRevision != 1 ||
            brief.PreviousContextSetId is not null ||
            brief.StateRevision != latestTask.SnapshotRevision ||
            !string.Equals(brief.SourceHash, latestTask.SnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(brief.TenantId, registration.TenantId, StringComparison.Ordinal) ||
            !string.Equals(
                brief.ProjectLinkId,
                registration.ProjectLinkId,
                StringComparison.Ordinal) ||
            !string.Equals(brief.ProjectId, registration.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(
                brief.ProjectNamespace,
                registration.ProjectNamespace,
                StringComparison.Ordinal) ||
            !string.Equals(
                brief.ContextSetId,
                CortexProjectBriefService.ComputeContextSetId(brief),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The rematerialized project brief failed its current source binding.");
        }

        var expected = exportedBrief with
        {
            BriefRevision = 1,
            PreviousContextSetId = null
        };
        expected = expected with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(expected)
        };
        if (!CortexCanonicalJson.Serialize(brief)
                .AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(expected)))
        {
            throw new InvalidDataException(
                "The rematerialized project brief changed task, reference, freshness, or recovery semantics.");
        }
        foreach (var projected in brief.HotTasks.Prepend(brief.CurrentTask))
        {
            var source = latestTask.Tasks.SingleOrDefault(task =>
                string.Equals(task.TaskId, projected.TaskId, StringComparison.Ordinal)) ??
                throw new InvalidDataException(
                    "The rematerialized project brief contains an unknown task.");
            if (!string.Equals(
                    projected.Source.ProducerContract,
                    latestTask.ContractId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    projected.Source.SourceId,
                    latestTask.TaskSetId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    projected.Source.SourceRevisionId,
                    latestTask.SnapshotId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    projected.Source.SourceHash,
                    latestTask.SnapshotHash,
                    StringComparison.Ordinal) ||
                !string.Equals(projected.Gate.State, source.ApprovalGate.State, StringComparison.Ordinal) ||
                !string.Equals(
                    projected.Gate.AuthorityId,
                    source.ApprovalGate.AuthorityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    projected.Gate.ApprovalReferenceId,
                    source.ApprovalGate.ApprovalReferenceId,
                    StringComparison.Ordinal) ||
                !string.Equals(projected.Gate.Summary, source.ApprovalGate.Summary, StringComparison.Ordinal) ||
                projected.Gate.GrantsAuthority)
            {
                throw new InvalidDataException(
                    "The rematerialized task source or approval gate changed.");
            }
        }
    }

    private static void RequireSameVerification(
        RestoreVerification first,
        RestoreVerification second)
    {
        if (!CortexCanonicalJson.Serialize(first.Brief)
                .AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(second.Brief)) ||
            first.Integrity != second.Integrity ||
            first.RestoredBytes != second.RestoredBytes)
        {
            throw new InvalidDataException(
                "The staged recovery database changed across restart.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record RestoreVerification(
        CortexProjectBriefSnapshot Brief,
        CortexLocalStoreIntegrity Integrity,
        long RestoredBytes);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
    }
}

/// <summary>
/// Exact staging-path lifecycle. Staging remains beneath the owner-only state
/// root; the dedicated-layout check makes normal startup reject it fail closed.
/// </summary>
internal static class CortexLocalRecoveryRestorePathPolicy
{
    public static string CreateStagingRoot(string stateRoot, string runId)
    {
        var canonicalState = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stateRoot));
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(canonicalState);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(canonicalState);
        if (Directory.EnumerateFileSystemEntries(canonicalState).Any(entry =>
                Path.GetFileName(entry).StartsWith(
                    ".restore-",
                    StringComparison.Ordinal)))
        {
            throw new IOException(
                "Recovery cleanup is required for an abandoned restore staging entry.");
        }
        var token = CortexCanonicalJson.Sha256(runId)[..16];
        var path = Path.Combine(
            canonicalState,
            $".restore-{token}-{Guid.NewGuid():N}");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("The unique recovery staging path is occupied.");
        return path;
    }

    public static void CreateProtectedStagingRoot(
        string stateRoot,
        string stagingRoot)
    {
        RequireChild(stateRoot, stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(stagingRoot);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(stagingRoot);
        if (Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            throw new IOException("The new recovery staging directory is not empty.");
    }

    public static void RequireClosedDatabase(string databasePath)
    {
        if (!File.Exists(databasePath) ||
            File.Exists(databasePath + "-wal") ||
            File.Exists(databasePath + "-shm") ||
            File.Exists(databasePath + "-journal") ||
            Directory.Exists(databasePath + "-wal") ||
            Directory.Exists(databasePath + "-shm") ||
            Directory.Exists(databasePath + "-journal"))
        {
            throw new IOException(
                "The staged database is absent or has live SQLite sidecars.");
        }
    }

    public static void DeleteCreatedStaging(
        string stateRoot,
        string stagingRoot,
        string databasePath)
    {
        RequireChild(stateRoot, stagingRoot);
        if (!Directory.Exists(stagingRoot))
            return;
        foreach (var path in new[]
                 {
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + "-journal",
                     databasePath
                 })
        {
            if (Directory.Exists(path))
                throw new IOException(
                    "Recovery cleanup refuses an unexpected staging directory.");
            var info = new FileInfo(path);
            if (info.LinkTarget is not null ||
                info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Recovery cleanup refuses a linked staging entry.");
            }
            if (info.Exists)
                info.Delete();
        }
        if (Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            throw new IOException(
                "Recovery cleanup refuses an unexpected staging entry.");
        Directory.Delete(stagingRoot);
    }

    private static void RequireChild(string stateRoot, string stagingRoot)
    {
        var canonicalState = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stateRoot));
        var canonicalStaging = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingRoot));
        if (!string.Equals(
                Path.GetDirectoryName(canonicalStaging),
                canonicalState,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !Path.GetFileName(canonicalStaging).StartsWith(
                ".restore-",
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Recovery staging must remain in its exact state-root child boundary.");
        }
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(canonicalState);
    }
}
