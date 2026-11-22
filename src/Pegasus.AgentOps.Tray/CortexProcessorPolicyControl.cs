using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexProcessorSaveResult(bool Ok,string? Error=null);
internal interface ICortexProcessorPolicyController
{
    Task<CortexProcessorSaveResult> SaveAsync(CortexProcessorSettings settings,CancellationToken cancellationToken);
}

/// <summary>One fixed UAC save; affinity applies only to the helper, never to an existing resident.</summary>
internal sealed class CortexProcessorPolicyControl:ICortexProcessorPolicyController
{
    internal static CortexProcessorSettings ReadCurrent()=>CortexProcessorPolicy.ReadForInstallRoot(ResolveInstalledHost().Root);

    private static (string Root,string Host) ResolveInstalledHost()
    {
        var host=Path.GetFullPath(Environment.ProcessPath??throw new InvalidDataException("The installed Host path is unavailable."));
        var root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host)!,"..",".."));
        var expected=Path.Combine(root,CortexProductCatalogV1.StableRolePaths["host"].Replace('/',Path.DirectorySeparatorChar));
        if(!StringComparer.OrdinalIgnoreCase.Equals(host,expected))
            throw new InvalidDataException("Processor settings must be saved by the installed Cortex Host.");
        CortexPrivilegedCodePath.RequireProtectedImage(root,host);
        _=CortexDataLocation.ResolveMachineDataRoot(root);
        return (root,host);
    }

    public async Task<CortexProcessorSaveResult> SaveAsync(CortexProcessorSettings settings,CancellationToken cancellationToken)
    {
        try
        {
            var selection=CortexProcessorPolicy.FormatSelection(settings);
            var installed=ResolveInstalledHost();
            var start=new ProcessStartInfo(installed.Host)
            {
                UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--processor-policy");
            start.ArgumentList.Add(selection);
            cancellationToken.ThrowIfCancellationRequested();
            using var process=Process.Start(start);
            if(process is null)return new(false,"The elevated settings helper could not be started.");
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(45),cancellationToken);
            if(process.ExitCode!=0)return new(false,$"Processor settings were not confirmed (helper exit {process.ExitCode}).");
            return CortexProcessorPolicy.FormatSelection(CortexProcessorPolicy.ReadForInstallRoot(installed.Root))==selection
                ?new(true):new(false,"The saved selection could not be confirmed. Refresh before making another change.");
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223)
        {return new(false,"Windows elevation was cancelled; processor settings are unchanged.");}
        catch(Exception error)when(error is TimeoutException or OperationCanceledException)
        {return new(false,"The save outcome is unknown. Refresh to inspect the saved selection; no retry was sent.");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {return new(false,"Processor settings could not be saved: "+error.GetType().Name);}
    }

    internal static int RunElevated(string selection)
    {
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 4;
            var settings=CortexProcessorPolicy.ParseSelection(selection);
            var installed=ResolveInstalledHost();
            CortexProcessorPolicy.SaveForInstallRoot(installed.Root,settings);
            _=CortexProcessorPolicy.ApplyForInstallRoot(installed.Root);
            return 0;
        }
        catch(InvalidDataException) { return 2; }
        catch(UnauthorizedAccessException) { return 12; }
        catch(Exception error)when(error is IOException or Win32Exception or ArgumentException or NotSupportedException)
        {return 8;}
    }
}
