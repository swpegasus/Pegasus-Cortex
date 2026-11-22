using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.IO;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.OfflineJournal.Security;
using Pegasus.Shared.Diagnostics;
using System.Security.Cryptography;

namespace Pegasus.Cortex.OfflineJournal.Journal;

public sealed class OfflineJournal : IAsyncDisposable
{
    private readonly OfflineJournalIdentity _identity;
    private readonly OfflineJournalOptions _options;
    private readonly IJournalPayloadProtector _protector;
    private readonly IJournalReceiptVerifier _receiptVerifier;
    private readonly JournalFileStore _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly List<JournalEntryMetadataV1> _entries = [];
    private readonly Dictionary<string, JournalEntryMetadataV1> _entriesById = new(StringComparer.Ordinal);
    private readonly Dictionary<long, JournalEntryMetadataV1> _entriesBySequence = [];
    private readonly Dictionary<string, JournalEntryMetadataV1> _idempotency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedEntryIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalEntrySyncStatus> _syncStatuses = new(StringComparer.Ordinal);
    private OfflineJournalStateV1? _state;
    private bool _loaded;
    private bool _ready;
    private long _pendingBytes;
    private int _pendingCount;
    private long _retainedBytes;
    private int _legacyEvidenceReceiptCount;
    private int _typedReceiptCount;
    private bool _disposed;

    internal OfflineJournal(
        string requestedRoot,
        string approvedRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        IJournalReceiptVerifier receiptVerifier,
        OfflineJournalOptions? options = null)
        : this(
            requestedRoot,
            approvedRoot,
            identity,
            protector,
            receiptVerifier,
            LexicalTestOnlyJournalFileSystemBoundaryFactory.Instance,
            options,
            allowLexicalTestOnly: true)
    {
    }

    public OfflineJournal(
        string requestedRoot,
        string approvedRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        IJournalReceiptVerifier receiptVerifier,
        IJournalFileSystemBoundaryFactory fileSystemBoundaryFactory,
        OfflineJournalOptions? options = null)
        : this(
            requestedRoot,
            approvedRoot,
            identity,
            protector,
            receiptVerifier,
            fileSystemBoundaryFactory,
            options,
            allowLexicalTestOnly: false)
    {
    }

