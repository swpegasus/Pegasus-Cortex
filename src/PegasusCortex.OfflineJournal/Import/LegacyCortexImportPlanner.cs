using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.IO;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.OfflineJournal.Security;

namespace Pegasus.Cortex.OfflineJournal.Import;

public sealed class LegacyCortexImportPlanner : IAsyncDisposable
{
    private static readonly IReadOnlySet<string> ApprovedRecoveryFiles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "README.md",
            "STATE.md",
            "RECOVERY.md",
            "WORKLOG.md",
            "LEARNINGS.md",
            "SESSIONS.md",
            "TASKS.md",
            "GUIDE.md",
            "RULES.md",
            "meta.json"
        };
    private const int MaximumRecoveryRootEntries = 64;

    private readonly OfflineJournalOptions _options;
    private readonly JournalFileStore _store;
    private readonly OfflineJournalIdentity _identity;
    private readonly IJournalPayloadProtector _protector;
    private readonly IJournalFileSystemBoundaryFactory _fileSystemBoundaryFactory;
    private readonly bool _allowLexicalTestOnly;
    private bool _disposed;

    internal LegacyCortexImportPlanner(
        string requestedJournalRoot,
        string approvedJournalRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        OfflineJournalOptions? options = null)
        : this(
            requestedJournalRoot,
            approvedJournalRoot,
            identity,
            protector,
            LexicalTestOnlyJournalFileSystemBoundaryFactory.Instance,
            options,
            allowLexicalTestOnly: true)
    {
    }

    public LegacyCortexImportPlanner(
        string requestedJournalRoot,
        string approvedJournalRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        IJournalFileSystemBoundaryFactory fileSystemBoundaryFactory,
        OfflineJournalOptions? options = null)
        : this(
            requestedJournalRoot,
            approvedJournalRoot,
            identity,
            protector,
            fileSystemBoundaryFactory,
            options,
            allowLexicalTestOnly: false)
    {
    }

    private LegacyCortexImportPlanner(
        string requestedJournalRoot,
        string approvedJournalRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        IJournalFileSystemBoundaryFactory fileSystemBoundaryFactory,
        OfflineJournalOptions? options,
        bool allowLexicalTestOnly)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(protector);
        _options = options ?? new OfflineJournalOptions();
        _options.Validate();
        if (!ValidSyncIdentifier(identity.JournalId) || !ValidSyncIdentifier(identity.WriterId) ||
            !ValidSyncIdentifier(identity.StoreId) || !ValidSyncIdentifier(identity.ProjectId))
        {
            throw new ArgumentException("Journal, writer, store, and project identifiers are invalid.", nameof(identity));
        }
        _identity = identity with { };
        _protector = protector;
        _fileSystemBoundaryFactory = fileSystemBoundaryFactory ?? throw new ArgumentNullException(nameof(fileSystemBoundaryFactory));
        _allowLexicalTestOnly = allowLexicalTestOnly;
        var opened = JournalFileSystemBoundaryGate.OpenJournal(
            fileSystemBoundaryFactory,
            requestedJournalRoot,
            approvedJournalRoot,
            allowLexicalTestOnly);
        try
        {
            _store = new JournalFileStore(opened.Paths, opened.Boundary);
        }
        catch
        {
            opened.Boundary.Dispose();
            throw;
        }
    }

    internal ValueTask<LegacyCortexImportResult> InventoryAndCopyAsync(
        string sourceRoot,
        string runId,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken) =>
        InventoryAndCopyAsync(sourceRoot, sourceRoot, runId, capturedAt, cancellationToken);

    public ValueTask<LegacyCortexImportResult> InventoryAndCopyAsync(
        string requestedSourceRoot,
        string approvedSourceRoot,
        string runId,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken) =>
        InventoryAndCopyCoreAsync(
            requestedSourceRoot,
            approvedSourceRoot,
            runId,
            capturedAt,
            recoverySetOnly: false,
            cancellationToken);

    /// <summary>
    /// Copies only the fixed human-readable recovery set from one exact
    /// .cortex root. It does not traverse archives or generated directories,
    /// and it never converts Markdown into task authority.
    /// </summary>
    public ValueTask<LegacyCortexImportResult> InventoryAndCopyRecoverySetAsync(
        string requestedSourceRoot,
        string approvedSourceRoot,
        string runId,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken) =>
        InventoryAndCopyCoreAsync(
            requestedSourceRoot,
            approvedSourceRoot,
            runId,
            capturedAt,
            recoverySetOnly: true,
            cancellationToken);

    private async ValueTask<LegacyCortexImportResult> InventoryAndCopyCoreAsync(
        string requestedSourceRoot,
        string approvedSourceRoot,
        string runId,
        DateTimeOffset capturedAt,
        bool recoverySetOnly,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? stage = null;
        try
        {
            if (!JournalPathPolicy.IsPortableIdentifier(runId, _options.MaxIdentifierCharacters) ||
                capturedAt.Offset != TimeSpan.Zero)
            {
                return Reject("legacy_cortex_import_request_invalid", "The import run identifier or capture time is invalid.");
            }

            _store.EnsureLayout();
            using var writerLease = _store.AcquireWriterLock();
            using var sourceBoundary = JournalFileSystemBoundaryGate.OpenLegacySource(
                _fileSystemBoundaryFactory,
                requestedSourceRoot,
                approvedSourceRoot,
                _allowLexicalTestOnly);
            var canonicalSource = sourceBoundary.RootPath;
            if (!string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalSource)),
                    ".cortex",
                    StringComparison.Ordinal))
            {
                return Reject(
                    "legacy_cortex_source_leaf_invalid",
                    "The v1 legacy source root must use the exact .cortex leaf name.");
            }
            EnsureRootsDoNotOverlap(canonicalSource, _store.JournalRoot);
            var sources = recoverySetOnly
                ? InventoryRecoverySource(sourceBoundary, cancellationToken)
                : InventorySource(sourceBoundary, cancellationToken);
            if (sources.Count == 0)
                return Reject("legacy_cortex_import_empty", "The legacy source contains no regular files to copy.");

            var final = _store.ImportPath(runId);
            if (_store.DirectoryExists(final))
            {
                var existing = await ReadAndValidateImportDirectoryAsync(final, cancellationToken).ConfigureAwait(false);
                return await MatchesCurrentSourcesAsync(
                        existing,
                        sources,
                        sourceBoundary,
                        recoverySetOnly,
                        cancellationToken).ConfigureAwait(false)
                    ? new LegacyCortexImportResult(
                        LegacyCortexImportDisposition.Existing,
                        existing,
                        CreateVerified(existing),
                        null)
                    : new LegacyCortexImportResult(
                        LegacyCortexImportDisposition.Conflict,
                        existing,
                        null,
                        OfflineJournalDiagnostics.Error(
                            "legacy_cortex_import_run_conflict",
                            "The run identifier already names different source provenance.",
                            "cortex.offline-journal.legacy-import"));
            }

            stage = _store.CreateStageDirectory("i", _options.MaxStagingArtifacts);
            var items = new List<LegacyCortexImportItemV1>(sources.Count);
            long totalBytes = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                await using var input = sourceBoundary.OpenReadFile(
                    source.RelativePath,
                    64 * 1024,
                    asynchronous: true,
                    sequentialScan: true);
                if (input.Length > _options.MaxLegacyImportFileBytes ||
                    totalBytes > _options.MaxLegacyImportTotalBytes - input.Length)
                {
                    throw new JournalLimitExceededException("legacy_import_bytes");
                }

                var storedFileName = $"f{index + 1:D6}.bin";
                var protectionContext = CreateProtectionContext(runId, source, index + 1);
                JournalPayloadProtectionMetadata protection;
                string sourceHash;
                string storedHash;
                long sourceBytes;
                long storedBytes;
                await using (var output = _store.CreatePrivateFile(Path.Combine(stage, storedFileName)))
                await using (var boundedOutput = new BoundedHashWriteStream(
                    output,
                    _options.MaxLegacyImportFileBytes + 64 * 1024,
                    leaveOpen: true))
                using (var boundedInput = new BoundedHashReadStream(input, _options.MaxLegacyImportFileBytes, input.Length))
                {
                    protection = await _protector.ProtectAsync(
                        boundedInput,
                        boundedOutput,
                        protectionContext,
                        cancellationToken).ConfigureAwait(false);
                    sourceHash = boundedInput.CompleteHash();
                    storedHash = boundedOutput.CompleteHash();
                    sourceBytes = boundedInput.BytesRead;
                    storedBytes = boundedOutput.BytesWritten;
                    ValidateProtectionMetadata(protection);
                    await boundedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    boundedOutput.FlushToDisk();
                }

                var sourceRecheck = await ComputeSourceDigestAsync(
                    sourceBoundary,
                    source.RelativePath,
                    _options.MaxLegacyImportFileBytes,
                    recoverySetOnly,
                    cancellationToken).ConfigureAwait(false);
                if (sourceRecheck.Bytes != sourceBytes || !string.Equals(sourceRecheck.Sha256, sourceHash, StringComparison.Ordinal))
                    throw new IOException("A legacy source changed during its copy-first capture.");
                totalBytes = checked(totalBytes + sourceBytes);
                items.Add(new LegacyCortexImportItemV1(
                    source.ExactPath,
                    source.RelativePath,
                    sourceBytes,
                    sourceHash,
                    storedFileName,
                    storedBytes,
                    storedHash,
                    protection,
                    "copied"));
            }

            var manifest = new LegacyCortexImportManifestV1(
                CortexSyncContractIds.SchemaVersion,
                OfflineJournalContractIds.LegacyImport,
                OfflineJournalContractIds.Canonicalization,
                runId,
                canonicalSource,
                capturedAt,
                Array.AsReadOnly(items.ToArray()),
                totalBytes,
                OfflineJournalContractIds.ContentTreatment,
                false,
                OfflineJournalContractIds.EmptySha256);
            manifest = manifest with { ManifestSha256 = ComputeManifestHash(manifest) };
            await _store.WriteCanonicalAsync(
                Path.Combine(stage, JournalFileStore.ImportManifestFileName),
                manifest,
                _options.MaxLegacyManifestBytes,
                cancellationToken).ConfigureAwait(false);
            await ValidateImportDirectoryAsync(stage, manifest, cancellationToken).ConfigureAwait(false);
            await ValidateRetainedLegacyMetaAsync(stage, manifest, cancellationToken).ConfigureAwait(false);

            var finalSources = recoverySetOnly
                ? InventoryRecoverySource(sourceBoundary, cancellationToken)
                : InventorySource(sourceBoundary, cancellationToken);
            if (!await MatchesCurrentSourcesAsync(
                    manifest,
                    finalSources,
                    sourceBoundary,
                    recoverySetOnly,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The legacy source path namespace changed before copy-first promotion.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _store.PromoteDirectory(stage, final);
            stage = null;
            return new LegacyCortexImportResult(
                LegacyCortexImportDisposition.Copied,
                manifest,
                CreateVerified(manifest),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalLimitExceededException exception)
        {
            return new LegacyCortexImportResult(
                LegacyCortexImportDisposition.Rejected,
                null,
                null,
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_import_limit_exceeded",
                    "The copy-first import exceeds a configured file, byte, metadata, or enumeration bound.",
                    "cortex.offline-journal.legacy-import",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["limit"] = exception.LimitName
                    },
                    cause: exception));
        }
        catch (LegacyCortexRecoveryContentRejectedException exception)
        {
            return new LegacyCortexImportResult(
                LegacyCortexImportDisposition.Rejected,
                null,
                null,
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_recovery_content_rejected",
                    "An approved recovery file contains prohibited raw, secret, credential, or private-reasoning content.",
                    "cortex.offline-journal.legacy-import",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["findings"] = string.Join(',', exception.FindingCodes)
                    }));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return new LegacyCortexImportResult(
                LegacyCortexImportDisposition.Rejected,
                null,
                null,
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_import_failed",
                    "The source could not be inventoried and copied without ambiguity.",
                    "cortex.offline-journal.legacy-import",
                    retryable: exception is IOException,
                    cause: exception));
        }
        finally
        {
            if (stage is not null)
            {
                try
                {
                    _store.DeleteOwnedStage(stage);
                }
                catch
                {
                    try
                    {
                        _store.QuarantineDirectory(stage, "i");
                    }
                    catch
                    {
                        // Source bytes remain untouched even if owned-stage recovery must be deferred.
                    }
                }
            }
        }
    }

    public async ValueTask<LegacyCortexExportResult> ExportPublicBundlesAsync(
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var boundManifest = verified.GetBoundManifest();
            if (!ValidExportRequest(verified, request))
            {
                return RejectExport(
                    "legacy_cortex_export_request_invalid",
                    "The legacy export request attempts to relabel verified scope or contains invalid bounds.");
            }

            // Reject retained-only oversized imports before opening or allocating any plaintext.
            LegacyCortexExportBuilder.Measure(
                boundManifest,
                _options.MaxLegacyExportPlaintextBytes,
                _options.MaxLegacyExportEvents);

            _store.EnsureLayout();
            using var writerLease = _store.AcquireWriterLock();
            var retained = await ReadAndValidateImportDirectoryAsync(
                _store.ImportPath(boundManifest.RunId),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(retained.ManifestSha256, boundManifest.ManifestSha256, StringComparison.Ordinal))
            {
                return RejectExport(
                    "legacy_cortex_export_manifest_mismatch",
                    "The opaque import proof does not identify the exact retained protected copy.");
            }
            var measure = LegacyCortexExportBuilder.Measure(
                retained,
                _options.MaxLegacyExportPlaintextBytes,
                _options.MaxLegacyExportEvents);
            var builder = new LegacyCortexExportBuilder(_identity, verified, request, measure.Events);

            for (var index = 0; index < retained.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = retained.Items[index];
                var bytes = await ReadVerifiedItemAsync(retained, item, index, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    builder.AddFile(item, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            return new LegacyCortexExportResult(true, builder.Complete(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalLimitExceededException exception)
        {
            return new LegacyCortexExportResult(
                false,
                [],
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_export_limit_exceeded",
                    "The retained import exceeds this bounded public-export invocation and remains preserved; paged export is required.",
                    "cortex.offline-journal.legacy-export",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["limit"] = exception.LimitName
                    },
                    cause: exception));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or CryptographicException)
        {
            return new LegacyCortexExportResult(
                false,
                [],
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_export_failed",
                    "The retained copy could not be authenticated and converted to typed public payload chunks.",
                    "cortex.offline-journal.legacy-export",
                    retryable: exception is IOException,
                    cause: exception));
        }
    }

    public async ValueTask<LegacyCortexPageExportResult> ExportPublicPageAsync(
        VerifiedLegacyCortexImport verified,
        LegacyCortexPageExportRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Export is null ||
                request.MaximumEvents is < 1 or > CortexSyncContractLimits.MaximumLegacyMigrationPageEvents ||
                !ValidExportRequest(verified, request.Export))
            {
                return RejectPage(
                    "legacy_cortex_page_request_invalid",
                    "The paged legacy export request is incomplete, relabels verified scope, or exceeds its event bound.");
            }

            var publicManifest = LegacyCortexImportAdapter.ToPublicManifest(verified, request.Export.Identity);
            var publicInventory = LegacyCortexImportAdapter.ToPublicInventory(publicManifest);
            var current = request.Cursor ?? CreateInitialCursor(publicManifest, publicInventory, request.Export);
            if (!ValidCursorForRequest(current, publicManifest, publicInventory, request.Export))
            {
                return RejectPage(
                    "legacy_cortex_page_cursor_invalid",
                    "The resume cursor is complete, tampered, stale, or does not match the exact retained migration and request chain.");
            }

            _store.EnsureLayout();
            using var writerLease = _store.AcquireWriterLock();
            var retained = await ReadAndValidateImportDirectoryAsync(
                _store.ImportPath(verified.GetBoundManifest().RunId),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(retained.ManifestSha256, verified.GetBoundManifest().ManifestSha256, StringComparison.Ordinal) ||
                current.NextFileIndex >= retained.Items.Count)
            {
                return RejectPage(
                    "legacy_cortex_page_manifest_mismatch",
                    "The cursor does not identify an available position in the exact retained protected migration copy.");
            }

            var builder = new LegacyCortexPageExportBuilder(
                _identity,
                verified,
                request.Export,
                current,
                publicInventory,
                request.MaximumEvents);
            while (!builder.IsFull && builder.NextFileIndex < retained.Items.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = builder.NextFileIndex;
                var item = retained.Items[index];
                var bytes = await ReadVerifiedItemAsync(retained, item, index, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    builder.AddAvailableChunks(index, item, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            return new LegacyCortexPageExportResult(
                true,
                builder.Complete(),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
                                           UnauthorizedAccessException or CryptographicException or ArithmeticException)
        {
            return new LegacyCortexPageExportResult(
                false,
                null,
                OfflineJournalDiagnostics.Error(
                    "legacy_cortex_page_export_failed",
                    "The retained migration page could not be authenticated and converted without changing the source.",
                    "cortex.offline-journal.legacy-page-export",
                    retryable: exception is IOException,
                    cause: exception));
        }
    }

    private IReadOnlyList<SourceFile> InventorySource(
        ILegacySourceFileSystemBoundary sourceBoundary,
        CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        var directories = new Queue<string>();
        directories.Enqueue(string.Empty);
        var discoveredEntries = 1;
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = directories.Dequeue();
            var remaining = _options.MaxLegacyImportFiles + 1 - discoveredEntries;
            if (remaining < 0)
                throw new JournalLimitExceededException("legacy_import_entries");
            foreach (var entry in sourceBoundary.EnumerateDirectory(relativeDirectory, remaining + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++discoveredEntries > _options.MaxLegacyImportFiles + 1)
                    throw new JournalLimitExceededException("legacy_import_entries");
                if (entry.Kind == JournalFileSystemEntryKind.Directory)
                {
                    directories.Enqueue(entry.RelativePath);
                    continue;
                }
                if (files.Count == _options.MaxLegacyImportFiles)
                    throw new JournalLimitExceededException("legacy_import_files");
                ValidateSourcePath(entry.ExactPath, entry.RelativePath);
                files.Add(new SourceFile(entry.ExactPath, entry.RelativePath));
            }
        }
        return files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<SourceFile> InventoryRecoverySource(
        ILegacySourceFileSystemBoundary sourceBoundary,
        CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>(ApprovedRecoveryFiles.Count);
        foreach (var entry in sourceBoundary.EnumerateDirectory(
                     string.Empty,
                     MaximumRecoveryRootEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ApprovedRecoveryFiles.Contains(entry.RelativePath))
                continue;
            if (entry.Kind != JournalFileSystemEntryKind.RegularFile)
            {
                throw new InvalidDataException(
                    "An approved recovery-set path is not a regular file.");
            }
            ValidateSourcePath(entry.ExactPath, entry.RelativePath);
            files.Add(new SourceFile(entry.ExactPath, entry.RelativePath));
        }

        if (!files.Any(item =>
                string.Equals(
                    item.RelativePath,
                    "meta.json",
                    StringComparison.Ordinal)) ||
            !files.Any(item =>
                item.RelativePath.EndsWith(
                    ".md",
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The approved recovery set requires meta.json and at least one recovery Markdown file.");
        }
        return files
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async ValueTask<LegacyCortexImportManifestV1> ReadAndValidateImportDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var (manifest, _) = await _store.ReadCanonicalAsync<LegacyCortexImportManifestV1>(
            Path.Combine(directory, JournalFileStore.ImportManifestFileName),
            _options.MaxLegacyManifestBytes,
            cancellationToken).ConfigureAwait(false);
        await ValidateImportDirectoryAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
        await ValidateRetainedLegacyMetaAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async ValueTask ValidateRetainedLegacyMetaAsync(
        string directory,
        LegacyCortexImportManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var matches = manifest.Items
            .Select((item, index) => (Item: item, Index: index))
            .Where(value => string.Equals(value.Item.RelativePath, "meta.json", comparison))
            .ToArray();
        if (matches.Length != 1 || matches[0].Item.SourceBytes is <= 0 or > int.MaxValue)
            throw new InvalidDataException("A verified legacy import requires one bounded non-empty meta.json.");

        var match = matches[0];
        using var plaintext = new MemoryStream((int)match.Item.SourceBytes);
        using var bounded = new BoundedHashWriteStream(plaintext, match.Item.SourceBytes, leaveOpen: true);
        await using (var protectedInput = _store.OpenReadFile(
                         Path.Combine(directory, match.Item.StoredFileName)))
        {
            await _protector.UnprotectAsync(
                protectedInput,
                bounded,
                match.Item.Protection,
                CreateProtectionContext(
                    manifest.RunId,
                    new SourceFile(match.Item.ExactSourcePath, match.Item.RelativePath),
                    match.Index + 1),
                cancellationToken).ConfigureAwait(false);
        }
        await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
        var sourceHash = bounded.CompleteHash();
        if (bounded.BytesWritten != match.Item.SourceBytes ||
            !string.Equals(sourceHash, match.Item.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The decrypted legacy meta.json does not match source provenance.");
        }
        var bytes = plaintext.ToArray();
        try
        {
            LegacyCortexMetaPolicy.Validate(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<bool> MatchesCurrentSourcesAsync(
        LegacyCortexImportManifestV1 manifest,
        IReadOnlyList<SourceFile> sources,
        ILegacySourceFileSystemBoundary sourceBoundary,
        bool requireRecoveryScreen,
        CancellationToken cancellationToken)
    {
        if (manifest.Items.Count != sources.Count)
            return false;
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index];
            var item = manifest.Items[index];
            if (!string.Equals(source.ExactPath, item.ExactSourcePath, StringComparison.Ordinal) ||
                !string.Equals(source.RelativePath, item.RelativePath, StringComparison.Ordinal))
            {
                return false;
            }
            var digest = await ComputeSourceDigestAsync(
                sourceBoundary,
                source.RelativePath,
                _options.MaxLegacyImportFileBytes,
                requireRecoveryScreen,
                cancellationToken).ConfigureAwait(false);
            if (digest.Bytes != item.SourceBytes || !string.Equals(digest.Sha256, item.SourceSha256, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private async ValueTask ValidateImportDirectoryAsync(
        string directory,
        LegacyCortexImportManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        var expectedFiles = manifest.Items.Select(item => item.StoredFileName)
            .Append(JournalFileStore.ImportManifestFileName)
            .ToArray();
        _store.EnsureExactFiles(directory, expectedFiles);
        long total = 0;
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = manifest.Items[index];
            var digest = await _store.ComputeDigestAsync(
                Path.Combine(directory, item.StoredFileName),
                _options.MaxLegacyImportFileBytes + 64 * 1024,
                cancellationToken,
                allowEmpty: true).ConfigureAwait(false);
            if (digest.Bytes != item.StoredBytes || !string.Equals(digest.Sha256, item.StoredSha256, StringComparison.Ordinal) ||
                item.SourceBytes < 0 || !IsSha256(item.SourceSha256))
            {
                throw new InvalidDataException("A protected legacy copy does not match its immutable storage digest.");
            }
            await using var protectedInput = _store.OpenReadFile(
                Path.Combine(directory, item.StoredFileName));
            var source = new SourceFile(item.ExactSourcePath, item.RelativePath);
            if (!await _protector.VerifyAsync(
                    protectedInput,
                    item.Protection,
                    CreateProtectionContext(manifest.RunId, source, index + 1),
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The host rejected a protected legacy copy.");
            }
            total = checked(total + item.SourceBytes);
        }
        if (total != manifest.SourceBytes)
            throw new InvalidDataException("The legacy import aggregate byte count is invalid.");
    }

    private static async ValueTask<FileDigest> ComputeSourceDigestAsync(
        ILegacySourceFileSystemBoundary sourceBoundary,
        string relativePath,
        long maximumBytes,
        bool requireRecoveryScreen,
        CancellationToken cancellationToken)
    {
        await using var input = sourceBoundary.OpenReadFile(
            relativePath,
            64 * 1024,
            asynchronous: true,
            sequentialScan: true);
        if (input.Length < 0 || input.Length > maximumBytes)
            throw new JournalLimitExceededException("legacy_import_file_bytes");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        byte[]? recoveryBytes = null;
        try
        {
            if (requireRecoveryScreen)
                recoveryBytes = GC.AllocateUninitializedArray<byte>((int)input.Length);
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (total > maximumBytes - read)
                    throw new JournalLimitExceededException("legacy_import_file_bytes");
                if (recoveryBytes is not null)
                {
                    if (total > recoveryBytes.Length - read)
                    {
                        throw new IOException(
                            "A recovery source changed size while it was screened.");
                    }
                    buffer.AsSpan(0, read).CopyTo(
                        recoveryBytes.AsSpan((int)total, read));
                }
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            if (total != input.Length)
                throw new IOException("A legacy source changed while its digest was captured.");
            if (recoveryBytes is not null)
            {
                LegacyCortexRecoveryContentPolicy.RequireAccepted(
                    relativePath,
                    recoveryBytes);
            }
            return new FileDigest(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            if (recoveryBytes is not null)
                CryptographicOperations.ZeroMemory(recoveryBytes);
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateManifest(LegacyCortexImportManifestV1 manifest)
    {
        if (manifest.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(manifest.ContractId, OfflineJournalContractIds.LegacyImport, StringComparison.Ordinal) ||
            !string.Equals(manifest.Canonicalization, OfflineJournalContractIds.Canonicalization, StringComparison.Ordinal) ||
            !JournalPathPolicy.IsPortableIdentifier(manifest.RunId, _options.MaxIdentifierCharacters) ||
            !Path.IsPathFullyQualified(manifest.ExactSourceRoot) || manifest.ExactSourceRoot.Length > CortexSyncContractLimits.MaximumExactSourcePathCharacters ||
            manifest.CapturedAt.Offset != TimeSpan.Zero || manifest.Items.Count is 0 ||
            manifest.Items.Count > _options.MaxLegacyImportFiles || manifest.SourceBytes < 0 ||
            manifest.SourceBytes > _options.MaxLegacyImportTotalBytes ||
            !string.Equals(manifest.ContentTreatment, OfflineJournalContractIds.ContentTreatment, StringComparison.Ordinal) ||
            manifest.Executable || !IsSha256(manifest.ManifestSha256) ||
            !string.Equals(manifest.ManifestSha256, ComputeManifestHash(manifest), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The copy-first legacy import manifest is invalid or modified.");
        }

        var relativePaths = new HashSet<string>(StringComparer.Ordinal);
        var storedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var item = manifest.Items[index];
            ValidateSourcePath(item.ExactSourcePath, item.RelativePath);
            if (!relativePaths.Add(item.RelativePath) || !storedNames.Add(item.StoredFileName) ||
                !JournalPathPolicy.IsPortableIdentifier(item.StoredFileName, 32) ||
                !string.Equals(item.StoredFileName, $"f{index + 1:D6}.bin", StringComparison.Ordinal) ||
                item.SourceBytes < 0 || item.SourceBytes > _options.MaxLegacyImportFileBytes ||
                item.StoredBytes < 0 || item.StoredBytes > _options.MaxLegacyImportFileBytes + 64 * 1024 ||
                !IsSha256(item.SourceSha256) || !IsSha256(item.StoredSha256) ||
                !string.Equals(item.Disposition, "copied", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A copy-first legacy import item is invalid or duplicated.");
            }
            ValidateProtectionMetadata(item.Protection);
            var expectedExactPath = Path.GetFullPath(Path.Combine(
                manifest.ExactSourceRoot,
                item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.Equals(expectedExactPath, item.ExactSourcePath, StringComparison.Ordinal))
                throw new InvalidDataException("Legacy source provenance does not resolve beneath its captured source root.");
        }
    }

    private static void EnsureRootsDoNotOverlap(string sourceRoot, string journalRoot)
    {
        var sourcePrefix = sourceRoot + Path.DirectorySeparatorChar;
        var journalPrefix = journalRoot + Path.DirectorySeparatorChar;
        if (string.Equals(sourceRoot, journalRoot, PathComparison) ||
            sourceRoot.StartsWith(journalPrefix, PathComparison) ||
            journalRoot.StartsWith(sourcePrefix, PathComparison))
        {
            throw new IOException("Legacy source and owned journal roots cannot overlap.");
        }
    }

    private static void ValidateSourcePath(string exactPath, string relativePath)
    {
        if (!Path.IsPathFullyQualified(exactPath) || exactPath.Length > CortexSyncContractLimits.MaximumExactSourcePathCharacters ||
            string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > CortexSyncContractLimits.MaximumRelativePathCharacters ||
            Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split('/', StringSplitOptions.None).Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("Legacy source provenance contains an unsafe or unbounded path.");
        }
    }

    private static string ComputeManifestHash(LegacyCortexImportManifestV1 manifest) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(manifest with
        {
            ManifestSha256 = OfflineJournalContractIds.EmptySha256
        }));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private JournalPayloadProtectionContext CreateProtectionContext(
        string runId,
        SourceFile source,
        int sequence)
    {
        var associatedData = CortexCanonicalJson.Serialize(new LegacyProtectionAssociatedDataV1(
            CortexSyncContractIds.SchemaVersion,
            "pegasus.cortex.legacy-copy-associated-data/1.0",
            OfflineJournalContractIds.Canonicalization,
            _identity.JournalId,
            _identity.WriterId,
            _identity.StoreId,
            _identity.ProjectId,
            runId,
            sequence,
            source.RelativePath));
        var idempotencyHash = CortexCanonicalJson.Sha256($"{runId}:{sequence}");
        return new JournalPayloadProtectionContext(
            _identity.JournalId,
            _identity.WriterId,
            _identity.StoreId,
            _identity.ProjectId,
            sequence,
            $"legacy-{idempotencyHash[..32]}",
            "legacy.copy",
            CortexSyncContractIds.LegacyFileCopyPayload,
            null,
            null,
            OfflineJournalContractIds.EmptySha256,
            associatedData);
    }

    private void ValidateProtectionMetadata(JournalPayloadProtectionMetadata metadata)
        => JournalPayloadProtectionPolicy.ValidateMetadata(
            metadata,
            _options.MaxIdentifierCharacters);

    private bool ValidExportRequest(
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request) =>
        request.Identity is not null &&
        string.Equals(verified.StoreId, _identity.StoreId, StringComparison.Ordinal) &&
        string.Equals(verified.ProjectId, _identity.ProjectId, StringComparison.Ordinal) &&
        string.Equals(request.Identity.StoreId, verified.StoreId, StringComparison.Ordinal) &&
        string.Equals(request.Identity.ProjectId, verified.ProjectId, StringComparison.Ordinal) &&
        ValidSyncIdentifier(request.Identity.StoreEpoch) &&
        ValidSyncIdentifier(request.Identity.SourceRootId) &&
        JournalPathPolicy.IsPortableIdentifier(
            request.BundleIdPrefix,
            _options.MaxIdentifierCharacters - 17) &&
        ValidSyncIdentifier(request.TenantIdHint) &&
        request.FirstSequence is >= 1 and < CortexSyncContractLimits.MaximumSequence &&
        request.CreatedAt.Offset == TimeSpan.Zero &&
        request.CreatedAt >= verified.GetBoundManifest().CapturedAt &&
        request.ObservedPolicyRevision is > 0 &&
        (request.BaseServerRevision is null || ValidSyncIdentifier(request.BaseServerRevision)) &&
        IsSha256(request.PreviousBundleHash) && IsSha256(request.PriorEventHash);

    private static CortexLegacyMigrationCursorV1 CreateInitialCursor(
        CortexLegacyMigrationManifestV1 manifest,
        CortexLegacyMigrationInventoryV1 inventory,
        LegacyCortexExportRequest request) =>
        CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationCursor,
            CortexSyncContractIds.Canonicalization,
            manifest.MigrationId,
            manifest.ManifestHash,
            inventory.InventoryHash,
            inventory.Files.Count,
            manifest.StoreId,
            manifest.StoreEpoch,
            manifest.ProjectId,
            manifest.SourceRootId,
            1,
            0,
            0,
            request.FirstSequence,
            request.PreviousBundleHash,
            request.PriorEventHash,
            false,
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));

    private static bool ValidCursorForRequest(
        CortexLegacyMigrationCursorV1 cursor,
        CortexLegacyMigrationManifestV1 manifest,
        CortexLegacyMigrationInventoryV1 inventory,
        LegacyCortexExportRequest request) =>
        CortexSyncContractPolicy.HasValidHash(cursor) && !cursor.Completed &&
        CortexSyncContractPolicy.HasValidHash(inventory) &&
        string.Equals(cursor.MigrationId, manifest.MigrationId, StringComparison.Ordinal) &&
        string.Equals(cursor.SourceManifestHash, manifest.ManifestHash, StringComparison.Ordinal) &&
        string.Equals(cursor.SourceInventoryHash, inventory.InventoryHash, StringComparison.Ordinal) &&
        cursor.SourceFileCount == inventory.Files.Count &&
        string.Equals(cursor.StoreId, manifest.StoreId, StringComparison.Ordinal) &&
        string.Equals(cursor.StoreEpoch, manifest.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(cursor.ProjectId, manifest.ProjectId, StringComparison.Ordinal) &&
        string.Equals(cursor.SourceRootId, manifest.SourceRootId, StringComparison.Ordinal) &&
        cursor.NextSequence == request.FirstSequence &&
        string.Equals(cursor.PreviousBundleHash, request.PreviousBundleHash, StringComparison.Ordinal) &&
        string.Equals(cursor.PriorEventHash, request.PriorEventHash, StringComparison.Ordinal);

    private async ValueTask<byte[]> ReadVerifiedItemAsync(
        LegacyCortexImportManifestV1 retained,
        LegacyCortexImportItemV1 item,
        int index,
        CancellationToken cancellationToken)
    {
        if (item.SourceBytes > int.MaxValue)
            throw new JournalLimitExceededException("legacy_export_file_bytes");
        var bytes = GC.AllocateUninitializedArray<byte>((int)item.SourceBytes);
        try
        {
            using var plaintext = new MemoryStream(
                bytes,
                0,
                bytes.Length,
                writable: true,
                publiclyVisible: true);
            using var bounded = new BoundedHashWriteStream(plaintext, item.SourceBytes, leaveOpen: true);
            await using (var protectedInput = _store.OpenReadFile(
                             Path.Combine(_store.ImportPath(retained.RunId), item.StoredFileName)))
            {
                await _protector.UnprotectAsync(
                    protectedInput,
                    bounded,
                    item.Protection,
                    CreateProtectionContext(
                        retained.RunId,
                        new SourceFile(item.ExactSourcePath, item.RelativePath),
                        index + 1),
                    cancellationToken).ConfigureAwait(false);
            }
            await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (bounded.BytesWritten != item.SourceBytes ||
                !string.Equals(bounded.CompleteHash(), item.SourceSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A decrypted legacy copy does not match verified source provenance.");
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private bool ValidSyncIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= _options.MaxIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static LegacyCortexImportResult Reject(string code, string message) =>
        new(
            LegacyCortexImportDisposition.Rejected,
            null,
            null,
            OfflineJournalDiagnostics.Error(code, message, "cortex.offline-journal.legacy-import"));

    private static LegacyCortexExportResult RejectExport(string code, string message) =>
        new(
            false,
            [],
            OfflineJournalDiagnostics.Error(code, message, "cortex.offline-journal.legacy-export"));

    private static LegacyCortexPageExportResult RejectPage(string code, string message) =>
        new(
            false,
            null,
            OfflineJournalDiagnostics.Error(code, message, "cortex.offline-journal.legacy-page-export"));

    private VerifiedLegacyCortexImport CreateVerified(LegacyCortexImportManifestV1 manifest) =>
        new(manifest, _identity.StoreId, _identity.ProjectId);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record SourceFile(string ExactPath, string RelativePath);

    private sealed record LegacyProtectionAssociatedDataV1(
        int SchemaVersion,
        string ContractId,
        string Canonicalization,
        string JournalId,
        string WriterId,
        string StoreId,
        string ProjectId,
        string RunId,
        int Sequence,
        string RelativePath);
}
