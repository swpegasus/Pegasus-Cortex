using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

public static class CortexResidentRuntime
{
    public static object DescribeEndpoint()
    {
        var owner = LocalIdentity.Current();
        var product = CortexProductLayout.ResolveCurrent();
        return new { product = CortexProductIdentity.Product, contract = CortexResidentServer.NativeContract,
            nativePipe = CortexProductIdentity.UserNativePipe(owner.Session), mcpPipe = CortexProductIdentity.UserMcpPipe(owner.Session),
            session = owner.Session, ownerSid = owner.Sid, authentication = "windows-owner-session-and-registered-api-key",
            hostRelativePath = "bin/host/PegasusCortex.Host.exe", framing = "u16le-major,u16le-minor,i32le-length,utf8-json",
            expectedHostExecutable = Path.Combine(product.InstallRoot, "bin", "host", "PegasusCortex.Host.exe"),
            credentialDirectory = Path.Combine(product.UserConfigDirectory, "connections"),
            frameMajor = 1, frameMinor = 0, maximumHelloBytes = 4096,
            maximumRequestBytes = CortexResidentServer.RequestBytes, maximumResponseBytes = CortexResidentServer.ResponseBytes,
            maximumConnections = CortexResidentServer.ConnectionLimit, network = false,
            availability = "descriptor-only; connect and validate hello before reporting ready" };
    }

    public static Task RunCurrentAsync(CancellationToken cancellationToken) =>
        RunCurrentCoreAsync(null, null, null, null, cancellationToken);

    internal static Task RunCurrentAsync(UserHostAdmission admission, Action<string> reportState,
        CancellationToken cancellationToken) => RunCurrentCoreAsync(admission, reportState, null, null, cancellationToken);

    internal static Task RunCurrentAsync(UserHostAdmission admission, Action<string> reportState,
        CortexOwnerMcpGateway serviceGateway, CancellationToken cancellationToken) =>
        RunCurrentCoreAsync(admission, reportState, serviceGateway, null, cancellationToken);

    internal static Task RunCurrentAsync(UserHostAdmission admission, Action<string> reportState,
        CortexOwnerMcpGateway serviceGateway, CortexPayloadTieringControl payloadTiering,
        CancellationToken cancellationToken) =>
        RunCurrentCoreAsync(admission, reportState, serviceGateway, payloadTiering, cancellationToken);

