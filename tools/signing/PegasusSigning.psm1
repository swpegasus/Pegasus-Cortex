Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProdAlphaChannel = 'Prod Alpha'
$script:RootSubject = 'CN=Pegasus Local Development Root CA'
$script:PublisherSubject = 'CN=Pegasus Software Signing Prod Alpha'
$script:CodeSigningOid = '1.3.6.1.5.5.7.3.3'
$script:Sha256Oid = '2.16.840.1.101.3.4.2.1'

function ConvertTo-PegasusThumbprint {
    param([Parameter(Mandatory)][string]$Value)

    $normalized = ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized -cnotmatch '^[0-9A-F]{40,128}$') {
        throw 'A certificate thumbprint is malformed.'
    }
    return $normalized
}

function Get-PegasusCertificateSha256 {
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData)).ToLowerInvariant()
}

function Assert-PegasusCertificateWindow {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow
    )

    if ($At.UtcDateTime -lt $Certificate.NotBefore.ToUniversalTime()) {
        throw "Certificate '$($Certificate.Subject)' is not yet valid."
    }
    if ($At.UtcDateTime -gt $Certificate.NotAfter.ToUniversalTime()) {
        throw "Certificate '$($Certificate.Subject)' is expired."
    }
}

function Get-PegasusBasicConstraints {
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    return @($Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]
        })
}

function Assert-PegasusRootCertificate {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$ExpectedThumbprint = '',
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow
    )

    if ($Certificate.Subject -cne $script:RootSubject -or
        $Certificate.Issuer -cne $script:RootSubject) {
        throw 'The root certificate subject or issuer is not the Prod Alpha root identity.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and
        (ConvertTo-PegasusThumbprint $Certificate.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $ExpectedThumbprint)) {
        throw 'The root certificate thumbprint is not the pinned thumbprint.'
    }
    Assert-PegasusCertificateWindow -Certificate $Certificate -At $At
    $constraints = @(Get-PegasusBasicConstraints -Certificate $Certificate)
    if ($constraints.Count -ne 1 -or -not $constraints[0].CertificateAuthority) {
        throw 'The Prod Alpha root certificate is not a certificate authority.'
    }
    $keyUsage = @($Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension]
        })
    if ($keyUsage.Count -ne 1 -or
        ($keyUsage[0].KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign) -eq 0) {
        throw 'The Prod Alpha root certificate lacks certificate-signing key usage.'
    }
}

function Assert-PegasusPublisherCertificate {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$ExpectedThumbprint = '',
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow,
        [switch]$RequirePrivateKey
    )

    if ($Certificate.Subject -cne $script:PublisherSubject) {
        throw 'The publisher certificate subject is not the Prod Alpha publisher identity.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and
        (ConvertTo-PegasusThumbprint $Certificate.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $ExpectedThumbprint)) {
        throw 'The publisher certificate thumbprint is not the pinned thumbprint.'
    }
    Assert-PegasusCertificateWindow -Certificate $Certificate -At $At
    $constraints = @(Get-PegasusBasicConstraints -Certificate $Certificate)
    if ($constraints.Count -ne 1 -or $constraints[0].CertificateAuthority) {
        throw 'The Prod Alpha publisher certificate must be an end-entity certificate.'
    }
    $keyUsage = @($Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension]
        })
    if ($keyUsage.Count -ne 1 -or
        ($keyUsage[0].KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
        throw 'The Prod Alpha publisher certificate lacks digital-signature key usage.'
    }
    $eku = @($Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
        })
    if ($eku.Count -ne 1 -or
        @($eku[0].EnhancedKeyUsages | Where-Object { $_.Value -ceq $script:CodeSigningOid }).Count -ne 1 -or
        $eku[0].EnhancedKeyUsages.Count -ne 1) {
        throw 'The Prod Alpha publisher certificate must have only the Code Signing EKU.'
    }
    if ($RequirePrivateKey -and -not $Certificate.HasPrivateKey) {
        throw 'The selected publisher certificate has no private key.'
    }
    $publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $publicKey) {
        throw 'The Prod Alpha publisher certificate must use RSA.'
    }
    $publicKey.Dispose()
}

