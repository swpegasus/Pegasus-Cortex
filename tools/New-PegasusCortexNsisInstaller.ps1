[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$ProductPackageRoot = '',
    [string]$OutputRoot = '',
    [switch]$CreateOnly,
    [Parameter(Mandatory)][string]$RootThumbprint,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CortexBuildWorkspace.ps1')

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 16) + "`n"),
        [Text.UTF8Encoding]::new($false))
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\','/')
$version = Get-Content -LiteralPath (Join-Path $root 'VERSION.json') -Raw -Encoding UTF8 |
    ConvertFrom-Json -Depth 16 -ErrorAction Stop
$releaseVersion = [string]$version.releaseVersion
$fileVersion = [string]$version.fileVersion
if ($releaseVersion -cnotmatch '^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$' -or
    $fileVersion -cnotmatch '^[12]\.\d+\.\d+\.\d+$' -or
    [string]$version.releaseChannel -cne 'Prod Alpha') {
    throw 'VERSION.json does not identify one canonical Prod Alpha installer release.'
}
if ([string]::IsNullOrWhiteSpace($ProductPackageRoot)) {
    $ProductPackageRoot = Join-Path $root 'artifacts\release\unified'
}
$product = [IO.Path]::GetFullPath($ProductPackageRoot).TrimEnd('\','/')
$productItem = Get-Item -LiteralPath $product -Force -ErrorAction Stop
if (-not $productItem.PSIsContainer -or
    (($productItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'ProductPackageRoot must be one ordinary admitted directory.'
}
$reparse = @(Get-ChildItem -LiteralPath $product -Recurse -Force -ErrorAction Stop |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
if ($reparse.Count -ne 0) { throw 'The product package contains a reparse point.' }

$productVerification = & (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') `
    -PackageRoot $product -Role product -ReleaseVersion $releaseVersion
$productManifest = Join-Path $product 'package-manifest.json'
$productSignature = Join-Path $product 'package-manifest.p7s'

if($CreateOnly -and [string]::IsNullOrWhiteSpace($OutputRoot)){throw 'Create-only installer requires an explicit output path.'}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root 'artifacts\release\installer'
}
$releaseBoundary = Join-Path $root 'artifacts\release'
$output = Assert-CortexBuildPath -Path $OutputRoot -Boundary $releaseBoundary
if ($product -eq $output -or $product.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($product + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer output must be separate from its admitted product input.'
}
if($CreateOnly -and (Test-Path -LiteralPath $output)){throw "Create-only installer output already exists: '$output'. Retain it for review."}
$workspace=if($CreateOnly){Enter-CortexBuildWorkspace -ProjectRoot $root -CreateOnlyName ('pao-maintenance-installer-'+$releaseVersion)}else{Enter-CortexBuildWorkspace -ProjectRoot $root}
try {
if($CreateOnly){New-Item -ItemType Directory -Path $output -ErrorAction Stop|Out-Null}else{Reset-CortexBuildDirectory -Path $output -Boundary $releaseBoundary}
$work = $workspace.Path
$package = $output
$sourcePath = Join-Path $work 'PegasusCortexInstaller.nsi'
$installerPath = Join-Path $package 'PegasusCortexInstaller.exe'
Import-Module (Join-Path $PSScriptRoot 'windows-installer\PegasusWindowsInstaller.psm1') -Force
$definition = Read-PegasusInstallerDefinition -Path (Join-Path $root 'deploy\x64\windows-installer.json')
$definition.releaseVersion = $releaseVersion
$definition.fileVersion = $fileVersion
$definition.launchPath = 'PegasusCortex.exe'
$script = New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $product `
    -OutputExecutable $installerPath -PublisherThumbprint $PublisherThumbprint -Provider $Provider
[IO.File]::WriteAllText($sourcePath, $script, [Text.UTF8Encoding]::new($false))

$compiler = Join-Path $product 'tools\nsis\3.12\Bin\makensis.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The admitted product package omits its product-local NSIS compiler.'
}
$compileResult = $null
$maximumAttempts=if($CreateOnly){1}else{2}
for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
    $compileResult = Invoke-CortexBuildProcess `
        -Executable $compiler `
        -Arguments @('/V3', '/WX', '/NOCONFIG', '/OUTPUTCHARSET', 'UTF8', $sourcePath) `
        -WorkingDirectory $work `
        -TimeoutSeconds 1800
    if ($compileResult.ExitCode -eq 0 -and (Test-Path -LiteralPath $installerPath -PathType Leaf)) { break }
    if($CreateOnly){throw "The create-only NSIS compile failed; partial installer and original workspace are retained."}
    if (Test-Path -LiteralPath $installerPath) {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction Stop
    }
    if ($attempt -eq 2) {
        $details = Get-CortexBuildProcessOutputTail -Text ($compileResult.StandardOutput + "`n" + $compileResult.StandardError)
        if (-not [string]::IsNullOrWhiteSpace($details)) { $details = "`n$details" }
        throw "The product-local NSIS compiler failed twice; final code $($compileResult.ExitCode).$details"
    }
    Write-Host "The product-local NSIS compiler did not produce the installer; retrying once in the isolated build workspace."
}

Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $package 'LICENSE') -ErrorAction Stop
# The outer coordinator keeps operational references in its adjacent product directory.
$distributionReadme = [IO.File]::ReadAllText((Join-Path $root 'README.md')).Replace('](docs/', '](product/docs/').Replace('](licenses/)', '](product/licenses/)')
[IO.File]::WriteAllText((Join-Path $package 'README.md'), $distributionReadme, [Text.UTF8Encoding]::new($false))
& (Join-Path $root 'tools\signing\Export-PegasusProdAlphaTrustBundle.ps1') `
    -PackageRoot $package -RootThumbprint $RootThumbprint -PublisherThumbprint $PublisherThumbprint | Out-Null
& (Join-Path $root 'tools\signing\Sign-PegasusArtifactTree.ps1') `
    -PackageRoot $package -PublisherThumbprint $PublisherThumbprint -Provider $Provider -PreserveValidPublisherSignatures:$CreateOnly | Out-Null
$companionProduct = Join-Path $package 'product'
New-Item -ItemType Directory -Path $companionProduct -ErrorAction Stop | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $product -Force -ErrorAction Stop) {
    Copy-Item -LiteralPath $item.FullName -Destination $companionProduct -Recurse -Force -ErrorAction Stop
}
& (Join-Path $root 'tools\assets\Attach-VisualLibraryModules.ps1') -InstallerRoot $package | Out-Null
Write-Json -Path (Join-Path $package 'installer-contract.v7.json') -Value ([ordered]@{
    contract='pegasus-cortex-nsis-installer'; version=7; releaseVersion=$releaseVersion;
    channel='Prod Alpha'; companionProductRole='product';
    payloadLayout='adjacent-uncompressed-product-directory'; payloadDirectory='product';
    wizard='shared NSIS Modern UI 2 with readable Windows typography and traditional Welcome, license/notices, Recommended or Custom setup type, relevant components, program/data locations, Ready summary, progress with expandable details, and Finish; existing products offer Modify, Repair or Update, and Uninstall maintenance';
    payloadExecution='the small signed coordinator uses the adjacent uncompressed signed product directory; required core, trust, and every Codex feature have separate routines; Modify executes only explicitly selected optional features; an unselected feature executes nothing';
    packageAdmission='outer detached signature and exact inventory admit the complete companion folder; nested product detached signature and exact immutable-file hashes remain the required security boundary';
    userSessionStartup='runtime remains stopped after setup; explicit tray launch starts the ordinary host; MCP/PAO clients attach without starting it; existing sign-in startup choice is preserved; privileged service is optional and never implicitly installed, enabled or started';
    configuration='one Pegasus Cortex Start Menu shortcut; configuration and service notifications open from its tray menu after setup exits';
    activation='stable launcher validates the protected current catalog and exact selected role/file on activation; runtime initialization does not block setup';
    starterGuidance=[ordered]@{
        optional=$true; defaultEnabled=$false;
        presentation='unchecked finish-page reference';
        relativePath='docs/STARTER_PROGRAMMING_TENETS.txt';
        authority='overview only; knowledge.shared starter records remain authoritative'
    };
    installerTemplate='tools/windows-installer/templates/product.json; shared generator is the default, not a copied NSIS implementation';
    elevation='automatic Windows UAC request for machine setup; no signed-in user process is launched from the elevated installer';
    interruption='core install, upgrade, and repair require owned Cortex components already stopped and refuse loaded product images; setup never kills or restarts them; Codex may remain open when Cortex images are released; explicitly selected Codex registration or guidance routines retain their separate closure requirements';
    programRetention='normal install and upgrade do not scan or prune historical program files; exact cleanup remains an explicit maintenance operation; all database and state files are preserved';
    companionProductManifestSha256=(Get-Sha256 -Path $productManifest);
    companionProductSignatureSha256=(Get-Sha256 -Path $productSignature);
    publisherThumbprint=[string]$productVerification.PublisherThumbprint;
    rootThumbprint=[string]$productVerification.RootThumbprint;
    installRoot='default %ProgramFiles%\Pegasus\Cortex; fresh installs allow a canonical local fixed-drive selection; upgrade and repair reuse the registered location';
    dataRoot='default %ProgramData%\Pegasus\Cortex for all mutable state; fresh installs select a durable local root independently; custom registered data roots are preserved; the exact legacy install-root/state default is copied with originals retained and registry selection last';
    dataIsolation='per-user isolation enforced by Cortex; program installation is machine-wide';
    databasePreservation='same-location setup preserves databases; exact legacy install-local state is copied to the separate data location with originals retained and registry selection last; no database conversion or seeding; fresh installation creates empty folders only';
    modes=@('install','upgrade','repair','modify','uninstall');
    components=@(
        [ordered]@{ id='core'; required=$true; default='selected'; routine='install-upgrade-repair' },
        [ordered]@{ id='trust'; required=$false; default='no-action'; selection='/TRUST=1' },
        [ordered]@{ id='codex-registration'; required=$false; default='no-action'; selection='/CODEXREGISTER=0|1' },
        [ordered]@{ id='codex-guidance'; required=$false; default='no-action'; selection='/CODEXGUIDANCE=0|1' }
    );
    downgrade='blocked';
    progress='plain status stages omit transaction identifiers during normal operation; persistent logs and failure receipts retain one internal RID for diagnostics';
    uninstall='validated uninstall.v1.dat inventory; immutable files and owned execution registrations removed; data-location binding and every database retained; ordinary uninstall exposes no data-deletion option';
    maintenance='the signed coordinator is cached as installer/Setup.exe; Add/Remove Programs exposes exact Modify, Repair, and Uninstall routes; the registered external companion source is reused for repair/update without duplicating the product payload';
    optionDetection='no hidden setup executable or feature-state probe runs before the wizard; every optional feature starts inactive';
    newOptionPolicy='fresh install, upgrade, repair, and Modify leave every unselected optional feature untouched; install, repair, and remove are separate explicitly selected routines';
    trust=[ordered]@{
        optional=$true; selection='/TRUST=0|1';
        interactive='unselected unless the user explicitly selects trust installation';
        silent='unselected unless the unattended caller explicitly supplies /TRUST=1';
        operation='runs before product setup; selected trust retained on product failure'
    };
    stateMigration='disabled-during-normal-install';
    codexRegistration=[ordered]@{
        optional=$true; defaultEnabled=$false; upgradeDefault='no-action'; selection='/CODEXREGISTER=1 selects the independent install or repair routine; /CODEXREGISTER=0 on an installed product selects the independent removal routine';
        defaultTarget='CODEX_HOME or %USERPROFILE%\.codex\config.toml';
        override='/CODEXHOME=<canonical-Codex-home>';
        operation='atomic-stable-mcp-route-replaces-exact-legacy-cortex-plugin';
        preservesUnrelatedConfiguration=$true
    };
    codexGuidance=[ordered]@{
        optional=$true; defaultEnabled=$false; upgradeDefault='no-action'; selection='/CODEXGUIDANCE=1 selects the independent install or repair routine; /CODEXGUIDANCE=0 on an installed product selects the independent removal routine';
        defaultTarget='%USERPROFILE%\.codex\AGENTS.md';
        override='/CODEXAGENTS=<canonical-AGENTS.md>';
        operation='verified-fragments-atomic-managed-block-update';
        preservesUnrelatedGuidance=$true
    }
})
& (Join-Path $root 'deploy\x64\New-PegasusCortexV2PackageManifest.ps1') `
    -PackageRoot $package -ReleaseVersion $releaseVersion -Role product-installer `
    -PublisherThumbprint $PublisherThumbprint -RootThumbprint $RootThumbprint | Out-Null
& (Join-Path $root 'tools\signing\Protect-PegasusPackageManifest.ps1') `
    -PackageRoot $package -PublisherThumbprint $PublisherThumbprint -Provider $Provider | Out-Null
$verification = & (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') `
    -PackageRoot $package -Role product-installer -ReleaseVersion $releaseVersion

[pscustomobject]@{
    InstallerRoot = $output
    EntryPoint = (Join-Path $output 'PegasusCortexInstaller.exe')
    ReleaseVersion = $releaseVersion
    Channel = 'Prod Alpha'
    InstallerSha256 = (Get-Sha256 -Path (Join-Path $output 'PegasusCortexInstaller.exe'))
    ManifestSha256 = (Get-Sha256 -Path (Join-Path $output 'package-manifest.json'))
    SignatureSha256 = (Get-Sha256 -Path (Join-Path $output 'package-manifest.p7s'))
    PublisherThumbprint = $verification.PublisherThumbprint
    RootThumbprint = $verification.RootThumbprint
}
}
finally { Exit-CortexBuildWorkspace -Workspace $workspace }
