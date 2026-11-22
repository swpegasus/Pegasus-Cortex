[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$binding = Get-ItemProperty -LiteralPath 'HKLM:\Software\Pegasus\Cortex'
$installRoot = [IO.Path]::GetFullPath([string]$binding.InstallLocation).TrimEnd('\')
if (-not [IO.Path]::IsPathFullyQualified($installRoot) -or -not (Test-Path -LiteralPath $installRoot -PathType Container)) {
    throw 'The registered Cortex install root is unavailable.'
}

# Read-only, identifier/size-only inspection. No command lines, environment,
# database bodies, process termination, forced GC or working-set trimming.
$names = @('PegasusCortex', 'PegasusCortex.Host', 'PegasusCortex.ExecutionService',
    'pao', 'Pegasus.AgentOps.ProcessHost', 'Pegasus.AgentOps.UiHost')
$rows = foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    try {
        $path = $process.Path
        if (-not $path -or -not $path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $role = if ($process.ProcessName -eq 'PegasusCortex.Host') { 'user-host' }
            elseif ($process.ProcessName -eq 'PegasusCortex.ExecutionService') { 'execution-service' }
            elseif ($path -eq ($installRoot + '\PegasusCortex.exe')) { 'launcher' }
            elseif ($path.StartsWith($installRoot + '\bin\semantics\', [StringComparison]::OrdinalIgnoreCase)) { 'semantic-connection' }
            else { 'execution-client-or-helper' }
        [pscustomobject]@{
            pid = $process.Id
            role = $role
            privateBytes = $process.PrivateMemorySize64
            workingSetBytes = $process.WorkingSet64
            startedUtc = $process.StartTime.ToUniversalTime().ToString('O')
        }
    }
    catch [System.InvalidOperationException] { } # Process exited during the snapshot.
    finally { $process.Dispose() }
}
$rows = @($rows)
$groups = @($rows | Group-Object role | ForEach-Object {
    [pscustomobject]@{
        role = $_.Name
        count = $_.Count
        privateBytes = [long](($_.Group | Measure-Object privateBytes -Sum).Sum)
        workingSetBytes = [long](($_.Group | Measure-Object workingSetBytes -Sum).Sum)
    }
})
[pscustomobject]@{
    contract = 'pegasus.cortex.memory-snapshot/1'
    measuredUtc = [DateTimeOffset]::UtcNow.ToString('O')
    processCount = $rows.Count
    groups = $groups
    largest = @($rows | Sort-Object privateBytes -Descending | Select-Object -First 12)
    note = 'Private bytes and working set are distinct; summed working sets may count shared pages more than once.'
} | ConvertTo-Json -Depth 5 -Compress
