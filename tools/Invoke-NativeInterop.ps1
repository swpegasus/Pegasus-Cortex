[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Start', 'Describe', 'Stop')][string]$Action,
    [string]$ClientExecutable,
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
$TaskSourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$TaskHarness = Join-Path $TaskSourceRoot 'tests\PegasusCortex.Mcp.Local.Tests\bin\Release\net10.0-windows\PegasusCortex.Mcp.Local.Tests.exe'
if (-not (Test-Path -LiteralPath $TaskHarness -PathType Leaf)) { throw 'Build the Release source test apphost first.' }
if ($Action -eq 'Prepare') {
    if ([string]::IsNullOrWhiteSpace($ClientExecutable)) { throw 'ClientExecutable must be the exact OS-observed client executable.' }
    & $TaskHarness native-interop prepare --application $ClientExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Isolated fixture preparation failed.' }
    return
}
if ([string]::IsNullOrWhiteSpace($RunRoot)) { throw 'RunRoot is required.' }
# The executable validates the exact source scratch parent, GUID leaf, owner ACL,
# SID/session, bounded manifest, pipe identity and non-reparse components before
# we use this path. It never consults installed bindings or credentials.
$TaskDescriptorLines = & $TaskHarness native-interop describe --root $RunRoot
if ($LASTEXITCODE -ne 0) { throw 'Invalid isolated fixture; no process was started.' }
$TaskDescriptor = ($TaskDescriptorLines -join "`n") | ConvertFrom-Json
if ($Action -eq 'Describe') { $TaskDescriptor | ConvertTo-Json -Compress; return }
if ($Action -eq 'Stop') {
    & $TaskHarness native-interop stop --root $TaskDescriptor.root
    if ($LASTEXITCODE -ne 0) { throw 'Isolated stop request failed.' }
    return
}
$TaskReady = Join-Path $TaskDescriptor.root 'ready.json'
if (Test-Path -LiteralPath $TaskReady) { throw 'This fixture has already been started; prepare a new one rather than replacing it.' }
$TaskProcess = Start-Process -FilePath $TaskHarness `
    -ArgumentList @('native-interop', 'serve', '--root', ('"' + $TaskDescriptor.root + '"')) `
    -WorkingDirectory $TaskSourceRoot -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $TaskDescriptor.root 'provider.stdout.jsonl') `
    -RedirectStandardError (Join-Path $TaskDescriptor.root 'provider.stderr.txt') -PassThru
$TaskDeadline = [DateTime]::UtcNow.AddSeconds(10)
while (-not (Test-Path -LiteralPath $TaskReady) -and -not $TaskProcess.HasExited -and [DateTime]::UtcNow -lt $TaskDeadline) {
    Start-Sleep -Milliseconds 50
    $TaskProcess.Refresh()
}
if (-not (Test-Path -LiteralPath $TaskReady)) { throw 'Source provider did not start; inspect the bounded fixture logs. Its lifetime is capped at 900 seconds.' }
[pscustomobject]@{
    ok = $true; state = 'launched'; isolated = $true; pid = $TaskProcess.Id
    descriptorFile = (Join-Path $TaskDescriptor.root 'endpoint.json')
    nativePipe = $TaskDescriptor.nativePipe; lifetimeSeconds = $TaskDescriptor.lifetimeSeconds
} | ConvertTo-Json -Compress
# Keep the ordinary shell/job owner alive. Codex may dispose descendants when
# a launching exec cell exits; a detached Start-Process alone is not durable.
# Use an exec session (short yield) or a dedicated terminal, never a PAO run.
$TaskProcess.WaitForExit()
if ($TaskProcess.ExitCode -ne 0) { throw 'Isolated source provider failed; inspect its fixture diagnostics.' }
