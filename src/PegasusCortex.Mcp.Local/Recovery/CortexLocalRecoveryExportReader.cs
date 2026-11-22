using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// SQLite-independent verifier and reader for an exported recovery run. It
/// validates the receipt, manifest, exact payload hashes, typed scope, and
/// complete revision chains before returning the hot project brief.
/// </summary>
internal static class CortexLocalRecoveryExportReader
{
    public static async ValueTask<CortexLocalRecoveryExportReadback> ReadAsync(
        string outputRoot,
        CortexLocalRecoveryExportLimits? limits,
        CancellationToken cancellationToken)
    {
        var bounds = (limits ?? new CortexLocalRecoveryExportLimits()).Validate();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The recovery export root does not exist.");
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(root);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(root);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(root, CortexUserStateAccess.Read);

        var receiptBytes = await ReadExactAsync(
            root,
            "receipt.json",
            64 * 1024,
            cancellationToken).ConfigureAwait(false);
        var receipt = Exact<CortexLocalRecoveryExportReceipt>(receiptBytes);
        var receiptTemplate = receipt with
        {
            ReceiptHash = CortexLocalRecoveryExportContractIds.EmptySha256
        };
        if (receipt.SchemaVersion != 1 ||
            !string.Equals(
                receipt.ContractId,
                CortexLocalRecoveryExportContractIds.Receipt,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Canonicalization,
                CortexLocalRecoveryExportContractIds.Canonicalization,
                StringComparison.Ordinal) ||
            !IsHash(receipt.ReceiptHash) ||
            !string.Equals(
                receipt.ReceiptHash,
                CortexCanonicalJson.Sha256(
                    CortexCanonicalJson.Serialize(receiptTemplate)),
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ManifestRelativePath,
                "manifest.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery export receipt failed its canonical integrity boundary.");
        }

        var manifestBytes = await ReadExactAsync(
            root,
            receipt.ManifestRelativePath,
            bounds.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);
        if (manifestBytes.Length != receipt.ManifestByteCount ||
            !string.Equals(
                CortexCanonicalJson.Sha256(manifestBytes),
                receipt.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery export manifest does not match its receipt.");
        }
        var manifest = Exact<CortexLocalRecoveryExportManifest>(manifestBytes);
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(
                manifest.ContractId,
                CortexLocalRecoveryExportContractIds.Manifest,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Canonicalization,
                CortexLocalRecoveryExportContractIds.Canonicalization,
                StringComparison.Ordinal) ||
            !CortexLocalRecoveryRunIdPolicy.IsValid(manifest.RunId) ||
            manifest.CapturedAt.Offset != TimeSpan.Zero ||
            !ValidIdentifier(manifest.ProfileId, 64) ||
            !IsHash(manifest.RegistryHash) ||
            !ValidIdentifier(manifest.TenantId, 128) ||
            !ValidIdentifier(manifest.ProjectLinkId, 128) ||
            !ValidIdentifier(manifest.ProjectId, 128) ||
            !ValidIdentifier(manifest.ProjectNamespace, 128) ||
            !string.Equals(manifest.RunId, receipt.RunId, StringComparison.Ordinal) ||
            manifest.CapturedAt != receipt.CapturedAt ||
            !string.Equals(
                manifest.ProjectLinkId,
                receipt.ProjectLinkId,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.ProjectId, receipt.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ProjectNamespace,
                receipt.ProjectNamespace,
                StringComparison.Ordinal) ||
            manifest.EntryCount != manifest.Entries.Count ||
            manifest.EntryCount != receipt.EntryCount ||
            manifest.EntryCount is < 1 ||
            manifest.EntryCount > bounds.MaximumEntries ||
            manifest.ExportedBytes != receipt.ExportedBytes ||
            manifest.ExportedBytes is < 2 ||
            manifest.ExportedBytes > bounds.MaximumExportedBytes)
        {
            throw new InvalidDataException(
                "The recovery export manifest failed its bounded scope boundary.");
        }

        var registration = new CortexLocalProjectRegistration(
            manifest.TenantId,
            manifest.ProjectLinkId,
            manifest.ProjectId,
            manifest.ProjectNamespace,
            root,
            CortexCanonicalJson.Sha256(root),
            AllowRead: true,
            AllowWrite: false);
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "manifest.json",
            "receipt.json"
        };
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<CortexLocalRecoveryExportEntry>>(
            StringComparer.Ordinal);
        long observedBytes = 0;
        byte[]? briefBytes = null;
        CortexProjectBriefSnapshot? brief = null;
        var taskHeads = new Dictionary<string, AgentTaskSnapshotV1>(
            StringComparer.Ordinal);
        var taskRevisions = new Dictionary<string, List<AgentTaskSnapshotV1>>(
            StringComparer.Ordinal);
        var packets = new Dictionary<string, AgentWorkPacketV1>(
            StringComparer.Ordinal);
        var handoffs = new Dictionary<string, AgentWorkPacketHandoffReceiptV1>(
            StringComparer.Ordinal);
        var acceptances = new List<AgentWorkPacketAcceptanceReceiptV1>();
        var retainedDocuments = new List<CortexRetainedDocument>(
            manifest.EntryCount);

        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity =
                $"{entry.RepositoryProjectId}\n{entry.Kind}\n{entry.DocumentId}\n{entry.Revision}";
            if (!identities.Add(identity) ||
                entry.ByteCount is < 2 or >
                    CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes ||
                !IsHash(entry.Sha256) ||
                entry.CapturedAt.Offset != TimeSpan.Zero ||
                entry.ExpiresAt.Offset != TimeSpan.Zero ||
                entry.ExpiresAt <= entry.CapturedAt)
            {
                throw new InvalidDataException(
                    "A recovery export entry is duplicate or invalid.");
            }

