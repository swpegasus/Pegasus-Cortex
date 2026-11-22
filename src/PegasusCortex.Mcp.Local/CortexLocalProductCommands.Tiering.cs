using System.Text.Json;
using Pegasus.Cortex.Persistence.Sqlite.V2;

public static partial class CortexLocalProductCommands
{
    // Root owns the fixed CLI entry and its quiesced-owner admission. These
    // helpers never discover a database, stop an owner, or migrate implicitly.
    // Upgrade is only the explicit admitted operation held under that lease.
    public static async ValueTask<(int ExitCode, string Json)> UpgradePayloadStorageAsync(
        string explicitDatabasePath, int expectedSchemaVersion,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireMaintenance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireMaintenance);
        await using var maintenance = await acquireMaintenance(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The product maintenance lease is unavailable.");
        await using var store = new SqliteCortexV2Store(new(explicitDatabasePath, EnablePayloadOffload: true));
        var receipt = await store.UpgradePayloadStorageAsync(expectedSchemaVersion, cancellationToken).ConfigureAwait(false);
        return (0, JsonSerializer.Serialize(receipt, JsonOptions));
    }

    public static async ValueTask<(int ExitCode, string Json)> TierArchivedPayloadsAsync(
        string explicitDatabasePath, SqliteCortexTieringRequest request, bool rehydrate,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireMaintenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireMaintenance);
        await using var maintenance = await acquireMaintenance(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The product maintenance lease is unavailable.");
        await using var store = new SqliteCortexV2Store(new(explicitDatabasePath, EnablePayloadOffload: true));
        var receipt = rehydrate
            ? await store.RehydratePayloadsAsync(request, cancellationToken).ConfigureAwait(false)
            : await store.OffloadArchivedPayloadsAsync(request, cancellationToken).ConfigureAwait(false);
        return (0, JsonSerializer.Serialize(receipt, JsonOptions));
    }
}
