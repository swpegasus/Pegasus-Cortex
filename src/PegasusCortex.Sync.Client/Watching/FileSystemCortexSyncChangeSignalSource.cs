using System.Text;
using System.Threading.Channels;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.Watching;

/// <summary>
/// File events are hints only. Every hint is debounced until the bounded authority files have
/// stable size/write-time fingerprints, while a separate 60-second poll guarantees recovery
/// from dropped watcher events.
/// </summary>
public sealed class FileSystemCortexSyncChangeSignalSource : ICortexSyncChangeSignalSource
{
    private const int MaximumWatchedFiles = 64;
    private const int MaximumAuthorityPathSegments = 32;
    private const int MaximumRawRelativePathCharacters = 240;
    private const int MaximumPortableSegmentBytes = 240;
    private const int MaximumCanonicalPathCodeUnits = 240;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string _projectRoot;
    private readonly HashSet<string> _watchedPaths;
    private readonly TimeSpan _stableDebounce;
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private bool _disposed;

    public FileSystemCortexSyncChangeSignalSource(
        string projectRoot,
        IEnumerable<string> relativeAuthorityPaths,
        TimeSpan? stableDebounce = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || projectRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(projectRoot))
        {
            throw new ArgumentException("An absolute project root is required.", nameof(projectRoot));
        }
        try
        {
            _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            if (!string.Equals(projectRoot, _projectRoot, PathComparison) ||
                !HasCanonicalPathBudget(_projectRoot) ||
                OperatingSystem.IsWindows() &&
                projectRoot.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("The project root must be an exact canonical local path.");
            }
            EnsureProjectRootIsSafe();
        }
        catch (Exception exception) when (IsExpectedPathInspectionFailure(exception))
        {
            throw new ArgumentException(
                "The project root must be an inspectable non-reparse directory.",
                nameof(projectRoot),
                exception);
        }
        _stableDebounce = stableDebounce ?? TimeSpan.FromMilliseconds(500);
        if (_stableDebounce < TimeSpan.FromMilliseconds(100) ||
            _stableDebounce > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(stableDebounce));

        var paths = relativeAuthorityPaths?.Take(MaximumWatchedFiles + 1).ToArray() ??
            throw new ArgumentNullException(nameof(relativeAuthorityPaths));
        if (paths.Length is < 1 or > MaximumWatchedFiles)
            throw new ArgumentOutOfRangeException(nameof(relativeAuthorityPaths));
        _watchedPaths = paths.Select(ResolveAuthorityPath).ToHashSet(PathComparer);
        if (_watchedPaths.Count != paths.Length)
            throw new ArgumentException("Authority paths must be unique.", nameof(relativeAuthorityPaths));

        _watcher = new FileSystemWatcher(_projectRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public async ValueTask<CortexSyncWakeReason> WaitAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (safetyPollInterval < TimeSpan.FromSeconds(1) ||
            safetyPollInterval > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(safetyPollInterval),
                "The bounded v1 retry/safety wait must be between 1 and 60 seconds.");
        }

