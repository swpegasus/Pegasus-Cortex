using System.Text.Json;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Mcp.Local.Offline;

public sealed record CortexOfflineWarmupProjectResult(
    string ProjectId,
    string RootPath,
    bool Healthy,
    string State,
    string? Error);

public sealed record CortexOfflineWarmupResult(
    string Schema,
    DateTimeOffset CompletedAtUtc,
    bool Healthy,
    string CentralState,
    int RegisteredProjects,
    IReadOnlyList<CortexOfflineWarmupProjectResult> Projects,
    SqliteCortexV2Capacity? Capacity,
    string ReceiptPath);

public static class CortexOfflineWarmup
{
    public const string Schema = "pegasus.cortex.offline-warmup-receipt/1.0";
    public const int MaximumReceiptUtf8Bytes = 64 * 1024;

    public static async ValueTask<CortexOfflineWarmupResult> RunAsync(
        CortexCurrentUserLayout layout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        timeProvider ??= TimeProvider.System;
        layout.EnsureCreated();
        var catalog = new CortexOfflineProjectCatalog(
            Path.Combine(layout.ConfigDirectory, "offline-projects.json"),
            timeProvider);
        var registered = await catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
        // Sign-in warmup has no active Codex task identity. It initializes and
        // checks the shared central store only; task projections are refreshed
        // explicitly by `offline refresh --task-id` so siblings are never
        // enumerated or opened from an ambient process.
        var projects = new List<CortexOfflineWarmupProjectResult>();
        var centralState = "ready";
        SqliteCortexV2Capacity? capacity = null;
        SqliteCortexV2OperationalStore? centralStore = null;
        try
        {
            var lowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
                timeProvider);
            centralStore = new SqliteCortexV2OperationalStore(
                lowLevel,
                CortexLocalProductCommands.Version);
            await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var quick = await lowLevel.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!quick.Healthy || quick.CorpusRowsRead != 0)
                throw new InvalidDataException("The central V2 quick check was not healthy and bounded.");
            capacity = await lowLevel.CapacityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            centralState = $"failed:{BoundedError(exception)}";
        }
        await using var centralLease = centralStore;

        var healthy = string.Equals(centralState, "ready", StringComparison.Ordinal);
        var receiptPath = Path.Combine(layout.ReceiptDirectory, "offline-warmup-latest.json");
        var result = new CortexOfflineWarmupResult(
            Schema,
            timeProvider.GetUtcNow().ToUniversalTime(),
            healthy,
            centralState,
            registered.Projects.Count,
            Array.AsReadOnly(projects.ToArray()),
            capacity,
            receiptPath);
        await WriteReceiptAsync(receiptPath, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask WriteReceiptAsync(
        string path,
        CortexOfflineWarmupResult result,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bytes.Length > MaximumReceiptUtf8Bytes)
            throw new InvalidOperationException("The offline warmup receipt exceeds its byte bound.");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (Directory.Exists(path) || info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The offline warmup receipt path is unsafe.");
        }
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".offline-warmup.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            CortexLocalRegistryAclPolicy.ProtectFile(temporary);
            File.Move(temporary, path, overwrite: true);
            CortexLocalRegistryAclPolicy.ProtectFile(path);
            CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string BoundedError(Exception exception)
    {
        var value = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 256 ? value : value[..256];
    }
}
