[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DefinitionPath,
    [Parameter(Mandatory)][string]$ProductDirectory,
    [Parameter(Mandatory)][string]$OutputSource,
    [string]$OutputExecutable = '',
    [string]$PublisherThumbprint = '',
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusWindowsInstaller.psm1') -Force
$definition = Read-PegasusInstallerDefinition -Path $DefinitionPath
$source = New-PegasusWindowsInstallerSource -Definition $definition -ProductDirectory $ProductDirectory `
    -OutputExecutable $OutputExecutable -PublisherThumbprint $PublisherThumbprint -Provider $Provider
$target = [IO.Path]::GetFullPath($OutputSource)
if (-not $target.EndsWith('.nsi', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputSource must be an .nsi file.' }
$current = $target
while ($current) {
    if ((Test-Path -LiteralPath $current) -and
        ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'OutputSource crosses a reparse point.'
    }
    $current = Split-Path -Parent $current
}
[IO.File]::WriteAllText($target, $source, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Source = $target
    ProductId = $definition.productId
    SignedUninstaller = [bool]$PublisherThumbprint
    CompileRoute = if ($PublisherThumbprint) { 'admitted release compiler' } else { 'pao nsis validate/build; development only' }
}
