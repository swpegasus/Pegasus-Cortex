using System.Runtime.InteropServices;

namespace Pegasus.Cortex.Service.Lifecycle;

/// <summary>
/// Converts supervisor termination and interactive cancellation into one process-lifetime token.
/// Pegasus Server remains responsible for service registration and supervision policy.
/// </summary>
internal sealed class CortexServiceShutdownSource : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly PosixSignalRegistration? _terminateRegistration;
    private int _disposed;

    private CortexServiceShutdownSource()
    {
        Console.CancelKeyPress += OnConsoleCancelKeyPress;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            _terminateRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    RequestShutdown();
                });
        }
    }

    internal CancellationToken Token => _shutdown.Token;

    internal static CortexServiceShutdownSource Create() => new();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Console.CancelKeyPress -= OnConsoleCancelKeyPress;
        _terminateRegistration?.Dispose();
        _shutdown.Dispose();
    }

    private void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        RequestShutdown();
    }

    private void RequestShutdown()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A signal raced with teardown after the rehearsal had already stopped.
        }
    }
}
