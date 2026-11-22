[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider',
    [switch]$IncludePowerShell,
    [switch]$PreserveValidPublisherSignatures,
    [hashtable]$PreservedFileHashes=@{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force

$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
Assert-PegasusNoPrivateKeyMaterial -Root $root
$trust = Read-PegasusTrustBundle -PackageRoot $root
$publisher = Get-PegasusStoreCertificate `
    -Thumbprint $PublisherThumbprint `
    -StoreLocation CurrentUser `
    -Provider $Provider `
    -RequirePrivateKey
if ((ConvertTo-PegasusThumbprint $publisher.Thumbprint) -cne
    (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint)) {
    throw 'The selected private publisher key does not match the package public trust bundle.'
}

$extensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @('.exe', '.dll')) { $null = $extensions.Add($extension) }
if ($IncludePowerShell) {
    foreach ($extension in @('.ps1', '.psm1', '.psd1')) { $null = $extensions.Add($extension) }
}
$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File -ErrorAction Stop |
        Where-Object { $extensions.Contains($_.Extension) } |
        Sort-Object FullName)
if ($files.Count -eq 0) { throw 'The artifact tree contains no signable files.' }
function Assert-PegasusPreservedSignature {
    param([string]$Path,[string]$ExpectedSha256,[string]$SelectedPublisher)
    if($ExpectedSha256-cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()-cne$ExpectedSha256){throw 'A preserved artifact changed.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    if($null-eq$signature.SignerCertificate -or (ConvertTo-PegasusThumbprint $signature.SignerCertificate.Thumbprint)-cne(ConvertTo-PegasusThumbprint $SelectedPublisher) -or $signature.Status-in@([Management.Automation.SignatureStatus]::NotSigned,[Management.Automation.SignatureStatus]::HashMismatch)){throw 'A preserved artifact is not signed by the selected publisher.'}
}
$signedCount=0;$preservedCount=0
foreach ($file in $files) {
    $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if($PreservedFileHashes.ContainsKey($relative))
    {
        $expected=[string]$PreservedFileHashes[$relative]
        if(-not $PreserveValidPublisherSignatures){throw 'Preserved hash input requires explicit preservation mode.'}
        Assert-PegasusPreservedSignature -Path $file.FullName -ExpectedSha256 $expected -SelectedPublisher $publisher.Thumbprint
        $preservedCount++
        continue
    }
    if($PreserveValidPublisherSignatures)
    {
        $existing=Get-AuthenticodeSignature -LiteralPath $file.FullName -ErrorAction Stop
        if($existing.Status -eq [Management.Automation.SignatureStatus]::Valid -and
           $null -ne $existing.SignerCertificate -and
           (ConvertTo-PegasusThumbprint $existing.SignerCertificate.Thumbprint) -ceq
           (ConvertTo-PegasusThumbprint $publisher.Thumbprint)){$preservedCount++;continue}
    }
    $result = $null
    $maximumAttempts=if($PreserveValidPublisherSignatures){1}else{50}
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        try {
            $result = Set-AuthenticodeSignature `
                -LiteralPath $file.FullName `
                -Certificate $publisher `
                -HashAlgorithm SHA256 `
                -ErrorAction Stop
            break
        }
        catch [IO.IOException] {
            if ($attempt -eq $maximumAttempts) { throw }
            [Threading.Thread]::Sleep(100)
        }
    }
    if ($null -eq $result.SignerCertificate -or
        (ConvertTo-PegasusThumbprint $result.SignerCertificate.Thumbprint) -cne
            (ConvertTo-PegasusThumbprint $publisher.Thumbprint) -or
        $result.Status -in @(
            [Management.Automation.SignatureStatus]::NotSigned,
            [Management.Automation.SignatureStatus]::HashMismatch)) {
        throw "Authenticode signing failed for '$($file.FullName)'."
    }
    $signedCount++
}

[pscustomobject]@{
    PackageRoot = $root
    PublisherThumbprint = (ConvertTo-PegasusThumbprint $publisher.Thumbprint)
    Provider = $Provider
    SignedFiles = $signedCount
    PreservedFiles = $preservedCount
    Timestamped = $false
}
