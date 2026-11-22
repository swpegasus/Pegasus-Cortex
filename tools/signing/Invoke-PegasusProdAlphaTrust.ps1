[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Uninstall')][string]$Action,
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$ComponentId,
    [string]$InstallRoot = '',
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PegasusSigning.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PegasusTrust.psm1') -Force

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$trust = Read-PegasusTrustBundle -PackageRoot $PackageRoot
$rootThumbprint = ConvertTo-PegasusThumbprint $trust.Root.Thumbprint
$publisherThumbprint = ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint
# Machine-wide certificate ownership must be shared across products, even when
# their installation roots differ. This is installer registry metadata, not a
# second product database. The former Cortex file is a read-only upgrade input.
$ownershipSubkey = 'Software\Pegasus\Trust\ProdAlpha\' + $rootThumbprint
$statePath = 'HKLM:\' + $ownershipSubkey
$mutex = $null
if (-not $PlanOnly) {
    if (-not $elevated) { throw 'Prod Alpha machine trust installation or removal requires explicit elevation.' }
    $mutex = [Threading.Mutex]::new($false, 'Global\Pegasus.ProdAlpha.Trust')
    if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); throw 'Another Pegasus trust operation is active.' }
}
try {
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
try {
    $key = $machine.OpenSubKey($ownershipSubkey)
    try { $rawState = if ($null -ne $key) { $key.GetValue('Ownership', $null) } else { $null } }
    finally { if ($null -ne $key) { $key.Dispose() } }
} finally { $machine.Dispose() }
$state = $null
if ($null -ne $rawState) {
    if ($rawState -isnot [string] -or $rawState.Length -gt 16384) { throw 'The Pegasus trust registry state is invalid.' }
    try { $state = $rawState | ConvertFrom-Json -Depth 10 -ErrorAction Stop }
    catch { throw 'The Pegasus trust registry state is malformed.' }
} else {
    $legacyRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Pegasus\Cortex'
    $legacyState = Join-Path $legacyRoot 'state\install\trust.v1.json'
    if (Test-Path -LiteralPath $legacyState -PathType Leaf) {
        $current = $legacyState
        while ($current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Legacy trust state crosses a reparse point.' }
            $current = Split-Path -Parent $current
        }
        if ((Get-Item -LiteralPath $legacyState).Length -gt 16384) { throw 'Legacy trust state exceeds its bound.' }
        $state = Get-Content -LiteralPath $legacyState -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 10 -ErrorAction Stop
    }
}

function Get-ExactCertificate {
    param(
        [Parameter(Mandatory)][string]$StoreName,
        [Parameter(Mandatory)][string]$Thumbprint
    )
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return @($store.Certificates | Where-Object {
                (ConvertTo-PegasusThumbprint $_.Thumbprint) -ceq $Thumbprint
            })
    }
    finally { $store.Dispose() }
}

$roots = @(Get-ExactCertificate -StoreName Root -Thumbprint $rootThumbprint)
$publishers = @(Get-ExactCertificate -StoreName TrustedPublisher -Thumbprint $publisherThumbprint)
if ($roots.Count -gt 1 -or $publishers.Count -gt 1) { throw 'The pinned machine trust identity is duplicated.' }
$transition = Get-PegasusTrustTransition `
    -Action $Action `
    -ComponentId $ComponentId `
    -RootThumbprint $rootThumbprint `
    -PublisherThumbprint $publisherThumbprint `
    -State $state `
    -RootPresent ($roots.Count -eq 1) `
    -PublisherPresent ($publishers.Count -eq 1)

$result = [pscustomobject]@{
    PlanOnly = [bool]$PlanOnly
    Action = $Action
    RequiresElevation = $true
    CurrentProcessElevated = $elevated
    Channel = 'Prod Alpha'
    ComponentId = $ComponentId
    RootThumbprint = $rootThumbprint
    PublisherThumbprint = $publisherThumbprint
    AddRoot = $transition.AddRoot
    AddPublisher = $transition.AddPublisher
    RemoveRoot = $transition.RemoveRoot
    RemovePublisher = $transition.RemovePublisher
    StatePath = $statePath
}
if ($PlanOnly) { return $result }
if (-not $elevated) { throw 'Prod Alpha machine trust installation or removal requires explicit elevation.' }
if (-not $PSCmdlet.ShouldProcess('LocalMachine Root and TrustedPublisher', "$Action pinned Pegasus Prod Alpha trust for $ComponentId")) {
    return
}

$addedRoot = $false
$addedPublisher = $false
try {
    if ($transition.AddRoot) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Add($trust.Root); $addedRoot = $true } finally { $store.Dispose() }
    }
    if ($transition.AddPublisher) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Add($trust.Publisher); $addedPublisher = $true } finally { $store.Dispose() }
    }
    if ($transition.RemovePublisher) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Remove($publishers[0]) } finally { $store.Dispose() }
    }
    if ($transition.RemoveRoot) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Remove($roots[0]) } finally { $store.Dispose() }
    }
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        # Keep an empty ownership tombstone after removal so a preserved legacy
        # file cannot resurrect an obsolete dependency on a subsequent install.
        $next = if ($null -ne $transition.NextState) { $transition.NextState } else {
            [ordered]@{
                schema='pegasus.machine-trust-ownership/1.0'; channel='Prod Alpha'
                rootThumbprint=$rootThumbprint; publisherThumbprint=$publisherThumbprint
                rootInstalledByPegasus=$false; publisherInstalledByPegasus=$false; components=@()
            }
        }
        $key = $machine.CreateSubKey($ownershipSubkey)
        try { $key.SetValue('Ownership', ($next | ConvertTo-Json -Depth 8 -Compress), [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
    } finally { $machine.Dispose() }
}
catch {
    if ($addedPublisher) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Remove($trust.Publisher) } finally { $store.Dispose() }
    }
    if ($addedRoot) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        try { $store.Open('ReadWrite'); $store.Remove($trust.Root) } finally { $store.Dispose() }
    }
    throw
}

$result
}
finally { if ($null -ne $mutex) { $mutex.ReleaseMutex(); $mutex.Dispose() } }
