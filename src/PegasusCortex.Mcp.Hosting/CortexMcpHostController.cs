using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Hosting;

public enum CortexMcpHostStatus { Disabled, ConfiguredForOtherOwner, Starting, Ready, Unavailable, Stopping, Stopped, ShutdownFailed }

public sealed record CortexMcpHostState(CortexMcpSettings? StartupSettings,CortexMcpOwner CurrentOwner,
    CortexMcpOwner? EffectiveOwner,CortexMcpHostStatus Status,string? Issue,bool PendingRestart,
    Uri? Endpoint,McpTlsQualificationResult? TlsQualification);

/// <summary>
/// Owns only one listener and its already-admitted product dispatcher. It never creates
/// a product composition, database, execution engine, service, identity or STDIO backend.
/// Settings are a startup snapshot: no hot takeover, restart, retry or listener adoption.
/// </summary>
public sealed class CortexMcpHostController:IAsyncDisposable
{
    public const string ProductKey="cortex";
    public const string EndpointPath="/mcp/"+ProductKey;
    private readonly object stateGate=new();
    private readonly TaskCompletionSource stop=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CortexMcpHostState state;
    private SharedMcpServer? listener;
    private CallbackDispatcher? dispatcher;
    private CortexMcpCertificates? certificates;
    private CancellationTokenRegistration stopRegistration;
    private Task completion=Task.CompletedTask;

    private CortexMcpHostController(CortexMcpSettings? settings,CortexMcpOwner owner)=>
        state=new(settings,owner,null,CortexMcpHostStatus.Starting,null,false,null,null);

    public CortexMcpHostState State
    {
        get
        {
            lock(stateGate)return state with
            { StartupSettings=state.StartupSettings is { } saved?saved with { AllowedPeerCidrs=saved.AllowedPeerCidrs.ToArray() }:null };
        }
    }

    /// <summary>Completes after stop and all product callbacks have actually drained. A noncooperative callback keeps ownership alive.</summary>
    public Task Completion=>completion;

    public static async Task<CortexMcpHostController> StartForInstallRootAsync(string installRoot,CortexMcpOwner currentOwner,
        Func<CancellationToken,Task<IMcpDispatcher>> admittedDispatcherFactory,CancellationToken stopToken,
        ICortexMcpCertificateResolver? certificateResolver=null)
    {
        RequireOwner(currentOwner);
        ArgumentNullException.ThrowIfNull(admittedDispatcherFactory);
        stopToken.ThrowIfCancellationRequested();
        CortexMcpSettings settings;
        try { settings=CortexMcpSettingsStore.ReadForInstallRoot(installRoot); }
        catch(Exception error)when(IsOperationalFailure(error))
        {
            var unavailable=new CortexMcpHostController(null,currentOwner);
            unavailable.SetState(CortexMcpHostStatus.Unavailable,"settings_unavailable:"+error.GetType().Name);
            return unavailable;
        }
        return await StartAsync(settings,currentOwner,admittedDispatcherFactory,stopToken,certificateResolver,
            new CortexMcpRequestAuthenticator(installRoot)).ConfigureAwait(false);
    }

