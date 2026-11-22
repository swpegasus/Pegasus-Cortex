using System.IO.Pipes;
using System.Runtime.Versioning;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

public interface IOperationDispatcher : IAsyncDisposable
{
    Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default);
}

public sealed class InProcessDispatcher : IOperationDispatcher
{
    private readonly AgentOpsEngine _engine;
    public InProcessDispatcher(string? stateRoot = null) => _engine = new AgentOpsEngine(stateRoot);
    public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default) => _engine.ExecuteAsync(request, cancellationToken);
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}

[SupportedOSPlatform("windows")]
public sealed class NamedPipeDispatcher : IOperationDispatcher
{
    public const string UserPipeName = CortexProductIdentity.ExecutionUserPipe;
    public const string SystemPipeName = CortexProductIdentity.ExecutionSystemPipe;
    public const string TrayRegistrationPipeName = CortexProductIdentity.HostRegistrationPipe;
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly LocalPeer? _expectedServer;
    private readonly MaintenanceHostIdentity? _expectedProcess;

    public NamedPipeDispatcher(string pipeName, TimeSpan? connectTimeout = null,LocalPeer? expectedServer=null)
    { _pipeName = pipeName; _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);_expectedServer=expectedServer; }

    internal NamedPipeDispatcher(string pipeName,LocalPeer expectedServer,MaintenanceHostIdentity expectedProcess)
        :this(pipeName,expectedServer:expectedServer){_expectedProcess=expectedProcess;}

    public async Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if(request.Op=="terminal"&&string.IsNullOrWhiteSpace(request.Rid)) request=request with{Rid=Guid.NewGuid().ToString("N")};
        var connected = false;
        var sent = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(_connectTimeout);
                await pipe.ConnectAsync(connectTimeout.Token);
            }
            connected = true;
            if(_expectedServer is not null&&!LocalIdentity.CaptureExpectedServer(pipe,_expectedServer).SameIdentity(_expectedServer))throw new AgentOpsException("auth");
            if(_expectedProcess is not null&&OwnerAdminExecution.HostIdentity(LocalIdentity.CaptureServer(pipe))!=_expectedProcess)throw new AgentOpsException("auth");
            // Once submission starts, any transport failure has unknown completion.
            sent = true;
            await BinaryProtocol.WriteAsync(pipe, request, cancellationToken);
            return await BinaryProtocol.ReadAsync<OperationReceipt>(pipe, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch(AgentOpsException ex)when(ex.Code=="auth"){return new OperationReceipt(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",request.Op,"auth");}
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            var code = connected && sent ? "recovery_required" : "svc_down";
            return new OperationReceipt(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), code == "recovery_required" ? "attention" : "failed", request.Op, code, Partial: sent);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[SupportedOSPlatform("windows")]
public sealed class RoutingDispatcher:IOperationDispatcher
{
    private static LocalPeer ExpectedService(){
#if DEBUG
        if(Environment.GetEnvironmentVariable("PAO_TEST_IDENTITY_FIXTURE")=="1"&&Environment.GetEnvironmentVariable("PAO_TEST_SERVICE_SID") is {Length:>0} sid&&int.TryParse(Environment.GetEnvironmentVariable("PAO_TEST_SERVICE_SESSION"),out var session))return new LocalPeer(sid,session);
#endif
        return new LocalPeer("S-1-5-18",0);
    }
    private readonly IOperationDispatcher _userHost;
    private readonly IOperationDispatcher _serviceUser;
    private readonly IOperationDispatcher _system;

    public RoutingDispatcher()
    {
        var owner = LocalIdentity.Current();
        _userHost = new NamedPipeDispatcher(
            CortexProductIdentity.UserExecutionPipe(owner.Session),
            connectTimeout: TimeSpan.FromMilliseconds(100),
            expectedServer: owner);
        _serviceUser = new NamedPipeDispatcher(
            NamedPipeDispatcher.UserPipeName,
            connectTimeout: TimeSpan.FromMilliseconds(750),
            expectedServer: ExpectedService());
        _system = new NamedPipeDispatcher(
            NamedPipeDispatcher.SystemPipeName,
            expectedServer: ExpectedService());
    }

    internal RoutingDispatcher(
        IOperationDispatcher userHost,
        IOperationDispatcher serviceUser,
        IOperationDispatcher system)
    {
        _userHost = userHost;
        _serviceUser = serviceUser;
        _system = system;
    }

    public async Task<OperationReceipt> DispatchAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if(request.Root is not null)
            return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
                "failed",OperationNames.Public(request),"root_codes_removed");
        // A PAO-owned external process already occupies the one mutation worker.
        // Reject a descendant mutation before pipe submission instead of letting
        // parent and child wait on one another until an unrelated timeout.
        if (AgentOpsExecutionContext.RejectsNestedMutation(request))
            return new(
                false,
                Guid.NewGuid().ToString("N"),
                request.Rid ?? Guid.NewGuid().ToString("N"),
                "failed",
                OperationNames.Public(request),
                "reentrant_mutation");

        // Protected work belongs to the LocalSystem broker, which independently
        // checks the current connection grant and the actual registered Host peer.
        // None of these paths depends on the tray UI.
        // Application lifetime belongs exclusively to this authenticated signed-in host.
        // An absent/older host is not permission to enqueue a deferred launch in the service.
        if(request.Op is "app" or "terminal" or "registry")
        {
            if(request.System)return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"user_dispatch_required");
            return await DispatchToUserHostAsync(request,cancellationToken);
        }
        if (request.Op == "admin" && !request.System) return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"full_control_required");
        if (request.System)
        {
            if(request.ConnectionAuthority is null)return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"full_control_required");
            return await _system.DispatchAsync(request, cancellationToken);
        }
        if(request.Op=="root")return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed","root","root_codes_removed");
        if (request.Op is "queue" or "application_access")
            return await _serviceUser.DispatchAsync(request, cancellationToken);

        // Ordinary owner-context work runs only in the durable signed-in user
        // host. Its absence is not permission to queue the request through the
        // privileged service or make tray approval an execution dependency.
        return await DispatchToUserHostAsync(request, cancellationToken);
    }

    private async Task<OperationReceipt> DispatchToUserHostAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        var direct = await _userHost.DispatchAsync(request, cancellationToken);
        // OFF is intentional. A client may attach to the owner host, but may
        // never create it or resend an ambiguously submitted operation.
        return direct.Err == "svc_down" ? UserHostUnavailable(direct) : direct;
    }

    private static OperationReceipt UserHostUnavailable(OperationReceipt receipt) =>
        receipt with { Err = "user_host_unavailable", State = "failed", Partial = false };

    public async ValueTask DisposeAsync()
    {
        await _userHost.DisposeAsync();
        await _serviceUser.DisposeAsync();
        await _system.DisposeAsync();
    }
}

