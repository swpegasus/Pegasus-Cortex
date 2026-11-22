[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [Parameter(Mandatory)]
    [string]$RuntimeArchivePath,

    [Parameter(Mandatory)]
    [string]$RuntimeReleaseMetadataPath,

    [string]$RuntimeVersion = '10.0.10',

    [string]$RuntimeArchiveSha512 = '3ea2ac626fdb0f26bb52f02584ae5e68ee88c275e740b34d93918ad380985d0dbfa1632c017658dfddc5fba396114b62748efc11b25897877814a4a4c5022edc',

    [string]$RuntimeSourceUri = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.10/dotnet-runtime-10.0.10-linux-arm64.tar.gz',

    [string]$RuntimeIdentifier = 'linux-arm64',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot = '',

    [string]$SourceSnapshotPath = '',

    [string]$ReleaseToolsPath = '',

    [string]$ReleaseToolsSha256 = '43559735b56d86c83cae408ccff4d7824da6a30650a5d39b274fe9890ce7d115'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:WindowsPathLimit = 240

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$Path
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Assert-ContainedPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate,
        [switch]$AllowRoot
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    $comparison = if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }

    if ($AllowRoot -and $candidatePath.Equals($rootPath, $comparison)) {
        return $candidatePath
    }

    $prefix = "$rootPath$([IO.Path]::DirectorySeparatorChar)"
    if (-not $candidatePath.StartsWith($prefix, $comparison)) {
        throw "Path '$candidatePath' is outside the owned root '$rootPath'."
    }

    return $candidatePath
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory)][string]$ExistingRoot,
        [Parameter(Mandatory)][string]$Candidate
    )

    $rootPath = (Resolve-Path -LiteralPath $ExistingRoot).Path
    $candidatePath = Assert-ContainedPath -Root $rootPath -Candidate $Candidate -AllowRoot
    $relative = $candidatePath.Substring($rootPath.TrimEnd('\', '/').Length).TrimStart('\', '/')
    $cursor = $rootPath
    $paths = @($cursor)
    foreach ($segment in $relative.Split(@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (-not (Test-Path -LiteralPath $cursor)) {
            break
        }
        $paths += $cursor
    }

    foreach ($path in $paths) {
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Path '$candidatePath' crosses reparse point '$path'."
        }
    }
}

function Assert-TrustedVolumeParent {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Secure staging volume parent is not a regular directory: '$Path'."
    }

    $trusted = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$trusted.Add([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    [void]$trusted.Add('S-1-5-18')       # Local System
    [void]$trusted.Add('S-1-5-32-544')  # Built-in Administrators
    $trustedInstallerAccount = [Security.Principal.NTAccount]::new('NT SERVICE', 'TrustedInstaller')
    $trustedInstaller = $trustedInstallerAccount.Translate([Security.Principal.SecurityIdentifier])
    [void]$trusted.Add($trustedInstaller.Value)
    $dangerous = [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $acl = $item.GetAccessControl(
        [Security.AccessControl.AccessControlSections]::Access -bor
        [Security.AccessControl.AccessControlSections]::Owner)
    if (-not $trusted.Contains($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value)) {
        throw "Secure staging volume parent has an untrusted owner: '$Path'."
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0 -or
            $trusted.Contains($rule.IdentityReference.Value)) {
            continue
        }
        if (($rule.FileSystemRights -band $dangerous) -ne 0) {
            throw "Secure staging volume parent grants an untrusted principal delete-child or ACL ownership authority: '$Path'."
        }
    }
}

function Assert-ExclusiveWriterDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowInheritedRules
    )

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Exclusive-writer path is not a regular directory: '$Path'."
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = $item.GetAccessControl(
        [Security.AccessControl.AccessControlSections]::Access -bor
        [Security.AccessControl.AccessControlSections]::Owner)
    if ((-not $AllowInheritedRules -and -not $acl.AreAccessRulesProtected) -or
        $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $currentSid.Value) {
        throw "Exclusive-writer directory does not have the required protected owner ACL: '$Path'."
    }

    $writerMask = [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Modify -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $currentFullControl = $false
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            continue
        }
        if ($rule.IdentityReference.Value -ceq $currentSid.Value -and
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl) {
            $currentFullControl = $true
            continue
        }
        if (($rule.FileSystemRights -band $writerMask) -ne 0) {
            throw "Exclusive-writer directory grants write authority to another principal: '$Path'."
        }
    }
    if (-not $currentFullControl) {
        throw "Exclusive-writer directory does not grant the invoking identity full control: '$Path'."
    }
}

function New-ExclusiveWriterDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -gt $script:WindowsPathLimit -or (Test-Path -LiteralPath $fullPath)) {
        throw "Exclusive-writer directory must be a new path within the $($script:WindowsPathLimit)-character budget: '$fullPath'."
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($currentSid)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $currentSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    [void]$security.AddAccessRule($rule)

    $created = $false
    try {
        $directory = [IO.DirectoryInfo]::new($fullPath)
        $directory.Create($security)
        $created = $true
        Assert-ExclusiveWriterDirectory -Path $fullPath
        if ([IO.Directory]::EnumerateFileSystemEntries($fullPath).GetEnumerator().MoveNext()) {
            throw "New exclusive-writer directory is unexpectedly non-empty: '$fullPath'."
        }
    }
    catch {
        if ($created -and (Test-Path -LiteralPath $fullPath)) {
            [IO.Directory]::Delete($fullPath, $false)
        }
        throw
    }
}

function Initialize-ExclusiveWriterDirectory {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Assert-ExclusiveWriterDirectory -Path $Path
    }
    else {
        New-ExclusiveWriterDirectory -Path $Path
    }
}

function Assert-RegularFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required regular file does not exist: '$Path'."
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse-point files are not accepted: '$Path'."
    }

    return $item
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($Path, "$json$([Environment]::NewLine)", [Text.UTF8Encoding]::new($false))
}

function Get-Utf8TextSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString(
            $algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($Value)))
        return $hash.Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-DirectoryDigestEvidence {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string[]]$ExcludedDirectoryNames = @()
    )

    $rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ExcludedDirectoryNames) {
        [void]$excluded.Add($name)
    }

    $lines = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force) {
        $relative = $file.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
        $isExcluded = $false
        foreach ($segment in $relative.Split('/')) {
            if ($excluded.Contains($segment)) {
                $isExcluded = $true
                break
            }
        }
        if ($isExcluded) {
            continue
        }
        Assert-RegularFile -Path $file.FullName | Out-Null
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("$relative|$hash")
    }
    if ($lines.Count -eq 0) {
        throw "Directory digest input is empty: '$rootPath'."
    }

    $values = $lines.ToArray()
    [Array]::Sort($values, [StringComparer]::Ordinal)
    $canonical = ($values -join "`n") + "`n"
    return [pscustomobject]@{
        Sha256 = Get-Utf8TextSha256 -Value $canonical
        FileCount = $values.Count
    }
}

function Get-ReleaseTimestamp {
    param([Parameter(Mandatory)][string]$ReleaseVersion)

    if ($ReleaseVersion -notmatch '^1\.(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<hour>\d{2})\.(?<minute>\d{2})\.[A-D]$') {
        throw "Release version is not canonical: '$ReleaseVersion'."
    }

    $local = [datetime]::new(
        [int]$Matches.year,
        [int]$Matches.month,
        [int]$Matches.day,
        [int]$Matches.hour,
        [int]$Matches.minute,
        0,
        [DateTimeKind]::Unspecified)
    try {
        $zone = [TimeZoneInfo]::FindSystemTimeZoneById('America/Los_Angeles')
    }
    catch [TimeZoneNotFoundException] {
        $zone = [TimeZoneInfo]::FindSystemTimeZoneById('Pacific Standard Time')
    }

    return [DateTimeOffset]::new($local, $zone.GetUtcOffset($local)).ToUniversalTime().ToString('O')
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation
    )

    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with dotnet exit code $LASTEXITCODE."
    }
}

function Set-ExactProcessEnvironment {
    param([Parameter(Mandatory)][Collections.IDictionary]$Variables)

    # MSBuild imports arbitrary environment variables as properties. Replace
    # the inherited process environment as one unit so an unlisted property,
    # SDK redirect, startup hook, or NuGet plugin path cannot affect a build.
    foreach ($name in @([Environment]::GetEnvironmentVariables(
                [EnvironmentVariableTarget]::Process).Keys)) {
        [Environment]::SetEnvironmentVariable(
            [string]$name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    foreach ($entry in $Variables.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            [string]$entry.Key,
            [string]$entry.Value,
            [EnvironmentVariableTarget]::Process)
    }
}

function Assert-OfflineNuGetConfiguration {
    param(
        [Parameter(Mandatory)][string]$ConfigurationPath,
        [Parameter(Mandatory)][string]$AllowedPackagesRoot
    )

    [xml]$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw
    $sources = @($configuration.configuration.packageSources.add)
    if ($sources.Count -eq 0) {
        throw 'The locked restore requires at least one explicit local package source.'
    }
    $configurationRoot = Split-Path -Parent $ConfigurationPath
    foreach ($source in $sources) {
        $value = [string]$source.value
        if ([string]::IsNullOrWhiteSpace($value) -or
            $value.StartsWith('\\', [StringComparison]::Ordinal)) {
            throw "Network or invalid NuGet package source is not allowed: '$value'."
        }
        if ([IO.Path]::IsPathRooted($value)) {
            $resolved = [IO.Path]::GetFullPath($value)
        }
        elseif ([Uri]::IsWellFormedUriString($value, [UriKind]::Absolute)) {
            throw "Network or invalid NuGet package source is not allowed: '$value'."
        }
        else {
            $resolved = Resolve-AbsolutePath -BasePath $configurationRoot -Path $value
        }
        Assert-ContainedPath -Root $AllowedPackagesRoot -Candidate $resolved | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $AllowedPackagesRoot -Candidate $resolved
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "Local NuGet package source does not exist: '$resolved'."
        }
    }
}

function New-ProtectedNuGetConfiguration {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][Collections.IDictionary]$SourceOverrides,
        [Parameter(Mandatory)][Collections.IDictionary]$ExpectedPackageMappings,
        [Parameter(Mandatory)][string]$AllowedPackagesRoot
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Protected NuGet configuration destination already exists: '$DestinationPath'."
    }
    [xml]$configuration = Get-Content -LiteralPath $SourcePath -Raw
    if ($null -ne $configuration.SelectSingleNode('/configuration/packageSourceCredentials')) {
        throw 'Credential-bearing NuGet configuration is not accepted by the evidence builder.'
    }
    $sourceNodes = @($configuration.SelectNodes('/configuration/packageSources/add'))
    if (@($configuration.SelectNodes('/configuration/packageSources/clear')).Count -ne 1) {
        throw 'NuGet configuration must clear inherited package sources exactly once.'
    }
    [string[]]$actualSourceKeys = @($sourceNodes | ForEach-Object { [string]$_.key })
    [string[]]$expectedSourceKeys = @($SourceOverrides.Keys | ForEach-Object { [string]$_ })
    [Array]::Sort($actualSourceKeys, [StringComparer]::Ordinal)
    [Array]::Sort($expectedSourceKeys, [StringComparer]::Ordinal)
    if (($actualSourceKeys -join "`n") -cne ($expectedSourceKeys -join "`n")) {
        throw 'NuGet configuration package sources do not match the exact protected source-key set.'
    }

    $mappingContainers = @($configuration.SelectNodes('/configuration/packageSourceMapping'))
    $mappingNodes = @($configuration.SelectNodes('/configuration/packageSourceMapping/packageSource'))
    [string[]]$actualMappingKeys = @($mappingNodes | ForEach-Object { [string]$_.key })
    [string[]]$expectedMappingKeys = @($ExpectedPackageMappings.Keys | ForEach-Object { [string]$_ })
    [Array]::Sort($actualMappingKeys, [StringComparer]::Ordinal)
    [Array]::Sort($expectedMappingKeys, [StringComparer]::Ordinal)
    if ($mappingContainers.Count -ne 1 -or
        ($actualMappingKeys -join "`n") -cne ($expectedMappingKeys -join "`n") -or
        ($expectedMappingKeys -join "`n") -cne ($expectedSourceKeys -join "`n")) {
        throw 'NuGet package-source mappings do not match the exact protected source-key set.'
    }
    foreach ($mapping in $mappingNodes) {
        $key = [string]$mapping.key
        [string[]]$actualPatterns = @($mapping.SelectNodes('./package') | ForEach-Object { [string]$_.pattern })
        [string[]]$expectedPatterns = @($ExpectedPackageMappings[$key] | ForEach-Object { [string]$_ })
        [Array]::Sort($actualPatterns, [StringComparer]::Ordinal)
        [Array]::Sort($expectedPatterns, [StringComparer]::Ordinal)
        if (($actualPatterns -join "`n") -cne ($expectedPatterns -join "`n")) {
            throw "NuGet package-source mapping '$key' does not match its exact package-pattern set."
        }
    }
    foreach ($source in $sourceNodes) {
        $key = [string]$source.key
        if ([string]::IsNullOrWhiteSpace($key) -or -not $SourceOverrides.Contains($key)) {
            throw "NuGet package source '$key' has no protected feed mapping."
        }
        $resolved = [IO.Path]::GetFullPath([string]$SourceOverrides[$key])
        Assert-ContainedPath -Root $AllowedPackagesRoot -Candidate $resolved | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $AllowedPackagesRoot -Candidate $resolved
        Assert-ExclusiveWriterDirectory -Path $resolved
        $source.value = $resolved
    }

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $stream = [IO.File]::Open(
        $DestinationPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $writer = [Xml.XmlWriter]::Create($stream, $settings)
        try {
            $configuration.Save($writer)
        }
        finally {
            $writer.Dispose()
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    Assert-OfflineNuGetConfiguration `
        -ConfigurationPath $DestinationPath `
        -AllowedPackagesRoot $AllowedPackagesRoot
}

function Assert-Aarch64Elf {
    param([Parameter(Mandatory)][string]$Path)

    $buffer = New-Object byte[] 20
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Read($buffer, 0, $buffer.Length) -ne $buffer.Length) {
            throw "Native runtime file is too short for an ELF header: '$Path'."
        }
    }
    finally {
        $stream.Dispose()
    }

    if ($buffer[0] -ne 0x7f -or $buffer[1] -ne 0x45 -or $buffer[2] -ne 0x4c -or $buffer[3] -ne 0x46 -or
        $buffer[4] -ne 2 -or $buffer[5] -ne 1 -or $buffer[18] -ne 0xb7 -or $buffer[19] -ne 0x00) {
        throw "Native runtime file is not a little-endian 64-bit AArch64 ELF: '$Path'."
    }
}

