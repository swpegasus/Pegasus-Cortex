using System.Diagnostics;
using System.Reflection;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.File.Configuration;
using Pegasus.Cortex.Persistence.File.Query;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Cortex.Persistence.Sqlite.Storage;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Persistence.File.Tests;

internal static class FileProjectBriefTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly string ProjectLinkId = $"plink_{new string('a', 64)}";

    public static async Task<int> HoldExclusiveLockAsync(
        string lockPath,
        string readyPath)
    {
        await using var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        await System.IO.File.WriteAllTextAsync(
            readyPath,
            "locked",
            CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 2;
    }

    public static async Task CrossProviderCanonicalBytesAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(Path.Combine(projectRoot, ".cortex"));
            var sourcePath = Path.Combine(projectRoot, ".cortex", "STATE.md");
            await System.IO.File.WriteAllTextAsync(
                sourcePath,
                "source-sentinel",
                CancellationToken.None).ConfigureAwait(false);
            var registration = new CortexProjectBriefFileRegistration(
                projectRoot,
                Scope());
            var fileStore = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            var snapshot = Snapshot(1, previous: null, "Cross-provider state.", '1');
            var fileResult = await fileStore.WriteSnapshotAsync(
                snapshot,
                CancellationToken.None).ConfigureAwait(false);

            await using var sqliteRetention = new SqliteCortexRetentionStore(
                new SqliteCortexStoreOptions(
                    Path.Combine(root, "sqlite", "cortex.db")));
            var sqliteStore = new SqliteCortexProjectBriefStore(
                sqliteRetention,
                new FixedTimeProvider(Now));
            var sqliteResult = await sqliteStore.SubmitSnapshotAsync(
                snapshot,
                "file-cross-provider",
                "file-cross-provider-idem-1",
                CancellationToken.None).ConfigureAwait(false);
            var fileBytes = await System.IO.File.ReadAllBytesAsync(
                registration.SnapshotPath,
                CancellationToken.None).ConfigureAwait(false);
            var canonical = CortexCanonicalJson.Serialize(snapshot);

            Require(
                fileResult.Disposition == CortexProjectBriefFileWriteDisposition.Written &&
                fileBytes.AsSpan().SequenceEqual(canonical) &&
                sqliteResult.Disposition == CortexRetentionWriteDispositions.Appended &&
                sqliteResult.Document!.CanonicalUtf8.AsSpan().SequenceEqual(fileBytes),
                "File and SQLite providers did not retain the same canonical bytes.");
            Require(
                await System.IO.File.ReadAllTextAsync(sourcePath).ConfigureAwait(false) ==
                    "source-sentinel",
                "Writing the generated brief changed a source continuity file.");
            Require(
                registration.SnapshotPath == Path.Combine(
                    projectRoot,
                    ".cortex",
                    "generated",
                    "project-brief.json"),
                "The file provider selected an unexpected path.");
            var current = await fileStore.FindCurrentAsync(
                Scope(),
                CancellationToken.None).ConfigureAwait(false);
            Require(current?.ContextSetId == snapshot.ContextSetId,
                "The file provider did not read its exact current snapshot.");
            var wrongProject = await fileStore.FindCurrentAsync(
                Scope() with { ProjectId = "different-project" },
                CancellationToken.None).ConfigureAwait(false);
            Require(
                wrongProject is null,
                "The file provider returned a brief across project identity.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    public static async Task CorruptionAndFreshnessAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(projectRoot);
            var registration = new CortexProjectBriefFileRegistration(projectRoot, Scope());
            var store = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            await store.WriteSnapshotAsync(
                Snapshot(1, null, "Before corruption.", '2'),
                CancellationToken.None).ConfigureAwait(false);
            await System.IO.File.WriteAllBytesAsync(
                registration.SnapshotPath,
                "{}"u8.ToArray(),
                CancellationToken.None).ConfigureAwait(false);
            await ExpectAsync<InvalidDataException>(async () =>
            {
                _ = await store.FindCurrentAsync(
                    Scope(),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var staleRoot = Path.Combine(root, "stale-project");
            Directory.CreateDirectory(staleRoot);
            var staleRegistration = new CortexProjectBriefFileRegistration(
                staleRoot,
                Scope());
            var staleStore = new FileCortexProjectBriefStore(
                staleRegistration,
                new FixedTimeProvider(Now.AddHours(2)));
            await ExpectAsync<InvalidDataException>(async () =>
            {
                _ = await staleStore.WriteSnapshotAsync(
                    Snapshot(1, null, "Expired state.", '3'),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            Require(
                !System.IO.File.Exists(staleRegistration.SnapshotPath),
                "A stale snapshot created a generated brief file.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    public static async Task StaleChainAndSnapshotFallbackAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(projectRoot);
            var registration = new CortexProjectBriefFileRegistration(projectRoot, Scope());
            var store = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            var first = Snapshot(1, null, "First state.", '4');
            var second = Snapshot(2, first.ContextSetId, "Second state.", '5');
            await store.WriteSnapshotAsync(first, CancellationToken.None).ConfigureAwait(false);
            var replay = await store.WriteSnapshotAsync(
                first,
                CancellationToken.None).ConfigureAwait(false);
            Require(
                replay.Disposition == CortexProjectBriefFileWriteDisposition.Idempotent,
                "An exact file snapshot replay was not idempotent.");
            await store.WriteSnapshotAsync(second, CancellationToken.None).ConfigureAwait(false);

            await ExpectAsync<InvalidOperationException>(async () =>
            {
                _ = await store.WriteSnapshotAsync(
                    Snapshot(2, $"ctx_{new string('f', 64)}", "Stale base.", '6'),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var service = new CortexProjectBriefService(
                store,
                new FixedTimeProvider(Now));
            var response = await service.ReadAsync(
                new CortexProjectBriefRequest(
                    ProjectLinkId,
                    second.AuthoritySetHash,
                    first.ContextSetId),
                Scope(),
                CancellationToken.None).ConfigureAwait(false);
            Require(
                response.Accepted &&
                response.Response.Disposition == CortexProjectBriefDisposition.Snapshot &&
                response.Response.BaseContextSetId is null,
                "A file provider without predecessor retention fabricated a delta.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    public static async Task PathLinkAndSourcePreservationAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var filesystemRoot = Path.GetPathRoot(root)!;
            Expect<ArgumentException>(() =>
                _ = new CortexProjectBriefFileRegistration(
                    filesystemRoot,
                    Scope()));
            Expect<ArgumentException>(() =>
                _ = new CortexProjectBriefFileRegistration(
                    Path.Combine(root, "missing", "..", "project"),
                    Scope()));

            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(Path.Combine(projectRoot, ".cortex", "generated"));
            var sourcePath = Path.Combine(projectRoot, "source.txt");
            await System.IO.File.WriteAllTextAsync(
                sourcePath,
                "do-not-rewrite",
                CancellationToken.None).ConfigureAwait(false);
            var registration = new CortexProjectBriefFileRegistration(projectRoot, Scope());
            var outside = Path.Combine(root, "outside.json");
            await System.IO.File.WriteAllTextAsync(
                outside,
                "outside-sentinel",
                CancellationToken.None).ConfigureAwait(false);
            var linkCreated = false;
            try
            {
                System.IO.File.CreateSymbolicLink(registration.SnapshotPath, outside);
                linkCreated = true;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or
                    PlatformNotSupportedException)
            {
                Console.WriteLine(
                    $"SKIP file symlink creation unavailable: {exception.GetType().Name}");
            }

            var store = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            if (linkCreated)
            {
                await ExpectAsync<InvalidDataException>(async () =>
                {
                    _ = await store.WriteSnapshotAsync(
                        Snapshot(1, null, "Linked target.", '7'),
                        CancellationToken.None).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            else
            {
                await store.WriteSnapshotAsync(
                    Snapshot(1, null, "Direct target.", '7'),
                    CancellationToken.None).ConfigureAwait(false);
            }

            Require(
                await System.IO.File.ReadAllTextAsync(sourcePath).ConfigureAwait(false) ==
                    "do-not-rewrite",
                "The file provider rewrote an unrelated source file.");
            Require(
                await System.IO.File.ReadAllTextAsync(outside).ConfigureAwait(false) ==
                    "outside-sentinel",
                "The file provider followed a link outside the registered root.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    public static async Task ConcurrentCommitAndFailurePreservationAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(projectRoot);
            var registration = new CortexProjectBriefFileRegistration(projectRoot, Scope());
            var firstStore = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            var secondStore = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            var first = Snapshot(1, null, "Concurrent base.", '8');
            await firstStore.WriteSnapshotAsync(
                first,
                CancellationToken.None).ConfigureAwait(false);

            var contenderA = Snapshot(
                2,
                first.ContextSetId,
                "Concurrent successor A.",
                '9');
            var contenderB = Snapshot(
                2,
                first.ContextSetId,
                "Concurrent successor B.",
                'a');
            var outcomes = await Task.WhenAll(
                CaptureWriteAsync(firstStore, contenderA),
                CaptureWriteAsync(secondStore, contenderB)).ConfigureAwait(false);
            Require(
                outcomes.Count(outcome => outcome is null) == 1 &&
                outcomes.Count(outcome => outcome is InvalidOperationException) == 1,
                "Divergent concurrent successors were not reduced to one exact winner.");
            var persisted = await System.IO.File.ReadAllBytesAsync(
                registration.SnapshotPath,
                CancellationToken.None).ConfigureAwait(false);
            Require(
                persisted.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(contenderA)) ||
                persisted.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(contenderB)),
                "A divergent concurrent commit persisted mixed or unexpected bytes.");

            if (OperatingSystem.IsWindows())
            {
                var winner = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(persisted);
                var successor = Snapshot(
                    3,
                    winner.ContextSetId,
                    "Commit must fail while replacement is denied.",
                    'b');
                await using (var denyReplacement = new FileStream(
                    registration.SnapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    var failure = await CaptureWriteAsync(
                        firstStore,
                        successor).ConfigureAwait(false);
                    Require(
                        failure is IOException or UnauthorizedAccessException,
                        "The controlled replacement denial did not fail as an I/O commit.");
                }
                var afterFailure = await System.IO.File.ReadAllBytesAsync(
                    registration.SnapshotPath,
                    CancellationToken.None).ConfigureAwait(false);
                Require(
                    afterFailure.AsSpan().SequenceEqual(persisted),
                    "A failed atomic replacement changed the prior project-brief bytes.");
                Require(
                    Directory.GetFiles(
                        Path.GetDirectoryName(registration.SnapshotPath)!,
                        ".pb-*.tmp").Length == 0,
                    "A failed atomic replacement left provider temporary data behind.");
            }
            else
            {
                Console.WriteLine(
                    "SKIP Windows sharing-denial commit failure unavailable on this platform");
            }
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    public static async Task CrossProcessCrashReleasedLockAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var projectRoot = Path.Combine(root, "registered-project");
            Directory.CreateDirectory(projectRoot);
            var registration = new CortexProjectBriefFileRegistration(projectRoot, Scope());
            var store = new FileCortexProjectBriefStore(
                registration,
                new FixedTimeProvider(Now));
            var first = Snapshot(1, null, "Cross-process lock base.", 'd');
            await store.WriteSnapshotAsync(first, CancellationToken.None).ConfigureAwait(false);

            var lockPath = Path.Combine(
                projectRoot,
                ".cortex",
                "generated",
                ".project-brief.lock");
            var readyPath = Path.Combine(root, "lock-ready.txt");
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current process path is unavailable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add("--hold-brief-lock");
            startInfo.ArgumentList.Add(lockPath);
            startInfo.ArgumentList.Add(readyPath);

            using var holder = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The lock holder did not start.");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!System.IO.File.Exists(readyPath) &&
                       !holder.HasExited &&
                       DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                }
                Require(System.IO.File.Exists(readyPath), "The child did not acquire the lock file.");
                Require(!holder.HasExited, "The child released the lock before the contention probe.");

                var successor = Snapshot(
                    2,
                    first.ContextSetId,
                    "Cross-process lock successor.",
                    'e');
                using var cancellation = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(250));
                await ExpectAsync<OperationCanceledException>(() =>
                    store.WriteSnapshotAsync(successor, cancellation.Token).AsTask())
                    .ConfigureAwait(false);

                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync().ConfigureAwait(false);
                var written = await store.WriteSnapshotAsync(
                    successor,
                    CancellationToken.None).ConfigureAwait(false);
                Require(
                    written.Disposition == CortexProjectBriefFileWriteDisposition.Written,
                    "The provider did not recover the lock after process termination.");
                Require(
                    System.IO.File.Exists(lockPath),
                    "The stable lock file was deleted and reopened a split-lock race.");
            }
            finally
            {
                if (!holder.HasExited)
                {
                    holder.Kill(entireProcessTree: true);
                    await holder.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static async Task<Exception?> CaptureWriteAsync(
        FileCortexProjectBriefStore store,
        CortexProjectBriefSnapshot snapshot)
    {
        try
        {
            _ = await store.WriteSnapshotAsync(
                snapshot,
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static CortexProjectBriefSnapshot Snapshot(
        long revision,
        string? previous,
        string currentState,
        char hash)
    {
        var freshness = new CortexProjectBriefFreshness(
            "current",
            Now.AddMinutes(-5),
            Now.AddHours(1),
            "exact-source-revision-and-hash");
        var task = new CortexProjectBriefTask(
            "task-current",
            0,
            "Retain current task",
            "active",
            currentState,
            "Continue from the bounded brief.",
            new CortexProjectBriefGate(
                "not_required",
                AuthorityId: null,
                ApprovalReferenceId: null,
                "No external approval is required for this reported task.",
                GrantsAuthority: false),
            null,
            new CortexProjectBriefTaskSource(
                "pegasus.agent-task-snapshot/1.0",
                "source-task-current",
                $"revision-{revision}",
                new string(hash, 64),
                "artifact://tasks/current"),
            freshness);
        var template = new CortexProjectBriefSnapshot(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            Scope().TenantId,
            ProjectLinkId,
            "project-file",
            Scope().ProjectNamespace,
            $"ctx_{new string('0', 64)}",
            previous,
            revision,
            revision,
            new string(hash, 64),
            "selection-v1",
            task,
            [],
            HasMore: false,
            CheckpointReferences:
            [
                new CortexProjectBriefReference(
                    "checkpoint://accepted/source",
                    new string('c', 64),
                    RecordId: null,
                    RevisionId: null)
            ],
            EvidenceReferences: [],
            Complete: true,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            freshness,
            AuthoritySetHash: new string(hash, 64));
        return template with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(template)
        };
    }

    private static CortexProjectBriefReadScope Scope() => new(
        "tenant-local",
        ProjectLinkId,
        "project-file",
        "project-file-local",
        "registered-file-authority",
        "registered-file-subject",
        "registered-file-service",
        PolicyRevision: 1);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cortex-file-brief-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cortex-file-brief-tests"));
        if (!fullRoot.StartsWith(
                expectedParent + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refused to delete a non-test directory.");
        }
        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ExpectAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
