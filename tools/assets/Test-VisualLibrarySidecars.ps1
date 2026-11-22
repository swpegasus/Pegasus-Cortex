[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=[Text.UTF8Encoding]::new($false,$true)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('cortex-visual-sidecar-'+[Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw 'Fixture destination collision.' }
New-Item -ItemType Directory -Path $fixture -ErrorAction Stop | Out-Null
function Hash-Bytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Add-Entry($Archive,[string]$Name,[byte[]]$Bytes) {
    $entry=$Archive.CreateEntry($Name);$stream=$entry.Open()
    try {$stream.Write($Bytes)} finally {$stream.Dispose()}
}
$packages=@();$version='0123456789012345678901234567890123456789'
foreach ($name in @('openmoji','fluent-emoji','svgmoji','remix-icon')) {
    $license=$utf8.GetBytes("MIT License`nFixture only.`n")
    $art=$utf8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20"/>')
    $path=('files/'+(Hash-Bytes $art)+'.svg')
    $spec=[ordered]@{namespace='knowledge.shared';id=('visual.'+$name+'.license');title='Fixture license';meaning='Fixture';mediaType='text/plain';license='MIT';source='https://example.com/fixture';pack=('visual.'+$name);packVersion=$version}
    $image=[ordered]@{};foreach ($key in $spec.Keys) {$image[$key]=$spec[$key]};$image.id=('visual.'+$name+'.image');$image.mediaType='image/svg+xml'
    $manifest=@(
        ([ordered]@{contract='pegasus.visual-library/1';namespace='knowledge.shared';pack=('visual.'+$name);version=$version;upstream='https://example.com/fixture';revision=$version;license='MIT'}|ConvertTo-Json -Compress),
        ([ordered]@{file='Licensing/LICENSE.txt';sha256=(Hash-Bytes $license);metadata=$spec;licenseId=$null}|ConvertTo-Json -Compress -Depth 6),
        ([ordered]@{file=$path;sha256=(Hash-Bytes $art);metadata=$image;licenseId=$spec.id}|ConvertTo-Json -Compress -Depth 6)
    ) -join "`n"
    $manifestBytes=$utf8.GetBytes($manifest+"`n");$manifestSha=Hash-Bytes $manifestBytes
    $acquisition=$utf8.GetBytes(([ordered]@{source=$name;revision=$version;manifestSha256=$manifestSha;artworkCount=1;artworkBytes=$art.Length;licensingFiles=1}|ConvertTo-Json -Compress))
    $destination=Join-Path $fixture ($name+'.zip')
    $archive=[IO.Compression.ZipFile]::Open($destination,[IO.Compression.ZipArchiveMode]::Create)
    try {Add-Entry $archive 'assets.jsonl' $manifestBytes;Add-Entry $archive 'acquisition.json' $acquisition;Add-Entry $archive 'Licensing/LICENSE.txt' $license;Add-Entry $archive $path $art} finally {$archive.Dispose()}
    $packages+=[ordered]@{contract='pegasus.visual-library-package/1';library=$name;file=$destination;sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant();manifestSha256=$manifestSha;files=4;bytes=(Get-Item -LiteralPath $destination).Length;uncompressedBytes=($manifestBytes.Length+$acquisition.Length+$license.Length+$art.Length);dataOnly=$true}
}
$inputIndex=Join-Path $fixture 'local.json';[IO.File]::WriteAllText($inputIndex,($packages|ConvertTo-Json -Depth 6),$utf8)
$installer=Join-Path $fixture 'installer';New-Item -ItemType Directory -Path $installer -ErrorAction Stop | Out-Null
$receipt=& (Join-Path $PSScriptRoot 'Attach-VisualLibraryModules.ps1') -InstallerRoot $installer -PackageIndex $inputIndex
$portablePath=Join-Path $installer 'optional-data\visual-library-modules.v1.json'
$portable=Get-Content -LiteralPath $portablePath -Raw | ConvertFrom-Json -Depth 16
if ($receipt.modules -ne 4 -or $receipt.automaticExtraction -or $receipt.liveAdmission -or $receipt.databaseInteraction -or
    (Get-Content -LiteralPath $portablePath -Raw).Contains($fixture) -or @(Get-ChildItem -LiteralPath $installer -Recurse -File).Count -ne 5) { throw 'Passive portable sidecar fixture failed.' }
$null=& (Join-Path $PSScriptRoot 'Test-VisualLibraryPackages.ps1') -PackageIndex $portablePath
$rejected=0
try { $null=& (Join-Path $PSScriptRoot 'Attach-VisualLibraryModules.ps1') -InstallerRoot $installer -PackageIndex $inputIndex } catch {$rejected++}
$portable.modules[0].file='../openmoji.zip'
[IO.File]::WriteAllText($portablePath,($portable|ConvertTo-Json -Depth 16),$utf8)
try {$null=& (Join-Path $PSScriptRoot 'Test-VisualLibraryPackages.ps1') -PackageIndex $portablePath} catch {$rejected++}
$portable.modules[0].file='openmoji.zip';$portable.modules[0].licensing[0].sha256=('0'*64)
[IO.File]::WriteAllText($portablePath,($portable|ConvertTo-Json -Depth 16),$utf8)
try {$null=& (Join-Path $PSScriptRoot 'Test-VisualLibraryPackages.ps1') -PackageIndex $portablePath} catch {$rejected++}
if ($rejected -ne 3) { throw 'Expected path/licensing/collision rejection did not occur.' }
[pscustomobject]@{ok=$true;evidence='isolated data-only ZIP fixtures';checks=5;modules=4;fixture=$fixture;liveAdmission=$false;databaseInteraction=$false}
