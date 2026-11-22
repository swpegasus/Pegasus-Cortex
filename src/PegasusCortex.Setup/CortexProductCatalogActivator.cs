using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public static class CortexProductCatalogActivator
{
    public static void Activate(CortexProductLayout layout, string candidateCatalogPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateCatalogPath);
        var candidatePath = Path.GetFullPath(candidateCatalogPath);
        RequireOrdinaryFile(candidatePath);
        var catalog = CortexProductCatalogV1.ReadExact(candidatePath);
        foreach (var role in catalog.Roles)
        {
            var rolePath = Path.GetFullPath(Path.Combine(
                layout.InstallRoot,
                role.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            RequireContained(layout.InstallRoot, rolePath);
            RequireOrdinaryFile(rolePath);
        }

        Directory.CreateDirectory(layout.InstallStateDirectory);
        var temporary = Path.Combine(
            layout.InstallStateDirectory,
            $".active.v1.json-{Guid.NewGuid():N}");
        try
        {
            using (var source = new FileStream(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            _ = CortexProductCatalogV1.ReadExact(temporary);
            File.Move(temporary, layout.ActiveCatalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void RequireOrdinaryFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("A Cortex catalog role is absent or is a reparse point.", path);
    }

    private static void RequireContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Cortex catalog role escaped the product root.");
        }
    }
}
