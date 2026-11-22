using System.IO.Pipes;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Protocol;

namespace Pegasus.Cortex.LocalTransport.Pipes;

/// <summary>
/// One explicit current-user local connection. The caller supplies a bounded
/// timeout; no endpoint is discovered from environment or network configuration.
/// </summary>
public sealed class CortexLocalPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _acceptingCalls = 1;

    private CortexLocalPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async ValueTask<CortexLocalPipeClient> ConnectAsync(
        CortexLocalEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var pipe = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return new CortexLocalPipeClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>?> CallAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        CortexLocalFrameProtocol.ValidateRequestPayload(request);
        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            try
            {
                if (!_pipe.IsConnected)
                    throw new IOException("The local Cortex pipe is not connected.");
                await CortexLocalFrameProtocol.WriteRequestAsync(_pipe, request, cancellationToken)
                    .ConfigureAwait(false);
                var response = await CortexLocalFrameProtocol.ReadResponseAsync(
                    _pipe,
                    cancellationToken).ConfigureAwait(false);
                if (response is null)
                    return null;
                ReadOnlyMemory<byte> captured = response;
                return captured;
            }
            catch
            {
                // Cancellation or an incomplete exchange can leave a byte
                // stream between frames. Retire that connection rather than
                // allowing a later caller to consume mismatched bytes.
                Interlocked.Exchange(ref _acceptingCalls, 0);
                await _pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _callGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _acceptingCalls, 0);
        await _callGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Keep the semaphore alive so calls already queued when disposal
            // began can wake, observe the terminal state, and fail cleanly.
            _callGate.Release();
        }
    }

    private void ThrowIfUnavailable() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _acceptingCalls) == 0,
            this);
}
