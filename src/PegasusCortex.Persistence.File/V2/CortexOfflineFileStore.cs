using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

/// <summary>
/// Bounded passive offline frame set for one stable task within one project. It is neither an
/// operational store nor a synchronization endpoint. The central V2 store
/// becomes authoritative only after the caller commits and exactly verifies a
/// record, then replaces the candidate with a compact receipt.
/// </summary>
public sealed class CortexOfflineFileStore : IAsyncDisposable
{
    private readonly string _projectRoot;
    private readonly string _taskId;
    private readonly string _scopeIdentity;
    private readonly CortexOfflineTaskManifestV1 _scopeManifest;
    private readonly CortexFileProjectManifestV2 _manifest;
    private readonly TimeProvider _timeProvider;
    private readonly CortexOfflinePayloadAdapterRegistry _adapters;

    private CortexOfflineFileStore(
        string projectRoot,
        string taskId,
        CortexOfflineTaskManifestV1 scopeManifest,
        CortexFileProjectManifestV2 manifest,
        TimeProvider timeProvider,
        CortexOfflinePayloadAdapterRegistry adapters)
    {
        _projectRoot = projectRoot;
        _taskId = taskId;
        _scopeIdentity = CortexOfflineTaskIdentity.ScopeIdentity(projectRoot, taskId);
        _scopeManifest = scopeManifest;
        _manifest = manifest;
        _timeProvider = timeProvider;
        _adapters = adapters;
    }

    public string ProjectRoot => _projectRoot;
    public string TaskId => _taskId;
    public string ScopeRoot => CortexFileV2PathPolicy.OfflineScopeRoot(_projectRoot, _taskId);
    public CortexOfflineTaskManifestV1 ScopeManifest => _scopeManifest;
    public CortexFileProjectManifestV2 Manifest => _manifest;