    private static async Task RunCurrentCoreAsync(UserHostAdmission? admission, Action<string>? reportState,
        CortexOwnerMcpGateway? serviceGateway, CortexPayloadTieringControl? payloadTiering,
        CancellationToken cancellationToken)
    {
        var diagnostics = TextWriter.Synchronized(Console.Error);
        var readiness = new Readiness(reportState);
        try
        {
            var maintenance = MaintenanceModeStore.OpenReader();
            UserHostStartup.RequireOpen(maintenance);
            using var initialization = admission?.TryEnter();
            if (admission is not null && initialization is null) return;
            readiness.Initializing();
            var owner = LocalIdentity.Current();
            var layout = CortexCurrentUserLayout.Resolve();
            UserHostStartup.RequireOpen(maintenance);
            layout.EnsureCreated();
            var product = CortexProductLayout.ResolveCurrent();
            var seeded = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                Path.Combine(product.InstallRoot, "bin", "semantics", "behavior-packs"), true, cancellationToken);
            if (seeded.ExitCode != 0) throw new InvalidDataException("starter_initialization_failed");
            var profile = new CortexPersonalProfileClient(layout.LogDirectory);
            var research = CortexShadowConfiguration.ReadOwner(layout.ConfigDirectory);
            UserHostStartup.RequireOpen(maintenance);
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(layout.V2DatabasePath,
                TimeProvider.System, cancellationToken, profile, research);
            if (composition.ShadowResearchDiagnostics is { Issue: { } issue })
                await diagnostics.WriteLineAsync("component=geometry-shadow state=disabled issue=" + issue);
            CortexAssetResourcesV2? assets = null;
            try
            {
                var store = new CortexAssetStoreV2(layout.AssetDirectory);
                assets = new(store, CortexAssetProductCommandsV2.ReadExposedNamespaces(store), profile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { await diagnostics.WriteLineAsync("component=assets state=disabled cause=" + error.GetType().Name); }
            var clients = new CortexClientRegistry(Path.Combine(layout.ConfigDirectory, "connections"), owner);
            await BootstrapManagedCodexCredentialAsync(clients,
                Path.Combine(product.InstallRoot, "bin", "semantics", "PegasusCortex.exe"), diagnostics);
            await using var execution = new RoutingDispatcher();
            var app = new CortexUnifiedMcpApplication(composition.Application, execution, assets);
            using var semanticLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ingressDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CortexMcpHostController? shared = null;
            void StopIngress()
            {
                readiness.Stopping();
                // Every front door is fenced even if another cancellation
                // callback reports a cleanup failure. Presentation is not the
                // admission boundary and cannot replace these actual stops.
                try { serviceGateway?.BeginClose(); }
                finally
                {
                    try { Volatile.Read(ref shared)?.RequestStop(); }
                    finally
                    {
                        try { semanticLifetime.Cancel(); }
                        catch (AggregateException) { /* The common drain still owns every callback. */ }
                    }
                }
            }
            void StopTieringIngress()
            {
                // Latch unavailable even when cancellation/cleanup reports a
                // failure. This callback never restarts a semantic owner.
                readiness.Unavailable();
                try { StopIngress(); }
                finally { readiness.Unavailable(); }
            }
            var server = new CortexResidentServer(app, clients, owner, CortexLocalProductCommands.Version, diagnostics,
                admission, endpointsStopping: StopIngress);
            var serving = server.RunAsync(CortexProductIdentity.UserNativePipe(owner.Session), CortexProductIdentity.UserMcpPipe(owner.Session), semanticLifetime.Token);
            readiness.ReadyIfServing(serving);
            initialization?.Dispose();
            var nativeObserved = false;
            try
            {
                // Only the real ordinary Host supplies this admission instance.
                // Shared hosting reuses its application/scheduler; public callers
                // without that owner boundary never start another HTTP endpoint.
                if (admission is not null)
                {
                    var started = await CortexMcpHostController.StartForInstallRootAsync(
                        product.InstallRoot, CortexMcpOwner.Application,
                        _ => Task.FromResult(server.CreateOwnerEndpointDispatcher(admission)),
                        semanticLifetime.Token);
                    Volatile.Write(ref shared, started);
                    if (semanticLifetime.IsCancellationRequested) started.RequestStop();
                    serviceGateway?.Bind(shared.State.StartupSettings,
                        () => server.CreateOwnerEndpointDispatcher(admission));
                    await diagnostics.WriteLineAsync("component=shared-mcp state=" +
                        shared.State.Status.ToString().ToLowerInvariant() +
                        (shared.State.Issue is { } sharedIssue ? " issue=" + sharedIssue : string.Empty));
                }
                if (payloadTiering is not null && admission is not null)
                    payloadTiering.BindCurrent(composition.V2Store,
                        new PayloadTieringRuntimeLifetime(StopTieringIngress, ingressDrained.Task), layout.ConfigDirectory);
                nativeObserved = true;
                await serving;
            }
            finally
            {
                // Close all front doors before the final scheduler drain. Its
                // native-loop drain alone can race a still-open HTTP listener.
                try
                {
                    string? stopIssue = null;
                    try { StopIngress(); }
                    catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
                    { stopIssue = cleanup.GetType().Name; }
                    try
                    {
                        try { if (serviceGateway is not null) await serviceGateway.CloseAndDrainAsync(); }
                        finally { if (shared is not null) await shared.DisposeAsync(); }
                    }
                    finally
                    {
                        try
                        {
                            // Startup may fail before the main await. Join native
                            // connections too, preserving the startup failure while
                            // observing any bounded cleanup failure separately.
                            if (!nativeObserved)
                                try { await serving; }
                                catch (OperationCanceledException) when (semanticLifetime.IsCancellationRequested) { }
                                catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
                                { await diagnostics.WriteLineAsync("component=resident-cleanup state=failed cause=" + cleanup.GetType().Name); }
                        }
                        finally { await server.WhenIdleAsync(); }
                    }
                    // A failed schema refresh may await this acknowledgement
                    // inside the control. Never substitute failed cleanup for
                    // an actual drain or release the composition beneath it.
                    readiness.Unavailable();
                    ingressDrained.TrySetResult();
                    if (stopIssue is not null)
                        await diagnostics.WriteLineAsync("component=resident-ingress-stop state=failed cause=" + stopIssue);
                }
                finally
                {
                    readiness.Unavailable();
                    if (payloadTiering is not null) await payloadTiering.CloseAndDrainAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // A semantic outage must not kill the independent execution host.
            // Never fall back to a private store, a different user or a second process.
            await diagnostics.WriteLineAsync("component=resident-semantics state=unavailable cause=" + error.GetType().Name);
        }
        finally { readiness.Unavailable(); }
    }

    private sealed class PayloadTieringRuntimeLifetime(Action stop, Task drained)
        : ICortexPayloadTieringRuntimeLifetime
    {
        public void StopIngress() => stop();
        public Task WhenIngressDrainedAsync() => drained;
    }


    // This one client's startup credential is optional for owner HTTP/service
    // ingress. The registry still rejects its revoked/expired/missing key, and
    // this call never repairs, rotates, re-enrolls or retries a refused credential.
    internal static async Task BootstrapManagedCodexCredentialAsync(CortexClientRegistry clients,
        string application, TextWriter diagnostics)
    {
        try { _ = clients.ManagedCodexCredential(application); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or
            InvalidDataException or JsonException or CryptographicException)
        {
            var cause = error.GetType().Name;
            if (cause.Length > 80) cause = cause[..80];
            try { await diagnostics.WriteLineAsync("component=managed-codex-credential state=unavailable cause=" + cause); }
            catch (Exception observation) when (observation is not OutOfMemoryException and not StackOverflowException)
            { /* A failed optional-client diagnostic cannot disable independent owner ingress. */ }
        }
    }

    // A native endpoint can fail synchronously and return an incomplete drain
    // task. Serialize notification with its stopping latch so the startup caller
    // cannot publish ready after that failure has already closed ingress.
    internal sealed class Readiness(Action<string>? report)
    {
        private readonly object gate = new();
        private bool stopping;
        private string? state;

        internal void Initializing()
        {
            lock (gate) if (!stopping && state is null) Notify("initializing");
        }

        internal void ReadyIfServing(Task serving)
        {
            ArgumentNullException.ThrowIfNull(serving);
            lock (gate) if (!stopping && !serving.IsCompleted) Notify("ready");
        }

        internal void Stopping()
        {
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                Notify("stopping");
            }
        }

        internal void Unavailable()
        {
            lock (gate)
            {
                stopping = true;
                Notify("unavailable");
            }
        }

        private void Notify(string value)
        {
            if (state == value) return;
            state = value;
            try { report?.Invoke(value); }
            catch (Exception observation) when (observation is not OutOfMemoryException and not StackOverflowException)
            { /* Presentation observers cannot skip semantic ingress closure or its actual drain. */ }
        }
    }

    internal static bool ValidHello(JsonElement response, string contract, LocalPeer owner, string clientId,
        string? expectedHostVersion = null) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
        response.TryGetProperty("contract", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == contract &&
        response.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String && product.GetString() == CortexProductIdentity.Product &&
        response.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && CortexProductIdentity.IsReleaseVersion(version.GetString()) &&
        (expectedHostVersion is null || version.GetString() == expectedHostVersion) &&
        response.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "ready" &&
        response.TryGetProperty("storageKind", out var storage) && storage.ValueKind == JsonValueKind.String && storage.GetString() == "sqlite-v2" &&
        response.TryGetProperty("ownerSid", out var sid) && sid.ValueKind == JsonValueKind.String && sid.GetString() == owner.Sid &&
        response.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Number && session.TryGetInt32(out var number) && number == owner.Session &&
        response.TryGetProperty("clientId", out var client) && client.ValueKind == JsonValueKind.String && client.GetString() == clientId;

    internal static string RequireSelectedAdapter(string installRoot, string catalogPath, string processPath, string componentVersion)
    {
        CortexProductLayout.RejectExistingReparseComponents(catalogPath);
        CortexProductLayout.RejectExistingReparseComponents(processPath);
        var catalog = CortexProductCatalogV1.ReadExact(catalogPath);
        var role = catalog.Roles.Single(item => item.Role == "mcp");
        var expected = Path.GetFullPath(Path.Combine(installRoot, role.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(processPath), expected) ||
            role.ComponentVersion != componentVersion || !File.Exists(expected))
            throw new InvalidDataException("adapter_selection_mismatch");
        return catalog.Roles.Single(item => item.Role == "host").ComponentVersion;
    }

}
