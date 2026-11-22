using System.Text.Json;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.V2;

public static partial class CortexLocalProductCommands
{
    public static async ValueTask<(int ExitCode, string Json)> UtilizationAsync(
        int days = 7, CancellationToken cancellationToken = default)
    {
        try
        {
            var layout = CortexCurrentUserLayout.Resolve();
            await using var store = new SqliteCortexV2Store(new(layout.V2DatabasePath));
            var report = await store.ReadUsageAsync(days, cancellationToken).ConfigureAwait(false);
            return (0, JsonSerializer.Serialize(report, JsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (1, "{\"schema\":\"cortex.utilization/1\",\"state\":\"unavailable\"}");
        }
    }
}
