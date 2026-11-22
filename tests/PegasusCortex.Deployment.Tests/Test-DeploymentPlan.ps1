[CmdletBinding()]
param(
    [string]$UnifiedPackage = '',
    [string]$InstallerPackage = '',
    [string]$ReleaseVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Get-PathSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Exists = $false; LastWriteTimeUtc = $null }
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    return [pscustomobject]@{
        Exists = $true
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('O')
    }
}

function Assert-OrdinaryTree {
    param([Parameter(Mandatory)][string]$Path)
    $root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    Assert-True -Condition ($root.PSIsContainer -and
        (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) `
        -Message "The package root is not one ordinary directory: '$Path'."
    $reparse = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    Assert-True -Condition ($reparse.Count -eq 0) `
        -Message "The package contains one or more reparse points: '$Path'."
}

function Get-CanonicalJson {
    param([Parameter(Mandatory)][string]$Path)
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
        ConvertFrom-Json -Depth 40 -ErrorAction Stop
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot 'tools\CortexBuildWorkspace.ps1')
$version = Get-CanonicalJson -Path (Join-Path $repositoryRoot 'VERSION.json')
$serviceProgram = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Pegasus.AgentOps.Service\Program.cs') -Raw -Encoding UTF8
$serviceWorker = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Pegasus.AgentOps.Service\ServiceRuntime.cs') -Raw -Encoding UTF8
$serviceRun = $serviceProgram.IndexOf('await host.RunAsync()', [StringComparison]::Ordinal)
$servicePrefix = if ($serviceRun -ge 0) { $serviceProgram.Substring(0, $serviceRun) } else { $serviceProgram }
Assert-True -Condition ($serviceRun -ge 0 -and
    -not $servicePrefix.Contains('ServiceStartupTrace.Write', [StringComparison]::Ordinal) -and
    -not $servicePrefix.Contains('ServiceProcessOwnership.EnsureCurrentProcess', [StringComparison]::Ordinal) -and
    $serviceWorker.Contains('ServiceProcessOwnership.EnsureCurrentProcess', [StringComparison]::Ordinal) -and
    $serviceWorker.Contains('BackgroundService execution begins only after WindowsServiceLifetime', [StringComparison]::Ordinal)) `
    -Message 'The execution service performs protected state work before connecting its host to SCM.'
if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $ReleaseVersion = [string]$version.releaseVersion
}
Assert-True -Condition ($ReleaseVersion -cmatch '^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') `
    -Message 'ReleaseVersion is not one canonical Pegasus release coordinate.'
Assert-True -Condition ([string]$version.releaseChannel -ceq 'Prod Alpha') `
    -Message 'VERSION.json is not on the Prod Alpha channel.'

if ([string]::IsNullOrWhiteSpace($UnifiedPackage)) {
    $UnifiedPackage = Join-Path $repositoryRoot 'artifacts\release\unified'
}
if ([string]::IsNullOrWhiteSpace($InstallerPackage)) {
    $InstallerPackage = Join-Path $repositoryRoot 'artifacts\release\installer'
}
$unified = [IO.Path]::GetFullPath($UnifiedPackage).TrimEnd('\', '/')
$installer = [IO.Path]::GetFullPath($InstallerPackage).TrimEnd('\', '/')
Assert-OrdinaryTree -Path $unified
Assert-OrdinaryTree -Path $installer

$programFiles = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFiles,
    [Environment+SpecialFolderOption]::DoNotVerify)
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($programFiles)) `
    -Message 'The Windows Program Files known folder is unavailable.'
$liveRoot = Join-Path $programFiles 'Pegasus\Cortex'
$beforeLiveRoot = Get-PathSnapshot -Path $liveRoot

$verifier = Join-Path $repositoryRoot 'tools\signing\Test-PegasusSignedPackage.ps1'
$unifiedVerification = & $verifier `
    -PackageRoot $unified -Role product -ReleaseVersion $ReleaseVersion
$installerVerification = & $verifier `
    -PackageRoot $installer -Role product-installer -ReleaseVersion $ReleaseVersion
Assert-True -Condition ($unifiedVerification.Valid -and $installerVerification.Valid) `
    -Message 'A signed package did not pass exact detached-signature and content admission.'
Assert-True -Condition (
    $unifiedVerification.PublisherThumbprint -ceq $installerVerification.PublisherThumbprint -and
    $unifiedVerification.RootThumbprint -ceq $installerVerification.RootThumbprint) `
    -Message 'The unified product and installer are not bound to one signing identity.'

$productManifestPath = Join-Path $unified 'package-manifest.json'
$productSignaturePath = Join-Path $unified 'package-manifest.p7s'
$installerManifest = Get-CanonicalJson -Path (Join-Path $installer 'package-manifest.json')
$productManifest = Get-CanonicalJson -Path $productManifestPath
$contract = Get-CanonicalJson -Path (Join-Path $installer 'installer-contract.v7.json')
Assert-True -Condition (
    [string]$productManifest.schema -ceq 'pegasus.cortex.local-operational-package/1.2' -and
    [string]$productManifest.role -ceq 'product' -and
    [string]$productManifest.identity.targetFramework -ceq 'net10.0' -and
    [string]$productManifest.identity.runtimeIdentifier -ceq 'win-x64' -and
    [bool]$productManifest.identity.selfContained -and
    [int]$productManifest.identity.roles -eq 8) `
    -Message 'The unified product identity is not the expected self-contained .NET 10 x64 contract.'
Assert-True -Condition (
    [string]$installerManifest.role -ceq 'product-installer' -and
    [string]$installerManifest.identity.deploymentMode -ceq 'signed-nsis-adjacent-product-installer' -and
    -not [bool]$installerManifest.identity.selfContained -and
    [string]$installerManifest.identity.companionProductRole -ceq 'product') `
    -Message 'The outer package is not the signed adjacent-product NSIS installer role.'

$productManifestSha = (Get-FileHash -LiteralPath $productManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$productSignatureSha = (Get-FileHash -LiteralPath $productSignaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True -Condition (
    [string]$contract.contract -ceq 'pegasus-cortex-nsis-installer' -and
    [int]$contract.version -eq 7 -and
    [string]$contract.releaseVersion -ceq $ReleaseVersion -and
    [string]$contract.channel -ceq 'Prod Alpha' -and
    [string]$contract.companionProductRole -ceq 'product' -and
    [string]$contract.payloadLayout -ceq 'adjacent-uncompressed-product-directory' -and
    [string]$contract.payloadDirectory -ceq 'product' -and
    [string]$contract.companionProductManifestSha256 -ceq $productManifestSha -and
    [string]$contract.companionProductSignatureSha256 -ceq $productSignatureSha -and
    [string]$contract.installRoot -ceq 'default %ProgramFiles%\Pegasus\Cortex; fresh installs allow a canonical local fixed-drive selection; upgrade and repair reuse the registered location' -and
    [string]$contract.dataRoot -ceq 'default %ProgramData%\Pegasus\Cortex for all mutable state; fresh installs select a durable local root independently; custom registered data roots are preserved; the exact legacy install-root/state default is copied with originals retained and registry selection last' -and
    [string]$contract.dataIsolation -ceq 'per-user isolation enforced by Cortex; program installation is machine-wide' -and
    [string]$contract.databasePreservation -ceq 'same-location setup preserves databases; exact legacy install-local state is copied to the separate data location with originals retained and registry selection last; no database conversion or seeding; fresh installation creates empty folders only' -and
    (@($contract.modes) -join ',') -ceq 'install,upgrade,repair,modify,uninstall' -and
    [string]$contract.downgrade -ceq 'blocked' -and
    [string]$contract.payloadExecution -ceq 'the small signed coordinator uses the adjacent uncompressed signed product directory; required core, trust, and every Codex feature have separate routines; Modify executes only explicitly selected optional features; an unselected feature executes nothing' -and
    [string]$contract.progress -ceq 'plain status stages omit transaction identifiers during normal operation; persistent logs and failure receipts retain one internal RID for diagnostics' -and
    [string]$contract.uninstall -ceq 'validated uninstall.v1.dat inventory; immutable files and owned execution registrations removed; data-location binding and every database retained; ordinary uninstall exposes no data-deletion option' -and
    [string]$contract.maintenance -ceq 'the signed coordinator is cached as installer/Setup.exe; Add/Remove Programs exposes exact Modify, Repair, and Uninstall routes; the registered external companion source is reused for repair/update without duplicating the product payload' -and
    [string]$contract.optionDetection -ceq 'no hidden setup executable or feature-state probe runs before the wizard; every optional feature starts inactive' -and
    [string]$contract.newOptionPolicy -ceq 'fresh install, upgrade, repair, and Modify leave every unselected optional feature untouched; install, repair, and remove are separate explicitly selected routines' -and
    (@($contract.components.id) -join ',') -ceq 'core,trust,codex-registration,codex-guidance' -and
    [bool]$contract.components[0].required -and
    @($contract.components | Select-Object -Skip 1 | Where-Object { [bool]$_.required -or [string]$_.default -cne 'no-action' }).Count -eq 0 -and
    [string]$contract.userSessionStartup -ceq 'runtime remains stopped after setup; explicit tray launch starts the ordinary host; MCP/PAO clients attach without starting it; existing sign-in startup choice is preserved; privileged service is optional and never implicitly installed, enabled or started' -and
    [string]$contract.interruption -ceq 'core install, upgrade, and repair require owned Cortex components already stopped and refuse loaded product images; setup never kills or restarts them; Codex may remain open when Cortex images are released; explicitly selected Codex registration or guidance routines retain their separate closure requirements' -and
    [string]$contract.configuration -ceq 'one Pegasus Cortex Start Menu shortcut; configuration and service notifications open from its tray menu after setup exits' -and
    [string]$contract.activation -ceq 'stable launcher validates the protected current catalog and exact selected role/file on activation; runtime initialization does not block setup' -and
    [string]$contract.programRetention -ceq 'normal install and upgrade do not scan or prune historical program files; exact cleanup remains an explicit maintenance operation; all database and state files are preserved' -and
    $contract.trust.optional -eq $true -and
    [string]$contract.trust.selection -ceq '/TRUST=0|1' -and
    [string]$contract.trust.interactive -ceq 'unselected unless the user explicitly selects trust installation' -and
    [string]$contract.trust.silent -ceq 'unselected unless the unattended caller explicitly supplies /TRUST=1' -and
    [string]$contract.stateMigration -ceq 'disabled-during-normal-install' -and
    $contract.starterGuidance.optional -eq $true -and
    $contract.starterGuidance.defaultEnabled -eq $false -and
    [string]$contract.starterGuidance.presentation -ceq 'unchecked finish-page reference' -and
    [string]$contract.starterGuidance.relativePath -ceq 'docs/STARTER_PROGRAMMING_TENETS.txt' -and
    [string]$contract.starterGuidance.authority -ceq 'overview only; knowledge.shared starter records remain authoritative' -and
    $contract.codexRegistration.optional -eq $true -and
    $contract.codexRegistration.defaultEnabled -eq $false -and
    [string]$contract.codexRegistration.upgradeDefault -ceq 'no-action' -and
    [string]$contract.codexRegistration.selection -ceq '/CODEXREGISTER=1 selects the independent install or repair routine; /CODEXREGISTER=0 on an installed product selects the independent removal routine' -and
    $contract.codexGuidance.optional -eq $true -and
    $contract.codexGuidance.defaultEnabled -eq $false -and
    [string]$contract.codexGuidance.upgradeDefault -ceq 'no-action' -and
    [string]$contract.codexGuidance.selection -ceq '/CODEXGUIDANCE=1 selects the independent install or repair routine; /CODEXGUIDANCE=0 on an installed product selects the independent removal routine' -and
    [bool]$contract.codexGuidance.preservesUnrelatedGuidance) `
    -Message 'The v7 installer contract does not bind adjacent delivery, independent durable locations, database preservation, section-isolated optional features, explicit trust, and guidance policy.'

$companion = Join-Path $installer 'product'
$companionVerification = & $verifier -PackageRoot $companion -Role product -ReleaseVersion $ReleaseVersion
Assert-True -Condition ($companionVerification.Valid -and
    (Get-FileHash -LiteralPath (Join-Path $companion 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $productManifestSha -and
    (Get-FileHash -LiteralPath (Join-Path $companion 'package-manifest.p7s') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $productSignatureSha) `
    -Message 'The adjacent companion product is absent, changed, or no longer independently signed.'

$catalogPath = Join-Path $unified "manifests\releases\$ReleaseVersion\product-catalog.v1.json"
$catalog = Get-CanonicalJson -Path $catalogPath
$expectedRoles = @('host','launcher','mcp','pao','process-host','service','setup','ui-host')
$actualRoles = @($catalog.roles.role | Sort-Object)
$expectedModules = @('assets.chat-icons','assets.files','core.bootstrap','execution.local','guidance.codex','knowledge.shared','pao.client','semantics.local','storage.sqlite')
$actualModules = @($catalog.modules.moduleId | Sort-Object)
Assert-True -Condition (
    [string]$catalog.contract -ceq 'pegasus-cortex-product-catalog' -and
    [string]$catalog.releaseVersion -ceq $ReleaseVersion -and
    [string]$catalog.channel -ceq 'Prod Alpha' -and
    [string]$catalog.executionProtocol -ceq '1.0' -and
    [int]$catalog.cortexMcpSchema -eq 2 -and
    [int]$catalog.paoSchema -eq 3 -and
    (($actualRoles -join ',') -ceq (($expectedRoles | Sort-Object) -join ',')) -and
    (($actualModules -join ',') -ceq (($expectedModules | Sort-Object) -join ','))) `
    -Message 'The product catalog does not expose the exact eight-role, nine-module unified product.'
$ownedModuleRoles = @($catalog.modules.roles | ForEach-Object { $_ } | Sort-Object)
$sqliteModule = @($catalog.modules | Where-Object { [string]$_.moduleId -ceq 'storage.sqlite' })
$semanticsModule = @($catalog.modules | Where-Object { [string]$_.moduleId -ceq 'semantics.local' })
Assert-True -Condition (
    (($ownedModuleRoles -join ',') -ceq (($expectedRoles | Sort-Object) -join ',')) -and
    $sqliteModule.Count -eq 1 -and [bool]$sqliteModule[0].required -and
    [string]$sqliteModule[0].statePolicy -ceq 'preserve' -and
    @($sqliteModule[0].provides) -ccontains 'storage.sqlite/2' -and
    $semanticsModule.Count -eq 1 -and
    @($semanticsModule[0].requires) -ccontains 'storage.sqlite/2') `
    -Message 'The selected module graph does not bind every role once or preserve the required SQLite provider contract.'
$assetModule = @($catalog.modules | Where-Object { [string]$_.moduleId -ceq 'assets.files' })
$iconModule = @($catalog.modules | Where-Object { [string]$_.moduleId -ceq 'assets.chat-icons' })
Assert-True -Condition (
    $assetModule.Count -eq 1 -and -not [bool]$assetModule[0].required -and
    [string]$assetModule[0].statePolicy -ceq 'preserve' -and
    (@($assetModule[0].provides) -join ',') -ceq 'assets.files/2' -and
    @($assetModule[0].roles).Count -eq 0 -and
    $iconModule.Count -eq 1 -and -not [bool]$iconModule[0].required -and
    [string]$iconModule[0].statePolicy -ceq 'none' -and
    (@($iconModule[0].provides) -join ',') -ceq 'assets.chat-icons/1' -and
    (@($iconModule[0].requires) -join ',') -ceq 'assets.files/2' -and
    @($iconModule[0].roles).Count -eq 0 -and
    @($semanticsModule[0].requires | Where-Object { $_ -clike 'assets.*' }).Count -eq 0) `
    -Message 'Optional file/icon modules became required, lost state preservation, or acquired runtime roles.'
foreach ($role in $catalog.roles) {
    $relative = [string]$role.relativePath
    Assert-True -Condition (-not [IO.Path]::IsPathRooted($relative) -and
        $relative -notmatch '(^|[\\/])\.\.([\\/]|$)' -and
        (Test-Path -LiteralPath (Join-Path $unified $relative) -PathType Leaf)) `
        -Message "Catalog role '$($role.role)' is absent or escapes the product root."
}

$starterRoot = Join-Path $unified 'bin\semantics\behavior-packs'
$starter = Get-CanonicalJson -Path (Join-Path $starterRoot 'starter-corpus.json')
$recordFiles = @(Get-ChildItem -LiteralPath $starterRoot -Recurse -File -Filter '*.record.md')
Assert-True -Condition (
    [string]$starter.schema -ceq 'pegasus.cortex.starter-corpus/1.0' -and
    @($starter.packs).Count -eq 4 -and
    @($starter.packs | Where-Object { [string]$_.namespace -cne 'knowledge.shared' }).Count -eq 0 -and
    (@($starter.packs.path | Sort-Object) -join ',') -ceq 'cerebral-guides,general-knowledge,generalized-programming-tenets,windows-operational-knowledge' -and
    $recordFiles.Count -eq 94) `
    -Message 'The bounded 94-record shared starter corpus is absent or changed shape.'
$starterRecordIds = @(
    foreach ($pack in $starter.packs) {
        $packManifest = Get-CanonicalJson -Path (Join-Path $starterRoot "$([string]$pack.path)\behavior-pack.json")
        Assert-True -Condition ([int]$packManifest.packRevision -ge 1) `
            -Message "Behavior pack '$([string]$pack.path)' has no managed revision."
        foreach ($record in $packManifest.records) { [string]$record.recordId }
    })
