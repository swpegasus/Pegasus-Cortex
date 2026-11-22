using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class ProductKnowledgeSpaceMigrationTests
{
    public static async Task BackupFirstConversionPreservesLegacyAndProjectStateAsync()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "cortex-product-knowledge-migration-tests",
            Guid.NewGuid().ToString("N")));
        var layout = CreateLayout(root);
        var time = new FixedMigrationTimeProvider(
            new DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.Zero));
        const string exact = "Use bounded contention evidence.\r\nΩ\tExact\0body.";
        try
        {
            layout.EnsureCreated();
            var lowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
                time);
            await using (var operational = new SqliteCortexV2OperationalStore(
                             lowLevel,
                             "migration-test"))
            {
                await lowLevel.InitializeAsync().ConfigureAwait(false);
                var provenance = new CortexProvenanceInputV2(
                    CortexProvenanceSourceV2.Imported,
                    "legacy-fixture",
                    "fixture");
                async Task SeedAsync(string ns, string id, string title, string content)
                {
                    var command = new CortexKnowledgeCommandV2(
                        ns,
                        CortexKnowledgeActionV2.Create,
                        id,
                        null,
                        $"seed-{ns}-{id}",
                        CortexKnowledgeKindV2.Tenet,
                        CortexKnowledgeTierV2.Verified,
                        title,
                        content,
                        ["migration", "exact"],
                        provenance,
                        null)
                    {
                        EffectiveProvenance = new(
                            CortexProvenanceSourceV2.Imported,
                            "os_fixture",
                            "legacy-fixture",
                            "fixture",
                            time.GetUtcNow())
                    };
                    _ = await operational.ExecuteKnowledgeAsync(command, default)
                        .ConfigureAwait(false);
                }

                await SeedAsync(
                    CortexKnowledgeSpaces.LegacySharedProgramming,
                    "legacy-tenet",
                    "Legacy programming tenet",
                    exact);
                await SeedAsync(
                    "project.untouched",
                    "project-record",
                    "Project record",
                    "Project bytes remain unchanged.");
            }

            var projectBefore = await ReadRequiredAsync(
                layout.V2DatabasePath,
                time,
                "project.untouched",
                "project-record");
            var result = await CortexLocalProductCommands.MigrateKnowledgeSpacesAsync(
                layout,
                time,
                "os_fixture",
                CancellationToken.None);
            LocalAssert.Equal(0, result.ExitCode, $"The product migration failed: {result.Json}");
            using var response = JsonDocument.Parse(result.Json);
            LocalAssert.True(
                response.RootElement.GetProperty("state").GetString() == "migrated" &&
                response.RootElement.GetProperty("sourceRecords").GetInt32() == 1 &&
                response.RootElement.GetProperty("migrated").GetInt32() == 1 &&
                response.RootElement.GetProperty("archived").GetInt32() == 1 &&
                response.RootElement.GetProperty("effectiveNamespaces")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .SequenceEqual([CortexKnowledgeSpaces.Shared, CortexKnowledgeSpaces.User]),
                "The migration receipt did not report its exact bounded result.");

            var backupDirectory = response.RootElement.GetProperty("backupDirectory").GetString()!;
            var backupDatabase = Path.Combine(
                backupDirectory,
                SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
            var backedLegacy = await ReadRequiredAsync(
                backupDatabase,
                time,
                CortexKnowledgeSpaces.LegacySharedProgramming,
                "legacy-tenet");
            LocalAssert.True(
                backedLegacy is { Revision: 1, Searchable: true } &&
                Encoding.UTF8.GetString(backedLegacy.ContentUtf8) == exact,
                "The pre-migration backup did not retain the exact active legacy head.");
            await using (var backup = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(backupDatabase), time))
            {
                LocalAssert.True(
                    await backup.ReadHeadAsync(
                        CortexKnowledgeSpaces.Shared,
                        "knowledge",
                        "legacy-tenet") is null,
                    "The backup was taken after, rather than before, conversion.");
            }

            var canonical = await ReadRequiredAsync(
                layout.V2DatabasePath,
                time,
                CortexKnowledgeSpaces.Shared,
                "legacy-tenet");
            var archived = await ReadRequiredAsync(
                layout.V2DatabasePath,
                time,
                CortexKnowledgeSpaces.LegacySharedProgramming,
                "legacy-tenet");
            var projectAfter = await ReadRequiredAsync(
                layout.V2DatabasePath,
                time,
                "project.untouched",
                "project-record");
            LocalAssert.True(
                canonical is { Revision: 1, Searchable: true } &&
                Encoding.UTF8.GetString(canonical.ContentUtf8) == exact &&
                archived is { Revision: 2, Searchable: false } &&
                projectAfter.Revision == projectBefore.Revision &&
                projectAfter.RevisionHash == projectBefore.RevisionHash &&
                projectAfter.ContentUtf8.SequenceEqual(projectBefore.ContentUtf8),
                "Conversion did not preserve exact canonical content, archived history, and project state.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SqliteCortexV2Record> ReadRequiredAsync(
        string databasePath,
        TimeProvider timeProvider,
        string ns,
        string recordId)
    {
        await using var store = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(databasePath),
            timeProvider);
        return await store.ReadHeadAsync(ns, "knowledge", recordId).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Missing fixture {ns}/{recordId}.");
    }

    private static CortexCurrentUserLayout CreateLayout(string root)
    {
        var canonical = Path.GetFullPath(root);
        var cortex = Path.Combine(canonical, "cortex");
        var data = Path.Combine(cortex, "data");
        return new(
            canonical,
            data,
            Path.Combine(data, "cortex.db"),
            Path.Combine(canonical, "config"),
            Path.Combine(cortex, "backups"),
            Path.Combine(canonical, "logs"),
            Path.Combine(canonical, "receipts"));
    }

    private sealed class FixedMigrationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
