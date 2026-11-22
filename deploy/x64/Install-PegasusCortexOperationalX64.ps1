[CmdletBinding()]
param(
    [string]$PublishRoot = "$PSScriptRoot\payload\app",
    [string]$ReleaseVersion = '1.2026.08.11.15.35.A',
    [string]$InstallRoot = "$env:LOCALAPPDATA\Programs\Pegasus\Cortex",
    [string]$DataRoot = "$env:LOCALAPPDATA\Pegasus\Cortex",
    [string]$CodexConfig = "$env:USERPROFILE\.codex\config.toml",
    [switch]$ReplaceRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw 'This historical per-user operational installer is disabled. Use Install-PegasusCortexV2X64.ps1 with explicit application and launcher package roots.'

function Assert-OrdinaryDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Expected an ordinary directory: '$Path'."
    }
}

function Assert-OrdinaryTree {
    param([Parameter(Mandatory)][string]$Path)
    Assert-OrdinaryDirectory -Path $Path
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Tree contains a reparse point: '$($item.FullName)'."
        }
    }
}

function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

if ($ReleaseVersion -notmatch '^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') {
    throw 'ReleaseVersion is invalid.'
}
$publish = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\', '/')
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
$data = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\', '/')
$config = [IO.Path]::GetFullPath($CodexConfig)
Assert-OrdinaryTree -Path $publish
foreach ($required in @('PegasusCortex.exe', 'PegasusCortex.dll', 'PegasusCortex.runtimeconfig.json')) {
    $path = Join-Path $publish $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "The publish is missing '$required'."
    }
}

$installParent = Split-Path -Parent $install
if (-not (Test-Path -LiteralPath $installParent)) {
    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
}
Assert-OrdinaryDirectory -Path $installParent
$versionRoot = Join-Path (Join-Path $install 'versions') $ReleaseVersion

if (-not (Test-Path -LiteralPath $install)) {
    $stage = Join-Path $installParent ('.cortex-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force
        $stageVersion = Join-Path (Join-Path $stage 'versions') $ReleaseVersion
        New-Item -ItemType Directory -Path $stageVersion -Force | Out-Null
        Copy-Item -Path (Join-Path $publish '*') -Destination $stageVersion -Recurse -Force
        Assert-OrdinaryTree -Path $stage
        [IO.Directory]::Move($stage, $install)
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
elseif (-not (Test-Path -LiteralPath $versionRoot)) {
    Assert-OrdinaryTree -Path $install
    $versions = Join-Path $install 'versions'
    if (-not (Test-Path -LiteralPath $versions)) {
        New-Item -ItemType Directory -Path $versions | Out-Null
    }
    $stageVersion = Join-Path $versions ('.version-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stageVersion | Out-Null
        Copy-Item -Path (Join-Path $publish '*') -Destination $stageVersion -Recurse -Force
        Assert-OrdinaryTree -Path $stageVersion
        [IO.Directory]::Move($stageVersion, $versionRoot)
    }
    finally {
        if (Test-Path -LiteralPath $stageVersion) {
            Remove-Item -LiteralPath $stageVersion -Recurse -Force
        }
    }
}
else {
    Assert-OrdinaryTree -Path $versionRoot
}

foreach ($name in @('data', 'config', 'backups', 'logs', 'receipts')) {
    $directory = Join-Path $data $name
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Assert-OrdinaryDirectory -Path $directory
}

$selector = Join-Path $data 'config\current-version.json'
$selectorTemporary = "$selector.$([Guid]::NewGuid().ToString('N')).tmp"
Write-Utf8NoBom -Path $selectorTemporary -Text (([ordered]@{
            SchemaVersion = 1
            ReleaseVersion = $ReleaseVersion
            SelectedAtUtc = [DateTime]::UtcNow.ToString('O')
        } | ConvertTo-Json -Compress) + "`n")
Move-Item -LiteralPath $selectorTemporary -Destination $selector -Force

$stableExecutable = Join-Path $install 'PegasusCortex.exe'
if (-not (Test-Path -LiteralPath $stableExecutable -PathType Leaf)) {
    throw 'The stable Cortex executable is missing after installation.'
}

$registrationChanged = $false
$configBackup = $null
if ($ReplaceRegistration) {
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
        throw "Codex configuration does not exist: '$config'."
    }
    $configText = [IO.File]::ReadAllText($config)
    $pattern = '(?ms)^\[mcp_servers\.pegasus_cortex\]\r?\n.*?(?=^\[|\z)'
    if (-not [regex]::IsMatch($configText, $pattern)) {
        throw 'The existing pegasus_cortex registration was not found; refusing an ambiguous edit.'
    }
    $backupRoot = Join-Path $data 'backups'
    $configBackup = Join-Path $backupRoot ("config-pre-operational-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')).toml")
    [IO.File]::Copy($config, $configBackup, $false)
    $quotedExecutable = $stableExecutable.Replace("'", "''")
    $replacement = "[mcp_servers.pegasus_cortex]`r`ncommand = '$quotedExecutable'`r`nargs = []`r`n`r`n"
    $updated = [regex]::Replace($configText, $pattern, $replacement, 1)
    $temporaryConfig = "$config.$([Guid]::NewGuid().ToString('N')).tmp"
    $replacementBackup = "$config.$([Guid]::NewGuid().ToString('N')).replace-backup"
    try {
        Write-Utf8NoBom -Path $temporaryConfig -Text $updated
        [void](Get-Content -LiteralPath $temporaryConfig -Raw)
        [IO.File]::Replace($temporaryConfig, $config, $replacementBackup)
        Remove-Item -LiteralPath $replacementBackup -Force
        $registrationChanged = $true
    }
    catch {
        if (Test-Path -LiteralPath $temporaryConfig) {
            Remove-Item -LiteralPath $temporaryConfig -Force
        }
        if (Test-Path -LiteralPath $replacementBackup) {
            Remove-Item -LiteralPath $replacementBackup -Force
        }
        [IO.File]::Copy($configBackup, $config, $true)
        throw
    }
}

$receipt = Join-Path $data "receipts\operational-$ReleaseVersion.json"
Write-Utf8NoBom -Path $receipt -Text (([ordered]@{
            schema = 'pegasus.cortex.local-operational-install/1.0'
            releaseVersion = $ReleaseVersion
            installedAtUtc = [DateTime]::UtcNow.ToString('O')
            stableExecutable = $stableExecutable
            immutableVersionRoot = $versionRoot
            centralDatabase = (Join-Path $data 'data\cortex.db')
            legacyProfilesPreserved = (Join-Path $data 'profiles')
            registrationChanged = $registrationChanged
            configBackup = $configBackup
        } | ConvertTo-Json -Depth 5) + "`n")

[pscustomobject]@{
    ReleaseVersion = $ReleaseVersion
    StableExecutable = $stableExecutable
    VersionRoot = $versionRoot
    Selector = $selector
    Receipt = $receipt
    RegistrationChanged = $registrationChanged
    ConfigBackup = $configBackup
}
