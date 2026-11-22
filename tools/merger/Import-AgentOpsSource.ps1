[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CortexRoot,

    [Parameter(Mandatory = $true)]
    [string] $AgentOpsRoot,

    [Parameter(Mandatory = $true)]
    [string] $BaselineManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SafeDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "Expected a directory: $Path"
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Source import roots cannot be reparse points: $($item.FullName)"
    }
    return $item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-WithinRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The path escaped its admitted root: $Path"
    }
    if ($Path.Length -gt 240) {
        throw "The imported path exceeds the product path budget: $Path"
    }
}

function Test-ExcludedRelativePath {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    foreach ($segment in ($RelativePath -split '[\\/]')) {
        if ($segment -in @('.git', '.vs', 'artifacts', 'bin', 'obj')) {
            return $true
        }
    }
    return $false
}

$resolvedCortex = Resolve-SafeDirectory -Path $CortexRoot
$resolvedAgentOps = Resolve-SafeDirectory -Path $AgentOpsRoot
$manifestItem = Get-Item -LiteralPath $BaselineManifest -Force -ErrorAction Stop
if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The source baseline manifest cannot be a reparse point.'
}

$manifest = Get-Content -LiteralPath $manifestItem.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 8
if ($manifest.schema -ne 'pegasus-cortex-unified-source-baseline/1.0') {
    throw "Unsupported source baseline schema: $($manifest.schema)"
}

$baseline = @{}
foreach ($file in $manifest.files) {
    $normalizedPath = ([string]$file.path).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    $key = "$($file.role)|$normalizedPath"
    if ($baseline.ContainsKey($key)) {
        throw "Duplicate source baseline coordinate: $key"
    }
    $baseline[$key] = $file
}

$directoryMappings = [ordered]@{
    'src/Pegasus.AgentOps.Protocol' = 'src/Pegasus.AgentOps.Protocol'
    'src/Pegasus.AgentOps.Engine' = 'src/Pegasus.AgentOps.Engine'
    'src/Pegasus.AgentOps.ProcessHost' = 'src/Pegasus.AgentOps.ProcessHost'
    'src/Pegasus.AgentOps.Service' = 'src/Pegasus.AgentOps.Service'
    'src/Pegasus.AgentOps.Tray' = 'src/Pegasus.AgentOps.Tray'
    'src/Pegasus.AgentOps.UiHost' = 'src/Pegasus.AgentOps.UiHost'
    'src/pao' = 'src/pao'
    'tests/Pegasus.AgentOps.AutoIt.Tests' = 'tests/Pegasus.AgentOps.AutoIt.Tests'
    'tests/Pegasus.AgentOps.FakeTool' = 'tests/Pegasus.AgentOps.FakeTool'
    'tests/Pegasus.AgentOps.MoveRemove.Tests' = 'tests/Pegasus.AgentOps.MoveRemove.Tests'
    'tests/Pegasus.AgentOps.PathContext.Tests' = 'tests/Pegasus.AgentOps.PathContext.Tests'
    'tests/Pegasus.AgentOps.ProcessNetwork.Tests' = 'tests/Pegasus.AgentOps.ProcessNetwork.Tests'
    'tests/Pegasus.AgentOps.ProcessRunner.Tests' = 'tests/Pegasus.AgentOps.ProcessRunner.Tests'
    'tests/Pegasus.AgentOps.SevenZip.Tests' = 'tests/Pegasus.AgentOps.SevenZip.Tests'
    'tests/Pegasus.AgentOps.Tests' = 'tests/Pegasus.AgentOps.Tests'
    'tests/Pegasus.AgentOps.Text.Tests' = 'tests/Pegasus.AgentOps.Text.Tests'
    'tests/Pegasus.AgentOps.ToolRecipe.Tests' = 'tests/Pegasus.AgentOps.ToolRecipe.Tests'
    'tests/Pegasus.AgentOps.UiHost.Tests' = 'tests/Pegasus.AgentOps.UiHost.Tests'
    'tests/Pegasus.AgentOps.Zip.Tests' = 'tests/Pegasus.AgentOps.Zip.Tests'
    'tests/assets' = 'tests/assets'
    'third_party/autoitx' = 'third_party/autoitx'
    'examples/ui-automation' = 'examples/ui-automation'
}

$fileMappings = [ordered]@{
    'licenses/AutoIt-EULA.txt' = 'licenses/AutoIt-EULA.txt'
    'THIRD-PARTY-NOTICES.txt' = 'licenses/AgentOps-THIRD-PARTY-NOTICES.txt'
    'docs/UI_AUTOMATION.md' = 'docs/execution/UI_AUTOMATION.md'
    'docs/NSIS.md' = 'docs/execution/NSIS.md'
}

