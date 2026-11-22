[CmdletBinding()]
param([switch]$Replace)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Drawing
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assets = Join-Path $repository 'src/Pegasus.AgentOps.Tray/Assets'
$output = Join-Path $repository 'artifacts/branding'
$kit = Join-Path $output 'CortexBrandingKit'
$archive = Join-Path $output 'CortexBrandingKit.zip'
$checksum = $archive + '.sha256'
$encoding = [Text.UTF8Encoding]::new($false)

function Assert-OrdinaryPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved.Length -gt 240) { throw 'A branding path exceeds the supported length.' }
    for ($cursor = $resolved; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) -and
            (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Branding inputs and outputs must not traverse reparse points.'
        }
    }
    return $resolved
}

function Publish-Bytes([string]$Path, [byte[]]$Bytes) {
    $target = Assert-OrdinaryPath $Path
    if ([IO.Directory]::Exists($target)) { throw 'A file output is occupied by a directory.' }
    if ([IO.File]::Exists($target) -and -not $Replace) { throw 'A branding output already exists; choose -Replace explicitly.' }
    $pending = Assert-OrdinaryPath ($target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [IO.FileStream]::new($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $null = Assert-OrdinaryPath $target
        if ([IO.File]::Exists($target)) {
            if (-not $Replace) { throw 'A branding output appeared during export; no file was replaced.' }
            [IO.File]::Replace($pending, $target, [NullString]::Value, $false)
        } else { [IO.File]::Move($pending, $target) }
    } finally {
        if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) }
    }
}

function Json-Bytes($Value) { return ,$encoding.GetBytes(($Value | ConvertTo-Json -Depth 12) + "`n") }
function Digest([byte[]]$Bytes) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }

$null = Assert-OrdinaryPath $repository
$null = Assert-OrdinaryPath $assets
$null = Assert-OrdinaryPath $output
$null = Assert-OrdinaryPath $kit
if ([IO.File]::Exists($output) -or [IO.File]::Exists($kit)) { throw 'A branding directory path is occupied by a file.' }
$artwork = Import-PowerShellDataFile (Join-Path $assets 'CortexRoleArtwork.psd1')
if ($artwork.SchemaVersion -ne 1 -or $artwork.Roles.Count -ne 11) { throw 'Unexpected Cortex role artwork schema or family size.' }
$roles = @($artwork.Roles.Keys | Sort-Object -CaseSensitive)
if (@($roles | Where-Object { $_ -cnotmatch '^[A-Z][A-Za-z]+$' }).Count) { throw 'Invalid Cortex role filename.' }

$sources = [ordered]@{}
foreach ($name in @('Cortex.Graphic.png', 'Cortex.Watermark.png', 'Pegasus.AgentOps.png', 'Pegasus.AgentOps.ico',
    'CortexAppMark.svg', 'Cortex.IconFamily.preview.png', 'CortexRoleArtwork.psd1', 'New-CortexAppMark.ps1', 'README.md')) {
    $sources[$name] = Join-Path $assets $name
}
foreach ($role in $roles) {
    foreach ($extension in @('png', 'ico', 'svg')) { $sources["Cortex.$role.$extension"] = Join-Path $assets "Cortex.$role.$extension" }
}
$sources['New-CortexBrandingKit.ps1'] = $PSCommandPath
$sources['LICENSE'] = Join-Path $repository 'LICENSE'
$metadata = @('Cortex.BrandingPalette.json', 'Cortex.BrandingProvenance.json', 'CortexBrandingKit.inventory.json')
$expected = @($sources.Keys) + $metadata
if ([IO.Directory]::Exists($kit)) {
    if (-not $Replace) { throw 'The branding kit already exists; choose -Replace explicitly.' }
    foreach ($entry in Get-ChildItem -LiteralPath $kit -Force) {
        $null = Assert-OrdinaryPath $entry.FullName
        if ($entry.PSIsContainer -or $entry.Name -cnotin $expected) { throw 'The branding kit contains an unexpected entry; preserve and review it.' }
    }
}
foreach ($path in @($archive, $checksum)) {
    $null = Assert-OrdinaryPath $path
    if ([IO.Directory]::Exists($path) -or ([IO.File]::Exists($path) -and -not $Replace)) {
        throw 'A branding archive/checksum output exists or has the wrong kind; choose -Replace only for an existing file.'
    }
}

