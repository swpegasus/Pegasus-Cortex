[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishRoot,

    [Parameter(Mandatory)]
    [string]$LauncherRoot,

    [Parameter(Mandatory)]
    [string]$ReleaseVersion,

    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$signingModule = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\tools\signing\PegasusSigning.psm1'))
if (-not (Test-Path -LiteralPath $signingModule -PathType Leaf)) {
    $signingModule = Join-Path $PSScriptRoot 'PegasusSigning.psm1'
}
Import-Module $signingModule -Force

function Get-RequiredKnownFolderPath {
    param(
        [Parameter(Mandatory)][Environment+SpecialFolder]$Folder,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $path = [Environment]::GetFolderPath(
        $Folder,
        [Environment+SpecialFolderOption]::DoNotVerify)
    if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) {
        throw "The Windows $DisplayName known folder is unavailable or is not rooted."
    }

    $canonical = [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($canonical) -or -not [IO.Path]::IsPathRooted($canonical)) {
        throw "The Windows $DisplayName known folder did not resolve to a rooted path."
    }
    return $canonical
}

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
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Tree contains a reparse point: '$($item.FullName)'."
        }
    }
}

function Copy-OrdinaryTreeContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    Assert-OrdinaryTree -Path $Source
    Assert-OrdinaryDirectory -Path $Destination
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -ErrorAction Stop
    }
}

function Preserve-OwnedStagingDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent,
        [Parameter(Mandatory)][string]$ExpectedPrefix,
        [Parameter(Mandatory)][string]$FailureSuffix
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $resolved)).TrimEnd('\', '/')
    $expected = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\', '/')
    $leaf = Split-Path -Leaf $resolved
    if (-not [string]::Equals($parent, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) {
        throw "Refusing to preserve an unowned staging path: '$resolved'."
    }
    Assert-OrdinaryTree -Path $resolved
    $preserved = Join-Path $expected ('.failed-' + $leaf.TrimStart('.') + '-' + $FailureSuffix)
    if (Test-Path -LiteralPath $preserved) {
        throw "The failed staging preservation path already exists: '$preserved'."
    }
    [IO.Directory]::Move($resolved, $preserved)
    return $preserved
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Description is missing required property '$Name'."
    }
    return $property.Value
}

function Read-JsonObject {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 -ErrorAction Stop |
            ConvertFrom-Json -Depth 100 -ErrorAction Stop
    }
    catch {
        throw "$Description is malformed."
    }
}

