using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

public sealed record CortexCurrentUserLayout(
    string Root,
    string DataDirectory,
    string DatabasePath,
    string ConfigDirectory,
    string BackupDirectory,
    string LogDirectory,
    string ReceiptDirectory)
{
    public string? SemanticUserRoot { get; init; }
    /// <summary>
    /// The preserved first-generation central store. V2 startup and product
    /// commands never open or migrate this database implicitly.
    /// </summary>
    public string LegacyDatabasePath => DatabasePath;

    /// <summary>
    /// The fresh operational V2 store used by the general local MCP route.
    /// </summary>
    public string V2DatabasePath => Path.Combine(DataDirectory, "cortex-v2.db");

    public string AssetDirectory => Path.Combine(Root, "assets");

    public static CortexCurrentUserLayout Resolve() => FromProduct(CortexProductLayout.ResolveCurrent());

    // Production resolves registration once; isolated startup fixtures pass the
    // already admitted product layout through this same projection.
    internal static CortexCurrentUserLayout FromProduct(CortexProductLayout product)
    {
        ArgumentNullException.ThrowIfNull(product);
        product.RequireRuntimePathsWithinDeclaredRoots();
        return new(
            product.UserStateDirectory,
            product.CortexDataDirectory,
            product.LegacyCortexDatabasePath,
            product.UserConfigDirectory,
            product.UserBackupDirectory,
            product.UserLogDirectory,
            product.UserReceiptDirectory)
        {
            SemanticUserRoot = Path.Combine(product.DataRoot, "users", product.OwnerSid)
        };
    }

    public void EnsureCreated()
    {
        foreach (var path in new[]
                 {
                     Root, SemanticUserRoot,
                     SemanticUserRoot is null ? null : Path.Combine(SemanticUserRoot, "cortex"),
                     DataDirectory, ConfigDirectory, BackupDirectory,
                     LogDirectory, ReceiptDirectory
                 }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CortexUserStateDirectories.EnsureCurrentUserDirectory(path);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(path);
        }
    }
}
