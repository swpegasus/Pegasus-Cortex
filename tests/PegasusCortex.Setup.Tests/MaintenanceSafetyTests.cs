using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;

internal static class MaintenanceSafetyTests
{
    internal static async Task RunAsync(string root)
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(root, "defaults"), "S-1-5-21-1-2-3-1001");
        CortexProductStateProvisioner.EnsureServiceConfiguration(layout);
        var configuration = Path.Combine(layout.ServiceStateDirectory, "service.v1.json");
        var before = File.ReadAllBytes(configuration);
        CortexProductStateProvisioner.EnsureServiceConfiguration(layout);
        Check(before.SequenceEqual(File.ReadAllBytes(configuration)), "valid defaults were overwritten");
        File.WriteAllText(configuration, "{\"contract\":\"conflicting-owner\"}");
        before = File.ReadAllBytes(configuration);
        Throws<InvalidDataException>(() => CortexProductStateProvisioner.EnsureServiceConfiguration(layout));
        Check(before.SequenceEqual(File.ReadAllBytes(configuration)), "conflicting defaults were replaced");
        CortexWindowsProductRegistration.RequireMatchingLocation(layout.DataRoot, layout.DataRoot);
        Throws<InvalidDataException>(() => CortexWindowsProductRegistration.RequireMatchingLocation("", layout.DataRoot));
        Throws<InvalidDataException>(() => CortexWindowsProductRegistration.RequireMatchingLocation(Path.Combine(root, "other"), layout.DataRoot));

        var maintenance = CortexProductLayout.ForIsolatedRoot(Path.Combine(root, "maintenance"), layout.OwnerSid);
        CortexProductStateProvisioner.EnsureServiceConfiguration(maintenance);
        CortexOptionalMaintenanceDefaults.Ensure(maintenance, allowFresh: true);
        var publicRoot = Path.Combine(maintenance.DataRoot, "execution-maintenance");
        var mode = Path.Combine(publicRoot, "mode.v1.json");
        var marker = Path.Combine(publicRoot, "required.v1");
        var modeBefore = File.ReadAllBytes(mode);
        var markerBefore = File.ReadAllBytes(marker);
        using (var document = JsonDocument.Parse(modeBefore))
            Check(document.RootElement.GetProperty("phase").GetString() == "off", "fresh optional service lacks initial OFF metadata");
        CortexOptionalMaintenanceDefaults.Ensure(maintenance, allowFresh: false);
        Check(modeBefore.SequenceEqual(File.ReadAllBytes(mode)) && markerBefore.SequenceEqual(File.ReadAllBytes(marker)),
            "existing maintenance defaults were rewritten");
        var uncertain = JsonNode.Parse(modeBefore)!.AsObject();
        uncertain["phase"] = "uncertain";
        uncertain["scopeId"] = Guid.NewGuid().ToString("N");
        uncertain["taskId"] = Guid.NewGuid().ToString("D");
        uncertain["ownerSession"] = 1;
        uncertain["serviceInstance"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(mode, uncertain.ToJsonString());
        var uncertainBytes = File.ReadAllBytes(mode);
        CortexOptionalMaintenanceDefaults.Ensure(maintenance, allowFresh: true);
        Check(uncertainBytes.SequenceEqual(File.ReadAllBytes(mode)), "uncertain maintenance was cleared");
        var originalSecurity = new FileInfo(mode).GetAccessControl();
        var denied = new FileSecurity();
        denied.SetSecurityDescriptorBinaryForm(originalSecurity.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        using (var identity = WindowsIdentity.GetCurrent())
            denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            new FileInfo(mode).SetAccessControl(denied);
            Throws<UnauthorizedAccessException>(() => CortexOptionalMaintenanceDefaults.Ensure(maintenance, allowFresh: true));
        }
        finally
        {
            var restore = new FileSecurity();
            restore.SetSecurityDescriptorBinaryForm(originalSecurity.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new FileInfo(mode).SetAccessControl(restore);
        }
        Check(originalSecurity.GetSecurityDescriptorBinaryForm().SequenceEqual(
            new FileInfo(mode).GetAccessControl().GetSecurityDescriptorBinaryForm()), "fixture DACL was not restored exactly");
        Check(uncertainBytes.SequenceEqual(File.ReadAllBytes(mode)), "unreadable maintenance was modified");
        File.Delete(mode);
        Throws<InvalidDataException>(() => CortexOptionalMaintenanceDefaults.Ensure(maintenance, allowFresh: true));
        Check(!File.Exists(mode) && markerBefore.SequenceEqual(File.ReadAllBytes(marker)), "lost required barrier was silently reset");
        var required = CortexProductLayout.ForIsolatedRoot(Path.Combine(root, "required-missing"), layout.OwnerSid);
        CortexProductStateProvisioner.EnsureServiceConfiguration(required);
        Throws<InvalidDataException>(() => CortexOptionalMaintenanceDefaults.Ensure(required, allowFresh: false));
        Check(!Directory.Exists(Path.Combine(required.DataRoot, "execution-maintenance")), "required missing state was initialized");

        var legacy = Path.Combine(root, "legacy");
        var data = Path.Combine(legacy, "data");
        Directory.CreateDirectory(data);
        var original = Path.Combine(root, "writer.db");
        var source = Path.Combine(data, "cortex-v2.db");
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = original, Pooling = false }.ConnectionString))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA user_version=27; " +
                "CREATE TABLE retained(body TEXT NOT NULL); INSERT INTO retained VALUES('committed-wal');";
            command.ExecuteNonQuery();
            // Controlled fixture writer is idle after commit. Preserve its full
            // physical set BEFORE closing/checkpointing the fixture connection.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(original + suffix)) File.Copy(original + suffix, source + suffix);
        }
        Check(File.Exists(source + "-wal"), "fixture did not retain committed WAL");
        var captured = CaptureBytes(source);
        using (var snapshot = CortexQuiescentDatabaseSnapshot.Capture(source))
            Check(ReadBody(snapshot.WorkingPath) == "committed-wal", "working copy lost committed WAL data");
        CheckUnchanged(source, captured);
        using (var writer = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Throws<IOException>(() => { using var unused = CortexQuiescentDatabaseSnapshot.Capture(source); });
        CheckUnchanged(source, captured);

        var installed = CortexProductLayout.ForIsolatedRoot(Path.Combine(root, "migrated"), layout.OwnerSid);
        var plan = CortexStateMigrationPlanV1.Create(installed,
            new CortexLegacyStateRootsV1(legacy, Path.Combine(root, "no-service"), Path.Combine(root, "no-user")));
        var executor = new CortexStateMigrationExecutor();
        await executor.ExecuteAsync(plan);
        Check(ReadBody(installed.CortexDatabasePath) == "committed-wal", "migration lost committed WAL data");
        CheckUnchanged(source, captured);
        var retained = CaptureBytes(installed.CortexDatabasePath);
        await executor.ExecuteAsync(plan);
        CheckUnchanged(installed.CortexDatabasePath, retained);
        CheckUnchanged(source, captured);
        Console.WriteLine("PASS create-only defaults/required-missing refusal (fixture ACL), preserved config and quiescent WAL capture/migration; isolated files/SQLite, no registry/service/installed access");
    }

    private static string? ReadBody(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM retained";
        return command.ExecuteScalar() as string;
    }

    private sealed record Image(byte[] Bytes, byte[] Security);
    private static Dictionary<string, Image> CaptureBytes(string database) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(database + suffix)).ToDictionary(suffix => suffix, suffix =>
            new Image(File.ReadAllBytes(database + suffix), new FileInfo(database + suffix).GetAccessControl()
                .GetSecurityDescriptorBinaryForm()));

    private static void CheckUnchanged(string database, Dictionary<string, Image> expected)
    {
        var actual = CaptureBytes(database);
        Check(actual.Count == expected.Count, "original database companion set changed");
        foreach (var item in expected)
            Check(actual.TryGetValue(item.Key, out var image) && image.Bytes.SequenceEqual(item.Value.Bytes) &&
                image.Security.SequenceEqual(item.Value.Security), "original database bytes or security changed");
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
