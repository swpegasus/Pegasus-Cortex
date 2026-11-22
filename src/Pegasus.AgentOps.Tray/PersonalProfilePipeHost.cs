using System.IO.Pipes;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

/// <summary>
/// One bounded read-only status endpoint, independent of the privileged service
/// and operation dispatcher. Authorization is the captured SID plus session.
/// </summary>
internal static class PersonalProfilePipeHost
{
    internal static Task RunAsync(
        LocalPeer owner, PersonalProfileAvailability availability, CancellationToken cancellationToken) =>
        RunAsync(CortexPersonalProfilePipe.Name(owner.Session), owner, availability, cancellationToken);

    // An exact isolated name supports real pipe tests without claiming the live endpoint.
    internal static async Task RunAsync(
        string pipeName, LocalPeer owner, PersonalProfileAvailability availability, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = LocalPipeFactory.Create(pipeName, owner.Sid);
                availability.PipeAvailable();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    var peer = LocalIdentity.Capture(pipe);
                    if (!IsAuthorized(owner, peer)) continue;
                    await CortexPersonalProfilePipe.ReadRequestAsync(pipe, request.Token).ConfigureAwait(false);
                    await CortexPersonalProfilePipe.WriteResponseAsync(
                        pipe, availability.Snapshot(), request.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                    JsonException or ArgumentException or InvalidOperationException or FormatException or
                    OperationCanceledException or AgentOpsException or UnauthorizedAccessException)
                {
                    // Malformed, disconnected, timed-out, and unauthenticated clients
                    // affect only their connection. Never retain caller payload or text.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                availability.PipeUnavailable();
                try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal static bool IsAuthorized(LocalPeer owner, LocalPeer peer) =>
        owner.Session > 0 && peer.SameIdentity(owner);
}
