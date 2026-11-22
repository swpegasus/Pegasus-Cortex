using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Operations;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Mcp.V2;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Query;
using Pegasus.Cortex.Persistence.Sqlite.Storage;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>
/// Explicit standalone composition. It owns one SQLite store and no remote,
/// PostgreSQL, Nexus, service, or environment-selected dependency.
/// </summary>
internal sealed class CortexMcpLocalComposition : IAsyncDisposable
{
    private readonly CortexShadowResearchHost? _shadowResearch;
    private readonly SqliteCortexRetentionStore? _legacyStore;
    private readonly SqliteCortexV2Store? _v2Store;
    private readonly SqliteCortexV2OperationalStore? _v2OperationalStore;
    private readonly CortexLocalProjectRegistry? _registry;
    private readonly CortexMcpLocalRuntime? _runtime;
    private readonly SqliteCortexProjectBriefStore? _projectBriefStore;

    private CortexMcpLocalComposition(
        SqliteCortexRetentionStore? legacyStore,
        SqliteCortexV2Store? v2Store,
        SqliteCortexV2OperationalStore? v2OperationalStore,
        CortexLocalProjectRegistry? registry,
        CortexMcpLocalRuntime? runtime,
        SqliteCortexProjectBriefStore? projectBriefStore,
        ICortexMcpProtocolApplication application,
        CortexShadowResearchHost? shadowResearch = null)
    {
        _shadowResearch = shadowResearch;
        _legacyStore = legacyStore;
        _v2Store = v2Store;
        _v2OperationalStore = v2OperationalStore;
        _registry = registry;
        _runtime = runtime;
        _projectBriefStore = projectBriefStore;
        Application = application;
    }

    public CortexLocalProjectRegistry Registry =>
        _registry ?? throw new InvalidOperationException("The general Cortex composition has no project registry.");
    internal CortexMcpLocalRuntime Runtime =>
        _runtime ?? throw new InvalidOperationException("The general Cortex composition has no legacy project runtime.");
    internal SqliteCortexProjectBriefStore ProjectBriefStore =>
        _projectBriefStore ?? throw new InvalidOperationException("The general Cortex composition has no legacy project-brief store.");
    internal CortexShadowHostDiagnostics? ShadowResearchDiagnostics => _shadowResearch?.Diagnostics;
    public ICortexMcpProtocolApplication Application { get; }
    public SqliteCortexRetentionStore Store =>
        _legacyStore ?? throw new InvalidOperationException("The general Cortex composition does not open the preserved V1 store.");
    public SqliteCortexV2Store V2Store =>
        _v2Store ?? throw new InvalidOperationException("The legacy profile composition has no V2 store.");

