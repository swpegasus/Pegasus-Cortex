using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;

internal static class ObsoleteProgramCleanupTests
{
    private const string Current = "1.2026.09.14.01.55.A";
    private const string Old = "1.2026.08.28.18.10.A";
    private const string Mixed = "1.2026.09.10.12.17.A";
    private const string Legacy = "1.2026.08.15.10.59.A";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Run(string root)
    {
        var metadataAcl = new DirectorySecurity();
        metadataAcl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        metadataAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        metadataAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        metadataAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        CortexObsoleteProgramCleanup.RequireMetadataAclForTest(metadataAcl);
        metadataAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
        var rejectedDeleteChild = false;
        try { CortexObsoleteProgramCleanup.RequireMetadataAclForTest(metadataAcl); }
        catch (UnauthorizedAccessException) { rejectedDeleteChild = true; }
        Require(rejectedDeleteChild, "Metadata admission accepted a parent DELETE_CHILD grant to ordinary users.");
        var basic = Layout(root, "obsolete-basic");
        var first = Put(basic, $"versions/execution/{Old}/host/old.dll", "old immutable one");
        var second = Put(basic, $"versions/semantics/{Old}/old.dll", "old immutable two");
        var changed = Put(basic, $"versions/execution/{Old}/host/changed.dll", "original");
        File.WriteAllText(PathOf(basic, changed.RelativePath), "modified");
        var current = Put(basic, $"versions/execution/{Current}/host/current.dll", "current");
        var mixed = Put(basic, $"versions/semantics/{Mixed}/current.dll", "active mixed semantics");
        var manifest = Put(basic, $"manifests/releases/{Old}/product-catalog.v1.json", "old catalog");
        var activeManifest = Put(basic, $"manifests/releases/{Mixed}/product-catalog.v1.json", "active catalog evidence");
        var missing = new CortexUninstallFileV1($"versions/execution/{Old}/missing.dll", 1, new string('a', 64));
        var unknown = Put(basic, $"versions/execution/{Old}/host/unowned.txt", "unknown");
        var embeddedDatabase = Put(basic, $"versions/execution/{Old}/host/private.db", "database sentinel");
        var data = Put(basic, "state/users/retained.db", "private state sentinel");
        var external = Path.Combine(root, "cleanup-external-data"); Directory.CreateDirectory(external);
        var externalDb = Path.Combine(external, "retained.db"); File.WriteAllText(externalDb, "external sentinel");
        var activeCatalogBytes = File.ReadAllBytes(basic.ActiveCatalogPath);
        basic = CortexProductLayout.ForIsolatedRoots(basic.InstallRoot, external, basic.OwnerSid);
        Directory.CreateDirectory(basic.InstallStateDirectory);
        File.WriteAllBytes(basic.ActiveCatalogPath, activeCatalogBytes);
        WriteInventory(basic, [first, second, changed, current, mixed, manifest, activeManifest, missing]);
        var originalInventory = CortexUninstallInventory.Read(basic);
        using (var databaseLock = File.Open(PathOf(basic, data.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var externalLock = File.Open(externalDb, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = CortexObsoleteProgramCleanup.RemoveForTest(basic);
            Require(result.RemovedFiles == 6 && result.MissingFiles == 1 && result.PreservedChangedFiles == 1 &&
                result.PreservedUnknownEntries >= 2 && result.CompactedInventoryEntries == 7 &&
                !result.DatabaseFilesTouched && !result.BackupCreated,
                "Obsolete cleanup counters or database/no-backup boundary differ.");
        }
        foreach (var file in new[] { changed, unknown, embeddedDatabase, data })
            Require(File.Exists(PathOf(basic, file.RelativePath)), "Changed, unknown, or database file was deleted.");
        foreach (var file in new[] { current, mixed, activeManifest })
            Require(!File.Exists(PathOf(basic, file.RelativePath)), "A release-numbered program artifact survived stable-layout cleanup.");
        Require(File.ReadAllText(externalDb) == "external sentinel" &&
            File.ReadAllText(PathOf(basic, embeddedDatabase.RelativePath)) == "database sentinel", "Database content changed.");
        Require(!Directory.Exists(PathOf(basic, $"versions/semantics/{Old}")) &&
            Directory.Exists(PathOf(basic, $"versions/execution/{Old}")), "Only empty obsolete directories may be removed.");
        var compactedInventory = CortexUninstallInventory.Read(basic, out var compactedBytes);
        var stableLauncher = "PegasusCortex.exe";
        Require(compactedInventory.Files.Select(file => file.RelativePath).Order().SequenceEqual(
                new[] { changed.RelativePath, stableLauncher }.Order()) &&
            compactedInventory.ReleaseVersion == originalInventory.ReleaseVersion &&
            compactedInventory.RegistryKeys.SequenceEqual(originalInventory.RegistryKeys) &&
            compactedInventory.Shortcuts.SequenceEqual(originalInventory.Shortcuts),
            "Compaction discarded retained paths or changed registration/shortcut metadata.");
        var repeated = CortexObsoleteProgramCleanup.RemoveForTest(basic);
        Require(repeated.RemovedFiles == 0 && repeated.MissingFiles == 0 && repeated.CompactedInventoryEntries == 0 &&
            File.ReadAllBytes(Path.Combine(basic.InstallStateDirectory, CortexUninstallInventory.FileName)).AsSpan().SequenceEqual(compactedBytes),
            "Repeated cleanup grew or rewrote the already compacted inventory.");
        Console.WriteLine("PASS obsolete_program_inventory (exact obsolete files, mixed active versions, unknown/changed/locked databases, no backups, compacted idempotence)");

        var locked = Layout(root, "obsolete-lock");
        var before = Put(locked, $"versions/execution/{Old}/a.dll", "first must remain");
        var blocked = Put(locked, $"versions/execution/{Old}/z.dll", "locked must remain");
        WriteInventory(locked, [before, blocked, Put(locked, $"versions/execution/{Current}/current.dll", "current")]);
        var beforeLockInventory = File.ReadAllBytes(Path.Combine(locked.InstallStateDirectory, CortexUninstallInventory.FileName));
        using (var held = File.Open(PathOf(locked, blocked.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Reject(() => CortexObsoleteProgramCleanup.RemoveForTest(locked));
            Require(failure.Stage == "preflight" && failure.InnerException is Win32Exception &&
                failure.Receipt.RemovedFiles == 0 && File.Exists(PathOf(locked, before.RelativePath)) &&
                File.ReadAllBytes(Path.Combine(locked.InstallStateDirectory, CortexUninstallInventory.FileName)).AsSpan().SequenceEqual(beforeLockInventory),
                "A later locked candidate caused earlier file deletion before complete preflight.");
        }
        Require(CortexObsoleteProgramCleanup.RemoveForTest(locked).RemovedFiles == 3, "Released-lock exact retry failed.");
        Console.WriteLine("PASS obsolete_program_locked_preflight (real exclusive native handle checks; no process termination)");
        VerifyCompactionFailure(root);

        var linked = Layout(root, "obsolete-junction");
        var safe = Put(linked, $"versions/execution/{Old}/a.dll", "must remain before link failure");
        var outside = Path.Combine(root, "cleanup-junction-target"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.db"), "outside untouched");
        var link = PathOf(linked, $"versions/execution/{Old}/linked");
        CreateJunction(link, outside);
        try
        {
            WriteInventory(linked, [safe]);
            var failure = Reject(() => CortexObsoleteProgramCleanup.RemoveForTest(linked));
            Require(failure.Receipt.RemovedFiles == 0 && File.Exists(PathOf(linked, safe.RelativePath)) &&
                File.ReadAllText(Path.Combine(outside, "private.db")) == "outside untouched", "Cleanup traversed a junction or partly deleted before rejecting it.");
        }
        finally { Directory.Delete(link, recursive: false); }
        Console.WriteLine("PASS obsolete_program_junction (real isolated NTFS junction rejected before deletion)");

        foreach (var extension in new[] { ".db", ".sqlite-wal", ".sqlite-shm", ".sqlite3-wal", ".sqlite3-shm" })
        {
            var forbidden = Layout(root, "obsolete-sidecar-" + extension.TrimStart('.'));
            var database = Put(forbidden, $"versions/execution/{Old}/private" + extension, "never remove database content");
            WriteInventory(forbidden, [database]);
            var rejected = false;
            try { CortexObsoleteProgramCleanup.RemoveForTest(forbidden); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && File.Exists(PathOf(forbidden, database.RelativePath)), "Database-shaped inventory was admitted for cleanup.");
        }

        VerifyHistorical(root);
    }

    private static void VerifyHistorical(string root)
    {
        var layout = Layout(root, "obsolete-historical");
        WriteInventory(layout, [Put(layout, $"versions/execution/{Current}/current.dll", "current")]);
        var unsigned = $"versions/{Legacy}";
        var failed = $"versions/.failed-{Legacy}-20260815T1815522490089Z";
        WriteHistorical(layout, unsigned, Legacy, flat: true, certificate: null);
        WriteHistorical(layout, failed, Legacy, flat: true, certificate: null);
        var foreign = $"versions/.failed-{Legacy}-20260815T1816423987376Z";
        WriteHistorical(layout, foreign, Legacy, flat: true, certificate: null, entryPoint: "Other.exe");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Isolated Cleanup Test Only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        WriteHistorical(layout, $"manifests/releases/{Old}", Old, flat: false, certificate);
        var invalidSigned = $"versions/1.2026.08.16.22.55.A";
        WriteHistorical(layout, invalidSigned, "1.2026.08.16.22.55.A", flat: true, certificate);
        File.WriteAllBytes(PathOf(layout, invalidSigned + "/package-manifest.p7s"), [1, 2, 3]);
        var result = CortexObsoleteProgramCleanup.RemoveForTest(layout, certificate.Thumbprint);
        Require(!Directory.Exists(PathOf(layout, unsigned)) && !Directory.Exists(PathOf(layout, failed)) &&
            !Directory.Exists(PathOf(layout, $"versions/execution/{Old}")) &&
            !Directory.Exists(PathOf(layout, $"manifests/releases/{Old}")), "Recognized historical flat/failed/signed component files were not removed.");
        Require(Directory.Exists(PathOf(layout, foreign)) && Directory.Exists(PathOf(layout, invalidSigned)) &&
            result.PreservedUnknownEntries >= 2 && !result.BackupCreated, "Unrecognized identity or invalid signed history was adopted.");
        Console.WriteLine("PASS obsolete_program_historical (unsigned ownership-fixture flat/failed manifests, real fixture CMS, malformed signature/foreign preservation)");
    }

    private static void VerifyCompactionFailure(string root)
    {
        var layout = Layout(root, "obsolete-compact-failure");
        var old = Put(layout, $"versions/execution/{Old}/obsolete.dll", "obsolete");
        var current = Put(layout, $"versions/execution/{Current}/current.dll", "active");
        WriteInventory(layout, [old, current]);
        var path = Path.Combine(layout.InstallStateDirectory, CortexUninstallInventory.FileName);
        var original = File.ReadAllBytes(path);
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Reject(() => CortexObsoleteProgramCleanup.RemoveForTest(layout));
            Require(failure.Stage == "compact-inventory" && failure.InnerException is IOException &&
                failure.Receipt.RemovedFiles == 2 && failure.Receipt.CompactedInventoryEntries == 0 &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
                "A failed atomic compaction lost partial evidence or replaced the locked inventory.");
        }
        var retry = CortexObsoleteProgramCleanup.RemoveForTest(layout);
        Require(retry.RemovedFiles == 0 && retry.MissingFiles == 2 && retry.CompactedInventoryEntries == 2 &&
            CortexUninstallInventory.Read(layout).Files.Single().RelativePath == "PegasusCortex.exe",
            "Retry did not compact exactly admitted release-numbered entries missing after partial cleanup.");

        var conflict = Layout(root, "obsolete-compact-conflict");
        var retained = Put(conflict, $"versions/execution/{Current}/current.dll", "active");
        var missing = new CortexUninstallFileV1($"versions/execution/{Old}/absent.dll", 1, new string('a', 64));
        WriteInventory(conflict, [retained, missing]);
        var admitted = CortexUninstallInventory.Read(conflict, out var preimage);
        var conflictPath = Path.Combine(conflict.InstallStateDirectory, CortexUninstallInventory.FileName);
        var concurrent = JsonSerializer.SerializeToUtf8Bytes(admitted with { Shortcuts = ["UnrelatedConcurrentShortcut"] }, Json);
        File.WriteAllBytes(conflictPath, concurrent);
        var rejected = false;
        try { CortexUninstallInventory.CompactAfterCleanup(conflict, admitted, preimage,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { missing.RelativePath }); }
        catch (IOException) { rejected = true; }
        Require(rejected && File.ReadAllBytes(conflictPath).AsSpan().SequenceEqual(concurrent),
            "Compaction overwrote a concurrently modified uninstall inventory.");
        Console.WriteLine("PASS obsolete_program_compaction_failure (exact preimage, locked inventory, partial stage, safe missing-entry retry)");
    }

    private static CortexProductLayout Layout(string root, string name)
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(root, name), "S-1-5-21-100-200-300-1001");
        Directory.CreateDirectory(layout.InstallStateDirectory);
        var roles = new[]
        {
            new CortexProductRoleV1("mcp", "semantics", Current, "bin/semantics/PegasusCortex.exe"),
            new CortexProductRoleV1("host", "execution", Current, "bin/host/PegasusCortex.Host.exe"),
            new CortexProductRoleV1("service", "execution", Current, "bin/service/PegasusCortex.ExecutionService.exe"),
            new CortexProductRoleV1("pao", "execution", Current, "bin/pao.exe"),
            new CortexProductRoleV1("process-host", "execution", Current, "bin/host/Pegasus.AgentOps.ProcessHost.exe"),
            new CortexProductRoleV1("ui-host", "execution", Current, "bin/ui/Pegasus.AgentOps.UiHost.exe"),
            new CortexProductRoleV1("launcher", "launcher", Current, "PegasusCortex.exe"),
            new CortexProductRoleV1("setup", "installer", Current, "installer/PegasusCortex.Setup.exe")
        };
        File.WriteAllText(layout.ActiveCatalogPath, JsonSerializer.Serialize(CortexProductCatalogV1.Create(Current, 2, 1, 1, roles), Json));
        return layout;
    }

    private static void WriteInventory(CortexProductLayout layout, IReadOnlyList<CortexUninstallFileV1> files)
    {
        var stable = Put(layout, "PegasusCortex.exe", "active stable launcher");
        var complete = files.Any(file => file.RelativePath.Equals(stable.RelativePath, StringComparison.OrdinalIgnoreCase))
            ? files : [.. files, stable];
        File.WriteAllText(Path.Combine(layout.InstallStateDirectory, CortexUninstallInventory.FileName),
            JsonSerializer.Serialize(new CortexUninstallInventoryV1(CortexUninstallInventory.Contract, 1, layout.InstallRoot, Current, complete,
                [@"HKLM64\Software\Pegasus\Cortex"], [@"CommonPrograms\Pegasus\Fixture.lnk"]), Json));
    }

    private static CortexUninstallFileV1 Put(CortexProductLayout layout, string relative, string content)
    {
        var path = PathOf(layout, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content); File.WriteAllBytes(path, bytes);
        return new(relative, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void WriteHistorical(CortexProductLayout layout, string slot, string version, bool flat,
        X509Certificate2? certificate, string entryPoint = "PegasusCortex.exe")
    {
        var file = Put(layout, flat ? slot + "/PegasusCortex.exe" : $"versions/execution/{version}/old.dll", "historic immutable program");
        var entries = new List<object> { new { path = flat ? "PegasusCortex.exe" : file.RelativePath, bytes = file.Bytes, sha256 = file.Sha256 } };
        if (!flat) entries.Add(new { path = "PegasusCortex.exe", bytes = 1, sha256 = new string('a', 64) });
        var manifest = new Dictionary<string, object>
        {
            ["schema"] = certificate is null ? "pegasus.cortex.local-operational-package/1.1" : "pegasus.cortex.local-operational-package/1.2",
            ["releaseVersion"] = version, ["role"] = flat ? "application" : "product",
            ["identity"] = new { entryPoint, productVersion = version, runtimeIdentifier = "win-x64", selfContained = true,
                deploymentMode = flat ? "self-contained-apphost" : "unified-one-root-nsis-product" }, ["files"] = entries
        };
        if (certificate is not null) manifest["signing"] = new { schema = "pegasus.package-signature/1.0", signedRole = flat ? "application" : "product",
            signedReleaseVersion = version, publisherThumbprint = certificate.Thumbprint, detachedSignature = "package-manifest.p7s" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var path = PathOf(layout, slot + "/package-manifest.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        if (certificate is not null)
        {
            var cms = new SignedCms(new ContentInfo(bytes), detached: true);
            cms.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") });
            File.WriteAllBytes(PathOf(layout, slot + "/package-manifest.p7s"), cms.Encode());
        }
    }

    private static string PathOf(CortexProductLayout layout, string relative) => Path.Combine(layout.InstallRoot, relative.Replace('/', '\\'));
    private static CortexObsoleteProgramCleanupException Reject(Action action)
    {
        try { action(); } catch (CortexObsoleteProgramCleanupException exception) { return exception; }
        throw new InvalidOperationException("Unsafe or locked obsolete cleanup was accepted.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xa0000003u); writer.Write(checked((ushort)(12 + substitute.Length + print.Length))); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2))); writer.Write(checked((ushort)print.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
        }
        using var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bytes = buffer.ToArray();
        if (!DeviceIoControl(handle, 0x000900a4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
