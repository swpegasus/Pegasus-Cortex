[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$RootThumbprint,
    [Parameter(Mandatory)][string]$PublisherThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force

$rootPath = 'Cert:\CurrentUser\My\' + (ConvertTo-PegasusThumbprint $RootThumbprint)
$publisherPath = 'Cert:\CurrentUser\My\' + (ConvertTo-PegasusThumbprint $PublisherThumbprint)
if (-not (Test-Path -LiteralPath $rootPath) -or -not (Test-Path -LiteralPath $publisherPath)) {
    throw 'The exact root or publisher certificate is absent from CurrentUser\My.'
}
$rootCertificate = Get-Item -LiteralPath $rootPath
$publisherCertificate = Get-Item -LiteralPath $publisherPath
Assert-PegasusRootCertificate -Certificate $rootCertificate -ExpectedThumbprint $RootThumbprint
Assert-PegasusPublisherCertificate -Certificate $publisherCertificate -ExpectedThumbprint $PublisherThumbprint -RequirePrivateKey
Assert-PegasusPublisherChain -Publisher $publisherCertificate -Root $rootCertificate

$package = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
$packageItem = Get-Item -LiteralPath $package -Force -ErrorAction Stop
if (-not $packageItem.PSIsContainer -or ($packageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PackageRoot must be an ordinary existing directory.'
}
$trust = Join-Path $package 'trust'
if (Test-Path -LiteralPath $trust) { throw 'The public trust bundle is create-only.' }
New-Item -ItemType Directory -Path $trust -ErrorAction Stop | Out-Null
try {
    $rootFile = Join-Path $trust 'pegasus-prod-alpha-root.cer'
    $publisherFile = Join-Path $trust 'pegasus-prod-alpha-publisher.cer'
    Export-Certificate -Cert $rootCertificate -FilePath $rootFile -Type CERT -Force | Out-Null
    Export-Certificate -Cert $publisherCertificate -FilePath $publisherFile -Type CERT -Force | Out-Null
    $entries = @(
        [ordered]@{
            role = 'root'
            file = 'pegasus-prod-alpha-root.cer'
            subject = $rootCertificate.Subject
            thumbprint = (ConvertTo-PegasusThumbprint $rootCertificate.Thumbprint)
            sha256 = (Get-FileHash -LiteralPath $rootFile -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            role = 'publisher'
            file = 'pegasus-prod-alpha-publisher.cer'
            subject = $publisherCertificate.Subject
            thumbprint = (ConvertTo-PegasusThumbprint $publisherCertificate.Thumbprint)
            sha256 = (Get-FileHash -LiteralPath $publisherFile -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    $manifest = [ordered]@{
        schema = 'pegasus.public-trust-bundle/1.0'
        channel = 'Prod Alpha'
        rootThumbprint = (ConvertTo-PegasusThumbprint $rootCertificate.Thumbprint)
        publisherThumbprint = (ConvertTo-PegasusThumbprint $publisherCertificate.Thumbprint)
        certificates = $entries
    }
    [IO.File]::WriteAllText(
        (Join-Path $trust 'trust-bundle.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $null = Read-PegasusTrustBundle -PackageRoot $package
}
catch {
    if (Test-Path -LiteralPath $trust) { Remove-Item -LiteralPath $trust -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}

[pscustomobject]@{
    TrustRoot = $trust
    RootThumbprint = (ConvertTo-PegasusThumbprint $rootCertificate.Thumbprint)
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $publisherCertificate.Thumbprint)
    Files = 3
}
