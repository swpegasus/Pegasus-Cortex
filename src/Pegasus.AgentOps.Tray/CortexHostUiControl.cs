using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

/// <summary>Fixed same-image UI activation on the existing user pipe, never runtime startup.</summary>
internal static class CortexHostUiControl
{
    internal const string Operation = "cortex_host_ui_show";
    internal static string MutexName(int session) => $"Local\\Pegasus.Cortex.Host.{session}";

    internal static bool HasInstance(int session)
    {
        if (!Mutex.TryOpenExisting(MutexName(session), out var existing)) return false;
        existing.Dispose();
        return true;
    }

    internal static async Task<bool> ShowExistingAsync(LocalPeer owner, CancellationToken token)
    {
        if (owner.ApplicationPath is null) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var pipe = await CortexMcpPipe.ConnectAsync(CortexProductIdentity.UserExecutionPipe(owner.Session),
                owner, owner.ApplicationPath, deadline.Token);
            var rid = Guid.NewGuid().ToString("N");
            await BinaryProtocol.WriteAsync(pipe, new OperationRequest(Operation, new Dictionary<string, string>(), rid), deadline.Token);
            var reply = await BinaryProtocol.ReadAsync<OperationReceipt>(pipe, deadline.Token);
            return reply.Ok && reply.Op == Operation && reply.Rid == rid && reply.State == "done";
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or AgentOpsException or UnauthorizedAccessException)
        { return false; }
    }

    internal static async Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer owner, LocalPeer peer,
        Func<CancellationToken, Task<bool>> show, CancellationToken token)
    {
        var rid = request.Rid ?? Guid.NewGuid().ToString("N");
        OperationReceipt Reply(bool ok, string? error = null) =>
            new(ok, Guid.NewGuid().ToString("N"), rid, ok ? "done" : "failed", Operation, error);
        if (!peer.SameIdentity(owner) || owner.ApplicationPath is null || peer.ApplicationPath is null ||
            !Path.GetFullPath(peer.ApplicationPath).Equals(Path.GetFullPath(owner.ApplicationPath), StringComparison.OrdinalIgnoreCase))
            return Reply(false, "auth");
        if (request.Op != Operation || request.Arg is null || request.Arg.Count != 0 || request.Root is not null || request.Body is not null ||
            request.Ws is not null || request.Cwd is not null || request.PathRoots is not null || !Guid.TryParseExact(request.Rid, "N", out _))
            return Reply(false, "invalid");
        return await show(token) ? Reply(true) : Reply(false, "ui_unavailable");
    }
}
