using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Local.Runtime;

namespace Pegasus.AgentOps.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // This fixed one-shot helper must not initialize the ordinary host,
        // data directories, database, tray, service registration or recovery.
        if (args.FirstOrDefault() == "--certificate-store-once")
            return args is ["--certificate-store-once", var ticket, var parent, var start] &&
                uint.TryParse(parent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parentPid)
                ? CortexCertificateStoreElevation.RunHelperAsync(ticket, parentPid, start, CancellationToken.None).GetAwaiter().GetResult() : 2;
        if (args.FirstOrDefault() == CortexMachineServerCertificateElevation.HelperFlag)
            return args is [CortexMachineServerCertificateElevation.HelperFlag, var machineTicket, var machineParent, var machineStart] &&
                uint.TryParse(machineParent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var machineParentPid)
                ? CortexMachineServerCertificateElevation.RunHelperAsync(machineTicket, machineParentPid, machineStart, CancellationToken.None).GetAwaiter().GetResult() : 2;
        if (args.FirstOrDefault() == CortexScheduledTaskElevation.HelperFlag)
            return args is [CortexScheduledTaskElevation.HelperFlag, var taskTicket, var taskParent, var taskStart] &&
                uint.TryParse(taskParent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var taskParentPid)
                ? CortexScheduledTaskElevation.RunHelperAsync(taskTicket, taskParentPid, taskStart, CancellationToken.None).GetAwaiter().GetResult() : 2;
        if (args.FirstOrDefault() == CortexWindowsServiceElevation.HelperFlag)
            return args is [CortexWindowsServiceElevation.HelperFlag, var serviceTicket, var serviceParent, var serviceStart] &&
                uint.TryParse(serviceParent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var serviceParentPid)
                ? CortexWindowsServiceElevation.RunHelperAsync(serviceTicket, serviceParentPid, serviceStart, CancellationToken.None).GetAwaiter().GetResult() : 2;
        if (args is ["--service-control", var action])
            return WindowsCortexServiceControl.RunElevated(action);
        if (args is ["--processor-policy", var selection])
            return CortexProcessorPolicyControl.RunElevated(selection);
        if (args.FirstOrDefault() == CortexMcpSetupControl.HelperFlag)
            return args is [CortexMcpSetupControl.HelperFlag,var setupOwner,var setupEnabled,var setupServer,
                var setupAuthority,var setupSid,var setupFingerprint]
                ? CortexMcpSetupControl.RunElevatedAsync(setupOwner,setupEnabled,setupServer,setupAuthority,
                    setupSid,setupFingerprint).GetAwaiter().GetResult() : 2;
        if (args.FirstOrDefault() == "--shared-mcp-settings")
            return args is ["--shared-mcp-settings", var mcpEnabled, var mcpOwner]
                ? CortexMcpSettingsControl.RunElevated(mcpEnabled, mcpOwner) : 2;
        if (args.FirstOrDefault() == "--shared-mcp-certificates")
            return args is ["--shared-mcp-certificates", var certificateOwner, var serverPin, var authorityPin, var certificateOwnerSid]
                ? CortexMcpSettingsControl.RunCertificateBindingElevated(certificateOwner, serverPin, authorityPin, certificateOwnerSid) : 2;
        if (args.FirstOrDefault() == "--exit")
        {
            if (args is not ["--exit"]) return 2;
            var result = CortexHostExitControl.RequestCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            return result.ExitCode;
        }
        if (args.FirstOrDefault() == CortexPayloadTieringClient.Flag)
        {
            // Only this fixed attach-only helper reads operator input. It does
            // not initialize an owner, database, tray or execution engine.
            using var input = new StreamReader(Console.OpenStandardInput(),
                new System.Text.UTF8Encoding(false, true), false, 1024, leaveOpen: true);
            var result = CortexPayloadTieringClient.RunAsync(args, input, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            return result.ExitCode;
        }
        // A transient control window, not a second tray or broker registration.
        if (args is ["--dashboard"])
        {
            var owner = LocalIdentity.Current();
            if (!IsOrdinaryUser(owner)) return 2;
            if (CortexHostUiControl.HasInstance(owner.Session))
                return CortexHostUiControl.ShowExistingAsync(owner, CancellationToken.None).GetAwaiter().GetResult() ? 0 : 3;
            ApplicationConfiguration.Initialize();
            Application.Run(new SettingsWindow());
            return 0;
        }
#if DEBUG
        if(args.Contains("--ui-smoke",StringComparer.Ordinal))return UiSmoke();
#endif
        if(args.Contains("--ui-provider-status-once",StringComparer.Ordinal))return UiProviderStatusOnceAsync().GetAwaiter().GetResult();
        if(args.Contains("--monitor-once",StringComparer.Ordinal))return MonitorOnceAsync().GetAwaiter().GetResult();
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (AgentOpsException error) when (error.Code is "maintenance_busy" or "maintenance_state_unavailable")
        {
            Console.Error.WriteLine("err=" + error.Code);
            if (Environment.UserInteractive)
                MessageBox.Show("Cortex is unavailable while maintenance is active or its status cannot be verified. Complete maintenance before starting Cortex.",
                    "Cortex has not started", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 3;
        }
    }

    private static async Task<int> UiProviderStatusOnceAsync()
    {
        var response=await UiAutomationClient.ExecuteAsync(
            new UiHostRequest(1,"host-ui-provider-status",UiOperation.Status),
            TimeSpan.FromSeconds(10),mutation:false,CancellationToken.None);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(response));
        return response.Ok?0:6;
    }