    public static async Task<CortexMcpHostController> StartAsync(CortexMcpSettings settings,CortexMcpOwner currentOwner,
        Func<CancellationToken,Task<IMcpDispatcher>> admittedDispatcherFactory,CancellationToken stopToken,
        ICortexMcpCertificateResolver? certificateResolver=null,IMcpRequestAuthenticator? authenticator=null)
    {
        RequireOwner(currentOwner);
        ArgumentNullException.ThrowIfNull(admittedDispatcherFactory);
        stopToken.ThrowIfCancellationRequested();
        settings=CortexMcpSettingsStore.Validate(settings);
        var controller=new CortexMcpHostController(settings,currentOwner);
        // These branches precede even certificate resolution. Installed/running service state is irrelevant.
        if(!settings.Enabled) { controller.SetState(CortexMcpHostStatus.Disabled);return controller; }
        if(settings.Owner!=currentOwner)
        { controller.SetState(CortexMcpHostStatus.ConfiguredForOtherOwner);return controller; }
        try
        {
            if(authenticator is null)throw new CortexMcpHostingException("connection_authenticator_required");
            controller.certificates=(certificateResolver??new CortexMcpCertificateResolver()).Resolve(settings,currentOwner);
            RequireResolvedCertificatePins(settings,currentOwner,controller.certificates);
            stopToken.ThrowIfCancellationRequested();
            var admitted=await admittedDispatcherFactory(stopToken).ConfigureAwait(false)??
                throw new InvalidOperationException("The admitted MCP dispatcher factory returned no dispatcher.");
            controller.dispatcher=new CallbackDispatcher(admitted);
            stopToken.ThrowIfCancellationRequested();
            controller.listener=await SharedMcpServer.StartAsync(new McpServerOptions
            {
                BindAddress=IPAddress.Parse(settings.BindAddress),Port=settings.Port,
                AllowedPeerCidrs=settings.AllowedPeerCidrs.ToArray(),ServerName=settings.ServerName,
                ServerCertificate=controller.certificates.Server,QualificationAuthority=controller.certificates.Authority,
                Authenticator=authenticator
            },new Dictionary<string,IMcpDispatcher>(StringComparer.Ordinal) { [ProductKey]=controller.dispatcher },stopToken).ConfigureAwait(false);
            controller.SetState(CortexMcpHostStatus.Ready,effectiveOwner:currentOwner,
                endpoint:new Uri(controller.listener.Address,EndpointPath),qualification:controller.listener.LastTlsQualification);
            controller.completion=controller.StopWhenRequestedAsync();
            controller.stopRegistration=stopToken.UnsafeRegister(static value=>((CortexMcpHostController)value!).RequestStop(),controller);
            return controller;
        }
        catch(OperationCanceledException)when(stopToken.IsCancellationRequested)
        {
            await controller.ReleaseOwnedAsync().ConfigureAwait(false);
            throw;
        }
        catch(Exception error)when(IsOperationalFailure(error))
        {
            // The failed bind is never interpreted as an owned/effective listener.
            var addressInUse=IsAddressInUse(error);
            try { await controller.ReleaseOwnedAsync().ConfigureAwait(false); }
            catch(Exception cleanup)when(IsOperationalFailure(cleanup))
            {
                controller.SetState(CortexMcpHostStatus.ShutdownFailed,"startup_cleanup_failed:"+cleanup.GetType().Name);
                return controller;
            }
            controller.SetState(CortexMcpHostStatus.Unavailable,addressInUse?"address_in_use":
                error is CortexMcpHostingException hosting?hosting.Code:
                error is McpTlsQualificationException tls?"tls_qualification:"+tls.Code:"startup_failed:"+error.GetType().Name,
                pendingRestart:addressInUse);
            return controller;
        }
    }

    private async Task StopWhenRequestedAsync()
    {
        await stop.Task.ConfigureAwait(false);
        SetState(CortexMcpHostStatus.Stopping);
        try
        {
            await ReleaseOwnedAsync().ConfigureAwait(false);
            SetState(CortexMcpHostStatus.Stopped);
        }
        catch
        {
            SetState(CortexMcpHostStatus.ShutdownFailed,"shutdown_failed");
            throw;
        }
    }