# Admission happens before publication. No source contents, absolute paths or
# machine identity are copied into the provenance metadata.
$content = [ordered]@{}
$totalSourceBytes = 0L
foreach ($name in @($sources.Keys | Sort-Object -CaseSensitive)) {
    $source = Assert-OrdinaryPath $sources[$name]
    if (-not [IO.File]::Exists($source)) { throw "Required branding input is missing: $name" }
    if (([IO.FileInfo]::new($source)).Length -gt 16777216) { throw 'A branding input exceeds the bounded asset size.' }
    $bytes = [IO.File]::ReadAllBytes($source)
    if ($bytes.Length -gt 16777216) { throw 'A branding input exceeds the bounded asset size.' }
    $totalSourceBytes += $bytes.Length
    if ($totalSourceBytes -gt 67108864) { throw 'The branding source set exceeds the 64 MiB export bound.' }
    $content[$name] = $bytes
}
$imageFacts = foreach ($name in @('Cortex.Graphic.png', 'Cortex.Watermark.png')) {
    $stream = [IO.MemoryStream]::new([byte[]]$content[$name], $false)
    $bitmap = $null
    try {
        $bitmap = [Drawing.Bitmap]::new($stream)
        if ($bitmap.Width -ne $bitmap.Height) { throw 'Branding masters must be square.' }
        [ordered]@{ file = $name; width = $bitmap.Width; height = $bitmap.Height; cornerAlpha = $bitmap.GetPixel(0, 0).A }
    } finally { if ($null -ne $bitmap) { $bitmap.Dispose() }; $stream.Dispose() }
}
if ($imageFacts[0].cornerAlpha -ne 255 -or $imageFacts[1].cornerAlpha -ne 0) {
    throw 'Use the full-contrast opaque icon master and separate transparent GUI watermark.'
}
$roleFacts = foreach ($role in $roles) {
    [ordered]@{ role = $role; label = $artwork.Roles[$role].Label; projects = @($artwork.Roles[$role].Projects); icon = "Cortex.$role.ico" }
}
$content['Cortex.BrandingPalette.json'] = Json-Bytes ([ordered]@{
    schema = 'pegasus.cortex.branding-palette/1'
    roleBadge = [ordered]@{ paper = '#F8FAFF'; ink = '#0B234A'; border = '#245BCE' }
    suiteAccents = @('#245BCE', '#A6E5FF')
    description = 'Badge and suite accent colors; the full-color raster master supplies the artwork colors.'
})
$content['Cortex.BrandingProvenance.json'] = Json-Bytes ([ordered]@{
    schema = 'pegasus.cortex.branding-provenance/1'
    artwork = 'AI-assisted Cortex C-ribbon and connected-node artwork supplied for the authorized product branding refresh.'
    iconMaster = 'Cortex.Graphic.png'; guiWatermark = 'Cortex.Watermark.png'
    generation = 'Native System.Drawing supersampled resizing and unchanged code-defined role badges.'
    svgFormat = 'Raster-backed SVG: keep the adjacent Cortex.Graphic.png with every SVG wrapper.'
    icoFrames = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    pngPixels = 512; images = @($imageFacts); roles = @($roleFacts)
    licenseFile = 'LICENSE'
})

$null = [IO.Directory]::CreateDirectory($kit)
foreach ($name in @($content.Keys | Sort-Object -CaseSensitive)) { Publish-Bytes (Join-Path $kit $name) ([byte[]]$content[$name]) }
$inventory = foreach ($name in @($content.Keys | Sort-Object -CaseSensitive)) {
    $bytes = [IO.File]::ReadAllBytes((Assert-OrdinaryPath (Join-Path $kit $name)))
    [ordered]@{ file = $name; bytes = $bytes.Length; sha256 = Digest $bytes }
}
Publish-Bytes (Join-Path $kit 'CortexBrandingKit.inventory.json') (Json-Bytes ([ordered]@{
    schema = 'pegasus.cortex.branding-inventory/1'; files = @($inventory)
}))

$zipBytes = [IO.MemoryStream]::new()
$zip = [IO.Compression.ZipArchive]::new($zipBytes, [IO.Compression.ZipArchiveMode]::Create, $true)
try {
    foreach ($name in @($expected | Sort-Object -CaseSensitive)) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $bytes = [IO.File]::ReadAllBytes((Assert-OrdinaryPath (Join-Path $kit $name)))
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
try {
    $bytes = $zipBytes.ToArray()
    Publish-Bytes $archive $bytes
    Publish-Bytes $checksum $encoding.GetBytes((Digest $bytes) + '  CortexBrandingKit.zip' + "`n")
} finally { $zipBytes.Dispose() }
[pscustomobject]@{ schema = 'pegasus.cortex.branding-export/1'; directory = 'artifacts/branding/CortexBrandingKit';
    archive = 'artifacts/branding/CortexBrandingKit.zip'; files = $expected.Count;
    bytes = ([IO.FileInfo]::new($archive)).Length; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
