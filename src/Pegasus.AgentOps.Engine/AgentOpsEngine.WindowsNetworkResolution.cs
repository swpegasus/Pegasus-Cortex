using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private WindowsNetworkResolutionOperations? _windowsNetworkResolution;
    private int _networkResolutionCallbacks;
    private TaskCompletionSource? _networkResolutionIdle;

    internal void BindWindowsNetworkResolution(WindowsNetworkResolutionOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_directOnly || _userHostAdmission is null || !operations.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _windowsNetworkResolution, operations, null) is not null)
            throw new AgentOpsException("auth");
    }

    private async Task<OperationReceipt> ExecuteEphemeralWindowsNetworkResolutionAsync(
        OperationRequest request, string jid, string rid, CancellationToken token)
    {
        lock (_admissionGate)
        {
            if (_stopping) return HostStopping(request, replayDurable: false);
            if (_networkResolutionCallbacks++ == 0)
                _networkResolutionIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            return await ExecuteReceiptAsync(request, jid, rid, 0, token, persist: false).ConfigureAwait(false);
        }
        finally
        {
            lock (_admissionGate)
                if (--_networkResolutionCallbacks == 0) _networkResolutionIdle!.TrySetResult();
        }
    }

    private Task DrainWindowsNetworkResolutionAsync()
    {
        // Dispose has closed admission before taking this snapshot. This joins
        // receipt projection and live-effect cleanup as well as the native task.
        lock (_admissionGate) return _networkResolutionIdle?.Task ?? Task.CompletedTask;
    }

    private async Task<OperationReceipt> ExecuteWindowsNetworkResolutionReceiptAsync(
        OperationRequest request, string jid, string rid, CancellationToken token)
    {
        // The original external envelope was validated before Engine's generic
        // projection added this marker. Never admit a caller-supplied marker;
        // only remove the internally generated one for the second typed parse.
        var shape = request with
        {
            Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        var invocation = WindowsNetworkResolutionContract.Parse(shape);
        var operations = _windowsNetworkResolution;
        if (_directOnly || operations?.IsCurrentOwner() != true)
            return new(false, jid, rid, "failed", OperationNames.Public(request), "auth");

        // Do not turn cancellation into an early return. The original native
        // query and any entered cancel callback must settle before releasing
        // ExecuteReceiptAsync's live effect and the provider's admission slot.
        var outcome = await operations.ExecuteAsync(invocation, token).ConfigureAwait(false);
        var data = new Dictionary<string, object?>
        {
            ["name"] = outcome.Name,
            ["recordType"] = outcome.RecordType,
            ["answers"] = outcome.Answers,
            ["queryIssued"] = outcome.QueryIssued
        };
        if (outcome.CancellationRequested) data["cancellationRequested"] = true;
        if (outcome.DeadlineElapsed) data["deadlineElapsed"] = true;
        if (request.Detail)
        {
            data["coverage"] = outcome.Coverage;
            data["observedUtc"] = outcome.ObservedUtc;
            data["elapsedMilliseconds"] = outcome.ElapsedMilliseconds;
            if (outcome.NativeError is { } error) data["nativeError"] = error;
            if (outcome.CancelNativeError is { } cancelError) data["cancelNativeError"] = cancelError;
        }
        var state = outcome.Ok ? "done" :
            outcome.CancellationRequested && !outcome.DeadlineElapsed ? "canceled" : "failed";
        return new(outcome.Ok, jid, rid, state, OperationNames.Public(request),
            outcome.Ok ? null : outcome.Code, data);
    }
}
