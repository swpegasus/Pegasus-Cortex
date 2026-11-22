[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BootstrapPublishRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$RootThumbprint,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$bootstrap = [IO.Path]::GetFullPath($BootstrapPublishRoot).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
if (Test-Path -LiteralPath $output) { throw 'The installer package output is create-only.' }
$bootstrapExe = Join-Path $bootstrap 'PegasusCortexInstaller.exe'
if (-not (Test-Path -LiteralPath $bootstrapExe -PathType Leaf)) { throw 'The installer bootstrap publish is missing PegasusCortexInstaller.exe.' }
New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
try {
    foreach ($name in @('PegasusCortexInstaller.exe', 'PegasusCortexInstaller.pdb')) {
        $source = Join-Path $bootstrap $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $output $name) -ErrorAction Stop }
    }
    $files = @{
        'Install-PegasusCortexV2X64.ps1' = 'deploy\x64\Install-PegasusCortexV2X64.ps1'
        'PegasusSigning.psm1' = 'tools\signing\PegasusSigning.psm1'
        'PegasusTrust.psm1' = 'tools\signing\PegasusTrust.psm1'
        'Invoke-PegasusProdAlphaTrust.ps1' = 'tools\signing\Invoke-PegasusProdAlphaTrust.ps1'
    }
    foreach ($entry in $files.GetEnumerator()) {
        Copy-Item -LiteralPath (Join-Path $repo $entry.Value) -Destination (Join-Path $output $entry.Key) -ErrorAction Stop
    }
    & (Join-Path $PSScriptRoot 'Export-PegasusProdAlphaTrustBundle.ps1') `
        -PackageRoot $output -RootThumbprint $RootThumbprint -PublisherThumbprint $PublisherThumbprint | Out-Null
    & (Join-Path $PSScriptRoot 'Sign-PegasusArtifactTree.ps1') `
        -PackageRoot $output -PublisherThumbprint $PublisherThumbprint -Provider $Provider -IncludePowerShell | Out-Null
    & (Join-Path $repo 'deploy\x64\New-PegasusCortexV2PackageManifest.ps1') `
        -PackageRoot $output -ReleaseVersion $ReleaseVersion -Role installer `
        -PublisherThumbprint $PublisherThumbprint -RootThumbprint $RootThumbprint | Out-Null
    & (Join-Path $PSScriptRoot 'Protect-PegasusPackageManifest.ps1') `
        -PackageRoot $output -PublisherThumbprint $PublisherThumbprint -Provider $Provider | Out-Null
    & (Join-Path $PSScriptRoot 'Test-PegasusSignedPackage.ps1') `
        -PackageRoot $output -Role installer -ReleaseVersion $ReleaseVersion | Out-Null
}
catch { throw }

[pscustomobject]@{
    PackageRoot = $output
    Role = 'installer'
    ReleaseVersion = $ReleaseVersion
    Channel = 'Prod Alpha'
    EntryPoint = (Join-Path $output 'PegasusCortexInstaller.exe')
}