function Get-ReleaseIdentity {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match(
        $Value,
        '^1\.(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<hour>\d{2})\.(?<minute>\d{2})\.(?<revision>[A-D])$')
    if (-not $match.Success) {
        throw 'ReleaseVersion is invalid.'
    }
    $month = [int]::Parse($match.Groups['month'].Value, [Globalization.CultureInfo]::InvariantCulture)
    return [pscustomobject]@{
        ProductVersion = $Value
        FileVersion = ('1.{0}.{1}{2}.{3}{4}' -f `
            $match.Groups['year'].Value,
            $month,
            $match.Groups['day'].Value,
            $match.Groups['hour'].Value,
            $match.Groups['minute'].Value)
    }
}

function Assert-ExecutableVersionIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$ReleaseIdentity,
        [Parameter(Mandatory)][string]$Description
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not ($item -is [IO.FileInfo]) -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "$Description is not an ordinary executable."
    }
    if ([string]$item.VersionInfo.ProductVersion -cne $ReleaseIdentity.ProductVersion -or
        [string]$item.VersionInfo.FileVersion -cne $ReleaseIdentity.FileVersion) {
        throw "$Description version identity does not match the admitted release."
    }
}

function Read-BoundedBundleIdentifier {
    param([Parameter(Mandatory)][IO.BinaryReader]$Reader)

    [long]$length = 0
    $terminated = $false
    for ($index = 0; $index -lt 5; $index++) {
        [int]$octet = $Reader.ReadByte()
        if ($index -eq 4 -and ($octet -band 0xf0) -ne 0) {
            throw 'The .NET single-file bundle identifier length is invalid.'
        }
        $length = $length -bor (
            ([long]($octet -band 0x7f)) -shl (7 * $index))
        if (($octet -band 0x80) -eq 0) {
            $terminated = $true
            break
        }
    }
    if (-not $terminated -or $length -le 0 -or $length -gt 128) {
        throw 'The .NET single-file bundle identifier is outside its bound.'
    }
    $value = $Reader.ReadBytes([int]$length)
    if ($value.Length -ne $length) {
        throw 'The .NET single-file bundle identifier is truncated.'
    }
    return [Text.UTF8Encoding]::new($false, $true).GetString($value)
}

function Assert-SelfContainedSingleFileIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Length -lt 1024 -or $item.Length -gt 67108864) {
        throw "$Description is outside the bounded single-file executable size."
    }

    $stream = $null
    $reader = $null
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $signature = [byte[]](
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
            0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae)
        $latin1 = [Text.Encoding]::Latin1
        $binaryText = $latin1.GetString($bytes)
        $signatureText = $latin1.GetString($signature)
        $signatureOffset = $binaryText.IndexOf($signatureText, [StringComparison]::Ordinal)
        if ($signatureOffset -lt 8 -or
            $binaryText.IndexOf($signatureText, $signatureOffset + 1, [StringComparison]::Ordinal) -ge 0) {
            throw 'The .NET single-file bundle signature is missing or ambiguous.'
        }

        $headerOffset = [BitConverter]::ToInt64($bytes, $signatureOffset - 8)
        if ($headerOffset -lt 40 -or $headerOffset -gt ($bytes.LongLength - 61)) {
            throw 'The .NET single-file bundle header offset is invalid.'
        }

        $stream = [IO.MemoryStream]::new($bytes, $false)
        $reader = [IO.BinaryReader]::new(
            $stream,
            [Text.UTF8Encoding]::new($false, $true),
            $false)
        $stream.Position = $headerOffset
        $major = $reader.ReadUInt32()
        $minor = $reader.ReadUInt32()
        $embeddedFiles = $reader.ReadInt32()
        $bundleId = Read-BoundedBundleIdentifier -Reader $reader
        $dependenciesOffset = $reader.ReadInt64()
        $dependenciesSize = $reader.ReadInt64()
        $runtimeConfigurationOffset = $reader.ReadInt64()
        $runtimeConfigurationSize = $reader.ReadInt64()
        $flags = $reader.ReadUInt64()

        if ($major -ne 6 -or $minor -ne 0 -or
            $embeddedFiles -lt 3 -or $embeddedFiles -gt 4096 -or
            [string]::IsNullOrWhiteSpace($bundleId) -or $bundleId.Length -gt 128 -or
            $flags -ne 0) {
            throw 'The .NET single-file bundle header is outside the supported production shape.'
        }

        foreach ($location in @(
                @($dependenciesOffset, $dependenciesSize, 'dependency manifest'),
                @($runtimeConfigurationOffset, $runtimeConfigurationSize, 'runtime configuration'))) {
            [long]$offset = $location[0]
            [long]$size = $location[1]
            if ($offset -lt 0 -or $size -le 0 -or $size -gt 4194304 -or
                $offset -gt ($bytes.LongLength - $size)) {
                throw "The embedded $($location[2]) location is invalid."
            }
        }

        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $dependenciesText = $strictUtf8.GetString(
            $bytes,
            [int]$dependenciesOffset,
            [int]$dependenciesSize)
        $runtimeConfigurationText = $strictUtf8.GetString(
            $bytes,
            [int]$runtimeConfigurationOffset,
            [int]$runtimeConfigurationSize)
        $dependencies = $dependenciesText | ConvertFrom-Json -Depth 100 -ErrorAction Stop
        $runtimeConfiguration = $runtimeConfigurationText | ConvertFrom-Json -Depth 100 -ErrorAction Stop
    }
    catch {
        throw "$Description is not a valid supported .NET self-contained single-file bundle. $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }

    $runtimeOptions = Get-RequiredJsonProperty $runtimeConfiguration 'runtimeOptions' 'The embedded launcher runtime configuration'
    if ([string](Get-RequiredJsonProperty $runtimeOptions 'tfm' 'The embedded launcher runtime options') -cne 'net10.0') {
        throw 'The embedded launcher runtime configuration is not net10.0.'
    }
    $includedFrameworks = @(Get-RequiredJsonProperty $runtimeOptions 'includedFrameworks' 'The embedded launcher runtime options')
    $coreFramework = @($includedFrameworks | Where-Object {
            [string](Get-RequiredJsonProperty $_ 'name' 'An embedded included framework') -ceq 'Microsoft.NETCore.App' -and
            [string](Get-RequiredJsonProperty $_ 'version' 'An embedded included framework') -match '^10\.'
        })
    if ($coreFramework.Count -ne 1) {
        throw 'The embedded launcher runtime configuration does not bind one included .NET 10 runtime.'
    }
    $runtimeVersion = [string](Get-RequiredJsonProperty $coreFramework[0] 'version' 'The embedded .NET runtime')

    $runtimeTarget = Get-RequiredJsonProperty $dependencies 'runtimeTarget' 'The embedded launcher dependency manifest'
    $runtimeTargetName = [string](Get-RequiredJsonProperty $runtimeTarget 'name' 'The embedded launcher runtime target')
    if ($runtimeTargetName -cne '.NETCoreApp,Version=v10.0/win-x64') {
        throw 'The embedded launcher dependency manifest is not the net10.0 win-x64 target.'
    }
    $targets = Get-RequiredJsonProperty $dependencies 'targets' 'The embedded launcher dependency manifest'
    $target = Get-RequiredJsonProperty $targets $runtimeTargetName 'The embedded launcher dependency targets'
    $runtimePackPrefix = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'
    $runtimePacks = @($target.PSObject.Properties | Where-Object {
            $_.Name.StartsWith($runtimePackPrefix, [StringComparison]::Ordinal)
        })
    if ($runtimePacks.Count -ne 1 -or
        $runtimePacks[0].Name.Substring($runtimePackPrefix.Length) -cne $runtimeVersion) {
        throw 'The embedded launcher dependency manifest does not bind the included win-x64 runtime pack.'
    }
}

function Assert-PackageRuntimeIdentity {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][ValidateSet('application', 'launcher')][string]$ExpectedRole,
        [Parameter(Mandatory)][string]$ExpectedReleaseVersion
    )

    $releaseIdentity = Get-ReleaseIdentity -Value $ExpectedReleaseVersion
    $identity = Get-RequiredJsonProperty $Manifest 'identity' "The $ExpectedRole package manifest"
    $expectedMode = if ($ExpectedRole -ceq 'application') {
        'self-contained-apphost'
    }
    else {
        'self-contained-single-file'
    }
    $expected = [ordered]@{
        entryPoint = 'PegasusCortex.exe'
        productVersion = $releaseIdentity.ProductVersion
        fileVersion = $releaseIdentity.FileVersion
        targetFramework = 'net10.0'
        runtimeIdentifier = 'win-x64'
        deploymentMode = $expectedMode
    }
    foreach ($name in $expected.Keys) {
        if ([string](Get-RequiredJsonProperty $identity $name "The $ExpectedRole package identity") -cne
            [string]$expected[$name]) {
            throw "The $ExpectedRole package identity has invalid '$name'."
        }
    }
    $selfContained = Get-RequiredJsonProperty $identity 'selfContained' "The $ExpectedRole package identity"
    if (-not ($selfContained -is [bool]) -or -not [bool]$selfContained) {
        throw "The $ExpectedRole package is not bound as self-contained."
    }

    $executable = Join-Path $Root 'PegasusCortex.exe'
    Assert-ExecutableVersionIdentity `
        -Path $executable `
        -ReleaseIdentity $releaseIdentity `
        -Description "The $ExpectedRole PegasusCortex.exe"

    if ($ExpectedRole -ceq 'launcher') {
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in @('package-manifest.json', 'package-manifest.p7s', 'PegasusCortex.exe', 'PegasusCortex.pdb')) {
            $null = $allowed.Add($name)
        }
        foreach ($item in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
            if ($item.PSIsContainer) {
                if ($item.Name -cne 'trust' -or
                    (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw 'The launcher package contains an unexpected directory.'
                }
                continue
            }
            if (-not $allowed.Contains($item.Name)) {
                throw 'The launcher package is not the admitted self-contained single-file shape.'
            }
        }
        Assert-SelfContainedSingleFileIdentity `
            -Path $executable `
            -Description 'The launcher PegasusCortex.exe'
        return
    }

    foreach ($required in @(
            'PegasusCortex.dll',
            'PegasusCortex.runtimeconfig.json',
            'PegasusCortex.deps.json',
            'coreclr.dll',
            'hostfxr.dll',
            'hostpolicy.dll')) {
        $path = Join-Path $Root $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "The application package is missing ordinary self-contained runtime file '$required'."
        }
    }

    $runtimeConfiguration = Read-JsonObject `
        -Path (Join-Path $Root 'PegasusCortex.runtimeconfig.json') `
        -Description 'The application runtime configuration'
    $runtimeOptions = Get-RequiredJsonProperty $runtimeConfiguration 'runtimeOptions' 'The application runtime configuration'
    if ([string](Get-RequiredJsonProperty $runtimeOptions 'tfm' 'The application runtime options') -cne 'net10.0') {
        throw 'The application runtime configuration is not net10.0.'
    }
    $includedFrameworks = @(Get-RequiredJsonProperty $runtimeOptions 'includedFrameworks' 'The application runtime options')
    $coreFramework = @($includedFrameworks | Where-Object {
            [string](Get-RequiredJsonProperty $_ 'name' 'An included framework') -ceq 'Microsoft.NETCore.App' -and
            [string](Get-RequiredJsonProperty $_ 'version' 'An included framework') -match '^10\.'
        })
    if ($coreFramework.Count -ne 1) {
        throw 'The application runtime configuration does not bind one included .NET 10 runtime.'
    }
    $runtimeVersion = [string](Get-RequiredJsonProperty $coreFramework[0] 'version' 'The included .NET runtime')
    if ([string](Get-RequiredJsonProperty $identity 'runtimeVersion' 'The application package identity') -cne $runtimeVersion) {
        throw 'The application package identity runtime version does not match its runtime configuration.'
    }

    $dependencies = Read-JsonObject `
        -Path (Join-Path $Root 'PegasusCortex.deps.json') `
        -Description 'The application dependency manifest'
    $runtimeTarget = Get-RequiredJsonProperty $dependencies 'runtimeTarget' 'The application dependency manifest'
    $runtimeTargetName = [string](Get-RequiredJsonProperty $runtimeTarget 'name' 'The application runtime target')
    if ($runtimeTargetName -cne '.NETCoreApp,Version=v10.0/win-x64') {
        throw 'The application dependency manifest is not the net10.0 win-x64 target.'
    }
    $targets = Get-RequiredJsonProperty $dependencies 'targets' 'The application dependency manifest'
    if ($null -eq $targets.PSObject.Properties[$runtimeTargetName]) {
        throw 'The application dependency manifest omits its declared runtime target.'
    }
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CurrentUserEvidence {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw 'The effective Windows user SID is unavailable.'
    }
    return [pscustomobject]@{
        Name = $identity.Name
        Sid = $identity.User.Value
        Elevated = (Test-IsElevated)
    }
}

function Move-FileToPreservation {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    $sourceItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not ($sourceItem -is [IO.FileInfo])) {
        throw "Refusing to preserve a non-regular file: '$Path'."
    }
    Assert-OrdinaryDirectory -Path (Split-Path -Parent $Destination)
    if (Test-Path -LiteralPath $Destination) {
        throw "A preservation destination already exists: '$Destination'."
    }
    [IO.File]::Move($Path, $Destination)
    return $Destination
}

