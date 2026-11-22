using System.Diagnostics;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.File.Configuration;
using Pegasus.Cortex.Persistence.File.Storage;

namespace Pegasus.Cortex.Persistence.File.Query;

public enum CortexProjectBriefFileWriteDisposition
{
    Written,
    Idempotent
}

public sealed record CortexProjectBriefFileWriteResult(
    CortexProjectBriefFileWriteDisposition Disposition,
    string ContentHash,
    int ByteCount);

/// <summary>
/// Single-snapshot local file provider. It writes only canonical brief bytes to
/// the registered generated path and never crawls or rewrites project metadata.
/// </summary>
public sealed class FileCortexProjectBriefStore : ICortexProjectBriefReadModel
{
    private readonly CortexProjectBriefFileRegistration _registration;
    private readonly TimeProvider _timeProvider;

    public FileCortexProjectBriefStore(
        CortexProjectBriefFileRegistration registration,
        TimeProvider? timeProvider = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string SnapshotPath => _registration.SnapshotPath;

    public async ValueTask<CortexProjectBriefFileWriteResult> WriteSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = CortexCanonicalJson.Serialize(snapshot);
        if (canonical.Length > CortexProjectBriefContract.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "The canonical project brief exceeds 8 KiB.");
        }
        await RequireValidCurrentSnapshotAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);

        using var writeLease = await AcquireWriteLeaseAsync(cancellationToken)
            .ConfigureAwait(false);
        var target = CortexProjectBriefFilePathPolicy.PrepareWriteTarget(
            _registration.RootPath);
        var observed = await ReadStoredStateAsync(
            target,
            requireExists: false,
            cancellationToken).ConfigureAwait(false);
        if (observed is not null)
        {
            if (observed.CanonicalUtf8.AsSpan().SequenceEqual(canonical))
            {
                return new CortexProjectBriefFileWriteResult(
                    CortexProjectBriefFileWriteDisposition.Idempotent,
                    CortexCanonicalJson.Sha256(canonical),
                    canonical.Length);
            }
            if (snapshot.BriefRevision != observed.Snapshot.BriefRevision + 1 ||
                !string.Equals(
                    snapshot.PreviousContextSetId,
                    observed.Snapshot.ContextSetId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The project-brief write does not extend the exact current file snapshot.");
            }
        }
        else if (snapshot.BriefRevision != 1 ||
                 snapshot.PreviousContextSetId is not null)
        {
            throw new InvalidOperationException(
                "A new file-backed project brief must begin at revision one.");
        }

        string? temporaryPath = null;
        try
        {
            temporaryPath = await WriteTemporaryAsync(canonical, cancellationToken)
                .ConfigureAwait(false);

            // Re-read the head immediately before replacement. This is an
            // optimistic CAS against non-cooperating writers; the stable
            // exclusive lock file serializes cooperating provider instances
            // across process and Windows-session boundaries.
            var currentHead = await ReadStoredStateAsync(
                target,
                requireExists: false,
                cancellationToken).ConfigureAwait(false);
            if (!StoredBytesEqual(observed, currentHead))
            {
                throw new InvalidOperationException(
                    "The project-brief head changed before its atomic commit.");
            }

            CommitTemporaryFile(temporaryPath, target, observed is not null);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && System.IO.File.Exists(temporaryPath))
                System.IO.File.Delete(temporaryPath);
        }

        var completedTarget = CortexProjectBriefFilePathPolicy.ValidateReadTarget(
            _registration.RootPath);
        if (!PathEquals(target, completedTarget))
            throw new InvalidDataException("The completed project-brief path changed.");
        var persisted = await ReadExactBytesAsync(
            completedTarget,
            cancellationToken).ConfigureAwait(false);
        if (!persisted.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException("The completed project-brief bytes changed.");

        return new CortexProjectBriefFileWriteResult(
            CortexProjectBriefFileWriteDisposition.Written,
            CortexCanonicalJson.Sha256(canonical),
            canonical.Length);
    }

    public async ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScopeMatches(scope, _registration.Scope))
            return null;
        var target = CortexProjectBriefFilePathPolicy.ValidateReadTarget(
            _registration.RootPath);
        return await ReadStoredAsync(
            target,
            requireExists: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexProjectBriefSnapshot?> FindAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextSetId);
        var current = await FindCurrentAsync(scope, cancellationToken).ConfigureAwait(false);
        return current is not null &&
            string.Equals(current.ContextSetId, contextSetId, StringComparison.Ordinal)
                ? current
                : null;
    }

    private async ValueTask<CortexProjectBriefSnapshot?> ReadStoredAsync(
        string target,
        bool requireExists,
        CancellationToken cancellationToken)
    {
        var stored = await ReadStoredStateAsync(
            target,
            requireExists,
            cancellationToken).ConfigureAwait(false);
        return stored?.Snapshot;
    }

    private async ValueTask<StoredSnapshot?> ReadStoredStateAsync(
        string target,
        bool requireExists,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(target))
        {
            if (requireExists)
                throw new FileNotFoundException("The registered project brief is unavailable.", target);
            return null;
        }
        var bytes = await ReadExactBytesAsync(target, cancellationToken).ConfigureAwait(false);
        CortexProjectBriefSnapshot snapshot;
        try
        {
            snapshot = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(bytes);
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The file-backed project brief is not contract data.",
                exception);
        }
        var canonical = CortexCanonicalJson.Serialize(snapshot);
        if (!canonical.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException(
                "The file-backed project brief is not exact canonical JSON.");
        }
        await RequireValidCurrentSnapshotAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        return new StoredSnapshot(snapshot, bytes);
    }

    private async ValueTask<byte[]> ReadExactBytesAsync(
        string target,
        CancellationToken cancellationToken)
    {
        var validatedTarget = CortexProjectBriefFilePathPolicy.ValidateReadTarget(
            _registration.RootPath);
        if (!PathEquals(target, validatedTarget))
            throw new InvalidDataException("The project-brief path changed before read.");
        await using var stream = new FileStream(
            validatedTarget,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > CortexProjectBriefContract.MaximumUtf8Bytes)
            throw new InvalidDataException("The file-backed project brief has an invalid byte length.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var completedTarget = CortexProjectBriefFilePathPolicy.ValidateReadTarget(
            _registration.RootPath);
        if (!PathEquals(validatedTarget, completedTarget))
            throw new InvalidDataException("The project-brief path changed during read.");
        return bytes;
    }

    private async ValueTask RequireValidCurrentSnapshotAsync(
        CortexProjectBriefSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var validation = await new CortexProjectBriefService(
                new InMemoryCortexProjectBriefReadModel([snapshot]),
                _timeProvider)
            .ReadAsync(
                new CortexProjectBriefRequest(
                    snapshot.ProjectLinkId,
                    snapshot.AuthoritySetHash),
                _registration.Scope,
                cancellationToken).ConfigureAwait(false);
        if (!validation.Accepted)
        {
            throw new InvalidDataException(
                "The project brief failed its scope, context, canonical, or freshness boundary.");
        }
    }

    private async ValueTask<IDisposable> AcquireWriteLeaseAsync(
        CancellationToken cancellationToken)
    {
        var lockPath = CortexProjectBriefFilePathPolicy.PrepareWriteLockTarget(
            _registration.RootPath);
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                try
                {
                    var completedLockPath =
                        CortexProjectBriefFilePathPolicy.PrepareWriteLockTarget(
                            _registration.RootPath);
                    if (!PathEquals(lockPath, completedLockPath) ||
                        !PathEquals(stream.Name, completedLockPath))
                    {
                        throw new InvalidDataException(
                            "The project-brief lock path changed during acquisition.");
                    }
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (
                IsLockContention(exception) &&
                timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeCode is 32 or 33
            : nativeCode is 11 or 35;
    }

    private async ValueTask<string> WriteTemporaryAsync(
        byte[] canonical,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var token = Guid.NewGuid().ToString("N")[..8];
            var temporaryPath =
                CortexProjectBriefFilePathPolicy.PrepareTemporaryWriteTarget(
                    _registration.RootPath,
                    token);
            FileStream stream;
            try
            {
                stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (System.IO.File.Exists(temporaryPath))
            {
                // A stale or colliding provider-owned temporary name is never
                // overwritten. Try a new bounded random name.
                continue;
            }

            try
            {
                await using (stream)
                {
                    await stream.WriteAsync(canonical, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                return temporaryPath;
            }
            catch
            {
                if (System.IO.File.Exists(temporaryPath))
                    System.IO.File.Delete(temporaryPath);
                throw;
            }
        }
        throw new IOException("A unique project-brief temporary path was unavailable.");
    }

    private static void CommitTemporaryFile(
        string temporaryPath,
        string target,
        bool targetExists)
    {
        if (!targetExists)
        {
            System.IO.File.Move(temporaryPath, target);
            return;
        }

        try
        {
            System.IO.File.Replace(
                temporaryPath,
                target,
                destinationBackupFileName: null);
        }
        catch (PlatformNotSupportedException)
        {
            System.IO.File.Move(temporaryPath, target, overwrite: true);
        }
    }

    private static bool StoredBytesEqual(
        StoredSnapshot? left,
        StoredSnapshot? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.CanonicalUtf8.AsSpan().SequenceEqual(right.CanonicalUtf8);

    private static bool ScopeMatches(
        CortexProjectBriefReadScope candidate,
        CortexProjectBriefReadScope registered) =>
        string.Equals(candidate.TenantId, registered.TenantId, StringComparison.Ordinal) &&
        string.Equals(candidate.ProjectLinkId, registered.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(candidate.ProjectId, registered.ProjectId, StringComparison.Ordinal) &&
        string.Equals(candidate.ProjectNamespace, registered.ProjectNamespace, StringComparison.Ordinal) &&
        string.Equals(candidate.AuthorityId, registered.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(candidate.SubjectId, registered.SubjectId, StringComparison.Ordinal) &&
        string.Equals(candidate.ServiceId, registered.ServiceId, StringComparison.Ordinal) &&
        candidate.PolicyRevision == registered.PolicyRevision;

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record StoredSnapshot(
        CortexProjectBriefSnapshot Snapshot,
        byte[] CanonicalUtf8);

}
