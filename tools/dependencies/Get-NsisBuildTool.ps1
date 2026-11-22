[CmdletBinding()]
param(
    [Parameter()]
    [string] $CacheRoot = (Join-Path $PSScriptRoot '..\..\artifacts\dependencies'),

    [Parameter()]
    [switch] $ForceDownload,

    [Parameter()]
    [string] $DestinationRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CortexNsisPinnedArchive.psm1') -Force

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
Assert-CortexNsisOrdinaryPath -Path $cache
[IO.Directory]::CreateDirectory($cache) | Out-Null
Assert-CortexNsisOrdinaryPath -Path $cache

$archivePath = Join-Path $cache ([string] $contract.archive.fileName)
$finalRoot = Join-Path $cache ([string] $contract.distributionRoot)
$expectedLength = [long] $contract.archive.length
$expectedSha256 = ([string] $contract.archive.sha256).ToLowerInvariant()
Assert-CortexNsisOrdinaryPath -Path $archivePath

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

$admitted = Open-CortexNsisPinnedArchive -ArchivePath $archivePath -Contract $contract
try {
    Assert-CortexNsisOrdinaryPath -Path $finalRoot
    if (Test-Path -LiteralPath $finalRoot) {
        # Every path and byte must match the inventory derived from this exact
        # still-open archive. A corrupt extracted cache is not silently repaired.
        Assert-CortexNsisCacheExact -Archive $admitted -Directory $finalRoot
    } else {
        [void](Expand-CortexNsisPinnedArchive -Archive $admitted -Destination $finalRoot)
    }
    $selectedRoot = $finalRoot
    if (-not [string]::IsNullOrWhiteSpace($DestinationRoot)) {
        $selectedRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\', '/')
        if ($selectedRoot -eq $cache -or
            $selectedRoot.StartsWith($cache + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $cache.StartsWith($selectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The product NSIS materialization must be separate from its mutable dependency cache.'
        }
        [void](Expand-CortexNsisPinnedArchive -Archive $admitted -Destination $selectedRoot)
    }
    # Exact archive and independent compiler pins identify this version without
    # executing the mutable cache. This is provenance, not an executed probe.
    [pscustomobject]@{
        Contract = [string]$contract.contract
        Name = [string]$contract.name
        Version = [string]$contract.productVersion
        Archive = $archivePath
        ArchiveSha256 = $admitted.ArchiveSha256
        CacheRoot = $finalRoot
        Root = $selectedRoot
        Compiler = Join-Path $selectedRoot $admitted.CompilerRelativePath.Replace('/', '\')
        CompilerSha256 = [string]$contract.compiler.sha256
        CompilerVersion = [string]$contract.compiler.expectedVersion
        VersionSource = 'pinned_archive_contract'
        VersionProbeExecuted = $false
        License = Join-Path $selectedRoot ([string]$contract.license.relativePath)
    }
} finally {
    Close-CortexNsisPinnedArchive -Archive $admitted
}