$planned = [Collections.Generic.List[object]]::new()
foreach ($mapping in $directoryMappings.GetEnumerator()) {
    $sourceBase = [IO.Path]::GetFullPath((Join-Path $resolvedAgentOps $mapping.Key))
    Assert-WithinRoot -Root $resolvedAgentOps -Path $sourceBase
    if (-not (Test-Path -LiteralPath $sourceBase -PathType Container)) {
        throw "Required AgentOps source directory is absent: $sourceBase"
    }

    $sourcePrefix = $sourceBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceBase -File -Recurse -Force -ErrorAction Stop) {
        $relativeWithinMapping = $sourceFile.FullName.Substring($sourcePrefix.Length)
        if (Test-ExcludedRelativePath -RelativePath $relativeWithinMapping) {
            continue
        }

        $trimCharacters = [char[]]@([char]'/', [IO.Path]::DirectorySeparatorChar)
        $sourceRelative = ($mapping.Key.TrimEnd($trimCharacters) + '/' + $relativeWithinMapping.Replace([IO.Path]::DirectorySeparatorChar, [char]'/'))
        $destinationRelative = ($mapping.Value.TrimEnd($trimCharacters) + '/' + $relativeWithinMapping.Replace([IO.Path]::DirectorySeparatorChar, [char]'/'))
        $destinationFile = [IO.Path]::GetFullPath((Join-Path $resolvedCortex $destinationRelative))
        Assert-WithinRoot -Root $resolvedCortex -Path $destinationFile
        $planned.Add([pscustomobject]@{
                Source = $sourceFile.FullName
                SourceRelative = $sourceRelative
                Destination = $destinationFile
                DestinationRelative = $destinationRelative
                LastWriteTimeUtc = $sourceFile.LastWriteTimeUtc
            })
    }
}

foreach ($mapping in $fileMappings.GetEnumerator()) {
    $sourceFile = [IO.Path]::GetFullPath((Join-Path $resolvedAgentOps $mapping.Key))
    $destinationFile = [IO.Path]::GetFullPath((Join-Path $resolvedCortex $mapping.Value))
    Assert-WithinRoot -Root $resolvedAgentOps -Path $sourceFile
    Assert-WithinRoot -Root $resolvedCortex -Path $destinationFile
    $sourceItem = Get-Item -LiteralPath $sourceFile -Force -ErrorAction Stop
    $planned.Add([pscustomobject]@{
            Source = $sourceItem.FullName
            SourceRelative = $mapping.Key
            Destination = $destinationFile
            DestinationRelative = $mapping.Value
            LastWriteTimeUtc = $sourceItem.LastWriteTimeUtc
        })
}

$duplicates = $planned | Group-Object Destination | Where-Object Count -gt 1
if ($duplicates) {
    throw "The source import has duplicate destinations: $($duplicates.Name -join ', ')"
}

foreach ($file in $planned) {
    if (Test-Path -LiteralPath $file.Destination) {
        throw "The create-only source import destination exists: $($file.Destination)"
    }

    $baselineKey = "agentops|$($file.SourceRelative.Replace([IO.Path]::DirectorySeparatorChar, [char]'/'))"
    if (-not $baseline.ContainsKey($baselineKey)) {
        throw "The source file is absent from the frozen baseline: $baselineKey"
    }

    $actualLength = (Get-Item -LiteralPath $file.Source -Force).Length
    $actualHash = (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $baseline[$baselineKey]
    if ($actualLength -ne [long]$expected.length -or
        $actualHash -ne [string]$expected.sha256) {
        throw "The AgentOps source changed after the baseline: $baselineKey"
    }
}

foreach ($file in $planned) {
    $destinationDirectory = Split-Path -Parent $file.Destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    [IO.File]::Copy($file.Source, $file.Destination, $false)
    [IO.File]::SetLastWriteTimeUtc($file.Destination, [DateTime]$file.LastWriteTimeUtc)

    $sourceHash = (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $file.Destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "The imported source failed exact verification: $($file.DestinationRelative)"
    }
}

[pscustomobject]@{
    schema = 'pegasus-cortex-agentops-source-import/1.0'
    sourceBaseline = $manifest.archive.sha256
    copiedFiles = $planned.Count
    sourceRoot = $resolvedAgentOps
    destinationRoot = $resolvedCortex
    sourceSetupImported = $false
}
