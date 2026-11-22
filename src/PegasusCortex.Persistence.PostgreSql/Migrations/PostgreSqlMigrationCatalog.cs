using System.Reflection;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Persistence.PostgreSql.Migrations;

public sealed record PostgreSqlMigration(
    string Id,
    string Description,
    string Sql,
    string Sha256);

public static class PostgreSqlMigrationCatalog
{
    /// <summary>
    /// The provider contains the append-only repository and the separate sync readiness probe.
    /// Hosts still must complete both base and sync admission before advertising capability.
    /// </summary>
    public const bool OfflineSyncRuntimeAvailable = true;
    public const string OfflineSyncRuntimeAvailabilityReason =
        "repository_and_sync_readiness_available";

    private static readonly IReadOnlyList<PostgreSqlMigration> Migrations = LoadMigrations();

    public static IReadOnlyList<PostgreSqlMigration> All => Migrations;
    public static string LatestId => Migrations[^1].Id;

    private static IReadOnlyList<PostgreSqlMigration> LoadMigrations()
    {
        var assembly = typeof(PostgreSqlMigrationCatalog).Assembly;
        return Array.AsReadOnly(
        [
            Load(assembly, "0001_initial.sql", "Initial immutable Cortex store, RLS, and lexical index"),
            Load(assembly, "0002_offline_sync.sql", "Attempt-first offline journal durability and reconciliation evidence"),
            Load(assembly, "0003_legacy_migration_cursor_authority.sql", "Durable server-authoritative legacy migration cursor continuation"),
            Load(assembly, "0004_hybrid_project_retention.sql", "Authenticated project correlation and approved retention promotion"),
            Load(assembly, "0005_audit_review_delegation.sql", "Immutable audit assessments and bounded delegated review authority"),
            Load(assembly, "0006_agent_retention.sql", "Immutable passive agent retention with authenticated rows and scoped project briefs"),
            Load(assembly, "0007_author_owned_publication_change.sql", "Author-owned immutable knowledge supersession and withdrawal evidence")
        ]);
    }

    private static PostgreSqlMigration Load(Assembly assembly, string suffix, string description)
    {
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        if (names.Length != 1)
            throw new InvalidOperationException($"Expected one embedded migration ending in '{suffix}', found {names.Length}.");

        using var stream = assembly.GetManifestResourceStream(names[0])
            ?? throw new InvalidOperationException($"Embedded migration '{names[0]}' is unavailable.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        var sql = NormalizeSql(reader.ReadToEnd());
        var id = suffix[..suffix.IndexOf('_')];
        if (sql.Length is < 1 or > 512 * 1024)
            throw new InvalidOperationException($"Migration '{id}' exceeds its bounded SQL contract.");
        return new PostgreSqlMigration(id, description, sql, CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(sql)));
    }

    private static string NormalizeSql(string sql) =>
        sql.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
}
