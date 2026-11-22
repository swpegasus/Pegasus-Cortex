using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pegasus.AgentOps.Engine;

/// <summary>Owns bounded application generations for one signed-in host lifetime, never a service or GUI lifetime.</summary>
public sealed partial class UserApplicationManager : IAsyncDisposable
{
    public const int MaxActiveApplications = 16;
    public const int MaxRetainedApplications = 128;
    private sealed record Entry(string Aid, string Name, string Identity, ApplicationProcessHost Host, string? PreviousAid, string? StartRid)
    {
        internal string? RestartRid { get; set; }
        internal string? Successor { get; set; }
    }
    private readonly ToolRecipeOperations _tools;
    private readonly Func<ToolInvocation, CancellationToken, Task<ApplicationProcessHost>> _launch;
    internal const string OperationEvidenceKey = "application";
    internal const string StopEvidenceKey = OperationEvidenceKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _names = new(StringComparer.Ordinal);
    private bool _disposed;

    public UserApplicationManager(ToolRecipeOperations tools) : this(tools, ApplicationProcessHost.StartAsync) { }

    internal UserApplicationManager(ToolRecipeOperations tools, Func<ToolInvocation, CancellationToken, Task<ApplicationProcessHost>> launch)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(launch);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned application lifetime requires Windows.");
        var owner = LocalIdentity.Current();
        if (owner.Session <= 0 || owner.Sid == "S-1-5-18") throw new AgentOpsException("auth");
        _tools = tools; _launch = launch;
    }

    public Task<IReadOnlyDictionary<string, object?>> StartAsync(string cfg, string name, IReadOnlyList<string> arguments,
        string? cwd = null, string? workspaceRoot = null, string? expectedAid = null, string? rid = null,
        CancellationToken cancellationToken = default) => StartCoreAsync(cfg, name, arguments, cwd, workspaceRoot, expectedAid, rid, null, cancellationToken);

    internal Task<IReadOnlyDictionary<string, object?>> ReplayStartAsync(string knownAid, string cfg, string name,
        IReadOnlyList<string> arguments, string? cwd, string? workspaceRoot, string? expectedAid, string? rid,
        CancellationToken cancellationToken) => StartCoreAsync(cfg, name, arguments, cwd, workspaceRoot, expectedAid, rid, knownAid, cancellationToken);

    private async Task<IReadOnlyDictionary<string, object?>> StartCoreAsync(string cfg, string name, IReadOnlyList<string> arguments,
        string? cwd, string? workspaceRoot, string? expectedAid, string? rid, string? knownAid, CancellationToken cancellationToken)
    {
        ValidateName(name); if (expectedAid is not null) ValidateAid(expectedAid);
        if (rid is { Length: > 128 } || rid?.Any(char.IsControl) == true) throw new AgentOpsException("invalid");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var invocation = _tools.PrepareTool(cfg, arguments, workspaceRoot, cwd);
            if (invocation.Mode != ToolExecutionMode.User) throw new AgentOpsException("auth");
            var identity = Identity(invocation);
            if (knownAid is not null)
            {
                if (!_entries.TryGetValue(knownAid, out var replay)) throw new AgentOpsException("app_owner_lost");
                if (replay.Name != name || replay.Identity != identity || replay.PreviousAid != expectedAid || replay.StartRid != rid)
                    throw new AgentOpsException("precondition");
                return Snapshot(replay);
            }
            Entry? previous = null;
            if (_names.TryGetValue(name, out var current))
            {
                if (expectedAid is not null && _entries.TryGetValue(expectedAid, out var retired) && retired.Name == name &&
                    retired.Successor is not null && !string.IsNullOrEmpty(rid) && retired.RestartRid == rid)
                {
                    if (!_entries.TryGetValue(retired.Successor, out var successor))
                        throw new AgentOpsException("app_owner_lost");
                    if (successor.Identity != identity) throw new AgentOpsException("precondition");
                    return Snapshot(successor);
                }
                if (expectedAid is null)
                {
                    if (current.Identity != identity) throw new AgentOpsException("precondition");
                    return Snapshot(current);
                }
                if (expectedAid != current.Aid || !Terminal(current)) throw new AgentOpsException("precondition");
                previous = current;
            }
            else if (expectedAid is not null) throw new AgentOpsException("precondition");
            // Preserve retained-first admission: do not query live job accounting
            // when the retained-generation bound already proves rejection.
            if (_entries.Count >= MaxRetainedApplications)
                throw new AgentOpsException("app_capacity",
                    failure: FailureEvidence.ApplicationCapacity("retained", _entries.Count, MaxRetainedApplications));
            var active = _entries.Values.Count(entry => !Terminal(entry));
            if (active >= MaxActiveApplications)
                throw new AgentOpsException("app_capacity",
                    failure: FailureEvidence.ApplicationCapacity("active", active, MaxActiveApplications));
            if (_catalogRevision == long.MaxValue) throw new AgentOpsException("precondition");
            using var launch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            ApplicationProcessHost host;
            try { host = await _launch(invocation, launch.Token); }
            catch (OperationCanceledException) when (!launch.IsCancellationRequested) { throw new AgentOpsException("app_launch_failed"); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { throw new AgentOpsException("app_launch_failed", inner: ex); }
            var entry = new Entry(Guid.NewGuid().ToString("N"), name, identity, host, expectedAid, rid);
            // Ownership commits only after the helper acknowledged an exact admitted root.
            // After this point request cancellation cannot release the application's job.
            _entries.Add(entry.Aid, entry); _names[name] = entry; _catalogRevision++;
            if (previous is not null) { previous.RestartRid = rid; previous.Successor = entry.Aid; }
            return Snapshot(entry);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, object?>> StatusAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name); await _gate.WaitAsync(cancellationToken);
        try { ThrowIfDisposed(); return _names.TryGetValue(name, out var entry) ? Snapshot(entry, allowUnknown: true) : throw new AgentOpsException("not_found"); }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyDictionary<string, object?>> InspectProcessesAsync(string name)
    {
        ValidateName(name); await _gate.WaitAsync();
        try { ThrowIfDisposed(); return _names.TryGetValue(name, out var entry) ? entry.Host.InspectProcesses() : throw new AgentOpsException("not_found"); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, object?>> StopAsync(string aid, CancellationToken cancellationToken = default)
    {
        ValidateAid(aid); await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(aid, out var entry)) throw new AgentOpsException("not_found");
            cancellationToken.ThrowIfCancellationRequested();
            // Once issued, exact-job termination completes independently of request cancellation.
            try { await entry.Host.StopAsync(); return Snapshot(entry); }
            catch (AgentOpsException ex) when (ex.Code == "app_stop_incomplete")
            {
                // Capture this exact generation while still holding its manager gate.
                // A failed accounting query must not fabricate a zero process count.
                IReadOnlyDictionary<string, object?> evidence;
                try { evidence = Snapshot(entry); }
                catch (Exception failure) when (failure is AgentOpsException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    evidence = new Dictionary<string, object?>
                    {
                        ["aid"] = entry.Aid, ["name"] = entry.Name, ["state"] = "stopping",
                        ["pid"] = entry.Host.Pid, ["start"] = entry.Host.Start, ["issue"] = "app_stop_incomplete"
                    };
                }
                ex.Data[StopEvidenceKey] = evidence;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> HasLiveApplicationsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            // A failed native observation is not evidence of an exited tree.
            return _entries.Values.Any(entry => !Terminal(entry));
        }
        finally { _gate.Release(); }
    }

    private static bool Terminal(Entry entry) => entry.Host.Snapshot().State is "exited" or "stopped";
    private static IReadOnlyDictionary<string, object?> Snapshot(Entry entry, bool allowUnknown = false)
    {
        (string State, int? Exit, int Processes, string? Issue) state;
        try { state = entry.Host.Snapshot(); }
        catch (AgentOpsException ex) when (ex.Code == "app_observation_incomplete")
        {
            var known = entry.Host.UnobservedSnapshot();
            var evidence = new Dictionary<string, object?>
            {
                ["aid"] = entry.Aid, ["name"] = entry.Name, ["state"] = known.State,
                ["pid"] = entry.Host.Pid, ["start"] = entry.Host.Start, ["issue"] = ex.Code
            };
            if (known.Exit is not null) evidence["exit"] = known.Exit.Value;
            // A committed start is a material effect even when its first live
            // count is unavailable. Status must still disclose the exact aid
            // needed for a safe stop, without inventing a zero count.
            if (allowUnknown) return evidence;
            ex.Data[OperationEvidenceKey] = evidence;
            throw;
        }
        var result = new Dictionary<string, object?>
        {
            ["aid"] = entry.Aid, ["name"] = entry.Name, ["state"] = state.State,
            ["pid"] = entry.Host.Pid, ["start"] = entry.Host.Start, ["processes"] = state.Processes
        };
        if (state.Exit is not null) result["exit"] = state.Exit.Value;
        if (state.Issue is not null) result["issue"] = state.Issue;
        return result;
    }

    private static string Identity(ToolInvocation invocation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            invocation.ToolName, invocation.ExecutablePath, invocation.Arguments, invocation.WorkingDirectory,
            invocation.Mode, invocation.Window, invocation.FileAdmissions,
            Environment = invocation.Environment.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static void ValidateName(string name)
    {
        if (name is null || name.Any(char.IsControl) || !Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new AgentOpsException("invalid");
    }
    internal static void ValidateAid(string aid)
    {
        if (aid is null || aid.Any(char.IsControl) || !Regex.IsMatch(aid, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new AgentOpsException("invalid");
    }
    private void ThrowIfDisposed() { if (_disposed) throw new AgentOpsException("app_owner_lost"); }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            // Request every owned tree's shutdown before waiting, bounding total
            // owner disposal rather than spending the timeout once per entry.
            await Task.WhenAll(_entries.Values.Select(async entry =>
            {
                try { await entry.Host.DisposeAsync(); }
                catch (AgentOpsException ex)
                {
                    ex.Data[OperationEvidenceKey] = new Dictionary<string, object?>
                    {
                        ["aid"] = entry.Aid, ["name"] = entry.Name, ["state"] = "stopping",
                        ["pid"] = entry.Host.Pid, ["start"] = entry.Host.Start, ["issue"] = ex.Code
                    };
                    throw;
                }
            }));
        }
        finally { _gate.Release(); }
    }
}