[SupportedOSPlatform("windows")]
public static class PipeHost
{
    public static async Task RunAsync(string pipeName, AgentOpsEngine engine, CancellationToken cancellationToken)
        => await AuthenticatedPipeHost.RunOperationsAsync(pipeName,LocalIdentity.Current().Sid,(request,_,ct)=>engine.ExecuteAsync(request,ct),cancellationToken);
}

[SupportedOSPlatform("windows")]
public static class AuthenticatedPipeHost
{
    public static Task RunOperationsAsync(string pipeName,string ownerSid,Func<OperationRequest,LocalPeer,CancellationToken,Task<OperationReceipt>> handler,CancellationToken cancellationToken)
        =>RunAsync<OperationRequest,OperationReceipt>(pipeName,ownerSid,handler,cancellationToken);

    internal static Task RunOperationsAsync(string pipeName,string ownerSid,Func<OperationRequest,LocalPeer,CancellationToken,Task<OperationReceipt>> handler,CancellationToken cancellationToken,Action<OperationReceipt> replied)
        =>RunAsync<OperationRequest,OperationReceipt>(pipeName,ownerSid,handler,cancellationToken,replied);

    // Separate from successful-write notification: an owner Exit must settle
    // even when its client disconnects after commit. Service COMMIT keeps using
    // the successful-write-only overload above.
    internal static Task RunOperationsAsync(string pipeName,string ownerSid,Func<OperationRequest,LocalPeer,CancellationToken,Task<OperationReceipt>> handler,CancellationToken cancellationToken,Action<OperationReceipt>? replied,Action<OperationReceipt> replyAttemptSettled)
        =>RunAsync<OperationRequest,OperationReceipt>(pipeName,ownerSid,handler,cancellationToken,replied,replyAttemptSettled);

