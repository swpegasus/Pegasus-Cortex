using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Pegasus.AgentOps.Zip.Tests")]

namespace Pegasus.AgentOps.Engine;

public sealed record ZipLimits(
    long MaxArchiveBytes = 1L * 1024 * 1024 * 1024,
    int MaxEntries = 100_000,
    long MaxEntryBytes = 4L * 1024 * 1024 * 1024,
    long MaxExpandedBytes = 16L * 1024 * 1024 * 1024,
    double MaxExpansionRatio = 200,
    int MaxEntryNameChars = 4096)
{
    internal void Validate()
    {
        if (MaxArchiveBytes < 1 || MaxEntries < 1 || MaxEntryBytes < 0 ||
            MaxExpandedBytes < 0 || !double.IsFinite(MaxExpansionRatio) ||
            MaxExpansionRatio < 1 || MaxEntryNameChars < 1)
        {
            throw new AgentOpsException("invalid");
        }
    }
}

public sealed record ZipEntryInfo(string Name, bool Directory, long Length, long CompressedLength, string? Sha256);

public sealed record ZipInspection(
    long ArchiveBytes,
    long ExpandedBytes,
    long CompressedBytes,
    IReadOnlyList<ZipEntryInfo> Entries);

public sealed record ZipResult(
    long Deleted,
    long Copied,
    long Changed,
    bool Partial,
    long ArchiveBytes,
    long ExpandedBytes,
    int Entries);

public sealed class ZipOperationException : Exception
{
    public ZipOperationException(
        string code,
        long deleted,
        long copied,
        long changed,
        Exception? inner = null)
        : this(code, deleted, copied, changed, inner, false)
    {
    }

    public ZipOperationException(string code, long deleted, long copied, long changed,
        Exception? inner, bool temporaryCleanupFailed)
        : base(code, inner)
    {
        Code = code;
        Deleted = deleted;
        Copied = copied;
        Changed = changed;
        TemporaryCleanupFailed = temporaryCleanupFailed;
    }

    public string Code { get; }
    public long Deleted { get; }
    public long Copied { get; }
    public long Changed { get; }
    public bool TemporaryCleanupFailed { get; }
    public bool Partial => Changed > 0 || TemporaryCleanupFailed;
}

internal enum ZipFaultPoint
{
    BeforePublish,
    BeforeExtractEntry,
    BeforeAdmissionContent,
    BeforeCleanDirectoryEnumeration,
    BeforeExtractFilePublish,
    BeforeSourceEntry,
    AfterContentChunk,
    BeforeCleanEntry,
    AfterDestinationChange,
    AfterPublish
}

internal interface IZipFaultInjector
{
    void OnFaultPoint(ZipFaultPoint point, string path);
}

/// <summary>
/// Implements bounded ZIP operations without shelling out. Archive admission is
/// completed before any extraction-side filesystem mutation.
/// </summary>
public sealed class ZipOperations
{
    private const int BufferSize = 128 * 1024;
    private static readonly StringComparer WindowsNameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly char[] InvalidWindowsNameChars = ['<', '>', ':', '"', '\\', '|', '?', '*'];
    private readonly IZipFaultInjector? _faults;

    public ZipOperations()
        : this(null)
    {
    }

    internal ZipOperations(IZipFaultInjector? faults)
    {
        _faults = faults;
    }

    public ZipInspection Inspect(string src, ZipLimits? limits = null) =>
        Inspect(src, limits, CancellationToken.None);

    public ZipInspection Inspect(string src, ZipLimits? limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        limits ??= new ZipLimits();
        limits.Validate();
        src = RequiredFullPath(src);
        EnsureNoReparseComponents(src);
        using var admitted = Admit(src, limits, token);
        return admitted.ToInspection(token);
    }

    public ZipResult Create(string src, string dst, string pol = "fail", ZipLimits? limits = null) =>
        Create(src, dst, pol, limits, CancellationToken.None);

