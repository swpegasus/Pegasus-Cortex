using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Task paths are Scheduler names, not filesystem paths or Root/Folders grants.
public static class WindowsTaskOperationContract
{
    public const int Version = 1;
    public const int MaximumBodyBytes = 16 * 1024;
    public const int MaximumPageSize = 64;
    public const int MaximumEntries = 4096;
    public const int MaximumPageBytes = 256 * 1024;
    public const int MaximumXmlCharacters = 256 * 1024;
    public const int MaximumSecurityCharacters = 32 * 1024;
    public const int MaximumPathCharacters = 240;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[]
        { "list", "read", "instances", "prepare", "run", "stop", "enable", "disable", "remove", "create", "update" });
    public static bool IsKnownAction(string? action) => action is not null && Actions.Contains(action, StringComparer.Ordinal);
    public static bool IsReadOnly(string? action) => action is "list" or "read" or "instances" or "prepare";
    public static bool IsOwnerElevationAction(string? action) => action is "read" or "instances" or "run" or "stop" or "enable" or "disable" or "remove";
    public static bool IsSystemAction(string? action) => IsKnownAction(action) && action is not ("prepare" or "create" or "update");
    public static string PublicName(string action) => "windows_task_" + action;
    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool HasSchedulerExpansion(string value) => value.Contains('%') || value.Contains("$(Arg", StringComparison.OrdinalIgnoreCase);

    public static WindowsTaskInvocation Parse(OperationRequest request)
    {
        var action = request.Arg.GetValueOrDefault("op");
        if (request.Op != "windows_task" || !IsKnownAction(action) || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg.Keys.Any(key => key is not ("op" or "rid" or "wait" or "elevate")) ||
            request.Arg.TryGetValue("rid", out var rid) && rid != request.Rid)
            throw Invalid("arguments");
        if (request.Root is not null && (!IsSystemAction(action) || request.Root.Length is < 1 or > 128 || request.Root.Any(char.IsControl)))
            throw Invalid("root");
        var requiresOwnerElevation = request.Arg.TryGetValue("elevate", out var elevation);
        if (requiresOwnerElevation && (elevation != "uac" || !IsOwnerElevationAction(action) || request.Root is not null))
            throw Invalid("elevate");
        var mutation = !IsReadOnly(action);
        if (request.Rid is { } supplied && !ValidRid(supplied) || (mutation || requiresOwnerElevation) && request.Rid is null) throw Invalid("rid");
        var wait = true;
        if (request.Arg.TryGetValue("wait", out var waitText))
        {
            if (!(mutation || requiresOwnerElevation) || waitText is not ("true" or "false")) throw Invalid("wait");
            wait = waitText == "true";
        }
        if (request.Body is null || Encoding.UTF8.GetByteCount(request.Body) > MaximumBodyBytes) throw Invalid("text");
        try
        {
            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 5 });
            var allowed = action switch
            {
                "list" => new[] { "version", "folder", "kind", "max", "after" },
                "read" or "instances" => ["version", "task"],
                "prepare" => ["version", "tool", "toolVersion", "toolSha", "argv", "environmentPolicy", "workspace", "cwd"],
                "create" => ["version", "task", "tool", "toolVersion", "toolSha", "argv", "environmentPolicy", "workspace", "cwd", "schedule"],
                "update" => ["version", "task", "taskSha", "tool", "toolVersion", "toolSha", "argv", "environmentPolicy", "workspace", "cwd", "schedule"],
                "stop" => ["version", "task", "taskSha", "instance"],
                _ => ["version", "task", "taskSha"]
            };
            var fields = Fields(document.RootElement, allowed);
            if (Integer(fields, "version") != Version) throw Invalid("version");
            string? task = null, taskSha = null, after = null, instance = null;
            var folder = "\\";
            var kind = "tasks";
            var maximum = 32;
            WindowsTaskToolRequest? tool = null;
            WindowsTaskDefinition? definition = null;
            if (action == "list")
            {
                if (fields.ContainsKey("folder")) folder = Text(fields, "folder");
                RequirePath(folder, folder: true);
                if (fields.ContainsKey("kind")) kind = Text(fields, "kind");
                if (kind is not ("tasks" or "folders")) throw Invalid("kind");
                if (fields.ContainsKey("max")) maximum = Integer(fields, "max");
                if (maximum is < 1 or > MaximumPageSize) throw Invalid("max");
                if (fields.ContainsKey("after"))
                {
                    after = Text(fields, "after"); RequirePath(after, folder: true);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Parent(after), folder)) throw Invalid("after");
                }
            }
            else if (action != "prepare") { task = Text(fields, "task"); RequirePath(task); }
            if (mutation && action != "create")
            {
                taskSha = Text(fields, "taskSha");
                if (!IsDigest(taskSha)) throw Invalid("taskSha");
            }
            if (action == "stop")
            {
                instance = Text(fields, "instance");
                if (!Guid.TryParseExact(instance, "D", out var parsed) || parsed == Guid.Empty || parsed.ToString("D") != instance)
                    throw Invalid("instance");
            }
            if (action is "prepare" or "create" or "update")
            {
                var name = Text(fields, "tool");
                if (name.Length is < 1 or > 64 || !name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_'))
                    throw Invalid("tool");
                var policy = Text(fields, "environmentPolicy");
                if (policy != "scheduler") throw Invalid("environmentPolicy");
                int? toolVersion = fields.ContainsKey("toolVersion") ? Integer(fields, "toolVersion") : null;
                var toolSha = fields.ContainsKey("toolSha") ? Text(fields, "toolSha") : null;
                if ((toolVersion is null) != (toolSha is null) || toolVersion is < 1 || toolSha is not null && !IsDigest(toolSha) ||
                    action != "prepare" && toolVersion is null) throw Invalid("toolSha");
                if (!fields.TryGetValue("argv", out var argvElement) || argvElement.ValueKind != JsonValueKind.Array)
                    throw Invalid("argv");
                var argv = new List<string>();
                var bytes = 0;
                foreach (var value in argvElement.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || argv.Count >= 128) throw Invalid("argv");
                    var arg = value.GetString()!;
                    if (arg.Length > 8192 || arg.Contains('\0') || !WellFormed(arg) || HasSchedulerExpansion(arg)) throw Invalid("argv");
                    bytes = checked(bytes + Encoding.UTF8.GetByteCount(arg));
                    if (bytes > 12 * 1024) throw Invalid("argv");
                    argv.Add(arg);
                }
                var workspace = fields.ContainsKey("workspace") ? Text(fields, "workspace") : null;
                if (workspace is not null && (workspace.Length is < 1 or > 64 ||
                    !workspace.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_')))
                    throw Invalid("workspace");
                var cwd = fields.ContainsKey("cwd") ? Text(fields, "cwd") : null;
                if (cwd is not null && (cwd.Length is < 3 or > MaximumPathCharacters || !Path.IsPathFullyQualified(cwd) ||
                    cwd.Any(char.IsControl) || !WellFormed(cwd) || HasSchedulerExpansion(cwd))) throw Invalid("cwd");
                tool = new(name, argv.AsReadOnly(), policy, toolVersion, toolSha, workspace, cwd);
                if (action != "prepare")
                {
                    if (!fields.TryGetValue("schedule", out var scheduleElement)) throw Invalid("schedule");
                    var scheduleFields = Fields(scheduleElement, ["kind", "startUtc", "daysInterval"]);
                    var scheduleKind = Text(scheduleFields, "kind");
                    if (scheduleKind is not ("on_demand" or "once" or "daily")) throw Invalid("schedule");
                    string? start = null;
                    int? days = null;
                    if (scheduleKind == "on_demand")
                    {
                        if (scheduleFields.Count != 1) throw Invalid("schedule");
                    }
                    else
                    {
                        start = Text(scheduleFields, "startUtc");
                        if (!DateTime.TryParseExact(start, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ||
                            time.Kind != DateTimeKind.Utc || time.ToString("O", CultureInfo.InvariantCulture) != start) throw Invalid("startUtc");
                        if (scheduleKind == "daily")
                        {
                            days = Integer(scheduleFields, "daysInterval");
                            if (days is < 1 or > 365) throw Invalid("daysInterval");
                        }
                        else if (scheduleFields.ContainsKey("daysInterval")) throw Invalid("daysInterval");
                    }
                    definition = new(tool, new(scheduleKind, start, days));
                }
            }
            return new(action!, task, folder, kind, maximum, after, taskSha, instance, definition, tool, wait, requiresOwnerElevation);
        }
        catch (WindowsTaskContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("text"); }
    }

    public static void RequirePath(string value, bool folder = false)
    {
        if (value.Length is < 1 or > MaximumPathCharacters || value[0] != '\\' || !WellFormed(value) ||
            value.Any(c => char.IsControl(c) || c is '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')) throw Invalid("task");
        if (value == "\\") { if (!folder) throw Invalid("task"); return; }
        var segments = value[1..].Split('\\');
        if (segments.Length > 32 || segments.Any(segment => segment.Length is < 1 or > 128 ||
            segment is "." or ".." || segment != segment.Trim())) throw Invalid("task");
    }
    internal static string Parent(string value) => value.LastIndexOf('\\') is var position && position > 0 ? value[..position] : "\\";
    internal static string Leaf(string value) => value[(value.LastIndexOf('\\') + 1)..];
    internal static bool WellFormed(string value)
    { try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; } catch (EncoderFallbackException) { return false; } }
    private static bool ValidRid(string value) => value.Length is >= 1 and <= 128 && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("text");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid(property.Name);
        return fields;
    }
    private static string Text(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(name);
    private static int Integer(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Invalid(name);
    private static WindowsTaskContractException Invalid(string field) => new(field);
}

public sealed class WindowsTaskContractException(string field) : Exception("invalid")
{ public string Field { get; } = field; }

internal sealed record WindowsTaskToolRequest(string Tool, IReadOnlyList<string> Argv, string EnvironmentPolicy,
    int? DefinitionVersion = null, string? DefinitionSha = null, string? Workspace = null, string? Cwd = null)
{ public override string ToString() => "WindowsTaskToolRequest(Body=omitted)"; }
internal sealed record WindowsTaskToolPin(string Tool, int DefinitionVersion, string DefinitionSha);
internal sealed record WindowsTaskSchedule(string Kind, string? StartUtc = null, int? DaysInterval = null);
internal sealed record WindowsTaskDefinition(WindowsTaskToolRequest Tool, WindowsTaskSchedule Schedule);
internal interface IWindowsTaskToolResolver
{ WindowsTaskPreparedTool Prepare(WindowsTaskToolRequest request, CancellationToken cancellationToken); }
internal sealed class WindowsTaskPreparedTool(ToolInvocation invocation, WindowsTaskToolPin pin, IDisposable admissionLease) : IDisposable
{
    private IDisposable? _lease = admissionLease ?? throw new ArgumentNullException(nameof(admissionLease));
    internal ToolInvocation Invocation { get; } = invocation ?? throw new ArgumentNullException(nameof(invocation));
    internal WindowsTaskToolPin Pin { get; } = pin ?? throw new ArgumentNullException(nameof(pin));
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    public override string ToString() => "WindowsTaskPreparedTool(Body=omitted)";
}

public sealed class WindowsTaskInvocation
{
    public string Action { get; }
    public string? TaskPath { get; }
    public string Folder { get; }
    public string Kind { get; }
    public int Maximum { get; }
    public string? AfterPath { get; }
    public string? TaskSha { get; }
    public string? InstanceGuid { get; }
    public bool Wait { get; }
    public bool RequiresOwnerElevation { get; }
    public bool RequiresDurableHelper => RequiresOwnerElevation;
    public string RequestIdentity { get; }
    public bool IsMutation => !WindowsTaskOperationContract.IsReadOnly(Action);
    internal WindowsTaskDefinition? Definition { get; }
    internal WindowsTaskToolRequest? Tool { get; }
    internal WindowsTaskInvocation(string action, string? task, string folder, string kind, int maximum, string? after,
        string? taskSha, string? instance, WindowsTaskDefinition? definition, WindowsTaskToolRequest? tool, bool wait, bool requiresOwnerElevation = false)
    {
        Action = action; TaskPath = task; Folder = folder; Kind = kind; Maximum = maximum; AfterPath = after;
        TaskSha = taskSha; InstanceGuid = instance; Definition = definition; Tool = tool; Wait = wait; RequiresOwnerElevation = requiresOwnerElevation;
        // Keep the exact original serialization when no route selector is
        // supplied. Only the explicit UAC route adds a fingerprint member.
        var identityBytes = requiresOwnerElevation
            ? JsonSerializer.SerializeToUtf8Bytes(new
                { family = "windows_task", version = 1, action, task, folder, kind, maximum, after, taskSha, instance, definition, tool, elevate = "uac" })
            : JsonSerializer.SerializeToUtf8Bytes(new
                { family = "windows_task", version = 1, action, task, folder, kind, maximum, after, taskSha, instance, definition, tool });
        try { RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(identityBytes)); }
        finally { CryptographicOperations.ZeroMemory(identityBytes); }
    }
    public override string ToString() => $"WindowsTaskInvocation(Action={Action}, Body=omitted)";
}
