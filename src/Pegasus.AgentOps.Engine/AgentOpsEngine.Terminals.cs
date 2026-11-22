using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private readonly LocalTerminalManager? _terminals;

    private async Task<OperationReceipt> ExecuteTerminalRequestAsync(OperationRequest request, string jid, string rid, CancellationToken cancellationToken)
    {
        var op = OperationNames.Public(request);
        if (_terminals is null || _directOnly || request.Root is not null)
            return new(false, jid, rid, "failed", op, "user_dispatch_required");
        try
        {
            if (!TerminalContract.Validate(request.Arg, request.Body)) throw new AgentOpsException("invalid");
            if (request.Arg["op"] == "connect" && request.Ws is not null)
            {
                var workspace = _workspaces.Resolve(request.Ws, request.Cwd ?? Environment.CurrentDirectory);
                if (workspace is null) throw new AgentOpsException("precondition");
                request = request with { PathRoots = new Dictionary<string, string> { ["cwd"] = workspace.Path } };
            }
            IDisposable Admit()
            {
                lock (_admissionGate)
                {
                    if (_stopping) throw new AgentOpsException("terminal_owner_lost");
                    if (MaintenanceClosed()) throw new AgentOpsException("maintenance_busy");
                    return RegisterMaintenanceEffect(request, jid, rid, 0);
                }
            }
            // Intentionally outside Claim/Finish/SqliteJobStore. Body and Data can
            // contain terminal content; neither is a durable operational receipt.
            var data = await _terminals.ExecuteAsync(request with { Rid = rid }, Admit, Admit, cancellationToken).ConfigureAwait(false);
            return new(true, jid, rid, "done", op, Data: data);
        }
        catch (AgentOpsException ex) { return new(false, jid, rid, "failed", op, ex.Code); }
        catch (UnauthorizedAccessException) { return new(false, jid, rid, "failed", op, "access"); }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { return new(false, jid, rid, "failed", op, "terminal_native"); }
    }
}
