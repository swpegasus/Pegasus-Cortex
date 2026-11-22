[CmdletBinding()]
param(
    [string]$ExistingRootThumbprint = '',
    [string]$ExistingPublisherThumbprint = '',
    [string]$PublicOutputDirectory = '',
    [ValidateRange(1, 10)][int]$ValidityYears = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force

$provider = 'Microsoft Software Key Storage Provider'
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    'My', [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$createdRoot = $null
$createdPublisher = $null
try {
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $rootMatches = @($store.Certificates | Where-Object { $_.Subject -ceq 'CN=Pegasus Local Development Root CA' })
    $publisherMatches = @($store.Certificates | Where-Object { $_.Subject -ceq 'CN=Pegasus Software Signing Prod Alpha' })
}
finally { $store.Dispose() }

if (-not [string]::IsNullOrWhiteSpace($ExistingRootThumbprint) -or
    -not [string]::IsNullOrWhiteSpace($ExistingPublisherThumbprint)) {
    if ([string]::IsNullOrWhiteSpace($ExistingRootThumbprint) -or
        [string]::IsNullOrWhiteSpace($ExistingPublisherThumbprint)) {
        throw 'Both existing thumbprints are required together.'
    }
    $rootExpected = ConvertTo-PegasusThumbprint $ExistingRootThumbprint
    $publisherExpected = ConvertTo-PegasusThumbprint $ExistingPublisherThumbprint
    $root = @($rootMatches | Where-Object { (ConvertTo-PegasusThumbprint $_.Thumbprint) -ceq $rootExpected })
    $publisher = @($publisherMatches | Where-Object { (ConvertTo-PegasusThumbprint $_.Thumbprint) -ceq $publisherExpected })
    if ($root.Count -ne 1 -or $publisher.Count -ne 1) {
        throw 'The exact existing Prod Alpha identity was not found uniquely in CurrentUser\My.'
    }
    Assert-PegasusRootCertificate -Certificate $root[0] -ExpectedThumbprint $rootExpected
    Assert-PegasusPublisherCertificate -Certificate $publisher[0] -ExpectedThumbprint $publisherExpected -RequirePrivateKey
    Assert-PegasusPublisherChain -Publisher $publisher[0] -Root $root[0]
    $rootCertificate = $root[0]
    $publisherCertificate = $publisher[0]
}
else {
    if ($rootMatches.Count -ne 0 -or $publisherMatches.Count -ne 0) {
        throw 'A Prod Alpha subject already exists. Re-run with both exact existing thumbprints; identities are never selected by subject alone.'
    }
    $notAfter = [DateTimeOffset]::UtcNow.AddYears($ValidityYears)
    try {
        $createdRoot = New-SelfSignedCertificate `
            -Type Custom `
            -Subject 'CN=Pegasus Local Development Root CA' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -Provider $provider `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable `
            -KeyUsage CertSign, CRLSign, DigitalSignature `
            -TextExtension @('2.5.29.19={critical}{text}ca=true&pathlength=1') `
            -NotAfter $notAfter.UtcDateTime
        $createdPublisher = New-SelfSignedCertificate `
            -Type Custom `
            -Subject 'CN=Pegasus Software Signing Prod Alpha' `
            -Signer $createdRoot `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -Provider $provider `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable `
            -KeyUsage DigitalSignature `
            -TextExtension @(
                '2.5.29.19={critical}{text}ca=false',
                '2.5.29.37={critical}{text}1.3.6.1.5.5.7.3.3') `
            -NotAfter $notAfter.UtcDateTime
        Assert-PegasusRootCertificate -Certificate $createdRoot
        Assert-PegasusPublisherCertificate -Certificate $createdPublisher -RequirePrivateKey
        Assert-PegasusPublisherChain -Publisher $createdPublisher -Root $createdRoot
        $rootCertificate = $createdRoot
        $publisherCertificate = $createdPublisher
    }
    catch {
        foreach ($certificate in @($createdPublisher, $createdRoot)) {
            if ($null -ne $certificate) {
                $path = 'Cert:\CurrentUser\My\' + $certificate.Thumbprint
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
            }
        }
        throw
    }
}

if (-not [string]::IsNullOrWhiteSpace($PublicOutputDirectory)) {
    $output = [IO.Path]::GetFullPath($PublicOutputDirectory).TrimEnd('\', '/')
    if (Test-Path -LiteralPath $output) { throw 'PublicOutputDirectory is create-only.' }
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    Export-Certificate -Cert $rootCertificate -FilePath (Join-Path $output 'pegasus-prod-alpha-root.cer') -Type CERT -Force | Out-Null
    Export-Certificate -Cert $publisherCertificate -FilePath (Join-Path $output 'pegasus-prod-alpha-publisher.cer') -Type CERT -Force | Out-Null
}

[pscustomobject]@{
    Channel = 'Prod Alpha'
    RootSubject = $rootCertificate.Subject
    RootThumbprint = (ConvertTo-PegasusThumbprint $rootCertificate.Thumbprint)
    PublisherSubject = $publisherCertificate.Subject
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $publisherCertificate.Thumbprint)
    Provider = $provider
    KeyAlgorithm = 'RSA-3072'
    HashAlgorithm = 'SHA-256'
    PrivateKeysExportable = $false
    PublicOutputDirectory = $PublicOutputDirectory
}
