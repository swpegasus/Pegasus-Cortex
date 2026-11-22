using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class CortexPayloadBudgetTests
{
    private const string Space = "project.budget";
    private static readonly DateTimeOffset Old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Old.AddDays(30);
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-budget-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await ReadBudgetAsync(Path.Combine(root, "read"));
            await RollbackBudgetAsync(Path.Combine(root, "rollback"));
            await CancellationAsync(Path.Combine(root, "cancel"));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("cortex-budget-", StringComparison.Ordinal), "scratch boundary");
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                Check((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "ordinary owned entries");
            Directory.Delete(root, recursive: true);
        }
        Check(!Directory.Exists(root), "all owned fixtures released");
        Console.WriteLine($"PASS payload budgets: {_checks} owned SQLite/file and injected-provider checks; read deferral, atomic rollback after publication, cooperative deadline vs caller cancellation and commit-winning late cancellation; no installed scheduling or hard I/O deadline claim");
    }

    private static async Task ReadBudgetAsync(string root)
    {
        Directory.CreateDirectory(root); var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db")), clock);
        var rows = Enumerable.Range(0, 300).Select(i => Seed("active-" + i, 0, "active payload " + i, false)).ToArray();
        Check((await store.AppendBatchAsync(rows[..256])).Committed && (await store.AppendBatchAsync(rows[256..])).Committed,
            "bounded owned seed batches");
        clock.Now = Cutoff.AddDays(1);
        await ThrowsAsync<InvalidOperationException>(() => store.RotateArchivedPayloadsAsync(new(1, Space, Cutoff)).AsTask(),
            "ordinary public store does not infer offload admission");
        await store.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Upgrade, 2, null);
        var before = await store.GetStorageMetricsAsync();
        var deferred = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 256, 10000, default);
        Check(deferred.State == "deferred_budget" && deferred.Tiering is null && deferred.Plan.HotCompressedBytes is null &&
            deferred.Plan.ScannedHeads is null && deferred.TargetSatisfied is null && deferred.MetadataObservations is >= 0 and <= 256 &&
            !deferred.ImmutableObjectsMayRemain, "exhausted read allowance is unavailable/deferred rather than zero/success");
        var after = await store.GetStorageMetricsAsync();
        Check(before.RevisionRows == after.RevisionRows && before.HotCompressedBlobBytes == after.HotCompressedBlobBytes &&
            after.ColdCompressedBlobBytes == 0, "read deferral mutates nothing");
        var completed = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 65536, 10000, default);
        Check(completed.State == "no_selection" && completed.Plan.ScannedHeads == 256 && completed.Plan.NextCursor is not null &&
            completed.ObservedHotCompressedBytesAfter is > 0 && completed.TargetSatisfied == false,
            "larger deliberate allowance reports truthful active data and candidate continuation");
        var longAge = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Old.AddYears(-100)), 65536, 10000, default);
        Check(longAge.State == "no_selection" && longAge.Tiering is null,
            "valid long archive age preserves a pre-Unix-epoch cutoff without clamping eligibility forward");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff),
            255, 1000, default).AsTask(), "metadata work range closed");
        Check((await store.FullDoctorAsync()).Healthy, "budget observations preserve original store");
    }

    private static async Task RollbackBudgetAsync(string root)
    {
        Directory.CreateDirectory(root); var clock = new Clock(Old);
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db")), clock);
        var head = await SeedHistoryAsync(store);
        clock.Now = Cutoff.AddDays(1);
        await store.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Upgrade, 2, null);
        var before = await store.GetStorageMetricsAsync();
        var deferred = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 256, 10000, default);
        Check(deferred.State == "deferred_budget" && deferred.Tiering is null && deferred.ImmutableObjectsMayRemain &&
            deferred.MetadataObservations is >= 0 and <= 256, "postpublication allowance exhaustion is explicit without a semantic commit");
        var after = await store.GetStorageMetricsAsync();
        Check(before.HotCompressedBlobBytes == after.HotCompressedBlobBytes && after.ColdCompressedBlobBytes == 0 &&
            before.RevisionRows == after.RevisionRows && (await store.ReadHeadAsync(Space, "task", "history"))!.RevisionHash == head!.RevisionHash,
            "original transaction rolled back all hot changes and retained exact head");
        Check(Directory.EnumerateFiles(Path.Combine(root, "cold-v1"), "*.br", SearchOption.AllDirectories).Any(),
            "immutable published objects are retained honestly after rollback");
        var completed = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 65536, 10000, default);
        Check(completed.State == "rotated" && completed.Tiering?.PayloadsChanged == 42 && completed.TargetSatisfied == true,
            "separate deliberate pass can verify existing immutable publications and finish");
        Check((await store.FullDoctorAsync()).Healthy, "rollback and later admitted pass preserve all chains");
        var callerRoot = Path.Combine(root, "caller"); Directory.CreateDirectory(callerRoot);
        var callerClock = new Clock(Old); var callerProvider = new HeldCold { Hold = false };
        await using var callerStore = new SqliteCortexV2Store(new(Path.Combine(callerRoot, "cortex-v2.db")), callerClock, callerProvider);
        _ = await SeedHistoryAsync(callerStore); callerClock.Now = Cutoff.AddDays(1);
        await callerStore.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Upgrade, 2, null);
        using var callerCancel = new CancellationTokenSource(); callerProvider.AfterRelease = callerCancel.Cancel;
        await ThrowsAsync<OperationCanceledException>(() => callerStore.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff),
            256, 10000, callerCancel.Token).AsTask(), "original cancellation during budget rollback wins deferral classification");
        Check(callerCancel.IsCancellationRequested && callerProvider.Leases == 0 &&
            (await callerStore.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0,
            "canceled budget original joins every owned lease and retains rolled-back hot data");
    }

    private static async Task CancellationAsync(string root)
    {
        Directory.CreateDirectory(root); var clock = new Clock(Old);
        var provider = new HeldCold();
        await using var store = new SqliteCortexV2Store(new(Path.Combine(root, "cortex-v2.db")), clock, provider);
        _ = await store.AppendAsync(Seed("deadline", 0, "owned deadline payload"));
        clock.Now = Cutoff.AddDays(1);
        await store.ExecuteTrustedPayloadMaintenanceAsync(SqliteCortexPayloadMaintenanceAction.Upgrade, 2, null);
        var timed = store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 65536, 1000, default).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deferred = await timed.WaitAsync(TimeSpan.FromSeconds(5));
        Check(deferred.State == "deferred_budget" && deferred.Tiering is null &&
            (await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0, "cooperative deadline joins original rollback before deferral");
        using var canceled = new CancellationTokenSource();
        var caller = store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 65536, 10000, canceled.Token).AsTask();
        await provider.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(5)),
            "caller cancellation is not relabeled as an automatic work-budget deferral");
        Check((await store.GetStorageMetricsAsync()).ColdCompressedBlobBytes == 0 && provider.Leases == 0,
            "canceled original has no cold descriptor or retained fake lease");
        provider.Hold = false;
        using var late = new CancellationTokenSource(); provider.AfterRelease = late.Cancel;
        var committed = await store.ExecuteTrustedPayloadRotationAsync(new(1, Space, Cutoff), 65536, 10000, late.Token);
        Check(late.IsCancellationRequested && committed.State == "rotated" && committed.Tiering?.PayloadsChanged == 1 &&
            committed.TargetSatisfied is null && committed.ObservationIssue == "payload_rotation_metrics_unavailable",
            "known commit wins cancellation from later publication-lease cleanup; actual=" + JsonSerializer.Serialize(new
            { canceled = late.IsCancellationRequested, committed.State, changed = committed.Tiering?.PayloadsChanged,
                committed.TargetSatisfied, committed.ObservationIssue, committed.MetadataObservations }));
        Check((await store.FullDoctorAsync()).Healthy && provider.Leases == 0, "late cancellation keeps actual cold bytes and releases owned provider");
    }

    private static SqliteCortexV2AppendRequest Seed(string id, long expected, string body, bool archived = true)
    {
        var attributes = JsonSerializer.Serialize(new
        {
            status = archived ? CortexTaskStatusV2.Archived : CortexTaskStatusV2.Open,
            assignee = (string?)null, claimedBy = (string?)null, dependencies = Array.Empty<string>(), blockedReason = (string?)null,
            createdAtUtc = Old, updatedAtUtc = Old, completedAtUtc = (DateTimeOffset?)Old,
            archivedAtUtc = archived ? (DateTimeOffset?)Old : null,
            provenance = new CortexProvenanceV2(CortexProvenanceSourceV2.Agent, "fixture", null, null, Old),
            checkpoint = (string?)null, evidence = (string?)null, handoff = (string?)null
        }, CortexV2Json.Options);
        return new(Space, "task", id, expected, "seed-" + id + "-" + expected, "fixture", "fixture", id, [], "fixture",
            Encoding.UTF8.GetBytes(body), !archived, attributes);
    }
    private static async Task<SqliteCortexV2WriteResult> SeedHistoryAsync(SqliteCortexV2Store store)
    {
        SqliteCortexV2WriteResult? head = null;
        for (var revision = 0; revision < 42; revision++)
            head = await store.AppendAsync(Seed("history", revision, "unique owned payload " + revision));
        return head!;
    }
    private static void Check(bool condition, string name) { _checks++; if (!condition) throw new InvalidOperationException(name); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class HeldCold : ISqliteCortexColdPayloadStore
    {
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        private int _attempts;
        internal bool Hold = true;
        internal int Leases;
        internal Action? AfterRelease;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<byte[]> ReadAsync(SqliteCortexColdPayload payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(_bytes[payload.CompressedSha256].ToArray()); }
        public async ValueTask<IAsyncDisposable> PublishVerifiedAsync(SqliteCortexColdPayload payload,
            ReadOnlyMemory<byte> compressed, CancellationToken token)
        {
            if (++_attempts == 1) Entered.TrySetResult(); else SecondEntered.TrySetResult();
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            _bytes[payload.CompressedSha256] = compressed.ToArray(); Leases++;
            return new Lease(this);
        }
        private sealed class Lease(HeldCold owner) : IAsyncDisposable
        {
            private bool _disposed;
            public ValueTask DisposeAsync()
            {
                if (!_disposed) { _disposed = true; owner.Leases--; owner.AfterRelease?.Invoke(); }
                return ValueTask.CompletedTask;
            }
        }
    }
}