function Assert-PegasusPublisherChain {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Publisher,
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Root,
        [Security.Cryptography.X509Certificates.X509Certificate2[]]$Intermediates = @(),
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow
    )

    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $null = $chain.ChainPolicy.CustomTrustStore.Add($Root)
        foreach ($intermediate in $Intermediates) {
            Assert-PegasusCertificateWindow -Certificate $intermediate -At $At
            $null = $chain.ChainPolicy.ExtraStore.Add($intermediate)
        }
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationTime = $At.UtcDateTime
        $null = $chain.ChainPolicy.ApplicationPolicy.Add(
            [Security.Cryptography.Oid]::new($script:CodeSigningOid))
        if (-not $chain.Build($Publisher)) {
            $details = (($chain.ChainStatus | ForEach-Object Status) -join ',')
            throw "The publisher certificate does not chain to the pinned Prod Alpha root: $details"
        }
        $last = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        if ((ConvertTo-PegasusThumbprint $last.Thumbprint) -cne
            (ConvertTo-PegasusThumbprint $Root.Thumbprint)) {
            throw 'The publisher certificate chain terminated at an unexpected root.'
        }
    }
    finally {
        $chain.Dispose()
    }
}

function Get-PegasusStoreCertificate {
    param(
        [Parameter(Mandatory)][string]$Thumbprint,
        [ValidateSet('CurrentUser', 'LocalMachine')][string]$StoreLocation = 'CurrentUser',
        [string]$Provider = '',
        [switch]$RequirePrivateKey
    )

    $expected = ConvertTo-PegasusThumbprint $Thumbprint
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        'My',
        [Security.Cryptography.X509Certificates.StoreLocation]::$StoreLocation)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = @($store.Certificates | Where-Object {
                (ConvertTo-PegasusThumbprint $_.Thumbprint) -ceq $expected
            })
        if ($matches.Count -ne 1) {
            throw 'The exact publisher certificate thumbprint is not uniquely present in the selected personal store.'
        }
        $certificate = $matches[0]
        Assert-PegasusPublisherCertificate -Certificate $certificate -ExpectedThumbprint $expected -RequirePrivateKey:$RequirePrivateKey
        if (-not [string]::IsNullOrWhiteSpace($Provider)) {
            if (-not $certificate.HasPrivateKey) {
                throw 'A provider assertion requires a private key.'
            }
            $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            try {
                if (-not ($rsa -is [Security.Cryptography.RSACng]) -or
                    $rsa.Key.Provider.Provider -cne $Provider) {
                    throw 'The selected publisher key provider does not match the exact provider assertion.'
                }
            }
            finally {
                if ($null -ne $rsa) { $rsa.Dispose() }
            }
        }
        return $certificate
    }
    finally {
        $store.Dispose()
    }
}

function Assert-PegasusNoPrivateKeyMaterial {
    param([Parameter(Mandatory)][string]$Root)

    $privateExtensions = @('.pfx', '.p12', '.p8', '.key', '.pem', '.pvk', '.jks', '.keystore')
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force -File -ErrorAction Stop) {
        if ($privateExtensions -ccontains $item.Extension.ToLowerInvariant() -or
            $item.BaseName -match '(?i)(?:^|[-_. ])(?:private[-_ ]?key|signing[-_ ]?secret|password|pin)(?:$|[-_. ])') {
            throw "Private-key-shaped package content is prohibited: '$($item.FullName)'."
        }
        if ($item.Length -le 1048576 -and $item.Extension -in @('.txt', '.json', '.xml', '.config', '.ps1', '.psm1', '.md', '.cer')) {
            $bytes = [IO.File]::ReadAllBytes($item.FullName)
            $text = [Text.Encoding]::ASCII.GetString($bytes)
            if ($text -match '-----BEGIN (?:ENCRYPTED )?PRIVATE KEY-----') {
                throw "Private key text is prohibited in a package: '$($item.FullName)'."
            }
        }
    }
}

