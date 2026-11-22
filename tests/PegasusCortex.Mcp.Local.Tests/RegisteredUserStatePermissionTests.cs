using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Product;

internal static class RegisteredUserStatePermissionTests
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier OrdinaryUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private sealed class AssertionFailure(string message) : Exception(message);
    private sealed record Entry(bool Directory, byte[] Security, byte[] Content);
    private sealed record Fixture(string Root, CortexProductLayout Product, CortexCurrentUserLayout Current,
        string ContentRoot, SecurityIdentifier Owner);

    // Fixture-only selector: Audit supplies a new isolated child. No registry,
    // installed endpoint, SQLite, service or workspace-journal entry is opened.
    public static async Task<int> RunAsync(string fixtureRoot)
    {
        var passed = 0;
        var catalogStage = "not-entered";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var owner = LocalIdentity.Current();
            Check(owner.Session > 0 && !owner.Elevated && owner.ApplicationPath is not null,
                "Run permission acceptance in the ordinary signed-in user session.");
            var sid = new SecurityIdentifier(owner.Sid);
            var root = AdmitNewRoot(fixtureRoot, sid);
            CreateDirectory(root, sid, includeAdministrators: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            DescriptorAdmissionPreservesInput(sid);
            Pass("trusted security owners and malformed descriptors (in-memory unit checks)", ref passed);
            await SafeSelectedRootAsync(CreateFixture(root, "standard", sid, true), owner, true, deadline.Token);
            Pass("selected root, cache and saved client accept user/SYSTEM/Administrators unchanged", ref passed);
            await SafeSelectedRootAsync(CreateFixture(root, "private", sid, false), owner, false, deadline.Token);
            Pass("safe existing owner-only state remains admitted without resealing", ref passed);
            RejectStartupBeforeWrites(root, sid);
            Pass("ordinary writers and private read leaks refuse before startup writes", ref passed);
            await RejectCacheFilesUnchangedAsync(root, sid, deadline.Token);
            Pass("unsafe existing cache files refuse reads/refresh without provider calls or mutation", ref passed);
            RejectConfigAndConnectionDirectoriesUnchanged(root, sid, owner);
            Pass("unsafe configuration files and connection directories refuse unchanged", ref passed);
            await CatalogParentAdmissionUnchangedAsync(root, sid, deadline.Token, stage => catalogStage = stage);
            Pass("catalog replay preserves owner-only, Modify and inherited parents; foreign writers refuse before writes", ref passed);
            Console.WriteLine($"METRIC registered-user-state-permissions passed={passed} total=7 fixture-only=true sqlite-opened=false");
            return 0;
        }
        catch (Exception error)
        {
            // Credential-bearing fixture operations never contribute exception text to logs.
            var detail = error is AssertionFailure ? error.Message : "operation rejected; inspect current fixture group";
            Console.Error.WriteLine($"FAIL registered-user-state-permissions passed={passed} catalog-stage={catalogStage} cause={error.GetType().Name} detail={detail}");
            return 1;
        }
    }

    private static string AdmitNewRoot(string supplied, SecurityIdentifier owner)
    {
        string? source = null;
        for (var candidate = new DirectoryInfo(AppContext.BaseDirectory); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "src", "PegasusCortex.Mcp.Local", "PegasusCortex.Mcp.Local.csproj")))
            { source = candidate.FullName; break; }
        Check(source is not null, "The source workspace could not be located.");
        var parent = Path.Combine(source!, "artifacts", "validation", "r2-clean-01a1f50b");
        var root = Path.GetFullPath(supplied);
        Check(Path.IsPathFullyQualified(supplied) && StringComparer.OrdinalIgnoreCase.Equals(root, supplied) &&
            root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && root.Length <= 100,
            "Use a new short canonical child of artifacts/validation/r2-clean-01a1f50b.");
        CortexProductLayout.RejectExistingReparseComponents(root);
        Check(!Directory.Exists(root) && !File.Exists(root), "The permission fixture root already exists.");
        _ = CortexProductLayout.ForIsolatedRoots(Path.Combine(root, "program"), Path.Combine(root, "data"), owner.Value);
        return root;
    }

    private static Fixture CreateFixture(string root, string name, SecurityIdentifier owner, bool standard)
    {
        var caseRoot = Path.Combine(root, name);
        CreateDirectory(caseRoot, owner, true);
        var install = Path.Combine(caseRoot, "program");
        var data = Path.Combine(caseRoot, "data");
        CreateDirectory(install, owner, true);
        CreateDirectory(data, owner, true);
        // ProgramData-like container browsing is separate from private leaf access.
        var container = new DirectoryInfo(data).GetAccessControl();
        container.AddAccessRule(new FileSystemAccessRule(OrdinaryUsers, FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(data).SetAccessControl(container);
        var declared = CortexProductLayout.ForIsolatedRoots(install, data, owner.Value);
        var selected = CortexDataLocation.ApplyRegistration(declared, install, data);
        var current = CortexCurrentUserLayout.FromProduct(selected);
        foreach (var path in new[]
                 {
                     selected.UserStateDirectory, Path.Combine(selected.UserStateDirectory, "cortex"),
                     selected.CortexDataDirectory, selected.UserBackupDirectory, selected.UserLogDirectory,
                     selected.UserReceiptDirectory, selected.UserExecutionDirectory, selected.TrayStateDirectory,
                     current.AssetDirectory
                 }) CreateDirectory(path, owner, standard);
        // This existing directory deliberately retains safe inherited permissions.
        Directory.CreateDirectory(selected.UserConfigDirectory);
        var content = Path.Combine(caseRoot, "workspace");
        CreateDirectory(content, owner, true);
        File.WriteAllText(Path.Combine(install, "retained.txt"), "unchanged code-root sentinel");
        return new(caseRoot, selected, current, content, owner);
    }

    private static void DescriptorAdmissionPreservesInput(SecurityIdentifier user)
    {
        foreach (var securityOwner in new[] { user, SystemSid, Administrators })
        {
            FileSystemSecurity[] entries = [DirectorySecurity(securityOwner, user, true), FileSecurity(securityOwner, user, true)];
            foreach (var entry in entries)
            {
                var before = entry.GetSecurityDescriptorBinaryForm();
                CortexUserStateDirectories.RequireWindowsUserStateAccess(entry, user);
                Check(before.SequenceEqual(entry.GetSecurityDescriptorBinaryForm()), "Descriptor admission resealed trusted state.");
            }
        }
        var nullDacl = new DirectorySecurity();
        nullDacl.SetSecurityDescriptorSddlForm($"O:{user.Value}D:NO_ACCESS_CONTROL");
        FileSystemSecurity[] invalid =
        [
            nullDacl,
            DirectorySecurity(new SecurityIdentifier("S-1-5-21-101-202-303-1009"), user, true),
            DirectorySecurity(Administrators, Administrators, true)
        ];
        foreach (var entry in invalid)
        {
            var before = entry.GetSecurityDescriptorBinaryForm();
            ExpectRefusal(() => CortexUserStateDirectories.RequireWindowsUserStateAccess(entry, user));
            Check(before.SequenceEqual(entry.GetSecurityDescriptorBinaryForm()), "Rejected descriptor was changed.");
        }
    }

    private static async Task SafeSelectedRootAsync(Fixture fixture, LocalPeer owner, bool standard, CancellationToken token)
    {
        var beforeStartup = Snapshot(fixture.Root);
        fixture.Current.EnsureCreated();
        CortexUserStateDirectories.EnsureExecution(fixture.Product);
        Unchanged(beforeStartup, fixture.Root);
        Check(!File.Exists(fixture.Product.CortexDatabasePath) && !File.Exists(fixture.Product.LegacyCortexDatabasePath),
            "Directory admission unexpectedly initialized a database.");

        var config = Path.Combine(fixture.Current.ConfigDirectory, "retained.json");
        File.WriteAllText(config, "{\"fixture\":true}");
        SetFileSecurity(config, fixture.Owner, standard);
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(config);
        var clientsPath = Path.Combine(fixture.Current.ConfigDirectory, "connections");
        CreateDirectory(clientsPath, fixture.Owner, standard);
        var registry = new CortexClientRegistry(clientsPath, owner);
        var credential = registry.Register("fixture-cerebral", owner.ApplicationPath!, "native",
            new(["project.permission-fixture", "knowledge.shared"], ["project.permission-fixture"]), DateTimeOffset.UtcNow.AddDays(1));
        var keyPath = Path.Combine(clientsPath, "fixture-cerebral.key");
        CortexClientRegistry.SaveProtectedCredential(keyPath, credential);
        SetFileSecurity(keyPath, fixture.Owner, standard);

        var cache = new CortexPrivateReadCache(fixture.Current.ConfigDirectory);
        var binding = await cache.BindAsync(Request(fixture.ContentRoot), token);
        await cache.InitAsync(binding, token);
        var first = await cache.RefreshAsync(binding, null, _ => ValueTask.FromResult("bounded passive fixture reference"), token);
        Check(first.Snapshot is { Authoritative: false, LoadedRevisionProof: false, CoreStoreIdentity: null },
            "Cache admission changed passive reference semantics.");
        var cacheRoot = Path.Combine(fixture.Current.ConfigDirectory, "rc");
        Check(Directory.Exists(cacheRoot), "The supported cache namespace was not constructed under selected config.");
        SetTreeSecurity(cacheRoot, fixture.Owner, standard);
        var before = Snapshot(fixture.Root);
        fixture.Current.EnsureCreated();
        CortexUserStateDirectories.EnsureExecution(fixture.Product);
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(config);
        var reopened = new CortexClientRegistry(clientsPath, owner);
        var decoded = CortexClientRegistry.ReadProtectedCredential(keyPath);
        var lease = reopened.Authenticate(decoded.ClientId, decoded.KeyId, decoded.ApiKey, "native", owner);
        Check(decoded == credential && reopened.RequireActive(lease).Generation == 1,
            "Permission admission rotated or invalidated an existing saved connection.");
        ExpectRefusal(() => reopened.Authenticate(decoded.ClientId, decoded.KeyId, decoded.ApiKey, "native",
            owner with { Session = owner.Session + 1 }));
        var read = await cache.ReadAsync(binding, token);
        var replay = await cache.InitAsync(binding, token);
        Check(read.SnapshotGeneration == first.SnapshotGeneration && replay.SnapshotGeneration == first.SnapshotGeneration &&
            read.Snapshot?.Content == "bounded passive fixture reference", "Safe existing cache content or generation changed.");
        Unchanged(before, fixture.Root);
        Check(!File.Exists(fixture.Product.CortexDatabasePath) && !File.Exists(fixture.Product.LegacyCortexDatabasePath),
            "Cache or saved connection access opened an operational database.");
    }

    private static void RejectStartupBeforeWrites(string root, SecurityIdentifier owner)
    {
        foreach (var leak in new[] { false, true })
        {
            var fixture = CreateFixture(root, leak ? "root-read" : "root-write", owner, true);
            var path = fixture.Product.UserStateDirectory;
            AddOrdinaryAccess(path, true, leak ? FileSystemRights.ReadAndExecute : FileSystemRights.Modify);
            var before = Snapshot(fixture.Root);
            ExpectRefusal(fixture.Current.EnsureCreated);
            ExpectRefusal(() => CortexUserStateDirectories.EnsureExecution(fixture.Product));
            Unchanged(before, fixture.Root);
        }
    }

    private static async Task RejectCacheFilesUnchangedAsync(string root, SecurityIdentifier owner, CancellationToken token)
    {
        foreach (var leak in new[] { false, true })
        {
            var fixture = CreateFixture(root, leak ? "cache-read" : "cache-write", owner, true);
            var cache = new CortexPrivateReadCache(fixture.Current.ConfigDirectory);
            var binding = await cache.BindAsync(Request(fixture.ContentRoot), token);
            await cache.InitAsync(binding, token);
            var seeded = await cache.RefreshAsync(binding, null, _ => ValueTask.FromResult("last good fixture value"), token);
            var hot = Directory.GetFiles(fixture.Current.ConfigDirectory, "HOT.json", SearchOption.AllDirectories).Single();
            AddOrdinaryAccess(hot, false, leak ? FileSystemRights.Read : FileSystemRights.Modify);
            var before = Snapshot(fixture.Root);
            await ExpectCacheRefusalAsync(() => cache.ReadAsync(binding, token).AsTask());
            var invoked = false;
            await ExpectCacheRefusalAsync(() => cache.RefreshAsync(binding, seeded.SnapshotGeneration,
                _ => { invoked = true; return ValueTask.FromResult("must not be consulted"); }, token).AsTask());
            Check(!invoked, "Unsafe cache admission invoked the fresh-reference provider.");
            Unchanged(before, fixture.Root);
        }
    }

    private static void RejectConfigAndConnectionDirectoriesUnchanged(string root, SecurityIdentifier sid, LocalPeer owner)
    {
        var fixture = CreateFixture(root, "config-refusal", sid, true);
        var config = Path.Combine(fixture.Current.ConfigDirectory, "retained.json");
        File.WriteAllText(config, "{\"fixture\":true}");
        AddOrdinaryAccess(config, false, FileSystemRights.Read);
        var clients = Path.Combine(fixture.Current.ConfigDirectory, "connections");
        CreateDirectory(clients, sid, true);
        AddOrdinaryAccess(clients, true, FileSystemRights.Modify);
        var before = Snapshot(fixture.Root);
        ExpectRefusal(() => CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(config));
        ExpectRefusal(() => { _ = new CortexClientRegistry(clients, owner); });
        Unchanged(before, fixture.Root);
        Check(!File.Exists(Path.Combine(clients, "clients.lock")), "Rejected connection admission created a lock file.");
    }

    private static async Task CatalogParentAdmissionUnchangedAsync(string root, SecurityIdentifier owner, CancellationToken token,
        Action<string> stage)
    {
        var manifest = new CortexFileProjectManifestV2(CortexFileProjectManifestV2.CurrentSchema,
            "permission-fixture", "project.permission-fixture", ["project.permission-fixture", "knowledge.shared"]);
        const string task = "20000000-0000-0000-0000-000000000002";
        var modify = FileSystemRights.Modify | FileSystemRights.Synchronize;
        foreach (var mode in new[] { "owner", "modify", "inherited" })
        {
            // Stage labels contain only these fixed case names and literal actions.
            stage("catalog-" + mode + ":create-fixture");
            var fixture = CreateFixture(root, "catalog-" + mode, owner, true);
            // Produce a real validated catalog before preparing an existing target.
            // Copying its bytes avoids hand-authoring a catalog/hash or making a
            // permissions fixture depend on the separate atomic-write policy.
            var staged = Path.Combine(fixture.Current.ConfigDirectory, "catalog-source", "offline-projects.json");
            stage("catalog-" + mode + ":source-constructor");
            var source = new CortexOfflineProjectCatalog(staged);
            stage("catalog-" + mode + ":source-register");
            var first = await source.RegisterAsync(fixture.ContentRoot, task, manifest, token);
            stage("catalog-" + mode + ":prepare-existing-parent");
            var parent = Path.Combine(fixture.Current.ConfigDirectory, "catalog-existing");
            if (mode == "inherited")
            {
                var container = Path.Combine(fixture.Current.ConfigDirectory, "catalog-parent");
                new DirectoryInfo(container).Create(DirectorySecurity(owner, owner, true, modify));
                parent = Path.Combine(container, "existing");
                Directory.CreateDirectory(parent);
                Check(!new DirectoryInfo(parent).GetAccessControl().AreAccessRulesProtected,
                    "The catalog fixture did not exercise inherited permissions.");
            }
            else
                new DirectoryInfo(parent).Create(DirectorySecurity(owner, owner, mode != "owner",
                    mode == "modify" ? modify : FileSystemRights.FullControl));
            var path = Path.Combine(parent, "offline-projects.json");
            stage("catalog-" + mode + ":copy-existing-catalog");
            File.Copy(staged, path, overwrite: false);
            File.WriteAllText(Path.Combine(parent, "retained.txt"), "existing configuration body");
            stage("catalog-" + mode + ":snapshot-before");
            var before = Snapshot(fixture.Root);
            stage("catalog-" + mode + ":reopen-constructor");
            var reopened = new CortexOfflineProjectCatalog(path);
            stage("catalog-" + mode + ":replay-register");
            var replay = await reopened.RegisterAsync(fixture.ContentRoot, task, manifest, token);
            stage("catalog-" + mode + ":read-back");
            var read = await reopened.ReadAsync(token);
            Check(replay.TaskId == first.TaskId && replay.RootPath == first.RootPath &&
                replay.ReadNamespaces.SequenceEqual(first.ReadNamespaces) && read.Projects.Count == 1,
                "Existing catalog replay changed its task/project registration.");
            stage("catalog-" + mode + ":compare-unchanged");
            Unchanged(before, fixture.Root);
        }
        foreach (var existing in new[] { false, true })
        {
            var name = existing ? "catalog-foreign-old" : "catalog-foreign-new";
            stage(name + ":create-fixture");
            var fixture = CreateFixture(root, name, owner, true);
            var path = Path.Combine(fixture.Current.ConfigDirectory, "offline-projects.json");
            stage(name + ":constructor");
            var catalog = new CortexOfflineProjectCatalog(path);
            if (existing)
            {
                stage(name + ":seed-existing");
                await catalog.RegisterAsync(fixture.ContentRoot, task, manifest, token);
            }
            stage(name + ":prepare-foreign-access");
            File.WriteAllText(Path.Combine(fixture.Current.ConfigDirectory, "retained.txt"), "do not adopt configuration");
            AddOrdinaryAccess(fixture.Current.ConfigDirectory, true, FileSystemRights.Modify);
            var before = Snapshot(fixture.Root);
            stage(name + ":expect-refusal");
            await ExpectRefusalAsync(() => catalog.RegisterAsync(fixture.ContentRoot, task, manifest, token).AsTask());
            stage(name + ":compare-unchanged");
            Unchanged(before, fixture.Root);
            Check(!File.Exists(path + ".lock") && File.Exists(path) == existing,
                "Rejected catalog parent admission created a catalog or lease file.");
        }
    }

    private static CortexPrivateReadCacheRequest Request(string content) => new(content,
        "20000000-0000-0000-0000-000000000002", "permission-fixture", "project.permission-fixture",
        ["project.permission-fixture", "knowledge.shared"]);

    private static DirectorySecurity DirectorySecurity(SecurityIdentifier securityOwner, SecurityIdentifier user, bool standard,
        FileSystemRights userRights = FileSystemRights.FullControl)
    {
        var security = new DirectorySecurity();
        security.SetOwner(securityOwner);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in standard ? new[] { user, SystemSid, Administrators }.Distinct() : new[] { user })
            security.AddAccessRule(new FileSystemAccessRule(sid, sid.Equals(user) ? userRights : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity FileSecurity(SecurityIdentifier securityOwner, SecurityIdentifier user, bool standard)
    {
        var security = new FileSecurity();
        security.SetOwner(securityOwner);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in standard ? new[] { user, SystemSid, Administrators }.Distinct() : new[] { user })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void CreateDirectory(string path, SecurityIdentifier owner, bool includeAdministrators) =>
        new DirectoryInfo(path).Create(DirectorySecurity(owner, owner, includeAdministrators));

    private static void SetFileSecurity(string path, SecurityIdentifier owner, bool standard) =>
        new FileInfo(path).SetAccessControl(FileSecurity(owner, owner, standard));

    private static void SetTreeSecurity(string root, SecurityIdentifier owner, bool standard)
    {
        foreach (var path in new[] { root }.Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories)))
            new DirectoryInfo(path).SetAccessControl(DirectorySecurity(owner, owner, standard));
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) SetFileSecurity(path, owner, standard);
    }

    private static void AddOrdinaryAccess(string path, bool directory, FileSystemRights rights)
    {
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(OrdinaryUsers, rights, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static Dictionary<string, Entry> Snapshot(string root)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)))
        {
            Check(result.Count < 128, "The fixture exceeded its bounded entry count.");
            var directory = Directory.Exists(path);
            FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            Check(directory || new FileInfo(path).Length <= 1024 * 1024, "The fixture file exceeded its size bound.");
            result.Add(Path.GetRelativePath(root, path), new(directory, acl.GetSecurityDescriptorBinaryForm(),
                directory ? [] : File.ReadAllBytes(path)));
        }
        return result;
    }

    private static void Unchanged(Dictionary<string, Entry> before, string root)
    {
        var after = Snapshot(root);
        Check(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var entry) &&
            entry.Directory == pair.Value.Directory && entry.Security.SequenceEqual(pair.Value.Security) &&
            entry.Content.SequenceEqual(pair.Value.Content)), "Admission changed existing fixture entries, permissions or bytes.");
    }

    private static void ExpectRefusal(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new AssertionFailure("Expected a user-state security refusal.");
    }

    private static async Task ExpectCacheRefusalAsync(Func<Task> action)
    {
        try { await action(); }
        catch (CortexPrivateReadCacheException error) when (error.IsSecurityError) { return; }
        throw new AssertionFailure("Expected a private-cache security refusal.");
    }

    private static async Task ExpectRefusalAsync(Func<Task> action)
    {
        try { await action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new AssertionFailure("Expected catalog parent admission to refuse before writes.");
    }

    private static void Pass(string message, ref int passed)
    { passed++; Console.WriteLine($"PASS registered-user-state-permissions {passed}: {message}"); }

    private static void Check(bool condition, string message)
    { if (!condition) throw new AssertionFailure(message); }
}
