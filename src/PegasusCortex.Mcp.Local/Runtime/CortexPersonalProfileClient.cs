using System.ComponentModel;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>
/// Optional read-only session status, never a dependency for the core database.
/// No profile bodies, arbitrary commands, database paths, or credentials cross
/// this pipe. Each read is fresh so a restarted tray works with existing MCPs.
/// </summary>
internal sealed class CortexPersonalProfileClient : ICortexPersonalProfileProvider
{
    private readonly string? _pipeName;
    private readonly LocalPeer? _expectedServer;
    private readonly TimeSpan _timeout;
    private readonly Action<string, string?>? _report;

    public CortexPersonalProfileClient(string? logDirectory = null)
        : this(null, null, TimeSpan.FromMilliseconds(250),
            logDirectory is null ? null : new CortexPersonalProfileDiagnostics(logDirectory).Report)
    {
    }

    // The alternate endpoint is an explicit test seam, not environment-selected
    // runtime configuration or a fallback to another user's session.
    internal CortexPersonalProfileClient(string? pipeName, LocalPeer? expectedServer,
        TimeSpan timeout, Action<string, string?>? report = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _pipeName = pipeName;
        _expectedServer = expectedServer;
        _timeout = timeout;
        _report = report;
    }

    public async ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueryAsync(cancellationToken).ConfigureAwait(false);
        // Optional availability is observed, never repaired by launching Cortex.
        return Report(result);
    }

    private async ValueTask<CortexPersonalProfileV2> QueryAsync(CancellationToken cancellationToken)
    {
        CortexPersonalProfileV2 result;
        try
        {
            var owner = _expectedServer ?? LocalIdentity.Current();
            await using var pipe = new NamedPipeClientStream(".",
                _pipeName ?? CortexPersonalProfilePipe.Name(owner.Session),
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!LocalIdentity.CaptureExpectedServer(pipe, owner).SameIdentity(owner))
                return Report(Unavailable("profile_peer_mismatch"));
            await CortexPersonalProfilePipe.WriteRequestAsync(pipe, deadline.Token).ConfigureAwait(false);
            var status = await CortexPersonalProfilePipe.ReadResponseAsync(pipe, deadline.Token)
                .ConfigureAwait(false);
            result = new(status.State, status.Profile, status.Generation, status.IssueCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            result = Unavailable("profile_protocol_invalid");
        }
        catch (Exception exception) when (exception is OperationCanceledException or
            TimeoutException or IOException)
        {
            result = Unavailable(CortexPersonalProfilePipe.PipeUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            ArgumentException or FormatException)
        {
            result = Unavailable("profile_protocol_invalid");
        }
        catch (Exception exception) when (exception is AgentOpsException or
            UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            result = Unavailable("profile_peer_mismatch");
        }
        return result;
    }

    private static CortexPersonalProfileV2 Unavailable(string issueCode) =>
        new(CortexPersonalProfilePipe.NotLoaded, IssueCode: issueCode);

    private CortexPersonalProfileV2 Report(CortexPersonalProfileV2 result)
    {
        // Logging is best-effort and contains only stable state/error codes.
        // It must not turn a recoverable personal-layer outage into core failure.
        try { _report?.Invoke(result.State, result.IssueCode); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or SecurityException) { }
        return result;
    }
}
