[CmdletBinding()]
param(
    [string]$HelperPath = (Join-Path $PSScriptRoot '..\..\tools\CortexOwnerAdminPackageReuse.ps1'),
    [string]$ReportRoot = (Join-Path $PSScriptRoot '..\..\fixture-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. ([IO.Path]::GetFullPath($HelperPath))

# All executables/manifests below are synthetic text bytes. This qualifies the
# copy/confinement contract, not package signatures, PE validity, or deployment.
$script:Results = [Collections.Generic.List[object]]::new()
$script:CaseNumber = 0
$reportBase = [IO.Path]::GetFullPath($ReportRoot)
[void][IO.Directory]::CreateDirectory($reportBase)
$script:RunRoot = Join-Path $reportBase ('r-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
if (Test-Path -LiteralPath $script:RunRoot) { throw 'Fixture run root is create-only.' }
[void][IO.Directory]::CreateDirectory($script:RunRoot)

function Assert-Fixture {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-FixtureRejects {
    param([scriptblock]$Action, [string]$Fragment = '')
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        $rejected = $true
        if ($Fragment -and -not $_.Exception.Message.Contains($Fragment, [StringComparison]::OrdinalIgnoreCase)) { throw "Expected '$Fragment', received: $($_.Exception.Message)" }
    }
    Assert-Fixture $rejected 'Expected fixture operation to reject.'
}

function Invoke-Fixture {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $script:Results.Add([pscustomobject]@{ Name = $Name; Passed = $true })
    } catch {
        $message = $_.Exception.Message
        if ($message.Length -gt 500) { $message = $message.Substring(0, 500) }
        $script:Results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Error = $message })
    }
}

function Write-FixtureFile {
    param([string]$Root, [string]$Relative, [byte[]]$Bytes)
    $path = [IO.Path]::Combine($Root, $Relative.Replace('/', '\'))
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllBytes($path, $Bytes)
}

function New-ReuseFixture {
    param([switch]$NoAffected)
    $script:CaseNumber++
    $directory = Join-Path $script:RunRoot ('c{0:d3}' -f $script:CaseNumber)
    $baseline = Join-Path $directory 'b'; $product = Join-Path $directory 'p'
    [void][IO.Directory]::CreateDirectory($baseline); [void][IO.Directory]::CreateDirectory($product)
    $paths = @(
        @(Get-CortexReuseRequiredPaths)
        'bin/host/Pegasus.AgentOps.ProcessHost.pdb'
        'bin/service/Pegasus.AgentOps.ProcessHost.pdb'
        'installer/resources/setup.txt'
        'installer/legal/license.txt'
        'bin/ui/fr/messages.json'
        'bin/ui/runtime-fixture.dll'
        'bin/host/PegasusCortex.Host.dll'
        'bin/semantics/PegasusCortex.exe'
    )
    $files = foreach ($path in $paths) {
        $bytes = [Text.Encoding]::UTF8.GetBytes('UNSIGNED SYNTHETIC FIXTURE: ' + $path)
        Write-FixtureFile $baseline $path $bytes
        [pscustomobject]@{ path = $path; bytes = [long]$bytes.Length; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
    }
    $manifest = [pscustomobject]@{ schema = 'pegasus.cortex.local-operational-package/1.2'; role = 'product'; files = @($files) }
    $inventory = @(Get-CortexOwnerAdminReuseInventory -Manifest $manifest)
    if (-not $NoAffected) { Write-FixtureFile $product 'bin/host/PegasusCortex.Host.dll' ([Text.Encoding]::UTF8.GetBytes('fresh affected role fixture')) }
    [pscustomobject]@{ Directory = $directory; Baseline = $baseline; Product = $product; Manifest = $manifest; Inventory = $inventory }
}

Invoke-Fixture 'fixed-four-role-inventory-and-create-only-copy' {
    $fixture = New-ReuseFixture
    Assert-Fixture (@($fixture.Inventory.Role | Sort-Object -Unique).Count -eq 4) 'Expected four fixed roles.'
    Assert-Fixture (-not ($fixture.Inventory.Path -contains 'bin/host/PegasusCortex.Host.dll')) 'Affected host leaked into reuse.'
    $copied = Copy-CortexOwnerAdminReusedRoles -BaselineRoot $fixture.Baseline -ProductRoot $fixture.Product -Inventory $fixture.Inventory
    $verified = Assert-CortexOwnerAdminReusedRoles -ProductRoot $fixture.Product -Inventory $fixture.Inventory
    Assert-Fixture ($copied.ReusedFiles -eq $fixture.Inventory.Count -and $verified.VerifiedFiles -eq $fixture.Inventory.Count) 'Incomplete exact copy.'
    foreach ($row in $fixture.Inventory) {
        $source = [IO.File]::ReadAllBytes((Join-Path $fixture.Baseline $row.Path))
        $target = [IO.File]::ReadAllBytes((Join-Path $fixture.Product $row.Path))
        Assert-Fixture ([Convert]::ToBase64String($source) -ceq [Convert]::ToBase64String($target)) 'Bytes differ after copy.'
    }
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $fixture.Product 'bin/host/PegasusCortex.Host.dll')) -ceq 'fresh affected role fixture') 'Affected file was changed.'
}