Assert-True -Condition (
    $starterRecordIds.Count -eq 94 -and
    @($starterRecordIds | Sort-Object -Unique).Count -eq 94 -and
    $starterRecordIds -ccontains 'Knowledge.Cerebral.Guide' -and
    $starterRecordIds -ccontains 'Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions' -and
    $starterRecordIds -ccontains 'Knowledge.Windows' -and
    $starterRecordIds -ccontains 'Knowledge.Windows.FileTransfer.Robocopy' -and
    $starterRecordIds -ccontains 'Knowledge.Windows.IPC.NamedPipes' -and
    @($starterRecordIds | Where-Object { $_ -clike 'guide.cerebral.*' }).Count -eq 0) `
    -Message 'The modular Cerebral and Windows shared knowledge packs are absent, duplicated, or retain superseded identities.'
Assert-True -Condition (
    (Test-Path -LiteralPath (Join-Path $unified 'bin\semantics\guidance\pegasus-cortex-agents.v2.md') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $unified 'bin\semantics\guidance\pegasus-cortex-pao.v1.md') -PathType Leaf)) `
    -Message 'The managed Cortex and PAO guidance fragments are absent.'
$packagedGuidance = Get-Content -LiteralPath (Join-Path $unified 'bin\semantics\guidance\pegasus-cortex-agents.v2.md') -Raw -Encoding UTF8
Assert-True -Condition ($packagedGuidance.StartsWith("## Pegasus Cortex continuity`n", [StringComparison]::Ordinal) -and
    $packagedGuidance.TrimEnd().EndsWith('End of Pegasus Cortex continuity', [StringComparison]::Ordinal) -and
    -not $packagedGuidance.Contains('PEGASUS:CORTEX:MANAGED', [StringComparison]::Ordinal)) `
    -Message 'The packaged Cortex guidance does not use the stable compact boundaries.'

$requiredFiles = @(
    'PegasusCortex.exe',
    'bin\pao.exe',
    'installer\PegasusCortex.Setup.exe',
    'docs\STARTER_PROGRAMMING_TENETS.txt',
    'docs\PAO_WINDOWS_REGISTRY.md',
    'docs\PAO_PROCESS_THREADS.md',
    'docs\PAO_CERTIFICATES.md',
    'tools\nsis\3.12\Bin\makensis.exe',
    'licenses\nsis-3.12\COPYING'
)
foreach ($relative in $requiredFiles) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $unified $relative) -PathType Leaf) `
        -Message "The unified product omits required file '$relative'."
}
$outerInstaller = Join-Path $installer 'PegasusCortexInstaller.exe'
$outerSignature = Get-AuthenticodeSignature -LiteralPath $outerInstaller
Assert-True -Condition ($null -ne $outerSignature.SignerCertificate -and
    $outerSignature.SignerCertificate.Thumbprint -ceq $installerVerification.PublisherThumbprint) `
    -Message 'The NSIS installer does not carry the pinned publisher Authenticode signature.'

$setup = Join-Path $unified 'installer\PegasusCortex.Setup.exe'
$catalogRun = Invoke-CortexBuildProcess -Executable $setup `
    -Arguments @('validate-catalog', $catalogPath) -WorkingDirectory $unified -TimeoutSeconds 120
