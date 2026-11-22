using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexHostExitClientResult(string State, string? Error = null,
    uint? ProcessId = null, string? CreationFileTime = null, bool HostStopped = false)
{
    internal int ExitCode => State switch { "stopped" => 0, "refused" => 3, _ => 4 };
}

internal interface ICortexHostExitConnection : IAsyncDisposable
{
    uint ProcessId { get; }
    long CreationFileTime { get; }
    Task<OperationReceipt> SendAsync(OperationRequest request, CancellationToken token);
    Task<bool> WaitForStopAsync(TimeSpan timeout, CancellationToken token);
}

/// <summary>One fixed, attach-only Exit on the existing authenticated user pipe.</summary>
internal sealed class CortexHostExitControl(LocalPeer owner, CortexRuntimeExit exit)
{
    internal const string Operation = "cortex_host_exit";
    private OperationReceipt? _committedReply;

    internal async Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer,
        CancellationToken token)
    {
        OperationReceipt Reply(bool ok, string? error = null) => new(ok, Guid.NewGuid().ToString("N"),
            request.Rid ?? Guid.NewGuid().ToString("N"), ok ? "done" : "failed", Operation, error);
        if (!IsOrdinaryPeer(owner) || !IsOrdinaryPeer(peer) || !peer.SameIdentity(owner) ||
            owner.ApplicationPath is null || peer.ApplicationPath is null ||
            !Path.GetFullPath(peer.ApplicationPath).Equals(Path.GetFullPath(owner.ApplicationPath), StringComparison.OrdinalIgnoreCase))
            return Reply(false, "auth");
        if (request.Op != Operation || request.Arg is null || request.Arg.Count != 0 || request.Root is not null ||
            request.Body is not null || request.Ws is not null || request.Cwd is not null || request.PathRoots is not null ||
            request.Quiet || request.Detail || !Guid.TryParseExact(request.Rid, "N", out _))
            return Reply(false, "invalid");
        var result = await exit.PrepareAsync(token).ConfigureAwait(false);
        var receipt = Reply(result.Accepted, result.Issue);
        if (result.Accepted) Interlocked.Exchange(ref _committedReply, receipt);
        return receipt;
    }

    // This is not the service's successful-write COMMIT notification. A user
    // Exit already committed before writing its acknowledgement; a lost reply
    // must neither reopen admission nor leave an idle Host closed indefinitely.
    internal void ReplyAttemptSettled(OperationReceipt receipt)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _committedReply, null, receipt), receipt))
            _ = exit.SignalStop();
    }

    internal static bool IsOrdinaryPeer(LocalPeer peer) => peer.Session > 0 && !peer.Elevated &&
        peer.Sid is not ("S-1-5-18" or "S-1-5-19" or "S-1-5-20");

    internal static Task<CortexHostExitClientResult> RequestCurrentAsync(CancellationToken token) =>
        RequestAsync(ConnectCurrentAsync, token);

    internal static async Task<CortexHostExitClientResult> RequestAsync(
        Func<CancellationToken, Task<ICortexHostExitConnection?>> connect, CancellationToken token,
        TimeSpan? stopTimeout = null)
    {
        ICortexHostExitConnection? connection = null;
        var submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            connection = await connect(token).ConfigureAwait(false);
            // Absence concerns the ordinary Host only; this does not certify
            // that an independently running optional service is also stopped.
            if (connection is null) return new("stopped", HostStopped: true);
            var request = new OperationRequest(Operation, new Dictionary<string, string>(), Guid.NewGuid().ToString("N"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            submitted = true;
            OperationReceipt reply;
            try { reply = await connection.SendAsync(request, deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (IsExpectedFailure(error))
            {
                // Observe only the retained original generation. Even an exit
                // after a lost ACK does not prove which shutdown policy ran.
                var stopped = await connection.WaitForStopAsync(stopTimeout ?? TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                return Result("uncertain", "exit_acknowledgement_unavailable", stopped);
            }
            if (reply.Op != Operation || reply.Rid != request.Rid || reply.Partial ||
                reply.State != (reply.Ok ? "done" : "failed") || (reply.Ok && reply.Err is not null))
                return Result("uncertain", "exit_reply_invalid");
            if (!reply.Ok) return Result("refused", reply.Err ?? "exit_refused");
            return await connection.WaitForStopAsync(stopTimeout ?? TimeSpan.FromSeconds(15), token).ConfigureAwait(false)
                ? Result("stopped", null, true) : Result("uncertain", "host_stop_unconfirmed");
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return Result(submitted ? "uncertain" : "refused", error switch
            {
                AgentOpsException agent => agent.Code,
                OperationCanceledException => submitted ? "exit_outcome_unknown" : "exit_canceled",
                _ => submitted ? "exit_outcome_unknown" : "host_unavailable"
            });
        }
        finally { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }

        CortexHostExitClientResult Result(string state, string? error, bool stopped = false) =>
            new(state, error, connection?.ProcessId,
                connection?.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture), stopped);
    }

    internal static bool IsExpectedFailure(Exception error) => error is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or
        AgentOpsException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
        System.Security.SecurityException or NotSupportedException;

    internal static async Task<ICortexHostExitConnection?> ConnectCurrentAsync(CancellationToken token)
    {
        var owner = LocalIdentity.Current();
        if (!IsOrdinaryPeer(owner)) throw new AgentOpsException("auth");
        // Validate the installed image/registration without initializing data,
        // tool discovery, UI, a database, or a second runtime owner.
        var layout = CortexProductLayout.ResolveExecution();
        _ = CortexDataLocation.ResolveMachineDataRoot(layout.InstallRoot);
        var expectedHost = Path.Combine(layout.InstallRoot,
            CortexProductCatalogV1.StableRolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
        if (owner.ApplicationPath is null || !Path.GetFullPath(owner.ApplicationPath).Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new AgentOpsException("auth");
        PathNormalizer.RejectAbsoluteReparseComponents(expectedHost);
        if (!CortexHostUiControl.HasInstance(owner.Session)) return null;
        var pipe = new NamedPipeClientStream(".", CortexProductIdentity.UserExecutionPipe(owner.Session),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return WindowsExitConnection.Capture(pipe, owner, expectedHost);
        }
        catch { pipe.Dispose(); throw; }
    }

    // All identity queries and exit observation use this one retained process
    // HANDLE. No PID/name lookup or replacement generation is used after capture.
    internal sealed class WindowsExitConnection(NamedPipeClientStream pipe, SafeProcessHandle process,
        uint pid, long creation) : ICortexHostExitConnection
    {
        public uint ProcessId => pid;
        public long CreationFileTime => creation;
        internal static WindowsExitConnection Capture(NamedPipeClientStream pipe, LocalPeer owner, string expectedHost)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ||
                !GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) || session != owner.Session)
                throw new AgentOpsException("auth");
            var process = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            try
            {
                if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var accessToken))
                    throw new AgentOpsException("auth");
                using (accessToken)
                using (var identity = new WindowsIdentity(accessToken.DangerousGetHandle()))
                {
                    // Role membership may duplicate a remote primary token.
                    // TokenElevation is a query-only read on this same handle.
                    if (!StringComparer.Ordinal.Equals(identity.User?.Value, owner.Sid) ||
                        !GetTokenInformation(accessToken, 20, out var elevation, sizeof(uint), out var elevationBytes) ||
                        elevationBytes != sizeof(uint) || elevation != 0 ||
                        !GetTokenInformation(accessToken, 12, out var tokenSession, sizeof(uint), out _) || tokenSession != owner.Session)
                        throw new AgentOpsException("auth");
                }
                var size = 32768;
                var image = new StringBuilder(size);
                if (!QueryFullProcessImageName(process, 0, image, ref size) ||
                    !Path.GetFullPath(image.ToString()).Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
                    !GetProcessTimes(process, out var creation, out _, out _, out _) || WaitForSingleObject(process, 0) != 258)
                    throw new AgentOpsException("auth");
                return new(pipe, process, pid, creation);
            }
            catch { process.Dispose(); throw; }
        }

        public async Task<OperationReceipt> SendAsync(OperationRequest request, CancellationToken token)
        {
            await BinaryProtocol.WriteAsync(pipe, request, token).ConfigureAwait(false);
            return await BinaryProtocol.ReadAsync<OperationReceipt>(pipe, token).ConfigureAwait(false);
        }

        public async Task<bool> WaitForStopAsync(TimeSpan timeout, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = WaitForSingleObject(process, 0);
                if (state == 0) return true;
                if (state != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (elapsed.Elapsed >= timeout) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() { pipe.Dispose(); process.Dispose(); return ValueTask.CompletedTask; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out uint value, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
