namespace Pegasus.Cortex.LocalTransport.Protocol;

/// <summary>
/// Processes one exact request frame. Returning null represents a notification,
/// not a failed or empty response.
/// </summary>
public interface ICortexLocalRequestHandler
{
    ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken);
}
