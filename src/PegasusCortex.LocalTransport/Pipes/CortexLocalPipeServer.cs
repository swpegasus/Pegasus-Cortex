using System.IO.Pipes;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Protocol;

namespace Pegasus.Cortex.LocalTransport.Pipes;

/// <summary>
/// Sequential current-user-only local service transport. CurrentUserOnly
/// authenticates the operating-system user, not an individual same-user
/// process or agent; profile names are endpoint/capability namespaces rather
/// than credentials. NamedPipeStream maps to Windows named pipes and
/// Unix-domain sockets; no TCP/UDP listener is created.
/// Each connection has an explicit frame deadline and request budget so one local
/// peer cannot monopolize the endpoint indefinitely.
/// </summary>
public sealed class CortexLocalPipeServer
{
    public static readonly TimeSpan DefaultFrameIdleTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinimumFrameIdleTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumFrameIdleTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaximumRequestsPerSession = 64;
    public const int MaximumRequestsPerSessionLimit = 256;

    private readonly CortexLocalEndpoint _endpoint;
    private readonly Func<ICortexLocalRequestHandler> _handlerFactory;
    private readonly Action<Exception>? _sessionFault;
    private readonly TimeSpan _frameIdleTimeout;
    private readonly int _maximumRequestsPerSession;

    public CortexLocalPipeServer(
        CortexLocalEndpoint endpoint,
        ICortexLocalRequestHandler handler,
        Action<Exception>? sessionFault = null,
        TimeSpan? frameIdleTimeout = null,
        int maximumRequestsPerSession = DefaultMaximumRequestsPerSession)
        : this(
            endpoint,
            () => handler ?? throw new InvalidOperationException(
                "The local Cortex request handler is unavailable."),
            sessionFault,
            frameIdleTimeout,
            maximumRequestsPerSession)
    {
        ArgumentNullException.ThrowIfNull(handler);
    }

    public CortexLocalPipeServer(
        CortexLocalEndpoint endpoint,
        Func<ICortexLocalRequestHandler> handlerFactory,
        Action<Exception>? sessionFault = null,
        TimeSpan? frameIdleTimeout = null,
        int maximumRequestsPerSession = DefaultMaximumRequestsPerSession)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
        _sessionFault = sessionFault;
        _frameIdleTimeout = frameIdleTimeout ?? DefaultFrameIdleTimeout;
        if (_frameIdleTimeout < MinimumFrameIdleTimeout ||
            _frameIdleTimeout > MaximumFrameIdleTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIdleTimeout));
        }
        if (maximumRequestsPerSession is < 1 or > MaximumRequestsPerSessionLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumRequestsPerSession));
        _maximumRequestsPerSession = maximumRequestsPerSession;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pipe = CreateServer();
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var handler = _handlerFactory() ??
                    throw new InvalidOperationException(
                        "The local Cortex request handler factory returned null.");
                await ServeConnectionAsync(pipe, handler, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A malformed, timed-out, disconnected, or faulty session is
                // discarded. The next loop creates a clean framing boundary.
                ReportSessionFault(exception);
            }
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _endpoint.PipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough,
        inBufferSize: CortexLocalFrameProtocol.MaximumRequestBytes + sizeof(int),
        outBufferSize: CortexLocalFrameProtocol.MaximumResponseBytes + sizeof(int));

    private async Task ServeConnectionAsync(
        Stream pipe,
        ICortexLocalRequestHandler handler,
        CancellationToken cancellationToken)
    {
        for (var requestCount = 0;
             requestCount < _maximumRequestsPerSession;
             requestCount++)
        {
            var request = await ExecuteWithDeadlineAsync(
                token => CortexLocalFrameProtocol.ReadRequestAsync(pipe, token),
                cancellationToken).ConfigureAwait(false);
            if (request is null)
                return;
            var response = await ExecuteWithDeadlineAsync(
                token => handler.ProcessAsync(request, token),
                cancellationToken).ConfigureAwait(false);
            _ = await ExecuteWithDeadlineAsync(
                async token =>
                {
                    await CortexLocalFrameProtocol.WriteResponseAsync(
                        pipe,
                        response,
                        token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> ExecuteWithDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_frameIdleTimeout);
        var operationTask = operation(deadline.Token).AsTask();
        try
        {
            // WaitAsync enforces the boundary even when a custom handler does
            // not cooperate with the cancellation token it receives.
            return await operationTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            ObserveLateFault(operationTask);
            if (cancellationToken.IsCancellationRequested ||
                !deadline.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException(
                "The local Cortex frame phase exceeded its idle deadline.",
                exception);
        }
    }

    private static void ObserveLateFault(Task operationTask) =>
        _ = operationTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void ReportSessionFault(Exception exception)
    {
        if (_sessionFault is null)
            return;
        try
        {
            _sessionFault(exception);
        }
        catch
        {
            // Fault observation is advisory and must not become a second path
            // for terminating the local service loop.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException;
}