function Read-PegasusTrustBundle {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow
    )

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $trustRoot = Join-Path $root 'trust'
    $manifestPath = Join-Path $trustRoot 'trust-bundle.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The package is missing trust/trust-bundle.json.'
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
            ConvertFrom-Json -Depth 16 -ErrorAction Stop
    }
    catch { throw 'The public trust-bundle manifest is malformed.' }
    if ([string]$manifest.schema -cne 'pegasus.public-trust-bundle/1.0' -or
        [string]$manifest.channel -cne $script:ProdAlphaChannel) {
        throw 'The public trust-bundle identity is invalid.'
    }
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $null = $declared.Add('trust-bundle.json')
    $certificates = [Collections.Generic.List[Security.Cryptography.X509Certificates.X509Certificate2]]::new()
    foreach ($entry in @($manifest.certificates)) {
        $name = [string]$entry.file
        if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.cer$' -or -not $declared.Add($name)) {
            throw 'The trust bundle contains an invalid or duplicate certificate filename.'
        }
        $path = Join-Path $trustRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The trust bundle is missing '$name'."
        }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne [string]$entry.sha256) {
            throw "The public certificate '$name' failed SHA-256 verification."
        }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.File]::ReadAllBytes($path))
        if ((ConvertTo-PegasusThumbprint $certificate.Thumbprint) -cne
            (ConvertTo-PegasusThumbprint ([string]$entry.thumbprint)) -or
            $certificate.Subject -cne [string]$entry.subject -or
            $certificate.HasPrivateKey) {
            throw "The public certificate '$name' does not match its manifest identity."
        }
        $certificates.Add($certificate)
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $trustRoot -Force -ErrorAction Stop) {
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The trust bundle must contain only ordinary public-certificate files and its manifest.'
        }
        $null = $actual.Add($item.Name)
    }
    if (-not $actual.SetEquals($declared)) {
        throw 'The public trust bundle contains missing or extra files.'
    }
    $roots = @($certificates | Where-Object { $_.Subject -ceq $script:RootSubject })
    $publishers = @($certificates | Where-Object { $_.Subject -ceq $script:PublisherSubject })
    if ($roots.Count -ne 1 -or $publishers.Count -ne 1) {
        throw 'The trust bundle must contain exactly one Prod Alpha root and publisher leaf.'
    }
    $rootCertificate = $roots[0]
    $publisher = $publishers[0]
    $intermediates = @($certificates | Where-Object {
            $_.Thumbprint -cne $rootCertificate.Thumbprint -and $_.Thumbprint -cne $publisher.Thumbprint
        })
    Assert-PegasusRootCertificate -Certificate $rootCertificate -ExpectedThumbprint ([string]$manifest.rootThumbprint) -At $At
    Assert-PegasusPublisherCertificate -Certificate $publisher -ExpectedThumbprint ([string]$manifest.publisherThumbprint) -At $At
    Assert-PegasusPublisherChain -Publisher $publisher -Root $rootCertificate -Intermediates $intermediates -At $At
    return [pscustomobject]@{
        Root = $rootCertificate
        Publisher = $publisher
        Intermediates = $intermediates
        Manifest = $manifest
        ManifestPath = $manifestPath
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Test-PegasusDetachedManifestSignature {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][ValidateSet('application', 'launcher', 'installer', 'product', 'product-installer')][string]$ExpectedRole,
        [Parameter(Mandatory)][string]$ExpectedReleaseVersion,
        [string]$ExpectedProductId = '',
        [DateTimeOffset]$At = [DateTimeOffset]::UtcNow
    )

    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    Assert-PegasusNoPrivateKeyMaterial -Root $root
    $trust = Read-PegasusTrustBundle -PackageRoot $root -At $At
    $manifestPath = Join-Path $root 'package-manifest.json'
    $signaturePath = Join-Path $root 'package-manifest.p7s'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw 'The package manifest or its detached signature is missing.'
    }
    $content = [IO.File]::ReadAllBytes($manifestPath)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new(
        [Security.Cryptography.Pkcs.ContentInfo]::new($content),
        $true)
    try {
        $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
        $cms.CheckSignature($true)
    }
    catch { throw 'The detached package-manifest signature is invalid or does not bind the exact manifest bytes.' }
    if ($cms.SignerInfos.Count -ne 1) {
        throw 'The package manifest must have exactly one detached signer.'
    }
    $signer = $cms.SignerInfos[0]
    if ((ConvertTo-PegasusThumbprint $signer.Certificate.Thumbprint) -cne
        (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint) -or
        $signer.DigestAlgorithm.Value -cne $script:Sha256Oid) {
        throw 'The detached package-manifest signer or digest algorithm is invalid.'
    }
    try {
        $manifest = [Text.UTF8Encoding]::new($false, $true).GetString($content) |
            ConvertFrom-Json -Depth 32 -ErrorAction Stop
    }
    catch { throw 'The signed package manifest is malformed.' }
    $signing = $manifest.signing
    $expectedSchema = if ($ExpectedProductId) { 'pegasus.windows-package/1' } else { 'pegasus.cortex.local-operational-package/1.2' }
    if ($ExpectedProductId -and ($ExpectedProductId -cnotmatch '^pegasus\.[a-z0-9][a-z0-9.-]{1,63}$' -or
        [string]$manifest.productId -cne $ExpectedProductId -or [string]$signing.signedProductId -cne $ExpectedProductId)) {
        throw 'The signed manifest does not bind the expected product identity.'
    }
    if ([string]$manifest.schema -cne $expectedSchema -or
        [string]$manifest.role -cne $ExpectedRole -or
        [string]$manifest.releaseVersion -cne $ExpectedReleaseVersion -or
        [string]$signing.schema -cne 'pegasus.package-signature/1.0' -or
        [string]$signing.channel -cne $script:ProdAlphaChannel -or
        [string]$signing.algorithm -cne 'CMS-SHA256-RSA' -or
        [string]$signing.detachedSignature -cne 'package-manifest.p7s' -or
        [string]$signing.signedRole -cne $ExpectedRole -or
        [string]$signing.signedReleaseVersion -cne $ExpectedReleaseVersion -or
        (ConvertTo-PegasusThumbprint ([string]$signing.publisherThumbprint)) -cne
            (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint) -or
        (ConvertTo-PegasusThumbprint ([string]$signing.rootThumbprint)) -cne
            (ConvertTo-PegasusThumbprint $trust.Root.Thumbprint) -or
        [string]$signing.trustBundleManifest -cne 'trust/trust-bundle.json' -or
        [string]$signing.trustBundleSha256 -cne $trust.ManifestSha256) {
        throw 'The signed manifest metadata does not bind the expected package, signer, root, and trust bundle.'
    }
    return [pscustomobject]@{ Manifest = $manifest; Trust = $trust; ManifestPath = $manifestPath; SignaturePath = $signaturePath }
}

