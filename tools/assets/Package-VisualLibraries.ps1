[CmdletBinding()]
param([string]$PackRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\libraries'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=[Text.UTF8Encoding]::new($false,$true)
function Check-Path([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path.Length -gt 240) { throw 'Unsafe data-package path.' }
    for ($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) { if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse package path rejected.' } }
    }
}
Check-Path $PackRoot
$results=@()
foreach ($name in @('openmoji','fluent-emoji','svgmoji','remix-icon')) {
    $folder=Join-Path $PackRoot $name
    $manifest=Join-Path $folder 'assets.jsonl'
    $acquisition=[IO.File]::ReadAllText((Join-Path $folder 'acquisition.json'),$utf8)|ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $acquisition.manifestSha256) { throw 'Manifest pin mismatch.' }
    $destination=Join-Path (Split-Path -Parent $PackRoot) ($name+'.zip');Check-Path $destination
    $temporary=$destination+'.part';Check-Path $temporary
    if ([IO.File]::Exists($destination) -or [IO.File]::Exists($temporary)) { throw 'Canonical data package already exists; no automatic package generations.' }
    $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $archive=[IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create,$true)
    $count=0;[long]$uncompressed=0
    function Add-PinnedFile([string]$Relative,[string]$Pin) {
        if ($Relative.Contains('\') -or $Relative.Contains(':') -or $Relative.StartsWith('/') -or $Relative.Split('/') -contains '..') { throw 'Unsafe archive entry.' }
        $path=Join-Path $folder $Relative;Check-Path $path
        $input=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant() -ne $Pin) { throw 'Data-package file digest differs.' }
            $input.Position=0
            $entry=$archive.CreateEntry($Relative,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime=[DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $target=$entry.Open()
            try {$input.CopyTo($target,65536)} finally {$target.Dispose()}
            $script:uncompressed+=$input.Length;$script:count++
        } finally {$input.Dispose()}
    }
    try {
        Add-PinnedFile 'assets.jsonl' $acquisition.manifestSha256
        Add-PinnedFile 'acquisition.json' (Get-FileHash -LiteralPath (Join-Path $folder 'acquisition.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        $reader=[IO.StreamReader]::new($manifest,$utf8,$false)
        try {
            $null=$reader.ReadLine()
            while ($null -ne ($line=$reader.ReadLine())) {
                if ($utf8.GetByteCount($line) -gt 32768) { throw 'Manifest line exceeds the data contract.' }
                $row=$line|ConvertFrom-Json -Depth 32
                Add-PinnedFile $row.file $row.sha256
            }
        } finally {$reader.Dispose()}
    } finally {$archive.Dispose();$output.Flush($true);$output.Dispose()}
    if ($count -ne ($acquisition.artworkCount+$acquisition.licensingFiles+2)) { throw 'Data package inventory mismatch.' }
    [IO.File]::Move($temporary,$destination)
    $result=@{contract='pegasus.visual-library-package/1';library=$name;file=$destination;sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant();manifestSha256=$acquisition.manifestSha256;files=$count;bytes=(Get-Item -LiteralPath $destination).Length;uncompressedBytes=$uncompressed;dataOnly=$true}
    $results+=$result
    Write-Output ($result|ConvertTo-Json -Compress)
}
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $PackRoot) 'visual-library-packages.json'),($results|ConvertTo-Json -Depth 6),$utf8)
