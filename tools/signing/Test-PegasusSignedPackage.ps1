[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][ValidateSet('application', 'launcher', 'installer', 'product', 'product-installer')][string]$Role,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [string]$ProductId = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force

$verified = Test-PegasusDetachedManifestSignature `
    -PackageRoot $PackageRoot `
    -ExpectedRole $Role `
    -ExpectedReleaseVersion $ReleaseVersion -ExpectedProductId $ProductId
Test-PegasusPackageContent -PackageRoot $PackageRoot -Manifest $verified.Manifest
Test-PegasusAuthenticodeFiles `
    -PackageRoot $PackageRoot `
    -Manifest $verified.Manifest `
    -Publisher $verified.Trust.Publisher

[pscustomobject]@{
    Valid = $true
    Channel = 'Prod Alpha'
    Role = $Role
    ReleaseVersion = $ReleaseVersion
    ManifestSha256 = (Get-FileHash -LiteralPath $verified.ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    SignatureSha256 = (Get-FileHash -LiteralPath $verified.SignaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $verified.Trust.Publisher.Thumbprint)
    RootThumbprint = (ConvertTo-PegasusThumbprint $verified.Trust.Root.Thumbprint)
}
