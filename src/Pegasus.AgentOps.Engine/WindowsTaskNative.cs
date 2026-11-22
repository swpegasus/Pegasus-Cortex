using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Pegasus.AgentOps.Engine;

// Fixed local Task Scheduler 2 COM only. No caller-selected class/interface/member,
// connection target, identity, XML, security setter, or runtime Run parameters.
// IIDs and vtable order come from Microsoft's SDK taskschd.h:
// https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/taskschd.h
// The four IDispatch prefix slots are declared explicitly; calls use fixed typed
// vtables, not Invoke, reflection, dynamic, ProgIDs, or guessed DISPIDs.
internal sealed class NativeWindowsTasks : IWindowsTaskPlatform
{
    internal static string ErrorCode(int status) => unchecked((uint)status) switch
    {
        0x80070002 or 0x80070003 => "not_found",
        0x80070005 => "task_access_denied",
        0x8004130F => "task_account_unavailable",
        0x80041326 => "task_disabled",
        0x80041315 or 0x800706BA => "task_scheduler_unavailable",
        _ => "task_native_failed"
    };

    public IWindowsTaskSession Connect(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new WindowsTaskNativeException("task_platform_unavailable");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            throw new WindowsTaskNativeException("task_worker_context_required");
        token.ThrowIfCancellationRequested();
        return new Session(token);
    }

    [SupportedOSPlatform("windows")]
    private sealed class ComLease<T>(T value) : IDisposable where T : class
    {
        private T? _value = value;
        internal T Value => _value ?? throw new ObjectDisposedException(nameof(ComLease<T>));
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _value, null);
            // One release for this acquired COM output. Cast/interface views do
            // not acquire another ownership entry and are never released twice.
            if (value is not null) Marshal.ReleaseComObject(value);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ComLease<T> Take<T>(int status, T? value) where T : class
    {
        if (status < 0 || value is null)
        {
            if (value is not null) Marshal.ReleaseComObject(value);
            CheckStatus(status);
            throw new WindowsTaskNativeException("task_native_failed");
        }
        return new(value);
    }

    private static void CheckStatus(int status)
    { if (status < 0) throw new WindowsTaskNativeException(ErrorCode(status), status); }
    private delegate int StringGetter(out nint value);
    private delegate int IntegerGetter(out int value);
    private delegate int BooleanGetter(out bool value);
    private delegate int DateGetter(out double value);

    private static string String(StringGetter getter, int maximum, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        nint value = 0;
        try
        {
            CheckStatus(getter(out value));
            if (value == 0) return string.Empty;
            var bytes = Marshal.ReadInt32(value, -4);
            if (bytes < 0 || (bytes & 1) != 0 || bytes / 2 > maximum) throw new WindowsTaskNativeException("task_property_too_large");
            var text = Marshal.PtrToStringBSTR(value);
            if (text.Contains('\0') || !WindowsTaskOperationContract.WellFormed(text))
                throw new WindowsTaskNativeException("task_property_invalid");
            return text;
        }
        finally { if (value != 0) Marshal.ZeroFreeBSTR(value); }
    }

