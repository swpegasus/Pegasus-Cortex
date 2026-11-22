using System.Buffers;
using System.Security.Cryptography;

namespace Pegasus.Cortex.LearningExports.IO;

internal sealed record ArtifactPublicationResult(string FinalDirectory, bool AlreadyExisted);

/// <summary>
/// Owns a private same-volume staging directory and publishes it with one directory rename.
/// Path checks reduce accidental traversal; filesystem namespace races remain platform-dependent.
/// </summary>
internal sealed class AtomicArtifactPublisher : IAsyncDisposable
{
    public const string ManifestFileName = "learning-dataset-manifest.v1.json";

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode NonOwnerPermissions =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _stagingRoot;
    private string? _stagingDirectory;
    private bool _published;

    private AtomicArtifactPublisher(
        string stagingRoot,
        string stagingDirectory,
        string finalDirectory,
        string artifactFileName)
    {
        _stagingRoot = stagingRoot;
        _stagingDirectory = stagingDirectory;
        FinalDirectory = finalDirectory;
        ArtifactFileName = artifactFileName;
    }

    public string FinalDirectory { get; }
    public string ArtifactFileName { get; }
    public string ArtifactPath => Path.Combine(RequireStagingDirectory(), ArtifactFileName);
    public string ManifestPath => Path.Combine(RequireStagingDirectory(), ManifestFileName);
    public Exception? CleanupFailure { get; private set; }