foreach ($relative in @('PegasusCortex.exe', 'installer/resources/setup.txt', 'bin/service/Pegasus.AgentOps.ProcessHost.dll', 'bin/ui/fr/messages.json')) {
    Invoke-Fixture ('post-sign-byte-mutation-detected:' + $relative) {
        $fixture = New-ReuseFixture
        Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory | Out-Null
        Write-FixtureFile $fixture.Product $relative ([Text.Encoding]::UTF8.GetBytes('synthetic post-sign byte change'))
        Assert-FixtureRejects { Assert-CortexOwnerAdminReusedRoles $fixture.Product $fixture.Inventory } 'target bytes changed'
    }
}

foreach ($relative in @('PegasusCortex.exe.extra', 'installer/deep/extra.bin', 'bin/ui/fr/extra.bin', 'bin/host/Pegasus.AgentOps.ProcessHost.xml', 'bin/service/Pegasus.AgentOps.ProcessHost.exe.extra')) {
    Invoke-Fixture ('extra-closure-file-rejected:' + $relative) {
        $fixture = New-ReuseFixture
        Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory | Out-Null
        Write-FixtureFile $fixture.Product $relative ([byte[]]@(1, 2, 3))
        Assert-FixtureRejects { Assert-CortexOwnerAdminReusedRoles $fixture.Product $fixture.Inventory }
    }
}

Invoke-Fixture 'existing-destination-rejected-before-any-copy' {
    $fixture = New-ReuseFixture -NoAffected
    Write-FixtureFile $fixture.Product 'PegasusCortex.exe' ([Text.Encoding]::UTF8.GetBytes('occupied'))
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory } 'collision'
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $fixture.Product 'PegasusCortex.exe')) -ceq 'occupied') 'Collision bytes were overwritten.'
    Assert-Fixture (@(Get-ChildItem -LiteralPath $fixture.Product -File -Recurse -Force).Count -eq 1) 'Copy happened before collision preflight.'
}

Invoke-Fixture 'missing-post-sign-closure-leaf-rejected' {
    $fixture = New-ReuseFixture -NoAffected
    foreach ($row in $fixture.Inventory) {
        if ($row.Path -ceq 'bin/service/Pegasus.AgentOps.ProcessHost.exe') { continue }
        Write-FixtureFile $fixture.Product $row.Path ([IO.File]::ReadAllBytes((Join-Path $fixture.Baseline $row.Path)))
    }
    Assert-FixtureRejects { Assert-CortexOwnerAdminReusedRoles $fixture.Product $fixture.Inventory } 'missing closure'
}

Invoke-Fixture 'changed-source-hash-rejected-and-partials-preserved' {
    $fixture = New-ReuseFixture
    Write-FixtureFile $fixture.Baseline 'PegasusCortex.exe' ([Text.Encoding]::UTF8.GetBytes('changed source after synthetic manifest'))
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory } 'source bytes changed'
    Assert-Fixture (-not [IO.File]::Exists((Join-Path $fixture.Product 'PegasusCortex.exe'))) 'Changed source was published.'
    Assert-Fixture (@(Get-CortexReuseClosureFiles $fixture.Product).Count -gt 0) 'A failure removed earlier task-owned partials.'
}

Invoke-Fixture 'source-read-lease-rejects-concurrent-writer' {
    $fixture = New-ReuseFixture
    $path = Join-Path $fixture.Baseline 'PegasusCortex.exe'
    $lease = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-FixtureRejects { $writer = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $writer.Dispose() }
    } finally { $lease.Dispose() }
    Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory | Out-Null
}

Invoke-Fixture 'case-insensitive-duplicate-manifest-path-rejected' {
    $fixture = New-ReuseFixture
    $row = $fixture.Manifest.files[0]
    $fixture.Manifest.files += [pscustomobject]@{ path = $row.path.ToUpperInvariant(); bytes = $row.bytes; sha256 = $row.sha256 }
    Assert-FixtureRejects { Get-CortexOwnerAdminReuseInventory $fixture.Manifest } 'duplicate'
}

Invoke-Fixture 'missing-process-host-service-mandatory-leaf-rejected' {
    $fixture = New-ReuseFixture
    $fixture.Manifest.files = @($fixture.Manifest.files | Where-Object path -ne 'bin/service/Pegasus.AgentOps.ProcessHost.dll')
    Assert-FixtureRejects { Get-CortexOwnerAdminReuseInventory $fixture.Manifest } 'mandatory'
}

