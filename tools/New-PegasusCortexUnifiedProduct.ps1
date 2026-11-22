[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$OutputRoot = '',
    [string]$OwnerAdminBaselinePackageRoot = '',
    [string]$OwnerAdminBaselineManifestSha256 = '',
    [string]$OwnerAdminBaselineSignatureSha256 = '',
    [string]$OwnerAdminRestorePropsPath = '',
    [string]$DotNetPath = 'C:\Program Files\dotnet\dotnet.exe',
    [Parameter(Mandatory)][string]$RootThumbprint,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CortexBuildWorkspace.ps1')
. (Join-Path $PSScriptRoot 'CortexOwnerAdminPackageReuse.ps1')
$ownerAdminReuse=-not [string]::IsNullOrWhiteSpace($OwnerAdminBaselinePackageRoot)
$ownerAdminExplicitOutput=-not [string]::IsNullOrWhiteSpace($OutputRoot)

function Assert-OrdinaryDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Expected an ordinary directory: '$Path'."
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory)][string]$Path)
    Assert-OrdinaryDirectory -Path $Path
    $reparse = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparse.Count -ne 0) {
        throw "A package source tree contains a reparse point: '$($reparse[0].FullName)'."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-CortexPinnedManifest {
    param([string]$Path,[string]$ExpectedSha256)
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try
    {
        if($stream.Length-lt 1-or$stream.Length-gt 16MB){throw 'The admitted manifest exceeds the bounded input contract.'}
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()-cne$ExpectedSha256){throw 'Pinned manifest bytes changed.'}
        $stream.Position=0
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false,4096,$true)
        try{return $reader.ReadToEnd()|ConvertFrom-Json -Depth 40}
        finally{$reader.Dispose()}
    }
    finally{$stream.Dispose()}
}

function Get-CortexComponentVersion {
    param([Parameter(Mandatory)][string]$Path)
    $value = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if ([string]::IsNullOrWhiteSpace($value) -or
        $value -cnotmatch '^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') {
        throw "A published Cortex component has no valid independent product version: '$Path'."
    }
    return $value
}

function Wait-FileReadyForSigning {
    param([Parameter(Mandatory)][string]$Path)
    for ($attempt = 1; $attempt -le 50; $attempt++) {
        try {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            $stream.Dispose()
            return
        }
        catch [IO.IOException] {
            if ($attempt -eq 50) { throw }
            [Threading.Thread]::Sleep(100)
        }
    }
}

