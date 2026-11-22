[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $InstallRoot = 'C:\Program Files\Pegasus\Cortex',

    [Parameter(Mandatory = $false)]
    [string] $Version = '1.2026.09.14.01.55.A'
)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($root -cne 'C:\Program Files\Pegasus\Cortex') {
    throw "Unexpected Cortex install root: $root"
}

$launcherPath = Join-Path $root 'PegasusCortex.exe'
$launcherFile = Get-Item -LiteralPath $launcherPath -ErrorAction Stop
if ($launcherFile.PSIsContainer -or
    $launcherFile.Name -cne 'PegasusCortex.exe' -or
    ($launcherFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Cortex launcher validation failed.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $launcherPath
if ($signature.Status -ne 'Valid') {
    throw "Cortex launcher signature is not valid: $($signature.Status)"
}

$menu = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Pegasus'
$mainLink = Join-Path $menu 'Pegasus Cortex.lnk'
$trayLink = Join-Path $menu 'Pegasus Cortex Tray.lnk'
$configurationLink = Join-Path $menu 'Pegasus Cortex Configuration.lnk'

$shell = New-Object -ComObject WScript.Shell
$main = $shell.CreateShortcut($mainLink)
$main.TargetPath = $launcherPath
$main.Arguments = ''
$main.WorkingDirectory = $root
$main.Description = 'Start Pegasus Cortex; configuration and service notifications are available from its tray menu.'
$main.Save()
foreach ($path in @($configurationLink, $trayLink)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop }
}

$verifiedMain = $shell.CreateShortcut($mainLink)
if ($verifiedMain.TargetPath -cne $launcherPath -or $verifiedMain.Arguments -ne '') {
    throw 'Pegasus Cortex shortcut verification failed.'
}
if (Test-Path -LiteralPath $configurationLink -or Test-Path -LiteralPath $trayLink) {
    throw 'A redundant Cortex Start Menu shortcut remains.'
}

[pscustomobject]@{
    ok = $true
    shortcut = $mainLink
    removed = @($configurationLink, $trayLink)
    launcher = $launcherPath
} | ConvertTo-Json -Compress
