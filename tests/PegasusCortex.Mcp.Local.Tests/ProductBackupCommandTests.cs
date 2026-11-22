using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using Pegasus.Cortex.Product;
using System.Security.Principal;

internal static class ProductBackupCommandTests
{
    public static async Task TimestampedCentralBackupIsCanonicalAndCreateOnlyAsync()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "cxbk",
            Guid.NewGuid().ToString("N")[..16]));
        var layout = CreateLayout(Path.Combine(root, "initialized"));
        var emptyLayout = CreateLayout(Path.Combine(root, "uninitialized"));
        var time = new FixedBackupTimeProvider(
            new DateTimeOffset(2026, 8, 13, 12, 34, 56, 123, TimeSpan.Zero).AddTicks(4_567));
        var exact = Encoding.UTF8.GetBytes("backup source\r\nΩ漢字\0exact");
        try
        {
            layout.EnsureCreated();
            await using (var source = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
                             time))
            {
                await source.InitializeAsync().ConfigureAwait(false);
                var written = await source.AppendAsync(new SqliteCortexV2AppendRequest(
                    "project.backup",
                    "knowledge",
                    "backup-record",
                    ExpectedRevision: 0,
                    IdempotencyKey: "backup-record-create",
                    Actor: "os_test",
                    EventType: "create",
                    Title: "Backup record",
                    Tags: ["backup"],
                    Summary: "backup source fixture",
                    ContentUtf8: exact,
                    Searchable: false,
                    AttributesJson: "{}"));
                LocalAssert.Equal(
                    SqliteCortexV2WriteDispositions.Appended,
                    written.Disposition,
                    "The central backup fixture was not retained.");
            }

            var result = await CortexLocalProductCommands.BackupAsync(
                layout,
                time,
                CancellationToken.None);
            LocalAssert.Equal(0, result.ExitCode, $"The product backup failed: {result.Json}");
            using var response = JsonDocument.Parse(result.Json);
            var backupDirectory = response.RootElement.GetProperty("backupDirectory").GetString()!;
            var expectedDirectory = Path.Combine(
                layout.BackupDirectory,
                "cortex-v2-20260813T123456.1234567Z");
            LocalAssert.Equal(
                expectedDirectory,
                backupDirectory,
                "The product backup did not use its deterministic UTC directory.");

            var entries = Directory.EnumerateFileSystemEntries(backupDirectory)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var snapshotPath = Path.Combine(backupDirectory, "cortex-v2.db");
            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            LocalAssert.True(
                entries.SequenceEqual(
                    new[] { snapshotPath, manifestPath }.Order(StringComparer.Ordinal),
                    StringComparer.OrdinalIgnoreCase) &&
                !File.Exists(snapshotPath + "-wal") &&
                !File.Exists(snapshotPath + "-shm"),
                "The timestamped product backup does not contain exactly one consolidated snapshot and manifest.");

            var manifestUtf8 = await File.ReadAllBytesAsync(manifestPath);
            var manifest = CortexCanonicalJson.Deserialize<CortexLocalProductBackupManifest>(manifestUtf8);
            LocalAssert.True(
                manifestUtf8.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(manifest)) &&
                manifestUtf8.Length <= CortexLocalProductCommands.MaximumBackupManifestUtf8Bytes,
                "The product backup manifest is not bounded exact canonical JSON.");
            var snapshotUtf8 = await File.ReadAllBytesAsync(snapshotPath);
            var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(snapshotUtf8));
            LocalAssert.True(
                manifest.Schema == "pegasus.cortex.central-backup-manifest/1.0" &&
                manifest.ProductVersion == CortexLocalProductCommands.Version &&
                manifest.StoreGeneration == 2 &&
                manifest.SchemaVersion == SqliteCortexV2Store.SchemaVersion &&
                manifest.CreatedAtUtc == time.GetUtcNow() &&
                manifest.SnapshotFile == "cortex-v2.db" &&
                manifest.SnapshotBytes == snapshotUtf8.LongLength &&
                manifest.SnapshotSha256 == snapshotHash &&
                manifest.NamespaceRows == 1 &&
                manifest.RevisionRows == 1 &&
                manifest.HeadRows == 1,
                "The product backup manifest does not bind exact version, hash, UTC, generation, and count evidence.");

            await using (var snapshot = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(snapshotPath),
                             time))
            {
                var retained = await snapshot.ReadHeadAsync(
                    "project.backup", "knowledge", "backup-record");
                LocalAssert.True(
                    retained is not null && retained.ContentUtf8.SequenceEqual(exact),
                    "The timestamped online snapshot did not retain exact source content.");
            }

            var replay = await CortexLocalProductCommands.BackupAsync(
                layout,
                time,
                CancellationToken.None);
            LocalAssert.True(
                replay.ExitCode == 1 &&
                (await File.ReadAllBytesAsync(snapshotPath)).SequenceEqual(snapshotUtf8) &&
                (await File.ReadAllBytesAsync(manifestPath)).SequenceEqual(manifestUtf8),
                "A repeated timestamped backup was not create-only or changed the prior backup.");

            var uninitialized = await CortexLocalProductCommands.BackupAsync(
                emptyLayout,
                time,
                CancellationToken.None);
            using var uninitializedJson = JsonDocument.Parse(uninitialized.Json);
            LocalAssert.True(
                uninitialized.ExitCode == 1 &&
                uninitializedJson.RootElement.GetProperty("state").GetString() == "uninitialized" &&
                !File.Exists(emptyLayout.V2DatabasePath) &&
                !Directory.Exists(emptyLayout.BackupDirectory),
                "An uninitialized product backup did not fail clearly without creating central state.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static CortexCurrentUserLayout CreateLayout(string root)
    {
        var canonical = Path.GetFullPath(root);
        using var identity = WindowsIdentity.GetCurrent();
        var product = CortexProductLayout.ForIsolatedRoots(
            Path.Combine(canonical, "program"), Path.Combine(canonical, "database"),
            identity.User?.Value ?? throw new InvalidOperationException("The fixture user SID is unavailable."));
        LocalAssert.True(!product.CortexDataDirectory.StartsWith(
                product.InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The runtime backup fixture must exercise independently selected mutable storage.");
        return new(
            product.UserStateDirectory, product.CortexDataDirectory, product.LegacyCortexDatabasePath,
            product.UserConfigDirectory, product.UserBackupDirectory, product.UserLogDirectory, product.UserReceiptDirectory)
        {
            SemanticUserRoot = Path.Combine(product.DataRoot, "users", product.OwnerSid)
        };
    }

    private sealed class FixedBackupTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