    public static Task RunRegistrationsAsync(string pipeName,string ownerSid,Func<TrayRegistration,LocalPeer,CancellationToken,Task<TrayRegistrationResult>> handler,CancellationToken cancellationToken)
        =>RunAsync<TrayRegistration,TrayRegistrationResult>(pipeName,ownerSid,handler,cancellationToken);

    // An isolated fixture can block the synchronous capture prefix.
    // Production callers never supply this worker-only barrier.
    internal static Task RunOperationsAsync(string pipeName,string ownerSid,Func<OperationRequest,LocalPeer,CancellationToken,Task<OperationReceipt>> handler,CancellationToken cancellationToken,Action beforeCapture)
        =>RunAsync<OperationRequest,OperationReceipt>(pipeName,ownerSid,handler,cancellationToken,beforeCapture:beforeCapture);

    private static async Task RunAsync<TRequest,TResponse>(string pipeName,string ownerSid,Func<TRequest,LocalPeer,CancellationToken,Task<TResponse>> handler,CancellationToken cancellationToken,Action<TResponse>? replied=null,Action<TResponse>? replyAttemptSettled=null,Action? beforeCapture=null)
    {
        using var connections = new SemaphoreSlim(32, 32);
        var handlers = new HashSet<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await connections.WaitAsync(cancellationToken);
                NamedPipeServerStream? pipe = null;
                var handlerOwnsConnection = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pipe = LocalPipeFactory.Create(pipeName,ownerSid);
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    var acceptedPipe = pipe;
                    // Capture, buffered reads and an ordinary handler can all run
                    // synchronously. Transfer their bounded ownership before any
                    // of that work so it cannot hold up the next listener.
                    var connectionTask = Task.Run(async () =>
                    {
                        try { await HandleAsync(acceptedPipe); }
                        finally { connections.Release(); }
                    }, CancellationToken.None);
                    handlerOwnsConnection = true;
                    lock (handlers) handlers.Add(connectionTask);
                    _ = connectionTask.ContinueWith(t => { lock (handlers) handlers.Remove(t); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                finally
                {
                    if (!handlerOwnsConnection)
                    {
                        try { if (pipe is not null) await pipe.DisposeAsync(); }
                        finally { connections.Release(); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Capacity-wait cancellation and listener creation/accept failures
            // still join every transferred owner before disposing the semaphore.
            Task[] remaining; lock (handlers) remaining = handlers.ToArray();
            await Task.WhenAll(remaining);
        }

        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                try
                {
                    beforeCapture?.Invoke();
                    var peer=LocalIdentity.Capture(pipe);
                    var request = await BinaryProtocol.ReadAsync<TRequest>(pipe, cancellationToken);
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var disconnected = MonitorDisconnectAsync(pipe, requestCancellation);
                    try
                    {
                        var receipt = await handler(request,peer,requestCancellation.Token);
                        try
                        {
                            if (pipe.IsConnected)
                            {
                                await BinaryProtocol.WriteAsync(pipe, receipt, cancellationToken);
                                replied?.Invoke(receipt);
                            }
                        }
                        finally { replyAttemptSettled?.Invoke(receipt); }
                    }
                    finally
                    {
                        requestCancellation.Cancel();
                        try { await disconnected; } catch (OperationCanceledException) { }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (AgentOpsException ex) { Console.Error.WriteLine($"pipe_diag={ex.Code}"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { Console.Error.WriteLine($"pipe_diag={ex.GetType().Name}"); }
                catch (Exception ex) { var message=ex.Message.Replace('\r',' ').Replace('\n',' ');Console.Error.WriteLine($"pipe_diag=internal type={ex.GetType().Name} msg={message[..Math.Min(message.Length,120)]}"); }
            }
        }

        static async Task MonitorDisconnectAsync(NamedPipeServerStream pipe, CancellationTokenSource cancellation)
        {
            var probe = new byte[1];
            try { if (await pipe.ReadAsync(probe, cancellation.Token) == 0) cancellation.Cancel(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (IOException) { cancellation.Cancel(); }
        }
    }
}