function Copy-FileAtomically {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$FailureRoot,
        [Parameter(Mandatory)][string]$FailureName
    )

    $sourceItem = Get-Item -LiteralPath $Source -Force -ErrorAction Stop
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not ($sourceItem -is [IO.FileInfo])) {
        throw "The atomic copy source is not an ordinary file: '$Source'."
    }
    $destinationParent = Split-Path -Parent $Destination
    Assert-OrdinaryDirectory -Path $destinationParent
    Assert-OrdinaryDirectory -Path $FailureRoot
    $temporary = Join-Path $destinationParent (
        '.' + (Split-Path -Leaf $Destination) + '-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        [IO.File]::Move($temporary, $Destination, $true)
    }
    catch {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            $null = Move-FileToPreservation `
                -Path $temporary `
                -Destination (Join-Path $FailureRoot ($FailureName + '-' + [Guid]::NewGuid().ToString('N')))
        }
        throw
    }
}

function Write-Utf8NoBomAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$FailureRoot,
        [Parameter(Mandatory)][string]$FailureName
    )

    $parent = Split-Path -Parent $Path
    Assert-OrdinaryDirectory -Path $parent
    Assert-OrdinaryDirectory -Path $FailureRoot
    $temporary = Join-Path $parent (
        '.' + (Split-Path -Leaf $Path) + '-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-Utf8NoBom -Path $temporary -Text $Text
        [IO.File]::Move($temporary, $Path, $true)
    }
    catch {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            $null = Move-FileToPreservation `
                -Path $temporary `
                -Destination (Join-Path $FailureRoot ($FailureName + '-' + [Guid]::NewGuid().ToString('N')))
        }
        throw
    }
}

