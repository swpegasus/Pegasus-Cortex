[CmdletBinding()]
param(
    [string]$ProductPackage = '',
    [switch]$SourceOnly,
    [switch]$RuntimePolicyOnly,
    [switch]$ExecWaitFixture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\CortexBuildWorkspace.ps1')
Import-Module (Join-Path $root 'tools\windows-installer\PegasusWindowsInstaller.psm1') -Force
if (-not $ProductPackage) { $ProductPackage = Join-Path $root 'artifacts\release\unified' }
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function MustFail([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Require $failed 'Invalid fixture was accepted.'
}
function Copy-Definition($Value) { return ($Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json -Depth 10) }
function Require-Ordered([string]$Text, [string[]]$Fragments, [string]$Message) {
    $previous = -1
    foreach ($fragment in $Fragments) {
        $position = $Text.IndexOf($fragment, $previous + 1, [StringComparison]::Ordinal)
        Require ($position -gt $previous) "$Message Missing or out-of-order fragment: $fragment"
        $previous = $position
    }
}
$definition = Read-PegasusInstallerDefinition -Path (Join-Path $root 'deploy\x64\windows-installer.json')
Require ($definition.userSessionStartup -eq $true -and $definition.removeObsoletePrograms -eq $false -and
    $definition.keepRuntimeStopped -eq $true -and
    $definition.userSessionShortcut -ceq 'single' -and
    $definition.payloadLayout -ceq 'adjacent' -and
    $null -eq $definition.PSObject.Properties['installerState'] -and
    $definition.wizardIconPath -ceq 'branding/Cortex.Setup.ico' -and
    $definition.noticePath -ceq 'licenses/INSTALLATION-NOTICES.txt' -and
    $definition.finishReference.schema -ceq 'pegasus.windows-installer.finish-reference/1' -and
    $definition.finishReference.relativePath -ceq 'docs/STARTER_PROGRAMMING_TENETS.txt' -and
    $definition.finishReference.label -ceq 'View included programming guidance') `
    'The Cortex definition lost adjacent payload delivery, minimal session activation, its icon, cleanup opt-out, or the optional starter-guidance reference.'
$templateRoot = Join-Path $root 'tools\windows-installer\templates'
foreach ($name in @('product.json', 'README.md')) {
    Require (Test-Path -LiteralPath (Join-Path $templateRoot $name) -PathType Leaf) "Reusable installer template is missing: $name"
}
$template = Read-PegasusInstallerDefinition -Path (Join-Path $templateRoot 'product.json')
Require ($template.productId -ceq 'pegasus.example' -and $template.name -ceq 'Pegasus Example' -and
    $template.codexGuidance -eq $false -and $template.userSessionStartup -eq $false -and $template.removeObsoletePrograms -eq $false -and
    $null -eq $template.PSObject.Properties['wizardIconPath'] -and $template.noticePath -ceq 'licenses/INSTALLATION-NOTICES.txt' -and
    $null -eq $template.PSObject.Properties['finishReference'] -and
    $null -eq $template.PSObject.Properties['installerState'] -and
    $null -eq $template.PSObject.Properties['payloadLayout'] -and
    $null -eq $template.PSObject.Properties['keepRuntimeStopped'] -and
    $null -eq $template.PSObject.Properties['userSessionShortcut'] -and
    $template.dataLocation.schema -ceq 'pegasus.windows-installer.data-location/1' -and
    $template.dataLocation.relativePath -ceq 'Pegasus/Example') 'Reusable installer template defaults leak Cortex-specific opt-ins.'
$templateReadme = Get-Content -LiteralPath (Join-Path $templateRoot 'README.md') -Raw
foreach ($fragment in @('New-PegasusWindowsInstallerSource.ps1', 'Modern UI 2',
    'Do not copy the generated NSIS', 'start-user-session --install-root',
    'initially unchecked', 'finishReference', 'noticePath', 'Recommended', 'Ready to proceed',
    'does not invoke `start-user-session`')) {
    Require ($templateReadme.Contains($fragment)) "Reusable template guidance lost $fragment"
}
Require (@(Get-ChildItem -LiteralPath $templateRoot -File -Filter '*.nsi').Count -eq 0) `
    'The reusable template duplicates the shared runtime NSIS implementation.'
$definition.name = 'Pegasus Example'
$definition.productId = 'pegasus.example'
$definition.productFolder = 'Example'
$definition.codexGuidance = $false
# Source may already target the next release while the immutable fixture
# payload still supplies the previous admitted package's executable paths.
$fixturePackage = Get-Content -LiteralPath (Join-Path $ProductPackage 'package-manifest.json') -Raw | ConvertFrom-Json -Depth 30
Require ($fixturePackage.releaseVersion -cmatch '^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') 'Fixture package version is invalid.'
$definition.launchPath = 'PegasusCortex.exe'
# Optional capabilities must not leak from the Cortex definition into unrelated products.
$definition.PSObject.Properties.Remove('dataLocation')
$definition.PSObject.Properties.Remove('wizardIconPath')
$definition.PSObject.Properties.Remove('noticePath')
$definition.PSObject.Properties.Remove('userSessionStartup')
$definition.PSObject.Properties.Remove('keepRuntimeStopped')
$definition.PSObject.Properties.Remove('userSessionShortcut')
$definition.PSObject.Properties.Remove('removeObsoletePrograms')
$definition.PSObject.Properties.Remove('finishReference')
$definition.PSObject.Properties.Remove('installerState')
$definition.PSObject.Properties.Remove('payloadLayout')
$source = New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $ProductPackage
$keepStoppedOff = Copy-Definition $definition
$keepStoppedOff | Add-Member -NotePropertyName keepRuntimeStopped -NotePropertyValue $false
Require ((New-PegasusWindowsInstallerSource -Definition $keepStoppedOff -ProductDirectory $ProductPackage) -ceq $source) `
    'Explicitly disabled keep-stopped presentation changed the existing default installer.'
foreach ($sessionStartup in @($false, $true)) {
    $legacySession = Copy-Definition $definition
    $legacySession | Add-Member -NotePropertyName userSessionStartup -NotePropertyValue $sessionStartup
    $legacySource = New-PegasusWindowsInstallerSource -Definition $legacySession -ProductDirectory $ProductPackage
    $explicitLegacy = Copy-Definition $legacySession
    $explicitLegacy | Add-Member -NotePropertyName keepRuntimeStopped -NotePropertyValue $false
    Require ((New-PegasusWindowsInstallerSource -Definition $explicitLegacy -ProductDirectory $ProductPackage) -ceq $legacySource) `
        'The existing resident-session installer changed without a keep-stopped opt-in.'
    $stopped = Copy-Definition $legacySession
    $stopped | Add-Member -NotePropertyName keepRuntimeStopped -NotePropertyValue $true
    $stoppedSource = New-PegasusWindowsInstallerSource -Definition $stopped -ProductDirectory $ProductPackage
    Require ($stoppedSource.Contains('Setup left the runtime stopped and preserved existing sign-in startup settings.') -and
        $stoppedSource.Contains('start the product explicitly when ready.') -and
        $stoppedSource.Contains('require the product already stopped') -and
        -not $stoppedSource.Contains('automatic startup is registered') -and
        -not $stoppedSource.Contains('starts on first use') -and
        -not $stoppedSource.Contains('then restart and verify') -and
        -not $stoppedSource.Contains('MUI_FINISHPAGE_RUN') -and
        -not $stoppedSource.Contains('"start-user-session"')) `
        'Keep-stopped presentation promises automatic startup, shutdown, restart, or a launch hook.'
}
foreach ($invalid in @($null, 'true', 'false', 0, 1, @($true), [pscustomobject]@{ enabled = $true })) {
    $bad = Copy-Definition $definition
    $bad | Add-Member -NotePropertyName keepRuntimeStopped -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
if ($RuntimePolicyOnly) {
    $copyDefinition = Copy-Definition $definition
    $copyDefinition | Add-Member -NotePropertyName dataLocation -NotePropertyValue ([pscustomobject]@{
        schema='pegasus.windows-installer.data-location/1'; defaultBase='commonApplicationData'
        relativePath='Pegasus/Example'; legacyRelativePath='state'; relocateLegacyData=$true
    })
    $copySource = New-PegasusWindowsInstallerSource -Definition $copyDefinition -ProductDirectory $ProductPackage
    foreach ($fragment in @('StrCmp $R2 "$ExistingInstallRoot\state" data_location_copy_legacy',
        'data_location_keep_custom:', 'StrCpy $DataFolder $R2', 'StrCpy $DataLocationRelocating "1"',
        'Copy existing data; retain the originals.', 'Original files remain in place.',
        'Originals retained; no database conversion.', '"--data-root" "$DataFolder"')) {
        Require ($copySource.Contains($fragment)) "Legacy data-copy handoff lost $fragment"
    }
    $copyOff = Copy-Definition $copyDefinition
    $copyOff.dataLocation.PSObject.Properties.Remove('relocateLegacyData')
    $oldDataSource = New-PegasusWindowsInstallerSource -Definition $copyOff -ProductDirectory $ProductPackage
    $copyOff.dataLocation | Add-Member -NotePropertyName relocateLegacyData -NotePropertyValue $false
    Require ((New-PegasusWindowsInstallerSource -Definition $copyOff -ProductDirectory $ProductPackage) -ceq $oldDataSource) `
        'An absent/disabled copy opt-in changed existing data-location behavior.'
    foreach ($invalid in @($null, 'true', 1, @($true))) {
        $bad = Copy-Definition $copyDefinition
        $bad.dataLocation.relocateLegacyData = $invalid
        MustFail { Assert-PegasusInstallerDefinition $bad }
    }
    Write-Host 'PASS keep-stopped installer presentation, default/resident compatibility and invalid-policy rejection (generated source only; no compile or install).'
    return
}
foreach ($text in @('Name "Pegasus Example Prod Alpha"', 'Software\Pegasus\Example',
    'Uninstall\Pegasus Example', 'OutFile "${PAO_OUT}"', '-ComponentId "pegasus.example"')) {
    Require ($source.Contains($text)) "Generic installer lost $text"
}
Require (-not $source.Contains('--codex-agents')) 'An unrelated product received Codex guidance mutation.'
Require (-not $source.Contains('!uninstfinalize')) 'PAO development source contains a release-only hook.'
Require (-not $source.Contains('--data-root') -and -not $source.Contains('Var DataFolder') -and
    -not $source.Contains('Function RetainedDataPageCreate')) 'An opt-out product received the data-location capability.'
Require (-not $source.Contains('"start-user-session"') -and -not $source.Contains('--dashboard') -and
    -not $source.Contains('MUI_FINISHPAGE_RUN') -and -not $source.Contains('MUI_FINISHPAGE_SHOWREADME') -and
    -not $source.Contains('!define MUI_ICON')) `
    'An unrelated product received session-start, dashboard, or product-specific wizard-icon behavior.'
Require (-not $source.Contains('"prune-program-versions"')) 'An opt-out product received obsolete-program cleanup.'
$cleanupOffDefinition = Copy-Definition $definition
$cleanupOffDefinition | Add-Member -NotePropertyName removeObsoletePrograms -NotePropertyValue $false
Require ((New-PegasusWindowsInstallerSource -Definition $cleanupOffDefinition -ProductDirectory $ProductPackage) -ceq $source) `
    'Explicitly disabled obsolete-program cleanup differs from the absent default.'
foreach ($invalid in @($null, 'true', 'false', 0, 1, @($true), [pscustomobject]@{ enabled = $true })) {
    $bad = Copy-Definition $cleanupOffDefinition
    $bad.removeObsoletePrograms = $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$adjacentDefinition = Copy-Definition $definition
$adjacentDefinition | Add-Member -NotePropertyName payloadLayout -NotePropertyValue 'adjacent'
$adjacentSource = New-PegasusWindowsInstallerSource -Definition $adjacentDefinition -ProductDirectory $ProductPackage
foreach ($text in @('Var PayloadRoot', 'Var PayloadSourceRoot', 'StrCpy $PayloadSourceRoot "$EXEDIR"',
    'StrCpy $PayloadRoot "$PayloadSourceRoot\product"',
    'ReadRegStr $ExistingInstallSource HKLM "Software\Pegasus\Example" "InstallSource"',
    'StrCpy $PayloadSourceRoot "$ExistingInstallSource"',
    'IfFileExists "$PayloadRoot\package-manifest.json" product_payload_ready',
    '"$PayloadRoot\installer\PegasusCortex.Setup.exe" "install-package" "$PayloadRoot"',
    'StrCmp $MaintenanceAction "modify" product_modify_only',
    'Modify mode leaves required Cortex program files and databases unchanged')) {
    Require ($adjacentSource.Contains($text)) "Adjacent installer source lost $text"
}
Require ($adjacentSource.Contains('WriteRegStr HKLM "Software\Pegasus\Example" "InstallSource" "$PayloadSourceRoot"') -and
    $adjacentSource.Contains('WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Example" "InstallSource" "$PayloadSourceRoot"')) `
    'Adjacent maintenance did not retain the external companion source for repair/update.'
Require (-not $adjacentSource.Contains('File /r ') -and -not $adjacentSource.Contains('$PLUGINSDIR\product') -and
    $adjacentSource.Contains('-File "$PayloadRoot\installer\Invoke-PegasusProdAlphaTrust.ps1"')) `
    'Adjacent delivery still embeds or extracts the product instead of using one companion payload root.'
foreach ($invalid in @($null, '', 'loose', 'Embedded', 0, $false, @('adjacent'))) {
    $bad = Copy-Definition $definition
    $bad | Add-Member -NotePropertyName payloadLayout -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$startupOffDefinition = Copy-Definition $definition
$startupOffDefinition | Add-Member -NotePropertyName userSessionStartup -NotePropertyValue $false
Require ((New-PegasusWindowsInstallerSource -Definition $startupOffDefinition -ProductDirectory $ProductPackage) -ceq $source) `
    'Explicitly disabled session startup differs from the absent-capability default.'
foreach ($invalid in @($null, 'true', 'false', 0, 1, @($true), [pscustomobject]@{ enabled = $true })) {
    $bad = Copy-Definition $startupOffDefinition
    $bad.userSessionStartup = $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
foreach ($invalid in @($null, $false, 1, @('icon.ico'), '', '../icon.ico', '/icon.ico', 'C:/icon.ico',
    'icons\\icon.ico', 'icons//icon.ico', 'icons/./icon.ico', 'icons/../icon.ico',
    'icons/icon.ico.', 'icons/icon.ico ', 'icons/icon.exe', 'icons/icon.png', 'state/icon.ico',
    'icons/a".ico', 'icons/a$.ico', "icons/a`n.ico", ('a' * 160 + '.ico'))) {
    $bad = Copy-Definition $definition
    $bad | Add-Member -NotePropertyName wizardIconPath -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$missingIcon = Copy-Definition $definition
$missingIcon | Add-Member -NotePropertyName wizardIconPath -NotePropertyValue 'installer/missing-fixture-icon.ico'
MustFail { New-PegasusWindowsInstallerSource -Definition $missingIcon -ProductDirectory $ProductPackage }
foreach ($invalid in @($null, $false, 1, '', '../notice.txt', '/notice.txt', 'C:/notice.txt',
    'licenses\\notice.txt', 'licenses/notice.md', 'state/notice.txt', 'licenses/a$.txt', ('a' * 160 + '.txt'))) {
    $bad = Copy-Definition $definition
    $bad | Add-Member -NotePropertyName noticePath -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$missingNotice = Copy-Definition $definition
$missingNotice | Add-Member -NotePropertyName noticePath -NotePropertyValue 'licenses/missing-notice.txt'
MustFail { New-PegasusWindowsInstallerSource -Definition $missingNotice -ProductDirectory $ProductPackage }
$noticeDefinition = Copy-Definition $definition
$noticeDefinition | Add-Member -NotePropertyName noticePath -NotePropertyValue 'licenses/AgentOps-THIRD-PARTY-NOTICES.txt'
$noticeSource = New-PegasusWindowsInstallerSource -Definition $noticeDefinition -ProductDirectory $ProductPackage
Require-Ordered $noticeSource @('!insertmacro MUI_PAGE_WELCOME',
    '!insertmacro MUI_PAGE_LICENSE', 'Page custom SetupTypePageCreate SetupTypePageLeave') `
    'The optional license-and-notices page is absent or out of order.'

$referenceDefinition = Copy-Definition $definition
$referenceDefinition | Add-Member -NotePropertyName finishReference -NotePropertyValue ([pscustomobject]@{
    schema = 'pegasus.windows-installer.finish-reference/1'
    relativePath = 'docs/execution/NSIS.md'
    label = 'View included guidance'
})
$referenceSource = New-PegasusWindowsInstallerSource -Definition $referenceDefinition -ProductDirectory $ProductPackage
foreach ($text in @('!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\docs\execution\NSIS.md"',
    '!define MUI_FINISHPAGE_SHOWREADME_TEXT "View included guidance"',
    '!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED')) {
    Require ($referenceSource.Contains($text)) "Finish-reference source lost $text"
}
Require (-not $referenceSource.Contains('MUI_FINISHPAGE_RUN')) `
    'A documentation reference implicitly enabled the product configuration action.'
foreach ($invalid in @(
    $null, $false, 'docs/readme.txt',
    [pscustomobject]@{ schema='wrong'; relativePath='docs/readme.txt'; label='View guidance' },
    [pscustomobject]@{ schema='pegasus.windows-installer.finish-reference/1'; relativePath='../readme.txt'; label='View guidance' },
    [pscustomobject]@{ schema='pegasus.windows-installer.finish-reference/1'; relativePath='state/readme.txt'; label='View guidance' },
    [pscustomobject]@{ schema='pegasus.windows-installer.finish-reference/1'; relativePath='docs/readme.exe'; label='View guidance' },
    [pscustomobject]@{ schema='pegasus.windows-installer.finish-reference/1'; relativePath='docs/readme.txt'; label='Bad $label' })) {
    $bad = Copy-Definition $definition
    $bad | Add-Member -NotePropertyName finishReference -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$missingReference = Copy-Definition $definition
$missingReference | Add-Member -NotePropertyName finishReference -NotePropertyValue ([pscustomobject]@{
    schema='pegasus.windows-installer.finish-reference/1'; relativePath='docs/missing.txt'; label='View guidance'
})
MustFail { New-PegasusWindowsInstallerSource -Definition $missingReference -ProductDirectory $ProductPackage }

$dataDefinition = Copy-Definition $definition
$dataDefinition | Add-Member -NotePropertyName dataLocation -NotePropertyValue ([pscustomobject]@{
    schema = 'pegasus.windows-installer.data-location/1'
    defaultBase = 'commonApplicationData'
    relativePath = 'Pegasus/Example'
    legacyRelativePath = 'state'
})
$dataSource = New-PegasusWindowsInstallerSource -Definition $dataDefinition -ProductDirectory $ProductPackage
foreach ($text in @('SetShellVarContext all', 'StrCpy $DataFolder "$APPDATA\Pegasus\Example"',
    'ReadRegStr $R2 HKLM "Software\Pegasus\Example" "DataLocation"',
    'StrCpy $DataFolder "$ExistingInstallRoot\state"', 'StrCpy $DataLocationLocked "1"',
    'Function DataDirectoryPagePre', 'StrCmp $DataLocationLocked "1" 0 data_directory_page_show',
    '!define MUI_DIRECTORYPAGE_VARIABLE $DataFolder', '!define MUI_PAGE_CUSTOMFUNCTION_PRE DataDirectoryPagePre',
    'Function RetainedDataPageCreate', 'Page custom RetainedDataPageCreate',
    'This location is locked during upgrade, repair, or reinstall',
    'Setup changes program files only; it does not copy, replace, seed, or migrate databases.')) {
    Require ($dataSource.Contains($text)) "Data-location source lost $text"
}
Require (-not $dataSource.Contains('un.Permanently remove') -and
    -not $dataSource.Contains('"remove-state" "remove-all-state"')) `
    'Ordinary uninstall still exposed a database/state deletion option.'
Require ([regex]::Matches($dataSource, '(?m)^!insertmacro MUI_PAGE_DIRECTORY\r?$').Count -eq 2) 'Fresh installation does not expose separate modern program and database directory pages.'
Require-Ordered $dataSource @('!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPagePre',
    '!insertmacro MUI_PAGE_DIRECTORY', '!define MUI_PAGE_CUSTOMFUNCTION_PRE DataDirectoryPagePre',
    '!define MUI_DIRECTORYPAGE_VARIABLE $DataFolder', '!insertmacro MUI_PAGE_DIRECTORY',
    'Page custom RetainedDataPageCreate', '!insertmacro MUI_PAGE_INSTFILES') `
    'Modern fresh/retained location pages changed order.'
Require ($dataSource.Contains('!insertmacro MUI_HEADER_TEXT "Existing database location"')) `
    'The retained location page lost its modern wizard header.'
Require (-not $dataSource.Contains('StrCpy $DataFolder "$APPDATA\Pegasus\Cortex"')) 'The generic data-location capability hard-codes Cortex storage.'
$guidanceDefinition = Copy-Definition $dataDefinition
$guidanceDefinition.codexGuidance = $true
$guidanceSource = New-PegasusWindowsInstallerSource -Definition $guidanceDefinition -ProductDirectory $ProductPackage
$startupDefinition = Copy-Definition $definition
$startupDefinition | Add-Member -NotePropertyName userSessionStartup -NotePropertyValue $true
$startupSource = New-PegasusWindowsInstallerSource -Definition $startupDefinition -ProductDirectory $ProductPackage
$iconDefinition = Copy-Definition $guidanceDefinition
$iconDefinition | Add-Member -NotePropertyName userSessionStartup -NotePropertyValue $true
$iconDefinition | Add-Member -NotePropertyName removeObsoletePrograms -NotePropertyValue $true
$iconDefinition | Add-Member -NotePropertyName wizardIconPath -NotePropertyValue 'tools/nsis/3.12/Contrib/Graphics/Icons/arrow-install.ico'
$iconDefinition | Add-Member -NotePropertyName finishReference -NotePropertyValue ([pscustomobject]@{
    schema='pegasus.windows-installer.finish-reference/1'; relativePath='docs/execution/NSIS.md'; label='View included guidance'
})
$iconSource = New-PegasusWindowsInstallerSource -Definition $iconDefinition -ProductDirectory $ProductPackage
$iconPath = Join-Path ([IO.Path]::GetFullPath($ProductPackage)) $iconDefinition.wizardIconPath.Replace('/', '\')
Require ($iconSource.Contains('!define MUI_ICON "' + $iconPath + '"') -and
    $iconSource.Contains('!define MUI_UNICON "' + $iconPath + '"')) 'The optional payload icon did not bind both wizard icons.'
$sourceCases = @(@{ Name = 'example'; Source = $source; Waits = 5; Directories = 1 },
    @{ Name = 'data'; Source = $dataSource; Waits = 5; Directories = 2 },
    @{ Name = 'data-guidance'; Source = $guidanceSource; Waits = 5; Directories = 2 },
    @{ Name = 'reference'; Source = $referenceSource; Waits = 5; Directories = 1 },
    @{ Name = 'startup'; Source = $startupSource; Waits = 5; Directories = 1 },
    @{ Name = 'notice'; Source = $noticeSource; Waits = 5; Directories = 1 },
    @{ Name = 'icon-all'; Source = $iconSource; Waits = 6; Directories = 2 })
foreach ($case in $sourceCases) {
    Require-Ordered $case.Source @('!include MUI2.nsh', '!insertmacro MUI_PAGE_WELCOME',
        'Page custom SetupTypePageCreate SetupTypePageLeave',
        'Page custom MaintenancePageCreate MaintenancePageLeave',
        '!insertmacro MUI_PAGE_COMPONENTS', '!insertmacro MUI_PAGE_DIRECTORY', 'Page custom ReadyPageCreate',
        '!insertmacro MUI_PAGE_INSTFILES',
        '!insertmacro MUI_PAGE_FINISH', '!insertmacro MUI_UNPAGE_CONFIRM', '!insertmacro MUI_UNPAGE_COMPONENTS',
        '!insertmacro MUI_UNPAGE_INSTFILES', '!insertmacro MUI_UNPAGE_FINISH', '!insertmacro MUI_LANGUAGE "English"') `
        'The default modern install/uninstall wizard page sequence changed.'
    Require ([regex]::Matches($case.Source, '(?m)^!insertmacro MUI_PAGE_DIRECTORY\r?$').Count -eq $case.Directories -and
        -not [regex]::IsMatch($case.Source, '(?m)^(?:Page (?:components|instfiles)|PageEx directory|UninstPage )') -and
        -not $case.Source.Contains('Function .onGUIInit')) 'Legacy pages or a conflicting GUI callback were added to the modern wizard.'
    foreach ($fragment in @('RequestExecutionLevel admin', 'ManifestDPIAware true', 'XPStyle on', 'SetFont "Segoe UI" 9',
        'ShowInstDetails hide', 'ShowUninstDetails show', 'CRCCheck on',
        '!define MUI_ABORTWARNING', '!define MUI_UNABORTWARNING',
        '!define MUI_CUSTOMFUNCTION_GUIINIT InitializeWizard', 'Function InitializeWizard',
        '!define MUI_WELCOMEPAGE_TITLE "Pegasus Example Setup"',
        '!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN', '!insertmacro MUI_FUNCTION_DESCRIPTION_END',
        '!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN', '!insertmacro MUI_UNFUNCTION_DESCRIPTION_END',
        '!insertmacro MUI_DESCRIPTION_TEXT ${SEC_PRODUCT}', '!insertmacro MUI_DESCRIPTION_TEXT ${SEC_TRUST}',
        '!insertmacro MUI_DESCRIPTION_TEXT ${un.SEC_PRODUCT}', '!insertmacro MUI_DESCRIPTION_TEXT ${un.SEC_TRUST}')) {
        Require ($case.Source.Contains($fragment)) "Modern wizard lost $fragment"
    }
    $prepare = [regex]::Match($case.Source, '(?ms)^Section -PreparePayload\r?\n(?<body>.*?)^SectionEnd').Groups['body'].Value
    Require-Ordered $prepare @('DetailPrint "Setup plan: run only selected feature routines', 'InitPluginsDir') `
        'The preparation step lost its bounded selected-feature plan.'
    Require (-not $prepare.Contains('SetOutPath "$PLUGINSDIR\product"') -and -not $prepare.Contains('File /r ')) `
        'The installer coordinator still extracts the complete product before selected sections run.'
    Require (-not $prepare.Contains('Abort') -and -not $prepare.Contains('ExecWait') -and
        -not $prepare.Contains('MessageBox') -and -not $prepare.Contains('Codex must be completely closed')) `
        'Core preparation still warns about, probes, or runs an unselected optional feature.'
    Require-Ordered $case.Source @('Section -PreparePayload', 'InitPluginsDir',
        'Section /o "Trust Pegasus Prod Alpha publisher on this machine" SEC_TRUST',
        'Section "Pegasus Example (required)" SEC_PRODUCT', 'SetOutPath "$PLUGINSDIR\product"',
        'File /r ', '"install-package"') `
        'Preparation must precede trust and the product backend.'
}
foreach ($optOut in @($source, $dataSource, $guidanceSource, $startupSource)) {
    Require (-not $optOut.Contains('"prune-program-versions"')) 'An independent capability implicitly enabled obsolete-program cleanup.'
}
$cleanupSection = [regex]::Match($iconSource, '(?ms)^Section "Pegasus Example \(required\)" SEC_PRODUCT\r?\n(?<body>.*?)^SectionEnd').Groups['body'].Value
Require-Ordered $cleanupSection @('WriteUninstaller "$INSTDIR\Uninstall.exe"',
    'ClearErrors', '"prune-program-versions" "--install-root" "$INSTDIR"',
    'IfErrors 0 +2', 'StrCpy $0 "8"', 'IntCmp $0 0 obsolete_programs_done',
    'SetErrorLevel $0', 'Abort', 'obsolete_programs_done:') `
    'Obsolete-program cleanup must follow product finalization and preserve the exact native failure boundary.'
Require ([regex]::Matches($iconSource, '"prune-program-versions"').Count -eq 1 -and
    -not $cleanupSection.Contains('IfSilent')) 'Cleanup must run exactly once after healthy install, including silent mode.'
foreach ($caseSource in @($startupSource, $iconSource)) {
    $productSection = [regex]::Match($caseSource, '(?ms)^Section "Pegasus Example \(required\)" SEC_PRODUCT\r?\n(?<body>.*?)^SectionEnd').Groups['body'].Value
    Require-Ordered $productSection @('"install-package"', 'product_done:',
        'CopyFiles /SILENT "$EXEPATH" "$INSTDIR\installer\Setup.exe"',
        'WriteUninstaller "$INSTDIR\Uninstall.exe"',
        'CreateShortCut "$SMPROGRAMS\Pegasus\Pegasus Example.lnk"',
        'WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Example" "DisplayName"',
        'WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Example" "ModifyPath"',
        'WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Example" "RepairPath"',
        'WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Example" "NoRepair" 0') `
        'User-session products must finish durable machine registration before optional cleanup.'
    Require (-not $productSection.Contains('"start-user-session"') -and
        -not $caseSource.Contains('MUI_FINISHPAGE_RUN') -and
        -not $caseSource.Contains('OpenProductConfiguration')) `
        'The elevated installer must not launch or gate success on signed-in user-session UI.'
    Require ($caseSource.Contains('signed-in user host starts on first use or at the next sign-in')) `
        'The finish page lost the bounded user-host startup explanation.'
}
Require (-not $startupSource.Contains('--codex-register') -and -not $startupSource.Contains('--data-root')) `
    'Independent generic session startup implicitly enabled data or Codex capabilities.'
foreach ($case in @(@{ Source = $dataSource; Count = 1 }, @{ Source = $guidanceSource; Count = 1 })) {
    $invocations = @($case.Source -split '\r?\n' | Where-Object { $_.Contains('"install-package"') })
    Require ($invocations.Count -eq $case.Count) 'A setup invocation branch is missing from the data-root forwarding fixture.'
    foreach ($invocation in $invocations) {
        Require ($invocation.Contains('"--install-root" "$INSTDIR" "--data-root" "$DataFolder"') -and
            [regex]::Matches($invocation, '"--data-root"').Count -eq 1) 'A setup invocation did not forward exactly one selected data root.'
    }
        Require ($case.Source.Contains('Setup backend finished with code $0') -and
        [regex]::Matches($case.Source, 'SetErrorLevel \$0').Count -ge 3) `
        'Setup transport failure or exact error-code propagation was lost.'
}
# The install backend streams its deliberately bounded progress into the
# visible details pane. Other fixed adapters retain the native wait boundary.
$waitPattern = '(?m)^  ClearErrors\r?\n  ExecWait (?<command>[^\r\n]+) (?<result>\$(?:[0-3]|R0))\r?\n  IfErrors 0 \+2\r?\n  StrCpy \k<result> "8"\r?$'
$streamPattern = '(?m)^  nsExec::ExecToLog (?<command>[^\r\n]+)\r?\n  Pop \$0\r?\n  StrCmp \$0 "error" 0 \+2\r?\n  StrCpy \$0 "8"\r?$'
foreach ($case in $sourceCases) {
    $caseSource = $case.Source
    Require (-not $caseSource.Contains('nsExec::ExecToStack') -and -not $caseSource.Contains('/TIMEOUT=') -and
        -not $caseSource.Contains('product_transport_failed')) 'Generated source retained an unbounded or parser-dependent output transport.'
    Require ([regex]::Matches($caseSource, '(?m)^  ExecWait ').Count -eq $case.Waits -and
        [regex]::Matches($caseSource, $waitPattern).Count -eq $case.Waits) 'A backend launch lost its explicit native wait and launch-error mapping.'
    $streams = @([regex]::Matches($caseSource, $streamPattern))
    $expectedStreams = if ($case.Name -in @('data-guidance', 'icon-all')) { 5 } else { 1 }
    Require ($streams.Count -eq $expectedStreams -and $streams[0].Value.Contains('"install-package"')) `
        'An installer feature adapter lost its independent bounded-progress transport or launch-error mapping.'
}
foreach ($text in @('Section /o "Install or repair the Cortex connection in Codex"',
    'Section /o "Install or repair Cortex guidance in Codex"',
    'Section /o "Remove the Cortex connection from Codex"',
    'Section /o "Remove Cortex guidance from Codex"',
    '/CODEXREGISTER=', '/CODEXGUIDANCE=', '/CODEXHOME=', '/CODEXAGENTS=',
    '"configure-codex-feature"', '"--feature" "registration" "--action" "install"',
    '"--feature" "guidance" "--action" "install"',
    '"--feature" "registration" "--action" "remove"',
    '"--feature" "guidance" "--action" "remove"',
    'Function ValidateFeatureSelections', 'SetErrorLevel 64')) {
    Require ($guidanceSource.Contains($text)) "Optional Codex source lost $text"
}
Require (-not $guidanceSource.Contains('InitializeInstallerState') -and
    -not $guidanceSource.Contains('"installer-state"') -and
    -not $guidanceSource.Contains('File /oname=PegasusCortex.Setup.exe')) `
    'The installer still extracts or runs a hidden setup/state probe before displaying feature choices.'
foreach ($text in @('/TRUST=', 'Function InitializeTrustOption',
    'SectionSetFlags ${SEC_TRUST} 0', 'StrCmp $R1 "1" 0 trust_option_invalid')) {
    Require ($guidanceSource.Contains($text)) "Explicit trust selection lost $text"
}
Require-Ordered $guidanceSource @('ReadRegStr $ExistingInstallRoot', 'existing_mode_selected:',
    'Call InitializeMaintenanceOptions', 'Call InitializeTrustOption', 'Call InitializeCodexOptions', 'StrCpy $WelcomeText') `
    'Upgrade mode and registered locations must be resolved before explicit feature overrides.'
foreach ($text in @('Function MaintenancePageCreate', 'Modify optional features',
    'Repair or update program files', 'Uninstall the program (keep all databases)',
    'Function ComponentsPagePre', 'StrCmp $MaintenanceAction "modify" components_page_show',
    'Function RunInstalledUninstaller', '"$INSTDIR\Uninstall.exe"',
    '/MAINTENANCE=', 'modify', 'repair', 'uninstall')) {
    Require ($guidanceSource.Contains($text)) "Maintenance-mode source lost $text"
}
foreach ($text in @('Function SetupTypePageCreate', 'Choose setup type', 'Recommended', 'Custom',
    'StrCmp $SetupType "custom" components_page_show', 'Function ReadyPageCreate', 'Ready to proceed',
    'Selected work:', 'SendMessage $R0 ${WM_SETTEXT} 0 "STR:$R1"')) {
    Require ($guidanceSource.Contains($text)) "Traditional setup workflow lost $text"
}
foreach ($invalid in @($null, $true, $false, 'true', 'false', 0, 1, @($true), [pscustomobject]@{ enabled = $true })) {
    $bad = Copy-Definition $guidanceDefinition
    $bad | Add-Member -NotePropertyName installerState -NotePropertyValue $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
Require (-not $dataSource.Contains('--codex-register') -and -not $dataSource.Contains('SEC_CODEX_')) `
    'An unrelated installer received Cortex/Codex-specific options.'
foreach ($section in @('SEC_CODEX_REGISTER', 'SEC_CODEX_GUIDANCE', 'SEC_CODEX_REGISTER_REMOVE', 'SEC_CODEX_GUIDANCE_REMOVE')) {
    Require ($guidanceSource.Contains('!insertmacro MUI_DESCRIPTION_TEXT ${' + $section + '}') -and
        -not $dataSource.Contains('!insertmacro MUI_DESCRIPTION_TEXT ${' + $section + '}')) `
        'Optional Codex descriptions do not match the declared components.'
}
foreach ($invalid in @($null, $false, 'data', @('data'))) {
    $bad = Copy-Definition $dataDefinition
    $bad.dataLocation = $invalid
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
foreach ($invalid in @('', '../escape', '/absolute', 'C:/data', 'data:stream', 'data.', 'data ',
    'a//b', 'CON', 'NUL', 'LPT9', 'data/COM1', 'a\b', 'a"b', 'a$b', ('a' * 121))) {
    foreach ($field in @('relativePath', 'legacyRelativePath')) {
        $bad = Copy-Definition $dataDefinition
        $bad.dataLocation.$field = $invalid
        MustFail { Assert-PegasusInstallerDefinition $bad }
    }
}
foreach ($field in @('schema', 'defaultBase', 'relativePath', 'legacyRelativePath')) {
    $bad = Copy-Definition $dataDefinition
    $bad.dataLocation.PSObject.Properties.Remove($field)
    MustFail { Assert-PegasusInstallerDefinition $bad }
    $bad = Copy-Definition $dataDefinition
    $bad.dataLocation.$field = 123
    MustFail { Assert-PegasusInstallerDefinition $bad }
}
$bad = Copy-Definition $dataDefinition
$bad.dataLocation.schema = 'pegasus.windows-installer.data-location/2'
MustFail { Assert-PegasusInstallerDefinition $bad }
$bad = Copy-Definition $dataDefinition
$bad.dataLocation.defaultBase = 'userProfile'
MustFail { Assert-PegasusInstallerDefinition $bad }
$bad = Copy-Definition $dataDefinition
$bad.dataLocation | Add-Member -NotePropertyName command -NotePropertyValue 'arbitrary'
MustFail { Assert-PegasusInstallerDefinition $bad }

$builder = Get-Content -LiteralPath (Join-Path $root 'tools\New-PegasusCortexNsisInstaller.ps1') -Raw
$manifestBuilder = Get-Content -LiteralPath (Join-Path $root 'deploy\x64\New-PegasusCortexV2PackageManifest.ps1') -Raw
# These source assertions protect admission, the keep-stopped policy and the
# deferred registration callback; they are not live lifecycle evidence.
$setupProgram = Get-Content -LiteralPath (Join-Path $root 'src\PegasusCortex.Setup\Program.cs') -Raw
$installEntry = $setupProgram.IndexOf('if (args.Length >= 2 && string.Equals(args[0], "install-package", StringComparison.Ordinal))', [StringComparison]::Ordinal)
Require ($installEntry -ge 0) 'The compiled install-package entry point is absent.'
$installFlow = $setupProgram.Substring($installEntry)
Require-Ordered $installFlow @('CortexSetupCommandLine.ParseInstallPackage(args)',
    'CortexSignedProductPackageVerifier.Verify(Path.GetFullPath(admittedPackageRoot))',
    'CortexProductInstallPlanner.Create(package, layout)',
    'CortexWindowsProductRegistration.RequireExistingProductLocations(layout)',
    'CortexProductStateProvisioner.ValidateLegacySemanticParent(layout, requireWritable: relocation is null)',
    'CortexProductInstallLifecycle.Prepare(layout)',
    'CortexProductInstallLifecycle.HoldUserHostAdmission(layout)',
    'void RegisterInstalled(CortexProductCatalogV1 catalog)',
    'registration = CortexWindowsProductRegistration.Apply(layout, catalog)',
    'relocation.CommitSelection(target =>',
    'registration = CortexWindowsProductRegistration.ApplyRelocated(target, catalog, relocation)',
    'CortexProductInstaller.InstallVerifiedFilesAndRegisterAsync(package, layout, null,',
    'RegisterInstalled, CancellationToken.None, ReportProvisioned)',
    'CortexProductInstaller.InstallRelocatedFilesAndRegisterAsync(package, relocation,',
    'RegisterInstalled, CancellationToken.None, ReportProvisioned)') `
    'Compiled setup must admit package and destinations, require stopped components and hold host admission, then pass the registration callback to both installation paths.'
Require (-not $installFlow.Contains('CortexCodexIntegration.Prepare(options, layout') -and
    $setupProgram.Contains('"configure-codex-feature"') -and
    $setupProgram.Contains('CortexCodexFeatureOperation.Execute(request)')) `
    'Core installation still owns optional Codex work or the independent feature entry point is absent.'
$lifecycle = Get-Content -LiteralPath (Join-Path $root 'src\PegasusCortex.Setup\CortexProductInstallLifecycle.cs') -Raw
Require-Ordered $lifecycle @('public static CortexProductInstallLifecycleReceiptV1 Prepare(',
    'layout.RequireRuntimePathsWithinInstallRoot()',
    'CortexWindowsProductRegistration.RequireOwnedServiceStopped(layout)',
    'RequireNoProductProcesses(layout.InstallRoot, Environment.ProcessId)',
    'RuntimeKeptStopped: true') `
    'Owned lifecycle must validate the layout and refuse running service/product images while keeping the runtime stopped.'
$installer = Get-Content -LiteralPath (Join-Path $root 'src\PegasusCortex.Setup\CortexProductInstaller.cs') -Raw
Require-Ordered $installer @('ReplaceFromVerifiedSource(source, destination, file.Bytes, file.Sha256)',
    'CortexProductCatalogActivator.Activate(', 'afterActivation?.Invoke(activeCatalog)') `
    'Registration callback must remain after verified replacement and catalog activation.'
$registration = Get-Content -LiteralPath (Join-Path $root 'src\PegasusCortex.Setup\CortexWindowsProductRegistration.cs') -Raw
$registrationStart = $registration.IndexOf('private static CortexWindowsRegistrationReceiptV1 ApplyCore(', [StringComparison]::Ordinal)
$registrationEnd = $registration.IndexOf('internal static bool StopOwnedServiceForUpdate(', $registrationStart + 1, [StringComparison]::Ordinal)
Require ($registrationStart -ge 0 -and $registrationEnd -gt $registrationStart) 'The core product registration boundary is absent.'
$coreRegistration = $registration.Substring($registrationStart, $registrationEnd - $registrationStart)
Require-Ordered $coreRegistration @('RequireExistingProductLocations(layout, relocation)',
    'WriteAtomic(Path.Combine(layout.InstallStateDirectory, "registration.v1.json")',
    'product.SetValue("DataLocation", layout.DataRoot, RegistryValueKind.String)', 'return receipt;') `
    'Core registration must prepare its receipt before committing a changed data location.'
Require (-not $coreRegistration.Contains('ServiceRegistration.Apply(') -and
    -not $coreRegistration.Contains('ServiceRegistration.Start(') -and
    -not $coreRegistration.Contains('ServiceRegistration.Stop')) `
    'Core registration must preserve the optional service without installing, enabling, starting or stopping it.'
foreach ($script in @($builder, $manifestBuilder)) {
    Require ($script.Contains('installer-contract.v7.json') -and -not $script.Contains('installer-contract.v6.json')) 'Installer builder and admission disagree on the v7 contract filename.'
    foreach ($text in @('same-location setup preserves databases; exact legacy install-local state is copied to the separate data location with originals retained and registry selection last; no database conversion or seeding; fresh installation creates empty folders only',
        'disabled-during-normal-install', 'data-location binding and every database retained',
        'per-user isolation enforced by Cortex; program installation is machine-wide')) {
        Require ($script.Contains($text)) "Installer contract source lost $text"
    }
}
$definition.name = 'Example$bad'
MustFail { New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $ProductPackage }
$definition.name = 'Pegasus Example'
$definition.setupPath = '../escape.exe'
MustFail { Assert-PegasusInstallerDefinition $definition }
$definition.setupPath = 'installer/PegasusCortex.Setup.exe'
$definition | Add-Member -NotePropertyName command -NotePropertyValue 'arbitrary'
MustFail { Assert-PegasusInstallerDefinition $definition }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pegasus-installer-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -ErrorAction Stop | Out-Null
try {
    $compiler = Join-Path $ProductPackage 'tools\nsis\3.12\Bin\makensis.exe'
    foreach ($case in $sourceCases) {
        $nsi = Join-Path $scratch ($case.Name + '.nsi')
        $executable = Join-Path $scratch ($case.Name + '.exe')
        [IO.File]::WriteAllText($nsi, $case.Source, [Text.UTF8Encoding]::new($false))
        $preprocess = Invoke-CortexBuildProcess -Executable $compiler `
            -Arguments @('/SAFEPPO', '/V4', '/WX', '/NOCONFIG', '/OUTPUTCHARSET', 'UTF8',
                ('/DPAO_OUT=' + $executable), $nsi) -WorkingDirectory $scratch -TimeoutSeconds 60
        if ($preprocess.ExitCode -ne 0) {
            throw ('Shared NSIS source failed preprocessing: ' +
                (Get-CortexBuildProcessOutputTail -Text ($preprocess.StandardOutput + $preprocess.StandardError)))
        }
        Require (-not (Test-Path -LiteralPath $executable)) 'Preprocessing unexpectedly compiled an executable.'
    }
    if ($ExecWaitFixture) {
        # Tiny user-mode executable exercises the actual generated streaming
        # block: bounded output, exact child failure, missing executable, then
        # finalization. It contains no product, registry or service operation.
        # It contains no product payload, registry/service action, or signing.
        $pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
        Require (Test-Path -LiteralPath $pwsh -PathType Leaf) 'PowerShell 7 fixture prerequisite is absent.'
        $child = Join-Path $scratch 'output.ps1'
        [IO.File]::WriteAllText($child, '[Console]::WriteLine((''x'' * 384)); exit [int]$args[0]', [Text.UTF8Encoding]::new($false))
        $waitBlock = @([regex]::Matches($source, $streamPattern) | Where-Object { $_.Value.Contains('"install-package"') })[0].Value
        Require ($waitBlock.Contains('"install-package"')) 'The generated fixture did not select the real install streaming block.'
        $nativeCases = @(
            @{ Command = ('''"{0}" -NoLogo -NoProfile -NonInteractive -File "{1}" 0''' -f $pwsh, $child); Expected = 0 },
            @{ Command = ('''"{0}" -NoLogo -NoProfile -NonInteractive -File "{1}" 4''' -f $pwsh, $child); Expected = 4 },
            @{ Command = ('''"{0}"''' -f (Join-Path $scratch 'absent.exe')); Expected = 8 }
        )
        $fixtureBody = ''
        for ($index = 0; $index -lt $nativeCases.Count; $index++) {
            $case = $nativeCases[$index]
            $launchLine = [regex]::Match($waitBlock, '(?m)^  nsExec::ExecToLog [^\r\n]+').Value
            $block = $waitBlock.Replace($launchLine, ('  nsExec::ExecToLog ' + $case.Command))
            $fixtureBody += $block + "`n  IntCmp `$0 $($case.Expected) fixture_$index`n  SetErrorLevel 1`n  Quit`nfixture_${index}:`n"
        }
        $nativeExe = Join-Path $scratch 'native-wait.exe'
        $nativeNsi = Join-Path $scratch 'native-wait.nsi'
        $finalized = Join-Path $scratch 'finalized.txt'
        $nativeSource = @"
Unicode true
RequestExecutionLevel user
OutFile "$nativeExe"
Section
$fixtureBody
  FileOpen `$0 "$finalized" w
  FileWrite `$0 "finalized"
  FileClose `$0
  SetErrorLevel 0
SectionEnd
"@
        [IO.File]::WriteAllText($nativeNsi, $nativeSource, [Text.UTF8Encoding]::new($false))
        $compile = Invoke-CortexBuildProcess -Executable $compiler `
            -Arguments @('/V2', '/WX', '/NOCONFIG', $nativeNsi) -WorkingDirectory $scratch -TimeoutSeconds 60
        Require ($compile.ExitCode -eq 0) ('Native wait fixture compilation failed: ' +
            (Get-CortexBuildProcessOutputTail -Text ($compile.StandardOutput + $compile.StandardError)))
        $process = [Diagnostics.Process]::new()
        try {
            $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($nativeExe)
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.ArgumentList.Add('/S')
            Require ($process.Start()) 'Native wait fixture did not start.'
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw 'Native wait fixture exceeded its 30-second test deadline.'
            }
            Require ($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $finalized -PathType Leaf) -and
                [IO.File]::ReadAllText($finalized) -ceq 'finalized') 'Native wait fixture lost an exit code or failed to finalize after long output.'
        } finally { $process.Dispose() }
        Write-Output 'PASS real user-mode NSIS fixture: visible bounded output, backend codes 0/4, launch failure 8, and post-backend finalization; no product or machine mutation'
    }
    if ($SourceOnly) {
        Write-Output 'PASS source-only: reusable template defaults, traditional MUI2 welcome/notices/setup-type/components/locations/ready/progress/finish workflow, maintenance choices, embedded and adjacent payload modes, admin/DPI manifest, no hidden setup probe, section-isolated optional features, visible bounded install progress, post-registration cleanup, no elevated user-session launch, native exit boundaries, invalid metadata, v7 source contract, and seven real NSIS preprocessing cases; no product build or signing performed'
        return
    }
    $output = Join-Path $scratch 'output'
    Reset-CortexBuildDirectory -Path $output -Boundary $scratch
    $file = Join-Path $output 'generated.txt'
    [IO.File]::WriteAllText($file, 'first build')
    $locked = [IO.File]::Open($file, 'Open', 'Read', 'None')
    try { MustFail { Reset-CortexBuildDirectory -Path $output -Boundary $scratch } }
    finally { $locked.Dispose() }
    Require (Test-Path -LiteralPath $file) 'Locked output was removed.'
    Reset-CortexBuildDirectory -Path $output -Boundary $scratch
    Require (@(Get-ChildItem -LiteralPath $output).Count -eq 0) 'Output was not reused cleanly.'
    [IO.File]::WriteAllText($file, 'second build')
    Reset-CortexBuildDirectory -Path $output -Boundary $scratch
    MustFail { Reset-CortexBuildDirectory -Path $scratch -Boundary $scratch }
    MustFail { Reset-CortexBuildDirectory -Path (Join-Path $scratch '..\escape') -Boundary $scratch }
    # The shared lock fails promptly rather than letting two builders clear each other's output.
    $workspace = Enter-CortexBuildWorkspace -ProjectRoot $scratch
    try { MustFail { Enter-CortexBuildWorkspace -ProjectRoot $scratch } }
    finally { Exit-CortexBuildWorkspace -Workspace $workspace }

    # Sign a tiny generic fixture with the existing identity; no certificate or trust creation.
    $fixture = Join-Path $scratch 'generic-package'
    New-Item -ItemType Directory -Path $fixture -ErrorAction Stop | Out-Null
    Copy-Item -LiteralPath (Join-Path $ProductPackage 'trust') -Destination (Join-Path $fixture 'trust') -Recurse -ErrorAction Stop
    $sample = Join-Path $ProductPackage 'tools\nsis\3.12\Plugins\x86-unicode\nsExec.dll'
    Copy-Item -LiteralPath $sample -Destination (Join-Path $fixture 'sample.dll') -ErrorAction Stop
    $trust = Get-Content -LiteralPath (Join-Path $fixture 'trust\trust-bundle.json') -Raw | ConvertFrom-Json -Depth 10
    $generic = & (Join-Path $root 'tools\windows-installer\New-PegasusSignedManifest.ps1') `
        -PackageRoot $fixture -ProductId 'pegasus.example' -ReleaseVersion '1.0.0' `
        -RootThumbprint 'C5A084A372E3FD021A91C3B8A17F4C0F14C0A8CB' -PublisherThumbprint '6AE355621D021F7189D9511488DB4A8CB6240F12'
    Require $generic.Valid 'The generic fixture did not pass real detached signature/content verification.'
    MustFail {
        & (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') -PackageRoot $fixture `
            -Role product -ReleaseVersion '1.0.0' -ProductId 'pegasus.wrong'
    }
    [IO.File]::AppendAllText((Join-Path $fixture 'sample.dll'), 'tamper')
    MustFail {
        & (Join-Path $root 'tools\signing\Test-PegasusSignedPackage.ps1') -PackageRoot $fixture `
            -Role product -ReleaseVersion '1.0.0' -ProductId 'pegasus.example'
    }
} finally {
    Reset-CortexBuildDirectory -Path $scratch -Boundary ([IO.Path]::GetTempPath())
    [IO.Directory]::Delete($scratch, $false)
}
'PASS generic product definition, optional data location, v7 section-isolated contract source, real NSIS preprocessing, injection/path rejection, same-folder replacement, locks, real generic CMS signature, wrong-product and tamper rejection'