function Write-Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 20) + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedFile,
        [switch]$SingleFile
    )
    if (Test-Path -LiteralPath $Destination) {
        throw "Publish destination is create-only: '$Destination'."
    }
    New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @(
        'publish', $Project, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '--no-restore', '--disable-build-servers', '--output', $Destination,
        '-nodeReuse:false', '-p:UseSharedCompilation=false',
        '-p:PublishReadyToRun=false', '-p:PublishAot=false')) {
        $arguments.Add($value)
    }
    if ($SingleFile) {
        # Projects declare bundling locally. A global PublishSingleFile property
        # leaks into referenced libraries and changes their locked SDK graph.
        foreach ($value in @(
            '-p:PublishTrimmed=false',
            '-p:EnableCompressionInSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true')) {
            $arguments.Add($value)
        }
    }
    if($ownerAdminReuse)
    {
        foreach($value in @('--artifacts-path',(Join-Path $workRoot 'artifacts'),
            ('-p:CustomBeforeMicrosoftCommonProps='+$OwnerAdminRestorePropsPath),
            '-p:RestoreLockedMode=true','-p:CortexOwnerAdminReuseUnchangedRoles=true',
            '-p:AgentOpsNeedsProcessHost=false','-p:AgentOpsNeedsUiHost=false')){$arguments.Add($value)}
    }
    else
    {
        # The normal builder owns the graph it publishes. An earlier framework-
        # dependent test restore is not a self-contained release restore. Keep
        # single-file declarations local to their projects, as in publish above.
        $restoreArguments = [Collections.Generic.List[string]]::new()
        foreach ($value in @('restore', $Project, '--runtime', 'win-x64',
            '--configfile', (Join-Path $root 'NuGet.Config'), '--force-evaluate', '--disable-parallel',
            '-p:Configuration=Release', '-p:SelfContained=true', '-p:PublishReadyToRun=false',
            '-p:PublishAot=false', '-p:NuGetAudit=false')) { $restoreArguments.Add($value) }
        if ($SingleFile) {
            foreach ($value in @('-p:PublishTrimmed=false', '-p:EnableCompressionInSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true')) { $restoreArguments.Add($value) }
        }
        $restore = Invoke-CortexBuildProcess -Executable $DotNetPath -Arguments $restoreArguments.ToArray() `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($Project))
        if ($restore.ExitCode -ne 0) {
            throw ('Normal product restore failed for ' + $Project + '. ' +
                (Get-CortexBuildProcessOutputTail -Text ($restore.StandardOutput + "`n" + $restore.StandardError)))
        }
    }
    $expectedPath = Join-Path $Destination $ExpectedFile
    $maximumAttempts=if($ownerAdminReuse){1}else{2}
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        $result = Invoke-CortexBuildProcess `
            -Executable $DotNetPath `
            -Arguments $arguments.ToArray() `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($Project))
        Write-Verbose (Get-CortexBuildProcessOutputTail -Text $result.StandardOutput)
        Write-Verbose (Get-CortexBuildProcessOutputTail -Text $result.StandardError)
        if ($result.ExitCode -ne 0) {
            $details = Get-CortexBuildProcessOutputTail -Text ($result.StandardOutput + "`n" + $result.StandardError)
            if (-not [string]::IsNullOrWhiteSpace($details)) { $details = "`n$details" }
            throw "dotnet publish failed for '$Project' with exit code $($result.ExitCode).$details"
        }
        if (Test-Path -LiteralPath $expectedPath -PathType Leaf) { break }
        if ($attempt -eq $maximumAttempts) {
            throw "dotnet publish returned success without the required output '$expectedPath'. Evidence is retained."
        }
        Write-Host "dotnet publish returned success without '$ExpectedFile'; retrying once with isolated build nodes."
    }
    Assert-NoReparseTree -Path $Destination
}

function Merge-TreeExact {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    Assert-NoReparseTree -Path $Source
    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
    }
    Assert-OrdinaryDirectory -Path $Destination
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\','/')
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force -ErrorAction Stop) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..')) {
            throw "Merge source escaped its root: '$($file.FullName)'."
        }
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null
        }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            if ((Get-Sha256 -Path $file.FullName) -cne (Get-Sha256 -Path $target)) {
                throw "Publish merge collision has different bytes: '$relative'."
            }
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
    }
}

function Merge-OwnedFilesExact {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string[]]$Names
    )
    Assert-NoReparseTree -Path $Source
    Assert-OrdinaryDirectory -Path $Destination
    foreach ($name in $Names) {
        if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw "An owned publish filename is unsafe: '$name'."
        }
        $sourcePath = Join-Path $Source $name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            if ($name.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)) { continue }
            throw "An owned publish file is absent: '$sourcePath'."
        }
        $targetPath = Join-Path $Destination $name
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
            if ((Get-Sha256 -Path $sourcePath) -cne (Get-Sha256 -Path $targetPath)) {
                throw "Owned publish collision has different bytes: '$name'."
            }
            continue
        }
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -ErrorAction Stop
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
Assert-OrdinaryDirectory -Path $root
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    throw "The source-declared .NET SDK host is absent: '$DotNetPath'."
}

$version = Get-Content -LiteralPath (Join-Path $root 'VERSION.json') -Raw -Encoding UTF8 |
    ConvertFrom-Json -Depth 20 -ErrorAction Stop
