[CmdletBinding()]
param([Parameter(Mandatory)][string] $HostExecutable)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($HostExecutable)) { throw 'Use an absolute candidate path.' }
$item = Get-Item -LiteralPath $HostExecutable -Force
if ($item.PSIsContainer -or $item.Name -cne 'PegasusCortex.Host.exe') { throw 'Unexpected candidate executable.' }
$ancestor = $item
while ($null -ne $ancestor) {
    if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unexpected reparse path.' }
    $ancestor = if ($ancestor.PSIsContainer) { $ancestor.Parent } else { $ancestor.Directory }
}
$start = [Diagnostics.ProcessStartInfo]::new($item.FullName)
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = $item.DirectoryName
$start.ArgumentList.Add('--dashboard')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CortexDashboardWindowProbe {
    private delegate bool EnumCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr Find(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId) return true;
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "Pegasus Cortex — utilization and execution controls") return true;
            found = window; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Close(IntPtr window) => PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
}
'@
$process = [Diagnostics.Process]::Start($start)
try {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw 'Candidate dashboard exited before creating its window.' }
        $window = [CortexDashboardWindowProbe]::Find($process.Id)
    } while ($window -eq [IntPtr]::Zero -and $timer.ElapsedMilliseconds -lt 10000)
    if ($window -eq [IntPtr]::Zero) { throw 'Dashboard did not create its window within the bound.' }
    if (-not [CortexDashboardWindowProbe]::Close($window) -or -not $process.WaitForExit(10000)) { throw 'Dashboard did not close cleanly.' }
    if ($process.ExitCode -ne 0) { throw 'Dashboard returned a failure exit.' }
    [pscustomobject]@{ candidateWindow = 'created-and-closed'; exit = 0; policyActions = 0; uiContentCaptured = $false } | ConvertTo-Json -Compress
}
finally {
    # This handle belongs only to the candidate launched above, never the live tray.
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
