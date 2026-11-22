namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Content-safe storage identities exposed through discovery and status.
/// They describe the composed provider only; they reveal no endpoint, path,
/// database name, credential, or connection string.
/// </summary>
public static class CortexMcpStorageKinds
{
    public const string None = "none";
    public const string Sqlite = "sqlite";
    public const string PostgreSql = "postgresql";

    public static bool IsSupported(string value) =>
        value is None or Sqlite or PostgreSql;
}
