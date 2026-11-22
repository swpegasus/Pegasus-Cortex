using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Cortex.Persistence.Sqlite.Storage;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Explicit copy-only export of one registry-authorized project's passive
/// retained state. A SQLite backup supplies one consistent read view; the
/// source database and project authority files are never changed.
/// </summary>
internal sealed class CortexLocalRecoveryExporter
{
    private static readonly string[] PassiveKinds =
    [
        CortexRetainedDocumentKinds.TaskSnapshot,
        CortexRetainedDocumentKinds.WorkPacket,
        CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
        CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
        CortexRetainedDocumentKinds.LearningFeedbackProposal
    ];

    private readonly CortexLocalProjectRegistry _registry;
    private readonly string _profileId;
    private readonly string _stateRoot;
    private readonly string _databasePath;
    private readonly CortexLocalRecoveryExportLimits _limits;

    public CortexLocalRecoveryExporter(
        CortexLocalProjectRegistry registry,
        string profileId,
        string stateRoot,
        string databasePath,
        CortexLocalRecoveryExportLimits? limits = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        CortexLocalProjectRegistryPolicy.RequireProfile(profileId);
        if (!string.Equals(
                registry.Document.ProfileId,
                profileId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The recovery exporter profile does not match the protected registry.",
                nameof(profileId));
        }
        _profileId = profileId;
        _stateRoot = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            stateRoot,
            requireExisting: true,
            allowAbsent: false);
        _databasePath = new SqliteCortexStoreOptions(databasePath)
            .Validate()
            .DatabasePath;
        _limits = (limits ?? new CortexLocalRecoveryExportLimits()).Validate();
    }

    public async ValueTask<CortexLocalRecoveryExportResult> ExportAsync(
        string projectLinkId,
        string destinationRoot,
        string runId,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeRecoveryExport(
                projectLinkId,
                out var registration))
        {
            return Rejected(
                projectLinkId,
                runId,
                "cortex_local_recovery_export_forbidden");
        }
        if (capturedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC capture time is required.", nameof(capturedAt));
        var actualNow = TimeProvider.System.GetUtcNow();
        if ((capturedAt - actualNow).Duration() > TimeSpan.FromMinutes(5))
            throw new ArgumentException(
                "The recovery export capture time must be within five minutes of the local clock.",
                nameof(capturedAt));
        if (!File.Exists(_databasePath))
            throw new FileNotFoundException(
                "The standalone retention database does not exist.",
                _databasePath);

        var destination = CortexLocalRecoveryExportPathPolicy
            .RequireDestinationRoot(
                destinationRoot,
                registration!.RootPath,
                _stateRoot);
        var finalRoot = CortexLocalRecoveryExportPathPolicy.OutputRoot(
            destination,
            runId);
        var stagingRoot = CortexLocalRecoveryExportPathPolicy.StagingRoot(
            destination,
            runId);
        Directory.CreateDirectory(stagingRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(stagingRoot);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(stagingRoot);

        try
        {
            var snapshotDirectory = Path.Combine(stagingRoot, ".snapshot");
            CortexLocalRecoveryExportPathPolicy.RequireOutputPath(
                stagingRoot,
                snapshotDirectory);
            Directory.CreateDirectory(snapshotDirectory);
            CortexLocalRegistryAclPolicy.ProtectDirectory(snapshotDirectory);
            var snapshotPath = Path.Combine(snapshotDirectory, "cortex.db");

            await CreateReadOnlySnapshotAsync(
                    snapshotPath,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<CortexLocalRecoveryExportEntry> entries;
            await using (var snapshot = new SqliteCortexRetentionStore(
                             new SqliteCortexStoreOptions(snapshotPath),
                             EarliestTimeProvider.Instance))
            {
                var integrity = await snapshot.CheckIntegrityAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!integrity.Healthy)
                    throw new InvalidDataException(
                        "The consistent SQLite export view failed integrity validation.");
                await RequireCurrentBriefAsync(
                        snapshot,
                        registration,
                        capturedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                entries = await ExportSnapshotAsync(
                        snapshot,
                        registration,
                        stagingRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                stagingRoot,
                snapshotDirectory);
            var exportedBytes = entries.Sum(entry => (long)entry.ByteCount);
            var manifest = new CortexLocalRecoveryExportManifest(
                SchemaVersion: 1,
                CortexLocalRecoveryExportContractIds.Manifest,
                CortexLocalRecoveryExportContractIds.Canonicalization,
                runId,
                capturedAt,
                _profileId,
                _registry.Document.RegistryHash,
                registration.TenantId,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                entries,
                entries.Count,
                exportedBytes);
            var manifestBytes = CortexCanonicalJson.Serialize(manifest);
            if (manifestBytes.Length > _limits.MaximumManifestBytes)
                throw new InvalidDataException(
                    "The recovery export manifest exceeds its configured byte boundary.");
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                    stagingRoot,
                    "manifest.json",
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            var manifestHash = CortexCanonicalJson.Sha256(manifestBytes);
            var receiptTemplate = new CortexLocalRecoveryExportReceipt(
                SchemaVersion: 1,
                CortexLocalRecoveryExportContractIds.Receipt,
                CortexLocalRecoveryExportContractIds.Canonicalization,
                runId,
                capturedAt,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                "manifest.json",
                manifestHash,
                manifestBytes.Length,
                entries.Count,
                exportedBytes,
                CortexLocalRecoveryExportContractIds.EmptySha256);
            var receipt = receiptTemplate with
            {
                ReceiptHash = CortexCanonicalJson.Sha256(
                    CortexCanonicalJson.Serialize(receiptTemplate))
            };
            var receiptBytes = CortexCanonicalJson.Serialize(receipt);
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                    stagingRoot,
                    "receipt.json",
                    receiptBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            _ = await CortexLocalRecoveryExportReader.ReadAsync(
                    stagingRoot,
                    _limits,
                    cancellationToken)
                .ConfigureAwait(false);

            var disposition = "exported";
            if (Directory.Exists(finalRoot))
            {
                var existing = await CortexLocalRecoveryExportReader.ReadAsync(
                        finalRoot,
                        _limits,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        CortexCanonicalJson.Sha256(
                            CortexCanonicalJson.Serialize(existing.Manifest)),
                        manifestHash,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        CortexCanonicalJson.Sha256(
                            CortexCanonicalJson.Serialize(existing.Receipt)),
                        CortexCanonicalJson.Sha256(receiptBytes),
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        "The recovery export run identifier already names different exact bytes.");
                }
                disposition = "idempotent";
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    destination,
                    stagingRoot);
            }
            else
            {
                Directory.Move(stagingRoot, finalRoot);
                CortexLocalRegistryAclPolicy.RequireProtectedDirectory(finalRoot);
            }

            return new CortexLocalRecoveryExportResult(
                CortexLocalRecoveryExportContractIds.Result,
                Accepted: true,
                disposition,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                runId,
                Path.GetRelativePath(destination, finalRoot)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                manifestHash,
                CortexCanonicalJson.Sha256(receiptBytes),
                entries.Count,
                exportedBytes,
                SourceMutationAllowed: false,
                FallbackRequired: false,
                FallbackInstructions:
                    "Read receipt.json, validate manifest.json, then read project-brief.json and bounded exact revision files.",
                DiagnosticCode: null);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    destination,
                    stagingRoot);
            }
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<CortexLocalRecoveryExportEntry>>
        ExportSnapshotAsync(
            ICortexAgentRetentionRepository repository,
            CortexLocalProjectRegistration registration,
            string stagingRoot,
            CancellationToken cancellationToken)
    {
        var entries = new List<CortexLocalRecoveryExportEntry>();
        var exportedBytes = 0L;
        var brief = await repository.FindHeadAsync(
            registration.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            registration.ProjectNamespace,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException(
                "The retained project has no current canonical project brief.");
        RequireExactDocument(brief, registration);
        exportedBytes = await AddEntryAsync(
            entries,
            brief,
            "project-brief.json",
            stagingRoot,
            exportedBytes,
            cancellationToken).ConfigureAwait(false);

        foreach (var kind in PassiveKinds)
        {
            string? after = null;
            var observedHeads = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await repository.ListHeadsByKindAsync(
                    registration.ProjectId,
                    kind,
                    after,
                    CortexAgentRetentionLimits.MaximumKindHeadPageSize,
                    cancellationToken).ConfigureAwait(false);
                foreach (var head in page.Items)
                {
                    if (!observedHeads.Add(head.DocumentId))
                        throw new InvalidDataException(
                            "The retained head listing repeated a document identity.");
                    var expectedRevision = 1L;
                    string? previousHash = null;
                    await foreach (var revision in repository
                        .ReadRevisionRangeAsync(
                            registration.ProjectId,
                            kind,
                            head.DocumentId,
                            1,
                            head.Revision,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (revision.Revision != expectedRevision ||
                            !string.Equals(
                                revision.PreviousHash,
                                previousHash,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "A retained revision range is incomplete or non-adjacent.");
                        }
                        RequireExactDocument(revision, registration);
                        var relative = RelativeRecordPath(revision);
                        exportedBytes = await AddEntryAsync(
                            entries,
                            revision,
                            relative,
                            stagingRoot,
                            exportedBytes,
                            cancellationToken).ConfigureAwait(false);
                        previousHash = revision.ContentHash;
                        expectedRevision++;
                    }
                    if (expectedRevision != head.Revision + 1 ||
                        !string.Equals(
                            previousHash,
                            head.ContentHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A retained head does not match its complete revision range.");
                    }
                }
                after = page.NextAfterDocumentId;
            }
            while (after is not null);
        }
        return entries.AsReadOnly();
    }

    private async ValueTask CreateReadOnlySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_databasePath, CortexUserStateAccess.Read);
        RequireConsolidatedSource();

        // Denying write sharing makes this a quiescent byte snapshot on
        // Windows. If a writer already owns the database, open fails closed.
        await using (var source = new FileStream(
                         _databasePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            RequireConsolidatedSource();
            await using var target = new FileStream(
                snapshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough);
            CortexLocalRegistryAclPolicy.ProtectFile(snapshotPath);
            await source.CopyToAsync(target, cancellationToken)
                .ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (source.Position != source.Length)
                throw new IOException("The quiescent source database copy was incomplete.");
        }
        RequireConsolidatedSource();

        var snapshotBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        await using var snapshot = new SqliteConnection(snapshotBuilder.ToString());
        await snapshot.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var configuration = snapshot.CreateCommand())
        {
            configuration.CommandText = """
                PRAGMA query_only = ON;
                PRAGMA foreign_keys = ON;
                PRAGMA trusted_schema = OFF;
                """;
            await configuration.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        await RequireCurrentReadOnlySchemaAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    private void RequireConsolidatedSource()
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = _databasePath + suffix;
            var info = new FileInfo(sidecar);
            if (info.LinkTarget is not null ||
                info.Exists &&
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The source SQLite sidecar cannot be a link or reparse point.");
            }
            if (info.Exists)
                throw new IOException(
                    "Recovery export requires a closed, checkpointed SQLite store with no live sidecars.");
        }
    }

    private static async ValueTask RequireCurrentReadOnlySchemaAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        await using (var integrity = source.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The source SQLite store failed read-only integrity validation.");
        }
        await using (var version = source.CreateCommand())
        {
            version.CommandText =
                "SELECT version FROM cortex_schema WHERE singleton = 1;";
            if (Convert.ToInt64(
                    await version.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    "The source SQLite store has an unsupported schema version.");
            }
        }
        await using (var index = source.CreateCommand())
        {
            index.CommandText = """
                SELECT sql
                  FROM sqlite_master
                 WHERE type = 'index'
                   AND name = 'ux_retained_head_single_task_snapshot';
                """;
            var sql = Convert.ToString(
                await index.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            var normalized = string.Join(
                ' ',
                (sql ?? string.Empty).Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
            if (!normalized.Contains(
                    "CREATE UNIQUE INDEX ux_retained_head_single_task_snapshot",
                    StringComparison.OrdinalIgnoreCase) ||
                !normalized.Contains(
                    "ON retained_head(project_id)",
                    StringComparison.OrdinalIgnoreCase) ||
                !normalized.Contains(
                    "WHERE kind = 'task-snapshot'",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The source SQLite store lacks the required single-task-head invariant.");
            }
        }
        await using (var foreignKeys = source.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "The source SQLite store failed its foreign-key boundary.");
        }
    }

    private static async ValueTask RequireCurrentBriefAsync(
        ICortexAgentRetentionRepository repository,
        CortexLocalProjectRegistration registration,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var scope = new CortexProjectBriefReadScope(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            "local-sqlite-policy",
            "local-os-user",
            "pegasus-cortex-local",
            PolicyRevision: 1);
        var timeProvider = new FixedTimeProvider(capturedAt);
        var briefStore = new SqliteCortexProjectBriefStore(
            repository,
            timeProvider);
        var currentBrief = await briefStore.FindCurrentAsync(
            scope,
            cancellationToken).ConfigureAwait(false);
        if (currentBrief is null)
        {
            throw new InvalidDataException(
                "The retained project has no current canonical project brief.");
        }
        var read = await new CortexProjectBriefService(
                briefStore,
                timeProvider)
            .ReadAsync(
                new CortexProjectBriefRequest(
                    registration.ProjectLinkId,
                    currentBrief.AuthoritySetHash),
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        if (!read.Accepted ||
            read.Response.FallbackRequired ||
            read.Response.StateRevision is null ||
            read.Response.SourceHash is null)
        {
            throw new InvalidDataException(
                "The current project brief failed the normal freshness and scope read boundary.");
        }

        var taskPage = await repository.ListHeadsByKindAsync(
            registration.ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            afterDocumentId: null,
            limit: 2,
            cancellationToken).ConfigureAwait(false);
        if (taskPage.Items.Count != 1 || taskPage.NextAfterDocumentId is not null)
            throw new InvalidDataException(
                "Recovery export requires exactly one current task-snapshot head.");
        var task = Exact<AgentTaskSnapshotV1>(taskPage.Items[0]);
        if (!AgentTaskSnapshotPolicy.HasValidHash(task) ||
            read.Response.StateRevision != task.SnapshotRevision ||
            !string.Equals(
                read.Response.SourceHash,
                task.SnapshotHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The current project brief does not bind the sole task-snapshot head.");
        }
    }

    private async ValueTask<long> AddEntryAsync(
        ICollection<CortexLocalRecoveryExportEntry> entries,
        CortexRetainedDocument document,
        string relativePath,
        string stagingRoot,
        long currentBytes,
        CancellationToken cancellationToken)
    {
        if (entries.Count >= _limits.MaximumEntries)
            throw new InvalidDataException(
                "The recovery export exceeds its configured entry boundary.");
        var nextBytes = checked(currentBytes + document.CanonicalUtf8.Length);
        if (nextBytes > _limits.MaximumExportedBytes)
            throw new InvalidDataException(
                "The recovery export exceeds its configured byte boundary.");
        await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                stagingRoot,
                relativePath,
                document.CanonicalUtf8,
                cancellationToken)
            .ConfigureAwait(false);
        entries.Add(new CortexLocalRecoveryExportEntry(
            document.ProjectId,
            document.Kind,
            document.DocumentId,
            document.Revision,
            document.PreviousHash,
            document.IdempotencyKey,
            document.ProducerId,
            document.ContractId,
            document.SearchText,
            document.ContentHash,
            document.CanonicalUtf8.Length,
            relativePath,
            document.CapturedAt,
            document.ExpiresAt));
        return nextBytes;
    }

    internal static void RequireExactDocument(
        CortexRetainedDocument document,
        CortexLocalProjectRegistration registration)
    {
        var expectedRepositoryProjectId =
            document.Kind == CortexRetainedDocumentKinds.ProjectBrief
                ? registration.ProjectLinkId
                : registration.ProjectId;
        if (!string.Equals(
                document.ProjectId,
                expectedRepositoryProjectId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A retained document crossed its registered project scope.");
        }
        _ = new CortexRetentionAppendRequest(
            document.ProjectId,
            document.Kind,
            document.DocumentId,
            document.Revision,
            document.PreviousHash,
            document.IdempotencyKey,
            document.ProducerId,
            document.ContractId,
            document.CanonicalUtf8,
            document.ContentHash,
            document.SearchText,
            document.CapturedAt,
            document.ExpiresAt).Validate();

        switch (document.Kind)
        {
            case CortexRetainedDocumentKinds.ProjectBrief:
            {
                RequireContract(document, CortexProjectBriefContract.ContractId);
                var value = Exact<CortexProjectBriefSnapshot>(document);
                if (document.CanonicalUtf8.Length >
                        CortexProjectBriefContract.MaximumUtf8Bytes ||
                    document.Revision != value.BriefRevision ||
                    !string.Equals(
                        document.ProjectId,
                        registration.ProjectLinkId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        document.DocumentId,
                        registration.ProjectNamespace,
                        StringComparison.Ordinal) ||
                    !MatchesProject(value, registration) ||
                    !string.Equals(
                        value.ContextSetId,
                        CortexProjectBriefService.ComputeContextSetId(value),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The retained project brief failed exact scope validation.");
                }
                break;
            }
            case CortexRetainedDocumentKinds.TaskSnapshot:
            {
                RequireContract(document, AgentTaskSnapshotContractIds.Snapshot);
                var value = Exact<AgentTaskSnapshotV1>(document);
                if (!AgentTaskSnapshotPolicy.HasValidHash(value) ||
                    value.SnapshotRevision != document.Revision ||
                    !string.Equals(value.TaskSetId, document.DocumentId, StringComparison.Ordinal) ||
                    !MatchesProject(value.ProjectId, value.ProjectLinkId, registration))
                    throw new InvalidDataException("A task snapshot failed exact scope validation.");
                break;
            }
            case CortexRetainedDocumentKinds.WorkPacket:
            {
                RequireContract(document, AgentWorkPacketContractIds.Packet);
                var value = Exact<AgentWorkPacketV1>(document);
                if (!AgentWorkPacketPolicy.HasValidHash(value) ||
                    value.PacketRevision != document.Revision ||
                    !string.Equals(value.WorkId, document.DocumentId, StringComparison.Ordinal) ||
                    !MatchesProject(value.ProjectId, value.ProjectLinkId, registration))
                    throw new InvalidDataException("A work packet failed exact scope validation.");
                break;
            }
            case CortexRetainedDocumentKinds.WorkPacketHandoffReceipt:
            {
                RequireContract(document, AgentWorkPacketContractIds.HandoffReceipt);
                var value = Exact<AgentWorkPacketHandoffReceiptV1>(document);
                if (document.Revision != 1 ||
                    !AgentWorkPacketPolicy.HasValidHash(value) ||
                    !string.Equals(value.ReceiptId, document.DocumentId, StringComparison.Ordinal))
                    throw new InvalidDataException("A handoff receipt failed exact validation.");
                break;
            }
            case CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt:
            {
                RequireContract(document, AgentWorkPacketContractIds.AcceptanceReceipt);
                var value = Exact<AgentWorkPacketAcceptanceReceiptV1>(document);
                if (document.Revision != 1 ||
                    !AgentWorkPacketPolicy.HasValidHash(value) ||
                    !string.Equals(value.ReceiptId, document.DocumentId, StringComparison.Ordinal))
                    throw new InvalidDataException("An acceptance receipt failed exact validation.");
                break;
            }
            case CortexRetainedDocumentKinds.LearningFeedbackProposal:
            {
                if (!LearningFeedbackContractIds.IsSupportedProposal(document.ContractId))
                {
                    throw new InvalidDataException(
                        "A private proposal has an unsupported contract identity.");
                }
                var value = Exact<LearningFeedbackProposalV1>(document);
                if (document.Revision != 1 ||
                    !string.Equals(value.ProposalId, document.DocumentId, StringComparison.Ordinal) ||
                    !string.Equals(value.TenantId, registration.TenantId, StringComparison.Ordinal) ||
                    !string.Equals(value.Namespace, registration.ProjectNamespace, StringComparison.Ordinal) ||
                    !LearningFeedbackSnapshotBoundary.TryCapture(
                        new LearningFeedbackSnapshot(value, Decision: null),
                        out var captured,
                        CancellationToken.None) ||
                    !CortexCanonicalJson.Serialize(captured!.Proposal)
                        .AsSpan().SequenceEqual(document.CanonicalUtf8))
                {
                    throw new InvalidDataException(
                        "A private proposal failed exact scope validation.");
                }
                break;
            }
            default:
                throw new InvalidDataException(
                    "The recovery exporter encountered an unsupported document kind.");
        }
    }

    private static T Exact<T>(CortexRetainedDocument document)
    {
        try
        {
            var value = CortexCanonicalJson.Deserialize<T>(document.CanonicalUtf8);
            if (!CortexCanonicalJson.Serialize(value)
                    .AsSpan().SequenceEqual(document.CanonicalUtf8))
                throw new InvalidDataException("Retained contract bytes are not exact canonical JSON.");
            return value;
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Text.Json.JsonException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException(
                "Retained contract bytes failed typed canonical validation.",
                exception);
        }
    }

    private static void RequireContract(
        CortexRetainedDocument document,
        string contractId)
    {
        if (!string.Equals(document.ContractId, contractId, StringComparison.Ordinal))
            throw new InvalidDataException("A retained kind has the wrong contract identity.");
    }

    private static bool MatchesProject(
        CortexProjectBriefSnapshot value,
        CortexLocalProjectRegistration registration) =>
        string.Equals(value.TenantId, registration.TenantId, StringComparison.Ordinal) &&
        string.Equals(value.ProjectLinkId, registration.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(value.ProjectId, registration.ProjectId, StringComparison.Ordinal) &&
        string.Equals(value.ProjectNamespace, registration.ProjectNamespace, StringComparison.Ordinal);

    private static bool MatchesProject(
        string projectId,
        string? projectLinkId,
        CortexLocalProjectRegistration registration) =>
        string.Equals(projectId, registration.ProjectId, StringComparison.Ordinal) &&
        string.Equals(projectLinkId, registration.ProjectLinkId, StringComparison.Ordinal);

    internal static string RelativeRecordPath(CortexRetainedDocument document)
    {
        var kind = document.Kind switch
        {
            CortexRetainedDocumentKinds.TaskSnapshot => "t",
            CortexRetainedDocumentKinds.WorkPacket => "w",
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt => "h",
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt => "a",
            CortexRetainedDocumentKinds.LearningFeedbackProposal => "p",
            _ => throw new InvalidDataException("The retained kind is not exportable.")
        };
        var documentToken = CortexCanonicalJson.Sha256(document.DocumentId)[..24];
        return $"records/{kind}/{documentToken}/{document.Revision:D20}.json";
    }

    private static CortexLocalRecoveryExportResult Rejected(
        string projectLinkId,
        string runId,
        string diagnosticCode) =>
        new(
            CortexLocalRecoveryExportContractIds.Result,
            Accepted: false,
            Disposition: "rejected",
            projectLinkId,
            ProjectId: null,
            ProjectNamespace: null,
            runId,
            ExportRelativePath: null,
            ManifestSha256: null,
            ReceiptSha256: null,
            EntryCount: 0,
            ExportedBytes: 0,
            SourceMutationAllowed: false,
            FallbackRequired: true,
            FallbackInstructions:
                "Use the registered project's authoritative local recovery files.",
            diagnosticCode);

    private sealed class EarliestTimeProvider : TimeProvider
    {
        public static EarliestTimeProvider Instance { get; } = new();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
    }
}

internal static class CortexLocalRecoveryExportPathPolicy
{
    private const int MaximumFinalPathCharacters = 240;

    public static string RequireDestinationRoot(
        string value,
        string projectRoot,
        string stateRoot)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An existing canonical local recovery destination is required.",
                nameof(value));
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(value, canonical, PathComparison) ||
            canonical.Length > MaximumFinalPathCharacters ||
            !Directory.Exists(canonical))
        {
            throw new ArgumentException(
                "The recovery destination must be an existing canonical directory.",
                nameof(value));
        }
        RequireLocalDrive(canonical);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(canonical);
        var info = new DirectoryInfo(canonical);
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recovery destination cannot be a link or reparse point.");
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(canonical);
        if (PathsOverlap(canonical, projectRoot) ||
            PathsOverlap(canonical, stateRoot))
        {
            throw new ArgumentException(
                "The recovery destination cannot overlap the project or standalone state tree.",
                nameof(value));
        }
        return canonical;
    }

    public static string OutputRoot(string destinationRoot, string runId)
    {
        var output = Path.Combine(
            destinationRoot,
            $"run-{CortexCanonicalJson.Sha256(runId)[..24]}");
        RequireOutputPath(destinationRoot, output);
        if (File.Exists(output))
            throw new IOException("The recovery export output path identifies a file.");
        if (Directory.Exists(output))
        {
            CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(output);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(output);
        }
        return output;
    }

    public static string StagingRoot(string destinationRoot, string runId)
    {
        var token = CortexCanonicalJson.Sha256(runId)[..16];
        var path = Path.Combine(
            destinationRoot,
            $".export-{token}-{Guid.NewGuid():N}");
        RequireOutputPath(destinationRoot, path);
        return path;
    }

    public static void RequireOutputPath(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonical = Path.GetFullPath(path);
        if (canonical.Length > MaximumFinalPathCharacters ||
            !canonical.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new ArgumentException(
                "A recovery export path escapes its root or exceeds 240 characters.",
                nameof(path));
        }
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(
            Path.GetDirectoryName(canonical) ?? canonicalRoot);
    }

    internal static void RequireLocalDrive(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        RequireSupportedDriveType(new DriveInfo(path).DriveType);
    }

    internal static void RequireSupportedDriveType(DriveType driveType)
    {
        if (driveType is DriveType.Network or DriveType.CDRom or
            DriveType.NoRootDirectory or DriveType.Unknown)
        {
            throw new ArgumentException(
                "Standalone recovery paths must use a supported local drive.");
        }
    }

    public static async ValueTask WriteNewProtectedAsync(
        string root,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.Split('/').Any(segment =>
                segment is "" or "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ')))
        {
            throw new ArgumentException("The recovery relative path is unsafe.", nameof(relativePath));
        }
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        RequireOutputPath(root, path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        using (var output = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 8192,
                   FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        CortexLocalRegistryAclPolicy.ProtectFile(path);
    }

    public static void DeleteVerifiedTree(string containingRoot, string path)
    {
        RequireOutputPath(containingRoot, path);
        if (!Directory.Exists(path))
            return;
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(path);
        var pending = new Queue<string>();
        var directories = new List<string>();
        var files = new List<string>();
        pending.Enqueue(path);
        var observed = 0;
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            directories.Add(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++observed > 500_000)
                    throw new IOException(
                        "Recovery cleanup exceeded its bounded entry count.");
                var info = new FileInfo(entry);
                var attributes = File.GetAttributes(entry);
                if (info.LinkTarget is not null ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Recovery cleanup encountered a link or reparse point.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Enqueue(entry);
                else
                    files.Add(entry);
            }
        }
        foreach (var file in files)
            File.Delete(file);
        foreach (var directory in directories
                     .OrderByDescending(value => value.Length))
            Directory.Delete(directory);
    }

    private static bool PathsOverlap(string left, string right)
    {
        var canonicalLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var canonicalRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(canonicalLeft, canonicalRight, PathComparison) ||
            canonicalLeft.StartsWith(
                canonicalRight + Path.DirectorySeparatorChar,
                PathComparison) ||
            canonicalRight.StartsWith(
                canonicalLeft + Path.DirectorySeparatorChar,
                PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