#if DEBUG
    private static int UiSmoke()
    {
        ApplicationConfiguration.Initialize();using var context=new TrayApplicationContext();using var timer=new System.Windows.Forms.Timer{Interval=300};timer.Tick+=(_,_)=>{timer.Stop();Application.ExitThread();};timer.Start();Application.Run(context);return 0;
    }
#endif

    private static async Task<int> MonitorOnceAsync()
    {
        await using var monitor=new RoutingDispatcher();var receipt=await monitor.DispatchAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","ls"}}));if(!receipt.Ok){Console.Error.WriteLine($"err={receipt.Err}");return receipt.ExitCode;}Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(receipt.Data));return 0;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var fixture=false;var retryFixture=false;var actual=LocalIdentity.Current();var declared=actual;var expectedService=new LocalPeer("S-1-5-18",0);
#if DEBUG
        fixture=args.Contains("--fixture-identity",StringComparer.Ordinal);
        retryFixture=args.Contains("--fixture-retry",StringComparer.Ordinal);fixture|=retryFixture;
        if(fixture)
        {
            declared=new LocalPeer(Environment.GetEnvironmentVariable("PAO_TRAY_DECLARED_SID")??actual.Sid,int.TryParse(Environment.GetEnvironmentVariable("PAO_TRAY_DECLARED_SESSION"),out var declaredSession)?declaredSession:actual.Session);
            expectedService=new LocalPeer(Environment.GetEnvironmentVariable("PAO_SERVICE_SID")??"S-1-5-18",int.TryParse(Environment.GetEnvironmentVariable("PAO_SERVICE_SESSION"),out var serviceSession)?serviceSession:0);
        }
#endif
        if (!fixture && !IsOrdinaryUser(actual))
        {
            Console.Error.WriteLine("Start Cortex from the signed-in user's ordinary session.");
            return 2;
        }
        using var single=CreateSingleInstance(fixture,actual.Session,out var created);
        if(!created)return await CortexHostUiControl.ShowExistingAsync(actual,CancellationToken.None)?0:3;
        var pipeName=$"pegasus-cortex-host-{actual.Session}-{Environment.ProcessId}";
        var executionLayout=fixture?null:CortexDataLocation.Load(CortexProductLayout.ResolveExecution());
        var stateRoot=fixture
            ? Path.GetFullPath(Environment.GetEnvironmentVariable("PAO_TRAY_STATE_ROOT")??Path.Combine(Path.GetTempPath(),"PegasusCortex","host-fixture"))
            : executionLayout!.UserExecutionDirectory;
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var maintenanceMode=fixture?new MaintenanceModeStore(Path.Combine(stateRoot,"private-maintenance"),protect:false):MaintenanceModeStore.OpenReader(requireProvisioned:PrivilegedServiceConfigured());
        // Read the durable barrier before directory protection, tool discovery,
        // SQLite initialization or recovery can write into owner state.
        UserHostStartup.RequireOpen(maintenanceMode);
        if(executionLayout is not null)_=CortexProcessorPolicy.ApplyForInstallRoot(executionLayout.InstallRoot);
        var admission = new UserHostAdmission();
        TrayApplicationContext? activeTray = null;
        await using var engine = executionLayout is null
            ? new AgentOpsEngine(stateRoot,pathNormalizer:null,directOnly:false,bundledNsis:null,enableUserApplications:true,systemTools:null,maintenanceMode:maintenanceMode,userHostAdmission:admission)
            : UserHostStartup.CreateOwnerEngine(executionLayout, maintenanceMode, admission);
        using var elevationSession = executionLayout is null ? null : new OwnerElevationSession(
            actual.Sid, actual.Session, CortexOwnerElevation.HostPath(executionLayout),
            new WindowsOwnerElevationLauncher(executionLayout, actual));
        using var certificateElevation = elevationSession is null ? null : new OwnerCertificateStoreElevation(elevationSession);
        if (certificateElevation is not null) engine.BindCertificateStoreElevation(certificateElevation);
        using var taskElevation = elevationSession is null ? null : new OwnerScheduledTaskElevation(elevationSession);
        if (taskElevation is not null) engine.BindScheduledTaskElevation(taskElevation);
        using var serviceElevation = elevationSession is null ? null : new OwnerWindowsServiceElevation(elevationSession);
        if (serviceElevation is not null) engine.BindWindowsServiceElevation(serviceElevation, actual.Sid);
        var exit = new CortexRuntimeExit(admission, engine.UserExitBlockerAsync,
            CortexServiceExitGate.StopForExitAsync, () => stop.Cancel());
        var exitControl = new CortexHostExitControl(actual, exit);
        await using var payloadTiering = new CortexPayloadTieringControl(actual, admission, engine, maintenanceMode);
        var optionalBootstrap=BootstrapWhenOpenAsync();
        async Task BootstrapWhenOpenAsync()
        {
            while(!stop.IsCancellationRequested)
            {
                if(await engine.TryRunStartupEffectAsync(async ct=>
                {
                    try{_ = await PowerShell7ToolBootstrap.EnsureAsync(stateRoot,ct);}
                    catch(AgentOpsException ex){Console.Error.WriteLine($"err=pwsh_unconfigured detail={ex.Code}");}
                    if(fixture)return null;
                    try{return await BundledNsisTool.AdmitAsync(executionLayout!.InstallRoot,stateRoot,ct);}
                    catch(AgentOpsException ex){Console.Error.WriteLine($"err=nsis_unconfigured detail={ex.Code}");return null;}
                },stop.Token))return;
                await Task.Delay(TimeSpan.FromSeconds(1),stop.Token);
            }
        }
        var userHost=AuthenticatedPipeHost.RunOperationsAsync(
            CortexProductIdentity.UserExecutionPipe(actual.Session),
            actual.Sid,
            (request,peer,ct)=>ExecuteUserRequestAsync(engine,actual,request,peer,ct,ShowTrayAsync,exitControl,payloadTiering),
            stop.Token, replied: null, replyAttemptSettled: exitControl.ReplyAttemptSettled);
        async Task<bool> ShowTrayAsync(CancellationToken ct)
        {
            var context = Volatile.Read(ref activeTray);
            return admission.Accepting && context is not null && await context.ShowUserInterfaceAsync(ct);
        }
        var ownerMcpGateway = new CortexOwnerMcpGateway();
        var serviceRoute=AuthenticatedPipeHost.RunOperationsAsync(pipeName,expectedService.Sid,async (request,peer,ct)=>
        {
            if (!peer.SameIdentity(expectedService))
                return new OperationReceipt(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",request.Op,"auth");
            if (request.Op == MaintenanceProbeProtocol.Operation)
                return MaintenanceProbeProtocol.Handle(engine,OwnerAdminExecution.HostIdentity(actual),request);
            if (CortexOwnerMcpGateway.IsServiceRelay(request))
                return await ownerMcpGateway.ExecuteServiceRequestAsync(request, ct);
            return await engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct);
        },stop.Token);
        await Task.Delay(50);
        if(fixture&&!retryFixture){var registration=await TryRegisterAsync(new TrayRegistration(declared.Sid,declared.Session,pipeName),expectedService,stop.Token);if(!registration.Ok){stop.Cancel();try{await Task.WhenAll(userHost,serviceRoute,optionalBootstrap);}catch(OperationCanceledException){}Console.Error.WriteLine($"err={registration.Err}");return 4;}try{await Task.WhenAll(userHost,serviceRoute,optionalBootstrap);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}return 0;}
        var registrationLoop=RegisterUntilStoppedAsync(new TrayRegistration(declared.Sid,declared.Session,pipeName),expectedService,stop.Token);
        if(retryFixture){try{await registrationLoop;}catch(OperationCanceledException)when(stop.IsCancellationRequested){}return 0;}
        var personalDiagnostics = new CortexPersonalProfileDiagnostics(executionLayout!.UserLogDirectory);
        var personalProfile = new PersonalProfileAvailability(Environment.UserName, personalDiagnostics.Report);
        var personalStatus = PersonalProfilePipeHost.RunAsync(actual, personalProfile, stop.Token);
        var semanticState = "initializing";
        var semantics = Pegasus.Cortex.Mcp.Local.Runtime.CortexResidentRuntime.RunCurrentAsync(admission,
            value => Volatile.Write(ref semanticState, value), ownerMcpGateway, payloadTiering, stop.Token);
        var trayUi = TrayUiSupervisor.RunAsync(
            async () =>
            {
                try
                {
                    await TrayUiThread.RunAsync(() =>
                    {
                        var context = new TrayApplicationContext(personalProfile, exit.RequestAsync, () =>
                        {
                            var profileState = personalProfile.Snapshot();
                            return new CortexUserPresentationState(admission.Accepting ? "running" : "stopping",
                                profileState.State, profileState.Profile, admission.Accepting ? Volatile.Read(ref semanticState) : "unavailable");
                        }, beginCertificatePasswordOperation: engine.BeginLocalCertificatePasswordOperation,
                            beginMcpSetupOperation: engine.BeginLocalMcpSetupOperation);
                        Volatile.Write(ref activeTray, context);
                        return context;
                    }, stop.Token);
                }
                finally { Volatile.Write(ref activeTray, null); personalProfile.UiUnavailable(); }
            },
            stop.Token,
            ReportTrayFailure);
        try
        {
            // The tray is a configuration surface, not the owner-context execution
            // broker. Closing it must leave the user pipe, applications, and personal
            // profile status pipe alive for PAO and Cortex MCP clients.
            await Task.WhenAll(userHost,serviceRoute,registrationLoop,personalStatus,semantics,optionalBootstrap);
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        finally
        {
            personalProfile.UiUnavailable();
            stop.Cancel();
            try { await trayUi; }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        }
        return 0;
    }

    private static bool IsOrdinaryUser(LocalPeer actual)
    {
        if (actual.Session <= 0 || actual.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return !new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool PrivilegedServiceConfigured()
    {
        try
        {
            using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+CortexProductIdentity.ServiceName,writable:false);
            return key is not null;
        }
        catch(System.Security.SecurityException){return true;}
        catch(UnauthorizedAccessException){return true;}
    }

    private static Task<OperationReceipt> ExecuteUserRequestAsync(
        AgentOpsEngine engine,
        LocalPeer owner,
        OperationRequest request,
        LocalPeer peer,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>> showUi,
        CortexHostExitControl exitControl,
        CortexPayloadTieringControl payloadTiering)
    {
        if (!peer.SameIdentity(owner))
            return Task.FromResult(Fail(request, "auth"));
        // This completes an already admitted owner job while the public gate is
        // closing. The coordinator independently checks the exact High child.
        if (CertificateStoreElevationContract.IsControlRequest(request) || MachineServerCertificateElevationContract.IsControlRequest(request))
            return engine.HandleCertificateElevationAsync(request, peer, cancellationToken);
        if (ScheduledTaskElevationContract.IsControlRequest(request))
            return engine.HandleScheduledTaskElevationAsync(request, peer, cancellationToken);
        if (WindowsServiceElevationContract.IsControlRequest(request))
            return engine.HandleWindowsServiceElevationAsync(request, peer, cancellationToken);
        if (request.Op == CortexHostUiControl.Operation)
            return CortexHostUiControl.HandleAsync(request, owner, peer, showUi, cancellationToken);
        if (request.Op == CortexHostExitControl.Operation)
            return exitControl.HandleAsync(request, peer, cancellationToken);
        if (request.Op == CortexPayloadTieringControl.Operation)
            return payloadTiering.HandleAsync(request, peer, cancellationToken);
        if (request.Root is not null || request.Op is "root" or "queue")
            return Task.FromResult(Fail(request, "service_unavailable"));
        return engine.ExecuteAsync(request, cancellationToken);
    }

    private static OperationReceipt Fail(OperationRequest request,string error) =>
        new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),error);

    private static void ReportTrayFailure(Exception exception)
    {
        var now=DateTimeOffset.UtcNow;
        var line=$"{now:O} event=tray_ui_restart type={exception.GetType().Name} hresult=0x{exception.HResult:X8}{Environment.NewLine}";
        Console.Error.Write(line);
        try
        {
            var executionLayout=CortexDataLocation.Load(CortexProductLayout.ResolveExecution());
            CortexUserStateDirectories.EnsureExecution(executionLayout);
            var directory=executionLayout.UserLogDirectory;
            var path=Path.Combine(directory,"execution-host.log");
            CortexLogRotation.RotateIfNeeded(path,Path.Combine(directory,"execution-host.previous.log"),256*1024,now);
            File.AppendAllText(path,line);
        }
        catch
        {
            // A logging failure must never terminate the execution host or its UI recovery loop.
        }
    }

    private static Mutex? CreateSingleInstance(bool fixture,int session,out bool created)
    {
        if(fixture){created=true;return null;}return new Mutex(true,CortexHostUiControl.MutexName(session),out created);
    }

    private static async Task<TrayRegistrationResult> RegisterAsync(TrayRegistration registration,LocalPeer expectedService,CancellationToken cancellationToken)
    {
        await using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",NamedPipeDispatcher.TrayRegistrationPipeName,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(2));await pipe.ConnectAsync(timeout.Token);if(!LocalIdentity.CaptureExpectedServer(pipe,expectedService).SameIdentity(expectedService))return new(false,"auth");await BinaryProtocol.WriteAsync(pipe,registration,timeout.Token);return await BinaryProtocol.ReadAsync<TrayRegistrationResult>(pipe,timeout.Token);
    }

    private static async Task<TrayRegistrationResult> TryRegisterAsync(TrayRegistration registration,LocalPeer expectedService,CancellationToken cancellationToken)
    {
        try{return await RegisterAsync(registration,expectedService,cancellationToken);}
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested){return new(false,"svc_down");}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or AgentOpsException){return new(false,ex is AgentOpsException agent?agent.Code:"svc_down");}
    }

    private static async Task RegisterUntilStoppedAsync(TrayRegistration registration,LocalPeer expectedService,CancellationToken cancellationToken)
    {
        while(!cancellationToken.IsCancellationRequested)
        {
            var result=await TryRegisterAsync(registration,expectedService,cancellationToken);var delay=result.Ok?TimeSpan.FromSeconds(5):TimeSpan.FromSeconds(1);
            try{await Task.Delay(delay,cancellationToken);}catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){break;}
        }
    }
}