Assert-True -Condition ($catalogRun.ExitCode -eq 0) `
    -Message "The packaged setup rejected the product catalog with code $($catalogRun.ExitCode)."
$catalogReceipt = $catalogRun.StandardOutput | ConvertFrom-Json -Depth 10
Assert-True -Condition ($catalogReceipt.ok -and [int]$catalogReceipt.roles -eq 8 -and
    [string]$catalogReceipt.releaseVersion -ceq $ReleaseVersion) `
    -Message 'The packaged setup catalog receipt is incomplete.'
$packageRun = Invoke-CortexBuildProcess -Executable $setup `
    -Arguments @('verify-package', $unified) -WorkingDirectory $unified -TimeoutSeconds 120
Assert-True -Condition ($packageRun.ExitCode -eq 0) `
    -Message "The packaged setup rejected its signed package with code $($packageRun.ExitCode)."
$packageReceipt = $packageRun.StandardOutput | ConvertFrom-Json -Depth 10
Assert-True -Condition ($packageReceipt.ok -and [int]$packageReceipt.files -eq @($productManifest.files).Count -and
    [string]$packageReceipt.releaseVersion -ceq $ReleaseVersion) `
    -Message 'The packaged setup signature-first admission receipt is incomplete.'

# Read-only discovery from the shipping PAO image; this never issues a certificate
# operation or connects to a product host.
$packagedPao = Join-Path $unified 'bin\pao.exe'
foreach ($action in @('certificate_issue_server', 'certificate_store_bind_server')) {
    $helpRun = Invoke-CortexBuildProcess -Executable $packagedPao `
        -Arguments @('help', $action) -WorkingDirectory $unified -TimeoutSeconds 30
    Assert-True -Condition ($helpRun.ExitCode -eq 0) `
        -Message "The packaged PAO did not expose $action."
    # Positional CLI help deliberately uses FormatHumanHelp; machine help is
    # covered by the captured MCP contract checks. Validate this shipping surface.
    $helpText = $helpRun.StandardOutput.Replace("`r`n", "`n")
    $allowedLine = @($helpText.Split("`n") | Where-Object { $_.StartsWith('Allowed: ', [StringComparison]::Ordinal) })
    $allowed = if ($allowedLine.Count -eq 1) { @($allowedLine[0].Substring(9).Split(',') | ForEach-Object { $_.Trim() }) } else { @() }
    Assert-True -Condition ($helpText.StartsWith("PAO $action`n", [StringComparison]::Ordinal) -and
        $helpText.Contains('Required: text, rid', [StringComparison]::Ordinal) -and
        $allowed -contains 'text' -and $allowed -contains 'rid' -and $allowed -notcontains 'root') `
        -Message "The packaged $action help lost its exact owner/RID contract."
    $bodyMarker = "Standard input:`n"
    $bodyStart = $helpText.IndexOf($bodyMarker, [StringComparison]::Ordinal)
    $bodyEnd = if ($bodyStart -ge 0) { $helpText.IndexOf("`nRequired:", $bodyStart + $bodyMarker.Length, [StringComparison]::Ordinal) } else { -1 }
    Assert-True -Condition ($bodyStart -ge 0 -and $bodyEnd -gt $bodyStart) `
        -Message "The packaged $action help omitted its bounded input example."
    $bodyStart += $bodyMarker.Length
    $body = $helpText.Substring($bodyStart, $bodyEnd - $bodyStart) | ConvertFrom-Json -Depth 12
    Assert-True -Condition ([int]$body.version -eq 1 -and $body.reference -and
        $(if ($action -ceq 'certificate_issue_server') { $body.dnsNames -contains 'localhost' -and $body.ipAddresses -contains '127.0.0.1' -and $body.issuer.certificateId -cmatch '^[a-f0-9]{64}$' }
          else { $body.certificateId -cmatch '^[a-f0-9]{64}$' -and @($body.PSObject.Properties).Count -eq 3 })) `
        -Message "The packaged $action example is not a versioned typed request."
}

