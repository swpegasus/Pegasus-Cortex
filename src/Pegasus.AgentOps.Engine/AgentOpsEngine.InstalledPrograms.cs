using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // Passive observation only, on the existing ordinary owner route. Neither
    // a registration nor its display metadata is proof of an installed binary.
    private OperationReceipt ExecuteInstalledProgramReceipt(OperationRequest request, string jid, string rid,
        CancellationToken token)
    {
        try
        {
            // The original envelope was validated before projection. Remove only
            // this generated marker; callers may never submit it themselves.
            var shape = request with { Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
            var invocation = WindowsInstalledProgramOperationContract.Parse(shape);
            var outcome = new WindowsInstalledProgramOperations(_registryOwner).Execute(invocation, token);
            var data = new Dictionary<string, object?>
            {
                ["action"] = outcome.Action, ["source"] = outcome.Source,
                ["programs"] = outcome.Programs, ["scanned"] = outcome.Scanned,
                ["has_more"] = outcome.HasMore, ["continuation"] = outcome.Continuation,
                ["visibility_partial"] = outcome.Partial, ["observation"] = outcome.Code,
                ["product_verified"] = false, ["log_available"] = false
            };
            return new(outcome.Ok, jid, rid, outcome.Ok ? "done" : "failed", OperationNames.Public(request),
                outcome.Ok ? null : outcome.Code, data, Partial: outcome.Partial);
        }
        catch (WindowsInstalledProgramContractException error)
        {
            return new(false, jid, rid, "failed", OperationNames.Public(request), "invalid",
                new Dictionary<string, object?> { ["field"] = error.Field, ["log_available"] = false });
        }
    }
}
