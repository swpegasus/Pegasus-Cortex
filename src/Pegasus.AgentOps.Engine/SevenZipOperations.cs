using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

public sealed record SevenZipLimits(
    int MaxEntries = 10_000,
    long MaxArchiveBytes = 1L * 1024 * 1024 * 1024,
    long MaxEntryBytes = 512L * 1024 * 1024,
    long MaxExpandedBytes = 2L * 1024 * 1024 * 1024,
    double MaxExpansionRatio = 1_000,
    int MaxListingBytes = 4 * 1024 * 1024,
    int MaxDiagnosticBytes = 1024 * 1024,
    int TimeoutSeconds = 300)
{
    internal void Validate()
    {
        if (MaxEntries is < 1 or > 1_000_000 ||
            MaxArchiveBytes < 1 || MaxEntryBytes < 0 || MaxExpandedBytes < 0 ||
            !double.IsFinite(MaxExpansionRatio) || MaxExpansionRatio < 1 ||
            MaxListingBytes is < 1 or > 64 * 1024 * 1024 ||
            MaxDiagnosticBytes is < 1 or > 16 * 1024 * 1024 ||
            TimeoutSeconds is < 1 or > 86_400)
            throw new AgentOpsException("invalid");
    }
}

public sealed record SevenZipArchiveEntry(
    string Path,
    bool Directory,
    long Size,
    long PackedSize,
    string? Crc32);

public sealed record SevenZipListResult(
    string Archive,
    int Entries,
    long ExpandedBytes,
    IReadOnlyList<SevenZipArchiveEntry> Items);

public sealed record SevenZipTestResult(
    bool Ok,
    string? Error,
    string Archive,
    int Entries,
    long ExpandedBytes);

public sealed record SevenZipExtractResult(
    bool Ok,
    string? Error,
    string Archive,
    string Destination,
    string Policy,
    int Entries,
    long ExpandedBytes,
    long Deleted,
    long Copied,
    long Changed,
    long Skipped,
    bool Partial);

public sealed record SevenZipProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds,
    long MaxStdoutBytes,
    int MaxStderrBytes);

public sealed record SevenZipProcessResult(
    int ExitCode,
    byte[] Stdout,
    string Stderr,
    long StdoutBytes,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    string? StreamError = null);

/// <summary>
/// Test seam around the only native-process boundary. Implementations receive
/// an executable and already-separated arguments; no command line is accepted.
/// </summary>
public interface ISevenZipProcessRunner
{
    Task<SevenZipProcessResult> RunTextAsync(
        SevenZipProcessRequest request,
        CancellationToken cancellationToken = default);

    Task<SevenZipProcessResult> RunToStreamAsync(
        SevenZipProcessRequest request,
        Stream stdoutDestination,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a registered 7z.exe directly. ProcessStartInfo.ArgumentList is the
/// sole argument boundary and both output streams are drained with allocation
/// limits. Timeout/cancellation terminates only the spawned process tree.
/// </summary>
public sealed class WindowsSevenZipProcessRunner : ISevenZipProcessRunner
{
    private readonly ProcessEnvironment _environment;

    public WindowsSevenZipProcessRunner(ProcessEnvironment? environment = null) =>
        _environment = environment ?? ProcessEnvironment.Empty;

    public Task<SevenZipProcessResult> RunTextAsync(
        SevenZipProcessRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(request, null, captureStdout: true, cancellationToken);

    public Task<SevenZipProcessResult> RunToStreamAsync(
        SevenZipProcessRequest request,
        Stream stdoutDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdoutDestination);
        return RunAsync(request, stdoutDestination, captureStdout: false, cancellationToken);
    }

    private async Task<SevenZipProcessResult> RunAsync(
        SevenZipProcessRequest request,
        Stream? stdoutDestination,
        bool captureStdout,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (!OperatingSystem.IsWindows()) throw new AgentOpsException("platform");

        var start = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var item in _environment.Entries) start.Environment[item.Key] = item.Value;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new AgentOpsException("tool_start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new AgentOpsException("tool_start", inner: ex);
        }

        process.StandardInput.Close();
        using var captured = captureStdout ? new MemoryStream() : null;
        var stdoutTarget = stdoutDestination ?? captured!;
        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, stdoutTarget, request.MaxStdoutBytes);
        using var stderr = new MemoryStream();
        var stderrTask = PumpAsync(process.StandardError.BaseStream, stderr, request.MaxStderrBytes);

        var timedOut = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

        var exitCode = -1;
        try { if (process.HasExited) exitCode = process.ExitCode; }
        catch (InvalidOperationException) { }
        return new(
            exitCode,
            captureStdout ? captured!.ToArray() : [],
            Encoding.UTF8.GetString(stderr.ToArray()),
            stdout.TotalBytes,
            stdout.Truncated,
            error.Truncated,
            timedOut,
            stdout.WriteError);
    }

