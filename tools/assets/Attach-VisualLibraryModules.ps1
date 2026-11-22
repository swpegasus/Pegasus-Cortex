[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerRoot,
    [string]$PackageIndex = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\visual-library-packages.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false,$true)

function Assert-LocalPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path.Length -gt 240) { throw 'An optional module path is unsafe.' }
    for ($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Optional module reparse path rejected.' }
    }
}
function Get-Digest([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-ArchiveJson($Archive,[string]$Name,[int]$Maximum) {
    $entry=$Archive.GetEntry($Name)
    if ($null -eq $entry -or $entry.Length -gt $Maximum) { throw 'Bounded module descriptor absent or oversized.' }
    $input=$entry.Open();$reader=[IO.StreamReader]::new($input,$utf8,$false)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json -Depth 32) } finally { $reader.Dispose() }
}

Assert-LocalPath $InstallerRoot
Assert-LocalPath $PackageIndex
$root=[IO.Path]::GetFullPath($InstallerRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'The installer assembly directory must already exist.' }
if ((Get-Item -LiteralPath $PackageIndex).Length -gt 32768) { throw 'Input module index oversized.' }
$inputIndex=Get-Content -LiteralPath $PackageIndex -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 32
$packages=if ($inputIndex -is [array]) { $inputIndex } else { $inputIndex.modules }
$expected=@('openmoji','fluent-emoji','svgmoji','remix-icon')
if (@($packages).Count -ne 4 -or @($packages.library | Sort-Object -Unique).Count -ne 4) { throw 'Expected exactly four distinct public visual modules.' }
foreach ($package in $packages) { if ($package.library -notin $expected) { throw 'Unknown optional visual module.' } }

# Full read-only ZIP verification runs before creating any sidecar. This never
# extracts archives or opens/creates application/database state.
$null=& (Join-Path $PSScriptRoot 'Test-VisualLibraryPackages.ps1') -PackageIndex $PackageIndex
$destination=Join-Path $root 'optional-data'
Assert-LocalPath $destination
if (Test-Path -LiteralPath $destination) { throw 'Optional module destination must be absent in the new installer assembly.' }
$modules=@()
foreach ($name in $expected) {
    $package=@($packages | Where-Object library -eq $name)[0]
    $source=if ([IO.Path]::IsPathFullyQualified([string]$package.file)) { [string]$package.file } else { Join-Path (Split-Path -Parent $PackageIndex) $package.file }
    Assert-LocalPath $source
    $archive=[IO.Compression.ZipFile]::OpenRead($source)
    try {
        $acquisition=Read-ArchiveJson $archive 'acquisition.json' 16384
        $entry=$archive.GetEntry('assets.jsonl');$stream=$entry.Open();$reader=[IO.StreamReader]::new($stream,$utf8,$false)
        try {
            $header=$reader.ReadLine() | ConvertFrom-Json -Depth 32
            $licensing=@()
            while ($null -ne ($line=$reader.ReadLine())) {
                if ($utf8.GetByteCount($line) -gt 32768) { throw 'Module licensing line oversized.' }
                $row=$line | ConvertFrom-Json -Depth 32
                if ($null -ne $row.licenseId) { break }
                if ($licensing.Count -ge 32 -or $row.metadata.mediaType -ne 'text/plain') { throw 'Module licensing inventory invalid.' }
                $licensing+= [ordered]@{ file=$row.file; id=$row.metadata.id; sha256=$row.sha256; license=$row.metadata.license; source=$row.metadata.source }
            }
        } finally { $reader.Dispose() }
        if ($acquisition.source -ne $name -or $header.pack -ne ('visual.'+$name) -or
            $header.version -ne $acquisition.revision -or $header.namespace -ne 'knowledge.shared' -or
            $header.contract -ne 'pegasus.visual-library/1' -or $licensing.Count -ne $acquisition.licensingFiles -or
            $acquisition.manifestSha256 -ne $package.manifestSha256 -or
            $package.files -ne ($acquisition.artworkCount+$acquisition.licensingFiles+2)) { throw 'Optional module descriptors differ.' }
        $modules+=[ordered]@{
            library=$name; moduleId=$header.pack; kind='data'; moduleVersion=$header.version;
            required=$false; defaultEnabled=$false; namespace='knowledge.shared'; requires=@('assets.files/2');
            file=($name+'.zip'); sha256=$package.sha256; manifestSha256=$package.manifestSha256;
            files=$package.files; bytes=$package.bytes; uncompressedBytes=$package.uncompressedBytes;
            artworkCount=$acquisition.artworkCount; artworkBytes=$acquisition.artworkBytes; licensing=$licensing
        }
    } finally { $archive.Dispose() }
}
$portable=[ordered]@{
    contract='pegasus.visual-library-modules/1'; version=1; dataOnly=$true;
    activation='explicit-selected-module-only'; databaseInteraction='none';
    importContract='PegasusCortex.exe assets import-visual-library --manifest <absolute assets.jsonl> --sha256 <manifestSha256>';
    licensing='all listed files are included in each complete ZIP; identifiers and digest pins are passive metadata, not runtime URIs or an artwork-export service';
    modules=$modules
}
$json=$portable | ConvertTo-Json -Depth 12
if ($utf8.GetByteCount($json) -gt 32768) { throw 'Portable module index exceeds 32 KiB.' }
New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
foreach ($module in $modules) {
    $package=@($packages | Where-Object library -eq $module.library)[0]
    $source=if ([IO.Path]::IsPathFullyQualified([string]$package.file)) { [string]$package.file } else { Join-Path (Split-Path -Parent $PackageIndex) $package.file }
    $target=Join-Path $destination $module.file;Assert-LocalPath $target
    Copy-Item -LiteralPath $source -Destination $target -ErrorAction Stop
    if ((Get-Item -LiteralPath $target).Length -ne $module.bytes -or (Get-Digest $target) -ne $module.sha256) { throw 'Copied optional module differs from its admitted source.' }
}
$index=Join-Path $destination 'visual-library-modules.v1.json'
[IO.File]::WriteAllText($index,$json+"`n",$utf8)
[pscustomobject]@{ contract='pegasus.visual-library-modules/1'; directory=$destination; modules=4;
    indexSha256=(Get-Digest $index); compressedBytes=($modules.bytes | Measure-Object -Sum).Sum;
    automaticExtraction=$false; liveAdmission=$false; databaseInteraction=$false }
