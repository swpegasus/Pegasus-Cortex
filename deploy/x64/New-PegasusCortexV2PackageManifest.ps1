[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory)]
    [ValidateSet('application', 'launcher', 'installer', 'product', 'product-installer')]
    [string]$Role,

    [Parameter(Mandatory)]
    [string]$PublisherThumbprint,

    [Parameter(Mandatory)]
    [string]$RootThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$signingModule = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\tools\signing\PegasusSigning.psm1'))
Import-Module $signingModule -Force

function Assert-OrdinaryDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Expected an ordinary directory: '$Path'."
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-RequiredProperty {
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
        '^(?<major>[12])\.(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<hour>\d{2})\.(?<minute>\d{2})\.(?<revision>[A-D])$')
    if (-not $match.Success) {
        throw 'ReleaseVersion is invalid.'
    }
    $month = [int]::Parse($match.Groups['month'].Value, [Globalization.CultureInfo]::InvariantCulture)
    $fileVersion = '{0}.{1}.{2}{3}.{4}{5}' -f `
        $match.Groups['major'].Value,
        $match.Groups['year'].Value,
        $month,
        $match.Groups['day'].Value,
        $match.Groups['hour'].Value,
        $match.Groups['minute'].Value
    # Windows PE version resources expose numeric components without leading
    # zeroes (01:55 is component 155), unlike the human release identifier.
    $fileVersion = ([version]$fileVersion).ToString()
    return [pscustomobject]@{
        ProductVersion = $Value
        FileVersion = $fileVersion
    }
}

function Get-ExecutableIdentity {
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
        throw "$Description version identity does not match ReleaseVersion."
    }
    return [ordered]@{
        productVersion = [string]$item.VersionInfo.ProductVersion
        fileVersion = [string]$item.VersionInfo.FileVersion
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

    $runtimeOptions = Get-RequiredProperty $runtimeConfiguration 'runtimeOptions' 'The embedded launcher runtime configuration'
    if ([string](Get-RequiredProperty $runtimeOptions 'tfm' 'The embedded launcher runtime options') -cne 'net10.0') {
        throw 'The embedded launcher runtime configuration is not net10.0.'
    }
    $includedFrameworks = @(Get-RequiredProperty $runtimeOptions 'includedFrameworks' 'The embedded launcher runtime options')
    $coreFramework = @($includedFrameworks | Where-Object {
            [string](Get-RequiredProperty $_ 'name' 'An embedded included framework') -ceq 'Microsoft.NETCore.App' -and
            [string](Get-RequiredProperty $_ 'version' 'An embedded included framework') -match '^10\.'
        })
    if ($coreFramework.Count -ne 1) {
        throw 'The embedded launcher runtime configuration does not bind one included .NET 10 runtime.'
    }
    $runtimeVersion = [string](Get-RequiredProperty $coreFramework[0] 'version' 'The embedded .NET runtime')

    $runtimeTarget = Get-RequiredProperty $dependencies 'runtimeTarget' 'The embedded launcher dependency manifest'
    $runtimeTargetName = [string](Get-RequiredProperty $runtimeTarget 'name' 'The embedded launcher runtime target')
    if ($runtimeTargetName -cne '.NETCoreApp,Version=v10.0/win-x64') {
        throw 'The embedded launcher dependency manifest is not the net10.0 win-x64 target.'
    }
    $targets = Get-RequiredProperty $dependencies 'targets' 'The embedded launcher dependency manifest'
    $target = Get-RequiredProperty $targets $runtimeTargetName 'The embedded launcher dependency targets'
    $runtimePackPrefix = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'
    $runtimePacks = @($target.PSObject.Properties | Where-Object {
            $_.Name.StartsWith($runtimePackPrefix, [StringComparison]::Ordinal)
        })
    if ($runtimePacks.Count -ne 1 -or
        $runtimePacks[0].Name.Substring($runtimePackPrefix.Length) -cne $runtimeVersion) {
        throw 'The embedded launcher dependency manifest does not bind the included win-x64 runtime pack.'
    }
}

function Get-PackageIdentity {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][ValidateSet('application', 'launcher', 'installer', 'product', 'product-installer')][string]$PackageRole,
        [Parameter(Mandatory)]$ReleaseIdentity
    )

    $executableName = if ($PackageRole -in @('installer', 'product-installer')) { 'PegasusCortexInstaller.exe' } else { 'PegasusCortex.exe' }
    $executablePath = Join-Path $Root $executableName
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "The $PackageRole package is missing $executableName."
    }
    $executable=$null
    if($PackageRole -cne 'product')
    {
    $executable = Get-ExecutableIdentity `
        -Path $executablePath `
        -ReleaseIdentity $ReleaseIdentity `
        -Description "The $PackageRole $executableName"
    }

    if ($PackageRole -ceq 'product') {
        $catalogRelativePath = "manifests/releases/$($ReleaseIdentity.ProductVersion)/product-catalog.v1.json"
        $catalogPath = Join-Path $Root $catalogRelativePath.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
            throw 'The unified product package is missing its product catalog.'
        }
        $catalog = Read-JsonObject -Path $catalogPath -Description 'The unified product catalog'
        if ([string]$catalog.contract -cne 'pegasus-cortex-product-catalog' -or
            [int]$catalog.version -ne 1 -or
            [string]$catalog.product -cne 'Pegasus Cortex' -or
            [string]$catalog.releaseVersion -cne $ReleaseIdentity.ProductVersion -or
            [string]$catalog.channel -cne 'Prod Alpha') {
            throw 'The unified product catalog identity is invalid.'
        }
        $roles = @($catalog.roles)
        if ($roles.Count -ne 8) {
            throw 'The unified product catalog does not contain exactly eight required roles.'
        }
        $roleNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($role in $roles) {
            $name = [string]$role.role
            $relative = [string]$role.relativePath
            if (-not $roleNames.Add($name) -or
                [string]::IsNullOrWhiteSpace($relative) -or
                [IO.Path]::IsPathRooted($relative) -or
                $relative.Contains(':') -or
                $relative.Contains('\') -or
                $relative.Split('/') -contains '..') {
                throw 'The unified product catalog contains an unsafe or duplicate role.'
            }
            $rolePath = Join-Path $Root $relative.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $rolePath -PathType Leaf) -or
                (((Get-Item -LiteralPath $rolePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "The unified product catalog role '$name' is missing or unsafe."
            }
            # Global release and each component retain separate exact identities.
            $componentIdentity=Get-ReleaseIdentity -Value ([string]$role.componentVersion)
            $roleExecutable=Get-ExecutableIdentity -Path $rolePath -ReleaseIdentity $componentIdentity -Description ('The product role '+$name)
            if($name -ceq 'launcher')
            {
                if($relative -cne 'PegasusCortex.exe'){throw 'The launcher role does not select the stable entrypoint.'}
                $executable=$roleExecutable
            }
        }
        foreach ($requiredRole in @('mcp','host','service','pao','process-host','ui-host','launcher','setup')) {
            if (-not $roleNames.Contains($requiredRole)) {
                throw "The unified product catalog is missing role '$requiredRole'."
            }
        }
        Assert-SelfContainedSingleFileIdentity `
            -Path $executablePath `
            -Description 'The unified product stable launcher'
        return [ordered]@{
            entryPoint = 'PegasusCortex.exe'
            productVersion = $executable.productVersion
            fileVersion = $executable.fileVersion
            targetFramework = 'net10.0'
            runtimeIdentifier = 'win-x64'
            deploymentMode = 'unified-one-root-nsis-product'
            selfContained = $true
            catalog = $catalogRelativePath
            roles = $roles.Count
        }
    }

    if ($PackageRole -ceq 'product-installer') {
        $contractPath = Join-Path $Root 'installer-contract.v7.json'
        if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
            throw 'The NSIS installer package is missing installer-contract.v7.json.'
        }
        $contract = Read-JsonObject -Path $contractPath -Description 'The NSIS installer contract'
        $guidance = $contract.codexGuidance
        $registration = $contract.codexRegistration
        $starterGuidance = $contract.starterGuidance
        $components = @($contract.components)
        if ([string]$contract.contract -cne 'pegasus-cortex-nsis-installer' -or
            [int]$contract.version -ne 7 -or
            [string]$contract.releaseVersion -cne $ReleaseIdentity.ProductVersion -or
            [string]$contract.channel -cne 'Prod Alpha' -or
            [string]$contract.companionProductRole -cne 'product' -or
            [string]$contract.payloadLayout -cne 'adjacent-uncompressed-product-directory' -or
            [string]$contract.payloadDirectory -cne 'product' -or
            [string]$contract.companionProductManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            [string]$contract.companionProductSignatureSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            [string]$contract.installRoot -cne 'default %ProgramFiles%\Pegasus\Cortex; fresh installs allow a canonical local fixed-drive selection; upgrade and repair reuse the registered location' -or
            [string]$contract.dataRoot -cne 'default %ProgramData%\Pegasus\Cortex for all mutable state; fresh installs select a durable local root independently; custom registered data roots are preserved; the exact legacy install-root/state default is copied with originals retained and registry selection last' -or
            [string]$contract.dataIsolation -cne 'per-user isolation enforced by Cortex; program installation is machine-wide' -or
            [string]$contract.databasePreservation -cne 'same-location setup preserves databases; exact legacy install-local state is copied to the separate data location with originals retained and registry selection last; no database conversion or seeding; fresh installation creates empty folders only' -or
            (@($contract.modes) -join ',') -cne 'install,upgrade,repair,modify,uninstall' -or
            [string]$contract.downgrade -cne 'blocked' -or
        [string]$contract.payloadExecution -cne 'the small signed coordinator uses the adjacent uncompressed signed product directory; required core, trust, and every Codex feature have separate routines; Modify executes only explicitly selected optional features; an unselected feature executes nothing' -or
        [string]$contract.packageAdmission -cne 'outer detached signature and exact inventory admit the complete companion folder; nested product detached signature and exact immutable-file hashes remain the required security boundary' -or
        [string]$contract.progress -cne 'plain status stages omit transaction identifiers during normal operation; persistent logs and failure receipts retain one internal RID for diagnostics' -or
            [string]$contract.uninstall -cne 'validated uninstall.v1.dat inventory; immutable files and owned execution registrations removed; data-location binding and every database retained; ordinary uninstall exposes no data-deletion option' -or
            [string]$contract.maintenance -cne 'the signed coordinator is cached as installer/Setup.exe; Add/Remove Programs exposes exact Modify, Repair, and Uninstall routes; the registered external companion source is reused for repair/update without duplicating the product payload' -or
            [string]$contract.optionDetection -cne 'no hidden setup executable or feature-state probe runs before the wizard; every optional feature starts inactive' -or
            [string]$contract.newOptionPolicy -cne 'fresh install, upgrade, repair, and Modify leave every unselected optional feature untouched; install, repair, and remove are separate explicitly selected routines' -or
            $components.Count -ne 4 -or
            (@($components | ForEach-Object { [string]$_.id }) -join ',') -cne 'core,trust,codex-registration,codex-guidance' -or
            [bool]$components[0].required -ne $true -or [string]$components[0].default -cne 'selected' -or
            @($components | Select-Object -Skip 1 | Where-Object { [bool]$_.required -or [string]$_.default -cne 'no-action' }).Count -ne 0 -or
            $contract.trust.optional -isnot [bool] -or -not $contract.trust.optional -or
            [string]$contract.trust.selection -cne '/TRUST=0|1' -or
            [string]$contract.trust.interactive -cne 'unselected unless the user explicitly selects trust installation' -or
            [string]$contract.trust.silent -cne 'unselected unless the unattended caller explicitly supplies /TRUST=1' -or
            [string]$contract.trust.operation -cne 'runs before product setup; selected trust retained on product failure' -or
            [string]$contract.stateMigration -cne 'disabled-during-normal-install' -or
            $null -eq $starterGuidance -or
            $starterGuidance.optional -isnot [bool] -or -not $starterGuidance.optional -or
            $starterGuidance.defaultEnabled -isnot [bool] -or $starterGuidance.defaultEnabled -or
            [string]$starterGuidance.presentation -cne 'unchecked finish-page reference' -or
            [string]$starterGuidance.relativePath -cne 'docs/STARTER_PROGRAMMING_TENETS.txt' -or
            [string]$starterGuidance.authority -cne 'overview only; knowledge.shared starter records remain authoritative' -or
            $null -eq $registration -or
            $registration.optional -isnot [bool] -or -not $registration.optional -or
            $registration.defaultEnabled -isnot [bool] -or $registration.defaultEnabled -or
            [string]$registration.upgradeDefault -cne 'no-action' -or
            [string]$registration.selection -cne '/CODEXREGISTER=1 selects the independent install or repair routine; /CODEXREGISTER=0 on an installed product selects the independent removal routine' -or
            [string]$registration.defaultTarget -cne 'CODEX_HOME or %USERPROFILE%\.codex\config.toml' -or
            [string]$registration.override -cne '/CODEXHOME=<canonical-Codex-home>' -or
            [string]$registration.operation -cne 'atomic-stable-mcp-route-replaces-exact-legacy-cortex-plugin' -or
            [bool]$registration.preservesUnrelatedConfiguration -ne $true -or
            $null -eq $guidance -or
            $guidance.optional -isnot [bool] -or -not $guidance.optional -or
            $guidance.defaultEnabled -isnot [bool] -or $guidance.defaultEnabled -or
            [string]$guidance.upgradeDefault -cne 'no-action' -or
            [string]$guidance.selection -cne '/CODEXGUIDANCE=1 selects the independent install or repair routine; /CODEXGUIDANCE=0 on an installed product selects the independent removal routine' -or
            [string]$guidance.defaultTarget -cne '%USERPROFILE%\.codex\AGENTS.md' -or
            [string]$guidance.override -cne '/CODEXAGENTS=<canonical-AGENTS.md>' -or
            [string]$guidance.operation -cne 'verified-fragments-atomic-managed-block-update' -or
            [bool]$guidance.preservesUnrelatedGuidance -ne $true) {
            throw 'The NSIS installer contract identity is invalid.'
        }
        $companionManifestPath = Join-Path $Root 'product\package-manifest.json'
        $companionSignaturePath = Join-Path $Root 'product\package-manifest.p7s'
        if (-not (Test-Path -LiteralPath $companionManifestPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $companionSignaturePath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $companionManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$contract.companionProductManifestSha256 -or
            (Get-FileHash -LiteralPath $companionSignaturePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$contract.companionProductSignatureSha256) {
            throw 'The adjacent companion product does not match the installer contract.'
        }
        return [ordered]@{
            entryPoint = 'PegasusCortexInstaller.exe'
            productVersion = $executable.productVersion
            fileVersion = $executable.fileVersion
            runtimeIdentifier = 'win-x64'
            deploymentMode = 'signed-nsis-adjacent-product-installer'
            selfContained = $false
            companionProductRole = 'product'
            companionProductManifestSha256 = [string]$contract.companionProductManifestSha256
            companionProductSignatureSha256 = [string]$contract.companionProductSignatureSha256
            codexRegistration = 'optional-explicit-stable-mcp-route'
            codexGuidanceOptional = $true
            codexGuidance = 'verified-fragments-atomic-managed-block-update'
            codexGuidanceDefaultTarget = '%USERPROFILE%\.codex\AGENTS.md'
            starterGuidance = 'optional-unchecked-finish-page-reference'
        }
    }

    if ($PackageRole -ceq 'installer') {
        foreach ($required in @(
                'Install-PegasusCortexV2X64.ps1',
                'PegasusSigning.psm1',
                'PegasusTrust.psm1',
                'Invoke-PegasusProdAlphaTrust.ps1')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $required) -PathType Leaf)) {
                throw "The installer package is missing '$required'."
            }
        }
        Assert-SelfContainedSingleFileIdentity `
            -Path $executablePath `
            -Description 'The installer bootstrap executable'
        return [ordered]@{
            entryPoint = $executableName
            productVersion = $executable.productVersion
            fileVersion = $executable.fileVersion
            targetFramework = 'net10.0'
            runtimeIdentifier = 'win-x64'
            deploymentMode = 'self-contained-single-file-installer-bootstrap'
            selfContained = $true
        }
    }

    if ($PackageRole -ceq 'launcher') {
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $null = $allowed.Add('PegasusCortex.exe')
        $null = $allowed.Add('PegasusCortex.pdb')
        foreach ($item in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
            if ($item.PSIsContainer) {
                if ($item.Name -cne 'trust' -or
                    (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw 'The launcher package contains an unexpected directory.'
                }
                continue
            }
            if (-not $allowed.Contains($item.Name)) {
                throw 'The launcher package is not the expected self-contained single-file publish.'
            }
        }
        Assert-SelfContainedSingleFileIdentity `
            -Path $executablePath `
            -Description 'The launcher PegasusCortex.exe'
        return [ordered]@{
            entryPoint = 'PegasusCortex.exe'
            productVersion = $executable.productVersion
            fileVersion = $executable.fileVersion
            targetFramework = 'net10.0'
            runtimeIdentifier = 'win-x64'
            deploymentMode = 'self-contained-single-file'
            selfContained = $true
        }
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
    $runtimeOptions = Get-RequiredProperty $runtimeConfiguration 'runtimeOptions' 'The application runtime configuration'
    $targetFramework = [string](Get-RequiredProperty $runtimeOptions 'tfm' 'The application runtime options')
    if ($targetFramework -cne 'net10.0') {
        throw 'The application runtime configuration is not net10.0.'
    }
    $includedFrameworks = @(Get-RequiredProperty $runtimeOptions 'includedFrameworks' 'The application runtime options')
    $coreFramework = @($includedFrameworks | Where-Object {
            [string](Get-RequiredProperty $_ 'name' 'An included framework') -ceq 'Microsoft.NETCore.App' -and
            [string](Get-RequiredProperty $_ 'version' 'An included framework') -match '^10\.'
        })
    if ($coreFramework.Count -ne 1) {
        throw 'The application runtime configuration does not bind one included .NET 10 runtime.'
    }
    $runtimeVersion = [string](Get-RequiredProperty $coreFramework[0] 'version' 'The included .NET runtime')

    $dependencies = Read-JsonObject `
        -Path (Join-Path $Root 'PegasusCortex.deps.json') `
        -Description 'The application dependency manifest'
    $runtimeTarget = Get-RequiredProperty $dependencies 'runtimeTarget' 'The application dependency manifest'
    $runtimeTargetName = [string](Get-RequiredProperty $runtimeTarget 'name' 'The application runtime target')
    if ($runtimeTargetName -cne '.NETCoreApp,Version=v10.0/win-x64') {
        throw 'The application dependency manifest is not the net10.0 win-x64 target.'
    }
    $targets = Get-RequiredProperty $dependencies 'targets' 'The application dependency manifest'
    if ($null -eq $targets.PSObject.Properties[$runtimeTargetName]) {
        throw 'The application dependency manifest omits its declared runtime target.'
    }

    return [ordered]@{
        entryPoint = 'PegasusCortex.exe'
        productVersion = $executable.productVersion
        fileVersion = $executable.fileVersion
        targetFramework = $targetFramework
        runtimeIdentifier = 'win-x64'
        runtimeVersion = $runtimeVersion
        deploymentMode = 'self-contained-apphost'
        selfContained = $true
    }
}

$releaseIdentity = Get-ReleaseIdentity -Value $ReleaseVersion

$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
Assert-OrdinaryDirectory -Path $root
Assert-PegasusNoPrivateKeyMaterial -Root $root
$trust = Read-PegasusTrustBundle -PackageRoot $root
if ((ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $PublisherThumbprint) -or
    (ConvertTo-PegasusThumbprint $trust.Root.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $RootThumbprint)) {
    throw 'The public trust bundle does not match the exact package signing identity.'
}
$manifestPath = Join-Path $root 'package-manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    throw 'The package manifest already exists; package admission is create-only.'
}
$packageIdentity = Get-PackageIdentity `
    -Root $root `
    -PackageRole $Role `
    -ReleaseIdentity $releaseIdentity
$paths = [Collections.Generic.List[string]]::new()
foreach ($item in Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The package contains a reparse point: '$($item.FullName)'."
    }
    if ($item.PSIsContainer) {
        continue
    }
    if (-not ($item -is [IO.FileInfo])) {
        throw "The package contains a non-regular file: '$($item.FullName)'."
    }
    $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or
        $relative.StartsWith('/', [StringComparison]::Ordinal) -or
        $relative.Contains(':', [StringComparison]::Ordinal) -or
        $relative.Split('/') -contains '..' -or
        $relative.Split('/') -contains '.') {
        throw "The package contains an unsafe relative path: '$relative'."
    }
    $paths.Add($relative)
}

if ($paths.Count -eq 0) {
    throw 'The package contains no payload files.'
}

$orderedPaths = $paths.ToArray()
[Array]::Sort($orderedPaths, [StringComparer]::Ordinal)
$authenticodePattern = if ($Role -in @('installer', 'product')) { '(?i)\.(exe|dll|ps1|psm1|psd1)$' } else { '(?i)\.(exe|dll)$' }
$pePaths = @($orderedPaths | Where-Object { $_ -match $authenticodePattern })
if ($pePaths.Count -eq 0) {
    throw 'The package contains no Authenticode-signable PE payload.'
}
foreach ($relative in $pePaths) {
    $path = Join-Path $root ($relative.Replace('/', '\'))
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($null -eq $signature.SignerCertificate -or
        $signature.Status -in @(
            [Management.Automation.SignatureStatus]::NotSigned,
            [Management.Automation.SignatureStatus]::HashMismatch) -or
        (ConvertTo-PegasusThumbprint $signature.SignerCertificate.Thumbprint) -cne
            (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint)) {
        throw "The package executable content is unsigned, corrupt, or signed by the wrong publisher: '$relative'."
    }
}

$payloadBytes = 0L
$files = foreach ($relative in $orderedPaths) {
    $path = Join-Path $root ($relative.Replace('/', '\'))
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    $payloadBytes = [long]($payloadBytes + $item.Length)
    [ordered]@{
        path = $relative
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifest = [ordered]@{
    schema = 'pegasus.cortex.local-operational-package/1.2'
    releaseVersion = $ReleaseVersion
    role = $Role
    channel = 'Prod Alpha'
    identity = $packageIdentity
    createdAtUtc = [DateTime]::UtcNow.ToString('O')
    signing = [ordered]@{
        schema = 'pegasus.package-signature/1.0'
        channel = 'Prod Alpha'
        algorithm = 'CMS-SHA256-RSA'
        authenticodeAlgorithm = 'SHA256-RSA'
        publisherThumbprint = (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint)
        rootThumbprint = (ConvertTo-PegasusThumbprint $trust.Root.Thumbprint)
        signedRole = $Role
        signedReleaseVersion = $ReleaseVersion
        detachedSignature = 'package-manifest.p7s'
        trustBundleManifest = 'trust/trust-bundle.json'
        trustBundleSha256 = $trust.ManifestSha256
    }
    files = @($files)
}
$temporary = Join-Path $root ('.package-manifest-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Write-Utf8NoBom -Path $temporary -Text (($manifest | ConvertTo-Json -Depth 8) + "`n")
    [IO.File]::Move($temporary, $manifestPath)
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction Stop
    }
}

[pscustomobject]@{
    Manifest = $manifestPath
    Role = $Role
    ReleaseVersion = $ReleaseVersion
    Files = $orderedPaths.Count
    PayloadBytes = $payloadBytes
    ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