    public static AtomicArtifactPublisher Create(
        string outputRoot,
        string approvedOutputRoot,
        string datasetId,
        string buildId,
        string artifactFileName)
    {
        if (!LocalOutputRootPolicy.TryAuthorize(
                outputRoot,
                approvedOutputRoot,
                out var root,
                out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }
        if (!LearningExportPathRules.IsSafeDatasetId(datasetId))
            throw new ArgumentException("The dataset ID is not a portable path segment.", nameof(datasetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        if (!LearningExportPathRules.IsSafeArtifactFileName(artifactFileName))
            throw new ArgumentException("The adapter artifact path must be one safe relative file name.", nameof(artifactFileName));

        EnsureExistingParentsAreNotReparsePoints(root);
        CreatePrivateDirectory(root);
        EnsureNotReparsePoint(root);

        var stagingRoot = Path.Combine(root, ".staging");
        var datasetRoot = Path.Combine(root, datasetId);
        CreatePrivateDirectory(stagingRoot);
        CreatePrivateDirectory(datasetRoot);
        EnsureNotReparsePoint(stagingRoot);
        EnsureNotReparsePoint(datasetRoot);

        var staging = Path.Combine(stagingRoot, $"{datasetId}.{buildId}.{Guid.NewGuid():N}");
        CreatePrivateDirectory(staging);
        EnsureNotReparsePoint(staging);
        var final = Path.Combine(datasetRoot, buildId);
        return new AtomicArtifactPublisher(stagingRoot, staging, final, artifactFileName);
    }

    public FileStream OpenArtifact() => CreatePrivateFile(ArtifactPath);
    public FileStream OpenManifest() => CreatePrivateFile(ManifestPath);

    public async ValueTask<ArtifactPublicationResult> PublishAsync(
        string expectedManifestHash,
        long expectedManifestBytes,
        string expectedArtifactHash,
        long expectedArtifactBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staging = RequireStagingDirectory();
        if (!await PublicationMatchesAsync(
                staging,
                expectedManifestHash,
                expectedManifestBytes,
                expectedArtifactHash,
                expectedArtifactBytes,
                cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The staged publication does not match its expected bounded hashes and lengths.");
        }

        if (Directory.Exists(FinalDirectory))
        {
            if (!await PublicationMatchesAsync(
                    FinalDirectory,
                    expectedManifestHash,
                    expectedManifestBytes,
                    expectedArtifactHash,
                    expectedArtifactBytes,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("An artifact directory with the same deterministic build ID has different contents.");
            }

            return new ArtifactPublicationResult(FinalDirectory, AlreadyExisted: true);
        }

        try
        {
            Directory.Move(staging, FinalDirectory);
            _published = true;
            _stagingDirectory = null;
            return new ArtifactPublicationResult(FinalDirectory, AlreadyExisted: false);
        }
        catch (IOException) when (Directory.Exists(FinalDirectory))
        {
            if (!await PublicationMatchesAsync(
                    FinalDirectory,
                    expectedManifestHash,
                    expectedManifestBytes,
                    expectedArtifactHash,
                    expectedArtifactBytes,
                    cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            return new ArtifactPublicationResult(FinalDirectory, AlreadyExisted: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_published && _stagingDirectory is not null)
                DeleteOwnedStagingDirectory(_stagingDirectory);
            TryDeleteEmptyStagingRoot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Cleanup must never replace the original failure or caller cancellation.
            CleanupFailure = exception;
        }
        finally
        {
            _stagingDirectory = null;
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> PublicationMatchesAsync(
        string directory,
        string expectedManifestHash,
        long expectedManifestBytes,
        string expectedArtifactHash,
        long expectedArtifactBytes,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(directory);
        if (!HasPrivatePermissions(directory, isDirectory: true) || !HasExactEntries(directory))
            return false;

        var manifestPath = Path.Combine(directory, ManifestFileName);
        var artifactPath = Path.Combine(directory, ArtifactFileName);
        EnsureNotReparsePoint(manifestPath);
        EnsureNotReparsePoint(artifactPath);
        if (!HasPrivatePermissions(manifestPath, isDirectory: false) ||
            !HasPrivatePermissions(artifactPath, isDirectory: false))
        {
            return false;
        }

        var manifestHash = await ComputeFileHashAsync(
            manifestPath,
            expectedManifestBytes,
            cancellationToken).ConfigureAwait(false);
        var artifactHash = await ComputeFileHashAsync(
            artifactPath,
            expectedArtifactBytes,
            cancellationToken).ConfigureAwait(false);
        return string.Equals(manifestHash, expectedManifestHash, StringComparison.Ordinal) &&
            string.Equals(artifactHash, expectedArtifactHash, StringComparison.Ordinal);
    }

    private bool HasExactEntries(string directory)
    {
        var actualNames = new List<string>(3);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (actualNames.Count == 2)
                return false;
            actualNames.Add(Path.GetFileName(entry));
        }
        actualNames.Sort(StringComparer.Ordinal);
        var expectedNames = new[] { ArtifactFileName, ManifestFileName };
        Array.Sort(expectedNames, StringComparer.Ordinal);
        return actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal);
    }

    private static async ValueTask<string?> ComputeFileHashAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 1)
            return null;
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedLength)
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = expectedLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return null;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0 ||
                input.Length != expectedLength)
            {
                return null;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void DeleteOwnedStagingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var ownedPrefix = Path.GetFullPath(_stagingRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(ownedPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) ||
            string.Equals(fullPath, _stagingRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a path outside the owned staging root.");
        }

        if (Directory.Exists(fullPath))
        {
            EnsureNotReparsePoint(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private void TryDeleteEmptyStagingRoot()
    {
        try
        {
            if (Directory.Exists(_stagingRoot) && !Directory.EnumerateFileSystemEntries(_stagingRoot).Take(1).Any())
                Directory.Delete(_stagingRoot, recursive: false);
        }
        catch (IOException)
        {
            // Another build may create an entry between the bounded check and deletion.
        }
        catch (UnauthorizedAccessException)
        {
            // A unique owned stage cleanup failure is surfaced through CleanupFailure instead.
        }
    }

    private string RequireStagingDirectory() =>
        _stagingDirectory ?? throw new InvalidOperationException("The staging directory is no longer available.");

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFileMode;
        var file = new FileStream(path, options);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, PrivateFileMode);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    private static bool HasPrivatePermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
            return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & NonOwnerPermissions) == 0;
    }

    private static void EnsureExistingParentsAreNotReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists)
                EnsureNotReparsePoint(current.FullName);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Artifact publication through a reparse point is not allowed.");
    }
}

internal static class LearningExportPathRules
{
    private static readonly HashSet<string> WindowsReservedBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    public static bool IsSafeDatasetId(string? value) =>
        IsPortableSegment(value, 128, lowerCaseOnly: true) && value![0] is >= 'a' and <= 'z' or >= '0' and <= '9';

    public static bool IsSafeArtifactFileName(string? path) =>
        IsPortableSegment(path, 256, lowerCaseOnly: false) &&
        !Path.IsPathFullyQualified(path!) &&
        string.Equals(Path.GetFileName(path), path, StringComparison.Ordinal) &&
        !string.Equals(path, AtomicArtifactPublisher.ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsPortableSegment(string? value, int maximumLength, bool lowerCaseOnly)
    {
        if (value is not { Length: > 0 } || value.Length > maximumLength || value.Any(char.IsControl) ||
            value.EndsWith('.') || value.EndsWith(' ') || value.Contains("..", StringComparison.Ordinal) ||
            value[0] is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')) ||
            value.Any(character => character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '.' or '_' or '-')) ||
            (lowerCaseOnly && value.Any(character => character is >= 'A' and <= 'Z')))
        {
            return false;
        }

        var basename = value.Split('.', 2)[0];
        return !WindowsReservedBasenames.Contains(basename);
    }
}
