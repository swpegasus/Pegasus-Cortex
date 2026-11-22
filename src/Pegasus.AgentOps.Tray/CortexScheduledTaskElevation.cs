using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Tray;

internal interface ICortexScheduledTaskExecutor
{
    Task<WindowsTaskOutcome> ExecuteAsync(WindowsTaskInvocation invocation, LocalPeer owner, Action requireHigh);
}

[SupportedOSPlatform("windows")]
internal static class CortexScheduledTaskElevation
{
    internal const string HelperFlag = "--scheduled-task-control-once";

    internal static void RequireTicket(string ticket) => ScheduledTaskElevationContract.ParseTicket(
        Control(ScheduledTaskElevationContract.RedeemOperation, ticket));

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
        ICortexScheduledTaskExecutor executor, CancellationToken token)
    {
        ScheduledTaskElevationRequest request;
        WindowsTaskInvocation invocation;
        CertificateElevationProcessIdentity helper;
        try
        {
            token.ThrowIfCancellationRequested(); RequireTicket(ticket);
            helper = CortexOwnerElevation.HighIdentity(current(), expectedOwner, captureHigh) ??
                throw new AgentOpsException("scheduled_task_elevation_owner_denied");
            // After redemption starts, caller cancellation is observation-only.
            // Await the exact original invocation and report once, without replay.
            var redeemed = await transport.DispatchAsync(Control(ScheduledTaskElevationContract.RedeemOperation, ticket),
                CancellationToken.None).ConfigureAwait(false);
            if (!redeemed.Ok || redeemed.Data is null || !redeemed.Data.TryGetValue("payload", out var value)) return 3;
            var text = value switch
            {
                ScheduledTaskElevationRequest typed => ScheduledTaskElevationContract.SerializeRequest(typed),
                JsonElement element => element.GetRawText(), string raw => raw,
                _ => throw new AgentOpsException("scheduled_task_elevation_payload_invalid")
            };
            request = ScheduledTaskElevationContract.ParseRequest(text);
            invocation = WindowsTaskOperationContract.Parse(ScheduledTaskElevationContract.Reconstruct(request));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 3; }

        WindowsTaskOutcome? result = null;
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
                result = new(false, request.Action, Phase: "admission", Error: "owner_denied");
            else
                // A callback exception supplies no native issued boundary. Leave
                // result absent so the parent conservatively retains uncertainty.
                error = "scheduled_task_helper_failed";
        }
        try
        {
            RequireHigh();
            var completed = await transport.DispatchAsync(Control(ScheduledTaskElevationContract.CompleteOperation, ticket,
                ScheduledTaskElevationContract.SerializeCompletion(request, result, error)), CancellationToken.None).ConfigureAwait(false);
            return completed.Ok ? 0 : 4;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return 4; }

        void RequireHigh()
        {
            if (CortexOwnerElevation.HighIdentity(current(), expectedOwner, captureHigh) != helper)
                throw new AgentOpsException("scheduled_task_elevation_owner_denied");
        }
    }

    private sealed class WindowsExecutor : ICortexScheduledTaskExecutor
    {
        public Task<WindowsTaskOutcome> ExecuteAsync(WindowsTaskInvocation invocation, LocalPeer owner, Action requireHigh) =>
            Task.Run(() =>
            {
                // COM references are created, used and released inside this one
                // original MTA worker. It owns no ordinary host or tool resolver.
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
                    return new WindowsTaskOutcome(false, invocation.Action, Phase: "admission", Error: "task_worker_context_required");
                requireHigh();
                var provider = WindowsTaskOperations.ForOwnerAdministrator(owner.Sid, owner.Session);
                return provider.Execute(invocation, CancellationToken.None);
            }, CancellationToken.None);
    }
}
