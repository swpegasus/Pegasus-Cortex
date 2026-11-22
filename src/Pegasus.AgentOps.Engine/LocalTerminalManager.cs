using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Owner-host lifetime only. No transcript, command text, input or output
/// enters SQLite, continuity, disk logs or the ordinary job recovery lane.</summary>
internal sealed class LocalTerminalManager(ToolRecipeOperations tools) : IAsyncDisposable
{
    internal const int MaximumActive = 8, MaximumRetained = 32, MaximumCommands = 64, MaximumAttempts = 256;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private bool _disposed;

    // No profile or persistent PSReadLine history is loaded. Basic console input
    // remains attached to a real ConPTY; callers may explicitly use interactive input.
    private static readonly string[] ShellArguments = ["-NoLogo", "-NoProfile", "-NoExit", "-Command",
        "Remove-Module PSReadLine -ErrorAction SilentlyContinue; function global:PSConsoleHostReadLine { [Console]::ReadLine() }"];

    private sealed class Entry(string sid, string name, string identity, ConPtyTerminalProcess process, TerminalOutputBuffer output, IDisposable lifetime)
    {
        internal readonly string Sid = sid, Name = name, Identity = identity;
        internal readonly ConPtyTerminalProcess Process = process;
        internal readonly TerminalOutputBuffer Output = output;
        internal readonly Dictionary<string, SavedCommand> Commands = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Attempt> Attempts = new(StringComparer.Ordinal);
        internal readonly IDisposable Lifetime = lifetime;
        internal bool Attached = true, Closed;
        internal string? LastAttempt, LastSavedCommand;
        internal Task? Pending;
    }
    private sealed class SavedCommand(string id, string text)
    {
        internal readonly string Id = id, Text = text;
        internal string? LastInput;
    }
    private sealed class Attempt(string id, string rid, string fingerprint, string? cid, string? reRunOf)
    {
        internal readonly string Id = id, Rid = rid, Fingerprint = fingerprint;
        internal readonly string? CommandId = cid, ReRunOf = reRunOf;
        internal readonly string SentAtUtc = DateTimeOffset.UtcNow.ToString("O");
        internal volatile string Delivery = "unknown";
    }

