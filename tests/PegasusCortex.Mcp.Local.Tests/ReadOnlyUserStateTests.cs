using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Product;

internal static class ReadOnlyUserStateTests
{
    internal static async Task RunAsync(string root, SecurityIdentifier user)
    {
        CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
        var registryDirectory = Path.Combine(root, "registry");
        CortexUserStateDirectories.EnsureCurrentUserDirectory(registryDirectory);
        var registryPath = Path.Combine(registryDirectory, "projects.json");
        const string profile = "readonly-fixture";
        var document = CortexLocalProjectRegistryFile.Create(profile, []);
        await CortexLocalProjectRegistryFile.WriteNewProtectedAsync(registryPath, document, CancellationToken.None);
        var shadowPath = Path.Combine(registryDirectory, CortexShadowConfiguration.FileName);
        File.WriteAllText(shadowPath, "{\"schema\":\"" + CortexShadowConfiguration.Schema + "\",\"enabled\":false}");
        ReadOnlyFile(registryPath, user);
        ReadOnlyFile(shadowPath, user);

        var leavesBefore = Snapshot(registryDirectory);
        var loaded = await CortexLocalProjectRegistry.LoadAsync(registryPath, profile, CancellationToken.None);
        LocalAssert.Equal(document.RegistryHash, loaded.Document.RegistryHash, "Read-only registry did not load its exact sealed body.");
        LocalAssert.Equal("off", CortexShadowConfiguration.ReadOwner(registryDirectory).State,
            "Read-only shadow configuration was not consumed by its reader.");
        ExpectWriterRefusal(registryPath);
        Unchanged(leavesBefore, registryDirectory);

        ReadOnlyDirectory(registryDirectory, user);
        var directoryBefore = Snapshot(registryDirectory);
        _ = await CortexLocalProjectRegistry.LoadAsync(registryPath, profile, CancellationToken.None);
        LocalAssert.Equal("off", CortexShadowConfiguration.ReadOwner(registryDirectory).State,
            "A read-only private parent was rejected by a read-only configuration caller.");
        Unchanged(directoryBefore, registryDirectory);

        var cacheRoot = Path.Combine(root, "cache");
        var contentRoot = Path.Combine(root, "content");
        CortexUserStateDirectories.EnsureCurrentUserDirectory(cacheRoot);
        CortexUserStateDirectories.EnsureCurrentUserDirectory(contentRoot);
        var cache = new CortexPrivateReadCache(cacheRoot);
        var request = new CortexPrivateReadCacheRequest(contentRoot, "readonly-task", "permission-readers",
            "project.permission-readers", ["project.permission-readers", "knowledge.shared"]);
        var binding = await cache.BindAsync(request);
        await cache.InitAsync(binding);
        var first = await cache.RefreshAsync(binding, null, _ => ValueTask.FromResult("retained read-only fixture preview"));
        LocalAssert.True(first.SnapshotGeneration is not null, "Cache fixture did not retain an initial snapshot.");
        foreach (var file in Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories)) ReadOnlyFile(file, user);
        var leafCacheBefore = Snapshot(cacheRoot);
        LocalAssert.Equal(first.SnapshotGeneration!, (await cache.ReadAsync(binding)).SnapshotGeneration!,
            "Cache read rejected a private read-only binding/snapshot/lock leaf.");
        Unchanged(leafCacheBefore, cacheRoot);

        // Isolate the Hot leaf gate: scope and lock remain writable, and the
        // binding is immutable/read-only. A missing Hot preflight must fail here.
        WritableFile(Directory.GetFiles(cacheRoot, ".lock", SearchOption.AllDirectories).Single(), user);
        var leafRefusalBefore = Snapshot(cacheRoot);
        var providerCalls = 0;
        await ExpectNoProviderRefusalAsync("Read-only snapshot leaf reached the fresh provider.");
        Unchanged(leafRefusalBefore, cacheRoot);

        // Isolate directory admission from the Hot and lock file gates.
        WritableFile(Directory.GetFiles(cacheRoot, "HOT.json", SearchOption.AllDirectories).Single(), user);
        foreach (var directory in Directory.GetDirectories(cacheRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)) ReadOnlyDirectory(directory, user);
        ReadOnlyDirectory(cacheRoot, user);
        var cacheBefore = Snapshot(cacheRoot);
        var rebound = await cache.BindAsync(request);
        LocalAssert.Equal(binding.BindingSha256, rebound.BindingSha256, "Read capability changed the exact cache binding.");
        var status = await cache.StatusAsync(binding);
        var read = await cache.ReadAsync(binding);
        LocalAssert.True(status.SnapshotGeneration == first.SnapshotGeneration &&
            read.Snapshot?.Content == "retained read-only fixture preview" && read.Snapshot is
            { Authoritative: false, LoadedRevisionProof: false, CoreStoreIdentity: null },
            "Read-only cache observation changed its generation or passive reference contract.");
        await ExpectNoProviderRefusalAsync("Read-only cache directory reached the fresh provider.");
        Unchanged(cacheBefore, cacheRoot);
        await RecoveryReadOnlyRootAsync(Path.Combine(root, "recovery-reader"), user);

