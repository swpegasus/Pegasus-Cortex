[CmdletBinding()]
param(
    [Parameter()]
    [string] $CacheRoot = (Join-Path $PSScriptRoot '..\..\artifacts\dependencies'),

    [Parameter()]
    [switch] $ForceDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contractPath = Join-Path $PSScriptRoot 'nsis-3.12.json'
$contract = Get-Content -LiteralPath $contractPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($contract.contract -ne 'pegasus-cortex-build-dependency' -or
    $contract.version -ne 1 -or
    $contract.name -ne 'nsis' -or
    $contract.productVersion -ne '3.12') {
    throw 'The NSIS dependency contract identity is invalid.'
}

$cache = [IO.Path]::GetFullPath($CacheRoot)
if (-not [IO.Path]::IsPathFullyQualified($cache)) {
    throw 'CacheRoot must be fully qualified.'
}
[IO.Directory]::CreateDirectory($cache) | Out-Null
if ((Get-Item -LiteralPath $cache -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'CacheRoot cannot be a reparse point.'
}

$archivePath = Join-Path $cache ([string] $contract.archive.fileName)
$finalRoot = Join-Path $cache ([string] $contract.distributionRoot)
$expectedLength = [long] $contract.archive.length
$expectedSha256 = ([string] $contract.archive.sha256).ToLowerInvariant()

function Test-ArchiveExact {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $expectedLength) {
        return $false
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedSha256
}

function Move-InvalidEvidence {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
    $quarantine = Join-Path $cache ("invalid-nsis-3.12-$stamp.bin")
    [IO.File]::Move($Path, $quarantine, $false)
}

if ($ForceDownload -or -not (Test-ArchiveExact -Path $archivePath)) {
    Move-InvalidEvidence -Path $archivePath
    $downloaded = $false
    foreach ($url in @($contract.archive.downloadUrls)) {
        $temporary = Join-Path $cache ('.nsis-3.12.' + [Guid]::NewGuid().ToString('N') + '.download')
        try {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.AllowAutoRedirect = $true
            $client = [Net.Http.HttpClient]::new($handler)
            $client.Timeout = [TimeSpan]::FromMinutes(3)
            try {
                $response = $client.GetAsync(
                    [string] $url,
                    [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                $response.EnsureSuccessStatusCode() | Out-Null
                $input = $response.Content.ReadAsStream()
                try {
                    $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally { $input.Dispose() }
            } finally {
                $client.Dispose()
                $handler.Dispose()
            }
            if (-not (Test-ArchiveExact -Path $temporary)) {
                Move-InvalidEvidence -Path $temporary
                continue
            }
            [IO.File]::Move($temporary, $archivePath, $false)
            $downloaded = $true
            break
        } catch {
            Move-InvalidEvidence -Path $temporary
        }
    }
    if (-not $downloaded) {
        throw 'No NSIS mirror returned the exact hash-pinned 3.12 distribution.'
    }
}

if (Test-Path -LiteralPath $finalRoot) {
    $compiler = Join-Path $finalRoot ([string] $contract.compiler.relativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $validExisting = Test-Path -LiteralPath $compiler -PathType Leaf
    if ($validExisting) {
        $compilerItem = Get-Item -LiteralPath $compiler -Force
        $validExisting = -not $compilerItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and
            $compilerItem.Length -eq [long] $contract.compiler.length -and
            (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant() -eq
                ([string] $contract.compiler.sha256).ToLowerInvariant()
    }
    if (-not $validExisting) {
        throw 'The existing NSIS cache root is not the admitted 3.12 distribution.'
    }
} else {
    $staging = Join-Path $cache ('.nsis-3.12.' + [Guid]::NewGuid().ToString('N') + '.staging')
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in $archive.Entries) {
                $candidate = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
                $relative = [IO.Path]::GetRelativePath($staging, $candidate)
                if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) {
                    throw 'The NSIS archive contains an escaping entry.'
                }
            }
        } finally { $archive.Dispose() }
        [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $staging, $false)
        $inner = Join-Path $staging ([string] $contract.distributionRoot)
        if (-not (Test-Path -LiteralPath $inner -PathType Container)) {
            throw 'The NSIS archive is missing its canonical distribution root.'
        }
        [IO.Directory]::Move($inner, $finalRoot)
    } finally {
        if (Test-Path -LiteralPath $staging) {
            [IO.Directory]::Delete($staging, $true)
        }
    }
}

$compilerPath = Join-Path $finalRoot ([string] $contract.compiler.relativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
$compilerHash = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($compilerHash -ne ([string] $contract.compiler.sha256).ToLowerInvariant()) {
    throw 'The extracted NSIS compiler digest is invalid.'
}
$probeStart = [Diagnostics.ProcessStartInfo]::new()
$probeStart.FileName = $compilerPath
$probeStart.UseShellExecute = $false
$probeStart.CreateNoWindow = $true
$probeStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$probeStart.RedirectStandardOutput = $true
$probeStart.RedirectStandardError = $true
$probeStart.ArgumentList.Add([string] $contract.compiler.versionArgument)
$probe = [Diagnostics.Process]::new()
$probe.StartInfo = $probeStart
try {
    if (-not $probe.Start()) { throw 'The extracted NSIS compiler version probe did not start.' }
    $probeOutputTask = $probe.StandardOutput.ReadToEndAsync()
    $probeErrorTask = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(30000)) {
        try { $probe.Kill($true) } catch { $probe.Kill() }
        $probe.WaitForExit()
        throw 'The extracted NSIS compiler version probe timed out.'
    }
    $versionOutput = ($probeOutputTask.GetAwaiter().GetResult() + $probeErrorTask.GetAwaiter().GetResult()).Trim()
    $probeExitCode = $probe.ExitCode
}
finally {
    $probe.Dispose()
}
if ($probeExitCode -ne 0 -or $versionOutput -ne [string] $contract.compiler.expectedVersion) {
    throw 'The extracted NSIS compiler version probe failed.'
}

[pscustomobject]@{
    Contract = [string] $contract.contract
    Name = [string] $contract.name
    Version = [string] $contract.productVersion
    Archive = $archivePath
    ArchiveSha256 = $expectedSha256
    Root = $finalRoot
    Compiler = $compilerPath
    CompilerSha256 = $compilerHash
    CompilerVersion = $versionOutput
    License = Join-Path $finalRoot ([string] $contract.license.relativePath)
}
