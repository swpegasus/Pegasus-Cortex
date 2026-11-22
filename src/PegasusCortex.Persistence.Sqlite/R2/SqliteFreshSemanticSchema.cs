using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.R2;

namespace Pegasus.Cortex.Persistence.Sqlite.R2;

internal static class SqliteFreshSemanticSchema
{
    // Distinct from V2 generation and from product/MCP versions.
    internal const int ApplicationId = 0x50435232;
    internal static readonly IReadOnlyDictionary<string, string> Definitions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cortex_r2_meta"] = """
                CREATE TABLE cortex_r2_meta (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                    contract TEXT NOT NULL,
                    schema_generation TEXT NOT NULL,
                    schema_version INTEGER NOT NULL,
                    store_instance_id TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    schema_sha256 TEXT NOT NULL
                )
                """,
            ["cortex_r2_projects"] = """
                CREATE TABLE cortex_r2_projects (
                    project_id TEXT NOT NULL PRIMARY KEY CHECK (length(project_id) BETWEEN 1 AND 128)
                )
                """,
            ["cortex_r2_chats"] = """
                CREATE TABLE cortex_r2_chats (
                    chat_id TEXT NOT NULL PRIMARY KEY CHECK (length(chat_id) BETWEEN 1 AND 128),
                    project_id TEXT REFERENCES cortex_r2_projects(project_id)
                )
                """,
            ["cortex_r2_tasks"] = """
                CREATE TABLE cortex_r2_tasks (
                    task_id TEXT NOT NULL PRIMARY KEY CHECK (length(task_id) BETWEEN 1 AND 128),
                    project_id TEXT REFERENCES cortex_r2_projects(project_id),
                    chat_id TEXT REFERENCES cortex_r2_chats(chat_id)
                )
                """,
            ["cortex_r2_record_identities"] = """
                CREATE TABLE cortex_r2_record_identities (
                    namespace TEXT NOT NULL CHECK (length(namespace) BETWEEN 1 AND 128),
                    record_kind TEXT NOT NULL CHECK (record_kind IN ('task', 'knowledge')),
                    record_id TEXT NOT NULL CHECK (length(record_id) BETWEEN 1 AND 128),
                    project_id TEXT REFERENCES cortex_r2_projects(project_id),
                    chat_id TEXT REFERENCES cortex_r2_chats(chat_id),
                    task_id TEXT REFERENCES cortex_r2_tasks(task_id),
                    PRIMARY KEY (namespace, record_kind, record_id),
                    CHECK (record_kind != 'task' OR (task_id IS NOT NULL AND task_id = record_id))
                )
                """
        };

    internal static string Fingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(CortexSemanticBaselineV1.ContractName + "\n" +
            CortexSemanticBaselineV1.Generation + "\n" + CortexSemanticBaselineV1.Version + "\n" +
            string.Join("\n", Definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Trim())))));
}