    private OfflineJournal(
        string requestedRoot,
        string approvedRoot,
        OfflineJournalIdentity identity,
        IJournalPayloadProtector protector,
        IJournalReceiptVerifier receiptVerifier,
        IJournalFileSystemBoundaryFactory fileSystemBoundaryFactory,
        OfflineJournalOptions? options,
        bool allowLexicalTestOnly)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(receiptVerifier);
        _options = options ?? new OfflineJournalOptions();
        _options.Validate();
        if (!ValidIdentifier(identity.JournalId) || !ValidIdentifier(identity.WriterId) ||
            !ValidIdentifier(identity.StoreId) || !ValidIdentifier(identity.ProjectId))
        {
            throw new ArgumentException(
                "Journal, writer, store, and project identifiers must use the bounded sync identifier syntax.",
                nameof(identity));
        }
        _identity = identity with { };
        _protector = protector;
        _receiptVerifier = receiptVerifier;
        var opened = JournalFileSystemBoundaryGate.OpenJournal(
            fileSystemBoundaryFactory,
            requestedRoot,
            approvedRoot,
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

    public JournalDurabilityCapabilities Durability => _store.Durability;

    public async ValueTask<JournalAppendResult> AppendAsync(
        JournalAppendRequest request,
        Stream plaintext,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plaintext);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        var stateReserved = false;
        try
        {
            var requestDiagnostic = ValidateAppendRequest(request, plaintext);
            if (requestDiagnostic is not null)
                return RejectedAppend(requestDiagnostic);
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;

            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
            {
                return RejectedAppend(OfflineJournalDiagnostics.Error(
                    "offline_journal_recovery_required",
                    "The journal contains quarantined or inconsistent state and cannot append.",
                    "cortex.offline-journal.append"));
            }

            var existingByKey = _idempotency.GetValueOrDefault(request.IdempotencyKey);
            if (existingByKey is null &&
                (_pendingCount >= _options.MaxPendingEntries ||
                 _pendingBytes > _options.MaxPendingBytes - _options.MaxProtectedPayloadBytes ||
                 _entries.Count >= _options.MaxRetainedEntries ||
                 _retainedBytes > _options.MaxRetainedBytes - _options.MaxProtectedPayloadBytes - 2L * _options.MaxMetadataBytes))
            {
                return RejectedAppend(OfflineJournalDiagnostics.Error(
                    "offline_journal_pending_quota_exceeded",
                    "The pending journal quota cannot reserve another maximum-size protected entry.",
                    "cortex.offline-journal.append",
                    context: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["pendingEntries"] = _pendingCount.ToString(),
                        ["pendingBytes"] = _pendingBytes.ToString(),
                        ["retainedEntries"] = _entries.Count.ToString(),
                        ["retainedBytes"] = _retainedBytes.ToString()
                    }));
            }

            var sequence = checked((_state?.HighWaterSequence ?? 0) + 1);
            if (sequence > CortexSyncContractLimits.MaximumSequence)
                return RejectedAppend(OfflineJournalDiagnostics.Error(
                    "offline_journal_sequence_exhausted",
                    "The portable v1 sequence limit has been reached.",
                    "cortex.offline-journal.append"));
            var previousHash = _state?.LastEntrySha256 ?? OfflineJournalContractIds.EmptySha256;
            var protectionContext = CreateProtectionContext(request, sequence, previousHash);

            stage = _store.CreateStageDirectory("e", _options.MaxStagingArtifacts);
            var payloadPath = Path.Combine(stage, JournalFileStore.PayloadFileName);
            JournalPayloadProtectionMetadata protection;
            string plaintextHash;
            string protectedHash;
            long plaintextBytes;
            long protectedBytes;
            await using (var output = _store.CreatePrivateFile(payloadPath))
            await using (var boundedOutput = new BoundedHashWriteStream(output, _options.MaxProtectedPayloadBytes, leaveOpen: true))
            using (var boundedInput = new BoundedHashReadStream(plaintext, _options.MaxPlaintextBytes, request.DeclaredPayloadBytes))
            {
                protection = await _protector.ProtectAsync(
                    boundedInput,
                    boundedOutput,
                    protectionContext,
                    cancellationToken).ConfigureAwait(false);
                plaintextHash = boundedInput.CompleteHash();
                protectedHash = boundedOutput.CompleteHash();
                plaintextBytes = boundedInput.BytesRead;
                protectedBytes = boundedOutput.BytesWritten;
                if (plaintextBytes == 0 || protectedBytes == 0)
                    throw new InvalidDataException("Offline journal payloads cannot be empty.");
                ValidateProtectionMetadata(protection);
                await boundedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                boundedOutput.FlushToDisk();
            }

            if (existingByKey is not null)
            {
                _store.DeleteOwnedStage(stage);
                stage = null;
                return Equivalent(existingByKey, request, plaintextBytes, plaintextHash)
                    ? new JournalAppendResult(JournalMutationDisposition.Existing, existingByKey, null)
                    : new JournalAppendResult(
                        JournalMutationDisposition.Conflict,
                        existingByKey,
                        OfflineJournalDiagnostics.Error(
                            "offline_journal_idempotency_conflict",
                            "The idempotency key already identifies different immutable content.",
                            "cortex.offline-journal.append",
                            context: EntryContext(existingByKey)));
            }

            var entryId = $"evt-{sequence:D16}";
            var entry = new JournalEntryMetadataV1(
                CortexSyncContractIds.SchemaVersion,
                OfflineJournalContractIds.Entry,
                OfflineJournalContractIds.Canonicalization,
                _identity.JournalId,
                _identity.WriterId,
                sequence,
                entryId,
                request.IdempotencyKey,
                request.Operation,
                request.PayloadContractId,
                request.BaseRevisionId,
                request.BaseRevisionSha256,
                request.CreatedAt,
                plaintextBytes,
                plaintextHash,
                protectedBytes,
                protectedHash,
                protection,
                previousHash,
                OfflineJournalContractIds.ContentTreatment,
                false,
                OfflineJournalContractIds.EmptySha256);
            entry = entry with { EntrySha256 = ComputeEntryHash(entry) };
            var metadataPath = Path.Combine(stage, JournalFileStore.MetadataFileName);
            await _store.WriteCanonicalAsync(
                metadataPath,
                entry,
                _options.MaxMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            await ValidateEntryDirectoryAsync(stage, cancellationToken).ConfigureAwait(false);

            var directoryName = EntryDirectoryName(entry);
            var finalPath = _store.PendingPath(directoryName);
            if (_store.DirectoryExists(finalPath) || _store.FileExists(finalPath))
            {
                return new JournalAppendResult(
                    JournalMutationDisposition.Conflict,
                    null,
                    OfflineJournalDiagnostics.Error(
                        "offline_journal_destination_conflict",
                        "A different artifact already occupies the deterministic pending path.",
                        "cortex.offline-journal.append",
                        context: EntryContext(entry)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var nextState = OfflineJournalStatePolicy.Advance(_state!, entry);
            await PersistStateCheckpointAsync(nextState, cancellationToken).ConfigureAwait(false);
            stateReserved = true;
            _store.PromoteDirectory(stage, finalPath);
            stage = null;
            AddLoadedEntry(entry);
            return new JournalAppendResult(JournalMutationDisposition.Added, entry, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalLimitExceededException exception)
        {
            return RejectedAppend(OfflineJournalDiagnostics.Error(
                "offline_journal_limit_exceeded",
                "The journal operation exceeded a configured resource bound.",
                "cortex.offline-journal.append",
                context: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["limit"] = exception.LimitName
                },
                cause: exception));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return RejectedAppend(OfflineJournalDiagnostics.Error(
                "offline_journal_append_failed",
                "The offline entry could not be verified and atomically committed.",
                "cortex.offline-journal.append",
                retryable: exception is IOException,
                cause: exception));
        }
        finally
        {
            if (stage is not null && !stateReserved)
                CleanupStage(stage);
            _mutationGate.Release();
            if (!leaveOpen)
                await plaintext.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask<JournalAcknowledgmentResult> RecordAcknowledgmentAsync(
        JournalAcknowledgmentRequest request,
        Stream serverEvidence,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serverEvidence);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            var validation = ValidateAcknowledgmentRequest(request, serverEvidence);
            if (validation is not null)
                return RejectedAcknowledgment(validation);
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
                return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                    "offline_journal_recovery_required",
                    "The journal must recover successfully before recording an acknowledgment.",
                    "cortex.offline-journal.acknowledge"));

            var entry = _entriesById.GetValueOrDefault(request.EntryId);
            if (entry is null || entry.Sequence != request.Sequence ||
                !string.Equals(entry.EntrySha256, request.EntrySha256, StringComparison.Ordinal))
            {
                return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                    "offline_journal_ack_entry_mismatch",
                    "The acknowledgment does not identify an exact pending journal entry.",
                    "cortex.offline-journal.acknowledge"));
            }

            if (!_acknowledgedEntryIds.Contains(entry.EntryId) &&
                _legacyEvidenceReceiptCount >= _options.MaxLegacyEvidenceReceipts)
            {
                return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                    "offline_journal_legacy_receipt_count_exceeded",
                    "The retained legacy evidence-receipt count is at its configured recovery-safe limit.",
                    "cortex.offline-journal.acknowledge"));
            }

            if (_retainedBytes > _options.MaxRetainedBytes - _options.MaxReceiptEvidenceBytes - _options.MaxMetadataBytes)
            {
                return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                    "offline_journal_retained_quota_exceeded",
                    "The retained journal quota cannot reserve the bounded receipt evidence.",
                    "cortex.offline-journal.acknowledge"));
            }

            stage = _store.CreateStageDirectory("r", _options.MaxStagingArtifacts);
            var evidencePath = Path.Combine(stage, JournalFileStore.EvidenceFileName);
            var evidence = await _store.CopyBoundedAsync(
                serverEvidence,
                evidencePath,
                _options.MaxReceiptEvidenceBytes,
                request.DeclaredEvidenceBytes,
                cancellationToken).ConfigureAwait(false);

            await using (var verificationInput = _store.OpenReadFile(evidencePath))
            {
                if (!await _receiptVerifier.VerifyAsync(
                        verificationInput,
                        request,
                        entry,
                        cancellationToken).ConfigureAwait(false))
                {
                    return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                        "offline_journal_ack_proof_invalid",
                        "The host rejected the server acknowledgment proof.",
                        "cortex.offline-journal.acknowledge"));
                }
            }

            var receipt = new JournalReceiptV1(
                CortexSyncContractIds.SchemaVersion,
                OfflineJournalContractIds.Receipt,
                OfflineJournalContractIds.Canonicalization,
                _identity.JournalId,
                _identity.WriterId,
                request.ReceiptId,
                request.AuthorityId,
                request.EntryId,
                request.Sequence,
                request.EntrySha256,
                request.ServerRevisionId,
                request.ServerProofAlgorithm,
                request.ServerProofKeyId,
                request.AcceptedAt,
                request.Disposition,
                evidence.Bytes,
                evidence.Sha256,
                OfflineJournalContractIds.ContentTreatment,
                false,
                OfflineJournalContractIds.EmptySha256);
            receipt = receipt with { ReceiptSha256 = ComputeReceiptHash(receipt) };
            await _store.WriteCanonicalAsync(
                Path.Combine(stage, JournalFileStore.ReceiptFileName),
                receipt,
                _options.MaxMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            await ValidateReceiptDirectoryAsync(stage, entry, request, verifyProof: false, cancellationToken).ConfigureAwait(false);

            var finalPath = _store.ReceiptPath(entry.EntryId);
            if (_store.DirectoryExists(finalPath))
            {
                var existing = await ValidateReceiptDirectoryAsync(
                    finalPath,
                    entry,
                    request,
                    verifyProof: true,
                    cancellationToken).ConfigureAwait(false);
                _store.DeleteOwnedStage(stage);
                stage = null;
                return string.Equals(existing.ReceiptSha256, receipt.ReceiptSha256, StringComparison.Ordinal)
                    ? ExistingAcknowledgment(existing, entry)
                    : new JournalAcknowledgmentResult(
                        JournalMutationDisposition.Conflict,
                        existing,
                        OfflineJournalDiagnostics.Error(
                            "offline_journal_ack_conflict",
                            "A different durable receipt already acknowledges this entry.",
                            "cortex.offline-journal.acknowledge",
                            context: EntryContext(entry)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _store.PromoteDirectory(stage, finalPath);
            stage = null;
            AddLoadedReceipt(receipt, entry);
            return new JournalAcknowledgmentResult(JournalMutationDisposition.Added, receipt, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalLimitExceededException exception)
        {
            return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                "offline_journal_ack_limit_exceeded",
                "The acknowledgment exceeds a configured resource bound.",
                "cortex.offline-journal.acknowledge",
                context: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["limit"] = exception.LimitName
                },
                cause: exception));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return RejectedAcknowledgment(OfflineJournalDiagnostics.Error(
                "offline_journal_ack_failed",
                "The server acknowledgment could not be verified and atomically recorded.",
                "cortex.offline-journal.acknowledge",
                retryable: exception is IOException,
                cause: exception));
        }
        finally
        {
            if (stage is not null)
                CleanupStage(stage);
            _mutationGate.Release();
            if (!leaveOpen)
                await serverEvidence.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<JournalExportResult> ExportPendingAsync(
        JournalExportRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var plaintextCopies = new List<byte[]>();
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
            {
                return RejectedExport(OfflineJournalDiagnostics.Error(
                    "offline_journal_recovery_required",
                    "The journal must recover successfully before exporting public synchronization events.",
                    "cortex.offline-journal.export"));
            }
            if (request.Limit is < 1 || request.Limit > _options.MaxExportEntries ||
                request.CreatedAt.Offset != TimeSpan.Zero || !ValidIdentifier(request.BundleId) ||
                !ValidIdentifier(request.TenantIdHint) || string.IsNullOrWhiteSpace(request.NamespaceHint) ||
                request.ObservedPolicyRevision is <= 0 ||
                request.BaseServerRevision is not null && !ValidIdentifier(request.BaseServerRevision) ||
                !IsSha256(request.PreviousBundleHash) || !IsSha256(request.PriorEventHash))
            {
                return RejectedExport(OfflineJournalDiagnostics.Error(
                    "offline_journal_export_invalid",
                    "The export request contains invalid bounds, identifiers, time, policy, or chain hashes.",
                    "cortex.offline-journal.export"));
            }

            var selected = _entries
                .Where(item => _syncStatuses.GetValueOrDefault(item.EntryId)?.State == JournalEntrySyncState.Pending)
                .Take(request.Limit)
                .ToArray();
            if (selected.Length == 0)
            {
                return RejectedExport(OfflineJournalDiagnostics.Error(
                    "offline_journal_export_empty",
                    "No upload-pending entries are available for export.",
                    "cortex.offline-journal.export"));
            }
            if (selected.Zip(selected.Skip(1), (left, right) => right.Sequence == left.Sequence + 1).Any(value => !value) ||
                selected.Sum(item => item.PlaintextBytes) > _options.MaxExportPlaintextBytes)
            {
                return RejectedExport(OfflineJournalDiagnostics.Error(
                    "offline_journal_export_bound_exceeded",
                    "The selected upload-pending range is not contiguous or exceeds the configured plaintext bound.",
                    "cortex.offline-journal.export"));
            }

            var decrypted = new List<DecryptedJournalEntry>(selected.Length);
            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.PlaintextBytes > int.MaxValue)
                    throw new JournalLimitExceededException("export_entry_bytes");
                using var plaintext = new MemoryStream((int)entry.PlaintextBytes);
                using var bounded = new BoundedHashWriteStream(plaintext, entry.PlaintextBytes, leaveOpen: true);
                await using (var protectedPayload = _store.OpenReadFile(
                                 Path.Combine(_store.PendingPath(EntryDirectoryName(entry)), JournalFileStore.PayloadFileName)))
                {
                    await _protector.UnprotectAsync(
                        protectedPayload,
                        bounded,
                        entry.Protection,
                        CreateProtectionContext(entry),
                        cancellationToken).ConfigureAwait(false);
                }
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                var plaintextHash = bounded.CompleteHash();
                if (bounded.BytesWritten != entry.PlaintextBytes ||
                    !string.Equals(plaintextHash, entry.PlaintextSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The decrypted payload does not match immutable journal metadata.");
                }
                var copy = plaintext.ToArray();
                plaintextCopies.Add(copy);
                decrypted.Add(new DecryptedJournalEntry(entry, copy));
            }

            var bundle = OfflineJournalExportPolicy.BuildBundle(_identity, _state!, request, decrypted);
            return new JournalExportResult(true, bundle, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JournalLimitExceededException exception)
        {
            return RejectedExport(OfflineJournalDiagnostics.Error(
                "offline_journal_export_limit_exceeded",
                "The export exceeded a configured resource bound.",
                "cortex.offline-journal.export",
                context: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["limit"] = exception.LimitName
                },
                cause: exception));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or CryptographicException)
        {
            return RejectedExport(OfflineJournalDiagnostics.Error(
                "offline_journal_export_failed",
                "A retained payload could not be authenticated and reconstructed as a public v1 event.",
                "cortex.offline-journal.export",
                retryable: exception is IOException,
                cause: exception));
        }
        finally
        {
            foreach (var copy in plaintextCopies)
                CryptographicOperations.ZeroMemory(copy);
            _mutationGate.Release();
        }
    }

    public async ValueTask<JournalTypedAcknowledgmentResult> RecordTypedAcknowledgmentAsync(
        CortexOfflineBundleV1 bundle,
        CortexSyncReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(receipt);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
                return RejectedTypedAcknowledgment("The journal must recover before recording a typed receipt.");

            var proofVerified = await _receiptVerifier.VerifyAsync(receipt, bundle, cancellationToken).ConfigureAwait(false);
            var projection = OfflineJournalTypedReceiptPolicy.Evaluate(
                _identity,
                _state!,
                receipt,
                bundle,
                proofVerified,
                _entriesBySequence);
            if (!projection.Accepted)
                return RejectedTypedAcknowledgment(projection.Failure ?? "The typed receipt was rejected.");

            var bundleBytes = CortexCanonicalJson.Serialize(bundle);
            var receiptBytes = CortexCanonicalJson.Serialize(receipt);
            if (bundleBytes.Length > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
                receiptBytes.Length > _options.MaxReceiptEvidenceBytes ||
                _retainedBytes > _options.MaxRetainedBytes - bundleBytes.Length - receiptBytes.Length)
            {
                return RejectedTypedAcknowledgment("The typed receipt evidence exceeds configured retained bounds.");
            }

            var final = _store.PublicReceiptPath(receipt.ReceiptId);
            if (_store.DirectoryExists(final))
            {
                var existing = await ValidateTypedReceiptDirectoryAsync(final, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existing.Bundle.BundleHash, bundle.BundleHash, StringComparison.Ordinal) ||
                    !string.Equals(existing.Receipt.ReceiptHash, receipt.ReceiptHash, StringComparison.Ordinal))
                {
                    return new JournalTypedAcknowledgmentResult(
                        JournalMutationDisposition.Conflict,
                        false,
                        false,
                        existing.Receipt,
                        OfflineJournalDiagnostics.Error(
                            "offline_journal_typed_receipt_conflict",
                            "A different typed receipt occupies the deterministic receipt path.",
                            "cortex.offline-journal.typed-receipt"));
                }
                ApplyTypedProjection(existing.Projection);
                return new JournalTypedAcknowledgmentResult(
                    JournalMutationDisposition.Existing,
                    true,
                    existing.Projection.Reconciled,
                    existing.Receipt,
                    null);
            }
            if (_typedReceiptCount >= _options.MaxTypedReceipts)
            {
                return new JournalTypedAcknowledgmentResult(
                    JournalMutationDisposition.Rejected,
                    false,
                    false,
                    null,
                    OfflineJournalDiagnostics.Error(
                        "offline_journal_typed_receipt_count_exceeded",
                        "The retained typed-receipt count is at its configured recovery-safe limit.",
                        "cortex.offline-journal.typed-receipt"));
            }

            stage = _store.CreateStageDirectory("a", _options.MaxStagingArtifacts);
            await _store.WriteCanonicalAsync(
                Path.Combine(stage, JournalFileStore.PublicBundleFileName),
                bundle,
                CortexSyncContractLimits.MaximumCanonicalBundleBytes,
                cancellationToken).ConfigureAwait(false);
            await _store.WriteCanonicalAsync(
                Path.Combine(stage, JournalFileStore.PublicReceiptFileName),
                receipt,
                checked((int)_options.MaxReceiptEvidenceBytes),
                cancellationToken).ConfigureAwait(false);
            var validated = await ValidateTypedReceiptDirectoryAsync(stage, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            _store.PromoteDirectory(stage, final);
            stage = null;
            _retainedBytes = checked(_retainedBytes + validated.Bytes);
            _typedReceiptCount++;
            ApplyTypedProjection(validated.Projection);
            return new JournalTypedAcknowledgmentResult(
                JournalMutationDisposition.Added,
                true,
                validated.Projection.Reconciled,
                validated.Receipt,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or CryptographicException)
        {
            return new JournalTypedAcknowledgmentResult(
                JournalMutationDisposition.Rejected,
                false,
                false,
                null,
                OfflineJournalDiagnostics.Error(
                    "offline_journal_typed_receipt_failed",
                    "The typed receipt could not be authenticated, bound, and durably recorded.",
                    "cortex.offline-journal.typed-receipt",
                    retryable: exception is IOException,
                    cause: exception));
        }
        finally
        {
            if (stage is not null)
                CleanupStage(stage);
            _mutationGate.Release();
        }
    }

    public async ValueTask<JournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            return await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Returns the authenticated durable high-water state after recovery. Sync clients use
    /// this to detect a missing journal tail or an epoch rollback before contacting a server.
    /// </summary>
    public async ValueTask<OfflineJournalStateV1> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready || _state is null)
                throw new InvalidDataException("The journal state is unavailable until recovery succeeds.");
            return _state with { };
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<JournalEntryMetadataV1>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (limit is < 1 || limit > _options.MaxPendingEntries)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
                return [];
            return Array.AsReadOnly(_entries
                .Where(item => _syncStatuses.GetValueOrDefault(item.EntryId)?.State == JournalEntrySyncState.Pending)
                .Take(limit)
                .Select(item => item with { })
                .ToArray());
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<JournalEntrySyncStatus>> ReadSyncStatusAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (limit is < 1 || limit > _options.MaxRetainedEntries)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Ready)
                return [];
            return Array.AsReadOnly(_entries
                .Take(limit)
                .Select(item => _syncStatuses[item.EntryId] with { })
                .ToArray());
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<JournalRetentionPlanResult> PlanRetentionAsync(
        JournalRetentionPlanRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReconciledBefore.Offset != TimeSpan.Zero || request.PlannedAt.Offset != TimeSpan.Zero ||
            request.PlannedAt < request.ReconciledBefore ||
            request.MaximumCandidates is < 1 || request.MaximumCandidates > _options.MaxRetentionPlanCandidates)
        {
            return new JournalRetentionPlanResult(
                false,
                null,
                OfflineJournalDiagnostics.Error(
                    "offline_journal_retention_plan_invalid",
                    "The retention plan request contains invalid UTC times or exceeds its candidate bound.",
                    "cortex.offline-journal.retention.plan"));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLease = _store.AcquireWriterLock();
            _loaded = false;
            var recovery = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var quarantineCount = _store.EnumerateDirectories(
                _store.QuarantineRoot,
                _options.MaxRecoveryEntries).Count;
            if (!recovery.Ready)
            {
                return new JournalRetentionPlanResult(
                    false,
                    null,
                    OfflineJournalDiagnostics.Error(
                        "offline_journal_retention_recovery_required",
                        "Retention eligibility cannot be planned until journal recovery and quarantine review succeed.",
                        "cortex.offline-journal.retention.plan",
                        context: new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["quarantinedArtifacts"] = quarantineCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }));
            }

            var candidates = Array.AsReadOnly(_entries
                .Where(item => item.CreatedAt <= request.ReconciledBefore)
                .Select(item => (Entry: item, Status: _syncStatuses.GetValueOrDefault(item.EntryId)))
                .Where(item => item.Status is { State: JournalEntrySyncState.Reconciled, ReceiptId: not null })
                .Take(request.MaximumCandidates)
                .Select(item => new JournalRetentionCandidateV1(
                    item.Entry.EntryId,
                    item.Entry.Sequence,
                    item.Entry.CreatedAt,
                    item.Entry.EntrySha256,
                    item.Status!.ReceiptId!))
                .ToArray());
            var planIdMaterial = string.Join(
                ":",
                new[]
                {
                    _identity.JournalId,
                    request.ReconciledBefore.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    request.PlannedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    string.Join(',', candidates.Select(item => item.EntryId))
                });
            var planId = $"retention-{CortexCanonicalJson.Sha256(planIdMaterial)[..24]}";
            var placeholder = new OfflineJournalRetentionPlanV1(
                CortexSyncContractIds.SchemaVersion,
                OfflineJournalContractIds.RetentionPlan,
                OfflineJournalContractIds.Canonicalization,
                planId,
                _identity.JournalId,
                _identity.StoreId,
                _identity.ProjectId,
                request.ReconciledBefore,
                request.PlannedAt,
                candidates,
                quarantineCount,
                "operator_review_only",
                false,
                false,
                true,
                OfflineJournalContractIds.EmptySha256);
            var plan = placeholder with
            {
                PlanHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(placeholder))
            };
            return new JournalRetentionPlanResult(true, plan, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new JournalRetentionPlanResult(
                false,
                null,
                OfflineJournalDiagnostics.Error(
                    "offline_journal_retention_plan_failed",
                    "Retention eligibility could not be inspected safely; no journal or legacy-source bytes were changed.",
                    "cortex.offline-journal.retention.plan",
                    retryable: exception is IOException,
                    cause: exception));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _mutationGate.Dispose();
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<JournalRecoveryResult> EnsureLoadedAsync(CancellationToken cancellationToken) =>
        _loaded
            ? new JournalRecoveryResult(_ready, _pendingCount, 0, 0, [])
            : await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Commits the monotonic store checkpoint before the corresponding entry is promoted. A
    /// crash after this point therefore leaves either a recoverable staged entry or a durable
    /// gap that blocks later writes; it can never silently reuse the reserved sequence.
    /// </summary>
    private async ValueTask PersistStateCheckpointAsync(
        OfflineJournalStateV1 nextState,
        CancellationToken cancellationToken)
    {
        OfflineJournalStatePolicy.Validate(
            nextState,
            _identity,
            _state,
            _options.MaxIdentifierCharacters);
        var canonical = CortexCanonicalJson.Serialize(nextState);
        if (_retainedBytes > _options.MaxRetainedBytes - canonical.Length)
            throw new JournalLimitExceededException("retained_state_bytes");

        string? stage = _store.CreateStageDirectory("h", _options.MaxStagingArtifacts);
        try
        {
            await _store.WriteCanonicalAsync(
                Path.Combine(stage, JournalFileStore.StateFileName),
                nextState,
                _options.MaxMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            var (written, bytes) = await _store.ReadCanonicalAsync<OfflineJournalStateV1>(
                Path.Combine(stage, JournalFileStore.StateFileName),
                _options.MaxMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            OfflineJournalStatePolicy.Validate(
                written,
                _identity,
                _state,
                _options.MaxIdentifierCharacters);
            if (!string.Equals(written.StateSha256, nextState.StateSha256, StringComparison.Ordinal) ||
                !bytes.AsSpan().SequenceEqual(canonical))
            {
                throw new InvalidDataException("The staged high-water checkpoint changed during verification.");
            }

            var final = _store.StatePath(OfflineJournalStatePolicy.DirectoryName(nextState));
            if (_store.DirectoryExists(final))
            {
                var (existing, existingBytes) = await _store.ReadCanonicalAsync<OfflineJournalStateV1>(
                    Path.Combine(final, JournalFileStore.StateFileName),
                    _options.MaxMetadataBytes,
                    cancellationToken).ConfigureAwait(false);
                OfflineJournalStatePolicy.Validate(
                    existing,
                    _identity,
                    _state,
                    _options.MaxIdentifierCharacters);
                if (!existingBytes.AsSpan().SequenceEqual(canonical))
                    throw new InvalidDataException("A different checkpoint occupies the deterministic state path.");
                _store.DeleteOwnedStage(stage);
            }
            else
            {
                _store.PromoteDirectory(stage, final);
            }
            stage = null;
            _state = nextState;
            _retainedBytes = checked(_retainedBytes + canonical.Length);
        }
        finally
        {
            if (stage is not null)
                CleanupStage(stage);
        }
    }

    private async ValueTask<JournalRecoveryResult> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        _store.EnsureLayout();
        _entries.Clear();
        _entriesById.Clear();
        _entriesBySequence.Clear();
        _idempotency.Clear();
        _acknowledgedEntryIds.Clear();
        _syncStatuses.Clear();
        _state = null;
        _pendingBytes = 0;
        _pendingCount = 0;
        _retainedBytes = 0;
        _legacyEvidenceReceiptCount = 0;
        _typedReceiptCount = 0;
        var diagnostics = new List<PegasusDiagnostic>();
        var quarantined = 0;
        var recovered = 0;
        var candidates = new List<(string Path, JournalEntryMetadataV1 Entry, long Bytes)>();
        var stagingDirectories = EnumerateRecoveryCollection(
            _store.StagingRoot,
            _options.MaxStagingArtifacts,
            "offline_journal_staging_count_exceeded",
            "The staging artifact count exceeds the configured recovery-safe limit.",
            diagnostics);

        var stateCandidates = new List<(string Path, OfflineJournalStateV1 State, int Bytes)>();
        foreach (var directory in _store.EnumerateDirectories(
                     _store.StateRoot,
                     checked(_options.MaxRecoveryEntries + 1)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _store.EnsureExactFiles(directory, JournalFileStore.StateFileName);
                var (state, bytes) = await _store.ReadCanonicalAsync<OfflineJournalStateV1>(
                    Path.Combine(directory, JournalFileStore.StateFileName),
                    _options.MaxMetadataBytes,
                    cancellationToken).ConfigureAwait(false);
                stateCandidates.Add((directory, state, bytes.Length));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_state_invalid",
                    "The durable high-water state is invalid, unsupported, or incomplete; writes remain blocked.",
                    "cortex.offline-journal.recover",
                    cause: exception));
                break;
            }
        }

        if (diagnostics.Count == 0)
        {
            foreach (var candidate in stateCandidates
                         .OrderBy(item => item.State.HighWaterSequence)
                         .ThenBy(item => item.State.StateSha256, StringComparer.Ordinal))
            {
                try
                {
                    OfflineJournalStatePolicy.Validate(
                        candidate.State,
                        _identity,
                        _state,
                        _options.MaxIdentifierCharacters);
                    if (!string.Equals(
                            Path.GetFileName(candidate.Path),
                            OfflineJournalStatePolicy.DirectoryName(candidate.State),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The durable state checkpoint path is not deterministic.");
                    }
                    if (_retainedBytes > _options.MaxRetainedBytes - candidate.Bytes)
                        throw new JournalLimitExceededException("retained_state_bytes");
                    _state = candidate.State;
                    _retainedBytes = checked(_retainedBytes + candidate.Bytes);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_state_invalid",
                        "The durable high-water state is invalid, unsupported, or incomplete; writes remain blocked.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                    break;
                }
            }
        }

        if (diagnostics.Count == 0)
        {
            var stateStages = new List<(string Path, OfflineJournalStateV1 State, int Bytes)>();
            foreach (var stage in stagingDirectories)
            {
                if (Path.GetFileName(stage)?.StartsWith("h-", StringComparison.Ordinal) != true)
                    continue;
                try
                {
                    _store.EnsureExactFiles(stage, JournalFileStore.StateFileName);
                    var (state, bytes) = await _store.ReadCanonicalAsync<OfflineJournalStateV1>(
                        Path.Combine(stage, JournalFileStore.StateFileName),
                        _options.MaxMetadataBytes,
                        cancellationToken).ConfigureAwait(false);
                    stateStages.Add((stage, state, bytes.Length));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    _store.QuarantineDirectory(stage, "h");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_state_stage_invalid",
                        "A staged high-water checkpoint is invalid or unsupported; writes remain blocked.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
            }

            foreach (var staged in stateStages.OrderBy(item => item.State.HighWaterSequence))
            {
                try
                {
                    var final = _store.StatePath(OfflineJournalStatePolicy.DirectoryName(staged.State));
                    if (_store.DirectoryExists(final))
                    {
                        var (existing, _) = await _store.ReadCanonicalAsync<OfflineJournalStateV1>(
                            Path.Combine(final, JournalFileStore.StateFileName),
                            _options.MaxMetadataBytes,
                            cancellationToken).ConfigureAwait(false);
                        if (!CortexCanonicalJson.Serialize(existing).AsSpan()
                                .SequenceEqual(CortexCanonicalJson.Serialize(staged.State)))
                        {
                            throw new InvalidDataException("A staged checkpoint conflicts with its deterministic retained path.");
                        }
                        _store.DeleteOwnedStage(staged.Path);
                        continue;
                    }
                    OfflineJournalStatePolicy.Validate(staged.State, _identity, _state, _options.MaxIdentifierCharacters);
                    if (_retainedBytes > _options.MaxRetainedBytes - staged.Bytes)
                        throw new JournalLimitExceededException("retained_state_bytes");
                    _store.PromoteDirectory(staged.Path, final);
                    recovered++;
                    _state = staged.State;
                    _retainedBytes = checked(_retainedBytes + staged.Bytes);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    if (_store.DirectoryExists(staged.Path))
                        _store.QuarantineDirectory(staged.Path, "h");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_state_stage_conflict",
                        "A staged high-water checkpoint conflicts with durable state; writes remain blocked.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
            }
        }

        foreach (var directory in _store.EnumerateDirectories(_store.PendingRoot, _options.MaxRecoveryEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = await ValidateEntryDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                var bytes = checked(entry.ProtectedPayloadBytes + CortexCanonicalJson.Serialize(entry).Length);
                candidates.Add((directory, entry, bytes));
            }
            catch (JournalPayloadProtectionUnavailableException exception)
            {
                diagnostics.Add(ProtectionUnavailableDiagnostic(exception));
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                _store.QuarantineDirectory(directory, "p");
                quarantined++;
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_pending_quarantined",
                    "A pending entry failed integrity validation and was quarantined.",
                    "cortex.offline-journal.recover",
                    cause: exception));
            }
        }

        foreach (var candidate in candidates.OrderBy(item => item.Entry.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedSequence = _entries.Count == 0 ? 1 : _entries[^1].Sequence + 1;
            var expectedPrevious = _entries.Count == 0
                ? OfflineJournalContractIds.EmptySha256
                : _entries[^1].EntrySha256;
            if (candidate.Entry.Sequence != expectedSequence ||
                !string.Equals(candidate.Entry.PreviousEntrySha256, expectedPrevious, StringComparison.Ordinal) ||
                _idempotency.ContainsKey(candidate.Entry.IdempotencyKey) ||
                !string.Equals(Path.GetFileName(candidate.Path), EntryDirectoryName(candidate.Entry), StringComparison.Ordinal))
            {
                _store.QuarantineDirectory(candidate.Path, "c");
                quarantined++;
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_chain_quarantined",
                    "A pending entry violates sequence, chain, identity, or deterministic-path invariants.",
                    "cortex.offline-journal.recover",
                    context: EntryContext(candidate.Entry)));
                continue;
            }
            if (_retainedBytes > _options.MaxRetainedBytes - candidate.Bytes ||
                _entries.Count == _options.MaxRetainedEntries)
            {
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_recovery_quota_exceeded",
                    "Retained entry data exceeds configured journal bounds.",
                    "cortex.offline-journal.recover",
                    context: EntryContext(candidate.Entry)));
                break;
            }
            AddLoadedEntry(candidate.Entry);
        }

        // Only a truly empty journal may establish a new epoch. Existing entries without their
        // state chain are ambiguous (for example, a deleted tail) and must never be renumbered.
        if (diagnostics.Count == 0 && _state is null)
        {
            if (_entries.Count == 0)
            {
                await PersistStateCheckpointAsync(
                    OfflineJournalStatePolicy.CreateGenesis(_identity),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_high_water_missing",
                    "Retained entries exist without their durable high-water chain; writes remain blocked.",
                    "cortex.offline-journal.recover"));
            }
        }

        if (diagnostics.Count == 0)
        {
            var stages = new List<(string Path, JournalEntryMetadataV1 Entry)>();
            foreach (var stage in stagingDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(stage)?.StartsWith("e-", StringComparison.Ordinal) != true)
                    continue;
                try
                {
                    stages.Add((stage, await ValidateEntryDirectoryAsync(stage, cancellationToken).ConfigureAwait(false)));
                }
                catch (JournalPayloadProtectionUnavailableException exception)
                {
                    diagnostics.Add(ProtectionUnavailableDiagnostic(exception));
                    break;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    _store.QuarantineDirectory(stage, "s");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_stage_quarantined",
                        "An incomplete or invalid entry stage was quarantined.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
            }

            IEnumerable<(string Path, JournalEntryMetadataV1 Entry)> recoverableStages =
                diagnostics.Count == 0
                    ? stages.OrderBy(item => item.Entry.Sequence)
                    : Enumerable.Empty<(string Path, JournalEntryMetadataV1 Entry)>();
            foreach (var staged in recoverableStages)
            {
                var expectedSequence = _entries.Count == 0 ? 1 : _entries[^1].Sequence + 1;
                var expectedPrevious = _entries.Count == 0
                    ? OfflineJournalContractIds.EmptySha256
                    : _entries[^1].EntrySha256;
                var final = _store.PendingPath(EntryDirectoryName(staged.Entry));
                var stateAlreadyReserved = _state is not null &&
                    _state.HighWaterSequence == staged.Entry.Sequence &&
                    string.Equals(_state.LastEntrySha256, staged.Entry.EntrySha256, StringComparison.Ordinal);
                var stateCanAdvance = _state is not null &&
                    _state.HighWaterSequence + 1 == staged.Entry.Sequence &&
                    string.Equals(_state.LastEntrySha256, expectedPrevious, StringComparison.Ordinal);
                if (staged.Entry.Sequence == expectedSequence &&
                    string.Equals(staged.Entry.PreviousEntrySha256, expectedPrevious, StringComparison.Ordinal) &&
                    !_idempotency.ContainsKey(staged.Entry.IdempotencyKey) &&
                    _pendingCount < _options.MaxPendingEntries &&
                    _pendingBytes <= _options.MaxPendingBytes - staged.Entry.ProtectedPayloadBytes - _options.MaxMetadataBytes &&
                    _entries.Count < _options.MaxRetainedEntries &&
                    _retainedBytes <= _options.MaxRetainedBytes - staged.Entry.ProtectedPayloadBytes - _options.MaxMetadataBytes &&
                    !_store.DirectoryExists(final) &&
                    (stateAlreadyReserved || stateCanAdvance))
                {
                    if (stateCanAdvance)
                    {
                        await PersistStateCheckpointAsync(
                            OfflineJournalStatePolicy.Advance(_state!, staged.Entry),
                            cancellationToken).ConfigureAwait(false);
                    }
                    _store.PromoteDirectory(staged.Path, final);
                    AddLoadedEntry(staged.Entry);
                    recovered++;
                }
                else if (_store.DirectoryExists(final))
                {
                    try
                    {
                        var committed = await ValidateEntryDirectoryAsync(final, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(committed.EntrySha256, staged.Entry.EntrySha256, StringComparison.Ordinal))
                            throw new InvalidDataException("The staged entry conflicts with its deterministic committed path.");
                        _store.DeleteOwnedStage(staged.Path);
                    }
                    catch (JournalPayloadProtectionUnavailableException exception)
                    {
                        diagnostics.Add(ProtectionUnavailableDiagnostic(exception));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                    {
                        _store.QuarantineDirectory(staged.Path, "s");
                        quarantined++;
                        diagnostics.Add(OfflineJournalDiagnostics.Error(
                            "offline_journal_stage_chain_conflict",
                            "A complete stage conflicts with the committed journal chain and was quarantined.",
                            "cortex.offline-journal.recover",
                            context: EntryContext(staged.Entry),
                            cause: exception));
                    }
                }
                else
                {
                    _store.QuarantineDirectory(staged.Path, "s");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_stage_chain_conflict",
                        "A complete stage conflicts with the committed journal chain and was quarantined.",
                        "cortex.offline-journal.recover",
                        context: EntryContext(staged.Entry)));
                }
            }
        }

        if (diagnostics.Count == 0)
        {
            var expectedHighWater = _entries.Count == 0 ? 0 : _entries[^1].Sequence;
            var expectedTailHash = _entries.Count == 0
                ? OfflineJournalContractIds.EmptySha256
                : _entries[^1].EntrySha256;
            if (_state is null || _state.HighWaterSequence != expectedHighWater ||
                !string.Equals(_state.LastEntrySha256, expectedTailHash, StringComparison.Ordinal) ||
                stateCandidates.Any(candidate =>
                    candidate.State.HighWaterSequence > 0 &&
                    (!_entriesBySequence.TryGetValue(candidate.State.HighWaterSequence, out var entry) ||
                     !string.Equals(entry.EntrySha256, candidate.State.LastEntrySha256, StringComparison.Ordinal))))
            {
                diagnostics.Add(OfflineJournalDiagnostics.Error(
                    "offline_journal_high_water_gap",
                    "The durable high-water state and retained entry chain do not describe the same tail; writes remain blocked.",
                    "cortex.offline-journal.recover"));
            }
        }

        IReadOnlyList<string> typedReceiptDirectories = [];
        var typedReceiptOccupancy = 0;
        if (diagnostics.Count == 0)
        {
            typedReceiptDirectories = EnumerateRecoveryCollection(
                _store.PublicReceiptRoot,
                _options.MaxTypedReceipts,
                "offline_journal_typed_receipt_count_exceeded",
                "The retained typed-receipt count exceeds the configured recovery-safe limit.",
                diagnostics);
            typedReceiptOccupancy = typedReceiptDirectories.Count;
        }

        if (diagnostics.Count == 0)
        {
            foreach (var stage in stagingDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(stage)?.StartsWith("a-", StringComparison.Ordinal) != true)
                    continue;
                try
                {
                    var staged = await ValidateTypedReceiptDirectoryAsync(stage, cancellationToken).ConfigureAwait(false);
                    var final = _store.PublicReceiptPath(staged.Receipt.ReceiptId);
                    if (_store.DirectoryExists(final))
                    {
                        var existing = await ValidateTypedReceiptDirectoryAsync(final, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(existing.Bundle.BundleHash, staged.Bundle.BundleHash, StringComparison.Ordinal) ||
                            !string.Equals(existing.Receipt.ReceiptHash, staged.Receipt.ReceiptHash, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("A staged typed receipt conflicts with its deterministic retained path.");
                        }
                        _store.DeleteOwnedStage(stage);
                    }
                    else
                    {
                        if (typedReceiptOccupancy >= _options.MaxTypedReceipts)
                            throw new JournalLimitExceededException("typed_receipt_count");
                        _store.PromoteDirectory(stage, final);
                        typedReceiptOccupancy++;
                        recovered++;
                    }
                }
                catch (JournalLimitExceededException exception) when (
                    string.Equals(exception.LimitName, "typed_receipt_count", StringComparison.Ordinal))
                {
                    if (_store.DirectoryExists(stage))
                        _store.QuarantineDirectory(stage, "a");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_typed_receipt_count_exceeded",
                        "A staged typed receipt would exceed the configured recovery-safe count and was quarantined.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or CryptographicException)
                {
                    if (_store.DirectoryExists(stage))
                        _store.QuarantineDirectory(stage, "a");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_typed_receipt_stage_invalid",
                        "A staged typed receipt failed exact bundle, entry, or host-authenticity validation.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
            }
        }

        if (diagnostics.Count == 0)
        {
            typedReceiptDirectories = EnumerateRecoveryCollection(
                _store.PublicReceiptRoot,
                _options.MaxTypedReceipts,
                "offline_journal_typed_receipt_count_exceeded",
                "The retained typed-receipt count exceeds the configured recovery-safe limit.",
                diagnostics);
        }

        if (diagnostics.Count == 0)
        {
            foreach (var directory in typedReceiptDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var retained = await ValidateTypedReceiptDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(Path.GetFileName(directory), retained.Receipt.ReceiptId, StringComparison.Ordinal))
                        throw new InvalidDataException("A typed receipt path does not match its exact receipt identifier.");
                    if (_retainedBytes > _options.MaxRetainedBytes - retained.Bytes)
                        throw new JournalLimitExceededException("retained_typed_receipt_bytes");
                    _retainedBytes = checked(_retainedBytes + retained.Bytes);
                    _typedReceiptCount++;
                    ApplyTypedProjection(retained.Projection);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or CryptographicException)
                {
                    _store.QuarantineDirectory(directory, "a");
                    quarantined++;
                    diagnostics.Add(OfflineJournalDiagnostics.Error(
                        "offline_journal_typed_receipt_quarantined",
                        "A retained typed receipt failed exact bundle, entry, or host-authenticity validation.",
                        "cortex.offline-journal.recover",
                        cause: exception));
                }
            }
        }

        if (diagnostics.Count == 0)
        {
            var legacyReceiptDirectories = EnumerateRecoveryCollection(
                _store.ReceiptRoot,
                _options.MaxLegacyEvidenceReceipts,
                "offline_journal_legacy_receipt_count_exceeded",
                "The retained legacy evidence-receipt count exceeds the configured recovery-safe limit.",
                diagnostics);
            if (diagnostics.Count == 0)
            {
                foreach (var directory in legacyReceiptDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var (candidate, _) = await _store.ReadCanonicalAsync<JournalReceiptV1>(
                            Path.Combine(directory, JournalFileStore.ReceiptFileName),
                            _options.MaxMetadataBytes,
                            cancellationToken).ConfigureAwait(false);
                        var entry = _entriesById.GetValueOrDefault(candidate.EntryId);
                        if (entry is null || !string.Equals(Path.GetFileName(directory), entry.EntryId, StringComparison.Ordinal))
                            throw new InvalidDataException("A receipt does not identify an exact retained entry path.");
                        var request = RequestFromReceipt(candidate);
                        var receipt = await ValidateReceiptDirectoryAsync(
                            directory,
                            entry,
                            request,
                            verifyProof: true,
                            cancellationToken).ConfigureAwait(false);
                        var receiptBytes = checked(receipt.EvidenceBytes + CortexCanonicalJson.Serialize(receipt).Length);
                        if (_retainedBytes > _options.MaxRetainedBytes - receiptBytes)
                            throw new JournalLimitExceededException("retained_bytes");
                        AddLoadedReceipt(receipt, entry);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                    {
                        _store.QuarantineDirectory(directory, "r");
                        quarantined++;
                        diagnostics.Add(OfflineJournalDiagnostics.Error(
                            "offline_journal_receipt_quarantined",
                            "A retained receipt failed entry binding, integrity, or host authenticity validation.",
                            "cortex.offline-journal.recover",
                            cause: exception));
                    }
                }
            }
        }

        if (_pendingCount > _options.MaxPendingEntries || _pendingBytes > _options.MaxPendingBytes)
        {
            diagnostics.Add(OfflineJournalDiagnostics.Error(
                "offline_journal_pending_quota_exceeded",
                "Unacknowledged journal data exceeds configured pending bounds; retained data was not deleted.",
                "cortex.offline-journal.recover"));
        }

        if (diagnostics.Count == 0 &&
            _store.EnumerateDirectories(_store.QuarantineRoot, _options.MaxRecoveryEntries).Count > 0)
        {
            diagnostics.Add(OfflineJournalDiagnostics.Error(
                "offline_journal_quarantine_present",
                "Quarantined journal artifacts require explicit operator review; writes remain blocked.",
                "cortex.offline-journal.recover"));
        }

        _loaded = true;
        _ready = diagnostics.Count == 0;
        return new JournalRecoveryResult(
            _ready,
            _pendingCount,
            recovered,
            quarantined,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private IReadOnlyList<string> EnumerateRecoveryCollection(
        string root,
        int maximumEntries,
        string diagnosticCode,
        string diagnosticMessage,
        List<PegasusDiagnostic> diagnostics)
    {
        try
        {
            var directories = _store.EnumerateDirectories(root, checked(maximumEntries + 1));
            if (directories.Count <= maximumEntries)
                return directories;
            diagnostics.Add(OfflineJournalDiagnostics.Error(
                diagnosticCode,
                diagnosticMessage,
                "cortex.offline-journal.recover",
                context: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["configuredMaximum"] = maximumEntries.ToString(),
                    ["observedMinimum"] = directories.Count.ToString()
                }));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            diagnostics.Add(OfflineJournalDiagnostics.Error(
                diagnosticCode,
                diagnosticMessage,
                "cortex.offline-journal.recover",
                cause: exception));
        }
        return [];
    }

    private async ValueTask<JournalEntryMetadataV1> ValidateEntryDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        _store.EnsureExactFiles(directory, JournalFileStore.MetadataFileName, JournalFileStore.PayloadFileName);
        var (entry, _) = await _store.ReadCanonicalAsync<JournalEntryMetadataV1>(
            Path.Combine(directory, JournalFileStore.MetadataFileName),
            _options.MaxMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        ValidateStoredEntry(entry);
        var digest = await _store.ComputeDigestAsync(
            Path.Combine(directory, JournalFileStore.PayloadFileName),
            _options.MaxProtectedPayloadBytes,
            cancellationToken).ConfigureAwait(false);
        if (digest.Bytes != entry.ProtectedPayloadBytes ||
            !string.Equals(digest.Sha256, entry.ProtectedPayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The protected journal payload hash or length is invalid.");
        }

        await using var input = _store.OpenReadFile(
            Path.Combine(directory, JournalFileStore.PayloadFileName));
        if (!await _protector.VerifyAsync(
                input,
                entry.Protection,
                CreateProtectionContext(entry),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The host rejected the protected journal payload authentication data.");
        }
        return entry;
    }

    private async ValueTask<JournalReceiptV1> ValidateReceiptDirectoryAsync(
        string directory,
        JournalEntryMetadataV1 entry,
        JournalAcknowledgmentRequest request,
        bool verifyProof,
        CancellationToken cancellationToken)
    {
        _store.EnsureExactFiles(directory, JournalFileStore.EvidenceFileName, JournalFileStore.ReceiptFileName);
        var (receipt, _) = await _store.ReadCanonicalAsync<JournalReceiptV1>(
            Path.Combine(directory, JournalFileStore.ReceiptFileName),
            _options.MaxMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        ValidateStoredReceipt(receipt, entry);
        var digest = await _store.ComputeDigestAsync(
            Path.Combine(directory, JournalFileStore.EvidenceFileName),
            _options.MaxReceiptEvidenceBytes,
            cancellationToken).ConfigureAwait(false);
        if (digest.Bytes != receipt.EvidenceBytes || !string.Equals(digest.Sha256, receipt.EvidenceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The durable acknowledgment evidence hash or length is invalid.");
        if (verifyProof)
        {
            await using var input = _store.OpenReadFile(
                Path.Combine(directory, JournalFileStore.EvidenceFileName));
            if (!await _receiptVerifier.VerifyAsync(input, request, entry, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The host rejected existing server acknowledgment evidence.");
        }
        return receipt;
    }

    private async ValueTask<ValidatedTypedReceipt> ValidateTypedReceiptDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        _store.EnsureExactFiles(
            directory,
            JournalFileStore.PublicBundleFileName,
            JournalFileStore.PublicReceiptFileName);
        var (bundle, bundleBytes) = await _store.ReadCanonicalAsync<CortexOfflineBundleV1>(
            Path.Combine(directory, JournalFileStore.PublicBundleFileName),
            CortexSyncContractLimits.MaximumCanonicalBundleBytes,
            cancellationToken).ConfigureAwait(false);
        var (receipt, receiptBytes) = await _store.ReadCanonicalAsync<CortexSyncReceiptV1>(
            Path.Combine(directory, JournalFileStore.PublicReceiptFileName),
            checked((int)_options.MaxReceiptEvidenceBytes),
            cancellationToken).ConfigureAwait(false);
        var proofVerified = await _receiptVerifier.VerifyAsync(receipt, bundle, cancellationToken).ConfigureAwait(false);
        var projection = OfflineJournalTypedReceiptPolicy.Evaluate(
            _identity,
            _state ?? throw new InvalidDataException("The journal store epoch is unavailable."),
            receipt,
            bundle,
            proofVerified,
            _entriesBySequence);
        if (!projection.Accepted)
            throw new InvalidDataException(projection.Failure ?? "The typed receipt projection was rejected.");
        return new ValidatedTypedReceipt(
            bundle,
            receipt,
            projection,
            checked(bundleBytes.Length + receiptBytes.Length));
    }

    private void ValidateStoredEntry(JournalEntryMetadataV1 entry)
    {
        if (entry.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(entry.ContractId, OfflineJournalContractIds.Entry, StringComparison.Ordinal) ||
            !string.Equals(entry.Canonicalization, OfflineJournalContractIds.Canonicalization, StringComparison.Ordinal) ||
            !string.Equals(entry.JournalId, _identity.JournalId, StringComparison.Ordinal) ||
            !string.Equals(entry.WriterId, _identity.WriterId, StringComparison.Ordinal) ||
            entry.Sequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            !string.Equals(entry.EntryId, $"evt-{entry.Sequence:D16}", StringComparison.Ordinal) ||
            !ValidIdentifier(entry.IdempotencyKey) || !ValidIdentifier(entry.Operation) ||
            !ValidContractId(entry.PayloadContractId) ||
            entry.CreatedAt.Offset != TimeSpan.Zero || entry.PlaintextBytes is <= 0 ||
            entry.PlaintextBytes > _options.MaxPlaintextBytes || entry.ProtectedPayloadBytes is <= 0 ||
            entry.ProtectedPayloadBytes > _options.MaxProtectedPayloadBytes ||
            !IsSha256(entry.PlaintextSha256) || !IsSha256(entry.ProtectedPayloadSha256) ||
            !IsSha256(entry.PreviousEntrySha256) || !IsSha256(entry.EntrySha256) ||
            !ValidBaseRevision(entry.BaseRevisionId, entry.BaseRevisionSha256) ||
            !string.Equals(entry.ContentTreatment, OfflineJournalContractIds.ContentTreatment, StringComparison.Ordinal) ||
            entry.Executable || !string.Equals(entry.EntrySha256, ComputeEntryHash(entry), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The offline journal entry metadata is invalid or has been modified.");
        }
        ValidateProtectionMetadata(entry.Protection);
    }

    private void ValidateStoredReceipt(JournalReceiptV1 receipt, JournalEntryMetadataV1 entry)
    {
        if (receipt.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(receipt.ContractId, OfflineJournalContractIds.Receipt, StringComparison.Ordinal) ||
            !string.Equals(receipt.Canonicalization, OfflineJournalContractIds.Canonicalization, StringComparison.Ordinal) ||
            !string.Equals(receipt.JournalId, _identity.JournalId, StringComparison.Ordinal) ||
            !string.Equals(receipt.WriterId, _identity.WriterId, StringComparison.Ordinal) ||
            !string.Equals(receipt.EntryId, entry.EntryId, StringComparison.Ordinal) ||
            receipt.Sequence != entry.Sequence || !string.Equals(receipt.EntrySha256, entry.EntrySha256, StringComparison.Ordinal) ||
            !ValidIdentifier(receipt.ReceiptId) || !ValidIdentifier(receipt.AuthorityId) ||
            !ValidIdentifier(receipt.ServerRevisionId) || !ValidIdentifier(receipt.ServerProofAlgorithm) ||
            !ValidIdentifier(receipt.ServerProofKeyId) || receipt.AcceptedAt.Offset != TimeSpan.Zero ||
            receipt.Disposition is not ("accepted" or "idempotent") || receipt.EvidenceBytes is <= 0 ||
            receipt.EvidenceBytes > _options.MaxReceiptEvidenceBytes || !IsSha256(receipt.EvidenceSha256) ||
            !string.Equals(receipt.ContentTreatment, OfflineJournalContractIds.ContentTreatment, StringComparison.Ordinal) ||
            receipt.Executable || !IsSha256(receipt.ReceiptSha256) ||
            !string.Equals(receipt.ReceiptSha256, ComputeReceiptHash(receipt), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The durable journal receipt is invalid or has been modified.");
        }
    }

    private PegasusDiagnostic? ValidateAppendRequest(JournalAppendRequest request, Stream plaintext)
    {
        if (!plaintext.CanRead || !ValidIdentifier(request.IdempotencyKey) || !ValidIdentifier(request.Operation) ||
            !ValidContractId(request.PayloadContractId) ||
            request.CreatedAt.Offset != TimeSpan.Zero || !ValidBaseRevision(request.BaseRevisionId, request.BaseRevisionSha256) ||
            request.DeclaredPayloadBytes is <= 0 || request.DeclaredPayloadBytes > _options.MaxPlaintextBytes)
        {
            return OfflineJournalDiagnostics.Error(
                "offline_journal_append_invalid",
                "The append request is unreadable, unbounded, or has invalid identifiers, time, or base revision.",
                "cortex.offline-journal.append");
        }
        return null;
    }

    private PegasusDiagnostic? ValidateAcknowledgmentRequest(JournalAcknowledgmentRequest request, Stream evidence)
    {
        if (!evidence.CanRead || !ValidIdentifier(request.ReceiptId) || !ValidIdentifier(request.AuthorityId) ||
            !ValidIdentifier(request.EntryId) || request.Sequence is < 1 or > CortexSyncContractLimits.MaximumSequence ||
            !IsSha256(request.EntrySha256) || !ValidIdentifier(request.ServerRevisionId) ||
            !ValidIdentifier(request.ServerProofAlgorithm) || !ValidIdentifier(request.ServerProofKeyId) ||
            request.AcceptedAt.Offset != TimeSpan.Zero || request.Disposition is not ("accepted" or "idempotent") ||
            request.DeclaredEvidenceBytes is <= 0 || request.DeclaredEvidenceBytes > _options.MaxReceiptEvidenceBytes)
        {
            return OfflineJournalDiagnostics.Error(
                "offline_journal_ack_invalid",
                "The acknowledgment request is unreadable, unbounded, or has invalid proof metadata.",
                "cortex.offline-journal.acknowledge");
        }
        return null;
    }

    private void AddLoadedEntry(JournalEntryMetadataV1 entry)
    {
        var storedBytes = checked(entry.ProtectedPayloadBytes + CortexCanonicalJson.Serialize(entry).Length);
        _entries.Add(entry);
        _entriesById.Add(entry.EntryId, entry);
        _entriesBySequence.Add(entry.Sequence, entry);
        _idempotency.Add(entry.IdempotencyKey, entry);
        _pendingBytes = checked(_pendingBytes + storedBytes);
        _pendingCount++;
        _retainedBytes = checked(_retainedBytes + storedBytes);
        _syncStatuses.Add(entry.EntryId, new JournalEntrySyncStatus(
            entry.EntryId,
            entry.Sequence,
            JournalEntrySyncState.Pending,
            null));
    }

    private void AddLoadedReceipt(JournalReceiptV1 receipt, JournalEntryMetadataV1 entry)
    {
        if (!_acknowledgedEntryIds.Add(entry.EntryId))
            return;
        _retainedBytes = checked(
            _retainedBytes + receipt.EvidenceBytes + CortexCanonicalJson.Serialize(receipt).Length);
        _legacyEvidenceReceiptCount++;
    }

    private void ApplyTypedProjection(TypedReceiptProjection projection)
    {
        foreach (var next in projection.Statuses)
        {
            var current = _syncStatuses[next.EntryId];
            if (current.State == JournalEntrySyncState.Reconciled ||
                current.State == JournalEntrySyncState.Uploaded && next.State == JournalEntrySyncState.Uploaded)
            {
                continue;
            }
            if (current.State == JournalEntrySyncState.Pending)
            {
                var entry = _entriesById[next.EntryId];
                _pendingBytes -= entry.ProtectedPayloadBytes + CortexCanonicalJson.Serialize(entry).Length;
                _pendingCount--;
            }
            _syncStatuses[next.EntryId] = next;
        }
    }

    private JournalAcknowledgmentResult ExistingAcknowledgment(
        JournalReceiptV1 receipt,
        JournalEntryMetadataV1 entry)
    {
        if (!_acknowledgedEntryIds.Contains(entry.EntryId))
            AddLoadedReceipt(receipt, entry);
        return new JournalAcknowledgmentResult(JournalMutationDisposition.Existing, receipt, null);
    }

    private static JournalAcknowledgmentRequest RequestFromReceipt(JournalReceiptV1 receipt) =>
        new(
            receipt.ReceiptId,
            receipt.AuthorityId,
            receipt.EntryId,
            receipt.Sequence,
            receipt.EntrySha256,
            receipt.ServerRevisionId,
            receipt.ServerProofAlgorithm,
            receipt.ServerProofKeyId,
            receipt.AcceptedAt,
            receipt.Disposition,
            receipt.EvidenceBytes);

    private JournalPayloadProtectionContext CreateProtectionContext(
        JournalAppendRequest request,
        long sequence,
        string previousHash)
    {
        var associatedData = CortexCanonicalJson.Serialize(new JournalAssociatedDataV1(
            CortexSyncContractIds.SchemaVersion,
            "pegasus.cortex.offline-journal-associated-data/1.0",
            OfflineJournalContractIds.Canonicalization,
            _identity.JournalId,
            _identity.WriterId,
            _identity.StoreId,
            _identity.ProjectId,
            sequence,
            request.IdempotencyKey,
            request.Operation,
            request.PayloadContractId,
            request.BaseRevisionId,
            request.BaseRevisionSha256,
            previousHash));
        return new JournalPayloadProtectionContext(
            _identity.JournalId,
            _identity.WriterId,
            _identity.StoreId,
            _identity.ProjectId,
            sequence,
            request.IdempotencyKey,
            request.Operation,
            request.PayloadContractId,
            request.BaseRevisionId,
            request.BaseRevisionSha256,
            previousHash,
            associatedData);
    }

    private JournalPayloadProtectionContext CreateProtectionContext(JournalEntryMetadataV1 entry) =>
        CreateProtectionContext(
            new JournalAppendRequest(
                entry.IdempotencyKey,
                entry.Operation,
                entry.PayloadContractId,
                entry.CreatedAt,
                entry.BaseRevisionId,
                entry.BaseRevisionSha256,
                entry.PlaintextBytes),
            entry.Sequence,
            entry.PreviousEntrySha256);

    private static string ComputeEntryHash(JournalEntryMetadataV1 entry) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(entry with
        {
            EntrySha256 = OfflineJournalContractIds.EmptySha256
        }));

    private static string ComputeReceiptHash(JournalReceiptV1 receipt) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(receipt with
        {
            ReceiptSha256 = OfflineJournalContractIds.EmptySha256
        }));

    private static string EntryDirectoryName(JournalEntryMetadataV1 entry) =>
        $"{entry.Sequence:D16}-{entry.EntrySha256[..16]}";

    private static bool Equivalent(
        JournalEntryMetadataV1 existing,
        JournalAppendRequest request,
        long plaintextBytes,
        string plaintextHash) =>
        existing.PlaintextBytes == plaintextBytes &&
        string.Equals(existing.PlaintextSha256, plaintextHash, StringComparison.Ordinal) &&
        string.Equals(existing.Operation, request.Operation, StringComparison.Ordinal) &&
        string.Equals(existing.PayloadContractId, request.PayloadContractId, StringComparison.Ordinal) &&
        string.Equals(existing.BaseRevisionId, request.BaseRevisionId, StringComparison.Ordinal) &&
        string.Equals(existing.BaseRevisionSha256, request.BaseRevisionSha256, StringComparison.Ordinal);

    private void ValidateProtectionMetadata(JournalPayloadProtectionMetadata metadata)
        => JournalPayloadProtectionPolicy.ValidateMetadata(
            metadata,
            _options.MaxIdentifierCharacters);

    private bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= _options.MaxIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidContractId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumContractIdCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) && value.Contains('/', StringComparison.Ordinal) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-' or '/');

    private bool ValidBaseRevision(string? revisionId, string? revisionHash) =>
        revisionId is null && revisionHash is null ||
        ValidIdentifier(revisionId) && IsSha256(revisionHash);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyDictionary<string, string?> EntryContext(JournalEntryMetadataV1 entry) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["entryId"] = entry.EntryId,
            ["sequence"] = entry.Sequence.ToString(),
            ["entrySha256"] = entry.EntrySha256
        };

    private static PegasusDiagnostic ProtectionUnavailableDiagnostic(
        JournalPayloadProtectionUnavailableException exception) =>
        OfflineJournalDiagnostics.Error(
            exception.DiagnosticCode,
            "A required host payload key or protection suite is unavailable; retained journal bytes were preserved and writes remain blocked.",
            "cortex.offline-journal.recover",
            retryable: exception.Retryable,
            context: exception.KeyId is null
                ? null
                : new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["keyId"] = exception.KeyId
                },
            cause: exception);

    private static JournalAppendResult RejectedAppend(PegasusDiagnostic diagnostic) =>
        new(JournalMutationDisposition.Rejected, null, diagnostic);

    private static JournalAcknowledgmentResult RejectedAcknowledgment(PegasusDiagnostic diagnostic) =>
        new(JournalMutationDisposition.Rejected, null, diagnostic);

    private static JournalExportResult RejectedExport(PegasusDiagnostic diagnostic) =>
        new(false, null, diagnostic);

    private static JournalTypedAcknowledgmentResult RejectedTypedAcknowledgment(string message) =>
        new(
            JournalMutationDisposition.Rejected,
            false,
            false,
            null,
            OfflineJournalDiagnostics.Error(
                "offline_journal_typed_receipt_invalid",
                message,
                "cortex.offline-journal.typed-receipt"));

    private void CleanupStage(string stage)
    {
        try
        {
            _store.DeleteOwnedStage(stage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                _store.QuarantineDirectory(stage, "x");
            }
            catch
            {
                // An owned-stage cleanup failure must not replace the operation result or caller cancellation.
            }
        }
    }

    private sealed record JournalAssociatedDataV1(
        int SchemaVersion,
        string ContractId,
        string Canonicalization,
        string JournalId,
        string WriterId,
        string StoreId,
        string ProjectId,
        long Sequence,
        string IdempotencyKey,
        string Operation,
        string PayloadContractId,
        string? BaseRevisionId,
        string? BaseRevisionSha256,
        string PreviousEntrySha256);

    private sealed record ValidatedTypedReceipt(
        CortexOfflineBundleV1 Bundle,
        CortexSyncReceiptV1 Receipt,
        TypedReceiptProjection Projection,
        int Bytes);
}
