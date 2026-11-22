using System.Text;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Private service-to-owner relay. This is not a PAO operation or a durable execution lane.</summary>
internal static class CortexSharedMcpRelay
{
    internal const string Operation = "_cortex_shared_mcp";

    internal static void RequireRequest(OperationRequest request)
    {
        if (request.Op != Operation || request.Root is not null || request.System || request.ConnectionAuthority is not null ||
            request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Quiet || request.Detail ||
            request.Arg.Count != 1 || request.Arg.GetValueOrDefault("kind") is not ("initialize" or "invoke") ||
            request.Rid is not { Length: 32 } rid || !rid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            request.Body is null || Encoding.UTF8.GetByteCount(request.Body) > OperationRequest.MaxBodyBytes)
            throw new AgentOpsException("invalid");
    }
}

public sealed partial class AuthenticatedServiceBroker
{
    // Called only by the service's own HTTP dispatcher after network/TLS admission.
    // HTTP labels cannot select a SID/session/pipe or create a System execution
    // marker. All semantic and PAO work stays inside the ordinary owner Host.
    internal async Task<OperationReceipt> RelaySharedMcpAsync(OperationRequest request, CancellationToken token)
    {
        CortexSharedMcpRelay.RequireRequest(request);
        token.ThrowIfCancellationRequested();
        if (!_serviceExitAdmission.TryEnter(out var admission)) return Fail(request, "service_exiting");
        using var lease = admission;
        if (MaintenanceClosed()) return Fail(request, "maintenance_busy");

        var route = _tray;
        if (route?.Identity is not { } identity || _serviceExitHost is null ||
            identity.Sid != _ownerSid || identity.Session <= 0 || route.Peer.Elevated ||
            route.Peer.ProcessId != identity.Pid || !route.Peer.SameIdentity(new(identity.Sid, identity.Session)) ||
            !StringComparer.OrdinalIgnoreCase.Equals(identity.ApplicationPath, _serviceExitHost))
            return Fail(request, "user_host_unavailable");

        // Pin this call to the registered image AND original process generation.
        // A later registration may serve a fresh request; a lost old request is
        // never replayed or redirected to its successor.
        OperationReceipt response;
        if (_trayDispatch is { } fixtureDispatch)
            response = await fixtureDispatch(route.Peer, route.PipeName, request, token).ConfigureAwait(false);
        else
        {
            await using var dispatcher = new NamedPipeDispatcher(route.PipeName, route.Peer, identity);
            response = await dispatcher.DispatchAsync(request, token).ConfigureAwait(false);
        }
        if (response.Op != CortexSharedMcpRelay.Operation || response.Rid != request.Rid)
            return Fail(request, "relay_identity_failed") with { Partial = true };
        return response;
    }
}