    public ZipResult Create(string src, string dst, string pol, ZipLimits? limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        limits ??= new ZipLimits();
        limits.Validate();
        if (pol is not ("fail" or "replace")) throw new AgentOpsException("invalid");

        src = Path.TrimEndingDirectorySeparator(RequiredFullPath(src));
        dst = RequiredFullPath(dst);
        if (!Directory.Exists(src)) throw new AgentOpsException("not_found");
        EnsureNoReparseComponents(src);
        EnsureNoReparseComponents(dst);
        RejectCreateRelationship(src, dst);

        var destinationDirectory = Path.GetDirectoryName(dst);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
            throw new AgentOpsException("not_found");
        if (Directory.Exists(dst)) throw new AgentOpsException("collision");
        if (File.Exists(dst) && pol != "replace") throw new AgentOpsException("collision");

        using var destinationPin = PinnedDirectoryChain.OpenExisting(destinationDirectory, token);
        var sourcePlan = AdmitSourceTree(src, limits, token);
        var temporary = UniqueSiblingPath(dst);
        var published = false;
        Exception? failure = null;
        try
        {
            destinationPin.Validate();
            token.ThrowIfCancellationRequested();
            var expected = WriteArchive(temporary, sourcePlan, limits, token);
            var archiveBytes = new FileInfo(temporary).Length;
            if (archiveBytes > limits.MaxArchiveBytes) throw new AgentOpsException("archive_limit");
            VerifyCreatedArchive(temporary, expected, limits, token);

            try
            {
                _faults?.OnFaultPoint(ZipFaultPoint.BeforePublish, dst);
                destinationPin.Validate();
                EnsureLeafNotReparse(dst);
                if (Directory.Exists(dst)) throw new AgentOpsException("collision");
                if (File.Exists(dst) && pol != "replace") throw new AgentOpsException("collision");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, dst, pol == "replace");
                published = true;
                NotifyPublished(dst);
            }
            catch (AgentOpsException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new AgentOpsException("publish_failed", inner: ex);
            }

            return new ZipResult(0, expected.Count(x => !x.Directory), 1, false, archiveBytes,
                expected.Sum(x => x.Length), expected.Count);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            // Cleanup owns its original resources even when token is canceled.
            // Never invent a destination Changed count for an unresolved temp.
            if (!published && !TryDeleteTemporary(temporary))
                throw new ZipOperationException(FailureCode(failure, "publish_failed"), 0, 0, 0, failure, true);
        }
    }

    public ZipResult Extract(string src, string dst, string pol = "fail", ZipLimits? limits = null) =>
        Extract(src, dst, pol, limits, CancellationToken.None);

    public ZipResult Extract(string src, string dst, string pol, ZipLimits? limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        limits ??= new ZipLimits();
        limits.Validate();
        if (pol is not ("fail" or "replace" or "skip" or "clean"))
            throw new AgentOpsException("invalid");

        src = RequiredFullPath(src);
        dst = Path.TrimEndingDirectorySeparator(RequiredFullPath(dst));
        if (WindowsNameComparer.Equals(dst, Path.GetPathRoot(dst)))
            throw new AgentOpsException("precondition");
        EnsureNoReparseComponents(src);
        EnsureNoReparseComponents(dst);
        RejectExtractRelationship(src, dst);

        // Admit and stream-test the entire archive before inspecting destination
        // contents for mutation or creating the destination root.
        using var admitted = Admit(src, limits, token);
        if (File.Exists(dst)) throw new AgentOpsException("collision");
        if (Directory.Exists(dst) && pol == "fail") throw new AgentOpsException("collision");

        HashSet<string> skipped = [];
        if (pol is "replace" or "skip")
            skipped = PreflightDestination(dst, admitted.Entries, pol, token);

        long deleted = 0;
        long copied = 0;
        long changed = 0;
        PinnedDirectoryChain? destinationPin = null;
        var cleaning = false;
        try
        {
            if (pol == "clean" && Directory.Exists(dst))
            {
                cleaning = true;
                destinationPin = PinnedDirectoryChain.OpenExisting(dst, token);
                _faults?.OnFaultPoint(ZipFaultPoint.BeforeCleanDirectoryEnumeration, dst);
                token.ThrowIfCancellationRequested();
                destinationPin.Validate();
                foreach (var entry in Directory.EnumerateFileSystemEntries(dst))
                    DeleteEntryNoFollow(entry, ref deleted, ref changed, token);
                cleaning = false;
            }

            destinationPin ??= PinnedDirectoryChain.OpenOrCreate(dst, ref changed, token, _faults);
            destinationPin.Validate();

            foreach (var entry in admitted.Entries.OrderBy(x => x.Directory ? 0 : 1).ThenBy(x => x.Depth))
            {
                token.ThrowIfCancellationRequested();
                if (skipped.Contains(entry.Name)) continue;
                var target = CombineAdmittedTarget(dst, entry.Name);
                _faults?.OnFaultPoint(ZipFaultPoint.BeforeExtractEntry, target);
                token.ThrowIfCancellationRequested();

                if (entry.Directory)
                {
                    if (File.Exists(target))
                    {
                        if (pol == "skip") continue;
                        throw new AgentOpsException("collision");
                    }
                    try
                    {
                        using var directoryPin = PinnedDirectoryChain.OpenOrCreate(target, ref changed, token, _faults);
                        directoryPin.Validate();
                    }
                    catch (AgentOpsException ex) when (pol == "skip" && ex.Code == "collision")
                    {
                        continue;
                    }
                    continue;
                }

                var parent = Path.GetDirectoryName(target) ?? throw new AgentOpsException("archive_name");
                if (!EnsureDirectoryPath(parent, pol, ref changed, token)) continue;
                if (Directory.Exists(target))
                {
                    if (pol == "skip") continue;
                    throw new AgentOpsException("collision");
                }
                EnsureLeafNotReparse(target);
                if (File.Exists(target) && pol == "skip") continue;
                if (PublishExtractedFile(entry, target, pol, token))
                {
                    copied++;
                    changed++;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            if (changed > 0) throw new ZipOperationException("canceled", deleted, copied, changed, ex);
            throw;
        }
        catch (TemporaryCleanupFailure ex)
        {
            throw new ZipOperationException(FailureCode(ex.InnerException, "extract_incomplete"),
                deleted, copied, changed, ex.InnerException, true);
        }
        catch (ZipOperationException)
        {
            throw;
        }
        catch (AgentOpsException ex)
        {
            if (cleaning) throw new ZipOperationException("clean_failed", deleted, copied, changed, ex);
            if (changed > 0)
                throw new ZipOperationException("extract_incomplete", deleted, copied, changed, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            throw new ZipOperationException(cleaning ? "clean_failed" : "extract_incomplete", deleted, copied, changed, ex);
        }
        finally
        {
            destinationPin?.Dispose();
        }

        return new ZipResult(deleted, copied, changed, false, admitted.ArchiveBytes,
            admitted.ExpandedBytes, admitted.Entries.Count);
    }

    private List<SourceEntry> AdmitSourceTree(string root, ZipLimits limits, CancellationToken token)
    {
        var entries = new List<SourceEntry>();
        var names = new HashSet<string>(WindowsNameComparer);
        long expanded = 0;
        Walk(root, string.Empty);
        return entries;

        void Walk(string directory, string relative)
        {
            token.ThrowIfCancellationRequested();
            using var directoryPin = PinnedPathHandle.OpenExisting(directory, expectedDirectory: true);
            directoryPin.Validate();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                _faults?.OnFaultPoint(ZipFaultPoint.BeforeSourceEntry, path);
                token.ThrowIfCancellationRequested();
                using var pathPin = OpenSourceEntry(path, expectedDirectory: null);
                var attrs = pathPin.Attributes;
                var name = Path.GetFileName(path);
                var archiveName = string.IsNullOrEmpty(relative) ? name : relative + "/" + name;
                var isDirectory = (attrs & FileAttributes.Directory) != 0;
                ValidateArchiveName(isDirectory ? archiveName + "/" : archiveName, isDirectory, limits);
                if (!names.Add(archiveName)) throw new AgentOpsException("archive_collision");
                if (entries.Count + 1 > limits.MaxEntries)
                    throw new AgentOpsException("archive_limit");

                if (isDirectory)
                {
                    entries.Add(new SourceEntry(path, archiveName + "/", true, 0));
                    pathPin.Validate();
                    Walk(path, archiveName);
                }
                else
                {
                    var length = new FileInfo(path).Length;
                    pathPin.Validate();
                    if (length > limits.MaxEntryBytes) throw new AgentOpsException("archive_limit");
                    try { expanded = checked(expanded + length); }
                    catch (OverflowException ex) { throw new AgentOpsException("archive_limit", inner: ex); }
                    if (expanded > limits.MaxExpandedBytes) throw new AgentOpsException("archive_limit");
                    entries.Add(new SourceEntry(path, archiveName, false, length));
                }
            }
        }
    }

    private List<ExpectedEntry> WriteArchive(string temporary, IReadOnlyList<SourceEntry> sourcePlan, ZipLimits limits,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var expected = new List<ExpectedEntry>(sourcePlan.Count);
        using var output = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.SequentialScan | FileOptions.WriteThrough
        });
        using var bounded = new BoundedWriteStream(output, limits.MaxArchiveBytes, token);
        var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true);
        Exception? archiveFailure = null;
        try
        {
            foreach (var source in sourcePlan)
            {
                token.ThrowIfCancellationRequested();
                var sourceParent = Path.GetDirectoryName(source.Path) ?? throw new AgentOpsException("precondition");
                using var sourceParentPin = PinnedDirectoryChain.OpenExisting(sourceParent, token);
                using var sourcePin = OpenSourceEntry(source.Path, source.Directory);
                sourceParentPin.Validate();
                sourcePin.Validate();
                var entry = archive.CreateEntry(source.Name, CompressionLevel.Optimal);
                entry.ExternalAttributes = source.Directory
                    ? (int)FileAttributes.Directory
                    : (int)FileAttributes.Archive;
                if (source.Directory)
                {
                    expected.Add(new ExpectedEntry(source.Name, true, 0, null, 0));
                    continue;
                }

                using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.SequentialScan);
                sourcePin.Validate();
                if (input.Length != source.Length) throw new AgentOpsException("precondition");
                var destination = entry.Open();
                Exception? contentFailure = null;
                try
                {
                    var (count, hash, crc32) = CopyAndHash(input, destination, limits.MaxEntryBytes, token, source.Path);
                    if (count != source.Length) throw new AgentOpsException("precondition");
                    expected.Add(new ExpectedEntry(source.Name, false, count, hash, crc32));
                }
                catch (Exception ex) { contentFailure = ex; throw; }
                finally { DisposePreservingPrimary(destination, contentFailure); }
            }
        }
        catch (Exception ex) { archiveFailure = ex; throw; }
        finally { DisposePreservingPrimary(archive, archiveFailure); }
        token.ThrowIfCancellationRequested();
        output.Flush(flushToDisk: true);
        return expected;
    }

    private void VerifyCreatedArchive(string path, IReadOnlyList<ExpectedEntry> expected, ZipLimits limits, CancellationToken token)
    {
        using var admitted = Admit(path, limits, token);
        if (admitted.Entries.Count != expected.Count) throw new AgentOpsException("archive_invalid");
        for (var i = 0; i < expected.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var actual = admitted.Entries[i];
            var wanted = expected[i];
            if (!StringComparer.Ordinal.Equals(actual.Name, wanted.Name) ||
                actual.Directory != wanted.Directory || actual.Length != wanted.Length ||
                !StringComparer.Ordinal.Equals(actual.Sha256, wanted.Sha256) || actual.Crc32 != wanted.Crc32)
            {
                throw new AgentOpsException("archive_invalid");
            }
        }
    }

    private AdmittedArchive Admit(string src, ZipLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(src)) throw new AgentOpsException("not_found");

        FileStream? stream = null;
        ZipArchive? archive = null;
        var transferred = false;
        Exception? failure = null;
        try
        {
            var sourceParent = Path.GetDirectoryName(src) ?? throw new AgentOpsException("invalid");
            using var sourceParentPin = PinnedDirectoryChain.OpenExisting(sourceParent, token);
            using var sourcePin = PinnedPathHandle.OpenExisting(src, expectedDirectory: false);
            stream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.RandomAccess);
            sourceParentPin.Validate();
            sourcePin.Validate();
            var archiveBytes = stream.Length;
            if (archiveBytes > limits.MaxArchiveBytes) throw new AgentOpsException("archive_limit");
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            token.ThrowIfCancellationRequested();
            if (archive.Entries.Count > limits.MaxEntries) throw new AgentOpsException("archive_limit");

            // Pass one is metadata-only. Every structural and expansion bound is
            // settled before opening even one compressed entry stream.
            var entries = new List<AdmittedEntry>(archive.Entries.Count);
            var names = new Dictionary<string, bool>(WindowsNameComparer);
            long expanded = 0;
            long compressed = 0;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                ValidateArchiveName(entry.FullName, directory, limits);
                ValidateExternalAttributes(entry, directory);
                var canonicalName = entry.FullName.TrimEnd('/');
                if (!names.TryAdd(canonicalName, directory)) throw new AgentOpsException("archive_collision");

                long length;
                long compressedLength;
                uint crc32;
                try
                {
                    length = entry.Length;
                    compressedLength = entry.CompressedLength;
                    crc32 = entry.Crc32;
                }
                catch (InvalidDataException ex)
                {
                    throw new AgentOpsException("archive_invalid", inner: ex);
                }
                if (directory && (length != 0 || compressedLength != 0 || crc32 != 0))
                    throw new AgentOpsException("archive_invalid");
                if (length < 0 || compressedLength < 0 || length > limits.MaxEntryBytes)
                    throw new AgentOpsException("archive_limit");
                try
                {
                    expanded = checked(expanded + length);
                    compressed = checked(compressed + compressedLength);
                }
                catch (OverflowException ex)
                {
                    throw new AgentOpsException("archive_limit", inner: ex);
                }
                if (expanded > limits.MaxExpandedBytes) throw new AgentOpsException("archive_limit");
                var entryRatio = length == 0 ? 0 : length / (double)Math.Max(1, compressedLength);
                if (entryRatio > limits.MaxExpansionRatio) throw new AgentOpsException("archive_limit");

                entries.Add(new AdmittedEntry(entry, entry.FullName, directory, length, compressedLength, null, crc32));
            }

            ValidateStructuralCollisions(names, token);
            var ratio = expanded == 0 ? 0 : expanded / (double)Math.Max(1, compressed);
            if (ratio > limits.MaxExpansionRatio) throw new AgentOpsException("archive_limit");

            // Pass two streams exact content, independently checking both the
            // declared uncompressed size and the stored central-directory CRC.
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var admitted = entries[i];
                if (admitted.Directory) continue;
                _faults?.OnFaultPoint(ZipFaultPoint.BeforeAdmissionContent, admitted.Name);
                token.ThrowIfCancellationRequested();
                using var content = admitted.Entry.Open();
                var tested = ReadAndHash(content, admitted.Length, token, admitted.Name, "archive_invalid");
                if (tested.Count != admitted.Length || tested.Crc32 != admitted.Crc32)
                    throw new AgentOpsException("archive_invalid");
                entries[i] = admitted with { Sha256 = tested.Hash };
            }

            var result = new AdmittedArchive(stream, archive, archiveBytes, expanded, compressed, entries);
            transferred = true;
            return result;
        }
        catch (AgentOpsException ex)
        {
            failure = ex;
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            failure = new AgentOpsException(ex is UnauthorizedAccessException ? "access" : "archive_invalid", inner: ex);
            throw failure;
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            // Includes cancellation: ownership transfers only on actual success.
            if (!transferred)
            {
                try { DisposePreservingPrimary(archive, failure); }
                finally { DisposePreservingPrimary(stream, failure); }
            }
        }
    }

    private static void ValidateArchiveName(string name, bool directory, ZipLimits limits)
    {
        if (string.IsNullOrEmpty(name) || name.Length > limits.MaxEntryNameChars ||
            name[0] is '/' or '\\' || name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains('\0') || name.Contains('\uFFFD') ||
            Path.IsPathRooted(name))
        {
            throw new AgentOpsException("archive_name");
        }
        if (directory != name.EndsWith("/", StringComparison.Ordinal))
            throw new AgentOpsException("archive_name");

        var candidate = directory ? name[..^1] : name;
        if (candidate.Length == 0 || candidate.EndsWith("/", StringComparison.Ordinal))
            throw new AgentOpsException("archive_name");
        var parts = candidate.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.Length > 255 ||
                part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(InvalidWindowsNameChars) >= 0 ||
                part.Any(char.IsControl) || IsReservedWindowsName(part))
            {
                throw new AgentOpsException(part is ".." ? "archive_traversal" : "archive_name");
            }
        }
    }

    private static bool IsReservedWindowsName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 && stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && IsReservedPortDigit(stem[3])) ||
               (stem.Length == 4 && stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && IsReservedPortDigit(stem[3]));
    }

    private static bool IsReservedPortDigit(char value) =>
        value is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';

    private static void ValidateExternalAttributes(ZipArchiveEntry entry, bool directory)
    {
        var attributes = unchecked((uint)entry.ExternalAttributes);
        var unixType = (attributes >> 16) & 0xF000;
        var dosAttributes = attributes & 0xFFFF;
        if ((dosAttributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Device)) != 0 ||
            unixType is not (0 or 0x8000 or 0x4000))
        {
            throw new AgentOpsException("archive_special");
        }
        if ((unixType == 0x4000) != directory && unixType != 0)
            throw new AgentOpsException("archive_special");
        if ((dosAttributes & (uint)FileAttributes.Directory) != 0 && !directory)
            throw new AgentOpsException("archive_special");
    }

    private static void ValidateStructuralCollisions(IReadOnlyDictionary<string, bool> names, CancellationToken token)
    {
        foreach (var item in names)
        {
            token.ThrowIfCancellationRequested();
            var name = item.Key;
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
            {
                if (names.TryGetValue(name[..slash], out var directory) && !directory)
                    throw new AgentOpsException("archive_collision");
            }
        }
    }

    private static HashSet<string> PreflightDestination(string root, IReadOnlyList<AdmittedEntry> entries, string pol,
        CancellationToken token)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var target = CombineAdmittedTarget(root, entry.Name);
            var relative = entry.Name.TrimEnd('/');
            var parts = relative.Split('/');
            var current = root;
            var blocked = false;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                token.ThrowIfCancellationRequested();
                current = Path.Combine(current, parts[i]);
                EnsureNoReparseComponents(current);
                if (File.Exists(current))
                {
                    if (pol == "skip") { blocked = true; break; }
                    throw new AgentOpsException("collision");
                }
            }
            if (blocked) { skipped.Add(entry.Name); continue; }
            EnsureNoReparseComponents(target);
            if (entry.Directory && File.Exists(target) || !entry.Directory && Directory.Exists(target))
            {
                if (pol == "skip") skipped.Add(entry.Name);
                else throw new AgentOpsException("collision");
            }
            else if (!entry.Directory && File.Exists(target) && pol == "skip")
            {
                skipped.Add(entry.Name);
            }
        }
        return skipped;
    }

    private bool PublishExtractedFile(AdmittedEntry entry, string target, string pol, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(target) ?? throw new AgentOpsException("archive_name");
        using var parentPin = PinnedDirectoryChain.OpenExisting(parent, token);
        parentPin.Validate();
        var temporary = UniqueSiblingPath(target);
        var moved = false;
        Exception? failure = null;
        try
        {
            using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan | FileOptions.WriteThrough
            }))
            using (var content = entry.Entry.Open())
            {
                var (count, hash, crc32) = CopyAndHash(content, output, entry.Length, token, target, "archive_invalid");
                if (count != entry.Length || !StringComparer.Ordinal.Equals(hash, entry.Sha256) ||
                    crc32 != entry.Crc32)
                    throw new AgentOpsException("archive_invalid");
                output.Flush(flushToDisk: true);
            }

            _faults?.OnFaultPoint(ZipFaultPoint.BeforeExtractFilePublish, target);
            parentPin.Validate();
            EnsureLeafNotReparse(target);
            if (Directory.Exists(target)) throw new AgentOpsException("collision");
            var overwrite = pol == "replace";
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, target, overwrite);
                moved = true;
                NotifyPublished(target);
            }
            catch (IOException) when (pol == "skip" && File.Exists(target))
            {
                // A target admitted as absent may appear before publication. Skip
                // is the only policy where that race has a non-error outcome.
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            if (!moved && !TryDeleteTemporary(temporary)) throw new TemporaryCleanupFailure(failure);
        }
        return moved;
    }

    private bool EnsureDirectoryPath(string path, string pol, ref long changed, CancellationToken token)
    {
        try
        {
            using var pin = PinnedDirectoryChain.OpenOrCreate(path, ref changed, token, _faults);
            pin.Validate();
            return true;
        }
        catch (AgentOpsException ex) when (pol == "skip" && ex.Code == "collision")
        {
            return false;
        }
    }

    private void DeleteEntryNoFollow(string path, ref long deleted, ref long changed, CancellationToken token)
    {
        _faults?.OnFaultPoint(ZipFaultPoint.BeforeCleanEntry, path);
        token.ThrowIfCancellationRequested();
        PinnedPathHandle? pin = PinnedPathHandle.OpenExisting(path, expectedDirectory: null, allowReparse: true);
        try
        {
            var attrs = pin.Attributes;
            var directory = (attrs & FileAttributes.Directory) != 0;
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                token.ThrowIfCancellationRequested();
                pin.ClearReadOnly();
                changed++;
                _faults?.OnFaultPoint(ZipFaultPoint.AfterDestinationChange, path);
            }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                pin.Validate();
                token.ThrowIfCancellationRequested();
                pin.Dispose();
                pin = null;
                if (directory) Directory.Delete(path); else File.Delete(path);
                deleted++;
                changed++;
                _faults?.OnFaultPoint(ZipFaultPoint.AfterDestinationChange, path);
                return;
            }
            if (!directory)
            {
                pin.Validate();
                token.ThrowIfCancellationRequested();
                pin.Dispose();
                pin = null;
                File.Delete(path);
                deleted++;
                changed++;
                _faults?.OnFaultPoint(ZipFaultPoint.AfterDestinationChange, path);
                return;
            }

            _faults?.OnFaultPoint(ZipFaultPoint.BeforeCleanDirectoryEnumeration, path);
            token.ThrowIfCancellationRequested();
            pin.Validate();
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                DeleteEntryNoFollow(child, ref deleted, ref changed, token);
            pin.Validate();
            token.ThrowIfCancellationRequested();
            pin.Dispose();
            pin = null;
            Directory.Delete(path);
            deleted++;
            changed++;
            _faults?.OnFaultPoint(ZipFaultPoint.AfterDestinationChange, path);
        }
        finally
        {
            pin?.Dispose();
        }
    }

    private static string CombineAdmittedTarget(string root, string archiveName)
    {
        var relative = archiveName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, target)) throw new AgentOpsException("archive_traversal");
        return target;
    }

    private static void EnsureNoReparseComponents(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
        var relative = path[root.Length..];
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new AgentOpsException("reparse_boundary");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static void RejectCreateRelationship(string src, string dst)
    {
        if (WindowsNameComparer.Equals(src, Path.TrimEndingDirectorySeparator(dst)) || IsStrictDescendant(src, dst))
            throw new AgentOpsException("precondition");
    }

    private static void RejectExtractRelationship(string src, string dst)
    {
        if (WindowsNameComparer.Equals(Path.TrimEndingDirectorySeparator(src), dst) || IsStrictDescendant(dst, src))
            throw new AgentOpsException("precondition");
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool Contains(string root, string candidate)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        candidate = Path.GetFullPath(candidate);
        return WindowsNameComparer.Equals(root, Path.TrimEndingDirectorySeparator(candidate)) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string UniqueSiblingPath(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new AgentOpsException("invalid");
        var name = Path.GetFileName(destination);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.pao.tmp");
    }

    private static string RequiredFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new AgentOpsException("invalid");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }
        var root = Path.GetPathRoot(full) ?? throw new AgentOpsException("invalid");
        if (full.AsSpan(root.Length).Contains(':')) throw new AgentOpsException("precondition");
        return full;
    }

    private (long Count, string Hash, uint Crc32) CopyAndHash(
        Stream input,
        Stream output,
        long max,
        CancellationToken token,
        string faultPath,
        string limitCode = "archive_limit")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc32 = new Crc32Accumulator();
        var buffer = new byte[BufferSize];
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            try { count = checked(count + read); }
            catch (OverflowException ex) { throw new AgentOpsException(limitCode, inner: ex); }
            if (count > max) throw new AgentOpsException(limitCode);
            hash.AppendData(buffer, 0, read);
            crc32.Append(buffer.AsSpan(0, read));
            output.Write(buffer, 0, read);
            _faults?.OnFaultPoint(ZipFaultPoint.AfterContentChunk, faultPath);
            token.ThrowIfCancellationRequested();
        }
        return (count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), crc32.Value);
    }

    private (long Count, string Hash, uint Crc32) ReadAndHash(
        Stream input,
        long max,
        CancellationToken token,
        string faultPath,
        string limitCode = "archive_limit")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc32 = new Crc32Accumulator();
        var buffer = new byte[BufferSize];
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            try { count = checked(count + read); }
            catch (OverflowException ex) { throw new AgentOpsException(limitCode, inner: ex); }
            if (count > max) throw new AgentOpsException(limitCode);
            hash.AppendData(buffer, 0, read);
            crc32.Append(buffer.AsSpan(0, read));
            _faults?.OnFaultPoint(ZipFaultPoint.AfterContentChunk, faultPath);
            token.ThrowIfCancellationRequested();
        }
        return (count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), crc32.Value);
    }

    private static void EnsureLeafNotReparse(string path)
    {
        try
        {
            using var pin = PinnedPathHandle.OpenExisting(path, expectedDirectory: null, allowReparse: true);
            if ((pin.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AgentOpsException("reparse_boundary");
            pin.Validate();
        }
        catch (AgentOpsException ex) when (ex.Code == "not_found")
        {
            // Missing is an admitted state at publication; the caller chooses
            // create-new or replace behavior explicitly in File.Move.
        }
    }

    private static PinnedPathHandle OpenSourceEntry(string path, bool? expectedDirectory)
    {
        try { return PinnedPathHandle.OpenExisting(path, expectedDirectory); }
        catch (AgentOpsException ex) when (ex.Code == "reparse_boundary")
        {
            throw new AgentOpsException("reparse_source", inner: ex);
        }
    }

    private sealed class PinnedDirectoryChain : IDisposable
    {
        private readonly List<PinnedPathHandle> _pins;

        private PinnedDirectoryChain(List<PinnedPathHandle> pins) => _pins = pins;

        public static PinnedDirectoryChain OpenExisting(string path, CancellationToken token = default)
        {
            long ignored = 0;
            return Open(path, createMissing: false, ref ignored, token, null);
        }

        public static PinnedDirectoryChain OpenOrCreate(string path, ref long changed, CancellationToken token,
            IZipFaultInjector? faults) => Open(path, createMissing: true, ref changed, token, faults);

        private static PinnedDirectoryChain Open(string path, bool createMissing, ref long changed,
            CancellationToken token, IZipFaultInjector? faults)
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
            var paths = new List<string> { root };
            var current = root;
            var relative = Path.GetRelativePath(root, path);
            if (relative != ".")
            {
                foreach (var component in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    paths.Add(current);
                }
            }

            var pins = new List<PinnedPathHandle>(paths.Count);
            try
            {
                foreach (var directory in paths)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var pin in pins) pin.Validate();
                    try
                    {
                        pins.Add(PinnedPathHandle.OpenExisting(directory, expectedDirectory: true));
                    }
                    catch (AgentOpsException ex) when (createMissing && ex.Code == "not_found")
                    {
                        token.ThrowIfCancellationRequested();
                        if (NativeMethods.CreateDirectoryExact(directory))
                        {
                            changed++;
                            faults?.OnFaultPoint(ZipFaultPoint.AfterDestinationChange, directory);
                        }
                        token.ThrowIfCancellationRequested();
                        pins.Add(PinnedPathHandle.OpenExisting(directory, expectedDirectory: true));
                    }
                }
                return new PinnedDirectoryChain(pins);
            }
            catch
            {
                for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose();
                throw;
            }
        }

        public void Validate()
        {
            foreach (var pin in _pins) pin.Validate();
        }

        public void Dispose()
        {
            for (var i = _pins.Count - 1; i >= 0; i--) _pins[i].Dispose();
        }
    }

    private sealed class PinnedPathHandle : IDisposable
    {
        private readonly string _path;
        private readonly bool? _expectedDirectory;
        private readonly bool _allowReparse;
        private readonly FileIdentity _identity;
        private SafeFileHandle _handle;

        private PinnedPathHandle(
            string path,
            bool? expectedDirectory,
            bool allowReparse,
            SafeFileHandle handle,
            NativeMethods.ByHandleFileInformation information)
        {
            _path = path;
            _expectedDirectory = expectedDirectory;
            _allowReparse = allowReparse;
            _handle = handle;
            _identity = NativeMethods.Identity(information);
            Attributes = (FileAttributes)information.FileAttributes;
        }

        public FileAttributes Attributes { get; private set; }

        public static PinnedPathHandle OpenExisting(
            string path,
            bool? expectedDirectory,
            bool allowReparse = false)
        {
            path = Path.GetFullPath(path);
            var handle = NativeMethods.OpenPath(path, NativeMethods.FileReadAttributes);
            try
            {
                var information = NativeMethods.Information(handle);
                ValidateType(information, expectedDirectory, allowReparse);
                return new PinnedPathHandle(path, expectedDirectory, allowReparse, handle, information);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Validate()
        {
            ThrowIfDisposed();
            using var current = NativeMethods.OpenPath(_path, NativeMethods.FileReadAttributes);
            var information = NativeMethods.Information(current);
            ValidateType(information, _expectedDirectory, _allowReparse);
            if (NativeMethods.Identity(information) != _identity)
                throw new AgentOpsException("precondition");
        }

        public void ClearReadOnly()
        {
            ThrowIfDisposed();
            using var writable = NativeMethods.OpenPath(
                _path,
                NativeMethods.FileReadAttributes | NativeMethods.FileWriteAttributes);
            var information = NativeMethods.Information(writable);
            ValidateType(information, _expectedDirectory, _allowReparse);
            if (NativeMethods.Identity(information) != _identity)
                throw new AgentOpsException("precondition");
            var basic = NativeMethods.BasicInformation(writable);
            basic.FileAttributes &= ~(uint)FileAttributes.ReadOnly;
            NativeMethods.SetBasicInformation(writable, basic);
            Attributes &= ~FileAttributes.ReadOnly;
        }

        private static void ValidateType(
            NativeMethods.ByHandleFileInformation information,
            bool? expectedDirectory,
            bool allowReparse)
        {
            var attributes = (FileAttributes)information.FileAttributes;
            if (!allowReparse && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new AgentOpsException("reparse_boundary");
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (expectedDirectory is bool expected && directory != expected)
                throw new AgentOpsException("collision");
        }

        private void ThrowIfDisposed()
        {
            if (_handle.IsClosed) throw new ObjectDisposedException(nameof(PinnedPathHandle));
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes, CancellationToken token) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set
            {
                CheckPosition(value);
                inner.Position = value;
            }
        }

        public override void Flush() { token.ThrowIfCancellationRequested(); inner.Flush(); }
        public override Task FlushAsync(CancellationToken cancellationToken)
        { token.ThrowIfCancellationRequested(); return inner.FlushAsync(cancellationToken); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => inner.Position,
                SeekOrigin.End => inner.Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            long target;
            try { target = checked(basis + offset); }
            catch (OverflowException ex) { throw new AgentOpsException("archive_limit", inner: ex); }
            CheckPosition(target);
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            CheckPosition(value);
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckWrite(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckWrite(buffer.Length);
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            CheckWrite(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            CheckWrite(1);
            inner.WriteByte(value);
        }

        private void CheckWrite(int count)
        {
            long end;
            try { end = checked(inner.Position + count); }
            catch (OverflowException ex) { throw new AgentOpsException("archive_limit", inner: ex); }
            CheckPosition(Math.Max(inner.Length, end));
        }

        private void CheckPosition(long value)
        {
            token.ThrowIfCancellationRequested();
            if (value < 0 || value > maxBytes) throw new AgentOpsException("archive_limit");
        }
    }

    private sealed class Crc32Accumulator
    {
        private static readonly uint[] Table = CreateTable();
        private uint _value = uint.MaxValue;

        public uint Value => _value ^ uint.MaxValue;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
                _value = Table[(byte)(_value ^ value)] ^ (_value >> 8);
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);

    private static class NativeMethods
    {
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileWriteAttributes = 0x00000100;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileBasicInfo information,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileBasicInfo information,
            uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryNative(string path, nint securityAttributes);

        public static SafeFileHandle OpenPath(string path, uint desiredAccess)
        {
            var handle = CreateFileW(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw MapFailure(error);
        }

        public static ByHandleFileInformation Information(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw MapFailure(Marshal.GetLastWin32Error());
            return information;
        }

        public static bool CreateDirectoryExact(string path)
        {
            if (CreateDirectoryNative(path, 0)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) return false;
            throw MapFailure(error);
        }

        public static FileBasicInfo BasicInformation(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, 0, out var information, (uint)Marshal.SizeOf<FileBasicInfo>()))
                throw MapFailure(Marshal.GetLastWin32Error());
            return information;
        }

        public static void SetBasicInformation(SafeFileHandle handle, FileBasicInfo information)
        {
            if (!SetFileInformationByHandle(handle, 0, ref information, (uint)Marshal.SizeOf<FileBasicInfo>()))
                throw MapFailure(Marshal.GetLastWin32Error());
        }

        public static FileIdentity Identity(ByHandleFileInformation information) => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

        private static AgentOpsException MapFailure(int error) => error switch
        {
            2 or 3 => new AgentOpsException("not_found", inner: new Win32Exception(error)),
            5 => new AgentOpsException("access", inner: new Win32Exception(error)),
            32 or 33 => new AgentOpsException("precondition", inner: new Win32Exception(error)),
            _ => new AgentOpsException("precondition", inner: new Win32Exception(error))
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileBasicInfo
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }
    }

    private static bool TryDeleteTemporary(string path)
    {
        PinnedPathHandle? pin = null;
        try
        {
            pin = PinnedPathHandle.OpenExisting(path, expectedDirectory: null, allowReparse: true);
            var attributes = pin.Attributes;
            var directory = (attributes & FileAttributes.Directory) != 0;
            var reparse = (attributes & FileAttributes.ReparsePoint) != 0;
            if (directory && !reparse) return false;
            if ((attributes & FileAttributes.ReadOnly) != 0) pin.ClearReadOnly();
            pin.Validate();
            pin.Dispose();
            pin = null;
            if (directory) Directory.Delete(path); else File.Delete(path);
            return true;
        }
        catch (AgentOpsException ex) when (ex.Code == "not_found") { return true; }
        catch (Exception ex) when (ex is AgentOpsException or IOException or UnauthorizedAccessException)
        {
            // No retry or false untouched-destination claim. Preserve the
            // primary error and expose unresolved sibling-temp cleanup.
            return false;
        }
        finally
        {
            pin?.Dispose();
        }
    }

    private static string FailureCode(Exception? failure, string fallback) => failure switch
    {
        OperationCanceledException => "canceled",
        AgentOpsException known => known.Code,
        ZipOperationException known => known.Code,
        _ => fallback
    };

    private static void DisposePreservingPrimary(IDisposable? resource, Exception? primary)
    {
        try { resource?.Dispose(); }
        catch when (primary is not null)
        {
            // ZIP finalization may write again during unwind. Keep the first
            // failure, still close outer resources, and independently report
            // any unresolved temporary through TemporaryCleanupFailed.
        }
    }

    private void NotifyPublished(string path)
    {
        // Test observation only: a completed atomic publication is authoritative.
        // Neither a newly canceled token nor an observer fault can undo it.
        try { _faults?.OnFaultPoint(ZipFaultPoint.AfterPublish, path); }
        catch { }
    }

    private sealed class TemporaryCleanupFailure(Exception? primary) : Exception("temporary_cleanup_failed", primary);

    private sealed record SourceEntry(string Path, string Name, bool Directory, long Length);
    private sealed record ExpectedEntry(string Name, bool Directory, long Length, string? Sha256, uint Crc32);

    private sealed record AdmittedEntry(
        ZipArchiveEntry Entry,
        string Name,
        bool Directory,
        long Length,
        long CompressedLength,
        string? Sha256,
        uint Crc32)
    {
        public int Depth => Name.Count(x => x == '/');
    }

    private sealed class AdmittedArchive : IDisposable
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;

        public AdmittedArchive(FileStream stream, ZipArchive archive, long archiveBytes, long expandedBytes,
            long compressedBytes, IReadOnlyList<AdmittedEntry> entries)
        {
            _stream = stream;
            _archive = archive;
            ArchiveBytes = archiveBytes;
            ExpandedBytes = expandedBytes;
            CompressedBytes = compressedBytes;
            Entries = entries;
        }

        public long ArchiveBytes { get; }
        public long ExpandedBytes { get; }
        public long CompressedBytes { get; }
        public IReadOnlyList<AdmittedEntry> Entries { get; }

        public ZipInspection ToInspection(CancellationToken token)
        {
            var result = new ZipEntryInfo[Entries.Count];
            for (var i = 0; i < result.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = Entries[i];
                result[i] = new(entry.Name, entry.Directory, entry.Length, entry.CompressedLength, entry.Sha256);
            }
            return new(ArchiveBytes, ExpandedBytes, CompressedBytes, result);
        }

        public void Dispose()
        {
            try { _archive.Dispose(); }
            finally { _stream.Dispose(); }
        }
    }
}