    private async Task ReleaseOwnedAsync()
    {
        // Refuse new callbacks before closing HTTP; cancel existing callbacks, then
        // await their actual return before disposing the admitted dispatcher and certificates.
        dispatcher?.BeginClose();
        Exception? listenerFailure=null;
        try { if(listener is not null)await listener.DisposeAsync().ConfigureAwait(false); }
        catch(Exception error)when(IsOperationalFailure(error)) { listenerFailure=error; }
        finally
        {
            listener=null;
            try { if(dispatcher is not null)await dispatcher.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                dispatcher=null;
                certificates?.Dispose();certificates=null;
            }
        }
        if(listenerFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(listenerFailure).Throw();
    }

    /// <summary>Synchronously closes callback admission before an owner begins its shared scheduler drain.</summary>
    public void RequestStop()
    {
        dispatcher?.BeginClose();
        stop.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        try { await completion.ConfigureAwait(false); }
        finally
        {
            // Unregister after cleanup, including a reported cleanup failure.
            stopRegistration.Dispose();
        }
    }

    private void SetState(CortexMcpHostStatus status,string? issue=null,bool pendingRestart=false,
        CortexMcpOwner? effectiveOwner=null,Uri? endpoint=null,McpTlsQualificationResult? qualification=null)
    {
        lock(stateGate)state=state with { Status=status,Issue=issue,PendingRestart=pendingRestart,
            EffectiveOwner=effectiveOwner,Endpoint=endpoint,TlsQualification=qualification??state.TlsQualification };
    }

    private static void RequireOwner(CortexMcpOwner owner)
    {
        if(!Enum.IsDefined(owner))throw new ArgumentOutOfRangeException(nameof(owner));
    }

    internal static void RequireResolvedCertificatePins(CortexMcpSettings settings,CortexMcpOwner owner,CortexMcpCertificates resolved)
    {
        var selected=owner==CortexMcpOwner.Application?settings.ApplicationCertificate:settings.ServiceCertificate;
        if(selected is null||settings.CertificateAuthority is not { } ca)
            throw new CortexMcpHostingException("tls_configuration_missing");
        CortexMcpSettingsStore.RequireCertificateReference(selected,owner);
        CortexMcpSettingsStore.RequireCertificateReference(ca,null);
        if(owner==CortexMcpOwner.Service&&ca.Location!="LocalMachine")
            throw new CortexMcpHostingException("service_ca_machine_reference_required");
        if(!resolved.Server.GetCertHashString(HashAlgorithmName.SHA256).Equals(selected.CertificateId,StringComparison.OrdinalIgnoreCase)||
            !resolved.Authority.GetCertHashString(HashAlgorithmName.SHA256).Equals(ca.CertificateId,StringComparison.OrdinalIgnoreCase))
            throw new CortexMcpHostingException("certificate_reference_mismatch");
        if(!resolved.Server.HasPrivateKey)throw new CortexMcpHostingException("certificate_private_key_unavailable");
    }

    internal static bool IsAddressInUse(Exception error)
    {
        // Inspect causes only; do not classify every bind/I/O failure as another owner.
        for(Exception? cause=error;cause is not null;cause=cause.InnerException)
            if(cause is SocketException socket&&socket.SocketErrorCode==SocketError.AddressAlreadyInUse||
                cause is Microsoft.AspNetCore.Connections.AddressInUseException)return true;
        return false;
    }

    private static bool IsOperationalFailure(Exception error)=>error is not OutOfMemoryException and not StackOverflowException;

    /// <summary>Tracks initialize AND dispatch, including callbacks that ignore cancellation.</summary>
    internal sealed class CallbackDispatcher:IMcpDispatcher,IMcpSessionLifecycle
    {
        private readonly IMcpDispatcher inner;
        private readonly object gate=new();
        private readonly CancellationTokenSource lifetime=new();
        private readonly Lazy<Task> disposal;
        private TaskCompletionSource idle=CompletedIdle();
        private int active;
        private bool closing;

        internal CallbackDispatcher(IMcpDispatcher inner)
        {
            this.inner=inner??throw new ArgumentNullException(nameof(inner));
            disposal=new(DisposeCoreAsync,LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)=>
            InvokeAsync(token=>inner.InitializeAsync(token),cancellationToken);
        public Task<JsonObject> DispatchAsync(McpRequestContext context,JsonObject request,CancellationToken cancellationToken)=>
            InvokeAsync(token=>inner.DispatchAsync(context,request,token),cancellationToken);

        public async ValueTask SessionOpenedAsync(McpSessionContext context,CancellationToken cancellationToken)
        {
            if(inner is not IMcpSessionLifecycle lifecycle)return;
            await InvokeAsync(async token=>
            {
                await lifecycle.SessionOpenedAsync(context,token).ConfigureAwait(false);
                return new JsonObject();
            },cancellationToken).ConfigureAwait(false);
        }
        // Listener disposal awaits this cleanup before releasing the borrowed dispatcher.
        // Cleanup remains legal after BeginClose and has no cancellation token.
        public ValueTask SessionClosedAsync(McpSessionContext context,McpSessionCloseReason reason)=>
            inner is IMcpSessionLifecycle lifecycle?lifecycle.SessionClosedAsync(context,reason):ValueTask.CompletedTask;

        private async Task<JsonObject> InvokeAsync(Func<CancellationToken,Task<JsonObject>> callback,CancellationToken cancellationToken)
        {
            lock(gate)
            {
                if(closing)throw new ObjectDisposedException(nameof(CortexMcpHostController));
                if(active++==0)idle=new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
                return await callback(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock(gate)if(--active==0)idle.TrySetResult();
            }
        }

        internal void BeginClose()
        {
            lock(gate) { if(closing)return;closing=true; }
            try { lifetime.Cancel(); }
            catch(AggregateException) { /* Cancellation callbacks cannot skip sibling cancellation or the drain. */ }
            catch(ObjectDisposedException) { /* A concurrent completed idle drain already disposed the source. */ }
        }

        internal Task WhenIdle { get { lock(gate)return idle.Task; } }

        private async Task DisposeCoreAsync()
        {
            BeginClose();
            await WhenIdle.ConfigureAwait(false);
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { lifetime.Dispose(); }
        }
        public ValueTask DisposeAsync()=>new(disposal.Value);
        private static TaskCompletionSource CompletedIdle()
        {
            var source=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();return source;
        }
    }
}