    private static async Task<PumpResult> PumpAsync(Stream source, Stream destination, long maximumBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        long written = 0;
        var truncated = false;
        string? writeError = null;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None).ConfigureAwait(false);
                if (read == 0) break;
                total = total > long.MaxValue - read ? long.MaxValue : total + read;
                var permitted = writeError is null ? (int)Math.Min(read, Math.Max(0, maximumBytes - written)) : 0;
                if (permitted > 0)
                {
                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, permitted), CancellationToken.None).ConfigureAwait(false);
                        written += permitted;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        writeError = "stream_write";
                    }
                }
                if (permitted != read) truncated = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new(total, truncated, writeError);
    }

    private static void ValidateRequest(SevenZipProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) ||
            !Path.IsPathFullyQualified(request.ExecutablePath) ||
            request.Arguments.Count is < 1 or > 4_096 ||
            request.Arguments.Any(static value => value is null || value.IndexOf('\0') >= 0) ||
            request.TimeoutSeconds is < 1 or > 86_400 ||
            request.MaxStdoutBytes < 0 ||
            request.MaxStderrBytes is < 1 or > 16 * 1024 * 1024)
            throw new AgentOpsException("invalid");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private readonly record struct PumpResult(long TotalBytes, bool Truncated, string? WriteError);
}

