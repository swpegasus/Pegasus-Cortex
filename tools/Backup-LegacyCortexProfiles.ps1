[CmdletBinding()]
param(
    [string]$SourceRoot = "$env:LOCALAPPDATA\Pegasus\Cortex\profiles",
    [string]$BackupParent = "$env:LOCALAPPDATA\Pegasus\Cortex\backups",
    [string]$BackupName = 'legacy-profiles-20260811'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRootPath = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$backupParentPath = [IO.Path]::GetFullPath($BackupParent).TrimEnd('\', '/')
$destination = Join-Path $backupParentPath $BackupName
if (Test-Path -LiteralPath $destination) {
    throw "Backup destination already exists: '$destination'."
}

$backupParentItem = Get-Item -LiteralPath $backupParentPath -Force
if (-not $backupParentItem.PSIsContainer -or
    (($backupParentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The backup parent is not an ordinary directory.'
}

$profiles = @(
    'cortex-CortexMCP-e74c146a',
    'cortex-neural-commander-f0388db0'
)
$stage = Join-Path $backupParentPath ('.legacy-profile-stage-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($profile in $profiles) {
        $source = Join-Path $sourceRootPath $profile
        $sourceItem = Get-Item -LiteralPath $source -Force
        if (-not $sourceItem.PSIsContainer -or
            (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Legacy profile is not an ordinary directory: '$source'."
        }
        $target = Join-Path $stage $profile
        New-Item -ItemType Directory -Path $target | Out-Null
        foreach ($name in @('cortex.db', 'projects.json')) {
            $file = Join-Path $source $name
            $item = Get-Item -LiteralPath $file -Force
            if ($item.PSIsContainer -or
                (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Legacy profile file is unsafe: '$file'."
            }
            Copy-Item -LiteralPath $file -Destination (Join-Path $target $name)
        }
    }
    [IO.Directory]::Move($stage, $destination)
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

Get-ChildItem -LiteralPath $destination -Recurse -File |
    Select-Object FullName, Length, LastWriteTime
