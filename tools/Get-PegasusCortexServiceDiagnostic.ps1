[CmdletBinding()]
param(
    [string] $ReportPath = '',
    [switch] $ReadOnly
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Pegasus Cortex Execution'
$root = 'C:\Program Files\Pegasus\Cortex'
$state = [System.IO.Path]::Combine($root, 'state', 'service')
$configPath = [System.IO.Path]::Combine($state, 'service.v1.json')

function Read-ServiceEvidence {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    return [ordered]@{
        state = [string]$service.State
        startMode = [string]$service.StartMode
        account = [string]$service.StartName
        image = [string]$service.PathName
        exitCode = [int]$service.ExitCode
        serviceSpecificExitCode = [int]$service.ServiceSpecificExitCode
        processId = [int]$service.ProcessId
    }
}

$before = Read-ServiceEvidence
$startError = $null
if (-not $ReadOnly -and $null -ne $before -and $before.state -ne 'Running') {
    try { Start-Service -Name $serviceName -ErrorAction Stop }
    catch {
        $startError = [ordered]@{
            type = $_.Exception.GetType().Name
            hresult = ('0x{0:X8}' -f $_.Exception.HResult)
            innerType = $_.Exception.InnerException?.GetType().Name
            innerHresult = if ($null -eq $_.Exception.InnerException) { $null } else { '0x{0:X8}' -f $_.Exception.InnerException.HResult }
            errorId = [string]$_.FullyQualifiedErrorId
            message = ([string]$_.Exception.Message).Substring(0, [Math]::Min(512, ([string]$_.Exception.Message).Length))
        }
    }
    [Threading.Thread]::Sleep(2000)
}
$after = Read-ServiceEvidence
$nativeStart = $null
if ($null -ne $startError -and $null -ne $after -and $after.state -ne 'Running') {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = [System.IO.Path]::Combine($env:SystemRoot, 'System32', 'sc.exe')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('start')
    $startInfo.ArgumentList.Add($serviceName)
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'The fixed service diagnostic did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $nativeStart = [ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdout.Substring(0, [Math]::Min(2048, $stdout.Length))
            stderr = $stderr.Substring(0, [Math]::Min(2048, $stderr.Length))
        }
    }
    finally { $process.Dispose() }
    [Threading.Thread]::Sleep(1000)
    $after = Read-ServiceEvidence
}
$config = $null
$configError = $null
try {
    $value = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 8
    $config = [ordered]@{
        contract = [string]$value.contract
        version = [int]$value.version
        ownerSid = [string]$value.ownerSid
    }
}
catch { $configError = $_.Exception.GetType().Name }

$acl = foreach ($path in @($root, (Join-Path $root 'state'), $state, (Join-Path $state 'startup.log'), (Join-Path $state 'workspaces.json'))) {
    try {
        $value = Get-Acl -LiteralPath $path -ErrorAction Stop
        [ordered]@{ role = Split-Path -Leaf $path; owner = [string]$value.Owner; protected = [bool]$value.AreAccessRulesProtected; allow = @($value.Access | Where-Object AccessControlType -eq Allow | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }); error = $null }
    }
    catch {
        [ordered]@{ role = Split-Path -Leaf $path; owner = $null; protected = $null; error = $_.Exception.GetType().Name }
    }
}

$result = [ordered]@{
    contract = 'pegasus-cortex-service-diagnostic'
    version = 1
    before = $before
    startError = $startError
    nativeStart = $nativeStart
    after = $after
    config = $config
    configError = $configError
    acl = @($acl)
    workspaceMetadata = $(
        $workspacePath = Join-Path $state 'workspaces.json'
        if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
            $workspaceItem = Get-Item -LiteralPath $workspacePath -Force
            if (($workspaceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $workspaceItem.Length -gt 1048576) { throw 'Unexpected workspace registry metadata target.' }
            $workspaceSecurity = Get-Acl -LiteralPath $workspacePath
            [ordered]@{bytes=$workspaceItem.Length;lastWriteUtc=$workspaceItem.LastWriteTimeUtc;sha256=(Get-FileHash -LiteralPath $workspacePath -Algorithm SHA256).Hash;dacl=$workspaceSecurity.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)}
        }
    )
    stateOwnership = @(
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($state)
        $examined = 0
        while ($pending.Count -gt 0 -and $examined -lt 256) {
            $directory = $pending.Pop()
            foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
                if (++$examined -gt 256) { break }
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unexpected service state reparse point.' }
                $security = Get-Acl -LiteralPath $entry.FullName
                $owner = $security.GetOwner([Security.Principal.SecurityIdentifier]).Value
                if ($owner -cne 'S-1-5-18') {
                    [ordered]@{entry=[IO.Path]::GetRelativePath($state, $entry.FullName);owner=$owner;directory=$entry.PSIsContainer;allow=@($security.Access | Where-Object AccessControlType -eq Allow | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value })}
                }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
        [ordered]@{examined=$examined;bounded=($pending.Count -gt 0 -or $examined -gt 256)}
    )
    startupTrace = @(
        $tracePath = Join-Path $state 'startup.log'
        if (Test-Path -LiteralPath $tracePath) {
            $traceItem = Get-Item -LiteralPath $tracePath -Force
            if (($traceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unexpected startup log reparse point.' }
            Get-Content -LiteralPath $tracePath -Tail 32 | Where-Object { $_ -match '^\S+ stage=[A-Za-z0-9_.-]{1,96} detail=[A-Za-z0-9_.-]{1,96}$' }
        }
    )
}
$json = $result | ConvertTo-Json -Compress -Depth 8
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $report = [System.IO.Path]::GetFullPath($ReportPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($report)) | Out-Null
    [System.IO.File]::WriteAllText($report, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}
$json