function Read-ValidatedPackage {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][ValidateSet('application', 'launcher')][string]$ExpectedRole,
        [Parameter(Mandatory)][string]$ExpectedReleaseVersion
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    Assert-OrdinaryTree -Path $canonicalRoot
    $manifestPath = Join-Path $canonicalRoot 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The $ExpectedRole package has no package-manifest.json."
    }
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force -ErrorAction Stop
    if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The $ExpectedRole package manifest is a reparse point."
    }
    $signedPackage = Test-PegasusDetachedManifestSignature `
        -PackageRoot $canonicalRoot `
        -ExpectedRole $ExpectedRole `
        -ExpectedReleaseVersion $ExpectedReleaseVersion
    $manifest = $signedPackage.Manifest
    if ([string]$manifest.schema -cne 'pegasus.cortex.local-operational-package/1.2' -or
        [string]$manifest.role -cne $ExpectedRole -or
        [string]$manifest.releaseVersion -cne $ExpectedReleaseVersion -or
        [string]$manifest.channel -cne 'Prod Alpha') {
        throw "The $ExpectedRole package identity is invalid."
    }

    $records = @($manifest.files)
    if ($records.Count -eq 0) {
        throw "The $ExpectedRole package manifest contains no files."
    }
    $expectedPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $null = $expectedPaths.Add('package-manifest.json')
    $null = $expectedPaths.Add('package-manifest.p7s')
    $rootPrefix = "$canonicalRoot\"
    foreach ($record in $records) {
        $relative = [string]$record.path
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.Contains('\', [StringComparison]::Ordinal) -or
            $relative.StartsWith('/', [StringComparison]::Ordinal) -or
            $relative.Contains(':', [StringComparison]::Ordinal)) {
            throw "The $ExpectedRole package manifest contains an unsafe path."
        }
        $segments = $relative.Split('/')
        if ($segments.Count -eq 0 -or
            $segments -contains '' -or
            $segments -contains '.' -or
            $segments -contains '..' -or
            [string]::Equals($relative, 'package-manifest.json', [StringComparison]::OrdinalIgnoreCase) -or
            -not $expectedPaths.Add($relative)) {
            throw "The $ExpectedRole package manifest contains an invalid or duplicate path: '$relative'."
        }

        $path = [IO.Path]::GetFullPath((Join-Path $canonicalRoot ($relative.Replace('/', '\'))))
        if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            ([OperatingSystem]::IsWindows() -and $path.Length -gt 240) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The $ExpectedRole package file is missing, outside its root, or over the path budget: '$relative'."
        }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not ($item -is [IO.FileInfo])) {
            throw "The $ExpectedRole package contains a non-regular file: '$relative'."
        }
        $expectedBytes = 0L
        try {
            $expectedBytes = [Convert]::ToInt64($record.bytes, [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            throw "The $ExpectedRole package contains an invalid length: '$relative'."
        }
        $expectedHash = [string]$record.sha256
        if ($expectedBytes -lt 0 -or
            $item.Length -ne $expectedBytes -or
            $expectedHash -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) {
            throw "The $ExpectedRole package file failed length or SHA-256 verification: '$relative'."
        }
    }

    $actualPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $canonicalRoot -Force -Recurse -File -ErrorAction Stop) {
        $relative = [IO.Path]::GetRelativePath($canonicalRoot, $item.FullName).Replace('\', '/')
        $null = $actualPaths.Add($relative)
    }
    if (-not $actualPaths.SetEquals($expectedPaths)) {
        throw "The $ExpectedRole package inventory differs from its manifest."
    }

    Assert-PackageRuntimeIdentity `
        -Root $canonicalRoot `
        -Manifest $manifest `
        -ExpectedRole $ExpectedRole `
        -ExpectedReleaseVersion $ExpectedReleaseVersion
    Test-PegasusAuthenticodeFiles `
        -PackageRoot $canonicalRoot `
        -Manifest $manifest `
        -Publisher $signedPackage.Trust.Publisher

    [pscustomobject]@{
        Root = $canonicalRoot
        ManifestPath = $manifestPath
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Files = $records
        Identity = $manifest.identity
        Signing = $manifest.signing
        ManifestSignatureSha256 = (Get-FileHash -LiteralPath $signedPackage.SignaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if ($ReleaseVersion -notmatch '^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') {
    throw 'ReleaseVersion is invalid.'
}

$programFiles = Get-RequiredKnownFolderPath `
    -Folder ([Environment+SpecialFolder]::ProgramFiles) `
    -DisplayName 'Program Files'
$localAppData = Get-RequiredKnownFolderPath `
    -Folder ([Environment+SpecialFolder]::LocalApplicationData) `
    -DisplayName 'Local Application Data'
$expectedInstall = [IO.Path]::GetFullPath((Join-Path $programFiles 'Pegasus\Cortex')).TrimEnd('\', '/')
$expectedData = [IO.Path]::GetFullPath((Join-Path $localAppData 'Pegasus\Cortex')).TrimEnd('\', '/')
$requestedInstallRoot = if ($PSBoundParameters.ContainsKey('InstallRoot')) { $InstallRoot } else { $expectedInstall }
$requestedDataRoot = if ($PSBoundParameters.ContainsKey('DataRoot')) { $DataRoot } else { $expectedData }
if ([string]::IsNullOrWhiteSpace($requestedInstallRoot) -or
    -not [IO.Path]::IsPathRooted($requestedInstallRoot)) {
    throw 'InstallRoot must be a non-empty rooted path.'
}
if ([string]::IsNullOrWhiteSpace($requestedDataRoot) -or
    -not [IO.Path]::IsPathRooted($requestedDataRoot)) {
    throw 'DataRoot must be a non-empty rooted path.'
}

$publish = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\', '/')
$launcher = [IO.Path]::GetFullPath($LauncherRoot).TrimEnd('\', '/')
$install = [IO.Path]::GetFullPath($requestedInstallRoot).TrimEnd('\', '/')
$data = [IO.Path]::GetFullPath($requestedDataRoot).TrimEnd('\', '/')
if (-not [string]::Equals($install, $expectedInstall, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The machine application root must be exactly '$expectedInstall'."
}
if (-not [string]::Equals($data, $expectedData, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The per-user mutable-data root must be exactly '$expectedData'."
}
Assert-OrdinaryTree -Path $publish
Assert-OrdinaryTree -Path $launcher
$applicationPackage = Read-ValidatedPackage `
    -Root $publish `
    -ExpectedRole 'application' `
    -ExpectedReleaseVersion $ReleaseVersion
$launcherPackage = Read-ValidatedPackage `
    -Root $launcher `
    -ExpectedRole 'launcher' `
    -ExpectedReleaseVersion $ReleaseVersion
if ([string]$applicationPackage.Signing.publisherThumbprint -cne [string]$launcherPackage.Signing.publisherThumbprint -or
    [string]$applicationPackage.Signing.rootThumbprint -cne [string]$launcherPackage.Signing.rootThumbprint) {
    throw 'The application and launcher packages are not signed by the same pinned Prod Alpha identity.'
}

foreach ($required in @('PegasusCortex.exe', 'PegasusCortex.dll', 'PegasusCortex.runtimeconfig.json')) {
    $path = Join-Path $publish $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "The immutable product publish is missing '$required'."
    }
}
$launcherExecutable = Join-Path $launcher 'PegasusCortex.exe'
if (-not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf) -or
    (((Get-Item -LiteralPath $launcherExecutable -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The stable launcher publish is missing an ordinary PegasusCortex.exe.'
}
$launcherExecutableRecords = @(
    $launcherPackage.Files |
        Where-Object { [string]$_.path -ceq 'PegasusCortex.exe' }
)
if ($launcherExecutableRecords.Count -ne 1) {
    throw 'The launcher package manifest must identify exactly one PegasusCortex.exe.'
}
$launcherExecutableRecord = $launcherExecutableRecords[0]

$installParent = Split-Path -Parent $install
$dataParent = Split-Path -Parent $data
Assert-OrdinaryDirectory -Path $programFiles
Assert-OrdinaryDirectory -Path $localAppData
if (Test-Path -LiteralPath $installParent) {
    Assert-OrdinaryDirectory -Path $installParent
}
if (Test-Path -LiteralPath $install) {
    Assert-OrdinaryTree -Path $install
}
if (Test-Path -LiteralPath $dataParent) {
    Assert-OrdinaryDirectory -Path $dataParent
}
if (Test-Path -LiteralPath $data) {
    Assert-OrdinaryTree -Path $data
}

$userEvidence = Get-CurrentUserEvidence
$plannedStableExecutable = Join-Path $install 'PegasusCortex.exe'
$plannedVersionRoot = Join-Path (Join-Path $install 'versions') $ReleaseVersion
$plannedSelector = Join-Path $data 'config\current-version.json'
if ($PlanOnly) {
    [pscustomobject]@{
        PlanOnly = $true
        RequiresElevation = $true
        CurrentProcessElevated = [bool]$userEvidence.Elevated
        InstallRoot = $install
        DataRoot = $data
        StableExecutable = $plannedStableExecutable
        ImmutableVersionRoot = $plannedVersionRoot
        Selector = $plannedSelector
        WarmupMode = 'explicit-command'
        WarmupCommand = '"' + $plannedStableExecutable + '" warmup'
        StartupRegistrationChanged = $false
        ApplicationPackageManifestSha256 = $applicationPackage.ManifestSha256
        LauncherPackageManifestSha256 = $launcherPackage.ManifestSha256
        ApplicationManifestSignatureSha256 = $applicationPackage.ManifestSignatureSha256
        LauncherManifestSignatureSha256 = $launcherPackage.ManifestSignatureSha256
        PublisherThumbprint = $applicationPackage.Signing.publisherThumbprint
        RootThumbprint = $applicationPackage.Signing.rootThumbprint
        ReleaseChannel = 'Prod Alpha'
        ApplicationIdentity = $applicationPackage.Identity
        LauncherIdentity = $launcherPackage.Identity
        InstallParentWillBeCreated = -not (Test-Path -LiteralPath $installParent)
        DataParentWillBeCreated = -not (Test-Path -LiteralPath $dataParent)
        DataRootWillBeCreated = -not (Test-Path -LiteralPath $data)
        McpRegistrationChanged = $false
    }
    return
}

if (-not $userEvidence.Elevated) {
    throw 'Pegasus Cortex machine application installation requires an elevated PowerShell process.'
}

if (-not (Test-Path -LiteralPath $installParent)) {
    New-Item -ItemType Directory -Path $installParent -ErrorAction Stop | Out-Null
}
Assert-OrdinaryDirectory -Path $installParent
if (-not [string]::Equals(
        (Split-Path -Parent $installParent),
        $programFiles,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Cortex install parent escaped Program Files: '$installParent'."
}

if (-not (Test-Path -LiteralPath $data)) {
    if (-not (Test-Path -LiteralPath $dataParent)) {
        New-Item -ItemType Directory -Path $dataParent -ErrorAction Stop | Out-Null
    }
    Assert-OrdinaryDirectory -Path $dataParent
    if (-not [string]::Equals(
            (Split-Path -Parent $dataParent),
            $localAppData,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Cortex data parent escaped local application data: '$dataParent'."
    }
    New-Item -ItemType Directory -Path $data -ErrorAction Stop | Out-Null
}
Assert-OrdinaryTree -Path $data

$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$legacyPayloadRoot = $null
$versionRoot = Join-Path (Join-Path $install 'versions') $ReleaseVersion
$installExisted = Test-Path -LiteralPath $install
$createdInstallRoot = $false
$createdVersionRoot = $false
$stableExecutable = Join-Path $install 'PegasusCortex.exe'
$launcherBackup = $null
$launcherChanged = $false
$selector = Join-Path $data 'config\current-version.json'
$selectorBackup = $null
$selectorChanged = $false
$backupRoot = Join-Path $data 'backups'
$movedLegacyEntries = [Collections.Generic.List[object]]::new()
$installationResult = $null

try {
if (-not $installExisted) {
    $stage = Join-Path $installParent ('.cortex-v2-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
        $stageVersions = Join-Path $stage 'versions'
        $stageVersion = Join-Path $stageVersions $ReleaseVersion
        New-Item -ItemType Directory -Path $stageVersion -Force -ErrorAction Stop | Out-Null
        Copy-OrdinaryTreeContents -Source $publish -Destination $stageVersion
        $stagedPackage = Read-ValidatedPackage `
            -Root $stageVersion `
            -ExpectedRole 'application' `
            -ExpectedReleaseVersion $ReleaseVersion
        if ($stagedPackage.ManifestSha256 -cne $applicationPackage.ManifestSha256) {
            throw 'The staged application package manifest differs from its admitted source.'
        }
        Copy-Item -LiteralPath $launcherExecutable -Destination (Join-Path $stage 'PegasusCortex.exe') -ErrorAction Stop
        Assert-OrdinaryTree -Path $stage
        [IO.Directory]::Move($stage, $install)
        $createdInstallRoot = $true
        $stage = $null
    }
    catch {
        if ($null -ne $stage -and (Test-Path -LiteralPath $stage)) {
            $null = Preserve-OwnedStagingDirectory `
                -Path $stage `
                -ExpectedParent $installParent `
                -ExpectedPrefix '.cortex-v2-install-' `
                -FailureSuffix $timestamp
        }
        throw
    }
}
else {
    Assert-OrdinaryTree -Path $install
    $versions = Join-Path $install 'versions'
    if (-not (Test-Path -LiteralPath $versions)) {
        New-Item -ItemType Directory -Path $versions -ErrorAction Stop | Out-Null
    }
    Assert-OrdinaryDirectory -Path $versions
    if (-not (Test-Path -LiteralPath $versionRoot)) {
        $stageVersion = Join-Path $versions ('.version-v2-' + [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $stageVersion -ErrorAction Stop | Out-Null
            Copy-OrdinaryTreeContents -Source $publish -Destination $stageVersion
            $stagedPackage = Read-ValidatedPackage `
                -Root $stageVersion `
                -ExpectedRole 'application' `
                -ExpectedReleaseVersion $ReleaseVersion
            if ($stagedPackage.ManifestSha256 -cne $applicationPackage.ManifestSha256) {
                throw 'The staged application package manifest differs from its admitted source.'
            }
            [IO.Directory]::Move($stageVersion, $versionRoot)
            $createdVersionRoot = $true
            $stageVersion = $null
        }
        catch {
            if ($null -ne $stageVersion -and (Test-Path -LiteralPath $stageVersion)) {
                $null = Preserve-OwnedStagingDirectory `
                    -Path $stageVersion `
                    -ExpectedParent $versions `
                    -ExpectedPrefix '.version-v2-' `
                    -FailureSuffix $timestamp
            }
            throw
        }
    }
    else {
        $installedPackage = Read-ValidatedPackage `
            -Root $versionRoot `
            -ExpectedRole 'application' `
            -ExpectedReleaseVersion $ReleaseVersion
        if ($installedPackage.ManifestSha256 -cne $applicationPackage.ManifestSha256) {
            throw 'The immutable release directory already exists with different package bytes.'
        }
    }
}

foreach ($name in @('data', 'config', 'backups', 'logs', 'receipts')) {
    $directory = Join-Path $data $name
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
    }
    Assert-OrdinaryDirectory -Path $directory
}

if ($installExisted -and (Test-Path -LiteralPath $stableExecutable -PathType Leaf)) {
    $stableItem = Get-Item -LiteralPath $stableExecutable -Force
    if (($stableItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The existing stable Cortex executable is a reparse point.'
    }
    $launcherBackup = Join-Path $backupRoot "PegasusCortex-launcher-pre-v2-$timestamp.exe"
    [IO.File]::Copy($stableExecutable, $launcherBackup, $false)
}

if ($installExisted) {
    Copy-FileAtomically `
        -Source $launcherExecutable `
        -Destination $stableExecutable `
        -FailureRoot $backupRoot `
        -FailureName "failed-launcher-copy-$timestamp"
    $launcherChanged = $true

    $stableItem = Get-Item -LiteralPath $stableExecutable -Force -ErrorAction Stop
    if ($stableItem.Length -ne [long]$launcherExecutableRecord.bytes -or
        (Get-FileHash -LiteralPath $stableExecutable -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            [string]$launcherExecutableRecord.sha256) {
        throw 'The installed stable launcher failed package verification.'
    }

    $allowedStableNames = @('PegasusCortex.exe', 'versions')
    $unexpectedStableEntries = @(
        Get-ChildItem -LiteralPath $install -Force -ErrorAction Stop |
            Where-Object { $allowedStableNames -notcontains $_.Name }
    )
    if ($unexpectedStableEntries.Count -gt 0) {
        $legacyPayloadRoot = Join-Path $versions ('.legacy-stable-' + $timestamp)
        New-Item -ItemType Directory -Path $legacyPayloadRoot -ErrorAction Stop | Out-Null
        try {
            foreach ($entry in $unexpectedStableEntries) {
                $destination = Join-Path $legacyPayloadRoot $entry.Name
                Move-Item -LiteralPath $entry.FullName -Destination $destination -ErrorAction Stop
                $movedLegacyEntries.Add([pscustomobject]@{
                        Source = $entry.FullName
                        Destination = $destination
                    })
            }
            Assert-OrdinaryTree -Path $legacyPayloadRoot
        }
        catch {
            for ($index = $movedLegacyEntries.Count - 1; $index -ge 0; $index--) {
                $moved = $movedLegacyEntries[$index]
                if (Test-Path -LiteralPath $moved.Destination) {
                    Move-Item -LiteralPath $moved.Destination -Destination $moved.Source -ErrorAction Stop
                }
            }
            throw
        }
    }
}
else {
    $stableItem = Get-Item -LiteralPath $stableExecutable -Force -ErrorAction Stop
    if ($stableItem.Length -ne [long]$launcherExecutableRecord.bytes -or
        (Get-FileHash -LiteralPath $stableExecutable -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            [string]$launcherExecutableRecord.sha256) {
        throw 'The installed stable launcher failed package verification.'
    }
}

if (Test-Path -LiteralPath $selector -PathType Leaf) {
    $selectorItem = Get-Item -LiteralPath $selector -Force
    if (($selectorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The existing Cortex version selector is a reparse point.'
    }
    $selectorBackup = Join-Path $backupRoot "current-version-pre-v2-$timestamp.json"
    [IO.File]::Copy($selector, $selectorBackup, $false)
}
$selectorText = (([ordered]@{
                SchemaVersion = 1
                ReleaseVersion = $ReleaseVersion
                StoreGeneration = 2
                SelectedAtUtc = [DateTime]::UtcNow.ToString('O')
            } | ConvertTo-Json -Compress) + "`n")
Write-Utf8NoBomAtomically `
    -Path $selector `
    -Text $selectorText `
    -FailureRoot $backupRoot `
    -FailureName "failed-selector-write-$timestamp"
$selectorChanged = $true

$v1Database = Join-Path $data 'data\cortex.db'
$v2Database = Join-Path $data 'data\cortex-v2.db'
$preservedPerUserApplication = [IO.Path]::GetFullPath(
    (Join-Path $localAppData 'Programs\Pegasus\Cortex')).TrimEnd('\', '/')
$receipt = Join-Path $data "receipts\operational-v2-$ReleaseVersion.json"
$receiptText = (([ordered]@{
            schema = 'pegasus.cortex.local-operational-install/2.0'
            releaseVersion = $ReleaseVersion
            installedAtUtc = [DateTime]::UtcNow.ToString('O')
            stableExecutable = $stableExecutable
            stableLauncherBytes = (Get-Item -LiteralPath $stableExecutable).Length
            stableLauncherSha256 = (Get-FileHash -LiteralPath $stableExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
            applicationPackageManifestSha256 = $applicationPackage.ManifestSha256
            launcherPackageManifestSha256 = $launcherPackage.ManifestSha256
            applicationManifestSignatureSha256 = $applicationPackage.ManifestSignatureSha256
            launcherManifestSignatureSha256 = $launcherPackage.ManifestSignatureSha256
            releaseChannel = 'Prod Alpha'
            publisherThumbprint = $applicationPackage.Signing.publisherThumbprint
            rootThumbprint = $applicationPackage.Signing.rootThumbprint
            applicationIdentity = $applicationPackage.Identity
            launcherIdentity = $launcherPackage.Identity
            immutableVersionRoot = $versionRoot
            selector = $selector
            selectorBackup = $selectorBackup
            launcherBackup = $launcherBackup
            machineApplication = $true
            elevatedIdentity = $userEvidence.Name
            elevatedUserSid = $userEvidence.Sid
            warmupMode = 'explicit-command'
            warmupCommand = '"' + $stableExecutable + '" warmup'
            startupRegistrationChanged = $false
            preservedLegacyPayload = $legacyPayloadRoot
            preservedPerUserApplication = $preservedPerUserApplication
            v2Database = $v2Database
            v1DatabasePreserved = $v1Database
            legacyProfilesPreserved = (Join-Path $data 'profiles')
            registrationChanged = $false
        } | ConvertTo-Json -Depth 5) + "`n")

$installationResult = [pscustomobject]@{
    ReleaseVersion = $ReleaseVersion
    StableExecutable = $stableExecutable
    StableLauncherBytes = (Get-Item -LiteralPath $stableExecutable).Length
    VersionRoot = $versionRoot
    Selector = $selector
    Receipt = $receipt
    V2Database = $v2Database
    V1DatabasePreserved = $v1Database
    SelectorBackup = $selectorBackup
    LauncherBackup = $launcherBackup
    PreservedLegacyPayload = $legacyPayloadRoot
    WarmupMode = 'explicit-command'
    WarmupCommand = '"' + $stableExecutable + '" warmup'
    StartupRegistrationChanged = $false
}
Write-Utf8NoBomAtomically `
    -Path $receipt `
    -Text $receiptText `
    -FailureRoot $backupRoot `
    -FailureName "failed-install-receipt-$timestamp"
}
catch {
    $failure = $_
    $rollbackFailures = [Collections.Generic.List[Exception]]::new()

    if ($selectorChanged) {
        try {
            if ($null -ne $selectorBackup) {
                Copy-FileAtomically `
                    -Source $selectorBackup `
                    -Destination $selector `
                    -FailureRoot $backupRoot `
                    -FailureName "failed-selector-rollback-$timestamp"
            }
            elseif (Test-Path -LiteralPath $selector -PathType Leaf) {
                $null = Move-FileToPreservation `
                    -Path $selector `
                    -Destination (Join-Path $backupRoot "failed-current-version-$timestamp.json")
            }
        }
        catch {
            $rollbackFailures.Add($_.Exception)
        }
    }

    if ($launcherChanged) {
        try {
            if ($null -ne $launcherBackup) {
                Copy-FileAtomically `
                    -Source $launcherBackup `
                    -Destination $stableExecutable `
                    -FailureRoot $backupRoot `
                    -FailureName "failed-launcher-rollback-$timestamp"
            }
            elseif (Test-Path -LiteralPath $stableExecutable -PathType Leaf) {
                $null = Move-FileToPreservation `
                    -Path $stableExecutable `
                    -Destination (Join-Path $backupRoot "failed-PegasusCortex-launcher-$timestamp.exe")
            }
        }
        catch {
            $rollbackFailures.Add($_.Exception)
        }
    }

    if ($movedLegacyEntries.Count -gt 0) {
        try {
            for ($index = $movedLegacyEntries.Count - 1; $index -ge 0; $index--) {
                $moved = $movedLegacyEntries[$index]
                if (Test-Path -LiteralPath $moved.Destination) {
                    if (Test-Path -LiteralPath $moved.Source) {
                        throw "A legacy stable entry rollback target already exists: '$($moved.Source)'."
                    }
                    Move-Item -LiteralPath $moved.Destination -Destination $moved.Source -ErrorAction Stop
                }
            }
        }
        catch {
            $rollbackFailures.Add($_.Exception)
        }
    }

    try {
        if ($createdInstallRoot -and (Test-Path -LiteralPath $install)) {
            $null = Preserve-OwnedStagingDirectory `
                -Path $install `
                -ExpectedParent $installParent `
                -ExpectedPrefix 'Cortex' `
                -FailureSuffix $timestamp
        }
        elseif ($createdVersionRoot -and (Test-Path -LiteralPath $versionRoot)) {
            $null = Preserve-OwnedStagingDirectory `
                -Path $versionRoot `
                -ExpectedParent (Join-Path $install 'versions') `
                -ExpectedPrefix $ReleaseVersion `
                -FailureSuffix $timestamp
        }
    }
    catch {
        $rollbackFailures.Add($_.Exception)
    }

    if ($rollbackFailures.Count -gt 0) {
        $exceptions = [Collections.Generic.List[Exception]]::new()
        $exceptions.Add($failure.Exception)
        foreach ($rollbackFailure in $rollbackFailures) {
            $exceptions.Add($rollbackFailure)
        }
        throw [AggregateException]::new(
            'Pegasus Cortex installation failed and rollback was incomplete.',
            $exceptions)
    }
    throw $failure
}

$installationResult