    internal async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(OperationRequest request,
        Func<IDisposable> admitLifetime, Func<IDisposable> admitInput, CancellationToken cancellationToken)
    {
        if (request.Root is not null || !TerminalContract.Validate(request.Arg, request.Body))
            throw new AgentOpsException("invalid");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local terminals require Windows.");
        var owner = LocalIdentity.Current();
        using (var identity = WindowsIdentity.GetCurrent())
            if (owner.Session <= 0 || owner.Sid == "S-1-5-18" ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new AgentOpsException("terminal_user_context_required");
        var op = request.Arg["op"];
        Task? wait = null;
        Attempt? submitted = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new AgentOpsException("terminal_owner_lost");
            if (op == "list") return Inventory(request.Detail);
            if (op == "connect")
            {
                var name = request.Arg["name"];
                var width = Integer(request, "width", 100); var height = Integer(request, "height", 30);
                var invocation = tools.PrepareTool("pwsh", ShellArguments, workspaceRoot: request.PathRoots?.GetValueOrDefault("cwd"), cwd: request.Cwd);
                if (invocation.Mode != ToolExecutionMode.User) throw new AgentOpsException("auth");
                var identity = invocation.WorkingDirectory + "\n" + width + "\n" + height + "\n" + invocation.ExecutablePath;
                if (_names.TryGetValue(name, out var existing) && _sessions.TryGetValue(existing, out var previous) &&
                    !previous.Closed && !previous.Process.Lifetime.IsCompletedSuccessfully)
                {
                    if (previous.Identity != identity) throw new AgentOpsException("terminal_connection_conflict");
                    previous.Attached = true;
                    return new Dictionary<string, object?> { ["sid"] = previous.Sid, ["state"] = State(previous) };
                }
                if (_sessions.Count >= MaximumRetained ||
                    _sessions.Values.Count(e => !e.Process.Lifetime.IsCompletedSuccessfully) >= MaximumActive)
                    throw new AgentOpsException("terminal_capacity");
                cancellationToken.ThrowIfCancellationRequested();
                var lifetime = admitLifetime();
                var output = new TerminalOutputBuffer();
                ConPtyTerminalProcess process;
                try
                {
                    // Once launch admission succeeds, client cancellation cannot
                    // abandon the process. The owner retains it even after a lost reply.
                    process = await Task.Run(() => ConPtyTerminalProcess.StartProcess(invocation, width, height, output.Append, output.End)).ConfigureAwait(false);
                }
                catch { lifetime.Dispose(); throw; }
                var sid = _epoch + ":" + Guid.NewGuid().ToString("N");
                var entry = new Entry(sid, name, identity, process, output, lifetime);
                _sessions.Add(sid, entry); _names[name] = sid;
                _ = RetireOwnershipAsync(entry);
                return new Dictionary<string, object?> { ["sid"] = sid, ["state"] = "connected" };
            }
            var session = Find(request.Arg["sid"]);
            if (op == "status")
            {
                var result = new Dictionary<string, object?> { ["state"] = State(session) };
                if (request.Detail)
                {
                    result["pid"] = session.Process.Pid; result["start"] = session.Process.Start;
                    result["provider"] = "windows-conpty"; result["screenSupported"] = false;
                    result["survivesDisconnect"] = true; result["survivesHostRestart"] = false;
                }
                if (session.Process.OutputIssue is { } issue) result["issue"] = issue;
                return result;
            }
            if (op is "disconnect" or "reconnect")
            {
                if (session.Closed || session.Process.Lifetime.IsCompletedSuccessfully) return new Dictionary<string, object?> { ["state"] = State(session) };
                session.Attached = op == "reconnect";
                return new Dictionary<string, object?> { ["state"] = State(session) };
            }
            if (op == "read")
            {
                var offset = request.Arg.TryGetValue("cursor", out var raw) ? long.Parse(raw, CultureInfo.InvariantCulture) : (long?)null;
                return session.Output.Read(offset, Integer(request, "max", 4096), request.Arg.GetValueOrDefault("fmt") == "base64");
            }
            if (op == "screen") throw new AgentOpsException("terminal_screen_unsupported");
            if (op == "recall") return Recall(session);
            if (op == "input_status") return InputStatus(session, request.Arg.GetValueOrDefault("iid"), request.Arg.GetValueOrDefault("original_rid"));
            if (op == "release")
            {
                if (!session.Process.Lifetime.IsCompletedSuccessfully) throw new AgentOpsException("terminal_not_releasable");
                _sessions.Remove(session.Sid);
                foreach (var key in _names.Where(p => p.Value == session.Sid).Select(p => p.Key).ToArray()) _names.Remove(key);
                return new Dictionary<string, object?> { ["state"] = "released" };
            }
            if (op == "close")
            {
                session.Closed = true;
                try { await session.Process.CloseAsync().ConfigureAwait(false); }
                catch (TimeoutException) { throw new AgentOpsException("terminal_close_incomplete"); }
                session.Lifetime.Dispose();
                return new Dictionary<string, object?> { ["state"] = "closed" };
            }
            var rid = request.Rid ?? throw new AgentOpsException("invalid");
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                op + "\n" + request.Arg.GetValueOrDefault("cid") + "\n" + request.Body)));
            // Exact known delivery can be reconciled after detach/exit without
            // reopening the session, even though NEW input is refused there.
            if (session.Attempts.TryGetValue(rid, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new AgentOpsException("terminal_input_conflict");
                return Submitted(prior);
            }
            if (session.Closed || session.Process.IsExited) throw new AgentOpsException("terminal_closed");
            if (!session.Attached) throw new AgentOpsException("terminal_disconnected");
            if (op == "resize")
            {
                using var effect = admitInput();
                session.Process.Resize(Integer(request, "width", 100), Integer(request, "height", 30));
                return new Dictionary<string, object?> { ["state"] = State(session) };
            }

            if (session.Pending is { IsCompleted: false }) throw new AgentOpsException("terminal_input_busy");
            if (session.Attempts.Count >= MaximumAttempts) throw new AgentOpsException("terminal_history_full");
            SavedCommand? command = null;
            string text;
            string? reRunOf = null;
            if (op == "send")
            {
                if (session.Commands.Count >= MaximumCommands) throw new AgentOpsException("terminal_history_full");
                command = new(Guid.NewGuid().ToString("N"), request.Body!);
                text = command.Text + "\r";
            }
            else if (op == "repeat")
            {
                if (!session.Commands.TryGetValue(request.Arg["cid"], out command)) throw new AgentOpsException("terminal_command_missing");
                reRunOf = command.LastInput;
                text = command.Text + "\r";
            }
            else text = op == "interrupt" ? "\u0003" : request.Body!;
            var effectLease = admitInput();
            submitted = new(Guid.NewGuid().ToString("N"), rid, fingerprint, command?.Id, reRunOf);
            if (command is not null) { session.Commands[command.Id] = command; command.LastInput = submitted.Id; session.LastSavedCommand = command.Id; }
            session.Attempts.Add(rid, submitted); session.LastAttempt = rid;
            var bytes = Encoding.UTF8.GetBytes(text);
            wait = session.Pending = Task.Run(() => DeliverAsync(session, submitted, bytes, effectLease));
        }
        finally { _gate.Release(); }
        // Observation cancellation never cancels delivery. Its original RID/input
        // remains inspectable; an unknown write must never be implicitly retried.
        try { await wait!.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        return Submitted(submitted!);
    }

    // Called only under the existing owner/session gate. Entries, including old
    // generations whose name has been reused, remain discoverable until release.
    // No output, input history, cwd or environment is observed by this read.
    private IReadOnlyDictionary<string, object?> Inventory(bool detail)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(_sessions.Count);
        foreach (var entry in _sessions.Values.OrderBy(value => value.Name, StringComparer.Ordinal)
                     .ThenBy(value => value.Sid, StringComparer.Ordinal))
        {
            var row = new Dictionary<string, object?>
                { ["sid"] = entry.Sid, ["name"] = entry.Name, ["state"] = State(entry) };
            if (detail) { row["pid"] = entry.Process.Pid; row["start"] = entry.Process.Start; }
            rows.Add(row);
        }
        return new Dictionary<string, object?> { ["sessions"] = rows.AsReadOnly() };
    }

    private Entry Find(string sid)
    {
        if (!sid.StartsWith(_epoch + ":", StringComparison.Ordinal)) throw new AgentOpsException("terminal_owner_lost");
        return _sessions.TryGetValue(sid, out var entry) ? entry : throw new AgentOpsException("terminal_missing");
    }
    private static async Task DeliverAsync(Entry entry, Attempt attempt, byte[] bytes, IDisposable lease)
    {
        try { await entry.Process.WriteAsync(bytes).ConfigureAwait(false); attempt.Delivery = "delivered"; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { attempt.Delivery = "unknown"; }
        finally { CryptographicOperations.ZeroMemory(bytes); lease.Dispose(); }
    }
    private static IReadOnlyDictionary<string, object?> Submitted(Attempt attempt)
    {
        var result = new Dictionary<string, object?> { ["input"] = attempt.Id, ["delivery"] = attempt.Delivery };
        if (attempt.CommandId is not null) result["cid"] = attempt.CommandId;
        return result;
    }
    private static IReadOnlyDictionary<string, object?> Recall(Entry entry)
    {
        if (entry.LastSavedCommand is null) return new Dictionary<string, object?> { ["state"] = "empty" };
        var command = entry.Commands[entry.LastSavedCommand];
        var attempt = entry.Attempts.Values.Single(a => a.Id == command.LastInput);
        var result = new Dictionary<string, object?>(Submitted(attempt))
        { ["text"] = command.Text, ["sentAtUtc"] = attempt.SentAtUtc, ["completion"] = "unknown" };
        if (attempt.ReRunOf is not null) result["reRunOf"] = attempt.ReRunOf;
        return result;
    }
    private static IReadOnlyDictionary<string, object?> InputStatus(Entry entry, string? id, string? originalRid)
    {
        var attempt = originalRid is not null ? entry.Attempts.GetValueOrDefault(originalRid) : id is null
            ? entry.LastAttempt is { } rid ? entry.Attempts[rid] : null
            : entry.Attempts.Values.SingleOrDefault(a => a.Id == id);
        if (attempt is null) return new Dictionary<string, object?> { ["state"] = id is null && originalRid is null ? "empty" : "missing" };
        var result = new Dictionary<string, object?>(Submitted(attempt))
        { ["sentAtUtc"] = attempt.SentAtUtc, ["completion"] = "unknown" };
        if (attempt.ReRunOf is not null) result["reRunOf"] = attempt.ReRunOf;
        return result;
    }
    private static string State(Entry entry) => entry.Process.Lifetime.IsCompletedSuccessfully
        ? entry.Closed ? "closed" : "exited"
        : entry.Process.IsExited ? "closing" : entry.Closed ? "closing" : entry.Attached ? "connected" : "disconnected";
    private static int Integer(OperationRequest request, string key, int fallback) =>
        request.Arg.TryGetValue(key, out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
    private static async Task RetireOwnershipAsync(Entry entry)
    { try { await entry.Process.Lifetime.ConfigureAwait(false); entry.Lifetime.Dispose(); } catch { /* Preserve ownership after unconfirmed native cleanup. */ } }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; entries = _sessions.Values.ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(entries.Select(async entry =>
        {
            try { await entry.Process.CloseAsync().ConfigureAwait(false); }
            catch (TimeoutException) { /* Ownership remains visible until native cleanup completes. */ }
        })).ConfigureAwait(false);
    }
}
