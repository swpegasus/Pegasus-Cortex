[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\libraries'),
    [string]$DownloadRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\build\current\visual-downloads')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Check-LocalPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path.Length -gt 240) { throw 'Unsafe visual-library path.' }
    for ($p = [IO.Path]::GetFullPath($Path); $p; $p = [IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) { if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse path rejected.' } }
    }
}
foreach ($root in @($OutputRoot, $DownloadRoot)) { Check-LocalPath $root; [IO.Directory]::CreateDirectory($root) | Out-Null; Check-LocalPath $root }
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(15)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Pegasus-Cortex-Visual-Library-Admission/1')
function Download([string]$Uri, [string]$Destination) {
    Check-LocalPath $Destination
    if ([IO.File]::Exists($Destination)) { return }
    $allowed = @('api.github.com','raw.githubusercontent.com','codeload.github.com')
    $parsed = [uri]$Uri
    if ($parsed.Scheme -ne 'https' -or $parsed.Host -notin $allowed) { throw 'Unexpected upstream endpoint.' }
    $response = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        if ($response.RequestMessage.RequestUri.Host -notin $allowed) { throw 'Unexpected download redirect.' }
        if ($response.Content.Headers.ContentLength -gt 2GB) { throw 'Download exceeds the bounded acquisition size.' }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.FileStream]::new($Destination + '.part', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = [byte[]]::new(65536); [long]$count = 0
            while (($read = $input.Read($buffer,0,$buffer.Length)) -gt 0) { $count += $read; if ($count -gt 2GB) { throw 'Download exceeds the bounded acquisition size.' }; $output.Write($buffer,0,$read) }
            $output.Flush($true)
        } finally { $output.Dispose(); $input.Dispose() }
        [IO.File]::Move($Destination + '.part', $Destination)
    } finally { $response.Dispose() }
}
function Hash-Bytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Read-ZipText($Zip, [string]$Name) {
    $entry = $Zip.GetEntry($Name); if ($null -eq $entry) { throw "Required source entry missing: $Name" }
    if ($entry.Length -gt 16MB) { throw 'Source metadata exceeds its bound.' }
    $stream = $entry.Open(); $reader = [IO.StreamReader]::new($stream,$utf8,$false)
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Write-Exact([string]$Path, [byte[]]$Bytes) {
    Check-LocalPath $Path
    if ([IO.File]::Exists($Path)) { if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne (Hash-Bytes $Bytes)) { throw 'Existing prepared content differs; no overwrite was performed.' }; return }
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length) } finally { $stream.Dispose() }
}
function Slug([string]$Text) { (($Text.ToLowerInvariant() -replace '[^a-z0-9]+','-').Trim('-')) }
function Short-Text([string]$Text,[int]$Maximum) { if ($Text.Length -le $Maximum) { $Text } else { $Text.Substring(0,$Maximum-3) + '...' } }
$sources = @(
    @{name='openmoji';repo='hfg-gmuend/openmoji';sha='aeb8bb3a59e2de39c754ac79180c8131c906acea';license='CC-BY-SA-4.0';licensePath='LICENSE.txt'},
    @{name='fluent-emoji';repo='microsoft/fluentui-emoji';sha='1ffb34c752ecf5d402f04cfb4b392c77f57c54bc';license='MIT';licensePath='LICENSE'},
    @{name='svgmoji';repo='svgmoji/svgmoji';sha='fb4bf0c2e6188c0cf70642d2931e92b9b7b0ae65';license='MIT';licensePath='LICENSE'},
    @{name='remix-icon';repo='Remix-Design/RemixIcon';sha='4ef50878f17accbed89ef2691bdc357562ed0a17';license='Apache-2.0';licensePath='License'}
)
# Svgmoji's MIT wrapper license is not the license of its aggregated artwork.
# Pin historical Noto artwork licensing rather than treating a newer font license as SVG permission.
$extraLicenses = @(
    @{key='noto';license='Apache-2.0';uri='https://raw.githubusercontent.com/googlefonts/noto-emoji/v2.038/LICENSE';credit='Google Noto Emoji contributors; SVG artwork, not font software.'},
    @{key='blob';license='Apache-2.0';uri='https://raw.githubusercontent.com/c1710/blobmoji/7dd14d2b0141693485fd26bc35817bd290352a79/LICENSE';credit='Blobmoji contributors and Google Noto Emoji contributors; SVG artwork.'},
    @{key='twemoji';license='CC-BY-4.0';uri='https://raw.githubusercontent.com/twitter/twemoji/bad3bceeafc901ace42a3dfe0421db6388daafb9/LICENSE-GRAPHICS';credit='Twemoji graphics by Twitter and contributors, obtained through Svgmoji.'},
    @{key='openmoji';license='CC-BY-SA-4.0';uri='https://raw.githubusercontent.com/hfg-gmuend/openmoji/aeb8bb3a59e2de39c754ac79180c8131c906acea/LICENSE.txt';credit='All emojis designed by OpenMoji, the open-source emoji and icon project.'}
)
try {
    foreach ($source in $sources) {
        $pack = 'visual.' + $source.name
        $folder = Join-Path $OutputRoot $source.name; Check-LocalPath $folder
        $receiptPath = Join-Path $folder 'acquisition.json'
        if ([IO.File]::Exists($receiptPath)) {
            $existing = [IO.File]::ReadAllText($receiptPath,$utf8) | ConvertFrom-Json
            $existingManifest = Join-Path $folder 'assets.jsonl'
            if ($existing.revision -ne $source.sha -or -not [IO.File]::Exists($existingManifest) -or
                (Get-FileHash -LiteralPath $existingManifest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $existing.manifestSha256) { throw 'Existing acquisition identity differs.' }
            Write-Output ('stage=prepared library='+$source.name+' replay=true assets='+$existing.artworkCount)
            continue
        }
        foreach ($dir in @($folder,(Join-Path $folder 'files'),(Join-Path $folder 'Licensing'))) { [IO.Directory]::CreateDirectory($dir) | Out-Null; Check-LocalPath $dir }
        $zipPath = Join-Path $DownloadRoot ($source.name + '.zip')
        Write-Output ('stage=acquire library=' + $source.name)
        Download ('https://codeload.github.com/' + $source.repo + '/zip/' + $source.sha) $zipPath
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $prefix = $source.repo.Split('/')[1] + '-' + $source.sha + '/'
            $sourceLicense = Read-ZipText $zip ($prefix + $source.licensePath)
            $licenseMarker = switch ($source.license) { 'MIT' {'MIT License'} 'Apache-2.0' {'Apache License'} 'CC-BY-SA-4.0' {'Attribution-ShareAlike 4.0'} }
            if (-not $sourceLicense.Contains($licenseMarker)) { throw 'Pinned upstream license does not match admission policy.' }
            Write-Exact (Join-Path $folder 'Licensing\LICENSE.txt') ($utf8.GetBytes($sourceLicense))
            $credit = switch ($source.name) {
                'openmoji' {'All emojis designed by OpenMoji - https://openmoji.org/ . License: CC BY-SA 4.0. Original artwork bytes retained; Cortex catalog adaptation only.'}
                'fluent-emoji' {'Fluent Emoji artwork copyright Microsoft Corporation. MIT License. Original artwork bytes retained.'}
                'remix-icon' {'Remix Icon v4.7.0, copyright Remix Design, Apache License 2.0. Original artwork bytes retained. Not the newly restricted 2026 license.'}
                'svgmoji' {'Svgmoji wrapper copyright its contributors, MIT. Artwork retains each underlying source license. Original Svgmoji SVG bytes retained, including prior upstream optimizations; no further artwork edits.'}
            }
            Write-Exact (Join-Path $folder 'Licensing\NOTICE.txt') ($utf8.GetBytes($credit + "`nUpstream: https://github.com/" + $source.repo + "`nCommit: " + $source.sha + "`nNo trademark or endorsement rights are represented.`n"))
            $upstreamNotice = $zip.GetEntry($prefix + 'NOTICE')
            if ($null -ne $upstreamNotice) { Write-Exact (Join-Path $folder 'Licensing\UPSTREAM-NOTICE.txt') ($utf8.GetBytes((Read-ZipText $zip $upstreamNotice.FullName))) }
            if ($source.name -eq 'svgmoji') {
                foreach ($extra in $extraLicenses) {
                    $dst = Join-Path $folder ('Licensing\' + $extra.key + '.txt'); Download $extra.uri $dst
                    $licenseText = [IO.File]::ReadAllText($dst,$utf8)
                    $marker = switch ($extra.license) { 'Apache-2.0' {'Apache License'} 'CC-BY-4.0' {'Attribution 4.0'} 'CC-BY-SA-4.0' {'Attribution-ShareAlike 4.0'} }
                    if (-not $licenseText.Contains($marker)) { throw 'Underlying artwork license does not match its declared license.' }
                    Write-Exact (Join-Path $folder ('Licensing\' + $extra.key + '-notice.txt')) ($utf8.GetBytes($extra.credit + "`nLicense source: " + $extra.uri + "`nOriginal Svgmoji bytes retained. No new artwork edits.`n"))
                }
            }
            $metadataByCode = @{}
            $metadataByName = @{}
            if ($source.name -eq 'openmoji') { foreach ($item in (Read-ZipText $zip ($prefix+'data/openmoji.json') | ConvertFrom-Json)) { $metadataByCode[$item.hexcode.ToLowerInvariant()] = $item } }
            if ($source.name -eq 'svgmoji') { foreach ($item in (Read-ZipText $zip ($prefix+'packages/svgmoji/emoji.json') | ConvertFrom-Json)) { $metadataByCode[$item.hexcode.ToLowerInvariant()] = $item } }
            if ($source.name -eq 'fluent-emoji') { foreach ($entry in $zip.Entries) { if ($entry.FullName -match '/assets/([^/]+)/metadata.json$') { $metadataByName[$Matches[1]] = (Read-ZipText $zip $entry.FullName | ConvertFrom-Json) } } }
            $manifestPath = Join-Path $folder 'assets.jsonl'
            if ([IO.File]::Exists($manifestPath)) { throw 'Prepared manifest already exists; use its pinned manifest digest rather than rebuilding it.' }
            # A failed acquisition is not a completed pack. This exact task-owned
            # staging file may be regenerated; final content is never overwritten.
            $manifestTemporary = $manifestPath + '.part'
            Check-LocalPath $manifestTemporary
            $writer = [IO.StreamWriter]::new($manifestTemporary,$false,$utf8)
            $count = 0; [long]$size = 0; $excludedFlags = 0
            try {
                $header = @{contract='pegasus.visual-library/1';namespace='knowledge.shared';pack=$pack;version=$source.sha;upstream=('https://github.com/'+$source.repo);revision=$source.sha;license=$source.license}
                $writer.WriteLine(($header | ConvertTo-Json -Compress))
                foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $folder 'Licensing') -File | Sort-Object Name)) {
                    $key = Slug $file.BaseName
                    $fileLicense=$source.license;$fileSource='https://github.com/'+$source.repo+'/blob/'+$source.sha+'/'+$source.licensePath
                    if ($source.name -eq 'svgmoji') {
                        $underlying = @($extraLicenses | Where-Object { $key -eq $_.key -or $key -eq ($_.key+'-notice') })
                        if ($underlying.Count -eq 1) { $fileLicense=$underlying[0].license; $fileSource=$underlying[0].uri }
                    }
                    $spec = @{namespace='knowledge.shared';id=($pack+'.'+$key);title=($source.name+' '+$file.BaseName);meaning='Exact license or attribution notice for this visual-library source.';mediaType='text/plain';tags=('visual library licensing '+$source.name);license=$fileLicense;source=$fileSource;pack=$pack;packVersion=$source.sha;parameters=(@{role='license-or-notice';upstreamRevision=$source.sha} | ConvertTo-Json -Compress)}
                    $writer.WriteLine((@{file=('Licensing/'+$file.Name);sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();metadata=$spec;licenseId=$null} | ConvertTo-Json -Compress -Depth 8))
                }
                foreach ($entry in $zip.Entries) {
                    if (-not $entry.FullName.StartsWith($prefix)) { throw 'Unexpected upstream archive root.' }
                    $relative = $entry.FullName.Substring($prefix.Length)
                    $style='';$code='';$item=$null;$name='';$glyph='';$license=$source.license;$licenseId=$pack+'.license'
                    switch ($source.name) {
                        'openmoji' { if ($relative -notmatch '^(black|color)/svg/([^/]+)\.svg$') { continue }; $style=$Matches[1];$code=$Matches[2].ToLowerInvariant();$item=$metadataByCode[$code]; if ($item) {$name=$item.annotation;$glyph=$item.emoji} else {$name=$code} }
                        'fluent-emoji' { if ($relative -notmatch '^assets/([^/]+)/(.*/)?(Flat|Color|High Contrast)/([^/]+)\.svg$') { continue }; $name=$Matches[1];$style=Slug $Matches[3];$item=$metadataByName[$name];if ($item -and -not $Matches[2]) {$glyph=$item.glyph}; if ($Matches[2]) {$name+=' '+$Matches[2].Trim('/')} }
                        'svgmoji' { if ($relative -notmatch '^packages/svgmoji__(blob|noto|openmoji|twemoji)/svg/([^/]+)\.svg$') { continue };$style=$Matches[1];$code=$Matches[2].ToLowerInvariant();$item=$metadataByCode[$code];if ($item) {$name=$item.annotation;$glyph=$item.emoji} else {$name=$code};$extra=$extraLicenses | Where-Object key -eq $style;$license=$extra.license;$licenseId=$pack+'.'+$style }
                        'remix-icon' { if ($relative -notmatch '^icons/([^/]+)/([^/]+)\.svg$') { continue };$style=Slug $Matches[1];$name=$Matches[2] -replace '-',' ' }
                    }
                    if (-not $style) { continue }
                    # Noto/Blob country flags have separate jurisdiction-specific
                    # rights. Leave those variants out instead of mislabeling them Apache.
                    if ($source.name -eq 'svgmoji' -and $style -in @('noto','blob') -and
                        ($code -match '^1f1[e-f][0-9a-f]-1f1[e-f][0-9a-f]$' -or $code -match '^1f3f4-e00')) { $excludedFlags++; continue }
                    if ((([long]$entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive symlink asset rejected.' }
                    if ($entry.Length -le 0 -or $entry.Length -gt 2MB) { throw 'Individual visual asset exceeds the fixed package bound.' }
                    $pathDigest=Hash-Bytes ($utf8.GetBytes($relative));$slug=Short-Text (Slug $name) 55
                    $id=$pack+'.'+$style+'.'+$slug+'.'+$pathDigest.Substring(0,12)
                    $target='files/'+$pathDigest+'.svg';$targetPath=Join-Path $folder $target
                    $input=$entry.Open();$memory=[IO.MemoryStream]::new()
                    try {$input.CopyTo($memory,65536);$bytes=$memory.ToArray()} finally {$input.Dispose();$memory.Dispose()}
                    Write-Exact $targetPath $bytes
                    $parameters=@{sourceFamily=$source.name;variant=$style;upstreamRevision=$source.sha;originalPath=$relative;artworkModifiedByCortex=$false;renderProfile='passive-svg-original';licenseId=$licenseId}
                    $xmlSettings=[Xml.XmlReaderSettings]::new();$xmlSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$xmlSettings.XmlResolver=$null;$xmlSettings.MaxCharactersInDocument=2MB
                    $xmlStream=[IO.MemoryStream]::new($bytes,$false);$xmlReader=[Xml.XmlReader]::Create($xmlStream,$xmlSettings)
                    try {
                        $xmlReader.MoveToContent() | Out-Null
                        if ($xmlReader.LocalName -ne 'svg' -or $xmlReader.NamespaceURI -ne 'http://www.w3.org/2000/svg') { throw 'Expected passive SVG artwork.' }
                        $viewBox=$xmlReader.GetAttribute('viewBox');$width=$xmlReader.GetAttribute('width');$height=$xmlReader.GetAttribute('height')
                        if ($viewBox) {$parameters.viewBox=$viewBox};if ($width) {$parameters.width=$width};if ($height) {$parameters.height=$height}
                    } finally {$xmlReader.Dispose();$xmlStream.Dispose()}
                    if ($item) {$parameters.upstreamMetadata=$item}
                    # Unknown variant Unicode is omitted rather than falsely labeling a skin-tone asset.
                    $spec=@{namespace='knowledge.shared';id=$id;title=(Short-Text ($name+' ['+$style+']') 128);meaning=(Short-Text $name 512);mediaType='image/svg+xml';tags=(Short-Text ('visual icon emoji '+$source.name+' '+$style+' '+$name) 256);license=$license;source=('https://github.com/'+$source.repo+'/blob/'+$source.sha+'/'+[uri]::EscapeDataString($relative).Replace('%2F','/'));pack=$pack;packVersion=$source.sha;parameters=($parameters|ConvertTo-Json -Compress -Depth 12)}
                    if ($glyph -and $glyph.Length -le 32) {$spec.text=$glyph}
                    $writer.WriteLine((@{file=$target;sha256=(Hash-Bytes $bytes);metadata=$spec;licenseId=$licenseId}|ConvertTo-Json -Compress -Depth 14))
                    $count++;$size+=$bytes.Length
                    if ($count % 1000 -eq 0) {Write-Output ('stage=prepare library='+$source.name+' assets='+$count)}
                }
            } finally {$writer.Dispose()}
            if ($count -eq 0) {throw 'No assets selected from the admitted source.'}
            [IO.File]::Move($manifestTemporary,$manifestPath)
            $receipt=@{contract='pegasus.visual-library-acquisition/1';source=$source.name;repository=$header.upstream;revision=$source.sha;manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();archiveSha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant();artworkCount=$count;artworkBytes=$size;excludedSeparateRightsFlags=$excludedFlags;licensingFiles=@(Get-ChildItem -LiteralPath (Join-Path $folder 'Licensing') -File).Count;remixPolicy=if($source.name -eq 'remix-icon') {'Pinned Apache-2.0 v4.7.0; no files from newer restricted releases.'} else {'not-applicable'}}
            Write-Exact $receiptPath ($utf8.GetBytes(($receipt|ConvertTo-Json -Depth 5)))
            Write-Output ($receipt|ConvertTo-Json -Compress -Depth 5)
        } finally {$zip.Dispose()}
    }
} finally {$client.Dispose()}