    public static async ValueTask<CortexOfflineFileStore> OpenAsync(
        string projectRoot,
        string taskId,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var validatedTaskId = CortexOfflineTaskIdentity.Require(taskId, nameof(taskId));
        CortexFileV2PathPolicy.ValidateOfflineScopeLayout(root, validatedTaskId);
        CortexFileV2PathPolicy.RejectLegacyUnscopedOfflineState(root);
        var scopeManifest = await CortexOfflineTaskManifestV1.ReadAsync(
                root,
                validatedTaskId,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = scopeManifest.ToProjectManifest().Validate();
        await RequireSharedBindingCompatibleAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        CortexFileV2PathPolicy.PrepareOfflineDirectories(root, validatedTaskId);
        return new CortexOfflineFileStore(
            root,
            validatedTaskId,
            scopeManifest,
            manifest,
            timeProvider ?? TimeProvider.System,
            CortexOfflinePayloadAdapterRegistry.CreateDefault());
    }

    public static async ValueTask<CortexOfflineFileStore> InitializeAsync(
        string projectRoot,
        string taskId,
        CortexFileProjectManifestV2 manifest,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var validatedTaskId = CortexOfflineTaskIdentity.Require(taskId, nameof(taskId));
        var requested = manifest.Validate();
        CortexFileV2PathPolicy.ValidateOfflineScopeLayout(root, validatedTaskId);
        CortexFileV2PathPolicy.RejectLegacyUnscopedOfflineState(root);

        await RequireSharedBindingCompatibleAsync(root, requested, cancellationToken)
            .ConfigureAwait(false);

        var requestedScope = CortexOfflineTaskManifestV1.From(validatedTaskId, requested);
        var scopeManifestPath = CortexFileV2PathPolicy.OfflineScopeManifestPath(root, validatedTaskId);
        if (Directory.Exists(scopeManifestPath))
            throw new InvalidDataException("A directory occupies the offline task manifest path.");
        if (System.IO.File.Exists(scopeManifestPath))
        {
            var existing = await CortexOfflineTaskManifestV1.ReadAsync(
                    root,
                    validatedTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ScopeManifestEquivalent(existing, requestedScope))
            {
                throw new InvalidOperationException(
                    "The existing offline task binding conflicts with the requested binding.");
            }
        }
        else
        {
            var directories = CaptureDirectoryCreationState(root, validatedTaskId);
            try
            {
                CortexFileV2PathPolicy.PrepareOfflineDirectories(root, validatedTaskId);
                await requestedScope.WriteImmutableAsync(root, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!System.IO.File.Exists(scopeManifestPath))
                    TryRemoveNewEmptyDirectories(directories);
                throw;
            }
        }
        return await OpenAsync(root, validatedTaskId, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexOfflineCaptureResult> CaptureAsync(
        CortexOfflineKnowledgeCreatePayloadV1 payload,
        CancellationToken cancellationToken = default)
    {
        var bytes = CortexOfflineFrameCodec.CreateKnowledgeCandidate(payload, UtcNow());
        var decoded = CortexOfflineFrameCodec.Decode(bytes);
        var validated = CortexOfflineFrameCodec.DecodeKnowledgePayload(decoded);
        RequireWritableNamespace(validated.Namespace);
        var frameId = decoded.Frame!.FrameId;
        var path = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);

        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            if (System.IO.File.Exists(path))
            {
                var existingBytes = await ReadFrameBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var existing = CortexOfflineFrameCodec.Decode(existingBytes);
                if (existing.Status == CortexOfflineFrameStatus.Receipt)
                {
                    var receipt = CortexOfflineFrameCodec.DecodeReceiptPayload(existing);
                    if (!string.Equals(
                            receipt.CandidateSemanticSha256,
                            CortexOfflineFrameCodec.KnowledgeSemanticSha256(validated),
                            StringComparison.Ordinal))
                        throw new InvalidOperationException("The deterministic frame ID is already retained for different content.");
                    return new(CortexOfflineOperationStatus.Existing,
                        await SummarizeAsync(path, existingBytes, existing, cancellationToken).ConfigureAwait(false));
                }
                if (existing.Status != CortexOfflineFrameStatus.Pending || existing.Frame is null ||
                    !string.Equals(existing.Frame.PayloadSchema, decoded.Frame.PayloadSchema, StringComparison.Ordinal) ||
                    !string.Equals(
                        CortexOfflineFrameCodec.KnowledgeSemanticSha256(
                            CortexOfflineFrameCodec.DecodeKnowledgePayload(existing)),
                        CortexOfflineFrameCodec.KnowledgeSemanticSha256(validated),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("The deterministic frame ID is already retained with divergent bytes.");
                return new(CortexOfflineOperationStatus.Existing,
                    await SummarizeAsync(path, existingBytes, existing, cancellationToken).ConfigureAwait(false));
            }

            var inventory = await LoadInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (inventory.Count >= CortexOfflineLimits.MaximumTotalFrames)
                throw new InvalidOperationException("The offline frame set reached its total frame bound.");
            var pending = inventory.Where(value => value.Decoded.Status != CortexOfflineFrameStatus.Receipt).ToArray();
            if (pending.Length >= CortexOfflineLimits.MaximumPendingFrames ||
                pending.Sum(value => value.Bytes.Length) + bytes.Length > CortexOfflineLimits.MaximumPendingUtf8Bytes)
            {
                throw new InvalidOperationException("The offline pending working set reached its file or byte bound.");
            }
            await CortexFileV2AtomicIo.WriteImmutableAsync(
                path,
                bytes,
                CortexOfflineLimits.MaximumFrameUtf8Bytes,
                CortexFileV2PathPolicy.OfflineFramesRoot(_projectRoot, _taskId),
                cancellationToken).ConfigureAwait(false);
        }

        return new(CortexOfflineOperationStatus.Created,
            await SummarizeAsync(path, bytes, decoded, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<CortexOfflineInspection> InspectAsync(
        string frameId,
        CancellationToken cancellationToken = default)
    {
        var path = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);
        var bytes = await ReadFrameBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var decoded = CortexOfflineFrameCodec.Decode(bytes);
        var summary = await SummarizeAsync(path, bytes, decoded, cancellationToken).ConfigureAwait(false);
        return new(summary, decoded.Status != CortexOfflineFrameStatus.UnsupportedOuterSchema, decoded.Diagnostic);
    }

    public async ValueTask<CortexOfflinePage> ListAsync(
        CortexOfflineListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePage(request.Limit);
        if (request.AfterFrameId is not null)
            CortexFileV2Validation.RequireIdentifier(request.AfterFrameId, nameof(request.AfterFrameId));
        var inventory = await LoadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var filtered = inventory
            .Where(value => request.AfterFrameId is null ||
                string.CompareOrdinal(value.FrameId, request.AfterFrameId) > 0)
            .Where(value => request.Status is null || value.Decoded.Status == request.Status)
            .Take(request.Limit + 1)
            .ToArray();
        var hasMore = filtered.Length > request.Limit;
        var selected = filtered.Take(request.Limit).ToArray();
        var items = new List<CortexOfflineFrameSummary>(selected.Length);
        foreach (var value in selected)
            items.Add(await SummarizeAsync(value.Path, value.Bytes, value.Decoded, cancellationToken).ConfigureAwait(false));
        return new(items, hasMore, hasMore ? items[^1].FrameId : null);
    }

    public async ValueTask<CortexOfflinePage> SearchAsync(
        CortexOfflineSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePage(request.Limit);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > CortexOfflineLimits.MaximumSearchCharacters)
            throw new ArgumentException("A bounded search query is required.", nameof(request));
        if (request.AfterFrameId is not null)
            CortexFileV2Validation.RequireIdentifier(request.AfterFrameId, nameof(request.AfterFrameId));
        var inventory = await LoadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var matches = new List<CortexOfflineFrameSummary>();
        foreach (var value in inventory.Where(value => value.Decoded.Status == CortexOfflineFrameStatus.Pending &&
                     (request.AfterFrameId is null || string.CompareOrdinal(value.FrameId, request.AfterFrameId) > 0)))
        {
            var payload = CortexOfflineFrameCodec.DecodeKnowledgePayload(value.Decoded);
            if (!Contains(payload.Title, request.Query) && !Contains(payload.Content, request.Query) &&
                !Contains(payload.Evidence, request.Query) && !payload.Tags.Any(tag => Contains(tag, request.Query)))
            {
                continue;
            }
            matches.Add(await SummarizeAsync(value.Path, value.Bytes, value.Decoded, cancellationToken)
                .ConfigureAwait(false));
            if (matches.Count > request.Limit)
                break;
        }
        var hasMore = matches.Count > request.Limit;
        var items = matches.Take(request.Limit).ToArray();
        return new(items, hasMore, hasMore ? items[^1].FrameId : null);
    }

    public async ValueTask<CortexOfflinePendingRead> ReadPendingAsync(
        string frameId,
        CancellationToken cancellationToken = default)
    {
        var path = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);
        var bytes = await ReadFrameBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var decoded = CortexOfflineFrameCodec.Decode(bytes);
        var payload = CortexOfflineFrameCodec.DecodeKnowledgePayload(decoded);
        var summary = await SummarizeAsync(path, bytes, decoded, cancellationToken).ConfigureAwait(false);
        return new(summary, payload, decoded.Frame!.FrameSha256, decoded.Frame.PayloadSha256);
    }

    public async ValueTask<CortexOfflineAdaptation> AdaptPendingAsync(
        string frameId,
        CancellationToken cancellationToken = default)
    {
        var path = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);
        var bytes = await ReadFrameBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var decoded = CortexOfflineFrameCodec.Decode(bytes);
        return _adapters.Adapt(decoded, _taskId, _scopeIdentity);
    }

    public async ValueTask<CortexOfflineReceiptResult> ReplaceWithReceiptAsync(
        string frameId,
        CortexOfflineVerifiedReceiptV1 receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        CortexFileV2Validation.RequireIdentifier(frameId, nameof(frameId));
        if (!string.Equals(receipt.TaskId, _taskId, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified receipt belongs to a different offline task scope.");
        if (!string.Equals(receipt.ScopeIdentity, _scopeIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified receipt belongs to a different project/task scope.");
        if (!string.Equals(frameId, receipt.CandidateFrameId, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified receipt belongs to a different candidate frame.");
        var path = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);
        await using (await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            var retainedBytes = await ReadFrameBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var retained = CortexOfflineFrameCodec.Decode(retainedBytes);
            if (retained.Status == CortexOfflineFrameStatus.Receipt)
            {
                var existing = CortexOfflineFrameCodec.DecodeReceiptPayload(retained);
                if (!ReceiptEquivalent(existing, receipt.Payload))
                    throw new InvalidOperationException("The candidate was already replaced by a divergent receipt.");
                return new(CortexOfflineOperationStatus.AlreadyReplaced,
                    await SummarizeAsync(path, retainedBytes, retained, cancellationToken).ConfigureAwait(false));
            }
            if (retained.Status != CortexOfflineFrameStatus.Pending || retained.Frame is null ||
                !string.Equals(retained.Frame.FrameSha256, receipt.Payload.CandidateFrameSha256, StringComparison.Ordinal) ||
                !string.Equals(retained.Frame.PayloadSha256, receipt.Payload.CandidatePayloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The pending candidate no longer matches the verified receipt.");
            }
            var receiptBytes = CortexOfflineFrameCodec.CreateReceipt(
                receipt.Payload,
                receipt.Payload.VerifiedAtUtc,
                frameId);
            await CortexFileV2AtomicIo.WriteAtomicAsync(
                path,
                receiptBytes,
                CortexOfflineLimits.MaximumFrameUtf8Bytes,
                CortexFileV2PathPolicy.OfflineFramesRoot(_projectRoot, _taskId),
                cancellationToken).ConfigureAwait(false);
            var decodedReceipt = CortexOfflineFrameCodec.Decode(receiptBytes);
            return new(CortexOfflineOperationStatus.Replaced,
                await SummarizeAsync(path, receiptBytes, decodedReceipt, cancellationToken).ConfigureAwait(false));
        }
    }

    public ValueTask<CortexOfflineHotRefreshResult> RefreshHotAsync(
        CortexOfflineHotRefreshRequest request,
        CancellationToken cancellationToken = default) =>
        WriteHotAsync(request, merge: false, cancellationToken);

    public ValueTask<CortexOfflineHotRefreshResult> MergeHotAsync(
        CortexOfflineHotRefreshRequest request,
        CancellationToken cancellationToken = default) =>
        WriteHotAsync(request, merge: true, cancellationToken);

    public async ValueTask<IReadOnlyList<CortexContextItemV2>> GetPendingContextItemsAsync(
        int limit = CortexOfflineLimits.MaximumPageSize,
        CancellationToken cancellationToken = default)
    {
        RequirePage(limit);
        var inventory = await LoadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CortexContextItemV2>(Math.Min(limit, inventory.Count));
        foreach (var value in inventory.Where(value => value.Decoded.Status == CortexOfflineFrameStatus.Pending))
        {
            var payload = CortexOfflineFrameCodec.DecodeKnowledgePayload(value.Decoded);
            result.Add(new CortexContextItemV2(
                payload.Namespace,
                "offline-knowledge-candidate",
                payload.RecordId,
                0,
                payload.Title,
                Excerpt(payload.Content),
                payload.Kind,
                payload.Tier,
                null,
                0));
            if (result.Count == limit)
                break;
        }
        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<CortexOfflineHotRefreshResult> WriteHotAsync(
        CortexOfflineHotRefreshRequest request,
        bool merge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CortexFileV2Validation.RequireIdentifier(request.ContextCursor, nameof(request.ContextCursor));
        RequireReadNamespaces(request.EffectiveNamespaces);
        ArgumentNullException.ThrowIfNull(request.Items);
        if (request.EffectiveNamespaces.Count == 0 && (request.Items.Count != 0 || request.HasMore))
            throw new ArgumentException("An unavailable HOT projection must be empty and complete.", nameof(request));
        foreach (var item in request.Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!request.EffectiveNamespaces.Contains(item.Namespace, StringComparer.Ordinal))
                throw new InvalidOperationException("HOT items cannot escape the effective namespaces.");
        }
        var items = request.Items.ToList();
        // An unavailable personal-only projection replaces derived context with
        // an empty snapshot without hydrating old HOT or pending personal frames.
        // The manifest and pending frames retain their original task binding.
        if (merge && request.EffectiveNamespaces.Count > 0)
        {
            var keys = items.Select(ItemKey).ToHashSet(StringComparer.Ordinal);
            var pending = await GetPendingContextItemsAsync(
                CortexOfflineLimits.MaximumPageSize,
                cancellationToken).ConfigureAwait(false);
            items.AddRange(pending.Where(item =>
                request.EffectiveNamespaces.Contains(item.Namespace, StringComparer.Ordinal) && keys.Add(ItemKey(item))));
        }
        var hotPath = CortexFileV2PathPolicy.OfflineHotPath(_projectRoot, _taskId);
        if (merge && request.EffectiveNamespaces.Count > 0 && System.IO.File.Exists(hotPath))
        {
            try
            {
                var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                    hotPath,
                    _manifest.HotMaximumUtf8Bytes,
                    cancellationToken).ConfigureAwait(false);
                var existing = JsonSerializer.Deserialize<CortexOfflineHotV1>(bytes, CortexFileV2Json.Options);
                if (existing is not null && existing.Generated && !existing.Authoritative &&
                    string.Equals(existing.Schema, CortexOfflineSchemas.HotV1, StringComparison.Ordinal) &&
                    string.Equals(existing.TaskId, _taskId, StringComparison.Ordinal) &&
                    string.Equals(existing.ProjectId, _manifest.ProjectId, StringComparison.Ordinal))
                {
                    var keys = items.Select(ItemKey).ToHashSet(StringComparer.Ordinal);
                    items.AddRange(existing.Items.Where(item =>
                        !string.Equals(item.RecordType, "offline-knowledge-candidate", StringComparison.Ordinal) &&
                        request.EffectiveNamespaces.Contains(item.Namespace, StringComparer.Ordinal) &&
                        keys.Add(ItemKey(item))));
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // HOT is explicitly disposable; refresh below replaces a bad projection.
            }
        }

        var candidateCount = items.Count;
        CortexOfflineHotV1 hot;
        byte[] serialized;
        do
        {
            hot = new CortexOfflineHotV1(
                CortexOfflineSchemas.HotV1,
                true,
                false,
                _taskId,
                _manifest.ProjectId,
                request.ContextCursor,
                UtcNow(),
                request.EffectiveNamespaces.ToArray(),
                items,
                request.HasMore || items.Count < candidateCount);
            serialized = JsonSerializer.SerializeToUtf8Bytes(hot, CortexFileV2Json.Options);
            if (serialized.Length <= _manifest.HotMaximumUtf8Bytes)
                break;
            if (items.Count == 0)
                throw new InvalidDataException("The HOT bound cannot hold its generated metadata.");
            items.RemoveAt(items.Count - 1);
        } while (true);

        await CortexFileV2AtomicIo.WriteAtomicAsync(
            hotPath,
            serialized,
            _manifest.HotMaximumUtf8Bytes,
            CortexFileV2PathPolicy.OfflineScopeRoot(_projectRoot, _taskId),
            cancellationToken).ConfigureAwait(false);
        return new(request.ContextCursor, items.Count, serialized.Length,
            request.HasMore || items.Count < candidateCount);
    }

    private async ValueTask<IReadOnlyList<InventoryItem>> LoadInventoryAsync(CancellationToken cancellationToken)
    {
        var directory = CortexFileV2PathPolicy.OfflineFramesRoot(_projectRoot, _taskId);
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(
            directory,
            CortexFileV2PathPolicy.OfflineScopeRoot(_projectRoot, _taskId));
        var files = new DirectoryInfo(directory).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > CortexOfflineLimits.MaximumTotalFrames)
            throw new InvalidDataException("The offline frame set exceeds its total frame bound.");
        var result = new List<InventoryItem>(files.Length);
        var unresolvedUtf8Bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CortexFileV2PathPolicy.RejectFileReparsePoint(file.FullName);
            var frameId = Path.GetFileNameWithoutExtension(file.Name);
            CortexFileV2Validation.RequireIdentifier(frameId, nameof(frameId));
            var expected = CortexFileV2PathPolicy.OfflineFramePath(_projectRoot, _taskId, frameId);
            if (!string.Equals(Path.GetFullPath(file.FullName), expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException("An offline frame path is not canonical.");
            }
            var bytes = await ReadFrameBytesAsync(expected, cancellationToken).ConfigureAwait(false);
            var decoded = CortexOfflineFrameCodec.Decode(bytes);
            if (decoded.Frame is not null && !string.Equals(decoded.Frame.FrameId, frameId, StringComparison.Ordinal))
                throw new InvalidDataException("An offline frame ID does not match its file name.");
            if (decoded.Status != CortexOfflineFrameStatus.Receipt &&
                (unresolvedUtf8Bytes += bytes.Length) > CortexOfflineLimits.MaximumPendingUtf8Bytes)
            {
                throw new InvalidDataException("The unresolved offline frame set exceeds its aggregate byte bound.");
            }
            result.Add(new(frameId, expected, bytes, decoded));
        }
        return result;
    }

    private async ValueTask<CortexOfflineFrameSummary> SummarizeAsync(
        string path,
        byte[] bytes,
        CortexOfflineDecodedFrame decoded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameId = Path.GetFileNameWithoutExtension(path);
        if (decoded.Frame is null)
        {
            return new(frameId, decoded.Status, decoded.OuterSchema, null, null, null, null, null, null, bytes.Length);
        }
        if (!string.Equals(decoded.Frame.FrameId, frameId, StringComparison.Ordinal))
            throw new InvalidDataException("An offline frame ID does not match its file name.");
        if (decoded.Status == CortexOfflineFrameStatus.Pending)
        {
            var payload = CortexOfflineFrameCodec.DecodeKnowledgePayload(decoded);
            return new(frameId, decoded.Status, decoded.Frame.Schema, decoded.Frame.PayloadSchema,
                decoded.Frame.CreatedAtUtc, payload.Namespace, payload.RecordId, payload.Title,
                Excerpt(payload.Content), bytes.Length);
        }
        if (decoded.Status == CortexOfflineFrameStatus.Receipt)
        {
            var payload = CortexOfflineFrameCodec.DecodeReceiptPayload(decoded);
            return new(frameId, decoded.Status, decoded.Frame.Schema, decoded.Frame.PayloadSchema,
                decoded.Frame.CreatedAtUtc, payload.EffectiveNamespace, payload.RecordId, payload.Title,
                null, bytes.Length);
        }
        return new(frameId, decoded.Status, decoded.Frame.Schema, decoded.Frame.PayloadSchema,
            decoded.Frame.CreatedAtUtc, null, null, null, null, bytes.Length);
    }

    private ValueTask<byte[]> ReadFrameBytesAsync(string path, CancellationToken cancellationToken) =>
        CortexFileV2AtomicIo.ReadExactAsync(path, CortexOfflineLimits.MaximumFrameUtf8Bytes, cancellationToken);

    private async ValueTask<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lockPath = CortexFileV2PathPolicy.OfflineWriteLockPath(_projectRoot, _taskId);
                return CortexFileV2AtomicIo.OpenWriteLease(lockPath);
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private void RequireWritableNamespace(string value)
    {
        CortexFileV2Validation.RequireIdentifier(value, nameof(value));
        if (!string.Equals(value, _manifest.WriteNamespace, StringComparison.Ordinal))
            throw new InvalidOperationException("Offline candidates must target the project's writable namespace.");
    }

    private void RequireReadNamespaces(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > CortexV2Limits.MaximumNamespaces ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException("HOT namespaces must be explicit, unique, and bounded.", nameof(values));
        }
        foreach (var value in values)
        {
            CortexFileV2Validation.RequireIdentifier(value, nameof(values));
            if (!_manifest.ReadNamespaces.Contains(value, StringComparer.Ordinal))
                throw new InvalidOperationException("HOT cannot include a namespace outside the project binding.");
        }
    }

    private static void RequirePage(int value)
    {
        if (value is < 1 or > CortexOfflineLimits.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static string Excerpt(string value)
    {
        var normalized = value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return normalized.Length <= CortexV2Limits.MaximumReceiptExcerptCharacters
            ? normalized
            : normalized[..CortexV2Limits.MaximumReceiptExcerptCharacters];
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string ItemKey(CortexContextItemV2 item) =>
        $"{item.Namespace}\n{item.RecordType}\n{item.RecordId}";

    private static bool ReceiptEquivalent(
        CortexOfflineReceiptPayloadV1 left,
        CortexOfflineReceiptPayloadV1 right) =>
        string.Equals(left.CandidateFrameId, right.CandidateFrameId, StringComparison.Ordinal) &&
        string.Equals(left.CandidateFrameSha256, right.CandidateFrameSha256, StringComparison.Ordinal) &&
        string.Equals(left.CandidatePayloadSha256, right.CandidatePayloadSha256, StringComparison.Ordinal) &&
        string.Equals(left.CandidateSemanticSha256, right.CandidateSemanticSha256, StringComparison.Ordinal) &&
        string.Equals(left.EffectiveNamespace, right.EffectiveNamespace, StringComparison.Ordinal) &&
        string.Equals(left.RecordId, right.RecordId, StringComparison.Ordinal) &&
        left.Revision == right.Revision && left.Kind == right.Kind && left.Tier == right.Tier &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.VerifiedContentSha256, right.VerifiedContentSha256, StringComparison.Ordinal);

    private static bool BindingEquivalent(
        CortexFileProjectManifestV2 left,
        CortexFileProjectManifestV2 right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.WriteNamespace, right.WriteNamespace, StringComparison.Ordinal) &&
        left.ReadNamespaces.SequenceEqual(right.ReadNamespaces, StringComparer.Ordinal);

    private static async ValueTask RequireSharedBindingCompatibleAsync(
        string root,
        CortexFileProjectManifestV2 requested,
        CancellationToken cancellationToken)
    {
        var path = CortexFileV2PathPolicy.ManifestPath(root);
        if (Directory.Exists(path))
            throw new InvalidDataException("A directory occupies the shared project namespace metadata path.");
        if (!System.IO.File.Exists(path))
            return;
        var shared = await CortexFileProjectManifestV2.ReadAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (!BindingEquivalent(shared, requested))
        {
            throw new InvalidOperationException(
                "The shared project namespace metadata conflicts with the offline task binding.");
        }
    }

    private static DirectoryCreationState CaptureDirectoryCreationState(string root, string taskId)
    {
        var paths = new[]
        {
            Path.Combine(root, ".cortex"),
            CortexFileV2PathPolicy.OfflineRoot(root),
            CortexFileV2PathPolicy.OfflineScopesRoot(root),
            CortexFileV2PathPolicy.OfflineScopeRoot(root, taskId),
            CortexFileV2PathPolicy.OfflineFramesRoot(root, taskId)
        };
        return new(paths.Select(path => new DirectoryState(path, Directory.Exists(path))).ToArray());
    }

    private static void TryRemoveNewEmptyDirectories(DirectoryCreationState state)
    {
        foreach (var directory in state.Directories.Reverse())
        {
            if (directory.Existed || !Directory.Exists(directory.Path))
                continue;
            try
            {
                var info = new DirectoryInfo(directory.Path);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    info.EnumerateFileSystemInfos().Any())
                {
                    continue;
                }
                Directory.Delete(directory.Path, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the initialization failure; never broaden cleanup or
                // remove a directory that ceased to be empty concurrently.
            }
        }
    }

    private static bool ScopeManifestEquivalent(
        CortexOfflineTaskManifestV1 left,
        CortexOfflineTaskManifestV1 right) =>
        string.Equals(left.Schema, right.Schema, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.WriteNamespace, right.WriteNamespace, StringComparison.Ordinal) &&
        left.ReadNamespaces.SequenceEqual(right.ReadNamespaces, StringComparer.Ordinal) &&
        left.HotMaximumUtf8Bytes == right.HotMaximumUtf8Bytes &&
        left.ChunkMaximumRecords == right.ChunkMaximumRecords &&
        left.ChunkMaximumUtf8Bytes == right.ChunkMaximumUtf8Bytes;

    private sealed record InventoryItem(
        string FrameId,
        string Path,
        byte[] Bytes,
        CortexOfflineDecodedFrame Decoded);

    private sealed record DirectoryState(string Path, bool Existed);

    private sealed record DirectoryCreationState(IReadOnlyList<DirectoryState> Directories);
}
