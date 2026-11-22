using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Pegasus.AgentOps.Text.Tests")]

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Bounded, shell-free text operations. All mutations publish a completed byte
/// sequence from a same-directory temporary file so a failed publish leaves the
/// prior target intact.
/// </summary>
public static class ExtendedOperations
{
    public const long DefaultMaxTextBytes = 16 * 1024 * 1024;
    public const int DefaultMaxLineReadBytes = 256 * 1024;

    internal static Action<TextPublicationHookPoint, string>? PublicationHookForTests { get; set; }

    public static IReadOnlyDictionary<string, object?> Read(
        string path,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        path = CanonicalDirectPath(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: true);
        var document = TextEncoding.Decode(snapshot.Bytes, requestedEncoding);
        return new Dictionary<string, object?>
        {
            ["src"] = path,
            ["text"] = document.Text,
            ["enc"] = document.Spec.Name,
            ["bom"] = document.Bom,
            ["newline"] = document.DominantNewline,
            ["len"] = snapshot.Bytes.LongLength
        };
    }

    /// <summary>
    /// Returns bounded, numbered context from a recognized text file together
    /// with the complete preimage hash. The response never contains text outside
    /// the selected range and never splits a Unicode scalar at the byte bound.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ReadLines(
        string path,
        int firstLine,
        int lineCount,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes,
        int maxResultBytes = DefaultMaxLineReadBytes)
    {
        if (firstLine < 1 || lineCount is < 1 or > 512 || maxResultBytes is < 1 or > DefaultMaxLineReadBytes)
            throw new AgentOpsException("invalid");

        path = CanonicalDirectPath(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: true);
        var document = TextEncoding.Decode(snapshot.Bytes, requestedEncoding);
        var lines = DocumentLine.Split(document.Text);
        var firstIndex = firstLine - 1;
        if (firstIndex >= lines.Count) throw new AgentOpsException("precondition");

        var entries = new List<object>();
        var remainingBytes = maxResultBytes;
        var rangeEnd = Math.Min(lines.Count, checked(firstIndex + lineCount));
        var textTruncated = false;
        for (var index = firstIndex; index < rangeEnd; index++)
        {
            var original = lines[index].Content;
            var bounded = BoundUtf8(original, remainingBytes, out var truncated);
            entries.Add(new { line = index + 1, text = bounded, truncated });
            remainingBytes -= Encoding.UTF8.GetByteCount(bounded);
            textTruncated |= truncated;
            if (remainingBytes == 0)
            {
                textTruncated |= index + 1 < rangeEnd;
                break;
            }
        }

        var returned = entries.Count;
        var next = firstIndex + returned < lines.Count ? firstIndex + returned + 1 : (int?)null;
        return new Dictionary<string, object?>
        {
            ["src"] = path,
            ["line"] = firstLine,
            ["count"] = returned,
            ["requested"] = lineCount,
            ["total"] = lines.Count,
            ["next"] = next,
            ["entries"] = entries,
            ["truncated"] = textTruncated || firstIndex + returned < rangeEnd,
            ["has_more"] = next is not null,
            ["enc"] = document.Spec.Name,
            ["bom"] = document.Bom,
            ["newline"] = document.DominantNewline,
            ["len"] = snapshot.Bytes.LongLength,
            ["sha"] = Sha256(snapshot.Bytes)
        };
    }

    /// <summary>Inspects text encoding and shape without returning file content.</summary>
    public static IReadOnlyDictionary<string, object?> InspectEncoding(
        string path,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        path = CanonicalDirectPath(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: true);
        var document = TextEncoding.Decode(snapshot.Bytes, requestedEncoding);
        return new Dictionary<string, object?>
        {
            ["src"] = path,
            ["enc"] = document.Spec.Name,
            ["bom"] = document.Bom,
            ["newline"] = document.DominantNewline,
            ["lines"] = DocumentLine.Split(document.Text).Count,
            ["len"] = snapshot.Bytes.LongLength,
            ["sha"] = Sha256(snapshot.Bytes)
        };
    }

    /// <summary>
    /// Converts a recognized source to an explicit target encoding. A distinct
    /// destination must be absent; in-place conversion is preimage-checked and
    /// atomic. Source decoding is never guessed beyond the recognized formats.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ConvertEncoding(
        string source,
        string destination,
        string targetEncoding,
        string? expectedSha = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        source = CanonicalDirectPath(source);
        destination = CanonicalDirectPath(destination);
        var sourceSnapshot = CaptureSnapshot(source, maxBytes, requireExisting: true);
        var beforeSha = Sha256(sourceSnapshot.Bytes);
        ValidateExpectedHash(expectedSha, beforeSha);
        var document = TextEncoding.Decode(sourceSnapshot.Bytes, null);
        var targetSpec = TextEncoding.ParseForWrite(targetEncoding);
        var converted = TextEncoding.Encode(document.Text, targetSpec, maxBytes);
        var sameTarget = StringComparer.OrdinalIgnoreCase.Equals(source, destination);

        if (sameTarget)
        {
            if (!sourceSnapshot.Bytes.AsSpan().SequenceEqual(converted))
                AtomicWrite(destination, converted, sourceSnapshot, maxBytes);
        }
        else
        {
            RejectDirectoryTarget(destination);
            var destinationSnapshot = CaptureSnapshot(destination, maxBytes, requireExisting: false);
            if (destinationSnapshot.Exists) throw new AgentOpsException("collision");
            AtomicWrite(destination, converted, destinationSnapshot, maxBytes);
        }

        return new Dictionary<string, object?>
        {
            ["src"] = source,
            ["dst"] = destination,
            ["enc_before"] = document.Spec.Name,
            ["bom_before"] = document.Bom,
            ["enc"] = targetSpec.Name,
            ["bom"] = targetSpec.EmitBom,
            ["newline"] = document.DominantNewline,
            ["sha_before"] = beforeSha,
            ["sha"] = Sha256(converted),
            ["len"] = converted.LongLength,
            ["changed"] = !sourceSnapshot.Bytes.AsSpan().SequenceEqual(converted) || !sameTarget
        };
    }

    public static IReadOnlyDictionary<string, object?> Write(
        string path,
        string text,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes) =>
        Write(path, text, requestedEncoding, expectedSha: null, maxBytes: maxBytes);

    // Retain the original four-argument library signature for existing callers.
    // The closed PAO operation uses this overload to bind an optional preimage.
    public static IReadOnlyDictionary<string, object?> Write(
        string path,
        string text,
        string? requestedEncoding,
        string? expectedSha,
        long maxBytes = DefaultMaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateLimit(maxBytes);
        path = CanonicalDirectPath(path);
        RejectDirectoryTarget(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: false);
        string? beforeSha = null;
        if (expectedSha is not null)
        {
            // A supplied hash is an existing-file preimage, never permission to
            // create a missing target. Omission retains legacy create/overwrite.
            if (!snapshot.Exists)
                throw new AgentOpsException("not_found", failure: FailureEvidence.TextGuard("preimage_missing", "sha"));
            beforeSha = Sha256(snapshot.Bytes);
            ValidateExpectedHash(expectedSha, beforeSha);
        }

        TextEncodingSpec targetEncoding;
        var normalized = text;
        if (requestedEncoding is null && snapshot.Exists)
        {
            var existing = TextEncoding.Decode(snapshot.Bytes, null);
            targetEncoding = existing.Spec;
            normalized = NormalizeNewlines(text, existing.DominantNewline);
        }
        else
        {
            targetEncoding = requestedEncoding is null
                ? TextEncoding.Utf8NoBom
                : TextEncoding.ParseForWrite(requestedEncoding);
        }

        var bytes = TextEncoding.Encode(normalized, targetEncoding, maxBytes);
        if (!snapshot.Exists || !snapshot.Bytes.AsSpan().SequenceEqual(bytes)) AtomicWrite(path, bytes, snapshot, maxBytes);
        var result = WriteResult(path, targetEncoding, bytes);
        if (beforeSha is not null) result["sha_before"] = beforeSha;
        return result;
    }

    public static IReadOnlyDictionary<string, object?> ReplaceLiteral(
        string path,
        string oldValue,
        string newValue,
        int expectedOccurrences,
        string? expectedSha = null,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        if (oldValue.Length == 0 || expectedOccurrences < 0) throw new AgentOpsException("invalid");

        path = CanonicalDirectPath(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: true);
        var originalBytes = snapshot.Bytes;
        var beforeSha = Sha256(originalBytes);
        ValidateExpectedHash(expectedSha, beforeSha);
        var document = TextEncoding.Decode(originalBytes, requestedEncoding);

        var actualOccurrences = CountOccurrences(document.Text, oldValue);
        if (actualOccurrences != expectedOccurrences)
        {
            var reason = actualOccurrences == 0 ? "literal_not_found"
                : expectedOccurrences == 1 && actualOccurrences > 1 ? "literal_not_unique"
                : "literal_count_mismatch";
            throw new AgentOpsException("precondition", failure: FailureEvidence.TextGuard(reason, reason == "literal_not_found" ? "old" : "count"));
        }

        var replacement = NormalizeNewlines(newValue, document.DominantNewline);
        var updated = document.Text.Replace(oldValue, replacement, StringComparison.Ordinal);
        var targetEncoding = requestedEncoding is null
            ? document.Spec
            : TextEncoding.ParseForWrite(requestedEncoding);
        var updatedBytes = TextEncoding.Encode(updated, targetEncoding, maxBytes);
        if (!originalBytes.AsSpan().SequenceEqual(updatedBytes)) AtomicWrite(path, updatedBytes, snapshot, maxBytes);

        return new Dictionary<string, object?>
        {
            ["dst"] = path,
            ["count"] = actualOccurrences,
            ["sha_before"] = beforeSha,
            ["sha"] = Sha256(updatedBytes),
            ["enc"] = targetEncoding.Name,
            ["bom"] = targetEncoding.EmitBom,
            ["len"] = updatedBytes.LongLength
        };
    }

    /// <summary>
    /// Replaces a one-based inclusive line range after decoding and validating
    /// the complete file. Unselected bytes are represented by their exact text
    /// and original line endings; publication remains preimage-checked and atomic.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ReplaceLines(
        string path,
        int firstLine,
        int lineCount,
        string replacement,
        string? expectedSha = null,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (firstLine < 1 || lineCount < 1) throw new AgentOpsException("invalid");

        path = CanonicalDirectPath(path);
        var snapshot = CaptureSnapshot(path, maxBytes, requireExisting: true);
        var originalBytes = snapshot.Bytes;
        var beforeSha = Sha256(originalBytes);
        ValidateExpectedHash(expectedSha, beforeSha);
        var document = TextEncoding.Decode(originalBytes, requestedEncoding);
        var lines = DocumentLine.Split(document.Text);
        var firstIndex = firstLine - 1;
        if (firstIndex >= lines.Count || lineCount > lines.Count - firstIndex)
            throw new AgentOpsException("precondition");

        var fallbackNewline = document.DominantNewline ?? Environment.NewLine;
        var normalized = NormalizeNewlines(replacement, document.DominantNewline);
        var selectedEndsWithNewline = lines[firstIndex + lineCount - 1].Ending.Length > 0;
        var hasSuffix = firstIndex + lineCount < lines.Count;
        if (normalized.Length > 0 && !EndsWithNewline(normalized) && (hasSuffix || selectedEndsWithNewline))
            normalized += fallbackNewline;

        var builder = new StringBuilder(document.Text.Length - lines
            .Skip(firstIndex).Take(lineCount).Sum(line => line.Content.Length + line.Ending.Length) + normalized.Length);
        for (var i = 0; i < firstIndex; i++) builder.Append(lines[i].Content).Append(lines[i].Ending);
        builder.Append(normalized);
        for (var i = firstIndex + lineCount; i < lines.Count; i++) builder.Append(lines[i].Content).Append(lines[i].Ending);

        var targetEncoding = requestedEncoding is null ? document.Spec : TextEncoding.ParseForWrite(requestedEncoding);
        var updatedBytes = TextEncoding.Encode(builder.ToString(), targetEncoding, maxBytes);
        if (!originalBytes.AsSpan().SequenceEqual(updatedBytes)) AtomicWrite(path, updatedBytes, snapshot, maxBytes);
        return new Dictionary<string, object?>
        {
            ["dst"] = path,
            ["line"] = firstLine,
            ["count"] = lineCount,
            ["lines_before"] = lines.Count,
            ["lines_after"] = DocumentLine.Split(builder.ToString()).Count,
            ["sha_before"] = beforeSha,
            ["sha"] = Sha256(updatedBytes),
            ["enc"] = targetEncoding.Name,
            ["bom"] = targetEncoding.EmitBom,
            ["len"] = updatedBytes.LongLength
        };
    }

    /// <summary>
    /// Applies one unified-diff file section to one existing workspace-relative
    /// file. Multi-file, create, delete, rename, binary, fuzzy, and no-newline
    /// marker patches are deliberately unsupported in v1. A single-file patch
    /// allows every preimage to be checked before one atomic publication.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ApplyUnifiedPatch(
        string workspaceRoot,
        string unifiedPatch,
        string? expectedSha = null,
        string? requestedEncoding = null,
        long maxBytes = DefaultMaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(unifiedPatch);
        ValidateLimit(maxBytes);
        if (Encoding.UTF8.GetByteCount(unifiedPatch) > maxBytes) throw new AgentOpsException("invalid");

        workspaceRoot = PathNormalizer.NormalizeDirectory(workspaceRoot);
        if (!Directory.Exists(workspaceRoot)) throw new AgentOpsException("not_found");
        PathNormalizer.RejectReparseComponents(workspaceRoot, workspaceRoot);

        var patch = UnifiedPatch.ParseSingleFile(unifiedPatch);
        var target = ResolvePatchTarget(workspaceRoot, patch.RelativePath);
        var snapshot = CaptureSnapshot(target, maxBytes, requireExisting: true);
        var originalBytes = snapshot.Bytes;
        var beforeSha = Sha256(originalBytes);
        ValidateExpectedHash(expectedSha, beforeSha);

        var document = TextEncoding.Decode(originalBytes, requestedEncoding);
        var updated = patch.Apply(document.Text, document.DominantNewline);
        var targetEncoding = requestedEncoding is null
            ? document.Spec
            : TextEncoding.ParseForWrite(requestedEncoding);
        var updatedBytes = TextEncoding.Encode(updated, targetEncoding, maxBytes);
        if (!originalBytes.AsSpan().SequenceEqual(updatedBytes)) AtomicWrite(target, updatedBytes, snapshot, maxBytes);

        return new Dictionary<string, object?>
        {
            ["dst"] = target,
            ["file"] = patch.RelativePath,
            ["hunks"] = patch.Hunks.Count,
            ["sha_before"] = beforeSha,
            ["sha"] = Sha256(updatedBytes),
            ["enc"] = targetEncoding.Name,
            ["bom"] = targetEncoding.EmitBom,
            ["partial"] = false
        };
    }

    private static Dictionary<string, object?> WriteResult(
        string path,
        TextEncodingSpec encoding,
        byte[] bytes) => new Dictionary<string, object?>
        {
            ["dst"] = path,
            ["enc"] = encoding.Name,
            ["bom"] = encoding.EmitBom,
            ["sha"] = Sha256(bytes),
            ["len"] = bytes.LongLength
        };

    private static bool EndsWithNewline(string value) => value.EndsWith('\n') || value.EndsWith('\r');

    private static string CanonicalDirectPath(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
        var allowedColon = root.IndexOf(':');
        if (allowedColon >= 0 && root.IndexOf(':', allowedColon + 1) >= 0) throw new AgentOpsException("precondition");
        for (var i = 0; i < path.Length; i++)
            if (path[i] == ':' && i != allowedColon) throw new AgentOpsException("precondition");
        return path;
    }

    private static void RejectDirectoryTarget(string path)
    {
        if (Directory.Exists(path)) throw new AgentOpsException("collision");
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new AgentOpsException("not_found");
    }

    private static TextFileSnapshot CaptureSnapshot(string path, long maxBytes, bool requireExisting)
    {
        ValidateLimit(maxBytes);
        if (OperatingSystem.IsWindows()) return WindowsTextPublication.Capture(path, maxBytes, requireExisting);

        var parent = Path.GetDirectoryName(path) ?? throw new AgentOpsException("invalid");
        if (!Directory.Exists(parent)) throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(parent);
        if (!File.Exists(path))
        {
            if (requireExisting) throw new AgentOpsException("not_found");
            return new TextFileSnapshot(false, [], null, null);
        }
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new TextFileSnapshot(true, ReadBoundedStream(stream, maxBytes), null, null);
    }

    internal static byte[] ReadBoundedStream(Stream stream, long maxBytes)
    {
        ValidateLimit(maxBytes);
        if (stream.CanSeek && stream.Length > maxBytes) throw new AgentOpsException("invalid");

        using var result = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, maxBytes) : 0);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new AgentOpsException("invalid");
            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    private static void AtomicWrite(string path, byte[] bytes, TextFileSnapshot expected, long maxBytes)
    {
        PublicationHookForTests?.Invoke(TextPublicationHookPoint.BeforePublicationLease, path);
        if (OperatingSystem.IsWindows())
        {
            WindowsTextPublication.Publish(path, bytes, expected, maxBytes, PublicationHookForTests);
            return;
        }

        var current = CaptureSnapshot(path, maxBytes, requireExisting: expected.Exists);
        EnsureSamePreimage(expected, current);
        PublicationHookForTests?.Invoke(TextPublicationHookPoint.AfterPublicationLease, path);
        WriteTemporaryAndMove(path, bytes, expected.Exists);
    }

    internal static void EnsureSamePreimage(TextFileSnapshot expected, TextFileSnapshot current)
    {
        if (expected.Exists != current.Exists || expected.ParentIdentity != current.ParentIdentity ||
            expected.Identity != current.Identity || !expected.Bytes.AsSpan().SequenceEqual(current.Bytes))
            throw new AgentOpsException("precondition", failure: FailureEvidence.TextGuard("preimage_changed", "sha"));
    }

    internal static void WriteTemporaryAndMove(string path, byte[] bytes, bool overwrite)
    {
        path = Path.GetFullPath(path);
        RejectDirectoryTarget(path);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.pao-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // The target was never published if the temp still exists. Cleanup
                // failure must not hide the original publication error.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolvePatchTarget(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            Regex.IsMatch(relativePath, "^[A-Za-z]:", RegexOptions.CultureInvariant))
            throw new AgentOpsException("precondition");

        var components = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var invalidNameCharacters = Path.GetInvalidFileNameChars();
        if (components.Length == 0 || components.Any(x => x is "." or ".." || x.IndexOfAny(invalidNameCharacters) >= 0))
            throw new AgentOpsException("precondition");

        var target = Path.GetFullPath(Path.Combine(workspaceRoot, Path.Combine(components)));
        if (!PathNormalizer.Contains(workspaceRoot, target)) throw new AgentOpsException("precondition");
        PathNormalizer.RejectReparseComponents(workspaceRoot, target);
        if (!File.Exists(target)) throw new AgentOpsException("not_found");
        return target;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var found = text.IndexOf(value, start, StringComparison.Ordinal);
            if (found < 0) break;
            count++;
            start = found + value.Length;
        }
        return count;
    }

    private static string NormalizeNewlines(string text, string? newline)
    {
        if (newline is null) return text;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);
    }

    private static void ValidateExpectedHash(string? expected, string actual)
    {
        if (expected is null) return;
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new AgentOpsException("precondition", failure: FailureEvidence.TextGuard("preimage_invalid", "sha"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected, actual))
            throw new AgentOpsException("precondition", failure: FailureEvidence.TextGuard("preimage_changed", "sha"));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string BoundUtf8(string value, int maxBytes, out bool truncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            truncated = false;
            return value;
        }

        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes) break;
            result.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        truncated = true;
        return result.ToString();
    }

    private static void ValidateLimit(long maxBytes)
    {
        if (maxBytes is < 1 or > int.MaxValue) throw new AgentOpsException("invalid");
    }
}

