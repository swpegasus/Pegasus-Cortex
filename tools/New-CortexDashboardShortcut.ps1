[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostExecutable,
    [Parameter(Mandatory)][string] $DestinationDirectory
)
$ErrorActionPreference = 'Stop'
function Get-OrdinaryPath([string] $Value, [bool] $Directory) {
    if (-not [IO.Path]::IsPathFullyQualified($Value)) { throw 'Use an absolute path.' }
    $item = Get-Item -LiteralPath $Value -Force
    if ($item.PSIsContainer -ne $Directory) { throw 'Unexpected path type.' }
    $cursor = $item
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths are not accepted.' }
        $cursor = if ($cursor.PSIsContainer) { $cursor.Parent } else { $cursor.Directory }
    }
    return $item.FullName
}
$executable = Get-OrdinaryPath $HostExecutable $false
$directory = Get-OrdinaryPath $DestinationDirectory $true
if ([IO.Path]::GetFileName($executable) -cne 'PegasusCortex.Host.exe') { throw 'Expected the Cortex user host executable.' }
$shortcutPath = Join-Path $directory 'Cortex Dashboard.lnk'
if (Test-Path -LiteralPath $shortcutPath) { throw 'The shortcut already exists; no overwrite was performed.' }
# Task-local convenience only. No startup, PATH, service or MCP registration.
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.Arguments = '--dashboard'
    $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
    $shortcut.Description = 'Cortex utilization dashboard and user-controlled execution scopes (candidate)'
    $shortcut.Save()
    $verified = $shell.CreateShortcut($shortcutPath)
    if ($verified.TargetPath -cne $executable -or $verified.Arguments -cne '--dashboard') { throw 'Shortcut validation failed.' }
    [pscustomobject]@{ shortcut = $shortcutPath; target = $executable; arguments = '--dashboard'; machineChanged = $false } | ConvertTo-Json -Compress
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
