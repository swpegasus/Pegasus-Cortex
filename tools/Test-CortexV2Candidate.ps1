[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot,
      [Parameter(Mandatory)][string]$ReleaseVersion)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($PackageRoot)
for ($path=$root; $path; $path=[IO.Path]::GetDirectoryName($path)) {
    $item=Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Candidate reparse path rejected.' }
}
$verified=& (Join-Path $PSScriptRoot 'signing\Test-PegasusSignedPackage.ps1') `
    -PackageRoot $root -Role product -ReleaseVersion $ReleaseVersion
$semantics=Join-Path $root 'bin\semantics\PegasusCortex.exe'
$version=& $semantics version
if ($LASTEXITCODE -ne 0 -or $version -cne $ReleaseVersion) { throw 'Actual semantic candidate version mismatch.' }
$pack=Join-Path $root 'bin\semantics\asset-packs\chat-icons'
if (-not (Test-Path -LiteralPath $pack -PathType Container)) { throw 'Frozen icon pack absent.' }
$catalogPin='04aa6881acc2f65edab2c046409cb87f71e932081fb0ac21bea9f2f526aa0011'
$files=[Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem -LiteralPath $pack -File -Recurse) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Frozen pack reparse file rejected.' }
    $relative=[IO.Path]::GetRelativePath($pack,$file.FullName).Replace('\','/')
    $files.Add($relative,(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
}
$canonical=[Text.StringBuilder]::new()
foreach ($pair in $files.GetEnumerator()) { $null=$canonical.Append($pair.Key).Append([char]0).Append($pair.Value).Append("`n") }
$contentPin=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical.ToString())))
$icons=& $semantics assets verify-icons --pack-root $pack --catalog-sha $catalogPin --content-sha $contentPin
if ($LASTEXITCODE -ne 0) { throw 'Actual candidate rejected its frozen icon pack.' }
$iconReceipt=$icons | ConvertFrom-Json
if (-not $iconReceipt.ok -or $iconReceipt.icons -ne 11) { throw 'Frozen icon count mismatch.' }

# Local discovery/help only: no installed host dispatch, semantic write, job,
# service/tray launch, signing/trust mutation or database path override.
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'bin\pao.exe'))
$start.ArgumentList.Add('mcp');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::new();$process.StartInfo=$start
try {
    if (-not $process.Start()) { throw 'Candidate PAO MCP did not start.' }
    $errors=$process.StandardError.ReadToEndAsync()
    function Request([int]$Id,[string]$Method,$Parameters) {
        $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters}|ConvertTo-Json -Compress -Depth 10))
        $read=$process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(10000) -or $null -eq $read.Result) { throw 'Candidate JSON-RPC response absent.' }
        $response=$read.Result | ConvertFrom-Json -Depth 30
        if ($response.id -ne $Id -or $response.PSObject.Properties['error']) { throw 'Candidate JSON-RPC response mismatch.' }
        return $response.result
    }
    $initialize=Request 1 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='bounded-candidate-check';version='1'}}
    if ($initialize.serverInfo.version -cne $ReleaseVersion) { throw 'Candidate PAO version mismatch.' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $listed=Request 2 'tools/list' @{}
    if (@($listed.tools).Count -ne 1 -or $listed.tools[0].name -cne 'pao' -or
        $listed.tools[0].inputSchema.properties.wait.type -cne 'boolean') { throw 'Candidate PAO deferred schema absent.' }
    $help=Request 3 'tools/call' @{name='pao';arguments=@{op='help';name='dotnet_build';output='agent'}}
    if ($help.isError -or ($help.content.text -join '') -notmatch 'wait') { throw 'Candidate PAO deferred help absent.' }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { throw 'Candidate PAO failed clean EOF.' }
    $stderr=$errors.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or $stderr.Length -ne 0) { throw 'Candidate PAO failed clean protocol exit.' }
    [pscustomobject]@{ok=$true;releaseVersion=$ReleaseVersion;signedPackage=$verified.Valid;
        actualCandidateVersion=$true;frozenIcons=11;actualPaoJsonRpc=$true;deferredSchema=$true;
        stderrBytes=0;exitCode=0;installedDispatch=$false;databaseWrites=$false;deployment=$false}
} finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill($true);$null=$process.WaitForExit(5000) }
    $process.Dispose()
}