internal enum TextPublicationHookPoint
{
    BeforePublicationLease,
    AfterPublicationLease
}

internal readonly record struct TextFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

internal sealed record TextFileSnapshot(
    bool Exists,
    byte[] Bytes,
    TextFileIdentity? Identity,
    TextFileIdentity? ParentIdentity);

[SupportedOSPlatform("windows")]
internal static class WindowsTextPublication
{
    public static TextFileSnapshot Capture(string path, long maxBytes, bool requireExisting)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new AgentOpsException("invalid");
        using var directories = PinnedDirectories.Open(parent);
        var opened = TryOpenFile(path, maxBytes, missingIsPrecondition: false);
        if (opened is null)
        {
            if (requireExisting) throw new AgentOpsException("not_found");
            return new TextFileSnapshot(false, [], null, directories.ParentIdentity);
        }
        using (opened) return new TextFileSnapshot(true, opened.Bytes, opened.Identity, directories.ParentIdentity);
    }

    public static void Publish(
        string path,
        byte[] bytes,
        TextFileSnapshot expected,
        long maxBytes,
        Action<TextPublicationHookPoint, string>? hook)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new AgentOpsException("invalid");
        using var directories = PinnedDirectories.Open(parent);
        if (expected.ParentIdentity is null || directories.ParentIdentity != expected.ParentIdentity)
            throw new AgentOpsException("precondition");

        // Keep the parent chain leased throughout publication, but release the
        // target file handle after each comparison. Holding the target open can
        // prevent ReplaceFile/MoveFileEx semantics even when FILE_SHARE_DELETE
        // was requested, while the directory leases are the handles that close
        // the parent-to-junction swap window this path is defending against.
        using (var current = TryOpenFile(path, maxBytes, missingIsPrecondition: true))
        {
            var currentSnapshot = current is null
                ? new TextFileSnapshot(false, [], null, directories.ParentIdentity)
                : new TextFileSnapshot(true, current.Bytes, current.Identity, directories.ParentIdentity);
            ExtendedOperations.EnsureSamePreimage(expected, currentSnapshot);
        }

        // Directory handles omit FILE_SHARE_DELETE and remain open through the
        // move. Every path component is therefore pinned and cannot be swapped
        // for a junction while the publication is in progress.
        hook?.Invoke(TextPublicationHookPoint.AfterPublicationLease, path);

        using (var revalidated = TryOpenFile(path, maxBytes, missingIsPrecondition: true))
        {
            var finalSnapshot = revalidated is null
                ? new TextFileSnapshot(false, [], null, directories.ParentIdentity)
                : new TextFileSnapshot(true, revalidated.Bytes, revalidated.Identity, directories.ParentIdentity);
            ExtendedOperations.EnsureSamePreimage(expected, finalSnapshot);
        }

        try
        {
            ExtendedOperations.WriteTemporaryAndMove(path, bytes, expected.Exists);
        }
        catch (IOException ex) when (!expected.Exists)
        {
            // File creation uses overwrite=false. A new target admitted after
            // the absent preimage check is a failed compare-and-swap.
            throw new AgentOpsException("precondition", inner: ex);
        }
    }

    private static OpenedFile? TryOpenFile(string path, long maxBytes, bool missingIsPrecondition)
    {
        var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GenericRead | NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareDelete,
            0,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint | NativeMethods.FileFlagSequentialScan,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound) return null;
            throw NativeMethods.MapFailure(error, missingIsPrecondition ? "precondition" : "not_found");
        }

        try
        {
            var information = NativeMethods.Information(handle);
            if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0 ||
                (information.FileAttributes & NativeMethods.FileAttributeDirectory) != 0)
                throw new AgentOpsException(missingIsPrecondition ? "precondition" : "not_found");
            var identity = NativeMethods.Identity(information);
            var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            handle = null!;
            var content = ExtendedOperations.ReadBoundedStream(stream, maxBytes);
            return new OpenedFile(stream, identity, content);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private sealed class OpenedFile : IDisposable
    {
        private readonly FileStream _stream;

        public OpenedFile(FileStream stream, TextFileIdentity identity, byte[] bytes)
        {
            _stream = stream;
            Identity = identity;
            Bytes = bytes;
        }

        public TextFileIdentity Identity { get; }
        public byte[] Bytes { get; }
        public void Dispose() => _stream.Dispose();
    }

    private sealed class PinnedDirectories : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;

        private PinnedDirectories(List<SafeFileHandle> handles, TextFileIdentity parentIdentity)
        {
            _handles = handles;
            ParentIdentity = parentIdentity;
        }

        public TextFileIdentity ParentIdentity { get; }

        public static PinnedDirectories Open(string parent)
        {
            parent = Path.GetFullPath(parent);
            var root = Path.GetPathRoot(parent) ?? throw new AgentOpsException("invalid");
            var paths = new List<string> { root };
            var current = root;
            var relative = Path.GetRelativePath(root, parent);
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

            var handles = new List<SafeFileHandle>(paths.Count);
            try
            {
                TextFileIdentity? parentIdentity = null;
                foreach (var directory in paths)
                {
                    var handle = NativeMethods.CreateFileW(
                        directory,
                        NativeMethods.FileReadAttributes,
                        NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                        0,
                        NativeMethods.OpenExisting,
                        NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                        0);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw NativeMethods.MapFailure(error, "precondition");
                    }
                    var information = NativeMethods.Information(handle);
                    if ((information.FileAttributes & NativeMethods.FileAttributeDirectory) == 0 ||
                        (information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new AgentOpsException("precondition");
                    }
                    parentIdentity = NativeMethods.Identity(information);
                    handles.Add(handle);
                }
                return new PinnedDirectories(handles, parentIdentity ?? throw new AgentOpsException("precondition"));
            }
            catch
            {
                foreach (var handle in handles) handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        }
    }

    private static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint FileShareDelete = 0x00000004;
        public const uint OpenExisting = 3;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const uint FileFlagSequentialScan = 0x08000000;
        public const uint FileAttributeDirectory = 0x00000010;
        public const uint FileAttributeReparsePoint = 0x00000400;
        public const int ErrorFileNotFound = 2;
        public const int ErrorPathNotFound = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
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

        public static ByHandleFileInformation Information(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw MapFailure(Marshal.GetLastWin32Error(), "precondition");
            return information;
        }

        public static TextFileIdentity Identity(ByHandleFileInformation information) => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

        public static AgentOpsException MapFailure(int error, string missingCode) => error switch
        {
            ErrorFileNotFound or ErrorPathNotFound => new AgentOpsException(missingCode),
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
    }
}