foreach ($action in @('windows_service_read', 'windows_service_startup')) {
    $helpRun = Invoke-CortexBuildProcess -Executable $packagedPao `
        -Arguments @('help', $action) -WorkingDirectory $unified -TimeoutSeconds 30
    $helpText = $helpRun.StandardOutput.Replace("`r`n", "`n")
    # Control selects approved SYSTEM intent or the explicit same-owner UAC route.
    # Only the body and mutation RID are unconditional; legacy root codes are absent.
    $required = if ($action -ceq 'windows_service_read') { 'Required: text' } else { 'Required: text, rid' }
    $allowedLine = @($helpText.Split("`n") | Where-Object { $_.StartsWith('Allowed: ', [StringComparison]::Ordinal) })
    $allowed = if ($allowedLine.Count -eq 1) { @($allowedLine[0].Substring(9).Split(',') | ForEach-Object { $_.Trim() }) } else { @() }
    $expectedAllowed = @('op', 'text', 'system', 'rid', 'detail', 'elevate', 'wait', 'output')
    Assert-True -Condition ($helpRun.ExitCode -eq 0 -and
        $helpText.StartsWith("PAO $action`n", [StringComparison]::Ordinal) -and
        $helpText.Contains("$required`n", [StringComparison]::Ordinal) -and
        $allowed.Count -eq $expectedAllowed.Count -and $allowed -cnotcontains 'root' -and
        @($expectedAllowed | Where-Object { $_ -cnotin $allowed }).Count -eq 0) `
        -Message "The packaged $action help lost its exact owner/System/UAC action contract."
}

$builderText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools\New-PegasusCortexNsisInstaller.ps1') -Raw -Encoding UTF8
Import-Module (Join-Path $repositoryRoot 'tools\windows-installer\PegasusWindowsInstaller.psm1') -Force
$definition = Read-PegasusInstallerDefinition -Path (Join-Path $repositoryRoot 'deploy\x64\windows-installer.json')
$definition.releaseVersion = $ReleaseVersion
$definition.fileVersion = [string]$version.fileVersion
$definition.launchPath = 'PegasusCortex.exe'
$generatedSource = New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $unified
$requiredBuilderFragments = @(
    'RequestExecutionLevel admin',
    'InstallDir "$PROGRAMFILES64\Pegasus\Cortex"',
    'InstallDirRegKey HKLM "Software\Pegasus\Cortex" "InstallLocation"',
    'StrCpy $DataFolder "$APPDATA\Pegasus\Cortex"',
    'ReadRegStr $R2 HKLM "Software\Pegasus\Cortex" "DataLocation"',
    'StrCmp $R2 "$ExistingInstallRoot\state" data_location_copy_legacy',
    'StrCpy $DataFolder $R2',
    '!define MUI_DIRECTORYPAGE_VARIABLE $DataFolder',
    '!define MUI_PAGE_CUSTOMFUNCTION_PRE DataDirectoryPagePre',
    'Page custom RetainedDataPageCreate',
    'This location is locked during upgrade, repair, or reinstall',
    'Page custom SetupTypePageCreate SetupTypePageLeave',
    '!insertmacro MUI_PAGE_LICENSE',
    'Page custom ReadyPageCreate',
    'Setup plan: run only selected feature routines',
    '"--install-root" "$INSTDIR" "--data-root" "$DataFolder"',
    '!insertmacro MUI_PAGE_DIRECTORY',
    'nsExec::ExecToLog',
    'Setup backend finished with code $0',
    'Section "Pegasus Cortex (required)" SEC_PRODUCT',
    '"install-package"',
    'Section /o "Trust Pegasus Prod Alpha publisher on this machine" SEC_TRUST',
    '!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\docs\STARTER_PROGRAMMING_TENETS.txt"',
    '!define MUI_FINISHPAGE_SHOWREADME_TEXT "View included programming guidance"',
    '!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED',
    'Page custom MaintenancePageCreate MaintenancePageLeave',
    'CopyFiles /SILENT "$EXEPATH" "$INSTDIR\installer\Setup.exe"',
    '"ModifyPath"', '"RepairPath"',
    'WriteUninstaller "$INSTDIR\Uninstall.exe"',
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Cortex'
)
foreach ($fragment in $requiredBuilderFragments) {
    Assert-True -Condition $generatedSource.Contains($fragment, [StringComparison]::Ordinal) `
        -Message "The NSIS builder lost required installer boundary '$fragment'."
}
$normalizedSource = $generatedSource.Replace("`r`n", "`n")
$freshDataDefault = @('  SetShellVarContext all', '  StrCpy $DataFolder "$APPDATA\Pegasus\Cortex"') -join "`n"
$customDataSelection = @('data_location_keep_custom:', '  StrCpy $DataFolder $R2', '  Goto data_location_ready') -join "`n"
$legacyCopySelection = @('data_location_copy_legacy:', '  StrCpy $DataLocationLocked "1"',
    '  StrCpy $DataLocationRelocating "1"', 'data_location_ready:') -join "`n"