        return await WaitCoreAsync(safetyPollInterval, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<CortexSyncWakeReason> WaitCoreAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (safetyPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(safetyPollInterval));

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _signals.Reader.ReadAsync(raceCancellation.Token).AsTask();
        var pollTask = Task.Delay(safetyPollInterval, raceCancellation.Token);
        var winner = await Task.WhenAny(signalTask, pollTask).ConfigureAwait(false);
        raceCancellation.Cancel();
        try
        {
            // Observe both branches. In particular, a poll winner must not leave a queued
            // channel reader that can consume the next real filesystem signal.
            await Task.WhenAll(signalTask, pollTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Exactly the losing branch is expected to observe the private race cancellation.
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (winner == pollTask)
            return CortexSyncWakeReason.SafetyPoll;
        await DrainAndWaitForStableAsync(cancellationToken).ConfigureAwait(false);
        return CortexSyncWakeReason.FileHint;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _signals.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }

    private async Task DrainAndWaitForStableAsync(CancellationToken cancellationToken)
    {
        while (_signals.Reader.TryRead(out _))
        {
        }
        while (true)
        {
            var before = Snapshot();
            await Task.Delay(_stableDebounce, cancellationToken).ConfigureAwait(false);
            while (_signals.Reader.TryRead(out _))
            {
            }
            var after = Snapshot();
            if (before.SequenceEqual(after))
                return;
        }
    }

    private AuthorityFileFingerprint[] Snapshot() =>
        _watchedPaths.OrderBy(path => path, PathComparer)
            .Select(CaptureFingerprint)
            .ToArray();

    // Focused friend tests use this hook to force replace/delete race probes without relying on
    // timing-sensitive FileSystemWatcher delivery.
    internal void CaptureHintSnapshotForTest() => _ = Snapshot();

    private AuthorityFileFingerprint CaptureFingerprint(string path)
    {
        try
        {
            if (!HasSafeExistingAuthorityComponents(path))
                return MissingFingerprint(path);

            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
                return MissingFingerprint(path);
            var length = file.Length;
            var lastWriteTicks = file.LastWriteTimeUtc.Ticks;

            // A component may have been swapped after the first inspection. Fingerprints are
            // hints only, so discard the observation instead of following or trusting the race.
            return HasSafeExistingAuthorityComponents(path)
                ? new AuthorityFileFingerprint(path, true, length, lastWriteTicks)
                : MissingFingerprint(path);
        }
        catch (Exception exception) when (IsExpectedPathInspectionFailure(exception))
        {
            // Delete, replace, and access races are ordinary watcher hints. The next event or
            // mandatory safety poll will probe again without turning the watch loop into a fault.
            return MissingFingerprint(path);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsWatched(args.FullPath))
            _signals.Writer.TryWrite(true);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsWatched(args.FullPath) || IsWatched(args.OldFullPath))
            _signals.Writer.TryWrite(true);
    }

    private void OnError(object sender, ErrorEventArgs args) =>
        _signals.Writer.TryWrite(true);

    private bool IsWatched(string path)
    {
        try
        {
            return _watchedPaths.Contains(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private string ResolveAuthorityPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > MaximumRawRelativePathCharacters ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Any(char.IsControl))
        {
            throw new ArgumentException("Authority paths must be bounded project-relative paths.", nameof(relativePath));
        }
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Length > MaximumAuthorityPathSegments ||
            segments.Any(segment => !IsPortableWindowsPathSegment(segment)))
            throw new ArgumentException("An authority path contains an unsafe or unbounded segment.", nameof(relativePath));

        var normalizedRelativePath = string.Join(Path.DirectorySeparatorChar, segments);
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(_projectRoot, normalizedRelativePath));
        }
        catch (Exception exception) when (IsExpectedPathInspectionFailure(exception))
        {
            throw new ArgumentException(
                "An authority path cannot be represented canonically.",
                nameof(relativePath),
                exception);
        }
        if (!resolved.StartsWith(_projectRoot + Path.DirectorySeparatorChar, PathComparison) ||
            !HasCanonicalPathBudget(resolved))
        {
            throw new ArgumentException("An authority path escapes the project root or exceeds the path budget.", nameof(relativePath));
        }
        try
        {
            EnsureSafeExistingAuthorityComponents(segments);
        }
        catch (Exception exception) when (IsExpectedPathInspectionFailure(exception))
        {
            throw new ArgumentException(
                "An existing authority-path component cannot be safely inspected.",
                nameof(relativePath),
                exception);
        }
        return resolved;
    }

    private void EnsureProjectRootIsSafe()
    {
        if (!Directory.Exists(_projectRoot))
            throw new DirectoryNotFoundException();
        for (var current = new DirectoryInfo(_projectRoot); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The project root crosses a link or reparse point.");
        }
        var rootAttributes = File.GetAttributes(_projectRoot);
        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The project root is not a regular directory.");
        }
    }

    private void EnsureSafeExistingAuthorityComponents(IReadOnlyList<string> segments)
    {
        var current = _projectRoot;
        for (var index = 0; index < segments.Count; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing paths are valid watcher targets. Every component will be inspected
                // again if it appears before its metadata contributes to a fingerprint.
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Authority paths cannot cross links or reparse points.");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index < segments.Count - 1 && !isDirectory)
                throw new IOException("An intermediate authority-path component is not a directory.");
            if (index == segments.Count - 1 && isDirectory)
                throw new IOException("An authority file path resolves to a directory.");
        }
    }

    private bool HasSafeExistingAuthorityComponents(string path)
    {
        var relative = Path.GetRelativePath(_projectRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        try
        {
            EnsureProjectRootIsSafe();
            EnsureSafeExistingAuthorityComponents(segments);
            if (!File.Exists(path))
                return false;
            EnsureSafeExistingAuthorityComponents(segments);
            return true;
        }
        catch (Exception exception) when (IsExpectedPathInspectionFailure(exception))
        {
            return false;
        }
    }

    private static bool IsPortableWindowsPathSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
        {
            return false;
        }
        if (!HasPortableComponentBudget(segment.AsSpan()))
            return false;

        var basename = segment.Split('.', 2)[0];
        if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var deviceDigit = basename.Length == 4 ? basename[3] : '\0';
        return !(basename.Length == 4 &&
            (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (deviceDigit is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'));
    }

    private static bool IsExpectedPathInspectionFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException;

    private static bool HasCanonicalPathBudget(string canonicalAbsolutePath) =>
        HasCanonicalPathBudget(
            canonicalAbsolutePath,
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    // Friend tests exercise both POSIX and Windows separator rules on every build host so the
    // portable path budget cannot regress behind an OperatingSystem gate.
    internal static bool HasCanonicalPathBudgetForTest(
        string canonicalAbsolutePath,
        char directorySeparator,
        char alternateDirectorySeparator) =>
        HasCanonicalPathBudget(
            canonicalAbsolutePath,
            directorySeparator,
            alternateDirectorySeparator);

    private static bool HasCanonicalPathBudget(
        string canonicalAbsolutePath,
        char directorySeparator,
        char alternateDirectorySeparator)
    {
        if (string.IsNullOrEmpty(canonicalAbsolutePath) ||
            canonicalAbsolutePath.Length > MaximumCanonicalPathCodeUnits)
        {
            return false;
        }

        var componentStart = 0;
        for (var index = 0; index <= canonicalAbsolutePath.Length; index++)
        {
            var atEnd = index == canonicalAbsolutePath.Length;
            if (!atEnd &&
                canonicalAbsolutePath[index] != directorySeparator &&
                canonicalAbsolutePath[index] != alternateDirectorySeparator)
            {
                continue;
            }

            if (index > componentStart &&
                !HasPortableComponentBudget(canonicalAbsolutePath.AsSpan(componentStart, index - componentStart)))
            {
                return false;
            }
            componentStart = index + 1;
        }
        return true;
    }

    private static bool HasPortableComponentBudget(ReadOnlySpan<char> component)
    {
        try
        {
            return StrictUtf8.GetByteCount(component) <= MaximumPortableSegmentBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static AuthorityFileFingerprint MissingFingerprint(string path) =>
        new(path, false, 0, 0);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record AuthorityFileFingerprint(
        string Path,
        bool Exists,
        long Length,
        long LastWriteTicks);
}
