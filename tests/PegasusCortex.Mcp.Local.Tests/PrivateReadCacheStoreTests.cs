using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Offline;

namespace Pegasus.Cortex.Mcp.Local.Tests;

/// <summary>
/// Windows-only isolated temporary filesystem checks. Most identity-change
/// cases inject synthetic content identities; they are unit fixtures, not
/// installed authentication, service, profile, or product acceptance.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrivateReadCacheStoreTests
{
    public static async Task RunAllAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private read-cache fixtures require Windows ACLs.");
        await SideEffectFreeReadsAndPassiveSnapshotsAsync().ConfigureAwait(false);
        await MutableBindingArgumentsRemainStableAsync().ConfigureAwait(false);
        await ConcurrentInitAndRefreshCasAsync().ConfigureAwait(false);
        await DriftFailuresPreserveLastGoodAsync().ConfigureAwait(false);
        await NamespaceAndPrivateIntegrityFailClosedAsync().ConfigureAwait(false);
        await OccupiedScopeAndForgedLockFailClosedAsync().ConfigureAwait(false);
        await LockTimeoutAndCallerCancellationAreDistinctAsync().ConfigureAwait(false);
        await ActualWindowsRootReplacementAsync().ConfigureAwait(false);
    }

    private static async Task SideEffectFreeReadsAndPassiveSnapshotsAsync()
    {
        using var fixture = new Fixture();
        var namespaces = new List<string> { "project.cache-tests", "knowledge.shared" };
        var request = fixture.Request with { ReadNamespaces = namespaces };
        var binding = await fixture.Store.BindAsync(request).ConfigureAwait(false);
        namespaces[0] = "project.changed";
        Check(binding.Request.ReadNamespaces[0] == "project.cache-tests", "Binding retained a mutable namespace list.");
        Check((await fixture.Store.StatusAsync(binding).ConfigureAwait(false)).State == "missing", "Missing cache status differed.");
        Check((await fixture.Store.ReadAsync(binding).ConfigureAwait(false)).Snapshot is null, "Missing cache read invented a snapshot.");
        Check(!Directory.EnumerateFileSystemEntries(fixture.PrivateParent).Any(), "Status/read created cache state.");

        var shared = Path.Combine(fixture.ContentRoot, ".cortex", "offline", "scopes", "untrusted");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "HOT.json"), "shared poison must never be loaded");
        File.WriteAllText(Path.Combine(shared, "candidate.json"), "not a private read cache");
        var receiptPoison = "{\"schema\":\"pegasus.cortex.offline.ingest-receipt/1.0\",\"disposition\":\"created\"}";
        File.WriteAllText(Path.Combine(shared, "receipt.json"), receiptPoison);
        var initialized = await fixture.Store.InitAsync(binding).ConfigureAwait(false);
        Check(initialized.State == "initialized" && initialized.SnapshotGeneration is null, "Init created snapshot state.");
        var calls = 0;
        ValueTask<string> Reader(CancellationToken _) { calls++; return ValueTask.FromResult("same passive reference"); }
        var first = await fixture.Store.RefreshAsync(binding, null, Reader).ConfigureAwait(false);
        var second = await fixture.Store.RefreshAsync(binding, first.SnapshotGeneration, Reader).ConfigureAwait(false);
        Check(calls == 2 && first.SnapshotGeneration != second.SnapshotGeneration, "Fresh read or non-ABA generation failed.");
        Check(second.Snapshot is { State: "passive-stale", Authoritative: false, CoreStoreIdentity: null,
            LoadedRevisionProof: false, Sequence: 2 } && second.Snapshot.PreviousGeneration == first.SnapshotGeneration,
            "Snapshot acquired authority or lost its generation chain.");
        var preview = string.Concat(Enumerable.Repeat("😀\"\\", 500));
        Check(Encoding.UTF8.GetByteCount(preview) <= 4096, "Fixture preview exceeds input bound.");
        var bounded = await fixture.Store.RefreshAsync(binding, second.SnapshotGeneration,
            _ => ValueTask.FromResult(preview)).ConfigureAwait(false);
        Check(bounded.Snapshot is { Truncated: true }, "Total-envelope clipping was not reported.");
        var hot = fixture.FindHot();
        var bytes = File.ReadAllBytes(hot);
        Check(bytes.Length <= 4096, "Total HOT bytes exceeded the fixed bound.");
        _ = new UTF8Encoding(false, true).GetString(bytes);
        Check((await fixture.Store.ReadAsync(binding).ConfigureAwait(false)).SnapshotGeneration == bounded.SnapshotGeneration,
            "Stored generation could not be read back.");
        Check(File.ReadAllText(Path.Combine(shared, "HOT.json")) == "shared poison must never be loaded",
            "A shared spool file changed.");
        Check(File.ReadAllText(Path.Combine(shared, "receipt.json")) == receiptPoison &&
            !bounded.Snapshot!.Content.Contains("ingest-receipt", StringComparison.Ordinal),
            "Shared receipt-shaped input was consumed or altered.");
    }

    private static async Task ConcurrentInitAndRefreshCasAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        var initialized = await Task.WhenAll(fixture.Store.InitAsync(binding).AsTask(), fixture.Store.InitAsync(binding).AsTask())
            .ConfigureAwait(false);
        Check(initialized.All(value => value.SnapshotGeneration is null), "Concurrent init invented a snapshot.");
        var readersEntered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<string> Reader(CancellationToken token)
        {
            if (Interlocked.Increment(ref readersEntered) == 2) release.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return "concurrent passive reference";
        }
        async Task<bool> Refresh()
        {
            try { await fixture.Store.RefreshAsync(binding, null, Reader).ConfigureAwait(false); return true; }
            catch (CortexPrivateReadCacheException error) when (error.Code == "cache_generation_conflict") { return false; }
        }
        var results = await Task.WhenAll(Refresh(), Refresh()).ConfigureAwait(false);
        Check(results.Count(value => value) == 1, "Concurrent refresh did not admit exactly one generation.");
        var current = await fixture.Store.ReadAsync(binding).ConfigureAwait(false);
        var invoked = false;
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.RefreshAsync(binding, null,
            _ => { invoked = true; return ValueTask.FromResult("must not be read"); }).AsTask()).ConfigureAwait(false);
        Check(!invoked && current.SnapshotGeneration is not null, "A stale generation invoked the provider.");
    }

    private static async Task MutableBindingArgumentsRemainStableAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        var mutableNamespaces = binding.Request.ReadNamespaces.ToList();
        var supplied = binding with { Request = binding.Request with { ReadNamespaces = mutableNamespaces } };
        fixture.IdentityReadAction = () => mutableNamespaces[1] = "project.changed";
        Check((await fixture.Store.InitAsync(supplied).ConfigureAwait(false)).State == "initialized",
            "Init did not freeze the public binding argument before identity callbacks.");
        mutableNamespaces[1] = "knowledge.shared";
        Check((await fixture.Store.StatusAsync(supplied).ConfigureAwait(false)).State == "initialized",
            "Status did not freeze the public binding argument.");
        mutableNamespaces[1] = "knowledge.shared";
        Check((await fixture.Store.ReadAsync(supplied).ConfigureAwait(false)).State == "initialized",
            "Read did not freeze the public binding argument.");
        mutableNamespaces[1] = "knowledge.shared";
        Check((await fixture.Store.RefreshAsync(supplied, null, _ => ValueTask.FromResult("fresh reference"))
            .ConfigureAwait(false)).Snapshot is not null, "Refresh did not freeze the public binding argument.");
        Check(mutableNamespaces[1] == "project.changed", "The deterministic mutation seam was not exercised.");
    }

    private static async Task DriftFailuresPreserveLastGoodAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        await fixture.Store.InitAsync(binding).ConfigureAwait(false);
        var first = await fixture.Store.RefreshAsync(binding, null, _ => ValueTask.FromResult("last good")).ConfigureAwait(false);
        var hot = fixture.FindHot();
        var before = File.ReadAllBytes(hot);
        await ThrowsAsync<InvalidOperationException>(() => fixture.Store.RefreshAsync(binding, first.SnapshotGeneration,
            _ => throw new InvalidOperationException("synthetic provider unavailable")).AsTask()).ConfigureAwait(false);
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(hot)), "Provider failure replaced last-good bytes.");
        using var cancellation = new CancellationTokenSource();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Store.RefreshAsync(binding, first.SnapshotGeneration,
            _ => { cancellation.Cancel(); return ValueTask.FromResult("cancelled value"); }, cancellation.Token).AsTask()).ConfigureAwait(false);
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(hot)), "Cancellation replaced last-good bytes.");
        await ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.RefreshAsync(binding, first.SnapshotGeneration,
            _ => throw new UnauthorizedAccessException("synthetic identity rejection")).AsTask()).ConfigureAwait(false);
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(hot)), "Security rejection replaced last-good bytes.");
        File.SetAttributes(hot, File.GetAttributes(hot) | FileAttributes.ReadOnly);
        try
        {
            var denied = await CacheThrowsAsync(() => fixture.Store.RefreshAsync(binding, first.SnapshotGeneration,
                _ => ValueTask.FromResult("must not overwrite read-only cache" )).AsTask()).ConfigureAwait(false);
            Check(denied.IsSecurityError && denied.Code == "cache_private_access_rejected",
                "Private access denial was flattened to availability.");
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(hot)), "Access denial replaced last-good bytes.");
        }
        finally { File.SetAttributes(hot, File.GetAttributes(hot) & ~FileAttributes.ReadOnly); }
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.RefreshAsync(binding, first.SnapshotGeneration,
            _ => { fixture.FileId = new string('2', 32); return ValueTask.FromResult("root changed during read"); }).AsTask()).ConfigureAwait(false);
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(hot)), "Root identity drift replaced last-good bytes.");
        fixture.FileId = new string('1', 32);
        fixture.CallerId = "different-caller";
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.ReadAsync(binding).AsTask()).ConfigureAwait(false);
        fixture.CallerId = "fixture-caller";
        fixture.RootOwner = "S-1-5-18";
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.ReadAsync(binding).AsTask()).ConfigureAwait(false);
    }

    private static async Task NamespaceAndPrivateIntegrityFailClosedAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        await fixture.Store.InitAsync(binding).ConfigureAwait(false);
        var first = await fixture.Store.RefreshAsync(binding, null, _ => ValueTask.FromResult("reference")).ConfigureAwait(false);
        var cortex = Path.Combine(fixture.ContentRoot, ".cortex");
        Directory.CreateDirectory(cortex);
        File.WriteAllText(Path.Combine(cortex, "project.json"),
            "{\"schema\":\"pegasus.cortex.file-project/2.0\",\"projectId\":\"cache-tests\",\"writeNamespace\":\"project.other\",\"readNamespaces\":[\"project.other\"]}");
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.ReadAsync(binding).AsTask()).ConfigureAwait(false);
        File.Delete(Path.Combine(cortex, "project.json"));
        var hot = fixture.FindHot();
        File.WriteAllText(hot, File.ReadAllText(hot).Replace("\"coreStoreIdentity\":null", "\"coreStoreIdentity\":\"invented\"", StringComparison.Ordinal));
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.ReadAsync(binding).AsTask()).ConfigureAwait(false);
        Check(first.SnapshotGeneration is not null, "Fixture never produced a valid initial generation.");

        using var insecure = new Fixture();
        var identity = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var security = new DirectoryInfo(insecure.PrivateParent).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(insecure.PrivateParent).SetAccessControl(security);
        await ThrowsAsync<CortexPrivateReadCacheException>(() => insecure.Store.BindAsync(insecure.Request).AsTask()).ConfigureAwait(false);
    }

    private static async Task ActualWindowsRootReplacementAsync()
    {
        using var fixture = new Fixture();
        var store = new CortexPrivateReadCache(fixture.PrivateParent, new FixedClock());
        var binding = await store.BindAsync(fixture.Request).ConfigureAwait(false);
        Check(binding.Root.FileId.Length == 32 && binding.Root.VolumeId.Length == 16,
            "Windows root identity was not read.");
        await store.InitAsync(binding).ConfigureAwait(false);
        var grants = new DirectoryInfo(fixture.ContentRoot).GetAccessControl();
        grants.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(fixture.ContentRoot).SetAccessControl(grants);
        var beforeAcl = new DirectoryInfo(fixture.ContentRoot).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        await ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() =>
            CortexLocalRegistryPathPolicy.ValidateProjectRoot(fixture.ContentRoot))).ConfigureAwait(false);
        Check((await store.RefreshAsync(binding, null, _ => ValueTask.FromResult("ACL drift is not cache authority"))
            .ConfigureAwait(false)).Snapshot is not null, "Content ACL grant drift became private-cache authority.");
        Check(new DirectoryInfo(fixture.ContentRoot).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == beforeAcl,
            "Private caching rewrote the content-root ACL.");
        Directory.Move(fixture.ContentRoot, fixture.ContentRoot + "-old");
        Directory.CreateDirectory(fixture.ContentRoot);
        var replacement = await store.BindAsync(fixture.Request).ConfigureAwait(false);
        Check(replacement.Root.FileId != binding.Root.FileId, "Fixture replacement reused its directory identity.");
        await ThrowsAsync<CortexPrivateReadCacheException>(() => store.InitAsync(replacement).AsTask()).ConfigureAwait(false);
        await ThrowsAsync<CortexPrivateReadCacheException>(() => store.ReadAsync(binding).AsTask()).ConfigureAwait(false);
    }

    private static async Task OccupiedScopeAndForgedLockFailClosedAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        await fixture.Store.InitAsync(binding).ConfigureAwait(false);
        var manifest = Directory.GetFiles(fixture.PrivateParent, "binding.v1.json", SearchOption.AllDirectories).Single();
        var scope = Path.GetDirectoryName(manifest)!;
        File.Delete(manifest);
        var unknown = Path.Combine(scope, "orphan.tmp");
        File.WriteAllText(unknown, "preserve this unknown file");
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.InitAsync(binding).AsTask()).ConfigureAwait(false);
        Check(File.ReadAllText(unknown) == "preserve this unknown file" && !File.Exists(manifest), "Init adopted an occupied unbound scope.");
        File.Delete(unknown);
        var control = Path.Combine(scope, ".lock");
        File.WriteAllText(control, "forged control bytes");
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.InitAsync(binding).AsTask()).ConfigureAwait(false);
        Check(File.ReadAllText(control) == "forged control bytes", "Forged control input was repaired or removed.");
        File.Delete(control);
        Directory.CreateDirectory(control);
        await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.InitAsync(binding).AsTask()).ConfigureAwait(false);
        Check(Directory.Exists(control), "Invalid control directory was removed.");
        Directory.Delete(control);
        var outside = Path.Combine(fixture.Root, "outside-lock-target.txt");
        File.WriteAllText(outside, "preserve unrelated target");
        var linkCreated = false;
        try { File.CreateSymbolicLink(control, outside); linkCreated = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { Console.WriteLine("SKIP symbolic-link fixture: OS link creation unavailable."); }
        if (linkCreated)
        {
            await ThrowsAsync<CortexPrivateReadCacheException>(() => fixture.Store.InitAsync(binding).AsTask()).ConfigureAwait(false);
            Check(new FileInfo(control).LinkTarget is not null && File.ReadAllText(outside) == "preserve unrelated target",
                "Control-file reparse rejection altered its link or target.");
        }
    }

    private static async Task LockTimeoutAndCallerCancellationAreDistinctAsync()
    {
        using var fixture = new Fixture();
        var binding = await fixture.Store.BindAsync(fixture.Request).ConfigureAwait(false);
        await fixture.Store.InitAsync(binding).ConfigureAwait(false);
        var control = Directory.GetFiles(fixture.PrivateParent, ".lock", SearchOption.AllDirectories).Single();
        using var held = new FileStream(control, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check((await fixture.Store.StatusAsync(binding).ConfigureAwait(false)).State == "busy", "Read-only status missed a busy lease.");
        var fast = fixture.CreateStore(new FastDeadlineClock());
        var expired = await CacheThrowsAsync(() => fast.RefreshAsync(binding, null,
            _ => ValueTask.FromResult("reference")).AsTask()).ConfigureAwait(false);
        Check(!expired.IsSecurityError && expired.Code == "cache_busy_timeout", "Lease timeout resembled caller cancellation/security.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Store.RefreshAsync(binding, null,
            _ => ValueTask.FromResult("reference"), cancellation.Token).AsTask()).ConfigureAwait(false);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task<CortexPrivateReadCacheException> CacheThrowsAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (CortexPrivateReadCacheException error) { return error; }
        throw new InvalidOperationException("Expected cache rejection.");
    }

    private sealed class FixedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero); }

    private sealed class FastDeadlineClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(callback, state, TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var identity = WindowsIdentity.GetCurrent();
            Sid = identity.User?.Value ?? throw new InvalidOperationException("Fixture owner missing.");
            RootOwner = Sid;
            Root = Path.Combine(Path.GetTempPath(), "pc-read-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            PrivateParent = Path.Combine(Root, "private");
            ContentRoot = Path.Combine(Root, "content");
            var security = new DirectorySecurity();
            var sid = new SecurityIdentifier(Sid);
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(PrivateParent).Create(security);
            Directory.CreateDirectory(ContentRoot);
            Request = new(ContentRoot, "20000000-0000-0000-0000-000000000001", "cache-tests", "project.cache-tests",
                ["project.cache-tests", "knowledge.shared"]);
            Store = CreateStore(new FixedClock());
        }
        public string Root { get; }
        public string PrivateParent { get; }
        public string ContentRoot { get; }
        public string Sid { get; }
        public string RootOwner { get; set; }
        public string CallerId { get; set; } = "fixture-caller";
        public string FileId { get; set; } = new('1', 32);
        public Action? IdentityReadAction { get; set; }
        public CortexPrivateReadCacheRequest Request { get; }
        public CortexPrivateReadCache Store { get; }
        public CortexPrivateReadCache CreateStore(TimeProvider clock) => new(PrivateParent, clock,
            path => { IdentityReadAction?.Invoke(); return new(path, new string('1', 16), FileId, RootOwner); },
            () => new(Sid, CallerId));
        public string FindHot() => Directory.GetFiles(PrivateParent, "HOT.json", SearchOption.AllDirectories).Single();
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            Check(full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("pc-read-cache-", StringComparison.Ordinal), "Unsafe fixture cleanup target.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