function Get-RuntimeReleaseMetadataEvidence {
    param(
        [Parameter(Mandatory)][string]$MetadataPath,
        [Parameter(Mandatory)][string]$RuntimeVersion,
        [Parameter(Mandatory)][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$SourceUri,
        [Parameter(Mandatory)][string]$ExpectedSha512
    )

    $metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
    $release = @($metadata.releases | Where-Object {
        [string]$_.runtime.version -ceq $RuntimeVersion
    })
    if ($release.Count -ne 1) {
        throw "Release metadata must contain exactly one runtime '$RuntimeVersion' release; found $($release.Count)."
    }
    $file = @($release[0].runtime.files | Where-Object {
        [string]$_.rid -ceq $RuntimeIdentifier -and [string]$_.url -ceq $SourceUri
    })
    $metadataHash = if ($file.Count -eq 1) { ([string]$file[0].hash).ToLowerInvariant() } else { '' }
    if ($file.Count -ne 1 -or $metadataHash -cne $ExpectedSha512.ToLowerInvariant()) {
        throw 'Release metadata does not bind the exact RID, URL, and SHA-512 runtime archive identity.'
    }

    return [ordered]@{
        channelVersion = [string]$metadata.'channel-version'
        releaseVersion = [string]$release[0].'release-version'
        releaseDate = [string]$release[0].'release-date'
        security = [bool]$release[0].security
        metadataSha256 = (Get-FileHash -LiteralPath $MetadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Set-PublishedRuntimePin {
    param(
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$AssemblyName,
        [Parameter(Mandatory)][string]$RuntimeVersion
    )

    $runtimeConfigurationPath = Join-Path $PublishRoot "$AssemblyName.runtimeconfig.json"
    Assert-RegularFile -Path $runtimeConfigurationPath | Out-Null
    $runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigurationPath -Raw | ConvertFrom-Json
    if ([string]$runtimeConfiguration.runtimeOptions.tfm -cne 'net10.0' -or
        [string]$runtimeConfiguration.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App') {
        throw "Published entrypoint '$AssemblyName' does not declare the expected net10.0 runtime."
    }

    $runtimeConfiguration.runtimeOptions.framework.version = $RuntimeVersion
    if ($null -eq $runtimeConfiguration.runtimeOptions.PSObject.Properties['rollForward']) {
        $runtimeConfiguration.runtimeOptions | Add-Member -NotePropertyName rollForward -NotePropertyValue 'Disable'
    }
    else {
        $runtimeConfiguration.runtimeOptions.rollForward = 'Disable'
    }
    Write-Utf8Json -Path $runtimeConfigurationPath -Value $runtimeConfiguration
}

function Get-PublishedArchiveToolPath {
    param(
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$AssemblyName,
        [Parameter(Mandatory)][string]$RuntimeVersion
    )

    [string[]]$expected = @(
        "$AssemblyName.deps.json",
        "$AssemblyName.dll",
        "$AssemblyName.runtimeconfig.json")
    [string[]]$actual = @(Get-ChildItem -LiteralPath $PublishRoot -File -Force | ForEach-Object Name)
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    [Array]::Sort($actual, [StringComparer]::Ordinal)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw 'The published ArchiveTool contains an unexpected file set.'
    }

    $runtimeConfiguration = Get-Content `
        -LiteralPath (Join-Path $PublishRoot "$AssemblyName.runtimeconfig.json") `
        -Raw | ConvertFrom-Json
    if ([string]$runtimeConfiguration.runtimeOptions.tfm -cne 'net10.0' -or
        [string]$runtimeConfiguration.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App' -or
        [string]$runtimeConfiguration.runtimeOptions.framework.version -cne $RuntimeVersion -or
        [string]$runtimeConfiguration.runtimeOptions.rollForward -cne 'Disable') {
        throw 'The published ArchiveTool does not declare the exact reviewed runtime.'
    }

    $toolPath = Join-Path $PublishRoot "$AssemblyName.dll"
    Assert-RegularFile -Path $toolPath | Out-Null
    return $toolPath
}

function Get-DotNetRuntimeEvidence {
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string]$RuntimeVersion
    )

    [string[]]$lines = @(& $DotNetPath --list-runtimes 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw 'The local dotnet host could not enumerate installed runtimes.'
    }

    $candidates = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, '^Microsoft\.NETCore\.App (?<version>10\.0\.\d+) \[(?<root>.+)\]$')
        if ($match.Success) {
            $candidates += [pscustomobject]@{
                Version = $match.Groups['version'].Value
                Root = $match.Groups['root'].Value
            }
        }
    }
    if ($candidates.Count -ne 1 -or [string]$candidates[0].Version -cne $RuntimeVersion) {
        throw "ArchiveTool execution requires exactly one installed Microsoft.NETCore.App 10.0 runtime, version '$RuntimeVersion'."
    }

    $frameworkRoot = Join-Path ([string]$candidates[0].Root) $RuntimeVersion
    $frameworkItem = Get-Item -LiteralPath $frameworkRoot -Force
    if (-not $frameworkItem.PSIsContainer -or
        ($frameworkItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The selected ArchiveTool execution runtime is not a regular directory.'
    }
    $digest = Get-DirectoryDigestEvidence -Root $frameworkRoot
    $componentEvidence = @()
    foreach ($name in @('System.Private.CoreLib.dll', 'System.Formats.Tar.dll', 'System.IO.Compression.dll')) {
        $path = Join-Path $frameworkRoot $name
        Assert-RegularFile -Path $path | Out-Null
        $componentEvidence += [ordered]@{
            name = $name
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    return [ordered]@{
        framework = 'Microsoft.NETCore.App'
        version = $RuntimeVersion
        directoryFileCount = $digest.FileCount
        directoryDigestSha256 = $digest.Sha256
        components = $componentEvidence
    }
}

function Invoke-ArchiveToolExtraction {
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string]$ArchiveToolPath,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$ExpectedArchiveSha256,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(Mandatory)][string]$InventoryPath
    )

    # The task-owned staging root is the ArchiveTool's exclusive-writer parent.
    # Its reparse checks are defense in depth, not a hostile-parent race claim.
    [string[]]$output = @(& $DotNetPath $ArchiveToolPath extract `
            --archive $ArchivePath `
            --sha256 $ExpectedArchiveSha256 `
            --destination-root $DestinationRoot `
            --destination-name runtime `
            --inventory $InventoryPath `
            --max-canonical-path-utf16 $script:WindowsPathLimit 2>&1 |
        ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    $diagnostic = ($output -join "`n").Trim()
    if ($exitCode -ne 0) {
        throw "Structured runtime extraction failed with exit code $exitCode. Diagnostic: $diagnostic"
    }
    if ($output.Count -ne 1) {
        throw 'Structured runtime extraction did not return exactly one result document.'
    }

    try {
        $result = $diagnostic | ConvertFrom-Json
    }
    catch {
        throw 'Structured runtime extraction returned malformed result JSON.'
    }
    if ([string]$result.schemaVersion -cne 'pegasus.cortex.archive-tool-result/1' -or
        [string]$result.status -cne 'succeeded' -or
        [string]$result.archiveSha256 -cne $ExpectedArchiveSha256.ToLowerInvariant()) {
        throw 'Structured runtime extraction returned an unexpected result contract.'
    }

    return $result
}

