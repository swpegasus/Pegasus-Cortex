using System.Reflection;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

public static partial class CortexLocalProductCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string Version =>
        typeof(CortexLocalProductCommands).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    public static string StatusJson()
    {
        var layout = CortexCurrentUserLayout.Resolve();
        _ = new SqliteCortexV2StoreOptions(layout.V2DatabasePath).Validate();
        return JsonSerializer.Serialize(new
        {
            product = "Pegasus Cortex",
            version = Version,
            mode = "local-operational",
            transport = "not_observed",
            storageKind = "sqlite",
            network = false,
            service = false,
            state = File.Exists(layout.V2DatabasePath) ? "initialized" : "uninitialized",
            storeGeneration = 2,
            legacyV1Preserved = File.Exists(layout.LegacyDatabasePath),
            layout = new
            {
                root = layout.Root,
                data = layout.DataDirectory,
                config = layout.ConfigDirectory,
                backups = layout.BackupDirectory,
                logs = layout.LogDirectory,
                receipts = layout.ReceiptDirectory
            }
        }, JsonOptions);
    }

    public static async ValueTask<string> StatusAsync(CancellationToken cancellationToken)
    {
        var layout = CortexCurrentUserLayout.Resolve();
        _ = new SqliteCortexV2StoreOptions(layout.V2DatabasePath).Validate();
        SqliteCortexV2Capacity? capacity = null;
        if (File.Exists(layout.V2DatabasePath))
        {
            var store = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
                TimeProvider.System);
            await using var operational = new SqliteCortexV2OperationalStore(store, Version);
            capacity = await store.CapacityAsync(cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.Serialize(new
        {
            product = "Pegasus Cortex",
            version = Version,
            mode = "local-operational",
            transport = "not_observed",
            storageKind = "sqlite",
            network = false,
            service = false,
            state = File.Exists(layout.V2DatabasePath) ? "initialized" : "uninitialized",
            storeGeneration = 2,
            legacyV1Preserved = File.Exists(layout.LegacyDatabasePath),
            capacity,
            personalProfile = JsonSerializer.SerializeToNode(
                await new CortexPersonalProfileClient(layout.LogDirectory)
                    .GetAsync(cancellationToken).ConfigureAwait(false), CortexV2Json.Options),
            layout = new
            {
                root = layout.Root,
                data = layout.DataDirectory,
                config = layout.ConfigDirectory,
                backups = layout.BackupDirectory,
                logs = layout.LogDirectory,
                receipts = layout.ReceiptDirectory
            }
        }, JsonOptions);
    }

    public static async ValueTask<(int ExitCode, string Json)> DoctorAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var layout = CortexCurrentUserLayout.Resolve();
            _ = new SqliteCortexV2StoreOptions(layout.V2DatabasePath).Validate();
            if (!File.Exists(layout.V2DatabasePath))
            {
                return (0, JsonSerializer.Serialize(new
                {
                    healthy = true,
                    state = "uninitialized",
                    message = "The central local store is initialized by the admitted Cortex owner startup.",
                    storeGeneration = 2,
                    legacyV1Preserved = File.Exists(layout.LegacyDatabasePath),
                    network = false,
                    service = false
                }, JsonOptions));
            }
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                layout.V2DatabasePath,
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            var integrity = await composition.V2Store.FullDoctorAsync(cancellationToken)
                .ConfigureAwait(false);
            var capacity = await composition.V2Store.CapacityAsync(cancellationToken)
                .ConfigureAwait(false);
            return (integrity.Healthy ? 0 : 1, JsonSerializer.Serialize(new
            {
                healthy = integrity.Healthy,
                state = integrity.State,
                storeGeneration = 2,
                revisionsScanned = integrity.RevisionRowsScanned,
                blobsScanned = integrity.BlobRowsScanned,
                heads = integrity.HeadRows,
                searchChunks = integrity.SearchChunkRows,
                ftsRows = integrity.FtsRows,
                capacity,
                legacyV1Preserved = File.Exists(layout.LegacyDatabasePath),
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
                state = "failed",
                error = message,
                network = false,
                service = false
            }, JsonOptions));
        }
    }

    public static async ValueTask<(int ExitCode, string Json)> ImportBehaviorPackAsync(
        string packRoot,
        string targetNamespace,
        string? adoptionManifestPath,
        CancellationToken cancellationToken)
    {
        return await ImportBehaviorPackCoreAsync(
            packRoot,
            targetNamespace,
            adoptionManifestPath,
            () =>
            {
                var layout = CortexCurrentUserLayout.Resolve();
                layout.EnsureCreated();
                var identity = CortexLocalOsIdentity.Capture();
                return new BehaviorPackImportTarget(
                    layout.V2DatabasePath,
                    $"os_{identity.Hash}",
                    TimeProvider.System);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask<(int ExitCode, string Json)> ImportBehaviorPackAsync(
        string packRoot,
        string targetNamespace,
        CancellationToken cancellationToken) =>
        ImportBehaviorPackAsync(packRoot, targetNamespace, null, cancellationToken);

    internal static ValueTask<(int ExitCode, string Json)> ImportBehaviorPackAsync(
        string packRoot,
        string databasePath,
        string targetNamespace,
        string actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        string? adoptionManifestPath = null) =>
        ImportBehaviorPackCoreAsync(
            packRoot,
            targetNamespace,
            adoptionManifestPath,
            () => new BehaviorPackImportTarget(databasePath, actor, timeProvider),
            cancellationToken);

    private static async ValueTask<(int ExitCode, string Json)> ImportBehaviorPackCoreAsync(
        string packRoot,
        string targetNamespace,
        string? adoptionManifestPath,
        Func<BehaviorPackImportTarget> resolveTarget,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetNamespace);
            ArgumentNullException.ThrowIfNull(resolveTarget);
            if (!string.Equals(
                    targetNamespace,
                    CortexKnowledgeSpaces.Shared,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Portable product behavior packs may target only knowledge.shared.");
            }
            var manifest = await CortexFileBehaviorPackManifestV2.ReadAsync(
                Path.GetFullPath(packRoot),
                cancellationToken).ConfigureAwait(false);
            var records = await manifest.LoadRecordsAsync(
                Path.GetFullPath(packRoot),
                cancellationToken).ConfigureAwait(false);
            if (records.Count is < 1 or > 256)
                throw new InvalidDataException("A product behavior pack must contain between 1 and 256 records.");
            var adoptions = adoptionManifestPath is null
                ? null
                : (await CortexBehaviorPackAdoptionManifestV1.ReadAsync(
                    adoptionManifestPath, manifest.PackId, manifest.PackRevision,
                    targetNamespace, cancellationToken).ConfigureAwait(false)).ToSeeds(targetNamespace);

            var target = resolveTarget();
            ArgumentException.ThrowIfNullOrWhiteSpace(target.DatabasePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Actor);
            ArgumentNullException.ThrowIfNull(target.TimeProvider);
            var lowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(Path.GetFullPath(target.DatabasePath)),
                target.TimeProvider);
            await using var operational = new SqliteCortexV2OperationalStore(lowLevel, Version);
            await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var seeds = records.Select(value => new SqliteCortexV2KnowledgeSeed(
                manifest.PackId,
                manifest.PackRevision,
                targetNamespace,
                value.Descriptor.RecordId,
                value.Descriptor.Kind,
                value.Descriptor.Tier,
                value.Descriptor.Title,
                value.Content,
                value.Descriptor.Tags,
                value.Descriptor.Source,
                value.Descriptor.SourceRelativePath)).ToArray();
            var result = await operational.ImportKnowledgeSeedsAsync(
                seeds,
                target.Actor,
                target.TimeProvider.GetUtcNow(),
                cancellationToken,
                adoptions).ConfigureAwait(false);

            return (0, JsonSerializer.Serialize(new
            {
                schema = "pegasus.cortex.behavior-pack-import/1.0",
                healthy = true,
                state = result.Imported == 0 && result.Revised == 0
                    ? "pack-current"
                    : result.Revised == 0 ? "pack-imported" : "pack-updated",
                productVersion = Version,
                packId = manifest.PackId,
                effectiveNamespaces = result.EffectiveNamespaces,
                result.Imported,
                result.Revised,
                result.Unchanged,
                result.Total,
                adopted = adoptions?.Count ?? 0,
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
                schema = "pegasus.cortex.behavior-pack-import/1.0",
                healthy = false,
                state = "pack-import-failed",
                error = message,
                network = false,
                service = false
            }, JsonOptions));
        }
    }

    private sealed record BehaviorPackImportTarget(
        string DatabasePath,
        string Actor,
        TimeProvider TimeProvider);

    public static async ValueTask<(int ExitCode, string Json)> ImportStarterCorpusAsync(
        string corpusRoot,
        bool onlyIfEmpty,
        CancellationToken cancellationToken)
    {
        var layout = CortexCurrentUserLayout.Resolve();
        layout.EnsureCreated();
        var identity = CortexLocalOsIdentity.Capture();
        return await ImportStarterCorpusAsync(
            corpusRoot,
            layout.V2DatabasePath,
            onlyIfEmpty,
            $"os_{identity.Hash}",
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<(int ExitCode, string Json)> ImportStarterCorpusAsync(
        string corpusRoot,
        string databasePath,
        bool onlyIfEmpty,
        string actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);
            ArgumentNullException.ThrowIfNull(timeProvider);
            var root = Path.GetFullPath(corpusRoot);
            var manifest = await CortexFileStarterCorpusManifestV1.ReadAsync(
                root,
                cancellationToken).ConfigureAwait(false);
            var seeds = new List<SqliteCortexV2KnowledgeSeed>();
            foreach (var pack in manifest.Packs)
            {
                var packRoot = manifest.ResolvePackRoot(root, pack);
                var behavior = await CortexFileBehaviorPackManifestV2.ReadAsync(
                    packRoot,
                    cancellationToken).ConfigureAwait(false);
                var records = await behavior.LoadRecordsAsync(
                    packRoot,
                    cancellationToken).ConfigureAwait(false);
                seeds.AddRange(records.Select(value => new SqliteCortexV2KnowledgeSeed(
                    behavior.PackId,
                    behavior.PackRevision,
                    pack.Namespace,
                    value.Descriptor.RecordId,
                    value.Descriptor.Kind,
                    value.Descriptor.Tier,
                    value.Descriptor.Title,
                    value.Content,
                    value.Descriptor.Tags,
                    value.Descriptor.Source,
                    value.Descriptor.SourceRelativePath)));
            }
            if (seeds.Count > 256 ||
                seeds.Select(value => $"{value.Namespace}\n{value.RecordId}")
                    .Distinct(StringComparer.Ordinal).Count() != seeds.Count)
            {
                throw new InvalidDataException(
                    "The starter corpus contains duplicate or excessive record coordinates.");
            }

            var lowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(Path.GetFullPath(databasePath)),
                timeProvider);
            await using var operational = new SqliteCortexV2OperationalStore(lowLevel, Version);
            await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var counters = await lowLevel.GetCatalogCountersAsync(cancellationToken).ConfigureAwait(false);
            if (onlyIfEmpty && counters.RevisionRows != 0)
            {
                return (0, JsonSerializer.Serialize(new
                {
                    healthy = true,
                    state = "existing-store-unchanged",
                    corpusId = manifest.CorpusId,
                    effectiveNamespaces = Array.Empty<string>(),
                    imported = 0,
                    unchanged = 0,
                    total = seeds.Count,
                    store = Path.GetFullPath(databasePath)
                }, JsonOptions));
            }

            var result = await operational.ImportKnowledgeSeedsAsync(
                seeds,
                actor,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return (0, JsonSerializer.Serialize(new
            {
                healthy = true,
                state = result.Imported == 0 && result.Revised == 0
                    ? "starter-current"
                    : result.Revised == 0 ? "starter-seeded" : "starter-updated",
                corpusId = manifest.CorpusId,
                effectiveNamespaces = result.EffectiveNamespaces,
                result.Imported,
                result.Revised,
                result.Unchanged,
                result.Total,
                store = Path.GetFullPath(databasePath)
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
                state = "starter-seed-failed",
                error = message,
                network = false,
                service = false
            }, JsonOptions));
        }
    }
}
