[CmdletBinding()]
param(
    [string]$ProjectRoot = '',

    [string]$RuntimeArchivePath =
        'D:\Codex Projects\_packages\External\dotnet-runtime\10.0.10\linux-arm64\dotnet-runtime-10.0.10-linux-arm64.tar.gz',

    [string]$OutputRoot = '',

    [string]$RuntimeVersion = '10.0.10',

    [ValidateSet('pinned-spki-private-ca-loopback', 'trusted-loopback-plaintext')]
    [string]$PostgreSqlSecurityProfile = 'pinned-spki-private-ca-loopback',

    [switch]$InspectSourceSet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# These accepted runtime facts are deliberately internal rather than caller
# parameters. A caller may select another path containing the same exact bytes,
# but cannot authorize a different runtime by supplying a matching hash.
$AcceptedRuntimeVersion = '10.0.10'
$AcceptedRuntimeArchiveName = "dotnet-runtime-$AcceptedRuntimeVersion-linux-arm64.tar.gz"
$AcceptedRuntimeArchiveSha512 =
    '3ea2ac626fdb0f26bb52f02584ae5e68ee88c275e740b34d93918ad380985d0dbfa1632c017658dfddc5fba396114b62748efc11b25897877814a4a4c5022edc'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha512 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Test-FullyQualifiedPath {
    param([Parameter(Mandatory)][string]$Path)

    try {
        if ([string]::IsNullOrWhiteSpace($Path) -or
            -not [IO.Path]::IsPathRooted($Path)) {
            return $false
        }
        $pathRoot = [IO.Path]::GetPathRoot($Path)
        $null = [IO.Path]::GetFullPath($Path)
        # Windows PowerShell 5.1 lacks Path.IsPathFullyQualified. A drive-relative
        # path (`C:child`) or current-drive-rooted path (`\child`) has a root no
        # longer than two characters; drive-absolute and UNC roots are longer.
        return -not [string]::IsNullOrWhiteSpace($pathRoot) -and $pathRoot.Length -gt 2
    }
    catch {
        return $false
    }
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText(
        $Path,
        "$json`n",
        [Text.UTF8Encoding]::new($false))
}

function Get-OrdinalSortedItems {
    param(
        [Parameter(Mandatory)][object[]]$Items,
        [Parameter(Mandatory)][string]$PropertyName
    )

    # Sort-Object follows the process culture. Release manifests are
    # language-neutral contracts, so their path arrays must use the same
    # ordinal ordering as the independent Python verifier.
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

    # Inspect the lexical chain rather than resolving it first. That preserves
    # visibility of a junction/symlink ancestor which would otherwise disappear
    # when its descendant is canonicalized.
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

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootPrefix = "$rootPath\"
    $evidence = @($Files | ForEach-Object {
        $fullPath = [IO.Path]::GetFullPath($_.FullName)
        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "File '$($_.FullName)' is outside '$Root'."
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "File '$fullPath' is not one direct regular file."
        }
        $null = Assert-UnlinkedDirectoryChain -Root $rootPath -Directory $item.Directory.FullName
        $relative = $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
        [pscustomobject][ordered]@{
            path = $relative
            bytes = $item.Length
            sha256 = Get-Sha256 -Path $item.FullName
        }
    })
    return @(Get-OrdinalSortedItems -Items $evidence -PropertyName 'path')
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
        [string[]]$ExcludedDirectoryNames = @(),
        [string[]]$ExcludedExtensions = @()
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $start = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not [string]::Equals($start, $canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $start.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source tree escaped the declared root: '$Directory'."
    }
    $start = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $start
    $startItem = Get-Item -LiteralPath $start -Force
    if (-not $startItem.PSIsContainer -or
        (($startItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Tree root is not one direct directory: '$start'."
    }

    $queue = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $queue.Enqueue([IO.DirectoryInfo]$startItem)
    while ($queue.Count -gt 0) {
        $directoryItem = $queue.Dequeue()
        $null = Assert-UnlinkedDirectoryChain `
            -Root $canonicalRoot `
            -Directory $directoryItem.FullName
        foreach ($item in Get-ChildItem -LiteralPath $directoryItem.FullName -Force) {
            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not [string]::Equals($fullPath, $canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -and
                -not $fullPath.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Tree entry escaped the declared root: '$fullPath'."
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Tree contains a link or reparse point: '$fullPath'."
            }
            if ($item.PSIsContainer) {
                if ($item.Name -notin $ExcludedDirectoryNames) {
                    $queue.Enqueue([IO.DirectoryInfo]$item)
                }
            }
            elseif ($item -is [IO.FileInfo]) {
                if ($item.Extension -notin $ExcludedExtensions) {
                    $files.Add([IO.FileInfo]$item)
                }
            }
            else {
                throw "Tree contains a non-regular filesystem entry: '$fullPath'."
            }
        }
    }
    return @($files)
}

function Get-ValidatedFlatDeliveryFiles {
    param(
        [Parameter(Mandatory)][string]$DeliveryRoot,
        [Parameter(Mandatory)][ValidateRange(1, 64)][int]$ExpectedFileCount
    )

    $deliveryEntries = @(Get-ChildItem -LiteralPath $DeliveryRoot -Force)
    $deliveryFiles = @(
        Get-SafeTreeFiles -Root $DeliveryRoot -Directory $DeliveryRoot
    )
    # Materialize the directory subset before evaluating the Boolean guard.
    # Without the parentheses/assignment, PowerShell pipelines the entire
    # preceding `-or` expression and StrictMode exposes a Boolean here rather
    # than the intended FileSystemInfo entries.
    $deliveryDirectories = @(
        $deliveryEntries | Where-Object { $_ -is [IO.DirectoryInfo] }
    )
    if ($deliveryEntries.Count -ne $ExpectedFileCount -or
        $deliveryFiles.Count -ne $ExpectedFileCount -or
        $deliveryDirectories.Count -ne 0) {
        throw "The staged ARM64 delivery is not the expected flat $ExpectedFileCount-file set."
    }
    return @($deliveryFiles)
}

function Resolve-SafeProjectRoot {
    param([Parameter(Mandatory)][string]$RequestedRoot)

    if (-not (Test-FullyQualifiedPath -Path $RequestedRoot)) {
        throw "Pegasus Cortex project root must be canonical and absolute: '$RequestedRoot'."
    }
    $lexicalTail = $RequestedRoot.Substring([IO.Path]::GetPathRoot($RequestedRoot).Length)
    if ($lexicalTail.Replace('/', '\').Split('\') | Where-Object { $_ -in '.', '..' }) {
        throw "Pegasus Cortex project root contains traversal: '$RequestedRoot'."
    }
    try {
        $canonical = [IO.Path]::GetFullPath($RequestedRoot).TrimEnd('\', '/')
        $rootItem = Get-Item -LiteralPath $canonical -Force
    }
    catch {
        throw "Pegasus Cortex project root is missing: '$RequestedRoot'."
    }
    if (-not $rootItem.PSIsContainer -or
        (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Pegasus Cortex project root is not one direct directory: '$canonical'."
    }
    for ($current = [IO.DirectoryInfo]::new($canonical); $null -ne $current; $current = $current.Parent) {
        if (-not $current.Exists -or
            (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Pegasus Cortex project root crosses a link or reparse point: '$($current.FullName)'."
        }
    }
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
        'src\PegasusCortex.Mcp.Integrated\PegasusCortex.Mcp.Integrated.csproj',
        'src\PegasusCortex.Mcp.Integrated\packages.lock.json',
        'src\PegasusCortex.Service\PegasusCortex.Service.csproj',
        'src\PegasusCortex.Service\packages.lock.json',
        'tools\New-CortexMcpArm64Package.ps1'
    )
    foreach ($relative in $requiredPaths) {
        $null = Assert-SafeSourceLeaf -Root $Root -Path (Join-Path $Root $relative)
    }

    # A tool outside the selected copy may operate on it only when its exact
    # bytes equal the builder that will be retained in that copy's source ledger.
    $executingPath = [IO.Path]::GetFullPath($ExecutingScript)
    $executingItem = Get-Item -LiteralPath $executingPath -Force
    if ($executingItem.PSIsContainer -or -not ($executingItem -is [IO.FileInfo]) -or
        (($executingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The executing ARM64 package builder is not one direct regular file.'
    }
    for ($current = $executingItem.Directory; $null -ne $current; $current = $current.Parent) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The executing ARM64 package builder crosses a link or reparse point: '$($current.FullName)'."
        }
    }
    $rootBuilder = Get-Item -LiteralPath (Join-Path $Root 'tools\New-CortexMcpArm64Package.ps1') -Force
    if ($executingItem.Length -ne $rootBuilder.Length -or
        (Get-Sha256 -Path $executingItem.FullName) -cne (Get-Sha256 -Path $rootBuilder.FullName)) {
        throw 'The executing ARM64 package builder does not match the selected project root.'
    }

    $version = Get-Content -LiteralPath (Join-Path $Root 'VERSION.json') -Raw | ConvertFrom-Json
    $releaseVersion = [string]$version.releaseVersion
    if ($releaseVersion -notmatch '^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') {
        throw "VERSION.json contains an invalid release version '$releaseVersion'."
    }
    [xml]$properties = Get-Content -LiteralPath (Join-Path $Root 'Directory.Version.props') -Raw
    $groups = @(@($properties.Project.PropertyGroup) | Where-Object { $null -ne $_.PegasusReleaseVersion })
    if ($groups.Count -ne 1 -or
        [string]$groups[0].PegasusReleaseVersion -cne $releaseVersion -or
        [string]$groups[0].PegasusNuGetVersion -cne [string]$version.nuGetVersion -or
        [string]$groups[0].PegasusAssemblyVersion -cne [string]$version.assemblyVersion -or
        [string]$groups[0].PegasusFileVersion -cne [string]$version.fileVersion) {
        throw 'VERSION.json and Directory.Version.props do not identify the same release bytes.'
    }

    [xml]$solution = Get-Content -LiteralPath (Join-Path $Root 'PegasusCortex.slnx') -Raw
    $solutionProjects = @($solution.SelectNodes('/Solution//Project'))
    $integratedPath = 'src/PegasusCortex.Mcp.Integrated/PegasusCortex.Mcp.Integrated.csproj'
    $servicePath = 'src/PegasusCortex.Service/PegasusCortex.Service.csproj'
    $integratedNodes = @($solutionProjects | Where-Object {
            ([string]$_.Path).Replace('\', '/') -ceq $integratedPath
        })
    $serviceNodes = @($solutionProjects | Where-Object {
            ([string]$_.Path).Replace('\', '/') -ceq $servicePath
        })
    if ($solution.DocumentElement.Name -cne 'Solution' -or
        $integratedNodes.Count -ne 1 -or $serviceNodes.Count -ne 1) {
        throw 'PegasusCortex.slnx does not identify the integrated MCP and Service projects exactly once.'
    }

    [xml]$integratedProject = Get-Content -LiteralPath (
        Join-Path $Root 'src\PegasusCortex.Mcp.Integrated\PegasusCortex.Mcp.Integrated.csproj') -Raw
    $serviceReferences = @($integratedProject.SelectNodes('/Project/ItemGroup/ProjectReference') |
        Where-Object {
            ([string]$_.Include).Replace('\', '/') -ceq '../PegasusCortex.Service/PegasusCortex.Service.csproj'
        })
    if ($serviceReferences.Count -ne 1) {
        throw 'The integrated MCP project does not reference the Service project exactly once.'
    }
    return $version
}

function Get-BoundSourceInputs {
    param([Parameter(Mandatory)][string]$Root)

    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in @(
        'Directory.Build.props',
        'Directory.Version.props',
        'PegasusCortex.slnx',
        'global.json',
        'NuGet.Config',
        'VERSION.json',
        'deploy\arm64\install_cortex_mcp_arm64.py',
        'deploy\arm64\smoke_cortex_mcp.py',
        'deploy\arm64\run-cortex-mcp',
        'deploy\arm64\run-cortex-migrate',
        'tools\cortex_current.py',
        'tools\New-CortexMcpArm64Package.ps1',
        'tools\Test-CortexMcpArm64Package.py',
        'tools\test_cortex_current.py',
        'tools\tests\test_canonical_timestamp_golden.py',
        'tools\tests\test_cortex_mcp_arm64_package.py',
        'tools\tests\fixtures\cortex_arm64_predecessor_1.2026.07.27.13.20.A.json'
    )) {
        $item = Assert-SafeSourceLeaf -Root $Root -Path (Join-Path $Root $relative)
        $null = $paths.Add($item.FullName.Substring($Root.Length + 1).Replace('\', '/'))
    }

    foreach ($sourceRoot in @(
            'integrations\codex\pegasus-cortex-client',
            'licenses',
            'schemas',
            'src\PegasusCortex.Contracts',
            'src\PegasusCortex.Core',
            'src\PegasusCortex.DefaultLibrary',
            'src\PegasusCortex.LocalTransport',
            'src\PegasusCortex.Mcp',
            'src\PegasusCortex.Mcp.Integrated',
            'src\PegasusCortex.Mcp.Local',
            'src\PegasusCortex.OfflineJournal',
            'src\PegasusCortex.OfflineJournal.Platform.Linux',
            'src\PegasusCortex.Persistence.Sqlite',
            'src\PegasusCortex.Service',
            'src\PegasusCortex.Sync.Client',
            'src\PegasusCortex.Persistence.PostgreSql',
            'tests\PegasusCortex.AgentRetention.Sqlite.Tests',
            'tests\PegasusCortex.AgentRetention.Tests',
            'tests\PegasusCortex.Contracts.Tests',
            'tests\PegasusCortex.Core.Tests',
            'tests\PegasusCortex.DefaultLibrary.Tests',
            'tests\PegasusCortex.LocalTransport.Tests',
            'tests\PegasusCortex.Mcp.Tests',
            'tests\PegasusCortex.Mcp.Integrated.Tests',
            'tests\PegasusCortex.Mcp.Local.Tests',
            'tests\PegasusCortex.OfflineJournal.Tests',
            'tests\PegasusCortex.OfflineJournal.Platform.Linux.Tests',
            'tests\PegasusCortex.Persistence.Sqlite.Tests',
            'tests\PegasusCortex.Service.Tests',
            'tests\PegasusCortex.Sync.Client.Tests',
            'tests\PegasusCortex.Persistence.PostgreSql.IntegrationTests',
            'tests\PegasusCortex.Persistence.PostgreSql.Tests')) {
        Get-SafeTreeFiles `
            -Root $Root `
            -Directory (Join-Path $Root $sourceRoot) `
            -ExcludedDirectoryNames @('bin', 'obj', '__pycache__') `
            -ExcludedExtensions @('.pyc') |
            ForEach-Object {
                $null = $paths.Add($_.FullName.Substring($Root.Length + 1).Replace('\', '/'))
            }
    }

    $orderedPaths = @($paths)
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

function Test-SafeWindowsPathSegment {
    param([Parameter(Mandatory)][string]$Segment)

    if ([string]::IsNullOrWhiteSpace($Segment) -or
        $Segment -in '.', '..' -or
        $Segment.EndsWith('.') -or $Segment.EndsWith(' ') -or
        $Segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        [Text.Encoding]::UTF8.GetByteCount($Segment) -gt 240) {
        return $false
    }
    $baseName = $Segment.Split('.')[0]
    return $baseName -notmatch '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
}

function Assert-GeneratedPathBudget {
    param([Parameter(Mandatory)][string]$Path)

    $canonical = [IO.Path]::GetFullPath($Path)
    if ($canonical.Length -gt 240) {
        throw "Generated path exceeds the 240-character contract: '$canonical'."
    }
    $tail = $canonical.Substring([IO.Path]::GetPathRoot($canonical).Length)
    foreach ($segment in $tail.Split(@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        if (-not (Test-SafeWindowsPathSegment -Segment $segment)) {
            throw "Generated path contains an unsafe segment: '$canonical'."
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
    # The project root itself may have been replaced after initial resolution.
    # Validate it and every filesystem ancestor on each output-boundary check.
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

    if (-not (Test-FullyQualifiedPath -Path $RequestedOutput)) {
        throw 'OutputRoot must be one canonical absolute path.'
    }
    $lexicalTail = $RequestedOutput.Substring([IO.Path]::GetPathRoot($RequestedOutput).Length)
    foreach ($segment in $lexicalTail.Replace('/', '\').Split('\')) {
        if (-not (Test-SafeWindowsPathSegment -Segment $segment)) {
            throw 'OutputRoot contains an unsafe or nonportable path segment.'
        }
    }
    $canonical = Assert-GeneratedPathBudget -Path $RequestedOutput
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $canonical.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputRoot must remain inside the Pegasus Cortex project.'
    }
    if ($canonical.Length -gt 150) {
        throw 'OutputRoot exceeds the bounded ARM64 package path contract.'
    }
    if (Test-Path -LiteralPath $canonical) {
        throw 'The immutable ARM64 package output already exists.'
    }

    $parent = Split-Path -Parent $canonical
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $parent
    return $canonical
}

function Resolve-AcceptedRuntimeArchive {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-FullyQualifiedPath -Path $Path)) {
        throw 'The ARM64 runtime archive path must be canonical and absolute.'
    }
    $canonical = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals($canonical, $Path, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($canonical) -cne $AcceptedRuntimeArchiveName) {
        throw 'The ARM64 runtime archive path or filename is not canonical.'
    }
    $item = Get-Item -LiteralPath $canonical -Force
    if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The ARM64 runtime archive is not one direct regular file.'
    }
    for ($current = $item.Directory; $null -ne $current; $current = $current.Parent) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The ARM64 runtime archive crosses a link or reparse point: '$($current.FullName)'."
        }
    }
    return $item.FullName
}

function Copy-AcceptedRuntimeArchive {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $validatedSource = Resolve-AcceptedRuntimeArchive -Path $Source
    if (-not [string]::Equals($validatedSource, $Source, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The ARM64 runtime archive identity changed before staging.'
    }
    if (Test-Path -LiteralPath $Destination) {
        throw 'The staged ARM64 runtime destination already exists.'
    }
    $sourceStream = $null
    $destinationStream = $null
    try {
        # FileShare.Read prevents a normal writer or delete from racing the one
        # source-to-stage copy. All later hashes and manifest facts use stage only.
        $sourceStream = [IO.File]::Open(
            $validatedSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $sourceLength = $sourceStream.Length
        $destinationStream = [IO.File]::Open(
            $Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $sourceStream.CopyTo($destinationStream)
        $destinationStream.Flush($true)
        if ($sourceStream.Length -ne $sourceLength -or
            $destinationStream.Length -ne $sourceLength) {
            throw 'The ARM64 runtime archive changed or truncated during its one staged copy.'
        }
    }
    finally {
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        if ($null -ne $sourceStream) { $sourceStream.Dispose() }
    }
    $validatedAfterCopy = Resolve-AcceptedRuntimeArchive -Path $Source
    if (-not [string]::Equals($validatedAfterCopy, $validatedSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The ARM64 runtime archive identity changed during staging.'
    }
    $staged = Get-Item -LiteralPath $Destination -Force
    if ($staged.PSIsContainer -or -not ($staged -is [IO.FileInfo]) -or
        (($staged.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The staged ARM64 runtime is not one direct regular file.'
    }
    $sha512 = Get-Sha512 -Path $staged.FullName
    if ($sha512 -cne $AcceptedRuntimeArchiveSha512) {
        throw 'The staged ARM64 .NET runtime does not match its internal accepted SHA-512.'
    }
    return [ordered]@{
        bytes = $staged.Length
        sha256 = Get-Sha256 -Path $staged.FullName
        sha512 = $sha512
    }
}

function Assert-StagedRuntimeEvidence {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Evidence
    )

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        $item.Length -ne [long]$Evidence.bytes -or
        (Get-Sha256 -Path $item.FullName) -cne [string]$Evidence.sha256 -or
        (Get-Sha512 -Path $item.FullName) -cne [string]$Evidence.sha512) {
        throw 'The staged ARM64 runtime changed after its accepted one-time copy.'
    }
}

function Remove-OwnedStage {
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
        [IO.Path]::GetFileName($canonicalStage) -notmatch '^\.arm-[0-9a-f]{32}$' -or
        -not $canonicalStage.StartsWith("$canonicalRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unowned ARM64 package stage: '$canonicalStage'."
    }
    # Fail closed and leave diagnostic residue rather than following an injected
    # link while recursively removing the stage.
    $null = @(Get-SafeTreeFiles -Root $canonicalStage -Directory $canonicalStage)
    $null = Assert-SafeOutputParent -Root $canonicalRoot -Parent $canonicalParent
    $null = Assert-UnlinkedDirectoryChain -Root $canonicalRoot -Directory $canonicalStage
    Remove-Item -LiteralPath $canonicalStage -Recurse -Force
}

$resolvedProjectRoot = Resolve-SafeProjectRoot -RequestedRoot $ProjectRoot
$version = Assert-CortexProjectRootIdentity -Root $resolvedProjectRoot -ExecutingScript $PSCommandPath

# Capture before reading any source-derived build fact. The post-publish replay
# below ensures SDK/version/guide/migration decisions and compiled bytes all
# came from one unchanged source set.
$sourceInputsBeforeBuild = @(Get-BoundSourceInputs -Root $resolvedProjectRoot)
$sourceLedger = (@($sourceInputsBeforeBuild | ForEach-Object {
    "$($_.sha256)  $($_.bytes)  $($_.path)"
}) -join "`n") + "`n"
$sourceSetSha256 = Get-TextSha256 -Value $sourceLedger
if ($InspectSourceSet) {
    [ordered]@{
        schemaVersion = 1
        algorithm = 'sha256-utf8-lines-v1'
        sourceSetSha256 = $sourceSetSha256
        sourceInputs = $sourceInputsBeforeBuild
    } | ConvertTo-Json -Depth 10
    return
}

$globalJsonPath = Join-Path $resolvedProjectRoot 'global.json'
$globalSdk = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$lockedSdkVersion = [string]$globalSdk.sdk.version
if ($lockedSdkVersion -cne '10.0.400' -or
    [string]$globalSdk.sdk.rollForward -cne 'latestPatch' -or
    [bool]$globalSdk.sdk.allowPrerelease) {
    throw 'global.json does not preserve the accepted .NET 10.0.400 latestPatch SDK contract.'
}
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$actualSdkVersion = [string](& $dotnet --version)
$actualSdkVersion = $actualSdkVersion.Trim()
if ($LASTEXITCODE -ne 0 -or
    $actualSdkVersion -notmatch '^10\.0\.4\d{2}$' -or
    [version]$actualSdkVersion -lt [version]$lockedSdkVersion) {
    throw "The active .NET SDK '$actualSdkVersion' does not satisfy the '$lockedSdkVersion' latestPatch policy."
}
if ($RuntimeVersion -cne $AcceptedRuntimeVersion) {
    throw 'The ARM64 runtime input must remain the accepted .NET 10.0.10 archive.'
}

$releaseVersion = [string]$version.releaseVersion
$runtimeArchive = Resolve-AcceptedRuntimeArchive -Path $RuntimeArchivePath

$guidePath = Join-Path $resolvedProjectRoot 'src\PegasusCortex.Mcp\Knowledge\continuity-guide.md'
$guideSha256 = Get-Sha256 -Path $guidePath
$guideContractPath = Join-Path $resolvedProjectRoot 'src\PegasusCortex.Mcp\Knowledge\CortexContinuityGuide.cs'
$guideContract = Get-Content -LiteralPath $guideContractPath -Raw
if ($guideContract.IndexOf($guideSha256, [StringComparison]::Ordinal) -lt 0) {
    throw 'The embedded guide source is not bound to its exact SHA-256 constant.'
}
$guideVersionMatch = [regex]::Match(
    $guideContract,
    'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)";',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $guideVersionMatch.Success) {
    throw 'The embedded guide source does not expose one bounded semantic version.'
}
$guideVersion = $guideVersionMatch.Groups[1].Value

$migrationRoot = Join-Path $resolvedProjectRoot 'src\PegasusCortex.Persistence.PostgreSql\Migrations'
$migrationFiles = @(Get-SafeTreeFiles -Root $resolvedProjectRoot -Directory $migrationRoot |
    Where-Object Extension -eq '.sql' |
    Sort-Object Name)
$migrationIds = @($migrationFiles |
    ForEach-Object {
        $match = [regex]::Match(
            $_.BaseName,
            '^([0-9]{4})_',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) {
            throw "Migration file '$($_.Name)' does not start with a four-digit migration ID."
        }
        $match.Groups[1].Value
    } |
    Sort-Object -Unique)
if ($migrationIds.Count -lt 1) {
    throw 'No PostgreSQL migrations were discovered.'
}
if ($migrationIds -notcontains '0007') {
    throw 'The ARM64 integrated package requires PostgreSQL migration 0007.'
}
$latestMigrationId = $migrationIds[-1]
$latestMigrationFileName = ($migrationFiles |
    Where-Object { $_.Name.StartsWith("$latestMigrationId`_", [StringComparison]::Ordinal) } |
    Select-Object -Last 1).Name
if ([string]::IsNullOrWhiteSpace($latestMigrationFileName)) {
    throw "Latest PostgreSQL migration '$latestMigrationId' has no matching file."
}
$migrationCatalogSource = Get-Content -LiteralPath (
    Join-Path $migrationRoot 'PostgreSqlMigrationCatalog.cs') -Raw
if ($migrationCatalogSource.IndexOf(
        "`"$latestMigrationFileName`"",
        [StringComparison]::Ordinal) -lt 0) {
    throw "The PostgreSQL migration catalog does not reference latest migration '$latestMigrationId'."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $resolvedProjectRoot (
        "artifacts\validation\cortex-mcp-integrated-arm64-$releaseVersion")
}
$resolvedOutputRoot = Resolve-SafeOutputRoot -Root $resolvedProjectRoot -RequestedOutput $OutputRoot
$outputParent = Split-Path -Parent $resolvedOutputRoot
$packageArchiveName = "pegasus-cortex-mcp-$releaseVersion-linux-arm64.tar.gz"
$stage = Join-Path $outputParent ('.arm-' + [Guid]::NewGuid().ToString('N'))
$stageOutput = Join-Path $stage 'o'
$publishRoot = Join-Path $stageOutput 'portable-publish'
$adminPublishRoot = Join-Path $publishRoot 'admin'
$stagedDelivery = Join-Path $stageOutput 'delivery'
$stagedPackageArchivePath = Join-Path $stageOutput $packageArchiveName
$finalDeliveryRoot = Join-Path $resolvedOutputRoot 'delivery'
$finalPortablePublishRoot = Join-Path $resolvedOutputRoot 'portable-publish'
$finalPackageArchivePath = Join-Path $resolvedOutputRoot $packageArchiveName
$budgetProbe = Join-Path $publishRoot (
    'admin\licenses\Microsoft.Extensions-10.0.0-THIRD-PARTY-NOTICES.txt')
$null = Assert-GeneratedPathBudget -Path $budgetProbe
New-Item -ItemType Directory -Path $stage | Out-Null
try {
New-Item -ItemType Directory -Path $stageOutput | Out-Null
New-Item -ItemType Directory -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Path $adminPublishRoot | Out-Null
New-Item -ItemType Directory -Path $stagedDelivery | Out-Null

$project = Join-Path $resolvedProjectRoot 'src\PegasusCortex.Mcp.Integrated\PegasusCortex.Mcp.Integrated.csproj'
$serviceProject = Join-Path $resolvedProjectRoot 'src\PegasusCortex.Service\PegasusCortex.Service.csproj'
& $dotnet restore $project `
    --locked-mode `
    --configfile (Join-Path $resolvedProjectRoot 'NuGet.Config') `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "RID-neutral locked restore failed with exit code $LASTEXITCODE."
}
& $dotnet publish $project `
    -c Release `
    --self-contained false `
    --no-restore `
    --nologo `
    -p:UseAppHost=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishAot=false `
    -p:PublishTrimmed=false `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Portable integrated MCP publish failed with exit code $LASTEXITCODE."
}
& $dotnet publish $serviceProject `
    -c Release `
    --self-contained false `
    --no-restore `
    --nologo `
    -p:UseAppHost=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishAot=false `
    -p:PublishTrimmed=false `
    -o $adminPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Portable migration-administration publish failed with exit code $LASTEXITCODE."
}

$entryAssembly = Join-Path $publishRoot 'PegasusCortex.Mcp.Integrated.dll'
$runtimeConfiguration = Join-Path $publishRoot 'PegasusCortex.Mcp.Integrated.runtimeconfig.json'
foreach ($required in @(
        $entryAssembly,
        $runtimeConfiguration,
        (Join-Path $publishRoot 'PegasusCortex.Mcp.Integrated.deps.json'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Portable integrated MCP publish is missing '$required'."
    }
}
$adminEntryAssembly = Join-Path $adminPublishRoot 'PegasusCortex.Service.dll'
foreach ($required in @(
        $adminEntryAssembly,
        (Join-Path $adminPublishRoot 'PegasusCortex.Service.runtimeconfig.json'),
        (Join-Path $adminPublishRoot 'PegasusCortex.Service.deps.json'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Portable migration-administration publish is missing '$required'."
    }
}
$applicationFiles = @(Get-SafeTreeFiles -Root $publishRoot -Directory $publishRoot)
foreach ($applicationFile in $applicationFiles) {
    $null = Assert-GeneratedPathBudget -Path $applicationFile.FullName
}
if ($applicationFiles | Where-Object Extension -eq '.exe') {
    throw 'Portable integrated MCP publish unexpectedly contains a Windows executable.'
}
$entryAssemblySha256 = Get-Sha256 -Path $entryAssembly

if ($applicationFiles | Where-Object {
        $_.Name.StartsWith('PegasusCortex.Sync.Client', [StringComparison]::Ordinal) -or
        $_.Name.StartsWith('PegasusCortex.Mcp.Local', [StringComparison]::Ordinal)
    }) {
    throw 'The integrated ARM64 application unexpectedly includes a local companion executable.'
}
$applicationInventory = Get-FileEvidence -Root $publishRoot -Files $applicationFiles
$applicationArchiveName = "cortex-mcp-app-$releaseVersion.tar.gz"
$applicationArchivePath = Join-Path $stagedDelivery $applicationArchiveName
$null = Assert-GeneratedPathBudget -Path $applicationArchivePath
& tar.exe -czf $applicationArchivePath -C $publishRoot .
if ($LASTEXITCODE -ne 0) {
    throw "Application archive creation failed with exit code $LASTEXITCODE."
}
$applicationArchiveItem = Get-Item -LiteralPath $applicationArchivePath -Force
if ($applicationArchiveItem.PSIsContainer -or
    -not ($applicationArchiveItem -is [IO.FileInfo]) -or
    (($applicationArchiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The staged application archive is not one direct regular file.'
}

$runtimeArchiveName = $AcceptedRuntimeArchiveName
$stagedRuntimeArchive = Join-Path $stagedDelivery $runtimeArchiveName
$runtimeEvidence = Copy-AcceptedRuntimeArchive `
    -Source $runtimeArchive `
    -Destination $stagedRuntimeArchive

$deployRoot = Join-Path $resolvedProjectRoot 'deploy\arm64'
$packagedToolNames = @(
    'install_cortex_mcp_arm64.py',
    'smoke_cortex_mcp.py',
    'run-cortex-mcp',
    'run-cortex-migrate'
)
$packagedToolEvidence = @{}
foreach ($name in $packagedToolNames) {
    $source = Join-Path $deployRoot $name
    $sourceItem = Assert-SafeSourceLeaf -Root $resolvedProjectRoot -Path $source
    $packagedToolEvidence[$name] = [ordered]@{
        path = "deploy/arm64/$name"
        bytes = $sourceItem.Length
        sha256 = Get-Sha256 -Path $source
    }
    $destination = Join-Path $stagedDelivery $name
    [IO.File]::Copy($source, $destination, $false)
    $destinationItem = Get-Item -LiteralPath $destination -Force
    if ($destinationItem.PSIsContainer -or -not ($destinationItem -is [IO.FileInfo]) -or
        (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Packaged tool '$name' did not materialize as one direct regular file."
    }
    if ((Get-Item -LiteralPath $destination).Length -ne $sourceItem.Length -or
        (Get-Sha256 -Path $destination) -cne $packagedToolEvidence[$name].sha256) {
        throw "Packaged tool '$name' changed during copy."
    }
}

$sourceInputs = $sourceInputsBeforeBuild
foreach ($name in $packagedToolNames) {
    $captured = $packagedToolEvidence[$name]
    $bound = @($sourceInputs | Where-Object { $_.path -ceq $captured.path })
    $source = Join-Path $resolvedProjectRoot $captured.path.Replace('/', '\')
    if ($bound.Count -ne 1 -or
        [long]$bound[0].bytes -ne [long]$captured.bytes -or
        [string]$bound[0].sha256 -cne [string]$captured.sha256 -or
        (Get-Item -LiteralPath $source).Length -ne [long]$captured.bytes -or
        (Get-Sha256 -Path $source) -cne [string]$captured.sha256) {
        throw "Packaged tool '$name' changed while source evidence was captured."
    }
}

$assetNames = @(
    $applicationArchiveName,
    $runtimeArchiveName,
    'install_cortex_mcp_arm64.py',
    'smoke_cortex_mcp.py',
    'run-cortex-mcp',
    'run-cortex-migrate'
)
$assets = @($assetNames | ForEach-Object {
    $path = Join-Path $stagedDelivery $_
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or -not ($item -is [IO.FileInfo]) -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Staged asset '$_' is not one direct regular file."
    }
    [ordered]@{
        name = $_
        bytes = if ($_ -ceq $runtimeArchiveName) { $runtimeEvidence.bytes } else { $item.Length }
        sha256 = if ($_ -ceq $runtimeArchiveName) { $runtimeEvidence.sha256 } else { Get-Sha256 -Path $path }
    }
})

# These exact surfaces are verified again under their separate OS principals
# by the native installer. Sync transport remains an API/client concern and is
# intentionally absent from the normal Codex MCP tool list.
$mainToolSurface = @(
    'cortex_discover',
    'cortex_status',
    'cortex_search',
    'cortex_read',
    'cortex_project_brief',
    'cortex_read_retained_record',
    'cortex_submit_task_snapshot',
    'cortex_submit_work_packet',
    'cortex_submit_work_packet_handoff_receipt',
    'cortex_submit_work_packet_acceptance_receipt',
    'cortex_correlate_project',
    'cortex_submit_proposal',
    'cortex_list_proposals',
    'cortex_change_publication',
    'cortex_review_proposal',
    'cortex_manage_review_grant'
)
$auditorToolSurface = @(
    'cortex_discover',
    'cortex_status',
    'cortex_list_proposals',
    'cortex_submit_audit'
)

$trustedLoopback = $PostgreSqlSecurityProfile -eq 'trusted-loopback-plaintext'
$runtimeVariables = @(
    'PEGASUS_CORTEX_AUTHORITY_FILE',
    'PEGASUS_CORTEX_ROW_KEY_FILE',
    'PEGASUS_CORTEX_RUNTIME_CONNECTION',
    'PEGASUS_CORTEX_RECEIPT_KEY_FILE',
    'PEGASUS_CORTEX_APPLICATION_VERSION',
    'PEGASUS_CORTEX_TLS_POLICY'
)
$migrationVariables = @(
    'PEGASUS_CORTEX_RUNTIME_CONNECTION',
    'PEGASUS_CORTEX_MIGRATION_CONNECTION',
    'PEGASUS_CORTEX_APPLICATION_VERSION',
    'PEGASUS_CORTEX_TLS_POLICY'
)
if (-not $trustedLoopback) {
    $runtimeVariables += @(
        'PEGASUS_CORTEX_ROOT_CA',
        'PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256'
    )
    $migrationVariables += @(
        'PEGASUS_CORTEX_ROOT_CA',
        'PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256'
    )
}
$certificateValidation = if ($trustedLoopback) {
    [ordered]@{
        encryptedTransport = $false
        singleLoopbackHost = $true
        privateCaChain = $false
        certificateLifetime = $false
        serverAuthenticationPurpose = $false
        sanOnlyHostMatch = $false
        wildcardHostMatch = $false
        spkiSha256Pin = $false
        certificateDownloads = $false
        revocationNetworkDependency = $false
        failClosed = $true
    }
}
else {
    [ordered]@{
        encryptedTransport = $true
        singleLoopbackHost = $true
        privateCaChain = $true
        certificateLifetime = $true
        serverAuthenticationPurpose = $true
        sanOnlyHostMatch = $true
        wildcardHostMatch = $false
        spkiSha256Pin = $true
        certificateDownloads = $false
        revocationNetworkDependency = $false
        failClosed = $true
    }
}

$manifest = [ordered]@{
    schemaVersion = 3
    releaseVersion = $releaseVersion
    packageVersion = [string]$version.nuGetVersion
    runtimeIdentifier = 'linux-arm64'
    runtimeVersion = $RuntimeVersion
    transport = 'stdio'
    network = $true
    networkScope = 'loopback-postgresql-only'
    database = $true
    authentication = $true
    writes = $true
    liveServices = $true
    serviceRegistration = $false
    x64MutationAuthorized = $false
    signatureStatus = 'unsigned-user-authorized-local-deployment'
    contractScope = 'integrated-stdio-with-local-postgresql'
    installationRoot = '/opt/pegasus'
    runtimePrincipals = [ordered]@{
        main = 'codexsvc'
        auditor = 'cortexaudit'
    }
    mcpToolSurfaces = [ordered]@{
        main = $mainToolSurface
        auditor = $auditorToolSurface
    }
    sdk = [ordered]@{
        version = $lockedSdkVersion
        actualVersion = $actualSdkVersion
        rollForward = 'latestPatch'
        allowPrerelease = $false
        globalJsonSha256 = Get-Sha256 -Path $globalJsonPath
    }
    publishContract = [ordered]@{
        configuration = 'Release'
        frameworkDependent = $true
        portable = $true
        useAppHost = $false
        singleFile = $false
        readyToRun = $false
        nativeAot = $false
        trimmed = $false
        debugSymbols = $false
    }
    companionSourceContract = [ordered]@{
        providerNeutralTransferSourceBound = $true
        localOperatorSourceBound = $true
        syncClientSourceBound = $true
        syncClientApplicationPublished = $false
        syncClientWatcherAutoStarted = $false
        syncToolsExposedThroughMcp = $false
    }
    configurationContract = [ordered]@{
        runtimeFilePattern = 'config/cortex/releases/{releaseVersion}/cortex-mcp.env'
        migrationFilePattern = 'config/cortex/releases/{releaseVersion}/cortex-migration.env'
        releaseScoped = $true
        runtimeFileMode = '0640'
        migrationFileMode = '0600'
        sharedByPrincipal = $true
        principalSelectedByOperatingSystemIdentity = $true
        secretsIncluded = $false
        requiredRuntimeVariables = $runtimeVariables
        requiredMigrationVariables = $migrationVariables
    }
    application = [ordered]@{
        archiveName = $applicationArchiveName
        profile = 'integrated-postgresql'
        entryAssembly = 'PegasusCortex.Mcp.Integrated.dll'
        entryAssemblySha256 = $entryAssemblySha256
        runtimeConfigurationSha256 = Get-Sha256 -Path $runtimeConfiguration
        administrationEntryAssembly = 'admin/PegasusCortex.Service.dll'
        administrationEntryAssemblySha256 = Get-Sha256 -Path $adminEntryAssembly
        fileCount = $applicationInventory.Count
        files = $applicationInventory
    }
    databaseContract = [ordered]@{
        provider = 'postgresql'
        migrationId = $latestMigrationId
        requiredMigrationId = '0007'
        migrationMode = 'one-shot-separated-role'
        runtimeRoleLeastPrivilege = $true
        migrationCredentialRetained = $false
        tlsPolicy = $PostgreSqlSecurityProfile
        certificateValidation = $certificateValidation
    }
    runtime = [ordered]@{
        archiveName = $runtimeArchiveName
        source = 'local-verified-dotnet-runtime-cache'
        sourceUri = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$RuntimeVersion/$runtimeArchiveName"
        sha256 = $runtimeEvidence.sha256
        sha512 = $runtimeEvidence.sha512
    }
    guide = [ordered]@{
        resourceUri = 'cortex://default-library/guide'
        version = $guideVersion
        sha256 = $guideSha256
        utf8Bytes = (Get-Item -LiteralPath $guidePath).Length
        freshnessState = 'current'
        projectFilesRemainAuthoritative = $true
    }
    sourceInputs = $sourceInputs
    assets = $assets
}
$manifestPath = Join-Path $stagedDelivery 'package-manifest.json'
Write-Utf8Json -Path $manifestPath -Value $manifest
$manifestSha256 = Get-Sha256 -Path $manifestPath

$checksumLines = @($assets | ForEach-Object { "$($_.sha256)  $($_.name)" })
$checksumLines += "$manifestSha256  package-manifest.json"
[IO.File]::WriteAllText(
    (Join-Path $stagedDelivery 'checksums.sha256'),
    ($checksumLines -join "`n") + "`n",
    [Text.ASCIIEncoding]::new())

$deliveryFiles = @(
    Get-ValidatedFlatDeliveryFiles -DeliveryRoot $stagedDelivery -ExpectedFileCount 8
)
foreach ($deliveryFile in $deliveryFiles) {
    $null = Assert-GeneratedPathBudget -Path $deliveryFile.FullName
}
$null = Assert-StagedRuntimeEvidence -Path $stagedRuntimeArchive -Evidence $runtimeEvidence

# Capture again after every source-derived manifest field is materialized, but
# before either immutable output is published from task-owned staging.
$sourceInputsAfterBuild = @(Get-BoundSourceInputs -Root $resolvedProjectRoot)
if (($sourceInputsBeforeBuild | ConvertTo-Json -Depth 10 -Compress) -cne
    ($sourceInputsAfterBuild | ConvertTo-Json -Depth 10 -Compress)) {
    throw 'Bound source inputs changed during restore or publish.'
}

# The outer archive and build result are also completed inside the sibling stage.
& tar.exe -czf $stagedPackageArchivePath -C $stagedDelivery .
if ($LASTEXITCODE -ne 0) {
    throw "Outer package archive creation failed with exit code $LASTEXITCODE."
}
$packageArchiveItem = Get-Item -LiteralPath $stagedPackageArchivePath -Force
if ($packageArchiveItem.PSIsContainer -or -not ($packageArchiveItem -is [IO.FileInfo]) -or
    (($packageArchiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The staged outer package is not one direct regular file.'
}

$result = [ordered]@{
    schemaVersion = 1
    status = 'built'
    releaseVersion = $releaseVersion
    runtimeIdentifier = 'linux-arm64'
    deliveryRoot = $finalDeliveryRoot
    packageArchive = $finalPackageArchivePath
    packageArchiveBytes = $packageArchiveItem.Length
    packageArchiveSha256 = Get-Sha256 -Path $stagedPackageArchivePath
    packageManifest = Join-Path $finalDeliveryRoot 'package-manifest.json'
    packageManifestSha256 = $manifestSha256
    portablePublishRoot = $finalPortablePublishRoot
    entryAssemblySha256 = $entryAssemblySha256
    guideSha256 = $guideSha256
    x64Changed = $false
}
Write-Utf8Json -Path (Join-Path $stageOutput 'build-result.json') -Value $result

# Revalidate the complete staged tree and destination parent immediately before
# the single no-overwrite publication move.
$stageOutputFiles = @(Get-SafeTreeFiles -Root $stageOutput -Directory $stageOutput)
foreach ($stageOutputFile in $stageOutputFiles) {
    $null = Assert-GeneratedPathBudget -Path $stageOutputFile.FullName
}
$null = Assert-StagedRuntimeEvidence -Path $stagedRuntimeArchive -Evidence $runtimeEvidence
$null = Assert-SafeOutputParent -Root $resolvedProjectRoot -Parent $outputParent
if (Test-Path -LiteralPath $resolvedOutputRoot) {
    throw 'The immutable ARM64 package output appeared before commit.'
}
[IO.Directory]::Move($stageOutput, $resolvedOutputRoot)
$result | ConvertTo-Json -Depth 10
}
finally {
    Remove-OwnedStage -Stage $stage -Parent $outputParent -Root $resolvedProjectRoot
}