Invoke-Fixture 'inventory-cannot-drop-optional-signed-closure-file' {
    $fixture = New-ReuseFixture
    $truncated = @($fixture.Inventory | Where-Object Path -ne 'bin/host/Pegasus.AgentOps.ProcessHost.pdb')
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $truncated } 'extra'
}

Invoke-Fixture 'caller-cannot-select-another-role' {
    $fixture = New-ReuseFixture
    $fixture.Inventory[0].Role = 'host'
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory } 'role does not match'
}

foreach ($badPath in @('../escape.bin', 'installer/../escape.bin', 'C:/escape.bin', 'installer\escape.bin', 'installer/file:stream', 'installer//double.bin', 'installer/CON.txt', 'installer/trailing.')) {
    Invoke-Fixture ('unsafe-manifest-path-rejected:' + $badPath) {
        $fixture = New-ReuseFixture
        $fixture.Manifest.files += [pscustomobject]@{ path = $badPath; bytes = [long]1; sha256 = ('a' * 64) }
        Assert-FixtureRejects { Get-CortexOwnerAdminReuseInventory $fixture.Manifest } 'unsafe'
    }
}

Invoke-Fixture 'fractional-byte-metadata-rejected' {
    $fixture = New-ReuseFixture
    $fixture.Manifest.files[0].bytes = [double]1.5
    Assert-FixtureRejects { Get-CortexOwnerAdminReuseInventory $fixture.Manifest } 'metadata'
}

Invoke-Fixture 'metadata-count-bound-enforced' {
    $fixture = New-ReuseFixture
    $record = $fixture.Manifest.files[0]
    $fixture.Manifest.files = [object[]]::new(20001)
    for ($index = 0; $index -lt 20001; $index++) { $fixture.Manifest.files[$index] = $record }
    Assert-FixtureRejects { Get-CortexOwnerAdminReuseInventory $fixture.Manifest } 'entry count'
}

Invoke-Fixture 'equal-and-nested-roots-rejected' {
    $fixture = New-ReuseFixture
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Baseline $fixture.Inventory } 'overlap'
    $nested = Join-Path $fixture.Baseline 'candidate'
    [void][IO.Directory]::CreateDirectory($nested)
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $nested $fixture.Inventory } 'overlap'
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $nested $fixture.Baseline $fixture.Inventory } 'overlap'
}

Invoke-Fixture 'source-reparse-subtree-rejected' {
    $fixture = New-ReuseFixture
    $outside = Join-Path $fixture.Directory 'x'; [void][IO.Directory]::CreateDirectory($outside)
    New-Item -ItemType Junction -Path (Join-Path $fixture.Baseline 'installer/redirect') -Target $outside -ErrorAction Stop | Out-Null
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory } 'reparse'
}

Invoke-Fixture 'destination-reparse-ancestor-rejected-without-escape' {
    $fixture = New-ReuseFixture -NoAffected
    $outside = Join-Path $fixture.Directory 'x'; [void][IO.Directory]::CreateDirectory($outside)
    New-Item -ItemType Junction -Path (Join-Path $fixture.Product 'bin') -Target $outside -ErrorAction Stop | Out-Null
    Assert-FixtureRejects { Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory } 'reparse'
    Assert-Fixture (@(Get-ChildItem -LiteralPath $outside -Force).Count -eq 0) 'Reparse escape received copied files.'
}

Invoke-Fixture 'post-sign-reparse-subtree-rejected' {
    $fixture = New-ReuseFixture
    Copy-CortexOwnerAdminReusedRoles $fixture.Baseline $fixture.Product $fixture.Inventory | Out-Null
    $outside = Join-Path $fixture.Directory 'x'; [void][IO.Directory]::CreateDirectory($outside)
    New-Item -ItemType Junction -Path (Join-Path $fixture.Product 'bin/ui/redirect') -Target $outside -ErrorAction Stop | Out-Null
    Assert-FixtureRejects { Assert-CortexOwnerAdminReusedRoles $fixture.Product $fixture.Inventory } 'reparse'
}

$passed = @($script:Results | Where-Object Passed).Count
$failed = $script:Results.Count - $passed
$report = [ordered]@{
    Schema = 'pegasus.cortex.owner-admin-package-reuse-fixtures/1'
    EvidenceTier = 'Deterministic synthetic manifest and local filesystem fixtures; no signature, executable, package, or deployment qualification.'
    Helper = [IO.Path]::GetFullPath($HelperPath)
    HelperSha256 = (Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Passed = $passed; Failed = $failed; Skipped = 0
    Results = @($script:Results)
    PreservedFixtureRoot = $script:RunRoot
}
$reportPath = Join-Path $script:RunRoot 'report.json'
$bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 8) + "`n")
$stream = [IO.FileStream]::new($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
[pscustomobject]@{ Report = $reportPath; ReportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant(); Passed = $passed; Failed = $failed; Skipped = 0 }
if ($failed -ne 0) { throw "$failed synthetic package-reuse fixture(s) failed; see $reportPath" }
