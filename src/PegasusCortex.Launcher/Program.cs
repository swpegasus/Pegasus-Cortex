using System.ComponentModel;
using Pegasus.Cortex.Launcher;

var guiRoute = CortexLauncherConsole.IsGuiRoute(args);
try
{
    // Attach only where appropriate, before .NET caches any Console streams.
    CortexLauncherConsole.Prepare(args);
    var exitCode = await CortexStableLauncher.RunSelectedVersionAsync(
        args,
        CancellationToken.None).ConfigureAwait(false);
    // The selected GUI application owns its errors. Only launcher failures
    // below need a launcher dialog; child exit codes remain unchanged.
    return exitCode;
}
catch (Exception exception) when (
    exception is ArgumentException or InvalidDataException or IOException or
        InvalidOperationException or UnauthorizedAccessException or Win32Exception)
{
    CortexLauncherConsole.ReportFailure(guiRoute, exception.Message);
    return 1;
}
