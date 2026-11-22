[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$OutputRoot = '',
    [string]$DotNetPath = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$PythonPath = 'python.exe',
    [switch]$InspectSourceSet
)

# RETIRED ENTRY POINT
#
# The implementation below is retained as historical evidence only. This
# unconditional boundary must remain above every source resolution, directory
# creation, restore, publish, staging, archive, and commit operation.
throw [InvalidOperationException]::new(
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The framework-dependent PegasusCortex.Mcp.Local.dll/profile package route is superseded and made no changes. Use deploy/x64/New-PegasusCortexV2PackageManifest.ps1 and deploy/x64/Install-PegasusCortexV2X64.ps1.')

# HISTORICAL IMPLEMENTATION BELOW (UNREACHABLE)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $algorithm.Dispose() }
}

function Write-Utf8Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($Path, "$json`n", [Text.UTF8Encoding]::new($false))
}

function Get-OrdinalSortedItems {
    param(
        [Parameter(Mandatory)][object[]]$Items,
        [Parameter(Mandatory)][string]$PropertyName
    )

    # Sort-Object follows the process culture. Release manifests and archives
    # are language-neutral contracts, so punctuation such as '-' versus '.'
    # must use the same ordinal ordering as the independent Python verifier.
    $ordered = [object[]]@($Items)
    $keyProperty = $PropertyName
    $comparer = [Collections.Generic.Comparer[object]]::Create(
        [Comparison[object]]{
            param($left, $right)
            $leftProperty = $left.PSObject.Properties[$keyProperty]
            $rightProperty = $right.PSObject.Properties[$keyProperty]
            if ($null -eq $leftProperty -or $null -eq $rightProperty) {
                throw "Cannot ordinal-sort an item without '$keyProperty'."
            }
            return [string]::CompareOrdinal(
                [string]$leftProperty.Value,
                [string]$rightProperty.Value)
        })
    [Array]::Sort($ordered, $comparer)
    return @($ordered)
}

function Assert-UnlinkedDirectoryChain {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Directory
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $canonicalDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not [string]::Equals(
            $canonicalDirectory,
            $canonicalRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        -not $canonicalDirectory.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory path escaped its declared root: '$Directory'."
    }

    # Inspect the lexical chain. Resolving first would hide a junction or link
    # that redirects one of the declared source or staging ancestors.
    $reachedRoot = $false
    for ($current = [IO.DirectoryInfo]::new($canonicalDirectory);
         $null -ne $current;
         $current = $current.Parent) {
        try {
            $item = Get-Item -LiteralPath $current.FullName -Force
        }
        catch {
            throw "Directory chain contains a missing entry: '$($current.FullName)'."
        }
        if (-not $item.PSIsContainer -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Directory chain crosses a link, reparse point, or non-directory: '$($current.FullName)'."
        }
        if ([string]::Equals(
                $current.FullName.TrimEnd('\', '/'),
                $canonicalRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            $reachedRoot = $true
        }
    }
    if (-not $reachedRoot) {
        throw "Directory chain did not reach its declared root: '$canonicalRoot'."
    }
    return $canonicalDirectory
}

function Get-FileEvidence {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][IO.FileInfo[]]$Files
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $prefix = "$canonicalRoot\"
    return @(Get-OrdinalSortedItems -Items @($Files) -PropertyName 'FullName' |
        ForEach-Object {
            $full = [IO.Path]::GetFullPath($_.FullName)
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "File '$($_.FullName)' is outside '$Root'."
            }
            $item = Get-Item -LiteralPath $full -Force
            if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
                (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "File '$full' is not one direct regular file."
            }
            $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $item.Directory.FullName
            [ordered]@{
                path = $full.Substring($prefix.Length).Replace('\', '/')
                bytes = $item.Length
                sha256 = Get-Sha256 -Path $full
            }
        })
}

function Get-Inventory {
    param([Parameter(Mandatory)][string]$Root)

    $files = @(Get-SafeTreeFiles -Root $Root -Directory $Root -IgnoredDirectoryNames @())
    return @(Get-FileEvidence -Root $Root -Files $files)
}

function Assert-InventoryUnchanged {
    param(
        [Parameter(Mandatory)][object[]]$Before,
        [Parameter(Mandatory)][object[]]$After,
        [Parameter(Mandatory)][string]$Operation
    )

    if (($Before | ConvertTo-Json -Depth 10 -Compress) -cne
        ($After | ConvertTo-Json -Depth 10 -Compress)) {
        throw "The staged x64 package tree changed during $Operation."
    }
}

function Assert-SafeSourceLeaf {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $canonical = [IO.Path]::GetFullPath($Path)
    if (-not $canonical.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
        throw "Source leaf escaped the product root or is missing: '$Path'."
    }
    $item = Get-Item -LiteralPath $canonical -Force
    if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Bound source leaf is not one direct regular file: '$canonical'."
    }
    $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $item.Directory.FullName
    return $item
}

