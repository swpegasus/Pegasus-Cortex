[CmdletBinding()]
param(
    [string]$ProductPackage = '',
    [switch]$Compile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\build\wizard-preview'))
if (-not $ProductPackage) { $ProductPackage = Join-Path $root 'artifacts\release\unified' }
$package = [IO.Path]::GetFullPath($ProductPackage)

function Assert-OrdinaryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 240 -or $full.IndexOfAny([char[]]@([char]34, [char]39, [char]96, [char]36, [char]13, [char]10)) -ge 0) {
        throw 'Preview path is outside the bounded literal contract.'
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Preview path crosses a reparse point.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}
function Assert-OutputFile([string]$Path) {
    $full = Assert-OrdinaryPath $Path
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetDirectoryName($full), $output)) {
        throw 'Preview output escaped its exact task-owned folder.'
    }
    if ((Test-Path -LiteralPath $full) -and -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw 'Preview output is not an ordinary file.'
    }
    return $full
}
[void](Assert-OrdinaryPath $output)
[void](Assert-OrdinaryPath $package)
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw 'Preview payload is absent.' }
if ((Test-Path -LiteralPath $output) -and -not (Test-Path -LiteralPath $output -PathType Container)) {
    throw 'Preview output is not an ordinary directory.'
}
New-Item -ItemType Directory -Path $output -Force -ErrorAction Stop | Out-Null

Import-Module (Join-Path $root 'tools\windows-installer\PegasusWindowsInstaller.psm1') -Force
$definition = Read-PegasusInstallerDefinition -Path (Join-Path $root 'deploy\x64\windows-installer.json')
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json -Depth 30
if ($manifest.releaseVersion -cnotmatch '^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$') { throw 'Preview payload version is invalid.' }
$definition.name = 'Pegasus Cortex Wizard Preview'
$definition.productId = 'pegasus.cortex.wizard-preview'
$definition.productFolder = 'CortexWizardPreview'
$definition.launchPath = 'PegasusCortex.exe'
$definition.wizardIconPath = 'tools/nsis/3.12/Contrib/Graphics/Icons/arrow-install.ico'
$definition.userSessionStartup = $true
$definition.dataLocation.relativePath = 'Pegasus/CortexWizardPreview'
$compiler = Assert-OrdinaryPath (Join-Path $package 'tools\nsis\3.12\Bin\makensis.exe')
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The packaged NSIS compiler is absent.' }

foreach ($mode in @('fresh', 'repair', 'failure')) {
    $nsi = Assert-OutputFile (Join-Path $output ($mode + '.nsi'))
    $exe = Assert-OutputFile (Join-Path $output ($mode + '.exe'))
    $uninstaller = Assert-OutputFile (Join-Path $output ('uninstall-' + $mode + '.exe'))
    $program = Assert-OrdinaryPath (Join-Path $output ('selected-program-' + $mode))
    $data = Assert-OrdinaryPath (Join-Path $output ('selected-data-' + $mode))
    $source = New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $package -OutputExecutable $exe
    $source = $source.Replace('RequestExecutionLevel admin', 'RequestExecutionLevel user')
    $source = [regex]::Replace($source, '(?m)^InstallDirRegKey [^\r\n]+\r?\n', '')
    $source = [regex]::Replace($source, '(?m)^InstallDir [^\r\n]+', ('InstallDir "' + $program + '"'))
    $source = [regex]::Replace($source, '(?m)^!define MUI_FINISHPAGE_TEXT [^\r\n]+',
        '!define MUI_FINISHPAGE_TEXT "SIMULATION completed. No product, service, trust, registry, or database was changed. The optional checkbox only shows a preview message."')

    # Preserve the production page functions and MUI macros. Only discovery and
    # action bodies are substituted; no production fixture switch is introduced.
    $installMode = if ($mode -eq 'repair') { 'repair' } else { 'install' }
    $locked = if ($mode -eq 'repair') { '1' } else { '0' }
    $init = @"
Function .onInit
  StrCpy `$INSTDIR "$program"
  StrCpy `$ExistingInstallRoot "$program"
  StrCpy `$ExistingVersion "$($manifest.releaseVersion)"
  StrCpy `$InstallMode "$installMode"
  StrCpy `$TrustInstalledThisRun "0"
  StrCpy `$DataFolder "$data"
  StrCpy `$DataLocationLocked "$locked"
  StrCpy `$WelcomeText "SIMULATION ONLY: $mode wizard. The actual shared MUI2 pages are retained. No product, service, trust, registry, or database changes are performed."
  Call InitializeCodexOptions
FunctionEnd
"@
    $initPattern = '(?ms)^Function \.onInit\r?\n.*?^FunctionEnd'
    if ([regex]::Matches($source, $initPattern).Count -ne 1) { throw 'Preview initialization boundary changed.' }
    $source = [regex]::Replace($source, $initPattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $init })
    $maintenancePattern = '(?ms)^Function RunInstalledUninstaller\r?\n.*?^FunctionEnd'
    if ([regex]::Matches($source, $maintenancePattern).Count -ne 1) { throw 'Preview maintenance boundary changed.' }
    $maintenance = @'
