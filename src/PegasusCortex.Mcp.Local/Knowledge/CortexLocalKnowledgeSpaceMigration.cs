using System.Text.Json;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.V2;

public static partial class CortexLocalProductCommands
{
    public static ValueTask<(int ExitCode, string Json)> MigrateKnowledgeSpacesAsync(
        CancellationToken cancellationToken)
    {
        var identity = CortexLocalOsIdentity.Capture();
        return MigrateKnowledgeSpacesAsync(
            CortexCurrentUserLayout.Resolve(),
            TimeProvider.System,
            $"os_{identity.Hash}",
            cancellationToken);
    }

    internal static async ValueTask<(int ExitCode, string Json)> MigrateKnowledgeSpacesAsync(
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        try
        {
            var options = new SqliteCortexV2StoreOptions(layout.V2DatabasePath).Validate();
            if (!File.Exists(options.DatabasePath))
            {
                return (1, JsonSerializer.Serialize(new
                {
                    healthy = false,
                    state = "uninitialized",
                    error = "The central Cortex v2 store is uninitialized; no migration or backup was performed.",
                    network = false,
                    service = false
                }, JsonOptions));
            }

            var backup = await BackupAsync(layout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (backup.ExitCode != 0)
            {
                return (1, JsonSerializer.Serialize(new
                {
                    healthy = false,
                    state = "backup-failed",
                    error = "The pre-migration backup failed; the knowledge-space migration was not attempted.",
                    backup = JsonDocument.Parse(backup.Json).RootElement.Clone(),
                    network = false,
                    service = false
                }, JsonOptions));
            }

            string backupDirectory;
            using (var backupJson = JsonDocument.Parse(backup.Json))
            {
                backupDirectory = backupJson.RootElement
                    .GetProperty("backupDirectory")
                    .GetString() ?? throw new InvalidDataException(
                        "The pre-migration backup receipt omitted its directory.");
            }

            var lowLevel = new SqliteCortexV2Store(options, timeProvider);
            await using var operational = new SqliteCortexV2OperationalStore(lowLevel, Version);
            await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var result = await operational.MigrateKnowledgeSpacesAsync(
                actor,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return (0, JsonSerializer.Serialize(new
            {
                healthy = true,
                state = result.SourceRecords == 0 ? "already-current" : "migrated",
                result.EffectiveNamespaces,
                result.SourceRecords,
                result.Migrated,
                result.Unchanged,
                result.Archived,
                result.Disposition,
                backupDirectory,
                store = options.DatabasePath,
                network = false,
                service = false
            }, JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 512)
                message = message[..512];
            return (1, JsonSerializer.Serialize(new
            {
                healthy = false,
                state = "migration-failed",
                error = message,
                network = false,
                service = false
            }, JsonOptions));
        }
    }
}
