[CmdletBinding()]
param(
    [string]$PackRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\libraries'),
    [string]$Executable = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Pegasus\Cortex\PegasusCortex.exe')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=[Text.UTF8Encoding]::new($false,$true)
foreach ($path in @($PackRoot,$Executable)) {
    if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Expected an absolute local path.' }
    for ($p=[IO.Path]::GetFullPath($path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) { if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse path rejected.' } }
    }
}
foreach ($name in @('openmoji','fluent-emoji','svgmoji','remix-icon')) {
    $folder=Join-Path $PackRoot $name
    $source=Join-Path $folder 'acquisition.json'
    $acquisition=[IO.File]::ReadAllText($source,$utf8)|ConvertFrom-Json
    $header=Get-Content -LiteralPath (Join-Path $folder 'assets.jsonl') -TotalCount 1|ConvertFrom-Json
    $spec=@{namespace='knowledge.shared';id=('visual.'+$name+'.catalog');title=($name+' visual library catalog');meaning=($acquisition.artworkCount.ToString()+' original SVGs; exact pinned source, sizes and manifest digest. Artwork retains its own per-asset license.');mediaType='application/json';tags=('visual icon emoji pack catalog descriptor '+$name);source=$acquisition.repository;pack=$header.pack;packVersion=$header.version;
        parameters=([ordered]@{role='pack-descriptor';manifestSha256=$acquisition.manifestSha256;artworkCount=$acquisition.artworkCount;artworkBytes=$acquisition.artworkBytes;licensingFiles=$acquisition.licensingFiles;sourceFamily=$name;upstreamRevision=$acquisition.revision;artworkLicensePolicy=if($name -eq 'svgmoji'){'per-artwork: Noto/Blob Apache-2.0, Twemoji CC-BY-4.0, OpenMoji CC-BY-SA-4.0'}else{$header.license}}|ConvertTo-Json -Compress)}
    $metadata=Join-Path $folder 'descriptor-metadata.json'
    [IO.File]::WriteAllText($metadata,($spec|ConvertTo-Json -Compress -Depth 10),$utf8)
    $start=[Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach ($argument in @('assets','import','--file',$source,'--metadata-file',$metadata)) {$start.ArgumentList.Add($argument)}
    $child=[Diagnostics.Process]::new();$child.StartInfo=$start
    try {
        if (-not $child.Start()) { throw 'Installed asset command did not start.' }
        $output=$child.StandardOutput.ReadToEndAsync();$errors=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) {$child.Kill($true);throw 'Descriptor admission timed out.'}
        if ($child.ExitCode -ne 0 -or -not ($output.Result|ConvertFrom-Json).ok) {throw ('Descriptor admission failed: '+$output.Result)}
        [IO.File]::WriteAllText((Join-Path $folder 'descriptor-admission.json'),$output.Result,$utf8)
        Write-Output $output.Result
    } finally {$child.Dispose()}
}