/// <summary>
/// Safe 7-Zip adapter. 7-Zip may read an archive and emit a single admitted
/// file to stdout, but it is never given a destination directory.
/// </summary>
public sealed class SevenZipOperations
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ISevenZipProcessRunner _runner;

    public SevenZipOperations(ISevenZipProcessRunner? runner = null) =>
        _runner = runner ?? new WindowsSevenZipProcessRunner();

    public async Task<SevenZipListResult> ListAsync(
        string executablePath,
        string archivePath,
        SevenZipLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new SevenZipLimits();
        limits.Validate();
        using var executable = PinnedFile.OpenExecutable(executablePath);
        using var archive = PinnedFile.OpenArchive(archivePath, limits.MaxArchiveBytes);
        RejectSameFile(executable, archive);
        var manifest = await ReadManifestAsync(executable, archive, limits, cancellationToken).ConfigureAwait(false);
        executable.Revalidate();
        archive.Revalidate();
        return new(archive.Path, manifest.Entries.Count, manifest.ExpandedBytes, manifest.Entries);
    }

    public async Task<SevenZipTestResult> TestAsync(
        string executablePath,
        string archivePath,
        SevenZipLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new SevenZipLimits();
        limits.Validate();
        using var executable = PinnedFile.OpenExecutable(executablePath);
        using var archive = PinnedFile.OpenArchive(archivePath, limits.MaxArchiveBytes);
        RejectSameFile(executable, archive);
        var manifest = await ReadManifestAsync(executable, archive, limits, cancellationToken).ConfigureAwait(false);
        var error = await TestIntegrityAsync(executable, archive, limits, cancellationToken).ConfigureAwait(false);
        executable.Revalidate();
        archive.Revalidate();
        return new(error is null, error, archive.Path, manifest.Entries.Count, manifest.ExpandedBytes);
    }

    public async Task<SevenZipExtractResult> ExtractAsync(
        string executablePath,
        string archivePath,
        string destinationPath,
        string? policy = null,
        SevenZipLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new SevenZipLimits();
        limits.Validate();
        var pol = NormalizePolicy(policy);
        var destination = ExactPath.Canonicalize(destinationPath, requireExisting: false);
        ExactPath.RejectVolumeRoot(destination);
        ValidateDestination(destination, pol);

        using var executable = PinnedFile.OpenExecutable(executablePath);
        using var archive = PinnedFile.OpenArchive(archivePath, limits.MaxArchiveBytes);
        RejectSameFile(executable, archive);
        if (ExactPath.Same(destination, executable.Path) || ExactPath.Same(destination, archive.Path))
            throw new AgentOpsException("precondition");
        if (ExactPath.Contains(destination, executable.Path) || ExactPath.Contains(destination, archive.Path))
            throw new AgentOpsException("precondition");

        var manifest = await ReadManifestAsync(executable, archive, limits, cancellationToken).ConfigureAwait(false);
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ExactPath.CombineAdmitted(destination,
                entry.Directory ? entry.Path.TrimEnd(Path.DirectorySeparatorChar) : entry.Path);
            if (!entry.Directory) _ = TemporaryPath(target);
        }
        var integrityError = await TestIntegrityAsync(executable, archive, limits, cancellationToken).ConfigureAwait(false);
        executable.Revalidate();
        archive.Revalidate();
        if (integrityError is not null)
            return Failure(integrityError, archive.Path, destination, pol, manifest, new Counters());

        var counters = new Counters();
        var cleanCompleted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareDestination(destination, pol, counters, cancellationToken);
            cleanCompleted = pol == "clean";
            using var rootPin = DirectoryHandle.OpenPinned(destination);

            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                executable.Revalidate();
                archive.Revalidate();
                if (entry.Directory)
                {
                    EnsureDirectoryPath(destination, entry.Path, pol, counters, out _);
                    continue;
                }

                await ExtractFileAsync(
                    executable,
                    archive,
                    destination,
                    entry,
                    pol,
                    limits,
                    counters,
                    cancellationToken).ConfigureAwait(false);
            }

            executable.Revalidate();
            archive.Revalidate();
            return new(
                true, null, archive.Path, destination, pol,
                manifest.Entries.Count, manifest.ExpandedBytes,
                counters.Deleted, counters.Copied, counters.Changed, counters.Skipped, false);
        }
        catch (OperationCanceledException)
        {
            return Failure("canceled", archive.Path, destination, pol, manifest, counters);
        }
        catch (AgentOpsException ex)
        {
            var error = cleanCompleted && ex.Code != "cleanup_pending" ? "copy_incomplete" : ex.Code;
            return Failure(error, archive.Path, destination, pol, manifest, counters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            var error = cleanCompleted ? "copy_incomplete" : "archive_extract_failed";
            return Failure(error, archive.Path, destination, pol, manifest, counters);
        }
    }

    private async Task<Manifest> ReadManifestAsync(
        PinnedFile executable,
        PinnedFile archive,
        SevenZipLimits limits,
        CancellationToken cancellationToken)
    {
        var request = Request(executable.Path,
            ["l", "-slt", "-ba", "-bd", "-bb0", "-sccUTF-8", "--", archive.Path],
            limits, limits.MaxListingBytes);
        var result = await _runner.RunTextAsync(request, cancellationToken).ConfigureAwait(false);
        executable.Revalidate();
        archive.Revalidate();
        RequireProcessSuccess(result, "archive_invalid");
        string listing;
        try { listing = StrictUtf8.GetString(result.Stdout); }
        catch (DecoderFallbackException ex) { throw new AgentOpsException("archive_listing", inner: ex); }
        return TechnicalListingParser.Parse(listing, archive.Length, limits);
    }

    private async Task<string?> TestIntegrityAsync(
        PinnedFile executable,
        PinnedFile archive,
        SevenZipLimits limits,
        CancellationToken cancellationToken)
    {
        var request = Request(executable.Path,
            ["t", "-bd", "-bb0", "-y", "-sccUTF-8", "--", archive.Path],
            limits, limits.MaxDiagnosticBytes);
        var result = await _runner.RunTextAsync(request, cancellationToken).ConfigureAwait(false);
        executable.Revalidate();
        archive.Revalidate();
        if (result.TimedOut) return "timeout";
        if (result.StdoutTruncated || result.StderrTruncated || result.StreamError is not null) return "tool_output_limit";
        return result.ExitCode == 0 ? null : "archive_invalid";
    }

    private async Task ExtractFileAsync(
        PinnedFile executable,
        PinnedFile archive,
        string root,
        SevenZipArchiveEntry entry,
        string policy,
        SevenZipLimits limits,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var parentRelative = Path.GetDirectoryName(entry.Path) ?? string.Empty;
        if (!EnsureDirectoryPath(root, parentRelative, policy, counters, out var parentBlocked) || parentBlocked)
        {
            counters.Skipped++;
            return;
        }

        var target = ExactPath.CombineAdmitted(root, entry.Path);
        using var parentPins = DirectoryChain.Pin(root, Path.GetDirectoryName(target)!);
        if (TryAttributes(target, out var existing))
        {
            if ((existing & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("reparse_boundary");
            if ((existing & FileAttributes.Directory) != 0)
            {
                if (policy == "skip") { counters.Skipped++; return; }
                throw new AgentOpsException("collision");
            }
            if (policy == "skip") { counters.Skipped++; return; }
            if (policy == "fail") throw new AgentOpsException("collision");
        }

        var temporary = TemporaryPath(target);
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            await using (var verified = new Crc32WriteStream(output, entry.Size))
            {
                var request = Request(executable.Path,
                    ["x", "-so", "-bd", "-bb0", "-y", "-sccUTF-8", "--", archive.Path, entry.Path],
                    limits, entry.Size);
                var result = await _runner.RunToStreamAsync(request, verified, cancellationToken).ConfigureAwait(false);
                executable.Revalidate();
                archive.Revalidate();
                RequireProcessSuccess(result, "archive_extract_failed");
                await verified.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (verified.LengthWritten != entry.Size) throw new AgentOpsException("archive_length");
                if (!StringComparer.OrdinalIgnoreCase.Equals(verified.Crc32, entry.Crc32))
                    throw new AgentOpsException("archive_crc");
            }

            executable.Revalidate();
            archive.Revalidate();
            ExactPath.RejectReparseAncestors(target, includeLeaf: false);
            if (TryAttributes(target, out existing))
            {
                if ((existing & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("reparse_boundary");
                if ((existing & FileAttributes.Directory) != 0) throw new AgentOpsException("collision");
                if (policy == "skip") { counters.Skipped++; return; }
                if (policy == "fail") throw new AgentOpsException("collision");
            }
            File.Move(temporary, target, overwrite: policy is "replace" or "clean");
            counters.Copied++;
            counters.Changed++;
        }
        finally
        {
            if (!TryDeleteTemporary(temporary))
            {
                counters.Changed++;
                throw new AgentOpsException("cleanup_pending");
            }
        }
    }

    private static bool EnsureDirectoryPath(
        string root,
        string relativePath,
        string policy,
        Counters counters,
        out bool blocked)
    {
        blocked = false;
        if (string.IsNullOrEmpty(relativePath)) return true;
        var admitted = ExactPath.CombineAdmitted(root, relativePath);
        var relative = Path.GetRelativePath(root, admitted);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            using var pins = DirectoryChain.Pin(root, current);
            current = Path.Combine(current, segment);
            if (TryAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("reparse_boundary");
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (policy == "skip") { blocked = true; return false; }
                    throw new AgentOpsException("collision");
                }
                continue;
            }
            Directory.CreateDirectory(current);
            using var created = DirectoryHandle.OpenPinned(current);
            counters.Changed++;
        }
        return true;
    }

    private static void PrepareDestination(
        string destination,
        string policy,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(destination))
        {
            var parent = Path.GetDirectoryName(destination) ?? throw new AgentOpsException("invalid");
            using var parentPins = DirectoryChain.Pin(parent, parent);
            Directory.CreateDirectory(destination);
            using var created = DirectoryHandle.OpenPinned(destination);
            counters.Changed++;
        }
        if (policy != "clean") return;

        try
        {
            using var root = DirectoryHandle.OpenPinned(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(destination).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteEntryNoFollow(child, counters, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or AgentOpsException)
        {
            throw new AgentOpsException("clean_failed", counters.Deleted, counters.Copied, ex);
        }
    }

    private static void DeleteEntryNoFollow(
        string path,
        Counters counters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var entry = DirectoryHandle.OpenForDelete(path);
        if (entry.Directory && !entry.ReparsePoint)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path).ToArray())
                DeleteEntryNoFollow(child, counters, cancellationToken);
        }
        entry.MarkDelete();
        counters.Deleted++;
        counters.Changed++;
    }

    private static void ValidateDestination(string destination, string policy)
    {
        ExactPath.RejectReparseAncestors(destination, includeLeaf: true);
        if (File.Exists(destination)) throw new AgentOpsException("collision");
        if (Directory.Exists(destination) && policy == "fail") throw new AgentOpsException("collision");
        var parent = Path.GetDirectoryName(destination) ?? throw new AgentOpsException("invalid");
        if (!Directory.Exists(parent)) throw new AgentOpsException("not_found");
        ExactPath.RejectReparseAncestors(parent, includeLeaf: true);
    }

    private static SevenZipProcessRequest Request(
        string executablePath,
        IReadOnlyList<string> arguments,
        SevenZipLimits limits,
        long maxStdoutBytes) =>
        new(executablePath, arguments, limits.TimeoutSeconds, maxStdoutBytes, limits.MaxDiagnosticBytes);

    private static void RequireProcessSuccess(SevenZipProcessResult result, string nonzeroCode)
    {
        if (result.TimedOut) throw new AgentOpsException("timeout");
        if (result.StdoutTruncated || result.StderrTruncated || result.StreamError is not null)
            throw new AgentOpsException("tool_output_limit");
        if (result.ExitCode != 0) throw new AgentOpsException(nonzeroCode);
    }

    private static SevenZipExtractResult Failure(
        string error,
        string archive,
        string destination,
        string policy,
        Manifest manifest,
        Counters counters) =>
        new(false, error, archive, destination, policy, manifest.Entries.Count, manifest.ExpandedBytes,
            counters.Deleted, counters.Copied, counters.Changed, counters.Skipped, counters.Changed > 0);

    private static string NormalizePolicy(string? policy)
    {
        var normalized = string.IsNullOrWhiteSpace(policy) ? "fail" : policy.ToLowerInvariant();
        return normalized is "fail" or "replace" or "skip" or "clean"
            ? normalized
            : throw new AgentOpsException("invalid");
    }

    private static void RejectSameFile(PinnedFile left, PinnedFile right)
    {
        if (left.Identity == right.Identity) throw new AgentOpsException("precondition");
    }

    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { attributes = default; return false; }
    }

    private static string TemporaryPath(string target) =>
        ArchivePathBudget.RequireAbsolute(Path.Combine(Path.GetDirectoryName(target)!,
            $".pao-7z-{Guid.NewGuid():N}.tmp"));

    private static bool TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed class Counters
    {
        public long Deleted;
        public long Copied;
        public long Changed;
        public long Skipped;
    }

    private sealed record Manifest(IReadOnlyList<SevenZipArchiveEntry> Entries, long ExpandedBytes);

    private static class TechnicalListingParser
    {
        private static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal)
        {
            "Path", "Folder", "Size", "Packed Size", "Modified", "Created", "Accessed",
            "Attributes", "CRC", "Encrypted", "Method", "Block", "Comment",
            "Characteristics", "Host OS", "Version", "Volume Index", "Offset"
        };

        private static readonly HashSet<string> LinkKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Symbolic Link", "Hard Link", "Link", "Reparse", "Alternate Stream"
        };

        public static Manifest Parse(string text, long archiveLength, SevenZipLimits limits)
        {
            var blocks = ReadBlocks(text);
            var entries = new List<SevenZipArchiveEntry>();
            var paths = new PathShapeSet();
            long expanded = 0;
            foreach (var block in blocks)
            {
                if (block.ContainsKey("Type") || block.ContainsKey("Physical Size")) continue;
                if (!block.ContainsKey("Path")) throw new AgentOpsException("archive_listing");
                foreach (var key in block.Keys)
                {
                    if (LinkKeys.Contains(key)) throw new AgentOpsException("archive_special");
                    if (!EntryKeys.Contains(key)) throw new AgentOpsException("archive_listing");
                }

                var path = AdmitArchivePath(Required(block, "Path"));
                var attributes = block.GetValueOrDefault("Attributes", string.Empty);
                var directory = ClassifyAttributes(
                    attributes,
                    Required(block, "Folder"),
                    path.EndsWith(Path.DirectorySeparatorChar));
                path = path.TrimEnd(Path.DirectorySeparatorChar);
                var size = ParseLength(Required(block, "Size"));
                var packed = ParsePackedLength(block, directory);
                if (directory && size != 0) throw new AgentOpsException("archive_special");
                if (!directory && size > limits.MaxEntryBytes) throw new AgentOpsException("archive_limit");
                if (!directory && size > 0 && packed == 0) throw new AgentOpsException("archive_limit");
                if (!directory && size > 0 && (double)size / packed > limits.MaxExpansionRatio)
                    throw new AgentOpsException("archive_limit");
                if (block.GetValueOrDefault("Encrypted", "-") != "-")
                    throw new AgentOpsException("archive_encrypted");

                string? crc = null;
                if (!directory)
                {
                    crc = Required(block, "CRC");
                    if (crc.Length != 8 || !uint.TryParse(crc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                        throw new AgentOpsException("archive_listing");
                }
                else if (block.TryGetValue("CRC", out var directoryCrc) && directoryCrc.Length != 0)
                {
                    if (directoryCrc.Length != 8 || !uint.TryParse(directoryCrc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                        throw new AgentOpsException("archive_listing");
                }

                paths.Add(path, directory);
                if (entries.Count == limits.MaxEntries) throw new AgentOpsException("archive_limit");
                try { expanded = checked(expanded + size); }
                catch (OverflowException ex) { throw new AgentOpsException("archive_limit", inner: ex); }
                if (expanded > limits.MaxExpandedBytes) throw new AgentOpsException("archive_limit");
                entries.Add(new(path, directory, size, packed, crc));
            }

            if (entries.Count == 0) throw new AgentOpsException("archive_listing");
            if (expanded > 0 && (double)expanded / Math.Max(1, archiveLength) > limits.MaxExpansionRatio)
                throw new AgentOpsException("archive_limit");
            return new(entries, expanded);
        }

        private static IReadOnlyList<Dictionary<string, string>> ReadBlocks(string text)
        {
            if (text.IndexOf('\0') >= 0) throw new AgentOpsException("archive_listing");
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var blocks = new List<Dictionary<string, string>>();
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            var admissionStarted = false;
            foreach (var raw in normalized.Split('\n'))
            {
                if (raw.Length == 0)
                {
                    Flush();
                    continue;
                }
                if (raw.All(static c => c == '-'))
                {
                    Flush();
                    admissionStarted = true;
                    continue;
                }
                var separator = raw.IndexOf(" = ", StringComparison.Ordinal);
                if (separator < 1)
                {
                    if (!admissionStarted && current.Count == 0) continue;
                    throw new AgentOpsException("archive_listing");
                }
                var key = raw[..separator];
                var value = raw[(separator + 3)..];
                if (!current.TryAdd(key, value)) throw new AgentOpsException("archive_listing");
            }
            Flush();
            return blocks;

            void Flush()
            {
                if (current.Count == 0) return;
                blocks.Add(current);
                current = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static string Required(IReadOnlyDictionary<string, string> block, string key) =>
            block.TryGetValue(key, out var value) && value.Length > 0
                ? value
                : throw new AgentOpsException("archive_listing");

        private static long ParseLength(string value) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : throw new AgentOpsException("archive_listing");

        private static long ParsePackedLength(IReadOnlyDictionary<string, string> block, bool directory)
        {
            if (!block.TryGetValue("Packed Size", out var value) || value.Length == 0)
            {
                if (directory) return 0;
                throw new AgentOpsException("archive_listing");
            }
            return ParseLength(value);
        }

        private static bool ClassifyAttributes(string attributes, string folder, bool pathDirectory)
        {
            if (attributes.Length > 128 || folder is not ("+" or "-")) throw new AgentOpsException("archive_listing");
            var underscore = attributes.IndexOf('_');
            string windows;
            string unix;
            if (underscore >= 0)
            {
                windows = attributes[..underscore];
                unix = attributes[(underscore + 1)..].TrimStart();
            }
            else
            {
                var tokens = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0 && LooksLikeUnixMode(tokens[0]))
                {
                    windows = string.Empty;
                    unix = tokens[0];
                }
                else
                {
                    windows = tokens.FirstOrDefault() ?? string.Empty;
                    unix = tokens.Skip(1).FirstOrDefault(LooksLikeUnixMode) ?? string.Empty;
                }
            }
            foreach (var value in windows)
            {
                if (value is not ('A' or 'D' or 'R' or 'H' or 'S' or 'C' or 'I' or 'N'))
                    throw new AgentOpsException("archive_special");
            }
            var directory = folder == "+";
            if (windows.Contains('D') && !directory) throw new AgentOpsException("archive_special");
            if (unix.Length > 0)
            {
                var kind = unix[0];
                if (kind is 'l' or 'b' or 'c' or 'p' or 's') throw new AgentOpsException("archive_special");
                if (kind is not ('-' or 'd')) throw new AgentOpsException("archive_special");
                if ((kind == 'd') != directory) throw new AgentOpsException("archive_special");
            }
            if (pathDirectory && !directory) throw new AgentOpsException("archive_special");
            return directory;

            static bool LooksLikeUnixMode(string value) =>
                value.Length >= 10 && value[0] is '-' or 'd' or 'l' or 'b' or 'c' or 'p' or 's';
        }

        private static string AdmitArchivePath(string raw)
        {
            if (raw.Length is < 1 or > 1_024 || raw.IndexOf('\0') >= 0 || raw.IndexOfAny(['*', '?']) >= 0)
                throw new AgentOpsException("archive_name");
            if (raw.StartsWith('/') || raw.StartsWith('\\') ||
                (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':'))
                throw new AgentOpsException("archive_traversal");

            var replaced = raw.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var directoryMarker = replaced.EndsWith(Path.DirectorySeparatorChar);
            var pieces = replaced.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            if (pieces.Length == 0) throw new AgentOpsException("archive_name");
            foreach (var piece in pieces)
            {
                if (piece.Length is < 1 or > 255 || piece is "." or "..")
                    throw new AgentOpsException(piece == ".." ? "archive_traversal" : "archive_name");
                if (piece.EndsWith(' ') || piece.EndsWith('.') || piece.Contains(':') ||
                    piece.Any(static value => value < 32) || piece.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    IsReservedDevice(piece))
                    throw new AgentOpsException("archive_name");
                ArchivePathBudget.RequireSegment(piece);
            }
            var canonical = string.Join(Path.DirectorySeparatorChar, pieces);
            return directoryMarker ? canonical + Path.DirectorySeparatorChar : canonical;
        }

        private static bool IsReservedDevice(string segment)
        {
            var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
                return stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
            return false;
        }

        private sealed class PathShapeSet
        {
            private readonly Dictionary<string, PathShape> _paths = new(StringComparer.OrdinalIgnoreCase);

            public void Add(string path, bool directory)
            {
                var pieces = path.Split(Path.DirectorySeparatorChar);
                var current = string.Empty;
                for (var index = 0; index < pieces.Length - 1; index++)
                {
                    current = current.Length == 0 ? pieces[index] : Path.Combine(current, pieces[index]);
                    if (_paths.TryGetValue(current, out var prior) && prior == PathShape.File)
                        throw new AgentOpsException("archive_collision");
                    _paths.TryAdd(current, PathShape.ImplicitDirectory);
                }
                if (_paths.TryGetValue(path, out var existing))
                {
                    if (directory && existing == PathShape.ImplicitDirectory)
                    {
                        _paths[path] = PathShape.ExplicitDirectory;
                        return;
                    }
                    throw new AgentOpsException("archive_collision");
                }
                if (!directory)
                {
                    var prefix = path + Path.DirectorySeparatorChar;
                    if (_paths.Keys.Any(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        throw new AgentOpsException("archive_collision");
                }
                _paths.Add(path, directory ? PathShape.ExplicitDirectory : PathShape.File);
            }

            private enum PathShape { ImplicitDirectory, ExplicitDirectory, File }
        }
    }

    private sealed class Crc32WriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private uint _crc = uint.MaxValue;

        public Crc32WriteStream(Stream inner, long expectedLength)
        {
            _inner = inner;
            _expectedLength = expectedLength;
        }

        public long LengthWritten { get; private set; }
        public string Crc32 => (~_crc).ToString("X8", CultureInfo.InvariantCulture);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => LengthWritten;
        public override long Position { get => LengthWritten; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Admit(buffer.Length);
            _inner.Write(buffer);
            Update(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Admit(buffer.Length);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Update(buffer.Span);
        }

        private void Admit(int count)
        {
            if (count < 0 || LengthWritten > _expectedLength - count)
                throw new IOException("7-Zip emitted more bytes than declared.");
        }

        private void Update(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
                _crc = Crc32Table.Values[(_crc ^ value) & 0xff] ^ (_crc >> 8);
            LengthWritten += bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns and separately disposes the destination stream.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(false);
            return ValueTask.CompletedTask;
        }
    }

    private static class Crc32Table
    {
        public static readonly uint[] Values = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    private sealed class PinnedFile : IDisposable
    {
        private readonly FileStream _stream;
        private readonly Native.FileIdentity _identity;

        private PinnedFile(string path, FileStream stream, Native.FileIdentity identity)
        { Path = path; _stream = stream; _identity = identity; Length = identity.Length; }

        public string Path { get; }
        public long Length { get; }
        public Native.FileIdentity Identity => _identity;

        public static PinnedFile OpenExecutable(string path)
        {
            var canonical = ExactPath.Canonicalize(path, requireExisting: false);
            if (!canonical.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new AgentOpsException("precondition");
            return Open(canonical, "tool_not_found");
        }

        public static PinnedFile OpenArchive(string path, long maximumBytes)
        {
            var canonical = ExactPath.Canonicalize(path, requireExisting: false);
            var pinned = Open(canonical, "not_found");
            if (pinned.Length > maximumBytes)
            {
                pinned.Dispose();
                throw new AgentOpsException("archive_limit");
            }
            return pinned;
        }

        private static PinnedFile Open(string path, string missingCode)
        {
            ExactPath.RejectReparseAncestors(path, includeLeaf: true);
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    var identity = Native.Identity(stream.SafeFileHandle);
                    if (identity.Directory || identity.ReparsePoint) throw new AgentOpsException("reparse_boundary");
                    return new(path, stream, identity);
                }
                catch { stream.Dispose(); throw; }
            }
            catch (FileNotFoundException ex) { throw new AgentOpsException(missingCode, inner: ex); }
            catch (DirectoryNotFoundException ex) { throw new AgentOpsException(missingCode, inner: ex); }
            catch (UnauthorizedAccessException ex) { throw new AgentOpsException("access", inner: ex); }
            catch (IOException ex) { throw new AgentOpsException("precondition", inner: ex); }
        }

        public void Revalidate()
        {
            try
            {
                using var current = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Native.Identity(current.SafeFileHandle) != _identity) throw new AgentOpsException("precondition");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                throw new AgentOpsException("precondition", inner: ex);
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class DirectoryChain : IDisposable
    {
        private readonly List<DirectoryHandle> _handles = [];

        public static DirectoryChain Pin(string root, string targetDirectory)
        {
            var chain = new DirectoryChain();
            try
            {
                var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var canonicalTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
                if (!ExactPath.Contains(canonicalRoot, canonicalTarget)) throw new AgentOpsException("precondition");
                var current = canonicalRoot;
                chain._handles.Add(DirectoryHandle.OpenPinned(current));
                var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
                if (relative != ".")
                {
                    foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    {
                        current = Path.Combine(current, segment);
                        chain._handles.Add(DirectoryHandle.OpenPinned(current));
                    }
                }
                return chain;
            }
            catch { chain.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
            _handles.Clear();
        }
    }

    private sealed class DirectoryHandle : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private bool _deleteMarked;

        private DirectoryHandle(SafeFileHandle handle, bool directory, bool reparsePoint)
        { _handle = handle; Directory = directory; ReparsePoint = reparsePoint; }

        public bool Directory { get; }
        public bool ReparsePoint { get; }

        public static DirectoryHandle OpenPinned(string path)
        {
            var handle = Native.OpenPath(path, deleteAccess: false);
            try
            {
                var identity = Native.Identity(handle);
                if (!identity.Directory || identity.ReparsePoint) throw new AgentOpsException("reparse_boundary");
                return new(handle, true, false);
            }
            catch { handle.Dispose(); throw; }
        }

        public static DirectoryHandle OpenForDelete(string path)
        {
            var handle = Native.OpenPath(path, deleteAccess: true);
            try
            {
                var identity = Native.Identity(handle);
                return new(handle, identity.Directory, identity.ReparsePoint);
            }
            catch { handle.Dispose(); throw; }
        }

        public void MarkDelete()
        {
            Native.MarkDelete(_handle);
            _deleteMarked = true;
        }

        public void Dispose()
        {
            _handle.Dispose();
            _ = _deleteMarked;
        }
    }

    private static class ExactPath
    {
        public static string Canonicalize(string path, bool requireExisting)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                path.IndexOf('\0') >= 0 || path.IndexOfAny(['*', '?']) >= 0)
                throw new AgentOpsException("invalid");
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new AgentOpsException("invalid", inner: ex); }
            var root = Path.GetPathRoot(full) ?? throw new AgentOpsException("invalid");
            if (full.AsSpan(root.Length).Contains(':')) throw new AgentOpsException("precondition");
            ArchivePathBudget.RequireAbsolute(full);
            if (requireExisting && !File.Exists(full) && !Directory.Exists(full)) throw new AgentOpsException("not_found");
            return Path.TrimEndingDirectorySeparator(full);
        }

        public static void RejectVolumeRoot(string path)
        {
            var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
            if (Same(path, root)) throw new AgentOpsException("precondition");
        }

        public static string CombineAdmitted(string root, string relative)
        {
            if (Path.IsPathFullyQualified(relative)) throw new AgentOpsException("archive_traversal");
            var combined = Path.GetFullPath(relative, root);
            if (!Contains(root, combined)) throw new AgentOpsException("archive_traversal");
            return ArchivePathBudget.RequireAbsolute(combined);
        }

        public static bool Contains(string root, string candidate)
        {
            var relative = Path.GetRelativePath(root, candidate);
            return relative == "." || (!Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

        public static bool Same(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

        public static void RejectReparseAncestors(string path, bool includeLeaf)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? throw new AgentOpsException("invalid");
            var current = includeLeaf ? full : Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(current) && current.Length >= root.Length)
            {
                if (TryAttributes(current, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new AgentOpsException("reparse_boundary");
                if (Same(current, root)) break;
                current = Path.GetDirectoryName(current);
            }
        }
    }

    private static class Native
    {
        private const uint FileReadAttributes = 0x0080;
        private const uint Delete = 0x00010000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileDispositionInfoEx = 21;
        private const uint FileDispositionDelete = 0x00000001;
        private const uint FileDispositionPosixSemantics = 0x00000002;
        private const uint FileDispositionIgnoreReadonlyAttribute = 0x00000010;

        public readonly record struct FileIdentity(
            uint VolumeSerial,
            uint FileIndexHigh,
            uint FileIndexLow,
            long Length,
            long LastWrite,
            bool Directory,
            bool ReparsePoint);

        public static SafeFileHandle OpenPath(string path, bool deleteAccess)
        {
            if (!OperatingSystem.IsWindows()) throw new AgentOpsException("platform");
            var handle = CreateFileW(
                path,
                FileReadAttributes | (deleteAccess ? Delete : 0),
                ShareRead | ShareWrite,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw MapNative(error);
            }
            return handle;
        }

        public static FileIdentity Identity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw MapNative(Marshal.GetLastWin32Error());
            var attributes = (FileAttributes)information.FileAttributes;
            return new(
                information.VolumeSerialNumber,
                information.FileIndexHigh,
                information.FileIndexLow,
                ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow,
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0);
        }

        public static void MarkDelete(SafeFileHandle handle)
        {
            var disposition = new FileDispositionInformationEx
            {
                Flags = FileDispositionDelete | FileDispositionPosixSemantics | FileDispositionIgnoreReadonlyAttribute
            };
            if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref disposition, Marshal.SizeOf<FileDispositionInformationEx>()))
                throw MapNative(Marshal.GetLastWin32Error());
        }

        private static Exception MapNative(int error) => error switch
        {
            2 or 3 => new AgentOpsException("not_found", inner: new Win32Exception(error)),
            5 => new AgentOpsException("access", inner: new Win32Exception(error)),
            32 or 33 => new AgentOpsException("precondition", inner: new Win32Exception(error)),
            _ => new IOException(new Win32Exception(error).Message, new Win32Exception(error))
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformationEx { public uint Flags; }

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
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileDispositionInformationEx fileInformation,
            int bufferSize);
    }
}