$releaseVersion = [string]$version.releaseVersion
if ($releaseVersion -cnotmatch '^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$' -or
    [string]$version.releaseChannel -cne 'Prod Alpha') {
    throw 'VERSION.json does not identify one canonical Prod Alpha release.'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root 'artifacts\release\unified'
}
$releaseBoundary = Join-Path $root 'artifacts\release'
$output = Assert-CortexBuildPath -Path $OutputRoot -Boundary $releaseBoundary
$reuseInventory=@()
if($ownerAdminReuse)
{
    if(-not $ownerAdminExplicitOutput -or
       $OwnerAdminBaselineManifestSha256 -cnotmatch '^[a-f0-9]{64}$' -or
       $OwnerAdminBaselineSignatureSha256 -cnotmatch '^[a-f0-9]{64}$' -or
       -not [IO.Path]::IsPathFullyQualified($OwnerAdminRestorePropsPath) -or
       -not (Test-Path -LiteralPath $OwnerAdminRestorePropsPath -PathType Leaf))
    {throw 'Owner-admin reuse requires explicit create-only output, exact baseline manifest/signature pins and prepared strict restore props.'}
    if(Test-Path -LiteralPath $output){throw "Create-only product output already exists: '$output'. Retain it for review."}
    $baseline=[IO.Path]::GetFullPath($OwnerAdminBaselinePackageRoot).TrimEnd('\','/')
    Assert-NoReparseTree -Path $baseline
    if($baseline -eq $output -or $baseline.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($baseline+'\',[StringComparison]::OrdinalIgnoreCase))
    {throw 'Owner-admin baseline and candidate output must be disjoint.'}
    $baselineManifest=Join-Path $baseline 'package-manifest.json';$baselineSignature=Join-Path $baseline 'package-manifest.p7s'
    if((Get-Sha256 $baselineManifest)-cne$OwnerAdminBaselineManifestSha256 -or (Get-Sha256 $baselineSignature)-cne$OwnerAdminBaselineSignatureSha256){throw 'The pinned baseline manifest/signature changed.'}
    $baselineBody=Read-CortexPinnedManifest -Path $baselineManifest -ExpectedSha256 $OwnerAdminBaselineManifestSha256
    $baselineVerification=& (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') -PackageRoot $baseline -Role product -ReleaseVersion ([string]$baselineBody.releaseVersion)
    if(-not $baselineVerification.Valid -or -not [StringComparer]::OrdinalIgnoreCase.Equals($baselineVerification.PublisherThumbprint,$PublisherThumbprint) -or -not [StringComparer]::OrdinalIgnoreCase.Equals($baselineVerification.RootThumbprint,$RootThumbprint))
    {throw 'The verified baseline does not match the selected existing signer identities.'}
    if((Get-Sha256 $baselineManifest)-cne$OwnerAdminBaselineManifestSha256 -or (Get-Sha256 $baselineSignature)-cne$OwnerAdminBaselineSignatureSha256){throw 'The verified baseline identity changed during admission.'}
    if([StringComparer]::Ordinal.Compare($releaseVersion,[string]$baselineBody.releaseVersion)-le 0){throw 'Owner-admin candidate requires a fresh release newer than the baseline.'}
    $reuseInventory=@(Get-CortexOwnerAdminReuseInventory -Manifest $baselineBody)
    $workspace = Enter-CortexBuildWorkspace -ProjectRoot $root -CreateOnlyName ('pao-maintenance-product-'+$releaseVersion)
}
else
{
    if($OwnerAdminBaselineManifestSha256 -or $OwnerAdminBaselineSignatureSha256 -or $OwnerAdminRestorePropsPath){throw 'Reuse-only fields require an explicit owner-admin baseline.'}
    $workspace = Enter-CortexBuildWorkspace -ProjectRoot $root
}
try {
if($ownerAdminReuse){New-Item -ItemType Directory -Path $output -ErrorAction Stop|Out-Null}
else{Reset-CortexBuildDirectory -Path $output -Boundary $releaseBoundary}
$workRoot = $workspace.Path
$product = $output

$bin = Join-Path $product 'bin'
$semantics = Join-Path $bin 'semantics'
$hostPayload = Join-Path $bin 'host'
$service = Join-Path $bin 'service'
$ui = Join-Path $bin 'ui'
$installer = Join-Path $product 'installer'
$publishes = Join-Path $workRoot 'publishes'
New-Item -ItemType Directory -Path $publishes -ErrorAction Stop | Out-Null

if(-not $ownerAdminReuse)
{
Invoke-Publish -Project (Join-Path $root 'src\PegasusCortex.Launcher\PegasusCortex.Launcher.csproj') -Destination (Join-Path $publishes 'launcher') -ExpectedFile 'PegasusCortex.exe' -SingleFile
Copy-Item -LiteralPath (Join-Path $publishes 'launcher\PegasusCortex.exe') -Destination (Join-Path $product 'PegasusCortex.exe') -ErrorAction Stop
if (Test-Path -LiteralPath (Join-Path $publishes 'launcher\PegasusCortex.pdb')) {
    Copy-Item -LiteralPath (Join-Path $publishes 'launcher\PegasusCortex.pdb') -Destination (Join-Path $product 'PegasusCortex.pdb') -ErrorAction Stop
}

}
if($ownerAdminReuse)
{
    foreach($project in @('src\PegasusCortex.App\PegasusCortex.App.csproj','src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj','src\Pegasus.AgentOps.Service\Pegasus.AgentOps.Service.csproj','src\pao\pao.csproj'))
    {
        $restore=Invoke-CortexBuildProcess -Executable $DotNetPath -WorkingDirectory $root -Arguments @(
            'restore',(Join-Path $root $project),'--runtime','win-x64','--artifacts-path',(Join-Path $workRoot 'artifacts'),
            ('-p:CustomBeforeMicrosoftCommonProps='+$OwnerAdminRestorePropsPath),'-p:RestoreLockedMode=true','-p:SelfContained=true',
            '-p:CortexOwnerAdminReuseUnchangedRoles=true','-p:AgentOpsNeedsProcessHost=false','-p:AgentOpsNeedsUiHost=false')
        if($restore.ExitCode-ne 0){throw ('Strict owner-admin production restore failed. '+(Get-CortexBuildProcessOutputTail ($restore.StandardOutput+"`n"+$restore.StandardError)))}
    }
}
Invoke-Publish -Project (Join-Path $root 'src\PegasusCortex.App\PegasusCortex.App.csproj') -Destination $semantics -ExpectedFile 'PegasusCortex.exe'
Invoke-Publish -Project (Join-Path $root 'src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj') -Destination $hostPayload -ExpectedFile 'PegasusCortex.Host.exe'
Invoke-Publish -Project (Join-Path $root 'src\Pegasus.AgentOps.Service\Pegasus.AgentOps.Service.csproj') -Destination $service -ExpectedFile 'PegasusCortex.ExecutionService.exe'
if(-not $ownerAdminReuse)
{
Invoke-Publish -Project (Join-Path $root 'src\Pegasus.AgentOps.ProcessHost\Pegasus.AgentOps.ProcessHost.csproj') -Destination (Join-Path $publishes 'process-host') -ExpectedFile 'Pegasus.AgentOps.ProcessHost.exe'
$processHostFiles = @(
    'Pegasus.AgentOps.ProcessHost.exe', 'Pegasus.AgentOps.ProcessHost.dll',
    'Pegasus.AgentOps.ProcessHost.deps.json', 'Pegasus.AgentOps.ProcessHost.runtimeconfig.json',
    'Pegasus.AgentOps.ProcessHost.pdb')
Merge-OwnedFilesExact -Source (Join-Path $publishes 'process-host') -Destination $hostPayload -Names $processHostFiles
Merge-OwnedFilesExact -Source (Join-Path $publishes 'process-host') -Destination $service -Names $processHostFiles
Invoke-Publish -Project (Join-Path $root 'src\Pegasus.AgentOps.UiHost\Pegasus.AgentOps.UiHost.csproj') -Destination $ui -ExpectedFile 'Pegasus.AgentOps.UiHost.exe'
}
Invoke-Publish -Project (Join-Path $root 'src\pao\pao.csproj') -Destination (Join-Path $publishes 'pao') -ExpectedFile 'pao.exe' -SingleFile
Merge-OwnedFilesExact -Source (Join-Path $publishes 'pao') -Destination $bin -Names @('pao.exe', 'pao.pdb')
if(-not $ownerAdminReuse)
{
Invoke-Publish -Project (Join-Path $root 'src\PegasusCortex.Setup\PegasusCortex.Setup.csproj') -Destination $installer -ExpectedFile 'PegasusCortex.Setup.exe' -SingleFile
foreach ($deploymentFile in @(
        'Invoke-PegasusProdAlphaTrust.ps1', 'PegasusSigning.psm1', 'PegasusTrust.psm1')) {
    Copy-Item `
        -LiteralPath (Join-Path $root ("tools\signing\" + $deploymentFile)) `
        -Destination (Join-Path $installer $deploymentFile) `
        -ErrorAction Stop
}

}
if($ownerAdminReuse){Copy-CortexOwnerAdminReusedRoles -BaselineRoot $baseline -ProductRoot $product -Inventory $reuseInventory}

$requiredEntrypoints = @(
    (Join-Path $product 'PegasusCortex.exe'),
    (Join-Path $semantics 'PegasusCortex.exe'),
    (Join-Path $hostPayload 'PegasusCortex.Host.exe'),
    (Join-Path $service 'PegasusCortex.ExecutionService.exe'),
    (Join-Path $bin 'pao.exe'),
    (Join-Path $hostPayload 'Pegasus.AgentOps.ProcessHost.exe'),
    (Join-Path $ui 'Pegasus.AgentOps.UiHost.exe'),
    (Join-Path $installer 'PegasusCortex.Setup.exe'))
foreach ($entrypoint in $requiredEntrypoints) {
    if (-not (Test-Path -LiteralPath $entrypoint -PathType Leaf)) {
        throw "A unified product entrypoint is absent: '$entrypoint'."
    }
}

if($ownerAdminReuse)
{
    foreach($entrypoint in @((Join-Path $semantics 'PegasusCortex.exe'),(Join-Path $hostPayload 'PegasusCortex.Host.exe'),(Join-Path $service 'PegasusCortex.ExecutionService.exe'),(Join-Path $bin 'pao.exe')))
    {if((Get-CortexComponentVersion $entrypoint)-cne$releaseVersion){throw 'A freshly affected role does not carry the reviewed fresh candidate version.'}}
}

$nsisTarget = Join-Path $product 'tools\nsis\3.12'
New-Item -ItemType Directory -Path (Split-Path -Parent $nsisTarget) -Force -ErrorAction Stop | Out-Null
# The resolver holds the pinned archive through full cache admission and writes
# this create-only target directly from that archive, never from cache files.
$dependency = & (Join-Path $root 'tools\dependencies\Get-NsisBuildTool.ps1') `
    -CacheRoot (Join-Path $root 'artifacts\dependencies') -DestinationRoot $nsisTarget
if ([string]$dependency.ArchiveSha256 -cne
    '56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f') {
    throw 'NSIS resolution did not admit the pinned distribution archive.'
}
if ([IO.Path]::GetFullPath([string]$dependency.Root) -cne [IO.Path]::GetFullPath($nsisTarget) -or
    $dependency.VersionProbeExecuted -ne $false -or $dependency.VersionSource -cne 'pinned_archive_contract') {
    throw 'NSIS resolution did not materialize the selected archive-owned target.'
}

$licenses = Join-Path $product 'licenses'
Merge-TreeExact -Source (Join-Path $root 'licenses') -Destination $licenses
# Keep one canonical first-party grant in source and ship its exact text with every product.
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') `
    -Destination (Join-Path $licenses 'Pegasus-Community-License-1.0.txt') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $product 'LICENSE') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $root 'README.md') `
    -Destination (Join-Path $product 'README.md') -ErrorAction Stop
$nsisLicense = Join-Path $nsisTarget 'COPYING'
if (-not (Test-Path -LiteralPath $nsisLicense -PathType Leaf)) {
    throw 'The official NSIS distribution is missing COPYING.'
}
New-Item -ItemType Directory -Path (Join-Path $licenses 'nsis-3.12') -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath $nsisLicense -Destination (Join-Path $licenses 'nsis-3.12\COPYING') -ErrorAction Stop
# Preserve the maintained documentation tree and its relative handbook/setup
# links. A flat allowlist left the shipped README pointing at absent guides.
Merge-TreeExact -Source (Join-Path $root 'docs') -Destination (Join-Path $product 'docs')
# Keep the linked client reference usable in source and in the signed product.
$clientReference = 'integrations\codex\pegasus-cortex-client\skills\pegasus-cortex-client\references\capabilities.md'
$referenceTarget = Join-Path $product $clientReference
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($referenceTarget)) -Force -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath (Join-Path $root $clientReference) -Destination $referenceTarget -ErrorAction Stop

# Ship the same shared source tools used by Cortex, with public identity only.
New-Item -ItemType Directory -Path (Join-Path $product 'branding') -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\Pegasus.AgentOps.Tray\Assets\Pegasus.AgentOps.ico') `
    -Destination (Join-Path $product 'branding\PegasusCortex.ico') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $root 'src\Pegasus.AgentOps.Tray\Assets\Cortex.Setup.ico') `
    -Destination (Join-Path $product 'branding\Cortex.Setup.ico') -ErrorAction Stop
Merge-TreeExact -Source (Join-Path $root 'tools\windows-installer') -Destination (Join-Path $product 'tools\windows-installer')
$sharedSigning = Join-Path $product 'tools\signing'
New-Item -ItemType Directory -Path $sharedSigning -ErrorAction Stop | Out-Null
foreach ($name in @('PegasusSigning.psm1', 'PegasusTrust.psm1',
    'New-PegasusProdAlphaSigningIdentity.ps1', 'Export-PegasusProdAlphaTrustBundle.ps1',
    'Sign-PegasusArtifactTree.ps1', 'Sign-PegasusSingleFile.ps1',
    'Protect-PegasusPackageManifest.ps1', 'Test-PegasusSignedPackage.ps1',
    'Invoke-PegasusProdAlphaTrust.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root ('tools\signing\' + $name)) -Destination (Join-Path $sharedSigning $name) -ErrorAction Stop
}

$catalogDirectory = Join-Path $product "manifests\releases\$releaseVersion"
New-Item -ItemType Directory -Path $catalogDirectory -ErrorAction Stop | Out-Null
$catalogPath = Join-Path $catalogDirectory 'product-catalog.v1.json'
$roles = @(
    [ordered]@{ role='mcp'; component='semantics'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\semantics\PegasusCortex.exe')); relativePath='bin/semantics/PegasusCortex.exe' },
    [ordered]@{ role='host'; component='execution'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\host\PegasusCortex.Host.exe')); relativePath='bin/host/PegasusCortex.Host.exe' },
    [ordered]@{ role='service'; component='execution'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\service\PegasusCortex.ExecutionService.exe')); relativePath='bin/service/PegasusCortex.ExecutionService.exe' },
    [ordered]@{ role='pao'; component='execution'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\pao.exe')); relativePath='bin/pao.exe' },
    [ordered]@{ role='process-host'; component='execution'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\host\Pegasus.AgentOps.ProcessHost.exe')); relativePath='bin/host/Pegasus.AgentOps.ProcessHost.exe' },
    [ordered]@{ role='ui-host'; component='execution'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'bin\ui\Pegasus.AgentOps.UiHost.exe')); relativePath='bin/ui/Pegasus.AgentOps.UiHost.exe' },
    [ordered]@{ role='launcher'; component='launcher'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'PegasusCortex.exe')); relativePath='PegasusCortex.exe' },
    [ordered]@{ role='setup'; component='installer'; componentVersion=(Get-CortexComponentVersion (Join-Path $product 'installer\PegasusCortex.Setup.exe')); relativePath='installer/PegasusCortex.Setup.exe' })
$modules = @(
    [ordered]@{
        moduleId='core.bootstrap'; kind='core'; moduleVersion=$releaseVersion; required=$true; statePolicy='none';
        roles=@('launcher','setup'); provides=@('product.catalog/1'); requires=@()
    },
    [ordered]@{
        moduleId='semantics.local'; kind='runtime'; moduleVersion=$releaseVersion; required=$true; statePolicy='none';
        roles=@('mcp'); provides=@('cortex.mcp/2'); requires=@('storage.sqlite/2')
    },
    [ordered]@{
        moduleId='execution.local'; kind='runtime'; moduleVersion=$releaseVersion; required=$true; statePolicy='preserve';
        roles=@('host','service','process-host','ui-host'); provides=@('execution.local/1'); requires=@()
    },
    [ordered]@{
        moduleId='pao.client'; kind='tooling'; moduleVersion=$releaseVersion; required=$true; statePolicy='none';
        roles=@('pao'); provides=@('pao/3'); requires=@('execution.local/1')
    },
    [ordered]@{
        moduleId='storage.sqlite'; kind='storage'; moduleVersion=$releaseVersion; required=$true; statePolicy='preserve';
        roles=@(); provides=@('storage.sqlite/2'); requires=@()
    },
    [ordered]@{
        moduleId='assets.files'; kind='storage'; moduleVersion=$releaseVersion; required=$false; statePolicy='preserve';
        roles=@(); provides=@('assets.files/2'); requires=@()
    },
    [ordered]@{
        moduleId='assets.chat-icons'; kind='knowledge'; moduleVersion=$releaseVersion; required=$false; statePolicy='none';
        roles=@(); provides=@('assets.chat-icons/1'); requires=@('assets.files/2')
    },
    [ordered]@{
        moduleId='knowledge.shared'; kind='knowledge'; moduleVersion=$releaseVersion; required=$false; statePolicy='preserve';
        roles=@(); provides=@('knowledge.shared/1'); requires=@()
    },
    [ordered]@{
        moduleId='guidance.codex'; kind='guidance'; moduleVersion=$releaseVersion; required=$false; statePolicy='none';
        roles=@(); provides=@('guidance.codex/2'); requires=@()
    })
Write-Json -Path $catalogPath -Value ([ordered]@{
    contract='pegasus-cortex-product-catalog'; version=1; product='Pegasus Cortex';
    releaseVersion=$releaseVersion; channel='Prod Alpha'; executionProtocol='1.0';
    cortexMcpSchema=2; paoSchema=3;
    stateSchemas=[ordered]@{ install=1; semantic=2; execution=1; applicationAuthorization=1; userExecution=1; tray=1 };
    roles=$roles; modules=$modules
})
$catalogValidation = Invoke-CortexBuildProcess `
    -Executable (Join-Path $installer 'PegasusCortex.Setup.exe') `
    -Arguments @('validate-catalog', $catalogPath) `
    -WorkingDirectory $installer `
    -TimeoutSeconds 120
if ($catalogValidation.ExitCode -ne 0) {
    $details = Get-CortexBuildProcessOutputTail -Text ($catalogValidation.StandardOutput + "`n" + $catalogValidation.StandardError)
    if (-not [string]::IsNullOrWhiteSpace($details)) { $details = "`n$details" }
    throw "The published setup rejected the generated product catalog.$details"
}
Wait-FileReadyForSigning -Path (Join-Path $installer 'PegasusCortex.Setup.exe')

& (Join-Path $root 'tools\signing\Export-PegasusProdAlphaTrustBundle.ps1') `
    -PackageRoot $product -RootThumbprint $RootThumbprint -PublisherThumbprint $PublisherThumbprint | Out-Null
$preservedHashes=@{}
if($ownerAdminReuse){foreach($entry in $reuseInventory){$preservedHashes[[string]$entry.Path]=[string]$entry.Sha256}}
& (Join-Path $root 'tools\signing\Sign-PegasusArtifactTree.ps1') `
    -PackageRoot $product -PublisherThumbprint $PublisherThumbprint -Provider $Provider -IncludePowerShell `
    -PreserveValidPublisherSignatures:$ownerAdminReuse -PreservedFileHashes $preservedHashes | Out-Null
if($ownerAdminReuse){Assert-CortexOwnerAdminReusedRoles -ProductRoot $product -Inventory $reuseInventory}

$compiler = Join-Path $nsisTarget 'Bin\makensis.exe'
$admittedCompilerSha = Get-Sha256 -Path $compiler
Write-Json -Path (Join-Path $nsisTarget 'pegasus-nsis-admission.v1.json') -Value ([ordered]@{
    contract='pegasus-cortex-bundled-nsis'; version=1; nsisVersion='3.12';
    archiveSha256='56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f';
    compilerRelativePath='Bin/makensis.exe';
    upstreamCompilerSha256='25d1aa7081db1a9de9690b59983f9652b7409a189c3444328cc1841eff693e8d';
    admittedCompilerSha256=$admittedCompilerSha
})
$compilerProbe = Invoke-CortexBuildProcess `
    -Executable $compiler `
    -Arguments @('/VERSION') `
    -WorkingDirectory ([IO.Path]::GetDirectoryName($compiler)) `
    -TimeoutSeconds 30
$compilerVersion = $compilerProbe.StandardOutput + $compilerProbe.StandardError
if ($compilerProbe.ExitCode -ne 0 -or $compilerVersion -notmatch 'v3\.12') {
    throw 'The signed product-local NSIS compiler failed its exact version probe.'
}

& (Join-Path $root 'deploy\x64\New-PegasusCortexV2PackageManifest.ps1') `
    -PackageRoot $product -ReleaseVersion $releaseVersion -Role product `
    -PublisherThumbprint $PublisherThumbprint -RootThumbprint $RootThumbprint | Out-Null
& (Join-Path $root 'tools\signing\Protect-PegasusPackageManifest.ps1') `
    -PackageRoot $product -PublisherThumbprint $PublisherThumbprint -Provider $Provider | Out-Null
$verification = & (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') `
    -PackageRoot $product -Role product -ReleaseVersion $releaseVersion

[pscustomobject]@{
    ReusedRoles = $(if($ownerAdminReuse){@('launcher','setup','process-host','ui-host')}else{@()})
    ProductRoot = $output
    ReleaseVersion = $releaseVersion
    Channel = 'Prod Alpha'
    PackageManifestSha256 = (Get-Sha256 -Path (Join-Path $output 'package-manifest.json'))
    PackageSignatureSha256 = (Get-Sha256 -Path (Join-Path $output 'package-manifest.p7s'))
    PublisherThumbprint = $verification.PublisherThumbprint
    RootThumbprint = $verification.RootThumbprint
    NsisVersion = '3.12'
    NsisAdmittedCompilerSha256 = $admittedCompilerSha
}
}
finally { Exit-CortexBuildWorkspace -Workspace $workspace }