            var expectedPath = entry.Kind ==
                CortexRetainedDocumentKinds.ProjectBrief
                    ? "project-brief.json"
                    : CortexLocalRecoveryExporter.RelativeRecordPath(
                        Placeholder(entry));
            if (!string.Equals(entry.RelativePath, expectedPath, StringComparison.Ordinal) ||
                !expectedFiles.Add(entry.RelativePath))
            {
                throw new InvalidDataException(
                    "A recovery export entry has an unsafe or colliding path.");
            }
            var bytes = await ReadExactAsync(
                root,
                entry.RelativePath,
                CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytes.Length != entry.ByteCount ||
                !string.Equals(
                    CortexCanonicalJson.Sha256(bytes),
                    entry.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A recovery export payload does not match its manifest.");
            }
            var retained = Placeholder(entry) with { CanonicalUtf8 = bytes };
            CortexLocalRecoveryExporter.RequireExactDocument(
                retained,
                registration);
            retainedDocuments.Add(retained.Snapshot());
            observedBytes = checked(observedBytes + bytes.Length);
            if (entry.Kind == CortexRetainedDocumentKinds.ProjectBrief)
            {
                if (briefBytes is not null)
                    throw new InvalidDataException(
                        "The recovery export contains more than one project brief.");
                briefBytes = bytes;
                brief = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
                    bytes);
            }
            else
            {
                var key =
                    $"{entry.RepositoryProjectId}\n{entry.Kind}\n{entry.DocumentId}";
                if (!groups.TryGetValue(key, out var group))
                {
                    group = [];
                    groups.Add(key, group);
                }
                group.Add(entry);
                if (entry.Kind == CortexRetainedDocumentKinds.TaskSnapshot)
                {
                    var task = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                        bytes);
                    if (!taskRevisions.TryGetValue(
                            entry.DocumentId,
                            out var revisions))
                    {
                        revisions = [];
                        taskRevisions.Add(entry.DocumentId, revisions);
                    }
                    revisions.Add(task);
                    if (!taskHeads.TryGetValue(entry.DocumentId, out var current) ||
                        task.SnapshotRevision > current.SnapshotRevision)
                    {
                        taskHeads[entry.DocumentId] = task;
                    }
                }
                else if (entry.Kind == CortexRetainedDocumentKinds.WorkPacket)
                {
                    var packet = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
                        bytes);
                    packets.Add(
                        $"{packet.WorkId}\n{packet.PacketRevision}",
                        packet);
                }
                else if (entry.Kind ==
                         CortexRetainedDocumentKinds.WorkPacketHandoffReceipt)
                {
                    var handoff =
                        CortexCanonicalJson.Deserialize<AgentWorkPacketHandoffReceiptV1>(
                            bytes);
                    handoffs.Add(handoff.ReceiptId, handoff);
                }
                else if (entry.Kind ==
                         CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt)
                {
                    acceptances.Add(
                        CortexCanonicalJson.Deserialize<
                            AgentWorkPacketAcceptanceReceiptV1>(bytes));
                }
            }
        }

        if (briefBytes is null ||
            brief is null ||
            taskHeads.Count != 1 ||
            observedBytes != manifest.ExportedBytes)
        {
            throw new InvalidDataException(
                "The recovery export is missing its brief or has the wrong byte total.");
        }
        foreach (var group in groups.Values)
        {
            var ordered = group.OrderBy(entry => entry.Revision).ToArray();
            string? previousHash = null;
            for (var index = 0; index < ordered.Length; index++)
            {
                var entry = ordered[index];
                if (entry.Revision != index + 1 ||
                    !string.Equals(
                        entry.PreviousHash,
                        previousHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "An exported immutable revision chain is incomplete.");
                }
                previousHash = entry.Sha256;
            }
        }

        var taskHead = taskHeads.Values.Single();
        var briefTasks = brief.HotTasks.Prepend(brief.CurrentTask);
        if (brief.StateRevision != taskHead.SnapshotRevision ||
            !string.Equals(
                brief.SourceHash,
                taskHead.SnapshotHash,
                StringComparison.Ordinal) ||
            briefTasks.Any(task =>
                !string.Equals(
                    task.Source.ProducerContract,
                    taskHead.ContractId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    task.Source.SourceId,
                    taskHead.TaskSetId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    task.Source.SourceRevisionId,
                    taskHead.SnapshotId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The exported brief does not bind the sole complete task head.");
        }
        foreach (var revisions in taskRevisions.Values)
        {
            var ordered = revisions
                .OrderBy(value => value.SnapshotRevision)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (!AgentTaskSnapshotPolicy.ValidateRevision(
                        ordered[index - 1],
                        ordered[index]).Valid)
                {
                    throw new InvalidDataException(
                        "An exported task-snapshot chain is not a valid typed revision lineage.");
                }
            }
        }
        foreach (var work in packets.Values
                     .GroupBy(value => value.WorkId, StringComparer.Ordinal))
        {
            var ordered = work
                .OrderBy(value => value.PacketRevision)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (!AgentWorkPacketPolicy.ValidateContinuation(
                        ordered[index - 1],
                        ordered[index]).Valid)
                {
                    throw new InvalidDataException(
                        "An exported work-packet chain is not a valid typed continuation lineage.");
                }
            }
        }
        var briefScope = new CortexProjectBriefReadScope(
            manifest.TenantId,
            manifest.ProjectLinkId,
            manifest.ProjectId,
            manifest.ProjectNamespace,
            "local-export-recovery",
            "local-os-user",
            "pegasus-cortex-local",
            PolicyRevision: 1);
        var semanticRead = await new CortexProjectBriefService(
                new InMemoryCortexProjectBriefReadModel([brief]),
                new FixedTimeProvider(manifest.CapturedAt))
            .ReadAsync(
                new CortexProjectBriefRequest(
                    manifest.ProjectLinkId,
                    brief.AuthoritySetHash),
                briefScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (!semanticRead.Accepted ||
            semanticRead.Response.FallbackRequired ||
            !string.Equals(
                semanticRead.Response.ContextSetId,
                brief.ContextSetId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exported project brief failed the normal semantic recovery boundary.");
        }
        foreach (var handoff in handoffs.Values)
        {
            if (!packets.TryGetValue(
                    $"{handoff.WorkId}\n{handoff.PacketRevision}",
                    out var packet) ||
                !AgentWorkPacketPolicy.ValidateHandoff(packet, handoff).Valid)
            {
                throw new InvalidDataException(
                    "An exported handoff receipt lacks its exact valid packet.");
            }
        }
        foreach (var acceptance in acceptances)
        {
            if (!handoffs.TryGetValue(
                    acceptance.HandoffReceiptId,
                    out var handoff) ||
                !AgentWorkPacketPolicy.ValidateAcceptance(
                    handoff,
                    acceptance).Valid)
            {
                throw new InvalidDataException(
                    "An exported acceptance receipt lacks its exact valid handoff.");
            }
        }

        var expectedDirectories = ExpectedDirectories(expectedFiles);
        var actual = EnumerateSafeTree(
            root,
            checked(bounds.MaximumEntries * 4 + 16));
        if (!actual.Files.SetEquals(expectedFiles) ||
            !actual.Directories.SetEquals(expectedDirectories))
            throw new InvalidDataException(
                "The recovery export contains missing or unmanifested entries.");

        return new CortexLocalRecoveryExportReadback(
            manifest,
            receipt,
            briefBytes.ToArray(),
            Array.AsReadOnly(
                retainedDocuments.Select(item => item.Snapshot()).ToArray()));
    }

    private static CortexRetainedDocument Placeholder(
        CortexLocalRecoveryExportEntry entry) =>
        new(
            entry.RepositoryProjectId,
            entry.Kind,
            entry.DocumentId,
            entry.Revision,
            entry.PreviousHash,
            entry.IdempotencyKey,
            entry.ProducerId,
            entry.ContractId,
            "{}"u8.ToArray(),
            entry.Sha256,
            entry.SearchText,
            entry.CapturedAt,
            entry.ExpiresAt);

    private static async ValueTask<byte[]> ReadExactAsync(
        string root,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.Split('/').Any(segment =>
                segment is "" or "." or ".."))
        {
            throw new InvalidDataException("A recovery export path is unsafe.");
        }
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        CortexLocalRecoveryExportPathPolicy.RequireOutputPath(root, path);
        var info = new FileInfo(path);
        if (!info.Exists ||
            info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 2 ||
            info.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "A recovery export file is absent, linked, or oversized.");
        }
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static T Exact<T>(byte[] bytes)
    {
        try
        {
            var value = CortexCanonicalJson.Deserialize<T>(bytes);
            if (!CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Recovery metadata is not canonical JSON.");
            return value;
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Text.Json.JsonException or
                InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException(
                "Recovery metadata failed canonical validation.",
                exception);
        }
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidIdentifier(string? value, int maximumCharacters) =>
        value is { Length: > 0 } &&
        value.Length <= maximumCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static HashSet<string> ExpectedDirectories(
        IEnumerable<string> files)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var current = file;
            while (current.LastIndexOf('/') is var separator && separator >= 0)
            {
                current = current[..separator];
                if (current.Length > 0)
                    result.Add(current);
            }
        }
        return result;
    }

    private static RecoveryTreeEntries EnumerateSafeTree(
        string root,
        int maximumEntries)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var observed = 0;
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++observed > maximumEntries)
                    throw new InvalidDataException(
                        "The recovery export tree exceeds its entry boundary.");
                var info = new FileInfo(entry);
                var attributes = File.GetAttributes(entry);
                if (info.LinkTarget is not null ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The recovery export tree contains a link or reparse point.");
                }
                var relative = Path.GetRelativePath(root, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Add(relative))
                        throw new InvalidDataException(
                            "The recovery export tree repeated a directory.");
                    pending.Enqueue(entry);
                }
                else if (!files.Add(relative))
                {
                    throw new InvalidDataException(
                        "The recovery export tree repeated a file.");
                }
            }
        }
        return new RecoveryTreeEntries(files, directories);
    }

    private sealed record RecoveryTreeEntries(
        HashSet<string> Files,
        HashSet<string> Directories);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
    }
}