function Invoke-ArchiveToolTests {
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string]$TestAssemblyPath,
        [Parameter(Mandatory)][int]$ExpectedCount
    )

    [string[]]$output = @(& $DotNetPath $TestAssemblyPath 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    $expectedSummary = "SUMMARY passed=$ExpectedCount failed=0"
    $summaryCount = @($output | Where-Object { $_ -ceq $expectedSummary }).Count
    if ($exitCode -ne 0 -or $summaryCount -ne 1) {
        throw "ArchiveTool focused tests failed with exit code $exitCode. Diagnostic: $(($output -join "`n").Trim())"
    }

    return [ordered]@{
        status = 'pass'
        passed = $ExpectedCount
        failed = 0
        outputSha256 = Get-Utf8TextSha256 -Value (($output -join "`n") + "`n")
    }
}

function Get-VerifiedRuntimeInventory {
    param(
        [Parameter(Mandatory)][string]$InventoryPath,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$RuntimeVersion,
        [Parameter(Mandatory)][string]$ExpectedArchiveSha256,
        [Parameter(Mandatory)][long]$ExpectedCompressedBytes,
        [Parameter(Mandatory)][object]$ToolResult
    )

    Assert-RegularFile -Path $InventoryPath | Out-Null
    $inventory = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
    $entries = @($inventory.entries)
    if ([string]$inventory.schemaVersion -cne 'pegasus.cortex.archive-inventory/1' -or
        [string]$inventory.archiveSha256 -cne $ExpectedArchiveSha256.ToLowerInvariant() -or
        [long]$inventory.compressedBytes -ne $ExpectedCompressedBytes -or
        [long]$ToolResult.compressedBytes -ne $ExpectedCompressedBytes -or
        [int]$inventory.materializedEntryCount -ne $entries.Count -or
        [long]$inventory.tarStreamBytes -ne [long]$ToolResult.tarStreamBytes -or
        [long]$inventory.totalFileBytes -ne [long]$ToolResult.totalFileBytes -or
        [int]$inventory.archiveEntryCount -ne [int]$ToolResult.archiveEntryCount -or
        [int]$inventory.materializedEntryCount -ne [int]$ToolResult.materializedEntryCount) {
        throw 'Structured runtime inventory is not bound to the extraction result and pinned archive.'
    }
    if ($ExpectedCompressedBytes -ne 34396993 -or
        [long]$inventory.tarStreamBytes -ne 88948224 -or
        [long]$inventory.totalFileBytes -ne 88803979 -or
        [int]$inventory.archiveEntryCount -ne 200 -or
        [int]$inventory.materializedEntryCount -ne 199) {
        throw 'Structured runtime inventory differs from the reviewed .NET 10.0.10 linux-arm64 archive shape.'
    }
    if ($entries.Count -eq 0) {
        throw 'Structured runtime inventory is empty.'
    }

    $rootPath = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\', '/')
    $rootItem = Get-Item -LiteralPath $rootPath -Force
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The extracted runtime root is not a regular directory.'
    }
    $previousPath = $null
    [string[]]$expectedEntries = @($entries | ForEach-Object {
        $entryPath = [string]$_.path
        $kind = [string]$_.kind
        $mode = [string]$_.mode
        if ([string]::IsNullOrWhiteSpace($entryPath) -or
            ($null -ne $previousPath -and [StringComparer]::Ordinal.Compare($previousPath, $entryPath) -ge 0)) {
            throw 'Structured runtime inventory paths are empty, duplicated, or not in ordinal order.'
        }
        $previousPath = $entryPath
        if (($kind -eq 'directory' -and $mode -cne '0755') -or
            ($kind -eq 'file' -and $mode -cne '0644' -and $mode -cne '0755') -or
            ($kind -ne 'directory' -and $kind -ne 'file')) {
            throw "Structured runtime inventory has an unsupported kind or mode for '$entryPath'."
        }

        $candidate = [IO.Path]::GetFullPath((Join-Path $rootPath $entryPath.Replace('/', '\')))
        Assert-ContainedPath -Root $rootPath -Candidate $candidate | Out-Null
        if ($candidate.Length -gt $script:WindowsPathLimit) {
            throw "Extracted runtime path exceeds the $($script:WindowsPathLimit)-character budget: '$entryPath'."
        }
        if ($kind -eq 'directory') {
            if (-not (Test-Path -LiteralPath $candidate -PathType Container) -or
                [long]$_.sizeBytes -ne 0 -or $null -ne $_.sha256) {
                throw "Extracted runtime directory differs from its structured inventory: '$entryPath'."
            }
            $item = Get-Item -LiteralPath $candidate -Force
        }
        else {
            $item = Assert-RegularFile -Path $candidate
            $expectedHash = [string]$_.sha256
            if ($item.Length -ne [long]$_.sizeBytes -or
                $expectedHash -notmatch '^[0-9a-f]{64}$' -or
                (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) {
                throw "Extracted runtime file differs from its structured inventory: '$entryPath'."
            }
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Extracted runtime inventory contains a reparse point: '$entryPath'."
        }
        "$kind|$entryPath"
    })

    [string[]]$actualEntries = @(Get-ChildItem -LiteralPath $RuntimeRoot -Recurse -Force | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Extracted runtime contains a reparse point: '$($_.Name)'."
        }
        $relative = $_.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
        $kind = if ($_.PSIsContainer) { 'directory' } else { 'file' }
        "$kind|$relative"
    })
    [Array]::Sort($expectedEntries, [StringComparer]::Ordinal)
    [Array]::Sort($actualEntries, [StringComparer]::Ordinal)
    if (($actualEntries -join "`n") -cne ($expectedEntries -join "`n")) {
        throw 'Extracted runtime tree differs from the structured inventory.'
    }

    foreach ($required in @(
            'dotnet',
            "host/fxr/$RuntimeVersion/libhostfxr.so",
            "shared/Microsoft.NETCore.App/$RuntimeVersion/System.Private.CoreLib.dll",
            "shared/Microsoft.NETCore.App/$RuntimeVersion/libhostpolicy.so",
            "shared/Microsoft.NETCore.App/$RuntimeVersion/libcoreclr.so",
            'LICENSE.txt',
            'ThirdPartyNotices.txt')) {
        Assert-RegularFile -Path (Join-Path $RuntimeRoot $required.Replace('/', '\')) | Out-Null
    }
    foreach ($forbiddenDirectory in @('sdk', 'sdk-manifests', 'packs', 'templates', 'metadata', 'workloads')) {
        if (Test-Path -LiteralPath (Join-Path $RuntimeRoot $forbiddenDirectory)) {
            throw "Runtime-only bundle unexpectedly contains '$forbiddenDirectory'."
        }
    }

    $fxrVersions = @(Get-ChildItem -LiteralPath (Join-Path $RuntimeRoot 'host\fxr') -Directory -Force | ForEach-Object Name)
    $runtimeFrameworks = @(Get-ChildItem -LiteralPath (Join-Path $RuntimeRoot 'shared') -Directory -Force | ForEach-Object Name)
    $frameworkVersions = @(Get-ChildItem -LiteralPath (Join-Path $RuntimeRoot 'shared\Microsoft.NETCore.App') -Directory -Force | ForEach-Object Name)
    if ($fxrVersions.Count -ne 1 -or $fxrVersions[0] -cne $RuntimeVersion -or
        $runtimeFrameworks.Count -ne 1 -or $runtimeFrameworks[0] -cne 'Microsoft.NETCore.App' -or
        $frameworkVersions.Count -ne 1 -or $frameworkVersions[0] -cne $RuntimeVersion) {
        throw 'Runtime-only bundle contains an unexpected framework or framework version.'
    }

    foreach ($entry in $entries | Where-Object {
            [string]$_.kind -eq 'file' -and
            ([string]$_.mode -eq '0755' -or ([string]$_.path).EndsWith('.so', [StringComparison]::Ordinal))
        }) {
        Assert-Aarch64Elf -Path (Join-Path $RuntimeRoot ([string]$entry.path).Replace('/', '\'))
    }

    return $entries
}

function Get-RelativeFileEvidence {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string[]]$ExcludedRelativePaths = @()
    )

    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $ExcludedRelativePaths) {
        [void]$excluded.Add($path.Replace('\', '/'))
    }

    [string[]]$paths = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object FullName)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $rootLength = $Root.TrimEnd('\', '/').Length + 1
    return @($paths | ForEach-Object {
        $relative = $_.Substring($rootLength).Replace('\', '/')
        if (-not $excluded.Contains($relative)) {
            $item = Get-Item -LiteralPath $_
            [ordered]@{
                path = $relative
                bytes = $item.Length
                sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    })
}

function Read-StreamExactly {
    param(
        [Parameter(Mandatory)][IO.Stream]$Stream,
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][int]$Count
    )

    if ($Count -lt 0 -or $Count -gt $Buffer.Length) {
        throw "Requested stream read length '$Count' is outside the supplied buffer."
    }

    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($Buffer, $offset, $Count - $offset)
        if ($read -le 0) {
            throw "Unexpected end of stream after $offset of $Count bytes."
        }
        $offset += $read
    }
}

function Invoke-ZipCreatorSystemPolicy {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)]
        [ValidateSet('NormalizeUnix', 'ValidateUnix')]
        [string]$Operation
    )

    $archive = Assert-RegularFile -Path $ArchivePath
    $access = if ($Operation -ceq 'NormalizeUnix') {
        [IO.FileAccess]::ReadWrite
    }
    else {
        [IO.FileAccess]::Read
    }
    $stream = [IO.File]::Open(
        $archive.FullName,
        [IO.FileMode]::Open,
        $access,
        [IO.FileShare]::None)
    try {
        if ($stream.Length -lt 22 -or $stream.Length -gt [uint32]::MaxValue) {
            throw "Archive length '$($stream.Length)' is not supported by the non-ZIP64 evidence profile."
        }

        $tailLength = [int][Math]::Min([long]65557, $stream.Length)
        $tail = New-Object byte[] $tailLength
        $tailOffset = $stream.Length - $tailLength
        $stream.Position = $tailOffset
        Read-StreamExactly -Stream $stream -Buffer $tail -Count $tailLength

        $eocdIndex = -1
        for ($index = $tailLength - 22; $index -ge 0; $index--) {
            if ($tail[$index] -eq 0x50 -and
                $tail[$index + 1] -eq 0x4b -and
                $tail[$index + 2] -eq 0x05 -and
                $tail[$index + 3] -eq 0x06) {
                $commentLength = [BitConverter]::ToUInt16($tail, $index + 20)
                if ($index + 22 + $commentLength -eq $tailLength) {
                    $eocdIndex = $index
                    break
                }
            }
        }
        if ($eocdIndex -lt 0) {
            throw 'Archive has no canonical end-of-central-directory record or contains trailing data.'
        }

        $diskNumber = [BitConverter]::ToUInt16($tail, $eocdIndex + 4)
        $centralDirectoryDisk = [BitConverter]::ToUInt16($tail, $eocdIndex + 6)
        $entriesOnDisk = [BitConverter]::ToUInt16($tail, $eocdIndex + 8)
        $entryCount = [BitConverter]::ToUInt16($tail, $eocdIndex + 10)
        $centralDirectorySize = [BitConverter]::ToUInt32($tail, $eocdIndex + 12)
        $centralDirectoryOffset = [BitConverter]::ToUInt32($tail, $eocdIndex + 16)
        if ($diskNumber -ne 0 -or $centralDirectoryDisk -ne 0 -or
            $entriesOnDisk -ne $entryCount -or $entryCount -eq 0 -or
            $entryCount -eq [uint16]::MaxValue -or
            $centralDirectorySize -eq [uint32]::MaxValue -or
            $centralDirectoryOffset -eq [uint32]::MaxValue) {
            throw 'Archive uses an unsupported multi-disk, empty, or ZIP64 central directory.'
        }

        $eocdOffset = $tailOffset + $eocdIndex
        $centralDirectoryEnd = [long]$centralDirectoryOffset + [long]$centralDirectorySize
        if ($centralDirectoryEnd -ne $eocdOffset) {
            throw 'Archive central-directory bounds do not terminate at the canonical end record.'
        }

        $header = New-Object byte[] 46
        $stream.Position = $centralDirectoryOffset
        for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
            $recordOffset = $stream.Position
            if ($recordOffset + $header.Length -gt $centralDirectoryEnd) {
                throw "Archive central-directory entry $entryIndex exceeds its declared bounds."
            }
            Read-StreamExactly -Stream $stream -Buffer $header -Count $header.Length
            if ([BitConverter]::ToUInt32($header, 0) -ne 0x02014b50) {
                throw "Archive central-directory entry $entryIndex has an invalid signature."
            }

            $creatorSystem = $header[5]
            if ($Operation -ceq 'NormalizeUnix') {
                if ($creatorSystem -ne 0 -and $creatorSystem -ne 3) {
                    throw "Archive central-directory entry $entryIndex has unsupported creator system '$creatorSystem'."
                }
                if ($creatorSystem -ne 3) {
                    $resumePosition = $stream.Position
                    $stream.Position = $recordOffset + 5
                    $stream.WriteByte(3)
                    $stream.Position = $resumePosition
                }
            }
            elseif ($creatorSystem -ne 3) {
                throw "Archive central-directory entry $entryIndex is not marked as Unix-created."
            }

            $nameLength = [BitConverter]::ToUInt16($header, 28)
            $extraLength = [BitConverter]::ToUInt16($header, 30)
            $commentLength = [BitConverter]::ToUInt16($header, 32)
            if ($nameLength -eq 0) {
                throw "Archive central-directory entry $entryIndex has an empty name."
            }
            $nextRecordOffset = $stream.Position + $nameLength + $extraLength + $commentLength
            if ($nextRecordOffset -gt $centralDirectoryEnd) {
                throw "Archive central-directory entry $entryIndex variable fields exceed declared bounds."
            }
            $stream.Position = $nextRecordOffset
        }

        if ($stream.Position -ne $centralDirectoryEnd) {
            throw 'Archive central-directory entry count does not consume its declared byte range.'
        }
        if ($Operation -ceq 'NormalizeUnix') {
            $stream.Flush($true)
        }
        return [int]$entryCount
    }
    finally {
        $stream.Dispose()
    }
}

function New-CortexDeterministicZipArchive {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$ExpectedRoot,
        [AllowEmptyCollection()]
        [string[]]$ExecutableRelativePaths = @()
    )

    $sharedResult = New-PegasusDeterministicZipArchive `
        -SourceRoot $SourceRoot `
        -ArchivePath $ArchivePath `
        -ExpectedRoot $ExpectedRoot `
        -ExecutableRelativePaths $ExecutableRelativePaths
    $normalizedCount = Invoke-ZipCreatorSystemPolicy `
        -ArchivePath $sharedResult.Path `
        -Operation NormalizeUnix
    $validatedCount = Invoke-ZipCreatorSystemPolicy `
        -ArchivePath $sharedResult.Path `
        -Operation ValidateUnix
    if ($normalizedCount -ne [int]$sharedResult.Entries -or $validatedCount -ne $normalizedCount) {
        throw 'Unix creator-system normalization did not cover every archive entry.'
    }

    $archive = Get-Item -LiteralPath $sharedResult.Path
    return [pscustomobject]@{
        Path = $archive.FullName
        Bytes = $archive.Length
        Entries = $normalizedCount
        Sha256 = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-ZipMatchesEvidence {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][object[]]$ExpectedFiles,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][string[]]$ExecutableRelativePaths
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $creatorEntryCount = Invoke-ZipCreatorSystemPolicy `
        -ArchivePath $ArchivePath `
        -Operation ValidateUnix
    if ($creatorEntryCount -ne $ExpectedFiles.Count) {
        throw "Archive creator-system entry count $creatorEntryCount does not match evidence count $($ExpectedFiles.Count)."
    }
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($file in $ExpectedFiles) {
        $expected.Add([string]$file.path, [string]$file.sha256)
    }
    $executables = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $ExecutableRelativePaths) {
        [void]$executables.Add($path.Replace('\', '/'))
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previousPath = $null
    # ZIP stores a DOS wall clock without an offset. .NET Framework assigns the
    # local offset when reopening it, so compare the preserved wall-clock value.
    $fixedTimestamp = [DateTime]::SpecifyKind(
        [DateTime]::new(2000, 1, 1, 0, 0, 0),
        [DateTimeKind]::Unspecified)
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $zip.Entries) {
            $path = $entry.FullName
            if ($path.Contains('\') -or [string]::IsNullOrWhiteSpace($entry.Name) -or -not $seen.Add($path)) {
                throw "Archive contains a directory or duplicate entry '$path'."
            }
            if ($null -ne $previousPath -and [StringComparer]::Ordinal.Compare($previousPath, $path) -ge 0) {
                throw "Archive entries are not in strict ordinal order at '$path'."
            }
            $previousPath = $path
            if (-not $expected.ContainsKey($path)) {
                throw "Archive contains unexpected entry '$path'."
            }
            if ($entry.LastWriteTime.DateTime -ne $fixedTimestamp) {
                throw "Archive entry timestamp is not canonical for '$path'."
            }

            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $actualHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
            }
            finally {
                $sha.Dispose()
                $stream.Dispose()
            }
            if ($actualHash -cne $expected[$path]) {
                throw "Archive entry hash mismatch for '$path'."
            }

            $expectedMode = if ($executables.Contains($path)) { 0x81ed } else { 0x81a4 }
            $actualMode = ($entry.ExternalAttributes -shr 16) -band 0xffff
            if ($actualMode -ne $expectedMode) {
                throw "Archive entry mode mismatch for '$path': expected $expectedMode, received $actualMode."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    if ($seen.Count -ne $expected.Count) {
        throw "Archive entry count $($seen.Count) does not match evidence count $($expected.Count)."
    }
}

function Copy-FileToProtectedPath {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    $source = Assert-RegularFile -Path $SourcePath
    $sourceRoot = [IO.Path]::GetPathRoot($source.FullName)
    if ([string]::IsNullOrWhiteSpace($sourceRoot) -or
        $sourceRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "Protected evidence inputs must come from a local filesystem path: '$SourcePath'."
    }
    Assert-NoReparseAncestors -ExistingRoot $sourceRoot -Candidate $source.FullName
    $destinationParent = Split-Path -Parent $DestinationPath
    Assert-ExclusiveWriterDirectory -Path $destinationParent
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Protected evidence input destination already exists: '$DestinationPath'."
    }

    $sourceStream = $null
    $destinationStream = $null
    try {
        # FileShare.Read keeps the exact source file stable while it is copied;
        # subsequent validation reads only the owner-protected destination.
        $sourceStream = [IO.File]::Open(
            $source.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sourceLength = $sourceStream.Length
        $destinationStream = [IO.File]::Open(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $sourceStream.CopyTo($destinationStream, 1MB)
        $destinationStream.Flush($true)
    }
    finally {
        if ($null -ne $destinationStream) {
            $destinationStream.Dispose()
        }
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
    }

    $destination = Assert-RegularFile -Path $DestinationPath
    if ($destination.Length -ne $sourceLength) {
        throw "Protected evidence input copy length mismatch for '$($source.Name)'."
    }
    return $destination
}

function Copy-VerifiedPackageFeed {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(Mandatory)][object[]]$Packages
    )

    $sourceRootPath = (Resolve-Path -LiteralPath $SourceRoot).Path
    New-ExclusiveWriterDirectory -Path $DestinationRoot
    $evidence = @()
    foreach ($package in $Packages) {
        $fileName = [string]$package.fileName
        if ([string]::IsNullOrWhiteSpace($fileName) -or
            $fileName -cne [IO.Path]::GetFileName($fileName) -or
            -not $fileName.EndsWith('.nupkg', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Protected package feed contains an invalid file name '$fileName'."
        }

        $sourcePath = Join-Path $sourceRootPath $fileName
        Assert-ContainedPath -Root $sourceRootPath -Candidate $sourcePath | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $sourceRootPath -Candidate $sourcePath
        $source = Assert-RegularFile -Path $sourcePath
        $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($source.Length -ne [long]$package.bytes -or
            $sourceHash -cne [string]$package.sha256) {
            throw "Package feed input does not match the reviewed byte identity: '$fileName'."
        }

        $destinationPath = Join-Path $DestinationRoot $fileName
        $destination = Copy-FileToProtectedPath `
            -SourcePath $source.FullName `
            -DestinationPath $destinationPath
        $destinationHash = (Get-FileHash -LiteralPath $destination.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($destination.Length -ne $source.Length -or $destinationHash -cne $sourceHash) {
            throw "Protected package feed copy differs from its reviewed input: '$fileName'."
        }
        $evidence += [ordered]@{
            id = [string]$package.id
            version = [string]$package.version
            feed = Split-Path -Leaf $DestinationRoot
            fileName = $fileName
            bytes = $destination.Length
            sha256 = $destinationHash
            use = [string]$package.use
        }
    }

    return @($evidence)
}

function Assert-VerifiedPackageFeedUnchanged {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][object[]]$Evidence
    )

    foreach ($package in $Evidence) {
        $relativePath = Join-Path ([string]$package.feed) ([string]$package.fileName)
        $path = Join-Path $Root $relativePath
        Assert-ContainedPath -Root $Root -Candidate $path | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $Root -Candidate $path
        $item = Assert-RegularFile -Path $path
        if ($item.Length -ne [long]$package.bytes -or
            (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne
                [string]$package.sha256) {
            throw "Protected package feed changed during restore or publish: '$relativePath'."
        }
    }
}

function Get-VerifiedSourceSnapshotEvidence {
    param(
        [Parameter(Mandatory)][string]$SnapshotPath,
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][object[]]$SourceInputs,
        [Parameter(Mandatory)][string]$ProtectedInputRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expectedName = "Pegasus-Cortex-$ReleaseVersion.zip"
    $sourceSnapshot = Assert-RegularFile -Path $SnapshotPath
    if ($sourceSnapshot.Name -cne $expectedName) {
        throw "Source snapshot file name must be '$expectedName'."
    }

    $sourceSnapshotDirectory = Split-Path -Parent $sourceSnapshot.FullName
    $sourceSnapshotManifestPath = Join-Path $sourceSnapshotDirectory 'snapshot-manifest.json'
    $sourceRetentionMarkerPath = Join-Path $sourceSnapshotDirectory '.pegasus-retention.json'
    Assert-RegularFile -Path $sourceSnapshotManifestPath | Out-Null
    Assert-RegularFile -Path $sourceRetentionMarkerPath | Out-Null

    $protectedSnapshotRoot = Join-Path $ProtectedInputRoot 'source-snapshot'
    New-ExclusiveWriterDirectory -Path $protectedSnapshotRoot
    $snapshot = Copy-FileToProtectedPath `
        -SourcePath $sourceSnapshot.FullName `
        -DestinationPath (Join-Path $protectedSnapshotRoot $expectedName)
    $snapshotManifestPath = Join-Path $protectedSnapshotRoot 'snapshot-manifest.json'
    $retentionMarkerPath = Join-Path $protectedSnapshotRoot '.pegasus-retention.json'
    Copy-FileToProtectedPath `
        -SourcePath $sourceSnapshotManifestPath `
        -DestinationPath $snapshotManifestPath | Out-Null
    Copy-FileToProtectedPath `
        -SourcePath $sourceRetentionMarkerPath `
        -DestinationPath $retentionMarkerPath | Out-Null

    $snapshotManifest = Get-Content -LiteralPath $snapshotManifestPath -Raw | ConvertFrom-Json
    $retentionMarker = Get-Content -LiteralPath $retentionMarkerPath -Raw | ConvertFrom-Json
    $snapshotHash = (Get-FileHash -LiteralPath $snapshot.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $record = @($snapshotManifest.snapshots | Where-Object {
            [string]$_.project -ceq 'Pegasus-Cortex' -and [string]$_.archive -ceq $expectedName
        })
    if ([int]$snapshotManifest.schemaVersion -ne 1 -or
        [string]$snapshotManifest.releaseVersion -cne $ReleaseVersion -or
        $record.Count -ne 1 -or
        [string]$record[0].sha256 -cne $snapshotHash -or
        [long]$record[0].archiveBytes -ne $snapshot.Length -or
        [int]$retentionMarker.schemaVersion -ne 1 -or
        [string]$retentionMarker.kind -cne 'snapshot' -or
        [string]$retentionMarker.releaseVersion -cne $ReleaseVersion -or
        $retentionMarker.verified -ne $true) {
        throw 'Source snapshot directory metadata does not bind the supplied immutable archive.'
    }

    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
    $portableEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($snapshot.FullName)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }
            $path = $entry.FullName.Replace('\', '/')
            if ($path.StartsWith('./', [StringComparison]::Ordinal)) {
                $path = $path.Substring(2)
            }
            if ([string]::IsNullOrWhiteSpace($path) -or $path.StartsWith('/') -or
                $path.Split('/') -contains '..' -or -not $portableEntries.Add($path)) {
                throw 'Source snapshot contains an ambiguous file path.'
            }
            $entries.Add($path, $entry)
        }

        foreach ($sourceInput in $SourceInputs) {
            $path = [string]$sourceInput.path
            if (-not $entries.ContainsKey($path)) {
                throw "Source snapshot is missing a sealed build input: '$path'."
            }
            $stream = $entries[$path].Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $actualHash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
            }
            finally {
                $algorithm.Dispose()
                $stream.Dispose()
            }
            if ($actualHash -cne [string]$sourceInput.sha256) {
                throw "Source snapshot build input differs from the protected build source: '$path'."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    return [ordered]@{
        fileName = $snapshot.Name
        bytes = $snapshot.Length
        sha256 = $snapshotHash
        snapshotManifestSha256 = (Get-FileHash -LiteralPath $snapshotManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        verifiedBuildInputCount = $SourceInputs.Count
    }
}

function Get-SourceInputEvidence {
    param([Parameter(Mandatory)][string]$Root)

    $fixed = @(
        'Directory.Build.props',
        'Directory.Version.props',
        'global.json',
        'NuGet.Config',
        'VERSION.json',
        'tools\New-CortexArm64EvidenceBundle.ps1')
    $scopedRoots = @(
        (Join-Path $Root 'src'),
        (Join-Path $Root 'docs'),
        (Join-Path $Root 'licenses'),
        (Join-Path $Root 'schemas'),
        (Join-Path $Root 'tools\PegasusCortex.ArchiveTool'),
        (Join-Path $Root 'tests\PegasusCortex.OfflineJournal.Platform.Linux.Tests'),
        (Join-Path $Root 'tests\PegasusCortex.Persistence.PostgreSql.IntegrationTests')
    )
    $paths = @($fixed | ForEach-Object { Join-Path $Root $_ })
    foreach ($scopedRoot in $scopedRoots) {
        $paths += @(Get-ChildItem -LiteralPath $scopedRoot -Recurse -File -Force |
            Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
            } |
            ForEach-Object FullName)
    }

    [string[]]$unique = @($paths | Sort-Object -Unique)
    [Array]::Sort($unique, [StringComparer]::Ordinal)
    $rootLength = $Root.TrimEnd('\', '/').Length + 1
    return @($unique | ForEach-Object {
        Assert-RegularFile -Path $_ | Out-Null
        [ordered]@{
            path = $_.Substring($rootLength).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Copy-SourceInputsToProtectedRoot {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(Mandatory)][object[]]$ExpectedEvidence
    )

    New-ExclusiveWriterDirectory -Path $DestinationRoot
    foreach ($entry in $ExpectedEvidence) {
        $relative = [string]$entry.path
        $source = Join-Path $SourceRoot $relative.Replace('/', '\')
        $destination = Join-Path $DestinationRoot $relative.Replace('/', '\')
        Assert-NoReparseAncestors -ExistingRoot $SourceRoot -Candidate $source
        Assert-RegularFile -Path $source | Out-Null
        if ($destination.Length -gt $script:WindowsPathLimit) {
            throw "Protected staged source path exceeds the $($script:WindowsPathLimit)-character budget: '$relative'."
        }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($source, $destination, $false)
        Assert-RegularFile -Path $destination | Out-Null
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            [string]$entry.sha256) {
            throw "Protected staged source differs from its pre-copy hash: '$relative'."
        }
    }

    $stagedEvidence = @(Get-SourceInputEvidence -Root $DestinationRoot)
    Assert-SourceEvidenceUnchanged -Before $ExpectedEvidence -After $stagedEvidence
}

function Get-DependencyInventory {
    param([Parameter(Mandatory)][string[]]$AssetsPaths)

    $components = @{}
    foreach ($assetsPath in $AssetsPaths) {
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        foreach ($property in $assets.libraries.PSObject.Properties) {
            $parts = ([string]$property.Name).Split(
                [char[]]@('/'),
                2,
                [StringSplitOptions]::None)
            $kind = [string]$property.Value.type
            $key = "$kind|$($parts[0])|$($parts[1])"
            if (-not $components.ContainsKey($key)) {
                $components[$key] = [ordered]@{
                    type = $kind
                    name = $parts[0]
                    version = $parts[1]
                    sha512 = if ($kind -eq 'package') { [string]$property.Value.sha512 } else { $null }
                }
            }
        }
    }

    return @($components.Keys | Sort-Object | ForEach-Object { $components[$_] })
}

function Test-JsonValuePresent {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [string]) {
        return -not [string]::IsNullOrWhiteSpace([string]$Value)
    }
    if ($Value -is [Collections.IDictionary]) {
        return $Value.Count -ne 0
    }
    if ($Value -is [Collections.IEnumerable]) {
        $enumerator = $Value.GetEnumerator()
        try {
            return $enumerator.MoveNext()
        }
        finally {
            if ($enumerator -is [IDisposable]) {
                $enumerator.Dispose()
            }
        }
    }
    if ($Value -is [psobject]) {
        return $Value.PSObject.Properties.Count -ne 0
    }
    return $true
}

function Get-PortableRestoreEvidence {
    param([Parameter(Mandatory)][string[]]$AssetsPaths)

    $targetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $projectCount = 0
    foreach ($assetsPath in $AssetsPaths) {
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        $projectCount++
        $restore = $assets.project.restore
        foreach ($propertyName in @('runtimeIdentifier', 'runtimeIdentifiers')) {
            $property = $restore.PSObject.Properties[$propertyName]
            if ($null -ne $property -and (Test-JsonValuePresent -Value $property.Value)) {
                throw "Portable locked restore contains '$propertyName' in '$([IO.Path]::GetFileName($assetsPath))'."
            }
        }

        foreach ($target in $assets.targets.PSObject.Properties) {
            $targetName = [string]$target.Name
            if ([string]::IsNullOrWhiteSpace($targetName) -or $targetName.Contains('/')) {
                throw "Portable locked restore contains a RID-specific target '$targetName'."
            }
            [void]$targetNames.Add($targetName)
            foreach ($library in $target.Value.PSObject.Properties) {
                foreach ($assetKind in @('runtimeTargets', 'native')) {
                    $assetProperty = $library.Value.PSObject.Properties[$assetKind]
                    if ($null -ne $assetProperty -and (Test-JsonValuePresent -Value $assetProperty.Value)) {
                        throw "Portable locked restore contains $assetKind assets for '$($library.Name)'."
                    }
                }
            }
        }
    }

    return [ordered]@{
        schemaVersion = 1
        status = 'pass'
        mode = 'portable-framework-dependent-managed-il'
        assetsFileCount = $projectCount
        targetNames = @($targetNames | Sort-Object)
        runtimeIdentifiers = @()
        nativeAssetCount = 0
        runtimeTargetAssetCount = 0
    }
}

function Assert-PublishedApplication {
    param(
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$AssemblyName,
        [Parameter(Mandatory)][string]$RuntimeVersion
    )

    foreach ($required in @("$AssemblyName.dll", "$AssemblyName.deps.json", "$AssemblyName.runtimeconfig.json")) {
        Assert-RegularFile -Path (Join-Path $PublishRoot $required) | Out-Null
    }
    if ((Test-Path -LiteralPath (Join-Path $PublishRoot "$AssemblyName.exe")) -or
        (Test-Path -LiteralPath (Join-Path $PublishRoot $AssemblyName))) {
        throw "Published application '$AssemblyName' unexpectedly contains an apphost."
    }
    $symbols = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Filter '*.pdb')
    if ($symbols.Count -ne 0) {
        throw "Published application '$AssemblyName' unexpectedly contains debug symbols."
    }

    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $PublishRoot "$AssemblyName.runtimeconfig.json") -Raw | ConvertFrom-Json
    if ([string]$runtimeConfiguration.runtimeOptions.tfm -cne 'net10.0' -or
        [string]$runtimeConfiguration.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App' -or
        [string]$runtimeConfiguration.runtimeOptions.framework.version -cne $RuntimeVersion -or
        [string]$runtimeConfiguration.runtimeOptions.rollForward -cne 'Disable') {
        throw "Published application '$AssemblyName' does not declare the exact reviewed net10.0 runtime."
    }
    $dependencyManifestText = Get-Content `
        -LiteralPath (Join-Path $PublishRoot "$AssemblyName.deps.json") `
        -Raw
    $dependencyManifest = $dependencyManifestText | ConvertFrom-Json
    $expectedRuntimeTarget = '.NETCoreApp,Version=v10.0'
    $targetNames = @($dependencyManifest.targets.PSObject.Properties.Name)
    if ([string]$dependencyManifest.runtimeTarget.name -cne $expectedRuntimeTarget -or
        $targetNames.Count -ne 1 -or
        [string]$targetNames[0] -cne $expectedRuntimeTarget -or
        $dependencyManifestText -match '"runtimeTargets"\s*:' -or
        $dependencyManifestText -match '"native"\s*:') {
        throw "Published application '$AssemblyName' is not portable net10.0 managed IL."
    }

    foreach ($file in Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Force) {
        $relative = $file.FullName.Substring($PublishRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        if ($file.Extension -notin @('.dll', '.json', '.txt') -or
            $file.Name -match '^(?i:appsettings.*\.json|hostfxr.*|hostpolicy.*|coreclr.*)$' -or
            $file.Extension -eq '.so') {
            throw "Published application '$AssemblyName' contains a disallowed runtime/configuration file '$relative'."
        }
    }
}

function Assert-NoSensitivePayloadNames {
    param([Parameter(Mandatory)][string]$Root)

    $forbidden = @(
        '*.pfx', '*.p12', '*.pem', '*.key', '*.crt', '.env', '.env.*',
        'appsettings.Local.json', '*runtime-token*', '*connection-string*', '*credentials*'
    )
    foreach ($pattern in $forbidden) {
        $match = Get-ChildItem -LiteralPath $Root -Recurse -Force -File -Filter $pattern | Select-Object -First 1
        if ($null -ne $match) {
            throw "Evidence payload contains forbidden sensitive filename '$($match.FullName)'."
        }
    }
}

function Get-SensitivePayloadScan {
    param([Parameter(Mandatory)][string]$Root)

    $patterns = @(
        '-----BEGIN [^-]*PRIVATE KEY-----',
        '(?i)\b(password|pwd|client_secret|access_token|refresh_token)\s*["'']?\s*[:=]\s*["'']?(?!\$|<|REDACTED|null|none)[A-Za-z0-9+/_=-]{8,}',
        '(?i)\bBearer\s+[A-Za-z0-9._~+/-]{16,}'
    )
    $textExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @('.json', '.txt', '.xml', '.config', '.md', '.sha256')) {
        [void]$textExtensions.Add($extension)
    }

    $scanned = 0
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        if (-not $textExtensions.Contains($file.Extension)) {
            continue
        }
        $scanned++
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($pattern in $patterns) {
            if ($content -match $pattern) {
                throw "Sensitive payload scan detected a prohibited value class in '$($file.Name)'; matched content is intentionally omitted."
            }
        }
    }

    return [ordered]@{
        schemaVersion = 1
        status = 'pass'
        filenamePolicy = 'explicit-forbidden-name-and-published-file-allowlist'
        contentPolicy = 'bounded-generated-text-pattern-scan'
        textFilesScanned = $scanned
        binaryResourceLimitation = 'Managed assemblies are selected build outputs and are not semantically decompiled by this bounded scan.'
        findingCount = 0
    }
}

function Assert-SourceEvidenceUnchanged {
    param(
        [Parameter(Mandatory)][object[]]$Before,
        [Parameter(Mandatory)][object[]]$After
    )

    $beforeValue = ($Before | ForEach-Object { "$($_.path)|$($_.sha256)" }) -join "`n"
    $afterValue = ($After | ForEach-Object { "$($_.path)|$($_.sha256)" }) -join "`n"
    if ($beforeValue -cne $afterValue) {
        throw 'Source inputs changed during restore/publish; refusing to seal mixed-state evidence.'
    }
}

function Remove-TreeNoFollow {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $attributes = [IO.File]::GetAttributes($Path)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            [IO.Directory]::Delete($Path, $false)
        }
        else {
            [IO.File]::Delete($Path)
        }
        return
    }
    if (($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
        [IO.File]::SetAttributes($Path, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($Path)
        return
    }

    foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($Path)) {
        Remove-TreeNoFollow -Path $child
    }
    [IO.Directory]::Delete($Path, $false)
}

