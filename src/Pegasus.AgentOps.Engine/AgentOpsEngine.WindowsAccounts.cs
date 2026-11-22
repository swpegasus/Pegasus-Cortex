using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private WindowsAccountOperations? _windowsAccounts;

    internal void BindWindowsAccounts(WindowsAccountOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_directOnly || _userHostAdmission is null || !operations.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _windowsAccounts, operations, null) is not null)
            throw new AgentOpsException("auth");
    }

    private OperationReceipt ExecuteWindowsAccountReceipt(OperationRequest request, string jid, string rid,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, jid, rid, "failed", OperationNames.Public(request), "platform_unsupported");
        try
        {
            var shape = request with { Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
            var invocation = WindowsAccountOperationContract.Parse(shape);
            if (_directOnly || _windowsAccounts?.IsCurrentOwner() != true)
                return new(false, jid, rid, "failed", OperationNames.Public(request), "auth");
            var outcome = _windowsAccounts.Execute(invocation, token);
            var data = new Dictionary<string, object?> { ["items"] = outcome.Items };
            if (outcome.HasMore) data["afterKey"] = outcome.NextKey;
            if (outcome.Partial) data["coverage"] = outcome.Coverage;
            if (outcome.Issues.Count > 0) data["issues"] = outcome.Issues;
            if (request.Detail)
            {
                data["view"] = outcome.View; data["observedUtc"] = outcome.ObservedUtc;
                data["coverage"] = outcome.Coverage; data["observation"] = outcome.Code;
            }
            return new(outcome.Ok, jid, rid, outcome.Ok ? "done" : "failed", OperationNames.Public(request),
                outcome.Ok ? null : outcome.Code, data, Partial: outcome.Partial);
        }
        catch (WindowsAccountContractException error)
        {
            return new(false, jid, rid, "failed", OperationNames.Public(request), "invalid",
                new Dictionary<string, object?> { ["field"] = error.Field });
        }
    }
}
