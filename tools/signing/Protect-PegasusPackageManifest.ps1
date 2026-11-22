[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
$manifestPath = Join-Path $root 'package-manifest.json'
$signaturePath = Join-Path $root 'package-manifest.p7s'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The canonical package manifest is missing.' }
if (Test-Path -LiteralPath $signaturePath) { throw 'The detached package signature is create-only.' }
$trust = Read-PegasusTrustBundle -PackageRoot $root
$publisher = Get-PegasusStoreCertificate `
    -Thumbprint $PublisherThumbprint -Provider $Provider -RequirePrivateKey
if ((ConvertTo-PegasusThumbprint $publisher.Thumbprint) -cne
    (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint)) {
    throw 'The selected manifest signer does not match the public trust bundle.'
}
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$cms = [Security.Cryptography.Pkcs.SignedCms]::new(
    [Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new(
    [Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber,
    $publisher)
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
$signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$cms.ComputeSignature($signer, $false)
[IO.File]::WriteAllBytes($signaturePath, $cms.Encode())

[pscustomobject]@{
    Manifest = $manifestPath
    ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Signature = $signaturePath
    SignatureSha256 = (Get-FileHash -LiteralPath $signaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $publisher.Thumbprint)
    Algorithm = 'CMS-SHA256-RSA'
    Timestamped = $false
}
