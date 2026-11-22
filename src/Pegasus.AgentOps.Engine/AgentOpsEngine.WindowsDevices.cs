using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private WindowsDeviceOperations? _windowsDevices;

    internal void BindWindowsDevices(WindowsDeviceOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_directOnly || _userHostAdmission is null || !operations.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _windowsDevices, operations, null) is not null)
            throw new AgentOpsException("auth");
    }

    private OperationReceipt ExecuteWindowsDeviceReceipt(OperationRequest request, string jid, string rid,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, jid, rid, "failed", OperationNames.Public(request), "platform_unsupported");
        try
        {
            // Original envelopes are checked before projection. Only this
            // generated body marker is removed at the internal read boundary.
            var shape = request with { Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
            var invocation = WindowsDeviceOperationContract.Parse(shape);
            if (_directOnly || _windowsDevices?.IsCurrentOwner() != true)
                return new(false, jid, rid, "failed", OperationNames.Public(request), "auth");
            var outcome = _windowsDevices.Execute(invocation, token);
            var data = new Dictionary<string, object?> { ["devices"] = outcome.Devices };
            if (outcome.HasMore) data["afterInstanceId"] = outcome.NextAfterInstanceId;
            if (request.Detail)
            {
                data["scope"] = outcome.Scope; data["scanned"] = outcome.Scanned;
                data["observation"] = outcome.Code;
            }
            if (!outcome.Ok && outcome.NativeError is { } native)
            { data["windows_status"] = native; data["native_domain"] = outcome.NativeDomain; }
            return new(outcome.Ok, jid, rid, outcome.Ok ? "done" : "failed", OperationNames.Public(request),
                outcome.Ok ? null : outcome.Code, data, Partial: outcome.Partial);
        }
        catch (WindowsDeviceContractException error)
        {
            return new(false, jid, rid, "failed", OperationNames.Public(request), "invalid",
                new Dictionary<string, object?> { ["field"] = error.Field, ["log_available"] = false });
        }
    }
}
