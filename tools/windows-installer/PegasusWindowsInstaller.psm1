Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PegasusInstallerLiteral {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.IndexOfAny([char[]]@([char]34, [char]39, [char]96, [char]36, [char]13, [char]10)) -ge 0 -or
        $Value.Length -gt 240) { throw 'An installer value contains unsafe NSIS syntax or exceeds the path budget.' }
    return $Value
}

function Read-PegasusInstallerDefinition {
    param([Parameter(Mandatory)][string]$Path)
    if ((Get-Item -LiteralPath $Path -ErrorAction Stop).Length -gt 16384) { throw 'Installer definition exceeds 16 KiB.' }
    $value = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 8 -ErrorAction Stop
    Assert-PegasusInstallerDefinition -Definition $value
    return $value
}

function Assert-PegasusInstallerDefinition {
    param([Parameter(Mandatory)]$Definition)
    $fields = @('schema', 'productId', 'name', 'publisher', 'productFolder', 'releaseVersion',
        'fileVersion', 'setupPath', 'launchPath', 'iconPath', 'trustPath', 'codexGuidance')
    foreach ($property in $Definition.PSObject.Properties.Name) {
        if ($property -cnotin ($fields + @('dataLocation', 'wizardIconPath', 'noticePath', 'userSessionStartup', 'userSessionShortcut', 'keepRuntimeStopped', 'removeObsoletePrograms', 'finishReference', 'payloadLayout'))) { throw "Unknown installer definition field: $property" }
    }
    foreach ($field in $fields) {
        if ($null -eq $Definition.PSObject.Properties[$field]) { throw "Missing installer definition field: $field" }
    }
    if ($Definition.schema -cne 'pegasus.windows-installer/1' -or
        $Definition.productId -cnotmatch '^[a-z][a-z0-9.-]{2,63}$' -or
        $Definition.releaseVersion -cnotmatch '^[0-9][A-Za-z0-9.-]{0,63}$' -or
        $Definition.fileVersion -cnotmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
        $Definition.codexGuidance -isnot [bool]) { throw 'Installer definition identity is invalid.' }
    if (@($Definition.fileVersion.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) {
        throw 'Windows file-version components must fit UInt16.'
    }
    foreach ($field in @('name', 'publisher', 'productFolder')) {
        if ($Definition.$field -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$') { throw "Unsafe installer name: $field" }
    }
    if ($null -ne $Definition.PSObject.Properties['userSessionStartup'] -and
        $Definition.userSessionStartup -isnot [bool]) { throw 'userSessionStartup must be a boolean.' }
    if ($null -ne $Definition.PSObject.Properties['keepRuntimeStopped'] -and
        $Definition.keepRuntimeStopped -isnot [bool]) { throw 'keepRuntimeStopped must be a boolean.' }
    if ($null -ne $Definition.PSObject.Properties['userSessionShortcut'] -and
        $Definition.userSessionShortcut -cnotin @('standard', 'single')) {
        throw 'userSessionShortcut must be standard or single.'
    }
    if ($null -ne $Definition.PSObject.Properties['removeObsoletePrograms'] -and
        $Definition.removeObsoletePrograms -isnot [bool]) { throw 'removeObsoletePrograms must be a boolean.' }
    if ($null -ne $Definition.PSObject.Properties['payloadLayout'] -and
        ($Definition.payloadLayout -isnot [string] -or
         ($Definition.payloadLayout -cne 'embedded' -and $Definition.payloadLayout -cne 'adjacent'))) {
        throw 'payloadLayout must be exactly embedded or adjacent.'
    }
    if ($null -ne $Definition.PSObject.Properties['finishReference']) {
        $reference = $Definition.finishReference
        $referenceFields = @('schema', 'relativePath', 'label')
        if ($null -eq $reference -or $reference -isnot [pscustomobject]) {
            throw 'finishReference must be a versioned object.'
        }
        foreach ($property in $reference.PSObject.Properties.Name) {
            if ($property -cnotin $referenceFields) { throw "Unknown finish-reference definition field: $property" }
        }
        foreach ($field in $referenceFields) {
            if ($null -eq $reference.PSObject.Properties[$field] -or $reference.$field -isnot [string]) {
                throw "Missing or invalid finish-reference definition field: $field"
            }
        }
        if ($reference.schema -cne 'pegasus.windows-installer.finish-reference/1' -or
            $reference.relativePath -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,159}$' -or
            @($reference.relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') }).Count -or
            $reference.relativePath.StartsWith('state/', [StringComparison]::OrdinalIgnoreCase) -or
            ($reference.relativePath -notmatch '\.(?:md|txt)$') -or
            $reference.label -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 ._()/-]{0,79}$') {
            throw 'Finish-reference identity, path, or label is invalid.'
        }
    }
    $pathFields = @('setupPath', 'launchPath', 'iconPath', 'trustPath')
    if ($null -ne $Definition.PSObject.Properties['wizardIconPath']) {
        if ($Definition.wizardIconPath -isnot [string] -or
            -not $Definition.wizardIconPath.EndsWith('.ico', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'wizardIconPath must identify a package-relative Windows icon.'
        }
        $pathFields += 'wizardIconPath'
    }
    if ($null -ne $Definition.PSObject.Properties['noticePath']) {
        if ($Definition.noticePath -isnot [string] -or
            -not $Definition.noticePath.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'noticePath must identify a package-relative plain-text notice.'
        }
        $pathFields += 'noticePath'
    }
    foreach ($field in $pathFields) {
        $relative = [string]$Definition.$field
        if ($relative -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,159}$' -or
            @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') }).Count -or
            $relative.StartsWith('state/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe installer payload path: $field"
        }
    }
    foreach ($field in @('setupPath', 'launchPath', 'iconPath')) {
        if (-not $Definition.$field.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { throw "$field must identify an executable." }
    }
    if (-not $Definition.trustPath.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) { throw 'trustPath must identify a PowerShell script.' }
    if ($null -ne $Definition.PSObject.Properties['dataLocation']) {
        $data = $Definition.dataLocation
        $dataFields = @('schema', 'defaultBase', 'relativePath', 'legacyRelativePath')
        if ($null -eq $data -or $data -isnot [pscustomobject]) { throw 'dataLocation must be a versioned object.' }
        foreach ($property in $data.PSObject.Properties.Name) {
            if ($property -cnotin ($dataFields + @('relocateLegacyData'))) { throw "Unknown data-location definition field: $property" }
        }
        foreach ($field in $dataFields) {
            if ($null -eq $data.PSObject.Properties[$field] -or $data.$field -isnot [string]) {
                throw "Missing or invalid data-location definition field: $field"
            }
        }
        if ($data.schema -cne 'pegasus.windows-installer.data-location/1' -or
            $data.defaultBase -cne 'commonApplicationData') { throw 'Data-location definition identity is invalid.' }
        if ($null -ne $data.PSObject.Properties['relocateLegacyData'] -and $data.relocateLegacyData -isnot [bool]) {
            throw 'relocateLegacyData must be a boolean.'
        }
        foreach ($field in @('relativePath', 'legacyRelativePath')) {
            $relative = $data.$field
            if ($relative.Length -gt 120 -or
                $relative -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9 _/-]*[A-Za-z0-9_-])?$' -or
                @($relative.Split('/') | Where-Object {
                    $_ -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$' -or $_.EndsWith(' ') -or
                    $_ -imatch '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
                }).Count) { throw "Unsafe data-location relative path: $field" }
        }
    }
}

function New-PegasusWindowsInstallerSource {
    param(
        [Parameter(Mandatory)]$Definition,
        [Parameter(Mandatory)][string]$ProductDirectory,
        [string]$OutputExecutable = '',
        [string]$PublisherThumbprint = '',
        [string]$Provider = 'Microsoft Software Key Storage Provider'
    )
    Assert-PegasusInstallerDefinition -Definition $Definition
    $displayName = $Definition.name
    $publisher = $Definition.publisher
    $productFolder = $Definition.productFolder
    $productId = $Definition.productId
    $registryKey = 'Software\' + $publisher + '\' + $productFolder
    $releaseVersion = $Definition.releaseVersion
    $fileVersion = $Definition.fileVersion
    $copyrightYear = (Get-Date).Year
    $installDate = Get-Date -Format 'yyyyMMdd'
    $productLiteral = Assert-PegasusInstallerLiteral -Value ([IO.Path]::GetFullPath($ProductDirectory))
    if (-not (Test-Path -LiteralPath $productLiteral -PathType Container)) { throw 'Product directory is absent.' }
    $installerLiteral = if ($OutputExecutable) {
        Assert-PegasusInstallerLiteral -Value ([IO.Path]::GetFullPath($OutputExecutable))
    } else { '$' + '{PAO_OUT}' }
    $pathFields = @('setupPath', 'launchPath', 'iconPath', 'trustPath')
    if ($null -ne $Definition.PSObject.Properties['wizardIconPath']) { $pathFields += 'wizardIconPath' }
    if ($null -ne $Definition.PSObject.Properties['noticePath']) { $pathFields += 'noticePath' }
    foreach ($field in $pathFields) {
        $payloadPath = Join-Path $productLiteral $Definition.$field.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) { throw "Required setup-adapter payload is missing: $field" }
        $current = $payloadPath
        while ($current) {
            if ((Get-Item -LiteralPath $current -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Installer payload crosses a reparse point.'
            }
            $current = Split-Path -Parent $current
        }
    }
    if ($null -ne $Definition.PSObject.Properties['finishReference']) {
        $payloadPath = Join-Path $productLiteral $Definition.finishReference.relativePath.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw 'Required finish-reference payload is missing.'
        }
        $current = $payloadPath
        while ($current) {
            if ((Get-Item -LiteralPath $current -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Installer finish-reference payload crosses a reparse point.'
            }
            $current = Split-Path -Parent $current
        }
    }
    $setupRelative = $Definition.setupPath.Replace('/', '\')
    $trustRelative = $Definition.trustPath.Replace('/', '\')
    $launchRelative = $Definition.launchPath.Replace('/', '\')
    $iconRelative = $Definition.iconPath.Replace('/', '\')
    $maintenanceSetupRelative = 'installer\Setup.exe'
    $setupDirectory = Split-Path -Parent $setupRelative
    $trustDirectory = Split-Path -Parent $trustRelative
    $payloadLayout = if ($null -ne $Definition.PSObject.Properties['payloadLayout']) {
        [string]$Definition.payloadLayout
    } else { 'embedded' }
    $adjacentPayload = $payloadLayout -ceq 'adjacent'
    $payloadRootReference = if ($adjacentPayload) { '$PayloadRoot' } else { '$PLUGINSDIR\product' }
    $payloadDeclarations = if ($adjacentPayload) { "Var PayloadRoot`nVar PayloadSourceRoot" } else { '' }
    $payloadInitialization = if ($adjacentPayload) { @'
  StrCpy $PayloadSourceRoot "$EXEDIR"
  StrCpy $PayloadRoot "$PayloadSourceRoot\product"
  IfFileExists "$PayloadRoot\package-manifest.json" payload_location_ready
  StrCmp $ExistingInstallSource "" payload_location_installed
  StrCpy $PayloadSourceRoot "$ExistingInstallSource"
  StrCpy $PayloadRoot "$PayloadSourceRoot\product"
  IfFileExists "$PayloadRoot\package-manifest.json" payload_location_ready
payload_location_installed:
  StrCmp $ExistingInstallRoot "" payload_location_ready
  StrCpy $PayloadSourceRoot "$ExistingInstallRoot"
  StrCpy $PayloadRoot "$ExistingInstallRoot"
payload_location_ready:
'@ } else { '' }
    $productPayloadPreparation = if ($adjacentPayload) { @'
  DetailPrint "Using the adjacent uncompressed signed product package."
  IfFileExists "$PayloadRoot\package-manifest.json" product_payload_ready
  MessageBox MB_ICONSTOP "The signed companion product folder is missing. Keep PegasusCortexInstaller.exe beside its product folder, then retry. No system change was made." /SD IDOK
  SetErrorLevel 3
  Abort
product_payload_ready:
'@ } else { @"
  SetOutPath "`$PLUGINSDIR\product"
  File /r "$productLiteral\*"
"@ }
    $trustPayloadPreparation = if ($adjacentPayload) { @'
  IfFileExists "$PayloadRoot\installer\Invoke-PegasusProdAlphaTrust.ps1" trust_payload_ready
  MessageBox MB_ICONSTOP "The signed companion trust files are missing. Keep the complete Cortex setup folder together, then retry. Product setup was not started." /SD IDOK
  SetErrorLevel 3
  Abort
trust_payload_ready:
'@ } else { @"
  SetOutPath "`$PLUGINSDIR\trustproduct\$trustDirectory"
  File "$productLiteral\$trustRelative"
  File "$productLiteral\$trustDirectory\PegasusSigning.psm1"
  File "$productLiteral\$trustDirectory\PegasusTrust.psm1"
  SetOutPath "`$PLUGINSDIR\trustproduct\trust"
  File "$productLiteral\trust\pegasus-prod-alpha-publisher.cer"
  File "$productLiteral\trust\pegasus-prod-alpha-root.cer"
  File "$productLiteral\trust\trust-bundle.json"
"@ }
    $trustPackageRoot = if ($adjacentPayload) { '$PayloadRoot' } else { '$PLUGINSDIR\trustproduct' }
    $installSourceReference = if ($adjacentPayload) { '$PayloadSourceRoot' } else { '$EXEDIR' }
    $upgradePayloadDescription = if ($adjacentPayload) {
        'verifying the adjacent signed product package'
    } else { 'extracting the full product payload' }
    $wizardIcons = ''
    if ($pathFields -contains 'wizardIconPath') {
        $wizardIcon = Assert-PegasusInstallerLiteral -Value (Join-Path $productLiteral $Definition.wizardIconPath.Replace('/', '\'))
        $wizardIcons = "!define MUI_ICON `"$wizardIcon`"`n!define MUI_UNICON `"$wizardIcon`""
    }
    $noticePage = ''
    if ($pathFields -contains 'noticePath') {
        $noticePath = Assert-PegasusInstallerLiteral -Value (Join-Path $productLiteral $Definition.noticePath.Replace('/', '\'))
        $noticePage = @"
!define MUI_LICENSEPAGE_TEXT_TOP "Review the product notice and the location of the complete third-party license materials."
!insertmacro MUI_PAGE_LICENSE "$noticePath"
"@
    }
    $estimatedKilobytes = [Math]::Ceiling((Get-ChildItem -LiteralPath $productLiteral -File -Recurse |
        Measure-Object -Property Length -Sum).Sum / 1KB)
    $signingDirective = ''
    if ($PublisherThumbprint) {
        if ($PublisherThumbprint -cnotmatch '^[0-9A-Fa-f]{40}$') { throw 'An exact publisher thumbprint is required.' }
        $pwshLiteral = Assert-PegasusInstallerLiteral -Value (Get-Command pwsh.exe -ErrorAction Stop).Source
        $signerLiteral = Assert-PegasusInstallerLiteral -Value ([IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot '..\signing\Sign-PegasusSingleFile.ps1')))
        if (-not (Test-Path -LiteralPath $signerLiteral -PathType Leaf)) { throw 'Shared uninstaller signer is absent.' }
        $providerLiteral = Assert-PegasusInstallerLiteral -Value $Provider
        $signingDirective = '!uninstfinalize ''"' + $pwshLiteral +
            '" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $signerLiteral +
            '" -LiteralPath "%1" -PublisherThumbprint "' + $PublisherThumbprint +
            '" -Provider "' + $providerLiteral + '"'' = 0'
    }

    $dataArguments = ''
    $dataDeclarations = ''
    $dataInitialization = ''
    $dataPages = ''
    $dataDetails = ''
    $stateRemovalSection = "un.Permanently remove all $displayName state"
    $stateRemovalWarning = "Permanently remove all $displayName databases, queues, settings, logs, backups, and receipts under this install root? Legacy source databases remain untouched."
    if ($null -ne $Definition.PSObject.Properties['dataLocation']) {
        $dataRelative = $Definition.dataLocation.relativePath.Replace('/', '\')
        $legacyDataRelative = $Definition.dataLocation.legacyRelativePath.Replace('/', '\')
        $dataArguments = ' "--data-root" "$DataFolder"'
        $stateRemovalSection = "un.Permanently remove install-local $displayName state"
        $stateRemovalWarning = "Permanently remove $displayName install-local state, including any databases, queues, settings, logs, backups, and receipts under this install root? A separately configured database location and its location binding are retained. Legacy source databases remain untouched."
        $dataDeclarations = @'
Var DataFolder
Var DataLocationLocked
Var DataLocationRelocating
'@
        $dataInitialization = @"
  SetShellVarContext all
  StrCpy `$DataFolder "`$APPDATA\$dataRelative"
  StrCpy `$DataLocationLocked "0"
  StrCpy `$DataLocationRelocating "0"
  ReadRegStr `$R2 HKLM "$registryKey" "DataLocation"
  StrCmp `$R2 "" data_location_without_binding
  StrCpy `$DataFolder `$R2
  StrCpy `$DataLocationLocked "1"
  Goto data_location_ready
data_location_without_binding:
  StrCmp `$ExistingInstallRoot "" data_location_ready
  StrCpy `$DataFolder "`$ExistingInstallRoot\$legacyDataRelative"
  StrCpy `$DataLocationLocked "1"
data_location_ready:
"@
        $relocateLegacyData = $null -ne $Definition.dataLocation.PSObject.Properties['relocateLegacyData'] -and $Definition.dataLocation.relocateLegacyData
        if ($relocateLegacyData) {
            # Only the exact old install-local default moves. Existing custom
            # data roots remain selected, and setup's typed copy keeps originals.
            $dataInitialization = @"
  SetShellVarContext all
  StrCpy `$DataFolder "`$APPDATA\$dataRelative"
  StrCpy `$DataLocationLocked "0"
  StrCpy `$DataLocationRelocating "0"
  ReadRegStr `$R2 HKLM "$registryKey" "DataLocation"
  StrCmp `$R2 "" data_location_without_binding
  StrCpy `$DataLocationLocked "1"
  StrCmp `$ExistingInstallRoot "" data_location_keep_custom
  StrCmp `$R2 "`$ExistingInstallRoot\$legacyDataRelative" data_location_copy_legacy
data_location_keep_custom:
  StrCpy `$DataFolder `$R2
  Goto data_location_ready
data_location_without_binding:
  StrCmp `$ExistingInstallRoot "" data_location_ready
data_location_copy_legacy:
  StrCpy `$DataLocationLocked "1"
  StrCpy `$DataLocationRelocating "1"
data_location_ready:
"@
        }
        $dataPages = @"
Function DataDirectoryPagePre
  StrCmp `$DataLocationLocked "1" 0 data_directory_page_show
  Abort
data_directory_page_show:
FunctionEnd
Function RetainedDataPageCreate
  StrCmp `$DataLocationLocked "1" retained_data_page_show
  Abort
retained_data_page_show:
  nsDialogs::Create 1018
  Pop `$0
  StrCmp `$0 error 0 retained_data_page_created
  Abort
retained_data_page_created:
  !insertmacro MUI_HEADER_TEXT "Existing database location" "The registered data location stays unchanged."
  `${NSD_CreateLabel} 0 0 100% 28u "The existing database location will be reused. Setup changes program files only; it does not copy, replace, seed, or migrate databases."
  Pop `$0
  `${NSD_CreateLabel} 0 40u 100% 40u "`$DataFolder"
  Pop `$0
  `${NSD_CreateLabel} 0 90u 100% 30u "This location is locked during upgrade, repair, or reinstall to preserve the association with existing data."
  Pop `$0
  nsDialogs::Show
FunctionEnd
!define MUI_PAGE_CUSTOMFUNCTION_PRE DataDirectoryPagePre
!define MUI_PAGE_HEADER_TEXT "Database location"
!define MUI_PAGE_HEADER_SUBTEXT "Choose where durable data belongs, separately from program files."
!define MUI_DIRECTORYPAGE_TEXT_TOP "Choose a dedicated local database folder. Setup creates empty folders only; the product manages database access."
!define MUI_DIRECTORYPAGE_VARIABLE `$DataFolder
!insertmacro MUI_PAGE_DIRECTORY
Page custom RetainedDataPageCreate
"@
        $dataDetails = @'
  DetailPrint "Data root: $DataFolder"
  DetailPrint "Database preservation: no database copy, replacement, seeding, or migration."
'@
        if ($relocateLegacyData) {
            $dataPages = $dataPages.Replace('  !insertmacro MUI_HEADER_TEXT "Existing database location" "The registered data location stays unchanged."', @'
  StrCmp $DataLocationRelocating "1" retained_data_copy retained_data_current
retained_data_copy:
  !insertmacro MUI_HEADER_TEXT "Move data out of program files" "Copy existing data; retain the originals."
  ${NSD_CreateLabel} 0 0 100% 28u "Setup copies the existing databases, settings and connections to the data folder below, then updates the registered location. Original files remain in place."
  Pop $0
  Goto retained_data_path
retained_data_current:
  !insertmacro MUI_HEADER_TEXT "Existing database location" "The registered data location stays unchanged."
'@).Replace('  ${NSD_CreateLabel} 0 40u 100% 40u "$DataFolder"', 'retained_data_path:' + "`r`n" + '  ${NSD_CreateLabel} 0 40u 100% 40u "$DataFolder"')
            $dataDetails = @'
  DetailPrint "Data root: $DataFolder"
  StrCmp $DataLocationRelocating "1" 0 retained_data_details_done
  DetailPrint "Copying existing install-local data to the separate data folder. Originals retained; no database conversion."
retained_data_details_done:
'@
        }
    }

    $codexDeclarations = ''
    $codexInitialization = ''
    $codexSections = ''
    $codexFunctions = ''
    # Stream the setup adapter's bounded progress lines into the visible NSIS
    # details pane. The setup adapter deliberately emits short one-line stages
    # and a compact final receipt, avoiding the NSIS 3.12 Unicode nsExec defect
    # triggered by unbounded single-line JSON while eliminating a blank child
    # console during long verification and install phases.
    $installInvocation = @"
  nsExec::ExecToLog '"$payloadRootReference\$setupRelative" "install-package" "$payloadRootReference" "--install-root" "`$INSTDIR"$dataArguments'
  Pop `$0
  StrCmp `$0 "error" 0 +2
  StrCpy `$0 "8"
"@
    if ($Definition.codexGuidance) {
        $codexDeclarations = @'
Var CodexAgentsPath
Var CodexHomePath
Var CodexHomeArgument
Var CodexAgentsArgument
'@
        $codexInitialization = '  Call InitializeCodexOptions'
        $codexSections = @'
Section /o "Install or repair the Cortex connection in Codex" SEC_CODEX_REGISTER
  DetailPrint "Installing the explicitly selected Cortex connection for Codex."
  nsExec::ExecToLog '"$PLUGINSDIR\product\installer\PegasusCortex.Setup.exe" "configure-codex-feature" "$PLUGINSDIR\product" "--install-root" "$INSTDIR" "--feature" "registration" "--action" "install" $CodexHomeArgument'
  Pop $0
  StrCmp $0 "error" 0 +2
  StrCpy $0 "8"
  IntCmp $0 0 codex_register_done
  MessageBox MB_ICONSTOP "The selected Cortex connection for Codex failed with code $0. The core product and databases were preserved."
  SetErrorLevel $0
  Abort
codex_register_done:
SectionEnd
Section /o "Install or repair Cortex guidance in Codex" SEC_CODEX_GUIDANCE
  DetailPrint "Installing the explicitly selected Cortex guidance for Codex."
  nsExec::ExecToLog '"$PLUGINSDIR\product\installer\PegasusCortex.Setup.exe" "configure-codex-feature" "$PLUGINSDIR\product" "--install-root" "$INSTDIR" "--feature" "guidance" "--action" "install" $CodexHomeArgument $CodexAgentsArgument'
  Pop $0
  StrCmp $0 "error" 0 +2
  StrCpy $0 "8"
  IntCmp $0 0 codex_guidance_done
  MessageBox MB_ICONSTOP "The selected Cortex guidance for Codex failed with code $0. The core product and databases were preserved."
  SetErrorLevel $0
  Abort
codex_guidance_done:
SectionEnd
Section /o "Remove the Cortex connection from Codex" SEC_CODEX_REGISTER_REMOVE
  DetailPrint "Removing only the explicitly selected Cortex connection from Codex."
  nsExec::ExecToLog '"$PLUGINSDIR\product\installer\PegasusCortex.Setup.exe" "configure-codex-feature" "$PLUGINSDIR\product" "--install-root" "$INSTDIR" "--feature" "registration" "--action" "remove" $CodexHomeArgument'
  Pop $0
  StrCmp $0 "error" 0 +2
  StrCpy $0 "8"
  IntCmp $0 0 codex_register_remove_done
  MessageBox MB_ICONSTOP "The selected Cortex connection removal failed with code $0. The core product and databases were preserved."
  SetErrorLevel $0
  Abort
codex_register_remove_done:
SectionEnd
Section /o "Remove Cortex guidance from Codex" SEC_CODEX_GUIDANCE_REMOVE
  DetailPrint "Removing only the explicitly selected Cortex guidance from Codex."
  nsExec::ExecToLog '"$PLUGINSDIR\product\installer\PegasusCortex.Setup.exe" "configure-codex-feature" "$PLUGINSDIR\product" "--install-root" "$INSTDIR" "--feature" "guidance" "--action" "remove" $CodexHomeArgument $CodexAgentsArgument'
  Pop $0
  StrCmp $0 "error" 0 +2
  StrCpy $0 "8"
  IntCmp $0 0 codex_guidance_remove_done
  MessageBox MB_ICONSTOP "The selected Cortex guidance removal failed with code $0. The core product and databases were preserved."
  SetErrorLevel $0
  Abort
codex_guidance_remove_done:
SectionEnd
'@
        $codexSections = $codexSections.Replace('$PLUGINSDIR\product', $payloadRootReference)
        # Every optional feature is a separate explicit section. An unselected
        # section has no command, no package admission, and no target inspection.
        $codexFunctions = @'
Function InitializeCodexOptions
  StrCpy $CodexAgentsPath ""
  StrCpy $CodexHomePath ""
  StrCpy $CodexHomeArgument ""
  StrCpy $CodexAgentsArgument ""
  SectionSetFlags ${SEC_CODEX_REGISTER} 0
  SectionSetFlags ${SEC_CODEX_GUIDANCE} 0
  SectionSetFlags ${SEC_CODEX_REGISTER_REMOVE} 0
  SectionSetFlags ${SEC_CODEX_GUIDANCE_REMOVE} 0
  SectionSetText ${SEC_CODEX_REGISTER_REMOVE} ""
  SectionSetText ${SEC_CODEX_GUIDANCE_REMOVE} ""
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/CODEXREGISTER=" $R1
  IfErrors codex_register_done
  StrCmp $R1 "0" codex_register_remove
  StrCmp $R1 "1" 0 codex_option_invalid
  SectionSetFlags ${SEC_CODEX_REGISTER} ${SF_SELECTED}
  StrCpy $MaintenanceAction "modify"
  StrCpy $MaintenanceExplicit "1"
  Goto codex_register_done
codex_register_remove:
  StrCmp $ExistingInstallRoot "" codex_register_done
  SectionSetFlags ${SEC_CODEX_REGISTER_REMOVE} ${SF_SELECTED}
  StrCpy $MaintenanceAction "modify"
  StrCpy $MaintenanceExplicit "1"
codex_register_done:
  ClearErrors
  ${GetOptions} $R0 "/CODEXGUIDANCE=" $R1
  IfErrors codex_guidance_done
  StrCmp $R1 "0" codex_guidance_remove
  StrCmp $R1 "1" 0 codex_option_invalid
  SectionSetFlags ${SEC_CODEX_GUIDANCE} ${SF_SELECTED}
  StrCpy $MaintenanceAction "modify"
  StrCpy $MaintenanceExplicit "1"
  Goto codex_guidance_done
codex_guidance_remove:
  StrCmp $ExistingInstallRoot "" codex_guidance_done
  SectionSetFlags ${SEC_CODEX_GUIDANCE_REMOVE} ${SF_SELECTED}
  StrCpy $MaintenanceAction "modify"
  StrCpy $MaintenanceExplicit "1"
codex_guidance_done:
  ClearErrors
  ${GetOptions} $R0 "/CODEXHOME=" $R1
  IfErrors codex_home_done
  StrCmp $R1 "" codex_option_invalid
  StrCpy $CodexHomePath $R1
  StrCpy $CodexHomeArgument '"--codex-home" "$CodexHomePath"'
codex_home_done:
  ClearErrors
  ${GetOptions} $R0 "/CODEXAGENTS=" $R1
  IfErrors codex_agents_done
  StrCmp $R1 "" codex_option_invalid
  StrCpy $CodexAgentsPath $R1
  StrCpy $CodexAgentsArgument '"--codex-agents" "$CodexAgentsPath"'
codex_agents_done:
  Return
codex_option_invalid:
  MessageBox MB_ICONSTOP "Codex selections require 0 or 1; explicit Codex paths must not be blank. Setup has not started." /SD IDOK
  SetErrorLevel 64
  Abort
FunctionEnd
'@
        $installInvocation = @"
  nsExec::ExecToLog '"$payloadRootReference\$setupRelative" "install-package" "$payloadRootReference" "--install-root" "`$INSTDIR"$dataArguments'
  Pop `$0
  StrCmp `$0 "error" 0 +2
  StrCpy `$0 "8"
"@
    }

    # Setup must reach its UI without extracting or executing an inner setup
    # program. Optional features therefore begin unselected and have no hidden
    # state probe. A selected feature performs its own bounded admission later.
    $maintenanceCodexSelection = ''
    if ($Definition.codexGuidance) {
        $maintenanceCodexSelection = @'
  SectionSetText ${SEC_CODEX_REGISTER_REMOVE} "Remove the Cortex connection from Codex"
  SectionSetText ${SEC_CODEX_GUIDANCE_REMOVE} "Remove Cortex guidance from Codex"
'@
    }
    $featureSelectionCall = ''
    $featureSelectionValidation = ''
    if ($Definition.codexGuidance) {
        $featureSelectionCall = '  Call ValidateFeatureSelections'
        $featureSelectionValidation = @'
Function ValidateFeatureSelections
  SectionGetFlags ${SEC_CODEX_REGISTER} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  SectionGetFlags ${SEC_CODEX_REGISTER_REMOVE} $R1
  IntOp $R1 $R1 & ${SF_SELECTED}
  IntCmp $R0 0 feature_guidance_check
  IntCmp $R1 0 feature_guidance_check
  MessageBox MB_ICONSTOP "Choose either install/repair or remove for the Cortex Codex connection, not both." /SD IDOK
  SetErrorLevel 64
  Abort
feature_guidance_check:
  SectionGetFlags ${SEC_CODEX_GUIDANCE} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  SectionGetFlags ${SEC_CODEX_GUIDANCE_REMOVE} $R1
  IntOp $R1 $R1 & ${SF_SELECTED}
  IntCmp $R0 0 feature_selection_valid
  IntCmp $R1 0 feature_selection_valid
  MessageBox MB_ICONSTOP "Choose either install/repair or remove for Cortex Codex guidance, not both." /SD IDOK
  SetErrorLevel 64
  Abort
feature_selection_valid:
FunctionEnd
'@
    }
    $readyCodexSummary = ''
    if ($Definition.codexGuidance) {
        $readyCodexSummary = @'
  SectionGetFlags ${SEC_CODEX_REGISTER} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  StrCmp $R0 0 ready_codex_guidance
  StrCpy $ReadyFeatures "$ReadyFeatures$\r$\n- Codex connection: install or repair"
ready_codex_guidance:
  SectionGetFlags ${SEC_CODEX_GUIDANCE} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  StrCmp $R0 0 ready_codex_register_remove
  StrCpy $ReadyFeatures "$ReadyFeatures$\r$\n- Codex guidance: install or repair"
ready_codex_register_remove:
  SectionGetFlags ${SEC_CODEX_REGISTER_REMOVE} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  StrCmp $R0 0 ready_codex_guidance_remove
  StrCpy $ReadyFeatures "$ReadyFeatures$\r$\n- Codex connection: remove"
ready_codex_guidance_remove:
  SectionGetFlags ${SEC_CODEX_GUIDANCE_REMOVE} $R0
  IntOp $R0 $R0 & ${SF_SELECTED}
  StrCmp $R0 0 ready_codex_done
  StrCpy $ReadyFeatures "$ReadyFeatures$\r$\n- Codex guidance: remove"
ready_codex_done:
'@
    }
    $readyDataSummary = if ($null -ne $Definition.PSObject.Properties['dataLocation']) {
        "  StrCpy `$ReadyLocations `"Program: `$INSTDIR`$\r`$\nData: `$DataFolder`""
    } else {
        "  StrCpy `$ReadyLocations `"Program: `$INSTDIR`""
    }
    $maintenanceFunctions = @"
Function SetupTypePageCreate
  StrCmp `$ExistingInstallRoot "" setup_type_page_show
  Abort
setup_type_page_show:
  nsDialogs::Create 1018
  Pop `$SetupTypeDialog
  StrCmp `$SetupTypeDialog error setup_type_page_skip
  !insertmacro MUI_HEADER_TEXT "Choose setup type" "Use recommended defaults or review every optional feature."
  `${NSD_CreateLabel} 0 0 100% 20u "Recommended installs the required product with safe defaults. Optional trust and integrations remain off."
  Pop `$R0
  `${NSD_CreateRadioButton} 0 36u 100% 14u "Recommended"
  Pop `$SetupTypeRecommended
  `${NSD_CreateLabel} 14u 55u 92% 22u "Install the core product, register normal startup, and preserve any existing data."
  Pop `$R0
  `${NSD_CreateRadioButton} 0 88u 100% 14u "Custom"
  Pop `$SetupTypeCustom
  `${NSD_CreateLabel} 14u 107u 92% 30u "Review optional publisher trust and product integrations on the next page. Nothing optional is selected automatically."
  Pop `$R0
  `${NSD_Check} `$SetupTypeRecommended
  nsDialogs::Show
setup_type_page_skip:
FunctionEnd

Function SetupTypePageLeave
  `${NSD_GetState} `$SetupTypeCustom `$R0
  StrCmp `$R0 `${BST_CHECKED} setup_type_custom
  StrCpy `$SetupType "recommended"
  Return
setup_type_custom:
  StrCpy `$SetupType "custom"
FunctionEnd

Function InitializeTrustOption
  SectionSetFlags `${SEC_TRUST} 0
  `${GetParameters} `$R0
  ClearErrors
  `${GetOptions} `$R0 "/TRUST=" `$R1
  IfErrors trust_option_done
  StrCmp `$R1 "0" trust_option_done
  StrCmp `$R1 "1" 0 trust_option_invalid
  SectionSetFlags `${SEC_TRUST} `${SF_SELECTED}
  Goto trust_option_done
trust_option_invalid:
  MessageBox MB_ICONSTOP "The trust selection requires /TRUST=0 or /TRUST=1. Setup has not started." /SD IDOK
  SetErrorLevel 64
  Abort
trust_option_done:
FunctionEnd

Function InitializeMaintenanceOptions
  StrCpy `$MaintenanceAction "install"
  StrCpy `$MaintenanceExplicit "0"
  StrCmp `$ExistingInstallRoot "" maintenance_option_parse
  StrCpy `$MaintenanceAction "repair"
maintenance_option_parse:
  `${GetParameters} `$R0
  ClearErrors
  `${GetOptions} `$R0 "/MAINTENANCE=" `$R1
  IfErrors maintenance_option_done
  StrCmp `$ExistingInstallRoot "" maintenance_option_invalid
  StrCmp `$R1 "modify" maintenance_option_explicit
  StrCmp `$R1 "repair" maintenance_option_explicit
  StrCmp `$R1 "uninstall" maintenance_option_explicit maintenance_option_invalid
maintenance_option_explicit:
  StrCpy `$MaintenanceAction `$R1
  StrCpy `$MaintenanceExplicit "1"
  Goto maintenance_option_done
maintenance_option_invalid:
  MessageBox MB_ICONSTOP "Maintenance requires an existing installation and exactly modify, repair, or uninstall. No system change was made." /SD IDOK
  SetErrorLevel 64
  Abort
maintenance_option_done:
FunctionEnd

Function RunInstalledUninstaller
  IfFileExists "`$INSTDIR\Uninstall.exe" maintenance_uninstaller_present
  MessageBox MB_ICONSTOP "The installed uninstaller is missing. Run Repair first; no databases were changed." /SD IDOK
  SetErrorLevel 3
  Abort
maintenance_uninstaller_present:
  ClearErrors
  ExecWait '"`$INSTDIR\Uninstall.exe"' `$R0
  IfErrors 0 +2
  StrCpy `$R0 "8"
  IntCmp `$R0 0 maintenance_uninstall_complete
  MessageBox MB_ICONSTOP "Uninstall did not complete (code `$R0). Program and database state were preserved for repair." /SD IDOK
  SetErrorLevel `$R0
  Abort
maintenance_uninstall_complete:
  Quit
FunctionEnd

Function MaintenancePageCreate
  StrCmp `$ExistingInstallRoot "" maintenance_page_skip
  StrCmp `$MaintenanceExplicit "1" maintenance_page_explicit
  nsDialogs::Create 1018
  Pop `$MaintenanceDialog
  StrCmp `$MaintenanceDialog error maintenance_page_skip
  !insertmacro MUI_HEADER_TEXT "Maintain $displayName" "Choose one action. Databases stay in place."
  `${NSD_CreateLabel} 0 0 100% 24u "Installed version: `$ExistingVersion"
  Pop `$R0
  `${NSD_CreateRadioButton} 0 34u 100% 14u "Modify optional features"
  Pop `$MaintenanceModify
  `${NSD_CreateRadioButton} 0 60u 100% 14u "Repair or update program files"
  Pop `$MaintenanceRepair
  `${NSD_CreateRadioButton} 0 86u 100% 14u "Uninstall the program (keep all databases)"
  Pop `$MaintenanceUninstall
  `${NSD_CreateLabel} 14u 107u 92% 30u "Modify changes only the features you select. Repair leaves optional features alone. Uninstall retains the configured data location and all database files."
  Pop `$R0
  `${NSD_Check} `$MaintenanceRepair
  nsDialogs::Show
  Return
maintenance_page_explicit:
  StrCmp `$MaintenanceAction "modify" 0 maintenance_explicit_not_modify
$maintenanceCodexSelection
  Goto maintenance_page_skip
maintenance_explicit_not_modify:
  StrCmp `$MaintenanceAction "uninstall" 0 maintenance_page_skip
  Call RunInstalledUninstaller
maintenance_page_skip:
  StrCmp `$MaintenanceAction "repair" 0 +2
  SectionSetFlags `${SEC_TRUST} 0
  Abort
FunctionEnd

Function MaintenancePageLeave
  `${NSD_GetState} `$MaintenanceModify `$R0
  StrCmp `$R0 `${BST_CHECKED} maintenance_choose_modify
  `${NSD_GetState} `$MaintenanceUninstall `$R0
  StrCmp `$R0 `${BST_CHECKED} maintenance_choose_uninstall
  StrCpy `$MaintenanceAction "repair"
  SectionSetFlags `${SEC_TRUST} 0
  Return
maintenance_choose_modify:
  StrCpy `$MaintenanceAction "modify"
$maintenanceCodexSelection
  Return
maintenance_choose_uninstall:
  StrCpy `$MaintenanceAction "uninstall"
  Call RunInstalledUninstaller
FunctionEnd

Function ComponentsPagePre
  StrCmp `$InstallMode "install" components_page_install
  StrCmp `$MaintenanceAction "modify" components_page_show
  Abort
components_page_install:
  StrCmp `$SetupType "custom" components_page_show
  Abort
components_page_show:
FunctionEnd

$featureSelectionValidation

Function ComponentsPageLeave
$featureSelectionCall
FunctionEnd

Function ReadyPageCreate
  nsDialogs::Create 1018
  Pop `$ReadyDialog
  StrCmp `$ReadyDialog error ready_page_skip
  !insertmacro MUI_HEADER_TEXT "Ready to proceed" "Review the selected action before setup changes this computer."
  StrCpy `$ReadyFeatures "- Required product files: install, update, or repair"
  StrCmp `$MaintenanceAction "modify" 0 ready_required_done
  StrCpy `$ReadyFeatures "- Required product files: unchanged"
ready_required_done:
  SectionGetFlags `${SEC_TRUST} `$R0
  IntOp `$R0 `$R0 & `${SF_SELECTED}
  StrCmp `$R0 0 ready_trust_done
  StrCpy `$ReadyFeatures "`$ReadyFeatures`$\r`$\n- Pegasus Prod Alpha publisher trust: install"
ready_trust_done:
$readyCodexSummary
$readyDataSummary
  `${NSD_CreateLabel} 0 0 100% 18u "Action: `$InstallMode"
  Pop `$R0
  `${NSD_CreateLabel} 0 25u 100% 36u "`$ReadyLocations"
  Pop `$R0
  `${NSD_CreateLabel} 0 70u 100% 88u "Selected work:`$\r`$\n`$ReadyFeatures"
  Pop `$R0
  GetDlgItem `$R0 `$HWNDPARENT 1
  StrCpy `$R1 "Install"
  StrCmp `$MaintenanceAction "modify" 0 +2
  StrCpy `$R1 "Apply"
  SendMessage `$R0 `${WM_SETTEXT} 0 "STR:`$R1"
  nsDialogs::Show
ready_page_skip:
FunctionEnd
"@
    $sessionStartup = $null -ne $Definition.PSObject.Properties['userSessionStartup'] -and $Definition.userSessionStartup
    # Declarative backend contract for presentation only. Missing/false keeps
    # existing products' generated source unchanged; no startup action is added.
    $keepRuntimeStopped = $null -ne $Definition.PSObject.Properties['keepRuntimeStopped'] -and $Definition.keepRuntimeStopped
    $sessionShortcut = if ($null -ne $Definition.PSObject.Properties['userSessionShortcut']) {
        [string]$Definition.userSessionShortcut
    } else { 'standard' }
    $finishOptions = ''
    $finishReferenceOptions = ''
    if ($null -ne $Definition.PSObject.Properties['finishReference']) {
        $referenceRelative = $Definition.finishReference.relativePath.Replace('/', '\')
        $referenceLabel = $Definition.finishReference.label
        $finishReferenceOptions = @"
!define MUI_FINISHPAGE_SHOWREADME "`$INSTDIR\$referenceRelative"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$referenceLabel"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
"@
    }
    $shortcutBlock = "  CreateShortCut `"`$SMPROGRAMS\$publisher\$displayName.lnk`" `"`$INSTDIR\$launchRelative`""
    $shortcutCleanup = ''
    $obsoleteCleanupBlock = ''
    if ($null -ne $Definition.PSObject.Properties['removeObsoletePrograms'] -and $Definition.removeObsoletePrograms) {
        $obsoleteCleanupBlock = @"
  DetailPrint "Removing superseded inventoried program files; active programs and all databases remain untouched."
  ClearErrors
  ExecWait '"`$INSTDIR\$setupRelative" "prune-program-versions" "--install-root" "`$INSTDIR"' `$0
  IfErrors 0 +2
  StrCpy `$0 "8"
  IntCmp `$0 0 obsolete_programs_done
  MessageBox MB_ICONSTOP "$displayName is installed, but obsolete program cleanup requires attention (code `$0). No backup was created. Review the bounded setup diagnostic; active programs and databases are preserved." /SD IDOK
  SetErrorLevel `$0
  Abort
obsolete_programs_done:
"@
    }
    $finishText = "$displayName has been installed. Existing product data was preserved.`$\r`$\n`$\r`$\nUse the Start Menu shortcut to open the product."
    if ($sessionStartup) {
        # Interactive shortcuts run as the invoking user. Background activation
        # is registered by the product adapter and can also start on first PAO
        # or MCP use. An elevated installer must not manufacture or depend on a
        # signed-in user's medium token after machine setup has succeeded.
        $finishText = "$displayName has been installed and automatic startup is registered. The signed-in user host starts on first use or at the next sign-in.`$\r`$\n`$\r`$\nUse the Start Menu shortcuts for configuration and service notifications."
        $shortcutBlock = @"
  CreateShortCut "`$SMPROGRAMS\$publisher\$displayName.lnk" "`$INSTDIR\$launchRelative" "--dashboard"
  CreateShortCut "`$SMPROGRAMS\$publisher\$displayName Configuration.lnk" "`$INSTDIR\$launchRelative" "--dashboard"
  CreateShortCut "`$SMPROGRAMS\$publisher\$displayName Tray.lnk" "`$INSTDIR\$launchRelative"
"@
        $shortcutCleanup = @"
  Delete "`$SMPROGRAMS\$publisher\$displayName Configuration.lnk"
  Delete "`$SMPROGRAMS\$publisher\$displayName Tray.lnk"
"@
        if ($sessionShortcut -ceq 'single') {
            $shortcutBlock = @"
  Delete "`$SMPROGRAMS\$publisher\$displayName.lnk"
  Delete "`$SMPROGRAMS\$publisher\$displayName Configuration.lnk"
  Delete "`$SMPROGRAMS\$publisher\$displayName Tray.lnk"
  CreateShortCut "`$SMPROGRAMS\$publisher\$displayName.lnk" "`$INSTDIR\$launchRelative"
"@
            $finishText = "$displayName has been installed and automatic startup is registered. The signed-in user host starts on first use or at the next sign-in.`$\r`$\n`$\r`$\nUse the $displayName shortcut for configuration and service notifications."
        }
    }
    $setupPlanText = 'Setup plan: run only selected feature routines, stop only owned product components when required, preserve databases, then restart and verify.'
    if ($keepRuntimeStopped) {
        $setupPlanText = 'Setup plan: run only selected feature routines, require the product already stopped, preserve databases and startup settings, and leave the runtime stopped.'
        $finishText = "$displayName has been installed. Setup left the runtime stopped and preserved existing sign-in startup settings.`$\r`$\n`$\r`$\nUse the Start Menu shortcut to start the product explicitly when ready."
    }
    if ($finishOptions -and $finishReferenceOptions) { $finishOptions += "`r`n" }
    $finishOptions += $finishReferenceOptions
    $codexDescriptions = if ($Definition.codexGuidance) { @'
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CODEX_REGISTER} "Optional explicit routine. Install or repair only the Cortex MCP connection for this user's Codex."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CODEX_GUIDANCE} "Optional explicit routine. Install or repair only the managed Cortex guidance blocks."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CODEX_REGISTER_REMOVE} "Optional explicit routine. Remove only the exact owned Cortex MCP connection."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CODEX_GUIDANCE_REMOVE} "Optional explicit routine. Remove only the exact owned Cortex guidance blocks."
'@ } else { '' }
    $optionalIntegration = if ($Definition.codexGuidance) { 'Trust and Codex integration are optional.' } else { 'Publisher trust installation is optional.' }
    $componentIntroduction = if ($Definition.codexGuidance) {
        'The core product is required. Every optional feature below has its own routine and runs only when explicitly selected.'
    } else { 'Install the required product. Publisher trust changes require an explicit selection.' }
    return @"
Unicode true
RequestExecutionLevel admin
ManifestDPIAware true
XPStyle on
SetFont "Segoe UI" 9
Name "$displayName Prod Alpha"
Caption "$displayName $releaseVersion Prod Alpha"
BrandingText "$displayName - Prod Alpha"
OutFile "$installerLiteral"
InstallDir "`$PROGRAMFILES64\$publisher\$productFolder"
InstallDirRegKey HKLM "$registryKey" "InstallLocation"
ShowInstDetails hide
ShowUninstDetails show
CRCCheck on
SetCompressor lzma
VIProductVersion "$fileVersion"
VIFileVersion "$fileVersion"
VIAddVersionKey /LANG=1033 "ProductName" "$displayName"
VIAddVersionKey /LANG=1033 "ProductVersion" "$releaseVersion"
VIAddVersionKey /LANG=1033 "CompanyName" "$publisher"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) $copyrightYear $publisher"
VIAddVersionKey /LANG=1033 "FileDescription" "$displayName unified Prod Alpha installer"
VIAddVersionKey /LANG=1033 "FileVersion" "$fileVersion"
$signingDirective
!include FileFunc.nsh
!include Sections.nsh
!include nsDialogs.nsh
!include WinMessages.nsh
!include MUI2.nsh
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING
!define MUI_CUSTOMFUNCTION_GUIINIT InitializeWizard
$wizardIcons
$codexDeclarations
Var WelcomeText
Var ExistingInstallRoot
Var ExistingInstallSource
Var ExistingVersion
Var InstallMode
Var TrustInstalledThisRun
Var MaintenanceAction
Var MaintenanceExplicit
Var MaintenanceDialog
Var MaintenanceModify
Var MaintenanceRepair
Var MaintenanceUninstall
Var SetupType
Var SetupTypeDialog
Var SetupTypeRecommended
Var SetupTypeCustom
Var ReadyDialog
Var ReadyFeatures
Var ReadyLocations
$payloadDeclarations
$dataDeclarations
Function .onInit
  SetRegView 64
  StrCpy "`$InstallMode" "install"
  StrCpy "`$SetupType" "recommended"
  StrCpy "`$TrustInstalledThisRun" "0"
  ReadRegStr `$ExistingInstallRoot HKLM "$registryKey" "InstallLocation"
  ReadRegStr `$ExistingInstallSource HKLM "$registryKey" "InstallSource"
  ReadRegStr `$ExistingVersion HKLM "$registryKey" "ReleaseVersion"
  StrCmp `$ExistingInstallRoot "" 0 existing_install_found
  ReadRegStr `$ExistingInstallRoot HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "InstallLocation"
  ReadRegStr `$ExistingInstallSource HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "InstallSource"
  ReadRegStr `$ExistingVersion HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "DisplayVersion"
  StrCmp `$ExistingInstallRoot "" init_done
existing_install_found:
  StrCpy `$INSTDIR `$ExistingInstallRoot
  StrCpy `$InstallMode "upgrade"
  StrCmp `$ExistingVersion "$releaseVersion" 0 existing_mode_selected
  StrCpy `$InstallMode "repair"
existing_mode_selected:
init_done:
$payloadInitialization
  Call InitializeMaintenanceOptions
  Call InitializeTrustOption
$dataInitialization
$codexInitialization
  StrCpy `$WelcomeText "This wizard will `$InstallMode $displayName $releaseVersion.`$\r`$\n`$\r`$\nExisting installations keep their registered program and database locations. $optionalIntegration`$\r`$\n`$\r`$\nSelect Next to review the setup choices."
  StrCmp `$ExistingInstallRoot "" +2
  StrCpy `$WelcomeText "Detected $displayName `$ExistingVersion.`$\r`$\n`$\r`$\nMode: `$InstallMode`$\r`$\nProgram folder: `$INSTDIR`$\r`$\n`$\r`$\nExisting databases will be preserved. Select Next to choose Update, Modify, Repair, or Uninstall."
FunctionEnd

Function DirectoryPagePre
  StrCmp `$InstallMode "install" directory_page_show
  Abort
directory_page_show:
FunctionEnd
!define MUI_WELCOMEPAGE_TITLE "$displayName Setup"
!define MUI_WELCOMEPAGE_TEXT "`$WelcomeText"
!insertmacro MUI_PAGE_WELCOME
$noticePage
Page custom SetupTypePageCreate SetupTypePageLeave
Page custom MaintenancePageCreate MaintenancePageLeave
!define MUI_COMPONENTSPAGE_TEXT_TOP "$componentIntroduction"
!define MUI_PAGE_CUSTOMFUNCTION_PRE ComponentsPagePre
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ComponentsPageLeave
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPagePre
!define MUI_PAGE_HEADER_TEXT "Program location"
!define MUI_PAGE_HEADER_SUBTEXT "Choose the machine-wide program folder for a new installation."
!insertmacro MUI_PAGE_DIRECTORY
$dataPages
Page custom ReadyPageCreate
!define MUI_PAGE_HEADER_TEXT "Installing $displayName"
!define MUI_PAGE_HEADER_SUBTEXT "Setup is applying the selected features. Expand details for bounded technical progress."
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$finishText"
$finishOptions
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Section -PreparePayload
$featureSelectionCall
  DetailPrint "$setupPlanText"
  InitPluginsDir
SectionEnd

Section /o "Trust Pegasus Prod Alpha publisher on this machine" SEC_TRUST
$trustPayloadPreparation
  IfFileExists "`$PROGRAMFILES64\PowerShell\7\pwsh.exe" trust_pwsh_present
  MessageBox MB_ICONSTOP "PowerShell 7 is required for the explicitly selected Prod Alpha trust operation. Product setup was not started."
  SetErrorLevel 8
  Abort
trust_pwsh_present:
  DetailPrint "Installing explicitly selected, pinned Pegasus Prod Alpha trust before product setup..."
  ClearErrors
  ExecWait '"`$PROGRAMFILES64\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -NonInteractive -File "$trustPackageRoot\$trustRelative" -Action Install -PackageRoot "$trustPackageRoot" -InstallRoot "`$INSTDIR" -ComponentId "$productId" -Confirm:`$`$false' `$1
  IfErrors 0 +2
  StrCpy `$1 "8"
  IntCmp `$1 0 trust_success
  MessageBox MB_ICONSTOP "The explicit trust operation failed closed with code `$1. Product setup was not started."
  SetErrorLevel `$1
  Abort
trust_success:
  StrCpy `$TrustInstalledThisRun "1"
  DetailPrint "Explicit trust operation completed."
SectionEnd

Section "$displayName (required)" SEC_PRODUCT
  SectionIn RO
  StrCmp `$MaintenanceAction "modify" product_modify_only
$productPayloadPreparation
  SetRegView 64
  DetailPrint "Mode: `$InstallMode"
  DetailPrint "Install root: `$INSTDIR"
$dataDetails
  DetailPrint "Verifying and installing the signed $displayName product. Bounded stages appear in the setup console."
  DetailPrint "Persistent setup diagnostics: `$INSTDIR\state\install\logs (when the install root is available)."
$installInvocation
  DetailPrint "Setup backend finished with code `$0 (8 also indicates a launch failure)."
  IntCmp `$0 0 product_done
  StrCmp `$TrustInstalledThisRun "1" 0 product_failed_no_trust
  MessageBox MB_ICONSTOP "$displayName `$InstallMode failed with setup code `$0. The explicitly selected trust remains installed. Rerun the corrected package to repair; review the visible details and setup failure receipt." /SD IDOK
  SetErrorLevel `$0
  Abort
product_failed_no_trust:
  MessageBox MB_ICONSTOP "$displayName `$InstallMode failed with setup code `$0. No trust was installed. Review the visible details and setup failure receipt." /SD IDOK
  SetErrorLevel `$0
  Abort
product_done:
  SetOutPath "`$INSTDIR"
  CreateDirectory "`$INSTDIR\installer"
  StrCmp `$EXEPATH "`$INSTDIR\$maintenanceSetupRelative" maintenance_setup_ready
  ClearErrors
  CopyFiles /SILENT "`$EXEPATH" "`$INSTDIR\$maintenanceSetupRelative"
  IfErrors maintenance_setup_failed maintenance_setup_ready
maintenance_setup_failed:
  MessageBox MB_ICONSTOP "$displayName program files were installed, but the reusable maintenance setup could not be saved. Databases were preserved; rerun this installer after closing Cortex." /SD IDOK
  SetErrorLevel 8
  Abort
maintenance_setup_ready:
  WriteUninstaller "`$INSTDIR\Uninstall.exe"
  SetShellVarContext all
  CreateDirectory "`$SMPROGRAMS\$publisher"
$shortcutBlock
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "DisplayName" "$displayName Prod Alpha"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "DisplayVersion" "$releaseVersion"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "Publisher" "$publisher"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "InstallLocation" "`$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "UninstallString" '"`$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "QuietUninstallString" '"`$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "ModifyPath" '"`$INSTDIR\$maintenanceSetupRelative" /MAINTENANCE=modify'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "RepairPath" '"`$INSTDIR\$maintenanceSetupRelative" /MAINTENANCE=repair'
  WriteRegStr HKLM "$registryKey" "InstallSource" "$installSourceReference"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "InstallSource" "$installSourceReference"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "DisplayIcon" "`$INSTDIR\$iconRelative"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "InstallDate" "$installDate"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "EstimatedSize" $estimatedKilobytes
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "NoModify" 0
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName" "NoRepair" 0
$obsoleteCleanupBlock
  DetailPrint "$displayName $releaseVersion `$InstallMode complete. Existing product databases were preserved."
  Goto product_section_done
product_modify_only:
  DetailPrint "Modify mode leaves required Cortex program files and databases unchanged; only explicitly selected optional features run."
product_section_done:
SectionEnd

$codexSections

$codexFunctions
$maintenanceFunctions
Function InitializeWizard
  SectionSetText `${SEC_PRODUCT} "$displayName `$InstallMode (preserve state)"
FunctionEnd

Section "un.Remove $displayName binaries (required)" un.SEC_PRODUCT
  SectionIn RO
  SetRegView 64
  DetailPrint "Stopping and unregistering the admitted $displayName product..."
  ClearErrors
  ExecWait '"`$INSTDIR\$setupRelative" "unregister-product"' `$0
  IfErrors 0 +2
  StrCpy `$0 "8"
  IntCmp `$0 0 unregistered
  MessageBox MB_ICONSTOP "$displayName unregistration failed with bounded setup code `$0. No product files or state were removed."
  SetErrorLevel `$0
  Abort
unregistered:
  DetailPrint "Removing exact immutable files from the validated uninstall inventory; mutable state is retained."
  ClearErrors
  ExecWait '"`$INSTDIR\$setupRelative" "remove-product-files"' `$3
  IfErrors 0 +2
  StrCpy `$3 "8"
  IntCmp `$3 0 immutable_removed
  MessageBox MB_ICONSTOP "The validated immutable-file removal failed with setup code `$3. State and remaining files were preserved for recovery."
  SetErrorLevel `$3
  Abort
immutable_removed:
  SetShellVarContext all
  Delete "`$SMPROGRAMS\$publisher\$displayName.lnk"
$shortcutCleanup
  RMDir "`$SMPROGRAMS\$publisher"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$displayName"
SectionEnd

Section /o "un.Remove Pegasus Prod Alpha trust installed for $displayName" un.SEC_TRUST
  IfFileExists "`$PROGRAMFILES64\PowerShell\7\pwsh.exe" untrust_pwsh_present
  MessageBox MB_ICONSTOP "PowerShell 7 is required for explicit Prod Alpha trust removal. Machine trust was left unchanged."
  SetErrorLevel 8
  Abort
untrust_pwsh_present:
  DetailPrint "Removing explicitly selected Prod Alpha trust; bounded stages appear in the setup console."
  ClearErrors
  ExecWait '"`$PROGRAMFILES64\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -NonInteractive -File "`$INSTDIR\$trustRelative" -Action Uninstall -PackageRoot "`$INSTDIR" -InstallRoot "`$INSTDIR" -ComponentId "$productId" -Confirm:`$`$false' `$1
  IfErrors 0 +2
  StrCpy `$1 "8"
  IntCmp `$1 0 untrust_done
  MessageBox MB_ICONSTOP "Explicit trust removal failed closed with code `$1. Machine trust and product state were preserved for recovery."
  SetErrorLevel `$1
  Abort
untrust_done:
  DetailPrint "Explicit trust removal completed."
SectionEnd

Section -un.Final
  Delete "`$INSTDIR\$maintenanceSetupRelative"
  Delete "`$INSTDIR\$setupRelative"
  Delete "`$INSTDIR\$trustRelative"
  Delete "`$INSTDIR\$trustDirectory\PegasusSigning.psm1"
  Delete "`$INSTDIR\$trustDirectory\PegasusTrust.psm1"
  RMDir "`$INSTDIR\$setupDirectory"
  RMDir "`$INSTDIR\$trustDirectory"
  Delete "`$INSTDIR\Uninstall.exe"
  RMDir "`$INSTDIR"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT `${SEC_PRODUCT} "Required program files and product-owned startup. Registered data locations and existing databases are preserved."
  !insertmacro MUI_DESCRIPTION_TEXT `${SEC_TRUST} "Optional machine trust for the pinned internal Prod Alpha publisher. Runs before setup; never enabled silently."
$codexDescriptions
!insertmacro MUI_FUNCTION_DESCRIPTION_END
!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT `${un.SEC_PRODUCT} "Remove only inventoried program files and owned registrations. Retain data by default."
  !insertmacro MUI_DESCRIPTION_TEXT `${un.SEC_TRUST} "Remove only product-owned trust when no other Pegasus component depends on it. Preserve preexisting trust."
!insertmacro MUI_UNFUNCTION_DESCRIPTION_END
"@
}

Export-ModuleMember -Function Read-PegasusInstallerDefinition, Assert-PegasusInstallerDefinition, New-PegasusWindowsInstallerSource
