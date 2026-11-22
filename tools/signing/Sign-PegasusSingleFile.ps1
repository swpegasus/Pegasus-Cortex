[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LiteralPath,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force

$path = [IO.Path]::GetFullPath($LiteralPath)
$item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
if ($item.PSIsContainer -or
    (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The release signer accepts one ordinary PE executable.'
}
$stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    if ($stream.Length -lt 2 -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
        throw 'The release signer accepts one ordinary PE executable.'
    }
}
finally { $stream.Dispose() }

$publisher = Get-PegasusStoreCertificate `
    -Thumbprint $PublisherThumbprint `
    -StoreLocation CurrentUser `
    -Provider $Provider `
    -RequirePrivateKey
$result = Set-AuthenticodeSignature `
    -LiteralPath $path `
    -Certificate $publisher `
    -HashAlgorithm SHA256 `
    -ErrorAction Stop
if ($null -eq $result.SignerCertificate -or
    (ConvertTo-PegasusThumbprint $result.SignerCertificate.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $publisher.Thumbprint) -or
    $result.Status -in @(
        [Management.Automation.SignatureStatus]::NotSigned,
        [Management.Automation.SignatureStatus]::HashMismatch)) {
    throw 'Authenticode signing failed for the generated executable.'
}

[pscustomobject]@{
    Path = $path
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $publisher.Thumbprint)
    Provider = $Provider
    Timestamped = $false
}