function Get-SafeTreeFiles {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Directory,
        [string[]]$IgnoredDirectoryNames = @('bin', 'obj', '__pycache__'),
        [Collections.Generic.List[IO.DirectoryInfo]]$ObservedDirectories
    )
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $start = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not [string]::Equals($start, $canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $start.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source tree escaped the declared root: '$Directory'."
    }
    $start = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $start
    $queue = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
    $startItem = Get-Item -LiteralPath $start -Force
    if (-not $startItem.PSIsContainer -or
        (($startItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Tree root is not one direct directory: '$start'."
    }
    $queue.Enqueue([IO.DirectoryInfo]$startItem)
    if ($null -ne $ObservedDirectories) { $ObservedDirectories.Add([IO.DirectoryInfo]$startItem) }
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    while ($queue.Count -gt 0) {
        $directoryItem = $queue.Dequeue()
        $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $directoryItem.FullName
        foreach ($item in Get-ChildItem -LiteralPath $directoryItem.FullName -Force) {
            $full = [IO.Path]::GetFullPath($item.FullName)
            if (-not [string]::Equals($full, $canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -and
                -not $full.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Tree entry escaped the declared root: '$full'."
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Tree contains a link or reparse point: '$full'."
            }
            if ($item.PSIsContainer) {
                if ($IgnoredDirectoryNames -notcontains $item.Name) {
                    $queue.Enqueue([IO.DirectoryInfo]$item)
                    if ($null -ne $ObservedDirectories) { $ObservedDirectories.Add([IO.DirectoryInfo]$item) }
                }
            }
            elseif ($item -is [IO.FileInfo]) {
                if ($item.Extension -cne '.pyc') { $files.Add([IO.FileInfo]$item) }
            }
            else {
                throw "Tree contains a non-regular filesystem entry: '$full'."
            }
        }
    }
    return @($files)
}

function Get-DeclaredRuntimeAssets {
    param([Parameter(Mandatory)][string]$DepsPath)

    $document = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
    $targetsProperty = $document.PSObject.Properties['targets']
    if ($null -eq $targetsProperty -or $null -eq $targetsProperty.Value) {
        throw 'The RID-neutral publish dependency manifest has no targets object.'
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assets = [Collections.Generic.List[object]]::new()
    foreach ($target in $targetsProperty.Value.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            $runtimeTargetsProperty = $library.Value.PSObject.Properties['runtimeTargets']
            if ($null -eq $runtimeTargetsProperty -or $null -eq $runtimeTargetsProperty.Value) { continue }
            foreach ($asset in $runtimeTargetsProperty.Value.PSObject.Properties) {
                $relative = [string]$asset.Name
                $metadata = $asset.Value
                $ridProperty = $metadata.PSObject.Properties['rid']
                $assetTypeProperty = $metadata.PSObject.Properties['assetType']
                $parts = @($relative.Split('/'))
                if ($relative.IndexOf('\') -ge 0 -or
                    $parts.Count -lt 3 -or
                    $parts[0] -cne 'runtimes' -or
                    $null -eq $ridProperty -or
                    [string]::IsNullOrWhiteSpace([string]$ridProperty.Value) -or
                    $parts[1] -cne [string]$ridProperty.Value -or
                    $null -eq $assetTypeProperty -or
                    [string]::IsNullOrWhiteSpace([string]$assetTypeProperty.Value) -or
                    @($parts | Where-Object {
                            [string]::IsNullOrWhiteSpace($_) -or $_ -in '.', '..' -or
                            $_.Contains(':') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
                            [Text.Encoding]::UTF8.GetByteCount($_) -gt 240
                        }).Count -gt 0) {
                    throw "The RID-neutral publish declares an unsafe runtime asset '$relative'."
                }
                if (-not $seen.Add($relative)) {
                    throw "The RID-neutral publish declares a duplicate runtime asset '$relative'."
                }
                $assets.Add([pscustomobject]@{
                        path = $relative
                        rid = [string]$ridProperty.Value
                    })
            }
        }
    }
    if ($assets.Count -lt 1) {
        throw 'The RID-neutral publish declares no runtime assets.'
    }
    return @(Get-OrdinalSortedItems -Items @($assets) -PropertyName 'path')
}

function Get-ExpectedDirectoryPaths {
    param([Parameter(Mandatory)][object[]]$RelativeFiles)

    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeFile in $RelativeFiles) {
        $parts = @(([string]$relativeFile).Split('/'))
        for ($index = 1; $index -lt $parts.Count; $index++) {
            $null = $directories.Add(($parts[0..($index - 1)] -join '/'))
        }
    }
    $ordered = @($directories)
    [Array]::Sort($ordered, [StringComparer]::OrdinalIgnoreCase)
    return $ordered
}

function Copy-RidFilteredX64Publish {
    param(
        [Parameter(Mandatory)][string]$RawRoot,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(Mandatory)][string]$RuntimeIdentifier
    )

    $raw = [IO.Path]::GetFullPath($RawRoot).TrimEnd('\')
    $destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
    $rawItem = Get-Item -LiteralPath $raw -Force
    $destinationItem = Get-Item -LiteralPath $destination -Force
    if (-not $rawItem.PSIsContainer -or
        (($rawItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        -not $destinationItem.PSIsContainer -or
        (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw 'The raw or filtered x64 publish root is unsafe or non-empty.'
    }

    $topLevelFiles = [Collections.Generic.List[IO.FileInfo]]::new()
    $runtimeDirectory = $null
    foreach ($item in Get-ChildItem -LiteralPath $raw -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The RID-neutral publish contains a link or reparse point: '$($item.FullName)'."
        }
        if ($item.PSIsContainer) {
            if ($item.Name -cne 'runtimes' -or $null -ne $runtimeDirectory) {
                throw "The RID-neutral publish contains an unexpected top-level directory '$($item.Name)'."
            }
            $runtimeDirectory = $item
            continue
        }
        if (-not (Test-Path -LiteralPath $item.FullName -PathType Leaf) -or
            $item.Extension -cnotin '.dll', '.json') {
            throw "The RID-neutral publish contains an unexpected top-level file '$($item.Name)'."
        }
        $topLevelFiles.Add($item)
    }
    if ($null -eq $runtimeDirectory) {
        throw 'The RID-neutral publish has no runtime asset directory.'
    }

    $depsPath = Join-Path $raw 'PegasusCortex.Mcp.Local.deps.json'
    $null = Assert-SafeSourceLeaf -Root $raw -Path $depsPath
    $declaredAssets = @(Get-DeclaredRuntimeAssets -DepsPath $depsPath)
    $runtimeDirectories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    $runtimeFiles = @(Get-SafeTreeFiles -Root $raw -Directory $runtimeDirectory.FullName `
            -IgnoredDirectoryNames @() -ObservedDirectories $runtimeDirectories)
    $actualRuntimePaths = @($runtimeFiles | ForEach-Object {
            $_.FullName.Substring($raw.Length + 1).Replace('\', '/')
        })
    [Array]::Sort($actualRuntimePaths, [StringComparer]::OrdinalIgnoreCase)
    $declaredRuntimePaths = @($declaredAssets | ForEach-Object { [string]$_.path })
    [Array]::Sort($declaredRuntimePaths, [StringComparer]::OrdinalIgnoreCase)
    if (($actualRuntimePaths -join "`n") -cne ($declaredRuntimePaths -join "`n")) {
        throw 'The RID-neutral publish runtime tree differs from its dependency manifest.'
    }

    $actualRuntimeDirectoryPaths = @($runtimeDirectories | ForEach-Object {
            $_.FullName.Substring($raw.Length + 1).Replace('\', '/')
        })
    [Array]::Sort($actualRuntimeDirectoryPaths, [StringComparer]::OrdinalIgnoreCase)
    $declaredRuntimeDirectories = @(Get-ExpectedDirectoryPaths -RelativeFiles $declaredRuntimePaths)
    if (($actualRuntimeDirectoryPaths -join "`n") -cne ($declaredRuntimeDirectories -join "`n")) {
        throw 'The RID-neutral publish contains an unexpected runtime directory.'
    }

    $selectedAssets = @($declaredAssets | Where-Object { [string]$_.rid -ceq $RuntimeIdentifier })
    if ($selectedAssets.Count -lt 1) {
        throw "The RID-neutral publish declares no '$RuntimeIdentifier' runtime assets."
    }
    $expectedEvidence = @{}
    foreach ($file in @(Get-OrdinalSortedItems -Items @($topLevelFiles) -PropertyName 'Name')) {
        $sourceItem = Assert-SafeSourceLeaf -Root $raw -Path $file.FullName
        $expectedEvidence[$file.Name] = [ordered]@{
            bytes = $sourceItem.Length
            sha256 = Get-Sha256 -Path $sourceItem.FullName
        }
        [IO.File]::Copy($sourceItem.FullName, (Join-Path $destination $file.Name), $false)
    }
    foreach ($asset in $selectedAssets) {
        $source = Join-Path $raw ([string]$asset.path).Replace('/', '\')
        $sourceItem = Assert-SafeSourceLeaf -Root $raw -Path $source
        $expectedEvidence[[string]$asset.path] = [ordered]@{
            bytes = $sourceItem.Length
            sha256 = Get-Sha256 -Path $sourceItem.FullName
        }
        $target = Join-Path $destination ([string]$asset.path).Replace('/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [IO.File]::Copy($sourceItem.FullName, $target, $false)
    }

    $packagedFiles = @(Get-SafeTreeFiles -Root (Split-Path -Parent $destination) `
            -Directory $destination -IgnoredDirectoryNames @())
    $packagedRelativeFiles = @($packagedFiles | ForEach-Object {
            $_.FullName.Substring($destination.Length + 1).Replace('\', '/')
        })
    [Array]::Sort($packagedRelativeFiles, [StringComparer]::OrdinalIgnoreCase)
    $expectedPackagedFiles = @($topLevelFiles | ForEach-Object { $_.Name }) +
        @($selectedAssets | ForEach-Object { [string]$_.path })
    [Array]::Sort($expectedPackagedFiles, [StringComparer]::OrdinalIgnoreCase)
    if (($packagedRelativeFiles -join "`n") -cne ($expectedPackagedFiles -join "`n")) {
        throw 'The filtered x64 publish differs from the selected managed and runtime assets.'
    }
    foreach ($packaged in @(Get-FileEvidence -Root $destination -Files $packagedFiles)) {
        $expected = $expectedEvidence[[string]$packaged.path]
        if ($null -eq $expected -or
            [long]$packaged.bytes -ne [long]$expected.bytes -or
            [string]$packaged.sha256 -cne [string]$expected.sha256) {
            throw "The filtered x64 publish changed while '$($packaged.path)' was copied."
        }
    }
}

function Get-BoundSourceInputs {
    param([Parameter(Mandatory)][string]$Root)
    $relativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @(
            'Directory.Build.props',
            'Directory.Version.props',
            'PegasusCortex.slnx',
            'global.json',
            'NuGet.Config',
            'VERSION.json',
            'tools\New-CortexMcpX64Package.ps1',
            'tools\Test-CortexMcpX64Package.py',
            'tools\tests\test_cortex_mcp_x64_package.py')) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Bound source input is missing: '$name'."
        }
        $item = Assert-SafeSourceLeaf -Root $Root -Path $path
        $null = $relativePaths.Add($item.FullName.Substring($Root.Length + 1).Replace('\', '/'))
    }
    foreach ($directory in @('schemas', 'src')) {
        Get-SafeTreeFiles -Root $Root -Directory (Join-Path $Root $directory) |
            ForEach-Object { $null = $relativePaths.Add($_.FullName.Substring($Root.Length + 1).Replace('\', '/')) }
    }
    $orderedPaths = @($relativePaths)
    [Array]::Sort($orderedPaths, [StringComparer]::Ordinal)
    return @($orderedPaths | ForEach-Object {
        $item = Assert-SafeSourceLeaf -Root $Root -Path (Join-Path $Root $_.Replace('/', '\'))
        [ordered]@{
            path = $_
            bytes = $item.Length
            sha256 = Get-Sha256 -Path $item.FullName
        }
    })
}

function Get-ProjectClosure {
    param(
        [Parameter(Mandatory)][string]$EntryProject,
        [Parameter(Mandatory)][string]$Root
    )
    $pending = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending.Enqueue([IO.Path]::GetFullPath($EntryProject))
    while ($pending.Count -gt 0) {
        $project = $pending.Dequeue()
        if (-not $seen.Add($project)) { continue }
        $null = Assert-SafeSourceLeaf -Root $Root -Path $project
        $lock = Join-Path (Split-Path -Parent $project) 'packages.lock.json'
        $null = Assert-SafeSourceLeaf -Root $Root -Path $lock
        [xml]$document = Get-Content -LiteralPath $project -Raw
        # XPath returns an empty collection for leaf projects. Direct dynamic
        # property traversal throws under StrictMode when an ItemGroup has no
        # ProjectReference member, which made the real package build fail even
        # though source-ledger inspection passed.
        foreach ($reference in @($document.SelectNodes('/Project/ItemGroup/ProjectReference'))) {
            if ($null -eq $reference -or [string]::IsNullOrWhiteSpace([string]$reference.Include)) {
                continue
            }
            $candidate = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $project) ([string]$reference.Include)))
            if (-not $candidate.StartsWith("$([IO.Path]::GetFullPath($Root).TrimEnd('\'))\", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Project reference escaped the product source root: '$candidate'."
            }
            $pending.Enqueue($candidate)
        }
    }
    $prefix = "$([IO.Path]::GetFullPath($Root).TrimEnd('\'))\"
    $orderedProjects = @($seen)
    [Array]::Sort($orderedProjects, [StringComparer]::OrdinalIgnoreCase)
    return @($orderedProjects | ForEach-Object {
        $lock = Join-Path (Split-Path -Parent $_) 'packages.lock.json'
        if (-not $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $lock.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Project dependency escaped the product source root: '$_'."
        }
        [ordered]@{
            project = $_.Substring($prefix.Length).Replace('\', '/')
            projectSha256 = Get-Sha256 -Path $_
            lockFile = $lock.Substring($prefix.Length).Replace('\', '/')
            lockSha256 = Get-Sha256 -Path $lock
        }
    })
}

function Resolve-SafeProjectRoot {
    param([Parameter(Mandatory)][string]$RequestedRoot)

    try {
        # Preserve the caller's lexical path while checking it. Resolve-Path can
        # follow a link before its reparse metadata is inspected.
        $canonical = [IO.Path]::GetFullPath($RequestedRoot).TrimEnd('\', '/')
    }
    catch {
        throw "Pegasus Cortex project root is missing: '$RequestedRoot'."
    }
    $rootItem = Get-Item -LiteralPath $canonical -Force
    if (-not $rootItem.PSIsContainer -or
        (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Pegasus Cortex project root is not one direct directory: '$canonical'."
    }

    # The root itself is not sufficient: a linked ancestor could redirect the
    # complete extracted source tree after its content was inspected.
    for ($current = [IO.DirectoryInfo]::new($canonical); $null -ne $current; $current = $current.Parent) {
        if (-not $current.Exists -or
            (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Pegasus Cortex project root crosses a link or reparse point: '$($current.FullName)'."
        }
    }
    return $canonical
}

function Assert-SafeOutputParent {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Parent
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $canonicalParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if (-not [string]::Equals($canonicalParent, $canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $canonicalParent.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputRoot parent escaped the Pegasus Cortex project.'
    }

    # Revalidate the project root and all ancestors on every output-boundary
    # check because any one of them could have been replaced after inspection.
    $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $canonicalRoot
    for ($current = [IO.DirectoryInfo]::new($canonicalParent);
         $null -ne $current -and $current.FullName.Length -ge $canonicalRoot.Length;
         $current = $current.Parent) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (-not $item.PSIsContainer -or
                (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "OutputRoot cannot cross a link, reparse point, or non-directory: '$($current.FullName)'."
            }
        }
        if ([string]::Equals(
                $current.FullName.TrimEnd('\', '/'),
                $canonicalRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
    }
    return $canonicalParent
}

function Resolve-SafeOutputRoot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RequestedOutput
    )

    if ([string]::IsNullOrWhiteSpace($RequestedOutput) -or
        -not [IO.Path]::IsPathRooted($RequestedOutput) -or
        [IO.Path]::GetPathRoot($RequestedOutput).Length -le 2) {
        throw 'OutputRoot must be one canonical absolute path.'
    }
    $lexicalTail = $RequestedOutput.Substring([IO.Path]::GetPathRoot($RequestedOutput).Length)
    $unsafeSegments = @($lexicalTail.Replace('/', '\').Split('\') | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -in '.', '..' -or $_.Contains(':') -or
            $_.EndsWith('.') -or $_.EndsWith(' ') -or
            [Text.Encoding]::UTF8.GetByteCount($_) -gt 240
        })
    if ($unsafeSegments.Count -gt 0) {
        throw 'OutputRoot contains an unsafe or nonportable path segment.'
    }

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $canonical = [IO.Path]::GetFullPath($RequestedOutput)
    if (-not $canonical.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputRoot must remain inside the Pegasus Cortex project.'
    }
    if ($canonical.Length -gt 160) {
        throw 'OutputRoot exceeds the bounded local path contract.'
    }
    if (Test-Path -LiteralPath $canonical) {
        throw 'The immutable x64 package output already exists.'
    }

    $parent = Split-Path -Parent $canonical
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $parent
    return $canonical
}

function Assert-CortexProjectRootIdentity {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ExecutingScript
    )

    $requiredPaths = @(
        'VERSION.json',
        'Directory.Version.props',
        'PegasusCortex.slnx',
        'src\PegasusCortex.Mcp.Local\PegasusCortex.Mcp.Local.csproj',
        'src\PegasusCortex.Mcp.Local\packages.lock.json',
        'tools\New-CortexMcpX64Package.ps1'
    )
    foreach ($relative in $requiredPaths) {
        $null = Assert-SafeSourceLeaf -Root $Root -Path (Join-Path $Root $relative)
    }

    # An external copy of this tool may inspect an extracted root only when the
    # root carries the exact same builder bytes that will enter its source
    # ledger. This preserves useful -ProjectRoot portability without allowing a
    # stale or unrelated builder to certify another tree.
    $executingPath = [IO.Path]::GetFullPath($ExecutingScript)
    $executingItem = Get-Item -LiteralPath $executingPath -Force
    if ($executingItem.PSIsContainer -or
        (($executingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The executing x64 package builder is not one direct regular file.'
    }
    for ($current = $executingItem.Directory; $null -ne $current; $current = $current.Parent) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The executing x64 package builder crosses a link or reparse point: '$($current.FullName)'."
        }
    }
    $rootBuilder = Get-Item -LiteralPath (Join-Path $Root 'tools\New-CortexMcpX64Package.ps1') -Force
    if ($executingItem.Length -ne $rootBuilder.Length -or
        (Get-Sha256 -Path $executingItem.FullName) -cne (Get-Sha256 -Path $rootBuilder.FullName)) {
        throw 'The executing x64 package builder does not match the selected project root.'
    }

    $version = Get-Content -LiteralPath (Join-Path $Root 'VERSION.json') -Raw | ConvertFrom-Json
    $releaseVersion = [string]$version.releaseVersion
    if ($releaseVersion -notmatch '^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') {
        throw "VERSION.json contains an invalid releaseVersion '$releaseVersion'."
    }
    [xml]$props = Get-Content -LiteralPath (Join-Path $Root 'Directory.Version.props') -Raw
    $groups = @(@($props.Project.PropertyGroup) | Where-Object { $null -ne $_.PegasusReleaseVersion })
    if ($groups.Count -ne 1 -or
        [string]$groups[0].PegasusReleaseVersion -cne $releaseVersion -or
        [string]$groups[0].PegasusNuGetVersion -cne [string]$version.nuGetVersion -or
        [string]$groups[0].PegasusAssemblyVersion -cne [string]$version.assemblyVersion -or
        [string]$groups[0].PegasusFileVersion -cne [string]$version.fileVersion) {
        throw 'VERSION.json and Directory.Version.props do not identify the same bytes.'
    }

    [xml]$solution = Get-Content -LiteralPath (Join-Path $Root 'PegasusCortex.slnx') -Raw
    $entryPath = 'src/PegasusCortex.Mcp.Local/PegasusCortex.Mcp.Local.csproj'
    $entryNodes = @($solution.SelectNodes('/Solution//Project') | Where-Object {
            ([string]$_.Path).Replace('\', '/') -ceq $entryPath
        })
    if ($solution.DocumentElement.Name -cne 'Solution' -or $entryNodes.Count -ne 1) {
        throw 'PegasusCortex.slnx does not identify the standalone MCP entry project.'
    }
    return $version
}

function Get-X64PackageSchemas {
    param([Parameter(Mandatory)][string]$SourceRoot)

    $schemaRoot = Join-Path $SourceRoot 'schemas'
    $schemas = @(Get-SafeTreeFiles -Root $SourceRoot -Directory $schemaRoot |
        Where-Object {
            [string]::Equals($_.Directory.FullName, $schemaRoot, [StringComparison]::OrdinalIgnoreCase) -and
            $_.Name.EndsWith('.schema.json', [StringComparison]::Ordinal)
        })
    return @(Get-OrdinalSortedItems -Items $schemas -PropertyName 'Name')
}

function Copy-X64PackageContent {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$GuidePath,
        [Parameter(Mandatory)][IO.FileInfo[]]$Schemas,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $destination = Assert-UnlinkedDirectoryChain -Root $DestinationRoot -Directory $DestinationRoot
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw 'The x64 package content destination must begin empty.'
    }
    $guide = Assert-SafeSourceLeaf -Root $SourceRoot -Path $GuidePath
    $expectedEvidence = @{
        'continuity-guide.md' = [ordered]@{
            bytes = $guide.Length
            sha256 = Get-Sha256 -Path $guide.FullName
        }
    }
    [IO.File]::Copy($guide.FullName, (Join-Path $destination 'continuity-guide.md'), $false)

    $schemaDestination = Join-Path $destination 'schemas'
    New-Item -ItemType Directory -Path $schemaDestination | Out-Null
    $null = Assert-UnlinkedDirectoryChain -Root $destination -Directory $schemaDestination
    $seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($schemaInput in @(Get-OrdinalSortedItems -Items @($Schemas) -PropertyName 'Name')) {
        $schema = Assert-SafeSourceLeaf -Root $SourceRoot -Path $schemaInput.FullName
        if (-not $schema.Name.EndsWith('.schema.json', [StringComparison]::Ordinal) -or
            -not $seenNames.Add($schema.Name)) {
            throw "The x64 package content has an invalid or duplicate schema '$($schema.Name)'."
        }
        $relative = "schemas/$($schema.Name)"
        $expectedEvidence[$relative] = [ordered]@{
            bytes = $schema.Length
            sha256 = Get-Sha256 -Path $schema.FullName
        }
        $schemaTarget = Join-Path $schemaDestination $schema.Name
        [IO.File]::Copy($schema.FullName, $schemaTarget, $false)
    }

    $inventory = @(Get-Inventory -Root $destination)
    if ($inventory.Count -ne ($Schemas.Count + 1)) {
        throw 'The staged x64 content set differs from the guide and schema inputs.'
    }
    foreach ($item in $inventory) {
        $expected = $expectedEvidence[[string]$item.path]
        if ($null -eq $expected -or
            [long]$item.bytes -ne [long]$expected.bytes -or
            [string]$item.sha256 -cne [string]$expected.sha256) {
            throw "The staged x64 content changed while '$($item.path)' was copied."
        }
    }
    return $inventory
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$Destination
    )
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $canonicalSource = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
            $prefix = "$canonicalSource\"
            $files = @(Get-SafeTreeFiles -Root $canonicalSource -Directory $canonicalSource `
                    -IgnoredDirectoryNames @())
            foreach ($file in @(Get-OrdinalSortedItems -Items @($files) -PropertyName 'FullName')) {
                $sourceItem = Assert-SafeSourceLeaf -Root $canonicalSource -Path $file.FullName
                $name = $sourceItem.FullName.Substring($prefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($sourceItem.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Remove-OwnedX64Stage {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Root
    )

    $canonicalStage = [IO.Path]::GetFullPath($Stage)
    $canonicalParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $canonicalStage)) { return }
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $canonicalParent
    $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $canonicalStage
    $stageItem = Get-Item -LiteralPath $canonicalStage -Force
    if (-not [string]::Equals(
            (Split-Path -Parent $canonicalStage),
            $canonicalParent,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $stageItem.PSIsContainer -or
        (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        [IO.Path]::GetFileName($canonicalStage) -notmatch '^\.x64-package-stage-[0-9a-f]{32}$' -or
        -not $canonicalStage.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unowned x64 package stage: '$canonicalStage'."
    }

    # Fail closed and retain diagnostic residue rather than allowing recursive
    # deletion to follow a child link injected into task-owned staging.
    $null = @(Get-SafeTreeFiles -Root $canonicalStage -Directory $canonicalStage `
            -IgnoredDirectoryNames @())
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $canonicalParent
    $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $canonicalStage
    Remove-Item -LiteralPath $canonicalStage -Recurse -Force
}

$root = Resolve-SafeProjectRoot -RequestedRoot $ProjectRoot
$version = Assert-CortexProjectRootIdentity -Root $root -ExecutingScript $PSCommandPath
$releaseVersion = [string]$version.releaseVersion

$sourceBefore = @(Get-BoundSourceInputs -Root $root)
$sourceJson = $sourceBefore | ConvertTo-Json -Depth 10 -Compress
$sourceLedger = (@($sourceBefore | ForEach-Object {
    "$($_.sha256)  $($_.bytes)  $($_.path)"
}) -join "`n") + "`n"
$sourceSetSha256 = Get-TextSha256 -Value $sourceLedger
$project = Join-Path $root 'src\PegasusCortex.Mcp.Local\PegasusCortex.Mcp.Local.csproj'
# InspectSourceSet is a release gate, so it must exercise the same complete
# dependency closure that the subsequent package build records.
$projectClosure = @(Get-ProjectClosure -EntryProject $project -Root $root)
if ($InspectSourceSet) {
    [ordered]@{
        schemaVersion = 1
        algorithm = 'sha256-utf8-lines-v1'
        sourceSetSha256 = $sourceSetSha256
        sourceInputs = $sourceBefore
    } | ConvertTo-Json -Depth 10
    return
}

$globalJsonPath = Join-Path $root 'global.json'
$global = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$lockedSdk = [string]$global.sdk.version
if ($lockedSdk -notmatch '^\d+\.\d+\.\d+$' -or
    [string]$global.sdk.rollForward -cne 'disable' -or
    [bool]$global.sdk.allowPrerelease) {
    throw 'global.json does not preserve the accepted locked SDK contract.'
}
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw "dotnet is missing: '$DotNetPath'." }
$actualSdk = [string](& $DotNetPath --version)
if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -cne $lockedSdk) {
    throw "Active SDK '$($actualSdk.Trim())' does not match '$lockedSdk'."
}

$guidePath = Join-Path $root 'src\PegasusCortex.Mcp\Knowledge\continuity-guide.md'
$guideItem = Assert-SafeSourceLeaf -Root $root -Path $guidePath
$guideSha256 = Get-Sha256 -Path $guidePath
$guideContractPath = Join-Path $root 'src\PegasusCortex.Mcp\Knowledge\CortexContinuityGuide.cs'
$null = Assert-SafeSourceLeaf -Root $root -Path $guideContractPath
$guideContract = Get-Content -LiteralPath $guideContractPath -Raw
if ($guideContract.IndexOf($guideSha256, [StringComparison]::Ordinal) -lt 0) {
    throw 'The continuity guide is not bound to its exact embedded hash.'
}
$guideVersionMatch = [regex]::Match($guideContract, 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)";')
if (-not $guideVersionMatch.Success) { throw 'The continuity guide has no bounded semantic version.' }
$guideVersion = [string]$guideVersionMatch.Groups[1].Value

$schemas = @(Get-X64PackageSchemas -SourceRoot $root)
if ($schemas.Count -lt 1) { throw 'No bundled Cortex schemas were found.' }
foreach ($schema in $schemas) {
    $schemaItem = Assert-SafeSourceLeaf -Root $root -Path $schema.FullName
    $null = Get-Content -LiteralPath $schemaItem.FullName -Raw | ConvertFrom-Json
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "artifacts\validation\cortex-mcp-local-x64-$releaseVersion"
}
$output = Resolve-SafeOutputRoot -Root $root -RequestedOutput $OutputRoot
$parent = Split-Path -Parent $output
$stage = Join-Path $parent ('.x64-package-stage-' + [Guid]::NewGuid().ToString('N'))
$stageOutput = Join-Path $stage 'output'
$delivery = Join-Path $stageOutput 'delivery'
$publish = Join-Path $delivery 'app'
$rawPublish = Join-Path $stage 'raw-publish'
$content = Join-Path $delivery 'content'

try {
    $null = Assert-SafeOutputParent -Root $root -Parent $parent
    New-Item -ItemType Directory -Path $stage | Out-Null
    $null = Assert-UnlinkedDirectoryChain -Root $root -Directory $stage
    New-Item -ItemType Directory -Path $stageOutput | Out-Null
    New-Item -ItemType Directory -Path $delivery | Out-Null
    New-Item -ItemType Directory -Path $publish | Out-Null
    New-Item -ItemType Directory -Path $content | Out-Null

    # The pinned offline sources do not carry the .NET 8 win-x64 runtime packs.
    # Restore and publish the framework-dependent graph without a RID, then
    # retain only the dependency-manifest-declared win-x64 native assets.
    & $DotNetPath restore $project --locked-mode --configfile (Join-Path $root 'NuGet.Config') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Locked RID-neutral x64 restore failed with exit code $LASTEXITCODE." }
    & $DotNetPath publish $project -c Release --self-contained false --no-restore --nologo `
        -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false `
        -p:PublishAot=false -p:PublishTrimmed=false -o $rawPublish
    if ($LASTEXITCODE -ne 0) { throw "RID-neutral framework-dependent local MCP publish failed with exit code $LASTEXITCODE." }
    Copy-RidFilteredX64Publish -RawRoot $rawPublish -DestinationRoot $publish -RuntimeIdentifier 'win-x64'

    foreach ($required in @('PegasusCortex.Mcp.Local.dll', 'PegasusCortex.Mcp.Local.deps.json', 'PegasusCortex.Mcp.Local.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
            throw "Portable local MCP publish is missing '$required'."
        }
    }
    $entryAssembly = Join-Path $publish 'PegasusCortex.Mcp.Local.dll'
    $publishFiles = @(Get-SafeTreeFiles -Root $publish -Directory $publish -IgnoredDirectoryNames @())
    if ($publishFiles | Where-Object { $_.Extension -in '.exe', '.pdb' }) {
        throw 'Portable local MCP publish unexpectedly contains an app host or debug symbols.'
    }
    if ($publishFiles | Where-Object {
            $_.FullName.IndexOf('\runtimes\', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.FullName.IndexOf('\runtimes\win-x64\', [StringComparison]::Ordinal) -lt 0
        }) {
        throw 'The win-x64 publish contains a foreign runtime asset.'
    }

    $null = Copy-X64PackageContent -SourceRoot $root -GuidePath $guideItem.FullName `
        -Schemas $schemas -DestinationRoot $content

    # The contract probe executes the staged assembly. Bind the complete staged
    # payload before and after it so a successful probe cannot certify different
    # bytes or an injected filesystem entry.
    $contractInputInventory = @(Get-Inventory -Root $delivery)

    $contractJson = [string](& $PythonPath (Join-Path $root 'tools\Test-CortexMcpX64Package.py') `
        --capture-contract $entryAssembly --dotnet $DotNetPath)
    if ($LASTEXITCODE -ne 0) { throw "Offline x64 contract capture failed with exit code $LASTEXITCODE." }
    $mcpContract = $contractJson | ConvertFrom-Json
    if (-not [bool]$mcpContract.verified -or
        [string]$mcpContract.storageKind -cne 'sqlite' -or
        [bool]$mcpContract.network -or
        [bool]$mcpContract.liveServices -or
        [string]$mcpContract.guideResourceSha256 -cne $guideSha256 -or
        [string]$mcpContract.guideVersion -cne $guideVersion -or
        [int]$mcpContract.instructionsUtf8Bytes -lt 1 -or
        [int]$mcpContract.instructionsUtf8Bytes -gt 512 -or
        [string]$mcpContract.instructionsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$mcpContract.toolDefinitionsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not [bool]$mcpContract.authorityFallbackVerified -or
        -not [bool]$mcpContract.privatePendingProposalVerified) {
        throw 'Offline x64 contract capture did not prove the complete standalone package contract.'
    }
    $contractOutputInventory = @(Get-Inventory -Root $delivery)
    Assert-InventoryUnchanged -Before $contractInputInventory -After $contractOutputInventory `
        -Operation 'offline contract capture'

    $toolSurface = @(
        'cortex_discover', 'cortex_status', 'cortex_search', 'cortex_read',
        'cortex_project_brief', 'cortex_read_retained_record',
        'cortex_submit_task_snapshot', 'cortex_submit_work_packet',
        'cortex_submit_work_packet_handoff_receipt',
        'cortex_submit_work_packet_acceptance_receipt',
        'cortex_submit_proposal', 'cortex_list_proposals')
    $payloadInventory = $contractOutputInventory
    $manifest = [ordered]@{
        schemaVersion = 1
        releaseVersion = $releaseVersion
        packageVersion = [string]$version.nuGetVersion
        runtimeIdentifier = 'win-x64'
        transport = 'stdio'
        storageKind = 'sqlite'
        network = $false
        liveServices = $false
        serviceRegistration = $false
        signatureStatus = 'unsigned-local-validation'
        entryAssembly = 'app/PegasusCortex.Mcp.Local.dll'
        toolSurface = $toolSurface
        mcpContract = $mcpContract
        publishContract = [ordered]@{
            configuration = 'Release'; frameworkDependent = $true; portable = $true
            useAppHost = $false; singleFile = $false; readyToRun = $false
            nativeAot = $false; trimmed = $false; debugSymbols = $false
        }
        sdk = [ordered]@{
            version = $lockedSdk; actualVersion = $actualSdk.Trim()
            rollForward = 'disable'; allowPrerelease = $false
            globalJsonSha256 = Get-Sha256 -Path $globalJsonPath
        }
        dependencyLocks = $projectClosure
        guide = [ordered]@{
            path = 'content/continuity-guide.md'; version = $guideVersion
            sha256 = $guideSha256; resourceUri = 'cortex://default-library/guide'
        }
        schemas = @($schemas | ForEach-Object {
            [ordered]@{ path = "content/schemas/$($_.Name)"; sha256 = Get-Sha256 -Path $_.FullName; bytes = $_.Length }
        })
        sourceSetSha256 = $sourceSetSha256
        sourceInputs = $sourceBefore
        payload = $payloadInventory
    }
    $manifestPath = Join-Path $delivery 'package-manifest.json'
    Write-Utf8Json -Path $manifestPath -Value $manifest
    $checksumLines = @("$(Get-Sha256 -Path $manifestPath)  package-manifest.json")
    $checksumLines += @($payloadInventory | ForEach-Object { "$($_.sha256)  $($_.path)" })
    [IO.File]::WriteAllText((Join-Path $delivery 'checksums.sha256'), (($checksumLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

    $sourceAfter = @(Get-BoundSourceInputs -Root $root)
    if (($sourceAfter | ConvertTo-Json -Depth 10 -Compress) -cne $sourceJson) {
        throw 'Bound source inputs changed during restore or publish.'
    }

    $zipName = "pegasus-cortex-mcp-local-$releaseVersion-win-x64.zip"
    $zipPath = Join-Path $stageOutput $zipName
    $deliveryBeforeArchive = @(Get-Inventory -Root $delivery)
    New-DeterministicZip -SourceRoot $delivery -Destination $zipPath
    $deliveryAfterArchive = @(Get-Inventory -Root $delivery)
    Assert-InventoryUnchanged -Before $deliveryBeforeArchive -After $deliveryAfterArchive `
        -Operation 'deterministic archive creation'
    $result = [ordered]@{
        schemaVersion = 1; releaseVersion = $releaseVersion; runtimeIdentifier = 'win-x64'
        sourceSetSha256 = $sourceSetSha256
        manifestSha256 = Get-Sha256 -Path $manifestPath
        zipName = $zipName; zipBytes = (Get-Item -LiteralPath $zipPath).Length
        zipSha256 = Get-Sha256 -Path $zipPath
        deliveryFileCount = $deliveryAfterArchive.Count
        smokeRequired = $true
        verified = $false
        offlineStdioSmoke = $false
    }
    Write-Utf8Json -Path (Join-Path $stageOutput 'build-result.json') -Value $result
    $null = @(Get-SafeTreeFiles -Root $stageOutput -Directory $stageOutput `
            -IgnoredDirectoryNames @())
    $null = Assert-SafeOutputParent -Root $root -Parent $parent
    if (Test-Path -LiteralPath $output) {
        throw 'The immutable x64 package output appeared before commit.'
    }
    [IO.Directory]::Move($stageOutput, $output)
    $result | ConvertTo-Json -Depth 10
}
finally {
    Remove-OwnedX64Stage -Stage $stage -Parent $parent -Root $root
}
