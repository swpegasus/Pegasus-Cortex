[CmdletBinding()]
param(
    [string]$ReleaseVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-FailsWith {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Fragment)
    try { & $Action; throw 'Expected operation succeeded.' }
    catch {
        if ($_.Exception.Message -ceq 'Expected operation succeeded.' -or
            -not $_.Exception.Message.Contains($Fragment, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected failure containing '$Fragment', received: $($_.Exception.Message)"
        }
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $ReleaseVersion = [string](Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION.json') -Raw |
        ConvertFrom-Json -Depth 10 -ErrorAction Stop).releaseVersion
}
$releaseRoot = Join-Path $repositoryRoot 'artifacts\release'
$verifier = Join-Path $repositoryRoot 'tools\signing\Test-PegasusSignedPackage.ps1'
$trustTool = Join-Path $repositoryRoot 'tools\signing\Invoke-PegasusProdAlphaTrust.ps1'
$trustModule = Join-Path $repositoryRoot 'tools\signing\PegasusTrust.psm1'
$signingModule = Join-Path $repositoryRoot 'tools\signing\PegasusSigning.psm1'
Import-Module $trustModule -Force
Import-Module $signingModule -Force

$packages = @(
    [pscustomobject]@{ Directory = 'unified'; Role = 'product' },
    [pscustomobject]@{ Directory = 'installer'; Role = 'product-installer' })
$results = foreach ($package in $packages) {
    & $verifier -PackageRoot (Join-Path $releaseRoot $package.Directory) `
        -Role $package.Role -ReleaseVersion $ReleaseVersion
}
Assert-True -Condition (@($results).Count -eq 2) -Message 'The unified product and installer did not both return signing evidence.'
Assert-True -Condition (@($results | Where-Object { -not $_.Valid }).Count -eq 0) -Message 'A signed package role was invalid.'
Assert-True -Condition (@($results | Where-Object { $_.Channel -cne 'Prod Alpha' }).Count -eq 0) -Message 'A package did not retain the Prod Alpha channel.'
$publisherThumbprints = @($results | ForEach-Object PublisherThumbprint | Sort-Object -Unique)
$rootThumbprints = @($results | ForEach-Object RootThumbprint | Sort-Object -Unique)
Assert-True -Condition ($publisherThumbprints.Count -eq 1 -and $rootThumbprints.Count -eq 1) -Message 'Package roles do not share one pinned signing identity.'

$beforeRoot = @(Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -ceq $rootThumbprints[0]).Count
$beforePublisher = @(Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object Thumbprint -ceq $publisherThumbprints[0]).Count
$plan = & $trustTool -Action Install -PackageRoot (Join-Path $releaseRoot 'unified') -ComponentId 'pegasus.cortex' -PlanOnly
$afterRoot = @(Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -ceq $rootThumbprints[0]).Count
$afterPublisher = @(Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object Thumbprint -ceq $publisherThumbprints[0]).Count
Assert-True -Condition ($plan.PlanOnly -and $plan.RequiresElevation) -Message 'Trust PlanOnly did not remain explicit and elevated.'
Assert-True -Condition ($beforeRoot -eq $afterRoot -and $beforePublisher -eq $afterPublisher) -Message 'Trust PlanOnly mutated a machine certificate store.'

$first = Get-PegasusTrustTransition -Action Install -ComponentId 'pegasus.cortex' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State $null -RootPresent $false -PublisherPresent $false
Assert-True -Condition ($first.AddRoot -and $first.AddPublisher -and $first.NextState.components.Count -eq 1) -Message 'Initial trust ownership transition is invalid.'
$second = Get-PegasusTrustTransition -Action Install -ComponentId 'pegasus.exchange' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State ([pscustomobject]$first.NextState) -RootPresent $true -PublisherPresent $true
Assert-True -Condition (-not $second.AddRoot -and -not $second.AddPublisher -and $second.NextState.components.Count -eq 2) -Message 'Shared trust installation is not idempotent.'
$removeFirst = Get-PegasusTrustTransition -Action Uninstall -ComponentId 'pegasus.cortex' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State ([pscustomobject]$second.NextState) -RootPresent $true -PublisherPresent $true
Assert-True -Condition (-not $removeFirst.RemoveRoot -and -not $removeFirst.RemovePublisher -and $removeFirst.NextState.components.Count -eq 1) -Message 'Trust was removed while another component still referenced it.'
$removeLast = Get-PegasusTrustTransition -Action Uninstall -ComponentId 'pegasus.exchange' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State ([pscustomobject]$removeFirst.NextState) -RootPresent $true -PublisherPresent $true
Assert-True -Condition ($removeLast.RemoveRoot -and $removeLast.RemovePublisher -and $null -eq $removeLast.NextState) -Message 'Last-owner trust removal is invalid.'
$preexisting = Get-PegasusTrustTransition -Action Install -ComponentId 'pegasus.cortex' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State $null -RootPresent $true -PublisherPresent $true
$preserve = Get-PegasusTrustTransition -Action Uninstall -ComponentId 'pegasus.cortex' `
    -RootThumbprint $rootThumbprints[0] -PublisherThumbprint $publisherThumbprints[0] `
    -State ([pscustomobject]$preexisting.NextState) -RootPresent $true -PublisherPresent $true
Assert-True -Condition (-not $preserve.RemoveRoot -and -not $preserve.RemovePublisher) -Message 'Preexisting machine trust was not preserved.'

$scratch = Join-Path ([IO.Path]::GetTempPath()) "cx-signing-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $scratch -ErrorAction Stop | Out-Null
    $productSource = Join-Path $releaseRoot 'unified'
    $installerSource = Join-Path $releaseRoot 'installer'

    $privateFixture = Join-Path $scratch 'private-content'
    Copy-Item -LiteralPath $installerSource -Destination $privateFixture -Recurse -ErrorAction Stop
    [IO.File]::WriteAllBytes((Join-Path $privateFixture 'operator-private-key.pfx'), [byte[]](1, 2, 3))
    Assert-FailsWith -Fragment 'Private-key-shaped' -Action {
        & $verifier -PackageRoot $privateFixture -Role product-installer -ReleaseVersion $ReleaseVersion | Out-Null
    }

    $missingSignature = Join-Path $scratch 'missing-signature'
    Copy-Item -LiteralPath $productSource -Destination $missingSignature -Recurse -ErrorAction Stop
    Remove-Item -LiteralPath (Join-Path $missingSignature 'package-manifest.p7s') -Force
    Assert-FailsWith -Fragment 'signature is missing' -Action {
        & $verifier -PackageRoot $missingSignature -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }

    $manifestTamper = Join-Path $scratch 'manifest-tamper'
    Copy-Item -LiteralPath $productSource -Destination $manifestTamper -Recurse -ErrorAction Stop
    [IO.File]::AppendAllText((Join-Path $manifestTamper 'package-manifest.json'), " ", [Text.UTF8Encoding]::new($false))
    Assert-FailsWith -Fragment 'detached package-manifest signature' -Action {
        & $verifier -PackageRoot $manifestTamper -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }

    $contentTamper = Join-Path $scratch 'content-tamper'
    Copy-Item -LiteralPath $productSource -Destination $contentTamper -Recurse -ErrorAction Stop
    [IO.File]::AppendAllText((Join-Path $contentTamper 'PegasusCortex.pdb'), 'tamper', [Text.UTF8Encoding]::new($false))
    Assert-FailsWith -Fragment 'exact length or SHA-256' -Action {
        & $verifier -PackageRoot $contentTamper -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }

    $resigned = Join-Path $scratch 'resigned-content'
    Copy-Item -LiteralPath $productSource -Destination $resigned -Recurse -ErrorAction Stop
    $publisherPrivate = Get-PegasusStoreCertificate -Thumbprint $publisherThumbprints[0] -RequirePrivateKey
    $resignedExecutable = Join-Path $resigned 'PegasusCortex.exe'
    [IO.File]::AppendAllText($resignedExecutable, 'changed-before-resigning', [Text.UTF8Encoding]::new($false))
    $null = Set-AuthenticodeSignature -LiteralPath $resignedExecutable `
        -Certificate $publisherPrivate -HashAlgorithm SHA256 -ErrorAction Stop
    Assert-FailsWith -Fragment 'exact length or SHA-256' -Action {
        & $verifier -PackageRoot $resigned -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }

    $missingTrust = Join-Path $scratch 'missing-trust'
    Copy-Item -LiteralPath $productSource -Destination $missingTrust -Recurse -ErrorAction Stop
    Remove-Item -LiteralPath (Join-Path $missingTrust 'trust\pegasus-prod-alpha-publisher.cer') -Force
    Assert-FailsWith -Fragment 'missing' -Action {
        & $verifier -PackageRoot $missingTrust -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }
    $extraTrust = Join-Path $scratch 'extra-trust'
    Copy-Item -LiteralPath $productSource -Destination $extraTrust -Recurse -ErrorAction Stop
    [IO.File]::WriteAllText((Join-Path $extraTrust 'trust\unexpected.txt'), 'public?', [Text.UTF8Encoding]::new($false))
    Assert-FailsWith -Fragment 'missing or extra' -Action {
        & $verifier -PackageRoot $extraTrust -Role product -ReleaseVersion $ReleaseVersion | Out-Null
    }

    Assert-FailsWith -Fragment 'signed manifest metadata' -Action {
        & $verifier -PackageRoot $productSource -Role product-installer -ReleaseVersion $ReleaseVersion | Out-Null
    }
    Assert-FailsWith -Fragment 'signed manifest metadata' -Action {
        & $verifier -PackageRoot $productSource -Role product -ReleaseVersion '1.2099.01.01.00.00.A' | Out-Null
    }

    $productVerified = Test-PegasusDetachedManifestSignature `
        -PackageRoot $productSource -ExpectedRole product -ExpectedReleaseVersion $ReleaseVersion
    $unsignedRoot = Join-Path $scratch 'unsigned-content'
    New-Item -ItemType Directory -Path $unsignedRoot -ErrorAction Stop | Out-Null
    [IO.File]::WriteAllText((Join-Path $unsignedRoot 'unsigned.dll'), 'not a signed PE', [Text.UTF8Encoding]::new($false))
    $unsignedManifest = [pscustomobject]@{
        role = 'application'
        files = @([pscustomobject]@{ path = 'unsigned.dll' })
    }
    Assert-FailsWith -Fragment 'Authenticode admission failed' -Action {
        Test-PegasusAuthenticodeFiles -PackageRoot $unsignedRoot `
            -Manifest $unsignedManifest -Publisher $productVerified.Trust.Publisher
    }
    Assert-FailsWith -Fragment 'Authenticode admission failed' -Action {
        Test-PegasusAuthenticodeFiles -PackageRoot $productSource `
            -Manifest $productVerified.Manifest -Publisher $productVerified.Trust.Root
    }
    Assert-FailsWith -Fragment 'not yet valid' -Action {
        Assert-PegasusPublisherCertificate -Certificate $productVerified.Trust.Publisher `
            -At ([DateTimeOffset]$productVerified.Trust.Publisher.NotBefore).AddSeconds(-1)
    }
    Assert-FailsWith -Fragment 'expired' -Action {
        Assert-PegasusPublisherCertificate -Certificate $productVerified.Trust.Publisher `
            -At ([DateTimeOffset]$productVerified.Trust.Publisher.NotAfter).AddSeconds(1)
    }

    $wrongRootKey = [Security.Cryptography.RSA]::Create(2048)
    $wrongEkuKey = [Security.Cryptography.RSA]::Create(2048)
    $wrongRoot = $null
    $wrongEku = $null
    try {
        $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Pegasus Local Development Root CA', $wrongRootKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $null = $rootRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 1, $true))
        $null = $rootRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,
                $true))
        $wrongRoot = $rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
        Assert-FailsWith -Fragment 'does not chain' -Action {
            Assert-PegasusPublisherChain -Publisher $productVerified.Trust.Publisher -Root $wrongRoot
        }

        $ekuRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Pegasus Software Signing Prod Alpha', $wrongEkuKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $null = $ekuRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $null = $ekuRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $oids = [Security.Cryptography.OidCollection]::new()
        $null = $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $null = $ekuRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
        $wrongEku = $ekuRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
        Assert-FailsWith -Fragment 'Code Signing EKU' -Action {
            Assert-PegasusPublisherCertificate -Certificate $wrongEku
        }
    }
    finally {
        if ($null -ne $wrongEku) { $wrongEku.Dispose() }
        if ($null -ne $wrongRoot) { $wrongRoot.Dispose() }
        $wrongEkuKey.Dispose()
        $wrongRootKey.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $item = Get-Item -LiteralPath $scratch -Force
        $parent = [IO.Path]::GetFullPath((Split-Path -Parent $item.FullName)).TrimEnd('\', '/')
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if (-not $item.PSIsContainer -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
            -not [string]::Equals($parent, $expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
            -not $item.Name.StartsWith('cx-signing-', [StringComparison]::Ordinal)) {
            throw "Refusing to clean unexpected signing-test path '$scratch'."
        }
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction Stop
    }
}

Write-Output 'PASS unified Prod Alpha product and installer share one verified signing identity'
Write-Output 'PASS trust PlanOnly is non-mutating and elevation-explicit'
Write-Output 'PASS trust ownership is idempotent, reference-counted, and preserves preexisting trust'
Write-Output 'PASS packages containing private signing material fail closed'
Write-Output 'PASS unsigned, tampered, re-signed, role, version, and trust-file package variants fail closed'
Write-Output 'PASS wrong signer, untrusted root, invalid validity window, and wrong EKU fail closed'