internal sealed record TextDocument(string Text, TextEncodingSpec Spec, bool Bom, string? DominantNewline);

internal sealed record TextEncodingSpec(Encoding Encoding, string Name, bool EmitBom)
{
    public byte[] Preamble => !EmitBom ? [] : Name switch
    {
        "utf8" => [0xEF, 0xBB, 0xBF],
        "utf16le" => [0xFF, 0xFE],
        "utf16be" => [0xFE, 0xFF],
        _ => Encoding.GetPreamble()
    };
}

internal static class TextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, true, true);
    private static readonly Encoding StrictAscii = Encoding.GetEncoding(
        20127,
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static TextEncodingSpec Utf8NoBom { get; } = new(StrictUtf8, "utf8", false);

    public static TextDocument Decode(byte[] bytes, string? requested)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var bomLength = 0;
        TextEncodingSpec spec;
        var bom = false;

        if (requested is not null)
        {
            spec = ParseForWrite(requested);
            bomLength = MatchingPreambleLength(bytes, spec.Encoding);
            bom = bomLength > 0;
        }
        else if (StartsWith(bytes, [0xEF, 0xBB, 0xBF]))
        {
            spec = new TextEncodingSpec(StrictUtf8, "utf8", true);
            bomLength = 3;
            bom = true;
        }
        else if (StartsWith(bytes, [0xFF, 0xFE]))
        {
            spec = new TextEncodingSpec(StrictUtf16Le, "utf16le", true);
            bomLength = 2;
            bom = true;
        }
        else if (StartsWith(bytes, [0xFE, 0xFF]))
        {
            spec = new TextEncodingSpec(StrictUtf16Be, "utf16be", true);
            bomLength = 2;
            bom = true;
        }
        else if (bytes.All(x => x <= 0x7F))
        {
            spec = new TextEncodingSpec(StrictAscii, "ascii", false);
        }
        else
        {
            spec = Utf8NoBom;
        }

        try
        {
            var text = spec.Encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            return new TextDocument(text, spec, bom, DetectDominantNewline(text));
        }
        catch (DecoderFallbackException ex)
        {
            throw new AgentOpsException("encoding_required", inner: ex);
        }
    }

    public static TextEncodingSpec ParseForWrite(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new AgentOpsException("invalid");
        var normalized = requested.Trim().ToLowerInvariant();
        return normalized switch
        {
            "ascii" => new TextEncodingSpec(StrictAscii, "ascii", false),
            "utf8" or "utf-8" => Utf8NoBom,
            "utf8bom" or "utf-8-bom" => new TextEncodingSpec(StrictUtf8, "utf8", true),
            "utf16le" or "utf-16le" => new TextEncodingSpec(StrictUtf16Le, "utf16le", true),
            "utf16be" or "utf-16be" => new TextEncodingSpec(StrictUtf16Be, "utf16be", true),
            _ when normalized.StartsWith("codepage:", StringComparison.Ordinal) => ParseCodePage(normalized),
            _ => throw new AgentOpsException("invalid")
        };
    }

    public static byte[] Encode(string text, TextEncodingSpec spec, long maxBytes)
    {
        try
        {
            var contentLength = spec.Encoding.GetByteCount(text);
            var preamble = spec.Preamble;
            if ((long)contentLength + preamble.Length > maxBytes) throw new AgentOpsException("invalid");
            var bytes = new byte[contentLength + preamble.Length];
            preamble.CopyTo(bytes, 0);
            spec.Encoding.GetBytes(text, 0, text.Length, bytes, preamble.Length);
            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new AgentOpsException("encoding_required", inner: ex);
        }
    }

    private static TextEncodingSpec ParseCodePage(string normalized)
    {
        if (!int.TryParse(normalized[9..], NumberStyles.None, CultureInfo.InvariantCulture, out var codePage) || codePage <= 0)
            throw new AgentOpsException("invalid");
        try
        {
            var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return new TextEncodingSpec(encoding, $"codepage:{codePage}", false);
        }
        catch (ArgumentException ex)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }
    }

    private static int MatchingPreambleLength(byte[] bytes, Encoding encoding)
    {
        if (encoding.CodePage == Encoding.UTF8.CodePage && StartsWith(bytes, [0xEF, 0xBB, 0xBF])) return 3;
        if (encoding.CodePage == Encoding.Unicode.CodePage && StartsWith(bytes, [0xFF, 0xFE])) return 2;
        if (encoding.CodePage == Encoding.BigEndianUnicode.CodePage && StartsWith(bytes, [0xFE, 0xFF])) return 2;
        var preamble = encoding.GetPreamble();
        return preamble.Length > 0 && StartsWith(bytes, preamble) ? preamble.Length : 0;
    }

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) =>
        bytes.AsSpan().StartsWith(prefix);

    private static string? DetectDominantNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0) return null;
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }
}

