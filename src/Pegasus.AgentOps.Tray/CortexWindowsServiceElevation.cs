using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Tray;

internal interface ICortexWindowsServiceExecutor
{
    Task<WindowsServiceOutcome> ExecuteAsync(WindowsServiceInvocation invocation, LocalPeer owner, Action requireHigh);
}

[SupportedOSPlatform("windows")]
internal static class CortexWindowsServiceElevation
{
    internal const string HelperFlag = "--windows-service-control-once";

    internal static void RequireTicket(string ticket) => WindowsServiceElevationContract.ParseTicket(
        Control(WindowsServiceElevationContract.RedeemOperation, ticket));

    private static OperationRequest Control(string operation, string ticket, string? body = null) =>
        new(operation, new Dictionary<string, string>(StringComparer.Ordinal) { ["ticket"] = ticket }, Body: body);

    internal static async Task<int> RunHelperAsync(string ticket, uint parentPid, string parentStart, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); RequireTicket(ticket);
            await using var context = CortexOwnerElevation.OpenHelperContext(parentPid, parentStart);
            return await RunOnceAsync(ticket, context.Owner, LocalIdentity.Current, WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity,
                context.Transport, new WindowsExecutor(), token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 2; }
    }

    internal static async Task<int> RunOnceAsync(string ticket, LocalPeer expectedOwner, Func<LocalPeer> current,
        Func<LocalPeer, CertificateElevationProcessIdentity?> captureHigh, IOperationDispatcher transport,
        ICortexWindowsServiceExecutor executor, CancellationToken token)
    {
        WindowsServiceElevationRequest request;
        WindowsServiceInvocation invocation;
        CertificateElevationProcessIdentity helper;
        try
        {
            token.ThrowIfCancellationRequested(); RequireTicket(ticket);
            helper = CortexOwnerElevation.HighIdentity(current(), expectedOwner, captureHigh) ??
                throw new AgentOpsException("service_elevation_owner_denied");
            // Cancellation cannot retract a delivered capability. Await this
            // original SCM invocation and its one report; do not retry or fall back.
            var redeemed = await transport.DispatchAsync(Control(WindowsServiceElevationContract.RedeemOperation, ticket),
                CancellationToken.None).ConfigureAwait(false);
            if (!redeemed.Ok || redeemed.Data is null || !redeemed.Data.TryGetValue("payload", out var value)) return 3;
            var text = value switch
            {
                WindowsServiceElevationRequest typed => WindowsServiceElevationContract.SerializeRequest(typed),
                JsonElement element => element.GetRawText(), string raw => raw,
                _ => throw new AgentOpsException("service_elevation_payload_invalid")
            };
            request = WindowsServiceElevationContract.ParseRequest(text);
            invocation = WindowsServiceOperationContract.Parse(WindowsServiceElevationContract.Reconstruct(request));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 3; }

        WindowsServiceOutcome? result = null;
        string? error = null;
        var entered = false;
        try
        {
            RequireHigh();
            entered = true;
            result = await executor.ExecuteAsync(invocation, expectedOwner, RequireHigh).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            if (!entered)
                result = new(false, request.Action, null, null, Error: "service_owner_context_required", Phase: "admission");
            else
                // An exception is not a native issued boundary. Preserve missing
                // evidence instead of inventing a false ActionIssued value.
                error = failure is OperationCanceledException ? "windows_service_helper_cancelled" : "windows_service_helper_failed";
        }
        try
        {
            RequireHigh();
            var completed = await transport.DispatchAsync(Control(WindowsServiceElevationContract.CompleteOperation, ticket,
                WindowsServiceElevationContract.SerializeCompletion(request, result, error)), CancellationToken.None).ConfigureAwait(false);
            return completed.Ok ? 0 : 4;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 4; }

        void RequireHigh()
        {
            if (CortexOwnerElevation.HighIdentity(current(), expectedOwner, captureHigh) != helper)
                throw new AgentOpsException("service_elevation_owner_denied");
        }
    }

    private sealed class WindowsExecutor : ICortexWindowsServiceExecutor
    {
        public async Task<WindowsServiceOutcome> ExecuteAsync(WindowsServiceInvocation invocation, LocalPeer owner, Action requireHigh)
        {
            requireHigh();
            var provider = WindowsServiceOperations.ForOwnerAdministrator(owner.Sid, owner.Session);
            // SCM handles are thread-agnostic. Keep the existing provider's
            // original async lifetime; no new thread, host, service or transport.
            return await provider.ExecuteAsync(invocation, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
