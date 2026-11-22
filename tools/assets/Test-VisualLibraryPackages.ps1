[CmdletBinding()]
param(
    [string]$PackageIndex = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\visual-library-packages.json'),
    [string]$EvidenceFile
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=[Text.UTF8Encoding]::new($false,$true)
if ((Get-Item -LiteralPath $PackageIndex).Length -gt 32768) { throw 'Module index oversized.' }
$index=Get-Content -LiteralPath $PackageIndex -Raw|ConvertFrom-Json
$packages=if ($index -is [array]) { $index } else {
    if ($index.contract -ne 'pegasus.visual-library-modules/1' -or $index.version -ne 1 -or -not $index.dataOnly) { throw 'Unsupported portable module index.' }
    $index.modules
}
$results=@()
foreach ($package in $packages) {
    if ($index -isnot [array]) {
        if ($package.moduleId -ne ('visual.'+$package.library) -or $package.kind -ne 'data' -or
            $package.required -or $package.defaultEnabled -or $package.namespace -ne 'knowledge.shared' -or
            $package.moduleVersion -notmatch '^[a-f0-9]{40}$' -or @($package.licensing).Count -gt 32 -or
            [IO.Path]::IsPathFullyQualified([string]$package.file)) { throw 'Portable optional-module contract invalid.' }
    }
    $file=if ([IO.Path]::IsPathFullyQualified([string]$package.file)) { [string]$package.file } else {
        if ($package.file -notmatch '^[a-z-]+\.zip$') { throw 'Unsafe relative module path.' }
        Join-Path (Split-Path -Parent $PackageIndex) $package.file
    }
    for ($p=[IO.Path]::GetFullPath($file);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) { if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package reparse path rejected.' } }
    }
    if ((Get-Item -LiteralPath $file).Length -ne $package.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.sha256) { throw 'Data module digest mismatch.' }
    $archive=[IO.Compression.ZipFile]::OpenRead($file)
    try {
        if ($archive.Entries.Count -ne $package.files -or $archive.Entries.Count -gt 50002) { throw 'Archive inventory differs.' }
        $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $names.Add($entry.FullName) -or $entry.FullName.Contains('\') -or $entry.FullName.Contains(':') -or $entry.FullName.StartsWith('/') -or $entry.FullName.Split('/') -contains '..' -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe/duplicate archive entry.' }
        }
        $manifest=$archive.GetEntry('assets.jsonl')
        if ($null -eq $manifest -or $manifest.Length -gt 128MB) { throw 'Manifest absent/oversized.' }
        $stream=$manifest.Open()
        try { $pin=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally {$stream.Dispose()}
        if ($pin -ne $package.manifestSha256) { throw 'Archived manifest differs.' }
        $stream=$manifest.Open();$reader=[IO.StreamReader]::new($stream,$utf8,$false)
        $records=0;[long]$bytes=0;$licensing=@()
        try {
            $header=$reader.ReadLine()|ConvertFrom-Json
            if ($header.contract -ne 'pegasus.visual-library/1' -or $header.namespace -ne 'knowledge.shared' -or $header.pack -ne ('visual.'+$package.library)) { throw 'Data module role/namespace differs.' }
            while ($null -ne ($line=$reader.ReadLine())) {
                if ($utf8.GetByteCount($line) -gt 32768) { throw 'Manifest line exceeds its bound.' }
                $row=$line|ConvertFrom-Json -Depth 32
                if ($row.metadata.namespace -ne 'knowledge.shared' -or $row.metadata.pack -ne $header.pack -or $row.metadata.packVersion -ne $header.version -or
                    $row.metadata.mediaType -notin @('text/plain','image/svg+xml') -or $row.file -notmatch '^(Licensing/[^/]+\.txt|files/[a-f0-9]{64}\.svg)$') { throw 'Unexpected data-only inventory field.' }
                $entry=$archive.GetEntry($row.file)
                if ($null -eq $entry -or $entry.Length -gt 2MB -or -not $names.Remove($row.file)) { throw 'Declared file missing/repeated/oversized.' }
                $input=$entry.Open()
                try { $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant() } finally {$input.Dispose()}
                if ($digest -ne $row.sha256) { throw 'Archived content differs from exact declared digest.' }
                if ($null -eq $row.licenseId) { $licensing+= [ordered]@{ file=$row.file; id=$row.metadata.id; sha256=$row.sha256; license=$row.metadata.license; source=$row.metadata.source } }
                $records++;$bytes+=$entry.Length
            }
        } finally {$reader.Dispose()}
        if ($names.Count -ne 2 -or -not $names.Contains('assets.jsonl') -or -not $names.Contains('acquisition.json')) { throw 'Archive contains undeclared extras.' }
        if ($index -isnot [array]) {
            if ($header.version -ne $package.moduleVersion -or
                ($licensing | ConvertTo-Json -Depth 4 -Compress) -cne ($package.licensing | ConvertTo-Json -Depth 4 -Compress)) { throw 'Portable licensing/version inventory differs.' }
        }
        $results+=@{library=$package.library;records=$records;originalBytes=$bytes;manifestVerified=$true;allArchivedDigestsVerified=$true;onlyPublicData=$true}
    } finally {$archive.Dispose()}
}
$receipt=@{ok=$true;evidence='real compressed data modules';modules=$results}|ConvertTo-Json -Compress -Depth 5
if ($EvidenceFile) {
    if (-not [IO.Path]::IsPathFullyQualified($EvidenceFile)) { throw 'Evidence path must be absolute.' }
    for ($path=[IO.Path]::GetFullPath($EvidenceFile);$path;$path=[IO.Path]::GetDirectoryName($path)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence reparse path rejected.' }
    }
    [IO.File]::WriteAllText($EvidenceFile,$receipt,$utf8)
}
$receipt
