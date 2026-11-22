using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

[assembly: InternalsVisibleTo("Pegasus.AgentOps.Tray")]
[assembly: InternalsVisibleTo("Pegasus.AgentOps.Service")]
[assembly: InternalsVisibleTo("PegasusCortex.Host")]
[assembly: InternalsVisibleTo("PegasusCortex.ExecutionService")]
[assembly: InternalsVisibleTo("Pegasus.AgentOps.Tests")]

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine : IAsyncDisposable
{
    private const string AuthenticatedFrozenKey="__authenticated_frozen";
    private sealed record Queued(OperationRequest Request, string Jid, string Rid, long Sequence, TaskCompletionSource<OperationReceipt> Completion, CancellationTokenSource Execution);

    private readonly IMaintenanceModeSource _maintenanceMode;
    private readonly UserHostAdmission? _userHostAdmission;
    private readonly Dictionary<string, MaintenanceEffect> _activeEffects = new(StringComparer.Ordinal);
    private readonly AgentOpsPaths _paths;
    private readonly WorkspaceRegistry _workspaces;
    private readonly WindowsPathContextNormalizer _pathNormalizer;
    private readonly ToolRecipeOperations _tools;
    private readonly UserApplicationManager? _applications;
    private readonly FeedbackOperations _feedback;
    private readonly object _admissionGate = new();
    private readonly BoundedMutationScheduler _scheduler;
    private readonly ConcurrentDictionary<string, Queued> _queued = new(StringComparer.Ordinal);
    private long _recoverySequence;
    private readonly long _recoveryCeiling;
    private bool _recoveryPending;
    private bool _stopping;
    private readonly ConcurrentDictionary<string, DurableJob> _jobs;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OperationReceipt>> _completions = new(StringComparer.Ordinal);
    private sealed record ApplicationTransition(string Identity, bool Mutation, TaskCompletionSource<OperationReceipt> Completion);
    private readonly ConcurrentDictionary<string, ApplicationTransition> _applicationTransitions = new(StringComparer.Ordinal);
    private readonly SqliteJobStore _store;
    private readonly FileStream _ownerLock;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _directOnly;
    private RegisteredToolDefinition? _bundledNsis;
    private readonly AgentOpsSystemToolBindings? _systemTools;
    private readonly IWindowsServiceLifecycle _serviceLifecycle;
    private readonly IProcessThreadInventory? _threadInventory;
    private readonly Func<CortexMcpConnectionAuthority,bool,bool>? _connectionAuthorization;

    public AgentOpsEngine(
        string? stateRoot = null,
        WindowsPathContextNormalizer? pathNormalizer = null,
        bool directOnly = false,
        RegisteredToolDefinition? bundledNsis = null,
        bool enableUserApplications = false)
        : this(stateRoot, pathNormalizer, directOnly, bundledNsis, enableUserApplications, systemTools: null, serviceLifecycle: null)
    {
    }

    internal AgentOpsEngine(
        string? stateRoot,
        WindowsPathContextNormalizer? pathNormalizer,
        bool directOnly,
        RegisteredToolDefinition? bundledNsis,
        bool enableUserApplications,
        AgentOpsSystemToolBindings? systemTools,
        IWindowsServiceLifecycle? serviceLifecycle = null,
        IMaintenanceModeSource? maintenanceMode = null,
        UserHostAdmission? userHostAdmission = null,
        IProcessThreadInventory? threadInventory = null,
        WindowsServiceOperations? windowsServices = null,
        ICopyFileTransfer? copyFileTransfer = null,
        ZipOperations? zipOperations = null,
        Func<CortexMcpConnectionAuthority,bool,bool>? connectionAuthorization = null)
    {
        _maintenanceMode = maintenanceMode ?? (OperatingSystem.IsWindows()
            ? MaintenanceModeStore.OpenReader(requireProvisioned: false) : new UnavailableMaintenanceModeSource());
        _userHostAdmission = userHostAdmission;
        _threadInventory = threadInventory;
        _connectionAuthorization = connectionAuthorization;
        _copyFileTransfer = copyFileTransfer;
        _zipOperations = zipOperations ?? new ZipOperations();
        _windowsServices = windowsServices ?? new WindowsServiceOperations();
        _directOnly=directOnly;_bundledNsis=bundledNsis;_systemTools=systemTools;_serviceLifecycle=serviceLifecycle??new WindowsServiceLifecycle();
        _paths = new AgentOpsPaths(stateRoot);_pathNormalizer=pathNormalizer??new WindowsPathContextNormalizer();
        _workspaces = new WorkspaceRegistry(_paths,_pathNormalizer);_tools=new ToolRecipeOperations(_paths.StateRoot);
        _applications = enableUserApplications ? new UserApplicationManager(_tools) : null;
        _terminals = enableUserApplications ? new LocalTerminalManager(_tools) : null;
        try { _ownerLock = new FileStream(Path.Combine(_paths.StateRoot, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new AgentOpsException("state_owned", inner: ex); }
        _feedback=new FeedbackOperations(_paths.StateRoot);
        _store = new SqliteJobStore(_paths.StateRoot);
        _recoveryCeiling = _store.HighestRecoverableSequence();
        _jobs = new ConcurrentDictionary<string, DurableJob>(StringComparer.Ordinal);
        _scheduler = new BoundedMutationScheduler(RunQueuedAsync, capacityAvailable: RefillRecovery);
        lock (_admissionGate) PumpRecovery();
    }

    public async Task<OperationReceipt> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if(request.Op.StartsWith('_'))return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
            "failed",OperationNames.Public(request),"private_operation");
        if(request.Root is not null)return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
            "failed",OperationNames.Public(request),"root_codes_removed");
        // SYSTEM intent is admitted only by the authenticated service broker;
        // passing that intent to the ordinary engine cannot select its authority.
        if(request.System)return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
            "failed",OperationNames.Public(request),"user_dispatch_required");
        if (_directOnly)
            return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "precondition");
        using var admission = _userHostAdmission?.TryEnter();
        if (_userHostAdmission is not null && admission is null) return HostStopping(request);
        return await ExecuteAsync(request, cancellationToken, authenticatedFrozen: false);
    }

    // The tray uses this only after authenticating the service peer. It revalidates
    // service-frozen paths but deliberately performs no second durable claim: the
    // service broker already owns the global JID, sequence, and retry state.
    internal async Task<OperationReceipt> ExecuteAuthenticatedFrozenDirectAsync(OperationRequest request, CancellationToken cancellationToken = default, bool allowApplications = false)
    {
        using var admission = _userHostAdmission?.TryEnter();
        if (_userHostAdmission is not null && admission is null) return HostStopping(request, replayDurable: false);
        return await ExecuteFrozenAdmittedAsync(request, cancellationToken, allowApplications);
    }

    private async Task<OperationReceipt> ExecuteFrozenAdmittedAsync(OperationRequest request, CancellationToken cancellationToken, bool allowApplications)
    {
        // This fixed family owns its durable request in the service engine,
        // rather than duplicating its private body in the router job store.
        if (request.Op is "windows_service" or "windows_task") return await ExecuteAsync(request, cancellationToken, authenticatedFrozen: true);
        if (request.Op == "job" && request.Arg.GetValueOrDefault("__execution_context_system") == "1" &&
            request.Arg.GetValueOrDefault("op") is "stat" or "wait")
            return await ObserveSystemServiceJobAsync(request, cancellationToken);
        if(ProcessThreadOperations.IsRequest(request)) return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"user_dispatch_required");
        if(request.Op is "registry" or "certificate") return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"auth");
        if(request.Op=="terminal") return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"user_dispatch_required");
        if(request.Op=="app"&&(!allowApplications||_applications is null||request.Arg.GetValueOrDefault("op")=="release"))
            return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"user_dispatch_required");
        var jid=Guid.NewGuid().ToString("N");var rid=string.IsNullOrWhiteSpace(request.Rid)?Guid.NewGuid().ToString("N"):request.Rid;
        try
        {
            RequireReleaseRid(request);
            if (request.Arg.TryGetValue("wait", out var wait) &&
                (wait is not ("true" or "false") || !SupportsDeferredCompletion(request)))
                throw new AgentOpsException("invalid");
            if(request.Root is not null)throw new AgentOpsException("auth");
            if(request.Body is not null&&System.Text.Encoding.UTF8.GetByteCount(request.Body)>OperationRequest.MaxBodyBytes)throw new AgentOpsException("invalid");
            request=ValidateAuthenticatedFrozen(request);var trusted=new Dictionary<string,string>(request.Arg.Where(x=>x.Key!=AuthenticatedFrozenKey),StringComparer.Ordinal){{AuthenticatedFrozenKey,"1"}};request=request with{Arg=trusted};
        }
        catch(AgentOpsException ex){return new(false,jid,rid,"failed",request.Op,ex.Code,new Dictionary<string,object?>{{"del",ex.Deleted},{"cp",ex.Copied}},Partial:ex.Deleted>0||ex.Copied>0);}
        catch(UnauthorizedAccessException){return new(false,jid,rid,"failed",request.Op,"access");}
        catch(Exception ex)when(ex is ArgumentException or IOException){return new(false,jid,rid,"failed",request.Op,"invalid");}
        return await ExecuteReceiptAsync(request,jid,rid,0,cancellationToken,persist:false);
    }

    private async Task<OperationReceipt> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken, bool authenticatedFrozen)
    {
        if (request.Op == "windows_task")
        {
            try
            {
                var invocation = WindowsTaskOperationContract.Parse(authenticatedFrozen ? WindowsServicePublicShape(request) : request);
                var system = authenticatedFrozen && request.Arg.GetValueOrDefault("__execution_context_system") == "1";
                if (!OperatingSystem.IsWindows() || _windowsTasks?.IsCurrentContext() != true ||
                    system != (_windowsTasks.Authority == WindowsTaskAuthority.System) ||
                    system && !WindowsTaskOperationContract.IsSystemAction(invocation.Action) ||
                    invocation.RequiresOwnerElevation && authenticatedFrozen ||
                    !system && (authenticatedFrozen || _directOnly)) throw new AgentOpsException("auth");
            }
            catch (Exception error) when (error is AgentOpsException or WindowsTaskContractException)
            { return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request),
                error is AgentOpsException known ? known.Code : "invalid"); }
        }
        if (ProcessThreadOperations.IsRequest(request))
        {
            // Validate the original request before generic projection can erase
            // forbidden authority, workspace or deferred-execution fields.
            try { _ = ProcessThreadOperations.ParseRequest(request); }
            catch (AgentOpsException error)
            { return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), error.Code); }
        }
        if (request.Op == "windows_service")
        {
            try
            {
                var invocation = WindowsServiceOperationContract.Parse(authenticatedFrozen ? WindowsServicePublicShape(request) : request);
                var system = authenticatedFrozen && request.Arg.GetValueOrDefault("__execution_context_system") == "1";
                if (invocation.RequiresOwnerElevation
                    ? authenticatedFrozen || _directOnly || _windowsServiceElevation?.Owner.IsCurrentOwner() != true
                    : invocation.IsMutation && !system ||
                        !invocation.IsMutation && authenticatedFrozen && (invocation.Action != "read" || !system))
                    throw new AgentOpsException("auth");
            }
            catch (AgentOpsException error)
            { return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), error.Code); }
        }
        if (request.Op == "connection_profile")
        {
            try { _ = ConnectionProfileOperationContract.Parse(request); }
            catch (ConnectionProfileContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed",
                    OperationNames.Public(request), "invalid", new Dictionary<string, object?> { ["field"] = error.Field });
            }
            if (!OperatingSystem.IsWindows() || authenticatedFrozen || _directOnly || _smbOwner?.IsCurrentOwner() != true)
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "windows_account")
        {
            try { _ = WindowsAccountOperationContract.Parse(request); }
            catch (WindowsAccountContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "invalid", new Dictionary<string, object?> { ["field"] = error.Field });
            }
            if (!OperatingSystem.IsWindows() || authenticatedFrozen || _directOnly || _windowsAccounts?.IsCurrentOwner() != true)
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "windows_network")
        {
            try
            {
                if (WindowsNetworkResolutionContract.IsRequest(request))
                    _ = WindowsNetworkResolutionContract.Parse(request);
                else _ = WindowsNetworkOperationContract.Parse(request);
            }
            catch (WindowsNetworkResolutionContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "invalid", new Dictionary<string, object?> { ["field"] = error.Field });
            }
            catch (WindowsNetworkContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "invalid", new Dictionary<string, object?> { ["field"] = error.Field });
            }
            if (!OperatingSystem.IsWindows() || authenticatedFrozen || _directOnly ||
                (WindowsNetworkResolutionContract.IsRequest(request)
                    ? _windowsNetworkResolution?.IsCurrentOwner() != true
                    : _windowsNetwork?.IsCurrentOwner() != true))
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "windows_device")
        {
            try { _ = WindowsDeviceOperationContract.Parse(request); }
            catch (WindowsDeviceContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "invalid", new Dictionary<string, object?> { ["field"] = error.Field });
            }
            if (!OperatingSystem.IsWindows() || authenticatedFrozen || _directOnly || _windowsDevices?.IsCurrentOwner() != true)
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "installed_program")
        {
            try { _ = WindowsInstalledProgramOperationContract.Parse(request); }
            catch (WindowsInstalledProgramContractException error)
            {
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "invalid",
                    new Dictionary<string, object?> { ["field"] = error.Field });
            }
            if (authenticatedFrozen || _directOnly || _registryOwner?.IsCurrentOwner() != true)
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "registry")
        {
            // Validate the original shape before generic path/body projection can
            // discard a forbidden field. Only normal owner composition binds this.
            try { _ = RegistryOperationContract.Parse(request); }
            catch (RegistryContractException error)
            { return RegistryReceiptAdapter.Invalid(request, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), error.Field); }
            if (authenticatedFrozen || _registryOwner is null || !_registryOwner.IsCurrentOwner())
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if (request.Op == "certificate")
        {
            try { _ = CertificateOperationContract.Parse(request); }
            catch (CertificateContractException error)
            { return CertificateReceiptAdapter.Invalid(request, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), error.Field); }
            if (authenticatedFrozen || _certificateOwner is null || !_certificateOwner.IsCurrentOwner())
                return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "auth");
        }
        if(request.Root is not null)return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
            "failed",OperationNames.Public(request),"root_codes_removed");
        var cleanArg=request.Arg.Where(x => x.Key is not ("root" or "stdin")&&x.Key!=OperationRequest.BodyOmittedKey&&x.Key!=AuthenticatedFrozenKey && !x.Key.StartsWith("env", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        if(request.Body is not null){if(System.Text.Encoding.UTF8.GetByteCount(request.Body)>OperationRequest.MaxBodyBytes)return new OperationReceipt(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),"invalid");cleanArg[OperationRequest.BodyOmittedKey]="1";}
        request = request with { Arg = cleanArg, PathRoots=authenticatedFrozen ? request.PathRoots : null };
        var rid = string.IsNullOrWhiteSpace(request.Rid) ? Guid.NewGuid().ToString("N") : request.Rid;
        var jid = Guid.NewGuid().ToString("N");
        try
        {
            RequireReleaseRid(request);
            if (request.Arg.TryGetValue("wait", out var wait) &&
                (wait is not ("true" or "false") || !SupportsDeferredCompletion(request)))
                throw new AgentOpsException("invalid");
            if(request.Op=="job"&&request.Arg.GetValueOrDefault("op")=="wait")
                return await WaitJobAsync(Required(request,"jid"),cancellationToken);
            request = authenticatedFrozen ? ValidateAuthenticatedFrozen(request) : FreezePaths(request);if(authenticatedFrozen){var trusted=new Dictionary<string,string>(request.Arg,StringComparer.Ordinal){{AuthenticatedFrozenKey,"1"}};request=request with{Arg=trusted};}
            RequireCertificateFileRoot(request);
        }
        catch(AgentOpsException ex)
        {
            return new OperationReceipt(false,jid,rid,"failed",OperationNames.Public(request),ex.Code,
                new Dictionary<string,object?>{{"del",ex.Deleted},{"cp",ex.Copied}},Partial:ex.Deleted>0||ex.Copied>0,
                Failure:FailureEvidence.Admission(ex,"request-admission"));
        }
        catch(UnauthorizedAccessException)
        { return new OperationReceipt(false,jid,rid,"failed",OperationNames.Public(request),"access",
            Failure:FailureEvidence.Generic("access",false,"request-admission")); }
        catch(Exception ex) when(ex is ArgumentException or IOException)
        { return new OperationReceipt(false,jid,rid,"failed",OperationNames.Public(request),"invalid",
            Failure:FailureEvidence.Generic("invalid",false,"request-admission")); }
        if(request.Op=="terminal") return await ExecuteTerminalRequestAsync(request,jid,rid,cancellationToken);
        // Health/monitor snapshots are not durable work. Authentication and input
        // admission still happen above; privileged broker policy is unchanged.
        if (request.Op == "windows_service" && request.Arg.GetValueOrDefault("elevate") != "uac" &&
            WindowsServiceOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")))
            return await ExecuteReceiptAsync(request, jid, rid, 0, cancellationToken, persist: false);
        // Resolution is active network I/O, but not durable/replayable work.
        // ExecuteReceiptAsync retains its live maintenance effect until the
        // original resolver and cancellation callback have actually settled.
        if (WindowsNetworkResolutionContract.IsRequest(request))
            return await ExecuteEphemeralWindowsNetworkResolutionAsync(request, jid, rid, cancellationToken);
        if (IsHistorySnapshot(request))
        {
            // Preserve replay of an already-durable RID, including receipts from
            // releases that retained health checks. New snapshots create no rows.
            if (request.Op is not ("registry" or "certificate" or "installed_program" or "connection_profile" or "windows_device" or "windows_network" or "windows_account" or "windows_task") && !ProcessThreadOperations.IsRequest(request) && !(request.Op=="app"&&request.Arg.GetValueOrDefault("op")=="inventory") &&
                request.Rid is not null && _store.FindByRid(rid) is { } previous)
                return HasPrivateReceipt(previous.Receipt) || WindowsServiceRequiresReplayMatch(request, previous.Receipt)
                    ? new(false, jid, rid, "failed", OperationNames.Public(request), "precondition") : previous.Receipt;
            return await ExecuteReceiptAsync(request, jid, rid, 0, cancellationToken, persist: false);
        }
        if(request.Op=="app")return await ExecuteApplicationRequestAsync(request with{Rid=rid},jid,rid,cancellationToken);
        var mutation = IsMutation(request);
        var deferred = request.Arg.GetValueOrDefault("wait") == "false";
        // Application transitions are manager-serialized, never recovered as queued launches.
        var initial = JobSubmissionProjection.Attach(WindowsServiceInitialReceipt(PrivateInitialReceipt(new OperationReceipt(true, jid, rid, mutation && request.Op!="app" ? "queued" : "running", OperationNames.Public(request)), request), request), request);
        var durableRequest=WindowsServiceDurableRequest(request);
        DurableJob claimed;
        Queued? item = null;
        lock (_admissionGate)
        {
            // RID replay precedes capacity admission. A retry must never execute a
            // second job, nor lose its accepted coordinates when the queue is full.
            var previous = _store.FindByRid(rid);
            if (previous is not null)
            {
                if (RequiresPrivateReplayMatch(request, previous.Receipt) && !PrivateReplayMatches(request, previous.Receipt) ||
                    WindowsServiceRequiresReplayMatch(request, previous.Receipt) && !WindowsServiceReplayMatches(request, previous.Receipt))
                    return new(false, jid, rid, "failed", OperationNames.Public(request), "precondition");
                claimed = previous;
            }
            else
            {
                if (mutation && MaintenanceClosed()) return MaintenanceBusy(request, jid, rid);
                if (mutation && _recoveryPending) PumpRecovery();
                if (cancellationToken.IsCancellationRequested)
                {
                    var canceled = _store.Claim(initial with { Ok = false, State = "canceled", Err = "canceled" }, durableRequest, mutation);
                    _store.Update(canceled with { Request = null });
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (_stopping || mutation && (_recoveryPending || !_scheduler.CanAccept(request)))
                    return new(false, jid, rid, "failed", OperationNames.Public(request), "queue_full",
                        Failure: FailureEvidence.Generic("queue_full", false, "bounded-admission"));
                claimed = _store.Claim(initial, durableRequest, mutation);
                // A lost cleanup result must not select another batch during
                // recovery. Only the live admission retains its exact request.
                if (request.Op == "job" && request.Arg.GetValueOrDefault("op") == "prune")
                {
                    claimed = claimed with { Request = null };
                    _store.Update(claimed);
                }
                _jobs.TryAdd(jid, claimed);
                if (mutation)
                {
                    // A deferred job belongs to the engine after durable admission,
                    // not to the transient client connection or its EOF token.
                    item = EnqueueClaimed(claimed, request, deferred ? CancellationToken.None : cancellationToken);
                }
            }
        }
        if (claimed.Receipt.Jid != jid)
        {
            if (RequiresPrivateReplayMatch(request, claimed.Receipt) && !PrivateReplayMatches(request, claimed.Receipt) ||
                WindowsServiceRequiresReplayMatch(request, claimed.Receipt) && !WindowsServiceReplayMatches(request, claimed.Receipt))
                return new(false, jid, rid, "failed", OperationNames.Public(request), "precondition");
            // A registry replay observes compact job state. Only the original
            // submission receives its live native result, never a history waiter.
            if (deferred || request.Op is "registry" or "certificate" or "windows_service" or "windows_task" or "connection_profile") return claimed.Receipt;
            if (_completions.TryGetValue(claimed.Receipt.Jid, out var active)) return await active.Task.WaitAsync(cancellationToken);
            return claimed.Receipt;
        }
        if (!mutation || request.Op=="app") return await ExecuteCoreAsync(request, jid, rid, claimed.Sequence, cancellationToken);
        if (deferred) return claimed.Receipt;
        using var cancellation = cancellationToken.Register(() => CancelQueued(jid));
        try { return await item!.Completion.Task.WaitAsync(cancellationToken); }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){CancelQueued(jid);throw;}
    }

    private Queued EnqueueClaimed(DurableJob claimed, OperationRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var item = new Queued(request, claimed.Receipt.Jid, claimed.Receipt.Rid, claimed.Sequence, completion, execution);
        _completions[item.Jid] = completion;
        _queued[item.Jid] = item;
        if (!_scheduler.TryEnqueue(item.Jid, item.Sequence, request, execution.Token))
        {
            // Defensive only: admission is locked and scheduler capacity can only
            // shrink here through disposal. Never leave an executable orphan row.
            var failed = Finish(claimed.Receipt with { Ok = false, State = "failed", Err = "queue_full" }, request, claimed.Sequence, true);
            completion.TrySetResult(failed);
            _queued.TryRemove(item.Jid, out _); _completions.TryRemove(item.Jid, out _); execution.Dispose();
        }
        return item;
    }

    private async Task RunQueuedAsync(string jid, CancellationToken cancellationToken)
    {
        if (!_queued.TryGetValue(jid, out var item)) return;
        MaintenanceLease? lease = null;
        var started = false;
        try
        {
            // A held original remains queued, including on shutdown. No mode
            // timeout, client cancellation, or shutdown fabricates its outcome.
            while (true)
            {
                lock (_admissionGate)
                {
                    if (_stopping)
                    {
                        item.Completion.TrySetCanceled();
                        return;
                    }
                    if (!MaintenanceClosed())
                    {
                        if (cancellationToken.IsCancellationRequested) CancelQueued(jid);
                        var running = JobSubmissionProjection.Attach(WindowsServiceInitialReceipt(PrivateInitialReceipt(new OperationReceipt(true, item.Jid, item.Rid, "running", OperationNames.Public(item.Request), Data: new Dictionary<string, object?> { ["seq"] = item.Sequence }), item.Request), item.Request), item.Request);
                        var runningJob = new DurableJob(running, true, item.Sequence, WindowsServiceDurableRequest(item.Request));
                        // The fresh mode read, live registration and queued CAS
                        // form one admission unit visible to the private probe.
                        lease = RegisterMaintenanceEffect(item.Request, jid, item.Rid, item.Sequence);
                        if (!_store.TryStartQueued(runningJob))
                        {
                            item.Completion.TrySetResult(_store.Find(jid)?.Receipt ?? new(false, jid, item.Rid, "failed", OperationNames.Public(item.Request), "not_found"));
                            return;
                        }
                        _jobs[jid] = runningJob;
                        started = true;
                        break;
                    }
                }
                try { await Task.Delay(50, _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { item.Completion.TrySetCanceled(); return; }
            }
            var receipt = await ExecuteReceiptAsync(item.Request, item.Jid, item.Rid, item.Sequence, cancellationToken, persist: true, maintenanceAdmitted: true);
            item.Completion.TrySetResult(receipt);
        }
        catch (Exception exception)
        {
            if (!started) { item.Completion.TrySetException(exception); return; }
            try
            {
                var failed = Finish(new(false, jid, item.Rid, "recovery_required", OperationNames.Public(item.Request), "recovery_required", Partial: true), item.Request, item.Sequence, true);
                item.Completion.TrySetResult(failed);
            }
            catch (Exception persistenceFailure)
            {
                item.Completion.TrySetException(new AgentOpsException("recovery_required", inner: persistenceFailure));
                throw;
            }
        }
        finally
        {
            lease?.Dispose();
            lock (_admissionGate)
            {
                _completions.TryRemove(jid, out _); _jobs.TryRemove(jid, out _); _queued.TryRemove(jid, out _);
                item.Execution.Dispose();
            }
        }
    }

    private void RefillRecovery()
    {
        lock (_admissionGate) PumpRecovery();
    }

    private async Task<OperationReceipt> ExecuteCoreAsync(OperationRequest req, string jid, string rid, long sequence, CancellationToken ct)
        => await ExecuteReceiptAsync(req,jid,rid,sequence,ct,persist:true);

    private async Task<OperationReceipt> ExecuteReceiptAsync(OperationRequest req, string jid, string rid, long sequence, CancellationToken ct,bool persist,string? knownAppAid=null,bool maintenanceAdmitted=false)
    {
        MaintenanceLease? acquired = null;
        if (IsMutation(req) && !maintenanceAdmitted)
        {
            lock (_admissionGate)
            {
                if (MaintenanceClosed()) return MaintenanceBusy(req, jid, rid);
                acquired = RegisterMaintenanceEffect(req, jid, rid, sequence);
            }
        }
        using var effect = acquired;
        var publicOp=OperationNames.Public(req);
        try
        {
            if(req.ConnectionAuthority is { } authority)
            {
                bool active;
                try{active=_connectionAuthorization?.Invoke(authority,req.System)==true;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or
                    System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException){active=false;}
                if(!active)throw new AgentOpsException("connection_revoked");
            }
            if (req.Op == "connection_profile") return await ExecuteConnectionProfileReceiptAsync(req, jid, rid, sequence, ct, persist);
            if (req.Op == "installed_program") return ExecuteInstalledProgramReceipt(req, jid, rid, ct);
            if (req.Op == "windows_device") return ExecuteWindowsDeviceReceipt(req, jid, rid, ct);
            if (req.Op == "windows_account") return ExecuteWindowsAccountReceipt(req, jid, rid, ct);
            if (WindowsNetworkResolutionContract.IsRequest(req))
                return await ExecuteWindowsNetworkResolutionReceiptAsync(req, jid, rid, ct);
            if (req.Op == "windows_network") return ExecuteWindowsNetworkReceipt(req, jid, rid, ct);
            if (req.Op == "windows_service") return await ExecuteWindowsServiceReceiptAsync(req, jid, rid, sequence, ct, persist);
            if (req.Op == "windows_task") return await ExecuteWindowsTaskReceiptAsync(req, jid, rid, sequence, ct, persist);
            if (req.Op == "registry") return ExecuteRegistryReceipt(req, jid, rid, sequence, ct, persist);
            if (req.Op == "certificate" && req.Arg.GetValueOrDefault("op") == "issue_machine_server")
                return await ExecuteMachineServerCertificateAsync(req, jid, rid, sequence, ct, persist);
            if (req.Op == "certificate") return CertificateOperationContract.IsAdministratorAction(req.Arg.GetValueOrDefault("op"))
                ? await ExecuteCertificateAdministrationAsync(req, jid, rid, sequence, ct, persist)
                : ExecuteCertificateReceipt(req, jid, rid, sequence, ct, persist);
            if (req.Op == "cp") return ExecuteCopyReceipt(req, jid, rid, sequence, ct, persist);
            IReadOnlyDictionary<string, object?> data = req.Op switch
            {
                "ctx" => Context(req),
                "stat" => Stat(ResolvePath(req, "src")),
                "ls" => List(ResolvePath(req, "src"), Int(req, "max", 48)),
                "find" => CompatibilityOperations.Find(ResolvePath(req,"src"),req.Arg.GetValueOrDefault("pattern"),req.Arg.GetValueOrDefault("type","any"),Int(req,"max",200),ct),
                "search" => CompatibilityOperations.Search(ResolvePath(req,"src"),Required(req,"pattern"),Bool(req,"recur"),Bool(req,"ignore_case"),req.Arg.GetValueOrDefault("fmt","literal")=="literal",req.Arg.GetValueOrDefault("include"),Int(req,"max",200),ct),
                "slice" => CompatibilityOperations.Slice(ResolvePath(req,"src"),req.Arg.GetValueOrDefault("mode","tail"),Int(req,"count",10),req.Arg.GetValueOrDefault("enc")),
                "diff" => CompatibilityOperations.Diff(ResolvePath(req,"src"),ResolvePath(req,"dst"),Int(req,"max",200)),
                "count" => CompatibilityOperations.Count(ResolvePath(req,"src")),
                "path" => CompatibilityOperations.PathInfo(ResolvePath(req,"src"),Required(req,"op")),
                "lines" => CompatibilityOperations.TransformLines(ResolvePath(req,"src"),Required(req,"op"),Bool(req,"ignore_case")),
                "disk" => CompatibilityOperations.Disk(ResolvePath(req,"src"),Required(req,"op"),ct),
                "rd" => ExtendedOperations.Read(ResolvePath(req,"src"),req.Arg.GetValueOrDefault("enc"),512*1024),
                "read_lines" => ExtendedOperations.ReadLines(ResolvePath(req,"src"),Int(req,"line",1),Int(req,"count",100),req.Arg.GetValueOrDefault("enc")),
                "encoding" => Encoding(req),
                "wr" => WriteText(req),
                "edit" => EditLines(req),
                "patch" => Patch(req),
                "mkdir" => MakeDirectory(ResolvePath(req, "dst")),
                "mv" => Move(req,ct),
                "rm" => Remove(req,ct),
                "zip" => Zip(req,ct),
                "sha" => Sha(ResolvePath(req, "src")),
                "run" => await RunRegisteredAsync(req,ct),
                "pwsh" => await PwshAsync(req,ct),
                "git" => await GitAsync(req,ct),
                "dotnet" => await DotnetAsync(req,ct),
                "tool" => await ToolAdminAsync(req,ct),
                "recipe" => RecipeAdmin(req),
                "do" => await RecipeAsync(req,ct),
                "7z" => await SevenZipAsync(req,ct),
                "proc" => await ProcessesAsync(req,ct),
                "service" => await ServiceAsync(req,ct),
                "net" => Listeners(req),
                "job" => Job(req),
                "feedback" => Feedback(req),
                "ui" => await UiAsync(req,ct),
                "nsis" => await NsisAsync(req,ct),
                "app" => await ApplicationAsync(req with{Rid=rid},ct,knownAppAid),
                "diag" => Diagnostics(),
                _ => throw new AgentOpsException("invalid")
            };
            var withSequence = new Dictionary<string, object?>(data) { ["seq"] = sequence };
            var receipt = new OperationReceipt(true, jid, rid, "done", publicOp, Data: withSequence);
            return Finish(receipt,req,sequence,persist);
        }
        catch (AgentOpsException ex)
        {
            if(req.Op=="app"&&ex.Data[UserApplicationManager.OperationEvidenceKey] is IReadOnlyDictionary<string,object?> applicationEvidence)
            {
                var evidence=new Dictionary<string,object?>(applicationEvidence){{"seq",sequence}};
                return Finish(new OperationReceipt(false,jid,rid,"attention",publicOp,ex.Code,evidence,Partial:true),req,sequence,persist);
            }
            var data = new Dictionary<string, object?> { ["del"] = ex.Deleted, ["cp"] = ex.Copied, ["seq"] = sequence };
            var partial = ex.Code == "copy_incomplete" || ex.Deleted > 0 || ex.Copied > 0;
            var receipt = new OperationReceipt(false, jid, rid, partial ? "attention" : "failed", publicOp, ex.Code, data, Partial: partial,
                Failure:ex.Failure ?? FailureEvidence.Generic(ex.Code,partial,"operation-engine"));
            return Finish(receipt,req,sequence,persist);
        }
        catch(OperationResultException ex)
        {
            var data=new Dictionary<string,object?>(ex.DataFields){{"seq",sequence}};var receipt=new OperationReceipt(false,jid,rid,ex.Partial?"attention":"failed",publicOp,ex.Code,data,Partial:ex.Partial,Failure:ex.Failure??FailureEvidence.Generic(ex.Code,ex.Partial,"operation-result","structured",LogAvailability(data)));return Finish(receipt,req,sequence,persist);
        }
        catch(UiAutomationException ex)
        {
            var data=new Dictionary<string,object?>{{"seq",sequence}};
            if(ex.Field is not null)data["field"]=ex.Field;
            var partial=ex.Partial||ex.RecoveryRequired;
            var receipt=new OperationReceipt(false,jid,rid,ex.RecoveryRequired?"recovery_required":partial?"attention":"failed",publicOp,ex.Code,data,Partial:partial);
            return Finish(receipt,req,sequence,persist);
        }
        catch (OperationCanceledException) when(!persist)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var receipt = new OperationReceipt(false, jid, rid, "canceled", publicOp, "canceled", new Dictionary<string, object?> { ["seq"] = sequence },
                Failure:FailureEvidence.Generic("canceled",false,"operation-cancellation"));
            return Finish(receipt,req,sequence,persist);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var code = ex is UnauthorizedAccessException ? "access" : ex is FileNotFoundException or DirectoryNotFoundException ? "not_found" : "invalid";
            var receipt = new OperationReceipt(false, jid, rid, "failed", publicOp, code, new Dictionary<string, object?> { ["seq"] = sequence },
                Failure:FailureEvidence.Generic(code,false,"operation-engine"));
            return Finish(receipt,req,sequence,persist);
        }
        catch (Win32Exception ex)
        {
            var code=ex.NativeErrorCode==5?"access":"external_failed";var receipt=new OperationReceipt(false,jid,rid,"failed",publicOp,code,new Dictionary<string,object?>{{"seq",sequence}},Failure:FailureEvidence.Generic(code,false,"win32-boundary"));return Finish(receipt,req,sequence,persist);
        }
        catch (Exception)
        {
            var receipt=new OperationReceipt(false,jid,rid,"failed",publicOp,"internal",new Dictionary<string,object?>{{"seq",sequence}},Failure:FailureEvidence.Generic("internal",false,"operation-engine"));return Finish(receipt,req,sequence,persist);
        }
    }

    private static string ApplicationRequestIdentity(OperationRequest request)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            Arguments=request.Arg.Where(pair=>pair.Key!="rid").OrderBy(pair=>pair.Key,StringComparer.Ordinal).ToArray(),
            // Release is host-scoped; a CLI-injected working directory cannot change its effect.
            request.Ws,Cwd=request.Arg.GetValueOrDefault("op")=="release"||request.Cwd is null?null:Path.GetFullPath(request.Cwd)
        });
        try{return Convert.ToHexStringLower(SHA256.HashData(bytes));}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }

    private static void RequireReleaseRid(OperationRequest request)
    {
        if(request.Op=="app"&&request.Arg.GetValueOrDefault("op")=="release"&&
            (string.IsNullOrWhiteSpace(request.Rid)||request.Rid.Length>128||request.Rid.Any(char.IsControl)))
            throw new AgentOpsException("invalid");
    }

    private async Task<OperationReceipt> ExecuteApplicationRequestAsync(OperationRequest request,string jid,string rid,CancellationToken cancellationToken)
    {
        var identity=ApplicationRequestIdentity(request);
        var mutation=IsMutation(request);
        var candidate=new ApplicationTransition(identity,mutation,new(TaskCreationOptions.RunContinuationsAsynchronously));
        ApplicationTransition active;
        DurableJob? claimed = null;
        MaintenanceLease? lease = null;
        lock (_admissionGate)
        {
            if (_applicationTransitions.TryGetValue(rid, out var existing)) active = existing;
            else
            {
                var previous = _store.FindByRid(rid);
                // Completed replay is metadata-only while closed; no manager
                // transition is installed and no launch/stop callback is made.
                if (mutation && MaintenanceClosed())
                    return previous is not null && previous.Receipt.Op == OperationNames.Public(request) &&
                        previous.Receipt.Data?.GetValueOrDefault("requestIdentity")?.ToString() == identity
                        ? previous.Receipt : MaintenanceBusy(request,jid,rid);
                active = candidate;
                _applicationTransitions[rid] = candidate;
                try
                {
                    var initial=new OperationReceipt(true,jid,rid,"running",OperationNames.Public(request));
                    claimed=previous ?? _store.Claim(initial,request with{Body=null},mutation);
                    if (claimed.Receipt.Jid == jid)
                    {
                        _jobs.TryAdd(jid,claimed);
                        if(mutation) lease = RegisterMaintenanceEffect(request,jid,rid,claimed.Sequence);
                    }
                }
                catch { _applicationTransitions.TryRemove(rid,out _); throw; }
            }
        }
        if(!ReferenceEquals(active,candidate))
        {
            if(active.Identity!=identity)return new(false,jid,rid,"failed",OperationNames.Public(request),"precondition");
            var completed=await active.Completion.Task.WaitAsync(cancellationToken);
            return await ReplayApplicationReceiptAsync(request,completed,cancellationToken);
        }
        try
        {
            var result=claimed!.Receipt.Jid==jid
                ?await ExecuteReceiptAsync(request,jid,rid,claimed.Sequence,cancellationToken,persist:true,maintenanceAdmitted:mutation)
                :await ReplayApplicationReceiptAsync(request,claimed.Receipt,cancellationToken);
            candidate.Completion.TrySetResult(result);
            return result;
        }
        catch(Exception exception){candidate.Completion.TrySetException(exception);throw;}
        finally
        {
            lock(_admissionGate) { lease?.Dispose(); _applicationTransitions.TryRemove(rid,out _); }
        }
    }

    private Task<OperationReceipt> ReplayApplicationReceiptAsync(OperationRequest request,OperationReceipt receipt,CancellationToken cancellationToken)
    {
        if (HasPrivateReceipt(receipt))
            return Task.FromResult(new OperationReceipt(false,receipt.Jid,request.Rid!,"failed",OperationNames.Public(request),"precondition"));
        if (IsHistoryPruned(receipt))
        {
            var sameRequest = receipt.Op == OperationNames.Public(request) &&
                (IsMutation(request)
                    ? receipt.Data?.GetValueOrDefault("requestIdentity")?.ToString() == ApplicationRequestIdentity(request)
                    : !receipt.Ok || receipt.Data?.GetValueOrDefault("name")?.ToString() == request.Arg.GetValueOrDefault("name"));
            // Pruned history is metadata only; never revisit an application
            // manager merely because a caller presents an old request ID.
            return Task.FromResult(sameRequest ? receipt :
                new OperationReceipt(false,receipt.Jid,request.Rid!,"failed",OperationNames.Public(request),"precondition"));
        }
        if(IsMutation(request))
        {
            lock(_admissionGate)
                if(MaintenanceClosed())
                    return Task.FromResult(receipt.Op==OperationNames.Public(request)&&
                        receipt.Data?.GetValueOrDefault("requestIdentity")?.ToString()==ApplicationRequestIdentity(request)
                        ?receipt:MaintenanceBusy(request,receipt.Jid,request.Rid!));
        }
        if(request.Arg.GetValueOrDefault("op")=="release")
            return Task.FromResult(receipt.Op==OperationNames.Public(request)&&
                receipt.Data?.GetValueOrDefault("requestIdentity")?.ToString()==ApplicationRequestIdentity(request)
                ?receipt:new OperationReceipt(false,receipt.Jid,request.Rid!,"failed",OperationNames.Public(request),"precondition"));
        if(!receipt.Ok)return Task.FromResult(receipt);
        if(receipt.Op!=OperationNames.Public(request)||receipt.Data?.GetValueOrDefault("aid")?.ToString() is not{Length:>0} knownAid||
            request.Arg.GetValueOrDefault("op") is "start" or "status"&&receipt.Data.GetValueOrDefault("name")?.ToString()!=request.Arg.GetValueOrDefault("name"))
            return Task.FromResult(new OperationReceipt(false,receipt.Jid,request.Rid!,"failed",OperationNames.Public(request),"precondition"));
        return ExecuteReceiptAsync(request,receipt.Jid,request.Rid!,0,cancellationToken,persist:false,knownAppAid:knownAid);
    }

    private Task<IReadOnlyDictionary<string,object?>> ApplicationAsync(OperationRequest request,CancellationToken cancellationToken,string? knownAid)
    {
        if(_applications is null)throw new AgentOpsException("user_dispatch_required");
        if(request.Root is not null)throw new AgentOpsException("auth");
        var action=Required(request,"op");
        var allowed=action switch
        {
            "start"=>new HashSet<string>(StringComparer.Ordinal){"op","cfg","name","aid","rid"},
            "status"=>new HashSet<string>(StringComparer.Ordinal){"op","name","rid"},
            "stop"=>new HashSet<string>(StringComparer.Ordinal){"op","aid","rid"},
            "inventory"=>new HashSet<string>(StringComparer.Ordinal){"op","max","cursor","rid"},
            "release"=>new HashSet<string>(StringComparer.Ordinal){"op","aid","epoch","rid"},
            _=>throw new AgentOpsException("invalid")
        };
        foreach(var key in request.Arg.Keys)
            if(!allowed.Contains(key)&&!(action=="start"&&key.StartsWith("arg",StringComparison.Ordinal)&&int.TryParse(key.AsSpan(3),out var index)&&index is >=0 and <256))
                throw new AgentOpsException("invalid");
        if(action=="start"&&request.Arg.Keys.Count(key=>key.StartsWith("arg",StringComparison.Ordinal))!=Arguments(request).Length)throw new AgentOpsException("invalid");
        if(action!="start"&&request.Ws is not null)throw new AgentOpsException("invalid");
        if(action=="inventory")return _applications.InventoryAsync(Int(request,"max",8),request.Arg.GetValueOrDefault("cursor"),cancellationToken);
        if(action=="release")return _applications.ReleaseAsync(Required(request,"aid"),Required(request,"epoch"),cancellationToken);
        if(action=="status")return _applications.StatusAsync(Required(request,"name"),cancellationToken);
        if(action=="stop")
        {
            var aid=Required(request,"aid");
            if(knownAid is not null&&aid!=knownAid)throw new AgentOpsException("precondition");
            return StopApplicationAsync(aid,cancellationToken);
        }
        var root=request.Ws is not null?_workspaces.Resolve(request.Ws,request.Cwd??Environment.CurrentDirectory)?.Path:request.PathRoots?.GetValueOrDefault("cwd");
        if(request.Ws is not null&&root is null)throw new AgentOpsException("precondition",failure:FailureEvidence.UnknownApplicationWorkspaceAlias());
        return knownAid is null
            ?_applications.StartAsync(Required(request,"cfg"),Required(request,"name"),Arguments(request),request.Cwd,root,request.Arg.GetValueOrDefault("aid"),request.Rid,cancellationToken)
            :_applications.ReplayStartAsync(knownAid,Required(request,"cfg"),Required(request,"name"),Arguments(request),request.Cwd,root,request.Arg.GetValueOrDefault("aid"),request.Rid,cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string,object?>> StopApplicationAsync(string aid,CancellationToken cancellationToken)
    {
        try{return await _applications!.StopAsync(aid,cancellationToken);}
        catch(AgentOpsException ex)when(ex.Code=="app_stop_incomplete")
        {
            var data=ex.Data[UserApplicationManager.StopEvidenceKey] as IReadOnlyDictionary<string,object?>
                ??new Dictionary<string,object?>{{"aid",aid},{"state","stopping"},{"issue","app_stop_incomplete"}};
            throw new OperationResultException(ex.Code,data,partial:true);
        }
    }

    private OperationReceipt Finish(OperationReceipt receipt,OperationRequest request,long sequence,bool persist)
    {
        if(request.Op=="app"&&IsMutation(request))
            receipt=receipt with{Data=new Dictionary<string,object?>(receipt.Data??new Dictionary<string,object?>())
                {["requestIdentity"]=ApplicationRequestIdentity(request)}};
        if(!persist)return receipt;
        receipt = JobSubmissionProjection.Attach(receipt, request);
        var job=new DurableJob(WindowsServiceReceiptForStore(receipt,request),IsMutation(request),sequence,null);
        _store.Update(job);
        // Active work leaves RAM for SQLite. Age/size retention may later delete
        // completed history after its replay window; active/uncertain work stays.
        _jobs.TryRemove(receipt.Jid,out _);
        MaybeCompactHistory();
        return receipt;
    }

    private static bool IsHistorySnapshot(OperationRequest request) =>
        request.Op == "windows_task" && request.Arg.GetValueOrDefault("elevate") != "uac" && WindowsTaskOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")) ||
        request.Op == "connection_profile" && ConnectionProfileOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")) ||
        request.Op is "installed_program" or "windows_device" or "windows_network" or "windows_account" ||
        ProcessThreadOperations.IsRequest(request) ||
        request.Op == "registry" && RegistryOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")) ||
        request.Op == "certificate" && CertificateOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")) ||
        request.Op == "app" && request.Arg.GetValueOrDefault("op") == "inventory" ||
        request.Op == "diag" || request.Op == "job" && request.Arg.GetValueOrDefault("op", "ls") is "ls" or "stat" or "log" or "cancel";

    internal int ResidentJobCount => _jobs.Count;
    internal int ResidentCompletionCount => _completions.Count;

    private IReadOnlyDictionary<string, object?> Context(OperationRequest req)
    {
        if (req.Arg.TryGetValue("register", out var registration))
        {
            var split = registration.IndexOf(':');
            if (split < 1) throw new AgentOpsException("invalid");
            var ws = _workspaces.Register(registration[..split], registration[(split + 1)..]);
            return new Dictionary<string, object?> { ["ws"] = ws.Alias, ["path"] = ws.Path };
        }
        var cwd = req.Cwd ?? Environment.CurrentDirectory;
        var resolved = _workspaces.Resolve(req.Ws, cwd);
        return new Dictionary<string, object?> { ["ws"] = resolved?.Alias, ["path"] = resolved?.Path, ["cwd"] = Path.GetFullPath(cwd), ["all"] = _workspaces.List() };
    }

    private static IReadOnlyDictionary<string, object?> Stat(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new AgentOpsException("not_found");
        var attrs = File.GetAttributes(path);
        return new Dictionary<string, object?> { ["src"] = path, ["kind"] = (attrs & FileAttributes.Directory) != 0 ? "dir" : "file", ["len"] = File.Exists(path) ? new FileInfo(path).Length : null, ["attrs"] = attrs.ToString(), ["reparse"] = (attrs & FileAttributes.ReparsePoint) != 0 };
    }

    private static IReadOnlyDictionary<string, object?> List(string path, int max)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new AgentOpsException("not_found");
        var entries = Directory.EnumerateFileSystemEntries(path).Take(Math.Clamp(max, 1, 48)).Select(x => new { name = Path.GetFileName(x), path = x, kind = Directory.Exists(x) ? "dir" : "file" }).ToArray();
        return new Dictionary<string, object?> { ["src"] = path, ["entries"] = entries, ["count"] = entries.Length };
    }

    private static IReadOnlyDictionary<string, object?> MakeDirectory(string path)
    { path = Path.GetFullPath(path); Directory.CreateDirectory(path); return new Dictionary<string, object?> { ["dst"] = path }; }

    private IReadOnlyDictionary<string,object?> WriteText(OperationRequest req)
    {
        var dst=ResolvePath(req,"dst");var body=RequiredBody(req);
        if(req.Arg.GetValueOrDefault("op","write")=="replace")return ExtendedOperations.ReplaceLiteral(dst,Required(req,"old"),body,Int(req,"count",1),req.Arg.GetValueOrDefault("sha"),req.Arg.GetValueOrDefault("enc"));
        return ExtendedOperations.Write(dst,body,req.Arg.GetValueOrDefault("enc"),req.Arg.GetValueOrDefault("sha"));
    }

    private IReadOnlyDictionary<string,object?> EditLines(OperationRequest req) => ExtendedOperations.ReplaceLines(
        ResolvePath(req,"dst"),Int(req,"line",0),Int(req,"count",0),RequiredBody(req),req.Arg.GetValueOrDefault("sha"),req.Arg.GetValueOrDefault("enc"));

    private IReadOnlyDictionary<string,object?> Encoding(OperationRequest req)
    {
        var op=Required(req,"op");
        if(op=="inspect")return ExtendedOperations.InspectEncoding(ResolvePath(req,"src"),req.Arg.GetValueOrDefault("enc"));
        if(op=="convert")return ExtendedOperations.ConvertEncoding(ResolvePath(req,"src"),ResolvePath(req,"dst"),Required(req,"enc"),req.Arg.GetValueOrDefault("sha"));
        throw new AgentOpsException("invalid");
    }

    private IReadOnlyDictionary<string,object?> Patch(OperationRequest req)
    {
        var root=Required(req,"workspace_root");return ExtendedOperations.ApplyUnifiedPatch(root,RequiredBody(req),req.Arg.GetValueOrDefault("sha"),req.Arg.GetValueOrDefault("enc"));
    }

    private static IReadOnlyDictionary<string,object?> Move(OperationRequest req,CancellationToken ct)
    {
        var result=new MoveRemoveOperations().Move(Required(req,"src"),Required(req,"dst"),req.Arg.GetValueOrDefault("pol"),new MoveRemoveOptions(req.PathRoots?.Values.ToArray()),ct);
        var data=new Dictionary<string,object?>{{"src",result.Source},{"dst",result.Destination},{"cross_volume",result.CrossVolume},{"verified",result.Verified},{"source_removed",result.SourceRemoved},{"recovery_required",result.RecoveryRequired},{"replaced",result.DestinationReplaced},{"cp",result.Copied}};
        if(!result.Ok)throw new OperationResultException(result.Error??"move_failed",data,result.Partial);return data;
    }

    private static IReadOnlyDictionary<string,object?> Remove(OperationRequest req,CancellationToken ct)
    {
        var result=new MoveRemoveOperations().Remove(Required(req,"dst"),Bool(req,"permanent"),new MoveRemoveOptions(req.PathRoots?.Values.ToArray()),ct);
        var data=new Dictionary<string,object?>{{"dst",result.Target},{"permanent",result.Permanent},{"recovery",result.RecoveryPath},{"del",result.Removed},{"recovery_required",result.RecoveryRequired}};
        if(!result.Ok)throw new OperationResultException(result.Error??"remove_failed",data,result.Partial);return data;
    }

    private static IReadOnlyDictionary<string, object?> Sha(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new AgentOpsException("not_found");
        using var stream = File.OpenRead(path); return new Dictionary<string, object?> { ["src"] = path, ["sha"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PwshAsync(OperationRequest req, CancellationToken ct)
    {
        var script=ResolvePath(req,"src");
        ToolInvocation invocation;
        try{invocation=_tools.PreparePowerShell(script,Arguments(req),workspaceRoot:req.PathRoots?.GetValueOrDefault("src"),cwd:req.Cwd);}
        catch(AgentOpsException ex)when(ex.Code=="not_found"){throw new OperationResultException("pwsh_unconfigured",new Dictionary<string,object?>{{"remedy","Install PowerShell 7 for all users or register an exact portable pwsh.exe with pao tool add --stdin."}});}
        return await RunInvocationAsync(invocation,ct);
    }

    private string ResolvePath(OperationRequest req, string key)
    {
        var value = Required(req, key);var normalizer=_pathNormalizer;
        Workspace? selected = req.PathRoots is not null && req.PathRoots.TryGetValue(key,out var frozenRoot) ? new Workspace("$frozen",frozenRoot) : null;
        if (selected is not null)
        {
            var frozenContext = normalizer.Normalize(Path.GetFullPath(value));var frozen=frozenContext.CanonicalPath;
            if (!PathNormalizer.Contains(selected.Path, frozen)) throw new AgentOpsException("precondition");
            WindowsPathContextNormalizer.RevalidateLocalNoFollow(frozenContext,selected.Path);
            return frozen;
        }
        var colon = value.IndexOf(':');
        if (IsAliasQualified(value,colon))
        {
            var alias = value[..colon];
            selected = _workspaces.Resolve(alias, req.Cwd ?? Environment.CurrentDirectory);
            if (selected is not null) value = value[(colon + 1)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        selected ??= _workspaces.Resolve(req.Ws, req.Cwd ?? Environment.CurrentDirectory);
        WindowsPathContextResult context;if(selected is not null&&!Path.IsPathFullyQualified(value))context=normalizer.ResolveAliasPath(normalizer.NormalizeAliasRoot(selected.Alias,selected.Path),value);else context=normalizer.Normalize(Path.GetFullPath(value,selected?.Path??req.Cwd??Environment.CurrentDirectory));var full=context.CanonicalPath;
        if (selected is not null && !PathNormalizer.Contains(selected.Path, full)) throw new AgentOpsException("precondition");
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(context,selected?.Path);
        return full;
    }

    private async Task<IReadOnlyDictionary<string,object?>> RunRegisteredAsync(OperationRequest req,CancellationToken ct)=>await RunInvocationAsync(_tools.PrepareTool(Required(req,"cfg"),Arguments(req),req.PathRoots?.GetValueOrDefault("cwd"),req.Cwd,Int(req,"tmo",30)),ct);
    private async Task<IReadOnlyDictionary<string,object?>> GitAsync(OperationRequest req,CancellationToken ct)
    {
        try{_=_tools.Tools.Get("git");}catch(AgentOpsException ex)when(ex.Code=="not_found"){throw new OperationResultException("git_unconfigured",new Dictionary<string,object?>{{"remedy","Register the exact canonical Git executable with pao tool add --stdin, then retry the typed Git operation."}});}
        return await RunInvocationAsync(_tools.PrepareGit(Required(req,"op"),Arguments(req),workspaceRoot:req.PathRoots?.GetValueOrDefault("cwd"),cwd:req.Cwd),ct);
    }
    private async Task<IReadOnlyDictionary<string,object?>> DotnetAsync(OperationRequest req,CancellationToken ct)
    {
        var operation=Required(req,"op");var target=ResolvePath(req,"src");string? output=null;
        if(!File.Exists(target))throw new OperationResultException("not_found",new Dictionary<string,object?>{{"field","src"}});
        if(operation=="publish"&&req.Arg.ContainsKey("dst"))output=ResolvePath(req,"dst");
        var result=await RunInvocationAsync(_tools.PrepareDotnet(operation,req.Arg.GetValueOrDefault("cfg"),Arguments(req),workspaceRoot:req.PathRoots?.GetValueOrDefault("src"),cwd:req.Cwd,target:target,output:output),ct);
        if(operation=="publish"&&output is not null&&
            (!Directory.Exists(output)||!Directory.EnumerateFiles(output,"*",SearchOption.AllDirectories).Any()))
            throw new OperationResultException("external_failed",new Dictionary<string,object?>{{"dst",output},{"diagnostic","dotnet publish reported success but produced no files"}});
        return result;
    }
    private async Task<IReadOnlyDictionary<string,object?>> ToolAdminAsync(OperationRequest req,CancellationToken ct)
    {
        var op=req.Arg.GetValueOrDefault("op","ls");
        if(op=="ls")return new Dictionary<string,object?>{{"tools",_tools.Tools.List().Select(x=>new{x.Name,x.DefinitionVersion,x.ExecutablePath,x.Mode,x.ProbeVerified,x.EnvironmentMode}).ToArray()}};
        if(op=="rm")return new Dictionary<string,object?>{{"removed",_tools.RemoveTool(Required(req,"name"),ct)}};
        if(op!="add")throw new AgentOpsException("invalid");
        RegisteredToolDefinition definition;try{definition=JsonSerializer.Deserialize<RegisteredToolDefinition>(RequiredBody(req),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new JsonException();}catch(JsonException ex){throw new AgentOpsException("invalid",inner:ex);}
        if(definition.Mode!=ToolExecutionMode.User)throw new AgentOpsException("service_unavailable");
        if(definition.EnvironmentMode!=ToolEnvironmentMode.Replace)throw new AgentOpsException("invalid");
        var environment=new Dictionary<string,string?>(MinimalToolEnvironment(definition.ExecutablePath),StringComparer.OrdinalIgnoreCase);
        if(definition.Environment is not null)foreach(var item in definition.Environment)
        {
            if(item.Key is not("DOTNET_ROOT" or "SystemRoot" or "WINDIR" or "TEMP" or "TMP")||string.IsNullOrWhiteSpace(item.Value)||!Path.IsPathFullyQualified(item.Value))throw new AgentOpsException("invalid");
            var directory=Path.GetFullPath(item.Value);if(!Directory.Exists(directory))throw new AgentOpsException("not_found");PathNormalizer.RejectAbsoluteReparseComponents(directory);environment[item.Key]=directory;
        }
        definition=definition with{Environment=environment};
        var registered=await _tools.Tools.RegisterAsync(definition,ct);
        return new Dictionary<string,object?>{{"name",registered.Name},{"version",registered.DefinitionVersion},{"path",registered.ExecutablePath},{"probed",registered.ProbeVerified}};
    }
    private IReadOnlyDictionary<string,object?> RecipeAdmin(OperationRequest req)
    {
        var op=req.Arg.GetValueOrDefault("op","ls");
        if(op=="ls")return new Dictionary<string,object?>{{"recipes",RecipeDisplayMetadata.ProjectList(
            _tools.Recipes.List(),RecipeDisplayMetadata.LoadForRegistry(_tools.Recipes.StorePath))}};
        if(op=="get")
        {
            if(req.Arg.Keys.Any(key=>key is not("op" or "name" or "generation")))throw new AgentOpsException("invalid");
            var recipe=_tools.Recipes.Get(Required(req,"name"));
            return new Dictionary<string,object?>{{"recipe",RecipeDisplayMetadata.ProjectDetail(recipe,Required(req,"generation"),
                RecipeDisplayMetadata.LoadForRegistry(_tools.Recipes.StorePath))}};
        }
        if(op=="rm")return new Dictionary<string,object?>{{"removed",_tools.RemoveRecipe(Required(req,"name"))}};
        if(op!="add")throw new AgentOpsException("invalid");
        OneActionRecipeDefinition definition;try{definition=JsonSerializer.Deserialize<OneActionRecipeDefinition>(RequiredBody(req),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new JsonException();}catch(JsonException ex){throw new AgentOpsException("invalid",inner:ex);}
        if(definition.Mode!=ToolExecutionMode.User)throw new AgentOpsException("service_unavailable");
        var registered=_tools.Recipes.Register(definition);return new Dictionary<string,object?>{{"name",registered.Name},{"version",registered.DefinitionVersion},{"tool",registered.ToolName},{"action",registered.Action.ToString()}};
    }
    private async Task<IReadOnlyDictionary<string,object?>> RecipeAsync(OperationRequest req,CancellationToken ct)
    {
        Dictionary<string,string>? variables=null;if(req.Body is not null)try{variables=JsonSerializer.Deserialize<Dictionary<string,string>>(req.Body,new JsonSerializerOptions(JsonSerializerDefaults.Web));}catch(JsonException ex){throw new AgentOpsException("invalid",inner:ex);}
        var roots=(req.PathRoots??new Dictionary<string,string>()).Where(x=>x.Key.StartsWith("ws:",StringComparison.Ordinal)).ToDictionary(x=>x.Key[3..],x=>x.Value,StringComparer.OrdinalIgnoreCase);if(roots.Count==0)roots=_workspaces.List().ToDictionary(x=>x.Alias,x=>x.Path,StringComparer.OrdinalIgnoreCase);return await RunInvocationAsync(_tools.PrepareRecipe(Required(req,"rec"),variables,roots,req.Cwd),ct);
    }
    private async Task<IReadOnlyDictionary<string,object?>> SevenZipAsync(OperationRequest req,CancellationToken ct)
    {
        var invocation=_systemTools is null
            ?_tools.PrepareTool("7z",[],req.PathRoots?.GetValueOrDefault("cwd"),req.Cwd)
            :_systemTools.SevenZip is null
                ?throw new AgentOpsException("not_found")
                :_tools.PrepareAdmittedTool(_systemTools.SevenZip,[],req.PathRoots?.GetValueOrDefault("cwd"),req.Cwd);
        var operations=new SevenZipOperations(new WindowsSevenZipProcessRunner(invocation.Environment));var op=Required(req,"op");
        if(op=="list"){var result=await operations.ListAsync(invocation.ExecutablePath,Required(req,"src"),cancellationToken:ct);return new Dictionary<string,object?>{{"src",result.Archive},{"count",result.Entries},{"expanded_bytes",result.ExpandedBytes},{"entries",result.Items.Take(48).ToArray()},{"truncated",result.Items.Count>48}};}
        if(op=="test"){var result=await operations.TestAsync(invocation.ExecutablePath,Required(req,"src"),cancellationToken:ct);if(!result.Ok)throw new OperationResultException(result.Error??"archive_invalid",new Dictionary<string,object?>{{"src",result.Archive},{"count",result.Entries},{"expanded_bytes",result.ExpandedBytes}});return new Dictionary<string,object?>{{"src",result.Archive},{"count",result.Entries},{"expanded_bytes",result.ExpandedBytes},{"tested",true}};}
        if(op=="extract"){var result=await operations.ExtractAsync(invocation.ExecutablePath,Required(req,"src"),Required(req,"dst"),req.Arg.GetValueOrDefault("pol"),cancellationToken:ct);var data=new Dictionary<string,object?>{{"src",result.Archive},{"dst",result.Destination},{"pol",result.Policy},{"count",result.Entries},{"expanded_bytes",result.ExpandedBytes},{"del",result.Deleted},{"cp",result.Copied},{"changed",result.Changed},{"skipped",result.Skipped}};if(!result.Ok)throw new OperationResultException(result.Error??"archive_extract_failed",data,result.Partial);return data;}
        throw new AgentOpsException("invalid");
    }
    internal const string NativeExecutableExtensions = ".COM;.EXE;.BAT;.CMD";

    internal static IReadOnlyDictionary<string,string?> MinimalToolEnvironment(string executable)
    {
        var values=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);foreach(var key in new[]{"SystemRoot","WINDIR","TEMP","TMP"})if(Environment.GetEnvironmentVariable(key) is {Length:>0} value)values[key]=value;
        // PowerShell adds .CPL when PATHEXT is absent. Without a native baseline,
        // it then treats even an exact .exe as a shell-opened background document.
        values["PATHEXT"] = NativeExecutableExtensions;
        var commandProcessor=Environment.GetEnvironmentVariable("ComSpec");
        if(string.IsNullOrWhiteSpace(commandProcessor))commandProcessor=Path.Combine(Environment.SystemDirectory,"cmd.exe");
        if(!string.IsNullOrWhiteSpace(commandProcessor)&&Path.IsPathFullyQualified(commandProcessor)&&File.Exists(commandProcessor))
            values["ComSpec"]=Path.GetFullPath(commandProcessor);
        var userProfile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(!string.IsNullOrWhiteSpace(userProfile)&&Path.IsPathFullyQualified(userProfile))
        {
            userProfile=Path.GetFullPath(userProfile);
            values["USERPROFILE"]=userProfile;
        }
        var applicationData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if(!string.IsNullOrWhiteSpace(applicationData)&&Path.IsPathFullyQualified(applicationData))
            values["APPDATA"]=Path.GetFullPath(applicationData);
        var localApplicationData=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if(!string.IsNullOrWhiteSpace(localApplicationData)&&Path.IsPathFullyQualified(localApplicationData))
            values["LOCALAPPDATA"]=Path.GetFullPath(localApplicationData);
        foreach(var folder in new[]{
            ("ProgramFiles",Environment.SpecialFolder.ProgramFiles),
            ("ProgramFiles(x86)",Environment.SpecialFolder.ProgramFilesX86),
            ("ProgramW6432",Environment.SpecialFolder.ProgramFiles),
            ("ProgramData",Environment.SpecialFolder.CommonApplicationData),
            ("CommonProgramFiles",Environment.SpecialFolder.CommonProgramFiles),
            ("CommonProgramFiles(x86)",Environment.SpecialFolder.CommonProgramFilesX86),
            ("CommonProgramW6432",Environment.SpecialFolder.CommonProgramFiles)})
        {
            var path=Environment.GetFolderPath(folder.Item2);
            if(!string.IsNullOrWhiteSpace(path)&&Path.IsPathFullyQualified(path))
                values[folder.Item1]=Path.GetFullPath(path);
        }
        var systemDrive=Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd(Path.DirectorySeparatorChar);
        if(!string.IsNullOrWhiteSpace(systemDrive))values["SystemDrive"]=systemDrive;
        if(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable),"dotnet.exe"))
        {
            values["DOTNET_ROOT"]=Path.GetDirectoryName(Path.GetFullPath(executable));
            if(values.TryGetValue("USERPROFILE",out var registeredUserProfile))
            {
                values["DOTNET_CLI_HOME"]=registeredUserProfile;
                values["NUGET_PACKAGES"]=Path.Combine(registeredUserProfile!,".nuget","packages");
            }
        }
        return values;
    }
    private async Task<IReadOnlyDictionary<string,object?>> RunInvocationAsync(ToolInvocation invocation,CancellationToken ct){try{return ToolData(await _tools.RunAsync(AgentOpsExecutionContext.MarkMutationChild(invocation),ct),false);}catch(ToolExecutionException ex){throw new OperationResultException(ex.Code,ToolData(ex.Result,ex.TimedOut),ex.Code=="recovery_required",FailureEvidence.ToolExecution(ex));}}
    private static IReadOnlyDictionary<string,object?> ToolData(ToolExecutionResult result,bool timedOut)=>new Dictionary<string,object?>{{"exit",result.ExitCode},{"out",result.Stdout},{"err_tail",result.Stderr},{"out_truncated",result.StdoutTruncated},{"err_truncated",result.StderrTruncated},{"timed_out",timedOut},{"tool",result.ToolName}};
    private static string[] Arguments(OperationRequest req){var values=new List<string>();for(var i=0;req.Arg.TryGetValue($"arg{i}",out var value);i++)values.Add(value);return values.ToArray();}

    private static IReadOnlyDictionary<string,object?> ProcessData(ProcessResult result)=>new Dictionary<string,object?>
    {{"exit",result.ExitCode},{"out",result.Stdout},{"err_tail",result.Stderr},{"out_truncated",result.StdoutTruncated},{"err_truncated",result.StderrTruncated},{"timed_out",result.TimedOut}};

    private static string LogAvailability(IReadOnlyDictionary<string,object?> data) =>
        data.TryGetValue("out",out var stdout)&&stdout is string{Length:>0}||data.TryGetValue("err_tail",out var stderr)&&stderr is string{Length:>0}
            ? "available" : "empty";

    private async Task<IReadOnlyDictionary<string,object?>> ProcessesAsync(OperationRequest req,CancellationToken ct)
    {
        var op=req.Arg.GetValueOrDefault("op","inspect");
        if (op == "threads")
        {
            var target = ProcessThreadOperations.ParseRequest(req);
            var observed = _threadInventory is null
                ? ProcessThreadOperations.List(target.Pid, target.Start, target.Max, ct)
                : ProcessThreadOperations.List(target.Pid, target.Start, target.Max, _threadInventory, ct);
            var observation = new Dictionary<string, object?> { ["result"] = observed };
            if (!observed.Ok) throw new OperationResultException(observed.Error ?? "external_failed", observation, observed.Threads.Count != 0);
            return observation;
        }
        if(op=="ls")
        {
            var max=Int(req,"max",256);var pattern=req.Arg.GetValueOrDefault("pattern");if(pattern is not null&&(string.IsNullOrWhiteSpace(pattern)||System.Text.Encoding.UTF8.GetByteCount(pattern)>256))throw new AgentOpsException("invalid");var snapshot=ProcessSnapshot.Capture(4096);var items=new List<object>();var matched=0;
            foreach(var entry in snapshot.Entries.Values.OrderBy(x=>x.Pid))
            {
                ct.ThrowIfCancellationRequested();
                try{var inspected=ProcessOperations.Inspect(entry.Pid);if(inspected.Ok&&inspected.Process is not null&&(pattern is null||inspected.Process.Name.Contains(pattern,StringComparison.OrdinalIgnoreCase))){matched++;if(items.Count<max)items.Add(new{pid=entry.Pid,parent=entry.ParentPid,start=inspected.Process.Start,name=inspected.Process.Name});}}
                catch(AgentOpsException){ }
            }
            return new Dictionary<string,object?>{{"processes",items},{"matched",matched},{"truncated",snapshot.Truncated||items.Count<matched}};
        }
        var pid=Int(req,"pid",0);object result=op switch{"inspect"=>ProcessOperations.Inspect(pid),"wait"=>await ProcessOperations.WaitAsync(pid,Required(req,"start"),Int(req,"tmo",30),ct),"stop"=>await ProcessOperations.StopAsync(pid,Required(req,"start"),Int(req,"tmo",30),ct),"tree"=>ProcessOperations.Tree(pid,Int(req,"max",256)),_=>throw new AgentOpsException("invalid")};
        var data=new Dictionary<string,object?>{{"result",result}};var ok=(bool)(result.GetType().GetProperty("Ok")?.GetValue(result)??false);if(!ok){var code=result.GetType().GetProperty("Error")?.GetValue(result)?.ToString()??"external_failed";var partial=result is ProcessActionResult action&&action.ActionIssued;throw new OperationResultException(code,data,partial);}return data;
    }

    private async Task<IReadOnlyDictionary<string,object?>> ServiceAsync(OperationRequest req,CancellationToken ct)
    {
        if(req.Arg.GetValueOrDefault("__authenticated_frozen")!="1"||req.Arg.GetValueOrDefault("__execution_context_system")!="1")
            throw new AgentOpsException("auth");
        var actionName=Required(req,"op");
        var action=actionName switch
        {
            "status"=>WindowsServiceLifecycleAction.Status,
            "start"=>WindowsServiceLifecycleAction.Start,
            "stop"=>WindowsServiceLifecycleAction.Stop,
            "restart"=>WindowsServiceLifecycleAction.Restart,
            _=>throw new AgentOpsException("invalid")
        };
        var allowed=new HashSet<string>(StringComparer.Ordinal){"op","service","expect","pid","start","tmo","rid","__authenticated_frozen","__execution_context_system"};
        if(req.Arg.Keys.Any(key=>!allowed.Contains(key)))throw new AgentOpsException("invalid");
        var service=Required(req,"service");
        if(!StringComparer.Ordinal.Equals(service,ApplicationAuthorizationProtocol.CerebralServiceName))throw new AgentOpsException("service_not_admitted");
        var timeout=Int(req,"tmo",30);if(timeout is <1 or >120)throw new AgentOpsException("invalid");
        string? expectedState=null;int? expectedPid=null;string? expectedStart=null;
        if(action==WindowsServiceLifecycleAction.Status)
        {
            if(req.Arg.Keys.Any(key=>key is "expect" or "pid" or "start"))throw new AgentOpsException("invalid");
        }
        else
        {
            expectedState=Required(req,"expect");
            if(action==WindowsServiceLifecycleAction.Start)
            {
                if(expectedState!="stopped"||req.Arg.ContainsKey("pid")||req.Arg.ContainsKey("start"))throw new AgentOpsException("invalid");
            }
            else
            {
                if(expectedState!="running")throw new AgentOpsException("invalid");
                expectedPid=Int(req,"pid",0);if(expectedPid<1)throw new AgentOpsException("invalid");
                expectedStart=Required(req,"start");
                if(!DateTimeOffset.TryParseExact(expectedStart,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out _))throw new AgentOpsException("invalid");
            }
        }
        var result=await _serviceLifecycle.ExecuteAsync(service,action,expectedState,expectedPid,expectedStart,TimeSpan.FromSeconds(timeout),ct);
        var data=ServiceData(result);
        if(!result.Ok)throw new OperationResultException(result.Error??"external_failed",data,result.Partial);
        return data;
    }

    private static IReadOnlyDictionary<string,object?> ServiceData(WindowsServiceLifecycleResult result)
    {
        var data=new Dictionary<string,object?>
        {
            ["service"]=result.Current.Service,
            ["action"]=result.Action.ToString().ToLowerInvariant(),
            ["previous_state"]=result.Previous.State,
            ["service_state"]=result.Current.State,
            ["action_issued"]=result.ActionIssued
        };
        if(result.Current.ProcessId is not null)data["pid"]=result.Current.ProcessId;
        if(result.Current.ProcessStart is not null)data["start"]=result.Current.ProcessStart;
        if(result.Previous.ProcessId is not null)data["previous_pid"]=result.Previous.ProcessId;
        if(result.Previous.ProcessStart is not null)data["previous_start"]=result.Previous.ProcessStart;
        return data;
    }

    private static IReadOnlyDictionary<string,object?> Listeners(OperationRequest req){var result=NetworkOperations.ListListeners(req.Arg.TryGetValue("port",out var port)&&int.TryParse(port,out var parsed)?parsed:null,Int(req,"max",512));return new Dictionary<string,object?>{{"listeners",result.Listeners},{"truncated",result.Truncated},{"matched",result.MatchedCount}};}

    private static async Task<IReadOnlyDictionary<string,object?>> UiAsync(OperationRequest req,CancellationToken ct)
    {
        var op=Required(req,"op");
        var hostOperation=op switch
        {
            "status"=>UiOperation.Status,
            "find"=>UiOperation.FindWindow,
            "window"=>UiOperation.WindowInfo,
            "control" when req.Arg.ContainsKey("ctrl")=>UiOperation.ControlInfo,
            "control"=>UiOperation.ControlResolve,
            "activate"=>UiOperation.Activate,
            "move"=>UiOperation.Move,
            "focus"=>UiOperation.ControlFocus,
            "click"=>UiOperation.ControlClick,
            "set_text"=>UiOperation.ControlSetText,
            "capture"=>UiOperation.CaptureWindow,
            _=>throw new AgentOpsException("invalid")
        };
        var mutation=hostOperation is UiOperation.Activate or UiOperation.Move or UiOperation.ControlFocus or UiOperation.ControlClick or UiOperation.ControlSetText or UiOperation.CaptureWindow;
        var timeout=op=="status"?10:Int(req,"tmo",0);
        if(timeout is <1 or >30)throw new AgentOpsException("invalid");
        UiTargetIdentity? identity=null;
        if(op is not("status" or "find"))
        {
            var handle=Handle(req,"hwnd");
            var pid=Int(req,"pid",0);
            if(pid<1||!DateTimeOffset.TryParseExact(Required(req,"start"),"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out var start))throw new AgentOpsException("invalid");
            identity=new(handle,pid,start.UtcTicks);
        }
        long? controlHandle=req.Arg.ContainsKey("ctrl")?Handle(req,"ctrl"):null;
        UiWindowQuery? query=op=="find"?new UiWindowQuery(Required(req,"title"),req.Arg.GetValueOrDefault("window_text"),req.Arg.GetValueOrDefault("match","exact")):null;
        string? selector=null;
        if(op=="control"&&controlHandle is null)selector=$"[CLASS:{Required(req,"control_class")}; INSTANCE:{Int(req,"instance",1)}]";
        UiBounds? bounds=op=="move"?new(Int(req,"x",int.MinValue),Int(req,"y",int.MinValue),Int(req,"width",0),Int(req,"height",0)):null;
        var request=new UiHostRequest(
            1,Guid.NewGuid().ToString("N"),hostOperation,query,identity,controlHandle,selector,bounds,
            req.Arg.GetValueOrDefault("button","left"),op=="click"?Int(req,"count",1):null,
            Text:op=="set_text"?RequiredBody(req):null,
            Destination:op=="capture"?Required(req,"dst"):null,
            Format:op=="capture"?req.Arg.GetValueOrDefault("fmt","png"):null);
        var response=await UiAutomationClient.ExecuteAsync(request,TimeSpan.FromSeconds(timeout),mutation,ct);
        if(!response.Ok)throw new OperationResultException(response.Err??"ui_unavailable",new Dictionary<string,object?>{{"field",response.Field}},response.Partial||response.RecoveryRequired);
        return UiData(op,response.Data,req.Body);
    }

    private async Task<IReadOnlyDictionary<string,object?>> NsisAsync(OperationRequest req,CancellationToken ct)
    {
        var operation=Required(req,"op");
        RegisteredToolDefinition definition;
        try { definition=_bundledNsis??_tools.Tools.Get("nsis"); }
        catch(AgentOpsException ex)when(ex.Code=="not_found")
        { throw new OperationResultException("nsis_unconfigured",new Dictionary<string,object?>{{"configured",false}}); }
        if(!definition.ProbeVerified)throw new OperationResultException("nsis_unconfigured",new Dictionary<string,object?>{{"configured",true},{"probe_verified",false}});
        if(operation=="status")return new Dictionary<string,object?>
        {
            {"configured",true},
            {"probe_verified",true},
            {"tool","nsis"},
            {"provider",_bundledNsis is null?"registered":"product"},
            {"definition_version",definition.DefinitionVersion}
        };
        if(operation is not("validate" or "build"))throw new AgentOpsException("invalid");
        var source=Required(req,"src");_ = NsisOperations.ReadAndAdmitSource(source);var sourceSha=ToolRecipeValidation.ComputeFileHash(source);var timeout=Int(req,"tmo",60);if(timeout is <1 or >3600)throw new AgentOpsException("invalid");
        string? output=null;
        if(operation=="build")
        {
            output=Required(req,"dst");
            if(File.Exists(output)||Directory.Exists(output))throw new AgentOpsException("collision");
            var parent=Path.GetDirectoryName(output);if(string.IsNullOrEmpty(parent)||!Directory.Exists(parent))throw new AgentOpsException("not_found");
        }
        ToolRecipeValidation.VerifyFileHash(source,sourceSha);
        var preprocess=_bundledNsis is null
            ? _tools.PrepareTool("nsis",NsisOperations.PreprocessArguments(source,output),req.PathRoots?.GetValueOrDefault("src"),req.Cwd,timeout)
            : _tools.PrepareAdmittedTool(_bundledNsis,NsisOperations.PreprocessArguments(source,output),req.PathRoots?.GetValueOrDefault("src"),req.Cwd,timeout);
        // The admitted script can be 512 KiB. Keep enough bounded transport and
        // tool output capacity to validate the complete expanded source instead
        // of failing at the generic registered-tool 64 KiB receipt limit.
        preprocess=preprocess.WithOutputCapacity(768*1024);
        var preprocessed=await RunNsisToolAsync(preprocess,ct,partialPath:null);
        var validation=NsisOperations.ValidateExpanded(source,preprocessed,output);
        var data=new Dictionary<string,object?>{{"src",source},{"lines",validation.Lines},{"bytes",validation.Bytes},{"sha",validation.Sha256},{"trace_tail",validation.TraceTail},{"trace_truncated",validation.TraceTruncated},{"validated",true},{"err_tail",preprocessed.Stderr},{"err_truncated",preprocessed.StderrTruncated},{"log_available",preprocessed.Stderr.Length>0}};
        if(operation=="validate")return data;
        ToolRecipeValidation.VerifyFileHash(source,sourceSha);
        if(File.Exists(output!)||Directory.Exists(output!))throw new AgentOpsException("collision");
        var build=_bundledNsis is null
            ? _tools.PrepareTool("nsis",NsisOperations.BuildArguments(source,output!),req.PathRoots?.GetValueOrDefault("src"),req.Cwd,timeout)
            : _tools.PrepareAdmittedTool(_bundledNsis,NsisOperations.BuildArguments(source,output!),req.PathRoots?.GetValueOrDefault("src"),req.Cwd,timeout);
        var compiled=await RunNsisToolAsync(build,ct,output);
        var verified=NsisOperations.VerifyInstaller(output!);
        var trace=ToolRecipeValidation.BoundUtf8(compiled.Stderr.Length>0?compiled.Stderr:compiled.Stdout,16*1024);
        data["dst"]=output;data["installer_bytes"]=verified.Bytes;data["installer_sha"]=verified.Sha256;data["exit"]=compiled.ExitCode;data["build_trace_tail"]=trace.Text;data["build_trace_truncated"]=trace.Truncated||compiled.StdoutTruncated||compiled.StderrTruncated;data["out"]=compiled.Stdout;data["err_tail"]=compiled.Stderr;data["out_truncated"]=compiled.StdoutTruncated;data["err_truncated"]=compiled.StderrTruncated;data["log_available"]=compiled.Stdout.Length>0||compiled.Stderr.Length>0;
        return data;
    }

    private async Task<ToolExecutionResult> RunNsisToolAsync(ToolInvocation invocation,CancellationToken ct,string? partialPath)
    {
        try{return await _tools.RunAsync(AgentOpsExecutionContext.MarkMutationChild(invocation),ct);}
        catch(ToolExecutionException ex)
        {
            var data=new Dictionary<string,object?>(ToolData(ex.Result,ex.TimedOut));
            var partial=partialPath is not null&&File.Exists(partialPath);if(partial){data["dst"]=partialPath;data["partial_output"]=true;}
            throw new OperationResultException(ex.Code,data,partial||ex.Code=="recovery_required");
        }
    }

    private static IReadOnlyDictionary<string,object?> UiData(string op,object? data,string? body)
    {
        if(data is not JsonElement element)return new Dictionary<string,object?>{{"result",data}};
        if(op=="status")return new Dictionary<string,object?>{{"available",GetBool(element,"available")},{"provider",GetString(element,"provider")},{"version",GetString(element,"version")},{"architecture",GetString(element,"architecture")},{"interactive_user",GetBool(element,"interactiveUser")}};
        if(op=="find")return IdentityData(element);
        if(op=="control"&&element.ValueKind==JsonValueKind.Number)return new Dictionary<string,object?>{{"ctrl",FormatHandle(element.GetInt64())}};
        if(op=="window")
        {
            var result=new Dictionary<string,object?>(IdentityData(element.GetProperty("target"))){{"title",GetString(element,"title")},{"class_list",GetString(element,"classList")},{"bounds",element.GetProperty("bounds").Clone()}};
            return result;
        }
        if(op=="control")return new Dictionary<string,object?>{{"hwnd",FormatHandle(element.GetProperty("window").GetProperty("handle").GetInt64())},{"ctrl",FormatHandle(element.GetProperty("handle").GetInt64())},{"text",GetString(element,"text")},{"bounds",element.GetProperty("bounds").Clone()}};
        if(op=="capture")return new Dictionary<string,object?>{{"dst",GetString(element,"destination")},{"fmt",GetString(element,"format")},{"width",element.GetProperty("width").GetInt32()},{"height",element.GetProperty("height").GetInt32()},{"bytes",element.GetProperty("bytes").GetInt64()},{"sha",GetString(element,"sha256")}};
        var mutation=new Dictionary<string,object?>{{"action_issued",GetBool(element,"actionIssued")},{"changed",GetBool(element,"changed")},{"verified",true}};
        if(op=="set_text")mutation["text_bytes"]=body is null?0:System.Text.Encoding.UTF8.GetByteCount(body);
        return mutation;
    }

    private static Dictionary<string,object?> IdentityData(JsonElement element)
    {
        var ticks=element.GetProperty("processStartUtcTicks").GetInt64();
        return new Dictionary<string,object?>{{"hwnd",FormatHandle(element.GetProperty("handle").GetInt64())},{"pid",element.GetProperty("processId").GetInt32()},{"start",new DateTimeOffset(ticks,TimeSpan.Zero).ToString("O",System.Globalization.CultureInfo.InvariantCulture)}};
    }
    private static bool GetBool(JsonElement element,string name)=>element.TryGetProperty(name,out var property)&&property.ValueKind==JsonValueKind.True;
    private static string? GetString(JsonElement element,string name)=>element.TryGetProperty(name,out var property)&&property.ValueKind==JsonValueKind.String?property.GetString():null;
    private static string FormatHandle(long value)=>"0x"+value.ToString("X16",System.Globalization.CultureInfo.InvariantCulture);
    private static long Handle(OperationRequest req,string key)
    {
        var value=Required(req,key);var span=value.AsSpan();if(span.StartsWith("0x",StringComparison.OrdinalIgnoreCase))span=span[2..];
        if(!long.TryParse(span,System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out var result)||result<=0)throw new AgentOpsException("invalid");return result;
    }
    private IReadOnlyDictionary<string,object?> Feedback(OperationRequest req)
    {
        var op=req.Arg.GetValueOrDefault("op","ls");
        if(op=="submit")
        {
            var item=_feedback.Submit(Required(req,"kind"),Required(req,"area"),Required(req,"summary"),req.Arg.GetValueOrDefault("notes"));
            return new Dictionary<string,object?>{{"feedback",item with{Notes=null}},{"fid",item.Fid},{"notes_recorded",item.Notes is not null},{"notes_bytes",item.Notes is null?0:System.Text.Encoding.UTF8.GetByteCount(item.Notes)}};
        }
        if(op=="get")return new Dictionary<string,object?>{{"feedback",_feedback.Get(Required(req,"fid"))}};
        if(op=="set")
        {
            var item=_feedback.SetStatus(Required(req,"fid"),Required(req,"status"));
            return new Dictionary<string,object?>{{"feedback",item with{Notes=null}},{"fid",item.Fid}};
        }
        var after=0L;if(req.Arg.TryGetValue("after",out var rawAfter)&&(!long.TryParse(rawAfter,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out after)||after<0))throw new AgentOpsException("invalid");
        var page=_feedback.List(req.Arg.GetValueOrDefault("status"),req.Arg.GetValueOrDefault("kind"),req.Arg.GetValueOrDefault("area"),after,Int(req,"max",25),includeNotes:op=="export");
        if(op=="ls")return new Dictionary<string,object?>{{"feedback",page.Items},{"count",page.Items.Count},{"next",page.Next},{"more",page.More}};
        if(op=="export")return new Dictionary<string,object?>{{"fmt","pegasus-agentops-feedback/1"},{"bundle",new{version=1,exportedUtc=DateTimeOffset.UtcNow.ToString("O"),items=page.Items,next=page.Next,more=page.More}},{"count",page.Items.Count},{"next",page.Next},{"more",page.More}};
        throw new AgentOpsException("invalid");
    }
    internal static bool IsMutation(OperationRequest req) => MaintenanceRequestPolicy.IsMutation(req);

    // One selector predicate serves raw engine admission, CLI/MCP validation and
    // discovery. Privileged, interactive and application lifetime routes retain
    // their independent contracts; this never implies permission to execute.
    public static bool SupportsDeferredCompletion(OperationRequest request)
        => request.Op == "windows_service" && request.Arg.GetValueOrDefault("elevate") == "uac" &&
            WindowsServiceOperationContract.IsOwnerElevationAction(request.Arg.GetValueOrDefault("op")) ||
            IsMutation(request) && request.Op is
            ("mkdir" or "wr" or "edit" or "patch" or "cp" or "mv" or "rm" or "run" or "pwsh" or "git" or "dotnet" or "do" or
             "nsis" or "encoding" or "tool" or "recipe" or "feedback" or "7z" or "zip" or "proc" or "ctx" or "connection_profile" or "windows_task");
    private OperationRequest FreezePaths(OperationRequest req)
    {
        if (req.Op == "certificate" && CertificateOperationContract.IsFileAction(req.Arg.GetValueOrDefault("op")))
            return FreezePaths(req with { Op = CertificateOperationContract.PathFields(req.Arg.GetValueOrDefault("op")).Single() == "src" ? "sha" : "mkdir" }) with { Op = req.Op };
        var keys=req.Op switch{"stat" or "ls" or "find" or "search" or "slice" or "count" or "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "pwsh"=>new[]{"src"},"dotnet" when req.Arg.GetValueOrDefault("op")=="publish"&&req.Arg.ContainsKey("dst")=>new[]{"src","dst"},"dotnet"=>new[]{"src"},"nsis" when req.Arg.GetValueOrDefault("op")=="build"=>new[]{"src","dst"},"nsis" when req.Arg.GetValueOrDefault("op")=="validate"=>new[]{"src"},"encoding" when req.Arg.GetValueOrDefault("op")=="inspect"=>new[]{"src"},"encoding"=>new[]{"src","dst"},"diff"=>new[]{"src","dst"},"mkdir" or "wr" or "edit" or "rm"=>new[]{"dst"},"ui" when req.Arg.GetValueOrDefault("op")=="capture"=>new[]{"dst"},"cp" or "mv"=>new[]{"src","dst"},"zip" or "7z" when req.Arg.GetValueOrDefault("op") is "list" or "test"=>new[]{"src"},"zip" or "7z"=>new[]{"src","dst"},_=>Array.Empty<string>()};
        if(req.Op=="patch")return FreezePatch(req);
        if(keys.Length==0)return req;
        var frozen=new Dictionary<string,string>(req.Arg,StringComparer.Ordinal);
        var roots=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var key in keys)
        {
            frozen[key]=ResolvePath(req,key);
            var original=Required(req,key); Workspace? selected=null; var colon=original.IndexOf(':');
            if(IsAliasQualified(original,colon))selected=_workspaces.Resolve(original[..colon],req.Cwd??Environment.CurrentDirectory);
            selected??=_workspaces.Resolve(req.Ws,req.Cwd??Environment.CurrentDirectory);
            if(selected is not null)roots[key]=selected.Path;
        }
        return req with{Arg=frozen,Ws=null,PathRoots=roots};
    }

    private OperationRequest FreezePatch(OperationRequest req)
    {
        var body=RequiredBody(req);var root=_workspaces.Resolve(req.Ws,req.Cwd??Environment.CurrentDirectory)?.Path??throw new AgentOpsException("precondition");PathNormalizer.RejectReparseComponents(root,root);
        var patch=UnifiedPatch.ParseSingleFile(body);var relative=patch.RelativePath;var dst=Path.GetFullPath(relative.Replace('/',Path.DirectorySeparatorChar),root);if(!PathNormalizer.Contains(root,dst))throw new AgentOpsException("precondition");PathNormalizer.RejectReparseComponents(root,dst);
        var arg=new Dictionary<string,string>(req.Arg,StringComparer.Ordinal){{"workspace_root",root},{"dst",dst}};return req with{Arg=arg,Ws=null,Cwd=null,PathRoots=new Dictionary<string,string>{{"dst",root}}};
    }

    private OperationRequest ValidateAuthenticatedFrozen(OperationRequest req)
    {
        var keys=req.Op switch{"stat" or "ls" or "find" or "search" or "slice" or "count" or "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "pwsh"=>new[]{"src"},"dotnet" when req.Arg.GetValueOrDefault("op")=="publish"&&req.Arg.ContainsKey("dst")=>new[]{"src","dst"},"dotnet"=>new[]{"src"},"nsis" when req.Arg.GetValueOrDefault("op")=="build"=>new[]{"src","dst"},"nsis" when req.Arg.GetValueOrDefault("op")=="validate"=>new[]{"src"},"encoding" when req.Arg.GetValueOrDefault("op")=="inspect"=>new[]{"src"},"encoding"=>new[]{"src","dst"},"diff"=>new[]{"src","dst"},"mkdir" or "wr" or "edit" or "rm"=>new[]{"dst"},"ui" when req.Arg.GetValueOrDefault("op")=="capture"=>new[]{"dst"},"cp" or "mv"=>new[]{"src","dst"},"zip" or "7z" when req.Arg.GetValueOrDefault("op") is "list" or "test"=>new[]{"src"},"zip" or "7z"=>new[]{"src","dst"},_=>Array.Empty<string>()};
        var allowed=new HashSet<string>(keys,StringComparer.Ordinal){"cwd"};if(req.Op=="patch")allowed.Add("dst");if(req.Op=="do"&&req.PathRoots is not null)foreach(var key in req.PathRoots.Keys.Where(x=>x.StartsWith("ws:",StringComparison.Ordinal)))allowed.Add(key);
        if(req.PathRoots is not null&&req.PathRoots.Keys.Any(key=>!allowed.Contains(key)))throw new AgentOpsException("invalid");
        foreach(var key in keys)ValidateAuthenticatedFrozenPath(req,key);
        if(req.Op=="do"&&req.PathRoots is not null)foreach(var root in req.PathRoots.Where(x=>x.Key.StartsWith("ws:",StringComparison.Ordinal)).Select(x=>x.Value)){if(!Path.IsPathFullyQualified(root))throw new AgentOpsException("invalid");var context=_pathNormalizer.Normalize(Path.GetFullPath(root));var canonical=context.CanonicalPath;if(!StringComparer.OrdinalIgnoreCase.Equals(canonical,root))throw new AgentOpsException("precondition");WindowsPathContextNormalizer.RevalidateLocalNoFollow(context);}
        if(req.Cwd is not null)
        {
            if(!Path.IsPathFullyQualified(req.Cwd))throw new AgentOpsException("invalid");
            ValidateAuthenticatedFrozenPath(req with{Arg=new Dictionary<string,string>(req.Arg,StringComparer.Ordinal){{"cwd",req.Cwd}}},"cwd");
        }
        if(req.Op=="patch")
        {
            var root=Required(req,"workspace_root");var dst=Required(req,"dst");
            ValidateAuthenticatedFrozenPath(req,"dst");
            if(req.PathRoots is null||!req.PathRoots.TryGetValue("dst",out var admittedRoot)||!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root),Path.GetFullPath(admittedRoot)))throw new AgentOpsException("precondition");
            var parsed=UnifiedPatch.ParseSingleFile(RequiredBody(req));var expected=Path.GetFullPath(parsed.RelativePath.Replace('/',Path.DirectorySeparatorChar),root);
            if(!StringComparer.OrdinalIgnoreCase.Equals(expected,Path.GetFullPath(dst)))throw new AgentOpsException("precondition");
        }
        return req with{Ws=null};
    }

    private void ValidateAuthenticatedFrozenPath(OperationRequest req,string key)
    {
        var value=Required(req,key);if(!Path.IsPathFullyQualified(value))throw new AgentOpsException("invalid");var context=_pathNormalizer.Normalize(Path.GetFullPath(value));var canonical=context.CanonicalPath;
        if(!StringComparer.OrdinalIgnoreCase.Equals(canonical,value))throw new AgentOpsException("precondition");
        if(req.PathRoots is not null&&req.PathRoots.TryGetValue(key,out var root))
        {
            root=_pathNormalizer.Normalize(Path.GetFullPath(root)).CanonicalPath;if(!PathNormalizer.Contains(root,canonical))throw new AgentOpsException("precondition");WindowsPathContextNormalizer.RevalidateLocalNoFollow(context,root);
        }
        else WindowsPathContextNormalizer.RevalidateLocalNoFollow(context);
    }

    private IReadOnlyDictionary<string, object?> Job(OperationRequest req)
    {
        var op=req.Arg.GetValueOrDefault("op","ls");
        if(op=="ls")return HistoryList(req);
        if(op=="prune")return PruneHistory(req);
        var id=Required(req,"jid"); var found=_store.Find(id);if(found is null)throw new AgentOpsException("not_found");
        if(op=="retry"&&IsHistoryPruned(found.Receipt))throw new AgentOpsException("job_pruned");
        return op switch
        {
            "stat" => ObserveJob(found.Receipt),
            "log" => JobLogProjection.Create(found.Receipt,LogMaximum(req)),
            "cancel" => CancelJob(id),
            _ => throw new AgentOpsException("invalid")
        };
    }
    private static int LogMaximum(OperationRequest req)
    {
        var maximum=Int(req,"max",16*1024);
        if(maximum is <256 or >65536)throw new AgentOpsException("invalid");
        return maximum;
    }
    private static bool IsAliasQualified(string value,int colon)=>colon>0&&!(colon==1&&value.Length>2&&value[2] is '\\' or '/');
    private async Task<OperationReceipt> WaitJobAsync(string jid,CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        while (true)
        {
            if (_completions.TryGetValue(jid, out var completion))
                return WindowsServiceReceiptForStore(await completion.Task.WaitAsync(lifetime.Token), null);
            var receipt = _store.FindReceipt(jid);
            if (receipt is null) return new(false, jid, Guid.NewGuid().ToString("N"), "failed", "job", "not_found");
            if (receipt.State is not ("queued" or "running")) return receipt;
            lock (_admissionGate)
            {
                if (IsAcknowledgedUnknown(jid)) return receipt;
                if (_recoveryPending) PumpRecovery();
            }
            // A legacy recovery backlog remains on disk until bounded capacity is
            // available. Poll only its receipt, never hydrate its request/body or
            // allocate one retained completion per unadmitted row.
            await Task.Delay(50, lifetime.Token);
        }
    }

    private IReadOnlyDictionary<string, object?> Diagnostics()
    {
        var data = new Dictionary<string, object?>
        {
        ["product"] = AgentOpsProductInfo.Product,
        ["product_version"] = AgentOpsProductInfo.ProductVersion,
        ["protocol"] = $"{ProtocolVersion.Major}.{ProtocolVersion.Minor}",
        ["mcp_schema"] = AgentOpsProductInfo.McpSchemaVersion,
        ["manifest"] = AgentOpsProductInfo.InstallManifestVersion,
        ["store"] = "sqlite-wal",
        ["fifo"] = "ordered-conflicts-bounded",
        ["queue_pending"] = _scheduler.PendingCount,
        ["queue_active"] = _scheduler.ActiveCount,
        ["queue_max_pending"] = _scheduler.MaximumPending,
        ["queue_max_active"] = _scheduler.MaximumActive,
        ["queue_payload_bytes"] = _scheduler.RetainedPayloadBytes,
        ["queue_max_payload_bytes"] = _scheduler.MaximumPayloadBytes,
        ["queue_callback_faults"] = _scheduler.CallbackFaultCount,
        ["queue_recovery_pending"] = _recoveryPending,
        ["history_mode"] = "sqlite-on-demand",
        ["history_resident_jobs"] = _jobs.Count,
        ["history_waiters"] = _completions.Count,
        ["tools_registered"] = _tools.Tools.List().Count,
        ["recipes_registered"] = _tools.Recipes.List().Count,
        ["git_ready"] = _tools.Tools.List().Any(tool => StringComparer.OrdinalIgnoreCase.Equals(tool.Name,"git")&&tool.ProbeVerified),
        ["dotnet_ready"] = _tools.Tools.List().Any(tool => StringComparer.OrdinalIgnoreCase.Equals(tool.Name,"dotnet")&&tool.ProbeVerified),
        ["pwsh_ready"] = _tools.Tools.List().Any(tool => StringComparer.OrdinalIgnoreCase.Equals(tool.Name,"pwsh")&&tool.ProbeVerified)
        , ["nsis_ready"] = _bundledNsis?.ProbeVerified == true || _tools.Tools.List().Any(tool => StringComparer.OrdinalIgnoreCase.Equals(tool.Name,"nsis")&&tool.ProbeVerified)
        , ["nsis_provider"] = _bundledNsis?.ProbeVerified == true ? "product" : _tools.Tools.List().Any(tool => StringComparer.OrdinalIgnoreCase.Equals(tool.Name,"nsis")&&tool.ProbeVerified) ? "registered" : "none"
        };
        try
        {
            var mode = _maintenanceMode.Read();
            data["maintenance_phase"] = mode?.Phase ?? "off";
            data["maintenance_closed"] = mode is { Phase: not "off" };
            if (mode is not null)
            {
                data["maintenance_epoch"] = mode.Epoch;
                data["maintenance_jid"] = mode.Jid;
                data["maintenance_rid"] = mode.Rid;
            }
        }
        catch (Exception exception)
        {
            data["maintenance_phase"] = "uncertain";
            data["maintenance_closed"] = true;
            data["maintenance_issue"] = "maintenance_state_unavailable";
            var diagnostic=MaintenanceReadDiagnostics.Describe(exception);
            data["maintenance_read_stage"]=diagnostic.Stage;
            data["maintenance_read_cause"]=diagnostic.Cause;
        }
        return data;
    }

    // A source failure closes only effect admission. Read/status paths never
    // depend on a healthy maintenance file, so diagnosis stays available.
    private sealed class UnavailableMaintenanceModeSource:IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read()=>throw new AgentOpsException("maintenance_state_unavailable");
        public bool IsAcknowledged(MaintenanceEffect effect)=>false;
    }

    // Optional host enrollment/probes are effects too. Register ownership before
    // the callback so a concurrent activation cannot mistake startup for idle.
    internal async Task<bool> TryRunStartupEffectAsync(Func<CancellationToken,Task<RegisteredToolDefinition?>> initialize,CancellationToken cancellationToken)
    {
        using var admission = _userHostAdmission?.TryEnter();
        if (_userHostAdmission is not null && admission is null) return false;
        MaintenanceLease effect;
        lock(_admissionGate)
        {
            if(MaintenanceClosed())return false;
            effect=RegisterMaintenanceEffect(new("tool",new Dictionary<string,string>{{"op","startup"}}),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),0);
        }
        using(effect)_bundledNsis=await initialize(cancellationToken);
        return true;
    }

    private bool MaintenanceClosed()
    {
        try { return _maintenanceMode.Read() is { Phase: not "off" }; }
        catch (Exception) { return true; }
    }

    private static OperationReceipt MaintenanceBusy(OperationRequest request,string jid,string rid) =>
        new(false,jid,rid,"failed",OperationNames.Public(request),"maintenance_busy",
            Data:new Dictionary<string,object?>{{"execution",OperationExecutionState.NotDispatched}});

    private MaintenanceLease RegisterMaintenanceEffect(OperationRequest request,string jid,string rid,long sequence)
    {
        // Caller holds admissionGate. The digest identifies a live dispatch; it
        // is not an original committed receipt and Live forbids acknowledgement.
        var bytes=JsonSerializer.SerializeToUtf8Bytes(request);
        string digest;
        try { digest=Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var handle=Guid.NewGuid().ToString("N");
        _activeEffects.Add(handle,new("user",jid,rid,"running",OperationNames.Public(request),digest,sequence,true));
        return new MaintenanceLease(this,handle);
    }

    private bool HasActiveEffect(string jid)=>_activeEffects.Values.Any(effect=>effect.Jid==jid);

    private sealed class MaintenanceLease(AgentOpsEngine owner,string handle):IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock(owner._admissionGate)
            {
                if(_disposed)return;
                _disposed=true;
                owner._activeEffects.Remove(handle);
            }
        }
    }

    private bool IsAcknowledgedUnknown(string jid)
    {
        if(_queued.ContainsKey(jid)||_jobs.ContainsKey(jid)||HasActiveEffect(jid))return false;
        var effect=_store.FindMaintenanceEffect(jid);
        if(effect is null||effect.State is not ("running" or "recovery_required" or "attention"))return false;
        try { return _maintenanceMode.IsAcknowledged(effect); }
        catch(Exception) { return false; }
    }

    // Only the authenticated private service callback invokes this method. No
    // public operation, caller-supplied label, or request marker reaches it.
    internal MaintenanceProbeResult ProbeMaintenance(MaintenanceProbeRequest request,MaintenanceHostIdentity host)
    {
        if(request.Version!=1||request.After<0||request.Action is not ("snapshot" or "effect")||
            request.Action=="effect"&&string.IsNullOrWhiteSpace(request.Jid))throw new AgentOpsException("invalid");
        lock(_admissionGate)
        {
            MaintenanceModeRecord? mode;
            try { mode=_maintenanceMode.Read(); }
            catch(Exception) { return new(1,request.Epoch,host,[],request.After,false,_activeEffects.Count + (_userHostAdmission?.ActiveCount ?? 0),
                _applicationTransitions.Values.Count(value=>value.Mutation),"maintenance_state_unavailable"); }
            if(request.Action=="snapshot"&&(mode is null||mode.Epoch!=request.Epoch))throw new AgentOpsException("maintenance_epoch_mismatch");
            List<MaintenanceEffect> effects;
            long next;
            bool more;
            if(request.Action=="effect")
            {
                var effect=_store.FindMaintenanceEffect(request.Jid!);
                effects=effect is null?[]:[effect];
                next=effect?.Sequence??request.After;
                more=false;
            }
            else
            {
                var page=_store.ListMaintenanceEffects(request.After);
                effects=page.Effects.ToList();next=page.Next;more=page.HasMore;
            }
            for(var index=0;index<effects.Count;index++)
            {
                var effect=effects[index];
                effects[index]=effect with{Live=_queued.ContainsKey(effect.Jid)||_jobs.ContainsKey(effect.Jid)||HasActiveEffect(effect.Jid)};
            }
            if(request.Action=="snapshot"&&request.After==0)
            {
                foreach(var live in _activeEffects.Values)
                {
                    if(effects.Count==8)break;
                    if(!effects.Any(effect=>effect.Jid==live.Jid)&&_store.FindMaintenanceEffect(live.Jid) is null)effects.Add(live);
                }
            }
            return new(1,request.Epoch,host,effects,next,more,_activeEffects.Count + (_userHostAdmission?.ActiveCount ?? 0),
                _applicationTransitions.Values.Count(value=>value.Mutation));
        }
    }

    private void PumpRecovery()
    {
        while (!_stopping)
        {
            if (MaintenanceClosed()) { _recoveryPending = true; return; }
            var job = _store.LoadNextRecoverable(_recoverySequence, _recoveryCeiling, _scheduler.MaximumPayloadBytes);
            if (job is null) { _recoveryPending = false; return; }
            if (IsAcknowledgedUnknown(job.Receipt.Jid)) { _recoverySequence = job.Sequence; continue; }
            if (job.Receipt.State == "running" || _directOnly || job.Request is null ||
                !_scheduler.CanEverAccept(job.Request) ||
                job.Request.Op == "app" || job.Request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            {
                var unresolved = job.Receipt with { Ok = false, State = "recovery_required", Err = "recovery_required" };
                _store.Update(job with { Receipt = WindowsServiceReceiptForStore(unresolved,job.Request), Request = null });
                _recoverySequence = job.Sequence;
                continue;
            }
            if (!_scheduler.CanAccept(job.Request)) { _recoveryPending = true; return; }
            _jobs[job.Receipt.Jid] = job;
            EnqueueClaimed(job, job.Request, CancellationToken.None);
            _recoverySequence = job.Sequence;
        }
    }

    private void CancelQueued(string jid)
    {
        lock (_admissionGate)
        {
            if (MaintenanceClosed()) return;
            var job = _store.Find(jid);
            if (job?.Receipt.State != "queued") return;
            var canceled = job with { Receipt = job.Receipt with { Ok = false, State = "canceled", Err = "canceled" }, Request = null };
            if (_store.TryCancelQueued(canceled))
            {
                _jobs.TryRemove(jid, out _);
                if (_completions.TryRemove(jid, out var completion)) completion.TrySetResult(canceled.Receipt);
                if (_queued.TryGetValue(jid, out var item)) item.Execution.Cancel();
            }
        }
    }

    private IReadOnlyDictionary<string, object?> CancelJob(string jid)
    {
        lock (_admissionGate)
        {
            if (MaintenanceClosed()) throw new AgentOpsException("maintenance_busy");
            CancelQueued(jid);
            var job = _store.Find(jid) ?? throw new AgentOpsException("not_found");
            if (job.Receipt.State == "running" && _queued.TryGetValue(jid, out var item))
            {
                item.Execution.Cancel();
                return new Dictionary<string, object?> { ["rec"] = job.Receipt, ["cancel_requested"] = true };
            }
            return new Dictionary<string, object?> { ["rec"] = job.Receipt };
        }
    }
    private static string Required(OperationRequest req, string key) => req.Arg.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new AgentOpsException("invalid");
    private static int Int(OperationRequest req, string key, int fallback) => req.Arg.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    private static bool Bool(OperationRequest req,string key)=>req.Arg.TryGetValue(key,out var value)&&(value=="1"||bool.TryParse(value,out var parsed)&&parsed);
    private static string RequiredBody(OperationRequest req)=>req.Body??throw new OperationResultException(req.Arg.ContainsKey(OperationRequest.BodyOmittedKey)?"recovery_required":"invalid",new Dictionary<string,object?>(),req.Arg.ContainsKey(OperationRequest.BodyOmittedKey));

    private sealed class OperationResultException(string code,IReadOnlyDictionary<string,object?> data,bool partial=false,OperationFailureEvidence? failure=null):Exception(code){public string Code{get;}=code;public IReadOnlyDictionary<string,object?> DataFields{get;}=data;public bool Partial{get;}=partial;public OperationFailureEvidence? Failure{get;}=failure;}

    public async ValueTask DisposeAsync()
    {
        lock (_admissionGate) { _stopping = true; _stop.Cancel(); }
        if (_windowsNetworkResolution is not null) await _windowsNetworkResolution.DisposeAsync();
        await DrainWindowsNetworkResolutionAsync();
        if (_terminals is not null) await _terminals.DisposeAsync();
        if (_applications is not null) await _applications.DisposeAsync();
        await _scheduler.DisposeAsync();
        foreach (var completion in _completions.Values) completion.TrySetCanceled();
        _feedback.Dispose(); _store.Dispose(); _ownerLock.Dispose(); _stop.Dispose();
    }
}