        async Task ExpectNoProviderRefusalAsync(string message)
        {
            providerCalls = 0;
            var writeRefused = false;
            try
            {
                await cache.RefreshAsync(binding, first.SnapshotGeneration, _ =>
                {
                    providerCalls++;
                    return ValueTask.FromResult("must not replace");
                });
            }
            catch (CortexPrivateReadCacheException error) when (error.IsSecurityError) { writeRefused = true; }
            LocalAssert.True(writeRefused && providerCalls == 0, message);
        }
    }

    private static async Task RecoveryReadOnlyRootAsync(string root, SecurityIdentifier user)
    {
        CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
        var template = new CortexLocalRecoveryExportReceipt(1, CortexLocalRecoveryExportContractIds.Receipt,
            CortexLocalRecoveryExportContractIds.Canonicalization, "fixture-reader",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "fixture-link", "fixture-project",
            "project.fixture-project", "manifest.json", new string('0', 64), 2, 1, 2,
            CortexLocalRecoveryExportContractIds.EmptySha256);
        var correctHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(template));
        var receipt = template with { ReceiptHash = (correctHash[0] == '0' ? "1" : "0") + correctHash[1..] };
        var path = Path.Combine(root, "receipt.json");
        File.WriteAllBytes(path, CortexCanonicalJson.Serialize(receipt));
        ReadOnlyFile(path, user);
        ReadOnlyDirectory(root, user);
        var before = Snapshot(root);
        var integrityRejected = false;
        try { await CortexLocalRecoveryExportReader.ReadAsync(root, limits: null, CancellationToken.None); }
        catch (InvalidDataException error) when (error.Message ==
            "The recovery export receipt failed its canonical integrity boundary.") { integrityRejected = true; }
        LocalAssert.True(integrityRejected,
            "Read-only recovery root did not reach the expected receipt-integrity boundary.");
        Unchanged(before, root);
    }

    private static void WritableFile(string path, SecurityIdentifier user)
    {
        var accessOnly = new FileSecurity();
        accessOnly.SetSecurityDescriptorBinaryForm(
            CortexUserStateDirectories.CreateWindowsUserFileSecurity(user).GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);
        try { new FileInfo(path).SetAccessControl(accessOnly); }
        catch (UnauthorizedAccessException error)
        { throw new UnauthorizedAccessException("fixture_stage=writable_file_dacl", error); }
    }

    private static void ExpectWriterRefusal(string file)
    {
        try { CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(file); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Writable admission accepted a read-only private registry file.");
    }

    private static void ReadOnlyFile(string path, SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        // Persist access rules only; existing fixture ownership is not a grant
        // to request WRITE_OWNER while reducing ordinary data permissions.
        var accessOnly = new FileSecurity();
        accessOnly.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        try { new FileInfo(path).SetAccessControl(accessOnly); }
        catch (UnauthorizedAccessException error)
        { throw new UnauthorizedAccessException("fixture_stage=readonly_file_dacl", error); }
    }

    private static void ReadOnlyDirectory(string path, SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var accessOnly = new DirectorySecurity();
        accessOnly.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        try { new DirectoryInfo(path).SetAccessControl(accessOnly); }
        catch (UnauthorizedAccessException error)
        { throw new UnauthorizedAccessException("fixture_stage=readonly_directory_dacl", error); }
    }

    private sealed record Entry(byte[] Security, byte[]? Bytes);

    private static Dictionary<string, Entry> Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root).ToDictionary(path => path, path =>
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            return new Entry(security.GetSecurityDescriptorBinaryForm(), Directory.Exists(path) ? null : File.ReadAllBytes(path));
        }, StringComparer.OrdinalIgnoreCase);

    private static void Unchanged(Dictionary<string, Entry> before, string root)
    {
        var after = Snapshot(root);
        LocalAssert.True(before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(after.Keys),
            "Read admission added or removed a fixture entry.");
        foreach (var (path, entry) in before)
            LocalAssert.True(entry.Security.SequenceEqual(after[path].Security) &&
                (entry.Bytes is null ? after[path].Bytes is null :
                    after[path].Bytes is { } bytes && entry.Bytes.SequenceEqual(bytes)),
                "Read admission or refused write changed permissions or bytes.");
    }
}