Assert-True -Condition ($normalizedSource.Contains($freshDataDefault, [StringComparison]::Ordinal) -and
    $normalizedSource.Contains($customDataSelection, [StringComparison]::Ordinal) -and
    $normalizedSource.Contains($legacyCopySelection, [StringComparison]::Ordinal) -and
    -not $generatedSource.Contains('StrCpy $DataFolder "$ExistingInstallRoot\state"', [StringComparison]::Ordinal)) `
    -Message 'The installer must default to machine ProgramData, preserve registered custom roots, and copy the exact legacy default without reselecting it.'
Assert-True -Condition (-not $generatedSource.Contains('Codex must be completely closed before this upgrade can begin', [StringComparison]::Ordinal)) `
    -Message 'Core install or repair retained an unconditional Codex-closure warning for unselected features.'
$mainShortcut = 'CreateShortCut "$SMPROGRAMS\Pegasus\Pegasus Cortex.lnk" "$INSTDIR\PegasusCortex.exe"'
Assert-True -Condition (($generatedSource.Split($mainShortcut, [StringSplitOptions]::None).Length - 1) -eq 1 -and
    -not $generatedSource.Contains('CreateShortCut "$SMPROGRAMS\Pegasus\Pegasus Cortex Configuration.lnk"',[StringComparison]::Ordinal) -and
    -not $generatedSource.Contains('CreateShortCut "$SMPROGRAMS\Pegasus\Pegasus Cortex Tray.lnk"',[StringComparison]::Ordinal)) `
    -Message 'Cortex must install one Pegasus Cortex shortcut targeting the stable root launcher.'
Assert-True -Condition (-not $generatedSource.Contains('nsExec::ExecToStack') -and
    $definition.userSessionStartup -eq $true -and $definition.keepRuntimeStopped -eq $true -and $definition.removeObsoletePrograms -eq $false -and
    $generatedSource.Contains('Setup left the runtime stopped and preserved existing sign-in startup settings.') -and
    $generatedSource.Contains('require the product already stopped') -and
    -not $generatedSource.Contains('automatic startup is registered') -and
    -not $generatedSource.Contains('then restart and verify') -and
    [regex]::Matches($generatedSource, '(?m)^  ClearErrors\r?\n  ExecWait [^\r\n]+ (?<result>\$(?:[0-3]|R0))\r?\n  IfErrors 0 \+2\r?\n  StrCpy \k<result> "8"\r?$').Count -eq 5 -and
    -not $generatedSource.Contains('"start-user-session"') -and
    -not $generatedSource.Contains('"prune-program-versions"') -and
    -not $generatedSource.Contains('MUI_FINISHPAGE_RUN') -and
    -not $generatedSource.Contains('OpenProductConfiguration')) `
    -Message 'A non-streamed backend launch lost native wait/launch-error handling.'
