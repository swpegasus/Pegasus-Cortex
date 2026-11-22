using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal enum WindowsTaskAuthority { Owner, OwnerAdmin, System }
internal sealed record WindowsTaskLocalTime(string Value, string TimeBasis, string TimeZoneId,
    bool Ambiguous, bool Invalid, string? UtcOffset);
internal sealed record WindowsTaskSummary(string Path, string Kind, string? State = null, bool? Enabled = null);
internal sealed record WindowsTaskPage(string Folder, string Kind, IReadOnlyList<WindowsTaskSummary> Items,
    string? Next, bool HasMore, bool BoundReached, int Scanned,
    string Visibility = "caller_queryable_tasks_only", string Consistency = "fresh_live_collection_not_snapshot");
internal sealed record WindowsTaskDetails(string Path, string State, bool Enabled, bool Hidden, bool AllowDemandStart,
    string TaskSha, string DefinitionSha, string SecuritySha, string PrincipalKind, string RunLevel,
    int ActionCount, int TriggerCount, bool AuthoringSupported, WindowsTaskLocalTime? LastRun = null,
    WindowsTaskLocalTime? NextRun = null, int? LastTaskResult = null);
internal sealed record WindowsTaskInstance(string InstanceGuid, string TaskPath, string State);
internal sealed record WindowsTaskInstances(IReadOnlyList<WindowsTaskInstance> Items, int ObservedCount,
    bool HasMore, bool BoundReached, string Visibility = "caller_context_visible_instances_only");
internal sealed record WindowsTaskOutcome(bool Ok, string Action, string Phase = "observation", string? Error = null,
    bool ActionIssued = false, bool OutcomeUnknown = false, WindowsTaskDetails? Previous = null,
    WindowsTaskDetails? Current = null, WindowsTaskPage? Page = null, WindowsTaskInstances? Instances = null,
    WindowsTaskToolPin? Tool = null, int? NativeError = null, string? Warning = null,
    WindowsTaskInstance? Instance = null, string? Observation = null);

internal interface IWindowsTaskPlatform { IWindowsTaskSession Connect(CancellationToken token = default); }
internal interface IWindowsTaskSession : IDisposable
{
    IWindowsTaskEnumeration OpenList(string folder, string kind, CancellationToken token = default);
    IWindowsTaskHandle? OpenTask(string taskPath, CancellationToken token = default);
    IWindowsTaskRegistration PrepareRegistration(string taskPath, IWindowsTaskHandle? existing,
        ToolInvocation invocation, WindowsTaskSchedule schedule, string ownerSid, CancellationToken token);
}
internal interface IWindowsTaskEnumeration : IDisposable
{
    int Count { get; }
    WindowsTaskSummary At(int index, CancellationToken token = default);
}
internal interface IWindowsTaskInstanceEnumeration : IDisposable
{
    int Count { get; }
    WindowsTaskInstance At(int index, CancellationToken token = default);
    IWindowsTaskRunningHandle Open(int index);
}
internal interface IWindowsTaskHandle : IDisposable
{
    WindowsTaskDetails Read(string ownerSid, CancellationToken token = default);
    IWindowsTaskInstanceEnumeration OpenInstances(CancellationToken token = default);
    WindowsTaskRunResult Run();
    int SetEnabled(bool enabled);
    int Remove();
}
internal interface IWindowsTaskRunningHandle : IDisposable
{
    WindowsTaskInstance Read(CancellationToken token = default);
    int Stop();
}
internal interface IWindowsTaskRegistration : IDisposable { WindowsTaskRegistrationResult Register(); }
internal sealed record WindowsTaskRegistrationResult(int Status, IWindowsTaskHandle? Task);
internal sealed record WindowsTaskRunResult(int Status, WindowsTaskInstance? Instance);
internal sealed class WindowsTaskNativeException(string code, int? status = null) : Exception(code)
{
    internal string Code { get; } = code;
    internal int? Status { get; } = status;
}

// Root composition supplies authority, not request JSON. Windows still enforces
// task ACLs. No request switches identity, connects remotely, or escalates.
internal sealed class WindowsTaskOperations
{
    private readonly string _ownerSid;
    private readonly int _session;
    private readonly int? _expectedAdministratorSession;
    private readonly Func<LocalPeer> _current;
    private readonly OwnerHostIdentity _ordinary;
    private readonly IWindowsTaskPlatform _platform;
    private readonly IWindowsTaskToolResolver? _tools;
    internal WindowsTaskAuthority Authority { get; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SupportedOSPlatform("windows")]
    internal WindowsTaskOperations(string ownerSid, IWindowsTaskToolResolver? tools = null)
        : this(ownerSid, LocalIdentity.Current, new NativeWindowsTasks(), tools) { }

