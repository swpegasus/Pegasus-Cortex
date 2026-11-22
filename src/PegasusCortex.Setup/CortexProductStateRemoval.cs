using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexProductStateRemovalReceiptV1(
    string Contract,
    int Version,
    string StateRoot,
    bool Removed);

public static class CortexProductStateRemoval
{
    public const string ReceiptContract = "pegasus-cortex-state-removal-receipt";

    public static CortexProductStateRemovalReceiptV1 RemoveAll(
        CortexProductLayout layout,
        string explicitConfirmation)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        if (!string.Equals(explicitConfirmation, "remove-all-state", StringComparison.Ordinal))
            throw new InvalidOperationException("Explicit remove-all-state confirmation is required.");

        var state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.StateDirectory));
        if (!string.Equals(state, Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.DataRoot)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The product state path is not the exact registered data directory.");
        if (!Directory.Exists(state))
            return new CortexProductStateRemovalReceiptV1(ReceiptContract, 1, state, Removed: false);

        var rootInfo = new DirectoryInfo(state);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The product state root is a reparse point.");
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Product state contains a reparse point and was not removed.");
                if (entry is DirectoryInfo directory)
                    pending.Push(directory);
            }
        }

        Directory.Delete(state, recursive: true);
        return new CortexProductStateRemovalReceiptV1(ReceiptContract, 1, state, Removed: true);
    }
}
