using System.Globalization;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed record AgentThreadIdentity(uint Tid, string Start);

// Coverage describes a bounded observation, never an atomic or still-live set.
// Scanned includes foreign processes. Raced/Unavailable refer only to candidates
// whose snapshot owner matched the requested PID.
public sealed record ProcessThreadsResult(
    bool Ok,
    string State,
    string? Error,
    int Pid,
    string ExpectedStart,
    string? ObservedStart,
    IReadOnlyList<AgentThreadIdentity> Threads,
    bool SnapshotExhausted,
    string? Limit,
    int Scanned,
    int Raced,
    int Unavailable);

internal sealed record ProcessThreadRequest(int Pid, string Start, int Max);

internal static class ProcessThreadOperations
{
    internal const int DefaultMaximum = 64;
    internal const int MaximumResults = 256;
    internal const int MaximumSnapshotEntries = 65536;

    // Pure FILETIME decoding is also used by portable fixtures. Zero is an
    // absent generation, not a verified 1601 creation identity.
    internal static string DecodeCreationTime(ulong fileTime, string code)
    {
        try
        {
            if (fileTime == 0) throw new ProcessThreadInventoryException(code);
            var value = checked((long)fileTime);
            return ProcessOperations.FormatStart(DateTime.FromFileTimeUtc(value));
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
        { throw new ProcessThreadInventoryException(code, error); }
    }

    internal static bool IsRequest(OperationRequest request) => request is not null &&
        request.Op == "proc" && request.Arg is not null && request.Arg.GetValueOrDefault("op") == "threads";

    // Inspect the original request before generic projection can remove forbidden
    // authority/context fields. Cwd is ambient only; this operation never opens it.
    internal static ProcessThreadRequest ParseRequest(OperationRequest request)
    {
        if (!IsRequest(request)) throw new AgentOpsException("invalid");
        if (request.Root is not null) throw new AgentOpsException("auth");
        if (request.Body is not null || request.Ws is not null || request.PathRoots is not null ||
            request.Arg.Keys.Any(key => key is not ("op" or "pid" or "start" or "max" or "rid" or "detail" or "output")))
            throw new AgentOpsException("invalid");
        if (request.Arg.TryGetValue("detail", out var detail) && detail is not ("true" or "false") ||
            request.Arg.TryGetValue("output", out var output) && output is not ("agent" or "structured"))
            throw new AgentOpsException("invalid");
        if (request.Rid is not null && (request.Rid.Length is < 1 or > 128 ||
                string.IsNullOrWhiteSpace(request.Rid) || request.Rid.Any(char.IsControl)) ||
            request.Arg.TryGetValue("rid", out var argumentRid) && !StringComparer.Ordinal.Equals(argumentRid, request.Rid))
            throw new AgentOpsException("invalid");
        var pid = Number(request.Arg.GetValueOrDefault("pid"));
        var start = request.Arg.GetValueOrDefault("start");
        var max = request.Arg.TryGetValue("max", out var rawMaximum) ? Number(rawMaximum) : DefaultMaximum;
        Validate(pid, start, max);
        return new(pid, start!, max);
    }

    internal static ProcessThreadsResult List(int pid, string expectedStart, int max = DefaultMaximum,
        CancellationToken cancellationToken = default)
    {
        Validate(pid, expectedStart, max);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Result(false, "unsupported", "unsupported_platform", pid, expectedStart);
        return List(pid, expectedStart, max, new WindowsThreadInventory(), cancellationToken);
    }

    internal static ProcessThreadsResult List(int pid, string expectedStart, int max,
        IProcessThreadInventory inventory, CancellationToken cancellationToken = default)
    {
        Validate(pid, expectedStart, max);
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        IProcessThreadProcessLease? opened;
        try { opened = inventory.OpenProcess(pid); }
        catch (ProcessThreadInventoryException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProcessFailure(error, pid, expectedStart);
        }
        using var process = opened;
        cancellationToken.ThrowIfCancellationRequested();
        if (process is null) return Result(true, "exited", null, pid, expectedStart);

        string? observedStart = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.IsExited()) return Result(true, "exited", null, pid, expectedStart);
            observedStart = ProcessStart(process);
            if (!StringComparer.Ordinal.Equals(expectedStart, observedStart))
                return Result(false, "reused", "pid_reused", pid, expectedStart, observedStart);
            if (process.IsExited()) return Result(true, "exited", null, pid, expectedStart, observedStart);
        }
        catch (ProcessThreadInventoryException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProcessFailure(error, pid, expectedStart, observedStart);
        }