    [SupportedOSPlatform("windows")]
    internal static WindowsTaskOperations ForSystem(string registeredOwnerSid) =>
        new(registeredOwnerSid, LocalIdentity.Current, new NativeWindowsTasks(), authority: WindowsTaskAuthority.System);

    [SupportedOSPlatform("windows")]
    internal static WindowsTaskOperations ForOwnerAdministrator(string ownerSid, int expectedSession)
    {
        if (expectedSession <= 0) throw new ArgumentException("task_elevation_identity_invalid");
        var operations = new WindowsTaskOperations(ownerSid, LocalIdentity.Current, new NativeWindowsTasks(),
            authority: WindowsTaskAuthority.OwnerAdmin, expectedAdministratorSession: expectedSession);
        if (!operations.IsCurrentContext()) throw new AgentOpsException("auth");
        return operations;
    }

    internal WindowsTaskOperations(string ownerSid, Func<LocalPeer> current, IWindowsTaskPlatform platform,
        IWindowsTaskToolResolver? tools = null, WindowsTaskAuthority authority = WindowsTaskAuthority.Owner, int? expectedAdministratorSession = null)
    {
        _ordinary = new(ownerSid, current);
        _ownerSid = ownerSid; _current = current; _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _tools = tools; Authority = authority; _expectedAdministratorSession = expectedAdministratorSession;
        try { _session = current().Session; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { _session = -1; }
    }

    internal bool IsCurrentContext()
    {
        try
        {
            if (Authority == WindowsTaskAuthority.Owner) return _ordinary.IsCurrentOwner();
            var peer = _current();
            return Authority switch
            {
                WindowsTaskAuthority.System => peer.Sid == "S-1-5-18" && peer.Session == 0 && peer.Elevated,
                WindowsTaskAuthority.OwnerAdmin => (_expectedAdministratorSession ?? _session) > 0 && peer.Session == (_expectedAdministratorSession ?? _session) && peer.Sid == _ownerSid && peer.Elevated,
                _ => false
            };
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    // Execute is synchronous deliberately. Root awaits the ORIGINAL worker even
    // after caller cancellation; no COM operation or reference escapes that worker.
    internal WindowsTaskOutcome Execute(WindowsTaskInvocation invocation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var issued = false;
        var phase = "admission";
        WindowsTaskDetails? previous = null;
        WindowsTaskOutcome? outcome = null;
        try
        {
            Check(token);
            if (invocation.RequiresOwnerElevation && Authority != WindowsTaskAuthority.OwnerAdmin)
                return Failure(invocation, "task_owner_elevation_required");
            if (Authority == WindowsTaskAuthority.OwnerAdmin && !WindowsTaskOperationContract.IsOwnerElevationAction(invocation.Action))
                return Failure(invocation, "task_authoring_requires_ordinary_owner");
            if (Authority != WindowsTaskAuthority.Owner && invocation.Action is "prepare" or "create" or "update")
                return Failure(invocation, "task_authoring_requires_ordinary_owner");
            if (invocation.Action == "prepare")
            {
                if (_tools is null) return Failure(invocation, "task_tool_resolver_unavailable");
                using (var tool = _tools.Prepare(invocation.Tool!, token))
                {
                    ValidateTool(tool, invocation.Tool!);
                    Check(token);
                    outcome = new(true, invocation.Action, Tool: tool.Pin, Observation: "creation_time_tool_admission_only");
                }
            }
            else
            {
                using (var session = _platform.Connect(token))
                {
                    Check(token);
                    if (invocation.Action == "list") outcome = List(session, invocation, token);
                    else
                    {
                        phase = "open";
                        using var task = session.OpenTask(invocation.TaskPath!, token);
                        Check(token);
                        if (invocation.Action == "create")
                        {
                            if (task is not null) throw new WindowsTaskNativeException("precondition");
                            outcome = Register(session, null, invocation, token, ref issued, ref phase);
                        }
                        else
                        {
                            if (task is null) throw new WindowsTaskNativeException("not_found");
                            previous = task.Read(_ownerSid, token); RequireExactPath(previous.Path, invocation.TaskPath!);
                            Check(token);
                            if (invocation.Action == "read") outcome = new(true, invocation.Action, Current: previous);
                            else if (invocation.Action == "instances")
                                outcome = new(true, invocation.Action, Current: previous, Instances: ReadInstances(task, invocation, token));
                            else
                            {
                                RequireMutation(previous, invocation);
                                if (invocation.Action == "update")
                                    outcome = Register(session, task, invocation, token, ref issued, ref phase, previous);
                                else
                                {
                                    phase = "precondition";
                                    var latest = task.Read(_ownerSid, token); RequireExactPath(latest.Path, invocation.TaskPath!);
                                    RequireMutation(latest, invocation); Check(token);
                                    if (invocation.Action == "run")
                                    {
                                        if (!latest.Enabled || !latest.AllowDemandStart) throw new WindowsTaskNativeException("task_not_demand_startable");
                                        phase = "run"; issued = true;
                                        var run = task.Run(); RequireSuccess(run.Status);
                                        if (run.Instance is { } instance) RequireInstance(instance, invocation.TaskPath!);
                                        outcome = new(true, invocation.Action, phase, ActionIssued: true, Previous: previous,
                                            Instance: run.Instance, NativeError: run.Status == 0 ? null : run.Status,
                                            Warning: run.Status == 0 ? null : "scheduler_warning",
                                            Observation: run.Instance is null ? "run_request_acknowledged_instance_not_observed" : "instance_observed_not_completion");
                                    }
                                    else if (invocation.Action == "stop")
                                        outcome = Stop(task, invocation, previous, token, ref issued, ref phase);
                                    else
                                    {
                                        phase = invocation.Action; Check(token); issued = true;
                                        var status = invocation.Action == "remove" ? task.Remove() :
                                            task.SetEnabled(invocation.Action == "enable");
                                        RequireSuccess(status);
                                        WindowsTaskDetails? current;
                                        if (invocation.Action == "remove")
                                        {
                                            using var remaining = session.OpenTask(invocation.TaskPath!, token);
                                            if (remaining is not null) throw new WindowsTaskNativeException("task_postcondition_unavailable");
                                            current = null;
                                        }
                                        else
                                        {
                                            current = task.Read(_ownerSid, token); RequireExactPath(current.Path, invocation.TaskPath!);
                                            if (current.Enabled != (invocation.Action == "enable"))
                                                throw new WindowsTaskNativeException("task_postcondition_unavailable");
                                        }
                                        outcome = new(true, invocation.Action, phase, ActionIssued: true, Previous: previous, Current: current,
                                            NativeError: status == 0 ? null : status, Warning: status == 0 ? null : "scheduler_warning",
                                            Observation: invocation.Action == "remove" ? "registration_absent_running_instances_not_stopped" :
                                                invocation.Action == "disable" ? "disabled_running_instances_not_stopped" : "enabled_future_triggers_may_run");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            Check(token); // Also covers reference cleanup and the tool-lease boundary.
            if (outcome is not null && JsonSerializer.SerializeToUtf8Bytes(outcome, Json).Length > WindowsTaskOperationContract.MaximumPageBytes)
                return Failure(invocation, "task_result_too_large", issued, issued, phase);
            return outcome ?? Failure(invocation, "task_unavailable", issued, issued, phase);
        }
        catch (OperationCanceledException)
        { return Failure(invocation, "cancelled", issued, outcome?.OutcomeUnknown ?? issued, phase); }
        catch (WindowsTaskNativeException failure)
        {
            // Payload is discarded on identity loss; effect truth is retained.
            if (!IsCurrentContext()) return Failure(invocation, "owner_denied", issued, outcome?.OutcomeUnknown ?? issued, phase);
            return Failure(invocation, failure.Code, issued, outcome?.OutcomeUnknown ?? issued, phase, previous, failure.Status);
        }
        catch (COMException failure)
        {
            if (!IsCurrentContext()) return Failure(invocation, "owner_denied", issued, outcome?.OutcomeUnknown ?? issued, phase);
            return Failure(invocation, NativeWindowsTasks.ErrorCode(failure.HResult), issued, outcome?.OutcomeUnknown ?? issued,
                phase, previous, failure.HResult);
        }
        catch (AgentOpsException)
        { return Failure(invocation, IsCurrentContext() ? "task_tool_precondition" : "owner_denied", issued, outcome?.OutcomeUnknown ?? issued, phase); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { return Failure(invocation, IsCurrentContext() ? "task_unavailable" : "owner_denied", issued, outcome?.OutcomeUnknown ?? issued, phase); }
    }

    private WindowsTaskOutcome List(IWindowsTaskSession session, WindowsTaskInvocation invocation, CancellationToken token)
    {
        using var enumeration = session.OpenList(invocation.Folder, invocation.Kind, token);
        Check(token);
        Check(token); var count = enumeration.Count;
        if (count is < 0 or > WindowsTaskOperationContract.MaximumEntries)
            return new(false, invocation.Action, Error: "task_enumeration_bound",
                Page: new(invocation.Folder, invocation.Kind, [], null, true, true, 0));
        var selected = new SortedDictionary<string, WindowsTaskSummary>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= count; i++)
        {
            Check(token);
            var item = enumeration.At(i, token);
            WindowsTaskOperationContract.RequirePath(item.Path, folder: invocation.Kind == "folders");
            if (item.Kind != invocation.Kind || !StringComparer.OrdinalIgnoreCase.Equals(WindowsTaskOperationContract.Parent(item.Path), invocation.Folder))
                throw new WindowsTaskNativeException("task_collection_changed");
            if (invocation.AfterPath is not null && StringComparer.OrdinalIgnoreCase.Compare(item.Path, invocation.AfterPath) <= 0) continue;
            if (!selected.TryAdd(item.Path, item)) throw new WindowsTaskNativeException("task_collection_changed");
            if (selected.Count > invocation.Maximum + 1) selected.Remove(selected.Last().Key);
        }
        Check(token);
        if (enumeration.Count != count) throw new WindowsTaskNativeException("task_collection_changed");
        var candidates = selected.Values.ToList();
        var hasMore = candidates.Count > invocation.Maximum;
        if (hasMore) candidates.RemoveAt(candidates.Count - 1);
        var items = new List<WindowsTaskSummary>();
        var bytes = 4096; // Closed envelope reserve; final complete outcome is checked too.
        foreach (var item in candidates)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, Json).Length;
            if (bytes + size > WindowsTaskOperationContract.MaximumPageBytes) { hasMore = true; break; }
            bytes += size; items.Add(item);
        }
        if (hasMore && items.Count == 0) throw new WindowsTaskNativeException("task_result_too_large");
        return new(true, invocation.Action, Page: new(invocation.Folder, invocation.Kind, items.AsReadOnly(),
            hasMore ? items[^1].Path : null, hasMore, false, count));
    }

    private WindowsTaskInstances ReadInstances(IWindowsTaskHandle task, WindowsTaskInvocation invocation, CancellationToken token)
    {
        using var instances = task.OpenInstances(token);
        Check(token); var count = instances.Count;
        if (count < 0) throw new WindowsTaskNativeException("task_collection_changed");
        var limit = Math.Min(count, WindowsTaskOperationContract.MaximumPageSize);
        var items = new List<WindowsTaskInstance>();
        for (var i = 1; i <= limit; i++)
        {
            Check(token); var instance = instances.At(i, token); RequireInstance(instance, invocation.TaskPath!);
            if (items.Any(item => item.InstanceGuid == instance.InstanceGuid)) throw new WindowsTaskNativeException("task_collection_changed");
            items.Add(instance);
        }
        Check(token);
        if (instances.Count != count) throw new WindowsTaskNativeException("task_collection_changed");
        return new(items.AsReadOnly(), count, count > limit, count > WindowsTaskOperationContract.MaximumEntries);
    }

    private WindowsTaskOutcome Stop(IWindowsTaskHandle task, WindowsTaskInvocation invocation, WindowsTaskDetails previous,
        CancellationToken token, ref bool issued, ref string phase)
    {
        using var instances = task.OpenInstances(token);
        Check(token); var count = instances.Count;
        if (count is < 0 or > WindowsTaskOperationContract.MaximumEntries) throw new WindowsTaskNativeException("task_enumeration_bound");
        for (var i = 1; i <= count; i++)
        {
            Check(token);
            using var running = instances.Open(i);
            var observed = running.Read(token); RequireInstance(observed, invocation.TaskPath!);
            if (observed.InstanceGuid != invocation.InstanceGuid) continue;
            // Check both the task preimage and the exact retained instance at the
            // final boundary. Windows provides no atomic definition/instance CAS.
            RequireMutation(task.Read(_ownerSid, token), invocation);
            var latest = running.Read(token); RequireInstance(latest, invocation.TaskPath!);
            if (latest.InstanceGuid != invocation.InstanceGuid) throw new WindowsTaskNativeException("precondition");
            Check(token); phase = "stop"; issued = true;
            var status = running.Stop(); RequireSuccess(status);
            return new(true, invocation.Action, phase, ActionIssued: true, Previous: previous, Instance: latest,
                NativeError: status == 0 ? null : status, Warning: status == 0 ? null : "scheduler_warning",
                Observation: "exact_instance_stop_acknowledged_not_process_tree_completion");
        }
        throw new WindowsTaskNativeException("instance_not_found_in_caller_context");
    }

    private WindowsTaskOutcome Register(IWindowsTaskSession session, IWindowsTaskHandle? existing, WindowsTaskInvocation invocation,
        CancellationToken token, ref bool issued, ref string phase, WindowsTaskDetails? previous = null)
    {
        if (_tools is null) throw new WindowsTaskNativeException("task_tool_resolver_unavailable");
        if (previous is not null && !previous.AuthoringSupported) throw new WindowsTaskNativeException("task_definition_not_supported");
        using var tool = _tools.Prepare(invocation.Tool!, token);
        ValidateTool(tool, invocation.Tool!); Check(token);
        using var registration = session.PrepareRegistration(invocation.TaskPath!, existing, tool.Invocation, invocation.Definition!.Schedule, _ownerSid, token);
        Check(token);
        if (existing is null)
        {
            using var occupied = session.OpenTask(invocation.TaskPath!, token);
            if (occupied is not null) throw new WindowsTaskNativeException("precondition");
        }
        else RequireMutation(existing.Read(_ownerSid, token), invocation);
        Check(token); phase = "register"; issued = true;
        var result = registration.Register();
        using var registered = result.Task;
        RequireSuccess(result.Status);
        if (registered is null) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        var current = registered.Read(_ownerSid, token); RequireExactPath(current.Path, invocation.TaskPath!);
        if (current.Enabled || current.PrincipalKind != "owner_interactive_limited" || !current.AuthoringSupported ||
            current.ActionCount != 1) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        return new(true, invocation.Action, phase, ActionIssued: true, Previous: previous, Current: current, Tool: tool.Pin,
            NativeError: result.Status == 0 ? null : result.Status, Warning: result.Status == 0 ? null : "scheduler_warning",
            Observation: "registered_disabled_creation_time_tool_admission_only_scheduler_environment");
    }

    private void RequireMutation(WindowsTaskDetails current, WindowsTaskInvocation invocation)
    {
        RequireExactPath(current.Path, invocation.TaskPath!);
        if (current.TaskSha != invocation.TaskSha) throw new WindowsTaskNativeException("precondition");
        if (Authority == WindowsTaskAuthority.Owner && current.PrincipalKind != "owner_interactive_limited")
            throw new WindowsTaskNativeException("task_identity_requires_explicit_authority");
    }
    private static void ValidateTool(WindowsTaskPreparedTool prepared, WindowsTaskToolRequest request)
    {
        var invocation = prepared.Invocation;
        if (prepared.Pin.Tool != request.Tool || prepared.Pin.DefinitionVersion < 1 ||
            !WindowsTaskOperationContract.IsDigest(prepared.Pin.DefinitionSha) ||
            request.DefinitionVersion is { } expected && expected != prepared.Pin.DefinitionVersion ||
            request.DefinitionSha is { } sha && sha != prepared.Pin.DefinitionSha ||
            request.EnvironmentPolicy != "scheduler" || invocation.Mode != ToolExecutionMode.User ||
            !invocation.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(invocation.ExecutablePath) || !Path.IsPathFullyQualified(invocation.WorkingDirectory) ||
            WindowsTaskOperationContract.HasSchedulerExpansion(invocation.ExecutablePath) ||
            WindowsTaskOperationContract.HasSchedulerExpansion(invocation.WorkingDirectory) ||
            invocation.Arguments.Any(WindowsTaskOperationContract.HasSchedulerExpansion) ||
            !invocation.Arguments.SequenceEqual(request.Argv, StringComparer.Ordinal))
            throw new WindowsTaskNativeException("task_tool_precondition");
    }
    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentContext()) throw new WindowsTaskNativeException("owner_denied");
    }
    private static void RequireExactPath(string actual, string expected)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expected)) throw new WindowsTaskNativeException("precondition");
    }
    private static void RequireInstance(WindowsTaskInstance instance, string path)
    {
        RequireExactPath(instance.TaskPath, path);
        if (!Guid.TryParseExact(instance.InstanceGuid, "D", out var id) || id == Guid.Empty || id.ToString("D") != instance.InstanceGuid)
            throw new WindowsTaskNativeException("task_instance_unavailable");
    }
    private static void RequireSuccess(int status)
    { if (status < 0) throw new WindowsTaskNativeException(NativeWindowsTasks.ErrorCode(status), status); }
    private static WindowsTaskOutcome Failure(WindowsTaskInvocation invocation, string code, bool issued = false,
        bool unknown = false, string phase = "admission", WindowsTaskDetails? previous = null, int? status = null) =>
        new(false, invocation.Action, phase, code, issued, unknown, Previous: previous, NativeError: status);
}