    private static string Digest(StringGetter getter, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint value = 0;
        byte[]? bytes = null;
        try
        {
            CheckStatus(getter(out value));
            if (value == 0) throw new WindowsTaskNativeException("task_fingerprint_unavailable");
            var length = Marshal.ReadInt32(value, -4);
            if (length < 0 || (length & 1) != 0 || length / 2 > maximum) throw new WindowsTaskNativeException("task_property_too_large");
            bytes = new byte[length];
            if (length != 0) Marshal.Copy(value, bytes, 0, length);
            // Definition contents are never converted into a managed string,
            // parsed into output, logged, or retained. Hash exact UTF-16 bytes.
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (value != 0) Marshal.ZeroFreeBSTR(value);
        }
    }
    private static int Integer(IntegerGetter getter, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); CheckStatus(getter(out var value)); return value; }
    private static bool Boolean(BooleanGetter getter, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); CheckStatus(getter(out var value)); return value; }
    private static int Count(IntegerGetter getter, CancellationToken token = default)
    {
        var count = Integer(getter, token);
        if (count < 0) throw new WindowsTaskNativeException("task_collection_changed");
        return count;
    }
    private static string State(int value) => value switch { 1 => "disabled", 2 => "queued", 3 => "ready", 4 => "running", _ => "unknown" };
    internal static WindowsTaskLocalTime? LocalTime(double value, TimeZoneInfo zone)
    {
        if (value == 0) return null; // Scheduler's unprovided DATE sentinel.
        DateTime local;
        try { local = DateTime.SpecifyKind(DateTime.FromOADate(value), DateTimeKind.Unspecified); }
        catch (ArgumentException) { throw new WindowsTaskNativeException("task_time_unavailable"); }
        var ambiguous = zone.IsAmbiguousTime(local);
        var invalid = zone.IsInvalidTime(local);
        var offset = ambiguous || invalid ? null : zone.GetUtcOffset(local).ToString("c", CultureInfo.InvariantCulture);
        return new(local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            "scheduler_local_ole_date", zone.Id, ambiguous, invalid, offset);
    }
    private static WindowsTaskLocalTime? Date(DateGetter getter, CancellationToken token)
    { token.ThrowIfCancellationRequested(); CheckStatus(getter(out var value)); return LocalTime(value, TimeZoneInfo.Local); }

    [SupportedOSPlatform("windows")]
    private sealed class Session : IWindowsTaskSession
    {
        private readonly ComLease<TaskService> _service;
        internal Session(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            object created = new TaskSchedulerClass();
            try { _service = new((TaskService)created); }
            catch { Marshal.ReleaseComObject(created); throw; }
            try
            {
                // All four are VT_EMPTY: local Scheduler, actual process token.
                token.ThrowIfCancellationRequested(); CheckStatus(_service.Value.Connect(null, null, null, null));
                if (!Boolean(_service.Value.get_Connected, token)) throw new WindowsTaskNativeException("task_scheduler_unavailable");
            }
            catch { _service.Dispose(); throw; }
        }
        private ComLease<TaskFolder> Folder(string path, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Take(_service.Value.GetFolder(path, out var folder), folder);
        }

        public IWindowsTaskEnumeration OpenList(string folder, string kind, CancellationToken token = default)
        {
            using var parent = Folder(folder, token);
            token.ThrowIfCancellationRequested();
            return kind == "folders"
                ? new FolderEnumeration(Take(parent.Value.GetFolders(0, out var folders), folders))
                : new TaskEnumeration(Take(parent.Value.GetTasks(1, out var tasks), tasks)); // Include hidden, still ACL-filtered.
        }

        public IWindowsTaskHandle? OpenTask(string taskPath, CancellationToken token = default)
        {
            var folder = Folder(WindowsTaskOperationContract.Parent(taskPath), token);
            try
            {
                token.ThrowIfCancellationRequested();
                var name = WindowsTaskOperationContract.Leaf(taskPath);
                var status = folder.Value.GetTask(name, out var task);
                if (unchecked((uint)status) is 0x80070002 or 0x80070003)
                {
                    if (task is not null) Marshal.ReleaseComObject(task);
                    folder.Dispose(); return null;
                }
                return new TaskHandle(Take(status, task), folder, name);
            }
            catch { folder.Dispose(); throw; }
        }

        public IWindowsTaskRegistration PrepareRegistration(string taskPath, IWindowsTaskHandle? existing,
            ToolInvocation invocation, WindowsTaskSchedule schedule, string ownerSid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var folder = Folder(WindowsTaskOperationContract.Parent(taskPath), token);
            ComLease<TaskDefinition>? definition = null;
            try
            {
                definition = existing switch
                {
                    null => Take(_service.Value.NewTask(0, out var created), created),
                    TaskHandle owned => owned.Definition(token),
                    _ => throw new WindowsTaskNativeException("task_native_failed")
                };
                // Update operates on the retained definition copy. It replaces
                // only the one Exec action and schedule and disables the task;
                // other existing settings/registration metadata remain intact.
                Configure(definition.Value, existing is null, invocation, schedule, ownerSid, token);
                string? sddl = existing is TaskHandle retained ? retained.SecurityDescriptor(token) : null;
                return new Registration(folder, definition, WindowsTaskOperationContract.Leaf(taskPath), ownerSid,
                    invocation, schedule, existing is null, sddl, token);
            }
            catch { definition?.Dispose(); folder.Dispose(); throw; }
        }

        public void Dispose() => _service.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class TaskEnumeration(ComLease<TaskCollection> collection) : IWindowsTaskEnumeration
    {
        public int Count => NativeWindowsTasks.Count(collection.Value.get_Count);
        public WindowsTaskSummary At(int index, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using var item = Take(collection.Value.get_Item(index, out var task), task);
            var path = String(item.Value.get_Path, WindowsTaskOperationContract.MaximumPathCharacters, token);
            return new(path, "tasks", State(Integer(item.Value.get_State, token)), Boolean(item.Value.get_Enabled, token));
        }
        public void Dispose() => collection.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class FolderEnumeration(ComLease<FolderCollection> collection) : IWindowsTaskEnumeration
    {
        public int Count => NativeWindowsTasks.Count(collection.Value.get_Count);
        public WindowsTaskSummary At(int index, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using var item = Take(collection.Value.get_Item(index, out var folder), folder);
            return new(String(item.Value.get_Path, WindowsTaskOperationContract.MaximumPathCharacters, token), "folders");
        }
        public void Dispose() => collection.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class TaskHandle(ComLease<RegisteredTask> task, ComLease<TaskFolder>? parent = null, string? name = null) : IWindowsTaskHandle
    {
        internal ComLease<TaskDefinition> Definition(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Take(task.Value.get_Definition(out var value), value); }
        internal string SecurityDescriptor(CancellationToken token) =>
            String((out nint value) => task.Value.GetSecurityDescriptor(7, out value), WindowsTaskOperationContract.MaximumSecurityCharacters, token);

        public WindowsTaskDetails Read(string ownerSid, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (parent is not null && name is not null)
            {
                // RegisteredTask has no Refresh. Reopen the exact name through
                // the retained local folder, then retain this acquired reference
                // through the ensuing action; never rely on an old definition copy.
                var fresh = Take(parent.Value.GetTask(name, out var observed), observed);
                var prior = task; task = fresh; prior.Dispose();
            }
            var path = String(task.Value.get_Path, WindowsTaskOperationContract.MaximumPathCharacters, token);
            WindowsTaskOperationContract.RequirePath(path);
            var state = State(Integer(task.Value.get_State, token));
            var enabled = Boolean(task.Value.get_Enabled, token);
            var definitionSha = Digest(task.Value.get_Xml, WindowsTaskOperationContract.MaximumXmlCharacters, token);
            var securitySha = Digest((out nint value) => task.Value.GetSecurityDescriptor(7, out value),
                WindowsTaskOperationContract.MaximumSecurityCharacters, token);
            using var definition = Definition(token);
            using var principal = Take(definition.Value.get_Principal(out var p), p);
            var user = String(principal.Value.get_UserId, 256, token);
            var group = String(principal.Value.get_GroupId, 256, token);
            var logon = Integer(principal.Value.get_LogonType, token);
            var level = Integer(principal.Value.get_RunLevel, token);
            var owner = StringComparer.Ordinal.Equals(user, ownerSid) && group.Length == 0 && logon == 3 && level == 0;
            var identity = owner ? "owner_interactive_limited" : user.StartsWith("S-", StringComparison.Ordinal) && group.Length == 0
                ? "different_or_noninteractive_identity" : "unverified_principal";
            using var settings = Take(definition.Value.get_Settings(out var s), s);
            var hidden = Boolean(settings.Value.get_Hidden, token);
            var demand = Boolean(settings.Value.get_AllowDemandStart, token);
            using var actions = Take(definition.Value.get_Actions(out var a), a);
            using var triggers = Take(definition.Value.get_Triggers(out var t), t);
            var actionCount = Count(actions.Value.get_Count, token);
            var triggerCount = Count(triggers.Value.get_Count, token);
            var supported = owner && actionCount == 1 && triggerCount <= 1;
            if (supported)
            {
                using var action = Take(actions.Value.get_Item(1, out var candidate), candidate);
                supported = Integer(action.Value.get_Type, token) == 0;
                if (supported && triggerCount == 1)
                {
                    using var trigger = Take(triggers.Value.get_Item(1, out var candidateTrigger), candidateTrigger);
                    supported = Integer(trigger.Value.get_Type, token) is 1 or 2;
                }
            }
            var keyBytes = Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "\n" + definitionSha + "\n" + securitySha);
            string sha;
            try { sha = Convert.ToHexStringLower(SHA256.HashData(keyBytes)); }
            finally { CryptographicOperations.ZeroMemory(keyBytes); }
            var lastRun = Date(task.Value.get_LastRunTime, token);
            var nextRun = Date(task.Value.get_NextRunTime, token);
            var lastResult = Integer(task.Value.get_LastTaskResult, token);
            if (definitionSha != Digest(task.Value.get_Xml, WindowsTaskOperationContract.MaximumXmlCharacters, token) ||
                securitySha != Digest((out nint value) => task.Value.GetSecurityDescriptor(7, out value),
                    WindowsTaskOperationContract.MaximumSecurityCharacters, token))
                throw new WindowsTaskNativeException("task_collection_changed");
            return new(path, state, enabled, hidden, demand, sha, definitionSha, securitySha, identity,
                level == 0 ? "least" : level == 1 ? "highest" : "unknown", actionCount, triggerCount, supported,
                lastRun, nextRun, lastResult);
        }
        public IWindowsTaskInstanceEnumeration OpenInstances(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return new InstanceEnumeration(Take(task.Value.GetInstances(0, out var collection), collection));
        }
        public WindowsTaskRunResult Run()
        {
            // No caller Run values, identity overrides, flags, or session routing.
            var status = task.Value.RunEx(null, 0, 0, null, out var running);
            using var instance = running is null ? null : new RunningHandle(new(running));
            CheckStatus(status);
            return new(status, instance?.Read());
        }
        public int SetEnabled(bool enabled) => task.Value.put_Enabled(enabled);
        public int Remove()
        {
            if (parent is null || name is null) throw new WindowsTaskNativeException("task_native_failed");
            return parent.Value.DeleteTask(name, 0);
        }
        public void Dispose()
        {
            try { task.Dispose(); }
            finally { parent?.Dispose(); }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class InstanceEnumeration(ComLease<RunningCollection> collection) : IWindowsTaskInstanceEnumeration
    {
        public int Count => NativeWindowsTasks.Count(collection.Value.get_Count);
        public IWindowsTaskRunningHandle Open(int index) =>
            new RunningHandle(Take(collection.Value.get_Item(index, out var running), running));
        public WindowsTaskInstance At(int index, CancellationToken token = default) { token.ThrowIfCancellationRequested(); using var running = Open(index); return running.Read(token); }
        public void Dispose() => collection.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class RunningHandle(ComLease<RunningTask> running) : IWindowsTaskRunningHandle
    {
        public WindowsTaskInstance Read(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); CheckStatus(running.Value.Refresh());
            var guidText = String(running.Value.get_InstanceGuid, 64, token);
            if (!Guid.TryParse(guidText, out var guid) || guid == Guid.Empty) throw new WindowsTaskNativeException("task_instance_unavailable");
            var path = String(running.Value.get_Path, WindowsTaskOperationContract.MaximumPathCharacters, token);
            WindowsTaskOperationContract.RequirePath(path);
            return new(guid.ToString("D"), path, State(Integer(running.Value.get_State, token)));
        }
        public int Stop() => running.Value.Stop();
        public void Dispose() => running.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class Registration(ComLease<TaskFolder> folder, ComLease<TaskDefinition> definition, string name,
        string ownerSid, ToolInvocation invocation, WindowsTaskSchedule schedule, bool create, string? sddl, CancellationToken token) : IWindowsTaskRegistration
    {
        public WindowsTaskRegistrationResult Register()
        {
            // Disabled even if a time trigger is already due. Update-only never
            // adopts a missing task, and create-only never overwrites one.
            var flags = (create ? 2 : 4 | 0x10) | 8 | 0x20;
            var status = folder.Value.RegisterTaskDefinition(name, definition.Value, flags, ownerSid, null, 3, sddl, out var value);
            var task = value is null ? null : new TaskHandle(new(value));
            try
            {
                CheckStatus(status);
                if (task is null) throw new WindowsTaskNativeException("task_postcondition_unavailable");
                Verify(task, invocation, schedule, ownerSid, token);
                return new(status, task);
            }
            catch { task?.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { definition.Dispose(); }
            finally { folder.Dispose(); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Configure(TaskDefinition definition, bool create, ToolInvocation invocation, WindowsTaskSchedule schedule,
        string ownerSid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var principal = Take(definition.get_Principal(out var p), p);
        if (create)
        {
            CheckStatus(principal.Value.put_Id("CortexOwner"));
            token.ThrowIfCancellationRequested(); CheckStatus(principal.Value.put_UserId(ownerSid));
            token.ThrowIfCancellationRequested(); CheckStatus(principal.Value.put_LogonType(3));
            token.ThrowIfCancellationRequested(); CheckStatus(principal.Value.put_RunLevel(0));
        }
        else if (String(principal.Value.get_UserId, 256, token) != ownerSid ||
            String(principal.Value.get_GroupId, 256, token).Length != 0 ||
            Integer(principal.Value.get_LogonType, token) != 3 || Integer(principal.Value.get_RunLevel, token) != 0)
            throw new WindowsTaskNativeException("precondition");

        token.ThrowIfCancellationRequested();
        using var settings = Take(definition.get_Settings(out var s), s);
        CheckStatus(settings.Value.put_Enabled(false));
        if (create)
        {
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_AllowDemandStart(true));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_MultipleInstances(2)); // IgnoreNew.
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_DisallowStartIfOnBatteries(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_StopIfGoingOnBatteries(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_StartWhenAvailable(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_RunOnlyIfNetworkAvailable(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_RunOnlyIfIdle(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_WakeToRun(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_Hidden(false));
            token.ThrowIfCancellationRequested(); CheckStatus(settings.Value.put_ExecutionTimeLimit("PT0S"));
            token.ThrowIfCancellationRequested();
            using var info = Take(definition.get_RegistrationInfo(out var ri), ri);
            CheckStatus(info.Value.put_Source("Pegasus.Cortex.WindowsTask/1"));
        }

        token.ThrowIfCancellationRequested();
        using var actions = Take(definition.get_Actions(out var a), a);
        CheckStatus(actions.Value.Clear());
        token.ThrowIfCancellationRequested();
        using var action = Take(actions.Value.Create(0, out var actionValue), actionValue);
        var exec = (ExecAction)action.Value; // Interface view of the same owned RCW.
        CheckStatus(exec.put_Path(invocation.ExecutablePath));
        token.ThrowIfCancellationRequested(); CheckStatus(exec.put_Arguments(WindowsOwnerAdminLauncher.Arguments(invocation.Arguments)));
        token.ThrowIfCancellationRequested(); CheckStatus(exec.put_WorkingDirectory(invocation.WorkingDirectory));
        token.ThrowIfCancellationRequested(); CheckStatus(actions.Value.put_Context(String(principal.Value.get_Id, 64, token)));

        token.ThrowIfCancellationRequested();
        using var triggers = Take(definition.get_Triggers(out var t), t);
        CheckStatus(triggers.Value.Clear());
        if (schedule.Kind != "on_demand")
        {
            token.ThrowIfCancellationRequested();
            using var trigger = Take(triggers.Value.Create(schedule.Kind == "once" ? 1 : 2, out var triggerValue), triggerValue);
            CheckStatus(trigger.Value.put_StartBoundary(schedule.StartUtc!));
            token.ThrowIfCancellationRequested(); CheckStatus(trigger.Value.put_Enabled(true));
            if (schedule.Kind == "daily")
            {
                token.ThrowIfCancellationRequested();
                CheckStatus(((DailyTrigger)trigger.Value).put_DaysInterval(checked((short)schedule.DaysInterval!.Value)));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Verify(TaskHandle task, ToolInvocation expected, WindowsTaskSchedule schedule, string ownerSid, CancellationToken token)
    {
        using var definition = task.Definition(token);
        using var principal = Take(definition.Value.get_Principal(out var p), p);
        if (String(principal.Value.get_UserId, 256, token) != ownerSid || String(principal.Value.get_GroupId, 256, token).Length != 0 ||
            Integer(principal.Value.get_LogonType, token) != 3 || Integer(principal.Value.get_RunLevel, token) != 0)
            throw new WindowsTaskNativeException("task_postcondition_unavailable");
        using var settings = Take(definition.Value.get_Settings(out var s), s);
        if (Boolean(settings.Value.get_Enabled, token)) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        using var actions = Take(definition.Value.get_Actions(out var a), a);
        if (Count(actions.Value.get_Count, token) != 1) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        using var action = Take(actions.Value.get_Item(1, out var act), act);
        if (Integer(action.Value.get_Type, token) != 0) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        var exec = (ExecAction)action.Value;
        if (String(exec.get_Path, 32766, token) != expected.ExecutablePath ||
            String(exec.get_Arguments, 32766, token) != WindowsOwnerAdminLauncher.Arguments(expected.Arguments) ||
            String(exec.get_WorkingDirectory, 32766, token) != expected.WorkingDirectory)
            throw new WindowsTaskNativeException("task_postcondition_unavailable");
        using var triggers = Take(definition.Value.get_Triggers(out var t), t);
        var count = Count(triggers.Value.get_Count, token);
        if (count != (schedule.Kind == "on_demand" ? 0 : 1)) throw new WindowsTaskNativeException("task_postcondition_unavailable");
        if (count == 1)
        {
            using var trigger = Take(triggers.Value.get_Item(1, out var tr), tr);
            if (Integer(trigger.Value.get_Type, token) != (schedule.Kind == "once" ? 1 : 2) || !Boolean(trigger.Value.get_Enabled, token))
                throw new WindowsTaskNativeException("task_postcondition_unavailable");
            var boundary = String(trigger.Value.get_StartBoundary, 128, token);
            if (!DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual) ||
                !DateTimeOffset.TryParse(schedule.StartUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var desired) ||
                actual.ToUniversalTime() != desired.ToUniversalTime())
                throw new WindowsTaskNativeException("task_postcondition_unavailable");
            if (schedule.Kind == "daily")
            {
                token.ThrowIfCancellationRequested(); CheckStatus(((DailyTrigger)trigger.Value).get_DaysInterval(out var interval));
                if (interval != schedule.DaysInterval) throw new WindowsTaskNativeException("task_postcondition_unavailable");
            }
        }
    }

    [ComImport, Guid("0f87369f-a4e5-4cfc-bd3e-73e6154572dd")]
    private class TaskSchedulerClass { }

    [ComImport, Guid("2faba4c7-4da9-4013-9697-20cc3fd40f85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskService
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int GetFolder( [MarshalAs(UnmanagedType.BStr)] string path, out TaskFolder? folder);
        [PreserveSig] int GetRunningTasks(int flags, out RunningCollection? tasks);
        [PreserveSig] int NewTask(uint flags, out TaskDefinition? definition);
        [PreserveSig] int Connect( [MarshalAs(UnmanagedType.Struct)] object? server, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? domain, [MarshalAs(UnmanagedType.Struct)] object? password);
        [PreserveSig] int get_Connected([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int get_TargetServer(out nint value);
        [PreserveSig] int get_ConnectedUser(out nint value);
        [PreserveSig] int get_ConnectedDomain(out nint value);
        [PreserveSig] int get_HighestVersion(out uint version);
    }
    [ComImport, Guid("8cfac062-a080-4c15-9a88-aa7c2af80dfc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskFolder
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Name(out nint value);
        [PreserveSig] int get_Path(out nint value);
        [PreserveSig] int GetFolder( [MarshalAs(UnmanagedType.BStr)] string path, out TaskFolder? folder);
        [PreserveSig] int GetFolders(int flags, out FolderCollection? folders);
        [PreserveSig] int CreateFolder( [MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.Struct)] object? sddl, out TaskFolder? folder);
        [PreserveSig] int DeleteFolder( [MarshalAs(UnmanagedType.BStr)] string path, int flags);
        [PreserveSig] int GetTask( [MarshalAs(UnmanagedType.BStr)] string path, out RegisteredTask? task);
        [PreserveSig] int GetTasks(int flags, out TaskCollection? tasks);
        [PreserveSig] int DeleteTask( [MarshalAs(UnmanagedType.BStr)] string name, int flags);
        [PreserveSig] int RegisterTask( [MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xml, int flags, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? password, int logon, [MarshalAs(UnmanagedType.Struct)] object? sddl, out RegisteredTask? task);
        [PreserveSig] int RegisterTaskDefinition( [MarshalAs(UnmanagedType.BStr)] string path, TaskDefinition definition, int flags, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? password, int logon, [MarshalAs(UnmanagedType.Struct)] object? sddl, out RegisteredTask? task);
        [PreserveSig] int GetSecurityDescriptor(int information, out nint sddl);
        [PreserveSig] int SetSecurityDescriptor( [MarshalAs(UnmanagedType.BStr)] string sddl, int flags);
    }
    [ComImport, Guid("9c86f320-dee3-4dd1-b972-a303f26b061e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface RegisteredTask
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Name(out nint value);
        [PreserveSig] int get_Path(out nint value);
        [PreserveSig] int get_State(out int value);
        [PreserveSig] int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int Run( [MarshalAs(UnmanagedType.Struct)] object? parameters, out RunningTask? instance);
        [PreserveSig] int RunEx( [MarshalAs(UnmanagedType.Struct)] object? parameters, int flags, int sessionId, [MarshalAs(UnmanagedType.BStr)] string? user, out RunningTask? instance);
        [PreserveSig] int GetInstances(int flags, out RunningCollection? instances);
        [PreserveSig] int get_LastRunTime(out double date);
        [PreserveSig] int get_LastTaskResult(out int value);
        [PreserveSig] int get_NumberOfMissedRuns(out int value);
        [PreserveSig] int get_NextRunTime(out double date);
        [PreserveSig] int get_Definition(out TaskDefinition? value);
        [PreserveSig] int get_Xml(out nint value);
        [PreserveSig] int GetSecurityDescriptor(int information, out nint sddl);
        [PreserveSig] int SetSecurityDescriptor( [MarshalAs(UnmanagedType.BStr)] string sddl, int flags);
        [PreserveSig] int Stop(int flags);
        [PreserveSig] int GetRunTimes(nint start, nint end, ref uint count, out nint times);
    }
    [ComImport, Guid("653758fb-7b9a-4f1e-a471-beeb8e9b834e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface RunningTask
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Name(out nint value);
        [PreserveSig] int get_InstanceGuid(out nint value);
        [PreserveSig] int get_Path(out nint value);
        [PreserveSig] int get_State(out int value);
        [PreserveSig] int get_CurrentAction(out nint value);
        [PreserveSig] int Stop();
        [PreserveSig] int Refresh();
        [PreserveSig] int get_EnginePID(out uint pid);
    }
    [ComImport, Guid("f5bc8fc5-536d-4f77-b852-fbc1356fdeb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskDefinition
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_RegistrationInfo(out RegistrationInfo? value);
        [PreserveSig] int put_RegistrationInfo(RegistrationInfo value);
        [PreserveSig] int get_Triggers(out TriggerCollection? value);
        [PreserveSig] int put_Triggers(TriggerCollection value);
        [PreserveSig] int get_Settings(out TaskSettings? value);
        [PreserveSig] int put_Settings(TaskSettings value);
        [PreserveSig] int get_Data(out nint value);
        [PreserveSig] int put_Data( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Principal(out Principal? value);
        [PreserveSig] int put_Principal(Principal value);
        [PreserveSig] int get_Actions(out ActionCollection? value);
        [PreserveSig] int put_Actions(ActionCollection value);
        [PreserveSig] int get_XmlText(out nint value);
        [PreserveSig] int put_XmlText( [MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("D98D51E5-C9B4-496a-A9C1-18980261CF0F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Principal
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Id(out nint value);
        [PreserveSig] int put_Id( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_DisplayName(out nint value);
        [PreserveSig] int put_DisplayName( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_UserId(out nint value);
        [PreserveSig] int put_UserId( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_LogonType(out int value);
        [PreserveSig] int put_LogonType(int value);
        [PreserveSig] int get_GroupId(out nint value);
        [PreserveSig] int put_GroupId( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_RunLevel(out int value);
        [PreserveSig] int put_RunLevel(int value);
    }
    [ComImport, Guid("02820E19-7B98-4ed2-B2E8-FDCCCEFF619B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ActionCollection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Count(out int value);
        [PreserveSig] int get_Item(int index, out TaskAction? action);
        [PreserveSig] int get__NewEnum(out nint value);
        [PreserveSig] int get_XmlText(out nint value);
        [PreserveSig] int put_XmlText( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int Create(int type, out TaskAction? action);
        [PreserveSig] int Remove( [MarshalAs(UnmanagedType.Struct)] object? index);
        [PreserveSig] int Clear();
        [PreserveSig] int get_Context(out nint value);
        [PreserveSig] int put_Context( [MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("BAE54997-48B1-4cbe-9965-D6BE263EBEA4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskAction
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Id(out nint value);
        [PreserveSig] int put_Id( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Type(out int value);
    }
    [ComImport, Guid("4c3d624d-fd6b-49a3-b9b7-09cb3cd3f047"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ExecAction
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Id(out nint value);
        [PreserveSig] int put_Id( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Type(out int value);
        [PreserveSig] int get_Path(out nint value);
        [PreserveSig] int put_Path( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Arguments(out nint value);
        [PreserveSig] int put_Arguments( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_WorkingDirectory(out nint value);
        [PreserveSig] int put_WorkingDirectory( [MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("85df5081-1b24-4f32-878a-d9d14df4cb77"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TriggerCollection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Count(out int value);
        [PreserveSig] int get_Item(int index, out Trigger? trigger);
        [PreserveSig] int get__NewEnum(out nint value);
        [PreserveSig] int Create(int type, out Trigger? trigger);
        [PreserveSig] int Remove( [MarshalAs(UnmanagedType.Struct)] object? index);
        [PreserveSig] int Clear();
    }
    [ComImport, Guid("09941815-ea89-4b5b-89e0-2a773801fac3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Trigger
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Type(out int value);
        [PreserveSig] int get_Id(out nint value);
        [PreserveSig] int put_Id( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Repetition(out nint value);
        [PreserveSig] int put_Repetition(nint value);
        [PreserveSig] int get_ExecutionTimeLimit(out nint value);
        [PreserveSig] int put_ExecutionTimeLimit( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_StartBoundary(out nint value);
        [PreserveSig] int put_StartBoundary( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_EndBoundary(out nint value);
        [PreserveSig] int put_EndBoundary( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
    }
    [ComImport, Guid("126c5cd8-b288-41d5-8dbf-e491446adc5c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DailyTrigger
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Type(out int value);
        [PreserveSig] int get_Id(out nint value);
        [PreserveSig] int put_Id( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Repetition(out nint value);
        [PreserveSig] int put_Repetition(nint value);
        [PreserveSig] int get_ExecutionTimeLimit(out nint value);
        [PreserveSig] int put_ExecutionTimeLimit( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_StartBoundary(out nint value);
        [PreserveSig] int put_StartBoundary( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_EndBoundary(out nint value);
        [PreserveSig] int put_EndBoundary( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_DaysInterval(out short days);
        [PreserveSig] int put_DaysInterval(short days);
        [PreserveSig] int get_RandomDelay(out nint value);
        [PreserveSig] int put_RandomDelay( [MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("8FD4711D-2D02-4c8c-87E3-EFF699DE127E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskSettings
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_AllowDemandStart([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_AllowDemandStart([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_RestartInterval(out nint value);
        [PreserveSig] int put_RestartInterval( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_RestartCount(out int value);
        [PreserveSig] int put_RestartCount(int value);
        [PreserveSig] int get_MultipleInstances(out int value);
        [PreserveSig] int put_MultipleInstances(int value);
        [PreserveSig] int get_StopIfGoingOnBatteries([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_StopIfGoingOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_DisallowStartIfOnBatteries([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_DisallowStartIfOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_AllowHardTerminate([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_AllowHardTerminate([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_StartWhenAvailable([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_StartWhenAvailable([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_XmlText(out nint value);
        [PreserveSig] int put_XmlText( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_RunOnlyIfNetworkAvailable([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_RunOnlyIfNetworkAvailable([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_ExecutionTimeLimit(out nint value);
        [PreserveSig] int put_ExecutionTimeLimit( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_DeleteExpiredTaskAfter(out nint value);
        [PreserveSig] int put_DeleteExpiredTaskAfter( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Priority(out int value);
        [PreserveSig] int put_Priority(int value);
        [PreserveSig] int get_Compatibility(out int value);
        [PreserveSig] int put_Compatibility(int value);
        [PreserveSig] int get_Hidden([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_Hidden([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_IdleSettings(out nint value);
        [PreserveSig] int put_IdleSettings(nint value);
        [PreserveSig] int get_RunOnlyIfIdle([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_RunOnlyIfIdle([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_WakeToRun([MarshalAs(UnmanagedType.VariantBool)] out bool value);
        [PreserveSig] int put_WakeToRun([MarshalAs(UnmanagedType.VariantBool)] bool value);
        [PreserveSig] int get_NetworkSettings(out nint value);
        [PreserveSig] int put_NetworkSettings(nint value);
    }
    [ComImport, Guid("416D8B73-CB41-4ea1-805C-9BE9A5AC4A74"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface RegistrationInfo
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Description(out nint value);
        [PreserveSig] int put_Description( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Author(out nint value);
        [PreserveSig] int put_Author( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Version(out nint value);
        [PreserveSig] int put_Version( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Date(out nint value);
        [PreserveSig] int put_Date( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_Documentation(out nint value);
        [PreserveSig] int put_Documentation( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_XmlText(out nint value);
        [PreserveSig] int put_XmlText( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_URI(out nint value);
        [PreserveSig] int put_URI( [MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_SecurityDescriptor([MarshalAs(UnmanagedType.Struct)] out object? variant);
        [PreserveSig] int put_SecurityDescriptor( [MarshalAs(UnmanagedType.Struct)] object? value);
        [PreserveSig] int get_Source(out nint value);
        [PreserveSig] int put_Source( [MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("79184a66-8664-423f-97f1-637356a5d812"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface FolderCollection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Count(out int value);
        [PreserveSig] int get_Item( [MarshalAs(UnmanagedType.Struct)] object? index, out TaskFolder? item);
        [PreserveSig] int get__NewEnum(out nint value);
    }
    [ComImport, Guid("86627eb4-42a7-41e4-a4d9-ac33a72f2d52"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TaskCollection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Count(out int value);
        [PreserveSig] int get_Item( [MarshalAs(UnmanagedType.Struct)] object? index, out RegisteredTask? item);
        [PreserveSig] int get__NewEnum(out nint value);
    }
    [ComImport, Guid("6a67614b-6828-4fec-aa54-6d52e8f1f2db"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface RunningCollection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
        [PreserveSig] int GetIdsOfNames(ref Guid iid, nint names, uint count, uint lcid, nint ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
        [PreserveSig] int get_Count(out int value);
        [PreserveSig] int get_Item( [MarshalAs(UnmanagedType.Struct)] object? index, out RunningTask? item);
        [PreserveSig] int get__NewEnum(out nint value);
    }
}
