[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$ProductId,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [ValidateSet('product', 'product-installer')][string]$Role = 'product',
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [Parameter(Mandatory)][string]$RootThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$signing = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\signing'))
Import-Module (Join-Path $signing 'PegasusSigning.psm1') -Force
if ($ProductId -cnotmatch '^pegasus\.[a-z0-9][a-z0-9.-]{1,63}$' -or
    $ReleaseVersion -cnotmatch '^[0-9][A-Za-z0-9.-]{0,63}$') { throw 'Invalid product or release identity.' }
$package = (Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).Path
$manifestPath = Join-Path $package 'package-manifest.json'
if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath (Join-Path $package 'package-manifest.p7s'))) {
    throw 'Remove the previous generated manifest/signature when replacing a build; never overwrite signed evidence in place.'
}
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($package)
$files = [Collections.Generic.List[object]]::new()
while ($pending.Count) {
    $directory = $pending.Pop()
    if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a reparse point.' }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a reparse point.' }
        if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
        $relative = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\', '/')
        if ($relative.StartsWith('state/', [StringComparison]::OrdinalIgnoreCase)) { throw 'Mutable state is not package content.' }
        $files.Add([ordered]@{ path=$relative; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
}
Assert-PegasusNoPrivateKeyMaterial -Root $package
$trust = Read-PegasusTrustBundle -PackageRoot $package
Assert-PegasusRootCertificate -Certificate $trust.Root -ExpectedThumbprint $RootThumbprint
Assert-PegasusPublisherCertificate -Certificate $trust.Publisher -ExpectedThumbprint $PublisherThumbprint
$manifest = [pscustomobject][ordered]@{
    schema='pegasus.windows-package/1'; productId=$ProductId; role=$Role; releaseVersion=$ReleaseVersion
    files=@($files | Sort-Object { $_.path } -CaseSensitive)
    signing=[ordered]@{
        schema='pegasus.package-signature/1.0'; channel='Prod Alpha'; algorithm='CMS-SHA256-RSA'
        detachedSignature='package-manifest.p7s'; signedProductId=$ProductId; signedRole=$Role; signedReleaseVersion=$ReleaseVersion
        publisherThumbprint=(ConvertTo-PegasusThumbprint $PublisherThumbprint); rootThumbprint=(ConvertTo-PegasusThumbprint $RootThumbprint)
        trustBundleManifest='trust/trust-bundle.json'; trustBundleSha256=$trust.ManifestSha256
    }
}
Test-PegasusAuthenticodeFiles -PackageRoot $package -Manifest $manifest -Publisher $trust.Publisher
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
& (Join-Path $signing 'Protect-PegasusPackageManifest.ps1') -PackageRoot $package -PublisherThumbprint $PublisherThumbprint -Provider $Provider | Out-Null
& (Join-Path $signing 'Test-PegasusSignedPackage.ps1') -PackageRoot $package -Role $Role -ReleaseVersion $ReleaseVersion -ProductId $ProductId