function Get-ArtifactRetentionPlan {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$CandidateVersion,
        [Parameter(Mandatory)][ValidateRange(5, 10)][int]$Keep,
        [Parameter(Mandatory)][ValidateSet('build', 'package', 'snapshot')][string]$Kind,
        [switch]$CandidateIsProspective
    )

    $rootPath = (Resolve-Path -LiteralPath $Root).Path
    $candidateVersionInfo = ConvertTo-PegasusReleaseVersion -Version $CandidateVersion
    $candidates = @()
    foreach ($directory in Get-ChildItem -LiteralPath $rootPath -Directory -Force) {
        try {
            $versionInfo = ConvertTo-PegasusReleaseVersion -Version $directory.Name
        }
        catch {
            continue
        }
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Retention candidate is a reparse point: '$($directory.FullName)'."
        }

        $markerPath = Join-Path $directory.FullName '.pegasus-retention.json'
        if (-not (Test-Path -LiteralPath $markerPath)) {
            continue
        }
        Assert-RegularFile -Path $markerPath | Out-Null
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.verified -ne $true -or
            [string]$marker.kind -cne $Kind -or
            [string]$marker.releaseVersion -cne $versionInfo.Canonical) {
            continue
        }
        $candidates += [pscustomobject]@{
            Directory = $directory
            Version = $versionInfo
            Prospective = $false
        }
    }

    if ($CandidateIsProspective) {
        if (@($candidates | Where-Object { $_.Version.Canonical -ceq $candidateVersionInfo.Canonical }).Count -ne 0) {
            throw "The prospective retention version is already registered: '$CandidateVersion'."
        }
        $candidates += [pscustomobject]@{
            Directory = $null
            Version = $candidateVersionInfo
            Prospective = $true
        }
    }

    $ordered = @($candidates |
        Sort-Object -Property @{ Expression = { $_.Version.SortKey }; Descending = $true })
    $retained = @($ordered | Select-Object -First $Keep)
    if (@($retained | Where-Object { $_.Version.Canonical -ceq $candidateVersionInfo.Canonical }).Count -ne 1) {
        throw "Release '$CandidateVersion' is older than the $Keep versions retained by policy."
    }

    $expired = @($ordered | Select-Object -Skip $Keep)
    foreach ($entry in $expired) {
        if ($entry.Prospective -or $null -eq $entry.Directory) {
            throw 'The retention plan would remove the version currently being built.'
        }
        Assert-ContainedPath -Root $rootPath -Candidate $entry.Directory.FullName | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $rootPath -Candidate $entry.Directory.FullName
        Assert-ExclusiveWriterDirectory -Path $entry.Directory.FullName -AllowInheritedRules
    }

    return [pscustomobject]@{
        CandidateVersion = $candidateVersionInfo.Canonical
        Keep = $Keep
        ExistingVerifiedCount = @($candidates | Where-Object { -not $_.Prospective }).Count
        ExpiredDirectories = @($expired | ForEach-Object { $_.Directory.FullName })
    }
}