Function RunInstalledUninstaller
  DetailPrint "SIMULATION: maintenance uninstall selection; no changes performed."
  MessageBox MB_ICONINFORMATION "SIMULATION ONLY: the real installer would open the registered uninstaller and retain all databases." /SD IDOK
FunctionEnd
'@
    $source = [regex]::Replace($source, $maintenancePattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $maintenance })
    $sectionPattern = '(?ms)^(?<header>Section [^\r\n]+)\r?\n.*?^SectionEnd'
    if ([regex]::Matches($source, $sectionPattern).Count -ne 10) { throw 'Preview section inventory changed; review action substitutions.' }
    $source = [regex]::Replace($source, $sectionPattern, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $header = $match.Groups['header'].Value
        $body = '  DetailPrint "SIMULATION: optional component; no changes performed."'
        if ($header.EndsWith(' SEC_PRODUCT', [StringComparison]::Ordinal)) {
            $body = "  SectionIn RO`n  DetailPrint `"SIMULATION: verifying package and preserving existing data...`"`n  Sleep 500`n"
            if ($mode -eq 'failure') {
                $body += "  MessageBox MB_ICONSTOP `"SIMULATED setup failure, code 4. No product changes were performed.`" /SD IDOK`n  SetErrorLevel 4`n  Abort"
            } else {
                # The only persistent action permitted when someone later runs
                # the preview: an exact task-owned uninstaller for UI inspection.
                $body += "  WriteUninstaller `"$uninstaller`"`n  DetailPrint `"SIMULATION complete. Only the preview uninstaller was written in the preview folder.`""
            }
        } elseif ($header.EndsWith(' un.SEC_PRODUCT', [StringComparison]::Ordinal)) {
            $body = "  SectionIn RO`n  DetailPrint `"SIMULATION: uninstall preview; no files or registrations removed.`"`n  Sleep 500"
        }
        return $header + "`n" + $body + "`nSectionEnd"
    })
    if ($source -match '(?mi)^\s*(?:Exec\w*|ReadReg\w*|WriteReg\w*|DeleteReg\w*|InstallDirRegKey|CreateShortCut|Delete|RMDir|CreateDirectory|SetOutPath|File(?:Open|Write|Read|Close)?|!uninstfinalize)\b' -or
        $source -match '(?i)nsExec::|System::Call|"start-user-session"|"install-package"|File /r') {
        throw 'Preview retains a product/system action; refusing to write or compile it.'
    }
    $uninstallerWrites = @([regex]::Matches($source, '(?m)^  WriteUninstaller [^\r\n]+'))
    if (($mode -eq 'failure' -and $uninstallerWrites.Count -ne 0) -or
        ($mode -ne 'failure' -and ($uninstallerWrites.Count -ne 1 -or $uninstallerWrites[0].Value -cne ('  WriteUninstaller "' + $uninstaller + '"')))) {
        throw 'Preview uninstaller output is not confined to its exact task-owned path.'
    }
    [void](Assert-OutputFile $nsi)
    [IO.File]::WriteAllText($nsi, $source, [Text.UTF8Encoding]::new($false))
    if ($Compile) {
        [void](Assert-OutputFile $exe)
        $compilerOutput = & $compiler /V2 /NOCONFIG $nsi 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('Preview compilation failed: ' + ($compilerOutput | Select-Object -Last 16 | Out-String)) }
        [void](Assert-OutputFile $exe)
    }
    [pscustomobject]@{ Mode = $mode; Source = $nsi; Executable = if ($Compile) { $exe } else { $null }; Launched = $false }
}
