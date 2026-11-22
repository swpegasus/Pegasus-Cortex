using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // Local UI only. The lease carries no reference or password, creates no job,
    // and is not an operation admitted through MCP or the execution pipe.
    internal IDisposable BeginLocalCertificatePasswordOperation()
        =>BeginLocalCertificateOperation("local_password","certificate_secret_owner_denied");

    // The whole guided workflow, including original UAC children, remains owned
    // when its page closes. Individual certificate actions retain their own jobs.
    internal IDisposable BeginLocalMcpSetupOperation()
        =>BeginLocalCertificateOperation("local_mcp_setup","certificate_setup_owner_denied");

    private IDisposable BeginLocalCertificateOperation(string operation,string denied)
    {
        if (_directOnly || _certificateOwner?.IsCurrentOwner() != true || _userHostAdmission is null)
            throw new AgentOpsException(denied);
        var admission = _userHostAdmission.TryEnter() ?? throw new AgentOpsException("host_stopping");
        IDisposable? effect = null;
        try
        {
            lock (_admissionGate)
            {
                if (_stopping) throw new AgentOpsException("host_stopping");
                if (MaintenanceClosed()) throw new AgentOpsException("maintenance_busy");
                if (!_certificateOwner.IsCurrentOwner()) throw new AgentOpsException(denied);
                effect = RegisterMaintenanceEffect(new(operation, new Dictionary<string, string>()),
                    Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 0);
            }
            return new LocalCertificateOperationLease(effect, admission);
        }
        catch
        {
            try { effect?.Dispose(); }
            finally { admission.Dispose(); }
            throw;
        }
    }

    private sealed class LocalCertificateOperationLease(IDisposable effect, IDisposable admission) : IDisposable
    {
        private IDisposable? _effect = effect;
        private IDisposable? _admission = admission;
        public void Dispose()
        {
            // Release active-effect ownership before announcing an idle owner.
            // Either observer may query the existing boundary during shutdown.
            try { Interlocked.Exchange(ref _effect, null)?.Dispose(); }
            finally { Interlocked.Exchange(ref _admission, null)?.Dispose(); }
        }
    }
}
