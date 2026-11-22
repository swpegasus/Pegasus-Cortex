using System.Diagnostics;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Persistence.File.V2;

/// <summary>
/// Portable reference implementation of the V2 operational store. Immutable
/// revision objects and immutable chunked generations are made visible by one
/// atomically replaced current pointer. No request inherits namespace state.
/// </summary>
public sealed partial class FileCortexV2Store : ICortexV2Store
{
    private const string TaskRecordType = "task";
    private const string KnowledgeRecordType = "knowledge";
    private readonly string _projectRoot;
    private readonly CortexFileProjectManifestV2 _manifest;
    private readonly TimeProvider _timeProvider;

    private FileCortexV2Store(
        string projectRoot,
        CortexFileProjectManifestV2 manifest,
        TimeProvider timeProvider)
    {
        _projectRoot = projectRoot;
        _manifest = manifest;
        _timeProvider = timeProvider;
    }

    public string ProjectRoot => _projectRoot;
    public CortexFileProjectManifestV2 ProjectManifest => _manifest;

    public static async ValueTask<FileCortexV2Store> OpenAsync(
        string projectRoot,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var manifest = await CortexFileProjectManifestV2.ReadAsync(root, cancellationToken)
            .ConfigureAwait(false);
        CortexFileV2PathPolicy.PrepareStoreDirectories(root);
        return new FileCortexV2Store(root, manifest, timeProvider ?? TimeProvider.System);
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private void RequireReadNamespaces(IReadOnlyList<string> namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        if (namespaces.Count is < 1 or > CortexV2Limits.MaximumNamespaces ||
            namespaces.Distinct(StringComparer.Ordinal).Count() != namespaces.Count)
        {
            throw new ArgumentException("Effective namespaces must be explicit, unique, and bounded.", nameof(namespaces));
        }
        foreach (var value in namespaces)
        {
            CortexFileV2Validation.RequireIdentifier(value, nameof(namespaces));
            if (!_manifest.ReadNamespaces.Contains(value, StringComparer.Ordinal))
                throw new InvalidOperationException($"Namespace '{value}' is not attached to this file project.");
        }
    }

    private void RequireReadableNamespace(string value) => RequireReadNamespaces([value]);

    private void RequireWritableNamespace(string value)
    {
        RequireReadableNamespace(value);
        if (!string.Equals(value, _manifest.WriteNamespace, StringComparison.Ordinal))
            throw new InvalidOperationException($"Namespace '{value}' is attached read-only to this file project.");
    }

    private async ValueTask<CortexFileV2State> LoadStateAsync(CancellationToken cancellationToken)
    {
        var pointerPath = CortexFileV2PathPolicy.CurrentPointerPath(_projectRoot);
        if (!System.IO.File.Exists(pointerPath))
            return new CortexFileV2State(0, "ctx-empty", DateTimeOffset.UnixEpoch);

        CortexFileV2Pointer pointer;
        try
        {
            var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                pointerPath,
                CortexFileV2Limits.MaximumPointerUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
            pointer = JsonSerializer.Deserialize<CortexFileV2Pointer>(bytes, CortexFileV2Json.Options) ??
                throw new InvalidDataException("The current file Cortex pointer is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The current file Cortex pointer is malformed.", exception);
        }
        if (!string.Equals(pointer.Schema, CortexFileV2Schemas.Pointer, StringComparison.Ordinal) ||
            pointer.GenerationRevision < 1 ||
            pointer.CommittedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The current file Cortex pointer is invalid.");
        }
        CortexFileV2Validation.RequireIdentifier(pointer.ContextCursor, nameof(pointer.ContextCursor));

        var generationDirectory = ResolveGenerationPath(pointer.GenerationPath);
        var generationPath = Path.Combine(generationDirectory, "generation.json");
        CortexFileV2Generation generation;
        try
        {
            var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                generationPath,
                CortexFileV2Limits.MaximumGenerationManifestUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
            generation = JsonSerializer.Deserialize<CortexFileV2Generation>(bytes, CortexFileV2Json.Options) ??
                throw new InvalidDataException("The file Cortex generation is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The file Cortex generation is malformed.", exception);
        }
        if (!string.Equals(generation.Schema, CortexFileV2Schemas.Generation, StringComparison.Ordinal) ||
            generation.Revision != pointer.GenerationRevision ||
            generation.CommittedAtUtc != pointer.CommittedAtUtc ||
            generation.IndexChunks.Count > 65_536 ||
            generation.ReceiptChunks.Count > 65_536)
        {
            throw new InvalidDataException("The file Cortex generation does not match its pointer.");
        }

        var entries = new List<CortexFileV2IndexEntry>();
        for (var index = 0; index < generation.IndexChunks.Count; index++)
        {
            var chunk = await ReadGenerationDocumentAsync<CortexFileV2IndexChunk>(
                generationDirectory,
                generation.IndexChunks[index],
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(chunk.Schema, CortexFileV2Schemas.IndexChunk, StringComparison.Ordinal) ||
                chunk.GenerationRevision != generation.Revision || chunk.Index != index)
            {
                throw new InvalidDataException("A file Cortex index chunk is out of sequence.");
            }
            entries.AddRange(chunk.Items);
        }

        var receipts = new List<CortexFileV2Receipt>();
        for (var index = 0; index < generation.ReceiptChunks.Count; index++)
        {
            var chunk = await ReadGenerationDocumentAsync<CortexFileV2ReceiptChunk>(
                generationDirectory,
                generation.ReceiptChunks[index],
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(chunk.Schema, CortexFileV2Schemas.ReceiptChunk, StringComparison.Ordinal) ||
                chunk.GenerationRevision != generation.Revision || chunk.Index != index)
            {
                throw new InvalidDataException("A file Cortex receipt chunk is out of sequence.");
            }
            receipts.AddRange(chunk.Items);
        }
        foreach (var receipt in receipts)
        {
            if (receipt.GenerationRevision is < 1 || receipt.GenerationRevision > generation.Revision ||
                receipt.CommittedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("A file Cortex receipt is invalid.");
            }
            RequireReadableNamespace(receipt.Namespace);
            CortexFileV2Validation.RequireIdentifier(receipt.IdempotencyKey, nameof(receipt.IdempotencyKey));
        }
        ValidateLoadedState(generation, entries, receipts);
        return new CortexFileV2State(
            generation.Revision,
            pointer.ContextCursor,
            generation.CommittedAtUtc,
            entries,
            receipts);
    }

    private async ValueTask<T> ReadGenerationDocumentAsync<T>(
        string generationDirectory,
        string relative,
        CancellationToken cancellationToken)
    {
        var path = ResolveBelow(generationDirectory, relative);
        try
        {
            var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                path,
                CortexFileV2Limits.MaximumChunkUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(bytes, CortexFileV2Json.Options) ??
                throw new InvalidDataException("A file Cortex generation document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A file Cortex generation document is malformed.", exception);
        }
    }

    private void ValidateLoadedState(
        CortexFileV2Generation generation,
        IReadOnlyList<CortexFileV2IndexEntry> entries,
        IReadOnlyList<CortexFileV2Receipt> receipts)
    {
        if (entries.Count != generation.RevisionCount ||
            entries.LongCount(value => value.IsHead) != generation.HeadCount ||
            receipts.Count != generation.ReceiptCount)
        {
            throw new InvalidDataException("The file Cortex generation counts do not reconcile.");
        }
        if (entries.GroupBy(value => (value.Namespace, value.RecordType, value.RecordId, value.Revision))
            .Any(group => group.Count() != 1) ||
            entries.Where(value => value.IsHead)
                .GroupBy(value => (value.Namespace, value.RecordType, value.RecordId))
                .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("The file Cortex index contains duplicate revisions or heads.");
        }
        foreach (var entry in entries)
        {
            RequireReadableNamespace(entry.Namespace);
            if (entry.RecordType is not (TaskRecordType or KnowledgeRecordType) || entry.Revision < 1 ||
                entry.UpdatedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("A file Cortex index entry is invalid.");
            }
            CortexFileV2Validation.RequireIdentifier(entry.RecordId, nameof(entry.RecordId));
            var objectPath = CortexFileV2PathPolicy.FromProjectRelative(_projectRoot, entry.ObjectPath);
            var expected = CortexFileV2PathPolicy.ObjectPath(_projectRoot, entry.ObjectHash);
            if (!string.Equals(objectPath, expected, PathComparison()))
                throw new InvalidDataException("A file Cortex index object path is not content-addressed.");
        }
    }

    private async ValueTask<CortexTaskV2> LoadTaskAsync(
        CortexFileV2IndexEntry entry,
        CancellationToken cancellationToken)
    {
        var document = await ReadObjectAsync<CortexFileV2TaskObject>(entry, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(document.Schema, CortexFileV2Schemas.TaskObject, StringComparison.Ordinal) ||
            document.Record.Namespace != entry.Namespace || document.Record.TaskId != entry.RecordId ||
            document.Record.Revision != entry.Revision)
        {
            throw new InvalidDataException("A file Cortex task object does not match its index entry.");
        }
        return document.Record;
    }

    private async ValueTask<CortexKnowledgeV2> LoadKnowledgeAsync(
        CortexFileV2IndexEntry entry,
        CancellationToken cancellationToken)
    {
        var document = await ReadObjectAsync<CortexFileV2KnowledgeObject>(entry, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(document.Schema, CortexFileV2Schemas.KnowledgeObject, StringComparison.Ordinal) ||
            document.Record.Namespace != entry.Namespace || document.Record.RecordId != entry.RecordId ||
            document.Record.Revision != entry.Revision)
        {
            throw new InvalidDataException("A file Cortex knowledge object does not match its index entry.");
        }
        return document.Record;
    }

    private async ValueTask<T> ReadObjectAsync<T>(
        CortexFileV2IndexEntry entry,
        CancellationToken cancellationToken)
    {
        var path = CortexFileV2PathPolicy.FromProjectRelative(_projectRoot, entry.ObjectPath);
        var bytes = await CortexFileV2AtomicIo.ReadExactAsync(
            path,
            CortexFileV2Limits.MaximumRecordUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(CortexFileV2AtomicIo.Sha256(bytes), entry.ObjectHash, StringComparison.Ordinal))
            throw new InvalidDataException("A file Cortex revision object failed its content-address check.");
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, CortexFileV2Json.Options) ??
                throw new InvalidDataException("A file Cortex revision object is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A file Cortex revision object is malformed.", exception);
        }
    }

    private async ValueTask AddTaskRevisionAsync(
        CortexFileV2State state,
        CortexTaskV2 task,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        var document = new CortexFileV2TaskObject(
            CortexFileV2Schemas.TaskObject,
            idempotencyKey,
            commandHash,
            task);
        var entry = await WriteObjectAsync(
            TaskRecordType,
            task.Namespace,
            task.TaskId,
            task.Revision,
            task.UpdatedAtUtc,
            document,
            cancellationToken).ConfigureAwait(false);
        ReplaceHead(state, entry);
    }

    private async ValueTask AddKnowledgeRevisionAsync(
        CortexFileV2State state,
        CortexKnowledgeV2 knowledge,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        var document = new CortexFileV2KnowledgeObject(
            CortexFileV2Schemas.KnowledgeObject,
            idempotencyKey,
            commandHash,
            knowledge);
        var entry = await WriteObjectAsync(
            KnowledgeRecordType,
            knowledge.Namespace,
            knowledge.RecordId,
            knowledge.Revision,
            knowledge.UpdatedAtUtc,
            document,
            cancellationToken).ConfigureAwait(false);
        ReplaceHead(state, entry);
    }

    private async ValueTask<CortexFileV2IndexEntry> WriteObjectAsync<T>(
        string recordType,
        string @namespace,
        string recordId,
        long revision,
        DateTimeOffset updatedAtUtc,
        T document,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, CortexFileV2Json.Options);
        var hash = CortexFileV2AtomicIo.Sha256(bytes);
        var path = CortexFileV2PathPolicy.ObjectPath(_projectRoot, hash);
        await CortexFileV2AtomicIo.WriteImmutableAsync(
            path,
            bytes,
            CortexFileV2Limits.MaximumRecordUtf8Bytes,
            CortexFileV2PathPolicy.ObjectRoot(_projectRoot),
            cancellationToken).ConfigureAwait(false);
        return new CortexFileV2IndexEntry(
            @namespace,
            recordType,
            recordId,
            revision,
            true,
            CortexFileV2PathPolicy.RelativeToProject(_projectRoot, path),
            hash,
            updatedAtUtc);
    }

    private static void ReplaceHead(CortexFileV2State state, CortexFileV2IndexEntry successor)
    {
        for (var index = 0; index < state.Entries.Count; index++)
        {
            var value = state.Entries[index];
            if (value.IsHead && value.RecordType == successor.RecordType &&
                value.Namespace == successor.Namespace && value.RecordId == successor.RecordId)
            {
                state.Entries[index] = value with { IsHead = false };
            }
        }
        state.Entries.Add(successor);
    }

    private async ValueTask CommitAsync(
        CortexFileV2State state,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(state.Revision + 1);
        var committedAtUtc = UtcNow();
        var token = Guid.NewGuid().ToString("N");
        var staging = CortexFileV2PathPolicy.GenerationStagingPath(_projectRoot, token);
        var final = CortexFileV2PathPolicy.GenerationPath(_projectRoot, nextRevision, token);
        CortexFileV2PathPolicy.PrepareOrdinaryDirectory(
            staging,
            CortexFileV2PathPolicy.GenerationsRoot(_projectRoot));
        var completed = false;
        try
        {
            var indexChunks = SplitChunks(
                state.Entries,
                items => JsonSerializer.SerializeToUtf8Bytes(
                    new CortexFileV2IndexChunk(CortexFileV2Schemas.IndexChunk, nextRevision, 0, items),
                    CortexFileV2Json.Options));
            var indexPaths = new List<string>(indexChunks.Count);
            for (var index = 0; index < indexChunks.Count; index++)
            {
                var path = Path.Combine(staging, $"index-{index:D5}.json");
                var document = new CortexFileV2IndexChunk(
                    CortexFileV2Schemas.IndexChunk,
                    nextRevision,
                    index,
                    indexChunks[index]);
                await WriteGenerationDocumentAsync(path, document, staging, cancellationToken)
                    .ConfigureAwait(false);
                indexPaths.Add(Path.GetFileName(path));
            }

            for (var receiptIndex = 0; receiptIndex < state.Receipts.Count; receiptIndex++)
            {
                var receipt = state.Receipts[receiptIndex];
                if (receipt.GenerationRevision == 0)
                {
                    state.Receipts[receiptIndex] = receipt with
                    {
                        GenerationRevision = nextRevision,
                        CommittedAtUtc = committedAtUtc
                    };
                }
            }
            var receiptChunks = SplitChunks(
                state.Receipts,
                items => JsonSerializer.SerializeToUtf8Bytes(
                    new CortexFileV2ReceiptChunk(CortexFileV2Schemas.ReceiptChunk, nextRevision, 0, items),
                    CortexFileV2Json.Options));
            var receiptPaths = new List<string>(receiptChunks.Count);
            for (var index = 0; index < receiptChunks.Count; index++)
            {
                var path = Path.Combine(staging, $"receipts-{index:D5}.json");
                var document = new CortexFileV2ReceiptChunk(
                    CortexFileV2Schemas.ReceiptChunk,
                    nextRevision,
                    index,
                    receiptChunks[index]);
                await WriteGenerationDocumentAsync(path, document, staging, cancellationToken)
                    .ConfigureAwait(false);
                receiptPaths.Add(Path.GetFileName(path));
            }
            var generation = new CortexFileV2Generation(
                CortexFileV2Schemas.Generation,
                nextRevision,
                committedAtUtc,
                indexPaths,
                receiptPaths,
                state.Entries.LongCount(value => value.IsHead),
                state.Entries.Count,
                state.Receipts.Count);
            await WriteGenerationDocumentAsync(
                Path.Combine(staging, "generation.json"),
                generation,
                staging,
                cancellationToken,
                CortexFileV2Limits.MaximumGenerationManifestUtf8Bytes).ConfigureAwait(false);

            Directory.Move(staging, final);
            var cursorSeed = JsonSerializer.SerializeToUtf8Bytes(new
            {
                revision = nextRevision,
                generation = Path.GetFileName(final)
            }, CortexFileV2Json.Options);
            var contextCursor = $"ctx-{CortexFileV2AtomicIo.Sha256(cursorSeed)[..32]}";
            var pointer = new CortexFileV2Pointer(
                CortexFileV2Schemas.Pointer,
                nextRevision,
                CortexFileV2PathPolicy.RelativeToProject(_projectRoot, final),
                contextCursor,
                committedAtUtc);
            var pointerBytes = JsonSerializer.SerializeToUtf8Bytes(pointer, CortexFileV2Json.Options);
            await CortexFileV2AtomicIo.WriteAtomicAsync(
                CortexFileV2PathPolicy.CurrentPointerPath(_projectRoot),
                pointerBytes,
                CortexFileV2Limits.MaximumPointerUtf8Bytes,
                CortexFileV2PathPolicy.DataRoot(_projectRoot),
                cancellationToken).ConfigureAwait(false);
            state.Revision = nextRevision;
            state.ContextCursor = contextCursor;
            state.CommittedAtUtc = committedAtUtc;
            completed = true;
        }
        finally
        {
            if (!completed && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private async ValueTask WriteGenerationDocumentAsync<T>(
        string path,
        T document,
        string containmentRoot,
        CancellationToken cancellationToken,
        int maximumBytes = CortexFileV2Limits.MaximumChunkUtf8Bytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, CortexFileV2Json.Options);
        await CortexFileV2AtomicIo.WriteImmutableAsync(
            path,
            bytes,
            maximumBytes,
            containmentRoot,
            cancellationToken).ConfigureAwait(false);
    }

    private List<IReadOnlyList<T>> SplitChunks<T>(
        IReadOnlyList<T> items,
        Func<IReadOnlyList<T>, byte[]> serialize)
    {
        var result = new List<IReadOnlyList<T>>();
        var current = new List<T>();
        foreach (var item in items)
        {
            if (current.Count == _manifest.ChunkMaximumRecords)
            {
                result.Add(current.ToArray());
                current.Clear();
            }
            current.Add(item);
            if (serialize(current).Length > _manifest.ChunkMaximumUtf8Bytes)
            {
                current.RemoveAt(current.Count - 1);
                if (current.Count == 0)
                    throw new InvalidDataException("One file Cortex index item exceeds the configured chunk bound.");
                result.Add(current.ToArray());
                current.Clear();
                current.Add(item);
                if (serialize(current).Length > _manifest.ChunkMaximumUtf8Bytes)
                    throw new InvalidDataException("One file Cortex index item exceeds the configured chunk bound.");
            }
        }
        if (current.Count > 0)
            result.Add(current.ToArray());
        return result;
    }

    private async ValueTask<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    CortexFileV2PathPolicy.WriteLockPath(_projectRoot),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CortexFileV2Receipt? FindReceipt(
        CortexFileV2State state,
        string @namespace,
        string operation,
        string idempotencyKey,
        string commandHash)
    {
        var receipt = state.Receipts.LastOrDefault(value =>
            value.Namespace == @namespace && value.Operation == operation &&
            value.IdempotencyKey == idempotencyKey);
        if (receipt is not null && receipt.CommandHash != commandHash)
            throw new InvalidOperationException("The idempotency key was already used for a different command.");
        return receipt;
    }

    private static string CommandHash<T>(T command) =>
        CortexFileV2AtomicIo.Sha256(JsonSerializer.SerializeToUtf8Bytes(command, CortexFileV2Json.Options));

    private static T RequireResponseBudget<T>(T response, int maximumUtf8Bytes)
    {
        var actual = JsonSerializer.SerializeToUtf8Bytes(response, CortexV2Json.Options).Length;
        if (actual > maximumUtf8Bytes)
            throw new CortexV2ResponseBudgetExceededException(maximumUtf8Bytes, actual);
        return response;
    }

    private static string TaskCommandHash(CortexTaskCommandV2 command) => CommandHash(new
    {
        Command = command with { MaxUtf8Bytes = CortexV2Limits.MinimumResponseUtf8Bytes },
        Provenance = StableProvenance(command.EffectiveProvenance)
    });

    private static string KnowledgeCommandHash(CortexKnowledgeCommandV2 command) => CommandHash(new
    {
        Command = command with { MaxUtf8Bytes = CortexV2Limits.MinimumResponseUtf8Bytes },
        Provenance = StableProvenance(command.EffectiveProvenance)
    });

    private static string FinishCommandHash(CortexFinishRequestV2 request) => CommandHash(new
    {
        Request = request with { MaxUtf8Bytes = CortexV2Limits.MinimumResponseUtf8Bytes },
        Provenance = StableProvenance(request.EffectiveProvenance),
        KnowledgeProvenance = request.Knowledge
            .Select(value => StableProvenance(value.EffectiveProvenance))
            .ToArray()
    });

    private static object? StableProvenance(CortexProvenanceV2? provenance) => provenance is null
        ? null
        : new
        {
            provenance.Source,
            provenance.Actor,
            provenance.SourceId,
            provenance.Evidence
        };

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private string ResolveGenerationPath(string projectRelative)
    {
        var path = CortexFileV2PathPolicy.FromProjectRelative(_projectRoot, projectRelative);
        var root = CortexFileV2PathPolicy.GenerationsRoot(_projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, PathComparison()) || !Directory.Exists(path))
            throw new InvalidDataException("The current pointer does not reference an ordinary generation directory.");
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A file Cortex generation is a link or reparse point.");
        return path;
    }

    private static string ResolveBelow(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) ||
            relative.Contains('/') || relative.Contains('\\'))
        {
            throw new InvalidDataException("A generation member path is invalid.");
        }
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
            throw new InvalidDataException("A generation member path escaped its generation.");
        return path;
    }
}
