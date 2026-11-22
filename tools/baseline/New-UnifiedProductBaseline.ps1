[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CortexRoot,

    [Parameter(Mandatory = $true)]
    [string] $AgentOpsRoot,

    [Parameter(Mandatory = $true)]
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-SafeDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "Expected a directory: $Path"
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Baseline roots cannot be reparse points: $($item.FullName)"
    }
    return $item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-ExcludedRelativePath {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $segments = $RelativePath -split '[\\/]'
    foreach ($segment in $segments) {
        if ($segment -in @('.git', '.vs', 'artifacts', 'bin', 'obj')) {
            return $true
        }
    }

    return $segments.Length -ge 2 -and
        $segments[0] -eq '.cortex' -and
        $segments[1] -eq 'offline'
}

function Get-BaselineFiles {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Role
    )

    $rootPrefix = $Root + [IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force -ErrorAction Stop) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Baseline input contains a reparse file: $($file.FullName)"
        }

        $relative = $file.FullName.Substring($rootPrefix.Length)
        if (Test-ExcludedRelativePath -RelativePath $relative) {
            continue
        }

        [pscustomobject]@{
            Role = $Role
            Root = $Root
            FullName = $file.FullName
            RelativePath = $relative.Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
            Length = $file.Length
            LastWriteTimeUtc = $file.LastWriteTimeUtc
        }
    }
}

$resolvedCortex = Resolve-SafeDirectory -Path $CortexRoot
$resolvedAgentOps = Resolve-SafeDirectory -Path $AgentOpsRoot

$destinationParent = Split-Path -Parent $Destination
if ([string]::IsNullOrWhiteSpace($destinationParent)) {
    throw 'The destination must have an explicit parent directory.'
}

$resolvedDestinationParent = [IO.Path]::GetFullPath($destinationParent)
New-Item -ItemType Directory -Path $resolvedDestinationParent -Force | Out-Null
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (-not $destinationPath.StartsWith(
        $resolvedDestinationParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The destination escaped its declared parent directory.'
}
if (Test-Path -LiteralPath $destinationPath) {
    throw "The baseline destination already exists: $destinationPath"
}

$files = @(
    Get-BaselineFiles -Root $resolvedCortex -Role 'cortex'
    Get-BaselineFiles -Root $resolvedAgentOps -Role 'agentops'
) | Sort-Object Role, RelativePath

if ($files.Count -eq 0) {
    throw 'The baseline contained no admitted files.'
}

New-Item -ItemType Directory -Path $destinationPath -ErrorAction Stop | Out-Null
$zipPath = Join-Path $destinationPath 'source.zip'
$manifestPath = Join-Path $destinationPath 'manifest.json'

$manifestFiles = [Collections.Generic.List[object]]::new()
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $zipStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $true,
        [Text.Encoding]::UTF8)
    try {
        foreach ($file in $files) {
            $entryName = "$($file.Role)/$($file.RelativePath)"
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new([DateTime]$file.LastWriteTimeUtc, [TimeSpan]::Zero)

            $sourceStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $entryStream = $entry.Open()
                try {
                    $sourceStream.CopyTo($entryStream)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
            finally {
                $sourceStream.Dispose()
            }

            $manifestFiles.Add([ordered]@{
                    role = $file.Role
                    path = $file.RelativePath
                    length = $file.Length
                    sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    lastWriteTimeUtc = ([DateTime]$file.LastWriteTimeUtc).ToString('O')
                })
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $zipStream.Dispose()
}

$manifest = [ordered]@{
    schema = 'pegasus-cortex-unified-source-baseline/1.0'
    createdAtUtc = [DateTime]::UtcNow.ToString('O')
    roots = [ordered]@{
        cortex = $resolvedCortex
        agentOps = $resolvedAgentOps
    }
    exclusions = @('.git', '.vs', 'artifacts', 'bin', 'obj', '.cortex/offline')
    fileCount = $manifestFiles.Count
    totalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    archive = [ordered]@{
        file = 'source.zip'
        length = (Get-Item -LiteralPath $zipPath).Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    files = $manifestFiles
}

$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $json + "`n", [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    schema = $manifest.schema
    destination = $destinationPath
    fileCount = $manifest.fileCount
    totalBytes = $manifest.totalBytes
    archiveBytes = $manifest.archive.length
    archiveSha256 = $manifest.archive.sha256
}