Assert-True -Condition (
    [regex]::Matches($generatedSource, '(?m)^  nsExec::ExecToLog [^\r\n]+\r?\n  Pop \$0\r?\n  StrCmp \$0 "error" 0 \+2\r?\n  StrCpy \$0 "8"\r?$').Count -eq 5) `
    -Message 'A core or optional feature routine lost its independent bounded-progress transport.'
Assert-True -Condition (
    [string]$definition.dataLocation.schema -ceq 'pegasus.windows-installer.data-location/1' -and
    [string]$definition.dataLocation.defaultBase -ceq 'commonApplicationData' -and
    [string]$definition.dataLocation.relativePath -ceq 'Pegasus/Cortex' -and
    [string]$definition.dataLocation.legacyRelativePath -ceq 'state' -and
    $definition.dataLocation.relocateLegacyData -eq $true -and
    [regex]::Matches($generatedSource, '(?m)^!insertmacro MUI_PAGE_DIRECTORY\r?$').Count -eq 2) `
    -Message 'The Cortex installer lost its separate fresh-install program and database location contract.'
Assert-True -Condition (
    [string]$definition.finishReference.schema -ceq 'pegasus.windows-installer.finish-reference/1' -and
    [string]$definition.finishReference.relativePath -ceq 'docs/STARTER_PROGRAMMING_TENETS.txt' -and
    [string]$definition.finishReference.label -ceq 'View included programming guidance') `
    -Message 'The Cortex installer lost its optional signed starter-guidance reference.'
$installInvocations = @($generatedSource -split '\r?\n' | Where-Object { $_.Contains('"install-package"') })
Assert-True -Condition ($installInvocations.Count -eq 1 -and
    -not $installInvocations[0].Contains('--codex-') -and
    $generatedSource.Contains('Section /o "Install or repair the Cortex connection in Codex"') -and
    $generatedSource.Contains('Section /o "Install or repair Cortex guidance in Codex"') -and
    $generatedSource.Contains('Section /o "Remove the Cortex connection from Codex"') -and
    $generatedSource.Contains('Section /o "Remove Cortex guidance from Codex"') -and
    [regex]::Matches($generatedSource, '"configure-codex-feature"').Count -eq 4) `
    -Message 'The independent optional Cortex registration/guidance selections are incomplete.'
