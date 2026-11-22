namespace Pegasus.AgentOps.Tray;

internal static class TrayUiThread
{
    internal static Task RunAsync(Func<ApplicationContext> createContext, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ApplicationConfiguration.Initialize();
                // Route message-loop failures to the supervisor so personal
                // availability is withdrawn and the execution host can survive.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                using var context = createContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                using var close = cancellationToken.Register(() =>
                {
                    try { dispatcher.BeginInvoke((Action)context.ExitThread); }
                    catch (InvalidOperationException) { }
                });
                if (!cancellationToken.IsCancellationRequested) Application.Run(context);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { Name = "Cortex desktop UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

internal static class TrayUiSupervisor
{
    internal static async Task RunAsync(
        Func<Task> runOnce,
        CancellationToken cancellationToken,
        Action<Exception>? report = null,
        TimeSpan? restartDelay = null)
    {
        var delay = restartDelay ?? TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await runOnce().ConfigureAwait(false);
                // A deliberate UI close is terminal for this optional surface only.
                // The resident execution host continues independently.
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                report?.Invoke(ex);
            }

            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