internal sealed record UnifiedPatch(string RelativePath, IReadOnlyList<PatchHunk> Hunks)
{
    private static readonly Regex HunkHeader = new(
        "^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@(?: .*)?$",
        RegexOptions.CultureInvariant);

    public static UnifiedPatch ParseSingleFile(string text)
    {
        var lines = NormalizePatchLines(text);
        if (lines.Count < 3 || !lines[0].StartsWith("--- ", StringComparison.Ordinal) ||
            !lines[1].StartsWith("+++ ", StringComparison.Ordinal))
            throw new AgentOpsException("invalid");

        var oldPath = ParseHeaderPath(lines[0][4..], "a/");
        var newPath = ParseHeaderPath(lines[1][4..], "b/");
        if (!StringComparer.Ordinal.Equals(oldPath, newPath)) throw new AgentOpsException("precondition");

        var hunks = new List<PatchHunk>();
        var index = 2;
        while (index < lines.Count)
        {
            if (lines[index].StartsWith("--- ", StringComparison.Ordinal) ||
                lines[index].StartsWith("+++ ", StringComparison.Ordinal))
                throw new AgentOpsException("invalid");

            var match = HunkHeader.Match(lines[index]);
            if (!match.Success) throw new AgentOpsException("invalid");
            var oldStart = ParseCoordinate(match.Groups[1].Value);
            var oldCount = match.Groups[2].Success ? ParseCoordinate(match.Groups[2].Value) : 1;
            var newStart = ParseCoordinate(match.Groups[3].Value);
            var newCount = match.Groups[4].Success ? ParseCoordinate(match.Groups[4].Value) : 1;
            if ((oldCount > 0 && oldStart == 0) || (newCount > 0 && newStart == 0))
                throw new AgentOpsException("invalid");
            index++;

            var operations = new List<PatchLine>();
            while (index < lines.Count && !lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var line = lines[index++];
                if (line.Length == 0 || line[0] is not (' ' or '-' or '+'))
                    throw new AgentOpsException("invalid");
                operations.Add(new PatchLine(line[0], line[1..]));
            }
            if (operations.Count == 0) throw new AgentOpsException("invalid");

            var actualOld = operations.Count(x => x.Kind is ' ' or '-');
            var actualNew = operations.Count(x => x.Kind is ' ' or '+');
            if (actualOld != oldCount || actualNew != newCount) throw new AgentOpsException("invalid");
            hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, operations));
        }

        if (hunks.Count == 0) throw new AgentOpsException("invalid");
        return new UnifiedPatch(newPath, hunks);
    }

    public string Apply(string source, string? dominantNewline)
    {
        var sourceLines = DocumentLine.Split(source);
        var output = new List<DocumentLine>(sourceLines.Count);
        var sourceCursor = 0;
        var newline = dominantNewline ?? Environment.NewLine;

        foreach (var hunk in Hunks)
        {
            var targetIndex = RangeIndex(hunk.OldStart, hunk.OldCount);
            var targetNewIndex = RangeIndex(hunk.NewStart, hunk.NewCount);
            if (targetIndex < sourceCursor || targetIndex > sourceLines.Count || targetNewIndex != output.Count + targetIndex - sourceCursor)
                throw new AgentOpsException("precondition");

            while (sourceCursor < targetIndex) output.Add(sourceLines[sourceCursor++]);
            foreach (var operation in hunk.Lines)
            {
                switch (operation.Kind)
                {
                    case ' ':
                        RequireLine(sourceLines, sourceCursor, operation.Text);
                        output.Add(sourceLines[sourceCursor++]);
                        break;
                    case '-':
                        RequireLine(sourceLines, sourceCursor, operation.Text);
                        sourceCursor++;
                        break;
                    case '+':
                        output.Add(new DocumentLine(operation.Text, newline));
                        break;
                    default:
                        throw new AgentOpsException("invalid");
                }
            }
        }

        while (sourceCursor < sourceLines.Count) output.Add(sourceLines[sourceCursor++]);
        return DocumentLine.Join(output, newline);
    }

    private static int RangeIndex(int start, int count) => count == 0 ? start : start - 1;

    private static List<string> NormalizePatchLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string ParseHeaderPath(string header, string optionalPrefix)
    {
        var tab = header.IndexOf('\t');
        var path = (tab >= 0 ? header[..tab] : header).TrimEnd();
        if (path.StartsWith(optionalPrefix, StringComparison.Ordinal)) path = path[optionalPrefix.Length..];
        if (path is "/dev/null" or "NUL" || path.Length == 0) throw new AgentOpsException("precondition");
        return path;
    }

    private static int ParseCoordinate(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            throw new AgentOpsException("invalid");
        return parsed;
    }

    private static void RequireLine(IReadOnlyList<DocumentLine> source, int index, string expected)
    {
        if (index >= source.Count || !StringComparer.Ordinal.Equals(source[index].Content, expected))
            throw new AgentOpsException("precondition");
    }
}

internal sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<PatchLine> Lines);
internal sealed record PatchLine(char Kind, string Text);

internal sealed record DocumentLine(string Content, string Ending)
{
    public static List<DocumentLine> Split(string text)
    {
        var lines = new List<DocumentLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                var length = i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                lines.Add(new DocumentLine(text[start..i], text.Substring(i, length)));
                i += length - 1;
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                lines.Add(new DocumentLine(text[start..i], "\n"));
                start = i + 1;
            }
        }

        if (start < text.Length) lines.Add(new DocumentLine(text[start..], string.Empty));
        return lines;
    }

    public static string Join(IReadOnlyList<DocumentLine> lines, string fallbackNewline)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i].Content);
            var ending = lines[i].Ending;
            if (ending.Length == 0 && i + 1 < lines.Count) ending = fallbackNewline;
            builder.Append(ending);
        }
        return builder.ToString();
    }
}