        var threads = new List<AgentThreadIdentity>(max);
        var scanned = 0;
        var raced = 0;
        var unavailable = 0;
        var exhausted = false;
        string? limit = null;
        string? enumerationError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var snapshot = inventory.OpenSnapshot();
            while (scanned < MaximumSnapshotEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!snapshot.MoveNext(out var candidate)) { exhausted = true; break; }
                scanned++;
                if (candidate.OwnerPid != (uint)pid) continue;
                if (candidate.Tid == 0) { unavailable++; continue; }
                cancellationToken.ThrowIfCancellationRequested();
                AgentThreadIdentity identity;
                try
                {
                    using var thread = inventory.OpenThread(candidate.Tid);
                    if (thread is null) { raced++; continue; }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (thread.ReadOwnerPid() != (uint)pid) { raced++; continue; }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (thread.IsExited()) { raced++; continue; }
                    cancellationToken.ThrowIfCancellationRequested();
                    var start = thread.ReadStart();
                    if (start is null || start.Length > 64 || !ProcessOperations.IsCanonicalStartIdentity(start))
                    { unavailable++; continue; }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (thread.IsExited()) { raced++; continue; }
                    identity = new(candidate.Tid, start);
                }
                catch (ProcessThreadInventoryException) { unavailable++; continue; }
                // The extra verified row proves the result limit, rather than
                // claiming truncation merely because the requested count was met.
                if (threads.Count == max) { limit = "results"; break; }
                threads.Add(identity);
            }
            if (!exhausted && limit is null) limit = "scan";
        }
        catch (ProcessThreadInventoryException) { enumerationError = "snapshot_failed"; }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Keep the original handle until this check. Rows from a process that
            // exited during enumeration must not describe a replacement PID.
            if (process.IsExited()) return Result(true, "exited", null, pid, expectedStart, observedStart,
                scanned: scanned, raced: raced, unavailable: unavailable);
            var finalStart = ProcessStart(process);
            if (!StringComparer.Ordinal.Equals(expectedStart, finalStart))
                return Result(false, "reused", "pid_reused", pid, expectedStart, finalStart,
                    scanned: scanned, raced: raced, unavailable: unavailable);
            if (process.IsExited()) return Result(true, "exited", null, pid, expectedStart, observedStart,
                scanned: scanned, raced: raced, unavailable: unavailable);
        }
        catch (ProcessThreadInventoryException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProcessFailure(error, pid, expectedStart, observedStart, scanned, raced, unavailable);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var partial = enumerationError is not null || !exhausted || raced != 0 || unavailable != 0;
        return Result(enumerationError is null, partial ? "partial" : "running", enumerationError, pid, expectedStart,
            observedStart, Array.AsReadOnly(threads.ToArray()), exhausted, limit, scanned, raced, unavailable);
    }

    private static string ProcessStart(IProcessThreadProcessLease process)
    {
        var start = process.ReadStart();
        if (start is null || start.Length > 64 || !ProcessOperations.IsCanonicalStartIdentity(start))
            throw new ProcessThreadInventoryException("process_query_failed");
        return start;
    }

    private static int Number(string? raw)
    {
        if (raw is null || raw.Length is < 1 or > 10 || raw.Any(character => character is < '0' or > '9') ||
            !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new AgentOpsException("invalid");
        return value;
    }

    private static void Validate(int pid, string? start, int max)
    {
        if (pid <= 0 || start is null || start.Length > 64 || !ProcessOperations.IsCanonicalStartIdentity(start) ||
            max is < 1 or > MaximumResults) throw new AgentOpsException("invalid");
    }

    private static ProcessThreadsResult ProcessFailure(ProcessThreadInventoryException error, int pid,
        string expectedStart, string? observedStart = null, int scanned = 0, int raced = 0, int unavailable = 0) =>
        Result(false, error.Code == "access" ? "locked" : "unknown", error.Code == "access" ? "access" : "process_query_failed",
            pid, expectedStart, observedStart, scanned: scanned, raced: raced, unavailable: unavailable);

    private static ProcessThreadsResult Result(bool ok, string state, string? error, int pid, string expectedStart,
        string? observedStart = null, IReadOnlyList<AgentThreadIdentity>? threads = null, bool exhausted = false,
        string? limit = null, int scanned = 0, int raced = 0, int unavailable = 0) =>
        new(ok, state, error, pid, expectedStart, observedStart, threads ?? Array.Empty<AgentThreadIdentity>(),
            exhausted, limit, scanned, raced, unavailable);
}

// These leases expose observations only. The Windows implementation fixes query
// rights internally; callers cannot submit handles, access masks or authority.
internal interface IProcessThreadInventory
{
    IProcessThreadProcessLease? OpenProcess(int pid);
    IProcessThreadSnapshot OpenSnapshot();
    IProcessThreadLease? OpenThread(uint tid);
}

internal interface IProcessThreadProcessLease : IDisposable
{
    bool IsExited();
    string ReadStart();
}

internal interface IProcessThreadSnapshot : IDisposable
{
    bool MoveNext(out ProcessThreadSnapshotEntry entry);
}

internal interface IProcessThreadLease : IDisposable
{
    uint ReadOwnerPid();
    bool IsExited();
    string ReadStart();
}

internal readonly record struct ProcessThreadSnapshotEntry(uint Tid, uint OwnerPid);

internal sealed class ProcessThreadInventoryException(string code, Exception? inner = null) : Exception(code, inner)
{
    internal string Code { get; } = code;
}
