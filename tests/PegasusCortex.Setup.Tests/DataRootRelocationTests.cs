using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;

internal static class DataRootRelocationTests
{
    internal static void Run(string root)
    {
        RobocopyTransferTests.Run(root);
        VerifyStreamingWalk();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var source = CortexProductLayout.ForIsolatedRoots(Path.Combine(root, "program"), Path.Combine(root, "old"), user.Value);
        Directory.CreateDirectory(source.InstallStateDirectory);
        Directory.CreateDirectory(source.UserConfigDirectory);
        Directory.CreateDirectory(source.CortexDataDirectory);
        CortexProductInstallPlanner.PreserveLocations(source);
        File.WriteAllText(Path.Combine(source.InstallStateDirectory, "registration.v1.json"), JsonSerializer.Serialize(new { DataRoot = source.DataRoot }));
        File.WriteAllText(source.ActiveCatalogPath, "{\"fixture\":\"catalog preserved\"}");
        var generation = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(source.UserConfigDirectory, "geometry-shadow.v1.json"), JsonSerializer.Serialize(new
        { generation, researchRoot = Path.Combine(source.UserConfigDirectory, "geometry-shadow", generation), enabled = true }));
        var key = Path.Combine(source.UserConfigDirectory, "connections", "retained.key");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        File.WriteAllBytes(key, [1, 3, 5, 7]); // Opaque fixture data, never a real credential.
        var history = Path.Combine(source.UserConfigDirectory, "history.txt");
        File.WriteAllText(history, "Historical path is passive: " + source.DataRoot);
        for (var index = 0; index < 24; index++)
            File.WriteAllText(Path.Combine(source.UserConfigDirectory, $"ordinary-{index}.txt"), "bulk fixture");
        var publicReader = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var publicMaintenance = Path.Combine(source.DataRoot, "execution-maintenance");
        var publicSecurity = CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user);
        publicSecurity.AddAccessRule(new FileSystemAccessRule(publicReader, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(publicMaintenance).Create(publicSecurity);
        foreach (var name in new[] { "mode.v1.json", "required.v1" })
        {
            var path = Path.Combine(publicMaintenance, name);
            File.WriteAllText(path, name == "mode.v1.json" ? "{\"phase\":\"off\",\"revision\":1}" : "maintenance/1\n");
            var security = new FileInfo(path).GetAccessControl();
            Check(!security.AreAccessRulesProtected && security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Value == publicReader.Value && rule.IsInherited),
                "Fixture did not establish an inherited public reader distinct from the staging principal");
        }
        CreateRetainedWal(root, source.CortexDatabasePath);
        File.WriteAllBytes(source.LegacyCortexDatabasePath, [9, 8, 7]);
        File.WriteAllBytes(Path.Combine(source.CortexDataDirectory, "orphan.sqlite3-shm"), [2, 4, 6]);
        var originals = Directory.EnumerateFiles(source.DataRoot, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(source.DataRoot, path), path =>
            (Bytes: File.ReadAllBytes(path), Security: new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()));

        try
        {
            var copy = CortexDataRootRelocation.Plan(source, Path.Combine(root, "new"));
            var late = Path.Combine(source.UserConfigDirectory, "exit-flush.txt");
            File.WriteAllText(late, "created after planning, before admitted copy");
            originals.Add(Path.GetRelativePath(source.DataRoot, late), (File.ReadAllBytes(late),
                new FileInfo(late).GetAccessControl().GetSecurityDescriptorBinaryForm()));
            Throws<InvalidOperationException>(() => copy.CommitSelection(_ => throw new Exception("Selected incomplete copy")));
            copy.Prepare(provision: PrivateFixture);
            Check(!Directory.Exists(copy.Destination.DataRoot), "Incomplete staging became the selected destination");
            foreach (var item in originals.Where(item => !item.Key.EndsWith("locations.v1.json", StringComparison.OrdinalIgnoreCase)))
                Check(item.Value.Bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(copy.Staging.DataRoot, item.Key))), "Raw copy altered a retained file");
            var activeAssignments = 0; var peakAssignments = 0; var assignments = 0;
            var target = copy.Publish(checkpoint: phase =>
            {
                if (phase == "file-acl-start")
                {
                    var active = Interlocked.Increment(ref activeAssignments);
                    Interlocked.Increment(ref assignments);
                    int previous;
                    do { previous = peakAssignments; if (previous >= active) break; }
                    while (Interlocked.CompareExchange(ref peakAssignments, active, previous) != previous);
                    Thread.Sleep(2); // Exercise the bounded batch/barrier interleaving.
                }
                else if (phase == "file-acl-end") Interlocked.Decrement(ref activeAssignments);
                else if (phase == "directory-acl") Check(Volatile.Read(ref activeAssignments) == 0,
                    "Directory access published before its file assignments joined");
            });
            Check(assignments >= originals.Count && peakAssignments is >= 1 and <= 8 && activeAssignments == 0,
                "Permission batches lost work, exceeded eight workers or escaped their lifetime");
            foreach (var name in new[] { "mode.v1.json", "required.v1" })
            {
                var path = Path.Combine(target.DataRoot, "execution-maintenance", name);
                var security = new FileInfo(path).GetAccessControl();
                Check(!security.AreAccessRulesProtected && security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Value == publicReader.Value &&
                        rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited &&
                        (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute),
                    "Public inherited reader was lost during private-parent publication");
                Check(File.ReadAllBytes(path).SequenceEqual(originals[Path.GetRelativePath(target.DataRoot, path)].Bytes),
                    "Maintenance record contents changed during permission publication");
            }
            Check(copy.CopiedFiles == originals.Count && File.ReadAllBytes(key).SequenceEqual(File.ReadAllBytes(
                Path.Combine(target.UserConfigDirectory, "connections", "retained.key"))), "Connection bytes or copied count changed");
            Check(File.ReadAllText(Path.Combine(target.UserConfigDirectory, "history.txt")) == File.ReadAllText(history), "Historical body was rewritten");
            Check(CortexProductInstallPlanner.ReadEstablishedDataRoot(target) == target.DataRoot, "Published location metadata points at staging");
            using (var registration = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target.InstallStateDirectory, "registration.v1.json"))))
                Check(registration.RootElement.GetProperty("DataRoot").GetString() == target.DataRoot, "Registration metadata was not rebased");
            using (var shadow = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target.UserConfigDirectory, "geometry-shadow.v1.json"))))
                Check(shadow.RootElement.GetProperty("researchRoot").GetString() == Path.Combine(target.UserConfigDirectory,
                    "geometry-shadow", generation), "Known research path was not rebased");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                Check(originals[Path.GetRelativePath(source.DataRoot, source.CortexDatabasePath + suffix)].Bytes.SequenceEqual(
                    File.ReadAllBytes(target.CortexDatabasePath + suffix)), "Database family bytes changed in the raw copy");
            string? selected = null;
            Throws<IOException>(() => copy.CommitSelection(layout => { selected = layout.DataRoot; throw new IOException("lost acknowledgement"); }));
            Check(selected == target.DataRoot && Directory.Exists(target.DataRoot), "Unknown selection acknowledgement caused a rollback");
            using (var working = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.CortexDatabasePath,
                       Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString))
            {
                working.Open();
                using var command = working.CreateCommand(); command.CommandText = "SELECT body FROM retained";
                Check((string?)command.ExecuteScalar() == "committed-wal", "Copied WAL data is unavailable");
            }

            // A damaged/occupied destination is never adopted from old receipts.
            var occupied = Path.Combine(root, "occupied"); Directory.CreateDirectory(Path.Combine(occupied, "install"));
            File.WriteAllText(Path.Combine(occupied, "install", "data-relocation.v1.json"), "{\"complete\":true}");
            Throws<IOException>(() => CortexDataRootRelocation.Plan(source, occupied));
            Check(File.Exists(Path.Combine(occupied, "install", "data-relocation.v1.json")), "Occupied data was removed");

            var locked = CortexDataRootRelocation.Plan(source, Path.Combine(root, "locked-target"));
            using (var writer = new FileStream(source.CortexDatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Throws<IOException>(() => locked.Prepare(provision: PrivateFixture));
            Check(!Directory.Exists(locked.Destination.DataRoot), "Locked source published partial data");

            var cancelled = CortexDataRootRelocation.Plan(source, Path.Combine(root, "cancelled-target"));
            using (var cancellation = new CancellationTokenSource())
                Throws<OperationCanceledException>(() => cancelled.Prepare(cancellation.Token, PrivateFixture,
                    phase => { if (phase == "copy") cancellation.Cancel(); }));
            Check(!Directory.Exists(cancelled.Destination.DataRoot), "Cancelled copy published partial data");

            var redirected = CortexDataRootRelocation.Plan(source, Path.Combine(root, "redirected-target"));
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var link = Path.Combine(redirected.Staging.DataRoot, "users");
            try
            {
                Throws<IOException>(() => redirected.Prepare(provision: PrivateFixture, checkpoint: phase =>
                {
                    if (phase != "copy" || !Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) return;
                    Directory.Delete(link, recursive: true);
                    ObsoleteProgramCleanupTests.CreateJunction(link, outside);
                }));
                Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Redirected staging wrote outside the target");
            }
            finally { if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link); }

            var longEntry = Path.Combine(source.DataRoot, new string('x', 75), new string('y', 65));
            Directory.CreateDirectory(Path.GetDirectoryName(longEntry)!); File.WriteAllText(longEntry, "budget fixture");
            Throws<IOException>(() => CortexDataRootRelocation.Plan(source, Path.Combine(root, new string('z', 85)))
                .Prepare(provision: PrivateFixture));
            File.Delete(longEntry); Directory.Delete(Path.GetDirectoryName(longEntry)!);
            foreach (var item in originals)
            {
                var path = Path.Combine(source.DataRoot, item.Key);
                Check(item.Value.Bytes.SequenceEqual(File.ReadAllBytes(path)) && item.Value.Security.SequenceEqual(
                    new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()), "Original data or permissions changed");
            }
            Console.WriteLine("PASS copy-first full database family/config/opaque connection preservation, known path fixups, commit-last/lost acknowledgement, occupied/locked/cancelled/redirected/long-path refusal; isolated files and SQLite only");
        }
        finally { RestoreFixtureAccess(root, user); }
    }

    private static void PrivateFixture(CortexProductLayout layout)
    {
        CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(layout.DataRoot);
        Directory.CreateDirectory(layout.InstallStateDirectory);
    }

    internal static void VerifyDataParentBoundary()
    {
        DirectorySecurity Safe()
        {
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),
                FileSystemRights.ReadAndExecute | (FileSystemRights)278, AccessControlType.Allow));
            return security;
        }
        CortexDataParentBoundary.RequireAccess(Safe());
        foreach (var right in new[] { FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles,
                     FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership })
        {
            var unsafeAccess = Safe();
            unsafeAccess.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), right, AccessControlType.Allow));
            Throws<UnauthorizedAccessException>(() => CortexDataParentBoundary.RequireAccess(unsafeAccess));
        }
        var foreignOwner = Safe(); foreignOwner.SetOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        Throws<UnauthorizedAccessException>(() => CortexDataParentBoundary.RequireAccess(foreignOwner));
        foreach (var sddl in new[] { "O:BAG:SYD:NO_ACCESS_CONTROL", "O:BAG:SYD:(A;;GA;;;BU)" })
        {
            var unsafeDescriptor = new DirectorySecurity(); unsafeDescriptor.SetSecurityDescriptorSddlForm(sddl);
            Throws<UnauthorizedAccessException>(() => CortexDataParentBoundary.RequireAccess(unsafeDescriptor));
        }
        // This selector runs before the driver's scratch-root creation. Read
        // only real default ancestor attributes/Owner/Access in either token.
        var parent = Path.GetDirectoryName(CortexDataLocation.DefaultDataRoot())!;
        while (true)
        {
            try { _ = File.GetAttributes(parent); break; }
            catch (FileNotFoundException) { parent = Path.GetDirectoryName(parent) ?? throw new DirectoryNotFoundException("The default data ancestor is unavailable."); }
            catch (DirectoryNotFoundException) { parent = Path.GetDirectoryName(parent) ?? throw new DirectoryNotFoundException("The default data ancestor is unavailable."); }
        }
        CortexDataParentBoundary.RequireExistingAncestors(parent);
        Console.WriteLine("PASS data-parent synthetic sibling-create278 admission/replacement refusal and actual default ancestor read-only admission; attributes/Owner/Access only, no filesystem or registry writes");
    }

    private static void VerifyStreamingWalk()
    {
        const int count = 100_005;
        var generated = 0;
        IEnumerable<CortexDataRootRelocation.Entry> Read(string directory)
        {
            if (directory.Length == 0) { yield return new("history", true); yield break; }
            for (var index = 0; index < count; index++)
            {
                generated++;
                yield return new(Path.Combine("history", index.ToString()), false);
            }
        }
        using (var first = CortexDataRootRelocation.WalkEntries("", Read).GetEnumerator())
        {
            Check(generated == 0 && first.MoveNext() && first.Current.Directory && generated == 0,
                "Directory walk eagerly read its descendants");
            Check(first.MoveNext() && generated == 1, "Directory walk buffered the whole history before returning a file");
        }
        generated = 0;
        var seen = 0;
        CortexDataRootRelocation.Entry? last = null;
        foreach (var entry in CortexDataRootRelocation.WalkEntries("", Read, postorder: true)) { seen++; last = entry; }
        Check(generated == count && seen == count + 1 && last is { Directory: true, Relative: "history" },
            "Streaming traversal truncated accumulated history or published directory access before its children");
    }

    private static void RestoreFixtureAccess(string root, SecurityIdentifier user)
    {
        // Exact task-owned fixture cleanup only. Publication deliberately tests
        // service-private ACLs, so the fixture owner restores its own DACLs.
        var directoryAccess = new DirectorySecurity();
        directoryAccess.SetSecurityDescriptorBinaryForm(
            CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user).GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);
        new DirectoryInfo(root).SetAccessControl(directoryAccess);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup encountered a reparse point");
            if ((attributes & FileAttributes.Directory) != 0) RestoreFixtureAccess(path, user);
            else
            {
                var fileAccess = new FileSecurity();
                fileAccess.SetSecurityDescriptorBinaryForm(
                    CortexUserStateDirectories.CreateWindowsUserFileSecurity(user).GetSecurityDescriptorBinaryForm(),
                    AccessControlSections.Access);
                new FileInfo(path).SetAccessControl(fileAccess);
            }
        }
    }

    private static void CreateRetainedWal(string root, string destination)
    {
        var writerPath = Path.Combine(root, "fixture-writer.db");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = writerPath, Pooling = false }.ConnectionString);
        writer.Open(); using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE retained(body TEXT); INSERT INTO retained VALUES('committed-wal');";
        command.ExecuteNonQuery();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Copy(writerPath + suffix, destination + suffix);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
