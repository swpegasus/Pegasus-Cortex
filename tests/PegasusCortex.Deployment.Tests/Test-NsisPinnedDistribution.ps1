[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $root 'tools\dependencies\CortexNsisPinnedArchive.psm1') -Force
$scratch = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\', '/')
if (-not [IO.Path]::IsPathFullyQualified($ScratchRoot) -or $scratch.Length -gt 150 -or
    $scratch -eq [IO.Path]::GetPathRoot($scratch).TrimEnd('\', '/') -or (Test-Path -LiteralPath $scratch)) {
    throw 'Supply one absent, short, absolute task-owned fixture directory.'
}
Assert-CortexNsisOrdinaryPath $scratch
$checks = 0
function Require([bool]$Value, [string]$Message) { $script:checks++; if (-not $Value) { throw $Message } }
function Must-Fail([scriptblock]$Action) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception }
    Require ($null -ne $failure) 'An NSIS admission failure unexpectedly succeeded.'
}
function Must-RejectArchive([string]$Path, $Pin) {
    Must-Fail {
        $unexpected = $null
        try { $unexpected = Open-CortexNsisPinnedArchive -ArchivePath $Path -Contract $Pin }
        finally { if ($null -ne $unexpected) { Close-CortexNsisPinnedArchive -Archive $unexpected } }
    }
}
function Digest([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$compilerBytes = [Text.Encoding]::UTF8.GetBytes('MZ synthetic compiler; never executable')
$pluginBytes = [Text.Encoding]::UTF8.GetBytes('MZ synthetic official plugin; never executable')
$normal = [ordered]@{
    'nsis-3.12/' = $null
    'nsis-3.12/Bin/makensis.exe' = $compilerBytes
    'nsis-3.12/Plugins/x86-unicode/nsExec.dll' = $pluginBytes
    'nsis-3.12/Include/MUI2.nsh' = [Text.Encoding]::UTF8.GetBytes('synthetic include')
    'nsis-3.12/COPYING' = [Text.Encoding]::UTF8.GetBytes('synthetic license')
    'nsis-3.12/Empty/' = $null
}
function New-FixtureArchive([string]$Path, [Collections.IDictionary]$Entries, [string]$ReparseEntry = '') {
    $stream = [IO.FileStream]::new($Path, 'CreateNew', 'ReadWrite', 'None')
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $Entries.Keys) {
                $entry = $zip.CreateEntry([string]$name)
                if ($name -ceq $ReparseEntry) { $entry.ExternalAttributes = 0x400 }
                if ($null -ne $Entries[$name]) {
                    $output = $entry.Open()
                    try { $bytes = [byte[]]$Entries[$name]; $output.Write($bytes, 0, $bytes.Length) }
                    finally { $output.Dispose() }
                }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    return [pscustomobject]@{
        archive = [pscustomobject]@{ length=(Get-Item -LiteralPath $Path).Length; sha256=(Digest $Path) }
        distributionRoot='nsis-3.12'
        compiler=[pscustomobject]@{ relativePath='Bin/makensis.exe'; length=$compilerBytes.Length;
            sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($compilerBytes)).ToLowerInvariant() }
    }
}
function Remove-FixtureTree([string]$Path) {
    # The caller names the whole disposable boundary; do not recurse through links.
    if ([IO.Path]::GetFullPath($Path).TrimEnd('\', '/') -cne $scratch) { throw 'Fixture cleanup escaped its exact boundary.' }
    $directories = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count -ne 0) {
        $directory = $pending.Pop()
        Assert-CortexNsisOrdinaryPath $directory
        $directories.Add($directory)
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unknown reparse entry retained during fixture cleanup.' }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
            else { [IO.File]::Delete($entry) }
        }
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { [IO.Directory]::Delete($directories[$index], $false) }
}

$archive = $null
$junction = $null
$passed = $false
New-Item -ItemType Directory -Path $scratch -ErrorAction Stop | Out-Null
try {
    $zipPath = Join-Path $scratch 'trusted.zip'
    $contract = New-FixtureArchive $zipPath $normal
    $archive = Open-CortexNsisPinnedArchive -ArchivePath $zipPath -Contract $contract
    $cache = Join-Path $scratch 'cache'
    [void](Expand-CortexNsisPinnedArchive -Archive $archive -Destination $cache)
    Assert-CortexNsisCacheExact -Archive $archive -Directory $cache
    Require ($archive.Files.Count -eq 4 -and $archive.Directories.ContainsKey('Empty')) 'Full derived file/directory inventory was lost.'
    $cachedCompiler = Join-Path $cache 'Bin\makensis.exe'
    $cachedPlugin = Join-Path $cache 'Plugins\x86-unicode\nsExec.dll'
    $compilerHash = Digest $cachedCompiler

    $modifiedPlugin = [byte[]]$pluginBytes.Clone()
    $modifiedPlugin[0] = $modifiedPlugin[0] -bxor 1
    [IO.File]::WriteAllBytes($cachedPlugin, $modifiedPlugin)
    Require ((Get-Item -LiteralPath $cachedPlugin).Length -eq $pluginBytes.Length) 'The modified-plugin fixture must exercise SHA admission with unchanged length.'
    Require ((Digest $cachedCompiler) -ceq $compilerHash) 'The modified-plugin fixture changed the pinned compiler.'
    $badTarget = Join-Path $scratch 'bad-cache-product'
    Must-Fail { Assert-CortexNsisCacheExact -Archive $archive -Directory $cache; Expand-CortexNsisPinnedArchive -Archive $archive -Destination $badTarget }
    Require (-not (Test-Path -LiteralPath $badTarget)) 'A modified plugin reached product materialization.'
    [IO.File]::WriteAllBytes($cachedPlugin, $pluginBytes)

    $extra = Join-Path $cache 'unexpected.exe'
    [IO.File]::WriteAllText($extra, 'MZ added executable')
    Must-Fail { Assert-CortexNsisCacheExact -Archive $archive -Directory $cache }
    [IO.File]::Delete($extra)
    $license = Join-Path $cache 'COPYING'
    $heldLicense = Join-Path $scratch 'COPYING.saved'
    [IO.File]::Move($license, $heldLicense, $false)
    Must-Fail { Assert-CortexNsisCacheExact -Archive $archive -Directory $cache }
    [IO.File]::Move($heldLicense, $license, $false)
    Assert-CortexNsisCacheExact -Archive $archive -Directory $cache

    # A cache mutation after a successful check cannot affect bytes selected
    # from the still-open archive. This is the old validation-to-copy gap.
    [IO.File]::WriteAllText($cachedPlugin, 'MZ racing altered plugin')
    [IO.File]::WriteAllText($extra, 'MZ racing added executable')
    $product = Join-Path $scratch 'product'
    [void](Expand-CortexNsisPinnedArchive -Archive $archive -Destination $product)
    Require ((Digest (Join-Path $product 'Plugins\x86-unicode\nsExec.dll')) -ceq $archive.Files['Plugins/x86-unicode/nsExec.dll'].Sha256) 'Mutable cache plugin bytes entered the product.'
    Require (-not (Test-Path -LiteralPath (Join-Path $product 'unexpected.exe'))) 'A cache-added executable entered the product.'
    Assert-CortexNsisCacheExact -Archive $archive -Directory $product
    Must-Fail { Assert-CortexNsisCacheExact -Archive $archive -Directory $cache }
    [IO.File]::WriteAllBytes($cachedPlugin, $pluginBytes)
    [IO.File]::Delete($extra)
    Must-Fail { Expand-CortexNsisPinnedArchive -Archive $archive -Destination $product }
    Require ((Digest (Join-Path $product 'Bin\makensis.exe')) -ceq $compilerHash) 'Create-only refusal altered existing output.'

    # Windows share admission must pin both content and the selected generation.
    Must-Fail { $writer=[IO.FileStream]::new($zipPath,'Open','ReadWrite','ReadWrite'); $writer.Dispose() }
    Must-Fail { [IO.File]::Move($zipPath, (Join-Path $scratch 'moved.zip'), $false) }
    Require ((Digest $zipPath) -ceq $contract.archive.sha256) 'Held archive content changed.'

    $longTarget = Join-Path $scratch ('x' * (238 - $scratch.Length - 1))
    Must-Fail { Expand-CortexNsisPinnedArchive -Archive $archive -Destination $longTarget }
    Require (-not (Test-Path -LiteralPath $longTarget)) 'Path-budget refusal wrote a target.'
    Must-Fail { Expand-CortexNsisPinnedArchive -Archive $archive -Destination 'relative-target' }

    # A junction must fail without traversing it or changing its target.
    $outside = Join-Path $scratch 'outside'
    New-Item -ItemType Directory -Path $outside -ErrorAction Stop | Out-Null
    $sentinel = Join-Path $outside 'retained.txt'
    [IO.File]::WriteAllText($sentinel, 'outside retained')
    $junction = Join-Path $cache 'linked'
    New-Item -ItemType Junction -Path $junction -Value $outside -ErrorAction Stop | Out-Null
    Must-Fail { Assert-CortexNsisCacheExact -Archive $archive -Directory $cache }
    Must-Fail { Expand-CortexNsisPinnedArchive -Archive $archive -Destination (Join-Path $junction 'escape') }
    Require ([IO.File]::ReadAllText($sentinel) -ceq 'outside retained') 'Reparse refusal touched its target.'
    [IO.Directory]::Delete($junction, $false)
    $junction = $null

    Close-CortexNsisPinnedArchive -Archive $archive
    $archive = $null
    $writer = [IO.FileStream]::new($zipPath, 'Open', 'ReadWrite', 'ReadWrite')
    try { Must-RejectArchive $zipPath $contract }
    finally { $writer.Dispose() }
    $exclusive = [IO.FileStream]::new($zipPath, 'Open', 'ReadWrite', 'None')
    $exclusive.Dispose()
    Require ($true) 'Archive ownership was not released.'

    $badArchive = Join-Path $scratch 'wrong.zip'
    [IO.File]::Copy($zipPath, $badArchive, $false)
    [IO.File]::AppendAllText($badArchive, 'not pinned')
    Must-RejectArchive $badArchive $contract
    $modifiedArchive = [IO.File]::ReadAllBytes($zipPath)
    $modifiedArchive[0] = $modifiedArchive[0] -bxor 1
    [IO.File]::WriteAllBytes($badArchive, $modifiedArchive)
    Require ((Get-Item -LiteralPath $badArchive).Length -eq $contract.archive.length) 'The archive SHA fixture changed its length.'
    Must-RejectArchive $badArchive $contract
    $wrongCompiler = $contract | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    $wrongCompiler.compiler.sha256 = '0' * 64
    Must-RejectArchive $zipPath $wrongCompiler

    foreach ($unsafe in @('nsis-3.12/../escape.exe', '/nsis-3.12/rooted.exe', 'nsis-3.12/CON.txt', 'nsis-3.12/name:stream', 'nsis-3.12/trailing.')) {
        $entries = [ordered]@{}
        foreach ($key in $normal.Keys) { $entries[$key] = $normal[$key] }
        $entries[$unsafe] = [byte[]]@(1)
        $path = Join-Path $scratch ('unsafe-' + $checks + '.zip')
        $pin = New-FixtureArchive $path $entries
        Must-RejectArchive $path $pin
    }
    $entries = [ordered]@{}
    foreach ($key in $normal.Keys) { $entries[$key] = $normal[$key] }
    $entries['nsis-3.12/bin/another.exe'] = [byte[]]@(1)
    $casePath = Join-Path $scratch 'case.zip'
    $casePin = New-FixtureArchive $casePath $entries
    Must-RejectArchive $casePath $casePin
    $reparsePath = Join-Path $scratch 'reparse.zip'
    $reparsePin = New-FixtureArchive $reparsePath $normal 'nsis-3.12/Plugins/x86-unicode/nsExec.dll'
    Must-RejectArchive $reparsePath $reparsePin

    # Deliberately invalidate only this fixture-owned ZIP object after admission
    # to force extraction failure after it creates an output file. No hook or
    # untrusted code is added to the production materialization path.
    $archive = Open-CortexNsisPinnedArchive -ArchivePath $zipPath -Contract $contract
    $archive.Zip.Dispose()
    $cleanupTarget = Join-Path $scratch 'cleanup-failure'
    Must-Fail { Expand-CortexNsisPinnedArchive -Archive $archive -Destination $cleanupTarget }
    Require (-not (Test-Path -LiteralPath $cleanupTarget)) 'Failed extraction left its owned files/directories behind.'
    Close-CortexNsisPinnedArchive -Archive $archive
    $archive = $null
    $passed = $true
} finally {
    if ($null -ne $archive) { Close-CortexNsisPinnedArchive -Archive $archive }
    if ($null -ne $junction -and [IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction, $false) }
    Remove-FixtureTree $scratch
}
if ($passed) {
    [pscustomobject]@{ Result='PASS'; Checks=$checks; Tier='isolated synthetic ZIP/filesystem fixture';
        ExecutableProbe=$false; PluginExecution=$false; Download=$false; Signing=$false; LiveCache=$false; CleanupComplete=$true } | ConvertTo-Json -Compress
}