    public static async ValueTask<CortexMcpLocalComposition> CreateGeneralAsync(
        string databasePath,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken,
        ICortexPersonalProfileProvider? personalProfileProvider = null,
        CortexShadowConfiguration? shadowResearch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();
        timeProvider ??= TimeProvider.System;
        var store = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(databasePath),
            timeProvider);
        CortexShadowResearchHost? researchHost = null;
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await store.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!integrity.Healthy || integrity.CorpusRowsRead != 0)
                throw new InvalidDataException("The central local Cortex V2 store failed its bounded startup check.");
            var identity = CortexLocalOsIdentity.Capture();
            var actor = $"os_{identity.Hash}";
            var operationalStore = new SqliteCortexV2OperationalStore(
                store,
                CortexLocalProductCommands.Version);
            researchHost = CortexShadowResearchHost.Create(operationalStore, shadowResearch);
            var application = new CortexV2McpApplication(
                new CortexV2Application(researchHost.Store, actor, timeProvider, personalProfileProvider),
                // Attribution comes from explicit operation task IDs. An inherited
                // process environment is not proof that all clients share one task.
                new CortexUsageRecorder(store, "mcp"));
            return new CortexMcpLocalComposition(
                legacyStore: null,
                v2Store: store,
                v2OperationalStore: operationalStore,
                registry: null,
                runtime: null,
                projectBriefStore: null,
                application, researchHost);
        }
        catch
        {
            try { if (researchHost is not null) await researchHost.DisposeAsync().ConfigureAwait(false); }
            finally { await store.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public static async ValueTask<CortexMcpLocalComposition> CreateAsync(
        string profileId,
        string databasePath,
        string registryPath,
        string selectedMode,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedMode);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateDedicatedStateLayout(
            profileId,
            databasePath,
            registryPath,
            selectedMode);
        var endpoint = CortexLocalEndpoint.Create(profileId);
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            registryPath,
            profileId,
            cancellationToken).ConfigureAwait(false);
        var store = new SqliteCortexRetentionStore(
            new SqliteCortexStoreOptions(databasePath),
            timeProvider);
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await store.CheckIntegrityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!integrity.Healthy)
            {
                throw new InvalidDataException(
                    $"The local Cortex store failed semantic startup integrity: {integrity.Result}");
            }
            var projectBriefStore = new SqliteCortexProjectBriefStore(store, timeProvider);
            var runtime = new CortexMcpLocalRuntime(
                registry,
                store,
                projectBriefStore,
                timeProvider);
            CortexMcpLocalProposalRuntime? proposalRuntime =
                registry.HasWritableProjects || registry.HasReadableProjects
                    ? new CortexMcpLocalProposalRuntime(
                        registry,
                        store,
                        timeProvider)
                    : null;

            var capabilities = CortexMcpRuntimeCapability.Read;
            if (registry.HasReadableProjects)
                capabilities |= CortexMcpRuntimeCapability.RetainedRead;
            if (registry.HasWritableProjects)
            {
                capabilities |=
                    CortexMcpRuntimeCapability.RetainedWrite |
                    CortexMcpRuntimeCapability.Propose;
            }
            var identity = CortexLocalOsIdentity.Capture();
            var descriptor = new CortexMcpRuntimeDescriptor(
                "standalone-local",
                "ready",
                IssueCode: null,
                endpoint.PipeName,
                "current-os-user",
                $"os_{identity.Hash}",
                capabilities,
                UsesNetwork: false,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: false,
                $"Explicit {selectedMode} profile '{profileId}' using a canonical local registry and current-user boundary.",
                StorageKind: CortexMcpStorageKinds.Sqlite);
            var ports = new CortexMcpRuntimeComposition(
                descriptor,
                ProposalSubmission:
                    registry.HasWritableProjects ? proposalRuntime : null,
                ProposalQuery:
                    registry.HasReadableProjects ? proposalRuntime : null,
                ProjectBrief: registry.HasReadableProjects ? runtime : null,
                RetainedAssertionReader: registry.HasReadableProjects ? runtime : null,
                RetainedAssertionWriter: registry.HasWritableProjects ? runtime : null);

            var source = DefaultLibraryQuerySource.CreateVerified();
            var queryService = new CortexQueryService(
                source,
                new CortexQueryServiceOptions(
                    DefaultKnowledgeLibrary.DefaultNamespace,
                    "isolated-read-only",
                    "verified-at-startup"));
            var authority = new CortexQueryAuthority(
                "local",
                "pegasus-cortex-mcp-local",
                $"local-{selectedMode}",
                "pegasus-cortex-mcp-local",
                policyRevision: 1,
                [new CortexQueryNamespaceGrant(
                    DefaultKnowledgeLibrary.DefaultNamespace,
                    "internal",
                    [
                        CortexQueryCapability.Discover,
                        CortexQueryCapability.Status,
                        CortexQueryCapability.Catalog,
                        CortexQueryCapability.Search,
                        CortexQueryCapability.Read
                    ],
                    allowStale: true,
                    allowBlockedOrUnverified: true)]);
            var application = await CortexMcpApplication.CreateAsync(
                queryService,
                source,
                authority,
                DefaultKnowledgeLibrary.DefaultNamespace,
                ports,
                cancellationToken).ConfigureAwait(false);
            return new CortexMcpLocalComposition(
                legacyStore: store,
                v2Store: null,
                v2OperationalStore: null,
                registry,
                runtime,
                projectBriefStore,
                application);
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_shadowResearch is not null) await _shadowResearch.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (_v2OperationalStore is not null)
                await _v2OperationalStore.DisposeAsync().ConfigureAwait(false);
            else if (_legacyStore is not null)
                await _legacyStore.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateDedicatedStateLayout(
        string profileId,
        string databasePath,
        string registryPath,
        string selectedMode)
    {
        var databaseParent = Path.GetDirectoryName(databasePath) ??
            throw new ArgumentException("The database path has no state root.", nameof(databasePath));
        var registryParent = Path.GetDirectoryName(registryPath) ??
            throw new ArgumentException("The registry path has no state root.", nameof(registryPath));
        if (!string.Equals(databaseParent, registryParent, PathComparison) ||
            !string.Equals(
                Path.GetFileName(databasePath),
                "cortex.db",
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(registryPath),
                "projects.json",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The database and registry must use the exact dedicated local state layout.");
        }
        var stateRoot = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            databaseParent,
            requireExisting: true,
            allowAbsent: false);
        if (string.Equals(selectedMode, "stdio", StringComparison.Ordinal) &&
            !string.Equals(
                Path.GetFileName(stateRoot),
                profileId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The portable stdio state-root leaf must equal the explicit profile.");
        }
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(stateRoot);

        var allowedNames = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            "projects.json",
            "cortex.db",
            "cortex.db-wal",
            "cortex.db-shm"
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(stateRoot))
        {
            if (!allowedNames.Contains(Path.GetFileName(entry)) ||
                Directory.Exists(entry))
            {
                throw new InvalidDataException(
                    "The local Cortex state root contains an unexpected entry and is not dedicated.");
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