function Test-PegasusAuthenticodeFiles {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Publisher
    )

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $pattern = if ([string]$Manifest.role -in @('installer', 'product')) {
        '(?i)\.(exe|dll|ps1|psm1|psd1)$'
    }
    else {
        '(?i)\.(exe|dll)$'
    }
    $peRecords = @($Manifest.files | Where-Object {
            [string]$_.path -match $pattern
        })
    if ($peRecords.Count -eq 0) { throw 'The package contains no signed PE payload.' }
    foreach ($record in $peRecords) {
        $path = Join-Path $root ([string]$record.path).Replace('/', '\')
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($null -eq $signature.SignerCertificate -or
            $signature.Status -in @(
                [Management.Automation.SignatureStatus]::NotSigned,
                [Management.Automation.SignatureStatus]::HashMismatch) -or
            (ConvertTo-PegasusThumbprint $signature.SignerCertificate.Thumbprint) -cne
                (ConvertTo-PegasusThumbprint $Publisher.Thumbprint)) {
            throw "Authenticode admission failed for '$([string]$record.path)'."
        }
    }
}

function Test-PegasusPackageContent {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)]$Manifest
    )

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $prefix = $root + '\'
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $null = $expected.Add('package-manifest.json')
    $null = $expected.Add('package-manifest.p7s')
    foreach ($record in @($Manifest.files)) {
        $relative = [string]$record.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
            $relative.StartsWith('/') -or $relative.Contains(':') -or
            $relative.Split('/') -contains '' -or $relative.Split('/') -contains '.' -or
            $relative.Split('/') -contains '..' -or -not $expected.Add($relative)) {
            throw 'The signed package manifest contains an unsafe or duplicate content path.'
        }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative.Replace('/', '\')))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Signed package content is missing or escaped its root: '$relative'."
        }
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Length -ne [long]$record.bytes -or
            [string]$record.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$record.sha256) {
            throw "Signed package content failed exact length or SHA-256 admission: '$relative'."
        }
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force -File) {
        $null = $actual.Add([IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/'))
    }
    if (-not $actual.SetEquals($expected)) { throw 'The signed package contains missing or extra files.' }
}

Export-ModuleMember -Function @(
    'ConvertTo-PegasusThumbprint',
    'Get-PegasusCertificateSha256',
    'Assert-PegasusCertificateWindow',
    'Assert-PegasusRootCertificate',
    'Assert-PegasusPublisherCertificate',
    'Assert-PegasusPublisherChain',
    'Get-PegasusStoreCertificate',
    'Assert-PegasusNoPrivateKeyMaterial',
    'Read-PegasusTrustBundle',
    'Test-PegasusDetachedManifestSignature',
    'Test-PegasusAuthenticodeFiles',
    'Test-PegasusPackageContent'
)
