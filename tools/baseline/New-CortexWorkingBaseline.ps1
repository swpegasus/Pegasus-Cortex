[CmdletBinding()]
param([Parameter(Mandatory)][string] $Name)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-z0-9-]{1,60}$') { throw 'Use a short lowercase baseline name.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$parent = Join-Path $projectRoot 'artifacts\baselines'
$destination = Join-Path $parent $Name
if (Test-Path -LiteralPath $destination) { throw 'Baseline destination already exists.' }
$allowedExtensions = @('.cs','.csproj','.props','.targets','.slnx','.json','.md','.ps1','.py','.nsi','.nsh','.xml','.toml','.config','.txt')
$files = [Collections.Generic.List[IO.FileInfo]]::new()
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($projectRoot)
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    $item = Get-Item -LiteralPath $directory -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse directory: $directory" }
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse input: $($entry.FullName)" }
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin @('artifacts','bin','obj','.git','.vs','.cortex','__pycache__')) { $pending.Push($entry.FullName) }
        } elseif ($entry.Extension -in $allowedExtensions) { $files.Add($entry) }
    }
}
if ($files.Count -eq 0) { throw 'Empty baseline.' }
foreach ($ancestor in @($projectRoot, (Join-Path $projectRoot 'artifacts'), $parent)) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe baseline parent.' }
    }
}
New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $destination 'source.zip'
$stream = [IO.File]::Open($zipPath, 'CreateNew', 'ReadWrite', 'None')
$manifest = [Collections.Generic.List[object]]::new()
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in ($files | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\','/')
            if ($relative.StartsWith('../') -or [IO.Path]::IsPathRooted($relative)) { throw 'Baseline input escaped root.' }
            $input = [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read')
            try {
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $file.LastWriteTimeUtc
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
                $input.Position = 0
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant()
                $manifest.Add([ordered]@{ path=$relative; bytes=$input.Length; sha256=$hash })
            } finally { $input.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$document = [ordered]@{ schema='cortex.working-baseline/1'; createdAtUtc=[DateTime]::UtcNow.ToString('O'); root=$projectRoot; files=$manifest }
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($document | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ destination=$destination; files=$manifest.Count; archiveBytes=(Get-Item -LiteralPath $zipPath).Length }
