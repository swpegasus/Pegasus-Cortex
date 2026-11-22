[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('openmoji','fluent-emoji','svgmoji','remix-icon')][string[]]$Library,
    [Parameter(Mandatory)][string]$Executable,
    [string]$PackRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\release\assets\libraries')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$tool = $Executable
foreach ($path in @($PackRoot,$tool)) {
    if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Expected an absolute local path.' }
    for ($p=[IO.Path]::GetFullPath($path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) { if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse path rejected.' } }
    }
}
if (-not [IO.File]::Exists($tool)) { throw 'The exact installed Cortex executable is unavailable.' }
foreach ($name in ($Library | Select-Object -Unique)) {
    $folder=Join-Path $PackRoot $name
    $receipt=[IO.File]::ReadAllText((Join-Path $folder 'acquisition.json'),$utf8)|ConvertFrom-Json
    $manifest=Join-Path $folder 'assets.jsonl'
    Write-Output ('stage=preflight-and-admit library='+$name)
    $admission=$null
    $start=[Diagnostics.ProcessStartInfo]::new($tool)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach ($argument in @('assets','import-visual-library','--manifest',$manifest,'--sha256',$receipt.manifestSha256)) { $start.ArgumentList.Add($argument) }
    $child=[Diagnostics.Process]::new();$child.StartInfo=$start
    $started=$false
    try {
        $started=$child.Start()
        if (-not $started) { throw 'Visual-library helper did not start.' }
        $errorText=$child.StandardError.ReadToEndAsync()
        $deadline=[DateTime]::UtcNow.AddMinutes(25)
        while ($true) {
            $line=$child.StandardOutput.ReadLineAsync()
            while (-not $line.Wait(1000)) {
                if ([DateTime]::UtcNow -gt $deadline) { $child.Kill($true);throw 'Visual-library import exceeded its bounded deadline.' }
            }
            if ($null -eq $line.Result) { break }
            Write-Output $line.Result
            if ($line.Result.StartsWith('{')) { $admission=$line.Result|ConvertFrom-Json }
        }
        if (-not $child.WaitForExit(10000)) { $child.Kill($true);throw 'Visual-library helper did not exit.' }
        if ($child.ExitCode -ne 0 -or $null -eq $admission -or -not $admission.ok) {
            $boundedError=if ($errorText.Wait(1000)) { $errorText.Result } else { 'stderr did not close' }
            throw ('Visual-library admission did not complete: '+$boundedError.Substring(0,[Math]::Min(1000,$boundedError.Length)))
        }
    } finally {
        # Parsing/reader failures must not orphan this exact owned import process.
        # The streaming importer retains truthful partial-commit receipts; cleanup
        # does not delete assets, restart shared hosts or manufacture completion.
        try {
            if ($started -and -not $child.HasExited) {
                $child.Kill($true)
                $null=$child.WaitForExit(5000)
            }
        } finally { $child.Dispose() }
    }
    if ($admission.artworkCount -ne $receipt.artworkCount -or $admission.artworkBytes -ne $receipt.artworkBytes) { throw 'Acquisition/admission inventories differ.' }
    # A current bounded receipt, not a second asset authority or backup generation.
    [IO.File]::WriteAllText((Join-Path $folder 'admission.json'),($admission|ConvertTo-Json -Depth 10),$utf8)
}
