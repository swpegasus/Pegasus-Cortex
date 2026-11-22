using System.Text.Json;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Product;

// Development convenience only. Public installations use the product CLI;
// validation and admission have one shared source implementation.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args is not ["import", "--manifest", var manifest, "--sha256", var pin])
        throw new InvalidDataException("Expected import --manifest <exact file> --sha256 <pin>.");
    var product = CortexProductLayout.ResolveCurrent();
    var root = Path.Combine(product.UserStateDirectory, "assets");
    CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
    var store = new CortexAssetStoreV2(root, CortexVisualLibraryV2.MaximumAssetBytes);
    var receipt = await CortexVisualLibraryV2.ImportAsync(store, manifest, pin, cancellation.Token, Console.WriteLine);
    Console.WriteLine(JsonSerializer.Serialize(receipt, CortexVisualLibraryV2.Json));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException)
{
    var asset = error as CortexAssetException;
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = asset?.Code ?? error.GetType().Name,
        committed = asset?.Committed,
        diagnostic = error is InvalidDataException ? error.Message : "Visual-library admission failed; exact committed assets remain replayable."
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    return asset?.Committed is > 0 ? 7 : 3;
}