function Remove-OwnedStagingRoot {
    param(
        [Parameter(Mandatory)][string]$StagingParent,
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$ExpectedMarkerValue
    )

    if (-not (Test-Path -LiteralPath $StagingRoot)) {
        return
    }
    Assert-ContainedPath -Root $StagingParent -Candidate $StagingRoot | Out-Null
    $marker = Join-Path $StagingRoot '.cortex-evidence-staging'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw "Refusing to remove unmarked staging root '$StagingRoot'."
    }
    Assert-RegularFile -Path $marker | Out-Null
    if ([IO.File]::ReadAllText($marker, [Text.Encoding]::UTF8) -cne $ExpectedMarkerValue) {
        throw "Refusing to remove staging root with a mismatched ownership marker: '$StagingRoot'."
    }
    Assert-NoReparseAncestors -ExistingRoot $StagingParent -Candidate $StagingRoot
    Assert-ExclusiveWriterDirectory -Path $StagingRoot

    # Keep the ownership marker until every other child has been removed. A
    # busy compiler output therefore remains safely identifiable for retry.
    foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($StagingRoot)) {
        if (-not $child.Equals($marker, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-TreeNoFollow -Path $child
        }
    }
    $remaining = @([IO.Directory]::EnumerateFileSystemEntries($StagingRoot))
    if ($remaining.Count -ne 1 -or
        -not ([string]$remaining[0]).Equals($marker, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Task-owned staging root still contains build material: '$StagingRoot'."
    }
    Remove-TreeNoFollow -Path $marker
    [IO.Directory]::Delete($StagingRoot, $false)
    if (Test-Path -LiteralPath $StagingRoot) {
        throw "Task-owned staging root was recreated during bounded cleanup: '$StagingRoot'."
    }
}

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
Assert-NoReparseAncestors -ExistingRoot $root -Candidate $root
$versionManifestPath = Join-Path $root 'VERSION.json'
$versionManifest = Get-Content -LiteralPath $versionManifestPath -Raw | ConvertFrom-Json
$releaseVersion = [string]$versionManifest.releaseVersion
$nuGetVersion = [string]$versionManifest.nuGetVersion
$releaseTimestamp = Get-ReleaseTimestamp -ReleaseVersion $releaseVersion

if ($RuntimeIdentifier -ne 'linux-arm64') {
    throw "This evidence builder is intentionally limited to linux-arm64; received '$RuntimeIdentifier'."
}
if ($RuntimeVersion -ne '10.0.10') {
    throw "This checkpoint requires the reviewed .NET runtime version 10.0.10; received '$RuntimeVersion'."
}
$officialRuntimeUri = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.10/dotnet-runtime-10.0.10-linux-arm64.tar.gz'
$officialRuntimeSha512 = '3ea2ac626fdb0f26bb52f02584ae5e68ee88c275e740b34d93918ad380985d0dbfa1632c017658dfddc5fba396114b62748efc11b25897877814a4a4c5022edc'
if ($RuntimeSourceUri -cne $officialRuntimeUri -or $RuntimeArchiveSha512.ToLowerInvariant() -cne $officialRuntimeSha512) {
    throw 'This checkpoint accepts only the reviewed official .NET 10.0.10 linux-arm64 archive identity.'
}

$runtimeArchive = (Resolve-Path -LiteralPath $RuntimeArchivePath).Path
$runtimeArchiveItem = Assert-RegularFile -Path $runtimeArchive
if ($runtimeArchiveItem.Length -gt 200MB) {
    throw "Runtime archive exceeds the 200 MiB acquisition boundary: $($runtimeArchiveItem.Length) bytes."
}
$actualRuntimeSha512 = (Get-FileHash -LiteralPath $runtimeArchive -Algorithm SHA512).Hash.ToLowerInvariant()
if ($actualRuntimeSha512 -cne $RuntimeArchiveSha512.ToLowerInvariant()) {
    throw "Runtime archive SHA-512 mismatch. Expected '$RuntimeArchiveSha512'; received '$actualRuntimeSha512'."
}
$actualRuntimeSha256 = (Get-FileHash -LiteralPath $runtimeArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeReleaseMetadata = (Resolve-Path -LiteralPath $RuntimeReleaseMetadataPath).Path
$runtimeReleaseMetadataItem = Assert-RegularFile -Path $runtimeReleaseMetadata
$runtimeMetadataEvidence = Get-RuntimeReleaseMetadataEvidence `
    -MetadataPath $runtimeReleaseMetadata `
    -RuntimeVersion $RuntimeVersion `
    -RuntimeIdentifier $RuntimeIdentifier `
    -SourceUri $RuntimeSourceUri `
    -ExpectedSha512 $actualRuntimeSha512
$nugetConfigurationPath = Join-Path $root 'NuGet.Config'
$approvedNugetConfigurationSha256 = 'e8f0d0aa683119856949a2807f812c21e12f5d4b951853a66df338fd046e5bf2'
if ((Get-FileHash -LiteralPath $nugetConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
    $approvedNugetConfigurationSha256) {
    throw 'NuGet.Config does not match the reviewed exact local-source and source-mapping policy.'
}
$allowedPackagesRoot = (Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $root) '_packages')).Path
Assert-OfflineNuGetConfiguration -ConfigurationPath $nugetConfigurationPath -AllowedPackagesRoot $allowedPackagesRoot
$pegasusPackageFeedSource = (Resolve-Path -LiteralPath (Join-Path $allowedPackagesRoot 'Pegasus')).Path
$externalPackageFeedSource = (Resolve-Path -LiteralPath (Join-Path $allowedPackagesRoot 'External')).Path
$pegasusPackages = @(
    [ordered]@{
        id = 'Pegasus.Shared'
        version = '1.20260711.421-a'
        fileName = 'pegasus.shared.1.20260711.421-a.nupkg'
        bytes = 114237
        sha256 = '9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6'
        use = 'build-and-runtime'
    })
$externalPackages = @(
    [ordered]@{
        id = 'Npgsql'
        version = '10.0.3'
        fileName = 'npgsql.10.0.3.nupkg'
        bytes = 1847911
        sha256 = '75d0970923a8c9fcbbd37e4ebe72fee0b10362a1e36723e86777df1b6728316d'
        use = 'build-and-runtime'
    },
    [ordered]@{
        id = 'Microsoft.Extensions.Logging.Abstractions'
        version = '10.0.0'
        fileName = 'microsoft.extensions.logging.abstractions.10.0.0.nupkg'
        bytes = 822954
        sha256 = '067860199734d47c134b1a206afabb51eab857b88c037c0f9db7d1c10e118469'
        use = 'build-and-runtime'
    },
    [ordered]@{
        id = 'Microsoft.Extensions.DependencyInjection.Abstractions'
        version = '10.0.0'
        fileName = 'microsoft.extensions.dependencyinjection.abstractions.10.0.0.nupkg'
        bytes = 285647
        sha256 = 'f62a1d5cfdfd62a831a269ce3cec5dfe6cdb1b425f3925f3168354de8993d8fb'
        use = 'build-and-runtime'
    },
    [ordered]@{
        id = 'Microsoft.NETCore.App.Ref'
        version = '8.0.29'
        fileName = 'microsoft.netcore.app.ref.8.0.29.nupkg'
        bytes = 6767675
        sha256 = '77102e11453554e1254380a92fd1cb855e0a545b510c07194bc5b4193d838ba8'
        use = 'build-only-reference-pack'
    },
    [ordered]@{
        id = 'Microsoft.AspNetCore.App.Ref'
        version = '8.0.29'
        fileName = 'microsoft.aspnetcore.app.ref.8.0.29.nupkg'
        bytes = 4371147
        sha256 = 'd302b92ec6b86b56a78bf82f4b3b2acae637478301b87ffd5cec997960945a85'
        use = 'build-only-reference-pack'
    },
    [ordered]@{
        id = 'Microsoft.WindowsDesktop.App.Ref'
        version = '8.0.29'
        fileName = 'microsoft.windowsdesktop.app.ref.8.0.29.nupkg'
        bytes = 4446999
        sha256 = 'f51c7176f5a91998657ffdd6cc551eb5d2193f02a7ca1ee4c4821512728ef641'
        use = 'build-only-reference-pack'
    })

$volumeRoot = [IO.Path]::GetPathRoot($root)
if ([string]::IsNullOrWhiteSpace($volumeRoot) -or $volumeRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'The sealed evidence builder requires a local same-volume evidence root.'
}
Assert-TrustedVolumeParent -Path $volumeRoot
$secureProductRoot = Join-Path $volumeRoot '.pegasus-cortex'
Initialize-ExclusiveWriterDirectory -Path $secureProductRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $secureProductRoot 'evidence'
}
$resolvedOutputRoot = Resolve-AbsolutePath -BasePath $secureProductRoot -Path $OutputRoot
Assert-ContainedPath -Root $secureProductRoot -Candidate $resolvedOutputRoot | Out-Null
Initialize-ExclusiveWriterDirectory -Path $resolvedOutputRoot
Assert-NoReparseAncestors -ExistingRoot $volumeRoot -Candidate $resolvedOutputRoot
$versionRoot = Join-Path $resolvedOutputRoot $releaseVersion
$leasePath = Join-Path $resolvedOutputRoot '.builder.lock'
if (Test-Path -LiteralPath $versionRoot) {
    throw "Immutable evidence version root already exists: '$versionRoot'."
}
$leaseStream = $null

if ([string]::IsNullOrWhiteSpace($ReleaseToolsPath)) {
    $ReleaseToolsPath = Join-Path (Split-Path -Parent $root) 'Pegasus Shared\tools\Pegasus.ReleaseTools.psm1'
}
$resolvedReleaseToolsPath = (Resolve-Path -LiteralPath $ReleaseToolsPath).Path
$approvedReleaseToolsSha256 = '43559735b56d86c83cae408ccff4d7824da6a30650a5d39b274fe9890ce7d115'
if ($ReleaseToolsSha256.ToLowerInvariant() -cne $approvedReleaseToolsSha256) {
    throw 'The release-tools hash override is not approved for this checkpoint.'
}
$actualReleaseToolsSha256 = (Get-FileHash -LiteralPath $resolvedReleaseToolsPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualReleaseToolsSha256 -cne $ReleaseToolsSha256.ToLowerInvariant()) {
    throw "Pegasus release-tools SHA-256 mismatch. Expected '$ReleaseToolsSha256'; received '$actualReleaseToolsSha256'."
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$expectedDotnet = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet\dotnet.exe'
if (-not $dotnet.Equals($expectedDotnet, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The build requires the protected machine dotnet host '$expectedDotnet'; resolved '$dotnet'."
}
Assert-NoReparseAncestors -ExistingRoot ([IO.Path]::GetPathRoot($dotnet)) -Candidate $dotnet
$dotnetSha256 = (Get-FileHash -LiteralPath $dotnet -Algorithm SHA256).Hash.ToLowerInvariant()
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
    throw "This checkpoint pins Windows PowerShell 5.1; received '$($PSVersionTable.PSVersion)'."
}
$sdkPolicy = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json
$expectedSdkVersion = [string]$sdkPolicy.sdk.version
if ($expectedSdkVersion -cne '10.0.400' -or [string]$sdkPolicy.sdk.rollForward -cne 'latestPatch') {
    throw 'This checkpoint requires the .NET SDK 10.0.400 latestPatch policy.'
}
$actualSdkVersion = $null
$stagingBase = Join-Path $secureProductRoot 'evidence-staging'
Initialize-ExclusiveWriterDirectory -Path $stagingBase
Assert-NoReparseAncestors -ExistingRoot $volumeRoot -Candidate $stagingBase
$stagingRoot = Join-Path $stagingBase ([guid]::NewGuid().ToString('N'))
$finalOutputRoot = Join-Path $stagingRoot 'final'
$applicationArchiveName = "pegasus-cortex-arm64-evidence-$releaseVersion.zip"
$applicationArchivePath = Join-Path $finalOutputRoot $applicationArchiveName
$runtimeBundleArchiveName = "pegasus-dotnet-runtime-$RuntimeVersion-$RuntimeIdentifier-$releaseVersion.zip"
$runtimeBundleArchivePath = Join-Path $finalOutputRoot $runtimeBundleArchiveName
$releaseManifestPath = Join-Path $finalOutputRoot 'cortex-arm64-evidence-release-manifest.json'
$applicationBundleRoot = Join-Path $stagingRoot 'cortex-arm64-evidence'
$runtimeBundleRoot = Join-Path $stagingRoot 'pegasus-dotnet-runtime'
$runtimeRoot = Join-Path $runtimeBundleRoot 'runtime'
$appsRoot = Join-Path $applicationBundleRoot 'apps'
$applicationEvidenceRoot = Join-Path $applicationBundleRoot 'evidence'
$privatePkiEvidenceRoot = Join-Path $applicationEvidenceRoot 'postgresql-private-pki'
$runtimeEvidenceRoot = Join-Path $runtimeBundleRoot 'evidence'
$verifiedInputsRoot = Join-Path $stagingRoot 'verified-inputs'
$protectedPackageFeedRoot = Join-Path $verifiedInputsRoot 'nuget-feed'
$protectedPegasusPackageFeed = Join-Path $protectedPackageFeedRoot 'Pegasus'
$protectedExternalPackageFeed = Join-Path $protectedPackageFeedRoot 'External'
$nugetCacheRoot = Join-Path $stagingRoot 'nuget-cache'
$dotnetArtifactsRoot = Join-Path $stagingRoot 'dotnet-artifacts'
$stagedSourceRoot = Join-Path $stagingRoot 'source'
$processTempRoot = Join-Path $stagingRoot 'temp'
$dotnetHomeRoot = Join-Path $stagingRoot 'dotnet-home'
$trustedSystemRoot = Split-Path -Parent ([Environment]::SystemDirectory)
$trustedSystem32 = Join-Path $trustedSystemRoot 'System32'
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
if ([string]::IsNullOrWhiteSpace($trustedSystemRoot) -or
    [string]::IsNullOrWhiteSpace($programFiles) -or
    -not (Test-Path -LiteralPath $trustedSystem32 -PathType Container)) {
    throw 'Unable to resolve the trusted Windows build environment roots.'
}
$previousEnvironment = [ordered]@{}
foreach ($entry in [Environment]::GetEnvironmentVariables(
        [EnvironmentVariableTarget]::Process).GetEnumerator()) {
    $previousEnvironment[[string]$entry.Key] = [string]$entry.Value
}
if (Test-Path -LiteralPath $leasePath) {
    Assert-RegularFile -Path $leasePath | Out-Null
}
$leaseStream = [IO.File]::Open($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
if (Test-Path -LiteralPath $versionRoot) {
    $leaseStream.Dispose()
    throw "Immutable evidence version root already exists: '$versionRoot'."
}
$primaryFailure = $null
$locationPushed = $false
$committedVersionRoot = $null
$retentionRegistered = $false
try {
    Assert-NoReparseAncestors -ExistingRoot $stagingBase -Candidate $stagingRoot
    New-ExclusiveWriterDirectory -Path $stagingRoot
    Assert-NoReparseAncestors -ExistingRoot $stagingBase -Candidate $stagingRoot
    [IO.File]::WriteAllText((Join-Path $stagingRoot '.cortex-evidence-staging'), $releaseVersion, [Text.UTF8Encoding]::new($false))
    New-ExclusiveWriterDirectory -Path $runtimeBundleRoot
    foreach ($path in @(
            $applicationBundleRoot,
            $appsRoot,
            $applicationEvidenceRoot,
            $runtimeEvidenceRoot,
            $verifiedInputsRoot,
            $finalOutputRoot,
            $nugetCacheRoot,
            $dotnetArtifactsRoot,
            $processTempRoot,
            $dotnetHomeRoot,
            (Join-Path $dotnetHomeRoot 'AppData\Local'),
            (Join-Path $dotnetHomeRoot 'AppData\Roaming'))) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    New-ExclusiveWriterDirectory -Path $privatePkiEvidenceRoot
    Assert-ExclusiveWriterDirectory -Path $runtimeBundleRoot
    Assert-ExclusiveWriterDirectory -Path $runtimeEvidenceRoot -AllowInheritedRules
    $buildEnvironment = [ordered]@{
        SystemDrive = [IO.Path]::GetPathRoot($trustedSystemRoot).TrimEnd('\')
        SystemRoot = $trustedSystemRoot
        WINDIR = $trustedSystemRoot
        ComSpec = Join-Path $trustedSystem32 'cmd.exe'
        PATH = "$(Split-Path -Parent $dotnet);$trustedSystem32;$trustedSystemRoot"
        PATHEXT = '.COM;.EXE;.BAT;.CMD'
        OS = 'Windows_NT'
        NUMBER_OF_PROCESSORS = [Environment]::ProcessorCount.ToString([Globalization.CultureInfo]::InvariantCulture)
        ProgramFiles = $programFiles
        ProgramW6432 = $programFiles
        USERPROFILE = $dotnetHomeRoot
        HOME = $dotnetHomeRoot
        HOMEDRIVE = [IO.Path]::GetPathRoot($dotnetHomeRoot).TrimEnd('\')
        HOMEPATH = $dotnetHomeRoot.Substring([IO.Path]::GetPathRoot($dotnetHomeRoot).Length - 1)
        LOCALAPPDATA = Join-Path $dotnetHomeRoot 'AppData\Local'
        APPDATA = Join-Path $dotnetHomeRoot 'AppData\Roaming'
        TEMP = $processTempRoot
        TMP = $processTempRoot
        DOTNET_ROOT = Split-Path -Parent $dotnet
        DOTNET_CLI_HOME = $dotnetHomeRoot
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        DOTNET_ROLL_FORWARD_TO_PRERELEASE = '0'
        DOTNET_MULTILEVEL_LOOKUP = '0'
        MSBUILDDISABLENODEREUSE = '1'
        MSBuildEnableWorkloadResolver = 'false'
        NUGET_PACKAGES = "$nugetCacheRoot$([IO.Path]::DirectorySeparatorChar)"
        NUGET_HTTP_CACHE_PATH = Join-Path $stagingRoot 'nuget-http-disabled'
        NUGET_XMLDOC_MODE = 'skip'
    }
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $buildEnvironment['ProgramFiles(x86)'] = $programFilesX86
    }
    Set-ExactProcessEnvironment -Variables $buildEnvironment

    $stagedRuntimeArchive = Join-Path $verifiedInputsRoot $runtimeArchiveItem.Name
    $stagedMetadataPath = Join-Path $verifiedInputsRoot 'dotnet-releases-10.0.json'
    $stagedReleaseToolsPath = Join-Path $verifiedInputsRoot 'Pegasus.ReleaseTools.psm1'
    Copy-Item -LiteralPath $runtimeArchive -Destination $stagedRuntimeArchive
    Copy-Item -LiteralPath $runtimeReleaseMetadata -Destination $stagedMetadataPath
    Copy-Item -LiteralPath $resolvedReleaseToolsPath -Destination $stagedReleaseToolsPath
    if ((Get-FileHash -LiteralPath $stagedRuntimeArchive -Algorithm SHA512).Hash.ToLowerInvariant() -cne $actualRuntimeSha512 -or
        (Get-FileHash -LiteralPath $stagedMetadataPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runtimeMetadataEvidence.metadataSha256 -or
        (Get-FileHash -LiteralPath $stagedReleaseToolsPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $actualReleaseToolsSha256) {
        throw 'A verified build input changed while it was copied into owned staging.'
    }
    $runtimeMetadataEvidence = Get-RuntimeReleaseMetadataEvidence `
        -MetadataPath $stagedMetadataPath `
        -RuntimeVersion $RuntimeVersion `
        -RuntimeIdentifier $RuntimeIdentifier `
        -SourceUri $RuntimeSourceUri `
        -ExpectedSha512 $actualRuntimeSha512
    Import-Module $stagedReleaseToolsPath -Force
    $sourceInputsBefore = @(Get-SourceInputEvidence -Root $root)
    Copy-SourceInputsToProtectedRoot `
        -SourceRoot $root `
        -DestinationRoot $stagedSourceRoot `
        -ExpectedEvidence $sourceInputsBefore
    $sourceInputsAfterCopy = @(Get-SourceInputEvidence -Root $root)
    Assert-SourceEvidenceUnchanged -Before $sourceInputsBefore -After $sourceInputsAfterCopy
    $privatePkiOperatorGuidePath = Join-Path $privatePkiEvidenceRoot 'OPERATOR.md'
    $privatePkiSchemaPath = Join-Path $privatePkiEvidenceRoot 'postgresql-private-pki-revocation.v1.schema.json'
    $privatePkiRequirementsPath = Join-Path $privatePkiEvidenceRoot 'postgresql-rehearsal-v3-requirements.json'
    Copy-FileToProtectedPath `
        -SourcePath (Join-Path $stagedSourceRoot 'docs\POSTGRESQL_PRIVATE_PKI_REVOCATION_V1.md') `
        -DestinationPath $privatePkiOperatorGuidePath | Out-Null
    Copy-FileToProtectedPath `
        -SourcePath (Join-Path $stagedSourceRoot 'schemas\postgresql-private-pki-revocation.v1.schema.json') `
        -DestinationPath $privatePkiSchemaPath | Out-Null
    Write-Utf8Json -Path $privatePkiRequirementsPath -Value ([ordered]@{
        schemaVersion = 1
        rehearsalVersion = 3
        profile = 'pegasus.cortex.postgresql.private-ca-crl/1'
        scope = 'contained-local-arm64-postgresql-private-pki-validation'
        productionRevocationOwner = 'external-authorized-platform-operator'
        cortexRuntimeOwnsResponder = $false
        fixtureResponder = [ordered]@{
            role = 'test-only-task-local-loopback-crl-responder'
            persistent = $false
            network = 'literal-loopback-http-only'
            boundedRequests = $true
            boundedResponseBytes = $true
            boundedTimeouts = $true
            cancellationRequired = $true
            deterministicCleanupRequired = $true
            pollingAllowed = $false
        }
        requiredPublicInputs = @(
            'private-CA root certificate',
            'PostgreSQL server certificate',
            'signed fresh CRL',
            'postgresql-private-pki-revocation v1 manifest')
        prohibitedInputs = @(
            'private keys',
            'database credentials in evidence',
            'bearer or service tokens',
            'host trust-store mutation',
            'Trust Server Certificate',
            'revocation bypass')
        endpointAccounting = @(
            [ordered]@{
                id = 'postgresql-tls'
                transport = 'tls'
                address = 'literal-loopback-ip'
                portMustDifferFrom = @('crl-http')
            },
            [ordered]@{
                id = 'crl-http'
                transport = 'http'
                address = 'literal-loopback-ip'
                portMustDifferFrom = @('postgresql-tls')
            })
        certificateRequirements = @(
            'server certificate chains to the exact private-CA root',
            'server certificate contains every configured literal IP SAN',
            'server certificate contains exactly the manifest CRL distribution point',
            'CRL issuer and signature match the private-CA root',
            'CRL is fresh and the server certificate serial is not revoked')
        connectionRequirements = [ordered]@{
            provider = 'Npgsql'
            sslMode = 'VerifyFull'
            rootCertificateRequired = $true
            checkCertificateRevocation = $true
            trustServerCertificate = $false
        }
        requiredReceiptEvidence = @(
            'manifest and CRL SHA-256 values',
            'PostgreSQL and CRL endpoint ports accounted separately',
            'responder process listener request and cleanup counts',
            'successful Npgsql pre-authentication TLS and revocation verification',
            'zero remaining fixture processes listeners roots and credentials')
        prohibitedActions = @(
            'application lifecycle rehearsal',
            'install or deployment',
            'persistent trust or certificate changes',
            'hardware or production-data mutation')
    })
    $stagedVersionManifest = Get-Content `
        -LiteralPath (Join-Path $stagedSourceRoot 'VERSION.json') `
        -Raw | ConvertFrom-Json
    if ([string]$stagedVersionManifest.releaseVersion -cne $releaseVersion -or
        [string]$stagedVersionManifest.nuGetVersion -cne $nuGetVersion -or
        (Get-ReleaseTimestamp -ReleaseVersion ([string]$stagedVersionManifest.releaseVersion)) -cne $releaseTimestamp) {
        throw 'Release identity read before staging does not match the protected source copy.'
    }
    $versionManifest = $stagedVersionManifest
    $artifactRetentionCount = [int]$versionManifest.artifactRetentionCount
    if ($artifactRetentionCount -lt 1 -or $artifactRetentionCount -gt 10) {
        throw 'The protected source artifact-retention count is outside the supported range of 1 through 10.'
    }
    New-ExclusiveWriterDirectory -Path $protectedPackageFeedRoot
    $protectedPackageEvidence = @()
    $protectedPackageEvidence += @(Copy-VerifiedPackageFeed `
            -SourceRoot $pegasusPackageFeedSource `
            -DestinationRoot $protectedPegasusPackageFeed `
            -Packages $pegasusPackages)
    $protectedPackageEvidence += @(Copy-VerifiedPackageFeed `
            -SourceRoot $externalPackageFeedSource `
            -DestinationRoot $protectedExternalPackageFeed `
            -Packages $externalPackages)
    $protectedNugetConfigurationPath = Join-Path $verifiedInputsRoot 'NuGet.protected.Config'
    New-ProtectedNuGetConfiguration `
        -SourcePath (Join-Path $stagedSourceRoot 'NuGet.Config') `
        -DestinationPath $protectedNugetConfigurationPath `
        -SourceOverrides ([ordered]@{
            PegasusLocal = $protectedPegasusPackageFeed
            ExternalLocal = $protectedExternalPackageFeed
        }) `
        -ExpectedPackageMappings ([ordered]@{
            PegasusLocal = @('Pegasus.*')
            ExternalLocal = @(
                'Npgsql',
                'Microsoft.Extensions.Logging.Abstractions',
                'Microsoft.Extensions.DependencyInjection.Abstractions',
                'Microsoft.NETCore.App.Ref',
                'Microsoft.AspNetCore.App.Ref',
                'Microsoft.WindowsDesktop.App.Ref')
        }) `
        -AllowedPackagesRoot $protectedPackageFeedRoot
    $protectedNugetConfigurationSha256 = (Get-FileHash `
            -LiteralPath $protectedNugetConfigurationPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $pegasusPackageFeed = $protectedPegasusPackageFeed
    Push-Location -LiteralPath $stagedSourceRoot
    $locationPushed = $true
    $stagedSdkPolicy = Get-Content `
        -LiteralPath (Join-Path $stagedSourceRoot 'global.json') `
        -Raw | ConvertFrom-Json
    $stagedExpectedSdkVersion = [string]$stagedSdkPolicy.sdk.version
    if ($stagedExpectedSdkVersion -cne '10.0.400' -or
        [string]$stagedSdkPolicy.sdk.rollForward -cne 'latestPatch' -or
        $stagedSdkPolicy.sdk.allowPrerelease -ne $false) {
        throw 'The protected source does not retain the reviewed .NET SDK policy.'
    }
    $stagedActualSdkVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $stagedActualSdkVersion -notmatch '^10\.0\.4\d{2}$' -or
        [version]$stagedActualSdkVersion -lt [version]$stagedExpectedSdkVersion) {
        throw "Protected source requires SDK '$stagedExpectedSdkVersion' latestPatch; received '$stagedActualSdkVersion'."
    }
    $actualSdkVersion = $stagedActualSdkVersion

    $archiveToolSourceRoot = Join-Path $stagedSourceRoot 'tools\PegasusCortex.ArchiveTool'
    $archiveToolSourceEvidence = Get-DirectoryDigestEvidence `
        -Root $archiveToolSourceRoot `
        -ExcludedDirectoryNames @('bin', 'obj')
    $approvedArchiveToolSourceSha256 = '582ee14698db44853fdea5212aa0ed8383535cda02f742d40554d41ef28892ad'
    if ($archiveToolSourceEvidence.FileCount -ne 13 -or
        $archiveToolSourceEvidence.Sha256 -cne $approvedArchiveToolSourceSha256) {
        throw 'ArchiveTool source does not match the independently reviewed frozen 13-file boundary.'
    }
    $archiveToolExecutionRuntime = Get-DotNetRuntimeEvidence `
        -DotNetPath $dotnet `
        -RuntimeVersion $RuntimeVersion

    $archiveToolProjectPath = Join-Path $stagedSourceRoot 'tools\PegasusCortex.ArchiveTool\src\PegasusCortex.ArchiveTool\PegasusCortex.ArchiveTool.csproj'
    $archiveToolLockPath = Join-Path (Split-Path -Parent $archiveToolProjectPath) 'packages.lock.json'
    $archiveToolTestProjectPath = Join-Path $stagedSourceRoot 'tools\PegasusCortex.ArchiveTool\tests\PegasusCortex.ArchiveTool.Tests\PegasusCortex.ArchiveTool.Tests.csproj'
    $archiveToolTestLockPath = Join-Path (Split-Path -Parent $archiveToolTestProjectPath) 'packages.lock.json'
    foreach ($path in @($archiveToolProjectPath, $archiveToolLockPath, $archiveToolTestProjectPath, $archiveToolTestLockPath)) {
        Assert-RegularFile -Path $path | Out-Null
    }
    $archiveToolPublishRoot = Join-Path $dotnetArtifactsRoot 'archive-tool-publish'
    New-Item -ItemType Directory -Path $archiveToolPublishRoot | Out-Null
    Invoke-DotNet -DotNetPath $dotnet -Operation 'Locked restore for ArchiveTool' -Arguments @(
        'restore', $archiveToolProjectPath,
        '--locked-mode',
        '--no-cache',
        '--configfile', $protectedNugetConfigurationPath,
        '--artifacts-path', $dotnetArtifactsRoot,
        '-p:NuGetAudit=false',
        '-p:RestoreIgnoreFailedSources=false',
        "-p:PegasusPackageFeed=$pegasusPackageFeed",
        '-p:RestoreAdditionalProjectSources=')
    Invoke-DotNet -DotNetPath $dotnet -Operation 'Portable publish for ArchiveTool' -Arguments @(
        'publish', $archiveToolProjectPath,
        '-c', $Configuration,
        '--self-contained', 'false',
        '--no-restore',
        '--artifacts-path', $dotnetArtifactsRoot,
        '-p:UseAppHost=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:UseSharedCompilation=false',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-p:PublishAot=false',
        '-p:PublishTrimmed=false',
        "-p:PegasusPackageFeed=$pegasusPackageFeed",
        '-o', $archiveToolPublishRoot)
    Set-PublishedRuntimePin `
        -PublishRoot $archiveToolPublishRoot `
        -AssemblyName 'PegasusCortex.ArchiveTool' `
        -RuntimeVersion $RuntimeVersion
    $archiveToolPath = Get-PublishedArchiveToolPath `
        -PublishRoot $archiveToolPublishRoot `
        -AssemblyName 'PegasusCortex.ArchiveTool' `
        -RuntimeVersion $RuntimeVersion
    $archiveToolSha256 = (Get-FileHash -LiteralPath $archiveToolPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveToolRuntimeConfigurationPath = Join-Path $archiveToolPublishRoot 'PegasusCortex.ArchiveTool.runtimeconfig.json'
    $archiveToolRuntimeConfigurationSha256 = (Get-FileHash -LiteralPath $archiveToolRuntimeConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveToolProjectSha256 = (Get-FileHash -LiteralPath $archiveToolProjectPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveToolLockSha256 = (Get-FileHash -LiteralPath $archiveToolLockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveToolTestProjectSha256 = (Get-FileHash -LiteralPath $archiveToolTestProjectPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveToolTestLockSha256 = (Get-FileHash -LiteralPath $archiveToolTestLockPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $archiveToolTestPublishRoot = Join-Path $dotnetArtifactsRoot 'archive-tool-tests-publish'
    New-Item -ItemType Directory -Path $archiveToolTestPublishRoot | Out-Null
    Invoke-DotNet -DotNetPath $dotnet -Operation 'Locked restore for ArchiveTool tests' -Arguments @(
        'restore', $archiveToolTestProjectPath,
        '--locked-mode',
        '--no-cache',
        '--configfile', $protectedNugetConfigurationPath,
        '--artifacts-path', $dotnetArtifactsRoot,
        '-p:NuGetAudit=false',
        '-p:RestoreIgnoreFailedSources=false',
        "-p:PegasusPackageFeed=$pegasusPackageFeed",
        '-p:RestoreAdditionalProjectSources=')
    Invoke-DotNet -DotNetPath $dotnet -Operation 'Portable publish for ArchiveTool tests' -Arguments @(
        'publish', $archiveToolTestProjectPath,
        '-c', $Configuration,
        '--self-contained', 'false',
        '--no-restore',
        '--artifacts-path', $dotnetArtifactsRoot,
        '-p:UseAppHost=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:UseSharedCompilation=false',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-p:PublishAot=false',
        '-p:PublishTrimmed=false',
        "-p:PegasusPackageFeed=$pegasusPackageFeed",
        '-o', $archiveToolTestPublishRoot)
    Set-PublishedRuntimePin `
        -PublishRoot $archiveToolTestPublishRoot `
        -AssemblyName 'PegasusCortex.ArchiveTool.Tests' `
        -RuntimeVersion $RuntimeVersion
    $archiveToolTestAssemblyPath = Join-Path $archiveToolTestPublishRoot 'PegasusCortex.ArchiveTool.Tests.dll'
    Assert-RegularFile -Path $archiveToolTestAssemblyPath | Out-Null
    $archiveToolTestedLibraryPath = Join-Path $archiveToolTestPublishRoot 'PegasusCortex.ArchiveTool.dll'
    Assert-RegularFile -Path $archiveToolTestedLibraryPath | Out-Null
    if ((Get-FileHash -LiteralPath $archiveToolTestedLibraryPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
        $archiveToolSha256) {
        throw 'ArchiveTool focused tests do not reference the exact assembly selected for extraction.'
    }
    if ((Test-Path -LiteralPath (Join-Path $archiveToolTestPublishRoot 'PegasusCortex.ArchiveTool.Tests.exe')) -or
        @(Get-ChildItem -LiteralPath $archiveToolTestPublishRoot -File -Filter '*.pdb').Count -ne 0) {
        throw 'Published ArchiveTool tests contain an apphost or debug symbols.'
    }
    $archiveToolTestResult = Invoke-ArchiveToolTests `
        -DotNetPath $dotnet `
        -TestAssemblyPath $archiveToolTestAssemblyPath `
        -ExpectedCount 23
    $archiveToolTestEvidencePath = Join-Path $runtimeEvidenceRoot 'archive-tool-tests.json'
    Write-Utf8Json -Path $archiveToolTestEvidencePath -Value ([ordered]@{
        schemaVersion = 1
        toolSourceSha256 = $archiveToolSourceEvidence.Sha256
        executionRuntime = $archiveToolExecutionRuntime
        result = $archiveToolTestResult
    })
    $archiveToolTestEvidenceSha256 = (Get-FileHash -LiteralPath $archiveToolTestEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $archiveInventoryPath = Join-Path $runtimeEvidenceRoot 'archive-inventory.json'
    $archiveToolResult = Invoke-ArchiveToolExtraction `
        -DotNetPath $dotnet `
        -ArchiveToolPath $archiveToolPath `
        -ArchivePath $stagedRuntimeArchive `
        -ExpectedArchiveSha256 $actualRuntimeSha256 `
        -DestinationRoot $runtimeBundleRoot `
        -InventoryPath $archiveInventoryPath
    $runtimeInventory = @(Get-VerifiedRuntimeInventory `
        -InventoryPath $archiveInventoryPath `
        -RuntimeRoot $runtimeRoot `
        -RuntimeVersion $RuntimeVersion `
        -ExpectedArchiveSha256 $actualRuntimeSha256 `
        -ExpectedCompressedBytes $runtimeArchiveItem.Length `
        -ToolResult $archiveToolResult)
    $archiveInventorySha256 = (Get-FileHash -LiteralPath $archiveInventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $runtimeModes = @($runtimeInventory |
        Where-Object { [string]$_.kind -eq 'file' } |
        ForEach-Object {
            [ordered]@{
                path = "runtime/$([string]$_.path)"
                mode = [string]$_.mode
            }
        })
    $modeInventoryPath = Join-Path $runtimeEvidenceRoot 'executable-modes.json'
    Write-Utf8Json -Path $modeInventoryPath -Value ([ordered]@{
        schemaVersion = 1
        defaultFileMode = '0644'
        requiredDirectoryMode = '0755'
        files = $runtimeModes
    })
    $modeInventorySha256 = (Get-FileHash -LiteralPath $modeInventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $stagedMetadataPath -Destination (Join-Path $runtimeEvidenceRoot 'dotnet-releases-10.0.json')
    Write-Utf8Json -Path (Join-Path $runtimeEvidenceRoot 'runtime-source.json') -Value ([ordered]@{
        schemaVersion = 1
        artifactId = 'pegasus-dotnet-runtime'
        version = $RuntimeVersion
        runtimeIdentifier = $RuntimeIdentifier
        sourceUri = $RuntimeSourceUri
        archiveName = $runtimeArchiveItem.Name
        archiveBytes = $runtimeArchiveItem.Length
        sha512 = $actualRuntimeSha512
        providedByCaller = $true
        installedSystemWide = $false
        sourceAuthenticity = 'official-release-metadata-url-and-sha512-match'
        sha256 = $actualRuntimeSha256
        releaseMetadata = $runtimeMetadataEvidence
    })
    Write-Utf8Json -Path (Join-Path $runtimeEvidenceRoot 'signature-status.json') -Value ([ordered]@{
        schemaVersion = 1
        artifactId = 'pegasus-dotnet-runtime'
        version = $RuntimeVersion
        runtimeIdentifier = $RuntimeIdentifier
        signatureStatus = 'unsigned'
        approvedSigner = $null
        deploymentAuthorized = $false
        reason = 'The hash-pinned Microsoft runtime payload is local test evidence until an approved Pegasus signer attests this archive.'
    })
    Write-Utf8Json -Path (Join-Path $runtimeEvidenceRoot 'sbom.cdx.json') -Value ([ordered]@{
        bomFormat = 'CycloneDX'
        specVersion = '1.5'
        version = 1
        metadata = [ordered]@{
            timestamp = $releaseTimestamp
            component = [ordered]@{
                type = 'framework'
                name = 'pegasus-dotnet-runtime'
                version = $RuntimeVersion
                'bom-ref' = "pkg:generic/pegasus-dotnet-runtime@${RuntimeVersion}?arch=arm64&os=linux"
            }
        }
        components = @(
            [ordered]@{
                type = 'framework'
                name = 'Microsoft.NETCore.App'
                version = $RuntimeVersion
                licenses = @([ordered]@{ license = [ordered]@{ id = 'MIT' } })
            },
            [ordered]@{
                type = 'file'
                name = $runtimeArchiveItem.Name
                version = $RuntimeVersion
                hashes = @(
                    [ordered]@{ alg = 'SHA-512'; content = $actualRuntimeSha512 },
                    [ordered]@{ alg = 'SHA-256'; content = $actualRuntimeSha256 })
            })
    })

    Assert-NoSensitivePayloadNames -Root $runtimeBundleRoot
    $runtimeExcluded = @('evidence/bundle-manifest.json', 'evidence/checksums.sha256')
    $runtimePayloadFiles = @(Get-RelativeFileEvidence -Root $runtimeBundleRoot -ExcludedRelativePaths $runtimeExcluded)
    $runtimeMaximumPath = ($runtimePayloadFiles | Sort-Object { $_.path.Length } -Descending | Select-Object -First 1).path
    Write-Utf8Json -Path (Join-Path $runtimeEvidenceRoot 'bundle-manifest.json') -Value ([ordered]@{
        schemaVersion = 1
        artifactId = 'pegasus-dotnet-runtime'
        version = $RuntimeVersion
        producerReleaseVersion = $releaseVersion
        createdAt = $releaseTimestamp
        runtimeIdentifier = $RuntimeIdentifier
        provisioningMode = 'installer-managed'
        lifecycleOwner = 'Pegasus Server'
        signatureStatus = 'unsigned'
        deploymentAuthorized = $false
        disposition = 'local_unsigned_non_deployable_non_cutover'
        bundleId = 'pegasus-dotnet-runtime'
        artifactProducer = 'Pegasus Cortex evidence builder'
        contentProfile = 'runtime-only'
        runtimeRootRelativePath = 'runtime'
        includesSdk = $false
        includesWorkloads = $false
        probeContract = 'pegasus.dependency-probe/dotnet-runtime-v1'
        executableModeInventoryReference = [ordered]@{
            path = 'evidence/executable-modes.json'
            sha256 = $modeInventorySha256
            applyBeforeProbe = $true
            installerAttestationRequired = $true
        }
        archiveExtraction = [ordered]@{
            resultSchema = [string]$archiveToolResult.schemaVersion
            inventorySchema = 'pegasus.cortex.archive-inventory/1'
            inventoryReference = [ordered]@{
                path = 'evidence/archive-inventory.json'
                sha256 = $archiveInventorySha256
            }
            archiveEntryCount = [int]$archiveToolResult.archiveEntryCount
            materializedEntryCount = [int]$archiveToolResult.materializedEntryCount
            tarStreamBytes = [long]$archiveToolResult.tarStreamBytes
            totalFileBytes = [long]$archiveToolResult.totalFileBytes
            parentAuthorityPrecondition = 'trusted-local-exclusive-writer'
            parentAuthorityVerified = $true
            parentAuthorityMethod = 'protected-owner-only-acl-under-delete-child-safe-volume-root'
            hostileParentPresent = $false
            hostileParentRaceProof = $false
            externalTarExecutableUsed = $false
            focusedTestReference = [ordered]@{
                path = 'evidence/archive-tool-tests.json'
                sha256 = $archiveToolTestEvidenceSha256
                status = 'pass'
                passed = 23
            }
        }
        licenseEvidence = @(
            [ordered]@{
                path = 'runtime/LICENSE.txt'
                sha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot 'LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
            },
            [ordered]@{
                path = 'runtime/ThirdPartyNotices.txt'
                sha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot 'ThirdPartyNotices.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        excludedContent = @('sdk', 'sdk-manifests', 'packs', 'templates', 'metadata', 'workloads')
        sourceArchive = [ordered]@{
            uri = $RuntimeSourceUri
            fileName = $runtimeArchiveItem.Name
            bytes = $runtimeArchiveItem.Length
            sha256 = $actualRuntimeSha256
            sha512 = $actualRuntimeSha512
            releaseMetadataSha256 = $runtimeMetadataEvidence.metadataSha256
        }
        toolchain = [ordered]@{
            powershell = $PSVersionTable.PSVersion.ToString()
            clr = [Environment]::Version.ToString()
            operatingSystem = [Environment]::OSVersion.VersionString
            dotnetSdk = $actualSdkVersion
            dotnetHostSha256 = $dotnetSha256
            releaseToolsSha256 = $actualReleaseToolsSha256
            buildSourceMode = 'protected-hash-verified-copy'
            protectedNugetConfigurationSha256 = $protectedNugetConfigurationSha256
            buildProcessEnvironmentMode = 'explicit-allowlist'
            buildProcessEnvironmentNames = @($buildEnvironment.Keys | Sort-Object)
            archiveTool = [ordered]@{
                assemblySha256 = $archiveToolSha256
                runtimeConfigurationSha256 = $archiveToolRuntimeConfigurationSha256
                frozenSourceTreeSha256 = $archiveToolSourceEvidence.Sha256
                frozenSourceFileCount = $archiveToolSourceEvidence.FileCount
                sourceProjectSha256 = $archiveToolProjectSha256
                sourceLockSha256 = $archiveToolLockSha256
                testProjectSha256 = $archiveToolTestProjectSha256
                testLockSha256 = $archiveToolTestLockSha256
                executionRuntime = $archiveToolExecutionRuntime
                focusedTestEvidenceSha256 = $archiveToolTestEvidenceSha256
                externalPackages = @()
            }
        }
        pathBudget = [ordered]@{
            windowsFinalPathLimit = $script:WindowsPathLimit
            maximumRelativePathCharacters = $runtimeMaximumPath.Length
            maximumRelativePath = $runtimeMaximumPath
            maximumSafeExtractionRootCharacters = $script:WindowsPathLimit - 1 - $runtimeMaximumPath.Length
        }
        files = $runtimePayloadFiles
    })
    $runtimePayloadFiles | ForEach-Object { "$($_.sha256)  $($_.path)" } |
        Set-Content -LiteralPath (Join-Path $runtimeEvidenceRoot 'checksums.sha256') -Encoding ASCII

    $runtimeAllFiles = @(Get-RelativeFileEvidence -Root $runtimeBundleRoot)
    foreach ($file in $runtimeAllFiles) {
        $generatedPath = Join-Path $runtimeBundleRoot $file.path.Replace('/', '\')
        if ($generatedPath.Length -gt $script:WindowsPathLimit) {
            throw "Generated runtime-bundle path exceeds the $($script:WindowsPathLimit)-character budget: '$generatedPath'."
        }
    }
    $runtimeExecutablePaths = @($runtimeModes | Where-Object mode -eq '0755' | ForEach-Object path)
    $runtimeArchiveResult = New-CortexDeterministicZipArchive `
        -SourceRoot $runtimeBundleRoot `
        -ArchivePath $runtimeBundleArchivePath `
        -ExpectedRoot $stagingRoot `
        -ExecutableRelativePaths $runtimeExecutablePaths
    $runtimeProbePath = Join-Path $stagingRoot "$runtimeBundleArchiveName.determinism-probe.zip"
    try {
        $runtimeProbeResult = New-CortexDeterministicZipArchive `
            -SourceRoot $runtimeBundleRoot `
            -ArchivePath $runtimeProbePath `
            -ExpectedRoot $stagingRoot `
            -ExecutableRelativePaths $runtimeExecutablePaths
        if ($runtimeArchiveResult.Sha256 -cne $runtimeProbeResult.Sha256) {
            throw 'Runtime dependency archive determinism verification produced different SHA-256 values.'
        }
        Assert-ZipMatchesEvidence `
            -ArchivePath $runtimeBundleArchivePath `
            -ExpectedFiles $runtimeAllFiles `
            -ExecutableRelativePaths $runtimeExecutablePaths
    }
    finally {
        if (Test-Path -LiteralPath $runtimeProbePath) {
            Remove-Item -LiteralPath $runtimeProbePath -Force
        }
    }

    $projects = @(
        [ordered]@{
            id = 'linux-native'
            project = 'tests\PegasusCortex.OfflineJournal.Platform.Linux.Tests\PegasusCortex.OfflineJournal.Platform.Linux.Tests.csproj'
            assembly = 'PegasusCortex.OfflineJournal.Platform.Linux.Tests'
            fixedArguments = @()
            disposition = 'isolated-linux-filesystem-validation'
        },
        [ordered]@{
            id = 'postgresql-fixture'
            project = 'tests\PegasusCortex.Persistence.PostgreSql.IntegrationTests\PegasusCortex.Persistence.PostgreSql.IntegrationTests.csproj'
            assembly = 'PegasusCortex.Persistence.PostgreSql.IntegrationTests'
            fixedArguments = @()
            disposition = 'offline-default-explicit-live-opt-in-trusted-loopback-or-private-pki-validation'
        },
        [ordered]@{
            id = 'mcp'
            project = 'src\PegasusCortex.Mcp\PegasusCortex.Mcp.csproj'
            assembly = 'PegasusCortex.Mcp'
            fixedArguments = @()
            disposition = 'isolated-stdio-validation'
        },
        [ordered]@{
            id = 'service-lifecycle'
            project = 'src\PegasusCortex.Service\PegasusCortex.Service.csproj'
            assembly = 'PegasusCortex.Service'
            fixedArguments = @('--service-lifecycle-rehearsal')
            disposition = 'validation-only-no-transport-no-database-no-keys'
        }
    )

    foreach ($project in $projects) {
        $projectPath = Join-Path $stagedSourceRoot $project.project
        Invoke-DotNet -DotNetPath $dotnet -Operation "Locked restore for $($project.id)" -Arguments @(
            'restore', $projectPath,
            '--locked-mode',
            '--no-cache',
            '--configfile', $protectedNugetConfigurationPath,
            '--artifacts-path', $dotnetArtifactsRoot,
            '-p:NuGetAudit=false',
            '-p:RestoreIgnoreFailedSources=false',
            "-p:PegasusPackageFeed=$pegasusPackageFeed",
            '-p:RestoreAdditionalProjectSources=')

        $publishRoot = Join-Path $appsRoot $project.id
        New-Item -ItemType Directory -Path $publishRoot | Out-Null
        Invoke-DotNet -DotNetPath $dotnet -Operation "Portable framework-dependent publish for $($project.id)" -Arguments @(
            'publish', $projectPath,
            '-c', $Configuration,
            '--self-contained', 'false',
            '--no-restore',
            '--artifacts-path', $dotnetArtifactsRoot,
            '-p:UseAppHost=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '-p:UseSharedCompilation=false',
            '-p:PublishSingleFile=false',
            '-p:PublishReadyToRun=false',
            '-p:PublishAot=false',
            '-p:PublishTrimmed=false',
            "-p:PegasusPackageFeed=$pegasusPackageFeed",
            '-o', $publishRoot)
        Set-PublishedRuntimePin `
            -PublishRoot $publishRoot `
            -AssemblyName $project.assembly `
            -RuntimeVersion $RuntimeVersion
        Assert-PublishedApplication `
            -PublishRoot $publishRoot `
            -AssemblyName $project.assembly `
            -RuntimeVersion $RuntimeVersion
    }

    $assetsPaths = @(Get-ChildItem -LiteralPath $dotnetArtifactsRoot -Recurse -File -Filter 'project.assets.json' |
        ForEach-Object FullName)
    if ($assetsPaths.Count -eq 0) {
        throw 'Isolated restore did not produce any project.assets.json evidence.'
    }
    $portableRestoreEvidence = Get-PortableRestoreEvidence -AssetsPaths $assetsPaths
    Write-Utf8Json `
        -Path (Join-Path $applicationEvidenceRoot 'portable-restore.json') `
        -Value $portableRestoreEvidence

    $locksRoot = Join-Path $applicationEvidenceRoot 'locks'
    New-Item -ItemType Directory -Path $locksRoot | Out-Null
    foreach ($project in $projects) {
        $projectDirectory = Split-Path -Parent (Join-Path $stagedSourceRoot $project.project)
        $lockPath = Join-Path $projectDirectory 'packages.lock.json'
        Assert-RegularFile -Path $lockPath | Out-Null
        Copy-Item -LiteralPath $lockPath -Destination (Join-Path $locksRoot "$($project.id).packages.lock.json")
    }

    $dependencyInventory = @(Get-DependencyInventory -AssetsPaths $assetsPaths)
    $sbomComponents = @(
        [ordered]@{
            type = 'framework'
            name = 'Microsoft.NETCore.App'
            version = $RuntimeVersion
            scope = 'required'
        }
    )
    foreach ($dependency in $dependencyInventory) {
        $properties = @([ordered]@{ name = 'pegasus.component.type'; value = $dependency.type })
        if (-not [string]::IsNullOrWhiteSpace([string]$dependency.sha512)) {
            $properties += [ordered]@{ name = 'nuget.contentHash'; value = $dependency.sha512 }
        }
        $sbomComponents += [ordered]@{
            type = 'library'
            name = $dependency.name
            version = $dependency.version
            properties = $properties
        }
    }
    $sbom = [ordered]@{
        bomFormat = 'CycloneDX'
        specVersion = '1.5'
        version = 1
        metadata = [ordered]@{
            timestamp = $releaseTimestamp
            component = [ordered]@{
                type = 'application'
                name = 'pegasus-cortex-arm64-evidence'
                version = $releaseVersion
                'bom-ref' = "pkg:generic/pegasus-cortex-arm64-evidence@$releaseVersion"
            }
        }
        components = $sbomComponents
    }
    Write-Utf8Json -Path (Join-Path $applicationEvidenceRoot 'sbom.cdx.json') -Value $sbom

    Write-Utf8Json -Path (Join-Path $applicationEvidenceRoot 'signature-status.json') -Value ([ordered]@{
        schemaVersion = 1
        artifactId = 'pegasus-cortex-arm64-evidence'
        version = $releaseVersion
        signatureStatus = 'unsigned'
        approvedSigner = $null
        deploymentAuthorized = $false
        reason = 'Local ARM64 test evidence requires an approved release signature before any deployment or activation.'
    })

    Assert-NoSensitivePayloadNames -Root $applicationBundleRoot
    $sensitivePayloadScan = Get-SensitivePayloadScan -Root $applicationBundleRoot
    $sensitivePayloadScanPath = Join-Path $applicationEvidenceRoot 'sensitive-payload-scan.json'
    Write-Utf8Json -Path $sensitivePayloadScanPath -Value $sensitivePayloadScan
    $sensitivePayloadScanSha256 = (Get-FileHash -LiteralPath $sensitivePayloadScanPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-RegularFile -Path $protectedNugetConfigurationPath | Out-Null
    Assert-NoReparseAncestors `
        -ExistingRoot $verifiedInputsRoot `
        -Candidate $protectedNugetConfigurationPath
    Assert-OfflineNuGetConfiguration `
        -ConfigurationPath $protectedNugetConfigurationPath `
        -AllowedPackagesRoot $protectedPackageFeedRoot
    if ((Get-FileHash -LiteralPath $protectedNugetConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
        $protectedNugetConfigurationSha256) {
        throw 'Protected NuGet configuration changed during restore or publish.'
    }
    Assert-VerifiedPackageFeedUnchanged `
        -Root $protectedPackageFeedRoot `
        -Evidence $protectedPackageEvidence
    $sourceInputs = @(Get-SourceInputEvidence -Root $root)
    Assert-SourceEvidenceUnchanged -Before $sourceInputsBefore -After $sourceInputs
    $stagedSourceInputsAfter = @(Get-SourceInputEvidence -Root $stagedSourceRoot)
    Assert-SourceEvidenceUnchanged -Before $sourceInputsBefore -After $stagedSourceInputsAfter
    $archiveToolSourceEvidenceAfter = Get-DirectoryDigestEvidence `
        -Root $archiveToolSourceRoot `
        -ExcludedDirectoryNames @('bin', 'obj')
    if ($archiveToolSourceEvidenceAfter.FileCount -ne $archiveToolSourceEvidence.FileCount -or
        $archiveToolSourceEvidenceAfter.Sha256 -cne $archiveToolSourceEvidence.Sha256) {
        throw 'Protected ArchiveTool source changed during restore, tests, publish, or extraction.'
    }
    $sourceSnapshot = $null
    if (-not [string]::IsNullOrWhiteSpace($SourceSnapshotPath)) {
        $resolvedSnapshotPath = (Resolve-Path -LiteralPath $SourceSnapshotPath).Path
        $sourceSnapshot = Get-VerifiedSourceSnapshotEvidence `
            -SnapshotPath $resolvedSnapshotPath `
            -ReleaseVersion $releaseVersion `
            -SourceInputs $sourceInputs `
            -ProtectedInputRoot $verifiedInputsRoot
    }

    $excludedManifestFiles = @('evidence/bundle-manifest.json', 'evidence/checksums.sha256')
    $payloadFiles = @(Get-RelativeFileEvidence -Root $applicationBundleRoot -ExcludedRelativePaths $excludedManifestFiles)
    $maximumRelativePath = ($payloadFiles | Sort-Object { $_.path.Length } -Descending | Select-Object -First 1).path
    $manifest = [ordered]@{
        schemaVersion = 1
        artifactId = 'pegasus-cortex-arm64-evidence'
        releaseVersion = $releaseVersion
        nuGetVersion = $nuGetVersion
        createdAt = $releaseTimestamp
        source = 'project://pegasus.cortex'
        runtimeIdentifier = $null
        executionRuntimeIdentifier = $RuntimeIdentifier
        artifactKind = 'validation-bundle'
        profileId = 'arm64-test-evidence'
        buildConfiguration = $Configuration
        applicationPublishMode = 'portable-framework-dependent-no-apphost'
        applicationAssetKind = 'managed-il'
        applicationRuntimeTarget = '.NETCoreApp,Version=v10.0'
        applicationContainsNativeAssets = $false
        runtimeProvisioningMode = 'installer-managed'
        deploymentAuthorized = $false
        signatureStatus = 'unsigned'
        disposition = 'local_unsigned_non_deployable_non_cutover'
        sourceSnapshot = $sourceSnapshot
        requiredDependencies = @([ordered]@{
            artifactId = 'pegasus-dotnet-runtime'
            version = $RuntimeVersion
            runtimeIdentifier = $RuntimeIdentifier
            relativePath = $runtimeBundleArchiveName
            sha256 = $runtimeArchiveResult.Sha256
            sourceArchiveSha512 = $actualRuntimeSha512
            provisioningMode = 'installer-managed'
            required = $true
            systemInstallRequired = $false
            resolutionBase = 'verified-artifact-extraction-root'
            runtimeRootRelativePath = 'runtime'
        })
        postgresqlFixtureTransport = [ordered]@{
            schemaVersion = 1
            selectionVariable = 'PEGASUS_CORTEX_TLS_POLICY'
            defaultProfile = 'verify-full'
            profiles = @(
                [ordered]@{
                    id = 'verify-full'
                    tlsEnabled = $true
                    literalLoopbackOnly = $true
                    rootCertificate = 'required'
                    trustBypass = 'forbidden'
                    revocationEvidence = 'required'
                    revocationProfile = 'pegasus.cortex.postgresql.private-ca-crl/1'
                },
                [ordered]@{
                    id = 'trusted-loopback'
                    tlsEnabled = $false
                    literalLoopbackOnly = $true
                    rootCertificate = 'forbidden'
                    trustBypass = 'forbidden'
                    revocationEvidence = 'forbidden'
                    revocationProfile = $null
                })
        }
        postgresqlPrivatePki = [ordered]@{
            transportProfile = 'verify-full'
            requiredOnlyWhenSelected = $true
            profile = 'pegasus.cortex.postgresql.private-ca-crl/1'
            productionRevocationOwner = 'external-authorized-platform-operator'
            cortexRuntimeOwnsResponder = $false
            fixtureResponderDisposition = 'test-only-task-local-loopback'
            operatorGuide = [ordered]@{
                path = 'evidence/postgresql-private-pki/OPERATOR.md'
                sha256 = (Get-FileHash -LiteralPath $privatePkiOperatorGuidePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            schema = [ordered]@{
                path = 'evidence/postgresql-private-pki/postgresql-private-pki-revocation.v1.schema.json'
                sha256 = (Get-FileHash -LiteralPath $privatePkiSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            rehearsalRequirements = [ordered]@{
                path = 'evidence/postgresql-private-pki/postgresql-rehearsal-v3-requirements.json'
                sha256 = (Get-FileHash -LiteralPath $privatePkiRequirementsPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        entrypointContract = 'pegasus.validation-entrypoint/v1'
        entrypoints = @($projects | ForEach-Object {
            [ordered]@{
                id = $_.id
                executableArtifactId = 'pegasus-dotnet-runtime'
                executableRelativePath = 'runtime/dotnet'
                argumentPaths = @([ordered]@{
                    artifactId = 'pegasus-cortex-arm64-evidence'
                    relativePath = "apps/$($_.id)/$($_.assembly).dll"
                })
                fixedArguments = @($_.fixedArguments)
                disposition = [string]$_.disposition
                workingDirectory = [ordered]@{
                    artifactId = 'pegasus-cortex-arm64-evidence'
                    relativePath = "apps/$($_.id)"
                }
                pathResolution = 'verified-artifact-extraction-root'
                tokenExpansion = 'none'
            }
        })
        executionEnvironment = @(
            [ordered]@{
                name = 'DOTNET_ROOT'
                valueKind = 'artifact-directory'
                artifactId = 'pegasus-dotnet-runtime'
                relativePath = 'runtime'
            },
            [ordered]@{ name = 'DOTNET_MULTILEVEL_LOOKUP'; valueKind = 'literal'; value = '0' },
            [ordered]@{ name = 'DOTNET_EnableDiagnostics'; valueKind = 'literal'; value = '0' })
        safety = [ordered]@{
            containsSecrets = $false
            containsPrivateKeys = $false
            containsDatabaseCredentials = $false
            containsCertificateAuthorityMaterial = $false
            sensitivePayloadScanReference = [ordered]@{
                path = 'evidence/sensitive-payload-scan.json'
                sha256 = $sensitivePayloadScanSha256
                status = 'pass'
            }
            postgresDefaultMode = 'offline-only'
            postgresLiveOptIn = 'PEGASUS_CORTEX_PG_INTEGRATION=1'
            postgresReadOnlyPreflightOptIn = 'PEGASUS_CORTEX_PG_PREFLIGHT_ONLY=1'
            postgresTransportProfileVariable = 'PEGASUS_CORTEX_TLS_POLICY'
            postgresDefaultTransportProfile = 'verify-full'
            postgresAllowedTransportProfiles = @('verify-full', 'trusted-loopback')
            postgresRevocationEvidenceRequiredForProfiles = @('verify-full')
            postgresRevocationEvidenceForbiddenForProfiles = @('trusted-loopback')
            postgresRevocationProfile = 'pegasus.cortex.postgresql.private-ca-crl/1'
            postgresFixtureResponderPersistent = $false
            linuxNativeTestRootOptIn = 'PEGASUS_CORTEX_LINUX_JOURNAL_TEST_ROOT'
            liveDatabaseAuthorized = $false
            hostMutationAuthorized = $false
        }
        pathBudget = [ordered]@{
            windowsFinalPathLimit = $script:WindowsPathLimit
            maximumRelativePathCharacters = $maximumRelativePath.Length
            maximumRelativePath = $maximumRelativePath
            maximumSafeExtractionRootCharacters = $script:WindowsPathLimit - 1 - $maximumRelativePath.Length
        }
        sourceInputs = $sourceInputs
        buildInputBoundary = [ordered]@{
            mode = 'protected-hash-verified-copy'
            protectedNugetConfigurationSha256 = $protectedNugetConfigurationSha256
            protectedPackageFeed = $protectedPackageEvidence
            sourceComparedBeforeCopyAfterCopyAndAfterBuild = $true
            processEnvironmentMode = 'explicit-allowlist'
            processEnvironmentNames = @($buildEnvironment.Keys | Sort-Object)
        }
        portableRestore = $portableRestoreEvidence
        dependencies = $dependencyInventory
        files = $payloadFiles
    }
    Write-Utf8Json -Path (Join-Path $applicationEvidenceRoot 'bundle-manifest.json') -Value $manifest
    $payloadFiles | ForEach-Object { "$($_.sha256)  $($_.path)" } |
        Set-Content -LiteralPath (Join-Path $applicationEvidenceRoot 'checksums.sha256') -Encoding ASCII

    Assert-NoSensitivePayloadNames -Root $applicationBundleRoot
    $allFiles = @(Get-RelativeFileEvidence -Root $applicationBundleRoot)
    foreach ($file in $allFiles) {
        $generatedPath = Join-Path $applicationBundleRoot $file.path.Replace('/', '\')
        if ($generatedPath.Length -gt $script:WindowsPathLimit) {
            throw "Generated bundle path exceeds the $($script:WindowsPathLimit)-character budget: '$generatedPath'."
        }
    }

    $archiveResult = New-CortexDeterministicZipArchive `
        -SourceRoot $applicationBundleRoot `
        -ArchivePath $applicationArchivePath `
        -ExpectedRoot $stagingRoot `
        -ExecutableRelativePaths @()
    $probePath = Join-Path $stagingRoot "$applicationArchiveName.determinism-probe.zip"
    try {
        $probeResult = New-CortexDeterministicZipArchive `
            -SourceRoot $applicationBundleRoot `
            -ArchivePath $probePath `
            -ExpectedRoot $stagingRoot `
            -ExecutableRelativePaths @()
        if ($archiveResult.Sha256 -cne $probeResult.Sha256) {
            throw 'Deterministic archive verification produced different SHA-256 values.'
        }
        Assert-ZipMatchesEvidence -ArchivePath $applicationArchivePath -ExpectedFiles $allFiles -ExecutableRelativePaths @()
    }
    finally {
        if (Test-Path -LiteralPath $probePath) {
            Remove-Item -LiteralPath $probePath -Force
        }
    }

    Write-Utf8Json -Path $releaseManifestPath -Value ([ordered]@{
        schemaVersion = 1
        releaseVersion = $releaseVersion
        runtimeIdentifier = $RuntimeIdentifier
        runtimeIdentifierScope = 'execution-runtime-bundle'
        applicationRuntimeIdentifier = $null
        runtimeVersion = $RuntimeVersion
        profileId = 'arm64-validation-with-installer-managed-runtime'
        buildConfiguration = $Configuration
        sourceSnapshot = $sourceSnapshot
        artifacts = @(
            [ordered]@{
                artifactId = 'pegasus-cortex-arm64-evidence'
                kind = 'validation'
                runtimeIdentifier = $null
                relativePath = $applicationArchiveName
                bytes = $archiveResult.Bytes
                entries = $archiveResult.Entries
                sha256 = $archiveResult.Sha256
                byteIdenticalArchiveSerializationVerified = $true
            },
            [ordered]@{
                artifactId = 'pegasus-dotnet-runtime'
                kind = 'dependency-bundle'
                version = $RuntimeVersion
                runtimeIdentifier = $RuntimeIdentifier
                relativePath = $runtimeBundleArchiveName
                bytes = $runtimeArchiveResult.Bytes
                entries = $runtimeArchiveResult.Entries
                sha256 = $runtimeArchiveResult.Sha256
                sourceArchiveSha512 = $actualRuntimeSha512
                byteIdenticalArchiveSerializationVerified = $true
            }
        )
        signatureStatus = 'unsigned'
        deploymentAuthorized = $false
        disposition = 'local_unsigned_non_deployable_non_cutover'
        contractScope = 'validation-only'
        deploymentManifestEmitted = $false
        buildDependencies = $protectedPackageEvidence
        permittedUse = 'isolated Cortex ARM64 filesystem, PostgreSQL-fixture, MCP-host, and service-lifecycle smoke evidence only'
    })
    $releaseManifestSha256 = (Get-FileHash -LiteralPath $releaseManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $retentionPlan = Get-ArtifactRetentionPlan `
        -Root $resolvedOutputRoot `
        -CandidateVersion $releaseVersion `
        -Keep $artifactRetentionCount `
        -Kind build `
        -CandidateIsProspective

    # Remove task-owned build material before commit so post-commit cleanup is
    # limited to the marker-only staging directory.
    if ($locationPushed) {
        Pop-Location
        $locationPushed = $false
    }
    foreach ($ownedChild in @(
            $applicationBundleRoot,
            $runtimeBundleRoot,
            $verifiedInputsRoot,
            $nugetCacheRoot,
            $dotnetArtifactsRoot,
            $processTempRoot,
            $stagedSourceRoot)) {
        Remove-TreeNoFollow -Path $ownedChild
    }

    # Directory.Move is a same-volume, no-replace commit. It throws instead of
    # nesting output if another producer won the immutable version path.
    [IO.Directory]::Move($finalOutputRoot, $versionRoot)
    $committedVersionRoot = $versionRoot
    Assert-ExclusiveWriterDirectory -Path $versionRoot -AllowInheritedRules
    $committedApplicationArchive = Join-Path $versionRoot $applicationArchiveName
    $committedRuntimeArchive = Join-Path $versionRoot $runtimeBundleArchiveName
    $committedReleaseManifest = Join-Path $versionRoot 'cortex-arm64-evidence-release-manifest.json'
    if ((Get-FileHash -LiteralPath $committedApplicationArchive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $archiveResult.Sha256 -or
        (Get-FileHash -LiteralPath $committedRuntimeArchive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runtimeArchiveResult.Sha256 -or
        (Get-FileHash -LiteralPath $committedReleaseManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $releaseManifestSha256) {
        throw 'Committed evidence differs from the verified staging output.'
    }

    # The Shared 421-a pruner has no preserve-current/no-follow contract. Its
    # version parser and marker writer remain canonical; this wrapper applies
    # the precomputed plan through the local no-follow deletion boundary.
    foreach ($expiredDirectory in $retentionPlan.ExpiredDirectories) {
        Assert-ContainedPath -Root $resolvedOutputRoot -Candidate $expiredDirectory | Out-Null
        Assert-NoReparseAncestors -ExistingRoot $resolvedOutputRoot -Candidate $expiredDirectory
        Assert-ExclusiveWriterDirectory -Path $expiredDirectory -AllowInheritedRules
        Remove-TreeNoFollow -Path $expiredDirectory
        if (Test-Path -LiteralPath $expiredDirectory) {
            throw "Retention did not remove expired evidence '$expiredDirectory'."
        }
    }
    if (-not (Test-Path -LiteralPath $versionRoot -PathType Container) -or
        (Get-FileHash -LiteralPath $committedApplicationArchive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $archiveResult.Sha256 -or
        (Get-FileHash -LiteralPath $committedRuntimeArchive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runtimeArchiveResult.Sha256 -or
        (Get-FileHash -LiteralPath $committedReleaseManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $releaseManifestSha256) {
        throw 'Committed evidence changed while applying retention.'
    }

    Register-PegasusArtifactVersion `
        -ArtifactDirectory $versionRoot `
        -Version $releaseVersion `
        -Kind build `
        -RuntimeIdentifiers @($RuntimeIdentifier)
    $retentionMarkerPath = Join-Path $versionRoot '.pegasus-retention.json'
    Assert-RegularFile -Path $retentionMarkerPath | Out-Null
    $retentionMarker = Get-Content -LiteralPath $retentionMarkerPath -Raw | ConvertFrom-Json
    if ($retentionMarker.verified -ne $true -or
        [string]$retentionMarker.kind -cne 'build' -or
        [string]$retentionMarker.releaseVersion -cne $releaseVersion -or
        @($retentionMarker.runtimeIdentifiers).Count -ne 1 -or
        [string]@($retentionMarker.runtimeIdentifiers)[0] -cne $RuntimeIdentifier) {
        throw 'The committed retention marker does not describe the verified evidence.'
    }
    $postRetentionPlan = Get-ArtifactRetentionPlan `
        -Root $resolvedOutputRoot `
        -CandidateVersion $releaseVersion `
        -Keep $artifactRetentionCount `
        -Kind build
    if ($postRetentionPlan.ExpiredDirectories.Count -ne 0) {
        throw 'Verified retention state still contains expired evidence versions.'
    }
    $retentionRegistered = $true

    [pscustomobject]@{
        ApplicationArchivePath = $committedApplicationArchive
        ApplicationArchiveSha256 = $archiveResult.Sha256
        RuntimeBundleArchivePath = $committedRuntimeArchive
        RuntimeBundleArchiveSha256 = $runtimeArchiveResult.Sha256
        ReleaseManifestPath = $committedReleaseManifest
        ReleaseManifestSha256 = $releaseManifestSha256
        RuntimeSha512 = $actualRuntimeSha512
        DeploymentAuthorized = $false
    }
}
catch {
    $primaryFailure = $_
    if ($null -ne $committedVersionRoot -and
        -not $retentionRegistered -and
        (Test-Path -LiteralPath $committedVersionRoot)) {
        try {
            Assert-ContainedPath -Root $resolvedOutputRoot -Candidate $committedVersionRoot | Out-Null
            Assert-NoReparseAncestors -ExistingRoot $resolvedOutputRoot -Candidate $committedVersionRoot
            Assert-ExclusiveWriterDirectory -Path $committedVersionRoot -AllowInheritedRules
            Remove-TreeNoFollow -Path $committedVersionRoot
        }
        catch {
            Write-Warning "Unable to remove the unregistered failed commit '$committedVersionRoot': $($_.Exception.Message)"
        }
    }
    throw
}
finally {
    if ($locationPushed) {
        Pop-Location
        $locationPushed = $false
    }
    Set-ExactProcessEnvironment -Variables $previousEnvironment
    $cleanupFailure = $null
    try {
        Remove-OwnedStagingRoot `
            -StagingParent $stagingBase `
            -StagingRoot $stagingRoot `
            -ExpectedMarkerValue $releaseVersion
    }
    catch {
        if ($null -ne $primaryFailure) {
            Write-Warning "Task-owned staging cleanup also failed after the primary error: $($_.Exception.Message)"
        }
        else {
            $cleanupFailure = $_
        }
    }
    if ($null -ne $leaseStream) {
        $leaseStream.Dispose()
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
}