foreach ($invocation in $installInvocations) {
    Assert-True -Condition ($invocation.Contains('"--data-root" "$DataFolder"') -and
        [regex]::Matches($invocation, '"--data-root"').Count -eq 1) `
        -Message 'A Cortex setup branch lost exact data-root forwarding.'
}
Assert-True -Condition (-not $generatedSource.Contains('CreateShortCut "$SMSTARTUP', [StringComparison]::OrdinalIgnoreCase) -and
    -not $generatedSource.Contains('netsh advfirewall', [StringComparison]::OrdinalIgnoreCase)) `
    -Message 'The NSIS builder contains forbidden startup-folder or firewall mutation.'
foreach ($file in @('tools/windows-installer/README.md', 'tools/windows-installer/PegasusWindowsInstaller.psm1',
    'tools/windows-installer/New-PegasusWindowsInstallerSource.ps1', 'tools/windows-installer/New-PegasusSignedManifest.ps1',
    'tools/signing/New-PegasusProdAlphaSigningIdentity.ps1', 'tools/signing/Test-PegasusSignedPackage.ps1')) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $unified $file) -PathType Leaf) -Message "Shared installer kit missing: $file"
}
$sourceGenerator = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools\windows-installer\PegasusWindowsInstaller.psm1') -Raw).Replace("`r`n", "`n").TrimEnd()
$packageGenerator = (Get-Content -LiteralPath (Join-Path $unified 'tools\windows-installer\PegasusWindowsInstaller.psm1') -Raw).Replace("`r`n", "`n")
$signatureOffset = $packageGenerator.IndexOf('# SIG # Begin signature block', [StringComparison]::Ordinal)
if ($signatureOffset -ge 0) { $packageGenerator = $packageGenerator.Substring(0, $signatureOffset) }
Assert-True -Condition ($sourceGenerator -ceq $packageGenerator.TrimEnd()) `
    -Message 'The signed package contains an older shared installer generator; rebuild the existing output from current source.'

$afterLiveRoot = Get-PathSnapshot -Path $liveRoot
Assert-True -Condition ($beforeLiveRoot.Exists -eq $afterLiveRoot.Exists -and
    $beforeLiveRoot.LastWriteTimeUtc -ceq $afterLiveRoot.LastWriteTimeUtc) `
    -Message 'The live Program Files product root changed during read-only package acceptance.'

Write-Output 'PASS unified and installer detached signatures, inventories, and shared signing identity'
Write-Output 'PASS machine-program catalog, exact eight roles, self-contained .NET 10 x64 identity, and v7 adjacent-payload InstallShield-style NSIS contract'
Write-Output 'PASS bounded modular 94-record knowledge.shared starter corpus and managed guidance fragments'
Write-Output 'PASS optional signed starter-guidance overview and unchecked finish-page reference'
Write-Output 'PASS execution service connects its host to SCM before protected state diagnostics'
Write-Output 'PASS packaged setup read-only catalog and signature-first package admission'
Write-Output 'PASS packaged PAO exposes typed TLS server issuance and fixed current-user key binding without executing them'
Write-Output 'PASS packaged PAO exposes general service read and explicit System startup metadata without executing them'
Write-Output 'PASS installer source keeps trust explicit, databases unchanged, separate locations explicit, external-data retention, and no firewall/startup-folder mutation'
Write-Output 'PASS read-only acceptance left the live Program Files product root unchanged'
